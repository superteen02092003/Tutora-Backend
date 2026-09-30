using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MV.ApplicationLayer.Common.Hubs;
using MV.ApplicationLayer.Helpers;
using MV.ApplicationLayer.Hubs;
using MV.ApplicationLayer.Interfaces;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.DomainLayer.Constants;
using MV.DomainLayer.DTO.RequestModel;
using MV.DomainLayer.DTO.ResponseModel;
using MV.DomainLayer.Entities;
using MV.DomainLayer.Helpers;

namespace MV.ApplicationLayer.Services;

public class SupportMessageService : ISupportMessageService
{
    private readonly IAppDbContext _context;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly IFileStorageService _storageService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<SupportMessageService> _logger;
    private const string SupportImageBucket = "support-images";

    public SupportMessageService(
        IAppDbContext context,
        IHubContext<NotificationHub> hubContext,
        IFileStorageService storageService,
        INotificationService notificationService,
        ILogger<SupportMessageService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _storageService = storageService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<List<SupportThreadSummaryResponse>> GetThreadsForAdminAsync(string? role, string? search)
    {
        var query = _context.Supportthreads.Include(t => t.User).AsQueryable();

        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(t => t.User!.Primaryrole == role);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILike (Npgsql.EntityFrameworkCore.PostgreSQL) isn't referenced from this layer;
            // ToLower().Contains() is provider-portable and still translates fine on Postgres.
            var term = search.Trim().ToLower();
            query = query.Where(t =>
                (t.User!.Fullname != null && t.User.Fullname.ToLower().Contains(term)) ||
                (t.User.Phone != null && t.User.Phone.ToLower().Contains(term)));
        }

        var threads = await query
            .OrderByDescending(t => t.Lastmessageat)
            .ThenByDescending(t => t.Createdat)
            .ToListAsync();

        var threadIds = threads.Select(t => t.Supportthreadid).ToList();
        var lastMessages = await _context.Supportmessages
            .Where(m => threadIds.Contains(m.Supportthreadid))
            .GroupBy(m => m.Supportthreadid)
            .Select(g => g.OrderByDescending(m => m.Createdat).ThenByDescending(m => m.Supportmessageid).First())
            .ToListAsync();
        var lastMessageByThread = lastMessages.ToDictionary(m => m.Supportthreadid);

        return threads.Select(t =>
        {
            lastMessageByThread.TryGetValue(t.Supportthreadid, out var last);
            return new SupportThreadSummaryResponse
            {
                SupportThreadId = t.Supportthreadid,
                UserId = t.Userid,
                UserName = t.User?.Fullname,
                UserAvatarUrl = t.User?.Avatarurl,
                UserRole = t.User?.Primaryrole,
                LastMessage = last?.Message,
                LastMessageAt = t.Lastmessageat,
                LastMessageFromAdmin = last?.Senderside == SupportSenderSide.Admin,
                UnreadCount = t.Unreadforadmin,
            };
        }).ToList();
    }

    public async Task<SupportThreadDetailResponse?> GetOrCreateThreadForAdminAsync(string userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Userid == userId);
        if (user == null) return null;

        var thread = await GetOrCreateThreadEntityAsync(userId);
        if (thread.Unreadforadmin != 0)
        {
            thread.Unreadforadmin = 0;
            await _context.SaveChangesAsync();
        }

        return await BuildDetailResponseAsync(thread, user);
    }

    public async Task<SupportMessageItemResponse?> SendMessageAsAdminAsync(string userId, string adminSenderId, string message)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Userid == userId);
        if (user == null) return null;

        var thread = await GetOrCreateThreadEntityAsync(userId);
        var sender = await _context.Users.FirstOrDefaultAsync(u => u.Userid == adminSenderId);

        var entity = await AppendMessageAsync(thread, adminSenderId, SupportSenderSide.Admin, message);
        thread.Unreadforuser += 1;
        thread.Unreadforadmin = 0; // gửi tin nghĩa là admin đã xem hết hội thoại tới thời điểm này
        await _context.SaveChangesAsync();

        var response = ToMessageResponse(entity, sender?.Fullname);
        await BroadcastMessageAsync(thread, response);
        await NotifyRecipientsAsync(thread, response, adminSenderId);
        return response;
    }

    public async Task<SupportMessageItemResponse?> SendImageAsAdminAsync(string userId, string adminSenderId, IFormFile file)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Userid == userId);
        if (user == null) return null;

        var thread = await GetOrCreateThreadEntityAsync(userId);
        var sender = await _context.Users.FirstOrDefaultAsync(u => u.Userid == adminSenderId);

        await _storageService.EnsureBucketExistsAsync(SupportImageBucket);
        var imageUrl = await _storageService.UploadFileAsync(SupportImageBucket, adminSenderId, file);

        var entity = await AppendMessageAsync(thread, adminSenderId, SupportSenderSide.Admin, imageUrl, ChatMessageType.Image);
        thread.Unreadforuser += 1;
        thread.Unreadforadmin = 0;
        await _context.SaveChangesAsync();

        var response = ToMessageResponse(entity, sender?.Fullname);
        await BroadcastMessageAsync(thread, response);
        await NotifyRecipientsAsync(thread, response, adminSenderId);
        return response;
    }

    public async Task<SupportThreadDetailResponse?> GetThreadForUserAsync(string userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Userid == userId);
        if (user == null) return null;

        var thread = await _context.Supportthreads
            .Include(t => t.Supportmessages)
            .FirstOrDefaultAsync(t => t.Userid == userId);
        if (thread == null) return null;

        if (thread.Unreadforuser != 0)
        {
            thread.Unreadforuser = 0;
            await _context.SaveChangesAsync();
        }

        return await BuildDetailResponseAsync(thread, user);
    }

    public async Task<SupportMessageItemResponse> SendMessageAsUserAsync(string userId, string message)
    {
        var thread = await GetOrCreateThreadEntityAsync(userId);
        var entity = await AppendMessageAsync(thread, userId, SupportSenderSide.User, message);
        thread.Unreadforadmin += 1;
        thread.Unreadforuser = 0;
        await _context.SaveChangesAsync();

        var response = ToMessageResponse(entity, senderName: null);
        await BroadcastMessageAsync(thread, response);
        await NotifyRecipientsAsync(thread, response);
        return response;
    }

    public async Task<SupportMessageItemResponse> SendImageAsUserAsync(string userId, IFormFile file)
    {
        var thread = await GetOrCreateThreadEntityAsync(userId);

        await _storageService.EnsureBucketExistsAsync(SupportImageBucket);
        var imageUrl = await _storageService.UploadFileAsync(SupportImageBucket, userId, file);

        var entity = await AppendMessageAsync(thread, userId, SupportSenderSide.User, imageUrl, ChatMessageType.Image);
        thread.Unreadforadmin += 1;
        thread.Unreadforuser = 0;
        await _context.SaveChangesAsync();

        var response = ToMessageResponse(entity, senderName: null);
        await BroadcastMessageAsync(thread, response);
        await NotifyRecipientsAsync(thread, response);
        return response;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Pushes a new message live to whichever side wasn't the one calling the API —
    /// the recipient's own personal group, and the shared support-admins group so any CMS inbox
    /// currently open picks it up too (mirrors DisputeService's NotifyAndBroadcastDisputeMessageAsync).</summary>
    private async Task BroadcastMessageAsync(Supportthread thread, SupportMessageItemResponse message)
    {
        var payload = new SupportMessageBroadcast
        {
            UserId = thread.Userid,
            SupportThreadId = thread.Supportthreadid,
            Message = message,
        };

        try
        {
            await _hubContext.Clients.Group($"user:{thread.Userid}").SendAsync("supportMessageReceived", payload);
            await _hubContext.Clients.Group(BaseHub.SupportAdminsGroup).SendAsync("supportMessageReceived", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast support message for thread {ThreadId}", thread.Supportthreadid);
        }
    }

    /// <summary>
    /// Persist notification cho phía nhận để badge/header hoạt động cả khi họ không mở inbox.
    /// Không báo lại cho Admin vừa gửi; các Admin khác vẫn được nhận để không bỏ sót yêu cầu.
    /// </summary>
    private async Task NotifyRecipientsAsync(
        Supportthread thread,
        SupportMessageItemResponse message,
        string? senderAdminId = null)
    {
        try
        {
            var preview = message.MessageType == ChatMessageType.Image
                ? "📷 Hình ảnh"
                : string.IsNullOrWhiteSpace(message.Message)
                    ? "Bạn có tin nhắn mới trong hội thoại hỗ trợ."
                    : message.Message.Length > 120 ? $"{message.Message[..120]}..." : message.Message;

            if (message.SenderSide == SupportSenderSide.Admin)
            {
                await _notificationService.CreateNotificationAsync(new NotificationRequest
                {
                    Userid = thread.Userid,
                    Title = "Phản hồi từ TopTutor",
                    Message = preview,
                    Type = NotificationType.SupportMessage,
                    Referenceid = thread.Supportthreadid.ToString(),
                });
                return;
            }

            var recipientIds = await PermissionRecipients.ResolveAsync(
                _context, Permissions.SupportView, excludeUserId: senderAdminId);

            if (recipientIds.Count == 0) return;
            await _notificationService.CreateNotificationsAsync(recipientIds.Select(adminId => new NotificationRequest
            {
                Userid = adminId,
                Title = "Yêu cầu hỗ trợ mới",
                Message = preview,
                Type = NotificationType.SupportMessage,
                Referenceid = thread.Supportthreadid.ToString(),
            }));
        }
        catch (Exception ex)
        {
            // Notification không được làm hỏng thao tác gửi tin nhắn đã lưu thành công.
            _logger.LogWarning(ex, "Failed to create support notification for thread {ThreadId}", thread.Supportthreadid);
        }
    }

    private async Task<Supportthread> GetOrCreateThreadEntityAsync(string userId)
    {
        var thread = await _context.Supportthreads.FirstOrDefaultAsync(t => t.Userid == userId);
        if (thread != null) return thread;

        thread = new Supportthread { Userid = userId, Createdat = TimeZoneHelper.UtcNow };
        _context.Supportthreads.Add(thread);
        await _context.SaveChangesAsync();
        return thread;
    }

    private async Task<Supportmessage> AppendMessageAsync(
        Supportthread thread, string senderId, string senderSide, string message, string messageType = ChatMessageType.Text)
    {
        var now = TimeZoneHelper.UtcNow;
        var entity = new Supportmessage
        {
            Supportthreadid = thread.Supportthreadid,
            Senderid = senderId,
            Senderside = senderSide,
            Messagetype = messageType,
            Message = messageType == ChatMessageType.Image ? message : message.Trim(),
            Createdat = now,
        };
        _context.Supportmessages.Add(entity);
        thread.Lastmessageat = now;
        return entity;
    }

    private async Task<SupportThreadDetailResponse> BuildDetailResponseAsync(Supportthread thread, User user)
    {
        var messages = await _context.Supportmessages
            .Where(m => m.Supportthreadid == thread.Supportthreadid)
            .OrderBy(m => m.Createdat)
            .ThenBy(m => m.Supportmessageid)
            .ToListAsync();

        var senderIds = messages.Where(m => m.Senderside == SupportSenderSide.Admin && m.Senderid != null)
            .Select(m => m.Senderid!).Distinct().ToList();
        var senderNames = senderIds.Count == 0
            ? new Dictionary<string, string?>()
            : await _context.Users.Where(u => senderIds.Contains(u.Userid))
                .ToDictionaryAsync(u => u.Userid, u => u.Fullname);

        return new SupportThreadDetailResponse
        {
            SupportThreadId = thread.Supportthreadid,
            UserId = user.Userid,
            UserName = user.Fullname,
            UserAvatarUrl = user.Avatarurl,
            UserRole = user.Primaryrole,
            UserPhone = user.Phone,
            UserEmail = user.Email,
            Messages = messages.Select(m => ToMessageResponse(
                m,
                m.Senderside == SupportSenderSide.Admin && m.Senderid != null
                    ? senderNames.GetValueOrDefault(m.Senderid)
                    : null)).ToList(),
        };
    }

    private static SupportMessageItemResponse ToMessageResponse(Supportmessage entity, string? senderName) => new()
    {
        SupportMessageId = entity.Supportmessageid,
        SenderId = entity.Senderid,
        SenderSide = entity.Senderside,
        SenderName = entity.Senderside == SupportSenderSide.Admin ? (senderName ?? "Admin") : senderName,
        MessageType = entity.Messagetype,
        Message = entity.Message,
        CreatedAt = entity.Createdat,
    };
}
