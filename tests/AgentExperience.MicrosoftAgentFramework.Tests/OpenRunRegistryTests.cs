using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The open-run ledger on its own, for the interleavings an agent-level test cannot reach on demand:
/// an entry forgotten between a claimer's lookup and its lock, and a claim given back while a bound
/// close holds the entry.
/// </summary>
public class OpenRunRegistryTests
{
    private static (OpenRunRegistry Registry, RecordingCaptureService Service, ManualBoundTimeProvider Clock) Create(Action<ExperienceCaptureFailure>? onFailure = null)
    {
        var service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
            new DefaultSanitizer(new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal))),
            new CaptureLimits(10, 50, 10_000, 10_000)));
        var clock = new ManualBoundTimeProvider();
        var options = new ExperienceCaptureOptions
        {
            ResolveRun = _ => throw new InvalidOperationException("not used by the ledger"),
            TimeProvider = clock,
            OnCaptureFailure = onFailure,
        };

        return (new OpenRunRegistry(service, options), service, clock);
    }

    /// <summary>
    /// The race the verification-gap review found. A claimer looks the run up, and before it takes the
    /// entry's gate the holder finishes and forgets the entry. The claimer must not then claim an entry
    /// that is no longer in the ledger: if it did, the next invocation naming the run would create a
    /// second, live entry beside it, and two scopes would capture on one run. It looks the run up
    /// again instead, so exactly one live claim exists and it is the findable one.
    /// </summary>
    [Theory]
    [InlineData("forget")]
    [InlineData("withdraw")]
    public void A_claim_that_finds_its_entry_forgotten_under_it_looks_the_run_up_again(string how)
    {
        var (registry, _, _) = Create();
        var runId = Guid.NewGuid();

        var holder = registry.TryClaim(runId, out var holderCreated);
        Assert.NotNull(holder);
        Assert.True(holderCreated);

        registry.AfterLookupForTesting = entry =>
        {
            if (ReferenceEquals(entry, holder))
            {
                if (how == "forget")
                {
                    registry.Forget(holder);
                }
                else
                {
                    registry.Withdraw(holder);
                }
            }
        };

        var claimer = registry.TryClaim(runId, out var claimerCreated);
        registry.AfterLookupForTesting = null;

        Assert.NotNull(claimer);
        Assert.NotSame(holder, claimer);
        Assert.True(claimerCreated);
        Assert.Equal(1, registry.Count);

        // The claimer's entry is the one in the ledger, so a third invocation is refused.
        Assert.Null(registry.TryClaim(runId, out var thirdCreated));
        Assert.False(thirdCreated);
    }

    /// <summary>
    /// The same race without a seam: many threads claiming and forgetting one run, counting how many
    /// hold it at once. More than one is two live scopes on one run.
    /// </summary>
    [Fact]
    public async Task Under_contention_at_most_one_claim_on_a_run_is_ever_live()
    {
        var (registry, _, _) = Create();
        var runId = Guid.NewGuid();
        var holders = 0;
        var overlaps = 0;
        var claims = 0;

        var workers = Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount)).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                var entry = registry.TryClaim(runId, out _);
                if (entry is null)
                {
                    continue;
                }

                Interlocked.Increment(ref claims);
                if (Interlocked.Increment(ref holders) != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                Thread.SpinWait(20);
                Interlocked.Decrement(ref holders);
                registry.Forget(entry);
            }
        }));

        await Task.WhenAll(workers);

        Assert.True(claims > 0);
        Assert.Equal(0, Volatile.Read(ref overlaps));
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// A claim on an existing entry, given back while a bound close has taken that entry over, leaves
    /// it held by the close: releasing it would let a new invocation in to append to a run that is
    /// being completed underneath it.
    /// </summary>
    [Fact]
    public async Task A_claim_given_back_while_a_bound_close_holds_the_entry_leaves_it_held()
    {
        var (registry, service, clock) = Create();
        var runId = Guid.NewGuid();

        var owner = registry.TryClaim(runId, out _);
        Assert.NotNull(owner);
        Assert.Equal(LeaveOpenResult.LeftOpen, registry.TryLeaveOpen(owner, clock.GetUtcNow()));
        var bound = Assert.Single(clock.Bounds);

        var intruder = registry.TryClaim(runId, out var intruderCreated);
        Assert.NotNull(intruder);
        Assert.Same(owner, intruder);
        Assert.False(intruderCreated);

        using var gate = new ManualResetEventSlim(false);
        service.BlockComplete = gate;
        try
        {
            // Handed to the holder once, then the close takes the entry over.
            bound.Fire();
            bound.Fire();
            await service.CompleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            registry.Unclaim(intruder);

            Assert.Null(registry.TryClaim(runId, out _));
        }
        finally
        {
            gate.Set();
        }

        await Eventually(() => registry.Count == 0, "The close never forgot the entry.");
    }

    // ---- Story 5.3: the bound is armed when the run opens -----------------------------------------

    /// <summary>
    /// Arming at open measures the run, not the arming: an entry armed for a run that was opened
    /// earlier (a continuation whose entry had been forgotten) gets only what remains of the bound,
    /// one already past it gets zero, never a negative due time, and one whose recorded start is ahead
    /// of this clock (skew, another node) gets the whole bound, never more.
    /// </summary>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(2, 3)]
    [InlineData(7, 0)]
    [InlineData(-3, 5)]
    public void Arming_at_open_arms_what_remains_of_the_runs_bound(int openedMinutesAgo, int expectedMinutes)
    {
        var (registry, _, clock) = Create();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        clock.Now = now;

        var entry = registry.TryClaim(Guid.NewGuid(), out _);
        Assert.NotNull(entry);
        registry.ArmAtOpen(entry, now.AddMinutes(-openedMinutesAgo));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), Assert.Single(clock.Bounds).DueTime);
    }

    /// <summary>
    /// One bound per run: arming again, and releasing the run open afterwards, reuse the bound armed
    /// at open rather than starting a second clock.
    /// </summary>
    [Fact]
    public void The_bound_armed_at_open_is_the_one_a_release_keeps()
    {
        var (registry, _, clock) = Create();
        var entry = registry.TryClaim(Guid.NewGuid(), out _);
        Assert.NotNull(entry);

        registry.ArmAtOpen(entry, clock.GetUtcNow());
        registry.ArmAtOpen(entry, clock.GetUtcNow());
        var bound = Assert.Single(clock.Bounds);

        Assert.Equal(LeaveOpenResult.LeftOpen, registry.TryLeaveOpen(entry, clock.GetUtcNow()));
        Assert.Same(bound, Assert.Single(clock.Bounds));
        Assert.False(bound.Disposed);

        registry.Forget(entry);
        Assert.True(bound.Disposed);
    }

    /// <summary>
    /// Disposal cancels a bound armed at open while its invocation is still in flight, and an entry
    /// claimed before disposal but armed after it arms nothing at all.
    /// </summary>
    [Fact]
    public void Disposal_cancels_a_bound_armed_at_open_and_arming_after_disposal_arms_nothing()
    {
        var (registry, service, clock) = Create();
        var inFlight = registry.TryClaim(Guid.NewGuid(), out _);
        var late = registry.TryClaim(Guid.NewGuid(), out _);
        Assert.NotNull(inFlight);
        Assert.NotNull(late);
        registry.ArmAtOpen(inFlight, clock.GetUtcNow());
        var bound = Assert.Single(clock.Bounds);

        registry.Dispose();
        Assert.True(bound.Disposed);

        registry.ArmAtOpen(late, clock.GetUtcNow());
        Assert.Single(clock.Bounds);

        bound.Fire();
        bound.Fire();
        Assert.Equal(0, service.CompleteCalls);
        Assert.Equal(LeaveOpenResult.Disposed, registry.TryLeaveOpen(inFlight, clock.GetUtcNow()));
    }

    /// <summary>
    /// The one late callback the removed-entry check cannot stop: it took the entry's gate before the
    /// run's own invocation forgot it, after that invocation's completion had already landed. Its
    /// close then finds the run completed, and reports nothing -- a normally completed run is never
    /// reported as still open at its bound. Driven as a fixed ordering (completion, fire, fire, forget)
    /// rather than a free race, so the ordering under test is the one that happens.
    /// </summary>
    [Fact]
    public async Task A_bound_close_ordered_after_a_normal_completion_reports_nothing()
    {
        var failures = new List<ExperienceCaptureFailure>();
        var (registry, service, clock) = Create(failure =>
        {
            lock (failures)
            {
                failures.Add(failure);
            }
        });
        var runId = Guid.NewGuid();
        var openedAt = clock.GetUtcNow();
        Assert.Equal(
            StartRunOutcome.Started,
            service.StartRun(runId, "task", null, new Scope("tenant-1", "app-1", "project-1"), new ExperienceCaptureOptions { ResolveRun = _ => null! }.Environment, new Provenance("tests", null, openedAt, null), openedAt).Outcome);

        var entry = registry.TryClaim(runId, out _);
        Assert.NotNull(entry);
        registry.ArmAtOpen(entry, openedAt);
        var bound = Assert.Single(clock.Bounds);

        // The invocation's completion lands, and before it forgets the entry the bound fires twice:
        // handed once, then the close takes the entry over.
        Assert.Equal(
            CompleteRunOutcome.Recorded,
            (await service.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, clock.GetUtcNow())).Outcome);
        bound.Fire();
        bound.Fire();

        // The close forgets the entry in its finally, after anything it would report: the entry
        // leaving the ledger is the close having finished, not a guess at how long it takes.
        await Eventually(() => registry.Count == 0, "The bound close never finished.");
        Assert.Equal(2, service.CompleteCalls);
        registry.Forget(entry);

        Assert.True(service.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
        lock (failures)
        {
            Assert.Empty(failures);
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> that fires a due-now timer synchronously, inside
    /// <c>CreateTimer</c> or <c>Change</c>, as some fake clocks do. The bound armed at open for a run
    /// already past it must still be handed to its invocation, not close the run underneath its own
    /// opener because the timer did not exist yet when it fired.
    /// </summary>
    [Fact]
    public void A_bound_fired_synchronously_while_being_armed_is_still_handed_to_the_invocation()
    {
        var service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
            new DefaultSanitizer(new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal))),
            new CaptureLimits(10, 50, 10_000, 10_000)));
        var clock = new SynchronousDueNowTimeProvider();
        var registry = new OpenRunRegistry(service, new ExperienceCaptureOptions
        {
            ResolveRun = _ => throw new InvalidOperationException("not used by the ledger"),
            TimeProvider = clock,
        });

        var entry = registry.TryClaim(Guid.NewGuid(), out _);
        Assert.NotNull(entry);
        registry.ArmAtOpen(entry, clock.GetUtcNow().AddMinutes(-10));

        var timer = Assert.Single(clock.Timers);
        Assert.Same(timer, entry.Timer);
        Assert.Equal(0, service.CompleteCalls);
        Assert.False(entry.Closing);

        // Handed to the invocation: re-armed for one further period, and its release must close.
        Assert.Equal(TimeSpan.FromMinutes(5), timer.LastDueTime);
        Assert.Equal(LeaveOpenResult.BoundReached, registry.TryLeaveOpen(entry, clock.GetUtcNow().AddMinutes(-10)));
    }

    /// <summary>
    /// A close started by a release (the bound was reached while the invocation held the run) marks
    /// the entry closing, so the bound's re-armed timer firing while that close is still completing
    /// the run does not start a second close.
    /// </summary>
    [Fact]
    public async Task A_rearmed_bound_firing_during_a_release_close_does_not_start_a_second_close()
    {
        var failures = new List<ExperienceCaptureFailure>();
        var (registry, service, clock) = Create(failure =>
        {
            lock (failures)
            {
                failures.Add(failure);
            }
        });
        var runId = Guid.NewGuid();
        var openedAt = clock.GetUtcNow();
        service.StartRun(runId, "task", null, new Scope("tenant-1", "app-1", "project-1"), new ExperienceCaptureOptions { ResolveRun = _ => null! }.Environment, new Provenance("tests", null, openedAt, null), openedAt);

        var entry = registry.TryClaim(runId, out _);
        Assert.NotNull(entry);
        registry.ArmAtOpen(entry, openedAt);
        var bound = Assert.Single(clock.Bounds);
        bound.Fire();
        Assert.Equal(LeaveOpenResult.BoundReached, registry.TryLeaveOpen(entry, openedAt));

        using var gate = new ManualResetEventSlim(false);
        service.BlockComplete = gate;
        try
        {
            registry.CloseNow(entry, atBound: true);
            await service.CompleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The re-armed period passes while the close is still inside the capture service.
            bound.Fire();
        }
        finally
        {
            gate.Set();
        }

        await Eventually(() => registry.Count == 0, "The close never finished.");
        Assert.Equal(1, service.CompleteCalls);
        lock (failures)
        {
            Assert.Single(failures, f => f.Reason.Contains("still open at its", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Disposal does not wait for a bound's close already inside the capture service, but that close
    /// reports nothing once it sees the registration disposed: the host that would receive the report
    /// is the one tearing down.
    /// </summary>
    [Fact]
    public async Task A_close_already_under_way_at_disposal_reports_nothing_afterwards()
    {
        var failures = new List<ExperienceCaptureFailure>();
        var (registry, service, clock) = Create(failure =>
        {
            lock (failures)
            {
                failures.Add(failure);
            }
        });
        var runId = Guid.NewGuid();
        var openedAt = clock.GetUtcNow();
        service.StartRun(runId, "task", null, new Scope("tenant-1", "app-1", "project-1"), new ExperienceCaptureOptions { ResolveRun = _ => null! }.Environment, new Provenance("tests", null, openedAt, null), openedAt);

        var entry = registry.TryClaim(runId, out _);
        Assert.NotNull(entry);
        registry.ArmAtOpen(entry, openedAt);
        Assert.Equal(LeaveOpenResult.LeftOpen, registry.TryLeaveOpen(entry, openedAt));

        using var gate = new ManualResetEventSlim(false);
        service.BlockComplete = gate;
        try
        {
            Assert.Single(clock.Bounds).Fire();
            await service.CompleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            registry.Dispose();
        }
        finally
        {
            gate.Set();
        }

        // The completion itself still lands (Dispose does not reach into the service) ...
        await Eventually(() => service.TryGetRun(runId, out var run) && run.ExecutionStatus is not null, "The close never completed the run.");

        // ... and the close's report, which follows it on the same thread, is suppressed. The close
        // is fire-and-forget with no handle to await, so this last check is a bounded wait.
        await Task.Delay(200);
        lock (failures)
        {
            Assert.Empty(failures);
        }
    }

    private sealed class SynchronousDueNowTimeProvider : TimeProvider
    {
        private readonly List<SynchronousTimer> _timers = [];

        public IReadOnlyList<SynchronousTimer> Timers => _timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (state is not OpenRun)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }

            var timer = new SynchronousTimer(callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    private sealed class SynchronousTimer(TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? LastDueTime { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            LastDueTime = dueTime;
            if (dueTime == TimeSpan.Zero)
            {
                callback(state);
            }

            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            await Task.Delay(10);
        }
    }
}
