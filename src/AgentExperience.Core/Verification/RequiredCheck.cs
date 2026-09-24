namespace AgentExperience.Core.Verification;

/// <summary>
/// One check a task declares as required for verification, and the kind of evaluator that is
/// allowed to satisfy it.
/// </summary>
/// <remarks>
/// <para>
/// Matching a required check by <see cref="CheckId"/> alone lets any producer claim any check: a
/// human approval, for instance, could satisfy a check the task meant to be answered by a test run.
/// <see cref="ExpectedKind"/> closes that gap, and it is required, so kind matching is default-deny.
/// <see cref="VerificationAggregator.Aggregate"/> counts a piece of
/// <see cref="AgentExperience.Abstractions.Evidence"/> for this check only when the evidence's own
/// <see cref="AgentExperience.Abstractions.Evidence.Kind"/> equals it (ordinal, case-sensitive);
/// evidence of any other kind is ignored entirely, exactly as if it had been recorded for a
/// different <see cref="CheckId"/>. A check whose evidence is all ignored has no evidence at all and
/// therefore resolves to <see cref="AgentExperience.Abstractions.CheckResult.Unknown"/> -- a
/// mismatched evaluator can never turn a check into a pass.
/// </para>
/// <para>
/// Accepting evidence of any kind is an explicit, visible opt-in: pass <see cref="AnyKind"/> as the
/// <see cref="ExpectedKind"/>. A <see langword="null"/> or blank <see cref="ExpectedKind"/> never means
/// "any": <see cref="VerificationAggregator.Aggregate"/> refuses it with an
/// <see cref="ArgumentException"/>, and <see cref="Accepts"/> accepts nothing for it.
/// </para>
/// </remarks>
/// <param name="CheckId">The task-declared required check ID. Must be non-blank and unique within one aggregation.</param>
/// <param name="ExpectedKind">
/// The <see cref="AgentExperience.Abstractions.Evidence.Kind"/> that may satisfy this check (e.g.
/// <c>"TestResult"</c>, <c>"ToolExitCode"</c>, <c>"HumanApproval"</c>), or <see cref="AnyKind"/> to
/// accept evidence of any kind. Must be non-blank.
/// </param>
public sealed record RequiredCheck(string CheckId, string ExpectedKind)
{
    /// <summary>
    /// The explicit wildcard <see cref="ExpectedKind"/>: a check declared with it accepts evidence of
    /// any kind. The value is <c>"*"</c>; it is compared ordinally, like any other kind.
    /// </summary>
    public const string AnyKind = "*";

    /// <summary>Whether this check was declared with <see cref="AnyKind"/>, and so accepts evidence of any kind.</summary>
    public bool AcceptsAnyKind => string.Equals(ExpectedKind, AnyKind, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="evidenceKind"/> is allowed to satisfy this check: always
    /// <see langword="true"/> for a check declared with <see cref="AnyKind"/>, otherwise an exact
    /// ordinal match. A check whose <see cref="ExpectedKind"/> is <see langword="null"/> or blank
    /// accepts nothing.
    /// </summary>
    /// <param name="evidenceKind">The candidate evidence's own <c>Kind</c>.</param>
    public bool Accepts(string? evidenceKind) =>
        AcceptsAnyKind
        || (!string.IsNullOrWhiteSpace(ExpectedKind) && string.Equals(ExpectedKind, evidenceKind, StringComparison.Ordinal));
}
