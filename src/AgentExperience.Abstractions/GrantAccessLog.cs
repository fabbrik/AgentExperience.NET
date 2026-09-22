namespace AgentExperience.Abstractions;

/// <summary>
/// One appended entry recording that a record was <em>delivered</em> to a caller because an
/// <see cref="ExperienceGrant"/> permitted it. It is the answer to "who read our team's experience,
/// and when", which a grant's administration trail deliberately cannot give.
/// </summary>
/// <remarks>
/// <para>
/// <b>A delivery is any read that hands the caller a record it does not own.</b> That is
/// <see cref="IExperienceRecordStore.GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> -- which the pre-injection re-read also goes through
/// -- <em>and</em> both search channels, because an <see cref="ExperienceCandidate"/> carries the
/// record read back in full: a host consuming <see cref="IExperienceCandidateSource"/> or
/// <see cref="IExperienceEmbeddingIndex"/> directly receives complete foreign records, so a search
/// result is a disclosure and not a match notice. A search's rows are recorded together, in one
/// statement, so auditing costs one round trip per search rather than one per row.
/// </para>
/// <para>
/// Two things are <em>not</em> deliveries. A record the caller owns: no grant permitted it, so there
/// is nothing to attribute to one. And a read the caller refuses after fetching it precisely
/// <em>because</em> a grant is what made it readable -- see
/// <see cref="ExperienceReadPurpose.ScopeCheck"/> -- because nothing was handed over.
/// </para>
/// <para>
/// Rows are append-only, like every other ledger here. Nothing updates or deletes one, and the
/// PostgreSQL adapter's table refuses both in the database. That is also why
/// <see cref="RecordRevision"/> and <see cref="CorrelationId"/> are on the row rather than joined in
/// later: a record is a mutable projection, so only the row itself can say which version was
/// disclosed, and neither value can ever be backfilled.
/// </para>
/// </remarks>
/// <param name="AccessId">The row's identity, minted by the reader for this one delivery.</param>
/// <param name="GrantId">
/// The grant that permitted the read -- the one the database actually used to decide it, not merely
/// some grant that could have. Only the implementation that applied the scope predicate knows which,
/// so only it may name it.
/// </param>
/// <param name="ExperienceId">The record that was delivered.</param>
/// <param name="RecordRevision">
/// The record's <see cref="ExperienceRecord.Revision"/> as it was delivered. Records are mutable
/// projections that every lifecycle commit moves, so without this the trail could say a record was
/// disclosed but never which version of it.
/// </param>
/// <param name="RecordScope">The scope that owns the record, as the read found it.</param>
/// <param name="RecipientScope">The scope the read was made in: the scope the grant permits.</param>
/// <param name="PrincipalId">
/// The reading principal, taken from the host-established
/// <see cref="AuthorizationContext.PrincipalId"/> and never from anything the caller passed as data.
/// It is required: a row that cannot say who read the record does not answer the question this ledger
/// exists for, so a blank principal is an audit failure rather than a row with a null in it.
/// </param>
/// <param name="CorrelationId">
/// The host's correlation identifier for the work that caused the read, when it supplied one -- Core's
/// retrieval request and the MAF provider both carry one -- so a delivery can be tied to the
/// invocation behind it. <see langword="null"/> when the caller named none.
/// </param>
/// <param name="OccurredAt">When the read happened, from the reader's clock.</param>
public sealed record ExperienceGrantAccess(
    Guid AccessId,
    Guid GrantId,
    Guid ExperienceId,
    long RecordRevision,
    Scope RecordScope,
    Scope RecipientScope,
    string PrincipalId,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

/// <summary>
/// How hard a failed access row is: whether a read whose audit could not be written still returns.
/// </summary>
public enum ExperienceGrantAuditingMode
{
    /// <summary>
    /// The read still returns and the failure is reported to the host through
    /// <see cref="ExperienceGrantAuditing.OnNotRecorded"/>. Availability of the read wins; the gap in
    /// the trail is reported rather than hidden.
    /// </summary>
    BestEffort,

    /// <summary>
    /// A read whose access rows cannot be written returns nothing -- exactly as if no grant permitted
    /// it -- and the failure is still reported. The trail wins: a record shared across scopes is not
    /// delivered unless the delivery is recorded.
    /// </summary>
    Required,
}

/// <summary>
/// Access rows that could not be written, reported to the host as it happens. A search reports its
/// whole batch in one failure, because the rows are written in one statement and fail together.
/// </summary>
/// <param name="Accesses">The rows the reader tried to append. Never empty.</param>
/// <param name="Mode">
/// The mode in force, which says what the read did about it: under
/// <see cref="ExperienceGrantAuditingMode.BestEffort"/> the records were still returned, and under
/// <see cref="ExperienceGrantAuditingMode.Required"/> they were not.
/// </param>
/// <param name="Failure">
/// Why the write failed. A caller's own cancellation arriving during the append shows up here too, as
/// an <see cref="OperationCanceledException"/>: the read itself had already finished, so the mode
/// decides what happens to it rather than the cancellation silently discarding a result the host asked
/// to keep.
/// </param>
public sealed record ExperienceGrantAccessFailure(
    IReadOnlyList<ExperienceGrantAccess> Accesses,
    ExperienceGrantAuditingMode Mode,
    Exception Failure);

/// <summary>
/// The host's auditing policy: where access rows go, how a failed one is reported, and whether a read
/// may proceed without one.
/// </summary>
/// <remarks>
/// <para>
/// Auditing is off unless a host constructs this and hands it to a reader. A deployment that does not
/// behaves exactly as it did before: no extra write, no extra table, no extra failure mode.
/// </para>
/// <para>
/// <see cref="OnNotRecorded"/> is required rather than optional on purpose. Under
/// <see cref="ExperienceGrantAuditingMode.BestEffort"/> the read returns anyway, so the callback is
/// the <em>only</em> place a missing row is visible: making it optional would be a way to turn
/// auditing into something that fails silently, which is precisely what an access log exists to rule
/// out.
/// </para>
/// <para>
/// <b>Reader obligation.</b> A host wires this once and expects every grant-widened read to honour it.
/// An <see cref="IExperienceRecordStore"/>, <see cref="IExperienceCandidateSource"/>, or
/// <see cref="IExperienceEmbeddingIndex"/> implementation that ignores a configured
/// <see cref="ExperienceGrantAuditing"/> is not conformant -- and because container registrations use
/// <c>TryAdd</c>, a host that registers its own implementation <em>before</em> the adapter's silently
/// takes that obligation on. Registering auditing does not make somebody else's store audit.
/// </para>
/// </remarks>
/// <param name="Log">The ledger access rows are appended to.</param>
/// <param name="OnNotRecorded">
/// Called when access rows could not be written, before the read returns. It must not throw; a reader
/// that catches a throwing callback still honours <paramref name="Mode"/>.
/// </param>
/// <param name="Mode">What a failed write does to the read. Defaults to <see cref="ExperienceGrantAuditingMode.BestEffort"/>.</param>
/// <param name="Clock">
/// The clock a row's <see cref="ExperienceGrantAccess.OccurredAt"/> is read from.
/// <see langword="null"/> means <see cref="TimeProvider.System"/>. It is here rather than on each
/// reader's constructor so one policy object freezes time for every audited channel at once.
/// </param>
public sealed record ExperienceGrantAuditing(
    IExperienceGrantAccessLog Log,
    Action<ExperienceGrantAccessFailure> OnNotRecorded,
    ExperienceGrantAuditingMode Mode = ExperienceGrantAuditingMode.BestEffort,
    TimeProvider? Clock = null)
{
    /// <summary>The ledger access rows are appended to (see the primary constructor's parameter doc).</summary>
    public IExperienceGrantAccessLog Log
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Log));
    } = Log ?? throw new ArgumentNullException(nameof(Log));

    /// <summary>How a failed write is reported (see the primary constructor's parameter doc).</summary>
    public Action<ExperienceGrantAccessFailure> OnNotRecorded
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(OnNotRecorded));
    } = OnNotRecorded ?? throw new ArgumentNullException(nameof(OnNotRecorded));

    /// <summary>
    /// What a failed write does to the read (see the primary constructor's parameter doc). Validated on
    /// <c>with</c> as well as at construction: a host that asked for
    /// <see cref="ExperienceGrantAuditingMode.Required"/> must not be able to end up somewhere else
    /// because an undefined value was copied over it.
    /// </summary>
    public ExperienceGrantAuditingMode Mode
    {
        get;
        init => field = EnsureMode(value);
    } = EnsureMode(Mode);

    /// <summary>The clock rows are timestamped from; never <see langword="null"/> once constructed.</summary>
    public TimeProvider Clock
    {
        get;
        init => field = value ?? TimeProvider.System;
    } = Clock ?? TimeProvider.System;

    private static ExperienceGrantAuditingMode EnsureMode(ExperienceGrantAuditingMode value) =>
        Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Mode), value, "Unknown grant auditing mode.");
}

/// <summary>
/// A keyset cursor into a record's access trail: the last row a page returned. Rows come back oldest
/// first, ordered by <see cref="ExperienceGrantAccess.OccurredAt"/> and then
/// <see cref="ExperienceGrantAccess.AccessId"/>, so passing the previous page's cursor continues
/// exactly where it stopped even when several deliveries share an instant.
/// </summary>
/// <param name="OccurredAt">The last returned row's <see cref="ExperienceGrantAccess.OccurredAt"/>.</param>
/// <param name="AccessId">The last returned row's <see cref="ExperienceGrantAccess.AccessId"/>.</param>
public sealed record ExperienceGrantAccessCursor(DateTimeOffset OccurredAt, Guid AccessId);

/// <summary>
/// One bounded page of the deliveries made out of one owner scope.
/// </summary>
/// <remarks>
/// Reading the trail is an <em>owner-scope</em> operation, exactly as listing a record's grants and
/// reading its lifecycle history are: a grant confers reading one record and never the right to see
/// who else has read it.
/// </remarks>
/// <param name="RecordScope">The exact owner scope to read within. Never treated as authority.</param>
/// <param name="ExperienceId">
/// One record to narrow to, or <see langword="null"/> for every delivery out of
/// <paramref name="RecordScope"/> -- which is the "who saw our team's experience, and when" question.
/// </param>
/// <param name="Limit">Maximum number of rows to return, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>. Defaults to <see cref="DefaultLimit"/>.</param>
/// <param name="StartAfter">Optional keyset cursor: return only rows after this one.</param>
public sealed record ExperienceGrantAccessQuery(
    Scope RecordScope,
    Guid? ExperienceId = null,
    int Limit = ExperienceGrantAccessQuery.DefaultLimit,
    ExperienceGrantAccessCursor? StartAfter = null)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 500;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 100;
}

/// <summary>
/// The result of <see cref="IExperienceGrantAccessLog.QueryAsync"/>.
/// </summary>
/// <param name="Outcome">
/// <see cref="ExperienceStoreOutcome.Found"/> -- a scope with no deliveries is still
/// <see cref="ExperienceStoreOutcome.Found"/> with no rows -- <see cref="ExperienceStoreOutcome.Invalid"/>,
/// or <see cref="ExperienceStoreOutcome.Denied"/>.
/// </param>
/// <param name="Accesses">The deliveries, oldest first, when <paramref name="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <paramref name="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="NextCursor">
/// The cursor to pass as <see cref="ExperienceGrantAccessQuery.StartAfter"/> for the next page, or
/// <see langword="null"/> when this page returned nothing.
/// </param>
public sealed record ExperienceGrantAccessQueryResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceGrantAccess> Accesses,
    IReadOnlyList<StoreValidationError> Errors,
    ExperienceGrantAccessCursor? NextCursor = null);

/// <summary>
/// Port for the append-only log of reads that a sharing grant delivered.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate port from <see cref="IExperienceGrantStore"/>, and its rows live in their own
/// table, so a deployment can keep grants without paying for access rows. A host that never wires it
/// keeps exactly the behaviour it had before: grants are administered and enforced as usual, and
/// nothing records who read what.
/// </para>
/// <para>
/// The rows are never written inside the read's own statement. Doing so would take a write lock on
/// every read and stop reads running on a replica; the append is a separate statement, after the
/// records have been read.
/// </para>
/// <para>
/// An implementation reports failure by throwing. The caller decides what that means through
/// <see cref="ExperienceGrantAuditing.Mode"/>, so an implementation must not swallow a failed write
/// and must not invent a row it did not append.
/// </para>
/// </remarks>
public interface IExperienceGrantAccessLog
{
    /// <summary>
    /// Appends every access row a single read produced, together. A search delivers many records at
    /// once, and writing them one statement at a time would make auditing cost a round trip per row;
    /// an implementation writes the batch in one statement, so the rows land together or not at all.
    /// </summary>
    /// <param name="accesses">What was delivered, to whom, under which grant, and when. Never empty.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ExperienceStoreException">The rows could not be appended.</exception>
    Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one bounded, cursored page of the deliveries made out of an owner scope, oldest first.
    /// This is the question the ledger exists to answer -- "who saw our team's experience, and when" --
    /// and it mirrors <see cref="IExperienceGrantStore.GetHistoryAsync"/>: owner scope only, exact
    /// match, never widened by a grant.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do. Applied to <see cref="ExperienceGrantAccessQuery.RecordScope"/>.</param>
    /// <param name="query">Which scope, optionally which record, how many, and where to continue from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceGrantAccessQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceGrantAccessQuery query,
        CancellationToken cancellationToken);
}
