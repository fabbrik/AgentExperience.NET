namespace AgentExperience.Core.Sanitization;

/// <summary>
/// The <see cref="AgentExperience.Abstractions.RawPayload.Kind"/> values Core's capture submits to the
/// <see cref="AgentExperience.Abstractions.ISanitizer"/>, so a <see cref="SanitizationOptions"/> can key its policies on
/// a named constant instead of a string typed by hand.
/// </summary>
/// <remarks>
/// A <c>Kind</c> with no policy is rejected outright by <see cref="DefaultSanitizer"/>, so a host that configures its own
/// <see cref="SanitizationOptions"/> needs a policy for both kinds below, or every tool call and every attempt result is
/// refused. <see cref="AgentExperience.Core.DependencyInjection.AgentExperienceDefaults.Sanitization"/> is a safe
/// starting point.
/// </remarks>
public static class SanitizationKinds
{
    /// <summary>
    /// A tool call's arguments, one field per argument name. Only allowlisted argument names are kept; every other
    /// argument is omitted, and a secret-classified one is redacted.
    /// </summary>
    public const string ToolArguments = "ToolArguments";

    /// <summary>
    /// A tool call's result or error, and an attempt's result or error. Each is submitted as a single text field named
    /// <see cref="ValueField"/>, so a policy for this kind keeps the text only when it allowlists that field.
    /// </summary>
    public const string ToolResult = "ToolResult";

    /// <summary>
    /// The one field a <see cref="ToolResult"/> payload carries its text under. Allowlist it to keep results and
    /// errors; leave it out and they are omitted.
    /// </summary>
    public const string ValueField = "value";
}
