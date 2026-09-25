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
/// The run the reuse was observed in -- never the run the record came from, which is refused
/// (<see cref="IndependenceRefusal.OwnRun"/>) in every mode. Must not be <see cref="Guid.Empty"/>. It is
/// half of every independence key, so under <see cref="IndependenceVerification.Verified"/> it must be a
/// run the library knows in <paramref name="Scope"/>: one finalized into a record there, or one the
/// capture service wired into the lifecycle service holds there. An invented run is refused
/// (<see cref="IndependenceRefusal.UnknownRun"/>) rather than becoming a fresh key, and a real run that was never
/// exposed to the record -- whose provenance does not name it at or before its current revision -- is refused
/// too (<see cref="IndependenceRefusal.NotExposed"/>).
/// </param>
/// <param name="VerificationRoundId">
/// The verification round the observation came from. Required for
/// <see cref="ConfidenceEvidenceSource.Machine"/> and rejected for
/// <see cref="ConfidenceEvidenceSource.Human"/>. Under verification it must be the round finalization
/// closed for <paramref name="RunId"/> (<see cref="ExperienceRecord.ClosedRoundId"/>); any other is
/// refused (<see cref="IndependenceRefusal.UnknownRound"/>).
/// </param>
/// <param name="Reason">Auditable, human-readable reason, stamped on the lifecycle event. Never private reasoning. Must be non-blank.</param>
/// <param name="Producer">Identity of whatever produced this evidence (an evaluator name, a tool, or a review process). Must be non-blank.</param>
/// <param name="OccurredAt">When the observation was made. Part of the event's stored identity, so it must not be regenerated on a retry.</param>
/// <param name="Detail">Optional sanitized, human-readable detail. Never private reasoning.</param>
/// <param name="AssessmentToken">
/// For <see cref="ConfidenceEvidenceSource.Human"/> evidence under verification: the token
/// <see cref="AssessmentTokenIssuer.Issue"/> minted for this review -- for <paramref name="Scope"/>,
/// <paramref name="RunId"/>, the reviewing principal, <paramref name="Kind"/>, and a set of records that
/// includes <paramref name="ExperienceId"/>. Must be <see langword="null"/> for machine evidence, and is
/// ignored when the host opted out of verification. A bearer credential: never an agent's output.
/// </param>
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
    string? Detail = null,
    string? AssessmentToken = null)
{
    /// <summary>Prints the request without its assessment token, which is a bearer credential for one review.</summary>
    /// <param name="builder">The builder the members are printed into.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("EventId = ").Append(EventId)
            .Append(", ExperienceId = ").Append(ExperienceId)
            .Append(", Scope = ").Append(Scope)
            .Append(", EvidenceId = ").Append(EvidenceId)
            .Append(", Kind = ").Append(Kind)
            .Append(", Source = ").Append(Source)
            .Append(", RunId = ").Append(RunId)
            .Append(", VerificationRoundId = ").Append(VerificationRoundId)
            .Append(", Reason = ").Append(Reason)
            .Append(", Producer = ").Append(Producer)
            .Append(", OccurredAt = ").Append(OccurredAt)
            .Append(", Detail = ").Append(Detail)
            .Append(", AssessmentToken = ").Append(AssessmentToken is null ? "null" : "<redacted>");
        return true;
    }
}

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

    /// <summary>
    /// The submission's independence key could not be verified: its run is the record's own, or not one
    /// the library knows in the record's scope (or known only through a hand-written record), its round is
    /// not the one finalization closed for that run, its assessment token is missing, invalid, expired, for
    /// another record, or already spent, or the run was never exposed to the record at or before its current
    /// revision.
    /// <see cref="ApplyConfidenceEvidenceResult.Refusal"/> says which. This call wrote nothing and moved no
    /// counter. It is not proof that the evidence was never written: verification runs before the store's
    /// replay check, so a retry of evidence that did land, made after its token expired, its key rotated, or
    /// its run stopped being known, is refused here although the original is durable -- reconcile against
    /// the record's history. Nor is it always permanent: a run that is finalized later becomes known.
    /// </summary>
    Unverified,
}

/// <summary>
/// Which confidence evidence <see cref="ExperienceLifecycleService.ReadConfidenceAsync"/> counts.
/// </summary>
public enum ConfidenceEvidenceFilter
{
    /// <summary>Everything the record's counters hold: the stored score, unchanged.</summary>
    All,

    /// <summary>
    /// Leaves out evidence the verification opt-out admitted
    /// (<see cref="ConfidenceEvidenceAdmission.HostTrusted"/>). Evidence stored before admission was recorded
    /// is kept.
    /// </summary>
    ExcludeHostTrusted,

    /// <summary>
    /// Counts only evidence recorded as <see cref="ConfidenceEvidenceAdmission.Verified"/>, plus the initial
    /// counters of a record finalization wrote: host-trusted evidence, evidence with no recorded admission (stored
    /// before admission was recorded, or written by something other than Core), and the initial counters of a
    /// record written by hand (<see cref="ExperienceRecordOrigin.HostWritten"/>, whose writer chose them) are all
    /// left out.
    /// </summary>
    VerifiedOnly,
}

/// <summary>How many counted updates of one admission moved each counter.</summary>
/// <param name="Supporting">Supporting validations those updates counted.</param>
/// <param name="Contradicting">Contradictions those updates counted.</param>
public sealed record ConfidenceAdmissionCounts(int Supporting, int Contradicting);

/// <summary>
/// A record's reuse confidence as <see cref="ExperienceLifecycleService.ReadConfidenceAsync"/> computed it.
/// </summary>
/// <param name="ExperienceId">The record.</param>
/// <param name="Revision">The revision the report reflects.</param>
/// <param name="Status">The record's status at that revision.</param>
/// <param name="Filter">Which evidence was counted.</param>
/// <param name="ReuseConfidence">The score over the counted evidence: the stored score when nothing was excluded, otherwise <see cref="ReuseConfidenceHeuristic.Score"/> over <paramref name="SupportingValidations"/> and <paramref name="Contradictions"/>.</param>
/// <param name="SupportingValidations">The supporting count with the excluded evidence taken out.</param>
/// <param name="Contradictions">The contradiction count with the excluded evidence taken out.</param>
/// <param name="StoredReuseConfidence">The record's stored score.</param>
/// <param name="StoredSupportingValidations">The record's stored supporting count, which includes its initial validation.</param>
/// <param name="StoredContradictions">The record's stored contradiction count.</param>
/// <param name="Verified">What verified evidence counted.</param>
/// <param name="HostTrusted">What evidence the verification opt-out admitted counted.</param>
/// <param name="Unrecorded">What evidence with no recorded admission counted: stored before admission was recorded, or written by something other than Core.</param>
public sealed record ConfidenceReport(
    Guid ExperienceId,
    long Revision,
    ExperienceStatus Status,
    ConfidenceEvidenceFilter Filter,
    double ReuseConfidence,
    int SupportingValidations,
    int Contradictions,
    double StoredReuseConfidence,
    int StoredSupportingValidations,
    int StoredContradictions,
    ConfidenceAdmissionCounts Verified,
    ConfidenceAdmissionCounts HostTrusted,
    ConfidenceAdmissionCounts Unrecorded)
{
    /// <summary>Who wrote the record, which decides whether <see cref="ConfidenceEvidenceFilter.VerifiedOnly"/> keeps its initial counters.</summary>
    public ExperienceRecordOrigin Origin { get; init; }

    /// <summary>The counters the record started with, which no history event explains.</summary>
    public ConfidenceAdmissionCounts Initial { get; init; } = new(0, 0);
}

/// <summary>
/// The result of one <see cref="ExperienceLifecycleService.ReadConfidenceAsync"/> call.
/// </summary>
/// <param name="Outcome">
/// <see cref="ExperienceStoreOutcome.Found"/> with a report; otherwise the store's refusal --
/// <see cref="ExperienceStoreOutcome.NotFound"/> (including a record readable only through a grant),
/// <see cref="ExperienceStoreOutcome.Deleted"/>, <see cref="ExperienceStoreOutcome.Denied"/> or
/// <see cref="ExperienceStoreOutcome.Invalid"/>.
/// </param>
/// <param name="Report">The report, on <see cref="ExperienceStoreOutcome.Found"/>; otherwise <see langword="null"/>.</param>
/// <param name="Errors">Every validation error on <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ConfidenceReadResult(
    ExperienceStoreOutcome Outcome,
    ConfidenceReport? Report,
    IReadOnlyList<StoreValidationError> Errors);

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
/// <param name="Refusal">Which identifier failed verification, on <see cref="ConfidenceUpdateOutcome.Unverified"/>; otherwise <see langword="null"/>.</param>
public sealed record ApplyConfidenceEvidenceResult(
    ConfidenceUpdateOutcome Outcome,
    LifecycleEvent? Event,
    ConfidenceUpdate? Update,
    long Revision,
    ExperienceStatus? Status,
    IReadOnlyList<StoreValidationError> Errors,
    string? Reason = null,
    ExperienceDeindexingResult? Deindexing = null,
    IndependenceRefusal? Refusal = null)
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
