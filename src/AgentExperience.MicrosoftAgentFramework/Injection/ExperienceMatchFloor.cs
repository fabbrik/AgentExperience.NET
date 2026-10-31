using AgentExperience.Core.Retrieval;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The injection-time match-strength floor (<see cref="ExperienceInjectionOptions.MinimumMatch"/>): a record is injected
/// only when its match is strong enough to be worth the context. Separate from retrieval's candidate floors, which it
/// never changes: retrieval still returns what it returns, and a record below this floor is omitted from the block as
/// <see cref="InjectionOmissionReason.WeakMatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// A record passes when <em>at least one</em> of these holds:
/// </para>
/// <list type="bullet">
/// <item><description>a failure-signature match, exact, on the current run: a recall hint
/// (<see cref="ExperienceInjectionOptions.Recall"/>) found by an exact signature lookup. The before-run block has no
/// signature, so this leg never applies to it; with a floor set, recall never asks for a coarse match;</description></item>
/// <item><description>its text coverage (<see cref="RankedExperience.TextCoverage"/>: the share of the request's distinct
/// terms it contains) is at least <see cref="MinimumTextCoverage"/>;</description></item>
/// <item><description>its semantic similarity (<see cref="RankedExperience.VectorRelevance"/>) is at least
/// <see cref="MinimumSemanticSimilarity"/>, or, when that is <see langword="null"/>, the retrieval policy's
/// <see cref="RetrievalPolicy.MinimumVectorRelevance"/>. With neither set, this leg is off: a record the vector channel
/// found passes only on its text. A <see cref="MinimumSemanticSimilarity"/> below the policy's floor does nothing, since
/// retrieval never returns a vector candidate below the policy's floor.</description></item>
/// </list>
/// <para>
/// <b>Coverage is adapter-relative.</b> What a term is depends on the text source: whole, unstemmed words in memory;
/// stemmed lexemes on PostgreSQL, with a hyphenated compound counted once; Porter-stemmed words on SQLite. The same
/// request can therefore have a different term count, and a record a different coverage, on each store. No value is
/// recommended: on the retrieval benchmark no floor met the precision target fixed before measuring (see
/// docs/benchmarks.md), so the floor is off by default and has no default coverage.
/// </para>
/// <para>
/// Values are compared with a tolerance of <see cref="Tolerance"/>, so a coverage the database computed in single
/// precision (three of ten terms as 0.3000000119) is not lost to rounding at a floor of 0.3.
/// </para>
/// </remarks>
public sealed record ExperienceMatchFloor
{
    /// <summary>The tolerance every comparison against the floor allows.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// Required: the smallest share of the request's distinct terms a record must contain to pass on its text match, in
    /// [0, 1]. 0 lets every record the text channel found pass. Relative to the store's terms (see the remarks).
    /// </summary>
    public required double MinimumTextCoverage
    {
        get;
        init => field = value is >= 0d and <= 1d
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MinimumTextCoverage), value, "The minimum text coverage must be between 0 and 1 inclusive.");
    }

    /// <summary>
    /// The smallest vector relevance, <c>(1 + cosine) / 2</c> in [0, 1], a record must have to pass on its semantic match,
    /// or <see langword="null"/> (the default) for the retrieval policy's <see cref="RetrievalPolicy.MinimumVectorRelevance"/>;
    /// with neither set, the semantic leg is off.
    /// </summary>
    public double? MinimumSemanticSimilarity
    {
        get;
        init => field = value is not { } floor || floor is >= 0d and <= 1d
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MinimumSemanticSimilarity), value, "The minimum semantic similarity must be between 0 and 1 inclusive.");
    }

    /// <summary>
    /// Whether <paramref name="ranked"/> passes the floor, without a signature match, under a retrieval policy whose vector
    /// floor is <paramref name="policyVectorFloor"/>. Fails closed: with no semantic floor anywhere, the semantic leg is off.
    /// </summary>
    internal bool Passes(RankedExperience ranked, double? policyVectorFloor)
    {
        if (ranked.TextCoverage is { } coverage && AtLeast(coverage, MinimumTextCoverage))
        {
            return true;
        }

        return ranked.VectorRelevance is { } similarity
            && (MinimumSemanticSimilarity ?? policyVectorFloor) is { } floor
            && AtLeast(similarity, floor);
    }

    /// <summary>The content-free omission detail for a record that did not pass.</summary>
    internal string Detail(RankedExperience ranked, double? policyVectorFloor)
    {
        var text = ranked.TextCoverage is { } coverage
            ? $"text coverage {Number(coverage)} is below {Number(MinimumTextCoverage)}"
            : "no text match";
        var semantic = (ranked.VectorRelevance, MinimumSemanticSimilarity ?? policyVectorFloor) switch
        {
            (null, _) => "no semantic match",
            ({ } similarity, { } floor) => $"semantic similarity {Number(similarity)} is below {Number(floor)}",
            (_, null) => "no semantic floor is set, so a semantic match does not count",
        };
        return $"Below the match floor: {text}, {semantic}, and no exact failure-signature match.";
    }

    private static bool AtLeast(double value, double floor) => !double.IsNaN(value) && value >= floor - Tolerance;

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
