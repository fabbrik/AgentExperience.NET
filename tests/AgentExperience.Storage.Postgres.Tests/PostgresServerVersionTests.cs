using AgentExperience.Tests.Shared;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 6.3 (KL-13): the suite really ran against the PostgreSQL major <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c>
/// asked for. Without this, a CI leg whose variable was misspelt or ignored would run the default image
/// and still be reported as evidence for another version.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresServerVersionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_server_reports_the_major_version_this_run_selected()
    {
        await using var command = fixture.DataSource.CreateCommand("SELECT current_setting('server_version_num')::int");
        var versionNumber = (int)(await command.ExecuteScalarAsync())!;

        Assert.Equal(PostgresTestImage.Major, versionNumber / 10000);
    }
}

/// <summary>
/// Story 6.3 (KL-13): how <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> is parsed. Pure, so it lives apart from the
/// container-backed class above and still runs under the no-Docker filter in CONTRIBUTING.md.
/// </summary>
public sealed class PostgresTestImageTests
{
    [Theory]
    [InlineData(null, 16)]
    [InlineData("", 16)]
    [InlineData("  ", 16)]
    [InlineData("15", 15)]
    [InlineData("17", 17)]
    [InlineData(" 18 ", 18)]
    public void A_supported_or_absent_value_selects_that_major(string? value, int expected) =>
        Assert.Equal(expected, PostgresTestImage.Resolve(value));

    [Theory]
    [InlineData("14")]
    [InlineData("19")]
    [InlineData("pg17")]
    [InlineData("17.2")]
    [InlineData("-16")]
    [InlineData("+16")]
    public void An_unsupported_or_malformed_value_fails_loudly_instead_of_falling_back(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PostgresTestImage.Resolve(value));
        Assert.Contains(PostgresTestImage.MajorVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_images_name_the_same_major()
    {
        Assert.Equal($"pgvector/pgvector:pg{PostgresTestImage.Major}", PostgresTestImage.Pgvector);
        Assert.Equal($"postgres:{PostgresTestImage.Major}", PostgresTestImage.Stock);
    }
}
