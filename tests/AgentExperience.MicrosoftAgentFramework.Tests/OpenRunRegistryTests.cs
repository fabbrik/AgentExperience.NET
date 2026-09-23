using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The open-run ledger on its own, for the interleavings an agent-level test cannot reach on demand:
/// an entry forgotten between a claimer's lookup and its lock, and a claim given back while a bound
/// close holds the entry.
/// </summary>
public class OpenRunRegistryTests
{
    private static (OpenRunRegistry Registry, RecordingCaptureService Service, ManualBoundTimeProvider Clock) Create()
    {
        var service = new RecordingCaptureService(new InMemoryExperienceCaptureService(
            new DefaultSanitizer(new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal))),
            new CaptureLimits(10, 50, 10_000, 10_000)));
        var clock = new ManualBoundTimeProvider();
        var options = new ExperienceCaptureOptions
        {
            ResolveRun = _ => throw new InvalidOperationException("not used by the ledger"),
            TimeProvider = clock,
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

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (registry.Count != 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The close never forgot the entry.");
            await Task.Delay(10);
        }
    }
}
