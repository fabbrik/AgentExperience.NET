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

        public Harness(int maxAttemptsPerRun = 10, ISanitizer? sanitizer = null, int maxResultLength = 10_000, int maxErrorLength = 10_000)
        {
            Service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
                sanitizer ?? new DefaultSanitizer(Sanitization),
                new CaptureLimits(maxAttemptsPerRun, MaxToolCallsPerAttempt: 50, MaxResultLength: maxResultLength, MaxErrorLength: maxErrorLength)));
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
            Func<Guid>? newId = null,
            Func<ExperienceRunCompletionContext, bool>? shouldComplete = null,
            TimeSpan? maxOpenRunDuration = null,
            int maxAttemptsPerOpenRun = 8,
            TimeProvider? timeProvider = null) => new()
            {
                TimeProvider = timeProvider ?? TimeProvider.System,
                NewId = newId ?? Guid.NewGuid,
                ResolveRun = resolve ?? (context => new ExperienceRunDescriptor(context.Messages.Last().Text, TestScope, "captured by tests")),
                CaptureToolCalls = captureToolCalls,
                FinalizationTimeout = finalizationTimeout ?? TimeSpan.FromSeconds(5),
                ShouldCompleteRun = shouldComplete ?? (static _ => true),
                MaxOpenRunDuration = maxOpenRunDuration ?? TimeSpan.FromMinutes(5),
                MaxAttemptsPerOpenRun = maxAttemptsPerOpenRun,
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
        var scope = CaptureScope.TryBegin(harness.Service, options, new OpenRunRegistry(harness.Service, options), [new ChatMessage(ChatRole.User, "task-duplicate")], null, CreateAgent(new ScriptedChatClient()));
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
    public void Resolved_Microsoft_Agents_AI_assembly_is_version_1_22_0()
    {
        var assembly = typeof(ChatClientAgent).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        Assert.StartsWith("1.22.0", informational, StringComparison.Ordinal);
        Assert.True(informational.Length == "1.22.0".Length || informational["1.22.0".Length] is '+', $"Unexpected informational version '{informational}'.");
    }

    [Fact]
    public async Task A_reused_ChatClientAgentRunOptions_instance_is_not_changed_and_each_run_records_its_own_tool_call_once()
    {
        // At 1.20.0, MAF's function middleware wrote its ChatClientFactory onto the options instance it
        // was given, so reusing one instance stacked a middleware layer per invocation and the adapter's
        // README told hosts not to. 1.22.0 works on a per-run clone. This pins that behaviour: if a later
        // pin reintroduces the mutation, this fails and the README caveat has to come back.
        var harness = new Harness();
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] }));
        var shared = new ChatClientAgentRunOptions();

        await agent.RunAsync("task-reuse-1", options: shared);
        await agent.RunAsync("task-reuse-2", options: shared);

        Assert.Null(shared.ChatClientFactory);
        Assert.Equal(2, harness.Service.StartedRunIds.Count);
        foreach (var runId in harness.Service.StartedRunIds)
        {
            Assert.True(harness.Service.TryGetRun(runId, out var run));
            var toolCall = Assert.Single(Assert.Single(run.Attempts).ToolCalls);
            Assert.Equal($"echo:{run.TaskId}", toolCall.Result);
        }

        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task A_reused_ChatClientAgentRunOptions_instance_keeps_the_hosts_own_ChatClientFactory()
    {
        // Assert.Null above only catches mutation from a null start. A host that sets its own factory must get
        // that same delegate back, still applied, and not wrapped again on each invocation.
        var harness = new Harness();
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] }));
        var hostFactoryCalls = 0;
        Func<IChatClient, IChatClient> hostFactory = client =>
        {
            Interlocked.Increment(ref hostFactoryCalls);
            return client;
        };
        var shared = new ChatClientAgentRunOptions { ChatClientFactory = hostFactory };

        await agent.RunAsync("task-host-factory-1", options: shared);
        await agent.RunAsync("task-host-factory-2", options: shared);

        Assert.Same(hostFactory, shared.ChatClientFactory);
        Assert.True(hostFactoryCalls >= 2, $"The host's factory ran {hostFactoryCalls} time(s) across two runs.");
        foreach (var runId in harness.Service.StartedRunIds)
        {
            Assert.True(harness.Service.TryGetRun(runId, out var run));
            Assert.Single(Assert.Single(run.Attempts).ToolCalls);
        }

        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task A_reused_ChatClientAgentRunOptions_instance_is_not_changed_across_streaming_runs()
    {
        // The streaming sibling of the test above: the README's reuse claim covers both paths, so both are pinned.
        var harness = new Harness();
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] }));
        var shared = new ChatClientAgentRunOptions();

        await CollectAsync(agent.RunStreamingAsync("task-stream-reuse-1", options: shared));
        await CollectAsync(agent.RunStreamingAsync("task-stream-reuse-2", options: shared));

        Assert.Null(shared.ChatClientFactory);
        Assert.Equal(2, harness.Service.StartedRunIds.Count);
        foreach (var runId in harness.Service.StartedRunIds)
        {
            Assert.True(harness.Service.TryGetRun(runId, out var run));
            var toolCall = Assert.Single(Assert.Single(run.Attempts).ToolCalls);
            Assert.Equal($"echo:{run.TaskId}", toolCall.Result);
        }

        Assert.Empty(harness.Failures);
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

    /// <summary>
    /// Story 4.6 rewrote this: a colliding run ID is a conflict when it names somebody else's run,
    /// and is a continuation when it names this same task's still-open run. This is the foreign half
    /// -- two different tasks forced onto one identifier -- and it behaves exactly as it always did.
    /// The continuation half is the matrix below.
    /// </summary>
    [Fact]
    public async Task StartRun_conflict_on_a_foreign_run_reports_StartRun_writes_nothing_to_the_session_and_leaves_the_first_run_intact()
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

    // ---- Story 4.6 matrix: continuing a run across invocations -----------------------------------

    /// <summary>
    /// Matrix row 1. A host that changes nothing gets exactly what it got before: one invocation, one
    /// attempt, run closed -- and a second invocation is a second run, not a second attempt.
    /// </summary>
    [Fact]
    public async Task Row1_a_host_that_does_nothing_still_gets_one_invocation_one_attempt_and_a_closed_run()
    {
        var harness = new Harness();
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] }));

        await agent.RunAsync("task-default");
        await agent.RunAsync("task-default");

        Assert.Equal(2, harness.Service.StartedRunIds.Count);
        foreach (var runId in harness.Service.StartedRunIds)
        {
            Assert.True(harness.Service.TryGetRun(runId, out var run));
            Assert.Single(run.Attempts);
            Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        }

        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// Matrix row 2, and the story itself: a failed invocation and its retry become two attempts of
    /// one run, so the reflection that is eventually produced sees the failure and the success
    /// together instead of quarantining one and learning nothing from the other.
    /// </summary>
    [Fact]
    public async Task Row2_a_retry_under_the_same_run_id_is_appended_as_the_next_attempt_and_the_run_stays_open()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var closing = false;
        var options = harness.Options(
            resolve: context => new ExperienceRunDescriptor("incident-42", TestScope, "retried by tests", ContinuesRunId: runId),
            shouldComplete: _ => closing);

        var failNow = 1;
        var client = new ScriptedChatClient
        {
            Calls = [EchoCall()],
            OnCall = () =>
            {
                if (Volatile.Read(ref failNow) == 1)
                {
                    throw new InvalidOperationException("the first try fails");
                }
            },
        };

        // One agent, built once, as a host builds one: continuation lives on the registration.
        var agent = harness.Capture(CreateAgent(client), options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("attempt-1"));

        // The failure did not close the run, so nothing about it has been finalized or quarantined.
        Assert.True(harness.Service.TryGetRun(runId, out var afterFirst));
        Assert.Null(afterFirst.ExecutionStatus);
        Assert.Single(afterFirst.Attempts);

        closing = true;
        Volatile.Write(ref failNow, 0);
        await agent.RunAsync("attempt-2");

        var runId2 = Assert.Single(harness.Service.StartedRunIds);
        Assert.Equal(runId, runId2);

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal([0, 1], run.Attempts.Select(attempt => attempt.SequenceNumber).ToArray());
        Assert.Equal(typeof(InvalidOperationException).FullName, run.Attempts[0].Error);
        Assert.Equal("Hello, world", run.Attempts[1].Result);
        Assert.Equal(EchoTool, Assert.Single(run.Attempts[1].ToolCalls).ToolName);
        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// Matrix row 3: a continuation identifier that names somebody else's run is still a conflict --
    /// for a different task, a task id that differs only by case, and a difference in any one of the
    /// six scope fields.
    /// </summary>
    /// <remarks>
    /// The owner and the intruder share one agent and so one registration, as two tenants of one host
    /// would. The intruder's claim lands on the owner's own entry, and giving it back must leave that
    /// entry and its armed bound exactly as they were: withdrawing it instead would dispose the owner's
    /// bound, and the owner's run would then never close.
    /// </remarks>
    [Theory]
    [InlineData("task")]
    [InlineData("task-case")]
    [InlineData("tenant")]
    [InlineData("application")]
    [InlineData("project")]
    [InlineData("team")]
    [InlineData("agent")]
    [InlineData("user")]
    public async Task Row3_a_continuation_id_naming_a_different_task_or_scope_is_refused_and_runs_uncaptured(string difference)
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var ownerScope = new Scope("tenant-1", "app-1", "project-1", "team-1", "agent-1", "user-1");
        var (intruderTask, intruderScope) = difference switch
        {
            "task" => ("incident-99", ownerScope),
            "task-case" => ("INCIDENT-42", ownerScope),
            "tenant" => ("incident-42", ownerScope with { TenantId = "tenant-2" }),
            "application" => ("incident-42", ownerScope with { ApplicationId = "app-2" }),
            "project" => ("incident-42", ownerScope with { ProjectId = "project-2" }),
            "team" => ("incident-42", ownerScope with { TeamId = "team-2" }),
            "agent" => ("incident-42", ownerScope with { AgentId = "agent-2" }),
            _ => ("incident-42", ownerScope with { UserId = "user-2" }),
        };

        var intruding = false;
        var options = harness.Options(
            resolve: _ => Volatile.Read(ref intruding)
                ? new ExperienceRunDescriptor(intruderTask, intruderScope, ContinuesRunId: runId)
                : new ExperienceRunDescriptor("incident-42", ownerScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);

        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();
        var registry = OpenRunsOf(captureLifetime);

        await agent.RunAsync("attempt-1");
        var bound = Assert.Single(clock.Bounds);

        Volatile.Write(ref intruding, true);
        var session = await agent.CreateSessionAsync();
        var response = await agent.RunAsync("attempt-2", session);

        // The invocation itself is untouched; only capture declined.
        Assert.Equal("Hello, world", response.Text);
        Assert.False(session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out _));

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Equal(runId, failure.RunId);
        Assert.Contains("Conflict", failure.Reason, StringComparison.Ordinal);

        // The owner's run is untouched: one attempt, still open, still its own task.
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal("incident-42", run.TaskId);
        Assert.Equal(ownerScope, run.Scope);
        Assert.Single(run.Attempts);
        Assert.Null(run.ExecutionStatus);

        // And so is the owner's entry and its bound: the refused claim was given back, not withdrawn.
        Assert.False(bound.Disposed);
        Assert.Equal(1, registry.Count);

        // The owner can still continue its own run -- the intruder left no claim behind ...
        Volatile.Write(ref intruding, false);
        await agent.RunAsync("attempt-3");
        Assert.True(harness.Service.TryGetRun(runId, out var continued));
        Assert.Equal(2, continued.Attempts.Count);
        Assert.Same(bound, Assert.Single(clock.Bounds));

        // ... and the owner's bound still closes it.
        bound.Fire();
        await Eventually(() => StatusOf(harness, runId) is not null);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        await Eventually(() => harness.Failures.Any(f => f.Reason.Contains("open-run bound", StringComparison.Ordinal)));
        await Eventually(() => registry.Count == 0);
    }

    /// <summary>
    /// Matrix row 4: a completed run is never reopened. The refusal happens at StartRun, before
    /// anything is captured, and the store's own "a finalized run accepts no further attempts"
    /// invariant still holds underneath it.
    /// </summary>
    [Fact]
    public async Task Row4_a_continuation_id_naming_an_already_completed_run_is_refused()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var options = harness.Options(resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId));

        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);
        await agent.RunAsync("attempt-1");
        Assert.True(harness.Service.TryGetRun(runId, out var completed));
        Assert.Equal(RunExecutionStatus.Completed, completed.ExecutionStatus);

        var response = await agent.RunAsync("attempt-2");

        Assert.Equal("Hello, world", response.Text);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Equal(runId, failure.RunId);

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Single(run.Attempts);

        // The deeper invariant the adapter relies on, asserted directly rather than assumed.
        var direct = await harness.Service.AppendAttemptAsync(
            runId,
            new AppendAttemptRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.Zero, [], "late", null));
        Assert.Equal(AppendAttemptOutcome.Conflict, direct.Outcome);
    }

    /// <summary>Matrix row 5: a continuation identifier for a run that does not exist simply opens it.</summary>
    [Fact]
    public async Task Row5_a_continuation_id_for_a_run_that_does_not_exist_starts_a_new_run_under_it()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var options = harness.Options(resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId));

        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("attempt-1", session);

        Assert.Equal(runId, Assert.Single(harness.Service.StartedRunIds));
        Assert.Equal(runId.ToString("D"), session.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey));
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Single(run.Attempts);
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// Matrix row 6, and frozen rule 3: an open run holds captured payload, so a host that walks away
    /// does not get to keep it open. The adapter closes it at the declared bound and says so.
    /// </summary>
    [Fact]
    public async Task Row6_a_run_the_host_never_closes_is_completed_at_its_bound_and_reported()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            maxOpenRunDuration: TimeSpan.FromMilliseconds(150));

        await harness.Capture(CreateAgent(new ScriptedChatClient { Calls = [EchoCall()] }), options).RunAsync("attempt-1");

        // Nobody ever comes back. The bound is the only thing that ends this run.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(harness.Service.TryGetRun(runId, out var current));
            if (current.ExecutionStatus is not null)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Cancelled, run.ExecutionStatus);

        // The attempt the host did capture is kept -- the bound ends the run, it does not discard it.
        Assert.Single(run.Attempts);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Equal(runId, failure.RunId);
        Assert.Contains("open-run bound", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Matrix row 7: the capture service's own attempt-count limit. An attempt that could not be
    /// recorded at all is never a reason to keep collecting more of them, so the run is completed.
    /// </summary>
    [Fact]
    public async Task Row7_reaching_the_services_attempt_capacity_completes_the_run_rather_than_leaving_it_open()
    {
        var harness = new Harness(maxAttemptsPerRun: 1);
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false);

        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);
        await agent.RunAsync("attempt-1");
        Assert.True(harness.Service.TryGetRun(runId, out var afterFirst));
        Assert.Null(afterFirst.ExecutionStatus);

        var response = await agent.RunAsync("attempt-2");

        Assert.Equal("Hello, world", response.Text);
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Single(run.Attempts);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains(nameof(AppendAttemptOutcome.CapacityExceeded), failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Matrix row 8, in the documented flow: the first invocation <em>opens</em> the run with no
    /// continuation identifier at all, and the second reads the run's identifier from the first one's
    /// session while the first is still in flight. The opener holds the run exactly as a continuation
    /// would, so the second is refused outright rather than interleaving a second attempt.
    /// </summary>
    [Fact]
    public async Task Row8_an_invocation_naming_a_run_its_opener_is_still_capturing_on_is_refused()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentSession? openerSession = null;

        // The opener names no run; anyone else continues whatever run the opener's session names.
        var options = harness.Options(
            resolve: context => new ExperienceRunDescriptor(
                "incident-42",
                TestScope,
                ContinuesRunId: ReferenceEquals(context.Session, openerSession)
                    ? null
                    : Guid.Parse(openerSession!.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey)!)),
            shouldComplete: _ => false);

        // The first invocation parks inside the model call, holding the run, while the second starts.
        var parked = 0;
        var client = new ScriptedChatClient
        {
            OnCall = () =>
            {
                if (Interlocked.Exchange(ref parked, 1) != 0)
                {
                    return;
                }

                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };

        var agent = harness.Capture(CreateAgent(client, allowConcurrentInvocation: true), options);
        openerSession = await agent.CreateSessionAsync();
        var session = openerSession;

        // Started on another thread: the fake model blocks synchronously before its first await, so
        // calling it inline would park the test itself.
        var first = Task.Run(() => agent.RunAsync("attempt-1", session));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var runId = Assert.Single(harness.Service.StartedRunIds);
        var second = await agent.RunAsync("attempt-2");
        release.TrySetResult();
        await first;

        // Both invocations returned their model's answer; only one of them was captured.
        Assert.Equal("Hello, world", second.Text);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Equal(runId, failure.RunId);
        Assert.Contains("already capturing", failure.Reason, StringComparison.Ordinal);

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Single(run.Attempts);
        Assert.Null(run.ExecutionStatus);
    }

    /// <summary>
    /// Frozen rule 3's other bound: the adapter's own maximum attempt count for a run it is holding
    /// open, which a host declares and which overrides a request to keep the run open.
    /// </summary>
    [Fact]
    public async Task The_adapters_open_run_attempt_bound_completes_the_run_and_is_reported()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            maxAttemptsPerOpenRun: 2);

        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);
        await agent.RunAsync("attempt-1");
        Assert.True(harness.Service.TryGetRun(runId, out var afterFirst));
        Assert.Null(afterFirst.ExecutionStatus);
        Assert.Empty(harness.Failures);

        await agent.RunAsync("attempt-2");

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(2, run.Attempts.Count);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("2-attempt open-run bound", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A throwing predicate fails closed. Keeping a run open on the strength of host code that just
    /// threw would retain a captured payload on the least trustworthy possible signal.
    /// </summary>
    [Fact]
    public async Task A_throwing_ShouldCompleteRun_completes_the_run_and_is_reported()
    {
        var harness = new Harness();
        var options = harness.Options(shouldComplete: _ => throw new InvalidOperationException("predicate bug"));

        var response = await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-predicate-throws");

        Assert.Equal("Hello, world", response.Text);
        Assert.Equal(RunExecutionStatus.Completed, harness.SingleRun().ExecutionStatus);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("ShouldCompleteRun threw", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the predicate is handed: the run's identity, how many attempts it now holds, how long it
    /// has been open, and the attempt's own sanitized result -- so "good enough?" can be answered in
    /// the same step rather than declared in advance.
    /// </summary>
    [Fact]
    public async Task ShouldCompleteRun_sees_the_runs_identity_attempt_count_and_the_attempts_sanitized_result()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var seen = new List<ExperienceRunCompletionContext>();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: context =>
            {
                seen.Add(context);
                return context.Result == "Hello, world";
            });

        var failNow = 1;
        var agent = harness.Capture(
            CreateAgent(new ScriptedChatClient
            {
                OnCall = () =>
                {
                    if (Volatile.Read(ref failNow) == 1)
                    {
                        throw new InvalidOperationException("the first try fails");
                    }
                },
            }),
            options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("attempt-1"));
        Volatile.Write(ref failNow, 0);
        await agent.RunAsync("attempt-2");

        Assert.Equal(2, seen.Count);

        Assert.Equal(runId, seen[0].RunId);
        Assert.Equal("incident-42", seen[0].TaskId);
        Assert.Equal(TestScope, seen[0].Scope);
        Assert.Equal(RunExecutionStatus.Failed, seen[0].ExecutionStatus);
        Assert.Equal(1, seen[0].AttemptCount);
        Assert.Null(seen[0].Result);
        Assert.Equal(typeof(InvalidOperationException).FullName, seen[0].Error);

        Assert.Equal(RunExecutionStatus.Completed, seen[1].ExecutionStatus);
        Assert.Equal(2, seen[1].AttemptCount);
        Assert.Equal("Hello, world", seen[1].Result);
        Assert.Null(seen[1].Error);

        // The second invocation continues the first run, so its OpenFor spans both, not just itself.
        Assert.True(seen[1].OpenFor >= seen[0].OpenFor);
        Assert.Equal(RunExecutionStatus.Completed, harness.SingleRun().ExecutionStatus);
    }

    // ---- Story 4.6 review fixes: the open-run registry under concurrency and teardown -------------

    /// <summary>Waits for a condition a background close drives, failing the test if it never holds.</summary>
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within 10 seconds.");
            await Task.Delay(10);
        }
    }

    /// <summary>The open-run ledger behind a registration, reached through the handle it hands back.</summary>
    private static OpenRunRegistry OpenRunsOf(IDisposable captureLifetime) =>
        Assert.IsType<ExperienceCaptureMiddleware>(captureLifetime).OpenRuns;

    private static RunExecutionStatus? StatusOf(Harness harness, Guid runId)
    {
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        return run.ExecutionStatus;
    }

    /// <summary>
    /// BH-2. A bound that fires while an invocation holds the run is handed to that invocation once --
    /// and re-armed, so an invocation that never comes back (a hung inner agent, an abandoned
    /// enumerator) cannot suspend the bound forever. The second firing closes the run underneath it.
    /// </summary>
    [Fact]
    public async Task A_bound_reached_while_an_invocation_holds_the_run_rearms_once_then_closes_the_run_underneath_it()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var park = 0;
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);

        var agent = harness.Capture(
            CreateAgent(new ScriptedChatClient
            {
                OnCall = () =>
                {
                    if (Volatile.Read(ref park) == 1)
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    }
                },
            }),
            options);

        await agent.RunAsync("attempt-1");
        var bound = Assert.Single(clock.Bounds);

        Volatile.Write(ref park, 1);
        var hung = Task.Run(() => agent.RunAsync("attempt-2"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // First firing: handed to the live invocation, and re-armed rather than forgotten.
            bound.Fire();
            Assert.Equal(1, bound.Changes);
            Assert.Null(StatusOf(harness, runId));

            // Second firing: the invocation never came back, so the run is closed underneath it.
            bound.Fire();
            await Eventually(() => StatusOf(harness, runId) is not null);
            Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
            await Eventually(() => harness.Failures.Any(failure => failure.Reason.Contains("open-run bound", StringComparison.Ordinal)));
        }
        finally
        {
            release.TrySetResult();
            await hung;
        }
    }

    /// <summary>
    /// BH-3. An opening invocation whose finalization overran its timeout may have left its run open
    /// -- whether the background work will complete it is unknown -- so the run is still registered and
    /// bounded, never left open with no entry and no timer at all.
    /// </summary>
    [Fact]
    public async Task A_run_whose_finalization_overran_its_timeout_is_still_bounded()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        using var block = new ManualResetEventSlim(false);
        harness.Service.BlockAppend = block;

        try
        {
            var options = harness.Options(finalizationTimeout: TimeSpan.FromMilliseconds(100), timeProvider: clock);
            await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-overrun");

            var runId = Assert.Single(harness.Service.StartedRunIds);
            Assert.Contains(harness.Failures, failure => failure.Reason.Contains("did not finish within", StringComparison.Ordinal));
            Assert.Null(StatusOf(harness, runId));

            // The abandoned append never returns, so only the bound can end this run.
            Assert.Single(clock.Bounds).Fire();
            await Eventually(() => StatusOf(harness, runId) is not null);
            Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        }
        finally
        {
            block.Set();
        }
    }

    /// <summary>
    /// BH-5. A bound's close that overruns its own timeout cancels the token the abandoned work holds,
    /// and does not dispose it underneath that work -- the precedent <c>FinalizeAsync</c> set.
    /// </summary>
    [Fact]
    public async Task A_bound_close_that_overruns_its_timeout_cancels_the_abandoned_work()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            finalizationTimeout: TimeSpan.FromMilliseconds(100),
            timeProvider: clock);

        await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("attempt-1");
        harness.Service.HangHonoringToken = true;

        Assert.Single(clock.Bounds).Fire();

        await harness.Service.HangCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => harness.Failures.Any(failure => failure.Reason.Contains("did not finish within", StringComparison.Ordinal)));
    }

    /// <summary>
    /// BH-9. A timer callback that was already running when the run was completed normally -- disposing
    /// a timer does not join it -- must not report the run as still open at its bound.
    /// </summary>
    [Fact]
    public async Task A_late_bound_callback_for_a_run_completed_normally_reports_nothing()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var closing = false;
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => closing,
            timeProvider: clock);

        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);
        await agent.RunAsync("attempt-1");
        closing = true;
        await agent.RunAsync("attempt-2");
        Assert.Equal(RunExecutionStatus.Completed, StatusOf(harness, runId));

        var bound = Assert.Single(clock.Bounds);
        Assert.True(bound.Disposed);
        var completions = harness.Service.CompleteCalls;

        bound.Fire();
        await Eventually(() => harness.Service.CompleteCalls > completions);
        await Task.Delay(200);

        Assert.Empty(harness.Failures);
        Assert.Equal(RunExecutionStatus.Completed, StatusOf(harness, runId));
    }

    /// <summary>
    /// BH-8. Disposing the handle <c>UseExperienceCapture</c> hands back cancels every armed bound, so
    /// no callback completes a run or reaches the host afterwards -- not even one already running --
    /// and later invocations run uncaptured and say why.
    /// </summary>
    [Fact]
    public async Task Disposing_capture_stops_every_bound_and_later_invocations_run_uncaptured()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);

        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();

        await agent.RunAsync("attempt-1");
        var bound = Assert.Single(clock.Bounds);

        captureLifetime.Dispose();
        Assert.True(bound.Disposed);

        // A callback that was already on its way when the timer was disposed does nothing.
        var completions = harness.Service.CompleteCalls;
        bound.Fire();
        await Task.Delay(200);
        Assert.Equal(completions, harness.Service.CompleteCalls);
        Assert.Null(StatusOf(harness, runId));
        Assert.Empty(harness.Failures);

        var response = await agent.RunAsync("attempt-2");
        Assert.Equal("Hello, world", response.Text);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Contains("disposed", failure.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Service.StartedRunIds);
    }

    /// <summary>
    /// BH-8, the other half: an invocation still in flight when capture is disposed abandons its run
    /// rather than arming a bound that would call back into a host that is tearing down, and says so
    /// itself, on its own thread.
    /// </summary>
    [Fact]
    public async Task An_invocation_in_flight_at_disposal_abandons_its_run_and_reports_it_on_its_own_thread()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = harness.Options(shouldComplete: _ => false, timeProvider: clock);

        var agent = CreateAgent(new ScriptedChatClient
        {
            OnCall = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        }).AsBuilder().UseExperienceCapture(harness.Service, options, out var captureLifetime).Build();

        var inFlight = Task.Run(() => agent.RunAsync("task-in-flight"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        captureLifetime.Dispose();
        release.TrySetResult();
        await inFlight;

        Assert.Empty(clock.Bounds);
        Assert.Null(harness.SingleRun().ExecutionStatus);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("disposed while the invocation was in flight", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// BH-12. A run that cannot be read back cannot be judged: the predicate would see zero attempts
    /// and no result, the attempt bound could never trip, and a <c>ctx =&gt; ctx.Result is not null</c>
    /// predicate would keep it open. The run is completed instead, and the miss is reported.
    /// </summary>
    [Fact]
    public async Task A_run_that_cannot_be_read_back_is_completed_and_the_miss_is_reported()
    {
        var harness = new Harness();
        var asked = false;
        harness.Service.MissOnTryGetRun = true;
        var options = harness.Options(shouldComplete: _ =>
        {
            asked = true;
            return false;
        });

        await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-unreadable");
        harness.Service.MissOnTryGetRun = false;

        Assert.False(asked);
        Assert.Equal(RunExecutionStatus.Completed, harness.SingleRun().ExecutionStatus);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("could not be read back", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// BH-14. An all-zeros continuation identifier is a default-valued field, not a run; accepting it
    /// would pile every invocation of every task onto one run.
    /// </summary>
    [Fact]
    public async Task An_all_zeros_continuation_id_is_refused_and_the_invocation_runs_uncaptured()
    {
        var harness = new Harness();
        var options = harness.Options(resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: Guid.Empty));

        var response = await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("task-empty-id");

        Assert.Equal("Hello, world", response.Text);
        Assert.Empty(harness.Service.StartedRunIds);
        Assert.False(harness.Service.TryGetRun(Guid.Empty, out _));
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.ResolveRun, failure.Stage);
        Assert.Contains("all-zeros", failure.Reason, StringComparison.Ordinal);
    }

    // ---- Story 4.6 verification-gap review: tests each able to kill a surviving mutant ------------

    /// <summary>
    /// VG-14. A start that fails -- the service handed back nothing, or threw -- gives back the claim
    /// it took, so the run it named is not held by an invocation that never captured on it and a later,
    /// valid continuation of that run is captured normally.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task A_failed_start_gives_its_claim_back_so_a_later_continuation_on_the_same_id_is_captured(string how)
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var options = harness.Options(resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId));
        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();

        if (how == "null")
        {
            harness.Service.NullStartRuns = 1;
        }
        else
        {
            harness.Service.ThrowingStartRuns = 1;
        }

        Assert.Equal("Hello, world", (await agent.RunAsync("attempt-1")).Text);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Contains(how == "null" ? "StartRun returned null" : "Starting the run threw", failure.Reason, StringComparison.Ordinal);
        Assert.Equal(0, OpenRunsOf(captureLifetime).Count);

        await agent.RunAsync("attempt-2");

        Assert.Single(harness.Failures);
        Assert.Equal(runId, Assert.Single(harness.Service.StartedRunIds));
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Single(run.Attempts);
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
    }

    /// <summary>
    /// VG-16. A close the bound started holds the run until it is done: an invocation naming the run
    /// while the close is still completing it is refused, never let in to append to a run that is
    /// being completed underneath it.
    /// </summary>
    [Fact]
    public async Task A_bound_close_in_progress_holds_the_run_so_no_invocation_can_claim_it()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);

        await agent.RunAsync("attempt-1");
        using var gate = new ManualResetEventSlim(false);
        harness.Service.BlockComplete = gate;
        try
        {
            Assert.Single(clock.Bounds).Fire();
            await harness.Service.CompleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await agent.RunAsync("attempt-2");

            var refusal = Assert.Single(harness.Failures);
            Assert.Equal(ExperienceCaptureFailureStage.StartRun, refusal.Stage);
            Assert.Contains("already capturing", refusal.Reason, StringComparison.Ordinal);
        }
        finally
        {
            gate.Set();
        }

        await Eventually(() => StatusOf(harness, runId) is not null);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Single(run.Attempts);
    }

    /// <summary>
    /// VG-17. A run the bound closed is forgotten with it: a later invocation naming the run is refused
    /// by the store, because the run is complete -- not by the registry, as if the close still held it.
    /// </summary>
    [Fact]
    public async Task A_run_closed_by_its_bound_is_forgotten_so_a_later_invocation_is_refused_by_the_store_not_the_registry()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);
        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();
        var registry = OpenRunsOf(captureLifetime);

        await agent.RunAsync("attempt-1");
        Assert.Single(clock.Bounds).Fire();
        await Eventually(() => registry.Count == 0);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));

        await agent.RunAsync("attempt-2");

        var refusal = Assert.Single(harness.Failures, f => f.Stage == ExperienceCaptureFailureStage.StartRun);
        Assert.Contains("StartRun returned Conflict", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(0, registry.Count);
    }

    /// <summary>VG-18. A bound close whose completion finds no such run says so, rather than nothing.</summary>
    [Fact]
    public async Task A_bound_close_that_finds_no_run_reports_RunNotFound()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);

        await harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunAsync("attempt-1");
        harness.Service.ForcedCompleteOutcome = CompleteRunOutcome.RunNotFound;

        Assert.Single(clock.Bounds).Fire();

        await Eventually(() => harness.Failures.Any(f => f.Reason.Contains("returned RunNotFound", StringComparison.Ordinal)));
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Equal(runId, failure.RunId);
        Assert.DoesNotContain("still open at its", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// VG-3/4. A bound that fires while an invocation holds the run is honoured when that invocation
    /// comes back, even though it asks to keep the run open: the run is completed as Cancelled, and the
    /// bound is reported exactly once -- not again by the re-armed timer's own late firing.
    /// </summary>
    [Fact]
    public async Task A_bound_reached_while_busy_is_honoured_when_the_invocation_returns_asking_to_stay_open()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var park = 0;
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);
        var agent = CreateAgent(new ScriptedChatClient
        {
            OnCall = () =>
            {
                if (Volatile.Read(ref park) == 1)
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }
            },
        }).AsBuilder().UseExperienceCapture(harness.Service, options, out var captureLifetime).Build();
        var registry = OpenRunsOf(captureLifetime);

        await agent.RunAsync("attempt-1");
        var bound = Assert.Single(clock.Bounds);

        Volatile.Write(ref park, 1);
        var busy = Task.Run(() => agent.RunAsync("attempt-2"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        bound.Fire();
        Assert.Equal(1, bound.Changes);
        Assert.Equal(options.MaxOpenRunDuration, bound.ChangedDueTime);
        Assert.Null(StatusOf(harness, runId));

        release.TrySetResult();
        await busy;

        await Eventually(() => registry.Count == 0);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(2, run.Attempts.Count);
        Assert.Single(harness.Failures, f => f.Reason.Contains("still open at its", StringComparison.Ordinal));

        // The re-armed timer firing late finds the run already completed and says nothing more.
        var completions = harness.Service.CompleteCalls;
        bound.Fire();
        await Eventually(() => harness.Service.CompleteCalls > completions);
        await Eventually(() => registry.Count == 0);
        await Task.Delay(100);
        Assert.Single(harness.Failures);
    }

    /// <summary>
    /// VG-5. A host <see cref="TimeProvider"/> that cannot make a timer never leaves a run open with no
    /// bound: the run is completed now, the reason is reported, and it is not misreported as a run
    /// that was still open at its bound.
    /// </summary>
    [Fact]
    public async Task A_TimeProvider_that_cannot_arm_a_bound_gets_the_run_completed_now_and_says_why()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider { ThrowOnBound = true };
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);
        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();
        var registry = OpenRunsOf(captureLifetime);

        Assert.Equal("Hello, world", (await agent.RunAsync("attempt-1")).Text);

        await Eventually(() => registry.Count == 0);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("Arming the open-run bound threw", failure.Reason, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.DoesNotContain(harness.Failures, f => f.Reason.Contains("still open at its", StringComparison.Ordinal));
    }

    /// <summary>
    /// VG-12. The duration bound is also checked when a continuation lands: one arriving exactly at the
    /// bound completes the run instead of leaving it open, and says the bound is why.
    /// </summary>
    [Fact]
    public async Task A_continuation_arriving_exactly_at_the_duration_bound_completes_the_run_and_reports_it()
    {
        var harness = new Harness();
        var openedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualBoundTimeProvider { Now = openedAt };
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);
        var agent = harness.Capture(CreateAgent(new ScriptedChatClient()), options);

        await agent.RunAsync("attempt-1");
        Assert.Null(StatusOf(harness, runId));
        Assert.Empty(harness.Failures);

        clock.Now = openedAt + options.MaxOpenRunDuration;
        await agent.RunAsync("attempt-2");

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(2, run.Attempts.Count);
        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalize, failure.Stage);
        Assert.Contains("at or beyond its", failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// VG-13. The bound measures the run, not the invocation: it is armed for what remains of the
    /// declared duration since the run opened, and armed once -- a continuation that leaves the run
    /// open again does not start a second clock.
    /// </summary>
    [Fact]
    public async Task The_bound_is_armed_for_what_remains_of_the_run_and_only_once()
    {
        var harness = new Harness();
        var openedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualBoundTimeProvider { Now = openedAt };
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            maxOpenRunDuration: TimeSpan.FromMinutes(5),
            timeProvider: clock);

        // The first invocation itself takes two minutes, so three are left when it releases the run.
        var calls = 0;
        var agent = harness.Capture(
            CreateAgent(new ScriptedChatClient
            {
                OnCall = () =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        clock.Now = openedAt.AddMinutes(2);
                    }
                },
            }),
            options);

        await agent.RunAsync("attempt-1");
        var bound = Assert.Single(clock.Bounds);
        Assert.Equal(TimeSpan.FromMinutes(3), bound.DueTime);

        clock.Now = openedAt.AddMinutes(4);
        await agent.RunAsync("attempt-2");

        Assert.Same(bound, Assert.Single(clock.Bounds));
        Assert.Equal(0, bound.Changes);
        Assert.False(bound.Disposed);
        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(2, run.Attempts.Count);
        Assert.Null(run.ExecutionStatus);
        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// VG-11/20, and frozen rule 1 as it is really implemented: the default path takes a transient
    /// claim for the length of each invocation -- the same serialization a continuing run gets -- and
    /// removes it when the invocation releases. After any number of default invocations, the
    /// registration holds no entry and has armed no bound.
    /// </summary>
    [Fact]
    public async Task The_default_path_takes_a_transient_claim_per_invocation_and_leaves_no_entry_and_no_bound_behind()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = 0;
        var agent = CreateAgent(new ScriptedChatClient
        {
            Calls = [EchoCall()],
            OnCall = () =>
            {
                if (Interlocked.Exchange(ref parked, 1) == 0)
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }
            },
        }).AsBuilder().UseExperienceCapture(harness.Service, harness.Options(timeProvider: clock), out var captureLifetime).Build();
        var registry = OpenRunsOf(captureLifetime);

        var first = Task.Run(() => agent.RunAsync("task-default"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, registry.Count);
        release.TrySetResult();
        await first;

        for (var i = 0; i < 4; i++)
        {
            await agent.RunAsync("task-default");
        }

        Assert.Equal(5, harness.Service.StartedRunIds.Count);
        Assert.All(harness.Service.StartedRunIds, runId => Assert.Equal(RunExecutionStatus.Completed, StatusOf(harness, runId)));
        Assert.Equal(0, registry.Count);
        Assert.Empty(clock.Bounds);
        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// VG-2. The completion predicate sees the attempt as it was stored -- redacted by the host's
    /// sanitizer and clamped by the capture limits -- never what the invocation produced.
    /// </summary>
    [Fact]
    public async Task ShouldCompleteRun_sees_the_stored_redacted_and_clamped_values_never_the_raw_ones()
    {
        var harness = new Harness(sanitizer: new RedactingSanitizer(), maxResultLength: 12, maxErrorLength: 12);
        var runId = Guid.NewGuid();
        var seen = new List<ExperienceRunCompletionContext>();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: context =>
            {
                seen.Add(context);
                return context.Error is null;
            });

        var failNow = 1;
        var agent = harness.Capture(
            CreateAgent(new ScriptedChatClient
            {
                FinalChunks = [RedactingSanitizer.Marker],
                OnCall = () =>
                {
                    if (Volatile.Read(ref failNow) == 1)
                    {
                        throw new InvalidOperationException("the first try fails");
                    }
                },
            }),
            options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("attempt-1"));
        Volatile.Write(ref failNow, 0);
        Assert.Equal(RedactingSanitizer.Marker, (await agent.RunAsync("attempt-2")).Text);

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(2, seen.Count);

        // The error the invocation produced is the exception's full type name; what was stored, and
        // what the predicate saw, is the limit's placeholder.
        Assert.NotEqual(typeof(InvalidOperationException).FullName, run.Attempts[0].Error);
        Assert.Equal(run.Attempts[0].Error, seen[0].Error);

        // The result the invocation produced is the raw marker; what was stored is the redaction.
        Assert.Equal(RedactingSanitizer.Redacted, run.Attempts[1].Result);
        Assert.Equal(RedactingSanitizer.Redacted, seen[1].Result);
        Assert.DoesNotContain(seen, context => (context.Result ?? string.Empty).Contains(RedactingSanitizer.Marker, StringComparison.Ordinal));
    }

    // ---- VG-7: the same guarantees through the streaming path -------------------------------------

    [Fact]
    public async Task Row2_through_streaming_a_retry_under_the_same_run_id_is_the_next_attempt()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        var closing = false;
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => closing);

        var failNow = 1;
        var agent = harness.Capture(
            CreateAgent(new ScriptedChatClient
            {
                OnCall = () =>
                {
                    if (Volatile.Read(ref failNow) == 1)
                    {
                        throw new InvalidOperationException("the first try fails");
                    }
                },
            }),
            options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(agent.RunStreamingAsync("attempt-1")));
        Assert.Null(StatusOf(harness, runId));

        closing = true;
        Volatile.Write(ref failNow, 0);
        await CollectAsync(agent.RunStreamingAsync("attempt-2"));

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal([0, 1], run.Attempts.Select(attempt => attempt.SequenceNumber).ToArray());
        Assert.Equal(typeof(InvalidOperationException).FullName, run.Attempts[0].Error);
        Assert.Equal("Hello, world", run.Attempts[1].Result);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task Row8_through_streaming_an_invocation_naming_a_run_its_opener_is_still_streaming_on_is_refused()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentSession? openerSession = null;
        var options = harness.Options(
            resolve: context => new ExperienceRunDescriptor(
                "incident-42",
                TestScope,
                ContinuesRunId: ReferenceEquals(context.Session, openerSession)
                    ? null
                    : Guid.Parse(openerSession!.StateBag.GetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey)!)),
            shouldComplete: _ => false);

        var parked = 0;
        var client = new ScriptedChatClient
        {
            OnCall = () =>
            {
                if (Interlocked.Exchange(ref parked, 1) != 0)
                {
                    return;
                }

                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };

        var agent = harness.Capture(CreateAgent(client, allowConcurrentInvocation: true), options);
        openerSession = await agent.CreateSessionAsync();
        var session = openerSession;

        var first = Task.Run(() => CollectAsync(agent.RunStreamingAsync("attempt-1", session)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var runId = Assert.Single(harness.Service.StartedRunIds);
        await CollectAsync(agent.RunStreamingAsync("attempt-2"));
        release.TrySetResult();
        await first;

        var failure = Assert.Single(harness.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.StartRun, failure.Stage);
        Assert.Equal(runId, failure.RunId);
        Assert.Contains("already capturing", failure.Reason, StringComparison.Ordinal);

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Single(run.Attempts);
    }

    [Fact]
    public async Task A_run_left_open_by_a_streaming_invocation_is_closed_at_its_bound()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);

        await CollectAsync(harness.Capture(CreateAgent(new ScriptedChatClient()), options).RunStreamingAsync("attempt-1"));
        Assert.Null(StatusOf(harness, runId));

        Assert.Single(clock.Bounds).Fire();

        await Eventually(() => StatusOf(harness, runId) is not null);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        await Eventually(() => harness.Failures.Any(f => f.Reason.Contains("still open at its", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A run opened by a streaming invocation and closed by a non-streaming one: both paths share the
    /// registration's one ledger, so the close forgets the entry and disposes the bound the streaming
    /// invocation armed.
    /// </summary>
    [Fact]
    public async Task A_run_mixing_streaming_and_non_streaming_invocations_is_one_run_on_one_ledger()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var closing = false;
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => closing,
            timeProvider: clock);
        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();

        await CollectAsync(agent.RunStreamingAsync("attempt-1"));
        var bound = Assert.Single(clock.Bounds);

        closing = true;
        await agent.RunAsync("attempt-2");

        Assert.True(harness.Service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        Assert.Equal(2, run.Attempts.Count);
        Assert.True(bound.Disposed);
        Assert.Equal(0, OpenRunsOf(captureLifetime).Count);
        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// BH-2's own case, through the real streaming path: a consumer that takes one update and then
    /// abandons the enumerator without disposing it never runs the finalizing <c>finally</c>, so the
    /// invocation never releases the run. The bound is handed to it once, then closes the run
    /// underneath it -- and the run it held is not left refusing every later invocation.
    /// </summary>
    [Fact]
    public async Task A_streaming_enumerator_abandoned_without_disposal_cannot_hold_its_run_past_the_bound()
    {
        var harness = new Harness();
        var clock = new ManualBoundTimeProvider();
        var runId = Guid.NewGuid();
        var options = harness.Options(
            resolve: _ => new ExperienceRunDescriptor("incident-42", TestScope, ContinuesRunId: runId),
            shouldComplete: _ => false,
            timeProvider: clock);
        var agent = CreateAgent(new ScriptedChatClient()).AsBuilder()
            .UseExperienceCapture(harness.Service, options, out var captureLifetime)
            .Build();
        var registry = OpenRunsOf(captureLifetime);

        await agent.RunAsync("attempt-1");
        var bound = Assert.Single(clock.Bounds);

        // Take one update and walk away: no DisposeAsync, so the middleware's finally never runs.
        var abandoned = agent.RunStreamingAsync("attempt-2").GetAsyncEnumerator();
        Assert.True(await abandoned.MoveNextAsync());

        bound.Fire();
        Assert.Equal(1, bound.Changes);
        Assert.Null(StatusOf(harness, runId));

        bound.Fire();
        await Eventually(() => registry.Count == 0);
        Assert.Equal(RunExecutionStatus.Cancelled, StatusOf(harness, runId));
        Assert.Contains(harness.Failures, f => f.Reason.Contains("still open at its", StringComparison.Ordinal));

        // The abandoned invocation's claim went with the run: a later one is refused by the store
        // because the run is complete, not by the ledger as if someone still held it.
        await agent.RunAsync("attempt-3");
        Assert.Contains(harness.Failures, f => f.Reason.Contains("StartRun returned Conflict", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Failures, f => f.Reason.Contains("already capturing", StringComparison.Ordinal));
        GC.KeepAlive(abandoned);
    }

    /// <summary>A sanitizer that redacts one marker wherever it appears and passes everything else through.</summary>
    private sealed class RedactingSanitizer : ISanitizer
    {
        public const string Marker = "RAW-MARKER";
        public const string Redacted = "[redacted]";

        public Task<SanitizedPayload> SanitizeAsync(RawPayload payload, CancellationToken cancellationToken = default)
        {
            var fields = payload.Fields.ToDictionary(
                field => field.Key,
                field => field.Value is string text ? text.Replace(Marker, Redacted, StringComparison.Ordinal) : field.Value,
                StringComparer.Ordinal);
            return Task.FromResult(new SanitizedPayload(SanitizationDecision.Allowed, fields, [], [], null));
        }
    }

    [Theory]
    [InlineData("duration-zero")]
    [InlineData("duration-max")]
    [InlineData("attempts-zero")]
    public void Out_of_range_open_run_bounds_throw_at_build(string value)
    {
        var harness = new Harness();
        var options = value switch
        {
            "duration-zero" => harness.Options(maxOpenRunDuration: TimeSpan.Zero),
            "duration-max" => harness.Options(maxOpenRunDuration: TimeSpan.MaxValue),
            _ => harness.Options(maxAttemptsPerOpenRun: 0),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateAgent(new ScriptedChatClient()).AsBuilder().UseExperienceCapture(harness.Service, options).Build());
    }

    [Fact]
    public void A_null_ShouldCompleteRun_throws_at_build()
    {
        var harness = new Harness();
        var options = harness.Options(shouldComplete: null);
        options = new ExperienceCaptureOptions
        {
            ResolveRun = options.ResolveRun,
            ShouldCompleteRun = null!,
        };

        Assert.Throws<ArgumentNullException>(() =>
            CreateAgent(new ScriptedChatClient()).AsBuilder().UseExperienceCapture(harness.Service, options).Build());
    }
}
