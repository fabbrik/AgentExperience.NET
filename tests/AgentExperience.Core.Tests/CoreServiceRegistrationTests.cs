using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Resolves what <see cref="AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceCore"/>
/// registers out of a real container, so deleting a registration fails here rather than only at a
/// host's startup. Also pins the two properties a host depends on: the caller's own arguments are the
/// ones that reach the services, and a host implementation registered first still wins.
/// </summary>
public class CoreServiceRegistrationTests
{
    private static readonly SanitizationOptions CallerOptions = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 1_000,
            MaxFieldNameLength: 100),
    });

    private static readonly CaptureLimits CallerLimits = new(10, 10, 1_000, 1_000);

    [Fact]
    public void AddAgentExperienceCore_registers_every_service_the_finalization_path_needs()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(new StubStore()); // the storage adapter's job
        services.AddAgentExperienceCore(CallerOptions, CallerLimits);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ISanitizer>());
        Assert.NotNull(provider.GetRequiredService<IExperienceCaptureService>());
        Assert.NotNull(provider.GetRequiredService<IExperienceReflector>());
        Assert.NotNull(provider.GetRequiredService<ExperienceLifecycleService>());
        Assert.NotNull(provider.GetRequiredService<ExperienceFinalizationService>());

        // Singletons, so capture's in-memory snapshots survive between resolutions.
        Assert.Same(provider.GetRequiredService<IExperienceCaptureService>(), provider.GetRequiredService<IExperienceCaptureService>());
        Assert.Same(provider.GetRequiredService<ExperienceFinalizationService>(), provider.GetRequiredService<ExperienceFinalizationService>());
    }

    [Fact]
    public async Task The_sanitizer_applies_the_policy_the_caller_passed_even_when_the_host_registered_another()
    {
        // A host-registered SanitizationOptions must not silently replace the policy the caller handed
        // to AddAgentExperienceCore: the sanitizer would then enforce a policy nobody passed to it.
        var services = new ServiceCollection();
        services.AddSingleton(SanitizationOptions.Empty); // registered first, so TryAdd keeps it
        services.AddAgentExperienceCore(CallerOptions, CallerLimits);

        using var provider = services.BuildServiceProvider();

        var sanitized = await provider.GetRequiredService<ISanitizer>().SanitizeAsync(
            new RawPayload("ToolResult", new Dictionary<string, object?> { ["value"] = "ok" }));

        Assert.Equal(SanitizationDecision.Allowed, sanitized.Decision);
    }

    [Fact]
    public void A_host_implementation_registered_first_wins()
    {
        var hostSanitizer = new DefaultSanitizer(CallerOptions);
        var hostReflector = new DefaultExperienceReflector();

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(new StubStore());
        services.AddSingleton<ISanitizer>(hostSanitizer);
        services.AddSingleton<IExperienceReflector>(hostReflector);
        services.AddAgentExperienceCore(CallerOptions, CallerLimits);

        using var provider = services.BuildServiceProvider();

        Assert.Same(hostSanitizer, provider.GetRequiredService<ISanitizer>());
        Assert.Same(hostReflector, provider.GetRequiredService<IExperienceReflector>());
    }

    [Fact]
    public void AddAgentExperienceRetrieval_registers_the_retrieval_service_with_the_documented_defaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceCandidateSource>(new StubCandidateSource()); // the storage adapter's job
        services.AddAgentExperienceRetrieval();

        using var provider = services.BuildServiceProvider();
        var retrieval = provider.GetRequiredService<ExperienceRetrievalService>();

        Assert.Same(RetrievalPolicy.Default, provider.GetRequiredService<RetrievalPolicy>());
        Assert.Same(RankingWeights.Default, provider.GetRequiredService<RankingWeights>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Equal(TimeSpan.FromMilliseconds(500), retrieval.Policy.Timeout);
        Assert.Equal(0.35, retrieval.Weights.Relevance);
        Assert.Same(retrieval, provider.GetRequiredService<ExperienceRetrievalService>());
    }

    [Fact]
    public void The_retrieval_service_uses_the_policy_and_weights_the_caller_passed_even_when_the_host_registered_others()
    {
        var callerPolicy = RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(2) };
        var callerWeights = new RankingWeights(1d, 0d, 0d, 0d, 0d);

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceCandidateSource>(new StubCandidateSource());
        services.AddSingleton(RetrievalPolicy.Default); // registered first, so TryAdd keeps it
        services.AddAgentExperienceRetrieval(callerPolicy, callerWeights);

        using var provider = services.BuildServiceProvider();
        var retrieval = provider.GetRequiredService<ExperienceRetrievalService>();

        Assert.Same(callerPolicy, retrieval.Policy);
        Assert.Same(callerWeights, retrieval.Weights);
        Assert.Same(RetrievalPolicy.Default, provider.GetRequiredService<RetrievalPolicy>());
    }

    [Fact]
    public async Task A_host_registered_TimeProvider_is_the_clock_the_retrieval_service_actually_measures_with()
    {
        // Resolved from the container, not defaulted: a service that quietly used TimeProvider.System
        // would judge expiry against the wall clock and return the stale record below.
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var stale = StubCandidateSource.RecordUpdatedAt(now - TimeSpan.FromDays(30));

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FrozenClock(now)); // registered first, so TryAdd keeps it
        services.AddSingleton<IExperienceCandidateSource>(new StubCandidateSource(stale));
        services.AddAgentExperienceRetrieval(RetrievalPolicy.Default with { MaxAge = TimeSpan.FromDays(7) });

        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<ExperienceRetrievalService>().RetrieveAsync(
            new RetrieveExperienceRequest(StubCandidateSource.Authorization, StubCandidateSource.Scope, "refund"));

        Assert.IsType<FrozenClock>(provider.GetRequiredService<TimeProvider>());
        Assert.Empty(result.Records);
        Assert.Equal(
            [new ExcludedExperience(stale.ExperienceId, RetrievalExclusionReason.Expired)],
            result.Excluded);
        Assert.Equal(TimeSpan.Zero, result.Elapsed); // measured with the frozen clock too
    }

    [Fact]
    public void Retrieval_without_a_candidate_source_fails_to_resolve_rather_than_retrieving_nothing()
    {
        var services = new ServiceCollection();
        services.AddAgentExperienceRetrieval();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ExperienceRetrievalService>());
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperienceRetrieval());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperienceCore(CallerOptions, CallerLimits));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperienceCore(null!, CallerLimits));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperienceCore(CallerOptions, null!));
    }

    /// <summary>Stands in for a storage adapter's search registration, answering with whatever it was given.</summary>
    private sealed class StubCandidateSource(params ExperienceRecord[] records) : IExperienceCandidateSource
    {
        public static Scope Scope { get; } = new("tenant-1", "app-1", "project-1");

        public static AuthorizationContext Authorization { get; } = new("tenant-1", "host", [], DateTimeOffset.UnixEpoch);

        public static ExperienceRecord RecordUpdatedAt(DateTimeOffset updatedAt) => new(
            Guid.NewGuid(), Guid.NewGuid(), Scope, "refund-ticket", "Resolve a refund", [],
            new Outcome(TaskVerificationStatus.Verified, [], null, updatedAt), 1, null,
            new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            new Provenance("tests", null, updatedAt, null),
            ExperienceStatus.Validated, 0.9, 1, 0, 1, updatedAt, updatedAt);

        public Task<ExperienceCandidateSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceCandidateQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceCandidateSearchResult(
                ExperienceStoreOutcome.Found,
                [.. records.Select(record => new ExperienceCandidate(record, 1d))],
                []));
    }

    /// <summary>A clock frozen at a known instant, standing in for a host's own <see cref="TimeProvider"/>.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverFires();

        private sealed class NeverFires : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Stands in for a storage adapter's registration; finalization never calls it here.</summary>
    private sealed class StubStore : IExperienceRecordStore
    {
        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
