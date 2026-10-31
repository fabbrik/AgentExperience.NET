using System.Runtime.CompilerServices;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Recall at the moment of failure at the provider level, over the fake world (record store and candidate source) and a
/// signature source each test scripts: the block's rules (grants, capability gate, model-authored policy, measured
/// benefit), coarse matching, unresolved records, a failing source, bounds, the session budget in both holdout arms,
/// parallel failures, a service that keeps the conversation, missing chat options and the holdout.
/// </summary>
public sealed class RecallProviderTests
{
    private const string Thrown = "System.InvalidOperationException";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");

    private static readonly Scope Lender = new("tenant-1", "app-1", "project-1", "team-lender");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], DateTimeOffset.UnixEpoch);

    private static ExperienceRecord Lesson(int n, string tool = "run_tests", Scope? scope = null, ReflectionAuthorship? authorship = null)
    {
        var record = InjectionRecords.Record(InjectionRecords.Id(n), scope ?? TestScope, attempts:
        [
            new Attempt(Guid.NewGuid(), 1, InjectionRecords.Now, TimeSpan.FromSeconds(1),
            [
                new ToolCallRecord(Guid.NewGuid(), 0, tool, new Dictionary<string, object?>(), InjectionRecords.Now, TimeSpan.Zero, null, Thrown),
                new ToolCallRecord(Guid.NewGuid(), 1, "pin_tool", new Dictionary<string, object?>(), InjectionRecords.Now, TimeSpan.Zero, "ok", null),
            ], "ok", null),
        ]);
        return authorship is { } author ? record with { Reflection = record.Reflection! with { Authorship = author } } : record;
    }

    [Theory]
    [InlineData(ExperienceGrantDisclosure.LessonOnly, false)]
    [InlineData(ExperienceGrantDisclosure.LessonAndApproach, true)]
    public async Task A_borrowed_record_gives_a_hint_only_when_its_grant_shows_the_approach(ExperienceGrantDisclosure level, bool hinted)
    {
        var h = new Harness();
        var borrowed = Lesson(1, scope: Lender);
        h.World.Store(borrowed);
        h.World.Grant(borrowed.ExperienceId, TestScope, level);
        h.Source.Add(borrowed, sharedBy: level);

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        if (hinted)
        {
            Assert.True(recall.Outcome == ExperienceRecallOutcome.Hinted, string.Join("; ", recall.Omitted.Select(o => o.Reason + ": " + o.Detail)) + recall.Failure?.Reason);
            Assert.Contains("Shared: ", h.Hint, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
            var omitted = Assert.Single(recall.Omitted);
            Assert.Equal(ExperienceContextProvider.RecallApproachWithheldDetail, omitted.Detail);
            Assert.Null(h.Hint);
        }
    }

    [Fact]
    public async Task The_capability_gate_keeps_out_a_record_whose_working_calls_the_agent_lacks()
    {
        var h = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = new HashSet<string>(["run_tests", "deploy"], StringComparer.Ordinal) } };
        h.Publish(Lesson(1));

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
        Assert.Equal(InjectionOmissionReason.ToolUnavailable, Assert.Single(recall.Omitted).Reason);
    }

    [Theory]
    [InlineData(ModelAuthoredLessonPolicy.Exclude)]
    [InlineData(ModelAuthoredLessonPolicy.Include)]
    public async Task A_model_authored_lesson_follows_the_policy_and_its_hint_carries_no_model_text(ModelAuthoredLessonPolicy policy)
    {
        var h = new Harness { ModelAuthoredLessons = policy };
        h.Publish(Lesson(1, authorship: ReflectionAuthorship.Model));

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        if (policy == ModelAuthoredLessonPolicy.Exclude)
        {
            Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
            Assert.True(h.Source.Queries.All(query => query.ExcludeModelAuthored));
            Assert.Equal(InjectionOmissionReason.ModelAuthored, Assert.Single(recall.Omitted).Reason);
        }
        else
        {
            // Only the worked calls, which no model wrote: nothing to fence.
            Assert.Equal(ExperienceRecallOutcome.Hinted, recall.Outcome);
            Assert.DoesNotContain(HistoricalReferenceWriter.ModelAuthoredLine, h.Hint, StringComparison.Ordinal);
            Assert.DoesNotContain("Check the lock table", h.Hint, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_coarse_match_is_asked_only_when_allowed_and_nothing_exact_resolves(bool allowCoarse)
    {
        var h = new Harness { Recall = new ExperienceRecallOptions { AllowCoarseMatch = allowCoarse } };
        var lesson = Lesson(1);
        h.World.Store(lesson);
        h.Source.Add(lesson, match: ExperienceSignatureMatch.Coarse);

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(allowCoarse ? [ExperienceSignatureMatch.Exact, ExperienceSignatureMatch.Coarse] : [ExperienceSignatureMatch.Exact], h.Source.Queries.Select(q => q.Match));
        if (allowCoarse)
        {
            Assert.Equal(ExperienceRecallOutcome.Hinted, recall.Outcome);
            Assert.Equal(ExperienceSignatureMatch.Coarse, recall.Match);
            Assert.Contains("hit a failure like this one", h.Hint, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
        }
    }

    [Fact]
    public async Task A_record_whose_signature_is_unresolved_gives_no_hint()
    {
        var h = new Harness();
        var lesson = Lesson(1);
        h.World.Store(lesson);
        h.Source.Add(lesson, resolution: ExperienceSignatureResolution.Unresolved);

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
        Assert.Equal(ExperienceContextProvider.UnresolvedDetail, Assert.Single(recall.Omitted).Detail);
    }

    [Fact]
    public async Task A_lesson_measured_as_hurting_gives_no_hint_when_acting_on_the_estimate()
    {
        var h = new Harness { Benefit = new ExperienceBenefitPolicy { Enabled = true } };
        var hurts = InjectionRecords.Id(1);
        h.Publish(Lesson(1));
        h.World.TracksHoldout = true;
        h.World.OnQueryHoldout = (_, _, _) => Task.FromResult(new HoldoutOutcomeQueryResult(
            ExperienceStoreOutcome.Found,
            [.. Rows(hurts, TaskVerificationStatus.Failed, TaskVerificationStatus.Verified, 0), .. Rows(InjectionRecords.Id(9), TaskVerificationStatus.Verified, TaskVerificationStatus.Failed, 1_000)],
            []));

        await h.RunAsync("run_tests");
        await h.Provider.BenefitRefreshesSettledAsync();
        h.Recalls.Clear();
        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
        Assert.Equal((hurts, InjectionOmissionReason.MeasuredHarmful), (recall.Omitted[0].ExperienceId, recall.Omitted[0].Reason));

        static IEnumerable<HoldoutOutcomeRow> Rows(Guid lesson, TaskVerificationStatus given, TaskVerificationStatus withheld, int offset) =>
            Enumerable.Range(0, 50).Select(i => new HoldoutOutcomeRow(
                new Guid($"{offset + i:D8}-0000-0000-0000-000000000000"),
                i < 25 ? HoldoutArm.Treatment : HoldoutArm.Holdout,
                lesson,
                InjectionRecords.Now,
                i < 25 ? given : withheld));
    }

    [Fact]
    public async Task A_failing_signature_source_gives_no_hint_and_frees_the_signature_for_a_later_failure()
    {
        var h = new Harness();
        h.Source.Throws = new InvalidOperationException("source down");

        await h.RunAsync("run_tests", "run_tests");

        Assert.Equal([ExperienceRecallOutcome.Failed, ExperienceRecallOutcome.Failed], h.Recalls.Select(r => r.Outcome));
        Assert.All(h.Recalls, recall => Assert.NotNull(recall.Failure));
        Assert.Equal(2, h.Source.Queries.Count);
        Assert.Null(h.Hint);
    }

    [Fact]
    public async Task MaxHintsPerRun_counts_answers_over_distinct_signatures()
    {
        var h = new Harness { Recall = new ExperienceRecallOptions { MaxHintsPerRun = 1 } };
        h.Publish(Lesson(1, tool: "run_tests"));
        h.Publish(Lesson(2, tool: "deploy"));

        await h.RunAsync("run_tests", "deploy");

        Assert.Equal([ExperienceRecallOutcome.Hinted, ExperienceRecallOutcome.Bounded], h.Recalls.Select(r => r.Outcome));
        Assert.Single(h.Source.Queries);
    }

    [Fact]
    public async Task A_lookup_with_no_answer_does_not_count_against_MaxHintsPerRun()
    {
        var h = new Harness { Recall = new ExperienceRecallOptions { MaxHintsPerRun = 1 } };
        h.Publish(Lesson(2, tool: "deploy"));

        await h.RunAsync("run_tests", "deploy");

        Assert.Equal([ExperienceRecallOutcome.NoMatch, ExperienceRecallOutcome.Hinted], h.Recalls.Select(r => r.Outcome));
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(0d)]
    public async Task A_hint_the_session_budget_cannot_take_is_Bounded_in_either_arm_and_assigns_nothing(double rate)
    {
        var assignments = new List<HoldoutAssignment>();
        var h = new Harness { SessionLimits = new ExperienceInjectionSessionLimits(32, 200), Holdout = new ExperienceHoldoutOptions { Rate = rate } };
        h.Publish(Lesson(1), index: false);
        h.World.TracksHoldout = true;
        h.World.OnRecordHoldout = (_, _, assignment, _) =>
        {
            lock (assignments)
            {
                assignments.Add(assignment);
            }

            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, []));
        };

        var session = await h.Agent.CreateSessionAsync();
        await h.RunAsync(session, "run_tests");
        await h.Provider.InjectionRecordsSettledAsync();

        Assert.Equal(ExperienceRecallOutcome.Bounded, Assert.Single(h.Recalls).Outcome);
        Assert.Empty(assignments);
        Assert.Null(h.Hint);
        Assert.True(InjectionSessionState.TryLoad(session.StateBag, ExperienceContextProvider.SessionStateKey, out var state));
        Assert.Equal(0, state.RecordsUsed);
    }

    [Fact]
    public async Task Parallel_failures_never_hint_or_charge_one_record_twice()
    {
        // One lesson holding both failures, resolved; both lookups are held until both have started.
        var h = new Harness { Concurrent = true };
        var lesson = Lesson(1);
        h.World.Store(lesson);
        h.Source.Add(lesson, tool: "run_tests");
        h.Source.Add(lesson, tool: "deploy");
        h.Source.Barrier = 2;

        var session = await h.Agent.CreateSessionAsync();
        await h.RunAsync(session, "run_tests", "deploy");

        Assert.Equal(2, h.Recalls.Count);
        Assert.Single(h.Recalls, r => r.Outcome == ExperienceRecallOutcome.Hinted);
        Assert.Single(h.Recalls, r => r.Outcome == ExperienceRecallOutcome.NoMatch && r.Omitted.Any(o => o.Reason == InjectionOmissionReason.AlreadyDelivered));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h.Hint!, System.Text.RegularExpressions.Regex.Escape(HistoricalReferenceWriter.RecallBegin)));
        Assert.True(InjectionSessionState.TryLoad(session.StateBag, ExperienceContextProvider.SessionStateKey, out var state));
        Assert.Equal(1, state.RecordsUsed);
    }

    [Fact]
    public async Task With_no_host_instructions_the_hint_is_the_instructions()
    {
        var h = new Harness { Instructions = null };
        h.Publish(Lesson(1));

        await h.RunAsync("run_tests");

        Assert.Null(h.Model.Instructions[0]);
        Assert.StartsWith(HistoricalReferenceWriter.RecallBegin, h.Model.Instructions[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_that_keeps_the_conversation_still_gets_the_hint_on_its_next_call()
    {
        var h = new Harness();
        h.Model.ConversationId = "conversation-1";
        h.Publish(Lesson(1));

        await h.RunAsync("run_tests");

        Assert.Equal(ExperienceRecallOutcome.Hinted, Assert.Single(h.Recalls).Outcome);
        Assert.DoesNotContain(HistoricalReferenceWriter.RecallBegin, h.Model.Instructions[0] ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(HistoricalReferenceWriter.RecallBegin, h.Model.Instructions[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_call_with_no_chat_options_gets_no_hint()
    {
        var h = new Harness();
        h.Publish(Lesson(1));
        var run = new RecallRun(h.Provider, new RetrieveExperienceRequest(Authorization, TestScope, "task"), null, null, Guid.NewGuid());

        await h.Provider.RecallAsync(run, null!, options: null, "run_tests", Thrown, CancellationToken.None);

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.Failed, recall.Outcome);
        Assert.Equal(ExperienceContextProvider.NoInstructionsReason, recall.Failure!.Reason);
        Assert.Empty(h.Source.Queries);
    }

    [Fact]
    public async Task A_run_whose_block_was_held_out_is_held_out_of_the_hint_too_and_both_are_assigned()
    {
        var assignments = new List<HoldoutAssignment>();
        var h = new Harness { Holdout = new ExperienceHoldoutOptions { Rate = 1 } };
        h.Publish(Lesson(1), index: true);
        h.World.TracksHoldout = true;
        h.World.OnRecordHoldout = (_, _, assignment, _) =>
        {
            lock (assignments)
            {
                assignments.Add(assignment);
            }

            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, []));
        };

        await h.RunAsync("run_tests");
        await h.Provider.InjectionRecordsSettledAsync();

        Assert.Equal(InjectionOutcome.Withheld, Assert.Single(h.Injections).Outcome);
        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.Withheld, recall.Outcome);
        Assert.Equal([InjectionRecords.Id(1)], recall.WithheldExperienceIds);
        Assert.Null(h.Hint);
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, assignment => Assert.Equal(HoldoutArm.Holdout, assignment.Arm));
        Assert.Single(assignments.Select(a => a.RunId).Distinct());
    }

    [Fact]
    public async Task Under_the_match_floor_an_exact_signature_match_still_gives_a_hint()
    {
        var h = new Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 1d } };
        h.Publish(Lesson(1));

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.Hinted, recall.Outcome);
        Assert.Equal([InjectionRecords.Id(1)], recall.ExperienceIds);
        Assert.NotNull(h.Hint);
    }

    [Fact]
    public async Task Under_the_match_floor_recall_never_asks_for_a_coarse_match()
    {
        var h = new Harness
        {
            Recall = new ExperienceRecallOptions { AllowCoarseMatch = true },
            MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 },
        };
        var lesson = Lesson(1);
        h.World.Store(lesson);
        h.Source.Add(lesson, match: ExperienceSignatureMatch.Coarse);

        await h.RunAsync("run_tests");

        var recall = Assert.Single(h.Recalls);
        Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
        Assert.Equal([ExperienceSignatureMatch.Exact], h.Source.Queries.Select(q => q.Match));
        Assert.Null(h.Hint);
    }

    [Fact]
    public async Task The_match_floor_runs_before_the_holdout_split_so_a_held_out_block_withholds_only_what_passed()
    {
        var assignments = new List<HoldoutAssignment>();
        var h = new Harness { Holdout = new ExperienceHoldoutOptions { Rate = 1 }, MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 } };
        h.World.Publish(Lesson(1, "deploy"), relevance: 1d);
        h.World.Publish(Lesson(2, "deploy"), relevance: 0.1d);
        h.World.TracksHoldout = true;
        h.World.OnRecordHoldout = (_, _, assignment, _) =>
        {
            lock (assignments)
            {
                assignments.Add(assignment);
            }

            return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, []));
        };

        await h.RunAsync();
        await h.Provider.InjectionRecordsSettledAsync();

        var injection = Assert.Single(h.Injections);
        Assert.Equal(InjectionOutcome.Withheld, injection.Outcome);
        Assert.Equal([InjectionRecords.Id(1)], injection.WithheldExperienceIds);
        Assert.Equal(new OmittedExperience(InjectionRecords.Id(2), InjectionOmissionReason.WeakMatch, injection.Omitted[0].Detail), Assert.Single(injection.Omitted));
        var assignment = Assert.Single(assignments);
        Assert.Equal(HoldoutArm.Holdout, assignment.Arm);
        Assert.Equal([InjectionRecords.Id(1)], assignment.Records.Select(r => r.ExperienceId));
    }

    /// <summary>A scripted signature source: candidates by tool and match kind; it records every query.</summary>
    private sealed class ScriptedSignatureSource : IExperienceSignatureSource
    {
        private readonly List<(string Tool, ExperienceSignatureMatch Match, ExperienceSignatureCandidate Candidate)> _candidates = [];
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public List<ExperienceSignatureQuery> Queries { get; } = [];

        public Exception? Throws { get; set; }

        /// <summary>When set, every lookup waits until this many have started.</summary>
        public int Barrier { get; set; }

        public void Add(
            ExperienceRecord record,
            string tool = "run_tests",
            ExperienceSignatureMatch match = ExperienceSignatureMatch.Exact,
            ExperienceSignatureResolution resolution = ExperienceSignatureResolution.Resolved,
            ExperienceGrantDisclosure? sharedBy = null) =>
            _candidates.Add((tool, match, new ExperienceSignatureCandidate(record, match, resolution, sharedBy is not null, sharedBy is not null ? Guid.NewGuid() : null) { GrantDisclosure = sharedBy }));

        public async Task<ExperienceSignatureSearchResult> FindBySignatureAsync(AuthorizationContext authorization, Scope scope, ExperienceSignatureQuery query, CancellationToken cancellationToken)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            if (Barrier > 0)
            {
                if (Interlocked.Increment(ref _entered) >= Barrier)
                {
                    _released.TrySetResult();
                }

                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            if (Throws is { } failure)
            {
                throw failure;
            }

            var found = _candidates
                .Where(c => c.Tool == query.Signature.ToolName && (c.Match == ExperienceSignatureMatch.Exact || query.Match == ExperienceSignatureMatch.Coarse))
                // The exclusion is not honoured here, so the provider's own check is what is tested.
                .Select(c => c.Candidate)
                .ToList();
            return new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Found, found, []);
        }
    }

    /// <summary>A model that calls the named tools on its first turn and answers on its second; records each call's instructions.</summary>
    private sealed class Model : IChatClient
    {
        public List<string> Tools { get; set; } = [];

        public List<string?> Instructions { get; } = [];

        public string? ConversationId { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (Instructions)
            {
                Instructions.Add(options?.Instructions);
            }

            var answered = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any() || (ConversationId is not null && Instructions.Count > 1);
            var message = answered
                ? new ChatMessage(ChatRole.Assistant, "Done.")
                : new ChatMessage(ChatRole.Assistant, Tools.Select((tool, i) => (AIContent)new FunctionCallContent("call-" + i, tool, new Dictionary<string, object?>())).ToList());
            return Task.FromResult(new ChatResponse(message) { ConversationId = ConversationId });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Harness
    {
        private ExperienceContextProvider? _provider;
        private AIAgent? _agent;

        public FakeExperienceWorld World { get; } = new();

        public ScriptedSignatureSource Source { get; } = new();

        public Model Model { get; } = new();

        public List<ExperienceRecallResult> Recalls { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public ExperienceRecallOptions Recall { get; init; } = new();

        public ReceivingAgentCapabilities? ReceivingAgent { get; init; }

        public ModelAuthoredLessonPolicy ModelAuthoredLessons { get; init; } = ModelAuthoredLessonPolicy.Include;

        public ExperienceBenefitPolicy Benefit { get; init; } = ExperienceBenefitPolicy.Off;

        public ExperienceInjectionSessionLimits? SessionLimits { get; init; } = ExperienceInjectionSessionLimits.Default;

        public ExperienceHoldoutOptions Holdout { get; init; } = ExperienceHoldoutOptions.Off;

        public ExperienceMatchFloor? MinimumMatch { get; init; }

        public string? Instructions { get; init; } = "Base instructions.";

        public bool Concurrent { get; init; }

        /// <summary>The first hint the model was shown, or <see langword="null"/>.</summary>
        public string? Hint => Model.Instructions
            .Select(text => text is not null && text.IndexOf(HistoricalReferenceWriter.RecallBegin, StringComparison.Ordinal) is >= 0 and var at ? text[at..] : null)
            .FirstOrDefault(text => text is not null);

        public ExperienceContextProvider Provider => _provider ??= new ExperienceContextProvider(
            new ExperienceRetrievalService(World, RetrievalPolicy.Default, RankingWeights.Default, new FrozenTimeProvider(InjectionRecords.Now)),
            World,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                TimeProvider = new FrozenTimeProvider(InjectionRecords.Now),
                ReceivingAgent = ReceivingAgent,
                ModelAuthoredLessons = ModelAuthoredLessons,
                Benefit = Benefit,
                SessionLimits = SessionLimits,
                Holdout = Holdout,
                MinimumMatch = MinimumMatch,
                OnContextInjected = Injections.Add,
                Recall = Recall with
                {
                    SignatureSource = Source,
                    OnRecall = result =>
                    {
                        lock (Recalls)
                        {
                            Recalls.Add(result);
                        }
                    },
                },
            });

        public AIAgent Agent => _agent ??= new ChatClientAgent(Model, new ChatClientAgentOptions
        {
            AIContextProviders = [Provider],
            AllowConcurrentInvocation = Concurrent,
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                Tools =
                [
                    AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("tests failed")), "run_tests"),
                    AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("deploy failed")), "deploy"),
                ],
            },
        })
            .AsBuilder()
            .UseExperienceCapture(
                new InMemoryExperienceCaptureService(new DefaultSanitizer(AgentExperienceDefaults.Sanitization), AgentExperienceDefaults.CaptureLimits),
                new ExperienceCaptureOptions { ResolveRun = _ => new ExperienceRunDescriptor("task", TestScope, "task") })
            .Build();

        /// <summary>Stores a record, indexes it for the block only when asked, and lets the source find it by its failing tool.</summary>
        public void Publish(ExperienceRecord record, bool index = false)
        {
            if (index)
            {
                World.Publish(record);
            }
            else
            {
                World.Store(record);
            }

            Source.Add(record, tool: record.Attempts[0].ToolCalls[0].ToolName);
        }

        public Task RunAsync(params string[] tools) => RunAsync(null, tools);

        public async Task RunAsync(AgentSession? session, params string[] tools)
        {
            Model.Tools = [.. tools];
            lock (Model.Instructions)
            {
                Model.Instructions.Clear();
            }

            await Agent.RunAsync("a task whose words match nothing", session);
        }
    }
}
