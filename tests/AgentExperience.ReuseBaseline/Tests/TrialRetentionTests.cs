using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// Errors, timeouts and retrieval failures are retained in the report, each classified, each still
/// counted in its condition's sample size.
/// </summary>
/// <remarks>
/// The 4.2 sample throws on any deviation, which is right for a demonstration and wrong here: a
/// harness that discards the trials that went wrong is a harness whose numbers describe a subset
/// nobody declared.
/// </remarks>
public class TrialRetentionTests
{
    [Fact]
    public async Task An_errored_trial_is_retained_classified_and_counted()
    {
        var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            FaultAt = index => index == 3 ? new TrialFault(TrialFaultKind.Throw) : null,
        });

        Assert.Equal(result.Preregistration.Design.TrialCount, result.Trials.Count);

        var errored = result.Trials.Single(trial => trial.Index == 3);
        Assert.Equal(TrialStatus.Errored, errored.Status);
        Assert.Equal("InvalidOperationException", errored.FailureClassification);

        // No value for the metrics a half-finished run cannot supply -- including the denial count,
        // which a trial killed mid-attempt would report truncated and which would then dilute the
        // guardrail mean towards passing.
        Assert.Null(errored.Metrics.FailedAttempts);
        Assert.Null(errored.Metrics.VerifiedSuccess);
        Assert.Null(errored.Metrics.ToolCalls);
        Assert.Null(errored.Metrics.UnauthorizedToolExecutions);

        // ... and a value for the one it can: elapsed time is complete whatever happened.
        Assert.NotNull(errored.Metrics.ElapsedMilliseconds);
        Assert.Equal(5, result.Gate.Enabled.UnauthorizedToolExecutions.Observations);

        // Still counted in its condition's trial total, and still in the rendered report.
        Assert.Equal(6, result.Gate.Enabled.Trials);
        Assert.Equal(1, result.Gate.Enabled.Errored);
        Assert.Equal(5, result.Gate.Enabled.FailedAttempts.Observations);
        Assert.Contains("Errored", ReuseBaselineReport.RenderDeterministic(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timed_out_trial_is_retained_and_classified_distinctly_from_an_error()
    {
        // On a clock that moves only when trial 2 hangs, so no clean trial can also time out under
        // load and make the counts below depend on the machine.
        var result = await ReuseBaselineExperiment.RunAsync(ManualDeadlineClock.Drive(TimeSpan.FromMilliseconds(250), new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            FaultAt = index => index == 2 ? new TrialFault(TrialFaultKind.Timeout) : null,
        }));

        var timedOut = result.Trials.Single(trial => trial.Index == 2);

        Assert.Equal(TrialStatus.TimedOut, timedOut.Status);
        Assert.NotEqual(TrialStatus.Errored, timedOut.Status);
        Assert.Contains("deadline", timedOut.FailureClassification!, StringComparison.Ordinal);
        Assert.Equal(1, result.Gate.Disabled.TimedOut);
        Assert.Equal(0, result.Gate.Disabled.Errored);
        Assert.Contains("TimedOut", ReuseBaselineReport.RenderDeterministic(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_memory_enabled_trial_whose_retrieval_failed_keeps_its_condition_and_is_recorded_as_a_retrieval_failure()
    {
        var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            FaultAt = index => index == 1 ? new TrialFault(TrialFaultKind.RetrievalFailure) : null,
        });

        var failed = result.Trials.Single(trial => trial.Index == 1);

        // Not silently reclassified: it is still a memory-enabled trial, and it says what went wrong.
        Assert.Equal(TrialCondition.MemoryEnabled, failed.Condition);
        Assert.Equal("InjectionOutcome.RetrievalFailed", failed.RetrievalFailure);
        Assert.Empty(failed.ExposedExperienceIds);
        Assert.Null(failed.FeedbackId);

        // It still ran, so it still has a primary-metric value -- it simply had nothing injected.
        Assert.Equal(TrialStatus.Completed, failed.Status);
        Assert.NotNull(failed.Metrics.FailedAttempts);

        Assert.Equal(1, result.Gate.Enabled.RetrievalFailures);

        var report = ReuseBaselineReport.RenderDeterministic(result);
        Assert.Contains("retrieval did not complete: InjectionOutcome.RetrievalFailed", report, StringComparison.Ordinal);
        Assert.Contains("keeps its condition", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two readings of "did this trial resolve its task" disagreeing, and the report saying so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cross-check could be deleted whole and every other test would still pass: the report's
    /// "agreed on every trial" would become vacuously true and the disagreement branch would be dead
    /// code. Here the resolving attempt's evidence is filed in the round the host never closes, so
    /// the verification aggregator cannot verify a run whose final exit code was zero.
    /// </para>
    /// <para>
    /// It is worth being clear about what this shows and what it does not. The two readings are not
    /// independent -- both start from the same recorded exit code -- so this catches evidence
    /// handling and nothing about the observation itself. That is what the report now says.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_two_readings_of_a_trial_can_disagree_and_the_report_names_the_trial()
    {
        var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            FaultAt = index => index == 5 ? new TrialFault(TrialFaultKind.MisfiledEvidence) : null,
        });

        var trial = result.Trials.Single(trial => trial.Index == 5);

        // The deterministic task check says the incident was resolved; the aggregator, working from
        // evidence that never reached a closed round, says it was not verified.
        Assert.Equal(TrialStatus.Completed, trial.Status);
        Assert.False(trial.Metrics.VerifiedSuccess);
        Assert.Equal([5], result.CheckDisagreements);

        var report = ReuseBaselineReport.RenderDeterministic(result);
        Assert.Contains("disagreed on trial(s) 5", report, StringComparison.Ordinal);
        Assert.Contains("NOT independent observations", report, StringComparison.Ordinal);

        // And the guardrail noticed: the memory-enabled condition lost a verified success, which is
        // the direction that makes the gate refuse the arm.
        Assert.Equal(5d / 6d, result.Gate.Enabled.VerifiedSuccessRate);
        Assert.Equal(1d, result.Gate.Disabled.VerifiedSuccessRate);
        Assert.Equal(GateVerdict.NoDemonstratedBenefit, result.Gate.Verdict);
    }

    [Fact]
    public async Task Every_trial_in_the_reference_run_appears_in_the_report()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var report = ReuseBaselineReport.RenderDeterministic(result);

        foreach (var trial in result.Trials)
        {
            Assert.Contains(trial.RunId.ToString("D"), report, StringComparison.Ordinal);
            Assert.Contains(trial.VerificationRoundId.ToString("D"), report, StringComparison.Ordinal);
        }
    }
}
