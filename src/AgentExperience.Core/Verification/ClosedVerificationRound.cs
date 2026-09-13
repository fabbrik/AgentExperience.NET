namespace AgentExperience.Core.Verification;

/// <summary>
/// The host-established, non-agent-overridable authority for exactly which verification round and
/// artifact revision <see cref="VerificationAggregator.Aggregate"/> is permitted to read evidence
/// from. Only a host closes a round (deciding "this is the final round for this artifact
/// revision"); nothing in this package accepts an agent-supplied round or revision selection as a
/// substitute -- <see cref="VerificationAggregator.Aggregate"/> takes only this type (or
/// <see langword="null"/>, meaning no round has been closed yet) to decide what counts, mirroring
/// how <see cref="AgentExperience.Abstractions.AuthorizationContext"/> is a host-established trust
/// boundary a request can never widen.
/// </summary>
/// <param name="RoundId">The verification round the host has closed as final for this evaluation.</param>
/// <param name="ArtifactRevision">The artifact revision the host has closed <paramref name="RoundId"/> against. Aggregation treats any evidence recorded under a different revision as stale, even if its <see cref="AgentExperience.Abstractions.Evidence.VerificationRoundId"/> matches.</param>
public sealed record ClosedVerificationRound(Guid RoundId, string ArtifactRevision);
