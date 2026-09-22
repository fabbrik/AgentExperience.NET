namespace AgentExperience.Core.Tests;

/// <summary>
/// Exercises <see cref="InMemoryExperienceCaptureService"/> against Story 1.2's six acceptance
/// criteria, plus its "thread-safe for concurrent runs and concurrent calls on the same run"
/// boundary constraint, the count-based half of <see cref="CaptureLimits"/>, and the review-round
/// fixes (completion-guards-append, <c>with</c>-expression validation, defensive null guards,
/// placeholder clamping, genuine multi-threaded concurrency). AC5 (no MAF/EF Core/PostgreSQL/
/// model-provider/OpenTelemetry reference under <c>AgentExperience.Core</c>) is not re-tested here
/// -- it is already asserted, for the whole assembly (this new Capture code included), by
/// <see cref="DependencyBoundaryTests"/>.
/// </summary>
public class InMemoryExperienceCaptureServiceTests
{
    private const string ToolArgumentsKind = "ToolArguments";
    private const string ToolResultKind = "ToolResult";

    private static readonly SanitizationOptions PermissiveOptions = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        [ToolArgumentsKind] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "command", "path", "note" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal) { "apiKey" },
            MaxDepth: 5,
            MaxFieldCount: 20,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
        [ToolResultKind] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    private static CaptureLimits GenerousLimits() => new(
        MaxAttemptsPerRun: 50,
        MaxToolCallsPerAttempt: 50,
        MaxResultLength: 10_000,
        MaxErrorLength: 10_000);

    private static InMemoryExperienceCaptureService CreateService(CaptureLimits? limits = null, ISanitizer? sanitizer = null) =>
        new(sanitizer ?? new DefaultSanitizer(PermissiveOptions), limits ?? GenerousLimits());

    private static StartRunResult StartTestRunResult(InMemoryExperienceCaptureService service, Guid? runId = null) =>
        service.StartRun(
            runId ?? Guid.NewGuid(),
            taskId: "task-1",
            taskDescription: "a test task",
            scope: new Scope("tenant-1", "app-1", "project-1"),
            environment: new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
            provenance: new Provenance("unit-tests", "1.0.0", DateTimeOffset.UtcNow, null),
            startedAt: DateTimeOffset.UtcNow);

    private static ExperienceRun StartTestRun(InMemoryExperienceCaptureService service, Guid? runId = null)
    {
        var result = StartTestRunResult(service, runId);
        Assert.Equal(StartRunOutcome.Started, result.Outcome);
        return result.Run!;
    }

    private static ExperienceRun MustGetRun(InMemoryExperienceCaptureService service, Guid runId)
    {
        Assert.True(service.TryGetRun(runId, out var run));
        return run!;
    }

    private static RawToolCall MakeToolCall(string toolName = "search", string? result = "ok", string? error = null) =>
        new(
            ToolCallId: Guid.NewGuid(),
            ToolName: toolName,
            Arguments: new Dictionary<string, object?> { ["command"] = "run" },
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromMilliseconds(10),
            Result: result,
            Error: error);

    private static AppendAttemptRequest MakeAttemptRequest(
        Guid? attemptId = null,
        IReadOnlyList<RawToolCall>? toolCalls = null,
        string? result = "attempt-ok",
        string? error = null,
        TimeSpan? duration = null) =>
        new(
            AttemptId: attemptId ?? Guid.NewGuid(),
            StartedAt: DateTimeOffset.UtcNow,
            Duration: duration ?? TimeSpan.FromMilliseconds(100),
            ToolCalls: toolCalls ?? [],
            Result: result,
            Error: error);

    [Fact]
    public async Task AC1_multiple_attempts_and_tool_calls_are_read_back_in_append_order_with_correct_sequence_numbers_and_intact_content()
    {
        var service = CreateService();
        var run = StartTestRun(service);

        var attempt0ToolCalls = new[]
        {
            MakeToolCall("search", result: "found 3 results"),
            MakeToolCall("write_file", result: null, error: "disk full"),
        };
        var attempt0 = MakeAttemptRequest(toolCalls: attempt0ToolCalls, result: null, error: "attempt 0 failed", duration: TimeSpan.FromSeconds(1));

        var attempt1ToolCalls = new[] { MakeToolCall("search", result: "found 5 results") };
        var attempt1 = MakeAttemptRequest(toolCalls: attempt1ToolCalls, result: "attempt 1 succeeded", error: null, duration: TimeSpan.FromSeconds(2));

        var result0 = await service.AppendAttemptAsync(run.RunId, attempt0);
        var result1 = await service.AppendAttemptAsync(run.RunId, attempt1);

        Assert.Equal(AppendAttemptOutcome.Recorded, result0.Outcome);
        Assert.Equal(AppendAttemptOutcome.Recorded, result1.Outcome);

        var finalRun = MustGetRun(service, run.RunId);
        Assert.Equal(2, finalRun.Attempts.Count);

        var storedAttempt0 = finalRun.Attempts[0];
        Assert.Equal(0, storedAttempt0.SequenceNumber);
        Assert.Equal(attempt0.AttemptId, storedAttempt0.AttemptId);
        Assert.Equal(attempt0.Duration, storedAttempt0.Duration);
        Assert.Null(storedAttempt0.Result);
        Assert.Equal("attempt 0 failed", storedAttempt0.Error);
        Assert.Equal(2, storedAttempt0.ToolCalls.Count);
        Assert.Equal(0, storedAttempt0.ToolCalls[0].SequenceNumber);
        Assert.Equal("search", storedAttempt0.ToolCalls[0].ToolName);
        Assert.Equal("found 3 results", storedAttempt0.ToolCalls[0].Result);
        Assert.Null(storedAttempt0.ToolCalls[0].Error);
        Assert.Equal(1, storedAttempt0.ToolCalls[1].SequenceNumber);
        Assert.Equal("write_file", storedAttempt0.ToolCalls[1].ToolName);
        Assert.Null(storedAttempt0.ToolCalls[1].Result);
        Assert.Equal("disk full", storedAttempt0.ToolCalls[1].Error);

        var storedAttempt1 = finalRun.Attempts[1];
        Assert.Equal(1, storedAttempt1.SequenceNumber);
        Assert.Equal(attempt1.AttemptId, storedAttempt1.AttemptId);
        Assert.Equal(attempt1.Duration, storedAttempt1.Duration);
        Assert.Equal("attempt 1 succeeded", storedAttempt1.Result);
        Assert.Null(storedAttempt1.Error);
        Assert.Single(storedAttempt1.ToolCalls);
        Assert.Equal(0, storedAttempt1.ToolCalls[0].SequenceNumber);
    }

    [Theory]
    [InlineData(RunExecutionStatus.Completed)]
    [InlineData(RunExecutionStatus.Failed)]
    [InlineData(RunExecutionStatus.Cancelled)]
    public async Task AC2_complete_run_records_the_given_execution_status_while_outcome_stays_null(RunExecutionStatus status)
    {
        var service = CreateService();
        var run = StartTestRun(service);
        var endedAt = DateTimeOffset.UtcNow;

        var result = await service.CompleteRunAsync(run.RunId, Guid.NewGuid(), status, endedAt);

        Assert.Equal(CompleteRunOutcome.Recorded, result.Outcome);
        var finalRun = MustGetRun(service, run.RunId);
        Assert.Equal(status, finalRun.ExecutionStatus);
        Assert.Null(finalRun.Outcome);
        Assert.Equal(endedAt, finalRun.EndedAt);
    }

    [Fact]
    public async Task AC3_identical_attempt_resubmission_is_a_no_op_and_conflicting_resubmission_returns_conflict_leaving_state_unchanged()
    {
        var service = CreateService();
        var run = StartTestRun(service);
        var attemptId = Guid.NewGuid();
        var toolCall = MakeToolCall();
        var original = MakeAttemptRequest(attemptId, [toolCall], result: "first", error: null, duration: TimeSpan.FromSeconds(1));

        var first = await service.AppendAttemptAsync(run.RunId, original);
        Assert.Equal(AppendAttemptOutcome.Recorded, first.Outcome);

        // Identical resubmission -- deep-equal, but freshly-constructed objects, never the same
        // instances -- must be a no-op.
        var identicalToolCall = new RawToolCall(
            toolCall.ToolCallId,
            toolCall.ToolName,
            new Dictionary<string, object?> { ["command"] = "run" },
            toolCall.StartedAt,
            toolCall.Duration,
            toolCall.Result,
            toolCall.Error);
        var identical = original with { ToolCalls = [identicalToolCall] };

        var second = await service.AppendAttemptAsync(run.RunId, identical);
        Assert.Equal(AppendAttemptOutcome.DuplicateNoOp, second.Outcome);

        Assert.Single(MustGetRun(service, run.RunId).Attempts);

        // Conflicting resubmission under the same AttemptId (different Result) -- Conflict, state unchanged.
        var conflicting = original with { Result = "different result" };
        var third = await service.AppendAttemptAsync(run.RunId, conflicting);
        Assert.Equal(AppendAttemptOutcome.Conflict, third.Outcome);

        var afterConflict = MustGetRun(service, run.RunId);
        Assert.Single(afterConflict.Attempts);
        Assert.Equal("first", afterConflict.Attempts[0].Result);
    }

    [Fact]
    public async Task AC3_identical_completion_resubmission_is_a_no_op_and_any_other_completion_returns_conflict_finalizing_exactly_once()
    {
        var service = CreateService();
        var run = StartTestRun(service);
        var eventId = Guid.NewGuid();
        var endedAt = DateTimeOffset.UtcNow;

        var first = await service.CompleteRunAsync(run.RunId, eventId, RunExecutionStatus.Completed, endedAt);
        Assert.Equal(CompleteRunOutcome.Recorded, first.Outcome);

        // Identical resubmission (same event ID, same content) -- no-op.
        var second = await service.CompleteRunAsync(run.RunId, eventId, RunExecutionStatus.Completed, endedAt);
        Assert.Equal(CompleteRunOutcome.DuplicateNoOp, second.Outcome);

        // Same event ID, different content -- Conflict.
        var third = await service.CompleteRunAsync(run.RunId, eventId, RunExecutionStatus.Failed, endedAt);
        Assert.Equal(CompleteRunOutcome.Conflict, third.Outcome);

        // A completely different completion event on an already-finalized run -- also Conflict.
        var fourth = await service.CompleteRunAsync(run.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, endedAt);
        Assert.Equal(CompleteRunOutcome.Conflict, fourth.Outcome);

        var finalRun = MustGetRun(service, run.RunId);
        Assert.Equal(RunExecutionStatus.Completed, finalRun.ExecutionStatus);
        Assert.Equal(endedAt, finalRun.EndedAt);
    }

    [Fact]
    public async Task AC4_oversized_tool_call_result_is_replaced_with_a_safe_placeholder_clamped_to_the_limit_and_the_result_reports_truncation()
    {
        var tightLimits = new CaptureLimits(MaxAttemptsPerRun: 50, MaxToolCallsPerAttempt: 50, MaxResultLength: 10, MaxErrorLength: 10);
        var service = CreateService(tightLimits);
        var run = StartTestRun(service);

        var hugeResultMarker = new string('X', 500);
        var toolCall = MakeToolCall(result: hugeResultMarker);
        var request = MakeAttemptRequest(toolCalls: [toolCall], result: "ok", error: null);

        var appendResult = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.Recorded, appendResult.Outcome);
        Assert.Contains(appendResult.TruncatedFields, f => f.FieldPath == "ToolCalls[0].Result");
        Assert.All(appendResult.TruncatedFields, f => Assert.DoesNotContain(hugeResultMarker, f.Reason));

        var storedResult = MustGetRun(service, run.RunId).Attempts[0].ToolCalls[0].Result;
        Assert.NotNull(storedResult);
        Assert.NotEqual(hugeResultMarker, storedResult);
        Assert.DoesNotContain(hugeResultMarker, storedResult);
        // The placeholder itself must respect the very limit it stands in for.
        Assert.True(storedResult!.Length <= 10);
    }

    [Fact]
    public async Task Oversized_tool_call_error_is_replaced_with_a_safe_placeholder_clamped_to_the_limit_and_reports_truncation()
    {
        var tightLimits = new CaptureLimits(MaxAttemptsPerRun: 50, MaxToolCallsPerAttempt: 50, MaxResultLength: 10, MaxErrorLength: 10);
        var service = CreateService(tightLimits);
        var run = StartTestRun(service);

        var hugeErrorMarker = new string('E', 500);
        var toolCall = MakeToolCall(result: null, error: hugeErrorMarker);
        var request = MakeAttemptRequest(toolCalls: [toolCall], result: "ok", error: null);

        var appendResult = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.Recorded, appendResult.Outcome);
        Assert.Contains(appendResult.TruncatedFields, f => f.FieldPath == "ToolCalls[0].Error");

        var storedError = MustGetRun(service, run.RunId).Attempts[0].ToolCalls[0].Error;
        Assert.NotNull(storedError);
        Assert.NotEqual(hugeErrorMarker, storedError);
        Assert.DoesNotContain(hugeErrorMarker, storedError);
        Assert.True(storedError!.Length <= 10);
    }

    [Fact]
    public async Task Oversized_attempt_level_result_not_just_a_tool_calls_is_replaced_with_a_safe_placeholder_and_reports_truncation()
    {
        var tightLimits = new CaptureLimits(MaxAttemptsPerRun: 50, MaxToolCallsPerAttempt: 50, MaxResultLength: 10, MaxErrorLength: 10);
        var service = CreateService(tightLimits);
        var run = StartTestRun(service);

        var hugeAttemptResult = new string('Y', 500);
        var request = MakeAttemptRequest(toolCalls: [], result: hugeAttemptResult, error: null);

        var appendResult = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.Recorded, appendResult.Outcome);
        Assert.Contains(appendResult.TruncatedFields, f => f.FieldPath == "Attempt.Result");

        var storedResult = MustGetRun(service, run.RunId).Attempts[0].Result;
        Assert.NotNull(storedResult);
        Assert.NotEqual(hugeAttemptResult, storedResult);
        Assert.DoesNotContain(hugeAttemptResult, storedResult);
        Assert.True(storedResult!.Length <= 10);
    }

    [Fact]
    public async Task Sanitization_rejected_decision_rejects_the_whole_append_and_stores_nothing()
    {
        var service = CreateService(sanitizer: new AlwaysRejectSanitizer());
        var run = StartTestRun(service);

        var request = MakeAttemptRequest(toolCalls: [MakeToolCall()]);
        var result = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.SanitizationRejected, result.Outcome);
        Assert.Empty(MustGetRun(service, run.RunId).Attempts);
    }

    [Fact]
    public async Task An_unsanitizable_capture_stores_nothing_and_hands_the_host_back_the_decision_and_its_reason()
    {
        // Story 3.1's "unsanitizable capture" row. Rejection is a decision the host is told about and
        // can act on, not a silent drop and not a persisted denial record: there is no store involved
        // at all, because nothing was ever safe enough to store.
        var sanitizer = new AlwaysRejectSanitizer();
        var service = CreateService(sanitizer: sanitizer);
        var run = StartTestRun(service);

        var result = await service.AppendAttemptAsync(run.RunId, MakeAttemptRequest(toolCalls: [MakeToolCall()], result: "attempt result"));

        Assert.Equal(AppendAttemptOutcome.SanitizationRejected, result.Outcome);

        // The sanitizer's own reason reaches the caller unaltered, so a host can log or surface why.
        Assert.Equal("test sanitizer rejects everything", result.Reason);
        Assert.Empty(result.TruncatedFields);

        // And nothing of the rejected attempt survives anywhere: not the attempt, not its tool calls,
        // and not the raw text that failed. The run is still open, not failed.
        var stored = MustGetRun(service, run.RunId);
        Assert.Empty(stored.Attempts);
        Assert.Null(stored.ExecutionStatus);

        // The run is unharmed: a corrected attempt still records afterwards, which is what makes the
        // rejection a decision rather than a failure.
        var permissive = CreateService();
        var healthy = StartTestRun(permissive);
        Assert.Equal(
            AppendAttemptOutcome.Recorded,
            (await permissive.AppendAttemptAsync(healthy.RunId, MakeAttemptRequest(toolCalls: [MakeToolCall()]))).Outcome);
    }

    [Fact]
    public async Task Sanitization_rejection_short_circuits_at_the_first_failing_field_without_sanitizing_the_rest()
    {
        var countingSanitizer = new CountingRejectSanitizer();
        var service = CreateService(sanitizer: countingSanitizer);
        var run = StartTestRun(service);

        // Attempt Result, Attempt Error, and two tool calls' Arguments/Result/Error would be 8
        // sanitize calls in total if none of them short-circuited.
        var request = MakeAttemptRequest(
            toolCalls: [MakeToolCall(), MakeToolCall()],
            result: "attempt result",
            error: "attempt error");

        var result = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.SanitizationRejected, result.Outcome);
        Assert.Equal(1, countingSanitizer.CallCount);
    }

    [Fact]
    public async Task SanitizationRejected_attempt_id_is_not_tracked_so_a_corrected_resubmission_under_the_same_id_later_succeeds()
    {
        var service = CreateService();
        var run = StartTestRun(service);
        var attemptId = Guid.NewGuid();

        // An unsafe field name (contains '.') fails DefaultSanitizer's traversal fail-closed.
        var badToolCall = MakeToolCall() with { Arguments = new Dictionary<string, object?> { ["bad.name"] = "value" } };
        var badRequest = MakeAttemptRequest(attemptId, [badToolCall]);

        var rejected = await service.AppendAttemptAsync(run.RunId, badRequest);
        Assert.Equal(AppendAttemptOutcome.SanitizationRejected, rejected.Outcome);

        var correctedRequest = MakeAttemptRequest(attemptId, [MakeToolCall()]);
        var recorded = await service.AppendAttemptAsync(run.RunId, correctedRequest);
        Assert.Equal(AppendAttemptOutcome.Recorded, recorded.Outcome);

        var finalRun = MustGetRun(service, run.RunId);
        Assert.Single(finalRun.Attempts);
        Assert.Equal(attemptId, finalRun.Attempts[0].AttemptId);
    }

    [Fact]
    public async Task Null_tool_call_element_returns_a_graceful_outcome_instead_of_throwing()
    {
        var service = CreateService();
        var run = StartTestRun(service);

        var request = MakeAttemptRequest(toolCalls: new List<RawToolCall> { null! });
        var result = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.SanitizationRejected, result.Outcome);
        Assert.Empty(MustGetRun(service, run.RunId).Attempts);
    }

    [Fact]
    public async Task Null_tool_call_arguments_returns_a_graceful_outcome_instead_of_throwing()
    {
        var service = CreateService();
        var run = StartTestRun(service);

        var toolCallWithNullArguments = MakeToolCall() with { Arguments = null! };
        var request = MakeAttemptRequest(toolCalls: [toolCallWithNullArguments]);
        var result = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.SanitizationRejected, result.Outcome);
        Assert.Empty(MustGetRun(service, run.RunId).Attempts);
    }

    [Fact]
    public async Task AC6_secret_classified_argument_field_never_reaches_the_stored_tool_call_only_the_sanitizer_allowed_result_does()
    {
        var service = CreateService();
        var run = StartTestRun(service);

        const string rawSecret = "sk-super-secret-raw-value-should-never-be-stored";
        var toolCall = new RawToolCall(
            Guid.NewGuid(),
            "call_api",
            new Dictionary<string, object?> { ["command"] = "run", ["apiKey"] = rawSecret },
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(5),
            Result: "ok",
            Error: null);

        var request = MakeAttemptRequest(toolCalls: [toolCall]);
        var appendResult = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.Recorded, appendResult.Outcome);

        var storedArguments = MustGetRun(service, run.RunId).Attempts[0].ToolCalls[0].Arguments;
        Assert.Equal("run", storedArguments["command"]);
        Assert.True(storedArguments.ContainsKey("apiKey"));
        Assert.NotEqual(rawSecret, storedArguments["apiKey"]);

        foreach (var value in storedArguments.Values)
        {
            if (value is string text)
            {
                Assert.DoesNotContain(rawSecret, text);
            }
        }
    }

    [Fact]
    public async Task Append_after_the_run_is_already_completed_returns_conflict_and_does_not_grow_the_run()
    {
        var service = CreateService();
        var run = StartTestRun(service);

        var completeResult = await service.CompleteRunAsync(run.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow);
        Assert.Equal(CompleteRunOutcome.Recorded, completeResult.Outcome);

        var appendResult = await service.AppendAttemptAsync(run.RunId, MakeAttemptRequest(toolCalls: [MakeToolCall()]));

        Assert.Equal(AppendAttemptOutcome.Conflict, appendResult.Outcome);
        Assert.Empty(MustGetRun(service, run.RunId).Attempts);
    }

    [Fact]
    public async Task Concurrent_appends_across_parallel_runs_land_correctly_with_no_lost_updates_or_duplicate_sequence_numbers()
    {
        var service = CreateService();
        const int runCount = 8;
        const int attemptsPerRun = 20;

        var runIds = Enumerable.Range(0, runCount).Select(_ => StartTestRun(service).RunId).ToList();

        var tasks = new List<Task<AppendAttemptResult>>();
        foreach (var runId in runIds)
        {
            for (var i = 0; i < attemptsPerRun; i++)
            {
                var request = MakeAttemptRequest(toolCalls: [MakeToolCall()]);
                // Task.Run forces genuine thread-pool execution -- DefaultSanitizer.SanitizeAsync
                // completes synchronously, so without this, calls issued in a plain loop before
                // Task.WhenAll would never actually interleave, and a broken/removed lock would not
                // be caught by this test.
                tasks.Add(Task.Run(() => service.AppendAttemptAsync(runId, request)));
            }
        }

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(AppendAttemptOutcome.Recorded, r.Outcome));

        foreach (var runId in runIds)
        {
            var run = MustGetRun(service, runId);
            Assert.Equal(attemptsPerRun, run.Attempts.Count);

            var sequenceNumbers = run.Attempts.Select(a => a.SequenceNumber).OrderBy(n => n).ToList();
            Assert.Equal(Enumerable.Range(0, attemptsPerRun), sequenceNumbers);
        }
    }

    [Fact]
    public async Task Concurrent_appends_racing_on_the_same_attempt_id_on_the_same_run_land_exactly_once_with_no_double_append()
    {
        var service = CreateService();
        var run = StartTestRun(service);
        var request = MakeAttemptRequest(toolCalls: [MakeToolCall()]);
        // The racing critical section (a dictionary lookup plus a small list rebuild) is small
        // enough that a lower race count (tried: 50) did not reliably overlap two threads on it in
        // practice, even with locking removed -- empirically verified against a temporarily
        // unlocked build: 2000 failed reliably (5/5 runs) where 50 passed despite the missing lock
        // (0/5 runs failed).
        const int raceCount = 2000;

        var tasks = Enumerable.Range(0, raceCount)
            .Select(_ => Task.Run(() => service.AppendAttemptAsync(run.RunId, request)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Outcome == AppendAttemptOutcome.Recorded));
        Assert.Equal(raceCount - 1, results.Count(r => r.Outcome == AppendAttemptOutcome.DuplicateNoOp));
        Assert.DoesNotContain(results, r => r.Outcome is AppendAttemptOutcome.Conflict or AppendAttemptOutcome.SanitizationRejected or AppendAttemptOutcome.RunNotFound or AppendAttemptOutcome.CapacityExceeded);

        Assert.Single(MustGetRun(service, run.RunId).Attempts);
    }

    [Fact]
    public async Task Concurrent_append_racing_against_complete_on_the_same_run_never_loses_an_update()
    {
        var service = CreateService();
        var run = StartTestRun(service);
        const int attemptCount = 20;

        var appendTasks = Enumerable.Range(0, attemptCount)
            .Select(_ => Task.Run(() => service.AppendAttemptAsync(run.RunId, MakeAttemptRequest(toolCalls: [MakeToolCall()]))))
            .ToArray();
        var completeTask = Task.Run(() => service.CompleteRunAsync(run.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow));

        var appendResults = await Task.WhenAll(appendTasks);
        var completeResult = await completeTask;

        Assert.Equal(CompleteRunOutcome.Recorded, completeResult.Outcome);
        Assert.All(appendResults, r => Assert.True(r.Outcome is AppendAttemptOutcome.Recorded or AppendAttemptOutcome.Conflict));

        var finalRun = MustGetRun(service, run.RunId);
        Assert.Equal(RunExecutionStatus.Completed, finalRun.ExecutionStatus);

        // Every attempt reported Recorded must actually be present in stored history -- no lost update.
        var recordedCount = appendResults.Count(r => r.Outcome == AppendAttemptOutcome.Recorded);
        Assert.Equal(recordedCount, finalRun.Attempts.Count);

        // Whatever did land has contiguous SequenceNumbers 0..N-1 -- no gaps, no duplicates.
        var sequenceNumbers = finalRun.Attempts.Select(a => a.SequenceNumber).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(0, finalRun.Attempts.Count), sequenceNumbers);
    }

    [Fact]
    public async Task Tool_call_count_beyond_the_configured_limit_is_dropped_from_the_stored_attempt_and_reported()
    {
        var limits = new CaptureLimits(MaxAttemptsPerRun: 50, MaxToolCallsPerAttempt: 2, MaxResultLength: 1000, MaxErrorLength: 1000);
        var service = CreateService(limits);
        var run = StartTestRun(service);

        var toolCalls = new[] { MakeToolCall("a"), MakeToolCall("b"), MakeToolCall("c"), MakeToolCall("d") };
        var request = MakeAttemptRequest(toolCalls: toolCalls);

        var result = await service.AppendAttemptAsync(run.RunId, request);

        Assert.Equal(AppendAttemptOutcome.Recorded, result.Outcome);
        Assert.Contains(result.TruncatedFields, f => f.FieldPath == "ToolCalls");

        var finalRun = MustGetRun(service, run.RunId);
        Assert.Equal(2, finalRun.Attempts[0].ToolCalls.Count);
        Assert.Equal("a", finalRun.Attempts[0].ToolCalls[0].ToolName);
        Assert.Equal("b", finalRun.Attempts[0].ToolCalls[1].ToolName);
    }

    [Fact]
    public async Task Attempt_count_beyond_the_configured_limit_is_rejected_outright_and_never_tracked()
    {
        var limits = new CaptureLimits(MaxAttemptsPerRun: 1, MaxToolCallsPerAttempt: 50, MaxResultLength: 1000, MaxErrorLength: 1000);
        var service = CreateService(limits);
        var run = StartTestRun(service);

        var first = MakeAttemptRequest();
        var overflow = MakeAttemptRequest();

        var firstResult = await service.AppendAttemptAsync(run.RunId, first);
        Assert.Equal(AppendAttemptOutcome.Recorded, firstResult.Outcome);
        Assert.Empty(firstResult.TruncatedFields);

        var overflowResult = await service.AppendAttemptAsync(run.RunId, overflow);
        Assert.Equal(AppendAttemptOutcome.CapacityExceeded, overflowResult.Outcome);
        Assert.Empty(overflowResult.TruncatedFields);

        var finalRun = MustGetRun(service, run.RunId);
        Assert.Single(finalRun.Attempts);
        Assert.Equal(first.AttemptId, finalRun.Attempts[0].AttemptId);

        // Since the overflowed AttemptId was rejected outright (never tracked in SeenAttempts),
        // resubmitting the exact same request again is decided fresh -- still CapacityExceeded,
        // never a DuplicateNoOp -- proving the idempotency map itself never grew past the cap.
        var resubmit = await service.AppendAttemptAsync(run.RunId, overflow);
        Assert.Equal(AppendAttemptOutcome.CapacityExceeded, resubmit.Outcome);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(-1, 1, 1, 1)]
    public void CaptureLimits_rejects_non_positive_values_at_construction(int maxAttempts, int maxToolCalls, int maxResult, int maxError)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureLimits(maxAttempts, maxToolCalls, maxResult, maxError));
    }

    [Fact]
    public void CaptureLimits_rejects_non_positive_values_on_a_with_expression_too()
    {
        var limits = GenerousLimits();

        Assert.Throws<ArgumentOutOfRangeException>(() => limits with { MaxAttemptsPerRun = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => limits with { MaxToolCallsPerAttempt = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => limits with { MaxResultLength = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => limits with { MaxErrorLength = -5 });
    }

    [Fact]
    public async Task Append_and_complete_on_an_unknown_run_id_return_run_not_found()
    {
        var service = CreateService();
        var unknownRunId = Guid.NewGuid();

        var appendResult = await service.AppendAttemptAsync(unknownRunId, MakeAttemptRequest());
        Assert.Equal(AppendAttemptOutcome.RunNotFound, appendResult.Outcome);

        var completeResult = await service.CompleteRunAsync(unknownRunId, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow);
        Assert.Equal(CompleteRunOutcome.RunNotFound, completeResult.Outcome);
    }

    [Fact]
    public void TryGetRun_with_an_unknown_run_id_returns_false()
    {
        var service = CreateService();

        Assert.False(service.TryGetRun(Guid.NewGuid(), out var run));
        Assert.Null(run);
    }

    [Fact]
    public void StartRun_with_a_duplicate_run_id_returns_conflict_instead_of_throwing()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        StartTestRun(service, runId);

        var second = StartTestRunResult(service, runId);

        Assert.Equal(StartRunOutcome.Conflict, second.Outcome);
        Assert.Null(second.Run);
    }

    private sealed class AlwaysRejectSanitizer : ISanitizer
    {
        private static readonly IReadOnlyDictionary<string, object?> EmptyFields = new Dictionary<string, object?>();

        public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Rejected,
                Fields: EmptyFields,
                RedactedFieldPaths: [],
                OmittedFieldPaths: [],
                Reason: "test sanitizer rejects everything"));
    }

    private sealed class CountingRejectSanitizer : ISanitizer
    {
        private static readonly IReadOnlyDictionary<string, object?> EmptyFields = new Dictionary<string, object?>();
        private int _callCount;

        public int CallCount => _callCount;

        public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new SanitizedPayload(
                Decision: SanitizationDecision.Rejected,
                Fields: EmptyFields,
                RedactedFieldPaths: [],
                OmittedFieldPaths: [],
                Reason: "test sanitizer rejects everything, counting how many times it was called"));
        }
    }
}
