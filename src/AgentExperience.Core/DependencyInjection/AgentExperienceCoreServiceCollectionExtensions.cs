using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
    /// <para>
    /// <b>Independence verification.</b> The lifecycle service verifies every confidence submission's
    /// run, round and assessment token (<see cref="IndependenceVerification.Verified"/>), counting as known
    /// the runs finalized into records and the runs the registered <see cref="IExperienceCaptureService"/>
    /// holds. Register an <see cref="ExperienceIndependenceOptions"/> singleton, before or after this call,
    /// to supply the assessment token key or to opt out with
    /// <see cref="IndependenceVerification.TrustHostSuppliedIdentifiers"/>; without one, verification is on
    /// and human evidence is refused for want of a key. <see cref="AssessmentTokenIssuer"/> is deliberately
    /// <em>not</em> registered: anything that can resolve it can mint, so construct it where your review flow
    /// records a person's decision, not in a container agent-driven components resolve from.
    /// </para>
    /// <para>
    /// <b>Provenance signing.</b> Register an <see cref="ExperienceProvenanceSigningOptions"/> singleton, before or
    /// after this call -- directly (<c>services.AddSingleton(options)</c>) or through the options pattern
    /// (<c>IOptions&lt;ExperienceProvenanceSigningOptions&gt;</c>, for example
    /// <c>services.AddSingleton(Options.Create(options))</c>); a direct registration wins when both exist. The lifecycle
    /// service then checks every run's finalized record against it, and finalization signs with the same ring. Without
    /// one, nothing is signed or checked.
    /// </para>
    /// <para>
    /// <b>Reflection screening.</b> Finalization screens every reflection through the registered
    /// <see cref="ISanitizer"/> as a <see cref="ReflectionScreening.PayloadKind"/> payload (see
    /// <see cref="ReflectionScreening"/>). The default sanitizer allows it unchanged unless the
    /// <paramref name="sanitizationOptions"/> configure a policy for that kind; a host sanitizer must
    /// allow it, or every reflection is refused and every record quarantined. Register an
    /// <see cref="ExperienceFinalizationOptions"/> singleton, before or after this call, to change the
    /// <see cref="ReflectionLimits"/>.
    /// </para>
    /// <para>
    /// <b>Confidence engine.</b> A host that registers an <see cref="IExperienceConfidenceEngine"/>, in any
    /// order relative to this call, has it score confidence evidence; otherwise
    /// <see cref="ReuseConfidenceHeuristicEngine"/> does. It is resolved once, from the root provider, so
    /// register it as a singleton. An engine with a malformed identity fails when the lifecycle service is
    /// first resolved.
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
        // registered it -- which is what a text-only deployment is. A host IExperienceConfidenceEngine
        // is resolved the same way; without one, the heuristic scores evidence.
        services.TryAddSingleton(provider => new ExperienceLifecycleService(
            provider.GetRequiredService<IExperienceRecordStore>(),
            provider.GetService<ExperienceIndexingService>(),
            provider.GetService<ExperienceIndependenceOptions>() ?? new ExperienceIndependenceOptions(),
            provider.GetRequiredService<IExperienceCaptureService>(),
            deindexingTimeout: null,
            provider.GetService<IExperienceConfidenceEngine>(),
            ProvenanceSigningFrom(provider, services)));

        // The indexing hook is resolved optionally, not required: a host that never registered
        // AddAgentExperienceIndexing gets finalization with no hook at all, which is exactly the
        // text-only deployment. Registering it later still works, because this factory runs when the
        // finalization singleton is first resolved rather than now.
        services.TryAddSingleton(provider => new ExperienceFinalizationService(
            provider.GetRequiredService<IExperienceCaptureService>(),
            provider.GetRequiredService<IExperienceReflector>(),
            provider.GetRequiredService<IExperienceRecordStore>(),
            provider.GetRequiredService<ExperienceLifecycleService>(),
            provider.GetService<ExperienceIndexingService>(),
            indexingTimeout: null,
            provenanceSigning: null,
            provider.GetRequiredService<ISanitizer>(),
            provider.GetService<ExperienceFinalizationOptions>()));

        return services;
    }

    /// <summary>
    /// The registered provenance signing options, directly or through an explicitly registered
    /// <see cref="IOptions{TOptions}"/>, or <see langword="null"/> when neither is registered.
    /// </summary>
    /// <remarks>
    /// <c>IOptions&lt;T&gt;</c> is resolved only when the collection names it explicitly: <c>AddOptions</c> registers it
    /// as an open generic whose manager needs a parameterless constructor, which these options deliberately lack (they
    /// are validated at construction), so resolving it blindly would throw for a host that signs nothing.
    /// </remarks>
    private static ExperienceProvenanceSigningOptions? ProvenanceSigningFrom(IServiceProvider provider, IServiceCollection services) =>
        provider.GetService<ExperienceProvenanceSigningOptions>()
        ?? (services.Any(descriptor => descriptor.ServiceType == typeof(IOptions<ExperienceProvenanceSigningOptions>))
            ? provider.GetService<IOptions<ExperienceProvenanceSigningOptions>>()?.Value
            : null);

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
    /// <para>
    /// A host that registers an <see cref="IEnvironmentCompatibilityScorer"/>, in any order relative
    /// to this call, has it grade the environment component; otherwise
    /// <see cref="AttributeMatchEnvironmentScorer"/> does.
    /// </para>
    /// <para>
    /// A host that registers a <see cref="ConfidenceDecayPolicy"/>, in any order relative to this call,
    /// has each record's confidence component decay by its domain's half-life at ranking time;
    /// otherwise confidence ranks as stored.
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
        // IExperienceEmbeddingGenerator, in any order relative to this call. An
        // IEnvironmentCompatibilityScorer is resolved the same way; without one, the default
        // attribute-match scorer grades the environment component. A ConfidenceDecayPolicy is
        // resolved the same way; without one, confidence does not decay.
        services.TryAddSingleton(provider => new ExperienceRetrievalService(
            provider.GetRequiredService<IExperienceCandidateSource>(),
            effectivePolicy,
            effectiveWeights,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IExperienceEmbeddingIndex>(),
            provider.GetService<IExperienceEmbeddingGenerator>(),
            provider.GetService<IEnvironmentCompatibilityScorer>(),
            provider.GetService<ConfidenceDecayPolicy>()));

        return services;
    }
}
