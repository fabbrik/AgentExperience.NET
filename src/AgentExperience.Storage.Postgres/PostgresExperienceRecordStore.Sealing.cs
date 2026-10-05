using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

// Crypto-shredding upgrade jobs: sealing plaintext records and backfilling sealed records' authorship flag.
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>The upgrade job's page: live plaintext records, oldest first, served by <c>0016</c>'s partial index.</summary>
    private const string SealCandidatesSql =
        $"SELECT {SweepCandidateColumns} FROM {Table} " +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND payload_version = 1 " +
        "ORDER BY created_at, experience_id LIMIT @limit";

    /// <summary><see cref="SealCandidatesSql"/> over a scope and everything beneath it.</summary>
    private const string SealSubtreeCandidatesSql =
        $"SELECT {SweepCandidateColumns} FROM {Table} " +
        $"WHERE {SubtreeScopePredicate} AND {LivePredicate} AND payload_version = 1 " +
        "ORDER BY created_at, experience_id LIMIT @limit";

    /// <summary>One plaintext record, whole, locked for the transaction that seals it.</summary>
    private const string LockPlaintextRecordSql =
        $"SELECT {SelectColumns} FROM {Table} WHERE experience_id = @experience_id AND {ScopePredicate} " +
        $"AND {LivePredicate} AND payload_version = 1 FOR UPDATE";

    /// <summary>
    /// The authorship backfill's worklist: live sealed records stored without <c>0021</c>'s flag, past the
    /// caller's cursor, in ID order, read whole so every key can be fetched in one batch before any row is locked.
    /// </summary>
    private const string UnflaggedSealedPageSql =
        $"SELECT {SelectColumns} FROM {Table} " +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND payload_version = 2 AND {ModelAuthoredColumn} IS NULL " +
        "AND (@start_after::uuid IS NULL OR experience_id > @start_after) " +
        "ORDER BY experience_id LIMIT @limit";

    /// <summary><see cref="UnflaggedSealedPageSql"/> over a scope and everything beneath it.</summary>
    private const string UnflaggedSealedSubtreePageSql =
        $"SELECT {SelectColumns} FROM {Table} " +
        $"WHERE {SubtreeScopePredicate} AND {LivePredicate} AND payload_version = 2 AND {ModelAuthoredColumn} IS NULL " +
        "AND (@start_after::uuid IS NULL OR experience_id > @start_after) " +
        "ORDER BY experience_id LIMIT @limit";

    /// <summary>
    /// The backfill's one write: the flag of a live sealed row that still has none, and nothing else. The statement
    /// takes the row lock and re-checks the flag itself. The application role holds no <c>UPDATE</c> on the column, so
    /// only the owner can run it; <c>0021</c>'s trigger keeps what a writer supplies on a sealed row, and <c>0010</c>'s
    /// projection guard admits a change that moves neither the status, the confidence nor the revision.
    /// </summary>
    private const string SetSealedAuthorshipSql =
        $"UPDATE {Table} SET {ModelAuthoredColumn} = @reflection_model_authored " +
        $"WHERE experience_id = @experience_id AND {ScopePredicate} AND {LivePredicate} " +
        $"AND payload_version = 2 AND {ModelAuthoredColumn} IS NULL";

    /// <summary><c>0016</c>'s one sealing transition.</summary>
    private const string SealRecordSql =
        "SELECT agent_experience.seal_experience_record(@experience_id, @tenant_id, @application_id, @project_id, " +
        "@team_id, @agent_id, @user_id, @expected_revision, @sealed_payload)";

    /// <summary>
    /// The crypto-shredding upgrade: seals up to <paramref name="batchSize"/> records that were written in
    /// plaintext, oldest first, in <paramref name="scope"/> -- or, with <see cref="ScopeMatch.Subtree"/>, in that
    /// scope and every scope beneath it. Bounded, resumable, and authorized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it does to one record</b>, in one transaction: locks the live plaintext row, creates the
    /// record's key, seals its task ID and payload together, opens the seal again and checks it gives back
    /// exactly the stored text, and only then calls <c>0016</c>'s <c>seal_experience_record</c>, which admits
    /// one transition (a live plaintext row at the revision read, into its sealed shape) and copies the row's
    /// own full-text vector into <c>search_vector_sealed</c>. The revision does not move and nothing else about
    /// the record changes: it reads, ranks and indexes exactly as before.
    /// </para>
    /// <para>
    /// <b>Bounded and resumable.</b> One page of at most <paramref name="batchSize"/> candidates, served by
    /// <c>0016</c>'s partial index over live plaintext rows, so a sealed record drops out of the worklist and a
    /// re-run starts where the last one stopped. <see cref="ExperienceSealingResult.MoreRemain"/> says whether
    /// another call would find more. Each record is its own transaction: an interrupted call leaves every
    /// record wholly sealed or wholly untouched, and sealing is idempotent, so a failed call is simply
    /// re-run.
    /// </para>
    /// <para>
    /// <b>Authorized twice.</b> <paramref name="authorization"/> must permit <paramref name="scope"/>, which
    /// covers its subtree exactly as it does for <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/>;
    /// and in the two-role deployment the application role can call the sealing function only when the host
    /// set <see cref="ExperienceApplicationRoleOptions.AllowSealing"/>.
    /// </para>
    /// <para>
    /// <b>What it cannot seal.</b> Rows the append-only ledgers already hold -- lifecycle reasons, evidence
    /// detail, feedback rationale, grant events -- and grant reasons written before the switch stay
    /// plaintext: this library does not open an <c>UPDATE</c> path on its audit trail. Copies made before
    /// sealing (backups, WAL archives, the dead tuple each seal leaves until <c>VACUUM</c>) are plaintext too.
    /// The upgrade runbook in docs/guide/crypto-shredding.md says what to do about each.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to seal, or the root of the subtree to seal. Never treated as authority.</param>
    /// <param name="batchSize">The most records this call may seal, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to seal <paramref name="scope"/> alone or everything beneath it too.</param>
    /// <param name="cancellationToken">Cancels the operation between records.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Committed"/> when the batch ran (possibly sealing nothing),
    /// <see cref="ExperienceStoreOutcome.Invalid"/> -- including when this store has no
    /// <see cref="ExperienceEncryption"/> -- or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public async Task<ExperienceSealingResult> SealPlaintextRecordsAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken)
    {
        // Counted like the other bounded batches this store runs: a count, whether it stopped early, and how
        // wide it reached -- never which records, and never anything they hold.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.RecordSeal);
        ErasureDiagnostics.TagScopeMatch(operation, match);

        ExperienceSealingResult result;
        try
        {
            result = await SealPlaintextRecordsCoreAsync(authorization, scope, batchSize, match, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.SealedCountAttribute, result.SealedCount);
        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    private async Task<ExperienceSealingResult> SealPlaintextRecordsCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateSealing(scope, batchSize, match, _encryption is not null);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, false, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, false, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            var candidates = new List<(Guid ExperienceId, Scope Scope)>(batchSize + 1);
            await using (var page = await ExperienceSessionContext.BeginAsync(connection, authorization, cancellationToken).ConfigureAwait(false))
            {
                await using (var command = new NpgsqlCommand(match == ScopeMatch.Subtree ? SealSubtreeCandidatesSql : SealCandidatesSql, connection, page))
                {
                    AddScopeParameters(command.Parameters, scope);
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

            var sealedCount = 0;
            foreach (var (experienceId, candidateScope) in candidates.Take(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Defence in depth over the page, exactly as the sweep does it.
                if (!IsAtOrBeneath(candidateScope, scope, match) || !authorization.Permits(candidateScope))
                {
                    throw new ExperienceStoreException(
                        "A sealing candidate lay outside the requested scope or authorization; the job stopped without sealing it.");
                }

                if (await SealOneAsync(connection, authorization, experienceId, candidateScope, cancellationToken).ConfigureAwait(false))
                {
                    sealedCount++;
                }
            }

            return new(ExperienceStoreOutcome.Committed, sealedCount, candidates.Count > batchSize, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "record sealing", cancellationToken);
        }
    }

    /// <summary>Seals one plaintext record, in its own transaction. <see langword="false"/> when there was nothing to seal.</summary>
    private async Task<bool> SealOneAsync(
        NpgsqlConnection connection,
        AuthorizationContext authorization,
        Guid experienceId,
        Scope scope,
        CancellationToken cancellationToken)
    {
        await using var transaction = await ExperienceSessionContext
            .BeginAsync(connection, authorization, cancellationToken, System.Data.IsolationLevel.ReadCommitted).ConfigureAwait(false);

        long revision;
        string plaintext;
        await using (var lockRow = new NpgsqlCommand(LockPlaintextRecordSql, connection, transaction))
        {
            lockRow.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            AddScopeParameters(lockRow.Parameters, scope);
            await using var reader = await lockRow.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Sealed, erased or moved since the page was read: nothing to do.
                return false;
            }

            // Decoded first so a row this adapter could not read is never sealed into something it cannot read
            // either; then the stored text itself -- not a re-serialization -- is what gets sealed.
            revision = ReadRecord(reader).Revision;
            plaintext = SealedText.SealedRecordPlaintext(reader.GetString(8), reader.GetString(17));
        }

        using var key = await _encryption!.ForWriteAsync(experienceId, scope, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            // Its key was destroyed by a delete that did not commit. Leaving it would keep it at the head of every
            // later page for ever, so the job finishes that delete instead (which needs AllowErasure, and throws
            // loudly without it). It is not counted as sealed.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await PurgeAsync(connection, authorization, scope, experienceId, expectedRevision: null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var sealedValue = key.Seal(SealedText.PayloadColumn, Guid.Empty, plaintext);

        // The check the database cannot make, because it holds no key: what is about to be stored opens, under
        // this record's own associated data, to the stored text, and that text still decodes as a record payload.
        var (openedTaskId, openedPayload) = SealedText.ReadSealedRecordPlaintext(
            key.Open(SealedText.PayloadColumn, Guid.Empty, SealedText.ReadPayloadEnvelope(SealedText.PayloadEnvelope(sealedValue))));
        if (!string.Equals(SealedText.SealedRecordPlaintext(openedTaskId, openedPayload), plaintext, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException("A sealed payload did not open to the text it was sealed from; nothing was written.");
        }

        _ = ExperiencePayload.Deserialize(ExperiencePayload.CurrentVersion, openedPayload);

        string outcome;
        await using (var seal = new NpgsqlCommand(SealRecordSql, connection, transaction))
        {
            seal.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            AddScopeParameters(seal.Parameters, scope);
            seal.Parameters.Add(new NpgsqlParameter<long>("expected_revision", revision));
            seal.Parameters.Add(new NpgsqlParameter<string>("sealed_payload", NpgsqlDbType.Jsonb)
            {
                TypedValue = SealedText.PayloadEnvelope(sealedValue),
            });
            outcome = (string?)await seal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }

        if (!string.Equals(outcome, "Sealed", StringComparison.Ordinal))
        {
            // Under the row lock none of the others can happen except by a writer outside this library.
            throw new ExperienceStoreException("The sealing function refused a record the job had locked.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The owner-run authorship backfill, from the start of the worklist. See
    /// <see cref="BackfillSealedAuthorshipAsync(AuthorizationContext, Scope, int, ScopeMatch, Guid?, CancellationToken)"/>.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to backfill, or the root of the subtree to backfill. Never treated as authority.</param>
    /// <param name="batchSize">The most records this call may examine, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to backfill <paramref name="scope"/> alone or everything beneath it too.</param>
    /// <param name="cancellationToken">Cancels the operation between records.</param>
    /// <returns>The batch's result; pass its <see cref="ExperienceAuthorshipBackfillResult.ResumeAfter"/> to the next call.</returns>
    public Task<ExperienceAuthorshipBackfillResult> BackfillSealedAuthorshipAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken) =>
        BackfillSealedAuthorshipAsync(authorization, scope, batchSize, match, startAfter: null, cancellationToken);

    /// <summary>
    /// The owner-run authorship backfill: examines up to <paramref name="batchSize"/> live sealed records in
    /// <paramref name="scope"/> -- or, with <see cref="ScopeMatch.Subtree"/>, in that scope and every scope beneath it --
    /// whose <c>0021</c> authorship flag is unknown (<c>NULL</c>), in ID order after <paramref name="startAfter"/>; opens
    /// each payload with the record's key, decides its authorship by the rule every store shares, and writes the flag.
    /// Bounded, resumable, and authorized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A sealed row stored without its flag -- sealed before <c>0021</c>, whose payload the migration could
    /// not open, sealed during a rolling deploy by an instance on an earlier build, or written by any writer that left
    /// the flag out -- is left out of every search that excludes model-authored records, because unknown authorship
    /// counts as model-authored. This job restores the flag, so a deterministic record is found again. Run it after
    /// upgrading: start with no cursor and pass each result's <see cref="ExperienceAuthorshipBackfillResult.ResumeAfter"/>
    /// to the next call until <see cref="ExperienceAuthorshipBackfillResult.MoreRemain"/> is <see langword="false"/>.
    /// </para>
    /// <para>
    /// <b>What it does.</b> One page of the worklist is read whole, and every key it needs is fetched in one key-store
    /// call (<see cref="IExperienceKeyStore.GetKeysAsync"/>) before any row is locked. Then, record by record, one
    /// statement locks the row, re-checks that it is still live, sealed and unflagged, and sets the flag to whether its
    /// reflection counts as model-authored (its authorship is anything but <see cref="ReflectionAuthorship.Deterministic"/>,
    /// or its producer starts with <see cref="ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix"/>); a
    /// record with no reflection gets <see langword="false"/>. Nothing else about the row changes: not its revision,
    /// payload, status or timestamps. A flag already written is never changed.
    /// </para>
    /// <para>
    /// <b>Skipped rows.</b> A record whose key was destroyed (an erasure that did not commit), or whose payload cannot
    /// be opened or decoded, is left alone and counted in <see cref="ExperienceAuthorshipBackfillResult.SkippedCount"/>;
    /// it never stops the batch, and the cursor moves past it. It stays unknown, and so excluded, until it is erased or
    /// repaired. A key the key store never held is a configuration failure and throws, as every read does.
    /// </para>
    /// <para>
    /// <b>Failure.</b> A call that throws may already have set some flags; the count is lost with the exception, but the
    /// job is idempotent, so rerunning it (from the same cursor, or from the start) is safe and finds only what is left.
    /// </para>
    /// <para>
    /// <b>Owner-run.</b> The application role holds no <c>UPDATE</c> on the flag, so this store must be constructed
    /// over the owner's data source, with the <see cref="ExperienceEncryption"/> the records were sealed with; over the
    /// application role the write fails with a permission error, wrapped in an <see cref="ExperienceStoreException"/>,
    /// and nothing is set. <paramref name="authorization"/> must permit <paramref name="scope"/>, which covers its
    /// subtree exactly as it does for <see cref="SealPlaintextRecordsAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to backfill, or the root of the subtree to backfill. Never treated as authority.</param>
    /// <param name="batchSize">The most records this call may examine, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to backfill <paramref name="scope"/> alone or everything beneath it too.</param>
    /// <param name="startAfter">
    /// The previous call's <see cref="ExperienceAuthorshipBackfillResult.ResumeAfter"/>, or <see langword="null"/> to
    /// start from the beginning of the worklist.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation between records.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Committed"/> when the batch ran (possibly setting nothing),
    /// <see cref="ExperienceStoreOutcome.Invalid"/> -- including when this store has no
    /// <see cref="ExperienceEncryption"/> -- or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public async Task<ExperienceAuthorshipBackfillResult> BackfillSealedAuthorshipAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        Guid? startAfter,
        CancellationToken cancellationToken)
    {
        // Counted like the sealing job: a count and how wide it reached -- never which records, and never a flag.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.AuthorshipBackfill);
        ErasureDiagnostics.TagScopeMatch(operation, match);

        ExperienceAuthorshipBackfillResult result;
        try
        {
            result = await BackfillSealedAuthorshipCoreAsync(authorization, scope, batchSize, match, startAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.BackfilledCountAttribute, result.SetCount);
        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    private async Task<ExperienceAuthorshipBackfillResult> BackfillSealedAuthorshipCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        Guid? startAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateAuthorshipBackfill(scope, batchSize, match, _encryption is not null);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, 0, false, startAfter, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, 0, false, startAfter, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            List<SnapshotRow> rows;
            await using (var page = await ExperienceSessionContext.BeginAsync(connection, authorization, cancellationToken).ConfigureAwait(false))
            {
                await using (var command = new NpgsqlCommand(
                    match == ScopeMatch.Subtree ? UnflaggedSealedSubtreePageSql : UnflaggedSealedPageSql, connection, page))
                {
                    AddScopeParameters(command.Parameters, scope);
                    command.Parameters.Add(new NpgsqlParameter("start_after", NpgsqlDbType.Uuid) { Value = (object?)startAfter ?? DBNull.Value });
                    command.Parameters.Add(new NpgsqlParameter<int>("limit", batchSize + 1));
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    rows = await SnapshotRow.ReadAllAsync(reader, cancellationToken).ConfigureAwait(false);
                }

                await page.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var examined = rows.Take(batchSize).ToList();
            var candidates = new List<(Guid ExperienceId, Scope Scope, string StoredPayload, SnapshotRow Row)>(examined.Count);
            foreach (var row in examined)
            {
                var candidate = (ExperienceId: row.GetGuid(0), Scope: ReadRecordScope(row), StoredPayload: row.GetString(17), Row: row);

                // Defence in depth over the page, exactly as the sealing job does it.
                if (!IsAtOrBeneath(candidate.Scope, scope, match) || !authorization.Permits(candidate.Scope))
                {
                    throw new ExperienceStoreException(
                        "An authorship backfill candidate lay outside the requested scope or authorization; the job stopped without writing it.");
                }

                candidates.Add(candidate);
            }

            // Every key the page needs, in one key-store call, before any row is locked.
            using var keys = await _encryption!
                .ForReadManyAsync(candidates.Select(c => new ExperienceKeyReference(c.ExperienceId, c.Scope)), cancellationToken)
                .ConfigureAwait(false);

            var setCount = 0;
            var skippedCount = 0;
            foreach (var (experienceId, candidateScope, storedPayload, row) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool modelAuthored;
                if (keys[new ExperienceKeyReference(experienceId, candidateScope)] is not { } key)
                {
                    // Its key was destroyed: the record is erased (a delete that did not commit). Left alone.
                    skippedCount++;
                    continue;
                }

                try
                {
                    // Opened with the record's own key, and decoded whole, so a row this adapter cannot read is never labelled.
                    modelAuthored = ReflectionAuthorshipRule.IsModelAuthored(DecodeSealedRecord(row, key, storedPayload).Reflection);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A payload that cannot be opened or decoded is left unknown, and so excluded; it never stops the batch.
                    skippedCount++;
                    continue;
                }

                await using var transaction = await ExperienceSessionContext
                    .BeginAsync(connection, authorization, cancellationToken, System.Data.IsolationLevel.ReadCommitted).ConfigureAwait(false);
                int written;
                await using (var update = new NpgsqlCommand(SetSealedAuthorshipSql, connection, transaction))
                {
                    update.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
                    AddScopeParameters(update.Parameters, candidateScope);
                    update.Parameters.Add(new NpgsqlParameter<bool>("reflection_model_authored", modelAuthored));
                    written = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                setCount += written;
            }

            var resumeAfter = examined.Count > 0 ? candidates[^1].ExperienceId : startAfter;
            return new(ExperienceStoreOutcome.Committed, setCount, skippedCount, rows.Count > batchSize, resumeAfter, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "authorship backfill", cancellationToken);
        }
    }
}
