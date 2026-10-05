using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// Verifies the identifiers an independence key is made of against what the library itself knows: the
/// records finalization wrote, the runs the capture service holds, and the assessment tokens it minted.
/// Shared by the confidence path and the reuse-feedback service, so both apply exactly one rule.
/// </summary>
internal sealed class IndependenceVerifier
{
    /// <summary>A scope check, never a delivery: nothing read here is handed to anyone.</summary>
    private static readonly ExperienceReadOptions ScopeCheckRead = new(ExperienceReadPurpose.ScopeCheck);

    private readonly IExperienceRecordStore _store;
    private readonly IExperienceCaptureService? _captureService;
    private readonly AssessmentTokenCodec? _codec;
    private readonly ProvenanceSigner? _signer;

    internal IndependenceVerifier(
        IExperienceRecordStore store,
        IExperienceCaptureService? captureService,
        ExperienceIndependenceOptions options,
        ExperienceProvenanceSigningOptions? provenanceSigning = null)
    {
        _store = store;
        _captureService = captureService;
        Mode = options.Verification;
        _codec = AssessmentTokenCodec.Create(options);
        options.ValidateHostTrustedEvidence();
        HostTrustedEvidence = options.HostTrustedEvidence;
        _signer = ProvenanceSigner.Create(provenanceSigning);
        if (_signer is not null)
        {
            EnsureNotAssessmentKey(_signer, nameof(provenanceSigning));
        }
    }

    /// <summary>
    /// Refuses a provenance signing ring that holds the assessment token key: whoever could mint tokens could then
    /// sign provenance, and the reverse. Compared in constant time; the message names neither key.
    /// </summary>
    internal void EnsureNotAssessmentKey(ProvenanceSigner signer, string parameterName)
    {
        if (_codec is not null && _codec.SharesKeyWith(signer))
        {
            throw new ArgumentException(
                "A provenance signing key must not be the assessment token key; give each purpose its own secret.", parameterName);
        }
    }

    /// <summary>The provenance signer, when signing is configured; finalization signs with the same one by default.</summary>
    internal ProvenanceSigner? Signer => _signer;

    internal IndependenceVerification Mode { get; }

    internal bool Verifies => Mode == IndependenceVerification.Verified;

    /// <summary>What host-trusted evidence does to its record; consulted only for evidence the opt-out admitted.</summary>
    internal HostTrustedEvidenceEffect HostTrustedEvidence { get; }

    /// <summary>
    /// What the library knows about <paramref name="runId"/> in exactly <paramref name="scope"/>: whether
    /// a record was finalized from it there (and the round that finalization closed, and the records the run
    /// was exposed to), or failing that whether the capture service holds it there (and its exposures).
    /// </summary>
    /// <remarks>
    /// The finalized record is found by the ID finalization derives for that run in that scope, through
    /// the ordinary scoped read: it must be found in exactly this scope (never through a grant, never a
    /// tombstone), name this run as its source, and have been written by finalization
    /// (<see cref="ExperienceRecordOrigin.Finalized"/>). A record under that ID that a host wrote by hand is
    /// its writer's statement and vouches for nothing; it is reported as <see cref="RunKnowledge.HostWrittenOnly"/>
    /// when the capture service does not hold the run either. No port lists records by run.
    /// With provenance signing configured, a record marked finalized also has to carry a signature over its
    /// finalization claims that verifies under a key in the ring (or carry no signature at all and be listed in
    /// the configured cutover set); otherwise it is treated exactly as a hand-written one, and
    /// <see cref="RunKnowledge.SignatureRefusal"/> says why.
    /// </remarks>
    internal async Task<RunKnowledge> LookUpRunAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var read = await _store
            .GetAsync(authorization, scope, ExperienceFinalizationService.ExperienceIdFor(runId, scope), ScopeCheckRead, cancellationToken)
            .ConfigureAwait(false);

        var hostWritten = false;
        ProvenanceSignatureCheck? signatureRefusal = null;
        if (read is { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } record }
            && record.SourceRunId == runId
            && record.Scope == scope)
        {
            if (record.Origin == ExperienceRecordOrigin.Finalized)
            {
                var check = _signer?.Verify(record) ?? ProvenanceSignatureCheck.Valid;
                if (check is ProvenanceSignatureCheck.Valid or ProvenanceSignatureCheck.TrustedUnsigned)
                {
                    return new(Known: true, Finalized: true, record.ClosedRoundId, ExposuresOf(record.Provenance));
                }

                // Claims to be finalized, but the library cannot confirm it wrote those claims: it is the
                // statement of whoever wrote it, exactly like a record marked HostWritten.
                signatureRefusal = check;
            }

            hostWritten = true;
        }

        if (_captureService is not null
            && _captureService.TryGetRun(runId, out var run)
            && run.RunId == runId
            && run.Scope == scope)
        {
            return new(Known: true, Finalized: false, ClosedRoundId: null, ExposuresOf(run.Provenance));
        }

        return new(Known: false, Finalized: false, ClosedRoundId: null, NoExposures)
        {
            HostWrittenOnly = hostWritten,
            SignatureRefusal = signatureRefusal,
        };
    }

    /// <summary>
    /// Under the opt-out, with provenance signing configured: whether the record finalization would have written
    /// for <paramref name="runId"/> in <paramref name="scope"/> -- read exactly as the verified path reads it, in this
    /// scope, marked finalized -- carries a signature that is present, under a key in the ring, and does not verify.
    /// The opt-out trusts the host's identifiers, so a record with no signature or under an unknown key is accepted
    /// there as host-trusted; what it does not accept is a record the library signed and someone then changed.
    /// </summary>
    internal async Task<bool> HasTamperedSignatureAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (_signer is null)
        {
            return false;
        }

        var read = await _store
            .GetAsync(authorization, scope, ExperienceFinalizationService.ExperienceIdFor(runId, scope), ScopeCheckRead, cancellationToken)
            .ConfigureAwait(false);

        return read is { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { ProvenanceSignature: not null } record }
            && record.SourceRunId == runId
            && record.Scope == scope
            && record.Origin == ExperienceRecordOrigin.Finalized
            && _signer.Verify(record) == ProvenanceSignatureCheck.Invalid;
    }

    /// <summary>
    /// Whether a run whose exposures are <paramref name="exposures"/> was exposed to <paramref name="target"/>
    /// at or before the revision evidence about it is computed against: the record's revision as Core read it.
    /// An exposure recorded at a later revision than that cannot have happened, and is refused rather than
    /// trusted.
    /// </summary>
    /// <returns><see langword="null"/> when it was exposed; otherwise why not, as a content-free sentence.</returns>
    internal static string? ExposureRefusal(IReadOnlyList<RunExposure> exposures, ExperienceRecord target)
    {
        foreach (var exposure in exposures)
        {
            if (exposure is null || exposure.ExperienceId != target.ExperienceId)
            {
                continue;
            }

            return exposure.Revision <= target.Revision
                ? null
                : "The run was exposed to this record only at a revision later than the one the evidence is computed against.";
        }

        return "The run was not exposed to this record: nothing records the library delivering it into the run.";
    }

    private static readonly IReadOnlyList<RunExposure> NoExposures = [];

    /// <summary>A provenance's exposures, or none when a hand-built one carries a null list.</summary>
    private static IReadOnlyList<RunExposure> ExposuresOf(Provenance? provenance) => provenance?.ExposedTo ?? NoExposures;

    /// <summary>Whether <paramref name="runId"/> is the source run of any of <paramref name="records"/>.</summary>
    internal static bool IsSourceRunOfAny(Guid runId, IEnumerable<ExperienceRecord> records) =>
        records.Any(record => record.SourceRunId == runId);

    /// <summary>
    /// The records among <paramref name="experienceIds"/> that are readable in exactly
    /// <paramref name="scope"/> (never through a grant), read once each as a scope check. A record that is
    /// not found there is left out, for the per-record path to report as unresolved.
    /// </summary>
    internal async Task<List<ExperienceRecord>> ReadInScopeAsync(
        AuthorizationContext authorization,
        Scope scope,
        IEnumerable<Guid> experienceIds,
        CancellationToken cancellationToken)
    {
        var found = new List<ExperienceRecord>();
        foreach (var experienceId in experienceIds)
        {
            var read = await _store
                .GetAsync(authorization, scope, experienceId, ScopeCheckRead, cancellationToken)
                .ConfigureAwait(false);

            if (read is { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } record }
                && record.ExperienceId == experienceId)
            {
                found.Add(record);
            }
        }

        return found;
    }

    /// <summary>
    /// The whole check for one submission about <paramref name="target"/>: the own-run rule in every
    /// mode, and under verification the run, the round (machine) or the assessment token (human), and
    /// last that the run was exposed to the record at or before its current revision.
    /// </summary>
    /// <remarks>
    /// Every submission this checks is a claim that reusing <paramref name="target"/> helped or hurt the
    /// named run, so every one of them -- machine and human alike -- must name a run that was given the
    /// record. The machine evidence that is about a record's own quality is its source run's evaluation,
    /// which finalization binds and which never comes through here (the own-run rule refuses it).
    /// </remarks>
    internal async Task<IndependenceCheck> VerifyAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceRecord target,
        ConfidenceEvidenceSource source,
        ConfidenceEvidenceKind kind,
        Guid runId,
        Guid? roundId,
        string? assessmentToken,
        CancellationToken cancellationToken)
    {
        if (runId == target.SourceRunId)
        {
            return IndependenceCheck.Refused(
                IndependenceRefusal.OwnRun,
                "The evidence names the record's own source run; a lesson is not reused in the run it came from.");
        }

        if (!Verifies)
        {
            return await HasTamperedSignatureAsync(authorization, scope, runId, cancellationToken).ConfigureAwait(false)
                ? IndependenceCheck.Refused(
                    IndependenceRefusal.HostWrittenRun,
                    $"The run is known here only through a record marked finalized whose {ProvenanceSigner.RefusalText}, so it vouches for nothing, even under the opt-out.")
                : IndependenceCheck.Passed(assessmentId: null, ConfidenceEvidenceAdmission.HostTrusted);
        }

        // Settled before any read when it can be: a human submission with no way to check its token fails
        // the same way whatever the run turns out to be.
        if (source == ConfidenceEvidenceSource.Human && _codec is null)
        {
            return IndependenceCheck.Refused(
                IndependenceRefusal.AssessmentKeyNotConfigured,
                "Human evidence needs an assessment token, and no assessment token key is configured to verify one.");
        }

        var run = await LookUpRunAsync(authorization, scope, runId, cancellationToken).ConfigureAwait(false);
        if (!run.Known)
        {
            return run.HostWrittenOnly
                ? IndependenceCheck.Refused(
                    IndependenceRefusal.HostWrittenRun,
                    run.SignatureRefusal is not null
                        ? $"The run is known here only through a record marked finalized whose {ProvenanceSigner.RefusalText}, so it is treated as written without finalization and vouches for nothing, and the capture service does not hold it."
                        : "The run is known here only through a record written without finalization, which vouches for nothing, and the capture service does not hold it.")
                : IndependenceCheck.Refused(
                    IndependenceRefusal.UnknownRun,
                    "The run is not one the library knows in this scope: no record was finalized from it here and the capture service does not hold it here.");
        }

        Guid? assessmentId = null;
        if (source == ConfidenceEvidenceSource.Machine)
        {
            if (!run.Finalized || run.ClosedRoundId is not { } closed || roundId != closed)
            {
                return IndependenceCheck.Refused(
                    IndependenceRefusal.UnknownRound,
                    "The verification round is not the one finalization closed for this run.");
            }
        }
        else
        {
            var token = CheckToken(assessmentToken, scope, runId, authorization.PrincipalId, kind);
            if (token.Refusal is { } refusal)
            {
                return IndependenceCheck.Refused(refusal, Describe(refusal));
            }

            if (!token.Covered.Contains(target.ExperienceId))
            {
                return IndependenceCheck.Refused(
                    IndependenceRefusal.AssessmentTokenNotForRecord, Describe(IndependenceRefusal.AssessmentTokenNotForRecord));
            }

            assessmentId = token.AssessmentId;
        }

        // Last, so every refusal story 6.6 reached is still reached first: the run is real and its round or
        // token genuine, and the question left is whether this run was ever given this record.
        return ExposureRefusal(run.Exposures, target) is { } notExposed
            ? IndependenceCheck.Refused(IndependenceRefusal.NotExposed, notExposed)
            : IndependenceCheck.Passed(assessmentId, ConfidenceEvidenceAdmission.Verified);
    }

    /// <summary>
    /// Checks a token against the binding the submission names. Never called without a key configured
    /// by a verifying caller that has not already refused.
    /// </summary>
    internal (IndependenceRefusal? Refusal, Guid AssessmentId, IReadOnlyList<Guid> Covered) CheckToken(
        string? token,
        Scope scope,
        Guid runId,
        string? principal,
        ConfidenceEvidenceKind kind)
    {
        if (_codec is null)
        {
            return (IndependenceRefusal.AssessmentKeyNotConfigured, Guid.Empty, []);
        }

        if (string.IsNullOrWhiteSpace(principal))
        {
            // The reviewer is half of the binding; a blank one binds to nothing. The shape checks refuse
            // this first everywhere, so this is only defence in depth.
            return (IndependenceRefusal.AssessmentTokenInvalid, Guid.Empty, []);
        }

        return _codec.Verify(token, scope, runId, principal, kind);
    }

    /// <summary>A content-free sentence for a refusal: it names the rule, never the token.</summary>
    internal static string Describe(IndependenceRefusal refusal) => refusal switch
    {
        IndependenceRefusal.OwnRun => "The evidence names the record's own source run.",
        IndependenceRefusal.UnknownRun => "The run is not one the library knows in this scope.",
        IndependenceRefusal.UnknownRound => "The verification round is not the one finalization closed for this run.",
        IndependenceRefusal.AssessmentTokenMissing => "Human evidence must present an assessment token.",
        IndependenceRefusal.AssessmentTokenInvalid =>
            "The assessment token is not one the library minted for this scope, run, reviewer and direction.",
        IndependenceRefusal.AssessmentTokenExpired => "The assessment token has expired.",
        IndependenceRefusal.AssessmentTokenNotForRecord => "The assessment token does not cover this record.",
        IndependenceRefusal.AssessmentTokenReplayed =>
            "The assessment token has already landed evidence for this record under another evidence ID.",
        IndependenceRefusal.AssessmentKeyNotConfigured => "No assessment token key is configured to verify human evidence.",
        IndependenceRefusal.NotExposed => "The run was not exposed to this record at or before its current revision.",
        IndependenceRefusal.HostWrittenRun => "The run is known only through a record written without finalization.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Independence could not be verified ({refusal})."),
    };
}

/// <summary>What the library knows about one run in one scope.</summary>
/// <param name="Known">Whether the run is one the library knows there at all.</param>
/// <param name="Finalized">Whether a record was finalized from it there.</param>
/// <param name="ClosedRoundId">The round that finalization closed, when it closed one.</param>
/// <param name="Exposures">The records the run was exposed to, as its finalized record or the capture service holds them.</param>
internal readonly record struct RunKnowledge(bool Known, bool Finalized, Guid? ClosedRoundId, IReadOnlyList<RunExposure> Exposures)
{
    /// <summary>
    /// Whether the run is unknown only because the record under its derived ID was written by hand: set
    /// only when <see cref="Known"/> is <see langword="false"/>.
    /// </summary>
    public bool HostWrittenOnly { get; init; }

    /// <summary>
    /// When <see cref="HostWrittenOnly"/> is set because a record marked finalized failed the provenance
    /// signature check: why it failed. <see langword="null"/> otherwise.
    /// </summary>
    public ProvenanceSignatureCheck? SignatureRefusal { get; init; }
}

/// <summary>The outcome of one independence check.</summary>
/// <param name="Refusal">Why it failed, or <see langword="null"/> when it passed.</param>
/// <param name="Reason">A content-free sentence for a refusal.</param>
/// <param name="AssessmentId">The verified assessment token's ID, for human evidence that passed.</param>
/// <param name="Admission">How the evidence was admitted, when it passed.</param>
internal readonly record struct IndependenceCheck(
    IndependenceRefusal? Refusal,
    string? Reason,
    Guid? AssessmentId,
    ConfidenceEvidenceAdmission? Admission)
{
    internal static IndependenceCheck Passed(Guid? assessmentId, ConfidenceEvidenceAdmission admission) =>
        new(null, null, assessmentId, admission);

    internal static IndependenceCheck Refused(IndependenceRefusal refusal, string reason) => new(refusal, reason, null, null);
}
