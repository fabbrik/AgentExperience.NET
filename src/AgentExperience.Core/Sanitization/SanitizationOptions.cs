namespace AgentExperience.Core.Sanitization;

/// <summary>
/// Per-<see cref="AgentExperience.Abstractions.RawPayload.Kind"/> sanitization policy: which field
/// names are allowlisted (kept -- passed through, or recursed into if their value is a nested
/// dictionary or list), which are classified as secret (redacted as a whole value, regardless of
/// whether that value is a string, a dictionary, or a list -- never traversed), and the
/// structural limits a payload of this <c>Kind</c> must stay within.
/// </summary>
/// <remarks>
/// A field name that is neither allowlisted nor secret-classified is omitted by default -- this
/// is a closed allowlist, not a denylist: unrecognized fields never pass through "just in case".
/// Configured names only ever match by field <em>name</em>; they cannot guarantee detection of an
/// arbitrary secret hiding under an allowlisted, unrecognized field name (see
/// <see cref="AgentExperience.Abstractions.SanitizedPayload"/>'s own disclaimer, and
/// <see cref="DefaultSanitizer"/>'s remarks).
/// </remarks>
/// <param name="AllowedFieldNames">
/// Field names kept in the sanitized output: a scalar value passes through unchanged, and a
/// dictionary or list value is recursed into (its own fields/items are classified the same way,
/// one level deeper).
/// </param>
/// <param name="SecretFieldNames">
/// Field names redacted as a whole value via the composed <c>Redactor</c>, whatever their value's
/// shape -- a secret-classified field is never traversed, so a secret field holding a dictionary
/// or list cannot leak a sub-field just because that sub-field's name looks allowlisted.
/// </param>
/// <param name="MaxDepth">
/// The maximum nesting depth allowed, counting the payload's top-level fields as depth 1 and each
/// nested dictionary or list as one level deeper. Exceeding this rejects the whole payload.
/// </param>
/// <param name="MaxFieldCount">
/// The maximum number of entries allowed in any single dictionary (field count) or list (item
/// count) encountered during traversal, checked at every nesting level independently. Exceeding
/// this rejects the whole payload.
/// </param>
/// <param name="MaxValueLength">
/// The maximum character length allowed for any leaf string value encountered during traversal --
/// checked before a field's allowlisted/secret/unknown disposition is decided, so an oversized
/// value rejects the whole payload even if that field would otherwise have been redacted or
/// omitted, rather than ever being silently truncated.
/// </param>
/// <param name="MaxFieldNameLength">
/// The maximum character length allowed for a field <em>name</em> itself, checked before that name
/// is ever used to build a dotted/indexed field path -- distinct from <see cref="MaxValueLength"/>
/// since a reasonable value length and a reasonable field-name length are typically very different
/// magnitudes. Exceeding this rejects the whole payload rather than building a path from (and
/// recording) an unbounded name.
/// </param>
public sealed record SanitizationPolicy(
    IReadOnlySet<string> AllowedFieldNames,
    IReadOnlySet<string> SecretFieldNames,
    int MaxDepth,
    int MaxFieldCount,
    int MaxValueLength,
    int MaxFieldNameLength)
{
    /// <summary>Field names kept in the sanitized output (see the primary constructor's <c>AllowedFieldNames</c> parameter doc).</summary>
    public IReadOnlySet<string> AllowedFieldNames { get; init; } = AllowedFieldNames ?? throw new ArgumentNullException(nameof(AllowedFieldNames));

    /// <summary>Field names redacted as a whole value (see the primary constructor's <c>SecretFieldNames</c> parameter doc).</summary>
    public IReadOnlySet<string> SecretFieldNames { get; init; } = SecretFieldNames ?? throw new ArgumentNullException(nameof(SecretFieldNames));
}

/// <summary>
/// Maps each payload <c>Kind</c> (e.g. "TaskContext", "ToolArguments", "ToolResult", "Evidence")
/// to its <see cref="SanitizationPolicy"/>, per <see cref="AgentExperience.Abstractions.RawPayload.Kind"/>'s
/// own doc comment: "a sanitizer can apply payload-specific policy". A <c>Kind</c> with no entry
/// here is not a gap to fall back on some shared/global policy for -- <see cref="DefaultSanitizer"/>
/// fail-closed rejects it outright, since accepting an unconfigured payload shape by default would
/// be the opposite of fail-closed.
/// </summary>
/// <param name="Policies">The configured policy for each recognized <c>Kind</c>, keyed by that exact <c>Kind</c> string.</param>
public sealed record SanitizationOptions(IReadOnlyDictionary<string, SanitizationPolicy> Policies)
{
    /// <summary>An empty options set: every <c>Kind</c> is unconfigured, so everything is rejected.</summary>
    public static SanitizationOptions Empty { get; } = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal));
}
