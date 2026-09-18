using AgentExperience.Abstractions;

namespace AgentExperience.Core.Lifecycle;

/// <summary>
/// One requested lifecycle transition, submitted to
/// <see cref="ExperienceLifecycleService.CommitAsync"/>. Every identifying value is caller-supplied so
/// a retry is byte-for-byte the same request: <see cref="EventId"/> is the idempotency key the store
/// deduplicates on, and <see cref="OccurredAt"/> is part of the event's stored identity, so neither may
/// be regenerated on a retry.
/// </summary>
/// <param name="EventId">Unique identifier for this transition, and this call's idempotency key. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="ExperienceId">The record to transition. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Scope">The exact scope the record lies in. Never treated as authority.</param>
/// <param name="PriorStatus">
/// The record's status this transition starts from, as the caller read it. Checked against the
/// allowed-transition table, stamped onto the event, and matched against the record's stored status when
/// the store applies it -- so asserting the wrong one is a
/// <see cref="LifecycleTransitionOutcome.StatusMismatch"/>, never a committed forbidden transition.
/// <see langword="null"/> records a first event on a record that has no lifecycle history yet, which
/// skips both the transition table and the store's status match.
/// </param>
/// <param name="CurrentStatus">The status to move the record to.</param>
/// <param name="Reason">Auditable, human-readable reason for the transition. Never private reasoning. Must be non-blank.</param>
/// <param name="Producer">Identity of whatever produced this transition (a policy, an evaluator, or a human principal identifier). Must be non-blank.</param>
/// <param name="OccurredAt">When this transition was decided.</param>
/// <param name="ExpectedRevision">The record's revision this transition was decided against. Must equal the stored revision or the commit is refused as stale.</param>
public sealed record CommitLifecycleTransitionRequest(
    Guid EventId,
    Guid ExperienceId,
    Scope Scope,
    ExperienceStatus? PriorStatus,
    ExperienceStatus CurrentStatus,
    string Reason,
    string Producer,
    DateTimeOffset OccurredAt,
    long ExpectedRevision);

/// <summary>
/// The disposition a <see cref="ExperienceLifecycleService.CommitAsync"/> call reached. Every member
/// except <see cref="TransitionNotAllowed"/> is the store port's own
/// <see cref="ExperienceStoreOutcome"/>, surfaced one-to-one and never reinterpreted;
/// <see cref="TransitionNotAllowed"/> is the one decision Core makes on its own, before the port is
/// called at all.
/// </summary>
public enum LifecycleTransitionOutcome
{
    /// <summary>
    /// The event was appended and the record's projection updated in one transaction, or an identical
    /// replay reported the original commit. <c>Revision</c> is the record's revision after it.
    /// </summary>
    Committed,

    /// <summary>
    /// Core refused: the requested <see cref="CommitLifecycleTransitionRequest.PriorStatus"/> to
    /// <see cref="CommitLifecycleTransitionRequest.CurrentStatus"/> move is not one this version
    /// allows. No store call was made and nothing was written.
    /// </summary>
    TransitionNotAllowed,

    /// <summary>
    /// The record's revision had already moved past
    /// <see cref="CommitLifecycleTransitionRequest.ExpectedRevision"/>. Nothing was written;
    /// <c>Revision</c> carries the record's current revision to re-decide against.
    /// </summary>
    StaleRevision,

    /// <summary>
    /// The record was not in the <see cref="CommitLifecycleTransitionRequest.PriorStatus"/> the
    /// transition was decided against, so the store refused it even though the revision matched. Nothing
    /// was written; <c>CurrentStatus</c> carries the status the record is actually in.
    /// </summary>
    StatusMismatch,

    /// <summary>
    /// This <see cref="CommitLifecycleTransitionRequest.EventId"/> is already stored with at least one
    /// differing field. Nothing was written and the stored event is unchanged.
    /// </summary>
    Conflict,

    /// <summary>No record with that ID exists within the requested scope (including when it exists in another scope).</summary>
    NotFound,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See <c>Errors</c>. No storage was accessed.</summary>
    Invalid,
}

/// <summary>
/// The result of one <see cref="ExperienceLifecycleService.CommitAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Event">
/// The event Core stamped and issued to the store, when the request passed the transition table;
/// <see langword="null"/> on <see cref="LifecycleTransitionOutcome.TransitionNotAllowed"/>. Present even
/// when the store wrote nothing, so a caller can log exactly what was attempted.
/// </param>
/// <param name="Revision">The record's revision after the commit, or its current revision on <see cref="LifecycleTransitionOutcome.StaleRevision"/> and <see cref="LifecycleTransitionOutcome.StatusMismatch"/>; otherwise 0.</param>
/// <param name="CurrentStatus">The record's stored status on <see cref="LifecycleTransitionOutcome.StatusMismatch"/>, to re-decide the transition against; otherwise <see langword="null"/>.</param>
/// <param name="Errors">Every store validation error when <see cref="Outcome"/> is <see cref="LifecycleTransitionOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="Reason">Optional, auditable, content-free explanation, e.g. why Core refused the transition.</param>
public sealed record CommitLifecycleTransitionResult(
    LifecycleTransitionOutcome Outcome,
    LifecycleEvent? Event,
    long Revision,
    ExperienceStatus? CurrentStatus,
    IReadOnlyList<StoreValidationError> Errors,
    string? Reason);
