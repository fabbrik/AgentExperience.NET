using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The recall hint's fixed shape: the failure it answers, the calls of the attempt that worked rendered as the block's
/// <c>Tried:</c> line renders them, the confidence; records dropped whole to fit; borrowed records only under a grant that
/// shows the approach, and without per-call markers; unconfirmed content fenced; nothing of the raw error or result.
/// </summary>
public class HistoricalReferenceRecallHintTests
{
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");

    private static ToolCallRecord Call(int sequence, string fix, string? error = null) => new(
        ToolCallId: Guid.NewGuid(),
        SequenceNumber: sequence,
        ToolName: "run_tests",
        Arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["fix"] = fix, ["apiKey"] = InjectionRecords.SecretArgument },
        StartedAt: InjectionRecords.Now,
        Duration: TimeSpan.FromMilliseconds(5),
        Result: error is null ? InjectionRecords.RawResult : null,
        Error: error);

    private static ExperienceRecord FailedThenWorked(int id = 1, double confidence = 0.8) =>
        InjectionRecords.Record(InjectionRecords.Id(id), TestScope, confidence: confidence, attempts:
        [
            new Attempt(Guid.NewGuid(), 1, InjectionRecords.Now, TimeSpan.FromSeconds(1),
                [Call(0, "retry", error: "exit 2 " + InjectionRecords.RawError), Call(1, "pin-version")], InjectionRecords.RawResult, null),
        ]);

    private static readonly ApproachArgumentAllowlist Allowlist = ApproachArgumentAllowlist.From(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["run_tests"] = ["fix"] },
        "allowlist");

    private static string? Hint(
        IReadOnlyList<RankedExperience> records,
        ExperienceSignatureMatch match = ExperienceSignatureMatch.Exact,
        int maxBytes = ExperienceRecallOptions.DefaultMaxHintBytes,
        Func<ExperienceRecord, bool>? confirmed = null) =>
        Hint(records, out _, out _, match, maxBytes, confirmed);

    private static string? Hint(
        IReadOnlyList<RankedExperience> records,
        out List<Guid> included,
        out List<Guid> overBudget,
        ExperienceSignatureMatch match = ExperienceSignatureMatch.Exact,
        int maxBytes = ExperienceRecallOptions.DefaultMaxHintBytes,
        Func<ExperienceRecord, bool>? confirmed = null) =>
        HistoricalReferenceWriter.RecallHint(records, "run_tests", "exit 2", match, Allowlist, confirmed, AttemptFailureDetail.ErrorClass, maxBytes, out included, out _, out overBudget);

    [Fact]
    public void An_own_record_gives_the_fixed_shape()
    {
        var hint = Hint([new RankedExperience(FailedThenWorked(), double.NaN, [])]);

        Assert.Equal(
            HistoricalReferenceWriter.RecallBegin + "\n"
            + "An earlier verified run hit this same failure (run_tests [failed: exit 2]) and then:\n"
            + "  - worked: attempt 1: run_tests(fix=\"retry\") [failed: exit 2], run_tests(fix=\"pin-version\") [returned] → completed\n"
            + "Confidence: 0.800 · Verified\n"
            + HistoricalReferenceWriter.BlockEnd + "\n",
            hint);
        Assert.DoesNotContain(InjectionRecords.RawError, hint, StringComparison.Ordinal);
        Assert.DoesNotContain(InjectionRecords.RawResult, hint, StringComparison.Ordinal);
        Assert.DoesNotContain(InjectionRecords.SecretArgument, hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_coarse_match_says_it_is_only_alike()
    {
        var hint = Hint([new RankedExperience(FailedThenWorked(), double.NaN, [])], ExperienceSignatureMatch.Coarse);

        Assert.Contains("hit a failure like this one (the same tool and error class: run_tests [failed: exit 2])", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_that_do_not_fit_are_dropped_whole_and_none_fitting_gives_no_hint()
    {
        var first = new RankedExperience(FailedThenWorked(1), double.NaN, []);
        var second = new RankedExperience(FailedThenWorked(2), double.NaN, []);
        var one = Hint([first])!;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(one);

        Assert.Equal(one, Hint([first, second], out var included, out var overBudget, maxBytes: bytes));
        Assert.Equal([first.Record.ExperienceId], included);
        Assert.Equal([second.Record.ExperienceId], overBudget);

        Assert.Null(Hint([first], out included, out overBudget, maxBytes: bytes - 1));
        Assert.Empty(included);
        Assert.Equal([first.Record.ExperienceId], overBudget);
    }

    [Theory]
    [InlineData(ExperienceGrantDisclosure.LessonOnly, false)]
    [InlineData(ExperienceGrantDisclosure.LessonAndApproach, true)]
    [InlineData(ExperienceGrantDisclosure.LessonApproachAndArguments, true)]
    public void A_borrowed_record_gives_a_hint_only_when_its_grant_shows_the_approach_and_never_its_failures(ExperienceGrantDisclosure level, bool hinted)
    {
        var borrowed = new RankedExperience(FailedThenWorked(), double.NaN, [], SharedByGrant: true, PermittingGrantId: Guid.NewGuid(), GrantDisclosure: level);

        var hint = Hint([borrowed]);

        if (!hinted)
        {
            Assert.Null(hint);
            return;
        }

        Assert.NotNull(hint);
        Assert.Contains("Shared: ", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("[failed", hint[hint.IndexOf(HistoricalReferenceWriter.RecallWorkedPrefix, StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("[returned]", hint, StringComparison.Ordinal);

        // No owner allowlist was given, so a borrowed value is shown under neither level.
        Assert.DoesNotContain("pin-version", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_no_verified_working_attempt_gives_no_hint()
    {
        var unverified = FailedThenWorked() with { Outcome = FailedThenWorked().Outcome with { Status = TaskVerificationStatus.Failed } };

        Assert.Null(Hint([new RankedExperience(unverified, double.NaN, [])]));
    }

    [Fact]
    public void Unconfirmed_content_is_fenced()
    {
        var hint = Hint([new RankedExperience(FailedThenWorked(), double.NaN, [])], confirmed: _ => false)!;

        var fence = hint.IndexOf(HistoricalReferenceWriter.ModelAuthoredLine, StringComparison.Ordinal);
        var worked = hint.IndexOf(HistoricalReferenceWriter.RecallWorkedPrefix, StringComparison.Ordinal);
        var end = hint.IndexOf(HistoricalReferenceWriter.ModelAuthoredEndLine, StringComparison.Ordinal);
        Assert.True(fence >= 0 && fence < worked && worked < end);
    }
}
