using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Retrieval;

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
/// <b>The transition table.</b> The accepted moves are exactly:
/// <see cref="ExperienceStatus.Candidate"/> to <see cref="ExperienceStatus.Validated"/> or
/// <see cref="ExperienceStatus.Quarantined"/>; <see cref="ExperienceStatus.Validated"/> to
/// <see cref="ExperienceStatus.Reinforced"/>; <see cref="ExperienceStatus.Validated"/> or
/// <see cref="ExperienceStatus.Reinforced"/> to <see cref="ExperienceStatus.Contested"/>,
/// <see cref="ExperienceStatus.Stale"/> or <see cref="ExperienceStatus.Superseded"/>; and any status
/// except <see cref="ExperienceStatus.Revoked"/> to <see cref="ExperienceStatus.Revoked"/>. Everything
/// else -- including a move to the status the record is already in -- is refused here, with
/// <see cref="LifecycleTransitionOutcome.TransitionNotAllowed"/>, and never reaches the store. The
/// table is a Core decision, but it is not Core's only defence: the store matches the event's
/// <see cref="LifecycleEvent.PriorStatus"/> against the record's real status, so a caller that asserts
/// a prior status the record is not in gets
/// <see cref="LifecycleTransitionOutcome.StatusMismatch"/> rather than a committed forbidden
/// transition.
/// </para>
/// <para>
/// <b>Supersession names a replacement.</b> A move to <see cref="ExperienceStatus.Superseded"/> must
/// carry <see cref="CommitLifecycleTransitionRequest.ReplacementExperienceId"/>, and every other move
/// must not. The replacement has to be a different record, in the record's exact scope, currently
/// eligible, and not already replaced by this record directly or transitively -- the last of which is
/// a walk over the stored replacement chain, so it is asked of the store
/// (<see cref="IExperienceRecordStore.CheckSupersessionAsync"/>) rather than guessed at here. All of
/// it happens before the event is stamped, so a cycle is refused with nothing written.
/// </para>
/// <para>
/// <b>Leaving eligibility drops the embedding -- as hygiene, not as a boundary.</b> When a commit
/// moves a record out of <see cref="ExperienceRetrievalService.EligibleStatuses"/> and an
/// <see cref="ExperienceIndexingService"/> is wired in, the record's stored vector is removed after
/// the fact, outside the canonical transaction and bounded by <see cref="DeindexingTimeout"/>. Be
/// clear about what that buys: a vector search joins the canonical record and filters on its status,
/// so a surviving vector is <em>already</em> unreachable the moment the transition commits. Removal
/// reclaims storage and index maintenance cost, and keeps a re-index pass from having to reason about
/// rows nothing can return -- it is not what makes an ineligible record uninjectable. Nothing about
/// reuse depends on it, which is why it can never change the outcome and why a failure is only ever
/// reported.
/// </para>
/// <para>
/// <b>Reconciling a removal that did not happen is the host's job.</b> There is deliberately no
/// background sweep here: <see cref="ExperienceIndexingService.ReindexAsync"/> lists only records a
/// search could return and never removes anything, so nothing retries a failed removal on its own.
/// A host that cares about the reclaimed storage should treat a
/// <see cref="CommitLifecycleTransitionResult.Deindexing"/> outcome other than
/// <see cref="ExperienceDeindexingOutcome.Removed"/> and
/// <see cref="ExperienceDeindexingOutcome.NotIndexed"/> as a work item -- log the record ID and scope,
/// and call <see cref="ExperienceIndexingService.RemoveAsync"/> again later. That applies to
/// <see cref="ExperienceDeindexingOutcome.Denied"/> too, which
/// <see cref="ExperienceDeindexingResult.IsRetryable"/> reports as <see langword="false"/> because
/// repeating the same call changes nothing: it needs a different authorization, not another attempt.
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
    /// <summary>
    /// The only status a record's <em>first</em> lifecycle event -- the one with no prior status -- may
    /// record. A record is created as a <see cref="ExperienceStatus.Candidate"/>, so its first event can
    /// only say so; anything else would be a transition, and a transition has to name what it moved from.
    /// </summary>
    public const ExperienceStatus FirstEventStatus = ExperienceStatus.Candidate;

    /// <summary>
    /// The default budget for the post-commit de-indexing hook, after which it is abandoned and
    /// reported as retryable. The transition is already durable when the hook starts, so this bounds
    /// nothing but the caller's wait.
    /// </summary>
    public static readonly TimeSpan DefaultDeindexingTimeout = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly IExperienceRecordStore _store;
    private readonly ExperienceIndexingService? _indexingService;

    /// <summary>Creates a lifecycle service over a record store, with no de-indexing hook.</summary>
    /// <param name="store">The port that persists events and projections atomically.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public ExperienceLifecycleService(IExperienceRecordStore store)
        : this(store, indexingService: null)
    {
    }

    /// <summary>
    /// Creates a lifecycle service with an optional post-commit de-indexing hook.
    /// </summary>
    /// <remarks>
    /// The hook runs only after a transition this call actually committed, only when that transition
    /// moved the record out of eligibility, and it can never fail the transition: every outcome it
    /// reaches, including a cancellation, is reported on the result and nothing more. See
    /// <see cref="CommitLifecycleTransitionResult.Deindexing"/>.
    /// </remarks>
    /// <param name="store">The port that persists events and projections atomically.</param>
    /// <param name="indexingService">Optional. Removes the record's stored vector once it leaves eligibility.</param>
    /// <param name="deindexingTimeout">Optional. How long that hook may take before it is abandoned and reported as retryable. Must be strictly positive. Defaults to <see cref="DefaultDeindexingTimeout"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deindexingTimeout"/> is not strictly positive.</exception>
    public ExperienceLifecycleService(
        IExperienceRecordStore store,
        ExperienceIndexingService? indexingService,
        TimeSpan? deindexingTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _indexingService = indexingService;
        DeindexingTimeout = deindexingTimeout ?? DefaultDeindexingTimeout;

        if (DeindexingTimeout <= TimeSpan.Zero || DeindexingTimeout.TotalMilliseconds > int.MaxValue)
        {
            // The upper bound is not cosmetic: CancellationTokenSource.CancelAfter throws for anything
            // past int.MaxValue milliseconds, so an over-long budget would fail at the first commit that
            // left eligibility rather than here, at wiring time.
            throw new ArgumentOutOfRangeException(
                nameof(deindexingTimeout),
                DeindexingTimeout,
                $"The de-indexing budget must be strictly positive and at most {int.MaxValue} milliseconds; " +
                "an unbounded hook is what this exists to prevent.");
        }
    }

    /// <summary>The budget this service gives the post-commit de-indexing hook.</summary>
    public TimeSpan DeindexingTimeout { get; }

    /// <summary>
    /// Determines whether the lifecycle allows moving a record from <paramref name="priorStatus"/> to
    /// <paramref name="currentStatus"/>. An undefined enum value is never allowed, so an external
    /// caller cannot read this as permission to attempt one. (Inside <see cref="CommitAsync"/> an
    /// undefined value is instead passed through to the store, which reports it as
    /// <see cref="ExperienceStoreOutcome.Invalid"/> with a field path, so a malformed request is not
    /// reported as a policy refusal.)
    /// </summary>
    /// <remarks>
    /// A move to the status the record is already in is never allowed, whichever status it is: an event
    /// that changes nothing would still consume a revision and sit in the audit trail claiming a
    /// transition that did not happen. That rule is stated on its own line below rather than left to
    /// fall out of the table, so it cannot be lost when the table changes.
    /// </remarks>
    /// <param name="priorStatus">The status the transition starts from.</param>
    /// <param name="currentStatus">The status the transition moves to.</param>
    /// <returns><see langword="true"/> when the transition is in the allowed table.</returns>
    public static bool IsTransitionAllowed(ExperienceStatus priorStatus, ExperienceStatus currentStatus) =>
        Enum.IsDefined(priorStatus)
        && Enum.IsDefined(currentStatus)
        && priorStatus != currentStatus
        && ((priorStatus == ExperienceStatus.Candidate
                && currentStatus is ExperienceStatus.Validated or ExperienceStatus.Quarantined)
            || (priorStatus == ExperienceStatus.Validated && currentStatus == ExperienceStatus.Reinforced)
            || (priorStatus is ExperienceStatus.Validated or ExperienceStatus.Reinforced
                && currentStatus is ExperienceStatus.Contested or ExperienceStatus.Stale or ExperienceStatus.Superseded)
            || (currentStatus == ExperienceStatus.Revoked && priorStatus != ExperienceStatus.Revoked));

    /// <summary>
    /// Whether a record in <paramref name="status"/> may still be retrieved, injected, or indexed.
    /// It is <see cref="ExperienceRetrievalService.EligibleStatuses"/>, asked as a question, so the
    /// de-indexing rule and the retrieval rule can never drift apart.
    /// </summary>
    /// <param name="status">The status to test.</param>
    /// <returns><see langword="true"/> when a record in this status is eligible.</returns>
    public static bool IsEligible(ExperienceStatus status) => ExperienceStatuses.IsEligibleForReuse(status);

    /// <summary>
    /// Validates the requested transition, stamps its <see cref="LifecycleEvent"/>, and commits it
    /// through the store.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do. Passed to the store unchanged.</param>
    /// <param name="request">The transition to commit.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The store's outcome, surfaced unchanged, or the refusal Core reached before calling it.</returns>
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

        // Only a well-formed status can be judged here at all; an undefined enum value is a malformed
        // request, which the store reports as Invalid with a field path rather than as a policy refusal.
        if (Enum.IsDefined(request.CurrentStatus))
        {
            if (request.PriorStatus is { } priorStatus)
            {
                if (Enum.IsDefined(priorStatus) && !IsTransitionAllowed(priorStatus, request.CurrentStatus))
                {
                    return Refused(
                        LifecycleTransitionOutcome.TransitionNotAllowed,
                        priorStatus == request.CurrentStatus
                            ? $"A record is already {request.CurrentStatus}; an event whose prior and current status are the same records no transition."
                            : $"Moving a record from {priorStatus} to {request.CurrentStatus} is not an allowed transition.");
                }
            }
            else if (request.CurrentStatus != FirstEventStatus)
            {
                // A null prior status is a record's *first* event and nothing else. Left unrestricted it
                // was a hole straight through the transition table: omit the prior status and a record
                // could be moved from any status to any other, which is the one thing the table exists
                // to stop. The store's own guard refuses it too, so neither layer stands alone.
                return Refused(
                    LifecycleTransitionOutcome.TransitionNotAllowed,
                    $"A record's first event has no prior status, so it may only record the record as {FirstEventStatus}; " +
                    $"moving it to {request.CurrentStatus} needs the status it is moving from.");
            }
        }

        var replacementRefusal = ValidateReplacementShape(request);
        if (replacementRefusal is not null)
        {
            return replacementRefusal;
        }

        var lifecycleEvent = new LifecycleEvent(
            EventId: request.EventId,
            ExperienceRecordId: request.ExperienceId,
            PriorStatus: request.PriorStatus,
            CurrentStatus: request.CurrentStatus,
            Reason: request.Reason,
            Producer: request.Producer,
            OccurredAt: request.OccurredAt,
            ExpectedRevision: request.ExpectedRevision,
            ReplacementExperienceId: request.ReplacementExperienceId);

        var result = await _store
            .CommitLifecycleEventAsync(authorization, request.Scope, lifecycleEvent, cancellationToken)
            .ConfigureAwait(false);

        var outcome = ToTransitionOutcome(result.Outcome);

        // Only after the transition is durable, and only when it actually left eligibility.
        var deindexing = outcome == LifecycleTransitionOutcome.Committed
            ? await TryRemoveEmbeddingAsync(authorization, request, cancellationToken).ConfigureAwait(false)
            : null;

        var reason = outcome == LifecycleTransitionOutcome.ReplacementNotAllowed
            ? ReplacementRefusalReason(result.CurrentStatus)
            : null;

        return new(outcome, lifecycleEvent, result.Revision, result.CurrentStatus, result.Errors, reason, deindexing);
    }

    /// <summary>
    /// Turns the store's in-transaction refusal into the sentence a caller can act on, from the one fact
    /// it reports: the replacement's stored status, or its absence.
    /// </summary>
    private static string ReplacementRefusalReason(ExperienceStatus? replacementStatus) => replacementStatus switch
    {
        null => "The named replacement does not exist within the record's exact scope.",
        { } status when !IsEligible(status) => string.Format(
            CultureInfo.InvariantCulture,
            "The named replacement is {0}, so it is not currently eligible for reuse and cannot replace anything.",
            status),
        _ => "The named replacement is already replaced by this record, directly or transitively, so superseding would close a cycle.",
    };

    /// <summary>
    /// Decides the two replacement rules that need no storage, and returns the refusal when they do not
    /// hold. <see langword="null"/> means the request may go to the store, which decides the rest.
    /// </summary>
    /// <remarks>
    /// Only the rules whose answer cannot change are settled here: a replacement is named exactly when
    /// the transition is a supersession, and it is not the record itself. Everything that depends on
    /// stored state -- both records being in this exact scope, the replacement being eligible, and the
    /// replacement not already sitting on a chain back to this record -- is decided by the store
    /// <em>inside the commit transaction</em>, with both record rows locked. Deciding those here first
    /// would be two mistakes at once: the answer could go stale between the check and the write (two
    /// supersessions naming each other would each pass and both commit a cycle), and the check would run
    /// ahead of replay detection, so retrying a committed supersession would be refused once the
    /// replacement had itself moved on. <see cref="IExperienceRecordStore.CheckSupersessionAsync"/> is
    /// still there for a caller that wants to know before it tries.
    /// </remarks>
    private static CommitLifecycleTransitionResult? ValidateReplacementShape(CommitLifecycleTransitionRequest request)
    {
        if (request.CurrentStatus != ExperienceStatus.Superseded)
        {
            return request.ReplacementExperienceId is null
                ? null
                : Refused(
                    LifecycleTransitionOutcome.ReplacementNotAllowed,
                    $"Only a transition to {ExperienceStatus.Superseded} names a replacement; this one moves the record to {request.CurrentStatus}.");
        }

        if (request.ReplacementExperienceId is not { } replacementId || replacementId == Guid.Empty)
        {
            return Refused(
                LifecycleTransitionOutcome.ReplacementNotAllowed,
                $"A transition to {ExperienceStatus.Superseded} must name the record that replaces this one.");
        }

        if (replacementId == request.ExperienceId)
        {
            // Refused before the store is called at all: a record cannot replace itself, and asking the
            // database would be asking a question whose answer cannot change.
            return Refused(
                LifecycleTransitionOutcome.ReplacementNotAllowed,
                "A record cannot replace itself.");
        }

        return null;
    }

    /// <summary>
    /// Removes the record's embedding when this commit took it out of eligibility, and swallows
    /// everything that can go wrong doing so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transition is already committed and durable when this runs. Embeddings are derived data, so
    /// failing the transition because a vector survived would be reporting a falsehood about the
    /// canonical record -- and would leave the caller believing the record is still eligible when the
    /// text channel already excludes it by status. Every failure is therefore reported as a retryable
    /// <see cref="ExperienceDeindexingResult"/>, cancellation included.
    /// </para>
    /// <para>
    /// A record whose <see cref="CommitLifecycleTransitionRequest.PriorStatus"/> was not eligible in
    /// the first place is skipped entirely: there is nothing an eligible record could have left behind.
    /// A first event (null prior status) is skipped for the same reason -- a record with no lifecycle
    /// history has never been eligible.
    /// </para>
    /// </remarks>
    private async Task<ExperienceDeindexingResult?> TryRemoveEmbeddingAsync(
        AuthorizationContext authorization,
        CommitLifecycleTransitionRequest request,
        CancellationToken cancellationToken)
    {
        if (_indexingService is null
            || request.PriorStatus is not { } priorStatus
            || !IsEligible(priorStatus)
            || IsEligible(request.CurrentStatus))
        {
            return null;
        }

        // Bounded, and on its own budget: derived data never holds the caller open after the canonical
        // work is done.
        using var deindexing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deindexing.CancelAfter(DeindexingTimeout);

        try
        {
            return await _indexingService
                .RemoveAsync(authorization, request.Scope, request.ExperienceId, deindexing.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Including cancellation, which reaches here only if a future hook throws before
            // ExperienceIndexingService.RemoveAsync's own try -- RemoveAsync reports its own
            // cancellation rather than throwing it, so there is no separate branch for a timeout.

            return new(
                ExperienceDeindexingOutcome.Failed,
                request.ExperienceId,
                new ExperienceIndexingFailure(
                    $"The de-indexing hook threw {ex.GetType().FullName} after the transition was already committed; " +
                    "the record is ineligible and the text channel already excludes it, and the vector can be removed later.",
                    NoErrors,
                    ex));
        }
    }

    private static CommitLifecycleTransitionResult Refused(LifecycleTransitionOutcome outcome, string? reason) => new(
        outcome,
        Event: null,
        Revision: 0,
        CurrentStatus: null,
        NoErrors,
        reason);

    /// <summary>
    /// Maps a store outcome to its lifecycle counterpart one-to-one. An outcome this operation cannot
    /// produce is a contract violation by the store, not something to silently reinterpret.
    /// </summary>
    private static LifecycleTransitionOutcome ToTransitionOutcome(ExperienceStoreOutcome outcome) => outcome switch
    {
        ExperienceStoreOutcome.Committed => LifecycleTransitionOutcome.Committed,
        ExperienceStoreOutcome.StaleRevision => LifecycleTransitionOutcome.StaleRevision,
        ExperienceStoreOutcome.StatusMismatch => LifecycleTransitionOutcome.StatusMismatch,
        ExperienceStoreOutcome.ReplacementNotAllowed => LifecycleTransitionOutcome.ReplacementNotAllowed,
        ExperienceStoreOutcome.Conflict => LifecycleTransitionOutcome.Conflict,
        ExperienceStoreOutcome.NotFound => LifecycleTransitionOutcome.NotFound,
        ExperienceStoreOutcome.Denied => LifecycleTransitionOutcome.Denied,
        ExperienceStoreOutcome.Invalid => LifecycleTransitionOutcome.Invalid,
        _ => throw new ExperienceStoreException(
            $"The Experience Record store returned '{outcome}', which is not a lifecycle commit outcome."),
    };
}
