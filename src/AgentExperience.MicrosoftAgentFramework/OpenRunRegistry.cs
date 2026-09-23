using System.Collections.Concurrent;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// The adapter's ledger of the runs it is capturing on, one entry per run, created the moment an
/// invocation opens or continues a run and removed the moment that run is completed. It exists for
/// the two things a per-invocation <see cref="CaptureScope"/> cannot do on its own: serialize
/// invocations that name the same run, and close a run nobody ever comes back for.
/// </summary>
/// <remarks>
/// <para>
/// <b>One invocation at a time per run.</b> An entry is claimed for the whole of the invocation
/// capturing on it -- by the invocation that <em>opens</em> the run exactly as by one that continues
/// it, because a run's identifier is readable from the session the moment it is opened and a second
/// invocation could otherwise claim it while the first is still in flight. A second invocation naming
/// a claimed run is refused outright -- it runs uncaptured and the refusal is reported -- rather than
/// being allowed to interleave a second half-recorded attempt into the same run. Appending is already
/// idempotent and serialized inside the capture service; what this adds is that two <em>live</em>
/// capture scopes never share a run in the first place.
/// </para>
/// <para>
/// <b>No run stays open because a host forgot.</b> An open run holds its captured payload in memory,
/// so an entry whose invocation finished without completing the run arms a timer on the host's own
/// <see cref="TimeProvider"/> for what remains of
/// <see cref="ExperienceCaptureOptions.MaxOpenRunDuration"/>. When it fires the run is completed and
/// the reason is reported through <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>. If an
/// invocation happens to be in flight at that moment the close is handed to it rather than raced
/// against it -- but only once: the bound re-arms for one further period, and the second firing
/// closes the run underneath an invocation that never came back. An invocation can fail to come back
/// at all (a streaming consumer that abandons its enumerator without disposing it, or an inner agent
/// that hangs with no cancellation), and a bound that such an invocation could suspend forever would
/// not be a bound.
/// </para>
/// <para>
/// <b>Callbacks reach the host off the invocation.</b> A run closed by its bound completes and
/// reports from a <see cref="TimeProvider"/> timer callback on a thread-pool thread, after the
/// invocation that opened the run has long returned. <see cref="Dispose"/> is what stops that: it
/// cancels every armed bound, so no callback can arrive after the host has torn down the data source
/// and logger those callbacks would reach.
/// </para>
/// <para>
/// Like everything else on this path, nothing here throws into MAF or the caller.
/// </para>
/// </remarks>
internal sealed class OpenRunRegistry(IExperienceCaptureService service, ExperienceCaptureOptions options) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, OpenRun> _entries = new();
    private int _disposed;

    /// <summary>Whether <see cref="Dispose"/> has run, after which nothing captures through this registration.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// How many runs this registration is tracking right now: claimed by an in-flight invocation, or
    /// left open with a bound armed. Zero whenever nothing is in flight and nothing is left open.
    /// </summary>
    internal int Count => _entries.Count;

    /// <summary>
    /// Test seam: runs inside <see cref="TryClaim"/> between the dictionary lookup and taking the
    /// entry's gate -- the window in which the entry can be forgotten underneath the claimer. Never
    /// set outside tests.
    /// </summary>
    internal Action<OpenRun>? AfterLookupForTesting { get; set; }

    /// <summary>
    /// Claims the entry for <paramref name="runId"/>, creating it when the adapter has never seen
    /// that run, for an invocation that is about to capture on it. Returns <see langword="null"/>
    /// when another invocation already holds it, or the registry has been disposed.
    /// </summary>
    /// <param name="runId">The run the invocation intends to open or continue.</param>
    /// <param name="created">Whether this call created the entry, so a caller that then fails to open the run can withdraw it again.</param>
    internal OpenRun? TryClaim(Guid runId, out bool created)
    {
        created = false;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        while (true)
        {
            // Reference identity rather than a flag set inside a value factory: under contention the
            // factory can run on a thread whose instance is then discarded, and a caller that withdrew
            // on the strength of that would remove the entry the winner is holding.
            var fresh = new OpenRun(runId);
            var entry = _entries.GetOrAdd(runId, fresh);
            created = ReferenceEquals(entry, fresh);
            AfterLookupForTesting?.Invoke(entry);

            lock (entry.Gate)
            {
                // Forgotten between the lookup and this lock: the entry is no longer in the
                // dictionary, so claiming it would hold a run nobody else can see is held -- and the
                // next invocation naming the run would create a second, live entry beside it. Look
                // the run up again instead.
                if (entry.Removed)
                {
                    created = false;
                    continue;
                }

                if (entry.Busy)
                {
                    created = false;
                    return null;
                }

                entry.Busy = true;
                return entry;
            }
        }
    }

    /// <summary>
    /// Withdraws an entry this invocation created but never opened a run under, so a failed start
    /// leaves no trace and no timer behind.
    /// </summary>
    internal void Withdraw(OpenRun entry) => Remove(entry);

    /// <summary>
    /// Gives back a claim on an entry this invocation did <em>not</em> create -- a run that already
    /// existed, which then refused to be continued -- leaving the entry, and the owner's armed bound,
    /// exactly where they were.
    /// </summary>
    /// <remarks>
    /// A bound that fired and began closing the run while this claim was held has already taken the
    /// entry over for the close, which releases it when it is done; the claim is then not the
    /// holder's to give back.
    /// </remarks>
    internal void Unclaim(OpenRun entry)
    {
        lock (entry.Gate)
        {
            // A removed entry needs nothing given back: it is never claimed again.
            if (!entry.Closing)
            {
                entry.Busy = false;
            }
        }
    }

    /// <summary>
    /// Releases an entry whose invocation has finished without completing the run: the run stays
    /// open for a later attempt, and the duration bound's timer is armed if it is not already.
    /// </summary>
    /// <param name="entry">The entry this invocation claimed at its start. Every captured invocation holds one.</param>
    /// <param name="runStartedAt">When the run itself was opened, which is what the duration bound is measured from.</param>
    /// <returns>
    /// <see cref="LeaveOpenResult.LeftOpen"/> when the run really was left open, bounded;
    /// <see cref="LeaveOpenResult.BoundReached"/> or <see cref="LeaveOpenResult.NoBound"/> when the
    /// caller must complete it instead; <see cref="LeaveOpenResult.Disposed"/> when this registration
    /// has been disposed and the caller must abandon it.
    /// </returns>
    internal LeaveOpenResult TryLeaveOpen(OpenRun entry, DateTimeOffset runStartedAt)
    {
        lock (entry.Gate)
        {
            entry.RunStartedAt = runStartedAt;

            if (IsDisposed)
            {
                return LeaveOpenResult.Disposed;
            }

            if (entry.CloseRequested)
            {
                return LeaveOpenResult.BoundReached;
            }

            if (!TryArm(entry))
            {
                return LeaveOpenResult.NoBound;
            }

            entry.Busy = false;
            return LeaveOpenResult.LeftOpen;
        }
    }

    /// <summary>
    /// Completes, in the background, a run that could not be left open after all -- its duration bound
    /// fired while this invocation held it, or no bound could be armed. The caller must still hold the
    /// entry, which this releases.
    /// </summary>
    /// <param name="entry">The entry the caller holds.</param>
    /// <param name="atBound">
    /// Whether the run is being closed because its duration bound was reached, which is then reported
    /// once the completion has landed. A run closed because no bound could be armed was already
    /// reported, with the reason, when arming failed.
    /// </param>
    internal void CloseNow(OpenRun entry, bool atBound) => _ = Task.Run(() => CloseAsync(entry, atBound));

    /// <summary>Forgets a run the caller has completed, cancelling its duration bound with it.</summary>
    internal void Forget(OpenRun claimed) => Remove(claimed);

    /// <summary>
    /// Takes an entry out of the dictionary and disposes its bound. The entry is marked removed and
    /// taken out <em>under its own gate</em>, so there is no moment at which it is both unclaimed and
    /// still findable: a claim that looked it up just before this ran sees it removed and looks the
    /// run up again, rather than claiming an entry nobody else can see.
    /// </summary>
    private void Remove(OpenRun entry)
    {
        lock (entry.Gate)
        {
            entry.Removed = true;
            entry.Busy = false;

            // Removed only when it is still this very entry, so forgetting a run can never take away
            // an entry some other invocation has since put there under the same identifier.
            _entries.TryRemove(new KeyValuePair<Guid, OpenRun>(entry.RunId, entry));
        }

        entry.DisposeTimer();
    }

    /// <summary>
    /// Stops every armed duration bound and drops every entry, so no timer callback can run after the
    /// host tore down what such a callback would reach.
    /// </summary>
    /// <remarks>
    /// A run still open at disposal is <em>abandoned</em>, not completed: nothing is written and no
    /// timer reports it, because the <see cref="ExperienceCaptureOptions.OnCaptureFailure"/> and
    /// <see cref="ExperienceCaptureOptions.OnRunFinalized"/> callbacks a close would use belong to the
    /// host that is tearing down. An invocation still in flight at disposal says so itself, on its own
    /// thread, as it returns. Complete the runs that matter -- by letting
    /// <see cref="ExperienceCaptureOptions.ShouldCompleteRun"/> return <see langword="true"/> on a
    /// final invocation -- before disposing. After disposal no further invocation captures through
    /// this registration.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _entries.Values)
        {
            entry.DisposeTimer();
        }

        _entries.Clear();
    }

    /// <summary>
    /// Arms the duration bound for what remains of it, once per entry. Must be called under the
    /// entry's gate. Returns <see langword="false"/> when no bound could be armed, in which case the
    /// caller must complete the run rather than leave it open without one.
    /// </summary>
    private bool TryArm(OpenRun entry)
    {
        if (entry.Timer is not null)
        {
            return true;
        }

        try
        {
            entry.Timer = options.TimeProvider.CreateTimer(OnBoundReached, entry, Remaining(entry), Timeout.InfiniteTimeSpan);
            return true;
        }
        catch (Exception ex)
        {
            // A TimeProvider that cannot make a timer must never leave a run open with no bound at
            // all: the run is completed by its own invocation instead, and the host is told why.
            Report(new ExperienceCaptureFailure(
                ExperienceCaptureFailureStage.Finalize,
                entry.RunId,
                $"Arming the open-run bound threw {ex.GetType().FullName}; the run is completed now rather than left open without a bound.",
                ex));
            return false;
        }
    }

    /// <summary>What is left of the declared open-run duration for this entry, never negative.</summary>
    private TimeSpan Remaining(OpenRun entry)
    {
        var remaining = options.MaxOpenRunDuration - (options.TimeProvider.GetUtcNow() - entry.RunStartedAt);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>
    /// The duration bound firing. Closes the run now when no invocation holds it; when one does, hands
    /// the close to it and re-arms for one further period rather than racing it -- and closes the run
    /// underneath it when that period passes too.
    /// </summary>
    private void OnBoundReached(object? state)
    {
        // Disposing a timer does not join a callback already running, so a callback can still arrive
        // just after disposal. It does nothing: after disposal no run is closed and nothing is reported.
        if (state is not OpenRun entry || IsDisposed)
        {
            return;
        }

        lock (entry.Gate)
        {
            entry.CloseRequested = true;

            // Handed to the invocation holding the run, so the bound is reached at the end of that
            // invocation instead of in the middle of it -- but handed once, never entrusted. An
            // invocation can fail to come back at all, and a run whose payload is then held for the
            // process lifetime, refusing every later invocation that names it, is the exact outcome
            // this bound exists to prevent.
            if (entry.Busy && entry.GraceRemaining > 0 && TryRearm(entry))
            {
                entry.GraceRemaining--;
                return;
            }

            // Taken over for the close, so no invocation can claim the run while it is being completed,
            // and an invocation that held it cannot hand it back until the close forgets it.
            entry.Busy = true;
            entry.Closing = true;
        }

        // Fire-and-forget by construction: a timer callback has nowhere to await, and this path
        // must never throw. CloseAsync swallows everything and reports through the host channel.
        _ = Task.Run(() => CloseAsync(entry, atBound: true));
    }

    /// <summary>
    /// Re-arms an already-created bound for one further period. Must be called under the entry's gate.
    /// Returns <see langword="false"/> when the timer could not be re-armed, in which case the caller
    /// closes the run now rather than leaving it unbounded.
    /// </summary>
    private bool TryRearm(OpenRun entry)
    {
        try
        {
            return entry.Timer?.Change(options.MaxOpenRunDuration, Timeout.InfiniteTimeSpan) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Completes a run the bound closed and hands it to the host's finalization when one is
    /// configured. Never throws; every problem goes to
    /// <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>.
    /// </summary>
    private async Task CloseAsync(OpenRun entry, bool atBound)
    {
        var runId = entry.RunId;
        if (IsDisposed)
        {
            Forget(entry);
            return;
        }

        CancellationTokenSource? timeout = null;
        try
        {
            // Cancelled only after a timeout has been reported (no timer of its own), so a
            // token-honoring service can never race a cancellation failure ahead of the timeout
            // report -- the same order CaptureScope.FinalizeAsync uses, for the same reason.
            timeout = new CancellationTokenSource();
            var token = timeout.Token;
            var work = Task.Run(() => CompleteAndFinalizeAsync(runId, atBound, token), CancellationToken.None);
            await work.WaitAsync(options.FinalizationTimeout, options.TimeProvider).ConfigureAwait(false);

            timeout.Dispose();
        }
        catch (TimeoutException ex)
        {
            Report(new ExperienceCaptureFailure(
                ExperienceCaptureFailureStage.Finalize,
                runId,
                $"Completing a run the adapter could not leave open did not finish within {options.FinalizationTimeout}; whether it was completed is unknown.",
                ex));

            // Left undisposed on purpose: the abandoned completion may still observe its token, and
            // disposing it underneath that work would throw ObjectDisposedException into a
            // fire-and-forget task where nobody would ever see it.
            try
            {
                timeout?.Cancel();
            }
            catch
            {
                // A throwing cancellation callback must never affect the agent invocation.
            }
        }
        catch (Exception ex)
        {
            timeout?.Dispose();
            Report(new ExperienceCaptureFailure(
                ExperienceCaptureFailureStage.Finalize,
                runId,
                $"Completing the bounded run threw {ex.GetType().FullName}.",
                ex));
        }
        finally
        {
            Forget(entry);
        }
    }

    private async Task CompleteAndFinalizeAsync(Guid runId, bool atBound, CancellationToken cancellationToken)
    {
        var completed = await service
            .CompleteRunAsync(runId, options.NewId(), RunExecutionStatus.Cancelled, options.TimeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        if (completed is null || completed.Outcome is CompleteRunOutcome.RunNotFound)
        {
            Report(new ExperienceCaptureFailure(
                ExperienceCaptureFailureStage.Finalize,
                runId,
                $"Completing the bounded run returned {completed?.Outcome.ToString() ?? "null"}.",
                null));
            return;
        }

        // A Conflict or a DuplicateNoOp means the run was already completed -- by the host's own
        // invocation between the bound firing and this call, or by a close that got there first
        // because disposing a timer does not join a callback already past its Busy check. Nothing was
        // still open at the bound, so the bound is not reported: a factually wrong failure report
        // about a host's own, normally completed run is worse than no report at all.
        if (completed.Outcome is not CompleteRunOutcome.Recorded)
        {
            return;
        }

        // Reported only once the completion has actually landed, and only for the close that landed
        // it, so this message is never emitted about a run that was never open at its bound.
        if (atBound)
        {
            Report(new ExperienceCaptureFailure(
                ExperienceCaptureFailureStage.Finalize,
                runId,
                $"The run was still open at its {options.MaxOpenRunDuration} open-run bound; the adapter completed it as Cancelled. Attempts captured so far are kept.",
                null));
        }

        await CaptureScope.FinalizeExperienceAsync(service, options, runId, Report, cancellationToken).ConfigureAwait(false);
    }

    private void Report(ExperienceCaptureFailure failure)
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
}

/// <summary>What <see cref="OpenRunRegistry.TryLeaveOpen"/> did with a run its invocation did not complete.</summary>
internal enum LeaveOpenResult
{
    /// <summary>The run is left open for a further attempt, with its duration bound armed.</summary>
    LeftOpen,

    /// <summary>The duration bound fired while the invocation held the run; the caller must complete it.</summary>
    BoundReached,

    /// <summary>No duration bound could be armed (already reported); the caller must complete the run.</summary>
    NoBound,

    /// <summary>The registration has been disposed; the caller abandons the run.</summary>
    Disposed,
}

/// <summary>One run the adapter is capturing on: who owns it right now, and its duration bound.</summary>
internal sealed class OpenRun(Guid runId)
{
    /// <summary>Guards every field below; held only for the few statements that read or set them.</summary>
    internal object Gate { get; } = new();

    /// <summary>The run this entry is for.</summary>
    internal Guid RunId { get; } = runId;

    /// <summary>Whether an invocation is capturing on this run right now.</summary>
    internal bool Busy { get; set; }

    /// <summary>Whether the duration bound has fired and the next release must complete the run.</summary>
    internal bool CloseRequested { get; set; }

    /// <summary>Whether a close of this run is under way; it holds the entry until it forgets it.</summary>
    internal bool Closing { get; set; }

    /// <summary>
    /// Whether this entry has been taken out of the registry. A removed entry is never claimed again:
    /// a claim that finds one looks the run up afresh.
    /// </summary>
    internal bool Removed { get; set; }

    /// <summary>
    /// How many further bound periods an invocation still holding this run may be given before the
    /// run is closed underneath it. One: the bound is handed to a live invocation once, and an
    /// invocation that has not come back a whole bound period later is not coming back.
    /// </summary>
    internal int GraceRemaining { get; set; } = 1;

    /// <summary>When the run itself was opened, which is what the duration bound is measured from.</summary>
    internal DateTimeOffset RunStartedAt { get; set; }

    /// <summary>The armed duration bound, created once and disposed when the run is forgotten.</summary>
    internal ITimer? Timer { get; set; }

    /// <summary>Disposes the duration bound, if one was armed. Never throws.</summary>
    internal void DisposeTimer()
    {
        ITimer? timer;
        lock (Gate)
        {
            timer = Timer;
            Timer = null;
        }

        try
        {
            timer?.Dispose();
        }
        catch
        {
            // Disposing a host TimeProvider's timer must never affect the agent invocation.
        }
    }
}
