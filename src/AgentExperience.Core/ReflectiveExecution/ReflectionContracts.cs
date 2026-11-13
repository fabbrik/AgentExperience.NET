namespace AgentExperience.Core.ReflectiveExecution;

/// <summary>
/// What an evaluator concluded about one attempt. Advisory: it steers a retry decision and is never verification
/// evidence, so it can never turn a failed or unverified run into a verified one.
/// </summary>
public enum ReflectionAssessment
{
    /// <summary>The evaluator found nothing to correct. Without a passing trusted check the run is still unverified.</summary>
    Satisfied = 0,

    /// <summary>The attempt has a problem a different strategy might fix.</summary>
    NeedsCorrection = 1,

    /// <summary>No strategy within the task's constraints is likely to fix it; the loop stops.</summary>
    Unrecoverable = 2,
}

/// <summary>The kind of problem an evaluator found.</summary>
public enum ReflectionIssueCategory
{
    /// <summary>A tool call failed.</summary>
    ToolFailure = 0,

    /// <summary>An explicit requirement of the task is not met.</summary>
    UnmetRequirement = 1,

    /// <summary>An intermediate or final result is invalid.</summary>
    InvalidResult = 2,

    /// <summary>Information the attempt needed was missing.</summary>
    MissingInformation = 3,

    /// <summary>The attempt contradicts evidence it was given.</summary>
    Contradiction = 4,

    /// <summary>Anything else.</summary>
    Other = 5,
}

/// <summary>One problem an evaluator found, as bounded, screened text.</summary>
/// <param name="Category">The kind of problem.</param>
/// <param name="Description">What is wrong.</param>
/// <param name="Evidence">The observable fact that shows it, or <see langword="null"/>.</param>
public sealed record ReflectionIssue(ReflectionIssueCategory Category, string Description, string? Evidence = null);

/// <summary>
/// A structured evaluation of one attempt. Every text is bounded and screened before the coordinator uses it
/// (<see cref="ReflectionEvaluationValidator"/>), and <see cref="Confidence"/> is the evaluator's own advisory number,
/// never the verified reuse confidence the library stores on a record.
/// </summary>
/// <param name="Assessment">The overall conclusion.</param>
/// <param name="Issues">The problems found.</param>
/// <param name="ProposedStrategy">The corrected strategy, in the evaluator's words, or <see langword="null"/>.</param>
/// <param name="RecommendRetry">Whether the evaluator thinks another attempt is worthwhile.</param>
/// <param name="Confidence">The evaluator's advisory confidence, from 0 to 1.</param>
public sealed record ReflectionEvaluation(
    ReflectionAssessment Assessment,
    IReadOnlyList<ReflectionIssue> Issues,
    string? ProposedStrategy,
    bool RecommendRetry,
    double Confidence);

/// <summary>A tool call that failed in an attempt: its name and error class only, never arguments or error text.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="ErrorClass">The sanitized error class.</param>
public sealed record ReflectionFailedCall(string Tool, string ErrorClass);

/// <summary>How an attempt ended, as far as the host can say without a model's opinion.</summary>
/// <param name="Completed">Whether the agent returned a response (as opposed to throwing).</param>
/// <param name="ErrorClass">The sanitized error class when it threw or failed, otherwise <see langword="null"/>.</param>
/// <param name="ResultExcerpt">A short sanitized excerpt of the response, or <see langword="null"/>.</param>
/// <param name="FailedCalls">The tool calls that failed.</param>
/// <param name="ToolCalls">The number of tool calls the attempt made, or <see langword="null"/> when not measurable.</param>
/// <param name="TokensUsed">The tokens the attempt used, or <see langword="null"/> when the provider did not report them.</param>
/// <param name="TrustedCheckPassed">
/// The host's own verdict (a test command's exit code, a person's approval): <see langword="true"/> passed,
/// <see langword="false"/> failed, <see langword="null"/> when the host has no check for this attempt. This, never a
/// model, is what ends the loop as a verified success.
/// </param>
/// <param name="Transient">Whether the failure is a transient technical one (a timeout, a rate limit) that the same input may survive.</param>
public sealed record ReflectionAttemptOutcome(
    bool Completed,
    string? ErrorClass,
    string? ResultExcerpt,
    IReadOnlyList<ReflectionFailedCall> FailedCalls,
    int? ToolCalls = null,
    long? TokensUsed = null,
    bool? TrustedCheckPassed = null,
    bool Transient = false);

/// <summary>What the coordinator hands the host for one attempt.</summary>
/// <param name="AttemptNumber">One-based.</param>
/// <param name="CorrectionNote">
/// The bounded, screened correction for this attempt, or <see langword="null"/> for the first and for a technical
/// retry. The host must present it as untrusted reference text, never as a system or developer instruction.
/// </param>
/// <param name="IsLastPermittedAttempt">Whether no further attempt is possible after this one, so the host can close the run on it.</param>
public sealed record ReflectionAttemptInput(int AttemptNumber, string? CorrectionNote, bool IsLastPermittedAttempt);

/// <summary>What an evaluator is given about the attempt just made.</summary>
/// <param name="TaskText">The original task text, unchanged across attempts.</param>
/// <param name="AttemptNumber">One-based.</param>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="PreviousStrategies">The corrections already tried, so an evaluator does not propose one again.</param>
public sealed record ReflectionAttemptContext(
    string TaskText,
    int AttemptNumber,
    ReflectionAttemptOutcome Outcome,
    IReadOnlyList<string> PreviousStrategies);

/// <summary>Evaluates one attempt. A deterministic rule set and a model both implement it.</summary>
public interface IReflectionEvaluator
{
    /// <summary>Evaluates the attempt in <paramref name="context"/>.</summary>
    /// <param name="context">The attempt and what was already tried.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The structured evaluation. It is validated before use; an invalid one stops the loop.</returns>
    ValueTask<ReflectionEvaluation> EvaluateAsync(ReflectionAttemptContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The host's gate before every semantic retry. The default (<see cref="DenyAll"/>) denies, so enabling the
/// coordinator never retries without a deliberate choice.
/// </summary>
public interface IRetryAuthorizer
{
    /// <summary>Whether another attempt may run.</summary>
    /// <param name="context">The attempt that just ended.</param>
    /// <param name="evaluation">The validated evaluation that proposed the retry.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns><see langword="true"/> to allow the retry.</returns>
    ValueTask<bool> AuthorizeAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken cancellationToken);

    /// <summary>An authorizer that never allows a retry.</summary>
    public static IRetryAuthorizer DenyAll { get; } = new DenyAllAuthorizer();

    private sealed class DenyAllAuthorizer : IRetryAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}

/// <summary>Why the loop ended. A caller's cancellation is not a stop reason: it propagates as <see cref="OperationCanceledException"/>.</summary>
public enum ReflectionStopReason
{
    /// <summary>The host's trusted check passed.</summary>
    VerifiedSuccess = 0,

    /// <summary>No trusted check exists and the evaluator found nothing to correct. Not a verified success.</summary>
    EvaluatorSatisfied = 1,

    /// <summary><see cref="ReflectionExecutionOptions.MaxReflectionCycles"/> was reached.</summary>
    CyclesExhausted = 2,

    /// <summary><see cref="ReflectionExecutionOptions.MaxAttempts"/> was reached.</summary>
    AttemptsExhausted = 3,

    /// <summary><see cref="ReflectionExecutionOptions.MaxDuration"/> elapsed.</summary>
    DurationExhausted = 4,

    /// <summary>The tool-call or token budget was reached.</summary>
    BudgetExhausted = 5,

    /// <summary>The <see cref="IRetryAuthorizer"/> refused.</summary>
    RetryDenied = 6,

    /// <summary>The task's <see cref="SideEffectPolicy"/> forbids a semantic retry, or the evaluator did not recommend one.</summary>
    NotRetryable = 7,

    /// <summary>The evaluator proposed a correction already tried.</summary>
    RepeatedStrategy = 8,

    /// <summary>The evaluator threw, timed out, or returned an invalid evaluation.</summary>
    EvaluationFailed = 9,

    /// <summary>The evaluator judged the task unrecoverable.</summary>
    Unrecoverable = 10,

    /// <summary>Reflective execution is disabled, so the one attempt ran as it would have without it.</summary>
    Disabled = 11,
}

/// <summary>Whether a task may be attempted again after it already acted.</summary>
public enum SideEffectPolicy
{
    /// <summary>The task acted in ways that cannot be repeated safely (the default): no semantic retry.</summary>
    NeverRetry = 0,

    /// <summary>A retry is allowed only when the <see cref="IRetryAuthorizer"/> says so.</summary>
    RequiresAuthorization = 1,

    /// <summary>The task is idempotent; a retry is still gated by the authorizer and every budget.</summary>
    Idempotent = 2,
}

/// <summary>Everything one reflective execution did.</summary>
/// <param name="StopReason">Why it ended.</param>
/// <param name="Attempts">Each attempt's outcome, in order.</param>
/// <param name="Evaluations">The validated evaluations, in order (fewer than attempts when no evaluator ran).</param>
/// <param name="RetryHappened">Whether any attempt after the first ran.</param>
/// <param name="RetryImprovedResult">Whether a later attempt completed where an earlier one had not, or had fewer failed calls.</param>
/// <param name="RetryReachedVerifiedSuccess">Whether a retry ended with a passing trusted check. The only one of the three that is evidence.</param>
public sealed record ReflectionExecutionResult(
    ReflectionStopReason StopReason,
    IReadOnlyList<ReflectionAttemptOutcome> Attempts,
    IReadOnlyList<ReflectionEvaluation> Evaluations,
    bool RetryHappened,
    bool RetryImprovedResult,
    bool RetryReachedVerifiedSuccess);
