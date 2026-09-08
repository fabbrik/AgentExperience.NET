namespace AgentExperience.Abstractions;

/// <summary>
/// A structured, auditable reflection derived from an <see cref="ExperienceRun"/>'s attempts and
/// its <see cref="AgentExperience.Abstractions.Outcome"/>. Reflections must be traceable to
/// evidence and evaluation rather than invented, and must never require private chain-of-thought
/// as an input or storage requirement. The shape defined here is a stable contract; alternate
/// <c>IExperienceReflector</c> implementations (defined outside this package) must preserve it.
/// </summary>
/// <param name="ReflectionId">Unique identifier for this reflection.</param>
/// <param name="ExperienceRunId">The run this reflection was derived from.</param>
/// <param name="Lesson">The central, auditable lesson learned from the run.</param>
/// <param name="SuccessfulApproaches">Approaches observed to work, traceable back to the run's attempts.</param>
/// <param name="FailedApproaches">Approaches observed not to work, traceable back to the run's attempts.</param>
/// <param name="Preconditions">Conditions this reflection assumes to hold; preconditions that could not be confirmed must still be listed, marked as unknown in <see cref="Warnings"/> rather than omitted.</param>
/// <param name="Warnings">Explicit uncertainty or caveats, including unverified preconditions or unverified outcomes that must not be presented as validated procedure.</param>
/// <param name="ReuseGuidance">Optional guidance on when/how this reflection may be safely reused.</param>
/// <param name="EvidenceIds">Identifiers of the <see cref="AgentExperience.Abstractions.Evidence"/> this reflection is traceable to.</param>
/// <param name="CreatedAt">When this reflection was generated.</param>
public sealed record Reflection(
    Guid ReflectionId,
    Guid ExperienceRunId,
    string Lesson,
    IReadOnlyList<string> SuccessfulApproaches,
    IReadOnlyList<string> FailedApproaches,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Warnings,
    string? ReuseGuidance,
    IReadOnlyList<Guid> EvidenceIds,
    DateTimeOffset CreatedAt);
