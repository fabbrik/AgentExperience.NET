using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The assignment is derived from the trial index, not chosen and not stored.
/// </summary>
/// <remarks>
/// Every assertion here is computed from the index rather than read out of anything the harness
/// recorded, which is the whole point: if the harness kept a list of assignments and the list
/// disagreed with the index, these tests would still say what the index says and the run's own
/// assertion below would fail.
/// </remarks>
public class TrialPlanTests
{
    [Theory]
    [InlineData(0, TrialCondition.MemoryDisabled)]
    [InlineData(1, TrialCondition.MemoryEnabled)]
    [InlineData(2, TrialCondition.MemoryDisabled)]
    [InlineData(3, TrialCondition.MemoryEnabled)]
    [InlineData(10, TrialCondition.MemoryDisabled)]
    [InlineData(11, TrialCondition.MemoryEnabled)]
    public void Condition_alternates_by_index_from_the_preregistered_starting_condition(int index, TrialCondition expected) =>
        Assert.Equal(expected, TrialPlan.ConditionFor(index, ExperimentFacts.Design().StartingCondition));

    [Fact]
    public void Flipping_the_starting_condition_flips_every_assignment()
    {
        for (var index = 0; index < 12; index++)
        {
            Assert.NotEqual(
                TrialPlan.ConditionFor(index, TrialCondition.MemoryDisabled),
                TrialPlan.ConditionFor(index, TrialCondition.MemoryEnabled));
        }
    }

    [Fact]
    public void Consecutive_trials_share_a_task_so_each_task_runs_once_under_each_condition()
    {
        var tasks = ReuseBaselineArms.Reference.TaskSet.EvaluationTasks;
        var design = ExperimentFacts.Design();

        var byTask = Enumerable.Range(0, design.TrialCount)
            .Select(index => (Task: TrialPlan.TaskFor(index, tasks).TaskId, Condition: TrialPlan.ConditionFor(index, design.StartingCondition)))
            .GroupBy(assignment => assignment.Task)
            .ToList();

        Assert.Equal(tasks.Count, byTask.Count);
        Assert.All(byTask, group =>
        {
            Assert.Equal(2, group.Count());
            Assert.Single(group, assignment => assignment.Condition == TrialCondition.MemoryEnabled);
            Assert.Single(group, assignment => assignment.Condition == TrialCondition.MemoryDisabled);
        });
    }

    /// <summary>
    /// The sequence the reference run actually produced, against a sequence written out by hand.
    /// </summary>
    /// <remarks>
    /// Deliberately not compared against <see cref="TrialPlan.ConditionFor"/>. Driving execution
    /// from a stored all-enabled list while keeping the recorded label derived passes a test that
    /// compares a value against the function that produced it; it does not pass this one, because
    /// this one knows what the answer is supposed to be.
    /// </remarks>
    [Fact]
    public async Task Every_trial_the_reference_run_produced_matches_the_sequence_written_out_by_hand()
    {
        var result = await ExperimentFacts.ReferenceAsync();

        string[] expectedConditions =
        [
            "memory-disabled", "memory-enabled",
            "memory-disabled", "memory-enabled",
            "memory-disabled", "memory-enabled",
            "memory-disabled", "memory-enabled",
            "memory-disabled", "memory-enabled",
            "memory-disabled", "memory-enabled",
        ];

        string[] expectedTasks =
        [
            "eval-incident-101", "eval-incident-101",
            "eval-incident-102", "eval-incident-102",
            "eval-incident-103", "eval-incident-103",
            "eval-incident-201", "eval-incident-201",
            "eval-incident-202", "eval-incident-202",
            "eval-incident-203", "eval-incident-203",
        ];

        Assert.Equal(expectedConditions.Length, result.Trials.Count);
        Assert.Equal(expectedConditions, result.Trials.Select(trial => result.Preregistration.Design.LabelFor(trial.Condition)).ToArray());
        Assert.Equal(expectedTasks, result.Trials.Select(trial => trial.TaskId).ToArray());
        Assert.Equal(Enumerable.Range(0, expectedTasks.Length).ToArray(), result.Trials.Select(trial => trial.Index).ToArray());

        // Balanced: the same number of trials on each side.
        Assert.Equal(
            result.Trials.Count(trial => trial.Condition == TrialCondition.MemoryEnabled),
            result.Trials.Count(trial => trial.Condition == TrialCondition.MemoryDisabled));
    }

    /// <summary>
    /// And what the harness recorded is what the index implies -- the other direction of the same
    /// claim, kept because it is the one that holds for any task set rather than only for this one.
    /// </summary>
    [Fact]
    public async Task Every_trial_the_reference_run_produced_matches_what_the_index_says_it_should_be()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var design = result.Preregistration.Design;
        var tasks = result.Arm.TaskSet.EvaluationTasks;

        Assert.Equal(design.TrialCount, result.Trials.Count);

        foreach (var trial in result.Trials)
        {
            Assert.Equal(TrialPlan.ConditionFor(trial.Index, design.StartingCondition), trial.Condition);
            Assert.Equal(TrialPlan.TaskFor(trial.Index, tasks).TaskId, trial.TaskId);
        }
    }

    [Fact]
    public void The_task_set_refuses_a_resolving_strategy_the_agent_could_never_explore()
    {
        var unreachable = ReuseBaselineArms.Reference.TaskSet with
        {
            EvaluationTasks = [new ReuseBaselineTask("eval-impossible", "Warehouse picking halted and no known approach restarts the reservation pipeline.", "teleport")],
        };

        var refused = Assert.Throws<TaskSetException>(unreachable.Validate);
        Assert.Contains("teleport", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exploring_cost_of_each_task_is_readable_off_the_task_set()
    {
        var taskSet = ReuseBaselineArms.Reference.TaskSet;

        // wait-for-lock is third in the exploration order, so an exploring agent fails twice first.
        Assert.Equal(2, taskSet.ExpectedExploringFailures(taskSet.EvaluationTasks[0]));

        // escalate-to-oncall is fourth, so three.
        Assert.Equal(3, taskSet.ExpectedExploringFailures(taskSet.EvaluationTasks[3]));
    }

    /// <summary>
    /// The cost an injected block implies, worked out from the task set alone. This is the
    /// arithmetic the harness compares every completed trial against.
    /// </summary>
    [Fact]
    public void The_cost_of_a_task_given_an_injected_block_is_readable_off_the_task_set()
    {
        var taskSet = ReuseBaselineArms.Reference.TaskSet;
        var lockTask = taskSet.EvaluationTasks[0];
        var overloadTask = taskSet.EvaluationTasks[3];

        // The block names the resolving strategy first: no failed attempt at all.
        Assert.Equal(0, taskSet.ExpectedFailuresGiven(lockTask, [IncidentStrategies.WaitForLock]));

        // The block names the other strategy first: exactly one, and then the exploration order.
        Assert.Equal(1, taskSet.ExpectedFailuresGiven(lockTask, [IncidentStrategies.EscalateToOnCall, IncidentStrategies.WaitForLock]));
        Assert.Equal(0, taskSet.ExpectedFailuresGiven(overloadTask, [IncidentStrategies.EscalateToOnCall]));

        // A block naming only a strategy that resolves nothing here pushes the answer back by one.
        Assert.Equal(3, taskSet.ExpectedFailuresGiven(lockTask, [IncidentStrategies.EscalateToOnCall]));

        // And with no block at all it is the exploration cost.
        Assert.Equal(taskSet.ExpectedExploringFailures(lockTask), taskSet.ExpectedFailuresGiven(lockTask, []));
    }
}
