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

/// <summary>
/// One holdout assignment row as <see cref="IExperienceRecordStore.QueryHoldoutOutcomesAsync"/> reads it back: the run,
/// its arm, one record its block named, and the run's own verified outcome. IDs, an arm, an instant and a status:
/// never content.
/// </summary>
/// <param name="RunId">The captured run.</param>
/// <param name="Arm">Whether the block was delivered or withheld.</param>
/// <param name="ExperienceId">The record the block named: given to a <see cref="HoldoutArm.Treatment"/> run, denied a <see cref="HoldoutArm.Holdout"/> one.</param>
/// <param name="AssignedAt">When the arm was decided, as stored.</param>
/// <param name="RunOutcome">
/// The <see cref="Outcome.Status"/> of the run's own finalized record (the live record in exactly the row's scope whose
/// <see cref="ExperienceRecord.SourceRunId"/> is <paramref name="RunId"/>). <see langword="null"/> when there is no such
/// record: not finalized yet, finalized in another scope, or sealed under a destroyed key.
/// </param>
public sealed record HoldoutOutcomeRow(
    Guid RunId,
    HoldoutArm Arm,
    Guid ExperienceId,
    DateTimeOffset AssignedAt,
    TaskVerificationStatus? RunOutcome)
{
    /// <summary>
    /// The <see cref="EnvironmentFingerprint.Metadata"/> key capture records the run's model under: the model that
    /// consumed a run's lessons, and, on the record a run produced, the model that learned it.
    /// </summary>
    public const string ModelMetadataKey = "agentexperience.model";

    /// <summary>
    /// The model of the run's own finalized record (its <see cref="EnvironmentFingerprint.Metadata"/> entry
    /// <see cref="ModelMetadataKey"/>): the model the lessons were given to, or withheld from. <see langword="null"/> when
    /// the record carries none, when there is no such record (see <see cref="RunOutcome"/>), or from a store that does not
    /// read it.
    /// </summary>
    public string? RunModel { get; init; }

    /// <summary>
    /// The model of the lesson <see cref="ExperienceId"/> names (its live record's <see cref="EnvironmentFingerprint.Metadata"/>
    /// entry <see cref="ModelMetadataKey"/>, in exactly the row's scope): the model that learned it. <see langword="null"/>
    /// when the record carries none, is gone, lies outside that scope or the caller's authorization, or is sealed under a
    /// destroyed key, or from a store that does not read it.
    /// </summary>
    public string? LessonModel { get; init; }
}

/// <summary>Where a page of <see cref="IExperienceRecordStore.QueryHoldoutOutcomesAsync"/> resumes: after this run and record.</summary>
/// <param name="RunId">The last row's <see cref="HoldoutOutcomeRow.RunId"/>.</param>
/// <param name="ExperienceId">The last row's <see cref="HoldoutOutcomeRow.ExperienceId"/>.</param>
public sealed record HoldoutOutcomeCursor(Guid RunId, Guid ExperienceId);

/// <summary>A page of holdout assignment rows in exactly one scope, with each run's outcome.</summary>
/// <param name="Scope">The exact scope whose assignments are read: the scope the blocks were built in.</param>
/// <param name="Limit">How many rows, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>.</param>
/// <param name="After">
/// Resume after this row, in the store's (run, record) order; <see langword="null"/> for the first page. Pass the last
/// row of the previous page.
/// </param>
public sealed record HoldoutOutcomeQuery(
    Scope Scope,
    int Limit = HoldoutOutcomeQuery.DefaultLimit,
    HoldoutOutcomeCursor? After = null)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 500;
}

/// <summary>The result of <see cref="IExperienceRecordStore.QueryHoldoutOutcomesAsync"/>.</summary>
/// <param name="Outcome"><see cref="ExperienceStoreOutcome.Found"/> (possibly with no rows), <see cref="ExperienceStoreOutcome.Denied"/> or <see cref="ExperienceStoreOutcome.Invalid"/>.</param>
/// <param name="Rows">The page, ordered by run and then record, when <paramref name="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <paramref name="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record HoldoutOutcomeQueryResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<HoldoutOutcomeRow> Rows,
    IReadOnlyList<StoreValidationError> Errors)
{
    /// <summary>
    /// What the port's default implementation answers: <see cref="ExperienceStoreOutcome.Found"/> with no rows and
    /// <see cref="Tracked"/> <see langword="false"/>.
    /// </summary>
    public static HoldoutOutcomeQueryResult NotTracked { get; } = new(ExperienceStoreOutcome.Found, [], []) { Tracked = false };

    /// <summary>Whether the store records holdout assignments at all. <see langword="false"/> only from the port's default implementation.</summary>
    public bool Tracked { get; init; } = true;
}
