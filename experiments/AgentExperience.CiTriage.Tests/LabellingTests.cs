using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Scripted;

namespace AgentExperience.CiTriage.Tests;

public sealed class LabellingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static CiRun Run(long id, string branch, string sha, string conclusion, int hour) =>
        new(id, branch, sha, conclusion, T0.AddHours(hour), T0.AddHours(hour).AddMinutes(10), 1);

    [Fact]
    public void The_green_run_is_the_first_later_success_on_the_same_branch()
    {
        var failure = Run(1, "feat/x", "a", "failure", 1);
        var runs = new[]
        {
            failure,
            Run(2, "feat/x", "z", "success", 0),        // earlier
            Run(3, "main", "b", "success", 2),          // another branch
            Run(4, "feat/x", "c", "cancelled", 3),      // not a success
            Run(5, "feat/x", "e", "success", 5),
            Run(6, "feat/x", "d", "success", 4),        // the first later success
        };

        Assert.Equal(6, Labelling.FindGreen(failure, runs)!.Id);
    }

    [Fact]
    public void No_later_green_run_is_pending()
    {
        var failure = Run(1, "feat/x", "a", "failure", 1);
        Assert.Equal(LabelKind.Pending, Labelling.Classify(failure, null, null).Kind);
    }

    [Fact]
    public void A_green_run_on_the_same_commit_is_flaky()
    {
        var failure = Run(1, "feat/x", "a", "failure", 1);
        var label = Labelling.Classify(failure, Run(2, "feat/x", "a", "success", 2), null);
        Assert.Equal(LabelKind.Flaky, label.Kind);
        Assert.Empty(label.FixFiles);
    }

    [Theory]
    [InlineData(1, LabelKind.Code)]
    [InlineData(10, LabelKind.Code)]
    [InlineData(11, LabelKind.Ambiguous)]
    [InlineData(0, LabelKind.Flaky)]
    public void Another_commit_is_code_up_to_ten_fix_files_and_ambiguous_beyond(int files, LabelKind expected)
    {
        var failure = Run(1, "feat/x", "a", "failure", 1);
        var diff = Enumerable.Range(0, files).Select(i => $"src/F{i}.cs").ToList();
        var label = Labelling.Classify(failure, Run(2, "feat/x", "b", "success", 2), diff);
        Assert.Equal(expected, label.Kind);
        Assert.Equal(expected == LabelKind.Code ? files : 0, label.FixFiles.Count);
    }

    [Fact]
    public void An_unreadable_diff_is_unavailable_and_never_triaged()
    {
        var failure = Run(1, "feat/x", "a", "failure", 1);
        var label = Labelling.Classify(failure, Run(2, "feat/x", "b", "success", 2), null);
        Assert.Equal(LabelKind.Unavailable, label.Kind);
        Assert.False(label.Triageable);
    }

    [Fact]
    public async Task Labelling_reads_the_diff_between_the_failing_and_the_green_commit()
    {
        var ci = new FakeCi();
        var at = T0.AddHours(3);
        ci.AddFailure("feat/x", "fail", at, ["src/A.cs"], "error", "green", at.AddHours(1), ["src/B.cs", "src/A.cs"]);
        ci.AddFailure("feat/y", "same", at.AddHours(2), ["src/C.cs"], "error", "same", at.AddHours(3));
        ci.AddFailure("feat/z", "lonely", at.AddHours(4), ["src/D.cs"], "error");

        var failures = await Labelling.LabelAsync(await ci.ListCompletedRunsAsync(default), ci, default);

        Assert.Equal([LabelKind.Code, LabelKind.Flaky, LabelKind.Pending], failures.Select(f => f.Label.Kind));
        Assert.Equal(["src/A.cs", "src/B.cs"], failures[0].Label.FixFiles);
    }
}
