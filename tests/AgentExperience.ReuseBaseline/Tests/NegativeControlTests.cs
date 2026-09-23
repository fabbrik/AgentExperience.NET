using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The negative control: frozen rule 6, and the single most important thing this story delivers.
/// </summary>
/// <remarks>
/// <para>
/// A harness that has never been observed to say no is not evidence when it says yes. This arm runs
/// the same evaluation tasks, the same agent policy, the same trial count and the same gate as the
/// reference experiment. The only difference is the learning set: its records name approaches the
/// exploring agent would have tried first anyway, so the injected experience carries no usable
/// advantage.
/// </para>
/// <para>
/// The tests below assert that the control is a real one before they assert the verdict. A negative
/// verdict because nothing was injected would prove nothing at all, so the injection is checked
/// first.
/// </para>
/// </remarks>
public class NegativeControlTests
{
    [Fact]
    public async Task The_gate_reports_NoDemonstratedBenefit()
    {
        var result = await ExperimentFacts.NegativeControlAsync();

        Assert.Equal(GateVerdict.NoDemonstratedBenefit, result.Gate.Verdict);
    }

    [Fact]
    public async Task The_records_really_were_injected_so_the_negative_verdict_is_about_their_content()
    {
        var result = await ExperimentFacts.NegativeControlAsync();

        var enabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryEnabled).ToList();

        Assert.NotEmpty(enabled);
        Assert.All(enabled, trial =>
        {
            Assert.NotEmpty(trial.ExposedExperienceIds);
            Assert.Null(trial.RetrievalFailure);
        });

        // And the learning phase really did produce usable records.
        Assert.All(result.Learned, record =>
        {
            Assert.Equal(AgentExperience.Abstractions.ExperienceStatus.Validated, record.Status);
            Assert.NotNull(record.WorkingStrategy);
        });
    }

    [Fact]
    public async Task It_fails_on_the_primary_metric_because_the_two_conditions_cost_the_same()
    {
        var result = await ExperimentFacts.NegativeControlAsync();

        var primary = result.Gate.Terms.Single(term => term.Metric == "failed_attempts");

        Assert.False(primary.Holds);
        Assert.Equal(result.Gate.Enabled.FailedAttempts.Mean, result.Gate.Disabled.FailedAttempts.Mean);

        // The guardrails are untouched: the failure is the primary metric's alone, which is what
        // "the injected experience carries no usable advantage" should look like.
        Assert.True(result.Gate.Terms.Single(term => term.Metric == "verified_success_rate").Holds);
        Assert.True(result.Gate.Terms.Single(term => term.Metric == "unauthorized_tool_executions").Holds);
    }

    [Fact]
    public async Task The_strategies_the_injected_block_names_are_ones_the_exploring_agent_would_have_tried_first()
    {
        var result = await ExperimentFacts.NegativeControlAsync();

        var order = result.Arm.TaskSet.ExplorationOrder;
        var learnedStrategies = result.Learned.Select(record => record.WorkingStrategy).ToList();

        // This is the property that makes the control a control, stated so a reader can check it:
        // every learned approach is one of the first two the exploration order reaches, and no
        // evaluation task is resolved by either of them.
        var firstTwo = order.Take(2).ToList();
        Assert.All(learnedStrategies, strategy => Assert.Contains(strategy, firstTwo));
        Assert.All(
            result.Arm.TaskSet.EvaluationTasks,
            task => Assert.DoesNotContain(task.ResolvingStrategy, learnedStrategies));
    }

    [Fact]
    public async Task The_report_states_the_negative_verdict_in_the_same_detail_as_a_pass()
    {
        var negative = ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.NegativeControlAsync());
        var reference = ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.ReferenceAsync());

        Assert.Contains("VERDICT: NoDemonstratedBenefit", negative, StringComparison.Ordinal);

        // Every section the passing report has, the failing one has too: there is one code path
        // through the renderer and it does not branch on the verdict.
        foreach (var heading in new[]
        {
            ReuseBaselineReport.MeasuresStatement,
            "AGENT POLICY",
            "TASK SET",
            "LEARNED RECORDS",
            "PER-CONDITION RESULTS",
            "TRIALS",
            "REUSE FEEDBACK",
            "NOTES",
        })
        {
            Assert.Contains(heading, negative, StringComparison.Ordinal);
            Assert.Contains(heading, reference, StringComparison.Ordinal);
        }

        // And the numbers that produced it are there, not just the word.
        Assert.Contains("failed_attempts (primary)", negative, StringComparison.Ordinal);
    }
}
