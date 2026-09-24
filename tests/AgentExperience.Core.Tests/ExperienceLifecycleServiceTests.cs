using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Core owns which transitions are legal (ARCHITECTURE-SPINE AD-6). These tests pin the complete MVP
/// table -- Candidate to Validated or Quarantined, Validated to Reinforced, Validated or Reinforced to
/// Contested/Stale/Superseded, and anything but Revoked to Revoked -- prove a transition outside it
/// never reaches the store, prove supersession's replacement rules are decided before any write, and
/// prove the store's outcome is surfaced one-to-one rather than reinterpreted.
/// </summary>
public class ExperienceLifecycleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "principal", ["experience:write"], Now);

    private static readonly ExperienceStatus[] EveryStatus = Enum.GetValues<ExperienceStatus>();

    private static CommitLifecycleTransitionRequest Request(
        ExperienceStatus? prior,
        ExperienceStatus current,
        long expectedRevision = 0,
        Guid? eventId = null,
        Guid? replacement = null,
        Guid? experienceId = null) => new(
            EventId: eventId ?? Guid.NewGuid(),
            ExperienceId: experienceId ?? Guid.NewGuid(),
            Scope: TestScope,
            PriorStatus: prior,
            CurrentStatus: current,
            Reason: "verified evidence",
            Producer: "finalization",
            OccurredAt: Now,
            ExpectedRevision: expectedRevision,
            ReplacementExperienceId: replacement ?? (current == ExperienceStatus.Superseded ? Guid.NewGuid() : null));

    [Fact]
    public async Task Candidate_to_Validated_is_allowed_and_reaches_the_store()
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);

        var result = await service.CommitAsync(Authorization, Request(ExperienceStatus.Candidate, ExperienceStatus.Validated, 2), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(3, result.Revision);
        Assert.Empty(result.Errors);
        Assert.Null(result.Reason);
        Assert.Null(result.Deindexing);
        Assert.Single(store.Commits);
    }

    [Fact]
    public async Task Every_status_except_Revoked_may_be_revoked()
    {
        foreach (var prior in EveryStatus.Where(s => s != ExperienceStatus.Revoked))
        {
            var store = new RecordingStore();
            var service = new ExperienceLifecycleService(store);

            var result = await service.CommitAsync(Authorization, Request(prior, ExperienceStatus.Revoked), CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
            Assert.Equal(ExperienceStatus.Revoked, Assert.Single(store.Commits).Event.CurrentStatus);
        }
    }

    [Fact]
    public async Task A_Validated_record_walks_the_whole_MVP_table_one_transition_at_a_time()
    {
        // Reinforce, contest, make stale, supersede: each is its own accepted move out of Validated or
        // Reinforced, and each reaches the store as exactly the event Core stamped.
        foreach (var (prior, current) in new[]
        {
            (ExperienceStatus.Validated, ExperienceStatus.Reinforced),
            (ExperienceStatus.Validated, ExperienceStatus.Contested),
            (ExperienceStatus.Validated, ExperienceStatus.Stale),
            (ExperienceStatus.Validated, ExperienceStatus.Superseded),
            (ExperienceStatus.Reinforced, ExperienceStatus.Contested),
            (ExperienceStatus.Reinforced, ExperienceStatus.Stale),
            (ExperienceStatus.Reinforced, ExperienceStatus.Superseded),
        })
        {
            var store = new RecordingStore();
            var service = new ExperienceLifecycleService(store);

            var result = await service.CommitAsync(Authorization, Request(prior, current), CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
            var committed = Assert.Single(store.Commits).Event;
            Assert.Equal(prior, committed.PriorStatus);
            Assert.Equal(current, committed.CurrentStatus);
        }
    }

    [Theory]
    [InlineData(ExperienceStatus.Revoked, ExperienceStatus.Quarantined)] // revocation is terminal
    [InlineData(ExperienceStatus.Revoked, ExperienceStatus.Revoked)] // including against itself
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Candidate)] // no walking a record back to candidate
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Reinforced)] // reinforcement follows validation, not capture
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Superseded)]
    [InlineData(ExperienceStatus.Quarantined, ExperienceStatus.Validated)] // a quarantine is reviewed, not reversed here
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Quarantined)] // quarantine is a capture-time decision
    [InlineData(ExperienceStatus.Reinforced, ExperienceStatus.Reinforced)] // reinforcing twice records no transition
    [InlineData(ExperienceStatus.Contested, ExperienceStatus.Validated)] // resolving a contest is not in the MVP table
    [InlineData(ExperienceStatus.Stale, ExperienceStatus.Superseded)]
    [InlineData(ExperienceStatus.Superseded, ExperienceStatus.Stale)]
    public async Task A_transition_outside_the_table_is_refused_by_Core_and_never_reaches_the_store(
        ExperienceStatus prior,
        ExperienceStatus current)
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);

        var result = await service.CommitAsync(Authorization, Request(prior, current), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.TransitionNotAllowed, result.Outcome);
        Assert.Empty(store.Commits);
        Assert.Empty(store.SupersessionChecks);
        Assert.Null(result.Event);
        Assert.Equal(0, result.Revision);
        Assert.Empty(result.Errors);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task An_event_whose_prior_and_current_status_are_the_same_is_refused_for_every_status()
    {
        foreach (var status in EveryStatus)
        {
            Assert.False(ExperienceLifecycleService.IsTransitionAllowed(status, status));

            var store = new RecordingStore();
            var result = await new ExperienceLifecycleService(store)
                .CommitAsync(Authorization, Request(status, status), CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.TransitionNotAllowed, result.Outcome);
            Assert.Empty(store.Commits);
            Assert.Contains("already", result.Reason!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every allowed (prior, current) pair, written out rather than derived, so this cannot silently
    /// agree with a changed implementation. 8 statuses x 8 statuses = 64 pairs; the 16 below are allowed
    /// and the other 48 are not.
    /// </summary>
    private static readonly HashSet<(ExperienceStatus Prior, ExperienceStatus Current)> AllowedPairs =
    [
        // Candidate -> Validated or Quarantined: the two outcomes finalization can reach.
        (ExperienceStatus.Candidate, ExperienceStatus.Validated),
        (ExperienceStatus.Candidate, ExperienceStatus.Quarantined),

        // Validated -> Reinforced: reuse was observed to succeed again.
        (ExperienceStatus.Validated, ExperienceStatus.Reinforced),

        // Validated or Reinforced -> Contested, Stale or Superseded: the three ways an eligible record
        // stops being eligible without being withdrawn outright.
        (ExperienceStatus.Validated, ExperienceStatus.Contested),
        (ExperienceStatus.Validated, ExperienceStatus.Stale),
        (ExperienceStatus.Validated, ExperienceStatus.Superseded),
        (ExperienceStatus.Reinforced, ExperienceStatus.Contested),
        (ExperienceStatus.Reinforced, ExperienceStatus.Stale),
        (ExperienceStatus.Reinforced, ExperienceStatus.Superseded),

        // Anything except Revoked -> Revoked. Revoked -> Revoked is excluded twice over: revocation is
        // terminal, and no event may leave a record where it already was.
        (ExperienceStatus.Candidate, ExperienceStatus.Revoked),
        (ExperienceStatus.Validated, ExperienceStatus.Revoked),
        (ExperienceStatus.Quarantined, ExperienceStatus.Revoked),
        (ExperienceStatus.Contested, ExperienceStatus.Revoked),
        (ExperienceStatus.Stale, ExperienceStatus.Revoked),
        (ExperienceStatus.Superseded, ExperienceStatus.Revoked),
        (ExperienceStatus.Reinforced, ExperienceStatus.Revoked),
    ];

    [Fact]
    public void The_allowed_table_is_exactly_the_enumerated_pairs()
    {
        Assert.Equal(16, AllowedPairs.Count);
        Assert.Equal(8, EveryStatus.Length);

        foreach (var prior in EveryStatus)
        {
            foreach (var current in EveryStatus)
            {
                Assert.Equal(
                    AllowedPairs.Contains((prior, current)),
                    ExperienceLifecycleService.IsTransitionAllowed(prior, current));
            }
        }

        // No pair in the table is a self-transition, and none starts from Revoked.
        Assert.DoesNotContain(AllowedPairs, pair => pair.Prior == pair.Current);
        Assert.DoesNotContain(AllowedPairs, pair => pair.Prior == ExperienceStatus.Revoked);
    }

    [Fact]
    public void Eligibility_is_the_retrieval_rule_asked_as_a_question()
    {
        Assert.True(ExperienceLifecycleService.IsEligible(ExperienceStatus.Validated));
        Assert.True(ExperienceLifecycleService.IsEligible(ExperienceStatus.Reinforced));

        foreach (var status in EveryStatus.Where(s => s is not (ExperienceStatus.Validated or ExperienceStatus.Reinforced)))
        {
            Assert.False(ExperienceLifecycleService.IsEligible(status));
        }
    }

    [Fact]
    public void An_undefined_status_is_never_an_allowed_transition_for_an_external_caller()
    {
        Assert.False(ExperienceLifecycleService.IsTransitionAllowed((ExperienceStatus)999, ExperienceStatus.Revoked));
        Assert.False(ExperienceLifecycleService.IsTransitionAllowed(ExperienceStatus.Candidate, (ExperienceStatus)999));
        Assert.False(ExperienceLifecycleService.IsTransitionAllowed((ExperienceStatus)998, (ExperienceStatus)999));
    }

    [Fact]
    public async Task The_stamped_event_carries_the_request_verbatim()
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);
        var request = Request(ExperienceStatus.Candidate, ExperienceStatus.Validated, 7);

        var result = await service.CommitAsync(Authorization, request, CancellationToken.None);

        var (scope, stamped) = Assert.Single(store.Commits);
        Assert.Same(request.Scope, scope);
        Assert.Equal(request.EventId, stamped.EventId);
        Assert.Equal(request.ExperienceId, stamped.ExperienceRecordId);
        Assert.Equal(request.PriorStatus, stamped.PriorStatus);
        Assert.Equal(request.CurrentStatus, stamped.CurrentStatus);
        Assert.Equal(request.Reason, stamped.Reason);
        Assert.Equal(request.Producer, stamped.Producer);
        Assert.Equal(request.OccurredAt, stamped.OccurredAt);
        Assert.Equal(request.ExpectedRevision, stamped.ExpectedRevision);
        Assert.Null(stamped.ReplacementExperienceId);
        Assert.Equal(stamped, result.Event);

        // Nothing is invented: the service never touches confidence, counters, or timestamps of its own.
        var replay = await service.CommitAsync(Authorization, request, CancellationToken.None);
        Assert.Equal(stamped, replay.Event);
    }

    [Fact]
    public async Task A_supersession_stamps_the_replacement_onto_the_event_and_lets_the_store_decide_it()
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);
        var replacement = Guid.NewGuid();
        var request = Request(ExperienceStatus.Validated, ExperienceStatus.Superseded, 4, replacement: replacement);

        var result = await service.CommitAsync(Authorization, request, CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(replacement, Assert.Single(store.Commits).Event.ReplacementExperienceId);
        Assert.Equal(replacement, result.Event!.ReplacementExperienceId);

        // Deliberately *not* pre-checked here. The rules that depend on stored state are decided inside
        // the commit transaction, which is what makes them atomic and what keeps a replay from being
        // re-validated against state that has moved on.
        Assert.Empty(store.SupersessionChecks);
    }

    [Fact]
    public async Task A_replay_of_a_committed_supersession_is_never_re_validated_by_Core()
    {
        // The store reports the original commit for an identical replay; Core must not have refused it
        // on the way in, however the replacement has moved since.
        var store = new RecordingStore
        {
            Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, 5, null, []),
        };
        var request = Request(ExperienceStatus.Validated, ExperienceStatus.Superseded, 4);

        var first = await new ExperienceLifecycleService(store).CommitAsync(Authorization, request, CancellationToken.None);
        var replay = await new ExperienceLifecycleService(store).CommitAsync(Authorization, request, CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, first.Outcome);
        Assert.Equal(LifecycleTransitionOutcome.Committed, replay.Outcome);
        Assert.Equal(5, replay.Revision);
        Assert.Equal(first.Event, replay.Event);
        Assert.Empty(store.SupersessionChecks);
    }

    [Fact]
    public async Task A_replacement_is_required_for_a_supersession_and_refused_for_anything_else()
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);

        var missing = await service.CommitAsync(
            Authorization,
            Request(ExperienceStatus.Validated, ExperienceStatus.Superseded) with { ReplacementExperienceId = null },
            CancellationToken.None);

        var empty = await service.CommitAsync(
            Authorization,
            Request(ExperienceStatus.Validated, ExperienceStatus.Superseded, replacement: Guid.Empty),
            CancellationToken.None);

        var uncalledFor = await service.CommitAsync(
            Authorization,
            Request(ExperienceStatus.Validated, ExperienceStatus.Stale) with { ReplacementExperienceId = Guid.NewGuid() },
            CancellationToken.None);

        Assert.All([missing, empty, uncalledFor], result =>
        {
            Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, result.Outcome);
            Assert.Null(result.Event);
            Assert.Equal(0, result.Revision);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        });

        // Nothing was written, and nothing was even asked of the store.
        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task A_record_cannot_replace_itself_and_the_store_is_never_asked()
    {
        var store = new RecordingStore();
        var id = Guid.NewGuid();

        var result = await new ExperienceLifecycleService(store).CommitAsync(
            Authorization,
            Request(ExperienceStatus.Validated, ExperienceStatus.Superseded, experienceId: id, replacement: id),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, result.Outcome);
        Assert.Contains("itself", result.Reason!, StringComparison.Ordinal);
        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task A_replacement_the_store_refuses_is_reported_with_the_reason_its_status_implies()
    {
        // Absent from the scope, ineligible, and on a closing chain are one store outcome with one
        // distinguishing fact -- the replacement's status, or its absence.
        var cases = new (ExperienceStatus? Status, string Fragment)[]
        {
            (null, "does not exist"),
            (ExperienceStatus.Stale, "Stale"),
            (ExperienceStatus.Quarantined, "Quarantined"),
            (ExperienceStatus.Revoked, "Revoked"),
            (ExperienceStatus.Validated, "cycle"),
            (ExperienceStatus.Reinforced, "cycle"),
        };

        foreach (var (status, fragment) in cases)
        {
            var store = new RecordingStore
            {
                Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.ReplacementNotAllowed, 0, status, []),
            };

            var result = await new ExperienceLifecycleService(store).CommitAsync(
                Authorization,
                Request(ExperienceStatus.Validated, ExperienceStatus.Superseded),
                CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.ReplacementNotAllowed, result.Outcome);
            Assert.Contains(fragment, result.Reason!, StringComparison.Ordinal);
            Assert.Null(result.Deindexing);
        }
    }

    [Fact]
    public async Task An_eligible_replacement_is_accepted()
    {
        var store = new RecordingStore();

        var result = await new ExperienceLifecycleService(store).CommitAsync(
            Authorization,
            Request(ExperienceStatus.Validated, ExperienceStatus.Superseded),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Single(store.Commits);
    }

    [Theory]
    [InlineData(ExperienceStoreOutcome.Committed, LifecycleTransitionOutcome.Committed)]
    [InlineData(ExperienceStoreOutcome.StaleRevision, LifecycleTransitionOutcome.StaleRevision)]
    [InlineData(ExperienceStoreOutcome.StatusMismatch, LifecycleTransitionOutcome.StatusMismatch)]
    [InlineData(ExperienceStoreOutcome.Conflict, LifecycleTransitionOutcome.Conflict)]
    [InlineData(ExperienceStoreOutcome.NotFound, LifecycleTransitionOutcome.NotFound)]
    [InlineData(ExperienceStoreOutcome.Denied, LifecycleTransitionOutcome.Denied)]
    [InlineData(ExperienceStoreOutcome.Invalid, LifecycleTransitionOutcome.Invalid)]
    [InlineData(ExperienceStoreOutcome.Deleted, LifecycleTransitionOutcome.Deleted)]
    public async Task The_store_outcome_is_surfaced_unchanged(ExperienceStoreOutcome stored, LifecycleTransitionOutcome expected)
    {
        var store = new RecordingStore
        {
            Result = new ExperienceLifecycleCommitResult(stored, 11, ExperienceStatus.Quarantined, [new StoreValidationError("Reason", "must not be empty or whitespace.")]),
        };
        var service = new ExperienceLifecycleService(store);

        var result = await service.CommitAsync(Authorization, Request(ExperienceStatus.Candidate, ExperienceStatus.Validated), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(11, result.Revision);
        Assert.Equal(ExperienceStatus.Quarantined, result.CurrentStatus);
        Assert.Equal("Reason", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public async Task An_undefined_status_is_left_to_the_store_to_report_as_Invalid_not_refused_as_a_transition()
    {
        var store = new RecordingStore
        {
            Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Invalid, 0, null, [new StoreValidationError("CurrentStatus", "is not a defined value.")]),
        };
        var service = new ExperienceLifecycleService(store);

        var result = await service.CommitAsync(Authorization, Request(ExperienceStatus.Candidate, (ExperienceStatus)999), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("CurrentStatus", Assert.Single(result.Errors).Path);
        Assert.Single(store.Commits);
    }

    [Fact]
    public async Task A_commit_against_an_erased_record_is_a_terminal_refusal_rather_than_a_store_failure()
    {
        // Since erasure shipped the store answers Deleted for a tombstone, and the port documents it as
        // a commit outcome. It must come back as a typed refusal, never as ExperienceStoreException --
        // which telemetry would count as an infrastructure failure -- and must not de-index anything.
        var index = new FakeEmbeddingIndex();
        var store = new RecordingStore { Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Deleted, 4, null, []) };

        var result = await new ExperienceLifecycleService(store, Indexing(index))
            .CommitAsync(Authorization, Request(ExperienceStatus.Validated, ExperienceStatus.Revoked, 3), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Deleted, result.Outcome);
        Assert.Equal(4, result.Revision);
        Assert.Null(result.Deindexing);
        Assert.Empty(index.Removals);
        Assert.Single(store.Commits);
    }

    [Fact]
    public async Task A_store_outcome_that_is_not_a_commit_outcome_is_never_reinterpreted()
    {
        var store = new RecordingStore { Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Created, 0, null, []) };
        var service = new ExperienceLifecycleService(store);

        await Assert.ThrowsAsync<ExperienceStoreException>(
            () => service.CommitAsync(Authorization, Request(ExperienceStatus.Candidate, ExperienceStatus.Validated), CancellationToken.None));
    }

    [Fact]
    public async Task Infrastructure_failures_and_cancellation_propagate_from_the_port()
    {
        var failing = new RecordingStore { Throw = () => new ExperienceStoreException("storage failed") };
        await Assert.ThrowsAsync<ExperienceStoreException>(
            () => new ExperienceLifecycleService(failing).CommitAsync(Authorization, Request(ExperienceStatus.Candidate, ExperienceStatus.Revoked), CancellationToken.None));

        var cancelling = new RecordingStore { Throw = () => new OperationCanceledException() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ExperienceLifecycleService(cancelling).CommitAsync(Authorization, Request(ExperienceStatus.Candidate, ExperienceStatus.Revoked), CancellationToken.None));
    }

    [Fact]
    public async Task A_null_prior_status_stamps_a_first_event_recording_the_record_as_a_Candidate()
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);

        // Candidate -> Candidate is not in the table, but with no prior status there is no transition to
        // look up: this is a record's first event, and it can only record where the record already is.
        var result = await service.CommitAsync(Authorization, Request(null, ExperienceStatus.Candidate), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Null(Assert.Single(store.Commits).Event.PriorStatus);
        Assert.Null(result.Event!.PriorStatus);
        Assert.Equal(ExperienceStatus.Candidate, ExperienceLifecycleService.FirstEventStatus);
    }

    [Fact]
    public async Task A_null_prior_status_is_not_a_way_around_the_transition_table()
    {
        // The hole this closes: without a prior status Core used to consult no table at all and the
        // store skipped its own guard, so omitting the prior status moved a record anywhere from
        // anywhere -- the exact thing the eight-status table exists to prevent.
        foreach (var current in EveryStatus.Where(s => s != ExperienceStatus.Candidate))
        {
            var store = new RecordingStore();

            var result = await new ExperienceLifecycleService(store)
                .CommitAsync(Authorization, Request(null, current), CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.TransitionNotAllowed, result.Outcome);
            Assert.Empty(store.Commits);
            Assert.Null(result.Event);
            Assert.Contains("first event", result.Reason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_undefined_status_with_no_prior_status_still_falls_through_to_the_store()
    {
        // A malformed request must come back as Invalid with a field path, never as a policy refusal.
        var store = new RecordingStore
        {
            Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Invalid, 0, null, [new StoreValidationError("CurrentStatus", "is not a defined value.")]),
        };

        var result = await new ExperienceLifecycleService(store)
            .CommitAsync(Authorization, Request(null, (ExperienceStatus)999), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Invalid, result.Outcome);
        Assert.Single(store.Commits);
    }

    [Fact]
    public void A_de_indexing_budget_beyond_the_cancellation_ceiling_is_refused_at_wiring_time()
    {
        // CancelAfter throws past int.MaxValue milliseconds, so an over-long budget has to fail here
        // rather than at the first commit that leaves eligibility.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceLifecycleService(
            new RecordingStore(),
            Indexing(new FakeEmbeddingIndex()),
            TimeSpan.FromMilliseconds(int.MaxValue + 1L)));

        Assert.Equal(
            TimeSpan.FromMilliseconds(int.MaxValue),
            new ExperienceLifecycleService(new RecordingStore(), Indexing(new FakeEmbeddingIndex()), TimeSpan.FromMilliseconds(int.MaxValue))
                .DeindexingTimeout);
    }

    [Fact]
    public async Task Null_arguments_throw_ArgumentNullException()
    {
        var service = new ExperienceLifecycleService(new RecordingStore());

        Assert.Throws<ArgumentNullException>(() => new ExperienceLifecycleService(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.CommitAsync(null!, Request(ExperienceStatus.Candidate, ExperienceStatus.Revoked), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CommitAsync(Authorization, null!, CancellationToken.None));

        // A null Scope is rejected here, not left to throw from inside the port.
        var noScope = Request(ExperienceStatus.Candidate, ExperienceStatus.Revoked) with { Scope = null! };
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CommitAsync(Authorization, noScope, CancellationToken.None));
    }

    [Fact]
    public void A_non_positive_deindexing_budget_is_refused_at_wiring_time()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExperienceLifecycleService(new RecordingStore(), Indexing(new FakeEmbeddingIndex()), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExperienceLifecycleService(new RecordingStore(), Indexing(new FakeEmbeddingIndex()), TimeSpan.FromSeconds(-1)));

        Assert.Equal(
            ExperienceLifecycleService.DefaultDeindexingTimeout,
            new ExperienceLifecycleService(new RecordingStore()).DeindexingTimeout);
    }

    [Theory]
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Revoked)]
    [InlineData(ExperienceStatus.Reinforced, ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Reinforced, ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Reinforced, ExperienceStatus.Revoked)]
    public async Task Leaving_eligibility_removes_the_record_s_embedding(ExperienceStatus prior, ExperienceStatus current)
    {
        var store = new RecordingStore();
        var index = new FakeEmbeddingIndex();
        var request = Request(prior, current);
        index.Stored[request.ExperienceId] = (new ExperienceEmbeddingDescriptor("fake-embed-v1", 4, "hash", 1), new float[4]);

        var result = await new ExperienceLifecycleService(store, Indexing(index)).CommitAsync(Authorization, request, CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Removed, result.Deindexing!.Outcome);
        Assert.True(result.Deindexing.IsRemoved);
        Assert.False(result.Deindexing.IsRetryable);
        Assert.Equal((request.Scope, request.ExperienceId), Assert.Single(index.Removals));
        Assert.Empty(index.Stored);
    }

    [Fact]
    public async Task A_supersession_removes_the_superseded_record_s_embedding_and_leaves_the_replacement_s_alone()
    {
        var store = new RecordingStore();
        var index = new FakeEmbeddingIndex();
        var replacement = Guid.NewGuid();
        var request = Request(ExperienceStatus.Validated, ExperienceStatus.Superseded, replacement: replacement);
        var descriptor = new ExperienceEmbeddingDescriptor("fake-embed-v1", 4, "hash", 1);
        index.Stored[request.ExperienceId] = (descriptor, new float[4]);
        index.Stored[replacement] = (descriptor, new float[4]);

        var result = await new ExperienceLifecycleService(store, Indexing(index)).CommitAsync(Authorization, request, CancellationToken.None);

        Assert.Equal(ExperienceDeindexingOutcome.Removed, result.Deindexing!.Outcome);
        Assert.Equal(replacement, Assert.Single(index.Stored).Key);
    }

    [Theory]
    // A move that stays eligible keeps the vector: nothing left eligibility.
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Reinforced)]
    // A record that was never eligible has nothing an eligible record could have left behind.
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Validated)]
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Quarantined)]
    [InlineData(ExperienceStatus.Quarantined, ExperienceStatus.Revoked)]
    [InlineData(ExperienceStatus.Contested, ExperienceStatus.Revoked)]
    public async Task A_transition_that_does_not_leave_eligibility_never_touches_the_index(
        ExperienceStatus prior,
        ExperienceStatus current)
    {
        var index = new FakeEmbeddingIndex();

        var result = await new ExperienceLifecycleService(new RecordingStore(), Indexing(index))
            .CommitAsync(Authorization, Request(prior, current), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Null(result.Deindexing);
        Assert.Empty(index.Removals);
    }

    [Fact]
    public async Task A_refused_or_rejected_commit_never_de_indexes()
    {
        var index = new FakeEmbeddingIndex();
        var store = new RecordingStore
        {
            Result = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, 9, null, []),
        };

        var rejected = await new ExperienceLifecycleService(store, Indexing(index))
            .CommitAsync(Authorization, Request(ExperienceStatus.Validated, ExperienceStatus.Stale), CancellationToken.None);

        var refused = await new ExperienceLifecycleService(new RecordingStore(), Indexing(index))
            .CommitAsync(Authorization, Request(ExperienceStatus.Stale, ExperienceStatus.Contested), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.StaleRevision, rejected.Outcome);
        Assert.Equal(LifecycleTransitionOutcome.TransitionNotAllowed, refused.Outcome);
        Assert.Null(rejected.Deindexing);
        Assert.Null(refused.Deindexing);
        Assert.Empty(index.Removals);
    }

    [Fact]
    public async Task A_record_that_was_never_embedded_is_reported_as_not_indexed_rather_than_a_failure()
    {
        var index = new FakeEmbeddingIndex();

        var result = await new ExperienceLifecycleService(new RecordingStore(), Indexing(index))
            .CommitAsync(Authorization, Request(ExperienceStatus.Validated, ExperienceStatus.Stale), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.NotIndexed, result.Deindexing!.Outcome);
        Assert.True(result.Deindexing.IsRemoved);
        Assert.Null(result.Deindexing.Failure);
    }

    [Fact]
    public async Task A_failed_removal_never_fails_the_transition_and_is_reported_as_retryable()
    {
        var index = new FakeEmbeddingIndex { RemoveThrows = FakeEmbeddingIndex.ThrownException };

        var result = await new ExperienceLifecycleService(new RecordingStore(), Indexing(index))
            .CommitAsync(Authorization, Request(ExperienceStatus.Validated, ExperienceStatus.Contested), CancellationToken.None);

        // The transition is a fact. Only the derived data failed.
        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Failed, result.Deindexing!.Outcome);
        Assert.True(result.Deindexing.IsRetryable);
        Assert.False(result.Deindexing.IsRemoved);
        Assert.Same(FakeEmbeddingIndex.ThrownException, result.Deindexing.Failure!.Exception);
    }

    [Fact]
    public async Task A_removal_that_outlasts_its_budget_is_abandoned_and_reported_retryable()
    {
        var index = new FakeEmbeddingIndex
        {
            // The hook's own linked token fires; the caller's token is untouched.
            BeforeRemove = token => token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),
        };

        var result = await new ExperienceLifecycleService(new RecordingStore(), Indexing(index), TimeSpan.FromMilliseconds(50))
            .CommitAsync(Authorization, Request(ExperienceStatus.Validated, ExperienceStatus.Stale), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Failed, result.Deindexing!.Outcome);
        Assert.True(result.Deindexing.IsRetryable);

        // The budget's own token is what ended it; the caller's was never cancelled.
        Assert.IsAssignableFrom<OperationCanceledException>(result.Deindexing.Failure!.Exception);
        Assert.Single(index.Removals);
    }

    [Fact]
    public async Task A_denied_removal_is_reported_and_still_leaves_the_transition_committed()
    {
        var index = new FakeEmbeddingIndex();
        var otherTenant = new AuthorizationContext("tenant-2", "principal", ["experience:write"], Now);
        var store = new RecordingStore();

        var result = await new ExperienceLifecycleService(store, Indexing(index))
            .CommitAsync(otherTenant, Request(ExperienceStatus.Validated, ExperienceStatus.Stale), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Denied, result.Deindexing!.Outcome);
        Assert.False(result.Deindexing.IsRetryable);
    }

    private static ExperienceIndexingService Indexing(FakeEmbeddingIndex index) =>
        new(index, new FakeEmbeddingGenerator());

    /// <summary>
    /// Records what the service handed the port, and answers with a configurable outcome. Every other
    /// port operation is out of this story's scope and fails loudly if the service ever calls it.
    /// </summary>
    private sealed class RecordingStore : IExperienceRecordStore
    {
        public List<(Scope Scope, LifecycleEvent Event)> Commits { get; } = [];

        public List<(Scope Scope, Guid ExperienceId, Guid ReplacementId)> SupersessionChecks { get; } = [];

        public ExperienceLifecycleCommitResult? Result { get; init; }

        public ExperienceSupersessionCheckResult? Supersession { get; init; }

        public Func<Exception>? Throw { get; init; }

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            Commits.Add((scope, lifecycleEvent));

            if (Throw is not null)
            {
                throw Throw();
            }

            return Task.FromResult(Result
                ?? new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, lifecycleEvent.ExpectedRevision + 1, null, []));
        }

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            SupersessionChecks.Add((scope, experienceId, replacementExperienceId));

            return Task.FromResult(Supersession
                ?? new ExperienceSupersessionCheckResult(ExperienceSupersessionOutcome.Allowed, ExperienceStatus.Validated, []));
        }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not create records.");

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not read records.");

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not query records.");

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not read history.");
    }
}
