using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.ExperienceRecordParameters;
using static AgentExperience.Storage.Postgres.ExperienceRecordRows;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;
using static AgentExperience.Storage.Postgres.ExperienceStoreFailures;

namespace AgentExperience.Storage.Postgres;

// The lifecycle commit: one event appended and the record's projection moved in one transaction, keyed by the
// event ID for idempotency and by the expected revision for concurrency, with the confidence-evidence ledger.
public sealed partial class PostgresExperienceRecordStore
{
    private const string InsertEventSql =
        $"INSERT INTO {EventsTable} ({EventColumns}) VALUES (@event_id, @experience_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @prior_status, @current_status, @reason, @producer, " +
        "@occurred_at, @recorded_at, @expected_revision, @applied_revision, @replacement_experience_id, @actor, " +
        "@confidence_evidence_id, @confidence_kind, @confidence_source, @confidence_run_id, " +
        "@confidence_verification_round_id, @confidence_reviewer_identity, @confidence_rule_version, " +
        "@confidence_detail, @prior_reuse_confidence, @new_reuse_confidence, @prior_supporting_validations, " +
        "@new_supporting_validations, @prior_contradictions, @new_contradictions, @confidence_assessment_id, " +
        "@confidence_admission)";

    /// <summary>The primary key a resubmitted <see cref="LifecycleEvent.EventId"/> violates.</summary>
    private const string EventPrimaryKey = "lifecycle_events_pkey";

    /// <summary>The unique index a second event claiming an already-taken record revision violates.</summary>
    private const string EventRevisionIndex = "ix_lifecycle_events_record_revision";

    /// <summary>The evidence ledger. Created by <c>0007_confidence_evidence.sql</c>.</summary>
    private const string EvidenceTable = "agent_experience.confidence_evidence";

    /// <summary>
    /// The evidence row's own columns. <c>independence_key</c> is deliberately absent: it is a generated
    /// column the database derives from <c>source</c>, <c>run_id</c>, <c>verification_round_id</c>, and
    /// <c>reviewer_identity</c>, precisely so no writer -- this one included -- can choose it.
    /// </summary>
    private const string EvidenceColumns =
        "evidence_id, experience_id, event_id, kind, source, run_id, verification_round_id, " +
        "reviewer_identity, counted, actor, rule_version, detail, recorded_at, applied_revision, applied_status, " +
        "prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, new_supporting_validations, " +
        "prior_contradictions, new_contradictions, assessment_id, admission";

    private const string InsertEvidenceSql =
        $"INSERT INTO {EvidenceTable} ({EvidenceColumns}) VALUES (@evidence_id, @experience_id, @event_id, " +
        "@confidence_kind, @confidence_source, @confidence_run_id, @confidence_verification_round_id, " +
        "@confidence_reviewer_identity, @counted, @actor, @confidence_rule_version, @confidence_detail, " +
        "@recorded_at, @applied_revision, @applied_status, @prior_reuse_confidence, @new_reuse_confidence, " +
        "@prior_supporting_validations, @new_supporting_validations, @prior_contradictions, @new_contradictions, " +
        "@confidence_assessment_id, @confidence_admission)";

    /// <summary>The primary key a resubmitted <see cref="ConfidenceUpdate.EvidenceId"/> violates.</summary>
    private const string EvidencePrimaryKey = "confidence_evidence_pkey";

    /// <summary>
    /// The unique index that spends an assessment: one piece of evidence per record per
    /// <see cref="ConfidenceUpdate.AssessmentId"/>. Created by <c>0015</c>. Violating it with a new evidence
    /// ID means the assessment token was replayed.
    /// </summary>
    private const string EvidenceAssessmentIndex = "ux_confidence_evidence_assessment";

    /// <summary>
    /// Whether an evidence ID is already in the ledger for a live record in this exact scope. Scoped like
    /// <see cref="SelectEvidenceSql"/>, so another scope's evidence reads as "not taken" and the answer
    /// reveals nothing about it.
    /// </summary>
    private const string EvidenceIdTakenSql =
        $"SELECT EXISTS (SELECT 1 FROM {EvidenceTable} ev JOIN {Table} r ON r.experience_id = ev.experience_id " +
        $"WHERE ev.evidence_id = @evidence_id AND {RecordScopePredicate} AND {RecordLivePredicate})";

    /// <summary>
    /// The partial unique index that decides independence. Violating it means this observation has
    /// already been counted for this record, which is not a failure: the submission is stored anyway,
    /// with <c>counted = false</c>, and the counters stay where they are.
    /// </summary>
    private const string EvidenceIndependenceIndex = "ux_confidence_evidence_independence";

    /// <summary>
    /// Whether another row in the ledger already holds this row's independence key for the same record as counted
    /// evidence or as recorded-only evidence that rode an event -- every counted row has an event, so both
    /// are "has an event", which <c>0023</c>'s partial index serves. Asked only for a recorded-only submission, right
    /// after its own row went in under the savepoint: the partial unique index cannot answer it, because neither row
    /// is counted. Two such submissions racing from one revision cannot both commit, because
    /// both events claim the same applied revision.
    /// </summary>
    private const string RecordedOnlyKeyTakenSql =
        $"SELECT EXISTS (SELECT 1 FROM {EvidenceTable} mine JOIN {EvidenceTable} other " +
        "ON other.experience_id = mine.experience_id AND other.independence_key = mine.independence_key " +
        "AND other.evidence_id <> mine.evidence_id " +
        "WHERE mine.evidence_id = @evidence_id AND other.event_id IS NOT NULL)";

    /// <summary>
    /// The savepoint the first evidence insert runs under, so a taken independence key costs only that
    /// statement rather than the whole transaction. Without it the unique violation would abort the
    /// commit that is supposed to record the duplicate.
    /// </summary>
    private const string EvidenceSavepoint = "confidence_evidence_attempt";

    /// <summary>
    /// One resubmitted evidence ID, read back whole from the ledger row itself, which carries every number
    /// a replay has to report so the answer describes one moment rather than one value from here and
    /// another from a later read.
    /// <para>
    /// The join to the record is not for data -- nothing is selected from it. It is there to carry
    /// <see cref="RecordScopePredicate"/>, so an evidence ID that belongs to another scope reads back as
    /// no row at all. Without it a guessed ID would hand a caller another tenant's scores, counters, and
    /// revision: the primary key is global, and this is the one statement that looks a row up by it alone.
    /// </para>
    /// </summary>
    private const string SelectEvidenceSql =
        "SELECT ev.experience_id, ev.event_id, ev.kind, ev.source, ev.run_id, ev.verification_round_id, " +
        "ev.reviewer_identity, ev.counted, ev.applied_revision, ev.applied_status, ev.rule_version, ev.detail, " +
        "ev.prior_reuse_confidence, ev.new_reuse_confidence, ev.prior_supporting_validations, " +
        "ev.new_supporting_validations, ev.prior_contradictions, ev.new_contradictions, ev.assessment_id, ev.admission " +
        $"FROM {EvidenceTable} ev JOIN {Table} r ON r.experience_id = ev.experience_id " +
        $"WHERE ev.evidence_id = @evidence_id AND {RecordScopePredicate} AND {RecordLivePredicate}";

    /// <summary>
    /// The record's revision and status, locked for the rest of the transaction. Used only on the
    /// duplicate path, which writes no projection update and therefore has no revision-guarded statement
    /// of its own to hold the row still while it records what the record currently looks like.
    /// </summary>
    private const string LockRevisionAndStatusSql = SelectRevisionAndStatusSql + " FOR UPDATE";

    /// <summary>
    /// The revision guard, the prior-status guard, and the scope predicate live in the same statement,
    /// so a stale revision, a prior status the record is not in, and a foreign scope are all "no row
    /// updated" and none of them can overwrite state it does not own.
    /// <para>
    /// A <see langword="null"/> <c>@prior_status</c> does <em>not</em> skip the status match: it falls
    /// back to <c>@current_status</c>, so a record's first event may only record the status the record
    /// is already in. Skipping the match -- which this statement used to do -- let a caller move a
    /// record from any status to any other simply by omitting the prior status, which is precisely what
    /// Core's transition table exists to prevent.
    /// </para>
    /// </summary>
    private const string UpdateProjectionSetSql =
        $"UPDATE {Table} SET status = @current_status, revision = @applied_revision, updated_at = @recorded_at";

    /// <summary>
    /// The three columns a counted confidence update moves, written in the same statement as the status
    /// and the revision -- which is what satisfies the database's own projection guard, and what makes
    /// "the counters moved" and "the event that says so was appended" one fact rather than two.
    /// Every value is one the event carried: this statement reads nothing and derives nothing.
    /// </summary>
    private const string UpdateProjectionConfidenceSetSql =
        ", reuse_confidence = @new_reuse_confidence, supporting_validations = @new_supporting_validations, " +
        "contradictions = @new_contradictions";

    /// <summary>
    /// The guards above plus "and this record has not been erased". A tombstone is terminal: a late
    /// commit against one matches no row here and is reported as
    /// <see cref="ExperienceStoreOutcome.Deleted"/> after the same re-read that tells a stale revision
    /// from a missing record. The database refuses it a second time from its own side -- <c>0010</c>'s
    /// projection guard rejects every UPDATE of a tombstone -- so neither this adapter nor a writer
    /// bypassing it can move one.
    /// <para>
    /// The tombstone term here is redundant and kept on purpose: <c>status = COALESCE(...)</c> compares
    /// against an <see cref="ExperienceStatus"/> member's name, and a tombstone's status is a literal no
    /// member has, so this statement could never match one anyway. It is defence in depth against a
    /// future status whose name collides, and it is named as redundant rather than counted as the thing
    /// that makes late commits safe -- the re-read below, and <c>0010</c>'s projection guard, are.
    /// </para>
    /// </summary>
    private const string UpdateProjectionWhereSql =
        " WHERE experience_id = @experience_id AND revision = @expected_revision " +
        $"AND status = COALESCE(@prior_status, @current_status) AND {ScopePredicate} AND {LivePredicate}";

    private const string UpdateProjectionSql = UpdateProjectionSetSql + UpdateProjectionWhereSql;

    /// <summary>
    /// The projection update for host-trusted evidence recorded only: the status (which the validator
    /// has already pinned to the prior one) and the revision, and deliberately not <c>updated_at</c>, which
    /// retrieval's recency and <c>MaxAge</c> read -- evidence that must not steer the record must not keep it recent.
    /// </summary>
    private const string UpdateProjectionRecordedOnlySql =
        $"UPDATE {Table} SET status = @current_status, revision = @applied_revision" + UpdateProjectionWhereSql;

    private const string UpdateProjectionWithConfidenceSql =
        UpdateProjectionSetSql + UpdateProjectionConfidenceSetSql + UpdateProjectionWhereSql;

    /// <summary>
    /// The record's revision, status, and tombstone marker within exactly this scope. <c>deleted_at</c>
    /// is selected rather than filtered on, because this read is what turns "the guarded UPDATE matched
    /// no row" into a reason, and "erased" is one of the reasons. The status of a tombstone is a literal
    /// this library's enum has no member for, so it is only ever decoded when <c>deleted_at</c> is null.
    /// </summary>
    private const string SelectRevisionAndStatusSql =
        $"SELECT revision, status, {DeletedAtAlias} FROM {Table} WHERE experience_id = @experience_id AND {ScopePredicate}";

    private const string SelectEventSql = $"SELECT {EventColumns} FROM {EventsTable} WHERE event_id = @event_id";

    /// <summary>
    /// Locks both the record being superseded and its proposed replacement, in a deterministic order so
    /// two supersessions naming each other cannot deadlock. Held for the rest of the commit transaction,
    /// which is what makes the replacement checks below atomic with the write: a concurrent transition
    /// of the replacement either lands before this lock (and is therefore seen by the check) or blocks
    /// behind it (and is therefore decided against a record this commit has already moved).
    /// </summary>
    private const string LockSupersessionRowsSql =
        $"SELECT experience_id FROM {Table} WHERE experience_id = ANY(@lock_ids) ORDER BY experience_id FOR UPDATE";

    /// <inheritdoc />
    public async Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        var errors = ExperienceRecordValidator.ValidateLifecycleEvent(scope, lifecycleEvent, authorization);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Both timestamps are truncated the same way the record's columns are, so a replay's stored
        // OccurredAt compares equal to the value the caller resubmits.
        var occurredAt = ToStoredTimestamp(lifecycleEvent.OccurredAt);
        var recordedAt = ToStoredTimestamp(_timeProvider.GetUtcNow());
        var appliedRevision = lifecycleEvent.ExpectedRevision + 1;

        // Encrypted mode: the event's reason and any confidence detail are sealed under the record's key. A
        // destroyed key means the record is erased, or mid-erasure (its key is gone and its tombstone not yet
        // written): a tombstone is terminal, so nothing is appended.
        using var key = _encryption is null
            ? null
            : await _encryption.ForWriteAsync(lifecycleEvent.ExperienceRecordId, scope, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            if (_encryption is not null && key is null)
            {
                return await ShreddedCommitOutcomeAsync(connection, authorization, scope, lifecycleEvent.ExperienceRecordId, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Pinned, not inherited: under REPEATABLE READ or SERIALIZABLE the same-revision race would
            // abort with a serialization failure instead of matching no row, turning an expected stale
            // revision into an infrastructure failure.
            await using var transaction = await ExperienceSessionContext
                .BeginAsync(connection, authorization, cancellationToken, System.Data.IsolationLevel.ReadCommitted).ConfigureAwait(false);

            // The evidence goes in first, because whether its independence key was free decides whether
            // there is anything else to write at all. An event is append-only once written, so it cannot
            // be corrected afterwards to say the counters did not move after all.
            ConfidenceUpdate? storedConfidence = null;
            if (lifecycleEvent.Confidence is { } submitted)
            {
                var applied = await InsertEvidenceAsync(
                    connection, transaction, scope, Actor(authorization), lifecycleEvent, submitted, recordedAt,
                    appliedRevision, key, cancellationToken)
                    .ConfigureAwait(false);

                if (applied.Settled is { } settled)
                {
                    // Either a resubmitted evidence ID, which writes nothing and reports the original
                    // outcome, or a duplicate independence key, whose ledger row is the whole of what this
                    // call writes. A duplicate that also moved the status, the revision, or updated_at
                    // would let one observation, replayed under fresh evidence IDs, keep a record
                    // permanently recent -- and would contest a record on evidence already counted.
                    if (settled.Commit)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    return settled.Result;
                }

                storedConfidence = applied.Stored;
            }

            var eventToStore = storedConfidence is null
                ? lifecycleEvent
                : lifecycleEvent with { Confidence = storedConfidence };

            try
            {
                await using var insert = new NpgsqlCommand(InsertEventSql, connection, transaction);
                AddEventParameters(insert.Parameters, authorization, scope, eventToStore, occurredAt, recordedAt, appliedRevision, key);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, EventPrimaryKey, cancellationToken))
            {
                // A resubmitted event ID. PostgreSQL has aborted the transaction, so nothing this call
                // attempted survives; the stored row then decides replay from conflict.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await ExperienceSessionContext.RunAsync(
                    connection,
                    authorization,
                    reread => CompareStoredEventAsync(connection, reread, scope, lifecycleEvent, occurredAt, key, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, EventRevisionIndex, cancellationToken))
            {
                // A different event already claimed this record revision. The unique index makes the
                // loser of a same-revision race block here and fail once the winner commits, which is a
                // stale revision by another name.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await ExperienceSessionContext.RunAsync(
                    connection,
                    authorization,
                    reread => StaleOrMissingAsync(connection, reread, scope, lifecycleEvent.ExperienceRecordId, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            // Deliberately after the insert, so a replay never reaches it: retrying a committed
            // supersession must report the original outcome even once the replacement has itself moved
            // on, which is exactly the retry a lost acknowledgement calls for.
            if (lifecycleEvent.ReplacementExperienceId is { } replacementId
                && await CheckReplacementInTransactionAsync(
                    connection, transaction, scope, lifecycleEvent.ExperienceRecordId, replacementId, cancellationToken)
                    .ConfigureAwait(false) is { } refusal)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return refusal;
            }

            // Only a counted update writes the three confidence columns. A duplicate submission takes the
            // statement that leaves them alone, so "the counters did not move" is a fact about the SQL
            // that ran, not a value that happened to be equal.
            var counted = storedConfidence is { Counted: true };
            var recordedOnly = storedConfidence is { Counted: false };

            int updated;
            try
            {
                await using var update = new NpgsqlCommand(
                    counted ? UpdateProjectionWithConfidenceSql : recordedOnly ? UpdateProjectionRecordedOnlySql : UpdateProjectionSql,
                    connection,
                    transaction);
                var parameters = update.Parameters;
                parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
                parameters.Add(new NpgsqlParameter<string>("current_status", lifecycleEvent.CurrentStatus.ToString()));
                parameters.Add(NullableText("prior_status", lifecycleEvent.PriorStatus?.ToString()));
                parameters.Add(new NpgsqlParameter<long>("expected_revision", lifecycleEvent.ExpectedRevision));
                parameters.Add(new NpgsqlParameter<long>("applied_revision", appliedRevision));
                if (!recordedOnly)
                {
                    parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
                }
                if (counted)
                {
                    parameters.Add(new NpgsqlParameter<double>("new_reuse_confidence", storedConfidence!.NewReuseConfidence));
                    parameters.Add(new NpgsqlParameter<int>("new_supporting_validations", storedConfidence.NewSupportingValidations));
                    parameters.Add(new NpgsqlParameter<int>("new_contradictions", storedConfidence.NewContradictions));
                }

                AddScopeParameters(parameters, scope);
                updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (!cancellationToken.IsCancellationRequested
                && ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                // Another writer got there first. However the server is configured, losing that race is an
                // expected condition, not an infrastructure failure.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await ExperienceSessionContext.RunAsync(
                    connection,
                    authorization,
                    reread => StaleOrMissingAsync(connection, reread, scope, lifecycleEvent.ExperienceRecordId, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                // The record is not in this scope, its revision has moved on, or it is not in the status
                // the event was decided against. The row is re-read inside the same transaction that is
                // about to be rolled back, so the event insert above never reaches the log.
                var current = await ReadRevisionAndStatusAsync(connection, transaction, scope, lifecycleEvent.ExperienceRecordId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                if (current is not { } record)
                {
                    return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
                }

                if (record.Deleted)
                {
                    // The record was erased. A tombstone is terminal, so this is not a race to retry:
                    // the event this call appended is rolled back with everything else.
                    return new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors);
                }

                return record.Revision != lifecycleEvent.ExpectedRevision
                    ? new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors)
                    // Scope and revision both matched, so the prior-status guard is what rejected it.
                    : new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors, storedConfidence);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            // Nothing this call wrote is visible unless the commit itself succeeded and only its
            // acknowledgement was lost; retrying the identical event then replays instead of reapplying.
            throw Translate(ex, "lifecycle commit", cancellationToken);
        }
    }

    /// <summary>
    /// Re-decides the replacement rules inside the commit transaction, with both record rows locked, and
    /// returns the refusal when they no longer hold. <see langword="null"/> means the supersession may
    /// proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the authoritative check, not a second opinion.
    /// <see cref="CheckSupersessionAsync"/> answers the same question on its own connection, which makes
    /// it useful for telling a caller <em>why</em> before it tries -- but an answer read outside this
    /// transaction is only a prediction. Two supersessions naming each other ("A by B" and "B by A")
    /// each pass such a prediction and would both commit the cycle the contract refuses. Running the
    /// check here, after locking both rows in a deterministic order, is what makes "a cycle is refused"
    /// and "an ineligible replacement is refused" true under concurrency: the loser either sees the
    /// winner's event or waits for it.
    /// </para>
    /// <para>
    /// Eligibility is read from <see cref="ExperienceStatuses.EligibleForReuse"/> rather than decided
    /// here, so the rule the transaction enforces is the same list retrieval and indexing apply.
    /// </para>
    /// </remarks>
    private static async Task<ExperienceLifecycleCommitResult?> CheckReplacementInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        Guid experienceId,
        Guid replacementId,
        CancellationToken cancellationToken)
    {
        await using (var locks = new NpgsqlCommand(LockSupersessionRowsSql, connection, transaction))
        {
            locks.Parameters.Add(new NpgsqlParameter<Guid[]>("lock_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = [experienceId, replacementId],
            });
            await locks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var check = await ReadSupersessionAsync(connection, transaction, scope, experienceId, replacementId, cancellationToken)
            .ConfigureAwait(false);

        // The record itself is left to the projection update, which reports NotFound in the one way every
        // other operation does.
        if (check.Outcome is ExperienceSupersessionOutcome.RecordNotFound or ExperienceSupersessionOutcome.Allowed
            && check.ReplacementStatus is { } status
            && ExperienceStatuses.IsEligibleForReuse(status))
        {
            return null;
        }

        return new(ExperienceStoreOutcome.ReplacementNotAllowed, 0, check.ReplacementStatus, NoErrors);
    }

    /// <summary>
    /// Decides a resubmitted <see cref="LifecycleEvent.EventId"/>: byte-for-byte the same event (scope
    /// included) is the original commit replayed, so its original outcome is returned and nothing is
    /// written; any difference is a <see cref="ExperienceStoreOutcome.Conflict"/>. The comparison is
    /// identical whichever scope owns the stored event, so it reveals no event data.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> CompareStoredEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        RecordKey? key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectEventSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", lifecycleEvent.EventId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Events are never deleted, so the row that just collided cannot vanish. Treat the
            // impossible case as a conflict rather than writing anything.
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        // An event ID taken by another record, or in another scope, is a conflict -- decided before anything
        // is opened, because that row's sealed text is bound to a different key and would (correctly) fail
        // this record's authentication.
        var storedScope = ReadEventScope(reader);
        if (reader.GetGuid(1) != lifecycleEvent.ExperienceRecordId || storedScope != scope)
        {
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        var stored = ReadEvent(reader, key);
        var appliedRevision = stored.AppliedRevision;

        // Record equality compares every field of the event -- the replacement ID included, so a replay
        // that names a different replacement is a conflict rather than a silent no-op. The scope is
        // compared alongside it. The revision reported is the one the original commit produced, not the
        // record's current one.
        // The confidence payload's admission is the one field left out, exactly as on the evidence ledger's
        // replay: a replay reports the admission the original was stored with, whatever mode resubmits it.
        var resubmitted = lifecycleEvent with
        {
            OccurredAt = occurredAt,
            Confidence = lifecycleEvent.Confidence is { } submittedConfidence
                ? ExperienceRecordValidator.AsReplayOf(submittedConfidence, stored.Event.Confidence)
                : null,
        };
        if (ExperienceRecordValidator.IsSameEvidence(lifecycleEvent.Confidence, stored.Event.Confidence))
        {
            // The status follows from whether the evidence was counted, which a host's HostTrustedEvidence setting
            // decides; a genuine replay after that setting changed is still the same event.
            resubmitted = resubmitted with { CurrentStatus = stored.Event.CurrentStatus };
        }
        return stored.Event == resubmitted && storedScope == scope
            ? new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors, stored.Event.Confidence)
            : new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
    }

    /// <summary>
    /// Writes the evidence row, and decides -- from the database, inside the commit transaction -- which
    /// of the three things this submission is: the first for its independence key, a later one for a key
    /// already counted, or a resubmission of an evidence ID that is already stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first insert claims the key by writing <c>counted = true</c>, which the partial unique index
    /// admits exactly once per record and key. It runs under a savepoint because losing that race is an
    /// <em>expected</em> outcome that the commit has to survive: a unique violation aborts the whole
    /// transaction otherwise, and the transaction is what is supposed to record the duplicate.
    /// </para>
    /// <para>
    /// On the violation the statement is undone and the same submission is written again with
    /// <c>counted = false</c> -- and that row is <em>all</em> this call writes. No event, no counters, no
    /// status, no revision, no <c>updated_at</c>. Each of those would be a way for one observation,
    /// replayed under fresh evidence IDs, to keep changing a record the independence rule has already
    /// declared it finished with: refreshing <c>updated_at</c> would keep it permanently recent for
    /// ranking and permanently un-expired, and writing the status would contest it on evidence that was
    /// not counted. An event is impossible as well as unwanted -- it must claim
    /// <c>expected_revision + 1</c>, and claiming a revision the record never reaches would wedge every
    /// later commit against the unique index on <c>(experience_id, applied_revision)</c>.
    /// </para>
    /// <para>
    /// Because that path writes no revision-guarded statement of its own, it re-reads the record
    /// <c>FOR UPDATE</c> first: the row it records has to say what the record actually looks like, and the
    /// usual refusals (gone, moved on, not in this status) still have to be reported rather than silently
    /// recorded against stale values.
    /// </para>
    /// <para>
    /// Whether this store's own reading of the key agrees with the database's is never asked: the key is
    /// a generated column, so the only writer who decides it is the database.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <c>Stored</c> is the payload the event must record, and is <see langword="null"/> when
    /// <c>Settled</c> is set. <c>Settled</c> is the outcome to return instead of writing an event and a
    /// projection: <c>Commit</c> says whether the transaction holds a ledger row worth keeping (a
    /// duplicate) or nothing at all (a resubmitted evidence ID, or a refusal).
    /// </returns>
    private static async Task<(ConfidenceUpdate? Stored, (ExperienceLifecycleCommitResult Result, bool Commit)? Settled)> InsertEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        string? actor,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        DateTimeOffset recordedAt,
        long appliedRevision,
        RecordKey? key,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync($"SAVEPOINT {EvidenceSavepoint}", cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertOneAsync(submitted, lifecycleEvent.EventId, appliedRevision, lifecycleEvent.CurrentStatus)
                .ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsViolationOf(ex, EvidenceIndependenceIndex, cancellationToken))
        {
            await ExecuteAsync($"ROLLBACK TO SAVEPOINT {EvidenceSavepoint}", CancellationToken.None).ConfigureAwait(false);
            return (null, await RecordDuplicateAsync().ConfigureAwait(false));
        }
        catch (PostgresException ex) when (IsViolationOf(ex, EvidencePrimaryKey, cancellationToken))
        {
            return (null, (await ReplayEvidenceAsync().ConfigureAwait(false), Commit: false));
        }
        catch (PostgresException ex) when (IsViolationOf(ex, EvidenceAssessmentIndex, cancellationToken))
        {
            return (null, (await ReplayOrSpentAsync().ConfigureAwait(false), Commit: false));
        }

        if (!submitted.Counted)
        {
            // Host-trusted evidence recorded only (story 17.3): its row is not counted, so the partial unique index
            // never refuses it. A key counted evidence or an earlier recorded-only event already holds makes it a
            // duplicate instead -- a ledger row and nothing else -- exactly as a counted submission would be.
            bool keyTaken;
            await using (var probe = new NpgsqlCommand(RecordedOnlyKeyTakenSql, connection, transaction))
            {
                probe.Parameters.Add(new NpgsqlParameter<Guid>("evidence_id", submitted.EvidenceId));
                keyTaken = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
            }

            if (keyTaken)
            {
                await ExecuteAsync($"ROLLBACK TO SAVEPOINT {EvidenceSavepoint}", CancellationToken.None).ConfigureAwait(false);
                return (null, await RecordDuplicateAsync().ConfigureAwait(false));
            }
        }

        await ExecuteAsync($"RELEASE SAVEPOINT {EvidenceSavepoint}", cancellationToken).ConfigureAwait(false);
        return (submitted, null);

        async Task ExecuteAsync(string sql, CancellationToken token)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        async Task InsertOneAsync(ConfidenceUpdate update, Guid? eventId, long revision, ExperienceStatus status)
        {
            await using var insert = new NpgsqlCommand(InsertEvidenceSql, connection, transaction);
            var parameters = insert.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("evidence_id", update.EvidenceId));
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
            parameters.Add(NullableUuid("event_id", eventId));
            parameters.Add(new NpgsqlParameter<string>("confidence_kind", update.Kind.ToString()));
            parameters.Add(new NpgsqlParameter<string>("confidence_source", update.Source.ToString()));
            parameters.Add(new NpgsqlParameter<Guid>("confidence_run_id", update.RunId));
            parameters.Add(NullableUuid("confidence_verification_round_id", update.VerificationRoundId));
            parameters.Add(NullableText("confidence_reviewer_identity", update.ReviewerIdentity));
            parameters.Add(new NpgsqlParameter<bool>("counted", update.Counted));
            parameters.Add(NullableText("actor", actor));
            parameters.Add(new NpgsqlParameter<string>("confidence_rule_version", update.RuleVersion));
            parameters.Add(NullableText(
                "confidence_detail",
                update.Detail is { } detail && key is not null ? key.Seal(SealedText.EvidenceDetailColumn, update.EvidenceId, detail) : update.Detail));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
            parameters.Add(new NpgsqlParameter<long>("applied_revision", revision));
            parameters.Add(new NpgsqlParameter<string>("applied_status", status.ToString()));
            parameters.Add(new NpgsqlParameter<double>("prior_reuse_confidence", update.PriorReuseConfidence));
            parameters.Add(new NpgsqlParameter<double>("new_reuse_confidence", update.NewReuseConfidence));
            parameters.Add(new NpgsqlParameter<int>("prior_supporting_validations", update.PriorSupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("new_supporting_validations", update.NewSupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("prior_contradictions", update.PriorContradictions));
            parameters.Add(new NpgsqlParameter<int>("new_contradictions", update.NewContradictions));
            parameters.Add(NullableUuid("confidence_assessment_id", update.AssessmentId));
            parameters.Add(NullableText("confidence_admission", update.Admission?.ToString()));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task<(ExperienceLifecycleCommitResult Result, bool Commit)> RecordDuplicateAsync()
        {
            var current = await ReadRevisionAndStatusAsync(
                connection, transaction, scope, lifecycleEvent.ExperienceRecordId, cancellationToken, forUpdate: true)
                .ConfigureAwait(false);

            if (current is not { } record)
            {
                return (new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors), Commit: false);
            }

            if (record.Deleted)
            {
                // Nothing is recorded against a tombstone -- not even a duplicate submission's ledger
                // row, which would put the erased record's ID back into a table the erasure emptied.
                return (new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors), Commit: false);
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return (new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors), Commit: false);
            }

            if (lifecycleEvent.PriorStatus is { } prior && record.Status != prior)
            {
                return (new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors), Commit: false);
            }

            var recordedOnly = submitted.AsRecordedOnly();
            try
            {
                // Not a tombstone, so the status decoded: the branch above returned for the one case
                // where it could not.
                await InsertOneAsync(recordedOnly, eventId: null, record.Revision, record.Status!.Value).ConfigureAwait(false);
            }
            catch (PostgresException pk) when (IsViolationOf(pk, EvidencePrimaryKey, cancellationToken))
            {
                return (await ReplayEvidenceAsync().ConfigureAwait(false), Commit: false);
            }
            catch (PostgresException spent) when (IsViolationOf(spent, EvidenceAssessmentIndex, cancellationToken))
            {
                return (await ReplayOrSpentAsync().ConfigureAwait(false), Commit: false);
            }

            await ExecuteAsync($"RELEASE SAVEPOINT {EvidenceSavepoint}", cancellationToken).ConfigureAwait(false);

            // The record is untouched, so its revision and status are reported exactly as they were read.
            return (
                new(ExperienceStoreOutcome.Committed, record.Revision, record.Status, NoErrors, recordedOnly),
                Commit: true);
        }

        async Task<ExperienceLifecycleCommitResult> ReplayOrSpentAsync()
        {
            // Which unique index PostgreSQL reports first when a statement violates several is not a
            // promise, so an identical retry -- which also re-presents its own, already stored assessment
            // -- can surface here rather than on the primary key. It is still a replay: the stored row
            // under this evidence ID decides it exactly as the primary-key path would. Only when no row
            // holds this evidence ID was the assessment spent by *other* evidence, which is a replayed
            // token: refused, and nothing written.
            await ExecuteAsync($"ROLLBACK TO SAVEPOINT {EvidenceSavepoint}", CancellationToken.None).ConfigureAwait(false);

            bool taken;
            await using (var probe = new NpgsqlCommand(EvidenceIdTakenSql, connection, transaction))
            {
                probe.Parameters.Add(new NpgsqlParameter<Guid>("evidence_id", submitted.EvidenceId));
                AddScopeParameters(probe.Parameters, scope);
                taken = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
            }

            if (taken)
            {
                return await CompareStoredEvidenceAsync(connection, transaction, scope, lifecycleEvent, submitted, key, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new(
                ExperienceStoreOutcome.Conflict,
                0,
                null,
                [new StoreValidationError(
                    ConfidenceUpdate.AssessmentIdPath,
                    "this assessment has already landed evidence for this record under another evidence ID.")]);
        }

        async Task<ExperienceLifecycleCommitResult> ReplayEvidenceAsync()
        {
            // The failed statement has left the transaction unusable; undoing it to the savepoint makes
            // the connection readable again so the stored row can be compared. The caller rolls the
            // whole transaction back afterwards, so nothing this call attempted survives either way.
            await ExecuteAsync($"ROLLBACK TO SAVEPOINT {EvidenceSavepoint}", CancellationToken.None).ConfigureAwait(false);
            return await CompareStoredEvidenceAsync(connection, transaction, scope, lifecycleEvent, submitted, key, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decides a resubmitted <see cref="ConfidenceUpdate.EvidenceId"/>: the same evidence about the same
    /// observation is the original submission replayed, so its original outcome is returned and nothing
    /// is written; anything else is a <see cref="ExperienceStoreOutcome.Conflict"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is compared is the evidence's <em>identity and claim</em>: the record it is about, the
    /// lifecycle event it rode in on, which way it points, who observed it, the run and round or reviewer
    /// it came from, the rule version, and the detail. The counters and the score are deliberately not
    /// compared -- they are derived from whatever the record held when the submission was first made, so a
    /// genuine replay that arrived after other evidence landed would otherwise be reported as a conflict
    /// for agreeing with itself.
    /// </para>
    /// <para>
    /// The event ID <em>is</em> compared, for a stored row that produced one. Without that, a retry under
    /// a fresh event ID would be reported as committed while carrying a lifecycle event that was never
    /// written -- the same trap the plain lifecycle replay avoids by comparing every field. A stored row
    /// that produced no event (a duplicate) has no event ID to contradict, so there is nothing to compare.
    /// </para>
    /// <para>
    /// Every number reported comes from the ledger row, so a replay describes the one moment the original
    /// submission settled rather than mixing a stored revision with a freshly read status.
    /// </para>
    /// </remarks>
    private static async Task<ExperienceLifecycleCommitResult> CompareStoredEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        RecordKey? key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectEvidenceSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("evidence_id", submitted.EvidenceId));
        AddScopeParameters(command.Parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Either no such row, or one whose record is in another scope -- reported identically, so a
            // guessed evidence ID reveals nothing about another scope's scores or counters. Evidence rows
            // are never deleted, so the row that just collided cannot otherwise vanish.
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        try
        {
            var storedEventId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);

            // Another record's evidence under this ID is a conflict, decided before its (differently keyed)
            // detail is opened.
            if (reader.GetGuid(0) != lifecycleEvent.ExperienceRecordId)
            {
                return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
            }

            var storedDetail = reader.IsDBNull(11)
                ? null
                : SealedText.OpenWith(key, SealedText.EvidenceDetailColumn, submitted.EvidenceId, reader.GetString(11));

            var sameContent =
                reader.GetGuid(0) == lifecycleEvent.ExperienceRecordId
                && (storedEventId is null || storedEventId == lifecycleEvent.EventId)
                && DecodeEnumText<ConfidenceEvidenceKind>(reader.GetString(2), "confidence evidence") == submitted.Kind
                && DecodeEnumText<ConfidenceEvidenceSource>(reader.GetString(3), "confidence evidence") == submitted.Source
                && reader.GetGuid(4) == submitted.RunId
                && (reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5)) == submitted.VerificationRoundId
                && string.Equals(reader.IsDBNull(6) ? null : reader.GetString(6), submitted.ReviewerIdentity, StringComparison.Ordinal)
                && string.Equals(reader.GetString(10), submitted.RuleVersion, StringComparison.Ordinal)
                && string.Equals(storedDetail, submitted.Detail, StringComparison.Ordinal)
                && (reader.IsDBNull(18) ? (Guid?)null : reader.GetGuid(18)) == submitted.AssessmentId;

            if (!sameContent)
            {
                return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
            }

            var stored = submitted with
            {
                PriorReuseConfidence = reader.GetDouble(12),
                NewReuseConfidence = reader.GetDouble(13),
                PriorSupportingValidations = reader.GetInt32(14),
                NewSupportingValidations = reader.GetInt32(15),
                PriorContradictions = reader.GetInt32(16),
                NewContradictions = reader.GetInt32(17),

                // Not compared above, on purpose: a replay reports the admission the original was stored
                // with, which is what the ledger says admitted it.
                Admission = reader.IsDBNull(19) ? null : DecodeEnumText<ConfidenceEvidenceAdmission>(reader.GetString(19), "confidence evidence"),
            };

            return new(
                ExperienceStoreOutcome.Committed,
                reader.GetInt64(8),
                ReadStoredStatus(reader, 9),
                NoErrors,
                stored);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // A retyped or hand-written row, reported the way every other decode failure is.
            throw new ExperienceStoreException("Stored confidence evidence could not be decoded.", ex);
        }
    }

    /// <summary>
    /// What a lifecycle commit reports for a record whose key was destroyed: <see cref="ExperienceStoreOutcome.Deleted"/>
    /// with the row's revision, exactly as for a tombstone -- or <see cref="ExperienceStoreOutcome.NotFound"/> when
    /// nothing with that ID is in this scope, so a destroyed key reveals nothing a tombstone would not.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> ShreddedCommitOutcomeAsync(
        NpgsqlConnection connection,
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        var current = await ExperienceSessionContext.RunAsync(
            connection,
            authorization,
            transaction => ReadRevisionAndStatusAsync(connection, transaction, scope, experienceId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return current is { } record
            ? new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors)
            : new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
    }

    /// <summary>
    /// Reports a lost race: the record's current revision, or <see cref="ExperienceStoreOutcome.NotFound"/>
    /// when it is not in this scope at all. Used where the server aborted the transaction itself, so the
    /// re-read runs outside it.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> StaleOrMissingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        var current = await ReadRevisionAndStatusAsync(connection, transaction, scope, experienceId, cancellationToken).ConfigureAwait(false);
        if (current is not { } record)
        {
            return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
        }

        return record.Deleted
            ? new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors)
            : new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors);
    }

    /// <summary>
    /// Reads the record's revision and status within exactly <paramref name="scope"/>, optionally locking
    /// the row for the rest of the transaction. Only the duplicate path needs the lock: every other caller
    /// either holds the row through its own revision-guarded UPDATE or is reporting a race it already lost.
    /// </summary>
    private static async Task<StoredRecordState?> ReadRevisionAndStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand(
            forUpdate ? LockRevisionAndStatusSql : SelectRevisionAndStatusSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(command.Parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // A tombstone's status is a literal no ExperienceStatus member names, so it is never decoded:
        // the marker is read first and the status left alone.
        return reader.IsDBNull(2)
            ? new StoredRecordState(ReadRevision(reader, 0), ReadStoredStatus(reader, 1), Deleted: false)
            : new StoredRecordState(ReadRevision(reader, 0), null, Deleted: true);
    }

    /// <summary>
    /// What a scoped read of one record row found: its revision, its status when it has one this
    /// library's enum names, and whether it is a tombstone.
    /// </summary>
    private readonly record struct StoredRecordState(long Revision, ExperienceStatus? Status, bool Deleted);

    /// <summary>
    /// Matches a unique violation of one named constraint. Naming it keeps the event primary key (a
    /// resubmitted event ID) apart from the record-revision index (a lost race), so neither is ever
    /// mistaken for the other or for a constraint added later.
    /// </summary>
    private static bool IsViolationOf(PostgresException ex, string constraintName, CancellationToken cancellationToken) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(ex.ConstraintName, constraintName, StringComparison.Ordinal)
        && !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Binds the event row, including the confidence payload when the event carries one and the actor
    /// the commit ran under.
    /// </summary>
    /// <remarks>
    /// The actor is <see cref="AuthorizationContext.PrincipalId"/> and is taken from the
    /// host-established context rather than from anything on the event -- which is the same rule the
    /// reviewer identity follows, for the same reason. It is written for every commit, not only a
    /// confidence one, because "who did this" is the question an auditor asks of every transition.
    /// </remarks>
    private static void AddEventParameters(
        NpgsqlParameterCollection parameters,
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt,
        long appliedRevision,
        RecordKey? key)
    {
        parameters.Add(new NpgsqlParameter<Guid>("event_id", lifecycleEvent.EventId));
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
        AddScopeParameters(parameters, scope);
        parameters.Add(NullableText("prior_status", lifecycleEvent.PriorStatus?.ToString()));
        parameters.Add(new NpgsqlParameter<string>("current_status", lifecycleEvent.CurrentStatus.ToString()));
        parameters.Add(new NpgsqlParameter<string>(
            "reason",
            key is null ? lifecycleEvent.Reason : key.Seal(SealedText.EventReasonColumn, lifecycleEvent.EventId, lifecycleEvent.Reason)));
        parameters.Add(new NpgsqlParameter<string>("producer", lifecycleEvent.Producer));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", occurredAt));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
        parameters.Add(new NpgsqlParameter<long>("expected_revision", lifecycleEvent.ExpectedRevision));
        parameters.Add(new NpgsqlParameter<long>("applied_revision", appliedRevision));
        parameters.Add(NullableUuid("replacement_experience_id", lifecycleEvent.ReplacementExperienceId));
        parameters.Add(NullableText("actor", Actor(authorization)));

        var confidence = lifecycleEvent.Confidence;
        parameters.Add(NullableUuid("confidence_evidence_id", confidence?.EvidenceId));
        parameters.Add(NullableText("confidence_kind", confidence?.Kind.ToString()));
        parameters.Add(NullableText("confidence_source", confidence?.Source.ToString()));
        parameters.Add(NullableUuid("confidence_run_id", confidence?.RunId));
        parameters.Add(NullableUuid("confidence_verification_round_id", confidence?.VerificationRoundId));
        parameters.Add(NullableText("confidence_reviewer_identity", confidence?.ReviewerIdentity));
        parameters.Add(NullableText("confidence_rule_version", confidence?.RuleVersion));
        parameters.Add(NullableText(
            "confidence_detail",
            confidence?.Detail is { } detail && key is not null
                ? key.Seal(SealedText.EventConfidenceDetailColumn, lifecycleEvent.EventId, detail)
                : confidence?.Detail));
        parameters.Add(NullableDouble("prior_reuse_confidence", confidence?.PriorReuseConfidence));
        parameters.Add(NullableDouble("new_reuse_confidence", confidence?.NewReuseConfidence));
        parameters.Add(NullableInt("prior_supporting_validations", confidence?.PriorSupportingValidations));
        parameters.Add(NullableInt("new_supporting_validations", confidence?.NewSupportingValidations));
        parameters.Add(NullableInt("prior_contradictions", confidence?.PriorContradictions));
        parameters.Add(NullableInt("new_contradictions", confidence?.NewContradictions));
        parameters.Add(NullableUuid("confidence_assessment_id", confidence?.AssessmentId));
        parameters.Add(NullableText("confidence_admission", confidence?.Admission?.ToString()));
    }

    /// <summary>
    /// The principal to record on a row, or <see langword="null"/> when the host established none worth
    /// recording. It is bound as null rather than as the blank string on purpose: the column's non-blank
    /// CHECK would otherwise turn a host with an empty <see cref="AuthorizationContext.PrincipalId"/> into
    /// an infrastructure failure on *every* lifecycle commit, confidence or not. A blank principal is
    /// still refused where it actually matters -- human evidence, whose whole independence rule rests on
    /// it -- and there it is a typed validation error naming the field.
    /// </summary>
    private static string? Actor(AuthorizationContext authorization) =>
        string.IsNullOrWhiteSpace(authorization.PrincipalId) ? null : authorization.PrincipalId;
}
