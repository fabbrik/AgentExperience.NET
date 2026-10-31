namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// Story 28.5: the injection decision under a match floor. A query's injected set at text-coverage floor <c>f</c> is its
/// candidates, in the source's order, whose term coverage (the share of the query's distinct terms they contain) is at
/// least <c>f</c>, cut to the first 8: what <c>ExperienceInjectionOptions.MinimumMatch</c> with that
/// <c>MinimumTextCoverage</c> would let reach the block at <c>MaxRecords = 8</c>, with no vector channel. A floor of 0 is
/// the block without one, so its row repeats the overall metrics.
/// </summary>
internal static class InjectionFloorSweep
{
    /// <summary>The floors swept, fixed in the story's rule before any run.</summary>
    public static IReadOnlyList<double> Floors { get; } = [0d, 0.3, 0.4, 0.5, 0.6];

    /// <summary>The comparison tolerance, the injection floor's own (<c>ExperienceMatchFloor.Tolerance</c>).</summary>
    public const double Tolerance = 1e-6;

    /// <summary>What reaches the block for <paramref name="outcome"/> at <paramref name="floor"/>.</summary>
    public static QueryOutcome Apply(QueryOutcome outcome, double floor)
    {
        var coverage = outcome.Coverage ?? throw new InvalidOperationException($"{outcome.Query.Id} carries no coverage.");
        return new QueryOutcome(
            outcome.Query,
            [.. outcome.Returned.Where((_, index) => coverage[index] >= floor - Tolerance).Take(8)],
            null);
    }

    /// <summary>Every query of <paramref name="result"/> at <paramref name="floor"/>, in corpus order.</summary>
    public static IReadOnlyList<QueryOutcome> Apply(RetrievalQualityResult result, double floor) =>
        [.. result.Outcomes.Select(outcome => Apply(outcome, floor))];
}
