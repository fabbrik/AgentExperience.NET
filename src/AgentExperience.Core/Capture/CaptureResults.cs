using AgentExperience.Abstractions;

namespace AgentExperience.Core.Capture;

/// <summary>
/// The disposition an <see cref="IExperienceCaptureService.StartRun"/> call reached.
/// </summary>
public enum StartRunOutcome
{
    /// <summary>A new run was opened with an empty attempt history.</summary>
    Started,

    /// <summary>
    /// A run with the given <c>RunId</c> already exists. Starting a run carries no caller-supplied
    /// idempotency event ID of its own to distinguish a legitimate retry from a colliding
    /// identifier, so any collision -- whatever the new call's own parameters -- is a
    /// <see cref="Conflict"/>, never silently reused or overwritten.
    /// </summary>
    Conflict,
}

/// <summary>
/// The disposition an <see cref="IExperienceCaptureService.AppendAttemptAsync"/> call reached.
/// </summary>
public enum AppendAttemptOutcome
{
    /// <summary>The attempt was sanitized and durably captured in the run's in-memory state.</summary>
    Recorded,

    /// <summary>
    /// This exact <c>AttemptId</c> was already recorded with identical content; nothing changed --
    /// resubmission is a no-op, never a second entry.
    /// </summary>
    DuplicateNoOp,

    /// <summary>
    /// This exact <c>AttemptId</c> was already recorded with <em>different</em> content, or this
    /// call arrived after the run was already finalized by <c>CompleteRunAsync</c>. The existing
    /// state is unchanged; the submission is rejected rather than silently overwriting it or
    /// reopening a finalized run.
    /// </summary>
    Conflict,

    /// <summary>
    /// The composed <see cref="AgentExperience.Abstractions.ISanitizer"/> returned
    /// <see cref="AgentExperience.Abstractions.SanitizationDecision.Rejected"/> for some part of
    /// this attempt (its own <c>Result</c>/<c>Error</c>, or a tool call's
    /// <c>Arguments</c>/<c>Result</c>/<c>Error</c>), or the raw request itself was malformed (e.g. a
    /// <see langword="null"/> tool call, or a tool call with a <see langword="null"/>
    /// <c>Arguments</c>). Fail-closed: nothing from this attempt was stored, and its
    /// <c>AttemptId</c> is not tracked -- a later, corrected resubmission under the same
    /// <c>AttemptId</c> is free to succeed as <see cref="Recorded"/>.
    /// </summary>
    SanitizationRejected,

    /// <summary>No run with the given <c>RunId</c> exists (or it was never started).</summary>
    RunNotFound,

    /// <summary>
    /// The run had already reached <see cref="CaptureLimits.MaxAttemptsPerRun"/> distinct attempts.
    /// This genuinely new <c>AttemptId</c> was rejected outright and is not tracked at all -- so
    /// <see cref="CaptureLimits"/>'s "no unbounded payload is ever retained" guarantee extends to
    /// the run's own idempotency bookkeeping, not just to stored field content.
    /// </summary>
    CapacityExceeded,
}

/// <summary>
/// The disposition an <see cref="IExperienceCaptureService.CompleteRunAsync"/> call reached.
/// </summary>
public enum CompleteRunOutcome
{
    /// <summary>The run's <c>ExecutionStatus</c>/<c>EndedAt</c> were finalized by this call.</summary>
    Recorded,

    /// <summary>
    /// This exact completion event ID already finalized the run with identical
    /// <c>ExecutionStatus</c>/<c>EndedAt</c>; nothing changed -- resubmission is a no-op.
    /// </summary>
    DuplicateNoOp,

    /// <summary>
    /// The run was already finalized -- either by this same event ID with different content, or by
    /// a different completion event entirely. A run finalizes exactly once; the existing state is
    /// unchanged.
    /// </summary>
    Conflict,

    /// <summary>No run with the given <c>RunId</c> exists (or it was never started).</summary>
    RunNotFound,
}

/// <summary>
/// Records that one stored field's value was replaced with a safe placeholder, or that excess
/// entries were dropped from a stored list, because it exceeded a configured
/// <see cref="CaptureLimits"/> limit. An auditable diagnostic only -- <see cref="Reason"/> never
/// carries the original, over-limit content.
/// </summary>
/// <param name="FieldPath">Identifies which field or list was truncated (e.g. <c>"Attempt.Result"</c>, <c>"ToolCalls[2].Error"</c>, <c>"ToolCalls"</c>).</param>
/// <param name="Reason">Human-readable, content-free explanation of the truncation (which limit was exceeded).</param>
public sealed record TruncatedField(string FieldPath, string Reason);

/// <summary>
/// The result of one <see cref="IExperienceCaptureService.StartRun"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Run">The newly opened run's initial snapshot when <see cref="Outcome"/> is <see cref="StartRunOutcome.Started"/>; <see langword="null"/> on <see cref="StartRunOutcome.Conflict"/>.</param>
/// <param name="Reason">Optional, auditable explanation, e.g. why a duplicate <c>RunId</c> conflicted.</param>
public sealed record StartRunResult(StartRunOutcome Outcome, ExperienceRun? Run, string? Reason);

/// <summary>
/// The result of one <see cref="IExperienceCaptureService.AppendAttemptAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="TruncatedFields">
/// Which fields, if any, were truncated to a safe placeholder or had excess entries dropped while
/// enforcing <see cref="CaptureLimits"/>. Always empty unless <see cref="Outcome"/> is
/// <see cref="AppendAttemptOutcome.Recorded"/>.
/// </param>
/// <param name="Reason">Optional, auditable, content-free explanation, e.g. why sanitization rejected this attempt.</param>
public sealed record AppendAttemptResult(
    AppendAttemptOutcome Outcome,
    IReadOnlyList<TruncatedField> TruncatedFields,
    string? Reason);

/// <summary>
/// The result of one <see cref="IExperienceCaptureService.CompleteRunAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Optional, auditable explanation, e.g. why a completion attempt conflicted.</param>
public sealed record CompleteRunResult(
    CompleteRunOutcome Outcome,
    string? Reason);
