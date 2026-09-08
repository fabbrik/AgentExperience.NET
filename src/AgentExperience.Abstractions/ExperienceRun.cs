namespace AgentExperience.Abstractions;

/// <summary>
/// How an <see cref="ExperienceRun"/> finished executing. This is a mechanical, execution-level
/// signal — whether the invocation itself ran to completion, failed, or was cancelled — and is
/// distinct from <see cref="TaskVerificationStatus"/>, which judges whether the task was actually
/// accomplished.
/// </summary>
public enum RunExecutionStatus
{
    /// <summary>The run executed to completion without an unhandled error.</summary>
    Completed,

    /// <summary>The run terminated due to an unhandled error or exception.</summary>
    Failed,

    /// <summary>The run was cancelled before it completed.</summary>
    Cancelled,
}

/// <summary>
/// A single observable tool invocation captured during an <see cref="Attempt"/>. Capture
/// aggregates sanitized experience only; the underlying tool execution itself remains owned by
/// the invoking framework (e.g. MAF).
/// </summary>
/// <param name="ToolCallId">Unique identifier for this tool call.</param>
/// <param name="SequenceNumber">The zero-based, strictly increasing order of this tool call within its <see cref="Attempt"/>.</param>
/// <param name="ToolName">The name of the invoked tool.</param>
/// <param name="Arguments">The (already-sanitized) arguments passed to the tool.</param>
/// <param name="StartedAt">When the tool call started.</param>
/// <param name="Duration">How long the tool call took to complete.</param>
/// <param name="Result">Optional, sanitized textual result of the tool call, present when it succeeded.</param>
/// <param name="Error">Optional, sanitized textual error, present when the tool call failed.</param>
public sealed record ToolCallRecord(
    Guid ToolCallId,
    int SequenceNumber,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? Result,
    string? Error);

/// <summary>
/// One observable attempt at accomplishing an <see cref="ExperienceRun"/>'s task: zero or more
/// ordered tool calls plus the attempt's own result or error and duration.
/// </summary>
/// <param name="AttemptId">Unique identifier for this attempt.</param>
/// <param name="SequenceNumber">The zero-based, strictly increasing order of this attempt within its <see cref="ExperienceRun"/>.</param>
/// <param name="StartedAt">When the attempt started.</param>
/// <param name="Duration">How long the attempt took.</param>
/// <param name="ToolCalls">The tool calls observed during this attempt, in the order they occurred.</param>
/// <param name="Result">Optional, sanitized textual result of the attempt, present when it succeeded.</param>
/// <param name="Error">Optional, sanitized textual error, present when the attempt failed.</param>
public sealed record Attempt(
    Guid AttemptId,
    int SequenceNumber,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    string? Result,
    string? Error);

/// <summary>
/// A portable, adapter-independent snapshot of one agent invocation: its task identity, ordered
/// attempts, scope, environment, and provenance, plus its execution status and (once evaluated)
/// task verification outcome. Both successful and failed invocations are representable without
/// requiring private chain-of-thought as a storage or diagnostic requirement.
/// </summary>
/// <param name="RunId">Unique identifier for this run. Available before the first observable action so it can correlate capture and telemetry from the start.</param>
/// <param name="TaskId">Identifies which task this run is attempting.</param>
/// <param name="TaskDescription">Optional, sanitized human-readable description of the task.</param>
/// <param name="Scope">The tenancy/ownership scope this run belongs to.</param>
/// <param name="Environment">The runtime environment this run executed in.</param>
/// <param name="Provenance">Where this run's capture originated.</param>
/// <param name="Attempts">The observable attempts made during this run, in the order they occurred.</param>
/// <param name="ExecutionStatus">The run's execution status once it has finished; <see langword="null"/> while the run is still in progress.</param>
/// <param name="Outcome">The run's task verification outcome once evaluated; <see langword="null"/> until an evaluation has run.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="EndedAt">When the run finished; <see langword="null"/> while the run is still in progress.</param>
public sealed record ExperienceRun(
    Guid RunId,
    string TaskId,
    string? TaskDescription,
    Scope Scope,
    EnvironmentFingerprint Environment,
    Provenance Provenance,
    IReadOnlyList<Attempt> Attempts,
    RunExecutionStatus? ExecutionStatus,
    Outcome? Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);
