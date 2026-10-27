using AgentExperience.Core.KeyManagement;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 25.8 against a real PostgreSQL container: <c>0029</c>'s provider holdout rows. One row per run and key, in the
/// caller's own scope, never a second, never content, read back with the run's own outcome. Each test uses its own
/// random tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresProviderHoldoutTests(PostgresFixture fixture)
{
    private readonly PostgresExperienceRecordStore _store = new(fixture.DataSource);

    [Fact]
    public async Task A_row_carries_the_caller_s_scope_and_a_clamped_instant_and_a_second_write_changes_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant, team: "team-a");
        var runId = Guid.NewGuid();

        await RecordAsync(auth, scope, new ProviderHoldoutAssignment(runId, "mem0", HoldoutArm.Holdout, DateTimeOffset.UtcNow.AddDays(1)));
        var before = await RowsAsync(runId);
        await RecordAsync(auth, scope, new ProviderHoldoutAssignment(runId, "mem0", HoldoutArm.Treatment, DateTimeOffset.UtcNow));
        var after = await RowsAsync(runId);

        var row = Assert.Single(after);
        Assert.Equal(before, after);
        Assert.Equal(("mem0", "Holdout", tenant, "team-a"), (row.Key, row.Arm, row.TenantId, row.TeamId));
        Assert.True(row.AssignedAt <= DateTimeOffset.UtcNow.AddMinutes(1));

        // The same run under another key is its own row.
        await RecordAsync(auth, scope, new ProviderHoldoutAssignment(runId, "zep", HoldoutArm.Treatment, DateTimeOffset.UtcNow));
        Assert.Equal(["mem0", "zep"], (await RowsAsync(runId)).Select(r => r.Key));
    }

    [Fact]
    public async Task The_check_constraint_refuses_a_malformed_key_written_by_hand()
    {
        await using var command = fixture.RawDataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_provider_holdout_assignments (run_id, provider_key, arm, tenant_id, application_id, " +
            "project_id, assigned_at) VALUES (gen_random_uuid(), 'a b', 'Holdout', 't', 'a', 'p', now())");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task Outcomes_join_each_run_to_its_own_live_record_in_the_same_tenant_only_one_bounded_page_at_a_time()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var verified = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var pending = Guid.NewGuid();
        foreach (var (run, arm) in new[] { (verified, HoldoutArm.Treatment), (failed, HoldoutArm.Holdout), (pending, HoldoutArm.Holdout) })
        {
            await RecordAsync(auth, scope, new ProviderHoldoutAssignment(run, "mem0", arm, DateTimeOffset.UtcNow));
        }

        await CreateRunRecordAsync(_store, auth, scope, verified, TaskVerificationStatus.Verified);
        await CreateRunRecordAsync(_store, auth, scope, failed, TaskVerificationStatus.Failed);

        // Another tenant names the pending run (a run id is caller-supplied): never this tenant's outcome.
        var otherTenant = NewTenant();
        await CreateRunRecordAsync(_store, Authorize(otherTenant), Scope(otherTenant), pending, TaskVerificationStatus.Verified);

        var expected = new (Guid, HoldoutArm, TaskVerificationStatus?)[]
        {
            (verified, HoldoutArm.Treatment, TaskVerificationStatus.Verified),
            (failed, HoldoutArm.Holdout, TaskVerificationStatus.Failed),
            (pending, HoldoutArm.Holdout, null),
        }.OrderBy(row => row.Item1.ToString(), StringComparer.Ordinal).ToList();

        var all = await _store.QueryProviderHoldoutOutcomesAsync(auth, new ProviderHoldoutOutcomeQuery(scope, "mem0"), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, all.Outcome);
        Assert.Equal(expected, all.Rows.Select(row => (row.RunId, row.Arm, row.RunOutcome)));

        var first = await _store.QueryProviderHoldoutOutcomesAsync(auth, new ProviderHoldoutOutcomeQuery(scope, "mem0", 2), CancellationToken.None);
        var rest = await _store.QueryProviderHoldoutOutcomesAsync(auth, new ProviderHoldoutOutcomeQuery(scope, "mem0", 2, first.Rows[^1].RunId), CancellationToken.None);
        Assert.Equal(expected, first.Rows.Concat(rest.Rows).Select(row => (row.RunId, row.Arm, row.RunOutcome)));

        // The other tenant reads none of it, and is denied this scope.
        Assert.Empty((await _store.QueryProviderHoldoutOutcomesAsync(Authorize(otherTenant), new ProviderHoldoutOutcomeQuery(Scope(otherTenant), "mem0"), CancellationToken.None)).Rows);
        Assert.Equal(
            ExperienceStoreOutcome.Denied,
            (await _store.QueryProviderHoldoutOutcomesAsync(Authorize(otherTenant), new ProviderHoldoutOutcomeQuery(scope, "mem0"), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_sealed_run_record_is_opened_for_its_outcome_and_one_under_a_destroyed_key_is_pending()
    {
        var keys = new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("provider-holdout-kek"), new InMemoryExperienceWrappedKeyRepository());
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: new ExperienceEncryption(keys));
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var opened = Guid.NewGuid();
        var shredded = Guid.NewGuid();
        await RecordAsync(auth, scope, new ProviderHoldoutAssignment(opened, "mem0", HoldoutArm.Treatment, DateTimeOffset.UtcNow), store);
        await RecordAsync(auth, scope, new ProviderHoldoutAssignment(shredded, "mem0", HoldoutArm.Holdout, DateTimeOffset.UtcNow), store);
        await CreateRunRecordAsync(store, auth, scope, opened, TaskVerificationStatus.Verified);
        var shreddedRecord = await CreateRunRecordAsync(store, auth, scope, shredded, TaskVerificationStatus.Failed);

        await keys.DestroyKeyAsync(new(shreddedRecord, scope), CancellationToken.None);

        var result = await store.QueryProviderHoldoutOutcomesAsync(auth, new ProviderHoldoutOutcomeQuery(scope, "mem0"), CancellationToken.None);

        Assert.Equal(
            new (Guid, TaskVerificationStatus?)[] { (opened, TaskVerificationStatus.Verified), (shredded, null) }.OrderBy(row => row.Item1.ToString(), StringComparer.Ordinal),
            result.Rows.Select(row => (row.RunId, row.RunOutcome)));
    }

    private static async Task<Guid> CreateRunRecordAsync(PostgresExperienceRecordStore store, AuthorizationContext auth, Scope scope, Guid runId, TaskVerificationStatus status)
    {
        var record = Minimal(scope) with { SourceRunId = runId, Outcome = new Outcome(status, [], null, DateTimeOffset.UtcNow) };
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    private async Task RecordAsync(AuthorizationContext auth, Scope scope, ProviderHoldoutAssignment assignment, PostgresExperienceRecordStore? store = null)
    {
        var result = await (store ?? _store).RecordProviderHoldoutAssignmentAsync(auth, scope, assignment, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        Assert.True(result.Tracked);
    }

    private async Task<List<Row>> RowsAsync(Guid runId)
    {
        await using var command = fixture.RawDataSource.CreateCommand(
            "SELECT provider_key, arm, tenant_id, team_id, assigned_at FROM agent_experience.experience_provider_holdout_assignments " +
            "WHERE run_id = @id ORDER BY provider_key");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", runId));
        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    private sealed record Row(string Key, string Arm, string TenantId, string? TeamId, DateTimeOffset AssignedAt);
}
