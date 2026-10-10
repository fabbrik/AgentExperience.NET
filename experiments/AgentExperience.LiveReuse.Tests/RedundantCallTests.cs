using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>
/// A model may make a second, redundant <c>apply_migration</c> call in the response that got the migration live. The
/// stored record keeps both calls and the final attempt's Tried: line lists both, in order; neither tool call threw, so both are
/// marked [returned] and the line does not say which one released the migration. The
/// harness must compare the whole sequence on both sides, and still refuse a block that differs from its store.
/// </summary>
public sealed class RedundantCallTests
{
    [Fact]
    public void Every_strategy_on_a_line_is_read_in_order_with_repeats()
    {
        Assert.Equal(["in-place", "batched-backfill", "in-place"], RolloutStrategies.AllNamedIn("  - attempt 2: apply_migration(strategy=\"in-place\"), apply_migration(strategy=\"batched-backfill\"), apply_migration(strategy=\"in-place\") \u2192 completed"));
        Assert.Empty(RolloutStrategies.AllNamedIn("  - attempt 2: describe_service, apply_migration \u2192 completed"));
        Assert.Empty(RolloutStrategies.AllNamedIn(null));
    }

    [Fact]
    public async Task A_redundant_call_after_success_is_stored_and_shown_in_full_and_the_run_reports()
    {
        var result = await TestSupport.RunScriptedAsync(new RedundantCaller(new ScriptedOperatorModel()));

        Assert.True(result.Complete);
        foreach (var instance in MigrationTaskSet.Current.Instances)
        {
            var learned = result.Learning.Single(run => run.Instance == instance.Index && run.Condition == LiveReuseExperiment.LearnHidden);
            Assert.Equal(2, learned.StoredStrategies.Count);
            Assert.Equal(instance.HiddenStrategy, learned.StoredStrategy);
            Assert.Equal(instance.HiddenStrategy, learned.StoredStrategies[0]);

            var trial = result.Trials.Single(run => run.Instance == instance.Index && run.Condition == "memory-enabled");
            Assert.Equal(learned.StoredStrategies, trial.BlockStrategies);
            Assert.Equal(instance.HiddenStrategy, trial.BlockStrategy);
            Assert.True(trial.FollowedBlock);

            Assert.Empty(result.Trials.Single(run => run.Instance == instance.Index && run.Condition == "memory-placebo").BlockStrategies);
        }

        var markdown = LiveReuseReport.Markdown(result);
        var first = result.Learning.First(run => run.StoredStrategies.Count == 2);
        Assert.Contains($"| {first.StoredStrategies[0]} > {first.StoredStrategies[1]} |", markdown, StringComparison.Ordinal);
        Assert.Contains("\"storedStrategies\": [", LiveReuseReport.Json(result), StringComparison.Ordinal);
        Assert.Contains("\"blockStrategies\": [", LiveReuseReport.Json(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_block_whose_sequence_differs_from_its_store_is_still_refused()
    {
        var design = LivePreregistration.ReadEmbedded();
        var redundant = await TestSupport.RunScriptedAsync(new RedundantCaller(new ScriptedOperatorModel()));
        var single = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());

        // Unchanged, both runs pass the check.
        LiveReuseExperiment.RequireConditionsDifferOnlyByInjection(design, redundant.Learning, redundant.Trials);
        LiveReuseExperiment.RequireConditionsDifferOnlyByInjection(design, single.Learning, single.Trials);

        // Only the first strategy of a two-strategy store: the first entries agree, the sequences do not.
        var truncated = Replace(redundant.Trials, "memory-enabled", trial => trial with { BlockStrategies = [trial.BlockStrategies[0]] });
        Assert.Contains("its store holds", Assert.Throws<HarnessIntegrityException>(() => LiveReuseExperiment.RequireConditionsDifferOnlyByInjection(design, redundant.Learning, truncated)).Message, StringComparison.Ordinal);

        // A different strategy than the store holds.
        var wrong = Replace(single.Trials, "negative-control", trial => trial with { BlockStrategies = [RolloutStrategies.All.First(strategy => strategy != trial.BlockStrategy)] });
        Assert.Throws<HarnessIntegrityException>(() => LiveReuseExperiment.RequireConditionsDifferOnlyByInjection(design, single.Learning, wrong));

        // A placebo whose line names a strategy.
        var leaky = Replace(single.Trials, "memory-placebo", trial => trial with { BlockStrategies = ["in-place"] });
        Assert.Throws<HarnessIntegrityException>(() => LiveReuseExperiment.RequireConditionsDifferOnlyByInjection(design, single.Learning, leaky));
    }

    private static List<RunRecord> Replace(IReadOnlyList<RunRecord> trials, string condition, Func<RunRecord, RunRecord> change)
    {
        var index = trials.ToList().FindIndex(trial => trial.Condition == condition);
        var copy = trials.ToList();
        copy[index] = change(copy[index]);
        return copy;
    }

    /// <summary>
    /// Adds a second <c>apply_migration</c> call to every response that makes one, as Claude sometimes did. The redundant
    /// strategy is neither the service's hidden nor its stale strategy, so the database always refuses it and it never
    /// stands in the way of the strategy that works.
    /// </summary>
    private sealed class RedundantCaller(IChatClient inner) : DelegatingChatClient(inner)
    {
        private int _sequence;

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            var message = response.Messages[^1];
            if (message.Contents.OfType<FunctionCallContent>().FirstOrDefault(call => call.Name == MigrationEnvironment.ApplyToolName) is { } apply
                && apply.Arguments is { } arguments
                && arguments.TryGetValue("service", out var service)
                && arguments.TryGetValue("strategy", out var strategy))
            {
                var instance = MigrationTaskSet.Current.Instances.Single(candidate => candidate.Service == (string)service!);
                var redundant = RolloutStrategies.All.First(candidate => candidate != instance.HiddenStrategy && candidate != instance.StaleStrategy && candidate != (string)strategy!);
                message.Contents.Add(new FunctionCallContent("redundant-" + (++_sequence), MigrationEnvironment.ApplyToolName, new Dictionary<string, object?>(arguments) { ["strategy"] = redundant }));
            }

            return response;
        }
    }
}
