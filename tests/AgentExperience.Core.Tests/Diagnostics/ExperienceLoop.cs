using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Tests.Diagnostics;

/// <summary>
/// The whole Core learning loop wired from the real services, with only the ports that would be a
/// database or a model provider replaced: capture, verification, reflection, finalization, indexing,
/// lifecycle, retrieval, and reuse feedback all run their shipping code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything it can carry is poisoned with <see cref="Marker"/>.</b> The task description, each
/// attempt's result and error, every tool call's arguments, result and error, the retrieval summary
/// the index embeds, and the task text retrieval matches on all contain it -- and the reflection the
/// library builds quotes several of them back. So a single drive of this loop is enough to prove
/// that nothing a run said reaches a span attribute or a metric dimension.
/// </para>
/// <para>
/// The sanitizer is deliberately permissive: a sanitizer that stripped the marker would make the
/// marker sweep pass for the wrong reason.
/// </para>
/// </remarks>
internal sealed class ExperienceLoop
{
    /// <summary>
    /// A string that appears nowhere in this library and cannot be produced by accident, planted in
    /// every piece of captured content the loop touches.
    /// </summary>
    internal const string Marker = "Q7-CANARY-9f3d-DO-NOT-EXPORT";

    /// <summary>The host-supplied correlation identifier, which telemetry <em>is</em> allowed to echo.</summary>
    internal const string CorrelationId = "corr-4242";

    internal const string ArtifactRevision = "rev-1";

    internal static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    internal static readonly Scope Scope = new("tenant-1", "app-1", "project-1");

    internal static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);

    internal static readonly ClosedVerificationRound Round = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), ArtifactRevision);

    private static readonly SanitizationOptions Permissive = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "query" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 3,
            MaxFieldCount: 10,
            MaxValueLength: 1_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 1_000,
            MaxFieldNameLength: 100),
    });

    internal ExperienceLoop()
    {
        Capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Permissive), new CaptureLimits(8, 8, 1_000, 1_000));
        Indexing = new ExperienceIndexingService(Index, Generator);
        Lifecycle = new ExperienceLifecycleService(Store, Indexing);
        Finalization = new ExperienceFinalizationService(Capture, new DefaultExperienceReflector(), Store, Lifecycle, Indexing);
        Retrieval = new ExperienceRetrievalService(Candidates, RetrievalPolicy.Default, RankingWeights.Default, TimeProvider.System);
        FeedbackService = new ExperienceReuseFeedbackService(FeedbackStore, Lifecycle);
    }

    internal LoopRecordStore Store { get; } = new();

    internal LoopCandidateSource Candidates { get; } = new();

    internal LoopFeedbackStore FeedbackStore { get; } = new();

    internal FakeEmbeddingIndex Index { get; } = new();

    internal FakeEmbeddingGenerator Generator { get; } = new();

    internal InMemoryExperienceCaptureService Capture { get; }

    internal ExperienceIndexingService Indexing { get; }

    internal ExperienceLifecycleService Lifecycle { get; }

    internal ExperienceFinalizationService Finalization { get; }

    internal ExperienceRetrievalService Retrieval { get; }

    internal ExperienceReuseFeedbackService FeedbackService { get; }

    /// <summary>
    /// Drives capture, verification, reflection, finalization, indexing, retrieval, reuse feedback and
    /// the confidence update it triggers, then de-indexes -- one call per operation in the frozen
    /// table, all of them succeeding.
    /// </summary>
    /// <param name="cancellationToken">Threaded into every call so a cancelled drive is a realistic one.</param>
    /// <returns>What the drive produced, for a caller that wants to assert on the results as well as on the telemetry.</returns>
    internal async Task<LoopResults> DriveAsync(CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();

        var started = Capture.StartRun(
            runId,
            "task-1",
            $"Summarize the incident report for {Marker}",
            Scope,
            new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string> { ["region"] = "eu-west-1" }),
            new Provenance("tests", "1.0.0", Now, CorrelationId),
            Now);

        var appended = await Capture.AppendAttemptAsync(
            runId,
            new AppendAttemptRequest(
                AttemptId: Guid.NewGuid(),
                StartedAt: Now,
                Duration: TimeSpan.FromSeconds(1),
                ToolCalls:
                [
                    new RawToolCall(
                        ToolCallId: Guid.NewGuid(),
                        ToolName: "search",
                        Arguments: new Dictionary<string, object?> { ["query"] = $"find {Marker}" },
                        StartedAt: Now,
                        Duration: TimeSpan.FromMilliseconds(50),
                        Result: $"tool result mentioning {Marker}",
                        Error: $"tool error mentioning {Marker}"),
                ],
                Result: $"attempt result mentioning {Marker}",
                Error: null),
            cancellationToken).ConfigureAwait(false);

        // A second attempt that failed, so the reflection has a failed approach to quote the marker
        // into as well as a successful one.
        await Capture.AppendAttemptAsync(
            runId,
            new AppendAttemptRequest(
                AttemptId: Guid.NewGuid(),
                StartedAt: Now.AddSeconds(1),
                Duration: TimeSpan.FromSeconds(1),
                ToolCalls: [],
                Result: null,
                Error: $"attempt error mentioning {Marker}"),
            cancellationToken).ConfigureAwait(false);

        var completed = await Capture
            .CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddSeconds(3), cancellationToken)
            .ConfigureAwait(false);

        // verify, driven directly, so the drive has one `verify` the host asked for alongside the one
        // the finalization below performs as a step of its own. The evidence it aggregates carries the
        // marker in both of its free-form fields.
        var verified = VerificationAggregator.Aggregate(
            runId,
            [Evidence()],
            [new RequiredCheck("tests", "TestResult")],
            Round,
            ArtifactRevision,
            Now,
            cancellationToken);

        var experienceId = ExperienceFinalizationService.ExperienceIdFor(runId, Scope);

        // Seeded at the revision the initial lifecycle event will leave the record at, with a summary
        // that carries the marker: the post-commit indexing hook then really embeds poisoned text.
        Index.Records[experienceId] = new FakeEmbeddingIndex.Row(1, $"retrieval summary mentioning {Marker}");

        var finalized = await Finalization.FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Authorization,
                ClosedRound: Round,
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: [Evidence()],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: Now.AddMinutes(1)),
            cancellationToken).ConfigureAwait(false);

        // lifecycle.commit, driven directly, for the same reason -- and with the marker in the two
        // free-form fields a transition carries, so the content sweep covers them too. The
        // finalization above committed the record's initial event, which is the nested counterpart.
        var transitioned = await Lifecycle.CommitAsync(
            Authorization,
            new CommitLifecycleTransitionRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: experienceId,
                Scope: Scope,
                PriorStatus: ExperienceStatus.Validated,
                CurrentStatus: ExperienceStatus.Reinforced,
                Reason: $"reuse of the lesson about {Marker} succeeded again",
                Producer: $"tests-{Marker}",
                OccurredAt: Now.AddMinutes(1),
                ExpectedRevision: 1),
            cancellationToken).ConfigureAwait(false);

        // confidence.apply, driven directly. The reuse-feedback submission below applies evidence once
        // per exposed record, so the drive covers both the direct call and the nested one.
        var confidence = await Lifecycle.ApplyEvidenceAsync(
            Authorization,
            new ApplyConfidenceEvidenceRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: experienceId,
                Scope: Scope,
                EvidenceId: Guid.NewGuid(),
                Kind: ConfidenceEvidenceKind.Supporting,
                Source: ConfidenceEvidenceSource.Machine,
                RunId: runId,
                VerificationRoundId: Round.RoundId,
                Reason: $"a later run reused the lesson about {Marker}",
                Producer: $"tests-{Marker}",
                OccurredAt: Now.AddMinutes(1),
                Detail: $"observed while handling {Marker}"),
            cancellationToken).ConfigureAwait(false);

        // Everything the loop retrieves is the record the transitions above left behind, marker and all.
        if (Store.Find(experienceId) is { } committed)
        {
            Candidates.Candidates = [new ExperienceCandidate(committed, Relevance: 0.9)];
        }

        var retrieved = await Retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(
                Authorization,
                Scope,
                $"another incident like {Marker}",
                RequiredEnvironmentAttributes: null,
                CorrelationId: CorrelationId),
            cancellationToken).ConfigureAwait(false);

        var feedback = await FeedbackService.RecordAsync(
            Authorization,
            new ExperienceReuseFeedback(
                FeedbackId: Guid.NewGuid(),
                RunId: Guid.NewGuid(),
                Scope: Scope,
                ExposedExperienceIds: [experienceId],
                RunOutcome: TaskVerificationStatus.Verified,
                Measure: new ReuseMeasure("task-success", 1),
                ObservedAt: Now.AddMinutes(2))
            {
                HumanAssessment = new HumanReuseAssessment(
                    Guid.NewGuid(),
                    ExperienceReuseBenefit.Improved,
                    [experienceId],
                    $"the lesson about {Marker} applied",
                    Now.AddMinutes(2)),
            },
            cancellationToken).ConfigureAwait(false);

        // The record moved on -- two lifecycle events since finalization embedded it -- so the stored
        // vector is stale and the explicit index pass below has real work to do rather than reporting
        // that nothing changed. Its summary carries the marker, so what is embedded is poisoned text.
        Index.Records[experienceId] = new FakeEmbeddingIndex.Row(3, $"reinforced retrieval summary mentioning {Marker}");

        // index, driven directly. Finalization's post-commit hook indexed the record too, on this
        // library's own budget rather than the caller's token -- which is the nested `index` the call
        // table pins, and the one whose failures an operator is paged for.
        var indexed = await Indexing
            .IndexAsync(Authorization, Scope, experienceId, cancellationToken)
            .ConfigureAwait(false);

        var reindexed = await Indexing
            .ReindexAsync(Authorization, new ReindexExperienceRequest(Scope, [experienceId], Limit: 1), cancellationToken)
            .ConfigureAwait(false);

        var deindexed = await Indexing
            .RemoveAsync(Authorization, Scope, experienceId, cancellationToken)
            .ConfigureAwait(false);

        return new LoopResults(
            runId,
            experienceId,
            started,
            appended,
            completed,
            verified,
            finalized,
            transitioned,
            confidence,
            retrieved,
            feedback,
            indexed,
            reindexed,
            deindexed);
    }

    /// <summary>
    /// One passing piece of evidence in the host-closed round, so the run verifies and is reflected on.
    /// </summary>
    /// <remarks>
    /// Both of its free-form fields carry <see cref="Marker"/>. <c>verify</c> otherwise receives no
    /// marker-bearing input at all, which would leave the content guarantee proven for the operations
    /// that happen to handle captured text and merely assumed for the ones that do not.
    /// </remarks>
    /// <param name="result">The check result this evidence reports.</param>
    /// <returns>The evidence.</returns>
    internal static Evidence Evidence(CheckResult result = CheckResult.Pass) => new(
        EvidenceId: Guid.NewGuid(),
        VerificationRoundId: Round.RoundId,
        ArtifactRevision: ArtifactRevision,
        CheckId: "tests",
        Kind: "TestResult",
        Result: result,
        Producer: $"ci-{Marker}",
        Detail: $"the suite reported {Marker}",
        CapturedAt: Now);
}

/// <summary>What one drive of <see cref="ExperienceLoop"/> produced, so a test can assert the loop really worked before asserting on its telemetry.</summary>
/// <param name="RunId">The captured run.</param>
/// <param name="ExperienceId">The record finalization derived for that run.</param>
/// <param name="Started">The start-run result.</param>
/// <param name="Appended">The first append-attempt result.</param>
/// <param name="Completed">The complete-run result.</param>
/// <param name="Verified">The explicit verification verdict.</param>
/// <param name="Finalized">The finalization result.</param>
/// <param name="Transitioned">The explicit lifecycle transition's result.</param>
/// <param name="Confidence">The explicit confidence-evidence result.</param>
/// <param name="Retrieved">The retrieval result.</param>
/// <param name="Feedback">The reuse-feedback result.</param>
/// <param name="Indexed">The explicit one-record indexing result.</param>
/// <param name="Reindexed">The explicit re-index pass's result.</param>
/// <param name="Deindexed">The explicit de-index result.</param>
internal sealed record LoopResults(
    Guid RunId,
    Guid ExperienceId,
    StartRunResult Started,
    AppendAttemptResult Appended,
    CompleteRunResult Completed,
    VerificationResult Verified,
    FinalizeExperienceResult Finalized,
    CommitLifecycleTransitionResult Transitioned,
    ApplyConfidenceEvidenceResult Confidence,
    ExperienceRetrievalResult Retrieved,
    ExperienceReuseFeedbackResult Feedback,
    ExperienceIndexingResult Indexed,
    ExperienceReindexResult Reindexed,
    ExperienceDeindexingResult Deindexed);

/// <summary>
/// An in-memory <see cref="IExperienceRecordStore"/> with just enough of the port's real contract for
/// the loop: create-once inserts, scope-exact reads, and event-ID-idempotent, revision-checked,
/// prior-status-guarded lifecycle commits that apply a confidence update when one is carried.
/// </summary>
internal sealed class LoopRecordStore : IExperienceRecordStore
{
    private readonly Dictionary<Guid, ExperienceRecord> _records = [];
    private readonly Dictionary<Guid, (LifecycleEvent Event, long Revision)> _events = [];
    private readonly HashSet<Guid> _erased = [];

    /// <summary>When set, every call throws this, which is how an infrastructure failure is driven through the loop.</summary>
    internal Func<Exception>? Throws { get; set; }

    /// <summary>
    /// Runs at the start of every store call, from inside whichever Core operation is in flight. It is
    /// where "the caller's <c>Activity.Current</c> is untouched" is actually observed -- at the bottom
    /// of the call stack, not from the test method.
    /// </summary>
    internal Action? OnCall { get; set; }

    /// <summary>The record as it now stands, or <see langword="null"/> when nothing was ever created for that ID.</summary>
    /// <param name="experienceId">The record to read.</param>
    internal ExperienceRecord? Find(Guid experienceId) => _records.GetValueOrDefault(experienceId);

    /// <summary>
    /// Erases a stored record the way the Postgres adapter does: its ID stays taken, and within its own
    /// scope both a read and a lifecycle commit answer <see cref="ExperienceStoreOutcome.Deleted"/>.
    /// </summary>
    /// <param name="experienceId">The record to erase.</param>
    internal void Erase(Guid experienceId) => _erased.Add(experienceId);

    public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken)
    {
        Fail(cancellationToken);

        if (_records.ContainsKey(record.ExperienceId))
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
        }

        if (!authorization.Permits(record.Scope))
        {
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, []));
        }

        _records[record.ExperienceId] = record;
        return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, []));
    }

    public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken)
    {
        Fail(cancellationToken);

        if (_records.TryGetValue(experienceId, out var record) && record.Scope == scope)
        {
            return Task.FromResult(_erased.Contains(experienceId)
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Deleted, null, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, []));
        }

        return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
    }

    public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        Fail(cancellationToken);

        if (_events.TryGetValue(lifecycleEvent.EventId, out var stored))
        {
            return Task.FromResult(stored.Event == lifecycleEvent
                ? new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, stored.Revision, null, [])
                : new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Conflict, 0, null, []));
        }

        if (!_records.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
        {
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
        }

        if (_erased.Contains(record.ExperienceId))
        {
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Deleted, record.Revision, null, []));
        }

        if (record.Revision != lifecycleEvent.ExpectedRevision)
        {
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
        }

        if (lifecycleEvent.PriorStatus is { } prior && record.Status != prior)
        {
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, []));
        }

        var revision = lifecycleEvent.ExpectedRevision + 1;
        var confidence = lifecycleEvent.Confidence;

        _records[record.ExperienceId] = record with
        {
            Status = lifecycleEvent.CurrentStatus,
            Revision = revision,
            UpdatedAt = lifecycleEvent.OccurredAt,
            ReuseConfidence = confidence?.NewReuseConfidence ?? record.ReuseConfidence,
            SupportingValidations = confidence?.NewSupportingValidations ?? record.SupportingValidations,
            Contradictions = confidence?.NewContradictions ?? record.Contradictions,
        };

        _events[lifecycleEvent.EventId] = (lifecycleEvent, revision);

        return Task.FromResult(new ExperienceLifecycleCommitResult(
            ExperienceStoreOutcome.Committed,
            revision,
            lifecycleEvent.CurrentStatus,
            [],
            confidence));
    }

    public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The telemetry loop never queries records.");

    public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The telemetry loop never reads history.");

    public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The telemetry loop never supersedes a record.");

    /// <remarks>
    /// The scripted exception is checked <em>before</em> the token, so a test that cancels the caller
    /// and scripts an <see cref="OperationCanceledException"/> still gets back the exact instance it
    /// supplied -- which is what lets the classification table assert identity rather than type.
    /// </remarks>
    private void Fail(CancellationToken cancellationToken)
    {
        OnCall?.Invoke();

        if (Throws is { } thrower)
        {
            throw thrower();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>A candidate source that answers with whatever the loop last committed.</summary>
internal sealed class LoopCandidateSource : IExperienceCandidateSource
{
    /// <summary>What the next search returns.</summary>
    internal IReadOnlyList<ExperienceCandidate> Candidates { get; set; } = [];

    /// <summary>The outcome the next search reports. Anything but <c>Found</c> is a refusal to answer.</summary>
    internal ExperienceStoreOutcome Outcome { get; set; } = ExperienceStoreOutcome.Found;

    /// <summary>When set, every search throws this.</summary>
    internal Func<Exception>? Throws { get; set; }

    public Task<ExperienceCandidateSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceCandidateQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Throws is { } thrower)
        {
            throw thrower();
        }

        return Task.FromResult(new ExperienceCandidateSearchResult(Outcome, Candidates, []));
    }
}

/// <summary>A reuse-feedback ledger that records every submission and reports it recorded.</summary>
internal sealed class LoopFeedbackStore : IExperienceReuseFeedbackStore
{
    /// <summary>Every submission the ledger was handed, in order.</summary>
    internal List<RecordedExperienceReuseFeedback> Submissions { get; } = [];

    /// <summary>When set, every write throws this.</summary>
    internal Func<Exception>? Throws { get; set; }

    public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
        AuthorizationContext authorization,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Throws is { } thrower)
        {
            throw thrower();
        }

        Submissions.Add(feedback);
        return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, []));
    }
}
