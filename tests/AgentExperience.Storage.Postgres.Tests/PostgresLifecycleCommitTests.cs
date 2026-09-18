using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 2.4's atomic lifecycle commit against a real PostgreSQL 16 container: one transaction per
/// commit, idempotency by event ID, optimistic concurrency by expected revision, and an append-only
/// history. Each test uses its own random tenant, so tests sharing the container never see each other's
/// rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresLifecycleCommitTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;

    public PostgresLifecycleCommitTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
    }

    [Fact]
    public async Task A_valid_commit_stores_the_event_updates_the_projection_and_raises_the_revision()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, record.Revision);
        var result = await _store.CommitLifecycleEventAsync(auth, record.Scope, lifecycleEvent, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        Assert.Equal(record.Revision + 1, result.Revision);
        Assert.Empty(result.Errors);

        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(record.Revision + 1, stored.Revision);
        Assert.True(stored.UpdatedAt > record.UpdatedAt);

        // The adapter persists only the decision it was given: nothing else about the record moves.
        Assert.Equal(record.ReuseConfidence, stored.ReuseConfidence);
        Assert.Equal(record.SupportingValidations, stored.SupportingValidations);
        Assert.Equal(record.Contradictions, stored.Contradictions);
        Assert.Equal(record.CreatedAt, stored.CreatedAt);

        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(record.Revision + 1, history.Revision);
        var only = Assert.Single(history.Events);
        Assert.Equal(lifecycleEvent.EventId, only.EventId);
        Assert.Equal(ExperienceStatus.Candidate, only.PriorStatus);
        Assert.Equal(ExperienceStatus.Validated, only.CurrentStatus);
        Assert.Equal(lifecycleEvent.Reason, only.Reason);
        Assert.Equal(lifecycleEvent.Producer, only.Producer);
        Assert.Equal(TimeSpan.Zero, only.OccurredAt.Offset);
    }

    [Fact]
    public async Task Replaying_an_identical_event_returns_the_original_outcome_and_writes_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);

        var first = await _store.CommitLifecycleEventAsync(auth, record.Scope, lifecycleEvent, CancellationToken.None);
        var replay = await _store.CommitLifecycleEventAsync(auth, record.Scope, lifecycleEvent, CancellationToken.None);
        var replayAgain = await _store.CommitLifecycleEventAsync(auth, record.Scope, lifecycleEvent, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, first.Outcome);
        Assert.Equal(first.Outcome, replay.Outcome);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(first.Outcome, replayAgain.Outcome);

        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Single(history.Events);
        Assert.Equal(1, history.Revision);
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("producer")]
    [InlineData("prior")]
    [InlineData("current")]
    [InlineData("occurred")]
    [InlineData("revision")]
    [InlineData("record")]
    [InlineData("scope")]
    public async Task A_stored_event_id_with_any_differing_field_is_Conflict_and_writes_nothing(string difference)
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        var other = Minimal(Scope(tenant, project: "project-2"));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        await _store.CreateAsync(auth, other, CancellationToken.None);

        var original = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await _store.CommitLifecycleEventAsync(auth, record.Scope, original, CancellationToken.None)).Outcome);

        var scope = record.Scope;
        var diverged = difference switch
        {
            "reason" => original with { Reason = original.Reason + "!" },
            "producer" => original with { Producer = "someone-else" },
            "prior" => original with { PriorStatus = null },
            "current" => original with { CurrentStatus = ExperienceStatus.Revoked },
            "occurred" => original with { OccurredAt = original.OccurredAt.AddSeconds(1) },
            "revision" => original with { ExpectedRevision = 1 },
            "record" => original with { ExperienceRecordId = other.ExperienceId },
            _ => original,
        };

        if (difference == "scope")
        {
            scope = other.Scope;
        }

        var result = await _store.CommitLifecycleEventAsync(auth, scope, diverged, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, result.Outcome);
        Assert.Equal(0, result.Revision);
        Assert.Empty(result.Errors);

        // Neither the stored event nor either record moved.
        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(original, Assert.Single(history.Events) with { OccurredAt = original.OccurredAt });
        Assert.Equal(1, history.Revision);
        var otherHistory = await _store.GetHistoryAsync(auth, other.Scope, other.ExperienceId, CancellationToken.None);
        Assert.Empty(otherHistory.Events);
        Assert.Equal(0, otherHistory.Revision);
    }

    [Fact]
    public async Task An_event_id_stored_in_a_foreign_tenant_conflicts_without_revealing_anything()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var record = Minimal(Scope(tenant));
        var foreignRecord = Minimal(Scope(foreignTenant));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        await _store.CreateAsync(Authorize(foreignTenant), foreignRecord, CancellationToken.None);

        var eventId = Guid.NewGuid();
        await _store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, eventId), CancellationToken.None);

        var result = await _store.CommitLifecycleEventAsync(
            Authorize(foreignTenant),
            foreignRecord.Scope,
            Event(foreignRecord.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, eventId),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, result.Outcome);
        Assert.Empty(result.Errors);
        var foreignHistory = await _store.GetHistoryAsync(Authorize(foreignTenant), foreignRecord.Scope, foreignRecord.ExperienceId, CancellationToken.None);
        Assert.Empty(foreignHistory.Events);
        Assert.Equal(0, foreignHistory.Revision);
    }

    [Fact]
    public async Task A_stale_expected_revision_writes_nothing_and_reports_the_current_revision()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);

        var behind = await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);
        var ahead = await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 5), CancellationToken.None);

        Assert.All([behind, ahead], result =>
        {
            Assert.Equal(ExperienceStoreOutcome.StaleRevision, result.Outcome);
            Assert.Equal(1, result.Revision);
            Assert.Empty(result.Errors);
        });

        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(1, stored.Revision);
        Assert.Single((await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task An_unknown_record_and_one_in_another_scope_are_both_NotFound()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant, project: "project-1", team: "team-1"));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        var missing = await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);
        var foreignScope = await _store.CommitLifecycleEventAsync(
            auth, Scope(tenant, project: "project-2", team: "team-1"), Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);
        var noTeam = await _store.CommitLifecycleEventAsync(
            auth, Scope(tenant, project: "project-1"), Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);

        Assert.All([missing, foreignScope, noTeam], result =>
        {
            Assert.Equal(ExperienceStoreOutcome.NotFound, result.Outcome);
            Assert.Equal(0, result.Revision);
            Assert.Empty(result.Errors);
        });

        // A foreign-scope attempt must not leave an orphan event behind.
        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Candidate, stored.Status);
        Assert.Equal(0, stored.Revision);
        Assert.Empty((await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Events);
        Assert.Equal(0, await CountEventsAsync(record.ExperienceId));
    }

    [Fact]
    public async Task History_of_a_record_in_another_scope_is_NotFound_like_a_missing_one()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant, team: "team-1"));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);

        var otherTeam = await _store.GetHistoryAsync(auth, Scope(tenant, team: "team-2"), record.ExperienceId, CancellationToken.None);
        var missing = await _store.GetHistoryAsync(auth, record.Scope, Guid.NewGuid(), CancellationToken.None);

        Assert.All([otherTeam, missing], result =>
        {
            Assert.Equal(ExperienceStoreOutcome.NotFound, result.Outcome);
            Assert.Equal(0, result.Revision);
            Assert.Empty(result.Events);
            Assert.Empty(result.Errors);
        });
    }

    [Fact]
    public async Task History_returns_every_step_oldest_first_with_the_record_s_current_revision()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        var validated = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        var quarantined = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Quarantined, 1, reason: "sanitization gap suspected");
        var revoked = Event(record.ExperienceId, ExperienceStatus.Quarantined, ExperienceStatus.Revoked, 2, reason: "withdrawn by policy", producer: "governance");

        foreach (var step in new[] { validated, quarantined, revoked })
        {
            Assert.Equal(
                ExperienceStoreOutcome.Committed,
                (await _store.CommitLifecycleEventAsync(auth, record.Scope, step, CancellationToken.None)).Outcome);
        }

        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(3, history.Revision);
        Assert.Equal([validated.EventId, quarantined.EventId, revoked.EventId], history.Events.Select(e => e.EventId));
        Assert.Equal(
            [
                (ExperienceStatus.Candidate, ExperienceStatus.Validated),
                (ExperienceStatus.Validated, ExperienceStatus.Quarantined),
                (ExperienceStatus.Quarantined, ExperienceStatus.Revoked),
            ],
            history.Events.Select(e => (e.PriorStatus, e.CurrentStatus)));
        Assert.Equal([0L, 1L, 2L], history.Events.Select(e => e.ExpectedRevision));
        Assert.Equal("withdrawn by policy", history.Events[^1].Reason);
        Assert.Equal("governance", history.Events[^1].Producer);

        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Revoked, stored.Status);
        Assert.Equal(3, stored.Revision);
    }

    [Fact]
    public async Task History_of_a_record_with_no_events_is_Found_and_empty()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var history = await _store.GetHistoryAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(0, history.Revision);
        Assert.Empty(history.Events);
    }

    [Fact]
    public async Task A_first_lifecycle_event_may_carry_a_null_prior_status()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var result = await _store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, null, ExperienceStatus.Quarantined, 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        var history = await _store.GetHistoryAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Null(Assert.Single(history.Events).PriorStatus);
    }

    [Fact]
    public async Task Two_commits_from_the_same_revision_race_to_exactly_one_winner()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        var validate = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        var revoke = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0);

        var results = await Task.WhenAll(
            _store.CommitLifecycleEventAsync(auth, record.Scope, validate, CancellationToken.None),
            _store.CommitLifecycleEventAsync(auth, record.Scope, revoke, CancellationToken.None));

        Assert.Equal(1, results.Count(r => r.Outcome == ExperienceStoreOutcome.Committed));
        Assert.Equal(1, results.Count(r => r.Outcome == ExperienceStoreOutcome.StaleRevision));

        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(1, stored.Revision);

        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(1, history.Revision);
        var winner = Assert.Single(history.Events);
        Assert.Equal(winner.CurrentStatus, stored.Status);

        // The loser's event never reached the log, so the log matches the projection exactly.
        Assert.Equal(1, await CountEventsAsync(record.ExperienceId));
    }

    [Fact]
    public async Task A_failure_after_the_event_insert_persists_neither_write()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        // The trigger below fires only for this task id, so no other test in the collection is affected.
        var record = Minimal(Scope(tenant)) with { TaskId = "fail-mid-commit" };
        await _store.CreateAsync(auth, record, CancellationToken.None);

        await ExecuteAsync("""
            CREATE OR REPLACE FUNCTION agent_experience.fail_mid_commit() RETURNS trigger AS $body$
            BEGIN
                RAISE EXCEPTION 'deliberate failure between the event insert and the projection update';
            END;
            $body$ LANGUAGE plpgsql;

            CREATE TRIGGER fail_mid_commit
                BEFORE UPDATE ON agent_experience.experience_records
                FOR EACH ROW WHEN (NEW.task_id = 'fail-mid-commit')
                EXECUTE FUNCTION agent_experience.fail_mid_commit();
            """);

        try
        {
            var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);

            var ex = await Assert.ThrowsAsync<ExperienceStoreException>(
                () => _store.CommitLifecycleEventAsync(auth, record.Scope, lifecycleEvent, CancellationToken.None));
            Assert.IsAssignableFrom<Npgsql.NpgsqlException>(ex.InnerException);

            // Neither write survives: no event row, and the projection is untouched.
            Assert.Equal(0, await CountEventsAsync(record.ExperienceId));
            var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
            Assert.Equal(ExperienceStatus.Candidate, stored.Status);
            Assert.Equal(0, stored.Revision);
            Assert.Equal(record.UpdatedAt, stored.UpdatedAt);
        }
        finally
        {
            await ExecuteAsync(
                "DROP TRIGGER IF EXISTS fail_mid_commit ON agent_experience.experience_records; " +
                "DROP FUNCTION IF EXISTS agent_experience.fail_mid_commit();");
        }

        // With the trigger gone the same event id commits normally, proving nothing was left half-written.
        var retry = await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, retry.Outcome);
        Assert.Equal(1, retry.Revision);
    }

    [Fact]
    public async Task Cancelling_a_commit_mid_flight_surfaces_an_unwrapped_OperationCanceledException_and_writes_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        // Hold the record's row so the commit's projection update blocks after its event insert, giving
        // the token a real in-flight command to cancel (rather than the pre-flight guard).
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        var blocking = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand(
            "SELECT revision FROM agent_experience.experience_records WHERE experience_id = @id FOR UPDATE", blocker, blocking))
        {
            hold.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            Assert.Equal(0L, await hold.ExecuteScalarAsync());
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _store.CommitLifecycleEventAsync(auth, record.Scope, lifecycleEvent, cts.Token));
            Assert.IsNotType<ExperienceStoreException>(ex);
        }
        finally
        {
            await blocking.RollbackAsync();
            await blocking.DisposeAsync();
        }

        Assert.Equal(0, await CountEventsAsync(record.ExperienceId));
        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Candidate, stored.Status);
        Assert.Equal(0, stored.Revision);
    }

    [Fact]
    public async Task Stored_event_rows_carry_the_scope_the_statuses_and_both_revisions()
    {
        var tenant = NewTenant();
        var scope = new Scope(tenant, "app-1", "project-1", "team-1", null, "user-1");
        var record = Minimal(scope);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        await _store.CommitLifecycleEventAsync(Authorize(tenant), scope, lifecycleEvent, CancellationToken.None);

        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT tenant_id, team_id, agent_id, user_id, prior_status, current_status, expected_revision, applied_revision, " +
            "occurred_at, recorded_at FROM agent_experience.lifecycle_events WHERE event_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", lifecycleEvent.EventId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(tenant, reader.GetString(0));
        Assert.Equal("team-1", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("user-1", reader.GetString(3));
        Assert.Equal("Candidate", reader.GetString(4));
        Assert.Equal("Validated", reader.GetString(5));
        Assert.Equal(0L, reader.GetInt64(6));
        Assert.Equal(1L, reader.GetInt64(7));
        // Sub-microsecond ticks are truncated on write, exactly as the record's own columns are.
        Assert.Equal(PayloadTime.AddTicks(-1), reader.GetFieldValue<DateTimeOffset>(8));
        Assert.True(reader.GetFieldValue<DateTimeOffset>(9) >= reader.GetFieldValue<DateTimeOffset>(8));
    }

    [Fact]
    public async Task The_schema_rejects_an_event_that_bypasses_the_store()
    {
        // The projection and the log can only stay consistent if applied_revision follows expected_revision.
        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.lifecycle_events (event_id, experience_id, tenant_id, application_id, project_id, " +
            "prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), 'tenant', 'app', 'proj', NULL, 'Revoked', 'because', 'tests', now(), now(), 3, 7)");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        await using var blank = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.lifecycle_events (event_id, experience_id, tenant_id, application_id, project_id, " +
            "prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), '  ', 'app', 'proj', NULL, 'Revoked', 'because', 'tests', now(), now(), 0, 1)");

        var blankTenant = await Assert.ThrowsAsync<PostgresException>(() => blank.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, blankTenant.SqlState);
    }

    [Fact]
    public async Task A_corrupt_stored_status_throws_ExperienceStoreException_on_history()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, lifecycleEvent, CancellationToken.None);

        await using (var corrupt = _fixture.DataSource.CreateCommand(
            "UPDATE agent_experience.lifecycle_events SET current_status = 'validated' WHERE event_id = @id"))
        {
            corrupt.Parameters.Add(new NpgsqlParameter<Guid>("id", lifecycleEvent.EventId));
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var ex = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => _store.GetHistoryAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None));

        // The message must name the row that is actually corrupt, not the record.
        Assert.Equal("Stored lifecycle event has an unrecognized status.", ex.Message);
    }

    [Fact]
    public async Task A_prior_status_the_record_is_not_in_writes_nothing_and_reports_the_stored_status()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        // Move the record to Quarantined, so its stored status no longer matches what a stale caller holds.
        await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Quarantined, 0), CancellationToken.None);

        // Revision 1 is correct, but the record is Quarantined, not Candidate. Without the prior-status
        // guard this would commit a Candidate -> Validated transition Core forbids from Quarantined, and
        // store a prior status the record never had.
        var dishonest = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 1);
        var result = await _store.CommitLifecycleEventAsync(auth, record.Scope, dishonest, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.StatusMismatch, result.Outcome);
        Assert.Equal(1, result.Revision);
        Assert.Equal(ExperienceStatus.Quarantined, result.CurrentStatus);
        Assert.Empty(result.Errors);

        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Quarantined, stored.Status);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(1, await CountEventsAsync(record.ExperienceId));
    }

    [Fact]
    public async Task A_transition_Core_forbids_is_also_refused_by_the_store_when_asserted_dishonestly()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);

        // Revoked -> Quarantined is outside Core's table. A caller that routes around Core by asserting a
        // prior status the record is not in still cannot commit it.
        var result = await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Quarantined, 1), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.StatusMismatch, result.Outcome);
        Assert.Equal(ExperienceStatus.Revoked, result.CurrentStatus);

        var stored = (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Revoked, stored.Status);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(1, await CountEventsAsync(record.ExperienceId));
    }

    [Fact]
    public async Task A_stale_revision_is_reported_even_when_the_prior_status_also_differs()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);

        // Both guards fail; the revision is the one the caller must fix first.
        var result = await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Quarantined, 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.StaleRevision, result.Outcome);
        Assert.Equal(1, result.Revision);
        Assert.Null(result.CurrentStatus);
    }

    [Fact]
    public async Task Replaying_an_event_after_a_later_commit_reports_its_own_revision_not_the_current_one()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        var first = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        var second = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Quarantined, 1);
        var third = Event(record.ExperienceId, ExperienceStatus.Quarantined, ExperienceStatus.Revoked, 2);
        foreach (var step in new[] { first, second, third })
        {
            Assert.Equal(
                ExperienceStoreOutcome.Committed,
                (await _store.CommitLifecycleEventAsync(auth, record.Scope, step, CancellationToken.None)).Outcome);
        }

        // The record is now at revision 3, so 1 can only come from the stored event's own applied revision.
        var replay = await _store.CommitLifecycleEventAsync(auth, record.Scope, first, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(1, replay.Revision);
        Assert.Null(replay.CurrentStatus);

        var middle = await _store.CommitLifecycleEventAsync(auth, record.Scope, second, CancellationToken.None);
        Assert.Equal(2, middle.Revision);

        // Nothing was written by either replay.
        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(3, history.Revision);
        Assert.Equal([first.EventId, second.EventId, third.EventId], history.Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task History_reports_a_revision_and_events_from_one_snapshot()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(auth, record, CancellationToken.None);
        await _store.CommitLifecycleEventAsync(
            auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);

        // Whatever else is happening, the revision must equal the highest applied revision in the events.
        var history = await _store.GetHistoryAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(history.Events.Max(e => e.ExpectedRevision) + 1, history.Revision);
    }

    private async Task<long> CountEventsAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _fixture.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
