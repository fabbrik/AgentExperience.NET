using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Starts one ephemeral <c>pgvector/pgvector:pg16</c> container for the whole collection, migrates its
/// default database with <see cref="ExperienceSchemaMigrator"/>, and tears the container down
/// afterwards. Migrator tests create their own databases in the same container through
/// <see cref="CreateDatabaseAsync"/>. Set <c>TESTCONTAINERS_RYUK_DISABLED=true</c> if Ryuk fails under
/// a local Docker setup.
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

        await ExperienceSchemaMigrator.MigrateAsync(_dataSource, CancellationToken.None);
    }

    /// <summary>
    /// Creates a login role in the shared container with only the privileges <paramref name="grants"/>
    /// names, and returns a data source connecting as it. The caller owns the data source and disposes
    /// it; the role goes away with the container.
    /// </summary>
    /// <remarks>
    /// For privilege tests -- proving that a role which is <em>not</em> the owner cannot reach an
    /// operation, which the owning fixture connection can never prove about itself.
    /// </remarks>
    /// <param name="purpose">A short name fragment: 1 to 20 lower-case ASCII letters, digits, or underscores.</param>
    /// <param name="grants">Statements to run as the owner after the role exists, each naming the role as <c>{role}</c>.</param>
    public async Task<NpgsqlDataSource> CreateRoleAsync(string purpose, params string[] grants)
    {
        Assert.InRange(purpose.Length, 1, 20);
        Assert.All(purpose, c => Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', $"Invalid purpose character '{c}'."));

        var name = $"aen_{purpose}_{Guid.NewGuid():N}";
        const string Password = "aen-role-password";

        // CREATE ROLE takes no parameters, so the identifier is interpolated; every character of it has
        // just been checked against the allowlist above.
        await using (var command = DataSource.CreateCommand($"CREATE ROLE \"{name}\" LOGIN PASSWORD '{Password}'"))
        {
            await command.ExecuteNonQueryAsync();
        }

        foreach (var grant in grants)
        {
            await using var command = DataSource.CreateCommand(grant.Replace("{role}", $"\"{name}\"", StringComparison.Ordinal));
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Username = name,
            Password = Password,
        };

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Creates an empty database in the shared container and returns a data source for it. The caller
    /// owns the data source and disposes it; the database goes away with the container.
    /// </summary>
    /// <param name="purpose">
    /// A short name fragment identifying the test: 1 to 20 lower-case ASCII letters, digits, or
    /// underscores. The bound keeps the generated identifier inside PostgreSQL's 63-byte limit, so two
    /// tests can never be truncated onto the same database.
    /// </param>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(string purpose)
    {
        Assert.InRange(purpose.Length, 1, 20);
        Assert.All(purpose, c => Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', $"Invalid purpose character '{c}'."));

        // "aen_" + <=20 + "_" + 32 hex = at most 57 bytes, inside PostgreSQL's 63-byte identifier limit.
        var name = $"aen_{purpose}_{Guid.NewGuid():N}";

        // CREATE DATABASE takes no parameters and cannot run inside a transaction, so the identifier is
        // interpolated. Every character of it has just been checked against the allowlist above.
        await using (var command = DataSource.CreateCommand($"CREATE DATABASE \"{name}\""))
        {
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = name };
        return NpgsqlDataSource.Create(builder.ConnectionString);
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
