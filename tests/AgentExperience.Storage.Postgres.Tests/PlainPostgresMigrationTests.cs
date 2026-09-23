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
    public void No_script_this_package_ships_creates_an_extension_or_depends_on_the_vectors_table()
    {
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames)
        {
            var lines = PostgresExperienceRecordSchema.GetScript(scriptName)
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal))
                .ToArray();

            Assert.DoesNotContain("CREATE EXTENSION", string.Join('\n', lines), StringComparison.OrdinalIgnoreCase);

            // The vectors package owns experience_embeddings, and this package must not come to depend
            // on it. 0010's erasure still has to remove a record's embedding where one exists, so every
            // mention of that table sits behind a to_regclass guard and is issued through EXECUTE: a
            // base-only database never parses it, which PlainPostgres proves by migrating without
            // pgvector available at all.
            foreach (var line in lines.Where(line => line.Contains("experience_embeddings", StringComparison.Ordinal)))
            {
                Assert.True(
                    line.Contains("to_regclass", StringComparison.Ordinal) || line.Contains("EXECUTE", StringComparison.Ordinal),
                    $"{scriptName} names experience_embeddings outside a to_regclass guard: {line.Trim()}");
            }
        }
    }

    [Fact]
    public async Task The_erasure_skips_the_embedding_step_here_and_refuses_to_run_against_a_divergent_one()
    {
        await ExperienceSchemaMigrator.MigrateAsync(DataSource, CancellationToken.None);

        // A base-only database: no vector extension, no embedding table, and an erasure that simply does
        // not perform step 8. This is what the to_regclass guard is *for*.
        var skipped = Guid.NewGuid();
        await SeedRecordAsync(skipped);
        Assert.Equal("Deleted", await PurgeAsync(skipped));

        // Now the case the guard did not cover: a relation under that name whose shape is not 0004's.
        // Without a column check the EXECUTE fails with a bare undefined_column mid-erasure -- safe,
        // because the whole thing is one transaction, but undiagnosable, and the header presents the
        // guard as tolerating the vectors package's *absence*, which is not the same as its divergence.
        await using (var divergent = DataSource.CreateCommand(
            "CREATE TABLE agent_experience.experience_embeddings (record_id uuid NOT NULL PRIMARY KEY)"))
        {
            await divergent.ExecuteNonQueryAsync();
        }

        var blocked = Guid.NewGuid();
        await SeedRecordAsync(blocked);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => PurgeAsync(blocked));

        Assert.Equal(PostgresErrorCodes.UndefinedColumn, refused.SqlState);
        Assert.Contains("experience_id column", refused.MessageText, StringComparison.Ordinal);

        // Nothing was erased: the record still carries its payload, so an operator who reconciles the
        // embedding table and retries loses nothing.
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{blocked}' " +
            "AND deleted_at IS NULL AND payload <> '{}'::jsonb"));

        await using var cleanup = DataSource.CreateCommand("DROP TABLE agent_experience.experience_embeddings");
        await cleanup.ExecuteNonQueryAsync();
    }

    private async Task SeedRecordAsync(Guid experienceId)
    {
        await using var command = DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, " +
            "application_id, project_id, task_id, status, reuse_confidence, supporting_validations, " +
            "contradictions, revision, created_at, updated_at, payload_version, payload) VALUES " +
            "(@id, @id, 'tenant-plain', 'app-1', 'project-1', 'task-1', 'Validated', 0, 0, 0, 0, now(), now(), 1, " +
            "'{\"taskSummary\":\"still here\"}'::jsonb)");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<object?> PurgeAsync(Guid experienceId)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(" +
            "@id, 'tenant-plain', 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return await command.ExecuteScalarAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
