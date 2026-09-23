using AgentExperience.Core.Retrieval;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// A clock frozen at a known instant. Its timers never fire, so a test that does not mean to
/// exercise the retrieval timeout cannot accidentally hit one.
/// </summary>
internal sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp() => now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverFires();

    private sealed class NeverFires : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}

/// <summary>
/// The world an injection test runs against: a search index and a record store that are
/// deliberately <em>separate</em> collections, because that is the whole point of the final
/// eligibility check. What <see cref="SearchAsync"/> returns is a snapshot taken when the record was
/// indexed; what <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> returns is the record as it stands now. A test makes a
/// record "revoked between retrieval and injection" simply by changing the stored one.
/// </summary>
internal sealed class FakeExperienceWorld : IExperienceCandidateSource, IExperienceRecordStore
{
    private readonly List<ExperienceCandidate> _indexed = [];
    private readonly Dictionary<Guid, ExperienceRecord> _stored = [];
    private readonly HashSet<Guid> _events = [];
    private readonly List<Guid> _reads = [];
    private readonly List<ExperienceReadOptions> _readOptions = [];

    /// <summary>Thrown by <see cref="SearchAsync"/> when set, to exercise a failing retrieval.</summary>
    public Exception? SearchThrows { get; set; }

    /// <summary>Thrown by <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> when set, to exercise a store that is down at re-check time.</summary>
    public Exception? GetThrows { get; set; }

    /// <summary>Awaited inside <see cref="SearchAsync"/> when set, to exercise the retrieval timeout.</summary>
    public Func<CancellationToken, Task>? SearchDelay { get; set; }

    /// <summary>Awaited inside <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> when set, to exercise the eligibility-check bound.</summary>
    public Func<CancellationToken, Task>? GetDelay { get; set; }

    /// <summary>Signalled the first time <see cref="SearchAsync"/> or <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> is entered.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Records <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> reports as <c>NotFound</c>, whatever is stored.</summary>
    public HashSet<Guid> Unreadable { get; } = [];

    /// <summary>Records <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> reports as <c>Denied</c>.</summary>
    public HashSet<Guid> Denied { get; } = [];

    /// <summary>Records <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> reports as <c>Invalid</c>.</summary>
    public HashSet<Guid> Invalid { get; } = [];

    /// <summary>Records <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> answers with a record carrying a <em>different</em> ID.</summary>
    public HashSet<Guid> Misidentified { get; } = [];

    /// <summary>
    /// Active sharing grants, as (record, recipient scope) pairs. They widen exactly the two calls the
    /// real adapter's SQL predicate widens -- the search and the re-read -- and nothing else, so a test
    /// can share a record with a sibling scope, or revoke it mid-flight by removing the pair.
    /// </summary>
    public HashSet<(Guid ExperienceId, Scope Recipient)> Grants { get; } = [];

    private readonly Dictionary<(Guid ExperienceId, Scope Recipient), Guid> _grantIds = [];

    /// <summary>
    /// The host's auditing policy, when a test wires one. <see langword="null"/> -- the default -- is a
    /// deployment with no access log: nothing is recorded and nothing else changes. The fake models
    /// the whole contract the real adapter implements, failure callback and mode included, so a test
    /// can exercise <see cref="ExperienceGrantAuditingMode.Required"/> here rather than inferring it
    /// from a database test elsewhere.
    /// </summary>
    public ExperienceGrantAuditing? Auditing { get; set; }

    /// <summary>Every read's options, in order, so a test can assert what the provider declared.</summary>
    public IReadOnlyList<ExperienceReadOptions> ReadOptions
    {
        get
        {
            lock (_readOptions)
            {
                return _readOptions.ToList();
            }
        }
    }

    /// <summary>Shares one record with one recipient scope, the way an administrator's grant would.</summary>
    /// <returns>The grant's ID, which a delivered read then names.</returns>
    public Guid Grant(Guid experienceId, Scope recipient)
    {
        Grants.Add((experienceId, recipient));
        var grantId = Guid.NewGuid();
        _grantIds[(experienceId, recipient)] = grantId;
        return grantId;
    }

    /// <summary>Withdraws a grant, the way a revocation or an expiry would between two reads.</summary>
    public void Revoke(Guid experienceId, Scope recipient)
    {
        Grants.Remove((experienceId, recipient));
        _grantIds.Remove((experienceId, recipient));
    }

    /// <summary>The grant a read through <paramref name="recipient"/> was permitted by, if this fake knows one.</summary>
    private Guid? PermittingGrant(Guid experienceId, Scope recipient) =>
        _grantIds.TryGetValue((experienceId, recipient), out var grantId) ? grantId : null;

    /// <summary>
    /// Records <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/> answers <c>Found</c> for with a record from another tenant,
    /// without declaring any grant -- a store that hands back something it was never asked for.
    /// </summary>
    public HashSet<Guid> Foreign { get; } = [];

    /// <summary>Whether <paramref name="scope"/> may read <paramref name="record"/>: its own scope, or an active grant.</summary>
    private bool Readable(ExperienceRecord record, Scope scope) =>
        record.Scope == scope || Grants.Contains((record.ExperienceId, scope));

    /// <summary>
    /// Whether the read was widened by a grant, which is exactly what the real adapter reports: it is
    /// the negation of the exact-scope match, decided by the same layer that decided readability.
    /// </summary>
    private bool SharedByGrant(ExperienceRecord record, Scope scope) => record.Scope != scope;

    /// <summary>Every record ID the final eligibility check re-read, in order.</summary>
    public IReadOnlyList<Guid> Reads
    {
        get
        {
            lock (_reads)
            {
                return _reads.ToList();
            }
        }
    }

    /// <summary>The records as they stand now.</summary>
    public IReadOnlyDictionary<Guid, ExperienceRecord> Stored
    {
        get
        {
            lock (_stored)
            {
                return _stored.ToDictionary();
            }
        }
    }

    /// <summary>Puts a record in the store and in the search index, as a finalized, indexed record would be.</summary>
    public void Publish(ExperienceRecord record, double relevance = 1d)
    {
        Store(record);
        Index(record, relevance);
    }

    /// <summary>Puts a record in the store only. The search index is untouched.</summary>
    public void Store(ExperienceRecord record)
    {
        lock (_stored)
        {
            _stored[record.ExperienceId] = record;
        }
    }

    /// <summary>Puts a snapshot of a record in the search index only. The store is untouched.</summary>
    public void Index(ExperienceRecord record, double relevance = 1d)
    {
        lock (_indexed)
        {
            _indexed.Add(new ExperienceCandidate(record, relevance));
        }
    }

    public async Task<ExperienceCandidateSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        Entered.TrySetResult();

        if (SearchDelay is { } delay)
        {
            await delay(cancellationToken);
        }

        if (SearchThrows is { } exception)
        {
            throw exception;
        }

        if (!authorization.Permits(query.Scope))
        {
            return new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Denied, [], []);
        }

        List<ExperienceCandidate> matches;
        lock (_indexed)
        {
            matches = _indexed
                .Where(candidate => Readable(candidate.Record, query.Scope)
                    && query.EligibleStatuses.Contains(candidate.Record.Status)
                    && candidate.Record.ReuseConfidence >= query.MinimumConfidence)
                .OrderByDescending(candidate => candidate.Relevance)
                .Take(query.Limit)
                .Select(candidate => candidate with { SharedByGrant = SharedByGrant(candidate.Record, query.Scope) })
                .ToList();
        }

        return new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, matches, []);
    }

    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        GetAsync(authorization, scope, experienceId, new ExperienceReadOptions(), cancellationToken);

    public async Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        lock (_reads)
        {
            _reads.Add(experienceId);
        }

        lock (_readOptions)
        {
            _readOptions.Add(options);
        }

        Entered.TrySetResult();

        if (GetDelay is { } delay)
        {
            await delay(cancellationToken);
        }

        if (GetThrows is { } exception)
        {
            throw exception;
        }

        if (!authorization.Permits(scope) || Denied.Contains(experienceId))
        {
            return new ExperienceRecordGetResult(ExperienceStoreOutcome.Denied, null, []);
        }

        if (Invalid.Contains(experienceId))
        {
            return new ExperienceRecordGetResult(ExperienceStoreOutcome.Invalid, null, [new StoreValidationError("ExperienceId", "malformed")]);
        }

        ExperienceRecordGetResult result;
        lock (_stored)
        {
            if (Unreadable.Contains(experienceId)
                || !_stored.TryGetValue(experienceId, out var record)
                || !Readable(record, scope))
            {
                return new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []);
            }

            // A store that answers Found with a record from another tenant and declares no grant.
            if (Foreign.Contains(experienceId))
            {
                return new ExperienceRecordGetResult(
                    ExperienceStoreOutcome.Found,
                    record with { Scope = record.Scope with { TenantId = "tenant-elsewhere" } },
                    []);
            }

            // A store that answers Found with somebody else's record: the provider must not trust it.
            if (Misidentified.Contains(experienceId))
            {
                return new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record with { ExperienceId = Guid.NewGuid() }, []);
            }

            var shared = SharedByGrant(record, scope);
            result = new ExperienceRecordGetResult(
                ExperienceStoreOutcome.Found,
                record,
                [],
                shared,
                shared ? PermittingGrant(record.ExperienceId, scope) : null);
        }

        // The adapter audits a DELIVERY, in a separate statement after the read, only when a grant is
        // what made the delivery possible, and never when the caller declared a scope check. The fake
        // mirrors all of that -- including the catch and the mode -- so an injection test can exercise
        // fail-closed auditing without a database.
        if (Auditing is not { } auditing
            || options.Purpose == ExperienceReadPurpose.ScopeCheck
            || result is not { SharedByGrant: true, Record: { } delivered, PermittingGrantId: { } grantId })
        {
            return result;
        }

        var access = new ExperienceGrantAccess(
            Guid.NewGuid(),
            grantId,
            delivered.ExperienceId,
            delivered.Revision,
            delivered.Scope,
            scope,
            authorization.PrincipalId,
            options.CorrelationId,
            auditing.Clock.GetUtcNow());

        try
        {
            await auditing.Log.RecordAsync([access], cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            auditing.OnNotRecorded(new ExperienceGrantAccessFailure([access], auditing.Mode, ex));
            return auditing.Mode == ExperienceGrantAuditingMode.Required
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, [])
                : result;
        }
    }

    public Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        lock (_stored)
        {
            if (_stored.ContainsKey(record.ExperienceId))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
            }

            if (!authorization.Permits(record.Scope))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, []));
            }

            _stored[record.ExperienceId] = record;
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, []));
        }
    }

    public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        lock (_stored)
        {
            if (!_stored.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
            }

            if (!_events.Add(lifecycleEvent.EventId))
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, record.Revision, null, []));
            }

            var applied = lifecycleEvent.ExpectedRevision + 1;
            _stored[record.ExperienceId] = record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = applied,
                UpdatedAt = lifecycleEvent.OccurredAt,
            };

            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, applied, null, []));
        }
    }

    public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Injection must not query records.");

    public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Injection must not read history.");

    public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Injection must not check supersession.");
}

/// <summary>
/// An access ledger held in memory, so an injection test can count the rows a delivery produced
/// without a database. It is append-only for the same reason the real table is: a test that could
/// rewrite a row could not tell the difference between one read and two.
/// </summary>
internal sealed class InMemoryGrantAccessLog : IExperienceGrantAccessLog
{
    private readonly List<ExperienceGrantAccess> _rows = [];

    /// <summary>Thrown by <see cref="RecordAsync"/> when set, to exercise a ledger that is down.</summary>
    public Exception? Throws { get; set; }

    /// <summary>Every access row appended, in order.</summary>
    public IReadOnlyList<ExperienceGrantAccess> Rows
    {
        get
        {
            lock (_rows)
            {
                return _rows.ToList();
            }
        }
    }

    public Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken)
    {
        if (Throws is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_rows)
        {
            _rows.AddRange(accesses);
        }

        return Task.CompletedTask;
    }

    public Task<ExperienceGrantAccessQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceGrantAccessQuery query,
        CancellationToken cancellationToken)
    {
        lock (_rows)
        {
            var rows = _rows
                .Where(row => row.RecordScope == query.RecordScope
                    && (query.ExperienceId is not { } id || row.ExperienceId == id))
                .OrderBy(row => row.OccurredAt)
                .ThenBy(row => row.AccessId)
                .Take(query.Limit)
                .ToList();

            return Task.FromResult(new ExperienceGrantAccessQueryResult(
                ExperienceStoreOutcome.Found,
                rows,
                [],
                rows.Count > 0 ? new ExperienceGrantAccessCursor(rows[^1].OccurredAt, rows[^1].AccessId) : null));
        }
    }
}

/// <summary>Builders for the Experience Records injection tests inject.</summary>
internal static class InjectionRecords
{
    public const string SecretArgument = "sk-live-must-never-be-injected";
    public const string RawResult = "raw-tool-result-must-never-be-injected";
    public const string RawError = "raw-tool-error-must-never-be-injected";
    public const string EvidenceDetail = "raw-evidence-detail-must-never-be-injected";

    public static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A record shaped like one finalization would have produced: a reflection with a lesson and its
    /// supporting fields, plus a raw captured attempt and evidence detail that injection must never
    /// serialize.
    /// </summary>
    public static ExperienceRecord Record(
        Guid experienceId,
        Scope scope,
        string taskId = "triage-ticket",
        string lesson = "Check the lock table before retrying the refund.",
        string? reuseGuidance = "Reuse only when the ticket is a refund.",
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 2d / 3d,
        Reflection? reflection = null,
        string toolName = "refund_ticket",
        IReadOnlyList<Attempt>? attempts = null,
        TaskVerificationStatus verification = TaskVerificationStatus.Verified)
    {
        var evidenceId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

        return new ExperienceRecord(
            ExperienceId: experienceId,
            SourceRunId: Guid.Parse("11111111-0000-0000-0000-000000000001"),
            Scope: scope,
            TaskId: taskId,
            TaskSummary: "A refund ticket stuck on a lock.",
            Attempts: attempts ??
            [
                new Attempt(
                    AttemptId: Guid.Parse("22222222-0000-0000-0000-000000000001"),
                    SequenceNumber: 0,
                    StartedAt: Now,
                    Duration: TimeSpan.FromSeconds(1),
                    ToolCalls:
                    [
                        new ToolCallRecord(
                            ToolCallId: Guid.Parse("33333333-0000-0000-0000-000000000001"),
                            SequenceNumber: 0,
                            ToolName: toolName,
                            Arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["apiKey"] = SecretArgument },
                            StartedAt: Now,
                            Duration: TimeSpan.FromMilliseconds(5),
                            Result: RawResult,
                            Error: RawError),
                    ],
                    Result: RawResult,
                    Error: null),
            ],
            Outcome: new Outcome(
                verification,
                [
                    new Evidence(
                        EvidenceId: evidenceId,
                        VerificationRoundId: Guid.Parse("44444444-0000-0000-0000-000000000001"),
                        ArtifactRevision: "rev-1",
                        CheckId: "tests",
                        Kind: "TestResult",
                        Result: CheckResult.Pass,
                        Producer: "ci",
                        Detail: EvidenceDetail,
                        CapturedAt: Now),
                ],
                Reason: null,
                EvaluatedAt: Now),
            CompletionScore: 1d,
            Reflection: reflection ?? new Reflection(
                ReflectionId: Guid.Parse("55555555-0000-0000-0000-000000000001"),
                ExperienceRunId: Guid.Parse("11111111-0000-0000-0000-000000000001"),
                Lesson: lesson,
                SuccessfulApproaches: ["Waited for the lock."],
                FailedApproaches: ["Retried immediately."],
                Preconditions: ["The ticket is a refund."],
                Warnings: ["The lock table is shared."],
                ReuseGuidance: reuseGuidance,
                EvidenceIds: [evidenceId],
                VerificationStatus: TaskVerificationStatus.Verified,
                CompletionScore: 1d,
                VerificationRuleVersion: "1",
                Producer: "tests",
                CreatedAt: Now),
            Environment: new EnvironmentFingerprint("host", "net10.0", "test-os", null, new Dictionary<string, string>(StringComparer.Ordinal)),
            Provenance: new Provenance("tests", null, Now, null),
            Status: status,
            ReuseConfidence: confidence,
            SupportingValidations: 1,
            Contradictions: 0,
            Revision: 1,
            CreatedAt: Now,
            UpdatedAt: Now);
    }

    /// <summary>An identifier that is easy to read in an assertion failure.</summary>
    public static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");
}
