using System.Collections;
using System.Text.Json;
using AgentExperience.Abstractions;
using Microsoft.Extensions.Compliance.Redaction;

namespace AgentExperience.Core.Sanitization;

/// <summary>
/// The default <see cref="ISanitizer"/> implementation: looks up a per-<c>Kind</c>
/// <see cref="SanitizationPolicy"/>, recursively traverses a <see cref="RawPayload"/>'s nested
/// dictionaries and lists, and classifies every field by name -- secret-classified fields are
/// redacted as a whole value (via a composed <see cref="Redactor"/>) before any container
/// recursion happens, so a secret field holding a nested dictionary or list is never traversed;
/// allowlisted fields pass through (or recurse, one level deeper, when their value is itself a
/// dictionary or list); every other field is omitted. A configured depth, field-count, value-length,
/// or field-name-length limit violated anywhere during traversal rejects the whole payload
/// fail-closed -- never a partial, truncated result.
/// </summary>
/// <remarks>
/// <para>
/// <b>This cannot guarantee every arbitrary secret is detected.</b> <see cref="SanitizationPolicy"/>
/// classifies fields purely by configured <em>name</em> (<see cref="SanitizationPolicy.SecretFieldNames"/>
/// / <see cref="SanitizationPolicy.AllowedFieldNames"/>). A secret value stored under an
/// allowlisted or otherwise-unrecognized field name is not detected by this implementation and
/// will pass through (or be omitted, never redacted) exactly like any other value under that
/// field name -- this mirrors the disclaimer already recorded on
/// <see cref="SanitizedPayload"/> itself, and it is a limitation to document plainly, not one to
/// silently work around by claiming broader coverage than name-based classification can offer.
/// </para>
/// <para>
/// Traversal order fixes two gaps Story 1.7's proof-only demo deliberately left open (see that
/// story's own doc comments): a secret-classified field's value is classified <em>before</em> any
/// container recursion is attempted, so a secret field holding a dictionary or list is redacted as
/// one unit and never walked into; and lists are recursed just like dictionaries, so a list of
/// nested dictionaries has its own secret-classified sub-fields redacted too.
/// </para>
/// <para>
/// Recognized shapes beyond a plain <see cref="string"/>/<see cref="IReadOnlyDictionary{TKey,TValue}"/>/
/// <see cref="IEnumerable"/>: a <see cref="JsonElement"/> (common after JSON deserialization) is
/// classified by its <see cref="JsonValueKind"/> (object -> dictionary, array -> list, otherwise a
/// scalar leaf); <see cref="byte"/>[] is treated as an opaque leaf checked against
/// <see cref="SanitizationPolicy.MaxValueLength"/>, never as a "list" of individually-counted
/// bytes; and dictionary-shaped values that are not exactly
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> (e.g. <c>Dictionary&lt;string,string&gt;</c>,
/// <c>ExpandoObject</c>) are still classified field-by-field, never misrouted through the generic
/// list branch. Any other, still-unrecognized reference-typed value is never blindly passed
/// through unexamined -- it is omitted, exactly like an unknown field name, because an unexamined
/// shape could carry an unclassified secret sub-value; a value-typed leaf (numeric, boolean,
/// <see cref="Guid"/>, <see cref="DateTimeOffset"/>, an enum, ...) is atomic and opaque, so it
/// passes through safely.
/// </para>
/// </remarks>
public sealed class DefaultSanitizer : ISanitizer
{
    /// <summary>
    /// A fixed, non-leaking placeholder fed to the composed <see cref="Redactor"/> in place of a
    /// secret-classified field's raw value whenever that value is not itself a string (a
    /// dictionary, a list, a number, a boolean, or <see langword="null"/>) -- the placeholder
    /// never reflects the original value's shape, length, or content, so it carries no
    /// information about what was redacted beyond the fact that a secret-classified field was
    /// present.
    /// </summary>
    private const string NonStringSecretPlaceholder = "[secret value redacted]";

    /// <summary>Characters reserved for building dotted/indexed field paths (<c>"a.b"</c>, <c>"a[0]"</c>) -- never allowed inside a raw field name, so a constructed path can never collide with a genuinely nested one.</summary>
    private static readonly char[] UnsafeFieldNameCharacters = ['.', '[', ']'];

    private static readonly IReadOnlyDictionary<string, object?> EmptyFields = new Dictionary<string, object?>();

    private readonly SanitizationOptions _options;
    private readonly Redactor _redactor;

    /// <summary>
    /// Creates a <see cref="DefaultSanitizer"/>.
    /// </summary>
    /// <param name="options">The per-<c>Kind</c> sanitization policy to apply.</param>
    /// <param name="redactor">
    /// The redactor used to redact secret-classified leaf values. Defaults to
    /// <see cref="ErasingRedactor"/> (per <c>Microsoft.Extensions.Compliance.Redaction</c>), which
    /// leaks strictly less than a fixed mask since it does not preserve the original value's
    /// length. Swap in a different <see cref="Redactor"/> to change that policy.
    /// </param>
    public DefaultSanitizer(SanitizationOptions options, Redactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _redactor = redactor ?? ErasingRedactor.Instance;
    }

    /// <inheritdoc />
    public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payload.Kind);
        ArgumentNullException.ThrowIfNull(payload.Fields);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_options.Policies.TryGetValue(payload.Kind, out var policy))
            {
                throw new SanitizationFailureException(payload.Kind, SanitizationFailureReason.NoPolicyConfigured);
            }

            var redactedPaths = new List<string>();
            var omittedPaths = new List<string>();

            EnsureDepth(1, policy, payload.Kind, fieldPath: null);
            var sanitizedFields = SanitizeFields(payload.Fields, policy, depth: 1, pathPrefix: null, payload.Kind, redactedPaths, omittedPaths, cancellationToken);

            return Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Allowed,
                Fields: sanitizedFields,
                RedactedFieldPaths: redactedPaths,
                OmittedFieldPaths: omittedPaths,
                Reason: null));
        }
        catch (SanitizationFailureException failure)
        {
            return Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Rejected,
                Fields: EmptyFields,
                RedactedFieldPaths: [],
                OmittedFieldPaths: [],
                Reason: failure.Message));
        }
    }

    /// <summary>
    /// Classifies and sanitizes every entry of a dictionary (the payload's top-level
    /// <see cref="RawPayload.Fields"/>, or a nested dictionary reached through an allowlisted
    /// field): a secret-classified field is redacted as a whole value ahead of any recursion, an
    /// allowlisted field passes through or recurses, and anything else is omitted.
    /// </summary>
    private Dictionary<string, object?> SanitizeFields(
        IReadOnlyDictionary<string, object?> fields,
        SanitizationPolicy policy,
        int depth,
        string? pathPrefix,
        string kind,
        List<string> redactedPaths,
        List<string> omittedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureFieldCount(fields.Count, policy, kind, pathPrefix);

        var sanitized = new Dictionary<string, object?>();

        foreach (var (key, value) in fields)
        {
            // A field *name* this long, or containing a path-delimiter character, is itself
            // treated as unsafe -- checked before any path is built from it.
            EnsureFieldNameLength(key.Length, policy, kind, pathPrefix);
            EnsureSafeFieldName(key, kind);

            var path = pathPrefix is null ? key : $"{pathPrefix}.{key}";

            if (value is string text)
            {
                EnsureValueLength(text.Length, policy, kind, path);
            }

            // Secret classification is checked before any container recursion is attempted, so a
            // secret field whose value is itself a dictionary or list is redacted as one unit and
            // never traversed -- closing Story 1.7's proof-only gap (a).
            if (policy.SecretFieldNames.Contains(key))
            {
                sanitized[key] = RedactWhole(value, kind, path);
                redactedPaths.Add(path);
                continue;
            }

            if (!policy.AllowedFieldNames.Contains(key))
            {
                omittedPaths.Add(path);
                continue;
            }

            if (TrySanitizeValue(value, policy, depth, path, kind, redactedPaths, omittedPaths, cancellationToken, out var sanitizedValue))
            {
                sanitized[key] = sanitizedValue;
            }
            else
            {
                omittedPaths.Add(path);
            }
        }

        return sanitized;
    }

    /// <summary>
    /// Classifies and sanitizes a value already determined to belong to an allowlisted dictionary
    /// field, or a list item (list items have no name of their own to classify, so every item of
    /// an allowlisted list is processed here directly). Recognized container shapes recurse one
    /// level deeper; recognized scalar/opaque leaf shapes pass through (after their own length
    /// check); any other, still-unrecognized reference-typed shape is never blindly passed through
    /// -- it is omitted, exactly like an unknown field name, because an unexamined shape could
    /// carry an unclassified secret sub-value.
    /// </summary>
    /// <returns><see langword="true"/> with the sanitized value if it should be kept; <see langword="false"/> if it must be omitted.</returns>
    private bool TrySanitizeValue(
        object? value,
        SanitizationPolicy policy,
        int depth,
        string path,
        string kind,
        List<string> redactedPaths,
        List<string> omittedPaths,
        CancellationToken cancellationToken,
        out object? sanitizedValue)
    {
        switch (value)
        {
            case null:
                sanitizedValue = null;
                return true;

            case string text:
                EnsureValueLength(text.Length, policy, kind, path);
                sanitizedValue = text;
                return true;

            // An opaque leaf, like a string -- length-limited, never reshaped into a "list" of
            // individually MaxFieldCount-counted bytes.
            case byte[] bytes:
                EnsureValueLength(bytes.Length, policy, kind, path);
                sanitizedValue = bytes;
                return true;

            case JsonElement json:
                return TrySanitizeJsonElement(json, policy, depth, path, kind, redactedPaths, omittedPaths, cancellationToken, out sanitizedValue);

            case IReadOnlyDictionary<string, object?> exactDict:
            {
                var childDepth = depth + 1;
                EnsureDepth(childDepth, policy, kind, path);
                sanitizedValue = SanitizeFields(exactDict, policy, childDepth, path, kind, redactedPaths, omittedPaths, cancellationToken);
                return true;
            }

            // Dictionary-shaped values that are not exactly IReadOnlyDictionary<string,object?>
            // (e.g. Dictionary<string,string>, Hashtable): still classified field-by-field, never
            // misrouted through the generic list branch below -- which would bypass name-based
            // classification entirely and leak via each KeyValuePair's default ToString().
            case System.Collections.IDictionary legacyDict:
            {
                var childDepth = depth + 1;
                EnsureDepth(childDepth, policy, kind, path);
                EnsureFieldCount(legacyDict.Count, policy, kind, path);
                sanitizedValue = SanitizeFields(ToStringKeyedFields(legacyDict), policy, childDepth, path, kind, redactedPaths, omittedPaths, cancellationToken);
                return true;
            }

            // Generic-only dictionary shapes with an object-typed value (e.g. ExpandoObject, which
            // implements IDictionary<string,object> but not the non-generic IDictionary above).
            case IEnumerable<KeyValuePair<string, object>> objectValuedDict:
            {
                var childDepth = depth + 1;
                EnsureDepth(childDepth, policy, kind, path);
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in objectValuedDict)
                {
                    fields[pair.Key] = pair.Value;
                }

                sanitizedValue = SanitizeFields(fields, policy, childDepth, path, kind, redactedPaths, omittedPaths, cancellationToken);
                return true;
            }

            // Lists are recursed just like dictionaries -- closing Story 1.7's proof-only gap (b),
            // where an allowlisted field holding a list of dictionaries was never walked into.
            case IEnumerable list:
            {
                var childDepth = depth + 1;
                EnsureDepth(childDepth, policy, kind, path);
                sanitizedValue = SanitizeList(list, policy, childDepth, path, kind, redactedPaths, omittedPaths, cancellationToken);
                return true;
            }

            default:
                if (value.GetType().IsValueType)
                {
                    // A value-type leaf (numeric, bool, Guid, DateTimeOffset, enum, a custom
                    // struct, ...): opaque and atomic, safe to pass through unexamined.
                    sanitizedValue = value;
                    return true;
                }

                // An unrecognized reference type: never blindly pass through an unexamined shape
                // -- a class instance could carry an arbitrary unclassified secret sub-value (e.g.
                // via its own fields or a custom ToString()). Omit it, exactly like an unknown
                // field name.
                sanitizedValue = null;
                return false;
        }
    }

    /// <summary>
    /// Classifies a <see cref="JsonElement"/> by its <see cref="JsonValueKind"/>: an object is
    /// treated as a dictionary and recursed field-by-field; an array is treated as a list and
    /// recursed item-by-item; any other kind is an opaque JSON scalar leaf.
    /// </summary>
    private bool TrySanitizeJsonElement(
        JsonElement json,
        SanitizationPolicy policy,
        int depth,
        string path,
        string kind,
        List<string> redactedPaths,
        List<string> omittedPaths,
        CancellationToken cancellationToken,
        out object? sanitizedValue)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var childDepth = depth + 1;
                EnsureDepth(childDepth, policy, kind, path);
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in json.EnumerateObject())
                {
                    fields[property.Name] = property.Value;
                }

                sanitizedValue = SanitizeFields(fields, policy, childDepth, path, kind, redactedPaths, omittedPaths, cancellationToken);
                return true;
            }

            case JsonValueKind.Array:
            {
                var childDepth = depth + 1;
                EnsureDepth(childDepth, policy, kind, path);
                // JsonElement.EnumerateArray() walks an already-fully-parsed, already-in-memory
                // document -- handed directly to SanitizeList rather than materialized here first.
                sanitizedValue = SanitizeList(json.EnumerateArray(), policy, childDepth, path, kind, redactedPaths, omittedPaths, cancellationToken);
                return true;
            }

            case JsonValueKind.String:
                EnsureValueLength((json.GetString() ?? string.Empty).Length, policy, kind, path);
                sanitizedValue = json;
                return true;

            default:
                // Number/True/False/Null/Undefined: an opaque JSON scalar leaf, passed through as-is.
                sanitizedValue = json;
                return true;
        }
    }

    /// <summary>
    /// Sanitizes every item of a list reached through an allowlisted field: each item is
    /// classified via <see cref="TrySanitizeValue"/> just like a dictionary field's value (so a
    /// list of dictionaries has its own secret-classified sub-fields redacted). The item count is
    /// checked incrementally, one item at a time, during enumeration -- never by first buffering
    /// the whole sequence into memory -- so a very large or effectively infinite lazily-evaluated
    /// <see cref="IEnumerable"/> is rejected as soon as it crosses the limit instead of hanging or
    /// exhausting memory before that limit ever gets a chance to run.
    /// </summary>
    private List<object?> SanitizeList(
        IEnumerable list,
        SanitizationPolicy policy,
        int depth,
        string pathPrefix,
        string kind,
        List<string> redactedPaths,
        List<string> omittedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = new List<object?>();
        var index = 0;

        foreach (var item in list)
        {
            EnsureFieldCount(index + 1, policy, kind, pathPrefix);

            var itemPath = $"{pathPrefix}[{index}]";

            if (TrySanitizeValue(item, policy, depth, itemPath, kind, redactedPaths, omittedPaths, cancellationToken, out var sanitizedItem))
            {
                sanitized.Add(sanitizedItem);
            }
            else
            {
                omittedPaths.Add(itemPath);
            }

            index++;
        }

        return sanitized;
    }

    /// <summary>
    /// Redacts a secret-classified field's value as a single unit, regardless of shape: a string
    /// value is redacted via the composed <see cref="Redactor"/> as-is; any other shape
    /// (dictionary, list, number, boolean) is never serialized or traversed -- a fixed,
    /// non-leaking <see cref="NonStringSecretPlaceholder"/> is redacted in its place instead, so no
    /// information about the original value ever reaches the composed redactor, let alone the
    /// sanitized result. A throwing injected <see cref="Redactor"/> never escapes uncaught -- it is
    /// converted into a <see cref="SanitizationFailureException"/>, rejecting the whole payload
    /// fail-closed exactly like any other traversal failure.
    /// </summary>
    private object RedactWhole(object? value, string kind, string path)
    {
        var text = value switch
        {
            string s => s,
            null => string.Empty,
            _ => NonStringSecretPlaceholder,
        };

        try
        {
            return _redactor.Redact(text);
        }
        catch (Exception ex) when (ex is not SanitizationFailureException)
        {
            throw new SanitizationFailureException(kind, SanitizationFailureReason.RedactorFailed, path);
        }
    }

    /// <summary>Converts any <see cref="System.Collections.IDictionary"/> (whatever its key/value types) into a string-keyed field map, dropping any entry whose key is not itself a string -- a non-string key cannot be classified by name at all, and is never coerced into one.</summary>
    private static Dictionary<string, object?> ToStringKeyedFields(System.Collections.IDictionary dictionary)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key)
            {
                fields[key] = entry.Value;
            }
        }

        return fields;
    }

    private static void EnsureDepth(int depth, SanitizationPolicy policy, string kind, string? fieldPath)
    {
        if (depth > policy.MaxDepth)
        {
            throw new SanitizationFailureException(kind, SanitizationFailureReason.DepthLimitExceeded, fieldPath);
        }
    }

    private static void EnsureFieldCount(int count, SanitizationPolicy policy, string kind, string? fieldPath)
    {
        if (count > policy.MaxFieldCount)
        {
            throw new SanitizationFailureException(kind, SanitizationFailureReason.FieldCountLimitExceeded, fieldPath);
        }
    }

    private static void EnsureValueLength(int length, SanitizationPolicy policy, string kind, string fieldPath)
    {
        if (length > policy.MaxValueLength)
        {
            throw new SanitizationFailureException(kind, SanitizationFailureReason.ValueLengthLimitExceeded, fieldPath);
        }
    }

    /// <summary>Rejects fail-closed rather than building a path from an unbounded field name -- mirrors <see cref="EnsureValueLength"/>, against the dedicated <see cref="SanitizationPolicy.MaxFieldNameLength"/> limit.</summary>
    private static void EnsureFieldNameLength(int length, SanitizationPolicy policy, string kind, string? pathPrefix)
    {
        if (length > policy.MaxFieldNameLength)
        {
            throw new SanitizationFailureException(kind, SanitizationFailureReason.FieldNameLengthExceeded, pathPrefix);
        }
    }

    /// <summary>Rejects fail-closed when a field name contains a character reserved for building dotted/indexed paths, so a constructed path can never collide with a genuinely nested one.</summary>
    private static void EnsureSafeFieldName(string key, string kind)
    {
        if (key.IndexOfAny(UnsafeFieldNameCharacters) >= 0)
        {
            throw new SanitizationFailureException(kind, SanitizationFailureReason.UnsafeFieldName, key);
        }
    }
}
