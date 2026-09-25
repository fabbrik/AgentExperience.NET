namespace AgentExperience.Abstractions;

/// <summary>
/// Whether reuse of the exposed records is judged to have helped a run, hurt it, or neither -- where
/// "neither" is overwhelmingly the honest answer, not a failure to gather data.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Unknown"/> is the default and it moves nothing.</b> Records being injected into a run
/// that then succeeded says only that both things happened. Attributing the success to the records is a
/// separate claim, and this library accepts it only from an authorized human assessment or a
/// comparative evaluator result -- never from a caller asserting it. Everything else is recorded as
/// <see cref="Unknown"/>, which changes no confidence, no counter, and no status.
/// </para>
/// <para>
/// This is deliberately not <see cref="ConfidenceEvidenceKind"/>. That enum is about one piece of
/// evidence already accepted against one record; this is about what a whole submission is judged to
/// show, and only <see cref="Improved"/> and <see cref="Harmed"/> ever become evidence at all.
/// </para>
/// </remarks>
public enum ExperienceReuseBenefit
{
    /// <summary>
    /// Nothing established whether reuse helped. The exposure is recorded and no record moves. This is
    /// what exposure alone, and a caller's unevidenced claim, both come to.
    /// </summary>
    Unknown,

    /// <summary>
    /// Attribution evidence says reuse helped. Each attributed record receives
    /// <see cref="ConfidenceEvidenceKind.Supporting"/> evidence through the ordinary confidence path.
    /// </summary>
    Improved,

    /// <summary>
    /// Attribution evidence says reuse hurt. Each attributed record receives
    /// <see cref="ConfidenceEvidenceKind.Contradicting"/> evidence, which contests it. Nothing is
    /// deleted: the record stays, and its own history carries the reason.
    /// </summary>
    Harmed,
}

/// <summary>
/// Which of the two accepted attribution shapes a submission carried, if either.
/// </summary>
public enum ReuseAttributionSource
{
    /// <summary>
    /// No attribution was offered, or none that this library accepts. The submission's benefit is
    /// <see cref="ExperienceReuseBenefit.Unknown"/> and no confidence evidence is submitted.
    /// </summary>
    None,

    /// <summary>
    /// A <see cref="HumanReuseAssessment"/>, attributed to the host's
    /// <see cref="AuthorizationContext.PrincipalId"/>. Produces
    /// <see cref="ConfidenceEvidenceSource.Human"/> evidence.
    /// </summary>
    HumanAssessment,

    /// <summary>
    /// A <see cref="ComparativeEvaluationResult"/> carrying its evidence and its verification round.
    /// Produces <see cref="ConfidenceEvidenceSource.Machine"/> evidence.
    /// </summary>
    ComparativeEvaluation,
}

/// <summary>
/// What the host actually measured about a run, as a named kind plus a number.
/// </summary>
/// <remarks>
/// The kind is deliberately an opaque, host-chosen string rather than an enum this library invents:
/// what "better" means is a property of the host's task, not of a memory library. Nothing here
/// interprets <paramref name="Value"/> -- it is recorded so a later measurement story can aggregate
/// memory-enabled against memory-disabled runs (see <see cref="ExperienceReuseFeedback.TrialLabel"/>),
/// and it never influences a confidence score.
/// </remarks>
/// <param name="Kind">What was measured, e.g. <c>"task-success-rate"</c>, <c>"tool-calls"</c>, or <c>"wall-clock-ms"</c>. Must be non-blank.</param>
/// <param name="Value">The measured value. Must be a finite number; higher is not assumed to be better.</param>
public sealed record ReuseMeasure(string Kind, double Value);

/// <summary>
/// An authorized human's judgement that reuse of named records helped or hurt a run. One of the two
/// shapes that can move confidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>READ THIS BEFORE WIRING IT UP: a human assessment is a HOST TRUST BOUNDARY, and it is the weakest
/// one in this library.</b> Nothing here can check that a human made this judgement, or that the human
/// saw the run. What the library enforces is worth stating exactly: the reviewer is the host's
/// <see cref="AuthorizationContext.PrincipalId"/>; the run must be one the library knows in the
/// feedback's scope; the assessment must present an <paramref name="AssessmentToken"/> the library
/// minted under the host's secret for exactly this scope, run, reviewer, direction and records, which
/// has not expired and lands at most once per record; and one reviewer's opinion about one run counts
/// once. An agent can therefore not mint an assessment, or a fresh independence key, from its own
/// output. Everything outside that -- that a person exists, that they read the transcript, that they
/// meant it -- is the host's to establish, and so is keeping the token issuer away from agent-driven
/// code. A host that opted out of verification
/// (<c>IndependenceVerification.TrustHostSuppliedIdentifiers</c>) is back to trusting the identifiers it
/// passes, and must establish them from its own bookkeeping, never from anything an agent produced.
/// </para>
/// <para>
/// <b>There is no reviewer field, on purpose.</b> The reviewer is the host's
/// <see cref="AuthorizationContext.PrincipalId"/> and nothing else -- it is half of the human
/// independence key (<c>human:{principal}:{run}</c>), so accepting it from the submission would let one
/// principal manufacture as many "independent" reviewers as it liked.
/// </para>
/// </remarks>
/// <param name="AssessmentId">
/// The identity of the review this judgement came out of. Must not be <see cref="Guid.Empty"/>. Under
/// verification it must be the ID of <paramref name="AssessmentToken"/> (the
/// <c>IssuedAssessmentToken.AssessmentId</c> the issuer returned with it). It is stored on the feedback
/// ledger so an auditor can go from a moved score back to the review that moved it; requiring it is what
/// keeps a human attribution from being a bare claim with a timestamp on it.
/// </param>
/// <param name="Benefit">Whether reuse helped or hurt. Must be <see cref="ExperienceReuseBenefit.Improved"/> or <see cref="ExperienceReuseBenefit.Harmed"/>: an assessment of <see cref="ExperienceReuseBenefit.Unknown"/> is not an assessment.</param>
/// <param name="AttributedExperienceIds">The exposed records this judgement is about. Must be non-empty, free of duplicates, and a subset of <see cref="ExperienceReuseFeedback.ExposedExperienceIds"/>.</param>
/// <param name="Rationale">Auditable, sanitized reasoning, recorded as the evidence's detail. Never private chain-of-thought. Must be non-blank.</param>
/// <param name="AssessedAt">When the assessment was made. Must be set, and it is stored.</param>
/// <param name="VerificationRoundId">
/// The verification round the assessment was made against, when the host closed one for the run;
/// <see langword="null"/> when it did not. Stored for audit. It is deliberately <em>not</em> part of the
/// independence key: human evidence counts once per reviewer and run, so keying on a round the reviewer
/// chose would let one reviewer's opinion about one run count as many times as rounds were closed.
/// </param>
/// <param name="AssessmentToken">
/// The assessment token <c>AssessmentTokenIssuer.Issue</c> minted for this review: for the feedback's
/// scope and run, the reviewing principal, the direction (<see cref="ExperienceReuseBenefit.Improved"/>
/// supports, <see cref="ExperienceReuseBenefit.Harmed"/> contradicts) and at least every attributed
/// record. Required unless the host opted out of verification; without a valid one the attribution is
/// dropped and the exposure is recorded with benefit <see cref="ExperienceReuseBenefit.Unknown"/>. It is
/// a bearer credential for one review: pass it from your review bookkeeping, never through anything an
/// agent can read or write.
/// </param>
public sealed record HumanReuseAssessment(
    Guid AssessmentId,
    ExperienceReuseBenefit Benefit,
    IReadOnlyList<Guid> AttributedExperienceIds,
    string Rationale,
    DateTimeOffset AssessedAt,
    Guid? VerificationRoundId = null,
    string? AssessmentToken = null)
{
    /// <summary>Prints the assessment without its token, which is a bearer credential for one review.</summary>
    /// <param name="builder">The builder the members are printed into.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("AssessmentId = ").Append(AssessmentId)
            .Append(", Benefit = ").Append(Benefit)
            .Append(", AttributedExperienceIds = ").Append(AttributedExperienceIds)
            .Append(", Rationale = ").Append(Rationale)
            .Append(", AssessedAt = ").Append(AssessedAt)
            .Append(", VerificationRoundId = ").Append(VerificationRoundId)
            .Append(", AssessmentToken = ").Append(AssessmentToken is null ? "null" : "<redacted>");
        return true;
    }
}

/// <summary>
/// The result a comparative evaluator reached about one run: the second shape that can move
/// confidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>This library does not implement a comparative evaluator.</b> It defines the contract and verifies
/// the result it is given: the run it names must be the run the feedback is about, it must carry a
/// verification round, it must name the records it is about, and it must carry the evidence it reached
/// its conclusion from. A result that does not is refused rather than downgraded silently.
/// </para>
/// <para>
/// <b><see cref="RunId"/> and <see cref="VerificationRoundId"/> are verified</b>, exactly as they are on
/// the confidence path: together they form the machine independence key <c>machine:{run}:{round}</c>, so
/// the run must be one finalized into a record in the feedback's scope and the round must be the one that
/// finalization closed (<see cref="ExperienceRecord.ClosedRoundId"/>). A result naming an invented run or
/// round has its attribution dropped. A host that opted out of verification is back to trusting both.
/// </para>
/// </remarks>
/// <param name="EvaluatorId">Identity of the evaluator that produced this result, recorded as the evidence's producer. Must be non-blank.</param>
/// <param name="RunId">The run that was evaluated. Must equal <see cref="ExperienceReuseFeedback.RunId"/>.</param>
/// <param name="VerificationRoundId">The verification round the comparison was made in. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Benefit">Whether reuse helped or hurt. Must be <see cref="ExperienceReuseBenefit.Improved"/> or <see cref="ExperienceReuseBenefit.Harmed"/>.</param>
/// <param name="AttributedExperienceIds">The exposed records the comparison attributes the difference to. Must be non-empty, free of duplicates, and a subset of <see cref="ExperienceReuseFeedback.ExposedExperienceIds"/>.</param>
/// <param name="Evidence">The evidence the evaluator reached its conclusion from. Must be non-empty.</param>
/// <param name="Summary">Auditable, sanitized summary, recorded as the evidence's detail. Must be non-blank.</param>
/// <param name="EvaluatedAt">When the comparison was made. Must be set.</param>
public sealed record ComparativeEvaluationResult(
    string EvaluatorId,
    Guid RunId,
    Guid VerificationRoundId,
    ExperienceReuseBenefit Benefit,
    IReadOnlyList<Guid> AttributedExperienceIds,
    IReadOnlyList<Evidence> Evidence,
    string Summary,
    DateTimeOffset EvaluatedAt);

/// <summary>
/// One submission of what happened in a run that stored experience was injected into: which records it
/// saw, how the run came out, what was measured, and -- only if evidence supports it -- whether reuse
/// is attributed with helping or hurting.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="FeedbackId"/> makes the whole submission idempotent.</b> Resubmitting it with identical
/// content reports the original outcome and writes nothing twice; resubmitting it with different content
/// is refused with nothing written. Each attributed record's confidence evidence ID is derived from this
/// ID and the record's ID, so a retry after a partial failure converges rather than double-counting.
/// </para>
/// <para>
/// <b><see cref="RunId"/> is half of every independence key</b> the confidence path deduplicates on, so an
/// attribution is accepted only for a run the library knows in <see cref="Scope"/> (finalized there, or
/// held by the capture service), and never for an attributed record's own source run. Exposure without
/// attribution keys nothing and is recorded against the run as given. Take it from your own run
/// bookkeeping (the adapter's session state), never from an identifier an agent produced.
/// </para>
/// <para>
/// <b>Exposure is not attribution.</b> A submission with no <see cref="HumanAssessment"/> and no
/// <see cref="ComparativeEvaluation"/> is recorded with benefit <see cref="ExperienceReuseBenefit.Unknown"/>
/// and moves nothing -- however confident <see cref="ClaimedBenefit"/> is.
/// </para>
/// </remarks>
/// <param name="FeedbackId">The submission's identity and its idempotency key. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="RunId">The run the records were injected into. Must not be <see cref="Guid.Empty"/>. Not the record's own source run.</param>
/// <param name="Scope">The exact scope the exposed records lie in. Never treated as authority.</param>
/// <param name="ExposedExperienceIds">Every record the run was exposed to, e.g. an injection result's injected IDs. Must be non-empty and free of duplicates and empty GUIDs.</param>
/// <param name="RunOutcome">How the run's task came out, as the host verified it. A failed run with no attribution still moves nothing.</param>
/// <param name="Measure">What the host measured about the run.</param>
/// <param name="ObservedAt">When the feedback was observed. Part of the derived evidence's stored identity, so it must not be regenerated on a retry. Must be set.</param>
/// <param name="ClaimedBenefit">
/// What the caller believes happened. Recorded verbatim for audit and analysis and never acted on: only
/// <see cref="HumanAssessment"/> or <see cref="ComparativeEvaluation"/> can move a score. It exists so a
/// caller's belief is visible in the ledger rather than being silently discarded.
/// </param>
/// <param name="HumanAssessment">An authorized human's attribution, or <see langword="null"/>. Mutually exclusive with <paramref name="ComparativeEvaluation"/>.</param>
/// <param name="ComparativeEvaluation">A comparative evaluator's attribution, or <see langword="null"/>. Mutually exclusive with <paramref name="HumanAssessment"/>.</param>
/// <param name="TrialLabel">
/// Optional host-chosen label naming the experimental condition this run belongs to, e.g.
/// <c>"memory-enabled"</c> or <c>"memory-disabled"</c>. Recorded so a later measurement can aggregate
/// conditions that were declared up front rather than selecting subsets after the fact. Must be
/// non-blank when supplied.
/// </param>
public sealed record ExperienceReuseFeedback(
    Guid FeedbackId,
    Guid RunId,
    Scope Scope,
    IReadOnlyList<Guid> ExposedExperienceIds,
    TaskVerificationStatus RunOutcome,
    ReuseMeasure Measure,
    DateTimeOffset ObservedAt,
    ExperienceReuseBenefit ClaimedBenefit = ExperienceReuseBenefit.Unknown,
    HumanReuseAssessment? HumanAssessment = null,
    ComparativeEvaluationResult? ComparativeEvaluation = null,
    string? TrialLabel = null)
{
    /// <summary>
    /// The largest number of records one submission may name as exposed. It is the only bound on the
    /// per-record confidence submissions a single call fans out into, which are sequential round trips,
    /// so it is stated here, checked by the store port, and mirrored by the schema as a bound on an
    /// exposure's ordinal.
    /// </summary>
    public const int MaxExposedRecords = 64;
}

/// <summary>
/// One exposed record as the feedback ledger stores it: the record the run saw, whether attribution
/// named it, and the confidence evidence ID that was derived for it if so.
/// </summary>
/// <param name="ExperienceId">The exposed record.</param>
/// <param name="Attributed">Whether accepted attribution evidence named this record.</param>
/// <param name="EvidenceId">
/// The confidence evidence ID <em>derived</em> for this record from the feedback ID, present exactly
/// when <paramref name="Attributed"/> is <see langword="true"/>.
/// <para>
/// It says which ID the submission for this record uses -- not that the submission landed. The exposure
/// is written before any confidence submission is attempted, and a record that turned out ineligible,
/// unresolved, or whose commit failed has this ID and no row in the confidence ledger. An auditor
/// therefore joins with a LEFT JOIN and reads a missing row as "attributed, not yet counted", which is
/// the outstanding work a retry of the same feedback converges on.
/// </para>
/// </param>
public sealed record ExperienceReuseExposure(
    Guid ExperienceId,
    bool Attributed,
    Guid? EvidenceId);

/// <summary>
/// One feedback submission in the shape the ledger stores it: the caller's submission with the
/// attribution decision already made, so a store persists a decision rather than re-deciding one.
/// </summary>
/// <remarks>
/// Core decides <paramref name="Benefit"/>, <paramref name="AttributionSource"/>, and which exposures
/// are attributed, and derives each attributed exposure's evidence ID. A store writes exactly what it
/// is given: it never promotes <see cref="ExperienceReuseBenefit.Unknown"/> and never decides that a
/// claim counts as attribution.
/// </remarks>
/// <param name="FeedbackId">The submission's identity and idempotency key.</param>
/// <param name="RunId">The run the records were injected into.</param>
/// <param name="Scope">The exact scope the exposed records lie in.</param>
/// <param name="RunOutcome">How the run's task came out.</param>
/// <param name="ClaimedBenefit">What the caller believed, recorded and never acted on.</param>
/// <param name="Benefit">What the accepted attribution established. <see cref="ExperienceReuseBenefit.Unknown"/> unless <paramref name="AttributionSource"/> is not <see cref="ReuseAttributionSource.None"/>.</param>
/// <param name="AttributionSource">Which attribution shape was accepted, if either.</param>
/// <param name="ReviewerIdentity">The host's <see cref="AuthorizationContext.PrincipalId"/> for a human assessment; otherwise <see langword="null"/>.</param>
/// <param name="EvaluatorId">The comparative evaluator's identity for a comparative result; otherwise <see langword="null"/>.</param>
/// <param name="VerificationRoundId">The comparative result's verification round; otherwise <see langword="null"/>.</param>
/// <param name="AssessmentId">The <see cref="HumanReuseAssessment.AssessmentId"/> for a human assessment; otherwise <see langword="null"/>.</param>
/// <param name="Rationale">The assessment's rationale or the evaluator's summary; <see langword="null"/> when there was no attribution.</param>
/// <param name="EvidenceIds">
/// The <see cref="Abstractions.Evidence.EvidenceId"/> of every piece of evidence a comparative result
/// reached its conclusion from, so an auditor can see what a moved score rested on rather than only the
/// evaluator's own summary of it. Empty for a human assessment and for no attribution.
/// </param>
/// <param name="AttributedAt">When the assessment or the comparison was made; <see langword="null"/> when there was no attribution.</param>
/// <param name="Measure">What the host measured.</param>
/// <param name="TrialLabel">The experimental condition, or <see langword="null"/>.</param>
/// <param name="ObservedAt">When the feedback was observed.</param>
/// <param name="Exposures">
/// One entry per exposed record, ordered by <see cref="ExperienceReuseExposure.ExperienceId"/>. The
/// order is normalized rather than the caller's, so two hosts submitting the same feedback with the same
/// records listed differently still produce the same stored submission and converge instead of
/// colliding.
/// </param>
public sealed record RecordedExperienceReuseFeedback(
    Guid FeedbackId,
    Guid RunId,
    Scope Scope,
    TaskVerificationStatus RunOutcome,
    ExperienceReuseBenefit ClaimedBenefit,
    ExperienceReuseBenefit Benefit,
    ReuseAttributionSource AttributionSource,
    string? ReviewerIdentity,
    string? EvaluatorId,
    Guid? VerificationRoundId,
    Guid? AssessmentId,
    string? Rationale,
    IReadOnlyList<Guid> EvidenceIds,
    DateTimeOffset? AttributedAt,
    ReuseMeasure Measure,
    string? TrialLabel,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ExperienceReuseExposure> Exposures);

/// <summary>
/// The disposition an <see cref="IExperienceReuseFeedbackStore"/> operation reached.
/// </summary>
public enum ExperienceReuseFeedbackStoreOutcome
{
    /// <summary>The submission and every one of its exposures were written in one transaction.</summary>
    Recorded,

    /// <summary>
    /// This <see cref="RecordedExperienceReuseFeedback.FeedbackId"/> is already stored with identical
    /// content. Nothing was written a second time and the stored submission is returned.
    /// </summary>
    AlreadyRecorded,

    /// <summary>
    /// This <see cref="RecordedExperienceReuseFeedback.FeedbackId"/> is already stored with differing
    /// content, in some scope. Nothing was written and the stored submission is not revealed.
    /// </summary>
    Conflict,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The submission was malformed. See the result's validation errors. Nothing was written.</summary>
    Invalid,
}

/// <summary>
/// The result of one <see cref="IExperienceReuseFeedbackStore.RecordAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Feedback">
/// The stored submission on <see cref="ExperienceReuseFeedbackStoreOutcome.Recorded"/> and
/// <see cref="ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded"/>; otherwise <see langword="null"/>.
/// On <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/> it is the <em>stored</em> submission
/// when the caller's authorization permits that submission's own scope, and <see langword="null"/>
/// otherwise -- a colliding ID must never hand back content from a scope the caller has no authority
/// over. Returning it when it is safe to is what lets a host whose retry was refused still see which
/// records the stored submission named.
/// </param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceReuseFeedbackStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceReuseFeedbackStoreResult(
    ExperienceReuseFeedbackStoreOutcome Outcome,
    RecordedExperienceReuseFeedback? Feedback,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// Port for the append-only ledger of reuse feedback: one row per submission and one per record it was
/// exposed to.
/// </summary>
/// <remarks>
/// <para>
/// The ledger records <em>exposure and attribution</em>. It never moves a confidence score: that
/// happens only through <c>ExperienceLifecycleService.ApplyEvidenceAsync</c>, after this ledger has
/// been written, and only for the exposures Core marked attributed.
/// </para>
/// <para>
/// Expected conditions return typed results; infrastructure failures throw
/// <see cref="ExperienceStoreException"/>; caller cancellation surfaces as an unwrapped
/// <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface IExperienceReuseFeedbackStore
{
    /// <summary>
    /// Writes one submission and all of its exposures in a single transaction: both or neither.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordedExperienceReuseFeedback.FeedbackId"/> is the idempotency key. An identical
    /// resubmission is <see cref="ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded"/> and writes
    /// nothing; one that differs in any stored field, or in its set of exposed records, is
    /// <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/> and writes nothing. Nothing is ever
    /// updated or deleted.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do. Applied to <see cref="RecordedExperienceReuseFeedback.Scope"/>.</param>
    /// <param name="feedback">The submission, with Core's attribution decision already made.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, and the stored submission when there is one to report.</returns>
    Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
        AuthorizationContext authorization,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken);
}

/// <summary>
/// What became of one exposed record once the feedback was recorded.
/// </summary>
public enum ExperienceExposureDisposition
{
    /// <summary>
    /// The exposure was recorded and nothing else was attempted, because no accepted attribution named
    /// this record. The honest default, and what every record of an unattributed submission gets.
    /// </summary>
    ExposureOnly,

    /// <summary>
    /// Confidence evidence was accepted for this record. Read <see cref="ExperienceExposureResult.Counted"/>
    /// to tell a first submission for its independence key from a later one that was stored and counted
    /// zero times -- both are accepted, and both are in the audit trail.
    /// </summary>
    EvidenceApplied,

    /// <summary>
    /// The record's status does not accept confidence evidence -- it is revoked, quarantined, stale,
    /// superseded, or still a candidate. The exposure is recorded; nothing was written for the record.
    /// </summary>
    Ineligible,

    /// <summary>
    /// No such record within the feedback's scope. The exposure is recorded as unresolved and nothing
    /// was submitted for it. A record in another scope is reported identically.
    /// </summary>
    Unresolved,

    /// <summary>
    /// The confidence path refused the submission outright -- a conflicting evidence ID, or a malformed
    /// derived request. Nothing was written for this record and repeating the call changes nothing.
    /// </summary>
    Refused,

    /// <summary>
    /// The submission for this record did not land and can be retried: a lost revision race, or a
    /// storage infrastructure failure. Every other record in the same submission is unaffected, and
    /// resubmitting the same feedback ID re-derives the same evidence ID, so a retry converges.
    /// </summary>
    Failed,
}

/// <summary>
/// What one exposed record's entry in a feedback submission came to.
/// </summary>
/// <param name="ExperienceId">The exposed record.</param>
/// <param name="Disposition">What became of it.</param>
/// <param name="EvidenceId">The evidence ID derived for it, when attribution named it; otherwise <see langword="null"/>.</param>
/// <param name="Counted">Whether the evidence moved a counter. <see langword="false"/> for an accepted submission whose independence key was already taken.</param>
/// <param name="ReuseConfidence">The record's reuse confidence after this call, when evidence was applied; otherwise <see langword="null"/>.</param>
/// <param name="Status">The record's status after this call, or the status that refused the evidence; <see langword="null"/> when neither is known.</param>
/// <param name="Retryable">Whether repeating the submission could still land this record's evidence.</param>
/// <param name="Reason">Optional, auditable, content-free explanation.</param>
public sealed record ExperienceExposureResult(
    Guid ExperienceId,
    ExperienceExposureDisposition Disposition,
    Guid? EvidenceId,
    bool Counted,
    double? ReuseConfidence,
    ExperienceStatus? Status,
    bool Retryable,
    string? Reason);

/// <summary>
/// The disposition one reuse-feedback submission reached.
/// </summary>
public enum ExperienceReuseFeedbackOutcome
{
    /// <summary>
    /// The exposure was recorded. Read the per-record results for what each exposed record came to: a
    /// submission is <see cref="Recorded"/> whether it moved every score, some of them, or none.
    /// </summary>
    Recorded,

    /// <summary>
    /// This feedback ID was already recorded with identical content. The ledger is unchanged; the
    /// per-record results come from replaying the (idempotent) confidence submissions, which is also
    /// how a partially failed submission is retried.
    /// </summary>
    AlreadyRecorded,

    /// <summary>This feedback ID is already recorded with different content. Nothing was written.</summary>
    Conflict,

    /// <summary>The request scope lies outside the host-established authorization. Nothing was written and no storage was accessed.</summary>
    Denied,

    /// <summary>The submission was malformed. See <c>Errors</c>. Nothing was written.</summary>
    Invalid,
}

/// <summary>
/// The result of recording one reuse-feedback submission.
/// </summary>
/// <param name="Outcome">What happened to the submission as a whole.</param>
/// <param name="FeedbackId">The submission's ID, echoed so a caller reconciling retries needs nothing else.</param>
/// <param name="Benefit">What the accepted attribution established, or <see cref="ExperienceReuseBenefit.Unknown"/>.</param>
/// <param name="AttributionSource">Which attribution shape was accepted, if either.</param>
/// <param name="Exposures">One entry per exposed record, in the order the caller listed them. Empty when nothing was written.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceReuseFeedbackOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="Reason">Optional, auditable, content-free explanation of a refusal.</param>
public sealed record ExperienceReuseFeedbackResult(
    ExperienceReuseFeedbackOutcome Outcome,
    Guid FeedbackId,
    ExperienceReuseBenefit Benefit,
    ReuseAttributionSource AttributionSource,
    IReadOnlyList<ExperienceExposureResult> Exposures,
    IReadOnlyList<StoreValidationError> Errors,
    string? Reason = null)
{
    /// <summary>
    /// Whether any exposed record's confidence submission can still be retried. The exposure itself is
    /// durable either way, so retrying means resubmitting the identical feedback: the ledger write is a
    /// no-op and the outstanding evidence submissions converge on their derived IDs.
    /// </summary>
    public bool IsRetryable => Exposures.Any(exposure => exposure.Retryable);
}
