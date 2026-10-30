using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework.Export;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.14's lesson-export checks: every fixture is what the .NET lessons view exports to the .NET Markdown sink
/// for the case's records and holdout rows, the file byte for byte.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public async Task Every_lesson_export_fixture_is_what_the_lessons_view_exports_to_the_markdown_sink()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in LessonExportContract.Cases)
        {
            var directory = $"fixtures/{LessonExportContract.Family}/{name}";
            ContractFiles.Verify($"{directory}/seed.json", ContractJson.Write(LessonExportContract.ToSeedJson(definition)), drift);
            ContractFiles.Verify($"{directory}/options.json", ContractJson.Write(definition.Options), drift);
            var previous = LessonExportContract.PreviousFile(definition);
            if (previous is not null)
            {
                ContractFiles.Verify($"{directory}/previous.md", previous, drift);
            }

            var run = await LessonExportContract.RunAsync(definition, ContractFiles.Read($"{directory}/previous.md") ?? previous);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(LessonExportContract.ExpectedJson(run.Result)), drift);
            ContractFiles.Verify($"{directory}/expected.md", run.File ?? string.Empty, drift);
        }

        ContractFiles.VerifyNoStray(LessonExportContract.Family, LessonExportContract.Cases.Select(entry => entry.Name), LessonExportContract.Files, drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public async Task The_lesson_export_cases_cover_every_rule()
    {
        var runs = new Dictionary<string, LessonExportRun>();
        foreach (var (name, definition) in LessonExportContract.Cases)
        {
            runs[name] = await LessonExportContract.RunAsync(definition, LessonExportContract.PreviousFile(definition));
        }

        // Only measured Helps, eligible, verified, in the scope: H, M and A of the nine lessons.
        Assert.Equal(3, runs["measured-helps-only"].Result.Lessons.Count);
        Assert.Contains(runs["measured-helps-only"].Result.Lessons, lesson => lesson.LessonText.Contains("Authored: ", StringComparison.Ordinal));
        Assert.Equal(new ExperienceLessonSyncResult(3, 0, 0), runs["measured-helps-only"].Result.Sync);

        Assert.Empty(runs["nothing-measured-helpful"].Result.Lessons);
        Assert.Contains("Lessons: 0\n", runs["nothing-measured-helpful"].File, StringComparison.Ordinal);

        Assert.Contains("    team: payments\n", runs["scope-with-every-segment"].File, StringComparison.Ordinal);
        Assert.Equal(new ExperienceLessonSyncResult(1, 1, 1), runs["sync-against-previous-file"].Result.Sync);
        Assert.Equal(new ExperienceLessonSyncResult(2, 1, 0), runs["hand-edited-previous-file"].Result.Sync);

        Assert.Equal(ExperienceStoreOutcome.Denied, runs["denied"].Result.Outcome);
        Assert.Null(runs["denied"].Result.Sync);
        Assert.Null(runs["denied"].File);
    }
}
