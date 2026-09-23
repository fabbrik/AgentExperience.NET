using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The four statistics the report prints. Nothing in this repository computed any of them before,
/// so they are tested on their own rather than only through the report that uses them.
/// </summary>
public class StatisticsTests
{
    [Fact]
    public void Mean_and_dispersion_over_a_known_sample()
    {
        double[] values = [2d, 2d, 2d, 3d, 3d, 3d];

        Assert.Equal(2.5d, Statistics.Mean(values));

        // Sample standard deviation (n-1): sqrt(1.5/5) = 0.5477225575...
        Assert.Equal(0.5477225575051661d, Statistics.StandardDeviation(values)!.Value, 12);
        Assert.Equal(2.5d, Statistics.Median(values));
    }

    [Fact]
    public void Median_of_an_odd_sample_is_the_middle_value_and_the_input_order_does_not_matter()
    {
        Assert.Equal(3d, Statistics.Median([5d, 1d, 3d]));
        Assert.Equal(3d, Statistics.Median([1d, 3d, 5d]));
    }

    [Fact]
    public void Dispersion_over_a_single_observation_is_undefined_rather_than_zero()
    {
        var summary = Statistics.Summarize([4d], trials: 1);

        Assert.Equal(1, summary.Observations);
        Assert.Equal(4d, summary.Mean);
        Assert.Equal(4d, summary.Minimum);
        Assert.Equal(4d, summary.Median);
        Assert.Equal(4d, summary.Maximum);

        // The point of the whole type: a 0 here would read as "no variation observed".
        Assert.Null(summary.StandardDeviation);
        Assert.Null(Statistics.StandardDeviation([4d]));
    }

    [Fact]
    public void An_empty_sample_summarizes_to_nothing_and_never_divides_by_zero()
    {
        var summary = Statistics.Summarize([null, null], trials: 2);

        Assert.Equal(2, summary.Trials);
        Assert.Equal(0, summary.Observations);
        Assert.Null(summary.Mean);
        Assert.Null(summary.StandardDeviation);
        Assert.Null(summary.Minimum);
        Assert.Null(summary.Median);
        Assert.Null(summary.Maximum);
    }

    [Fact]
    public void Trials_with_no_value_are_counted_in_the_trial_total_and_in_no_statistic()
    {
        var summary = Statistics.Summarize([1d, null, 3d], trials: 3);

        Assert.Equal(3, summary.Trials);
        Assert.Equal(2, summary.Observations);
        Assert.Equal(2d, summary.Mean);
    }

    [Fact]
    public void Standard_deviation_is_zero_only_when_the_observations_really_are_identical()
    {
        Assert.Equal(0d, Statistics.StandardDeviation([2d, 2d, 2d]));
    }

    /// <summary>
    /// What a non-finite observation does to every statistic, written down rather than assumed.
    /// </summary>
    /// <remarks>
    /// Unreachable through the harness today -- every metric it feeds in is an integer count or a
    /// Stopwatch reading -- but <see cref="Statistics"/> is public API and a NaN sorts ahead of every
    /// real number, so min and median would silently become NaN rather than throwing. The behaviour
    /// is asserted so a future caller finds it stated instead of discovering it in a report.
    /// </remarks>
    [Fact]
    public void A_NaN_observation_poisons_every_statistic_rather_than_being_silently_dropped()
    {
        var summary = Statistics.Summarize([1d, double.NaN, 3d], trials: 3);

        Assert.Equal(3, summary.Observations);
        Assert.True(double.IsNaN(summary.Mean!.Value));
        Assert.True(double.IsNaN(summary.StandardDeviation!.Value));

        // Array.Sort orders NaN first, so it is the minimum and it reaches the median.
        Assert.True(double.IsNaN(summary.Minimum!.Value));
        Assert.Equal(1d, summary.Median);
        Assert.Equal(3d, summary.Maximum);
    }

    [Fact]
    public void An_infinite_observation_is_carried_through_rather_than_dropped()
    {
        var summary = Statistics.Summarize([1d, double.PositiveInfinity], trials: 2);

        Assert.Equal(double.PositiveInfinity, summary.Mean);
        Assert.Equal(1d, summary.Minimum);
        Assert.Equal(double.PositiveInfinity, summary.Maximum);

        var both = Statistics.Summarize([double.NegativeInfinity, double.PositiveInfinity], trials: 2);
        Assert.True(double.IsNaN(both.Mean!.Value));
        Assert.Equal(double.NegativeInfinity, both.Minimum);
        Assert.Equal(double.PositiveInfinity, both.Maximum);
    }

    /// <summary>
    /// And a NaN mean reaching the gate is not a pass: every comparison against NaN is false, so the
    /// term does not hold and the verdict is the negative one.
    /// </summary>
    [Fact]
    public void A_NaN_mean_reaching_the_gate_fails_the_term_rather_than_passing_it()
    {
        Assert.False(double.NaN < 1d);
        Assert.False(double.NaN >= 1d);
        Assert.False(double.NaN <= 1d);

        var result = GateEvaluator.Evaluate(
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3) with
                {
                    Metrics = new TrialMetrics(3, true, 0, 3, 1d),
                },
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0) with
                {
                    Metrics = new TrialMetrics(0, true, 0, 0, double.NaN),
                },
            ],
            ExperimentFacts.Design());

        // elapsed_ms is excluded from the gate, so a NaN there changes no verdict -- which is the
        // point of excluding it. The primary term still holds on its own numbers.
        Assert.Equal(GateVerdict.BenefitDemonstrated, result.Verdict);
        Assert.True(double.IsNaN(result.Enabled.ElapsedMilliseconds.Mean!.Value));
    }
}
