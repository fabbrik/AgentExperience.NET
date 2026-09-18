using AgentExperience.Abstractions;

namespace AgentExperience.Core.Lifecycle;

/// <summary>
/// Core's lifecycle owner: it decides whether a requested transition is legal, stamps the
/// <see cref="LifecycleEvent"/> that records it, and hands that event to the
/// <see cref="IExperienceRecordStore"/> port to append atomically with the record's projection. The
/// adapter persists the decision as given; it never invents a transition, a status, or a score
/// (ARCHITECTURE-SPINE AD-6).
/// </summary>
/// <remarks>
/// <para>
/// This version allows only the minimal table the first durable lifecycle needs:
/// <see cref="ExperienceStatus.Candidate"/> to <see cref="ExperienceStatus.Validated"/>; any status
/// other than <see cref="ExperienceStatus.Revoked"/> to <see cref="ExperienceStatus.Quarantined"/>;
/// and any status to <see cref="ExperienceStatus.Revoked"/>. Anything else is refused here, with
/// <see cref="LifecycleTransitionOutcome.TransitionNotAllowed"/>, and never reaches the store.
/// Reinforcement, contest, staleness, and supersession are later stories. The table is a Core decision,
/// but it is not Core's only defence: the store matches the event's
/// <see cref="LifecycleEvent.PriorStatus"/> against the record's real status, so a caller that asserts a
/// prior status the record is not in gets <see cref="LifecycleTransitionOutcome.StatusMismatch"/> rather
/// than a committed forbidden transition.
/// </para>
/// <para>
/// Reading a record's history is deliberately not mirrored here. It is a plain scoped read with no
/// lifecycle decision in it, so it stays on the <see cref="IExperienceRecordStore.GetHistoryAsync"/>
/// port rather than becoming a pass-through this service would only forward.
/// </para>
/// <para>
/// Nothing here computes reuse confidence or counters, decides storage or risk policy, or orchestrates
/// finalization. A store outcome is surfaced one-to-one, so a database failure can never be reported as
/// a durable success: infrastructure failures throw <see cref="ExperienceStoreException"/> and caller
/// cancellation surfaces as an unwrapped <see cref="OperationCanceledException"/>, both straight from
/// the port.
/// </para>
/// </remarks>
public sealed class ExperienceLifecycleService
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly IExperienceRecordStore _store;

    /// <summary>Creates a lifecycle service over a record store.</summary>
    /// <param name="store">The port that persists events and projections atomically.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public ExperienceLifecycleService(IExperienceRecordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Determines whether this version of the lifecycle allows moving a record from
    /// <paramref name="priorStatus"/> to <paramref name="currentStatus"/>. An undefined enum value is
    /// never allowed, so an external caller cannot read this as permission to attempt one. (Inside
    /// <see cref="CommitAsync"/> an undefined value is instead passed through to the store, which
    /// reports it as <see cref="ExperienceStoreOutcome.Invalid"/> with a field path, so a malformed
    /// request is not reported as a policy refusal.)
    /// </summary>
    /// <param name="priorStatus">The status the transition starts from.</param>
    /// <param name="currentStatus">The status the transition moves to.</param>
    /// <returns><see langword="true"/> when the transition is in the allowed table.</returns>
    public static bool IsTransitionAllowed(ExperienceStatus priorStatus, ExperienceStatus currentStatus) =>
        Enum.IsDefined(priorStatus)
        && Enum.IsDefined(currentStatus)
        && ((priorStatus == ExperienceStatus.Candidate && currentStatus == ExperienceStatus.Validated)
            || (currentStatus == ExperienceStatus.Quarantined && priorStatus != ExperienceStatus.Revoked)
            || currentStatus == ExperienceStatus.Revoked);

    /// <summary>
    /// Validates the requested transition, stamps its <see cref="LifecycleEvent"/>, and commits it
    /// through the store.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do. Passed to the store unchanged.</param>
    /// <param name="request">The transition to commit.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The store's outcome, surfaced unchanged, or <see cref="LifecycleTransitionOutcome.TransitionNotAllowed"/> when Core refused before calling it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">Storage infrastructure failed. Lifecycle state is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<CommitLifecycleTransitionResult> CommitAsync(
        AuthorizationContext authorization,
        CommitLifecycleTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope, $"{nameof(request)}.{nameof(request.Scope)}");

        // A null PriorStatus is a record's first event: there is no starting status to look up, and the
        // store skips its status match too. Only a well-formed pair can be looked up in the table at
        // all; an undefined enum value is a malformed request, which the store reports with its field path.
        if (request.PriorStatus is { } priorStatus
            && Enum.IsDefined(priorStatus)
            && Enum.IsDefined(request.CurrentStatus)
            && !IsTransitionAllowed(priorStatus, request.CurrentStatus))
        {
            return new(
                LifecycleTransitionOutcome.TransitionNotAllowed,
                Event: null,
                Revision: 0,
                CurrentStatus: null,
                NoErrors,
                $"Moving a record from {priorStatus} to {request.CurrentStatus} is not an allowed transition.");
        }

        var lifecycleEvent = new LifecycleEvent(
            EventId: request.EventId,
            ExperienceRecordId: request.ExperienceId,
            PriorStatus: request.PriorStatus,
            CurrentStatus: request.CurrentStatus,
            Reason: request.Reason,
            Producer: request.Producer,
            OccurredAt: request.OccurredAt,
            ExpectedRevision: request.ExpectedRevision);

        var result = await _store
            .CommitLifecycleEventAsync(authorization, request.Scope, lifecycleEvent, cancellationToken)
            .ConfigureAwait(false);

        return new(ToTransitionOutcome(result.Outcome), lifecycleEvent, result.Revision, result.CurrentStatus, result.Errors, Reason: null);
    }

    /// <summary>
    /// Maps a store outcome to its lifecycle counterpart one-to-one. An outcome this operation cannot
    /// produce is a contract violation by the store, not something to silently reinterpret.
    /// </summary>
    private static LifecycleTransitionOutcome ToTransitionOutcome(ExperienceStoreOutcome outcome) => outcome switch
    {
        ExperienceStoreOutcome.Committed => LifecycleTransitionOutcome.Committed,
        ExperienceStoreOutcome.StaleRevision => LifecycleTransitionOutcome.StaleRevision,
        ExperienceStoreOutcome.StatusMismatch => LifecycleTransitionOutcome.StatusMismatch,
        ExperienceStoreOutcome.Conflict => LifecycleTransitionOutcome.Conflict,
        ExperienceStoreOutcome.NotFound => LifecycleTransitionOutcome.NotFound,
        ExperienceStoreOutcome.Denied => LifecycleTransitionOutcome.Denied,
        ExperienceStoreOutcome.Invalid => LifecycleTransitionOutcome.Invalid,
        _ => throw new ExperienceStoreException(
            $"The Experience Record store returned '{outcome}', which is not a lifecycle commit outcome."),
    };
}
