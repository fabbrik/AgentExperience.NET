using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// An <see cref="IExperienceCandidateSource"/> that searches the records of one
/// <see cref="InMemoryExperienceRecordStore"/> by their words. <b>For development and tests only.</b> Its data is
/// the record store's, which is lost when the process ends, and none of the PostgreSQL guarantees apply (append-only
/// enforcement, erasure reach, backups, the two database roles, crypto-shredding).
/// </summary>
/// <remarks>
/// <para>
/// <b>How it matches.</b> Text is normalized to Unicode form KC and case-folded, then split into words: runs of
/// letters and digits, with a combining mark continuing the word it follows. A query's terms are its distinct words,
/// less a built-in list of common English stopwords (the Snowball list PostgreSQL's <c>english</c> configuration
/// drops: "a", "and", "the", "to", "with", ...) and less any term shorter than two characters. A record matches only
/// when <em>every</em> remaining term is a word of its <see cref="ExperienceRecord.TaskId"/>,
/// <see cref="ExperienceRecord.TaskSummary"/> or reflection lesson, as PostgreSQL's <c>websearch_to_tsquery</c> ANDs
/// its terms; a query with no terms left matches nothing. Each record's words are taken once, when it is created, from
/// at most the first 100,000 characters of that text, the bound PostgreSQL's search column applies.
/// </para>
/// <para>
/// <b>Relevance.</b> The fraction of the query's terms the record contains, weighted towards the task summary: each
/// term counts three when it is in the summary, one when it is in the task ID and one when it is in the lesson, out
/// of five. It lies in (0, 1] for every match, and is 1 when every term is in all three fields.
/// </para>
/// <para>
/// <b>Order and filters.</b> Only records in exactly the query's scope, in one of its eligible statuses, and at or
/// above its minimum confidence are considered; they are ranked strongest first, then by ID, as the PostgreSQL
/// candidate source orders ties, and only then cut to the limit.
/// </para>
/// <para>
/// <b>Not PostgreSQL-compatible.</b> This is not full-text search: there is no stemming ("invoices" does not match
/// "invoice") and no query syntax, and its relevance values are not comparable with the PostgreSQL candidate source's
/// <c>ts_rank_cd</c> values. Ranking built on them (Core's retrieval score) therefore differs between the two stores
/// for the same records, even where both return the same candidates.
/// </para>
/// <para>
/// There are no grants here: a search returns only the requester's own records, and
/// <see cref="ExperienceCandidate.SharedByGrant"/> is never set.
/// </para>
/// </remarks>
public sealed class InMemoryExperienceCandidateSource : IExperienceCandidateSource
{
    private const int SummaryWeight = 3;
    private const int TaskIdWeight = 1;
    private const int LessonWeight = 1;
    private const int TermWeight = SummaryWeight + TaskIdWeight + LessonWeight;

    private readonly InMemoryExperienceRecordStore _store;

    /// <summary>
    /// Creates a candidate source over <paramref name="store"/>'s records. <b>For development and tests only</b>: data
    /// is lost when the process ends, and none of the PostgreSQL guarantees apply.
    /// </summary>
    /// <param name="store">The record store whose records are searched. A committed write is visible to the next search.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public InMemoryExperienceCandidateSource(InMemoryExperienceRecordStore store)
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

        var statuses = query.EligibleStatuses.ToHashSet();
        var candidates = _store
            .Select(query.Scope, record => statuses.Contains(record.Status) && record.ReuseConfidence >= query.MinimumConfidence)
            .Where(indexed => terms.All(indexed.Text.Contains))
            .Select(indexed => new ExperienceCandidate(indexed.Record, Relevance(indexed.Text, terms)))
            .OrderByDescending(candidate => candidate.Relevance)
            .ThenBy(candidate => candidate.Record.ExperienceId)
            .Take(query.Limit)
            .ToList();

        return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, []));
    }

    private static double Relevance(RecordSearchText text, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            score += (text.Summary.Contains(term) ? SummaryWeight : 0)
                + (text.TaskId.Contains(term) ? TaskIdWeight : 0)
                + (text.Lesson.Contains(term) ? LessonWeight : 0);
        }

        return (double)score / (TermWeight * terms.Count);
    }
}
