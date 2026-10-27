namespace AgentExperience.Abstractions;

/// <summary>
/// One captured run's holdout assignment for another memory provider, as
/// <see cref="IExperienceRecordStore.RecordProviderHoldoutAssignmentAsync"/> records it: the run, the host's key for the
/// provider, its arm, and when. The provider's memory items are opaque: no item, no content, only the arm.
/// </summary>
/// <param name="RunId">The captured run. Never <see cref="Guid.Empty"/>.</param>
/// <param name="ProviderKey">
/// The host's name for the measured provider (<c>"mem0"</c>, say): 1 to <see cref="MaxProviderKeyLength"/> characters of
/// ASCII letters, digits, <c>.</c>, <c>_</c> and <c>-</c>. See <see cref="IsValidProviderKey"/>.
/// </param>
/// <param name="Arm">
/// Whether the provider's context was passed through (<see cref="HoldoutArm.Treatment"/>) or withheld
/// (<see cref="HoldoutArm.Holdout"/>).
/// </param>
/// <param name="AssignedAt">When the arm was decided, by the caller's clock. Must be set.</param>
public sealed record ProviderHoldoutAssignment(Guid RunId, string ProviderKey, HoldoutArm Arm, DateTimeOffset AssignedAt)
{
    /// <summary>The longest permitted provider key.</summary>
    public const int MaxProviderKeyLength = 100;

    /// <summary>
    /// Whether <paramref name="providerKey"/> is a valid provider key: 1 to <see cref="MaxProviderKeyLength"/> characters,
    /// each an ASCII letter or digit, <c>.</c>, <c>_</c> or <c>-</c>.
    /// </summary>
    /// <param name="providerKey">The key to check.</param>
    /// <returns><see langword="true"/> when it is valid.</returns>
    public static bool IsValidProviderKey(string? providerKey)
    {
        if (string.IsNullOrEmpty(providerKey) || providerKey.Length > MaxProviderKeyLength)
        {
            return false;
        }

        foreach (var c in providerKey)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One provider holdout row as <see cref="IExperienceRecordStore.QueryProviderHoldoutOutcomesAsync"/> reads it back: the
/// run, its arm, and the run's own verified outcome. IDs, a key, an arm, an instant and a status: never content.
/// </summary>
/// <param name="RunId">The captured run.</param>
/// <param name="ProviderKey">The measured provider's key.</param>
/// <param name="Arm">Whether the provider's context was passed through or withheld.</param>
/// <param name="AssignedAt">When the arm was decided, as stored.</param>
/// <param name="RunOutcome">
/// The <see cref="Outcome.Status"/> of the run's own finalized record (the live record in exactly the row's scope whose
/// <see cref="ExperienceRecord.SourceRunId"/> is <paramref name="RunId"/>), exactly as
/// <see cref="HoldoutOutcomeRow.RunOutcome"/>. <see langword="null"/> when there is no such record.
/// </param>
public sealed record ProviderHoldoutOutcomeRow(
    Guid RunId,
    string ProviderKey,
    HoldoutArm Arm,
    DateTimeOffset AssignedAt,
    TaskVerificationStatus? RunOutcome);

/// <summary>A page of one provider's holdout rows in exactly one scope, with each run's outcome.</summary>
/// <param name="Scope">The exact scope whose rows are read: the scope the runs were captured in.</param>
/// <param name="ProviderKey">The measured provider's key; see <see cref="ProviderHoldoutAssignment.IsValidProviderKey"/>.</param>
/// <param name="Limit">How many rows, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>.</param>
/// <param name="AfterRunId">
/// Resume after this run, in the store's run order; <see langword="null"/> for the first page. Pass the last row's run.
/// </param>
public sealed record ProviderHoldoutOutcomeQuery(
    Scope Scope,
    string ProviderKey,
    int Limit = ProviderHoldoutOutcomeQuery.DefaultLimit,
    Guid? AfterRunId = null)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 500;
}

/// <summary>The result of <see cref="IExperienceRecordStore.QueryProviderHoldoutOutcomesAsync"/>.</summary>
/// <param name="Outcome"><see cref="ExperienceStoreOutcome.Found"/> (possibly with no rows), <see cref="ExperienceStoreOutcome.Denied"/> or <see cref="ExperienceStoreOutcome.Invalid"/>.</param>
/// <param name="Rows">The page, ordered by run, when <paramref name="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <paramref name="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ProviderHoldoutOutcomeQueryResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ProviderHoldoutOutcomeRow> Rows,
    IReadOnlyList<StoreValidationError> Errors)
{
    /// <summary>
    /// What the port's default implementation answers: <see cref="ExperienceStoreOutcome.Found"/> with no rows and
    /// <see cref="Tracked"/> <see langword="false"/>.
    /// </summary>
    public static ProviderHoldoutOutcomeQueryResult NotTracked { get; } = new(ExperienceStoreOutcome.Found, [], []) { Tracked = false };

    /// <summary>Whether the store records provider holdout assignments at all. <see langword="false"/> only from the port's default implementation.</summary>
    public bool Tracked { get; init; } = true;
}
