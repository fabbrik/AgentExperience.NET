using System.Text.Json.Serialization;

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
/// <param name="VerificationStatus">The task verification status of the evaluation this reflection was derived from, copied exactly -- never re-judged by the reflector.</param>
/// <param name="CompletionScore">
/// The completion score (fraction of required checks that conclusively passed) of the evaluation
/// this reflection was derived from, copied exactly. It is <em>not</em> reuse confidence: reuse
/// confidence belongs to the durable Experience Record and is assigned and updated there, never on a
/// <see cref="Reflection"/>. A high completion score next to a non-<see cref="TaskVerificationStatus.Verified"/>
/// <paramref name="VerificationStatus"/> is not verification.
/// </param>
/// <param name="VerificationRuleVersion">The version of the verification rule the evaluation was computed under, so this reflection's basis stays auditable across rule changes.</param>
/// <param name="Producer">Identity (name and template/implementation version) of the reflector implementation that produced this reflection.</param>
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
    TaskVerificationStatus VerificationStatus,
    double CompletionScore,
    string VerificationRuleVersion,
    string Producer,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Who wrote this reflection's free text: <see cref="ReflectionAuthorship.Deterministic"/> (the default,
    /// and what every reflection written before this property existed reads back as) or
    /// <see cref="ReflectionAuthorship.Model"/>. It is recorded exactly as the reflector returned it. It is
    /// <b>self-declared</b>: a reflector that has a model write any of the free text must set
    /// <see cref="ReflectionAuthorship.Model"/> itself, or its lessons read as deterministic and escape both the content
    /// guard and the injection label. The one exception is the library's own model-backed reflector: a reflection whose
    /// <see cref="Producer"/> starts with <c>AgentExperience.ChatClientExperienceReflector/</c> (ordinal) counts as
    /// model-authored whatever this property says, so its records written before it declared authorship are covered
    /// too; no other producer is inferred from. A model-authored reflection (any value but
    /// <see cref="ReflectionAuthorship.Deterministic"/>, so a tampered or future value counts, or that producer) is held to
    /// finalization's content guard and labelled as such when injected. With provenance signing configured, this property
    /// and the free-text fields are covered by the record's version 2 signature, and a record whose content
    /// no such signature confirms counts as model-authored whatever this property says. Omitted from System.Text.Json output when it is
    /// <see cref="ReflectionAuthorship.Deterministic"/>, so a deterministic reflection serializes exactly as it
    /// did before this property existed, and an absent member reads back as <c>Deterministic</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReflectionAuthorship Authorship { get; init; }
}

/// <summary>
/// The conventions every component that decides a <see cref="Reflection"/>'s authorship applies, including a host's
/// own <see cref="IExperienceCandidateSource"/> or <see cref="IExperienceEmbeddingIndex"/> honouring
/// <c>ExcludeModelAuthored</c>.
/// </summary>
public static class ReflectionAuthorshipConventions
{
    /// <summary>
    /// The start of every <see cref="Reflection.Producer"/> the library's own model-backed reflector
    /// (<c>ChatClientExperienceReflector</c> in the MAF adapter) writes, in every version. A reflection whose producer
    /// starts with it, compared ordinally, is model-authored whatever its <see cref="Reflection.Authorship"/> says, so
    /// its records written before it declared authorship count too. No other producer is ever read.
    /// </summary>
    public const string LibraryModelReflectorProducerPrefix = "AgentExperience.ChatClientExperienceReflector/";
}

/// <summary>Who wrote a <see cref="Reflection"/>'s free-text fields.</summary>
public enum ReflectionAuthorship
{
    /// <summary>A deterministic reflector derived the text from the run, without a model. The default.</summary>
    Deterministic = 0,

    /// <summary>
    /// A model wrote the text from captured run output. Such text can be steered by what the run captured,
    /// so it is screened by a content guard and labelled as unverified guidance when injected.
    /// </summary>
    Model = 1,
}
