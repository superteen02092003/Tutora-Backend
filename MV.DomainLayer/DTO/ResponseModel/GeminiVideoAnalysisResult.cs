namespace MV.DomainLayer.DTO.ResponseModel;

/// <summary>File đã upload lên Gemini File API — Name dùng để poll trạng thái, Uri dùng để tham chiếu trong generateContent.</summary>
public sealed record GeminiUploadedFile(string Name, string Uri);

/// <summary>Một lượt hội thoại dùng để dựng lại context cho follow-up chat. Role: "user" | "assistant".</summary>
public sealed record GeminiChatTurn(string Role, string Content);

/// <summary>Kết quả AI tự động điền báo cáo buổi học cho gia sư — khớp 3 field free-text của <c>ClassSessionReport</c>.</summary>
public sealed class TutorReportAiFillResult
{
    public string LessonContent { get; set; } = string.Empty;
    public string Homework { get; set; } = string.Empty;
    public string TutorNotes { get; set; } = string.Empty;

    /// <summary>
    /// Biên bản buổi học dành riêng cho GIA SƯ (không gửi phụ huynh) — thay cho việc xem lại toàn bộ
    /// lời thoại/audio. Sinh chung trong cùng lượt gọi Gemini với báo cáo để không tốn thêm một lượt
    /// nghe audio. Nullable: bản nháp cũ (trước khi có field này) hoặc model bỏ sót thì là null.
    /// </summary>
    public TutorSessionMinutes? SessionMinutes { get; set; }

    /// <summary>
    /// Bản tóm tắt NGẮN gửi phụ huynh qua tin Zalo (ZBS template: giá trị dòng bảng tối đa 90 ký tự).
    /// Sinh chung lượt gọi Gemini với báo cáo đầy đủ. Nullable: bản nháp cũ hoặc model bỏ sót thì là
    /// null — job gửi Zalo sẽ fallback về bản đầy đủ bị cắt ngắn.
    /// </summary>
    public TutorZaloSummary? ZaloSummary { get; set; }
}

/// <summary>Tóm tắt báo cáo cho tin Zalo: mỗi field một câu hoàn chỉnh, văn bản thuần, ≤ 90 ký tự.</summary>
public sealed class TutorZaloSummary
{
    public string? Content { get; set; }
    public string? Homework { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Biên bản buổi học cho gia sư — đọc lại thay cho nghe lại cả buổi. Chỉ ghi dạng bài và kiến thức, không
/// chép đề bài hay con số (quyết định 2026-09-29). Summary/KeyPoints/FollowUps có từ đầu; các field còn lại
/// thêm 2026-09-29 nên bản nháp cũ để trống.
/// </summary>
public sealed class TutorSessionMinutes
{
    /// <summary>3–5 câu: mục tiêu, diễn biến chính, kết quả của buổi.</summary>
    public string? Summary { get; set; }

    /// <summary>Diễn biến buổi học theo thứ tự thời gian (2–8 phần).</summary>
    public List<TutorMinutesSection> Sections { get; set; } = new();

    /// <summary>Các bài đã làm, ghi theo dạng bài (tối đa 15).</summary>
    public List<TutorMinutesExercise> Exercises { get; set; } = new();

    /// <summary>Điều học sinh đã nắm, kèm dẫn chứng (tối đa 5).</summary>
    public List<string> Strengths { get; set; } = new();

    /// <summary>Chỗ học sinh còn sai/chưa hiểu, nêu đúng bước sai (tối đa 5).</summary>
    public List<string> Difficulties { get; set; } = new();

    /// <summary>Thông tin có ích cho việc dạy: mẹo, kinh nghiệm học/làm bài/đi thi, lịch kiểm tra, cách
    /// học và động lực của học sinh (tối đa 6).</summary>
    public List<string> UsefulNotes { get; set; } = new();

    /// <summary>0–3 nhận xét về cách dạy, mỗi mục kèm tình huống và câu hỏi gợi ý.</summary>
    public List<TutorTeachingNote> TeachingNotes { get; set; } = new();

    /// <summary>3–6 kiến thức quan trọng nhất cần nhớ.</summary>
    public List<string> KeyPoints { get; set; } = new();

    /// <summary>0–6 việc gia sư cần làm/nhớ cho buổi sau.</summary>
    public List<string> FollowUps { get; set; } = new();
}

/// <summary>Một phần của buổi học: tiêu đề ngắn và các ý chi tiết.</summary>
public sealed class TutorMinutesSection
{
    public string? Title { get; set; }
    public List<string> Details { get; set; } = new();
}

/// <summary>Một bài đã làm, ghi theo dạng bài (không chép đề).</summary>
public sealed class TutorMinutesExercise
{
    /// <summary>Dạng bài, viết bằng lời.</summary>
    public string? Type { get; set; }
    /// <summary>Một trong <see cref="Results"/>.</summary>
    public string? Result { get; set; }
    /// <summary>Học sinh vướng ở bước nào / gia sư gợi ý gì.</summary>
    public string? Note { get; set; }

    public static readonly string[] Results =
        ["Tự làm đúng", "Đúng sau khi được gợi ý", "Làm sai", "Chưa làm xong", "Gia sư làm mẫu"];
}

/// <summary>Nhận xét về cách dạy (theo các "nước đi" sư phạm của Tutor CoPilot, arXiv 2410.03017).</summary>
public sealed class TutorTeachingNote
{
    public string? Content { get; set; }
    /// <summary>Tình huống trong buổi và một câu hỏi gia sư có thể dùng lần sau.</summary>
    public string? Example { get; set; }
}

/// <summary>Thông tin buổi học gửi kèm audio để AI viết đúng môn, đúng trình độ. Không chứa tên học sinh.</summary>
public sealed record TutorReportContext(string? Subject, short? Grade);

/// <summary>Một buổi trong batch chép lời: Key để khớp kết quả trả về (lessonId).</summary>
public sealed record GeminiBatchAudioItem(string Key, string FileUri, string MimeType);

public enum GeminiBatchState { Running, Succeeded, Failed, Cancelled, Expired }

/// <summary>Kết quả một yêu cầu trong batch. Text null = yêu cầu đó lỗi (xem Error).</summary>
public sealed record GeminiBatchItemResult(string? Key, string? Text, string? Error);

public sealed record GeminiBatchStatus(GeminiBatchState State, IReadOnlyList<GeminiBatchItemResult> Items, string? RawState);
