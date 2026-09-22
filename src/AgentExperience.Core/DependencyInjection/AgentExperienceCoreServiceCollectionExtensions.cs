using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
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
    /// Both also pick up an <see cref="ExperienceIndexingService"/> when one is registered -- so a
    /// committed record is embedded and a record that leaves eligibility is de-indexed -- and work
    /// without one, which is the text-only deployment. Registration order does not matter.
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
        // Both hooks are resolved through an explicit factory rather than by constructor selection,
        // because the optional ExperienceIndexingService has to come back as null when nothing
        // registered it -- which is what a text-only deployment is.
        services.TryAddSingleton(provider => new ExperienceLifecycleService(
            provider.GetRequiredService<IExperienceRecordStore>(),
            provider.GetService<ExperienceIndexingService>()));

        // The indexing hook is resolved optionally, not required: a host that never registered
        // AddAgentExperienceIndexing gets finalization with no hook at all, which is exactly the
        // text-only deployment. Registering it later still works, because this factory runs when the
        // finalization singleton is first resolved rather than now.
        services.TryAddSingleton(provider => new ExperienceFinalizationService(
            provider.GetRequiredService<IExperienceCaptureService>(),
            provider.GetRequiredService<IExperienceReflector>(),
            provider.GetRequiredService<IExperienceRecordStore>(),
            provider.GetRequiredService<ExperienceLifecycleService>(),
            provider.GetService<ExperienceIndexingService>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="ExperienceReuseFeedbackService"/> as a singleton, so a host can record what
    /// happened in a run that stored experience was injected into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered separately from <see cref="AddAgentExperienceCore"/> because it needs an
    /// <see cref="IExperienceReuseFeedbackStore"/>, which Core does not implement: register a storage
    /// adapter's ledger as well (for example <c>AddAgentExperiencePostgresReuseFeedbackStore</c>), or
    /// resolving the service fails. It also needs the <see cref="ExperienceLifecycleService"/> that
    /// <see cref="AddAgentExperienceCore"/> registers, which is the one path any score moves through.
    /// </para>
    /// <para>
    /// Recording feedback is optional and additive: a host that never calls it simply never moves a
    /// score from reuse, and a host that calls it with no attribution evidence records the exposure and
    /// still moves nothing.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperienceReuseFeedback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => new ExperienceReuseFeedbackService(
            provider.GetRequiredService<IExperienceReuseFeedbackStore>(),
            provider.GetRequiredService<ExperienceLifecycleService>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="ExperienceIndexingService"/> as a singleton, so a committed record can be
    /// embedded and its vector stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered separately from <see cref="AddAgentExperienceCore"/> because it needs an
    /// <see cref="IExperienceEmbeddingIndex"/> and an <see cref="IExperienceEmbeddingGenerator"/>,
    /// neither of which Core implements: register an adapter's index (for example
    /// <c>AddAgentExperiencePostgresEmbeddingIndex</c>) and a generator as well, or resolving the
    /// service fails.
    /// </para>
    /// <para>
    /// Order does not matter. <see cref="AddAgentExperienceCore"/> resolves this service optionally
    /// and lazily, so calling this before or after it wires the post-commit indexing hook either way;
    /// omitting it entirely leaves finalization with no hook, which is a supported deployment.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperienceIndexing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The retrieval policy is resolved optionally and shared: its confidence floor is the same one
        // the vector search applies, so a record the search would never return is never embedded. A
        // host that never registered one gets RetrievalPolicy.Default, which is what retrieval would
        // have used anyway.
        services.TryAddSingleton(provider => new ExperienceIndexingService(
            provider.GetRequiredService<IExperienceEmbeddingIndex>(),
            provider.GetRequiredService<IExperienceEmbeddingGenerator>(),
            provider.GetService<RetrievalPolicy>()));

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
        // The vector channel is resolved optionally: both halves of it present means hybrid
        // retrieval, and anything less means an explicitly flagged text-only result rather than a
        // failure. A host adds it by registering an IExperienceEmbeddingIndex and an
        // IExperienceEmbeddingGenerator, in any order relative to this call.
        services.TryAddSingleton(provider => new ExperienceRetrievalService(
            provider.GetRequiredService<IExperienceCandidateSource>(),
            effectivePolicy,
            effectiveWeights,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IExperienceEmbeddingIndex>(),
            provider.GetService<IExperienceEmbeddingGenerator>()));

        return services;
    }
}
