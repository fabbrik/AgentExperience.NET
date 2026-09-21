using System.Globalization;
using AgentExperience.Abstractions;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors;

/// <summary>
/// The explicit, out-of-band creation of the approximate-nearest-neighbour index on
/// <c>agent_experience.experience_embeddings</c>. It is a separate, deliberate call rather than part
/// of the schema migration because an HNSW index needs a <em>dimension</em>, and the dimension
/// belongs to whichever embedding model a host configured -- something no shipped migration can know.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the column is unconstrained and the index is not.</b> <c>0004</c> declares
/// <c>embedding vector</c> with no type modifier, so one deployment can hold 384-wide vectors and
/// another 1536-wide ones without a schema fork. pgvector's opclasses, however, refuse a column
/// without a dimension. The index is therefore built over the expression
/// <c>embedding::vector(n)</c> and is <em>partial</em> on <c>dimension = n</c>: the predicate is what
/// makes the cast safe, because the build only ever touches rows that really are that wide. Several
/// dimensions can coexist, each with its own index.
/// </para>
/// <para>
/// <b>It is optional.</b> Every search is correct without it -- pgvector falls back to an exact scan,
/// which is what a small deployment wants anyway. The index changes latency, and it also makes a
/// search <em>approximate</em>: HNSW may miss a true nearest neighbour. Create it when a scope holds
/// enough embeddings for an exact scan to hurt.
/// </para>
/// <para>
/// <b>It matches the search's own expression.</b> The distance
/// <see cref="PostgresExperienceEmbeddingIndex"/> computes is cosine
/// (<c>&lt;=&gt;</c>, <c>vector_cosine_ops</c>) over exactly <c>embedding::vector(n)</c>, with
/// <c>dimension = n</c> in the predicate. Changing either side alone silently stops the index being
/// used.
/// </para>
/// <para>
/// This runs DDL, so it needs a connection with rights to create an index on the schema. Building an
/// HNSW index over many rows takes minutes and holds a lock that blocks writes to the table for its
/// duration -- run it from a maintenance path, never from request handling.
/// </para>
/// </remarks>
public static class ExperienceVectorIndexMaintenance
{
    /// <summary>The name of the dimension-specific index, so a host can find, monitor, or drop it by name.</summary>
    /// <param name="dimension">The vector width the index covers.</param>
    /// <returns>The index name, unqualified.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimension"/> is not between 1 and <see cref="ExperienceEmbeddingDescriptor.MaxDimension"/>.</exception>
    public static string IndexNameFor(int dimension) =>
        string.Create(CultureInfo.InvariantCulture, $"ix_experience_embeddings_hnsw_{Ensure(dimension)}");

    /// <summary>
    /// Creates the cosine HNSW index for <paramref name="dimension"/>-wide vectors if it does not
    /// already exist. Idempotent: calling it again once the index exists does nothing.
    /// </summary>
    /// <param name="dataSource">The host-owned data source. Never disposed here.</param>
    /// <param name="dimension">The vector width to index, which must be the dimension of the model the host embeds with.</param>
    /// <param name="cancellationToken">Cancels the operation. Cancelling does not necessarily stop an index build already running on the server.</param>
    /// <returns>A task that completes once the index exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimension"/> is not between 1 and <see cref="ExperienceEmbeddingDescriptor.MaxDimension"/>.</exception>
    /// <exception cref="ExperienceStoreException">The index could not be created.</exception>
    public static async Task EnsureHnswIndexAsync(
        NpgsqlDataSource dataSource,
        int dimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var width = Ensure(dimension).ToString(CultureInfo.InvariantCulture);

        // The dimension is an int this method has already bounded, so nothing caller-controlled
        // reaches the statement text; a pgvector type modifier can never be a parameter anyway.
        var sql =
            $"CREATE INDEX IF NOT EXISTS {IndexNameFor(dimension)} " +
            $"ON {PostgresExperienceEmbeddingIndex.Table} " +
            $"USING hnsw ((embedding::vector({width})) vector_cosine_ops) " +
            $"WHERE dimension = {width}";

        try
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "embedding index creation", cancellationToken);
        }
    }

    /// <summary>
    /// Drops the dimension-specific index if it exists, for a host retiring a model. Dropping it never
    /// changes a search's results, only its latency and whether it is approximate.
    /// </summary>
    /// <param name="dataSource">The host-owned data source. Never disposed here.</param>
    /// <param name="dimension">The vector width whose index to drop.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the index is gone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimension"/> is not between 1 and <see cref="ExperienceEmbeddingDescriptor.MaxDimension"/>.</exception>
    /// <exception cref="ExperienceStoreException">The index could not be dropped.</exception>
    public static async Task DropHnswIndexAsync(
        NpgsqlDataSource dataSource,
        int dimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var name = IndexNameFor(dimension);

        try
        {
            await using var command = dataSource.CreateCommand(
                $"DROP INDEX IF EXISTS {PostgresExperienceRecordSchema.SchemaName}.{name}");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "embedding index removal", cancellationToken);
        }
    }

    private static int Ensure(int dimension) =>
        dimension is >= 1 and <= ExperienceEmbeddingDescriptor.MaxDimension
            ? dimension
            : throw new ArgumentOutOfRangeException(
                nameof(dimension),
                dimension,
                $"The vector dimension must be between 1 and {ExperienceEmbeddingDescriptor.MaxDimension}.");
}
