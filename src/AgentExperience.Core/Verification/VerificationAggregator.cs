using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;

namespace AgentExperience.Core.Verification;

/// <summary>
/// Resolves a task's declared required <c>CheckId</c>s against evidence, producing exactly one
/// <see cref="VerificationResult"/>. Pure and synchronous: it never selects a round or revision
/// itself (only a host-supplied <see cref="ClosedVerificationRound"/> does that -- see AC4's "agent-
/// supplied timestamps or round selections cannot override the host-selected round"), never
/// performs I/O, and never combines evidence across rounds or artifact revisions to manufacture a
/// pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection:</b> a <see langword="null"/> <see cref="ClosedVerificationRound"/>, or one whose
/// own <see cref="ClosedVerificationRound.ArtifactRevision"/> does not match the caller's
/// <c>currentArtifactRevision</c>, is stale/unclosed verification -- aggregation stops immediately
/// with an <see cref="TaskVerificationStatus.Unknown"/> result, no evidence examined. Otherwise,
/// only evidence whose own <see cref="Evidence.VerificationRoundId"/> equals the closed round's
/// <see cref="ClosedVerificationRound.RoundId"/> <em>and</em> whose own
/// <see cref="Evidence.ArtifactRevision"/> equals <c>currentArtifactRevision</c> is ever considered
/// -- evidence from an earlier (now-superseded) round, or any other artifact revision, is filtered
/// out entirely and never contributes, though it remains untouched wherever a caller separately
/// keeps attempt/round history.
/// </para>
/// <para>
/// <b>Per-check resolution</b> (the two AC4 conflict clauses, reconciled -- see this story's Design
/// Notes): for each <see cref="RequiredCheck"/>, gather only the selected evidence carrying its
/// <see cref="RequiredCheck.CheckId"/> <em>and</em> a <see cref="Evidence.Kind"/> its
/// <see cref="RequiredCheck.ExpectedKind"/> accepts. No
/// evidence at all is a missing check (<see cref="CheckResult.Unknown"/>); any
/// <see cref="CheckResult.Fail"/> among it makes the check <see cref="CheckResult.Fail"/> --
/// dominating even a <see cref="CheckResult.Pass"/> recorded for the same check in the same
/// selected round; otherwise any <see cref="CheckResult.Unknown"/> (with no <see cref="CheckResult.Fail"/>)
/// makes the check <see cref="CheckResult.Unknown"/>; only when every piece of evidence for that
/// check is <see cref="CheckResult.Pass"/> does the check resolve to <see cref="CheckResult.Pass"/>.
/// </para>
/// <para>
/// <b>Overall verdict:</b> any required check resolving to <see cref="CheckResult.Fail"/> makes the
/// whole result <see cref="TaskVerificationStatus.Failed"/>, regardless of any other check's
/// verdict; otherwise any required check resolving to <see cref="CheckResult.Unknown"/> (missing,
/// errored, or genuinely inconclusive) makes it <see cref="TaskVerificationStatus.Unknown"/>; only
/// when every required check resolves to <see cref="CheckResult.Pass"/> is the result
/// <see cref="TaskVerificationStatus.Verified"/>; an empty required-check set is always
/// <see cref="TaskVerificationStatus.Unknown"/> (there is nothing to have conclusively verified).
/// </para>
/// <para>
/// <b>Completion score</b> is the fraction of required checks that resolved to
/// <see cref="CheckResult.Pass"/> (<c>0</c> for an empty required set), reported alongside the
/// <see cref="Outcome"/> on <see cref="VerificationResult"/> -- never folded into or substituted for
/// the verdict itself, per this story's frozen Boundaries.
/// </para>
/// <para>
/// <b>Cancellation</b> propagates as a thrown exception, checked cooperatively, distinct from a
/// returned <see cref="TaskVerificationStatus.Unknown"/>: a cancelled call never returns a
/// <see cref="VerificationResult"/> at all (partial or otherwise) -- it throws, exactly like this
/// codebase's other cancellable calls (e.g. <c>InMemoryExperienceCaptureService.AppendAttemptAsync</c>).
/// </para>
/// </remarks>
public static class VerificationAggregator
{
    /// <summary>
    /// Identifies the aggregation rule version every <see cref="VerificationResult"/> produced by
    /// this type's current build is computed under, so a future rule change stays auditable against
    /// results computed under an earlier one.
    /// </summary>
    public const string RuleVersion = "1.0.0";

    /// <summary>
    /// Aggregates <paramref name="evidence"/> against <paramref name="requiredChecks"/>, reading
    /// only the evidence in <paramref name="closedRound"/> for <paramref name="currentArtifactRevision"/>
    /// -- see this type's remarks for the full selection, per-check, and overall-verdict rules.
    /// </summary>
    /// <param name="evidence">All evidence available to consider, in the order it was produced. Never filtered or reordered by the caller; this call does that filtering itself. A <see langword="null"/> entry is a caller error and throws.</param>
    /// <param name="requiredChecks">The task's declared required checks, whose <see cref="RequiredCheck.CheckId"/>s must be unique (a duplicate is a caller error and throws, rather than silently skewing the completion score) and non-blank. A <see langword="null"/> entry is a caller error and throws. An empty set always yields <see cref="TaskVerificationStatus.Unknown"/>.</param>
    /// <param name="closedRound">The host-closed verification round and artifact revision to read from, or <see langword="null"/> if the host has not closed a round yet. Never agent-suppliable -- only a host establishes this.</param>
    /// <param name="currentArtifactRevision">The artifact's current revision. If it does not match <paramref name="closedRound"/>'s own revision, verification is stale.</param>
    /// <param name="evaluatedAt">When this aggregation is being performed.</param>
    /// <param name="cancellationToken">Checked cooperatively; a cancelled call throws rather than returning any <see cref="VerificationResult"/>.</param>
    public static VerificationResult Aggregate(
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<RequiredCheck> requiredChecks,
        ClosedVerificationRound? closedRound,
        string currentArtifactRevision,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default)
    {
        // A static ActivitySource instruments a static pure function without giving it a constructor,
        // a container, or a seam -- emission is listener-driven, so nothing about calling Aggregate
        // changes when nobody is subscribed.
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.Verify, cancellationToken);

        VerificationResult result;
        try
        {
            result = AggregateCore(evidence, requiredChecks, closedRound, currentArtifactRevision, evaluatedAt, cancellationToken);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.Verify, ex);
            throw;
        }

        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.Verify, result.Outcome.Status.ToString());
        return result;
    }

    /// <summary>
    /// The body of <see cref="Aggregate"/>, unchanged by instrumentation: it neither reads nor writes
    /// a span. It exists so that the wrapper's own tagging and metric writes sit outside the region
    /// that guards the call, and it is <see langword="private"/> because every caller -- this library's
    /// own finalization included -- goes through the instrumented entry point.
    /// </summary>
    /// <param name="evidence">The evidence to aggregate.</param>
    /// <param name="requiredChecks">The checks the round must satisfy.</param>
    /// <param name="closedRound">The host-closed verification round, if any.</param>
    /// <param name="currentArtifactRevision">The artifact revision the evidence must match.</param>
    /// <param name="evaluatedAt">When the aggregation was performed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The verification verdict.</returns>
    private static VerificationResult AggregateCore(
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<RequiredCheck> requiredChecks,
        ClosedVerificationRound? closedRound,
        string currentArtifactRevision,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(requiredChecks);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentArtifactRevision);

        // Invalid input throws -- never silently dropped or tolerated into a fabricated result, per
        // TaskCheckEvaluators' own convention.
        if (evidence.Any(e => e is null))
        {
            throw new ArgumentException("Evidence must not contain null entries.", nameof(evidence));
        }

        if (requiredChecks.Any(c => c is null))
        {
            throw new ArgumentException("Required checks must not contain null entries.", nameof(requiredChecks));
        }

        if (requiredChecks.Any(c => string.IsNullOrWhiteSpace(c.CheckId)))
        {
            throw new ArgumentException("Required check IDs must not be null, empty, or whitespace.", nameof(requiredChecks));
        }

        if (requiredChecks.Any(c => c.ExpectedKind is not null && string.IsNullOrWhiteSpace(c.ExpectedKind)))
        {
            throw new ArgumentException("A required check's ExpectedKind must be null or non-blank.", nameof(requiredChecks));
        }

        if (requiredChecks.Select(c => c.CheckId).Distinct(StringComparer.Ordinal).Count() != requiredChecks.Count)
        {
            throw new ArgumentException("Required check IDs must be unique.", nameof(requiredChecks));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Stale/unclosed short-circuit -- no round or revision selection from evidence or
        // requiredChecks themselves is ever consulted here; only the host-supplied closedRound
        // decides. Nothing is examined further.
        if (closedRound is null)
        {
            return UnknownResult("No verification round has been closed by the host; verification is unclosed.", evaluatedAt);
        }

        if (!string.Equals(closedRound.ArtifactRevision, currentArtifactRevision, StringComparison.Ordinal))
        {
            return UnknownResult(
                $"The host-closed verification round applies to artifact revision '{closedRound.ArtifactRevision}', which does not match the current artifact revision '{currentArtifactRevision}'; verification is stale.",
                evaluatedAt);
        }

        if (requiredChecks.Count == 0)
        {
            return UnknownResult("No required checks were declared for this task; an empty required set can never be conclusively verified.", evaluatedAt);
        }

        // Filter to exactly the host-closed round and current artifact revision -- never blended
        // with any other round or revision to manufacture a pass.
        var selectedEvidence = evidence
            .Where(e => e.VerificationRoundId == closedRound.RoundId && string.Equals(e.ArtifactRevision, currentArtifactRevision, StringComparison.Ordinal))
            .ToList();

        var contributingEvidenceIds = new HashSet<Guid>();
        var passingCheckCount = 0;
        var failedCheckIds = new List<string>();
        var unknownCheckIds = new List<string>();

        foreach (var requiredCheck in requiredChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A named ExpectedKind narrows the evidence for this check: evidence of any other kind is
            // ignored outright, so a mismatched evaluator can never satisfy the check (it becomes a
            // check with no evidence, i.e. Unknown).
            var checkEvidence = selectedEvidence
                .Where(e => string.Equals(e.CheckId, requiredCheck.CheckId, StringComparison.Ordinal) && requiredCheck.Accepts(e.Kind))
                .ToList();

            foreach (var e in checkEvidence)
            {
                contributingEvidenceIds.Add(e.EvidenceId);
            }

            switch (ResolveCheck(checkEvidence))
            {
                case CheckResult.Fail:
                    failedCheckIds.Add(requiredCheck.CheckId);
                    break;

                case CheckResult.Unknown:
                    unknownCheckIds.Add(requiredCheck.CheckId);
                    break;

                case CheckResult.Pass:
                    passingCheckCount++;
                    break;
            }
        }

        var completionScore = (double)passingCheckCount / requiredChecks.Count;

        // The evidence backing the outcome, in the order it was produced (Outcome.Evidence's own
        // contract) -- the original evidence list's own order, not the order requiredChecks
        // happened to name checks in. Drawn only from the already round/revision-scoped selection.
        var contributingEvidence = selectedEvidence.Where(e => contributingEvidenceIds.Contains(e.EvidenceId)).ToList();

        if (failedCheckIds.Count > 0)
        {
            return new VerificationResult(
                new Outcome(
                    TaskVerificationStatus.Failed,
                    contributingEvidence,
                    $"Required check(s) resolved to Fail in the host-closed verification round: {string.Join(", ", failedCheckIds)}; a Fail always dominates a Pass recorded for the same check.",
                    evaluatedAt),
                completionScore,
                RuleVersion);
        }

        if (unknownCheckIds.Count > 0)
        {
            return new VerificationResult(
                new Outcome(
                    TaskVerificationStatus.Unknown,
                    contributingEvidence,
                    $"Required check(s) have no conclusive Pass/Fail evidence (missing, errored, or genuinely inconclusive) in the host-closed verification round: {string.Join(", ", unknownCheckIds)}.",
                    evaluatedAt),
                completionScore,
                RuleVersion);
        }

        return new VerificationResult(
            new Outcome(
                TaskVerificationStatus.Verified,
                contributingEvidence,
                "Every required check conclusively resolved to Pass in the host-closed verification round for the current artifact revision.",
                evaluatedAt),
            completionScore,
            RuleVersion);
    }

    private static VerificationResult UnknownResult(string reason, DateTimeOffset evaluatedAt) =>
        new(new Outcome(TaskVerificationStatus.Unknown, [], reason, evaluatedAt), CompletionScore: 0.0, RuleVersion);

    /// <summary>
    /// Resolves one required check's verdict from the (possibly empty) selected evidence carrying
    /// its <c>CheckId</c>: missing evidence is <see cref="CheckResult.Unknown"/>; any
    /// <see cref="CheckResult.Fail"/> dominates; otherwise any <see cref="CheckResult.Unknown"/>
    /// (with no <see cref="CheckResult.Fail"/> present) resolves to <see cref="CheckResult.Unknown"/>;
    /// only all-<see cref="CheckResult.Pass"/> resolves to <see cref="CheckResult.Pass"/>. A
    /// <see cref="Evidence.Result"/> outside the defined <see cref="CheckResult"/> values is never
    /// treated as a pass -- it resolves to <see cref="CheckResult.Unknown"/>.
    /// </summary>
    private static CheckResult ResolveCheck(IReadOnlyList<Evidence> checkEvidence)
    {
        if (checkEvidence.Count == 0)
        {
            return CheckResult.Unknown;
        }

        if (checkEvidence.Any(e => e.Result == CheckResult.Fail))
        {
            return CheckResult.Fail;
        }

        return checkEvidence.All(e => e.Result == CheckResult.Pass) ? CheckResult.Pass : CheckResult.Unknown;
    }
}
