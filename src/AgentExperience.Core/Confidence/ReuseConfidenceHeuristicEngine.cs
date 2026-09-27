namespace AgentExperience.Core.Confidence;

/// <summary>
/// The default <see cref="IExperienceConfidenceEngine"/>: <see cref="ReuseConfidenceHeuristic.Score"/>,
/// <c>(1 + S) / (2 + S + F)</c>, under <see cref="ReuseConfidenceHeuristic.RuleVersion"/>.
/// </summary>
/// <remarks>
/// It is what the lifecycle service scores with when no host engine is supplied, and it reproduces the
/// library's behaviour before the engine was replaceable bit for bit: the same scores, and the plain
/// <c>"1.0.0"</c> recorded as each update's rule version.
/// </remarks>
public sealed class ReuseConfidenceHeuristicEngine : IExperienceConfidenceEngine
{
    /// <summary>The default engine's <see cref="RuleId"/>. A host engine may not use it.</summary>
    public const string HeuristicRuleId = "reuse-heuristic";

    private ReuseConfidenceHeuristicEngine()
    {
    }

    /// <summary>The one instance.</summary>
    public static ReuseConfidenceHeuristicEngine Instance { get; } = new();

    /// <inheritdoc/>
    public string RuleId => HeuristicRuleId;

    /// <inheritdoc/>
    public string RuleVersion => ReuseConfidenceHeuristic.RuleVersion;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Either counter is negative.</exception>
    public double Score(ExperienceConfidenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReuseConfidenceHeuristic.Score(input.SupportingValidations, input.Contradictions);
    }
}
