using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

// In the container's own namespace, like other registration extensions, so a host needs no extra using for them.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The one-call setup for AgentExperience.NET with Microsoft Agent Framework.</summary>
public static class AgentExperienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything capture, injection and finalization need, with safe defaults, and returns a builder to
    /// choose the storage on. Then build the agent with <c>UseAgentExperience(services)</c>, which wires capture and
    /// injection both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It registers Core (<see cref="AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceCore"/> with
    /// <see cref="AgentExperienceOptions.Sanitization"/> and <see cref="AgentExperienceOptions.CaptureLimits"/>),
    /// retrieval (<see cref="AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceRetrieval"/>), and the
    /// <see cref="ExperienceContextProvider"/> and the read-only <see cref="AgentExperienceLessons"/> view, each with <c>TryAdd</c>, so a service the host registered first is kept.
    /// </para>
    /// <para>
    /// When the container has a <c>Microsoft.Extensions.AI</c> <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>,
    /// registered before or after this call, retrieval is also semantic: the generator, adapted, embeds each task text, and
    /// finalization embeds each committed record. Without one, retrieval is text-only, as before. See
    /// <see cref="AgentExperienceOptions.Embeddings"/>.
    /// </para>
    /// <para>
    /// The storage is chosen on the returned builder. Without one, building an agent with <c>UseAgentExperience</c> or
    /// resolving the context provider throws <see cref="InvalidOperationException"/> naming the choices. PostgreSQL
    /// migrations stay an explicit step for the database owner role; nothing here migrates a schema.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Sets the options; called once, here. <see cref="AgentExperienceOptions.ResolveIdentity"/> is required (<see cref="AgentExperienceOptions.UseDevelopmentIdentity"/> sets it for development).</param>
    /// <returns>A builder to choose the storage on.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The options are invalid, for example <see cref="AgentExperienceOptions.ResolveIdentity"/> is not set.</exception>
    /// <exception cref="InvalidOperationException">
    /// This was already called on <paramref name="services"/>, or <see cref="AgentExperienceOptions.TimeProvider"/> differs
    /// from a <see cref="TimeProvider"/> already registered.
    /// </exception>
    public static IAgentExperienceBuilder AddAgentExperience(this IServiceCollection services, Action<AgentExperienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(AgentExperienceRegistration)))
        {
            throw new InvalidOperationException(
                $"{nameof(AddAgentExperience)} was already called on this service collection, so its options are already set; call it once.");
        }

        var options = new AgentExperienceOptions();
        configure(options);
        options.Validate();

        // Resolved through a factory, so the development identity's environment check runs when the container first
        // hands the registration out, the moment the in-memory store checks its own.
        var registration = new AgentExperienceRegistration(options);
        services.AddSingleton(provider =>
        {
            registration.Bind(provider);
            return registration;
        });

        if (options.UsesDevelopmentIdentity)
        {
            // And when a Generic Host starts, as the in-memory store checks its own.
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new DevelopmentIdentityEnvironmentCheck(registration, provider));
        }

        // Before retrieval, which registers the system clock when no clock is registered yet.
        if (options.TimeProvider is { } clock)
        {
            if (services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TimeProvider)) is { } registered
                && !ReferenceEquals(registered.ImplementationInstance, clock))
            {
                throw new InvalidOperationException(
                    $"{nameof(AgentExperienceOptions)}.{nameof(AgentExperienceOptions.TimeProvider)} differs from the TimeProvider already registered, "
                    + "so capture, retrieval and injection would run on two clocks. Leave the option unset to use the registered one, or pass that same instance.");
            }

            services.TryAddSingleton(clock);
        }

        // One decision about embeddings per container. Core is handed its indexing service and vector channel through
        // its own constructors, so nothing registered here ever resolves to null.
        services.AddSingleton(provider => new AgentExperienceEmbeddingsRuntime(options, provider));
        services.AddAgentExperienceCoreWithIndexing(
            options.Sanitization,
            options.CaptureLimits,
            provider => provider.GetRequiredService<AgentExperienceEmbeddingsRuntime>().Indexing());
        ApplyFinalizationOptions(services, options);
        services.AddAgentExperienceRetrievalWithVectorChannel(
            policy: null,
            weights: null,
            provider => provider.GetRequiredService<AgentExperienceEmbeddingsRuntime>().VectorChannel());
        services.TryAddSingleton(provider => AgentExperienceRegistration.From(provider).CreateContextProvider(provider));
        services.TryAddSingleton<AgentExperienceLifetimes>();
        services.TryAddSingleton(provider =>
        {
            var registration = AgentExperienceRegistration.From(provider);
            return new AgentExperienceLessons(
                provider.GetRequiredService<IExperienceRecordStore>(),
                provider.GetRequiredService<ExperienceContextProvider>(),
                registration.ClockFrom(provider),
                registration);
        });

        return new AgentExperienceBuilder(services);
    }

    /// <summary>
    /// The <see cref="ExperienceContextProvider"/> the one-call setup registered, for
    /// <c>ChatClientAgentOptions.AIContextProviders</c>.
    /// </summary>
    /// <remarks>
    /// Optional with <c>UseAgentExperience</c>, which wires injection at the agent level when the agent was not given
    /// this provider, and over a chat client agent sees there the chat history the agent replays and the tools a run
    /// adds, as on the agent's options. On the agent's options the provider also sees what another context provider
    /// there adds, and the history exactly as the agent's reducer and filter present it:
    /// <c>new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })</c>.
    /// The agent then gets exactly one block either way.
    /// </remarks>
    /// <param name="services">The service provider built from the collection <see cref="AddAgentExperience"/> was called on.</param>
    /// <returns>The context provider.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AddAgentExperience"/> was not called, no storage was chosen, or
    /// <see cref="AgentExperienceOptions.UseDevelopmentIdentity"/> is used outside a development or test environment.
    /// </exception>
    public static ExperienceContextProvider GetAgentExperienceContextProvider(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AgentExperienceRegistration.From(services);
        return services.GetRequiredService<ExperienceContextProvider>();
    }

    /// <summary>
    /// Wraps another memory provider (Mem0, Zep, Letta, the host's own) in an <see cref="ExperienceHoldoutContextProvider"/>
    /// over the one-call setup's store and clock, for <c>ChatClientAgentOptions.AIContextProviders</c>, so its benefit can
    /// be measured with <see cref="AgentExperienceBenefit.EstimateProviderAsync"/>.
    /// </summary>
    /// <remarks>
    /// <code>
    /// AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
    /// {
    ///     AIContextProviders = [services.UseMeasuredMemory(mem0Provider, "mem0")],
    /// }).AsBuilder().UseAgentExperience(services).Build();
    /// </code>
    /// A run is recorded under the identity <c>UseAgentExperience</c> resolved for it, in the scope it is captured in.
    /// Without <paramref name="holdout"/> the wrapper takes the injection holdout's rate and salt
    /// (<see cref="ExperienceInjectionOptions.Holdout"/>, off by default); the provider's key is mixed into the salt, so
    /// its arms are drawn independently of the lessons' either way.
    /// </remarks>
    /// <param name="services">The service provider built from the collection <see cref="AddAgentExperience"/> was called on.</param>
    /// <param name="memory">The memory provider to measure.</param>
    /// <param name="providerKey">The key its assignments are recorded and estimated under; see <see cref="ProviderHoldoutAssignment.IsValidProviderKey"/>.</param>
    /// <param name="holdout">The rate and salt; <see langword="null"/> for the injection holdout's.</param>
    /// <returns>The wrapping provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="memory"/> or <paramref name="providerKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="providerKey"/> is malformed, or the holdout's salt is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The holdout's rate is outside [0, 1].</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AddAgentExperience"/> was not called, no storage was chosen, or
    /// <see cref="AgentExperienceOptions.UseDevelopmentIdentity"/> is used outside a development or test environment.
    /// </exception>
    public static ExperienceHoldoutContextProvider UseMeasuredMemory(
        this IServiceProvider services,
        AIContextProvider memory,
        string providerKey,
        ExperienceHoldoutOptions? holdout = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(providerKey);

        var registration = AgentExperienceRegistration.From(services);
        AgentExperienceRegistration.EnsureStorage(services);
        return new ExperienceHoldoutContextProvider(
            memory,
            providerKey,
            holdout ?? services.GetRequiredService<ExperienceContextProvider>().HoldoutOptions,
            services.GetRequiredService<IExperienceRecordStore>(),
            authorize: null,
            registration.ClockFrom(services));
    }

    /// <summary>
    /// The read-only lessons view the one-call setup registered: what is stored in a scope, and the block an agent there
    /// would be shown for each lesson. Serve it only behind the host's own authorization (see <see cref="AgentExperienceLessons"/>).
    /// </summary>
    /// <param name="services">The service provider built from the collection <see cref="AddAgentExperience"/> was called on.</param>
    /// <returns>The lessons view.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AddAgentExperience"/> was not called, no storage was chosen, or
    /// <see cref="AgentExperienceOptions.UseDevelopmentIdentity"/> is used outside a development or test environment.
    /// </exception>
    public static AgentExperienceLessons GetAgentExperienceLessons(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AgentExperienceRegistration.From(services);
        AgentExperienceRegistration.EnsureStorage(services);
        return services.GetRequiredService<AgentExperienceLessons>();
    }

    /// <summary>
    /// Carries <see cref="AgentExperienceOptions.ReuseEvidence"/> and <see cref="AgentExperienceOptions.ContradictOnFailure"/>
    /// onto the registered <see cref="ExperienceFinalizationOptions"/>. At their defaults nothing is registered or replaced.
    /// </summary>
    private static void ApplyFinalizationOptions(IServiceCollection services, AgentExperienceOptions options)
    {
        if (options.ReuseEvidence == ReuseEvidenceMode.Off && !options.ContradictOnFailure)
        {
            return;
        }

        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(ExperienceFinalizationOptions) && !descriptor.IsKeyedService)
            .ToList();
        var registered = registrations.LastOrDefault();
        ExperienceFinalizationOptions baseline;
        if (registered is null)
        {
            baseline = ExperienceFinalizationOptions.Default;
        }
        else if (registered.ImplementationInstance is ExperienceFinalizationOptions instance)
        {
            baseline = instance;
        }
        else
        {
            throw new InvalidOperationException(
                $"{nameof(AgentExperienceOptions)}.{nameof(AgentExperienceOptions.ReuseEvidence)} or {nameof(AgentExperienceOptions.ContradictOnFailure)} is set, "
                + $"but an {nameof(ExperienceFinalizationOptions)} is already registered through a factory or a type, which this cannot carry them onto. "
                + "Register it as an instance, or set the two on it instead.");
        }

        foreach (var registration in registrations)
        {
            services.Remove(registration);
        }

        services.AddSingleton(baseline with
        {
            ReuseEvidence = options.ReuseEvidence,
            ContradictOnFailure = options.ContradictOnFailure,
        });
    }

    private sealed class AgentExperienceBuilder(IServiceCollection services) : IAgentExperienceBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
