using AgentExperience.LiveReuse.Harness;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>
/// The whole harness, end to end, against scripted models: the library's capture, verification, reflection, store and
/// injection are all real; only the model is a script. Each behaviour proves one thing the live run relies on.
/// </summary>
public sealed class ScriptedExperimentTests
{
    [Fact]
    public async Task A_model_that_follows_the_block_benefits_from_correct_experience_and_not_from_stale_experience()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel(ScriptedBehavior.FollowsBlock));

        Assert.True(result.Complete);
        Assert.Equal(12, result.Trials.Count(trial => trial.Condition == "memory-enabled"));
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, result.Reference.Verdict);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.NegativeControl.Verdict);
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, result.Content.Verdict);
        Assert.Equal(OverallConclusion.ReuseBenefitAttributableToContent, result.Conclusion);

        // The listing-order explorer fails the hidden strategy's listing position; two instances hide the first one.
        Assert.Equal(2.5, result.Reference.ControlMeanFailedAttempts);
        Assert.Equal(0.0, result.Reference.TreatmentMeanFailedAttempts);
        Assert.Equal(new SignTestResult(10, 0, 2, 1d / 1024), result.Reference.SignTest);

        // Stale experience costs a failed attempt whenever the stale strategy is listed after the hidden one.
        Assert.Equal(3.0, result.NegativeControl.TreatmentMeanFailedAttempts);
        Assert.Equal(0, result.NegativeControl.SignTest!.Wins);
    }

    [Fact]
    public async Task Every_learning_run_stores_the_strategy_its_database_accepted_and_the_block_carries_it()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());

        foreach (var instance in MigrationTaskSet.Current.Instances)
        {
            var current = result.Learning.Single(run => run.Instance == instance.Index && run.Condition == LiveReuseExperiment.LearnHidden);
            var stale = result.Learning.Single(run => run.Instance == instance.Index && run.Condition == LiveReuseExperiment.LearnStale);
            Assert.Equal(instance.HiddenStrategy, current.StoredStrategy);
            Assert.Equal(instance.StaleStrategy, stale.StoredStrategy);

            // Read out of the text the model was sent, not out of the task set.
            Assert.Equal(instance.HiddenStrategy, result.Trials.Single(trial => trial.Instance == instance.Index && trial.Condition == "memory-enabled").BlockStrategy);
            Assert.Equal(instance.StaleStrategy, result.Trials.Single(trial => trial.Instance == instance.Index && trial.Condition == "negative-control").BlockStrategy);
            Assert.Null(result.Trials.Single(trial => trial.Instance == instance.Index && trial.Condition == "memory-disabled").BlockStrategy);

            // The placebo is shown the same record, with the strategy withheld.
            var placebo = result.Trials.Single(trial => trial.Instance == instance.Index && trial.Condition == "memory-placebo");
            Assert.True(placebo.BlockSeen);
            Assert.Null(placebo.BlockStrategy);
        }
    }

    /// <summary>
    /// The harness does not plant the answer: a model that never reads the block gets exactly the same numbers in all
    /// four conditions, so any difference a live run reports can only have come through the block.
    /// </summary>
    [Fact]
    public async Task A_model_that_ignores_the_block_shows_no_benefit_and_identical_numbers_in_every_condition()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel(ScriptedBehavior.IgnoresBlock));

        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Reference.Verdict);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.NegativeControl.Verdict);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Content.Verdict);
        Assert.Equal(OverallConclusion.NoDemonstratedBenefit, result.Conclusion);

        foreach (var group in result.Trials.GroupBy(trial => trial.Instance))
        {
            Assert.Single(group.Select(trial => trial.FailedAttempts).Distinct());
            Assert.Single(group.Select(trial => string.Join(">", trial.Changes.Select(change => change.Strategy))).Distinct());
        }
    }

    /// <summary>
    /// The only thing that differs between the conditions is the Historical Reference: take the block message out of a
    /// memory-enabled or negative-control trial's first model call and what is left is byte-identical to the
    /// memory-disabled trial's first call for the same instance.
    /// </summary>
    [Fact]
    public async Task The_conditions_differ_by_the_injected_block_and_nothing_else()
    {
        var model = new ScriptedOperatorModel();
        var result = await TestSupport.RunScriptedAsync(model);
        var calls = TestSupport.CallsByRun(result, model);

        static string Render(IReadOnlyList<ChatMessage> messages) =>
            string.Join("\n---\n", messages
                .Where(message => !message.Text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal))
                .Select(message => message.Role + ": " + message.Text));

        foreach (var instance in MigrationTaskSet.Current.Instances)
        {
            var first = calls
                .Where(entry => entry.Run.Phase == "evaluation" && entry.Run.Instance == instance.Index)
                .ToDictionary(entry => entry.Run.Condition, entry => entry.Calls[0]);

            var control = Render(first["memory-disabled"]);
            Assert.Equal(control, Render(first["memory-enabled"]));
            Assert.Equal(control, Render(first["negative-control"]));
            Assert.Equal(control, Render(first["memory-placebo"]));

            Assert.DoesNotContain(first["memory-disabled"], message => message.Text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
            Assert.Single(first["memory-enabled"], message => message.Text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
            Assert.Single(first["negative-control"], message => message.Text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Nothing a memory-disabled run is shown names a strategy until the model itself has tried one: the task text and
    /// describe_service are strategy-free, so the only strategy names in its conversation are its own earlier calls.
    /// </summary>
    [Fact]
    public async Task No_memory_disabled_run_is_shown_a_strategy_it_did_not_try_itself()
    {
        var model = new ScriptedOperatorModel();
        var result = await TestSupport.RunScriptedAsync(model);

        Assert.Null(RolloutStrategies.FirstNamedIn(LiveReuseExperiment.Instructions));
        Assert.Null(RolloutStrategies.FirstNamedIn(LiveReuseExperiment.FollowUp));

        foreach (var (run, calls) in TestSupport.CallsByRun(result, model).Where(entry => entry.Run.Phase == "learning" || entry.Run.Condition == "memory-disabled"))
        {
            var tried = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                var text = string.Join("\n", call.Select(message => message.Text));
                foreach (var strategy in RolloutStrategies.All.Where(strategy => text.Contains(strategy, StringComparison.Ordinal)))
                {
                    Assert.True(run.Changes.Any(change => change.Strategy == strategy), $"Run {run.Sequence} was shown '{strategy}' without trying it.");
                }
            }
        }
    }

    [Fact]
    public async Task A_model_that_never_acts_fails_everywhere_stores_nothing_and_its_trials_see_no_block()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel(ScriptedBehavior.NeverActs));

        Assert.All(result.Learning, run => Assert.Null(run.StoredStrategy));
        Assert.All(result.Learning, run => Assert.Equal(result.Design.LearningAttemptLimit, run.Attempts));
        Assert.All(result.Trials, trial =>
        {
            Assert.False(trial.Verified);
            Assert.False(trial.BlockSeen);
            Assert.Equal(result.Design.EvaluationAttemptLimit, trial.FailedAttempts);
        });
        Assert.Equal(OverallConclusion.NoDemonstratedBenefit, result.Conclusion);
    }

    /// <summary>The guardrail term is live: a model that asks for the bypass whenever a block is present fails the gate.</summary>
    [Fact]
    public async Task A_model_that_asks_for_the_bypass_when_shown_a_block_fails_the_guardrail()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel(ScriptedBehavior.ForcesWhenBlockPresent));

        Assert.Equal(0, result.Reference.ControlUnauthorized);
        Assert.Equal(12, result.Reference.TreatmentUnauthorized);
        Assert.False(result.Reference.Terms[3].Holds);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Reference.Verdict);
        Assert.Equal(OverallConclusion.NoDemonstratedBenefit, result.Conclusion);
    }

    [Fact]
    public async Task The_evaluation_order_rotates_the_conditions_by_instance()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());

        Assert.Equal(["memory-disabled", "memory-enabled", "negative-control", "memory-placebo"], result.Trials.Where(t => t.Instance == 0).Select(t => t.Condition));
        Assert.Equal(["memory-enabled", "negative-control", "memory-placebo", "memory-disabled"], result.Trials.Where(t => t.Instance == 1).Select(t => t.Condition));
        Assert.Equal(["memory-placebo", "memory-disabled", "memory-enabled", "negative-control"], result.Trials.Where(t => t.Instance == 3).Select(t => t.Condition));
        Assert.Equal(["memory-disabled", "memory-enabled", "negative-control", "memory-placebo"], result.Trials.Where(t => t.Instance == 4).Select(t => t.Condition));
        Assert.All(result.Trials, trial => Assert.Equal(MigrationTaskSet.Current.Instances[trial.Instance].HiddenStrategy, trial.AcceptedStrategy));
    }

    [Fact]
    public async Task A_provider_failure_is_recorded_by_type_only_and_excludes_its_instance()
    {
        // The learning phase makes 108 calls with this script, so call 150 falls in an evaluation trial.
        var result = await TestSupport.RunScriptedAsync(new FailingOnCall(new ScriptedOperatorModel(), failOnCall: 150));

        var errored = Assert.Single(result.Trials.Concat(result.Learning), run => run.Status == RunStatus.Errored);
        Assert.Equal("evaluation", errored.Phase);
        Assert.Equal(nameof(InvalidOperationException), errored.Classification);
        Assert.Null(errored.FailedAttempts);
        Assert.Null(errored.Verified);
        Assert.Equal([errored.Instance], result.ExcludedInstances);
        Assert.Equal(11, result.Reference.Pairs);
        Assert.Equal(11, result.NegativeControl.Pairs);
        Assert.Equal(11, result.Content.Pairs);
        Assert.DoesNotContain("secret detail", LiveReuseReport.Markdown(result), StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", LiveReuseReport.Json(result), StringComparison.Ordinal);
    }

    /// <summary>A learning run that fails stores nothing; its instance's trial still runs, with nothing to retrieve.</summary>
    [Fact]
    public async Task A_failed_learning_run_stores_nothing_and_its_trial_runs_without_a_block()
    {
        var result = await TestSupport.RunScriptedAsync(new FailingOnCall(new ScriptedOperatorModel(), failOnCall: 1));

        var first = result.Learning[0];
        Assert.Equal(RunStatus.Errored, first.Status);
        Assert.Null(first.StoredStrategy);

        var trial = result.Trials.Single(run => run.Instance == 0 && run.Condition == "memory-enabled");
        Assert.Equal(RunStatus.Completed, trial.Status);
        Assert.False(trial.BlockSeen);
        Assert.Empty(result.ExcludedInstances);
        Assert.Equal(12, result.Reference.Pairs);
    }

    private sealed class FailingOnCall(IChatClient inner, int failOnCall) : DelegatingChatClient(inner)
    {
        private int _calls;

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            ++_calls == failOnCall
                ? throw new InvalidOperationException("secret detail that must not be published")
                : base.GetResponseAsync(messages, options, cancellationToken);
    }
}
