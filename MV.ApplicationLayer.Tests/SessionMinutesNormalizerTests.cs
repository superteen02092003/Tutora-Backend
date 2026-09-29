using System.Text.Json;
using MV.ApplicationLayer.Helpers;
using MV.DomainLayer.DTO.ResponseModel;
using Xunit;

namespace MV.ApplicationLayer.Tests;

/// <summary>
/// Biên bản buổi học v3 (2026-09-29): code phải chặn được những gì model hay làm sai — trả quá số lượng,
/// trả null cho list, trả kết quả bài làm ngoài danh sách cho phép — và vẫn đọc được bản nháp cũ.
/// </summary>
public class SessionMinutesNormalizerTests
{
    [Fact]
    public void Normalize_CapsListsAtPromptLimits()
    {
        var minutes = new TutorSessionMinutes
        {
            Summary = "  Tóm tắt  ",
            Exercises = Enumerable.Range(0, 20)
                .Select(i => new TutorMinutesExercise { Type = $"Dạng {i}", Result = "Tự làm đúng" }).ToList(),
            Strengths = Enumerable.Range(0, 9).Select(i => $"Ý {i}").ToList(),
            TeachingNotes = Enumerable.Range(0, 5)
                .Select(i => new TutorTeachingNote { Content = $"Nhận xét {i}", Example = "Ví dụ" }).ToList(),
            FollowUps = Enumerable.Range(0, 9).Select(i => $"Việc {i}").ToList()
        };

        var result = SessionMinutesNormalizer.Normalize(minutes)!;

        Assert.Equal("Tóm tắt", result.Summary);
        Assert.Equal(SessionMinutesNormalizer.MaxExercises, result.Exercises.Count);
        Assert.Equal(SessionMinutesNormalizer.MaxStrengths, result.Strengths.Count);
        Assert.Equal(SessionMinutesNormalizer.MaxTeachingNotes, result.TeachingNotes.Count);
        Assert.Equal(SessionMinutesNormalizer.MaxFollowUps, result.FollowUps.Count);
    }

    [Fact]
    public void Normalize_DropsUnknownExerciseResultAndEmptyItems()
    {
        var minutes = new TutorSessionMinutes
        {
            Exercises =
            [
                new() { Type = "Bất phương trình có chuyển vế", Result = "Xuất sắc" },
                new() { Type = "  ", Result = "Làm sai" }
            ],
            TeachingNotes = [new() { Content = " ", Example = "x" }],
            Sections = [new() { Title = null, Details = ["", " "] }]
        };

        var result = SessionMinutesNormalizer.Normalize(minutes)!;

        var exercise = Assert.Single(result.Exercises);
        Assert.Null(exercise.Result);
        Assert.Empty(result.TeachingNotes);
        Assert.Empty(result.Sections);
    }

    [Fact]
    public void Normalize_ReturnsNullWhenNothingUseful()
    {
        Assert.Null(SessionMinutesNormalizer.Normalize(new TutorSessionMinutes { Summary = " " }));
        Assert.Null(SessionMinutesNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_HandlesNullListsFromModelJson()
    {
        const string json = """
            {"summary":"Buổi học ôn bất phương trình.","sections":null,"exercises":null,"keyPoints":null,
             "followUps":null,"strengths":null,"difficulties":null,"usefulNotes":null,"teachingNotes":null}
            """;
        var parsed = JsonSerializer.Deserialize<TutorSessionMinutes>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var result = SessionMinutesNormalizer.Normalize(parsed)!;

        Assert.Equal("Buổi học ôn bất phương trình.", result.Summary);
        Assert.Empty(result.Exercises);
        Assert.Empty(result.TeachingNotes);
    }

    [Fact]
    public void DraftSavedBeforeV3_StillDeserializes()
    {
        // Airesult của các buổi cũ được lưu bằng JsonSerializer mặc định (PascalCase), chỉ có 3 field biên bản.
        const string oldDraft = """
            {"LessonContent":"a","Homework":"b","TutorNotes":"c",
             "SessionMinutes":{"Summary":"s","KeyPoints":["k"],"FollowUps":["f"]},
             "ZaloSummary":{"Content":"x","Homework":"y","Notes":"z"}}
            """;

        var draft = JsonSerializer.Deserialize<TutorReportAiFillResult>(oldDraft)!;

        Assert.Equal("s", draft.SessionMinutes!.Summary);
        Assert.Single(draft.SessionMinutes.KeyPoints);
        Assert.Empty(draft.SessionMinutes.Exercises);
        Assert.Empty(draft.SessionMinutes.TeachingNotes);
    }
}
