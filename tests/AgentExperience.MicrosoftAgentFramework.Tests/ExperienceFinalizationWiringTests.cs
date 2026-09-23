using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 2.5: the MAF adapter's completed run reaches Core's finalization service. The agent runs
/// for real against the scripted fake model; capture, reflection, and lifecycle are the real
/// implementations, and only the record store is in-memory. Finalization never changes what the
/// caller of the agent observes, and a finalization problem is a reported capture failure, never an
/// exception.
/// </summary>
public class ExperienceFinalizationWiringTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:write"], DateTimeOffset.UnixEpoch);
    private static readonly ClosedVerificationRound Round = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), ArtifactRevision);

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    private static Evidence PassingEvidence(CheckResult result = CheckResult.Pass) => new(
        EvidenceId: Guid.NewGuid(),
        VerificationRoundId: Round.RoundId,
        ArtifactRevision: ArtifactRevision,
        CheckId: "tests",
        Kind: "TestResult",
        Result: result,
        Producer: "ci",
        Detail: null,
        CapturedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_completed_run_is_finalized_into_a_durable_Validated_record()
    {
        var harness = new Harness();
        var agent = harness.Capture(new ScriptedChatClient());

        var response = await agent.RunAsync("task-finalize");

        Assert.Equal("Hello, world", response.Text);

        var result = Assert.Single(harness.Finalized);
        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(1, result.Revision);
        Assert.Empty(harness.Failures);

        // The record is the one this invocation's run produced.
        var runId = Assert.Single(harness.Service.StartedRunIds);
        Assert.Equal(ExperienceFinalizationService.ExperienceIdFor(runId, TestScope), result.ExperienceId);
        Assert.Equal(runId, result.Record!.SourceRunId);
        Assert.Equal(ExperienceStatus.Validated, harness.Store.StatusOf(result.ExperienceId!.Value));
    }

    [Fact]
    public async Task A_resolver_that_returns_null_skips_finalizing_that_run_without_reporting_a_failure()
    {
        var harness = new Harness { Resolve = _ => null };

        await harness.Capture(new ScriptedChatClient()).RunAsync("task-skip");

        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Failures);
        Assert.Empty(harness.Store.Records);
    }

    [Fact]
    public async Task A_host_storage_denial_is_an_outcome_not_a_capture_failure()
    {
        var harness = new Harness { Decision = StorageDecision.Deny("host policy") };

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-denied");

        // The agent's own result is untouched.
        Assert.Equal("Hello, world", response.Text);

        var result = Assert.Single(harness.Finalized);
        Assert.Equal(FinalizationOutcome.StorageDenied, result.Outcome);
        Assert.Empty(harness.Store.Records);

        // The host decided this on purpose. Reporting it through the capture-failure channel would give
        // a host whose policy denies most runs one "failure" per invocation, mixed in with real defects.
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task A_finalization_that_actually_failed_is_reported_as_a_capture_failure_and_never_thrown()
    {
        var harness = new Harness();
        harness.Store.ThrowOnCreate = true;

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-store-down");

        Assert.Equal("Hello, world", response.Text);

        var result = Assert.Single(harness.Finalized);
        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.Contains("Failed", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resolver_that_returns_a_request_for_another_run_is_refused()
    {
        var foreignRunId = Guid.NewGuid();
        var harness = new Harness { Resolve = _ => ForeignRequest(foreignRunId) };

        await harness.Capture(new ScriptedChatClient()).RunAsync("task-foreign-run");

        // An unrelated captured run must never be finalized on this invocation's behalf.
        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Store.Records);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.Contains("different run", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exceptions_thrown_by_the_finalized_callback_are_swallowed()
    {
        var harness = new Harness { ThrowFromOnRunFinalized = true };

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-callback-throws");

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(FinalizationOutcome.Validated, Assert.Single(harness.Finalized).Outcome);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task A_throwing_resolver_reports_a_capture_failure_and_leaves_the_invocation_alone()
    {
        var harness = new Harness { Resolve = _ => throw new InvalidOperationException("resolver failed") };

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-resolver-throws");

        Assert.Equal("Hello, world", response.Text);
        Assert.Empty(harness.Finalized);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public async Task A_run_whose_capture_failed_is_never_finalized()
    {
        var harness = new Harness();
        harness.Service.ForcedCompleteOutcome = CompleteRunOutcome.Conflict;

        await harness.Capture(new ScriptedChatClient()).RunAsync("task-capture-failed");

        // A half-captured run must not be persisted as if it were whole.
        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Store.Records);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, Assert.Single(harness.Failures).Stage);
    }

    [Fact]
    public void Half_configured_finalization_is_rejected_at_wiring_time_in_both_directions()
    {
        var harness = new Harness();

        // A service with no resolver can never build a request...
        var serviceOnly = new ExperienceCaptureOptions
        {
            ResolveRun = _ => new ExperienceRunDescriptor("task", TestScope),
            FinalizationService = harness.Finalization,
            ResolveFinalization = null,
        };

        // ...and a resolver with no service -- the easier mistake -- would silently never finalize.
        var resolverOnly = new ExperienceCaptureOptions
        {
            ResolveRun = _ => new ExperienceRunDescriptor("task", TestScope),
            FinalizationService = null,
            ResolveFinalization = _ => null,
        };

        foreach (var options in new[] { serviceOnly, resolverOnly })
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new ScriptedAgent().AsBuilder().UseExperienceCapture(harness.Service, options).Build());

            Assert.Contains(nameof(ExperienceCaptureOptions.ResolveFinalization), exception.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ExperienceCaptureOptions.FinalizationService), exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_failed_invocation_is_still_finalized_and_quarantined()
    {
        var harness = new Harness { Evidence = () => [PassingEvidence(CheckResult.Fail)] };
        var client = new ScriptedChatClient { Throw = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Capture(client).RunAsync("task-failed"));

        var result = Assert.Single(harness.Finalized);
        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Null(result.Record!.Reflection);
        Assert.Equal(ExperienceStatus.Quarantined, harness.Store.StatusOf(result.ExperienceId!.Value));
    }

    /// <summary>A well-formed request that simply names some other captured run.</summary>
    private static FinalizeExperienceRequest ForeignRequest(Guid runId) => new(
        RunId: runId,
        Authorization: Authorization,
        ClosedRound: Round,
        RequiredChecks: [new RequiredCheck("tests", "TestResult")],
        Evidence: [PassingEvidence()],
        CurrentArtifactRevision: ArtifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: DateTimeOffset.UnixEpoch.AddDays(1));

    private sealed class Harness
    {
        private readonly List<ExperienceCaptureFailure> _failures = [];
        private readonly List<FinalizeExperienceResult> _finalized = [];

        public Harness()
        {
            Service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
                new DefaultSanitizer(Sanitization),
                new CaptureLimits(10, 50, 10_000, 10_000)));
            Store = new InMemoryRecordStore();
            Finalization = new ExperienceFinalizationService(
                Service,
                new DefaultExperienceReflector(),
                Store,
                new ExperienceLifecycleService(Store));
        }

        public RecordingCaptureService Service { get; }

        public InMemoryRecordStore Store { get; }

        public ExperienceFinalizationService Finalization { get; }

        public StorageDecision Decision { get; init; } = StorageDecision.Permit;

        public Func<IReadOnlyList<Evidence>> Evidence { get; init; } = () => [PassingEvidence()];

        public Func<ExperienceFinalizationContext, FinalizeExperienceRequest?>? Resolve { get; init; }

        public bool ThrowFromOnRunFinalized { get; init; }

        public FinalizeExperienceRequest RequestFor(Guid runId) => new(
            RunId: runId,
            Authorization: Authorization,
            ClosedRound: Round,
            RequiredChecks: [new RequiredCheck("tests", "TestResult")],
            Evidence: Evidence(),
            CurrentArtifactRevision: ArtifactRevision,
            StorageDecision: Decision,
            FinalizedAt: DateTimeOffset.UnixEpoch.AddDays(1));

        public IReadOnlyList<ExperienceCaptureFailure> Failures
        {
            get
            {
                lock (_failures)
                {
                    return _failures.ToList();
                }
            }
        }

        public IReadOnlyList<FinalizeExperienceResult> Finalized
        {
            get
            {
                lock (_finalized)
                {
                    return _finalized.ToList();
                }
            }
        }

        public AIAgent Capture(ScriptedChatClient client)
        {
            var inner = new ChatClientAgent(client, new ChatClientAgentOptions());
            return inner.AsBuilder().UseExperienceCapture(Service, Options()).Build();
        }

        private ExperienceCaptureOptions Options() => new()
        {
            ResolveRun = context => new ExperienceRunDescriptor(context.Messages.Last().Text, TestScope),
            CaptureToolCalls = false,
            FinalizationService = Finalization,
            ResolveFinalization = Resolve ?? (context => RequestFor(context.Run.RunId)),
            OnRunFinalized = result =>
            {
                lock (_finalized)
                {
                    _finalized.Add(result);
                }

                if (ThrowFromOnRunFinalized)
                {
                    throw new InvalidOperationException("host finalization callback failure");
                }
            },
            OnCaptureFailure = failure =>
            {
                lock (_failures)
                {
                    _failures.Add(failure);
                }
            },
        };
    }

    /// <summary>
    /// A minimal in-memory <see cref="IExperienceRecordStore"/>: enough of the port's contract for the
    /// adapter wiring to be exercised end to end without a database. Query and history are not part of
    /// finalization and fail loudly if they are ever called.
    /// </summary>
    private sealed class InMemoryRecordStore : IExperienceRecordStore
    {
        private readonly Dictionary<Guid, ExperienceRecord> _records = [];
        private readonly HashSet<Guid> _events = [];

        public IReadOnlyDictionary<Guid, ExperienceRecord> Records
        {
            get
            {
                lock (_records)
                {
                    return _records.ToDictionary();
                }
            }
        }

        public ExperienceStatus StatusOf(Guid experienceId)
        {
            lock (_records)
            {
                return _records[experienceId].Status;
            }
        }

        public bool ThrowOnCreate { get; set; }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken)
        {
            if (ThrowOnCreate)
            {
                throw new ExperienceStoreException("database unavailable");
            }

            lock (_records)
            {
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
        }

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken)
        {
            lock (_records)
            {
                return Task.FromResult(_records.TryGetValue(experienceId, out var record) && record.Scope == scope
                    ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [])
                    : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
            }
        }

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            lock (_records)
            {
                if (!_records.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
                {
                    return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
                }

                if (!_events.Add(lifecycleEvent.EventId))
                {
                    return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, record.Revision, null, []));
                }

                var applied = lifecycleEvent.ExpectedRevision + 1;
                _records[record.ExperienceId] = record with
                {
                    Status = lifecycleEvent.CurrentStatus,
                    Revision = applied,
                    UpdatedAt = lifecycleEvent.OccurredAt,
                };

                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, applied, null, []));
            }
        }

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not query records.");

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not read history.");

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not check supersession.");
    }
}
