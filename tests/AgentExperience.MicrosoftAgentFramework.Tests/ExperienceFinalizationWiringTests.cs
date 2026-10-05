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
    public async Task An_async_resolver_that_awaits_its_evidence_finalizes_the_run_as_the_sync_form_does()
    {
        Harness? harness = null;
        CancellationToken observed = default;
        harness = new Harness
        {
            ResolveAsync = async (context, cancellationToken) =>
            {
                observed = cancellationToken;
                await Task.Yield();
                await Task.Delay(1, cancellationToken);
                return harness!.RequestFor(context.Run.RunId);
            },
        };

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-finalize-async");

        Assert.Equal("Hello, world", response.Text);
        var result = Assert.Single(harness.Finalized);
        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Empty(harness.Failures);
        var runId = Assert.Single(harness.Service.StartedRunIds);
        Assert.Equal(ExperienceFinalizationService.ExperienceIdFor(runId, TestScope), result.ExperienceId);

        // Awaited under the finalization-bounded token, which a resolver can honour.
        Assert.True(observed.CanBeCanceled);
    }

    [Fact]
    public async Task The_async_resolvers_token_is_the_finalization_token_not_the_callers()
    {
        using var caller = new CancellationTokenSource();
        Harness? harness = null;
        var cancelledWhenResolving = true;
        harness = new Harness
        {
            ResolveAsync = async (context, cancellationToken) =>
            {
                // The caller gives up while the evidence is being produced; finalization is not the caller's to stop.
                await caller.CancelAsync();
                cancelledWhenResolving = cancellationToken.IsCancellationRequested;
                return harness!.RequestFor(context.Run.RunId);
            },
        };

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-caller-cancels", cancellationToken: caller.Token);

        Assert.Equal("Hello, world", response.Text);
        Assert.False(cancelledWhenResolving);
        Assert.Equal(FinalizationOutcome.Validated, Assert.Single(harness.Finalized).Outcome);
        Assert.Empty(harness.Failures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_async_resolver_overrunning_FinalizationTimeout_is_reported_once_and_finalizes_nothing(bool honoursToken)
    {
        var clock = new ManualTimeoutTimeProvider();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness? harness = null;
        harness = new Harness
        {
            Clock = clock,
            ResolveAsync = async (context, cancellationToken) =>
            {
                try
                {
                    entered.TrySetResult(cancellationToken);
                    if (honoursToken)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    else
                    {
                        await release.Task;
                    }

                    return harness!.RequestFor(context.Run.RunId);
                }
                finally
                {
                    exited.TrySetResult();
                }
            },
        };

        var run = harness.Capture(new ScriptedChatClient()).RunAsync("task-slow-evidence");
        var token = await entered.Task;
        await clock.FireTimeoutsAsync();

        // Capture never changes what the caller sees: the invocation returns once the step has timed out.
        Assert.Equal("Hello, world", (await run).Text);
        Assert.True(token.IsCancellationRequested);

        release.TrySetResult();
        await exited.Task;
        await Task.Delay(50);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("did not finish within", failure.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Store.Records);
    }

    [Fact]
    public async Task Disposing_capture_while_the_async_resolver_awaits_finalizes_nothing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness? harness = null;
        harness = new Harness
        {
            ResolveAsync = async (context, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return harness!.RequestFor(context.Run.RunId);
            },
        };

        var run = harness.Capture(new ScriptedChatClient(), out var captureLifetime).RunAsync("task-disposed");
        await entered.Task;
        captureLifetime.Dispose();
        release.TrySetResult();

        Assert.Equal("Hello, world", (await run).Text);
        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Store.Records);
    }

    [Fact]
    public async Task An_async_resolver_that_returns_null_skips_finalizing_that_run_without_reporting_a_failure()
    {
        var harness = new Harness { ResolveAsync = static async (_, _) => { await Task.Yield(); return null; } };

        await harness.Capture(new ScriptedChatClient()).RunAsync("task-skip-async");

        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Failures);
        Assert.Empty(harness.Store.Records);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_async_resolver_that_throws_or_faults_reports_a_capture_failure_and_leaves_the_invocation_alone(bool faultAfterAwait)
    {
        var harness = new Harness
        {
            ResolveAsync = faultAfterAwait
                ? static async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("evidence unavailable");
                }
                : static (_, _) => throw new InvalidOperationException("evidence unavailable"),
        };

        var response = await harness.Capture(new ScriptedChatClient()).RunAsync("task-resolver-async-throws");

        Assert.Equal("Hello, world", response.Text);
        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Store.Records);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.Contains(nameof(ExperienceCaptureOptions.ResolveFinalizationAsync), failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_async_resolver_that_returns_a_request_for_another_run_is_refused()
    {
        var foreignRunId = Guid.NewGuid();
        var harness = new Harness { ResolveAsync = async (_, _) => { await Task.Yield(); return ForeignRequest(foreignRunId); } };

        await harness.Capture(new ScriptedChatClient()).RunAsync("task-foreign-run-async");

        Assert.Empty(harness.Finalized);
        Assert.Empty(harness.Store.Records);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.Contains("different run", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalization_pairing_counts_either_resolver_and_refuses_both()
    {
        var harness = new Harness();
        static ExperienceRunDescriptor Run(ExperienceRunContext _) => new("task", TestScope);

        // An async resolver alone, with no service, would silently never finalize...
        var asyncResolverOnly = new ExperienceCaptureOptions
        {
            ResolveRun = Run,
            ResolveFinalizationAsync = static (_, _) => ValueTask.FromResult<FinalizeExperienceRequest?>(null),
        };

        // ...and a service with both resolvers is ambiguous.
        var bothResolvers = new ExperienceCaptureOptions
        {
            ResolveRun = Run,
            FinalizationService = harness.Finalization,
            ResolveFinalization = static _ => null,
            ResolveFinalizationAsync = static (_, _) => ValueTask.FromResult<FinalizeExperienceRequest?>(null),
        };

        foreach (var options in new[] { asyncResolverOnly, bothResolvers })
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new ScriptedAgent().AsBuilder().UseExperienceCapture(harness.Service, options).Build());

            Assert.Contains(nameof(ExperienceCaptureOptions.ResolveFinalization), exception.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ExperienceCaptureOptions.ResolveFinalizationAsync), exception.Message, StringComparison.Ordinal);
        }

        // A service with only the async resolver is complete.
        var serviceAndAsync = new ExperienceCaptureOptions
        {
            ResolveRun = Run,
            CaptureToolCalls = false,
            FinalizationService = harness.Finalization,
            ResolveFinalizationAsync = static (_, _) => ValueTask.FromResult<FinalizeExperienceRequest?>(null),
        };
        Assert.NotNull(new ScriptedAgent().AsBuilder().UseExperienceCapture(harness.Service, serviceAndAsync).Build());
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

    /// <summary>
    /// VG-15. A run the adapter closes at its open-run bound -- from a timer callback, long after the
    /// invocation that left it open returned -- is still handed to the host's finalization, exactly as
    /// a run its own invocation completed would be.
    /// </summary>
    [Fact]
    public async Task A_run_closed_at_its_open_run_bound_is_still_finalized()
    {
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var harness = new Harness { ContinuesRunId = runId, ShouldComplete = _ => false, Clock = clock };

        await harness.Capture(new ScriptedChatClient()).RunAsync("task-left-open");
        Assert.Empty(harness.Finalized);

        Assert.Single(clock.Bounds).Fire();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (harness.Finalized.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The bound's close never reached finalization.");
            await Task.Delay(10);
        }

        var result = Assert.Single(harness.Finalized);
        Assert.Equal(ExperienceFinalizationService.ExperienceIdFor(runId, TestScope), result.ExperienceId);
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Cancelled, run.ExecutionStatus);
        Assert.Contains(harness.Failures, failure => failure.Reason.Contains("still open at its", StringComparison.Ordinal));
    }

    internal sealed class Harness
    {
        private readonly List<ExperienceCaptureFailure> _failures = [];
        private readonly List<FinalizeExperienceResult> _finalized = [];

        public Harness(IExperienceReflector? reflector = null)
        {
            Service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
                new DefaultSanitizer(Sanitization),
                new CaptureLimits(10, 50, 10_000, 10_000)));
            Store = new InMemoryRecordStore();
            Finalization = new ExperienceFinalizationService(
                Service,
                reflector ?? new DefaultExperienceReflector(),
                Store,
                new ExperienceLifecycleService(Store));
        }

        public RecordingCaptureService Service { get; }

        public InMemoryRecordStore Store { get; }

        public ExperienceFinalizationService Finalization { get; }

        public StorageDecision Decision { get; init; } = StorageDecision.Permit;

        public Func<IReadOnlyList<Evidence>> Evidence { get; init; } = () => [PassingEvidence()];

        public Func<ExperienceFinalizationContext, FinalizeExperienceRequest?>? Resolve { get; init; }

        /// <summary>When set, wired as <see cref="ExperienceCaptureOptions.ResolveFinalizationAsync"/> instead of any sync resolver.</summary>
        public Func<ExperienceFinalizationContext, CancellationToken, ValueTask<FinalizeExperienceRequest?>>? ResolveAsync { get; init; }

        public bool ThrowFromOnRunFinalized { get; init; }

        /// <summary>When set, every invocation continues this run rather than opening its own.</summary>
        public Guid? ContinuesRunId { get; init; }

        public Func<ExperienceRunCompletionContext, bool> ShouldComplete { get; init; } = static _ => true;

        public TimeProvider Clock { get; init; } = TimeProvider.System;

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

        /// <summary>As <see cref="Capture(ScriptedChatClient)"/>, handing back the capture lifetime.</summary>
        public AIAgent Capture(ScriptedChatClient client, out IDisposable captureLifetime)
        {
            var inner = new ChatClientAgent(client, new ChatClientAgentOptions());
            return inner.AsBuilder().UseExperienceCapture(Service, Options(), out captureLifetime).Build();
        }

        public AIAgent Capture(ScriptedChatClient client)
        {
            var inner = new ChatClientAgent(client, new ChatClientAgentOptions());
            return inner.AsBuilder().UseExperienceCapture(Service, Options()).Build();
        }

        private ExperienceCaptureOptions Options() => new()
        {
            ResolveRun = context => new ExperienceRunDescriptor(context.Messages.Last().Text, TestScope, ContinuesRunId: ContinuesRunId),
            ShouldCompleteRun = ShouldComplete,
            TimeProvider = Clock,
            CaptureToolCalls = false,
            FinalizationService = Finalization,
            ResolveFinalization = ResolveAsync is null
                ? Resolve ?? (context => RequestFor(context.Run.RunId))
                : Resolve is null ? null : throw new InvalidOperationException("Set Resolve or ResolveAsync, not both."),
            ResolveFinalizationAsync = ResolveAsync,
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
    internal sealed class InMemoryRecordStore : IExperienceRecordStore
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
