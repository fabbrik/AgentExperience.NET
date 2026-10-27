using AgentExperience.Core.Holdout;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 25.2: the benefit estimator. Every interval is pinned to 6 decimals against an independent reference
/// computation (Python's <c>decimal</c> at 50 digits, Wilson bounds in Newcombe's closed form
/// (2np + z² ∓ z√(z² + 4npq)) / 2(n + z²), then method 10 of Newcombe 1998). Its published example, 56/70 against
/// 48/80, gives 0.0524 to 0.3339.
/// </summary>
public class ExperienceBenefitEstimatorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid LessonA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid LessonB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ---- The math -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(40, 50, 10, 50, 0.6, 0.4156274798, 0.7238320748)]
    [InlineData(10, 50, 40, 50, -0.6, -0.7238320748, -0.4156274798)]
    [InlineData(5, 5, 0, 5, 1.0, 0.3855490057, 1.0)]
    [InlineData(26, 50, 24, 50, 0.04, -0.1507527331, 0.2267167034)]
    [InlineData(50, 50, 0, 50, 1.0, 0.8990992577, 1.0)]
    [InlineData(0, 50, 50, 50, -1.0, -1.0, -0.8990992577)]
    [InlineData(56, 70, 48, 80, 0.2, 0.0524314724, 0.3338726540)]
    [InlineData(1, 1, 1, 1, 0.0, -0.7934506856, 0.7934506856)]
    [InlineData(3, 7, 2, 9, 0.2063492063, -0.2165429010, 0.5645418310)]
    public void The_Newcombe_interval_matches_the_reference_computation(
        int s1, int n1, int s2, int n2, double difference, double lower, double upper)
    {
        var (d, l, u) = ExperienceBenefitEstimator.Newcombe(s1, n1, s2, n2);
        Assert.Equal(difference, d, 6);
        Assert.Equal(lower, l, 6);
        Assert.Equal(upper, u, 6);
        Assert.InRange(l, -1d, 1d);
        Assert.InRange(u, -1d, 1d);
        Assert.True(l <= d && d <= u);
    }

    [Theory]
    [InlineData(0, 10, 0.0, 0.2775327999)]
    [InlineData(10, 10, 0.7224672001, 1.0)]
    [InlineData(7, 20, 0.1811918241, 0.5671457233)]
    public void The_Wilson_interval_matches_the_reference_computation(int successes, int n, double lower, double upper)
    {
        var (l, u) = ExperienceBenefitEstimator.Wilson(successes, n);
        Assert.Equal(lower, l, 6);
        Assert.Equal(upper, u, 6);
    }

    // ---- The I/O matrix -----------------------------------------------------------------------------------------------

    [Fact]
    public void No_rows_is_all_zero_and_no_evidence()
    {
        var estimate = ExperienceBenefitEstimator.Estimate([]);

        AssertCounts(estimate.Overall, 0, 0, 0, 0);
        Assert.Null(estimate.Overall.Treatment.Rate);
        Assert.Null(estimate.Overall.Difference);
        Assert.Null(estimate.Overall.Lower);
        Assert.Null(estimate.Overall.Upper);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
        Assert.Equal((0, 0, 0), (estimate.Overall.Unverified, estimate.Overall.Pending, estimate.Overall.MixedArm));
        Assert.Empty(estimate.Lessons);
        Assert.True(estimate.Tracked);
        Assert.Equal(ExperienceStoreOutcome.Found, estimate.Outcome);
        Assert.Equal(ExperienceBenefitOptions.DefaultMinimumRunsPerArm, estimate.MinimumRunsPerArm);
    }

    [Fact]
    public void A_clear_benefit_helps()
    {
        var estimate = ExperienceBenefitEstimator.Estimate(Runs(40, 50, 10, 50));

        AssertCounts(estimate.Overall, 50, 40, 50, 10);
        Assert.Equal(0.8, estimate.Overall.Treatment.Rate!.Value, 10);
        Assert.Equal(0.2, estimate.Overall.Holdout.Rate!.Value, 10);
        Assert.Equal(0.6, estimate.Overall.Difference!.Value, 10);
        Assert.Equal(0.415627, estimate.Overall.Lower!.Value, 6);
        Assert.Equal(0.723832, estimate.Overall.Upper!.Value, 6);
        Assert.True(estimate.Overall.Lower > 0);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);
    }

    [Fact]
    public void A_clear_harm_hurts()
    {
        var estimate = ExperienceBenefitEstimator.Estimate(Runs(10, 50, 40, 50));

        Assert.Equal(-0.723832, estimate.Overall.Lower!.Value, 6);
        Assert.Equal(-0.415627, estimate.Overall.Upper!.Value, 6);
        Assert.Equal(ExperienceBenefitVerdict.Hurts, estimate.Overall.Verdict);
    }

    [Fact]
    public void Too_few_runs_is_no_evidence_with_the_rates_still_reported()
    {
        var estimate = ExperienceBenefitEstimator.Estimate(Runs(5, 5, 0, 5), new ExperienceBenefitOptions { MinimumRunsPerArm = 20 });

        AssertCounts(estimate.Overall, 5, 5, 5, 0);
        Assert.Equal(1.0, estimate.Overall.Treatment.Rate);
        Assert.Equal(0.0, estimate.Overall.Holdout.Rate);
        Assert.Equal(0.385549, estimate.Overall.Lower!.Value, 6);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);

        // The same rows with a lower threshold do support a claim.
        Assert.Equal(
            ExperienceBenefitVerdict.Helps,
            ExperienceBenefitEstimator.Estimate(Runs(5, 5, 0, 5), new ExperienceBenefitOptions { MinimumRunsPerArm = 5 }).Overall.Verdict);
    }

    [Fact]
    public void An_interval_that_spans_zero_is_no_evidence()
    {
        var estimate = ExperienceBenefitEstimator.Estimate(Runs(26, 50, 24, 50));

        Assert.Equal(-0.150753, estimate.Overall.Lower!.Value, 6);
        Assert.Equal(0.226717, estimate.Overall.Upper!.Value, 6);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
    }

    [Fact]
    public void Edge_rates_give_a_finite_interval_inside_minus_one_to_one()
    {
        var estimate = ExperienceBenefitEstimator.Estimate(Runs(50, 50, 0, 50));

        Assert.Equal(1.0, estimate.Overall.Difference);
        Assert.True(double.IsFinite(estimate.Overall.Lower!.Value) && double.IsFinite(estimate.Overall.Upper!.Value));
        Assert.InRange(estimate.Overall.Lower.Value, -1d, 1d);
        Assert.InRange(estimate.Overall.Upper.Value, -1d, 1d);
        Assert.Equal(0.899099, estimate.Overall.Lower.Value, 6);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);
    }

    [Fact]
    public void Unknown_pending_and_mixed_arm_runs_are_left_out_of_the_rates_and_counted()
    {
        var rows = Runs(40, 50, 10, 50).ToList();
        rows.Add(Row(Guid.NewGuid(), HoldoutArm.Treatment, LessonA, TaskVerificationStatus.Unknown));
        rows.Add(Row(Guid.NewGuid(), HoldoutArm.Holdout, LessonA, TaskVerificationStatus.Unknown));
        rows.Add(Row(Guid.NewGuid(), HoldoutArm.Holdout, LessonA, null));
        var mixed = Guid.NewGuid();
        rows.Add(Row(mixed, HoldoutArm.Treatment, LessonA, TaskVerificationStatus.Verified));
        rows.Add(Row(mixed, HoldoutArm.Holdout, LessonB, TaskVerificationStatus.Verified));

        var estimate = ExperienceBenefitEstimator.Estimate(rows);

        AssertCounts(estimate.Overall, 50, 40, 50, 10);
        Assert.Equal((2, 1, 1), (estimate.Overall.Unverified, estimate.Overall.Pending, estimate.Overall.MixedArm));
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);

        // Lesson B was named only by the mixed run.
        var b = Assert.Single(estimate.Lessons, lesson => lesson.ExperienceId == LessonB).Comparison;
        AssertCounts(b, 0, 0, 0, 0);
        Assert.Equal(1, b.MixedArm);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, b.Verdict);
    }

    [Fact]
    public void Each_lesson_is_compared_over_the_runs_whose_blocks_named_it()
    {
        // Lesson A is in every run: treatment 40/50, holdout 10/50. Lesson B only in the first 20 runs of each arm, of
        // which the first 20 treatment runs verified and the first 10 holdout runs verified.
        var rows = new List<HoldoutOutcomeRow>();
        for (var i = 0; i < 50; i++)
        {
            var treatment = RunId(HoldoutArm.Treatment, i);
            var holdout = RunId(HoldoutArm.Holdout, i);
            var treatmentOutcome = i < 40 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed;
            var holdoutOutcome = i < 10 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed;
            rows.Add(Row(treatment, HoldoutArm.Treatment, LessonA, treatmentOutcome));
            rows.Add(Row(holdout, HoldoutArm.Holdout, LessonA, holdoutOutcome));
            if (i < 20)
            {
                rows.Add(Row(treatment, HoldoutArm.Treatment, LessonB, treatmentOutcome));
                rows.Add(Row(holdout, HoldoutArm.Holdout, LessonB, holdoutOutcome));
            }
        }

        var estimate = ExperienceBenefitEstimator.Estimate(rows);

        AssertCounts(estimate.Overall, 50, 40, 50, 10);
        Assert.Equal([LessonA, LessonB], estimate.Lessons.Select(lesson => lesson.ExperienceId));
        AssertCounts(estimate.Lessons[0].Comparison, 50, 40, 50, 10);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Lessons[0].Comparison.Verdict);
        var b = estimate.Lessons[1].Comparison;
        AssertCounts(b, 20, 20, 20, 10);
        Assert.Equal(0.5, b.Difference!.Value, 10);
        Assert.Equal(ExperienceBenefitVerdict.Helps, b.Verdict);

        // Pure and deterministic: the same rows in another order give the same estimate.
        var reversed = ExperienceBenefitEstimator.Estimate(Enumerable.Reverse(rows));
        Assert.Equal(estimate.Overall, reversed.Overall);
        Assert.Equal(estimate.Lessons, reversed.Lessons);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_minimum_below_one_is_refused(int minimum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceBenefitEstimator.Estimate([], new ExperienceBenefitOptions { MinimumRunsPerArm = minimum }));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private static void AssertCounts(ExperienceBenefitComparison comparison, int treatmentRuns, int treatmentSuccesses, int holdoutRuns, int holdoutSuccesses)
    {
        Assert.Equal(new ExperienceBenefitArm(treatmentRuns, treatmentSuccesses), comparison.Treatment);
        Assert.Equal(new ExperienceBenefitArm(holdoutRuns, holdoutSuccesses), comparison.Holdout);
    }

    /// <summary>One row per run, every run naming <see cref="LessonA"/>, the first <c>successes</c> of each arm verified.</summary>
    private static IEnumerable<HoldoutOutcomeRow> Runs(int treatmentSuccesses, int treatmentRuns, int holdoutSuccesses, int holdoutRuns)
    {
        for (var i = 0; i < treatmentRuns; i++)
        {
            yield return Row(RunId(HoldoutArm.Treatment, i), HoldoutArm.Treatment, LessonA, i < treatmentSuccesses ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed);
        }

        for (var i = 0; i < holdoutRuns; i++)
        {
            yield return Row(RunId(HoldoutArm.Holdout, i), HoldoutArm.Holdout, LessonA, i < holdoutSuccesses ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed);
        }
    }

    private static Guid RunId(HoldoutArm arm, int i) => new($"{(int)arm + 1:x8}-0000-0000-0000-{i:x12}");

    private static HoldoutOutcomeRow Row(Guid run, HoldoutArm arm, Guid lesson, TaskVerificationStatus? outcome) => new(run, arm, lesson, At, outcome);
}
