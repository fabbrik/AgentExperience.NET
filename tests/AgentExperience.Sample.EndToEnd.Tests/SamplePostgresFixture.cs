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

        var superuser = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        return RowLevelSecurityOn ? await TwoRoleWithRowLevelSecurityAsync(name, superuser) : superuser;
    }

    /// <summary>
    /// <c>AGENTEXPERIENCE_TEST_RLS=on</c> (story 15.1): the sample then runs as an application role behind PostgreSQL
    /// row-level security instead of as the container's superuser, whom row-level security does not bind.
    /// </summary>
    private static bool RowLevelSecurityOn { get; } =
        string.Equals(Environment.GetEnvironmentVariable("AGENTEXPERIENCE_TEST_RLS"), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The two-role deployment for one database: an owner migrates it and applies the manifest with row-level security
    /// on, and the returned connection string is the application role's. The sample runs the migrator on startup, as a
    /// host would; with nothing pending that only reads the journal, so -- in this test mode only, after the manifest
    /// was verified -- the application role is also let read the journal.
    /// </summary>
    private async Task<string> TwoRoleWithRowLevelSecurityAsync(string database, string superuserConnectionString)
    {
        const string Password = "aes-role-password";
        var owner = $"{database}_o";
        var app = $"{database}_a";
        await using (var dataSource = NpgsqlDataSource.Create(_container!.GetConnectionString()))
        {
            foreach (var sql in new[]
            {
                $"CREATE ROLE \"{owner}\" LOGIN PASSWORD '{Password}'",
                $"CREATE ROLE \"{app}\" LOGIN PASSWORD '{Password}'",
                $"ALTER DATABASE \"{database}\" OWNER TO \"{owner}\"",
                $"GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized TO \"{owner}\"",
            })
            {
                await using var command = dataSource.CreateCommand(sql);
                await command.ExecuteNonQueryAsync();
            }
        }

        string As(string role) => new NpgsqlConnectionStringBuilder(superuserConnectionString) { Username = role, Password = Password }.ConnectionString;

        await using (var ownerSource = NpgsqlDataSource.Create(As(owner)))
        {
            await Storage.Postgres.ExperienceSchemaMigrator.MigrateAsync(ownerSource, CancellationToken.None);
            await Storage.Postgres.ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
                ownerSource,
                new Storage.Postgres.ExperienceApplicationRoleOptions(app) { AllowErasure = true, EnableRowLevelSecurity = true },
                CancellationToken.None);
            await using var journal = ownerSource.CreateCommand($"GRANT SELECT ON agent_experience.schema_versions TO \"{app}\"");
            await journal.ExecuteNonQueryAsync();
        }

        return As(app);
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
