using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MV.ApplicationLayer.Helpers;
using MV.ApplicationLayer.Interfaces;
using MV.ApplicationLayer.RepositoryInterfaces;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.DomainLayer.Constants;
using MV.DomainLayer.DTO.RequestModel;
using MV.DomainLayer.DTO.ResponseModel;
using MV.DomainLayer.Entities;
using MV.DomainLayer.Exceptions;
using MV.DomainLayer.Helpers;
using System.Diagnostics;
using System.Text.Json;

namespace MV.ApplicationLayer.Services;

public class ClassSessionVideoAiService(
    IAppDbContext db,
    IClassSessionService classSessionService,
    IGoogleDriveService driveService,
    IGeminiVideoAnalysisService geminiService,
    IAiChatRepository aiChatRepo,
    IStudentRepository studentRepo,
    INotificationService notificationService,
    IBackgroundJobClient backgroundJobClient,
    ILogger<ClassSessionVideoAiService> logger) : IClassSessionVideoAiService
{
    // Chỉ gửi audio cho Gemini, không gửi hình — cắt phần lớn token/thời gian xử lý so với gửi
    // nguyên video (đổi lại mất nội dung chỉ hiện trên màn hình mà không nói ra, đã xác nhận với
    // người yêu cầu tính năng là chấp nhận được cho buổi học 1-kèm-1 chủ yếu qua lời nói).
    private const string AudioMimeType = "audio/mp3";
    private const long MaxFileSizeBytes = 2_000_000_000;

    // ── Học sinh: tóm tắt (tự động dựa trên mọi video hiện có trong chuỗi) ──

    /// <summary>1 video trong chuỗi → tóm tắt đúng video đó (student_summary); ≥2 video → tự động hợp
    /// nhất (chain_summary). Học sinh không cần biết/chọn giữa 2 chế độ — chuỗi dài ra tới đâu, job
    /// mới được tạo tới đó, luôn lưu dưới id buổi GỐC để trigger từ buổi nào trong chuỗi cũng ra cùng
    /// 1 kết quả.</summary>
    public async Task<ClassSessionAiJobResponse> TriggerStudentSummaryAsync(int classSessionId, string studentUserId, CancellationToken ct = default)
    {
        var profile = await studentRepo.FindByStudentOrLinkedUserAsync(studentUserId)
            ?? throw new StudentNotFoundException();

        var session = await LoadSessionForAuthAsync(classSessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");
        if (session.Studentid != profile.Studentid)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập buổi học này.");

        var chain = await LoadAiChainOrThrowAsync(classSessionId);
        var rootSessionId = chain[0].ClassSessionId;

        if (chain.Count == 1)
        {
            EnsureRecordingAvailable(session);

            var existingSingle = await FindActiveJobAsync(rootSessionId, ClassSessionAiJobType.StudentSummary, ct);
            if (existingSingle != null)
                return ToResponse(existingSingle);

            var singleJob = await CreateJobAsync(rootSessionId, ClassSessionAiJobType.StudentSummary, studentUserId, ct);
            backgroundJobClient.Enqueue<IClassSessionVideoAiService>(s => s.RunStudentSummaryJobAsync(singleJob.JobId, true));
            return ToResponse(singleJob);
        }

        if (!chain.Any(leg => leg.Available))
            throw new InvalidOperationException("Video buổi học chưa sẵn sàng để phân tích.");

        var existingChain = await FindActiveJobAsync(rootSessionId, ClassSessionAiJobType.ChainSummary, ct);
        if (existingChain != null)
            return ToResponse(existingChain);

        var chainJob = await CreateJobAsync(rootSessionId, ClassSessionAiJobType.ChainSummary, studentUserId, ct);
        backgroundJobClient.Enqueue<IClassSessionVideoAiService>(s => s.RunChainSummaryJobAsync(chainJob.JobId, true));
        return ToResponse(chainJob);
    }

    public async Task<ClassSessionAiJobResponse> GetStudentSummaryStatusAsync(int classSessionId, string studentUserId, CancellationToken ct = default)
    {
        var profile = await studentRepo.FindByStudentOrLinkedUserAsync(studentUserId)
            ?? throw new StudentNotFoundException();
        var session = await LoadSessionForAuthAsync(classSessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");
        if (session.Studentid != profile.Studentid)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập buổi học này.");

        var chain = await LoadAiChainOrThrowAsync(classSessionId);
        var rootSessionId = chain[0].ClassSessionId;
        var jobType = chain.Count > 1 ? ClassSessionAiJobType.ChainSummary : ClassSessionAiJobType.StudentSummary;

        var job = await FindBestJobAsync(rootSessionId, jobType, ct);
        if (job == null)
            return new ClassSessionAiJobResponse { Status = "none" };

        var response = ToResponse(job);

        // Chuỗi vừa dài thêm ra (có buổi bù/phụ/học lại mới) sau khi job chain_summary đã chạy xong —
        // job chép lời của buổi lẻ (job.Classsessionid == classSessionId) chạy tách riêng, có thể xong
        // SAU job chain_summary nên chain_summary chưa kịp có Transcripttext. Không để mất bản chép lời
        // đã có sẵn của buổi lẻ hiện tại chỉ vì chain_summary (cấp cao hơn) chưa gộp kịp.
        if (job.Jobtype == ClassSessionAiJobType.ChainSummary && response.TranscriptText == null)
        {
            var legJob = await FindBestJobAsync(classSessionId, ClassSessionAiJobType.StudentSummary, ct);
            if (legJob?.Transcripttext != null)
                response.TranscriptText = legJob.Transcripttext;
        }

        return response;
    }

    public async Task<string> AskFollowUpAsync(int classSessionId, string studentUserId, string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException("Câu hỏi không được để trống.");

        var profile = await studentRepo.FindByStudentOrLinkedUserAsync(studentUserId)
            ?? throw new StudentNotFoundException();
        var session = await LoadSessionForAuthAsync(classSessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");
        if (session.Studentid != profile.Studentid)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập buổi học này.");

        var chain = await LoadAiChainOrThrowAsync(classSessionId);
        var rootSessionId = chain[0].ClassSessionId;
        var jobType = chain.Count > 1 ? ClassSessionAiJobType.ChainSummary : ClassSessionAiJobType.StudentSummary;

        // Nguồn sự thật cho tóm tắt là job đã Completed (luôn được lưu thành công), không phải việc
        // phiên chat có được tạo đúng hay không — tránh phụ thuộc vào 1 bước phụ (seed chat) có thể lỗi.
        var summaryJob = await db.ClassSessionAiJobs.AsNoTracking()
            .Where(j => j.Classsessionid == rootSessionId
                && j.Jobtype == jobType
                && j.Status == ClassSessionAiJobStatus.Completed
                && j.Resulttext != null)
            .OrderByDescending(j => j.Completedat)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Chưa có tóm tắt video cho buổi học này.");

        // Dùng cả tóm tắt lẫn hội thoại đầy đủ khi có đủ cả 2 (xem BuildChatContext) — job cũ trước khi
        // có tính năng chép lời thì Transcripttext vẫn null, tự động rơi về chỉ dùng tóm tắt.
        var context = BuildChatContext(summaryJob.Resulttext, summaryJob.Transcripttext);
        // Phiên chat gắn theo buổi GỐC của chuỗi — hỏi từ trang buổi nào trong chuỗi cũng ra cùng 1
        // lịch sử hội thoại, không đứt quãng khi chuỗi dài thêm (có buổi phụ/học lại mới).
        var chatSession = await EnsureVideoSummaryChatSessionAsync(rootSessionId, studentUserId, context);
        var (history, summary) = await LoadChatContextAsync(chatSession.SessionId);
        // "??" chỉ chặn null, không chặn chuỗi rỗng — message system lưu trong DB có thể là "" nếu từng
        // bị 1 lượt refresh trước đó ghi đè bằng transcript rỗng (xem EnsureVideoSummaryChatSessionAsync).
        // Coi rỗng như chưa có, luôn fallback về context vừa tính lại từ job (chắc chắn không rỗng).
        var summaryText = !string.IsNullOrWhiteSpace(summary) ? summary : context;

        var now = TimeZoneHelper.UtcNow;
        aiChatRepo.AddMessage(new ChatHistory
        {
            MessageId = Guid.NewGuid(),
            SessionId = chatSession.SessionId,
            Role = ChatHistoryRole.User,
            Content = question,
            CreatedAt = now
        });
        chatSession.UpdatedAt = now;
        aiChatRepo.UpdateSession(chatSession);
        await aiChatRepo.SaveChangesAsync();

        var answer = await geminiService.AskFollowUpAsync(summaryText, history, question, ct);

        aiChatRepo.AddMessage(new ChatHistory
        {
            MessageId = Guid.NewGuid(),
            SessionId = chatSession.SessionId,
            Role = ChatHistoryRole.Assistant,
            Content = answer,
            CreatedAt = TimeZoneHelper.UtcNow
        });
        await aiChatRepo.SaveChangesAsync();

        return answer;
    }

    public async Task<List<ClassSessionVideoChatMessageResponse>> GetFollowUpMessagesAsync(int classSessionId, string studentUserId, CancellationToken ct = default)
    {
        var profile = await studentRepo.FindByStudentOrLinkedUserAsync(studentUserId)
            ?? throw new StudentNotFoundException();
        var session = await LoadSessionForAuthAsync(classSessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");
        if (session.Studentid != profile.Studentid)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập buổi học này.");

        var chain = await LoadAiChainOrThrowAsync(classSessionId);
        var rootSessionId = chain[0].ClassSessionId;

        var chatSession = await aiChatRepo.FindSessionByUserAndClassSessionAsync(studentUserId, ChatSessionType.VideoSummary, rootSessionId);
        if (chatSession == null) return new List<ClassSessionVideoChatMessageResponse>();

        var (items, _) = await aiChatRepo.GetMessagesPagedAsync(chatSession.SessionId, 1, 200);
        // Bỏ message "system" (tóm tắt gốc) khỏi khung chat — FE hiện tóm tắt riêng ở khối trên chat.
        return items
            .Where(m => m.Role != ChatHistoryRole.System)
            .Select(m => new ClassSessionVideoChatMessageResponse { Role = m.Role, Content = m.Content, CreatedAt = m.CreatedAt })
            .ToList();
    }

    // ── Gia sư: auto-fill báo cáo ──────────────────────────────────────

    public async Task<ClassSessionAiJobResponse> TriggerTutorReportFillAsync(int classSessionId, string tutorUserId, CancellationToken ct = default)
    {
        var session = await LoadSessionForAuthAsync(classSessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");
        if (session.Tutorid != tutorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập buổi học này.");

        EnsureRecordingAvailable(session);

        var existing = await FindActiveJobAsync(classSessionId, ClassSessionAiJobType.TutorReportFill, ct);
        if (existing != null)
            return ToResponse(existing);

        var job = await CreateJobAsync(classSessionId, ClassSessionAiJobType.TutorReportFill, tutorUserId, ct);
        backgroundJobClient.Enqueue<IClassSessionVideoAiService>(s => s.RunTutorReportFillJobAsync(job.JobId, true));
        return ToResponse(job);
    }

    public async Task<ClassSessionAiJobResponse> GetTutorReportFillStatusAsync(int classSessionId, string tutorUserId, CancellationToken ct = default)
    {
        var session = await LoadSessionForAuthAsync(classSessionId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");
        if (session.Tutorid != tutorUserId)
            throw new UnauthorizedAccessException("Bạn không có quyền truy cập buổi học này.");

        var job = await FindLatestJobAsync(classSessionId, ClassSessionAiJobType.TutorReportFill, ct);
        return job == null ? new ClassSessionAiJobResponse { Status = "none" } : ToResponse(job);
    }

    private async Task<List<ClassSessionRecordingChainItem>> LoadAiChainOrThrowAsync(int classSessionId)
    {
        var chain = await classSessionService.GetClassSessionAiChainAsync(classSessionId);
        return chain is { Count: > 0 } ? chain : throw new KeyNotFoundException("Không tìm thấy buổi học.");
    }

    // ── Hangfire job bodies ────────────────────────────────────────────

    public async Task RunStudentSummaryJobAsync(Guid jobId, bool swallowFailure)
    {
        var job = await db.ClassSessionAiJobs.FirstOrDefaultAsync(j => j.JobId == jobId);
        if (job == null)
        {
            logger.LogWarning("RunStudentSummaryJobAsync: job {JobId} not found", jobId);
            return;
        }

        try
        {
            job.Status = ClassSessionAiJobStatus.Processing;
            job.Stage = ClassSessionAiJobStage.Analyzing;
            await db.SaveChangesAsync();

            var file = await EnsureUploadedFileAsync(job, CancellationToken.None);

            // Bắn job chép lời NGAY khi file đã upload xong (cache Geminifileuri đã lưu ở
            // EnsureUploadedFileAsync) — thay vì đợi tóm tắt viết xong mới enqueue như trước. 2 job giờ
            // chạy THẬT SỰ song song (interactive vs bulk, 2 worker khác nhau): job chép lời tự đọc lại
            // đúng file đã cache, không tải/tách/upload lại — không có chuyện 2 job cùng upload trùng
            // audio. Tóm tắt (nhanh hơn) vẫn luôn trả cho học sinh trước vì Gemini sinh chữ tuần tự và
            // tóm tắt ngắn hơn transcript 10-15 lần; transcript trả sau, ai đợi nó cũng không cản đường
            // tóm tắt nữa.
            backgroundJobClient.Enqueue<IClassSessionVideoAiService>(s => s.RunStudentTranscriptJobAsync(job.JobId, true));

            // Bỏ lượt xem lại video lần 2 để tự soát (VerifyStudentAnalysisAsync) — tốn gần gấp đôi
            // thời gian chờ cho một bước cải thiện nhỏ, đổi lấy UX nhanh hơn rõ rệt.
            var summary = await geminiService.SummarizeVideoForStudentAsync(file.Uri, AudioMimeType, CancellationToken.None);

            job.Resulttext = summary;
            job.Status = ClassSessionAiJobStatus.Completed;
            // Đánh dấu bản chép lời còn đang chạy nền — job đã Completed vì tóm tắt (thứ người dùng
            // chờ) đã có, transcript thiếu không làm hỏng kết quả.
            job.Stage = ClassSessionAiJobStage.Transcribing;
            job.Completedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();

            // Tách try/catch riêng: lỗi ở bước này (tạo phiên chat) không được làm job quay lại
            // Failed — tóm tắt đã lưu xong là thành công rồi, chat hỏi tiếp chỉ là phần thêm.
            try
            {
                logger.LogInformation(
                    "[VideoSummaryChat] Bắt đầu tạo phiên chat cho classSession {ClassSessionId}, user {UserId}",
                    job.Classsessionid, job.Requestedbyuserid);
                // Tóm tắt và chép lời giờ chạy SONG SONG (2 job riêng, enqueue gần như cùng lúc) nên
                // không còn chắc job nào xong trước — đọc lại Transcripttext mới nhất từ DB (biến `job`
                // trong bộ nhớ được nạp từ đầu hàm, không tự cập nhật nếu job chép lời chạy ở 1
                // DbContext khác đã ghi xong) rồi dùng BuildChatContext như bên chép lời. Nếu chỉ seed
                // bằng `summary` trơn ở đây, lỡ chép lời đã xong trước và chèn message system chứa cả
                // transcript thì bước này sẽ ghi đè mất, làm chat "tụt hạng" về lại bản tóm tắt ngắn.
                var transcriptSoFar = await db.ClassSessionAiJobs.AsNoTracking()
                    .Where(j => j.JobId == job.JobId)
                    .Select(j => j.Transcripttext)
                    .FirstOrDefaultAsync();
                await EnsureVideoSummaryChatSessionAsync(
                    job.Classsessionid, job.Requestedbyuserid, BuildChatContext(summary, transcriptSoFar));
                logger.LogInformation(
                    "[VideoSummaryChat] Đã tạo xong phiên chat cho classSession {ClassSessionId}",
                    job.Classsessionid);
            }
            catch (Exception seedEx)
            {
                logger.LogError(seedEx,
                    "[VideoSummaryChat] LỖI khi tạo phiên chat cho classSession {ClassSessionId}, user {UserId}",
                    job.Classsessionid, job.Requestedbyuserid);
            }

            await NotifyAsync(
                job.Requestedbyuserid,
                "Tóm tắt buổi học đã sẵn sàng",
                $"Video buổi học #{job.Classsessionid} đã được tóm tắt xong. Vào chi tiết buổi học để xem.",
                NotificationType.LessonVideoSummaryReady,
                job.Classsessionid);
        }
        catch (Exception ex) when (swallowFailure)
        {
            await MarkJobFailedAsync(job, ex);
        }
    }

    /// <summary>Chép lời chạy nền sau khi tóm tắt đã trả cho học sinh. Job đã ở trạng thái Completed nên
    /// mọi lỗi ở đây chỉ được ghi log — không kéo job ngược về Failed, vì tóm tắt (thứ người dùng chờ) vẫn
    /// dùng tốt; hỏng chép lời chỉ làm tab "Hội thoại" trống.</summary>
    public async Task RunStudentTranscriptJobAsync(Guid jobId, bool swallowFailure)
    {
        var job = await db.ClassSessionAiJobs.FirstOrDefaultAsync(j => j.JobId == jobId);
        if (job == null)
        {
            logger.LogWarning("RunStudentTranscriptJobAsync: job {JobId} not found", jobId);
            return;
        }

        try
        {
            var file = await EnsureUploadedFileAsync(job, CancellationToken.None);
            var transcript = await geminiService.TranscribeVideoAsync(file.Uri, AudioMimeType, CancellationToken.None);

            job.Transcripttext = transcript;
            job.Stage = null;
            await db.SaveChangesAsync();

            // Nâng ngữ cảnh chat lên bản chép lời (chi tiết hơn tóm tắt) cho các câu hỏi sau. Gọi qua
            // EnsureVideoSummaryChatSessionAsync (thay vì tự tìm+chèn message như trước) để: (1) TẠO LUÔN
            // phiên chat nếu học sinh chưa từng mở khung chat (trước đây bỏ qua hoàn toàn nếu chatSession
            // == null, khiến transcript không bao giờ tới được chat của những học sinh mở chat muộn), và
            // (2) refresh đúng 1 lần nếu nội dung thật sự khác bản đã lưu, không chèn trùng lặp.
            try
            {
                // `job.Resulttext` là bản NẠP LÚC ĐẦU hàm (dòng 331) — từ khi tóm tắt và chép lời chạy
                // song song, tóm tắt (nhanh hơn) rất có thể vừa lưu xong TRONG lúc job này còn đang chờ
                // Gemini chép lời, mà biến `job` trong bộ nhớ không tự cập nhật theo. Đọc lại Resulttext
                // mới nhất từ DB ở đây, không thì hay rơi vào đúng case "tóm tắt xong trước, chép lời
                // xong sau" lại bị coi như tóm tắt chưa có, kết quả chỉ còn mỗi transcript làm ngữ cảnh
                // thay vì gộp cả 2 như đáng lẽ phải vậy.
                var summarySoFar = await db.ClassSessionAiJobs.AsNoTracking()
                    .Where(j => j.JobId == job.JobId)
                    .Select(j => j.Resulttext)
                    .FirstOrDefaultAsync();
                await EnsureVideoSummaryChatSessionAsync(
                    job.Classsessionid, job.Requestedbyuserid, BuildChatContext(summarySoFar, transcript));
            }
            catch (Exception chatEx)
            {
                logger.LogError(chatEx,
                    "[VideoSummaryChat] Không nâng được ngữ cảnh chat lên transcript cho classSession {ClassSessionId}",
                    job.Classsessionid);
            }
        }
        catch (Exception ex) when (swallowFailure)
        {
            logger.LogError(ex,
                "Chép lời buổi học {ClassSessionId} thất bại — tóm tắt vẫn giữ nguyên, chỉ thiếu tab Hội thoại.",
                job.Classsessionid);
            job.Stage = null;
            // Ghi lại lỗi thật thay vì chỉ xoá Stage — nếu không, job Completed/Stage=null/Transcripttext=null
            // này không phân biệt được với job Completed thật sự khoẻ mạnh, khiến GetStudentSummaryStatusAsync
            // (hoặc bất kỳ ai đọc lại sau) không biết đây là job đã fail chép lời.
            job.Errormessage = ex is BadRequestException ? ex.Message : "Chép lời thất bại, vui lòng thử lại.";
            try
            {
                await db.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx, "Không xoá được stage transcribing của job {JobId}.", job.JobId);
            }
        }
    }

    public async Task RunTutorReportFillJobAsync(Guid jobId, bool swallowFailure)
    {
        var job = await db.ClassSessionAiJobs.FirstOrDefaultAsync(j => j.JobId == jobId);
        if (job == null)
        {
            logger.LogWarning("RunTutorReportFillJobAsync: job {JobId} not found", jobId);
            return;
        }

        try
        {
            job.Status = ClassSessionAiJobStatus.Processing;
            await db.SaveChangesAsync();

            var file = await EnsureUploadedFileAsync(job, CancellationToken.None);
            var result = await geminiService.GenerateTutorReportFieldsAsync(file.Uri, AudioMimeType, context: null, CancellationToken.None);

            job.Resultjson = JsonSerializer.Serialize(result);
            job.Status = ClassSessionAiJobStatus.Completed;
            job.Completedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();

            await NotifyAsync(
                job.Requestedbyuserid,
                "Gợi ý báo cáo buổi học đã sẵn sàng",
                $"AI đã điền sẵn nội dung báo cáo cho buổi học #{job.Classsessionid}. Vào trang báo cáo để xem/sửa trước khi nộp.",
                NotificationType.LessonReportAiFillReady,
                job.Classsessionid);
        }
        catch (Exception ex) when (swallowFailure)
        {
            await MarkJobFailedAsync(job, ex);
        }
    }

    /// <summary>Job hợp nhất: tự tóm tắt (và cache lại như 1 job student_summary bình thường) mọi buổi
    /// còn thiếu tóm tắt trong chuỗi, rồi gọi 1 lượt Gemini text-only để viết lại thành 1 bản duy nhất.
    /// Buổi nào lỗi (ghi hình hỏng, Gemini lỗi tạm...) bị bỏ qua, không kéo sập cả bản tổng hợp.</summary>
    public async Task RunChainSummaryJobAsync(Guid jobId, bool swallowFailure)
    {
        var job = await db.ClassSessionAiJobs.FirstOrDefaultAsync(j => j.JobId == jobId);
        if (job == null)
        {
            logger.LogWarning("RunChainSummaryJobAsync: job {JobId} not found", jobId);
            return;
        }

        try
        {
            job.Status = ClassSessionAiJobStatus.Processing;
            await db.SaveChangesAsync();

            var chain = await classSessionService.GetClassSessionAiChainAsync(job.Classsessionid)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học.");

            var legSummaries = new List<(string Label, string Summary)>();
            var legTranscripts = new List<(string Label, string Transcript)>();
            foreach (var leg in chain.Where(l => l.Available))
            {
                try
                {
                    var (summary, transcript) = await GetOrCreateLegSummaryAsync(leg.ClassSessionId, job.Requestedbyuserid, CancellationToken.None);
                    legSummaries.Add((leg.Label, summary));
                    if (transcript != null)
                        legTranscripts.Add((leg.Label, transcript));
                }
                catch (Exception legEx)
                {
                    logger.LogWarning(legEx,
                        "Không tóm tắt được {Label} (classSession {ClassSessionId}) khi tổng hợp chuỗi cho job {JobId}, bỏ qua buổi này.",
                        leg.Label, leg.ClassSessionId, job.JobId);
                }
            }

            if (legSummaries.Count == 0)
                throw new InvalidOperationException("Không buổi nào trong chuỗi tóm tắt được để tổng hợp.");

            // Chỉ 1 buổi tóm tắt được thì dùng thẳng, khỏi tốn thêm 1 lượt gọi Gemini chỉ để "hợp
            // nhất" một mục duy nhất.
            var merged = legSummaries.Count == 1
                ? legSummaries[0].Summary
                : await geminiService.SynthesizeChainSummaryAsync(legSummaries, CancellationToken.None);

            // Chép lời không cần Gemini viết lại như tóm tắt — chỉ là bản ghi thô, nối theo đúng thứ
            // tự thời gian kèm nhãn buổi là đủ, không cần "liền mạch hoá".
            var mergedTranscript = legTranscripts.Count == 0
                ? null
                : string.Join("\n\n", legTranscripts.Select(t => $"## {t.Label}\n{t.Transcript}"));

            job.Resulttext = merged;
            job.Transcripttext = mergedTranscript;
            job.Status = ClassSessionAiJobStatus.Completed;
            job.Completedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();

            // Trước đây job này KHÔNG bao giờ đụng tới phiên chat — học sinh hỏi tiếp về 1 buổi trong
            // chuỗi vẫn chỉ nhận ngữ cảnh của leg đầu tiên (từ lúc chuỗi chưa dài ra) mãi mãi. Giờ
            // EnsureVideoSummaryChatSessionAsync tự refresh nếu nội dung khác bản đã lưu (xem hàm đó),
            // nên gọi lại đây là đủ để chat luôn bắt kịp bản tổng hợp chuỗi mới nhất.
            try
            {
                await EnsureVideoSummaryChatSessionAsync(
                    job.Classsessionid, job.Requestedbyuserid, BuildChatContext(merged, mergedTranscript));
            }
            catch (Exception seedEx)
            {
                logger.LogError(seedEx,
                    "[VideoSummaryChat] LỖI khi refresh phiên chat cho chain summary classSession {ClassSessionId}",
                    job.Classsessionid);
            }

            await NotifyAsync(
                job.Requestedbyuserid,
                "Tóm tắt buổi học đã sẵn sàng",
                $"Video buổi học #{job.Classsessionid} đã được tóm tắt xong. Vào chi tiết buổi học để xem.",
                NotificationType.LessonVideoSummaryReady,
                job.Classsessionid);
        }
        catch (Exception ex) when (swallowFailure)
        {
            await MarkJobFailedAsync(job, ex);
        }
    }

    /// <summary>Tóm tắt + chép lời đúng 1 buổi trong chuỗi nếu chưa có, cache lại như 1 job
    /// student_summary bình thường (để lần sau — kể cả từ trang chi tiết buổi đó — không phải tóm tắt
    /// lại). Tái dùng nguyên vẹn EnsureUploadedFileAsync/SummarizeVideoForStudentAsync/
    /// TranscribeVideoAsync, chỉ khác là chạy đồng bộ ngay trong job hợp nhất (đang chạy nền rồi,
    /// không cần enqueue thêm Hangfire job con).</summary>
    private async Task<(string Summary, string? Transcript)> GetOrCreateLegSummaryAsync(int legClassSessionId, string requestedByUserId, CancellationToken ct)
    {
        var legJob = await db.ClassSessionAiJobs
            .Where(j => j.Classsessionid == legClassSessionId
                && j.Jobtype == ClassSessionAiJobType.StudentSummary
                && j.Status == ClassSessionAiJobStatus.Completed
                && j.Resulttext != null)
            .OrderByDescending(j => j.Completedat)
            .FirstOrDefaultAsync(ct);

        if (legJob == null)
        {
            legJob = await CreateJobAsync(legClassSessionId, ClassSessionAiJobType.StudentSummary, requestedByUserId, ct);
            legJob.Status = ClassSessionAiJobStatus.Processing;
            await db.SaveChangesAsync(ct);

            var summaryFile = await EnsureUploadedFileAsync(legJob, ct);
            legJob.Resulttext = await geminiService.SummarizeVideoForStudentAsync(summaryFile.Uri, AudioMimeType, ct);
            legJob.Status = ClassSessionAiJobStatus.Completed;
            legJob.Completedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // Buổi đã được tóm tắt riêng trước đó (ví dụ học sinh từng xem buổi này khi chuỗi mới có 1
        // video) có thể chưa kịp chép lời xong (chạy nền tách riêng, xem RunStudentTranscriptJobAsync)
        // — job hợp nhất đang chạy nền rồi nên chờ luôn ở đây thay vì bỏ qua.
        if (legJob.Transcripttext == null)
        {
            var transcriptFile = await EnsureUploadedFileAsync(legJob, ct);
            legJob.Transcripttext = await geminiService.TranscribeVideoAsync(transcriptFile.Uri, AudioMimeType, ct);
            legJob.Stage = null;
            await db.SaveChangesAsync(ct);
        }

        return (legJob.Resulttext!, legJob.Transcripttext);
    }

    /// <summary>Best-effort: tải/tách audio/upload video buổi học lên Gemini trước, KHÔNG gọi model sinh
    /// nội dung gì cả — chỉ để tới lúc học sinh/gia sư bấm tóm tắt/điền báo cáo, cache GeminiFileUri
    /// (xem EnsureUploadedFileAsync) đã sẵn sàng từ trước, khỏi phải trả giá tải+transcode+upload (thường
    /// là phần chiếm phần lớn thời gian chờ). Gọi từ RecordingRelayService ngay sau khi video relay xong
    /// lên Drive, trước khi có ai request gì cả. Không throw ra ngoài: lỗi ở đây không ảnh hưởng chức năng
    /// chính, job tóm tắt/điền báo cáo thật vẫn tự upload lại bình thường nếu cache chưa kịp có.</summary>
    public async Task PrewarmGeminiFileAsync(int classSessionId)
    {
        var now = TimeZoneHelper.UtcNow;
        var alreadyWarm = await db.ClassSessionAiJobs.AnyAsync(j => j.Classsessionid == classSessionId
            && j.Geminifileuri != null && j.Geminifilename != null
            && j.Geminifileexpiresat != null && j.Geminifileexpiresat > now);
        if (alreadyWarm)
            return;

        var job = new ClassSessionAiJob
        {
            JobId = Guid.NewGuid(),
            Classsessionid = classSessionId,
            Jobtype = ClassSessionAiJobType.Prewarm,
            Requestedbyuserid = "system",
            Status = ClassSessionAiJobStatus.Processing,
            Createdat = now
        };
        db.ClassSessionAiJobs.Add(job);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Race hiếm: request thật (tóm tắt/điền báo cáo) đã tự upload và cache xong trước khi prewarm
            // kịp lưu job — coi như đã "nóng" rồi, không cần làm gì thêm.
            return;
        }

        try
        {
            await EnsureUploadedFileAsync(job, CancellationToken.None);
            job.Status = ClassSessionAiJobStatus.Completed;
            job.Completedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Prewarm Gemini file cho classSession {ClassSessionId} thất bại — không ảnh hưởng chức năng chính, job tóm tắt/điền báo cáo thật sẽ tự upload lại khi cần.",
                classSessionId);
            job.Status = ClassSessionAiJobStatus.Failed;
            job.Errormessage = "Prewarm thất bại (không ảnh hưởng chức năng chính).";
            job.Completedat = TimeZoneHelper.UtcNow;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx, "Không lưu được trạng thái Failed cho prewarm job classSession {ClassSessionId}.", classSessionId);
            }
        }
    }

    // ── Helpers dùng chung ─────────────────────────────────────────────

    /// <summary>Tái dùng GeminiFileUri còn hạn của bất kỳ job nào (2 loại) thuộc cùng buổi học; nếu không có thì tải từ Drive + upload mới.</summary>
    private async Task<GeminiUploadedFile> EnsureUploadedFileAsync(ClassSessionAiJob job, CancellationToken ct)
    {
        var now = TimeZoneHelper.UtcNow;
        var cached = await db.ClassSessionAiJobs
            .Where(j => j.Classsessionid == job.Classsessionid
                && j.Geminifileuri != null && j.Geminifilename != null
                && j.Geminifileexpiresat != null && j.Geminifileexpiresat > now)
            .OrderByDescending(j => j.Createdat)
            .FirstOrDefaultAsync(ct);

        if (cached != null)
            return new GeminiUploadedFile(cached.Geminifilename!, cached.Geminifileuri!);

        var fileId = await classSessionService.GetRecordingDriveFileIdAsync(job.Classsessionid)
            ?? throw new GeminiFileProcessingException("Buổi học chưa có video để phân tích.");

        GeminiUploadedFile uploaded;
        string audioPath;
        // Đo tạm thời để tìm bước nào chiếm phần lớn thời gian chờ (người dùng thấy chậm dù video
        // ngắn) — tải Drive + tách audio chạy chung 1 stream pipe nên không tách được thành 2 mốc
        // riêng, tính gộp. Bỏ log này khi đã xác định rõ bottleneck ở đâu, không để lại vĩnh viễn.
        var downloadSw = Stopwatch.StartNew();
        using (var media = await driveService.GetMediaAsync(fileId, null, ct))
        {
            if (media.ContentLength is { } size && size > MaxFileSizeBytes)
                throw new GeminiVideoTooLargeException();

            audioPath = await ExtractAudioToTempFileAsync(media.Content, job.Classsessionid, ct);
        }
        downloadSw.Stop();

        var uploadSw = Stopwatch.StartNew();
        try
        {
            await using var audioStream = File.OpenRead(audioPath);
            var audioLength = new FileInfo(audioPath).Length;
            uploaded = await geminiService.UploadVideoAsync(
                audioStream, audioLength, AudioMimeType, $"class-session-{job.Classsessionid}.mp3", ct);
        }
        finally
        {
            File.Delete(audioPath);
        }
        uploadSw.Stop();

        var pollSw = Stopwatch.StartNew();
        await geminiService.WaitForFileActiveAsync(uploaded.Name, ct);
        pollSw.Stop();

        logger.LogInformation(
            "[VideoAI timing] classSession={ClassSessionId}: tải Drive+tách audio={DownloadMs}ms, upload Gemini={UploadMs}ms, chờ Gemini xử lý xong (ACTIVE)={PollMs}ms",
            job.Classsessionid, downloadSw.ElapsedMilliseconds, uploadSw.ElapsedMilliseconds, pollSw.ElapsedMilliseconds);

        job.Geminifileuri = uploaded.Uri;
        job.Geminifilename = uploaded.Name;
        // Gemini giữ file ~48h — trừ hao 1h để không dùng cache sát nút hết hạn.
        job.Geminifileexpiresat = TimeZoneHelper.UtcNow.AddHours(47);
        await db.SaveChangesAsync(ct);

        return uploaded;
    }

    /// <summary>Tách track audio khỏi video buổi học (stream thẳng từ Drive qua ffmpeg, không ghi video gốc
    /// ra đĩa) — chỉ file audio kết quả (nhỏ hơn nhiều) mới được ghi tạm, xoá ngay sau khi upload xong.</summary>
    private async Task<string> ExtractAudioToTempFileAsync(Stream videoStream, int classSessionId, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"class-session-audio-{Guid.NewGuid():N}.mp3");
        try
        {
            await FFMpegArguments
                .FromPipeInput(new StreamPipeSource(videoStream))
                .OutputToFile(tempPath, overwrite: true, options => options
                    .WithAudioCodec("libmp3lame")
                    .DisableChannel(Channel.Video))
                .ProcessAsynchronously();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tách audio khỏi video buổi học {ClassSessionId} thất bại.", classSessionId);
            throw new GeminiFileProcessingException("Không thể tách âm thanh từ video buổi học.");
        }
        return tempPath;
    }

    private async Task MarkJobFailedAsync(ClassSessionAiJob job, Exception ex)
    {
        logger.LogError(ex, "Job {JobId} (type={JobType}, classSession={ClassSessionId}) thất bại.",
            job.JobId, job.Jobtype, job.Classsessionid);
        job.Status = ClassSessionAiJobStatus.Failed;
        job.Errormessage = ex is BadRequestException ? ex.Message : "Có lỗi xảy ra, vui lòng thử lại.";
        job.Completedat = TimeZoneHelper.UtcNow;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception saveEx)
        {
            logger.LogError(saveEx, "Không lưu được trạng thái Failed cho job {JobId}.", job.JobId);
        }
    }

    private Task<ClassSessionAiJob?> FindActiveJobAsync(int classSessionId, string jobType, CancellationToken ct)
        => db.ClassSessionAiJobs
            .Where(j => j.Classsessionid == classSessionId && j.Jobtype == jobType
                && (j.Status == ClassSessionAiJobStatus.Pending || j.Status == ClassSessionAiJobStatus.Processing))
            .OrderByDescending(j => j.Createdat)
            .FirstOrDefaultAsync(ct);

    private Task<ClassSessionAiJob?> FindLatestJobAsync(int classSessionId, string jobType, CancellationToken ct)
        => db.ClassSessionAiJobs
            .AsNoTracking()
            .Where(j => j.Classsessionid == classSessionId && j.Jobtype == jobType)
            .OrderByDescending(j => j.Createdat)
            .FirstOrDefaultAsync(ct);

    /// <summary>Như FindLatestJobAsync, nhưng ưu tiên job đã Completed VÀ thực sự có nội dung
    /// (Resulttext != null) trước, mới tới mới nhất theo Createdat. Bắt buộc phải lọc theo nội dung vì
    /// RunStudentTranscriptJobAsync/RunChainSummaryJobAsync có thể tạo 1 job MỚI hơn nhưng job đó fail
    /// âm thầm (Completed nhưng Resulttext/Transcripttext rỗng) — nếu chỉ lấy theo Createdat mới nhất,
    /// job rỗng này sẽ đè lên job cũ đã có kết quả tốt, khiến client thấy "completed" nhưng rỗng nội dung.</summary>
    private Task<ClassSessionAiJob?> FindBestJobAsync(int classSessionId, string jobType, CancellationToken ct)
        => db.ClassSessionAiJobs
            .AsNoTracking()
            .Where(j => j.Classsessionid == classSessionId && j.Jobtype == jobType)
            .OrderByDescending(j => j.Status == ClassSessionAiJobStatus.Completed && j.Resulttext != null)
            // Không có unique index chặn tạo job mới khi job cũ đã Completed (TriggerStudentSummaryAsync
            // chỉ tái dùng job đang Pending/Processing) — bấm tóm tắt lại tạo hẳn 1 row mới. Row mới có
            // thể đã Completed + có Resulttext nhưng job chép lời riêng (RunStudentTranscriptJobAsync)
            // của NÓ chưa xong/đã fail, nên Transcripttext vẫn null — nếu chỉ so theo Createdat, row mới
            // rỗng transcript này sẽ đè lên 1 row CŨ hơn đã có transcript đầy đủ. Ưu tiên transcript có
            // nội dung trước khi so tới thời gian.
            .ThenByDescending(j => j.Transcripttext != null)
            .ThenByDescending(j => j.Createdat)
            .FirstOrDefaultAsync(ct);

    private async Task<ClassSessionAiJob> CreateJobAsync(int classSessionId, string jobType, string userId, CancellationToken ct)
    {
        var job = new ClassSessionAiJob
        {
            JobId = Guid.NewGuid(),
            Classsessionid = classSessionId,
            Jobtype = jobType,
            Requestedbyuserid = userId,
            Status = ClassSessionAiJobStatus.Pending,
            Createdat = TimeZoneHelper.UtcNow
        };
        db.ClassSessionAiJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race: 2 request cùng lúc đều pass qua FindActiveJobAsync trước khi cái đầu insert xong —
            // unique index là nguồn sự thật, request thua cuộc dùng lại job của người thắng.
            db.ClassSessionAiJobs.Remove(job);
            var winner = await FindActiveJobAsync(classSessionId, jobType, ct)
                ?? throw new InvalidOperationException("Không thể tạo yêu cầu phân tích video.");
            return winner;
        }
        return job;
    }

    private Task<ClassSession?> LoadSessionForAuthAsync(int classSessionId, CancellationToken ct)
        => db.ClassSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Classsessionid == classSessionId, ct);

    private static void EnsureRecordingAvailable(ClassSession session)
    {
        var (status, _) = RecordingStatusResolver.Resolve(
            session.Recordingurl, session.Recordings3key, session.Recordingsid, session.Checkouttime.HasValue);
        if (status != "available")
            throw new InvalidOperationException("Video buổi học chưa sẵn sàng để phân tích.");
    }

    private static ClassSessionAiJobResponse ToResponse(ClassSessionAiJob job) => new()
    {
        JobId = job.JobId,
        Status = job.Status,
        Stage = job.Stage,
        ResultText = job.Resulttext,
        TranscriptText = job.Transcripttext,
        ResultJson = job.Resultjson != null ? JsonSerializer.Deserialize<TutorReportAiFillResult>(job.Resultjson) : null,
        ErrorMessage = job.Errormessage
    };

    private async Task NotifyAsync(string userId, string title, string message, string type, int classSessionId)
    {
        try
        {
            await notificationService.CreateNotificationAsync(new NotificationRequest
            {
                Userid = userId,
                Title = title,
                Message = message,
                Type = type,
                Referenceid = classSessionId.ToString()
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không gửi được thông báo {Type} cho user {UserId}", type, userId);
        }
    }

    /// <summary>Ghép tóm tắt + hội thoại đầy đủ làm ngữ cảnh chat khi có đủ cả 2 — tóm tắt cho cái nhìn
    /// tổng quan súc tích (đã có cấu trúc, công thức, ý chính), hội thoại cho chi tiết nguyên văn khi học
    /// sinh cần hỏi trích dẫn chính xác câu chữ gia sư nói. Chỉ dùng 1 trong 2 khi cái còn lại chưa có
    /// (job chép lời/tổng hợp transcript chạy nền riêng, có thể chưa xong tại thời điểm gọi).</summary>
    private static string BuildChatContext(string? resultText, string? transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return resultText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resultText))
            return transcriptText;
        return $"## Tóm tắt\n{resultText}\n\n## Hội thoại đầy đủ\n{transcriptText}";
    }

    /// <summary>Tìm hoặc tạo phiên chat tóm tắt cho (user, classSessionId); luôn refresh message "system"
    /// nếu ngữ cảnh mới nhất (summary/transcript/chain vừa cập nhật) khác nội dung đã lưu — self-healing,
    /// không phụ thuộc bước seed trong Hangfire job phải chạy đúng và đúng thứ tự thì chat mới dùng được.
    ///
    /// Trước đây chỉ chèn message system MỘT LẦN DUY NHẤT lúc tạo phiên rồi bỏ qua mãi mãi nếu đã có 1
    /// message system bất kỳ — khiến chat bị "đóng băng" ở bản tóm tắt ngắn ban đầu, không bao giờ thấy
    /// được bản chép lời chi tiết hơn (job riêng, chạy xong sau) hay bản tổng hợp chuỗi mới hơn. Giờ luôn
    /// so sánh với nội dung mới nhất, chỉ chèn thêm khi thực sự khác — không so dựa vào "có message system
    /// hay chưa" nữa.</summary>
    private async Task<ChatSession> EnsureVideoSummaryChatSessionAsync(int classSessionId, string userId, string summary)
    {
        var now = TimeZoneHelper.UtcNow;
        var existing = await aiChatRepo.FindSessionByUserAndClassSessionAsync(userId, ChatSessionType.VideoSummary, classSessionId);

        if (existing == null)
        {
            existing = new ChatSession
            {
                SessionId = Guid.NewGuid(),
                UserId = userId,
                SessionType = ChatSessionType.VideoSummary,
                ClassSessionId = classSessionId,
                Title = $"Tóm tắt buổi học #{classSessionId}",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            aiChatRepo.AddSession(existing);
            try
            {
                await aiChatRepo.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Race: job tóm tắt (tự seed) và học sinh hỏi câu đầu tiên (AskFollowUpAsync cũng tự gọi
                // hàm này) có thể cùng thấy existing == null và cùng insert — unique index
                // ux_chat_sessions_user_type_class_session là nguồn sự thật, request thua cuộc dùng lại
                // đúng phiên của người thắng thay vì tạo phiên trùng (từng gây ra 2 phiên độc lập, ngữ
                // cảnh mới bị ghi nhầm vào phiên mà chat KHÔNG dùng).
                existing = await aiChatRepo.FindSessionByUserAndClassSessionAsync(userId, ChatSessionType.VideoSummary, classSessionId)
                    ?? throw new InvalidOperationException("Không thể tạo phiên chat tóm tắt video.");
            }
        }

        var (items, _) = await aiChatRepo.GetMessagesPagedAsync(existing.SessionId, 1, 200);
        var lastSystemContent = items.LastOrDefault(m => m.Role == ChatHistoryRole.System)?.Content;
        // Chặn ghi đè bằng chuỗi RỖNG: TranscribeVideoAsync/SummarizeVideoForStudentAsync chỉ kiểm tra
        // != null trước khi trả về, không kiểm tra rỗng — Gemini có thể hợp lệ về mặt JSON nhưng trả
        // transcript/summary rỗng (buổi ghi âm quá ngắn/im lặng...). Nếu không chặn ở đây, message system
        // TỐT đang có (tóm tắt đầy đủ) sẽ bị 1 lượt gọi sau đó (vd job chép lời fail-nhẹ trả về "") ghi đè
        // thành rỗng — từ đó AskFollowUpAsync gửi context rỗng cho Gemini, học sinh nhận câu trả lời kiểu
        // "chưa có nội dung buổi học" dù tóm tắt vẫn hiển thị đầy đủ trên UI (đọc thẳng từ Resulttext, không
        // qua chat_histories).
        if (!string.IsNullOrWhiteSpace(summary) && lastSystemContent != summary)
        {
            aiChatRepo.AddMessage(new ChatHistory
            {
                MessageId = Guid.NewGuid(),
                SessionId = existing.SessionId,
                Role = ChatHistoryRole.System,
                Content = summary,
                CreatedAt = now
            });
            existing.UpdatedAt = now;
            await aiChatRepo.SaveChangesAsync();
        }

        return existing;
    }

    /// <summary>Trả (lịch sử user/assistant để đưa Gemini, tóm tắt gốc lấy từ message system mới nhất).</summary>
    private async Task<(IReadOnlyList<GeminiChatTurn> History, string? Summary)> LoadChatContextAsync(Guid sessionId)
    {
        var (items, _) = await aiChatRepo.GetMessagesPagedAsync(sessionId, 1, 200);
        var summary = items.LastOrDefault(m => m.Role == ChatHistoryRole.System)?.Content;
        var history = items
            .Where(m => m.Role != ChatHistoryRole.System)
            .Select(m => new GeminiChatTurn(m.Role, m.Content))
            .ToList();
        return (history, summary);
    }
}
