using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;
using Microsoft.Agents.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 28.5: the injection-time match-strength floor (<see cref="ExperienceInjectionOptions.MinimumMatch"/>). The fake
/// world's candidates report no term coverage, so retrieval's <see cref="RankedExperience.TextCoverage"/> is their
/// relevance, which each test sets.
/// </summary>
public class MatchFloorTests
{
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Off_by_default_a_weak_match_is_injected_as_before()
    {
        var harness = new Harness();
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 0.1d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Null(new ExperienceInjectionOptions().MinimumMatch);
        Assert.Equal(InjectionOutcome.Injected, harness.Last.Outcome);
        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        Assert.Empty(harness.Last.Omitted);
    }

    [Fact]
    public async Task A_weak_match_is_omitted_as_weak_match_with_a_content_free_detail_and_a_strong_one_injected()
    {
        var harness = new Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 } };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 0.9d);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope), relevance: 0.25d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        var omission = Assert.Single(harness.Last.Omitted);
        Assert.Equal(InjectionRecords.Id(2), omission.ExperienceId);
        Assert.Equal(InjectionOmissionReason.WeakMatch, omission.Reason);
        Assert.Equal(
            "Below the match floor: text coverage 0.25 is below 0.5, no semantic match, and no exact failure-signature match.",
            omission.Detail);
        Assert.DoesNotContain(InjectionRecords.Id(2).ToString("D"), harness.InjectedText(), StringComparison.Ordinal);
        Assert.DoesNotContain(InjectionRecords.Id(2), harness.World.Reads);
    }

    [Fact]
    public async Task A_weak_match_takes_no_slot_so_a_stronger_one_ranked_below_it_fills_the_limit()
    {
        // The weak record outranks the strong one on confidence and status, so with one slot and no floor it would take it.
        var harness = new Harness
        {
            MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.3 },
            Limits = ExperienceInjectionLimits.Default with { MaxRecords = 1 },
        };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope, status: ExperienceStatus.Reinforced, confidence: 1d), relevance: 0.29d);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope, confidence: 0.5d), relevance: 0.31d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(2)], harness.Last.InjectedExperienceIds);
        Assert.Equal(new[] { InjectionOmissionReason.WeakMatch }, harness.Last.Omitted.Select(o => o.Reason));

        var unfloored = new Harness { Limits = ExperienceInjectionLimits.Default with { MaxRecords = 1 } };
        unfloored.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope, status: ExperienceStatus.Reinforced, confidence: 1d), relevance: 0.29d);
        unfloored.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope, confidence: 0.5d), relevance: 0.31d);
        await unfloored.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.Equal([InjectionRecords.Id(1)], unfloored.Last.InjectedExperienceIds);
    }

    [Fact]
    public async Task A_block_whose_every_record_is_weak_injects_nothing()
    {
        var harness = new Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 } };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 0.2d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOutcome.NothingToInject, harness.Last.Outcome);
        Assert.Empty(harness.Last.InjectedExperienceIds);
        Assert.Equal(InjectionOmissionReason.WeakMatch, Assert.Single(harness.Last.Omitted).Reason);
        Assert.Null(harness.InjectedText());
    }

    [Fact]
    public async Task A_coverage_a_hair_below_the_floor_from_single_precision_still_passes()
    {
        var harness = new Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.3 } };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 0.3d - 5e-7);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
    }

    [Fact]
    public async Task The_floor_is_snapshotted_when_the_provider_is_built()
    {
        var options = new ExperienceInjectionOptions
        {
            ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
            TimeProvider = new FrozenTimeProvider(InjectionRecords.Now),
        };
        var results = new List<ExperienceInjectionResult>();
        options.OnContextInjected = results.Add;
        var world = new FakeExperienceWorld();
        world.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 0.1d);
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(world, RetrievalPolicy.Default, RankingWeights.Default, new FrozenTimeProvider(InjectionRecords.Now)),
            world,
            options);

        options.MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 1d };
        await new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [provider] }).RunAsync("x");

        Assert.Equal([InjectionRecords.Id(1)], Assert.Single(results).InjectedExperienceIds);
    }

    [Fact]
    public void A_host_limited_request_is_widened_for_backfill_only_while_the_floor_is_on()
    {
        var request = new RetrieveExperienceRequest(Authorization, TestScope, "refund", Limit: 8);

        Assert.Same(request, ExperienceContextProvider.GatedWindow(request, gate: null, maxRecords: 8, candidateLimit: 50));
        Assert.Equal(32, ExperienceContextProvider.GatedWindow(request, gate: null, maxRecords: 8, candidateLimit: 50, floorActive: true).Limit);
        Assert.Equal(20, ExperienceContextProvider.GatedWindow(request, gate: null, maxRecords: 8, candidateLimit: 20, floorActive: true).Limit);

        var unlimited = request with { Limit = null };
        Assert.Same(unlimited, ExperienceContextProvider.GatedWindow(unlimited, gate: null, maxRecords: 8, candidateLimit: 50, floorActive: true));
    }

    // ---- The floor itself ----------------------------------------------------------------------------

    [Fact]
    public async Task A_weak_match_fetched_only_as_backfill_past_the_host_s_limit_is_not_reported()
    {
        // The host asks for one record; the floor widens retrieval to 32. The weak record ranked first is reported, the
        // strong one backfills, and the weak one ranked third was never in the host's window, so it is not reported.
        var harness = new Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 }, RequestLimit = 1 };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope, status: ExperienceStatus.Reinforced, confidence: 1d), relevance: 0.45d);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope, confidence: 0.6d), relevance: 0.5d);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(3), TestScope, confidence: 0.5d), relevance: 0.1d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(2)], harness.Last.InjectedExperienceIds);
        var omission = Assert.Single(harness.Last.Omitted);
        Assert.Equal((InjectionRecords.Id(1), InjectionOmissionReason.WeakMatch), (omission.ExperienceId, omission.Reason));
    }

    // ---- The semantic leg, through a hybrid retrieval ---------------------------------------------------

    [Fact]
    public async Task A_vector_only_match_passes_on_a_semantic_floor_and_fails_closed_without_one()
    {
        var record = InjectionRecords.Record(InjectionRecords.Id(1), TestScope);
        var withPolicyFloor = new Harness
        {
            MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 },
            Policy = RetrievalPolicy.Default with { MinimumVectorRelevance = 0.75 },
            Vectors = new ScriptedVectorIndex(new ExperienceCandidate(record, 0.8)),
        };
        var withOwnFloor = new Harness
        {
            MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5, MinimumSemanticSimilarity = 0.85 },
            Policy = RetrievalPolicy.Default with { MinimumVectorRelevance = 0.75 },
            Vectors = new ScriptedVectorIndex(new ExperienceCandidate(record, 0.8)),
        };
        var withNone = new Harness
        {
            MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 },
            Vectors = new ScriptedVectorIndex(new ExperienceCandidate(record, 0.99)),
        };
        foreach (var harness in new[] { withPolicyFloor, withOwnFloor, withNone })
        {
            harness.World.Store(record);
            await harness.Agent().RunAsync("refund ticket stuck on a lock");
        }

        Assert.Equal([InjectionRecords.Id(1)], withPolicyFloor.Last.InjectedExperienceIds);
        Assert.Null(withPolicyFloor.Last.VectorFallback);

        Assert.Equal(InjectionOutcome.NothingToInject, withOwnFloor.Last.Outcome);
        Assert.Equal(
            "Below the match floor: no text match, semantic similarity 0.8 is below 0.85, and no exact failure-signature match.",
            Assert.Single(withOwnFloor.Last.Omitted).Detail);

        // No semantic floor anywhere: the semantic leg is off, so a vector-only record fails however close it is.
        Assert.Equal(InjectionOutcome.NothingToInject, withNone.Last.Outcome);
        var omission = Assert.Single(withNone.Last.Omitted);
        Assert.Equal(InjectionOmissionReason.WeakMatch, omission.Reason);
        Assert.Equal(
            "Below the match floor: no text match, no semantic floor is set, so a semantic match does not count, and no exact failure-signature match.",
            omission.Detail);
    }

    // ---- The floor itself ----------------------------------------------------------------------------

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void A_floor_outside_zero_to_one_is_refused(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceMatchFloor { MinimumTextCoverage = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceMatchFloor { MinimumTextCoverage = 0.5, MinimumSemanticSimilarity = value });
    }

    [Fact]
    public void The_coverage_has_no_default_and_the_semantic_floor_defaults_to_the_policy_s()
    {
        Assert.True(typeof(ExperienceMatchFloor).GetProperty(nameof(ExperienceMatchFloor.MinimumTextCoverage))!
            .IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), inherit: false));
        Assert.Null(new ExperienceMatchFloor { MinimumTextCoverage = 0.5 }.MinimumSemanticSimilarity);
    }

    [Fact]
    public void Any_one_leg_passes_a_record_and_a_missing_leg_never_does()
    {
        var floor = new ExperienceMatchFloor { MinimumTextCoverage = 0.5, MinimumSemanticSimilarity = 0.8 };

        Assert.True(floor.Passes(Ranked(text: 0.5, vector: null), policyVectorFloor: null));
        Assert.True(floor.Passes(Ranked(text: 0.1, vector: 0.8), policyVectorFloor: null));
        Assert.True(floor.Passes(Ranked(text: null, vector: 0.9), policyVectorFloor: null));
        Assert.False(floor.Passes(Ranked(text: 0.1, vector: 0.7), policyVectorFloor: null));
        Assert.False(floor.Passes(Ranked(text: null, vector: null), policyVectorFloor: null));
        Assert.False(floor.Passes(Ranked(text: double.NaN, vector: null), policyVectorFloor: null));
    }

    [Fact]
    public void The_semantic_leg_defaults_to_the_retrieval_policy_s_vector_floor_and_is_off_without_one()
    {
        var floor = new ExperienceMatchFloor { MinimumTextCoverage = 1d };
        var hit = Ranked(text: null, vector: 0.7);

        Assert.False(floor.Passes(hit, policyVectorFloor: 0.75));
        Assert.True(floor.Passes(hit, policyVectorFloor: 0.7));
        Assert.False(floor.Passes(Ranked(text: null, vector: 1d), policyVectorFloor: null));
        Assert.False((floor with { MinimumSemanticSimilarity = 0.71 }).Passes(hit, policyVectorFloor: 0.5));
        Assert.True((floor with { MinimumSemanticSimilarity = 0.7 }).Passes(hit, policyVectorFloor: null));
        Assert.Equal(
            "Below the match floor: no text match, semantic similarity 0.7 is below 0.75, and no exact failure-signature match.",
            floor.Detail(hit, policyVectorFloor: 0.75));
    }

    private static RankedExperience Ranked(double? text, double? vector) =>
        new(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), 0.5, [])
        {
            TextCoverage = text,
            VectorRelevance = vector,
        };

    internal sealed class Harness
    {
        private readonly List<ExperienceInjectionResult> _results = [];

        public FakeExperienceWorld World { get; } = new();

        public RecordingChatClient Client { get; } = new();

        public ExperienceMatchFloor? MinimumMatch { get; init; }

        public ExperienceInjectionLimits Limits { get; init; } = ExperienceInjectionLimits.Default;

        public string CorrelationId { get; init; } = "corr-floor";

        public int? RequestLimit { get; init; }

        public RetrievalPolicy Policy { get; init; } = RetrievalPolicy.Default;

        public ScriptedVectorIndex? Vectors { get; init; }

        public ExperienceInjectionResult Last
        {
            get
            {
                lock (_results)
                {
                    return _results[^1];
                }
            }
        }

        public ChatClientAgent Agent() => new(Client, new ChatClientAgentOptions { AIContextProviders = [Provider()] });

        public string? InjectedText() => Client.LastMessages
            ?.FirstOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)
            ?.Text;

        public ExperienceContextProvider Provider()
        {
            var clock = new FrozenTimeProvider(InjectionRecords.Now);
            return new(
                new ExperienceRetrievalService(World, Policy, RankingWeights.Default, clock, Vectors, Vectors is null ? null : new ConstantGenerator()),
                World,
                new ExperienceInjectionOptions
                {
                    ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock", CorrelationId: CorrelationId, Limit: RequestLimit),
                    Limits = Limits,
                    TimeProvider = clock,
                    MinimumMatch = MinimumMatch,
                    OnContextInjected = result =>
                    {
                        lock (_results)
                        {
                            _results.Add(result);
                        }
                    },
                });
        }
    }
    /// <summary>An embedding index that answers every search with the same candidates and nothing else.</summary>
    internal sealed class ScriptedVectorIndex(params ExperienceCandidate[] candidates) : IExperienceEmbeddingIndex
    {
        public Task<ExperienceIndexWriteResult> WriteAsync(AuthorizationContext authorization, ExperienceIndexWrite write, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceIndexScanResult> ScanAsync(AuthorizationContext authorization, ExperienceIndexScan scan, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceIndexRemoveResult> RemoveAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceVectorSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceVectorQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Found, candidates, []));
    }

    /// <summary>A two-dimensional generator whose vector never matters: the scripted index ignores it.</summary>
    private sealed class ConstantGenerator : IExperienceEmbeddingGenerator
    {
        public string ModelId => "test-model";

        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1f, 0f });
    }
}

/// <summary>Story 28.5: the telemetry of a block the match floor emptied. Process-wide listeners, so its own collection.</summary>
[Collection(TelemetryCollection.Name)]
public class MatchFloorTelemetryTests
{
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");

    [Fact]
    public async Task A_block_the_floor_emptied_reports_nothing_to_inject_with_a_weak_match_reason_and_others_carry_none()
    {
        using var probe = TelemetryProbe.Start();

        var emptied = new MatchFloorTests.Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 }, CorrelationId = "corr-floor-emptied" };
        emptied.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 0.1d);
        await emptied.Agent().RunAsync("refund ticket stuck on a lock");

        var injected = new MatchFloorTests.Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 }, CorrelationId = "corr-floor-injected" };
        injected.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 1d);
        injected.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope), relevance: 0.1d);
        await injected.Agent().RunAsync("refund ticket stuck on a lock");

        var empty = new MatchFloorTests.Harness { MinimumMatch = new ExperienceMatchFloor { MinimumTextCoverage = 0.5 }, CorrelationId = "corr-floor-nothing" };
        await empty.Agent().RunAsync("refund ticket stuck on a lock");

        var span = Assert.Single(InjectSpans(probe), activity => Equals(activity.GetTagItem(InjectionDiagnostics.CorrelationIdAttribute), "corr-floor-emptied"));
        Assert.Equal("NothingToInject", span.GetTagItem(InjectionDiagnostics.OutcomeAttribute));
        Assert.Equal(InjectionDiagnostics.WeakMatchReason, span.GetTagItem(InjectionDiagnostics.NothingReasonAttribute));

        foreach (var correlation in new[] { "corr-floor-injected", "corr-floor-nothing" })
        {
            var other = Assert.Single(InjectSpans(probe), activity => Equals(activity.GetTagItem(InjectionDiagnostics.CorrelationIdAttribute), correlation));
            Assert.Null(other.GetTagItem(InjectionDiagnostics.NothingReasonAttribute));
        }
    }

    private static IEnumerable<System.Diagnostics.Activity> InjectSpans(TelemetryProbe probe) =>
        probe.LibraryActivities.Where(activity => Equals(activity.GetTagItem(InjectionDiagnostics.OperationAttribute), InjectionDiagnostics.Inject));
}
