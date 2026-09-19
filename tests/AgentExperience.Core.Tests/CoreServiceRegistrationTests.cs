using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
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
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperienceCore(CallerOptions, CallerLimits));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperienceCore(null!, CallerLimits));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperienceCore(CallerOptions, null!));
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
