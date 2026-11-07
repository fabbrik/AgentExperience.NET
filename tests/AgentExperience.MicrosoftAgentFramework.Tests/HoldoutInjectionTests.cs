using AgentExperience.Core.Capture;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 25.1: the injection holdout. On a deterministic, run-ID-derived share of captured runs that have a block to
/// inject, the block is withheld and recorded; on the rest it is injected as before and recorded as the treatment arm.
/// Off by default. In the telemetry collection, because some tests read the process-wide probe.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class HoldoutInjectionTests
{
    private const string TaskId = "triage-refund";
    private const string ArtifactRevision = "rev-1";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read", "experience:write"], DateTimeOffset.UnixEpoch);

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

    // ---- The assignment function ------------------------------------------------------------------------------------

    /// <summary>
    /// Pinned against an independent implementation (Python's hashlib over <c>uuid.UUID(s).bytes</c>, RFC 4122 order,
    /// then the salt's UTF-8 bytes), so the ports can reproduce every arm.
    /// </summary>
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", 0.3139116302163824, HoldoutArm.Holdout)]
    [InlineData("11111111-2222-3333-4444-555555555555", 0.24520626598382958, HoldoutArm.Holdout)]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff", 0.47486808769227573, HoldoutArm.Holdout)]
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479", 0.25656011927796707, HoldoutArm.Holdout)]
    [InlineData("a3bb189e-8bf9-3888-9912-ace4e6543002", 0.8913670481238475, HoldoutArm.Treatment)]
    [InlineData("c9a646d3-9c61-4cb7-bfcd-ee2522c8f633", 0.8851032471291671, HoldoutArm.Treatment)]
    public void A_run_lands_where_SHA_256_over_its_id_and_the_salt_puts_it(string runId, double fraction, HoldoutArm armAtHalf)
    {
        var id = Guid.Parse(runId);
        Assert.Equal(fraction, HoldoutAssignmentFunction.Fraction(id, ExperienceHoldoutOptions.DefaultSalt), 15);
        Assert.Equal(armAtHalf, HoldoutAssignmentFunction.Assign(id, ExperienceHoldoutOptions.DefaultSalt, 0.5));

        // Pure: the same inputs give the same arm every time.
        Assert.Equal(armAtHalf, HoldoutAssignmentFunction.Assign(id, ExperienceHoldoutOptions.DefaultSalt, 0.5));
    }

    [Fact]
    public void Rate_zero_never_holds_a_run_out_and_rate_one_always_does()
    {
        foreach (var id in Ids(2_000, seed: 7))
        {
            Assert.Equal(HoldoutArm.Treatment, HoldoutAssignmentFunction.Assign(id, ExperienceHoldoutOptions.DefaultSalt, 0));
            Assert.Equal(HoldoutArm.Holdout, HoldoutAssignmentFunction.Assign(id, ExperienceHoldoutOptions.DefaultSalt, 1));
            var fraction = HoldoutAssignmentFunction.Fraction(id, ExperienceHoldoutOptions.DefaultSalt);
            Assert.InRange(fraction, 0d, 1d);
            Assert.True(fraction < 1d);
        }
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void The_held_out_share_of_ten_thousand_runs_is_within_three_points_of_the_rate(double rate)
    {
        var held = Ids(10_000, seed: 25).Count(id => HoldoutAssignmentFunction.Assign(id, ExperienceHoldoutOptions.DefaultSalt, rate) == HoldoutArm.Holdout);
        Assert.InRange(held / 10_000d, rate - 0.03, rate + 0.03);
    }

    [Fact]
    public void A_different_salt_draws_different_arms()
    {
        var ids = Ids(1_000, seed: 3);
        var moved = ids.Count(id =>
            HoldoutAssignmentFunction.Assign(id, ExperienceHoldoutOptions.DefaultSalt, 0.5)
            != HoldoutAssignmentFunction.Assign(id, "another-experiment", 0.5));
        Assert.InRange(moved, 350, 650);
    }

    // ---- Options ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0000001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_rate_outside_zero_to_one_is_refused_when_the_provider_is_built(double rate)
    {
        var world = new FakeExperienceWorld();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Provider(world, new ExperienceHoldoutOptions { Rate = rate }, []));
        Assert.Equal("options.Holdout.Rate", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_salt_is_refused_when_the_provider_is_built(string? salt)
    {
        var world = new FakeExperienceWorld();
        var error = Assert.Throws<ArgumentException>(() => Provider(world, new ExperienceHoldoutOptions { Rate = 0.5, Salt = salt! }, []));
        Assert.Equal("options.Holdout.Salt", error.ParamName);
    }

    [Fact]
    public void No_holdout_options_at_all_is_refused_when_the_provider_is_built()
    {
        var world = new FakeExperienceWorld();
        Assert.Throws<ArgumentNullException>(() => Provider(world, null!, []));
    }

    [Fact]
    public void The_holdout_is_off_by_default()
    {
        var options = new ExperienceInjectionOptions();
        Assert.Same(ExperienceHoldoutOptions.Off, options.Holdout);
        Assert.Equal(0, options.Holdout.Rate);
        Assert.Equal("agentexperience-holdout-v1", options.Holdout.Salt);
    }

    // ---- The provider -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_default_options_a_captured_run_is_injected_as_before_and_nothing_is_assigned()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(ExperienceHoldoutOptions.Off);

        var runId = Guid.NewGuid();
        await harness.RunAsync(runId);
        await harness.Provider.InjectionRecordsSettledAsync();

        var result = Assert.Single(harness.Results);
        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.Empty(result.WithheldExperienceIds);
        Assert.NotNull(harness.Block());
        Assert.Equal(2, harness.ExposuresOf(runId).Count);
        Assert.Empty(harness.Assignments);
        Assert.Empty(probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.RecordHoldout));
    }

    [Fact]
    public async Task The_treatment_arm_is_injected_as_before_and_its_records_are_recorded_with_the_arm()
    {
        using var probe = TelemetryProbe.Start();

        // Only a run whose digest begins with eight zero bytes falls below the smallest positive rate.
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = double.Epsilon });
        var runId = Guid.NewGuid();
        await harness.RunAsync(runId);
        await harness.Provider.InjectionRecordsSettledAsync();

        var result = Assert.Single(harness.Results);
        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.Empty(result.WithheldExperienceIds);
        Assert.Equal(2, result.InjectedExperienceIds.Count);
        Assert.NotNull(harness.Block());

        // Exposure, injection tracking: exactly as before.
        var exposures = harness.ExposuresOf(runId);
        Assert.Equal(result.InjectedExperienceIds.Order(), exposures.Select(e => e.ExperienceId).Order());
        Assert.Single(harness.InjectionWrites);

        var (scope, assignment) = Assert.Single(harness.Assignments);
        Assert.Equal(TestScope, scope);
        Assert.Equal(runId, assignment.RunId);
        Assert.Equal(HoldoutArm.Treatment, assignment.Arm);
        Assert.Equal(InjectionRecords.Now, assignment.AssignedAt);
        Assert.Equal(result.InjectedExperienceIds, assignment.Records.Select(r => r.ExperienceId));
        Assert.Equal(exposures.OrderBy(e => e.ExperienceId), assignment.Records.OrderBy(r => r.ExperienceId));
        Assert.Equal([InjectionDiagnostics.RecordedOutcome], Outcomes(probe));
    }

    [Fact]
    public async Task The_holdout_arm_gets_no_block_no_exposure_and_no_injection_record_and_its_withheld_records_are_recorded()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 });
        var runId = Guid.NewGuid();

        var response = await harness.RunAsync(runId);
        await harness.Provider.InjectionRecordsSettledAsync();

        Assert.Equal("Hello, world", response.Text);
        Assert.Null(harness.Block());

        var result = Assert.Single(harness.Results);
        Assert.Equal(InjectionOutcome.Withheld, result.Outcome);
        Assert.False(result.Injected);
        Assert.Empty(result.InjectedExperienceIds);
        Assert.Equal(0, result.PayloadBytes);
        Assert.Null(result.Failure);
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(2)], result.WithheldExperienceIds);

        Assert.Empty(harness.ExposuresOf(runId));
        Assert.Empty(harness.InjectionWrites);

        var (_, assignment) = Assert.Single(harness.Assignments);
        Assert.Equal(runId, assignment.RunId);
        Assert.Equal(HoldoutArm.Holdout, assignment.Arm);
        Assert.Equal(result.WithheldExperienceIds, assignment.Records.Select(r => r.ExperienceId));
        Assert.All(assignment.Records, r => Assert.Equal(harness.World.Stored[r.ExperienceId].Revision, r.Revision));

        Assert.Equal([InjectionDiagnostics.RecordedOutcome], Outcomes(probe));
        Assert.Contains(
            probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.Inject),
            m => (string)m.Tags["outcome"]! == nameof(InjectionOutcome.Withheld));
    }

    [Theory]
    [InlineData("a3bb189e-8bf9-3888-9912-ace4e6543002", InjectionOutcome.Injected)]
    [InlineData("c9a646d3-9c61-4cb7-bfcd-ee2522c8f633", InjectionOutcome.Injected)]
    [InlineData("00000000-0000-0000-0000-000000000001", InjectionOutcome.Withheld)]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff", InjectionOutcome.Withheld)]
    public async Task At_half_each_run_goes_to_the_arm_its_id_hashes_to(string runId, InjectionOutcome expected)
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 0.5 });
        await harness.RunAsync(Guid.Parse(runId));
        await harness.Provider.InjectionRecordsSettledAsync();

        Assert.Equal(expected, Assert.Single(harness.Results).Outcome);
        var (_, assignment) = Assert.Single(harness.Assignments);
        Assert.Equal(expected == InjectionOutcome.Withheld ? HoldoutArm.Holdout : HoldoutArm.Treatment, assignment.Arm);
        Assert.Equal(expected == InjectionOutcome.Injected, harness.Block() is not null);
    }

    [Fact]
    public async Task An_uncaptured_invocation_is_injected_as_before_and_assigned_nothing_even_at_rate_one()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 });

        await harness.RunUncapturedAsync();
        await harness.Provider.InjectionRecordsSettledAsync();

        Assert.Equal(InjectionOutcome.Injected, Assert.Single(harness.Results).Outcome);
        Assert.NotNull(harness.Block());
        Assert.Empty(harness.Assignments);
        Assert.Empty(Outcomes(probe));
    }

    [Fact]
    public async Task An_invocation_with_nothing_to_inject_is_never_withheld_and_assigned_nothing()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, records: 0);
        await harness.RunAsync(Guid.NewGuid());
        await harness.Provider.InjectionRecordsSettledAsync();

        Assert.Equal(InjectionOutcome.NothingToInject, Assert.Single(harness.Results).Outcome);
        Assert.Empty(harness.Assignments);
    }

    [Fact]
    public async Task Every_invocation_of_a_held_out_run_is_withheld_again()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, completeRuns: false);
        var runId = Guid.NewGuid();

        await harness.RunAsync(runId);
        await harness.RunAsync(runId);
        await harness.Provider.InjectionRecordsSettledAsync();

        Assert.Equal([InjectionOutcome.Withheld, InjectionOutcome.Withheld], harness.Results.Select(r => r.Outcome));
        Assert.Null(harness.Block());
        Assert.Empty(harness.ExposuresOf(runId));

        // The provider records each invocation; the store keeps one row per run and record (its primary key).
        Assert.All(harness.Assignments, a => Assert.Equal((runId, HoldoutArm.Holdout), (a.Assignment.RunId, a.Assignment.Arm)));
    }

    [Fact]
    public async Task A_withheld_block_stages_nothing_in_the_session()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 });
        var session = await harness.Agent.CreateSessionAsync();

        await harness.RunAsync(Guid.NewGuid(), session);

        var result = Assert.Single(harness.Results);
        Assert.Equal(InjectionOutcome.Withheld, result.Outcome);
        Assert.NotNull(result.Session);
        Assert.Equal(0, result.Session!.RecordsUsed);
        Assert.Equal(0, result.Session.BytesUsed);
        Assert.Equal(0, result.Session.TrackedRecords);
    }

    [Fact]
    public async Task A_held_out_run_is_still_given_the_withdrawal_notices_it_owes_but_none_of_the_records()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 0.5 });
        var session = await harness.Agent.CreateSessionAsync();
        var treatment = Guid.Parse("a3bb189e-8bf9-3888-9912-ace4e6543002");
        var holdout = Guid.Parse("00000000-0000-0000-0000-000000000001");

        await harness.RunAsync(treatment, session);
        Assert.Equal(InjectionOutcome.Injected, harness.Results[^1].Outcome);

        var first = InjectionRecords.Record(InjectionRecords.Id(1), TestScope, lesson: "Check the lock table first.");
        harness.World.Replace(first with { Status = ExperienceStatus.Revoked, Revision = 2 });
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(3), TestScope, lesson: "Lesson 3."), relevance: 0.95d);

        await harness.RunAsync(holdout, session);
        await harness.Provider.InjectionRecordsSettledAsync();

        var result = harness.Results[^1];
        Assert.Equal(InjectionOutcome.Retracted, result.Outcome);
        Assert.Equal([InjectionRecords.Id(1)], result.RetractedExperienceIds);
        Assert.Equal([InjectionRecords.Id(3)], result.WithheldExperienceIds);
        Assert.Empty(result.InjectedExperienceIds);

        var block = harness.Block()?.Text;
        Assert.NotNull(block);
        Assert.Contains($"Withdrawn: experience {InjectionRecords.Id(1):D}", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Lesson 3.", block, StringComparison.Ordinal);
        Assert.Empty(harness.ExposuresOf(holdout));

        var (_, assignment) = Assert.Single(harness.Assignments, a => a.Assignment.RunId == holdout);
        Assert.Equal(HoldoutArm.Holdout, assignment.Arm);
        Assert.Equal([InjectionRecords.Id(3)], assignment.Records.Select(r => r.ExperienceId));
    }

    [Theory]
    [InlineData(1d, "throws", "NotRecorded")]
    [InlineData(1d, "denied", "NotRecorded")]
    [InlineData(1d, "not tracked", "NotTracked")]
    [InlineData(double.Epsilon, "throws", "NotRecorded")]
    [InlineData(double.Epsilon, "denied", "NotRecorded")]
    [InlineData(double.Epsilon, "not tracked", "NotTracked")]
    public async Task A_store_that_cannot_record_the_assignment_changes_nothing_about_the_invocation(double rate, string failure, string outcome)
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = rate });
        harness.World.TracksHoldout = failure != "not tracked";
        harness.World.OnRecordHoldout = (_, _, _, _) => failure switch
        {
            "throws" => throw new ExperienceStoreException("the assignment table is not granted"),
            _ => Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Denied, [])),
        };

        var runId = Guid.NewGuid();
        var response = await harness.RunAsync(runId);
        await harness.Provider.InjectionRecordsSettledAsync();

        Assert.Equal("Hello, world", response.Text);
        var result = Assert.Single(harness.Results);
        Assert.Null(result.Failure);
        if (rate >= 1)
        {
            Assert.Equal(InjectionOutcome.Withheld, result.Outcome);
            Assert.Null(harness.Block());
        }
        else
        {
            Assert.Equal(InjectionOutcome.Injected, result.Outcome);
            Assert.NotNull(harness.Block());
            Assert.Equal(2, harness.ExposuresOf(runId).Count);
        }

        Assert.Equal([outcome], Outcomes(probe));
    }

    /// <summary>
    /// Acceptance: at a rate of 0.2, of 10,000 captured runs with a block, 1,700 to 2,300 are withheld, and every run has
    /// rows with exactly one arm.
    /// </summary>
    [Fact]
    public async Task Ten_thousand_captured_runs_at_a_fifth_are_withheld_about_a_fifth_of_the_time_and_each_has_one_arm()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 0.2 });
        foreach (var runId in Ids(10_000, seed: 2025))
        {
            await harness.RunAsync(runId);
        }

        await harness.Provider.InjectionRecordsSettledAsync();

        var withheld = harness.Results.Count(r => r.Outcome == InjectionOutcome.Withheld);
        Assert.Equal(10_000, harness.Results.Count);
        Assert.Equal(10_000 - withheld, harness.Results.Count(r => r.Outcome == InjectionOutcome.Injected));
        Assert.InRange(withheld, 1_700, 2_300);

        var byRun = harness.Assignments.GroupBy(a => a.Assignment.RunId).ToList();
        Assert.Equal(10_000, byRun.Count);
        Assert.All(byRun, run => Assert.Single(run.Select(a => a.Assignment.Arm).Distinct()));
        Assert.Equal(withheld, byRun.Count(run => run.First().Assignment.Arm == HoldoutArm.Holdout));
    }

    /// <summary>
    /// Acceptance: a held-out run, once finalized, is exposed to nothing, and its assignment rows name the withheld
    /// records.
    /// </summary>
    [Fact]
    public async Task A_finalized_held_out_run_is_exposed_to_nothing_and_its_rows_name_what_was_withheld()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 0.5 }, finalize: true);

        // This run hashes below one half: the holdout arm.
        var runId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await harness.RunAsync(runId);
        await harness.Provider.InjectionRecordsSettledAsync();

        var outcome = Assert.Single(harness.Finalized);
        Assert.True(outcome.IsDurable);
        var record = harness.World.Stored[ExperienceFinalizationService.ExperienceIdFor(runId, TestScope)];
        Assert.Equal(runId, record.SourceRunId);
        Assert.Empty(record.Provenance.ExposedTo);

        var (_, assignment) = Assert.Single(harness.Assignments);
        Assert.Equal(HoldoutArm.Holdout, assignment.Arm);
        Assert.Equal(runId, assignment.RunId);
        Assert.Equal(Assert.Single(harness.Results).WithheldExperienceIds, assignment.Records.Select(r => r.ExperienceId));
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(2)], assignment.Records.Select(r => r.ExperienceId));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private static List<Guid> Ids(int count, int seed)
    {
        var random = new Random(seed);
        var ids = new List<Guid>(count);
        var bytes = new byte[16];
        for (var i = 0; i < count; i++)
        {
            random.NextBytes(bytes);
            ids.Add(new Guid(bytes));
        }

        return ids;
    }

    private static string[] Outcomes(TelemetryProbe probe) =>
        [.. probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.RecordHoldout).Select(m => (string)m.Tags["outcome"]!)];

    private static ExperienceContextProvider Provider(FakeExperienceWorld world, ExperienceHoldoutOptions holdout, List<ExperienceInjectionResult> results)
    {
        var clock = new FrozenTimeProvider(InjectionRecords.Now);
        return new ExperienceContextProvider(
            new ExperienceRetrievalService(world, RetrievalPolicy.Default, RankingWeights.Default, clock),
            world,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                TimeProvider = clock,
                Holdout = holdout,
                OnContextInjected = result =>
                {
                    lock (results)
                    {
                        results.Add(result);
                    }
                },
            });
    }

    private sealed class Harness
    {
        private readonly InMemoryExperienceCaptureService _capture;
        private readonly RecordingChatClient _client = new();
        private readonly RecordingChatClient _uncapturedClient = new();
        private readonly AIAgent _uncaptured;
        private Guid _nextRun;

        public Harness(ExperienceHoldoutOptions holdout, int records = 2, bool completeRuns = true, bool finalize = false)
        {
            for (var i = 1; i <= records; i++)
            {
                World.Publish(
                    InjectionRecords.Record(InjectionRecords.Id(i), TestScope, lesson: i == 1 ? "Check the lock table first." : $"Lesson {i}."),
                    relevance: 1d - (i * 0.1d));
            }

            World.TracksInjections = true;
            World.OnRecordInjection = (_, _, ids, _, _) =>
            {
                lock (InjectionWrites)
                {
                    InjectionWrites.Add([.. ids]);
                }

                return Task.FromResult(new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Committed, []));
            };
            World.TracksHoldout = true;
            World.OnRecordHoldout = (_, scope, assignment, _) =>
            {
                lock (Assignments)
                {
                    Assignments.Add((scope, assignment));
                }

                return Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Committed, []));
            };

            Provider = HoldoutInjectionTests.Provider(World, holdout, Results);
            _capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000));

            ExperienceFinalizationService? finalization = null;
            if (finalize)
            {
                var lifecycle = new ExperienceLifecycleService(World, indexingService: null, new ExperienceIndependenceOptions(), _capture);
                finalization = new ExperienceFinalizationService(_capture, new DefaultExperienceReflector(), World, lifecycle);
            }

            Agent = new ChatClientAgent(_client, new ChatClientAgentOptions { AIContextProviders = [Provider] })
                .AsBuilder()
                .UseExperienceCapture(_capture, new ExperienceCaptureOptions
                {
                    ResolveRun = _ => new ExperienceRunDescriptor(TaskId, TestScope, "Triage a refund ticket", ContinuesRunId: _nextRun),
                    CaptureToolCalls = false,
                    ShouldCompleteRun = _ => completeRuns,
                    FinalizationService = finalization,
                    ResolveFinalization = finalization is null ? null : context => Request(context.Run.RunId),
                    OnRunFinalized = Finalized.Add,
                })
                .Build();

            _uncaptured = new ChatClientAgent(_uncapturedClient, new ChatClientAgentOptions { AIContextProviders = [Provider] });
        }

        public FakeExperienceWorld World { get; } = new();

        public ExperienceContextProvider Provider { get; }

        public AIAgent Agent { get; }

        public List<ExperienceInjectionResult> Results { get; } = [];

        public List<(Scope Scope, HoldoutAssignment Assignment)> Assignments { get; } = [];

        public List<Guid[]> InjectionWrites { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        private RecordingChatClient? _last;

        public Task<AgentResponse> RunAsync(Guid runId, AgentSession? session = null)
        {
            _nextRun = runId;
            _last = _client;
            return Agent.RunAsync("another refund ticket stuck on a lock", session);
        }

        public Task<AgentResponse> RunUncapturedAsync()
        {
            _last = _uncapturedClient;
            return _uncaptured.RunAsync("another refund ticket stuck on a lock");
        }

        /// <summary>The Historical Reference message the model was last given, or <see langword="null"/>.</summary>
        public ChatMessage? Block() =>
            _last?.LastMessages?.LastOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true);

        public IReadOnlyList<RunExposure> ExposuresOf(Guid runId)
        {
            Assert.True(_capture.TryGetRun(runId, out var run));
            return run.Provenance.ExposedTo;
        }

        private static FinalizeExperienceRequest Request(Guid runId)
        {
            var round = Guid.NewGuid();
            return new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Authorization,
                ClosedRound: new ClosedVerificationRound(round, ArtifactRevision),
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: [new Evidence(Guid.NewGuid(), round, ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, InjectionRecords.Now)],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: InjectionRecords.Now);
        }
    }
}
