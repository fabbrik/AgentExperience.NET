using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// An <see cref="IExperienceRecordStore"/> held in process memory. <b>For development and tests only.</b> Every
/// record, lifecycle event and piece of confidence evidence is lost when the process ends, and none of the
/// PostgreSQL store's guarantees apply: no database-enforced append-only logs, no erasure (and so no erasure reach),
/// no backups, no two database roles, no crypto-shredding.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does keep.</b> It passes the same store conformance suite as the PostgreSQL adapter, so the rules Core
/// relies on hold: create-only records with IDs unique across every scope, exact-scope reads that tell a caller
/// nothing about another scope, idempotent lifecycle commits keyed on <see cref="LifecycleEvent.EventId"/>, the
/// optimistic revision, prior-status and supersession guards, ordered and paged history, and the confidence
/// ledger's evidence-ID idempotency, independence key and single-use assessment. It validates every request with the
/// PostgreSQL store's own rules, compiled from the same source file, so it refuses exactly what that store refuses.
/// </para>
/// <para>
/// <b>What it leaves out.</b> Sharing grants and the grant access log: a read behaves as if no grant exists, so a
/// record is readable only in its own exact scope and <see cref="ExperienceRecordGetResult.SharedByGrant"/> is never
/// set. Also erasure, retention and encryption, which are not port members.
/// </para>
/// <para>
/// <b>Thread safety.</b> Every operation is atomic under one lock per instance, so concurrent creates of one ID have
/// exactly one winner and concurrent commits from one revision exactly one. A record is deep-copied when it is created,
/// into a snapshot whose collections are read-only, so mutating the caller's instance afterwards changes nothing
/// stored, and a record a read returns is safe to share. As in the PostgreSQL store,
/// <see cref="ExperienceRecord.CreatedAt"/>, <see cref="ExperienceRecord.UpdatedAt"/> and a lifecycle event's
/// <see cref="LifecycleEvent.OccurredAt"/> are stored in UTC, truncated to whole microseconds.
/// </para>
/// <para>
/// Construct it directly in tests. In a host, register it with
/// <see cref="DependencyInjection.AgentExperienceInMemoryServiceCollectionExtensions.AddAgentExperienceInMemoryStorageForDevelopment"/>,
/// which refuses to run in any host environment but Development, Test or Testing unless explicitly overridden.
/// Direct construction is not guarded.
/// </para>
/// </remarks>
public sealed class InMemoryExperienceRecordStore : IExperienceRecordStore
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];
    private static readonly ExperienceReadOptions DeliveryRead = new();

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private readonly Dictionary<Guid, ExperienceRecord> _records = [];
    private readonly Dictionary<Guid, RecordSearchText> _searchText = [];
    private readonly Dictionary<Guid, StoredEvent> _events = [];
    private readonly Dictionary<Guid, List<StoredEvent>> _history = [];
    private readonly Dictionary<Guid, StoredEvidence> _evidence = [];
    private readonly HashSet<(Guid ExperienceId, string Key)> _countedIndependenceKeys = [];

    /// <summary>
    /// The keys host-trusted evidence recorded without counting rode in on an event under: a later
    /// recorded-only submission for one of them is a duplicate, exactly as a counted key's would be.
    /// </summary>
    private readonly HashSet<(Guid ExperienceId, string Key)> _recordedOnlyIndependenceKeys = [];
    private readonly HashSet<(Guid ExperienceId, Guid AssessmentId)> _spentAssessments = [];

    /// <summary>
    /// Creates an empty store. <b>For development and tests only</b>: data is lost when the process ends, and none
    /// of the PostgreSQL guarantees apply.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock a commit stamps <see cref="ExperienceRecord.UpdatedAt"/> and <see cref="StoredLifecycleEvent.RecordedAt"/>
    /// with. <see langword="null"/> uses <see cref="TimeProvider.System"/>.
    /// </param>
    public InMemoryExperienceRecordStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(record);

        var errors = ExperienceRecordValidator.ValidateRecord(record);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Invalid, errors));
        }

        if (!authorization.Permits(record.Scope))
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordCreateResult>(cancellationToken);
        }

        // Copied, and its searchable words taken, before the lock: the stored snapshot shares nothing with the
        // caller's instance, and its text never changes afterwards.
        var snapshot = StoredSnapshots.Record(record);
        var searchText = SearchText.Index(snapshot);

        lock (_gate)
        {
            // IDs are unique across every scope, and the answer is the same whichever scope holds the existing one.
            if (!_records.TryAdd(snapshot.ExperienceId, snapshot))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, NoErrors));
            }

            _searchText.Add(snapshot.ExperienceId, searchText);
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, NoErrors));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        GetAsync(authorization, scope, experienceId, DeliveryRead, cancellationToken);

    /// <inheritdoc />
    /// <remarks>There are no grants here, so <paramref name="options"/> changes nothing: nothing is audited.</remarks>
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Invalid, null, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Denied, null, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordGetResult>(cancellationToken);
        }

        lock (_gate)
        {
            return Task.FromResult(Read(scope, experienceId));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetManyResult> GetManyAsync(
        AuthorizationContext authorization,
        Scope scope,
        IReadOnlyList<Guid> experienceIds,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(experienceIds);
        ArgumentNullException.ThrowIfNull(options);

        var errors = ExperienceRecordValidator.ValidateGetMany(scope, experienceIds.Count);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordGetManyResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceRecordGetManyResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordGetManyResult>(cancellationToken);
        }

        var results = new ExperienceRecordGetResult[experienceIds.Count];
        lock (_gate)
        {
            for (var i = 0; i < results.Length; i++)
            {
                // An empty GUID is answered per position, with exactly the errors a single read of it gets.
                var id = experienceIds[i];
                results[i] = id == Guid.Empty
                    ? new(ExperienceStoreOutcome.Invalid, null, ExperienceRecordValidator.ValidateGet(scope, id))
                    : Read(scope, id);
            }
        }

        return Task.FromResult(new ExperienceRecordGetManyResult(ExperienceStoreOutcome.Found, results, NoErrors));
    }

    /// <inheritdoc />
    public Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordQueryResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceRecordQueryResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordQueryResult>(cancellationToken);
        }

        var statuses = query.Statuses?.ToHashSet();
        List<ExperienceRecord> records;
        lock (_gate)
        {
            records = [.. _records.Values
                .Where(record => record.Scope == query.Scope && (statuses is null || statuses.Contains(record.Status)))
                .OrderByDescending(record => record.CreatedAt)
                .ThenBy(record => record.ExperienceId)
                .Take(query.Limit)];
        }

        return Task.FromResult(new ExperienceRecordQueryResult(ExperienceStoreOutcome.Found, records, NoErrors));
    }

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

        lock (_gate)
        {
            // Compared and stored with OccurredAt normalized, as the PostgreSQL store stores and compares it.
            return Task.FromResult(Commit(authorization, scope, StoredSnapshots.Event(lifecycleEvent)));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        ExperienceRecordHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Scope, $"{nameof(query)}.{nameof(query.Scope)}");

        var errors = ExperienceRecordValidator.ValidateHistoryQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Invalid, 0, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Denied, 0, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceRecordHistoryResult>(cancellationToken);
        }

        lock (_gate)
        {
            if (!TryGetInScope(query.Scope, query.ExperienceId, out var record))
            {
                // A record in another scope is indistinguishable from a missing one.
                return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.NotFound, 0, [], NoErrors));
            }

            // Events are kept in the order they were applied, which is ascending applied revision.
            var events = (_history.TryGetValue(record.ExperienceId, out var history) ? history : [])
                .Where(stored => query.StartAfterRevision is not { } cursor || stored.AppliedRevision > cursor)
                .Take(query.Limit)
                .Select(stored => new StoredLifecycleEvent(stored.Event, stored.RecordedAt, stored.AppliedRevision, stored.Actor))
                .ToList();

            return Task.FromResult(new ExperienceRecordHistoryResult(
                ExperienceStoreOutcome.Found,
                record.Revision,
                events,
                NoErrors,
                events.Count > 0 ? events[^1].AppliedRevision : null));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        Guid replacementExperienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateSupersessionCheck(scope, experienceId, replacementExperienceId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceSupersessionCheckResult(ExperienceSupersessionOutcome.Invalid, null, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceSupersessionCheckResult(ExperienceSupersessionOutcome.Denied, null, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceSupersessionCheckResult>(cancellationToken);
        }

        lock (_gate)
        {
            return Task.FromResult(CheckSupersession(scope, experienceId, replacementExperienceId));
        }
    }

    /// <summary>
    /// The records in exactly <paramref name="scope"/> that <paramref name="predicate"/> accepts, with their searchable
    /// words, read under the store's lock, for the candidate source that searches this store. Both are immutable
    /// snapshots, so the caller may use them after the lock is released.
    /// </summary>
    internal List<(ExperienceRecord Record, RecordSearchText Text)> Select(Scope scope, Func<ExperienceRecord, bool> predicate)
    {
        lock (_gate)
        {
            return [.. _records.Values
                .Where(record => record.Scope == scope && predicate(record))
                .Select(record => (record, _searchText[record.ExperienceId]))];
        }
    }

    private ExperienceRecordGetResult Read(Scope scope, Guid experienceId) =>
        TryGetInScope(scope, experienceId, out var record)
            ? new(ExperienceStoreOutcome.Found, record, NoErrors)
            : new(ExperienceStoreOutcome.NotFound, null, NoErrors);

    /// <summary>The record, only when it lies in exactly <paramref name="scope"/> (ordinal, case-sensitive, null matches only null).</summary>
    private bool TryGetInScope(Scope scope, Guid experienceId, out ExperienceRecord record)
    {
        if (_records.TryGetValue(experienceId, out var found) && found.Scope == scope)
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    /// <summary>
    /// The whole commit, under the lock, in the PostgreSQL store's order: a resubmitted evidence ID, a spent
    /// assessment and a taken independence key are decided first (they settle before an event is written); then a
    /// resubmitted event ID (a replay, or a conflict); then the supersession guard, which a replay never reaches; then
    /// the scope, revision and prior-status guards; and only then the write.
    /// </summary>
    private ExperienceLifecycleCommitResult Commit(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent)
    {
        var recordId = lifecycleEvent.ExperienceRecordId;

        if (lifecycleEvent.Confidence is { } submitted)
        {
            if (_evidence.TryGetValue(submitted.EvidenceId, out var storedEvidence))
            {
                return CompareStoredEvidence(scope, lifecycleEvent, submitted, storedEvidence);
            }

            if (submitted.AssessmentId is { } assessment && _spentAssessments.Contains((recordId, assessment)))
            {
                // One assessment lands at most one piece of evidence per record: a replayed token.
                return new(
                    ExperienceStoreOutcome.Conflict,
                    0,
                    null,
                    [new StoreValidationError(
                        ConfidenceUpdate.AssessmentIdPath,
                        "this assessment has already landed evidence for this record under another evidence ID.")]);
            }

            // A counted submission is a duplicate when its key was counted. A recorded-only one (host-trusted
            // evidence that moves nothing) is one when its key was counted or already recorded that way, so one
            // observation rides at most one event.
            var key = (recordId, IndependenceKey(submitted));
            if (_countedIndependenceKeys.Contains(key)
                || (!submitted.Counted && _recordedOnlyIndependenceKeys.Contains(key)))
            {
                return RecordDuplicate(scope, lifecycleEvent, submitted);
            }
        }

        if (_events.TryGetValue(lifecycleEvent.EventId, out var storedEvent))
        {
            return CompareStoredEvent(scope, lifecycleEvent, storedEvent);
        }

        if (!TryGetInScope(scope, recordId, out var record))
        {
            return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
        }

        // After replay detection, so retrying a committed supersession reports its original outcome even once the
        // replacement has moved on.
        if (lifecycleEvent.ReplacementExperienceId is { } replacementId)
        {
            var check = CheckSupersession(scope, recordId, replacementId);
            if (check.Outcome != ExperienceSupersessionOutcome.Allowed
                || check.ReplacementStatus is not { } replacementStatus
                || !ExperienceStatuses.IsEligibleForReuse(replacementStatus))
            {
                return new(ExperienceStoreOutcome.ReplacementNotAllowed, 0, check.ReplacementStatus, NoErrors);
            }
        }

        if (record.Revision != lifecycleEvent.ExpectedRevision)
        {
            return new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors);
        }

        // A null prior status does not skip the guard: it falls back to the event's current status, so a first event
        // may only record the status the record is already in.
        if (record.Status != (lifecycleEvent.PriorStatus ?? lifecycleEvent.CurrentStatus))
        {
            return new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors);
        }

        var now = StoredSnapshots.Timestamp(_timeProvider.GetUtcNow());
        var appliedRevision = lifecycleEvent.ExpectedRevision + 1;
        // Host-trusted evidence recorded only moves the revision and nothing else: refreshing UpdatedAt would let it
        // keep the record recent for ranking and un-expired for MaxAge, which is exactly what it must not steer.
        var recordedOnly = lifecycleEvent.Confidence is { Counted: false };
        var moved = record with
        {
            Status = lifecycleEvent.CurrentStatus,
            Revision = appliedRevision,
            UpdatedAt = recordedOnly ? record.UpdatedAt : now,
        };

        // Only a counted update writes the three confidence values, and every one is a number the event carried.
        if (lifecycleEvent.Confidence is { Counted: true } counted)
        {
            moved = moved with
            {
                ReuseConfidence = counted.NewReuseConfidence,
                SupportingValidations = counted.NewSupportingValidations,
                Contradictions = counted.NewContradictions,
            };
        }

        var stored = new StoredEvent(lifecycleEvent, scope, appliedRevision, now, Actor(authorization));
        _records[recordId] = moved;
        _events[lifecycleEvent.EventId] = stored;
        if (!_history.TryGetValue(recordId, out var history))
        {
            _history[recordId] = history = [];
        }

        history.Add(stored);

        if (lifecycleEvent.Confidence is { } confidence)
        {
            AddEvidence(recordId, lifecycleEvent.EventId, confidence, appliedRevision, lifecycleEvent.CurrentStatus);
        }

        return new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors, lifecycleEvent.Confidence);
    }

    /// <summary>
    /// A submission whose independence key was already counted: it is recorded, for audit, with nothing moved, and
    /// that ledger entry is all it writes. No event, no counters, no status, no revision, no <c>UpdatedAt</c>.
    /// </summary>
    private ExperienceLifecycleCommitResult RecordDuplicate(Scope scope, LifecycleEvent lifecycleEvent, ConfidenceUpdate submitted)
    {
        if (!TryGetInScope(scope, lifecycleEvent.ExperienceRecordId, out var record))
        {
            return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
        }

        if (record.Revision != lifecycleEvent.ExpectedRevision)
        {
            return new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors);
        }

        if (lifecycleEvent.PriorStatus is { } prior && record.Status != prior)
        {
            return new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors);
        }

        var recordedOnly = submitted.AsRecordedOnly();
        AddEvidence(record.ExperienceId, eventId: null, recordedOnly, record.Revision, record.Status);
        return new(ExperienceStoreOutcome.Committed, record.Revision, record.Status, NoErrors, recordedOnly);
    }

    private void AddEvidence(Guid recordId, Guid? eventId, ConfidenceUpdate update, long appliedRevision, ExperienceStatus appliedStatus)
    {
        _evidence[update.EvidenceId] = new StoredEvidence(recordId, eventId, update, appliedRevision, appliedStatus);
        if (update.Counted)
        {
            _countedIndependenceKeys.Add((recordId, IndependenceKey(update)));
        }
        else if (eventId is not null)
        {
            _recordedOnlyIndependenceKeys.Add((recordId, IndependenceKey(update)));
        }

        if (update.AssessmentId is { } assessment)
        {
            _spentAssessments.Add((recordId, assessment));
        }
    }

    /// <summary>
    /// Decides a resubmitted <see cref="ConfidenceUpdate.EvidenceId"/>, as the PostgreSQL store does: the same
    /// evidence about the same observation is the original replayed, reported with the numbers it was stored with;
    /// anything else, or evidence for a record outside this scope, is a conflict. The counters and the score are not
    /// compared, because a genuine replay that arrives after other evidence carries different ones.
    /// </summary>
    private ExperienceLifecycleCommitResult CompareStoredEvidence(
        Scope scope,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        StoredEvidence stored)
    {
        var sameContent =
            stored.ExperienceId == lifecycleEvent.ExperienceRecordId
            && TryGetInScope(scope, stored.ExperienceId, out _)
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
    /// Decides a resubmitted <see cref="LifecycleEvent.EventId"/>: the same event, every field and the scope included,
    /// is the original commit replayed and reports the revision that commit produced; anything else is a conflict,
    /// whichever scope owns the stored event. A confidence payload's admission is the one field not compared.
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
            // The status follows from whether the evidence was counted, which a host's HostTrustedEvidence setting
            // decides; a genuine replay after that setting changed is still the same event.
            resubmitted = resubmitted with { CurrentStatus = stored.Event.CurrentStatus };
        }

        return stored.Event == resubmitted
            ? new(ExperienceStoreOutcome.Committed, stored.AppliedRevision, null, NoErrors, stored.Event.Confidence)
            : new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
    }

    /// <summary>
    /// Whether <paramref name="replacementId"/> may replace <paramref name="experienceId"/> in exactly
    /// <paramref name="scope"/>: both must be there, and the replacement must not already sit on a chain that leads
    /// back to the record. The chain is walked over this scope's events only, so another scope's events can neither
    /// extend it nor reveal that they exist. A record naming itself is the shortest such chain.
    /// </summary>
    private ExperienceSupersessionCheckResult CheckSupersession(Scope scope, Guid experienceId, Guid replacementId)
    {
        if (!TryGetInScope(scope, experienceId, out _))
        {
            return new(ExperienceSupersessionOutcome.RecordNotFound, null, NoErrors);
        }

        if (!TryGetInScope(scope, replacementId, out var replacement))
        {
            return new(ExperienceSupersessionOutcome.ReplacementNotFound, null, NoErrors);
        }

        var reached = new HashSet<Guid> { replacementId };
        var pending = new Queue<Guid>(reached);
        while (pending.TryDequeue(out var current))
        {
            if (!_history.TryGetValue(current, out var events))
            {
                continue;
            }

            foreach (var stored in events)
            {
                if (stored.Scope == scope && stored.Event.ReplacementExperienceId is { } next && reached.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return new(
            reached.Contains(experienceId) ? ExperienceSupersessionOutcome.Cycle : ExperienceSupersessionOutcome.Allowed,
            replacement.Status,
            NoErrors);
    }

    /// <summary>
    /// The key independent evidence is counted once per record under: a machine observation once per run and
    /// verification round, a human judgement once per reviewer and run. The same shape as the PostgreSQL ledger's
    /// generated column.
    /// </summary>
    private static string IndependenceKey(ConfidenceUpdate update) => update.Source == ConfidenceEvidenceSource.Machine
        ? $"machine:{update.RunId}:{update.VerificationRoundId}"
        : $"human:{update.ReviewerIdentity}:{update.RunId}";

    private static string? Actor(AuthorizationContext authorization) =>
        string.IsNullOrWhiteSpace(authorization.PrincipalId) ? null : authorization.PrincipalId;

    /// <summary>One committed lifecycle event, with what only the store knows about it.</summary>
    private sealed record StoredEvent(LifecycleEvent Event, Scope Scope, long AppliedRevision, DateTimeOffset RecordedAt, string? Actor);

    /// <summary>One piece of confidence evidence in the ledger, counted or recorded only.</summary>
    private sealed record StoredEvidence(Guid ExperienceId, Guid? EventId, ConfidenceUpdate Update, long AppliedRevision, ExperienceStatus AppliedStatus);
}
