using AgentExperience.Core.Confidence;
using AgentExperience.Core.Lifecycle;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 3.4 against a real PostgreSQL container: the evidence ledger, its unique independence index,
/// the counters and score moving in the same transaction as the evidence row and the lifecycle event, the
/// duplicate that is recorded and counted zero times, the concurrent submission that loses on revision,
/// and the database refusing a direct rewrite of the confidence columns. Each test uses its own random
/// tenant, so tests sharing the container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresConfidenceEvidenceTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly ExperienceLifecycleService _lifecycle;

    public PostgresConfidenceEvidenceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _lifecycle = new ExperienceLifecycleService(_store);
    }

    [Fact]
    public async Task A_confirmation_then_a_contradiction_walk_the_record_from_two_thirds_to_three_quarters_to_three_fifths()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        // Initial: the finalized record carries one supporting validation and no contradictions.
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 3d, 1, 0, ExperienceStatus.Validated);

        var confirmation = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting));

        Assert.Equal(ConfidenceUpdateOutcome.Applied, confirmation.Outcome);
        Assert.True(confirmation.Counted);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        var contradiction = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Contradicting));

        Assert.Equal(ConfidenceUpdateOutcome.Applied, contradiction.Outcome);
        Assert.True(contradiction.Counted);

        // Three fifths, and Contested -- the status change is what takes it out of reuse, not the score.
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 5d, 2, 1, ExperienceStatus.Contested);

        // And the history reconstructs both: prior and new score, prior and new counters, the evidence
        // ID, the rule version, and the actor the commit ran under.
        var history = await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        var updates = history.Events.Where(e => e.Event.Confidence is not null).ToList();

        Assert.Equal(2, updates.Count);
        Assert.All(updates, stored =>
        {
            Assert.Equal(auth.PrincipalId, stored.Actor);
            Assert.Equal(ReuseConfidenceHeuristic.RuleVersion, stored.Event.Confidence!.RuleVersion);
            Assert.True(stored.Event.Confidence.Counted);
        });

        Assert.Equal(confirmation.Update!.EvidenceId, updates[0].Event.Confidence!.EvidenceId);
        Assert.Equal((2d / 3d, 3d / 4d), (updates[0].Event.Confidence!.PriorReuseConfidence, updates[0].Event.Confidence!.NewReuseConfidence));
        Assert.Equal((1, 2), (updates[0].Event.Confidence!.PriorSupportingValidations, updates[0].Event.Confidence!.NewSupportingValidations));

        Assert.Equal(contradiction.Update!.EvidenceId, updates[1].Event.Confidence!.EvidenceId);
        Assert.Equal((3d / 4d, 3d / 5d), (updates[1].Event.Confidence!.PriorReuseConfidence, updates[1].Event.Confidence!.NewReuseConfidence));
        Assert.Equal((0, 1), (updates[1].Event.Confidence!.PriorContradictions, updates[1].Event.Confidence!.NewContradictions));

        // Every event carries the actor now, including the initial transition.
        Assert.All(history.Events, stored => Assert.Equal(auth.PrincipalId, stored.Actor));
    }

    [Fact]
    public async Task The_same_run_and_round_under_a_new_evidence_id_is_stored_and_counted_zero_times()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var runId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        var first = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting, runId, roundId));
        Assert.True(first.Counted);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        var afterFirst = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;
        var eventsAfterFirst = await CountEventsAsync(record.ExperienceId);

        // A new evidence ID for the same observation. It is accepted and recorded; it moves nothing.
        var again = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting, runId, roundId));

        Assert.Equal(ConfidenceUpdateOutcome.Applied, again.Outcome);
        Assert.False(again.Counted);
        Assert.Equal(3d / 4d, again.ReuseConfidence);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        // Both submissions are in the ledger; exactly one of them counted.
        Assert.Equal(2, await CountEvidenceAsync(record.ExperienceId, counted: null));
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: true));

        // And it changed nothing else at all. The revision and updated_at matter as much as the counters:
        // retrieval ranks recency on UpdatedAt and expires on it, so a duplicate that refreshed it would
        // let one observation, replayed under fresh evidence IDs, keep a record permanently recent and
        // permanently un-expired.
        var after = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(afterFirst.Revision, after.Revision);
        Assert.Equal(afterFirst.UpdatedAt, after.UpdatedAt);
        Assert.Equal(afterFirst.Revision, again.Revision);

        // It wrote no lifecycle event either: an event must claim expected_revision + 1, so one that
        // moved nothing would consume a revision the record never reaches.
        Assert.Equal(eventsAfterFirst, await CountEventsAsync(record.ExperienceId));

        // The ledger row is the whole of the audit trail for it, and says plainly that nothing moved.
        var duplicate = await ReadLedgerAsync(again.Update!.EvidenceId);
        Assert.False(duplicate.Counted);
        Assert.Null(duplicate.EventId);
        Assert.Equal(afterFirst.Revision, duplicate.AppliedRevision);
        Assert.Equal(ExperienceStatus.Validated.ToString(), duplicate.AppliedStatus);
        Assert.Equal(duplicate.PriorSupporting, duplicate.NewSupporting);
    }

    [Fact]
    public async Task A_contradiction_sharing_a_key_with_a_counted_confirmation_contests_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var runId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        Assert.True((await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting, runId, roundId))).Counted);
        var afterFirst = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;

        // The same run and round, now pointing the other way. The independence rule has already counted
        // that observation, so this must not contest the record either -- contesting it on evidence that
        // was not counted would leave a ledger with zero counted contradictions beside a Contested record.
        var contradiction = await ApplyAsync(
            auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Contradicting, runId, roundId));

        Assert.Equal(ConfidenceUpdateOutcome.Applied, contradiction.Outcome);
        Assert.False(contradiction.Counted);
        Assert.Equal(ExperienceStatus.Validated, contradiction.Status);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        var after = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(afterFirst.Revision, after.Revision);
        Assert.Equal(afterFirst.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task An_evidence_id_from_another_scope_reveals_nothing_about_it()
    {
        var owner = NewTenant();
        var ownerAuth = Authorize(owner);
        var ownerScope = Scope(owner);
        var record = await ValidatedAsync(ownerAuth, ownerScope);
        var applied = await ApplyAsync(ownerAuth, Machine(ownerScope, record.ExperienceId, ConfidenceEvidenceKind.Supporting));

        // A stranger who guesses the evidence ID and submits under it must learn nothing: the primary key
        // is global, and this is the one lookup that would otherwise find a row by it alone.
        var stranger = NewTenant();
        var strangerAuth = Authorize(stranger);
        var strangerScope = Scope(stranger);
        var strangerRecord = await ValidatedAsync(strangerAuth, strangerScope);

        var probe = await ApplyAsync(
            strangerAuth,
            Machine(strangerScope, strangerRecord.ExperienceId, ConfidenceEvidenceKind.Supporting) with
            {
                EvidenceId = applied.Update!.EvidenceId,
            });

        Assert.Equal(ConfidenceUpdateOutcome.Conflict, probe.Outcome);
        Assert.Null(probe.Update);
        Assert.Equal(0, probe.Revision);
        Assert.Null(probe.Status);

        // Neither record moved, and the owner's ledger is untouched.
        await AssertConfidenceAsync(ownerAuth, ownerScope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
        await AssertConfidenceAsync(strangerAuth, strangerScope, strangerRecord.ExperienceId, 2d / 3d, 1, 0, ExperienceStatus.Validated);
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: null));
        Assert.Equal(0, await CountEvidenceAsync(strangerRecord.ExperienceId, counted: null));
    }

    [Fact]
    public async Task A_retry_under_a_fresh_event_id_is_a_conflict_rather_than_a_phantom_event()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var request = Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting);
        Assert.True((await ApplyAsync(auth, request)).Counted);

        // Reporting this as committed would hand back a lifecycle event that was never written.
        var reissued = await ApplyAsync(auth, request with { EventId = Guid.NewGuid() });

        Assert.Equal(ConfidenceUpdateOutcome.Conflict, reissued.Outcome);
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: null));
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task The_same_reviewer_and_run_under_a_new_evidence_id_is_stored_and_counted_zero_times()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var runId = Guid.NewGuid();

        var first = await ApplyAsync(auth, Human(scope, record.ExperienceId, runId));
        Assert.True(first.Counted);
        Assert.Equal(auth.PrincipalId, first.Update!.ReviewerIdentity);

        var afterFirst = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;

        var again = await ApplyAsync(auth, Human(scope, record.ExperienceId, runId));

        Assert.Equal(ConfidenceUpdateOutcome.Applied, again.Outcome);
        Assert.False(again.Counted);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
        Assert.Equal(2, await CountEvidenceAsync(record.ExperienceId, counted: null));

        var after = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(afterFirst.Revision, after.Revision);
        Assert.Equal(afterFirst.UpdatedAt, after.UpdatedAt);

        // A different reviewer, same run, is a genuinely independent opinion and does count.
        var otherReviewer = auth with { PrincipalId = "reviewer-2" };
        var second = await ApplyAsync(otherReviewer, Human(scope, record.ExperienceId, runId));

        Assert.True(second.Counted);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 4d / 5d, 3, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task The_databases_independence_key_is_the_one_Core_computes()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var machineRun = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var machine = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting, machineRun, roundId));

        var humanRun = Guid.NewGuid();
        var human = await ApplyAsync(auth, Human(scope, record.ExperienceId, humanRun));

        // The rule is stated twice -- in AgentExperience.Core.Confidence and in migration 0007 -- because
        // only the database can enforce it and only Core can reason about it. If the two ever disagreed,
        // an observation one side deduplicates would be counted by the other.
        Assert.Equal(
            ConfidenceIndependenceKey.ForMachine(machineRun, roundId).Value,
            await ReadIndependenceKeyAsync(machine.Update!.EvidenceId));
        Assert.Equal(
            ConfidenceIndependenceKey.ForHuman(auth.PrincipalId, humanRun).Value,
            await ReadIndependenceKeyAsync(human.Update!.EvidenceId));
    }

    [Fact]
    public async Task The_same_evidence_id_with_different_content_is_rejected_and_writes_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var evidenceId = Guid.NewGuid();
        var first = Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting) with { EvidenceId = evidenceId };
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await ApplyAsync(auth, first)).Outcome);

        var eventsBefore = await CountEventsAsync(record.ExperienceId);

        // Same evidence ID, different claim: a different run, and pointing the other way.
        var altered = first with
        {
            EventId = Guid.NewGuid(),
            Kind = ConfidenceEvidenceKind.Contradicting,
            RunId = Guid.NewGuid(),
            VerificationRoundId = Guid.NewGuid(),
        };

        var conflict = await ApplyAsync(auth, altered);

        Assert.Equal(ConfidenceUpdateOutcome.Conflict, conflict.Outcome);
        Assert.Null(conflict.Update);
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: null));
        Assert.Equal(eventsBefore, await CountEventsAsync(record.ExperienceId));
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task Resubmitting_identical_evidence_reports_the_original_outcome_and_writes_nothing_twice()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var request = Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Contradicting);
        var first = await ApplyAsync(auth, request);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, first.Outcome);
        Assert.Equal(ExperienceStatus.Contested, first.Status);

        var eventsAfterFirst = await CountEventsAsync(record.ExperienceId);

        // The retry a lost acknowledgement calls for: byte-for-byte the same submission.
        var replay = await ApplyAsync(auth, request);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, replay.Outcome);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.True(replay.Counted);
        Assert.Equal(first.Update!.NewReuseConfidence, replay.Update!.NewReuseConfidence);
        Assert.Equal(first.Update.NewContradictions, replay.Update.NewContradictions);

        // Nothing landed a second time, and the counters did not move again.
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: null));
        Assert.Equal(eventsAfterFirst, await CountEventsAsync(record.ExperienceId));
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 4d, 1, 1, ExperienceStatus.Contested);
    }

    [Fact]
    public async Task Two_submissions_racing_from_one_revision_produce_exactly_one_update_and_one_revision_conflict()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        // Both read the record at revision 1 and both compute against it. Only the revision guard, inside
        // the commit, can stop them both applying -- and the loser must be told, not silently dropped.
        // The barrier makes "both read before either commits" a fact rather than a timing hope: without
        // it, a loaded runner can finish the first submission before the second reads, and the second then
        // legitimately applies at revision 2 (story 6.3 CI, with two framework test hosts in parallel).
        var racing = new ExperienceLifecycleService(new ReadBarrierStore(_store, parties: 2));
        var results = await Task.WhenAll(
            racing.ApplyEvidenceAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting), CancellationToken.None),
            racing.ApplyEvidenceAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting), CancellationToken.None));

        Assert.Equal(1, results.Count(r => r.Outcome == ConfidenceUpdateOutcome.Applied));
        Assert.Equal(1, results.Count(r => r.Outcome == ConfidenceUpdateOutcome.StaleRevision));

        // Exactly one counter moved, and the loser wrote no evidence row at all.
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: null));

        // Resubmitting the loser's identical request now recomputes against the revision it reports.
        var loser = results.Single(r => r.Outcome == ConfidenceUpdateOutcome.StaleRevision);
        Assert.Equal(2, loser.Revision);
    }

    [Theory]
    [InlineData(ExperienceStatus.Revoked)]
    [InlineData(ExperienceStatus.Quarantined)]
    public async Task Evidence_against_a_record_that_does_not_accept_it_is_refused_and_moves_nothing(ExperienceStatus status)
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);

        Guid experienceId;
        if (status == ExperienceStatus.Quarantined)
        {
            var quarantined = Minimal(scope);
            Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, quarantined, CancellationToken.None)).Outcome);
            await CommitAsync(auth, scope, quarantined.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Quarantined, 0);
            experienceId = quarantined.ExperienceId;
        }
        else
        {
            var validated = await ValidatedAsync(auth, scope);
            await CommitAsync(auth, scope, validated.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 1);
            experienceId = validated.ExperienceId;
        }

        var before = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;

        var result = await ApplyAsync(auth, Machine(scope, experienceId, ConfidenceEvidenceKind.Supporting));

        Assert.Equal(ConfidenceUpdateOutcome.Ineligible, result.Outcome);
        Assert.Equal(status, result.Status);
        Assert.Equal(0, await CountEvidenceAsync(experienceId, counted: null));

        var after = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;
        Assert.Equal(before.ReuseConfidence, after.ReuseConfidence);
        Assert.Equal(before.SupportingValidations, after.SupportingValidations);
        Assert.Equal(before.Contradictions, after.Contradictions);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task A_contradiction_against_a_contested_record_keeps_its_status_and_still_moves_the_counters()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Contradicting));
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 4d, 1, 1, ExperienceStatus.Contested);

        var second = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Contradicting));

        Assert.Equal(ConfidenceUpdateOutcome.Applied, second.Outcome);
        Assert.Equal(ExperienceStatus.Contested, second.Status);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 5d, 1, 2, ExperienceStatus.Contested);

        // Supporting evidence against the same contested record still counts and still changes nothing
        // about its status: only a lifecycle transition could, and the table has no way back.
        var supporting = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting));

        Assert.Equal(ExperienceStatus.Contested, supporting.Status);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 6d, 2, 2, ExperienceStatus.Contested);
    }

    [Fact]
    public async Task The_score_stays_inside_zero_and_one_and_the_counters_never_go_negative_across_a_long_sequence()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        // Contradict, contradict, support, contradict, support: an arbitrary sequence of accepted,
        // independent evidence, each read back from the database rather than assumed.
        foreach (var kind in new[]
        {
            ConfidenceEvidenceKind.Contradicting,
            ConfidenceEvidenceKind.Contradicting,
            ConfidenceEvidenceKind.Supporting,
            ConfidenceEvidenceKind.Contradicting,
            ConfidenceEvidenceKind.Supporting,
        })
        {
            var applied = await ApplyAsync(auth, Machine(scope, record.ExperienceId, kind));
            Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);

            var stored = (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record!;
            Assert.True(stored.ReuseConfidence > 0 && stored.ReuseConfidence < 1, $"score {stored.ReuseConfidence} left (0, 1)");
            Assert.True(stored.SupportingValidations >= 0);
            Assert.True(stored.Contradictions >= 0);
            Assert.Equal(ReuseConfidenceHeuristic.Score(stored.SupportingValidations, stored.Contradictions), stored.ReuseConfidence);
        }

        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 4d / 8d, 3, 3, ExperienceStatus.Contested);
    }

    [Fact]
    public async Task The_confidence_and_counter_columns_cannot_be_rewritten_outside_this_path()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        // Each of these would move trust without any evidence saying it should, while the immutable
        // event log kept describing the counters the record no longer has.
        foreach (var sql in new[]
        {
            "UPDATE agent_experience.experience_records SET reuse_confidence = 0.99 WHERE experience_id = @id",
            "UPDATE agent_experience.experience_records SET supporting_validations = supporting_validations + 5 WHERE experience_id = @id",
            "UPDATE agent_experience.experience_records SET contradictions = 0, reuse_confidence = 1 WHERE experience_id = @id",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, record.ExperienceId));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // Advancing the revision is not a way round it either. The numbers have to be ones a lifecycle
        // event already recorded for exactly that revision, so a rewrite that dresses itself up as a
        // commit still has no event to point at.
        var forged = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_records " +
            "SET reuse_confidence = 1, supporting_validations = 99, revision = revision + 1 WHERE experience_id = @id",
            record.ExperienceId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forged.SqlState);

        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 3d, 1, 0, ExperienceStatus.Validated);

        // The store's own path is unaffected: it moves the counters together with the revision, to the
        // values the event it just appended recorded.
        Assert.True((await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting))).Counted);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task The_evidence_ledger_is_append_only_in_the_database()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var applied = await ApplyAsync(auth, Machine(scope, record.ExperienceId, ConfidenceEvidenceKind.Supporting));

        // Editing counted, or deleting the row, would free the independence key so the same observation
        // could be counted a second time.
        foreach (var sql in new[]
        {
            "UPDATE agent_experience.confidence_evidence SET counted = false WHERE evidence_id = @id",
            "DELETE FROM agent_experience.confidence_evidence WHERE evidence_id = @id",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, applied.Update!.EvidenceId));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        var truncate = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync("TRUNCATE agent_experience.confidence_evidence", null));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);

        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId, counted: true));
    }

    [Fact]
    public async Task A_hand_written_evidence_row_cannot_dodge_the_independence_key()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        // Machine evidence with no round, and human evidence with no reviewer, would both generate a null
        // key -- which a unique index cannot deduplicate, so every such submission would count. The table
        // refuses them whatever the writer.
        foreach (var (source, round, reviewer) in new (string Source, Guid? Round, string? Reviewer)[]
        {
            ("Machine", null, null),
            ("Machine", Guid.NewGuid(), "someone"),
            ("Human", null, null),
            ("Human", Guid.NewGuid(), "someone"),
        })
        {
            await using var command = _fixture.DataSource.CreateCommand(
                "INSERT INTO agent_experience.confidence_evidence (evidence_id, experience_id, event_id, kind, source, " +
                "run_id, verification_round_id, reviewer_identity, counted, rule_version, recorded_at, applied_revision, " +
                "applied_status, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, " +
                "new_supporting_validations, prior_contradictions, new_contradictions) " +
                "VALUES (gen_random_uuid(), @experience_id, gen_random_uuid(), 'Supporting', @source, gen_random_uuid(), " +
                "@round, @reviewer, true, '1.0.0', now(), 2, 'Validated', 2.0/3.0, 3.0/4.0, 1, 2, 0, 0)");
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            command.Parameters.Add(new NpgsqlParameter<string>("source", source));
            command.Parameters.Add(new NpgsqlParameter("round", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = round is { } id ? id : DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("reviewer", NpgsqlTypes.NpgsqlDbType.Text) { Value = reviewer ?? (object)DBNull.Value });

            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }

        Assert.Equal(0, await CountEvidenceAsync(record.ExperienceId, counted: null));
    }

    /// <summary>
    /// Creates a record and commits its initial event exactly as finalization does, leaving it
    /// <see cref="ExperienceStatus.Validated"/> at revision 1 with one supporting validation, no
    /// contradictions, and reuse confidence 2/3.
    /// </summary>
    private async Task<ExperienceRecord> ValidatedAsync(AuthorizationContext auth, Scope scope)
    {
        var record = Minimal(scope) with
        {
            ReuseConfidence = 2d / 3d,
            SupportingValidations = 1,
            Contradictions = 0,
        };

        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        return record;
    }

    private Task<ApplyConfidenceEvidenceResult> ApplyAsync(AuthorizationContext auth, ApplyConfidenceEvidenceRequest request) =>
        _lifecycle.ApplyEvidenceAsync(auth, request, CancellationToken.None);

    private static ApplyConfidenceEvidenceRequest Machine(
        Scope scope,
        Guid experienceId,
        ConfidenceEvidenceKind kind,
        Guid? runId = null,
        Guid? roundId = null) => new(
            EventId: Guid.NewGuid(),
            ExperienceId: experienceId,
            Scope: scope,
            EvidenceId: Guid.NewGuid(),
            Kind: kind,
            Source: ConfidenceEvidenceSource.Machine,
            RunId: runId ?? Guid.NewGuid(),
            VerificationRoundId: roundId ?? Guid.NewGuid(),
            Reason: $"reuse was observed to be {kind}",
            Producer: "tests",
            OccurredAt: PayloadTime);

    private static ApplyConfidenceEvidenceRequest Human(Scope scope, Guid experienceId, Guid runId) =>
        Machine(scope, experienceId, ConfidenceEvidenceKind.Supporting, runId) with
        {
            Source = ConfidenceEvidenceSource.Human,
            VerificationRoundId = null,
        };

    private async Task AssertConfidenceAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        double confidence,
        int supporting,
        int contradictions,
        ExperienceStatus status)
    {
        var stored = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;

        Assert.Equal(confidence, stored.ReuseConfidence);
        Assert.Equal(supporting, stored.SupportingValidations);
        Assert.Equal(contradictions, stored.Contradictions);
        Assert.Equal(status, stored.Status);
    }

    private async Task CommitAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        ExperienceStatus? prior,
        ExperienceStatus current,
        long expectedRevision)
    {
        var result = await _lifecycle.CommitAsync(
            auth,
            new CommitLifecycleTransitionRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: experienceId,
                Scope: scope,
                PriorStatus: prior,
                CurrentStatus: current,
                Reason: $"moved to {current}",
                Producer: "tests",
                OccurredAt: PayloadTime,
                ExpectedRevision: expectedRevision),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
    }

    private async Task<(bool Counted, Guid? EventId, long AppliedRevision, string AppliedStatus, int PriorSupporting, int NewSupporting)> ReadLedgerAsync(Guid evidenceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT counted, event_id, applied_revision, applied_status, prior_supporting_validations, " +
            "new_supporting_validations FROM agent_experience.confidence_evidence WHERE evidence_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", evidenceId));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
    }

    private async Task<string?> ReadIndependenceKeyAsync(Guid evidenceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT independence_key FROM agent_experience.confidence_evidence WHERE evidence_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", evidenceId));
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<long> CountEvidenceAsync(Guid experienceId, bool? counted)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.confidence_evidence " +
            "WHERE experience_id = @id AND (@counted::boolean IS NULL OR counted = @counted)");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        command.Parameters.Add(new NpgsqlParameter("counted", NpgsqlTypes.NpgsqlDbType.Boolean)
        {
            Value = counted is { } value ? value : DBNull.Value,
        });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountEventsAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A hand-written statement, as the tables' owner: the guard under test must refuse a writer that holds the privilege.</summary>
    private async Task<int> ExecuteAsync(string sql, Guid? id)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        if (id is { } value)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", value));
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Forwards everything to the real store, but holds each single-record read until
    /// <paramref name="parties"/> reads have completed, so concurrent callers all see the same revision
    /// before any of them can commit.
    /// </summary>
    private sealed class ReadBarrierStore(IExperienceRecordStore inner, int parties) : IExperienceRecordStore
    {
        private readonly TaskCompletionSource _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(authorization, record, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            HoldAsync(inner.GetAsync(authorization, scope, experienceId, cancellationToken));

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            HoldAsync(inner.GetAsync(authorization, scope, experienceId, options, cancellationToken));

        public Task<ExperienceRecordGetManyResult> GetManyAsync(AuthorizationContext authorization, Scope scope, IReadOnlyList<Guid> experienceIds, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            inner.GetManyAsync(authorization, scope, experienceIds, options, cancellationToken);

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryAsync(authorization, query, cancellationToken);

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            inner.GetHistoryAsync(authorization, query, cancellationToken);

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken);

        private async Task<ExperienceRecordGetResult> HoldAsync(Task<ExperienceRecordGetResult> read)
        {
            var result = await read;
            if (Interlocked.Increment(ref _reads) >= parties)
            {
                _allRead.TrySetResult();
            }

            await _allRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return result;
        }
    }
}
