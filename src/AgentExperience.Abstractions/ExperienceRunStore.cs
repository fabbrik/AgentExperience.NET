namespace AgentExperience.Abstractions;

/// <summary>
/// Optional port for keeping completed <see cref="ExperienceRun"/>s durably, so a run captured in one process can be
/// finalized in a later one -- when its verdict arrives (a CI result, a merged pull request) after the process that ran it
/// has ended. The capture service writes each run it completes here; finalization reads a run from here when the capture
/// service no longer holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it holds.</b> The run exactly as capture holds it once completed: already sanitized (capture sanitizes every
/// attempt before it keeps it), with its execution status, its end, and the records it was exposed to. Nothing is
/// sanitized again here, and nothing unsanitized ever reaches it. It is a staging area, not the learning: a run becomes an
/// <see cref="ExperienceRecord"/> only through finalization, and a stored run is meant to be deleted once it is finalized
/// or has waited past a retention the host chooses.
/// </para>
/// <para>
/// <b>The same trust boundary as every port.</b> Every operation takes a host-established
/// <see cref="AuthorizationContext"/>; a scope outside it is <see cref="ExperienceRunStoreOutcome.Denied"/> before any
/// storage access, and a run in a scope the caller may not read is indistinguishable from a missing one. Run IDs are
/// unique across every scope, as record IDs are. Expected conditions return typed results; infrastructure failures throw
/// <see cref="ExperienceStoreException"/>; caller cancellation surfaces as an unwrapped
/// <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface IExperienceRunStore
{
    /// <summary>
    /// Saves a completed run, keyed by its <see cref="ExperienceRun.RunId"/>. Saving the same run again under the same
    /// <paramref name="completionEventId"/> is <see cref="ExperienceRunStoreOutcome.AlreadySaved"/> and writes nothing; a
    /// stored run with that ID under another completion event, or in any other scope, is
    /// <see cref="ExperienceRunStoreOutcome.Conflict"/> and is left as it is, revealing nothing about it.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do. The run's <see cref="ExperienceRun.Scope"/> must lie within it.</param>
    /// <param name="run">The completed run: its <see cref="ExperienceRun.ExecutionStatus"/> and <see cref="ExperienceRun.EndedAt"/> set.</param>
    /// <param name="completionEventId">The event that completed it (the capture service's completion event ID). Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceRunStoreOutcome.Saved"/>, <see cref="ExperienceRunStoreOutcome.AlreadySaved"/>,
    /// <see cref="ExperienceRunStoreOutcome.Conflict"/>, <see cref="ExperienceRunStoreOutcome.Invalid"/> or
    /// <see cref="ExperienceRunStoreOutcome.Denied"/>.
    /// </returns>
    Task<ExperienceRunStoreResult> SaveAsync(
        AuthorizationContext authorization,
        ExperienceRun run,
        Guid completionEventId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a stored run by ID, when its scope lies within <paramref name="authorization"/>; a run outside it is
    /// <see cref="ExperienceRunStoreOutcome.NotFound"/>, exactly as one that does not exist. Finalization in a later process
    /// knows the run ID and the authorization, not necessarily the scope, so the scope is the stored run's own and is only
    /// ever checked, never taken from the caller.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="runId">The run. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceRunStoreOutcome.Found"/>, <see cref="ExperienceRunStoreOutcome.NotFound"/> or <see cref="ExperienceRunStoreOutcome.Invalid"/>.</returns>
    Task<ExperienceRunGetResult> GetAsync(
        AuthorizationContext authorization,
        Guid runId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the stored runs in exactly <see cref="ExperienceRunQuery.Scope"/> not yet marked finalized, that ended before
    /// <see cref="ExperienceRunQuery.EndedBefore"/> when it is set, a page at a time in run ID order: the runs still
    /// waiting for a verdict, and the ones a retention should delete.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="query">The exact scope, the age bound, the page size and where to resume.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceRunStoreOutcome.Found"/> (fewer runs than the limit means the last page), <see cref="ExperienceRunStoreOutcome.Invalid"/> or <see cref="ExperienceRunStoreOutcome.Denied"/>.</returns>
    Task<ExperienceRunListResult> ListUnfinalizedAsync(
        AuthorizationContext authorization,
        ExperienceRunQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a stored run in exactly <paramref name="scope"/> finalized at <paramref name="finalizedAt"/>, so it no longer
    /// lists as waiting. Marking it again keeps the first instant. Finalization calls it after a run it read from here
    /// became (or already was) a durable record.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The run's exact scope.</param>
    /// <param name="runId">The run. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="finalizedAt">When it was finalized. Must be set.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceRunStoreOutcome.Updated"/>, <see cref="ExperienceRunStoreOutcome.NotFound"/>, <see cref="ExperienceRunStoreOutcome.Invalid"/> or <see cref="ExperienceRunStoreOutcome.Denied"/>.</returns>
    Task<ExperienceRunStoreResult> MarkFinalizedAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        DateTimeOffset finalizedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a stored run in exactly <paramref name="scope"/>: after it is finalized, or when it has waited too long.
    /// Deleting a run that is not there is <see cref="ExperienceRunStoreOutcome.NotFound"/> and changes nothing.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The run's exact scope.</param>
    /// <param name="runId">The run. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceRunStoreOutcome.Deleted"/>, <see cref="ExperienceRunStoreOutcome.NotFound"/>, <see cref="ExperienceRunStoreOutcome.Invalid"/> or <see cref="ExperienceRunStoreOutcome.Denied"/>.</returns>
    Task<ExperienceRunStoreResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid runId,
        CancellationToken cancellationToken);
}

/// <summary>What an <see cref="IExperienceRunStore"/> operation reached.</summary>
public enum ExperienceRunStoreOutcome
{
    /// <summary>The run was saved.</summary>
    Saved,

    /// <summary>The same run was already saved under the same completion event; nothing was written.</summary>
    AlreadySaved,

    /// <summary>A run with that ID is stored under another completion event or in another scope; nothing was written and nothing about it is revealed.</summary>
    Conflict,

    /// <summary>The requested run or page was read. A page with no runs is still <see cref="Found"/>.</summary>
    Found,

    /// <summary>No run with that ID lies in the requested scope (including one in another scope).</summary>
    NotFound,

    /// <summary>The run was marked finalized, or already was.</summary>
    Updated,

    /// <summary>The run was deleted.</summary>
    Deleted,

    /// <summary>The scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See the result's validation errors. No storage was accessed.</summary>
    Invalid,
}

/// <summary>One stored run, with what only the store knows about it.</summary>
/// <param name="Run">The completed run, as it was saved.</param>
/// <param name="CompletionEventId">The event that completed it.</param>
/// <param name="SavedAt">When the store saved it, on the store's clock.</param>
/// <param name="FinalizedAt">When it was marked finalized, or <see langword="null"/> while it waits.</param>
public sealed record StoredExperienceRun(
    ExperienceRun Run,
    Guid CompletionEventId,
    DateTimeOffset SavedAt,
    DateTimeOffset? FinalizedAt);

/// <summary>A page of a scope's unfinalized runs.</summary>
/// <param name="Scope">The exact scope. Never treated as authority.</param>
/// <param name="EndedBefore">Only runs that ended before this instant; <see langword="null"/> for every one.</param>
/// <param name="Limit">The page size, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>.</param>
/// <param name="AfterRunId">Resume after this run ID (the previous page's last); <see langword="null"/> from the start.</param>
public sealed record ExperienceRunQuery(
    Scope Scope,
    DateTimeOffset? EndedBefore = null,
    int Limit = ExperienceRunQuery.DefaultLimit,
    Guid? AfterRunId = null)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 500;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 50;
}

/// <summary>The result of a run store write.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Errors">Every validation error when <paramref name="Outcome"/> is <see cref="ExperienceRunStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRunStoreResult(
    ExperienceRunStoreOutcome Outcome,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>The result of <see cref="IExperienceRunStore.GetAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Run">The stored run when <paramref name="Outcome"/> is <see cref="ExperienceRunStoreOutcome.Found"/>; otherwise <see langword="null"/>.</param>
/// <param name="Errors">Every validation error when <paramref name="Outcome"/> is <see cref="ExperienceRunStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRunGetResult(
    ExperienceRunStoreOutcome Outcome,
    StoredExperienceRun? Run,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>The result of <see cref="IExperienceRunStore.ListUnfinalizedAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Runs">The page, in run ID order, when <paramref name="Outcome"/> is <see cref="ExperienceRunStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <paramref name="Outcome"/> is <see cref="ExperienceRunStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRunListResult(
    ExperienceRunStoreOutcome Outcome,
    IReadOnlyList<StoredExperienceRun> Runs,
    IReadOnlyList<StoreValidationError> Errors);
