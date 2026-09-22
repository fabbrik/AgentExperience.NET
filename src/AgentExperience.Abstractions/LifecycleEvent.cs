namespace AgentExperience.Abstractions;

/// <summary>
/// The canonical lifecycle states an Experience Record can occupy. This package fixes only the shape
/// of the enum and the event that carries transitions between its values; ownership of the state
/// machine and which transitions are valid belongs to Core's lifecycle service.
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
/// Facts about <see cref="ExperienceStatus"/> itself, as opposed to the state machine over it. Which
/// transitions are legal belongs to Core; which statuses describe a record that may still be reused is a
/// property of the enum's own members, spelled out here once so retrieval, indexing, and the storage
/// adapter that has to apply it inside a transaction all read the same list.
/// </summary>
public static class ExperienceStatuses
{
    /// <summary>
    /// The statuses in which a record may be retrieved, injected, indexed, or named as another record's
    /// replacement. Every other status describes a record that is withheld, disputed, out of date,
    /// already replaced, or withdrawn -- none of which may be handed to an agent as applicable
    /// experience.
    /// </summary>
    public static IReadOnlyList<ExperienceStatus> EligibleForReuse { get; } =
        [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    /// <summary>Whether a record in <paramref name="status"/> may still be reused.</summary>
    /// <param name="status">The status to test.</param>
    /// <returns><see langword="true"/> when the status is one of <see cref="EligibleForReuse"/>.</returns>
    public static bool IsEligibleForReuse(ExperienceStatus status) =>
        status is ExperienceStatus.Validated or ExperienceStatus.Reinforced;
}

/// <summary>
/// An append-only record of a single lifecycle state transition for an Experience Record.
/// Lifecycle changes are events first; current state is a projection derived from them. This
/// package defines only the event's data shape. Which transitions are valid is owned by Core's
/// lifecycle service, and the store enforces that decision against real state by matching
/// <see cref="PriorStatus"/> and <see cref="ExpectedRevision"/> when it applies the event.
/// </summary>
/// <param name="EventId">Unique identifier for this lifecycle event.</param>
/// <param name="ExperienceRecordId">The Experience Record this event applies to.</param>
/// <param name="PriorStatus">The status before this transition; <see langword="null"/> when this is the record's first lifecycle event.</param>
/// <param name="CurrentStatus">The status this transition moves the record to.</param>
/// <param name="Reason">Auditable, human-readable reason for the transition. Never private reasoning.</param>
/// <param name="Producer">Identity of whatever produced this transition (a policy, an evaluator, or a human principal identifier). Not tied to any identity-provider shape.</param>
/// <param name="OccurredAt">When this transition occurred.</param>
/// <param name="ExpectedRevision">The Experience Record revision this event was appended against, for optimistic-concurrency enforcement by the store that applies it.</param>
/// <param name="ReplacementExperienceId">
/// The Experience Record that replaces this one, for a transition to
/// <see cref="ExperienceStatus.Superseded"/>; <see langword="null"/> for every other transition. It is
/// a column on the event rather than a field of the record's payload because supersession is a fact
/// about <em>this transition</em>, and because the replacement chain is walked over the event log
/// itself when a store rejects a cycle. Which replacements are acceptable -- a different record, in
/// the same exact scope, currently eligible, and not one this record already replaces -- is decided by
/// Core before the event is stamped.
/// </param>
/// <param name="Confidence">
/// The evidence-based confidence movement this event applies, or <see langword="null"/> when the
/// transition carries none. It rides the lifecycle event rather than travelling a write path of its own,
/// so the evidence row, the counters, the score, the status change, and the audit entry are one
/// transaction under one idempotency key. Core computes every number on it from the record it read; a
/// store persists them as given and never derives a score. See <see cref="ConfidenceUpdate"/>.
/// </param>
public sealed record LifecycleEvent(
    Guid EventId,
    Guid ExperienceRecordId,
    ExperienceStatus? PriorStatus,
    ExperienceStatus CurrentStatus,
    string Reason,
    string Producer,
    DateTimeOffset OccurredAt,
    long ExpectedRevision,
    Guid? ReplacementExperienceId = null,
    ConfidenceUpdate? Confidence = null);
