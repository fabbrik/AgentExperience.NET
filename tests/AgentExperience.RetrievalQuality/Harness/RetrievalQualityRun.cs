using AgentExperience.Storage.Conformance;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>What one query's search returned, by corpus record ID, strongest match first.</summary>
/// <param name="Query">The query.</param>
/// <param name="Returned">The corpus IDs of the candidates, in the order the source returned them.</param>
internal sealed record QueryOutcome(CorpusQuery Query, IReadOnlyList<string> Returned);

/// <summary>One adapter's run over the whole corpus.</summary>
/// <param name="Adapter">The adapter's name in the report.</param>
/// <param name="Corpus">The corpus that was seeded and searched.</param>
/// <param name="Outcomes">One outcome per query, in corpus order.</param>
internal sealed record RetrievalQualityResult(string Adapter, RetrievalCorpus Corpus, IReadOnlyList<QueryOutcome> Outcomes);

/// <summary>
/// Seeds the corpus into a record store and runs every query through a candidate source's
/// <see cref="IExperienceCandidateSource.SearchAsync"/>, directly: no retrieval service, so no clock, environment
/// compatibility or ranking policy is mixed into what is measured. The numbers are lexical recall, nothing more.
/// </summary>
internal static class RetrievalQualityRun
{
    /// <summary>The candidate limit of every search, the port's default today. Stated here, so a change to the default is not a change to the benchmark.</summary>
    public const int Limit = 50;

    /// <summary>The confidence floor of every search. The corpus places some ineligible records just below it.</summary>
    public const double MinimumConfidence = 0.5;

    /// <summary>
    /// Seeds <paramref name="corpus"/> into a fresh tenant of <paramref name="store"/> and searches it with every query.
    /// A fresh tenant keeps two runs over one database apart, and the tenant is not part of anything reported.
    /// </summary>
    public static async Task<RetrievalQualityResult> RunAsync(
        string adapter,
        RetrievalCorpus corpus,
        IExperienceRecordStore store,
        IExperienceCandidateSource source,
        CancellationToken cancellationToken = default)
    {
        var tenant = ConformanceData.NewTenant();
        var authorization = ConformanceData.Authorize(tenant);
        var queryScope = ConformanceData.Scope(tenant);
        var otherScope = ConformanceData.Scope(tenant, project: "project-2");

        foreach (var record in corpus.Records)
        {
            var created = await store.CreateAsync(
                authorization,
                ConformanceData.Record(
                    record.Scope == RetrievalCorpus.QueryScope ? queryScope : otherScope,
                    record.Status,
                    record.ExperienceId,
                    record.TaskId,
                    record.Summary,
                    record.Lesson,
                    record.Confidence),
                cancellationToken);
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException(
                    $"Seeding {record.Id} returned {created.Outcome}: {string.Join("; ", created.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
            }
        }

        var byExperienceId = corpus.Records.ToDictionary(r => r.ExperienceId, r => r.Id);
        var outcomes = new List<QueryOutcome>();
        foreach (var query in corpus.Queries)
        {
            var result = await source.SearchAsync(
                authorization,
                new ExperienceCandidateQuery(queryScope, query.Text, ExperienceStatuses.EligibleForReuse, MinimumConfidence, Limit),
                cancellationToken);
            if (result.Outcome != ExperienceStoreOutcome.Found)
            {
                throw new InvalidOperationException(
                    $"Query {query.Id} returned {result.Outcome}: {string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
            }

            outcomes.Add(new QueryOutcome(
                query,
                [.. result.Candidates.Select(c => byExperienceId.TryGetValue(c.Record.ExperienceId, out var id)
                    ? id
                    : throw new InvalidOperationException($"Query {query.Id} returned {c.Record.ExperienceId}, which is not a corpus record."))]));
        }

        return new RetrievalQualityResult(adapter, corpus, outcomes);
    }
}
