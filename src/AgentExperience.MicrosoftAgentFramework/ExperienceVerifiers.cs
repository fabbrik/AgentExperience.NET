using AgentExperience.Abstractions;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Reflections;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Ready-made <see cref="AgentExperienceOptions.Verify"/> callbacks, built from signals a host already has: the outcome
/// of a tool call, its own check, or a person's approval. Each member returns the exact delegate type of
/// <c>Verify</c>, so <c>options.Verify = ExperienceVerifiers.ToolSucceeded("run_tests");</c> works.
/// </summary>
/// <remarks>
/// <para>
/// A verifier decides only from facts the host's code produced -- the captured outcome of a tool call, or the host's
/// own delegate -- never from message text or model output. Returning <see langword="null"/> means the run is not
/// stored. A verifier that throws, like any <c>Verify</c> callback, leaves the run unstored and the failure reported
/// through <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>.
/// </para>
/// <para>
/// Each verifier declares one required check, with an expected evidence kind of its own
/// (<c>"ToolOutcome"</c>, <c>"HostCheck"</c> or <c>"HumanApproval"</c>), and evidence for it
/// built with <see cref="ExperienceVerificationContext.CreateEvidence"/>. The artifact revision defaults to
/// <see cref="UnversionedRevision"/>; pass a real one (a build identifier or a commit) when the artifact the agent works
/// on changes between runs.
/// </para>
/// </remarks>
public static class ExperienceVerifiers
{
    /// <summary>The artifact revision a verifier names when it is given none: <c>"unversioned"</c>.</summary>
    public const string UnversionedRevision = "unversioned";

    internal const string ToolOutcomeKind = "ToolOutcome";

    internal const string HostCheckKind = "HostCheck";

    internal const string HumanApprovalKind = "HumanApproval";

    /// <summary>
    /// Verifies a run by the outcome of its <b>last</b> call to <paramref name="toolName"/>: the calls are ordered by
    /// attempt sequence, then by call sequence within the attempt, across all of the run's attempts, and the last one
    /// decides. (A call's sequence number counts only within its attempt, so the attempt order comes first; a retry that
    /// finally passed is judged by its passing call.) A call that returned passes; a call that failed -- it threw, or the
    /// host's <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> named an error class for it -- fails, and the run
    /// is stored <c>Quarantined</c>. A run with no such call is not stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It decides from each captured call's <see cref="ToolCallRecord.ToolName"/> (ordinal match), sequence numbers and
    /// <see cref="ToolCallRecord.Error"/>, never from a result, so it works when sanitization redacts results. That also
    /// means it relies on capture to mark failures: a tool that reports failure in its return value (an exit code, an
    /// HTTP status) counts as returned unless <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> classifies it,
    /// for example with <see cref="ExperienceToolResultClassifiers.ExitCode"/>.
    /// </para>
    /// <para>
    /// The evidence is of kind <c>"ToolOutcome"</c>, produced by <c>"tool:" + toolName</c>, and its detail names only
    /// the failed call's error class.
    /// </para>
    /// </remarks>
    /// <param name="toolName">The tool's registered name, as capture records it.</param>
    /// <param name="checkId">The required check's identifier; <paramref name="toolName"/> when <see langword="null"/>.</param>
    /// <param name="artifactRevision">The artifact revision checked; <see cref="UnversionedRevision"/> when <see langword="null"/>.</param>
    /// <returns>A <see cref="AgentExperienceOptions.Verify"/> callback.</returns>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is blank, or <paramref name="checkId"/> or <paramref name="artifactRevision"/> is given but blank.</exception>
    public static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> ToolSucceeded(
        string toolName,
        string? checkId = null,
        string? artifactRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var check = checkId ?? toolName;
        ArgumentException.ThrowIfNullOrWhiteSpace(check, nameof(checkId));
        var revision = RevisionOf(artifactRevision);
        var producer = "tool:" + toolName;

        return (context, _) =>
        {
            ArgumentNullException.ThrowIfNull(context);

            ToolCallRecord? last = null;
            foreach (var attempt in context.Run.Attempts.OrderBy(attempt => attempt.SequenceNumber))
            {
                foreach (var call in attempt.ToolCalls.OrderBy(call => call.SequenceNumber))
                {
                    if (string.Equals(call.ToolName, toolName, StringComparison.Ordinal))
                    {
                        last = call;
                    }
                }
            }

            if (last is null)
            {
                return ValueTask.FromResult<ExperienceVerification?>(null);
            }

            var failed = last.Error is not null;
            var evidence = context.CreateEvidence(
                check,
                ToolOutcomeKind,
                failed ? CheckResult.Fail : CheckResult.Pass,
                producer,
                revision,
                failed ? $"The last {toolName} call failed: {ErrorClass.Of(last.Error)}." : $"The last {toolName} call returned.");
            return ValueTask.FromResult<ExperienceVerification?>(Single(check, ToolOutcomeKind, evidence, revision));
        };
    }

    /// <summary>
    /// Verifies a run by the host's own check: <see langword="true"/> passes, <see langword="false"/> fails (the run is
    /// stored <c>Quarantined</c>), and <see langword="null"/> means the run is not stored.
    /// </summary>
    /// <remarks>
    /// The evidence is of kind <c>"HostCheck"</c>, produced by <c>"host"</c>. The verdict must come from the
    /// host's own code (a test run, an exit code, a query), never from the model's claim.
    /// </remarks>
    /// <param name="checkId">The required check's identifier.</param>
    /// <param name="check">The host's check of the run.</param>
    /// <param name="artifactRevision">The artifact revision checked; <see cref="UnversionedRevision"/> when <see langword="null"/>.</param>
    /// <returns>A <see cref="AgentExperienceOptions.Verify"/> callback.</returns>
    /// <exception cref="ArgumentException"><paramref name="checkId"/> is blank, or <paramref name="artifactRevision"/> is given but blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="check"/> is <see langword="null"/>.</exception>
    public static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> Check(
        string checkId,
        Func<ExperienceVerificationContext, CancellationToken, ValueTask<bool?>> check,
        string? artifactRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        ArgumentNullException.ThrowIfNull(check);
        var revision = RevisionOf(artifactRevision);

        return async (context, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            if (await check(context, cancellationToken).ConfigureAwait(false) is not { } passed)
            {
                return null;
            }

            var evidence = context.CreateEvidence(checkId, HostCheckKind, passed ? CheckResult.Pass : CheckResult.Fail, "host", revision);
            return Single(checkId, HostCheckKind, evidence, revision);
        };
    }

    /// <summary>
    /// Verifies a run by a person's decision: approved passes, rejected fails (the run is stored <c>Quarantined</c>),
    /// and <see langword="null"/> means the run is not stored.
    /// </summary>
    /// <remarks>
    /// The evidence is of kind <c>"HumanApproval"</c>, and <see cref="ExperienceApproval.ApproverId"/> is stored verbatim
    /// as its producer, so pass an opaque identifier (a user's object ID), not an email address or a name. A blank
    /// approver identifier throws <see cref="InvalidOperationException"/> from the callback, so the run is not stored
    /// and the failure is reported. The approver must come from the host's own authentication.
    /// </remarks>
    /// <param name="checkId">The required check's identifier.</param>
    /// <param name="approve">The host's approval flow for the run.</param>
    /// <param name="artifactRevision">The artifact revision approved; <see cref="UnversionedRevision"/> when <see langword="null"/>.</param>
    /// <returns>A <see cref="AgentExperienceOptions.Verify"/> callback.</returns>
    /// <exception cref="ArgumentException"><paramref name="checkId"/> is blank, or <paramref name="artifactRevision"/> is given but blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="approve"/> is <see langword="null"/>.</exception>
    public static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> Approval(
        string checkId,
        Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceApproval?>> approve,
        string? artifactRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        ArgumentNullException.ThrowIfNull(approve);
        var revision = RevisionOf(artifactRevision);

        return async (context, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            if (await approve(context, cancellationToken).ConfigureAwait(false) is not { } approval)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(approval.ApproverId))
            {
                throw new InvalidOperationException(
                    $"The approval for check '{checkId}' names no approver; an approval must name who decided. The run is not stored.");
            }

            var evidence = context.CreateEvidence(
                checkId, HumanApprovalKind, approval.Approved ? CheckResult.Pass : CheckResult.Fail, approval.ApproverId, revision);
            return Single(checkId, HumanApprovalKind, evidence, revision);
        };
    }

    /// <summary>
    /// Verifies a run by every one of <paramref name="verifiers"/>: it runs them in order and returns one verification
    /// with all their required checks and evidence, so the run is stored <c>Validated</c> only when every check passes.
    /// When one returns <see langword="null"/>, it stops there (the rest are not run, so no person is asked for an
    /// approval needlessly) and the run is not stored.
    /// </summary>
    /// <remarks>
    /// The combined revision is the one revision the verifiers name other than <see cref="UnversionedRevision"/> (a
    /// verifier given no revision takes it, and its evidence is restamped with it), else <see cref="UnversionedRevision"/>.
    /// Two different explicit revisions, two verifiers requiring the same check identifier, or a <see langword="null"/>
    /// required check throw <see cref="InvalidOperationException"/> from the callback, so the run is not stored and the
    /// failure is reported.
    /// </remarks>
    /// <param name="verifiers">The verifiers, at least one.</param>
    /// <returns>A <see cref="AgentExperienceOptions.Verify"/> callback.</returns>
    /// <exception cref="ArgumentException"><paramref name="verifiers"/> is empty or holds <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="verifiers"/> is <see langword="null"/>.</exception>
    public static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> AllOf(
        params Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>>[] verifiers)
    {
        var all = Validate(verifiers);

        return async (context, cancellationToken) =>
        {
            var results = new List<ExperienceVerification>(all.Length);
            foreach (var verifier in all)
            {
                if (await verifier(context, cancellationToken).ConfigureAwait(false) is not { } result)
                {
                    return null;
                }

                results.Add(result);
            }

            var explicitRevisions = results
                .Select(result => result.ArtifactRevision)
                .Where(revision => !string.Equals(revision, UnversionedRevision, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (explicitRevisions.Count > 1)
            {
                throw new InvalidOperationException(
                    "AllOf combined verifiers that name different artifact revisions; give them all the same revision. The run is not stored.");
            }

            var revision = explicitRevisions.Count == 1 ? explicitRevisions[0] : UnversionedRevision;
            var checks = new List<RequiredCheck>();
            var evidence = new List<Evidence>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                foreach (var check in result.RequiredChecks)
                {
                    if (check is null)
                    {
                        throw new InvalidOperationException("AllOf combined a verification with a null required check. The run is not stored.");
                    }

                    if (!ids.Add(check.CheckId))
                    {
                        throw new InvalidOperationException(
                            $"AllOf combined verifiers that both require the check '{check.CheckId}'; give each its own check identifier. The run is not stored.");
                    }

                    checks.Add(check);
                }

                // Evidence of a verifier that took the default revision is restamped with the explicit one; any other
                // mismatch is left as it is, for Verify's own check to refuse.
                evidence.AddRange(result.Evidence.Select(item =>
                    item is not null
                    && string.Equals(result.ArtifactRevision, UnversionedRevision, StringComparison.Ordinal)
                    && string.Equals(item.ArtifactRevision, UnversionedRevision, StringComparison.Ordinal)
                        ? item with { ArtifactRevision = revision }
                        : item!));
            }

            return new ExperienceVerification(checks, evidence, revision);
        };
    }

    /// <summary>
    /// Verifies a run by the first of <paramref name="verifiers"/> that passes: it runs them in order and returns the
    /// first verification that passed, without running the rest. A verification passed when it requires at least one
    /// check and every required check has evidence for this round and the verification's revision, with that check's
    /// identifier and a kind it accepts (<see cref="RequiredCheck.AnyKind"/> accepts any), all of it
    /// <see cref="CheckResult.Pass"/> -- the rule finalization stores <c>Validated</c> by. When none
    /// passes, it returns the first one that returned a verification, so the failure is stored <c>Quarantined</c>; when
    /// all return <see langword="null"/>, the run is not stored.
    /// </summary>
    /// <param name="verifiers">The verifiers, at least one.</param>
    /// <returns>A <see cref="AgentExperienceOptions.Verify"/> callback.</returns>
    /// <exception cref="ArgumentException"><paramref name="verifiers"/> is empty or holds <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="verifiers"/> is <see langword="null"/>.</exception>
    public static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> AnyOf(
        params Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>>[] verifiers)
    {
        var all = Validate(verifiers);

        return async (context, cancellationToken) =>
        {
            ExperienceVerification? first = null;
            foreach (var verifier in all)
            {
                var result = await verifier(context, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    continue;
                }

                if (Passed(result, context.RoundId))
                {
                    return result;
                }

                first ??= result;
            }

            return first;
        };
    }

    /// <summary>Whether every required check has matching evidence for the round and revision, and all of it passed.</summary>
    private static bool Passed(ExperienceVerification verification, Guid roundId)
    {
        if (verification.RequiredChecks.Count == 0)
        {
            return false;
        }

        foreach (var check in verification.RequiredChecks)
        {
            if (check is null)
            {
                return false;
            }

            var matching = verification.Evidence
                .Where(evidence => evidence is not null
                    && evidence.VerificationRoundId == roundId
                    && string.Equals(evidence.ArtifactRevision, verification.ArtifactRevision, StringComparison.Ordinal)
                    && string.Equals(evidence.CheckId, check.CheckId, StringComparison.Ordinal)
                    && check.Accepts(evidence.Kind))
                .ToList();
            if (matching.Count == 0 || matching.Any(evidence => evidence.Result != CheckResult.Pass))
            {
                return false;
            }
        }

        return true;
    }

    private static string RevisionOf(string? artifactRevision)
    {
        if (artifactRevision is null)
        {
            return UnversionedRevision;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRevision);
        return artifactRevision;
    }

    private static ExperienceVerification Single(string checkId, string kind, Evidence evidence, string revision) =>
        new([new RequiredCheck(checkId, kind)], [evidence], revision);

    private static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>>[] Validate(
        Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>>[] verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        if (verifiers.Length == 0)
        {
            throw new ArgumentException("At least one verifier is required.", nameof(verifiers));
        }

        if (verifiers.Any(verifier => verifier is null))
        {
            throw new ArgumentException("A verifier is null.", nameof(verifiers));
        }

        // A copy, so a later change to the caller's array changes nothing.
        return [.. verifiers];
    }
}
