using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Recall at the moment of failure, through the one-call setup over a real <see cref="ChatClientAgent"/> and a scripted
/// model: the first run tries two fixes that fail and a third that works, and is stored as a verified lesson; a second
/// run on unrelated words (so no block is injected before it starts) fails the same way, is shown one hint naming what
/// worked, and calls it next. The hint travels in the instructions of the model's next call, never in a tool result.
/// </summary>
public sealed partial class RecallAtFailureTests
{
    private const string FirstTask = "Repair the failing alpha build quickly";

    private const string SecondTask = "Payments pipeline broke overnight";

    private const string Tool = "run_tests";

    private const string WorkingFix = "pin-version";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_failure_a_stored_lesson_resolved_gets_one_hint_naming_what_worked_and_the_model_calls_it_next(bool classified, bool streaming)
    {
        var host = new Host { Classified = classified };
        await using var services = host.Build(options => options.Recall = new ExperienceRecallOptions());
        var agent = host.Agent(services);

        await Run(agent, FirstTask, streaming);
        var learned = Assert.Single(host.Finalized).ExperienceId!.Value;
        Assert.Equal(["retry", "clear-cache", WorkingFix], host.Desk.Tried);

        // Run 1 had nothing to recall: its first failure found no match, its second (the same signature) was not looked up.
        Assert.Equal([ExperienceRecallOutcome.NoMatch, ExperienceRecallOutcome.Bounded], host.Recalls.Select(r => r.Outcome));
        Assert.All(host.Model.Calls, call => Assert.DoesNotContain(HistoricalReferenceWriter.RecallBegin, call.Instructions ?? string.Empty, StringComparison.Ordinal));

        host.Reset();
        await Run(agent, SecondTask, streaming);

        // No block before the run: the words differ. The failure is what found the lesson.
        Assert.NotEqual(InjectionOutcome.Injected, host.Injections[^1].Outcome);
        var recall = Assert.Single(host.Recalls);
        Assert.Equal(ExperienceRecallOutcome.Hinted, recall.Outcome);
        Assert.Equal([learned], recall.ExperienceIds);
        Assert.Equal(ExperienceSignatureMatch.Exact, recall.Match);
        Assert.Equal(Tool, recall.Signature!.ToolName);
        Assert.True(recall.HintBytes is > 0 and <= ExperienceRecallOptions.DefaultMaxHintBytes);

        // One failure, then the fix the hint named.
        Assert.Equal(["retry", WorkingFix], host.Desk.Tried);

        // The first call had no hint; the call after the failure had exactly one, in its instructions, naming the fix.
        Assert.Equal(3, host.Model.Calls.Count);
        Assert.DoesNotContain(HistoricalReferenceWriter.RecallBegin, host.Model.Calls[0].Instructions ?? string.Empty, StringComparison.Ordinal);
        var instructions = host.Model.Calls[1].Instructions!;
        Assert.Single(Regex.Matches(instructions, Regex.Escape(HistoricalReferenceWriter.RecallBegin)));
        Assert.StartsWith(Host.Instructions, instructions, StringComparison.Ordinal);
        var hint = instructions[instructions.IndexOf(HistoricalReferenceWriter.RecallBegin, StringComparison.Ordinal)..];
        Assert.Equal(recall.HintBytes, System.Text.Encoding.UTF8.GetByteCount(hint));
        var marker = classified ? "[failed: exit 2]" : "[failed: InvalidOperationException]";
        Assert.Contains($"An earlier verified run hit this same failure ({Tool} {marker}) and then:", hint, StringComparison.Ordinal);
        Assert.Contains($"{Tool}(fix=\"{WorkingFix}\") [returned]", hint, StringComparison.Ordinal);
        Assert.Contains("Confidence: ", hint, StringComparison.Ordinal);
        Assert.EndsWith(HistoricalReferenceWriter.BlockEnd + "\n", hint, StringComparison.Ordinal);

        // Never in a message: no tool result and no other message carries it.
        Assert.All(host.Model.Calls, call => Assert.DoesNotContain(call.Messages, message => message.Text.Contains(HistoricalReferenceWriter.RecallBegin, StringComparison.Ordinal)));
        Assert.All(host.Model.Calls.SelectMany(call => call.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            result => Assert.DoesNotContain(HistoricalReferenceWriter.RecallBegin, result.Result?.ToString() ?? string.Empty, StringComparison.Ordinal));

        Assert.Empty(host.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task What_capture_stores_and_what_the_model_is_handed_back_are_the_same_with_recall_on_and_off(bool classified)
    {
        async Task<(string Stored, string Seen)> ScenarioAsync(bool recall)
        {
            var host = new Host { Classified = classified, FollowHints = false };
            await using var services = host.Build(options => options.Recall = recall ? new ExperienceRecallOptions() : null);
            var agent = host.Agent(services);
            await agent.RunAsync(FirstTask);
            host.Reset();
            await agent.RunAsync(SecondTask);

            // The model here ignores hints, so both runs make the same calls whatever recall does.
            Assert.Equal(recall ? [ExperienceRecallOutcome.Hinted, ExperienceRecallOutcome.Bounded] : [], host.Recalls.Select(r => r.Outcome));
            var finalized = host.Finalized[^1];
            var identity = host.Verifications[^1].Identity;
            var stored = await services.GetRequiredService<IExperienceRecordStore>()
                .GetAsync(identity.Authorization, identity.Scope, finalized.ExperienceId!.Value, CancellationToken.None);
            var calls = stored.Record!.Attempts.SelectMany(attempt => attempt.ToolCalls)
                .Select(call => new { call.ToolName, call.Arguments, call.Result, call.Error });
            var seen = host.Model.Calls.SelectMany(call => call.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString());
            return (JsonSerializer.Serialize(calls), JsonSerializer.Serialize(seen));
        }

        var off = await ScenarioAsync(recall: false);
        var on = await ScenarioAsync(recall: true);

        Assert.Equal(off.Stored, on.Stored);
        Assert.Equal(off.Seen, on.Seen);
        Assert.Contains(classified ? "exit 2" : "System.InvalidOperationException", on.Stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_held_out_run_gets_no_hint_and_its_would_be_hinted_record_is_recorded_as_its_assignment()
    {
        var host = new Host();
        await using var services = host.Build(options =>
        {
            options.Recall = new ExperienceRecallOptions();
            options.Injection = injection => injection.Holdout = new ExperienceHoldoutOptions { Rate = 1 };
        });
        var agent = host.Agent(services);

        await agent.RunAsync(FirstTask);
        var learned = host.Finalized[^1].ExperienceId!.Value;
        host.Reset();
        await agent.RunAsync(SecondTask);

        // The model is never hinted, so it fails twice; the second failure is the same signature, not looked up again.
        Assert.Equal([ExperienceRecallOutcome.Withheld, ExperienceRecallOutcome.Bounded], host.Recalls.Select(r => r.Outcome));
        var recall = host.Recalls[0];
        Assert.Empty(recall.ExperienceIds);
        Assert.Equal([learned], recall.WithheldExperienceIds);
        Assert.All(host.Model.Calls, call => Assert.DoesNotContain(HistoricalReferenceWriter.RecallBegin, call.Instructions ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(["retry", "clear-cache", WorkingFix], host.Desk.Tried);

        await services.GetRequiredService<ExperienceContextProvider>().InjectionRecordsSettledAsync();
        var identity = host.Verifications[^1].Identity;
        var rows = await services.GetRequiredService<IExperienceRecordStore>()
            .QueryHoldoutOutcomesAsync(identity.Authorization, new HoldoutOutcomeQuery(identity.Scope), CancellationToken.None);
        var row = Assert.Single(rows.Rows);
        Assert.Equal(HoldoutArm.Holdout, row.Arm);
        Assert.Equal(learned, row.ExperienceId);
        Assert.Equal(host.Verifications[^1].Run.RunId, row.RunId);
    }

    [Fact]
    public async Task A_treatment_run_records_the_hinted_record_as_its_assignment_and_exposure()
    {
        var host = new Host();
        await using var services = host.Build(options =>
        {
            options.Recall = new ExperienceRecallOptions();
            options.Injection = injection => injection.Holdout = new ExperienceHoldoutOptions { Rate = double.Epsilon };
        });
        var agent = host.Agent(services);

        await agent.RunAsync(FirstTask);
        var learned = host.Finalized[^1].ExperienceId!.Value;
        host.Reset();
        await agent.RunAsync(SecondTask);

        Assert.Equal(ExperienceRecallOutcome.Hinted, Assert.Single(host.Recalls).Outcome);
        await services.GetRequiredService<ExperienceContextProvider>().InjectionRecordsSettledAsync();
        var identity = host.Verifications[^1].Identity;
        var rows = await services.GetRequiredService<IExperienceRecordStore>()
            .QueryHoldoutOutcomesAsync(identity.Authorization, new HoldoutOutcomeQuery(identity.Scope), CancellationToken.None);
        var row = Assert.Single(rows.Rows);
        Assert.Equal(HoldoutArm.Treatment, row.Arm);
        Assert.Equal(learned, row.ExperienceId);

        // The hinted record is the run's exposure, exactly as a block's would be.
        Assert.Contains(host.Verifications[^1].Run.Provenance.ExposedTo, exposure => exposure.ExperienceId == learned);
    }

    [Fact]
    public async Task A_lookup_that_does_not_answer_within_its_bound_gives_no_hint_and_the_run_goes_on()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var hanging = new HangingSignatureSource();
        var host = new Host();
        await using var services = host.Build(options =>
        {
            options.TimeProvider = clock;
            options.Recall = new ExperienceRecallOptions { SignatureSource = hanging };
        });
        var agent = host.Agent(services);

        var run = agent.RunAsync(FirstTask);
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await clock.AdvanceUntilAsync(run, TimeSpan.FromMilliseconds(100));
        await run;

        var recall = host.Recalls[0];
        Assert.Equal(ExperienceRecallOutcome.TimedOut, recall.Outcome);
        Assert.Empty(recall.ExperienceIds);
        Assert.NotNull(recall.Failure);
        Assert.Equal(["retry", "clear-cache", WorkingFix], host.Desk.Tried);
        Assert.All(host.Model.Calls, call => Assert.DoesNotContain(HistoricalReferenceWriter.RecallBegin, call.Instructions ?? string.Empty, StringComparison.Ordinal));
        Assert.Single(host.Finalized);
    }

    [Fact]
    public async Task A_run_is_hinted_at_most_MaxHintsPerRun_times_and_each_signature_is_looked_up_once()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Recall = new ExperienceRecallOptions { MaxHintsPerRun = 1 });
        var agent = host.Agent(services);

        await agent.RunAsync(FirstTask);

        // Two failures of the same signature: one lookup, then Bounded.
        Assert.Equal([ExperienceRecallOutcome.NoMatch, ExperienceRecallOutcome.Bounded], host.Recalls.Select(r => r.Outcome));
    }

    [Fact]
    public async Task Without_a_signature_source_recall_is_reported_once_and_does_nothing()
    {
        var host = new Host { WithoutSignatureSource = true };
        await using var services = host.Build(options => options.Recall = new ExperienceRecallOptions());
        var agent = host.Agent(services);

        await agent.RunAsync(FirstTask);
        host.Reset();
        await agent.RunAsync(SecondTask);

        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Recall, failure.Stage);
        Assert.Empty(host.Recalls);
        Assert.Equal(["retry", "clear-cache", WorkingFix], host.Desk.Tried);
    }

    [Fact]
    public async Task With_recall_off_nothing_is_looked_up()
    {
        var host = new Host();
        var source = new HangingSignatureSource();
        await using var services = host.Build(options => options.Injection = injection => injection.Recall = null);
        var agent = host.Agent(services);

        await agent.RunAsync(FirstTask);
        host.Reset();
        await agent.RunAsync(SecondTask);

        Assert.Empty(host.Recalls);
        Assert.False(source.Entered.Task.IsCompleted);
        Assert.Empty(host.Failures);
        Assert.Null(services.GetRequiredService<ExperienceContextProvider>().RecallOptions);
    }

    [Fact]
    public async Task A_session_is_charged_for_a_hint_and_a_record_its_block_gave_is_not_hinted_again()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Recall = new ExperienceRecallOptions());
        var agent = host.Agent(services);
        await agent.RunAsync(FirstTask);
        var learned = host.Finalized[^1].ExperienceId!.Value;

        // A session whose first turn is given the lesson in its block (the same words), and then fails the same way.
        host.Reset();
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(FirstTask, session);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[^1].Outcome);
        Assert.Equal([ExperienceRecallOutcome.NoMatch, ExperienceRecallOutcome.Bounded], host.Recalls.Select(r => r.Outcome));
        var recall = host.Recalls[0];
        Assert.Equal(InjectionOmissionReason.AlreadyDelivered, Assert.Single(recall.Omitted, o => o.ExperienceId == learned).Reason);

        // A second session that gets no block is hinted, and charged for it.
        host.Reset();
        var other = await agent.CreateSessionAsync();
        await agent.RunAsync(SecondTask, other);
        Assert.Equal(ExperienceRecallOutcome.Hinted, Assert.Single(host.Recalls).Outcome);
        Assert.True(InjectionSessionState.TryLoad(other.StateBag, ExperienceContextProvider.SessionStateKey, out var state));
        Assert.Equal(1, state.RecordsUsed);
        Assert.Equal(host.Recalls[0].HintBytes, state.BytesUsed);
        // Whichever lesson ranked first (the first session's run was stored too), charged as delivered but unsure: the hint
        // is not in the session's history, so a later turn may be given it again.
        var delivered = Assert.Single(state.Delivered);
        Assert.Equal(Assert.Single(host.Recalls[0].ExperienceIds), delivered.ExperienceId);
        Assert.False(delivered.Confirmed);
    }

    private static async Task Run(AIAgent agent, string task, bool streaming)
    {
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync(task))
            {
            }
        }
        else
        {
            await agent.RunAsync(task);
        }
    }

    /// <summary>The tool's world: which fix works, every fix tried this run, and whether the build is green.</summary>
    internal sealed class Desk
    {
        public List<string> Tried { get; } = [];

        public bool Green => Tried.Contains(WorkingFix);

        public bool Classified { get; init; }

        public AIFunction Tool => AIFunctionFactory.Create(
            (string fix) =>
            {
                Tried.Add(fix);
                if (fix == WorkingFix)
                {
                    return "exit=0 all tests pass";
                }

                return Classified ? "exit=2 tests failed" : throw new InvalidOperationException("exit=2 tests failed in /tmp/build-" + Tried.Count);
            },
            RecallAtFailureTests.Tool);
    }

    /// <summary>
    /// A scripted model. It tries <c>retry</c>, <c>clear-cache</c> and <c>pin-version</c> in that order, one call per turn,
    /// until one exits 0, except that, when <see cref="FollowHints"/> and its instructions carry a recall hint naming a fix
    /// that returned, it tries that one next. It records what it was handed.
    /// </summary>
    internal sealed partial class Model : IChatClient
    {
        private static readonly string[] Order = ["retry", "clear-cache", WorkingFix];

        private int _sequence;

        public List<(List<ChatMessage> Messages, string? Instructions)> Calls { get; } = [];

        public bool FollowHints { get; init; } = true;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (Calls)
            {
                Calls.Add((list, options?.Instructions));
            }

            var results = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Any(result => result.Result?.ToString()?.StartsWith("exit=0", StringComparison.Ordinal) == true))
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Fixed.")));
            }

            var tried = list.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
                .Select(call => call.Arguments?.TryGetValue("fix", out var value) == true ? value?.ToString() : null)
                .ToHashSet(StringComparer.Ordinal);
            var hinted = FollowHints && options?.Instructions is { } instructions && HintedFix().Match(instructions) is { Success: true } match
                ? match.Groups[1].Value
                : null;
            var next = hinted is not null && !tried.Contains(hinted) ? hinted : Order.FirstOrDefault(fix => !tried.Contains(fix));
            if (next is null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Gave up.")));
            }

            var id = "call-" + Interlocked.Increment(ref _sequence);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, Tool, new Dictionary<string, object?> { ["fix"] = next })])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        [GeneratedRegex("^  - worked: .*?fix=\"([^\"]+)\"\\) \\[returned\\]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
        private static partial Regex HintedFix();
    }

    /// <summary>A signature source that says when it is asked and never answers until its token is cancelled.</summary>
    private sealed class HangingSignatureSource : IExperienceSignatureSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExperienceSignatureSearchResult> FindBySignatureAsync(AuthorizationContext authorization, Scope scope, ExperienceSignatureQuery query, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>The system clock, with timers that never fire, so no bound races a loaded machine.</summary>
    private sealed class NeverFiringTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal sealed class Host
    {
        public const string Instructions = "You fix failing tests with run_tests, one fix per call.";

        public bool Classified { get; init; }

        public bool FollowHints { get; init; } = true;

        public bool WithoutSignatureSource { get; init; }

        public Desk Desk { get; private set; } = new();

        public Model Model { get; private set; } = new();

        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public List<ExperienceRecallResult> Recalls { get; } = [];

        public List<ExperienceVerificationContext> Verifications { get; } = [];

        private AIFunction? _tool;

        /// <summary>A new run's desk and model call log; the stored lessons, the agent and the reports so far stay.</summary>
        public void Reset()
        {
            Desk.Tried.Clear();
            lock (Model.Calls)
            {
                Model.Calls.Clear();
            }

            Recalls.Clear();
        }

        public AIAgent Agent(IServiceProvider services)
        {
            Desk = new Desk { Classified = Classified };
            Model = new Model { FollowHints = FollowHints };
            _tool = Desk.Tool;
            return new ChatClientAgent(Model, new ChatClientAgentOptions
            {
                Name = "fixer",
                ChatOptions = new ChatOptions { Instructions = Instructions, Tools = [_tool] },
            }).AsBuilder().UseAgentExperience(services).Build();
        }

        public ServiceProvider Build(Action<AgentExperienceOptions>? configure = null)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment(Environments.Development));
            collection
                .AddAgentExperience(options =>
                {
                    options.UseDevelopmentIdentity();
                    options.TimeProvider = new NeverFiringTimers();
                    options.Sanitization = AgentExperienceDefaults.SanitizationAllowing("fix");
                    options.Verify = (context, cancellationToken) =>
                    {
                        Verifications.Add(context);
                        var result = Desk.Green ? CheckResult.Pass : CheckResult.Fail;
                        return ValueTask.FromResult<ExperienceVerification?>(new(
                            RequiredChecks: [new RequiredCheck("tests", "ToolExitCode")],
                            Evidence: [context.CreateEvidence("tests", "ToolExitCode", result, producer: "ci", "recall-tests")],
                            ArtifactRevision: "recall-tests"));
                    };
                    configure?.Invoke(options);
                    var injection = options.Injection;
                    options.Injection = settings =>
                    {
                        settings.ApproachArguments[Tool] = ["fix"];
                        injection?.Invoke(settings);
                        settings.OnContextInjected = Injections.Add;
                        if (settings.Recall is { } recall)
                        {
                            settings.Recall = recall with { OnRecall = result => { lock (Recalls) { Recalls.Add(result); } } };
                        }
                    };
                    options.Capture = capture =>
                    {
                        capture.OnCaptureFailure = Failures.Add;
                        capture.OnRunFinalized = Finalized.Add;
                        if (Classified)
                        {
                            capture.ClassifyToolResult = context =>
                                context.Result?.ToString() is { } text && text.StartsWith("exit=", StringComparison.Ordinal) && !text.StartsWith("exit=0", StringComparison.Ordinal)
                                    ? "exit " + text["exit=".Length..text.IndexOf(' ', StringComparison.Ordinal)]
                                    : null;
                        }
                    };
                })
                .UseInMemoryStorageForDevelopment();
            if (WithoutSignatureSource)
            {
                collection.RemoveAll<IExperienceSignatureSource>();
            }

            return collection.BuildServiceProvider();
        }
    }
}
