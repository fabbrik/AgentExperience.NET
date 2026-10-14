using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Why a store answered the lessons view's injection read without injections, or refused to record one.
/// </summary>
/// <param name="Operation">What hit it: <c>"query with injections"</c>.</param>
/// <param name="Reason">Which condition was detected. Content-free and safe to log.</param>
public sealed record ExperienceInjectionTrackingNotice(string Operation, ExperienceInjectionTrackingReason Reason);

/// <summary>Which condition made <c>agent_experience.experience_injections</c> unusable.</summary>
public enum ExperienceInjectionTrackingReason
{
    /// <summary>The table does not exist: <c>0026_experience_injections.sql</c> has not been applied.</summary>
    TableMissing,

    /// <summary>The connecting role may not read the table: the privileges call has not been re-run since <c>0026</c>.</summary>
    NotPermitted,
}

/// <summary>
/// When each record was last injected, for the lessons view. <c>0026</c>'s <c>agent_experience.experience_injections</c>
/// holds one row per record injected in its own scope; <see cref="RecordInjectionAsync"/> upserts it in one statement
/// per injected block, and <see cref="QueryWithInjectionsAsync"/> reads it with the page of records in one statement.
/// </summary>
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>The injection table <c>0026</c> creates.</summary>
    internal const string InjectionsTable = "agent_experience.experience_injections";

    /// <summary>
    /// The one write: a row per named record that is live in exactly the caller's scope, its scope copied from the record
    /// so a row can never name another scope. The instant is clamped to the database's clock, then moved only forward;
    /// the count rises by one and saturates at the <c>bigint</c> maximum. The records are locked <c>FOR KEY SHARE</c> in
    /// ID order, the pattern of every adapter write that checks a record: the purge locks its record <c>FOR UPDATE</c>
    /// first, so an erasure racing the write either waits for it and then deletes its row through <c>0026</c>'s trigger,
    /// or runs first and leaves a tombstone this statement no longer selects. <c>0026</c>'s header explains why no
    /// deadlock is possible. It is <c>contract/sql/record-injection.sql</c> (contract 1.4), line for line, which the
    /// Python and Go ports run as written; a contract test keeps the two identical.
    /// </summary>
    internal const string RecordInjectionSql =
        $"INSERT INTO {InjectionsTable} AS i (experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "last_injected_at, injection_count)\n" +
        "SELECT r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, " +
        "LEAST(@injected_at, pg_catalog.clock_timestamp()), 1\n" +
        $"FROM {Table} r\n" +
        $"WHERE r.experience_id = ANY(@experience_ids) AND {RecordScopePredicate} AND {RecordLivePredicate}\n" +
        $"ORDER BY r.experience_id {RecordKeyShareLock}\n" +
        "ON CONFLICT (experience_id) DO UPDATE SET " +
        "last_injected_at = GREATEST(i.last_injected_at, EXCLUDED.last_injected_at), " +
        "injection_count = CASE WHEN i.injection_count = 9223372036854775807 THEN i.injection_count ELSE i.injection_count + 1 END";

    /// <summary>
    /// <see cref="QuerySql"/>'s page, then each row's injection summary joined on its primary key and its scope: one
    /// statement, bounded by the page's limit. The record columns keep ordinals 0-17; the summary is 18 and 19, null for a
    /// record never injected.
    /// </summary>
    internal const string QueryWithInjectionsPrefix = "WITH page AS (" + QuerySql;

    internal const string QueryWithInjectionsSuffix =
        QueryOrderAndLimit + ") " +
        $"SELECT page.*, i.last_injected_at, i.injection_count FROM page LEFT JOIN {InjectionsTable} i " +
        "ON i.experience_id = page.experience_id AND i.tenant_id = page.tenant_id AND i.application_id = page.application_id " +
        "AND i.project_id = page.project_id AND i.team_id IS NOT DISTINCT FROM page.team_id " +
        "AND i.agent_id IS NOT DISTINCT FROM page.agent_id AND i.user_id IS NOT DISTINCT FROM page.user_id " +
        "ORDER BY page.created_at DESC, page.experience_id";

    private const string UndefinedTableState = "42P01";

    private const string InsufficientPrivilegeState = "42501";

    private int _injectionsNoticeSent;

    /// <summary>
    /// Called at most once per store, the first time <see cref="QueryWithInjectionsAsync"/> finds
    /// <c>agent_experience.experience_injections</c> missing (not migrated to <c>0026</c>) or unreadable (the privileges
    /// call not re-run since) and answers the page without injections. Optional: the fallback happens either way. A
    /// callback that throws is ignored.
    /// </summary>
    public Action<ExperienceInjectionTrackingNotice>? OnInjectionTrackingUnavailable { get; init; }

    /// <inheritdoc />
    public bool TracksInjections => true;

    /// <inheritdoc />
    /// <remarks>
    /// One statement, whatever the number of records: an upsert into <c>agent_experience.experience_injections</c> of the
    /// named records that are live in exactly <paramref name="scope"/>. In encrypted mode a record whose key is destroyed
    /// counts as erased and is skipped, at the cost of one batched key lookup. Until the application role has been
    /// granted the table (re-run
    /// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(NpgsqlDataSource, ExperienceApplicationRoleOptions, CancellationToken)"/>
    /// after migrating to <c>0026</c>) it throws <see cref="ExperienceStoreException"/>, which the injection adapter
    /// reports without affecting the injection.
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

        try
        {
            if (_encryption is not null && ids.Length > 0)
            {
                ids = await WithLiveKeysAsync(ids, scope, cancellationToken).ConfigureAwait(false);
            }

            if (ids.Length == 0)
            {
                return new(ExperienceStoreOutcome.Committed, NoErrors);
            }

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

    /// <summary>
    /// The IDs whose key in <paramref name="scope"/> is not destroyed: a sealed record whose key is gone is erased, so it
    /// is never recorded as injected. A row with no key at all (written before encryption) stays in.
    /// </summary>
    private async Task<Guid[]> WithLiveKeysAsync(Guid[] ids, Scope scope, CancellationToken cancellationToken)
    {
        var references = ids.Select(id => new ExperienceKeyReference(id, scope)).ToArray();
        using var keys = await _encryption!.ForReadManyAsync(references, cancellationToken, allowMissing: true).ConfigureAwait(false);
        return [.. ids.Where((_, i) => keys.IsMissing(references[i]) || keys[references[i]] is not null)];
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement: <see cref="QueryAsync"/>'s page, left-joined on the primary key and scope to
    /// <c>agent_experience.experience_injections</c>. A schema the application role cannot yet read that table in (not
    /// migrated to <c>0026</c>, or the privileges call not re-run since) answers exactly as <see cref="QueryAsync"/>,
    /// with <see cref="ExperienceRecordQueryResult.Injections"/> <see langword="null"/> (not tracked), and is reported
    /// once through <see cref="OnInjectionTrackingUnavailable"/>.
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
                    // A sealed record whose key was destroyed is erased, and absent here like a tombstone, with its
                    // injection summary.
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
        catch (PostgresException ex) when (ex.SqlState is UndefinedTableState or InsufficientPrivilegeState && !cancellationToken.IsCancellationRequested)
        {
            // The injection table is not there yet, or not granted yet: the page alone, read as untracked.
            NoticeInjectionsUnavailable(ex.SqlState == UndefinedTableState
                ? ExperienceInjectionTrackingReason.TableMissing
                : ExperienceInjectionTrackingReason.NotPermitted);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query", cancellationToken);
        }

        return await QueryAsync(authorization, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reports the injection table's absence once per store; a throwing callback is the host's problem only.</summary>
    private void NoticeInjectionsUnavailable(ExperienceInjectionTrackingReason reason)
    {
        if (Interlocked.Exchange(ref _injectionsNoticeSent, 1) != 0)
        {
            return;
        }

        try
        {
            OnInjectionTrackingUnavailable?.Invoke(new ExperienceInjectionTrackingNotice("query with injections", reason));
        }
#pragma warning disable CA1031 // A host's logging callback must not turn a page that read into a failure.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
