using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using AgentExperience.Storage.Postgres.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

// In the container's own namespace, beside AddAgentExperience, so a host needs no extra using for it.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Chooses PostgreSQL storage for the one-call setup.</summary>
public static class AgentExperiencePostgresBuilderExtensions
{
    /// <summary>
    /// Stores experience in PostgreSQL: registers a data source for <paramref name="connectionString"/>, owned and
    /// disposed by the container, and the record store and candidate source over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connect as the <b>application role</b>. The schema is not migrated here: apply it on every deploy, as the database
    /// owner role, with <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> and
    /// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync"/> (see the deployment guide).
    /// </para>
    /// <para>
    /// The record store and candidate source are added with <c>TryAdd</c>, so ones the host registered first are kept.
    /// Grants, the grant access log, reuse feedback, encryption and vectors are registered with their own
    /// <c>AddAgentExperiencePostgres…</c> calls, which find this data source.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBuilder">The builder <c>AddAgentExperience</c> returned.</typeparam>
    /// <param name="builder">The builder <c>AddAgentExperience</c> returned.</param>
    /// <param name="connectionString">The application role's connection string.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is blank or malformed.</exception>
    /// <exception cref="InvalidOperationException">
    /// A storage was already chosen, or an <see cref="NpgsqlDataSource"/> is already registered: use <see cref="UsePostgres{TBuilder}(TBuilder, NpgsqlDataSource)"/>
    /// with it instead, so the connection string passed here is not silently ignored.
    /// </exception>
    public static TBuilder UsePostgres<TBuilder>(this TBuilder builder, string connectionString)
        where TBuilder : IAgentExperienceBuilder<IServiceCollection>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Parsed now, so a malformed string fails at registration rather than on the first query.
        _ = new NpgsqlConnectionStringBuilder(connectionString);

        var services = builder.Services;
        EnsureNoStorageChosen(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)))
        {
            throw new InvalidOperationException(
                $"An {nameof(NpgsqlDataSource)} is already registered, so the connection string passed to UsePostgres would be ignored. "
                + $"Pass that data source instead: UsePostgres(dataSource).");
        }

        // A factory, so the container owns the data source and disposes it with itself.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddAgentExperiencePostgresStore();
        services.AddAgentExperiencePostgresCandidateSource();
        return builder;
    }

    /// <summary>
    /// Stores experience in PostgreSQL through <paramref name="dataSource"/>, which the host owns and disposes:
    /// registers it and the record store and candidate source over it.
    /// </summary>
    /// <remarks>
    /// Connect as the <b>application role</b>. The schema is not migrated here; see
    /// <see cref="UsePostgres{TBuilder}(TBuilder, string)"/>.
    /// </remarks>
    /// <typeparam name="TBuilder">The builder <c>AddAgentExperience</c> returned.</typeparam>
    /// <param name="builder">The builder <c>AddAgentExperience</c> returned.</param>
    /// <param name="dataSource">The application role's data source.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A storage was already chosen, or a different data source is already registered.</exception>
    public static TBuilder UsePostgres<TBuilder>(this TBuilder builder, NpgsqlDataSource dataSource)
        where TBuilder : IAgentExperienceBuilder<IServiceCollection>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);

        var services = builder.Services;
        EnsureNoStorageChosen(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)
            && !ReferenceEquals(descriptor.ImplementationInstance, dataSource)))
        {
            throw new InvalidOperationException(
                $"A different {nameof(NpgsqlDataSource)} is already registered, so the stores and the rest of the container would "
                + "connect through two data sources. Pass the registered one, or register only this one.");
        }

        services.TryAddSingleton(dataSource);
        services.AddAgentExperiencePostgresStore(dataSource);
        services.AddAgentExperiencePostgresCandidateSource(dataSource);
        return builder;
    }

    /// <summary>One storage per setup: a second choice, of either kind, would leave one of them unused or mixed.</summary>
    private static void EnsureNoStorageChosen(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IExperienceRecordStore)))
        {
            throw new InvalidOperationException(
                "A storage was already chosen for AddAgentExperience (an IExperienceRecordStore is registered). Choose one storage, once: "
                + "UseInMemoryStorageForDevelopment() or UsePostgres(...).");
        }
    }
}
