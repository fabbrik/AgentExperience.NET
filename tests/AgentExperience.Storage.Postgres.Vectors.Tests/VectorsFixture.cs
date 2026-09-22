using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Starts one ephemeral <c>pgvector/pgvector:pg16</c> container for the whole collection, migrates its
/// default database with <see cref="ExperienceSchemaMigrator"/> and <em>then</em> with this package's
/// own <see cref="ExperienceVectorSchemaMigrator"/>, and tears the container down afterwards. The two
/// calls are separate exactly as a host's are: the base schema needs no extension privilege, and only
/// this second call creates the <c>vector</c> extension. Set <c>TESTCONTAINERS_RYUK_DISABLED=true</c>
/// if Ryuk fails under a local Docker setup.
/// </summary>
public sealed class VectorsFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
        await _container.StartAsync();

        // Deliberately a plain data source: no UseVector() call. The adapter must work on whatever
        // data source the host built, and these tests would not notice if it had silently started
        // depending on the Pgvector type mapping being registered.
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());

        await ExperienceSchemaMigrator.MigrateAsync(_dataSource, CancellationToken.None);
        await ExperienceVectorSchemaMigrator.MigrateAsync(_dataSource, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class VectorsCollection : ICollectionFixture<VectorsFixture>
{
    public const string Name = "PostgresVectors";
}

/// <summary>
/// A deterministic <see cref="IExperienceEmbeddingGenerator"/>. Every integration test in this project
/// embeds through this rather than a model: there are no credentials, no network, and no variance, so
/// a semantic-retrieval assertion is a statement about the adapter rather than about a provider.
/// </summary>
/// <remarks>
/// The vector is a bag-of-topics projection: each configured topic owns one axis, and a text scores on
/// an axis for every one of that topic's words it contains. Two texts that share <em>no</em> words but
/// belong to the same topic therefore point the same way, which is exactly the property a semantic
/// match has to have for the test to mean anything -- and a SHA-256 of the text is folded into the
/// remaining axis so unrelated texts do not accidentally coincide.
/// </remarks>
internal sealed class TopicEmbeddingGenerator : IExperienceEmbeddingGenerator
{
    private static readonly string[][] Topics =
    [
        ["refund", "chargeback", "reimburse", "money", "payment", "invoice", "billing"],
        ["deadlock", "lock", "contention", "blocked", "stuck", "concurrency", "timeout"],
        ["deploy", "release", "rollback", "pipeline", "build", "ship"],
    ];

    public string ModelId { get; init; } = "topic-embed-v1";

    public int Dimension => Topics.Length + 1;

    /// <summary>When set, every call throws this instead of embedding.</summary>
    public Exception? Throws { get; init; }

    /// <summary>Every text this generator was asked to embed, in order.</summary>
    public List<string> Requests { get; } = [];

    public static ReadOnlyMemory<float> VectorFor(string text)
    {
        var words = text.ToLowerInvariant().Split(
            [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '(', ')', '/'],
            StringSplitOptions.RemoveEmptyEntries);

        var vector = new float[Topics.Length + 1];
        for (var topic = 0; topic < Topics.Length; topic++)
        {
            foreach (var word in words)
            {
                if (Topics[topic].Contains(word, StringComparer.Ordinal))
                {
                    vector[topic] += 1f;
                }
            }
        }

        // A small, deterministic idiosyncrasy per text, so two unrelated texts never come out exactly
        // parallel just because neither matched a topic.
        vector[^1] = SHA256.HashData(Encoding.UTF8.GetBytes(text))[0] / 255f * 0.25f;

        // Normalized, so cosine distance is a pure direction comparison and the relevance a test reads
        // back does not depend on how many words a summary happened to contain.
        var magnitude = MathF.Sqrt(vector.Sum(component => component * component));
        if (magnitude > 0f)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }

        return vector;
    }

    public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(text);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Throws is not null ? throw Throws : Task.FromResult(VectorFor(text));
    }
}

/// <summary>A generator that answers with a fixed-width vector of a chosen model, for the mismatch tests.</summary>
internal sealed class FixedEmbeddingGenerator : IExperienceEmbeddingGenerator
{
    public required string ModelId { get; init; }

    public required int Dimension { get; init; }

    public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        var vector = new float[Dimension];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        for (var i = 0; i < Dimension; i++)
        {
            vector[i] = ((hash[i % hash.Length] / 255f) * 2f) - 1f;
        }

        return Task.FromResult<ReadOnlyMemory<float>>(vector);
    }
}
