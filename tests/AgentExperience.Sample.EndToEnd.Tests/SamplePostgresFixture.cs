using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Starts one ephemeral stock <c>postgres</c> container for the collection and hands out an empty
/// database per test. Set <c>TESTCONTAINERS_RYUK_DISABLED=true</c> if Ryuk fails under a local
/// Docker setup.
/// </summary>
/// <remarks>
/// Stock <c>postgres</c>, not <c>pgvector/pgvector</c>, on purpose: the sample never touches
/// the vector path, and running it against an image with no pgvector is how the sample's README
/// claim that the base schema needs no extension and no superuser gets exercised rather than
/// asserted. The sample runs <c>ExperienceSchemaMigrator.MigrateAsync</c> itself, so this
/// fixture deliberately leaves each database unmigrated.
/// </remarks>
public sealed class SamplePostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(AgentExperience.Tests.Shared.PostgresTestImage.Stock).Build();
        await _container.StartAsync();
    }

    /// <summary>
    /// Creates an empty, unmigrated database in the shared container and returns its connection
    /// string. The database goes away with the container.
    /// </summary>
    /// <param name="purpose">A short name fragment identifying the test: 1 to 20 lower-case ASCII letters, digits, or underscores.</param>
    public async Task<string> CreateDatabaseAsync(string purpose)
    {
        Assert.InRange(purpose.Length, 1, 20);
        Assert.All(purpose, c => Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', $"Invalid purpose character '{c}'."));

        // "aes_" + <=20 + "_" + 32 hex = at most 57 bytes, inside PostgreSQL's 63-byte identifier limit.
        var name = $"aes_{purpose}_{Guid.NewGuid():N}";

        await using (var dataSource = NpgsqlDataSource.Create(_container!.GetConnectionString()))
        {
            // CREATE DATABASE takes no parameters and cannot run inside a transaction, so the
            // identifier is interpolated. Every character of it has just been checked above.
            await using var command = dataSource.CreateCommand($"CREATE DATABASE \"{name}\"");
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SamplePostgresCollection : ICollectionFixture<SamplePostgresFixture>
{
    public const string Name = "SamplePostgres";
}
