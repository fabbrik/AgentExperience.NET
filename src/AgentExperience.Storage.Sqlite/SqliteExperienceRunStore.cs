using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// An <see cref="IExperienceRunStore"/> in the SQLite database: each completed run, as capture held it (already
/// sanitized), so a run captured in one process can be finalized in a later one. It passes the run store conformance
/// suite, as the PostgreSQL run store does, and stores the same version-1 payload.
/// </summary>
/// <remarks>
/// Run IDs are unique across every scope: saving a run whose ID another scope holds is
/// <see cref="ExperienceRunStoreOutcome.Conflict"/>, revealing nothing. Erasing a record deletes its run's row too, when
/// that was the run's last live record. A stored run is not encrypted; delete it once it is finalized, or after a retention.
/// </remarks>
public sealed class SqliteExperienceRunStore : IExperienceRunStore
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private const string Columns =
        "run_id, " + ScopeColumns + ", completion_event_id, saved_at, finalized_at, payload_version, payload";

    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a run store over the database <paramref name="connectionString"/> names.</summary>
    /// <param name="connectionString">A <c>Microsoft.Data.Sqlite</c> connection string naming the database file; see <see cref="ExperienceSqliteSchema.ConnectionStringFor"/>.</param>
    /// <param name="timeProvider">The clock a save is stamped with. <see langword="null"/> uses <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is blank, malformed, or names no file.</exception>
    public SqliteExperienceRunStore(string connectionString, TimeProvider? timeProvider = null)
    {
        _database = new SqliteDatabase(connectionString);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<ExperienceRunStoreResult> SaveAsync(
        AuthorizationContext authorization,
        ExperienceRun run,
        Guid completionEventId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateRunSave(run, completionEventId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRunStoreResult(ExperienceRunStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(run.Scope))
        {
            return Task.FromResult(new ExperienceRunStoreResult(ExperienceRunStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRunStoreResult>(cancellationToken);
        }

        var payload = ExperienceRunPayload.Serialize(run);
        var savedAt = Ticks(_timeProvider.GetUtcNow());
        return _database.WriteAsync(
            "The SQLite run could not be saved.",
            async (connection, transaction) =>
            {
                var inserted = await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO experience_runs (run_id, {ScopeColumns}, completion_event_id, execution_status, ended_at, saved_at, "
                    + $"payload_version, payload) VALUES ($run, {ScopeValues}, $event, $status, $ended_at, $saved_at, $version, $payload) "
                    + "ON CONFLICT (run_id) DO NOTHING",
                    cancellationToken,
                    [
                        ("$run", Id(run.RunId)),
                        ("$event", Id(completionEventId)),
                        ("$status", run.ExecutionStatus!.Value.ToString()),
                        ("$ended_at", Ticks(run.EndedAt!.Value)),
                        ("$saved_at", savedAt),
                        ("$version", ExperienceRunPayload.CurrentVersion),
                        ("$payload", payload),
                        .. ScopeParameters(run.Scope),
                    ]).ConfigureAwait(false);
                if (inserted == 1)
                {
                    return (new ExperienceRunStoreResult(ExperienceRunStoreOutcome.Saved, NoErrors), true);
                }

                // The same run under the same completion event, in the same scope, is a replay; anything else is a conflict.
                var same = await ScalarAsync(
                    connection,
                    transaction,
                    $"SELECT 1 FROM experience_runs WHERE run_id = $run AND completion_event_id = $event AND {ScopePredicate}",
                    cancellationToken,
                    [("$run", Id(run.RunId)), ("$event", Id(completionEventId)), .. ScopeParameters(run.Scope)]).ConfigureAwait(false);
                return (new ExperienceRunStoreResult(
                    same is null ? ExperienceRunStoreOutcome.Conflict : ExperienceRunStoreOutcome.AlreadySaved,
                    NoErrors), false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExperienceRunGetResult> GetAsync(AuthorizationContext authorization, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateRunId(runId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRunGetResult(ExperienceRunStoreOutcome.Invalid, null, errors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRunGetResult>(cancellationToken);
        }

        return _database.RunAsync(
            "The SQLite run could not be read.",
            async connection =>
            {
                // The tenant is matched in the statement, so another tenant's run is never even read; the remaining bounds
                // are the authorization's own rule, applied to the stored scope.
                using var command = Command(
                    connection,
                    null,
                    $"SELECT {Columns} FROM experience_runs WHERE run_id = $run AND tenant_id = $tenant",
                    ("$run", Id(runId)),
                    ("$tenant", authorization.TenantId));
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new ExperienceRunGetResult(ExperienceRunStoreOutcome.NotFound, null, NoErrors);
                }

                var stored = Read(reader);
                return authorization.Permits(stored.Run.Scope)
                    ? new ExperienceRunGetResult(ExperienceRunStoreOutcome.Found, stored, NoErrors)
                    : new ExperienceRunGetResult(ExperienceRunStoreOutcome.NotFound, null, NoErrors);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExperienceRunListResult> ListUnfinalizedAsync(AuthorizationContext authorization, ExperienceRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateRunQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRunListResult(ExperienceRunStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceRunListResult(ExperienceRunStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRunListResult>(cancellationToken);
        }

        return _database.RunAsync(
            "The SQLite runs could not be listed.",
            async connection =>
            {
                using var command = Command(
                    connection,
                    null,
                    $"SELECT {Columns} FROM experience_runs WHERE {ScopePredicate} AND finalized_at IS NULL "
                    + "AND ($before IS NULL OR ended_at < $before) AND ($after IS NULL OR run_id > $after) ORDER BY run_id LIMIT $limit",
                    [
                        .. ScopeParameters(query.Scope),
                        ("$before", query.EndedBefore is { } before ? Ticks(before) : null),
                        ("$after", query.AfterRunId is { } after ? Id(after) : null),
                        ("$limit", query.Limit),
                    ]);
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var runs = new List<StoredExperienceRun>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    runs.Add(Read(reader));
                }

                return new ExperienceRunListResult(ExperienceRunStoreOutcome.Found, runs, NoErrors);
            },
            cancellationToken);
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
            $"UPDATE experience_runs SET finalized_at = coalesce(finalized_at, $at) WHERE run_id = $run AND {ScopePredicate}",
            ("$at", Ticks(finalizedAt)),
            ExperienceRunStoreOutcome.Updated,
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
            $"DELETE FROM experience_runs WHERE run_id = $run AND {ScopePredicate}",
            ("$unused", null),
            ExperienceRunStoreOutcome.Deleted,
            cancellationToken);

    private Task<ExperienceRunStoreResult> WriteInScopeAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        IReadOnlyList<StoreValidationError> errors,
        string sql,
        (string, object?) extra,
        ExperienceRunStoreOutcome done,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRunStoreResult(ExperienceRunStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceRunStoreResult(ExperienceRunStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRunStoreResult>(cancellationToken);
        }

        return _database.WriteAsync(
            "The SQLite run could not be written.",
            async (connection, transaction) =>
            {
                var changed = await ExecuteAsync(
                    connection,
                    transaction,
                    sql,
                    cancellationToken,
                    [("$run", Id(runId)), extra, .. ScopeParameters(scope)]).ConfigureAwait(false);
                return (new ExperienceRunStoreResult(changed == 0 ? ExperienceRunStoreOutcome.NotFound : done, NoErrors), changed > 0);
            },
            cancellationToken);
    }

    private static StoredExperienceRun Read(SqliteDataReader reader)
    {
        var runId = ReadId(reader, 0);
        var scope = ReadScope(reader, 1);
        return new StoredExperienceRun(
            ExperienceRunPayload.Deserialize(reader.GetInt32(10), reader.GetString(11), runId, scope),
            ReadId(reader, 7),
            Instant(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : Instant(reader.GetInt64(9)));
    }
}
