using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;
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

    private const string Earlier = "Reconcile the ledger for invoice 7731";

    private const string FollowUp = "and close it";

    private const string Joined = Earlier + " — " + FollowUp;

    private static readonly AIFunction Echo = AIFunctionFactory.Create((string note) => $"echo:{note}", EchoTool);

    private static readonly AIFunction EchoTwin = AIFunctionFactory.Create((string note) => $"twin:{note}", EchoTool);

    private static readonly AIFunction RunOnly = AIFunctionFactory.Create((string note) => $"run:{note}", RunOnlyTool);

    /// <summary>Where a pass-through run middleware sits relative to <c>UseAgentExperience</c>.</summary>
    public enum Shape
    {
        None,
        MiddlewareBefore,
        MiddlewareAfter,
    }

    // ---- The chat history ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_short_follow_up_in_a_session_is_joined_as_on_the_agents_options(bool streaming)
    {
        var (single, explicitTrace) = await Both(async (trace, agent) =>
        {
            // A lesson for the whole request, then a session whose second turn says only the rest of it.
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);
            var session = await agent.CreateSessionAsync();
            await Run(agent, Earlier, session, options: null, streaming);
            await Run(agent, FollowUp, session, options: null, streaming);
        });

        AssertSame(explicitTrace, single);
        Assert.Equal(Joined, single.DerivedTexts[^1]);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[^1].Outcome);
        Assert.Single(single.Model.Calls[^1], IsInjectedNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_session_both_wirings_agree(bool streaming)
    {
        var (single, explicitTrace) = await Both(async (trace, agent) =>
        {
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);

            // A short request with no session has no earlier turn to join.
            await Run(agent, FollowUp, session: null, options: null, streaming);
        });

        AssertSame(explicitTrace, single);
        Assert.Equal(FollowUp, single.DerivedTexts[^1]);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[1].Outcome);
    }

    [Fact]
    public async Task Without_a_session_a_history_that_needs_none_is_joined_as_on_the_agents_options()
    {
        // The agent starts a session of its own and reads its history provider through it; so does the agent level.
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
                await Run(agent, FollowUp, session: null, options: null, streaming: false);
            },
            options: () => new ChatClientAgentOptions { ChatHistoryProvider = new FixedHistory(Earlier) });

        AssertSame(explicitTrace, single);
        Assert.Equal(Joined, single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task A_reducer_that_runs_before_retrieval_is_not_run_a_second_time()
    {
        var (single, explicitTrace, reducers) = await WithReducer(keep: 8);

        AssertSame(explicitTrace, single);
        Assert.Equal(Joined, single.DerivedTexts[^1]);

        // Single-call first, then explicit: the reducer ran as often in both, once per read the agent makes.
        Assert.Equal(reducers[1].Calls, reducers[0].Calls);
        Assert.True(reducers[0].Calls > 0);
    }

    [Fact]
    public async Task A_reducer_that_drops_the_earlier_turn_before_retrieval_is_the_documented_difference()
    {
        // The agent level joins from the history as stored; the agent's reducer, which it does not run again, would have
        // dropped the earlier turn from the model's view, so on the agent's options the follow-up stands alone.
        var (single, explicitTrace, _) = await WithReducer(keep: 2);

        Assert.Equal(FollowUp, explicitTrace.DerivedTexts[^1]);
        Assert.Equal(Joined, single.DerivedTexts[^1]);
    }

    private static async Task<(Trace Single, Trace Explicit, List<CountingReducer> Reducers)> WithReducer(int keep)
    {
        var reducers = new List<CountingReducer>();
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
                var session = await agent.CreateSessionAsync();
                await Run(agent, "Prepare the ledger export", session, options: null, streaming: false);
                await Run(agent, Earlier, session, options: null, streaming: false);
                await Run(agent, FollowUp, session, options: null, streaming: false);
            },
            options: () =>
            {
                var reducer = new CountingReducer(keep);
                reducers.Add(reducer);
                return new ChatClientAgentOptions
                {
                    ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                    {
                        ChatReducer = reducer,
                        ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.BeforeMessagesRetrieval,
                    }),
                };
            });

        return (single, explicitTrace, reducers);
    }

    [Fact]
    public async Task With_history_persisted_per_service_call_no_history_is_joined_by_either_wiring()
    {
        var histories = new List<InMemoryChatHistoryProvider>();
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
                var session = await agent.CreateSessionAsync();

                // The history holds an earlier turn, which the agent loads only per service call, after its providers.
                histories[^1].SetMessages(session, [new ChatMessage(ChatRole.User, Earlier), new ChatMessage(ChatRole.Assistant, "Done.")]);
                await Run(agent, FollowUp, session, options: null, streaming: false);
            },
            options: () =>
            {
                histories.Add(new InMemoryChatHistoryProvider());
                return new ChatClientAgentOptions { ChatHistoryProvider = histories[^1], RequirePerServiceCallChatHistoryPersistence = true };
            });

        AssertSame(explicitTrace, single);
        Assert.Equal(FollowUp, single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task With_a_conversation_ID_on_the_session_no_history_is_joined_by_either_wiring()
    {
        var histories = new List<InMemoryChatHistoryProvider>();
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
                var session = await agent.CreateSessionAsync();
                trace.Model.ConversationId = "conversation-1";
                await Run(agent, "Prepare the ledger export", session, options: null, streaming: false);
                Assert.Equal("conversation-1", ((ChatClientAgentSession)session).ConversationId);

                // The local history holds an earlier turn the agent no longer replays.
                histories[^1].SetMessages(session, [new ChatMessage(ChatRole.User, Earlier), new ChatMessage(ChatRole.Assistant, "Done.")]);
                await Run(agent, FollowUp, session, options: null, streaming: false);
            },
            options: () =>
            {
                histories.Add(new InMemoryChatHistoryProvider());
                return new ChatClientAgentOptions { ChatHistoryProvider = histories[^1], ThrowOnChatHistoryProviderConflict = false, ClearOnChatHistoryProviderConflict = false };
            });

        AssertSame(explicitTrace, single);
        Assert.Equal(FollowUp, single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task With_a_conversation_ID_on_the_agents_chat_options_no_history_is_joined_by_either_wiring()
    {
        var histories = new List<InMemoryChatHistoryProvider>();
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                trace.Model.ConversationId = "conversation-1";
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
                var session = await agent.CreateSessionAsync();
                histories[^1].SetMessages(session, [new ChatMessage(ChatRole.User, Earlier), new ChatMessage(ChatRole.Assistant, "Done.")]);
                await Run(agent, FollowUp, session, options: null, streaming: false);
            },
            options: () =>
            {
                histories.Add(new InMemoryChatHistoryProvider());
                return new ChatClientAgentOptions
                {
                    ChatHistoryProvider = histories[^1],
                    ThrowOnChatHistoryProviderConflict = false,
                    ClearOnChatHistoryProviderConflict = false,
                    ChatOptions = new ChatOptions { ConversationId = "conversation-1" },
                };
            });

        AssertSame(explicitTrace, single);
        Assert.Equal(FollowUp, single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task With_a_conversation_ID_on_the_runs_chat_options_no_history_is_joined_by_either_wiring()
    {
        var (single, explicitTrace) = await Both(async (trace, agent) =>
        {
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
            var session = await agent.CreateSessionAsync();
            await Run(agent, Earlier, session, options: null, streaming: false);

            // A conversation ID tells the agent the service holds the history, so it replays none, even in a session
            // whose local history holds the first turn.
            var serviceKept = new ChatClientAgentRunOptions(new ChatOptions { ConversationId = "conversation-1" });
            await Run(agent, FollowUp, session, serviceKept, streaming: false);
        });

        AssertSame(explicitTrace, single);
        Assert.Equal(FollowUp, single.DerivedTexts[^1]);
    }

    [Fact]
    public async Task A_history_provider_a_run_passes_wins_in_the_agents_order()
    {
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
                var session = await agent.CreateSessionAsync();
                var fromRun = Seeded(session, "from-run", "Reconcile the ledger for invoice A");
                var fromRunChat = Seeded(session, "from-run-chat", "Reconcile the ledger for invoice B");

                // All three passed: the run options' own wins.
                var all = new ChatClientAgentRunOptions(new ChatOptions { AdditionalProperties = Passing(fromRunChat) })
                {
                    AdditionalProperties = Passing(fromRun),
                };
                await Run(agent, FollowUp, session, all, streaming: false);

                // Without it, the run's chat options' wins over the agent's.
                var two = new ChatClientAgentRunOptions(new ChatOptions { AdditionalProperties = Passing(fromRunChat) });
                await Run(agent, FollowUp, session, two, streaming: false);

                // Without either, the agent's own chat options pass theirs.
                await Run(agent, FollowUp, session, options: null, streaming: false);
            },
            options: () => new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { AdditionalProperties = Passing(new FixedHistory("Reconcile the ledger for invoice C")) },
            });

        AssertSame(explicitTrace, single);
        Assert.Equal(
            [
                "Reconcile the ledger for invoice A — and close it",
                "Reconcile the ledger for invoice B — and close it",
                "Reconcile the ledger for invoice C — and close it",
            ],
            single.DerivedTexts[^3..]);
    }

    // ---- The tools ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_UseRunTools_a_run_only_tool_counts_as_on_the_agents_options(bool streaming)
    {
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                // A verified run whose working attempt calls the echo tool, which the agent has only on its run
                // options; the default tools have another.
                var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo] });
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);

                // Without the run's tool the record is withheld, by both wirings.
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming);
            },
            options: () => new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [RunOnly] } },
            useRunTools: true);

        AssertSame(explicitTrace, single);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[1].Outcome);
        Assert.Contains(single.Host.Injections[2].Omitted, omission => omission.Reason == InjectionOmissionReason.ToolUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_UseRunTools_an_agent_with_no_tools_of_its_own_sees_the_runs(bool streaming)
    {
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo] });
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);
            },
            useRunTools: true);

        AssertSame(explicitTrace, single);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[1].Outcome);
    }

    [Fact]
    public async Task With_UseRunTools_a_tool_name_on_both_the_run_and_the_agent_counts_as_on_the_agents_options()
    {
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                // Taught through the agent's own echo; then a run offers a second tool of the same name.
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, new ChatClientAgentRunOptions(new ChatOptions { Tools = [RunOnly] }), streaming: false);
                try
                {
                    await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo, RunOnly] }), streaming: false);
                }
                catch (InvalidOperationException ex)
                {
                    // Newer Microsoft.Extensions.AI refuses duplicate tool names at the model call, after injection.
                    trace.Errors.Add(ex.GetType().Name);
                }
            },
            options: () => new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [EchoTwin] } },
            useRunTools: true);

        AssertSame(explicitTrace, single);
        Assert.Equal(explicitTrace.Errors, single.Errors);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[1].Outcome);
    }

    // ---- Pipeline shapes, continuations and refusals ------------------------------------------------------

    [Theory]
    [InlineData(Shape.MiddlewareBefore, false)]
    [InlineData(Shape.MiddlewareAfter, false)]
    [InlineData(Shape.MiddlewareBefore, true)]
    [InlineData(Shape.MiddlewareAfter, true)]
    public async Task Run_middleware_before_or_after_UseAgentExperience_changes_nothing(Shape shape, bool streaming)
    {
        var (single, explicitTrace) = await Both(
            async (trace, agent) =>
            {
                var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo] });
                await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, runOptions, streaming);
                var session = await agent.CreateSessionAsync();
                await Run(agent, Earlier, session, runOptions, streaming);
                await Run(agent, FollowUp, session, runOptions, streaming);
            },
            options: () => new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [RunOnly] } },
            useRunTools: true,
            shape: shape);

        AssertSame(explicitTrace, single);
        Assert.Equal(Joined, single.DerivedTexts[^1]);
        Assert.Equal(InjectionOutcome.Injected, single.Host.Injections[^1].Outcome);
    }

    [Fact]
    public async Task A_continued_background_response_gets_no_injection_in_either_wiring()
    {
        var (single, explicitTrace) = await Both(async (trace, agent) =>
        {
            await Run(agent, AgentExperienceFiveLineSetupTests.Task, session: null, options: null, streaming: false);
            var session = await agent.CreateSessionAsync();
#pragma warning disable MEAI001 // Background responses, for the continuation the agent runs no providers for.
            var started = await agent.RunAsync(AgentExperienceFiveLineSetupTests.Task, session, new ChatClientAgentRunOptions { AllowBackgroundResponses = true });
            Assert.NotNull(started.ContinuationToken);
            var injections = trace.Host.Injections.Count;
            await agent.RunAsync(session, new ChatClientAgentRunOptions { ContinuationToken = started.ContinuationToken });
#pragma warning restore MEAI001
            Assert.Equal(injections, trace.Host.Injections.Count);
        });

        AssertSame(explicitTrace, single);
        Assert.DoesNotContain(single.Model.Calls[^1], IsInjectedNow);
    }

    [Fact]
    public async Task A_run_the_agent_refuses_for_a_passed_history_on_a_service_kept_conversation_reads_nothing()
    {
        foreach (var explicitWiring in new[] { false, true })
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(trace.RecordDerivedTexts);
            trace.Model.ConversationId = "conversation-1";
            var agent = Wire(explicitWiring, services, new ChatClientAgentOptions
            {
                ThrowOnChatHistoryProviderConflict = true,
                ChatOptions = new ChatOptions { ConversationId = "conversation-1" },
            }, trace.Model);
            var passed = new FixedHistory(Earlier);
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { AdditionalProperties = Passing(passed) });

            var session = await agent.CreateSessionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(FollowUp, session, runOptions));
            Assert.Equal(0, passed.Reads);
        }
    }

    [Fact]
    public async Task A_run_the_agent_refuses_for_a_state_key_clash_reads_nothing()
    {
        foreach (var explicitWiring in new[] { false, true })
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(trace.RecordDerivedTexts);
            var agent = Wire(explicitWiring, services, new ChatClientAgentOptions { AIContextProviders = [new KeyedProvider("clash")] }, trace.Model);
            var passed = new FixedHistory(Earlier, stateKey: "clash");
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { AdditionalProperties = Passing(passed) });

            var session = await agent.CreateSessionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(FollowUp, session, runOptions));
            Assert.Equal(0, passed.Reads);
        }
    }

    // ---- Failures, timeouts and cancellation --------------------------------------------------------------

    [Fact]
    public async Task A_history_that_cannot_be_read_joins_nothing_never_fails_the_run_and_is_reported()
    {
        var trace = new Trace();
        await using var services = trace.Host.Build(trace.RecordDerivedTexts);
        var history = new FlakyHistory();
        var agent = Wire(explicitWiring: false, services, new ChatClientAgentOptions { ChatHistoryProvider = history }, trace.Model);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(Earlier, session);

        // The agent-level read comes first and fails; the agent's own read then succeeds, and so does the run.
        using var probe = TelemetryProbe.Start();
        history.Next = FlakyHistory.Failure.Throw;
        var response = await agent.RunAsync(FollowUp, session);

        Assert.Equal("Done.", response.Text);
        Assert.Equal(FlakyHistory.Failure.None, history.Next);
        Assert.Empty(trace.Host.Failures);
        Assert.Equal(FollowUp, trace.DerivedTexts[^1]);
        Assert.Contains(probe.LibraryActivities, activity => Equals(activity.GetTagItem(InjectionDiagnostics.HistoryReadAttribute), "failed"));
    }

    [Fact]
    public async Task A_history_read_cancelled_without_the_caller_joins_nothing_and_never_fails_the_run()
    {
        var trace = new Trace();
        await using var services = trace.Host.Build(trace.RecordDerivedTexts);
        var history = new FlakyHistory();
        var agent = Wire(explicitWiring: false, services, new ChatClientAgentOptions { ChatHistoryProvider = history }, trace.Model);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(Earlier, session);

        history.Next = FlakyHistory.Failure.Cancel;
        var response = await agent.RunAsync(FollowUp, session);

        Assert.Equal("Done.", response.Text);
        Assert.Equal(FollowUp, trace.DerivedTexts[^1]);
    }

    [Fact]
    public async Task A_history_read_past_its_bound_joins_nothing_never_fails_the_run_and_is_reported()
    {
        var trace = new Trace();
        await using var services = trace.Host.Build(options => trace.RecordDerivedTexts(options, realTimers: true));
        var history = new FlakyHistory();
        var agent = Wire(explicitWiring: false, services, new ChatClientAgentOptions { ChatHistoryProvider = history }, trace.Model);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(Earlier, session);

        using var probe = TelemetryProbe.Start();
        history.Next = FlakyHistory.Failure.Hang;
        var response = await agent.RunAsync(FollowUp, session);

        Assert.Equal("Done.", response.Text);
        Assert.Equal(FollowUp, trace.DerivedTexts[^1]);
        Assert.Contains(probe.LibraryActivities, activity => Equals(activity.GetTagItem(InjectionDiagnostics.HistoryReadAttribute), "timed_out"));
    }

    [Fact]
    public async Task The_callers_cancellation_during_the_history_read_reaches_the_caller()
    {
        var trace = new Trace();
        await using var services = trace.Host.Build(trace.RecordDerivedTexts);
        using var caller = new CancellationTokenSource();
        var history = new FlakyHistory { Caller = caller };
        var agent = Wire(explicitWiring: false, services, new ChatClientAgentOptions { ChatHistoryProvider = history }, trace.Model);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(Earlier, session);
        var reads = history.Reads;
        var derived = trace.DerivedTexts.Count;

        history.Next = FlakyHistory.Failure.CancelCaller;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync(FollowUp, session, cancellationToken: caller.Token));

        // Only the agent-level read ran, and the injection never started: the run stopped there.
        Assert.Equal(reads + 1, history.Reads);
        Assert.Equal(derived, trace.DerivedTexts.Count);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="scenario"/> through the single-call wiring, then the explicit one.</summary>
    private static async Task<(Trace Single, Trace Explicit)> Both(
        Func<Trace, AIAgent, Task> scenario,
        Func<ChatClientAgentOptions>? options = null,
        bool useRunTools = false,
        Shape shape = Shape.None)
    {
        async Task<Trace> One(bool explicitWiring)
        {
            var trace = new Trace();
            await using var services = trace.Host.Build(configure =>
            {
                trace.RecordDerivedTexts(configure);
                if (useRunTools)
                {
                    trace.Model = new Model { Tool = EchoTool };
                    var injection = configure.Injection;
                    configure.Injection = settings =>
                    {
                        injection?.Invoke(settings);
                        settings.ReceivingAgent = new ReceivingAgentCapabilities { UseRunTools = true };
                    };
                }
            });
            var agent = Wire(explicitWiring, services, options?.Invoke() ?? new ChatClientAgentOptions(), trace.Model, shape);
            await scenario(trace, agent);
            return trace;
        }

        var single = await One(explicitWiring: false);
        return (single, await One(explicitWiring: true));
    }

    /// <summary>The agent under test: the provider on its options (the explicit wiring) or not, then <c>UseAgentExperience</c>.</summary>
    private static AIAgent Wire(bool explicitWiring, IServiceProvider services, ChatClientAgentOptions options, Model model, Shape shape = Shape.None)
    {
        if (explicitWiring)
        {
            options.AIContextProviders = [.. options.AIContextProviders ?? [], services.GetAgentExperienceContextProvider()];
        }

        var builder = new ChatClientAgent(model, options).AsBuilder();
        if (shape == Shape.MiddlewareBefore)
        {
            builder = PassThrough(builder);
        }

        builder = builder.UseAgentExperience(services);
        if (shape == Shape.MiddlewareAfter)
        {
            builder = PassThrough(builder);
        }

        var agent = builder.Build();

        // The single-call wiring adds the agent-level provider; the explicit one adds none, so either way one block.
        Assert.Equal(!explicitWiring, agent.GetService<ExperienceMessageContextProvider>() is not null);
        return agent;
    }

    private static AIAgentBuilder PassThrough(AIAgentBuilder builder) =>
        builder.Use(
            (messages, session, options, inner, cancellationToken) => inner.RunAsync(messages, session, options, cancellationToken),
            (messages, session, options, inner, cancellationToken) => inner.RunStreamingAsync(messages, session, options, cancellationToken));

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

    private static AdditionalPropertiesDictionary Passing(ChatHistoryProvider history) =>
        new() { [typeof(ChatHistoryProvider).FullName!] = history };

    private static InMemoryChatHistoryProvider Seeded(AgentSession session, string stateKey, string earlier)
    {
        var history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions { StateKey = stateKey });
        history.SetMessages(session, [new ChatMessage(ChatRole.User, earlier), new ChatMessage(ChatRole.Assistant, "Done.")]);
        return history;
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

    /// <summary>The system clock, except that no timer it creates ever fires, so no real-clock timeout can be hit.</summary>
    private sealed class NeverFiringTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Everything one wiring's scenario produced.</summary>
    private sealed class Trace
    {
        public SetupHost Host { get; } = new();

        public Model Model { get; set; } = new();

        public List<string?> DerivedTexts { get; } = [];

        public List<string> Errors { get; } = [];

        public void RecordDerivedTexts(AgentExperienceOptions options) => RecordDerivedTexts(options, realTimers: false);

        /// <param name="options">The options to record on.</param>
        /// <param name="realTimers">
        /// Only a test that needs a bound to fire (a hung history read) passes <see langword="true"/>. Every other test runs
        /// on a clock whose timers never fire, so a loaded CI machine cannot time out a retrieval, an eligibility re-read or
        /// a history read that would have finished, and make the two wirings disagree.
        /// </param>
        public void RecordDerivedTexts(AgentExperienceOptions options, bool realTimers)
        {
            if (!realTimers)
            {
                options.TimeProvider = new NeverFiringTimers();
            }
            else
            {
                // The shared host defaults to timers that never fire; a test that needs a bound to fire says so.
                options.TimeProvider = TimeProvider.System;
            }

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

    /// <summary>A history provider that replays one earlier turn whatever the session, and counts its reads.</summary>
    private sealed class FixedHistory(string earlier, string? stateKey = null) : ChatHistoryProvider
    {
        public int Reads { get; private set; }

        public override IReadOnlyList<string> StateKeys => stateKey is null ? base.StateKeys : [stateKey];

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult<IEnumerable<ChatMessage>>([new ChatMessage(ChatRole.User, earlier), new ChatMessage(ChatRole.Assistant, "Done.")]);
        }

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>An in-memory history whose next read can be made to fail, hang or cancel, once.</summary>
    private sealed class FlakyHistory : ChatHistoryProvider
    {
        private readonly List<ChatMessage> _messages = [];

        public enum Failure
        {
            None,
            Throw,
            Cancel,
            Hang,
            CancelCaller,
        }

        public Failure Next { get; set; }

        public int Reads { get; private set; }

        public CancellationTokenSource? Caller { get; init; }

        protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            Reads++;
            var failure = Next;
            Next = Failure.None;
            switch (failure)
            {
                case Failure.Throw:
                    throw new InvalidOperationException("the history store is down");
                case Failure.Cancel:
                    throw new OperationCanceledException("the history store gave up");
                case Failure.Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                case Failure.CancelCaller:
                    await Caller!.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
            }

            return _messages.ToArray();
        }

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            _messages.AddRange(context.RequestMessages.Concat(context.ResponseMessages ?? []));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A reducer that keeps the last messages and counts its calls.</summary>
    private sealed class CountingReducer(int keep) : IChatReducer
    {
        public int Calls { get; private set; }

        public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
        {
            Calls++;
            var list = messages.ToList();
            return Task.FromResult<IEnumerable<ChatMessage>>(list.Skip(Math.Max(0, list.Count - keep)).ToList());
        }
    }

    /// <summary>A context provider that only holds a state key.</summary>
    private sealed class KeyedProvider(string stateKey) : AIContextProvider
    {
        public override IReadOnlyList<string> StateKeys => [stateKey];

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AIContext());
    }
}
