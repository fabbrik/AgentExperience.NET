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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 25.4: capture records the model each run ran on, the holdout read carries the run's and the lesson's models, the
/// estimate splits by them, and with <see cref="ExperienceBenefitPolicy.PerModel"/> each agent acts on its own model's
/// verdicts, falling back to the pooled ones where its model has no evidence.
/// </summary>
public sealed class PerModelBenefitTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read", "experience:write"], DateTimeOffset.UnixEpoch);
    private static readonly Guid Lesson = InjectionRecords.Id(1);
    private static readonly Guid Other = InjectionRecords.Id(2);

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

    // ---- Capture ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Capture_records_the_model_the_chat_client_reports()
    {
        var (capture, agent) = Captured(new ModelChatClient("gpt-x"));

        await agent.RunAsync("refund ticket");

        Assert.Equal("gpt-x", SingleRun(capture).Environment.Metadata[HoldoutOutcomeRow.ModelMetadataKey]);
        Assert.Equal(Environment.MachineName, SingleRun(capture).Environment.HostName);
        Assert.True(SingleRun(capture).Environment.Metadata.ContainsKey("ProcessArchitecture"));
    }

    [Fact]
    public async Task The_host_s_own_model_entry_wins()
    {
        var (capture, agent) = Captured(
            new ModelChatClient("gpt-x"),
            environment: new EnvironmentFingerprint("h", "r", "o", null, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HoldoutOutcomeRow.ModelMetadataKey] = "host-model",
            }));

        await agent.RunAsync("refund ticket");

        Assert.Equal("host-model", SingleRun(capture).Environment.Metadata[HoldoutOutcomeRow.ModelMetadataKey]);
    }

    [Fact]
    public async Task A_client_without_metadata_records_no_model()
    {
        var (capture, agent) = Captured(new RecordingChatClient());

        await agent.RunAsync("refund ticket");

        Assert.False(SingleRun(capture).Environment.Metadata.ContainsKey(HoldoutOutcomeRow.ModelMetadataKey));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task A_blank_model_id_records_no_model(string modelId)
    {
        var (capture, agent) = Captured(new ModelChatClient(modelId));

        await agent.RunAsync("refund ticket");

        Assert.False(SingleRun(capture).Environment.Metadata.ContainsKey(HoldoutOutcomeRow.ModelMetadataKey));
    }

    [Fact]
    public async Task A_model_id_is_trimmed_and_capped()
    {
        var (capture, agent) = Captured(new ModelChatClient("  " + new string('m', 300) + "  "));

        await agent.RunAsync("refund ticket");

        Assert.Equal(new string('m', AgentModel.MaxLength), SingleRun(capture).Environment.Metadata[HoldoutOutcomeRow.ModelMetadataKey]);
    }

    [Fact]
    public void A_cap_never_splits_a_surrogate_pair()
    {
        var id = new string('m', AgentModel.MaxLength - 1) + "\U0001F600" + "tail";
        Assert.Equal(new string('m', AgentModel.MaxLength - 1), AgentModel.Normalize(id));
    }

    [Fact]
    public void A_lesson_learned_on_another_model_renders_exactly_as_one_with_no_model()
    {
        var plain = InjectionRecords.Record(Lesson, TestScope);
        plain = plain with
        {
            Environment = plain.Environment with { Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["zone"] = "b" } },
        };
        var onA = plain with
        {
            Environment = plain.Environment with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["zone"] = "b", [HoldoutOutcomeRow.ModelMetadataKey] = "model-a" },
            },
        };

        var withModel = HistoricalReferenceWriter.Write([new RankedExperience(onA, 0.5d, [])], ExperienceInjectionLimits.Default);
        var without = HistoricalReferenceWriter.Write([new RankedExperience(plain, 0.5d, [])], ExperienceInjectionLimits.Default);

        Assert.Equal(without.Text, withModel.Text);
        Assert.Contains("zone b", withModel.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("model-a", withModel.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(HoldoutOutcomeRow.ModelMetadataKey, withModel.Text, StringComparison.Ordinal);
    }

    // ---- Policy -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Per_model_is_off_by_default()
    {
        Assert.False(new ExperienceBenefitPolicy().PerModel);
        Assert.False(new ExperienceBenefitPolicy().EstimatorOptions.SplitByModel);
        Assert.True(new ExperienceBenefitPolicy { PerModel = true }.EstimatorOptions.SplitByModel);
    }

    [Fact]
    public void A_view_for_a_model_uses_that_model_s_verdicts_and_falls_back_to_the_pooled_ones()
    {
        // The lesson: helps model A's runs, hurts model B's; pooled it cancels out. Model C has too few runs to say.
        var rows = new List<HoldoutOutcomeRow>();
        rows.AddRange(Runs("model-a", Lesson, 25, TaskVerificationStatus.Verified, 25, TaskVerificationStatus.Failed, offset: 0));
        rows.AddRange(Runs("model-b", Lesson, 25, TaskVerificationStatus.Failed, 25, TaskVerificationStatus.Verified, offset: 1_000));
        rows.AddRange(Runs("model-c", Lesson, 2, TaskVerificationStatus.Verified, 2, TaskVerificationStatus.Failed, offset: 2_000));

        // The other lesson hurts pooled (model A's runs) and has no evidence for model C, and none at all for model B.
        rows.AddRange(Runs("model-a", Other, 25, TaskVerificationStatus.Failed, 25, TaskVerificationStatus.Verified, offset: 3_000));
        rows.AddRange(Runs("model-c", Other, 1, TaskVerificationStatus.Verified, 1, TaskVerificationStatus.Failed, offset: 4_000));

        // A third lesson helps A and B, so no model's runs, and not the pooled ones, hurt overall.
        var third = InjectionRecords.Id(3);
        rows.AddRange(Runs("model-a", third, 25, TaskVerificationStatus.Verified, 25, TaskVerificationStatus.Failed, offset: 5_000));
        rows.AddRange(Runs("model-b", third, 25, TaskVerificationStatus.Verified, 25, TaskVerificationStatus.Failed, offset: 6_000));

        var estimate = ExperienceBenefitEstimator.Estimate(rows, new ExperienceBenefitOptions { SplitByModel = true });
        var views = BenefitViews.From(estimate)!;
        Assert.All(estimate.ByConsumingModel!, group => Assert.NotEqual(ExperienceBenefitVerdict.Hurts, group.Comparison.Verdict));
        Assert.NotEqual(ExperienceBenefitVerdict.Hurts, estimate.Overall.Verdict);

        // Pooled: the lesson says nothing, the other lesson hurts.
        var pooled = views.For(null)!;
        Assert.False(pooled.ScopeHurts);
        Assert.False(pooled.Harmful(Lesson));
        Assert.True(pooled.Harmful(Other));

        // Model A: the lesson helps; the other lesson hurts (A's own verdict).
        var a = views.For("model-a")!;
        Assert.Contains(Lesson, a.Helps);
        Assert.True(a.Harmful(Other));

        // Model B: the lesson hurts; nothing of B's on the other lesson, so the pooled verdict (hurts) stands.
        var b = views.For("model-b")!;
        Assert.True(b.Harmful(Lesson));
        Assert.True(b.Harmful(Other));

        // Model C: no evidence of its own anywhere: the pooled view.
        var c = views.For("model-c")!;
        Assert.False(c.Harmful(Lesson));
        Assert.True(c.Harmful(Other));

        // A model never measured reads the pooled view.
        Assert.Same(pooled, views.For("model-z"));
    }

    [Fact]
    public void A_model_s_scope_verdict_replaces_the_pooled_one_only_when_it_has_evidence()
    {
        // Every run names both lessons. Model B's runs fail when given them; model A's verify only when given them.
        var rows = new List<HoldoutOutcomeRow>();
        foreach (var lesson in new[] { Lesson, Other })
        {
            rows.AddRange(Runs("model-b", lesson, 25, TaskVerificationStatus.Failed, 25, TaskVerificationStatus.Verified, offset: 0));
            rows.AddRange(Runs("model-a", lesson, 25, TaskVerificationStatus.Verified, 25, TaskVerificationStatus.Failed, offset: 1_000));
        }

        var estimate = ExperienceBenefitEstimator.Estimate(rows, new ExperienceBenefitOptions { SplitByModel = true });
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimate.Overall.Verdict);
        var views = BenefitViews.From(estimate)!;

        Assert.True(views.For("model-b")!.ScopeHurts);
        Assert.False(views.For("model-a")?.ScopeHurts ?? false);
    }

    /// <summary>
    /// End to end: two agents on two models (fake chat clients reporting different model ids) share one store and one
    /// lesson learned by model A. Under the holdout, model A's runs verify only when given the lesson and model B's only
    /// when it is withheld. Split, the estimate says Helps for A and Hurts for B; with <see cref="ExperienceBenefitPolicy.PerModel"/>
    /// the next invocation on B omits the lesson, while A's keeps it.
    /// </summary>
    [Fact]
    public async Task Two_models_sharing_one_lesson_each_act_on_their_own_verdict()
    {
        var clock = new ManualClock(InjectionRecords.Now);
        var store = new InMemoryExperienceRecordStore(clock);
        var lesson = InjectionRecords.Record(Lesson, TestScope, lesson: "Check the lock table first.");
        lesson = lesson with
        {
            Environment = lesson.Environment with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [HoldoutOutcomeRow.ModelMetadataKey] = "model-a" },
            },
        };
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorization, lesson, CancellationToken.None)).Outcome);

        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000));
        var lifecycle = new ExperienceLifecycleService(store, indexingService: null, new ExperienceIndependenceOptions(), capture);
        var finalization = new ExperienceFinalizationService(capture, new DefaultExperienceReflector(), store, lifecycle);
        var holdout = new ExperienceHoldoutOptions { Rate = 0.5 };
        var policy = new ExperienceBenefitPolicy { Enabled = true, PerModel = true, MinimumRunsPerArm = 10 };
        var injections = new List<ExperienceInjectionResult>();
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                TimeProvider = clock,
                Holdout = holdout,
                Benefit = policy,
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
        AIAgent Agent(string model, bool helped) =>
            new ChatClientAgent(new ModelChatClient(model), new ChatClientAgentOptions { AIContextProviders = [provider] })
                .AsBuilder()
                .UseExperienceCapture(capture, new ExperienceCaptureOptions
                {
                    ResolveRun = _ => new ExperienceRunDescriptor("nightly-batch", TestScope, "Process the nightly batch", ContinuesRunId: next),
                    CaptureToolCalls = false,
                    FinalizationService = finalization,
                    ResolveFinalization = context => Request(
                        context.Run.RunId,
                        pass: (HoldoutAssignmentFunction.Assign(context.Run.RunId, holdout.Salt, holdout.Rate) == HoldoutArm.Treatment) == helped),
                    OnRunFinalized = finalized.Add,
                })
                .Build();

        var onA = Agent("model-a", helped: true);
        var onB = Agent("model-b", helped: false);
        for (var i = 0; i < 80; i++)
        {
            next = new Guid($"5a5a5a5a-0000-4000-8000-{i:D12}");
            await onA.RunAsync("another refund ticket stuck on a lock");
            next = new Guid($"5b5b5b5b-0000-4000-8000-{i:D12}");
            await onB.RunAsync("another refund ticket stuck on a lock");
        }

        await provider.InjectionRecordsSettledAsync();
        await provider.BenefitRefreshesSettledAsync();
        Assert.Equal(160, finalized.Count);

        // The store's rows carry both models, and the split estimate tells them apart.
        var services = new ServiceCollection().AddSingleton<IExperienceRecordStore>(store).BuildServiceProvider();
        var estimate = await AgentExperienceBenefit.EstimateAsync(
            services, Authorization, TestScope, new ExperienceBenefitOptions { MinimumRunsPerArm = 10, SplitByModel = true });
        Assert.Equal(["model-a", "model-b"], estimate.ByConsumingModel!.Select(group => group.Model));
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimate.ByConsumingModel![0].Comparison.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Hurts, estimate.ByConsumingModel![1].Comparison.Verdict);
        var measured = Assert.Single(estimate.Lessons, l => l.ExperienceId == Lesson);
        Assert.Equal(
            [("model-a", "model-a", ExperienceBenefitVerdict.Helps), ("model-a", "model-b", ExperienceBenefitVerdict.Hurts)],
            measured.ByModelPair!.Select(pair => (pair.LessonModel, pair.RunModel, pair.Comparison.Verdict)));

        // Unsplit, the same call gives no split at all.
        var pooled = await AgentExperienceBenefit.EstimateAsync(services, Authorization, TestScope, new ExperienceBenefitOptions { MinimumRunsPerArm = 10 });
        Assert.Null(pooled.ByConsumingModel);
        Assert.Equal(estimate.Overall, pooled.Overall);

        // Stale after the interval: one invocation starts the refresh, the next acts on it, per model.
        clock.Advance(TimeSpan.FromMinutes(11));
        var uncapturedA = new ChatClientAgent(new ModelChatClient("model-a"), new ChatClientAgentOptions { AIContextProviders = [provider] });
        var uncapturedB = new ChatClientAgent(new ModelChatClient("model-b"), new ChatClientAgentOptions { AIContextProviders = [provider] });
        await uncapturedA.RunAsync("another refund ticket stuck on a lock");
        await provider.BenefitRefreshesSettledAsync();

        await uncapturedB.RunAsync("another refund ticket stuck on a lock");
        var forB = injections[^1];
        Assert.DoesNotContain(Lesson, forB.InjectedExperienceIds);
        Assert.Equal(InjectionOmissionReason.MeasuredHarmful, Assert.Single(forB.Omitted, o => o.ExperienceId == Lesson).Reason);

        await uncapturedA.RunAsync("another refund ticket stuck on a lock");
        var forA = injections[^1];
        Assert.Contains(Lesson, forA.InjectedExperienceIds);
        Assert.DoesNotContain(forA.Omitted, o => o.Reason == InjectionOmissionReason.MeasuredHarmful);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private static (InMemoryExperienceCaptureService Capture, AIAgent Agent) Captured(IChatClient client, EnvironmentFingerprint? environment = null)
    {
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000));
        var options = new ExperienceCaptureOptions
        {
            ResolveRun = _ => new ExperienceRunDescriptor("triage", TestScope, "Triage the ticket"),
            CaptureToolCalls = false,
            ShouldCompleteRun = static _ => false,
            NewId = () => CapturedRunId,
        };
        if (environment is not null)
        {
            options.Environment = environment;
        }

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions()).AsBuilder().UseExperienceCapture(capture, options).Build();
        return (capture, agent);
    }

    private static readonly Guid CapturedRunId = Guid.Parse("c0c0c0c0-0000-4000-8000-000000000001");

    private static ExperienceRun SingleRun(InMemoryExperienceCaptureService capture)
    {
        Assert.True(capture.TryGetRun(CapturedRunId, out var run));
        return run;
    }

    private static IEnumerable<HoldoutOutcomeRow> Runs(
        string model,
        Guid lesson,
        int treatment,
        TaskVerificationStatus treatmentOutcome,
        int holdout,
        TaskVerificationStatus holdoutOutcome,
        int offset)
    {
        for (var i = 0; i < treatment + holdout; i++)
        {
            yield return new HoldoutOutcomeRow(
                new Guid($"{offset + i:D8}-0000-0000-0000-000000000000"),
                i < treatment ? HoldoutArm.Treatment : HoldoutArm.Holdout,
                lesson,
                InjectionRecords.Now,
                i < treatment ? treatmentOutcome : holdoutOutcome)
            {
                RunModel = model,
                LessonModel = "model-a",
            };
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

    /// <summary>A chat client that reports <see cref="ChatClientMetadata"/> with a model id, and answers every call.</summary>
    private sealed class ModelChatClient(string modelId) : IChatClient
    {
        private readonly ChatClientMetadata _metadata = new("fake", null, modelId);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : null;

        public void Dispose()
        {
        }
    }
}
