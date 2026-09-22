namespace AgentExperience.Abstractions;

/// <summary>
/// Which way a piece of confidence evidence points: the lesson worked again, or it did not. It is
/// deliberately not <see cref="CheckResult"/>: that verdict is about one required check inside one
/// verification round, while this is about the stored lesson itself being reused.
/// </summary>
public enum ConfidenceEvidenceKind
{
    /// <summary>The lesson was reused and the reuse succeeded. Counts towards <c>S</c>.</summary>
    Supporting,

    /// <summary>The lesson was reused and the reuse did not hold. Counts towards <c>F</c>.</summary>
    Contradicting,
}

/// <summary>
/// Who observed the evidence, which is what decides the independence key it is deduplicated on.
/// </summary>
public enum ConfidenceEvidenceSource
{
    /// <summary>
    /// A deterministic evaluator observed the reuse. Independence is keyed on the record, the run, and
    /// the verification round, so one round of one run counts once however many times it is submitted.
    /// </summary>
    Machine,

    /// <summary>
    /// A human reviewer judged the reuse. Independence is keyed on the record, the reviewer, and the
    /// run, so one reviewer's opinion about one run counts once however many times it is submitted. The
    /// reviewer identity is the host's <see cref="AuthorizationContext.PrincipalId"/> and never
    /// anything an agent supplied.
    /// </summary>
    Human,
}

/// <summary>
/// One evidence-based movement of a record's reuse confidence, carried on the
/// <see cref="LifecycleEvent"/> that applies it. Core computes every number here from the record it
/// read; a store persists them exactly as given and never derives a score of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The score is a heuristic, not a probability.</b> It is
/// <c>(1 + S) / (2 + S + F)</c> -- Laplace's rule of succession over independent observations -- where
/// <c>S</c> counts independent accepted supporting validations (including the one the record was
/// finalized with) and <c>F</c> counts independent accepted contradictions. It is a monotone, bounded
/// summary of how often reuse held up, useful for ranking and for a floor; it is not calibrated
/// against anything, and nothing may present it as the probability that the next reuse succeeds.
/// </para>
/// <para>
/// <b>It never changes eligibility.</b> Confidence is independent of
/// <see cref="ExperienceRecord.CompletionScore"/> and of <see cref="ExperienceRecord.Status"/>: no
/// number here can make an ineligible record eligible. A contradiction moves a
/// <see cref="ExperienceStatus.Validated"/> or <see cref="ExperienceStatus.Reinforced"/> record to
/// <see cref="ExperienceStatus.Contested"/>, and that status change -- not the score -- is what takes
/// it out of reuse.
/// </para>
/// <para>
/// <b>Duplicates are recorded, not counted.</b> The first submission for an independence key is the
/// one that moves the counters. A later submission under a new
/// <see cref="EvidenceId"/> with the same key is still stored, for audit, with
/// <see cref="Counted"/> <see langword="false"/> -- its prior and new values are equal, because
/// nothing moved. Which independence key applies is decided by <see cref="Source"/>; see
/// <see cref="ConfidenceEvidenceSource"/>.
/// </para>
/// </remarks>
/// <param name="EvidenceId">Unique identifier for this submission, and the idempotency key a store deduplicates it on. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Kind">Whether this evidence supports reuse or contradicts it.</param>
/// <param name="Source">Whether a machine evaluator or a human reviewer observed it.</param>
/// <param name="RunId">
/// The run the reuse was observed in. Not <see cref="ExperienceRecord.SourceRunId"/>, which is the run
/// the record came from. A host trust boundary: nothing in this library can check that the run happened,
/// so a caller that invents one gets a fresh independence key and can drive the score at will. Establish
/// it from your own run bookkeeping, exactly as you establish <see cref="AuthorizationContext"/>, and
/// never pass through an identifier an agent produced.
/// </param>
/// <param name="VerificationRoundId">
/// The verification round the observation came from. Required for
/// <see cref="ConfidenceEvidenceSource.Machine"/>, and <see langword="null"/> for a human submission. The
/// same host trust boundary as <paramref name="RunId"/>: nothing here can check that a round was closed.
/// </param>
/// <param name="ReviewerIdentity">The reviewing principal. Required for <see cref="ConfidenceEvidenceSource.Human"/>, and <see langword="null"/> for a machine submission. Always the host's <see cref="AuthorizationContext.PrincipalId"/>, never agent input.</param>
/// <param name="RuleVersion">The version of the confidence rule that produced <paramref name="NewReuseConfidence"/>, so a later rule change stays auditable against updates computed under an earlier one.</param>
/// <param name="PriorReuseConfidence">The record's <see cref="ExperienceRecord.ReuseConfidence"/> as Core read it.</param>
/// <param name="NewReuseConfidence">The confidence this update writes. Equal to <paramref name="PriorReuseConfidence"/> when the submission was a duplicate.</param>
/// <param name="PriorSupportingValidations">The record's <see cref="ExperienceRecord.SupportingValidations"/> as Core read it.</param>
/// <param name="NewSupportingValidations">The supporting count this update writes.</param>
/// <param name="PriorContradictions">The record's <see cref="ExperienceRecord.Contradictions"/> as Core read it.</param>
/// <param name="NewContradictions">The contradiction count this update writes.</param>
/// <param name="Detail">Optional sanitized, human-readable detail. Never private reasoning.</param>
public sealed record ConfidenceUpdate(
    Guid EvidenceId,
    ConfidenceEvidenceKind Kind,
    ConfidenceEvidenceSource Source,
    Guid RunId,
    Guid? VerificationRoundId,
    string? ReviewerIdentity,
    string RuleVersion,
    double PriorReuseConfidence,
    double NewReuseConfidence,
    int PriorSupportingValidations,
    int NewSupportingValidations,
    int PriorContradictions,
    int NewContradictions,
    string? Detail = null)
{
    /// <summary>
    /// Whether this submission actually moved a counter. It is read off the stored numbers rather than
    /// carried as a flag of its own, so a row can never claim it counted while its prior and new values
    /// say otherwise.
    /// </summary>
    public bool Counted =>
        NewSupportingValidations != PriorSupportingValidations || NewContradictions != PriorContradictions;

    /// <summary>
    /// The same submission with nothing moved: what a store writes when the independence key was
    /// already taken. Declining to apply an increment is not deriving a score -- every number in the
    /// result is one Core already read from the record.
    /// </summary>
    /// <returns>A copy whose new values equal its prior values.</returns>
    public ConfidenceUpdate AsRecordedOnly() => this with
    {
        NewReuseConfidence = PriorReuseConfidence,
        NewSupportingValidations = PriorSupportingValidations,
        NewContradictions = PriorContradictions,
    };
}
