using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;

/// <summary>
/// Story 21.8: after an injected block is handed over, the provider records when its records were injected, in the
/// background, bounded by its own timeout, and reports the outcome as the <c>record_injection</c> operation:
/// <c>Recorded</c>, <c>NotTracked</c>, <c>NotRecorded</c> or <c>OutcomeUnknown</c>. Nothing about it can delay or fail the
/// invocation. In the telemetry collection, because the probe listens process-wide.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class InjectionRecordingTests
{
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task The_block_is_handed_over_and_the_model_called_before_the_store_records_it()
    {
        using var probe = TelemetryProbe.Start();
        var world = World(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new List<(Scope Scope, Guid[] Ids, DateTimeOffset At)>();
        world.TracksInjections = true;
        world.OnRecordInjection = async (_, scope, ids, at, _) =>
        {
            await release.Task;
            lock (recorded)
            {
                recorded.Add((scope, ids.ToArray(), at));
            }

            return new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Committed, []);
        };
        var (provider, client, results) = Provider(world);

        // The store is held, and the invocation still completes, with the block, before it is released.
        var response = await Agent(provider, client).RunAsync("refund ticket stuck on a lock");
        Assert.Equal("Hello, world", response.Text);
        Assert.Contains("Check the lock table first.", client.LastMessages!.Last(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true).Text, StringComparison.Ordinal);
        Assert.Empty(recorded);
        Assert.Empty(Outcomes(probe));

        release.SetResult();
        await provider.InjectionRecordsSettledAsync();

        var (scope, ids, at) = Assert.Single(recorded);
        Assert.Equal(TestScope, scope);
        Assert.Equal(Assert.Single(results).InjectedExperienceIds, ids);
        Assert.Equal(InjectionRecords.Now, at);
        Assert.Equal([InjectionDiagnostics.RecordedOutcome], Outcomes(probe));
        Assert.Empty(probe.For(InjectionDiagnostics.OperationFailuresInstrument, InjectionDiagnostics.RecordInjection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_store_that_does_not_track_injections_is_reported_as_not_tracked(bool claimsToTrack)
    {
        using var probe = TelemetryProbe.Start();
        var world = World(1);
        var calls = 0;
        world.TracksInjections = claimsToTrack;
        world.OnRecordInjection = (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ExperienceInjectionRecordResult.NotTracked);
        };
        var (provider, client, results) = Provider(world);

        await Agent(provider, client).RunAsync("refund ticket stuck on a lock");
        await provider.InjectionRecordsSettledAsync();

        // A store without the capability is never asked; one that claims it but answers "not tracked" is reported so.
        Assert.Equal(InjectionOutcome.Injected, Assert.Single(results).Outcome);
        Assert.Equal(claimsToTrack ? 1 : 0, calls);
        Assert.Equal([InjectionDiagnostics.NotTrackedOutcome], Outcomes(probe));
    }

    [Theory]
    [InlineData("throws", "NotRecorded", "Infrastructure")]
    [InlineData("throws unexpectedly", "NotRecorded", "Unexpected")]
    [InlineData("denied", "NotRecorded", null)]
    [InlineData("no result", "NotRecorded", null)]
    [InlineData("cancelled", "OutcomeUnknown", "Cancelled")]
    [InlineData("hangs, honouring its token", "OutcomeUnknown", "Timeout")]
    [InlineData("hangs, ignoring its token", "OutcomeUnknown", "Timeout")]
    public async Task A_store_that_cannot_record_the_injection_never_costs_the_invocation_anything(string failure, string outcome, string? errorClass)
    {
        using var probe = TelemetryProbe.Start();
        var hangs = failure.StartsWith("hangs", StringComparison.Ordinal);
        var world = World(1);
        var never = new TaskCompletionSource<ExperienceInjectionRecordResult>();
        world.TracksInjections = true;
        world.OnRecordInjection = (_, _, _, _, token) => failure switch
        {
            "throws" => throw new ExperienceStoreException("the injection table is not granted"),
            "throws unexpectedly" => throw new InvalidOperationException("a bug"),
            "denied" => Task.FromResult(new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Denied, [])),
            "no result" => Task.FromResult<ExperienceInjectionRecordResult>(null!),
            "cancelled" => Task.FromCanceled<ExperienceInjectionRecordResult>(new CancellationToken(canceled: true)),
            "hangs, honouring its token" => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith<ExperienceInjectionRecordResult>(
                _ => throw new OperationCanceledException(token), TaskScheduler.Default),
            _ => never.Task,
        };
        var (provider, client, results) = Provider(world, hangs ? TimeProvider.System : null);
        provider.InjectionRecordTimeout = TimeSpan.FromMilliseconds(200);

        var response = await Agent(provider, client).RunAsync("refund ticket stuck on a lock");
        Assert.Equal("Hello, world", response.Text);
        var result = Assert.Single(results);
        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.Null(result.Failure);

        await provider.InjectionRecordsSettledAsync();
        Assert.Equal([outcome], Outcomes(probe));
        var failures = probe.For(InjectionDiagnostics.OperationFailuresInstrument, InjectionDiagnostics.RecordInjection);
        Assert.Equal(errorClass is null ? [] : [errorClass], failures.Select(m => (string)m.Tags["error.class"]!));

        // A write abandoned past its bound is counted until it ends; one that honours its token ends once cancelled.
        if (failure == "hangs, ignoring its token")
        {
            Assert.Equal(1, provider.AbandonedInjectionRecords);
            never.SetException(new InvalidOperationException("observed, never unobserved"));
            await WaitUntilAsync(() => provider.AbandonedInjectionRecords == 0);
        }
        else if (failure == "hangs, honouring its token")
        {
            await WaitUntilAsync(() => provider.AbandonedInjectionRecords == 0);
        }
    }

    [Fact]
    public async Task More_records_than_one_write_may_name_are_recorded_in_chunks()
    {
        using var probe = TelemetryProbe.Start();
        var world = World(3);
        var writes = new List<Guid[]>();
        world.TracksInjections = true;
        world.OnRecordInjection = (_, _, ids, _, _) =>
        {
            lock (writes)
            {
                writes.Add(ids.ToArray());
            }

            return Task.FromResult(new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Committed, []));
        };
        var (provider, client, results) = Provider(world);
        provider.InjectionRecordChunkSize = 2;

        await Agent(provider, client).RunAsync("refund ticket stuck on a lock");
        await provider.InjectionRecordsSettledAsync();

        var injected = Assert.Single(results).InjectedExperienceIds;
        Assert.Equal(3, injected.Count);
        Assert.Equal([2, 1], writes.Select(w => w.Length));
        Assert.Equal(injected, writes.SelectMany(w => w));
        Assert.Equal([InjectionDiagnostics.RecordedOutcome], Outcomes(probe));
    }

    [Fact]
    public async Task Nothing_is_recorded_or_reported_when_nothing_is_injected()
    {
        using var probe = TelemetryProbe.Start();
        var world = new FakeExperienceWorld { TracksInjections = true };
        var calls = 0;
        world.OnRecordInjection = (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ExperienceInjectionRecordResult(ExperienceStoreOutcome.Committed, []));
        };
        var (provider, client, results) = Provider(world);

        await Agent(provider, client).RunAsync("refund ticket stuck on a lock");
        await provider.InjectionRecordsSettledAsync();

        Assert.Equal(InjectionOutcome.NothingToInject, Assert.Single(results).Outcome);
        Assert.Equal(0, calls);
        Assert.Empty(Outcomes(probe));
    }

    private static FakeExperienceWorld World(int records)
    {
        var world = new FakeExperienceWorld();
        for (var i = 1; i <= records; i++)
        {
            world.Publish(
                InjectionRecords.Record(InjectionRecords.Id(i), TestScope, lesson: i == 1 ? "Check the lock table first." : $"Lesson {i}."),
                relevance: 1d - (i * 0.1d));
        }

        return world;
    }

    private static (ExperienceContextProvider Provider, RecordingChatClient Client, List<ExperienceInjectionResult> Results) Provider(
        FakeExperienceWorld world,
        TimeProvider? clock = null)
    {
        clock ??= new FrozenTimeProvider(InjectionRecords.Now);
        var results = new List<ExperienceInjectionResult>();
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(world, RetrievalPolicy.Default, RankingWeights.Default, clock),
            world,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, "refund ticket stuck on a lock"),
                TimeProvider = clock,
                OnContextInjected = result =>
                {
                    lock (results)
                    {
                        results.Add(result);
                    }
                },
            });
        return (provider, new RecordingChatClient(), results);
    }

    private static ChatClientAgent Agent(ExperienceContextProvider provider, RecordingChatClient client) =>
        new(client, new ChatClientAgentOptions { AIContextProviders = [provider] });

    private static string[] Outcomes(TelemetryProbe probe) =>
        [.. probe.For(InjectionDiagnostics.OperationCountInstrument, InjectionDiagnostics.RecordInjection).Select(m => (string)m.Tags["outcome"]!)];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within 10 seconds.");
            await Task.Delay(10);
        }
    }
}
