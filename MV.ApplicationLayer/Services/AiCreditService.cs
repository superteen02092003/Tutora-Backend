using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MV.ApplicationLayer.Helpers;
using MV.ApplicationLayer.Interfaces;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.DomainLayer.Configuration;
using MV.DomainLayer.Constants;
using MV.DomainLayer.DTO.RequestModel;
using MV.DomainLayer.DTO.ResponseModel;
using MV.DomainLayer.Entities;
using MV.DomainLayer.Exceptions;
using MV.DomainLayer.Helpers;
using PayOS;

namespace MV.ApplicationLayer.Services;

public class AiCreditService(
    IAppDbContext context,
    IOptions<PaymentSettings> paymentSettings,
    [FromKeyedServices(ServiceKeys.PayOS.Checkout)] PayOSClient payOS,
    IFileStorageService fileStorage,
    ILogger<AiCreditService> logger) : IAiCreditService
{
    private readonly PayOSLinkFactory _linkFactory = new(
        payOS,
        paymentSettings.Value.ReturnUrl,
        paymentSettings.Value.CancelUrl);

    // Ledger core
    public async Task<int> GrantAsync(
        string userId, int amount, string source, string? referenceId, string? description,
        CancellationToken ct = default)
        => await GrantAsync(userId, amount, source, referenceId, description, null, ct);

    /// <summary>
    /// Cấp credit kèm LÔ có hạn dùng.
    /// </summary>
    public async Task<int> GrantAsync(
        string userId, int amount, string source, string? referenceId, string? description,
        string? batchSource, CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new BookingException(AiCreditErrorCodes.InvalidAmount, "Số credit cấp phải > 0.", 400);

        var balance = await ApplyDeltaAsync(userId, amount, source, referenceId, description, ct);

        if (batchSource is not null)
        {
            var months = await GetExpiryMonthsAsync(ct);
            context.AiCreditBatches.Add(new AiCreditBatch
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Source = batchSource,
                ReferenceId = referenceId,
                Granted = amount,
                GrantedAt = TimeZoneHelper.UtcNow,
                // months <= 0 -> admin tắt hết hạn.
                ExpiresAt = months > 0 ? TimeZoneHelper.UtcNow.AddMonths(months) : null,
            });
            await context.SaveChangesAsync(ct);
        }

        return balance;
    }

    /// <summary>Số tháng hết hạn — admin chỉnh trong CMS, mặc định 3 nếu chưa cấu hình.</summary>
    public async Task<int> GetExpiryMonthsAsync(CancellationToken ct = default)
    {
        var cfg = await context.Systemconfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Configkey == AiCreditConfigKeys.ExpiryMonths, ct);
        return int.TryParse(cfg?.Configvalue, out var v) ? v : 3;
    }

    /// <summary>
    /// Tiêu credit
    /// </summary>
    public async Task<int> SpendAsync(
        string userId, int amount, string? referenceId, string? description,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new BookingException(AiCreditErrorCodes.InvalidAmount, "Số credit tiêu phải > 0.", 400);

        // Dọn lô quá hạn TRƯỚC khi trừ: học sinh không được tiêu lượt đã hết hạn.
        await ExpireOverdueBatchesAsync(userId, ct);

        var affected = await context.Users
            .Where(u => u.Userid == userId && u.AiCreditsBalance >= amount)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AiCreditsBalance, u => u.AiCreditsBalance - amount), ct);

        // Ghi vào lô SẮP HẾT HẠN TRƯỚC (FIFO theo expires_at) để không phí lượt còn hạn dài.
        if (affected > 0)
            await ConsumeBatchesAsync(userId, amount, ct);

        if (affected == 0)
        {
            // Không trừ được: hoặc không đủ credit, hoặc user không tồn tại.
            var exists = await context.Users.AsNoTracking().AnyAsync(u => u.Userid == userId, ct);
            if (!exists)
                throw new BookingException(AiCreditErrorCodes.UserNotFound, "Không tìm thấy tài khoản.", 404);
            throw new BookingException(AiCreditErrorCodes.InsufficientCredits, "Số lượt sử dụng đã hết.", 400);
        }

        await RecordMonthlyUsageAsync(userId, amount, ct);

        return await context.Users.AsNoTracking()
            .Where(u => u.Userid == userId).Select(u => u.AiCreditsBalance).FirstAsync(ct);
    }

    /// <summary>
    /// Cộng dồn lượt dùng vào ô (tài khoản, tháng hiện tại)
    /// </summary>
    private async Task RecordMonthlyUsageAsync(string userId, int amount, CancellationToken ct)
    {
        try
        {
            var now = TimeZoneHelper.UtcNow;
            var period = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ai_usage_monthly (user_id, period, used_count, updated_at)
                VALUES ({userId}, {period}::date, {amount}, {now})
                ON CONFLICT (user_id, period)
                DO UPDATE SET used_count = ai_usage_monthly.used_count + {amount},
                              updated_at = {now};
                """, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Không ghi được thống kê lượt dùng AI cho {UserId} — số dư đã trừ đúng, chỉ thiếu số liệu báo cáo.",
                userId);
        }
    }

    /// <summary>Áp một delta (dương = cấp, âm = tiêu) lên số dư của tài khoản trong 1 transaction serializable:
    /// khóa user, kiểm tra đủ credit (nếu âm), ghi ledger, cập nhật cache balance.</summary>
    private async Task<int> ApplyDeltaAsync(
        string userId, int delta, string source, string? referenceId, string? description,
        CancellationToken ct)
    {
        await using var tx = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var user = await context.Users
                .FirstOrDefaultAsync(u => u.Userid == userId, ct)
                ?? throw new BookingException(AiCreditErrorCodes.UserNotFound, "Không tìm thấy tài khoản.", 404);

            var newBalance = user.AiCreditsBalance + delta;
            if (newBalance < 0)
                throw new BookingException(
                    AiCreditErrorCodes.InsufficientCredits,
                    "Số lượt sử dụng đã hết.", 400);

            user.AiCreditsBalance = newBalance;

            context.AiCreditTransactions.Add(new AiCreditTransaction
            {
                Userid = userId,
                Amount = delta,
                Balanceafter = newBalance,
                Source = source,
                Referenceid = referenceId,
                Description = description,
                Createdat = TimeZoneHelper.UtcNow
            });

            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return newBalance;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task GrantFreePackageAsync(string userId, CancellationToken ct = default)
    {
        // Số lượt tặng lấy từ config (admin chỉnh), rơi về package Free nếu chưa cấu hình.
        var cfg = await context.Systemconfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Configkey == AiCreditConfigKeys.FreeOnSignup, ct);

        int amount;
        if (int.TryParse(cfg?.Configvalue, out var configured) && configured > 0)
        {
            amount = configured;
        }
        else
        {
            var freePkg = await context.AiCreditPackages
                .FirstOrDefaultAsync(p => p.Code == AiCreditPackageCode.Free && p.Isactive, ct);
            if (freePkg is null || freePkg.Creditamount <= 0)
            {
                logger.LogWarning("Chưa cấu hình credit tặng và package Free cũng không có; bỏ qua user {UserId}", userId);
                return;
            }
            amount = freePkg.Creditamount;
        }

        var refId = $"free:{userId}";
        if (await ReferenceAlreadyGrantedAsync(userId, AiCreditSource.Grant, refId, ct))
            return;

        await GrantAsync(userId, amount, AiCreditSource.Grant, refId,
            "Tặng credit khi xác thực số điện thoại.", AiCreditBatchSource.FreeSignup, ct);
    }

    public async Task GrantBookingBonusAsync(string userId, int bookingId, CancellationToken ct = default)
    {
        var bonus = await AdminGetBookingBonusAsync(ct);
        if (bonus <= 0) return;

        var refId = $"booking:{bookingId}";
        if (await ReferenceAlreadyGrantedAsync(userId, AiCreditSource.Grant, refId, ct))
            return;

        await GrantAsync(userId, bonus, AiCreditSource.Grant, refId,
            $"Tặng credit khi thanh toán booking #{bookingId}.", AiCreditBatchSource.BookingBonus, ct);
    }

    private Task<bool> ReferenceAlreadyGrantedAsync(string userId, string source, string referenceId, CancellationToken ct)
        => context.AiCreditTransactions.AnyAsync(
            t => t.Userid == userId && t.Source == source && t.Referenceid == referenceId, ct);

    // Query

    public async Task<AiCreditBalanceResponse> GetBalanceAsync(string userId, CancellationToken ct = default)
    {
        // Dọn lô quá hạn trước khi trả số — học sinh phải thấy con số dùng được thật.
        await ExpireOverdueBatchesAsync(userId, ct);

        var balance = await context.Users.AsNoTracking()
            .Where(u => u.Userid == userId)
            .Select(u => (int?)u.AiCreditsBalance)
            .FirstOrDefaultAsync(ct)
            ?? throw new BookingException(AiCreditErrorCodes.UserNotFound, "Không tìm thấy tài khoản.", 404);

        // Lô sắp hết hạn nhất, để UI nhắc trước khi mất lượt.
        var next = await context.AiCreditBatches.AsNoTracking()
            .Where(b => b.UserId == userId && b.Consumed < b.Granted && b.ExpiresAt != null)
            .OrderBy(b => b.ExpiresAt)
            .Select(b => new { b.ExpiresAt, Remaining = b.Granted - b.Consumed })
            .FirstOrDefaultAsync(ct);

        return new AiCreditBalanceResponse
        {
            Balance = balance,
            NextExpiryAt = next?.ExpiresAt,
            ExpiringAmount = next?.Remaining ?? 0,
        };
    }

    public async Task<IReadOnlyList<AiCreditTransactionResponse>> GetHistoryAsync(
        string userId, int take, CancellationToken ct = default)
    {
        take = take is <= 0 or > 200 ? 50 : take;
        return await context.AiCreditTransactions
            .AsNoTracking()
            .Where(t => t.Userid == userId)
            .OrderByDescending(t => t.Createdat)
            .Take(take)
            .Select(t => new AiCreditTransactionResponse
            {
                TransactionId = t.Transactionid,
                Amount = t.Amount,
                BalanceAfter = t.Balanceafter,
                Source = t.Source,
                ReferenceId = t.Referenceid,
                Description = t.Description,
                CreatedAt = t.Createdat
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AiCreditPackageResponse>> GetActivePackagesAsync(CancellationToken ct = default)
    {
        var packages = await context.AiCreditPackages
            .AsNoTracking()
            .Where(p => p.Isactive)
            .OrderBy(p => p.Sortorder).ThenBy(p => p.Packageid)
            .Select(p => ToPackageResponse(p))
            .ToListAsync(ct);

        // Hạn dùng là cấu hình chung, không phải thuộc tính của từng gói — nhưng gắn vào
        // response để FE hiện được ngay trên thẻ giá, người mua biết trước khi trả tiền.
        var months = await GetExpiryMonthsAsync(ct);
        foreach (var p in packages) p.ExpiryMonths = months;

        return packages;
    }

    // Purchase

    public async Task<AiCreditPurchaseResponse> InitiatePurchaseAsync(
        string buyerUserId, AiCreditPurchaseRequest request, CancellationToken ct = default)
    {
        var package = await context.AiCreditPackages
            .FirstOrDefaultAsync(p => p.Packageid == request.PackageId, ct)
            ?? throw new BookingException(AiCreditErrorCodes.PackageNotFound, "Không tìm thấy gói.", 404);

        if (!package.Isactive || !package.Ispurchasable)
            throw new BookingException(AiCreditErrorCodes.PackageNotPurchasable, "Gói này không mở bán.", 400);

        // App độc lập: người đăng nhập mua cho CHÍNH tài khoản của họ (mọi role).
        // Không cần validate quyền trên student — credit về đúng người login.
        var orderCode = await GenerateUniqueOrderCodeAsync(ct);
        var now = TimeZoneHelper.UtcNow;
        var amount = checked((int)package.Price);

        // Ý định thanh toán PENDING → payment_requests (không ghi payment_transactions
        // cho tới khi webhook báo thành công — payment_transactions là sổ cái success-only).
        var paymentRequest = new PaymentRequest
        {
            Bookingid = null,                              
            Userid = buyerUserId,                          
            Provider = PaymentRequestProvider.PayOS,
            Phase = PaymentRequestPhase.AiCredit,
            Ordercode = orderCode,
            Amount = package.Price,
            Currency = Currency.Vnd,
            Status = PaymentRequestStatus.Pending,
            AiCreditPackageid = package.Packageid,
            AiCreditUserid = buyerUserId,                  
            Description = $"TopTutor AI {package.Name}",
            Expiresat = now.AddHours(24),
            Createdat = now,
            Updatedat = now
        };
        context.PaymentRequests.Add(paymentRequest);
        await context.SaveChangesAsync(ct);

        var expiredAtUnix = (int)new DateTimeOffset(
            DateTime.SpecifyKind(paymentRequest.Expiresat!.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();

        var link = await _linkFactory.CreatePaymentLink(
            orderCode, amount, $"TopTutor AI {package.Code}", expiredAtUnix);

        paymentRequest.Paymentlinkid = link.PaymentLinkId;
        paymentRequest.Checkouturl = link.CheckoutUrl;
        paymentRequest.Qrcode = link.QrCode;
        paymentRequest.Updatedat = TimeZoneHelper.UtcNow;
        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Initiated AI credit purchase order {OrderCode} package {Package} buyer {Buyer} amount {Amount}",
            orderCode, package.Code, buyerUserId, package.Price);

        return new AiCreditPurchaseResponse
        {
            OrderCode = orderCode,
            CheckoutUrl = link.CheckoutUrl,
            QrCode = link.QrCode,
            PaymentLinkId = link.PaymentLinkId,
            Amount = package.Price,
            PackageId = package.Packageid,
            ExpiresAt = paymentRequest.Expiresat
        };
    }

    public async Task CompletePurchaseAsync(PaymentWebhookRequest webhook, string? rawPayload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(webhook.Data);
        var orderCode = webhook.Data.OrderCode;

        var pr = await context.PaymentRequests
            .FirstOrDefaultAsync(r => r.Ordercode == orderCode && r.Phase == PaymentRequestPhase.AiCredit, ct);
        if (pr is null)
        {
            logger.LogWarning("AI credit purchase webhook for unknown order {OrderCode}", orderCode);
            return;
        }

        // Webhook đã báo success → capture từ webhook (đầy đủ nhất), cộng credit.
        var capture = PaymentTransactionCapture.FromPayOSWebhook(webhook, rawPayload);
        await FinalizePurchaseAsync(pr, capture, ct);
    }

    public async Task<AiCreditPurchaseStatusResponse> GetPurchaseStatusAsync(
        string userId, long orderCode, CancellationToken ct = default)
    {
        var pr = await context.PaymentRequests
            .FirstOrDefaultAsync(r => r.Ordercode == orderCode && r.Phase == PaymentRequestPhase.AiCredit, ct)
            ?? throw new BookingException(AiCreditErrorCodes.PackageNotFound, "Không tìm thấy đơn mua.", 404);

        // Chỉ chủ đơn (người trả) hoặc người nhận credit mới xem được trạng thái.
        if (pr.Userid != userId && pr.AiCreditUserid != userId)
            throw new BookingException(AiCreditErrorCodes.UserNotFound, "Không có quyền xem đơn này.", 403);

        // Đã ghi credit rồi → trả PAID luôn, không cần gọi PayOS.
        if (await AlreadyCreditedAsync(orderCode, ct))
            return await BuildStatusAsync(PaymentRequestStatus.Paid, pr.AiCreditUserid, ct);

        // CHỦ ĐỘNG hỏi PayOS trạng thái đơn (không phụ thuộc webhook).
        string providerStatus;
        try
        {
            var link = !string.IsNullOrWhiteSpace(pr.Paymentlinkid)
                ? await payOS.PaymentRequests.GetAsync(pr.Paymentlinkid)
                : await payOS.PaymentRequests.GetAsync(orderCode);
            providerStatus = NormalizeStatus(link.Status.ToString());

            if (providerStatus == PaymentRequestStatus.Paid)
            {
                // PayOS xác nhận đã trả → cộng credit ngay (self-healing).
                var captures = PaymentTransactionCapture.FromPayOSPaymentLink(link);
                await FinalizePurchaseAsync(pr, captures.Count > 0 ? captures[0] : null, ct);
                return await BuildStatusAsync(PaymentRequestStatus.Paid, pr.AiCreditUserid, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không hỏi được PayOS trạng thái đơn AI credit {OrderCode}", orderCode);
            providerStatus = pr.Status; // giữ trạng thái cục bộ nếu PayOS lỗi
        }

        // Chưa trả: cập nhật trạng thái + kiểm tra hết hạn.
        var isExpired = providerStatus is PaymentRequestStatus.Expired or PaymentRequestStatus.Cancelled
            || (pr.Expiresat.HasValue && pr.Expiresat.Value < TimeZoneHelper.UtcNow);
        if (isExpired && PaymentRequestStatus.IsActive(pr.Status))
        {
            pr.Status = PaymentRequestStatus.Expired;
            pr.Updatedat = TimeZoneHelper.UtcNow;
            await context.SaveChangesAsync(ct);
        }

        return new AiCreditPurchaseStatusResponse
        {
            Status = isExpired ? PaymentRequestStatus.Expired : providerStatus,
            IsPaid = false,
            IsExpired = isExpired,
            Balance = null
        };
    }

    /// <summary>Ghi payment_transactions (nếu có capture) + đổi status PAID + cộng credit. Idempotent theo orderCode.</summary>
    private async Task FinalizePurchaseAsync(PaymentRequest pr, PaymentTransactionCapture? capture, CancellationToken ct)
    {
        var orderCode = pr.Ordercode ?? 0;

        if (await AlreadyCreditedAsync(orderCode, ct))
        {
            logger.LogInformation("AI credit order {OrderCode} already credited; skipping.", orderCode);
            return;
        }

        if (pr.AiCreditPackageid is null || string.IsNullOrWhiteSpace(pr.AiCreditUserid))
        {
            logger.LogError("AI credit order {OrderCode} thiếu package/user; không thể cộng credit.", orderCode);
            return;
        }

        var package = await context.AiCreditPackages
            .FirstOrDefaultAsync(p => p.Packageid == pr.AiCreditPackageid, ct);
        if (package is null)
        {
            logger.LogError("AI credit package {PackageId} not found for order {OrderCode}.", pr.AiCreditPackageid, orderCode);
            return;
        }

        var beneficiaryUserId = pr.AiCreditUserid!;

        // Ghi sổ cái giao dịch thành công (capture từ webhook hoặc từ PayOS lookup).
        if (capture is not null)
        {
            var pt = capture.Create(
                purpose: PaymentTransactionPurpose.AiCreditPurchase,
                direction: PaymentTransactionDirection.Inbound,
                amount: pr.Amount ?? package.Price,
                userId: pr.Userid,
                orderCode: orderCode,
                description: $"Goi AI {package.Name}",
                paymentRequestId: pr.Paymentrequestid,
                aiCreditPackageId: package.Packageid,
                aiCreditUserId: beneficiaryUserId);
            context.PaymentTransactions.Add(pt);
        }

        pr.Status = PaymentRequestStatus.Paid;
        pr.Updatedat = TimeZoneHelper.UtcNow;
        await context.SaveChangesAsync(ct);

        // Credit mua cũng có hạn dùng như credit tặng — cùng số tháng trong config.
        await GrantAsync(
            beneficiaryUserId, package.Creditamount, AiCreditSource.Purchase,
            orderCode.ToString(), $"Mua gói {package.Code} (+{package.Creditamount} lượt).",
            AiCreditBatchSource.Purchase, ct);

        logger.LogInformation(
            "Completed AI credit purchase order {OrderCode}: +{Amount} credits to user {UserId}.",
            orderCode, package.Creditamount, beneficiaryUserId);
    }

    private Task<bool> AlreadyCreditedAsync(long orderCode, CancellationToken ct)
        => context.AiCreditTransactions
            .AnyAsync(t => t.Source == AiCreditSource.Purchase && t.Referenceid == orderCode.ToString(), ct);

    private async Task<AiCreditPurchaseStatusResponse> BuildStatusAsync(string status, string? beneficiaryUserId, CancellationToken ct)
    {
        int? balance = null;
        if (!string.IsNullOrWhiteSpace(beneficiaryUserId))
            balance = await context.Users.AsNoTracking()
                .Where(u => u.Userid == beneficiaryUserId).Select(u => (int?)u.AiCreditsBalance).FirstOrDefaultAsync(ct);
        return new AiCreditPurchaseStatusResponse
        {
            Status = status,
            IsPaid = status == PaymentRequestStatus.Paid,
            IsExpired = status is PaymentRequestStatus.Expired or PaymentRequestStatus.Cancelled,
            Balance = balance
        };
    }

    private static string NormalizeStatus(string? status)
        => (status?.Trim().ToUpperInvariant()) switch
        {
            PayOSLinkStatus.Pending => PaymentRequestStatus.Pending,
            PayOSLinkStatus.Processing => PaymentRequestStatus.Processing,
            PayOSLinkStatus.Paid => PaymentRequestStatus.Paid,
            PayOSLinkStatus.Cancelled => PaymentRequestStatus.Cancelled,
            PayOSLinkStatus.Expired => PaymentRequestStatus.Expired,
            _ => PaymentRequestStatus.RequiresReview
        };

    private async Task<long> GenerateUniqueOrderCodeAsync(CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var candidate = OrderCodeHelper.GenerateAiCreditOrderCode();
            // Order code phải duy nhất trên cả 2 bảng: intent (payment_requests) + ledger (payment_transactions).
            var exists = await context.PaymentRequests.AnyAsync(r => r.Ordercode == candidate, ct)
                || await context.PaymentTransactions.AnyAsync(t => t.Ordercode == candidate, ct);
            if (!exists) return candidate;
        }
        throw new BookingException("AI_CREDIT_ORDER_CODE", "Không tạo được mã đơn hàng.", 500);
    }

    // Admin CRUD gói
    public async Task<IReadOnlyList<AiCreditPackageResponse>> AdminGetPackagesAsync(CancellationToken ct = default)
        => await context.AiCreditPackages
            .AsNoTracking()
            .OrderBy(p => p.Sortorder).ThenBy(p => p.Packageid)
            .Select(p => ToPackageResponse(p))
            .ToListAsync(ct);

    public async Task<AiCreditPackageResponse> AdminCreatePackageAsync(
        AiCreditPackageCreateRequest request, CancellationToken ct = default)
    {
        var code = request.Code.Trim().ToLowerInvariant();
        var dup = await context.AiCreditPackages.AnyAsync(p => p.Code.ToLower() == code, ct);
        if (dup)
            throw new BookingException(AiCreditErrorCodes.PackageCodeExists, "Mã gói đã tồn tại.", 409);

        var now = TimeZoneHelper.UtcNow;
        var pkg = new AiCreditPackage
        {
            Code = code,
            Name = request.Name.Trim(),
            Creditamount = request.CreditAmount,
            Price = request.Price,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "VND" : request.Currency,
            Ispurchasable = request.IsPurchasable,
            Isactive = request.IsActive,
            Sortorder = request.SortOrder,
            Description = request.Description,
            Iconurl = request.IconUrl,
            Createdat = now,
            Updatedat = now
        };
        context.AiCreditPackages.Add(pkg);
        await context.SaveChangesAsync(ct);
        return ToPackageResponse(pkg);
    }

    public async Task<AiCreditPackageResponse> AdminUpdatePackageAsync(
        int packageId, AiCreditPackageUpdateRequest request, CancellationToken ct = default)
    {
        var pkg = await context.AiCreditPackages.FirstOrDefaultAsync(p => p.Packageid == packageId, ct)
            ?? throw new BookingException(AiCreditErrorCodes.PackageNotFound, "Không tìm thấy gói.", 404);

        pkg.Name = request.Name.Trim();
        pkg.Creditamount = request.CreditAmount;
        pkg.Price = request.Price;
        pkg.Currency = string.IsNullOrWhiteSpace(request.Currency) ? "VND" : request.Currency;
        pkg.Ispurchasable = request.IsPurchasable;
        pkg.Isactive = request.IsActive;
        pkg.Sortorder = request.SortOrder;
        pkg.Description = request.Description;
        pkg.Iconurl = request.IconUrl;
        pkg.Updatedat = TimeZoneHelper.UtcNow;

        await context.SaveChangesAsync(ct);
        return ToPackageResponse(pkg);
    }

    public async Task AdminDeletePackageAsync(int packageId, CancellationToken ct = default)
    {
        var pkg = await context.AiCreditPackages.FirstOrDefaultAsync(p => p.Packageid == packageId, ct)
            ?? throw new BookingException(AiCreditErrorCodes.PackageNotFound, "Không tìm thấy gói.", 404);

        // Xóa cứng chỉ an toàn khi gói chưa từng có giao dịch nào tham chiếu tới — payment_transactions
        // và payment_requests đều FK ON DELETE SET NULL vào package_id, nên xóa cứng một gói đã có giao
        // dịch sẽ không lỗi nhưng làm rỗng luôn "giao dịch này thuộc gói nào" trong lịch sử/báo cáo.
        var hasTransactions = await context.PaymentTransactions.AnyAsync(t => t.AiCreditPackageid == packageId, ct)
            || await context.PaymentRequests.AnyAsync(r => r.AiCreditPackageid == packageId, ct);
        if (hasTransactions)
            throw new BookingException(
                AiCreditErrorCodes.PackageHasTransactions,
                "Gói đã có giao dịch nên không thể xóa. Hãy tắt \"Đang hoạt động\" ở màn hình sửa để ẩn gói thay vào đó.",
                409);

        context.AiCreditPackages.Remove(pkg);
        await context.SaveChangesAsync(ct);
    }

    public async Task<AiCreditPackageResponse> AdminUploadIconAsync(
        int packageId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            throw new BookingException(AiCreditErrorCodes.InvalidAmount, "File icon rỗng.", 400);

        var pkg = await context.AiCreditPackages.FirstOrDefaultAsync(p => p.Packageid == packageId, ct)
            ?? throw new BookingException(AiCreditErrorCodes.PackageNotFound, "Không tìm thấy gói.", 404);

        // Xóa icon cũ (best-effort) trước khi thay.
        if (!string.IsNullOrWhiteSpace(pkg.Iconurl))
        {
            try { await fileStorage.DeleteFileAsync(StorageBucket.AiCreditIcons, pkg.Code, pkg.Iconurl); }
            catch (Exception ex) { logger.LogWarning(ex, "Không xóa được icon cũ của gói {PackageId}", packageId); }
        }

        var url = await fileStorage.UploadFileAsync(StorageBucket.AiCreditIcons, pkg.Code, file);
        pkg.Iconurl = url;
        pkg.Updatedat = TimeZoneHelper.UtcNow;
        await context.SaveChangesAsync(ct);

        return ToPackageResponse(pkg);
    }

    // Hạn dùng credit

    /// <summary>
    /// Trừ credit đã hết hạn khỏi balance. Chạy trước mỗi lần tiêu — rẻ hơn hẳn so với
    /// job quét định kỳ, và học sinh luôn thấy con số đúng ngay khi mở app.
    /// </summary>
    private async Task ExpireOverdueBatchesAsync(string userId, CancellationToken ct)
    {
        var now = TimeZoneHelper.UtcNow;
        var overdue = await context.AiCreditBatches
            .Where(b => b.UserId == userId
                        && b.ExpiresAt != null && b.ExpiresAt < now
                        && b.Consumed < b.Granted)
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        var lost = overdue.Sum(b => b.Granted - b.Consumed);
        foreach (var b in overdue) b.Consumed = b.Granted;   // đánh dấu tiêu hết

        // Balance không được âm dù dữ liệu lệch.
        await context.Users
            .Where(u => u.Userid == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.AiCreditsBalance,
                u => u.AiCreditsBalance > lost ? u.AiCreditsBalance - lost : 0), ct);

        await context.SaveChangesAsync(ct);
        logger.LogInformation("Hết hạn {Lost} credit của user {UserId}", lost, userId);
    }

    /// <summary>Ghi số đã tiêu vào các lô, sắp hết hạn tiêu trước.</summary>
    private async Task ConsumeBatchesAsync(string userId, int amount, CancellationToken ct)
    {
        var batches = await context.AiCreditBatches
            .Where(b => b.UserId == userId && b.Consumed < b.Granted)
            // NULL (không hết hạn) xếp cuối — tiêu lô có hạn trước.
            .OrderBy(b => b.ExpiresAt == null)
            .ThenBy(b => b.ExpiresAt)
            .ToListAsync(ct);

        var left = amount;
        foreach (var b in batches)
        {
            if (left <= 0) break;
            var take = Math.Min(left, b.Granted - b.Consumed);
            b.Consumed += take;
            left -= take;
        }

        // left > 0 nghĩa là balance nhiều hơn tổng các lô — credit cấp trước khi có bảng
        // này, hoặc admin cấp tay. Không phải lỗi, cứ để balance làm nguồn chân lý.
        await context.SaveChangesAsync(ct);
    }

    // Config bonus

    public async Task AdminSetExpiryMonthsAsync(int months, string? updatedByUserId, CancellationToken ct = default)
    {
        if (months < 0)
            throw new BookingException(AiCreditErrorCodes.InvalidAmount, "Số tháng không hợp lệ.", 400);
        await UpsertConfigAsync(AiCreditConfigKeys.ExpiryMonths, months.ToString(), updatedByUserId,
            "So THANG credit AI het han ke tu ngay cap.", ct);
    }

    public async Task<int> AdminGetFreeOnSignupAsync(CancellationToken ct = default)
    {
        var cfg = await context.Systemconfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Configkey == AiCreditConfigKeys.FreeOnSignup, ct);
        return int.TryParse(cfg?.Configvalue, out var v) ? v : 0;
    }

    public async Task AdminSetFreeOnSignupAsync(int amount, string? updatedByUserId, CancellationToken ct = default)
    {
        if (amount < 0)
            throw new BookingException(AiCreditErrorCodes.InvalidAmount, "Số lượt không hợp lệ.", 400);
        await UpsertConfigAsync(AiCreditConfigKeys.FreeOnSignup, amount.ToString(), updatedByUserId,
            "So luot AI tang khi xac thuc so dien thoai.", ct);
    }

    /// <summary>Ghi 1 key trong system_configs, tạo mới nếu chưa có.</summary>
    private async Task UpsertConfigAsync(
        string key, string value, string? updatedByUserId, string description, CancellationToken ct)
    {
        var cfg = await context.Systemconfigs.FirstOrDefaultAsync(c => c.Configkey == key, ct);
        if (cfg is null)
        {
            cfg = new Systemconfig { Configkey = key, Description = description };
            context.Systemconfigs.Add(cfg);
        }
        cfg.Configvalue = value;
        cfg.Updatedby = updatedByUserId;
        cfg.Updatedat = TimeZoneHelper.UtcNow;
        await context.SaveChangesAsync(ct);
    }

    public async Task<int> AdminGetBookingBonusAsync(CancellationToken ct = default)
    {
        var cfg = await context.Systemconfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Configkey == AiCreditConfigKeys.BonusOnBooking, ct);
        return int.TryParse(cfg?.Configvalue, out var v) ? v : 0;
    }

    public async Task AdminSetBookingBonusAsync(int amount, string? updatedByUserId, CancellationToken ct = default)
    {
        if (amount < 0)
            throw new BookingException(AiCreditErrorCodes.InvalidAmount, "Số bonus không hợp lệ.", 400);

        var cfg = await context.Systemconfigs
            .FirstOrDefaultAsync(c => c.Configkey == AiCreditConfigKeys.BonusOnBooking, ct);
        if (cfg is null)
        {
            cfg = new Systemconfig
            {
                Configkey = AiCreditConfigKeys.BonusOnBooking,
                Description = "Số lượt hỏi AI tặng cho học sinh mỗi khi có booking."
            };
            context.Systemconfigs.Add(cfg);
        }
        cfg.Configvalue = amount.ToString();
        cfg.Updatedby = updatedByUserId;
        cfg.Updatedat = TimeZoneHelper.UtcNow;
        await context.SaveChangesAsync(ct);
    }

    private static AiCreditPackageResponse ToPackageResponse(AiCreditPackage p) => new()
    {
        PackageId = p.Packageid,
        Code = p.Code,
        Name = p.Name,
        CreditAmount = p.Creditamount,
        Price = p.Price,
        Currency = p.Currency,
        IsPurchasable = p.Ispurchasable,
        IsActive = p.Isactive,
        SortOrder = p.Sortorder,
        Description = p.Description,
        IconUrl = p.Iconurl
    };
}
