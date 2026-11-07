using AgentExperience.Abstractions;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>Injection tracking and both holdouts: rows of identifiers and instants beside the records, never content.</summary>
public sealed partial class SqliteExperienceRecordStore
{
    /// <inheritdoc />
    public bool TracksInjections => true;

    /// <inheritdoc />
    /// <remarks>
    /// The instant is clamped to this store's clock (its <see cref="TimeProvider"/>), and the count saturates at
    /// <see cref="long.MaxValue"/>. Only a live record in exactly <paramref name="scope"/> is counted.
    /// </remarks>
    public Task<ExperienceInjectionRecordResult> RecordInjectionAsync(
        AuthorizationContext authorization,
        Scope scope,
        IReadOnlyList<Guid> experienceIds,
        DateTimeOffset injectedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(experienceIds);

        var errors = ExperienceRecordValidator.ValidateRecordInjection(scope, experienceIds, injectedAt);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceInjectionRecordResult>(cancellationToken);
        }

        var now = Now;
        var at = Micro(injectedAt);
        if (at > now)
        {
            at = now;
        }

        var ids = experienceIds.Distinct().ToArray();
        return Database.WriteAsync(
            "The SQLite injection could not be recorded.",
            async (connection, transaction) =>
            {
                foreach (var id in ids)
                {
                    // Only a live record in exactly this scope; anything else is skipped, revealing nothing.
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO experience_injections (experience_id, last_injected_at, injection_count) "
                        + $"SELECT experience_id, $at, 1 FROM experience_records WHERE experience_id = $id AND {ScopePredicate} AND deleted_at IS NULL "
                        + "ON CONFLICT (experience_id) DO UPDATE SET last_injected_at = max(last_injected_at, excluded.last_injected_at), "
                        + "injection_count = CASE WHEN injection_count = 9223372036854775807 THEN injection_count ELSE injection_count + 1 END",
                        cancellationToken,
                        [("$id", Id(id)), ("$at", Ticks(at)), .. ScopeParameters(scope)]).ConfigureAwait(false);
                }

                return (new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Committed, NoErrors), true);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordQueryResult> QueryWithInjectionsAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateQuery(query);
        if (errors.Count > 0)
        {
            return new ExperienceRecordQueryResult(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new ExperienceRecordQueryResult(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Database.RunAsync(
            "The SQLite experience records could not be queried.",
            async connection =>
            {
                // One read transaction: the page and its injection summaries describe one moment.
                using var transaction = connection.BeginTransaction(deferred: true);
                var records = await QueryRecordsAsync(connection, transaction, query, cancellationToken).ConfigureAwait(false);
                var injections = new Dictionary<Guid, ExperienceInjectionSummary>();
                foreach (var record in records)
                {
                    using var command = Command(
                        connection,
                        transaction,
                        "SELECT last_injected_at, injection_count FROM experience_injections WHERE experience_id = $id",
                        ("$id", Id(record.ExperienceId)));
                    using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        injections[record.ExperienceId] = new ExperienceInjectionSummary(Instant(reader.GetInt64(0)), reader.GetInt64(1));
                    }
                }

                return new ExperienceRecordQueryResult(ExperienceStoreOutcome.Found, records, NoErrors) { Injections = injections };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TracksHoldout => true;

    /// <inheritdoc />
    /// <remarks>
    /// The instant is clamped to this store's clock. A row that already exists for a run and record is kept as it is. Only
    /// a live record in exactly <paramref name="scope"/> gets a row, and erasing a record removes the rows that name it and
    /// the rows of the run it was finalized from.
    /// </remarks>
    public Task<ExperienceHoldoutRecordResult> RecordHoldoutAssignmentAsync(
        AuthorizationContext authorization,
        Scope scope,
        HoldoutAssignment assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateHoldoutAssignment(scope, assignment);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceHoldoutRecordResult>(cancellationToken);
        }

        var now = Now;
        var at = Micro(assignment.AssignedAt);
        if (at > now)
        {
            at = now;
        }

        return Database.WriteAsync(
            "The SQLite holdout assignment could not be recorded.",
            async (connection, transaction) =>
            {
                if (await RunErasedAsync(connection, transaction, scope, assignment.RunId, cancellationToken).ConfigureAwait(false))
                {
                    return (new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, NoErrors), false);
                }

                foreach (var exposure in assignment.Records)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO experience_holdout_assignments (run_id, experience_id, revision, arm, " + ScopeColumns + ", assigned_at) "
                        + $"SELECT $run, experience_id, $revision, $arm, {ScopeValues}, $at FROM experience_records "
                        + $"WHERE experience_id = $id AND {ScopePredicate} AND deleted_at IS NULL "
                        + "ON CONFLICT (run_id, experience_id) DO NOTHING",
                        cancellationToken,
                        [
                            ("$run", Id(assignment.RunId)),
                            ("$id", Id(exposure.ExperienceId)),
                            ("$revision", exposure.Revision),
                            ("$arm", assignment.Arm.ToString()),
                            ("$at", Ticks(at)),
                            .. ScopeParameters(scope),
                        ]).ConfigureAwait(false);
                }

                return (new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, NoErrors), true);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The rows in exactly the query's scope, ordered by run and then record, each joined to the live record in exactly the
    /// row's scope whose <see cref="ExperienceRecord.SourceRunId"/> is its run (the earliest created, then the lowest ID).
    /// </remarks>
    public Task<HoldoutOutcomeQueryResult> QueryHoldoutOutcomesAsync(
        AuthorizationContext authorization,
        HoldoutOutcomeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateHoldoutOutcomeQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new HoldoutOutcomeQueryResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new HoldoutOutcomeQueryResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<HoldoutOutcomeQueryResult>(cancellationToken);
        }

        return Database.RunAsync(
            "The SQLite holdout outcomes could not be read.",
            async connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var page = new List<(Guid RunId, Guid ExperienceId, HoldoutArm Arm, DateTimeOffset AssignedAt)>();
                using (var command = Command(
                    connection,
                    transaction,
                    $"SELECT run_id, experience_id, arm, assigned_at FROM experience_holdout_assignments WHERE {ScopePredicate} "
                    + "AND ($after_run IS NULL OR run_id > $after_run OR (run_id = $after_run AND experience_id > $after_id)) "
                    + "ORDER BY run_id, experience_id LIMIT $limit",
                    [
                        .. ScopeParameters(query.Scope),
                        ("$after_run", query.After is { } after ? Id(after.RunId) : null),
                        ("$after_id", query.After is { } afterId ? Id(afterId.ExperienceId) : null),
                        ("$limit", query.Limit),
                    ]))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        page.Add((ReadId(reader, 0), ReadId(reader, 1), Enum.Parse<HoldoutArm>(reader.GetString(2)), Instant(reader.GetInt64(3))));
                    }
                }

                var runs = new Dictionary<Guid, ExperienceRecord?>();
                var lessons = new Dictionary<Guid, string?>();
                var rows = new List<HoldoutOutcomeRow>(page.Count);
                foreach (var row in page)
                {
                    if (!runs.TryGetValue(row.RunId, out var runRecord))
                    {
                        runRecord = await RunRecordAsync(connection, transaction, query.Scope, row.RunId, cancellationToken).ConfigureAwait(false);
                        runs[row.RunId] = runRecord;
                    }

                    if (!lessons.TryGetValue(row.ExperienceId, out var lessonModel))
                    {
                        var lesson = await ReadAsync(connection, transaction, query.Scope, row.ExperienceId, cancellationToken).ConfigureAwait(false);
                        lessonModel = ModelOf(lesson.Record);
                        lessons[row.ExperienceId] = lessonModel;
                    }

                    rows.Add(new HoldoutOutcomeRow(row.RunId, row.Arm, row.ExperienceId, row.AssignedAt, runRecord?.Outcome?.Status)
                    {
                        RunModel = ModelOf(runRecord),
                        LessonModel = lessonModel,
                    });
                }

                return new HoldoutOutcomeQueryResult(ExperienceStoreOutcome.Found, rows, NoErrors);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public bool TracksProviderHoldout => true;

    /// <inheritdoc />
    /// <remarks>
    /// The instant is clamped to this store's clock. A row that already exists for a run and key is kept as it is. A write
    /// for a run whose own records in this scope are all erased is refused (it writes nothing and still reports
    /// <see cref="ExperienceStoreOutcome.Committed"/>), as the PostgreSQL store refuses it.
    /// </remarks>
    public Task<ExperienceHoldoutRecordResult> RecordProviderHoldoutAssignmentAsync(
        AuthorizationContext authorization,
        Scope scope,
        ProviderHoldoutAssignment assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateProviderHoldoutAssignment(scope, assignment);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceHoldoutRecordResult>(cancellationToken);
        }

        var now = Now;
        var at = Micro(assignment.AssignedAt);
        if (at > now)
        {
            at = now;
        }

        return Database.WriteAsync(
            "The SQLite provider holdout assignment could not be recorded.",
            async (connection, transaction) =>
            {
                if (await RunErasedAsync(connection, transaction, scope, assignment.RunId, cancellationToken).ConfigureAwait(false))
                {
                    return (new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, NoErrors), false);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO experience_provider_holdout_assignments (run_id, provider_key, arm, " + ScopeColumns + ", assigned_at) "
                    + $"VALUES ($run, $key, $arm, {ScopeValues}, $at) ON CONFLICT (tenant_id, run_id, provider_key) DO NOTHING",
                    cancellationToken,
                    [
                        ("$run", Id(assignment.RunId)),
                        ("$key", assignment.ProviderKey),
                        ("$arm", assignment.Arm.ToString()),
                        ("$at", Ticks(at)),
                        .. ScopeParameters(scope),
                    ]).ConfigureAwait(false);

                return (new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, NoErrors), true);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProviderHoldoutOutcomeQueryResult> QueryProviderHoldoutOutcomesAsync(
        AuthorizationContext authorization,
        ProviderHoldoutOutcomeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateProviderHoldoutOutcomeQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ProviderHoldoutOutcomeQueryResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ProviderHoldoutOutcomeQueryResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ProviderHoldoutOutcomeQueryResult>(cancellationToken);
        }

        return Database.RunAsync(
            "The SQLite provider holdout outcomes could not be read.",
            async connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var page = new List<(Guid RunId, string Key, HoldoutArm Arm, DateTimeOffset AssignedAt)>();
                using (var command = Command(
                    connection,
                    transaction,
                    $"SELECT run_id, provider_key, arm, assigned_at FROM experience_provider_holdout_assignments WHERE {ScopePredicate} "
                    + "AND provider_key = $key AND ($after IS NULL OR run_id > $after) ORDER BY run_id LIMIT $limit",
                    [
                        .. ScopeParameters(query.Scope),
                        ("$key", query.ProviderKey),
                        ("$after", query.AfterRunId is { } after ? Id(after) : null),
                        ("$limit", query.Limit),
                    ]))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        page.Add((ReadId(reader, 0), reader.GetString(1), Enum.Parse<HoldoutArm>(reader.GetString(2)), Instant(reader.GetInt64(3))));
                    }
                }

                var rows = new List<ProviderHoldoutOutcomeRow>(page.Count);
                foreach (var row in page)
                {
                    var runRecord = await RunRecordAsync(connection, transaction, query.Scope, row.RunId, cancellationToken).ConfigureAwait(false);
                    rows.Add(new ProviderHoldoutOutcomeRow(row.RunId, row.Key, row.Arm, row.AssignedAt, runRecord?.Outcome?.Status));
                }

                return new ProviderHoldoutOutcomeQueryResult(ExperienceStoreOutcome.Found, rows, NoErrors);
            },
            cancellationToken);
    }

    /// <summary>
    /// The live record in exactly <paramref name="scope"/> whose source run is <paramref name="runId"/>: the earliest
    /// created, then the lowest ID, should more than one name it.
    /// </summary>
    private static async Task<ExperienceRecord?> RunRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Scope scope,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using var command = Command(
            connection,
            transaction,
            $"SELECT {RecordColumns} FROM experience_records WHERE source_run_id = $run AND {ScopePredicate} AND deleted_at IS NULL "
            + "ORDER BY created_at, experience_id LIMIT 1",
            [("$run", Id(runId)), .. ScopeParameters(scope)]);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <summary>Whether the run's own records in exactly <paramref name="scope"/> exist and are all erased.</summary>
    private static async Task<bool> RunErasedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Scope scope,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var counts = await ScalarAsync(
            connection,
            transaction,
            $"SELECT CASE WHEN count(*) > 0 AND count(*) = count(deleted_at) THEN 1 END FROM experience_records "
            + $"WHERE source_run_id = $run AND {ScopePredicate}",
            cancellationToken,
            [("$run", Id(runId)), .. ScopeParameters(scope)]).ConfigureAwait(false);
        return counts is not null;
    }

    /// <summary>The model a record's environment names (<see cref="HoldoutOutcomeRow.ModelMetadataKey"/>), or <see langword="null"/> when none or blank.</summary>
    private static string? ModelOf(ExperienceRecord? record) =>
        record?.Environment?.Metadata is { } metadata
        && metadata.TryGetValue(HoldoutOutcomeRow.ModelMetadataKey, out var model)
        && !string.IsNullOrWhiteSpace(model)
            ? model
            : null;
}
