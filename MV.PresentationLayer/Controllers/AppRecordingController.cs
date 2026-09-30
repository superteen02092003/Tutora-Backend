using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MV.ApplicationLayer.ServiceInterfaces;
using MV.DomainLayer.Constants;
using MV.DomainLayer.DTO;
using MV.DomainLayer.DTO.RequestModel;

namespace MV.PresentationLayer.Controllers;

/// <summary>
/// Ghi âm buổi dạy TẠI NHÀ từ app gia sư.
///
/// Khác <see cref="RecordingController"/> (điều khiển Agora Cloud Recording cho
/// buổi học trực tuyến): ở đây không có kênh RTC nào cả. Gia sư ghi âm ngay
/// trên điện thoại, app cắt đoạn 5 phút và PUT thẳng lên kho qua URL presigned,
/// backend chỉ giữ trạng thái và xếp hàng job AI.
///
/// Mọi endpoint đều kiểm tra gia sư đang đăng nhập có đúng là người dạy buổi đó
/// không. App không bao giờ gửi kèm người nhận báo cáo — server tự suy ra.
/// </summary>
[ApiController]
[Route("api/recording/app")]
[Authorize(Roles = UserRole.Tutor)]
public class AppRecordingController(IAppRecordingService service) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>POST /api/recording/app/{classSessionId}/start — buổi có booking.</summary>
    [HttpPost("{classSessionId:int}/start")]
    public async Task<IActionResult> Start(int classSessionId, [FromBody] AppRecordingStartRequest? body, CancellationToken ct)
    {
        var result = await service.StartForClassSessionAsync(classSessionId, UserId, body?.ConsentSnapshot, ct);
        return Ok(APIResponse<object>.Success(result, "Đã mở bản ghi cho buổi học."));
    }

    /// <summary>POST /api/recording/app/lesson/{lessonId}/start — buổi đã tạo sẵn cho học sinh ngoài nền tảng.</summary>
    [HttpPost("lesson/{lessonId:guid}/start")]
    public async Task<IActionResult> StartLesson(Guid lessonId, [FromBody] AppRecordingStartRequest? body, CancellationToken ct)
    {
        var result = await service.StartForLessonAsync(lessonId, UserId, body?.ConsentSnapshot, ct);
        return Ok(APIResponse<object>.Success(result, "Đã mở bản ghi cho buổi học."));
    }

    /// <summary>POST /api/recording/app/student/{studentId}/start — ghi ngay, tự tạo buổi mới.</summary>
    [HttpPost("student/{studentId:guid}/start")]
    public async Task<IActionResult> StartStudent(Guid studentId, [FromBody] AppRecordingStartRequest? body, CancellationToken ct)
    {
        var result = await service.StartForStudentAsync(studentId, UserId, body?.ConsentSnapshot, ct);
        return Ok(APIResponse<object>.Success(result, "Đã mở bản ghi cho buổi học."));
    }

    /// <summary>POST /api/recording/app/{recordingId}/approve — gia sư duyệt báo cáo đã sửa.</summary>
    [HttpPost("{recordingId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid recordingId, [FromBody] RecorderApproveRequest body, CancellationToken ct)
    {
        var result = await service.ApproveAsync(recordingId, UserId, body, ct);
        return Ok(APIResponse<object>.Success(result, "Đã duyệt báo cáo."));
    }

    /// <summary>POST /api/recording/app/{recordingId}/upload-url?partNumber=0</summary>
    [HttpPost("{recordingId:guid}/upload-url")]
    public async Task<IActionResult> UploadUrl(Guid recordingId, [FromQuery] int partNumber, CancellationToken ct)
    {
        var result = await service.CreateUploadUrlAsync(recordingId, UserId, partNumber, ct);
        return Ok(APIResponse<object>.Success(result, "Đã cấp đường dẫn tải lên."));
    }

    /// <summary>POST /api/recording/app/{recordingId}/complete</summary>
    [HttpPost("{recordingId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid recordingId, [FromBody] AppRecordingCompleteRequest body, CancellationToken ct)
    {
        var result = await service.CompleteAsync(recordingId, UserId, body.DurationSec, ct);
        return Ok(APIResponse<object>.Success(result, "Đã chốt bản ghi, AI đang xử lý."));
    }

    /// <summary>GET /api/recording/app/{recordingId} — app poll trạng thái.</summary>
    [HttpGet("{recordingId:guid}")]
    public async Task<IActionResult> Get(Guid recordingId, CancellationToken ct)
    {
        var result = await service.GetAsync(recordingId, UserId, ct);
        return Ok(APIResponse<object>.Success(result, "Lấy trạng thái bản ghi thành công."));
    }

    /// <summary>
    /// GET /api/recording/app/{recordingId}/audio-url — ĐÃ TẮT. Gia sư không được nghe lại bản ghi;
    /// chỉ admin Tutora nghe qua GET /api/admin/recorder/lessons/{lessonId}/audio. Giữ route để
    /// bản app cũ nhận thông báo rõ ràng (403) thay vì 404.
    /// </summary>
    [HttpGet("{recordingId:guid}/audio-url")]
    public IActionResult AudioUrl(Guid recordingId) =>
        StatusCode(403, APIResponse<object>.Fail(
            "Gia sư không thể nghe lại bản ghi âm. Bản ghi chỉ được TopTutor dùng để tạo báo cáo và xử lý khiếu nại.", 403));

    /// <summary>POST /api/recording/app/{recordingId}/ai-feedback — gia sư báo nội dung AI sai / không phù hợp.</summary>
    [HttpPost("{recordingId:guid}/ai-feedback")]
    public async Task<IActionResult> ReportAiFeedback(Guid recordingId, [FromBody] RecorderAiFeedbackRequest body, CancellationToken ct)
    {
        await service.ReportAiFeedbackAsync(recordingId, UserId, body, ct);
        return Ok(APIResponse<object>.Success(new { }, "Cảm ơn bạn đã báo. TopTutor sẽ xem lại nội dung này."));
    }

    /// <summary>DELETE /api/recording/app/{recordingId} — gia sư bỏ bản ghi.</summary>
    [HttpDelete("{recordingId:guid}")]
    public async Task<IActionResult> Discard(Guid recordingId, CancellationToken ct)
    {
        await service.DiscardAsync(recordingId, UserId, ct);
        return Ok(APIResponse<object>.Success(new { }, "Đã bỏ bản ghi."));
    }
}
