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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 25.8: <see cref="ExperienceHoldoutContextProvider"/> puts a fake memory provider under the holdout, and
/// <see cref="AgentExperienceBenefit.EstimateProviderAsync"/> measures it, end to end through capture and finalization.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class ProviderHoldoutTests
{
    private const string Key = "mem0";
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

    [Fact]
    public async Task At_the_default_rate_the_memory_is_passed_through_and_nothing_is_recorded()
    {
        var harness = new Harness(new ExperienceHoldoutOptions());

        await harness.RunAsync(Guid.NewGuid());
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal(2, harness.MemoryMessagesSeen());
        Assert.Empty((await harness.RowsAsync()));
        Assert.Equal((1, 1), (harness.Memory.Invoking, harness.Memory.Invoked));
    }

    [Fact]
    public async Task A_treatment_run_is_passed_through_and_recorded_as_treatment()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 0.5 });
        var runId = RunIn(HoldoutArm.Treatment, 0.5);

        await harness.RunAsync(runId);
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal(2, harness.MemoryMessagesSeen());
        var row = Assert.Single((await harness.RowsAsync()));
        Assert.Equal((runId, Key, HoldoutArm.Treatment, TestScope), (row.RunId, row.ProviderKey, row.Arm, row.Scope));
        Assert.Equal((1, 1), (harness.Memory.Invoking, harness.Memory.Invoked));
    }

    [Fact]
    public async Task A_held_out_run_gets_none_of_the_memory_yet_the_memory_still_runs_and_learns()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 0.5 });
        var runId = RunIn(HoldoutArm.Holdout, 0.5);

        var response = await harness.RunAsync(runId);
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(0, harness.MemoryMessagesSeen());
        Assert.Contains(harness.Client.LastMessages!, m => m.Text == "another refund ticket stuck on a lock");
        Assert.Equal((1, 1), (harness.Memory.Invoking, harness.Memory.Invoked));
        var row = Assert.Single((await harness.RowsAsync()));
        Assert.Equal((runId, HoldoutArm.Holdout), (row.RunId, row.Arm));
    }

    [Fact]
    public async Task A_held_out_run_loses_the_memory_s_tools_and_instructions_too()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, memory: new FakeMemoryProvider(0, tool: true, instructions: "Remember the user prefers terse answers."));

        await harness.RunAsync(Guid.NewGuid());
        await harness.Wrapper.RecordsSettledAsync();

        Assert.DoesNotContain(harness.Client.LastOptions?.Tools ?? [], tool => tool.Name == "recall");
        Assert.DoesNotContain("terse", harness.Client.LastOptions?.Instructions ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(HoldoutArm.Holdout, Assert.Single((await harness.RowsAsync())).Arm);
    }

    [Fact]
    public async Task An_empty_memory_answer_is_passed_through_and_not_recorded()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, memory: new FakeMemoryProvider(0));

        await harness.RunAsync(Guid.NewGuid());
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Empty((await harness.RowsAsync()));
        Assert.Equal((1, 1), (harness.Memory.Invoking, harness.Memory.Invoked));
    }

    [Fact]
    public async Task An_uncaptured_run_is_passed_through_and_not_recorded()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 });

        await harness.RunUncapturedAsync();
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal(2, harness.MemoryMessagesSeen());
        Assert.Empty((await harness.RowsAsync()));
    }

    [Fact]
    public async Task A_run_with_no_authorization_to_record_under_does_not_take_part()
    {
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, authorize: _ => throw new InvalidOperationException("no identity"));

        await harness.RunAsync(Guid.NewGuid());
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal(2, harness.MemoryMessagesSeen());
        Assert.Empty((await harness.RowsAsync()));
    }

    [Fact]
    public async Task A_memory_that_throws_throws_the_same_exception_and_nothing_is_recorded()
    {
        var thrown = new InvalidOperationException("the memory service is down");
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, memory: new FakeMemoryProvider(2, throws: thrown));

        var caught = await Assert.ThrowsAnyAsync<Exception>(() => harness.RunAsync(Guid.NewGuid()));
        await harness.Wrapper.RecordsSettledAsync();

        var bare = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [new FakeMemoryProvider(2, throws: thrown)] });
        var unwrapped = await Assert.ThrowsAnyAsync<Exception>(() => bare.RunAsync("another refund ticket stuck on a lock"));
        Assert.Same(thrown, caught);
        Assert.Same(thrown, unwrapped);
        Assert.Empty((await harness.RowsAsync()));
    }

    [Theory]
    [InlineData("throws", "NotRecorded")]
    [InlineData("denied", "NotRecorded")]
    [InlineData("not tracked", "NotTracked")]
    public async Task A_store_that_cannot_record_changes_nothing_about_the_invocation(string failure, string outcome)
    {
        using var probe = TelemetryProbe.Start();
        var world = new FakeExperienceWorld
        {
            TracksProviderHoldout = failure != "not tracked",
            OnRecordProviderHoldout = (_, _, _, _) => failure == "throws"
                ? throw new ExperienceStoreException("the provider holdout table is not granted")
                : Task.FromResult(new ExperienceHoldoutRecordResult(ExperienceStoreOutcome.Denied, [])),
        };
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, store: world);

        var response = await harness.RunAsync(Guid.NewGuid());
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(0, harness.MemoryMessagesSeen());
        Assert.Equal(
            [outcome],
            probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.RecordProviderHoldout).Select(m => (string)m.Tags["outcome"]!));
    }

    /// <summary>
    /// A sequence the wrapper reads after the memory answered and that throws (here, on its first enumeration only) never
    /// fails the invocation: the memory's answer goes on unchanged, as without the wrapper, and nothing is recorded.
    /// </summary>
    [Fact]
    public async Task A_memory_answer_that_throws_when_read_is_passed_through_unchanged_and_not_recorded()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness(new ExperienceHoldoutOptions { Rate = 1 }, memory: new FakeMemoryProvider(2, throwOnFirstRead: true));

        var response = await harness.RunAsync(Guid.NewGuid());
        await harness.Wrapper.RecordsSettledAsync();

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(2, harness.MemoryMessagesSeen());
        Assert.Empty(await harness.RowsAsync());
        Assert.Equal(
            ["NotRecorded"],
            probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.RecordProviderHoldout).Select(m => (string)m.Tags["outcome"]!));
    }

    [Fact]
    public async Task The_context_passed_in_is_never_changed()
    {
        var wrapper = new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), Key, new ExperienceHoldoutOptions { Rate = 1 }, new FakeExperienceWorld());
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") }.Select(m => m);
        var tools = new List<AITool> { AIFunctionFactory.Create(() => "x", "existing") }.AsReadOnly();
        var given = new AIContext { Messages = messages, Tools = tools, Instructions = "be brief" };
        var agent = new ChatClientAgent(new RecordingChatClient());

#pragma warning disable MAAI001 // The invoking context's constructor is MAF's evaluation API: built by hand only to call the wrapper directly.
        await wrapper.InvokingAsync(new AIContextProvider.InvokingContext(agent, null, given));
#pragma warning restore MAAI001

        Assert.Same(messages, given.Messages);
        Assert.Same(tools, given.Tools);
        Assert.Equal("be brief", given.Instructions);
    }

    [Fact]
    public async Task The_wrapper_forwards_the_memory_s_state_keys_and_services()
    {
        var memory = new FakeMemoryProvider(2);
        var wrapper = new ExperienceHoldoutContextProvider(memory, Key, new ExperienceHoldoutOptions(), new FakeExperienceWorld());

        Assert.Equal(memory.StateKeys, wrapper.StateKeys);
        Assert.Same(memory, wrapper.GetService(typeof(FakeMemoryProvider)));
        Assert.Same(wrapper, wrapper.GetService(typeof(ExperienceHoldoutContextProvider)));
        Assert.Same(memory, wrapper.InnerProvider);
        Assert.Equal(Key, wrapper.ProviderKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("mem0/v2")]
    [InlineData("mémoire")]
    public void A_malformed_key_is_refused(string key)
    {
        Assert.Throws<ArgumentException>(() => new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), key, new ExperienceHoldoutOptions(), new FakeExperienceWorld()));
        Assert.Throws<ArgumentException>(() => new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), new string('k', 101), new ExperienceHoldoutOptions(), new FakeExperienceWorld()));
        Assert.NotNull(new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), new string('k', 100), new ExperienceHoldoutOptions(), new FakeExperienceWorld()));
        Assert.NotNull(new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), "Zep_v2.1-eu", new ExperienceHoldoutOptions(), new FakeExperienceWorld()));
    }

    [Fact]
    public void A_bad_rate_or_salt_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), Key, new ExperienceHoldoutOptions { Rate = 1.5 }, new FakeExperienceWorld()));
        Assert.Throws<ArgumentException>(() => new ExperienceHoldoutContextProvider(new FakeMemoryProvider(2), Key, new ExperienceHoldoutOptions { Rate = 0.5, Salt = string.Empty }, new FakeExperienceWorld()));
    }

    /// <summary>
    /// The lessons' holdout and a measured provider's on one run draw their arms independently (different salts) and
    /// write separate rows.
    /// </summary>
    [Fact]
    public async Task The_lessons_and_a_measured_provider_on_one_run_draw_independent_arms_and_separate_rows()
    {
        var clock = new FrozenTimeProvider(InjectionRecords.Now);
        var store = new InMemoryExperienceRecordStore(clock);
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await store.CreateAsync(Authorization, InjectionRecords.Record(InjectionRecords.Id(1), TestScope, lesson: "Check the lock table first."), CancellationToken.None)).Outcome);

        var holdout = new ExperienceHoldoutOptions { Rate = 0.5 };
        var lessons = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                TimeProvider = clock,
                Holdout = holdout,
            });
        var harness = new Harness(holdout, store: store, lessons: lessons);

        // Runs whose two draws disagree, both ways: one arm is not the other's.
        var lessonsOnly = Runs().First(run => Arm(run, holdout.Salt, 0.5) == HoldoutArm.Holdout && Arm(run, ExperienceHoldoutContextProvider.ProviderSalt(Key, holdout.Salt), 0.5) == HoldoutArm.Treatment);
        var memoryOnly = Runs().First(run => Arm(run, holdout.Salt, 0.5) == HoldoutArm.Treatment && Arm(run, ExperienceHoldoutContextProvider.ProviderSalt(Key, holdout.Salt), 0.5) == HoldoutArm.Holdout);
        foreach (var run in new[] { lessonsOnly, memoryOnly })
        {
            await harness.RunAsync(run);
        }

        await harness.Wrapper.RecordsSettledAsync();
        await lessons.InjectionRecordsSettledAsync();

        var lessonRows = (await store.QueryHoldoutOutcomesAsync(Authorization, new HoldoutOutcomeQuery(TestScope), CancellationToken.None)).Rows;
        Assert.Equal([(lessonsOnly, HoldoutArm.Holdout), (memoryOnly, HoldoutArm.Treatment)], lessonRows.OrderBy(r => r.RunId == memoryOnly).Select(r => (r.RunId, r.Arm)));
        var memoryRows = await harness.RowsAsync();
        Assert.Equal([(lessonsOnly, HoldoutArm.Treatment), (memoryOnly, HoldoutArm.Holdout)], memoryRows.OrderBy(r => r.RunId == memoryOnly).Select(r => (r.RunId, r.Arm)));

        // Over many runs the two draws agree about half the time: independent, not the same draw.
        var agree = Runs().Take(2_000).Count(run => Arm(run, holdout.Salt, 0.5) == Arm(run, ExperienceHoldoutContextProvider.ProviderSalt(Key, holdout.Salt), 0.5));
        Assert.InRange(agree, 850, 1_150);
    }

    /// <summary>
    /// Acceptance: a fake memory provider wrapped at rate 0.5, 100 finalized runs where treatment verifies and holdout
    /// fails, estimated as <see cref="ExperienceBenefitVerdict.Helps"/>.
    /// </summary>
    [Fact]
    public async Task One_hundred_finalized_runs_at_half_where_treatment_verifies_and_holdout_fails_help()
    {
        var clock = new FrozenTimeProvider(InjectionRecords.Now);
        var store = new InMemoryExperienceRecordStore(clock);
        var holdout = new ExperienceHoldoutOptions { Rate = 0.5 };
        var salt = ExperienceHoldoutContextProvider.ProviderSalt(Key, holdout.Salt);
        var harness = new Harness(holdout, store: store, finalize: run => Arm(run, salt, 0.5) == HoldoutArm.Treatment);

        var runs = Enumerable.Range(0, 100).Select(i => new Guid($"5a5a5a5a-0000-4000-8000-{i:D12}")).ToList();
        foreach (var run in runs)
        {
            await harness.RunAsync(run);
        }

        await harness.Wrapper.RecordsSettledAsync();
        Assert.Equal(100, harness.Finalized.Count);
        Assert.All(harness.Finalized, result => Assert.True(result.IsDurable));

        var treatment = runs.Count(run => Arm(run, salt, 0.5) == HoldoutArm.Treatment);
        Assert.InRange(treatment, 20, 80);

        var services = new ServiceCollection().AddSingleton<IExperienceRecordStore>(store).BuildServiceProvider();
        var estimate = await AgentExperienceBenefit.EstimateProviderAsync(services, Authorization, TestScope, Key);

        Assert.True(estimate.Tracked);
        Assert.Equal((Key, ExperienceStoreOutcome.Found), (estimate.ProviderKey, estimate.Outcome));
        Assert.Equal(new ExperienceBenefitArm(treatment, treatment), estimate.Overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(100 - treatment, 0), estimate.Overall.Holdout);
        Assert.Equal((0, 0, 0), (estimate.Overall.Unverified, estimate.Overall.Pending, estimate.Overall.MixedArm));
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);

        // Another key measures nothing here.
        var other = await AgentExperienceBenefit.EstimateProviderAsync(services, Authorization, TestScope, "zep");
        Assert.Equal(new ExperienceBenefitArm(0, 0), other.Overall.Treatment);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, other.Overall.Verdict);
    }

    /// <summary>Two tenants recording the same run ID and key each keep their row, and each estimate counts only its own.</summary>
    [Fact]
    public async Task Two_tenants_with_the_same_run_and_key_each_estimate_only_their_own()
    {
        var store = new InMemoryExperienceRecordStore(new FrozenTimeProvider(InjectionRecords.Now));
        var runId = Guid.NewGuid();
        var other = new Scope("tenant-2", "app-1", "project-1");
        var otherAuthorization = Authorization with { TenantId = "tenant-2" };
        foreach (var (auth, scope, arm) in new[] { (Authorization, TestScope, HoldoutArm.Treatment), (otherAuthorization, other, HoldoutArm.Holdout) })
        {
            var recorded = await store.RecordProviderHoldoutAssignmentAsync(auth, scope, new ProviderHoldoutAssignment(runId, Key, arm, InjectionRecords.Now), CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, recorded.Outcome);
        }

        var mine = await AgentExperienceBenefit.EstimateProviderAsync(Services(store), Authorization, TestScope, Key);
        var theirs = await AgentExperienceBenefit.EstimateProviderAsync(Services(store), otherAuthorization, other, Key);

        // Neither run is finalized: each estimate counts exactly one pending run, its own.
        Assert.Equal((1, 1), (mine.Overall.Pending, theirs.Overall.Pending));
        var mineRow = Assert.Single((await store.QueryProviderHoldoutOutcomesAsync(Authorization, new ProviderHoldoutOutcomeQuery(TestScope, Key), CancellationToken.None)).Rows);
        var theirRow = Assert.Single((await store.QueryProviderHoldoutOutcomesAsync(otherAuthorization, new ProviderHoldoutOutcomeQuery(other, Key), CancellationToken.None)).Rows);
        Assert.Equal((HoldoutArm.Treatment, HoldoutArm.Holdout), (mineRow.Arm, theirRow.Arm));
    }

    [Fact]
    public async Task A_store_that_does_not_track_provider_holdouts_says_so_and_claims_nothing()
    {
        var world = new FakeExperienceWorld();
        var estimate = await AgentExperienceBenefit.EstimateProviderAsync(Services(world), Authorization, TestScope, Key);

        Assert.False(estimate.Tracked);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
        Assert.Equal(new ExperienceBenefitArm(0, 0), estimate.Overall.Treatment);

        // A decorator that forwards the capability but not the read reads as not tracked too.
        world.TracksProviderHoldout = true;
        Assert.False((await AgentExperienceBenefit.EstimateProviderAsync(Services(world), Authorization, TestScope, Key)).Tracked);
    }

    [Fact]
    public async Task A_scope_outside_the_authorization_is_an_empty_estimate_carrying_the_refusal()
    {
        var store = new InMemoryExperienceRecordStore(new FrozenTimeProvider(InjectionRecords.Now));

        var estimate = await AgentExperienceBenefit.EstimateProviderAsync(Services(store), Authorization, TestScope with { TenantId = "tenant-2" }, Key);

        Assert.Equal(ExperienceStoreOutcome.Denied, estimate.Outcome);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
    }

    [Fact]
    public async Task Every_page_is_read_after_the_last_run_of_the_one_before_with_pending_and_unverified_runs_counted_apart()
    {
        var rows = Enumerable.Range(0, 2_500)
            .Select(i => new ProviderHoldoutOutcomeRow(
                new Guid($"{i:D8}-0000-0000-0000-000000000000"),
                Key,
                i % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout,
                InjectionRecords.Now,
                i % 10 == 9 ? null : i % 10 == 8 ? TaskVerificationStatus.Unknown : i % 2 == 0 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed))
            .ToList();
        var queries = new List<ProviderHoldoutOutcomeQuery>();
        var world = new FakeExperienceWorld
        {
            TracksProviderHoldout = true,
            OnQueryProviderHoldout = (_, query, _) =>
            {
                queries.Add(query);
                var start = query.AfterRunId is { } after ? rows.FindIndex(row => row.RunId == after) + 1 : 0;
                return Task.FromResult(new ProviderHoldoutOutcomeQueryResult(ExperienceStoreOutcome.Found, rows.Skip(start).Take(query.Limit).ToList(), []));
            },
        };

        var estimate = await AgentExperienceBenefit.EstimateProviderAsync(Services(world), Authorization, TestScope, Key);

        Assert.Equal(3, queries.Count);
        Assert.All(queries, query => Assert.Equal((TestScope, Key, ProviderHoldoutOutcomeQuery.MaxLimit), (query.Scope, query.ProviderKey, query.Limit)));
        Assert.Equal([null, rows[999].RunId, rows[1999].RunId], queries.Select(query => query.AfterRunId));
        Assert.Equal((250, 250), (estimate.Overall.Pending, estimate.Overall.Unverified));
        Assert.Equal(new ExperienceBenefitArm(1_000, 1_000), estimate.Overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(1_000, 0), estimate.Overall.Holdout);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.Overall.Verdict);
    }

    [Fact]
    public async Task A_malformed_key_or_minimum_is_refused_before_anything_is_read()
    {
        var world = new FakeExperienceWorld { TracksProviderHoldout = true, OnQueryProviderHoldout = (_, _, _) => throw new InvalidOperationException("must not read") };

        await Assert.ThrowsAsync<ArgumentException>(() => AgentExperienceBenefit.EstimateProviderAsync(Services(world), Authorization, TestScope, "a b"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            AgentExperienceBenefit.EstimateProviderAsync(Services(world), Authorization, TestScope, Key, new ExperienceBenefitOptions { MinimumRunsPerArm = 0 }));
    }

    /// <summary>The one-call setup: the wrapper records under the identity <c>UseAgentExperience</c> resolved, in its scope.</summary>
    [Fact]
    public async Task The_one_call_setup_records_under_the_resolved_identity()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment(Environments.Development));
        collection
            .AddAgentExperience(options =>
            {
                options.UseDevelopmentIdentity();
                options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(new ExperienceIdentity(Authorization, TestScope));
            })
            .UseInMemoryStorageForDevelopment();
        using var services = collection.BuildServiceProvider();

        var memory = new FakeMemoryProvider(2);
        var client = new RecordingChatClient();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "triage-bot",
            AIContextProviders = [services.UseMeasuredMemory(memory, Key, new ExperienceHoldoutOptions { Rate = 1 })],
        }).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync("another refund ticket stuck on a lock");
        var wrapper = Assert.IsType<ExperienceHoldoutContextProvider>(
            Assert.Single(agent.GetService<ChatClientAgent>()!.AIContextProviders!, provider => provider is ExperienceHoldoutContextProvider));
        await wrapper.RecordsSettledAsync();

        Assert.DoesNotContain(client.LastMessages!, m => m.Text.StartsWith("Memory ", StringComparison.Ordinal));
        var store = services.GetRequiredService<IExperienceRecordStore>();
        var page = await store.QueryProviderHoldoutOutcomesAsync(Authorization, new ProviderHoldoutOutcomeQuery(TestScope, Key), CancellationToken.None);
        Assert.Equal(HoldoutArm.Holdout, Assert.Single(page.Rows).Arm);

        // Without a holdout of its own it takes the injection holdout's, off by default.
        var defaulted = services.UseMeasuredMemory(memory, Key);
        Assert.NotNull(defaulted);
        Assert.Throws<ArgumentException>(() => services.UseMeasuredMemory(memory, "a b"));
    }

    private static HoldoutArm Arm(Guid run, string salt, double rate) => HoldoutAssignmentFunction.Assign(run, salt, rate);

    /// <summary>A run in <paramref name="arm"/> of the wrapper's holdout at the default salt and <paramref name="rate"/>.</summary>
    private static Guid RunIn(HoldoutArm arm, double rate) =>
        Runs().First(run => Arm(run, ExperienceHoldoutContextProvider.ProviderSalt(Key, ExperienceHoldoutOptions.DefaultSalt), rate) == arm);

    private static IEnumerable<Guid> Runs() =>
        Enumerable.Range(1, int.MaxValue - 1).Select(i => new Guid($"00000000-0000-4000-8000-{i:D12}"));

    private static ServiceProvider Services(IExperienceRecordStore store) =>
        new ServiceCollection().AddSingleton(store).BuildServiceProvider();

    /// <summary>A memory provider that adds a fixed answer, and counts how often MAF invokes it.</summary>
    private sealed class FakeMemoryProvider(int messages, Exception? throws = null, bool tool = false, string? instructions = null, bool throwOnFirstRead = false) : AIContextProvider
    {
        private int _invoking;
        private int _invoked;

        public int Invoking => Volatile.Read(ref _invoking);

        public int Invoked => Volatile.Read(ref _invoked);

        public override IReadOnlyList<string> StateKeys { get; } = ["FakeMemory.State"];

        protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            var merged = await base.InvokingCoreAsync(context, cancellationToken);
            if (throwOnFirstRead)
            {
                merged.Messages = new ThrowOnFirstRead<ChatMessage>([.. merged.Messages ?? []]);
            }

            return merged;
        }

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoking);
            if (throws is not null)
            {
                throw throws;
            }

            return ValueTask.FromResult(new AIContext
            {
                Messages = [.. Enumerable.Range(0, messages).Select(i => new ChatMessage(ChatRole.User, $"Memory {i}: the customer was refunded twice before."))],
                Tools = tool ? [AIFunctionFactory.Create(() => "nothing", "recall")] : null,
                Instructions = instructions,
            });
        }

        protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoked);
            return default;
        }
    }

    /// <summary>A sequence whose first enumeration throws and every later one reads the items.</summary>
    private sealed class ThrowOnFirstRead<T>(IReadOnlyList<T> items) : IEnumerable<T>
    {
        private int _reads;

        public IEnumerator<T> GetEnumerator() =>
            Interlocked.Increment(ref _reads) == 1 ? throw new InvalidOperationException("first read") : items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Harness
    {
        private readonly RecordingChatClient _uncapturedClient = new();
        private readonly AIAgent _agent;
        private readonly AIAgent _uncaptured;
        private RecordingChatClient _last;
        private Guid _nextRun;

        public Harness(
            ExperienceHoldoutOptions holdout,
            FakeMemoryProvider? memory = null,
            IExperienceRecordStore? store = null,
            Func<Scope, AuthorizationContext?>? authorize = null,
            ExperienceContextProvider? lessons = null,
            Func<Guid, bool>? finalize = null)
        {
            var clock = new FrozenTimeProvider(InjectionRecords.Now);
            Store = store ?? new InMemoryExperienceRecordStore(clock);
            Memory = memory ?? new FakeMemoryProvider(2);
            Wrapper = new ExperienceHoldoutContextProvider(Memory, Key, holdout, Store, authorize ?? (_ => Authorization), clock);
            _last = Client;

            var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000));
            ExperienceFinalizationService? finalization = null;
            if (finalize is not null)
            {
                var lifecycle = new ExperienceLifecycleService(Store, indexingService: null, new ExperienceIndependenceOptions(), capture);
                finalization = new ExperienceFinalizationService(capture, new DefaultExperienceReflector(), Store, lifecycle);
            }

            AIContextProvider[] providers = lessons is null ? [Wrapper] : [lessons, Wrapper];
            _agent = new ChatClientAgent(Client, new ChatClientAgentOptions { AIContextProviders = providers })
                .AsBuilder()
                .UseExperienceCapture(capture, new ExperienceCaptureOptions
                {
                    ResolveRun = _ => new ExperienceRunDescriptor("nightly-batch", TestScope, "Process the nightly batch", ContinuesRunId: _nextRun),
                    CaptureToolCalls = false,
                    FinalizationService = finalization,
                    ResolveFinalization = finalize is null ? null : context => Request(context.Run.RunId, finalize(context.Run.RunId)),
                    OnRunFinalized = Finalized.Add,
                })
                .Build();
            _uncaptured = new ChatClientAgent(_uncapturedClient, new ChatClientAgentOptions { AIContextProviders = [Wrapper] });
        }

        public RecordingChatClient Client { get; } = new();

        public IExperienceRecordStore Store { get; }

        public FakeMemoryProvider Memory { get; }

        public ExperienceHoldoutContextProvider Wrapper { get; }

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public Task<AgentResponse> RunAsync(Guid runId)
        {
            _nextRun = runId;
            _last = Client;
            return _agent.RunAsync("another refund ticket stuck on a lock");
        }

        public Task<AgentResponse> RunUncapturedAsync()
        {
            _last = _uncapturedClient;
            return _uncaptured.RunAsync("another refund ticket stuck on a lock");
        }

        /// <summary>How many of the memory's messages the model was last given.</summary>
        public int MemoryMessagesSeen() => _last.LastMessages?.Count(m => m.Text.StartsWith("Memory ", StringComparison.Ordinal)) ?? 0;

        /// <summary>Every provider holdout row in the in-memory store.</summary>
        public async Task<IReadOnlyList<(Guid RunId, string ProviderKey, HoldoutArm Arm, Scope Scope)>> RowsAsync()
        {
            if (Store is not InMemoryExperienceRecordStore memory)
            {
                return [];
            }

            var page = await memory.QueryProviderHoldoutOutcomesAsync(
                Authorization, new ProviderHoldoutOutcomeQuery(TestScope, Key, ProviderHoldoutOutcomeQuery.MaxLimit), CancellationToken.None);
            return [.. page.Rows.Select(row => (row.RunId, row.ProviderKey, row.Arm, TestScope))];
        }
    }

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
