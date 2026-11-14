using AgentExperience.Core.ReflectiveExecution;

namespace AgentExperience.MicrosoftAgentFramework.ReflectiveExecution;

/// <summary>
/// What a host's trusted check is shown about the attempt that just ended: sanitized, as capture stored it, never raw
/// tool arguments or results.
/// </summary>
/// <param name="RunId">The run every attempt of this execution belongs to.</param>
/// <param name="AttemptNumber">One-based.</param>
/// <param name="Result">The attempt's sanitized result text, or <see langword="null"/>.</param>
/// <param name="Error">The attempt's sanitized error text, or <see langword="null"/>.</param>
/// <param name="FailedCalls">The tool calls that failed, by name and error class.</param>
public sealed record ReflectiveAttemptView(
    Guid RunId,
    int AttemptNumber,
    string? Result,
    string? Error,
    IReadOnlyList<ReflectionFailedCall> FailedCalls);

/// <summary>
/// Configuration for reflective execution under Microsoft Agent Framework. Experimental, and disabled by default:
/// with <see cref="Limits"/>.<see cref="ReflectionExecutionOptions.Enabled"/> <see langword="false"/> the wrapped agent
/// behaves exactly as without it.
/// </summary>
public sealed class ReflectiveExecutionOptions
{
    /// <summary>Budgets and policy. <see cref="ReflectionExecutionOptions.Enabled"/> defaults to <see langword="false"/>.</summary>
    public ReflectionExecutionOptions Limits { get; set; } = new();

    /// <summary>
    /// Evaluates a failed or unverified attempt. <see langword="null"/> (the default) means no semantic retry: only a
    /// transient technical failure can be retried. Use <see cref="DeterministicReflectionEvaluator"/> or
    /// <see cref="ChatClientReflectionEvaluator"/>, or your own.
    /// </summary>
    public IReflectionEvaluator? Evaluator { get; set; }

    /// <summary>
    /// Builds the correction an attempt is given from an evaluation. <see langword="null"/> (the default) uses the
    /// evaluator's own proposal. It can only return text, which is bounded and screened; it cannot change the task or
    /// the permissions.
    /// </summary>
    public IReplanningStrategy? Replanning { get; set; }

    /// <summary>The gate before every retry. <see langword="null"/> (the default) denies every retry.</summary>
    public IRetryAuthorizer? Authorizer { get; set; }

    /// <summary>
    /// The host's own check of an attempt: <see langword="true"/> passed, <see langword="false"/> failed,
    /// <see langword="null"/> no verdict. Only a <see langword="true"/> ends the loop as verified. It is a retry
    /// signal, not the verification the experience record is finalized with: that stays the host's
    /// <c>Verify</c>/<c>ResolveFinalization</c>, which runs once on the finished run.
    /// </summary>
    public Func<ReflectiveAttemptView, CancellationToken, ValueTask<bool?>>? TrustedCheck { get; set; }

    /// <summary>Called for each stop with the reason and counters; carries no prompt, tool argument or model text.</summary>
    public Action<ReflectiveExecutionEvent>? OnEvent { get; set; }

    /// <summary>
    /// Receives each step of the loop (attempt, verification, reflection, evaluation, replanning, retry authorized or
    /// denied, budget exhausted, stopped) with the run id once it is known. Counts and enums only; an exception from it is
    /// swallowed. <see cref="ReflectionExecutionOptions.OnStep"/> on <see cref="Limits"/> still works and is called too.
    /// </summary>
    public Action<ReflectiveStepEvent>? OnStep { get; set; }
}

/// <summary>What the reflective execution did, as counts and enums only.</summary>
/// <param name="RunId">The run, or <see langword="null"/> when capture did not open one.</param>
/// <param name="StopReason">Why the loop ended.</param>
/// <param name="Attempts">How many attempts ran.</param>
/// <param name="RetryHappened">Whether any attempt after the first ran.</param>
/// <param name="RetryImprovedResult">Whether a retry did better than the attempt before it.</param>
/// <param name="RetryReachedVerifiedSuccess">Whether a retry ended with a passing trusted check.</param>
public sealed record ReflectiveExecutionEvent(
    Guid? RunId,
    ReflectionStopReason StopReason,
    int Attempts,
    bool RetryHappened,
    bool RetryImprovedResult,
    bool RetryReachedVerifiedSuccess);

/// <summary>One step of a reflective execution, with the run it belongs to.</summary>
/// <param name="RunId">The one run all attempts belong to, or <see langword="null"/> before the first attempt records it.</param>
/// <param name="Step">The step: counts and enums only, never text.</param>
public sealed record ReflectiveStepEvent(Guid? RunId, ReflectionStep Step);
