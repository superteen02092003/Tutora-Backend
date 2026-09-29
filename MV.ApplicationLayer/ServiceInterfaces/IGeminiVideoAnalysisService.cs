using MV.DomainLayer.DTO.ResponseModel;

namespace MV.ApplicationLayer.ServiceInterfaces
{
    /// <summary>
    /// Client gọi thẳng Gemini File API + generateContent để phân tích video buổi học.
    /// Không đụng DB — mọi lỗi (upload thất bại, file xử lý lỗi, HTTP lỗi, response không hợp lệ)
    /// đều throw để caller (ClassSessionVideoAiService) tự quyết định nuốt lỗi hay không.
    /// </summary>
    public interface IGeminiVideoAnalysisService
    {
        /// <summary>Upload video lên Gemini File API (resumable upload) — stream thẳng, không buffer cả file vào RAM.</summary>
        Task<GeminiUploadedFile> UploadVideoAsync(
            Stream videoStream, long contentLength, string mimeType, string displayName, CancellationToken ct = default);

        /// <summary>Chờ file chuyển sang state ACTIVE (Gemini xử lý video xong) — poll định kỳ, có timeout.</summary>
        Task WaitForFileActiveAsync(string fileName, CancellationToken ct = default);

        /// <summary>Tóm tắt buổi học bằng tiếng Việt. Tách riêng khỏi chép lời vì đầu ra ngắn hơn 10-15 lần
        /// nên xong sau vài giây — gộp chung 1 lượt gọi thì người dùng phải đợi cả bản chép lời viết xong
        /// mới thấy được tóm tắt (LLM sinh token tuần tự, response chỉ về khi viết hết).</summary>
        Task<string> SummarizeVideoForStudentAsync(string fileUri, string mimeType, CancellationToken ct = default);

        /// <summary>Chép lời (transcript) đầy đủ buổi học. Chạy nền sau khi tóm tắt đã trả cho người dùng,
        /// dùng model riêng (<see cref="MV.DomainLayer.Configuration.GoogleGeminiSettings.TranscriptModel"/>).</summary>
        Task<string> TranscribeVideoAsync(string fileUri, string mimeType, CancellationToken ct = default);

        /// <summary>Chép lời bản ghi âm từ app gia sư, MỖI lượt nói một dòng có mốc thời gian:
        /// "[mm:ss] Gia sư: …" — để app hiển thị và chạm vào mốc thì tua tới đó.</summary>
        Task<string> TranscribeLessonAudioAsync(string fileUri, string mimeType, CancellationToken ct = default);

        /// <summary>Gửi một batch chép lời (Gemini Batch API, giá 50%) cho nhiều buổi. Trả về tên
        /// batch (batches/...). Cùng prompt với TranscribeLessonAudioAsync.</summary>
        Task<string> CreateLessonTranscriptBatchAsync(
            IReadOnlyList<GeminiBatchAudioItem> items, string displayName, CancellationToken ct = default);

        /// <summary>Trạng thái + kết quả (khi đã xong) của một batch.</summary>
        Task<GeminiBatchStatus> GetBatchAsync(string batchName, CancellationToken ct = default);

        /// <summary>Xoá batch trên Google sau khi đã lấy kết quả — mặc định Google giữ kết quả 6 tuần.</summary>
        Task DeleteBatchAsync(string batchName, CancellationToken ct = default);

        /// <summary>Sinh nội dung báo cáo có cấu trúc (structured JSON output) cho gia sư.</summary>
        Task<TutorReportAiFillResult> GenerateTutorReportFieldsAsync(
            string fileUri, string mimeType, TutorReportContext? context = null, CancellationToken ct = default);

        /// <summary>Trả lời câu hỏi tiếp theo dựa trên tóm tắt đã có + lịch sử hội thoại — không cần video nữa.</summary>
        Task<string> AskFollowUpAsync(
            string summaryText, IReadOnlyList<GeminiChatTurn> history, string question, CancellationToken ct = default);

        /// <summary>Hợp nhất nhiều tóm tắt (mỗi buổi trong 1 chuỗi bù/phụ/học lại đã có tóm tắt riêng)
        /// thành 1 bản tóm tắt liền mạch duy nhất — chỉ gửi text, không upload/đọc lại video nào.</summary>
        Task<string> SynthesizeChainSummaryAsync(
            IReadOnlyList<(string Label, string Summary)> legSummaries, CancellationToken ct = default);
    }
}
