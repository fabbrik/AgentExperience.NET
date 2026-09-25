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
}
