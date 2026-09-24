using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Diagnostics;
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
/// <b>Confidence moves only through <see cref="ApplyEvidenceAsync"/>.</b> That is the one entry point
/// that touches <see cref="ExperienceRecord.ReuseConfidence"/> and the counters behind it, and it owns
/// the arithmetic outright: it reads the record, computes the new counters and the new score with
/// <see cref="ReuseConfidenceHeuristic"/>, and submits them on the lifecycle event with the revision it
/// read. The adapter writes those numbers and never derives any. A contradiction moves a live record to
/// <see cref="ExperienceStatus.Contested"/> in the same transaction; supporting evidence never moves a
/// status by itself. <see cref="CommitAsync"/> carries no confidence payload and changes no counter, so
/// the ordinary transition table is unchanged by any of this.
/// </para>
/// <para>
/// Nothing here decides storage or risk policy, or orchestrates finalization. A store outcome is
/// surfaced one-to-one, so a database failure can never be reported as a durable success:
/// infrastructure failures throw <see cref="ExperienceStoreException"/> and caller cancellation
/// surfaces as an unwrapped <see cref="OperationCanceledException"/>, both straight from the port.
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

    /// <summary>
    /// How the confidence path reads a record: to check it against the caller's own scope, never to
    /// hand it over. A grant confers reading one record and never writing to it, so a record only a
    /// grant made readable is refused a few lines later -- which makes the read a scope check rather
    /// than a delivery, and means no access row should claim otherwise.
    /// </summary>
    private static readonly ExperienceReadOptions ScopeCheckRead = new(ExperienceReadPurpose.ScopeCheck);

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
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.LifecycleCommit, cancellationToken);

        CommitLifecycleTransitionResult result;
        try
        {
            // Both identifiers are in hand before the call, so a commit that threw still says which
            // record and which event it was committing -- which is exactly the span an operator opens
            // first. The argument check is restated ahead of the tag so that a null request is still
            // the ArgumentNullException the body would have thrown.
            ArgumentNullException.ThrowIfNull(request);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.ExperienceIdAttribute, request.ExperienceId.ToString("D"));
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.EventIdAttribute, request.EventId.ToString("D"));

            result = await CommitCoreAsync(authorization, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.LifecycleCommit, ex);
            throw;
        }

        // Outside the guarded region on purpose. By this line the transition is durable, and frozen
        // rule 6 says a throw from a tag expression or a metric write must never report it as failed.
        // A refused transition is a decision, not a failure: the span stays Ok and only the outcome
        // says the record did not move.
        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.LifecycleCommit, result.Outcome.ToString());
        return result;
    }

    /// <summary>
    /// The body of <see cref="CommitAsync"/>, unchanged by instrumentation: it neither reads nor writes
    /// a span. It exists so that the wrapper's own tagging and metric writes sit outside the region that
    /// guards the call, and it is <see langword="private"/> because every caller -- the finalization
    /// service, which commits a record's initial lifecycle event, included -- goes through the
    /// instrumented entry point and is counted there as a nested operation.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="request">The transition to commit.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The store's outcome, surfaced unchanged, or the refusal Core reached before calling it.</returns>
    private async Task<CommitLifecycleTransitionResult> CommitCoreAsync(
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
            ? await TryRemoveEmbeddingAsync(
                authorization, request.Scope, request.ExperienceId, request.PriorStatus, request.CurrentStatus, cancellationToken)
                .ConfigureAwait(false)
            : null;

        var reason = outcome == LifecycleTransitionOutcome.ReplacementNotAllowed
            ? ReplacementRefusalReason(result.CurrentStatus)
            : null;

        return new(outcome, lifecycleEvent, result.Revision, result.CurrentStatus, result.Errors, reason, deindexing);
    }

    /// <summary>
    /// Applies one piece of evidence about a stored lesson having been reused: reads the record,
    /// computes its new counters and score from what it read, and commits the evidence, the counters,
    /// the score, any status change, and the lifecycle event in one store transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Core owns the arithmetic.</b> The new counters and the new score are computed here, by
    /// <see cref="ReuseConfidenceHeuristic"/>, from the record this call read, and are submitted with
    /// <em>that</em> record's revision. The store writes those numbers and enforces two rules only it
    /// can: that the revision has not moved, and that this submission's independence key has not already
    /// been counted. Nothing downstream derives a score.
    /// </para>
    /// <para>
    /// <b>A duplicate is accepted, recorded, and counted zero times.</b> Resubmitting the same
    /// observation under a fresh <see cref="ApplyConfidenceEvidenceRequest.EvidenceId"/> is
    /// <see cref="ConfidenceUpdateOutcome.Applied"/> with
    /// <see cref="ApplyConfidenceEvidenceResult.Counted"/> <see langword="false"/>: the submission is in
    /// the audit trail and the counters did not move. Resubmitting the same <em>evidence ID</em> with
    /// identical content reports the original outcome; with different content it is
    /// <see cref="ConfidenceUpdateOutcome.Conflict"/> and nothing is written.
    /// </para>
    /// <para>
    /// <b>Status, not score, decides reuse.</b> A contradiction against a
    /// <see cref="ExperienceStatus.Validated"/> or <see cref="ExperienceStatus.Reinforced"/> record
    /// moves it to <see cref="ExperienceStatus.Contested"/> in the same transaction, and a record
    /// already <see cref="ExperienceStatus.Contested"/> stays there while its counters keep moving.
    /// Supporting evidence never changes a status. A record in any other status refuses the evidence
    /// outright (<see cref="ConfidenceUpdateOutcome.Ineligible"/>), so a score can never be used to
    /// argue a withdrawn, quarantined, stale, or superseded record back into reuse.
    /// </para>
    /// <para>
    /// <b>The reviewer is the host's, never the caller's.</b> For
    /// <see cref="ConfidenceEvidenceSource.Human"/> evidence the reviewer identity is
    /// <see cref="AuthorizationContext.PrincipalId"/>. The request has no field for it, because the
    /// count of distinct human reviewers is exactly what the independence rule protects.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do. Also the source of the reviewer identity for human evidence.</param>
    /// <param name="request">The evidence to apply.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, and the confidence movement as the transaction stored it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">Storage infrastructure failed. Lifecycle state and counters are unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ApplyConfidenceEvidenceResult> ApplyEvidenceAsync(
        AuthorizationContext authorization,
        ApplyConfidenceEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.ConfidenceApply, cancellationToken);

        ApplyConfidenceEvidenceResult result;
        try
        {
            // Request-derived, so tagged before the call and present on a faulted span too.
            ArgumentNullException.ThrowIfNull(request);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.ExperienceIdAttribute, request.ExperienceId.ToString("D"));
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.EventIdAttribute, request.EventId.ToString("D"));

            result = await ApplyEvidenceCoreAsync(authorization, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.ConfidenceApply, ex);
            throw;
        }

        // The evidence's own Detail is not written here: it is content-free by contract, but it is
        // also of no use to an operator, and the fewer free-form values a span carries the less
        // there is for a future change to get wrong.
        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.ConfidenceApply, result.Outcome.ToString());
        return result;
    }

    /// <summary>
    /// The body of <see cref="ApplyEvidenceAsync"/>, unchanged by instrumentation: it neither reads nor
    /// writes a span. It exists so that the wrapper's own tagging and metric writes sit outside the
    /// region that guards the call, and it is <see langword="private"/> because every caller -- the
    /// reuse-feedback service, which applies one piece of evidence per exposed record, included --
    /// goes through the instrumented entry point.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="request">The evidence to apply.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, and the confidence movement as the transaction stored it.</returns>
    private async Task<ApplyConfidenceEvidenceResult> ApplyEvidenceCoreAsync(
        AuthorizationContext authorization,
        ApplyConfidenceEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope, $"{nameof(request)}.{nameof(request.Scope)}");

        if (ValidateEvidenceShape(request, authorization.PrincipalId) is { Count: > 0 } shapeErrors)
        {
            return new(ConfidenceUpdateOutcome.Invalid, null, null, 0, null, shapeErrors);
        }

        // A scope check, not a delivery: the grant branch below refuses the record outright, so nothing
        // is handed over and an access log must not record one. Declaring it also keeps that refusal's
        // own message, which a fail-closed audit would otherwise replace with a bare NotFound.
        var read = await _store
            .GetAsync(authorization, request.Scope, request.ExperienceId, ScopeCheckRead, cancellationToken)
            .ConfigureAwait(false);

        if (read.Outcome != ExperienceStoreOutcome.Found)
        {
            return new(ToConfidenceOutcome(read.Outcome), null, null, 0, null, read.Errors);
        }

        if (read.Record is not { } record)
        {
            // A store that reports Found with no record is broken, but a broken store is a refusal to
            // report, not an infrastructure failure to raise: there is nothing here to compute against
            // and nothing was written, which is exactly what NotFound already means.
            return new(
                ConfidenceUpdateOutcome.NotFound,
                null,
                null,
                0,
                null,
                NoErrors,
                "The store reported the record as found but returned nothing to compute against.");
        }

        if (read.SharedByGrant)
        {
            // A grant confers reading one named record and nothing else. Writing to it would be a
            // foreign-scope write, so it is refused exactly like a record that is not here at all --
            // which is also what the store's own exact-scope projection update would report.
            return new(
                ConfidenceUpdateOutcome.NotFound,
                null,
                null,
                0,
                null,
                NoErrors,
                "The record was readable only through a sharing grant, which never confers writing to it.");
        }

        if (!ReuseConfidenceHeuristic.AcceptsEvidenceIn(record.Status))
        {
            return new(
                ConfidenceUpdateOutcome.Ineligible,
                null,
                null,
                record.Revision,
                record.Status,
                NoErrors,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "A {0} record does not accept confidence evidence; only {1} do, and no score may change that.",
                    record.Status,
                    string.Join(", ", ReuseConfidenceHeuristic.AcceptsEvidence)));
        }

        if (ValidateStoredCounters(record) is { Count: > 0 } counterErrors)
        {
            // Apply throws on these, and an unreadable record is a typed refusal everywhere else in this
            // library; a store that hands back a negative or saturated counter must not become the one
            // place a caller has to catch.
            return new(
                ConfidenceUpdateOutcome.Invalid,
                null,
                null,
                record.Revision,
                record.Status,
                counterErrors,
                "The stored record's evidence counters cannot have evidence applied to them.");
        }

        var update = ReuseConfidenceHeuristic.Apply(
            record,
            request.EvidenceId,
            request.Kind,
            request.Source,
            request.RunId,
            request.VerificationRoundId,
            request.Source == ConfidenceEvidenceSource.Human ? authorization.PrincipalId : null,
            request.Detail);

        var currentStatus = ReuseConfidenceHeuristic.StatusAfter(record.Status, request.Kind);

        var lifecycleEvent = new LifecycleEvent(
            EventId: request.EventId,
            ExperienceRecordId: request.ExperienceId,
            PriorStatus: record.Status,
            CurrentStatus: currentStatus,
            Reason: request.Reason,
            Producer: request.Producer,
            OccurredAt: request.OccurredAt,
            ExpectedRevision: record.Revision,
            ReplacementExperienceId: null,
            Confidence: update);

        var result = await _store
            .CommitLifecycleEventAsync(authorization, request.Scope, lifecycleEvent, cancellationToken)
            .ConfigureAwait(false);

        var outcome = ToConfidenceOutcome(result.Outcome);

        if (outcome != ConfidenceUpdateOutcome.Applied)
        {
            return new(outcome, lifecycleEvent, null, result.Revision, result.CurrentStatus, result.Errors);
        }

        // Only after the update is durable, and only when this call's contradiction actually took the
        // record out of reuse. A duplicate and a replay both leave the record where it was, and the store
        // reports that by naming the status it did not move -- so the hook is asked about the status the
        // record is in, never about the one an unapplied submission would have produced.
        var settledStatus = result.CurrentStatus ?? currentStatus;
        var deindexing = await TryRemoveEmbeddingAsync(
                authorization, request.Scope, request.ExperienceId, record.Status, settledStatus, cancellationToken)
            .ConfigureAwait(false);

        return new(
            ConfidenceUpdateOutcome.Applied,
            lifecycleEvent,
            // What the transaction stored, which is the submitted payload unless the independence key
            // was taken; a store that reports nothing is taken at its word that nothing moved.
            result.AppliedConfidence ?? update.AsRecordedOnly(),
            result.Revision,
            // The store reports the record's status when it knows it -- which is every case where it did
            // not move the record: a duplicate that left it alone, and a replay reporting the moment the
            // original submission settled. Falling back to the derived status covers the plain accepted
            // update, where the store moved the record to exactly this.
            settledStatus,
            result.Errors,
            Reason: null,
            deindexing);
    }

    /// <summary>
    /// The rules about a submission's own shape, which need no stored state and are therefore settled
    /// before the record is read: the identifiers that must be present, and the two fields that belong
    /// to exactly one <see cref="ConfidenceEvidenceSource"/> each.
    /// </summary>
    /// <summary>
    /// Checks that the counters the store handed back can have evidence applied to them at all: not
    /// negative, and not already at <see cref="int.MaxValue"/>, where the increment would have nowhere to
    /// go. Both are contract violations by the store rather than caller errors, but they are reported the
    /// way every other refusal here is -- a typed result naming the field -- because a caller that has
    /// never had to catch an exception from this call should not start now.
    /// </summary>
    private static List<StoreValidationError> ValidateStoredCounters(ExperienceRecord record)
    {
        var errors = new List<StoreValidationError>();

        foreach (var (count, path) in new[]
        {
            (record.SupportingValidations, nameof(record.SupportingValidations)),
            (record.Contradictions, nameof(record.Contradictions)),
        })
        {
            if (count < 0)
            {
                errors.Add(new(path, "the stored record reports a negative evidence counter."));
            }
            else if (count == int.MaxValue)
            {
                errors.Add(new(path, "the stored record's evidence counter is already at its maximum, so no further evidence can be counted."));
            }
        }

        return errors;
    }

    private static List<StoreValidationError> ValidateEvidenceShape(ApplyConfidenceEvidenceRequest request, string? principalId)
    {
        const string PrincipalPath = "Authorization.PrincipalId";

        var errors = new List<StoreValidationError>();

        if (request.EventId == Guid.Empty)
        {
            errors.Add(new(nameof(request.EventId), "must not be an empty GUID."));
        }

        if (request.ExperienceId == Guid.Empty)
        {
            errors.Add(new(nameof(request.ExperienceId), "must not be an empty GUID."));
        }

        if (request.EvidenceId == Guid.Empty)
        {
            errors.Add(new(nameof(request.EvidenceId), "must not be an empty GUID."));
        }

        if (request.RunId == Guid.Empty)
        {
            // The run is half of every independence key; without it the submission cannot be counted
            // once rather than every time it is sent.
            errors.Add(new(nameof(request.RunId), "must name the run the reuse was observed in."));
        }

        if (!Enum.IsDefined(request.Kind))
        {
            errors.Add(new(nameof(request.Kind), "must be a defined evidence kind."));
        }

        if (!Enum.IsDefined(request.Source))
        {
            errors.Add(new(nameof(request.Source), "must be a defined evidence source."));
        }
        else if (request.Source == ConfidenceEvidenceSource.Machine)
        {
            if (request.VerificationRoundId is not { } roundId || roundId == Guid.Empty)
            {
                errors.Add(new(
                    nameof(request.VerificationRoundId),
                    $"is required for {ConfidenceEvidenceSource.Machine} evidence, which is counted once per run and round."));
            }
        }
        else
        {
            if (request.VerificationRoundId is not null)
            {
                errors.Add(new(
                    nameof(request.VerificationRoundId),
                    $"must be null for {ConfidenceEvidenceSource.Human} evidence, which is counted once per reviewer and run."));
            }

            // The reviewer identity is the whole of the human independence rule, and it comes from the
            // authorization context rather than the request -- so it is checked here, before the record is
            // read, rather than being discovered as a constraint violation after the work is done.
            if (string.IsNullOrWhiteSpace(principalId))
            {
                errors.Add(new(
                    PrincipalPath,
                    $"must be non-blank for {ConfidenceEvidenceSource.Human} evidence: it is the reviewer the submission is counted under."));
            }
            else if (!string.Equals(principalId, principalId.Trim(), StringComparison.Ordinal))
            {
                // Compared ordinally, like every other identity here, so " alice" and "alice" would key as
                // two independent reviewers. Refused rather than trimmed: normalizing would be this
                // library deciding who a reviewer is.
                errors.Add(new(
                    PrincipalPath,
                    "must not have leading or trailing whitespace: it would be counted as a second, independent reviewer."));
            }
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors.Add(new(nameof(request.Reason), "must be a non-blank, auditable reason."));
        }

        if (string.IsNullOrWhiteSpace(request.Producer))
        {
            errors.Add(new(nameof(request.Producer), "must be a non-blank producer identity."));
        }

        if (request.OccurredAt == default)
        {
            errors.Add(new(nameof(request.OccurredAt), "must be set to when the observation was made."));
        }

        return errors;
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
        Scope scope,
        Guid experienceId,
        ExperienceStatus? priorStatus,
        ExperienceStatus currentStatus,
        CancellationToken cancellationToken)
    {
        if (_indexingService is null
            || priorStatus is not { } prior
            || !IsEligible(prior)
            || IsEligible(currentStatus))
        {
            return null;
        }

        // Bounded, and on its own budget: derived data never holds the caller open after the canonical
        // work is done.
        using var deindexing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deindexing.CancelAfter(DeindexingTimeout);

        try
        {
            // The public, instrumented sibling, handed this library's own budget rather than the
            // caller's token. A vector this hook fails to remove is a record that stays searchable
            // after it stopped being eligible, so the hook has to be visible as a `deindex` of its own
            // -- nested in the transition that asked for it -- rather than silently absorbed into it.
            // The budget expiring classifies as a Timeout, not as the caller cancelling: the wrapper
            // compares this token against the host's.
            return await _indexingService
                .RemoveAsync(authorization, scope, experienceId, deindexing.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Including cancellation, which reaches here only if a future hook throws before
            // ExperienceIndexingService.RemoveAsync's own try -- RemoveAsync reports its own
            // cancellation rather than throwing it, so there is no separate branch for a timeout.

            return new(
                ExperienceDeindexingOutcome.Failed,
                experienceId,
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

        // A commit against a tombstone. An expected refusal the port documents, not a store fault: it
        // is surfaced like NotFound -- a returned outcome, so it is counted under its own name and never
        // reaches the failure counter -- and it is terminal, which is why it is not StaleRevision.
        ExperienceStoreOutcome.Deleted => LifecycleTransitionOutcome.Deleted,
        _ => throw new ExperienceStoreException(
            $"The Experience Record store returned '{outcome}', which is not a lifecycle commit outcome."),
    };

    /// <summary>
    /// Maps a store outcome to its confidence-update counterpart one-to-one. It covers both port calls
    /// this operation makes -- the read and the commit -- because both can refuse for the same reasons
    /// and a caller should not have to know which stage reported it.
    /// </summary>
    private static ConfidenceUpdateOutcome ToConfidenceOutcome(ExperienceStoreOutcome outcome) => outcome switch
    {
        ExperienceStoreOutcome.Committed => ConfidenceUpdateOutcome.Applied,
        ExperienceStoreOutcome.StaleRevision => ConfidenceUpdateOutcome.StaleRevision,
        ExperienceStoreOutcome.StatusMismatch => ConfidenceUpdateOutcome.StatusMismatch,
        ExperienceStoreOutcome.Conflict => ConfidenceUpdateOutcome.Conflict,
        ExperienceStoreOutcome.NotFound => ConfidenceUpdateOutcome.NotFound,
        ExperienceStoreOutcome.Denied => ConfidenceUpdateOutcome.Denied,
        ExperienceStoreOutcome.Invalid => ConfidenceUpdateOutcome.Invalid,

        // The read or the commit found a tombstone. Surfaced like NotFound (a returned refusal, never
        // an infrastructure failure), and terminal: no resubmission can land against an erased record.
        ExperienceStoreOutcome.Deleted => ConfidenceUpdateOutcome.Deleted,
        _ => throw new ExperienceStoreException(
            $"The Experience Record store returned '{outcome}', which is not a confidence-update outcome."),
    };
}
