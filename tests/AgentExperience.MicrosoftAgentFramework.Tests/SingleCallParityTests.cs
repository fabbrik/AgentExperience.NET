using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Model = AgentExperience.MicrosoftAgentFramework.Tests.AgentExperienceFiveLineSetupTests.Model;
using SetupHost = AgentExperience.MicrosoftAgentFramework.Tests.AgentExperienceFiveLineSetupTests.Host;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 21.7: over a <see cref="ChatClientAgent"/>, the single-call wiring (<c>UseAgentExperience</c> alone) and the
/// explicit wiring (the provider on the agent's options, then <c>UseAgentExperience</c>) run the same scenario and must
/// agree byte for byte: the derived task texts, the injection outcomes, every block the model received, and the
/// captured runs. The MAF compatibility probe runs this suite on the floor and on the newest 1.x.
/// </summary>
public sealed class SingleCallParityTests
{
    private const string EchoTool = "echo";

    private const string RunOnlyTool = "run_only";

    private static readonly AIFunction Echo = AIFunctionFactory.Create((string note) => $"echo:{note}", EchoTool);

    private static readonly AIFunction RunOnly = AIFunctionFactory.Create((string note) => $"run:{note}", RunOnlyTool);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_short_follow_up_in_a_session_is_joined_as_on_the_agents_options(bool streaming)
    {
        async Task<Trace> Scenario(bool explicitWiring)
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(trace.RecordDerivedTexts);
            var agent = Wire(explicitWiring, services, new ChatClientAgentOptions(), trace.Model);

            // A lesson for the whole request, then a session whose second turn says only the rest of it.
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);
            var session = await agent.CreateSessionAsync();
            await Run(agent, "Reconcile the ledger for invoice 7731", session, options: null, streaming);
            await Run(agent, "and close it", session, options: null, streaming);
            return trace;
        }

        var single = await Scenario(explicitWiring: false);
        var explicitTrace = await Scenario(explicitWiring: true);

        AssertSame(explicitTrace, single);
        Assert.Equal("Reconcile the ledger for invoice 7731 — and close it", single.DerivedTexts[^1]);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[^1].Outcome);
        Assert.Single(single.Model.Calls[^1], IsInjectedNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_UseRunTools_a_run_only_tool_counts_as_on_the_agents_options(bool streaming)
    {
        async Task<Trace> Scenario(bool explicitWiring)
        {
            var trace = new Trace { Model = new Model { Tool = EchoTool } };
            await using var services = trace.Host.Build(options =>
            {
                trace.RecordDerivedTexts(options);
                var injection = options.Injection;
                options.Injection = settings =>
                {
                    injection?.Invoke(settings);
                    settings.ReceivingAgent = new ReceivingAgentCapabilities { UseRunTools = true };
                };
            });

            // A verified run whose working attempt calls the echo tool, which the agent has only on its run options; the
            // default tools have another.
            var agent = Wire(
                explicitWiring,
                services,
                new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [RunOnly] } },
                trace.Model);
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo] });
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);

            // Without the run's tool the record is withheld, by both wirings.
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);
            return trace;
        }

        var single = await Scenario(explicitWiring: false);
        var explicitTrace = await Scenario(explicitWiring: true);

        AssertSame(explicitTrace, single);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[1].Outcome);
        Assert.Contains(single.Host.Injections[2].Omitted, omission => omission.Reason == InjectionOmissionReason.ToolUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_session_both_wirings_agree(bool streaming)
    {
        async Task<Trace> Scenario(bool explicitWiring)
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(trace.RecordDerivedTexts);
            var agent = Wire(explicitWiring, services, new ChatClientAgentOptions(), trace.Model);
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);

            // A short request with no session has no earlier turn to join.
            await Run(agent, "and close it", session: null, options: null, streaming);
            return trace;
        }

        var single = await Scenario(explicitWiring: false);
        AssertSame(await Scenario(explicitWiring: true), single);
        Assert.Equal("and close it", single.DerivedTexts[^1]);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[1].Outcome);
    }

    [Fact]
    public async Task A_history_provider_a_run_passes_is_read_as_the_agent_reads_it()
    {
        async Task<Trace> Scenario(bool explicitWiring)
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(trace.RecordDerivedTexts);
            var agent = Wire(explicitWiring, services, new ChatClientAgentOptions(), trace.Model);
            var passed = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions { StateKey = "passed-history" });
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { AdditionalProperties = new() { [typeof(ChatHistoryProvider).FullName!] = passed } });

            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
            var session = await agent.CreateSessionAsync();

            // The first turn goes to the agent's own history; the follow-up reads the one the run passes, which is empty.
            await Run(agent, "Reconcile the ledger for invoice 7731", session, options: null, streaming: false);
            await Run(agent, "and close it", session, runOptions, streaming: false);
            return trace;
        }

        var single = await Scenario(explicitWiring: false);
        AssertSame(await Scenario(explicitWiring: true), single);
        Assert.Equal("and close it", single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task With_the_conversation_kept_by_the_service_no_history_is_joined_by_either_wiring()
    {
        async Task<Trace> Scenario(bool explicitWiring)
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(trace.RecordDerivedTexts);
            var agent = Wire(explicitWiring, services, new ChatClientAgentOptions(), trace.Model);

            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
            var session = await agent.CreateSessionAsync();
            await Run(agent, "Reconcile the ledger for invoice 7731", session, options: null, streaming: false);

            // A conversation ID tells the agent the service holds the history, so it replays none, even in a session
            // whose local history holds the first turn.
            var serviceKept = new ChatClientAgentRunOptions(new ChatOptions { ConversationId = "conversation-1" });
            await Run(agent, "and close it", session, serviceKept, streaming: false);
            return trace;
        }

        var single = await Scenario(explicitWiring: false);
        AssertSame(await Scenario(explicitWiring: true), single);
        Assert.Equal("and close it", single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_joins_nothing_and_never_fails_the_run()
    {
        var trace = new Trace();
        await using var services = trace.Host.Build(trace.RecordDerivedTexts);
        var history = new FlakyHistory();
        var agent = Wire(explicitWiring: false, services, new ChatClientAgentOptions { ChatHistoryProvider = history }, trace.Model);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("Reconcile the ledger for invoice 7731", session);

        // The agent-level read comes first and fails; the agent's own read then succeeds, and so does the run.
        history.FailNextRead = true;
        var response = await agent.RunAsync("and close it", session);

        Assert.Equal("Done.", response.Text);
        Assert.False(history.FailNextRead);
        Assert.Empty(trace.Host.Failures);
        Assert.Equal("and close it", trace.DerivedTexts[^1]);
    }

    /// <summary>An in-memory history whose next read can be made to fail, once.</summary>
    private sealed class FlakyHistory : ChatHistoryProvider
    {
        private readonly List<ChatMessage> _messages = [];

        public bool FailNextRead { get; set; }

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new InvalidOperationException("the history store is down");
            }

            return ValueTask.FromResult<IEnumerable<ChatMessage>>(_messages.ToArray());
        }

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            _messages.AddRange(context.RequestMessages.Concat(context.ResponseMessages ?? []));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The agent under test: the provider on its options (the explicit wiring) or not, then <c>UseAgentExperience</c>.</summary>
    private static AIAgent Wire(bool explicitWiring, IServiceProvider services, ChatClientAgentOptions options, Model model)
    {
        if (explicitWiring)
        {
            options.AIContextProviders = [services.GetAgentExperienceContextProvider()];
        }

        var agent = new ChatClientAgent(model, options).AsBuilder().UseAgentExperience(services).Build();

        // The single-call wiring adds the agent-level provider; the explicit one adds none, so either way one block.
        Assert.Equal(!explicitWiring, agent.GetService<ExperienceMessageContextProvider>() is not null);
        return agent;
    }

    private static async Task Run(AIAgent agent, string text, AgentSession? session, AgentRunOptions? options, bool streaming)
    {
        if (!streaming)
        {
            await agent.RunAsync(text, session, options);
            return;
        }

        await foreach (var _ in agent.RunStreamingAsync(text, session, options))
        {
        }
    }

    private static void AssertSame(Trace expected, Trace actual)
    {
        Assert.Empty(expected.Host.Failures);
        Assert.Empty(actual.Host.Failures);
        Assert.Equal(expected.DerivedTexts, actual.DerivedTexts);
        Assert.Equal(expected.Injections(), actual.Injections());
        Assert.Equal(expected.Blocks(), actual.Blocks());
        Assert.Equal(expected.Runs(), actual.Runs());

        // At most one block injected into any model call, in either wiring (a session's replayed history may carry
        // an earlier turn's, in both).
        Assert.All(actual.Model.Calls, call => Assert.True(call.Count(IsInjectedNow) <= 1));
        Assert.All(expected.Model.Calls, call => Assert.True(call.Count(IsInjectedNow) <= 1));
    }

    private static bool IsHistoricalReference(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true;

    private static bool IsInjectedNow(ChatMessage message) =>
        IsHistoricalReference(message) && message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory;

    /// <summary>Everything one wiring's scenario produced.</summary>
    private sealed class Trace
    {
        public SetupHost Host { get; } = new();

        public Model Model { get; init; } = new();

        public List<string?> DerivedTexts { get; } = [];

        public void RecordDerivedTexts(AgentExperienceOptions options)
        {
            var injection = options.Injection;
            options.Injection = settings =>
            {
                injection?.Invoke(settings);
                var resolve = settings.ResolveRequestAsync!;
                settings.ResolveRequestAsync = (context, cancellationToken) =>
                {
                    DerivedTexts.Add(context.DerivedTaskText);
                    return resolve(context, cancellationToken);
                };
            };
        }

        public List<string> Injections() =>
            Host.Injections
                .Select(result => $"{result.Outcome} injected={result.InjectedExperienceIds.Count} "
                    + $"omitted=[{string.Join(",", result.Omitted.Select(omission => omission.Reason))}]")
                .ToList();

        /// <summary>For each model call, the exact text of every block in it.</summary>
        public List<string> Blocks() =>
            Model.Calls
                .Select(call => string.Join("\n---\n", call.Where(IsHistoricalReference).Select(message => message.Text)))
                .ToList();

        /// <summary>Each captured run, without the identifiers and times that differ from one host to the next.</summary>
        public List<string> Runs() =>
            Host.Verifications
                .Select(context => context.Run)
                .Select(run => $"{run.TaskId}|{run.TaskDescription}|{run.Scope}|{run.ExecutionStatus}|"
                    + string.Join(";", run.Attempts.Select(attempt =>
                        $"{attempt.SequenceNumber}:{string.Join(",", attempt.ToolCalls.Select(call => $"{call.ToolName}({string.Join(",", call.Arguments.Select(argument => $"{argument.Key}={argument.Value}"))})"))}:{attempt.Result}:{attempt.Error}")))
                .ToList();
    }
}
