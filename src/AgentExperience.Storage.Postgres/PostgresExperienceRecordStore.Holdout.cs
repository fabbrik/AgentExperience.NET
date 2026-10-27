using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The injection holdout's assignments. <c>0027</c>'s <c>agent_experience.experience_holdout_assignments</c>
/// holds one row per captured run and record its block named, with the arm; <see cref="RecordHoldoutAssignmentAsync"/>
/// inserts them in one statement per assigned block, and <see cref="QueryHoldoutOutcomesAsync"/> reads them back, a page at
/// a time, with each run's own verified outcome.
/// </summary>
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>The holdout assignment table <c>0027</c> creates.</summary>
    internal const string HoldoutAssignmentsTable = "agent_experience.experience_holdout_assignments";

    /// <summary>
    /// The one write: a row per named record that is live in exactly the caller's scope, its scope copied from the record
    /// so a row can never name another scope, at the revision the block rendered. The instant is clamped to the database's
    /// clock. A row that already exists for the run and record is left as it is. The records are locked <c>FOR KEY
    /// SHARE</c> in ID order, as <see cref="RecordInjectionSql"/> locks them: an erasure racing the write either waits for
    /// it and then deletes its rows through <c>0027</c>'s trigger, or runs first and leaves a tombstone this statement no
    /// longer selects.
    /// </summary>
    internal const string RecordHoldoutAssignmentSql =
        $"INSERT INTO {HoldoutAssignmentsTable} (run_id, experience_id, revision, arm, tenant_id, application_id, project_id, " +
        "team_id, agent_id, user_id, assigned_at)\n" +
        "SELECT @run_id, r.experience_id, n.revision, @arm, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, " +
        "r.user_id, LEAST(@assigned_at, pg_catalog.clock_timestamp())\n" +
        $"FROM {Table} r\n" +
        "JOIN ROWS FROM (pg_catalog.unnest(@experience_ids), pg_catalog.unnest(@revisions)) AS n(experience_id, revision) " +
        "ON n.experience_id = r.experience_id\n" +
        $"WHERE {RecordScopePredicate} AND {RecordLivePredicate}\n" +
        $"ORDER BY r.experience_id {RecordKeyShareLock}\n" +
        "ON CONFLICT (run_id, experience_id) DO NOTHING";

    /// <inheritdoc />
    public bool TracksHoldout => true;

    /// <inheritdoc />
    /// <remarks>
    /// One statement, whatever the number of records: an insert into <c>agent_experience.experience_holdout_assignments</c>
    /// of the named records that are live in exactly <paramref name="scope"/>. In encrypted mode a record whose key is
    /// destroyed counts as erased and is skipped, at the cost of one batched key lookup. Until the application role has
    /// been granted the table (re-run
    /// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(NpgsqlDataSource, ExperienceApplicationRoleOptions, CancellationToken)"/>
    /// after migrating to <c>0027</c>) it throws <see cref="ExperienceStoreException"/>, which the injection adapter
    /// reports without affecting the injection.
    /// </remarks>
    public async Task<ExperienceHoldoutRecordResult> RecordHoldoutAssignmentAsync(
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
            return new(ExperienceStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A record named twice counts once, at the revision it was first named with.
        var records = assignment.Records.DistinctBy(exposure => exposure.ExperienceId).ToArray();
        var ids = records.Select(exposure => exposure.ExperienceId).ToArray();

        try
        {
            if (_encryption is not null && ids.Length > 0)
            {
                var live = (await WithLiveKeysAsync(ids, scope, cancellationToken).ConfigureAwait(false)).ToHashSet();
                records = [.. records.Where(exposure => live.Contains(exposure.ExperienceId))];
                ids = [.. records.Select(exposure => exposure.ExperienceId)];
            }

            if (ids.Length == 0)
            {
                return new(ExperienceStoreOutcome.Committed, NoErrors);
            }

            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            await using (var command = session.CreateCommand(RecordHoldoutAssignmentSql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("run_id", assignment.RunId));
                command.Parameters.Add(new NpgsqlParameter<string>("arm", assignment.Arm.ToString()));
                command.Parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", ids));
                command.Parameters.Add(new NpgsqlParameter<long[]>("revisions", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                {
                    TypedValue = [.. records.Select(exposure => exposure.Revision)],
                });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("assigned_at", ToStoredTimestamp(assignment.AssignedAt)));
                AddScopeParameters(command.Parameters, scope);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Committed, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "record holdout assignment", cancellationToken);
        }
    }

    /// <summary>
    /// The read's first part: a page of the caller's exact-scope assignment rows in (run, record) order, then, for each
    /// run on the page, the run's own live record in exactly the same scope (the earliest created, then the lowest ID),
    /// found with one semi-join rather than one probe per row. A record in another scope that names the same run is never
    /// the run's record. The record columns keep ordinals 0-17, null when the run has no record; the row is 18-21.
    /// Row-level security bounds both tables to the caller's bounds. The keyset predicate, when there is one, goes
    /// between the two parts.
    /// </summary>
    internal const string QueryHoldoutOutcomesPrefix =
        "WITH page AS (SELECT run_id, arm, experience_id, assigned_at\n" +
        $"FROM {HoldoutAssignmentsTable}\n" +
        $"WHERE {ScopePredicate}";

    /// <summary>Resumes after the previous page's last row.</summary>
    internal const string QueryHoldoutOutcomesAfterPredicate = " AND (run_id, experience_id) > (@after_run_id, @after_experience_id)";

    internal const string QueryHoldoutOutcomesSuffix =
        "\nORDER BY run_id, experience_id LIMIT @limit),\n" +
        $"run_record AS (SELECT DISTINCT ON (source_run_id) {SelectColumns}\n" +
        $"FROM {Table}\n" +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND source_run_id IN (SELECT run_id FROM page)\n" +
        "ORDER BY source_run_id, created_at, experience_id)\n" +
        "SELECT run_record.*, page.run_id, page.arm, page.experience_id, page.assigned_at\n" +
        "FROM page LEFT JOIN run_record ON run_record.source_run_id = page.run_id\n" +
        "ORDER BY page.run_id, page.experience_id";

    /// <inheritdoc />
    /// <remarks>
    /// One statement per page (see <see cref="QueryHoldoutOutcomesPrefix"/>). The rows are read into memory and the
    /// connection released before any key is fetched; then every sealed run record on the page is opened in one key-store
    /// call, through the store's own read path. One sealed under a destroyed key reads as no outcome. Until the
    /// application role has been granted <c>0027</c>'s table it throws <see cref="ExperienceStoreException"/>.
    /// </remarks>
    public async Task<HoldoutOutcomeQueryResult> QueryHoldoutOutcomesAsync(
        AuthorizationContext authorization,
        HoldoutOutcomeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateHoldoutOutcomeQuery(query);
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
            var sql = QueryHoldoutOutcomesPrefix
                + (query.After is null ? string.Empty : QueryHoldoutOutcomesAfterPredicate)
                + QueryHoldoutOutcomesSuffix;

            List<SnapshotRow> snapshot;
            await using (var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false))
            {
                await using (var command = session.CreateCommand(sql))
                {
                    AddScopeParameters(command.Parameters, query.Scope);
                    command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));
                    if (query.After is { } after)
                    {
                        command.Parameters.Add(new NpgsqlParameter<Guid>("after_run_id", after.RunId));
                        command.Parameters.Add(new NpgsqlParameter<Guid>("after_experience_id", after.ExperienceId));
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    snapshot = await SnapshotRow.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
                }

                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // One record per run (every row of a run carries the same one), decoded together: sealed ones in one batch.
            var runRows = snapshot.Where(row => !row.IsDBNull(0)).DistinctBy(row => row.GetGuid(18)).ToArray();
            var records = await ReadRecordsAsync(runRows, _encryption, cancellationToken).ConfigureAwait(false);
            var outcomes = new Dictionary<Guid, TaskVerificationStatus?>(runRows.Length);
            for (var i = 0; i < runRows.Length; i++)
            {
                outcomes[runRows[i].GetGuid(18)] = records[i]?.Outcome?.Status;
            }

            var rows = snapshot.Select(row => new HoldoutOutcomeRow(
                    row.GetGuid(18),
                    Enum.Parse<HoldoutArm>(row.GetString(19)),
                    row.GetGuid(20),
                    row.GetFieldValue<DateTimeOffset>(21),
                    outcomes.GetValueOrDefault(row.GetGuid(18))))
                .ToList();
            return new(ExperienceStoreOutcome.Found, rows, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query holdout outcomes", cancellationToken);
        }
    }
}
