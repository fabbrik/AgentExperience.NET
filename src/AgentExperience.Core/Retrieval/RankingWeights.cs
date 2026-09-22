using System.Globalization;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// The weight each normalized ranking component carries in a retrieved record's total score. Every
/// weight must be a finite, non-negative number, and the five must sum to 1 within
/// <see cref="SumTolerance"/>; anything else throws <see cref="ArgumentOutOfRangeException"/> at
/// construction, so an invalid set can never reach a retrieval call.
/// </summary>
/// <remarks>
/// <para>
/// This follows <see cref="AgentExperience.Core.Capture.CaptureLimits"/>'s convention of a validated
/// options record that refuses an invalid value outright, with one deliberate difference: the
/// properties are get-only rather than <c>init</c>. <c>CaptureLimits</c> re-validates in each
/// <c>init</c> accessor because each of its limits is independently valid or not. The sum-to-1 rule
/// here spans all five weights at once, and a <c>with</c> expression assigns them one at a time
/// <em>after</em> the copy constructor has already run -- there is no hook that could re-check the
/// sum afterwards. Read-only properties close that gap by construction: the constructor is the only
/// way to obtain an instance, and it validates everything. Build a different weighting with
/// <c>new RankingWeights(...)</c>.
/// </para>
/// <para>
/// Weights are the <em>effective</em> weights reported on every retrieved record alongside the
/// component they were applied to, so a host can always see why one record outranked another.
/// </para>
/// </remarks>
/// <param name="Relevance">Weight of how strongly the record's indexed text matched the task text.</param>
/// <param name="Confidence">Weight of the record's <see cref="AgentExperience.Abstractions.ExperienceRecord.ReuseConfidence"/>.</param>
/// <param name="Recency">Weight of how recently the record was last updated.</param>
/// <param name="Status">Weight of the record's lifecycle status among the eligible ones.</param>
/// <param name="EnvironmentCompatibility">Weight of the record's compatibility with the request's required environment attributes.</param>
public sealed record RankingWeights(
    double Relevance,
    double Confidence,
    double Recency,
    double Status,
    double EnvironmentCompatibility)
{
    /// <summary>
    /// How far the weights' sum may sit from 1 and still be accepted, so a set written as ordinary
    /// decimal literals is not rejected for binary floating-point rounding alone.
    /// </summary>
    public const double SumTolerance = 1e-6;

    /// <summary>
    /// The documented default weighting: relevance 0.35, confidence 0.25, recency 0.15, status 0.15,
    /// environment compatibility 0.10.
    /// </summary>
    public static RankingWeights Default { get; } = new(0.35, 0.25, 0.15, 0.15, 0.10);

    /// <summary>Weight of how strongly the record's indexed text matched the task text.</summary>
    public double Relevance { get; } = EnsureWeight(Relevance, nameof(Relevance));

    /// <summary>Weight of the record's reuse confidence.</summary>
    public double Confidence { get; } = EnsureWeight(Confidence, nameof(Confidence));

    /// <summary>Weight of how recently the record was last updated.</summary>
    public double Recency { get; } = EnsureWeight(Recency, nameof(Recency));

    /// <summary>Weight of the record's lifecycle status among the eligible ones.</summary>
    public double Status { get; } = EnsureWeight(Status, nameof(Status));

    /// <summary>Weight of the record's compatibility with the request's required environment attributes.</summary>
    public double EnvironmentCompatibility { get; } = EnsureWeight(EnvironmentCompatibility, nameof(EnvironmentCompatibility));

    /// <summary>
    /// The weights' sum. Declared last on purpose: property initializers run in declaration order, so
    /// every weight has already been checked to be finite and non-negative by the time the
    /// cross-property sum rule is applied, and a negative weight is reported as such rather than as a
    /// bad sum. This is also the only place the sum rule can run -- a record has no constructor body
    /// to put it in.
    /// </summary>
    public double Sum { get; } = EnsureSum(Relevance, Confidence, Recency, Status, EnvironmentCompatibility);

    private static double EnsureSum(double relevance, double confidence, double recency, double status, double environmentCompatibility)
    {
        var sum = relevance + confidence + recency + status + environmentCompatibility;
        if (Math.Abs(sum - 1d) > SumTolerance)
        {
            // The out-of-range value is the sum itself, not any one weight, so that is what the
            // exception names: no single constructor parameter is at fault.
            throw new ArgumentOutOfRangeException(
                nameof(Sum),
                sum,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Ranking weights must sum to 1 within {0}; this set sums to {1}.",
                    SumTolerance.ToString("R", CultureInfo.InvariantCulture),
                    sum.ToString("R", CultureInfo.InvariantCulture)));
        }

        return sum;
    }

    private static double EnsureWeight(double value, string paramName) =>
        double.IsFinite(value) && value >= 0d
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "Ranking weights must be finite and non-negative.");
}
