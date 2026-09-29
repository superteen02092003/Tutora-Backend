using MV.DomainLayer.DTO.ResponseModel;

namespace MV.ApplicationLayer.Helpers;

/// <summary>
/// Chuẩn hoá biên bản buổi học Gemini trả về. Model có thể lờ giới hạn trong prompt (buổi dài dễ kể lể) hoặc
/// trả null cho list (ghi đè giá trị mặc định), nên code cắt lại theo đúng số lượng trong prompt và bỏ mục
/// rỗng. Biên bản chỉ là phần cho gia sư — trả thiếu thì bỏ qua chứ không làm hỏng báo cáo gửi phụ huynh.
/// </summary>
public static class SessionMinutesNormalizer
{
    public const int MaxSections = 8;
    public const int MaxSectionDetails = 6;
    public const int MaxExercises = 15;
    public const int MaxStrengths = 5;
    public const int MaxDifficulties = 5;
    public const int MaxUsefulNotes = 6;
    public const int MaxTeachingNotes = 3;
    public const int MaxKeyPoints = 6;
    public const int MaxFollowUps = 6;

    public static TutorSessionMinutes? Normalize(TutorSessionMinutes? minutes)
    {
        if (minutes is null) return null;

        minutes.Summary = Trim(minutes.Summary);
        minutes.Sections = (minutes.Sections ?? new())
            .Where(s => s is not null)
            .Select(s => new TutorMinutesSection { Title = Trim(s.Title), Details = Clean(s.Details, MaxSectionDetails) })
            .Where(s => s.Title is not null || s.Details.Count > 0)
            .Take(MaxSections)
            .ToList();
        minutes.Exercises = (minutes.Exercises ?? new())
            .Where(e => e is not null && Trim(e.Type) is not null)
            .Select(e => new TutorMinutesExercise
            {
                Type = Trim(e.Type),
                // Schema đã ép enum; giá trị lạ (model cũ, bản nháp tay) thì bỏ trống thay vì hiện sai.
                Result = TutorMinutesExercise.Results.Contains(Trim(e.Result)) ? Trim(e.Result) : null,
                Note = Trim(e.Note)
            })
            .Take(MaxExercises)
            .ToList();
        minutes.Strengths = Clean(minutes.Strengths, MaxStrengths);
        minutes.Difficulties = Clean(minutes.Difficulties, MaxDifficulties);
        minutes.UsefulNotes = Clean(minutes.UsefulNotes, MaxUsefulNotes);
        minutes.TeachingNotes = (minutes.TeachingNotes ?? new())
            .Where(n => n is not null && Trim(n.Content) is not null)
            .Select(n => new TutorTeachingNote { Content = Trim(n.Content), Example = Trim(n.Example) })
            .Take(MaxTeachingNotes)
            .ToList();
        minutes.KeyPoints = Clean(minutes.KeyPoints, MaxKeyPoints);
        minutes.FollowUps = Clean(minutes.FollowUps, MaxFollowUps);

        var empty = minutes.Summary is null
            && minutes.Sections.Count == 0 && minutes.Exercises.Count == 0
            && minutes.Strengths.Count == 0 && minutes.Difficulties.Count == 0
            && minutes.UsefulNotes.Count == 0 && minutes.TeachingNotes.Count == 0
            && minutes.KeyPoints.Count == 0 && minutes.FollowUps.Count == 0;
        return empty ? null : minutes;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> Clean(List<string>? items, int max) =>
        (items ?? new List<string>())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Take(max)
            .ToList();
}
