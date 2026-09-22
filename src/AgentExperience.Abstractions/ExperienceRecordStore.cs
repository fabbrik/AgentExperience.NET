namespace AgentExperience.Abstractions;

/// <summary>
/// Port for durable, scoped persistence of canonical <see cref="ExperienceRecord"/>s. Every
/// operation takes a host-established <see cref="AuthorizationContext"/>; a request scope outside
/// it is <see cref="ExperienceStoreOutcome.Denied"/> before any storage access, and scope matching
/// is exact (ordinal, case-sensitive, <see langword="null"/> matches only <see langword="null"/>).
/// The single, explicit exception is <see cref="GetAsync"/>, which also returns a record an active
/// <see cref="ExperienceGrant"/> permits this scope to read; every other operation here, writes and
/// the lifecycle audit trail included, stays exact-scope whatever grants exist.
/// Expected conditions return typed results; infrastructure failures throw
/// <see cref="ExperienceStoreException"/>; caller cancellation surfaces as an unwrapped
/// <see cref="OperationCanceledException"/>.
/// </summary>
public interface IExperienceRecordStore
{
    /// <summary>
    /// Persists a new record (create-only). An existing <see cref="ExperienceRecord.ExperienceId"/>
    /// in any scope yields <see cref="ExperienceStoreOutcome.Conflict"/> and leaves the stored record
    /// unchanged; the result never reveals whether the existing record is in the caller's scope.
    /// A retried create whose earlier acknowledgement was lost (for example cancelled or timed out
    /// after the commit) also returns <see cref="ExperienceStoreOutcome.Conflict"/>; follow a
    /// <see cref="ExperienceStoreOutcome.Conflict"/> with <see cref="GetAsync"/> in your own scope to
    /// check whether the stored record is yours.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="record">The record to persist; its <see cref="ExperienceRecord.Scope"/> must lie within <paramref name="authorization"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Created"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, <see cref="ExperienceStoreOutcome.Denied"/>, or <see cref="ExperienceStoreOutcome.Conflict"/>.</returns>
    Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one record by ID within exactly <paramref name="scope"/>, or one that an active
    /// <see cref="ExperienceGrant"/> names and permits <paramref name="scope"/> to read. A record that
    /// is neither is indistinguishable from a missing one
    /// (<see cref="ExperienceStoreOutcome.NotFound"/>), and so is one whose grant has expired or been
    /// revoked.
    /// </summary>
    /// <remarks>
    /// A record read through a grant comes back exactly as its owner sees it, carrying its owner's
    /// <see cref="ExperienceRecord.Scope"/> -- reading it does not move it, and the reader gains no
    /// authority over it. Whether a grant applies is decided inside the implementation's own query,
    /// never by the caller and never in application code.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact request scope to read within.</param>
    /// <param name="experienceId">The record to read. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/>, <see cref="ExperienceStoreOutcome.NotFound"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists records within exactly <see cref="ExperienceRecordQuery.Scope"/>, optionally filtered by
    /// status, bounded by <see cref="ExperienceRecordQuery.Limit"/>, newest first
    /// (<see cref="ExperienceRecord.CreatedAt"/> descending, then <see cref="ExperienceRecord.ExperienceId"/> in a stable, store-defined order).
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="query">The scoped query.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/> (possibly with no records), <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="lifecycleEvent"/> and updates the record's projection
    /// (<see cref="ExperienceRecord.Status"/>, <see cref="ExperienceRecord.Revision"/>,
    /// <see cref="ExperienceRecord.UpdatedAt"/>) in one transaction: both writes commit together or
    /// neither does. The store persists the decision exactly as given and never derives a status, a
    /// score, or a counter of its own -- deciding which transition is legal belongs to Core.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotency.</b> <see cref="LifecycleEvent.EventId"/> is the idempotency key. Replaying an
    /// event whose stored fields (including its scope) are identical returns the original outcome --
    /// <see cref="ExperienceStoreOutcome.Committed"/> with the revision that commit produced -- and
    /// writes nothing. A stored <see cref="LifecycleEvent.EventId"/> with any differing field is
    /// <see cref="ExperienceStoreOutcome.Conflict"/>, whichever scope owns it, and writes nothing.
    /// </para>
    /// <para>
    /// <b>Concurrency.</b> <see cref="LifecycleEvent.ExpectedRevision"/> must equal the record's
    /// current <see cref="ExperienceRecord.Revision"/>. A successful commit sets the revision to
    /// <see cref="LifecycleEvent.ExpectedRevision"/> + 1. Any other value is
    /// <see cref="ExperienceStoreOutcome.StaleRevision"/> and writes nothing, so two commits racing
    /// from the same revision never both apply.
    /// </para>
    /// <para>
    /// <b>Prior-status guard.</b> When <see cref="LifecycleEvent.PriorStatus"/> is non-null it must
    /// also equal the record's stored <see cref="ExperienceRecord.Status"/>, matched in the same
    /// statement as the revision. That is what keeps Core's transition table enforced against real
    /// state rather than against what the caller asserted, and keeps a stored event from recording a
    /// prior status the record never had. A mismatch is
    /// <see cref="ExperienceStoreOutcome.StatusMismatch"/>, carries the stored status, and writes
    /// nothing. A <see langword="null"/> <see cref="LifecycleEvent.PriorStatus"/> (a record's first
    /// event) skips the status match.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact request scope the record must lie in. Never treated as authority.</param>
    /// <param name="lifecycleEvent">The transition Core decided, already stamped.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Committed"/>, <see cref="ExperienceStoreOutcome.StaleRevision"/>,
    /// <see cref="ExperienceStoreOutcome.StatusMismatch"/>,
    /// <see cref="ExperienceStoreOutcome.NotFound"/> (missing, or in another scope),
    /// <see cref="ExperienceStoreOutcome.Conflict"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, or
    /// <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one record's lifecycle history within exactly <paramref name="scope"/>: its current
    /// <see cref="ExperienceRecord.Revision"/> plus every appended event, oldest first. A record that
    /// exists in a different scope is indistinguishable from a missing one
    /// (<see cref="ExperienceStoreOutcome.NotFound"/>). Events are never deleted or rewritten.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact request scope to read within.</param>
    /// <param name="experienceId">The record whose history to read. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/> (possibly with no events), <see cref="ExperienceStoreOutcome.NotFound"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceRecordHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A scoped query over <see cref="ExperienceRecord"/>s.
/// </summary>
/// <param name="Scope">The exact scope to query within. Never treated as authority.</param>
/// <param name="Statuses">Optional status filter; <see langword="null"/> returns every status. When supplied it must be non-empty and contain only defined values.</param>
/// <param name="Limit">Maximum number of records to return, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>. Defaults to <see cref="DefaultLimit"/>.</param>
public sealed record ExperienceRecordQuery(
    Scope Scope,
    IReadOnlyList<ExperienceStatus>? Statuses = null,
    int Limit = ExperienceRecordQuery.DefaultLimit)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 500;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 50;
}

/// <summary>
/// The disposition an <see cref="IExperienceRecordStore"/> operation reached.
/// </summary>
public enum ExperienceStoreOutcome
{
    /// <summary>The record was persisted.</summary>
    Created,

    /// <summary>The requested record(s) were read. A query with no matches is still <see cref="Found"/>.</summary>
    Found,

    /// <summary>No record with that ID exists within the requested scope (including when it exists in another scope).</summary>
    NotFound,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See the result's validation errors. No storage was accessed.</summary>
    Invalid,

    /// <summary>
    /// A record with the same ID already exists in some scope, or a lifecycle event with the same
    /// <see cref="LifecycleEvent.EventId"/> is already stored with differing fields. Nothing was
    /// written and the stored state is unchanged and not revealed.
    /// </summary>
    Conflict,

    /// <summary>
    /// A lifecycle event and its projection update were committed together. The record's
    /// <see cref="ExperienceRecord.Revision"/> is now <see cref="LifecycleEvent.ExpectedRevision"/> + 1.
    /// An identical replay reports this same outcome without writing again.
    /// </summary>
    Committed,

    /// <summary>
    /// The lifecycle event's <see cref="LifecycleEvent.ExpectedRevision"/> did not equal the record's
    /// current <see cref="ExperienceRecord.Revision"/>, so newer state was not overwritten. Nothing
    /// was written.
    /// </summary>
    StaleRevision,

    /// <summary>
    /// The lifecycle event's <see cref="LifecycleEvent.PriorStatus"/> did not equal the record's stored
    /// <see cref="ExperienceRecord.Status"/>, so the transition was decided against state the record was
    /// not in. Nothing was written, and the result carries the stored status to re-decide against.
    /// </summary>
    StatusMismatch,
}

/// <summary>
/// One validation failure on a store request.
/// </summary>
/// <param name="Path">The field path that failed validation, e.g. <c>"Scope.TenantId"</c> or <c>"Attempts[0].ToolCalls[1].ToolName"</c>.</param>
/// <param name="Message">A content-free, human-readable explanation. Never echoes the offending value.</param>
public sealed record StoreValidationError(string Path, string Message);

/// <summary>
/// The result of <see cref="IExperienceRecordStore.CreateAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRecordCreateResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// The result of <see cref="IExperienceRecordStore.GetAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Record">The record when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise <see langword="null"/>.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="SharedByGrant">
/// <see langword="true"/> when <paramref name="Record"/> belongs to another scope and was readable
/// only because an active <see cref="ExperienceGrant"/> permits the requested scope to read it. Only
/// the implementation that applied the scope predicate knows this, so only it may set it; a consumer
/// must treat an unset flag as "this record is the requester's own" rather than comparing scopes to
/// decide.
/// </param>
public sealed record ExperienceRecordGetResult(
    ExperienceStoreOutcome Outcome,
    ExperienceRecord? Record,
    IReadOnlyList<StoreValidationError> Errors,
    bool SharedByGrant = false);

/// <summary>
/// The result of <see cref="IExperienceRecordStore.QueryAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Records">The matching records when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRecordQueryResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceRecord> Records,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// The result of <see cref="IExperienceRecordStore.CommitLifecycleEventAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Revision">
/// The record's <see cref="ExperienceRecord.Revision"/> after a
/// <see cref="ExperienceStoreOutcome.Committed"/> commit (or after the original commit, when this call
/// was an identical replay); the record's current revision on
/// <see cref="ExperienceStoreOutcome.StaleRevision"/>, so the caller can retry against it; otherwise 0.
/// </param>
/// <param name="CurrentStatus">
/// The record's stored <see cref="ExperienceRecord.Status"/> when <see cref="Outcome"/> is
/// <see cref="ExperienceStoreOutcome.StatusMismatch"/>, so the caller can re-decide the transition
/// against the state the record is actually in; otherwise <see langword="null"/>.
/// </param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceLifecycleCommitResult(
    ExperienceStoreOutcome Outcome,
    long Revision,
    ExperienceStatus? CurrentStatus,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// The result of <see cref="IExperienceRecordStore.GetHistoryAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Revision">The record's current <see cref="ExperienceRecord.Revision"/> when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise 0.</param>
/// <param name="Events">The record's lifecycle events, oldest first, when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRecordHistoryResult(
    ExperienceStoreOutcome Outcome,
    long Revision,
    IReadOnlyList<LifecycleEvent> Events,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// Thrown by an <see cref="IExperienceRecordStore"/> implementation when storage infrastructure
/// fails (database unavailable, driver error, timeout) or a stored record cannot be read (for
/// example an unsupported payload version). The original failure, when any, is the
/// <see cref="Exception.InnerException"/>. Never carries record payload content.
/// </summary>
public class ExperienceStoreException : Exception
{
    /// <summary>Creates an exception with a content-free message.</summary>
    /// <param name="message">A content-free description of the failure.</param>
    public ExperienceStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping the original infrastructure failure.</summary>
    /// <param name="message">A content-free description of the failure.</param>
    /// <param name="innerException">The original failure.</param>
    public ExperienceStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
