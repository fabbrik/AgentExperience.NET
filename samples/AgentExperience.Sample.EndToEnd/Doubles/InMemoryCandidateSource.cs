using AgentExperience.Abstractions;

namespace AgentExperience.Sample.EndToEnd.Doubles;

/// <summary>
/// A demonstration double, not a search index and not durable: it scans the sample's in-memory
/// record store -- which dies with the process -- and scores each record by how many of the
/// request's words its task text repeats.
/// </summary>
/// <remarks>
/// <para>
/// It reads the same records the store holds, the way the PostgreSQL candidate source reads the
/// same table the record store writes. That is why the sample needs no separate "index this
/// record" step, and why the seven stages are identical in both modes.
/// </para>
/// <para>
/// The scope, status, and confidence filters below are the ones the real adapter applies in SQL.
/// Word overlap is not a text index: the real adapter ranks with PostgreSQL full-text search, so
/// the relevance numbers -- and therefore the ranked score -- differ between the two modes. Nothing
/// else does.
/// </para>
/// </remarks>
internal sealed class InMemoryCandidateSource(InMemoryRecordStore store) : IExperienceCandidateSource
{
    private static readonly char[] WordSeparators = [' ', '\t', '\n', '\r', '.', ',', ';', ':', '\'', '"', '(', ')', '-', '/'];

    /// <inheritdoc />
    public Task<ExperienceCandidateSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Denied, [], []));
        }

        var wanted = Words(query.TaskText);

        var candidates = store.Snapshot()
            .Where(record => record.Scope == query.Scope
                && query.EligibleStatuses.Contains(record.Status)
                && record.ReuseConfidence >= query.MinimumConfidence)
            .Select(record => new ExperienceCandidate(record, Relevance(record, wanted)))
            .Where(candidate => candidate.Relevance > 0d)
            .OrderByDescending(candidate => candidate.Relevance)
            .ThenBy(candidate => candidate.Record.ExperienceId)
            .Take(query.Limit)
            .ToList();

        return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, []));
    }

    private static double Relevance(ExperienceRecord record, HashSet<string> wanted)
    {
        if (wanted.Count == 0)
        {
            return 0d;
        }

        var have = Words(record.TaskId + " " + (record.TaskSummary ?? string.Empty));
        return (double)wanted.Count(have.Contains) / wanted.Count;
    }

    private static HashSet<string> Words(string text) =>
        new(text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.ToLowerInvariant()),
            StringComparer.Ordinal);
}
