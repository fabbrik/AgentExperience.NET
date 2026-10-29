using System.Globalization;

namespace AgentExperience.CiTriage.Scripted;

/// <summary>
/// The fake history <c>--scripted</c> replays: sixteen compile failures over sixteen days, each fixed the same day (one
/// in five by a file the failing commit did not touch, so the stand-in gets it wrong), plus one flaky failure, one whose
/// fix touched twelve files, one still waiting for its fix, and one whose log has expired.
/// </summary>
public static class ScriptedScenario
{
    public static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public static FakeCi Create()
    {
        var ci = new FakeCi();
        ci.AddRun("main", "main-0", "success", Start.AddHours(1));
        for (var i = 0; i < 16; i++)
        {
            var area = "Area" + (i % 4).ToString(CultureInfo.InvariantCulture);
            var n = i.ToString(CultureInfo.InvariantCulture);
            var source = $"src/AgentExperience.Core/{area}/{area}Options.cs";
            var test = $"tests/AgentExperience.Core.Tests/{area}Tests.cs";
            var fix = i % 5 == 4 ? $"src/AgentExperience.Core/{area}/{area}Service.cs" : source;
            ci.AddFailure(
                branch: "feat/" + n,
                failSha: "fail-" + n,
                failedAt: Start.AddDays(i).AddHours(10),
                changedFiles: [source, test],
                log: CompileLog(area),
                greenSha: "green-" + n,
                greenAt: Start.AddDays(i).AddHours(18),
                fixFiles: [fix]);
        }

        ci.AddFailure("feat/flaky", "flaky-1", Start.AddDays(3).AddHours(12), ["src/AgentExperience.Core/Area3/Area3Options.cs"],
            "##[error]The operation timed out after 100 seconds waiting for the PostgreSQL container.\n", "flaky-1", Start.AddDays(3).AddHours(14));
        ci.AddFailure("feat/big", "big-1", Start.AddDays(5).AddHours(12), ["README.md"], CompileLog("Area1"), "big-2", Start.AddDays(5).AddHours(20),
            [.. Enumerable.Range(0, 12).Select(k => "docs/page" + k.ToString(CultureInfo.InvariantCulture) + ".md")]);
        ci.AddFailure("feat/waiting", "wait-1", Start.AddDays(15).AddHours(23), ["src/AgentExperience.Core/Area2/Area2Options.cs"], CompileLog("Area2"));
        ci.AddFailure("feat/expired", "old-1", Start.AddDays(7).AddHours(12), ["src/AgentExperience.Core/Area0/Area0Options.cs"], null, "old-2", Start.AddDays(7).AddHours(13),
            ["src/AgentExperience.Core/Area0/Area0Options.cs"]);
        return ci;
    }

    /// <summary>A compile failure as GitHub's log shows it, timestamps and all.</summary>
    public static string CompileLog(string area) =>
        "2026-09-01T10:00:00.0000000Z Restore complete.\n"
        + $"2026-09-01T10:00:01.0000000Z /home/runner/work/x/src/AgentExperience.Core/{area}/{area}Options.cs(42,17): error CS0103: The name '{area}Limit' does not exist in the current context\n"
        + "2026-09-01T10:00:02.0000000Z Build FAILED.\n"
        + "2026-09-01T10:00:03.0000000Z ##[error]Process completed with exit code 1.\n";
}
