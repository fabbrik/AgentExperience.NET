using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;
using AgentExperience.Storage.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 25.3: acting on measured benefit. With <see cref="ExperienceInjectionOptions.Benefit"/> on, the provider caches a
/// benefit estimate per scope and principal, refreshed in the background over a sliding window, omits lessons measured as
/// hurting, withholds every lesson of a scope measured as hurting, and optionally prefers lessons measured as helping.
/// In the telemetry collection, because some tests read the process-wide probe.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class BenefitPolicyTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read", "experience:write"], DateTimeOffset.UnixEpoch);
    private static readonly ExperienceBenefitPolicy On = new() { Enabled = true };

    private static readonly Guid A = InjectionRecords.Id(1);
    private static readonly Guid B = InjectionRecords.Id(2);

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    // ---- Options ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_policy_is_off_by_default()
    {
        var options = new ExperienceInjectionOptions();
        Assert.Same(ExperienceBenefitPolicy.Off, options.Benefit);
        Assert.False(options.Benefit.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Benefit.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Benefit.RefreshTimeout);
        Assert.Equal(TimeSpan.FromDays(30), options.Benefit.Window);
        Assert.Equal(20, options.Benefit.MinimumRunsPerArm);
        Assert.False(options.Benefit.PreferMeasuredHelpful);
    }

    public static TheoryData<ExperienceBenefitPolicy, string> InvalidPolicies => new()
    {
        { new() { Enabled = true, RefreshInterval = TimeSpan.Zero }, "options.Benefit.RefreshInterval" },
        { new() { RefreshInterval = TimeSpan.FromSeconds(-1) }, "options.Benefit.RefreshInterval" },
        { new() { Enabled = true, RefreshTimeout = TimeSpan.Zero }, "options.Benefit.RefreshTimeout" },
        { new() { RefreshTimeout = TimeSpan.FromDays(2) }, "options.Benefit.RefreshTimeout" },
        { new() { Enabled = true, Window = TimeSpan.Zero }, "options.Benefit.Window" },
        { new() { Window = TimeSpan.FromDays(-1) }, "options.Benefit.Window" },
        { new() { Enabled = true, MinimumRunsPerArm = 0 }, "options.Benefit.MinimumRunsPerArm" },
    };

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public void An_invalid_policy_is_refused_when_the_provider_is_built(ExperienceBenefitPolicy policy, string paramName)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Harness(policy));
        Assert.Equal(paramName, error.ParamName);
    }

    [Fact]
    public void No_policy_at_all_is_refused_when_the_provider_is_built()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new Harness(null!));
        Assert.Equal("options.Benefit", error.ParamName);
    }

    // ---- The provider -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_policy_off_nothing_is_read_and_injection_is_as_before()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(ExperienceBenefitPolicy.Off);
        harness.Rows = LessonHurts(A);

        await harness.InvokeTwiceAsync();

        Assert.Equal(0, harness.Queries);
        Assert.Null(harness.Provider.BenefitCache);
        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
        Assert.Empty(harness.Results[^1].Omitted);
        Assert.Empty(probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.BenefitRefresh));
    }

    [Fact]
    public async Task The_first_invocation_is_injected_as_before_and_starts_one_refresh()
    {
        var harness = new Harness(On);
        var release = new TaskCompletionSource();
        harness.Rows = LessonHurts(A);
        harness.Gate = release.Task;

        await harness.InvokeAsync();
        await harness.InvokeAsync();

        // Both invocations ran on the empty cache while the one refresh was still reading.
        Assert.All(harness.Results, result => Assert.Equal([A, B, InjectionRecords.Id(3)], result.InjectedExperienceIds));
        Assert.Equal(1, harness.Queries);

        release.SetResult();
        await harness.Provider.BenefitRefreshesSettledAsync();
        Assert.Equal(1, harness.Queries);
    }

    [Fact]
    public async Task A_lesson_measured_as_hurting_is_omitted_and_the_rest_injected()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(On);
        harness.Rows = LessonHurts(A);

        await harness.InvokeTwiceAsync();

        var result = harness.Results[^1];
        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.Equal([B, InjectionRecords.Id(3)], result.InjectedExperienceIds);
        var omitted = Assert.Single(result.Omitted);
        Assert.Equal((A, InjectionOmissionReason.MeasuredHarmful), (omitted.ExperienceId, omitted.Reason));
        Assert.Equal(ExperienceContextProvider.MeasuredHarmfulDetail, omitted.Detail);
        Assert.DoesNotContain("Check the lock table first.", harness.Block()!.Text, StringComparison.Ordinal);
        Assert.Contains(InjectionDiagnostics.RefreshedOutcome, Outcomes(probe));
    }

    [Fact]
    public async Task A_scope_measured_as_hurting_gets_no_lessons()
    {
        var harness = new Harness(On);
        harness.Rows = ScopeHurts();

        await harness.InvokeTwiceAsync();

        var result = harness.Results[^1];
        Assert.Equal(InjectionOutcome.NothingToInject, result.Outcome);
        Assert.Empty(result.InjectedExperienceIds);
        Assert.Equal([A, B, InjectionRecords.Id(3)], result.Omitted.Select(o => o.ExperienceId));
        Assert.All(result.Omitted, o =>
        {
            Assert.Equal(InjectionOmissionReason.MeasuredHarmful, o.Reason);
            Assert.Equal(ExperienceContextProvider.ScopeMeasuredHarmfulDetail, o.Detail);
        });
        Assert.Null(harness.Block());
    }

    [Fact]
    public async Task A_scope_measured_as_hurting_still_gets_the_withdrawal_notices_it_owes()
    {
        var harness = new Harness(On);
        harness.Rows = ScopeHurts();
        var session = await harness.Agent.CreateSessionAsync();

        // The first invocation runs on the empty cache, so the session is given the records.
        await harness.InvokeAsync(session);
        Assert.Equal(InjectionOutcome.Injected, harness.Results[^1].Outcome);
        await harness.Provider.BenefitRefreshesSettledAsync();

        harness.World.Replace(InjectionRecords.Record(A, TestScope, lesson: "Check the lock table first.") with { Status = ExperienceStatus.Revoked, Revision = 2 });
        await harness.InvokeAsync(session);

        var result = harness.Results[^1];
        Assert.Equal(InjectionOutcome.Retracted, result.Outcome);
        Assert.Equal([A], result.RetractedExperienceIds);
        Assert.Empty(result.InjectedExperienceIds);
        Assert.Contains($"Withdrawn: experience {A:D}", harness.Block()!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lesson_measured_as_helping_is_selected_first_when_preferred()
    {
        var harness = new Harness(On with { PreferMeasuredHelpful = true }, records: 6, maxRecords: 3);
        var helpful = InjectionRecords.Id(5);
        harness.Rows = LessonHelps(helpful);

        await harness.InvokeTwiceAsync();

        var result = harness.Results[^1];
        Assert.Equal([helpful, A, B], result.InjectedExperienceIds);
        Assert.Equal(
            [(InjectionRecords.Id(3), InjectionOmissionReason.OverRecordLimit), (InjectionRecords.Id(4), InjectionOmissionReason.OverRecordLimit), (InjectionRecords.Id(6), InjectionOmissionReason.OverRecordLimit)],
            result.Omitted.Select(o => (o.ExperienceId, o.Reason)));
    }

    [Fact]
    public async Task A_lesson_measured_as_helping_keeps_its_rank_when_not_preferred()
    {
        var harness = new Harness(On, records: 6, maxRecords: 3);
        harness.Rows = LessonHelps(InjectionRecords.Id(5));

        await harness.InvokeTwiceAsync();

        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_previous_estimate_and_says_so_only_in_telemetry()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(On);
        harness.Rows = LessonHurts(A);
        await harness.InvokeTwiceAsync();
        Assert.Equal([B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.Failure = new ExperienceStoreException("the store is down");
        await harness.InvokeTwiceAsync();

        Assert.Equal(2, harness.Queries);
        Assert.All(harness.Results.Skip(2), result =>
        {
            Assert.Equal(InjectionOutcome.Injected, result.Outcome);
            Assert.Equal([B, InjectionRecords.Id(3)], result.InjectedExperienceIds);
        });
        Assert.Contains(InjectionDiagnostics.NotRefreshedOutcome, Outcomes(probe));
        Assert.Contains(
            probe.For(InjectionDiagnostics.OperationFailuresInstrument, InjectionDiagnostics.BenefitRefresh),
            m => (string)m.Tags["error.class"]! == "Infrastructure");
    }

    [Fact]
    public async Task A_refresh_that_runs_past_its_timeout_is_abandoned_and_the_previous_estimate_kept()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(On);
        harness.Rows = LessonHurts(A);
        await harness.InvokeTwiceAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.Gate = new TaskCompletionSource().Task;
        await harness.InvokeAsync();
        await harness.Clock.AdvanceUntilAsync(harness.Provider.BenefitRefreshesSettledAsync(), TimeSpan.FromSeconds(5));

        Assert.Contains(InjectionDiagnostics.OutcomeUnknownOutcome, Outcomes(probe));
        await harness.InvokeAsync();
        Assert.Equal([B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
    }

    [Fact]
    public async Task Evidence_older_than_the_window_ages_out_and_the_lesson_is_injected_again()
    {
        var harness = new Harness(On);
        harness.Rows = LessonHurts(A);
        await harness.InvokeTwiceAsync();
        Assert.Equal([B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);

        // Every row was assigned at the start; 31 days later none is in the window.
        harness.Clock.Advance(TimeSpan.FromDays(31));
        await harness.InvokeAsync();

        // That invocation still used the cached verdict while the refresh ran.
        Assert.Equal([B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
        await harness.Provider.BenefitRefreshesSettledAsync();

        await harness.InvokeAsync();
        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
        Assert.Empty(harness.Results[^1].Omitted);
    }

    [Fact]
    public async Task Two_principals_in_one_scope_never_share_an_estimate()
    {
        var harness = new Harness(On);
        harness.Rows = LessonHurts(A);
        harness.Answer = (authorization, _) => authorization.PrincipalId == "host" ? LessonHurts(A) : [];

        await harness.InvokeTwiceAsync();
        harness.Authorization = Authorization with { PrincipalId = "someone-else" };
        await harness.InvokeTwiceAsync();

        Assert.Equal(2, harness.Provider.BenefitCache!.Count);
        Assert.Equal(["host", "someone-else"], harness.ReadAs);
        Assert.Equal([B, InjectionRecords.Id(3)], harness.Results[1].InjectedExperienceIds);
        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
    }

    [Fact]
    public async Task An_estimate_the_store_could_not_give_is_never_acted_on()
    {
        var harness = new Harness(On);
        harness.Rows = ScopeHurts();
        harness.Untracked = true;
        await harness.InvokeTwiceAsync();
        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);

        harness.Untracked = false;
        harness.Refusal = ExperienceStoreOutcome.Denied;
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        await harness.InvokeTwiceAsync();
        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
    }

    [Fact]
    public async Task Only_assignments_inside_the_window_are_counted()
    {
        var harness = new Harness(On with { Window = TimeSpan.FromDays(1) });

        // The harmful evidence is two days old; the recent rows alone say nothing.
        harness.Rows = [.. LessonHurts(A, InjectionRecords.Now.AddDays(-2)), .. Runs(B, 5, TaskVerificationStatus.Verified, 5, TaskVerificationStatus.Failed, InjectionRecords.Now, offset: 9_000)];

        await harness.InvokeTwiceAsync();

        Assert.Equal([A, B, InjectionRecords.Id(3)], harness.Results[^1].InjectedExperienceIds);
    }

    /// <summary>
    /// Acceptance: 100 finalized runs at rate 0.5 where runs given lesson A fail and runs it was withheld from verify; with
    /// the policy on and the refresh settled, the next invocation omits A as <see cref="InjectionOmissionReason.MeasuredHarmful"/>.
    /// </summary>
    [Fact]
    public async Task One_hundred_runs_where_the_lesson_hurts_lead_to_it_being_omitted()
    {
        var clock = new ManualClock(InjectionRecords.Now);
        var store = new InMemoryExperienceRecordStore(clock);
        var created = await store.CreateAsync(Authorization, InjectionRecords.Record(A, TestScope, lesson: "Check the lock table first."), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);

        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000));
        var lifecycle = new ExperienceLifecycleService(store, indexingService: null, new ExperienceIndependenceOptions(), capture);
        var finalization = new ExperienceFinalizationService(capture, new DefaultExperienceReflector(), store, lifecycle);
        var holdout = new ExperienceHoldoutOptions { Rate = 0.5 };
        var injections = new List<ExperienceInjectionResult>();
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                TimeProvider = clock,
                Holdout = holdout,
                Benefit = On,
                OnContextInjected = result =>
                {
                    lock (injections)
                    {
                        injections.Add(result);
                    }
                },
            });

        var finalized = new List<FinalizeExperienceResult>();
        var next = Guid.Empty;
        var client = new RecordingChatClient();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] })
            .AsBuilder()
            .UseExperienceCapture(capture, new ExperienceCaptureOptions
            {
                ResolveRun = _ => new ExperienceRunDescriptor("nightly-batch", TestScope, "Process the nightly batch", ContinuesRunId: next),
                CaptureToolCalls = false,
                FinalizationService = finalization,
                ResolveFinalization = context => Request(
                    context.Run.RunId,
                    pass: HoldoutAssignmentFunction.Assign(context.Run.RunId, holdout.Salt, holdout.Rate) == HoldoutArm.Holdout),
                OnRunFinalized = finalized.Add,
            })
            .Build();

        foreach (var run in Enumerable.Range(0, 100).Select(i => new Guid($"5a5a5a5a-0000-4000-8000-{i:D12}")))
        {
            next = run;
            await agent.RunAsync("another refund ticket stuck on a lock");
        }

        await provider.InjectionRecordsSettledAsync();
        await provider.BenefitRefreshesSettledAsync();
        Assert.Equal(100, finalized.Count);
        Assert.All(finalized, result => Assert.True(result.IsDurable));

        // The holdout ran as without the policy: no estimate existed while the runs were made.
        Assert.All(injections, result => Assert.Contains(result.Outcome, new[] { InjectionOutcome.Injected, InjectionOutcome.Withheld }));
        Assert.DoesNotContain(injections, result => result.Omitted.Any(o => o.Reason == InjectionOmissionReason.MeasuredHarmful));

        // The cached estimate is stale after the interval: one invocation starts the refresh, the next acts on it.
        clock.Advance(TimeSpan.FromMinutes(11));
        var uncaptured = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [provider] });
        await uncaptured.RunAsync("another refund ticket stuck on a lock");
        await provider.BenefitRefreshesSettledAsync();
        await uncaptured.RunAsync("another refund ticket stuck on a lock");

        var result = injections[^1];
        Assert.Empty(result.InjectedExperienceIds);
        var omitted = Assert.Single(result.Omitted, o => o.ExperienceId == A);
        Assert.Equal(InjectionOmissionReason.MeasuredHarmful, omitted.Reason);
    }

    // ---- Rows ---------------------------------------------------------------------------------------------------------

    /// <summary>Lesson <paramref name="lesson"/> hurts (given: 0 of 25 verify; withheld: 25 of 25), and lesson B helps by as much, so the scope overall says nothing.</summary>
    private static List<HoldoutOutcomeRow> LessonHurts(Guid lesson, DateTimeOffset? at = null) =>
    [
        .. Runs(lesson, 25, TaskVerificationStatus.Failed, 25, TaskVerificationStatus.Verified, at ?? InjectionRecords.Now, offset: 0),
        .. Runs(B, 25, TaskVerificationStatus.Verified, 25, TaskVerificationStatus.Failed, at ?? InjectionRecords.Now, offset: 1_000),
    ];

    /// <summary>Every run named A and B; given them, 0 of 25 verify; withheld, 25 of 25.</summary>
    private static List<HoldoutOutcomeRow> ScopeHurts() =>
    [
        .. Runs(A, 25, TaskVerificationStatus.Failed, 25, TaskVerificationStatus.Verified, InjectionRecords.Now, offset: 0),
        .. Runs(B, 25, TaskVerificationStatus.Failed, 25, TaskVerificationStatus.Verified, InjectionRecords.Now, offset: 0),
    ];

    private static List<HoldoutOutcomeRow> LessonHelps(Guid lesson) =>
        [.. Runs(lesson, 25, TaskVerificationStatus.Verified, 25, TaskVerificationStatus.Failed, InjectionRecords.Now, offset: 0)];

    private static IEnumerable<HoldoutOutcomeRow> Runs(
        Guid lesson,
        int treatment,
        TaskVerificationStatus treatmentOutcome,
        int holdout,
        TaskVerificationStatus holdoutOutcome,
        DateTimeOffset at,
        int offset)
    {
        for (var i = 0; i < treatment + holdout; i++)
        {
            yield return new HoldoutOutcomeRow(
                new Guid($"{offset + i:D8}-0000-0000-0000-000000000000"),
                i < treatment ? HoldoutArm.Treatment : HoldoutArm.Holdout,
                lesson,
                at,
                i < treatment ? treatmentOutcome : holdoutOutcome);
        }
    }

    private static string[] Outcomes(TelemetryProbe probe) =>
        [.. probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.BenefitRefresh).Select(m => (string)m.Tags["outcome"]!)];

    private static FinalizeExperienceRequest Request(Guid runId, bool pass)
    {
        var round = Guid.NewGuid();
        return new FinalizeExperienceRequest(
            RunId: runId,
            Authorization: Authorization,
            ClosedRound: new ClosedVerificationRound(round, ArtifactRevision),
            RequiredChecks: [new RequiredCheck("tests", "TestResult")],
            Evidence: [new Evidence(Guid.NewGuid(), round, ArtifactRevision, "tests", "TestResult", pass ? CheckResult.Pass : CheckResult.Fail, "ci", null, InjectionRecords.Now)],
            CurrentArtifactRevision: ArtifactRevision,
            StorageDecision: StorageDecision.Permit,
            FinalizedAt: InjectionRecords.Now);
    }

    /// <summary>A provider over the fake world, three or more published records, and a holdout read the test controls.</summary>
    private sealed class Harness
    {
        private readonly RecordingChatClient _client = new();
        private int _queries;

        public Harness(ExperienceBenefitPolicy policy, int records = 3, int maxRecords = ExperienceInjectionLimits.DefaultMaxRecords)
        {
            for (var i = 1; i <= records; i++)
            {
                World.Publish(
                    InjectionRecords.Record(InjectionRecords.Id(i), TestScope, lesson: i == 1 ? "Check the lock table first." : $"Lesson {i}."),
                    relevance: 1d - (i * 0.1d));
            }

            World.TracksHoldout = true;
            World.OnQueryHoldout = async (authorization, query, _) =>
            {
                Interlocked.Increment(ref _queries);
                lock (ReadAs)
                {
                    ReadAs.Add(authorization.PrincipalId);
                }

                if (Gate is { } gate)
                {
                    await gate;
                }

                if (Failure is { } failure)
                {
                    throw failure;
                }

                if (Refusal is { } refusal)
                {
                    return new HoldoutOutcomeQueryResult(refusal, [], []);
                }

                if (Untracked)
                {
                    return HoldoutOutcomeQueryResult.NotTracked;
                }

                var rows = Answer?.Invoke(authorization, query) ?? Rows;
                return new HoldoutOutcomeQueryResult(ExperienceStoreOutcome.Found, rows.OrderBy(r => r.RunId).ThenBy(r => r.ExperienceId).ToList(), []);
            };

            Provider = new ExperienceContextProvider(
                new ExperienceRetrievalService(World, RetrievalPolicy.Default, RankingWeights.Default, Clock),
                World,
                new ExperienceInjectionOptions
                {
                    ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                    TimeProvider = Clock,
                    Limits = ExperienceInjectionLimits.Default with { MaxRecords = maxRecords },
                    Benefit = policy,
                    OnContextInjected = result =>
                    {
                        lock (Results)
                        {
                            Results.Add(result);
                        }
                    },
                });

            Agent = new ChatClientAgent(_client, new ChatClientAgentOptions { AIContextProviders = [Provider] });
        }

        public FakeExperienceWorld World { get; } = new();

        public ManualClock Clock { get; } = new(InjectionRecords.Now);

        public ExperienceContextProvider Provider { get; }

        public AIAgent Agent { get; }

        public List<ExperienceInjectionResult> Results { get; } = [];

        public List<string> ReadAs { get; } = [];

        public AuthorizationContext Authorization { get; set; } = BenefitPolicyTests.Authorization;

        public List<HoldoutOutcomeRow> Rows { get; set; } = [];

        public Func<AuthorizationContext, HoldoutOutcomeQuery, List<HoldoutOutcomeRow>>? Answer { get; set; }

        public Task? Gate { get; set; }

        public Exception? Failure { get; set; }

        public ExperienceStoreOutcome? Refusal { get; set; }

        public bool Untracked { get; set; }

        public int Queries => Volatile.Read(ref _queries);

        public Task<AgentResponse> InvokeAsync(AgentSession? session = null) =>
            Agent.RunAsync("another refund ticket stuck on a lock", session);

        /// <summary>One invocation that starts the refresh, the refresh settled, and one that acts on it.</summary>
        public async Task InvokeTwiceAsync()
        {
            await InvokeAsync();
            await Provider.BenefitRefreshesSettledAsync();
            await InvokeAsync();
        }

        public ChatMessage? Block() =>
            _client.LastMessages?.LastOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true);
    }
}
