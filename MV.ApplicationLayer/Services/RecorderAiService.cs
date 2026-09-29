using System.Text.Json;
using FFMpegCore;
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
using MV.DomainLayer.Helpers;

namespace MV.ApplicationLayer.Services;

/// <summary>
/// Job AI của luồng ghi âm app: tải các đoạn từ kho (Supabase), ghép bằng
/// ffmpeg, đưa lên Gemini, lưu bản nháp vào recorder.lessons rồi báo gia sư.
/// Hoàn toàn tách khỏi ClassSessionVideoAiService (luồng video Agora).
/// </summary>
public class RecorderAiService(
    IAppDbContext db,
    IAppRecordingStorage storage,
    IGeminiVideoAnalysisService geminiService,
    INotificationService notificationService,
    IOptions<GoogleGeminiSettings> geminiSettings,
    ILogger<RecorderAiService> logger) : IRecorderAiService
{
    /// <summary>AAC-LC trong container MP4 — đúng thứ package `record` trên app xuất ra.</summary>
    private const string AudioMimeType = "audio/mp4";
    private const long MaxBytes = 2_000_000_000;

    public async Task RunLessonReportJobAsync(Guid lessonId)
    {
        var lesson = await db.RecorderLessons.FirstOrDefaultAsync(l => l.Lessonid == lessonId);
        if (lesson == null)
        {
            logger.LogWarning("RunLessonReportJobAsync: buổi {LessonId} không tồn tại", lessonId);
            return;
        }
        // Buổi đã bị huỷ / đã duyệt trong lúc job xếp hàng thì không tốn tiền model nữa.
        if (lesson.Status != SessionRecordingStatus.Processing) return;

        try
        {
            lesson.Aistatus = RecorderAiStatus.Processing;
            lesson.Updatedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();

            var fileUri = await EnsureGeminiFileAsync(lesson, CancellationToken.None);
            var grade = lesson.Studentid is Guid studentId
                ? await db.RecorderStudents.Where(s => s.Studentid == studentId).Select(s => s.Grade).FirstOrDefaultAsync()
                : null;
            var context = new TutorReportContext(lesson.Subject, grade);
            var result = await geminiService.GenerateTutorReportFieldsAsync(fileUri, AudioMimeType, context, CancellationToken.None);

            lesson.Airesult = JsonSerializer.Serialize(result);
            lesson.Aistatus = RecorderAiStatus.Completed;
            lesson.Aierror = null;
            lesson.Status = SessionRecordingStatus.AwaitingApproval;
            lesson.Updatedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();

            await NotifyAsync(lesson,
                "Báo cáo buổi học đã sẵn sàng",
                "AI đã viết xong báo cáo. Mở app để xem, sửa và gửi phụ huynh.");

            // Transcript là nguyên liệu thô, gia sư không xem → không chép lời ngay ở đây mà xếp
            // hàng để RecorderTranscriptBatchJob gom nhiều buổi vào một batch Gemini (giá 50%).
            if (geminiSettings.Value.GenerateTranscript)
            {
                lesson.Transcriptstatus = RecorderTranscriptStatus.Queued;
                lesson.Transcriptqueuedat = TimeZoneHelper.UtcNow;
                lesson.Transcripterror = null;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job AI cho buổi {LessonId} thất bại.", lessonId);
            lesson.Aistatus = RecorderAiStatus.Failed;
            lesson.Aierror = "Chưa tạo được báo cáo từ bản ghi. Bạn thử lại hoặc tự viết báo cáo.";
            lesson.Status = SessionRecordingStatus.Failed;
            lesson.Updatedat = TimeZoneHelper.UtcNow;
            await db.SaveChangesAsync();

            await NotifyAsync(lesson,
                "Chưa tạo được báo cáo",
                "AI chưa xử lý được bản ghi buổi học. Bản ghi vẫn được giữ, bạn có thể thử lại.");
        }
    }

    /// <summary>
    /// Ghép các đoạn rồi upload lên Gemini (tái dùng file nếu còn hạn ~48h).
    ///
    /// Vì sao ghép bằng ffmpeg chứ không nối byte: mỗi đoạn là một file MP4
    /// hoàn chỉnh có moov atom riêng. Nối thô ra một file hỏng — Gemini chỉ
    /// đọc được đoạn đầu. Concat demuxer với -c copy chỉ dựng lại container.
    /// </summary>
    private Task<string> EnsureGeminiFileAsync(RecorderLesson lesson, CancellationToken ct) =>
        EnsureGeminiFileAsync(lesson, TimeSpan.FromMinutes(10), ct);

    /// <summary>Dùng cho batch chép lời: batch có thể mất tới 24 giờ nên file trên Gemini phải
    /// còn sống đủ lâu — hết hạn giữa chừng thì yêu cầu trong batch lỗi.</summary>
    public Task<string> EnsureGeminiFileAsync(RecorderLesson lesson, TimeSpan minRemaining, CancellationToken ct = default) =>
        EnsureGeminiFileCoreAsync(lesson, minRemaining, ct);

    private async Task<string> EnsureGeminiFileCoreAsync(RecorderLesson lesson, TimeSpan minRemaining, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(lesson.Geminifileuri)
            && lesson.Geminifileexpiresat > TimeZoneHelper.UtcNow.Add(minRemaining))
            return lesson.Geminifileuri!;

        if (string.IsNullOrWhiteSpace(lesson.Storagekey))
            throw new InvalidOperationException("Buổi học chưa có bản ghi.");

        var parts = await storage.ListPartsAsync(lesson.Storagekey!, ct);
        if (parts.Count == 0)
            throw new InvalidOperationException("Không tìm thấy đoạn ghi âm nào trên kho.");
        if (parts.Sum(p => p.Bytes) > MaxBytes)
            throw new InvalidOperationException("Bản ghi quá lớn.");

        var workDir = Path.Combine(Path.GetTempPath(), $"recorder-{lesson.Lessonid:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var mergedPath = Path.Combine(workDir, "merged.m4a");

        try
        {
            var localFiles = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                var local = Path.Combine(workDir, $"part-{part.PartNumber:D4}.m4a");
                await using (var source = await storage.OpenPartAsync(part.Key, ct))
                await using (var target = File.Create(local))
                {
                    await source.CopyToAsync(target, ct);
                }
                localFiles.Add(local);
            }

            if (localFiles.Count == 1)
            {
                File.Move(localFiles[0], mergedPath);
            }
            else
            {
                var listPath = Path.Combine(workDir, "parts.txt");
                await File.WriteAllLinesAsync(
                    listPath,
                    localFiles.Select(f => $"file '{f.Replace("'", @"'\''")}'"),
                    ct);

                await FFMpegArguments
                    .FromFileInput(listPath, verifyExists: false, options => options
                        .WithCustomArgument("-f concat -safe 0"))
                    .OutputToFile(mergedPath, overwrite: true, options => options
                        .WithCustomArgument("-c copy"))
                    .ProcessAsynchronously();
            }

            // Giữ bản đã ghép trên kho để app nghe lại (một file, tua được) — thay vì
            // bắt app tự nối nhiều đoạn 5 phút. Hỏng bước này không chặn báo cáo.
            try
            {
                var audioKey = $"{lesson.Storagekey}merged.m4a";
                await storage.UploadAsync(audioKey, mergedPath, AudioMimeType, ct);
                lesson.Audiokey = audioKey;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không lưu được file nghe lại cho buổi {LessonId}.", lesson.Lessonid);
            }

            GeminiUploadedFile uploaded;
            await using (var audio = File.OpenRead(mergedPath))
            {
                uploaded = await geminiService.UploadVideoAsync(
                    audio, new FileInfo(mergedPath).Length, AudioMimeType, $"lesson-{lesson.Lessonid:N}.m4a", ct);
            }
            await geminiService.WaitForFileActiveAsync(uploaded.Name, ct);

            lesson.Geminifilename = uploaded.Name;
            lesson.Geminifileuri = uploaded.Uri;
            lesson.Geminifileexpiresat = TimeZoneHelper.UtcNow.AddHours(47);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Buổi {LessonId}: ghép {PartCount} đoạn và đưa lên Gemini.", lesson.Lessonid, parts.Count);
            return uploaded.Uri;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* dọn rác hỏng không được làm hỏng job */ }
        }
    }

    private async Task NotifyAsync(RecorderLesson lesson, string title, string message)
    {
        try
        {
            await notificationService.CreateNotificationAsync(new NotificationRequest
            {
                Userid = lesson.Tutorid,
                Title = title,
                Message = message,
                Type = NotificationType.LessonReportAiFillReady,
                Referenceid = lesson.Lessonid.ToString()
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không gửi được thông báo cho gia sư {TutorId}", lesson.Tutorid);
        }
    }
}
