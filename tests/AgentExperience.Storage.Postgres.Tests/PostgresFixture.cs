using AgentExperience.Storage.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Starts one ephemeral <c>pgvector/pgvector:pg16</c> container for the whole collection, applies the
/// package's embedded schema scripts in order, and tears the container down afterwards. Set
/// <c>TESTCONTAINERS_RYUK_DISABLED=true</c> if Ryuk fails under a local Docker setup.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());

        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames)
        {
            await using var command = _dataSource.CreateCommand(PostgresExperienceRecordSchema.GetScript(scriptName));
            await command.ExecuteNonQueryAsync();
        }
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
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
