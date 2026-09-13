using AgentExperience.Abstractions;

namespace AgentExperience.Core.Verification;

/// <summary>
/// The result of one <see cref="VerificationAggregator.Aggregate"/> call: the resulting
/// <see cref="AgentExperience.Abstractions.Outcome"/> (the actual task verification verdict) plus a
/// versioned <see cref="CompletionScore"/> reported alongside it -- never as a substitute for it. A
/// host must always read <see cref="Outcome"/>.<c>Status</c> to know whether a task verified; a high
/// <see cref="CompletionScore"/> next to an <see cref="TaskVerificationStatus.Unknown"/>/
/// <see cref="TaskVerificationStatus.Failed"/> <see cref="Outcome"/> is not a pass and must never be
/// treated as one.
/// </summary>
/// <param name="Outcome">The aggregated task verification outcome: status, backing evidence (in the order it was produced), an auditable reason, and when it was evaluated.</param>
/// <param name="CompletionScore">
/// The fraction (between <c>0.0</c> and <c>1.0</c>) of required checks that conclusively passed in
/// the selected round; <c>0</c> for an empty required-check set. Distinct from reuse confidence
/// (introduced in a later epic) and never alone grants verification -- it is reported alongside
/// <see cref="Outcome"/>, not folded into it.
/// </param>
/// <param name="RuleVersion">Identifies the aggregation rule version this result was computed under, so a future rule change stays auditable against results computed under an earlier one.</param>
public sealed record VerificationResult(Outcome Outcome, double CompletionScore, string RuleVersion);
