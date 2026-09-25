using AgentExperience.Tests.Shared;
using Npgsql;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Story 6.3 (KL-13): the sample's PostgreSQL mode really ran against the major
/// <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> selected, on a stock image with no <c>vector</c> extension
/// available, so a CI leg cannot report it as evidence for a major it did not reach.
/// </summary>
[Collection(SamplePostgresCollection.Name)]
public sealed class SamplePostgresServerVersionTests(SamplePostgresFixture fixture)
{
    [Fact]
    public async Task The_container_reports_the_major_version_this_run_selected_and_has_no_vector_extension()
    {
        await using var dataSource = NpgsqlDataSource.Create(await fixture.CreateDatabaseAsync("server_version"));

        await using var version = dataSource.CreateCommand("SELECT current_setting('server_version_num')::int");
        Assert.Equal(PostgresTestImage.Major, (int)(await version.ExecuteScalarAsync())! / 10000);

        await using var vector = dataSource.CreateCommand("SELECT count(*) FROM pg_available_extensions WHERE name = 'vector'");
        Assert.Equal(0L, (long)(await vector.ExecuteScalarAsync())!);
    }
}
