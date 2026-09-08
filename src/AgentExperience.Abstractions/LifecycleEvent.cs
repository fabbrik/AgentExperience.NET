namespace AgentExperience.Abstractions;

/// <summary>
/// The canonical lifecycle states an Experience Record can occupy. Ownership of the state
/// machine and valid transitions belongs to a later story; this package fixes only the shape of
/// the enum and the event that carries transitions between its values.
/// </summary>
public enum ExperienceStatus
{
    /// <summary>Newly captured; not yet validated for reuse.</summary>
    Candidate,

    /// <summary>Verified and authorized for reuse.</summary>
    Validated,

    /// <summary>Withheld from reuse pending review (e.g. suspected sanitization gap or policy concern).</summary>
    Quarantined,

    /// <summary>Under active dispute (e.g. a correction or conflicting evidence was submitted).</summary>
    Contested,

    /// <summary>No longer current but not yet superseded or revoked; reuse confidence should be discounted.</summary>
    Stale,

    /// <summary>Replaced by a newer Experience Record.</summary>
    Superseded,

    /// <summary>Withdrawn from reuse by an authorized action; no longer eligible for retrieval or injection.</summary>
    Revoked,

    /// <summary>Reuse was observed to succeed again, reinforcing confidence in a previously validated record.</summary>
    Reinforced,
}

/// <summary>
/// An append-only record of a single lifecycle state transition for an Experience Record.
/// Lifecycle changes are events first; current state is a projection derived from them. This
/// package defines only the event's data shape — transition validity rules belong to a later
/// story.
/// </summary>
/// <param name="EventId">Unique identifier for this lifecycle event.</param>
/// <param name="ExperienceRecordId">The Experience Record this event applies to.</param>
/// <param name="PriorStatus">The status before this transition; <see langword="null"/> when this is the record's first lifecycle event.</param>
/// <param name="CurrentStatus">The status this transition moves the record to.</param>
/// <param name="Reason">Auditable, human-readable reason for the transition. Never private reasoning.</param>
/// <param name="Producer">Identity of whatever produced this transition (a policy, an evaluator, or a human principal identifier). Not tied to any identity-provider shape.</param>
/// <param name="OccurredAt">When this transition occurred.</param>
/// <param name="ExpectedRevision">The Experience Record revision this event was appended against, for optimistic-concurrency enforcement by the store that applies it.</param>
public sealed record LifecycleEvent(
    Guid EventId,
    Guid ExperienceRecordId,
    ExperienceStatus? PriorStatus,
    ExperienceStatus CurrentStatus,
    string Reason,
    string Producer,
    DateTimeOffset OccurredAt,
    long ExpectedRevision);
