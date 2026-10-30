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
/// signature, so this leg never applies to it, and a coarse recall match does not count;</description></item>
/// <item><description>its text coverage (<see cref="RankedExperience.TextCoverage"/>: the share of the request's distinct
/// terms it contains) is at least <see cref="MinimumTextCoverage"/>;</description></item>
/// <item><description>its semantic similarity (<see cref="RankedExperience.VectorRelevance"/>) is at least
/// <see cref="MinimumSemanticSimilarity"/>, or, when that is <see langword="null"/>, the retrieval policy's
/// <see cref="RetrievalPolicy.MinimumVectorRelevance"/>. With neither set, any record the vector channel found passes this
/// leg: set one when the vector channel is on.</description></item>
/// </list>
/// <para>
/// Values are compared with a tolerance of <see cref="Tolerance"/>, so a coverage the database computed in single
/// precision (three of ten terms as 0.3000000119) is not lost to rounding at a floor of 0.3.
/// </para>
/// </remarks>
public sealed record ExperienceMatchFloor
{
    /// <summary>
    /// The <see cref="MinimumTextCoverage"/> a new floor starts with: the lowest floor at which the retrieval benchmark's noise
    /// stops falling. No floor met the benchmark's precision target, so none is on by default.
    /// </summary>
    public const double DefaultMinimumTextCoverage = 0.3;

    /// <summary>The tolerance every comparison against the floor allows.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// The smallest share of the request's distinct terms a record must contain to pass on its text match, in [0, 1].
    /// Defaults to <see cref="DefaultMinimumTextCoverage"/>. 0 lets every record the text channel found pass.
    /// </summary>
    public double MinimumTextCoverage
    {
        get;
        init => field = value is >= 0d and <= 1d
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MinimumTextCoverage), value, "The minimum text coverage must be between 0 and 1 inclusive.");
    } = DefaultMinimumTextCoverage;

    /// <summary>
    /// The smallest vector relevance, <c>(1 + cosine) / 2</c> in [0, 1], a record must have to pass on its semantic match,
    /// or <see langword="null"/> (the default) for the retrieval policy's <see cref="RetrievalPolicy.MinimumVectorRelevance"/>.
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
    /// floor is <paramref name="policyVectorFloor"/>.
    /// </summary>
    internal bool Passes(RankedExperience ranked, double? policyVectorFloor)
    {
        if (ranked.TextCoverage is { } coverage && AtLeast(coverage, MinimumTextCoverage))
        {
            return true;
        }

        if (ranked.VectorRelevance is { } similarity)
        {
            return (MinimumSemanticSimilarity ?? policyVectorFloor) is not { } floor || AtLeast(similarity, floor);
        }

        return false;
    }

    /// <summary>The content-free omission detail for a record that did not pass.</summary>
    internal string Detail(RankedExperience ranked, double? policyVectorFloor)
    {
        var text = ranked.TextCoverage is { } coverage
            ? $"text coverage {Number(coverage)} is below {Number(MinimumTextCoverage)}"
            : "no text match";
        var semantic = ranked.VectorRelevance is { } similarity
            ? $"semantic similarity {Number(similarity)} is below {Number(MinimumSemanticSimilarity ?? policyVectorFloor ?? 0d)}"
            : "no semantic match";
        return $"Below the match floor: {text}, {semantic}, and no exact failure-signature match.";
    }

    /// <summary>The omission detail of a recall candidate whose signature matched only coarsely.</summary>
    internal const string CoarseSignatureDetail =
        "Below the match floor: the failure signature matched only coarsely, and a recall hint has no text or semantic match.";

    private static bool AtLeast(double value, double floor) => !double.IsNaN(value) && value >= floor - Tolerance;

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
