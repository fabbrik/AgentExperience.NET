using System.Collections.Frozen;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Sanitization;

namespace AgentExperience.Core.DependencyInjection;

/// <summary>
/// Safe defaults for the two host decisions <see cref="AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceCore"/>
/// requires: what capture may keep, and how much of it. The one-call setup in the MAF adapter uses them unless the host
/// passes its own.
/// </summary>
/// <remarks>
/// They err on the side of keeping too little. No tool argument value is captured until the host allowlists the
/// argument by name, and a field whose name looks like a secret is redacted wherever it appears.
/// </remarks>
public static class AgentExperienceDefaults
{
    /// <summary>The structural limits both default policies apply: nesting depth.</summary>
    public const int MaxDepth = 4;

    /// <summary>The structural limits both default policies apply: entries per dictionary or list.</summary>
    public const int MaxFieldCount = 50;

    /// <summary>
    /// The structural limits both default policies apply: characters per string value. Far above
    /// <see cref="CaptureLimits"/>, because the sanitizer rejects an over-long value outright, before capture's own
    /// limits replace an over-long result or error with a placeholder: long tool output is truncated, not refused.
    /// </summary>
    public const int MaxValueLength = 1_000_000;

    /// <summary>The structural limits both default policies apply: characters per field name.</summary>
    public const int MaxFieldNameLength = 100;

    /// <summary>
    /// The field names redacted as secrets, matched case-insensitively: <c>password</c>, <c>secret</c>, <c>token</c>,
    /// <c>apiKey</c>, <c>api_key</c>, <c>authorization</c> and <c>connectionString</c>. Names only: a secret under any
    /// other field name is not found by this list (see <see cref="DefaultSanitizer"/>).
    /// </summary>
    public static IReadOnlySet<string> SecretFieldNames { get; } = new[]
    {
        "password",
        "secret",
        "token",
        "apiKey",
        "api_key",
        "authorization",
        "connectionString",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The default sanitization: <see cref="SanitizationKinds.ToolArguments"/> keeps <b>no</b> argument field, so no
    /// argument value is captured until the host allowlists it, and <see cref="SanitizationKinds.ToolResult"/> keeps
    /// the <see cref="SanitizationKinds.ValueField"/> field, so tool and attempt results and errors are kept. Both
    /// redact <see cref="SecretFieldNames"/> and apply <see cref="MaxDepth"/>, <see cref="MaxFieldCount"/>,
    /// <see cref="MaxValueLength"/> and <see cref="MaxFieldNameLength"/>.
    /// </summary>
    /// <remarks>
    /// To keep an argument, allowlist it by name with <see cref="SanitizationAllowing"/>. A value longer than
    /// <see cref="MaxValueLength"/> rejects its whole payload rather than being cut; shorter results and errors over
    /// <see cref="CaptureLimits"/> are replaced with a placeholder by capture.
    /// </remarks>
    public static SanitizationOptions Sanitization { get; } = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        [SanitizationKinds.ToolArguments] = ToolArgumentsPolicy,
        [SanitizationKinds.ToolResult] = ToolResultPolicy,
    }.ToFrozenDictionary(StringComparer.Ordinal));

    /// <summary>
    /// <see cref="Sanitization"/>, except that the named tool arguments are kept. A name that is also in
    /// <see cref="SecretFieldNames"/> is still redacted.
    /// </summary>
    /// <param name="argumentNames">The argument names to keep, matched exactly (ordinal).</param>
    /// <returns>The sanitization options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="argumentNames"/> or one of its names is <see langword="null"/>.</exception>
    public static SanitizationOptions SanitizationAllowing(params string[] argumentNames)
    {
        ArgumentNullException.ThrowIfNull(argumentNames);
        foreach (var name in argumentNames)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(argumentNames));
        }

        return new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
        {
            [SanitizationKinds.ToolArguments] = ToolArgumentsPolicy with { AllowedFieldNames = argumentNames.ToFrozenSet(StringComparer.Ordinal) },
            [SanitizationKinds.ToolResult] = ToolResultPolicy,
        }.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>The default <see cref="SanitizationKinds.ToolArguments"/> policy: no argument kept, secrets redacted.</summary>
    public static SanitizationPolicy ToolArgumentsPolicy => new(
        AllowedFieldNames: FrozenSet<string>.Empty,
        SecretFieldNames: SecretFieldNames,
        MaxDepth: MaxDepth,
        MaxFieldCount: MaxFieldCount,
        MaxValueLength: MaxValueLength,
        MaxFieldNameLength: MaxFieldNameLength);

    /// <summary>The default <see cref="SanitizationKinds.ToolResult"/> policy: the text field kept, secrets redacted.</summary>
    public static SanitizationPolicy ToolResultPolicy => new(
        AllowedFieldNames: new[] { SanitizationKinds.ValueField }.ToFrozenSet(StringComparer.Ordinal),
        SecretFieldNames: SecretFieldNames,
        MaxDepth: MaxDepth,
        MaxFieldCount: MaxFieldCount,
        MaxValueLength: MaxValueLength,
        MaxFieldNameLength: MaxFieldNameLength);

    /// <summary>
    /// The default capture limits: 10 attempts per run, 50 tool calls per attempt, and 4,000 characters for a result
    /// and for an error.
    /// </summary>
    public static CaptureLimits CaptureLimits { get; } = new(
        MaxAttemptsPerRun: 10,
        MaxToolCallsPerAttempt: 50,
        MaxResultLength: 4_000,
        MaxErrorLength: 4_000);
}
