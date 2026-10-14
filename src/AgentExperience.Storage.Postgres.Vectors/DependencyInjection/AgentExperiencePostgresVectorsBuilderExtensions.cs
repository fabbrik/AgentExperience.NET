using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres.Vectors;
using AgentExperience.Storage.Postgres.Vectors.DependencyInjection;
using Npgsql;

// In the container's own namespace, beside AddAgentExperience and UsePostgres, so a host needs no extra using for it.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the pgvector embedding index to a PostgreSQL storage chosen with the one-call setup.</summary>
public static class AgentExperiencePostgresVectorsBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresExperienceEmbeddingIndex"/> over the data source <c>UsePostgres</c> registered, so
    /// that, when the container also has a <c>Microsoft.Extensions.AI</c> <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>,
    /// the one-call setup embeds each stored lesson and retrieves by meaning as well as by words. Call it after
    /// <c>UsePostgres</c>: <c>.UsePostgres(connectionString).WithPostgresVectors()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The vector schema is not applied here.</b> As the database owner role, apply it on every deploy, after the base
    /// schema: <see cref="ExperienceVectorSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> after
    /// <c>ExperienceSchemaMigrator.MigrateAsync</c>, then the application role's privileges (the deployment guide has the
    /// order). Creating the <c>vector</c> extension needs a privilege a text-only host never needs, which is why it is a
    /// separate step and a separate package. Until the schema exists, retrieval falls back to text alone and says so on
    /// every result (<c>VectorSearchFailed</c>), and indexing reports each failed write; nothing fails a run.
    /// </para>
    /// <para>
    /// Without this call, a PostgreSQL setup stays text-only even with an embedding generator registered, and the one-call
    /// setup reports that once. The index is added with <c>TryAdd</c>, so one the host registered first is kept.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBuilder">The builder <c>AddAgentExperience</c> returned.</typeparam>
    /// <param name="builder">The builder, after <c>UsePostgres</c>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="NpgsqlDataSource"/> is registered: call <c>UsePostgres</c> first.</exception>
    public static TBuilder WithPostgresVectors<TBuilder>(this TBuilder builder)
        where TBuilder : IAgentExperienceBuilder<IServiceCollection>
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)))
        {
            throw new InvalidOperationException(
                $"WithPostgresVectors() needs the {nameof(NpgsqlDataSource)} UsePostgres registers, and none is registered. "
                + "Call it after UsePostgres: .UsePostgres(connectionString).WithPostgresVectors().");
        }

        services.AddAgentExperiencePostgresEmbeddingIndex();
        return builder;
    }
}
