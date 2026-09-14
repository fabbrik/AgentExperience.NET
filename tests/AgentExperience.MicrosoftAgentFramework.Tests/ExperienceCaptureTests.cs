using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentExperience.MicrosoftAgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 1.6: real <see cref="ChatClientAgent"/> execution against a scripted fake model, captured
/// through the real <see cref="InMemoryExperienceCaptureService"/> and <see cref="DefaultSanitizer"/>.
/// One test per I/O matrix row, plus the pinned-version, session, run-ID-before-first-call, and
/// non-<see cref="ChatClientAgent"/> checks. No database or model credentials are needed.
/// </summary>
public class ExperienceCaptureTests
{
    private const string EchoTool = "echo";
    private const string FailTool = "fail_tool";
    private const string SlowEchoTool = "slow_echo";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "note" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal) { "apiKey" },
            MaxDepth: 5,
            MaxFieldCount: 20,
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

    private static readonly AIFunction Echo = AIFunctionFactory.Create(
        (string note, string apiKey) => $"echo:{note}",
        EchoTool);

    private static readonly AIFunction Fail = AIFunctionFactory.Create(
        new Func<string, string>(note => throw new ToolFailureException($"secret detail for {note}")),
        FailTool);

    private static readonly AIFunction SlowEcho = AIFunctionFactory.Create(
        async (string note) =>
        {
            await Task.Delay(Random.Shared.Next(1, 15));
            return $"echo:{note}";
        },
        SlowEchoTool);

    private const string PocoToolName = "poco_tool";
    private const string NullToolName = "null_tool";
    private static readonly ToolPoco PocoResult = new("widget", 2);
    private static readonly AIFunction PocoTool = new RawResultFunction(PocoToolName, PocoResult);
    private static readonly AIFunction NullTool = new RawResultFunction(NullToolName, null);

    private static ScriptedCall EchoCall() => new(EchoTool, text => new Dictionary<string, object?> { ["note"] = text, ["apiKey"] = "sk-live-123" });

    private sealed class Harness
    {
        private readonly List<ExperienceCaptureFailure> _failures = [];

        public Harness()
        {
            Service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
                new DefaultSanitizer(Sanitization),
                new CaptureLimits(MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 10_000, MaxErrorLength: 10_000)));
        }

        public RecordingCaptureService Service { get; }

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

        public ExperienceCaptureOptions Options(
            Func<ExperienceRunContext, ExperienceRunDescriptor>? resolve = null,
            bool captureToolCalls = true,
            TimeSpan? finalizationTimeout = null,
            bool throwingCallback = false,
            Func<Guid>? newId = null) => new()
            {
                NewId = newId ?? Guid.NewGuid,
                ResolveRun = resolve ?? (context => new ExperienceRunDescriptor(context.Messages.Last().Text, TestScope, "captured by tests")),
                CaptureToolCalls = captureToolCalls,
                FinalizationTimeout = finalizationTimeout ?? TimeSpan.FromSeconds(5),
                OnCaptureFailure = failure =>
                {
                    lock (_failures)
                    {
                        _failures.Add(failure);
                    }

                    if (throwingCallback)
                    {
                        throw new InvalidOperationException("host callback failure");
                    }
                },
            };

        public AIAgent Capture(AIAgent inner, ExperienceCaptureOptions? options = null) =>
            inner.AsBuilder().UseExperienceCapture(Service, options ?? Options()).Build();

        public ExperienceRun SingleRun()
        {
            var runId = Assert.Single(Service.StartedRunIds);
            Assert.True(Service.TryGetRun(runId, out var run));
            return run;
        }
    }

    private static ChatClientAgent CreateAgent(ScriptedChatClient client, bool allowConcurrentInvocation = false) =>
        new(client, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Tools = [Echo, Fail, SlowEcho, PocoTool, NullTool] },
            AllowConcurrentInvocation = allowConcurrentInvocation,
        });

    private static string Shape(AgentResponse response) =>
        JsonSerializer.Serialize(response.Messages.Select(m => new
        {
            Role = m.Role.Value,
            m.Text,
            Contents = m.Contents.Select(c => c switch
            {
                FunctionCallContent call => $"call:{call.CallId}:{call.Name}",
                FunctionResultContent result => $"result:{result.CallId}:{result.Result}",
                _ => c.GetType().Name,
            }).ToList(),
        }));

    private static string Shape(IEnumerable<AgentResponseUpdate> updates) =>
        JsonSerializer.Serialize(updates.Select(u => new
        {
            Role = u.Role?.Value,
            u.Text,
            Contents = u.Contents.Select(c => c switch
            {
                FunctionCallContent call => $"call:{call.CallId}:{call.Name}",
                FunctionResultContent result => $"result:{result.CallId}:{result.Result}",
                _ => c.GetType().Name,
            }).ToList(),
        }));

    private static async Task<List<AgentResponseUpdate>> CollectAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        var list = new List<AgentResponseUpdate>();
        await foreach (var update in updates)
        {
            list.Add(update);
        }

        return list;
    }

    // ---- Matrix: Ordinary success ----------------------------------------------------------------

    [Fact]
    public async Task Ordinary_success_captures_a_completed_run_with_one_attempt_and_a_sanitized_tool_call()
    {
        var harness = new Harness();
        var client = new ScriptedChatClient { Calls = [EchoCall()] };
        var inner = CreateAgent(client);

        var expected = await inner.RunAsync("task-ordinary");
        var response = await harness.Capture(inner).RunAsync("task-ordinary");

        Assert.Equal(Shape(expected), Shape(response));
        Assert.Equal(expected.Text, response.Text);

        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal("task-ordinary", run.TaskId);
        Assert.Equal(CaptureScope.ProvenanceSource, run.Provenance.Source);
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal(response.Text, attempt.Result);
        Assert.Null(attempt.Error);

        var toolCall = Assert.Single(attempt.ToolCalls);
        Assert.Equal(EchoTool, toolCall.ToolName);
        Assert.Equal("echo:task-ordinary", toolCall.Result);
        Assert.Null(toolCall.Error);
        Assert.Equal("task-ordinary", toolCall.Arguments["note"]?.ToString());
        Assert.NotEqual("sk-live-123", toolCall.Arguments["apiKey"]?.ToString());
        Assert.Empty(harness.Failures);
    }

    // ---- Matrix: Streaming success ---------------------------------------------------------------

    [Fact]
    public async Task Streaming_success_captures_the_same_run_and_yields_updates_unchanged()
    {
        var harness = new Harness();
        var client = new ScriptedChatClient { Calls = [EchoCall()] };
        var inner = CreateAgent(client);

        var expected = await CollectAsync(inner.RunStreamingAsync("task-streaming"));
        var updates = await CollectAsync(harness.Capture(inner).RunStreamingAsync("task-streaming"));

        Assert.Equal(Shape(expected), Shape(updates));

        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal("Hello, world", attempt.Result);
        Assert.Equal(string.Concat(updates.Select(u => u.Text)), attempt.Result);
        var toolCall = Assert.Single(attempt.ToolCalls);
        Assert.Equal("echo:task-streaming", toolCall.Result);
        Assert.Empty(harness.Failures);
    }

    // ---- Matrix: Tool throws, run continues ------------------------------------------------------

    [Fact]
    public async Task Tool_exception_is_recorded_as_its_type_name_and_the_run_still_completes()
    {
        var harness = new Harness();
        var client = new ScriptedChatClient
        {
            Calls = [new ScriptedCall(FailTool, text => new Dictionary<string, object?> { ["note"] = text }), EchoCall()],
        };
        var inner = CreateAgent(client);

        var expected = await inner.RunAsync("task-tool-throws");
        var response = await harness.Capture(inner).RunAsync("task-tool-throws");

        Assert.Equal(Shape(expected), Shape(response));

        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal(2, attempt.ToolCalls.Count);

        var failed = Assert.Single(attempt.ToolCalls, c => c.ToolName == FailTool);
        Assert.Equal(typeof(ToolFailureException).FullName, failed.Error);
        Assert.Null(failed.Result);
        Assert.DoesNotContain("secret detail", failed.Error!, StringComparison.Ordinal);

        var succeeded = Assert.Single(attempt.ToolCalls, c => c.ToolName == EchoTool);
        Assert.Equal("echo:task-tool-throws", succeeded.Result);
    }

    // ---- Matrix: Agent fails ---------------------------------------------------------------------

    [Fact]
    public async Task Agent_failure_non_streaming_rethrows_the_original_exception_and_records_a_failed_run()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { Throw = true });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Capture(inner).RunAsync("task-fails"));

        Assert.Same(ScriptedChatClient.ThrownException, thrown);
        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Failed, run.ExecutionStatus);
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal(typeof(InvalidOperationException).FullName, attempt.Error);
        Assert.Null(attempt.Result);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task Agent_failure_mid_stream_rethrows_the_original_exception_and_records_a_failed_run()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall()], Throw = true });
        var received = new List<AgentResponseUpdate>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var update in harness.Capture(inner).RunStreamingAsync("task-fails-streaming"))
            {
                received.Add(update);
            }
        });

        Assert.Same(ScriptedChatClient.ThrownException, thrown);
        Assert.Contains(received, u => u.Text == "Hello");
        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Failed, run.ExecutionStatus);
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal(typeof(InvalidOperationException).FullName, attempt.Error);
        Assert.Single(attempt.ToolCalls);
    }

    // ---- Matrix: Cancelled -----------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_non_streaming_records_a_cancelled_run_and_propagates_the_callers_cancellation()
    {
        var harness = new Harness();
        var client = new ScriptedChatClient { BlockUntilCancelled = true };
        var inner = CreateAgent(client);
        using var cts = new CancellationTokenSource();

        var run = harness.Capture(inner).RunAsync("task-cancelled", cancellationToken: cts.Token);
        await client.Entered.Task;
        await cts.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(cts.Token, thrown.CancellationToken);
        var captured = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Cancelled, captured.ExecutionStatus);
        Assert.Equal(thrown.GetType().FullName, Assert.Single(captured.Attempts).Error);
    }

    [Fact]
    public async Task Cancellation_mid_stream_records_a_cancelled_run_and_propagates_the_callers_cancellation()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { BlockUntilCancelled = true });
        using var cts = new CancellationTokenSource();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in harness.Capture(inner).RunStreamingAsync("task-cancelled-streaming", cancellationToken: cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.Equal(cts.Token, thrown.CancellationToken);
        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Cancelled, run.ExecutionStatus);
        Assert.Equal(thrown.GetType().FullName, Assert.Single(run.Attempts).Error);
    }

    // ---- Matrix: Early stream break --------------------------------------------------------------

    [Fact]
    public async Task Early_stream_break_finalizes_the_run_as_cancelled_on_enumerator_dispose()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { FinalChunks = ["first", "second", "third"] });

        await foreach (var update in harness.Capture(inner).RunStreamingAsync("task-break"))
        {
            Assert.Equal("first", update.Text);
            break;
        }

        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Cancelled, run.ExecutionStatus);
        var attempt = Assert.Single(run.Attempts);
        Assert.Null(attempt.Error);
        Assert.Null(attempt.Result);
        Assert.Equal(1, harness.Service.AppendCalls);
        Assert.Equal(1, harness.Service.CompleteCalls);
    }

    // ---- Matrix: Concurrent runs -----------------------------------------------------------------

    [Fact]
    public async Task Concurrent_runs_keep_every_tool_call_in_its_own_run()
    {
        const int runCount = 25;
        var harness = new Harness();
        var slowCall = new ScriptedCall(SlowEchoTool, text => new Dictionary<string, object?> { ["note"] = text });
        var client = new ScriptedChatClient { Calls = [slowCall, slowCall, slowCall] };
        var agent = harness.Capture(CreateAgent(client, allowConcurrentInvocation: true));

        var responses = await Task.WhenAll(Enumerable.Range(0, runCount).Select(i => Task.Run(() => agent.RunAsync($"marker-{i}"))));

        Assert.Equal(runCount, responses.Length);
        Assert.Equal(runCount, harness.Service.StartedRunIds.Count);
        foreach (var runId in harness.Service.StartedRunIds)
        {
            Assert.True(harness.Service.TryGetRun(runId, out var run));
            Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
            var attempt = Assert.Single(run.Attempts);
            Assert.Equal(3, attempt.ToolCalls.Count);
            Assert.All(attempt.ToolCalls, call =>
            {
                Assert.Equal(run.TaskId, call.Arguments["note"]?.ToString());
                Assert.Equal($"echo:{run.TaskId}", call.Result);
            });
            Assert.Equal(attempt.ToolCalls.OrderBy(c => c.StartedAt).Select(c => c.ToolCallId), attempt.ToolCalls.Select(c => c.ToolCallId));
        }

        var runs = harness.Service.StartedRunIds.Select(id => { harness.Service.TryGetRun(id, out var r); return r!; }).ToList();
        Assert.Equal(runCount, runs.Select(r => r.TaskId).Distinct().Count());

        // Sanity: MAF really ran tools concurrently (some calls in one attempt overlap in time).
        Assert.Contains(runs, r =>
        {
            var calls = r.Attempts[0].ToolCalls;
            return calls.Skip(1).Any(later => later.StartedAt < calls[0].StartedAt + calls[0].Duration);
        });
        Assert.Empty(harness.Failures);
    }

    // ---- Matrix: Duplicate finalization ----------------------------------------------------------

    [Fact]
    public async Task Duplicate_finalization_appends_one_attempt_and_completes_once()
    {
        var harness = new Harness();
        var options = harness.Options();
        var scope = CaptureScope.TryBegin(harness.Service, options, [new ChatMessage(ChatRole.User, "task-duplicate")], null, CreateAgent(new ScriptedChatClient()));
        Assert.NotNull(scope);

        await Task.WhenAll(
            scope.FinalizeAsync(RunExecutionStatus.Completed, "done", null),
            scope.FinalizeAsync(RunExecutionStatus.Failed, null, "System.Exception"));
        await scope.FinalizeAsync(RunExecutionStatus.Cancelled, null, null);

        Assert.Equal(1, harness.Service.AppendCalls);
        Assert.Equal(1, harness.Service.CompleteCalls);
        var run = harness.SingleRun();
        Assert.Single(run.Attempts);
        Assert.NotNull(run.ExecutionStatus);
        Assert.Empty(harness.Failures);
    }

    // ---- Matrix: Capture fails -------------------------------------------------------------------

    [Fact]
    public async Task Resolver_failure_leaves_a_successful_response_unchanged_and_is_reported_once()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] });
        var options = harness.Options(resolve: _ => throw new InvalidOperationException("resolver bug"));

        var expected = await inner.RunAsync("task-resolver");
        var response = await harness.Capture(inner, options).RunAsync("task-resolver");

        Assert.Equal(Shape(expected), Shape(response));
        Assert.Empty(harness.Service.StartedRunIds);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, failure.Stage);
    }

    [Fact]
    public async Task Resolver_failure_leaves_an_agent_exception_unchanged_and_is_reported_once()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { Throw = true });
        var options = harness.Options(resolve: _ => throw new InvalidOperationException("resolver bug"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Capture(inner, options).RunAsync("task-resolver-fails"));

        Assert.Same(ScriptedChatClient.ThrownException, thrown);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, Assert.Single(harness.Failures).Stage);
    }

    [Fact]
    public async Task Throwing_capture_service_leaves_a_successful_response_unchanged_and_is_reported_once()
    {
        var harness = new Harness();
        harness.Service.ThrowOnFinalize = true;
        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] });

        var expected = await inner.RunAsync("task-service-throws");
        var response = await harness.Capture(inner).RunAsync("task-service-throws");

        Assert.Equal(Shape(expected), Shape(response));
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Equal(Assert.Single(harness.Service.StartedRunIds), failure.RunId);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public async Task Throwing_capture_service_leaves_a_streaming_response_unchanged_and_is_reported_once()
    {
        var harness = new Harness();
        harness.Service.ThrowOnFinalize = true;
        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] });

        var expected = await CollectAsync(inner.RunStreamingAsync("task-service-throws-streaming"));
        var updates = await CollectAsync(harness.Capture(inner).RunStreamingAsync("task-service-throws-streaming"));

        Assert.Equal(Shape(expected), Shape(updates));
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, Assert.Single(harness.Failures).Stage);
    }

    [Fact]
    public async Task Hanging_capture_service_is_bounded_by_the_finalization_timeout_and_leaves_an_agent_exception_unchanged()
    {
        var harness = new Harness();
        harness.Service.HangOnFinalize = true;
        var inner = CreateAgent(new ScriptedChatClient { Throw = true });
        var options = harness.Options(finalizationTimeout: TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Capture(inner, options).RunAsync("task-service-hangs"));

        stopwatch.Stop();
        Assert.Same(ScriptedChatClient.ThrownException, thrown);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Finalization was not bounded: {stopwatch.Elapsed}.");
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.IsType<TimeoutException>(failure.Exception);
    }

    [Fact]
    public async Task Exceptions_thrown_by_the_failure_callback_are_swallowed()
    {
        var harness = new Harness();
        harness.Service.ThrowOnFinalize = true;
        var inner = CreateAgent(new ScriptedChatClient());
        var options = harness.Options(throwingCallback: true);

        var response = await harness.Capture(inner, options).RunAsync("task-callback-throws");

        Assert.Equal("Hello, world", response.Text);
        Assert.Single(harness.Failures);
    }

    // ---- Additional checks -----------------------------------------------------------------------

    [Fact]
    public void Resolved_Microsoft_Agents_AI_assembly_is_version_1_20_0()
    {
        var assembly = typeof(ChatClientAgent).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        Assert.StartsWith("1.20.0", informational, StringComparison.Ordinal);
        Assert.True(informational.Length == "1.20.0".Length || informational["1.20.0".Length] is '+', $"Unexpected informational version '{informational}'.");
    }

    [Fact]
    public async Task Session_state_bag_gains_only_the_run_id_key()
    {
        var harness = new Harness();
        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] });
        var captured = harness.Capture(inner);

        var uncapturedSession = await inner.CreateSessionAsync();
        await inner.RunAsync("task-session", uncapturedSession);
        var capturedSession = await captured.CreateSessionAsync();
        await captured.RunAsync("task-session", capturedSession);

        var uncapturedKeys = Keys(uncapturedSession.StateBag.Serialize());
        var capturedKeys = Keys(capturedSession.StateBag.Serialize());
        Assert.Equal([ExperienceCaptureAgentBuilderExtensions.RunIdStateKey], capturedKeys.Except(uncapturedKeys));
        Assert.Empty(uncapturedKeys.Except(capturedKeys));
        Assert.Equal(
            Assert.Single(harness.Service.StartedRunIds).ToString("D"),
            capturedSession.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey));

        static List<string> Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToList();
    }

    [Fact]
    public async Task Run_id_is_known_before_the_fake_clients_first_call()
    {
        var harness = new Harness();
        Guid? runIdAtFirstCall = null;
        ExperienceRun? runAtFirstCall = null;
        AgentSession? session = null;
        string? sessionRunIdAtFirstCall = null;
        var client = new ScriptedChatClient
        {
            OnCall = () =>
            {
                if (runIdAtFirstCall is not null || !harness.Service.StartedRunIds.TryPeek(out var id))
                {
                    return;
                }

                runIdAtFirstCall = id;
                harness.Service.TryGetRun(id, out runAtFirstCall);
                sessionRunIdAtFirstCall = session!.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey);
            },
        };
        var agent = harness.Capture(CreateAgent(client));
        session = await agent.CreateSessionAsync();

        await agent.RunAsync("task-run-id-first", session);

        Assert.NotNull(runIdAtFirstCall);
        Assert.NotNull(runAtFirstCall);
        Assert.Null(runAtFirstCall.ExecutionStatus);
        Assert.Equal(runIdAtFirstCall.Value.ToString("D"), sessionRunIdAtFirstCall);
        Assert.Equal(runIdAtFirstCall, harness.SingleRun().RunId);
    }

    [Fact]
    public async Task Non_ChatClientAgent_with_tool_capture_disabled_captures_run_lifecycle()
    {
        var harness = new Harness();
        var options = harness.Options(captureToolCalls: false);
        var agent = harness.Capture(new ScriptedAgent(), options);

        var response = await agent.RunAsync("task-scripted");
        var updates = await CollectAsync(agent.RunStreamingAsync("task-scripted-streaming"));
        var failing = harness.Capture(new ScriptedAgent { Throw = true }, options);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunAsync("task-scripted-fails"));

        Assert.Equal("scripted agent reply", response.Text);
        Assert.Equal("scripted stream", string.Concat(updates.Select(u => u.Text)));
        Assert.Same(ScriptedChatClient.ThrownException, thrown);

        var runs = harness.Service.StartedRunIds.Select(id => { Assert.True(harness.Service.TryGetRun(id, out var r)); return r; }).ToDictionary(r => r.TaskId);
        Assert.Equal(3, runs.Count);
        Assert.Equal(RunExecutionStatus.Completed, runs["task-scripted"].ExecutionStatus);
        Assert.Equal("scripted agent reply", Assert.Single(runs["task-scripted"].Attempts).Result);
        Assert.Equal(RunExecutionStatus.Completed, runs["task-scripted-streaming"].ExecutionStatus);
        Assert.Equal("scripted stream", Assert.Single(runs["task-scripted-streaming"].Attempts).Result);
        Assert.Equal(RunExecutionStatus.Failed, runs["task-scripted-fails"].ExecutionStatus);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public void Tool_capture_on_a_non_ChatClientAgent_fails_at_build_time()
    {
        var harness = new Harness();

        Assert.Throws<InvalidOperationException>(() => harness.Capture(new ScriptedAgent()));
    }

    // ---- Review follow-ups -----------------------------------------------------------------------

    [Theory]
    [InlineData("append")]
    [InlineData("complete")]
    public async Task Non_success_capture_outcome_reports_one_finalize_failure_and_leaves_the_response_unchanged(string step)
    {
        var harness = new Harness();
        if (step == "append")
        {
            harness.Service.ForcedAppendOutcome = AppendAttemptOutcome.SanitizationRejected;
        }
        else
        {
            harness.Service.ForcedCompleteOutcome = CompleteRunOutcome.Conflict;
        }

        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] });

        var expected = await inner.RunAsync("task-non-success");
        var response = await harness.Capture(inner).RunAsync("task-non-success");

        Assert.Equal(Shape(expected), Shape(response));
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Equal(Assert.Single(harness.Service.StartedRunIds), failure.RunId);
        Assert.Contains(step == "append" ? nameof(AppendAttemptOutcome.SanitizationRejected) : nameof(CompleteRunOutcome.Conflict), failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_throwing_still_completes_the_run()
    {
        var harness = new Harness();
        harness.Service.ThrowOnAppendOnly = true;

        await harness.Capture(CreateAgent(new ScriptedChatClient())).RunAsync("task-append-throws");

        Assert.Equal(1, harness.Service.CompleteCalls);
        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Empty(run.Attempts);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, Assert.Single(harness.Failures).Stage);
    }

    [Fact]
    public async Task Synchronously_blocking_capture_service_is_bounded_by_the_finalization_timeout()
    {
        var harness = new Harness();
        using var gate = new ManualResetEventSlim(false);
        harness.Service.BlockAppend = gate;
        var options = harness.Options(finalizationTimeout: TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-blocks");

            stopwatch.Stop();
            Assert.Equal("Hello, world", response.Text);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Finalization was not bounded: {stopwatch.Elapsed}.");
            Assert.IsType<TimeoutException>(Assert.Single(harness.Failures).Exception);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Token_honoring_hang_reports_the_timeout_not_the_resulting_cancellation()
    {
        var harness = new Harness();
        harness.Service.HangHonoringToken = true;
        var options = harness.Options(finalizationTimeout: TimeSpan.FromMilliseconds(200));

        var response = await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-hang-token");

        Assert.Equal("Hello, world", response.Text);

        // Let the abandoned finalization observe its cancelled token and try completion.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.Service.CompleteCalls == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);

        Assert.Equal(1, harness.Service.CompleteCalls);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.IsType<TimeoutException>(failure.Exception);
        Assert.Contains("did not finish", failure.Reason, StringComparison.Ordinal);

        var run = harness.SingleRun();
        Assert.Null(run.ExecutionStatus);
        Assert.Empty(run.Attempts);
    }

    [Fact]
    public async Task StartRun_conflict_reports_StartRun_writes_nothing_to_the_session_and_leaves_the_first_run_intact()
    {
        var harness = new Harness();
        var fixedId = Guid.NewGuid();
        var options = harness.Options(newId: () => fixedId);
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);

        var firstSession = await agent.CreateSessionAsync();
        await agent.RunAsync("task-first", firstSession);
        var secondSession = await agent.CreateSessionAsync();
        var response = await agent.RunAsync("task-second", secondSession);

        Assert.Equal("Hello, world", response.Text);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Equal(fixedId, failure.RunId);
        Assert.False(secondSession.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out _));

        var run = harness.SingleRun();
        Assert.Equal("task-first", run.TaskId);
        Assert.Single(run.Attempts);
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
    }

    [Fact]
    public async Task Distinct_stage_failures_are_each_reported_once_and_same_stage_duplicates_are_deduped()
    {
        var harness = new Harness();
        harness.Service.ThrowOnFinalize = true;
        var calls = 0;

        // NewId order within a run: run ID, then one ID per tool call, then attempt and completion IDs.
        // Failing both tool-call IDs yields two ToolCall-stage failures; the service yields a Finalize failure.
        var options = harness.Options(newId: () =>
        {
            var call = Interlocked.Increment(ref calls);
            return call is 2 or 3 ? throw new InvalidOperationException("id source failure") : Guid.NewGuid();
        });
        var inner = CreateAgent(new ScriptedChatClient { Calls = [EchoCall(), EchoCall()] });

        var expected = await inner.RunAsync("task-stages");
        var response = await harness.Capture(inner, options).RunAsync("task-stages");

        Assert.Equal(Shape(expected), Shape(response));
        Assert.Equal(
            [ExperienceCaptureFailureStage.ToolCall, ExperienceCaptureFailureStage.Finalize],
            harness.Failures.Select(f => f.Stage).ToArray());
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("max")]
    public void Out_of_range_finalization_timeout_throws_at_build(string value)
    {
        var harness = new Harness();
        var timeout = value switch
        {
            "zero" => TimeSpan.Zero,
            "negative" => TimeSpan.FromMilliseconds(-1),
            _ => TimeSpan.MaxValue,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            harness.Capture(CreateAgent(new ScriptedChatClient()), harness.Options(finalizationTimeout: timeout)));
    }

    [Fact]
    public async Task Provider_cancellation_with_an_uncancelled_caller_token_records_a_failed_run()
    {
        var harness = new Harness();
        var providerTimeout = new TaskCanceledException("provider timeout");
        var inner = CreateAgent(new ScriptedChatClient { Throw = true, ExceptionToThrow = providerTimeout });

        var thrown = await Assert.ThrowsAsync<TaskCanceledException>(() => harness.Capture(inner).RunAsync("task-provider-timeout"));

        Assert.Same(providerTimeout, thrown);
        var run = harness.SingleRun();
        Assert.Equal(RunExecutionStatus.Failed, run.ExecutionStatus);
        Assert.Equal(typeof(TaskCanceledException).FullName, Assert.Single(run.Attempts).Error);
    }

    [Fact]
    public async Task Poco_tool_result_is_recorded_as_json_and_a_null_tool_result_stays_null()
    {
        var harness = new Harness();
        var noArguments = new Func<string, IDictionary<string, object?>>(_ => new Dictionary<string, object?>());
        var inner = CreateAgent(new ScriptedChatClient { Calls = [new ScriptedCall(PocoToolName, noArguments), new ScriptedCall(NullToolName, noArguments)] });

        await harness.Capture(inner).RunAsync("task-poco");

        var calls = Assert.Single(harness.SingleRun().Attempts).ToolCalls;
        Assert.Equal(JsonSerializer.Serialize(PocoResult, AIJsonUtilities.DefaultOptions), Assert.Single(calls, c => c.ToolName == PocoToolName).Result);
        var nullCall = Assert.Single(calls, c => c.ToolName == NullToolName);
        Assert.Null(nullCall.Result);
        Assert.Null(nullCall.Error);
    }

    [Fact]
    public async Task One_shot_message_sequence_still_reaches_the_inner_agent_when_the_resolver_enumerates_it()
    {
        var harness = new Harness();
        var client = new ScriptedChatClient();
        var middleware = new ExperienceCaptureMiddleware(harness.Service, harness.Options());
        var messages = new OneShotEnumerable<ChatMessage>([new ChatMessage(ChatRole.User, "task-one-shot")]);

        var response = await middleware.RunAsync(messages, null, null, CreateAgent(client), CancellationToken.None);

        Assert.Equal("Hello, world", response.Text);
        Assert.Contains("task-one-shot", client.UserTexts);
        Assert.Equal("task-one-shot", harness.SingleRun().TaskId);
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("taskId")]
    [InlineData("scope")]
    public async Task Null_descriptor_parts_report_ResolveRun_and_run_uncaptured(string part)
    {
        var harness = new Harness();
        var options = harness.Options(resolve: _ => part switch
        {
            "descriptor" => null!,
            "taskId" => new ExperienceRunDescriptor(null!, TestScope),
            _ => new ExperienceRunDescriptor("task", null!),
        });

        var response = await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-null-parts");

        Assert.Equal("Hello, world", response.Text);
        Assert.Empty(harness.Service.StartedRunIds);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, Assert.Single(harness.Failures).Stage);
    }

    [Fact]
    public async Task Streaming_writes_the_run_id_to_a_supplied_session()
    {
        var harness = new Harness();
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] }));
        var session = await agent.CreateSessionAsync();

        await CollectAsync(agent.RunStreamingAsync("task-streaming-session", session));

        Assert.Equal(
            Assert.Single(harness.Service.StartedRunIds).ToString("D"),
            session.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey));
    }

    [Fact]
    public async Task Throwing_failure_callback_on_the_resolver_path_is_swallowed()
    {
        var harness = new Harness();
        var options = harness.Options(resolve: _ => throw new InvalidOperationException("resolver bug"), throwingCallback: true);

        var response = await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-resolver-callback-throws");

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, Assert.Single(harness.Failures).Stage);
    }
}
