using AgentExperience.Abstractions;
using AgentExperience.Storage.InMemory;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// An <see cref="IExperienceCandidateSource"/> over one <see cref="SqliteExperienceRecordStore"/>'s records, through
/// SQLite's FTS5 full-text index.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it matches.</b> A query's terms are read exactly as the in-memory source reads them: its distinct words
/// (Unicode form KC, case-folded, runs of letters and digits), less the English stopwords PostgreSQL's <c>english</c>
/// configuration drops and any term shorter than two characters. Each term is then looked up in the FTS5 index over the
/// record's task ID, task summary and reflection lesson, which tokenizes with <c>unicode61</c> and stems with FTS5's
/// Porter stemmer -- so "invoices" matches "invoice", as in PostgreSQL and unlike the in-memory source. A record matches
/// when at least <see cref="ExperienceCandidateQuery.MinimumMatchedTerms"/> of the terms, capped at their number, are in
/// any of the three fields; a query with no terms left matches nothing.
/// </para>
/// <para>
/// <b>Relevance</b> is the in-memory source's coverage: each term counts three when it is in the summary, one in the
/// task ID and one in the lesson, out of five per query term, so it lies in (0, 1]. Candidates are ranked strongest
/// first, then by ID, after the filters (exact scope, eligible status, minimum confidence, model authorship), and only
/// then cut to the limit. Its values are not comparable with the PostgreSQL source's.
/// </para>
/// <para>
/// <b>Term coverage</b> (<see cref="ExperienceCandidate.TermCoverage"/>) is the plain share of the query's terms the record
/// holds in any field, as the in-memory source counts it, except that a term matches on its Porter stem.
/// </para>
/// <para>There are no grants here: a search returns only the requester's own records.</para>
/// </remarks>
public sealed class SqliteExperienceCandidateSource : IExperienceCandidateSource
{
    private const int SummaryWeight = 3;
    private const int TaskIdWeight = 1;
    private const int LessonWeight = 1;
    private const int TermWeight = SummaryWeight + TaskIdWeight + LessonWeight;

    private static readonly string[] Columns = ["summary", "task_id", "lesson"];

    private readonly SqliteExperienceRecordStore _store;

    /// <summary>Creates a candidate source over <paramref name="store"/>'s records.</summary>
    /// <param name="store">The record store whose records are searched. A committed write is visible to the next search.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public SqliteExperienceCandidateSource(SqliteExperienceRecordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<ExperienceCandidateSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateCandidateQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Denied, [], []));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceCandidateSearchResult>(cancellationToken);
        }

        var terms = SearchText.QueryTerms(query.TaskText);
        if (terms.Count == 0)
        {
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, [], []));
        }

        var required = Math.Min(query.MinimumMatchedTerms, terms.Count);
        return _store.Database.RunAsync(
            "The SQLite candidate search failed.",
            connection => SearchCoreAsync(connection, query, terms, required, cancellationToken),
            cancellationToken);
    }

    private static async Task<ExperienceCandidateSearchResult> SearchCoreAsync(
        SqliteConnection connection,
        ExperienceCandidateQuery query,
        IReadOnlyList<string> terms,
        int required,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: true);

        var filterParameters = new List<(string, object?)>(ScopeParameters(query.Scope))
        {
            ("$minimum", query.MinimumConfidence),
            ("$exclude", query.ExcludeModelAuthored ? 1 : 0),
        };
        var statusNames = new List<string>();
        foreach (var (status, i) in query.EligibleStatuses.Distinct().Select((status, i) => (status, i)))
        {
            statusNames.Add($"$status{i}");
            filterParameters.Add(($"$status{i}", status.ToString()));
        }

        var filter =
            $"r.tenant_id = $tenant_id AND r.application_id = $application_id AND r.project_id = $project_id "
            + "AND r.team_id IS $team_id AND r.agent_id IS $agent_id AND r.user_id IS $user_id "
            + $"AND r.deleted_at IS NULL AND r.status IN ({string.Join(", ", statusNames)}) "
            + "AND r.reuse_confidence >= $minimum AND ($exclude = 0 OR r.model_authored = 0)";

        // Per record: for each term, which fields hold it (bit 0 summary, bit 1 task ID, bit 2 lesson).
        var hits = new Dictionary<string, int[]>(StringComparer.Ordinal);
        for (var t = 0; t < terms.Count; t++)
        {
            var phrase = "\"" + terms[t].Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            for (var c = 0; c < Columns.Length; c++)
            {
                using var command = Command(
                    connection,
                    transaction,
                    "SELECT m.experience_id FROM experience_search s "
                    + "JOIN experience_search_rows m ON m.search_rowid = s.rowid "
                    + "JOIN experience_records r ON r.experience_id = m.experience_id "
                    + $"WHERE experience_search MATCH $match AND {filter}",
                    [("$match", $"{{{Columns[c]}}} : {phrase}"), .. filterParameters]);
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    if (!hits.TryGetValue(id, out var fields))
                    {
                        hits[id] = fields = new int[terms.Count];
                    }

                    fields[t] |= 1 << c;
                }
            }
        }

        var ranked = hits
            .Where(hit => hit.Value.Count(fields => fields != 0) >= required)
            .Select(hit => (Id: Guid.Parse(hit.Key), Relevance: Relevance(hit.Value), Coverage: (double)hit.Value.Count(fields => fields != 0) / hit.Value.Length))
            .OrderByDescending(hit => hit.Relevance)
            .ThenBy(hit => hit.Id)
            .Take(query.Limit)
            .ToList();

        var candidates = new List<ExperienceCandidate>(ranked.Count);
        foreach (var (id, relevance, coverage) in ranked)
        {
            using var command = Command(
                connection,
                transaction,
                $"SELECT {SqliteExperienceRecordStore.RecordColumns} FROM experience_records WHERE experience_id = $id",
                ("$id", Id(id)));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && SqliteExperienceRecordStore.ReadRecord(reader) is { } record)
            {
                // The plain share of the query's terms, before the field weighting, as the in-memory source reports it.
                candidates.Add(new ExperienceCandidate(record, relevance) { TermCoverage = coverage });
            }
        }

        transaction.Commit();
        return new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, []);
    }

    private static double Relevance(int[] fields)
    {
        var score = 0;
        foreach (var held in fields)
        {
            score += ((held & 1) != 0 ? SummaryWeight : 0)
                + ((held & 2) != 0 ? TaskIdWeight : 0)
                + ((held & 4) != 0 ? LessonWeight : 0);
        }

        return (double)score / (TermWeight * fields.Length);
    }
}
