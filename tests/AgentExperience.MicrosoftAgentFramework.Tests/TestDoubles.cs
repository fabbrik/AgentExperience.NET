using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentExperience.MicrosoftAgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>A scripted function call the fake model emits on its first turn.</summary>
internal sealed record ScriptedCall(string ToolName, Func<string, IDictionary<string, object?>> Arguments);

/// <summary>
/// A deterministic fake <see cref="IChatClient"/> (extends the <c>MafHooksProof.cs</c> pattern): on a
/// turn with no tool results yet it emits <see cref="Calls"/> as <see cref="FunctionCallContent"/>;
/// otherwise it answers with <see cref="FinalChunks"/>. It can throw (before any output, or mid-stream
/// after the first chunk) or block until cancelled. Stateless per call, so it is safe for concurrent runs.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public static readonly InvalidOperationException ThrownException = new("scripted chat client failure");

    public IReadOnlyList<ScriptedCall> Calls { get; init; } = [];

    public IReadOnlyList<string> FinalChunks { get; init; } = ["Hello", ", world"];

    public bool Throw { get; init; }

    public Exception ExceptionToThrow { get; init; } = ThrownException;

    public bool BlockUntilCancelled { get; init; }

    public Action? OnCall { get; init; }

    public ConcurrentQueue<string> UserTexts { get; } = new();

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        foreach (var message in list.Where(m => m.Role == ChatRole.User))
        {
            UserTexts.Enqueue(message.Text);
        }

        OnCall?.Invoke();
        Entered.TrySetResult();

        if (Throw)
        {
            throw ExceptionToThrow;
        }

        if (BlockUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (NeedsToolCalls(list))
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, FunctionCalls(list)));
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(FinalChunks)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        OnCall?.Invoke();

        if (NeedsToolCalls(list))
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, FunctionCalls(list));
            yield break;
        }

        for (var i = 0; i < FinalChunks.Count; i++)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, FinalChunks[i]);

            if (i == 0)
            {
                Entered.TrySetResult();
                if (Throw)
                {
                    throw ExceptionToThrow;
                }

                if (BlockUntilCancelled)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private bool NeedsToolCalls(List<ChatMessage> messages) =>
        Calls.Count > 0 && !messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();

    private List<AIContent> FunctionCalls(List<ChatMessage> messages)
    {
        var userText = messages.Last(m => m.Role == ChatRole.User).Text;
        return Calls
            .Select((call, index) => (AIContent)new FunctionCallContent($"call-{index}", call.ToolName, call.Arguments(userText)))
            .ToList();
    }
}

/// <summary>A tool that returns a fixed raw object (not marshalled to JSON by MEAI).</summary>
internal sealed class RawResultFunction(string name, object? result) : AIFunction
{
    public override string Name => name;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(result);
}

/// <summary>A POCO tool result.</summary>
internal sealed record ToolPoco(string Name, int Count);

/// <summary>A sequence that can be enumerated only once; later enumerations are empty.</summary>
internal sealed class OneShotEnumerable<T>(IEnumerable<T> items) : IEnumerable<T>
{
    private IEnumerator<T>? _enumerator = items.GetEnumerator();

    public IEnumerator<T> GetEnumerator() => Interlocked.Exchange(ref _enumerator, null) ?? Enumerable.Empty<T>().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A tool failure whose message must never be captured.</summary>
internal sealed class ToolFailureException(string message) : Exception(message);

/// <summary>
/// Wraps a real <see cref="IExperienceCaptureService"/>, recording started run IDs and call counts,
/// and optionally throwing or hanging on finalization calls.
/// </summary>
internal sealed class RecordingCaptureService(IExperienceCaptureService inner) : IExperienceCaptureService
{
    private int _appendCalls;
    private int _completeCalls;

    public ConcurrentQueue<Guid> StartedRunIds { get; } = new();

    public bool ThrowOnFinalize { get; set; }

    public bool HangOnFinalize { get; set; }

    public bool ThrowOnAppendOnly { get; set; }

    public bool HangHonoringToken { get; set; }

    public ManualResetEventSlim? BlockAppend { get; set; }

    public AppendAttemptOutcome? ForcedAppendOutcome { get; set; }

    public CompleteRunOutcome? ForcedCompleteOutcome { get; set; }

    public int AppendCalls => _appendCalls;

    public int CompleteCalls => _completeCalls;

    public StartRunResult StartRun(Guid runId, string taskId, string? taskDescription, Scope scope, EnvironmentFingerprint environment, Provenance provenance, DateTimeOffset startedAt)
    {
        var result = inner.StartRun(runId, taskId, taskDescription, scope, environment, provenance, startedAt);
        if (result.Outcome == StartRunOutcome.Started)
        {
            StartedRunIds.Enqueue(runId);
        }

        return result;
    }

    public Task<AppendAttemptResult> AppendAttemptAsync(Guid runId, AppendAttemptRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _appendCalls);
        if (ThrowOnFinalize || ThrowOnAppendOnly)
        {
            throw new InvalidOperationException("capture store unavailable");
        }

        BlockAppend?.Wait();

        if (HangHonoringToken)
        {
            return Hang<AppendAttemptResult>(cancellationToken);
        }

        if (ForcedAppendOutcome is { } appendOutcome)
        {
            return Task.FromResult(new AppendAttemptResult(appendOutcome, [], "forced"));
        }

        if (HangOnFinalize)
        {
            return new TaskCompletionSource<AppendAttemptResult>().Task;
        }

        return inner.AppendAttemptAsync(runId, request, cancellationToken);
    }

    public Task<CompleteRunResult> CompleteRunAsync(Guid runId, Guid completionEventId, RunExecutionStatus executionStatus, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _completeCalls);
        if (ThrowOnFinalize)
        {
            throw new InvalidOperationException("capture store unavailable");
        }

        if (HangHonoringToken)
        {
            return Hang<CompleteRunResult>(cancellationToken);
        }

        if (ForcedCompleteOutcome is { } completeOutcome)
        {
            return Task.FromResult(new CompleteRunResult(completeOutcome, "forced"));
        }

        if (HangOnFinalize)
        {
            return new TaskCompletionSource<CompleteRunResult>().Task;
        }

        return inner.CompleteRunAsync(runId, completionEventId, executionStatus, endedAt, cancellationToken);
    }

    public bool TryGetRun(Guid runId, [NotNullWhen(true)] out ExperienceRun? run) => inner.TryGetRun(runId, out run);

    private static async Task<T> Hang<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }
}

/// <summary>A hand-rolled, non-<see cref="ChatClientAgent"/> agent with no tool pipeline.</summary>
internal sealed class ScriptedAgent : AIAgent
{
    public bool Throw { get; init; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new ScriptedSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(session.StateBag.Serialize());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(new ScriptedSession());

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (Throw)
        {
            throw ScriptedChatClient.ThrownException;
        }

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, "scripted agent reply"));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "scripted ");
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "stream");
    }

    private sealed class ScriptedSession : AgentSession;
}
