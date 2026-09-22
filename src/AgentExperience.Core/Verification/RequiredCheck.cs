namespace AgentExperience.Core.Verification;

/// <summary>
/// One check a task declares as required for verification, and (optionally) the kind of evaluator
/// that is allowed to satisfy it.
/// </summary>
/// <remarks>
/// <para>
/// Matching a required check by <see cref="CheckId"/> alone lets any producer claim any check: a
/// human approval, for instance, could satisfy a check the task meant to be answered by a test run.
/// <see cref="ExpectedKind"/> closes that gap. When it is non-<see langword="null"/>,
/// <see cref="VerificationAggregator.Aggregate"/> counts a piece of
/// <see cref="AgentExperience.Abstractions.Evidence"/> for this check only when the evidence's own
/// <see cref="AgentExperience.Abstractions.Evidence.Kind"/> equals it (ordinal, case-sensitive);
/// evidence of any other kind is ignored entirely, exactly as if it had been recorded for a
/// different <see cref="CheckId"/>. A check whose evidence is all ignored has no evidence at all and
/// therefore resolves to <see cref="AgentExperience.Abstractions.CheckResult.Unknown"/> -- a
/// mismatched evaluator can never turn a check into a pass.
/// </para>
/// <para>
/// A <see langword="null"/> <see cref="ExpectedKind"/> accepts evidence of any kind, which is the
/// behaviour this type replaced (checks were previously declared as bare <c>CheckId</c> strings).
/// </para>
/// </remarks>
/// <param name="CheckId">The task-declared required check ID. Must be non-blank and unique within one aggregation.</param>
/// <param name="ExpectedKind">
/// The <see cref="AgentExperience.Abstractions.Evidence.Kind"/> that may satisfy this check (e.g.
/// <c>"TestResult"</c>, <c>"ToolExitCode"</c>, <c>"HumanApproval"</c>), or <see langword="null"/> to
/// accept any kind. When supplied it must be non-blank.
/// </param>
public sealed record RequiredCheck(string CheckId, string? ExpectedKind = null)
{
    /// <summary>
    /// Whether <paramref name="evidenceKind"/> is allowed to satisfy this check: always
    /// <see langword="true"/> when no <see cref="ExpectedKind"/> is named, otherwise an exact
    /// ordinal match.
    /// </summary>
    /// <param name="evidenceKind">The candidate evidence's own <c>Kind</c>.</param>
    public bool Accepts(string? evidenceKind) =>
        ExpectedKind is null || string.Equals(ExpectedKind, evidenceKind, StringComparison.Ordinal);
}
