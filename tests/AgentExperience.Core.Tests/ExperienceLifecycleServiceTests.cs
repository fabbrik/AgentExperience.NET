using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Core owns which transitions are legal (ARCHITECTURE-SPINE AD-6). These tests pin the minimal table
/// this version allows -- Candidate to Validated, anything but Revoked to Quarantined, anything to
/// Revoked -- prove a transition outside it never reaches the store, and prove the store's outcome is
/// surfaced one-to-one rather than reinterpreted.
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
        Guid? eventId = null) => new(
            EventId: eventId ?? Guid.NewGuid(),
            ExperienceId: Guid.NewGuid(),
            Scope: TestScope,
            PriorStatus: prior,
            CurrentStatus: current,
            Reason: "verified evidence",
            Producer: "finalization",
            OccurredAt: Now,
            ExpectedRevision: expectedRevision);

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
        Assert.Single(store.Commits);
    }

    [Fact]
    public async Task Every_status_except_Revoked_may_be_quarantined()
    {
        foreach (var prior in EveryStatus.Where(s => s != ExperienceStatus.Revoked))
        {
            var store = new RecordingStore();
            var service = new ExperienceLifecycleService(store);

            var result = await service.CommitAsync(Authorization, Request(prior, ExperienceStatus.Quarantined), CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
            Assert.Equal(prior, Assert.Single(store.Commits).Event.PriorStatus);
        }
    }

    [Fact]
    public async Task Every_status_may_be_revoked()
    {
        foreach (var prior in EveryStatus)
        {
            var store = new RecordingStore();
            var service = new ExperienceLifecycleService(store);

            var result = await service.CommitAsync(Authorization, Request(prior, ExperienceStatus.Revoked), CancellationToken.None);

            Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
            Assert.Equal(ExperienceStatus.Revoked, Assert.Single(store.Commits).Event.CurrentStatus);
        }
    }

    [Theory]
    [InlineData(ExperienceStatus.Revoked, ExperienceStatus.Quarantined)] // revocation is terminal except for re-revocation
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Candidate)] // no walking a record back to candidate
    [InlineData(ExperienceStatus.Candidate, ExperienceStatus.Reinforced)] // reinforcement is Epic 3
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Validated, ExperienceStatus.Superseded)]
    [InlineData(ExperienceStatus.Quarantined, ExperienceStatus.Validated)]
    public async Task A_transition_outside_the_table_is_refused_by_Core_and_never_reaches_the_store(
        ExperienceStatus prior,
        ExperienceStatus current)
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);

        var result = await service.CommitAsync(Authorization, Request(prior, current), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.TransitionNotAllowed, result.Outcome);
        Assert.Empty(store.Commits);
        Assert.Null(result.Event);
        Assert.Equal(0, result.Revision);
        Assert.Empty(result.Errors);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    /// <summary>
    /// Every allowed (prior, current) pair, written out rather than derived, so this cannot silently
    /// agree with a changed implementation. 8 statuses x 8 statuses = 64 pairs; the 16 below are allowed
    /// and the other 48 are not.
    /// </summary>
    private static readonly HashSet<(ExperienceStatus Prior, ExperienceStatus Current)> AllowedPairs =
    [
        // Candidate -> Validated (the only promotion this version allows).
        (ExperienceStatus.Candidate, ExperienceStatus.Validated),

        // Anything except Revoked -> Quarantined.
        (ExperienceStatus.Candidate, ExperienceStatus.Quarantined),
        (ExperienceStatus.Validated, ExperienceStatus.Quarantined),
        (ExperienceStatus.Quarantined, ExperienceStatus.Quarantined),
        (ExperienceStatus.Contested, ExperienceStatus.Quarantined),
        (ExperienceStatus.Stale, ExperienceStatus.Quarantined),
        (ExperienceStatus.Superseded, ExperienceStatus.Quarantined),
        (ExperienceStatus.Reinforced, ExperienceStatus.Quarantined),

        // Anything -> Revoked.
        (ExperienceStatus.Candidate, ExperienceStatus.Revoked),
        (ExperienceStatus.Validated, ExperienceStatus.Revoked),
        (ExperienceStatus.Quarantined, ExperienceStatus.Revoked),
        (ExperienceStatus.Contested, ExperienceStatus.Revoked),
        (ExperienceStatus.Stale, ExperienceStatus.Revoked),
        (ExperienceStatus.Superseded, ExperienceStatus.Revoked),
        (ExperienceStatus.Revoked, ExperienceStatus.Revoked),
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
        Assert.Equal(stamped, result.Event);

        // Nothing is invented: the service never touches confidence, counters, or timestamps of its own.
        var replay = await service.CommitAsync(Authorization, request, CancellationToken.None);
        Assert.Equal(stamped, replay.Event);
    }

    [Theory]
    [InlineData(ExperienceStoreOutcome.Committed, LifecycleTransitionOutcome.Committed)]
    [InlineData(ExperienceStoreOutcome.StaleRevision, LifecycleTransitionOutcome.StaleRevision)]
    [InlineData(ExperienceStoreOutcome.StatusMismatch, LifecycleTransitionOutcome.StatusMismatch)]
    [InlineData(ExperienceStoreOutcome.Conflict, LifecycleTransitionOutcome.Conflict)]
    [InlineData(ExperienceStoreOutcome.NotFound, LifecycleTransitionOutcome.NotFound)]
    [InlineData(ExperienceStoreOutcome.Denied, LifecycleTransitionOutcome.Denied)]
    [InlineData(ExperienceStoreOutcome.Invalid, LifecycleTransitionOutcome.Invalid)]
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
    public async Task A_null_prior_status_stamps_a_first_event_and_skips_the_transition_table()
    {
        var store = new RecordingStore();
        var service = new ExperienceLifecycleService(store);

        // Candidate -> Candidate is not in the table, but with no prior status there is no transition to
        // look up: this is a record's first event, and the store skips its status match too.
        var result = await service.CommitAsync(Authorization, Request(null, ExperienceStatus.Candidate), CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Null(Assert.Single(store.Commits).Event.PriorStatus);
        Assert.Null(result.Event!.PriorStatus);
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

    /// <summary>
    /// Records what the service handed the port, and answers with a configurable outcome. Every other
    /// port operation is out of this story's scope and fails loudly if the service ever calls it.
    /// </summary>
    private sealed class RecordingStore : IExperienceRecordStore
    {
        public List<(Scope Scope, LifecycleEvent Event)> Commits { get; } = [];

        public ExperienceLifecycleCommitResult? Result { get; init; }

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

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not create records.");

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not read records.");

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not query records.");

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not read history.");
    }
}
