using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Storage.InMemory;
using AgentExperience.Storage.Postgres;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// An <see cref="IExperienceRecordStore"/> in one local SQLite database file: durable, for a single agent (or a few
/// cooperating processes) on one machine -- a coding agent, a CLI, an MCP server. Create its schema first with
/// <see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it keeps.</b> It passes the same store conformance suite as the PostgreSQL and in-memory stores, and validates
/// every request with the PostgreSQL store's own rules, compiled from the same source file: create-only records with IDs
/// unique across every scope, exact-scope reads that reveal nothing across scopes, idempotent lifecycle commits keyed on
/// <see cref="LifecycleEvent.EventId"/>, the revision, prior-status and supersession guards, ordered and paged history, the
/// confidence ledger's evidence-ID idempotency, independence key and single-use assessment, injection tracking and both
/// holdouts. Every write runs in one <c>BEGIN IMMEDIATE</c> transaction, so two connections -- two stores, two processes --
/// committing from the same revision have exactly one winner. The record's payload is the PostgreSQL store's version-1 JSON.
/// </para>
/// <para>
/// <b>Erasure.</b> <see cref="DeleteAsync"/> leaves a payload-free tombstone and deletes every row that named the record
/// (its events, evidence, search text, signatures, injections and holdout rows), as the PostgreSQL store does; reads in its
/// own scope then answer <see cref="ExperienceStoreOutcome.Deleted"/>.
/// </para>
/// <para>
/// <b>What it leaves out.</b> Sharing grants and the grant access log (a read behaves as if no grant exists, so
/// <see cref="ExperienceRecordGetResult.SharedByGrant"/> is never set), encryption and crypto-shredding, row-level
/// security and database roles (anything that can open the file can read and write all of it), vectors, and retention
/// sweeps. Timestamps are stored in UTC, truncated to whole microseconds, as PostgreSQL keeps them.
/// </para>
/// </remarks>
public sealed partial class SqliteExperienceRecordStore : IExperienceRecordStore
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];
    private static readonly ExperienceReadOptions DeliveryRead = new();

    /// <summary>The columns <see cref="ReadRecord"/> reads, in its order.</summary>
    internal const string RecordColumns =
        "experience_id, source_run_id, " + ScopeColumns + ", task_id, status, reuse_confidence, supporting_validations, "
        + "contradictions, revision, created_at, updated_at, payload_version, payload, deleted_at";

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a store over the database <paramref name="connectionString"/> names. Use
    /// <see cref="ExperienceSqliteSchema.ConnectionStringFor"/> to build one from a file path.
    /// </summary>
    /// <param name="connectionString">A <c>Microsoft.Data.Sqlite</c> connection string naming the database file.</param>
    /// <param name="timeProvider">
    /// The clock a commit stamps <see cref="ExperienceRecord.UpdatedAt"/> and <see cref="StoredLifecycleEvent.RecordedAt"/>
    /// with, and injections and holdout rows are clamped to. <see langword="null"/> uses <see cref="TimeProvider.System"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is blank, malformed, or names no file.</exception>
    public SqliteExperienceRecordStore(string connectionString, TimeProvider? timeProvider = null)
        : this(new SqliteDatabase(connectionString), timeProvider)
    {
    }

    /// <summary>A store over a database the registration shares between its stores.</summary>
    internal SqliteExperienceRecordStore(SqliteDatabase database, TimeProvider? timeProvider)
    {
        Database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The database, for the candidate and signature sources that search this store's records.</summary>
    internal SqliteDatabase Database { get; }

    /// <summary>The store's clock, read as stored.</summary>
    private DateTimeOffset Now => Micro(_timeProvider.GetUtcNow());

    // ---------------------------------------------------------------- create and read

    /// <inheritdoc />
    public Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(record);

        var errors = ExperienceRecordValidator.ValidateRecord(record);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(record.Scope))
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordCreateResult>(cancellationToken);
        }

        return CreateCoreAsync(record, cancellationToken);
    }

    private Task<ExperienceRecordCreateResult> CreateCoreAsync(ExperienceRecord record, CancellationToken cancellationToken)
    {
        // Everything derived before the transaction: the payload, the searchable text, the signatures.
        var payload = ExperiencePayload.Serialize(record);
        var (taskIdText, summaryText, lessonText) = SearchColumns(record);
        var signatures = ExperienceFailureSignatures.Of(record);

        return Database.WriteAsync<ExperienceRecordCreateResult>(
            "The SQLite experience record could not be created.",
            async (connection, transaction) =>
            {
                var inserted = await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO experience_records (experience_id, source_run_id, " + ScopeColumns + ", task_id, status, "
                    + "reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, model_authored, "
                    + "payload_version, payload) VALUES ($id, $run, " + ScopeValues + ", $task_id, $status, $confidence, "
                    + "$supporting, $contradictions, $revision, $created_at, $updated_at, $model_authored, $payload_version, $payload) "
                    + "ON CONFLICT (experience_id) DO NOTHING",
                    cancellationToken,
                    [
                        ("$id", Id(record.ExperienceId)),
                        ("$run", Id(record.SourceRunId)),
                        .. ScopeParameters(record.Scope),
                        ("$task_id", record.TaskId),
                        ("$status", record.Status.ToString()),
                        ("$confidence", record.ReuseConfidence),
                        ("$supporting", record.SupportingValidations),
                        ("$contradictions", record.Contradictions),
                        ("$revision", record.Revision),
                        ("$created_at", Ticks(record.CreatedAt)),
                        ("$updated_at", Ticks(record.UpdatedAt)),
                        ("$model_authored", ReflectionAuthorshipRule.IsModelAuthored(record.Reflection) ? 1 : 0),
                        ("$payload_version", ExperiencePayload.CurrentVersion),
                        ("$payload", payload),
                    ]).ConfigureAwait(false);

                // IDs are unique across every scope, and the answer is the same whichever scope holds the existing one.
                if (inserted == 0)
                {
                    return (new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, NoErrors), false);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO experience_search (task_id, summary, lesson) VALUES ($task_id, $summary, $lesson)",
                    cancellationToken,
                    ("$task_id", taskIdText),
                    ("$summary", summaryText),
                    ("$lesson", lessonText)).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO experience_search_rows (experience_id, search_rowid) VALUES ($id, last_insert_rowid())",
                    cancellationToken,
                    ("$id", Id(record.ExperienceId))).ConfigureAwait(false);

                foreach (var held in signatures)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO experience_failure_signatures (experience_id, signature_key, tool_name, error_class, fingerprint, resolved) "
                        + "VALUES ($id, $key, $tool, $class, $fingerprint, $resolved) ON CONFLICT DO NOTHING",
                        cancellationToken,
                        ("$id", Id(record.ExperienceId)),
                        ("$key", held.Signature.Key),
                        ("$tool", held.Signature.ToolName),
                        ("$class", held.Signature.ErrorClass),
                        ("$fingerprint", held.Signature.Fingerprint),
                        ("$resolved", held.Resolution == ExperienceSignatureResolution.Resolved ? 1 : 0)).ConfigureAwait(false);
                }

                return (new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, NoErrors), true);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        GetAsync(authorization, scope, experienceId, DeliveryRead, cancellationToken);

    /// <inheritdoc />
    /// <remarks>There are no grants here, so <paramref name="options"/> changes nothing: nothing is audited.</remarks>
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Invalid, null, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Denied, null, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordGetResult>(cancellationToken);
        }

        return Database.RunAsync(
            "The SQLite experience record could not be read.",
            connection => ReadAsync(connection, null, scope, experienceId, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetManyResult> GetManyAsync(
        AuthorizationContext authorization,
        Scope scope,
        IReadOnlyList<Guid> experienceIds,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(experienceIds);
        ArgumentNullException.ThrowIfNull(options);

        var errors = ExperienceRecordValidator.ValidateGetMany(scope, experienceIds.Count);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordGetManyResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceRecordGetManyResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordGetManyResult>(cancellationToken);
        }

        var ids = experienceIds.ToArray();
        return Database.RunAsync(
            "The SQLite experience records could not be read.",
            async connection =>
            {
                // One read transaction, so every position sees the same snapshot.
                using var transaction = connection.BeginTransaction(deferred: true);
                var results = new ExperienceRecordGetResult[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    // An empty GUID is answered per position, with exactly the errors a single read of it gets.
                    results[i] = ids[i] == Guid.Empty
                        ? new(ExperienceStoreOutcome.Invalid, null, ExperienceRecordValidator.ValidateGet(scope, ids[i]))
                        : await ReadAsync(connection, transaction, scope, ids[i], cancellationToken).ConfigureAwait(false);
                }

                transaction.Commit();
                return new ExperienceRecordGetManyResult(ExperienceStoreOutcome.Found, results, NoErrors);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordQueryResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceRecordQueryResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordQueryResult>(cancellationToken);
        }

        return Database.RunAsync(
            "The SQLite experience records could not be queried.",
            async connection => new ExperienceRecordQueryResult(
                ExperienceStoreOutcome.Found,
                await QueryRecordsAsync(connection, null, query, cancellationToken).ConfigureAwait(false),
                NoErrors),
            cancellationToken);
    }

    private static async Task<List<ExperienceRecord>> QueryRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        var parameters = new List<(string, object?)>(ScopeParameters(query.Scope)) { ("$limit", query.Limit) };
        var statusFilter = string.Empty;
        if (query.Statuses is { } statuses)
        {
            var names = new List<string>();
            foreach (var (status, i) in statuses.Distinct().Select((status, i) => (status, i)))
            {
                names.Add($"$status{i}");
                parameters.Add(($"$status{i}", status.ToString()));
            }

            statusFilter = $" AND status IN ({string.Join(", ", names)})";
        }

        using var command = Command(
            connection,
            transaction,
            $"SELECT {RecordColumns} FROM experience_records WHERE {ScopePredicate} AND deleted_at IS NULL{statusFilter} "
            + "ORDER BY created_at DESC, experience_id LIMIT $limit",
            [.. parameters]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<ExperienceRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader)!);
        }

        return records;
    }

    // ---------------------------------------------------------------- history and supersession

    /// <inheritdoc />
    public Task<ExperienceRecordHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        ExperienceRecordHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Scope, $"{nameof(query)}.{nameof(query.Scope)}");

        var errors = ExperienceRecordValidator.ValidateHistoryQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Invalid, 0, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Denied, 0, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordHistoryResult>(cancellationToken);
        }

        return Database.RunAsync(
            "The SQLite experience record's history could not be read.",
            async connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var state = await StateInScopeAsync(connection, transaction, query.Scope, query.ExperienceId, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    // A record in another scope is indistinguishable from a missing one.
                    return new ExperienceRecordHistoryResult(ExperienceStoreOutcome.NotFound, 0, [], NoErrors);
                }

                if (state.Deleted)
                {
                    return new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Deleted, 0, [], NoErrors);
                }

                using var command = Command(
                    connection,
                    transaction,
                    "SELECT event, recorded_at, applied_revision, actor FROM experience_lifecycle_events "
                    + "WHERE experience_id = $id AND ($after IS NULL OR applied_revision > $after) "
                    + "ORDER BY applied_revision LIMIT $limit",
                    ("$id", Id(query.ExperienceId)),
                    ("$after", query.StartAfterRevision),
                    ("$limit", query.Limit));
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var events = new List<StoredLifecycleEvent>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    events.Add(new StoredLifecycleEvent(
                        FromJson<LifecycleEvent>(reader.GetString(0)),
                        Instant(reader.GetInt64(1)),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }

                return new ExperienceRecordHistoryResult(
                    ExperienceStoreOutcome.Found,
                    state.Revision,
                    events,
                    NoErrors,
                    events.Count > 0 ? events[^1].AppliedRevision : null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        Guid replacementExperienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateSupersessionCheck(scope, experienceId, replacementExperienceId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceSupersessionCheckResult(ExperienceSupersessionOutcome.Invalid, null, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceSupersessionCheckResult(ExperienceSupersessionOutcome.Denied, null, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceSupersessionCheckResult>(cancellationToken);
        }

        return Database.RunAsync(
            "The SQLite supersession check could not be read.",
            async connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                return await CheckSupersessionAsync(connection, transaction, scope, experienceId, replacementExperienceId, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="replacementId"/> may replace <paramref name="experienceId"/> in exactly
    /// <paramref name="scope"/>: both live there, and the replacement not already on a chain that leads back to the record.
    /// The chain is walked over this scope's events only.
    /// </summary>
    private static async Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Scope scope,
        Guid experienceId,
        Guid replacementId,
        CancellationToken cancellationToken)
    {
        var record = await StateInScopeAsync(connection, transaction, scope, experienceId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Deleted)
        {
            return new(ExperienceSupersessionOutcome.RecordNotFound, null, NoErrors);
        }

        var replacement = await StateInScopeAsync(connection, transaction, scope, replacementId, cancellationToken).ConfigureAwait(false);
        if (replacement is null || replacement.Deleted)
        {
            return new(ExperienceSupersessionOutcome.ReplacementNotFound, null, NoErrors);
        }

        var reached = new HashSet<Guid> { replacementId };
        var pending = new Queue<Guid>(reached);
        while (pending.TryDequeue(out var current))
        {
            using var command = Command(
                connection,
                transaction,
                $"SELECT replacement_experience_id FROM experience_lifecycle_events WHERE experience_id = $id AND {ScopePredicate} "
                + "AND replacement_experience_id IS NOT NULL",
                [("$id", Id(current)), .. ScopeParameters(scope)]);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var next = ReadId(reader, 0);
                if (reached.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return new(
            reached.Contains(experienceId) ? ExperienceSupersessionOutcome.Cycle : ExperienceSupersessionOutcome.Allowed,
            replacement.Status,
            NoErrors);
    }

    // ---------------------------------------------------------------- reading rows

    /// <summary>One read, answered as the port answers it: found, erased (only to its own scope), or not found.</summary>
    private static async Task<ExperienceRecordGetResult> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        using var command = Command(
            connection,
            transaction,
            $"SELECT {RecordColumns} FROM experience_records WHERE experience_id = $id AND {ScopePredicate}",
            [("$id", Id(experienceId)), .. ScopeParameters(scope)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(ExperienceStoreOutcome.NotFound, null, NoErrors);
        }

        var record = ReadRecord(reader);
        return record is null
            ? new(ExperienceStoreOutcome.Deleted, null, NoErrors)
            : new(ExperienceStoreOutcome.Found, record, NoErrors);
    }

    /// <summary>A record row read with <see cref="RecordColumns"/>, or <see langword="null"/> for a tombstone.</summary>
    internal static ExperienceRecord? ReadRecord(SqliteDataReader reader)
    {
        if (!reader.IsDBNull(18))
        {
            return null;
        }

        var payload = ExperiencePayload.Deserialize(reader.GetInt32(16), reader.GetString(17));
        return ExperiencePayload.ToRecord(
            payload,
            ReadId(reader, 0),
            ReadId(reader, 1),
            ReadScope(reader, 2),
            reader.GetString(8),
            Enum.Parse<ExperienceStatus>(reader.GetString(9)),
            reader.GetDouble(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            Instant(reader.GetInt64(14)),
            Instant(reader.GetInt64(15)));
    }

    /// <summary>What a commit needs to know about a record in exactly one scope.</summary>
    private sealed record RecordState(ExperienceStatus Status, long Revision, DateTimeOffset UpdatedAt, bool Deleted);

    /// <summary>The record's state when it lies in exactly <paramref name="scope"/>, live or erased; otherwise <see langword="null"/>.</summary>
    private static async Task<RecordState?> StateInScopeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        using var command = Command(
            connection,
            transaction,
            $"SELECT status, revision, updated_at, deleted_at FROM experience_records WHERE experience_id = $id AND {ScopePredicate}",
            [("$id", Id(experienceId)), .. ScopeParameters(scope)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RecordState(
            Enum.Parse<ExperienceStatus>(reader.GetString(0)),
            reader.GetInt64(1),
            Instant(reader.GetInt64(2)),
            !reader.IsDBNull(3));
    }

    // ---------------------------------------------------------------- search text

    /// <summary>
    /// The three searchable fields as the full-text table indexes them: Unicode form KC, at most
    /// <see cref="SearchText.MaxIndexedLength"/> characters across task ID, task summary and lesson in that order (a
    /// separator between fields counting too), the PostgreSQL search column's bound.
    /// </summary>
    internal static (string TaskId, string Summary, string Lesson) SearchColumns(ExperienceRecord record)
    {
        var budget = SearchText.MaxIndexedLength;
        var taskId = Take(record.TaskId, ref budget);
        var summary = Take(record.TaskSummary, ref budget);
        var lesson = Take(record.Reflection?.Lesson, ref budget);
        return (taskId, summary, lesson);

        static string Take(string? text, ref int budget)
        {
            if (string.IsNullOrEmpty(text) || budget <= 0)
            {
                budget--;
                return string.Empty;
            }

            var taken = text.Length <= budget ? text : text[..budget];
            budget -= taken.Length + 1;
            return Normalize(taken);
        }
    }

    /// <summary>Unicode form KC, or the text as it is when it is not valid UTF-16.</summary>
    internal static string Normalize(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static string? Actor(AuthorizationContext authorization) =>
        string.IsNullOrWhiteSpace(authorization.PrincipalId) ? null : authorization.PrincipalId;
}
