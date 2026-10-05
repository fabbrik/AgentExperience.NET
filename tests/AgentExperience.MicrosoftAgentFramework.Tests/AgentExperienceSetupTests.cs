using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The one-call setup: <c>AddAgentExperience</c>, a storage choice, <c>UseAgentExperience</c> and the container's
/// context provider, over the in-memory storage. One test per row of the story's matrix, using only the new API.
/// </summary>
public sealed class AgentExperienceSetupTests
{
    private const string Revision = "build-42";

    private static readonly ExperienceIdentity Caller = new(
        new AuthorizationContext("contoso", "svc-support-agent", [], DateTimeOffset.UnixEpoch),
        new Scope("contoso", "support", "tickets"));

    [Fact]
    public async Task A_verified_first_run_is_injected_into_the_second()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Verify = Passing);
        var (agent, model) = AgentOver(services);

        await agent.RunAsync("Reconcile the ledger for invoice 7731 and close it");

        Assert.Empty(host.Failures);
        var first = Assert.Single(host.Finalized);
        Assert.Equal(FinalizationOutcome.Validated, first.Outcome);
        Assert.Equal(InjectionOutcome.NothingToInject, Assert.Single(host.Injections).Outcome);
        Assert.DoesNotContain(model.LastMessages!, IsHistoricalReference);

        var stored = await services.GetRequiredService<IExperienceRecordStore>()
            .GetAsync(Caller.Authorization, Caller.Scope, first.ExperienceId!.Value, CancellationToken.None);
        var record = stored.Record!;
        Assert.Equal(AgentExperienceOptions.DefaultTaskId, record.TaskId);
        Assert.Equal("Reconcile the ledger for invoice 7731 and close it", record.TaskSummary);

        // Text search matches every term of the new task, as PostgreSQL's does.
        await agent.RunAsync("Reconcile the ledger for invoice 7731");

        Assert.Empty(host.Failures);
        var second = host.Injections[1];
        Assert.Equal(InjectionOutcome.Injected, second.Outcome);
        Assert.Equal([record.ExperienceId], second.InjectedExperienceIds);
        var block = Assert.Single(model.LastMessages!, IsHistoricalReference);
        Assert.Contains(record.Reflection!.Lesson, block.Text, StringComparison.Ordinal);

        // The library filled in the request the explicit wiring asks the host for.
        Assert.Equal(2, host.Finalized.Count);
        Assert.All(host.Verifications, context => Assert.Equal(Caller, context.Identity));
        Assert.NotEqual(host.Verifications[0].RoundId, host.Verifications[1].RoundId);
    }

    [Fact]
    public async Task With_no_identity_the_invocation_is_neither_injected_nor_captured()
    {
        var host = new Host { Identity = null };
        var taskIdCalls = 0;
        await using var services = host.Build(options =>
        {
            options.Verify = Passing;
            options.ResolveTaskId = _ =>
            {
                taskIdCalls++;
                return "triage";
            };
        });
        var (agent, model) = AgentOver(services);
        var session = await agent.CreateSessionAsync();

        var response = await agent.RunAsync("Reconcile the ledger for invoice 7731", session);

        Assert.Equal("Hello, world", response.Text);
        Assert.Empty(host.Failures);
        Assert.Empty(host.Verifications);
        Assert.Empty(host.Finalized);
        Assert.Equal(0, taskIdCalls);
        Assert.False(session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out _));
        Assert.Equal(InjectionOutcome.Skipped, Assert.Single(host.Injections).Outcome);
        Assert.DoesNotContain(model.LastMessages!, IsHistoricalReference);

        // Asked once, before capture; injection reuses the answer, none.
        Assert.Equal(1, host.IdentityCalls);
    }

    [Fact]
    public async Task Capture_resolves_the_identity_once_and_injection_reuses_it()
    {
        var host = new Host();
        await using var services = host.Build(options => options.ResolveTaskId = _ => "triage");
        var (agent, _) = AgentOver(services);

        await agent.RunAsync("Reconcile the ledger for invoice 7731");

        Assert.Equal(1, host.IdentityCalls);
        Assert.Empty(host.Failures);
    }

    [Fact]
    public async Task Without_Verify_a_run_is_captured_and_never_finalized()
    {
        var host = new Host();
        await using var services = host.Build(options => options.TaskId = "triage");
        var (agent, _) = AgentOver(services);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("Reconcile the ledger for invoice 7731", session);

        Assert.Empty(host.Failures);
        Assert.Empty(host.Finalized);
        var runId = Guid.Parse(session.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey)!);
        Assert.True(services.GetRequiredService<IExperienceCaptureService>().TryGetRun(runId, out var run));
        Assert.Equal("triage", run.TaskId);
        Assert.Equal(Caller.Scope, run.Scope);
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal("Reconcile the ledger for invoice 7731", run.TaskDescription);
    }

    [Fact]
    public async Task A_null_verdict_finalizes_nothing_and_reports_nothing()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Verify = (_, _) => ValueTask.FromResult<ExperienceVerification?>(null));
        var (agent, _) = AgentOver(services);

        await agent.RunAsync("Reconcile the ledger for invoice 7731");

        Assert.Empty(host.Finalized);
        Assert.Empty(host.Failures);
    }

    [Fact]
    public async Task A_throwing_Verify_is_reported_and_the_invocation_is_unaffected()
    {
        var host = new Host();
        var thrown = new InvalidOperationException("the test run could not start");
        await using var services = host.Build(options => options.Verify = (_, _) => throw thrown);
        var (agent, _) = AgentOver(services);

        var response = await agent.RunAsync("Reconcile the ledger for invoice 7731");

        Assert.Equal("Hello, world", response.Text);
        Assert.Empty(host.Finalized);
        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.Same(thrown, failure.Exception);
    }

    [Fact]
    public async Task A_throwing_ResolveIdentity_is_reported_and_the_invocation_is_unaffected()
    {
        var host = new Host { ThrowFromIdentity = true };
        await using var services = host.Build(options => options.Verify = Passing);
        var (agent, model) = AgentOver(services);

        var response = await agent.RunAsync("Reconcile the ledger for invoice 7731");

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, Assert.Single(host.Failures).Stage);
        Assert.Equal(InjectionOutcome.Failed, Assert.Single(host.Injections).Outcome);
        Assert.Equal(1, host.IdentityCalls);
        Assert.Empty(host.Finalized);
        Assert.DoesNotContain(model.LastMessages!, IsHistoricalReference);
    }

    [Fact]
    public async Task Without_a_storage_choice_resolving_fails_with_a_clear_message()
    {
        var collection = new ServiceCollection();
        collection.AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller));
        await using var services = collection.BuildServiceProvider();

        var building = Assert.Throws<InvalidOperationException>(() =>
            new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions()).AsBuilder().UseAgentExperience(services));
        var resolving = Assert.Throws<InvalidOperationException>(() => services.GetAgentExperienceContextProvider());

        foreach (var refused in new[] { building, resolving })
        {
            Assert.Contains("UseInMemoryStorageForDevelopment", refused.Message, StringComparison.Ordinal);
            Assert.Contains("UsePostgres", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_AddAgentExperience_the_agent_side_says_so()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions()).AsBuilder().UseAgentExperience(services));

        Assert.Contains("AddAgentExperience", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_options_are_checked_when_they_are_registered()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAgentExperience(_ => { }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAgentExperience(options =>
        {
            options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller);
            options.TaskId = "triage";
            options.ResolveTaskId = _ => "triage";
        }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAgentExperience(options =>
        {
            options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller);
            options.TaskId = " ";
        }));

        var collection = new ServiceCollection();
        collection.AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller));
        Assert.Throws<InvalidOperationException>(() =>
            collection.AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller)));
    }

    [Fact]
    public async Task The_defaults_are_registered_and_one_provider_is_shared()
    {
        var host = new Host();
        await using var services = host.Build(_ => { });

        Assert.Same(AgentExperienceDefaults.Sanitization, services.GetRequiredService<SanitizationOptions>());
        Assert.Same(AgentExperienceDefaults.CaptureLimits, services.GetRequiredService<CaptureLimits>());
        Assert.Same(services.GetAgentExperienceContextProvider(), services.GetAgentExperienceContextProvider());
    }

    [Fact]
    public async Task Under_the_defaults_a_tool_argument_value_is_not_captured_and_a_secret_named_one_is_redacted()
    {
        var host = new Host();
        await using var services = host.Build(_ => { });
        var (agent, session) = await ToolAgentOver(services);

        await agent.RunAsync("Echo the note", session);

        Assert.Empty(host.Failures);
        var call = Assert.Single(Assert.Single(RunOf(services, session).Attempts).ToolCalls);
        Assert.Equal(EchoTool, call.ToolName);
        Assert.False(call.Arguments.ContainsKey("note"));
        Assert.NotEqual("sk-not-a-real-key", call.Arguments["apiKey"]);
    }

    [Fact]
    public async Task The_hooks_apply_after_the_defaults_and_take_effect()
    {
        var host = new Host();
        var issued = new List<Guid>();
        var at = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        await using var services = host.Build(options =>
        {
            options.Verify = Passing;
            options.Injection = injection => injection.Rendering = HistoricalReferenceRendering.Verbose;
            options.Capture = capture =>
            {
                capture.CaptureToolCalls = false;
                capture.TimeProvider = new FrozenTimeProvider(at);
                capture.NewId = () =>
                {
                    var id = Guid.NewGuid();
                    lock (issued)
                    {
                        issued.Add(id);
                    }

                    return id;
                };
            };
        });
        var (agent, session) = await ToolAgentOver(services);

        await agent.RunAsync("Echo the note for invoice 7731", session);
        await agent.RunAsync("Echo the note for invoice 7731", session);

        Assert.Empty(host.Failures);
        Assert.Empty(Assert.Single(RunOf(services, session).Attempts).ToolCalls);
        Assert.All(host.Verifications, context => Assert.Contains(context.RoundId, issued));
        Assert.All(host.Verifications, context => Assert.Equal(at, context.CreateEvidence("c", "k", CheckResult.Pass, "p", Revision).CapturedAt));
        var injected = host.Injections[1];
        Assert.Equal(InjectionOutcome.Injected, injected.Outcome);
    }

    [Fact]
    public async Task The_injection_hook_applies_after_the_defaults()
    {
        var host = new Host();
        await using var services = host.Build(options =>
        {
            options.Verify = Passing;
            options.Injection = injection => injection.Rendering = HistoricalReferenceRendering.Verbose;
        });
        var (agent, model) = AgentOver(services);

        await agent.RunAsync("Reconcile the ledger for invoice 7731");
        await agent.RunAsync("Reconcile the ledger for invoice 7731");

        var block = Assert.Single(model.LastMessages!, IsHistoricalReference);
        Assert.Contains("Applicability", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_injection_options_changed_after_the_provider_is_built_change_nothing()
    {
        var host = new Host();
        await using var services = host.Build(_ => { });
        var asked = new List<string>();
        var options = new ExperienceInjectionOptions
        {
            ResolveRequest = _ =>
            {
                asked.Add("built with");
                return null;
            },
        };
        var provider = new ExperienceContextProvider(
            services.GetRequiredService<Core.Retrieval.ExperienceRetrievalService>(),
            services.GetRequiredService<IExperienceRecordStore>(),
            options);
        options.ResolveRequest = _ =>
        {
            asked.Add("changed later");
            return null;
        };
        options.ApproachArguments["echo"] = ["note"];

        await new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [provider] }).RunAsync("Reconcile");

        Assert.Equal(["built with"], asked);
    }

    [Fact]
    public async Task A_run_continued_under_the_same_identity_is_continued()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Capture = ContinueFromSession);
        var (agent, _) = AgentOver(services);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("Reconcile the ledger", session);
        host.Identity = Caller with { Authorization = Caller.Authorization with { IssuedAt = DateTimeOffset.UnixEpoch.AddHours(1) } };
        await agent.RunAsync("and retry", session);

        Assert.Empty(host.Failures);
        Assert.Equal(2, RunOf(services, session).Attempts.Count);
    }

    [Fact]
    public async Task A_run_is_not_continued_under_another_identity()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Capture = ContinueFromSession);
        var (agent, _) = AgentOver(services);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("Reconcile the ledger", session);
        host.Identity = Caller with { Authorization = Caller.Authorization with { PrincipalId = "someone-else" } };
        var response = await agent.RunAsync("and retry", session);

        Assert.Equal("Hello, world", response.Text);
        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Contains("different identity", failure.Reason, StringComparison.Ordinal);
        Assert.Single(RunOf(services, session).Attempts);
    }

    [Fact]
    public async Task A_run_the_bound_closes_is_finalized_under_the_identity_it_was_opened_with()
    {
        var host = new Host();
        var clock = new ManualBoundTimeProvider();
        await using var services = host.Build(options =>
        {
            options.TimeProvider = clock;
            options.Verify = Passing;
            options.Capture = capture => capture.ShouldCompleteRun = _ => false;
        });
        var (agent, _) = AgentOver(services);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("Reconcile the ledger", session);
        Assert.Empty(host.Finalized);

        host.Identity = null;
        Assert.Single(clock.Bounds).Fire();
        await WaitUntil(() => host.Finalized.Count == 1);

        Assert.NotNull(Assert.Single(host.Finalized).ExperienceId);
        Assert.Equal(Caller, Assert.Single(host.Verifications).Identity);
        Assert.DoesNotContain(host.Failures, failure => failure.Stage == ExperienceCaptureFailureStage.Finalization);
    }

    [Fact]
    public async Task A_slow_ResolveIdentity_times_out_and_the_invocation_runs_without_memory()
    {
        var host = new Host
        {
            IdentityOverride = async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Caller;
            },
        };
        await using var services = host.Build(options => options.IdentityTimeout = TimeSpan.FromMilliseconds(50));
        var (agent, model) = AgentOver(services);

        var response = await agent.RunAsync("Reconcile the ledger");

        Assert.Equal("Hello, world", response.Text);
        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, failure.Stage);
        Assert.IsType<TimeoutException>(failure.Exception);
        Assert.Equal(InjectionOutcome.Failed, Assert.Single(host.Injections).Outcome);
        Assert.Equal(1, host.IdentityCalls);
        Assert.DoesNotContain(model.LastMessages!, IsHistoricalReference);
    }

    [Fact]
    public async Task Evidence_for_another_revision_is_reported_and_nothing_is_stored()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Verify = (context, _) => ValueTask.FromResult<ExperienceVerification?>(new(
            [new RequiredCheck("tests-pass", "TestResult")],
            [context.CreateEvidence("tests-pass", "TestResult", CheckResult.Pass, "ci", "build-41")],
            Revision)));
        var (agent, _) = AgentOver(services);

        await agent.RunAsync("Reconcile the ledger");

        Assert.Empty(host.Finalized);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, Assert.Single(host.Failures).Stage);
    }

    [Fact]
    public async Task A_blank_task_identifier_runs_the_invocation_uncaptured_and_is_reported()
    {
        var host = new Host();
        await using var services = host.Build(options => options.ResolveTaskId = _ => " ");
        var (agent, _) = AgentOver(services);

        await agent.RunAsync("Reconcile the ledger");

        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, Assert.Single(host.Failures).Stage);
    }

    [Fact]
    public async Task Verify_can_be_unit_tested_with_a_context_of_its_own()
    {
        var run = new ExperienceRun(
            Guid.NewGuid(), "triage", null, Caller.Scope,
            new EnvironmentFingerprint("host", "net10.0", "os", null, new Dictionary<string, string>()),
            new Provenance("test", null, DateTimeOffset.UnixEpoch, null),
            [], RunExecutionStatus.Completed, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var roundId = Guid.NewGuid();

        var verification = await Passing(ExperienceVerificationContext.Create(run, roundId, Caller), CancellationToken.None);

        var evidence = Assert.Single(verification!.Evidence);
        Assert.Equal(roundId, evidence.VerificationRoundId);
        Assert.Equal(Revision, evidence.ArtifactRevision);
    }

    [Fact]
    public async Task A_Capture_hook_that_sets_the_sync_resolver_beside_Verify_is_refused()
    {
        var host = new Host();
        await using var services = host.Build(options =>
        {
            options.Verify = Passing;
            options.Capture = capture => capture.ResolveFinalization = _ => null;
        });

        var refused = Assert.Throws<InvalidOperationException>(() => AgentOver(services));
        Assert.Contains("Verify", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_the_container_ends_the_capture_of_agents_built_from_it()
    {
        var host = new Host();
        var services = host.Build(_ => { });
        var agent = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions()).AsBuilder().UseAgentExperience(services).Build();

        await services.DisposeAsync();
        var response = await agent.RunAsync("Reconcile the ledger");

        Assert.Equal("Hello, world", response.Text);
        var failure = Assert.Single(host.Failures);
        Assert.Contains("disposed", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_options_clock_drives_capture_and_evidence_and_is_the_containers()
    {
        var host = new Host();
        var at = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new FrozenTimeProvider(at);
        await using var services = host.Build(options =>
        {
            options.TimeProvider = clock;
            options.Verify = Passing;
        });
        var (agent, _) = AgentOver(services);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("Reconcile the ledger", session);

        Assert.Same(clock, services.GetRequiredService<TimeProvider>());
        Assert.Equal(at, RunOf(services, session).StartedAt);
        Assert.Single(host.Verifications);
        Assert.Equal(at, CapturedEvidence(host).CapturedAt);
        Assert.Equal(FinalizationOutcome.Validated, Assert.Single(host.Finalized).Outcome);
    }

    [Fact]
    public void A_clock_other_than_the_registered_one_is_refused()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => collection.AddAgentExperience(options =>
        {
            options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller);
            options.TimeProvider = new FrozenTimeProvider(DateTimeOffset.UnixEpoch);
        }));
    }

    [Fact]
    public void Storage_is_chosen_once()
    {
        var twice = new ServiceCollection().AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller));
        twice.UseInMemoryStorageForDevelopment();
        Assert.Contains("already chosen", Assert.Throws<InvalidOperationException>(() => twice.UseInMemoryStorageForDevelopment()).Message, StringComparison.Ordinal);

        // A record store already registered stands in for UsePostgres, which this project does not reference.
        var afterAnother = new ServiceCollection();
        afterAnother.AddSingleton<IExperienceRecordStore>(new Storage.InMemory.InMemoryExperienceRecordStore());
        var builder = afterAnother.AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller));
        Assert.Throws<InvalidOperationException>(() => builder.UseInMemoryStorageForDevelopment());
    }

    [Fact]
    public async Task Explicit_capture_options_changed_after_the_agent_is_built_change_nothing()
    {
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(AgentExperienceDefaults.Sanitization), AgentExperienceDefaults.CaptureLimits);
        var described = new List<string>();
        var options = new ExperienceCaptureOptions
        {
            ResolveRun = _ =>
            {
                described.Add("built with");
                return new ExperienceRunDescriptor("triage", Caller.Scope);
            },
        };
        var agent = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions()).AsBuilder().UseExperienceCapture(capture, options).Build();

        options.ResolveRun = _ =>
        {
            described.Add("changed later");
            return new ExperienceRunDescriptor("triage", Caller.Scope);
        };
        await agent.RunAsync("Reconcile the ledger");

        Assert.Equal(["built with"], described);
    }

    private static async ValueTask<ExperienceVerification?> Passing(ExperienceVerificationContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return new ExperienceVerification(
            RequiredChecks: [new RequiredCheck("tests-pass", "TestResult")],
            Evidence: [context.CreateEvidence("tests-pass", "TestResult", CheckResult.Pass, producer: "ci", Revision)],
            ArtifactRevision: Revision);
    }

    private const string EchoTool = "echo";

    private static readonly AIFunction Echo = AIFunctionFactory.Create((string note, string apiKey) => $"echo:{note}", EchoTool);

    private static Evidence CapturedEvidence(Host host) =>
        host.Verifications[0].CreateEvidence("tests-pass", "TestResult", CheckResult.Pass, "ci", Revision);

    private static void ContinueFromSession(ExperienceCaptureOptions capture)
    {
        capture.ShouldCompleteRun = _ => false;
        var describe = capture.ResolveRun;
        capture.ResolveRun = context => describe(context) with
        {
            ContinuesRunId = context.Session is { } session
                && session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out var runId)
                    ? Guid.Parse(runId!)
                    : null,
        };
    }

    private static ExperienceRun RunOf(IServiceProvider services, AgentSession session)
    {
        var runId = Guid.Parse(session.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey)!);
        Assert.True(services.GetRequiredService<IExperienceCaptureService>().TryGetRun(runId, out var run));
        return run;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var waited = 0; !condition() && waited < 10_000; waited += 20)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private static async Task<(AIAgent Agent, AgentSession Session)> ToolAgentOver(IServiceProvider services)
    {
        var model = new ScriptedChatClient
        {
            Calls = [new ScriptedCall(EchoTool, _ => new Dictionary<string, object?> { ["note"] = "the note", ["apiKey"] = "sk-not-a-real-key" })],
        };
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Tools = [Echo] },
                AIContextProviders = [services.GetAgentExperienceContextProvider()],
            })
            .AsBuilder()
            .UseAgentExperience(services)
            .Build();
        return (agent, await agent.CreateSessionAsync());
    }

    private static bool IsHistoricalReference(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true;

    private static (AIAgent Agent, RecordingChatClient Model) AgentOver(IServiceProvider services)
    {
        var model = new RecordingChatClient();
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
            .AsBuilder()
            .UseAgentExperience(services)
            .Build();
        return (agent, model);
    }

    /// <summary>The host side: its identity, and everything the library reported back to it.</summary>
    private sealed class Host
    {
        private int _identityCalls;

        public ExperienceIdentity? Identity { get; set; } = Caller;

        public Func<CancellationToken, Task<ExperienceIdentity?>>? IdentityOverride { get; init; }

        public bool ThrowFromIdentity { get; init; }

        public int IdentityCalls => Volatile.Read(ref _identityCalls);

        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public List<ExperienceVerificationContext> Verifications { get; } = [];

        public ServiceProvider Build(Action<AgentExperienceOptions> configure)
        {
            var collection = new ServiceCollection();
            collection
                .AddAgentExperience(options =>
                {
                    options.ResolveIdentity = (_, cancellationToken) =>
                    {
                        Interlocked.Increment(ref _identityCalls);
                        return ThrowFromIdentity
                            ? throw new InvalidOperationException("the identity provider is down")
                            : IdentityOverride is { } identityOverride
                                ? new ValueTask<ExperienceIdentity?>(identityOverride(cancellationToken))
                                : ValueTask.FromResult(Identity);
                    };
                    configure(options);
                    options.Injection = Chain(options.Injection, injection => injection.OnContextInjected = Injections.Add);
                    options.Capture = Chain(options.Capture, capture =>
                    {
                        capture.OnCaptureFailure = Failures.Add;
                        capture.OnRunFinalized = Finalized.Add;
                    });
                    if (options.Verify is { } verify)
                    {
                        options.Verify = (context, cancellationToken) =>
                        {
                            Verifications.Add(context);
                            return verify(context, cancellationToken);
                        };
                    }
                })
                .UseInMemoryStorageForDevelopment();
            return collection.BuildServiceProvider();
        }

        private static Action<T> Chain<T>(Action<T>? first, Action<T> then) => value =>
        {
            first?.Invoke(value);
            then(value);
        };
    }
}
