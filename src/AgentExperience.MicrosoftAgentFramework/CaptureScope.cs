using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// The per-invocation capture state: the run ID opened before the inner agent executes, a
/// thread-safe buffer of the tool calls observed during the invocation, and a once-only,
/// timeout-bounded finalization (append the single attempt, then complete the run). Correlation
/// between run middleware and function middleware goes through <see cref="Current"/>, an
/// <see cref="AsyncLocal{T}"/> set by run middleware.
/// </summary>
/// <remarks>
/// Nothing on this type ever throws into MAF or the caller: every capture problem is routed to
/// <see cref="ExperienceCaptureOptions.OnCaptureFailure"/> (at most once per
/// <see cref="ExperienceCaptureFailureStage"/> per run) and swallowed.
/// </remarks>
internal sealed class CaptureScope
{
    internal const string ProvenanceSource = "AgentExperience.MicrosoftAgentFramework";

    private static readonly AsyncLocal<CaptureScope?> CurrentScope = new();

    private static readonly string? AdapterVersion =
        typeof(CaptureScope).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CaptureScope).Assembly.GetName().Version?.ToString();

    private readonly IExperienceCaptureService _service;
    private readonly ExperienceCaptureOptions _options;
    private readonly long _startTimestamp;
    private readonly object _toolCallsGate = new();
    private readonly List<(long Order, RawToolCall Call)> _toolCalls = [];
    private long _nextToolCallOrder;
    private int _finalized;
    private int _reportedStages;

    private CaptureScope(IExperienceCaptureService service, ExperienceCaptureOptions options, Guid runId, DateTimeOffset startedAt, long startTimestamp)
    {
        _service = service;
        _options = options;
        RunId = runId;
        StartedAt = startedAt;
        _startTimestamp = startTimestamp;
    }

    /// <summary>The capture scope of the invocation executing on the current async flow, if any.</summary>
    internal static CaptureScope? Current
    {
        get => CurrentScope.Value;
        set => CurrentScope.Value = value;
    }

    /// <summary>The run opened for this invocation.</summary>
    internal Guid RunId { get; }

    /// <summary>When the run (and its single attempt) started.</summary>
    internal DateTimeOffset StartedAt { get; }

    /// <summary>Whether finalization has already been triggered for this run.</summary>
    internal bool IsFinalized => Volatile.Read(ref _finalized) != 0;

    /// <summary>
    /// Resolves the run descriptor, opens the run, and (when the caller supplied a session) writes
    /// the run ID to it. Returns <see langword="null"/> -- the invocation then runs uncaptured --
    /// when any of that fails; the failure is reported, never thrown.
    /// </summary>
    internal static CaptureScope? TryBegin(
        IExperienceCaptureService service,
        ExperienceCaptureOptions options,
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AIAgent agent)
    {
        ExperienceRunDescriptor? descriptor;
        try
        {
            descriptor = options.ResolveRun(new ExperienceRunContext(messages, session, agent));
        }
        catch (Exception ex)
        {
            Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.ResolveRun, null, $"ResolveRun threw {ex.GetType().FullName}; the invocation runs uncaptured.", ex));
            return null;
        }

        if (descriptor is null || descriptor.TaskId is null || descriptor.Scope is null)
        {
            Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.ResolveRun, null, "ResolveRun returned a null descriptor, TaskId, or Scope; the invocation runs uncaptured.", null));
            return null;
        }

        Guid? assignedRunId = null;
        Guid runId;
        DateTimeOffset startedAt;
        long startTimestamp;
        try
        {
            runId = options.NewId();
            assignedRunId = runId;
            startedAt = options.TimeProvider.GetUtcNow();
            startTimestamp = options.TimeProvider.GetTimestamp();

            var provenance = new Provenance(
                Source: ProvenanceSource,
                SourceVersion: AdapterVersion,
                RecordedAt: startedAt,
                CorrelationId: Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.TraceId.ToHexString() : null);

            var started = service.StartRun(runId, descriptor.TaskId, descriptor.TaskDescription, descriptor.Scope, options.Environment, provenance, startedAt);
            if (started is null || started.Outcome != StartRunOutcome.Started)
            {
                Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.StartRun, runId, $"StartRun returned {started?.Outcome.ToString() ?? "null"}; the invocation runs uncaptured.", null));
                return null;
            }
        }
        catch (Exception ex)
        {
            Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.StartRun, assignedRunId, $"Starting the run threw {ex.GetType().FullName}; the invocation runs uncaptured.", ex));
            return null;
        }

        var scope = new CaptureScope(service, options, runId, startedAt, startTimestamp);

        if (session is not null)
        {
            try
            {
                session.StateBag.SetValue(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, runId.ToString("D", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                scope.ReportFailure(ExperienceCaptureFailureStage.StartRun, $"Writing the run ID to the session threw {ex.GetType().FullName}.", ex);
            }
        }

        return scope;
    }

    /// <summary>
    /// Marks the start of one tool call. Returns <see langword="null"/> when the run is already
    /// finalized (the call is then not recorded) or the start could not be captured.
    /// </summary>
    internal PendingToolCall? BeginToolCall(FunctionInvocationContext context)
    {
        if (IsFinalized)
        {
            return null;
        }

        try
        {
            var arguments = context.Arguments is { } raw
                ? new Dictionary<string, object?>(raw, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            var toolCallId = _options.NewId();
            var toolName = context.Function?.Name ?? context.CallContent?.Name ?? string.Empty;

            // Order and start time are taken together so "ordered by start" is consistent with StartedAt
            // even when MAF invokes tools concurrently.
            lock (_toolCallsGate)
            {
                return new PendingToolCall(
                    Order: ++_nextToolCallOrder,
                    ToolCallId: toolCallId,
                    ToolName: toolName,
                    Arguments: arguments,
                    StartedAt: _options.TimeProvider.GetUtcNow(),
                    StartTimestamp: _options.TimeProvider.GetTimestamp());
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ExperienceCaptureFailureStage.ToolCall, $"Capturing a tool call's start threw {ex.GetType().FullName}; the tool call is not recorded.", ex);
            return null;
        }
    }

    /// <summary>Records a tool call that returned <paramref name="result"/>.</summary>
    internal void CompleteToolCall(PendingToolCall pending, object? result)
    {
        string? text;
        try
        {
            text = result switch
            {
                null => null,
                string value => value,
                JsonElement element => element.ToString(),
                _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
            };
        }
        catch (Exception ex)
        {
            ReportFailure(ExperienceCaptureFailureStage.ToolCall, $"Converting a tool result to text threw {ex.GetType().FullName}; the tool call is not recorded.", ex);
            return;
        }

        AddToolCall(pending, text, error: null);
    }

    /// <summary>Records a tool call that threw <paramref name="exception"/> (type name only).</summary>
    internal void FailToolCall(PendingToolCall pending, Exception exception) =>
        AddToolCall(pending, result: null, error: ErrorOf(exception));

    /// <summary>
    /// Finalizes the run exactly once: appends the invocation's single attempt (every buffered tool
    /// call, ordered by start) and then completes the run. Bounded by
    /// <see cref="ExperienceCaptureOptions.FinalizationTimeout"/> with its own token, never the
    /// caller's. A second call is a no-op. Never throws.
    /// </summary>
    internal async Task FinalizeAsync(RunExecutionStatus status, string? result, string? error)
    {
        if (Interlocked.Exchange(ref _finalized, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? timeoutSource = null;
        try
        {
            var timeProvider = _options.TimeProvider;
            var endedAt = timeProvider.GetUtcNow();
            var duration = timeProvider.GetElapsedTime(_startTimestamp);

            RawToolCall[] toolCalls;
            lock (_toolCallsGate)
            {
                toolCalls = _toolCalls.OrderBy(entry => entry.Order).Select(entry => entry.Call).ToArray();
            }

            var request = new AppendAttemptRequest(_options.NewId(), StartedAt, duration, toolCalls, result, error);
            var completionEventId = _options.NewId();

            // Cancelled only after a timeout has been reported (no timer of its own), so a token-honoring
            // service can never race a cancellation failure ahead of the timeout report.
            timeoutSource = new CancellationTokenSource();
            var token = timeoutSource.Token;

            // Task.Run also bounds a capture service that blocks or throws synchronously.
            var work = Task.Run(() => FinalizeCoreAsync(request, completionEventId, status, endedAt, token), CancellationToken.None);
            await work.WaitAsync(_options.FinalizationTimeout, timeProvider).ConfigureAwait(false);

            timeoutSource.Dispose();
        }
        catch (TimeoutException ex)
        {
            // Report first, so a token-honoring service's cancellation cannot be reported in its place.
            ReportFailure(ExperienceCaptureFailureStage.Finalize, $"Finalization did not finish within {_options.FinalizationTimeout}.", ex);

            // Leave the token source undisposed: the abandoned finalization may still observe its token.
            try
            {
                timeoutSource?.Cancel();
            }
            catch
            {
                // A throwing cancellation callback must never affect the agent invocation.
            }
        }
        catch (Exception ex)
        {
            timeoutSource?.Dispose();
            ReportFailure(ExperienceCaptureFailureStage.Finalize, $"Finalization threw {ex.GetType().FullName}.", ex);
        }
    }

    /// <summary>The error text recorded for an exception: its full type name, never its message.</summary>
    internal static string ErrorOf(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;

    private async Task FinalizeCoreAsync(AppendAttemptRequest request, Guid completionEventId, RunExecutionStatus status, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        var problems = new List<string>(2);
        Exception? firstException = null;

        try
        {
            var appended = await _service.AppendAttemptAsync(RunId, request, cancellationToken).ConfigureAwait(false);
            if (appended is null || appended.Outcome is not (AppendAttemptOutcome.Recorded or AppendAttemptOutcome.DuplicateNoOp))
            {
                problems.Add($"AppendAttemptAsync returned {appended?.Outcome.ToString() ?? "null"}.");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"AppendAttemptAsync threw {ex.GetType().FullName}.");
            firstException = ex;
        }

        // Completion is attempted even when the append failed. The run may still be left open if
        // finalization times out (completion then starts with an already-cancelled token).
        try
        {
            var completed = await _service.CompleteRunAsync(RunId, completionEventId, status, endedAt, cancellationToken).ConfigureAwait(false);
            if (completed is null || completed.Outcome is not (CompleteRunOutcome.Recorded or CompleteRunOutcome.DuplicateNoOp))
            {
                problems.Add($"CompleteRunAsync returned {completed?.Outcome.ToString() ?? "null"}.");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"CompleteRunAsync threw {ex.GetType().FullName}.");
            firstException ??= ex;
        }

        if (problems.Count > 0)
        {
            ReportFailure(ExperienceCaptureFailureStage.Finalize, string.Join(" ", problems), firstException);
        }
    }

    private void AddToolCall(PendingToolCall pending, string? result, string? error)
    {
        try
        {
            var call = new RawToolCall(
                ToolCallId: pending.ToolCallId,
                ToolName: pending.ToolName,
                Arguments: pending.Arguments,
                StartedAt: pending.StartedAt,
                Duration: _options.TimeProvider.GetElapsedTime(pending.StartTimestamp),
                Result: result,
                Error: error);

            lock (_toolCallsGate)
            {
                // A tool call finishing after finalization took its snapshot is not recorded.
                if (!IsFinalized)
                {
                    _toolCalls.Add((pending.Order, call));
                }
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ExperienceCaptureFailureStage.ToolCall, $"Recording a tool call threw {ex.GetType().FullName}; the tool call is not recorded.", ex);
        }
    }

    private void ReportFailure(ExperienceCaptureFailureStage stage, string reason, Exception? exception)
    {
        // At most one report per stage per run, so an earlier minor failure never hides a later one.
        var bit = 1 << (int)stage;
        int current;
        do
        {
            current = Volatile.Read(ref _reportedStages);
            if ((current & bit) != 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _reportedStages, current | bit, current) != current);

        Report(_options, new ExperienceCaptureFailure(stage, RunId, reason, exception));
    }

    private static void Report(ExperienceCaptureOptions options, ExperienceCaptureFailure failure)
    {
        try
        {
            options.OnCaptureFailure?.Invoke(failure);
        }
        catch
        {
            // The host's failure callback must never affect the agent invocation.
        }
    }

    /// <summary>A tool call that has started but not yet finished.</summary>
    internal sealed record PendingToolCall(
        long Order,
        Guid ToolCallId,
        string ToolName,
        IReadOnlyDictionary<string, object?> Arguments,
        DateTimeOffset StartedAt,
        long StartTimestamp);
}
