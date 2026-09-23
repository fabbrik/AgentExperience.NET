using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// The per-invocation capture state: the run ID opened -- or continued -- before the inner agent
/// executes, a thread-safe buffer of the tool calls observed during the invocation, and a once-only,
/// timeout-bounded finalization (append this invocation's attempt, then complete the run unless the
/// host asked to keep it open for a further attempt). Correlation between run middleware and
/// function middleware goes through <see cref="Current"/>, an <see cref="AsyncLocal{T}"/> set by run
/// middleware.
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
    private readonly OpenRunRegistry _registry;
    private readonly OpenRun _openRun;
    private readonly ExperienceRunDescriptor _descriptor;
    private readonly long _startTimestamp;
    private readonly object _toolCallsGate = new();
    private readonly List<(long Order, RawToolCall Call)> _toolCalls = [];
    private long _nextToolCallOrder;
    private int _finalized;
    private int _reportedStages;
    private int _completedRun;

    private CaptureScope(
        IExperienceCaptureService service,
        ExperienceCaptureOptions options,
        OpenRunRegistry registry,
        OpenRun openRun,
        ExperienceRunDescriptor descriptor,
        Guid runId,
        DateTimeOffset runStartedAt,
        DateTimeOffset startedAt,
        long startTimestamp)
    {
        _service = service;
        _options = options;
        _registry = registry;
        _openRun = openRun;
        _descriptor = descriptor;
        RunId = runId;
        RunStartedAt = runStartedAt;
        StartedAt = startedAt;
        _startTimestamp = startTimestamp;
    }

    /// <summary>The capture scope of the invocation executing on the current async flow, if any.</summary>
    internal static CaptureScope? Current
    {
        get => CurrentScope.Value;
        set => CurrentScope.Value = value;
    }

    /// <summary>The run opened -- or continued -- for this invocation.</summary>
    internal Guid RunId { get; }

    /// <summary>
    /// When the run itself was opened. Equal to <see cref="StartedAt"/> for a run this invocation
    /// opened, and earlier than it for a run this invocation continued. The open-run duration bound
    /// is measured from here, so continuing a run does not restart its clock.
    /// </summary>
    internal DateTimeOffset RunStartedAt { get; }

    /// <summary>When this invocation's attempt started.</summary>
    internal DateTimeOffset StartedAt { get; }

    /// <summary>Whether finalization has already been triggered for this run.</summary>
    internal bool IsFinalized => Volatile.Read(ref _finalized) != 0;

    /// <summary>
    /// Resolves the run descriptor, opens the run -- or continues the one the descriptor names -- and
    /// (when the caller supplied a session) writes the run ID to it. Returns <see langword="null"/>
    /// -- the invocation then runs uncaptured -- when any of that fails; the failure is reported,
    /// never thrown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run -- the one the descriptor names, or the one this invocation is about to mint -- is
    /// claimed in <paramref name="registry"/> first, so a second invocation naming it while this one
    /// is still in flight is refused rather than interleaved, and only then is the capture service
    /// asked whether the identifier really is the same, still-open run. The claim is taken for a run
    /// this invocation <em>opens</em> exactly as for one it continues: the identifier is written to
    /// the session as soon as the run is open, so a concurrent invocation can read it and name it
    /// while this one is still capturing.
    /// </para>
    /// </remarks>
    internal static CaptureScope? TryBegin(
        IExperienceCaptureService service,
        ExperienceCaptureOptions options,
        OpenRunRegistry registry,
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

        // An all-zeros continuation identifier is a default-valued struct field, not a run: accepting
        // it would silently accumulate every invocation of every task onto one run.
        if (descriptor.ContinuesRunId == Guid.Empty)
        {
            Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.ResolveRun, null, "ResolveRun returned an all-zeros ContinuesRunId, which is a default-valued field rather than a run identifier; the invocation runs uncaptured.", null));
            return null;
        }

        OpenRun? claimed = null;
        var createdEntry = false;
        Guid? assignedRunId = descriptor.ContinuesRunId;
        Guid runId;
        DateTimeOffset runStartedAt;
        DateTimeOffset startedAt;
        long startTimestamp;
        try
        {
            runId = descriptor.ContinuesRunId ?? options.NewId();
            assignedRunId = runId;

            // Claimed before the run is opened, and for an opening invocation as much as for a
            // continuing one: the run identifier is readable from the session the moment the run
            // exists, so "nobody can name a run I only just opened" is not true and an unclaimed
            // opener would let a second live scope share its run.
            claimed = registry.TryClaim(runId, out createdEntry);
            if (claimed is null && registry.IsDisposed)
            {
                Report(options, new ExperienceCaptureFailure(
                    ExperienceCaptureFailureStage.StartRun,
                    runId,
                    "Capture for this agent has been disposed; the invocation runs uncaptured.",
                    null));
                return null;
            }

            if (claimed is null)
            {
                Report(options, new ExperienceCaptureFailure(
                    ExperienceCaptureFailureStage.StartRun,
                    runId,
                    "Another invocation is already capturing on this run; the invocation runs uncaptured rather than interleaving a second attempt.",
                    null));
                return null;
            }

            startedAt = options.TimeProvider.GetUtcNow();
            startTimestamp = options.TimeProvider.GetTimestamp();

            var provenance = new Provenance(
                Source: ProvenanceSource,
                SourceVersion: AdapterVersion,
                RecordedAt: startedAt,
                CorrelationId: Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.TraceId.ToHexString() : null);

            var started = service.StartRun(runId, descriptor.TaskId, descriptor.TaskDescription, descriptor.Scope, options.Environment, provenance, startedAt);
            if (started is null || started.Outcome is not (StartRunOutcome.Started or StartRunOutcome.Continued))
            {
                Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.StartRun, runId, $"StartRun returned {started?.Outcome.ToString() ?? "null"}; the invocation runs uncaptured.", null));
                Unclaim(registry, claimed, createdEntry);
                return null;
            }

            // A continued run keeps the start time it was opened with, so the open-run duration bound
            // measures the run and not this one invocation of it.
            runStartedAt = started.Outcome == StartRunOutcome.Continued && started.Run is { } existing
                ? existing.StartedAt
                : startedAt;
        }
        catch (Exception ex)
        {
            Report(options, new ExperienceCaptureFailure(ExperienceCaptureFailureStage.StartRun, assignedRunId, $"Starting the run threw {ex.GetType().FullName}; the invocation runs uncaptured.", ex));
            Unclaim(registry, claimed, createdEntry);
            return null;
        }

        var scope = new CaptureScope(service, options, registry, claimed, descriptor, runId, runStartedAt, startedAt, startTimestamp);

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
    /// Gives back a claim taken for a run that then failed to open, so a failed start leaves no entry
    /// behind when it created one and does not steal a genuinely open run when it did not.
    /// </summary>
    private static void Unclaim(OpenRunRegistry registry, OpenRun? claimed, bool created)
    {
        if (claimed is null)
        {
            return;
        }

        if (created)
        {
            registry.Withdraw(claimed);
            return;
        }

        registry.Unclaim(claimed);
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

            // The resolved function's own name, and deliberately nothing else. MAF resolves the name
            // the model emitted against the host's tool inventory and short-circuits to "Function not
            // found" before any middleware runs, so this is the name the host registered the tool
            // under -- fixed at registration time, not derived from this run's data flow. A fallback
            // to the model's own CallContent.Name used to sit here; it was unreachable, and it read
            // as if model output were an acceptable source for the one captured field that later
            // reaches a model through injection.
            var toolName = context.Function?.Name ?? string.Empty;

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
    /// Finalizes this invocation exactly once: appends its attempt (every buffered tool call, ordered
    /// by start), asks <see cref="ExperienceCaptureOptions.ShouldCompleteRun"/> whether the run is
    /// finished, and -- when it is -- completes the run and, if the host configured one, hands it to
    /// Core's finalization service to become a durable Experience Record. When the run is to stay
    /// open, nothing is completed and nothing is finalized: a later invocation naming this run
    /// appends the next attempt to it. Bounded by
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
        finally
        {
            ReleaseRun();
        }
    }

    /// <summary>
    /// Gives the run back, whichever way finalization went. A run this invocation completed is
    /// forgotten; a run that is still open -- because the host asked to keep it open, or because
    /// finalization failed or overran and whether the run was completed is genuinely unknown -- is
    /// unclaimed and bounded, so the next invocation can continue it and no invocation at all still
    /// closes it.
    /// </summary>
    /// <remarks>
    /// Reached from <c>finally</c>, and never skipped: every captured invocation holds a claim, so
    /// there is no path -- not a finalization that threw, not one that overran its timeout, not the
    /// default single-invocation path -- on which the claim is kept or a run is left open with no
    /// bound armed. Completing an already-completed run is a harmless conflict, whereas leaving an
    /// open one unbounded would hold its payload for the process lifetime.
    /// </remarks>
    private void ReleaseRun()
    {
        if (Volatile.Read(ref _completedRun) != 0)
        {
            _registry.Forget(_openRun);
            return;
        }

        switch (_registry.TryLeaveOpen(_openRun, RunStartedAt))
        {
            case LeaveOpenResult.LeftOpen:
                return;

            case LeaveOpenResult.Disposed:
                // Capture was disposed while this invocation was in flight. The run is abandoned rather
                // than completed -- a completion would call back into a host that is tearing down --
                // and this invocation, still on its own thread, is the last chance to say so.
                ReportFailure(
                    ExperienceCaptureFailureStage.Finalize,
                    "Capture for this agent was disposed while the invocation was in flight; the run is abandoned open and nothing further is recorded for it.",
                    null);
                _registry.Forget(_openRun);
                return;

            case LeaveOpenResult.BoundReached:
                // Reported by the close itself, and only once its completion has landed: a run that
                // turns out to have been completed already is never reported as closed at its bound.
                _registry.CloseNow(_openRun, atBound: true);
                return;

            default:
                // No bound could be armed, which was reported with its reason when arming failed.
                _registry.CloseNow(_openRun, atBound: false);
                return;
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

        // Whether this invocation ends the run. The host's predicate decides, but only for a run that
        // is in a state to be left open at all: an attempt that could not be recorded, a bound the run
        // has reached, or a predicate that threw all complete the run instead. Failing closed is the
        // only choice that cannot retain a captured payload.
        var complete = problems.Count > 0 || ShouldComplete(status, endedAt, request);
        if (!complete)
        {
            return;
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
            else
            {
                Volatile.Write(ref _completedRun, 1);
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
            return;
        }

        // Only a run whose attempt and completion both landed is worth turning into a durable record:
        // finalizing a half-captured run would persist an incomplete history as if it were whole.
        await FinalizeExperienceAsync(
            _service,
            _options,
            RunId,
            failure => ReportFailure(failure.Stage, failure.Reason, failure.Exception),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this invocation ends its run: the host's
    /// <see cref="ExperienceCaptureOptions.ShouldCompleteRun"/> decides, and the adapter's own bounds
    /// override a request to keep the run open.
    /// </summary>
    /// <remarks>
    /// The predicate is asked first so that the default configuration -- which always completes --
    /// never consults a bound and therefore never reports one. A bound is reported only where it
    /// actually changed the answer: a host that asked to keep a run open and had it closed anyway is
    /// exactly the case that must never be silent.
    /// </remarks>
    private bool ShouldComplete(RunExecutionStatus status, DateTimeOffset endedAt, AppendAttemptRequest request)
    {
        Attempt? recorded = null;
        var attemptCount = 0;
        try
        {
            if (!_service.TryGetRun(RunId, out var run) || run.Attempts is not { } attempts)
            {
                // Silence here would be the worst of both: the predicate would be handed
                // AttemptCount 0 and a null Result -- so the attempt bound could never trip and the
                // documented `ctx => ctx.Result is not null` shape would keep the run open until the
                // duration bound -- on the strength of a read that did not work.
                ReportFailure(
                    ExperienceCaptureFailureStage.Finalize,
                    "The run could not be read back to decide whether it is finished, so the attempt count and result the predicate would see are unknown; the run is completed.",
                    null);
                return true;
            }

            attemptCount = attempts.Count;
            foreach (var attempt in attempts)
            {
                if (attempt is not null && attempt.AttemptId == request.AttemptId)
                {
                    recorded = attempt;
                }
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ExperienceCaptureFailureStage.Finalize, $"Reading the run back to decide whether it is finished threw {ex.GetType().FullName}; the run is completed.", ex);
            return true;
        }

        var openFor = endedAt - RunStartedAt;

        try
        {
            // The predicate sees what was stored, not what was submitted: the attempt's sanitized,
            // limit-enforced Result/Error, so deciding "good enough" cannot be the one place a raw
            // payload leaves capture.
            if (_options.ShouldCompleteRun(new ExperienceRunCompletionContext(
                RunId,
                _descriptor.TaskId,
                _descriptor.Scope,
                status,
                attemptCount,
                openFor,
                recorded?.Result,
                recorded?.Error)))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ExperienceCaptureFailureStage.Finalize, $"ShouldCompleteRun threw {ex.GetType().FullName}; the run is completed rather than left open.", ex);
            return true;
        }

        if (attemptCount >= _options.MaxAttemptsPerOpenRun)
        {
            ReportFailure(
                ExperienceCaptureFailureStage.Finalize,
                $"The run reached its {_options.MaxAttemptsPerOpenRun}-attempt open-run bound; it is completed rather than left open for another attempt.",
                null);
            return true;
        }

        if (openFor >= _options.MaxOpenRunDuration)
        {
            ReportFailure(
                ExperienceCaptureFailureStage.Finalize,
                $"The run has been open for {openFor}, at or beyond its {_options.MaxOpenRunDuration} open-run bound; it is completed rather than left open for another attempt.",
                null);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Hands the completed run to Core's finalization service, through the host's own request
    /// resolver. Like everything else on this type, nothing here throws into MAF or the caller: every
    /// problem is reported to <see cref="ExperienceCaptureOptions.OnCaptureFailure"/> and swallowed,
    /// and the captured run is left untouched so the host can retry finalization itself.
    /// </summary>
    internal static async Task FinalizeExperienceAsync(
        IExperienceCaptureService service,
        ExperienceCaptureOptions options,
        Guid runId,
        Action<ExperienceCaptureFailure> report,
        CancellationToken cancellationToken)
    {
        if (options.FinalizationService is not { } finalization || options.ResolveFinalization is not { } resolve)
        {
            return;
        }

        void Fail(ExperienceCaptureFailureStage stage, string reason, Exception? exception) =>
            report(new ExperienceCaptureFailure(stage, runId, reason, exception));

        FinalizeExperienceRequest? request;
        try
        {
            if (!service.TryGetRun(runId, out var run))
            {
                Fail(ExperienceCaptureFailureStage.Finalization, "The completed run could not be read back for finalization; it is not finalized.", null);
                return;
            }

            request = resolve(new ExperienceFinalizationContext(run));
        }
        catch (Exception ex)
        {
            Fail(ExperienceCaptureFailureStage.Finalization, $"ResolveFinalization threw {ex.GetType().FullName}; the run is not finalized.", ex);
            return;
        }

        // A null request is the host declining to finalize this particular run -- not a failure.
        if (request is null)
        {
            return;
        }

        // The resolver is host code and could hand back a request for some other captured run, which
        // would finalize an unrelated run on this invocation's behalf.
        if (request.RunId != runId)
        {
            Fail(
                ExperienceCaptureFailureStage.Finalization,
                "ResolveFinalization returned a request for a different run; the run is not finalized.",
                null);
            return;
        }

        FinalizeExperienceResult result;
        try
        {
            result = await finalization.FinalizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ExperienceCaptureFailureStage.Finalization, $"Finalizing the run threw {ex.GetType().FullName}.", ex);
            return;
        }

        if (result is null)
        {
            Fail(ExperienceCaptureFailureStage.Finalization, "FinalizeAsync returned null.", null);
            return;
        }

        // Only a genuine defect goes to the failure channel. A host whose policy denies storage, or
        // whose authorization refuses a scope, made that decision on purpose and should not get a
        // failure callback per invocation -- OnRunFinalized already carries the whole result.
        if (result.Outcome is FinalizationOutcome.Failed)
        {
            Fail(
                ExperienceCaptureFailureStage.Finalization,
                $"Finalization ended at stage {result.Stage} with outcome {result.Outcome}; no Experience Record is durable for this run.",
                result.Failure?.Exception);
        }

        // Finalization may have overrun the timeout and already been reported as such; telling the
        // host it finished after that would contradict the failure it already saw.
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            options.OnRunFinalized?.Invoke(result);
        }
        catch
        {
            // The host's finalization callback must never affect the agent invocation.
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
