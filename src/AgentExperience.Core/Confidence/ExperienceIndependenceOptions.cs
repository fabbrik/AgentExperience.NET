namespace AgentExperience.Core.Confidence;

/// <summary>
/// Whether the confidence path verifies the identifiers an independence key is made of, or trusts the
/// host to have established them.
/// </summary>
public enum IndependenceVerification
{
    /// <summary>
    /// The default. Every piece of confidence evidence must name a run the library knows in the record's
    /// scope; machine evidence must name the verification round finalization closed for that run; and
    /// human evidence must present an assessment token the library minted, which has not expired and is
    /// spent once per record. A caller that invents any of them is refused
    /// (<c>ConfidenceUpdateOutcome.Unverified</c>) rather than handed a fresh independence key.
    /// </summary>
    Verified = 0,

    /// <summary>
    /// <b>The opt-out, and the previous behaviour.</b> The run, the round and the assessment are taken as
    /// given: nothing checks that the run happened, that the round was closed, or that an assessment was
    /// made, so a host that lets agent output populate any of them hands the agent a fresh independence key
    /// per call. Assessment tokens are neither required nor checked, and no assessment is recorded. The one
    /// rule it keeps is that evidence never names the record's own source run. Meant for a host that cannot
    /// adopt verification yet -- one that captures and retrieves in different scopes, finalizes nothing, or
    /// must keep accepting evidence about runs finalized before verification existed.
    /// </summary>
    TrustHostSuppliedIdentifiers = 1,
}

/// <summary>
/// What evidence the <see cref="IndependenceVerification.TrustHostSuppliedIdentifiers"/> opt-out admitted
/// (stored as <c>ConfidenceEvidenceAdmission.HostTrusted</c>) does to the record it is about. Only meaningful
/// under the opt-out: verified evidence always counts.
/// </summary>
public enum HostTrustedEvidenceEffect
{
    /// <summary>
    /// The default, and the previous behaviour. Host-trusted evidence counts like verified evidence: it moves
    /// the record's counters, its stored reuse confidence (which retrieval ranks and filters on) and, for a
    /// contradiction, its status (to <c>Contested</c>).
    /// </summary>
    Counted = 0,

    /// <summary>
    /// Host-trusted evidence is recorded and does not move the record. Its lifecycle event and its evidence
    /// row are stored as under <see cref="Counted"/> (same IDs, idempotency, assessment spending and
    /// <c>HostTrusted</c> admission), but the event's new counters, new score and status equal the prior ones,
    /// and the record's <c>UpdatedAt</c> is not refreshed, so ranking, the confidence floor, <c>MaxAge</c> and
    /// eligibility are exactly what they were. The confidence engine is not asked to score it.
    /// <c>ExperienceLifecycleService.ReadConfidenceAsync</c> with <c>ConfidenceEvidenceFilter.All</c> adds it back,
    /// deduplicated by independence key. Evidence counted before this was set stays counted.
    /// </summary>
    RecordedOnly = 1,
}

/// <summary>
/// How confidence independence is verified: the mode, the host-held secret that assessment tokens are
/// minted and verified under, how long a token stays valid, and the clock that decides it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is a secret the host holds.</b> Anyone who has it can mint an assessment token, so it
/// belongs in the host's secret store, never in configuration an agent can read and never next to agent
/// tooling. It is copied when a service is constructed, so changing the array afterwards changes nothing,
/// and it appears in no token, log, span or message. Rotating it invalidates every outstanding token,
/// which the default lifetime bounds to a day.
/// </para>
/// <para>
/// Machine evidence needs no key: it is verified against the library's own records. Human evidence under
/// <see cref="IndependenceVerification.Verified"/> with no key configured is refused
/// (<c>IndependenceRefusal.AssessmentKeyNotConfigured</c>).
/// </para>
/// </remarks>
public sealed class ExperienceIndependenceOptions
{
    /// <summary>The shortest <see cref="AssessmentTokenKey"/> accepted: 32 bytes, HMAC-SHA256's output size.</summary>
    public const int MinimumAssessmentTokenKeyBytes = 32;

    /// <summary>The default <see cref="AssessmentTokenLifetime"/>: one day.</summary>
    public static readonly TimeSpan DefaultAssessmentTokenLifetime = TimeSpan.FromDays(1);

    /// <summary>The longest <see cref="AssessmentTokenLifetime"/> accepted: 30 days.</summary>
    public static readonly TimeSpan MaximumAssessmentTokenLifetime = TimeSpan.FromDays(30);

    /// <summary>Whether independence is verified (the default) or the host's identifiers are trusted.</summary>
    public IndependenceVerification Verification { get; init; } = IndependenceVerification.Verified;

    /// <summary>
    /// What host-trusted evidence does to its record: <see cref="HostTrustedEvidenceEffect.Counted"/> (the default)
    /// moves its counters, score and status like verified evidence; <see cref="HostTrustedEvidenceEffect.RecordedOnly"/>
    /// records it and moves nothing, so evidence nothing verified cannot steer what retrieval ranks or gives agents.
    /// Has no effect unless <see cref="Verification"/> is <see cref="IndependenceVerification.TrustHostSuppliedIdentifiers"/>.
    /// An undefined value is refused when a service is constructed.
    /// </summary>
    public HostTrustedEvidenceEffect HostTrustedEvidence { get; init; } = HostTrustedEvidenceEffect.Counted;

    /// <summary>Refuses an undefined <see cref="HostTrustedEvidence"/>; called wherever a service is built over these options.</summary>
    /// <exception cref="ArgumentException"><see cref="HostTrustedEvidence"/> is not a defined effect.</exception>
    internal void ValidateHostTrustedEvidence()
    {
        if (!Enum.IsDefined(HostTrustedEvidence))
        {
            throw new ArgumentException("ExperienceIndependenceOptions.HostTrustedEvidence must be a defined effect.", "independence");
        }
    }

    /// <summary>
    /// The secret assessment tokens are signed and verified under (HMAC-SHA256). At least
    /// <see cref="MinimumAssessmentTokenKeyBytes"/> bytes, from a cryptographic random source; or
    /// <see langword="null"/> when the host records no human assessments.
    /// </summary>
    public byte[]? AssessmentTokenKey { get; init; }

    /// <summary>
    /// How long an assessment token minted under these options stays valid. Strictly positive and at most
    /// <see cref="MaximumAssessmentTokenLifetime"/>; defaults to <see cref="DefaultAssessmentTokenLifetime"/>.
    /// The expiry is signed into each token, so changing this affects tokens minted afterwards only. A
    /// submission, and any retry of it, has to land before the token expires: verification runs before the
    /// store's replay check, so a retry of evidence that did land, made after expiry, is refused as
    /// <c>AssessmentTokenExpired</c> even though the original is durable.
    /// </summary>
    public TimeSpan AssessmentTokenLifetime { get; init; } = DefaultAssessmentTokenLifetime;

    /// <summary>The clock tokens are stamped and checked against. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
