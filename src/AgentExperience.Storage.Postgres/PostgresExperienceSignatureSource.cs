using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceSignatureSource"/> over <c>0030</c>'s <c>agent_experience.experience_failure_signatures</c>.
/// It follows exactly the order <see cref="PostgresExperienceCandidateSource"/> uses -- validate the
/// request, check it against the host-established <see cref="AuthorizationContext"/>, and only then open a connection
/// and run parameterized SQL whose predicates apply the scope -- and translates failures the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>What runs in SQL.</b> The signature match (an exact key, or a tool and error class, in the caller's tenant, of
/// <see cref="ExperienceFailureSignature.SignatureVersion"/> only, through the table's two tenant-leading indexes),
/// then on the record: the scope predicate -- including any active sharing grant, through the same lateral join the
/// text channel uses -- the tombstone filter, the status filter, the confidence floor, the authorship exclusion when
/// the query asks for it (<c>0021</c>'s flag, failing closed on an unknown one), the order and the limit. A record is
/// returned once, however many of its signatures matched.
/// </para>
/// <para>
/// <b>Sharing grants and auditing</b> behave exactly as in <see cref="PostgresExperienceCandidateSource"/>: a granted
/// record is a candidate on the same terms as an owned one and says so; a role or database without the grant table
/// falls back to the exact scope once, reporting it through <c>onGrantsUnavailable</c>; and with
/// <see cref="ExperienceGrantAuditing"/> every grant-permitted record returned is recorded, in one statement.
/// </para>
/// <para>
/// <b>Under row-level security</b> the statement runs unchanged: <c>0030</c>'s read policy admits a signature row exactly
/// as <c>0019</c>'s admits its record. It needs <c>SELECT</c> on the record and signature tables (the privileges call
/// grants both) and, to honour sharing grants, on <c>experience_grants</c>. A database not migrated to <c>0030</c>, or a
/// role not yet granted the table, fails the search with <see cref="ExperienceStoreException"/>; it is never mistaken
/// for a missing grant table.
/// </para>
/// <para>
/// <b>Encrypted mode.</b> Signatures are stored in the clear, like the other search data; the records a search returns
/// are opened with their keys, and a record whose key was destroyed is never a candidate.
/// </para>
/// </remarks>
public sealed class PostgresExperienceSignatureSource : IExperienceSignatureSource
{
    /// <summary>The alias whether the record holds the whole key is selected under.</summary>
    internal const string ExactColumn = "signature_exact";

    /// <summary>The alias whether the matched signature was resolved is selected under.</summary>
    internal const string ResolvedColumn = "signature_resolved";

    /// <summary>
    /// The matching signature rows, one per record: whether it holds the exact key, and whether the matched signature
    /// (the exact one when there is one, else any coarse one) was resolved. <c>{0}</c> is the match predicate.
    /// </summary>
    private const string MatchedTemplate =
        "(SELECT s.experience_id AS matched_id, bool_or(s.signature_key = @signature_key) AS exact, " +
        "coalesce(bool_or(s.resolved) FILTER (WHERE s.signature_key = @signature_key), bool_or(s.resolved)) AS resolved " +
        $"FROM {PostgresExperienceRecordStore.FailureSignaturesTable} s " +
        "WHERE s.tenant_id = @tenant_id AND s.signature_version = @signature_version AND {0} " +
        "GROUP BY s.experience_id) m";

    /// <summary>An exact match: the whole key, through <c>(tenant_id, signature_key)</c>.</summary>
    internal const string ExactMatchPredicate = "s.signature_key = @signature_key";

    /// <summary>A coarse match: the tool and error class, through <c>(tenant_id, tool_name, error_class)</c>.</summary>
    internal const string CoarseMatchPredicate = "s.tool_name = @tool_name AND s.error_class = @error_class";

    private const string Columns =
        $"SELECT {ExperienceRecordSql.SelectColumns}, m.exact AS {ExactColumn}, m.resolved AS {ResolvedColumn}, ";

    private const string Filters =
        $" AND {ExperienceRecordSql.RecordLivePredicate} AND status = ANY(@statuses) AND reuse_confidence >= @min_confidence ";

    private const string ExcludingFilter = "AND " + PostgresExperienceCandidateSource.ModelAuthoredPredicate + " ";

    private const string OrderAndLimit =
        "ORDER BY m.exact DESC, m.resolved DESC, reuse_confidence DESC, created_at DESC, experience_id LIMIT @limit";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceSignatureCandidate> NoCandidates = [];

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresGrantSupport _grants;

    private readonly ExperienceGrantAuditing? _auditing;

    private readonly ExperienceEncryption? _encryption;

    /// <summary>Creates a signature source over a host-owned data source. The source never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="onGrantsUnavailable">Called at most once, when a search first finds <c>agent_experience.experience_grants</c> missing or unreadable and falls back to the exact-scope predicate. Optional.</param>
    /// <param name="auditing">Where to record the grant-permitted records this search returns, and what a failed recording does to it. <see langword="null"/> -- the default -- switches auditing off.</param>
    /// <param name="encryption">The deployment's crypto-shredding configuration, needed to open the sealed records a search returns. <see langword="null"/> -- the default -- is plaintext mode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceSignatureSource(
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

    /// <summary>The statement for a query: grant-aware or exact, exact or coarse match, with or without the authorship exclusion.</summary>
    internal static string Sql(bool withGrants, ExperienceSignatureMatch match, bool excludeModelAuthored)
    {
        var matched = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            MatchedTemplate,
            match == ExperienceSignatureMatch.Exact ? ExactMatchPredicate : CoarseMatchPredicate);
        var head = withGrants
            ? Columns + ExperienceRecordSql.SharedByGrantColumn + ", " + ExperienceRecordSql.PermittingGrantColumn + ", "
                + ExperienceRecordSql.PermittingDisclosureColumn
                + $" FROM {ExperienceRecordSql.Table} r JOIN {matched} ON m.matched_id = r.experience_id "
                + ExperienceRecordSql.PermittingGrantJoin + " WHERE " + ExperienceRecordSql.ReadableWithNamedGrantPredicate
            : Columns + "false AS " + ExperienceRecordSql.SharedByGrantAlias + ", NULL::uuid AS " + ExperienceRecordSql.PermittingGrantAlias
                + ", NULL::text AS " + ExperienceRecordSql.PermittingDisclosureAlias
                + $" FROM {ExperienceRecordSql.Table} r JOIN {matched} ON m.matched_id = r.experience_id WHERE "
                + ExperienceRecordSql.RecordScopePredicate;
        return head + Filters + (excludeModelAuthored ? ExcludingFilter : string.Empty) + OrderAndLimit;
    }

    /// <inheritdoc />
    public async Task<ExperienceSignatureSearchResult> FindBySignatureAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceSignatureQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateSignatureQuery(scope, query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, NoCandidates, errors);
        }

        if (!authorization.Permits(scope))
        {
            // Fail-closed, and before any connection opens: no search is issued at all.
            return new(ExperienceStoreOutcome.Denied, NoCandidates, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        ExperienceSignatureSearchResult result;
        IReadOnlyList<ExperienceGrantDisclosure?> disclosures;
        try
        {
            try
            {
                (result, disclosures) = await RunAsync(_grants.Available, authorization, scope, query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ConcernsSignatureTable(ex) && _grants.ShouldFallBack(ex, "signature search", cancellationToken))
            {
                // No grant table, or no permission to read it: search the exact scope only.
                (result, disclosures) = await RunAsync(withGrants: false, authorization, scope, query, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ExperienceStoreFailures.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw ExperienceStoreFailures.Translate(ex, "signature search", cancellationToken);
        }

        return _auditing is null
            ? result
            : await RecordGrantAccessAsync(authorization, scope, query, result, disclosures, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a failure is about the signature table itself -- not migrated, or not granted -- rather than the grant
    /// table: it must surface as a failed search, never latch the grant fallback.
    /// </summary>
    private static bool ConcernsSignatureTable(Exception ex) =>
        ex is PostgresException postgres
        && postgres.MessageText.Contains("experience_failure_signatures", StringComparison.Ordinal);

    private async Task<(ExperienceSignatureSearchResult Result, IReadOnlyList<ExperienceGrantDisclosure?> Disclosures)> RunAsync(
        bool withGrants,
        AuthorizationContext authorization,
        Scope scope,
        ExperienceSignatureQuery query,
        CancellationToken cancellationToken)
    {
        // Read into memory, and the connection returned, before any key is fetched, as the text channel does.
        List<SnapshotRow> rows;
        await using (var session = await AuthorizedTransaction.OpenAsync(_dataSource, authorization, cancellationToken).ConfigureAwait(false))
        {
            await using (var command = session.CreateCommand(Sql(withGrants, query.Match, query.ExcludeModelAuthored)))
            {
                var parameters = command.Parameters;
                ExperienceRecordParameters.AddScopeParameters(parameters, scope);
                var signature = query.Signature;
                parameters.Add(new NpgsqlParameter<string>("signature_key", NpgsqlDbType.Text) { TypedValue = signature.Key });
                parameters.Add(new NpgsqlParameter<string>("tool_name", NpgsqlDbType.Text) { TypedValue = signature.ToolName });
                parameters.Add(new NpgsqlParameter<string>("error_class", NpgsqlDbType.Text) { TypedValue = signature.ErrorClass });
                parameters.Add(new NpgsqlParameter<int>("signature_version", ExperienceFailureSignature.SignatureVersion));
                var statuses = query.EligibleStatuses.Distinct().Select(status => status.ToString()).ToArray();
                parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
                parameters.Add(new NpgsqlParameter<double>("min_confidence", query.MinimumConfidence));
                parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                rows = await SnapshotRow.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var candidates = new List<ExperienceSignatureCandidate>();
        var disclosures = new List<ExperienceGrantDisclosure?>();
        var records = await ExperienceRecordRows.ReadRecordsAsync(rows, _encryption, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            // A sealed record whose key was destroyed is erased: never a candidate.
            if (records[i] is not { } record)
            {
                continue;
            }

            var row = rows[i];
            var shared = ExperienceRecordRows.ReadSharedByGrant(row);
            var disclosure = ExperienceRecordRows.ReadPermittingDisclosure(row);
            candidates.Add(new ExperienceSignatureCandidate(
                record,
                ReadFlag(row, ExactColumn) ? ExperienceSignatureMatch.Exact : ExperienceSignatureMatch.Coarse,
                ReadFlag(row, ResolvedColumn) ? ExperienceSignatureResolution.Resolved : ExperienceSignatureResolution.Unresolved,
                shared,
                ExperienceRecordRows.ReadPermittingGrant(row))
            {
                GrantDisclosure = shared ? disclosure : null,
            });
            disclosures.Add(disclosure);
        }

        return (new(ExperienceStoreOutcome.Found, candidates, NoErrors), disclosures);
    }

    private static bool ReadFlag(System.Data.Common.DbDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    /// <summary>
    /// Appends one access row per grant-permitted record this search returns, in one statement, and decides what a
    /// failed append does to the result, exactly as <see cref="PostgresExperienceCandidateSource"/> does.
    /// </summary>
    private async Task<ExperienceSignatureSearchResult> RecordGrantAccessAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceSignatureQuery query,
        ExperienceSignatureSearchResult result,
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
            throw new ExperienceStoreException("The disclosure levels read do not line up with the candidates returned.");
        }

        var accesses = new List<ExperienceGrantAccess>();
        for (var i = 0; i < result.Candidates.Count; i++)
        {
            if (result.Candidates[i] is { SharedByGrant: true, Record: { } record } candidate)
            {
                accesses.Add(GrantAuditing.Access(
                    auditing,
                    authorization,
                    scope,
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
}
