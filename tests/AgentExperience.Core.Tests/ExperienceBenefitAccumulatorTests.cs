using AgentExperience.Core.Holdout;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 25.10: the estimator's incremental form. Folding rows run by run gives exactly the pure form's estimate, on
/// randomised inputs that exercise every run class (both arms, mixed arms, verified, failed, unknown and pending, rows
/// whose outcomes disagree within a run, repeated rows), while holding counters rather than rows.
/// </summary>
public class ExperienceBenefitAccumulatorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 20)]
    [InlineData(4, 1)]
    [InlineData(5, 5)]
    [InlineData(6, 2)]
    [InlineData(7, 10)]
    [InlineData(8, 1)]
    public void Folding_run_by_run_gives_the_pure_estimate_on_randomised_rows(int seed, int minimum)
    {
        var random = new Random(seed);
        var lessons = Enumerable.Range(0, random.Next(1, 12)).Select(_ => NewGuid(random)).ToArray();
        var rows = new List<HoldoutOutcomeRow>();
        var runs = random.Next(0, 400);
        for (var run = 0; run < runs; run++)
        {
            var runId = NewGuid(random);
            var arm = random.Next(2) == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            var outcome = Outcome(random);
            foreach (var lesson in lessons.Where(_ => random.Next(3) == 0).DefaultIfEmpty(lessons[0]))
            {
                // Now and then a run straddles both arms, carries another outcome on one row, or repeats a row.
                var rowArm = random.Next(25) == 0 ? Other(arm) : arm;
                var rowOutcome = random.Next(10) == 0 ? Outcome(random) : outcome;
                rows.Add(new HoldoutOutcomeRow(runId, rowArm, lesson, At, rowOutcome));
                if (random.Next(30) == 0)
                {
                    rows.Add(new HoldoutOutcomeRow(runId, rowArm, lesson, At, Outcome(random)));
                }
            }
        }

        var options = new ExperienceBenefitOptions { MinimumRunsPerArm = minimum };
        var shuffled = rows.OrderBy(_ => random.Next()).ToList();
        var expected = ExperienceBenefitEstimator.Estimate(shuffled, options);

        // As the port pages them: by run, then record (a stable sort keeps repeated rows in the pure form's order).
        var paged = new ExperienceBenefitAccumulator(options);
        paged.AddRange(shuffled.OrderBy(row => row.RunId).ThenBy(row => row.ExperienceId));
        AssertSame(expected, paged.ToEstimate());

        // Grouped by run but in any order, runs and rows alike, with nulls among them.
        var grouped = new ExperienceBenefitAccumulator(options);
        foreach (var run in shuffled.GroupBy(row => row.RunId).OrderBy(_ => random.Next()))
        {
            grouped.Add(null);
            foreach (var row in run.OrderBy(row => row.ExperienceId))
            {
                grouped.Add(row);
            }
        }

        AssertSame(expected, grouped.ToEstimate());
    }

    [Fact]
    public void A_large_scope_folds_to_the_pure_estimate()
    {
        // 50,000 rows over 10,000 runs, five lessons each, from 40 lessons.
        var random = new Random(25_10);
        var lessons = Enumerable.Range(0, 40).Select(_ => NewGuid(random)).ToArray();
        var rows = new List<HoldoutOutcomeRow>(50_000);
        for (var run = 0; run < 10_000; run++)
        {
            var runId = NewGuid(random);
            var arm = run % 3 == 0 ? HoldoutArm.Holdout : HoldoutArm.Treatment;
            var outcome = random.Next(10) switch
            {
                < 6 => TaskVerificationStatus.Verified,
                < 9 => TaskVerificationStatus.Failed,
                _ => (TaskVerificationStatus?)null,
            };
            foreach (var lesson in lessons.OrderBy(_ => random.Next()).Take(5))
            {
                rows.Add(new HoldoutOutcomeRow(runId, arm, lesson, At, outcome));
            }
        }

        var accumulator = new ExperienceBenefitAccumulator();
        accumulator.AddRange(rows.OrderBy(row => row.RunId).ThenBy(row => row.ExperienceId));

        AssertSame(ExperienceBenefitEstimator.Estimate(rows), accumulator.ToEstimate());
        Assert.Equal(40, accumulator.ToEstimate().Lessons.Count);
    }

    [Fact]
    public void Nothing_added_is_the_empty_estimate()
    {
        AssertSame(ExperienceBenefitEstimator.Empty(), new ExperienceBenefitAccumulator().ToEstimate());
    }

    [Fact]
    public void A_run_added_again_after_another_run_is_refused()
    {
        var first = Guid.NewGuid();
        var accumulator = new ExperienceBenefitAccumulator();
        accumulator.Add(new HoldoutOutcomeRow(first, HoldoutArm.Treatment, Guid.NewGuid(), At, TaskVerificationStatus.Verified));
        accumulator.Add(new HoldoutOutcomeRow(Guid.NewGuid(), HoldoutArm.Holdout, Guid.NewGuid(), At, TaskVerificationStatus.Failed));

        Assert.Throws<InvalidOperationException>(() =>
            accumulator.Add(new HoldoutOutcomeRow(first, HoldoutArm.Treatment, Guid.NewGuid(), At, TaskVerificationStatus.Verified)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_minimum_below_one_is_refused(int minimum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceBenefitAccumulator(new ExperienceBenefitOptions { MinimumRunsPerArm = minimum }));
    }

    private static void AssertSame(ExperienceBenefitEstimate expected, ExperienceBenefitEstimate actual)
    {
        Assert.Equal(expected.Overall, actual.Overall);
        Assert.Equal(expected.Lessons, actual.Lessons);
        Assert.Equal(expected.MinimumRunsPerArm, actual.MinimumRunsPerArm);
        Assert.Equal(expected.Tracked, actual.Tracked);
        Assert.Equal(expected.Outcome, actual.Outcome);
    }

    private static HoldoutArm Other(HoldoutArm arm) => arm == HoldoutArm.Treatment ? HoldoutArm.Holdout : HoldoutArm.Treatment;

    private static TaskVerificationStatus? Outcome(Random random) => random.Next(5) switch
    {
        0 => null,
        1 => TaskVerificationStatus.Unknown,
        2 => TaskVerificationStatus.Failed,
        _ => TaskVerificationStatus.Verified,
    };

    private static Guid NewGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
