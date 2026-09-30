namespace MV.DomainLayer.Configuration;

/// <summary>
/// Cấu hình Resend API để gửi email.
/// </summary>
public class ResendSettings
{
    public const string SectionName = "ResendSettings";

    /// <summary>API Key lấy từ dashboard Resend.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Email gửi đi (phải là domain đã verify trên Resend).</summary>
    public string SenderEmail { get; set; } = string.Empty;

    /// <summary>Tên hiển thị của người gửi.</summary>
    public string SenderName { get; set; } = "TopTutor";
}
