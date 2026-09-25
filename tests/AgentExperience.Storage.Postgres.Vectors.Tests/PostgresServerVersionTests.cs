using AgentExperience.Tests.Shared;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Story 6.3 (KL-13): the vector suite really ran against the PostgreSQL major
/// <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> asked for, with the <c>vector</c> extension this package's own
/// migrator created. A CI leg whose variable was ignored would otherwise report the default image as
/// evidence for another version.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class PostgresServerVersionTests(VectorsFixture fixture)
{
    [Fact]
    public async Task The_server_reports_the_major_version_this_run_selected_and_has_the_vector_extension()
    {
        await using var version = fixture.DataSource.CreateCommand("SELECT current_setting('server_version_num')::int");
        Assert.Equal(PostgresTestImage.Major, (int)(await version.ExecuteScalarAsync())! / 10000);

        await using var extension = fixture.DataSource.CreateCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'");
        Assert.IsType<string>(await extension.ExecuteScalarAsync());
    }
}
