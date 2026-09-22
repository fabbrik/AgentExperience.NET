using AgentExperience.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AgentExperience.Storage.Postgres.DependencyInjection;

/// <summary>
/// Registers the PostgreSQL Experience Record store in a <see cref="IServiceCollection"/>. The
/// adapter owns its own registration, exactly as Core owns <c>AddAgentExperienceCore</c>, so a host
/// wires the two together without either package knowing the other's concrete types.
/// </summary>
public static class AgentExperiencePostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresExperienceRecordStore"/> as the singleton
    /// <see cref="IExperienceRecordStore"/>, over an <see cref="NpgsqlDataSource"/> resolved from the
    /// container.
    /// </summary>
    /// <remarks>
    /// The host owns the data source's lifetime and the store never disposes it. The schema is not
    /// applied here: call
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> once
    /// at startup.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceRecordStore>(provider =>
            new PostgresExperienceRecordStore(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceRecordStore"/> as the singleton
    /// <see cref="IExperienceRecordStore"/> over <paramref name="dataSource"/>, for a host that keeps
    /// its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the store opens connections from. Never disposed by the store.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresStore(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton<IExperienceRecordStore>(new PostgresExperienceRecordStore(dataSource));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantStore"/> as the singleton
    /// <see cref="IExperienceGrantStore"/>, over an <see cref="NpgsqlDataSource"/> resolved from the
    /// container, so a host can administer explicit sharing grants.
    /// </summary>
    /// <remarks>
    /// Registered separately from the store and the candidate source: a host that never shares
    /// anything across scopes needs no grant administration, and the reads that honour grants do so
    /// through their own SQL predicate whether or not this registration is present. The schema is not
    /// applied here -- the grant tables live in <c>0005_create_experience_grants.sql</c>, applied by
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> at
    /// startup like the rest of the schema.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceGrantStore>(provider =>
            new PostgresExperienceGrantStore(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantStore"/> as the singleton
    /// <see cref="IExperienceGrantStore"/> over <paramref name="dataSource"/>, for a host that keeps
    /// its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the grant store opens connections from. Never disposed by the store.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantStore(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton<IExperienceGrantStore>(new PostgresExperienceGrantStore(dataSource));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceCandidateSource"/> as the singleton
    /// <see cref="IExperienceCandidateSource"/>, over an <see cref="NpgsqlDataSource"/> resolved from
    /// the container, so Core's retrieval service has something to search.
    /// </summary>
    /// <remarks>
    /// Registered separately from the store: the two are independent ports, and a host that only
    /// writes experience does not need the search index. The schema is not applied here -- the search
    /// column and its index live in <c>0003_add_experience_search.sql</c>, applied by
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> at
    /// startup like the rest of the schema.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresCandidateSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceCandidateSource>(provider =>
            new PostgresExperienceCandidateSource(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceCandidateSource"/> as the singleton
    /// <see cref="IExperienceCandidateSource"/> over <paramref name="dataSource"/>, for a host that
    /// keeps its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the search opens connections from. Never disposed by the source.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresCandidateSource(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton<IExperienceCandidateSource>(new PostgresExperienceCandidateSource(dataSource));

        return services;
    }
}
