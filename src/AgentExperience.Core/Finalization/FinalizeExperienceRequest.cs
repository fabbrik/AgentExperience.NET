using AgentExperience.Abstractions;
using AgentExperience.Core.Verification;

namespace AgentExperience.Core.Finalization;

/// <summary>
/// One request to turn a captured, completed run into a durable Experience Record, submitted to
/// <see cref="ExperienceFinalizationService.FinalizeAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// No evaluation is accepted here. The request carries the host-closed round, the artifact revision,
/// the declared required checks, and the evidence; finalization runs
/// <see cref="VerificationAggregator.Aggregate"/> itself against exactly those, so an evaluation
/// computed for some other run can never be substituted for this one's.
/// </para>
/// <para>
/// <see cref="FinalizedAt"/> is caller-supplied rather than read from a clock so a retry of the same
/// run is the same request. The record ID, the reflection ID, and the initial lifecycle event ID are
/// all derived from <see cref="RunId"/>, so a retry can never produce a second record or a second
/// initial confirmation (see <see cref="ExperienceFinalizationService"/>).
/// </para>
/// </remarks>
/// <param name="RunId">The captured run to finalize. Must not be <see cref="Guid.Empty"/>; the run must exist in the capture service and must already carry an <see cref="ExperienceRun.ExecutionStatus"/>.</param>
/// <param name="Authorization">What the host has established the caller may do. The run's own <see cref="ExperienceRun.Scope"/> must lie within it or nothing is stored.</param>
/// <param name="ClosedRound">The verification round the host closed for this run, or <see langword="null"/> when the host has closed none (which evaluates to <see cref="TaskVerificationStatus.Unknown"/>).</param>
/// <param name="RequiredChecks">The task's declared required checks. An empty set can never verify.</param>
/// <param name="Evidence">All evidence available for this run, in the order it was produced. Aggregation filters it to <paramref name="ClosedRound"/> and <paramref name="CurrentArtifactRevision"/> itself.</param>
/// <param name="CurrentArtifactRevision">The artifact revision verification is being judged against. Must be non-blank.</param>
/// <param name="StorageDecision">The host's storage-policy decision. A decision that does not permit storage writes nothing at all.</param>
/// <param name="FinalizedAt">When the host decided to finalize this run. Stamped onto the record and its initial lifecycle event (truncated to whole microseconds in UTC, which is the precision the store keeps).</param>
public sealed record FinalizeExperienceRequest(
    Guid RunId,
    AuthorizationContext Authorization,
    ClosedVerificationRound? ClosedRound,
    IReadOnlyList<RequiredCheck> RequiredChecks,
    IReadOnlyList<Evidence> Evidence,
    string CurrentArtifactRevision,
    StorageDecision StorageDecision,
    DateTimeOffset FinalizedAt);
