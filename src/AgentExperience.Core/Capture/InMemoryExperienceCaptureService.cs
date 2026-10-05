using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;
using AgentExperience.Core.Sanitization;

namespace AgentExperience.Core.Capture;

/// <summary>
/// The in-memory <see cref="IExperienceCaptureService"/> implementation: a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>-backed store, one entry per run, each guarded by
/// its own lock so concurrent calls on different runs proceed fully in parallel while concurrent
/// calls on the <em>same</em> run are serialized (assigning <c>SequenceNumber</c> correctly and
/// deciding idempotency consistently). Every tool call's arguments/result/error -- and each
/// attempt's own result/error -- is sanitized via the composed <see cref="ISanitizer"/> before
/// anything is stored; a <see cref="SanitizationDecision.Rejected"/> decision anywhere rejects the
/// whole append fail-closed. <see cref="CaptureLimits"/> are enforced with safe-placeholder (string
/// fields) or reject-outright (attempt count) handling, reported on the call's result -- never by
/// changing an <c>AgentExperience.Abstractions</c> record shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sanitization scope, one step wider than the letter of "every tool call's
/// Arguments/Result/Error":</b> an attempt's own <see cref="AppendAttemptRequest.Result"/>/
/// <see cref="AppendAttemptRequest.Error"/> are sanitized too, under the same <see cref="SanitizationKinds.ToolResult"/>
/// <c>Kind</c> as a tool call's -- both are the same kind of thing (sanitized, tool-produced
/// textual output), just at a different granularity, and the epic's own policy-before-persistence
/// requirement ("sanitize task context, tool arguments, tool results, and evidence before anything
/// is persisted") does not carve out attempt-level text as an exception. <see cref="ExperienceRun"/>
/// fields set only by <see cref="StartRun"/> (<c>TaskDescription</c>, <c>Scope</c>,
/// <c>Environment</c>, <c>Provenance</c>) are deliberately left out of scope here: Story 1.2's own
/// frozen Boundaries name only "every tool call's Arguments/Result/Error", and <c>StartRun</c>'s
/// task description is exactly the "task context" the epic assigns no owner for in this story.
/// </para>
/// <para>
/// <b>A finalized run accepts no further attempts:</b> <see cref="AppendAttemptAsync"/> checks
/// <c>Completion</c> first, before idempotency or capacity -- once <see cref="CompleteRunAsync"/>
/// has finalized a run, every subsequent <see cref="AppendAttemptAsync"/> call on it, whatever its
/// <c>AttemptId</c> or content, returns <see cref="AppendAttemptOutcome.Conflict"/> rather than
/// silently growing a run that has already been reported done.
/// </para>
/// <para>
/// <b><see cref="CaptureLimits.MaxToolCallsPerAttempt"/> truncates a list; <see cref="CaptureLimits.MaxAttemptsPerRun"/>
/// rejects outright:</b> excess tool calls within one <c>AppendAttempt</c> call, beyond
/// <c>MaxToolCallsPerAttempt</c>, are dropped (earliest-first order preserved) and reported as a
/// truncated <c>"ToolCalls"</c> field. A run already holding <c>MaxAttemptsPerRun</c> attempts
/// rejects a further, genuinely new <c>AttemptId</c> outright (<see cref="AppendAttemptOutcome.CapacityExceeded"/>)
/// -- it is never tracked, so the run's own idempotency bookkeeping (<c>SeenAttempts</c>) never
/// exceeds <c>MaxAttemptsPerRun</c> entries either, closing the same "no unbounded payload is ever
/// retained" guarantee at the bookkeeping level, not just the stored-field level.
/// </para>
/// <para>
/// <b>Idempotency, keyed purely by the caller-supplied event ID:</b> <see cref="AppendAttemptAsync"/>
/// compares a resubmitted <c>AttemptId</c>'s full raw request (deep value equality, not reference
/// equality, over its tool calls' nested arguments) against what was first recorded under that ID --
/// checked cheaply, under the run's lock, <em>before</em> any sanitization work, so a duplicate
/// resubmission never re-pays that cost. <see cref="CompleteRunAsync"/> tracks at most one
/// completion per run: the same <c>completionEventId</c> resubmitted with identical content is a
/// no-op; any other completion attempt on an already-finalized run -- whether a different ID, or the
/// same ID with different content -- is a <see cref="CompleteRunOutcome.Conflict"/>, so a run
/// finalizes exactly once.
/// </para>
/// <para>
/// <b>How many runs stay open, and for how long:</b> at most <see cref="CaptureLimits.MaxOpenRuns"/> runs
/// are open at once. Past it, a <see cref="StartRun"/> call that would open a new run is refused with
/// <see cref="StartRunOutcome.CapacityExceeded"/> and stores nothing; continuing an open run is never
/// refused, and a run leaves the open count the moment it completes. When
/// <see cref="CaptureLimits.MaxOpenRunAge"/> is set, an open run older than it (on the monotonic timestamp,
/// from when this service opened it) is completed by this service as
/// <see cref="RunExecutionStatus.Cancelled"/>, ended at the provider's <see cref="TimeProvider.GetUtcNow"/>,
/// under a completion event ID derived from its run ID -- so a later completion from the host under its own
/// event ID is a <see cref="CompleteRunOutcome.Conflict"/>, exactly as for any finalized run. It is then a
/// completed run like any other. An open run is never dropped without being completed first. Expiry is
/// applied lazily, before every call answers, from a queue kept in start order and checked at its head only.
/// </para>
/// <para>
/// <b>How long a completed run is kept:</b> an open run is never dropped by these bounds. A completed run is kept until
/// it is older than <see cref="CaptureLimits.CompletedRunRetention"/>, or until more than
/// <see cref="CaptureLimits.MaxRetainedCompletedRuns"/> completed runs are held, the earliest completed
/// dropped first. Finalization does not drop a run, so while a run is held a finalization retry still
/// answers <c>AlreadyFinalized</c> and reusing its ID is still a conflict. Age is measured on the injected
/// <see cref="TimeProvider"/>'s monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/>) from when
/// this service recorded the completion, so a wall-clock step forward or back changes nothing. Both bounds
/// are applied lazily, before every call of this service answers, from a queue kept in completion order:
/// each completed run is queued once and dropped at most once, so the cost per call is amortized constant,
/// and no background timer runs. A dropped run answers every call exactly as a run that never existed.
/// </para>
/// </remarks>
public sealed class InMemoryExperienceCaptureService : IExperienceCaptureService
{
    private const string ToolArgumentsKind = SanitizationKinds.ToolArguments;
    private const string ToolResultKind = SanitizationKinds.ToolResult;
    private const string TextValueFieldName = SanitizationKinds.ValueField;
    private const string TruncationPlaceholder = "[truncated: value exceeded the configured limit]";

    private readonly ISanitizer _sanitizer;
    private readonly CaptureLimits _limits;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();

    // Completed runs still held, earliest completion first. Guarded by _retentionGate, which is only ever
    // taken for constant work per entry and only ever *inside* a run's own Gate, never around one.
    private readonly Queue<CompletedRun> _completed = new();
    private readonly object _retentionGate = new();

    // The monotonic timestamp of the head of _completed (long.MaxValue when nothing is held): read
    // without the lock so that a call with nothing yet to expire never takes it.
    private long _oldestCompletedAtTimestamp = long.MaxValue;

    // How many runs are open (started, not completed). Raised by StartRun before it adds a run (and given
    // back if the add does not happen), lowered exactly once per run by the completion transition.
    private int _openCount;

    // Open runs in start order, kept only when CaptureLimits.MaxOpenRunAge is set. An entry whose run has
    // since completed stays until it reaches the head or the queue is compacted. Guarded by _openGate, which
    // is taken outside a run's own Gate (and so outside _retentionGate), never inside one.
    private Queue<OpenedRun> _opened = new();
    private readonly object _openGate = new();

    // The monotonic timestamp of the head of _opened (long.MaxValue when it is empty), for the lock-free check.
    private long _oldestOpenedAtTimestamp = long.MaxValue;

    /// <summary>Creates an <see cref="InMemoryExperienceCaptureService"/> that measures retention on <see cref="TimeProvider.System"/>.</summary>
    /// <param name="sanitizer">The port every raw tool-call/attempt field is sanitized through before storage.</param>
    /// <param name="limits">The positive capture limits this instance enforces.</param>
    public InMemoryExperienceCaptureService(ISanitizer sanitizer, CaptureLimits limits)
        : this(sanitizer, limits, TimeProvider.System)
    {
    }

    /// <summary>Creates an <see cref="InMemoryExperienceCaptureService"/>.</summary>
    /// <param name="sanitizer">The port every raw tool-call/attempt field is sanitized through before storage.</param>
    /// <param name="limits">The positive capture limits this instance enforces.</param>
    /// <param name="timeProvider">The clock a completed run's age is measured on, against <see cref="CaptureLimits.CompletedRunRetention"/>.</param>
    public InMemoryExperienceCaptureService(ISanitizer sanitizer, CaptureLimits limits, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _sanitizer = sanitizer;
        _limits = limits;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public StartRunResult StartRun(
        Guid runId,
        string taskId,
        string? taskDescription,
        Scope scope,
        EnvironmentFingerprint environment,
        Provenance provenance,
        DateTimeOffset startedAt)
    {
        // The span wraps the whole call, argument validation included, so a malformed call is visible
        // as a failure rather than as a missing operation. Nothing below it reads the span back, and
        // the run's task description is never written to it.
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.CaptureStartRun, CancellationToken.None);
        ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.RunIdAttribute, runId.ToString("D"));

        // Only the call itself is guarded. Tagging and the metric writes happen outside it, so a
        // throw from telemetry can never be mistaken for -- or turned into -- a failure of the run.
        StartRunResult result;
        try
        {
            result = StartRunCore(runId, taskId, taskDescription, scope, environment, provenance, startedAt);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.CaptureStartRun, ex);
            throw;
        }

        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.CaptureStartRun, result.Outcome.ToString());
        return result;
    }

    /// <summary>The body of <see cref="StartRun"/>, unchanged by instrumentation: it neither reads nor writes a span.</summary>
    private StartRunResult StartRunCore(
        Guid runId,
        string taskId,
        string? taskDescription,
        Scope scope,
        EnvironmentFingerprint environment,
        Provenance provenance,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(provenance);

        // Exposure is recorded only through RecordExposure, as the library delivers records into the run; a
        // provenance that arrives already claiming exposures would be the caller's statement in the one
        // field confidence verification relies on to be the library's.
        if (provenance.ExposedTo is not { Count: 0 })
        {
            throw new ArgumentException(
                "A run starts exposed to nothing: Provenance.ExposedTo must be empty, and exposures are recorded through RecordExposure.",
                nameof(provenance));
        }

        // Normalized rather than kept: the caller's list object, empty now, could be filled afterwards.
        provenance = provenance with { ExposedTo = [] };

        var run = new ExperienceRun(
            RunId: runId,
            TaskId: taskId,
            TaskDescription: taskDescription,
            Scope: scope,
            Environment: environment,
            Provenance: provenance,
            Attempts: Array.Empty<Attempt>(),
            ExecutionStatus: null,
            Outcome: null,
            StartedAt: startedAt,
            EndedAt: null);

        EvictExpired();

        // An existing run is decided first, so a continuation is never refused for capacity. A run dropped
        // between the failed add and the next read is gone, so the ID names no run and a new one is opened
        // under it -- exactly as if the drop had happened before this call.
        while (true)
        {
            if (_runs.TryGetValue(runId, out var existing))
            {
                return Continue(existing, runId, taskId, scope);
            }

            if (!TryReserveOpenSlot())
            {
                // A concurrent call may have opened this very ID meanwhile: that is a continuation, not a refusal.
                if (_runs.TryGetValue(runId, out existing))
                {
                    return Continue(existing, runId, taskId, scope);
                }

                return new StartRunResult(
                    StartRunOutcome.CapacityExceeded,
                    null,
                    $"{_limits.MaxOpenRuns} runs are already open; no new run can be opened until one completes.");
            }

            var state = new RunState(run);
            if (_runs.TryAdd(runId, state))
            {
                TrackOpened(runId, state);
                return new StartRunResult(StartRunOutcome.Started, run, null);
            }

            Interlocked.Decrement(ref _openCount);
        }
    }

    /// <summary>Takes one open-run slot, or returns <see langword="false"/> when <see cref="CaptureLimits.MaxOpenRuns"/> are already taken.</summary>
    private bool TryReserveOpenSlot()
    {
        // Only ever raised while below the bound, so the count never reads past it and a concurrent start is
        // never refused while a slot is free.
        var current = Volatile.Read(ref _openCount);
        while (current < _limits.MaxOpenRuns)
        {
            var observed = Interlocked.CompareExchange(ref _openCount, current + 1, current);
            if (observed == current)
            {
                return true;
            }

            current = observed;
        }

        return false;
    }

    /// <summary>
    /// Queues a just-opened run for the age bound, when <see cref="CaptureLimits.MaxOpenRunAge"/> is set.
    /// Entries for runs that have completed since are compacted away once they outnumber the open runs, so
    /// the queue never holds much more than twice <see cref="CaptureLimits.MaxOpenRuns"/> entries and each
    /// entry is copied a constant number of times on average.
    /// </summary>
    private void TrackOpened(Guid runId, RunState state)
    {
        if (_limits.MaxOpenRunAge is null)
        {
            return;
        }

        lock (_openGate)
        {
            // Read under the lock, so the queue is in start order and its head is the run that expires first.
            _opened.Enqueue(new OpenedRun(runId, state, _timeProvider.GetTimestamp()));

            if (_opened.Count > (2 * Math.Max(Volatile.Read(ref _openCount), 0)) + 16)
            {
                _opened = new Queue<OpenedRun>(_opened.Where(entry => !entry.State.IsCompleted));
            }

            PublishOldestOpened();
        }
    }

    /// <summary>
    /// Decides what an existing <c>RunId</c> means: a continuation of the same, still-open run, or a
    /// collision. Nothing is written here -- a continuation returns the run exactly as it stands, so
    /// a second <c>StartRun</c> can never overwrite a task description, a scope, an environment, a
    /// provenance or a start time the first one recorded.
    /// </summary>
    /// <remarks>
    /// The finalized check comes first and is the same invariant <see cref="TryShortCircuit"/>
    /// enforces one level down: a run completes exactly once and is never reopened. Refusing here as
    /// well means a caller that meant to continue a run learns so before it captures anything,
    /// instead of discovering it when its attempt is rejected.
    /// </remarks>
    private static StartRunResult Continue(RunState state, Guid runId, string taskId, Scope scope)
    {
        // A run dropped after it was read here was completed (only completed runs are ever dropped), so
        // the finalized check below refuses it -- the answer it would have had a moment earlier.
        lock (state.Gate)
        {
            if (state.Completion is not null)
            {
                return new StartRunResult(StartRunOutcome.Conflict, null, $"Run '{runId}' is already finalized and cannot be continued.");
            }

            var existing = state.Run;
            if (!string.Equals(existing.TaskId, taskId, StringComparison.Ordinal) || existing.Scope != scope)
            {
                return new StartRunResult(StartRunOutcome.Conflict, null, $"A different run already exists under RunId '{runId}'; its TaskId or Scope does not match this call's.");
            }

            return new StartRunResult(StartRunOutcome.Continued, existing, null);
        }
    }

    /// <inheritdoc />
    public bool TryGetRun(Guid runId, [NotNullWhen(true)] out ExperienceRun? run)
    {
        EvictExpired();

        if (_runs.TryGetValue(runId, out var state))
        {
            lock (state.Gate)
            {
                run = state.Run;
                return true;
            }
        }

        run = null;
        return false;
    }

    /// <inheritdoc />
    public RecordExposureResult RecordExposure(Guid runId, IReadOnlyList<RunExposure> exposures)
    {
        ArgumentNullException.ThrowIfNull(exposures);

        // One snapshot, validated before anything is looked at, so a list that changes under us cannot put
        // an unchecked exposure on the run.
        var submitted = exposures.ToArray();
        foreach (var exposure in submitted)
        {
            ArgumentNullException.ThrowIfNull(exposure, nameof(exposures));
            if (exposure.ExperienceId == Guid.Empty)
            {
                throw new ArgumentException("An exposure must name a record, never an empty GUID.", nameof(exposures));
            }

            if (exposure.Revision < 0)
            {
                throw new ArgumentException("An exposure's revision must not be negative.", nameof(exposures));
            }
        }

        EvictExpired();

        if (!_runs.TryGetValue(runId, out var state))
        {
            return new RecordExposureResult(RecordExposureOutcome.RunNotFound, $"No run with RunId '{runId}' exists.");
        }

        lock (state.Gate)
        {
            if (state.Completion is not null)
            {
                return new RecordExposureResult(
                    RecordExposureOutcome.Conflict,
                    "The run is already completed; its provenance is closed and no further exposure can be recorded.");
            }

            var merged = new SortedDictionary<Guid, long>();
            foreach (var held in state.Run.Provenance.ExposedTo)
            {
                merged[held.ExperienceId] = held.Revision;
            }

            var changed = false;
            foreach (var exposure in submitted)
            {
                if (!merged.TryGetValue(exposure.ExperienceId, out var revision) || exposure.Revision < revision)
                {
                    merged[exposure.ExperienceId] = exposure.Revision;
                    changed = true;
                }
            }

            if (!changed)
            {
                return new RecordExposureResult(RecordExposureOutcome.DuplicateNoOp, Reason: null);
            }

            if (merged.Count > RunExposure.MaxPerRun)
            {
                return new RecordExposureResult(
                    RecordExposureOutcome.CapacityExceeded,
                    $"The run would be exposed to more than {RunExposure.MaxPerRun} records; nothing from this call was recorded.");
            }

            state.Run = state.Run with
            {
                Provenance = state.Run.Provenance with
                {
                    ExposedTo = [.. merged.Select(entry => new RunExposure(entry.Key, entry.Value))],
                },
            };

            return new RecordExposureResult(RecordExposureOutcome.Recorded, Reason: null);
        }
    }

    /// <inheritdoc />
    public async Task<AppendAttemptResult> AppendAttemptAsync(
        Guid runId,
        AppendAttemptRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.CaptureAppendAttempt, cancellationToken);
        ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.RunIdAttribute, runId.ToString("D"));

        AppendAttemptResult result;
        try
        {
            // Both identifiers come off the request, so both are on the span before the call: a span
            // that records a failure is worth far less if it cannot say which attempt failed. The
            // argument check is restated ahead of the tag so that a null request is still the
            // ArgumentNullException the body would have thrown, never a NullReferenceException from
            // instrumentation -- and is still recorded as this operation's failure.
            ArgumentNullException.ThrowIfNull(request);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.AttemptIdAttribute, request.AttemptId.ToString("D"));

            result = await AppendAttemptCoreAsync(runId, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.CaptureAppendAttempt, ex);
            throw;
        }

        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.CaptureAppendAttempt, result.Outcome.ToString());
        return result;
    }

    /// <summary>The body of <see cref="AppendAttemptAsync"/>, unchanged by instrumentation: it neither reads nor writes a span.</summary>
    private async Task<AppendAttemptResult> AppendAttemptCoreAsync(
        Guid runId,
        AppendAttemptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ToolCalls);
        cancellationToken.ThrowIfCancellationRequested();

        EvictExpired();

        if (!_runs.TryGetValue(runId, out var state))
        {
            return new AppendAttemptResult(AppendAttemptOutcome.RunNotFound, [], $"No run with RunId '{runId}' exists.");
        }

        // Cheap pre-check under the run's lock: a run already finalized, a known duplicate/
        // conflicting AttemptId, or a run already at its attempt-count capacity are all decided
        // without paying for any (possibly costly, external) sanitization work.
        if (TryShortCircuit(state, request, out var earlyResult))
        {
            return earlyResult;
        }

        // Defensive: a null element, or a tool call with a null Arguments dictionary, in the raw
        // request must produce a graceful outcome, never an NRE/ArgumentNullException. Checked
        // before any sanitization is attempted, and before this AttemptId is ever tracked -- so a
        // later, corrected resubmission under the same AttemptId can still succeed.
        foreach (var rawToolCall in request.ToolCalls)
        {
            if (rawToolCall is null)
            {
                return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], "A tool call in the request was null.");
            }

            if (rawToolCall.Arguments is null)
            {
                return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], $"Tool call '{rawToolCall.ToolCallId}' has a null Arguments dictionary.");
            }
        }

        // Sanitization and CaptureLimits enforcement happen outside the run's lock -- they're pure,
        // non-mutating work over the raw request -- so one run's (possibly async) sanitization work
        // never blocks a concurrent call on a *different* run. The lock is only taken to decide
        // idempotency/completion/capacity and commit the outcome, which must be serialized per-run.
        var truncatedFields = new List<TruncatedField>();

        var attemptResult = await SanitizeTextAsync(request.Result, "Attempt.Result", ToolResultKind, _limits.MaxResultLength, truncatedFields, cancellationToken).ConfigureAwait(false);
        if (attemptResult.Rejected)
        {
            return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], attemptResult.RejectionReason);
        }

        var attemptError = await SanitizeTextAsync(request.Error, "Attempt.Error", ToolResultKind, _limits.MaxErrorLength, truncatedFields, cancellationToken).ConfigureAwait(false);
        if (attemptError.Rejected)
        {
            return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], attemptError.RejectionReason);
        }

        var rawToolCalls = request.ToolCalls;
        var truncatedToolCallCount = 0;
        if (rawToolCalls.Count > _limits.MaxToolCallsPerAttempt)
        {
            truncatedToolCallCount = rawToolCalls.Count - _limits.MaxToolCallsPerAttempt;
            rawToolCalls = rawToolCalls.Take(_limits.MaxToolCallsPerAttempt).ToList();
        }

        var toolCalls = new List<ToolCallRecord>(rawToolCalls.Count);
        for (var sequenceNumber = 0; sequenceNumber < rawToolCalls.Count; sequenceNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawToolCall = rawToolCalls[sequenceNumber];

            var argumentsResult = await _sanitizer.SanitizeAsync(new RawPayload(ToolArgumentsKind, rawToolCall.Arguments), cancellationToken).ConfigureAwait(false);
            if (argumentsResult.Decision == SanitizationDecision.Rejected)
            {
                return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], argumentsResult.Reason);
            }

            var toolResult = await SanitizeTextAsync(rawToolCall.Result, $"ToolCalls[{sequenceNumber}].Result", ToolResultKind, _limits.MaxResultLength, truncatedFields, cancellationToken).ConfigureAwait(false);
            if (toolResult.Rejected)
            {
                return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], toolResult.RejectionReason);
            }

            var toolError = await SanitizeTextAsync(rawToolCall.Error, $"ToolCalls[{sequenceNumber}].Error", ToolResultKind, _limits.MaxErrorLength, truncatedFields, cancellationToken).ConfigureAwait(false);
            if (toolError.Rejected)
            {
                return new AppendAttemptResult(AppendAttemptOutcome.SanitizationRejected, [], toolError.RejectionReason);
            }

            toolCalls.Add(new ToolCallRecord(
                ToolCallId: rawToolCall.ToolCallId,
                SequenceNumber: sequenceNumber,
                ToolName: rawToolCall.ToolName,
                Arguments: argumentsResult.Fields,
                StartedAt: rawToolCall.StartedAt,
                Duration: rawToolCall.Duration,
                Result: toolResult.Value,
                Error: toolError.Value));
        }

        if (truncatedToolCallCount > 0)
        {
            truncatedFields.Add(new TruncatedField(
                "ToolCalls",
                $"{truncatedToolCallCount} tool call(s) beyond the configured limit of {_limits.MaxToolCallsPerAttempt} were dropped from this attempt."));
        }

        lock (state.Gate)
        {
            // Re-decide under the lock: another thread may have raced this exact AttemptId to
            // completion (recorded it, conflicted it, finalized the run, or exhausted capacity)
            // while this request's sanitization was in flight. `lock` is re-entrant on the same
            // thread, so calling the same helper here (which itself takes `state.Gate`) is safe.
            if (TryShortCircuit(state, request, out var racedResult))
            {
                return racedResult;
            }

            state.SeenAttempts[request.AttemptId] = request;

            var attempt = new Attempt(
                AttemptId: request.AttemptId,
                SequenceNumber: state.Run.Attempts.Count,
                StartedAt: request.StartedAt,
                Duration: request.Duration,
                ToolCalls: toolCalls.AsReadOnly(),
                Result: attemptResult.Value,
                Error: attemptError.Value);

            var updatedAttempts = new List<Attempt>(state.Run.Attempts.Count + 1);
            updatedAttempts.AddRange(state.Run.Attempts);
            updatedAttempts.Add(attempt);

            state.Run = state.Run with { Attempts = updatedAttempts.AsReadOnly() };

            return new AppendAttemptResult(AppendAttemptOutcome.Recorded, truncatedFields, Reason: null);
        }
    }

    /// <inheritdoc />
    public async Task<CompleteRunResult> CompleteRunAsync(
        Guid runId,
        Guid completionEventId,
        RunExecutionStatus executionStatus,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.CaptureCompleteRun, cancellationToken);
        ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.RunIdAttribute, runId.ToString("D"));
        ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.EventIdAttribute, completionEventId.ToString("D"));

        CompleteRunResult result;
        try
        {
            result = await CompleteRunCoreAsync(runId, completionEventId, executionStatus, endedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.CaptureCompleteRun, ex);
            throw;
        }

        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.CaptureCompleteRun, result.Outcome.ToString());
        return result;
    }

    /// <summary>The body of <see cref="CompleteRunAsync"/>, unchanged by instrumentation: it neither reads nor writes a span.</summary>
#pragma warning disable CS1998 // Deliberately async with no internal await: this captures a synchronous ThrowIfCancellationRequested() throw into the returned Task (matching AppendAttemptAsync's contract) instead of letting it escape synchronously at the call site.
    private async Task<CompleteRunResult> CompleteRunCoreAsync(
        Guid runId,
        Guid completionEventId,
        RunExecutionStatus executionStatus,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
#pragma warning restore CS1998
    {
        cancellationToken.ThrowIfCancellationRequested();

        EvictExpired();

        if (!_runs.TryGetValue(runId, out var state))
        {
            return new CompleteRunResult(CompleteRunOutcome.RunNotFound, $"No run with RunId '{runId}' exists.");
        }

        lock (state.Gate)
        {
            if (state.Completion is { } existing)
            {
                if (existing.EventId != completionEventId)
                {
                    return new CompleteRunResult(CompleteRunOutcome.Conflict, "The run was already finalized by a different completion event.");
                }

                return existing.Status == executionStatus && existing.EndedAt == endedAt
                    ? new CompleteRunResult(CompleteRunOutcome.DuplicateNoOp, Reason: null)
                    : new CompleteRunResult(CompleteRunOutcome.Conflict, "This completion event was already recorded with different content.");
            }

            MarkCompleted(runId, state, completionEventId, executionStatus, endedAt);

            return new CompleteRunResult(CompleteRunOutcome.Recorded, Reason: null);
        }
    }

    /// <summary>
    /// The one transition from open to completed, whether the host completes the run or the open-run age
    /// bound does: records the completion, frees the run's open slot, and queues it as a completed run.
    /// Called under <paramref name="state"/>'s lock, with the run not yet completed.
    /// </summary>
    private void MarkCompleted(Guid runId, RunState state, Guid completionEventId, RunExecutionStatus executionStatus, DateTimeOffset endedAt)
    {
        state.Completion = (completionEventId, executionStatus, endedAt);
        state.Run = state.Run with { ExecutionStatus = executionStatus, EndedAt = endedAt };
        state.IsCompleted = true;
        Interlocked.Decrement(ref _openCount);

        Retain(runId, state);
    }

    /// <summary>
    /// The completion event ID under which the open-run age bound completes <paramref name="runId"/>: a
    /// name-based (SHA-256, RFC 9562 version 8) GUID over a fixed label and the run ID, so it is the same
    /// every time for the same run and is never the run ID itself.
    /// </summary>
    private static Guid ExpiredOpenRunCompletionEventId(Guid runId)
    {
        Span<byte> input = stackalloc byte[ExpiredOpenRunLabel.Length + 16];
        ExpiredOpenRunLabel.CopyTo(input);
        runId.TryWriteBytes(input[ExpiredOpenRunLabel.Length..], bigEndian: true, out _);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }

    private static ReadOnlySpan<byte> ExpiredOpenRunLabel => "AgentExperience.Capture.ExpiredOpenRun:"u8;

    /// <summary>
    /// Queues a just-completed run at the tail of the completion order and, if that puts more completed
    /// runs on hold than <see cref="CaptureLimits.MaxRetainedCompletedRuns"/>, drops the earliest. Called
    /// under <paramref name="state"/>'s lock.
    /// </summary>
    private void Retain(Guid runId, RunState state)
    {
        lock (_retentionGate)
        {
            // A monotonic timestamp read under the lock, so the queue is in completion order and its head
            // is always the run that expires first, whatever the wall clock does.
            var completedAt = _timeProvider.GetTimestamp();
            _completed.Enqueue(new CompletedRun(runId, state, completedAt));

            while (_completed.Count > _limits.MaxRetainedCompletedRuns)
            {
                DropOldest();
            }

            DropExpired(completedAt);
            PublishOldest();
        }
    }

    /// <summary>
    /// First completes every open run past <see cref="CaptureLimits.MaxOpenRunAge"/> (see <see cref="ExpireOpenRuns"/>),
    /// then drops every completed run older than <see cref="CaptureLimits.CompletedRunRetention"/>. Nothing is
    /// locked when the earliest held completion is not yet due; otherwise the call waits for the lock, so
    /// it never answers for a run that is already due to be dropped.
    /// </summary>
    private void EvictExpired()
    {
        ExpireOpenRuns();

        var oldest = Volatile.Read(ref _oldestCompletedAtTimestamp);
        if (oldest == long.MaxValue)
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        if (!IsExpired(oldest, now))
        {
            return;
        }

        lock (_retentionGate)
        {
            DropExpired(now);
            PublishOldest();
        }
    }

    /// <summary>Drops completed runs from the head of the queue while they are older than the retention. Called under <see cref="_retentionGate"/>.</summary>
    private void DropExpired(long now)
    {
        while (_completed.TryPeek(out var head) && IsExpired(head.CompletedAt, now))
        {
            DropOldest();
        }
    }

    private bool IsExpired(long completedAtTimestamp, long now) =>
        _timeProvider.GetElapsedTime(completedAtTimestamp, now) >= _limits.CompletedRunRetention;

    /// <summary>Drops the completed run at the head of the queue. Called under <see cref="_retentionGate"/> with the queue non-empty.</summary>
    private void DropOldest()
    {
        var head = _completed.Dequeue();

        // Only this exact run: never a different run that holds the ID.
        _runs.TryRemove(new KeyValuePair<Guid, RunState>(head.RunId, head.State));
    }

    /// <summary>Publishes the head's completion time for the lock-free check in <see cref="EvictExpired"/>. Called under <see cref="_retentionGate"/>.</summary>
    private void PublishOldest() =>
        Volatile.Write(ref _oldestCompletedAtTimestamp, _completed.TryPeek(out var head) ? head.CompletedAt : long.MaxValue);

    /// <summary>
    /// Completes, as <see cref="RunExecutionStatus.Cancelled"/>, every open run older than
    /// <see cref="CaptureLimits.MaxOpenRunAge"/>, so it is then held and dropped like any completed run.
    /// Nothing is locked when the earliest opened run is not yet due; otherwise the call waits for the lock,
    /// so it never answers for a run that is already due to be completed.
    /// </summary>
    private void ExpireOpenRuns()
    {
        if (_limits.MaxOpenRunAge is not { } maxAge)
        {
            return;
        }

        var oldest = Volatile.Read(ref _oldestOpenedAtTimestamp);
        if (oldest == long.MaxValue)
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        if (_timeProvider.GetElapsedTime(oldest, now) < maxAge)
        {
            return;
        }

        lock (_openGate)
        {
            while (_opened.TryPeek(out var head))
            {
                // A run completed since it was queued has already left the open count: just let go of it.
                if (!head.State.IsCompleted)
                {
                    if (_timeProvider.GetElapsedTime(head.OpenedAt, now) < maxAge)
                    {
                        break;
                    }

                    lock (head.State.Gate)
                    {
                        // Re-checked under the run's lock: the host may have completed it a moment ago.
                        if (head.State.Completion is null)
                        {
                            MarkCompleted(
                                head.RunId,
                                head.State,
                                ExpiredOpenRunCompletionEventId(head.RunId),
                                RunExecutionStatus.Cancelled,
                                Later(_timeProvider.GetUtcNow(), head.State.Run.StartedAt));
                        }
                    }
                }

                _opened.Dequeue();
            }

            PublishOldestOpened();
        }
    }

    /// <summary>An expired run never ends before the start time its host supplied, whatever the clock says now.</summary>
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    /// <summary>Publishes the head's start time for the lock-free check in <see cref="ExpireOpenRuns"/>. Called under <see cref="_openGate"/>.</summary>
    private void PublishOldestOpened() =>
        Volatile.Write(ref _oldestOpenedAtTimestamp, _opened.TryPeek(out var head) ? head.OpenedAt : long.MaxValue);

    /// <summary>
    /// Decides, under <paramref name="state"/>'s lock, whether <paramref name="request"/> can be
    /// short-circuited without needing its (potentially not-yet-sanitized) content: the run is
    /// already finalized (<see cref="AppendAttemptOutcome.Conflict"/>); this <c>AttemptId</c> was
    /// already seen, identical (<see cref="AppendAttemptOutcome.DuplicateNoOp"/>) or conflicting
    /// (<see cref="AppendAttemptOutcome.Conflict"/>); or the run is already at its attempt-count
    /// capacity for a genuinely new <c>AttemptId</c> (<see cref="AppendAttemptOutcome.CapacityExceeded"/>).
    /// Callable both before sanitization (to skip it entirely for an already-decided case) and,
    /// re-checked, at commit time (to catch a race) -- <c>lock</c> is re-entrant on the same thread.
    /// </summary>
    /// <returns><see langword="true"/> with the decided <paramref name="result"/> when no further work is needed; <see langword="false"/> when this is a genuinely new attempt that should proceed.</returns>
    private bool TryShortCircuit(RunState state, AppendAttemptRequest request, out AppendAttemptResult result)
    {
        lock (state.Gate)
        {
            if (state.Completion is not null)
            {
                result = new AppendAttemptResult(AppendAttemptOutcome.Conflict, [], "The run is already finalized; no further attempts can be appended.");
                return true;
            }

            if (state.SeenAttempts.TryGetValue(request.AttemptId, out var previous))
            {
                result = AreAppendRequestsEquivalent(previous, request)
                    ? new AppendAttemptResult(AppendAttemptOutcome.DuplicateNoOp, [], Reason: null)
                    : new AppendAttemptResult(AppendAttemptOutcome.Conflict, [], $"AttemptId '{request.AttemptId}' was already recorded with different content.");
                return true;
            }

            if (state.Run.Attempts.Count >= _limits.MaxAttemptsPerRun)
            {
                result = new AppendAttemptResult(AppendAttemptOutcome.CapacityExceeded, [], $"Run attempt capacity ({_limits.MaxAttemptsPerRun}) has already been reached; this attempt was not recorded.");
                return true;
            }

            result = null!;
            return false;
        }
    }

    /// <summary>
    /// Sanitizes an optional raw text field (an attempt's or tool call's <c>Result</c>/<c>Error</c>)
    /// by wrapping it as the single <see cref="TextValueFieldName"/> field of a <paramref name="kind"/>-
    /// <c>Kind</c> <see cref="RawPayload"/>, then enforces <paramref name="maxLength"/> on the
    /// sanitized (not raw) value, truncating to a <paramref name="maxLength"/>-clamped
    /// <see cref="TruncationPlaceholder"/> and recording a <see cref="TruncatedField"/> when it is
    /// exceeded -- the placeholder itself must never exceed the very limit it stands in for.
    /// </summary>
    private async Task<SanitizedText> SanitizeTextAsync(
        string? rawText,
        string diagnosticFieldPath,
        string kind,
        int maxLength,
        List<TruncatedField> truncatedFields,
        CancellationToken cancellationToken)
    {
        if (rawText is null)
        {
            return new SanitizedText(null, Rejected: false, RejectionReason: null);
        }

        var payload = new RawPayload(kind, new Dictionary<string, object?> { [TextValueFieldName] = rawText });
        var sanitized = await _sanitizer.SanitizeAsync(payload, cancellationToken).ConfigureAwait(false);

        if (sanitized.Decision == SanitizationDecision.Rejected)
        {
            return new SanitizedText(null, Rejected: true, RejectionReason: sanitized.Reason);
        }

        // Defensive: a non-conforming custom ISanitizer could return Allowed with a null Fields
        // dictionary despite the port's contract. The sanitizer may also legitimately omit this
        // field (not allowlisted for this Kind) or redact it to a non-string placeholder -- in any
        // of these cases, there is nothing left to store or truncate.
        if (sanitized.Fields is null || !sanitized.Fields.TryGetValue(TextValueFieldName, out var sanitizedValue) || sanitizedValue is not string sanitizedText)
        {
            return new SanitizedText(null, Rejected: false, RejectionReason: null);
        }

        if (sanitizedText.Length > maxLength)
        {
            truncatedFields.Add(new TruncatedField(
                diagnosticFieldPath,
                $"Sanitized value exceeded the configured limit of {maxLength} characters and was replaced with a placeholder."));

            var placeholder = TruncationPlaceholder.Length > maxLength ? TruncationPlaceholder[..maxLength] : TruncationPlaceholder;
            return new SanitizedText(placeholder, Rejected: false, RejectionReason: null);
        }

        return new SanitizedText(sanitizedText, Rejected: false, RejectionReason: null);
    }

    /// <summary>
    /// Deep value equality between two <see cref="AppendAttemptRequest"/>s submitted under the same
    /// <c>AttemptId</c>, to distinguish an identical resubmission (no-op) from a conflicting one.
    /// </summary>
    private static bool AreAppendRequestsEquivalent(AppendAttemptRequest a, AppendAttemptRequest b)
    {
        if (a.AttemptId != b.AttemptId || a.StartedAt != b.StartedAt || a.Duration != b.Duration ||
            a.Result != b.Result || a.Error != b.Error || a.ToolCalls.Count != b.ToolCalls.Count)
        {
            return false;
        }

        for (var i = 0; i < a.ToolCalls.Count; i++)
        {
            var left = a.ToolCalls[i];
            var right = b.ToolCalls[i];

            // A previously-tracked request (`a`) is always fully validated (never contains a null
            // element), but an incoming resubmission (`b`) under a known AttemptId could still be
            // malformed -- guard defensively rather than dereferencing a possibly-null RawToolCall.
            if (left is null || right is null)
            {
                if (!ReferenceEquals(left, right))
                {
                    return false;
                }

                continue;
            }

            if (!AreToolCallsEquivalent(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreToolCallsEquivalent(RawToolCall a, RawToolCall b) =>
        a.ToolCallId == b.ToolCallId
        && a.ToolName == b.ToolName
        && a.StartedAt == b.StartedAt
        && a.Duration == b.Duration
        && a.Result == b.Result
        && a.Error == b.Error
        && AreValuesEquivalent(a.Arguments, b.Arguments);

    /// <summary>
    /// Structural (not reference) equality over the value shapes reachable through a
    /// <see cref="RawToolCall.Arguments"/> dictionary -- <see langword="null"/>, <see cref="string"/>,
    /// <see cref="byte"/>[], <see cref="JsonElement"/>, a nested
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> (order-independent), a nested list
    /// (order-sensitive), and value-type scalars via their own <see cref="object.Equals(object)"/>.
    /// This intentionally mirrors the shape family <c>DefaultSanitizer</c> itself recognizes rather
    /// than every legacy dictionary shape it also accepts, since <see cref="RawToolCall.Arguments"/>
    /// is always exactly an <see cref="IReadOnlyDictionary{TKey,TValue}"/> at the top level -- only
    /// its nested values are free-form. An unrecognized reference type falls back to its own
    /// <see cref="object.Equals(object)"/>, which is reference equality unless overridden; this is a
    /// scoped, documented limitation, not a claim of exhaustive structural comparison.
    /// </summary>
    private static bool AreValuesEquivalent(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        switch (a, b)
        {
            case (string x, string y):
                return x == y;

            case (byte[] x, byte[] y):
                return x.AsSpan().SequenceEqual(y);

            case (JsonElement x, JsonElement y):
                return JsonElement.DeepEquals(x, y);

            case (IReadOnlyDictionary<string, object?> x, IReadOnlyDictionary<string, object?> y):
                if (x.Count != y.Count)
                {
                    return false;
                }

                foreach (var (key, value) in x)
                {
                    if (!y.TryGetValue(key, out var otherValue) || !AreValuesEquivalent(value, otherValue))
                    {
                        return false;
                    }
                }

                return true;

            case (IEnumerable x, IEnumerable y) when a is not string && b is not string:
                var left = x.Cast<object?>().ToList();
                var right = y.Cast<object?>().ToList();
                if (left.Count != right.Count)
                {
                    return false;
                }

                for (var i = 0; i < left.Count; i++)
                {
                    if (!AreValuesEquivalent(left[i], right[i]))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return a.Equals(b);
        }
    }

    /// <summary>The outcome of sanitizing one optional raw text field.</summary>
    private readonly record struct SanitizedText(string? Value, bool Rejected, string? RejectionReason);

    /// <summary>
    /// Mutable, per-run state: the run's current immutable snapshot (replaced wholesale under
    /// <see cref="Gate"/> on every mutation), the raw requests seen so far keyed by
    /// <c>AttemptId</c> (idempotency for <see cref="AppendAttemptAsync"/>; capped at
    /// <see cref="CaptureLimits.MaxAttemptsPerRun"/> entries since a genuinely new AttemptId beyond
    /// capacity is rejected before ever being added here), and at most one completion snapshot
    /// (idempotency for <see cref="CompleteRunAsync"/>).
    /// </summary>
    private sealed class RunState(ExperienceRun run)
    {
        public readonly object Gate = new();
        public ExperienceRun Run = run;
        public readonly Dictionary<Guid, AppendAttemptRequest> SeenAttempts = new();
        public (Guid EventId, RunExecutionStatus Status, DateTimeOffset EndedAt)? Completion;

        // Set once, under Gate, with Completion; readable without the lock (Completion itself is a struct
        // that could be read torn), so the open-run queue can skip a completed run cheaply.
        private volatile bool _isCompleted;

        public bool IsCompleted
        {
            get => _isCompleted;
            set => _isCompleted = value;
        }
    }

    /// <summary>One completed run on the completion queue, and the monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/>) at which this service recorded its completion.</summary>
    private readonly record struct CompletedRun(Guid RunId, RunState State, long CompletedAt);

    /// <summary>One run on the open-run queue, and the monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/>) at which this service opened it.</summary>
    private readonly record struct OpenedRun(Guid RunId, RunState State, long OpenedAt);
}
