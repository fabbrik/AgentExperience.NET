using AgentExperience.Abstractions;

namespace AgentExperience.Core.Verification;

/// <summary>
/// The mechanical, observable status a workflow reported for itself, as input to
/// <see cref="TaskCheckEvaluators.WorkflowCompletion"/>. Deliberately its own three-value shape
/// (not reused from <see cref="RunExecutionStatus"/>): a workflow's own completion signal is a
/// distinct observable from the run's own execution status.
/// </summary>
public enum WorkflowCompletionStatus
{
    /// <summary>The workflow reported that it completed successfully.</summary>
    Completed,

    /// <summary>The workflow reported that it failed.</summary>
    Failed,

    /// <summary>The workflow's completion status could not be observed.</summary>
    Unknown,
}

/// <summary>
/// A human principal's decision, as input to both <see cref="TaskCheckEvaluators.HumanApproval"/>
/// and <see cref="TaskCheckEvaluators.HumanCorrection"/>. Shared across both evaluators because
/// both checks reduce to the same three-value shape: the human explicitly approved (no correction
/// needed), explicitly rejected/required a correction, or has not yet decided.
/// </summary>
public enum HumanDecision
{
    /// <summary>The human principal explicitly approved.</summary>
    Approved,

    /// <summary>The human principal explicitly rejected (or required a correction).</summary>
    Rejected,

    /// <summary>The human principal has not yet reached a decision.</summary>
    Pending,
}

/// <summary>
/// Five pure, static, evidence-producing evaluators -- one per deterministic check kind named on
/// <see cref="Evidence.Kind"/>'s doc comment: tool exit code, test result, workflow completion,
/// human approval, and human correction. Each function maps its input deterministically to a
/// <see cref="CheckResult"/> (no LLM, no I/O, no randomness, no wall-clock reads -- every identity
/// and timestamp is caller-supplied, mirroring every other capture/identity call in this codebase)
/// and returns exactly one <see cref="Evidence"/>. None of the five ever produces a false
/// <see cref="CheckResult.Pass"/>/<see cref="CheckResult.Fail"/> from absent input: a missing
/// observable (a <see langword="null"/> exit code or test result, an explicit
/// <see cref="WorkflowCompletionStatus.Unknown"/>/<see cref="HumanDecision.Pending"/>) always maps
/// to <see cref="CheckResult.Unknown"/>, never silently defaulted to a pass.
/// </summary>
/// <remarks>
/// <b>An evaluator's own internal failure is caught and reported as <see cref="CheckResult.Unknown"/>
/// evidence with a safe diagnostic</b> (a Boundary this story establishes): <see cref="WorkflowCompletion"/>,
/// <see cref="HumanApproval"/>, and <see cref="HumanCorrection"/> classify a caller-supplied enum
/// value; an enum value outside the ones explicitly handled here (e.g. an unchecked numeric cast)
/// is the one way one of these otherwise-pure functions could fail internally. That failure is
/// caught here, inside the evaluator, rather than left to propagate -- the returned
/// <see cref="Evidence"/> still carries the right <c>CheckId</c>/<c>Producer</c> and a content-free
/// diagnostic <c>Detail</c>, exactly like any other <see cref="CheckResult.Unknown"/> result, so an
/// aggregator downstream needs no special case for it. <see cref="ExitCode"/> and
/// <see cref="TestResult"/> have no analogous invalid-input case to guard (their inputs are already
/// exhaustively either a concrete value or <see langword="null"/>, and <see langword="null"/> is
/// itself a legitimate, non-failure <see cref="CheckResult.Unknown"/> case), so they are not
/// wrapped the same way. A <see langword="null"/>/empty required argument (<c>checkId</c>,
/// <c>artifactRevision</c>, <c>producer</c>) is genuinely invalid input, not an internal failure --
/// it throws, consistent with this codebase's convention that an exception is reserved for a
/// caller error, never silently swallowed into a fabricated result.
/// </remarks>
public static class TaskCheckEvaluators
{
    private const string ToolExitCodeKind = "ToolExitCode";
    private const string TestResultKind = "TestResult";
    private const string WorkflowCompletionKind = "WorkflowCompletion";
    private const string HumanApprovalKind = "HumanApproval";
    private const string HumanCorrectionKind = "HumanCorrection";

    /// <summary>
    /// Evaluates a tool's observed process exit code against an expected value.
    /// <paramref name="exitCode"/> equal to <paramref name="expected"/> is <see cref="CheckResult.Pass"/>;
    /// a different, observed exit code is <see cref="CheckResult.Fail"/>; a <see langword="null"/>
    /// (unobserved) exit code is <see cref="CheckResult.Unknown"/> -- an absent exit code is never
    /// treated as a pass.
    /// </summary>
    /// <param name="evidenceId">Unique identifier for the produced evidence.</param>
    /// <param name="checkId">The required check ID this evidence backs.</param>
    /// <param name="verificationRoundId">The verification round this evidence is produced in.</param>
    /// <param name="artifactRevision">The artifact revision this evidence applies to.</param>
    /// <param name="producer">Identity of whatever observed the exit code (a tool, an evaluator name, or a host component).</param>
    /// <param name="capturedAt">When this evidence was captured.</param>
    /// <param name="exitCode">The observed process exit code, or <see langword="null"/> if none was observed.</param>
    /// <param name="expected">The exit code that counts as success; defaults to <c>0</c>.</param>
    public static Evidence ExitCode(
        Guid evidenceId,
        string checkId,
        Guid verificationRoundId,
        string artifactRevision,
        string producer,
        DateTimeOffset capturedAt,
        int? exitCode,
        int expected = 0)
    {
        ValidateRequiredArguments(checkId, artifactRevision, producer);

        var (result, detail) = exitCode is null
            ? (CheckResult.Unknown, "No exit code was observed.")
            : exitCode.Value == expected
                ? (CheckResult.Pass, $"Exit code {exitCode.Value} matched the expected {expected}.")
                : (CheckResult.Fail, $"Exit code {exitCode.Value} did not match the expected {expected}.");

        return new Evidence(evidenceId, verificationRoundId, artifactRevision, checkId, ToolExitCodeKind, result, producer, detail, capturedAt);
    }

    /// <summary>
    /// Evaluates a test's observed pass/fail outcome. <paramref name="passed"/> of
    /// <see langword="true"/> is <see cref="CheckResult.Pass"/>; <see langword="false"/> is
    /// <see cref="CheckResult.Fail"/>; <see langword="null"/> (no test result observed) is
    /// <see cref="CheckResult.Unknown"/>.
    /// </summary>
    /// <param name="evidenceId">Unique identifier for the produced evidence.</param>
    /// <param name="checkId">The required check ID this evidence backs.</param>
    /// <param name="verificationRoundId">The verification round this evidence is produced in.</param>
    /// <param name="artifactRevision">The artifact revision this evidence applies to.</param>
    /// <param name="producer">Identity of whatever produced the test result (e.g. a CI test runner).</param>
    /// <param name="capturedAt">When this evidence was captured.</param>
    /// <param name="passed">Whether the test passed, or <see langword="null"/> if no result was observed.</param>
    public static Evidence TestResult(
        Guid evidenceId,
        string checkId,
        Guid verificationRoundId,
        string artifactRevision,
        string producer,
        DateTimeOffset capturedAt,
        bool? passed)
    {
        ValidateRequiredArguments(checkId, artifactRevision, producer);

        var (result, detail) = passed switch
        {
            true => (CheckResult.Pass, "Test result: passed."),
            false => (CheckResult.Fail, "Test result: failed."),
            null => (CheckResult.Unknown, "No test result was observed."),
        };

        return new Evidence(evidenceId, verificationRoundId, artifactRevision, checkId, TestResultKind, result, producer, detail, capturedAt);
    }

    /// <summary>
    /// Evaluates a workflow's own reported completion status.
    /// <see cref="WorkflowCompletionStatus.Completed"/> is <see cref="CheckResult.Pass"/>;
    /// <see cref="WorkflowCompletionStatus.Failed"/> is <see cref="CheckResult.Fail"/>;
    /// <see cref="WorkflowCompletionStatus.Unknown"/> is <see cref="CheckResult.Unknown"/>. An
    /// out-of-range <paramref name="status"/> value is an evaluator-internal failure: caught and
    /// reported as <see cref="CheckResult.Unknown"/> with a safe diagnostic rather than thrown (see
    /// this type's remarks).
    /// </summary>
    /// <param name="evidenceId">Unique identifier for the produced evidence.</param>
    /// <param name="checkId">The required check ID this evidence backs.</param>
    /// <param name="verificationRoundId">The verification round this evidence is produced in.</param>
    /// <param name="artifactRevision">The artifact revision this evidence applies to.</param>
    /// <param name="producer">Identity of whatever reported the workflow's completion status.</param>
    /// <param name="capturedAt">When this evidence was captured.</param>
    /// <param name="status">The workflow's observed completion status.</param>
    public static Evidence WorkflowCompletion(
        Guid evidenceId,
        string checkId,
        Guid verificationRoundId,
        string artifactRevision,
        string producer,
        DateTimeOffset capturedAt,
        WorkflowCompletionStatus status)
    {
        ValidateRequiredArguments(checkId, artifactRevision, producer);

        var (result, detail) = ClassifySafely(
            () => status switch
            {
                WorkflowCompletionStatus.Completed => (CheckResult.Pass, $"Workflow completion status: {status}."),
                WorkflowCompletionStatus.Failed => (CheckResult.Fail, $"Workflow completion status: {status}."),
                WorkflowCompletionStatus.Unknown => (CheckResult.Unknown, $"Workflow completion status: {status}."),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unrecognized workflow completion status."),
            },
            "workflow completion status");

        return new Evidence(evidenceId, verificationRoundId, artifactRevision, checkId, WorkflowCompletionKind, result, producer, detail, capturedAt);
    }

    /// <summary>
    /// Evaluates a human principal's approval decision. <see cref="HumanDecision.Approved"/> is
    /// <see cref="CheckResult.Pass"/>; <see cref="HumanDecision.Rejected"/> is
    /// <see cref="CheckResult.Fail"/>; <see cref="HumanDecision.Pending"/> is
    /// <see cref="CheckResult.Unknown"/>. An out-of-range <paramref name="decision"/> value is an
    /// evaluator-internal failure: caught and reported as <see cref="CheckResult.Unknown"/> with a
    /// safe diagnostic rather than thrown (see this type's remarks).
    /// </summary>
    /// <param name="evidenceId">Unique identifier for the produced evidence.</param>
    /// <param name="checkId">The required check ID this evidence backs.</param>
    /// <param name="verificationRoundId">The verification round this evidence is produced in.</param>
    /// <param name="artifactRevision">The artifact revision this evidence applies to.</param>
    /// <param name="producer">Identity of the human principal (or the system recording their decision) who decided.</param>
    /// <param name="capturedAt">When this evidence was captured.</param>
    /// <param name="decision">The human principal's approval decision.</param>
    public static Evidence HumanApproval(
        Guid evidenceId,
        string checkId,
        Guid verificationRoundId,
        string artifactRevision,
        string producer,
        DateTimeOffset capturedAt,
        HumanDecision decision) =>
        FromHumanDecision(evidenceId, checkId, verificationRoundId, artifactRevision, HumanApprovalKind, producer, capturedAt, decision);

    /// <summary>
    /// Evaluates a human principal's correction decision (e.g. did a human need to correct the
    /// artifact, or explicitly accept it as-is). Shares <see cref="HumanDecision"/> and the same
    /// mapping as <see cref="HumanApproval"/>: <see cref="HumanDecision.Approved"/> is
    /// <see cref="CheckResult.Pass"/>, <see cref="HumanDecision.Rejected"/> is
    /// <see cref="CheckResult.Fail"/>, <see cref="HumanDecision.Pending"/> is
    /// <see cref="CheckResult.Unknown"/>; an out-of-range value is caught and reported as
    /// <see cref="CheckResult.Unknown"/> with a safe diagnostic (see this type's remarks). Kept as
    /// a distinct evaluator (rather than an alias for <see cref="HumanApproval"/>) because it backs
    /// a distinct <see cref="Evidence.Kind"/> ("HumanCorrection") for a distinct required check.
    /// </summary>
    /// <param name="evidenceId">Unique identifier for the produced evidence.</param>
    /// <param name="checkId">The required check ID this evidence backs.</param>
    /// <param name="verificationRoundId">The verification round this evidence is produced in.</param>
    /// <param name="artifactRevision">The artifact revision this evidence applies to.</param>
    /// <param name="producer">Identity of the human principal (or the system recording their decision) who decided.</param>
    /// <param name="capturedAt">When this evidence was captured.</param>
    /// <param name="decision">The human principal's correction decision.</param>
    public static Evidence HumanCorrection(
        Guid evidenceId,
        string checkId,
        Guid verificationRoundId,
        string artifactRevision,
        string producer,
        DateTimeOffset capturedAt,
        HumanDecision decision) =>
        FromHumanDecision(evidenceId, checkId, verificationRoundId, artifactRevision, HumanCorrectionKind, producer, capturedAt, decision);

    private static Evidence FromHumanDecision(
        Guid evidenceId,
        string checkId,
        Guid verificationRoundId,
        string artifactRevision,
        string kind,
        string producer,
        DateTimeOffset capturedAt,
        HumanDecision decision)
    {
        ValidateRequiredArguments(checkId, artifactRevision, producer);

        var (result, detail) = ClassifySafely(
            () => decision switch
            {
                HumanDecision.Approved => (CheckResult.Pass, $"Human decision: {decision}."),
                HumanDecision.Rejected => (CheckResult.Fail, $"Human decision: {decision}."),
                HumanDecision.Pending => (CheckResult.Unknown, $"Human decision: {decision}."),
                _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unrecognized human decision."),
            },
            "human decision");

        return new Evidence(evidenceId, verificationRoundId, artifactRevision, checkId, kind, result, producer, detail, capturedAt);
    }

    /// <summary>
    /// Runs <paramref name="classify"/> and, if it throws <see cref="ArgumentOutOfRangeException"/>
    /// (the one documented failure mode: an unrecognized enum value), reports it as a
    /// <see cref="CheckResult.Unknown"/> result with a content-free diagnostic (never the raw
    /// exception message, which could otherwise leak an unsanitized value baked into a future,
    /// more complex classifier) -- this is the "evaluator's own internal failure is caught" Boundary,
    /// applied uniformly to every evaluator classifying a caller-supplied enum. Any other exception
    /// is a genuine bug and propagates rather than being disguised as ordinary Unknown evidence.
    /// </summary>
    private static (CheckResult Result, string Detail) ClassifySafely(Func<(CheckResult, string)> classify, string whatFailed)
    {
        try
        {
            return classify();
        }
        catch (ArgumentOutOfRangeException)
        {
            return (CheckResult.Unknown, $"Evaluator failed to classify the observed {whatFailed}; treated as Unknown.");
        }
    }

    /// <summary>Genuinely invalid input (a null/empty required argument) throws, per this codebase's convention -- never silently swallowed into a fabricated result.</summary>
    private static void ValidateRequiredArguments(string checkId, string artifactRevision, string producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
    }
}
