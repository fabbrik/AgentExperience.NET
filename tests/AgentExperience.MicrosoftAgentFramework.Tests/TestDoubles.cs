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
    private int _nullStartRuns;
    private int _throwingStartRuns;

    public ConcurrentQueue<Guid> StartedRunIds { get; } = new();

    public bool ThrowOnFinalize { get; set; }

    public bool HangOnFinalize { get; set; }

    public bool ThrowOnAppendOnly { get; set; }

    public bool HangHonoringToken { get; set; }

    public ManualResetEventSlim? BlockAppend { get; set; }

    public AppendAttemptOutcome? ForcedAppendOutcome { get; set; }

    /// <summary>When set, <see cref="TryGetRun"/> reports a miss, as a store that cannot read a run back would.</summary>
    public bool MissOnTryGetRun { get; set; }

    /// <summary>Completed when a <see cref="HangHonoringToken"/> hang observes its token being cancelled.</summary>
    public TaskCompletionSource HangCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CompleteRunOutcome? ForcedCompleteOutcome { get; set; }

    /// <summary>When set, <see cref="CompleteRunAsync"/> signals <see cref="CompleteEntered"/> and then waits on it.</summary>
    public ManualResetEventSlim? BlockComplete { get; set; }

    /// <summary>Completed when a <see cref="BlockComplete"/>-gated completion has been entered.</summary>
    public TaskCompletionSource CompleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many of the next <see cref="StartRun"/> calls return <see langword="null"/>, as a non-conforming service would.</summary>
    public int NullStartRuns
    {
        get => Volatile.Read(ref _nullStartRuns);
        set => Volatile.Write(ref _nullStartRuns, value);
    }

    /// <summary>How many of the next <see cref="StartRun"/> calls throw.</summary>
    public int ThrowingStartRuns
    {
        get => Volatile.Read(ref _throwingStartRuns);
        set => Volatile.Write(ref _throwingStartRuns, value);
    }

    public int AppendCalls => _appendCalls;

    public int CompleteCalls => _completeCalls;

    /// <summary>When set, <see cref="RecordExposure"/> throws, as a failing capture store would.</summary>
    public bool ThrowOnRecordExposure { get; set; }

    /// <summary>When set, <see cref="RecordExposure"/> returns this outcome and records nothing, as a service that does not record exposure would.</summary>
    public RecordExposureOutcome? ForcedExposureOutcome { get; set; }

    /// <summary>Every exposure list the adapter recorded, in order.</summary>
    public ConcurrentQueue<(Guid RunId, IReadOnlyList<RunExposure> Exposures)> RecordedExposures { get; } = new();

    public RecordExposureResult RecordExposure(Guid runId, IReadOnlyList<RunExposure> exposures)
    {
        if (ThrowOnRecordExposure)
        {
            throw new InvalidOperationException("capture store unavailable while recording exposure");
        }

        RecordedExposures.Enqueue((runId, exposures));
        return ForcedExposureOutcome is { } forced
            ? new RecordExposureResult(forced, "forced by the test")
            : inner.RecordExposure(runId, exposures);
    }

    public StartRunResult StartRun(Guid runId, string taskId, string? taskDescription, Scope scope, EnvironmentFingerprint environment, Provenance provenance, DateTimeOffset startedAt)
    {
        if (Interlocked.Decrement(ref _nullStartRuns) >= 0)
        {
            return null!;
        }

        Interlocked.Exchange(ref _nullStartRuns, 0);
        if (Interlocked.Decrement(ref _throwingStartRuns) >= 0)
        {
            throw new InvalidOperationException("capture store unavailable at start");
        }

        Interlocked.Exchange(ref _throwingStartRuns, 0);
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

        if (BlockComplete is { } gate)
        {
            CompleteEntered.TrySetResult();
            gate.Wait();
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

    public bool TryGetRun(Guid runId, [NotNullWhen(true)] out ExperienceRun? run)
    {
        if (MissOnTryGetRun)
        {
            run = null;
            return false;
        }

        return inner.TryGetRun(runId, out run);
    }

    private async Task<T> Hang<T>(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            HangCancelled.TrySetResult();
            throw;
        }

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

/// <summary>
/// A <see cref="TimeProvider"/> whose open-run duration bounds fire only when a test fires them, so a
/// bound can be driven into exactly the interleaving under test. Every other timer -- the finalization
/// timeout's, for one -- is a real system timer, and the clock itself is the system clock.
/// </summary>
internal sealed class ManualBoundTimeProvider : TimeProvider
{
    private readonly List<ManualBoundTimer> _bounds = [];
    private readonly object _nowGate = new();
    private DateTimeOffset? _now;

    /// <summary>
    /// When set, the clock reads exactly this instant instead of the system clock, so a test can put a
    /// run at, or just past, its bound to the tick. Timestamps and non-bound timers stay real.
    /// </summary>
    public DateTimeOffset? Now
    {
        get
        {
            lock (_nowGate)
            {
                return _now;
            }
        }

        set
        {
            lock (_nowGate)
            {
                _now = value;
            }
        }
    }

    /// <summary>When set, creating an open-run bound throws, as a broken host <see cref="TimeProvider"/> would.</summary>
    public bool ThrowOnBound { get; set; }

    public override DateTimeOffset GetUtcNow() => Now ?? base.GetUtcNow();

    public IReadOnlyList<ManualBoundTimer> Bounds
    {
        get
        {
            lock (_bounds)
            {
                return _bounds.ToList();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (state is not OpenRun)
        {
            return System.CreateTimer(callback, state, dueTime, period);
        }

        if (ThrowOnBound)
        {
            throw new InvalidOperationException("no timers here");
        }

        var timer = new ManualBoundTimer(callback, state, dueTime);
        lock (_bounds)
        {
            _bounds.Add(timer);
        }

        return timer;
    }
}

/// <summary>An open-run bound that fires when <see cref="Fire"/> is called, disposed or not -- as a real callback already running at disposal would.</summary>
internal sealed class ManualBoundTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
{
    private int _changes;

    public bool Disposed { get; private set; }

    public int Changes => Volatile.Read(ref _changes);

    /// <summary>The due time the bound was created with: what remained of the run's open duration when it was armed.</summary>
    public TimeSpan DueTime { get; } = dueTime;

    /// <summary>The due time of the most recent <see cref="Change"/>, if any.</summary>
    public TimeSpan? ChangedDueTime { get; private set; }

    public void Fire() => callback(state);

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _changes);
        ChangedDueTime = dueTime;
        return !Disposed;
    }

    public void Dispose() => Disposed = true;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
