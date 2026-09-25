using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 3.2 against a real PostgreSQL 16 container: the completed transition table driven end to end,
/// supersession's recorded replacement and its refusals (self, cross-scope, ineligible, cyclic), the
/// bounded and cursored history, and the two event logs now being append-only in the database rather
/// than by convention. Each test uses its own random tenant, so tests sharing the container never see
/// each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSupersessionAndAppendOnlyTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly ExperienceLifecycleService _lifecycle;

    public PostgresSupersessionAndAppendOnlyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _lifecycle = new ExperienceLifecycleService(_store);
    }

    [Theory]
    [InlineData(ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Superseded)]
    public async Task A_validated_record_is_reinforced_then_leaves_eligibility_then_is_revoked(ExperienceStatus exit)
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);

        var record = await ValidatedAsync(auth, scope);
        var replacement = exit == ExperienceStatus.Superseded ? (await ValidatedAsync(auth, scope)).ExperienceId : (Guid?)null;

        // Reinforced is still eligible, so the record keeps being retrievable across this step.
        Assert.Equal(ExperienceStatus.Validated, await StatusAsync(auth, scope, record.ExperienceId));
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 1);
        Assert.Contains(await StatusAsync(auth, scope, record.ExperienceId), ExperienceRetrievalService.EligibleStatuses);

        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Reinforced, exit, 2, replacement);
        Assert.DoesNotContain(await StatusAsync(auth, scope, record.ExperienceId), ExperienceRetrievalService.EligibleStatuses);

        await CommitAsync(auth, scope, record.ExperienceId, exit, ExperienceStatus.Revoked, 3);

        var history = await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(4, history.Revision);
        Assert.Equal(
            [
                ((ExperienceStatus?)ExperienceStatus.Candidate, ExperienceStatus.Validated),
                (ExperienceStatus.Validated, ExperienceStatus.Reinforced),
                (ExperienceStatus.Reinforced, exit),
                (exit, ExperienceStatus.Revoked),
            ],
            history.Events.Select(e => (e.Event.PriorStatus, e.Event.CurrentStatus)));

        // The replacement is on exactly the superseding event and on no other.
        Assert.Equal(
            replacement,
            history.Events.Single(e => e.Event.CurrentStatus == exit).Event.ReplacementExperienceId);
        Assert.All(
            history.Events.Where(e => e.Event.CurrentStatus != ExperienceStatus.Superseded),
            e => Assert.Null(e.Event.ReplacementExperienceId));
    }

    [Fact]
    public async Task A_supersession_stores_the_replacement_and_reads_it_back()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var replacement = await ValidatedAsync(auth, scope);

        var result = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, replacement.ExperienceId),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);

        var stored = await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        var superseding = stored.Events[^1];
        Assert.Equal(ExperienceStatus.Superseded, superseding.Event.CurrentStatus);
        Assert.Equal(replacement.ExperienceId, superseding.Event.ReplacementExperienceId);

        // And the column really is on the row, not reconstructed from anywhere else.
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT replacement_experience_id FROM agent_experience.lifecycle_events WHERE event_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", superseding.Event.EventId));
        Assert.Equal(replacement.ExperienceId, (Guid)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Replaying_a_supersession_with_a_different_replacement_is_a_conflict()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var first = await ValidatedAsync(auth, scope);
        var second = await ValidatedAsync(auth, scope);

        var eventId = Guid.NewGuid();
        var original = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, eventId, replacement: first.ExperienceId);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await _store.CommitLifecycleEventAsync(auth, scope, original, CancellationToken.None)).Outcome);

        var replay = await _store.CommitLifecycleEventAsync(auth, scope, original, CancellationToken.None);
        var diverged = await _store.CommitLifecycleEventAsync(
            auth, scope, original with { ReplacementExperienceId = second.ExperienceId }, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(2, replay.Revision);
        Assert.Equal(ExperienceStatus.Superseded, (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!.Status);
        Assert.Equal(ExperienceStoreOutcome.Conflict, diverged.Outcome);
        Assert.Equal(
            first.ExperienceId,
            (await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Events[^1].Event.ReplacementExperienceId);
    }

    [Fact]
    public async Task A_replacement_that_is_the_record_itself_is_refused_before_any_write()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var result = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, record.ExperienceId),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, result.Outcome);
        await AssertUnchangedAsync(auth, scope, record.ExperienceId, ExperienceStatus.Validated, 1, events: 1);
    }

    [Fact]
    public async Task A_replacement_in_another_scope_is_refused_and_reveals_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var otherScope = Scope(tenant, project: "project-2");
        var record = await ValidatedAsync(auth, scope);
        var foreign = await ValidatedAsync(auth, otherScope);
        var missing = Guid.NewGuid();

        var crossScope = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, foreign.ExperienceId),
            CancellationToken.None);
        var absent = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, missing),
            CancellationToken.None);

        // Indistinguishable: a replacement in another scope tells the caller exactly as much as one
        // that does not exist at all.
        Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, crossScope.Outcome);
        Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, absent.Outcome);
        Assert.Equal(crossScope.Reason, absent.Reason);

        await AssertUnchangedAsync(auth, scope, record.ExperienceId, ExperienceStatus.Validated, 1, events: 1);
        await AssertUnchangedAsync(auth, otherScope, foreign.ExperienceId, ExperienceStatus.Validated, 1, events: 1);
    }

    [Theory]
    [InlineData(ExperienceStatus.Quarantined)]
    [InlineData(ExperienceStatus.Revoked)]
    [InlineData(ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Stale)]
    public async Task An_ineligible_replacement_is_refused(ExperienceStatus replacementStatus)
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var replacement = await ValidatedAsync(auth, scope);

        // Walk the replacement out of eligibility through the real table.
        if (replacementStatus == ExperienceStatus.Quarantined)
        {
            // Quarantine is only reachable from Candidate, so this one starts from a fresh record.
            replacement = Minimal(scope);
            await _store.CreateAsync(auth, replacement, CancellationToken.None);
            await CommitAsync(auth, scope, replacement.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Quarantined, 0);
        }
        else
        {
            await CommitAsync(auth, scope, replacement.ExperienceId, ExperienceStatus.Validated, replacementStatus, 1);
        }

        var result = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, replacement.ExperienceId),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, result.Outcome);
        Assert.Contains(replacementStatus.ToString(), result.Reason!, StringComparison.Ordinal);
        await AssertUnchangedAsync(auth, scope, record.ExperienceId, ExperienceStatus.Validated, 1, events: 1);
    }

    [Fact]
    public async Task A_replacement_that_would_close_a_cycle_is_refused_directly_and_transitively()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);

        // a is superseded by b, then b by c. The chain now runs a -> b -> c.
        var a = await ValidatedAsync(auth, scope);
        var b = await ValidatedAsync(auth, scope);
        var c = await ValidatedAsync(auth, scope);
        var unrelated = await ValidatedAsync(auth, scope);

        await SupersedeAsync(auth, scope, a.ExperienceId, b.ExperienceId);
        await SupersedeAsync(auth, scope, b.ExperienceId, c.ExperienceId);

        // Directly: a already replaces b's predecessor, so making a replace b closes the loop a -> b -> a.
        var direct = await _store.CheckSupersessionAsync(auth, scope, b.ExperienceId, a.ExperienceId, CancellationToken.None);

        // Transitively: c is at the end of the chain that starts at a, so a replacing c closes it too.
        var transitive = await _store.CheckSupersessionAsync(auth, scope, c.ExperienceId, a.ExperienceId, CancellationToken.None);

        // And a record that is on no chain at all is not a cycle, however long the chain beside it is.
        var allowed = await _store.CheckSupersessionAsync(auth, scope, unrelated.ExperienceId, c.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.Cycle, direct.Outcome);
        Assert.Equal(ExperienceSupersessionOutcome.Cycle, transitive.Outcome);
        Assert.Equal(ExperienceSupersessionOutcome.Allowed, allowed.Outcome);
        Assert.Equal(ExperienceStatus.Validated, allowed.ReplacementStatus);
    }

    [Fact]
    public async Task A_cyclic_supersession_is_refused_through_the_lifecycle_service_with_nothing_written()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var a = await ValidatedAsync(auth, scope);
        var b = await ValidatedAsync(auth, scope);

        await SupersedeAsync(auth, scope, a.ExperienceId, b.ExperienceId);

        // b is eligible and in scope, but a already replaces it, so b cannot be superseded by a.
        var result = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, b.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, a.ExperienceId),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        await AssertUnchangedAsync(auth, scope, b.ExperienceId, ExperienceStatus.Validated, 1, events: 1);

        // The refusal is reported against the replacement's own status (a is Superseded by now, which is
        // ineligible on its own), so the cycle itself is asserted against the check that decides it.
        Assert.Equal(
            ExperienceSupersessionOutcome.Cycle,
            (await _store.CheckSupersessionAsync(auth, scope, b.ExperienceId, a.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_cycle_already_in_the_log_does_not_make_the_check_run_forever()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Two hand-written rows that close a loop. The store's commit path refuses to *add* the closing
        // link, but it has never been able to unwrite one, and a database written through 0001-0005 --
        // before Core's table was enforced anywhere -- could hold any pair of them. The walk has to
        // terminate over whatever it finds.
        await InsertSupersedingEventAsync(scope, a, b);
        await InsertSupersedingEventAsync(scope, b, a);

        var record = await ValidatedAsync(auth, scope);
        var check = await _store.CheckSupersessionAsync(auth, scope, record.ExperienceId, a, CancellationToken.None);

        // It terminates, and it reports the replacement as absent rather than hanging on the loop.
        Assert.Equal(ExperienceSupersessionOutcome.ReplacementNotFound, check.Outcome);
    }

    [Fact]
    public async Task A_supersession_check_never_leaves_the_requesting_scope()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var scope = Scope(tenant);
        var record = await ValidatedAsync(Authorize(tenant), scope);
        var foreign = await ValidatedAsync(Authorize(foreignTenant), Scope(foreignTenant));

        var outsideAuthorization = await _store.CheckSupersessionAsync(
            Authorize(tenant), Scope(foreignTenant), record.ExperienceId, foreign.ExperienceId, CancellationToken.None);
        var foreignReplacement = await _store.CheckSupersessionAsync(
            Authorize(tenant), scope, record.ExperienceId, foreign.ExperienceId, CancellationToken.None);
        var missingRecord = await _store.CheckSupersessionAsync(
            Authorize(tenant), scope, Guid.NewGuid(), record.ExperienceId, CancellationToken.None);
        var malformed = await _store.CheckSupersessionAsync(
            Authorize(tenant), scope, Guid.Empty, Guid.Empty, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.Denied, outsideAuthorization.Outcome);
        Assert.Equal(ExperienceSupersessionOutcome.ReplacementNotFound, foreignReplacement.Outcome);
        Assert.Null(foreignReplacement.ReplacementStatus);
        Assert.Equal(ExperienceSupersessionOutcome.RecordNotFound, missingRecord.Outcome);
        Assert.Equal(ExperienceSupersessionOutcome.Invalid, malformed.Outcome);
        Assert.Equal(
            ["ExperienceId", "ReplacementExperienceId"],
            malformed.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task History_pages_with_a_keyset_cursor_and_a_record_with_nothing_left_stays_Found()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = Minimal(scope);
        await _store.CreateAsync(auth, record, CancellationToken.None);

        await CommitAsync(auth, scope, record.ExperienceId, null, ExperienceStatus.Candidate, 0);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 1);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 2);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Reinforced, ExperienceStatus.Stale, 3);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Stale, ExperienceStatus.Revoked, 4);

        var walked = new List<StoredLifecycleEvent>();
        long? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await _store.GetHistoryAsync(
                auth,
                new ExperienceRecordHistoryQuery(scope, record.ExperienceId, Limit: 2, StartAfterRevision: cursor),
                CancellationToken.None);

            // Every page -- including the one past the end -- reports the record, its revision, and its
            // page. Found with nothing is never mistaken for NotFound.
            Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
            Assert.Equal(5, result.Revision);
            Assert.True(result.Events.Count <= 2);

            if (result.Events.Count == 0)
            {
                Assert.Null(result.NextStartAfterRevision);
                break;
            }

            walked.AddRange(result.Events);
            cursor = result.NextStartAfterRevision;
            Assert.Equal(result.Events[^1].AppliedRevision, cursor);
        }

        // Five events, in order, with no gap and no repetition.
        Assert.Equal([1L, 2L, 3L, 4L, 5L], walked.Select(e => e.AppliedRevision));
        Assert.Equal(5, walked.Select(e => e.Event.EventId).Distinct().Count());
    }

    [Fact]
    public async Task A_history_cursor_past_the_end_is_Found_and_empty_while_a_missing_record_is_NotFound()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var exhausted = await _store.GetHistoryAsync(
            auth, new ExperienceRecordHistoryQuery(scope, record.ExperienceId, StartAfterRevision: 999), CancellationToken.None);
        var missing = await _store.GetHistoryAsync(
            auth, new ExperienceRecordHistoryQuery(scope, Guid.NewGuid(), StartAfterRevision: 999), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, exhausted.Outcome);
        Assert.Equal(1, exhausted.Revision);
        Assert.Empty(exhausted.Events);
        Assert.Null(exhausted.NextStartAfterRevision);

        Assert.Equal(ExperienceStoreOutcome.NotFound, missing.Outcome);
        Assert.Equal(0, missing.Revision);
    }

    [Fact]
    public async Task A_malformed_history_request_is_Invalid_with_a_field_path()
    {
        var tenant = NewTenant();

        var result = await _store.GetHistoryAsync(
            Authorize(tenant),
            new ExperienceRecordHistoryQuery(Scope(tenant), Guid.NewGuid(), Limit: 0, StartAfterRevision: -1),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["Limit", "StartAfterRevision"], result.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_schema_refuses_a_superseding_event_with_no_replacement_and_a_replacement_on_anything_else()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        // The store's validator catches both first, as typed Invalid results with field paths...
        var noReplacement = await _store.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(Guid.NewGuid(), ExperienceStatus.Validated, ExperienceStatus.Superseded, 0), CancellationToken.None);
        var strayReplacement = await _store.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(Guid.NewGuid(), ExperienceStatus.Validated, ExperienceStatus.Stale, 0, replacement: Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, noReplacement.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Invalid, strayReplacement.Outcome);
        Assert.Equal("ReplacementExperienceId", Assert.Single(noReplacement.Errors).Path);
        Assert.Equal("ReplacementExperienceId", Assert.Single(strayReplacement.Errors).Path);

        // ...and the database states the same rule, so a writer that bypasses the store still meets it.
        var stray = await Assert.ThrowsAsync<PostgresException>(
            () => InsertEventAsync(scope, Guid.NewGuid(), "Stale", Guid.NewGuid()));
        var missing = await Assert.ThrowsAsync<PostgresException>(
            () => InsertEventAsync(scope, Guid.NewGuid(), "Superseded", null));

        Assert.Equal(PostgresErrorCodes.CheckViolation, stray.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, missing.SqlState);
        Assert.Equal("lifecycle_events_replacement_only_when_superseded", stray.ConstraintName);
        Assert.Equal("lifecycle_events_replacement_only_when_superseded", missing.ConstraintName);

        // And a row that names itself as its own replacement cannot be stored either.
        var id = Guid.NewGuid();
        var selfReplacing = await Assert.ThrowsAsync<PostgresException>(() => InsertEventAsync(scope, id, "Superseded", id));
        Assert.Equal("lifecycle_events_replacement_is_another_record", selfReplacing.ConstraintName);
    }

    [Fact]
    public async Task A_stored_lifecycle_event_cannot_be_updated_or_deleted_by_a_writer_holding_the_privilege()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var stored = (await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Events[^1];

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.lifecycle_events SET reason = 'rewritten' WHERE event_id = @id", stored.Event.EventId));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.lifecycle_events WHERE event_id = @id", stored.Event.EventId));

        // A tamperer is told this is a privilege failure, not an incidental constraint.
        Assert.All([update, delete], ex =>
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
        });

        // The trail is exactly as it was.
        var after = await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(stored, after.Events[^1]);
        Assert.Single(after.Events);
    }

    [Fact]
    public async Task An_unqualified_update_or_delete_across_the_whole_event_log_is_refused_too()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        await ValidatedAsync(auth, scope);

        // The trigger is per row, so a statement that would have rewritten everything fails on the
        // first row it reaches and takes its whole transaction with it.
        var wipe = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync("DELETE FROM agent_experience.lifecycle_events", id: null));
        var rewrite = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync("UPDATE agent_experience.lifecycle_events SET producer = 'nobody'", id: null));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, wipe.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rewrite.SqlState);
        Assert.True(await CountEventsAsync() > 0);
    }

    [Fact]
    public async Task A_grant_revocation_cannot_be_cleared_and_its_expiry_cannot_be_extended()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Minimal(scope);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var administration = new GrantAdministration("admin-1", ColumnTime);
        var grantId = Guid.NewGuid();
        var issued = await grants.CreateAsync(
            Authorize(tenant),
            administration,
            new ExperienceGrantRequest(
                grantId,
                record.ExperienceId,
                scope,
                scope with { TeamId = "team-2" },
                "shared for review",
                DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, issued.Outcome);

        // An expiry that only ever moves closer: shortening is permitted, extending is not.
        Assert.Equal(1, await ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET expires_at = expires_at - interval '10 minutes' WHERE grant_id = @id", grantId));
        var extended = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET expires_at = expires_at + interval '1 year' WHERE grant_id = @id", grantId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, extended.SqlState);
        Assert.Contains("extended", extended.MessageText, StringComparison.Ordinal);

        Assert.Equal(
            ExperienceGrantOutcome.Revoked,
            (await grants.RevokeAsync(
                Authorize(tenant),
                administration,
                new ExperienceGrantRevocation(grantId, scope, "no longer needed"),
                CancellationToken.None)).Outcome);

        var cleared = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET revoked_at = NULL, revocation_reason = NULL WHERE grant_id = @id", grantId));
        var moved = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET revoked_at = now() + interval '1 day' WHERE grant_id = @id", grantId));
        var reworded = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET revocation_reason = 'never happened' WHERE grant_id = @id", grantId));

        Assert.All([cleared, moved, reworded], ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));

        // And the grant's own audit log is as untouchable as the lifecycle one.
        var grantEvent = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_grant_events SET reason = 'rewritten' WHERE grant_id = @id", grantId));
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.experience_grant_events WHERE grant_id = @id", grantId));
        Assert.All([grantEvent, deleted], ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));

        var history = await grants.GetHistoryAsync(Authorize(tenant), scope, grantId, CancellationToken.None);
        Assert.Equal(2, history.Events.Count);
        Assert.NotNull(history.Grant!.RevokedAt);
    }

    [Fact]
    public async Task Neither_event_log_can_be_truncated_away()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        await ValidatedAsync(auth, scope);
        var before = await CountEventsAsync();

        // TRUNCATE does not fire FOR EACH ROW triggers at all, so without a statement-level trigger it
        // would erase a whole audit log with no error whatsoever.
        var events = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync("TRUNCATE agent_experience.lifecycle_events", id: null));
        var grantEvents = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync("TRUNCATE agent_experience.experience_grant_events", id: null));
        var grants = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync("TRUNCATE agent_experience.experience_grants", id: null));

        Assert.All([events, grantEvents, grants], ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));
        Assert.Equal(before, await CountEventsAsync());
    }

    [Fact]
    public async Task The_guards_are_not_skipped_in_replica_mode()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var stored = (await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Events[^1];

        // session_replication_role = 'replica' is what a logical-replication applier and several restore
        // and ETL tools run in, and it skips an ordinary ENABLE trigger silently. These are ENABLE ALWAYS.
        // Setting it needs a superuser, and a superuser holds every privilege, so what refuses the writes
        // below is the trigger and nothing else.
        await using var connection = await _fixture.SuperuserDataSource.OpenConnectionAsync();
        await using (var mode = new NpgsqlCommand("SET session_replication_role = 'replica'", connection))
        {
            await mode.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[]
        {
            "UPDATE agent_experience.lifecycle_events SET reason = 'rewritten by a replica apply' WHERE event_id = @id",
            "DELETE FROM agent_experience.lifecycle_events WHERE event_id = @id",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", stored.Event.EventId));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var truncate = new NpgsqlCommand("TRUNCATE agent_experience.lifecycle_events", connection))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.Equal(stored, (await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Events[^1]);
    }

    [Fact]
    public async Task A_revoked_grant_cannot_be_deleted_and_reinserted_unrevoked()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Minimal(scope);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var administration = new GrantAdministration("admin-1", ColumnTime);
        var grantId = Guid.NewGuid();
        await grants.CreateAsync(
            Authorize(tenant),
            administration,
            new ExperienceGrantRequest(grantId, record.ExperienceId, scope, scope with { TeamId = "team-2" }, "shared", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        await grants.RevokeAsync(
            Authorize(tenant), administration, new ExperienceGrantRevocation(grantId, scope, "ended"), CancellationToken.None);

        // Clearing revoked_at is already refused; deleting the row and inserting it again would have had
        // exactly the same effect, with the audit trail still claiming the grant was revoked.
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.experience_grants WHERE grant_id = @id", grantId));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleted.SqlState);
        Assert.NotNull((await grants.GetHistoryAsync(Authorize(tenant), scope, grantId, CancellationToken.None)).Grant!.RevokedAt);
    }

    [Fact]
    public async Task A_live_grant_cannot_be_re_pointed_at_another_record_or_recipient()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Minimal(scope);
        var other = Minimal(scope);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        await _store.CreateAsync(Authorize(tenant), other, CancellationToken.None);

        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var grantId = Guid.NewGuid();
        await grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration("admin-1", ColumnTime),
            new ExperienceGrantRequest(grantId, record.ExperienceId, scope, scope with { TeamId = "team-2" }, "shared", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);

        // Each of these would hand out access an administrator never issued, while the audit trail kept
        // describing the grant that was.
        foreach (var sql in new[]
        {
            "UPDATE agent_experience.experience_grants SET experience_id = @other WHERE grant_id = @id",
            "UPDATE agent_experience.experience_grants SET recipient_team_id = 'team-9' WHERE grant_id = @id",
            "UPDATE agent_experience.experience_grants SET reason = 'something else entirely' WHERE grant_id = @id",
        })
        {
            await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", grantId));
            command.Parameters.Add(new NpgsqlParameter<Guid>("other", other.ExperienceId));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        var stored = (await grants.GetHistoryAsync(Authorize(tenant), scope, grantId, CancellationToken.None)).Grant!;
        Assert.Equal(record.ExperienceId, stored.ExperienceId);
        Assert.Equal("team-2", stored.RecipientScope.TeamId);
    }

    [Fact]
    public async Task A_live_grants_disclosure_level_cannot_be_changed_in_place()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Minimal(scope);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var grantId = Guid.NewGuid();
        await grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration("admin-1", ColumnTime),
            new ExperienceGrantRequest(grantId, record.ExperienceId, scope, scope with { TeamId = "team-2" }, "shared", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);

        // Widening in place would disclose tool names nobody issued a grant for; the level is one of the
        // grant's identity pins, so the only way to change it is to revoke and issue a new grant.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET disclosure = 'LessonAndApproach' WHERE grant_id = @id", grantId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        var stored = (await grants.GetHistoryAsync(Authorize(tenant), scope, grantId, CancellationToken.None)).Grant!;
        Assert.Equal(ExperienceGrantDisclosure.LessonOnly, stored.Disclosure);
    }

    [Fact]
    public async Task The_record_projection_cannot_be_wound_back_or_moved_without_its_revision()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        // An immutable log beside a freely rewritable projection proves nothing: winding the revision
        // back would let a stored event apply a second time, and a bare status change would contradict
        // a log that says no such transition happened.
        var rewound = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_records SET revision = revision - 1 WHERE experience_id = @id", record.ExperienceId));
        var moved = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_records SET status = 'Revoked' WHERE experience_id = @id", record.ExperienceId));

        Assert.All([rewound, moved], ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));

        var stored = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(1, stored.Revision);

        // The store's own commit is unaffected: it moves the status and the revision together.
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 1);
        Assert.Equal(ExperienceStatus.Revoked, await StatusAsync(auth, scope, record.ExperienceId));
    }

    [Fact]
    public async Task Two_supersessions_naming_each_other_race_to_exactly_one_winner()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var a = await ValidatedAsync(auth, scope);
        var b = await ValidatedAsync(auth, scope);

        // Both pass a check taken outside any transaction -- neither chain exists yet. Only a check
        // taken inside the commit, behind the row locks, can stop them both committing the cycle.
        Assert.Equal(
            ExperienceSupersessionOutcome.Allowed,
            (await _store.CheckSupersessionAsync(auth, scope, a.ExperienceId, b.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceSupersessionOutcome.Allowed,
            (await _store.CheckSupersessionAsync(auth, scope, b.ExperienceId, a.ExperienceId, CancellationToken.None)).Outcome);

        var results = await Task.WhenAll(
            _lifecycle.CommitAsync(auth, Transition(scope, a.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, b.ExperienceId), CancellationToken.None),
            _lifecycle.CommitAsync(auth, Transition(scope, b.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, a.ExperienceId), CancellationToken.None));

        Assert.Equal(1, results.Count(r => r.Outcome == LifecycleTransitionOutcome.Committed));
        Assert.Equal(1, results.Count(r => r.Outcome == LifecycleTransitionOutcome.ReplacementNotAllowed));

        // Exactly one of the two is superseded, and the chain has no loop in it.
        var statuses = new[]
        {
            await StatusAsync(auth, scope, a.ExperienceId),
            await StatusAsync(auth, scope, b.ExperienceId),
        };
        Assert.Equal(1, statuses.Count(status => status == ExperienceStatus.Superseded));
        Assert.Equal(1, statuses.Count(status => status == ExperienceStatus.Validated));
        Assert.Equal(1, await CountSupersedingEventsAsync(a.ExperienceId, b.ExperienceId));
    }

    [Fact]
    public async Task Retrying_a_committed_supersession_reports_the_original_commit_even_after_the_replacement_moves_on()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var replacement = await ValidatedAsync(auth, scope);

        var request = Transition(scope, record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, replacement.ExperienceId);
        var first = await _lifecycle.CommitAsync(auth, request, CancellationToken.None);
        Assert.Equal(LifecycleTransitionOutcome.Committed, first.Outcome);

        // The replacement itself leaves eligibility. A retry of the identical event -- the retry a lost
        // acknowledgement calls for -- must still report the original commit rather than refusing it.
        await CommitAsync(auth, scope, replacement.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Stale, 1);

        var replay = await _lifecycle.CommitAsync(auth, request, CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, replay.Outcome);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Single(
            (await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Events,
            e => e.Event.CurrentStatus == ExperienceStatus.Superseded);
    }

    private async Task<long> CountSupersedingEventsAsync(params Guid[] experienceIds)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.lifecycle_events " +
            "WHERE experience_id = ANY(@ids) AND replacement_experience_id IS NOT NULL");
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            TypedValue = experienceIds,
        });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Creates a record as a <see cref="ExperienceStatus.Candidate"/> and commits its initial event,
    /// exactly as finalization does, leaving it <see cref="ExperienceStatus.Validated"/> at revision 1
    /// with one event.
    /// </summary>
    private async Task<ExperienceRecord> ValidatedAsync(AuthorizationContext auth, Scope scope)
    {
        var record = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        return record;
    }

    private async Task CommitAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        ExperienceStatus? prior,
        ExperienceStatus current,
        long expectedRevision,
        Guid? replacement = null)
    {
        var result = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, experienceId, prior, current, expectedRevision, replacement),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
    }

    private static CommitLifecycleTransitionRequest Transition(
        Scope scope,
        Guid experienceId,
        ExperienceStatus? prior,
        ExperienceStatus current,
        long expectedRevision,
        Guid? replacement) => new(
            EventId: Guid.NewGuid(),
            ExperienceId: experienceId,
            Scope: scope,
            PriorStatus: prior,
            CurrentStatus: current,
            Reason: $"moved to {current}",
            Producer: "tests",
            OccurredAt: PayloadTime,
            ExpectedRevision: expectedRevision,
            ReplacementExperienceId: replacement);

    private async Task<ExperienceStatus> StatusAsync(AuthorizationContext auth, Scope scope, Guid experienceId) =>
        (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!.Status;

    private async Task AssertUnchangedAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        ExperienceStatus status,
        long revision,
        int events)
    {
        var stored = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;
        Assert.Equal(status, stored.Status);
        Assert.Equal(revision, stored.Revision);

        var history = await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None);
        Assert.Equal(events, history.Events.Count);
        Assert.DoesNotContain(history.Events, e => e.Event.CurrentStatus == ExperienceStatus.Superseded);
    }

    private Task InsertSupersedingEventAsync(Scope scope, Guid experienceId, Guid replacementId) =>
        InsertEventAsync(scope, experienceId, "Superseded", replacementId);

    private async Task SupersedeAsync(AuthorizationContext auth, Scope scope, Guid experienceId, Guid replacementId)
    {
        var result = await _lifecycle.CommitAsync(
            auth,
            Transition(scope, experienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 1, replacementId),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
    }

    private async Task InsertEventAsync(Scope scope, Guid experienceId, string currentStatus, Guid? replacementId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.lifecycle_events (event_id, experience_id, tenant_id, application_id, project_id, " +
            "prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision, " +
            "replacement_experience_id) VALUES (gen_random_uuid(), @experience_id, @tenant, @app, @project, 'Validated', " +
            "@current_status, 'hand-written', 'tests', now(), now(), 0, 1, @replacement)");
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", scope.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("app", scope.ApplicationId));
        command.Parameters.Add(new NpgsqlParameter<string>("project", scope.ProjectId));
        command.Parameters.Add(new NpgsqlParameter<string>("current_status", currentStatus));
        command.Parameters.Add(new NpgsqlParameter("replacement", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = replacementId is { } id ? id : DBNull.Value,
        });

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs a hand-written statement as the tables' <em>owner</em>, which holds every table privilege: what
    /// these tests prove is that the triggers refuse a writer that is allowed to write. The application
    /// role is refused earlier, by the privilege system, which <c>PostgresApplicationRoleTests</c> proves.
    /// </summary>
    private async Task<int> ExecuteAsync(string sql, Guid? id)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        if (id is { } value)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", value));
        }

        return await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountEventsAsync()
    {
        await using var command = _fixture.DataSource.CreateCommand("SELECT count(*) FROM agent_experience.lifecycle_events");
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
