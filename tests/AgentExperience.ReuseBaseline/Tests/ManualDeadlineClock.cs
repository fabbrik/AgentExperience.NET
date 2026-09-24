using AgentExperience.ReuseBaseline.Experiment;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// A clock for the per-trial deadline that moves only when <see cref="Advance"/> is called, so
/// whether a deadline elapses is decided by the test and never by how loaded the machine is.
/// </summary>
/// <remarks>
/// <para>
/// A faulted run golden-files which trials timed out. On the system clock with a 250 ms deadline,
/// a clean trial that happened to take 270 ms under CPU contention was recorded as TimedOut, and
/// the verdict flipped. On this clock a clean trial's deadline cannot elapse, and a hung trial's
/// always does: the run advances the clock past the deadline from
/// <see cref="ExperimentOptions.OnTrialHanging"/>.
/// </para>
/// <para>
/// Trials run one after another and each disposes its deadline before the next starts, so an
/// advance only ever reaches the deadline of the trial that is hanging.
/// </para>
/// </remarks>
internal sealed class ManualDeadlineClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A run configured to pair this clock with its trial deadline: every hang advances it past <paramref name="trialTimeout"/>.</summary>
    /// <param name="trialTimeout">The per-trial deadline the run declares.</param>
    /// <param name="configure">The rest of the run's options.</param>
    public static ExperimentOptions Drive(TimeSpan trialTimeout, ExperimentOptions configure)
    {
        var clock = new ManualDeadlineClock();

        return configure with
        {
            TrialTimeout = trialTimeout,
            DeadlineClock = clock,
            OnTrialHanging = _ => clock.Advance(trialTimeout),
        };
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires, on the calling thread, every timer that has come due.</summary>
    /// <param name="by">How far to move it.</param>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        DateTimeOffset target;
        lock (gate)
        {
            target = now + by;
        }

        while (true)
        {
            ManualTimer? due;
            lock (gate)
            {
                due = timers
                    .Where(timer => timer.DueAt is { } at && at <= target)
                    .OrderBy(timer => timer.DueAt)
                    .FirstOrDefault();

                if (due is null)
                {
                    now = target;
                    return;
                }

                now = due.DueAt!.Value;
                due.DueAt = due.Period is { } period ? now + period : null;
            }

            // Outside the lock: a deadline's callback cancels a token, whose own callbacks may
            // read or re-arm this clock.
            due.Fire();
        }
    }

    private sealed class ManualTimer(ManualDeadlineClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;

        public DateTimeOffset? DueAt { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed)
                {
                    return false;
                }

                if (!clock.timers.Contains(this))
                {
                    clock.timers.Add(this);
                }

                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock.gate)
            {
                disposed = true;
                clock.timers.Remove(this);
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
