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
    /// A run with the given <c>RunId</c> already exists, it is still open, and it is <em>the same
    /// run</em>: its <c>TaskId</c> and <c>Scope</c> both match this call's. The existing run is
    /// returned untouched -- nothing it already holds is overwritten, and no attempt is added here --
    /// so the caller may append a further attempt to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a host continues one task across several framework invocations (a retry loop is
    /// the motivating case): the second invocation names the same <c>RunId</c> and gets its failed
    /// predecessor's attempts, instead of opening a second run whose first attempt has no knowledge
    /// that the failure happened. Matching on <c>TaskId</c> <em>and</em> <c>Scope</c> is what keeps
    /// this from being a silent reuse of somebody else's identifier: anything that does not match is
    /// still a <see cref="Conflict"/>.
    /// </para>
    /// <para>
    /// <b>What the continuing call's own arguments do, which is nothing.</b> The run keeps the
    /// <c>TaskDescription</c>, <c>Environment</c>, <c>Provenance</c> -- its <c>CorrelationId</c>
    /// included -- and <c>StartedAt</c> it was opened with; the ones this call passed are discarded
    /// without a word. Overwriting them would rewrite the recorded history of a run that already
    /// holds attempts, so discarding is the right half of the trade -- but it does mean a
    /// <c>RunId</c> reused by accident on a matching task and scope is <em>merged</em> into one run
    /// and reflected on as one, rather than refused. Matching on task and scope is the whole of the
    /// protection against that.
    /// </para>
    /// </remarks>
    Continued,

    /// <summary>
    /// A run with the given <c>RunId</c> already exists and this call is <em>not</em> a continuation
    /// of it: its <c>TaskId</c> or <c>Scope</c> differs, or the run has already been finalized by
    /// <c>CompleteRunAsync</c>. Starting a run carries no caller-supplied idempotency event ID of its
    /// own, so a collision that is not provably the same, still-open run is refused rather than
    /// silently reused or overwritten -- and a finalized run is never reopened, which is the same
    /// invariant <c>AppendAttemptAsync</c> enforces.
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
/// <param name="Run">The newly opened run's initial snapshot when <see cref="Outcome"/> is <see cref="StartRunOutcome.Started"/>, or the existing run's current snapshot when it is <see cref="StartRunOutcome.Continued"/>; <see langword="null"/> on <see cref="StartRunOutcome.Conflict"/>.</param>
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
/// The disposition an <see cref="IExperienceCaptureService.RecordExposure"/> call reached.
/// </summary>
public enum RecordExposureOutcome
{
    /// <summary>At least one exposure was new to the run, or earlier than the one it held, and was recorded.</summary>
    Recorded,

    /// <summary>The run already held every exposure at the same or an earlier revision; nothing changed.</summary>
    DuplicateNoOp,

    /// <summary>
    /// The run is already completed, so its provenance -- which finalization copies onto its record -- is
    /// closed. Nothing was recorded.
    /// </summary>
    Conflict,

    /// <summary>No run with the given <c>RunId</c> exists (or it was never started).</summary>
    RunNotFound,

    /// <summary>
    /// Recording these exposures would take the run past <see cref="RunExposure.MaxPerRun"/> distinct
    /// records. Nothing from this call was recorded.
    /// </summary>
    CapacityExceeded,

    /// <summary>
    /// The capture service does not record exposure: the port's default for an implementation written
    /// before exposure existed. Evidence about reuse in its runs is refused as not exposed.
    /// </summary>
    NotSupported,
}

/// <summary>
/// The result of one <see cref="IExperienceCaptureService.RecordExposure"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Optional, auditable, content-free explanation of a refusal.</param>
public sealed record RecordExposureResult(RecordExposureOutcome Outcome, string? Reason);

/// <summary>
/// The result of one <see cref="IExperienceCaptureService.CompleteRunAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Optional, auditable explanation, e.g. why a completion attempt conflicted.</param>
public sealed record CompleteRunResult(
    CompleteRunOutcome Outcome,
    string? Reason);
