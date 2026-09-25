using AgentExperience.Core.Confidence;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Lifecycle;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 4.5 against a real PostgreSQL container: the one destructive operation this library has.
/// Every claim here is proved against rows in a database rather than reasoned about -- what erasure
/// removes, what it deliberately keeps, that a foreign scope cannot tell a refusal from an absence, that
/// a tombstone is terminal for every write path, that the append-only guards stay armed in another
/// session while a purge transaction is open, and that a retention sweep erases exactly what a frozen
/// clock says is past the cutoff. Each test uses its own random tenant, so tests sharing the container
/// never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresDeletionTests
{
    private const string Administrator = "sharing-administrator";

    /// <summary>The literal <c>0010</c> writes into a tombstone's status column. No ExperienceStatus names it.</summary>
    private const string TombstoneStatus = "Deleted";

    /// <summary>The literal <c>0010</c> writes into a tombstone's task_id, which cannot be blank.</summary>
    private const string TombstoneTaskId = "(deleted)";

    /// <summary>
    /// How long a concurrency test lets a second writer reach the row lock before the purge holding it
    /// commits. It is not a correctness bound -- a writer that arrives late simply reads the committed
    /// tombstone and loses for the other reason -- only a way of making the interesting interleaving the
    /// usual one rather than the rare one.
    /// </summary>
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromMilliseconds(300);

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly PostgresExperienceGrantStore _grants;
    private readonly PostgresExperienceReuseFeedbackStore _ledger;
    private readonly PostgresExperienceGrantAccessLog _access;
    private readonly ExperienceLifecycleService _lifecycle;
    private readonly ExperienceReuseFeedbackService _feedback;

    public PostgresDeletionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _access = new PostgresExperienceGrantAccessLog(fixture.DataSource);
        _store = new PostgresExperienceRecordStore(
            fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(_access, _ => { }));
        _grants = new PostgresExperienceGrantStore(fixture.DataSource);
        _ledger = new PostgresExperienceReuseFeedbackStore(fixture.DataSource);
        _lifecycle = new ExperienceLifecycleService(_store);
        _feedback = new ExperienceReuseFeedbackService(_ledger, _lifecycle);
    }

    // ------------------------------------------------------------------ the erasure itself

    [Fact]
    public async Task Deleting_a_record_erases_every_row_that_named_it_and_leaves_exactly_the_tombstone()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = await PopulatedAsync(auth, owner, recipient);

        // Everything the record accumulated is really there before the delete, or the assertions below
        // would pass over an empty database.
        Assert.True(await CountAsync("lifecycle_events", record.ExperienceId) > 0);
        Assert.True(await CountAsync("confidence_evidence", record.ExperienceId) > 0);
        Assert.True(await CountAsync("reuse_feedback_exposures", record.ExperienceId) > 0);
        Assert.True(await CountAsync("experience_grants", record.ExperienceId) > 0);
        Assert.True(await CountGrantEventsAsync(record.ExperienceId) > 0);
        Assert.Equal(1, await CountAsync("experience_grant_access", record.ExperienceId));
        var feedbackId = Assert.Single(await FeedbackIdsAsync(record.ExperienceId));

        var deleted = await _store.DeleteAsync(auth, owner, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Empty(deleted.Errors);

        // One erasure, one frozen order, and nothing that named the record survives it.
        Assert.Equal(0, await CountAsync("confidence_evidence", record.ExperienceId));
        Assert.Equal(0, await CountAsync("reuse_feedback_exposures", record.ExperienceId));
        Assert.Equal(0, await CountFeedbackAsync(feedbackId));
        Assert.Equal(0, await CountGrantEventsAsync(record.ExperienceId));
        Assert.Equal(0, await CountAsync("experience_grants", record.ExperienceId));
        Assert.Equal(0, await CountAsync("lifecycle_events", record.ExperienceId));

        // ...except the access trail, which is retained on purpose: it names a grant and a principal,
        // carries no payload, and is the answer to "who read this before it was deleted". It has to stay
        // *readable* to be that answer, so it is read back through the port rather than counted in SQL:
        // the ledger joins no record, so erasing one cannot make its trail unreadable.
        Assert.Equal(1, await CountAsync("experience_grant_access", record.ExperienceId));

        var trail = await _access.QueryAsync(
            auth, new ExperienceGrantAccessQuery(owner, record.ExperienceId), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, trail.Outcome);
        Assert.Equal(record.ExperienceId, Assert.Single(trail.Accesses).ExperienceId);

        // The tombstone carries the retained list and nothing else. Every other column is a fixed,
        // content-free value -- including the timestamps, so it cannot say when the work happened.
        var tombstone = await ReadTombstoneAsync(record.ExperienceId);

        Assert.Equal(owner.TenantId, tombstone.TenantId);
        Assert.Equal(owner.ApplicationId, tombstone.ApplicationId);
        Assert.Equal(owner.ProjectId, tombstone.ProjectId);
        Assert.Equal(owner.TeamId, tombstone.TeamId);
        Assert.Null(tombstone.AgentId);
        Assert.Null(tombstone.UserId);
        Assert.Equal(deleted.Revision, tombstone.Revision);
        Assert.Equal(TombstoneStatus, tombstone.Status);
        Assert.Equal(TombstoneTaskId, tombstone.TaskId);
        Assert.NotNull(tombstone.DeletedAt);

        Assert.Equal(Guid.Empty, tombstone.SourceRunId);
        Assert.Equal("{}", tombstone.Payload);
        Assert.Equal(0d, tombstone.ReuseConfidence);
        Assert.Equal(0, tombstone.SupportingValidations);
        Assert.Equal(0, tombstone.Contradictions);
        Assert.Equal(tombstone.DeletedAt, tombstone.CreatedAt);
        Assert.Equal(tombstone.DeletedAt, tombstone.UpdatedAt);

        // The generated search vector regenerates from the placeholder alone, so the record's text is
        // gone from the index as well as from the column -- no separate index maintenance, by design.
        Assert.Equal("'delet':1", tombstone.SearchVector);

        // And the revision moved exactly once, the way every other change to a record does.
        Assert.Equal(record.Revision + 1, tombstone.Revision);
    }

    [Fact]
    public async Task A_submission_that_named_other_records_survives_with_only_this_exposure_gone()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var erased = await ValidatedAsync(auth, scope);
        var kept = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [erased.ExperienceId, kept.ExperienceId]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, erased.ExperienceId, CancellationToken.None)).Outcome);

        // The submission still describes the other record it named, so its row stays; only the exposure
        // of the erased record is removed. A submission left with no exposures at all is deleted -- that
        // is the case the first test covers.
        Assert.Equal(1, await CountFeedbackAsync(feedback.FeedbackId));
        Assert.Equal(0, await CountAsync("reuse_feedback_exposures", erased.ExperienceId));
        Assert.Equal(1, await CountAsync("reuse_feedback_exposures", kept.ExperienceId));
    }

    [Fact]
    public async Task The_projection_guard_accepts_the_tombstone_from_every_prior_status()
    {
        // The open risk the spec flagged: the tombstone's final UPDATE has to satisfy the very guard
        // that protects the projection, from wherever the record happened to be. It advances the
        // revision like any other transition, so every prior status is accepted -- proved, not assumed.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);

        foreach (var status in Enum.GetValues<ExperienceStatus>())
        {
            var record = Minimal(scope);
            Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);

            // Straight to the status under test, through the store's own guarded commit. A superseding
            // event has to name a replacement, so that one gets an eligible record to point at.
            Guid? replacement = null;
            if (status == ExperienceStatus.Superseded)
            {
                replacement = (await ValidatedAsync(auth, scope)).ExperienceId;
            }

            var commit = await _store.CommitLifecycleEventAsync(
                auth,
                scope,
                Event(record.ExperienceId, ExperienceStatus.Candidate, status, 0, replacement: replacement),
                CancellationToken.None);

            Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);

            var deleted = await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None);

            Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
            Assert.Equal(TombstoneStatus, (await ReadTombstoneAsync(record.ExperienceId)).Status);
        }
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task A_delete_naming_another_scope_is_the_same_answer_as_one_naming_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var mine = Scope(tenant, team: "team-a");
        var theirs = Scope(tenant, team: "team-b");
        var record = await ValidatedAsync(auth, theirs);

        var foreign = await _store.DeleteAsync(auth, mine, record.ExperienceId, CancellationToken.None);
        var absent = await _store.DeleteAsync(auth, mine, Guid.NewGuid(), CancellationToken.None);

        // Identical outcome, identical revision, identical (empty) errors: nothing about the record's
        // existence reaches a scope that does not own it.
        Assert.Equal(ExperienceStoreOutcome.NotFound, foreign.Outcome);
        Assert.Equal(absent.Outcome, foreign.Outcome);
        Assert.Equal(absent.Revision, foreign.Revision);
        Assert.Equal(absent.Errors, foreign.Errors);

        // And the record it named is untouched.
        var stored = await _store.GetAsync(auth, theirs, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, stored.Outcome);
        Assert.Null((await ReadTombstoneAsync(record.ExperienceId)).DeletedAt);

        // A scope outside the host-established authorization never reaches storage at all.
        var denied = await _store.DeleteAsync(Authorize(NewTenant()), theirs, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, theirs, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Deleting_twice_is_a_success_that_touches_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var first = await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        var tombstone = await ReadTombstoneAsync(record.ExperienceId);

        var again = await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, again.Outcome);
        Assert.Equal(first.Revision, again.Revision);

        // Not one column moved -- not the revision, not deleted_at, not updated_at. A second delete that
        // re-stamped the tombstone would make "when was this erased" a value any caller could refresh.
        var after = await ReadTombstoneAsync(record.ExperienceId);
        Assert.Equal(tombstone, after);
    }

    [Fact]
    public async Task A_stale_expected_revision_refuses_the_delete_and_erases_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var stale = await _store.DeleteAsync(auth, scope, record.ExperienceId, expectedRevision: 0, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.StaleRevision, stale.Outcome);
        Assert.Equal(1, stale.Revision);
        Assert.Equal(1, await CountAsync("lifecycle_events", record.ExperienceId));
        Assert.Null((await ReadTombstoneAsync(record.ExperienceId)).DeletedAt);

        // ...and the same call against the revision the record is really at erases it.
        var deleted = await _store.DeleteAsync(auth, scope, record.ExperienceId, expectedRevision: 1, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Equal(2, deleted.Revision);

        // A stale revision in another scope is still NotFound: the guard never reveals the record.
        var other = await ValidatedAsync(auth, Scope(tenant, team: "team-c"));
        var foreign = await _store.DeleteAsync(auth, scope, other.ExperienceId, expectedRevision: 0, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, foreign.Outcome);
    }

    [Fact]
    public async Task A_malformed_delete_or_sweep_is_refused_before_any_connection_opens()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);

        var offline = new PostgresExperienceRecordStore(Unreachable());

        var empty = await offline.DeleteAsync(auth, scope, Guid.Empty, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, empty.Outcome);
        Assert.Equal("ExperienceId", Assert.Single(empty.Errors).Path);

        var negative = await offline.DeleteAsync(auth, scope, Guid.NewGuid(), expectedRevision: -1, CancellationToken.None);
        Assert.Equal("ExpectedRevision", Assert.Single(negative.Errors).Path);

        // There is no retention age that means "delete everything", and no unbounded batch.
        var forever = await offline.SweepExpiredAsync(auth, scope, TimeSpan.Zero, 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, forever.Outcome);
        Assert.Equal("RetentionAge", Assert.Single(forever.Errors).Path);

        var unbounded = await offline.SweepExpiredAsync(
            auth, scope, TimeSpan.FromDays(1), PostgresExperienceRecordStore.MaxSweepBatchSize + 1, CancellationToken.None);
        Assert.Equal("BatchSize", Assert.Single(unbounded.Errors).Path);

        var denied = await offline.SweepExpiredAsync(Authorize(NewTenant()), scope, TimeSpan.FromDays(1), 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
    }

    // ------------------------------------------------------------------ a tombstone is terminal

    [Fact]
    public async Task Every_write_path_refuses_a_tombstone_rather_than_resurrecting_it()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = await ValidatedAsync(auth, owner);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, owner, record.ExperienceId, CancellationToken.None)).Outcome);

        // A create under the erased ID collides with the tombstone, exactly as it would with any stored
        // record, and says nothing about which scope holds it.
        var recreated = await _store.CreateAsync(auth, Minimal(owner, record.ExperienceId), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Conflict, recreated.Outcome);

        // A late lifecycle commit is Deleted, not StaleRevision and not NotFound: within its own scope a
        // host can tell "erased" from "never existed", and neither answer moves the tombstone.
        var commit = await _store.CommitLifecycleEventAsync(
            auth, owner, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 1), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, commit.Outcome);
        Assert.Equal(0, await CountAsync("lifecycle_events", record.ExperienceId));

        // A late confidence submission is refused for the same reason and writes no ledger row either.
        var withEvidence = await _store.CommitLifecycleEventAsync(
            auth,
            owner,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 1) with
            {
                Confidence = new ConfidenceUpdate(
                    EvidenceId: Guid.NewGuid(),
                    Kind: ConfidenceEvidenceKind.Supporting,
                    Source: ConfidenceEvidenceSource.Machine,
                    RunId: Guid.NewGuid(),
                    VerificationRoundId: Guid.NewGuid(),
                    ReviewerIdentity: null,
                    RuleVersion: ReuseConfidenceHeuristic.RuleVersion,
                    PriorReuseConfidence: 0.5,
                    NewReuseConfidence: 0.6,
                    PriorSupportingValidations: 1,
                    NewSupportingValidations: 2,
                    PriorContradictions: 0,
                    NewContradictions: 0),
            },
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, withEvidence.Outcome);
        Assert.Equal(0, await CountAsync("confidence_evidence", record.ExperienceId));

        // Feedback naming the tombstone is refused, by position, with nothing written.
        var feedback = Feedback(owner, [record.ExperienceId]);
        var recorded = await _ledger.RecordAsync(auth, Submission(feedback), CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, recorded.Outcome);
        Assert.Equal("Exposures[0].ExperienceId", Assert.Single(recorded.Errors).Path);
        Assert.Equal(0, await CountFeedbackAsync(feedback.FeedbackId));
        Assert.Equal(0, await CountAsync("reuse_feedback_exposures", record.ExperienceId));

        // A grant over the tombstone is NotFound: there is nothing left to share.
        var granted = await _grants.CreateAsync(
            auth,
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), record.ExperienceId, owner, recipient, "after the erasure", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.NotFound, granted.Outcome);
        Assert.Equal(0, await CountAsync("experience_grants", record.ExperienceId));
    }

    [Fact]
    public async Task Core_reports_a_late_write_against_a_tombstone_as_a_terminal_refusal_rather_than_throwing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // The store's Deleted answer reaches Core's lifecycle service through the real adapter. Before
        // Core mapped it, both calls threw ExperienceStoreException -- an "infrastructure failure" for
        // what is an ordinary late write against an erased record.
        var transitioned = await _lifecycle.CommitAsync(
            auth,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(), record.ExperienceId, scope, record.Status, ExperienceStatus.Revoked,
                "revoking a record that was erased meanwhile", "tests", DateTimeOffset.UtcNow, record.Revision),
            CancellationToken.None);
        Assert.Equal(LifecycleTransitionOutcome.Deleted, transitioned.Outcome);

        var applied = await _lifecycle.ApplyEvidenceAsync(
            auth,
            new ApplyConfidenceEvidenceRequest(
                Guid.NewGuid(), record.ExperienceId, scope, Guid.NewGuid(),
                ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Machine,
                Guid.NewGuid(), Guid.NewGuid(), "evidence about a record that was erased meanwhile", "tests",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Deleted, applied.Outcome);

        // Neither answer moved the tombstone or wrote anything against it.
        Assert.Equal(0, await CountAsync("lifecycle_events", record.ExperienceId));
        Assert.Equal(0, await CountAsync("confidence_evidence", record.ExperienceId));

        // Another scope is still told nothing: the same calls there are NotFound.
        var foreign = Scope(tenant, team: "team-z");
        var foreignCommit = await _lifecycle.CommitAsync(
            auth,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(), record.ExperienceId, foreign, record.Status, ExperienceStatus.Revoked,
                "from another scope", "tests", DateTimeOffset.UtcNow, record.Revision),
            CancellationToken.None);
        Assert.Equal(LifecycleTransitionOutcome.NotFound, foreignCommit.Outcome);
    }

    [Fact]
    public async Task Every_read_path_reports_the_tombstone_as_erased_or_not_at_all()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var survivor = await ValidatedAsync(auth, scope);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // Named reads inside the owning scope say "erased" and hand back nothing.
        var get = await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, get.Outcome);
        Assert.Null(get.Record);

        var history = await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, history.Outcome);
        Assert.Empty(history.Events);

        // Enumerations simply do not contain it. A tombstone has no payload to return.
        var listed = await _store.QueryAsync(auth, new ExperienceRecordQuery(scope), CancellationToken.None);
        Assert.Equal([survivor.ExperienceId], listed.Records.Select(r => r.ExperienceId));

        var search = await new PostgresExperienceCandidateSource(_fixture.DataSource).SearchAsync(
            auth,
            new ExperienceCandidateQuery(scope, "task-1 deleted", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0, 50),
            CancellationToken.None);
        Assert.DoesNotContain(record.ExperienceId, search.Candidates.Select(c => c.Record.ExperienceId));

        // And it can neither be superseded nor named as a replacement.
        var asRecord = await _store.CheckSupersessionAsync(auth, scope, record.ExperienceId, survivor.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceSupersessionOutcome.RecordNotFound, asRecord.Outcome);

        var asReplacement = await _store.CheckSupersessionAsync(auth, scope, survivor.ExperienceId, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceSupersessionOutcome.ReplacementNotFound, asReplacement.Outcome);

        // A foreign scope is told nothing at all -- not even that the ID was once used here.
        var foreign = await _store.GetAsync(auth, Scope(tenant, team: "team-z"), record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, foreign.Outcome);
    }

    [Fact]
    public async Task The_database_refuses_a_tombstone_written_or_moved_outside_the_purge_path()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var live = await ValidatedAsync(auth, scope);
        var erased = await ValidatedAsync(auth, scope);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, erased.ExperienceId, CancellationToken.None)).Outcome);

        // Erasure has one code path. A direct statement cannot mark a record erased...
        var marked = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_records SET deleted_at = now(), revision = revision + 1 " +
            "WHERE experience_id = @id",
            live.ExperienceId));

        // ...and cannot change a tombstone once it exists, in any way, marker or no marker.
        var moved = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_records SET updated_at = now() WHERE experience_id = @id",
            erased.ExperienceId));

        var revived = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.experience_records SET deleted_at = NULL, revision = revision + 1 " +
            "WHERE experience_id = @id",
            erased.ExperienceId));

        Assert.All([marked, moved, revived], ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));

        // The tombstone-shape CHECK is the other half: "erased" is one shape, never a flag set over a
        // payload that is still there.
        var halfErased = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, " +
            "project_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, " +
            "created_at, updated_at, payload_version, payload, deleted_at) VALUES (@id, @id, @tenant, 'app-1', " +
            "'project-1', 'task-1', 'Validated', 0, 0, 0, 0, now(), now(), 1, '{\"taskSummary\":\"still here\"}'::jsonb, now())",
            Guid.NewGuid(),
            tenant));

        Assert.Equal(PostgresErrorCodes.CheckViolation, halfErased.SqlState);
        Assert.Equal("experience_records_tombstone_shape", halfErased.ConstraintName);
    }

    [Fact]
    public async Task The_append_only_guards_stay_armed_in_another_session_while_a_purge_is_open()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var erasing = await ValidatedAsync(auth, scope);
        var bystander = await ValidatedAsync(auth, scope);
        await ApplyEvidenceAsync(auth, scope, bystander.ExperienceId);

        // One connection holds an open purge transaction -- the marker is set, the rows are gone, and
        // nothing is committed yet.
        await using var purging = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await purging.BeginTransactionAsync();

        await using (var purge = new NpgsqlCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(" +
            "@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            purging,
            transaction))
        {
            purge.Parameters.Add(new NpgsqlParameter<Guid>("id", erasing.ExperienceId));
            purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
            Assert.Equal("Deleted", await purge.ExecuteScalarAsync());
        }

        // Meanwhile, on a different connection, the guards are exactly as armed as they always are. This
        // is the whole point of a transaction-scoped marker over DISABLE TRIGGER: the window a purge
        // opens is invisible to every other session.
        var deleteLog = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.lifecycle_events WHERE experience_id = @id", bystander.ExperienceId));
        var rewriteLog = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE agent_experience.lifecycle_events SET producer = 'nobody' WHERE experience_id = @id", bystander.ExperienceId));
        var evidence = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.confidence_evidence WHERE experience_id = @id", bystander.ExperienceId));

        Assert.All(
            [deleteLog, rewriteLog, evidence],
            ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));

        // Rolling the purge back leaves the record it was erasing whole: the erasure is one transaction,
        // so a failure part-way through erases nothing at all.
        await transaction.RollbackAsync();

        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, scope, erasing.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(1, await CountAsync("lifecycle_events", erasing.ExperienceId));

        // TRUNCATE is asserted after the rollback rather than during the purge, and for a reason worth
        // stating: it takes an ACCESS EXCLUSIVE lock, so it would queue behind any open writer whatever
        // the guards said, and a test that "passed" by timing out would prove nothing.
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "TRUNCATE agent_experience.lifecycle_events", id: null));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);
    }

    [Fact]
    public async Task The_marker_does_not_outlive_the_purge_function_inside_its_own_transaction()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var erasing = await ValidatedAsync(auth, scope);
        var bystander = await ValidatedAsync(auth, scope);

        // As the owner, which holds DELETE: for the application role the privilege system would refuse the
        // statement below before the marker was ever consulted, and this test is about the marker.
        await using var connection = await _fixture.OwnerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var purge = new NpgsqlCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(" +
            "@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            connection,
            transaction))
        {
            purge.Parameters.Add(new NpgsqlParameter<Guid>("id", erasing.ExperienceId));
            purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
            await purge.ExecuteScalarAsync();
        }

        // The function declares a SET for the same variable, so the marker is restored when it returns
        // rather than at the end of the transaction: even the purging connection cannot go on to delete
        // another record's log with it.
        await using var afterwards = new NpgsqlCommand(
            "DELETE FROM agent_experience.lifecycle_events WHERE experience_id = @id", connection, transaction);
        afterwards.Parameters.Add(new NpgsqlParameter<Guid>("id", bystander.ExperienceId));

        var refused = await Assert.ThrowsAsync<PostgresException>(() => afterwards.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_erasure_is_out_of_reach_of_a_role_that_was_never_granted_it()
    {
        // The most severe thing this story could have shipped: both purge functions are SECURITY
        // DEFINER, and PostgreSQL grants EXECUTE on a new function to PUBLIC by default. Left at that
        // default, a SELECT-only reporting role -- no DELETE, no UPDATE, anywhere -- could read an
        // experience_id and a scope out of the record table and permanently erase that record, in any
        // tenant. This proves the revoke, from a connection that is not the owner, which is the only
        // connection that can prove it.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        await using var reporterSource = await _fixture.CreateRoleAsync(
            "reporter",
            "GRANT USAGE ON SCHEMA agent_experience TO {role}",
            "GRANT SELECT ON ALL TABLES IN SCHEMA agent_experience TO {role}");

        await using var reporter = await reporterSource.OpenConnectionAsync();

        // It really can read the ID and the scope it would need. That is the whole premise.
        await using (var read = new NpgsqlCommand(
            "SELECT tenant_id FROM agent_experience.experience_records WHERE experience_id = @id", reporter))
        {
            read.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            Assert.Equal(tenant, await read.ExecuteScalarAsync());
        }

        await using (var purge = new NpgsqlCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(" +
            "@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            reporter))
        {
            purge.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });

            var denied = await Assert.ThrowsAsync<PostgresException>(() => purge.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        // ...and the grant purge is no easier, which matters because it is the unbounded-by-default one.
        await using (var grants = new NpgsqlCommand(
            "SELECT agent_experience.purge_expired_grants(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, now(), NULL)",
            reporter))
        {
            grants.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });

            var denied = await Assert.ThrowsAsync<PostgresException>(() => grants.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        // Nothing was erased, and the owner can still erase.
        Assert.Null((await ReadTombstoneAsync(record.ExperienceId)).DeletedAt);
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_record_row_itself_cannot_be_deleted_or_truncated_by_anybody()
    {
        // The statement that undoes the whole erasure: deleting the record row orphans an audit trail
        // that has no foreign key back to it, and -- worse -- FREES THE ID, so a record recreated under
        // it inherits every grant issued over the old content. That is exactly what the tombstone
        // exists to prevent, so the schema refuses it rather than the documentation promising it.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var bare = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, bare.SqlState);

        // The marker is not a way round it either: this guard has no exception at all, because the
        // erasure never deletes that row.
        var marked = await Assert.ThrowsAsync<PostgresException>(() => MarkedAsync(
            "DELETE FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, marked.SqlState);

        var truncate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "TRUNCATE agent_experience.experience_records CASCADE", id: null));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);

        // The record, its history, and its scope are all still there.
        Assert.Equal(1, await CountAsync("lifecycle_events", record.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // And after a real erasure the ID is still taken, which is the property the guard protects: a
        // create under it collides with the tombstone rather than inheriting the old grants.
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Conflict,
            (await _store.CreateAsync(auth, Minimal(scope, record.ExperienceId), CancellationToken.None)).Outcome);

        var tombstoneGone = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, tombstoneGone.SqlState);
    }

    [Fact]
    public async Task A_marked_update_cannot_tombstone_a_record_into_another_scope_or_restamp_it()
    {
        // The marked exception is shape-checked, and the shape includes the scope. Without that, one
        // marked UPDATE could tombstone a record into another tenant: the scope that owned it would then
        // see NotFound for its own erased record and a scope that never held it would see Deleted.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var mine = Scope(tenant, team: "team-a");
        var theirs = Scope(tenant, team: "team-b");
        var record = await ValidatedAsync(auth, mine);

        const string Tombstone =
            "UPDATE agent_experience.experience_records SET payload = '{}'::jsonb, task_id = '(deleted)', " +
            "status = 'Deleted', source_run_id = '00000000-0000-0000-0000-000000000000'::uuid, " +
            "reuse_confidence = 0, supporting_validations = 0, contradictions = 0, " +
            "created_at = now(), updated_at = now(), deleted_at = now(), revision = revision + 1";

        var moved = await Assert.ThrowsAsync<PostgresException>(() => MarkedAsync(
            $"{Tombstone}, team_id = 'team-b' WHERE experience_id = @id", record.ExperienceId));

        // ...nor keep a timestamp that says when the work happened...
        var restamped = await Assert.ThrowsAsync<PostgresException>(() => MarkedAsync(
            "UPDATE agent_experience.experience_records SET payload = '{}'::jsonb, task_id = '(deleted)', " +
            "status = 'Deleted', source_run_id = '00000000-0000-0000-0000-000000000000'::uuid, " +
            "reuse_confidence = 0, supporting_validations = 0, contradictions = 0, " +
            "updated_at = now(), deleted_at = now(), revision = revision + 1 WHERE experience_id = @id",
            record.ExperienceId));

        // ...nor rewrite the envelope version on the way past...
        var reversioned = await Assert.ThrowsAsync<PostgresException>(() => MarkedAsync(
            $"{Tombstone}, payload_version = payload_version + 1 WHERE experience_id = @id", record.ExperienceId));

        // ...nor skip a revision, which would leave a gap the event log could never explain.
        var jumped = await Assert.ThrowsAsync<PostgresException>(() => MarkedAsync(
            $"{Tombstone.Replace("revision = revision + 1", "revision = revision + 7", StringComparison.Ordinal)} " +
            "WHERE experience_id = @id",
            record.ExperienceId));

        Assert.All(
            [moved, restamped, reversioned, jumped],
            ex => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState));

        // Nothing moved, and the record is still the owning scope's live record.
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, mine, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await _store.GetAsync(auth, theirs, record.ExperienceId, CancellationToken.None)).Outcome);

        var stored = await ReadTombstoneAsync(record.ExperienceId);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(mine.TeamId, stored.TeamId);
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task Two_concurrent_deletes_of_one_record_erase_it_exactly_once()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await PopulatedAsync(auth, scope, Scope(tenant, team: "team-b"));

        var results = await Task.WhenAll(
            _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None),
            _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None));

        // Both succeed -- erasing an erased record is a success that touches nothing -- and both report
        // the same revision, because only one of them moved it.
        Assert.All(results, result => Assert.Equal(ExperienceStoreOutcome.Deleted, result.Outcome));
        Assert.Equal(results[0].Revision, results[1].Revision);

        var tombstone = await ReadTombstoneAsync(record.ExperienceId);
        Assert.Equal(record.Revision + 1, tombstone.Revision);
        Assert.Equal(results[0].Revision, tombstone.Revision);
    }

    [Fact]
    public async Task A_delete_racing_a_lifecycle_commit_leaves_a_tombstone_and_no_history()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var delete = _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        var commit = _store.CommitLifecycleEventAsync(
            auth, scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 1), CancellationToken.None);

        var deleted = await delete;
        var committed = await commit;

        // Whichever order the two land in, the record ends up erased with nothing left of its history:
        // if the commit won, the erasure swept the event it had just written; if the erasure won, the
        // commit is refused as Deleted and its event is rolled back with it.
        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Contains(
            committed.Outcome,
            new[] { ExperienceStoreOutcome.Committed, ExperienceStoreOutcome.Deleted, ExperienceStoreOutcome.StaleRevision });

        Assert.Equal(0, await CountAsync("lifecycle_events", record.ExperienceId));
        Assert.NotNull((await ReadTombstoneAsync(record.ExperienceId)).DeletedAt);
    }

    [Fact]
    public async Task A_grant_issued_while_a_record_is_being_erased_loses_instead_of_surviving_the_purge()
    {
        // AD-1, the grant path. experience_grants has no foreign key to experience_records, so nothing
        // parks this writer against the erasure by itself: before the row lock it decided against a
        // snapshot taken before the purge committed, and left a live 90-day permission over a spent ID.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = await ValidatedAsync(auth, owner);

        await using var purging = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await purging.BeginTransactionAsync();
        await PurgeInAsync(purging, transaction, record.ExperienceId, tenant, owner.TeamId);

        // Issued while the purge is open, so it blocks on the record row the purge holds FOR UPDATE.
        var issuing = _grants.CreateAsync(
            auth,
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), record.ExperienceId, owner, recipient, "a sibling team owns the follow-up",
                DateTimeOffset.UtcNow.AddDays(90)),
            CancellationToken.None);

        await Task.Delay(OverlapWindow);
        await transaction.CommitAsync();

        var issued = await issuing;

        Assert.Equal(ExperienceGrantOutcome.NotFound, issued.Outcome);
        Assert.Equal(0, await CountAsync("experience_grants", record.ExperienceId));
        Assert.Equal(0, await CountGrantEventsAsync(record.ExperienceId));
    }

    [Fact]
    public async Task Feedback_written_while_a_record_is_being_erased_loses_instead_of_surviving_the_purge()
    {
        // AD-1, the feedback path -- the one that leaves a reviewer identity and a free-text rationale
        // about an erased record permanently in an append-only table.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        await using var purging = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await purging.BeginTransactionAsync();
        await PurgeInAsync(purging, transaction, record.ExperienceId, tenant, scope.TeamId);

        var feedback = Feedback(scope, [record.ExperienceId]);
        var recording = _ledger.RecordAsync(auth, Submission(feedback), CancellationToken.None);

        await Task.Delay(OverlapWindow);
        await transaction.CommitAsync();

        var recorded = await recording;

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, recorded.Outcome);
        Assert.Equal("Exposures[0].ExperienceId", Assert.Single(recorded.Errors).Path);
        Assert.Equal(0, await CountFeedbackAsync(feedback.FeedbackId));
        Assert.Equal(0, await CountAsync("reuse_feedback_exposures", record.ExperienceId));
    }

    [Fact]
    public async Task Two_purges_sharing_one_submission_never_leave_it_orphaned()
    {
        // AD-4. A submission naming two records, both erased at once: each purge's "are there exposures
        // left?" used to see the other's uncommitted delete and leave the parent, so the row survived
        // describing nothing -- carrying a run ID, a scope, an outcome and a measure that nothing else
        // would ever collect. The submissions are locked before their exposures are deleted now.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var first = await ValidatedAsync(auth, scope);
        var second = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [first.ExperienceId, second.ExperienceId]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        // Deterministically interleaved: the first purge holds its transaction open across the second's
        // whole run, which is exactly the window the defect lived in.
        await using (var purging = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await purging.BeginTransactionAsync();
            await PurgeInAsync(purging, transaction, first.ExperienceId, tenant, scope.TeamId);

            var concurrent = _store.DeleteAsync(auth, scope, second.ExperienceId, CancellationToken.None);
            await Task.Delay(OverlapWindow);
            await transaction.CommitAsync();

            Assert.Equal(ExperienceStoreOutcome.Deleted, (await concurrent).Outcome);
        }

        Assert.Equal(0, await CountFeedbackAsync(feedback.FeedbackId));
        Assert.Equal(0, await CountAsync("reuse_feedback_exposures", first.ExperienceId));
        Assert.Equal(0, await CountAsync("reuse_feedback_exposures", second.ExperienceId));

        // ...and the same through two ordinary concurrent deletes, whatever order they happen to take.
        var third = await ValidatedAsync(auth, scope);
        var fourth = await ValidatedAsync(auth, scope);
        var shared = Feedback(scope, [third.ExperienceId, fourth.ExperienceId]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, shared, CancellationToken.None)).Outcome);

        var raced = await Task.WhenAll(
            _store.DeleteAsync(auth, scope, third.ExperienceId, CancellationToken.None),
            _store.DeleteAsync(auth, scope, fourth.ExperienceId, CancellationToken.None));

        Assert.All(raced, result => Assert.Equal(ExperienceStoreOutcome.Deleted, result.Outcome));
        Assert.Equal(0, await CountFeedbackAsync(shared.FeedbackId));
    }

    [Fact]
    public async Task A_rolled_back_purge_leaves_a_fully_populated_record_exactly_as_it_was()
    {
        // Rollback was proven only on a record carrying nothing: no evidence, no exposures, no feedback,
        // no grants, no embedding. Atomicity is worth exactly as much as the fullest record it holds for.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = await PopulatedAsync(auth, owner, recipient);
        var feedbackId = Assert.Single(await FeedbackIdsAsync(record.ExperienceId));

        var before = await ReadTombstoneAsync(record.ExperienceId);
        var counts = await EveryCountAsync(record.ExperienceId, feedbackId);

        await using (var purging = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await purging.BeginTransactionAsync();
            await PurgeInAsync(purging, transaction, record.ExperienceId, tenant, owner.TeamId);

            // Inside the open transaction the erasure has really happened -- so the rollback below is
            // undoing work, not asserting over a purge that never ran.
            await using (var check = new NpgsqlCommand(
                "SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = @id", purging, transaction))
            {
                check.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
                Assert.Equal(0L, await check.ExecuteScalarAsync());
            }

            await transaction.RollbackAsync();
        }

        Assert.Equal(before, await ReadTombstoneAsync(record.ExperienceId));
        Assert.Equal(counts, await EveryCountAsync(record.ExperienceId, feedbackId));
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, owner, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public async Task A_sweep_erases_only_what_a_frozen_clock_puts_past_the_cutoff_and_says_whether_more_remain()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var clock = new FrozenClock(ColumnTime);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: clock);

        // Four records at known ages, created oldest first, plus one that is comfortably inside the
        // retention window.
        var ancient = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-400));
        var older = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-200));
        var old = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-100));
        var fresh = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-1));

        // A ninety-day retention, two at a time: the sweep is bounded and says another pass would find
        // more, which is how a host's own scheduler drives it without this library owning a timer.
        var first = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 2, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, first.Outcome);
        Assert.Equal(2, first.DeletedCount);
        Assert.True(first.MoreRemain);

        // Oldest first, so it is the two oldest that went.
        Assert.NotNull((await ReadTombstoneAsync(ancient)).DeletedAt);
        Assert.NotNull((await ReadTombstoneAsync(older)).DeletedAt);
        Assert.Null((await ReadTombstoneAsync(old)).DeletedAt);

        var second = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 2, CancellationToken.None);

        Assert.Equal(1, second.DeletedCount);
        Assert.False(second.MoreRemain);
        Assert.NotNull((await ReadTombstoneAsync(old)).DeletedAt);

        // The one inside the window is untouched, and a third pass finds nothing left to do.
        Assert.Null((await ReadTombstoneAsync(fresh)).DeletedAt);

        var third = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 2, CancellationToken.None);
        Assert.Equal(0, third.DeletedCount);
        Assert.False(third.MoreRemain);

        // Age is measured from CreatedAt on this store's own clock. Winding the clock forward by a year
        // makes the record that was inside the window fall outside it -- and nothing else changed.
        clock.Advance(TimeSpan.FromDays(365));

        var later = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 2, CancellationToken.None);
        Assert.Equal(1, later.DeletedCount);
        Assert.NotNull((await ReadTombstoneAsync(fresh)).DeletedAt);

        // And a sweep of a scope that holds nothing is a normal, empty answer rather than a refusal.
        var elsewhere = await store.SweepExpiredAsync(auth, Scope(tenant, team: "team-empty"), TimeSpan.FromDays(1), 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, elsewhere.Outcome);
        Assert.Equal(0, elsewhere.DeletedCount);
    }

    [Fact]
    public async Task A_sweep_never_reaches_another_scope()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var mine = Scope(tenant, team: "team-a");
        var theirs = Scope(tenant, team: "team-b");
        var clock = new FrozenClock(ColumnTime);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: clock);

        var ours = await SeedAtAsync(auth, mine, ColumnTime.AddDays(-400));
        var other = await SeedAtAsync(auth, theirs, ColumnTime.AddDays(-400));

        var swept = await store.SweepExpiredAsync(auth, mine, TimeSpan.FromDays(90), 50, CancellationToken.None);

        Assert.Equal(1, swept.DeletedCount);
        Assert.NotNull((await ReadTombstoneAsync(ours)).DeletedAt);
        Assert.Null((await ReadTombstoneAsync(other)).DeletedAt);
    }

    [Fact]
    public async Task Expired_grants_and_their_events_are_purged_and_a_live_grant_is_left_alone()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var other = Scope(tenant, team: "team-c");
        var record = await ValidatedAsync(auth, owner);

        var live = await IssueAsync(auth, record.ExperienceId, owner, recipient, DateTimeOffset.UtcNow.AddHours(1));

        // A grant issued two days ago for one day, which is therefore a day past its expiry. It is
        // seeded directly rather than issued and waited out: an expiry may only ever move closer
        // (0006's monotonicity guard) but never behind issued_at (0005's CHECK), so the only honest way
        // to have an expired grant is to have issued it in the past.
        var expired = await SeedExpiredGrantAsync(record.ExperienceId, owner, other);

        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);
        var purged = await _grants.PurgeExpiredAsync(auth, administration, owner, 50, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, purged.Outcome);
        Assert.Equal(1, purged.PurgedCount);
        Assert.False(purged.MoreRemain);

        // The expired grant and its whole trail are gone; the live one and its trail are untouched.
        Assert.Equal(0, await CountGrantAsync(expired));
        Assert.Equal(0, await CountGrantEventsForAsync(expired));
        Assert.Equal(1, await CountGrantAsync(live));
        Assert.True(await CountGrantEventsForAsync(live) > 0);

        // Administrator authority is required, exactly as it is for issuing and revoking.
        var unauthorized = await _grants.PurgeExpiredAsync(auth, administration: null, owner, 50, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, unauthorized.Outcome);

        // And so is authorization over the owner scope itself.
        var denied = await _grants.PurgeExpiredAsync(Authorize(NewTenant()), administration, owner, 50, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);

        var invalid = await _grants.PurgeExpiredAsync(auth, administration, owner, 0, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, invalid.Outcome);
        Assert.Equal("BatchSize", Assert.Single(invalid.Errors).Path);
    }

    [Fact]
    public async Task A_sweep_measures_age_on_created_at_even_when_the_record_has_been_written_since()
    {
        // The documented guarantee: "a record that is read, ranked, or re-scored does not thereby become
        // younger". A fixture whose created_at and updated_at are equal cannot tell the two apart, so a
        // sweep that measured the wrong one would pass -- and a repeatedly-reinforced record would never
        // expire, which is a retention obligation silently unmet.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var clock = new FrozenClock(ColumnTime);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: clock);

        var stale = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-400));

        // A lifecycle commit stamps updated_at from the store's own clock, so this record is four hundred
        // days old and was written a moment ago.
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await store.CommitLifecycleEventAsync(
                auth, scope, Event(stale, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None)).Outcome);

        var written = await ReadTombstoneAsync(stale);
        Assert.True(written.UpdatedAt > written.CreatedAt, "The fixture has to separate the two timestamps or it proves nothing.");
        Assert.Equal(ColumnTime, written.UpdatedAt);

        var swept = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 50, CancellationToken.None);

        Assert.Equal(1, swept.DeletedCount);
        Assert.NotNull((await ReadTombstoneAsync(stale)).DeletedAt);
    }

    [Fact]
    public async Task A_sweep_stamps_the_tombstone_from_the_injected_clock_and_says_so_when_it_stops_early()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var clock = new FrozenClock(ColumnTime);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: clock);

        // deleted_at is the injected clock's reading, to the microsecond, not merely "consistent with"
        // created_at and updated_at.
        var first = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-400));
        var deleted = await store.DeleteAsync(auth, scope, first, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Equal(ColumnTime, (await ReadTombstoneAsync(first)).DeletedAt);

        // ...and the same clock stamps a sweep's tombstones.
        var second = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-399));
        var third = await SeedAtAsync(auth, scope, ColumnTime.AddDays(-398));

        clock.Advance(TimeSpan.FromDays(3));

        // Now the interruption. The sweep erases the older of the two and then blocks on the row another
        // connection is holding, which is the moment the caller cancels -- so the count it reports is a
        // fact about irreversible work rather than a number thrown away with the exception.
        await using var holding = await _fixture.DataSource.OpenConnectionAsync();
        await using var hold = await holding.BeginTransactionAsync();
        await using (var pin = new NpgsqlCommand(
            "SELECT revision FROM agent_experience.experience_records WHERE experience_id = @id FOR UPDATE", holding, hold))
        {
            pin.Parameters.Add(new NpgsqlParameter<Guid>("id", third));
            Assert.NotNull(await pin.ExecuteScalarAsync());
        }

        using var cancellation = new CancellationTokenSource();
        var sweeping = store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 50, cancellation.Token);

        await Task.Delay(OverlapWindow);
        await cancellation.CancelAsync();

        var partial = await sweeping;

        Assert.True(partial.Interrupted);
        Assert.Equal(1, partial.DeletedCount);
        Assert.True(partial.MoreRemain);
        Assert.Equal(ColumnTime.AddDays(3), (await ReadTombstoneAsync(second)).DeletedAt);
        Assert.Null((await ReadTombstoneAsync(third)).DeletedAt);

        await hold.RollbackAsync();

        // And the record it stopped on is still there to be erased by the next pass, which is what
        // MoreRemain promised.
        var resumed = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 50, CancellationToken.None);
        Assert.False(resumed.Interrupted);
        Assert.Equal(1, resumed.DeletedCount);
    }

    [Fact]
    public async Task A_grant_purge_never_reaches_another_scope_and_bounds_its_own_batch()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var mine = Scope(tenant, team: "team-a");
        var theirs = Scope(tenant, team: "team-b");
        var ours = await ValidatedAsync(auth, mine);
        var other = await ValidatedAsync(auth, theirs);

        var expiredHere = await SeedExpiredGrantAsync(ours.ExperienceId, mine, Scope(tenant, team: "recipient-1"));
        var expiredThere = await SeedExpiredGrantAsync(other.ExperienceId, theirs, Scope(tenant, team: "recipient-2"));

        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);
        var purged = await _grants.PurgeExpiredAsync(auth, administration, mine, 50, CancellationToken.None);

        // One scope's purge collects one scope's grants. Without the scope predicate this would erase
        // every tenant's expired grants and their audit events, and nothing would have noticed.
        Assert.Equal(1, purged.PurgedCount);
        Assert.Equal(0, await CountGrantAsync(expiredHere));
        Assert.Equal(1, await CountGrantAsync(expiredThere));
        Assert.True(await CountGrantEventsForAsync(expiredThere) > 0);

        // The batch bound is the function's, not the caller's. Three more expired grants, two at a time.
        var a = await SeedExpiredGrantAsync(ours.ExperienceId, mine, Scope(tenant, team: "recipient-3"));
        var b = await SeedExpiredGrantAsync(ours.ExperienceId, mine, Scope(tenant, team: "recipient-4"));
        var c = await SeedExpiredGrantAsync(ours.ExperienceId, mine, Scope(tenant, team: "recipient-5"));

        var page = await _grants.PurgeExpiredAsync(auth, administration, mine, 2, CancellationToken.None);
        Assert.Equal(2, page.PurgedCount);
        Assert.True(page.MoreRemain);

        var last = await _grants.PurgeExpiredAsync(auth, administration, mine, 2, CancellationToken.None);
        Assert.Equal(1, last.PurgedCount);
        Assert.False(last.MoreRemain);
        Assert.Equal(0, await CountGrantAsync(a) + await CountGrantAsync(b) + await CountGrantAsync(c));

        // And the bound holds for a hand-caller that never touches this adapter: LIMIT NULL means "no
        // limit" in PostgreSQL, so a bound that lived only in the validator was no bound at all.
        await SeedExpiredGrantsAsync(ours.ExperienceId, mine, PostgresExperienceRecordStore.MaxSweepBatchSize + 1);

        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT agent_experience.purge_expired_grants(@tenant, 'app-1', 'project-1', @team, NULL, NULL, now(), NULL)");
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
        command.Parameters.Add(new NpgsqlParameter<string>("team", NpgsqlDbType.Text) { TypedValue = mine.TeamId! });

        Assert.Equal((long)PostgresExperienceRecordStore.MaxSweepBatchSize, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_grant_naming_an_erased_record_is_collected_and_a_skewed_host_clock_collects_nothing_extra()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var record = await ValidatedAsync(auth, owner);

        // A live grant, and a record erased out from under it. The record purge removes its grants in its
        // own transaction, so this one is written afterwards -- the case the script justifies at length
        // as "exactly the row nothing else would ever collect", and the one with no test at all.
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, owner, record.ExperienceId, CancellationToken.None)).Outcome);

        var orphan = await SeedGrantAsync(record.ExperienceId, owner, Scope(tenant, team: "recipient-1"), expiresIn: TimeSpan.FromDays(90));
        Assert.Equal(1, await CountGrantAsync(orphan));

        // A second, live grant over a live record, to prove the collection is about the tombstone rather
        // than about sweeping everything in the scope.
        var live = await ValidatedAsync(auth, owner);
        var kept = await SeedGrantAsync(live.ExperienceId, owner, Scope(tenant, team: "recipient-2"), expiresIn: TimeSpan.FromHours(1));

        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);

        // The host's clock is a day fast. That must not widen what is destroyed: the cutoff is
        // LEAST(host, clock_timestamp()), so the grant that expires in an hour is still live.
        var skewed = new PostgresExperienceGrantStore(
            _fixture.DataSource, policy: null, timeProvider: new FrozenClock(DateTimeOffset.UtcNow.AddDays(1)));

        var purged = await skewed.PurgeExpiredAsync(auth, administration, owner, 50, CancellationToken.None);

        Assert.Equal(1, purged.PurgedCount);
        Assert.Equal(0, await CountGrantAsync(orphan));
        Assert.Equal(1, await CountGrantAsync(kept));
    }

    // ------------------------------------------------------------------ what a tombstone still hides

    [Fact]
    public async Task Another_scopes_tombstone_is_invisible_to_a_feedback_submission()
    {
        // The refusal names an exposure by position, so it is a statement about an ID the caller
        // supplied. If the check could see another scope's tombstone, that refusal would tell one tenant
        // that another tenant once held -- and erased -- a record under an ID it merely guessed.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var mine = Scope(tenant, team: "team-a");
        var theirs = Scope(tenant, team: "team-b");
        var record = await ValidatedAsync(auth, theirs);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, theirs, record.ExperienceId, CancellationToken.None)).Outcome);

        var feedback = Feedback(mine, [record.ExperienceId]);
        var recorded = await _ledger.RecordAsync(auth, Submission(feedback), CancellationToken.None);

        // Recorded, exactly as an ID that never existed anywhere would be: "the run saw an ID that
        // resolves to nothing here" is a fact worth keeping, and it is the same fact either way.
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Recorded, recorded.Outcome);
        Assert.Equal(1, await CountFeedbackAsync(feedback.FeedbackId));

        // ...while the scope that owns the tombstone is still refused.
        var owning = Feedback(theirs, [record.ExperienceId]);
        var refused = await _ledger.RecordAsync(auth, Submission(owning), CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, refused.Outcome);
        Assert.Equal("Exposures[0].ExperienceId", Assert.Single(refused.Errors).Path);
    }

    [Fact]
    public async Task The_erased_text_itself_is_no_longer_findable_by_search()
    {
        // The point of erasing a record is that its words are gone, so the assertion has to be about the
        // words -- searching for a term that only ever appeared in the erased payload. Searching for
        // "deleted" instead would tokenize to the tombstone's own placeholder and prove the opposite of
        // what it looks like it proves.
        const string Distinctive = "zanzibarine";

        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var search = new PostgresExperienceCandidateSource(_fixture.DataSource);
        var query = new ExperienceCandidateQuery(
            scope, $"{Distinctive} reconciliation", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0, 50);

        var record = Minimal(scope) with
        {
            TaskSummary = $"{Distinctive} reconciliation of a settlement ledger",
            ReuseConfidence = 2d / 3d,
            SupportingValidations = 1,
        };

        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await _store.CommitLifecycleEventAsync(
                auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None)).Outcome);

        // Findable by that word, which is what makes the assertion after the erasure mean something.
        var before = await search.SearchAsync(auth, query, CancellationToken.None);
        Assert.Contains(record.ExperienceId, before.Candidates.Select(c => c.Record.ExperienceId));

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        var after = await search.SearchAsync(auth, query, CancellationToken.None);
        Assert.Empty(after.Candidates);

        // Not filtered out of the answer -- gone from the generated index term itself, which is what
        // "no separate index maintenance" rests on.
        var tombstone = await ReadTombstoneAsync(record.ExperienceId);
        Assert.Equal("'delet':1", tombstone.SearchVector);
        Assert.DoesNotContain(Distinctive, tombstone.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Distinctive, tombstone.TaskId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confidence_evidence_written_back_against_a_tombstone_is_never_read_as_a_replay()
    {
        // The evidence ID is a global primary key, and this is the one statement that looks a row up by
        // it alone. Without the tombstone filter, a re-inserted ledger row naming an erased record would
        // make a replayed submission come back Committed -- reporting a commit that never happened, and
        // handing back the erased record's old revision and status on the way.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var evidenceId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var confidence = new ConfidenceUpdate(
            EvidenceId: evidenceId,
            Kind: ConfidenceEvidenceKind.Supporting,
            Source: ConfidenceEvidenceSource.Machine,
            RunId: Guid.NewGuid(),
            VerificationRoundId: Guid.NewGuid(),
            ReviewerIdentity: null,
            RuleVersion: ReuseConfidenceHeuristic.RuleVersion,
            PriorReuseConfidence: 2d / 3d,
            NewReuseConfidence: 3d / 4d,
            PriorSupportingValidations: 1,
            NewSupportingValidations: 2,
            PriorContradictions: 0,
            NewContradictions: 0);

        var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 1, eventId) with
        {
            Confidence = confidence,
        };

        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await _store.CommitLifecycleEventAsync(auth, scope, lifecycleEvent, CancellationToken.None)).Outcome);

        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await _store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(0, await CountAsync("confidence_evidence", record.ExperienceId));

        // The ledger row put back by hand. Nothing in this library writes evidence against a tombstone --
        // the append-only guard refuses UPDATE and DELETE, not INSERT -- but nothing in the schema stops
        // another tool from doing it either, which is exactly the case this filter exists for.
        await RestoreEvidenceAsync(evidenceId, record.ExperienceId, eventId, confidence);
        Assert.Equal(1, await CountAsync("confidence_evidence", record.ExperienceId));

        // The identical submission again. Conflict, not Committed: the stored row's record is a
        // tombstone, so the replay comparison never sees it and nothing about it leaks back.
        var replay = await _store.CommitLifecycleEventAsync(auth, scope, lifecycleEvent, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, replay.Outcome);
        Assert.Null(replay.AppliedConfidence);
        Assert.Equal(0, replay.Revision);
    }

    [Fact]
    public async Task The_feedback_store_stamps_recorded_at_from_its_own_injected_clock()
    {
        // The TimeProvider this story added to the feedback store is not exercised by any other test, so
        // a store that ignored it entirely would look exactly as correct.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var ledger = new PostgresExperienceReuseFeedbackStore(_fixture.DataSource, new FrozenClock(ColumnTime));
        var feedback = Feedback(scope, [record.ExperienceId]);

        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Recorded,
            (await ledger.RecordAsync(auth, Submission(feedback), CancellationToken.None)).Outcome);

        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT recorded_at, observed_at FROM agent_experience.reuse_feedback WHERE feedback_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", feedback.FeedbackId));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        // recorded_at is the store's own reading of when the row landed; observed_at is the caller's.
        Assert.Equal(ColumnTime, reader.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(feedback.ObservedAt, reader.GetFieldValue<DateTimeOffset>(1));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A record carrying one of everything an erasure has to reach.</summary>
    private async Task<ExperienceRecord> PopulatedAsync(AuthorizationContext auth, Scope owner, Scope recipient)
    {
        var record = await ValidatedAsync(auth, owner);

        // Confidence evidence and a second lifecycle event.
        await ApplyEvidenceAsync(auth, owner, record.ExperienceId);

        // A feedback submission and its exposure row.
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, Feedback(owner, [record.ExperienceId]), CancellationToken.None)).Outcome);

        // A grant, its issue event, and one recorded delivery through it.
        await IssueAsync(auth, record.ExperienceId, owner, recipient, DateTimeOffset.UtcNow.AddHours(1));

        var shared = await _store.GetAsync(auth, recipient, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, shared.Outcome);
        Assert.True(shared.SharedByGrant);

        return record with { Revision = 2 };
    }

    /// <summary>One counted piece of confidence evidence, which is also a second lifecycle event.</summary>
    private async Task ApplyEvidenceAsync(AuthorizationContext auth, Scope scope, Guid experienceId)
    {
        var applied = await _lifecycle.ApplyEvidenceAsync(
            auth,
            new ApplyConfidenceEvidenceRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: experienceId,
                Scope: scope,
                EvidenceId: Guid.NewGuid(),
                Kind: ConfidenceEvidenceKind.Supporting,
                Source: ConfidenceEvidenceSource.Machine,
                RunId: Guid.NewGuid(),
                VerificationRoundId: Guid.NewGuid(),
                Reason: "reuse was observed to hold up",
                Producer: "tests",
                OccurredAt: PayloadTime),
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
    }

    private async Task<Guid> IssueAsync(
        AuthorizationContext auth,
        Guid experienceId,
        Scope owner,
        Scope recipient,
        DateTimeOffset expiry)
    {
        var result = await _grants.CreateAsync(
            auth,
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), experienceId, owner, recipient, "a sibling team owns the follow-up", expiry),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
        return result.Grant!.GrantId;
    }

    /// <summary>
    /// A grant written straight into the table, with an expiry the caller chooses. The library refuses to
    /// issue one over a tombstone, so this is how a test stages the row the expired-grant purge exists to
    /// collect: a permission naming a record that is already erased.
    /// </summary>
    private async Task<Guid> SeedGrantAsync(Guid experienceId, Scope owner, Scope recipient, TimeSpan expiresIn)
    {
        var grantId = Guid.NewGuid();

        await using var grant = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, " +
            "project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, reason, " +
            "administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) VALUES " +
            "(@grant_id, @experience_id, @tenant, 'app-1', 'project-1', @team, NULL, NULL, @tenant, 'app-1', " +
            "'project-1', @recipient_team, NULL, NULL, 'written outside this library', @administrator, " +
            "now(), now() + @expires_in, NULL, NULL)");

        grant.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
        grant.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        grant.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = owner.TenantId });
        grant.Parameters.Add(new NpgsqlParameter<string>("team", NpgsqlDbType.Text) { TypedValue = owner.TeamId! });
        grant.Parameters.Add(new NpgsqlParameter<string>("recipient_team", NpgsqlDbType.Text) { TypedValue = recipient.TeamId! });
        grant.Parameters.Add(new NpgsqlParameter<string>("administrator", NpgsqlDbType.Text) { TypedValue = Administrator });
        grant.Parameters.Add(new NpgsqlParameter<TimeSpan>("expires_in", expiresIn));

        Assert.Equal(1, await grant.ExecuteNonQueryAsync());
        return grantId;
    }

    /// <summary>
    /// <paramref name="count"/> expired grants in one statement, each to its own recipient team, so a
    /// hand-caller's unbounded batch has more than the function's maximum to reach for.
    /// </summary>
    private async Task SeedExpiredGrantsAsync(Guid experienceId, Scope owner, int count)
    {
        await using var grants = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, " +
            "project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, reason, " +
            "administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) " +
            "SELECT gen_random_uuid(), @experience_id, @tenant, 'app-1', 'project-1', @team, NULL, NULL, @tenant, " +
            "'app-1', 'project-1', 'bulk-' || i, NULL, NULL, 'a window that has since closed', @administrator, " +
            "now() - interval '2 days', now() - interval '1 day', NULL, NULL FROM generate_series(1, @count) i");

        grants.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        grants.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = owner.TenantId });
        grants.Parameters.Add(new NpgsqlParameter<string>("team", NpgsqlDbType.Text) { TypedValue = owner.TeamId! });
        grants.Parameters.Add(new NpgsqlParameter<string>("administrator", NpgsqlDbType.Text) { TypedValue = Administrator });
        grants.Parameters.Add(new NpgsqlParameter<int>("count", count));

        Assert.Equal(count, await grants.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// Puts one confidence-evidence row back after an erasure removed it, exactly as it was. The
    /// append-only guard refuses UPDATE and DELETE, never INSERT, so this is reachable by any writer with
    /// INSERT on the table -- which is the point.
    /// </summary>
    private async Task RestoreEvidenceAsync(Guid evidenceId, Guid experienceId, Guid eventId, ConfidenceUpdate confidence)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.confidence_evidence (evidence_id, experience_id, event_id, kind, source, " +
            "run_id, verification_round_id, reviewer_identity, counted, actor, rule_version, detail, recorded_at, " +
            "applied_revision, applied_status, prior_reuse_confidence, new_reuse_confidence, " +
            "prior_supporting_validations, new_supporting_validations, prior_contradictions, new_contradictions) " +
            "VALUES (@evidence_id, @experience_id, @event_id, @kind, @source, @run_id, @round_id, NULL, true, " +
            "'tests', @rule_version, NULL, now(), 2, 'Reinforced', @prior_confidence, @new_confidence, " +
            "@prior_supporting, @new_supporting, 0, 0)");

        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("evidence_id", evidenceId));
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        parameters.Add(new NpgsqlParameter<Guid>("event_id", eventId));
        parameters.Add(new NpgsqlParameter<string>("kind", NpgsqlDbType.Text) { TypedValue = confidence.Kind.ToString() });
        parameters.Add(new NpgsqlParameter<string>("source", NpgsqlDbType.Text) { TypedValue = confidence.Source.ToString() });
        parameters.Add(new NpgsqlParameter<Guid>("run_id", confidence.RunId));
        parameters.Add(new NpgsqlParameter<Guid>("round_id", confidence.VerificationRoundId!.Value));
        parameters.Add(new NpgsqlParameter<string>("rule_version", NpgsqlDbType.Text) { TypedValue = confidence.RuleVersion });
        parameters.Add(new NpgsqlParameter<double>("prior_confidence", confidence.PriorReuseConfidence));
        parameters.Add(new NpgsqlParameter<double>("new_confidence", confidence.NewReuseConfidence));
        parameters.Add(new NpgsqlParameter<int>("prior_supporting", confidence.PriorSupportingValidations));
        parameters.Add(new NpgsqlParameter<int>("new_supporting", confidence.NewSupportingValidations));

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>A grant issued two days ago for one day: live when it was issued, expired now.</summary>
    private async Task<Guid> SeedExpiredGrantAsync(Guid experienceId, Scope owner, Scope recipient)
    {
        var grantId = Guid.NewGuid();

        await using (var grant = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, " +
            "project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, reason, " +
            "administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) VALUES " +
            "(@grant_id, @experience_id, @tenant, 'app-1', 'project-1', @team, NULL, NULL, @tenant, 'app-1', " +
            "'project-1', @recipient_team, NULL, NULL, 'a window that has since closed', @administrator, " +
            "now() - interval '2 days', now() - interval '1 day', NULL, NULL)"))
        {
            grant.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
            grant.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            grant.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = owner.TenantId });
            grant.Parameters.Add(new NpgsqlParameter<string>("team", NpgsqlDbType.Text) { TypedValue = owner.TeamId! });
            grant.Parameters.Add(new NpgsqlParameter<string>("recipient_team", NpgsqlDbType.Text) { TypedValue = recipient.TeamId! });
            grant.Parameters.Add(new NpgsqlParameter<string>("administrator", NpgsqlDbType.Text) { TypedValue = Administrator });
            Assert.Equal(1, await grant.ExecuteNonQueryAsync());
        }

        await using (var issued = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grant_events (event_id, grant_id, experience_id, action, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, recipient_tenant_id, " +
            "recipient_application_id, recipient_project_id, recipient_team_id, recipient_agent_id, " +
            "recipient_user_id, reason, administrator_principal_id, administrator_authorized_at, expires_at, " +
            "occurred_at, recorded_at, disclosure) SELECT @event_id, g.grant_id, g.experience_id, 'Issued', g.tenant_id, " +
            "g.application_id, g.project_id, g.team_id, g.agent_id, g.user_id, g.recipient_tenant_id, " +
            "g.recipient_application_id, g.recipient_project_id, g.recipient_team_id, g.recipient_agent_id, " +
            "g.recipient_user_id, g.reason, g.administrator_principal_id, g.issued_at, g.expires_at, " +
            "g.issued_at, g.issued_at, g.disclosure FROM agent_experience.experience_grants g WHERE g.grant_id = @grant_id"))
        {
            issued.Parameters.Add(new NpgsqlParameter<Guid>("event_id", Guid.NewGuid()));
            issued.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
            Assert.Equal(1, await issued.ExecuteNonQueryAsync());
        }

        return grantId;
    }

    private async Task<ExperienceRecord> ValidatedAsync(AuthorizationContext auth, Scope scope)
    {
        var record = Minimal(scope) with
        {
            ReuseConfidence = 2d / 3d,
            SupportingValidations = 1,
            Contradictions = 0,
        };

        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);

        var commit = await _store.CommitLifecycleEventAsync(
            auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);

        return record with { Status = ExperienceStatus.Validated, Revision = 1 };
    }

    private async Task<Guid> SeedAtAsync(AuthorizationContext auth, Scope scope, DateTimeOffset createdAt)
    {
        var record = Minimal(scope, createdAt: createdAt);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    private static ExperienceReuseFeedback Feedback(Scope scope, IReadOnlyList<Guid> exposed) => new(
        FeedbackId: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        Scope: scope,
        ExposedExperienceIds: exposed,
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new("task-success", 1),
        ObservedAt: ColumnTime,
        TrialLabel: "memory-enabled");

    /// <summary>The unattributed ledger shape of <paramref name="feedback"/>, for driving the port directly.</summary>
    private static RecordedExperienceReuseFeedback Submission(ExperienceReuseFeedback feedback) => new(
        feedback.FeedbackId,
        feedback.RunId,
        feedback.Scope,
        feedback.RunOutcome,
        feedback.ClaimedBenefit,
        ExperienceReuseBenefit.Unknown,
        ReuseAttributionSource.None,
        ReviewerIdentity: null,
        EvaluatorId: null,
        VerificationRoundId: null,
        AssessmentId: null,
        Rationale: null,
        EvidenceIds: [],
        AttributedAt: null,
        feedback.Measure,
        feedback.TrialLabel,
        feedback.ObservedAt,
        [.. feedback.ExposedExperienceIds.Order().Select(id => new ExperienceReuseExposure(id, false, null))]);

    private async Task<long> CountAsync(string table, Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            $"SELECT count(*) FROM agent_experience.{table} WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountGrantEventsAsync(Guid experienceId) =>
        await CountAsync("experience_grant_events", experienceId);

    private async Task<long> CountGrantEventsForAsync(Guid grantId) =>
        await ScalarAsync("SELECT count(*) FROM agent_experience.experience_grant_events WHERE grant_id = @id", grantId);

    private async Task<long> CountGrantAsync(Guid grantId) =>
        await ScalarAsync("SELECT count(*) FROM agent_experience.experience_grants WHERE grant_id = @id", grantId);

    private async Task<long> CountFeedbackAsync(Guid feedbackId) =>
        await ScalarAsync("SELECT count(*) FROM agent_experience.reuse_feedback WHERE feedback_id = @id", feedbackId);

    private async Task<long> ScalarAsync(string sql, Guid id)
    {
        await using var command = _fixture.DataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<Guid>> FeedbackIdsAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT feedback_id FROM agent_experience.reuse_feedback_exposures WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>The stored row, read column by column, because the point is what the columns hold.</summary>
    private async Task<StoredRow> ReadTombstoneAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, " +
            "reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload::text, " +
            "deleted_at, search_vector::text FROM agent_experience.experience_records WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The record row is gone; a delete leaves a tombstone, never nothing.");

        return new StoredRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetDouble(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(12),
            reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetString(17));
    }

    /// <summary>
    /// Runs the purge function inside a transaction the caller keeps open, so a second writer can be
    /// issued against a record that is erased but not yet committed. That window is where every one of
    /// this story's concurrency defects lived, and holding it open is what makes a race a test rather
    /// than a coincidence.
    /// </summary>
    private static async Task PurgeInAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid experienceId,
        string tenant,
        string? team)
    {
        await using var purge = new NpgsqlCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(" +
            "@id, @tenant, 'app-1', 'project-1', @team, NULL, NULL, NULL, now())",
            connection,
            transaction);

        purge.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
        purge.Parameters.Add(new NpgsqlParameter("team", NpgsqlDbType.Text) { Value = (object?)team ?? DBNull.Value });

        Assert.Equal("Deleted", await purge.ExecuteScalarAsync());
    }

    /// <summary>Runs one statement with the purge marker hand-set, which any session may do.</summary>
    private async Task<int> MarkedAsync(string sql, Guid id)
    {
        // As the tables' owner, which holds DELETE: the marker is only meaningful to a writer that could
        // delete at all. The application role is refused by the privilege system first
        // (PostgresApplicationRoleTests).
        await using var connection = await _fixture.OwnerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var marker = new NpgsqlCommand("SET LOCAL agent_experience.purge_authorized = 'on'", connection, transaction))
        {
            await marker.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));

        try
        {
            var affected = await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return affected;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Every table an erasure sweeps, counted in one shape, so "nothing moved" is one assertion.</summary>
    private async Task<StoredCounts> EveryCountAsync(Guid experienceId, Guid feedbackId) => new(
        await CountAsync("lifecycle_events", experienceId),
        await CountAsync("confidence_evidence", experienceId),
        await CountAsync("reuse_feedback_exposures", experienceId),
        await CountFeedbackAsync(feedbackId),
        await CountAsync("experience_grants", experienceId),
        await CountGrantEventsAsync(experienceId),
        await CountAsync("experience_grant_access", experienceId));

    /// <summary>A hand-written statement, as the tables' owner: the guard under test must refuse a writer that holds the privilege.</summary>
    private async Task<int> ExecuteAsync(string sql, Guid? id, string? tenant = null)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        if (id is { } value)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", value));
        }

        if (tenant is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
        }

        return await command.ExecuteNonQueryAsync();
    }

    private sealed record StoredCounts(
        long LifecycleEvents,
        long ConfidenceEvidence,
        long Exposures,
        long Feedback,
        long Grants,
        long GrantEvents,
        long GrantAccess);

    private sealed record StoredRow(
        Guid SourceRunId,
        string TenantId,
        string ApplicationId,
        string ProjectId,
        string? TeamId,
        string? AgentId,
        string? UserId,
        string TaskId,
        string Status,
        double ReuseConfidence,
        int SupportingValidations,
        int Contradictions,
        long Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string Payload,
        DateTimeOffset? DeletedAt,
        string SearchVector);

    /// <summary>A clock the test moves by hand, so "older than ninety days" is a fact rather than a wait.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
