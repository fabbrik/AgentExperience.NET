namespace AgentExperience.Abstractions;

/// <summary>
/// Administrator authority for the grant-mutating operations of <see cref="IExperienceGrantStore"/>.
/// It is a distinct, explicit input the host constructs: it is never derived from an
/// <see cref="AuthorizationContext"/>, from <see cref="AuthorizationContext.Roles"/>, or from the
/// requesting <see cref="Scope"/>. A host that cannot name an administrator has no administrator, and
/// the call is <see cref="ExperienceGrantOutcome.Denied"/>.
/// </summary>
/// <remarks>
/// This is authority to <em>administer sharing</em>, and nothing else. It does not stand in for the
/// caller's own authorization: every grant call still takes an <see cref="AuthorizationContext"/> and
/// still applies it to the record's owner scope.
/// </remarks>
/// <param name="AdministratorPrincipalId">
/// An opaque, host-assigned identifier for the administrator, recorded on the grant and on its audit
/// events. Must not be empty or whitespace.
/// </param>
/// <param name="AuthorizedAt">
/// When the host established this administrator authority. Recorded on the audit event, so the trail
/// says when the authority the action was taken under was granted, not only when the action happened.
/// Must be set.
/// </param>
public sealed record GrantAdministration(
    string AdministratorPrincipalId,
    DateTimeOffset AuthorizedAt);

/// <summary>
/// An explicit, audited permission for one recipient <see cref="Scope"/> to <em>read</em> one
/// <see cref="ExperienceRecord"/> that another scope owns, until it expires or is revoked.
/// </summary>
/// <remarks>
/// <para>
/// A grant relaxes only the optional scope fields -- <see cref="Scope.TeamId"/>,
/// <see cref="Scope.AgentId"/>, <see cref="Scope.UserId"/>. <see cref="RecipientScope"/> always keeps
/// the same <see cref="Scope.TenantId"/>, <see cref="Scope.ApplicationId"/>, and
/// <see cref="Scope.ProjectId"/> as <see cref="RecordScope"/>.
/// </para>
/// <para>
/// A grant permits reading only -- <see cref="IExperienceRecordStore.GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>, text retrieval, and
/// vector retrieval, and therefore injection, which re-reads through the same path. Creating,
/// committing lifecycle changes, reading lifecycle history, submitting feedback, and issuing further
/// grants are never inferred from a grant and still require the caller's own authority.
/// </para>
/// </remarks>
/// <param name="GrantId">The grant's identity. Also the idempotency key a re-issued create collides on.</param>
/// <param name="ExperienceId">The single record this grant names.</param>
/// <param name="RecordScope">The scope that owns the record. Copied from the stored record, never from caller input.</param>
/// <param name="RecipientScope">The scope this grant permits to read the record.</param>
/// <param name="Reason">Why the grant was issued. Recorded on the grant and on its issue event.</param>
/// <param name="AdministratorPrincipalId">The <see cref="GrantAdministration.AdministratorPrincipalId"/> that issued it.</param>
/// <param name="IssuedAt">When the grant was issued, taken from the database's own clock.</param>
/// <param name="ExpiresAt">When the grant stops permitting reads, compared against the database's own clock.</param>
/// <param name="RevokedAt">When the grant was revoked, or <see langword="null"/> while it stands.</param>
/// <param name="RevocationReason">Why it was revoked, or <see langword="null"/> while it stands.</param>
public sealed record ExperienceGrant(
    Guid GrantId,
    Guid ExperienceId,
    Scope RecordScope,
    Scope RecipientScope,
    string Reason,
    string AdministratorPrincipalId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevocationReason)
{
    /// <summary>The smallest permitted <see cref="IExperienceGrantStore.ListAsync"/> limit.</summary>
    public const int MinListLimit = 1;

    /// <summary>The largest permitted <see cref="IExperienceGrantStore.ListAsync"/> limit.</summary>
    public const int MaxListLimit = 500;

    /// <summary>The <see cref="IExperienceGrantStore.ListAsync"/> limit used when none is specified.</summary>
    public const int DefaultListLimit = 100;
}

/// <summary>
/// A request to issue one <see cref="ExperienceGrant"/>.
/// </summary>
/// <param name="GrantId">
/// The identity to issue the grant under. Must not be <see cref="Guid.Empty"/>. A
/// <see cref="GrantId"/> already stored, in any scope, is
/// <see cref="ExperienceGrantOutcome.Conflict"/> and writes nothing.
/// </param>
/// <param name="ExperienceId">The record to share. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="RecordScope">The exact scope the record must lie in. Never treated as authority.</param>
/// <param name="RecipientScope">
/// The scope to permit. Must keep <paramref name="RecordScope"/>'s <see cref="Scope.TenantId"/>,
/// <see cref="Scope.ApplicationId"/>, and <see cref="Scope.ProjectId"/>; anything else is
/// <see cref="ExperienceGrantOutcome.Invalid"/> with the field path, and nothing is written. It must
/// also differ from <paramref name="RecordScope"/>: a grant to the scope that already owns the record
/// permits nothing and is rejected rather than stored as a misleading audit row.
/// <para>
/// A <see langword="null"/> optional field is <em>not</em> a wildcard, but it is not "one sibling
/// team" either: scope matching is exact, so a recipient of
/// <c>(tenant, application, project, TeamId: null, AgentId: null, UserId: null)</c> permits exactly
/// the requests whose scope has all three null -- the project-level scope. Name every optional field
/// the recipient actually uses when the intent is to share with one team, agent, or user.
/// </para>
/// </param>
/// <param name="Reason">Why the grant is being issued. Must not be empty or whitespace.</param>
/// <param name="ExpiresAt">
/// When the grant stops permitting reads. Must be later than the moment the database issues it,
/// which is the database's own clock rather than the caller's.
/// <para>
/// It is also bounded above: an implementation applies a host-configured maximum grant lifetime, and
/// an expiry beyond it is <see cref="ExperienceGrantOutcome.Invalid"/> on this field with nothing
/// written. There is therefore no such thing as a permanent grant --
/// <see cref="DateTimeOffset.MaxValue"/> is refused like any other over-long expiry. An expiry
/// exactly at the maximum is accepted.
/// </para>
/// </param>
public sealed record ExperienceGrantRequest(
    Guid GrantId,
    Guid ExperienceId,
    Scope RecordScope,
    Scope RecipientScope,
    string Reason,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A request to revoke one <see cref="ExperienceGrant"/>. Revocation appends another audit event and
/// never deletes the grant or its history.
/// </summary>
/// <param name="GrantId">The grant to revoke. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="RecordScope">The exact owner scope the grant must lie in. Never treated as authority.</param>
/// <param name="Reason">Why the grant is being revoked. Must not be empty or whitespace.</param>
public sealed record ExperienceGrantRevocation(
    Guid GrantId,
    Scope RecordScope,
    string Reason);

/// <summary>
/// What one <see cref="ExperienceGrantEvent"/> records.
/// </summary>
public enum ExperienceGrantAction
{
    /// <summary>The grant was issued.</summary>
    Issued,

    /// <summary>The grant was revoked. The grant row and its issue event both stay stored.</summary>
    Revoked,
}

/// <summary>
/// One appended entry in a grant's audit trail. Events are never updated or deleted, so issuing and
/// then revoking a grant leaves both entries and revocation can never erase that access was given.
/// </summary>
/// <remarks>
/// The trail records <em>administration</em> -- who allowed what, until when, and when they stopped
/// allowing it -- so this history answers "who permitted this?". Who actually <em>read</em> the
/// record is a separate, optional ledger: <see cref="IExperienceGrantAccessLog"/>, which a host wires
/// on its own and which records the reads a grant delivered rather than the permissions it granted.
/// </remarks>
/// <param name="EventId">The event's identity.</param>
/// <param name="GrantId">The grant this event is about.</param>
/// <param name="ExperienceId">The record the grant names.</param>
/// <param name="Action">What happened.</param>
/// <param name="RecordScope">The record's owner scope, as stored on the grant.</param>
/// <param name="RecipientScope">The scope the grant permits, as stored on the grant.</param>
/// <param name="Reason">Why the grant was issued or revoked, as given for this action.</param>
/// <param name="AdministratorPrincipalId">The administrator who took this action.</param>
/// <param name="AdministratorAuthorizedAt">When the host established that administrator's authority.</param>
/// <param name="ExpiresAt">The grant's expiry, as stored when this event was appended.</param>
/// <param name="OccurredAt">When the action happened, from the database's own clock.</param>
public sealed record ExperienceGrantEvent(
    Guid EventId,
    Guid GrantId,
    Guid ExperienceId,
    ExperienceGrantAction Action,
    Scope RecordScope,
    Scope RecipientScope,
    string Reason,
    string AdministratorPrincipalId,
    DateTimeOffset AdministratorAuthorizedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset OccurredAt);

/// <summary>
/// The result of <see cref="IExperienceGrantStore.GetHistoryAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Grant">The grant as it stands now when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Found"/>; otherwise <see langword="null"/>.</param>
/// <param name="Events">Its events, oldest first, when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceGrantHistoryResult(
    ExperienceGrantOutcome Outcome,
    ExperienceGrant? Grant,
    IReadOnlyList<ExperienceGrantEvent> Events,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// The disposition an <see cref="IExperienceGrantStore"/> operation reached.
/// </summary>
public enum ExperienceGrantOutcome
{
    /// <summary>The grant and its issue event were committed together.</summary>
    Created,

    /// <summary>The grant was revoked and its revocation event appended, in one transaction.</summary>
    Revoked,

    /// <summary>The requested grants were read. A record with no grants is still <see cref="Found"/>.</summary>
    Found,

    /// <summary>
    /// No such record, or no such grant, within the requested owner scope -- including when it exists
    /// in another scope. Nothing was written.
    /// </summary>
    NotFound,

    /// <summary>
    /// There was no administrator authority, or the request scope lies outside the host-established
    /// authorization. No storage was accessed and nothing was written.
    /// </summary>
    Denied,

    /// <summary>The request was malformed. See the result's validation errors. Nothing was written.</summary>
    Invalid,

    /// <summary>
    /// A grant with the same <see cref="ExperienceGrant.GrantId"/> is already stored in some scope, or
    /// an active grant over the same record already permits the same recipient scope. Nothing was
    /// written. At most one active grant may exist per (record, recipient scope) pair, so revoking the
    /// grant an administrator knows about actually ends that recipient's access.
    /// </summary>
    Conflict,

    /// <summary>The grant was already revoked. Nothing was written and its history is unchanged.</summary>
    AlreadyRevoked,
}

/// <summary>
/// The result of <see cref="IExperienceGrantStore.CreateAsync"/> or
/// <see cref="IExperienceGrantStore.RevokeAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Grant">
/// The stored grant when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Created"/>,
/// <see cref="ExperienceGrantOutcome.Revoked"/>, or <see cref="ExperienceGrantOutcome.AlreadyRevoked"/>;
/// otherwise <see langword="null"/>.
/// </param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceGrantResult(
    ExperienceGrantOutcome Outcome,
    ExperienceGrant? Grant,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// The result of <see cref="IExperienceGrantStore.ListAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Grants">
/// Every grant naming the record in the requested owner scope, revoked and expired ones included,
/// oldest first, when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Found"/>; otherwise
/// empty.
/// </param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceGrantOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceGrantListResult(
    ExperienceGrantOutcome Outcome,
    IReadOnlyList<ExperienceGrant> Grants,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// Port for administering explicit, audited sharing grants over individual
/// <see cref="ExperienceRecord"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Every mutating operation takes two separate things: the host-established
/// <see cref="AuthorizationContext"/>, applied to the record's owner scope exactly as it is for any
/// other store operation, and a <see cref="GrantAdministration"/>, which is the explicit
/// administrator authority. Neither is derived from the other, and a missing
/// <see cref="GrantAdministration"/> is <see cref="ExperienceGrantOutcome.Denied"/> before any
/// storage is accessed.
/// </para>
/// <para>
/// <b>Grants are enforced by the store, not by this port.</b> An implementation applies an active
/// grant inside the same query predicate as the exact scope match, so a read can never return more
/// than the predicate permitted. Nothing in Core or in an adapter widens a read in application code.
/// </para>
/// <para>
/// Expected conditions return typed results; infrastructure failures throw
/// <see cref="ExperienceStoreException"/>; caller cancellation surfaces as an unwrapped
/// <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface IExperienceGrantStore
{
    /// <summary>
    /// Issues one grant and appends its audit event in a single transaction: both writes commit
    /// together or neither does.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do. Applied to <see cref="ExperienceGrantRequest.RecordScope"/>.</param>
    /// <param name="administration">The explicit administrator authority. <see langword="null"/> is <see cref="ExperienceGrantOutcome.Denied"/>.</param>
    /// <param name="request">The grant to issue.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceGrantOutcome.Created"/>, <see cref="ExperienceGrantOutcome.Invalid"/>,
    /// <see cref="ExperienceGrantOutcome.Denied"/>, <see cref="ExperienceGrantOutcome.Conflict"/>, or
    /// <see cref="ExperienceGrantOutcome.NotFound"/> when no such record exists in the owner scope.
    /// </returns>
    Task<ExperienceGrantResult> CreateAsync(
        AuthorizationContext authorization,
        GrantAdministration? administration,
        ExperienceGrantRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes one grant and appends its revocation event in a single transaction. The grant row and
    /// both of its events stay stored: revocation is an append, never a delete.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do. Applied to <see cref="ExperienceGrantRevocation.RecordScope"/>.</param>
    /// <param name="administration">The explicit administrator authority. <see langword="null"/> is <see cref="ExperienceGrantOutcome.Denied"/>.</param>
    /// <param name="revocation">The grant to revoke.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceGrantOutcome.Revoked"/>, <see cref="ExperienceGrantOutcome.AlreadyRevoked"/>,
    /// <see cref="ExperienceGrantOutcome.NotFound"/>, <see cref="ExperienceGrantOutcome.Invalid"/>, or
    /// <see cref="ExperienceGrantOutcome.Denied"/>.
    /// </returns>
    Task<ExperienceGrantResult> RevokeAsync(
        AuthorizationContext authorization,
        GrantAdministration? administration,
        ExperienceGrantRevocation revocation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every grant naming <paramref name="experienceId"/> within exactly
    /// <paramref name="recordScope"/>, revoked and expired ones included, oldest first. Reading the
    /// grants over a record is an owner-scope operation: a grant never confers the right to enumerate
    /// the grants over the record it names.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="recordScope">The exact owner scope to list within. Never treated as authority.</param>
    /// <param name="experienceId">The record whose grants to list. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="limit">Maximum number of grants to return, from <see cref="ExperienceGrant.MinListLimit"/> to <see cref="ExperienceGrant.MaxListLimit"/>.</param>
    /// <returns>
    /// <see cref="ExperienceGrantOutcome.Found"/> (possibly with no grants),
    /// <see cref="ExperienceGrantOutcome.NotFound"/> when no such record exists in
    /// <paramref name="recordScope"/> -- which is a different answer from a record that simply has no
    /// grants -- <see cref="ExperienceGrantOutcome.Invalid"/>, or
    /// <see cref="ExperienceGrantOutcome.Denied"/>.
    /// </returns>
    Task<ExperienceGrantListResult> ListAsync(
        AuthorizationContext authorization,
        Scope recordScope,
        Guid experienceId,
        CancellationToken cancellationToken,
        int limit = ExperienceGrant.DefaultListLimit);

    /// <summary>
    /// Reads one grant's audit trail within exactly <paramref name="recordScope"/>: the grant as it
    /// stands now plus every appended event, oldest first. Mirrors
    /// <see cref="IExperienceRecordStore.GetHistoryAsync"/>, and like it is an owner-scope read: a
    /// grant never confers the right to read its own history.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="recordScope">The exact owner scope to read within. Never treated as authority.</param>
    /// <param name="grantId">The grant whose history to read. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceGrantOutcome.Found"/>, <see cref="ExperienceGrantOutcome.NotFound"/>, <see cref="ExperienceGrantOutcome.Invalid"/>, or <see cref="ExperienceGrantOutcome.Denied"/>.</returns>
    Task<ExperienceGrantHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        Scope recordScope,
        Guid grantId,
        CancellationToken cancellationToken);
}
