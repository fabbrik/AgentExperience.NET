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
    string? UserId = null)
{
    /// <summary>
    /// Whether <paramref name="other"/> lies inside the boundary an <see cref="ExperienceGrant"/> can
    /// never cross: the same <see cref="TenantId"/>, <see cref="ApplicationId"/>, and
    /// <see cref="ProjectId"/>, compared ordinally and case-sensitively. The optional fields are
    /// deliberately not compared, because relaxing exactly those three is all a grant may ever do.
    /// </summary>
    /// <remarks>
    /// This is not an authorization check and it never widens a read. It exists so that the
    /// defence-in-depth checks over records a store has already returned -- retrieval's own
    /// "is this candidate in scope" guard and the pre-injection re-check -- can keep rejecting a
    /// record from another tenant, application, or project while still accepting a record that was
    /// legitimately read through a grant. Whether a grant actually permitted that read is decided in
    /// the persistence layer's query predicate, never here.
    /// </remarks>
    /// <param name="other">The scope to compare against.</param>
    /// <returns><see langword="true"/> when both scopes share the three required fields; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool SharesGrantBoundary(Scope other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(TenantId, other.TenantId, StringComparison.Ordinal)
            && string.Equals(ApplicationId, other.ApplicationId, StringComparison.Ordinal)
            && string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal);
    }
}

/// <summary>
/// Represents what a host application has already established a caller is permitted to do,
/// independent of any identity provider. This is deliberately a different shape from
/// <see cref="Scope"/>: <see cref="AuthorizationContext"/> is the trust boundary a host asserts
/// before a request is evaluated; <see cref="Scope"/> only selects within it. Request-supplied
/// scope can narrow an <see cref="AuthorizationContext"/> but can never widen it.
/// </summary>
/// <remarks>
/// The optional bounds (<see cref="ApplicationId"/>, <see cref="ProjectId"/>, <see cref="TeamId"/>,
/// <see cref="AgentId"/>, <see cref="UserId"/>) restrict which request scopes this context permits.
/// A non-null bound must equal the corresponding <see cref="Scope"/> field exactly (ordinal,
/// case-sensitive); a <see langword="null"/> bound leaves that field unrestricted.
/// <see cref="TenantId"/> must always match. See <see cref="Permits(Scope)"/>.
/// </remarks>
/// <param name="TenantId">The tenant the host has authorized this caller to act within.</param>
/// <param name="PrincipalId">An opaque, host-assigned identifier for the authorized caller. Not tied to any specific identity-provider shape (no claims, tokens, or provider-specific types).</param>
/// <param name="Roles">The roles or capabilities the host has granted this caller, as opaque strings.</param>
/// <param name="IssuedAt">When the host established this authorization context.</param>
/// <param name="ApplicationId">Optional bound. When non-null, only request scopes with exactly this <see cref="Scope.ApplicationId"/> are permitted.</param>
/// <param name="ProjectId">Optional bound. When non-null, only request scopes with exactly this <see cref="Scope.ProjectId"/> are permitted.</param>
/// <param name="TeamId">Optional bound. When non-null, only request scopes with exactly this <see cref="Scope.TeamId"/> are permitted.</param>
/// <param name="AgentId">Optional bound. When non-null, only request scopes with exactly this <see cref="Scope.AgentId"/> are permitted.</param>
/// <param name="UserId">Optional bound. When non-null, only request scopes with exactly this <see cref="Scope.UserId"/> are permitted.</param>
public sealed record AuthorizationContext(
    string TenantId,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    DateTimeOffset IssuedAt,
    string? ApplicationId = null,
    string? ProjectId = null,
    string? TeamId = null,
    string? AgentId = null,
    string? UserId = null)
{
    /// <summary>
    /// Determines whether this host-established authorization permits a request in
    /// <paramref name="scope"/>. <see cref="TenantId"/> must equal <see cref="Scope.TenantId"/>, and
    /// every non-null bound must equal the corresponding scope field; all comparisons are ordinal and
    /// case-sensitive. A <see langword="null"/> bound leaves its field unrestricted, and a
    /// <see langword="null"/>, empty, or whitespace <see cref="TenantId"/> permits nothing. This never widens
    /// authority: the request scope is only ever checked against the context, never trusted on its own.
    /// </summary>
    /// <param name="scope">The request scope to check.</param>
    /// <returns><see langword="true"/> when the scope lies within this authorization; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public bool Permits(Scope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return !string.IsNullOrWhiteSpace(TenantId)
            && string.Equals(TenantId, scope.TenantId, StringComparison.Ordinal)
            && BoundMatches(ApplicationId, scope.ApplicationId)
            && BoundMatches(ProjectId, scope.ProjectId)
            && BoundMatches(TeamId, scope.TeamId)
            && BoundMatches(AgentId, scope.AgentId)
            && BoundMatches(UserId, scope.UserId);
    }

    private static bool BoundMatches(string? bound, string? value) =>
        bound is null || string.Equals(bound, value, StringComparison.Ordinal);
}

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
