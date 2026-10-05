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
/// confidence floor, the authorship exclusion when the query asks for it, the text match, and the limit.
/// Nothing else: expiry and environment compatibility are Core's decisions, made over what comes back,
/// because they depend on policy and on the request's required attributes rather than on stored state alone.
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
/// <b>The authorship exclusion</b> (<see cref="ExperienceCandidateQuery.ExcludeModelAuthored"/>, story 14.4) reads
/// <c>0021</c>'s plaintext flag, so it runs before the limit in both modes. It keeps only records the flag marks
/// deterministic and fails closed on the rest (story 17.1): a sealed row stored without its flag -- sealed before
/// <c>0021</c>, whose payload the migration could not open, sealed during a rolling deploy by an instance still on the
/// previous build, or written by any writer that left the flag out -- is left out of an excluding search as if a model
/// wrote it, and takes no place in its window. The owner-run
/// <see cref="PostgresExperienceRecordStore.BackfillSealedAuthorshipAsync(AuthorizationContext, Scope, int, ScopeMatch, Guid?, CancellationToken)"/> opens such rows and writes their flag, so
/// a deterministic one is found again. Without the exclusion nothing changes.
/// </para>
/// <para>
/// This source reads and never writes. It needs <c>SELECT</c> on
/// <c>agent_experience.experience_records</c> and, to honour sharing grants, on
/// <c>agent_experience.experience_grants</c>. The second is optional: a role without it (or a database
/// that has not applied <c>0005</c>) falls back to the exact-scope predicate, which narrows what the
/// search returns rather than failing it, and reports it once through the constructor's
/// <c>onGrantsUnavailable</c> callback.
/// </para>
/// <para>
/// <b>Under row-level security</b> (story 17.7) the search runs through <see cref="TextSearchFunction"/>,
/// <c>agent_experience.search_experience_text</c>, which applies the read policy's admission and then exactly this
/// statement's predicates as the owner, so the GIN indexes stay usable; it is used only while row-level security is
/// enabled on <c>experience_records</c> and the role holds <c>EXECUTE</c> on it, which the privileges call grants
/// exactly then. Otherwise the statement below runs, unchanged.
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
    internal const string RelevanceColumn = "relevance";

    /// <summary>
    /// The columns, the relevance, and the shared-by-grant flag. The table is aliased <c>r</c> so the
    /// grant subquery inside the readable predicate can correlate unambiguously; every other column
    /// here is still unqualified and still resolves to this one table.
    /// </summary>
    private const string SearchSelect =
        $"SELECT {PostgresExperienceRecordStore.SelectColumns}, " +
        $"ts_rank_cd({RankedVector}, websearch_to_tsquery('{SearchConfiguration}', @task_text), 32) AS {RelevanceColumn}, ";

    /// <summary>
    /// The vector a row is ranked on: a sealed record's derived <c>search_vector_sealed</c> (<c>0016</c>), or a
    /// plaintext record's generated <c>search_vector</c> (<c>0003</c>). Both come from the same expression over
    /// the same three fields, so a record ranks identically whichever mode wrote it.
    /// </summary>
    internal const string RankedVector = "coalesce(search_vector_sealed, search_vector)";

    /// <summary>
    /// The match, stated per kind of row so each half can use its own GIN index: a plaintext row through
    /// <c>search_vector</c>, a sealed row through <c>search_vector_sealed</c> only -- never through the
    /// generated vector of its placeholder task ID.
    /// </summary>
    internal const string MatchPredicate =
        $"((search_vector_sealed IS NULL AND search_vector @@ websearch_to_tsquery('{SearchConfiguration}', @task_text)) " +
        $"OR search_vector_sealed @@ websearch_to_tsquery('{SearchConfiguration}', @task_text))";

    private const string SearchFrom =
        $" FROM {PostgresExperienceRecordStore.Table} r WHERE ";

    /// <summary>
    /// The same <c>FROM</c> with the lateral join that names the grant a shared row came back through.
    /// The join exposes only <c>grant_id</c> and <c>disclosure</c>, both read qualified, so every unqualified column in the select list, the
    /// filters, and the ordering still resolves to <c>r</c> exactly as before.
    /// </summary>
    private const string SearchFromWithGrant =
        $" FROM {PostgresExperienceRecordStore.Table} r {PostgresExperienceRecordStore.PermittingGrantJoin} WHERE ";

    private const string SearchFilterHead =
        // A tombstone carries no payload, so it is not a candidate: its generated search_vector holds
        // only the deletion placeholder, and a row that matched it would come back with nothing in it.
        //
        // Redundant today, and kept on purpose. The status filter below already excludes a tombstone --
        // its status is a literal no ExperienceStatus member names -- so no query this adapter can build
        // distinguishes the two, and no test can either. It is stated here rather than left to be
        // rediscovered: it is defence in depth against a future status whose name collides, not the
        // thing that makes erased text unfindable. What makes erased text unfindable is that
        // search_vector is GENERATED ALWAYS and regenerates from the placeholder alone, and that 0016's
        // trigger clears a sealed record's search_vector_sealed on the same transition.
        $" AND {PostgresExperienceRecordStore.RecordLivePredicate} " +
        "AND status = ANY(@statuses) " +
        "AND reuse_confidence >= @min_confidence ";

    private const string SearchFilterTail =
        $"AND {MatchPredicate} " +
        $"ORDER BY {RelevanceColumn} DESC, experience_id LIMIT @limit";

    /// <summary>Every filter after the scope predicate, through the limit. Shared with <see cref="TextSearchFunction"/>.</summary>
    internal const string SearchFilters = SearchFilterHead + SearchFilterTail;

    /// <summary>
    /// The authorship exclusion (story 14.4), <c>0021</c>'s flag, applied before the limit like the status filter and
    /// the confidence floor. It keeps <c>false</c> only: <c>NULL</c> is a sealed row stored without its flag, whose
    /// authorship SQL cannot read, and it fails closed (story 17.1) -- left out like <c>true</c>, until the owner's
    /// backfill writes its flag. It is <see cref="ModelAuthoredPredicate"/>, unqualified, which resolves to <c>r</c>
    /// here as every column does.
    /// </summary>
    internal const string ExcludingSearchFilters = SearchFilterHead + "AND " + ModelAuthoredPredicate + " " + SearchFilterTail;

    /// <summary>
    /// "the row says this record's reflection was not written by a model": <c>0021</c>'s flag is <c>false</c>, so an
    /// unknown (<c>NULL</c>) flag counts as model-authored. Shared with the vectors package's search, which qualifies
    /// the column itself.
    /// </summary>
    internal const string ModelAuthoredPredicate = PostgresExperienceRecordStore.ModelAuthoredColumn + " IS FALSE";

    /// <summary>
    /// The grant-aware statement up to and including its scope predicate: the select list, the <c>FROM</c> with the
    /// lateral grant join, and the readable predicate. Shared with <see cref="TextSearchFunction"/>, which appends the
    /// row-level security admission (story 17.7) between it and the filters.
    /// </summary>
    internal const string ReadableSearchHead =
        SearchSelect + PostgresExperienceRecordStore.SharedByGrantColumn + ", "
        + PostgresExperienceRecordStore.PermittingGrantColumn + ", "
        + PostgresExperienceRecordStore.PermittingDisclosureColumn + SearchFromWithGrant
        + PostgresExperienceRecordStore.ReadableWithNamedGrantPredicate;

    /// <summary>The exact-scope statement up to and including its scope predicate. Shared with <see cref="TextSearchFunction"/>.</summary>
    internal const string ExactSearchHead =
        SearchSelect + "false AS " + PostgresExperienceRecordStore.SharedByGrantAlias
        + ", NULL::uuid AS " + PostgresExperienceRecordStore.PermittingGrantAlias
        + ", NULL::text AS " + PostgresExperienceRecordStore.PermittingDisclosureAlias + SearchFrom
        + PostgresExperienceRecordStore.RecordScopePredicate;

    private const string SearchSql = ReadableSearchHead + SearchFilters;

    /// <summary>
    /// The same search with the grant branch removed, for a database that has no
    /// <c>experience_grants</c> table or a role that may not read it. See
    /// <see cref="PostgresGrantSupport"/>.
    /// </summary>
    private const string SearchExactSql = ExactSearchHead + SearchFilters;

    /// <summary><see cref="SearchSql"/> with the authorship exclusion. Nothing else differs.</summary>
    private const string ExcludingSearchSql = ReadableSearchHead + ExcludingSearchFilters;

    /// <summary><see cref="SearchExactSql"/> with the authorship exclusion. Nothing else differs.</summary>
    private const string ExcludingSearchExactSql = ExactSearchHead + ExcludingSearchFilters;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceCandidate> NoCandidates = [];

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresGrantSupport _grants;

    private readonly ExperienceGrantAuditing? _auditing;

    private readonly ExperienceEncryption? _encryption;

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
    /// <param name="encryption">
    /// The deployment's crypto-shredding configuration, needed to open the sealed records a search returns.
    /// <see langword="null"/> -- the default -- is plaintext mode. See <see cref="ExperienceEncryption"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceCandidateSource(
        NpgsqlDataSource dataSource,
        Action<ExperienceGrantSupportNotice>? onGrantsUnavailable = null,
        ExperienceGrantAuditing? auditing = null,
        ExperienceEncryption? encryption = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _grants = new PostgresGrantSupport(onGrantsUnavailable);
        _auditing = auditing;
        _encryption = ExperienceEncryption.Resolve(encryption);
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
        IReadOnlyList<ExperienceGrantDisclosure?> disclosures;
        try
        {
            try
            {
                (result, disclosures) = await RunSearchAsync(_grants.Available, authorization, query, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (_grants.ShouldFallBack(ex, "candidate search", cancellationToken))
            {
                // No grant table, or no permission to read it: search the exact scope only. Falling
                // back narrows the answer and can never return a record this scope did not own.
                (result, disclosures) = await RunSearchAsync(withGrants: false, authorization, query, cancellationToken).ConfigureAwait(false);
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
            : await RecordGrantAccessAsync(authorization, query, result, disclosures, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The grant-aware statement for <paramref name="query"/>, with the authorship exclusion when it asks for one.</summary>
    private static string Readable(ExperienceCandidateQuery query) => query.ExcludeModelAuthored ? ExcludingSearchSql : SearchSql;

    /// <summary>The exact-scope statement for <paramref name="query"/>, with the authorship exclusion when it asks for one.</summary>
    private static string Exact(ExperienceCandidateQuery query) => query.ExcludeModelAuthored ? ExcludingSearchExactSql : SearchExactSql;

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
    /// <para>
    /// <paramref name="disclosures"/> runs parallel to the candidates: each permitting grant's level,
    /// read by the same statement. It goes onto the access row only -- an
    /// <see cref="ExperienceCandidate"/> never carries it.
    /// </para>
    /// </remarks>
    private async Task<ExperienceCandidateSearchResult> RecordGrantAccessAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        ExperienceCandidateSearchResult result,
        IReadOnlyList<ExperienceGrantDisclosure?> disclosures,
        CancellationToken cancellationToken)
    {
        var auditing = _auditing!;

        if (result.Outcome != ExperienceStoreOutcome.Found)
        {
            return result;
        }

        if (disclosures.Count != result.Candidates.Count)
        {
            // Read row by row alongside the candidates, so this cannot happen; if it ever did, guessing a
            // level for a row would attribute a disclosure to the wrong delivery.
            throw new ExperienceStoreException(
                "The disclosure levels read do not line up with the candidates returned.");
        }

        var accesses = new List<ExperienceGrantAccess>();
        for (var i = 0; i < result.Candidates.Count; i++)
        {
            if (result.Candidates[i] is { SharedByGrant: true, Record: { } record } candidate)
            {
                accesses.Add(GrantAuditing.Access(
                    auditing,
                    authorization,
                    query.Scope,
                    query.CorrelationId,
                    record,
                    candidate.PermittingGrantId,
                    disclosures[i]));
            }
        }

        return await GrantAuditing.RecordAsync(auditing, authorization, accesses, cancellationToken).ConfigureAwait(false)
            ? result
            : new(ExperienceStoreOutcome.Found, NoCandidates, NoErrors);
    }

    /// <summary>
    /// Runs the search, through <see cref="TextSearchFunction"/> while row-level security is on and the role may call it
    /// (story 17.7), and through the store's own statement otherwise. A call that fails as an undefined function or an
    /// insufficient privilege re-detects the route: when the function is now gone or no longer granted -- the deployment
    /// changed since the route was cached -- the store's own statement runs, which the policies confine exactly as before;
    /// when it is still usable, the error came from inside it and is rethrown.
    /// </summary>
    private async Task<(ExperienceCandidateSearchResult Result, IReadOnlyList<ExperienceGrantDisclosure?> Disclosures)> RunSearchAsync(
        bool withGrants,
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        var (found, stale) = await RunSearchAsync(withGrants, allowFunction: true, authorization, query, cancellationToken).ConfigureAwait(false);
        if (found is { } answer)
        {
            return answer;
        }

        TextSearchRoute.Invalidate(_dataSource);
        bool stillUsable;
        await using (var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false))
        {
            stillUsable = await TextSearchRoute.UsesFunctionAsync(_dataSource, session, cancellationToken).ConfigureAwait(false);
        }

        if (stillUsable)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(stale!);
        }

        return (await RunSearchAsync(withGrants, allowFunction: false, authorization, query, cancellationToken).ConfigureAwait(false)).Found!.Value;
    }

    /// <summary>The search, or the error of a function call that may have been refused as stale.</summary>
    private async Task<((ExperienceCandidateSearchResult Result, IReadOnlyList<ExperienceGrantDisclosure?> Disclosures)? Found, PostgresException? Stale)> RunSearchAsync(
        bool withGrants,
        bool allowFunction,
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        // The rows are read into memory, the reader closed, the read-only transaction committed and the connection
        // returned to the pool before any key is fetched: nothing after decoding writes in this transaction (the
        // access rows are appended later, on a connection of their own), so a slow key store holds no connection.
        // Every sealed row's key then comes from one key-store call, not one call per row.
        List<SnapshotRow> rows;
        await using (var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false))
        {
            var useFunction = allowFunction
                && await TextSearchRoute.UsesFunctionAsync(_dataSource, session, cancellationToken).ConfigureAwait(false);
            var sql = useFunction ? TextSearchFunction.CallSql : withGrants ? Readable(query) : Exact(query);
            await using (var command = session.CreateCommand(sql))
            {
                var parameters = command.Parameters;
                PostgresExperienceRecordStore.AddScopeParameters(parameters, query.Scope);
                parameters.Add(new NpgsqlParameter<string>("task_text", NpgsqlDbType.Text) { TypedValue = query.TaskText });

                var statuses = query.EligibleStatuses.Distinct().Select(status => status.ToString()).ToArray();
                parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
                parameters.Add(new NpgsqlParameter<double>("min_confidence", query.MinimumConfidence));
                parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));
                if (useFunction)
                {
                    parameters.Add(new NpgsqlParameter<bool>("exclude_model_authored", query.ExcludeModelAuthored));
                    parameters.Add(new NpgsqlParameter<bool>("with_grants", withGrants));
                }

                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    rows = await SnapshotRow.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException ex) when (useFunction && TextSearchRoute.MayBeStale(ex, cancellationToken))
                {
                    return (null, ex);
                }
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var candidates = new List<ExperienceCandidate>();
        var disclosures = new List<ExperienceGrantDisclosure?>();
        var records = await PostgresExperienceRecordStore.ReadRecordsAsync(rows, _encryption, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            // A sealed record whose key was destroyed is erased: never a candidate, like a tombstone.
            if (records[i] is not { } record)
            {
                continue;
            }

            var row = rows[i];
            candidates.Add(new ExperienceCandidate(
                record,
                ReadRelevance(row),
                PostgresExperienceRecordStore.ReadSharedByGrant(row),
                PostgresExperienceRecordStore.ReadPermittingGrant(row)));
            disclosures.Add(PostgresExperienceRecordStore.ReadPermittingDisclosure(row));
        }

        return ((new(ExperienceStoreOutcome.Found, candidates, NoErrors), disclosures), null);
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
