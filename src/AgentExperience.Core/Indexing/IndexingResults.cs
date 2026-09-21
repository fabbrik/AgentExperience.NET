using AgentExperience.Abstractions;

namespace AgentExperience.Core.Indexing;

/// <summary>One scoped, explicit re-index pass.</summary>
/// <param name="Scope">The exact scope to re-index within. Never treated as authority, and never widened.</param>
/// <param name="ExperienceIds">
/// Optional. Exactly which records to consider; <see langword="null"/> considers every record in the
/// scope, bounded by <paramref name="Limit"/>. Re-indexing is never implicit and never repository-wide:
/// a caller always names a scope, and may narrow it further to specific records.
/// </param>
/// <param name="Limit">
/// The most records one pass may consider, from <see cref="ExperienceIndexScan.MinLimit"/> to
/// <see cref="ExperienceIndexScan.MaxLimit"/>. A pass is a batch job that pages; it never loads a
/// whole scope at once.
/// </param>
/// <param name="StartAfterId">
/// Optional keyset cursor. Records are always considered in ascending
/// <see cref="ExperienceRecord.ExperienceId"/> order, so passing the previous pass's
/// <see cref="ExperienceReindexResult.LastExaminedId"/> here walks a scope larger than
/// <paramref name="Limit"/> to the end; a pass that returns a <see langword="null"/>
/// <see cref="ExperienceReindexResult.LastExaminedId"/> has reached it.
/// </param>
public sealed record ReindexExperienceRequest(
    Scope Scope,
    IReadOnlyList<Guid>? ExperienceIds = null,
    int Limit = ExperienceIndexScan.DefaultLimit,
    Guid? StartAfterId = null);

/// <summary>What indexing one record ended as.</summary>
public enum ExperienceIndexingOutcome
{
    /// <summary>The record was embedded and its vector stored, replacing any previous vector for it.</summary>
    Indexed,

    /// <summary>
    /// The stored vector was already produced by this model from exactly this text, so nothing
    /// changed: no provider was called and nothing was written. This is what makes re-indexing
    /// idempotent and cheap.
    /// </summary>
    Skipped,

    /// <summary>
    /// The record's revision moved between being read and the write landing, so the write was
    /// rejected and the stored vector is unchanged. Re-running the pass picks up the new revision.
    /// </summary>
    Stale,

    /// <summary>
    /// No such record exists within the requested scope -- it was deleted, or it is in another scope.
    /// Nothing was written and no row was created.
    /// </summary>
    Missing,

    /// <summary>The request scope lies outside the host-established authorization. Nothing was read, embedded, or written.</summary>
    Denied,

    /// <summary>
    /// The record's status or reuse confidence means a vector search could never return it, so it was
    /// not embedded at all. Nothing was written, and -- the point of the check -- its task summary and
    /// reflection lesson never left the database for a third-party provider.
    /// </summary>
    Ineligible,

    /// <summary>
    /// The embedding provider failed, timed out, or returned something unusable. The record is
    /// untouched: still committed, still durable, still text-searchable -- and still indexable by a
    /// later pass.
    /// </summary>
    ProviderFailed,

    /// <summary>
    /// The index itself failed or refused the write. As with a provider failure, the record is
    /// untouched and a later pass can try again.
    /// </summary>
    IndexFailed,
}

/// <summary>
/// Why indexing could not do what it was asked. <see cref="Reason"/> is safe to log or surface: it
/// never carries record content. <see cref="Exception"/> is <em>not</em> held to that standard -- it
/// is whatever the port or the provider threw, and a driver or HTTP client message can quote SQL
/// text, parameter values, connection detail, or a request body. Treat it as local diagnostics only.
/// </summary>
/// <param name="Reason">A human-readable, content-free explanation.</param>
/// <param name="Errors">The index's validation errors when it reported the write malformed; otherwise empty.</param>
/// <param name="Exception">The original failure, when one was caught. Diagnostic only; may carry adapter detail.</param>
public sealed record ExperienceIndexingFailure(
    string Reason,
    IReadOnlyList<StoreValidationError> Errors,
    Exception? Exception);

/// <summary>
/// The result of indexing one record. It always says what happened; it is never an exception for an
/// expected condition, and never a silent success.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ExperienceId">The record this result is about.</param>
/// <param name="Descriptor">
/// The descriptor now stored for the record: the one just written
/// (<see cref="ExperienceIndexingOutcome.Indexed"/>) or the one already there
/// (<see cref="ExperienceIndexingOutcome.Skipped"/>). <see langword="null"/> whenever nothing is
/// known to be stored.
/// </param>
/// <param name="Failure">Why the pass could not index this record; otherwise <see langword="null"/>.</param>
public sealed record ExperienceIndexingResult(
    ExperienceIndexingOutcome Outcome,
    Guid ExperienceId,
    ExperienceEmbeddingDescriptor? Descriptor,
    ExperienceIndexingFailure? Failure)
{
    /// <summary>Whether a vector for this record is now stored under the current model, whether this call wrote it or found it already there.</summary>
    public bool IsIndexed => Outcome is ExperienceIndexingOutcome.Indexed or ExperienceIndexingOutcome.Skipped;

    /// <summary>
    /// Whether running the same pass again could still succeed. A provider or index failure is
    /// transient by nature, and a stale revision simply means the record moved on and should be read
    /// again. A missing record and a denied scope are not: repeating them changes nothing.
    /// </summary>
    public bool IsRetryable => Outcome is ExperienceIndexingOutcome.ProviderFailed
        or ExperienceIndexingOutcome.IndexFailed
        or ExperienceIndexingOutcome.Stale;
}

/// <summary>What a whole re-index pass ended as.</summary>
public enum ExperienceReindexOutcome
{
    /// <summary>The pass ran to the end. Individual records may still have been skipped, rejected, or failed; see the per-record results.</summary>
    Completed,

    /// <summary>The request scope lies outside the host-established authorization. Nothing was read, embedded, or written.</summary>
    Denied,

    /// <summary>The request was malformed, so the index refused to list anything. Nothing was read, embedded, or written.</summary>
    Invalid,

    /// <summary>The pass could not list what to consider at all, so no record was examined.</summary>
    Failed,
}

/// <summary>
/// The result of one scoped re-index pass: the tally, plus a result for every record it considered.
/// </summary>
/// <param name="Outcome">What the pass as a whole ended as.</param>
/// <param name="Examined">How many records the pass considered. Bounded by the request's limit; reaching it means there may be more.</param>
/// <param name="Indexed">How many were embedded and written.</param>
/// <param name="Skipped">How many already had this model's vector for exactly this text, so no provider was called for them.</param>
/// <param name="Rejected">How many were rejected without being written -- stale or missing. Not failures: the index declined to overwrite state it does not own.</param>
/// <param name="Failed">How many failed against the provider or the index. Each is retryable.</param>
/// <param name="Records">One result per considered record, in ascending <see cref="ExperienceRecord.ExperienceId"/> order.</param>
/// <param name="Failure">Why the pass itself could not run, when <see cref="Outcome"/> is <see cref="ExperienceReindexOutcome.Failed"/> or <see cref="ExperienceReindexOutcome.Invalid"/>; otherwise <see langword="null"/>.</param>
/// <param name="LastExaminedId">
/// The last record this pass considered, to pass as the next pass's
/// <see cref="ReindexExperienceRequest.StartAfterId"/>. <see langword="null"/> means the pass
/// considered nothing, which is how a caller knows the scope is exhausted rather than looping over the
/// same first page forever.
/// </param>
public sealed record ExperienceReindexResult(
    ExperienceReindexOutcome Outcome,
    int Examined,
    int Indexed,
    int Skipped,
    int Rejected,
    int Failed,
    IReadOnlyList<ExperienceIndexingResult> Records,
    ExperienceIndexingFailure? Failure,
    Guid? LastExaminedId = null);
