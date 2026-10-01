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
/// change what it may do, not how fast a statement it may run executes. Row-level security is the exception, because
/// its policies do change what a statement costs and a superuser bypasses them: with
/// <c>AGENTEXPERIENCE_BENCHMARK_RLS</c> set to <c>on</c> or <c>off</c> (story 15.1), each dataset is instead the
/// supported two-role deployment -- an owner role migrates, and the stores connect as an application role given the
/// manifest by <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync"/> with
/// <see cref="ExperienceApplicationRoleOptions.EnableRowLevelSecurity"/> set to that value.
/// </remarks>
internal sealed class BenchmarkPostgres : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private readonly NpgsqlDataSource _admin;
    private readonly List<NpgsqlDataSource> _databases = [];

    /// <summary>The environment variable that selects the two-role deployment and its row-level security.</summary>
    internal const string RowLevelSecurityVariable = "AGENTEXPERIENCE_BENCHMARK_RLS";

    private const string OwnerRole = "aen_bench_owner";

    private const string ApplicationRole = "aen_bench_app";

    private const string RolePassword = "aen-bench-password";

    /// <summary>
    /// <see langword="null"/> for the superuser deployment (the default), or whether the two-role deployment switches
    /// row-level security on.
    /// </summary>
    internal static bool? RowLevelSecurity { get; } = Environment.GetEnvironmentVariable(RowLevelSecurityVariable) switch
    {
        null or "" => null,
        "on" => true,
        "off" => false,
        var other => throw new InvalidOperationException($"{RowLevelSecurityVariable}='{other}' must be 'on', 'off', or unset."),
    };

    private readonly Dictionary<NpgsqlDataSource, NpgsqlDataSource> _owners = [];

    private bool _rolesCreated;

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
        if (RowLevelSecurity is { } enable)
        {
            return await CreateTwoRoleDatabaseAsync(name, enable).ConfigureAwait(false);
        }

        // CREATE DATABASE takes no parameters; every name comes from BenchmarkData's own constants.
        await ExecuteAsync($"CREATE DATABASE \"{name}\"").ConfigureAwait(false);

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None).ConfigureAwait(false);
        _databases.Add(dataSource);
        return dataSource;
    }

    /// <summary>The two-role deployment: the owner migrates, and the returned data source connects as the application role.</summary>
    private async Task<NpgsqlDataSource> CreateTwoRoleDatabaseAsync(string name, bool enableRowLevelSecurity)
    {
        if (!_rolesCreated)
        {
            await ExecuteAsync($"CREATE ROLE {OwnerRole} LOGIN PASSWORD '{RolePassword}'").ConfigureAwait(false);
            await ExecuteAsync($"CREATE ROLE {ApplicationRole} LOGIN PASSWORD '{RolePassword}'").ConfigureAwait(false);
            await ExecuteAsync(
                "GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized " +
                $"TO {OwnerRole}").ConfigureAwait(false);
            _rolesCreated = true;
        }

        await ExecuteAsync($"CREATE DATABASE \"{name}\" OWNER {OwnerRole}").ConfigureAwait(false);

        var owner = NpgsqlDataSource.Create(ConnectionString(name, OwnerRole));
        _databases.Add(owner);
        await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None).ConfigureAwait(false);
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ApplicationRole) { EnableRowLevelSecurity = enableRowLevelSecurity },
            CancellationToken.None).ConfigureAwait(false);

        var application = NpgsqlDataSource.Create(ConnectionString(name, ApplicationRole));
        _databases.Add(application);
        _owners[application] = owner;
        return application;
    }

    /// <summary>
    /// Refreshes the planner's statistics for a dataset's database, as the role that owns its tables: only a table's
    /// owner can analyze it, and in the two-role deployment the dataset's own data source is the application role.
    /// </summary>
    internal async Task AnalyzeAsync(NpgsqlDataSource dataSource)
    {
        await using var analyze = (_owners.TryGetValue(dataSource, out var owner) ? owner : dataSource).CreateCommand("ANALYZE");
        await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string ConnectionString(string database, string role) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = role,
            Password = RolePassword,
        }.ConnectionString;

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _admin.CreateCommand(sql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
