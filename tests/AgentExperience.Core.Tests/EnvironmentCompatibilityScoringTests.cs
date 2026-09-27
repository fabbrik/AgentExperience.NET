using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Retrieval;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers graded environment compatibility (story 10.1): one test per row of the story's I/O matrix,
/// the default scorer on its own, the optional constructor argument, and the DI resolution of a
/// host-registered <see cref="IEnvironmentCompatibilityScorer"/>.
/// </summary>
public class EnvironmentCompatibilityScoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Scope RequestScope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:read"], Now);

    private const string TaskText = "resolve a refund ticket";

    // ---------------------------------------------------------------- matrix: nothing preferred

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nothing_preferred_with_the_default_scorer_scores_every_record_one_and_changes_nothing(bool emptyRatherThanNull)
    {
        var candidates = new[]
        {
            new ExperienceCandidate(Record(Id(1), metadata: Metadata(("a", "1"))), 0.9),
            new ExperienceCandidate(Record(Id(2)), 0.4),
        };

        var baseline = await Service(candidates).RetrieveAsync(Request());
        var preferred = await Service(candidates).RetrieveAsync(
            Request() with { PreferredEnvironmentAttributes = emptyRatherThanNull ? new Dictionary<string, string>() : null });

        Assert.All(preferred.Records, ranked => Assert.Equal(1d, EnvironmentComponent(ranked).Value));
        Assert.Equal(ExperienceRetrievalService.CompatibleEnvironmentScore, EnvironmentComponent(preferred.Records[0]).Value);
        Assert.Equal(
            baseline.Records.Select(ranked => (ranked.Record.ExperienceId, ranked.Score, Components: string.Join('|', ranked.Components))),
            preferred.Records.Select(ranked => (ranked.Record.ExperienceId, ranked.Score, Components: string.Join('|', ranked.Components))));
        Assert.Equal(baseline.Excluded, preferred.Excluded);
    }

    // ---------------------------------------------------------------- matrix: partial match

    [Fact]
    public async Task A_partial_match_scores_the_matched_fraction_and_outranks_an_otherwise_equal_record_with_none()
    {
        // Handed over worse-first, and the worse one has the lower ID, so only the environment
        // component can put the partial match on top.
        var service = Service(
            new ExperienceCandidate(Record(Id(1), metadata: Metadata(("a", "0"), ("b", "0"))), 0.5),
            new ExperienceCandidate(Record(Id(2), metadata: Metadata(("a", "1"), ("b", "3"))), 0.5));

        var result = await service.RetrieveAsync(Request() with { PreferredEnvironmentAttributes = Metadata(("a", "1"), ("b", "2")) });

        Assert.Equal([Id(2), Id(1)], result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Equal(0.5, EnvironmentComponent(result.Records[0]).Value);
        Assert.Equal(0d, EnvironmentComponent(result.Records[1]).Value);
        Assert.Equal(RankingWeights.Default.EnvironmentCompatibility, EnvironmentComponent(result.Records[0]).Weight);
        Assert.Equal(result.Records[0].Components.Sum(component => component.Contribution), result.Records[0].Score, 12);
    }

    // ---------------------------------------------------------------- matrix: missing key

    [Fact]
    public async Task A_missing_preferred_key_scores_zero_but_never_excludes_the_record()
    {
        var service = Service(new ExperienceCandidate(Record(Id(1), metadata: Metadata(("other", "1"))), 0.5));

        var result = await service.RetrieveAsync(Request() with { PreferredEnvironmentAttributes = Metadata(("a", "1")) });

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        var ranked = Assert.Single(result.Records);
        Assert.Equal(0d, EnvironmentComponent(ranked).Value);
        Assert.Empty(result.Excluded);
        Assert.True(result.EnvironmentUnrestricted); // preferences are not requirements
    }

    [Fact]
    public void The_default_scorer_matches_ordinally_and_never_semantically()
    {
        var scorer = AttributeMatchEnvironmentScorer.Instance;
        var environment = Fingerprint(Metadata(("region", "us-east"), ("dotnet", "10.0.1")));

        Assert.Equal(1d, scorer.Score(environment, new Dictionary<string, string>()));
        Assert.Equal(1d, scorer.Score(environment, Metadata(("region", "us-east"))));
        Assert.Equal(0d, scorer.Score(environment, Metadata(("region", "US-EAST"))));
        Assert.Equal(0d, scorer.Score(environment, Metadata(("dotnet", "10.0"))));
        Assert.Equal(0.5, scorer.Score(environment, Metadata(("region", "us-east"), ("dotnet", "10.0"))));
        Assert.Throws<ArgumentNullException>(() => scorer.Score(null!, new Dictionary<string, string>()));
        Assert.Throws<ArgumentNullException>(() => scorer.Score(environment, null!));
    }

    // ---------------------------------------------------------------- matrix: required + preferred

    [Fact]
    public async Task A_required_mismatch_is_excluded_before_scoring_and_the_scorer_never_sees_it()
    {
        var scorer = new RecordingScorer(0.3);
        var excluded = Record(Id(1), metadata: Metadata(("r", "y"), ("a", "1")));
        var kept = Record(Id(2), metadata: Metadata(("r", "x")));
        var service = Service([new ExperienceCandidate(excluded, 0.9), new ExperienceCandidate(kept, 0.5)], scorer);

        var result = await service.RetrieveAsync(
            Request(required: Metadata(("r", "x"))) with { PreferredEnvironmentAttributes = Metadata(("a", "1")) });

        Assert.Equal([new ExcludedExperience(Id(1), RetrievalExclusionReason.EnvironmentMismatch)], result.Excluded);
        var ranked = Assert.Single(result.Records);
        Assert.Equal(Id(2), ranked.Record.ExperienceId);
        Assert.Equal(0.3, EnvironmentComponent(ranked).Value);
        var call = Assert.Single(scorer.Calls);
        Assert.Same(kept.Environment, call.Environment);
        Assert.Equal(Metadata(("a", "1")), call.Preferred);
    }

    [Fact]
    public async Task A_host_scorer_is_called_for_every_eligible_record_with_an_empty_dictionary_when_nothing_is_preferred()
    {
        var scorer = new RecordingScorer(0.25);
        var service = Service(
            [new ExperienceCandidate(Record(Id(1)), 0.5), new ExperienceCandidate(Record(Id(2)), 0.5)],
            scorer);

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(2, scorer.Calls.Count);
        Assert.All(scorer.Calls, call => Assert.Empty(call.Preferred));
        Assert.All(result.Records, ranked => Assert.Equal(0.25, EnvironmentComponent(ranked).Value));
    }

    // ---------------------------------------------------------------- matrix: host scorer out of range

    [Theory]
    [InlineData(1.7, 1d)]
    [InlineData(-0.2, 0d)]
    [InlineData(double.NaN, 0d)]
    [InlineData(double.PositiveInfinity, 1d)]
    [InlineData(double.NegativeInfinity, 0d)]
    public async Task A_host_scorer_value_outside_the_unit_interval_is_clamped_and_NaN_counts_as_zero(double returned, double expected)
    {
        var service = Service([new ExperienceCandidate(Record(Id(1)), 0.5)], new RecordingScorer(returned));

        var result = await service.RetrieveAsync(Request());

        var ranked = Assert.Single(result.Records);
        Assert.Equal(expected, EnvironmentComponent(ranked).Value);
        Assert.Equal(ranked.Components.Sum(component => component.Contribution), ranked.Score, 12);
    }

    // ---------------------------------------------------------------- matrix: host scorer throws

    [Fact]
    public async Task A_throwing_host_scorer_fails_the_whole_retrieval_closed_with_a_content_free_reason()
    {
        const string Secret = "attribute-value-that-must-not-leak";
        var boom = new InvalidOperationException(Secret);
        var service = Service(
            [
                new ExperienceCandidate(Record(Id(1), metadata: Metadata(("a", Secret))), 0.9),
                new ExperienceCandidate(Record(Id(2)), 0.5),
            ],
            new RecordingScorer(0.5) { ThrowOnCall = 2, Exception = boom });

        var result = await service.RetrieveAsync(
            Request(correlationId: "corr-1") with { PreferredEnvironmentAttributes = Metadata(("a", Secret)) });

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records); // never the one record scored before the throw
        Assert.Empty(result.Excluded);
        Assert.Equal("corr-1", result.CorrelationId);
        var failure = Assert.IsType<RetrievalFailure>(result.Failure);
        Assert.Same(boom, failure.Exception);
        Assert.DoesNotContain(Secret, failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\"a\"", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scorer_that_cancels_the_callers_token_makes_retrieval_throw_OperationCanceledException_unwrapped()
    {
        using var caller = new CancellationTokenSource();
        var scorer = new CancellingScorer(caller);
        var service = Service(
            [new ExperienceCandidate(Record(Id(1)), 0.5), new ExperienceCandidate(Record(Id(2)), 0.5)],
            scorer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RetrieveAsync(Request(), caller.Token));
        Assert.Equal(1, scorer.Calls);
    }

    // ---------------------------------------------------------------- construction

    [Fact]
    public async Task A_null_scorer_argument_means_the_default_scorer_and_the_other_arguments_are_still_required()
    {
        var source = new FakeCandidateSource(new ExperienceCandidate(Record(Id(1), metadata: Metadata(("a", "1"), ("b", "0"))), 0.5));
        var clock = new FixedTimeProvider(Now);
        var service = new ExperienceRetrievalService(
            source, RetrievalPolicy.Default, RankingWeights.Default, clock, embeddingIndex: null, embeddingGenerator: null, environmentScorer: null);

        var result = await service.RetrieveAsync(Request() with { PreferredEnvironmentAttributes = Metadata(("a", "1"), ("b", "2")) });

        Assert.Equal(0.5, EnvironmentComponent(Assert.Single(result.Records)).Value);
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(null!, RetrievalPolicy.Default, RankingWeights.Default, clock, null, null, null));
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(source, null!, RankingWeights.Default, clock, null, null, null));
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(source, RetrievalPolicy.Default, null!, clock, null, null, null));
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, null!, null, null, null));
    }

    // ---------------------------------------------------------------- DI

    [Fact]
    public async Task A_host_registered_scorer_is_the_one_AddAgentExperienceRetrieval_builds_the_service_with()
    {
        var scorer = new RecordingScorer(0.42);
        var services = new ServiceCollection();
        services.AddAgentExperienceRetrieval();
        services.AddSingleton<IExperienceCandidateSource>(new FakeCandidateSource(new ExperienceCandidate(Record(Id(1)), 0.5)));
        services.AddSingleton<IEnvironmentCompatibilityScorer>(scorer); // after the call: resolved lazily

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ExperienceRetrievalService>().RetrieveAsync(Request());

        Assert.Equal(0.42, EnvironmentComponent(Assert.Single(result.Records)).Value);
        Assert.Single(scorer.Calls);
    }

    [Fact]
    public async Task Without_a_registered_scorer_AddAgentExperienceRetrieval_uses_the_default_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceCandidateSource>(
            new FakeCandidateSource(new ExperienceCandidate(Record(Id(1), metadata: Metadata(("a", "1"))), 0.5)));
        services.AddAgentExperienceRetrieval();

        using var provider = services.BuildServiceProvider();
        var retrieval = provider.GetRequiredService<ExperienceRetrievalService>();

        var none = await retrieval.RetrieveAsync(Request());
        var half = await retrieval.RetrieveAsync(Request() with { PreferredEnvironmentAttributes = Metadata(("a", "1"), ("b", "2")) });

        Assert.Equal(1d, EnvironmentComponent(Assert.Single(none.Records)).Value);
        Assert.Equal(0.5, EnvironmentComponent(Assert.Single(half.Records)).Value);
    }

    // ---------------------------------------------------------------- helpers

    private static RankingComponent EnvironmentComponent(RankedExperience ranked) =>
        Assert.Single(ranked.Components, component => component.Kind == RankingComponentKind.EnvironmentCompatibility);

    private static Guid Id(int n) => Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{n:000000000000}"));

    private static Dictionary<string, string> Metadata(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static RetrieveExperienceRequest Request(
        IReadOnlyDictionary<string, string>? required = null,
        string? correlationId = null) => new(Authorization, RequestScope, TaskText, required, correlationId);

    private static ExperienceRetrievalService Service(params ExperienceCandidate[] candidates) =>
        Service(candidates, scorer: null);

    private static ExperienceRetrievalService Service(ExperienceCandidate[] candidates, IEnvironmentCompatibilityScorer? scorer) =>
        new(new FakeCandidateSource(candidates), RetrievalPolicy.Default, RankingWeights.Default, new FixedTimeProvider(Now), null, null, scorer);

    private static EnvironmentFingerprint Fingerprint(IReadOnlyDictionary<string, string>? metadata) =>
        new("worker-01", "10.0.0", "linux-x64", null, metadata ?? new Dictionary<string, string>());

    private static ExperienceRecord Record(Guid id, IReadOnlyDictionary<string, string>? metadata = null) => new(
        ExperienceId: id,
        SourceRunId: Guid.NewGuid(),
        Scope: RequestScope,
        TaskId: "refund-ticket",
        TaskSummary: "Resolve a refund ticket",
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Verified, [], "checks passed", Now),
        CompletionScore: 1,
        Reflection: null,
        Environment: Fingerprint(metadata),
        Provenance: new Provenance("tests", null, Now, null),
        Status: ExperienceStatus.Validated,
        ReuseConfidence: 0.5,
        SupportingValidations: 1,
        Contradictions: 0,
        Revision: 1,
        CreatedAt: Now,
        UpdatedAt: Now);

    /// <summary>A host scorer that returns a fixed value and records every call, optionally throwing on the Nth.</summary>
    private sealed class RecordingScorer(double value) : IEnvironmentCompatibilityScorer
    {
        public List<(EnvironmentFingerprint Environment, IReadOnlyDictionary<string, string> Preferred)> Calls { get; } = [];

        public int ThrowOnCall { get; init; }

        public Exception? Exception { get; init; }

        public double Score(EnvironmentFingerprint recordEnvironment, IReadOnlyDictionary<string, string> preferredAttributes)
        {
            Calls.Add((recordEnvironment, preferredAttributes));
            if (Calls.Count == ThrowOnCall)
            {
                throw Exception!;
            }

            return value;
        }
    }

    /// <summary>Cancels the caller's token on its first call, then returns normally.</summary>
    private sealed class CancellingScorer(CancellationTokenSource caller) : IEnvironmentCompatibilityScorer
    {
        public int Calls { get; private set; }

        public double Score(EnvironmentFingerprint recordEnvironment, IReadOnlyDictionary<string, string> preferredAttributes)
        {
            Calls++;
            caller.Cancel();
            return 1d;
        }
    }

    private sealed class FakeCandidateSource(params ExperienceCandidate[] candidates) : IExperienceCandidateSource
    {
        public Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, []));
    }

    /// <summary>
    /// A clock frozen at a known instant. Its timers never fire, so the retrieval timeout cannot trip on
    /// a slow runner: without this override, <see cref="TimeProvider.CreateTimer"/> is a real-time timer.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();

        private sealed class FrozenTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
