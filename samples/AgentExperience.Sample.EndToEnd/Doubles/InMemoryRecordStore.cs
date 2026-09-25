using AgentExperience.Abstractions;

namespace AgentExperience.Sample.EndToEnd.Doubles;

/// <summary>
/// A demonstration double, not durable storage: Experience Records held in a dictionary that dies
/// with the process.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the sample runs on a fresh clone with no Docker and no database. It lives under
/// <c>samples/</c> and is <see langword="internal"/> on purpose: shipping an in-memory record store
/// from a published package would invite someone to run it in production. Set
/// <c>AGENTEXPERIENCE_SAMPLE_POSTGRES</c> to run the same seven stages against the real PostgreSQL
/// adapter instead.
/// </para>
/// <para>
/// Only the three operations this sample's loop reaches are implemented --
/// <see cref="CreateAsync"/>, <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>,
/// and <see cref="CommitLifecycleEventAsync"/>. The rest throw
/// <see cref="NotSupportedException"/> rather than returning a plausible-looking empty answer,
/// because a double that quietly answers a question it cannot answer is worse than one that stops.
/// </para>
/// <para>
/// <b>Where it is deliberately no more permissive than the real store.</b> A double that accepts
/// what the real adapter refuses makes the whole demonstration unsound, so the three refusals that
/// protect a record are reproduced here: structural validation before authorization
/// (<see cref="SampleRecordValidation"/>), the prior-status guard, and replay comparison -- a
/// resubmitted <see cref="LifecycleEvent.EventId"/> whose content differs is a
/// <see cref="ExperienceStoreOutcome.Conflict"/>, and a replay that matches reports the revision
/// the <em>original</em> commit applied rather than whatever the record's revision is now. The
/// three confidence columns are written only when <see cref="ConfidenceUpdate.Counted"/> is true,
/// exactly as the adapter picks between two UPDATE statements, so "the counters did not move" is a
/// fact about the write that ran rather than a value that happened to compare equal.
/// </para>
/// <para>
/// What it does <em>not</em> reproduce: the adapter runs its validation through
/// <c>ExperienceRecordValidator</c>, which is <see langword="internal"/> to
/// <c>AgentExperience.Storage.Postgres</c> and visible only to that package's own tests and to the
/// vectors package. The sample cannot call it, so <see cref="SampleRecordValidation"/> restates the
/// subset the sample's records can violate and says so here rather than implying full fidelity. Nor
/// does it keep the adapter's evidence ledger: it writes whatever counters Core submits, so the
/// independence key and an assessment's single use (story 6.6) are the adapter's to enforce, not this
/// double's -- the sample submits no attributed feedback, so it never relies on either.
/// </para>
/// </remarks>
internal sealed class InMemoryRecordStore : IExperienceRecordStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ExperienceRecord> _records = [];
    private readonly Dictionary<Guid, StoredEvent> _events = [];

    /// <summary>One committed lifecycle event, kept so a resubmission can be compared against it.</summary>
    /// <param name="Event">The event exactly as it was committed.</param>
    /// <param name="Scope">The scope it was committed under. Compared alongside the event, as the adapter compares it.</param>
    /// <param name="AppliedRevision">The revision that commit produced. A replay reports this, never the record's current revision.</param>
    private sealed record StoredEvent(LifecycleEvent Event, Scope Scope, long AppliedRevision);

    /// <summary>The records as they stand now, for the sample's candidate source to search.</summary>
    public IReadOnlyList<ExperienceRecord> Snapshot()
    {
        lock (_gate)
        {
            return [.. _records.Values];
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(record);

        // Structural validation first, before authorization and before any store access, in the
        // adapter's order: an unreadable payload is refused the same way whoever asked for it.
        var errors = SampleRecordValidation.ValidateRecord(record);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Invalid, errors));
        }

        lock (_gate)
        {
            if (!authorization.Permits(record.Scope))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, []));
            }

            if (!_records.TryAdd(record.ExperienceId, record))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
            }

            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, []));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        lock (_gate)
        {
            if (!authorization.Permits(scope))
            {
                return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Denied, null, []));
            }

            // A record outside the caller's scope is indistinguishable from a missing one, exactly as
            // the PostgreSQL adapter's scope predicate makes it.
            return Task.FromResult(_records.TryGetValue(experienceId, out var record) && record.Scope == scope
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        ExperienceReadOptions options,
        CancellationToken cancellationToken) =>
        GetAsync(authorization, scope, experienceId, cancellationToken);

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

        var errors = SampleRecordValidation.ValidateLifecycleEvent(scope, lifecycleEvent, authorization);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Invalid, 0, null, errors));
        }

        lock (_gate)
        {
            if (!authorization.Permits(scope))
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Denied, 0, null, []));
            }

            // The event ID is the idempotency key, as it is in the real table. Byte for byte the same
            // event under the same scope is the original commit replayed: nothing is written and the
            // revision that commit applied is reported. Anything else under that ID is a Conflict --
            // the dangerous direction, because an accepted replay that names a different target status
            // would move a record on evidence the log already says was settled.
            if (_events.TryGetValue(lifecycleEvent.EventId, out var stored))
            {
                return Task.FromResult(stored.Event == lifecycleEvent && stored.Scope == scope
                    ? new ExperienceLifecycleCommitResult(
                        ExperienceStoreOutcome.Committed, stored.AppliedRevision, null, [], stored.Event.Confidence)
                    : new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Conflict, 0, null, []));
            }

            if (!_records.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
            }

            // The prior-status guard, in the adapter's form: a null PriorStatus does not skip the match,
            // it falls back to CurrentStatus, so a record's first event may only record the status the
            // record is already in. Skipping it would let a caller move a record from any status to any
            // other by omitting the prior status, which is what Core's transition table exists to stop.
            var requiredPriorStatus = lifecycleEvent.PriorStatus ?? lifecycleEvent.CurrentStatus;
            if (record.Status != requiredPriorStatus)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(
                    ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, []));
            }

            var applied = lifecycleEvent.ExpectedRevision + 1;

            // Only a counted update writes the three confidence columns. The adapter makes this choice
            // by picking between two UPDATE statements; the double makes it by writing two different
            // projections, so "the counters did not move" is a fact about the write that ran.
            var counted = lifecycleEvent.Confidence is { Counted: true } confidence ? confidence : null;

            var updated = record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = applied,
                UpdatedAt = lifecycleEvent.OccurredAt,
            };

            _records[record.ExperienceId] = counted is null
                ? updated
                : updated with
                {
                    ReuseConfidence = counted.NewReuseConfidence,
                    SupportingValidations = counted.NewSupportingValidations,
                    Contradictions = counted.NewContradictions,
                };

            _events[lifecycleEvent.EventId] = new StoredEvent(lifecycleEvent, scope, applied);

            return Task.FromResult(new ExperienceLifecycleCommitResult(
                ExperienceStoreOutcome.Committed, applied, null, [], lifecycleEvent.Confidence));
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(Unsupported(nameof(QueryAsync)));

    /// <inheritdoc />
    public Task<ExperienceRecordHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        ExperienceRecordHistoryQuery query,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(Unsupported(nameof(GetHistoryAsync)));

    /// <inheritdoc />
    public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        Guid replacementExperienceId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(Unsupported(nameof(CheckSupersessionAsync)));

    private static string Unsupported(string operation) =>
        $"The AgentExperience.NET end-to-end sample's in-memory record store implements only the operations its seven stages reach; {operation} is not one of them. The PostgreSQL adapter implements all of them.";
}
