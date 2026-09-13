namespace AgentExperience.Core.Tests;

/// <summary>
/// Exercises Story 1.5's AC1: each of the five deterministic evaluators
/// (<see cref="TaskCheckEvaluators.ExitCode"/>, <see cref="TaskCheckEvaluators.TestResult"/>,
/// <see cref="TaskCheckEvaluators.WorkflowCompletion"/>, <see cref="TaskCheckEvaluators.HumanApproval"/>,
/// <see cref="TaskCheckEvaluators.HumanCorrection"/>) emits Pass, Fail, or Unknown with the evidence
/// carrying the right <c>CheckId</c>/<c>Kind</c>/<c>Producer</c> -- so an unrelated passing check can
/// never satisfy a different required <c>CheckId</c>, since <c>Evidence.CheckId</c> is exactly what
/// ties evidence to the check it backs. Also covers the "evaluator's own internal failure is caught
/// and reported as Unknown evidence with a safe diagnostic" Boundary for the three evaluators that
/// classify a caller-supplied enum, and that genuinely invalid required input (a null/empty
/// <c>CheckId</c>/revision/producer) throws rather than silently producing a fabricated result.
/// </summary>
public class TaskCheckEvaluatorsTests
{
    private static readonly Guid ExpectedEvidenceId = Guid.NewGuid();
    private static readonly Guid ExpectedRoundId = Guid.NewGuid();
    private static readonly DateTimeOffset ExpectedCapturedAt = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private const string ExpectedArtifactRevision = "rev-1";
    private const string ExpectedCheckId = "build-succeeds";
    private const string ExpectedProducer = "ci-runner";

    private static void AssertCommonShape(Evidence evidence, string expectedKind, CheckResult expectedResult)
    {
        Assert.Equal(ExpectedEvidenceId, evidence.EvidenceId);
        Assert.Equal(ExpectedRoundId, evidence.VerificationRoundId);
        Assert.Equal(ExpectedArtifactRevision, evidence.ArtifactRevision);
        Assert.Equal(ExpectedCheckId, evidence.CheckId);
        Assert.Equal(expectedKind, evidence.Kind);
        Assert.Equal(expectedResult, evidence.Result);
        Assert.Equal(ExpectedProducer, evidence.Producer);
        Assert.Equal(ExpectedCapturedAt, evidence.CapturedAt);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Detail));
    }

    [Theory]
    [InlineData(0, 0, CheckResult.Pass)]
    [InlineData(1, 0, CheckResult.Fail)]
    [InlineData(2, 2, CheckResult.Pass)]
    [InlineData(1, 2, CheckResult.Fail)]
    public void ExitCode_maps_observed_exit_code_against_expected(int exitCode, int expected, CheckResult expectedResult)
    {
        var evidence = TaskCheckEvaluators.ExitCode(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, exitCode, expected);

        AssertCommonShape(evidence, "ToolExitCode", expectedResult);
    }

    [Fact]
    public void ExitCode_with_no_observed_exit_code_is_Unknown_never_a_pass()
    {
        var evidence = TaskCheckEvaluators.ExitCode(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, exitCode: null);

        AssertCommonShape(evidence, "ToolExitCode", CheckResult.Unknown);
    }

    [Theory]
    [InlineData(true, CheckResult.Pass)]
    [InlineData(false, CheckResult.Fail)]
    public void TestResult_maps_observed_pass_or_fail(bool passed, CheckResult expectedResult)
    {
        var evidence = TaskCheckEvaluators.TestResult(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, passed);

        AssertCommonShape(evidence, "TestResult", expectedResult);
    }

    [Fact]
    public void TestResult_with_no_observed_result_is_Unknown()
    {
        var evidence = TaskCheckEvaluators.TestResult(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, passed: null);

        AssertCommonShape(evidence, "TestResult", CheckResult.Unknown);
    }

    [Theory]
    [InlineData(WorkflowCompletionStatus.Completed, CheckResult.Pass)]
    [InlineData(WorkflowCompletionStatus.Failed, CheckResult.Fail)]
    [InlineData(WorkflowCompletionStatus.Unknown, CheckResult.Unknown)]
    public void WorkflowCompletion_maps_reported_status(WorkflowCompletionStatus status, CheckResult expectedResult)
    {
        var evidence = TaskCheckEvaluators.WorkflowCompletion(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, status);

        AssertCommonShape(evidence, "WorkflowCompletion", expectedResult);
    }

    [Fact]
    public void WorkflowCompletion_with_an_unrecognized_status_is_caught_internally_and_reported_as_Unknown_with_a_safe_diagnostic()
    {
        var invalidStatus = (WorkflowCompletionStatus)999;

        var evidence = TaskCheckEvaluators.WorkflowCompletion(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, invalidStatus);

        AssertCommonShape(evidence, "WorkflowCompletion", CheckResult.Unknown);
        // Safe/content-free diagnostic: never reflects the raw invalid numeric value.
        Assert.DoesNotContain("999", evidence.Detail);
    }

    [Theory]
    [InlineData(HumanDecision.Approved, CheckResult.Pass)]
    [InlineData(HumanDecision.Rejected, CheckResult.Fail)]
    [InlineData(HumanDecision.Pending, CheckResult.Unknown)]
    public void HumanApproval_maps_human_decision(HumanDecision decision, CheckResult expectedResult)
    {
        var evidence = TaskCheckEvaluators.HumanApproval(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, decision);

        AssertCommonShape(evidence, "HumanApproval", expectedResult);
    }

    [Theory]
    [InlineData(HumanDecision.Approved, CheckResult.Pass)]
    [InlineData(HumanDecision.Rejected, CheckResult.Fail)]
    [InlineData(HumanDecision.Pending, CheckResult.Unknown)]
    public void HumanCorrection_maps_human_decision(HumanDecision decision, CheckResult expectedResult)
    {
        var evidence = TaskCheckEvaluators.HumanCorrection(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, decision);

        AssertCommonShape(evidence, "HumanCorrection", expectedResult);
    }

    [Fact]
    public void HumanApproval_and_HumanCorrection_with_an_unrecognized_decision_are_caught_internally_and_reported_as_Unknown()
    {
        var invalidDecision = (HumanDecision)999;

        var approval = TaskCheckEvaluators.HumanApproval(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, invalidDecision);
        var correction = TaskCheckEvaluators.HumanCorrection(ExpectedEvidenceId, ExpectedCheckId, ExpectedRoundId, ExpectedArtifactRevision, ExpectedProducer, ExpectedCapturedAt, invalidDecision);

        AssertCommonShape(approval, "HumanApproval", CheckResult.Unknown);
        AssertCommonShape(correction, "HumanCorrection", CheckResult.Unknown);
    }

    [Theory]
    [InlineData(null, "rev-1", "producer")]
    [InlineData("", "rev-1", "producer")]
    [InlineData("check-id", null, "producer")]
    [InlineData("check-id", "", "producer")]
    [InlineData("check-id", "rev-1", null)]
    [InlineData("check-id", "rev-1", "   ")]
    public void Evaluators_throw_on_a_null_or_empty_required_argument_rather_than_silently_producing_a_result(string? checkId, string? artifactRevision, string? producer)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            TaskCheckEvaluators.ExitCode(ExpectedEvidenceId, checkId!, ExpectedRoundId, artifactRevision!, producer!, ExpectedCapturedAt, exitCode: 0));
        Assert.ThrowsAny<ArgumentException>(() =>
            TaskCheckEvaluators.TestResult(ExpectedEvidenceId, checkId!, ExpectedRoundId, artifactRevision!, producer!, ExpectedCapturedAt, passed: true));
        Assert.ThrowsAny<ArgumentException>(() =>
            TaskCheckEvaluators.WorkflowCompletion(ExpectedEvidenceId, checkId!, ExpectedRoundId, artifactRevision!, producer!, ExpectedCapturedAt, WorkflowCompletionStatus.Completed));
        Assert.ThrowsAny<ArgumentException>(() =>
            TaskCheckEvaluators.HumanApproval(ExpectedEvidenceId, checkId!, ExpectedRoundId, artifactRevision!, producer!, ExpectedCapturedAt, HumanDecision.Approved));
        Assert.ThrowsAny<ArgumentException>(() =>
            TaskCheckEvaluators.HumanCorrection(ExpectedEvidenceId, checkId!, ExpectedRoundId, artifactRevision!, producer!, ExpectedCapturedAt, HumanDecision.Approved));
    }
}
