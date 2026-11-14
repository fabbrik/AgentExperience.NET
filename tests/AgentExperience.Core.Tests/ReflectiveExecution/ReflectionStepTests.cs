using AgentExperience.Core.ReflectiveExecution;

namespace AgentExperience.Core.Tests.ReflectiveExecution;

/// <summary>The structured steps of the loop: the order, what they carry, and that observing never changes the loop.</summary>
public class ReflectionStepTests
{
    private static ReflectionAttemptOutcome Fail() =>
        new(false, "exit 1", null, [new ReflectionFailedCall("build", "exit 1")], 1, null, false);

    private static ReflectionAttemptOutcome Pass() => new(true, null, "ok", [], 1, null, true);

    private static ReflectionEvaluation Fix(string strategy) =>
        new(ReflectionAssessment.NeedsCorrection, [new ReflectionIssue(ReflectionIssueCategory.ToolFailure, "build failed", "exit 1")], strategy, true, 0.8);

    private sealed class Eval(Func<ReflectionEvaluation> f) : IReflectionEvaluator
    {
        public ValueTask<ReflectionEvaluation> EvaluateAsync(ReflectionAttemptContext c, CancellationToken ct) => ValueTask.FromResult(f());
    }

    private sealed class Gate(bool allow) : IRetryAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ReflectionAttemptContext c, ReflectionEvaluation e, CancellationToken ct) => ValueTask.FromResult(allow);
    }

    private static ReflectionAttemptDelegate Script(params ReflectionAttemptOutcome[] outcomes)
    {
        var i = 0;
        return (_, _) => ValueTask.FromResult(outcomes[Math.Min(i++, outcomes.Length - 1)]);
    }

    private static ReflectionExecutionOptions Options(List<ReflectionStep> steps, int maxAttempts = 3) =>
        new() { Enabled = true, SideEffects = SideEffectPolicy.Idempotent, MaxAttempts = maxAttempts, MaxReflectionCycles = maxAttempts - 1, OnStep = steps.Add };

    [Fact]
    public async Task A_corrected_run_reports_every_step_in_order()
    {
        var steps = new List<ReflectionStep>();
        await new ReflectionExecutionCoordinator(Options(steps), new Eval(() => Fix("pin the version")), new Gate(true)).ExecuteAsync("t", Script(Fail(), Pass()));

        Assert.Equal(
            [
                ReflectionStepKind.ExecutionStarted,
                ReflectionStepKind.AttemptCompleted, ReflectionStepKind.VerificationReceived,
                ReflectionStepKind.ReflectionRequested, ReflectionStepKind.EvaluationCompleted, ReflectionStepKind.ReplanningPerformed, ReflectionStepKind.RetryAuthorized,
                ReflectionStepKind.AttemptCompleted, ReflectionStepKind.VerificationReceived,
                ReflectionStepKind.ExecutionStopped,
            ],
            steps.Select(s => s.Kind));
        Assert.Equal(false, steps.First(s => s.Kind == ReflectionStepKind.VerificationReceived).TrustedCheckPassed);
        Assert.Equal(true, steps.Last(s => s.Kind == ReflectionStepKind.VerificationReceived).TrustedCheckPassed);
        Assert.Equal(ReflectionAssessment.NeedsCorrection, steps.Single(s => s.Kind == ReflectionStepKind.EvaluationCompleted).Assessment);
        Assert.Equal(ReflectionStopReason.VerifiedSuccess, steps[^1].StopReason);
    }

    [Fact]
    public async Task A_denied_retry_is_reported_as_denied_and_never_as_authorized()
    {
        var steps = new List<ReflectionStep>();
        await new ReflectionExecutionCoordinator(Options(steps), new Eval(() => Fix("a")), new Gate(false)).ExecuteAsync("t", Script(Fail(), Pass()));

        Assert.Contains(steps, s => s.Kind == ReflectionStepKind.RetryDenied);
        Assert.DoesNotContain(steps, s => s.Kind == ReflectionStepKind.RetryAuthorized);
        Assert.Equal(ReflectionStopReason.RetryDenied, steps[^1].StopReason);
    }

    [Fact]
    public async Task Running_out_of_attempts_reports_the_budget_before_the_stop()
    {
        var steps = new List<ReflectionStep>();
        var n = 0;
        await new ReflectionExecutionCoordinator(Options(steps, maxAttempts: 2), new Eval(() => Fix("fix " + n++)), new Gate(true)).ExecuteAsync("t", Script(Fail()));

        var kinds = steps.Select(s => s.Kind).ToList();
        Assert.Equal(ReflectionStepKind.BudgetExhausted, kinds[^2]);
        Assert.Equal(ReflectionStepKind.ExecutionStopped, kinds[^1]);
        Assert.Equal(steps[^1].StopReason, steps[^2].StopReason);
    }

    [Fact]
    public async Task A_first_attempt_that_passes_asks_for_no_reflection()
    {
        var steps = new List<ReflectionStep>();
        await new ReflectionExecutionCoordinator(Options(steps), new Eval(() => throw new InvalidOperationException()), new Gate(true)).ExecuteAsync("t", Script(Pass()));

        Assert.DoesNotContain(steps, s => s.Kind is ReflectionStepKind.ReflectionRequested or ReflectionStepKind.EvaluationCompleted);
        Assert.Equal(ReflectionStopReason.VerifiedSuccess, steps[^1].StopReason);
    }

    [Fact]
    public async Task An_observer_that_throws_changes_nothing()
    {
        var options = new ReflectionExecutionOptions { Enabled = true, SideEffects = SideEffectPolicy.Idempotent, OnStep = _ => throw new InvalidOperationException("observer") };
        var result = await new ReflectionExecutionCoordinator(options, new Eval(() => Fix("a")), new Gate(true)).ExecuteAsync("t", Script(Fail(), Pass()));

        Assert.Equal(ReflectionStopReason.VerifiedSuccess, result.StopReason);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public void A_step_carries_no_text()
    {
        // The contract is enums, numbers and a nullable flag. A string property here would be a way for text to leak.
        var types = typeof(ReflectionStep).GetProperties().Select(p => p.PropertyType).ToList();
        Assert.DoesNotContain(typeof(string), types);
    }
}
