using AgentExperience.Core.KeyManagement;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 25.1 against a real PostgreSQL container: <c>0027</c>'s holdout assignment rows. One row per run and own-scope
/// live record, never a second for the same pair, never content, and gone with the record it names or with the run's own
/// record. Each test uses its own random tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresHoldoutAssignmentTests(PostgresFixture fixture)
{
    private readonly PostgresExperienceRecordStore _store = new(fixture.DataSource);

    [Fact]
    public async Task Holdout_assignment_rows_copy_the_record_s_scope_and_the_rendered_revision_and_skip_what_is_not_the_caller_s()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant, team: "team-a");
        var other = Scope(tenant, team: "team-b");
        var first = await CreateAsync(auth, scope);
        var second = await CreateAsync(auth, scope);
        var borrowed = await CreateAsync(auth, other);
        var runId = Guid.NewGuid();
        var ahead = DateTimeOffset.UtcNow.AddDays(1);

        var result = await _store.RecordHoldoutAssignmentAsync(
            auth,
            scope,
            new HoldoutAssignment(
                runId,
                HoldoutArm.Holdout,
                [new(first, 3), new(borrowed, 0), new(Guid.NewGuid(), 0), new(second, 0), new(first, 9)],
                ahead),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        Assert.True(result.Tracked);

        var rows = await RowsAsync(runId);
        Assert.Equal(
            new (Guid, long, string, string?)[] { (first, 3L, "Holdout", "team-a"), (second, 0L, "Holdout", "team-a") }.OrderBy(r => r.Item1).ToList(),
            rows.Select(r => (r.ExperienceId, r.Revision, r.Arm, r.TeamId)).OrderBy(r => r.ExperienceId).ToList());

        // The instant is clamped to the database's clock.
        Assert.All(rows, row => Assert.True(row.AssignedAt <= DateTimeOffset.UtcNow.AddMinutes(1)));

        // The borrowed record names no row of this run, in either scope.
        Assert.Equal(0L, await CountAsync("experience_id = @id", borrowed));
    }

    [Fact]
    public async Task Recording_the_same_run_again_writes_nothing_new_and_changes_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await CreateAsync(auth, scope);
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);

        await RecordAsync(auth, scope, new HoldoutAssignment(runId, HoldoutArm.Treatment, [new(record, 0)], at));
        var before = await RowsAsync(runId);

        await RecordAsync(auth, scope, new HoldoutAssignment(runId, HoldoutArm.Holdout, [new(record, 4)], DateTimeOffset.UtcNow));
        var after = await RowsAsync(runId);

        Assert.Single(after);
        Assert.Equal(before, after);
        Assert.Equal("Treatment", Assert.Single(after).Arm);

        // The record itself is untouched by either write.
        var read = await _store.GetAsync(auth, scope, record, CancellationToken.None);
        Assert.Equal(0, read.Record!.Revision);
    }

    [Fact]
    public async Task Erasing_a_lesson_removes_every_row_that_names_it_and_a_later_write_records_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lesson = await CreateAsync(auth, scope);
        var kept = await CreateAsync(auth, scope);
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await RecordAsync(auth, scope, new HoldoutAssignment(runA, HoldoutArm.Holdout, [new(lesson, 0), new(kept, 0)], DateTimeOffset.UtcNow));
        await RecordAsync(auth, scope, new HoldoutAssignment(runB, HoldoutArm.Treatment, [new(lesson, 0)], DateTimeOffset.UtcNow));
        Assert.Equal(2L, await CountAsync("experience_id = @id", lesson));

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await _store.DeleteAsync(auth, scope, lesson, CancellationToken.None)).Outcome);

        Assert.Equal(0L, await CountAsync("experience_id = @id", lesson));
        Assert.Equal(1L, await CountAsync("experience_id = @id", kept));

        // A tombstone is not a live record: an assignment naming it afterwards writes nothing.
        await RecordAsync(auth, scope, new HoldoutAssignment(Guid.NewGuid(), HoldoutArm.Holdout, [new(lesson, 0)], DateTimeOffset.UtcNow));
        Assert.Equal(0L, await CountAsync("experience_id = @id", lesson));
    }

    [Fact]
    public async Task Erasing_a_run_s_own_record_removes_that_run_s_rows_and_no_other_run_s()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lesson = await CreateAsync(auth, scope);
        var runId = Guid.NewGuid();
        var otherRun = Guid.NewGuid();

        // The run's own record, as finalization would write it once the run ends.
        var runRecord = Minimal(scope) with { SourceRunId = runId };
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, runRecord, CancellationToken.None)).Outcome);

        await RecordAsync(auth, scope, new HoldoutAssignment(runId, HoldoutArm.Holdout, [new(lesson, 0)], DateTimeOffset.UtcNow));
        await RecordAsync(auth, scope, new HoldoutAssignment(otherRun, HoldoutArm.Holdout, [new(lesson, 0)], DateTimeOffset.UtcNow));
        Assert.Equal(1L, await CountAsync("run_id = @id", runId));

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await _store.DeleteAsync(auth, scope, runRecord.ExperienceId, CancellationToken.None)).Outcome);

        Assert.Equal(0L, await CountAsync("run_id = @id", runId));
        Assert.Equal(1L, await CountAsync("run_id = @id", otherRun));
    }

    [Fact]
    public async Task Erasing_another_tenant_s_record_that_names_the_same_run_leaves_this_tenant_s_rows()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lesson = await CreateAsync(auth, scope);
        var runId = Guid.NewGuid();
        await RecordAsync(auth, scope, new HoldoutAssignment(runId, HoldoutArm.Holdout, [new(lesson, 0)], DateTimeOffset.UtcNow));

        // A run id is caller-supplied: another tenant can write a record that names this one.
        var otherTenant = NewTenant();
        var otherAuth = Authorize(otherTenant);
        var otherScope = Scope(otherTenant);
        var impostor = Minimal(otherScope) with { SourceRunId = runId };
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(otherAuth, impostor, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await _store.DeleteAsync(otherAuth, otherScope, impostor.ExperienceId, CancellationToken.None)).Outcome);

        Assert.Equal(1L, await CountAsync("run_id = @id", runId));
    }

    // ---- Reading outcomes back (story 25.2) ----------------------------------------------------------------------------

    [Fact]
    public async Task Holdout_outcomes_join_each_run_to_its_own_live_record_in_the_same_tenant_only()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lesson = await CreateAsync(auth, scope);
        var verified = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var pending = Guid.NewGuid();
        foreach (var (run, arm) in new[] { (verified, HoldoutArm.Treatment), (failed, HoldoutArm.Holdout), (pending, HoldoutArm.Holdout) })
        {
            await RecordAsync(auth, scope, new HoldoutAssignment(run, arm, [new(lesson, 0)], DateTimeOffset.UtcNow));
        }

        await CreateRunRecordAsync(auth, scope, verified, TaskVerificationStatus.Verified);
        await CreateRunRecordAsync(auth, scope, failed, TaskVerificationStatus.Failed);

        // Another tenant names the pending run (a run id is caller-supplied): never this tenant's outcome.
        var otherTenant = NewTenant();
        await CreateRunRecordAsync(Authorize(otherTenant), Scope(otherTenant), pending, TaskVerificationStatus.Verified);

        var result = await _store.QueryHoldoutOutcomesAsync(auth, new HoldoutOutcomeQuery(scope), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Equal(
            new (Guid, HoldoutArm, TaskVerificationStatus?)[]
            {
                (verified, HoldoutArm.Treatment, TaskVerificationStatus.Verified),
                (failed, HoldoutArm.Holdout, TaskVerificationStatus.Failed),
                (pending, HoldoutArm.Holdout, null),
            }.OrderBy(row => row.Item1),
            result.Rows.Select(row => (row.RunId, row.Arm, row.RunOutcome)));

        // The other tenant reads none of this tenant's rows, and its own scope has none.
        var foreign = await _store.QueryHoldoutOutcomesAsync(Authorize(otherTenant), new HoldoutOutcomeQuery(Scope(otherTenant)), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, foreign.Outcome);
        Assert.Empty(foreign.Rows);
        var denied = await _store.QueryHoldoutOutcomesAsync(Authorize(otherTenant), new HoldoutOutcomeQuery(scope), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
    }

    [Fact]
    public async Task An_erased_run_record_takes_its_run_s_rows_out_of_the_read()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lesson = await CreateAsync(auth, scope);
        var kept = Guid.NewGuid();
        var erased = Guid.NewGuid();
        await RecordAsync(auth, scope, new HoldoutAssignment(kept, HoldoutArm.Treatment, [new(lesson, 0)], DateTimeOffset.UtcNow));
        await RecordAsync(auth, scope, new HoldoutAssignment(erased, HoldoutArm.Holdout, [new(lesson, 0)], DateTimeOffset.UtcNow));
        await CreateRunRecordAsync(auth, scope, kept, TaskVerificationStatus.Verified);
        var erasedRecord = await CreateRunRecordAsync(auth, scope, erased, TaskVerificationStatus.Failed);

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await _store.DeleteAsync(auth, scope, erasedRecord, CancellationToken.None)).Outcome);

        var result = await _store.QueryHoldoutOutcomesAsync(auth, new HoldoutOutcomeQuery(scope), CancellationToken.None);
        var row = Assert.Single(result.Rows);
        Assert.Equal((kept, (TaskVerificationStatus?)TaskVerificationStatus.Verified), (row.RunId, row.RunOutcome));
    }

    [Fact]
    public async Task A_sealed_run_record_is_opened_for_its_outcome_and_one_under_a_destroyed_key_is_pending()
    {
        var keys = new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("holdout-kek"), new InMemoryExperienceWrappedKeyRepository());
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: new ExperienceEncryption(keys));
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lesson = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, lesson, CancellationToken.None)).Outcome);
        var opened = Guid.NewGuid();
        var shredded = Guid.NewGuid();
        var openedRecord = RunRecord(scope, opened, TaskVerificationStatus.Verified);
        var shreddedRecord = RunRecord(scope, shredded, TaskVerificationStatus.Failed);
        foreach (var (run, record) in new[] { (opened, openedRecord), (shredded, shreddedRecord) })
        {
            Assert.Equal(ExperienceStoreOutcome.Committed, (await store.RecordHoldoutAssignmentAsync(
                auth, scope, new HoldoutAssignment(run, HoldoutArm.Treatment, [new(lesson.ExperienceId, 0)], DateTimeOffset.UtcNow), CancellationToken.None)).Outcome);
            Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        }

        // The payload is sealed at rest: the status is not readable without the key.
        await using (var command = fixture.RawDataSource.CreateCommand("SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = @id"))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", openedRecord.ExperienceId));
            Assert.NotEqual(1, (int)(await command.ExecuteScalarAsync())!);
        }

        // A key destroyed with no tombstone yet (a delete whose commit failed): the record is erased, so no outcome.
        await keys.DestroyKeyAsync(new(shreddedRecord.ExperienceId, scope), CancellationToken.None);

        var result = await store.QueryHoldoutOutcomesAsync(auth, new HoldoutOutcomeQuery(scope), CancellationToken.None);

        Assert.Equal(
            new (Guid, TaskVerificationStatus?)[] { (opened, TaskVerificationStatus.Verified), (shredded, null) }.OrderBy(row => row.Item1),
            result.Rows.Select(row => (row.RunId, row.RunOutcome)));
    }

    [Fact]
    public async Task The_read_is_one_bounded_page_after_the_cursor()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lessons = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            lessons.Add(await CreateAsync(auth, scope));
        }

        var runs = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var run in runs)
        {
            await RecordAsync(auth, scope, new HoldoutAssignment(run, HoldoutArm.Holdout, [.. lessons.Select(id => new RunExposure(id, 0))], DateTimeOffset.UtcNow));
            await CreateRunRecordAsync(auth, scope, run, TaskVerificationStatus.Failed);
        }

        // PostgreSQL's uuid order is the byte order of the canonical text, which .NET's Guid comparison agrees with.
        var expected = runs.SelectMany(run => lessons.Select(lesson => (run, lesson)))
            .OrderBy(pair => pair.run.ToString(), StringComparer.Ordinal)
            .ThenBy(pair => pair.lesson.ToString(), StringComparer.Ordinal)
            .ToList();
        var first = await _store.QueryHoldoutOutcomesAsync(auth, new HoldoutOutcomeQuery(scope, 5), CancellationToken.None);
        Assert.Equal(expected.Take(5), first.Rows.Select(row => (row.RunId, row.ExperienceId)));
        var rest = await _store.QueryHoldoutOutcomesAsync(
            auth, new HoldoutOutcomeQuery(scope, 1000, new(first.Rows[^1].RunId, first.Rows[^1].ExperienceId)), CancellationToken.None);
        Assert.Equal(expected.Skip(5), rest.Rows.Select(row => (row.RunId, row.ExperienceId)));
        Assert.All(rest.Rows, row => Assert.Equal(TaskVerificationStatus.Failed, row.RunOutcome));
    }

    private async Task<Guid> CreateRunRecordAsync(AuthorizationContext auth, Scope scope, Guid runId, TaskVerificationStatus status)
    {
        var record = RunRecord(scope, runId, status);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    private static ExperienceRecord RunRecord(Scope scope, Guid runId, TaskVerificationStatus status) =>
        Minimal(scope) with { SourceRunId = runId, Outcome = new Outcome(status, [], null, DateTimeOffset.UtcNow) };

    private async Task<Guid> CreateAsync(AuthorizationContext auth, Scope scope)
    {
        var record = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    private async Task RecordAsync(AuthorizationContext auth, Scope scope, HoldoutAssignment assignment)
    {
        var result = await _store.RecordHoldoutAssignmentAsync(auth, scope, assignment, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
    }

    private async Task<long> CountAsync(string predicate, Guid id)
    {
        await using var command = fixture.RawDataSource.CreateCommand(
            $"SELECT count(*) FROM agent_experience.experience_holdout_assignments WHERE {predicate}");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<Row>> RowsAsync(Guid runId)
    {
        await using var command = fixture.RawDataSource.CreateCommand(
            "SELECT experience_id, revision, arm, team_id, assigned_at FROM agent_experience.experience_holdout_assignments " +
            "WHERE run_id = @id ORDER BY experience_id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", runId));
        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    private sealed record Row(Guid ExperienceId, long Revision, string Arm, string? TeamId, DateTimeOffset AssignedAt);
}
