using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The injection holdout's assignments. <c>0027</c>'s <c>agent_experience.experience_holdout_assignments</c>
/// holds one row per captured run and record its block named, with the arm; <see cref="RecordHoldoutAssignmentAsync"/>
/// inserts them in one statement per assigned block.
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
}
