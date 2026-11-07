using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>The two columns of one hybrid run: the text channel alone, and text plus vectors, over the same seeded store.</summary>
/// <param name="Text">The text channel's result, exactly what the in-memory golden measures.</param>
/// <param name="Hybrid">The merged result of the text and vector channels.</param>
/// <param name="Embedded">How many records were embedded: the eligible ones in the query scope.</param>
internal sealed record HybridRetrievalResult(RetrievalQualityResult Text, RetrievalQualityResult Hybrid, int Embedded);

/// <summary>
/// The opt-in hybrid column (story 21.3): seeds the corpus into the in-memory store, embeds every record a search could
/// return through the in-memory embedding index, and runs every query through both channels, the text one exactly as
/// <see cref="RetrievalQualityRun"/> does.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "hybrid" means here.</b> Core's merge rule and nothing more: the two channels' candidates are deduplicated by
/// experience ID, a record found by both keeps the higher of its two relevances, and the list is ordered by that
/// relevance, then by ID, and cut to <see cref="RetrievalQualityRun.Limit"/>. Core's other ranking components (confidence,
/// recency, status, environment) are left out, as the text column leaves them out, so the two columns differ only by the
/// vector channel.
/// </para>
/// <para>
/// <b>Embedding.</b> Only records a search could return are embedded (an eligible status and the confidence floor, in the
/// query scope), as Core's indexing embeds only those, and only their sanitized retrieval summary
/// (<see cref="ExperienceRetrievalSummary"/>). Each query's task text is embedded once.
/// </para>
/// </remarks>
internal static class HybridRetrievalRun
{
    /// <summary>
    /// The vector channel's floor: the one-call setup's default minimum cosine similarity, 0.5, as relevance
    /// <c>(1 + 0.5) / 2</c>. The column measures what the setup does by default.
    /// </summary>
    public const double MinimumVectorRelevance = 0.75;

    /// <summary>How many summaries one provider call embeds.</summary>
    private const int BatchSize = 64;

    public static async Task<HybridRetrievalResult> RunAsync(
        string adapter,
        RetrievalCorpus corpus,
        IExperienceEmbeddingGenerator generator,
        CancellationToken cancellationToken = default)
    {
        var store = new InMemoryExperienceRecordStore();
        var source = new InMemoryExperienceCandidateSource(store);
        var index = new InMemoryExperienceEmbeddingIndex(store);
        var (authorization, queryScope) = await RetrievalQualityRun.SeedAsync(corpus, store, cancellationToken);
        var embedded = await EmbedAsync(authorization, queryScope, index, generator, cancellationToken);

        var byExperienceId = corpus.Records.ToDictionary(r => r.ExperienceId, r => r.Id);
        string Id(ExperienceCandidate candidate, string queryId) =>
            byExperienceId.TryGetValue(candidate.Record.ExperienceId, out var id)
                ? id
                : throw new InvalidOperationException($"Query {queryId} returned {candidate.Record.ExperienceId}, which is not a corpus record.");

        var text = new List<QueryOutcome>();
        var hybrid = new List<QueryOutcome>();
        foreach (var query in corpus.Queries)
        {
            var lexical = await source.SearchAsync(
                authorization,
                new ExperienceCandidateQuery(queryScope, query.Text, ExperienceStatuses.EligibleForReuse, RetrievalQualityRun.MinimumConfidence, RetrievalQualityRun.Limit),
                cancellationToken);
            Ensure(lexical.Outcome == ExperienceStoreOutcome.Found, query.Id, lexical.Outcome.ToString());

            var vector = await index.SearchAsync(
                authorization,
                new ExperienceVectorQuery(
                    queryScope,
                    generator.ModelId,
                    await generator.GenerateAsync(query.Text, cancellationToken),
                    ExperienceStatuses.EligibleForReuse,
                    RetrievalQualityRun.MinimumConfidence,
                    RetrievalQualityRun.Limit),
                cancellationToken);
            Ensure(vector.Outcome == ExperienceVectorSearchOutcome.Found, query.Id, vector.Outcome.ToString());

            text.Add(new QueryOutcome(query, [.. lexical.Candidates.Select(c => Id(c, query.Id))], [.. lexical.Candidates.Select(c => c.TermCoverage ?? 0d)]));
            var aboveFloor = vector.Candidates.Where(c => c.Relevance >= MinimumVectorRelevance).ToList();
            var merged = Merge(lexical.Candidates, aboveFloor);

            // Story 28.5: a record the vector channel found above the policy's floor passes the injection floor's semantic
            // leg (MinimumSemanticSimilarity defaults to that floor), so it counts as full strength; the rest by their text
            // coverage.
            var semantic = aboveFloor.Select(c => c.Record.ExperienceId).ToHashSet();
            var coverage = lexical.Candidates.ToDictionary(c => c.Record.ExperienceId, c => c.TermCoverage ?? 0d);
            hybrid.Add(new QueryOutcome(
                query,
                [.. merged.Select(c => Id(c, query.Id))],
                [.. merged.Select(c => semantic.Contains(c.Record.ExperienceId) ? 1d : coverage.GetValueOrDefault(c.Record.ExperienceId))]));
        }

        // Story 28.2: the signature channel does not depend on vectors, so both columns carry the same lookups.
        var signatures = await RetrievalQualityRun.SignatureOutcomesAsync(
            corpus, authorization, queryScope, new InMemoryExperienceSignatureSource(store), cancellationToken);
        return new HybridRetrievalResult(
            new RetrievalQualityResult(adapter, corpus, text, signatures),
            new RetrievalQualityResult(adapter + " + vectors", corpus, hybrid, signatures),
            embedded);
    }

    /// <summary>
    /// Core's own merge (<c>ExperienceRetrievalService.MergeChannels</c>: one entry per record, keeping the higher
    /// relevance); then strongest first, ties by ID, cut to the limit.
    /// </summary>
    internal static IReadOnlyList<ExperienceCandidate> Merge(IReadOnlyList<ExperienceCandidate> text, IReadOnlyList<ExperienceCandidate> vector) =>
        [.. ExperienceRetrievalService.MergeChannels(text, vector)
            .OrderByDescending(candidate => candidate.Relevance)
            .ThenBy(candidate => candidate.Record.ExperienceId)
            .Take(RetrievalQualityRun.Limit)];

    /// <summary>Embeds every record in the query scope a search could return, a batch at a time, through the index's own scan.</summary>
    private static async Task<int> EmbedAsync(
        AuthorizationContext authorization,
        Scope scope,
        IExperienceEmbeddingIndex index,
        IExperienceEmbeddingGenerator generator,
        CancellationToken cancellationToken)
    {
        var embedded = 0;
        Guid? after = null;
        while (true)
        {
            var scan = await index.ScanAsync(
                authorization,
                new ExperienceIndexScan(scope, generator.ModelId, ExperienceStatuses.EligibleForReuse, RetrievalQualityRun.MinimumConfidence, Limit: BatchSize, StartAfterId: after),
                cancellationToken);
            Ensure(scan.Outcome == ExperienceStoreOutcome.Found, "index scan", scan.Outcome.ToString());
            if (scan.Targets.Count == 0)
            {
                return embedded;
            }

            var vectors = await generator.GenerateBatchAsync([.. scan.Targets.Select(target => target.Summary)], cancellationToken);
            for (var i = 0; i < scan.Targets.Count; i++)
            {
                var target = scan.Targets[i];
                var written = await index.WriteAsync(
                    authorization,
                    new ExperienceIndexWrite(
                        scope,
                        target.ExperienceId,
                        new ExperienceEmbeddingDescriptor(
                            generator.ModelId,
                            generator.Dimension,
                            ExperienceEmbeddingDescriptor.ComputeContentHash(generator.ModelId, target.Summary),
                            target.SourceRevision),
                        vectors[i]),
                    cancellationToken);
                Ensure(written.Outcome == ExperienceIndexOutcome.Written, target.ExperienceId.ToString(), written.Outcome.ToString());
                embedded++;
            }

            after = scan.LastExaminedId;
        }
    }

    private static void Ensure(bool condition, string what, string outcome)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{what} returned {outcome}.");
        }
    }
}
