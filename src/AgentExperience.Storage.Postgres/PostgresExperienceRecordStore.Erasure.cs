using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

// Erasure and retention: delete, the bounded retention sweep, and the one purge path they share.
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>
    /// The one erasure path, created by <c>0010</c>. Every step of it -- the scope and revision guards,
    /// the seven tables it sweeps, and the tombstone it leaves -- runs inside this one function, in one
    /// transaction, under a marker the append-only guards recognise and no other session can see. This
    /// adapter composes no DELETE of its own: there is nothing here to get out of step with the order the
    /// script pins.
    /// </summary>
    /// <summary>
    /// The transaction-local marker 0016's guard reads: this transaction destroys the key of any sealed record it
    /// tombstones. Like 0010's marker it is not a privilege boundary -- any session can set it -- only a guard
    /// against a process that forgot its encryption.
    /// </summary>
    private const string DestroysKeyMarkerSql = "SET LOCAL agent_experience.erasure_destroys_key = 'on'";

    private const string PurgeSql =
        "SELECT purge_outcome, purge_revision FROM agent_experience.purge_experience_record(" +
        "@experience_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, " +
        "@expected_revision, @deleted_at)";

    /// <summary>
    /// One bounded page of a scope's records that are older than the retention cutoff, oldest first.
    /// Deliberately only the IDs and their scope: the sweep erases what it finds and never reads a
    /// payload it is about to destroy. One row beyond the batch is selected so the result can say
    /// whether more remain without a second count.
    /// </summary>
    private const string SweepCandidatesSql =
        $"SELECT {SweepCandidateColumns} FROM {Table} " +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND created_at < @cutoff " +
        "ORDER BY created_at, experience_id LIMIT @limit";

    /// <summary>
    /// The same page across the scope and every scope beneath it (<see cref="ScopeMatch.Subtree"/>).
    /// Served by <c>0010</c>'s <c>ix_experience_records_live_by_age</c>, whose leading columns are the
    /// three required fields and then <c>created_at</c>.
    /// </summary>
    private const string SweepSubtreeCandidatesSql =
        $"SELECT {SweepCandidateColumns} FROM {Table} " +
        $"WHERE {SubtreeScopePredicate} AND {LivePredicate} AND created_at < @cutoff " +
        "ORDER BY created_at, experience_id LIMIT @limit";

    /// <summary>The purge function's outcome for a record it erased.</summary>
    private const string PurgedOutcome = "Deleted";

    /// <summary>The purge function's outcome for a record that was already a tombstone.</summary>
    private const string AlreadyPurgedOutcome = "AlreadyDeleted";

    /// <summary>The purge function's outcome for a record that is not in the requesting scope, erased or not.</summary>
    private const string PurgeNotFoundOutcome = "NotFound";

    /// <summary>The purge function's outcome for a record whose revision has moved past the expected one.</summary>
    private const string PurgeStaleOutcome = "StaleRevision";

    /// <summary>
    /// Erases one record: its payload and every stored row that named it, leaving a payload-free
    /// tombstone under the same ID. This is the only destructive operation this library has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is erased, and what is left.</b> The evidence ledger, the exposure rows, the grants and
    /// their audit events, the lifecycle history, and the embedding are removed. The record row survives
    /// carrying only <see cref="ExperienceRecord.ExperienceId"/>, the six scope columns,
    /// <see cref="ExperienceRecord.Revision"/>, the deletion timestamp, a tombstone status, and a fixed
    /// <c>task_id</c> placeholder. <c>experience_grant_access</c> rows are deliberately kept: they name
    /// a grant and a principal, carry no payload, and are the answer to "who read this before it was
    /// deleted". See docs/guide/deletion-and-retention.md for the retained list, stated exhaustively.
    /// </para>
    /// <para>
    /// <b>One transaction, one code path.</b> Every step runs inside <c>0010</c>'s
    /// <c>purge_experience_record</c> function, in the order that script pins, under a
    /// transaction-scoped marker the append-only guards recognise. The guards are never disabled and
    /// never widened for another session. That buys atomicity and a single path -- not a privilege
    /// boundary; docs/guide/deletion-and-retention.md says exactly what it does not bind.
    /// </para>
    /// <para>
    /// <b>Foreign scope is indistinguishable from absent</b>, exactly as it is everywhere else: both are
    /// <see cref="ExperienceStoreOutcome.NotFound"/>, decided by one statement's predicate rather than
    /// by a branch here. <b>Deleting twice</b> is <see cref="ExperienceStoreOutcome.Deleted"/> again,
    /// with nothing written.
    /// </para>
    /// <para>
    /// <b>This is not on <see cref="IExperienceRecordStore"/>.</b> Erasure is a capability of this
    /// adapter, not of the port: Core never deletes, and a port method would oblige every
    /// implementation -- including the in-memory doubles hosts write for tests -- to promise an erasure
    /// it cannot actually perform.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the record must lie in. Never treated as authority.</param>
    /// <param name="experienceId">The record to erase. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Deleted"/>, <see cref="ExperienceStoreOutcome.NotFound"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public Task<ExperienceRecordDeleteResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        DeleteAsync(authorization, scope, experienceId, expectedRevision: null, cancellationToken);

    /// <summary>
    /// The same erasure, refused unless the record is still at <paramref name="expectedRevision"/>.
    /// </summary>
    /// <remarks>
    /// The revision guard, the scope predicate, and the existence check are one statement inside the
    /// purge function, so a stale revision, a foreign scope, and a missing record are all "no row" --
    /// and only the scope that owns the record is told which. Pass <see langword="null"/> to erase
    /// whatever revision the record is at, which is what the retention sweep does: an age-based deletion
    /// is not racing a writer for a particular version.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the record must lie in. Never treated as authority.</param>
    /// <param name="experienceId">The record to erase. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="expectedRevision">The revision the record must still be at, or <see langword="null"/> for none. Must not be negative.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Deleted"/>, <see cref="ExperienceStoreOutcome.StaleRevision"/>
    /// (carrying the record's current revision), <see cref="ExperienceStoreOutcome.NotFound"/>,
    /// <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public async Task<ExperienceRecordDeleteResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        // Opened before the arguments are checked, exactly as Core's operations are, so even a refused
        // or malformed erasure is counted. The record ID is the only identifier written: the tombstone
        // keeps it, and nothing the erasure removed ever reaches telemetry.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.Delete);
        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.ExperienceIdAttribute, experienceId.ToString("D"));

        ExperienceRecordDeleteResult result;
        try
        {
            result = await DeleteCoreAsync(authorization, scope, experienceId, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    private async Task<ExperienceRecordDeleteResult> DeleteCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateDelete(scope, experienceId, expectedRevision);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, errors);
        }

        if (!authorization.Permits(scope))
        {
            // Fail-closed, and before any connection opens: nothing is erased and nothing is read.
            return new(ExperienceStoreOutcome.Denied, 0, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (result, _) = await PurgeAsync(connection, authorization, scope, experienceId, expectedRevision, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "delete", cancellationToken);
        }
    }

    /// <summary>
    /// Erases the records in exactly one scope that are older than <paramref name="retentionAge"/>, in
    /// one bounded batch. The same as
    /// <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/>
    /// with <see cref="ScopeMatch.Exact"/>.
    /// </summary>
    /// <remarks>
    /// <b>This sweeps the exact scope and no scope under it.</b> A sweep of <c>(tenant, app, project)</c>
    /// with no team, agent or user reaches only the records stored with all three of those null, and
    /// reports <see cref="ExperienceRetentionSweepResult.MoreRemain"/> <see langword="false"/> while
    /// team-, agent- and user-scoped records under it survive. A host whose policy covers everything
    /// under a scope passes <see cref="ScopeMatch.Subtree"/> to the other overload.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope to sweep, matched field for field. Never treated as authority.</param>
    /// <param name="retentionAge">How long a record may be kept, measured from <see cref="ExperienceRecord.CreatedAt"/>. Must be strictly positive.</param>
    /// <param name="batchSize">The most records this call may erase, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="cancellationToken">Cancels the operation between records; records already erased stay erased, and the count comes back on the result rather than being lost.</param>
    /// <returns>See the other overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceRetentionSweepInterruptedException">A storage failure stopped the batch part-way; the count of what was erased is on the exception.</exception>
    public Task<ExperienceRetentionSweepResult> SweepExpiredAsync(
        AuthorizationContext authorization,
        Scope scope,
        TimeSpan retentionAge,
        int batchSize,
        CancellationToken cancellationToken) =>
        SweepExpiredAsync(authorization, scope, retentionAge, batchSize, ScopeMatch.Exact, cancellationToken);

    /// <summary>
    /// Erases the records in a scope -- or, with <see cref="ScopeMatch.Subtree"/>, in that scope and
    /// every scope beneath it -- that are older than <paramref name="retentionAge"/>, in one bounded
    /// batch, through exactly the same erasure as <see cref="DeleteAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no default retention and no timer.</b> Nothing expires unless a host calls this with
    /// a positive age, and this library ships no scheduler, no background service, and no hosted
    /// service: when a sweep runs is the host's decision, made with the host's own scheduling, because
    /// only the host knows what its data-retention obligations are.
    /// </para>
    /// <para>
    /// <b>Bounded, and resumable.</b> At most <paramref name="batchSize"/> records are erased per call,
    /// oldest <see cref="ExperienceRecord.CreatedAt"/> first across everything the match reaches, and
    /// <see cref="ExperienceRetentionSweepResult.MoreRemain"/> says whether another call would find
    /// more -- across the whole subtree under <see cref="ScopeMatch.Subtree"/>. Each record is erased in
    /// its own transaction, in its own exact stored scope, so an interrupted sweep leaves every record
    /// it reached wholly erased and every record it did not reach wholly untouched.
    /// <see cref="ExperienceRetentionSweepResult.DeletedCount"/> counts only the records this call
    /// erased: one another caller erased first is not counted again.
    /// </para>
    /// <para>
    /// The cutoff is measured on this store's <see cref="TimeProvider"/> against the record's stored
    /// <see cref="ExperienceRecord.CreatedAt"/>, never against <see cref="ExperienceRecord.UpdatedAt"/>:
    /// age is how long the library has held the data, and a record that is read, ranked, or re-scored
    /// does not thereby become younger.
    /// </para>
    /// <para>
    /// <b><see cref="ScopeMatch.Exact"/> sweeps the exact scope and no scope under it, and that is the
    /// one failure mode here that looks like success.</b> A sweep of <c>(tenant, app, project)</c> with
    /// no team, agent or user reaches only the records stored with all three of those null; records the
    /// same project holds under a team, an agent or a user are a <em>different</em> scope and are not
    /// swept, not counted, and not reflected in <see cref="ExperienceRetentionSweepResult.MoreRemain"/>.
    /// <see cref="ScopeMatch.Subtree"/> is how a host whose policy covers everything under a scope says
    /// so. What it reaches is defined exactly on <see cref="ScopeMatch"/>: never another tenant,
    /// application or project, never an ancestor or a sibling of <paramref name="scope"/>.
    /// </para>
    /// <para>
    /// <b>Authorization is decided on <paramref name="scope"/>, and that covers the subtree.</b> An
    /// <see cref="AuthorizationContext"/> that permits the root has no bound on any field the root
    /// leaves null, so it permits every scope beneath it; one bounded to a team does not permit a
    /// project root at all, and is <see cref="ExperienceStoreOutcome.Denied"/> before any connection
    /// opens. Every candidate is checked again, against the root and the authorization, before it is
    /// erased.
    /// </para>
    /// <para>
    /// <b>Stopping early.</b> Cancelling between records returns what the call had already erased, with
    /// <see cref="ExperienceRetentionSweepResult.Interrupted"/> set, rather than throwing away the
    /// count. A storage failure part-way through throws
    /// <see cref="ExperienceRetentionSweepInterruptedException"/>, which carries the same partial
    /// result and is an <see cref="ExperienceStoreException"/> like any other storage failure here.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to sweep, or the root of the subtree to sweep. Never treated as authority.</param>
    /// <param name="retentionAge">How long a record may be kept, measured from <see cref="ExperienceRecord.CreatedAt"/>. Must be strictly positive.</param>
    /// <param name="batchSize">The most records this call may erase, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to sweep <paramref name="scope"/> alone or everything beneath it too. A value the enum does not define is <see cref="ExperienceStoreOutcome.Invalid"/>.</param>
    /// <param name="cancellationToken">Cancels the operation between records; records already erased stay erased, and the count comes back on the result rather than being lost.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Deleted"/> when the sweep ran (possibly erasing nothing, and
    /// possibly stopping early -- see <see cref="ExperienceRetentionSweepResult.Interrupted"/>),
    /// <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceRetentionSweepInterruptedException">A storage failure stopped the batch part-way; the count of what was erased is on the exception.</exception>
    public async Task<ExperienceRetentionSweepResult> SweepExpiredAsync(
        AuthorizationContext authorization,
        Scope scope,
        TimeSpan retentionAge,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken)
    {
        // One span for the whole batch, never one per record: the records are erased through PurgeAsync,
        // not through DeleteAsync, and a span attribute is not a place for a list that grows with the
        // batch. What reaches the trace is how many were erased, whether the batch stopped early, and
        // how wide it was asked to reach -- never which scope.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.RetentionSweep);
        ErasureDiagnostics.TagScopeMatch(operation, match);

        ExperienceRetentionSweepResult result;
        try
        {
            result = await SweepExpiredCoreAsync(authorization, scope, retentionAge, batchSize, match, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExperienceRetentionSweepInterruptedException ex)
        {
            // How much a failed sweep irreversibly erased is the one fact a compliance log needs, so it
            // reaches the trace as well as the exception.
            TagSweep(operation, ex.Partial);
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        TagSweep(operation, result);
        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    /// <summary>Writes what a sweep did -- a count and a flag, never which records -- onto its span.</summary>
    private static void TagSweep(in ErasureTrace operation, ExperienceRetentionSweepResult result)
    {
        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.ErasedCountAttribute, result.DeletedCount);
        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.InterruptedAttribute, result.Interrupted);
    }

    private async Task<ExperienceRetentionSweepResult> SweepExpiredCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        TimeSpan retentionAge,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateRetentionSweep(scope, retentionAge, batchSize, match);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, false, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, false, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = ToStoredTimestamp(_timeProvider.GetUtcNow() - retentionAge);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            var candidates = new List<(Guid ExperienceId, Scope Scope)>(batchSize + 1);
            var candidatesSql = match == ScopeMatch.Subtree ? SweepSubtreeCandidatesSql : SweepCandidatesSql;
            await using (var page = await ExperienceSessionContext.BeginAsync(connection, authorization, cancellationToken).ConfigureAwait(false))
            {
                await using (var command = new NpgsqlCommand(candidatesSql, connection, page))
                {
                    AddScopeParameters(command.Parameters, scope);
                    command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));

                    // One row beyond the batch, so "more remain" is read off the same statement rather than
                    // from a second count that could disagree with it.
                    command.Parameters.Add(new NpgsqlParameter<int>("limit", batchSize + 1));

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        candidates.Add((
                            reader.GetGuid(0),
                            new Scope(
                                reader.GetString(1),
                                reader.GetString(2),
                                reader.GetString(3),
                                reader.IsDBNull(4) ? null : reader.GetString(4),
                                reader.IsDBNull(5) ? null : reader.GetString(5),
                                reader.IsDBNull(6) ? null : reader.GetString(6))));
                    }
                }

                await page.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var moreRemain = candidates.Count > batchSize;

            // Declared outside the loop, and read again by both handlers below, because the number of
            // records this call irreversibly erased is the one fact a compliance log needs and it must
            // not be lost just because the batch stopped early.
            var deleted = 0;
            try
            {
                foreach (var (experienceId, candidateScope) in candidates.Take(batchSize))
                {
                    // Defence in depth over the page the database returned: the candidate must lie at or
                    // beneath the root this call was authorized for, and the authorization must permit
                    // it on its own. Both hold by construction (see ScopeMatch), so neither can fail
                    // unless the predicate above is wrong. If it is, the sweep stops loudly without
                    // erasing that record: silently skipping it would leave it at the head of every later
                    // page, so every call would erase nothing and report MoreRemain forever.
                    if (!IsAtOrBeneath(candidateScope, scope, match) || !authorization.Permits(candidateScope))
                    {
                        throw new ExperienceRetentionSweepInterruptedException(
                            new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true),
                            new ExperienceStoreException(
                                "A retention sweep candidate lay outside the requested scope or authorization; "
                                + "the sweep stopped without erasing it."));
                    }

                    // No expected revision: a sweep deletes a record for its age, not for the version it
                    // happened to be at when the page was read. The candidate's own exact scope, so the
                    // purge function's scope guard is the same exact-match guard DeleteAsync relies on.
                    var (_, erasedNow) = await PurgeAsync(connection, authorization, candidateScope, experienceId, expectedRevision: null, cancellationToken)
                        .ConfigureAwait(false);

                    // Only what this call erased. A record another sweep or delete erased first comes back
                    // as already a tombstone, and counting it here too would let two racing sweeps report
                    // more erasures than there were records.
                    if (erasedNow)
                    {
                        deleted++;
                    }
                }
            }
            catch (Exception ex) when (ex is not ExperienceStoreException && cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation, however the driver reported it -- an OperationCanceledException,
                // or the server's own query_canceled for a statement that was already running. Decided
                // by the token exactly as Translate decides it, so the two never disagree.
                //
                // The host asked the sweep to stop, which is a normal way to run one: each record was
                // erased in its own transaction, so what is erased is erased and what is left is whole.
                // Returned rather than thrown, because a cancelled sweep that threw away its count would
                // leave a host unable to say how much of its data it had just destroyed. MoreRemain is
                // true regardless of what the page said: at least the record it stopped on is still there.
                return new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true);
            }
            catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
            {
                // A failure, not a request. Thrown -- a host must not read this as a sweep that ran --
                // but thrown carrying the count, as an ExperienceStoreException like every other storage
                // failure here, so nothing that already catches those has to change.
                throw new ExperienceRetentionSweepInterruptedException(
                    new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true),
                    Translate(ex, "retention sweep", cancellationToken));
            }
            catch (ExperienceStoreException ex) when (ex is not ExperienceRetentionSweepInterruptedException)
            {
                // Encrypted mode's key store failed to destroy a key (or a sealed value failed to open).
                // That record's erasure rolled back whole; the count of what this call erased before it is
                // kept, exactly as for a database failure.
                throw new ExperienceRetentionSweepInterruptedException(
                    new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true),
                    ex);
            }

            return new(ExperienceStoreOutcome.Deleted, deleted, moreRemain, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            // Only the candidate read can reach this now, and it erases nothing.
            throw Translate(ex, "retention sweep", cancellationToken);
        }
    }

    /// <summary>
    /// Erases one record. In plaintext mode that is the purge function alone -- one statement, so the whole
    /// erasure is one transaction whether or not the caller opened one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In encrypted mode the key is destroyed inside the erasure's transaction, after every database-side
    /// check and before the commit.</b> The purge function runs first -- it locks the row, applies the scope and
    /// revision guards, needs <c>EXECUTE</c> on itself, and writes the tombstone -- all uncommitted, so nothing
    /// is visible to anyone yet. Only when it answers that it erased the record (or that the record already was a
    /// tombstone) is the record's key destroyed, and only then does the transaction commit. A refusal of any
    /// kind -- another scope, a stale revision, no <c>EXECUTE</c>, a failing statement -- destroys nothing. The
    /// one rule both remaining failure orders follow is "a record is erased once its key is destroyed":
    /// </para>
    /// <list type="bullet">
    /// <item><b>The key store fails.</b> The transaction rolls back: the record is untouched, live and
    /// readable, and its key is alive. The caller gets an <see cref="ExperienceStoreException"/>. The record
    /// never looks erased while its key survives.</item>
    /// <item><b>The key is destroyed and the commit then fails</b> (a lost connection, a crash). The row is
    /// still there, but every read treats a sealed row whose key is destroyed exactly as a tombstone, and every
    /// write is refused, because the key store will not create a key for a destroyed reference. The record
    /// never looks live while its key is gone. A retried delete, or the next sweep, finds the row still live,
    /// destroys the (already destroyed) key again and commits the tombstone.</item>
    /// </list>
    /// <para>
    /// A record that is already a tombstone has its key destroyed again (idempotently), so a tombstone written
    /// by a process that was not configured for encryption still loses its key.
    /// </para>
    /// </remarks>
    private async Task<(ExperienceRecordDeleteResult Result, bool ErasedNow)> PurgeAsync(
        NpgsqlConnection connection,
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        // The purge function runs as its owner, which row-level security does not bind; the erasure still
        // declares its bounds like every other operation, so nothing it runs before or around the function can
        // reach outside them.
        await using var transaction = await ExperienceSessionContext
            .BeginAsync(connection, authorization, cancellationToken, System.Data.IsolationLevel.ReadCommitted).ConfigureAwait(false);

        if (_encryption is null)
        {
            var plaintext = await RunPurgeAsync(connection, transaction, scope, experienceId, expectedRevision, cancellationToken)
                .ConfigureAwait(false);

            // The purge has run; a late cancellation must not turn its commit into an erasure the caller is told
            // failed, exactly as the encrypted path below commits past the caller's token.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return plaintext;
        }

        // 0016's guard refuses to tombstone a sealed row unless the erasing transaction says it destroys the key:
        // a process that was not configured for encryption fails loudly instead of leaving the key behind.
        await using (var marker = new NpgsqlCommand(DestroysKeyMarkerSql, connection, transaction))
        {
            await marker.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await RunPurgeAsync(connection, transaction, scope, experienceId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        if (result.Result.Outcome != ExperienceStoreOutcome.Deleted)
        {
            // NotFound or StaleRevision: nothing was written, and no key is touched.
            return result;
        }

        // From here the caller's token no longer applies: a destruction that completed must be followed by its
        // commit, or a cancelled request would leave exactly the half-erased record this ordering exists to
        // avoid. Throws on failure, and the transaction is then rolled back by its disposal: nothing erased.
        await _encryption.DestroyAsync(experienceId, scope, CancellationToken.None).ConfigureAwait(false);

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs the purge function and maps its outcome.</summary>
    private async Task<(ExperienceRecordDeleteResult Result, bool ErasedNow)> RunPurgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PurgeSql, connection, transaction);
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(parameters, scope);
        parameters.Add(new NpgsqlParameter("expected_revision", NpgsqlDbType.Bigint)
        {
            Value = expectedRevision is { } revision ? revision : DBNull.Value,
        });
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("deleted_at", ToStoredTimestamp(_timeProvider.GetUtcNow())));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The function always returns exactly one row; treat the impossible case the way a missing
            // record is treated, which writes nothing and claims nothing.
            return (new(ExperienceStoreOutcome.NotFound, 0, NoErrors), false);
        }

        var outcome = reader.GetString(0);
        var currentRevision = reader.GetInt64(1);

        return outcome switch
        {
            // Erased now, or erased earlier: deleting twice is a success that touches nothing. Only the
            // first is this call's erasure, which is what a sweep counts.
            PurgedOutcome => (new(ExperienceStoreOutcome.Deleted, currentRevision, NoErrors), true),
            AlreadyPurgedOutcome => (new(ExperienceStoreOutcome.Deleted, currentRevision, NoErrors), false),
            PurgeStaleOutcome => (new(ExperienceStoreOutcome.StaleRevision, currentRevision, NoErrors), false),
            PurgeNotFoundOutcome => (new(ExperienceStoreOutcome.NotFound, 0, NoErrors), false),
            _ => throw new ExperienceStoreException("The erasure function reported an unrecognized outcome."),
        };
    }
}
