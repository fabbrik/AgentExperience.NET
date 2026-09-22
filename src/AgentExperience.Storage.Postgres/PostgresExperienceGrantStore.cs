using System.Data.Common;
using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceGrantStore"/> over PostgreSQL with plain Npgsql. It follows exactly the order
/// <see cref="PostgresExperienceRecordStore"/> uses -- validate the request, check the explicit
/// administrator authority, check the request against the host-established
/// <see cref="AuthorizationContext"/>, and only then open a connection -- and translates failures the
/// same way. The schema must already exist: <c>0005_create_experience_grants.sql</c> creates
/// <c>experience_grants</c> and its append-only <c>experience_grant_events</c> log, and the host
/// applies it by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two authorities, never one.</b> A grant-mutating call needs the caller's own
/// <see cref="AuthorizationContext"/> over the record's owner scope <em>and</em> a
/// <see cref="GrantAdministration"/> the host constructed. Neither is derived from the other, and
/// administrator authority is never read out of <see cref="AuthorizationContext.Roles"/>, out of a
/// role string, or out of the requesting scope. A missing administrator is
/// <see cref="ExperienceGrantOutcome.Denied"/> before any connection opens.
/// </para>
/// <para>
/// <b>Both writes or neither.</b> Issuing a grant inserts the grant row and its <c>Issued</c> event in
/// one transaction on one connection; revoking updates the row and appends a <c>Revoked</c> event in
/// another. Nothing is ever deleted, so a revoked grant keeps both of its events and the fact that
/// access was once given cannot be erased.
/// </para>
/// <para>
/// <b>The owner scope is copied, never asserted.</b> The insert's source row is the canonical record
/// itself, matched on the exact owner scope, so a grant naming a record that does not exist in that
/// scope writes nothing (<see cref="ExperienceGrantOutcome.NotFound"/>) and a stored grant's owner
/// scope can never disagree with the record it names. That is also why the table has no foreign key:
/// a missing record is a typed outcome here rather than an infrastructure failure.
/// </para>
/// <para>
/// <b>This store never reads a record.</b> Issuing or listing grants tells the caller nothing about
/// the record's contents, and a grant confers no authority here: listing the grants over a record is
/// an owner-scope operation, and a recipient cannot issue, revoke, or enumerate anything.
/// </para>
/// </remarks>
public sealed class PostgresExperienceGrantStore : IExperienceGrantStore
{
    /// <summary>The append-only grant audit log. Created by <c>0005_create_experience_grants.sql</c>.</summary>
    internal const string EventsTable = "agent_experience.experience_grant_events";

    /// <summary>
    /// The grant columns every read selects, in the order <see cref="DecodeGrant"/> expects (ordinals 0-19).
    /// </summary>
    private const string GrantColumns =
        "grant_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
        "recipient_team_id, recipient_agent_id, recipient_user_id, " +
        "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason";

    /// <summary>
    /// The conditional insert. The source row is the canonical record, matched on the exact owner
    /// scope, so nothing is written unless that record exists exactly there; the owner scope columns
    /// are copied from it rather than from caller input. <c>issued_at</c> is the database's own clock,
    /// which is the same clock the read predicate compares <c>expires_at</c> against.
    /// <para>
    /// <b>The maximum lifetime is enforced here too, against that same clock.</b> The client check in
    /// <see cref="ExperienceRecordValidator.ValidateGrantRequest"/> produces the friendly message, but
    /// it measures from the caller's clock; this statement measures from the one that actually stamps
    /// <c>issued_at</c>, so a caller whose clock runs behind cannot buy itself a longer grant. An
    /// over-long expiry is written as <c>NULL</c> into a <c>NOT NULL</c> column rather than filtered
    /// out by the <c>WHERE</c>: filtering would make it indistinguishable from "no such record" and
    /// report <see cref="ExperienceGrantOutcome.NotFound"/>, while the not-null violation names the
    /// column and is reported on the field the caller got wrong. Nothing is written either way.
    /// </para>
    /// </summary>
    private static readonly string InsertGrantSql =
        $"INSERT INTO {PostgresExperienceRecordStore.GrantsTable} ({GrantColumns}) " +
        "SELECT @grant_id, r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, " +
        "@recipient_tenant_id, @recipient_application_id, @recipient_project_id, " +
        "@recipient_team_id, @recipient_agent_id, @recipient_user_id, " +
        "@reason, @administrator_principal_id, now(), " +
        "(CASE WHEN @expires_at <= now() + @max_lifetime::interval THEN @expires_at END), NULL, NULL " +
        $"FROM {PostgresExperienceRecordStore.Table} r " +
        $"WHERE r.experience_id = @experience_id AND {PostgresExperienceRecordStore.RecordScopePredicate} " +
        $"RETURNING {GrantColumns}";

    /// <summary>
    /// The revocation. The <c>revoked_at IS NULL</c> guard and the owner-scope predicate live in the
    /// same statement, so revoking twice and revoking someone else's grant are both "no row updated"
    /// and neither can rewrite history.
    /// </summary>
    private static readonly string RevokeGrantSql =
        $"UPDATE {PostgresExperienceRecordStore.GrantsTable} SET revoked_at = now(), revocation_reason = @reason " +
        $"WHERE grant_id = @grant_id AND revoked_at IS NULL AND {PostgresExperienceRecordStore.ScopePredicate} " +
        $"RETURNING {GrantColumns}";

    private static readonly string SelectGrantSql =
        $"SELECT {GrantColumns} FROM {PostgresExperienceRecordStore.GrantsTable} " +
        $"WHERE grant_id = @grant_id AND {PostgresExperienceRecordStore.ScopePredicate}";

    /// <summary><see cref="GrantColumns"/> qualified with the <c>g</c> alias, for the joined listing.</summary>
    private const string JoinedGrantColumns =
        "g.grant_id, g.experience_id, g.tenant_id, g.application_id, g.project_id, g.team_id, g.agent_id, g.user_id, " +
        "g.recipient_tenant_id, g.recipient_application_id, g.recipient_project_id, " +
        "g.recipient_team_id, g.recipient_agent_id, g.recipient_user_id, " +
        "g.reason, g.administrator_principal_id, g.issued_at, g.expires_at, g.revoked_at, g.revocation_reason";

    /// <summary>
    /// The grants over one record, driven from the record itself so that "no such record here" and
    /// "no grants over it" are different answers. A record with no grants comes back as one row with a
    /// null <c>grant_id</c>, the way the lifecycle history reports a record with no events.
    /// </summary>
    private static readonly string ListGrantsSql =
        $"SELECT {JoinedGrantColumns} " +
        $"FROM {PostgresExperienceRecordStore.Table} r " +
        $"LEFT JOIN {PostgresExperienceRecordStore.GrantsTable} g ON g.experience_id = r.experience_id " +
        $"WHERE r.experience_id = @experience_id AND {PostgresExperienceRecordStore.RecordScopePredicate} " +
        "ORDER BY g.issued_at, g.grant_id LIMIT @limit";

    /// <summary>One grant's trail, oldest first, alongside the grant as it stands now.</summary>
    private static readonly string HistorySql =
        $"SELECT {EventColumns} FROM {EventsTable} e " +
        $"WHERE e.grant_id = @grant_id ORDER BY e.recorded_at, e.event_id";

    private const string EventColumns =
        "e.event_id, e.grant_id, e.experience_id, e.action, " +
        "e.tenant_id, e.application_id, e.project_id, e.team_id, e.agent_id, e.user_id, " +
        "e.recipient_tenant_id, e.recipient_application_id, e.recipient_project_id, " +
        "e.recipient_team_id, e.recipient_agent_id, e.recipient_user_id, " +
        "e.reason, e.administrator_principal_id, e.administrator_authorized_at, e.expires_at, e.occurred_at";

    /// <summary>
    /// The audit event, assembled from the grant row itself inside the same transaction, so an event
    /// can never describe a grant that was not written. Only what the event is <em>about</em> -- the
    /// action, its reason, and the administrator who took it -- comes from the caller.
    /// </summary>
    private static readonly string InsertEventSql =
        $"INSERT INTO {EventsTable} (event_id, grant_id, experience_id, action, " +
        "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
        "recipient_team_id, recipient_agent_id, recipient_user_id, " +
        "reason, administrator_principal_id, administrator_authorized_at, expires_at, occurred_at, recorded_at) " +
        "SELECT @event_id, g.grant_id, g.experience_id, @action, " +
        "g.tenant_id, g.application_id, g.project_id, g.team_id, g.agent_id, g.user_id, " +
        "g.recipient_tenant_id, g.recipient_application_id, g.recipient_project_id, " +
        "g.recipient_team_id, g.recipient_agent_id, g.recipient_user_id, " +
        "@reason, @administrator_principal_id, @administrator_authorized_at, g.expires_at, now(), now() " +
        $"FROM {PostgresExperienceRecordStore.GrantsTable} g WHERE g.grant_id = @grant_id";

    /// <summary>The primary key a re-issued <see cref="ExperienceGrant.GrantId"/> violates.</summary>
    private const string GrantPrimaryKey = "experience_grants_pkey";

    /// <summary>The constraint an expiry that is not in the database's own future violates.</summary>
    private const string ExpiryConstraint = "experience_grants_expires_after_issue";

    /// <summary>
    /// The database's own fixed ceiling on a grant's lifetime, added by <c>0009</c>. Reaching it means
    /// the host's configured maximum did not catch the request first -- a clock far enough behind the
    /// database's, or a policy configured up against the ceiling -- so it is reported on the same
    /// field, and nothing is written either way.
    /// </summary>
    private const string LifetimeConstraint = "experience_grants_lifetime_bounded";

    /// <summary>The constraint a recipient scope crossing tenant, application, or project violates.</summary>
    private const string BoundaryConstraint = "experience_grants_same_boundary";

    /// <summary>The unique index a second <em>active</em> grant to the same recipient violates.</summary>
    private const string ActiveRecipientIndex = "ux_experience_grants_active_recipient";

    /// <summary>The constraint a grant whose recipient scope equals the owner's violates.</summary>
    private const string RecipientDiffersConstraint = "experience_grants_recipient_differs";

    private const string IssuedAction = "Issued";

    private const string RevokedAction = "Revoked";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceGrant> NoGrants = [];

    private static readonly IReadOnlyList<ExperienceGrantEvent> NoEvents = [];

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresExperienceGrantPolicy _policy;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a grant store over a host-owned data source. The store never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="policy">
    /// The bounds grants are administered under, chiefly the maximum lifetime a new grant may be
    /// issued with. Defaults to <see cref="PostgresExperienceGrantPolicy.Default"/> -- a 90-day
    /// maximum -- because there is no unbounded option: an expiry no policy bounds is what
    /// <c>DateTimeOffset.MaxValue</c> used to buy.
    /// </param>
    /// <param name="timeProvider">
    /// The clock the maximum lifetime is measured from. Defaults to <see cref="TimeProvider.System"/>.
    /// It decides only the upper bound; whether a grant is still live is always the database's clock.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceGrantStore(
        NpgsqlDataSource dataSource,
        PostgresExperienceGrantPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _policy = policy ?? PostgresExperienceGrantPolicy.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The bounds this store administers grants under.</summary>
    public PostgresExperienceGrantPolicy Policy => _policy;

    /// <inheritdoc />
    public async Task<ExperienceGrantResult> CreateAsync(
        AuthorizationContext authorization,
        GrantAdministration? administration,
        ExperienceGrantRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);

        var errors = ExperienceRecordValidator.ValidateGrantRequest(request, _policy, _timeProvider.GetUtcNow());
        if (errors.Count > 0)
        {
            return new(ExperienceGrantOutcome.Invalid, null, errors);
        }

        if (Administrator(administration) is not { } administrator)
        {
            // No administrator authority, so there is nothing to check the request against. Denied
            // before any connection opens, exactly like a scope outside the authorization.
            return new(ExperienceGrantOutcome.Denied, null, NoErrors);
        }

        if (!authorization.Permits(request.RecordScope))
        {
            return new(ExperienceGrantOutcome.Denied, null, NoErrors);
        }

        var administrationErrors = ExperienceRecordValidator.ValidateAdministration(administration!);
        if (administrationErrors.Count > 0)
        {
            return new(ExperienceGrantOutcome.Invalid, null, administrationErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Pinned, not inherited, for the same reason the lifecycle commit pins it: the expected
            // conditions here are decided by predicates that matched no row, never by a serialization
            // failure that a stricter level would raise instead.
            await using var transaction = await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            ExperienceGrant? grant;
            try
            {
                await using var insert = new NpgsqlCommand(InsertGrantSql, connection, transaction);
                var parameters = insert.Parameters;
                parameters.Add(new NpgsqlParameter<Guid>("grant_id", request.GrantId));
                parameters.Add(new NpgsqlParameter<Guid>("experience_id", request.ExperienceId));
                PostgresExperienceRecordStore.AddScopeParameters(parameters, request.RecordScope);
                AddRecipientParameters(parameters, request.RecipientScope);
                parameters.Add(new NpgsqlParameter<string>("reason", NpgsqlDbType.Text) { TypedValue = request.Reason });
                parameters.Add(new NpgsqlParameter<string>("administrator_principal_id", NpgsqlDbType.Text) { TypedValue = administrator });
                // Truncated the way every other stored timestamp is, so the grant that comes back
                // carries exactly the value a caller can compare against what it asked for.
                parameters.Add(new NpgsqlParameter<DateTimeOffset>(
                    "expires_at",
                    PostgresExperienceRecordStore.ToStoredTimestamp(request.ExpiresAt)));
                parameters.Add(new NpgsqlParameter<TimeSpan>("max_lifetime", _policy.MaxLifetime));

                grant = await ReadOneAsync(insert, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.UniqueViolation, GrantPrimaryKey, cancellationToken))
            {
                // This grant ID is already stored, in some scope. Identical whichever scope owns it, so
                // nothing about the existing grant is revealed.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(ExperienceGrantOutcome.Conflict, null, NoErrors);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.CheckViolation, ExpiryConstraint, cancellationToken))
            {
                // The expiry was not in the future of the clock that decides expiry: the database's.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    ExperienceGrantOutcome.Invalid,
                    null,
                    [new StoreValidationError("ExpiresAt", "must be later than the moment the database issues the grant.")]);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.CheckViolation, LifetimeConstraint, cancellationToken))
            {
                // Past the database's own fixed ceiling, measured from the clock that issues the grant.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    ExperienceGrantOutcome.Invalid,
                    null,
                    [new StoreValidationError("ExpiresAt", "must not be more than ten years after the moment the database issues the grant.")]);
            }
            catch (PostgresException ex) when (!cancellationToken.IsCancellationRequested
                && ex.SqlState == PostgresErrorCodes.NotNullViolation
                && string.Equals(ex.ColumnName, "expires_at", StringComparison.Ordinal))
            {
                // The statement's own maximum-lifetime guard, measured from the database's clock rather
                // than the caller's. Reaching it means the client check passed on a clock that runs
                // behind the database's; the answer is the same either way.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    ExperienceGrantOutcome.Invalid,
                    null,
                    [new StoreValidationError(
                        "ExpiresAt",
                        $"must not be more than {_policy.MaxLifetime} after the moment the database issues the grant, which is the configured maximum grant lifetime.")]);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.UniqueViolation, ActiveRecipientIndex, cancellationToken))
            {
                // An active grant over this record already permits this recipient. Stacking a second
                // one would mean revoking the known grant did not end access, so it is refused.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(ExperienceGrantOutcome.Conflict, null, NoErrors);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.CheckViolation, RecipientDiffersConstraint, cancellationToken))
            {
                // Unreachable through this store -- validation rejects it first -- and enforced anyway.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    ExperienceGrantOutcome.Invalid,
                    null,
                    [new StoreValidationError("RecipientScope", "must differ from the record's own scope, which already permits the read.")]);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.CheckViolation, BoundaryConstraint, cancellationToken))
            {
                // Unreachable through this store -- validation rejects it first -- and enforced anyway,
                // because the boundary is the database's rule rather than this class's.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    ExperienceGrantOutcome.Invalid,
                    null,
                    [new StoreValidationError("RecipientScope", "must keep the record's tenant, application, and project.")]);
            }

            if (grant is null)
            {
                // No such record in this owner scope: indistinguishable from one that exists elsewhere.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(ExperienceGrantOutcome.NotFound, null, NoErrors);
            }

            await AppendEventAsync(connection, transaction, grant.GrantId, IssuedAction, grant.Reason, administration!, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceGrantOutcome.Created, grant, NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant create", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceGrantResult> RevokeAsync(
        AuthorizationContext authorization,
        GrantAdministration? administration,
        ExperienceGrantRevocation revocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(revocation);

        var errors = ExperienceRecordValidator.ValidateGrantRevocation(revocation);
        if (errors.Count > 0)
        {
            return new(ExperienceGrantOutcome.Invalid, null, errors);
        }

        if (Administrator(administration) is null)
        {
            return new(ExperienceGrantOutcome.Denied, null, NoErrors);
        }

        if (!authorization.Permits(revocation.RecordScope))
        {
            return new(ExperienceGrantOutcome.Denied, null, NoErrors);
        }

        var administrationErrors = ExperienceRecordValidator.ValidateAdministration(administration!);
        if (administrationErrors.Count > 0)
        {
            return new(ExperienceGrantOutcome.Invalid, null, administrationErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            ExperienceGrant? revoked;
            try
            {
                await using var update = new NpgsqlCommand(RevokeGrantSql, connection, transaction);
                var parameters = update.Parameters;
                parameters.Add(new NpgsqlParameter<Guid>("grant_id", revocation.GrantId));
                parameters.Add(new NpgsqlParameter<string>("reason", NpgsqlDbType.Text) { TypedValue = revocation.Reason });
                PostgresExperienceRecordStore.AddScopeParameters(parameters, revocation.RecordScope);

                revoked = await ReadOneAsync(update, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, PostgresErrorCodes.CheckViolation, LifetimeConstraint, cancellationToken))
            {
                // Defensive. 0009 exempts a revoked row from the lifetime ceiling precisely so this
                // cannot happen -- PostgreSQL re-checks a CHECK on every UPDATE, and without that
                // exemption a grant stored before 0009 with an unbounded expiry could never be revoked,
                // which is the one remedy the migration's runbook prescribes for it. If a deployment
                // ever reinstates the constraint without the exemption, the answer is a typed refusal
                // naming the problem rather than an infrastructure failure thrown at an administrator
                // trying to end access.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(
                    ExperienceGrantOutcome.Invalid,
                    null,
                    [new StoreValidationError(
                        "GrantId",
                        "names a stored grant whose expiry is past the database's lifetime ceiling, and the ceiling refuses the update that would revoke it. See 0009_grant_access_log.sql.")]);
            }

            if (revoked is null)
            {
                // Either the grant is not in this owner scope, or it was already revoked. The re-read
                // runs inside the transaction that is about to be rolled back, so nothing is written
                // either way.
                var existing = await ReadStoredGrantAsync(connection, transaction, revocation, cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                return existing is { } stored
                    ? new(ExperienceGrantOutcome.AlreadyRevoked, stored, NoErrors)
                    : new(ExperienceGrantOutcome.NotFound, null, NoErrors);
            }

            await AppendEventAsync(connection, transaction, revoked.GrantId, RevokedAction, revocation.Reason, administration!, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceGrantOutcome.Revoked, revoked, NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant revoke", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceGrantListResult> ListAsync(
        AuthorizationContext authorization,
        Scope recordScope,
        Guid experienceId,
        CancellationToken cancellationToken,
        int limit = ExperienceGrant.DefaultListLimit)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(recordScope);

        var errors = ExperienceRecordValidator.ValidateGrantList(recordScope, experienceId, limit);
        if (errors.Count > 0)
        {
            return new(ExperienceGrantOutcome.Invalid, NoGrants, errors);
        }

        if (!authorization.Permits(recordScope))
        {
            return new(ExperienceGrantOutcome.Denied, NoGrants, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(ListGrantsSql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, recordScope);
            command.Parameters.Add(new NpgsqlParameter<int>("limit", limit));

            var grants = new List<ExperienceGrant>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // No record in this owner scope: a different answer from a record nobody has shared.
                return new(ExperienceGrantOutcome.NotFound, NoGrants, NoErrors);
            }

            if (!reader.IsDBNull(0))
            {
                // A null grant_id is the outer join's single "record with no grants" row.
                do
                {
                    grants.Add(ReadGrant(reader));
                }
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            return new(ExperienceGrantOutcome.Found, grants, NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant list", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceGrantHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        Scope recordScope,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(recordScope);

        var errors = ExperienceRecordValidator.ValidateGrantHistory(recordScope, grantId);
        if (errors.Count > 0)
        {
            return new(ExperienceGrantOutcome.Invalid, null, NoEvents, errors);
        }

        if (!authorization.Permits(recordScope))
        {
            return new(ExperienceGrantOutcome.Denied, null, NoEvents, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // One connection, so the grant and its events come from one snapshot. The grant is read
            // first and in the owner scope, so a grant that is not this scope's reveals no events.
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            ExperienceGrant? grant;
            await using (var command = new NpgsqlCommand(SelectGrantSql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
                PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, recordScope);
                grant = await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (grant is null)
            {
                return new(ExperienceGrantOutcome.NotFound, null, NoEvents, NoErrors);
            }

            var events = new List<ExperienceGrantEvent>();
            await using (var command = new NpgsqlCommand(HistorySql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    events.Add(ReadEvent(reader));
                }
            }

            return new(ExperienceGrantOutcome.Found, grant, events, NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant history", cancellationToken);
        }
    }

    /// <summary>
    /// The administrator's principal ID, or <see langword="null"/> when the host supplied no usable
    /// administrator authority. Nothing else is consulted: not the authorization context, not its
    /// roles, not the requesting scope.
    /// </summary>
    private static string? Administrator(GrantAdministration? administration) =>
        administration is { AdministratorPrincipalId: { } principal } && !string.IsNullOrWhiteSpace(principal)
            ? principal
            : null;

    /// <summary>
    /// Appends the audit event for an action, inside the action's own transaction. The insert's source
    /// is the grant row, so a missing source row would write no event and leave the action committed
    /// without a trail: the rowcount is therefore asserted, and anything but exactly one row fails the
    /// whole transaction rather than silently producing an unaudited grant.
    /// </summary>
    private static async Task AppendEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid grantId,
        string action,
        string reason,
        GrantAdministration administration,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(InsertEventSql, connection, transaction);
        var parameters = insert.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("event_id", Guid.NewGuid()));
        parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
        parameters.Add(new NpgsqlParameter<string>("action", NpgsqlDbType.Text) { TypedValue = action });
        parameters.Add(new NpgsqlParameter<string>("reason", NpgsqlDbType.Text) { TypedValue = reason });
        parameters.Add(new NpgsqlParameter<string>("administrator_principal_id", NpgsqlDbType.Text) { TypedValue = administration.AdministratorPrincipalId });
        parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "administrator_authorized_at",
            PostgresExperienceRecordStore.ToStoredTimestamp(administration.AuthorizedAt)));

        var written = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (written != 1)
        {
            throw new ExperienceStoreException("A sharing grant was written without its audit event, so the change was rolled back.");
        }
    }

    private static async Task<ExperienceGrant?> ReadStoredGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExperienceGrantRevocation revocation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectGrantSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", revocation.GrantId));
        PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, revocation.RecordScope);

        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExperienceGrant?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGrant(reader) : null;
    }

    private static void AddRecipientParameters(NpgsqlParameterCollection parameters, Scope recipient)
    {
        parameters.Add(new NpgsqlParameter<string>("recipient_tenant_id", NpgsqlDbType.Text) { TypedValue = recipient.TenantId });
        parameters.Add(new NpgsqlParameter<string>("recipient_application_id", NpgsqlDbType.Text) { TypedValue = recipient.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("recipient_project_id", NpgsqlDbType.Text) { TypedValue = recipient.ProjectId });
        parameters.Add(NullableText("recipient_team_id", recipient.TeamId));
        parameters.Add(NullableText("recipient_agent_id", recipient.AgentId));
        parameters.Add(NullableText("recipient_user_id", recipient.UserId));
    }

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    /// <summary>
    /// Matches a violation of one named constraint, so a re-issued grant ID, an expiry in the past,
    /// and a boundary-crossing recipient stay distinguishable from each other and from a constraint
    /// added later.
    /// </summary>
    private static bool IsViolationOf(PostgresException ex, string sqlState, string constraintName, CancellationToken cancellationToken) =>
        ex.SqlState == sqlState
        && string.Equals(ex.ConstraintName, constraintName, StringComparison.Ordinal)
        && !cancellationToken.IsCancellationRequested;

    private static ExperienceGrantEvent ReadEvent(DbDataReader reader)
    {
        try
        {
            return new ExperienceGrantEvent(
                EventId: reader.GetGuid(0),
                GrantId: reader.GetGuid(1),
                ExperienceId: reader.GetGuid(2),
                Action: DecodeAction(reader.GetString(3)),
                RecordScope: new Scope(
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)),
                RecipientScope: new Scope(
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15)),
                Reason: reader.GetString(16),
                AdministratorPrincipalId: reader.GetString(17),
                AdministratorAuthorizedAt: reader.GetFieldValue<DateTimeOffset>(18),
                ExpiresAt: reader.GetFieldValue<DateTimeOffset>(19),
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(20));
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored sharing-grant event could not be decoded.", ex);
        }
    }

    private static ExperienceGrantAction DecodeAction(string action) =>
        Enum.TryParse<ExperienceGrantAction>(action, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ExperienceStoreException("Stored sharing-grant event has an unrecognized action.");

    private static ExperienceGrant ReadGrant(DbDataReader reader)
    {
        try
        {
            return DecodeGrant(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt row (e.g. InvalidCastException on a retyped column).
            throw new ExperienceStoreException("Stored sharing grant could not be decoded.", ex);
        }
    }

    private static ExperienceGrant DecodeGrant(DbDataReader reader) => new(
        GrantId: reader.GetGuid(0),
        ExperienceId: reader.GetGuid(1),
        RecordScope: new Scope(
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7)),
        RecipientScope: new Scope(
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13)),
        Reason: reader.GetString(14),
        AdministratorPrincipalId: reader.GetString(15),
        IssuedAt: reader.GetFieldValue<DateTimeOffset>(16),
        ExpiresAt: reader.GetFieldValue<DateTimeOffset>(17),
        RevokedAt: reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
        RevocationReason: reader.IsDBNull(19) ? null : reader.GetString(19));
}
