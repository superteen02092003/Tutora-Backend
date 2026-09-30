using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MV.ApplicationLayer.Interfaces;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.DomainLayer.Configuration;
using MV.DomainLayer.Constants;
using MV.DomainLayer.DTO.RequestModel;
using MV.DomainLayer.DTO.ResponseModel;
using MV.DomainLayer.Entities;
using MV.DomainLayer.Exceptions;
using MV.DomainLayer.Helpers;

namespace MV.ApplicationLayer.Services;

/// <summary>
/// Xem <see cref="IRecorderParentLinkService"/>.
///
/// Xác minh khi phụ huynh đồng ý (không tin dữ liệu Mini App gửi lên):
///   1. accessToken (getAccessToken trong Mini App) → Graph API → ID theo Zalo App.
///   2. idByOA (getUserInfo) → OA user/detail → user_id_by_app phải trùng bước 1
///      và người dùng phải đang quan tâm OA.
/// Hai bước này chứng minh người đang mở Mini App đúng là chủ UID đó, nên người
/// khác cầm link chuyển tiếp không tự gắn UID của mình/ai khác vào được.
/// </summary>
public class RecorderParentLinkService(
    IAppDbContext db,
    IZaloAuthService zaloAuth,
    IZaloOAService zaloOA,
    INotificationService notificationService,
    IOptions<ZaloMiniAppSettings> miniAppOptions,
    IOptions<ZaloOAConfig> oaOptions,
    ILogger<RecorderParentLinkService> logger) : IRecorderParentLinkService
{
    private readonly ZaloMiniAppSettings _miniApp = miniAppOptions.Value;
    private readonly ZaloOAConfig _oa = oaOptions.Value;

    // ── Gia sư ───────────────────────────────────────────────────────────────

    public async Task<RecorderParentInviteResponse> CreateInviteAsync(Guid studentId, string tutorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_miniApp.MiniAppId))
            throw new RecorderNotReadyException("Chưa cấu hình Zalo Mini App (ZaloMiniApp:MiniAppId).");

        var student = await LoadStudentAsync(studentId, tutorId, ct);
        var now = TimeZoneHelper.UtcNow;

        // Mỗi học sinh chỉ có một link đang hiệu lực: link cũ bị thu hồi để link
        // từng gửi nhầm người không còn dùng được.
        var active = await db.RecorderParentInvites
            .Where(i => i.Studentid == studentId && i.Usedat == null && i.Revokedat == null)
            .ToListAsync(ct);
        foreach (var old in active) old.Revokedat = now;

        var invite = new RecorderParentInvite
        {
            Inviteid = Guid.NewGuid(),
            Studentid = studentId,
            Tutorid = tutorId,
            Token = NewToken(),
            Createdat = now,
            Expiresat = now.AddDays(RecorderParentInviteRules.ValidDays)
        };
        db.RecorderParentInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        var tutorName = await TutorNameAsync(tutorId, ct);
        var url = _miniApp.BuildUrl("", $"invite={invite.Token}");
        var greeting = string.IsNullOrWhiteSpace(student.Parentname) ? "Chào anh/chị" : $"Chào {student.Parentname}";
        var share =
            $"{greeting}, em là {tutorName}, gia sư của {student.Fullname}. " +
            "Sau mỗi buổi học em sẽ gửi báo cáo cho anh/chị qua Zalo (TopTutor). " +
            $"Anh/chị bấm vào link này để nhận báo cáo nhé: {url}";

        return new RecorderParentInviteResponse
        {
            InviteUrl = url,
            ExpiresAt = invite.Expiresat,
            ShareText = share
        };
    }

    public async Task UnlinkAsync(Guid studentId, string tutorId, CancellationToken ct = default)
    {
        var student = await LoadStudentAsync(studentId, tutorId, ct);
        if (student.Parentid == null) return;

        var now = TimeZoneHelper.UtcNow;
        db.RecorderConsentEvents.Add(new RecorderConsentEvent
        {
            Eventid = Guid.NewGuid(),
            Studentid = studentId,
            Parentid = student.Parentid,
            Action = RecorderConsentAction.Unlinked,
            Method = RecorderConsentMethod.Tutor,
            Createdat = now
        });
        student.Parentid = null;
        student.Parentlinkedat = null;
        if (student.Consentstatus == RecorderConsentStatus.ParentConfirmed)
        {
            student.Consentstatus = RecorderConsentStatus.Unknown;
            student.Consentat = null;
        }
        student.Updatedat = now;
        await db.SaveChangesAsync(ct);
    }

    // ── Mini App (phụ huynh) ─────────────────────────────────────────────────

    public async Task<RecorderParentInvitePreviewResponse> GetInvitePreviewAsync(string token, CancellationToken ct = default)
    {
        var (invite, student) = await LoadInviteAsync(token, ct);
        return new RecorderParentInvitePreviewResponse
        {
            Status = InviteStatus(invite),
            StudentName = student.Fullname,
            TutorName = await TutorNameAsync(invite.Tutorid, ct),
            Subject = student.Subject,
            Grade = student.Grade,
            ExpiresAt = invite.Expiresat,
            ConsentVersion = RecorderParentInviteRules.ConsentVersion,
            OaId = _oa.OAId ?? string.Empty
        };
    }

    public async Task<RecorderParentLinkResultResponse> AcceptInviteAsync(
        string token, RecorderParentLinkAcceptRequest request, string? ipAddress, string? userAgent,
        CancellationToken ct = default)
    {
        if (!request.Agreed)
            throw new RecorderZaloVerifyException("Anh/chị cần đồng ý để nhận báo cáo.");

        var (invite, student) = await LoadInviteAsync(token, ct);
        var status = InviteStatus(invite);
        if (status != "valid")
            throw new RecorderInviteInvalidException(status switch
            {
                "expired" => "Link mời đã hết hạn. Anh/chị nhờ gia sư gửi lại link mới nhé.",
                "used" => "Link mời này đã được dùng.",
                _ => "Link mời không còn hiệu lực. Anh/chị nhờ gia sư gửi lại link mới nhé."
            });

        var (uid, appUserId, displayName, isFollower) = await VerifyZaloAsync(request, ct);

        var now = TimeZoneHelper.UtcNow;
        var parent = await db.RecorderParents.FirstOrDefaultAsync(p => p.Zalouid == uid, ct);
        if (parent == null)
        {
            parent = new RecorderParent
            {
                Parentid = Guid.NewGuid(),
                Zalouid = uid,
                Createdat = now
            };
            db.RecorderParents.Add(parent);
        }
        parent.Zaloappuserid = appUserId ?? parent.Zaloappuserid;
        parent.Displayname = displayName ?? parent.Displayname;
        parent.Isfollower = isFollower;
        // Vừa quan tâm OA = một lần tương tác → mở cửa sổ Tin Tư vấn.
        parent.Lastinteractionat = now;
        parent.Updatedat = now;

        student.Parentid = parent.Parentid;
        student.Parentlinkedat = now;
        student.Consentstatus = RecorderConsentStatus.ParentConfirmed;
        student.Consentat = now;
        student.Updatedat = now;

        invite.Usedat = now;
        invite.Usedbyparentid = parent.Parentid;

        db.RecorderConsentEvents.Add(new RecorderConsentEvent
        {
            Eventid = Guid.NewGuid(),
            Studentid = student.Studentid,
            Parentid = parent.Parentid,
            Action = RecorderConsentAction.Linked,
            Method = RecorderConsentMethod.MiniAppFollow,
            Consentversion = Truncate(request.ConsentVersion ?? RecorderParentInviteRules.ConsentVersion, 30),
            Zalouid = uid,
            Ipaddress = Truncate(ipAddress, 64),
            Useragent = Truncate(userAgent, 500),
            Createdat = now
        });

        await db.SaveChangesAsync(ct);

        var tutorName = await TutorNameAsync(invite.Tutorid, ct);
        await NotifyTutorAsync(invite.Tutorid, student);

        return new RecorderParentLinkResultResponse
        {
            StudentName = student.Fullname,
            TutorName = tutorName,
            LinkedAt = now
        };
    }

    // ── Xác minh Zalo ────────────────────────────────────────────────────────

    private async Task<(string Uid, string? AppUserId, string? DisplayName, bool IsFollower)> VerifyZaloAsync(
        RecorderParentLinkAcceptRequest request, CancellationToken ct)
    {
        var claimedUid = request.IdByOA?.Trim() ?? string.Empty;
        if (claimedUid.Length == 0)
            throw new RecorderZaloVerifyException("Anh/chị cần bấm \"Quan tâm\" OA TopTutor để nhận báo cáo.");

        var appUserId = await zaloAuth.GetZaloAppUserIdAsync(request.AccessToken, _miniApp.SecretKey);
        var detail = await zaloOA.GetOAUserDetailAsync(claimedUid, ct);

        if (appUserId != null && detail != null)
        {
            if (!string.Equals(detail.UserIdByApp, appUserId, StringComparison.Ordinal))
            {
                // Hay gặp nhất khi Mini App và OA thuộc hai Zalo App khác nhau: hai ID
                // "theo app" khác nhau dù cùng một người. Ghi log để chẩn đoán khi test.
                logger.LogWarning(
                    "Parent link: user_id_by_app không khớp (graph={GraphId}, oa={OaAppId}, uid={Uid})",
                    appUserId, detail.UserIdByApp, claimedUid);
                throw new RecorderZaloVerifyException("Không xác minh được tài khoản Zalo. Anh/chị thử mở lại link nhé.");
            }
            if (!detail.IsFollower)
                throw new RecorderZaloVerifyException("Anh/chị cần bấm \"Quan tâm\" OA TopTutor để nhận báo cáo.");

            return (claimedUid, appUserId, detail.DisplayName, true);
        }

        if (_miniApp.AllowUnverifiedInDevelopment)
        {
            logger.LogWarning(
                "Parent link: BỎ QUA xác minh Zalo (AllowUnverifiedInDevelopment). graph={GraphOk}, oa={OaOk}",
                appUserId != null, detail != null);
            return (claimedUid, appUserId, detail?.DisplayName, detail?.IsFollower ?? true);
        }

        logger.LogWarning("Parent link: xác minh thất bại (graph={GraphOk}, oa={OaOk})", appUserId != null, detail != null);
        throw new RecorderZaloVerifyException("Không xác minh được tài khoản Zalo. Anh/chị thử mở lại link nhé.");
    }

    // ── nội bộ ────────────────────────────────────────────────────────────────

    private async Task<RecorderStudent> LoadStudentAsync(Guid studentId, string tutorId, CancellationToken ct) =>
        await db.RecorderStudents.FirstOrDefaultAsync(
            s => s.Studentid == studentId && s.Tutorid == tutorId && s.Archivedat == null, ct)
        ?? throw new RecorderNotFoundException("Không tìm thấy học sinh.");

    private async Task<(RecorderParentInvite Invite, RecorderStudent Student)> LoadInviteAsync(string token, CancellationToken ct)
    {
        var t = token?.Trim() ?? string.Empty;
        var invite = t.Length is < 20 or > 64 ? null
            : await db.RecorderParentInvites.FirstOrDefaultAsync(i => i.Token == t, ct);
        if (invite == null) throw new RecorderNotFoundException("Link mời không tồn tại.");

        var student = await db.RecorderStudents.FirstOrDefaultAsync(s => s.Studentid == invite.Studentid, ct);
        if (student == null || student.Archivedat != null)
            throw new RecorderNotFoundException("Link mời không còn hiệu lực.");
        return (invite, student);
    }

    private static string InviteStatus(RecorderParentInvite i) =>
        i.Usedat != null ? "used"
        : i.Revokedat != null ? "revoked"
        : i.Expiresat <= TimeZoneHelper.UtcNow ? "expired"
        : "valid";

    private async Task<string> TutorNameAsync(string tutorId, CancellationToken ct) =>
        await db.Users.Where(u => u.Userid == tutorId).Select(u => u.Fullname).FirstOrDefaultAsync(ct)
        ?? "Gia sư";

    private async Task NotifyTutorAsync(string tutorId, RecorderStudent student)
    {
        try
        {
            await notificationService.CreateNotificationAsync(new NotificationRequest
            {
                Userid = tutorId,
                Title = "Phụ huynh đã kết nối Zalo",
                Message = $"Phụ huynh của {student.Fullname} đã đồng ý nhận báo cáo qua Zalo.",
                Type = NotificationType.RecorderParentLinked,
                Referenceid = student.Studentid.ToString()
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không gửi được thông báo liên kết phụ huynh cho {TutorId}", tutorId);
        }
    }

    /// <summary>32 byte ngẫu nhiên, base64url (43 ký tự) — không đoán được.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? v, int max) =>
        string.IsNullOrEmpty(v) ? v : v.Length <= max ? v : v[..max];
}
