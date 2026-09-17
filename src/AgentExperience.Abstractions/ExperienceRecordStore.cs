namespace AgentExperience.Abstractions;

/// <summary>
/// Port for durable, scoped persistence of canonical <see cref="ExperienceRecord"/>s. Every
/// operation takes a host-established <see cref="AuthorizationContext"/>; a request scope outside
/// it is <see cref="ExperienceStoreOutcome.Denied"/> before any storage access, and scope matching
/// is exact (ordinal, case-sensitive, <see langword="null"/> matches only <see langword="null"/>).
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
    /// Reads one record by ID within exactly <paramref name="scope"/>. A record that exists in a
    /// different scope is indistinguishable from a missing one (<see cref="ExperienceStoreOutcome.NotFound"/>).
    /// </summary>
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

    /// <summary>A record with the same ID already exists in some scope. The stored record is unchanged and not revealed.</summary>
    Conflict,
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
public sealed record ExperienceRecordGetResult(
    ExperienceStoreOutcome Outcome,
    ExperienceRecord? Record,
    IReadOnlyList<StoreValidationError> Errors);

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
