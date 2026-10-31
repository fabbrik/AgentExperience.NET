using AgentExperience.Storage.Conformance;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>What one query's search returned, by corpus record ID, strongest match first.</summary>
/// <param name="Query">The query.</param>
/// <param name="Returned">The corpus IDs of the candidates, in the order the source returned them.</param>
/// <param name="Coverage">
/// Story 28.5: each candidate's <see cref="ExperienceCandidate.TermCoverage"/>, parallel to <paramref name="Returned"/>, for a
/// text query; <see langword="null"/> for a signature lookup.
/// </param>
internal sealed record QueryOutcome(CorpusQuery Query, IReadOnlyList<string> Returned, IReadOnlyList<double>? Coverage = null);

/// <summary>Story 28.2: what one signature query's two lookups returned, by corpus record ID, in the source's order.</summary>
/// <param name="Query">The signature query.</param>
/// <param name="Exact">The exact match's candidates.</param>
/// <param name="Coarse">The coarse match's (tool and error class) candidates.</param>
internal sealed record SignatureOutcome(CorpusSignatureQuery Query, QueryOutcome Exact, QueryOutcome Coarse);

/// <summary>One adapter's run over the whole corpus.</summary>
/// <param name="Adapter">The adapter's name in the report.</param>
/// <param name="Corpus">The corpus that was seeded and searched.</param>
/// <param name="Outcomes">One outcome per query, in corpus order.</param>
/// <param name="Signatures">Story 28.2: one outcome per signature query, in corpus order.</param>
internal sealed record RetrievalQualityResult(
    string Adapter,
    RetrievalCorpus Corpus,
    IReadOnlyList<QueryOutcome> Outcomes,
    IReadOnlyList<SignatureOutcome> Signatures);

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
        IExperienceSignatureSource signatureSource,
        CancellationToken cancellationToken = default)
    {
        var (authorization, queryScope) = await SeedAsync(corpus, store, cancellationToken);

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
                    : throw new InvalidOperationException($"Query {query.Id} returned {c.Record.ExperienceId}, which is not a corpus record."))],
                [.. result.Candidates.Select(c => c.TermCoverage
                    ?? throw new InvalidOperationException($"Query {query.Id}'s candidate {c.Record.ExperienceId} carries no term coverage."))]));
        }

        return new RetrievalQualityResult(
            adapter, corpus, outcomes, await SignatureOutcomesAsync(corpus, authorization, queryScope, signatureSource, cancellationToken));
    }

    /// <summary>
    /// Story 28.2: every signature query, looked up exactly and coarsely through
    /// <see cref="IExperienceSignatureSource.FindBySignatureAsync"/>, directly, with the same statuses, floor and limit as
    /// the text queries.
    /// </summary>
    public static async Task<IReadOnlyList<SignatureOutcome>> SignatureOutcomesAsync(
        RetrievalCorpus corpus,
        AuthorizationContext authorization,
        Scope queryScope,
        IExperienceSignatureSource source,
        CancellationToken cancellationToken = default)
    {
        var byExperienceId = corpus.Records.ToDictionary(r => r.ExperienceId, r => r.Id);
        var outcomes = new List<SignatureOutcome>();
        foreach (var query in corpus.SignatureQueries)
        {
            outcomes.Add(new SignatureOutcome(
                query,
                await LookUpAsync(query, ExperienceSignatureMatch.Exact),
                await LookUpAsync(query, ExperienceSignatureMatch.Coarse)));
        }

        return outcomes;

        async Task<QueryOutcome> LookUpAsync(CorpusSignatureQuery query, ExperienceSignatureMatch match)
        {
            var result = await source.FindBySignatureAsync(
                authorization,
                queryScope,
                new ExperienceSignatureQuery(query.Signature, ExperienceStatuses.EligibleForReuse, MinimumConfidence, match, Limit),
                cancellationToken);
            if (result.Outcome != ExperienceStoreOutcome.Found)
            {
                throw new InvalidOperationException(
                    $"Signature query {query.Id} returned {result.Outcome}: {string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
            }

            return new QueryOutcome(
                query.AsQuery(),
                [.. result.Candidates.Select(c => byExperienceId.TryGetValue(c.Record.ExperienceId, out var id)
                    ? id
                    : throw new InvalidOperationException($"Signature query {query.Id} returned {c.Record.ExperienceId}, which is not a corpus record."))]);
        }
    }

    /// <summary>
    /// Seeds <paramref name="corpus"/> into a fresh tenant of <paramref name="store"/>: query-scope records into the scope
    /// every query searches, the rest into a sibling project. Returns the authorization and the query scope.
    /// </summary>
    public static async Task<(AuthorizationContext Authorization, Scope QueryScope)> SeedAsync(
        RetrievalCorpus corpus,
        IExperienceRecordStore store,
        CancellationToken cancellationToken = default)
    {
        var tenant = ConformanceData.NewTenant();
        var authorization = ConformanceData.Authorize(tenant);
        var queryScope = ConformanceData.Scope(tenant);
        var otherScope = ConformanceData.Scope(tenant, project: "project-2");

        foreach (var record in corpus.Records)
        {
            var seeded = ConformanceData.Record(
                record.Scope == RetrievalCorpus.QueryScope ? queryScope : otherScope,
                record.Status,
                record.ExperienceId,
                record.TaskId,
                record.Summary,
                record.Lesson,
                record.Confidence);

            // Story 28.2: the failed call the record's run made, if the corpus gives it one; the text search indexes
            // none of it, so the text queries' numbers do not move.
            if (corpus.Failures.SingleOrDefault(f => f.Record == record.Id) is { } failure)
            {
                seeded = WithFailure(seeded, failure);
            }

            var created = await store.CreateAsync(authorization, seeded, cancellationToken);
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException(
                    $"Seeding {record.Id} returned {created.Outcome}: {string.Join("; ", created.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
            }
        }

        return (authorization, queryScope);
    }

    /// <summary>
    /// <paramref name="record"/> as a verified run whose first attempt's call to the failure's tool failed with its error
    /// and, when the failure was resolved, whose second attempt's call to the same tool succeeded.
    /// </summary>
    private static ExperienceRecord WithFailure(ExperienceRecord record, CorpusFailure failure)
    {
        ToolCallRecord Call(string? error) => new(
            Guid.NewGuid(), 0, failure.Tool, new Dictionary<string, object?>(), ConformanceData.Time, TimeSpan.FromSeconds(1), error is null ? "ok" : null, error);

        Attempt Attempt(int sequence, ToolCallRecord call) =>
            new(Guid.NewGuid(), sequence, ConformanceData.Time, TimeSpan.FromSeconds(1), [call], null, null);

        return record with
        {
            Attempts = failure.Resolved
                ? [Attempt(0, Call(failure.Error)), Attempt(1, Call(null))]
                : [Attempt(0, Call(failure.Error))],
            Outcome = record.Outcome with { Status = TaskVerificationStatus.Verified },
        };
    }
}
