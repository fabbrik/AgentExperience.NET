using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers the post-commit indexing hook on <see cref="ExperienceFinalizationService"/>: it indexes a
/// record that was just committed, it never runs on an already-finalized replay, and no failure it
/// can reach -- a provider that throws, an unreachable index, even a cancellation -- is ever allowed
/// to turn a durable finalization into anything else.
/// </summary>
public class FinalizationIndexingHookTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);
    private static readonly ClosedVerificationRound Round = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), ArtifactRevision);

    /// <summary>The same permissive policy the rest of the finalization tests capture under.</summary>
    private static readonly SanitizationOptions PermissiveOptions = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
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

    // ---------------------------------------------------------------- matrix: index after commit

    [Fact]
    public async Task A_finalized_run_is_indexed_after_its_initial_event_commits_at_revision_1()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(1, result.Revision);

        var indexing = result.Indexing!;
        Assert.Equal(ExperienceIndexingOutcome.Indexed, indexing.Outcome);
        Assert.Equal(result.ExperienceId, indexing.ExperienceId);
        Assert.Equal(1, indexing.Descriptor!.SourceRevision);
        Assert.Equal("fake-embed-v1", indexing.Descriptor.ModelId);
        Assert.Equal(4, indexing.Descriptor.Dimension);
        Assert.True(harness.Index.Stored.ContainsKey(result.ExperienceId!.Value));
    }

    [Fact]
    public async Task Only_the_records_task_id_summary_and_lesson_are_handed_to_the_provider()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync();

        var summary = Assert.Single(harness.Generator.Requests);
        Assert.Equal(ExperienceRetrievalSummary.For(result.Record!), summary);
        Assert.StartsWith("task-1 a test task", summary, StringComparison.Ordinal);

        // Nothing operational travels with it: the run's attempt outcome is not in the summary.
        Assert.DoesNotContain("done", summary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- matrix: provider down

    [Fact]
    public async Task A_provider_that_throws_leaves_the_record_committed_durable_and_text_searchable()
    {
        var harness = await Harness.WithCompletedRunAsync(
            generator: new FakeEmbeddingGenerator { Throws = FakeEmbeddingGenerator.ThrownException });

        var result = await harness.FinalizeAsync();

        // Finalization itself is untouched by the outage.
        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceStatus.Validated, result.Status);
        Assert.Equal(1, result.Revision);
        Assert.Null(result.Failure);

        // And the failure is reported, and retryable.
        Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, result.Indexing!.Outcome);
        Assert.True(result.Indexing.IsRetryable);
        Assert.Same(FakeEmbeddingGenerator.ThrownException, result.Indexing.Failure!.Exception);
        Assert.Empty(harness.Index.Stored);
    }

    [Fact]
    public async Task An_index_that_throws_is_reported_and_never_fails_finalization()
    {
        var harness = await Harness.WithCompletedRunAsync(
            index: new FakeEmbeddingIndex { ScanThrows = FakeEmbeddingIndex.ThrownException });

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceIndexingOutcome.IndexFailed, result.Indexing!.Outcome);
        Assert.True(result.Indexing.IsRetryable);
    }

    [Fact]
    public async Task A_cancellation_inside_the_hook_is_reported_rather_than_denying_a_record_that_is_already_durable()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var harness = await Harness.WithCompletedRunAsync(generator: new FakeEmbeddingGenerator { Gate = gate });

        var finalizing = harness.Service.FinalizeAsync(harness.Request(), cancellation.Token);

        // Cancel once the provider has actually been entered, so the commit is already done.
        while (harness.Generator.Requests.Count == 0)
        {
            await Task.Yield();
        }

        await cancellation.CancelAsync();
        var result = await finalizing;

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceIndexingOutcome.IndexFailed, result.Indexing!.Outcome);
        Assert.Contains("cancelled", result.Indexing.Failure!.Reason, StringComparison.OrdinalIgnoreCase);
        gate.TrySetResult();
    }

    [Fact]
    public async Task A_hung_provider_cannot_hold_the_call_open_after_the_record_is_durable()
    {
        // "Derived data never blocks canonical data" includes not blocking the caller's thread once the
        // canonical work is done: the hook gets its own budget, not the caller's unbounded token.
        var gate = new TaskCompletionSource();
        var harness = await Harness.WithCompletedRunAsync(
            generator: new FakeEmbeddingGenerator { Gate = gate },
            indexingTimeout: TimeSpan.FromMilliseconds(50));

        var finalizing = harness.Service.FinalizeAsync(harness.Request(), CancellationToken.None);

        var result = await finalizing.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceIndexingOutcome.IndexFailed, result.Indexing!.Outcome);
        Assert.True(result.Indexing.IsRetryable);
        Assert.Contains("did not finish within", result.Indexing.Failure!.Reason, StringComparison.Ordinal);
        gate.TrySetResult();
    }

    // ---------------------------------------------------------------- ineligible records

    [Fact]
    public async Task A_quarantined_record_is_never_sent_to_a_provider()
    {
        // Its vector could never be returned by a search, so embedding it would hand a third party a
        // task summary and lesson for nothing.
        var harness = await Harness.WithCompletedRunAsync(reflector: new ThrowingReflector());

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceIndexingOutcome.Ineligible, result.Indexing!.Outcome);
        Assert.False(result.Indexing.IsRetryable);
        Assert.Empty(harness.Generator.Requests);
        Assert.Empty(harness.Index.Stored);
    }

    // ---------------------------------------------------------------- replay

    [Fact]
    public async Task An_already_finalized_replay_never_re_embeds_anything()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var first = await harness.FinalizeAsync();
        Assert.Equal(FinalizationOutcome.Validated, first.Outcome);
        Assert.Single(harness.Generator.Requests);

        var replay = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.AlreadyFinalized, replay.Outcome);
        Assert.Null(replay.Indexing);
        Assert.Single(harness.Generator.Requests);
    }

    [Fact]
    public async Task A_finalization_that_never_commits_never_indexes()
    {
        var harness = await Harness.WithCompletedRunAsync();

        // The host's storage decision refuses before anything is written, so there is nothing to index.
        var result = await harness.FinalizeAsync(decision: StorageDecision.Deny("retention policy"));

        Assert.Equal(FinalizationOutcome.StorageDenied, result.Outcome);
        Assert.Null(result.Indexing);
        Assert.Empty(harness.Generator.Requests);
    }

    [Fact]
    public async Task Finalization_without_an_indexing_hook_reports_no_indexing_at_all()
    {
        var harness = await Harness.WithCompletedRunAsync(withHook: false);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Null(result.Indexing);
    }

    // ---------------------------------------------------------------- harness

    /// <summary>Real capture, a real lifecycle service and a real indexing service over in-memory doubles.</summary>
    private sealed class Harness
    {
        public required InMemoryExperienceCaptureService Capture { get; init; }

        public required RecordingStore Store { get; init; }

        public required FakeEmbeddingIndex Index { get; init; }

        public required FakeEmbeddingGenerator Generator { get; init; }

        public required ExperienceFinalizationService Service { get; init; }

        public Guid RunId { get; private set; }

        public static async Task<Harness> WithCompletedRunAsync(
            FakeEmbeddingIndex? index = null,
            FakeEmbeddingGenerator? generator = null,
            bool withHook = true,
            IExperienceReflector? reflector = null,
            TimeSpan? indexingTimeout = null)
        {
            var capture = new InMemoryExperienceCaptureService(
                new DefaultSanitizer(PermissiveOptions),
                new CaptureLimits(50, 50, 10_000, 10_000));
            var embeddingIndex = index ?? new FakeEmbeddingIndex();
            var embeddingGenerator = generator ?? new FakeEmbeddingGenerator();
            var store = new RecordingStore(embeddingIndex);

            var harness = new Harness
            {
                Capture = capture,
                Store = store,
                Index = embeddingIndex,
                Generator = embeddingGenerator,
                Service = new ExperienceFinalizationService(
                    capture,
                    reflector ?? new DefaultExperienceReflector(),
                    store,
                    new ExperienceLifecycleService(store),
                    withHook ? new ExperienceIndexingService(embeddingIndex, embeddingGenerator) : null,
                    indexingTimeout),
            };

            harness.RunId = Guid.NewGuid();
            Assert.Equal(
                StartRunOutcome.Started,
                capture.StartRun(
                    harness.RunId,
                    taskId: "task-1",
                    taskDescription: "a test task",
                    scope: TestScope,
                    environment: new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
                    provenance: new Provenance("unit-tests", "1.0.0", Now, null),
                    startedAt: Now).Outcome);

            Assert.Equal(
                AppendAttemptOutcome.Recorded,
                (await capture.AppendAttemptAsync(
                    harness.RunId,
                    new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null))).Outcome);

            Assert.Equal(
                CompleteRunOutcome.Recorded,
                (await capture.CompleteRunAsync(harness.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddMinutes(1))).Outcome);

            return harness;
        }

        public FinalizeExperienceRequest Request(StorageDecision? decision = null) => new(
            RunId: RunId,
            Authorization: Authorization,
            ClosedRound: Round,
            RequiredChecks: [new RequiredCheck("tests", "TestResult")],
            Evidence:
            [
                new Evidence(
                    EvidenceId: Guid.NewGuid(),
                    VerificationRoundId: Round.RoundId,
                    ArtifactRevision: ArtifactRevision,
                    CheckId: "tests",
                    Kind: "TestResult",
                    Result: CheckResult.Pass,
                    Producer: "ci",
                    Detail: null,
                    CapturedAt: Now),
            ],
            CurrentArtifactRevision: ArtifactRevision,
            StorageDecision: decision ?? StorageDecision.Permit,
            FinalizedAt: Now.AddMinutes(2));

        public Task<FinalizeExperienceResult> FinalizeAsync(StorageDecision? decision = null) =>
            Service.FinalizeAsync(Request(decision), CancellationToken.None);
    }

    /// <summary>A reflector that always fails, so the record is committed as Quarantined.</summary>
    private sealed class ThrowingReflector : IExperienceReflector
    {
        public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("scripted reflector failure");
    }

    /// <summary>
    /// A minimal in-memory record store that also keeps the embedding index's view of the canonical
    /// world in step, so the conditional-write contract the index enforces is exercised against the
    /// same revisions finalization actually committed.
    /// </summary>
    private sealed class RecordingStore(FakeEmbeddingIndex index) : IExperienceRecordStore
    {
        private readonly Dictionary<Guid, ExperienceRecord> _records = [];

        public Task<ExperienceRecordCreateResult> CreateAsync(
            AuthorizationContext authorization,
            ExperienceRecord record,
            CancellationToken cancellationToken)
        {
            if (!_records.TryAdd(record.ExperienceId, record))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
            }

            Publish(record);
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, []));
        }

        public Task<ExperienceRecordGetResult> GetAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_records.TryGetValue(experienceId, out var record) && record.Scope == scope
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));

        public Task<ExperienceRecordQueryResult> QueryAsync(
            AuthorizationContext authorization,
            ExperienceRecordQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceRecordQueryResult(ExperienceStoreOutcome.Found, [], []));

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            if (!_records.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
            }

            var applied = lifecycleEvent.ExpectedRevision + 1;
            var updated = record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = applied,
                UpdatedAt = lifecycleEvent.OccurredAt,
            };

            _records[record.ExperienceId] = updated;
            Publish(updated);
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, applied, null, []));
        }

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(
            AuthorizationContext authorization,
            ExperienceRecordHistoryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Found, 0, [], []));

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not check supersession.");

        /// <summary>Mirrors a committed record into the index's view of the world, exactly as the real schema's join would see it.</summary>
        private void Publish(ExperienceRecord record) =>
            index.Records[record.ExperienceId] = new FakeEmbeddingIndex.Row(record.Revision, ExperienceRetrievalSummary.For(record));
    }
}
