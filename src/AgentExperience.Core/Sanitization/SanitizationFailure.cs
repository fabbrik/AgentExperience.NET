namespace AgentExperience.Core.Sanitization;

/// <summary>
/// Classifies why <see cref="DefaultSanitizer"/> rejected a payload. Every member names a
/// structural policy violation, never a value -- see <see cref="SanitizationFailureException"/>.
/// </summary>
public enum SanitizationFailureReason
{
    /// <summary>No <see cref="SanitizationPolicy"/> is configured for the payload's <c>Kind</c>; fail-closed rejects it rather than falling back to a default policy.</summary>
    NoPolicyConfigured,

    /// <summary>Traversal reached a nesting depth beyond the configured policy's <see cref="SanitizationPolicy.MaxDepth"/>.</summary>
    DepthLimitExceeded,

    /// <summary>A dictionary or list at some nesting level holds more entries than the configured policy's <see cref="SanitizationPolicy.MaxFieldCount"/>.</summary>
    FieldCountLimitExceeded,

    /// <summary>A leaf string value is longer than the configured policy's <see cref="SanitizationPolicy.MaxValueLength"/>.</summary>
    ValueLengthLimitExceeded,

    /// <summary>A field name itself is longer than the configured policy's <see cref="SanitizationPolicy.MaxFieldNameLength"/>.</summary>
    FieldNameLengthExceeded,

    /// <summary>A field name contains a character (<c>.</c>, <c>[</c>, or <c>]</c>) reserved for building dotted/indexed field paths, which could otherwise collide with a genuinely nested path.</summary>
    UnsafeFieldName,

    /// <summary>The composed <see cref="Microsoft.Extensions.Compliance.Redaction.Redactor"/> threw while redacting a secret-classified value.</summary>
    RedactorFailed,
}

/// <summary>
/// A payload-free sanitization failure: raised internally by <see cref="DefaultSanitizer"/> during
/// traversal when a payload's <c>Kind</c> has no configured policy, or when traversal finds a
/// policy limit violated, and always caught by <see cref="DefaultSanitizer.SanitizeAsync"/> itself
/// -- never left to escape to a caller -- and converted into a
/// <see cref="AgentExperience.Abstractions.SanitizedPayload"/> with
/// <see cref="AgentExperience.Abstractions.SanitizationDecision.Rejected"/>, whose
/// <see cref="AgentExperience.Abstractions.SanitizedPayload.Reason"/> is this exception's
/// <see cref="Exception.Message"/>.
/// </summary>
/// <remarks>
/// This type is payload-free by construction, not merely by convention: its constructor accepts
/// only a <c>Kind</c> string, a <see cref="SanitizationFailureReason"/> classification, and an
/// optional dotted/indexed <see cref="FieldPath"/> built from field <em>names</em> -- there is no
/// parameter through which a field's raw <em>value</em> could ever reach this type, so
/// <see cref="Exception.Message"/> can never contain one. A field path built purely from field
/// names is treated as a safe identifier (the same kind of path already exposed, unredacted, via
/// <see cref="AgentExperience.Abstractions.SanitizedPayload.RedactedFieldPaths"/> and
/// <see cref="AgentExperience.Abstractions.SanitizedPayload.OmittedFieldPaths"/>), never a value.
/// </remarks>
public sealed class SanitizationFailureException : Exception
{
    /// <summary>The <c>Kind</c> of the payload that was rejected.</summary>
    public string Kind { get; }

    /// <summary>Why the payload was rejected.</summary>
    public SanitizationFailureReason FailureReason { get; }

    /// <summary>
    /// The dotted/indexed field path (built from field names only, e.g. <c>"items[2].apiKey"</c>)
    /// where the violation was found, or <see langword="null"/> when the failure is not tied to a
    /// specific field (e.g. <see cref="SanitizationFailureReason.NoPolicyConfigured"/>).
    /// </summary>
    public string? FieldPath { get; }

    /// <summary>Creates a payload-free sanitization failure.</summary>
    /// <param name="kind">The <c>Kind</c> of the payload being rejected.</param>
    /// <param name="failureReason">Why the payload is being rejected.</param>
    /// <param name="fieldPath">Optional dotted/indexed field path (field names only, never a value) where the violation was found.</param>
    public SanitizationFailureException(string kind, SanitizationFailureReason failureReason, string? fieldPath = null)
        : base(BuildSafeMessage(kind, failureReason, fieldPath))
    {
        Kind = kind;
        FailureReason = failureReason;
        FieldPath = fieldPath;
    }

    private static string BuildSafeMessage(string kind, SanitizationFailureReason failureReason, string? fieldPath)
    {
        var location = fieldPath is null ? string.Empty : $" at field '{fieldPath}'";
        return $"Sanitization rejected payload of kind '{kind}'{location}: {failureReason}.";
    }
}
