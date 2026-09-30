using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MV.ApplicationLayer.Helpers;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.DomainLayer.Configuration;
using MV.DomainLayer.DTO.ResponseModel;
using MV.DomainLayer.Exceptions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MV.ApplicationLayer.Services;

/// <summary>
/// Dùng Gemini File API + generateContent để phân tích video buổi học đã ghi — tóm tắt cho học
/// sinh, sinh nội dung báo cáo có cấu trúc cho gia sư, và trả lời câu hỏi hỏi tiếp dựa trên tóm tắt.
/// Gọi thẳng REST API (không qua SDK) — cùng phong cách với DisputeClassificationService (Groq).
/// </summary>
public class GeminiVideoAnalysisService : IGeminiVideoAnalysisService
{
    // File API >2GB bị Gemini từ chối thẳng — chặn trước khi tốn công tải/upload.
    private const long MaxFileSizeBytes = 2_000_000_000;
    private const int FileActivePollIntervalSeconds = 5;
    private const int FileActiveMaxWaitMinutes = 10;
    private const string GoogleApiKeyHeader = "x-goog-api-key";

    private readonly HttpClient _httpClient;
    private readonly GoogleGeminiSettings _settings;
    private readonly ILogger<GeminiVideoAnalysisService> _logger;
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiVideoAnalysisService(
        HttpClient httpClient,
        IOptions<GoogleGeminiSettings> settings,
        ILogger<GeminiVideoAnalysisService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        // Gửi API key qua header thay vì query string (?key=...) để key không lọt vào URL —
        // URL request bị HttpClient ghi ra log, proxy/access log, trace...
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey)
            && !_httpClient.DefaultRequestHeaders.Contains(GoogleApiKeyHeader))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(GoogleApiKeyHeader, _settings.ApiKey);
    }

    public async Task<GeminiUploadedFile> UploadVideoAsync(
        Stream videoStream, long contentLength, string mimeType, string displayName, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (contentLength > MaxFileSizeBytes)
            throw new GeminiVideoTooLargeException();

        // Bước 1: khởi tạo resumable upload session, lấy URL upload thật từ header X-Goog-Upload-URL.
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/upload/v1beta/files");
        startRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "resumable");
        startRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "start");
        startRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Length", contentLength.ToString());
        startRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Type", mimeType);
        var startBody = JsonSerializer.Serialize(new { file = new { display_name = displayName } });
        startRequest.Content = new StringContent(startBody, Encoding.UTF8, "application/json");

        using var startResponse = await _httpClient.SendAsync(startRequest, ct);
        if (!startResponse.IsSuccessStatusCode)
        {
            var body = await startResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Gemini upload-start lỗi: {StatusCode} - {Body}", startResponse.StatusCode, body);
            throw new GeminiApiException((int)startResponse.StatusCode, "Không thể bắt đầu upload video lên Gemini.");
        }

        if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
            throw new GeminiFileProcessingException("Gemini không trả về địa chỉ upload.");
        var uploadUrl = uploadUrls.First();

        // Bước 2: đẩy bytes thật — stream thẳng từ videoStream (Drive), không đọc hết vào RAM trước.
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
        uploadRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
        uploadRequest.Content = new StreamContent(videoStream);
        uploadRequest.Content.Headers.ContentLength = contentLength;
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        using var uploadResponse = await _httpClient.SendAsync(uploadRequest, ct);
        var uploadResponseBody = await uploadResponse.Content.ReadAsStringAsync(ct);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini upload video lỗi: {StatusCode} - {Body}", uploadResponse.StatusCode, uploadResponseBody);
            throw new GeminiApiException((int)uploadResponse.StatusCode, "Upload video lên Gemini thất bại.");
        }

        var parsed = JsonSerializer.Deserialize<GeminiFileEnvelope>(uploadResponseBody, CamelCaseOptions);
        var file = parsed?.File;
        if (file?.Name is null || file.Uri is null)
            throw new GeminiResponseParseException("Gemini trả về thông tin file không hợp lệ sau khi upload.");

        _logger.LogInformation("Đã upload video lên Gemini: name={Name} state={State}", file.Name, file.State);
        return new GeminiUploadedFile(file.Name, file.Uri);
    }

    public async Task WaitForFileActiveAsync(string fileName, CancellationToken ct = default)
    {
        EnsureConfigured();
        var deadline = DateTime.UtcNow.AddMinutes(FileActiveMaxWaitMinutes);

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1beta/{fileName}");
            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini kiểm tra trạng thái file lỗi: {StatusCode} - {Body}", response.StatusCode, body);
                throw new GeminiApiException((int)response.StatusCode, "Không kiểm tra được trạng thái xử lý video trên Gemini.");
            }

            var file = JsonSerializer.Deserialize<GeminiFile>(body, CamelCaseOptions);
            if (string.Equals(file?.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(file?.State, "FAILED", StringComparison.OrdinalIgnoreCase))
                throw new GeminiFileProcessingException("Gemini xử lý video thất bại.");

            if (DateTime.UtcNow >= deadline)
                throw new GeminiFileProcessingException("Gemini xử lý video quá lâu, vui lòng thử lại sau.");

            await Task.Delay(TimeSpan.FromSeconds(FileActivePollIntervalSeconds), ct);
        }
    }

    public async Task<string> SummarizeVideoForStudentAsync(string fileUri, string mimeType, CancellationToken ct = default)
    {
        const string prompt = """
            Bạn là trợ lý xử lý bản ghi âm buổi học 1-kèm-1 giữa gia sư và học sinh. Hãy nghe kỹ rồi viết bản
            tóm tắt bằng tiếng Việt, giọng văn gần gũi như đang giải thích lại cho học sinh chứ không phải
            liệt kê khô khan. Dùng markdown (tiêu đề phụ "##", in đậm "**...**" cho từ khoá quan trọng, gạch
            đầu dòng "-" cho danh sách). Công thức/ký hiệu toán học viết bằng LaTeX: đặt giữa 1 cặp dấu $ cho
            công thức ngắn nằm trong câu (vd $x^2 + 1$), giữa 1 cặp dấu $$ cho công thức dài/quan trọng cần
            tách dòng riêng. Không chào hỏi mở đầu, không lặp lại nguyên văn lời nói.

            Các mục có thể đưa vào: nội dung chính đã học/dạy (giải thích ngắn gọn ý nghĩa, không chỉ liệt kê
            tên chủ đề), các điểm quan trọng/công thức/kết luận đáng nhớ, và bài tập về nhà.

            QUAN TRỌNG: chỉ viết những mục thật sự có nội dung trong buổi học. Mục nào không có thì BỎ HẲN,
            không in tiêu đề của mục đó ra. Tuyệt đối không viết những câu như "Không có.", "Không đề cập.",
            "Buổi học này không giao bài tập." — thà thiếu mục còn hơn có mục rỗng.

            TUYỆT ĐỐI KHÔNG BỊA: không tự suy đoán hay thêm nội dung không thực sự có trong audio — đặc biệt
            là bài tập về nhà. Nếu audio không đề cập rõ ràng việc giao bài tập, coi như không có (bỏ hẳn mục
            này theo quy tắc ở trên), tuyệt đối không đoán/viết thêm bài tập không được nhắc tới trong buổi học.
            """;

        var schema = new GeminiSchema
        {
            Type = "OBJECT",
            Properties = new Dictionary<string, GeminiSchema> { ["summary"] = new() { Type = "STRING" } },
            Required = ["summary"]
        };

        var requestBody = BuildGenerateContentRequest(fileUri, mimeType, prompt, schema);
        var text = await SendGenerateContentAsync(requestBody, _settings.Model, ct, "StudentSummary", fileUri);

        var parsed = ParseOrThrow<SummaryJson>(text, "tóm tắt");
        if (string.IsNullOrWhiteSpace(parsed.Summary))
            throw new GeminiResponseParseException("Gemini trả về tóm tắt không hợp lệ.");
        return parsed.Summary.Trim();
    }

    /// <summary>Prompt chép lời buổi học ghi âm từ app — dùng chung cho gọi thường và Batch API.
    /// Có yêu cầu ẩn danh vì transcript được lưu làm nguyên liệu thô lâu dài.</summary>
    private const string LessonTranscriptPrompt = """
            Bạn là trợ lý chép lời bản ghi âm một buổi học 1-kèm-1 giữa gia sư và học sinh (ghi tại nhà,
            bằng điện thoại). Hãy nghe kỹ và chép lại bằng tiếng Việt toàn bộ hội thoại, đúng những gì
            từng người thực sự nói, đúng trình tự thời gian. Không tóm lược, không bỏ sót.

            ĐỊNH DẠNG — bắt buộc tuyệt đối:
            - MỖI lượt nói là MỘT dòng, dạng: [mm:ss] Người nói: nội dung
              ví dụ: [00:05] Gia sư: Hôm nay mình ôn phương trình bậc hai nhé.
            - [mm:ss] là thời điểm lượt nói BẮT ĐẦU trong bản ghi, tính từ 00:00. Quá 60 phút thì
              ghi tiếp số phút, ví dụ [75:12].
            - Người nói chỉ được là một trong: "Gia sư", "Học sinh", "Không rõ".
              Gia sư là người giảng, đặt câu hỏi, giao bài; học sinh là người trả lời, hỏi lại.
            - Không dùng markdown, không in đậm, không dòng trống, không tiêu đề.
            - Đoạn im lặng dài hoặc tiếng ồn thì bỏ qua, không cần ghi.

            - ẨN DANH: không chép họ tên riêng của bất kỳ ai (học sinh, gia sư, người thân, bạn bè),
              số điện thoại, địa chỉ nhà, tên trường. Thay lần lượt bằng [TÊN], [SĐT], [ĐỊA CHỈ], [TRƯỜNG].
              Tên nhân vật trong đề bài/sách (vd "bạn Lan mua 3 quả táo") thì giữ nguyên.

            Trả lời trực tiếp bằng văn bản theo đúng định dạng trên, KHÔNG bọc trong JSON.
            """;

    public async Task<string> TranscribeLessonAudioAsync(string fileUri, string mimeType, CancellationToken ct = default)
    {
        const string prompt = LessonTranscriptPrompt;

        // Văn bản thường, không JSON: bị cắt vì trần token thì phần đã chép vẫn dùng được.
        var requestBody = BuildGenerateContentRequest(fileUri, mimeType, prompt, jsonSchema: null, _settings.TranscriptMaxOutputTokens);
        var text = await SendGenerateContentAsync(requestBody, _settings.TranscriptModel, ct, "LessonTranscript", fileUri);
        if (string.IsNullOrWhiteSpace(text))
            throw new GeminiResponseParseException("Gemini trả về lời thoại rỗng.");
        return text.Trim();
    }

    public async Task<string> TranscribeVideoAsync(string fileUri, string mimeType, CancellationToken ct = default)
    {
        const string prompt = """
            Bạn là trợ lý chép lời bản ghi âm buổi học 1-kèm-1 giữa gia sư và học sinh. Hãy nghe kỹ và chép
            lại bằng tiếng Việt toàn bộ hội thoại, theo sát những gì từng người thực sự nói, đúng trình tự
            thời gian. Không tóm lược, không bỏ sót đoạn nào.

            QUY TẮC ĐỊNH DẠNG — bắt buộc tuân thủ tuyệt đối, không có ngoại lệ:
            - MỌI đoạn văn đều phải mở đầu bằng nhãn người nói: "**Gia sư:** " hoặc "**Học sinh:** ".
              Không được để bất kỳ đoạn nào thiếu nhãn.
            - Khi CÙNG một người nói liên tiếp nhiều đoạn, từng đoạn vẫn phải lặp lại nhãn của người đó.
              Không được chỉ ghi nhãn ở đoạn đầu rồi bỏ trống các đoạn sau.
            - Mỗi đoạn cách nhau bằng 1 dòng trống. Không gộp lời của 2 người vào chung 1 đoạn.
            - Không xác định được ai đang nói thì ghi "**Không rõ:** ".
            - Nếu cả buổi chỉ có 1 người nói (ví dụ gia sư thử mic, học sinh chưa vào), vẫn phải gắn nhãn
              cho từng đoạn đúng như trên.

            Trả lời trực tiếp bằng văn bản chép lời, KHÔNG bọc trong JSON.
            """;

        // Cố tình KHÔNG dùng structured JSON output (jsonSchema: null) cho lượt chép lời — khác với
        // tóm tắt/điền báo cáo. Lý do: buổi học dài có thể khiến Gemini bị cắt giữa chừng do chạm trần
        // maxOutputTokens (65536 — trần CỨNG của cả dòng Gemini hiện tại, không thể tăng qua config).
        // Với JSON, bị cắt giữa chừng = chuỗi "transcript" chưa đóng ngoặc kép/ngoặc nhọn → parse lỗi →
        // MẤT TOÀN BỘ transcript dù phần lớn đã chép đúng. Với văn bản thường, bị cắt giữa chừng chỉ đơn
        // giản là dừng lại giữa câu — vẫn là text hợp lệ, dùng được ngay phần đã chép, không cần parse gì
        // cả. Đánh đổi: mất khả năng ép cấu trúc chặt ở tầng schema, nhưng QUY TẮC ĐỊNH DẠNG ở trên đã đủ
        // chặt để Gemini tuân theo mà không cần schema ép buộc.
        var requestBody = BuildGenerateContentRequest(fileUri, mimeType, prompt, jsonSchema: null, _settings.TranscriptMaxOutputTokens);
        var text = await SendGenerateContentAsync(requestBody, _settings.TranscriptModel, ct, "Transcript", fileUri);

        if (string.IsNullOrWhiteSpace(text))
            throw new GeminiResponseParseException("Gemini trả về hội thoại không hợp lệ.");
        return text.Trim();
    }

    /// <summary>Nếu Gemini bị cắt giữa chừng do vượt maxOutputTokens (buổi học quá dài), JSON trả về sẽ dở
    /// dang — ném lỗi có nghĩa cho người dùng thay vì để JsonException thô lộ ra ngoài.</summary>
    private T ParseOrThrow<T>(string text, string label) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, CamelCaseOptions)
                ?? throw new GeminiResponseParseException($"Gemini trả về {label} không hợp lệ.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Gemini trả về JSON {Label} dở dang (khả năng bị cắt do vượt maxOutputTokens).", label);
            throw new GeminiResponseParseException(
                $"Buổi học quá dài, Gemini không viết kịp hết {label} trong giới hạn cho phép. Vui lòng thử lại.");
        }
    }

    public async Task<TutorReportAiFillResult> GenerateTutorReportFieldsAsync(
        string fileUri, string mimeType, TutorReportContext? context = null, CancellationToken ct = default)
    {
        // Prompt v3 (2026-09-29): biên bản chỉ ghi dạng bài và kiến thức, không chép đề bài/con số — các lần
        // thử cho thấy model hay nghe nhầm hoặc bịa đề, và audio không chứa đề chỉ hiện trên màn hình.
        const string instructions = """
            Bạn là trợ lý của gia sư trên nền tảng TopTutor. Nghe kỹ toàn bộ bản ghi âm buổi học 1 kèm 1 và trả về JSON theo schema, viết bằng tiếng Việt.

            QUY TẮC CHUNG (áp dụng cho mọi phần):
            - CHỈ ghi điều thật sự có trong audio. Không suy đoán, không bịa. Phần nào không có căn cứ thì để trống (mảng rỗng) hoặc ghi đúng câu mặc định được chỉ định.
            - KHÔNG chép đề bài, công thức hay con số của bài tập ở bất kỳ phần nào. Chỉ ghi DẠNG BÀI và NỘI DUNG KIẾN THỨC (ví dụ "bất phương trình bậc nhất có chuyển vế", "rút gọn biểu thức chứa căn"), viết bằng lời.
            - Viết cụ thể về kiến thức và kỹ năng: dạng bài nào, bước nào làm được, bước nào sai (ví dụ "quên đổi chiều khi chia hai vế cho số âm"). Tránh câu chung chung như "con học tốt", "nắm được bài", "hợp tác tốt", "buổi học diễn ra tích cực" nếu không kèm dẫn chứng cụ thể.
            - Không nhắc tới các buổi học trước — bạn chỉ có thông tin của buổi này.
            - Không khẳng định tiến bộ dài hạn ("con tiến bộ rõ rệt", "chắc chắn đạt điểm cao"). Chỉ nói điều thấy được trong buổi này.
            - Bỏ qua: chuyện phiếm không liên quan đến việc học, thủ tục, vấn đề kỹ thuật (âm thanh, mạng, thiết bị). Thay đổi lịch học thì ghi vào followUps. KHÔNG ghi thông tin cá nhân nhạy cảm (sức khoẻ, chuyện gia đình, tài chính, tôn giáo) dù có nghe thấy.
            - Không viết tắt ("Học sinh", "bài tập", "phương trình"; không "HS", "BT", "PT"). Gọi học sinh là "con" trong phần gửi phụ huynh, "học sinh" trong phần cho gia sư; gọi người dạy là "gia sư". Không dùng từ chỉ giới tính.

            PHẦN 1 — BÁO CÁO ĐẦY ĐỦ (gia sư xem và sửa trước khi gửi phụ huynh):
            - lessonContent: Hôm nay con học chủ đề gì và làm gì trong buổi (giảng lý thuyết, luyện những dạng bài nào, khoảng bao nhiêu bài). Số bài phải khớp với số mục trong sessionMinutes.exercises. 2–4 câu.
            - homework: Bài tập về nhà gia sư THẬT SỰ giao (dạng bài, số lượng). Nếu audio không nhắc tới, ghi đúng: "Không đề cập giao bài tập."
            - tutorNotes: Một điều con làm tốt và một điều con cần cải thiện, đều cụ thể và có trong buổi học; thêm việc gia sư sẽ làm tiếp nếu gia sư có nói. Nếu không có căn cứ, ghi đúng: "Không đề cập gì thêm."

            PHẦN 2 — sessionMinutes: BIÊN BẢN cho GIA SƯ đọc lại thay vì nghe lại cả buổi. Chi tiết về nội dung và cách học của học sinh, nhưng không thừa: mọi ý phải giúp gia sư dạy buổi sau tốt hơn.
            - summary: 3–5 câu: mục tiêu buổi học, diễn biến chính, kết quả.
            - sections: diễn biến theo thứ tự thời gian, 2–8 phần. Mỗi phần: title (ngắn, ví dụ "Ôn quy tắc chuyển vế") và details 1–6 ý: kiến thức hoặc dạng bài, gia sư giải thích thế nào, học sinh phản ứng ra sao (trả lời đúng/sai, hỏi lại điều gì).
            - exercises: liệt kê TẤT CẢ các bài/câu đã làm trong buổi theo thứ tự, tối đa 15. Mỗi bài: type (dạng bài, viết bằng lời, không chép đề), result (một trong: "Tự làm đúng", "Đúng sau khi được gợi ý", "Làm sai", "Chưa làm xong", "Gia sư làm mẫu"), note (học sinh vướng ở bước nào hoặc gia sư gợi ý gì; để trống nếu không có).
            - strengths: tối đa 5 điều học sinh đã nắm về kiến thức/kỹ năng, mỗi ý kèm dẫn chứng (ví dụ "tự quy đồng và khử mẫu đúng ở các bài có phân thức").
            - difficulties: tối đa 5 chỗ học sinh còn sai hoặc chưa hiểu, nêu đúng bước sai (ví dụ "quên đổi chiều bất phương trình khi chia hai vế cho số âm").
            - usefulNotes: tối đa 6 thông tin không hẳn là bài giảng nhưng có ích cho việc dạy: mẹo và kinh nghiệm học, làm bài, đi thi được nhắc tới; lịch kiểm tra, điểm số ở trường; cách học, thói quen, sở thích, động lực của học sinh ảnh hưởng tới việc học.
            - teachingNotes: 0–3 nhận xét về CÁCH DẠY để gia sư tự điều chỉnh (không chấm điểm). Mỗi mục: content (nhận xét) và example (tình huống cụ thể trong buổi, và một câu hỏi gia sư có thể dùng lần sau). Dựa trên: gia sư có đặt câu hỏi gợi mở không; có để học sinh tự giải thích cách làm không; có đưa đáp án hoặc cách giải quá sớm không; có khen đúng lúc khi học sinh làm đúng không. Viết giọng hỗ trợ. Không có căn cứ rõ thì trả mảng rỗng.
            - keyPoints: 3–6 kiến thức quan trọng nhất cần nhớ từ buổi này, mỗi ý một câu ngắn.
            - followUps: 0–6 việc cho buổi sau: ôn lại chỗ học sinh còn vướng, tài liệu gia sư đã hứa, lịch thay đổi; kiểm tra bài tập về nhà CHỈ KHI homework có giao bài. Chỉ ghi việc có căn cứ từ audio.

            PHẦN 3 — zaloSummary: TIN ZALO gửi PHỤ HUYNH. Phụ huynh chỉ đọc phần này, nên phải cho phụ huynh biết rõ hôm nay con học như thế nào.
            - content (ô "Nội dung"): hôm nay con học chủ đề gì, làm gì. Ví dụ: "Con học giải bất phương trình bậc nhất một ẩn và luyện 6 bài từ dễ đến khó."
            - homework (ô "Bài tập"): bài tập gia sư THẬT SỰ giao. Không có thì ghi đúng: "Không có bài tập về nhà."
            - notes (ô "Ghi chú"): một điều con làm tốt và một điều con cần cải thiện, cụ thể. Ví dụ: "Con tự làm đúng 4 trên 6 bài. Con còn quên đổi chiều khi chia cho số âm, buổi sau sẽ luyện thêm phần này." Không có căn cứ thì ghi đúng: "Không có nhận xét thêm."
            Quy tắc BẮT BUỘC cho 3 field này:
            - Mỗi field 1–2 câu hoàn chỉnh, khoảng 100–160 ký tự, TUYỆT ĐỐI KHÔNG quá 180 ký tự (tính cả dấu cách). Ít nội dung thì viết ngắn hơn.
            - Chỉ văn bản thuần: không markdown, không gạch đầu dòng, không ký hiệu toán, không emoji, không xuống dòng.
            """;
        var prompt = BuildReportContextLine(context) + instructions;

        var stringArray = new GeminiSchema { Type = "ARRAY", Items = new() { Type = "STRING" } };
        var schema = new GeminiSchema
        {
            Type = "OBJECT",
            Properties = new Dictionary<string, GeminiSchema>
            {
                ["lessonContent"] = new() { Type = "STRING" },
                ["homework"] = new() { Type = "STRING" },
                ["tutorNotes"] = new() { Type = "STRING" },
                ["sessionMinutes"] = new()
                {
                    Type = "OBJECT",
                    Properties = new Dictionary<string, GeminiSchema>
                    {
                        ["summary"] = new() { Type = "STRING" },
                        ["sections"] = new()
                        {
                            Type = "ARRAY",
                            Items = new()
                            {
                                Type = "OBJECT",
                                Properties = new Dictionary<string, GeminiSchema>
                                {
                                    ["title"] = new() { Type = "STRING" },
                                    ["details"] = stringArray
                                },
                                Required = ["title", "details"]
                            }
                        },
                        ["exercises"] = new()
                        {
                            Type = "ARRAY",
                            Items = new()
                            {
                                Type = "OBJECT",
                                Properties = new Dictionary<string, GeminiSchema>
                                {
                                    ["type"] = new() { Type = "STRING" },
                                    ["result"] = new() { Type = "STRING", Enum = TutorMinutesExercise.Results },
                                    ["note"] = new() { Type = "STRING" }
                                },
                                Required = ["type", "result"]
                            }
                        },
                        ["strengths"] = stringArray,
                        ["difficulties"] = stringArray,
                        ["usefulNotes"] = stringArray,
                        ["teachingNotes"] = new()
                        {
                            Type = "ARRAY",
                            Items = new()
                            {
                                Type = "OBJECT",
                                Properties = new Dictionary<string, GeminiSchema>
                                {
                                    ["content"] = new() { Type = "STRING" },
                                    ["example"] = new() { Type = "STRING" }
                                },
                                Required = ["content", "example"]
                            }
                        },
                        ["keyPoints"] = stringArray,
                        ["followUps"] = stringArray
                    },
                    Required = ["summary", "sections", "exercises", "strengths", "difficulties", "usefulNotes",
                        "teachingNotes", "keyPoints", "followUps"]
                },
                ["zaloSummary"] = new()
                {
                    Type = "OBJECT",
                    Properties = new Dictionary<string, GeminiSchema>
                    {
                        ["content"] = new() { Type = "STRING" },
                        ["homework"] = new() { Type = "STRING" },
                        ["notes"] = new() { Type = "STRING" }
                    },
                    Required = ["content", "homework", "notes"]
                }
            },
            Required = ["lessonContent", "homework", "tutorNotes", "sessionMinutes", "zaloSummary"]
        };

        var requestBody = BuildGenerateContentRequest(fileUri, mimeType, prompt, schema,
            _settings.ReportMaxOutputTokens, _settings.ReportThinkingLevel);
        var text = await SendGenerateContentAsync(requestBody, _settings.Model, ct, "TutorReportFill", fileUri);

        var parsed = JsonSerializer.Deserialize<TutorReportAiFillResult>(text, CamelCaseOptions);
        if (parsed is null)
            throw new GeminiResponseParseException("Gemini trả về nội dung báo cáo không hợp lệ.");

        // Buổi học không giao bài thì Gemini có thể vẫn trả về rỗng hoặc vài chữ ngắn ("Không có") dù
        // prompt đã dặn — model có thể bỏ qua hướng dẫn, code thì không. Form báo cáo phía FE
        // (LessonReportForm.tsx) yêu cầu tối thiểu 10 ký tự cho field này nếu không để trống hẳn — một
        // câu trả lời kiểu "Không có." (9 ký tự) sẽ bị FE từ chối, buộc gia sư phải tự gõ lại. Chuẩn hoá
        // luôn cả trường hợp "có nội dung nhưng quá ngắn", không chỉ trường hợp rỗng hoàn toàn.
        const int minHomeworkLength = 10;
        if (parsed.Homework == null || parsed.Homework.Trim().Length < minHomeworkLength)
            parsed.Homework = "Không đề cập giao bài tập.";

        parsed.SessionMinutes = SessionMinutesNormalizer.Normalize(parsed.SessionMinutes);
        parsed.ZaloSummary = NormalizeZaloSummary(parsed.ZaloSummary);

        return parsed;
    }

    /// <summary>
    /// Giới hạn tham số content/homework/note của template ZBS 640496 (loại "Tên sản phẩm / Thương
    /// hiệu", 200 ký tự). Prompt nhắm ≤ 180 để chừa biên khi model đếm sai.
    /// </summary>
    private const int ZaloSummaryMax = 200;

    /// <summary>
    /// Tóm tắt Zalo là phần phụ — model trả thiếu thì bỏ qua (job gửi tự fallback về bản đầy đủ bị cắt).
    /// Model có thể lờ quy tắc trong prompt nên code chuẩn hoá lại: một dòng, bỏ ký tự markdown/LaTeX,
    /// cắt ở 200 ký tự (ưu tiên cắt tại dấu hết câu để câu không bị cụt).
    /// </summary>
    private static TutorZaloSummary? NormalizeZaloSummary(TutorZaloSummary? summary)
    {
        if (summary is null) return null;

        summary.Content = CleanZaloText(summary.Content);
        summary.Homework = CleanZaloText(summary.Homework);
        summary.Notes = CleanZaloText(summary.Notes);

        if (summary.Content is null && summary.Homework is null && summary.Notes is null)
            return null;
        return summary;
    }

    private static string? CleanZaloText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '#' or '*' or '$' or '`') continue;
            sb.Append(ch);
        }
        var text = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        if (text.Length <= ZaloSummaryMax) return text;

        // Quá dài: giữ lại các câu trọn vẹn nếu còn đủ ý (≥ 1/2 giới hạn), không thì cắt theo từ.
        var head = text[..ZaloSummaryMax];
        var lastStop = head.LastIndexOfAny(new[] { '.', '!', '?', ';' });
        if (lastStop >= ZaloSummaryMax / 2) return head[..(lastStop + 1)].TrimEnd();
        var lastSpace = head.LastIndexOf(' ', ZaloSummaryMax - 2);
        var cut = lastSpace > 0 ? lastSpace : ZaloSummaryMax - 1;
        return text[..cut].TrimEnd(' ', ',', ';', ':') + "…";
    }

    /// <summary>Dòng đầu prompt: môn và lớp (không có tên học sinh) để AI gọi đúng tên kiến thức.</summary>
    private static string BuildReportContextLine(TutorReportContext? context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context?.Subject)) parts.Add($"Môn: {context!.Subject!.Trim()}");
        if (context?.Grade is short grade) parts.Add($"Lớp: {grade}");
        return parts.Count == 0 ? string.Empty : $"Thông tin buổi học: {string.Join(" · ", parts)}\n\n";
    }

    public async Task<string> AskFollowUpAsync(
        string summaryText, IReadOnlyList<GeminiChatTurn> history, string question, CancellationToken ct = default)
    {
        EnsureConfigured();

        var contents = new List<object>();
        foreach (var turn in history)
        {
            // Gemini dùng role "model" cho lượt AI, không phải "assistant".
            var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
            contents.Add(new { role, parts = new object[] { new { text = turn.Content } } });
        }
        contents.Add(new { role = "user", parts = new object[] { new { text = question } } });

        var requestBody = new
        {
            systemInstruction = new
            {
                parts = new object[]
                {
                    new
                    {
                        text = "Bạn là trợ lý trả lời câu hỏi của học sinh về buổi học đã diễn ra, dựa trên nội dung " +
                            "buổi học dưới đây. Trả lời bằng tiếng Việt, giọng thân thiện như một gia sư đang giải " +
                            "thích lại — đừng chỉ nêu đáp án khô khan, hãy giải thích ngắn gọn tại sao/như thế nào " +
                            "khi câu hỏi cần điều đó. Chỉ trả lời trong phạm vi nội dung buổi học, nếu câu hỏi ngoài " +
                            "phạm vi thì nói rõ là không có thông tin trong buổi học này (không bịa). Được dùng " +
                            "markdown (in đậm, gạch đầu dòng) khi giúp câu trả lời dễ đọc hơn. Công thức/ký hiệu " +
                            "toán học viết bằng LaTeX (đặt giữa 1 cặp dấu $ cho công thức ngắn trong câu, giữa 1 " +
                            "cặp dấu $$ cho công thức dài cần tách dòng riêng). Kết thúc câu trả lời " +
                            "bằng 1 câu ngắn gợi ý học sinh có thể hỏi thêm gì liên quan (nếu còn nội dung đáng hỏi " +
                            "trong buổi học), không cần gợi ý nếu câu hỏi đã bao quát hết.\n\n" +
                            $"NỘI DUNG BUỔI HỌC:\n{summaryText}"
                    }
                }
            },
            contents,
            generationConfig = new
            {
                temperature = _settings.Temperature,
                maxOutputTokens = _settings.MaxOutputTokens
            }
        };

        var text = await SendGenerateContentAsync(requestBody, _settings.Model, ct, "FollowUp");
        return text.Trim();
    }

    public async Task<string> SynthesizeChainSummaryAsync(
        IReadOnlyList<(string Label, string Summary)> legSummaries, CancellationToken ct = default)
    {
        EnsureConfigured();

        const string instruction = """
            Bạn là trợ lý tổng hợp tóm tắt buổi học 1-kèm-1. Buổi học dưới đây đã bị ngắt giữa chừng
            ít nhất 1 lần nên được chia thành nhiều buổi liên tiếp (buổi bù/buổi phụ/buổi học lại),
            mỗi buổi đã có tóm tắt riêng theo đúng thứ tự thời gian. Hãy đọc và viết lại thành DUY
            NHẤT một bản tóm tắt liền mạch cho toàn bộ nội dung đã học, như thể đó là một buổi học
            liên tục — không nhắc tới việc buổi học bị chia/nối/ngắt, không lặp lại nội dung trùng
            giữa các buổi. Dùng markdown giống các tóm tắt gốc (tiêu đề phụ "##", in đậm "**...**"
            cho từ khoá quan trọng, gạch đầu dòng "-" cho danh sách). Giữ nguyên công thức toán học
            ở dạng LaTeX (giữa cặp dấu $ hoặc $$) nếu các tóm tắt gốc đã viết như vậy.

            QUAN TRỌNG: chỉ viết những mục thật sự có nội dung. Mục nào không có ở bất kỳ buổi nào
            thì BỎ HẲN, không in tiêu đề của mục đó ra.
            """;

        var joined = string.Join(
            "\n\n",
            legSummaries.Select(leg => $"--- {leg.Label} ---\n{leg.Summary}"));
        var prompt = $"{instruction}\n\n{joined}";

        var schema = new GeminiSchema
        {
            Type = "OBJECT",
            Properties = new Dictionary<string, GeminiSchema> { ["summary"] = new() { Type = "STRING" } },
            Required = ["summary"]
        };

        var requestBody = new
        {
            contents = new object[]
            {
                new { role = "user", parts = new object[] { new { text = prompt } } }
            },
            generationConfig = new Dictionary<string, object?>
            {
                ["temperature"] = _settings.Temperature,
                ["maxOutputTokens"] = _settings.MaxOutputTokens,
                ["thinkingConfig"] = MinimalThinkingConfig,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = schema
            }
        };

        var text = await SendGenerateContentAsync(requestBody, _settings.Model, ct, "ChainSummary");
        var parsed = ParseOrThrow<SummaryJson>(text, "tóm tắt tổng hợp");
        if (string.IsNullOrWhiteSpace(parsed.Summary))
            throw new GeminiResponseParseException("Gemini trả về tóm tắt tổng hợp không hợp lệ.");
        return parsed.Summary.Trim();
    }

    // Các tác vụ dùng chung builder này (tóm tắt học sinh, soát lại, chép lời) chỉ "đọc và tường thuật
    // lại" video, không cần suy luận sâu — hạ thinking xuống mức thấp nhất để trả lời nhanh hơn mức mặc
    // định. Riêng auto-fill báo cáo gia sư truyền ReportThinkingLevel ("high"): ở "minimal" model bỏ sót
    // gần hết các bài đã làm trong buổi (thử 2026-09-25). AskFollowUpAsync (chat hỏi tiếp) KHÔNG dùng builder này, cố tình
    // giữ nguyên thinking mặc định vì trả lời câu hỏi tự do cần suy luận thật.
    //
    // Gemini 3.x đổi hẳn cách cấu hình thinking so với 2.5: không còn "thinkingBudget" (số, 0 = tắt
    // hẳn) mà dùng "thinkingLevel" (chuỗi enum minimal/low/medium/high) — thinkingBudget vẫn được
    // chấp nhận "để tương thích ngược" nhưng Google cảnh báo có thể gây hành vi không như mong đợi
    // trên model 3.x, và dòng Flash 3.x không hỗ trợ tắt hẳn thinking (không có mức "off", thấp nhất
    // là "minimal"). Xem https://ai.google.dev/gemini-api/docs/thinking.
    private static readonly Dictionary<string, object?> MinimalThinkingConfig = new() { ["thinkingLevel"] = "minimal" };

    // Chỉ gửi audio (đã tách khỏi video trước khi upload — xem ClassSessionVideoAiService), không
    // còn frame hình ảnh nào để cấu hình mediaResolution/fps nữa — cắt phần lớn token so với gửi
    // nguyên video, nhanh hơn rõ rệt, đổi lại mất mọi nội dung chỉ hiện trên màn hình mà không nói ra.
    private object BuildGenerateContentRequest(
        string fileUri, string mimeType, string prompt, GeminiSchema? jsonSchema, int? maxOutputTokens = null,
        string? thinkingLevel = null)
    {
        var tokenLimit = maxOutputTokens ?? _settings.MaxOutputTokens;
        var thinkingConfig = string.IsNullOrWhiteSpace(thinkingLevel)
            ? MinimalThinkingConfig
            : new Dictionary<string, object?> { ["thinkingLevel"] = thinkingLevel };
        var generationConfig = jsonSchema is null
            ? new Dictionary<string, object?>
            {
                ["temperature"] = _settings.Temperature,
                ["maxOutputTokens"] = tokenLimit,
                ["thinkingConfig"] = thinkingConfig
            }
            : new Dictionary<string, object?>
            {
                ["temperature"] = _settings.Temperature,
                ["maxOutputTokens"] = tokenLimit,
                ["thinkingConfig"] = thinkingConfig,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = jsonSchema
            };

        return new
        {
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { fileData = new { mimeType, fileUri } },
                        new { text = prompt }
                    }
                }
            },
            generationConfig
        };
    }

    private async Task<string> SendGenerateContentAsync(object requestBody, string model, CancellationToken ct, string caller, string? fileUri = null)
    {
        EnsureConfigured();

        var json = JsonSerializer.Serialize(requestBody, CamelCaseOptions);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var responseBody = await PostGenerateContentWithRetryAsync(json, model, ct);
        sw.Stop();

        var parsed = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(responseBody, CamelCaseOptions);
        var candidate = parsed?.Candidates?.FirstOrDefault();
        if (candidate is null)
        {
            // HTTP 200 nhưng candidates rỗng — thường là Gemini chặn ngay ở tầng PROMPT (trước khi
            // sinh candidate nào), khác với chặn ở tầng candidate (FinishReason=SAFETY, đã có nhánh
            // riêng bên dưới). promptFeedback.blockReason mới cho biết lý do thật; trước đây field
            // này không được parse/log nên mọi lần rơi vào nhánh này đều là hộp đen, không tra được vì
            // sao — log nguyên response ở đây (giống nhánh lỗi HTTP ở PostGenerateContentWithRetryAsync)
            // để lần sau có gì để tra thay vì chỉ có đúng 1 câu chung chung.
            var blockReason = parsed?.PromptFeedback?.BlockReason;
            _logger.LogError(
                "Gemini generateContent trả 200 nhưng candidates rỗng, caller={Caller} fileUri={FileUri} blockReason={BlockReason}: {Body}",
                caller, fileUri, blockReason, responseBody);

            throw new GeminiResponseParseException(
                blockReason != null
                    ? $"Gemini không trả về kết quả nào (bị chặn: {blockReason})."
                    : "Gemini không trả về kết quả nào.");
        }

        if (string.Equals(candidate.FinishReason, "SAFETY", StringComparison.OrdinalIgnoreCase))
            throw new GeminiResponseParseException("Nội dung video bị chặn bởi bộ lọc an toàn của Gemini, không thể tóm tắt.");

        var text = candidate.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new GeminiResponseParseException("Gemini trả về nội dung rỗng.");

        // Số liệu thật để xác nhận thinking có đang chiếm phần lớn thời gian không, thay vì đoán —
        // xem lại log này nếu sau khi tắt thinkingConfig vẫn còn chậm (model mới, chưa chắc field
        // thinkingBudget=0 có tác dụng như kỳ vọng).
        // {Caller}/{FileUri} chỉ để debug thủ công: nhiều loại job (tóm tắt, chép lời, điền báo cáo,
        // hỏi tiếp, tổng hợp chuỗi) đều log chung 1 dòng này nên khi nhiều việc chạy song song trên
        // production, không có 2 trường này thì không cách nào biết dòng log nào ứng với job/buổi học
        // nào. Với job có audio, FileUri khớp trực tiếp với cột Geminifileuri trong class_session_ai_jobs
        // — tra SQL theo giá trị đó ra được classSessionId chính xác.
        if (parsed?.UsageMetadata is { } usage)
        {
            _logger.LogInformation(
                "Gemini usage ({Model}) caller={Caller} fileUri={FileUri}: thoughtsTokens={ThoughtsTokens}, outputTokens={OutputTokens}, totalTokens={TotalTokens}, elapsed={ElapsedMs}ms",
                model, caller, fileUri, usage.ThoughtsTokenCount, usage.CandidatesTokenCount, usage.TotalTokenCount, sw.ElapsedMilliseconds);
        }

        return text;
    }

    /// <summary>Retry cho lỗi tạm thời (mạng chập chờn, Gemini quá tải/5xx) — video đã tốn công
    /// upload + chờ xử lý xong mới tới bước này, để cả job "chết" vì 1 lần trục trặc thoáng qua thì
    /// người dùng phải tóm tắt lại từ đầu, đắt hơn nhiều so với thử lại ngay tại đây. Không retry lỗi
    /// 4xx (request sai, bị chặn an toàn...) vì thử lại cũng vô ích.
    /// 5 lần / backoff 3s-6s-12s-24s (tổng ~45s chờ giữa các lần) — tăng từ 3 lần/~9s sau khi log
    /// production cho thấy có đợt Gemini 503 (quá tải) kéo dài hơn tổng thời gian của 3 lần thử.</summary>
    private async Task<string> PostGenerateContentWithRetryAsync(string json, string model, CancellationToken ct)
    {
        const int maxAttempts = 5;
        var delay = TimeSpan.FromSeconds(3);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"/v1beta/models/{model}:generateContent")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Gemini generateContent lỗi mạng, thử lại lần {Attempt}/{Max}.", attempt, maxAttempts);
                await Task.Delay(delay, ct);
                delay += delay;
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return body;

                var isTransient = (int)response.StatusCode >= 500 || (int)response.StatusCode == 429;
                if (isTransient && attempt < maxAttempts)
                {
                    _logger.LogWarning("Gemini generateContent lỗi tạm thời {StatusCode}, thử lại lần {Attempt}/{Max}.",
                        response.StatusCode, attempt, maxAttempts);
                    await Task.Delay(delay, ct);
                    delay += delay;
                    continue;
                }

                _logger.LogError("Gemini generateContent lỗi: {StatusCode} - {Body}", response.StatusCode, body);
                throw new GeminiApiException((int)response.StatusCode, "Gemini không thể xử lý yêu cầu này.");
            }
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogError("Google Gemini API key is not configured.");
            throw new GeminiApiException(500, "Dịch vụ tóm tắt video chưa được cấu hình.");
        }
    }

    // ─── Batch API (chép lời nền, giá 50%) ─────────────────────────────────────

    public async Task<string> CreateLessonTranscriptBatchAsync(
        IReadOnlyList<GeminiBatchAudioItem> items, string displayName, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (items.Count == 0) throw new ArgumentException("Batch rỗng.", nameof(items));

        var requests = items.Select(i => new
        {
            request = BuildGenerateContentRequest(i.FileUri, i.MimeType, LessonTranscriptPrompt, jsonSchema: null,
                _settings.TranscriptMaxOutputTokens),
            metadata = new { key = i.Key }
        }).ToList();

        var body = new
        {
            batch = new
            {
                displayName,
                inputConfig = new { requests = new { requests } }
            }
        };

        var json = JsonSerializer.Serialize(body, CamelCaseOptions);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1beta/models/{_settings.TranscriptModel}:batchGenerateContent")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini batchGenerateContent lỗi: {StatusCode} - {Body}", response.StatusCode, responseBody);
            throw new GeminiApiException((int)response.StatusCode, "Gemini không tạo được batch chép lời.");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            throw new GeminiResponseParseException("Gemini không trả về tên batch.");

        _logger.LogInformation("Gemini batch {Batch} tạo với {Count} buổi ({Model}).", name, items.Count, _settings.TranscriptModel);
        return name!;
    }

    public async Task<GeminiBatchStatus> GetBatchAsync(string batchName, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1beta/{batchName}");
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Gemini get batch {Batch} lỗi: {StatusCode} - {Body}", batchName, response.StatusCode, body);
            throw new GeminiApiException((int)response.StatusCode, "Không lấy được trạng thái batch.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // REST trả Operation: metadata.state (BATCH_STATE_* hoặc JOB_STATE_*), done, và kết quả nằm ở
        // response.inlinedResponses hoặc metadata.output.inlinedResponses (có nơi bọc thêm một lớp
        // inlinedResponses.inlinedResponses) — đọc mềm dẻo để không vỡ khi Google đổi vỏ.
        string? rawState = null;
        if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("state", out var st))
            rawState = st.GetString();
        var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;

        var state = rawState switch
        {
            { } x when x.Contains("SUCCEEDED", StringComparison.OrdinalIgnoreCase) => GeminiBatchState.Succeeded,
            { } x when x.Contains("FAILED", StringComparison.OrdinalIgnoreCase) => GeminiBatchState.Failed,
            { } x when x.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) => GeminiBatchState.Cancelled,
            { } x when x.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase) => GeminiBatchState.Expired,
            _ when done && root.TryGetProperty("error", out _) => GeminiBatchState.Failed,
            _ when done => GeminiBatchState.Succeeded,
            _ => GeminiBatchState.Running
        };

        var items = new List<GeminiBatchItemResult>();
        if (state == GeminiBatchState.Succeeded)
        {
            var arr = FindInlinedResponses(root);
            if (arr is { } list)
            {
                foreach (var item in list.EnumerateArray())
                {
                    string? key = null;
                    if (item.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
                        && m.TryGetProperty("key", out var k))
                        key = k.GetString();

                    if (item.TryGetProperty("error", out var err))
                    {
                        items.Add(new GeminiBatchItemResult(key, null,
                            err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString()));
                        continue;
                    }

                    string? text = null;
                    string? finish = null;
                    if (item.TryGetProperty("response", out var resp)
                        && resp.TryGetProperty("candidates", out var cands)
                        && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
                    {
                        var c0 = cands[0];
                        if (c0.TryGetProperty("finishReason", out var fr)) finish = fr.GetString();
                        if (c0.TryGetProperty("content", out var content)
                            && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        {
                            text = string.Concat(parts.EnumerateArray()
                                .Where(p => p.TryGetProperty("text", out _))
                                .Select(p => p.GetProperty("text").GetString()));
                        }
                    }

                    items.Add(string.IsNullOrWhiteSpace(text)
                        ? new GeminiBatchItemResult(key, null, finish != null ? $"Không có nội dung (finishReason={finish})." : "Không có nội dung.")
                        : new GeminiBatchItemResult(key, text, null));
                }
            }
            else
            {
                _logger.LogWarning("Gemini batch {Batch} xong nhưng không tìm thấy inlinedResponses.", batchName);
            }
        }

        return new GeminiBatchStatus(state, items, rawState);
    }

    public async Task DeleteBatchAsync(string batchName, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1beta/{batchName}");
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Không xoá được Gemini batch {Batch}: {StatusCode}", batchName, response.StatusCode);
    }

    /// <summary>Tìm mảng inlinedResponses ở response.* hoặc metadata.output.* (có thể bọc 2 lớp).</summary>
    private static JsonElement? FindInlinedResponses(JsonElement root)
    {
        static JsonElement? Unwrap(JsonElement e)
        {
            if (!e.TryGetProperty("inlinedResponses", out var ir)) return null;
            if (ir.ValueKind == JsonValueKind.Array) return ir;
            if (ir.ValueKind == JsonValueKind.Object && ir.TryGetProperty("inlinedResponses", out var inner)
                && inner.ValueKind == JsonValueKind.Array) return inner;
            return null;
        }

        if (root.TryGetProperty("response", out var resp) && Unwrap(resp) is { } a) return a;
        if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("output", out var output)
            && Unwrap(output) is { } b) return b;
        if (root.TryGetProperty("dest", out var dest) && Unwrap(dest) is { } c) return c;
        return null;
    }

    #region Response Models

    private class SummaryJson
    {
        public string? Summary { get; set; }
    }

    private class GeminiFileEnvelope
    {
        public GeminiFile? File { get; set; }
    }

    private class GeminiFile
    {
        public string? Name { get; set; }
        public string? Uri { get; set; }
        public string? State { get; set; }
    }

    private class GeminiSchema
    {
        public string Type { get; set; } = "STRING";
        public Dictionary<string, GeminiSchema>? Properties { get; set; }
        public string[]? Required { get; set; }
        /// <summary>Kiểu phần tử khi Type = "ARRAY".</summary>
        public GeminiSchema? Items { get; set; }
        /// <summary>Giá trị cho phép khi Type = "STRING".</summary>
        public string[]? Enum { get; set; }
    }

    private class GeminiGenerateContentResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
        public GeminiUsageMetadata? UsageMetadata { get; set; }
        public GeminiPromptFeedback? PromptFeedback { get; set; }
    }

    /// <summary>Chỉ có mặt khi Gemini chặn request ngay ở tầng prompt (candidates rỗng) — lý do thật
    /// nằm ở BlockReason (vd "SAFETY", "BLOCKLIST", "PROHIBITED_CONTENT"...).</summary>
    private class GeminiPromptFeedback
    {
        public string? BlockReason { get; set; }
    }

    private class GeminiUsageMetadata
    {
        public int? ThoughtsTokenCount { get; set; }
        public int? CandidatesTokenCount { get; set; }
        public int? TotalTokenCount { get; set; }
    }

    private class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
        public string? FinishReason { get; set; }
    }

    private class GeminiContent
    {
        public List<GeminiPart>? Parts { get; set; }
        public string? Role { get; set; }
    }

    private class GeminiPart
    {
        public string? Text { get; set; }
    }

    #endregion
}
