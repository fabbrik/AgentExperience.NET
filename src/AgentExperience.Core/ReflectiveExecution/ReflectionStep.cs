namespace AgentExperience.Core.ReflectiveExecution;

/// <summary>What a <see cref="ReflectionStep"/> reports. Enums and numbers only, never text.</summary>
public enum ReflectionStepKind
{
    /// <summary>The loop began.</summary>
    ExecutionStarted,

    /// <summary>An attempt finished, whatever its result.</summary>
    AttemptCompleted,

    /// <summary>The host's trusted check answered (or there is none: <see cref="ReflectionStep.TrustedCheckPassed"/> is <see langword="null"/>).</summary>
    VerificationReceived,

    /// <summary>The evaluator is about to be asked.</summary>
    ReflectionRequested,

    /// <summary>The evaluator returned a valid evaluation (<see cref="ReflectionStep.Assessment"/>).</summary>
    EvaluationCompleted,

    /// <summary>A correction was built and accepted for the next attempt.</summary>
    ReplanningPerformed,

    /// <summary>The host's <see cref="IRetryAuthorizer"/> allowed the retry.</summary>
    RetryAuthorized,

    /// <summary>The host's <see cref="IRetryAuthorizer"/> refused (or failed to answer).</summary>
    RetryDenied,

    /// <summary>A bound ended the loop: attempts, cycles, duration, tool calls or tokens.</summary>
    BudgetExhausted,

    /// <summary>The loop ended; <see cref="ReflectionStep.StopReason"/> says why. Always the last step.</summary>
    ExecutionStopped,
}

/// <summary>
/// One structured step of a reflective execution, for logs and traces. It carries counts and enums only: no task text,
/// result, tool argument, error text, evaluator text or correction note ever appears in it, so it is safe to record.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="AttemptNumber">The attempt it belongs to (1 for the first); 0 for <see cref="ReflectionStepKind.ExecutionStarted"/>.</param>
/// <param name="TrustedCheckPassed">For <see cref="ReflectionStepKind.VerificationReceived"/>: the host's verdict, or <see langword="null"/> for none.</param>
/// <param name="Assessment">For <see cref="ReflectionStepKind.EvaluationCompleted"/>: the evaluator's advisory assessment.</param>
/// <param name="StopReason">For <see cref="ReflectionStepKind.BudgetExhausted"/> and <see cref="ReflectionStepKind.ExecutionStopped"/>: why the loop ended.</param>
public sealed record ReflectionStep(
    ReflectionStepKind Kind,
    int AttemptNumber,
    bool? TrustedCheckPassed = null,
    ReflectionAssessment? Assessment = null,
    ReflectionStopReason? StopReason = null);
