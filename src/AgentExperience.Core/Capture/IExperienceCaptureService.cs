using System.Diagnostics.CodeAnalysis;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Capture;

/// <summary>
/// Raw (unsanitized) data describing one observable tool invocation, submitted as part of an
/// <see cref="AppendAttemptRequest"/>. The <em>content</em> fields pass through the composed
/// <see cref="ISanitizer"/> before they are ever stored as part of a
/// <see cref="AgentExperience.Abstractions.ToolCallRecord"/>: <see cref="Arguments"/> as
/// <c>RawPayload</c> <c>Kind</c> <c>"ToolArguments"</c>, <see cref="Result"/>/<see cref="Error"/>
/// each as <c>Kind</c> <c>"ToolResult"</c>.
/// </summary>
/// <remarks>
/// <b><see cref="ToolName"/> is the exception, and it is the one field a host's sanitizer never
/// sees.</b> It is stored verbatim: no sanitization policy applies to it, no
/// <c>MaxValueLength</c> truncates it, and <see cref="CaptureLimits"/> carries no bound for it. It
/// is treated that way because of its <em>provenance</em>, not its shape: it identifies a tool the
/// caller's inventory had already resolved when the call was made, so it is fixed before the run
/// starts and is not derived from the run's own data flow. It is not, however, guaranteed short or
/// structured -- an MCP or OpenAPI inventory takes its names from a remote server or a specification
/// -- so a caller that needs a bound on it must apply one before submitting.
/// </remarks>
/// <param name="ToolCallId">Unique identifier for this tool call.</param>
/// <param name="ToolName">The name of the invoked tool. Stored verbatim: this is the one field on this type the sanitizer never sees.</param>
/// <param name="Arguments">The raw, unsanitized arguments passed to the tool.</param>
/// <param name="StartedAt">When the tool call started.</param>
/// <param name="Duration">How long the tool call took to complete.</param>
/// <param name="Result">Optional, raw textual result of the tool call, present when it succeeded.</param>
/// <param name="Error">Optional, raw textual error, present when the tool call failed.</param>
public sealed record RawToolCall(
    Guid ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? Result,
    string? Error);

/// <summary>
/// Raw (unsanitized) data describing one observable attempt, submitted to
/// <see cref="IExperienceCaptureService.AppendAttemptAsync"/>. <see cref="AttemptId"/> doubles as
/// this call's idempotency event ID: an identical resubmission (same <see cref="AttemptId"/>, same
/// content) is a no-op; a resubmission under the same <see cref="AttemptId"/> with different
/// content returns <see cref="AppendAttemptOutcome.Conflict"/>.
/// </summary>
/// <param name="AttemptId">Unique identifier for this attempt -- also its idempotency event ID.</param>
/// <param name="StartedAt">When the attempt started.</param>
/// <param name="Duration">How long the attempt took.</param>
/// <param name="ToolCalls">The tool calls observed during this attempt, in the order they occurred.</param>
/// <param name="Result">Optional, raw textual result of the attempt, present when it succeeded.</param>
/// <param name="Error">Optional, raw textual error, present when the attempt failed.</param>
public sealed record AppendAttemptRequest(
    Guid AttemptId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<RawToolCall> ToolCalls,
    string? Result,
    string? Error);

/// <summary>
/// Thread-safe, in-memory accumulation and finalization of one <see cref="ExperienceRun"/>'s
/// attempts. <see cref="StartRun"/> opens a run -- or, on a <c>RunId</c> that names a still-open run
/// with the same task and scope, continues it, so one task's repeated tries accumulate as attempts
/// of one run instead of becoming unrelated runs; <see cref="AppendAttemptAsync"/> records one
/// sanitized attempt at a time, assigning <c>SequenceNumber</c> itself, by append order, so
/// ordering is correct by construction; <see cref="CompleteRunAsync"/> finalizes the run's
/// <see cref="RunExecutionStatus"/>. <see cref="ExperienceRun.Outcome"/> (task verification) is
/// never set here -- every run this service produces keeps a <see langword="null"/>
/// <see cref="ExperienceRun.Outcome"/>; a later evaluator (Story 1.5) owns setting it. Capture
/// aggregates sanitized experience only: no persistence (in-memory only; Epic 2 owns durable
/// storage) and no verification/evaluation logic. Every operation returns a typed outcome for an
/// expected condition (a duplicate/conflicting id, an unknown run, a rejected/over-capacity
/// attempt) rather than throwing; an exception is reserved for genuinely invalid input (a
/// <see langword="null"/> required argument).
/// </summary>
public interface IExperienceCaptureService
{
    /// <summary>
    /// Opens a new run with an empty attempt history, a <see langword="null"/>
    /// <see cref="ExperienceRun.ExecutionStatus"/>/<see cref="ExperienceRun.Outcome"/>/
    /// <see cref="ExperienceRun.EndedAt"/>.
    /// </summary>
    /// <param name="runId">
    /// Caller-supplied so it is available for correlation before any attempt is captured, per the
    /// epic's requirement that a run's identity precede the first observable action. A run with
    /// this <paramref name="runId"/> already existing is an expected condition, not an error, and it
    /// returns a typed outcome rather than throwing, mirroring
    /// <see cref="AppendAttemptAsync"/>/<see cref="CompleteRunAsync"/>: a still-open run whose
    /// <paramref name="taskId"/> and <paramref name="scope"/> both match is
    /// <see cref="StartRunOutcome.Continued"/> -- the caller is continuing that same run and may
    /// append a further attempt to it -- and anything else, including an already-finalized run, is
    /// <see cref="StartRunOutcome.Conflict"/>. A continuation never writes: the existing run's task
    /// description, scope, environment, provenance and start time all stand.
    /// </param>
    /// <param name="taskId">Identifies which task this run is attempting.</param>
    /// <param name="taskDescription">Optional human-readable description of the task. Not sanitized by this service (Story 1.2 scopes sanitization to tool-call/attempt content only).</param>
    /// <param name="scope">The tenancy/ownership scope this run belongs to.</param>
    /// <param name="environment">The runtime environment this run executes in.</param>
    /// <param name="provenance">Where this run's capture originates.</param>
    /// <param name="startedAt">When the run started.</param>
    StartRunResult StartRun(
        Guid runId,
        string taskId,
        string? taskDescription,
        Scope scope,
        EnvironmentFingerprint environment,
        Provenance provenance,
        DateTimeOffset startedAt);

    /// <summary>
    /// Records one attempt's raw tool-call data as a sanitized <c>Attempt</c>, assigning its own
    /// and each tool call's <c>SequenceNumber</c> by append order. Every tool call's
    /// <see cref="RawToolCall.Arguments"/>/<see cref="RawToolCall.Result"/>/
    /// <see cref="RawToolCall.Error"/> -- and the attempt's own
    /// <see cref="AppendAttemptRequest.Result"/>/<see cref="AppendAttemptRequest.Error"/> -- passes
    /// through the composed <see cref="ISanitizer"/> first; a
    /// <see cref="SanitizationDecision.Rejected"/> decision anywhere in the attempt fails the whole
    /// append closed (nothing from it is stored, and its <c>AttemptId</c> is not tracked). A call
    /// arriving after the run is already finalized (<see cref="CompleteRunAsync"/> has run) returns
    /// <see cref="AppendAttemptOutcome.Conflict"/>. Configured <see cref="CaptureLimits"/> are then
    /// enforced: a string field beyond its limit is truncated to a safe placeholder (reported on the
    /// result); a run already at its attempt-count capacity rejects a genuinely new attempt outright
    /// (<see cref="AppendAttemptOutcome.CapacityExceeded"/>).
    /// </summary>
    /// <remarks>
    /// <b><see cref="RawToolCall.ToolName"/> is not sanitized and is not length-bounded.</b> It is
    /// the one field of a submitted attempt that is stored exactly as it was passed: no sanitization
    /// policy is applied to it and no <see cref="CaptureLimits"/> entry bounds it. A caller
    /// submitting names from an inventory it does not control -- an MCP server's, or an OpenAPI
    /// specification's -- owns clamping them.
    /// </remarks>
    Task<AppendAttemptResult> AppendAttemptAsync(
        Guid runId,
        AppendAttemptRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes a run's <see cref="RunExecutionStatus"/>. <see cref="ExperienceRun.Outcome"/>
    /// (task verification) is never touched here.
    /// </summary>
    /// <param name="runId">The run to finalize.</param>
    /// <param name="completionEventId">
    /// This call's idempotency event ID: an identical resubmission (same ID, same
    /// <paramref name="executionStatus"/>/<paramref name="endedAt"/>) is a no-op; any other
    /// completion attempt on an already-finalized run returns
    /// <see cref="CompleteRunOutcome.Conflict"/> -- a run is finalized exactly once either way.
    /// </param>
    /// <param name="executionStatus">The run's mechanical execution status (Completed/Failed/Cancelled).</param>
    /// <param name="endedAt">When the run finished.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CompleteRunResult> CompleteRunAsync(
        Guid runId,
        Guid completionEventId,
        RunExecutionStatus executionStatus,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Reads back the current, in-memory snapshot of a run, if it exists.</summary>
    bool TryGetRun(Guid runId, [NotNullWhen(true)] out ExperienceRun? run);
}
