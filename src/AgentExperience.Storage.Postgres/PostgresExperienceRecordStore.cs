using System.Data.Common;
using System.Net.Sockets;
using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceRecordStore"/> over PostgreSQL with plain Npgsql. Each operation validates
/// the request, checks it against the host-established <see cref="AuthorizationContext"/>, and only
/// then opens a connection and runs parameterized SQL whose predicates apply the exact scope. The
/// schema must already exist: the host applies it once by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>. The store
/// never migrates, on construction or otherwise.
/// </summary>
/// <remarks>
/// <see cref="CommitLifecycleEventAsync"/> is the only operation that changes a stored record: it appends
/// the event and updates the record's projection in one transaction on one connection, keyed by
/// <see cref="LifecycleEvent.EventId"/> for idempotency and by
/// <see cref="LifecycleEvent.ExpectedRevision"/> for concurrency. The store persists the transition Core
/// decided and never derives a status, score, or counter of its own.
/// PostgreSQL <c>timestamptz</c> stores microseconds, so <see cref="ExperienceRecord.CreatedAt"/> and
/// <see cref="ExperienceRecord.UpdatedAt"/> are truncated to whole microseconds (in UTC) on write.
/// Nested timestamps live in the JSONB payload at full precision and are also returned in UTC.
/// Tool-call argument values read back JSON-normalized: <see cref="string"/>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="double"/>, <see langword="null"/>,
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/>, and
/// <see cref="List{T}"/> of <see cref="object"/>. Dictionary key order is not preserved, and whole-number
/// doubles read back as <see cref="long"/>. Query ties on <c>CreatedAt</c> are broken by PostgreSQL <c>uuid</c>
/// byte order, which differs from .NET <see cref="Guid"/> comparison.
/// </remarks>
public sealed class PostgresExperienceRecordStore : IExperienceRecordStore
{
    /// <summary>The canonical record table. Shared with <see cref="PostgresExperienceCandidateSource"/>, which reads from it.</summary>
    internal const string Table = "agent_experience.experience_records";

    /// <summary>
    /// The record columns every read selects, in the order <see cref="DecodeRecord"/> expects (ordinals 0-17).
    /// A reader that selects more must append its extra columns <em>after</em> these, never before.
    /// </summary>
    internal const string SelectColumns =
        "experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, " +
        "status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
        "payload_version, payload";

    /// <summary>The exact-scope predicate every statement applies, shared with <see cref="PostgresExperienceCandidateSource"/>.</summary>
    internal const string ScopePredicate =
        "tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id " +
        "AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND user_id IS NOT DISTINCT FROM @user_id";

    private const string InsertSql =
        $"INSERT INTO {Table} ({SelectColumns}) VALUES (@experience_id, @source_run_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @task_id, @status, @reuse_confidence, @supporting_validations, " +
        "@contradictions, @revision, @created_at, @updated_at, @payload_version, @payload)";

    private const string GetSql =
        $"SELECT {SelectColumns} FROM {Table} WHERE experience_id = @experience_id AND {ScopePredicate}";

    private const string QuerySql = $"SELECT {SelectColumns} FROM {Table} WHERE {ScopePredicate}";

    private const string QueryStatusPredicate = " AND status = ANY(@statuses)";

    private const string QueryOrderAndLimit = " ORDER BY created_at DESC, experience_id LIMIT @limit";

    private const string EventsTable = "agent_experience.lifecycle_events";

    private const string EventColumns =
        "event_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision";

    private const string InsertEventSql =
        $"INSERT INTO {EventsTable} ({EventColumns}) VALUES (@event_id, @experience_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @prior_status, @current_status, @reason, @producer, " +
        "@occurred_at, @recorded_at, @expected_revision, @applied_revision)";

    /// <summary>The primary key a resubmitted <see cref="LifecycleEvent.EventId"/> violates.</summary>
    private const string EventPrimaryKey = "lifecycle_events_pkey";

    /// <summary>The unique index a second event claiming an already-taken record revision violates.</summary>
    private const string EventRevisionIndex = "ix_lifecycle_events_record_revision";

    /// <summary>
    /// The revision guard, the prior-status guard, and the scope predicate live in the same statement,
    /// so a stale revision, a prior status the record is not in, and a foreign scope are all "no row
    /// updated" and none of them can overwrite state it does not own. A <see langword="null"/>
    /// <c>@prior_status</c> (a record's first event) skips the status match.
    /// </summary>
    private const string UpdateProjectionSql =
        $"UPDATE {Table} SET status = @current_status, revision = @applied_revision, updated_at = @recorded_at " +
        $"WHERE experience_id = @experience_id AND revision = @expected_revision " +
        $"AND (@prior_status IS NULL OR status = @prior_status) AND {ScopePredicate}";

    private const string SelectRevisionAndStatusSql =
        $"SELECT revision, status FROM {Table} WHERE experience_id = @experience_id AND {ScopePredicate}";

    private const string SelectEventSql = $"SELECT {EventColumns} FROM {EventsTable} WHERE event_id = @event_id";

    private const string JoinedEventColumns =
        "e.event_id, e.experience_id, e.tenant_id, e.application_id, e.project_id, e.team_id, e.agent_id, e.user_id, " +
        "e.prior_status, e.current_status, e.reason, e.producer, e.occurred_at, e.recorded_at, e.expected_revision, " +
        "e.applied_revision";

    /// <summary>
    /// The same exact-scope predicate as <see cref="ScopePredicate"/>, qualified with the <c>r</c>
    /// alias for a statement that joins the record table to another one. Shared with the vectors
    /// adapter, so both retrieval channels apply a byte-for-byte identical scope match.
    /// </summary>
    internal const string RecordScopePredicate =
        "r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id " +
        "AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND r.user_id IS NOT DISTINCT FROM @user_id";

    /// <summary>
    /// One statement, so the revision and the events come from one snapshot however the server is
    /// configured: a commit landing mid-read can never make the returned revision contradict the
    /// returned events. The outer join keeps a record with no events a <see cref="ExperienceStoreOutcome.Found"/>
    /// with an empty history -- that row has a null <c>event_id</c>.
    /// </summary>
    private const string HistorySql =
        $"SELECT {JoinedEventColumns}, r.revision FROM {Table} r " +
        $"LEFT JOIN {EventsTable} e ON e.experience_id = r.experience_id " +
        $"WHERE r.experience_id = @experience_id AND {RecordScopePredicate} " +
        "ORDER BY e.applied_revision";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a store over a host-owned data source. The store never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceRecordStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(record);

        var errors = ExperienceRecordValidator.ValidateRecord(record);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(record.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoErrors);
        }

        string payload;
        try
        {
            payload = ExperiencePayload.Serialize(record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any serialization failure (non-finite doubles, invalid UTF-16, cycles, throwing getters)
            // is a malformed request, never an infrastructure failure.
            return new(
                ExperienceStoreOutcome.Invalid,
                [new StoreValidationError("Attempts", "tool-call arguments could not be serialized to JSON.")]);
        }

        if (ContainsEscapedNul(payload))
        {
            return new(
                ExperienceStoreOutcome.Invalid,
                [new StoreValidationError("Payload", "must not contain the NUL character (U+0000), which PostgreSQL jsonb cannot store.")]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(InsertSql);
            var parameters = command.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            parameters.Add(new NpgsqlParameter<Guid>("source_run_id", record.SourceRunId));
            AddScopeParameters(parameters, record.Scope);
            parameters.Add(new NpgsqlParameter<string>("task_id", record.TaskId));
            parameters.Add(new NpgsqlParameter<string>("status", record.Status.ToString()));
            parameters.Add(new NpgsqlParameter<double>("reuse_confidence", record.ReuseConfidence));
            parameters.Add(new NpgsqlParameter<int>("supporting_validations", record.SupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("contradictions", record.Contradictions));
            parameters.Add(new NpgsqlParameter<long>("revision", record.Revision));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", ToStoredTimestamp(record.CreatedAt)));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("updated_at", ToStoredTimestamp(record.UpdatedAt)));
            parameters.Add(new NpgsqlParameter<int>("payload_version", ExperiencePayload.CurrentVersion));
            parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = payload });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Created, NoErrors);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && !cancellationToken.IsCancellationRequested)
        {
            // Identical regardless of which scope owns the existing ID: no record data is revealed.
            return new(ExperienceStoreOutcome.Conflict, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "create", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(GetSql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            AddScopeParameters(command.Parameters, scope);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(ExperienceStoreOutcome.NotFound, null, NoErrors);
            }

            return new(ExperienceStoreOutcome.Found, ReadRecord(reader), NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "get", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var sql = query.Statuses is null
                ? QuerySql + QueryOrderAndLimit
                : QuerySql + QueryStatusPredicate + QueryOrderAndLimit;

            await using var command = _dataSource.CreateCommand(sql);
            AddScopeParameters(command.Parameters, query.Scope);
            if (query.Statuses is not null)
            {
                var statuses = query.Statuses.Distinct().Select(s => s.ToString()).ToArray();
                command.Parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
            }

            command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            var records = new List<ExperienceRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(ReadRecord(reader));
            }

            return new(ExperienceStoreOutcome.Found, records, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        var errors = ExperienceRecordValidator.ValidateLifecycleEvent(scope, lifecycleEvent);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Both timestamps are truncated the same way the record's columns are, so a replay's stored
        // OccurredAt compares equal to the value the caller resubmits.
        var occurredAt = ToStoredTimestamp(lifecycleEvent.OccurredAt);
        var recordedAt = ToStoredTimestamp(DateTimeOffset.UtcNow);
        var appliedRevision = lifecycleEvent.ExpectedRevision + 1;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Pinned, not inherited: under REPEATABLE READ or SERIALIZABLE the same-revision race would
            // abort with a serialization failure instead of matching no row, turning an expected stale
            // revision into an infrastructure failure.
            await using var transaction = await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            try
            {
                await using var insert = new NpgsqlCommand(InsertEventSql, connection, transaction);
                AddEventParameters(insert.Parameters, scope, lifecycleEvent, occurredAt, recordedAt, appliedRevision);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, EventPrimaryKey, cancellationToken))
            {
                // A resubmitted event ID. PostgreSQL has aborted the transaction, so nothing this call
                // attempted survives; the stored row then decides replay from conflict.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await CompareStoredEventAsync(connection, scope, lifecycleEvent, occurredAt, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, EventRevisionIndex, cancellationToken))
            {
                // A different event already claimed this record revision. The unique index makes the
                // loser of a same-revision race block here and fail once the winner commits, which is a
                // stale revision by another name.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await StaleOrMissingAsync(connection, null, scope, lifecycleEvent.ExperienceRecordId, cancellationToken).ConfigureAwait(false);
            }

            int updated;
            try
            {
                await using var update = new NpgsqlCommand(UpdateProjectionSql, connection, transaction);
                var parameters = update.Parameters;
                parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
                parameters.Add(new NpgsqlParameter<string>("current_status", lifecycleEvent.CurrentStatus.ToString()));
                parameters.Add(NullableText("prior_status", lifecycleEvent.PriorStatus?.ToString()));
                parameters.Add(new NpgsqlParameter<long>("expected_revision", lifecycleEvent.ExpectedRevision));
                parameters.Add(new NpgsqlParameter<long>("applied_revision", appliedRevision));
                parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
                AddScopeParameters(parameters, scope);
                updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (!cancellationToken.IsCancellationRequested
                && ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                // Another writer got there first. However the server is configured, losing that race is an
                // expected condition, not an infrastructure failure.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await StaleOrMissingAsync(connection, null, scope, lifecycleEvent.ExperienceRecordId, cancellationToken).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                // The record is not in this scope, its revision has moved on, or it is not in the status
                // the event was decided against. The row is re-read inside the same transaction that is
                // about to be rolled back, so the event insert above never reaches the log.
                var current = await ReadRevisionAndStatusAsync(connection, transaction, scope, lifecycleEvent.ExperienceRecordId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                if (current is not { } record)
                {
                    return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
                }

                return record.Revision != lifecycleEvent.ExpectedRevision
                    ? new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors)
                    // Scope and revision both matched, so the prior-status guard is what rejected it.
                    : new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            // Nothing this call wrote is visible unless the commit itself succeeded and only its
            // acknowledgement was lost; retrying the identical event then replays instead of reapplying.
            throw Translate(ex, "lifecycle commit", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, [], errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(HistorySql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            AddScopeParameters(command.Parameters, scope);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // No record in this scope: indistinguishable from one that exists elsewhere.
                return new(ExperienceStoreOutcome.NotFound, 0, [], NoErrors);
            }

            var revision = ReadRevision(reader, 16);

            var events = new List<LifecycleEvent>();
            if (!reader.IsDBNull(0))
            {
                // A null event_id is the outer join's single "record with no events" row.
                do
                {
                    events.Add(ReadEvent(reader));
                }
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            return new(ExperienceStoreOutcome.Found, revision, events, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "history", cancellationToken);
        }
    }

    /// <summary>
    /// Decides a resubmitted <see cref="LifecycleEvent.EventId"/>: byte-for-byte the same event (scope
    /// included) is the original commit replayed, so its original outcome is returned and nothing is
    /// written; any difference is a <see cref="ExperienceStoreOutcome.Conflict"/>. The comparison is
    /// identical whichever scope owns the stored event, so it reveals no event data.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> CompareStoredEventAsync(
        NpgsqlConnection connection,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectEventSql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", lifecycleEvent.EventId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Events are never deleted, so the row that just collided cannot vanish. Treat the
            // impossible case as a conflict rather than writing anything.
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        var stored = ReadEvent(reader);
        var storedScope = ReadEventScope(reader);
        var appliedRevision = ReadRevision(reader, 15);

        // Record equality compares every field of the event; the scope is compared alongside it. The
        // revision reported is the one the original commit produced, not the record's current one.
        var resubmitted = lifecycleEvent with { OccurredAt = occurredAt };
        return stored == resubmitted && storedScope == scope
            ? new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors)
            : new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
    }

    /// <summary>
    /// Reports a lost race: the record's current revision, or <see cref="ExperienceStoreOutcome.NotFound"/>
    /// when it is not in this scope at all. Used where the server aborted the transaction itself, so the
    /// re-read runs outside it.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> StaleOrMissingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        var current = await ReadRevisionAndStatusAsync(connection, transaction, scope, experienceId, cancellationToken).ConfigureAwait(false);
        return current is { } record
            ? new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors)
            : new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
    }

    private static async Task<(long Revision, ExperienceStatus Status)?> ReadRevisionAndStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectRevisionAndStatusSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(command.Parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (ReadRevision(reader, 0), ReadStoredStatus(reader, 1));
    }

    /// <summary>
    /// Matches a unique violation of one named constraint. Naming it keeps the event primary key (a
    /// resubmitted event ID) apart from the record-revision index (a lost race), so neither is ever
    /// mistaken for the other or for a constraint added later.
    /// </summary>
    private static bool IsViolationOf(PostgresException ex, string constraintName, CancellationToken cancellationToken) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(ex.ConstraintName, constraintName, StringComparison.Ordinal)
        && !cancellationToken.IsCancellationRequested;

    private static void AddEventParameters(
        NpgsqlParameterCollection parameters,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt,
        long appliedRevision)
    {
        parameters.Add(new NpgsqlParameter<Guid>("event_id", lifecycleEvent.EventId));
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
        AddScopeParameters(parameters, scope);
        parameters.Add(NullableText("prior_status", lifecycleEvent.PriorStatus?.ToString()));
        parameters.Add(new NpgsqlParameter<string>("current_status", lifecycleEvent.CurrentStatus.ToString()));
        parameters.Add(new NpgsqlParameter<string>("reason", lifecycleEvent.Reason));
        parameters.Add(new NpgsqlParameter<string>("producer", lifecycleEvent.Producer));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", occurredAt));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
        parameters.Add(new NpgsqlParameter<long>("expected_revision", lifecycleEvent.ExpectedRevision));
        parameters.Add(new NpgsqlParameter<long>("applied_revision", appliedRevision));
    }

    internal static void AddScopeParameters(NpgsqlParameterCollection parameters, Scope scope)
    {
        parameters.Add(new NpgsqlParameter<string>("tenant_id", NpgsqlDbType.Text) { TypedValue = scope.TenantId });
        parameters.Add(new NpgsqlParameter<string>("application_id", NpgsqlDbType.Text) { TypedValue = scope.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("project_id", NpgsqlDbType.Text) { TypedValue = scope.ProjectId });
        parameters.Add(NullableText("team_id", scope.TeamId));
        parameters.Add(NullableText("agent_id", scope.AgentId));
        parameters.Add(NullableText("user_id", scope.UserId));
    }

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static DateTimeOffset ToStoredTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }

    /// <summary>
    /// Finds a JSON <c>\u0000</c> escape whose backslash is not itself escaped (an odd run of backslashes).
    /// </summary>
    private static bool ContainsEscapedNul(string json)
    {
        const string Escape = "\\u0000";
        for (var index = json.IndexOf(Escape, StringComparison.Ordinal); index >= 0; index = json.IndexOf(Escape, index + 1, StringComparison.Ordinal))
        {
            var backslashes = 0;
            for (var i = index; i >= 0 && json[i] == '\\'; i--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 1)
            {
                return true;
            }
        }

        return false;
    }

    internal static ExperienceRecord ReadRecord(DbDataReader reader)
    {
        try
        {
            return DecodeRecord(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt payload (e.g. InvalidCastException, a null array element).
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static LifecycleEvent ReadEvent(DbDataReader reader)
    {
        try
        {
            return DecodeEvent(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt row (e.g. InvalidCastException on a retyped column).
            throw new ExperienceStoreException("Stored lifecycle event could not be decoded.", ex);
        }
    }

    private static LifecycleEvent DecodeEvent(DbDataReader reader) => new(
        EventId: reader.GetGuid(0),
        ExperienceRecordId: reader.GetGuid(1),
        PriorStatus: reader.IsDBNull(8) ? null : DecodeStatus(reader.GetString(8), "lifecycle event"),
        CurrentStatus: DecodeStatus(reader.GetString(9), "lifecycle event"),
        Reason: reader.GetString(10),
        Producer: reader.GetString(11),
        OccurredAt: reader.GetFieldValue<DateTimeOffset>(12),
        ExpectedRevision: reader.GetInt64(14));

    /// <summary>Reads a <c>bigint</c> revision, reporting schema drift the way the row decoders do.</summary>
    private static long ReadRevision(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static ExperienceStatus ReadStoredStatus(DbDataReader reader, int ordinal)
    {
        try
        {
            return DecodeStatus(reader.GetString(ordinal), "Experience Record");
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static Scope ReadEventScope(DbDataReader reader) => new(
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <param name="statusText">The stored status text.</param>
    /// <param name="objectKind">Which stored object the text came from, so a failure names the right row.</param>
    private static ExperienceStatus DecodeStatus(string statusText, string objectKind)
    {
        if (!Enum.TryParse<ExperienceStatus>(statusText, ignoreCase: false, out var status) || !Enum.IsDefined(status)
            || !string.Equals(status.ToString(), statusText, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException($"Stored {objectKind} has an unrecognized status.");
        }

        return status;
    }

    private static ExperienceRecord DecodeRecord(DbDataReader reader)
    {
        var status = DecodeStatus(reader.GetString(9), "Experience Record");

        var payload = ExperiencePayload.Deserialize(reader.GetInt32(16), reader.GetString(17));

        var scope = new Scope(
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

        return ExperiencePayload.ToRecord(
            payload,
            reader.GetGuid(0),
            reader.GetGuid(1),
            scope,
            reader.GetString(8),
            status,
            reader.GetDouble(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }

    /// <summary>
    /// Driver, socket, and timeout failures are translated. An <see cref="OperationCanceledException"/>
    /// caused by the caller's own token is not matched, so it propagates unwrapped with its stack.
    /// </summary>
    internal static bool IsInfrastructureFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        NpgsqlException or SocketException or TimeoutException => true,
        _ => false,
    };

    internal static Exception Translate(Exception ex, string operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled while the driver reported a failure: surface cancellation, unwrapped.
            return new OperationCanceledException("The Experience Record store operation was cancelled.", ex, cancellationToken);
        }

        return new ExperienceStoreException($"Experience Record {operation} failed due to a storage infrastructure error.", ex);
    }
}
