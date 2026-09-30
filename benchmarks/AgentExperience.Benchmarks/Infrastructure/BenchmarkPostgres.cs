using AgentExperience.Storage.Postgres;
using AgentExperience.Tests.Shared;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Benchmarks.Infrastructure;

/// <summary>
/// The one PostgreSQL container a benchmark run uses: <c>pgvector/pgvector</c> at the major
/// <see cref="PostgresTestImage"/> selects (16 unless <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> says otherwise), started
/// once by <c>Program</c> before any benchmark runs. Each dataset gets a database of its own in it, migrated with
/// <see cref="ExperienceSchemaMigrator"/>.
/// </summary>
/// <remarks>
/// When Docker is unavailable the container cannot start; <see cref="StartAsync"/> then records why, prints it, and
/// <see cref="Stores.All"/> offers the in-memory store only, so the PostgreSQL benchmarks are skipped rather than failed.
/// The stores connect as the container's superuser, not as a separate application role: the privileges a role holds
/// change what it may do, not how fast a statement it may run executes.
/// </remarks>
internal sealed class BenchmarkPostgres : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private readonly NpgsqlDataSource _admin;
    private readonly List<NpgsqlDataSource> _databases = [];

    private BenchmarkPostgres(PostgreSqlContainer container, NpgsqlDataSource admin, string serverVersion)
    {
        _container = container;
        _admin = admin;
        ServerVersion = serverVersion;
    }

    /// <summary>The running instance, or <see langword="null"/> when none could be started.</summary>
    internal static BenchmarkPostgres? Current { get; private set; }

    /// <summary>What the server reports as <c>server_version</c>, for the environment the results are recorded with.</summary>
    internal string ServerVersion { get; }

    /// <summary>
    /// Starts the container and makes it <see cref="Current"/>. Never throws for an unavailable Docker: it writes one
    /// line saying the PostgreSQL benchmarks are skipped, and returns <see langword="null"/>.
    /// </summary>
    internal static async Task<BenchmarkPostgres?> StartAsync(TextWriter log)
    {
        PostgreSqlContainer? container = null;
        try
        {
            container = new PostgreSqlBuilder(PostgresTestImage.Pgvector).Build();
            await container.StartAsync().ConfigureAwait(false);

            var admin = NpgsqlDataSource.Create(container.GetConnectionString());
            await using var command = admin.CreateCommand("SHOW server_version");
            var version = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;

            Current = new BenchmarkPostgres(container, admin, version);
            log.WriteLine($"PostgreSQL benchmarks: {PostgresTestImage.Pgvector}, server_version {version}.");
            return Current;
        }
        catch (Exception ex)
        {
            if (container is not null)
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }

            log.WriteLine($"PostgreSQL benchmarks skipped: Docker is unavailable or the container did not start ({ex.GetType().Name}: {ex.Message}). The in-memory benchmarks still run.");
            return null;
        }
    }

    /// <summary>Creates a fresh database named <paramref name="name"/>, migrates it, and returns a data source for it.</summary>
    internal async Task<NpgsqlDataSource> CreateDatabaseAsync(string name)
    {
        // CREATE DATABASE takes no parameters; every name comes from BenchmarkData's own constants.
        await using (var create = _admin.CreateCommand($"CREATE DATABASE \"{name}\""))
        {
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None).ConfigureAwait(false);
        _databases.Add(dataSource);
        return dataSource;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in _databases)
        {
            await dataSource.DisposeAsync().ConfigureAwait(false);
        }

        await _admin.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
        Current = null;
    }
}
