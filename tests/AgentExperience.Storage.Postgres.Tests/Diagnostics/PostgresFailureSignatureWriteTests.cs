using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests.Diagnostics;

/// <summary>
/// Story 28.2 review: a record is created without its failure signatures only when the signature table, a column it
/// reads, or the role's privilege on it is missing, and every such skip is reported, with its SQLSTATE and nothing else;
/// a row-level security refusal of a role that holds the privilege fails the create instead. These tests change the
/// fixture's schema for a moment (renaming the table, revoking a grant, adding a refusing policy) and put it back, so
/// they run in the telemetry collection: on their own, with their own container.
/// </summary>
[Collection(PostgresTelemetryCollection.Name)]
public sealed class PostgresFailureSignatureWriteTests(PostgresFixture fixture)
{
    private const string Table = "agent_experience.experience_failure_signatures";

    private const string SpanName = "agentexperience.record.signature.write";

    [Fact]
    public async Task A_missing_table_creates_the_record_without_signatures_and_reports_42P01()
    {
        var (record, created, probe) = await CreateWhileAsync(
            $"ALTER TABLE {Table} RENAME TO experience_failure_signatures_hidden",
            "ALTER TABLE agent_experience.experience_failure_signatures_hidden RENAME TO experience_failure_signatures");
        using (probe)
        {
            Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
            await AssertStoredAsync(record, signatures: 0);
            AssertSkipped(probe, "42P01");
        }
    }

    [Fact]
    public async Task A_missing_privilege_creates_the_record_without_signatures_and_reports_42501()
    {
        var (record, created, probe) = await CreateWhileAsync(
            $"REVOKE INSERT ON {Table} FROM {PostgresFixture.ApplicationRoleName}",
            $"GRANT INSERT ON {Table} TO {PostgresFixture.ApplicationRoleName}");
        using (probe)
        {
            Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
            await AssertStoredAsync(record, signatures: 0);
            AssertSkipped(probe, "42501");
        }
    }

    [Fact]
    public async Task A_row_level_security_refusal_of_a_role_that_holds_the_privilege_fails_the_create_and_reports_no_skip()
    {
        var wasOn = await ScalarAsync<bool>("SELECT relrowsecurity FROM pg_class WHERE oid = 'agent_experience.experience_failure_signatures'::regclass");
        var tenant = NewTenant();
        var record = WithFailedCall(Minimal(Scope(tenant), status: ExperienceStatus.Validated));
        using var probe = TelemetryProbe.All();

        await OwnerAsync($"ALTER TABLE {Table} ENABLE ROW LEVEL SECURITY");
        await OwnerAsync($"CREATE POLICY test_refuse_signatures ON {Table} AS RESTRICTIVE FOR INSERT WITH CHECK (false)");
        ExperienceStoreException thrown;
        try
        {
            thrown = await Assert.ThrowsAsync<ExperienceStoreException>(() =>
                new PostgresExperienceRecordStore(fixture.DataSource).CreateAsync(Authorize(tenant), record, CancellationToken.None));
        }
        finally
        {
            await OwnerAsync($"DROP POLICY IF EXISTS test_refuse_signatures ON {Table}");
            if (!wasOn)
            {
                await OwnerAsync($"ALTER TABLE {Table} DISABLE ROW LEVEL SECURITY");
            }
        }

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(thrown.InnerException).SqlState);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{record.ExperienceId}'"));
        Assert.Empty(probe.Spans(SpanName));
        Assert.Empty(probe.For("agentexperience.operation.count", "record.signature.write"));
    }

    [Fact]
    public async Task A_write_that_succeeds_reports_nothing()
    {
        var tenant = NewTenant();
        var record = WithFailedCall(Minimal(Scope(tenant), status: ExperienceStatus.Validated));
        using var probe = TelemetryProbe.All();

        var created = await new PostgresExperienceRecordStore(fixture.DataSource).CreateAsync(Authorize(tenant), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        await AssertStoredAsync(record, signatures: 1);
        Assert.Empty(probe.Spans(SpanName));
    }

    private async Task<(ExperienceRecord Record, ExperienceRecordCreateResult Created, TelemetryProbe Probe)> CreateWhileAsync(string change, string restore)
    {
        var tenant = NewTenant();
        var record = WithFailedCall(Minimal(Scope(tenant), status: ExperienceStatus.Validated));
        var probe = TelemetryProbe.All();
        await OwnerAsync(change);
        try
        {
            var created = await new PostgresExperienceRecordStore(fixture.DataSource).CreateAsync(Authorize(tenant), record, CancellationToken.None);
            return (record, created, probe);
        }
        finally
        {
            await OwnerAsync(restore);
        }
    }

    private async Task AssertStoredAsync(ExperienceRecord record, long signatures)
    {
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{record.ExperienceId}'"));
        Assert.Equal(signatures, await ScalarAsync<long>($"SELECT count(*) FROM {Table} WHERE experience_id = '{record.ExperienceId}'"));
    }

    private static void AssertSkipped(TelemetryProbe probe, string sqlState)
    {
        var span = Assert.Single(probe.Spans(SpanName));
        Assert.Equal(sqlState, span.GetTagItem("agentexperience.sqlstate"));
        Assert.Equal("Skipped", span.GetTagItem("agentexperience.outcome"));

        // The SQLSTATE and the outcome only: no table, record, scope or message.
        Assert.Equal(
            ["agentexperience.operation", "agentexperience.outcome", "agentexperience.sqlstate"],
            span.TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal));

        var count = Assert.Single(probe.For("agentexperience.operation.count", "record.signature.write"));
        Assert.Equal("Skipped", count.Tags["outcome"]);
        Assert.Equal(1d, count.Value);
    }

    private async Task OwnerAsync(string sql)
    {
        await using var command = fixture.OwnerDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = fixture.OwnerDataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
