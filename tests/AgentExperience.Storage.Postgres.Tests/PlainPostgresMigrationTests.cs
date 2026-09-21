using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Proves this package's schema needs nothing pgvector provides, against a stock <c>postgres:16</c>
/// image with no <c>vector</c> extension available at all.
/// </summary>
/// <remarks>
/// This is the regression guard for a real defect: the embedding schema was briefly in this package's
/// script list, which made <c>CREATE EXTENSION vector</c> -- an untrusted extension, so superuser-only
/// -- a startup requirement for every host, including text-only ones that never enable the vector
/// channel. A stock image cannot even satisfy it, so if the embedding script ever comes back here,
/// this test fails rather than a text-only deployment failing at someone's startup.
/// </remarks>
public sealed class PlainPostgresMigrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        // Stock postgres:16, deliberately not pgvector/pgvector:pg16.
        _container = new PostgreSqlBuilder("postgres:16").Build();
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
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

    [Fact]
    public async Task The_base_schema_migrates_on_a_PostgreSQL_without_pgvector_available()
    {
        // Sanity: the extension really is unavailable here, so the assertion below means something.
        Assert.Equal(
            0L,
            await ScalarAsync<long>("SELECT count(*) FROM pg_available_extensions WHERE name = 'vector'"));

        var applied = await ExperienceSchemaMigrator.MigrateAsync(DataSource, CancellationToken.None);

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames.Count, applied.AppliedScripts.Count);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, applied.AppliedScripts);

        // The store's tables exist, so a text-only deployment is fully usable.
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'agent_experience' AND table_name = 'experience_records'"));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'agent_experience' AND table_name = 'lifecycle_events'"));

        // And nothing here created an extension or an embedding table.
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'agent_experience' AND table_name = 'experience_embeddings'"));

        // Rerunning is a no-op, as for any other database.
        Assert.Empty((await ExperienceSchemaMigrator.MigrateAsync(DataSource, CancellationToken.None)).AppliedScripts);
    }

    [Fact]
    public void No_script_this_package_ships_creates_an_extension()
    {
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames)
        {
            var statements = string.Join(
                '\n',
                PostgresExperienceRecordSchema.GetScript(scriptName)
                    .Split('\n')
                    .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

            Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("experience_embeddings", statements, StringComparison.Ordinal);
        }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
