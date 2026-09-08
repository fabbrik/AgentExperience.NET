namespace AgentExperience.Abstractions;

/// <summary>
/// Identifies the tenancy and ownership boundary a piece of captured experience belongs to,
/// or that a request is scoped within. <see cref="Scope"/> is a request/record concern and is
/// deliberately distinct from <see cref="AuthorizationContext"/>, which represents what a host
/// has already established the caller is permitted to do. No public operation may accept a
/// request-supplied <see cref="Scope"/> as a substitute for host authorization.
/// </summary>
/// <param name="TenantId">Required. The tenant boundary experience can never cross implicitly.</param>
/// <param name="ApplicationId">Required. The application/product boundary within the tenant.</param>
/// <param name="ProjectId">Required. The project or workspace boundary within the application.</param>
/// <param name="TeamId">Optional. The team that owns or is scoped to this experience, if applicable.</param>
/// <param name="AgentId">Optional. The specific agent identity the experience is scoped to, if applicable.</param>
/// <param name="UserId">Optional. The specific end-user the experience is scoped to, if applicable.</param>
public sealed record Scope(
    string TenantId,
    string ApplicationId,
    string ProjectId,
    string? TeamId = null,
    string? AgentId = null,
    string? UserId = null);

/// <summary>
/// Represents what a host application has already established a caller is permitted to do,
/// independent of any identity provider. This is deliberately a different shape from
/// <see cref="Scope"/>: <see cref="AuthorizationContext"/> is the trust boundary a host asserts
/// before a request is evaluated; <see cref="Scope"/> only selects within it. Request-supplied
/// scope can narrow an <see cref="AuthorizationContext"/> but can never widen it.
/// </summary>
/// <param name="TenantId">The tenant the host has authorized this caller to act within.</param>
/// <param name="PrincipalId">An opaque, host-assigned identifier for the authorized caller. Not tied to any specific identity-provider shape (no claims, tokens, or provider-specific types).</param>
/// <param name="Roles">The roles or capabilities the host has granted this caller, as opaque strings.</param>
/// <param name="IssuedAt">When the host established this authorization context.</param>
public sealed record AuthorizationContext(
    string TenantId,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    DateTimeOffset IssuedAt);

/// <summary>
/// A fingerprint of the runtime environment an <see cref="ExperienceRun"/> executed in, captured
/// for reproducibility and diagnosis without depending on any specific telemetry implementation.
/// </summary>
/// <param name="HostName">Identifies the host process or machine that executed the run.</param>
/// <param name="RuntimeVersion">The .NET (or other) runtime version the run executed under.</param>
/// <param name="OperatingSystem">The operating system description the run executed under.</param>
/// <param name="ApplicationVersion">Optional. The version of the host application producing the run.</param>
/// <param name="Metadata">Additional free-form, already-sanitized environment metadata.</param>
public sealed record EnvironmentFingerprint(
    string HostName,
    string RuntimeVersion,
    string OperatingSystem,
    string? ApplicationVersion,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Records where an <see cref="ExperienceRun"/> (or any captured artifact) came from, so
/// downstream consumers can trace it back to the producing adapter without depending on that
/// adapter's package at compile time.
/// </summary>
/// <param name="Source">Identifies the producer, e.g. an adapter package name.</param>
/// <param name="SourceVersion">Optional. The version of the producing adapter/component.</param>
/// <param name="RecordedAt">When this provenance record was captured.</param>
/// <param name="CorrelationId">Optional. An external correlation identifier (e.g. a trace ID) for cross-system tracing.</param>
public sealed record Provenance(
    string Source,
    string? SourceVersion,
    DateTimeOffset RecordedAt,
    string? CorrelationId);
