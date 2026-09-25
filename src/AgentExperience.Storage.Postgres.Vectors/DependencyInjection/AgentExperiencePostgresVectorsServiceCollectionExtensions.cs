using AgentExperience.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.DependencyInjection;

/// <summary>
/// Registers the pgvector embedding index, and optionally an embedding generator over a
/// <c>Microsoft.Extensions.AI</c> one, in a <see cref="IServiceCollection"/>. The adapter owns its
/// own registration, exactly as Core owns <c>AddAgentExperienceCore</c>, so a host wires them
/// together without either package knowing the other's concrete types.
/// </summary>
public static class AgentExperiencePostgresVectorsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresExperienceEmbeddingIndex"/> as the singleton
    /// <see cref="IExperienceEmbeddingIndex"/>, over an <see cref="NpgsqlDataSource"/> resolved from
    /// the container.
    /// </summary>
    /// <remarks>
    /// The host owns the data source's lifetime and the index never disposes it. The schema is not
    /// applied here: the extension and the embedding table live in this package's own
    /// <c>0004_add_experience_embeddings.sql</c>, applied at startup by
    /// <see cref="ExperienceVectorSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>
    /// <em>after</em> the base adapter's <c>ExperienceSchemaMigrator.MigrateAsync</c>. The two are
    /// separate calls on purpose: this one needs the privilege to create the <c>vector</c> extension,
    /// and a text-only host should never be made to have it. The dimension-specific HNSW index is
    /// separate again, and optional; see <see cref="ExperienceVectorIndexMaintenance"/>.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresEmbeddingIndex(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceEmbeddingIndex>(provider =>
            new PostgresExperienceEmbeddingIndex(
                provider.GetRequiredService<NpgsqlDataSource>(),
                onGrantsUnavailable: null,
                auditing: provider.GetService<ExperienceGrantAuditing>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceEmbeddingIndex"/> as the singleton
    /// <see cref="IExperienceEmbeddingIndex"/> over <paramref name="dataSource"/>, for a host that
    /// keeps its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the index opens connections from. Never disposed by the index.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresEmbeddingIndex(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // A factory rather than a ready-made instance, so a registered ExperienceGrantAuditing is picked
        // up however the two registrations are ordered. Still one singleton either way.
        services.TryAddSingleton<IExperienceEmbeddingIndex>(provider =>
            new PostgresExperienceEmbeddingIndex(
                dataSource,
                onGrantsUnavailable: null,
                auditing: provider.GetService<ExperienceGrantAuditing>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="AiExperienceEmbeddingGenerator"/> as the singleton
    /// <see cref="IExperienceEmbeddingGenerator"/> over an
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> resolved from the container.
    /// </summary>
    /// <remarks>
    /// Registered separately from the index because they are independent decisions: a host may index
    /// with one generator and query with another process, or use a deterministic generator in tests
    /// while keeping the real index. The model ID and dimension are resolved once, when the singleton
    /// is first created, so a generator that reports neither -- or an argument that contradicts what it
    /// does report -- fails there rather than mid-query.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="modelId">Optional. The model ID to use when the generator reports none. It may not contradict one the generator does report.</param>
    /// <param name="dimension">Optional. The dimension to use when the generator reports none. It may not contradict one the generator does report.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperienceEmbeddingGenerator(
        this IServiceCollection services,
        string? modelId = null,
        int? dimension = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceEmbeddingGenerator>(provider => new AiExperienceEmbeddingGenerator(
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            modelId,
            dimension));

        return services;
    }
}
