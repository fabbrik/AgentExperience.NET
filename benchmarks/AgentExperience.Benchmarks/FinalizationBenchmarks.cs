using AgentExperience.Benchmarks.Infrastructure;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.Storage.Conformance;
using BenchmarkDotNet.Attributes;

namespace AgentExperience.Benchmarks;

/// <summary>
/// <see cref="ExperienceFinalizationService.FinalizeAsync"/> of a completed, captured run with two attempts and four
/// tool calls (the first attempt failed, the second passed): verification against a closed round, the default
/// reflector, the record's creation and its initial lifecycle transition. Capturing the runs is not timed: each
/// iteration's <see cref="CaptureRuns"/> captures exactly the runs its invocations finalize, into a new capture service
/// (and, in memory, a new store), so nothing an iteration leaves behind grows the heap the next one is timed on.
/// </summary>
[MemoryDiagnoser]
[InvocationCount(RunsPerIteration)]
public class FinalizationBenchmarks
{
    private const int RunsPerIteration = 4_096;
    private const string ArtifactRevision = "rev-1";
    private const string CheckId = "unit-tests-pass";

    private static readonly ClosedVerificationRound Round = new(Guid.Parse("33333333-3333-4333-8333-333333333333"), ArtifactRevision);

    private static readonly RequiredCheck[] RequiredChecks = [new(CheckId, "TestResult")];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "query", "ticketId", "strategy" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal) { "apiKey" },
            MaxDepth: 3,
            MaxFieldCount: 10,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    private readonly Queue<Guid> _runs = new(RunsPerIteration);
    private InMemoryExperienceCaptureService _capture = null!;
    private ExperienceFinalizationService _finalization = null!;

    [ParamsSource(typeof(Stores), nameof(Stores.All))]
    public StoreKind Store { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        await NewServicesAsync();
        var check = await _finalization.FinalizeAsync(Request(await CaptureRunAsync()));
        if (check.Outcome != FinalizationOutcome.Validated || !check.IsDurable)
        {
            throw new InvalidOperationException($"Finalization setup check failed: {check.Outcome} at {check.Stage}.");
        }
    }

    [IterationSetup]
    public void CaptureRuns()
    {
        NewServicesAsync().GetAwaiter().GetResult();
        _runs.Clear();
        for (var i = 0; i < RunsPerIteration; i++)
        {
            _runs.Enqueue(CaptureRunAsync().GetAwaiter().GetResult());
        }
    }

    [Benchmark]
    public Task<FinalizeExperienceResult> Finalize() => _finalization.FinalizeAsync(Request(_runs.Dequeue()));

    private async Task NewServicesAsync()
    {
        var dataset = await BenchmarkData.WritesAsync(Store);
        _capture = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(Sanitization),
            new CaptureLimits(MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 10_000, MaxErrorLength: 10_000));
        _finalization = new ExperienceFinalizationService(
            _capture, new DefaultExperienceReflector(), dataset.Records, new ExperienceLifecycleService(dataset.Records));
    }

    private static FinalizeExperienceRequest Request(Guid runId) => new(
        RunId: runId,
        Authorization: BenchmarkData.Authorization,
        ClosedRound: Round,
        RequiredChecks: RequiredChecks,
        Evidence: [new Evidence(Guid.NewGuid(), Round.RoundId, ArtifactRevision, CheckId, "TestResult", CheckResult.Pass, "ci", "42 of 42 passed", ConformanceData.Time)],
        CurrentArtifactRevision: ArtifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: ConformanceData.Time.AddMinutes(5));

    private async Task<Guid> CaptureRunAsync()
    {
        var runId = Guid.NewGuid();
        var at = ConformanceData.Time;
        var started = _capture.StartRun(
            runId,
            taskId: "refund-ticket-resolution",
            taskDescription: "Refund ticket RF-4821 is stuck on a database lock.",
            scope: BenchmarkData.Scope,
            environment: new EnvironmentFingerprint("worker-01", "net10.0", "linux-x64", "1.2.3", new Dictionary<string, string> { ["region"] = "us-east" }),
            provenance: new Provenance("benchmarks", "1.0.0", at, "trace-1"),
            startedAt: at);
        Require(started.Outcome == StartRunOutcome.Started, $"StartRun returned {started.Outcome}");

        var failed = await _capture.AppendAttemptAsync(runId, new AppendAttemptRequest(
            AttemptId: Guid.NewGuid(),
            StartedAt: at,
            Duration: TimeSpan.FromSeconds(2),
            ToolCalls:
            [
                Call("search_docs", new Dictionary<string, object?> { ["query"] = "refund lock", ["apiKey"] = "not-a-real-key" }, at, "3 documents", null),
                Call("post_refund", new Dictionary<string, object?> { ["ticketId"] = "RF-4821", ["strategy"] = "immediate" }, at.AddSeconds(1), null, "System.TimeoutException"),
            ],
            Result: null,
            Error: "post_refund timed out while the ledger was locked"));
        Require(failed.Outcome == AppendAttemptOutcome.Recorded, $"AppendAttemptAsync returned {failed.Outcome}");

        var passed = await _capture.AppendAttemptAsync(runId, new AppendAttemptRequest(
            AttemptId: Guid.NewGuid(),
            StartedAt: at.AddSeconds(5),
            Duration: TimeSpan.FromSeconds(3),
            ToolCalls:
            [
                Call("wait_for_lock", new Dictionary<string, object?> { ["ticketId"] = "RF-4821" }, at.AddSeconds(5), "released", null),
                Call("post_refund", new Dictionary<string, object?> { ["ticketId"] = "RF-4821", ["strategy"] = "after-lock" }, at.AddSeconds(7), "posted", null),
            ],
            Result: "refund posted",
            Error: null));
        Require(passed.Outcome == AppendAttemptOutcome.Recorded, $"AppendAttemptAsync returned {passed.Outcome}");

        var completed = await _capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, at.AddMinutes(1));
        Require(completed.Outcome == CompleteRunOutcome.Recorded, $"CompleteRunAsync returned {completed.Outcome}");
        return runId;
    }

    private static RawToolCall Call(string name, Dictionary<string, object?> arguments, DateTimeOffset at, string? result, string? error) =>
        new(ToolCallId: Guid.NewGuid(), ToolName: name, Arguments: arguments, StartedAt: at, Duration: TimeSpan.FromMilliseconds(120), Result: result, Error: error);

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Capturing a benchmark run failed: {what}.");
        }
    }
}
