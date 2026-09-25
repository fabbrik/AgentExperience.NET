namespace AgentExperience.Abstractions;

/// <summary>
/// A durable, scoped Experience Record: the canonical, immutable snapshot of what was learned from
/// one captured <see cref="ExperienceRun"/> -- its attempts, verified outcome, reflection, environment,
/// provenance, lifecycle status, and reuse-confidence inputs. Schema versioning of the persisted
/// form is owned by the storage adapter; this contract carries no version field.
/// </summary>
/// <param name="ExperienceId">Unique identifier for this record. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="SourceRunId">The <see cref="ExperienceRun.RunId"/> this record was derived from.</param>
/// <param name="Scope">The tenancy/ownership scope this record belongs to. Required fields must be non-blank; optional fields are either <see langword="null"/> or non-blank.</param>
/// <param name="TaskId">Identifies which task this experience is about. Must be non-blank.</param>
/// <param name="TaskSummary">Optional, sanitized, human-readable summary of the task.</param>
/// <param name="Attempts">The observable attempts from the source run, in the order they occurred.</param>
/// <param name="Outcome">The task verification outcome the record was finalized against.</param>
/// <param name="CompletionScore">The fraction of required checks that conclusively passed, in [0, 1]. Not reuse confidence.</param>
/// <param name="Reflection">The auditable reflection derived from the run; <see langword="null"/> when the record is quarantined without an eligible lesson.</param>
/// <param name="Environment">The runtime environment the source run executed in.</param>
/// <param name="Provenance">Where this record's source capture originated.</param>
/// <param name="Status">The record's current lifecycle status.</param>
/// <param name="ReuseConfidence">The record's current reuse confidence, in [0, 1].</param>
/// <param name="SupportingValidations">Non-negative count of validations supporting reuse, kept so confidence can be recomputed.</param>
/// <param name="Contradictions">Non-negative count of contradictions observed against reuse, kept so confidence can be recomputed.</param>
/// <param name="Revision">Non-negative revision number used for optimistic concurrency by lifecycle commits.</param>
/// <param name="CreatedAt">When the record was created. Persisted and returned in UTC.</param>
/// <param name="UpdatedAt">When the record was last changed. Persisted and returned in UTC.</param>
public sealed record ExperienceRecord(
    Guid ExperienceId,
    Guid SourceRunId,
    Scope Scope,
    string TaskId,
    string? TaskSummary,
    IReadOnlyList<Attempt> Attempts,
    Outcome Outcome,
    double CompletionScore,
    Reflection? Reflection,
    EnvironmentFingerprint Environment,
    Provenance Provenance,
    ExperienceStatus Status,
    double ReuseConfidence,
    int SupportingValidations,
    int Contradictions,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The verification round finalization closed for <see cref="SourceRunId"/> -- the
    /// <c>ClosedRound</c> of the evaluation this record was finalized against -- or <see langword="null"/>
    /// when no round was closed, or when the record was written before this was recorded.
    /// </summary>
    /// <remarks>
    /// It is what lets confidence evidence name a verification round the library itself saw closed:
    /// machine evidence observed in a run counts only against the round that run was finalized with, so a
    /// round invented by the caller is refused rather than becoming a fresh independence key. A record
    /// written by hand through <see cref="IExperienceRecordStore.CreateAsync"/> carries whatever its
    /// writer set, which is that writer's statement -- and, unless it is marked
    /// <see cref="ExperienceRecordOrigin.Finalized"/>, one that confidence verification does not rely on.
    /// Must not be <see cref="Guid.Empty"/> when set.
    /// </remarks>
    public Guid? ClosedRoundId { get; init; }

    /// <summary>
    /// Who wrote this record: <see cref="ExperienceRecordOrigin.Finalized"/> when finalization derived it
    /// from a captured run, and <see cref="ExperienceRecordOrigin.HostWritten"/> (the default) otherwise.
    /// </summary>
    /// <remarks>
    /// Confidence verification treats only a finalized record as the library's own knowledge of its source
    /// run: its <see cref="SourceRunId"/>, <see cref="ClosedRoundId"/> and the exposures in its
    /// <see cref="ExperienceRecord.Provenance"/>. A run known only through a hand-written record is refused
    /// (<c>IndependenceRefusal.HostWrittenRun</c>), so a record written through
    /// <see cref="IExperienceRecordStore.CreateAsync"/> without finalization -- a direct aggregator result
    /// included -- vouches for no run, round or exposure by default. It is a marker, not a signature: a host
    /// writing through the store port can set <see cref="ExperienceRecordOrigin.Finalized"/> itself, and is
    /// then making that statement. Records stored before this was recorded read back as
    /// <see cref="ExperienceRecordOrigin.HostWritten"/>.
    /// </remarks>
    public ExperienceRecordOrigin Origin { get; init; } = ExperienceRecordOrigin.HostWritten;
}

/// <summary>
/// Who wrote an <see cref="ExperienceRecord"/>, which decides whether confidence verification relies on what
/// it says about its source run.
/// </summary>
public enum ExperienceRecordOrigin
{
    /// <summary>
    /// Written by a host through <see cref="IExperienceRecordStore.CreateAsync"/> without finalization, or
    /// stored before the origin was recorded. Unverified: its source run, round and exposures are its
    /// writer's statement, and vouch for no run in confidence verification. The default.
    /// </summary>
    HostWritten = 0,

    /// <summary>
    /// Derived by <c>ExperienceFinalizationService</c> from a run the capture service held, with the round
    /// finalization closed and the exposures the run recorded.
    /// </summary>
    Finalized = 1,
}
