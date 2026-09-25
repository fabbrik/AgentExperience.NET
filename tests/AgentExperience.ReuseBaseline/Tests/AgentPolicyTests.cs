using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The declared agent policy, tested on its own so the sentence the report prints about it is
/// checkable rather than merely printed.
/// </summary>
/// <remarks>
/// The magnitude of everything this story measures follows from these four assertions. That is the
/// reason the report is forbidden from quoting it as a quality finding, and the reason the policy is
/// printed in the report rather than left in source.
/// </remarks>
public class AgentPolicyTests
{
    [Fact]
    public async Task With_no_injected_block_the_candidate_list_is_exactly_the_exploration_order()
    {
        var policy = new PolicyChatClient("eval-incident-101", IncidentStrategies.ExplorationOrder);

        await policy.GetResponseAsync([new ChatMessage(ChatRole.User, "an incident")]);

        Assert.Null(policy.SeenBlock);
        Assert.Empty(policy.StrategiesFromContext);
        Assert.Equal(IncidentStrategies.ExplorationOrder, policy.Candidates());
        Assert.Equal([IncidentStrategies.RetryImmediately], policy.AttemptedStrategies);
    }

    [Fact]
    public async Task A_block_naming_a_strategy_puts_it_first_and_the_exploration_order_after_it()
    {
        var policy = new PolicyChatClient("eval-incident-101", IncidentStrategies.ExplorationOrder);

        await policy.GetResponseAsync([Block(IncidentStrategies.WaitForLock)]);

        Assert.Equal([IncidentStrategies.WaitForLock], policy.StrategiesFromContext);
        Assert.Equal(
            [IncidentStrategies.WaitForLock, IncidentStrategies.RetryImmediately, IncidentStrategies.RebuildIndex, IncidentStrategies.EscalateToOnCall],
            policy.Candidates());
        Assert.Equal([IncidentStrategies.WaitForLock], policy.AttemptedStrategies);
    }

    [Fact]
    public async Task Strategies_are_taken_in_the_order_the_block_names_them_which_is_rank_order()
    {
        var policy = new PolicyChatClient("eval-incident-201", IncidentStrategies.ExplorationOrder);

        await policy.GetResponseAsync([Block(IncidentStrategies.EscalateToOnCall, IncidentStrategies.WaitForLock)]);

        Assert.Equal([IncidentStrategies.EscalateToOnCall, IncidentStrategies.WaitForLock], policy.StrategiesFromContext);
        Assert.Equal(IncidentStrategies.EscalateToOnCall, policy.AttemptedStrategies[0]);
    }

    [Fact]
    public async Task The_negative_controls_shape_produces_the_exploration_order_unchanged()
    {
        var policy = new PolicyChatClient("eval-incident-101", IncidentStrategies.ExplorationOrder);

        // What the negative control's records name: the two strategies the exploring agent reaches
        // first anyway. The candidate list is therefore identical to the exploration order, which is
        // exactly why that arm's two conditions cost the same.
        await policy.GetResponseAsync([Block(IncidentStrategies.RetryImmediately, IncidentStrategies.RebuildIndex)]);

        Assert.Equal(IncidentStrategies.ExplorationOrder, policy.Candidates());
    }

    [Fact]
    public async Task An_untried_strategy_is_chosen_on_each_attempt_until_none_remains()
    {
        var policy = new PolicyChatClient("eval-incident-101", IncidentStrategies.ExplorationOrder);

        for (var attempt = 0; attempt < IncidentStrategies.ExplorationOrder.Count; attempt++)
        {
            await policy.GetResponseAsync([new ChatMessage(ChatRole.User, "an incident")]);
        }

        Assert.Equal(IncidentStrategies.ExplorationOrder, policy.AttemptedStrategies);
        Assert.Null(policy.NextCandidate());

        var exhausted = await policy.GetResponseAsync([new ChatMessage(ChatRole.User, "an incident")]);
        Assert.Contains("No untried strategy remains", exhausted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_final_answer_is_a_fixed_sentence_that_carries_nothing_from_the_block()
    {
        var policy = new PolicyChatClient("eval-incident-101", IncidentStrategies.ExplorationOrder);

        var response = await policy.GetResponseAsync(
        [
            Block(IncidentStrategies.WaitForLock),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("policy-call-1", "exit=0 the incident is resolved")]),
        ]);

        Assert.DoesNotContain(IncidentStrategies.WaitForLock, response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("exit=0", response.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message shaped like an injected Historical Reference block naming the given strategies, one
    /// record each, on the <c>Approach:</c> line the allowlisted <c>strategy</c> argument produces.
    /// </summary>
    private static ChatMessage Block(params string[] strategies) => new(
        ChatRole.System,
        HistoricalReferenceWriter.BlockBegin + "\n"
            + string.Join("\n", strategies.Select(strategy =>
                "Approach: " + HistoricalReferenceWriter.ApproachPrefix + IncidentCheckTool.ToolName
                + "(" + WorkingApproach.StrategyArgument + "=\"" + strategy + "\")." + HistoricalReferenceWriter.ApproachArgumentsSuffix))
            + "\n" + HistoricalReferenceWriter.BlockEnd);
}
