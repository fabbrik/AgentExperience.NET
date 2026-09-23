namespace AgentExperience.ReuseBaseline.Harness;

/// <summary>
/// One metric summarised over one condition. Every statistic is <see langword="null"/> when it is
/// undefined for the sample that produced it, never a zero that reads like a measurement.
/// </summary>
/// <param name="Trials">How many trials ran under this condition, including the ones with no value.</param>
/// <param name="Observations">How many of them had a value for this metric.</param>
/// <param name="Mean">The arithmetic mean, or <see langword="null"/> when there is nothing to average.</param>
/// <param name="StandardDeviation">
/// The sample standard deviation (Bessel-corrected, <c>n-1</c>), or <see langword="null"/> when
/// <see cref="Observations"/> is below two. Dispersion over a single observation is undefined, and
/// reporting it as <c>0</c> would read as "no variation observed".
/// </param>
/// <param name="Minimum">The smallest observation, or <see langword="null"/>.</param>
/// <param name="Median">The middle observation, or the mean of the two middle ones for an even count. <see langword="null"/> when there are none.</param>
/// <param name="Maximum">The largest observation, or <see langword="null"/>.</param>
public sealed record MetricSummary(
    int Trials,
    int Observations,
    double? Mean,
    double? StandardDeviation,
    double? Minimum,
    double? Median,
    double? Maximum);

/// <summary>
/// Mean, sample standard deviation, and min/median/max.
/// </summary>
/// <remarks>
/// Written here rather than taken from a package because nothing in this repository computes any of
/// them, and four functions over a list of doubles is a smaller thing to own than a dependency. The
/// behaviour that matters is at the edges: an empty sample summarises to nothing, and a
/// single-observation sample has no dispersion.
/// </remarks>
public static class Statistics
{
    /// <summary>Summarises <paramref name="values"/> over a condition that ran <paramref name="trials"/> trials.</summary>
    /// <param name="values">The observations. Entries with no value are counted in <paramref name="trials"/> and excluded from every statistic.</param>
    /// <param name="trials">How many trials the condition ran in total.</param>
    public static MetricSummary Summarize(IEnumerable<double?> values, int trials)
    {
        ArgumentNullException.ThrowIfNull(values);

        var observed = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();

        if (observed.Length == 0)
        {
            // Every statistic undefined. This is the "all trials in one condition failed" case, and
            // it must not divide by zero and must not look like a measured zero.
            return new MetricSummary(trials, 0, null, null, null, null, null);
        }

        Array.Sort(observed);

        return new MetricSummary(
            trials,
            observed.Length,
            Mean(observed),
            StandardDeviation(observed),
            observed[0],
            MedianOfSorted(observed),
            observed[^1]);
    }

    /// <summary>The arithmetic mean, or <see langword="null"/> for an empty sample.</summary>
    /// <param name="values">The observations.</param>
    public static double? Mean(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return null;
        }

        var total = 0d;
        foreach (var value in values)
        {
            total += value;
        }

        return total / values.Count;
    }

    /// <summary>
    /// The sample standard deviation, or <see langword="null"/> when there are fewer than two
    /// observations.
    /// </summary>
    /// <param name="values">The observations.</param>
    public static double? StandardDeviation(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return null;
        }

        var mean = Mean(values)!.Value;
        var sumOfSquares = 0d;
        foreach (var value in values)
        {
            var deviation = value - mean;
            sumOfSquares += deviation * deviation;
        }

        return Math.Sqrt(sumOfSquares / (values.Count - 1));
    }

    /// <summary>The median, or <see langword="null"/> for an empty sample.</summary>
    /// <param name="values">The observations, in any order.</param>
    public static double? Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        return MedianOfSorted(sorted);
    }

    private static double MedianOfSorted(double[] sorted) => sorted.Length % 2 == 1
        ? sorted[sorted.Length / 2]
        : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d;
}
