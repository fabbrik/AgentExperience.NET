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

    internal IndependenceVerifier(
        IExperienceRecordStore store,
        IExperienceCaptureService? captureService,
        ExperienceIndependenceOptions options)
    {
        _store = store;
        _captureService = captureService;
        Mode = options.Verification;
        _codec = AssessmentTokenCodec.Create(options);
    }

    internal IndependenceVerification Mode { get; }

    internal bool Verifies => Mode == IndependenceVerification.Verified;

    /// <summary>
    /// What the library knows about <paramref name="runId"/> in exactly <paramref name="scope"/>: whether
    /// a record was finalized from it there (and the round that finalization closed), or failing that
    /// whether the capture service holds it there.
    /// </summary>
    /// <remarks>
    /// The finalized record is found by the ID finalization derives for that run in that scope, through
    /// the ordinary scoped read: it must be found in exactly this scope (never through a grant, never a
    /// tombstone) and name this run as its source. No port lists records by run.
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

        if (read is { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } record }
            && record.SourceRunId == runId
            && record.Scope == scope)
        {
            return new(Known: true, Finalized: true, record.ClosedRoundId);
        }

        if (_captureService is not null
            && _captureService.TryGetRun(runId, out var run)
            && run.RunId == runId
            && run.Scope == scope)
        {
            return new(Known: true, Finalized: false, ClosedRoundId: null);
        }

        return new(Known: false, Finalized: false, ClosedRoundId: null);
    }

    /// <summary>
    /// Whether <paramref name="runId"/> is the source run of any of <paramref name="experienceIds"/> that is
    /// readable in exactly <paramref name="scope"/>. A record that is not found there is left to the
    /// per-record path, which reports it as unresolved.
    /// </summary>
    internal async Task<bool> IsSourceRunOfAnyAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        IEnumerable<Guid> experienceIds,
        CancellationToken cancellationToken)
    {
        foreach (var experienceId in experienceIds)
        {
            var read = await _store
                .GetAsync(authorization, scope, experienceId, ScopeCheckRead, cancellationToken)
                .ConfigureAwait(false);

            if (read is { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } record }
                && record.SourceRunId == runId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The whole check for one submission about <paramref name="target"/>: the own-run rule in every
    /// mode, and under verification the run, the round (machine) or the assessment token (human).
    /// </summary>
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
            return IndependenceCheck.Passed(assessmentId: null);
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
            return IndependenceCheck.Refused(
                IndependenceRefusal.UnknownRun,
                "The run is not one the library knows in this scope: no record was finalized from it here and the capture service does not hold it here.");
        }

        if (source == ConfidenceEvidenceSource.Machine)
        {
            return run.Finalized && run.ClosedRoundId is { } closed && roundId == closed
                ? IndependenceCheck.Passed(assessmentId: null)
                : IndependenceCheck.Refused(
                    IndependenceRefusal.UnknownRound,
                    "The verification round is not the one finalization closed for this run.");
        }

        var token = CheckToken(assessmentToken, scope, runId, authorization.PrincipalId, kind);
        if (token.Refusal is { } refusal)
        {
            return IndependenceCheck.Refused(refusal, Describe(refusal));
        }

        return token.Covered.Contains(target.ExperienceId)
            ? IndependenceCheck.Passed(token.AssessmentId)
            : IndependenceCheck.Refused(IndependenceRefusal.AssessmentTokenNotForRecord, Describe(IndependenceRefusal.AssessmentTokenNotForRecord));
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
        _ => string.Create(CultureInfo.InvariantCulture, $"Independence could not be verified ({refusal})."),
    };
}

/// <summary>What the library knows about one run in one scope.</summary>
/// <param name="Known">Whether the run is one the library knows there at all.</param>
/// <param name="Finalized">Whether a record was finalized from it there.</param>
/// <param name="ClosedRoundId">The round that finalization closed, when it closed one.</param>
internal readonly record struct RunKnowledge(bool Known, bool Finalized, Guid? ClosedRoundId);

/// <summary>The outcome of one independence check.</summary>
/// <param name="Refusal">Why it failed, or <see langword="null"/> when it passed.</param>
/// <param name="Reason">A content-free sentence for a refusal.</param>
/// <param name="AssessmentId">The verified assessment token's ID, for human evidence that passed.</param>
internal readonly record struct IndependenceCheck(IndependenceRefusal? Refusal, string? Reason, Guid? AssessmentId)
{
    internal static IndependenceCheck Passed(Guid? assessmentId) => new(null, null, assessmentId);

    internal static IndependenceCheck Refused(IndependenceRefusal refusal, string reason) => new(refusal, reason, null);
}
