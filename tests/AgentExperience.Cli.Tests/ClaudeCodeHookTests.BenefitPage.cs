using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentExperience.Cli.ClaudeCode;

namespace AgentExperience.Cli.Tests;

/// <summary>
/// <c>aexp benefit --markdown</c>: the results page, golden, over a SQLite store seeded through the Claude Code hooks with
/// one session in each holdout arm (so every count is fixed: the arms follow the session IDs, not chance).
/// </summary>
public sealed partial class ClaudeCodeHookTests
{
    private const string UpdateGoldenVariable = "AEXP_UPDATE_GOLDEN";

    [Fact]
    public async Task Benefit_markdown_writes_the_results_page_for_a_seeded_store()
    {
        using var project = new TempProject(config => config["injection"] = new JsonObject { ["holdoutRate"] = 0.5 });
        await LearnAsync(project, "d0d0d0d0-d0d0-4d0d-8d0d-d0d0d0d0d0d0");

        var salt = MicrosoftAgentFramework.Injection.ExperienceHoldoutOptions.DefaultSalt;
        string Find(Abstractions.HoldoutArm arm) => Enumerable.Range(0, 1000)
            .Select(i => $"d1d1d1d1-d1d1-4d1d-8d1d-{i:D12}")
            .First(id => SessionHoldout.ArmOf(id, salt, 0.5) == arm);

        // Treatment: three turns, the second fails its tests. Holdout: two turns, the first fails, then a third turn the
        // session ends before it stops, so it is stored without a verdict (pending).
        await TurnsAsync(project, Find(Abstractions.HoldoutArm.Treatment), [true, false, true]);
        var holdout = Find(Abstractions.HoldoutArm.Holdout);
        await TurnsAsync(project, holdout, [false, true]);
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(holdout, Task + " last turn")));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(holdout)));
        Assert.Null(await HookAsync(project, "SessionEnd", Session(holdout)));

        var written = await project.OkAsync("benefit", "--markdown", "results.md");
        Assert.Equal("Wrote the holdout's estimate (NoEvidence) to results.md." + Environment.NewLine, written);

        var page = await File.ReadAllTextAsync(Path.Combine(project.Root, "results.md"));
        Assert.Matches(new Regex(@"\A# Holdout results, \d{4}-\d{2}-\d{2}\n"), page);
        var normalized = Regex.Replace(page, @"\A# Holdout results, \d{4}-\d{2}-\d{2}\n", "# Holdout results, <date>\n");
        Assert.DoesNotContain(project.Root, page, StringComparison.Ordinal);

        var golden = GoldenPath();
        if (Environment.GetEnvironmentVariable(UpdateGoldenVariable) == "1")
        {
            await File.WriteAllTextAsync(golden, normalized);
        }

        Assert.Equal(await File.ReadAllTextAsync(golden), normalized);

        // Without --markdown the terminal summary is unchanged.
        Assert.Contains("Given lessons:    2/3 verified", await project.OkAsync("benefit"));
    }

    [Fact]
    public async Task Benefit_markdown_writes_inside_the_project_only_and_takes_no_other_option()
    {
        using var project = new TempProject();
        var outside = Path.Combine(Path.GetTempPath(), $"aexp-outside-{Guid.NewGuid():N}.md");
        var refused = await project.RunAsync("benefit", "--markdown", outside);
        Assert.Equal(AexpCli.Usage, refused.Exit);
        Assert.False(File.Exists(outside));

        Assert.Equal(AexpCli.Usage, (await project.RunAsync("benefit", "--html", "x.html")).Exit);
        Assert.Equal(AexpCli.Usage, (await project.RunAsync("benefit", "--markdown")).Exit);

        // An empty store: no runs in either arm, the verdict for lack of runs, no difference.
        await project.OkAsync("benefit", "--markdown", "empty.md");
        var page = await File.ReadAllTextAsync(Path.Combine(project.Root, "empty.md"));
        Assert.Contains("| treatment | 0 | 0 | n/a |", page);
        Assert.Contains("- Difference (treatment minus holdout): n/a, 95% interval n/a", page);
        Assert.Contains("for lack of runs", page);
    }

    /// <summary>One turn per entry: a prompt, a write, then Stop with the tests passing or failing as given.</summary>
    private static async Task TurnsAsync(TempProject project, string session, bool[] passes)
    {
        for (var turn = 0; turn < passes.Length; turn++)
        {
            // A treatment session's prompt is answered with the block; a holdout session's with nothing.
            await HookAsync(project, "UserPromptSubmit", Session(session, Task + " turn " + turn));
            Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
            var fixedFile = Path.Combine(project.Root, "fixed");
            if (passes[turn])
            {
                project.Fix();
            }
            else
            {
                File.Delete(fixedFile);
            }

            var stop = (string?)(await HookAsync(project, "Stop", Session(session)))?["systemMessage"];
            Assert.Contains(passes[turn] ? "exit 0 (pass)" : "exit 1", stop);
        }

        project.Fix();
    }

    private static string GoldenPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Goldens", "benefit-results.md");
}
