using AgentExperience.Abstractions;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>The lifecycle commit: the event, the projection and the confidence ledger in one write transaction.</summary>
public sealed partial class SqliteExperienceRecordStore
{
    /// <inheritdoc />
    public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
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
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Invalid, 0, null, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Denied, 0, null, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceLifecycleCommitResult>(cancellationToken);
        }

        // Compared and stored with OccurredAt normalized, as the PostgreSQL store stores and compares it.
        var normalized = lifecycleEvent with { OccurredAt = Micro(lifecycleEvent.OccurredAt) };
        return Database.WriteAsync(
            "The SQLite lifecycle event could not be committed.",
            async (connection, transaction) =>
            {
                var result = await CommitAsync(connection, transaction, authorization, scope, normalized, cancellationToken).ConfigureAwait(false);
                return (result.Result, result.Wrote);
            },
            cancellationToken);
    }

    /// <summary>
    /// The whole commit, under the write lock, in the PostgreSQL (and in-memory) store's order: a resubmitted evidence ID,
    /// a spent assessment and a taken independence key are decided first; then a resubmitted event ID (a replay, or a
    /// conflict); then the supersession guard, which a replay never reaches; then the scope, revision and prior-status
    /// guards; and only then the write.
    /// </summary>
    private async Task<(ExperienceLifecycleCommitResult Result, bool Wrote)> CommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        var recordId = lifecycleEvent.ExperienceRecordId;

        if (lifecycleEvent.Confidence is { } submitted)
        {
            var storedEvidence = await ReadEvidenceAsync(connection, transaction, submitted.EvidenceId, cancellationToken).ConfigureAwait(false);
            if (storedEvidence is not null)
            {
                return (await CompareStoredEvidenceAsync(connection, transaction, scope, lifecycleEvent, submitted, storedEvidence, cancellationToken).ConfigureAwait(false), false);
            }

            if (submitted.AssessmentId is { } assessment
                && await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT 1 FROM experience_confidence_evidence WHERE experience_id = $id AND assessment_id = $assessment",
                    cancellationToken,
                    ("$id", Id(recordId)),
                    ("$assessment", Id(assessment))).ConfigureAwait(false) is not null)
            {
                // One assessment lands at most one piece of evidence per record: a replayed token.
                return (new(
                    ExperienceStoreOutcome.Conflict,
                    0,
                    null,
                    [new StoreValidationError(
                        ConfidenceUpdate.AssessmentIdPath,
                        "this assessment has already landed evidence for this record under another evidence ID.")]), false);
            }

            // A counted submission is a duplicate when its key was counted. A recorded-only one is one when its key was
            // counted or already recorded on an event, so one observation rides at most one event.
            var key = IndependenceKey(submitted);
            var taken = await ScalarAsync(
                connection,
                transaction,
                "SELECT 1 FROM experience_confidence_evidence WHERE experience_id = $id AND independence_key = $key "
                + "AND (counted = 1 OR ($counted = 0 AND event_id IS NOT NULL)) LIMIT 1",
                cancellationToken,
                ("$id", Id(recordId)),
                ("$key", key),
                ("$counted", submitted.Counted ? 1 : 0)).ConfigureAwait(false);
            if (taken is not null)
            {
                return await RecordDuplicateAsync(connection, transaction, scope, lifecycleEvent, submitted, cancellationToken).ConfigureAwait(false);
            }
        }

        var storedEvent = await ReadEventAsync(connection, transaction, lifecycleEvent.EventId, cancellationToken).ConfigureAwait(false);
        if (storedEvent is not null)
        {
            return (CompareStoredEvent(scope, lifecycleEvent, storedEvent), false);
        }

        var record = await StateInScopeAsync(connection, transaction, scope, recordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors), false);
        }

        if (record.Deleted)
        {
            // A tombstone is terminal: nothing is appended against one.
            return (new(ExperienceStoreOutcome.Deleted, 0, null, NoErrors), false);
        }

        // After replay detection, so retrying a committed supersession reports its original outcome even once the
        // replacement has moved on.
        if (lifecycleEvent.ReplacementExperienceId is { } replacementId)
        {
            var check = await CheckSupersessionAsync(connection, transaction, scope, recordId, replacementId, cancellationToken).ConfigureAwait(false);
            if (check.Outcome != ExperienceSupersessionOutcome.Allowed
                || check.ReplacementStatus is not { } replacementStatus
                || !ExperienceStatuses.IsEligibleForReuse(replacementStatus))
            {
                return (new(ExperienceStoreOutcome.ReplacementNotAllowed, 0, check.ReplacementStatus, NoErrors), false);
            }
        }

        if (record.Revision != lifecycleEvent.ExpectedRevision)
        {
            return (new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors), false);
        }

        // A null prior status does not skip the guard: it falls back to the event's current status.
        if (record.Status != (lifecycleEvent.PriorStatus ?? lifecycleEvent.CurrentStatus))
        {
            return (new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors), false);
        }

        var now = Now;
        var appliedRevision = lifecycleEvent.ExpectedRevision + 1;

        // Host-trusted evidence recorded only moves the revision and nothing else: refreshing UpdatedAt would let it keep
        // the record recent for ranking and un-expired for MaxAge, which is exactly what it must not steer.
        var recordedOnly = lifecycleEvent.Confidence is { Counted: false };
        var counted = lifecycleEvent.Confidence is { Counted: true } update ? update : null;
        var moved = await ExecuteAsync(
            connection,
            transaction,
            "UPDATE experience_records SET status = $status, revision = $applied, updated_at = $updated_at, "
            + "reuse_confidence = coalesce($confidence, reuse_confidence), "
            + "supporting_validations = coalesce($supporting, supporting_validations), "
            + "contradictions = coalesce($contradictions, contradictions) "
            + "WHERE experience_id = $id AND revision = $expected AND deleted_at IS NULL",
            cancellationToken,
            ("$status", lifecycleEvent.CurrentStatus.ToString()),
            ("$applied", appliedRevision),
            ("$updated_at", Ticks(recordedOnly ? record.UpdatedAt : now)),
            ("$confidence", counted?.NewReuseConfidence),
            ("$supporting", counted?.NewSupportingValidations),
            ("$contradictions", counted?.NewContradictions),
            ("$id", Id(recordId)),
            ("$expected", lifecycleEvent.ExpectedRevision)).ConfigureAwait(false);
        if (moved != 1)
        {
            // Unreachable under the write lock; refused rather than written over.
            return (new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors), false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO experience_lifecycle_events (event_id, experience_id, " + ScopeColumns + ", applied_revision, recorded_at, "
            + "actor, replacement_experience_id, event) VALUES ($event_id, $id, " + ScopeValues + ", $applied, $recorded_at, $actor, "
            + "$replacement, $event)",
            cancellationToken,
            [
                ("$event_id", Id(lifecycleEvent.EventId)),
                ("$id", Id(recordId)),
                .. ScopeParameters(scope),
                ("$applied", appliedRevision),
                ("$recorded_at", Ticks(now)),
                ("$actor", Actor(authorization)),
                ("$replacement", lifecycleEvent.ReplacementExperienceId is { } replacement ? Id(replacement) : null),
                ("$event", ToJson(lifecycleEvent)),
            ]).ConfigureAwait(false);

        if (lifecycleEvent.Confidence is { } confidence)
        {
            await AddEvidenceAsync(connection, transaction, recordId, lifecycleEvent.EventId, confidence, appliedRevision, lifecycleEvent.CurrentStatus, cancellationToken).ConfigureAwait(false);
        }

        return (new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors, lifecycleEvent.Confidence), true);
    }

    /// <summary>
    /// A submission whose independence key was already counted: recorded, for audit, with nothing moved, and that ledger
    /// entry is all it writes.
    /// </summary>
    private static async Task<(ExperienceLifecycleCommitResult, bool)> RecordDuplicateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        CancellationToken cancellationToken)
    {
        var record = await StateInScopeAsync(connection, transaction, scope, lifecycleEvent.ExperienceRecordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors), false);
        }

        if (record.Deleted)
        {
            return (new(ExperienceStoreOutcome.Deleted, 0, null, NoErrors), false);
        }

        if (record.Revision != lifecycleEvent.ExpectedRevision)
        {
            return (new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors), false);
        }

        if (lifecycleEvent.PriorStatus is { } prior && record.Status != prior)
        {
            return (new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors), false);
        }

        var recordedOnly = submitted.AsRecordedOnly();
        await AddEvidenceAsync(connection, transaction, lifecycleEvent.ExperienceRecordId, eventId: null, recordedOnly, record.Revision, record.Status, cancellationToken).ConfigureAwait(false);
        return (new(ExperienceStoreOutcome.Committed, record.Revision, record.Status, NoErrors, recordedOnly), true);
    }

    private static Task AddEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        Guid? eventId,
        ConfidenceUpdate update,
        long appliedRevision,
        ExperienceStatus appliedStatus,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO experience_confidence_evidence (evidence_id, experience_id, event_id, independence_key, counted, "
            + "assessment_id, applied_revision, applied_status, evidence) VALUES ($evidence_id, $id, $event_id, $key, $counted, "
            + "$assessment, $applied, $applied_status, $evidence)",
            cancellationToken,
            ("$evidence_id", Id(update.EvidenceId)),
            ("$id", Id(recordId)),
            ("$event_id", eventId is { } e ? Id(e) : null),
            ("$key", IndependenceKey(update)),
            ("$counted", update.Counted ? 1 : 0),
            ("$assessment", update.AssessmentId is { } a ? Id(a) : null),
            ("$applied", appliedRevision),
            ("$applied_status", appliedStatus.ToString()),
            ("$evidence", ToJson(update)));

    /// <summary>
    /// Decides a resubmitted <see cref="ConfidenceUpdate.EvidenceId"/>, as the other stores do: the same evidence about the
    /// same observation is the original replayed, reported with the numbers it was stored with; anything else, or evidence
    /// for a record outside this scope, is a conflict.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> CompareStoredEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        StoredEvidence stored,
        CancellationToken cancellationToken)
    {
        var inScope = await StateInScopeAsync(connection, transaction, scope, stored.ExperienceId, cancellationToken).ConfigureAwait(false);
        var sameContent =
            stored.ExperienceId == lifecycleEvent.ExperienceRecordId
            && inScope is { Deleted: false }
            && (stored.EventId is null || stored.EventId == lifecycleEvent.EventId)
            && stored.Update.Kind == submitted.Kind
            && stored.Update.Source == submitted.Source
            && stored.Update.RunId == submitted.RunId
            && stored.Update.VerificationRoundId == submitted.VerificationRoundId
            && string.Equals(stored.Update.ReviewerIdentity, submitted.ReviewerIdentity, StringComparison.Ordinal)
            && string.Equals(stored.Update.RuleVersion, submitted.RuleVersion, StringComparison.Ordinal)
            && string.Equals(stored.Update.Detail, submitted.Detail, StringComparison.Ordinal)
            && stored.Update.AssessmentId == submitted.AssessmentId;

        if (!sameContent)
        {
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        var replayed = submitted with
        {
            PriorReuseConfidence = stored.Update.PriorReuseConfidence,
            NewReuseConfidence = stored.Update.NewReuseConfidence,
            PriorSupportingValidations = stored.Update.PriorSupportingValidations,
            NewSupportingValidations = stored.Update.NewSupportingValidations,
            PriorContradictions = stored.Update.PriorContradictions,
            NewContradictions = stored.Update.NewContradictions,
            Admission = stored.Update.Admission,
        };

        return new(ExperienceStoreOutcome.Committed, stored.AppliedRevision, stored.AppliedStatus, NoErrors, replayed);
    }

    /// <summary>
    /// Decides a resubmitted <see cref="LifecycleEvent.EventId"/>: the same event, every field and the scope included, is
    /// the original commit replayed and reports the revision that commit produced; anything else is a conflict.
    /// </summary>
    private static ExperienceLifecycleCommitResult CompareStoredEvent(Scope scope, LifecycleEvent lifecycleEvent, StoredEvent stored)
    {
        if (stored.Event.ExperienceRecordId != lifecycleEvent.ExperienceRecordId || stored.Scope != scope)
        {
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        var resubmitted = lifecycleEvent with
        {
            Confidence = lifecycleEvent.Confidence is { } confidence
                ? ExperienceRecordValidator.AsReplayOf(confidence, stored.Event.Confidence)
                : null,
        };
        if (ExperienceRecordValidator.IsSameEvidence(lifecycleEvent.Confidence, stored.Event.Confidence))
        {
            resubmitted = resubmitted with { CurrentStatus = stored.Event.CurrentStatus };
        }

        return stored.Event == resubmitted
            ? new(ExperienceStoreOutcome.Committed, stored.AppliedRevision, null, NoErrors, stored.Event.Confidence)
            : new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
    }

    private static async Task<StoredEvidence?> ReadEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction, Guid evidenceId, CancellationToken cancellationToken)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT experience_id, event_id, evidence, applied_revision, applied_status FROM experience_confidence_evidence WHERE evidence_id = $id",
            ("$id", Id(evidenceId)));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredEvidence(
            ReadId(reader, 0),
            ReadOptionalId(reader, 1),
            FromJson<ConfidenceUpdate>(reader.GetString(2)),
            reader.GetInt64(3),
            Enum.Parse<ExperienceStatus>(reader.GetString(4)));
    }

    private static async Task<StoredEvent?> ReadEventAsync(SqliteConnection connection, SqliteTransaction transaction, Guid eventId, CancellationToken cancellationToken)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT event, " + ScopeColumns + ", applied_revision FROM experience_lifecycle_events WHERE event_id = $id",
            ("$id", Id(eventId)));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredEvent(FromJson<LifecycleEvent>(reader.GetString(0)), ReadScope(reader, 1), reader.GetInt64(7));
    }

    /// <summary>
    /// The key independent evidence is counted once per record under: a machine observation once per run and verification
    /// round, a human judgement once per reviewer and run. The same shape as the PostgreSQL ledger's generated column.
    /// </summary>
    private static string IndependenceKey(ConfidenceUpdate update) => update.Source == ConfidenceEvidenceSource.Machine
        ? $"machine:{update.RunId}:{update.VerificationRoundId}"
        : $"human:{update.ReviewerIdentity}:{update.RunId}";

    /// <summary>One committed lifecycle event, as the replay check needs it.</summary>
    private sealed record StoredEvent(LifecycleEvent Event, Scope Scope, long AppliedRevision);

    /// <summary>One piece of confidence evidence in the ledger.</summary>
    private sealed record StoredEvidence(Guid ExperienceId, Guid? EventId, ConfidenceUpdate Update, long AppliedRevision, ExperienceStatus AppliedStatus);
}
