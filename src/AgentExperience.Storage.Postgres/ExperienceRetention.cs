using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The result of <see cref="PostgresExperienceRecordStore.DeleteAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// These results live in this package rather than in <c>AgentExperience.Abstractions</c> on purpose:
/// erasure is a capability of this adapter, not of the <see cref="IExperienceRecordStore"/> port. Core
/// never deletes, and a port method would oblige every implementation -- including the in-memory
/// doubles hosts write for their tests -- to promise an erasure it cannot perform.
/// </remarks>
/// <param name="Outcome">
/// <see cref="ExperienceStoreOutcome.Deleted"/> when the record was erased, or was already a tombstone;
/// <see cref="ExperienceStoreOutcome.StaleRevision"/> when an expected revision was given and the record
/// has moved past it; <see cref="ExperienceStoreOutcome.NotFound"/> when no record with that ID is in the
/// requested scope -- including when it is in another one; <see cref="ExperienceStoreOutcome.Denied"/> or
/// <see cref="ExperienceStoreOutcome.Invalid"/> before any storage was touched.
/// </param>
/// <param name="Revision">
/// The tombstone's <see cref="ExperienceRecord.Revision"/> on
/// <see cref="ExperienceStoreOutcome.Deleted"/> -- erasure advances it once, exactly as a lifecycle
/// commit does -- or the record's current revision on
/// <see cref="ExperienceStoreOutcome.StaleRevision"/>, so the caller can retry against it. Otherwise 0.
/// </param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceRecordDeleteResult(
    ExperienceStoreOutcome Outcome,
    long Revision,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// The result of one call to <see cref="PostgresExperienceRecordStore.SweepExpiredAsync"/>.
/// </summary>
/// <param name="Outcome">
/// <see cref="ExperienceStoreOutcome.Deleted"/> when the sweep ran -- including when it found nothing to
/// erase -- or <see cref="ExperienceStoreOutcome.Denied"/> or
/// <see cref="ExperienceStoreOutcome.Invalid"/> before any storage was touched.
/// </param>
/// <param name="DeletedCount">How many records this call erased. Zero is a normal answer.</param>
/// <param name="MoreRemain">
/// Whether another call with the same arguments would find more records past the cutoff. It is what lets
/// a host's own scheduler drive a long sweep in bounded batches without this library owning a timer.
/// </param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="Interrupted">
/// Whether the batch stopped early, leaving records that were past the cutoff untouched. It is
/// <see langword="false"/> for every sweep that ran to the end of its batch, including one that found
/// nothing. When it is <see langword="true"/>, <see cref="DeletedCount"/> still counts exactly the
/// records this call erased -- erasure is per record and per transaction, so a stopped sweep leaves
/// every record it reached wholly erased -- and <see cref="MoreRemain"/> is <see langword="true"/>,
/// because at least the record it stopped on is still there.
/// </param>
public sealed record ExperienceRetentionSweepResult(
    ExperienceStoreOutcome Outcome,
    int DeletedCount,
    bool MoreRemain,
    IReadOnlyList<StoreValidationError> Errors,
    bool Interrupted = false);

/// <summary>
/// A retention sweep that was interrupted by a storage failure part-way through its batch, carrying how
/// much of the batch was irreversibly erased before it stopped.
/// </summary>
/// <remarks>
/// <para>
/// Erasure is the one operation this library cannot undo, and the count is the only thing a compliance
/// log could record about a sweep that failed half-way. A bare
/// <see cref="ExperienceStoreException"/> would throw that number away, so this carries it -- while
/// still being an <see cref="ExperienceStoreException"/>, so a host that already catches the general
/// case is unaffected and does not have to learn a new type to stay correct.
/// </para>
/// <para>
/// Caller <em>cancellation</em> does not raise this: a cancelled sweep returns its
/// <see cref="ExperienceRetentionSweepResult"/> with
/// <see cref="ExperienceRetentionSweepResult.Interrupted"/> set, because stopping between records is a
/// normal, expected way to run a sweep and the host asked for it.
/// </para>
/// </remarks>
public sealed class ExperienceRetentionSweepInterruptedException : ExperienceStoreException
{
    /// <summary>Creates the exception around the work the sweep had already done.</summary>
    /// <param name="partial">What the sweep erased before it stopped.</param>
    /// <param name="innerException">The storage failure that stopped it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="partial"/> is <see langword="null"/>.</exception>
    public ExperienceRetentionSweepInterruptedException(ExperienceRetentionSweepResult partial, Exception innerException)
        : base("An Experience Record retention sweep was interrupted by a storage infrastructure error; "
            + "the records it had already erased are erased.", innerException)
    {
        ArgumentNullException.ThrowIfNull(partial);
        Partial = partial;
    }

    /// <summary>
    /// What the sweep erased before it stopped, with
    /// <see cref="ExperienceRetentionSweepResult.Interrupted"/> set. Its
    /// <see cref="ExperienceRetentionSweepResult.DeletedCount"/> is exact.
    /// </summary>
    public ExperienceRetentionSweepResult Partial { get; }
}

/// <summary>
/// The result of one call to <see cref="PostgresExperienceGrantStore.PurgeExpiredAsync"/>.
/// </summary>
/// <remarks>
/// It reports an <see cref="ExperienceStoreOutcome"/> rather than an
/// <see cref="ExperienceGrantOutcome"/>: that enum's vocabulary is issue, revoke, and read, and a purge
/// is none of the three. Calling a purge <c>Revoked</c> would put the word for "access ended, trail
/// kept" on the one operation that removes the trail.
/// </remarks>
/// <param name="Outcome">
/// <see cref="ExperienceStoreOutcome.Deleted"/> when the purge ran -- including when it found nothing --
/// or <see cref="ExperienceStoreOutcome.Denied"/> or <see cref="ExperienceStoreOutcome.Invalid"/> before
/// any storage was touched.
/// </param>
/// <param name="PurgedCount">How many grant rows this call removed, with their audit events.</param>
/// <param name="MoreRemain">Whether another call with the same arguments would find more.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceGrantPurgeResult(
    ExperienceStoreOutcome Outcome,
    int PurgedCount,
    bool MoreRemain,
    IReadOnlyList<StoreValidationError> Errors);
