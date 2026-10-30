using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// An <see cref="IExperienceRunStore"/> over <c>0032</c>'s <c>agent_experience.experience_runs</c>: each completed run the
/// capture service completes, as capture held it (already sanitized), so a run captured in one process can be finalized in
/// a later one. Register it with <c>AddAgentExperiencePostgresRunStore</c>; nothing writes the table otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Every operation runs in one transaction with the authorization's bounds declared, as every store operation does, so
/// under row-level security the table's policies bind it too. Run IDs are unique across every scope; a save under an ID
/// another scope holds is <see cref="ExperienceRunStoreOutcome.Conflict"/>, revealing nothing.
/// </para>
/// <para>
/// <b>Plaintext.</b> A stored run is not sealed, so this store is not available in crypto-shredding mode: its registration
/// refuses beside <c>AddAgentExperiencePostgresEncryption</c>. Erasing a record deletes the stored run it was finalized
/// from (when no other live record came from that run), through <c>0032</c>'s trigger.
/// </para>
/// </remarks>
public sealed class PostgresExperienceRunStore : IExperienceRunStore
{
    /// <summary>The table <c>0032</c> creates.</summary>
    internal const string RunsTable = "agent_experience.experience_runs";

    private const string Columns =
        "run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, completion_event_id, saved_at, " +
        "finalized_at, payload_version, payload::text";

    /// <summary>The save: a new row, or nothing when the run ID is taken in any scope.</summary>
    internal const string SaveSql =
        $"INSERT INTO {RunsTable} (run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "completion_event_id, execution_status, ended_at, saved_at, payload_version, payload)\n" +
        "VALUES (@run_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, @completion_event_id, " +
        "@execution_status, @ended_at, pg_catalog.clock_timestamp(), @payload_version, @payload)\n" +
        "ON CONFLICT (run_id) DO NOTHING";

    /// <summary>After a save that wrote nothing: whether the stored run is this one, in this scope, under this event.</summary>
    internal const string SameRunSql =
        $"SELECT 1 FROM {RunsTable} WHERE run_id = @run_id AND completion_event_id = @completion_event_id AND {ScopePredicate}";

    /// <summary>A run by ID, in the authorization's tenant and within each of its bounds.</summary>
    internal const string GetSql =
        $"SELECT {Columns} FROM {RunsTable}\n" +
        "WHERE run_id = @run_id AND tenant_id = @auth_tenant\n" +
        "AND (@auth_application IS NULL OR application_id = @auth_application)\n" +
        "AND (@auth_project IS NULL OR project_id = @auth_project)\n" +
        "AND (@auth_team IS NULL OR team_id = @auth_team)\n" +
        "AND (@auth_agent IS NULL OR agent_id = @auth_agent)\n" +
        "AND (@auth_user IS NULL OR user_id = @auth_user)";

    /// <summary>A page of a scope's unfinalized runs, in run order.</summary>
    internal const string ListSql =
        $"SELECT {Columns} FROM {RunsTable}\n" +
        $"WHERE {ScopePredicate} AND finalized_at IS NULL\n" +
        "AND (@ended_before IS NULL OR ended_at < @ended_before)\n" +
        "AND (@after_run_id IS NULL OR run_id > @after_run_id)\n" +
        "ORDER BY run_id LIMIT @limit";

    /// <summary>Marks a run finalized, keeping the first instant.</summary>
    internal const string MarkFinalizedSql =
        $"UPDATE {RunsTable} SET finalized_at = COALESCE(finalized_at, @finalized_at) WHERE run_id = @run_id AND {ScopePredicate}";

    /// <summary>Deletes a run in exactly the scope.</summary>
    internal const string DeleteSql = $"DELETE FROM {RunsTable} WHERE run_id = @run_id AND {ScopePredicate}";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a run store over <paramref name="dataSource"/>, connected as the application role.</summary>
    /// <param name="dataSource">The application role's data source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceRunStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<ExperienceRunStoreResult> SaveAsync(
        AuthorizationContext authorization,
        ExperienceRun run,
        Guid completionEventId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateRunSave(run, completionEventId);
        if (errors.Count > 0)
        {
            return new(ExperienceRunStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(run.Scope))
        {
            return new(ExperienceRunStoreOutcome.Denied, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var payload = ExperienceRunPayload.Serialize(run);
        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            int inserted;
            await using (var command = session.CreateCommand(SaveSql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("run_id", run.RunId));
                command.Parameters.Add(new NpgsqlParameter<Guid>("completion_event_id", completionEventId));
                command.Parameters.Add(new NpgsqlParameter<string>("execution_status", run.ExecutionStatus!.Value.ToString()));
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("ended_at", ToStoredTimestamp(run.EndedAt!.Value)));
                command.Parameters.Add(new NpgsqlParameter<int>("payload_version", ExperienceRunPayload.CurrentVersion));
                command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = payload });
                AddScopeParameters(command.Parameters, run.Scope);
                inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var outcome = ExperienceRunStoreOutcome.Saved;
            if (inserted == 0)
            {
                await using var command = session.CreateCommand(SameRunSql);
                command.Parameters.Add(new NpgsqlParameter<Guid>("run_id", run.RunId));
                command.Parameters.Add(new NpgsqlParameter<Guid>("completion_event_id", completionEventId));
                AddScopeParameters(command.Parameters, run.Scope);
                outcome = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null
                    ? ExperienceRunStoreOutcome.Conflict
                    : ExperienceRunStoreOutcome.AlreadySaved;
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(outcome, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "save run", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRunGetResult> GetAsync(AuthorizationContext authorization, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateRunId(runId);
        if (errors.Count > 0)
        {
            return new(ExperienceRunStoreOutcome.Invalid, null, errors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            StoredExperienceRun? stored = null;
            await using (var command = session.CreateCommand(GetSql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("run_id", runId));
                command.Parameters.Add(new NpgsqlParameter<string>("auth_tenant", NpgsqlDbType.Text) { TypedValue = authorization.TenantId ?? string.Empty });
                command.Parameters.Add(NullableText("auth_application", authorization.ApplicationId));
                command.Parameters.Add(NullableText("auth_project", authorization.ProjectId));
                command.Parameters.Add(NullableText("auth_team", authorization.TeamId));
                command.Parameters.Add(NullableText("auth_agent", authorization.AgentId));
                command.Parameters.Add(NullableText("auth_user", authorization.UserId));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    stored = Read(reader);
                }
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);

            // The statement applied the bounds; the authorization's own rule is applied again, in depth.
            return stored is not null && authorization.Permits(stored.Run.Scope)
                ? new(ExperienceRunStoreOutcome.Found, stored, NoErrors)
                : new(ExperienceRunStoreOutcome.NotFound, null, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "get run", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRunListResult> ListUnfinalizedAsync(AuthorizationContext authorization, ExperienceRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateRunQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceRunStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceRunStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            var runs = new List<StoredExperienceRun>();
            await using (var command = session.CreateCommand(ListSql))
            {
                AddScopeParameters(command.Parameters, query.Scope);
                command.Parameters.Add(new NpgsqlParameter("ended_before", NpgsqlDbType.TimestampTz)
                {
                    Value = query.EndedBefore is { } before ? ToStoredTimestamp(before) : DBNull.Value,
                });
                command.Parameters.Add(NullableUuid("after_run_id", query.AfterRunId));
                command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    runs.Add(Read(reader));
                }
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceRunStoreOutcome.Found, runs, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "list runs", cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRunStoreResult> MarkFinalizedAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        DateTimeOffset finalizedAt,
        CancellationToken cancellationToken) =>
        WriteInScopeAsync(
            authorization,
            scope,
            runId,
            ExperienceRecordValidator.ValidateRunInScope(scope, runId, finalizedAt, requireInstant: true),
            MarkFinalizedSql,
            parameters => parameters.Add(new NpgsqlParameter<DateTimeOffset>("finalized_at", ToStoredTimestamp(finalizedAt))),
            ExperienceRunStoreOutcome.Updated,
            "mark run finalized",
            cancellationToken);

    /// <inheritdoc />
    public Task<ExperienceRunStoreResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        CancellationToken cancellationToken) =>
        WriteInScopeAsync(
            authorization,
            scope,
            runId,
            ExperienceRecordValidator.ValidateRunInScope(scope, runId),
            DeleteSql,
            static _ => { },
            ExperienceRunStoreOutcome.Deleted,
            "delete run",
            cancellationToken);

    private async Task<ExperienceRunStoreResult> WriteInScopeAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        IReadOnlyList<StoreValidationError> errors,
        string sql,
        Action<NpgsqlParameterCollection> addParameters,
        ExperienceRunStoreOutcome done,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        if (errors.Count > 0)
        {
            return new(ExperienceRunStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceRunStoreOutcome.Denied, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            int changed;
            await using (var command = session.CreateCommand(sql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("run_id", runId));
                AddScopeParameters(command.Parameters, scope);
                addParameters(command.Parameters);
                changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(changed == 0 ? ExperienceRunStoreOutcome.NotFound : done, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, operation, cancellationToken);
        }
    }

    private static StoredExperienceRun Read(NpgsqlDataReader reader)
    {
        var runId = reader.GetGuid(0);
        var scope = new Scope(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
        return new StoredExperienceRun(
            ExperienceRunPayload.Deserialize(reader.GetInt32(10), reader.GetString(11), runId, scope),
            reader.GetGuid(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
    }
}
