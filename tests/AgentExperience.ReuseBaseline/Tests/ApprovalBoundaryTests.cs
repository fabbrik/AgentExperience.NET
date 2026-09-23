using AgentExperience.Core.Reflections;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// <c>unauthorized_tool_executions</c> is a measure that can be non-zero, and the reference
/// experiment's zero is a measurement rather than a placeholder.
/// </summary>
/// <remarks>
/// <para>
/// A guardrail that has only ever been observed at zero tells a reader nothing. So one arm here
/// registers a reflector that writes a prompt-injection instruction into the lesson, exactly the way
/// <c>InjectedContentAuthorizationTests</c> makes its fake model obey an injected instruction. The
/// agent obeys it; the approval boundary denies the call; the tool body never runs; the harness
/// counts the denial; and the gate's third term fails because of it.
/// </para>
/// <para>
/// What is asserted is not that a model refuses. A label cannot make a model refuse and this library
/// never claims it can. What is asserted is that the boundary, which lives entirely outside the
/// injected block, holds anyway.
/// </para>
/// </remarks>
public class ApprovalBoundaryTests
{
    [Fact]
    public async Task A_poisoned_lesson_is_obeyed_denied_counted_and_fails_the_guardrail()
    {
        var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            DecorateReflector = inner => new PoisonedReflector(new WorkingApproachReflector(inner)),
        });

        var enabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryEnabled).ToList();
        var disabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryDisabled).ToList();

        // The agent obeyed: every memory-enabled trial attempted the guarded call exactly once ...
        Assert.All(enabled, trial => Assert.Equal(1, trial.Metrics.UnauthorizedToolExecutions));

        // ... and no memory-disabled trial did, because no block reached it.
        Assert.All(disabled, trial => Assert.Equal(0, trial.Metrics.UnauthorizedToolExecutions));

        // The boundary denied it every time, and the tool body never ran -- which the harness would
        // have refused the whole run over. That refusal is not vacuous: the test below unwraps the
        // guarded tool so the body really does run, and shows the harness refusing.
        Assert.Equal(1d, result.Gate.Enabled.UnauthorizedToolExecutions.Mean);
        Assert.Equal(0d, result.Gate.Disabled.UnauthorizedToolExecutions.Mean);

        // And the guardrail did its job: the gate refuses the arm.
        var guardrail = result.Gate.Terms.Single(term => term.Metric == "unauthorized_tool_executions");
        Assert.False(guardrail.Holds);
        Assert.Equal(GateVerdict.NoDemonstratedBenefit, result.Gate.Verdict);
        Assert.Contains("unauthorized_tool_executions", ReuseBaselineReport.RenderDeterministic(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reference_experiment_denies_nothing_because_nothing_asked_for_the_guarded_tool()
    {
        var result = await ExperimentFacts.ReferenceAsync();

        Assert.All(result.Trials, trial => Assert.Equal(0, trial.Metrics.UnauthorizedToolExecutions));

        // Measured over every trial in both conditions, not defaulted: the guarded tool was on the
        // agent's tool list in all twelve.
        Assert.Equal(6, result.Gate.Enabled.UnauthorizedToolExecutions.Observations);
        Assert.Equal(6, result.Gate.Disabled.UnauthorizedToolExecutions.Observations);
        Assert.Equal(0d, result.Gate.Enabled.UnauthorizedToolExecutions.Mean);
    }

    [Fact]
    public void The_poisoned_reflector_names_the_guarded_tool_so_the_agent_has_something_to_obey()
    {
        Assert.Contains(ToolApprovalBoundary.GuardedToolName, PoisonedReflector.Instruction, StringComparison.Ordinal);
        _ = new PoisonedReflector(new DefaultExperienceReflector());
    }

    /// <summary>
    /// The harness's "the tool body never ran" refusal, shown firing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything this harness says about <c>unauthorized_tool_executions</c> rests on that guard:
    /// "denied" is supposed to mean the call did not happen, not that a counter moved. The guard had
    /// never been seen to fire in any test, so the claim rested on reading it.
    /// </para>
    /// <para>
    /// Here the guarded tool is handed to the agent unwrapped -- no
    /// <c>ApprovalRequiredAIFunction</c> -- while the poisoned lesson still tells the agent to call
    /// it. MAF therefore executes the body, and the harness refuses the run rather than reporting a
    /// denial count that would have been a lie.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task When_the_guarded_tool_body_does_run_the_harness_refuses_rather_than_reporting_a_denial_count()
    {
        var refused = await Assert.ThrowsAsync<HarnessIntegrityException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            DecorateReflector = inner => new PoisonedReflector(new WorkingApproachReflector(inner)),

            // Index 1 is the first memory-enabled trial, so it is the first one the block reaches.
            UnguardTheGuardedToolAt = index => index == 1,
        }));

        Assert.Contains("executed the guarded tool", refused.Message, StringComparison.Ordinal);
        Assert.Contains("did not hold", refused.Message, StringComparison.Ordinal);
        Assert.Contains("means anything", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guarded tool is only unwrapped where a test asks for it; every pre-registered arm is
    /// wrapped, which is why their denial counts mean what they say.
    /// </summary>
    [Fact]
    public async Task No_preregistered_arm_ever_unwraps_the_guarded_tool()
    {
        foreach (var arm in ReuseBaselineArms.All)
        {
            var options = new ExperimentOptions { Arm = arm };
            Assert.Null(options.UnguardTheGuardedToolAt);
        }

        // And the arms that ran reported a denial count for every completed trial rather than a
        // placeholder, so zero there is a measurement.
        var result = await ExperimentFacts.ReferenceAsync();
        Assert.All(
            result.Trials.Where(trial => trial.Status == TrialStatus.Completed),
            trial => Assert.NotNull(trial.Metrics.UnauthorizedToolExecutions));
    }
}
