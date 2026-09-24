using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Indexing;

namespace AgentExperience.Core.Lifecycle;

/// <summary>
/// One submission of evidence about a stored lesson having been reused, handed to
/// <see cref="ExperienceLifecycleService.ApplyEvidenceAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no expected revision on this request and no counters or score. Core reads the
/// record, computes the new counters and the new score from what it read, and submits them with
/// <em>that</em> revision, so the arithmetic and the concurrency guard can never be about two different
/// versions of the record. A caller that loses the race is told so
/// (<see cref="ConfidenceUpdateOutcome.StaleRevision"/>) and can resubmit the identical request.
/// </para>
/// <para>
/// There is no reviewer identity on it either. For
/// <see cref="ConfidenceEvidenceSource.Human"/> evidence the reviewer is taken from the host's
/// <see cref="AuthorizationContext.PrincipalId"/> and nowhere else: it is the one field that decides
/// how many independent human opinions a record can accumulate, so accepting it from agent input would
/// make the independence rule mean nothing.
/// </para>
/// </remarks>
/// <param name="EventId">Unique identifier for the lifecycle event this submission rides on, and the commit's idempotency key. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="ExperienceId">The record the evidence is about. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Scope">The exact scope the record lies in. Never treated as authority.</param>
/// <param name="EvidenceId">Unique identifier for this evidence. Its own idempotency key: resubmitting it with identical content reports the original outcome, and with different content is refused. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Kind">Whether the reuse succeeded (<see cref="ConfidenceEvidenceKind.Supporting"/>) or did not (<see cref="ConfidenceEvidenceKind.Contradicting"/>).</param>
/// <param name="Source">Whether a machine evaluator or a human reviewer observed it.</param>
/// <param name="RunId">
/// The run the reuse was observed in -- not the run the record came from. Must not be
/// <see cref="Guid.Empty"/>, and must be established by the host: it is half of every independence key,
/// nothing here can check that the run happened, and a caller that invents one on every submission gets
/// a fresh key every time and can drive the score as high as it likes. Treat it exactly as you treat
/// <see cref="AuthorizationContext"/> -- never a value an agent produced.
/// </param>
/// <param name="VerificationRoundId">
/// The verification round the observation came from. Required for
/// <see cref="ConfidenceEvidenceSource.Machine"/> and rejected for
/// <see cref="ConfidenceEvidenceSource.Human"/>. The same host trust boundary as
/// <paramref name="RunId"/>: nothing here can check that a round was closed.
/// </param>
/// <param name="Reason">Auditable, human-readable reason, stamped on the lifecycle event. Never private reasoning. Must be non-blank.</param>
/// <param name="Producer">Identity of whatever produced this evidence (an evaluator name, a tool, or a review process). Must be non-blank.</param>
/// <param name="OccurredAt">When the observation was made. Part of the event's stored identity, so it must not be regenerated on a retry.</param>
/// <param name="Detail">Optional sanitized, human-readable detail. Never private reasoning.</param>
public sealed record ApplyConfidenceEvidenceRequest(
    Guid EventId,
    Guid ExperienceId,
    Scope Scope,
    Guid EvidenceId,
    ConfidenceEvidenceKind Kind,
    ConfidenceEvidenceSource Source,
    Guid RunId,
    Guid? VerificationRoundId,
    string Reason,
    string Producer,
    DateTimeOffset OccurredAt,
    string? Detail = null);

/// <summary>
/// The disposition an <see cref="ExperienceLifecycleService.ApplyEvidenceAsync"/> call reached. Every
/// member but <see cref="Ineligible"/> is the store port's own
/// <see cref="ExperienceStoreOutcome"/> surfaced one-to-one; <see cref="Ineligible"/> is the refusal
/// Core reaches from the record it read, before anything is written.
/// </summary>
public enum ConfidenceUpdateOutcome
{
    /// <summary>
    /// The evidence, the counters, the score, any status change, and the lifecycle event were committed
    /// together -- or an identical resubmission reported the commit that already happened. Read
    /// <see cref="ApplyConfidenceEvidenceResult.Counted"/> to tell a first submission for an
    /// independence key from a later one that was stored and counted zero times: both are
    /// <see cref="Applied"/>, because both were accepted and both are in the audit trail.
    /// </summary>
    Applied,

    /// <summary>
    /// The record's status does not accept confidence evidence -- see
    /// <see cref="ReuseConfidenceHeuristic.AcceptsEvidence"/>. Nothing was written and no counter moved.
    /// <see cref="ApplyConfidenceEvidenceResult.Status"/> carries the status that refused it.
    /// </summary>
    /// <remarks>
    /// This gate runs on the record Core read, <em>before</em> the store is asked anything, so it takes
    /// precedence over the store's own idempotency check. The consequence is worth knowing: resubmitting
    /// a piece of evidence that was already accepted, after the record has since been revoked or
    /// quarantined, reports <see cref="Ineligible"/> rather than replaying the original
    /// <see cref="Applied"/>. Nothing is lost by that -- the original update is durable and is in the
    /// record's history -- but a caller reconciling retries should read the history rather than treat
    /// this as "my submission never landed".
    /// </remarks>
    Ineligible,

    /// <summary>
    /// The record moved between Core reading it and the commit, so the arithmetic was about a version
    /// that is no longer current. Nothing was written; <see cref="ApplyConfidenceEvidenceResult.Revision"/>
    /// carries the record's current revision, and resubmitting the identical request recomputes against it.
    /// </summary>
    StaleRevision,

    /// <summary>
    /// The record was not in the status Core read it in, although the revision matched. Nothing was
    /// written; <see cref="ApplyConfidenceEvidenceResult.Status"/> carries the stored status.
    /// </summary>
    StatusMismatch,

    /// <summary>
    /// This <see cref="ApplyConfidenceEvidenceRequest.EvidenceId"/> or
    /// <see cref="ApplyConfidenceEvidenceRequest.EventId"/> is already stored with differing content.
    /// Nothing was written and the stored submission is unchanged.
    /// </summary>
    Conflict,

    /// <summary>No record with that ID exists within the requested scope (including when it exists in another scope).</summary>
    NotFound,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See <c>Errors</c>. No counter moved.</summary>
    Invalid,

    /// <summary>
    /// The record was erased: only a payload-free tombstone remains under its ID, and the store reported
    /// it as <see cref="ExperienceStoreOutcome.Deleted"/> -- either when Core read the record or when it
    /// committed the evidence. Nothing was written and no counter moved.
    /// <para>
    /// This is terminal and never retryable. A tombstone is never resurrected, so resubmitting the same
    /// evidence can never land. It is reported only within the scope that owned the record; any other
    /// scope sees <see cref="NotFound"/>, whether or not the ID was ever erased.
    /// </para>
    /// </summary>
    Deleted,
}

/// <summary>
/// The result of one <see cref="ExperienceLifecycleService.ApplyEvidenceAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Event">
/// The lifecycle event Core stamped, including the confidence payload <em>as submitted</em>, when the
/// request got as far as the store; otherwise <see langword="null"/>. Present even when nothing was
/// written, so a caller can log exactly what was attempted. To see what actually landed, read
/// <paramref name="Update"/>.
/// </param>
/// <param name="Update">
/// The confidence movement as the transaction stored it, on <see cref="ConfidenceUpdateOutcome.Applied"/>;
/// otherwise <see langword="null"/>. Its prior and new values equal each other exactly when the
/// submission's independence key was already taken.
/// </param>
/// <param name="Revision">The record's revision after the commit, or its current revision on <see cref="ConfidenceUpdateOutcome.StaleRevision"/> and <see cref="ConfidenceUpdateOutcome.StatusMismatch"/>; otherwise 0.</param>
/// <param name="Status">The record's status: after the update on <see cref="ConfidenceUpdateOutcome.Applied"/>, the status that refused the evidence on <see cref="ConfidenceUpdateOutcome.Ineligible"/>, the stored status on <see cref="ConfidenceUpdateOutcome.StatusMismatch"/>; otherwise <see langword="null"/>.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ConfidenceUpdateOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="Reason">Optional, auditable, content-free explanation of a refusal.</param>
/// <param name="Deindexing">
/// What became of the record's embedding, when a contradiction moved it out of eligibility and a
/// de-indexing hook is wired in; otherwise <see langword="null"/>. It can never change
/// <see cref="Outcome"/> -- the update is already durable by the time it runs, and both retrieval
/// channels filter on the record's status anyway. See
/// <see cref="CommitLifecycleTransitionResult.Deindexing"/> for what a host should do with it.
/// </param>
public sealed record ApplyConfidenceEvidenceResult(
    ConfidenceUpdateOutcome Outcome,
    LifecycleEvent? Event,
    ConfidenceUpdate? Update,
    long Revision,
    ExperienceStatus? Status,
    IReadOnlyList<StoreValidationError> Errors,
    string? Reason = null,
    ExperienceDeindexingResult? Deindexing = null)
{
    /// <summary>
    /// Whether this submission moved a counter. <see langword="false"/> for an accepted submission whose
    /// independence key was already taken -- which is still <see cref="ConfidenceUpdateOutcome.Applied"/>,
    /// because the submission is recorded, just not counted.
    /// </summary>
    public bool Counted => Update?.Counted ?? false;

    /// <summary>The record's reuse confidence after this call, on <see cref="ConfidenceUpdateOutcome.Applied"/>; otherwise <see langword="null"/>.</summary>
    public double? ReuseConfidence => Update?.NewReuseConfidence;

    /// <summary>The record's supporting-validation count after this call, on <see cref="ConfidenceUpdateOutcome.Applied"/>; otherwise <see langword="null"/>.</summary>
    public int? SupportingValidations => Update?.NewSupportingValidations;

    /// <summary>The record's contradiction count after this call, on <see cref="ConfidenceUpdateOutcome.Applied"/>; otherwise <see langword="null"/>.</summary>
    public int? Contradictions => Update?.NewContradictions;
}
