using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// When each record was last injected, for the lessons view. <c>0026</c>'s <c>agent_experience.experience_injections</c> holds one
/// row per record injected in its own scope; <see cref="RecordInjectionAsync"/> upserts it in one statement per injected
/// block, and <see cref="QueryWithInjectionsAsync"/> reads it with the page of records in one statement.
/// </summary>
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>The injection table <c>0026</c> creates.</summary>
    internal const string InjectionsTable = "agent_experience.experience_injections";

    /// <summary>
    /// The one write: a row per named record that is live in exactly the caller's scope, its scope copied from the record
    /// so a row can never name another scope, the instant moved only forward and the count raised by one. The records
    /// are locked <c>FOR KEY SHARE</c> in ID order: an erasure racing the write then either finds the row and its trigger
    /// deletes it, or runs first and leaves the record a tombstone this statement no longer selects; and two blocks
    /// recording the same records lock them in one order.
    /// </summary>
    internal const string RecordInjectionSql =
        $"INSERT INTO {InjectionsTable} AS i (experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "last_injected_at, injection_count) " +
        "SELECT r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, @injected_at, 1 " +
        $"FROM {Table} r WHERE r.experience_id = ANY(@experience_ids) AND {RecordScopePredicate} AND {RecordLivePredicate} " +
        $"ORDER BY r.experience_id {RecordKeyShareLock} " +
        "ON CONFLICT (experience_id) DO UPDATE SET " +
        "last_injected_at = GREATEST(i.last_injected_at, EXCLUDED.last_injected_at), " +
        "injection_count = i.injection_count + 1";

    /// <summary>
    /// <see cref="QuerySql"/>'s page, then each row's injection summary joined on its primary key: one statement, bounded
    /// by the page's limit. The record columns keep ordinals 0-17; the summary is 18 and 19, null for a record never
    /// injected.
    /// </summary>
    private const string QueryWithInjectionsPrefix = "WITH page AS (" + QuerySql;

    private const string QueryWithInjectionsSuffix =
        QueryOrderAndLimit + ") " +
        $"SELECT page.*, i.last_injected_at, i.injection_count FROM page LEFT JOIN {InjectionsTable} i ON i.experience_id = page.experience_id " +
        "ORDER BY page.created_at DESC, page.experience_id";

    /// <inheritdoc />
    /// <remarks>
    /// One statement, whatever the number of records: an upsert into <c>agent_experience.experience_injections</c> of the
    /// named records that are live in exactly <paramref name="scope"/>. Until the application role has been granted the
    /// table (re-run <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(NpgsqlDataSource, ExperienceApplicationRoleOptions, CancellationToken)"/>
    /// after migrating to <c>0026</c>) it throws <see cref="ExperienceStoreException"/>, which the injection adapter
    /// reports and injects anyway.
    /// </remarks>
    public async Task<ExperienceInjectionRecordResult> RecordInjectionAsync(
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
            return new(ExperienceStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ids = experienceIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new(ExperienceStoreOutcome.Committed, NoErrors);
        }

        try
        {
            await using var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false);
            await using (var command = session.CreateCommand(RecordInjectionSql))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", ids));
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("injected_at", ToStoredTimestamp(injectedAt)));
                AddScopeParameters(command.Parameters, scope);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Committed, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "record injection", cancellationToken);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement: <see cref="QueryAsync"/>'s page, left-joined on the primary key to
    /// <c>agent_experience.experience_injections</c>. A schema the application role cannot yet read that table in (not
    /// migrated to <c>0026</c>, or the privileges call not re-run since) answers exactly as <see cref="QueryAsync"/>,
    /// with <see cref="ExperienceRecordQueryResult.Injections"/> <see langword="null"/>: not tracked.
    /// </remarks>
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
            return new(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var sql = QueryWithInjectionsPrefix
                + (query.Statuses is null ? string.Empty : QueryStatusPredicate)
                + QueryWithInjectionsSuffix;

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
            var injections = new Dictionary<Guid, ExperienceInjectionSummary>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // A sealed record whose key was destroyed is erased, and absent here like a tombstone.
                    if (await ReadRecordAsync(reader, _encryption, cancellationToken).ConfigureAwait(false) is not { } record)
                    {
                        continue;
                    }

                    records.Add(record);
                    if (!await reader.IsDBNullAsync(18, cancellationToken).ConfigureAwait(false))
                    {
                        injections[record.ExperienceId] = new ExperienceInjectionSummary(
                            reader.GetFieldValue<DateTimeOffset>(18),
                            reader.GetInt64(19));
                    }
                }
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Found, records, NoErrors) { Injections = injections };
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UndefinedTable)
        {
            // The injection table is not there yet, or not granted yet: the page alone, read as untracked.
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query", cancellationToken);
        }

        return await QueryAsync(authorization, query, cancellationToken).ConfigureAwait(false);
    }
}
