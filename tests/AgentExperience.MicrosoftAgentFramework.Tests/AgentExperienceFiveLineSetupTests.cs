using System.Reflection;
using System.Runtime.CompilerServices;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The five-line setup: <c>UseDevelopmentIdentity()</c>, the task identifier taken from the agent's name, and
/// <c>UseAgentExperience</c> wiring injection as well as capture, over a real <see cref="ChatClientAgent"/> and a
/// scripted model, in ordinary and streaming runs.
/// </summary>
public sealed class AgentExperienceFiveLineSetupTests
{
    internal const string Task = "Reconcile the ledger for invoice 7731 and close it";

    private const string EchoTool = "echo";

    private const string RunOnlyTool = "run_only";

    private static readonly AIFunction Echo = AIFunctionFactory.Create((string note) => $"echo:{note}", EchoTool);

    private static readonly AIFunction RunOnly = AIFunctionFactory.Create((string note) => $"run:{note}", RunOnlyTool);

    internal static string DevelopmentSegment =>
        ExperienceNames.Reduce(Assembly.GetEntryAssembly()?.GetName().Name) ?? AgentExperienceOptions.DevelopmentApplication;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_UseAgentExperience_injects_the_first_runs_lesson_into_the_second(bool streaming)
    {
        var host = new Host();
        await using var services = host.Build();
        var model = new Model();
        AIAgent agent = new ChatClientAgent(model).AsBuilder().UseAgentExperience(services).Build();

        await Run(agent, Task, streaming);

        Assert.Empty(host.Failures);
        var first = Assert.Single(host.Finalized);
        Assert.Equal(FinalizationOutcome.Validated, first.Outcome);
        Assert.DoesNotContain(model.Calls[0], IsHistoricalReference);

        await Run(agent, Task, streaming);

        Assert.Empty(host.Failures);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.Equal([first.ExperienceId!.Value], host.Injections[1].InjectedExperienceIds);
        Assert.Single(model.Calls[1], IsHistoricalReference);

        // Captured and stored under the development identity, and the unnamed agent's task identifier.
        var identity = host.Verifications[0].Identity;
        // The application and project come from the host environment's application name.
        Assert.Equal(new Scope("local", "tests", "tests"), identity.Scope);
        Assert.Equal("local-developer", identity.Authorization.PrincipalId);
        Assert.Empty(identity.Authorization.Roles);
        Assert.True(identity.Authorization.Permits(identity.Scope));
        var stored = await services.GetRequiredService<IExperienceRecordStore>()
            .GetAsync(identity.Authorization, identity.Scope, first.ExperienceId.Value, CancellationToken.None);
        Assert.Equal(AgentExperienceOptions.DefaultTaskId, stored.Record!.TaskId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_two_line_wiring_gets_exactly_one_block_and_nothing_is_added(bool streaming)
    {
        var host = new Host();
        await using var services = host.Build();
        var model = new Model();
        var inner = new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] });
        var agent = inner.AsBuilder().UseAgentExperience(services).Build();

        await Run(agent, Task, streaming);
        await Run(agent, Task, streaming);

        Assert.Empty(host.Failures);
        Assert.Equal(2, host.Injections.Count);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);

        // Every model call carries at most one block, and no agent-level provider was put in the pipeline.
        Assert.Equal(2, model.Calls.Count);
        Assert.DoesNotContain(model.Calls[0], IsHistoricalReference);
        Assert.Single(model.Calls[1], IsHistoricalReference);
        Assert.Null(agent.GetService<ExperienceMessageContextProvider>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_UseAgentExperience_calls_on_one_pipeline_inject_one_block(bool streaming)
    {
        var host = new Host();
        await using var services = host.Build();
        var model = new Model();
        var agent = new ChatClientAgent(model).AsBuilder().UseAgentExperience(services).UseAgentExperience(services).Build();

        await Run(agent, Task, streaming);
        await Run(agent, Task, streaming);

        Assert.Equal(2, model.Calls.Count);
        Assert.DoesNotContain(model.Calls[0], IsHistoricalReference);
        Assert.Single(model.Calls[1], IsHistoricalReference);
        Assert.NotNull(agent.GetService<ExperienceMessageContextProvider>());
    }

    [Fact]
    public async Task With_InjectWithUseAgentExperience_off_the_agent_is_captured_but_not_injected()
    {
        var host = new Host();
        await using var services = host.Build(options => options.InjectWithUseAgentExperience = false);
        var model = new Model();
        var agent = new ChatClientAgent(model).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);
        await agent.RunAsync(Task);

        Assert.Empty(host.Failures);
        Assert.Equal(2, host.Finalized.Count);
        Assert.Empty(host.Injections);
        Assert.All(model.Calls, call => Assert.DoesNotContain(call, IsHistoricalReference));
        Assert.Null(agent.GetService<ExperienceMessageContextProvider>());
    }

    [Fact]
    public async Task Under_ReuseEvidence_the_single_call_wiring_counts_the_injected_lesson()
    {
        var host = new Host();
        await using var services = host.Build(options => options.ReuseEvidence = ReuseEvidenceMode.SameTask);
        var agent = new ChatClientAgent(new Model(), name: "Ledger Bot").AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);
        var lesson = Assert.Single(host.Finalized).ExperienceId!.Value;
        await agent.RunAsync(Task);

        Assert.Empty(host.Failures);
        Assert.Equal([lesson], host.Injections[1].InjectedExperienceIds);
        var applied = Assert.Single(host.Finalized[1].ReuseEvidence);
        Assert.Equal(lesson, applied.ExperienceId);
        Assert.Equal(ConfidenceEvidenceKind.Supporting, applied.Kind);
        Assert.Equal(AgentExperience.Core.Lifecycle.ConfidenceUpdateOutcome.Applied, applied.Outcome);
        Assert.True(applied.Counted);
    }

    [Fact]
    public async Task At_the_agent_level_a_short_follow_up_is_joined_with_the_replayed_history()
    {
        async Task<List<string?>> TextsOf(bool twoLines)
        {
            var texts = new List<string?>();
            var host = new Host();
            await using var services = host.Build(options => options.Injection = settings =>
            {
                var resolve = settings.ResolveRequestAsync!;
                settings.ResolveRequestAsync = (context, cancellationToken) =>
                {
                    texts.Add(context.DerivedTaskText);
                    return resolve(context, cancellationToken);
                };
            });
            var model = new Model();
            var inner = twoLines
                ? new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
                : new ChatClientAgent(model);
            var agent = inner.AsBuilder().UseAgentExperience(services).Build();
            var session = await agent.CreateSessionAsync();
            await agent.RunAsync("Reconcile the ledger for invoice 7731", session);
            await agent.RunAsync("and close it", session);
            return texts;
        }

        Assert.Equal("Reconcile the ledger for invoice 7731 \u2014 and close it", (await TextsOf(twoLines: true))[1]);
        Assert.Equal("Reconcile the ledger for invoice 7731 \u2014 and close it", (await TextsOf(twoLines: false))[1]);
    }

    [Fact]
    public async Task A_faulted_model_call_settles_the_staged_delivery_as_on_the_agents_options()
    {
        async Task<List<string>> TraceOf(bool twoLines)
        {
            var host = new Host();
            await using var services = host.Build();
            var model = new Model();
            var inner = twoLines
                ? new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
                : new ChatClientAgent(model);
            var agent = inner.AsBuilder().UseAgentExperience(services).Build();

            await agent.RunAsync(Task);
            var session = await agent.CreateSessionAsync();
            model.ThrowNext = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(Task, session));
            await agent.RunAsync(Task, session);

            return host.Injections
                .Select(result => $"{result.Outcome} injected={result.InjectedExperienceIds.Count} "
                    + $"omitted=[{string.Join(",", result.Omitted.Select(omission => omission.Reason))}]")
                .ToList();
        }

        var agentLevel = await TraceOf(twoLines: false);
        Assert.Equal(await TraceOf(twoLines: true), agentLevel);

        // The failed run's delivery was discarded, so the session is given the lesson again, not told it holds it.
        Assert.StartsWith("Injected", agentLevel[2], StringComparison.Ordinal);
        Assert.DoesNotContain("AlreadyDelivered", agentLevel[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_development_identity_is_issued_at_each_invocation_by_the_configured_clock()
    {
        var clock = new SettableClock { Now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero) };
        var host = new Host();
        await using var services = host.Build(options => options.TimeProvider = clock);
        var agent = new ChatClientAgent(new Model()).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);
        clock.Now = clock.Now.AddHours(1);
        await agent.RunAsync(Task);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), host.Verifications[0].Identity.Authorization.IssuedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), host.Verifications[1].Identity.Authorization.IssuedAt);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task A_Generic_Host_with_the_development_identity_refuses_to_start_outside_development(string environment, bool refused)
    {
        var host = new Host { Environment = environment, InMemoryOverride = true };
        await using var services = host.Build();
        var hosted = services.GetServices<IHostedService>().ToList();
        Assert.NotEmpty(hosted);

        async Task StartAll()
        {
            foreach (var service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }
        }

        if (refused)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(StartAll);
            Assert.Contains("UseDevelopmentIdentity", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            await StartAll();
        }
    }

    [Fact]
    public async Task Both_wirings_hand_the_model_the_same_block()
    {
        async Task<string> BlockOf(bool twoLines)
        {
            var host = new Host();
            await using var services = host.Build();
            var model = new Model();
            var inner = twoLines
                ? new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
                : new ChatClientAgent(model);
            var agent = inner.AsBuilder().UseAgentExperience(services).Build();
            await agent.RunAsync(Task);
            await agent.RunAsync(Task);
            return Assert.Single(model.Calls[1], IsHistoricalReference).Text;
        }

        Assert.Equal(await BlockOf(twoLines: true), await BlockOf(twoLines: false));
    }

    [Fact]
    public async Task A_reused_session_is_tracked_at_the_agent_level_as_on_the_agents_options()
    {
        async Task<List<string>> TraceOf(bool twoLines)
        {
            var host = new Host();
            await using var services = host.Build();
            var model = new Model();
            var inner = twoLines
                ? new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
                : new ChatClientAgent(model);
            var agent = inner.AsBuilder().UseAgentExperience(services).Build();

            await agent.RunAsync(Task);
            var session = await agent.CreateSessionAsync();
            await agent.RunAsync(Task, session);
            await agent.RunAsync(Task, session);
            await agent.RunAsync(Task, session);

            Assert.Empty(host.Failures);
            return host.Injections
                .Select((result, index) =>
                    $"{result.Outcome} injected={result.InjectedExperienceIds.Count} "
                    + $"omitted=[{string.Join(",", result.Omitted.Select(omission => omission.Reason))}] "
                    + $"blocks={model.Calls[index].Count(IsHistoricalReference)}")
                .ToList();
        }

        var agentLevel = await TraceOf(twoLines: false);
        Assert.Equal(await TraceOf(twoLines: true), agentLevel);

        // The session's account deduplicated what it already held.
        Assert.Contains("AlreadyDelivered", agentLevel[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_UseRunTools_the_agent_level_sees_the_agents_default_tools_and_a_runs_own()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Injection = injection =>
            injection.ReceivingAgent = new ReceivingAgentCapabilities { UseRunTools = true });

        // A verified run whose working attempt calls the echo tool.
        var teacher = new ChatClientAgent(new Model { Tool = EchoTool }, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [Echo] } })
            .AsBuilder().UseAgentExperience(services).Build();
        await teacher.RunAsync(Task);
        Assert.Empty(host.Failures);
        Assert.Single(host.Finalized);

        // The tool on the agent's options is seen: the record is injected.
        var withDefault = new Model { Tool = EchoTool };
        var defaultAgent = new ChatClientAgent(withDefault, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [Echo] } })
            .AsBuilder().UseAgentExperience(services).Build();
        await defaultAgent.RunAsync(Task);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[^1].Outcome);
        Assert.Single(withDefault.Calls[0], IsHistoricalReference);

        // The same tool given only on the run's own options is seen at the agent level too: the record is injected.
        var runOnly = new Model { Tool = EchoTool };
        var runAgent = new ChatClientAgent(runOnly, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [RunOnly] } })
            .AsBuilder().UseAgentExperience(services).Build();
        await runAgent.RunAsync(Task, options: new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo] }));
        Assert.Equal(InjectionOutcome.Injected, host.Injections[^1].Outcome);
        Assert.Single(runOnly.Calls[0], IsHistoricalReference);

        // Without it, neither on the run nor on the agent, the record is withheld.
        var without = new Model { Tool = EchoTool };
        var withoutAgent = new ChatClientAgent(without, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [RunOnly] } })
            .AsBuilder().UseAgentExperience(services).Build();
        await withoutAgent.RunAsync(Task);
        var withheld = host.Injections[^1];
        Assert.NotEqual(InjectionOutcome.Injected, withheld.Outcome);
        Assert.Contains(withheld.Omitted, omission => omission.Reason == InjectionOmissionReason.ToolUnavailable);
        Assert.DoesNotContain(without.Calls[0], IsHistoricalReference);

        // On the agent's options, the provider sees the run's tools, as before.
        var seesRun = new Model { Tool = EchoTool };
        var twoLines = new ChatClientAgent(seesRun, new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Tools = [RunOnly] },
                AIContextProviders = [services.GetAgentExperienceContextProvider()],
            })
            .AsBuilder().UseAgentExperience(services).Build();
        await twoLines.RunAsync(Task, options: new ChatClientAgentRunOptions(new ChatOptions { Tools = [Echo] }));
        Assert.Equal(InjectionOutcome.Injected, host.Injections[^1].Outcome);
        Assert.Single(seesRun.Calls[0], IsHistoricalReference);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public async Task The_development_identity_refuses_any_other_environment(string environment)
    {
        var host = new Host { Environment = environment, InMemoryOverride = true };
        await using var services = host.Build();

        var refused = Assert.Throws<InvalidOperationException>(() => new ChatClientAgent(new Model()).AsBuilder().UseAgentExperience(services));
        Assert.Contains("UseDevelopmentIdentity", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Development, Test, Testing", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"the host environment is '{environment}'", refused.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => services.GetAgentExperienceContextProvider());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("test")]
    [InlineData("Testing")]
    public async Task The_development_identity_runs_in_a_development_or_test_environment(string environment)
    {
        var host = new Host { Environment = environment };
        await using var services = host.Build();
        var model = new Model();
        var agent = new ChatClientAgent(model).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);

        Assert.Empty(host.Failures);
        Assert.Single(host.Finalized);
    }

    [Fact]
    public async Task A_resolver_set_after_the_development_identity_replaces_it_and_its_environment_check()
    {
        var host = new Host { Environment = "Production", InMemoryOverride = true };
        var caller = new ExperienceIdentity(
            new AuthorizationContext("contoso", "svc", [], DateTimeOffset.UnixEpoch),
            new Scope("contoso", "support", "tickets"));
        await using var services = host.Build(options =>
            options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(caller));
        var agent = new ChatClientAgent(new Model()).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);

        Assert.Empty(host.Failures);
        Assert.Equal(caller, host.Verifications[0].Identity);
    }

    [Fact]
    public async Task The_development_identity_takes_a_tenant()
    {
        var host = new Host { Tenant = "acme" };
        await using var services = host.Build();
        await new ChatClientAgent(new Model()).AsBuilder().UseAgentExperience(services).Build().RunAsync(Task);

        Assert.Equal("acme", host.Verifications[0].Identity.Scope.TenantId);
        Assert.Equal("acme", host.Verifications[0].Identity.Authorization.TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_development_tenant_is_refused(string tenant) =>
        Assert.Throws<ArgumentException>(() => new AgentExperienceOptions().UseDevelopmentIdentity(tenant));

    [Theory]
    [InlineData("Triage Bot", "triage-bot")]
    [InlineData(null, AgentExperienceOptions.DefaultTaskId)]
    [InlineData("   ", AgentExperienceOptions.DefaultTaskId)]
    [InlineData("!!!", AgentExperienceOptions.DefaultTaskId)]
    public async Task With_no_task_identifier_set_a_run_is_captured_under_the_agents_name(string? name, string expected)
    {
        var host = new Host();
        await using var services = host.Build();
        var agent = new ChatClientAgent(new Model(), name: name).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);

        Assert.Empty(host.Failures);
        Assert.Equal(expected, host.Verifications[0].Run.TaskId);
    }

    [Fact]
    public async Task A_task_identifier_the_host_set_wins_over_the_agents_name()
    {
        var host = new Host();
        await using var services = host.Build(options => options.TaskId = "triage-ticket");
        var agent = new ChatClientAgent(new Model(), name: "Triage Bot").AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(Task);

        Assert.Equal("triage-ticket", host.Verifications[0].Run.TaskId);
    }

    [Theory]
    [InlineData("Triage Bot", "triage-bot")]
    [InlineData("Triage - Bot", "triage-bot")]
    [InlineData("a---b", "a-b")]
    [InlineData("---", null)]
    [InlineData("AgentExperience.Sample.QuickStart", "agentexperience.sample.quickstart")]
    [InlineData("  ops / on-call (EU)  ", "ops-on-call-eu")]
    [InlineData("café_bot", "caf-_bot")]
    [InlineData("--x--", "x")]
    [InlineData("é", null)]
    [InlineData(null, null)]
    public void A_name_is_reduced_to_an_identifier(string? name, string? expected) =>
        Assert.Equal(expected, ExperienceNames.Reduce(name));

    [Theory]
    [InlineData(98)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void A_long_name_is_cut_without_fusing_two_words(int length)
    {
        // A first word of the given length, then a second one: the second fits only after a separator.
        var reduced = ExperienceNames.Reduce(new string('a', length) + " b")!;

        Assert.True(reduced.Length <= ExperienceNames.MaxLength);
        Assert.Equal(length <= 98 ? new string('a', length) + "-b" : new string('a', Math.Min(length, 100)), reduced);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void A_single_word_is_cut_at_the_maximum(int length) =>
        Assert.Equal(new string('a', Math.Min(length, ExperienceNames.MaxLength)), ExperienceNames.Reduce(new string('a', length)));

    private static async Task Run(AIAgent agent, string text, bool streaming)
    {
        if (!streaming)
        {
            await agent.RunAsync(text);
            return;
        }

        await foreach (var _ in agent.RunStreamingAsync(text))
        {
        }
    }

    private static async ValueTask<ExperienceVerification?> Passing(ExperienceVerificationContext context, CancellationToken cancellationToken)
    {
        await System.Threading.Tasks.Task.Yield();
        return new ExperienceVerification(
            RequiredChecks: [new RequiredCheck("tests-pass", "TestResult")],
            Evidence: [context.CreateEvidence("tests-pass", "TestResult", CheckResult.Pass, producer: "ci", "build-42")],
            ArtifactRevision: "build-42");
    }

    private static bool IsHistoricalReference(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true;

    /// <summary>A model that keeps every call's messages, optionally calls one tool first, and streams too.</summary>
    internal sealed class Model : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = [];

        public string? Tool { get; init; }

        /// <summary>Makes the next call throw, once.</summary>
        public bool ThrowNext { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new ChatResponse(Answer(messages)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var answer = Answer(messages);
            await System.Threading.Tasks.Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. answer.Contents]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private ChatMessage Answer(IEnumerable<ChatMessage> messages)
        {
            var list = messages.ToList();
            if (ThrowNext)
            {
                ThrowNext = false;
                Calls.Add(list);
                throw new InvalidOperationException("the model is down");
            }

            var toolTurn = Tool is not null && !list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (!toolTurn)
            {
                Calls.Add(list);
                return new ChatMessage(ChatRole.Assistant, "Done.");
            }

            Calls.Add(list);
            return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-0", Tool!, new Dictionary<string, object?> { ["note"] = "n" })]);
        }
    }

    /// <summary>The host side: the five-line setup, and everything the library reported back.</summary>
    internal sealed class Host
    {
        public string Environment { get; init; } = Environments.Development;

        public string? Tenant { get; init; }

        public bool InMemoryOverride { get; init; }

        /// <summary>Registers no <see cref="IHostEnvironment"/>, so the guard reads the process environment variables.</summary>
        public bool NoHostEnvironment { get; init; }

        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public List<ExperienceVerificationContext> Verifications { get; } = [];

        public ServiceProvider Build(Action<AgentExperienceOptions>? configure = null)
        {
            var collection = new ServiceCollection();
            if (!NoHostEnvironment)
            {
                collection.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(Environment));
            }

            collection
                .AddAgentExperience(options =>
                {
                    options.UseDevelopmentIdentity(Tenant);
                    options.Verify = (context, cancellationToken) =>
                    {
                        Verifications.Add(context);
                        return Passing(context, cancellationToken);
                    };
                    configure?.Invoke(options);
                    var injection = options.Injection;
                    options.Injection = settings =>
                    {
                        injection?.Invoke(settings);
                        settings.OnContextInjected = Injections.Add;
                    };
                    options.Capture = capture =>
                    {
                        capture.OnCaptureFailure = Failures.Add;
                        capture.OnRunFinalized = Finalized.Add;
                    };
                })
                .UseInMemoryStorageForDevelopment(storage => storage.AllowProductionEnvironment = InMemoryOverride);
            return collection.BuildServiceProvider();
        }
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = "/";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>Runs the tests that change the process's environment variables alone, never beside another test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DevelopmentIdentityEnvironmentCollection
{
    public const string Name = "Development identity: process environment variables";
}

/// <summary>
/// With no <see cref="IHostEnvironment"/> registered, the development identity's guard reads <c>DOTNET_ENVIRONMENT</c>,
/// then <c>ASPNETCORE_ENVIRONMENT</c>, as the in-memory store's does. Each test sets both and restores them.
/// </summary>
[Collection(DevelopmentIdentityEnvironmentCollection.Name)]
public sealed class DevelopmentIdentityEnvironmentVariableTests : IDisposable
{
    private readonly string? _dotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
    private readonly string? _aspnetcore = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _dotnet);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _aspnetcore);
    }

    [Theory]
    [InlineData("Production", null, "DOTNET_ENVIRONMENT is 'Production'")]
    [InlineData(null, "Production", "ASPNETCORE_ENVIRONMENT is 'Production'")]
    [InlineData("Staging", "Development", "DOTNET_ENVIRONMENT is 'Staging'")]
    public async Task Without_a_host_environment_the_variables_are_read(string? dotnet, string? aspnetcore, string named)
    {
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnetcore);
        var host = new AgentExperienceFiveLineSetupTests.Host { NoHostEnvironment = true, InMemoryOverride = true };
        await using var services = host.Build();

        var refused = Assert.Throws<InvalidOperationException>(() => new ChatClientAgent(new AgentExperienceFiveLineSetupTests.Model()).AsBuilder().UseAgentExperience(services));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData(null, "Testing")]
    [InlineData(null, null)]
    public async Task Without_a_host_environment_a_development_variable_or_none_is_allowed(string? dotnet, string? aspnetcore)
    {
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnetcore);
        var host = new AgentExperienceFiveLineSetupTests.Host { NoHostEnvironment = true };
        await using var services = host.Build();
        var agent = new ChatClientAgent(new AgentExperienceFiveLineSetupTests.Model()).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(AgentExperienceFiveLineSetupTests.Task);

        // With no host environment, the application is named after the entry assembly.
        var segment = AgentExperienceFiveLineSetupTests.DevelopmentSegment;
        Assert.Equal(new Scope("local", segment, segment), host.Verifications[0].Identity.Scope);
    }
}
