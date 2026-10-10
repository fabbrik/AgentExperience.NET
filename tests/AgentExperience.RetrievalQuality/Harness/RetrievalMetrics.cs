namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// The retrieval measures over a set of query outcomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recall@k</b> is the share of a query's expected records in its first k candidates; <b>MRR</b> is the reciprocal
/// rank of its first expected record, or 0 when none is returned. Both are averaged over the queries that expect a
/// record (<see cref="Labelled"/>), since neither means anything for a query that expects none, and are
/// <see langword="null"/> when there are none.
/// </para>
/// <para>
/// <b>Precision@k</b> is the share of the first k candidates that are expected records, averaged over the labelled
/// queries that got a candidate (a query with none has no precision; recall already counts it), and
/// <see langword="null"/> when there are none. <b>False positives@8</b> is the number of candidates in the first 8 that
/// no label expects, averaged over every query, including those that expect nothing: the noise an agent would be shown.
/// Unlike a precision divided by k, it rises when partial matching adds distractors. <b>Zero-candidate share</b> is the
/// share of all queries that got no candidate at all.
/// </para>
/// <para>
/// k = 8 is the injection provider's default <c>MaxRecords</c>: what an agent would actually be shown.
/// </para>
/// </remarks>
/// <param name="Queries">The number of queries.</param>
/// <param name="Labelled">The number of queries that expect at least one record.</param>
/// <param name="RecallAt1">Mean recall@1 over the labelled queries.</param>
/// <param name="RecallAt3">Mean recall@3 over the labelled queries.</param>
/// <param name="RecallAt8">Mean recall@8 over the labelled queries.</param>
/// <param name="PrecisionAt3">Mean precision@3 over the labelled queries that got a candidate.</param>
/// <param name="PrecisionAt8">Mean precision@8 over the labelled queries that got a candidate.</param>
/// <param name="FalsePositivesAt8">Mean number of unexpected candidates in the first 8, over every query.</param>
/// <param name="MeanReciprocalRank">Mean reciprocal rank over the labelled queries.</param>
/// <param name="ZeroCandidates">The number of queries that got no candidate.</param>
internal sealed record RetrievalMetrics(
    int Queries,
    int Labelled,
    double? RecallAt1,
    double? RecallAt3,
    double? RecallAt8,
    double? PrecisionAt3,
    double? PrecisionAt8,
    double FalsePositivesAt8,
    double? MeanReciprocalRank,
    int ZeroCandidates)
{
    /// <summary>The share of queries that got no candidate.</summary>
    public double ZeroCandidateShare => Queries == 0 ? 0d : (double)ZeroCandidates / Queries;

    /// <summary>The measures over <paramref name="outcomes"/>.</summary>
    public static RetrievalMetrics Of(IReadOnlyList<QueryOutcome> outcomes)
    {
        var labelled = outcomes.Where(o => o.Query.Expected.Count > 0).ToList();
        var answered = labelled.Where(o => o.Returned.Count > 0).ToList();
        return new RetrievalMetrics(
            outcomes.Count,
            labelled.Count,
            MeanOrNull(labelled, o => Recall(o, 1)),
            MeanOrNull(labelled, o => Recall(o, 3)),
            MeanOrNull(labelled, o => Recall(o, 8)),
            MeanOrNull(answered, o => Precision(o, 3)),
            MeanOrNull(answered, o => Precision(o, 8)),
            outcomes.Count == 0 ? 0d : outcomes.Average(o => FalsePositives(o, 8)),
            MeanOrNull(labelled, ReciprocalRank),
            outcomes.Count(o => o.Returned.Count == 0));
    }

    /// <summary>The 1-based rank of <paramref name="id"/> in <paramref name="outcome"/>, or <see langword="null"/> when it was not returned.</summary>
    public static int? Rank(QueryOutcome outcome, string id)
    {
        var index = outcome.Returned.ToList().IndexOf(id);
        return index < 0 ? null : index + 1;
    }

    /// <summary>The expected records of <paramref name="outcome"/> that are not in its first <paramref name="k"/> candidates.</summary>
    public static IReadOnlyList<string> Misses(QueryOutcome outcome, int k) =>
        [.. outcome.Query.Expected.Where(id => Rank(outcome, id) is not { } rank || rank > k)];

    private static int Hits(QueryOutcome outcome, int k) => outcome.Returned.Take(k).Count(outcome.Query.Expected.Contains);

    private static double Recall(QueryOutcome outcome, int k) => (double)Hits(outcome, k) / outcome.Query.Expected.Count;

    private static double Precision(QueryOutcome outcome, int k) => (double)Hits(outcome, k) / Math.Min(k, outcome.Returned.Count);

    private static int FalsePositives(QueryOutcome outcome, int k) => Math.Min(k, outcome.Returned.Count) - Hits(outcome, k);

    private static double ReciprocalRank(QueryOutcome outcome)
    {
        var first = outcome.Returned.Select((id, index) => (id, index)).FirstOrDefault(p => outcome.Query.Expected.Contains(p.id));
        return first.id is null ? 0d : 1d / (first.index + 1);
    }

    private static double? MeanOrNull(IReadOnlyList<QueryOutcome> outcomes, Func<QueryOutcome, double> measure) =>
        outcomes.Count == 0 ? null : outcomes.Average(measure);
}
