using AgentExperience.Abstractions;
using AgentExperience.Storage.InMemory;
using AgentExperience.Storage.InMemory.DependencyInjection;

// In the container's own namespace, beside AddAgentExperience, so a host needs no extra using for it.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Chooses the in-memory storage for the one-call setup. <b>For development and tests only.</b></summary>
public static class AgentExperienceInMemoryBuilderExtensions
{
    /// <summary>
    /// Stores experience in memory, through
    /// <see cref="AgentExperienceInMemoryServiceCollectionExtensions.AddAgentExperienceInMemoryStorageForDevelopment"/>.
    /// <b>For development and tests only</b>: everything is lost when the process ends, and none of the PostgreSQL
    /// guarantees apply.
    /// </summary>
    /// <remarks>
    /// The same guard applies: the stores refuse to run outside a <c>Development</c>, <c>Test</c> or <c>Testing</c>
    /// environment unless <see cref="InMemoryStorageOptions.AllowProductionEnvironment"/> is set, and registering them
    /// beside another record store throws.
    /// </remarks>
    /// <typeparam name="TBuilder">The builder <c>AddAgentExperience</c> returned.</typeparam>
    /// <param name="builder">The builder <c>AddAgentExperience</c> returned.</param>
    /// <param name="configure">Sets <see cref="InMemoryStorageOptions"/>; called once, here.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A storage was already chosen: a record store is already registered.</exception>
    public static TBuilder UseInMemoryStorageForDevelopment<TBuilder>(this TBuilder builder, Action<InMemoryStorageOptions>? configure = null)
        where TBuilder : IAgentExperienceBuilder<IServiceCollection>
    {
        ArgumentNullException.ThrowIfNull(builder);

        // One storage per setup: a second choice, of either kind, would leave one of them unused or mixed.
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IExperienceRecordStore)))
        {
            throw new InvalidOperationException(
                "A storage was already chosen for AddAgentExperience (an IExperienceRecordStore is registered). Choose one storage, once: "
                + "UseInMemoryStorageForDevelopment() or UsePostgres(...).");
        }

        builder.Services.AddAgentExperienceInMemoryStorageForDevelopment(configure);
        return builder;
    }
}
