using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// In the container's own namespace, like other registration extensions, so a host needs no extra using for them.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The one-call setup for AgentExperience.NET with Microsoft Agent Framework.</summary>
public static class AgentExperienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything capture, injection and finalization need, with safe defaults, and returns a builder to
    /// choose the storage on. Then build the agent with <c>UseAgentExperience(services)</c> and give it
    /// <see cref="GetAgentExperienceContextProvider"/> as a context provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It registers Core (<see cref="AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceCore"/> with
    /// <see cref="AgentExperienceOptions.Sanitization"/> and <see cref="AgentExperienceOptions.CaptureLimits"/>),
    /// retrieval (<see cref="AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceRetrieval"/>), and the
    /// <see cref="ExperienceContextProvider"/>, each with <c>TryAdd</c>, so a service the host registered first is kept.
    /// </para>
    /// <para>
    /// The storage is chosen on the returned builder. Without one, building an agent with <c>UseAgentExperience</c> or
    /// resolving the context provider throws <see cref="InvalidOperationException"/> naming the choices. PostgreSQL
    /// migrations stay an explicit step for the database owner role; nothing here migrates a schema.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Sets the options; called once, here. <see cref="AgentExperienceOptions.ResolveIdentity"/> is required.</param>
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

        services.AddSingleton(new AgentExperienceRegistration(options));

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

        services.AddAgentExperienceCore(options.Sanitization, options.CaptureLimits);
        ApplyFinalizationOptions(services, options);
        services.AddAgentExperienceRetrieval();
        services.TryAddSingleton(provider => AgentExperienceRegistration.From(provider).CreateContextProvider(provider));
        services.TryAddSingleton<AgentExperienceLifetimes>();

        return new AgentExperienceBuilder(services);
    }

    /// <summary>
    /// The <see cref="ExperienceContextProvider"/> the one-call setup registered, for
    /// <c>ChatClientAgentOptions.AIContextProviders</c>.
    /// </summary>
    /// <remarks>
    /// MAF's agent builder can add only a message-level context provider, which sees the caller's input but not the
    /// chat client agent's session and history the provider works with, so the provider goes on the agent's options:
    /// <c>new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })</c>.
    /// </remarks>
    /// <param name="services">The service provider built from the collection <see cref="AddAgentExperience"/> was called on.</param>
    /// <returns>The context provider.</returns>
    /// <exception cref="InvalidOperationException"><see cref="AddAgentExperience"/> was not called, or no storage was chosen.</exception>
    public static ExperienceContextProvider GetAgentExperienceContextProvider(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AgentExperienceRegistration.From(services);
        return services.GetRequiredService<ExperienceContextProvider>();
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
