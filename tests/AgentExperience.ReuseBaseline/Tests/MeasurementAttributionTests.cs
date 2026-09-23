using AgentExperience.Abstractions;
using AgentExperience.Core.Reflections;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// Whether the memory-enabled arm's advantage actually comes from the injected block.
/// </summary>
/// <remarks>
/// <para>
/// This is the file the review said the story's credibility rests on. Before it existed, a harness
/// that fed the reference arm's agent the task's ground-truth resolving strategy directly -- while
/// still retrieving, injecting and recording the block, and merely ignoring its content -- passed
/// every test with both golden reports byte-identical. Its <c>BenefitDemonstrated</c>, its means and
/// its whole report were indistinguishable from a working harness's.
/// </para>
/// <para>
/// Three things close that. The harness itself refuses a run whose measured cost is not the cost its
/// own task set implies for the strategies the agent read out of context. The wrong-strategy arm is
/// a run in which reading the block must make things <em>worse</em>, by exactly one attempt, which
/// an agent handed the answer cannot produce. And the measured means are compared here against
/// arithmetic over the task set, so the golden file is no longer the only detector.
/// </para>
/// </remarks>
public class MeasurementAttributionTests
{
    /// <summary>
    /// Every memory-enabled trial read strategies out of its context, and every strategy it read is
    /// one the learned records name.
    /// </summary>
    [Fact]
    public async Task Every_enabled_trial_read_its_strategies_out_of_the_records_the_learning_phase_wrote()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var learned = result.Learned.Select(record => record.WorkingStrategy).OfType<string>().ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(learned);

        var enabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryEnabled).ToList();
        Assert.Equal(6, enabled.Count);

        Assert.All(enabled, trial =>
        {
            Assert.True(trial.SawInjectedBlock, $"Trial {trial.Index} saw no injected block.");
            Assert.NotEmpty(trial.StrategiesReadFromContext);
            Assert.All(trial.StrategiesReadFromContext, strategy => Assert.Contains(strategy, learned));
        });
    }

    /// <summary>
    /// And no memory-disabled trial was exposed to anything, read anything, or saw a block. "The two
    /// arms differ by the condition alone" asserted rather than left to a byte comparison.
    /// </summary>
    [Fact]
    public async Task No_disabled_trial_was_exposed_to_a_record_or_saw_a_block()
    {
        foreach (var result in new[]
        {
            await ExperimentFacts.ReferenceAsync(),
            await ExperimentFacts.NegativeControlAsync(),
            await ExperimentFacts.WrongStrategyAsync(),
        })
        {
            var disabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryDisabled).ToList();

            Assert.Equal(6, disabled.Count);
            Assert.All(disabled, trial =>
            {
                Assert.Empty(trial.ExposedExperienceIds);
                Assert.Empty(trial.StrategiesReadFromContext);
                Assert.False(trial.SawInjectedBlock, $"Trial {trial.Index} of {result.Arm.Id} saw a block under the memory-disabled condition.");
                Assert.Null(trial.FeedbackId);
            });

            // And the ledger holds no row for any of them, which is the store's own account of the
            // same fact.
            var disabledRuns = disabled.Select(trial => trial.RunId).ToHashSet();
            Assert.DoesNotContain(result.LedgerRows, row => disabledRuns.Contains(row.RunId));
        }
    }

    /// <summary>
    /// Every trial's measured cost equals the cost the task set implies, per trial and per condition
    /// mean.
    /// </summary>
    /// <remarks>
    /// The left-hand side is read out of the capture service's snapshot; the right-hand side is
    /// arithmetic over <see cref="ReuseBaselineTaskSet"/>, which never sees the agent. The golden
    /// report is no longer the only thing that would notice a number moving.
    /// </remarks>
    [Fact]
    public async Task The_measured_means_are_the_ones_the_task_set_implies()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var taskSet = result.Arm.TaskSet;

        foreach (var trial in result.Trials)
        {
            var task = taskSet.EvaluationTasks.Single(candidate => candidate.TaskId == trial.TaskId);

            Assert.Equal(
                taskSet.ExpectedFailuresGiven(task, trial.StrategiesReadFromContext),
                trial.Metrics.FailedAttempts);
        }

        // The memory-disabled arm is pure exploration, so its mean is the mean position of the
        // evaluation tasks' resolving strategies in the exploration order: (2+2+2+3+3+3)/6.
        var exploring = taskSet.EvaluationTasks.Select(taskSet.ExpectedExploringFailures).ToList();
        Assert.Equal([2, 2, 2, 3, 3, 3], exploring);
        Assert.Equal(exploring.Average(), result.Gate.Disabled.FailedAttempts.Mean);
        Assert.Equal(2.5d, result.Gate.Disabled.FailedAttempts.Mean);

        // The memory-enabled arm's mean is what the injected block's ordering implies, task by task.
        var withBlock = result.Trials
            .Where(trial => trial.Condition == TrialCondition.MemoryEnabled)
            .Select(trial => taskSet.ExpectedFailuresGiven(
                taskSet.EvaluationTasks.Single(task => task.TaskId == trial.TaskId),
                trial.StrategiesReadFromContext))
            .ToList();

        Assert.Equal(withBlock.Average(), result.Gate.Enabled.FailedAttempts.Mean);
        Assert.Equal(0.5d, result.Gate.Enabled.FailedAttempts.Mean);
    }

    /// <summary>
    /// The wrong-strategy arm: the injected record names an approach that resolves none of its
    /// evaluation tasks, so the memory-enabled condition must cost exactly one more failed attempt.
    /// </summary>
    /// <remarks>
    /// This is the arm an answer-planting harness cannot pass. Every other arm rewards reading the
    /// block or is indifferent to it; here reading it is strictly worse, by a fixed amount, and an
    /// agent acting on anything other than the block produces a different number.
    /// </remarks>
    [Fact]
    public async Task The_wrong_strategy_arm_costs_exactly_one_extra_attempt_under_the_memory_enabled_condition()
    {
        var result = await ExperimentFacts.WrongStrategyAsync();
        var taskSet = result.Arm.TaskSet;

        // The premise: one learned record, naming a strategy no evaluation task is resolved by.
        var learned = Assert.Single(result.Learned);
        Assert.Equal(IncidentStrategies.EscalateToOnCall, learned.WorkingStrategy);
        Assert.All(taskSet.EvaluationTasks, task => Assert.Equal(IncidentStrategies.WaitForLock, task.ResolvingStrategy));

        // The block really did reach every memory-enabled trial, and named exactly that strategy.
        var enabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryEnabled).ToList();
        Assert.All(enabled, trial =>
        {
            Assert.NotEmpty(trial.ExposedExperienceIds);
            Assert.Equal([IncidentStrategies.EscalateToOnCall], trial.StrategiesReadFromContext);
        });

        // Exactly one more attempt: three against two, every trial, no exceptions.
        Assert.All(enabled, trial => Assert.Equal(3, trial.Metrics.FailedAttempts));
        Assert.All(
            result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryDisabled),
            trial => Assert.Equal(2, trial.Metrics.FailedAttempts));

        Assert.Equal(3d, result.Gate.Enabled.FailedAttempts.Mean);
        Assert.Equal(2d, result.Gate.Disabled.FailedAttempts.Mean);
        Assert.Equal(1d, result.Gate.Enabled.FailedAttempts.Mean - result.Gate.Disabled.FailedAttempts.Mean);

        // And the gate says no, on the primary metric, with the guardrails untouched.
        Assert.Equal(GateVerdict.NoDemonstratedBenefit, result.Gate.Verdict);
        Assert.False(result.Gate.Terms.Single(term => term.Metric == "failed_attempts").Holds);
        Assert.True(result.Gate.Terms.Single(term => term.Metric == "verified_success_rate").Holds);
        Assert.True(result.Gate.Terms.Single(term => term.Metric == "unauthorized_tool_executions").Holds);
    }

    /// <summary>
    /// And the harness refuses outright when a trial's cost is not explained by what its agent read.
    /// </summary>
    /// <remarks>
    /// Simulated here by handing the agent a block it cannot have got from the store: a reflector
    /// that writes a strategy no learning run ever used. The trial then reads a strategy the learned
    /// records do not name, which is exactly the shape of "the advantage came from somewhere else".
    /// </remarks>
    [Fact]
    public async Task A_strategy_reaching_the_agent_from_outside_the_learned_records_stops_the_run()
    {
        var refused = await Assert.ThrowsAsync<HarnessIntegrityException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.NegativeControl,
            DecorateReflector = inner => new SmuggledStrategyReflector(inner),
        }));

        Assert.Contains("reached the agent from somewhere other than a stored record", refused.Message, StringComparison.Ordinal);
        Assert.Contains(IncidentStrategies.WaitForLock, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reflector that writes a working approach into the lesson that the run it reflects on never
    /// used. Test-only: it is the smallest version of "the answer got in by another route".
    /// </summary>
    private sealed class SmuggledStrategyReflector(IExperienceReflector inner) : IExperienceReflector
    {
        public async Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default)
        {
            var reflection = await inner.ReflectAsync(request, cancellationToken).ConfigureAwait(false);
            return reflection with { Lesson = reflection.Lesson + WorkingApproachReflector.Sentence(IncidentStrategies.WaitForLock) };
        }
    }
}
