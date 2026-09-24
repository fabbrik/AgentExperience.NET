using AgentExperience.Abstractions;

namespace AgentExperience.Core.Verification;

/// <summary>
/// The result of one <see cref="VerificationAggregator.Aggregate"/> call: the resulting
/// <see cref="AgentExperience.Abstractions.Outcome"/> (the actual task verification verdict), a
/// versioned <see cref="CompletionScore"/> reported alongside it -- never as a substitute for it --
/// and the <see cref="Basis"/> it was computed from. A host must always read
/// <see cref="Outcome"/>.<c>Status</c> to know whether a task verified; a high
/// <see cref="CompletionScore"/> next to an <see cref="TaskVerificationStatus.Unknown"/>/
/// <see cref="TaskVerificationStatus.Failed"/> <see cref="Outcome"/> is not a pass and must never be
/// treated as one.
/// </summary>
/// <remarks>
/// Only <see cref="VerificationAggregator.Aggregate"/> produces one: there is no public constructor
/// and every property is get-only, so an evaluation cannot be constructed except by aggregating, or
/// re-pointed at another run with <c>new</c> or <c>with</c>. (Aggregating is public, so what an
/// evaluation says is still only as true as the evidence and run ID its caller supplied.) For the
/// same reason it cannot be deserialized. That is what lets
/// <see cref="AgentExperience.Core.Reflections.ReflectionRequest"/> trust <see cref="Basis"/> when it
/// refuses to pair an evaluation with a run it was not computed for.
/// </remarks>
public sealed record VerificationResult
{
    internal VerificationResult(Outcome outcome, double completionScore, string ruleVersion, VerificationBasis basis)
    {
        Outcome = outcome;
        CompletionScore = completionScore;
        RuleVersion = ruleVersion;
        Basis = basis;
    }

    /// <summary>The aggregated task verification outcome: status, backing evidence (in the order it was produced), an auditable reason, and when it was evaluated.</summary>
    public Outcome Outcome { get; }

    /// <summary>
    /// The fraction (between <c>0.0</c> and <c>1.0</c>) of required checks that conclusively passed in
    /// the selected round; <c>0</c> for an empty required-check set. Distinct from reuse confidence
    /// and never alone grants verification -- it is reported alongside <see cref="Outcome"/>, not
    /// folded into it.
    /// </summary>
    public double CompletionScore { get; }

    /// <summary>Identifies the aggregation rule version this result was computed under, so a future rule change stays auditable against results computed under an earlier one.</summary>
    public string RuleVersion { get; }

    /// <summary>The run, closed round, artifact revision and required checks this result was computed from, exactly as the aggregation was called with them.</summary>
    public VerificationBasis Basis { get; }
}

/// <summary>
/// What one <see cref="VerificationResult"/> was computed from, recorded by
/// <see cref="VerificationAggregator.Aggregate"/> so the result stays bound to it.
/// </summary>
/// <remarks>
/// The aggregator records the <see cref="RunId"/> its caller names; it cannot check it.
/// <see cref="AgentExperience.Abstractions.Evidence"/> carries no run ID, so which run a round's
/// evidence belongs to is the host's statement, like every other host-supplied identifier.
/// </remarks>
/// <param name="RunId">The run the caller evaluated.</param>
/// <param name="ClosedRound">The host-closed round the evaluation read, or <see langword="null"/> when none had been closed.</param>
/// <param name="CurrentArtifactRevision">The artifact revision verification was judged against.</param>
/// <param name="RequiredChecks">The required checks, in the order the caller declared them. A read-only copy taken at aggregation.</param>
public sealed record VerificationBasis(
    Guid RunId,
    ClosedVerificationRound? ClosedRound,
    string CurrentArtifactRevision,
    IReadOnlyList<RequiredCheck> RequiredChecks)
{
    /// <summary>Value equality, with <see cref="RequiredChecks"/> compared element by element rather than by reference.</summary>
    /// <param name="other">The basis to compare with.</param>
    /// <returns>Whether the two bases name the same run, round, revision and checks, in the same order.</returns>
    public bool Equals(VerificationBasis? other) =>
        other is not null
        && RunId == other.RunId
        && Equals(ClosedRound, other.ClosedRound)
        && string.Equals(CurrentArtifactRevision, other.CurrentArtifactRevision, StringComparison.Ordinal)
        && (ReferenceEquals(RequiredChecks, other.RequiredChecks)
            || (RequiredChecks is not null && other.RequiredChecks is not null && RequiredChecks.SequenceEqual(other.RequiredChecks)));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RunId);
        hash.Add(ClosedRound);
        hash.Add(CurrentArtifactRevision, StringComparer.Ordinal);
        foreach (var check in RequiredChecks ?? [])
        {
            hash.Add(check);
        }

        return hash.ToHashCode();
    }
}
