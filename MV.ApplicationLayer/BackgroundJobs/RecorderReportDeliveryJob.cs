using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MV.ApplicationLayer.Interfaces;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.ApplicationLayer.Services;
using MV.DomainLayer.Constants;
using MV.DomainLayer.DTO.RequestModel;
using MV.DomainLayer.Entities;
using MV.DomainLayer.Helpers;
using System.Text.RegularExpressions;

namespace MV.ApplicationLayer.BackgroundJobs;

/// <summary>
/// Gửi báo cáo buổi học (ghi âm từ app, học sinh NGOÀI nền tảng) tới Zalo phụ huynh qua
/// ZBS Template Message theo SĐT. AppRecordingService.ApproveAsync chỉ đánh dấu
/// delivery_channel = zns + delivery_status = pending; job này quét các dòng đó mỗi phút.
///
/// - Chưa cấu hình template (Zalo chưa duyệt mẫu) → bỏ qua, giữ pending để gửi bù khi có.
/// - 21:30–06:30 giờ VN không gửi: Zalo chặn mẫu ban đêm (-133, 22h–6h), chừa thêm 30 phút
///   hai đầu cho chắc và cho phụ huynh khỏi bị làm phiền.
/// - Lỗi tạm thời (mạng/5xx/-133) → thử lại lượt sau, quá 24h kể từ lúc duyệt thì thôi.
/// - Lỗi phía người nhận (không dùng Zalo, chặn OA...) → failed + báo gia sư tự gửi qua Zalo.
/// </summary>
public class RecorderReportDeliveryJob(IServiceProvider sp, ILogger<RecorderReportDeliveryJob> logger)
    : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 20;

    /// <summary>Quá hạn này kể từ lúc duyệt mà vẫn chưa gửi được thì chuyển failed để gia sư tự gửi.</summary>
    private static readonly TimeSpan MaxRetryAge = TimeSpan.FromHours(24);

    private static readonly TimeSpan QuietStart = new(21, 30, 0);
    private static readonly TimeSpan QuietEnd = new(6, 30, 0);

    // Giới hạn độ dài tham số template của Zalo.
    private const int ShortParamMax = 30;   // tên, môn, ngày
    private const int TableValueMax = 200;  // content/homework/note (template 640496: loại 200 ký tự)
    private const string EmptyValue = "Không có";

    /// <summary>Lỗi được thử lại lượt sau: -133 ngoài giờ gửi, -124 token hỏng (job refresh
    /// token sẽ sửa) — đều là vấn đề phía hệ thống, không phải phía phụ huynh.</summary>
    private static readonly HashSet<int> RetryableCodes = [-133, -124];

    // Chỉ cảnh báo "chưa cấu hình template" mỗi giờ một lần, tránh ngập log (job chạy mỗi phút).
    private DateTime _lastMissingTemplateWarning = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Đợi app khởi động xong (migration, Hangfire) rồi mới quét.
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi khi quét báo cáo buổi học chờ gửi Zalo.");
            }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var nowUtc = TimeZoneHelper.UtcNow;
        var vnNow = TimeZoneHelper.ToVietnamTime(nowUtc);
        if (IsQuietHours(vnNow.TimeOfDay)) return;

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var templateId = scope.ServiceProvider.GetRequiredService<IOptions<ZaloOAConfig>>().Value.ZbsTemplateRecorderReport;

        var pending = await db.RecorderLessons
            .Include(l => l.Student)
            .Where(l => l.Deliverychannel == RecorderDeliveryChannel.Zns
                && l.Deliverystatus == RecorderDeliveryStatus.Pending)
            .OrderBy(l => l.Approvedat)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        if (string.IsNullOrWhiteSpace(templateId))
        {
            if (nowUtc - _lastMissingTemplateWarning > TimeSpan.FromHours(1))
            {
                _lastMissingTemplateWarning = nowUtc;
                logger.LogWarning(
                    "Có {Count}+ báo cáo chờ gửi Zalo nhưng chưa cấu hình ZaloOA:ZbsTemplateRecorderReport — giữ pending.",
                    pending.Count);
            }
            return;
        }

        var zalo = scope.ServiceProvider.GetRequiredService<IZaloOAService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var tutorIds = pending.Select(l => l.Tutorid).Distinct().ToList();
        var tutorNames = await db.Users
            .Where(u => tutorIds.Contains(u.Userid))
            .Select(u => new { u.Userid, u.Fullname })
            .ToDictionaryAsync(u => u.Userid, u => u.Fullname, ct);

        foreach (var lesson in pending)
        {
            try
            {
                tutorNames.TryGetValue(lesson.Tutorid, out var tutorName);
                await DeliverAsync(db, zalo, notifications, lesson, templateId, tutorName, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lỗi không lường trước (DB...) — giữ pending, lượt sau thử lại; không chặn các buổi khác.
                logger.LogWarning(ex, "Chưa gửi được báo cáo buổi {LessonId} tới Zalo, lượt sau thử lại.", lesson.Lessonid);
            }
        }
    }

    private async Task DeliverAsync(
        IAppDbContext db,
        IZaloOAService zalo,
        INotificationService notifications,
        RecorderLesson lesson,
        string templateId,
        string? tutorName,
        CancellationToken ct)
    {
        var student = lesson.Student;

        // ── Điều kiện gửi ────────────────────────────────────────────────────
        string? precondition = null;
        if (student == null)
            precondition = "Buổi học không gắn với học sinh nào.";
        else if (string.IsNullOrWhiteSpace(student.Parentphone))
            precondition = "Học sinh chưa có số điện thoại phụ huynh.";
        else if (student.Consentstatus != RecorderConsentStatus.TutorConfirmed
                 && student.Consentstatus != RecorderConsentStatus.ParentConfirmed)
            precondition = "Phụ huynh chưa đồng ý nhận báo cáo qua Zalo.";

        if (precondition != null)
        {
            await MarkFailedAsync(db, notifications, lesson, student, precondition, ct);
            return;
        }

        // ── Gửi ──────────────────────────────────────────────────────────────
        var data = BuildTemplateData(lesson, student!, tutorName);
        var result = await zalo.SendZbsTemplateByPhoneAsync(student!.Parentphone!, templateId, data, ct);
        var now = TimeZoneHelper.UtcNow;

        if (result.Success)
        {
            lesson.Deliverystatus = RecorderDeliveryStatus.Sent;
            lesson.Sentat = now;
            lesson.Deliveryerror = null;
            lesson.Updatedat = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Đã gửi báo cáo buổi {LessonId} tới Zalo phụ huynh (msg_id={MsgId}).",
                lesson.Lessonid, result.MessageId);
            return;
        }

        var retryable = result.IsTransient || (result.ErrorCode is int c && RetryableCodes.Contains(c));
        var approvedAt = lesson.Approvedat ?? lesson.Updatedat;
        if (retryable && now - approvedAt < MaxRetryAge)
        {
            // Giữ pending, lượt sau thử lại. Ghi lại lỗi gần nhất để dễ tra khi cần.
            lesson.Deliveryerror = DescribeError(result.ErrorCode, result.Error);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Báo cáo buổi {LessonId} chưa gửi được (lỗi tạm thời {Code}: {Error}), thử lại sau.",
                lesson.Lessonid, result.ErrorCode, result.Error);
            return;
        }

        var reason = retryable
            ? $"Quá 24 giờ vẫn chưa gửi được. Lỗi gần nhất: {DescribeError(result.ErrorCode, result.Error)}"
            : DescribeError(result.ErrorCode, result.Error);
        await MarkFailedAsync(db, notifications, lesson, student, reason, ct);
    }

    private async Task MarkFailedAsync(
        IAppDbContext db,
        INotificationService notifications,
        RecorderLesson lesson,
        RecorderStudent? student,
        string reason,
        CancellationToken ct)
    {
        var now = TimeZoneHelper.UtcNow;
        lesson.Deliverystatus = RecorderDeliveryStatus.Failed;
        lesson.Deliveryerror = reason;
        lesson.Updatedat = now;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Không gửi được báo cáo buổi {LessonId} tới Zalo phụ huynh: {Reason}", lesson.Lessonid, reason);

        // Thông báo lỗi không được làm hỏng trạng thái đã lưu ở trên.
        try
        {
            var who = student != null ? $"của {student.Fullname} " : "";
            await notifications.CreateNotificationAsync(new NotificationRequest
            {
                Userid = lesson.Tutorid,
                Title = "Chưa gửi được báo cáo cho phụ huynh",
                Message = $"Báo cáo buổi học {who}chưa gửi được qua Zalo: {reason} "
                          + "Bạn hãy kiểm tra lại số điện thoại phụ huynh trong hồ sơ học sinh.",
                Type = NotificationType.RecorderReportDeliveryFailed,
                Referenceid = lesson.Lessonid.ToString()
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Không gửi được thông báo lỗi gửi báo cáo cho gia sư {TutorId}", lesson.Tutorid);
        }
    }

    private static bool IsQuietHours(TimeSpan vnTimeOfDay)
        => vnTimeOfDay >= QuietStart || vnTimeOfDay < QuietEnd;

    private static Dictionary<string, string> BuildTemplateData(
        RecorderLesson lesson, RecorderStudent student, string? tutorName)
    {
        // Ngày buổi học theo giờ VN: ưu tiên giờ dự kiến, rồi giờ bắt đầu ghi thực tế.
        var lessonUtc = lesson.Scheduledstart ?? lesson.Startedat ?? lesson.Approvedat ?? lesson.Createdat;
        var lessonDate = TimeZoneHelper.ToVietnamTime(lessonUtc).ToString("dd/MM/yyyy");

        return new Dictionary<string, string>
        {
            ["parent_name"] = Fit(student.Parentname, ShortParamMax, "Quý phụ huynh"),
            ["student_name"] = Fit(student.Fullname, ShortParamMax),
            ["tutor_name"] = Fit(tutorName, ShortParamMax),
            ["subject"] = Fit(lesson.Subject ?? student.Subject, ShortParamMax),
            ["lesson_date"] = Fit(lessonDate, ShortParamMax),
            // Ưu tiên bản tóm tắt ngắn cho Zalo (≤ 200 ký tự, câu trọn ý); bản ghi cũ chưa có thì
            // fallback về báo cáo đầy đủ đã bỏ markdown rồi cắt ngắn.
            ["content"] = TableValue(lesson.Zalocontent, lesson.Reportcontent),
            ["homework"] = TableValue(lesson.Zalohomework, lesson.Reporthomework),
            ["note"] = TableValue(lesson.Zalonotes, lesson.Reportnotes),
        };
    }

    private static string TableValue(string? zaloSummary, string? fullText)
        => zaloSummary is not null
            ? Fit(zaloSummary, TableValueMax)
            : Fit(StripMarkdown(fullText), TableValueMax);

    private static readonly Regex ListMarker = new(@"^\s*(?:[-*+•]|\d+[.)])\s+", RegexOptions.Multiline);

    /// <summary>
    /// Bỏ ký hiệu markdown/LaTeX thường gặp trong báo cáo AI (tiêu đề #, in đậm *, gạch đầu dòng, $...$,
    /// backtick) — tin Zalo hiển thị văn bản thuần nên các ký tự này chỉ làm rối và tốn chỗ.
    /// </summary>
    private static string? StripMarkdown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var text = ListMarker.Replace(value, string.Empty);
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '#' or '*' or '$' or '`') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Gộp xuống dòng/khoảng trắng thừa (tham số template là một dòng) rồi cắt theo giới hạn
    /// của Zalo, thêm "…" khi bị cắt — Zalo từ chối cả tin nếu tham số vượt độ dài.
    /// </summary>
    private static string Fit(string? value, int max, string empty = EmptyValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return empty;
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= max) return text;
        return text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Lỗi Zalo → câu tiếng Việt cho gia sư đọc hiểu, kèm mã để tra cứu.</summary>
    private static string DescribeError(int? code, string? raw)
    {
        var text = code switch
        {
            -108 => "Số điện thoại phụ huynh không hợp lệ",
            -118 => "Số điện thoại chưa dùng Zalo hoặc lâu không hoạt động",
            -119 => "Tài khoản Zalo của phụ huynh không nhận được tin",
            -110 => "Phiên bản Zalo của phụ huynh quá cũ",
            -114 => "Zalo của phụ huynh không hoạt động hoặc đã từ chối nhận tin",
            -133 => "Ngoài khung giờ Zalo cho phép gửi",
            -134 => "Phụ huynh chưa phản hồi yêu cầu nhận tin",
            -139 => "Phụ huynh đã từ chối nhận loại tin này",
            -141 => "Phụ huynh đã chặn mọi tin từ TopTutor",
            -144 => "TopTutor đã hết lượt gửi tin trong ngày",
            -124 => "Lỗi xác thực với Zalo",
            -131 => "Mẫu tin chưa được Zalo duyệt",
            null => string.IsNullOrWhiteSpace(raw) ? "Lỗi kết nối tới Zalo" : $"Lỗi kết nối tới Zalo: {raw}",
            _ => string.IsNullOrWhiteSpace(raw) ? "Zalo từ chối gửi tin" : $"Zalo từ chối gửi tin: {raw}"
        };
        return code is int c ? $"{text} ({c})." : $"{text}.";
    }
}
