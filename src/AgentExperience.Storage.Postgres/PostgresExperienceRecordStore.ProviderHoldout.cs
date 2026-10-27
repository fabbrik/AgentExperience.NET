using AgentExperience.Abstractions;
using Npgsql;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Another memory provider's holdout assignments. <c>0029</c>'s
/// <c>agent_experience.experience_provider_holdout_assignments</c> holds one row per captured run and provider key, with
/// the arm; <see cref="RecordProviderHoldoutAssignmentAsync"/> inserts it, and
/// <see cref="QueryProviderHoldoutOutcomesAsync"/> reads one provider's rows back, a page at a time, with each run's own
/// verified outcome.
/// </summary>
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>The provider holdout table <c>0029</c> creates.</summary>
    internal const string ProviderHoldoutAssignmentsTable = "agent_experience.experience_provider_holdout_assignments";

    /// <summary>
    /// The one write: a row for the run and key in exactly the caller's scope, its instant clamped to the database's clock.
    /// A row that already exists for the run and key is left as it is. The late-write guard is
    /// <see cref="RecordHoldoutAssignmentSql"/>'s: the run's own records (<c>source_run_id</c> is the run, in the caller's
    /// tenant) are locked <c>FOR KEY SHARE</c> in ID order first, and nothing is written when every one of them is by then
    /// a tombstone, so an erasure of the run's record that overlaps the write serialises with it in either order.
    /// </summary>
    internal const string RecordProviderHoldoutAssignmentSql =
        "WITH run_record AS MATERIALIZED (SELECT d.deleted_at\n" +
        $"FROM {Table} d\n" +
        "WHERE d.experience_id = ANY (ARRAY(SELECT s.experience_id FROM " + Table + " s " +
        "WHERE s.tenant_id = @tenant_id AND s.source_run_id = @run_id))\n" +
        "ORDER BY d.experience_id FOR KEY SHARE OF d)\n" +
        $"INSERT INTO {ProviderHoldoutAssignmentsTable} (run_id, provider_key, arm, tenant_id, application_id, project_id, " +
        "team_id, agent_id, user_id, assigned_at)\n" +
        "SELECT @run_id, @provider_key, @arm, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, " +
        "LEAST(@assigned_at, pg_catalog.clock_timestamp())\n" +
        $"WHERE {RunNotErasedPredicate}\n" +
        "ON CONFLICT (tenant_id, run_id, provider_key) DO NOTHING";

    /// <inheritdoc />
    public bool TracksProviderHoldout => true;

    /// <inheritdoc />
    /// <remarks>
    /// One statement: an insert into <c>agent_experience.experience_provider_holdout_assignments</c> in exactly
    /// <paramref name="scope"/>. Until the application role has been granted the table (re-run
    /// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(NpgsqlDataSource, ExperienceApplicationRoleOptions, CancellationToken)"/>
    /// after migrating to <c>0029</c>) it throws <see cref="ExperienceStoreException"/>, which the measuring wrapper
    /// reports without affecting the invocation.
    /// </remarks>
    public async Task<ExperienceHoldoutRecordResult> RecordProviderHoldoutAssignmentAsync(
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
            return new(ExperienceStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            await using (var command = session.CreateCommand(RecordProviderHoldoutAssignmentSql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid>("run_id", assignment.RunId));
                command.Parameters.Add(new NpgsqlParameter<string>("provider_key", assignment.ProviderKey));
                command.Parameters.Add(new NpgsqlParameter<string>("arm", assignment.Arm.ToString()));
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("assigned_at", ToStoredTimestamp(assignment.AssignedAt)));
                AddScopeParameters(command.Parameters, scope);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Committed, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "record provider holdout assignment", cancellationToken);
        }
    }

    /// <summary>
    /// The read's first part: a page of the caller's exact-scope rows for one provider key in run order, then, for each run
    /// on the page, the run's own live record in exactly the same scope (the earliest created, then the lowest ID), as
    /// <see cref="QueryHoldoutOutcomesPrefix"/> finds it. The record columns keep ordinals 0-17, null when the run has no
    /// record; the row is 18-20. The keyset predicate, when there is one, goes between the two parts.
    /// </summary>
    internal const string QueryProviderHoldoutOutcomesPrefix =
        "WITH page AS (SELECT run_id, arm, assigned_at\n" +
        $"FROM {ProviderHoldoutAssignmentsTable}\n" +
        $"WHERE {ScopePredicate} AND provider_key = @provider_key";

    /// <summary>Resumes after the previous page's last run.</summary>
    internal const string QueryProviderHoldoutOutcomesAfterPredicate = " AND run_id > @after_run_id";

    internal const string QueryProviderHoldoutOutcomesSuffix =
        "\nORDER BY run_id LIMIT @limit),\n" +
        $"run_record AS (SELECT DISTINCT ON (source_run_id) {SelectColumns}\n" +
        $"FROM {Table}\n" +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND source_run_id IN (SELECT run_id FROM page)\n" +
        "ORDER BY source_run_id, created_at, experience_id)\n" +
        "SELECT run_record.*, page.run_id, page.arm, page.assigned_at\n" +
        "FROM page LEFT JOIN run_record ON run_record.source_run_id = page.run_id\n" +
        "ORDER BY page.run_id";

    /// <inheritdoc />
    /// <remarks>
    /// One statement per page (see <see cref="QueryProviderHoldoutOutcomesPrefix"/>). The rows are read into memory and
    /// the connection released before any key is fetched; then every sealed run record on the page is opened in one
    /// key-store call, through the store's own read path. One sealed under a destroyed key reads as no outcome. Until the
    /// application role has been granted <c>0029</c>'s table it throws <see cref="ExperienceStoreException"/>.
    /// </remarks>
    public async Task<ProviderHoldoutOutcomeQueryResult> QueryProviderHoldoutOutcomesAsync(
        AuthorizationContext authorization,
        ProviderHoldoutOutcomeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var errors = ExperienceRecordValidator.ValidateProviderHoldoutOutcomeQuery(query);
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
            var sql = QueryProviderHoldoutOutcomesPrefix
                + (query.AfterRunId is null ? string.Empty : QueryProviderHoldoutOutcomesAfterPredicate)
                + QueryProviderHoldoutOutcomesSuffix;

            List<SnapshotRow> snapshot;
            await using (var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false))
            {
                await using (var command = session.CreateCommand(sql))
                {
                    AddScopeParameters(command.Parameters, query.Scope);
                    command.Parameters.Add(new NpgsqlParameter<string>("provider_key", query.ProviderKey));
                    command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));
                    if (query.AfterRunId is { } after)
                    {
                        command.Parameters.Add(new NpgsqlParameter<Guid>("after_run_id", after));
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    snapshot = await SnapshotRow.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
                }

                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // One row per run, so each run record is decoded once: sealed ones in one batch.
            var runRows = snapshot.Where(row => !row.IsDBNull(0)).ToArray();
            var records = await ReadRecordsAsync(runRows, _encryption, cancellationToken).ConfigureAwait(false);
            var outcomes = new Dictionary<Guid, TaskVerificationStatus?>(runRows.Length);
            for (var i = 0; i < runRows.Length; i++)
            {
                outcomes[runRows[i].GetGuid(18)] = records[i]?.Outcome?.Status;
            }

            var rows = snapshot.Select(row => new ProviderHoldoutOutcomeRow(
                    row.GetGuid(18),
                    query.ProviderKey,
                    Enum.Parse<HoldoutArm>(row.GetString(19)),
                    row.GetFieldValue<DateTimeOffset>(20),
                    outcomes.GetValueOrDefault(row.GetGuid(18))))
                .ToList();
            return new(ExperienceStoreOutcome.Found, rows, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query provider holdout outcomes", cancellationToken);
        }
    }
}
