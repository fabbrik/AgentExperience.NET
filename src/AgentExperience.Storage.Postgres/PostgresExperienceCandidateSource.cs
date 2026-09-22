using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceCandidateSource"/> over PostgreSQL full-text search. It follows exactly the
/// order <see cref="PostgresExperienceRecordStore"/> uses -- validate the request, check it against
/// the host-established <see cref="AuthorizationContext"/>, and only then open a connection and run
/// parameterized SQL whose predicates apply the exact scope -- and translates failures the same way.
/// The schema must already exist: <c>0003_add_experience_search.sql</c> adds the generated
/// <c>search_vector</c> column this searches, and the host applies it by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What runs in SQL.</b> The scope predicate -- including any active sharing grant, through
/// <see cref="PostgresExperienceRecordStore.ReadableRecordScopePredicate"/> -- the status filter, the
/// confidence floor, the text match, and the limit. Nothing else: expiry and environment
/// compatibility are Core's decisions, made over what comes back, because they depend on policy and
/// on the request's required attributes rather than on stored state alone.
/// </para>
/// <para>
/// <b>A granted record is a candidate on the same terms as an owned one.</b> Widening happens in the
/// predicate and only there, so a shared record still has to pass the status filter and the
/// confidence floor to be returned, and is then ranked by Core exactly like any other candidate.
/// </para>
/// <para>
/// <b>Relevance.</b> <c>websearch_to_tsquery</c> parses the task text (it accepts arbitrary input --
/// quotes, <c>or</c>, <c>-</c> -- and never raises a syntax error on it), and <c>ts_rank_cd</c> with
/// normalization flag 32 divides the raw rank by itself plus one, so the reported relevance is
/// already in [0, 1). It is a within-search measure: two records' relevances are comparable to each
/// other, not to a relevance from a different query.
/// </para>
/// <para>
/// This source reads and never writes. It needs <c>SELECT</c> on
/// <c>agent_experience.experience_records</c> and, to honour sharing grants, on
/// <c>agent_experience.experience_grants</c>. The second is optional: a role without it (or a database
/// that has not applied <c>0005</c>) falls back to the exact-scope predicate, which narrows what the
/// search returns rather than failing it, and reports it once through the constructor's
/// <c>onGrantsUnavailable</c> callback.
/// </para>
/// </remarks>
public sealed class PostgresExperienceCandidateSource : IExperienceCandidateSource
{
    /// <summary>
    /// The text-search configuration the generated column was built with. It must stay identical to
    /// the one in <c>0003_add_experience_search.sql</c>: querying with a different configuration than
    /// the column was analyzed under silently changes which rows match.
    /// </summary>
    internal const string SearchConfiguration = "english";

    /// <summary>
    /// The alias the rank is selected under. It is appended <em>after</em> the record columns, so
    /// <see cref="PostgresExperienceRecordStore.ReadRecord"/>'s ordinals 0-17 are untouched, and it is
    /// read back by name rather than by a hard-coded ordinal so that adding a column to
    /// <see cref="PostgresExperienceRecordStore.SelectColumns"/> cannot silently shift the rank out
    /// from under this reader.
    /// </summary>
    private const string RelevanceColumn = "relevance";

    /// <summary>
    /// The columns, the relevance, and the shared-by-grant flag. The table is aliased <c>r</c> so the
    /// grant subquery inside the readable predicate can correlate unambiguously; every other column
    /// here is still unqualified and still resolves to this one table.
    /// </summary>
    private const string SearchSelect =
        $"SELECT {PostgresExperienceRecordStore.SelectColumns}, " +
        $"ts_rank_cd(search_vector, websearch_to_tsquery('{SearchConfiguration}', @task_text), 32) AS {RelevanceColumn}, ";

    private const string SearchFrom =
        $" FROM {PostgresExperienceRecordStore.Table} r WHERE ";

    /// <summary>
    /// The same <c>FROM</c> with the lateral join that names the grant a shared row came back through.
    /// The join exposes only <c>grant_id</c>, so every unqualified column in the select list, the
    /// filters, and the ordering still resolves to <c>r</c> exactly as before.
    /// </summary>
    private const string SearchFromWithGrant =
        $" FROM {PostgresExperienceRecordStore.Table} r {PostgresExperienceRecordStore.PermittingGrantJoin} WHERE ";

    private const string SearchFilters =
        " AND status = ANY(@statuses) " +
        "AND reuse_confidence >= @min_confidence " +
        $"AND search_vector @@ websearch_to_tsquery('{SearchConfiguration}', @task_text) " +
        $"ORDER BY {RelevanceColumn} DESC, experience_id LIMIT @limit";

    private const string SearchSql =
        SearchSelect + PostgresExperienceRecordStore.SharedByGrantColumn + ", "
        + PostgresExperienceRecordStore.PermittingGrantColumn + SearchFromWithGrant
        + PostgresExperienceRecordStore.ReadableWithNamedGrantPredicate + SearchFilters;

    /// <summary>
    /// The same search with the grant branch removed, for a database that has no
    /// <c>experience_grants</c> table or a role that may not read it. See
    /// <see cref="PostgresGrantSupport"/>.
    /// </summary>
    private const string SearchExactSql =
        SearchSelect + "false AS " + PostgresExperienceRecordStore.SharedByGrantAlias
        + ", NULL::uuid AS " + PostgresExperienceRecordStore.PermittingGrantAlias + SearchFrom
        + PostgresExperienceRecordStore.RecordScopePredicate + SearchFilters;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceCandidate> NoCandidates = [];

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresGrantSupport _grants;

    private readonly ExperienceGrantAuditing? _auditing;

    /// <summary>Creates a candidate source over a host-owned data source. The source never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="onGrantsUnavailable">
    /// Called at most once, when a search first finds <c>agent_experience.experience_grants</c> missing
    /// or unreadable and falls back to the exact-scope predicate. Optional.
    /// </param>
    /// <param name="auditing">
    /// Where to record the grant-permitted records this search <em>returns</em>, and what a failed
    /// recording does to the search. A candidate carries the record read back in full, so returning one
    /// is a disclosure; the whole search's rows are written in one statement. <see langword="null"/> --
    /// the default -- switches auditing off entirely.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceCandidateSource(
        NpgsqlDataSource dataSource,
        Action<ExperienceGrantSupportNotice>? onGrantsUnavailable = null,
        ExperienceGrantAuditing? auditing = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _grants = new PostgresGrantSupport(onGrantsUnavailable);
        _auditing = auditing;
    }

    /// <inheritdoc />
    public async Task<ExperienceCandidateSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateCandidateQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, NoCandidates, errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            // Fail-closed, and before any connection opens: no search is issued at all.
            return new(ExperienceStoreOutcome.Denied, NoCandidates, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        ExperienceCandidateSearchResult result;
        try
        {
            try
            {
                result = await RunSearchAsync(_grants.Available ? SearchSql : SearchExactSql, query, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (_grants.ShouldFallBack(ex, "candidate search", cancellationToken))
            {
                // No grant table, or no permission to read it: search the exact scope only. Falling
                // back narrows the answer and can never return a record this scope did not own.
                result = await RunSearchAsync(SearchExactSql, query, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "candidate search", cancellationToken);
        }

        // Outside the read's own translation, exactly as the record store's is: an audit failure is the
        // host's policy to decide, never a storage failure raised as a failed search.
        return _auditing is null
            ? result
            : await RecordGrantAccessAsync(authorization, query, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends one access row per grant-permitted record this search is about to return -- all of them
    /// in a single statement, so auditing costs one round trip per search rather than one per row --
    /// and decides what a failed append does to the result.
    /// </summary>
    /// <remarks>
    /// A candidate carries <see cref="ExperienceCandidate.Record"/> read back in full, so handing one
    /// to a caller that does not own it is a disclosure and not a notice that something matched. Under
    /// <see cref="ExperienceGrantAuditingMode.Required"/> a search whose rows cannot be written returns
    /// <em>no</em> candidates rather than the ones that needed no grant: the mode's promise is that
    /// nothing crosses a scope unrecorded, and a partly-returned page would quietly become a different
    /// search than the caller asked for.
    /// </remarks>
    private async Task<ExperienceCandidateSearchResult> RecordGrantAccessAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        ExperienceCandidateSearchResult result,
        CancellationToken cancellationToken)
    {
        var auditing = _auditing!;

        if (result.Outcome != ExperienceStoreOutcome.Found)
        {
            return result;
        }

        var accesses = new List<ExperienceGrantAccess>();
        foreach (var candidate in result.Candidates)
        {
            if (candidate is { SharedByGrant: true, Record: { } record })
            {
                accesses.Add(GrantAuditing.Access(
                    auditing, authorization, query.Scope, query.CorrelationId, record, candidate.PermittingGrantId));
            }
        }

        return await GrantAuditing.RecordAsync(auditing, accesses, cancellationToken).ConfigureAwait(false)
            ? result
            : new(ExperienceStoreOutcome.Found, NoCandidates, NoErrors);
    }

    private async Task<ExperienceCandidateSearchResult> RunSearchAsync(
        string sql,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        var parameters = command.Parameters;
        PostgresExperienceRecordStore.AddScopeParameters(parameters, query.Scope);
        parameters.Add(new NpgsqlParameter<string>("task_text", NpgsqlDbType.Text) { TypedValue = query.TaskText });

        var statuses = query.EligibleStatuses.Distinct().Select(status => status.ToString()).ToArray();
        parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
        parameters.Add(new NpgsqlParameter<double>("min_confidence", query.MinimumConfidence));
        parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

        var candidates = new List<ExperienceCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new ExperienceCandidate(
                PostgresExperienceRecordStore.ReadRecord(reader),
                ReadRelevance(reader),
                PostgresExperienceRecordStore.ReadSharedByGrant(reader),
                PostgresExperienceRecordStore.ReadPermittingGrant(reader)));
        }

        return new(ExperienceStoreOutcome.Found, candidates, NoErrors);
    }

    /// <summary>
    /// Reads the rank and clamps it into [0, 1]. Normalization flag 32 already bounds it, but a rank
    /// read back as NaN or out of range would otherwise travel into Core's ranking arithmetic and
    /// poison every comparison against it.
    /// </summary>
    private static double ReadRelevance(System.Data.Common.DbDataReader reader)
    {
        double rank;
        try
        {
            rank = reader.GetDouble(reader.GetOrdinal(RelevanceColumn));
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }

        if (double.IsNaN(rank))
        {
            return 0d;
        }

        return Math.Clamp(rank, 0d, 1d);
    }
}
