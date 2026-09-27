using AgentExperience.Abstractions;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// Grades how closely an eligible record's environment fits the one a retrieval prefers. Its value
/// becomes the record's <see cref="RankingComponentKind.EnvironmentCompatibility"/> component.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ranking only, never eligibility.</b> The scorer is called only for records that already
/// passed every eligibility check, required environment attributes included, so it can reorder
/// records but never admit or exclude one.
/// </para>
/// <para>
/// <b>Called for every eligible record.</b> When the request prefers nothing,
/// <c>preferredAttributes</c> is an empty dictionary rather than <see langword="null"/>, so a host
/// scorer can grade on the fingerprint alone.
/// </para>
/// <para>
/// <b>The value is untrusted.</b> Retrieval clamps it to [0, 1] and scores a NaN as 0. A scorer
/// that throws fails the whole retrieval closed: the result is
/// <see cref="RetrievalOutcome.Failed"/> with no records, never a partial ranking. The
/// <see cref="RetrievalFailure"/>'s reason is fixed and content-free; its exception is the
/// scorer's own, passed through as is, so treat it as local diagnostics. An
/// <see cref="OperationCanceledException"/> thrown while the caller's token is cancelled
/// propagates unwrapped instead.
/// </para>
/// <para>
/// It runs synchronously, after both channels have answered, and is not bounded by
/// <see cref="RetrievalPolicy.Timeout"/>. The caller's cancellation token is checked between
/// records, not during a call, so a scorer should be cheap and must not block.
/// </para>
/// <para>
/// It must be thread-safe: one instance serves concurrent retrievals. With
/// <c>AddAgentExperienceRetrieval</c> it is resolved once, from the root provider, so register it
/// as a singleton.
/// </para>
/// </remarks>
public interface IEnvironmentCompatibilityScorer
{
    /// <summary>Grades one eligible record's environment against the preferred attributes.</summary>
    /// <param name="recordEnvironment">The environment the record was captured in.</param>
    /// <param name="preferredAttributes">The attributes the request prefers; empty when it prefers none.</param>
    /// <returns>A compatibility in [0, 1], 1 being the closest fit. Values outside it are clamped, and NaN counts as 0.</returns>
    double Score(EnvironmentFingerprint recordEnvironment, IReadOnlyDictionary<string, string> preferredAttributes);
}
