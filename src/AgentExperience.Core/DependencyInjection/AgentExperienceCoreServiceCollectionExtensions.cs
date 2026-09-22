using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentExperience.Core.DependencyInjection;

/// <summary>
/// Registers AgentExperience.NET's Core services in a <see cref="IServiceCollection"/>. Core owns
/// its own registration so a host never has to know which concrete types implement which port; the
/// storage adapter registers its own in the same way (see
/// <c>AddAgentExperiencePostgresStore</c>), and <c>AgentExperience.Abstractions</c> stays BCL-only.
/// </summary>
public static class AgentExperienceCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sanitizer, the in-memory capture service, the default reflector, the lifecycle
    /// service, and the finalization service as singletons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every registration uses <c>TryAdd</c>, so a host that has already registered its own
    /// <see cref="ISanitizer"/>, <see cref="IExperienceCaptureService"/>, or
    /// <see cref="IExperienceReflector"/> keeps it.
    /// </para>
    /// <para>
    /// <see cref="ExperienceLifecycleService"/> and <see cref="ExperienceFinalizationService"/> both
    /// need an <see cref="IExperienceRecordStore"/>, which Core does not implement: register a
    /// storage adapter (for example <c>AddAgentExperiencePostgresStore</c>) as well, or resolving
    /// them fails.
    /// </para>
    /// <para>
    /// No sanitization policy or capture limit is invented here: both are host decisions with real
    /// security and memory consequences, so both are required arguments.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="sanitizationOptions">The per-<c>Kind</c> sanitization policy the default sanitizer applies.</param>
    /// <param name="captureLimits">The limits in-memory capture enforces.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperienceCore(
        this IServiceCollection services,
        SanitizationOptions sanitizationOptions,
        CaptureLimits captureLimits)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sanitizationOptions);
        ArgumentNullException.ThrowIfNull(captureLimits);

        // The arguments are captured by the factories rather than resolved back out of the container.
        // Re-resolving them would let a SanitizationOptions or CaptureLimits the host registered earlier
        // silently replace the caller's, so the sanitizer would run a policy nobody passed to it.
        services.TryAddSingleton(sanitizationOptions);
        services.TryAddSingleton(captureLimits);
        services.TryAddSingleton<ISanitizer>(_ => new DefaultSanitizer(sanitizationOptions));
        services.TryAddSingleton<IExperienceCaptureService>(provider => new InMemoryExperienceCaptureService(
            provider.GetRequiredService<ISanitizer>(),
            captureLimits));
        services.TryAddSingleton<IExperienceReflector, DefaultExperienceReflector>();
        services.TryAddSingleton<ExperienceLifecycleService>();
        services.TryAddSingleton<ExperienceFinalizationService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="ExperienceRetrievalService"/> as a singleton, together with the
    /// <see cref="RetrievalPolicy"/> and <see cref="RankingWeights"/> it runs under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retrieval is registered separately from <see cref="AddAgentExperienceCore"/> because it needs
    /// an <see cref="IExperienceCandidateSource"/>, which Core does not implement: register a storage
    /// adapter's search as well (for example <c>AddAgentExperiencePostgresCandidateSource</c>), or
    /// resolving the service fails.
    /// </para>
    /// <para>
    /// Unlike sanitization policy and capture limits, retrieval has documented defaults
    /// (<see cref="RetrievalPolicy.Default"/> and <see cref="RankingWeights.Default"/>), so both
    /// arguments are optional. Passing an invalid policy or weighting is impossible: both throw at
    /// construction, before this call. The <see cref="TimeProvider"/> the timeout, expiry, and recency
    /// are measured with is <see cref="TimeProvider.System"/> unless the host registered its own
    /// first.
    /// </para>
    /// <para>
    /// Every registration uses <c>TryAdd</c>, so a host that registered its own policy, weights,
    /// clock, or service keeps it.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="policy">The retrieval bounds and thresholds. Defaults to <see cref="RetrievalPolicy.Default"/>.</param>
    /// <param name="weights">The ranking weights. Defaults to <see cref="RankingWeights.Default"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperienceRetrieval(
        this IServiceCollection services,
        RetrievalPolicy? policy = null,
        RankingWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var effectivePolicy = policy ?? RetrievalPolicy.Default;
        var effectiveWeights = weights ?? RankingWeights.Default;

        services.TryAddSingleton(effectivePolicy);
        services.TryAddSingleton(effectiveWeights);
        services.TryAddSingleton(TimeProvider.System);

        // The caller's own policy and weights are captured rather than resolved back out of the
        // container, for the same reason the sanitizer's options are: a RetrievalPolicy the host
        // registered earlier must not silently replace the one passed here.
        services.TryAddSingleton(provider => new ExperienceRetrievalService(
            provider.GetRequiredService<IExperienceCandidateSource>(),
            effectivePolicy,
            effectiveWeights,
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }
}
