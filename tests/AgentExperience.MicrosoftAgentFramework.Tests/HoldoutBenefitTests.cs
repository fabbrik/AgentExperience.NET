using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 25.2: <see cref="AgentExperienceBenefit.EstimateAsync"/>, end to end through capture, the injection holdout,
/// finalization and the in-memory store, and its handling of stores that do not track, refuse or page.
/// </summary>
public sealed class HoldoutBenefitTests
{
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

    /// <summary>
    /// Acceptance: 100 finalized runs at rate 0.5, treatment runs verifying and holdout runs failing, read back as
    /// <see cref="ExperienceBenefitVerdict.Helps"/> overall and for each lesson, over the runs each lesson was in.
    /// </summary>
    [Fact]
    public async Task One_hundred_finalized_runs_at_half_where_treatment_verifies_and_holdout_fails_help()
    {
        var clock = new FrozenTimeProvider(InjectionRecords.Now);
        var store = new InMemoryExperienceRecordStore(clock);
        var lessons = new[] { InjectionRecords.Id(1), InjectionRecords.Id(2) };
        foreach (var id in lessons)
        {
            var created = await store.CreateAsync(Authorization, InjectionRecords.Record(id, TestScope, lesson: $"Check the lock table first ({id})."), CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        }

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

        // The runs' own task shares no word with the lessons', so a finalized run never becomes a lesson of a later one.
        var agent = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [provider] })
            .AsBuilder()
            .UseExperienceCapture(capture, new ExperienceCaptureOptions
            {
                ResolveRun = _ => new ExperienceRunDescriptor("nightly-batch", TestScope, "Process the nightly batch", ContinuesRunId: next),
                CaptureToolCalls = false,
                FinalizationService = finalization,
                ResolveFinalization = context => Request(
                    context.Run.RunId,
                    HoldoutAssignmentFunction.Assign(context.Run.RunId, holdout.Salt, holdout.Rate) == HoldoutArm.Treatment),
                OnRunFinalized = finalized.Add,
            })
            .Build();

        var runs = Enumerable.Range(0, 100).Select(i => new Guid($"5a5a5a5a-0000-4000-8000-{i:D12}")).ToList();
        foreach (var run in runs)
        {
            next = run;
            await agent.RunAsync("another refund ticket stuck on a lock");
        }

        await provider.InjectionRecordsSettledAsync();
        Assert.Equal(100, finalized.Count);
        Assert.All(finalized, result => Assert.True(result.IsDurable));
        Assert.Equal(100, injections.Count);

        var treatment = runs.Count(run => HoldoutAssignmentFunction.Assign(run, holdout.Salt, holdout.Rate) == HoldoutArm.Treatment);
        Assert.InRange(treatment, 20, 80);

        var services = new ServiceCollection().AddSingleton<IExperienceRecordStore>(store).BuildServiceProvider();
        var estimate = await AgentExperienceBenefit.EstimateAsync(services, Authorization, TestScope);

        Assert.True(estimate.Tracked);
        Assert.Equal(ExperienceStoreOutcome.Found, estimate.Outcome);
        Assert.Equal(new ExperienceBenefitArm(treatment, treatment), estimate.Overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(100 - treatment, 0), estimate.Overall.Holdout);
        Assert.Equal((0, 0, 0), (estimate.Overall.Unverified, estimate.Overall.Pending, estimate.Overall.MixedArm));
        Assert.Equal(1.0, estimate.Overall.Difference);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);

        // Every run's block named both lessons, so each lesson's comparison is over all 100 runs.
        Assert.Equal(lessons, estimate.Lessons.Select(lesson => lesson.ExperienceId));
        Assert.All(estimate.Lessons, lesson =>
        {
            Assert.Equal(estimate.Overall.Treatment, lesson.Comparison.Treatment);
            Assert.Equal(estimate.Overall.Holdout, lesson.Comparison.Holdout);
            Assert.Equal(100, lesson.Comparison.Treatment.Runs + lesson.Comparison.Holdout.Runs);
            Assert.Equal(ExperienceBenefitVerdict.Helps, lesson.Comparison.Verdict);
        });
    }

    [Fact]
    public async Task A_store_that_does_not_track_the_holdout_says_so_and_claims_nothing()
    {
        var world = new FakeExperienceWorld();
        var estimate = await AgentExperienceBenefit.EstimateAsync(Services(world), Authorization, TestScope);

        Assert.False(estimate.Tracked);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
        Assert.Equal(new ExperienceBenefitArm(0, 0), estimate.Overall.Treatment);
        Assert.Empty(estimate.Lessons);

        // A decorator that forwards the capability but not the read reads as not tracked too.
        world.TracksHoldout = true;
        Assert.False((await AgentExperienceBenefit.EstimateAsync(Services(world), Authorization, TestScope)).Tracked);
    }

    [Fact]
    public async Task A_scope_outside_the_authorization_is_an_empty_estimate_carrying_the_refusal()
    {
        var store = new InMemoryExperienceRecordStore(new FrozenTimeProvider(InjectionRecords.Now));
        var services = new ServiceCollection().AddSingleton<IExperienceRecordStore>(store).BuildServiceProvider();

        var estimate = await AgentExperienceBenefit.EstimateAsync(services, Authorization, TestScope with { TenantId = "tenant-2" });

        Assert.Equal(ExperienceStoreOutcome.Denied, estimate.Outcome);
        Assert.True(estimate.Tracked);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
    }

    [Fact]
    public async Task Every_page_is_read_after_the_last_row_of_the_one_before()
    {
        // 2,500 single-row runs, alternating arms; treatment runs verify, holdout runs fail.
        var rows = Enumerable.Range(0, 2_500)
            .Select(i => new HoldoutOutcomeRow(
                new Guid($"{i:D8}-0000-0000-0000-000000000000"),
                i % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout,
                InjectionRecords.Id(1),
                InjectionRecords.Now,
                i % 2 == 0 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed))
            .ToList();
        var queries = new List<HoldoutOutcomeQuery>();
        var world = new FakeExperienceWorld
        {
            TracksHoldout = true,
            OnQueryHoldout = (_, query, _) =>
            {
                queries.Add(query);
                var start = query.After is { } after ? rows.FindIndex(row => row.RunId == after.RunId) + 1 : 0;
                return Task.FromResult(new HoldoutOutcomeQueryResult(ExperienceStoreOutcome.Found, rows.Skip(start).Take(query.Limit).ToList(), []));
            },
        };

        var estimate = await AgentExperienceBenefit.EstimateAsync(Services(world), Authorization, TestScope);

        Assert.Equal(3, queries.Count);
        Assert.All(queries, query => Assert.Equal((TestScope, HoldoutOutcomeQuery.MaxLimit), (query.Scope, query.Limit)));
        Assert.Equal([null, new(rows[999].RunId, rows[999].ExperienceId), new(rows[1999].RunId, rows[1999].ExperienceId)], queries.Select(query => query.After));
        Assert.Equal(new ExperienceBenefitArm(1_250, 1_250), estimate.Overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(1_250, 0), estimate.Overall.Holdout);
    }

    [Fact]
    public async Task A_run_whose_rows_straddle_a_page_boundary_is_counted_once_in_its_one_arm()
    {
        // 999 single-row holdout runs fill the first page but one; the straddling treatment run's two rows are the first
        // page's last row and the second page's first.
        var rows = Enumerable.Range(0, 999)
            .Select(i => new HoldoutOutcomeRow(
                new Guid($"{i:D8}-0000-0000-0000-000000000000"), HoldoutArm.Holdout, InjectionRecords.Id(1), InjectionRecords.Now, TaskVerificationStatus.Failed))
            .ToList();
        var straddling = new Guid("99999999-0000-0000-0000-000000000000");
        rows.Add(new HoldoutOutcomeRow(straddling, HoldoutArm.Treatment, InjectionRecords.Id(1), InjectionRecords.Now, TaskVerificationStatus.Verified));
        rows.Add(new HoldoutOutcomeRow(straddling, HoldoutArm.Treatment, InjectionRecords.Id(2), InjectionRecords.Now, TaskVerificationStatus.Verified));
        var world = new FakeExperienceWorld
        {
            TracksHoldout = true,
            OnQueryHoldout = (_, query, _) =>
            {
                var start = query.After is { } after
                    ? rows.FindIndex(row => row.RunId == after.RunId && row.ExperienceId == after.ExperienceId) + 1
                    : 0;
                return Task.FromResult(new HoldoutOutcomeQueryResult(ExperienceStoreOutcome.Found, rows.Skip(start).Take(query.Limit).ToList(), []));
            },
        };

        var estimate = await AgentExperienceBenefit.EstimateAsync(Services(world), Authorization, TestScope);

        Assert.Equal(new ExperienceBenefitArm(1, 1), estimate.Overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(999, 0), estimate.Overall.Holdout);
        Assert.Equal(0, estimate.Overall.MixedArm);
        var second = Assert.Single(estimate.Lessons, lesson => lesson.ExperienceId == InjectionRecords.Id(2)).Comparison;
        Assert.Equal(new ExperienceBenefitArm(1, 1), second.Treatment);
    }

    [Fact]
    public async Task A_minimum_below_one_is_refused_before_anything_is_read()
    {
        var world = new FakeExperienceWorld { TracksHoldout = true, OnQueryHoldout = (_, _, _) => throw new InvalidOperationException("must not read") };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            AgentExperienceBenefit.EstimateAsync(Services(world), Authorization, TestScope, new ExperienceBenefitOptions { MinimumRunsPerArm = 0 }));
    }

    private static ServiceProvider Services(IExperienceRecordStore store) =>
        new ServiceCollection().AddSingleton(store).BuildServiceProvider();

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
}
