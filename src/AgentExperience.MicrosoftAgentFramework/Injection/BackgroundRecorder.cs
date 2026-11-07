using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>What one background write answered: whether it committed, and whether the store tracks what it was asked to record.</summary>
internal readonly record struct RecordAnswer(bool Committed, bool Tracked);

/// <summary>
/// The background store writes a context provider starts after it has answered the invocation: the injection record, the
/// holdout assignment, and the measured memory provider's assignment. Never throws and never makes the invocation wait;
/// every outcome is reported through this assembly's diagnostics as the operation it was started under: <c>Recorded</c>,
/// <c>NotTracked</c> (the store does not track it), <c>NotRecorded</c> (it refused or failed), or <c>OutcomeUnknown</c>
/// (it timed out or was cancelled).
/// </summary>
/// <param name="clock">The clock the write's bound is measured on.</param>
/// <param name="maxAbandoned">How many abandoned writes may still be running before no new one is started.</param>
internal sealed class BackgroundRecorder(TimeProvider clock, int maxAbandoned)
{
    /// <summary>The default <see cref="Timeout"/>.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The background records still running, so a test can wait for them.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _recordings = new();

    private int _abandoned;

    /// <summary>How long one background record may take, all chunks together; a few seconds by default.</summary>
    internal TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>How many items one write names: the store's maximum, <see cref="ExperienceRecordGetManyResult.MaxCount"/>. Lower only in tests.</summary>
    internal int ChunkSize { get; set; } = ExperienceRecordGetManyResult.MaxCount;

    /// <summary>How many abandoned writes are still running against the store. For tests.</summary>
    internal int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>Completes once every background record started so far has ended. For tests.</summary>
    internal Task SettledAsync() => Task.WhenAll(_recordings.Keys);

    /// <summary>
    /// Starts one background record of <paramref name="count"/> items, reported as <paramref name="operation"/>: skipped
    /// and reported as not tracked when <paramref name="tracks"/> says the store does not record them, otherwise run on
    /// the thread pool by <see cref="RecordAsync"/> and tracked until it ends. Never throws.
    /// </summary>
    internal void Start(string operation, Func<bool> tracks, int count, Func<Func<int, int, CancellationToken, Task<RecordAnswer>>> prepare)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!tracks())
            {
                InjectionDiagnostics.BackgroundRecorded(operation, InjectionDiagnostics.NotTrackedOutcome, started, errorClass: null);
                return;
            }

            var write = prepare();
            var work = Task.Run(() => RecordAsync(operation, count, write, started), CancellationToken.None);
            _recordings.TryAdd(work, 0);
            _ = work.ContinueWith(
                done => _recordings.TryRemove(done, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            // A store whose capability throws, or a scheduler that refuses the work: telemetry only.
            InjectionDiagnostics.BackgroundRecorded(operation, InjectionDiagnostics.NotRecordedOutcome, started, ExperienceOperationErrorClass.Unexpected);
        }
    }

    /// <summary>
    /// The background record: the items in chunks of at most <see cref="ExperienceRecordGetManyResult.MaxCount"/>, all
    /// within <see cref="Timeout"/> on the clock. Each write is awaited with a timeout rather than trusted to honour its
    /// token: a store that ignores cancellation is abandoned, counted against the abandoned bound, and its fault observed
    /// once it ends. Never throws.
    /// </summary>
    private async Task RecordAsync(string operation, int count, Func<int, int, CancellationToken, Task<RecordAnswer>> writeChunk, long started)
    {
        var outcome = InjectionDiagnostics.RecordedOutcome;
        ExperienceOperationErrorClass? errorClass = null;
        try
        {
            var since = clock.GetTimestamp();
            var chunkSize = Math.Clamp(ChunkSize, 1, ExperienceRecordGetManyResult.MaxCount);
            for (var offset = 0; offset < count; offset += chunkSize)
            {
                if (Volatile.Read(ref _abandoned) >= maxAbandoned)
                {
                    // Too many earlier writes are still running against the store: another would only add to them.
                    outcome = InjectionDiagnostics.NotRecordedOutcome;
                    errorClass = ExperienceOperationErrorClass.Timeout;
                    break;
                }

                var left = Timeout - clock.GetElapsedTime(since);
                if (left <= TimeSpan.Zero)
                {
                    throw new TimeoutException("The record ran out of time.");
                }

                var from = offset;
                var length = Math.Min(count, offset + chunkSize) - offset;
                var source = new CancellationTokenSource();
                var write = Task.Run(() => writeChunk(from, length, source.Token), CancellationToken.None);
                RecordAnswer recorded;
                var abandoned = false;
                try
                {
                    recorded = await write.WaitAsync(left, clock).ConfigureAwait(false);
                }
                catch (TimeoutException) when (!write.IsCompleted)
                {
                    abandoned = true;
                    Abandon(write, source);
                    throw;
                }
                finally
                {
                    if (!abandoned)
                    {
                        source.Dispose();
                    }
                }

                if (!recorded.Committed)
                {
                    outcome = InjectionDiagnostics.NotRecordedOutcome;
                    break;
                }

                if (!recorded.Tracked)
                {
                    outcome = InjectionDiagnostics.NotTrackedOutcome;
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            outcome = InjectionDiagnostics.OutcomeUnknownOutcome;
            errorClass = ExperienceOperationErrorClass.Timeout;
        }
        catch (OperationCanceledException)
        {
            outcome = InjectionDiagnostics.OutcomeUnknownOutcome;
            errorClass = ExperienceOperationErrorClass.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = InjectionDiagnostics.NotRecordedOutcome;
            errorClass = ex is ExperienceStoreException ? ExperienceOperationErrorClass.Infrastructure : ExperienceOperationErrorClass.Unexpected;
        }

        InjectionDiagnostics.BackgroundRecorded(operation, outcome, started, errorClass);
    }

    /// <summary>
    /// Hands a write that overran its bound off to run itself down: counts it, cancels its token off the recording's own
    /// path, and once it and the cancellation have ended, observes its fault, stops counting it and disposes the token
    /// source.
    /// </summary>
    private void Abandon(Task write, CancellationTokenSource source)
    {
        Interlocked.Increment(ref _abandoned);
        var cancelling = Task.Run(
            async () =>
            {
                try
                {
                    await source.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A throwing cancellation callback changes nothing about the report.
                }
            },
            CancellationToken.None);

        _ = Task.WhenAll(write, cancelling).ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                _ = write.Exception;
                source.Dispose();
                Interlocked.Decrement(ref _abandoned);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
