using AgentExperience.Core.Holdout;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 25.4: the estimate split by model. Off by default and then identical to the pooled estimate; on, every run counts
/// in exactly one consuming-model group and every lesson's runs in exactly one (producing, consuming) pair, each decided by
/// the same rules, and the incremental form gives exactly what the pure form gives.
/// </summary>
public class ExperienceBenefitModelSplitTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Lesson = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly ExperienceBenefitOptions Split = new() { SplitByModel = true };

    [Fact]
    public void Split_is_off_by_default_and_the_estimate_is_the_pooled_one()
    {
        Assert.False(new ExperienceBenefitOptions().SplitByModel);

        var rows = Acceptance();
        var plain = rows.Select(row => row with { RunModel = null, LessonModel = null }).ToList();
        var labelled = ExperienceBenefitEstimator.Estimate(rows);
        var unlabelled = ExperienceBenefitEstimator.Estimate(plain);

        Assert.Null(labelled.ByConsumingModel);
        Assert.All(labelled.Lessons, lesson => Assert.Null(lesson.ByModelPair));
        Assert.Equal(unlabelled.Overall, labelled.Overall);
        Assert.Equal(unlabelled.Lessons, labelled.Lessons);

        var accumulator = new ExperienceBenefitAccumulator();
        accumulator.AddRange(rows.OrderBy(row => row.RunId).ThenBy(row => row.ExperienceId));
        Assert.Null(accumulator.ToEstimate().ByConsumingModel);
    }

    /// <summary>
    /// Acceptance: model A's runs verify only with the lessons and model B's only without; split, A's verdict is Helps and
    /// B's Hurts, while pooled they cancel out to NoEvidence.
    /// </summary>
    [Fact]
    public void Model_A_helped_and_model_B_hurt_while_the_pooled_verdict_says_nothing()
    {
        var estimate = ExperienceBenefitEstimator.Estimate(Acceptance(), Split);

        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
        Assert.Equal(["model-a", "model-b"], estimate.ByConsumingModel!.Select(group => group.Model));
        var a = estimate.ByConsumingModel![0].Comparison;
        var b = estimate.ByConsumingModel![1].Comparison;
        Assert.Equal(ExperienceBenefitVerdict.Helps, a.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Hurts, b.Verdict);
        Assert.Equal((25, 25, 25, 0), (a.Treatment.Runs, a.Treatment.Successes, a.Holdout.Runs, a.Holdout.Successes));
        Assert.Equal((25, 0, 25, 25), (b.Treatment.Runs, b.Treatment.Successes, b.Holdout.Runs, b.Holdout.Successes));

        // The lesson was learned by A: its pairs are (A, A) and (A, B), with the same verdicts.
        var lesson = Assert.Single(estimate.Lessons);
        Assert.Equal(
            [("model-a", "model-a", ExperienceBenefitVerdict.Helps), ("model-a", "model-b", ExperienceBenefitVerdict.Hurts)],
            lesson.ByModelPair!.Select(pair => (pair.LessonModel, pair.RunModel, pair.Comparison.Verdict)));
    }

    [Fact]
    public void A_run_or_lesson_with_no_model_is_grouped_as_unknown()
    {
        var rows = new List<HoldoutOutcomeRow>
        {
            Row(1, HoldoutArm.Treatment, TaskVerificationStatus.Verified, runModel: null, lessonModel: null),
            Row(2, HoldoutArm.Holdout, TaskVerificationStatus.Failed, runModel: "model-b", lessonModel: null),
            Row(3, HoldoutArm.Holdout, TaskVerificationStatus.Failed, runModel: "  ", lessonModel: "model-a"),
        };

        var estimate = ExperienceBenefitEstimator.Estimate(rows, Split);

        Assert.Equal(["model-b", ExperienceBenefitEstimate.UnknownModel], estimate.ByConsumingModel!.Select(group => group.Model));
        Assert.Equal(
            [("model-a", "unknown"), ("unknown", "model-b"), ("unknown", "unknown")],
            estimate.Lessons[0].ByModelPair!.Select(pair => (pair.LessonModel, pair.RunModel)));
    }

    [Fact]
    public void Each_group_needs_the_minimum_per_arm_on_its_own()
    {
        // Pooled, 40 runs per arm; model B alone has 5 per arm, under the minimum of 20.
        var rows = new List<HoldoutOutcomeRow>();
        for (var i = 0; i < 80; i++)
        {
            var model = i % 40 < 35 ? "model-a" : "model-b";
            var arm = i < 40 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            rows.Add(Row(i, arm, arm == HoldoutArm.Treatment ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed, model, "model-a"));
        }

        var estimate = ExperienceBenefitEstimator.Estimate(rows, Split);

        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.ByConsumingModel![0].Comparison.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.ByConsumingModel![1].Comparison.Verdict);
        Assert.Equal((5, 5), (estimate.ByConsumingModel![1].Comparison.Treatment.Runs, estimate.ByConsumingModel![1].Comparison.Holdout.Runs));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 20)]
    [InlineData(4, 5)]
    [InlineData(5, 2)]
    public void Groups_sum_to_the_pooled_counts_and_the_accumulator_gives_the_pure_split(int seed, int minimum)
    {
        var random = new Random(seed);
        string?[] models = [null, "model-a", "model-b", "model-c"];
        var lessons = Enumerable.Range(0, random.Next(1, 8)).Select(_ => (Id: NewGuid(random), Model: models[random.Next(models.Length)])).ToArray();
        var rows = new List<HoldoutOutcomeRow>();
        var runs = random.Next(0, 300);
        for (var run = 0; run < runs; run++)
        {
            var runId = NewGuid(random);
            var arm = random.Next(2) == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            var outcome = Outcome(random);
            var runModel = models[random.Next(models.Length)];
            foreach (var lesson in lessons.Where(_ => random.Next(3) == 0).DefaultIfEmpty(lessons[0]))
            {
                var rowArm = random.Next(25) == 0 ? (arm == HoldoutArm.Treatment ? HoldoutArm.Holdout : HoldoutArm.Treatment) : arm;
                rows.Add(new HoldoutOutcomeRow(runId, rowArm, lesson.Id, At, outcome) { RunModel = runModel, LessonModel = lesson.Model });
            }
        }

        var options = Split with { MinimumRunsPerArm = minimum };
        var shuffled = rows.OrderBy(_ => random.Next()).ToList();
        var expected = ExperienceBenefitEstimator.Estimate(shuffled, options);

        AssertSums(expected.Overall, expected.ByConsumingModel!.Select(group => group.Comparison));
        foreach (var lesson in expected.Lessons)
        {
            AssertSums(lesson.Comparison, lesson.ByModelPair!.Select(pair => pair.Comparison));
        }

        // Without the split, the pooled parts are exactly what the split estimate pooled.
        var pooled = ExperienceBenefitEstimator.Estimate(shuffled, options with { SplitByModel = false });
        Assert.Equal(pooled.Overall, expected.Overall);
        Assert.Equal(pooled.Lessons.Select(lesson => lesson.Comparison), expected.Lessons.Select(lesson => lesson.Comparison));

        var accumulator = new ExperienceBenefitAccumulator(options);
        foreach (var run in shuffled.GroupBy(row => row.RunId).OrderBy(_ => random.Next()))
        {
            accumulator.AddRange(run.OrderBy(_ => random.Next()));
        }

        var actual = accumulator.ToEstimate();
        Assert.Equal(expected.Overall, actual.Overall);
        Assert.Equal(expected.ByConsumingModel, actual.ByConsumingModel);
        Assert.Equal(expected.Lessons.Count, actual.Lessons.Count);
        for (var i = 0; i < expected.Lessons.Count; i++)
        {
            Assert.Equal(expected.Lessons[i].ExperienceId, actual.Lessons[i].ExperienceId);
            Assert.Equal(expected.Lessons[i].Comparison, actual.Lessons[i].Comparison);
            Assert.Equal(expected.Lessons[i].ByModelPair, actual.Lessons[i].ByModelPair);
        }
    }

    [Fact]
    public void Nothing_split_is_empty_groups()
    {
        var estimate = ExperienceBenefitEstimator.Empty(Split);
        Assert.Empty(estimate.ByConsumingModel!);
        Assert.Empty(new ExperienceBenefitAccumulator(Split).ToEstimate().ByConsumingModel!);
    }

    private static void AssertSums(ExperienceBenefitComparison pooled, IEnumerable<ExperienceBenefitComparison> groups)
    {
        var list = groups.ToList();
        Assert.Equal(pooled.Treatment.Runs, list.Sum(group => group.Treatment.Runs));
        Assert.Equal(pooled.Treatment.Successes, list.Sum(group => group.Treatment.Successes));
        Assert.Equal(pooled.Holdout.Runs, list.Sum(group => group.Holdout.Runs));
        Assert.Equal(pooled.Holdout.Successes, list.Sum(group => group.Holdout.Successes));
        Assert.Equal(pooled.Unverified, list.Sum(group => group.Unverified));
        Assert.Equal(pooled.Pending, list.Sum(group => group.Pending));
        Assert.Equal(pooled.MixedArm, list.Sum(group => group.MixedArm));
    }

    /// <summary>Model A: 25 given the lesson verify, 25 denied it fail. Model B: the reverse. The lesson was learned by A.</summary>
    private static List<HoldoutOutcomeRow> Acceptance()
    {
        var rows = new List<HoldoutOutcomeRow>();
        for (var i = 0; i < 100; i++)
        {
            var model = i < 50 ? "model-a" : "model-b";
            var arm = i % 50 < 25 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            var helped = (model == "model-a") == (arm == HoldoutArm.Treatment);
            rows.Add(Row(i, arm, helped ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed, model, "model-a"));
        }

        return rows;
    }

    private static HoldoutOutcomeRow Row(int run, HoldoutArm arm, TaskVerificationStatus? outcome, string? runModel, string? lessonModel) =>
        new(new Guid($"00000000-0000-0000-0000-{run:x12}"), arm, Lesson, At, outcome) { RunModel = runModel, LessonModel = lessonModel };

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
