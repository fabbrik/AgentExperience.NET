using System.Data.Common;
using AgentExperience.Abstractions;
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
