using System.Globalization;
using System.Security.Cryptography;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Diagnostics;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Feedback;

/// <summary>
/// Records what happened in a run that stored experience was injected into, and -- only when the
/// submission carries attribution this library accepts -- turns that into confidence evidence for each
/// attributed record, through the ordinary evidence path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exposure is not attribution, and exposure alone moves nothing.</b> A submission with no
/// <see cref="HumanReuseAssessment"/> and no <see cref="ComparativeEvaluationResult"/> is written to the
/// feedback ledger with benefit <see cref="ExperienceReuseBenefit.Unknown"/>, and no record's
/// confidence, counters, or status changes. That is the honest answer to "records were injected and the
/// run succeeded", not a failure to do something: the run succeeding and the records being present are
/// two facts, and nothing in them attributes one to the other.
/// </para>
/// <para>
/// <b>A caller cannot assert benefit into existence.</b>
/// <see cref="ExperienceReuseFeedback.ClaimedBenefit"/> is recorded verbatim, for audit and for later
/// analysis, and is never acted on. Exactly two shapes move a score: an authorized
/// <see cref="HumanReuseAssessment"/>, whose reviewer is the host's
/// <see cref="AuthorizationContext.PrincipalId"/> and never a field on the submission, and a
/// <see cref="ComparativeEvaluationResult"/> that carries its evidence, its verification round, the run
/// it evaluated, and the records it attributes the difference to.
/// </para>
/// <para>
/// <b>Improvement supports; harm contradicts.</b> Attributed improvement submits
/// <see cref="ConfidenceEvidenceKind.Supporting"/> evidence for each attributed record; attributed harm
/// submits <see cref="ConfidenceEvidenceKind.Contradicting"/> evidence, which moves a live record to
/// <see cref="ExperienceStatus.Contested"/> in the same transaction that records the evidence. Nothing
/// is ever deleted, and the record's own history carries the reason.
/// </para>
/// <para>
/// <b>Everything goes through the existing evidence path.</b>
/// <see cref="ExperienceLifecycleService.ApplyEvidenceAsync"/> is the only thing that moves confidence,
/// so independence keying, duplicate suppression, the revision guard, the eligibility gate, and the
/// audit trail all apply here unchanged. This service adds no rule of its own about what a score may
/// do.
/// </para>
/// <para>
/// <b>Derived IDs make a retry converge.</b> Each attributed record's
/// <see cref="ApplyConfidenceEvidenceRequest.EvidenceId"/> and
/// <see cref="ApplyConfidenceEvidenceRequest.EventId"/> are derived by hash from the feedback ID and the
/// experience ID, and the submission's <see cref="ApplyConfidenceEvidenceRequest.OccurredAt"/> is the
/// caller's <see cref="ExperienceReuseFeedback.ObservedAt"/>. Resubmitting the same feedback therefore
/// re-derives the same identifiers and replays rather than double-counting -- which is exactly how a
/// partial failure is retried.
/// </para>
/// <para>
/// <b>The ledger is written first, and one record's failure is not the others'.</b> The exposure rows
/// are committed before any confidence submission, so what the run saw is durable even if every score
/// submission then fails. Each record is then submitted independently: one failing leaves the rest
/// applied and is reported as <see cref="ExperienceExposureDisposition.Failed"/> with
/// <see cref="ExperienceExposureResult.Retryable"/> set. Cancellation part-way through is reported the
/// same way rather than thrown, because the ledger is already durable and some records may already have
/// moved -- throwing would leave the caller unable to find out which.
/// </para>
/// <para>
/// <b>A failed attribution costs the attribution, not the exposure.</b> An attribution that does not
/// meet its evidence requirements -- no assessment ID behind a human judgement, no evidence behind a
/// comparison, an attribution of <see cref="ExperienceReuseBenefit.Unknown"/> -- is dropped, and the
/// submission is recorded with benefit <see cref="ExperienceReuseBenefit.Unknown"/> and a reason saying
/// what was refused. Only a structurally incoherent submission is
/// <see cref="ExperienceReuseFeedbackOutcome.Invalid"/> with nothing written: no feedback ID, no records,
/// an attribution naming a record the run never saw, or a comparative result about a different run.
/// </para>
/// <para>
/// <b>The fan-out is bounded by the submission, and by nothing else.</b> Each attributed record costs a
/// scoped read plus a full transaction, run sequentially, with only the caller's
/// <see cref="CancellationToken"/> as a time bound -- there is deliberately no internal budget, unlike
/// retrieval's. Retrieval's timeout is safe because abandoning it yields an empty result and the agent
/// runs on; abandoning half a fan-out would leave some records moved and others not, with no way to say
/// which from a timeout alone, so the bound that exists is on <em>size</em>:
/// <see cref="ExperienceReuseFeedback.MaxExposedRecords"/>. Pass a token with a deadline if the call
/// needs one; what has been decided by then is still reported.
/// </para>
/// <para>
/// <b>An attribution's identifiers are verified before the ledger is written</b>, with the confidence
/// path's own rule (unless the host opted out): <see cref="ExperienceReuseFeedback.RunId"/> must be a run the
/// library knows in the feedback's scope, a comparative result's round the one that run was finalized with,
/// and a human assessment must present an assessment token minted for this scope, run, reviewer and
/// direction, covering every attributed record, whose ID is its <see cref="HumanReuseAssessment.AssessmentId"/>.
/// A failure degrades the attribution like any other. Each record's evidence is then verified again, and the
/// token spent, by the confidence path. Exposure without attribution is recorded against the run as given:
/// it keys nothing.
/// </para>
/// </remarks>
public sealed class ExperienceReuseFeedbackService
{
    /// <summary>
    /// The producer recorded on evidence a human assessment produced. The reviewer identity is carried
    /// separately, by the evidence path, from the host's authorization context.
    /// </summary>
    public const string HumanAssessmentProducer = "experience-reuse-feedback/human-assessment";

    private static readonly Guid DerivationNamespace = new("3f5a1d62-8c04-4b91-a7e3-6d2f0b48c915");

    private const byte EvidenceIdTag = 1;
    private const byte EventIdTag = 2;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceExposureResult> NoExposures = [];

    private readonly IExperienceReuseFeedbackStore _store;
    private readonly ExperienceLifecycleService _lifecycleService;

    /// <summary>Creates a feedback service over the feedback ledger and Core's lifecycle owner.</summary>
    /// <param name="store">The append-only ledger the exposure is written to, before any score moves.</param>
    /// <param name="lifecycleService">The one path that moves confidence. Called once per attributed record.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ExperienceReuseFeedbackService(IExperienceReuseFeedbackStore store, ExperienceLifecycleService lifecycleService)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lifecycleService);

        _store = store;
        _lifecycleService = lifecycleService;
    }

    /// <summary>
    /// The <see cref="ApplyConfidenceEvidenceRequest.EvidenceId"/> feedback <paramref name="feedbackId"/>
    /// submits for <paramref name="experienceId"/>, derived from both so a retry re-derives the same ID
    /// and converges instead of counting the observation twice.
    /// </summary>
    /// <param name="feedbackId">The submission.</param>
    /// <param name="experienceId">The attributed record.</param>
    /// <returns>The derived evidence ID.</returns>
    public static Guid EvidenceIdFor(Guid feedbackId, Guid experienceId) => Derive(feedbackId, experienceId, EvidenceIdTag);

    /// <summary>
    /// The <see cref="ApplyConfidenceEvidenceRequest.EventId"/> feedback <paramref name="feedbackId"/>
    /// commits for <paramref name="experienceId"/>, derived for the same reason: a retry must not commit
    /// a second lifecycle event for one observation.
    /// </summary>
    /// <param name="feedbackId">The submission.</param>
    /// <param name="experienceId">The attributed record.</param>
    /// <returns>The derived event ID.</returns>
    public static Guid EventIdFor(Guid feedbackId, Guid experienceId) => Derive(feedbackId, experienceId, EventIdTag);

    /// <summary>
    /// Records one feedback submission: validates it, decides whether it carries attribution this
    /// library accepts, writes the exposure ledger, and then submits confidence evidence for each
    /// attributed record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is written when the request is malformed
    /// (<see cref="ExperienceReuseFeedbackOutcome.Invalid"/>), when
    /// <paramref name="feedback"/>'s scope lies outside <paramref name="authorization"/>
    /// (<see cref="ExperienceReuseFeedbackOutcome.Denied"/>, decided before any storage is touched), or
    /// when the feedback ID is already stored with different content
    /// (<see cref="ExperienceReuseFeedbackOutcome.Conflict"/>).
    /// </para>
    /// <para>
    /// An exposed record that does not exist in the feedback's scope is reported
    /// <see cref="ExperienceExposureDisposition.Unresolved"/>, and one whose status refuses evidence --
    /// revoked, quarantined, stale, superseded, or still a candidate --
    /// <see cref="ExperienceExposureDisposition.Ineligible"/>. Both keep their exposure row; neither
    /// writes anything for the record.
    /// </para>
    /// <para>
    /// Storage infrastructure failures from the <em>ledger</em> write propagate as
    /// <see cref="ExperienceStoreException"/>: nothing was recorded, so there is nothing to report
    /// per record. A failure from an individual confidence submission does not, because the exposure is
    /// already durable: it is reported as that record's
    /// <see cref="ExperienceExposureDisposition.Failed"/> and the remaining records are still submitted.
    /// Cancellation once the ledger has been written is reported the same way, for the same reason.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do, and the reviewer identity for a human assessment.</param>
    /// <param name="feedback">The submission.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened to the submission, and to each record it named.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="feedback"/>, or the feedback's <see cref="ExperienceReuseFeedback.Scope"/>, is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">The feedback ledger write, or the read that verifies an attribution's run before it, failed. Nothing was recorded and no score moved.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the ledger write completed, so nothing was recorded. Cancellation after it is reported per record instead.</exception>
    public async Task<ExperienceReuseFeedbackResult> RecordAsync(
        AuthorizationContext authorization,
        ExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.ReuseFeedback, cancellationToken);

        ExperienceReuseFeedbackResult result;
        try
        {
            // The submission's own identifier and the run it is about are both on the request, so both
            // are on the span before the call and survive a throw.
            ArgumentNullException.ThrowIfNull(feedback);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.FeedbackIdAttribute, feedback.FeedbackId.ToString("D"));
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.RunIdAttribute, feedback.RunId.ToString("D"));

            result = await RecordCoreAsync(authorization, feedback, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.ReuseFeedback, ex);
            throw;
        }

        // Per-record dispositions stay on the typed result: they are a list whose length is the
        // submission's, which is exactly the kind of thing a span attribute must not become.
        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.ReuseFeedback, result.Outcome.ToString());
        return result;
    }

    /// <summary>The body of <see cref="RecordAsync"/>, unchanged by instrumentation: it neither reads nor writes a span.</summary>
    private async Task<ExperienceReuseFeedbackResult> RecordCoreAsync(
        AuthorizationContext authorization,
        ExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(feedback.Scope, $"{nameof(feedback)}.{nameof(feedback.Scope)}");

        var validation = Validate(feedback, authorization);
        if (validation.Fatal.Count > 0)
        {
            // Structurally broken: there is no coherent exposure to record, so nothing is written.
            return Ended(ExperienceReuseFeedbackOutcome.Invalid, feedback.FeedbackId, validation.Fatal);
        }

        if (!authorization.Permits(feedback.Scope))
        {
            // Before any storage is touched: a run in a scope this caller has no authority over is not
            // a submission to record and then refuse, it is one that never happened here.
            return Ended(
                ExperienceReuseFeedbackOutcome.Denied,
                feedback.FeedbackId,
                NoErrors,
                "The feedback's scope lies outside the host-established authorization.");
        }

        // An attribution is only as good as the independence key it would produce, so under verification
        // its run, round and assessment token are checked here, before the ledger is written -- a forged
        // one is dropped like any other attribution that fails its evidence requirements, rather than
        // being stored as an attributed benefit that the confidence path then refuses record by record.
        if (validation.Degrading.Count == 0
            && feedback is { HumanAssessment: not null } or { ComparativeEvaluation: not null }
            && _lifecycleService.Independence.Verifies)
        {
            validation.Degrading.AddRange(
                await VerifyAttributionAsync(authorization, feedback, cancellationToken).ConfigureAwait(false));
        }

        // An attribution that failed its evidence requirements is dropped, not fatal: the exposure is
        // still a fact about the run, and losing it to protect a score nothing was going to move is the
        // worse trade. The submission is recorded with benefit Unknown and the reason says why.
        var attribution = validation.Degrading.Count > 0
            ? Attribution.None
            : Attribution.From(feedback, authorization);
        var degradedReason = validation.Degrading.Count > 0
            ? "The attribution did not meet its evidence requirements, so the exposure was recorded with benefit "
                + $"{ExperienceReuseBenefit.Unknown} and no confidence submission: "
                + string.Join("; ", validation.Degrading.Select(error => $"{error.Path} {error.Message}"))
            : null;

        var submission = ToLedgerSubmission(feedback, attribution);

        var stored = await _store.RecordAsync(authorization, submission, cancellationToken).ConfigureAwait(false);

        switch (stored.Outcome)
        {
            case ExperienceReuseFeedbackStoreOutcome.Recorded:
            case ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded:
                break;

            case ExperienceReuseFeedbackStoreOutcome.Conflict:
                // Nothing was written. When the store could safely hand back what *is* stored under this
                // ID, report those records so a host whose retry was refused can still see what the
                // original submission named rather than being left with no way to ask.
                return new(
                    ExperienceReuseFeedbackOutcome.Conflict,
                    feedback.FeedbackId,
                    ExperienceReuseBenefit.Unknown,
                    ReuseAttributionSource.None,
                    stored.Feedback is { } conflicting
                        ? [.. conflicting.Exposures.Select(exposure => new ExperienceExposureResult(
                            exposure.ExperienceId,
                            ExperienceExposureDisposition.Refused,
                            exposure.EvidenceId,
                            Counted: false,
                            ReuseConfidence: null,
                            Status: null,
                            Retryable: false,
                            "Recorded by the submission already stored under this feedback ID, not by this call."))]
                        : NoExposures,
                    NoErrors,
                    "This feedback ID is already recorded with different content; nothing was written.");

            case ExperienceReuseFeedbackStoreOutcome.Denied:
                return Ended(
                    ExperienceReuseFeedbackOutcome.Denied,
                    feedback.FeedbackId,
                    NoErrors,
                    "The feedback's scope lies outside the host-established authorization.");

            default:
                return Ended(ExperienceReuseFeedbackOutcome.Invalid, feedback.FeedbackId, stored.Errors);
        }

        var results = new List<ExperienceExposureResult>(submission.Exposures.Count);
        for (var index = 0; index < submission.Exposures.Count; index++)
        {
            var exposure = submission.Exposures[index];

            if (!exposure.Attributed)
            {
                results.Add(new(
                    exposure.ExperienceId,
                    ExperienceExposureDisposition.ExposureOnly,
                    EvidenceId: null,
                    Counted: false,
                    ReuseConfidence: null,
                    Status: null,
                    Retryable: false,
                    Reason: attribution.Source == ReuseAttributionSource.None
                        ? degradedReason ?? "Exposure without attribution evidence moves nothing."
                        : "The accepted attribution did not name this record."));
                continue;
            }

            try
            {
                results.Add(await SubmitEvidenceAsync(authorization, feedback, attribution, exposure, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // The ledger is durable and some records may already have moved, so throwing here would
                // leave the caller unable to find out which. Report what was decided and mark the rest
                // retryable: resubmitting the same feedback re-derives the same IDs and converges.
                for (var remaining = index; remaining < submission.Exposures.Count; remaining++)
                {
                    var abandoned = submission.Exposures[remaining];
                    results.Add(new(
                        abandoned.ExperienceId,
                        ExperienceExposureDisposition.Failed,
                        abandoned.EvidenceId,
                        Counted: false,
                        ReuseConfidence: null,
                        Status: null,
                        Retryable: true,
                        "Cancelled before this record's evidence was submitted; resubmit the same feedback ID."));
                }

                break;
            }
        }

        return new(
            stored.Outcome == ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded
                ? ExperienceReuseFeedbackOutcome.AlreadyRecorded
                : ExperienceReuseFeedbackOutcome.Recorded,
            feedback.FeedbackId,
            attribution.Benefit,
            attribution.Source,
            results,
            NoErrors,
            degradedReason);
    }

    /// <summary>
    /// Submits one attributed record's evidence through the confidence path and maps what came back
    /// onto the exposure's disposition. Every outcome the path can reach is answered here, because a
    /// record whose submission did not land must be distinguishable from one that was never attributed.
    /// </summary>
    private async Task<ExperienceExposureResult> SubmitEvidenceAsync(
        AuthorizationContext authorization,
        ExperienceReuseFeedback feedback,
        Attribution attribution,
        ExperienceReuseExposure exposure,
        CancellationToken cancellationToken)
    {
        // Never re-derived here: the ledger already stores the ID this submission must use, and deriving
        // a second one would quietly break the convergence the whole retry story rests on. An attributed
        // exposure always carries it -- the store port refuses one that does not.
        var evidenceId = exposure.EvidenceId!.Value;

        var request = new ApplyConfidenceEvidenceRequest(
            EventId: EventIdFor(feedback.FeedbackId, exposure.ExperienceId),
            ExperienceId: exposure.ExperienceId,
            Scope: feedback.Scope,
            EvidenceId: evidenceId,
            Kind: attribution.Benefit == ExperienceReuseBenefit.Harmed
                ? ConfidenceEvidenceKind.Contradicting
                : ConfidenceEvidenceKind.Supporting,
            Source: attribution.Source == ReuseAttributionSource.HumanAssessment
                ? ConfidenceEvidenceSource.Human
                : ConfidenceEvidenceSource.Machine,
            RunId: feedback.RunId,
            VerificationRoundId: attribution.EvidenceRoundId,
            Reason: ReasonFor(feedback.FeedbackId, attribution),
            Producer: attribution.Producer
                ?? throw new InvalidOperationException("An attributed exposure cannot come from a submission that carried no attribution."),
            // The caller's own observation time, never a fresh clock read: it is part of the stored
            // event's identity, so a retry that regenerated it would stop being a retry.
            OccurredAt: feedback.ObservedAt,
            Detail: attribution.Rationale,
            // Re-verified, and spent for this record, by the confidence path itself.
            AssessmentToken: attribution.AssessmentToken);

        ApplyConfidenceEvidenceResult applied;
        try
        {
            // The public, instrumented sibling: a submission exposing five records really does apply
            // confidence evidence five times, and an operator who cannot see those five -- or the one
            // of them that failed -- cannot tell a working feedback loop from a stuck one.
            applied = await _lifecycleService.ApplyEvidenceAsync(authorization, request, cancellationToken).ConfigureAwait(false);
        }
        catch (ExperienceStoreException ex)
        {
            // The exposure is already durable, so this is one record's retryable failure rather than the
            // submission's. Every other exposed record is still submitted.
            return new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.Failed,
                evidenceId,
                Counted: false,
                ReuseConfidence: null,
                Status: null,
                Retryable: true,
                Reason: ex.Message);
        }

        return applied.Outcome switch
        {
            ConfidenceUpdateOutcome.Applied => new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.EvidenceApplied,
                evidenceId,
                applied.Counted,
                applied.ReuseConfidence,
                applied.Status,
                Retryable: false,
                Reason: applied.Counted ? null : "The same run already produced evidence for this record; recorded, not counted."),

            ConfidenceUpdateOutcome.Ineligible => new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.Ineligible,
                evidenceId,
                Counted: false,
                ReuseConfidence: null,
                applied.Status,
                Retryable: false,
                applied.Reason),

            // The confidence path's own reason is the specific one -- "readable only through a sharing
            // grant, which never confers writing to it" is a different fact from "not here at all", and
            // overwriting it would hide the one case a host can actually act on.
            ConfidenceUpdateOutcome.NotFound or ConfidenceUpdateOutcome.Denied => new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.Unresolved,
                evidenceId,
                Counted: false,
                ReuseConfidence: null,
                Status: null,
                Retryable: false,
                applied.Reason ?? "No such record within the feedback's scope."),

            // A lost revision race and a status that moved under the read are both answered by
            // resubmitting the identical feedback: the derived IDs are the same, so the recomputation
            // lands against the record's current revision.
            ConfidenceUpdateOutcome.StaleRevision or ConfidenceUpdateOutcome.StatusMismatch => new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.Failed,
                evidenceId,
                Counted: false,
                ReuseConfidence: null,
                applied.Status,
                Retryable: true,
                "The record moved between the read and the commit; resubmit the same feedback ID."),

            // The record was erased after this submission's exposure was written (the ledger refuses a
            // submission that names a tombstone up front, so only a later erasure reaches here).
            // ExperienceExposureDisposition has no erased member, and Refused is already its terminal
            // one -- "nothing was written and repeating the call changes nothing" -- which is exactly
            // what a tombstone means. It must never be Failed: that disposition promises a retry can land.
            ConfidenceUpdateOutcome.Deleted => new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.Refused,
                evidenceId,
                Counted: false,
                ReuseConfidence: null,
                Status: null,
                Retryable: false,
                applied.Reason ?? "The record was erased after this exposure was recorded; no evidence can be applied to it."),

            _ => new(
                exposure.ExperienceId,
                ExperienceExposureDisposition.Refused,
                evidenceId,
                Counted: false,
                ReuseConfidence: null,
                applied.Status,
                Retryable: false,
                applied.Errors.Count > 0
                    ? string.Join("; ", applied.Errors.Select(error => $"{error.Path}: {error.Message}"))
                    : applied.Reason ?? "The confidence path refused this submission."),
        };
    }

    private static RecordedExperienceReuseFeedback ToLedgerSubmission(ExperienceReuseFeedback feedback, Attribution attribution)
    {
        // Ordered by record, not as the caller listed them. The set of exposed records is the fact; the
        // order they were typed in is not, and letting it into the stored submission would make two
        // hosts submitting the same feedback with the records in different orders collide as a conflict
        // that no retry could ever resolve.
        var exposures = new List<ExperienceReuseExposure>(feedback.ExposedExperienceIds.Count);
        foreach (var experienceId in feedback.ExposedExperienceIds.Order())
        {
            var attributed = attribution.Attributes(experienceId);
            exposures.Add(new(
                experienceId,
                attributed,
                attributed ? EvidenceIdFor(feedback.FeedbackId, experienceId) : null));
        }

        return new(
            feedback.FeedbackId,
            feedback.RunId,
            feedback.Scope,
            feedback.RunOutcome,
            feedback.ClaimedBenefit,
            attribution.Benefit,
            attribution.Source,
            attribution.ReviewerIdentity,
            attribution.EvaluatorId,
            attribution.VerificationRoundId,
            attribution.AssessmentId,
            attribution.Rationale,
            attribution.EvidenceIds,
            attribution.AttributedAt,
            feedback.Measure,
            feedback.TrialLabel,
            feedback.ObservedAt,
            exposures);
    }

    private static string ReasonFor(Guid feedbackId, Attribution attribution) => string.Create(
        CultureInfo.InvariantCulture,
        $"Reuse feedback {feedbackId:D} attributed {(attribution.Benefit == ExperienceReuseBenefit.Harmed ? "harm" : "improvement")} to this record.");

    private static ExperienceReuseFeedbackResult Ended(
        ExperienceReuseFeedbackOutcome outcome,
        Guid feedbackId,
        IReadOnlyList<StoreValidationError> errors,
        string? reason = null) => new(
            outcome,
            feedbackId,
            ExperienceReuseBenefit.Unknown,
            ReuseAttributionSource.None,
            NoExposures,
            errors,
            reason);

    /// <summary>
    /// Derives a stable identifier from the feedback, the record, and a per-purpose tag: SHA-256 over a
    /// fixed namespace and the three inputs, stamped with the RFC 9562 custom version (8) and variant.
    /// Same submission in, same identifiers out -- which is what makes a retry a retry.
    /// </summary>
    private static Guid Derive(Guid feedbackId, Guid experienceId, byte tag)
    {
        Span<byte> input = stackalloc byte[49];
        DerivationNamespace.TryWriteBytes(input[..16], bigEndian: true, out _);
        feedbackId.TryWriteBytes(input.Slice(16, 16), bigEndian: true, out _);
        experienceId.TryWriteBytes(input.Slice(32, 16), bigEndian: true, out _);
        input[48] = tag;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        var id = hash[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true);
    }

    /// <summary>
    /// The attribution decision, made once per submission: which shape was accepted, which way it
    /// points, which records it names, and the identifiers the derived evidence carries. Nothing here
    /// reads <see cref="ExperienceReuseFeedback.ClaimedBenefit"/>.
    /// </summary>
    private sealed record Attribution(
        ReuseAttributionSource Source,
        ExperienceReuseBenefit Benefit,
        IReadOnlyList<Guid> AttributedExperienceIds,
        string? ReviewerIdentity,
        string? EvaluatorId,
        Guid? VerificationRoundId,
        Guid? AssessmentId,
        string? Rationale,
        IReadOnlyList<Guid> EvidenceIds,
        DateTimeOffset? AttributedAt,
        string? Producer,
        string? AssessmentToken = null)
    {
        /// <summary>
        /// No attribution: every field an attribution would carry is absent, including the producer,
        /// because nothing is submitted. A non-null producer here would be a plausible-looking value on
        /// a path that must never reach the confidence service.
        /// </summary>
        public static Attribution None { get; } = new(
            ReuseAttributionSource.None,
            ExperienceReuseBenefit.Unknown,
            [],
            ReviewerIdentity: null,
            EvaluatorId: null,
            VerificationRoundId: null,
            AssessmentId: null,
            Rationale: null,
            EvidenceIds: [],
            AttributedAt: null,
            Producer: null);

        public static Attribution From(ExperienceReuseFeedback feedback, AuthorizationContext authorization) => feedback switch
        {
            { HumanAssessment: { } assessment } => new(
                ReuseAttributionSource.HumanAssessment,
                assessment.Benefit,
                assessment.AttributedExperienceIds,
                // The reviewer is the host's principal and nothing else: it is half of the human
                // independence key, so a submission never gets to name it.
                authorization.PrincipalId,
                EvaluatorId: null,
                // Stored for audit, and deliberately not passed to the confidence path: human evidence
                // is counted once per reviewer and run, and the path rejects a round on it outright.
                assessment.VerificationRoundId,
                assessment.AssessmentId,
                assessment.Rationale,
                EvidenceIds: [],
                assessment.AssessedAt,
                HumanAssessmentProducer,
                assessment.AssessmentToken),

            { ComparativeEvaluation: { } comparative } => new(
                ReuseAttributionSource.ComparativeEvaluation,
                comparative.Benefit,
                comparative.AttributedExperienceIds,
                ReviewerIdentity: null,
                comparative.EvaluatorId,
                comparative.VerificationRoundId,
                AssessmentId: null,
                comparative.Summary,
                // Stored, so an auditor sees what the conclusion rested on rather than only the
                // evaluator's own summary of it.
                [.. comparative.Evidence.Select(evidence => evidence.EvidenceId)],
                comparative.EvaluatedAt,
                comparative.EvaluatorId),

            _ => None,
        };

        public bool Attributes(Guid experienceId) =>
            Source != ReuseAttributionSource.None && AttributedExperienceIds.Contains(experienceId);

        /// <summary>
        /// The verification round the derived confidence evidence is keyed on: the comparative result's,
        /// and never a human assessment's, whose round is audit only.
        /// </summary>
        public Guid? EvidenceRoundId =>
            Source == ReuseAttributionSource.ComparativeEvaluation ? VerificationRoundId : null;
    }

    /// <summary>
    /// Everything that can be decided from the submission's own shape, settled before the ledger is
    /// touched. The attribution rules are here rather than in the store because they are the story's
    /// whole point: what counts as evidence is a Core decision, and an adapter must never be able to
    /// promote a claim into one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Errors are separated by what losing the submission would cost. <b>Fatal</b> is a submission that
    /// cannot be recorded coherently at all -- no feedback ID to be idempotent on, no records to record
    /// an exposure for, an attribution naming records the run never saw, or a comparative result about a
    /// different run. Those are <see cref="ExperienceReuseFeedbackOutcome.Invalid"/> with nothing
    /// written.
    /// </para>
    /// <para>
    /// <b>Degrading</b> is an attribution that simply failed its evidence requirements -- no assessment
    /// ID, no evidence behind a comparison, a blank rationale, an attribution of
    /// <see cref="ExperienceReuseBenefit.Unknown"/>. The exposure is still a true fact about the run and
    /// is recorded, with benefit <see cref="ExperienceReuseBenefit.Unknown"/> and no confidence
    /// submission. Dropping the exposure to punish a bad attribution would lose the one thing that was
    /// never in doubt.
    /// </para>
    /// </remarks>
    private static ValidationOutcome Validate(ExperienceReuseFeedback feedback, AuthorizationContext authorization)
    {
        var fatal = new List<StoreValidationError>();
        var degrading = new List<StoreValidationError>();

        if (feedback.FeedbackId == Guid.Empty)
        {
            fatal.Add(new(nameof(feedback.FeedbackId), "must not be an empty GUID: it is the submission's idempotency key."));
        }

        if (feedback.RunId == Guid.Empty)
        {
            fatal.Add(new(nameof(feedback.RunId), "must name the run the records were injected into."));
        }

        if (!Enum.IsDefined(feedback.RunOutcome))
        {
            fatal.Add(new(nameof(feedback.RunOutcome), "must be a defined task verification status."));
        }

        if (!Enum.IsDefined(feedback.ClaimedBenefit))
        {
            fatal.Add(new(nameof(feedback.ClaimedBenefit), "must be a defined benefit."));
        }

        if (feedback.ObservedAt == default)
        {
            fatal.Add(new(nameof(feedback.ObservedAt), "must be set to when the feedback was observed."));
        }

        if (feedback.TrialLabel is { } trial && string.IsNullOrWhiteSpace(trial))
        {
            fatal.Add(new(nameof(feedback.TrialLabel), "must be non-blank when supplied; omit it instead."));
        }

        ValidateMeasure(feedback.Measure, fatal);
        var exposed = ValidateExposed(feedback.ExposedExperienceIds, fatal);

        if (feedback is { HumanAssessment: not null, ComparativeEvaluation: not null })
        {
            fatal.Add(new(
                nameof(feedback.HumanAssessment),
                "a submission carries at most one attribution: a human assessment or a comparative evaluation, never both."));
            return new(fatal, degrading);
        }

        if (feedback.HumanAssessment is { } assessment)
        {
            ValidateHumanAssessment(assessment, exposed, authorization, fatal, degrading);
        }
        else if (feedback.ComparativeEvaluation is { } comparative)
        {
            ValidateComparative(comparative, feedback.RunId, exposed, fatal, degrading);
        }

        return new(fatal, degrading);
    }

    private static void ValidateMeasure(ReuseMeasure measure, List<StoreValidationError> fatal)
    {
        if (measure is null)
        {
            fatal.Add(new(nameof(ExperienceReuseFeedback.Measure), "must name what was measured about the run."));
            return;
        }

        if (string.IsNullOrWhiteSpace(measure.Kind))
        {
            fatal.Add(new("Measure.Kind", "must be a non-blank name for what was measured."));
        }

        if (!double.IsFinite(measure.Value))
        {
            fatal.Add(new("Measure.Value", "must be a finite number."));
        }
    }

    private static HashSet<Guid> ValidateExposed(IReadOnlyList<Guid> exposedIds, List<StoreValidationError> fatal)
    {
        const string Path = nameof(ExperienceReuseFeedback.ExposedExperienceIds);

        var exposed = new HashSet<Guid>();
        if (exposedIds is null)
        {
            fatal.Add(new(Path, "must name the records the run was exposed to."));
            return exposed;
        }

        if (exposedIds.Count == 0)
        {
            fatal.Add(new(Path, "must name at least one record: feedback about no exposure records nothing."));
            return exposed;
        }

        if (exposedIds.Count > ExperienceReuseFeedback.MaxExposedRecords)
        {
            fatal.Add(new(Path, $"must name at most {ExperienceReuseFeedback.MaxExposedRecords} records."));
        }

        foreach (var experienceId in exposedIds)
        {
            if (experienceId == Guid.Empty)
            {
                fatal.Add(new(Path, "must not contain an empty GUID."));
            }
            else if (!exposed.Add(experienceId))
            {
                // One exposure row per record, so a repeated ID would be one row claiming to be two
                // exposures -- and, once attributed, one derived evidence ID submitted twice.
                fatal.Add(new(Path, "must not name the same record twice."));
            }
        }

        return exposed;
    }

    private static void ValidateHumanAssessment(
        HumanReuseAssessment assessment,
        HashSet<Guid> exposed,
        AuthorizationContext authorization,
        List<StoreValidationError> fatal,
        List<StoreValidationError> degrading)
    {
        const string Path = nameof(ExperienceReuseFeedback.HumanAssessment);

        ValidateAttributedIds(assessment.AttributedExperienceIds, exposed, $"{Path}.{nameof(assessment.AttributedExperienceIds)}", fatal, degrading);
        ValidateBenefit(assessment.Benefit, $"{Path}.{nameof(assessment.Benefit)}", degrading);

        // The host-established identity of the review this came out of. Without it a human attribution
        // is a benefit, a list of record IDs, and a string -- which is exactly the bare claim this story
        // refuses from anyone else.
        if (assessment.AssessmentId == Guid.Empty)
        {
            degrading.Add(new(
                $"{Path}.{nameof(assessment.AssessmentId)}",
                "must name the host-established review this judgement came out of."));
        }

        if (assessment.VerificationRoundId is { } round && round == Guid.Empty)
        {
            degrading.Add(new(
                $"{Path}.{nameof(assessment.VerificationRoundId)}",
                "must name a verification round or be omitted; an empty GUID is neither."));
        }

        if (string.IsNullOrWhiteSpace(assessment.Rationale))
        {
            degrading.Add(new($"{Path}.{nameof(assessment.Rationale)}", "must be a non-blank, auditable rationale."));
        }

        if (assessment.AssessedAt == default)
        {
            degrading.Add(new($"{Path}.{nameof(assessment.AssessedAt)}", "must be set to when the assessment was made."));
        }

        // The reviewer is the whole of the human independence rule and it comes from the authorization
        // context, so it is checked here rather than discovered as a refusal after the ledger is written.
        if (string.IsNullOrWhiteSpace(authorization.PrincipalId))
        {
            degrading.Add(new(
                "Authorization.PrincipalId",
                "must be non-blank for a human assessment: it is the reviewer the attribution is counted under."));
        }
        else if (!string.Equals(authorization.PrincipalId, authorization.PrincipalId.Trim(), StringComparison.Ordinal))
        {
            degrading.Add(new(
                "Authorization.PrincipalId",
                "must not have leading or trailing whitespace: it would be counted as a second, independent reviewer."));
        }
    }

    private static void ValidateComparative(
        ComparativeEvaluationResult comparative,
        Guid feedbackRunId,
        HashSet<Guid> exposed,
        List<StoreValidationError> fatal,
        List<StoreValidationError> degrading)
    {
        const string Path = nameof(ExperienceReuseFeedback.ComparativeEvaluation);

        ValidateAttributedIds(comparative.AttributedExperienceIds, exposed, $"{Path}.{nameof(comparative.AttributedExperienceIds)}", fatal, degrading);
        ValidateBenefit(comparative.Benefit, $"{Path}.{nameof(comparative.Benefit)}", degrading);

        if (comparative.RunId != feedbackRunId)
        {
            // Fatal rather than degrading: a result about a different run does not describe this
            // submission at all, so there is nothing coherent to record it beside.
            fatal.Add(new(
                $"{Path}.{nameof(comparative.RunId)}",
                "must be the run this feedback is about."));
        }

        if (string.IsNullOrWhiteSpace(comparative.EvaluatorId))
        {
            degrading.Add(new($"{Path}.{nameof(comparative.EvaluatorId)}", "must be a non-blank evaluator identity."));
        }

        if (comparative.VerificationRoundId == Guid.Empty)
        {
            degrading.Add(new(
                $"{Path}.{nameof(comparative.VerificationRoundId)}",
                "must name the verification round the comparison was made in: it is half of the machine independence key."));
        }

        ValidateComparativeEvidence(comparative, $"{Path}.{nameof(comparative.Evidence)}", degrading);

        if (string.IsNullOrWhiteSpace(comparative.Summary))
        {
            degrading.Add(new($"{Path}.{nameof(comparative.Summary)}", "must be a non-blank, auditable summary."));
        }

        if (comparative.EvaluatedAt == default)
        {
            degrading.Add(new($"{Path}.{nameof(comparative.EvaluatedAt)}", "must be set to when the comparison was made."));
        }
    }

    /// <summary>
    /// The evidence a comparative result claims to have reached its conclusion from. It is checked
    /// rather than merely counted: this library does not implement a comparative evaluator, so verifying
    /// the result it is given is the whole of what it can do, and evidence from some other round is not
    /// evidence about this comparison.
    /// </summary>
    private static void ValidateComparativeEvidence(
        ComparativeEvaluationResult comparative,
        string path,
        List<StoreValidationError> degrading)
    {
        if (comparative.Evidence is not { Count: > 0 })
        {
            degrading.Add(new(path, "must carry the evidence the comparison was reached from."));
            return;
        }

        var seen = new HashSet<Guid>();
        foreach (var evidence in comparative.Evidence)
        {
            if (evidence is null)
            {
                degrading.Add(new(path, "must not contain a null piece of evidence."));
                continue;
            }

            if (evidence.EvidenceId == Guid.Empty)
            {
                degrading.Add(new($"{path}.EvidenceId", "must not be an empty GUID."));
            }
            else if (!seen.Add(evidence.EvidenceId))
            {
                degrading.Add(new($"{path}.EvidenceId", "must not name the same piece of evidence twice."));
            }

            if (evidence.VerificationRoundId != comparative.VerificationRoundId)
            {
                degrading.Add(new(
                    $"{path}.VerificationRoundId",
                    "must be the round the result names: evidence from another round is not evidence about this comparison."));
            }
        }
    }

    private static void ValidateBenefit(ExperienceReuseBenefit benefit, string path, List<StoreValidationError> degrading)
    {
        if (!Enum.IsDefined(benefit))
        {
            degrading.Add(new(path, "must be a defined benefit."));
        }
        else if (benefit == ExperienceReuseBenefit.Unknown)
        {
            degrading.Add(new(
                path,
                "must attribute improvement or harm; an attribution of Unknown is not an attribution, so omit it instead."));
        }
    }

    private static void ValidateAttributedIds(
        IReadOnlyList<Guid> attributedIds,
        HashSet<Guid> exposed,
        string path,
        List<StoreValidationError> fatal,
        List<StoreValidationError> degrading)
    {
        if (attributedIds is not { Count: > 0 })
        {
            degrading.Add(new(path, "must name at least one exposed record."));
            return;
        }

        var seen = new HashSet<Guid>();
        foreach (var experienceId in attributedIds)
        {
            if (!seen.Add(experienceId))
            {
                degrading.Add(new(path, "must not name the same record twice."));
            }
            else if (!exposed.Contains(experienceId))
            {
                // Fatal: attribution naming a record the run never saw contradicts the exposure it is
                // attached to, so there is no coherent submission to record at all.
                fatal.Add(new(path, "must name only records the run was exposed to."));
            }
        }
    }

    /// <summary>
    /// The independence checks an attribution must pass before it is recorded as one, under verification:
    /// the run must be one the library knows in the feedback's scope; a comparative result's round must be
    /// the one finalization closed for that run; and a human assessment must carry an assessment token that
    /// verifies for this scope, run, reviewer and direction, covers every attributed record, and whose ID
    /// is the assessment's. Every failure degrades the attribution; none is fatal, because the exposure is
    /// still a true fact about the run.
    /// </summary>
    /// <remarks>
    /// The same checks run again, record by record, when the evidence is applied, and the token is spent
    /// there -- so this is not the enforcement point, only what keeps a forged or own-run attribution out of
    /// the ledger's benefit column. It cannot see whether a genuine token was already spent: a second
    /// feedback submission presenting one is recorded as attributed, and each of its records is then
    /// refused by the confidence path, so a ledger reader counting reviews must join the evidence ledger.
    /// </remarks>
    private async Task<List<StoreValidationError>> VerifyAttributionAsync(
        AuthorizationContext authorization,
        ExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        var errors = new List<StoreValidationError>();
        var verifier = _lifecycleService.Independence;

        var run = await verifier
            .LookUpRunAsync(authorization, feedback.Scope, feedback.RunId, cancellationToken)
            .ConfigureAwait(false);

        if (!run.Known)
        {
            errors.Add(new(
                nameof(feedback.RunId),
                "must be a run the library knows in the feedback's scope (finalized there, or held by the capture service) for an attribution to count."));
            return errors;
        }

        var attributed = feedback.HumanAssessment?.AttributedExperienceIds ?? feedback.ComparativeEvaluation!.AttributedExperienceIds;
        if (await verifier
            .IsSourceRunOfAnyAsync(authorization, feedback.Scope, feedback.RunId, attributed, cancellationToken)
            .ConfigureAwait(false))
        {
            errors.Add(new(
                nameof(feedback.RunId),
                "must not be an attributed record's own source run: a lesson is not reused in the run it came from."));
            return errors;
        }

        if (feedback.ComparativeEvaluation is { } comparative)
        {
            if (!run.Finalized || run.ClosedRoundId is not { } closed || comparative.VerificationRoundId != closed)
            {
                errors.Add(new(
                    $"{nameof(feedback.ComparativeEvaluation)}.{nameof(comparative.VerificationRoundId)}",
                    "must be the verification round finalization closed for the run."));
            }

            return errors;
        }

        var assessment = feedback.HumanAssessment!;
        const string TokenPath = $"{nameof(ExperienceReuseFeedback.HumanAssessment)}.{nameof(HumanReuseAssessment.AssessmentToken)}";

        var kind = assessment.Benefit == ExperienceReuseBenefit.Harmed
            ? ConfidenceEvidenceKind.Contradicting
            : ConfidenceEvidenceKind.Supporting;
        var token = verifier.CheckToken(assessment.AssessmentToken, feedback.Scope, feedback.RunId, authorization.PrincipalId, kind);

        if (token.Refusal is { } refusal)
        {
            errors.Add(new(TokenPath, IndependenceVerifier.Describe(refusal)));
            return errors;
        }

        if (token.AssessmentId != assessment.AssessmentId)
        {
            errors.Add(new(
                $"{nameof(ExperienceReuseFeedback.HumanAssessment)}.{nameof(HumanReuseAssessment.AssessmentId)}",
                "must be the ID of the assessment token it presents."));
        }

        if (assessment.AttributedExperienceIds.Any(id => !token.Covered.Contains(id)))
        {
            errors.Add(new(TokenPath, IndependenceVerifier.Describe(IndependenceRefusal.AssessmentTokenNotForRecord)));
        }

        return errors;
    }

    /// <summary>What validation found, split by whether losing the whole submission is the right price.</summary>
    private readonly record struct ValidationOutcome(
        List<StoreValidationError> Fatal,
        List<StoreValidationError> Degrading);
}
