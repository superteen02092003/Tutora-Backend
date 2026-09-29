namespace MV.DomainLayer.DTO.ResponseModel;

/// <summary>Kết quả mở một bản ghi cho buổi học.</summary>
public class AppRecordingStartResponse
{
    public Guid RecordingId { get; set; }

    /// <summary>Tên học sinh — app hiển thị lại để gia sư xác nhận đúng buổi.</summary>
    public string StudentName { get; set; } = string.Empty;

    /// <summary>Số phần đã có sẵn trên kho (khi mở lại bản ghi còn dở).</summary>
    public int UploadedParts { get; set; }
}

/// <summary>Một URL presigned để app PUT thẳng một đoạn lên kho.</summary>
public class AppRecordingUploadUrlResponse
{
    public int PartNumber { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Trạng thái bản ghi + báo cáo AI khi đã có.</summary>
public class AppRecordingStatusResponse
{
    /// <summary>= LessonId trong recorder.lessons (app vẫn gọi là recordingId).</summary>
    public Guid RecordingId { get; set; }

    /// <summary>Có khi buổi thuộc booking; null với học sinh ngoài nền tảng.</summary>
    public int? ClassSessionId { get; set; }

    public Guid? StudentId { get; set; }
    public string StudentName { get; set; } = string.Empty;
    public string? DeliveryChannel { get; set; }
    public string? DeliveryStatus { get; set; }
    /// <summary>Lý do gửi phụ huynh thất bại (khi DeliveryStatus báo lỗi) — để app hiện cho gia sư.</summary>
    public string? DeliveryError { get; set; }
    /// <summary>SĐT phụ huynh của học sinh ngoài nền tảng — app cần để gia sư kiểm tra/gửi lại. Null với buổi booking.</summary>
    public string? ParentPhone { get; set; }

    // ── Chi tiết buổi (màn xem lại) ──────────────────────────────────────
    public short? Grade { get; set; }
    public string? Subject { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? SentAt { get; set; }

    /// <summary>Lời thoại: mỗi dòng "[mm:ss] Gia sư: …".</summary>
    public string? Transcript { get; set; }
    /// <summary>none | pending | processing | completed | failed</summary>
    public string TranscriptStatus { get; set; } = "none";

    /// <summary>Còn file để nghe lại không.</summary>
    public bool AudioAvailable { get; set; }
    /// <summary>File âm thanh bị xoá sau thời điểm này.</summary>
    public DateTime? AudioExpiresAt { get; set; }

    /// <summary>Xem SessionRecordingStatus.</summary>
    public string Status { get; set; } = string.Empty;

    public int PartCount { get; set; }
    public long Bytes { get; set; }
    public int DurationSec { get; set; }

    /// <summary>Trạng thái job AI: pending | processing | completed | failed | none.</summary>
    public string AiStatus { get; set; } = "none";

    public string? LessonContent { get; set; }
    public string? Homework { get; set; }
    public string? TutorNotes { get; set; }

    /// <summary>
    /// Bản tóm tắt ngắn (≤ 90 ký tự, văn bản thuần) gửi phụ huynh qua tin Zalo — bản đã duyệt, chưa
    /// duyệt thì là bản nháp AI. Null khi AI chưa xong hoặc bản nháp cũ chưa có phần này.
    /// </summary>
    public string? ZaloContent { get; set; }
    public string? ZaloHomework { get; set; }
    public string? ZaloNotes { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Biên bản buổi học cho gia sư (thay cho xem lại transcript/audio). Null khi AI chưa xong hoặc bản
    /// nháp cũ chưa có phần này.
    /// </summary>
    public AppRecordingSessionMinutes? SessionMinutes { get; set; }
}

/// <summary>Biên bản buổi học — chỉ gia sư xem, không gửi phụ huynh.</summary>
public class AppRecordingSessionMinutes
{
    /// <summary>2–4 câu tóm tắt buổi học.</summary>
    public string? Summary { get; set; }
    /// <summary>Các ý chính đã dạy/thảo luận.</summary>
    public List<string> KeyPoints { get; set; } = new();
    /// <summary>Việc gia sư cần làm/nhớ cho buổi sau.</summary>
    public List<string> FollowUps { get; set; } = new();
    /// <summary>Diễn biến buổi học theo thứ tự.</summary>
    public List<TutorMinutesSection> Sections { get; set; } = new();
    /// <summary>Các bài đã làm (theo dạng bài) và kết quả.</summary>
    public List<TutorMinutesExercise> Exercises { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public List<string> Difficulties { get; set; } = new();
    public List<string> UsefulNotes { get; set; } = new();
    public List<TutorTeachingNote> TeachingNotes { get; set; } = new();
}

/// <summary>Link nghe lại có hạn.</summary>
public class AppRecordingAudioUrlResponse
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
