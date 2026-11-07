using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Each record's failure signatures. <c>0030</c>'s <c>agent_experience.experience_failure_signatures</c>
/// holds one row per record and signature; <see cref="CreateAsync"/> writes a record's rows in the same transaction as the
/// record, and <see cref="BackfillFailureSignaturesAsync(AuthorizationContext, Scope, int, ScopeMatch, Guid?, CancellationToken)"/>
/// writes them for plaintext records stored before <c>0030</c>.
/// </summary>
public sealed partial class PostgresExperienceRecordStore
{
    /// <summary>The signature table <c>0030</c> creates.</summary>
    internal const string FailureSignaturesTable = "agent_experience.experience_failure_signatures";

    /// <summary>
    /// The one write: every signature of one record, its scope columns copied from the record, which is read live and in
    /// exactly the caller's scope <c>FOR KEY SHARE</c> first, so an erasure that overlaps the write serialises with it
    /// (see <see cref="RecordKeyShareLock"/>). A signature the record already has is left as it is.
    /// </summary>
    internal const string InsertFailureSignaturesSql =
        "WITH signed_record AS MATERIALIZED (SELECT r.experience_id, r.tenant_id, r.application_id, r.project_id, " +
        "r.team_id, r.agent_id, r.user_id\n" +
        $"FROM {Table} r\n" +
        $"WHERE r.experience_id = @experience_id AND {RecordScopePredicate} AND {RecordLivePredicate}\n" +
        $"{RecordKeyShareLock})\n" +
        $"INSERT INTO {FailureSignaturesTable} (experience_id, signature_version, signature_key, tool_name, error_class, " +
        "fingerprint, resolved, tenant_id, application_id, project_id, team_id, agent_id, user_id)\n" +
        "SELECT r.experience_id, @signature_version, s.signature_key, s.tool_name, s.error_class, s.fingerprint, s.resolved, " +
        "r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id\n" +
        "FROM signed_record r CROSS JOIN unnest(@signature_keys, @tool_names, @error_classes, @fingerprints, @resolved) " +
        "AS s(signature_key, tool_name, error_class, fingerprint, resolved)\n" +
        "ON CONFLICT (experience_id, signature_version, signature_key) DO NOTHING";

    /// <summary>The backfill's worklist: live plaintext records, past the caller's cursor, in ID order, read whole.</summary>
    private const string PlaintextPageSql =
        $"SELECT {SelectColumns} FROM {Table} " +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND payload_version = 1 " +
        "AND (@start_after::uuid IS NULL OR experience_id > @start_after) " +
        "ORDER BY experience_id LIMIT @limit";

    /// <summary><see cref="PlaintextPageSql"/> over a scope and everything beneath it.</summary>
    private const string PlaintextSubtreePageSql =
        $"SELECT {SelectColumns} FROM {Table} " +
        $"WHERE {SubtreeScopePredicate} AND {LivePredicate} AND payload_version = 1 " +
        "AND (@start_after::uuid IS NULL OR experience_id > @start_after) " +
        "ORDER BY experience_id LIMIT @limit";

    private const string UndefinedTable = "42P01";

    private const string InsufficientPrivilege = "42501";

    private const string UndefinedColumn = "42703";

    private const string SignatureSavepoint = "experience_failure_signatures";

    /// <summary>Whether the connecting role may insert into the signature table, asked after a refusal.</summary>
    private const string MayInsertSignaturesSql =
        "SELECT pg_catalog.has_table_privilege('" + FailureSignaturesTable + "', 'INSERT')";

    /// <summary>
    /// Writes <paramref name="record"/>'s failure signatures inside the create's transaction, under a savepoint. Three
    /// conditions roll back to the savepoint and let the record be created without them, for the backfill to add later:
    /// the table does not exist yet (<c>42P01</c>), a column the statement reads does not (<c>42703</c>: a database not
    /// migrated to <c>0030</c>, or to the scripts it builds on), or the role has not been granted the table
    /// (<c>42501</c>, the privileges call not re-run). Each skip is reported as a <c>record.signature.write</c> operation
    /// with the outcome <c>Skipped</c> and the SQLSTATE, the storage adapter's one operator-visible channel.
    /// </summary>
    /// <remarks>
    /// <c>42501</c> is also what a row-level security policy's refusal raises. The two are told apart by asking the
    /// database, not by reading the (localizable) message: after rolling back to the savepoint, the role's
    /// <c>INSERT</c> privilege on the table is checked, and a refusal by a role that holds it -- a policy, or anything
    /// else -- is rethrown and fails the create. Any other failure fails the create too.
    /// </remarks>
    private static async Task WriteFailureSignaturesAsync(
        AuthorizedTransaction session,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        var signatures = ExperienceFailureSignatures.Of(record);
        if (signatures.Count == 0)
        {
            return;
        }

        await session.Transaction.SaveAsync(SignatureSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = session.CreateCommand(InsertFailureSignaturesSql);
            AddFailureSignatureParameters(command.Parameters, record.ExperienceId, record.Scope, signatures);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await session.Transaction.ReleaseAsync(SignatureSavepoint, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is UndefinedTable or UndefinedColumn or InsufficientPrivilege && !cancellationToken.IsCancellationRequested)
        {
            await session.Transaction.RollbackAsync(SignatureSavepoint, cancellationToken).ConfigureAwait(false);
            if (ex.SqlState == InsufficientPrivilege)
            {
                await using var check = session.CreateCommand(MayInsertSignaturesSql);
                if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    // The role may insert, so this was not a missing grant: a row-level security policy (or something
                    // else) refused the row, and hiding that would hide a misconfigured or tampered policy.
                    throw;
                }
            }

            ErasureDiagnostics.SignatureWriteSkipped(ex.SqlState);
        }
    }

    private static void AddFailureSignatureParameters(
        NpgsqlParameterCollection parameters,
        Guid experienceId,
        Scope scope,
        IReadOnlyList<ExperienceRecordFailureSignature> signatures)
    {
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(parameters, scope);
        parameters.Add(new NpgsqlParameter<int>("signature_version", ExperienceFailureSignature.SignatureVersion));
        parameters.Add(TextArray("signature_keys", signatures.Select(s => s.Signature.Key)));
        parameters.Add(TextArray("tool_names", signatures.Select(s => s.Signature.ToolName)));
        parameters.Add(TextArray("error_classes", signatures.Select(s => s.Signature.ErrorClass)));
        parameters.Add(TextArray("fingerprints", signatures.Select(s => s.Signature.Fingerprint)));
        parameters.Add(new NpgsqlParameter<bool[]>("resolved", NpgsqlDbType.Array | NpgsqlDbType.Boolean)
        {
            TypedValue = [.. signatures.Select(s => s.Resolution == ExperienceSignatureResolution.Resolved)],
        });

        static NpgsqlParameter<string[]> TextArray(string name, IEnumerable<string> values) =>
            new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. values] };
    }

    /// <summary>
    /// The failure-signature backfill, from the start of the worklist. See
    /// <see cref="BackfillFailureSignaturesAsync(AuthorizationContext, Scope, int, ScopeMatch, Guid?, CancellationToken)"/>.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to backfill, or the root of the subtree to backfill. Never treated as authority.</param>
    /// <param name="batchSize">The most records this call may examine, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to backfill <paramref name="scope"/> alone or everything beneath it too.</param>
    /// <param name="cancellationToken">Cancels the operation between records.</param>
    /// <returns>The batch's result; pass its <see cref="ExperienceFailureSignatureBackfillResult.ResumeAfter"/> to the next call.</returns>
    public Task<ExperienceFailureSignatureBackfillResult> BackfillFailureSignaturesAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken) =>
        BackfillFailureSignaturesAsync(authorization, scope, batchSize, match, startAfter: null, cancellationToken);

    /// <summary>
    /// The failure-signature backfill: examines up to <paramref name="batchSize"/> live plaintext records in
    /// <paramref name="scope"/> -- or, with <see cref="ScopeMatch.Subtree"/>, in that scope and every scope beneath it --
    /// in ID order after <paramref name="startAfter"/>, derives each one's failure signatures
    /// (<see cref="ExperienceFailureSignatures.Of"/>) and writes the ones it does not hold yet. Bounded, resumable,
    /// idempotent and authorized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A record stored before <c>0030</c> -- or while the application role was not yet granted its table, or by
    /// an instance still on an earlier build during a rolling deploy -- holds no signature rows, so no signature search
    /// finds it. Run this after upgrading: start with no cursor and pass each result's
    /// <see cref="ExperienceFailureSignatureBackfillResult.ResumeAfter"/> to the next call until
    /// <see cref="ExperienceFailureSignatureBackfillResult.MoreRemain"/> is <see langword="false"/>.
    /// </para>
    /// <para>
    /// <b>Sealed records are skipped.</b> The worklist is plaintext records only (<c>payload_version = 1</c>): the
    /// database cannot read a sealed payload, and this job does not open one. A record sealed before <c>0030</c> gains
    /// signatures only when it is finalized again; one sealed afterwards by the upgrade job keeps the rows it was written
    /// with. Records written in encrypted mode after <c>0030</c> carry their signatures from the start, in the clear,
    /// like the other search data.
    /// </para>
    /// <para>
    /// <b>What it does.</b> One page of the worklist is read whole; then, record by record, one statement locks the
    /// record <c>FOR KEY SHARE</c>, re-checks that it is still live, and inserts its signatures, leaving any it already
    /// holds as they are. A record with no failed tool call writes nothing. A record whose payload cannot be decoded is
    /// left alone and counted in <see cref="ExperienceFailureSignatureBackfillResult.SkippedCount"/>; the cursor moves
    /// past it.
    /// </para>
    /// <para>
    /// <b>Who runs it.</b> It is an upgrade job, normally run by the owner after migrating, over the owner's data source;
    /// it needs only <c>SELECT</c> on the records and <c>INSERT</c> on the signature table, which the application role
    /// also holds once the privileges call has been re-run. <paramref name="authorization"/> must permit
    /// <paramref name="scope"/>, which covers its subtree exactly as it does for <see cref="SealPlaintextRecordsAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to backfill, or the root of the subtree to backfill. Never treated as authority.</param>
    /// <param name="batchSize">The most records this call may examine, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to backfill <paramref name="scope"/> alone or everything beneath it too.</param>
    /// <param name="startAfter">The previous call's <see cref="ExperienceFailureSignatureBackfillResult.ResumeAfter"/>, or <see langword="null"/> to start from the beginning.</param>
    /// <param name="cancellationToken">Cancels the operation between records.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Committed"/> when the batch ran (possibly writing nothing),
    /// <see cref="ExperienceStoreOutcome.Invalid"/> or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public async Task<ExperienceFailureSignatureBackfillResult> BackfillFailureSignaturesAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        Guid? startAfter,
        CancellationToken cancellationToken)
    {
        // Counted like the other upgrade jobs: a count and how wide it reached -- never which records or signatures.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.SignatureBackfill);
        ErasureDiagnostics.TagScopeMatch(operation, match);

        ExperienceFailureSignatureBackfillResult result;
        try
        {
            result = await BackfillFailureSignaturesCoreAsync(authorization, scope, batchSize, match, startAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.BackfilledCountAttribute, result.RecordCount);
        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    private async Task<ExperienceFailureSignatureBackfillResult> BackfillFailureSignaturesCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        int batchSize,
        ScopeMatch match,
        Guid? startAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateSignatureBackfill(scope, batchSize, match);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, 0, 0, false, startAfter, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, 0, 0, false, startAfter, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            List<SnapshotRow> rows;
            await using (var page = await ExperienceSessionContext.BeginAsync(connection, authorization, cancellationToken).ConfigureAwait(false))
            {
                await using (var command = new NpgsqlCommand(
                    match == ScopeMatch.Subtree ? PlaintextSubtreePageSql : PlaintextPageSql, connection, page))
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
            var recordCount = 0;
            var signatureCount = 0;
            var skippedCount = 0;
            foreach (var row in examined)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candidateScope = ReadRecordScope(row);

                // Defence in depth over the page, exactly as the sealing job does it.
                if (!IsAtOrBeneath(candidateScope, scope, match) || !authorization.Permits(candidateScope))
                {
                    throw new ExperienceStoreException(
                        "A failure-signature backfill candidate lay outside the requested scope or authorization; the job stopped without writing it.");
                }

                IReadOnlyList<ExperienceRecordFailureSignature> signatures;
                try
                {
                    signatures = ExperienceFailureSignatures.Of(ReadRecord(row));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A payload this adapter cannot decode is left without signatures; it never stops the batch.
                    skippedCount++;
                    continue;
                }

                if (signatures.Count == 0)
                {
                    continue;
                }

                await using var transaction = await ExperienceSessionContext
                    .BeginAsync(connection, authorization, cancellationToken, System.Data.IsolationLevel.ReadCommitted).ConfigureAwait(false);
                int written;
                await using (var insert = new NpgsqlCommand(InsertFailureSignaturesSql, connection, transaction))
                {
                    AddFailureSignatureParameters(insert.Parameters, row.GetGuid(0), candidateScope, signatures);
                    written = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                signatureCount += written;
                recordCount += written > 0 ? 1 : 0;
            }

            var resumeAfter = examined.Count > 0 ? examined[^1].GetGuid(0) : startAfter;
            return new(ExperienceStoreOutcome.Committed, recordCount, signatureCount, skippedCount, rows.Count > batchSize, resumeAfter, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "failure signature backfill", cancellationToken);
        }
    }
}
