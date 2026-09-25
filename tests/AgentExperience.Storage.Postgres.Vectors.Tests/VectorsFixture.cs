using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Starts one ephemeral <c>pgvector/pgvector</c> container for the whole collection, at the PostgreSQL major
/// <see cref="AgentExperience.Tests.Shared.PostgresTestImage"/> selects (16 unless
/// <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> says otherwise), and sets up the supported two-role deployment in it: a superuser creates the <c>vector</c> extension and grants the
/// owner role <c>SET</c> on the two purge markers, the owner role (no superuser) runs
/// <see cref="ExperienceSchemaMigrator"/> and <em>then</em> this package's own
/// <see cref="ExperienceVectorSchemaMigrator"/>, exactly as a host's two calls are separate, and finally
/// applies the application role's privileges -- after both migrators, so the embedding table is covered.
/// <see cref="DataSource"/> connects as the application role; <see cref="OwnerDataSource"/> as the owner,
/// for index maintenance (building an index needs ownership). Set
/// <c>TESTCONTAINERS_RYUK_DISABLED=true</c> if Ryuk fails under a local Docker setup.
/// </summary>
public sealed class VectorsFixture : IAsyncLifetime
{
    public const string OwnerRoleName = "aen_vectors_owner";
    public const string ApplicationRoleName = "aen_vectors_app";
    private const string StoreDatabase = "aen_vectors_store";
    private const string RolePassword = "aen-role-password";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _owner;
    private NpgsqlDataSource? _dataSource;

    /// <summary>The fixture database, connecting as the application role.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>The fixture database, connecting as the owner role that ran both migrators.</summary>
    public NpgsqlDataSource OwnerDataSource => _owner ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(AgentExperience.Tests.Shared.PostgresTestImage.Pgvector).Build();
        await _container.StartAsync();

        await using (var superuser = NpgsqlDataSource.Create(_container.GetConnectionString()))
        {
            foreach (var sql in new[]
            {
                $"CREATE ROLE {OwnerRoleName} LOGIN PASSWORD '{RolePassword}'",
                $"CREATE ROLE {ApplicationRoleName} LOGIN PASSWORD '{RolePassword}'",
                $"CREATE DATABASE {StoreDatabase} OWNER {OwnerRoleName}",
                "GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized " +
                $"TO {OwnerRoleName}",
            })
            {
                await using var command = superuser.CreateCommand(sql);
                await command.ExecuteNonQueryAsync();
            }
        }

        // pgvector is not a trusted extension, so only a superuser can create it; the vectors migrator's
        // CREATE EXTENSION IF NOT EXISTS is then a no-op for the owner.
        await using (var superuserInStore = NpgsqlDataSource.Create(ConnectionString(username: null)))
        await using (var extension = superuserInStore.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
        {
            await extension.ExecuteNonQueryAsync();
        }

        // Deliberately plain data sources: no UseVector() call. The adapter must work on whatever
        // data source the host built, and these tests would not notice if it had silently started
        // depending on the Pgvector type mapping being registered.
        _owner = NpgsqlDataSource.Create(ConnectionString(OwnerRoleName));
        _dataSource = NpgsqlDataSource.Create(ConnectionString(ApplicationRoleName));

        await ExperienceSchemaMigrator.MigrateAsync(_owner, CancellationToken.None);
        await ExperienceVectorSchemaMigrator.MigrateAsync(_owner, CancellationToken.None);
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _owner,
            new ExperienceApplicationRoleOptions(ApplicationRoleName) { AllowErasure = true, AllowAccessLogPurge = true, AllowSealing = true },
            CancellationToken.None);
    }

    private string ConnectionString(string? username)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = StoreDatabase };
        if (username is not null)
        {
            builder.Username = username;
            builder.Password = RolePassword;
        }

        return builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_owner is not null)
        {
            await _owner.DisposeAsync();
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
