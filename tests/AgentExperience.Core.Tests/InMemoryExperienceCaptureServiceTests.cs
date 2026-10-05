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

    private static StartRunResult StartTestRunResult(
        InMemoryExperienceCaptureService service,
        Guid? runId = null,
        string taskId = "task-1",
        Scope? scope = null,
        string? taskDescription = "a test task",
        DateTimeOffset? startedAt = null) =>
        service.StartRun(
            runId ?? Guid.NewGuid(),
            taskId: taskId,
            taskDescription: taskDescription,
            scope: scope ?? new Scope("tenant-1", "app-1", "project-1"),
            environment: new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
            provenance: new Provenance("unit-tests", "1.0.0", DateTimeOffset.UtcNow, null),
            startedAt: startedAt ?? DateTimeOffset.UtcNow);

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
    public void StartRun_on_the_same_still_open_run_continues_it_and_overwrites_nothing()
    {
        // Story 4.6: one task retried across two framework invocations is one run with two attempts,
        // not two runs -- so a second StartRun naming the same still-open run, for the same task in
        // the same scope, is a continuation rather than a collision.
        var service = CreateService();
        var runId = Guid.NewGuid();
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var first = StartTestRunResult(service, runId, startedAt: openedAt);
        Assert.Equal(StartRunOutcome.Started, first.Outcome);

        var second = StartTestRunResult(service, runId, taskDescription: "a different description", startedAt: DateTimeOffset.UtcNow);

        Assert.Equal(StartRunOutcome.Continued, second.Outcome);
        Assert.NotNull(second.Run);

        // A continuation reads; it never writes. The run keeps the description and the start time the
        // first call opened it with, so continuing cannot quietly rewrite a run's own history.
        Assert.Equal("a test task", second.Run!.TaskDescription);
        Assert.Equal(openedAt, second.Run.StartedAt);
        Assert.Empty(second.Run.Attempts);
    }

    [Fact]
    public async Task A_continued_run_keeps_accumulating_attempts_with_the_next_sequence_number()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        StartTestRun(service, runId);
        await service.AppendAttemptAsync(runId, MakeAttemptRequest(result: null, error: "System.TimeoutException"));

        Assert.Equal(StartRunOutcome.Continued, StartTestRunResult(service, runId).Outcome);
        var second = await service.AppendAttemptAsync(runId, MakeAttemptRequest(result: "worked"));

        Assert.Equal(AppendAttemptOutcome.Recorded, second.Outcome);
        var run = MustGetRun(service, runId);
        Assert.Equal([0, 1], run.Attempts.Select(attempt => attempt.SequenceNumber).ToArray());
        Assert.Equal("System.TimeoutException", run.Attempts[0].Error);
        Assert.Equal("worked", run.Attempts[1].Result);
    }

    [Fact]
    public void StartRun_with_a_duplicate_run_id_for_a_different_task_returns_conflict_instead_of_throwing()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        StartTestRun(service, runId);

        var second = StartTestRunResult(service, runId, taskId: "task-2");

        Assert.Equal(StartRunOutcome.Conflict, second.Outcome);
        Assert.Null(second.Run);
    }

    [Fact]
    public void StartRun_with_a_duplicate_run_id_in_a_different_scope_returns_conflict()
    {
        // Matching on the task alone would let one tenant continue another tenant's run.
        var service = CreateService();
        var runId = Guid.NewGuid();
        StartTestRun(service, runId);

        var second = StartTestRunResult(service, runId, scope: new Scope("tenant-2", "app-1", "project-1"));

        Assert.Equal(StartRunOutcome.Conflict, second.Outcome);
        Assert.Null(second.Run);
    }

    /// <summary>
    /// A continuation must match the run's task id ordinally and every one of its six scope fields.
    /// Each case below differs from the open run in exactly one of them -- including a task id that
    /// differs only by case -- and each is a collision, never a continuation.
    /// </summary>
    [Theory]
    [InlineData("task-case")]
    [InlineData("tenant")]
    [InlineData("application")]
    [InlineData("project")]
    [InlineData("team")]
    [InlineData("agent")]
    [InlineData("user")]
    public async Task StartRun_differing_from_the_open_run_in_any_one_identity_field_is_a_conflict_that_writes_nothing(string field)
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        var scope = new Scope("tenant-1", "app-1", "project-1", "team-1", "agent-1", "user-1");
        Assert.Equal(StartRunOutcome.Started, StartTestRunResult(service, runId, taskId: "task-1", scope: scope).Outcome);
        await service.AppendAttemptAsync(runId, MakeAttemptRequest());

        var (taskId, otherScope) = field switch
        {
            "task-case" => ("TASK-1", scope),
            "tenant" => ("task-1", scope with { TenantId = "tenant-2" }),
            "application" => ("task-1", scope with { ApplicationId = "app-2" }),
            "project" => ("task-1", scope with { ProjectId = "project-2" }),
            "team" => ("task-1", scope with { TeamId = "team-2" }),
            "agent" => ("task-1", scope with { AgentId = "agent-2" }),
            _ => ("task-1", scope with { UserId = "user-2" }),
        };

        var second = StartTestRunResult(service, runId, taskId: taskId, scope: otherScope);

        Assert.Equal(StartRunOutcome.Conflict, second.Outcome);
        Assert.Null(second.Run);
        var run = MustGetRun(service, runId);
        Assert.Equal("task-1", run.TaskId);
        Assert.Equal(scope, run.Scope);
        Assert.Single(run.Attempts);

        // The exact identity still continues, so the refusals above are about the one field changed.
        Assert.Equal(StartRunOutcome.Continued, StartTestRunResult(service, runId, taskId: "task-1", scope: scope).Outcome);
    }

    /// <summary>
    /// A continuation reads the run and writes nothing: the environment and provenance the run was
    /// opened with -- its correlation id included -- survive a later StartRun that carries different
    /// ones, both in what the continuation returns and in what is stored.
    /// </summary>
    [Fact]
    public void Continuing_a_run_keeps_its_original_environment_and_provenance_including_the_correlation_id()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        var scope = new Scope("tenant-1", "app-1", "project-1");
        var openedEnvironment = new EnvironmentFingerprint("host-1", "net10.0", "os-1", "1.0.0", new Dictionary<string, string> { ["region"] = "eu" });
        var openedProvenance = new Provenance("opener", "1.0.0", DateTimeOffset.UtcNow.AddMinutes(-2), "0af7651916cd43dd8448eb211c80319c");

        var first = service.StartRun(runId, "task-1", "a test task", scope, openedEnvironment, openedProvenance, DateTimeOffset.UtcNow.AddMinutes(-2));
        Assert.Equal(StartRunOutcome.Started, first.Outcome);

        var second = service.StartRun(
            runId,
            "task-1",
            "a test task",
            scope,
            new EnvironmentFingerprint("host-2", "net11.0", "os-2", "2.0.0", new Dictionary<string, string> { ["region"] = "us" }),
            new Provenance("continuer", "2.0.0", DateTimeOffset.UtcNow, "4bf92f3577b34da6a3ce929d0e0e4736"),
            DateTimeOffset.UtcNow);

        Assert.Equal(StartRunOutcome.Continued, second.Outcome);
        foreach (var run in new[] { second.Run!, MustGetRun(service, runId) })
        {
            Assert.Same(openedEnvironment, run.Environment);
            Assert.Equal(openedProvenance, run.Provenance);
            Assert.Equal("0af7651916cd43dd8448eb211c80319c", run.Provenance.CorrelationId);
        }
    }

    [Fact]
    public async Task StartRun_on_an_already_completed_run_returns_conflict_and_the_run_stays_finalized()
    {
        // A run finalizes exactly once and is never reopened. Refusing here means a caller that meant
        // to continue learns so before it captures anything -- and the deeper invariant, that
        // AppendAttemptAsync refuses a finalized run whatever it is handed, still holds underneath.
        var service = CreateService();
        var runId = Guid.NewGuid();
        StartTestRun(service, runId);
        await service.AppendAttemptAsync(runId, MakeAttemptRequest());
        Assert.Equal(CompleteRunOutcome.Recorded, (await service.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow)).Outcome);

        var continued = StartTestRunResult(service, runId);

        Assert.Equal(StartRunOutcome.Conflict, continued.Outcome);
        Assert.Null(continued.Run);

        var appended = await service.AppendAttemptAsync(runId, MakeAttemptRequest());
        Assert.Equal(AppendAttemptOutcome.Conflict, appended.Outcome);
        Assert.Single(MustGetRun(service, runId).Attempts);
    }

    // -- Story 16.1: bounding the completed runs held --

    private static InMemoryExperienceCaptureService CreateRetainingService(ManualClock clock, int maxRetained = 10_000, TimeSpan? retention = null) =>
        new(
            new DefaultSanitizer(PermissiveOptions),
            GenerousLimits() with
            {
                MaxRetainedCompletedRuns = maxRetained,
                CompletedRunRetention = retention ?? TimeSpan.FromHours(24),
            },
            clock);

    private static async Task<Guid> StartAndCompleteAsync(InMemoryExperienceCaptureService service, ManualClock clock, Guid? runId = null)
    {
        var id = runId ?? Guid.NewGuid();
        StartTestRun(service, id);
        Assert.Equal(CompleteRunOutcome.Recorded, (await service.CompleteRunAsync(id, Guid.NewGuid(), RunExecutionStatus.Completed, clock.GetUtcNow())).Outcome);
        return id;
    }

    [Fact]
    public void CaptureLimits_retention_bounds_default_to_ten_thousand_runs_and_twenty_four_hours()
    {
        var limits = GenerousLimits();

        Assert.Equal(10_000, limits.MaxRetainedCompletedRuns);
        Assert.Equal(TimeSpan.FromHours(24), limits.CompletedRunRetention);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CaptureLimits_rejects_a_non_positive_retained_run_count(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => GenerousLimits() with { MaxRetainedCompletedRuns = value });
        Assert.Equal(nameof(CaptureLimits.MaxRetainedCompletedRuns), ex.ParamName);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void CaptureLimits_rejects_a_non_positive_retention(long ticks)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => GenerousLimits() with { CompletedRunRetention = TimeSpan.FromTicks(ticks) });
        Assert.Equal(nameof(CaptureLimits.CompletedRunRetention), ex.ParamName);
    }

    [Fact]
    public void The_service_rejects_a_null_time_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new InMemoryExperienceCaptureService(new DefaultSanitizer(PermissiveOptions), GenerousLimits(), null!));
    }

    [Fact]
    public async Task A_completed_run_older_than_the_retention_is_dropped_on_the_next_call_while_open_runs_stay()
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, retention: TimeSpan.FromMinutes(10));
        var completed = await StartAndCompleteAsync(service, clock);
        var open = Guid.NewGuid();
        StartTestRun(service, open);

        clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.True(service.TryGetRun(completed, out _));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(service.TryGetRun(completed, out _));
        await AssertAnswersAsUnknownAsync(service, completed, Guid.NewGuid());

        // The open run is older still, and is never dropped by the retention bound.
        clock.Advance(TimeSpan.FromDays(30));
        Assert.True(service.TryGetRun(open, out _));
        Assert.Equal(AppendAttemptOutcome.Recorded, (await service.AppendAttemptAsync(open, MakeAttemptRequest())).Outcome);
    }

    public static TheoryData<string> ServiceMethods => new() { "TryGetRun", "AppendAttempt", "CompleteRun", "RecordExposure", "StartRun" };

    [Theory]
    [MemberData(nameof(ServiceMethods))]
    public async Task An_expired_run_is_dropped_whichever_method_is_the_first_call_after_it_expires(string method)
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, retention: TimeSpan.FromMinutes(10));
        var runId = await StartAndCompleteAsync(service, clock);
        Assert.True(service.TryGetRun(runId, out _));

        clock.Advance(TimeSpan.FromMinutes(10));

        // While held, every one of these calls would answer Conflict / Found; dropped, each answers as for an unknown run.
        switch (method)
        {
            case "TryGetRun":
                Assert.False(service.TryGetRun(runId, out _));
                break;
            case "AppendAttempt":
                Assert.Equal(AppendAttemptOutcome.RunNotFound, (await service.AppendAttemptAsync(runId, MakeAttemptRequest())).Outcome);
                break;
            case "CompleteRun":
                Assert.Equal(CompleteRunOutcome.RunNotFound, (await service.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow)).Outcome);
                break;
            case "RecordExposure":
                Assert.Equal(RecordExposureOutcome.RunNotFound, service.RecordExposure(runId, [new RunExposure(Guid.NewGuid(), 1)]).Outcome);
                break;
            case "StartRun":
                var restarted = StartTestRunResult(service, runId);
                Assert.Equal(StartRunOutcome.Started, restarted.Outcome);
                Assert.Empty(restarted.Run!.Attempts);
                Assert.Null(restarted.Run.ExecutionStatus);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }

        Assert.False(service.TryGetRun(runId, out _));
    }

    [Fact]
    public async Task A_held_completed_run_is_still_refused_on_reuse_and_on_a_second_completion()
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, retention: TimeSpan.FromMinutes(10));
        var runId = await StartAndCompleteAsync(service, clock);

        clock.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(StartRunOutcome.Conflict, StartTestRunResult(service, runId).Outcome);
        Assert.Equal(CompleteRunOutcome.Conflict, (await service.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow)).Outcome);
        Assert.Equal(AppendAttemptOutcome.Conflict, (await service.AppendAttemptAsync(runId, MakeAttemptRequest())).Outcome);
    }

    [Fact]
    public async Task Age_is_measured_from_when_the_service_saw_the_completion_not_from_the_run_s_own_timestamps()
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, retention: TimeSpan.FromMinutes(10));
        var runId = Guid.NewGuid();
        StartTestRunResult(service, runId, startedAt: DateTimeOffset.UnixEpoch);

        clock.Advance(TimeSpan.FromHours(5));   // open for hours: not counted
        Assert.Equal(CompleteRunOutcome.Recorded, (await service.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Failed, DateTimeOffset.UnixEpoch)).Outcome);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(service.TryGetRun(runId, out _));
    }

    [Fact]
    public async Task A_wall_clock_step_back_or_forward_does_not_change_when_a_completed_run_is_dropped()
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, retention: TimeSpan.FromMinutes(10));

        var first = await StartAndCompleteAsync(service, clock);
        clock.StepWallClock(TimeSpan.FromHours(-3));            // wall clock jumps back: age is not reset
        clock.Advance(TimeSpan.FromMinutes(2));
        var second = await StartAndCompleteAsync(service, clock);
        clock.StepWallClock(TimeSpan.FromDays(2));              // wall clock jumps forward: nothing expires early

        clock.Advance(TimeSpan.FromMinutes(8) - TimeSpan.FromTicks(1));
        Assert.True(service.TryGetRun(first, out _));
        Assert.True(service.TryGetRun(second, out _));

        clock.Advance(TimeSpan.FromTicks(1));                   // 10 minutes after `first` completed
        Assert.False(service.TryGetRun(first, out _));
        Assert.True(service.TryGetRun(second, out _));

        clock.StepWallClock(TimeSpan.FromHours(-5));
        clock.Advance(TimeSpan.FromMinutes(2));                 // 10 minutes after `second` completed
        Assert.False(service.TryGetRun(second, out _));
    }

    [Fact]
    public async Task Past_the_count_bound_the_earliest_completed_run_is_dropped_first_and_open_runs_are_untouched()
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, maxRetained: 3);
        var open = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in open)
        {
            StartTestRun(service, id);
        }

        var completed = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            completed.Add(await StartAndCompleteAsync(service, clock));
        }

        Assert.False(service.TryGetRun(completed[0], out _));
        Assert.False(service.TryGetRun(completed[1], out _));
        Assert.All(completed.Skip(2), id => Assert.True(service.TryGetRun(id, out _)));
        Assert.All(open, id => Assert.True(service.TryGetRun(id, out _)));
        await AssertAnswersAsUnknownAsync(service, completed[0], Guid.NewGuid());

        // Completing an open run counts it from then on, and pushes the next-earliest completed out.
        Assert.Equal(CompleteRunOutcome.Recorded, (await service.CompleteRunAsync(open[0], Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow)).Outcome);
        Assert.False(service.TryGetRun(completed[2], out _));
        Assert.True(service.TryGetRun(open[0], out _));
    }

    [Fact]
    public async Task An_id_reused_after_its_run_was_dropped_is_retained_as_its_own_new_run()
    {
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, retention: TimeSpan.FromMinutes(10));
        var reused = await StartAndCompleteAsync(service, clock);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(service.TryGetRun(reused, out _));

        // Same ID, a new run, completed later: only its own completion time counts.
        clock.Advance(TimeSpan.FromMinutes(5));
        await StartAndCompleteAsync(service, clock, reused);
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(service.TryGetRun(reused, out _));
    }

    [Fact]
    public async Task Soak_fifty_thousand_runs_never_hold_more_completed_runs_than_the_bound_and_open_runs_stay_intact()
    {
        const int Bound = 500;
        const int Runs = 50_000;
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, maxRetained: Bound, retention: TimeSpan.FromMinutes(30));

        // Open runs held throughout: older than the retention many times over by the end.
        var open = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in open)
        {
            StartTestRun(service, id);
            Assert.Equal(AppendAttemptOutcome.Recorded, (await service.AppendAttemptAsync(id, MakeAttemptRequest())).Outcome);
        }

        var completed = new List<Guid>(Runs);
        for (var i = 1; i <= Runs; i++)
        {
            completed.Add(await StartAndCompleteAsync(service, clock));
            clock.Advance(TimeSpan.FromMilliseconds(100));

            if (i % 2_500 == 0)
            {
                Assert.InRange(completed.Count(id => service.TryGetRun(id, out _)), 1, Bound);
            }
        }

        Assert.Equal(Bound, completed.Count(id => service.TryGetRun(id, out _)));
        Assert.True(service.TryGetRun(completed[^1], out _));

        // And the age bound empties what is left once it has all aged out.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.DoesNotContain(completed, id => service.TryGetRun(id, out _));

        foreach (var id in open)
        {
            var run = MustGetRun(service, id);
            Assert.Single(run.Attempts);
            Assert.Null(run.ExecutionStatus);
            Assert.Equal(CompleteRunOutcome.Recorded, (await service.CompleteRunAsync(id, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow)).Outcome);
        }
    }

    [Fact]
    public async Task Concurrent_completions_across_many_runs_stay_within_the_bound()
    {
        const int Bound = 64;
        var clock = new ManualClock();
        var service = CreateRetainingService(clock, maxRetained: Bound);
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 4_000), async (_, _) => ids.Add(await StartAndCompleteAsync(service, clock)));

        Assert.Equal(Bound, ids.Count(id => service.TryGetRun(id, out _)));
    }

    /// <summary>A dropped run must get, on every method, the answer an ID the service never saw gets.</summary>
    private static async Task AssertAnswersAsUnknownAsync(InMemoryExperienceCaptureService service, Guid dropped, Guid unknown)
    {
        foreach (var id in new[] { dropped, unknown })
        {
            Assert.False(service.TryGetRun(id, out _));
            Assert.Equal(AppendAttemptOutcome.RunNotFound, (await service.AppendAttemptAsync(id, MakeAttemptRequest())).Outcome);
            Assert.Equal(CompleteRunOutcome.RunNotFound, (await service.CompleteRunAsync(id, Guid.NewGuid(), RunExecutionStatus.Completed, DateTimeOffset.UtcNow)).Outcome);
            Assert.Equal(RecordExposureOutcome.RunNotFound, service.RecordExposure(id, [new RunExposure(Guid.NewGuid(), 1)]).Outcome);
        }
    }

    /// <summary>
    /// A clock that moves only when told to: <see cref="Advance"/> moves both the wall clock and the
    /// monotonic timestamp; <see cref="StepWallClock"/> moves only the wall clock, as an NTP or manual
    /// clock change would.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            _now += by;
            _timestamp += by.Ticks;
        }

        public void StepWallClock(TimeSpan by) => _now += by;
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
