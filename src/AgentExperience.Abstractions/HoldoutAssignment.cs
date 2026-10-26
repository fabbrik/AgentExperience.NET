namespace AgentExperience.Abstractions;

/// <summary>Which arm of the injection holdout a captured run was assigned to.</summary>
public enum HoldoutArm
{
    /// <summary>The block was injected as usual.</summary>
    Treatment = 0,

    /// <summary>The block was built and then withheld: the run's model never saw it.</summary>
    Holdout = 1,
}

/// <summary>
/// One captured run's holdout assignment, as <see cref="IExperienceRecordStore.RecordHoldoutAssignmentAsync"/> records
/// it: the run, its arm, the records its block named, and when. IDs and revisions only, never content.
/// </summary>
/// <param name="RunId">The captured run. Never <see cref="Guid.Empty"/>.</param>
/// <param name="Arm">Whether the block was delivered or withheld.</param>
/// <param name="Records">
/// The records the block named, each at the revision it was rendered from: exactly what was delivered
/// (<see cref="HoldoutArm.Treatment"/>) or would have been (<see cref="HoldoutArm.Holdout"/>).
/// </param>
/// <param name="AssignedAt">When the arm was decided, by the caller's clock. Must be set.</param>
public sealed record HoldoutAssignment(Guid RunId, HoldoutArm Arm, IReadOnlyList<RunExposure> Records, DateTimeOffset AssignedAt);

/// <summary>The result of <see cref="IExperienceRecordStore.RecordHoldoutAssignmentAsync"/>.</summary>
/// <param name="Outcome"><see cref="ExperienceStoreOutcome.Committed"/>, <see cref="ExperienceStoreOutcome.Denied"/> or <see cref="ExperienceStoreOutcome.Invalid"/>.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceHoldoutRecordResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<StoreValidationError> Errors)
{
    /// <summary>
    /// What the port's default implementation answers: <see cref="ExperienceStoreOutcome.Committed"/> with
    /// <see cref="Tracked"/> <see langword="false"/>, having recorded nothing.
    /// </summary>
    public static ExperienceHoldoutRecordResult NotTracked { get; } = new(ExperienceStoreOutcome.Committed, []) { Tracked = false };

    /// <summary>Whether the store records holdout assignments at all. <see langword="false"/> only from the port's default implementation.</summary>
    public bool Tracked { get; init; } = true;
}
