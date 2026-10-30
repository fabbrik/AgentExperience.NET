using AgentExperience.Abstractions;
using AgentExperience.Storage.Sqlite;
using AgentExperience.Storage.Sqlite.DependencyInjection;

// In the container's own namespace, beside AddAgentExperience, so a host needs no extra using for it.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Chooses SQLite storage for the one-call setup.</summary>
public static class AgentExperienceSqliteBuilderExtensions
{
    /// <summary>
    /// Stores experience in the SQLite database file at <paramref name="databasePath"/>, through
    /// <see cref="AgentExperienceSqliteServiceCollectionExtensions.AddAgentExperienceSqliteStorage"/>: durable, local, for a
    /// single agent on one machine. Its runs are stored too, so a run captured in one process can be finalized in a later one.
    /// </summary>
    /// <remarks>
    /// The schema is not migrated here unless <see cref="SqliteStorageOptions.MigrateSchemaOnFirstUse"/> is set: call
    /// <see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/> at startup, or opt in. Grants,
    /// encryption, row-level security and vectors are not provided; retrieval is text and failure signature only.
    /// </remarks>
    /// <typeparam name="TBuilder">The builder <c>AddAgentExperience</c> returned.</typeparam>
    /// <param name="builder">The builder <c>AddAgentExperience</c> returned.</param>
    /// <param name="databasePath">The database file. It is created when missing.</param>
    /// <param name="configure">Sets <see cref="SqliteStorageOptions"/>; called once, here.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">A storage was already chosen: a record store is already registered.</exception>
    public static TBuilder UseSqlite<TBuilder>(this TBuilder builder, string databasePath, Action<SqliteStorageOptions>? configure = null)
        where TBuilder : IAgentExperienceBuilder<IServiceCollection>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IExperienceRecordStore)))
        {
            throw new InvalidOperationException(
                "A storage was already chosen for AddAgentExperience (an IExperienceRecordStore is registered). Choose one storage, once: "
                + "UseInMemoryStorageForDevelopment(), UsePostgres(...) or UseSqlite(...).");
        }

        builder.Services.AddAgentExperienceSqliteStorage(databasePath, configure);
        return builder;
    }
}
