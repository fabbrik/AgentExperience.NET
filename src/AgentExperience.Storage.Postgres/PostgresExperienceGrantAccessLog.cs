using System.Data.Common;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceGrantAccessLog"/> over PostgreSQL with plain Npgsql: one appended row per
/// record a sharing grant delivered, and an owner-scoped reader for them. The schema must already
/// exist -- <c>0009_grant_access_log.sql</c> creates <c>experience_grant_access</c>, and the host
/// applies it by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement per read, on its own connection, afterwards.</b> The append never joins the read's
/// transaction and never runs inside the read's statement: a read that wrote its own audit row would
/// take a write lock on every read and could never run on a replica. A search hands over many records
/// at once, so its rows arrive as one batch and are written by a single <c>unnest</c> insert -- one
/// round trip per search, not one per row. What a failed append means for the read is the host's
/// <see cref="ExperienceGrantAuditing.Mode"/>, decided by the reader, not here.
/// </para>
/// <para>
/// <b>It authorizes nothing on the write side and checks nothing.</b> Everything on a row was decided
/// by the read that already happened -- which grant the predicate used, whose record it was, who read
/// it, at which revision. This class only makes that durable, and it reports a failure by throwing,
/// exactly as the port requires: a log that swallowed a failed write would be worse than no log,
/// because it would look like a trail.
/// </para>
/// <para>
/// <b>Reading the trail is an owner-scope operation</b> and does check authorization, exactly as
/// listing a record's grants does. A grant confers reading one record and never the right to see who
/// else has read it.
/// </para>
/// </remarks>
public sealed class PostgresExperienceGrantAccessLog : IExperienceGrantAccessLog
{
    /// <summary>The append-only access ledger. Created by <c>0009_grant_access_log.sql</c>.</summary>
    internal const string Table = "agent_experience.experience_grant_access";

    /// <summary>
    /// The row's columns in the order <see cref="Decode"/> expects (ordinals 0-19), <c>recorded_at</c>
    /// excluded because it is the database's own and nothing reads it back. <c>disclosure</c> was added
    /// by <c>0011</c> and is null on every row written before it.
    /// </summary>
    private const string Columns =
        "access_id, grant_id, experience_id, record_revision, " +
        "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
        "recipient_team_id, recipient_agent_id, recipient_user_id, " +
        "principal_id, correlation_id, occurred_at, disclosure";

    /// <summary>
    /// The batch insert. Every column arrives as an array of the same length and <c>unnest</c> turns
    /// them back into rows, so a search's whole batch is one statement and one round trip however many
    /// records it delivered. <c>recorded_at</c> is <c>clock_timestamp()</c> rather than <c>now()</c>:
    /// this column is meant to be the instant the row landed, and <c>now()</c> is fixed at the start of
    /// the surrounding transaction.
    /// </summary>
    private const string InsertSql =
        $"INSERT INTO {Table} ({Columns}, recorded_at) " +
        "SELECT a.access_id, a.grant_id, a.experience_id, a.record_revision, " +
        "a.tenant_id, a.application_id, a.project_id, a.team_id, a.agent_id, a.user_id, " +
        "a.recipient_tenant_id, a.recipient_application_id, a.recipient_project_id, " +
        "a.recipient_team_id, a.recipient_agent_id, a.recipient_user_id, " +
        "a.principal_id, a.correlation_id, a.occurred_at, a.disclosure, clock_timestamp() " +
        "FROM unnest(@access_id, @grant_id, @experience_id, @record_revision, " +
        "@tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, " +
        "@recipient_tenant_id, @recipient_application_id, @recipient_project_id, " +
        "@recipient_team_id, @recipient_agent_id, @recipient_user_id, " +
        "@principal_id, @correlation_id, @occurred_at, @disclosure) " +
        $"AS a({Columns})";

    /// <summary>
    /// The owner-scoped page. The keyset cursor is <c>(occurred_at, access_id)</c> compared as a row,
    /// so several deliveries sharing an instant page correctly instead of repeating or vanishing.
    /// </summary>
    private const string QuerySql =
        $"SELECT {Columns} FROM {Table} " +
        $"WHERE {PostgresExperienceRecordStore.ScopePredicate} " +
        "AND (@experience_id IS NULL OR experience_id = @experience_id) " +
        "AND (@cursor_occurred_at IS NULL " +
        "OR (occurred_at, access_id) > (@cursor_occurred_at, @cursor_access_id)) " +
        "ORDER BY occurred_at, access_id LIMIT @limit";

    /// <summary>
    /// The access-row retention path, created by <c>0012</c>. Bounded, scoped, floored, and through its
    /// own marker (transaction-local, and reset when the function returns), so the append-only guard over
    /// this table is never switched off.
    /// </summary>
    private const string PurgeSql =
        "SELECT purge_outcome, purged, more_remain FROM agent_experience.purge_grant_access(" +
        "@tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, @subtree, @cutoff, @limit)";

    /// <summary>The purge function's outcome when it ran.</summary>
    private const string PurgedOutcome = "Purged";

    /// <summary>The purge function's outcome for a cutoff inside the minimum retention, or a null one.</summary>
    private const string CutoffTooRecentOutcome = "CutoffTooRecent";

    /// <summary>
    /// The fewest whole days an access row is kept before <see cref="PurgeOlderThanAsync"/> may remove
    /// it, measured on the database's clock against the row's <c>recorded_at</c>. Fixed in <c>0012</c>
    /// and enforced there twice -- by the purge function and by the append-only guard -- so it is a
    /// floor no host configuration can lower. The host's own retention is the cutoff it passes.
    /// </summary>
    public const int MinimumRetentionDays = 30;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceGrantAccess> NoAccesses = [];

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates an access log over a host-owned data source. The log never disposes it.</summary>
    /// <param name="dataSource">
    /// The Npgsql data source to open connections from. It may deliberately be a <em>different</em> one
    /// from the store's: every audited read costs one pooled connection and one synchronous round trip
    /// here, so pointing the ledger at its own pool keeps that cost off the read pool.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceGrantAccessLog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accesses);
        if (accesses.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var count = accesses.Count;
        var accessIds = new Guid[count];
        var grantIds = new Guid[count];
        var experienceIds = new Guid[count];
        var revisions = new long[count];
        var tenantIds = new string[count];
        var applicationIds = new string[count];
        var projectIds = new string[count];
        var teamIds = new string?[count];
        var agentIds = new string?[count];
        var userIds = new string?[count];
        var recipientTenantIds = new string[count];
        var recipientApplicationIds = new string[count];
        var recipientProjectIds = new string[count];
        var recipientTeamIds = new string?[count];
        var recipientAgentIds = new string?[count];
        var recipientUserIds = new string?[count];
        var principalIds = new string[count];
        var correlationIds = new string?[count];
        var occurredAt = new DateTimeOffset[count];
        var disclosures = new string?[count];

        for (var i = 0; i < count; i++)
        {
            var access = accesses[i];
            ArgumentNullException.ThrowIfNull(access, nameof(accesses));
            ArgumentNullException.ThrowIfNull(access.RecordScope, $"{nameof(accesses)}[{i}].{nameof(access.RecordScope)}");
            ArgumentNullException.ThrowIfNull(access.RecipientScope, $"{nameof(accesses)}[{i}].{nameof(access.RecipientScope)}");

            accessIds[i] = access.AccessId;
            grantIds[i] = access.GrantId;
            experienceIds[i] = access.ExperienceId;
            revisions[i] = access.RecordRevision;
            tenantIds[i] = access.RecordScope.TenantId;
            applicationIds[i] = access.RecordScope.ApplicationId;
            projectIds[i] = access.RecordScope.ProjectId;
            teamIds[i] = access.RecordScope.TeamId;
            agentIds[i] = access.RecordScope.AgentId;
            userIds[i] = access.RecordScope.UserId;
            recipientTenantIds[i] = access.RecipientScope.TenantId;
            recipientApplicationIds[i] = access.RecipientScope.ApplicationId;
            recipientProjectIds[i] = access.RecipientScope.ProjectId;
            recipientTeamIds[i] = access.RecipientScope.TeamId;
            recipientAgentIds[i] = access.RecipientScope.AgentId;
            recipientUserIds[i] = access.RecipientScope.UserId;
            principalIds[i] = access.PrincipalId;
            correlationIds[i] = access.CorrelationId;
            occurredAt[i] = PostgresExperienceRecordStore.ToStoredTimestamp(access.OccurredAt);

            // Only a defined level is written by name. Null, or a value the enum does not define, goes
            // in as null, and 0011's experience_grant_access_disclosure_recorded refuses the row: a
            // delivery that cannot say what it disclosed is an audit failure, decided by the mode.
            disclosures[i] = access.Disclosure is { } level && Enum.IsDefined(level) ? level.ToString() : null;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(InsertSql);
            var parameters = command.Parameters;
            parameters.Add(Array("access_id", NpgsqlDbType.Uuid, accessIds));
            parameters.Add(Array("grant_id", NpgsqlDbType.Uuid, grantIds));
            parameters.Add(Array("experience_id", NpgsqlDbType.Uuid, experienceIds));
            parameters.Add(Array("record_revision", NpgsqlDbType.Bigint, revisions));
            parameters.Add(Array("tenant_id", NpgsqlDbType.Text, tenantIds));
            parameters.Add(Array("application_id", NpgsqlDbType.Text, applicationIds));
            parameters.Add(Array("project_id", NpgsqlDbType.Text, projectIds));
            parameters.Add(Array("team_id", NpgsqlDbType.Text, teamIds));
            parameters.Add(Array("agent_id", NpgsqlDbType.Text, agentIds));
            parameters.Add(Array("user_id", NpgsqlDbType.Text, userIds));
            parameters.Add(Array("recipient_tenant_id", NpgsqlDbType.Text, recipientTenantIds));
            parameters.Add(Array("recipient_application_id", NpgsqlDbType.Text, recipientApplicationIds));
            parameters.Add(Array("recipient_project_id", NpgsqlDbType.Text, recipientProjectIds));
            parameters.Add(Array("recipient_team_id", NpgsqlDbType.Text, recipientTeamIds));
            parameters.Add(Array("recipient_agent_id", NpgsqlDbType.Text, recipientAgentIds));
            parameters.Add(Array("recipient_user_id", NpgsqlDbType.Text, recipientUserIds));
            parameters.Add(Array("principal_id", NpgsqlDbType.Text, principalIds));
            parameters.Add(Array("correlation_id", NpgsqlDbType.Text, correlationIds));
            parameters.Add(Array("occurred_at", NpgsqlDbType.TimestampTz, occurredAt));
            parameters.Add(Array("disclosure", NpgsqlDbType.Text, disclosures));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant access audit", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceGrantAccessQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceGrantAccessQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateGrantAccessQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, NoAccesses, errors);
        }

        if (!authorization.Permits(query.RecordScope))
        {
            return new(ExperienceStoreOutcome.Denied, NoAccesses, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(QuerySql);
            var parameters = command.Parameters;
            PostgresExperienceRecordStore.AddScopeParameters(parameters, query.RecordScope);
            parameters.Add(new NpgsqlParameter("experience_id", NpgsqlDbType.Uuid)
            {
                Value = query.ExperienceId is { } id ? id : DBNull.Value,
            });
            parameters.Add(new NpgsqlParameter("cursor_occurred_at", NpgsqlDbType.TimestampTz)
            {
                Value = query.StartAfter is { } cursor ? PostgresExperienceRecordStore.ToStoredTimestamp(cursor.OccurredAt) : DBNull.Value,
            });
            parameters.Add(new NpgsqlParameter("cursor_access_id", NpgsqlDbType.Uuid)
            {
                Value = query.StartAfter is { } after ? after.AccessId : DBNull.Value,
            });
            parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            var rows = new List<ExperienceGrantAccess>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(Read(reader));
            }

            return new(
                ExperienceStoreOutcome.Found,
                rows,
                NoErrors,
                rows.Count > 0 ? new ExperienceGrantAccessCursor(rows[^1].OccurredAt, rows[^1].AccessId) : null);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant access query", cancellationToken);
        }
    }

    /// <summary>
    /// Removes the access rows in one owner scope -- or, with <see cref="ScopeMatch.Subtree"/>, in that
    /// scope and every scope beneath it -- that the database recorded before <paramref name="cutoff"/>,
    /// in one bounded batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the ledger's only retention path, and it is not erasure.</b> Deleting a record keeps
    /// the access rows that name it, on purpose: they answer "who read this before it was deleted". This
    /// collects them by age instead, when the host decides the answer need not be kept any longer.
    /// There is no default retention and no timer; nothing is collected unless a host calls this.
    /// </para>
    /// <para>
    /// <b>A row younger than <see cref="MinimumRetentionDays"/> days is never removed.</b> The purge
    /// removes the answer to "who read our experience", so it must not be usable to erase a read the
    /// moment after it happened. A <paramref name="cutoff"/> later than that floor, by the
    /// <em>database's</em> clock, is refused -- <see cref="ExperienceStoreOutcome.Invalid"/> on
    /// <c>Cutoff</c>, with nothing removed -- rather than silently clamped, so a purge never reports
    /// success while rows the host asked about survive. The database's append-only guard re-checks the
    /// floor on every row.
    /// </para>
    /// <para>
    /// <b>Age is the row's <c>recorded_at</c></b>, the database's own clock when the row landed, never
    /// <see cref="ExperienceGrantAccess.OccurredAt"/>, which is the reader's clock and could be skewed
    /// into the purge window.
    /// </para>
    /// <para>
    /// <b>Authorized like the expired-grant purge.</b> Administrator authority is required -- this
    /// removes an audit trail -- and <paramref name="authorization"/> must permit
    /// <paramref name="recordScope"/>, both before a connection opens. Authorizing the root authorizes
    /// its subtree: see <see cref="ScopeMatch"/>. The rows matched are those whose <em>owner</em> scope
    /// is at or beneath the root; the recipient scope plays no part.
    /// </para>
    /// <para>
    /// Bounded: at most <paramref name="batchSize"/> rows, oldest <c>recorded_at</c> first, in one
    /// transaction inside <c>0012</c>'s <c>agent_experience.purge_grant_access</c>.
    /// <see cref="ExperienceGrantAccessPurgeResult.MoreRemain"/> is asked after the delete, in the same
    /// transaction, with the same predicate.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="administration">The host-constructed administrator authority. Required, exactly as for the expired-grant purge.</param>
    /// <param name="recordScope">The owner scope to purge within, or the root of the subtree to purge. Never treated as authority.</param>
    /// <param name="cutoff">Rows the database recorded strictly before this instant are removed. Must be at least <see cref="MinimumRetentionDays"/> days before the database's clock.</param>
    /// <param name="match">Whether to purge <paramref name="recordScope"/> alone or everything beneath it too.</param>
    /// <param name="batchSize">The most rows this call may remove, from <see cref="PostgresExperienceRecordStore.MinSweepBatchSize"/> to <see cref="PostgresExperienceRecordStore.MaxSweepBatchSize"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Deleted"/> when the purge ran (possibly removing nothing),
    /// <see cref="ExperienceStoreOutcome.Denied"/>, or <see cref="ExperienceStoreOutcome.Invalid"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="recordScope"/> is <see langword="null"/>.</exception>
    public async Task<ExperienceGrantAccessPurgeResult> PurgeOlderThanAsync(
        AuthorizationContext authorization,
        GrantAdministration? administration,
        Scope recordScope,
        DateTimeOffset cutoff,
        ScopeMatch match,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // A count and how wide it reached, nothing else: the access IDs, principals, grants and scopes
        // this removes are exactly what the purge exists to stop keeping.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.GrantAccessPurge);
        ErasureDiagnostics.TagScopeMatch(operation, match);

        ExperienceGrantAccessPurgeResult result;
        try
        {
            result = await PurgeOlderThanCoreAsync(authorization, administration, recordScope, cutoff, match, batchSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.ErasedCountAttribute, result.PurgedCount);
        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    private async Task<ExperienceGrantAccessPurgeResult> PurgeOlderThanCoreAsync(
        AuthorizationContext authorization,
        GrantAdministration? administration,
        Scope recordScope,
        DateTimeOffset cutoff,
        ScopeMatch match,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(recordScope);

        var errors = ExperienceRecordValidator.ValidateGrantAccessPurge(recordScope, cutoff, match, batchSize);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, false, errors);
        }

        if (administration is not { AdministratorPrincipalId: { } administrator } || string.IsNullOrWhiteSpace(administrator))
        {
            return new(ExperienceStoreOutcome.Denied, 0, false, NoErrors);
        }

        if (!authorization.Permits(recordScope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, false, NoErrors);
        }

        var administrationErrors = ExperienceRecordValidator.ValidateAdministration(administration);
        if (administrationErrors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, false, administrationErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(PurgeSql);
            var parameters = command.Parameters;
            PostgresExperienceRecordStore.AddScopeParameters(parameters, recordScope);
            parameters.Add(new NpgsqlParameter<bool>("subtree", match == ScopeMatch.Subtree));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", PostgresExperienceRecordStore.ToStoredTimestamp(cutoff)));
            parameters.Add(new NpgsqlParameter<int>("limit", batchSize));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ExperienceStoreException("The access-log purge function returned no row.");
            }

            var outcome = reader.GetString(0);
            var purged = reader.GetInt64(1);
            var moreRemain = reader.GetBoolean(2);

            return outcome switch
            {
                PurgedOutcome => new(ExperienceStoreOutcome.Deleted, (int)purged, moreRemain, NoErrors),
                CutoffTooRecentOutcome => new(
                    ExperienceStoreOutcome.Invalid,
                    0,
                    false,
                    [new StoreValidationError(
                        "Cutoff",
                        $"must be at least {MinimumRetentionDays} days before the database's clock; an access row is kept at least that long.")]),
                _ => throw new ExperienceStoreException("The access-log purge function reported an unrecognized outcome."),
            };
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "grant access purge", cancellationToken);
        }
    }

    private static NpgsqlParameter Array(string name, NpgsqlDbType elementType, object value) =>
        new(name, NpgsqlDbType.Array | elementType) { Value = value };

    private static ExperienceGrantAccess Read(DbDataReader reader)
    {
        try
        {
            return Decode(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored grant access row could not be decoded.", ex);
        }
    }

    private static ExperienceGrantAccess Decode(DbDataReader reader) => new(
        AccessId: reader.GetGuid(0),
        GrantId: reader.GetGuid(1),
        ExperienceId: reader.GetGuid(2),
        RecordRevision: reader.GetInt64(3),
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
        PrincipalId: reader.GetString(16),
        CorrelationId: reader.IsDBNull(17) ? null : reader.GetString(17),
        OccurredAt: reader.GetFieldValue<DateTimeOffset>(18),
        Disclosure: reader.IsDBNull(19) ? null : PostgresExperienceRecordStore.ParseDisclosure(reader.GetString(19)));
}
