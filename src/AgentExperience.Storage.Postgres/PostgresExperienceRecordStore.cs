using System.Data.Common;
using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceRecordStore"/> over PostgreSQL with plain Npgsql. Each operation validates
/// the request, checks it against the host-established <see cref="AuthorizationContext"/>, and only
/// then opens a connection and runs parameterized SQL whose predicates apply the exact scope -- or,
/// for <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> alone, the exact scope or an active sharing grant. The
/// schema must already exist: the host applies it once by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>. The store
/// never migrates, on construction or otherwise.
/// </summary>
/// <remarks>
/// <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> is also the one read that can be <em>audited</em>: when the host wires an
/// <see cref="ExperienceGrantAuditing"/>, a record delivered through a grant appends one access row naming the
/// grant the statement actually used. The append is a separate statement after the read, never part of it, so a
/// read still takes no write lock and can still run on a replica. An owner's own record writes nothing, and nor
/// does a read the caller declared an <see cref="ExperienceReadPurpose.ScopeCheck"/> -- it is about to refuse the
/// record for being grant-readable, so nothing is handed over. The two search channels audit what they return
/// through their own batched append.
/// <see cref="CommitLifecycleEventAsync"/> is the only operation that changes a <em>live</em> record: it appends
/// the event and updates the record's projection in one transaction on one connection, keyed by
/// <see cref="LifecycleEvent.EventId"/> for idempotency and by
/// <see cref="LifecycleEvent.ExpectedRevision"/> for concurrency. The store persists the transition Core
/// decided and never derives a status, score, or counter of its own.
/// <see cref="DeleteAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> is the one destructive
/// operation: it erases a record's payload and every stored row that named it, in one transaction, and leaves a
/// payload-free tombstone behind that every other path then refuses.
/// PostgreSQL <c>timestamptz</c> stores microseconds, so <see cref="ExperienceRecord.CreatedAt"/> and
/// <see cref="ExperienceRecord.UpdatedAt"/> are truncated to whole microseconds (in UTC) on write.
/// Nested timestamps live in the JSONB payload at full precision and are also returned in UTC.
/// Tool-call argument values read back JSON-normalized: <see cref="string"/>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="double"/>, <see langword="null"/>,
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/>, and
/// <see cref="List{T}"/> of <see cref="object"/>. Dictionary key order is not preserved, and whole-number
/// doubles read back as <see cref="long"/>. Query ties on <c>CreatedAt</c> are broken by PostgreSQL <c>uuid</c>
/// byte order, which differs from .NET <see cref="Guid"/> comparison.
/// <para>
/// Source map: this file holds the constructor, create and the reads (get, get many, query, history, supersession
/// check). <c>PostgresExperienceRecordStore.Lifecycle.cs</c> holds the lifecycle commit;
/// <c>PostgresExperienceRecordStore.Erasure.cs</c> delete, the retention sweep and the purge; and
/// <c>PostgresExperienceRecordStore.Sealing.cs</c> sealing and the authorship backfill. The shared SQL fragments,
/// row decoding, parameter binding and failure translation live in <c>ExperienceRecordSql</c>,
/// <c>ExperienceRecordRows</c>, <c>ExperienceRecordParameters</c> and <c>ExperienceStoreFailures</c>.
/// </para>
/// </remarks>
public sealed partial class PostgresExperienceRecordStore : IExperienceRecordStore
{
    /// <summary>The smallest batch <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/> accepts. There is no "sweep everything".</summary>
    public const int MinSweepBatchSize = 1;

    /// <summary>
    /// The largest batch <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/> accepts. Each record in a batch is erased in
    /// its own transaction across seven tables, so the bound is what keeps one sweep call from becoming
    /// an unbounded amount of destructive work the host cannot interrupt.
    /// </summary>
    public const int MaxSweepBatchSize = 500;

    private const string InsertSql =
        $"INSERT INTO {Table} ({SelectColumns}) VALUES (@experience_id, @source_run_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @task_id, @status, @reuse_confidence, @supporting_validations, " +
        "@contradictions, @revision, @created_at, @updated_at, @payload_version, @payload)";

    /// <summary>
    /// The insert for a sealed record: the sealed payload, the placeholder task ID, the derived vector, and the
    /// authorship flag, which stays in the clear so a search can filter on it.
    /// </summary>
    private const string InsertSealedSql =
        $"INSERT INTO {Table} ({SelectColumns}, search_vector_sealed, {ModelAuthoredColumn}) VALUES (@experience_id, " +
        "@source_run_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, @task_id, @status, " +
        "@reuse_confidence, @supporting_validations, @contradictions, @revision, @created_at, @updated_at, " +
        "@payload_version, @payload, " + SealedSearchVectorExpression + ", @reflection_model_authored)";

    private const string QuerySql = $"SELECT {SelectColumns} FROM {Table} WHERE {ScopePredicate} AND {LivePredicate}";

    private const string QueryStatusPredicate = " AND status = ANY(@statuses)";

    private const string QueryOrderAndLimit = " ORDER BY created_at DESC, experience_id LIMIT @limit";

    /// <summary>The columns a sweep candidate is read with: its ID, and the exact scope it is erased in.</summary>
    private const string SweepCandidateColumns =
        "experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id";

    /// <summary>The ordinal <c>r.revision</c> sits at in <see cref="HistorySql"/>, straight after <see cref="EventColumns"/>.</summary>
    private const int HistoryRevisionOrdinal = 34;

    /// <summary>The ordinal <c>r.deleted_at</c> sits at in <see cref="HistorySql"/>, straight after the revision.</summary>
    private const int HistoryDeletedAtOrdinal = 35;

    /// <summary>
    /// One statement, so the revision and the events come from one snapshot however the server is
    /// configured: a commit landing mid-read can never make the returned revision contradict the
    /// returned events. The outer join keeps a record with no events a <see cref="ExperienceStoreOutcome.Found"/>
    /// with an empty history -- that row has a null <c>event_id</c>.
    /// <para>
    /// Both the cursor and the bound are deliberately awkward here, and both are placed the only way
    /// that works. The cursor lives in the join's <c>ON</c> clause rather than in the <c>WHERE</c>:
    /// moved to the <c>WHERE</c>, a cursor past the last event would filter the single joined row away
    /// and turn an exhausted history into <see cref="ExperienceStoreOutcome.NotFound"/> -- losing the
    /// distinction between "this record has nothing more to show" and "no such record in this scope".
    /// The <c>LIMIT</c> is safe where it is for the mirror reason: the no-events row appears only
    /// when the join matched nothing at all, so any limit of at least one still keeps the row that
    /// carries <c>r.revision</c>.
    /// </para>
    /// </summary>
    private const string HistorySql =
        $"SELECT {JoinedEventColumns}, r.revision, r.{DeletedAtAlias}, r.payload_version FROM {Table} r " +
        $"LEFT JOIN {EventsTable} e ON e.experience_id = r.experience_id " +
        "AND (@start_after_revision IS NULL OR e.applied_revision > @start_after_revision) " +
        $"WHERE r.experience_id = @experience_id AND {RecordScopePredicate} " +
        "ORDER BY e.applied_revision LIMIT @limit";

    /// <summary>
    /// The whole supersession check, in one statement and one round trip: is the record in this exact
    /// scope, is the replacement, and does the replacement already sit on a chain that leads back to
    /// the record.
    /// <para>
    /// The recursive term walks <c>replacement_experience_id</c> forward from the proposed replacement:
    /// each step asks "and what replaced <em>that</em>". Reaching the record being superseded means the
    /// record already replaces the replacement, directly or transitively, so accepting this one would
    /// close a cycle. It is <c>UNION</c>, not <c>UNION ALL</c>, so the walk terminates even over a chain
    /// some earlier writer managed to close. The recursion is scope-qualified like everything else, so
    /// a foreign-scope event can neither extend the chain nor reveal that it exists.
    /// </para>
    /// </summary>
    private static readonly string SupersessionCheckSql = $"""
        WITH RECURSIVE replaced_by(experience_id) AS (
            SELECT @replacement_id::uuid
            UNION
            SELECT e.replacement_experience_id
            FROM {EventsTable} e
            JOIN replaced_by c ON e.experience_id = c.experience_id
            WHERE e.replacement_experience_id IS NOT NULL AND {EventScopePredicate}
        )
        SELECT
            (SELECT r.status FROM {Table} r
             WHERE r.experience_id = @experience_id AND {RecordScopePredicate} AND {RecordLivePredicate}),
            (SELECT r.status FROM {Table} r
             WHERE r.experience_id = @replacement_id AND {RecordScopePredicate} AND {RecordLivePredicate}),
            EXISTS (SELECT 1 FROM replaced_by WHERE experience_id = @experience_id)
        """;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    /// <summary>What the four-argument <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> means: a caller that keeps what it reads.</summary>
    private static readonly ExperienceReadOptions DeliveryRead = new();

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresGrantSupport _grants;

    private readonly ExperienceGrantAuditing? _auditing;

    private readonly TimeProvider _timeProvider;

    private readonly ExperienceEncryption? _encryption;

    /// <summary>Creates a store over a host-owned data source. The store never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="onGrantsUnavailable">
    /// Called at most once, when a read first finds <c>agent_experience.experience_grants</c> missing
    /// or unreadable and falls back to the exact-scope predicate. Optional: the fallback happens either
    /// way, and it only ever narrows what a read returns.
    /// </param>
    /// <param name="auditing">
    /// Where to record reads that a grant delivered, and what a failed recording does to the read.
    /// <see langword="null"/> -- the default -- switches auditing off entirely: no extra write, no
    /// extra failure mode, and a deployment behaves exactly as it did before this was added.
    /// </param>
    /// <param name="timeProvider">
    /// The clock this store stamps its own readings from: a lifecycle event's <c>recorded_at</c>, a
    /// tombstone's <c>deleted_at</c>, and the cutoff a retention sweep measures against
    /// <see cref="ExperienceRecord.CreatedAt"/>. Defaults to <see cref="TimeProvider.System"/>. It is
    /// this store's own clock and never a caller's: whether a sharing grant is still live is always the
    /// database's <c>clock_timestamp()</c>, which no host can wind.
    /// </param>
    /// <param name="encryption">
    /// Turns on crypto-shredding: payloads, task IDs, event reasons and evidence detail are written sealed
    /// under a per-record key, and <see cref="DeleteAsync(AuthorizationContext, Scope, Guid, long?, CancellationToken)"/>
    /// destroys that key. <see langword="null"/> -- the default -- is plaintext mode, exactly as before. Every
    /// component of one deployment must be given the same instance; see <see cref="ExperienceEncryption"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceRecordStore(
        NpgsqlDataSource dataSource,
        Action<ExperienceGrantSupportNotice>? onGrantsUnavailable = null,
        ExperienceGrantAuditing? auditing = null,
        TimeProvider? timeProvider = null,
        ExperienceEncryption? encryption = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _grants = new PostgresGrantSupport(onGrantsUnavailable);
        _auditing = auditing;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _encryption = ExperienceEncryption.Resolve(encryption);
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

        // Encrypted mode: the task ID and the whole payload are sealed together under the record's key, and
        // what the row stores is the sealed envelope, the placeholder task ID, and the derived search vector.
        // A reference whose key was destroyed is a record that was erased (or is mid-erasure): its ID is
        // spent, exactly as a tombstone's is, so it is reported the way a taken ID is.
        var storedTaskId = record.TaskId;
        var storedPayload = payload;
        var payloadVersion = ExperiencePayload.CurrentVersion;
        if (_encryption is not null)
        {
            using var key = await _encryption.ForWriteAsync(record.ExperienceId, record.Scope, cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                return new(ExperienceStoreOutcome.Conflict, NoErrors);
            }

            storedPayload = SealedText.PayloadEnvelope(
                key.Seal(SealedText.PayloadColumn, Guid.Empty, SealedText.SealedRecordPlaintext(record.TaskId, payload)));
            storedTaskId = SealedText.SealedTaskId;
            payloadVersion = SealedText.SealedPayloadVersion;
        }

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            await using var command = session.CreateCommand(_encryption is null ? InsertSql : InsertSealedSql);
            var parameters = command.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            parameters.Add(new NpgsqlParameter<Guid>("source_run_id", record.SourceRunId));
            AddScopeParameters(parameters, record.Scope);
            parameters.Add(new NpgsqlParameter<string>("task_id", storedTaskId));
            parameters.Add(new NpgsqlParameter<string>("status", record.Status.ToString()));
            parameters.Add(new NpgsqlParameter<double>("reuse_confidence", record.ReuseConfidence));
            parameters.Add(new NpgsqlParameter<int>("supporting_validations", record.SupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("contradictions", record.Contradictions));
            parameters.Add(new NpgsqlParameter<long>("revision", record.Revision));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", ToStoredTimestamp(record.CreatedAt)));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("updated_at", ToStoredTimestamp(record.UpdatedAt)));
            parameters.Add(new NpgsqlParameter<int>("payload_version", payloadVersion));
            parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = storedPayload });
            if (_encryption is not null)
            {
                AddSealedSearchParameters(parameters, record.TaskId, record.TaskSummary, record.Reflection?.Lesson);

                // The database cannot read a sealed payload, so the store says what 0021's trigger would have derived
                // from a plaintext one, by the rule every authorship decision shares (ReflectionAuthorshipRule): a
                // reflection whose authorship is anything but Deterministic, or whose producer names the library's own
                // model-backed reflector, is model-authored; no reflection is not.
                parameters.Add(new NpgsqlParameter<bool>(
                    "reflection_model_authored",
                    ReflectionAuthorshipRule.IsModelAuthored(record.Reflection)));
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
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
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        GetAsync(authorization, scope, experienceId, DeliveryRead, cancellationToken);

    /// <inheritdoc />
    public async Task<ExperienceRecordGetResult> GetAsync(
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
            return new(ExperienceStoreOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        ExperienceRecordGetResult result;
        try
        {
            try
            {
                result = await ReadOneAsync(_grants.Available ? GetSql : GetExactSql, authorization, scope, experienceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (_grants.ShouldFallBack(ex, "get", cancellationToken))
            {
                // No grant table, or no permission to read it. Falling back narrows the read to the
                // exact scope; it can never return a record this scope did not already own.
                result = await ReadOneAsync(GetExactSql, authorization, scope, experienceId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "get", cancellationToken);
        }

        // Deliberately outside the read's own translation: an audit failure is the host's policy to
        // decide, not a storage failure to raise, and it must never be reported as a failed read.
        return _auditing is null || options.Purpose == ExperienceReadPurpose.ScopeCheck
            ? result
            : await RecordGrantAccessAsync(authorization, scope, options, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExperienceRecordGetResult> ReadOneAsync(
        string sql,
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
        ExperienceRecordGetResult result;
        await using (var command = session.CreateCommand(sql))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            AddScopeParameters(command.Parameters, scope);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? await ResultFromRowAsync(reader, cancellationToken).ConfigureAwait(false)
                : new(ExperienceStoreOutcome.NotFound, null, NoErrors);
        }

        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// What one row of <see cref="GetSql"/>, <see cref="GetExactSql"/>, <see cref="GetManySql"/> or
    /// <see cref="GetManyExactSql"/> means. The single and the batched read both decide through here, so
    /// the tombstone rule and the grant columns are read the same way for both.
    /// <para>
    /// In encrypted mode a sealed row whose key was destroyed is answered exactly as a tombstone is: the
    /// erasure's key destruction has happened even if its database side has not (see
    /// <see cref="PurgeAsync"/>), and a record is erased once its key is gone.
    /// </para>
    /// </summary>
    private async ValueTask<ExperienceRecordGetResult> ResultFromRowAsync(DbDataReader reader, CancellationToken cancellationToken) =>
        ResultFromRow(reader, ReadDeleted(reader) ? null : await ReadRecordAsync(reader, _encryption, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <see cref="ResultFromRowAsync"/> for a row whose record has already been decoded: <paramref name="record"/>
    /// is <see langword="null"/> for a tombstone or a sealed row whose key was destroyed.
    /// </summary>
    private static ExperienceRecordGetResult ResultFromRow(DbDataReader reader, ExperienceRecord? record)
    {
        var sharedByGrant = ReadSharedByGrant(reader);

        if (record is null)
        {
            // An erased record. The owner is told so -- the ID is spent and no retry will make it
            // resolve -- but a reader that only reached the row through a grant is told nothing it did
            // not already have: the erasure purges every grant over the record, so a grant that still
            // names a tombstone was written outside this library, and answering it with anything but
            // NotFound would leak the tombstone's existence across a scope boundary.
            return new(
                sharedByGrant ? ExperienceStoreOutcome.NotFound : ExperienceStoreOutcome.Deleted,
                null,
                NoErrors);
        }

        return new(
            ExperienceStoreOutcome.Found,
            record,
            NoErrors,
            sharedByGrant,
            ReadPermittingGrant(reader),
            ReadPermittingDisclosure(reader),
            ReadPermittingApproachArguments(reader));
    }

    /// <summary>
    /// Appends the access row for a record a grant just delivered, and decides what a failed append
    /// does to the read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a delivery is recorded.</b> A read that found nothing, and an owner reading its own
    /// record, both write nothing: no grant permitted either, so there is no access to attribute to
    /// one. This is also the only reason every stored row can carry a non-null grant.
    /// </para>
    /// <para>
    /// <b>The failure path is the host's policy, not a storage error.</b> Under
    /// <see cref="ExperienceGrantAuditingMode.BestEffort"/> the record is still returned and the
    /// failure is reported; under <see cref="ExperienceGrantAuditingMode.Required"/> the read returns
    /// <see cref="ExperienceStoreOutcome.NotFound"/> -- the same answer as a record no grant permitted,
    /// so failing closed tells a caller nothing it would not otherwise have -- and the failure is
    /// reported just the same. Caller cancellation is never an audit failure and propagates unwrapped.
    /// </para>
    /// <para>
    /// A read that came back shared but unnamed cannot happen through this store's own SQL, because the
    /// same lateral join decides both. If it ever did, the row is attempted anyway with an empty grant
    /// ID, the database's own <c>experience_grant_access_grant_id_not_empty</c> refuses it, and the
    /// configured mode decides the read -- which is the safe direction, rather than quietly delivering
    /// a shared record with no trail.
    /// </para>
    /// </remarks>
    private async Task<ExperienceRecordGetResult> RecordGrantAccessAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceReadOptions options,
        ExperienceRecordGetResult result,
        CancellationToken cancellationToken)
    {
        var auditing = _auditing!;

        if (result is not { Outcome: ExperienceStoreOutcome.Found, Record: { } record, SharedByGrant: true })
        {
            return result;
        }

        var access = GrantAuditing.Access(
            auditing, authorization, scope, options.CorrelationId, record, result.PermittingGrantId, result.GrantDisclosure);

        return await GrantAuditing.RecordAsync(auditing, authorization, [access], cancellationToken).ConfigureAwait(false)
            ? result
            : new(ExperienceStoreOutcome.NotFound, null, NoErrors);
    }

    /// <summary>
    /// Reads several records by ID in <em>one</em> statement, each answered exactly as
    /// <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/>
    /// answers it: the same exact-scope-or-active-grant predicate (the SQL shares its select, join and
    /// predicate text with the single read), the same tombstone rule, the same permitting grant and
    /// disclosure, and the same grant fallback. Grant-delivered positions are audited in one append of
    /// one row each, under the same mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Round trips.</b> One statement for the reads, and -- only when auditing is wired and at least
    /// one position was delivered through a grant -- one statement for the access rows. The per-record
    /// path it replaces was one statement per record, plus one per grant-delivered record.
    /// </para>
    /// <para>
    /// <b>Required auditing fails closed for the whole batch's grant deliveries.</b> The rows go in one
    /// statement, so they land together or not at all; when they cannot be written under
    /// <see cref="ExperienceGrantAuditingMode.Required"/>, every grant-delivered position becomes
    /// <see cref="ExperienceStoreOutcome.NotFound"/> and every owner-scope position is returned as
    /// read, which is what a failing ledger does to each single read.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    public async Task<ExperienceRecordGetManyResult> GetManyAsync(
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
            return new(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // An empty GUID is answered per position, with exactly the errors a single read of it gets, and
        // is never sent to the database -- the single read never sends it either.
        var wanted = experienceIds.Where(id => id != Guid.Empty).Distinct().ToArray();

        Dictionary<Guid, ExperienceRecordGetResult> found;
        if (wanted.Length == 0)
        {
            found = [];
        }
        else
        {
            try
            {
                try
                {
                    found = await ReadManyAsync(_grants.Available ? GetManySql : GetManyExactSql, authorization, scope, wanted, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (_grants.ShouldFallBack(ex, "get", cancellationToken))
                {
                    // Exactly the single read's fallback: narrower, never wider.
                    found = await ReadManyAsync(GetManyExactSql, authorization, scope, wanted, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
            {
                throw Translate(ex, "get", cancellationToken);
            }
        }

        var notFound = new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, NoErrors);
        var results = new ExperienceRecordGetResult[experienceIds.Count];
        for (var i = 0; i < results.Length; i++)
        {
            var id = experienceIds[i];
            results[i] = id == Guid.Empty
                ? new(ExperienceStoreOutcome.Invalid, null, ExperienceRecordValidator.ValidateGet(scope, id))
                : found.TryGetValue(id, out var result) ? result : notFound;
        }

        if (_auditing is not null && options.Purpose != ExperienceReadPurpose.ScopeCheck)
        {
            await RecordGrantAccessAsync(authorization, scope, options, results, cancellationToken).ConfigureAwait(false);
        }

        return new(ExperienceStoreOutcome.Found, results, NoErrors);
    }

    private async Task<Dictionary<Guid, ExperienceRecordGetResult>> ReadManyAsync(
        string sql,
        AuthorizationContext authorization,
        Scope scope,
        Guid[] experienceIds,
        CancellationToken cancellationToken)
    {
        // The rows are read into memory, the reader closed, the read-only transaction committed and the connection
        // returned to the pool before any key is fetched: nothing after decoding writes in this transaction (the
        // access rows are appended by GetManyAsync afterwards, on a connection of their own), so a slow key store
        // holds no connection. The key store is then asked once, for every sealed live row together.
        List<SnapshotRow> rows;
        await using (var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false))
        {
            await using (var command = session.CreateCommand(sql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", experienceIds));
                AddScopeParameters(command.Parameters, scope);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                rows = await SnapshotRow.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // A tombstone is answered without its key, exactly as the single read answers it.
        var live = rows.Where(row => !ReadDeleted(row)).ToArray();
        var records = await ReadRecordsAsync(live, _encryption, cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<Guid, ExperienceRecordGetResult>(experienceIds.Length);
        var next = 0;
        foreach (var row in rows)
        {
            var record = next < live.Length && ReferenceEquals(live[next], row) ? records[next++] : null;

            // experience_id is the primary key, so a row per ID at most; the lateral join is LIMIT 1.
            found[row.GetGuid(0)] = ResultFromRow(row, record);
        }

        return found;
    }

    /// <summary>
    /// The batched form of the single read's access-row append: one row per position a grant delivered,
    /// in request order, written in one statement, with the single read's failure policy applied to
    /// every one of those positions. Positions are rewritten in place.
    /// </summary>
    private async Task RecordGrantAccessAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceReadOptions options,
        ExperienceRecordGetResult[] results,
        CancellationToken cancellationToken)
    {
        var auditing = _auditing!;
        var accesses = new List<ExperienceGrantAccess>();
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { Outcome: ExperienceStoreOutcome.Found, Record: { } record, SharedByGrant: true } delivered)
            {
                accesses.Add(GrantAuditing.Access(
                    auditing, authorization, scope, options.CorrelationId, record, delivered.PermittingGrantId, delivered.GrantDisclosure));
            }
        }

        if (await GrantAuditing.RecordAsync(auditing, authorization, accesses, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var refused = new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, NoErrors);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { Outcome: ExperienceStoreOutcome.Found, Record: not null, SharedByGrant: true })
            {
                results[i] = refused;
            }
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

            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            await using var command = session.CreateCommand(sql);
            AddScopeParameters(command.Parameters, query.Scope);
            if (query.Statuses is not null)
            {
                var statuses = query.Statuses.Distinct().Select(s => s.ToString()).ToArray();
                command.Parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
            }

            command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            var records = new List<ExperienceRecord>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // A sealed record whose key was destroyed is erased, and absent here like a tombstone.
                    if (await ReadRecordAsync(reader, _encryption, cancellationToken).ConfigureAwait(false) is { } record)
                    {
                        records.Add(record);
                    }
                }
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Found, records, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordHistoryResult> GetHistoryAsync(
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
            return new(ExperienceStoreOutcome.Invalid, 0, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            await using var command = session.CreateCommand(HistorySql);
            var parameters = command.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", query.ExperienceId));
            AddScopeParameters(parameters, query.Scope);
            parameters.Add(new NpgsqlParameter("start_after_revision", NpgsqlDbType.Bigint)
            {
                Value = query.StartAfterRevision is { } cursor ? cursor : DBNull.Value,
            });
            parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // No record in this scope: indistinguishable from one that exists elsewhere.
                return new(ExperienceStoreOutcome.NotFound, 0, [], NoErrors);
            }

            if (!reader.IsDBNull(HistoryDeletedAtOrdinal))
            {
                // An erased record has no history left to page: its events were removed with its
                // payload. Reported as Deleted rather than as an empty page, which would say the record
                // is alive and has nothing to show.
                return new(ExperienceStoreOutcome.Deleted, 0, [], NoErrors);
            }

            var revision = ReadRevision(reader, HistoryRevisionOrdinal);

            // Encrypted mode: the events' reasons and details are sealed under the record's key. A destroyed
            // key is an erased record, reported exactly as a tombstone is. A key the store never held is fine
            // for a record written before the upgrade -- its events are plaintext -- and a sealed value met
            // without one fails loudly in the decoder rather than being reported as erased.
            RecordKey? key = null;
            if (_encryption is not null)
            {
                var lookup = await _encryption.LookupAsync(query.ExperienceId, query.Scope, cancellationToken).ConfigureAwait(false);
                if (lookup.Destroyed && reader.GetInt32(HistoryDeletedAtOrdinal + 1) == SealedText.SealedPayloadVersion)
                {
                    // A sealed record whose key is gone reads as erased everywhere; a plaintext one (written before
                    // the upgrade) is answered by its row, exactly as GetAsync answers it.
                    return new(ExperienceStoreOutcome.Deleted, 0, [], NoErrors);
                }

                key = lookup.Key;
            }

            using var ownedKey = key;
            var events = new List<StoredLifecycleEvent>();
            if (!reader.IsDBNull(0))
            {
                // A null event_id is the outer join's single "record with no events" row -- which is
                // also what an exhausted cursor produces, and deliberately so: it keeps the record
                // Found with nothing left to show rather than making it look missing.
                do
                {
                    events.Add(ReadEvent(reader, key));
                }
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            return new(
                ExperienceStoreOutcome.Found,
                revision,
                events,
                NoErrors,
                events.Count > 0 ? events[^1].AppliedRevision : null);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "history", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
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
            return new(ExperienceSupersessionOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceSupersessionOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            var check = await ReadSupersessionAsync(session.Connection, session.Transaction, scope, experienceId, replacementExperienceId, cancellationToken)
                .ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return check;
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "supersession check", cancellationToken);
        }
    }

    /// <summary>
    /// Runs the supersession statement, optionally inside a transaction, and turns its three values into
    /// an outcome. Shared by the read-only port operation and the in-transaction gate, so the two can
    /// never disagree about what a cycle is.
    /// </summary>
    private static async Task<ExperienceSupersessionCheckResult> ReadSupersessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        Guid replacementId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SupersessionCheckSql, connection, transaction);
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        parameters.Add(new NpgsqlParameter<Guid>("replacement_id", replacementId));
        AddScopeParameters(parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The statement always produces exactly one row; treat the impossible case as "no record".
            return new(ExperienceSupersessionOutcome.RecordNotFound, null, NoErrors);
        }

        if (reader.IsDBNull(0))
        {
            return new(ExperienceSupersessionOutcome.RecordNotFound, null, NoErrors);
        }

        if (reader.IsDBNull(1))
        {
            // Missing, or in another scope: identical either way, so nothing about it is revealed.
            return new(ExperienceSupersessionOutcome.ReplacementNotFound, null, NoErrors);
        }

        var replacementStatus = ReadStoredStatus(reader, 1);

        // The cycle is reported before the status, so a replacement that is both eligible and on a
        // closing chain is still refused for the reason that actually matters.
        return reader.GetBoolean(2)
            ? new(ExperienceSupersessionOutcome.Cycle, replacementStatus, NoErrors)
            : new(ExperienceSupersessionOutcome.Allowed, replacementStatus, NoErrors);
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
}
