using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Story 6.1 for the vectors package: every other test in this project already runs as the application
/// role (see <see cref="VectorsFixture"/>). The embedding table is derived, rebuildable data, so the
/// application role may upsert and remove vectors -- but it owns nothing, so it cannot build or drop the
/// out-of-band index, truncate the table, or reach the ledgers beside it any further than the base
/// package allows.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class ApplicationRoleVectorsTests(VectorsFixture fixture)
{
    [Fact]
    public async Task The_application_role_may_upsert_and_remove_vectors_but_not_rewrite_their_scope_and_owns_neither_the_table_nor_its_index()
    {
        await using var command = fixture.OwnerDataSource.CreateCommand(
            "SELECT has_table_privilege(@role, 'agent_experience.experience_embeddings', 'SELECT'), " +
            "has_table_privilege(@role, 'agent_experience.experience_embeddings', 'INSERT'), " +
            "has_column_privilege(@role, 'agent_experience.experience_embeddings', 'embedding', 'UPDATE'), " +
            "has_table_privilege(@role, 'agent_experience.experience_embeddings', 'DELETE'), " +
            "has_table_privilege(@role, 'agent_experience.experience_embeddings', 'TRUNCATE,TRIGGER,REFERENCES') " +
            "OR has_table_privilege(@role, 'agent_experience.experience_embeddings', 'UPDATE') " +
            "OR has_column_privilege(@role, 'agent_experience.experience_embeddings', 'experience_id', 'UPDATE') " +
            "OR has_column_privilege(@role, 'agent_experience.experience_embeddings', 'tenant_id', 'UPDATE'), " +
            "pg_has_role(@role, (SELECT relowner FROM pg_class WHERE oid = 'agent_experience.experience_embeddings'::regclass), 'MEMBER')");
        command.Parameters.Add(new NpgsqlParameter<string>("role", VectorsFixture.ApplicationRoleName));
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.False(reader.GetBoolean(4));
            Assert.False(reader.GetBoolean(5));
        }

        // Index maintenance is an owner operation.
        var build = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(fixture.DataSource, 11));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(build.InnerException).SqlState);

        await using var truncate = fixture.DataSource.CreateCommand("TRUNCATE agent_experience.experience_embeddings");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_embeddings_table_is_behind_row_level_security_exactly_when_the_mode_says_so_and_then_admits_only_the_declared_bounds()
    {
        // Story 15.1. The table's state follows the suite's mode, so this suite cannot believe it proved the
        // policies while they were off.
        await using (var state = fixture.OwnerDataSource.CreateCommand(
            "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = 'agent_experience.experience_embeddings'::regclass"))
        await using (var reader = await state.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(RowLevelSecurityMode.IsOn, reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }

        var world = await TestWorld.CreateAsync(fixture);
        var id = await world.AddRecordAsync("refund-ticket-triage", "Resolve a customer refund", "Check the invoice first.");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id, CancellationToken.None);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));

        const string ById = "SELECT count(*) FROM agent_experience.experience_embeddings WHERE experience_id = @id";

        // Declared bounds for this world's tenant see the vector. With row-level security on, another tenant's
        // bounds do not, and neither does a statement that declared nothing; off, the store predicates are the
        // only layer and this hand-written statement has none.
        var hidden = RowLevelSecurityMode.IsOn ? 0L : 1L;
        Assert.Equal(1L, await CountAsDeclaredAsync(world.Authorization, ById, id));
        Assert.Equal(hidden, await CountAsDeclaredAsync(world.Authorization with { TenantId = "tenant-" + Guid.NewGuid().ToString("N") }, ById, id));
        Assert.Equal(hidden, await CountAsDeclaredAsync(null, ById, id));
    }

    [Fact]
    public async Task The_vectors_migrator_refuses_to_run_its_policies_before_the_base_migration_created_their_helpers()
    {
        var (owner, app, _) = await fixture.CreateDatabaseAsync("rlsorder");
        await using var ownerSource = owner;
        await using var appSource = app;
        await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None);

        // A base schema without 0019's helpers, as a database migrated by an older base package would be.
        await Execute(owner, "DROP FUNCTION agent_experience.rls_bound(text) CASCADE");

        await Assert.ThrowsAsync<ExperienceStoreException>(() => ExperienceVectorSchemaMigrator.MigrateAsync(owner, CancellationToken.None));
        Assert.Equal(0L, await Scalar<long>(owner, "SELECT count(*) FROM agent_experience.schema_versions WHERE scriptname LIKE '%0020_embeddings_row_level_security.sql'"));
        Assert.Equal(0L, await Scalar<long>(owner, "SELECT count(*) FROM pg_policy WHERE polname LIKE 'rls_embeddings_%'"));
    }

    [Fact]
    public async Task Enabling_row_level_security_over_an_embedding_table_without_its_policies_is_refused_and_enables_nothing()
    {
        var (owner, app, appRole) = await fixture.CreateDatabaseAsync("rlsnopol");
        await using var ownerSource = owner;
        await using var appSource = app;
        await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None);

        // Only 0004: the embedding table exists, its 0020 policies do not.
        await ExperienceSchemaMigrator.MigrateAsync(
            owner, typeof(ExperienceVectorSchema).Assembly, ExperienceVectorSchema.ResourcePrefix + "0004", CancellationToken.None);

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner, new ExperienceApplicationRoleOptions(appRole) { EnableRowLevelSecurity = true }, CancellationToken.None));
        Assert.Contains("rls_embeddings_select", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ExperienceVectorSchemaMigrator", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await Scalar<long>(owner, "SELECT count(*) FROM pg_class WHERE relnamespace = 'agent_experience'::regnamespace AND relrowsecurity"));

        // With 0020 applied the same call succeeds, and covers the embedding table.
        await ExperienceVectorSchemaMigrator.MigrateAsync(owner, CancellationToken.None);
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner, new ExperienceApplicationRoleOptions(appRole) { EnableRowLevelSecurity = true }, CancellationToken.None);
        Assert.True(await Scalar<bool>(owner, "SELECT relrowsecurity FROM pg_class WHERE oid = 'agent_experience.experience_embeddings'::regclass"));
    }

    [Fact]
    public async Task Another_tenants_embedding_is_invisible_and_cannot_be_written_under_a_declaration()
    {
        var (owner, app, appRole) = await fixture.CreateDatabaseAsync("rlsemb");
        await using var ownerSource = owner;
        await using var appSource = app;
        await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None);
        await ExperienceVectorSchemaMigrator.MigrateAsync(owner, CancellationToken.None);
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner, new ExperienceApplicationRoleOptions(appRole) { EnableRowLevelSecurity = true }, CancellationToken.None);

        // Tenant B's record and its vector, written through the store and the index as the application role.
        var tenantB = "tenant-" + Guid.NewGuid().ToString("N");
        var scopeB = new Scope(tenantB, "app-1", "project-1", "team-a");
        var authB = new AuthorizationContext(tenantB, "p", [], DateTimeOffset.UtcNow);
        var id = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow;
        stamp = new DateTimeOffset(stamp.UtcTicks - (stamp.UtcTicks % 10), TimeSpan.Zero);
        var record = new ExperienceRecord(
            id, Guid.NewGuid(), scopeB, "refund-ticket", "Resolve a refund", [], new Outcome(TaskVerificationStatus.Verified, [], "ok", stamp), 1,
            null, new EnvironmentFingerprint("h", "10", "linux", null, new Dictionary<string, string>()), new Provenance("t", null, stamp, null),
            ExperienceStatus.Validated, 0.8, 0, 0, 0, stamp, stamp);
        Assert.Equal(ExperienceStoreOutcome.Created, (await new PostgresExperienceRecordStore(app).CreateAsync(authB, record, CancellationToken.None)).Outcome);
        var indexing = new AgentExperience.Core.Indexing.ExperienceIndexingService(new PostgresExperienceEmbeddingIndex(app), new TopicEmbeddingGenerator());
        await indexing.IndexAsync(authB, scopeB, id, CancellationToken.None);
        Assert.Equal(1L, await Scalar<long>(owner, "SELECT count(*) FROM agent_experience.experience_embeddings"));

        var authA = new AuthorizationContext("tenant-" + Guid.NewGuid().ToString("N"), "p", [], DateTimeOffset.UtcNow);
        Assert.Equal(0L, await DeclaredScalarAsync<long>(app, authA, "SELECT count(*) FROM agent_experience.experience_embeddings"));
        Assert.Equal(0L, await DeclaredScalarAsync<long>(app, authA, "DELETE FROM agent_experience.experience_embeddings RETURNING 1") );

        // Written back under tenant A's bounds, tenant B's vector is refused.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => DeclaredScalarAsync<long>(
            app,
            authA,
            "INSERT INTO agent_experience.experience_embeddings (experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "model_id, dimension, content_hash, source_revision, embedding, created_at, updated_at) " +
            $"VALUES ('{id}', '{tenantB}', 'app-1', 'project-1', 'team-a', NULL, NULL, 'm', 2, 'h', 0, '[1,2]', now(), now()) RETURNING 1"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Equal(1L, await Scalar<long>(owner, "SELECT count(*) FROM agent_experience.experience_embeddings"));
    }

    private static async Task<T> DeclaredScalarAsync<T>(NpgsqlDataSource source, AuthorizationContext declare, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task Execute(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsDeclaredAsync(AuthorizationContext? declare, string sql, Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (declare is not null)
        {
            await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
