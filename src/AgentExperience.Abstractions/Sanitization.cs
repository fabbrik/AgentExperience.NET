namespace AgentExperience.Abstractions;

/// <summary>
/// An unsanitized, opaque payload envelope submitted to an <see cref="ISanitizer"/> before
/// persistence or context injection. <see cref="Kind"/> lets a sanitizer apply payload-specific
/// policy (task context, tool arguments, tool results, evidence, ...) without this package
/// knowing what any specific implementation's policy is.
/// </summary>
/// <param name="Kind">Identifies what this payload represents, e.g. "TaskContext", "ToolArguments", "ToolResult", "Evidence".</param>
/// <param name="Fields">The raw, unsanitized field values.</param>
public sealed record RawPayload(
    string Kind,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// The overall disposition an <see cref="ISanitizer"/> reached for a <see cref="RawPayload"/>.
/// </summary>
public enum SanitizationDecision
{
    /// <summary>The payload may proceed to persistence or context injection, possibly with some fields redacted or omitted.</summary>
    Allowed,

    /// <summary>The payload must not proceed to persistence or context injection (fail-closed).</summary>
    Rejected,
}

/// <summary>
/// The result of running a <see cref="RawPayload"/> through an <see cref="ISanitizer"/>: either
/// the payload is allowed to proceed (with an auditable record of which fields, if any, were
/// redacted or omitted), or it is rejected outright. Sanitization cannot guarantee detection of
/// every arbitrary secret; this contract records what an implementation decided, not a guarantee
/// that every sensitive value was found.
/// </summary>
/// <param name="Decision">Whether the payload is allowed to proceed or was rejected.</param>
/// <param name="Fields">The sanitized field values when <see cref="Decision"/> is <see cref="SanitizationDecision.Allowed"/>; empty when rejected. Unknown fields are omitted by default rather than passed through.</param>
/// <param name="RedactedFieldPaths">Field paths (dotted/indexed as appropriate) whose value was kept but replaced/masked (e.g. a configured secret), for audit purposes.</param>
/// <param name="OmittedFieldPaths">Field paths (dotted/indexed as appropriate) that were dropped entirely, e.g. an unknown field omitted by default, for audit purposes.</param>
/// <param name="Reason">Optional, auditable explanation for the decision, e.g. why a payload was rejected. Never private reasoning.</param>
public sealed record SanitizedPayload(
    SanitizationDecision Decision,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyList<string> RedactedFieldPaths,
    IReadOnlyList<string> OmittedFieldPaths,
    string? Reason);

/// <summary>
/// The port every captured payload passes through before it is persisted or injected into
/// context: task context, tool arguments, tool results, and evidence alike. Implementations
/// decide policy (allowlists, configured secret patterns, nested-value traversal, rejection
/// rules); this port only fixes the redact-or-reject shape. A denial must be fail-closed.
/// </summary>
public interface ISanitizer
{
    /// <summary>
    /// Sanitizes a <see cref="RawPayload"/>, returning either an allowed (possibly redacted)
    /// payload or a rejection. Implementations may need to call out to external policy or
    /// classification services, so this is asynchronous and cancellable like other I/O-facing
    /// ports.
    /// </summary>
    Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default);
}
