using AgentExperience.Benchmarks.Infrastructure;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using BenchmarkDotNet.Attributes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.Benchmarks;

/// <summary>
/// <see cref="ExperienceContextProvider"/> producing one Historical Reference block of 8 records: retrieval over the 1k
/// dataset, the batched final eligibility re-read (<c>GetManyAsync</c>), the host decision and the writer, with the
/// default limits. Invoked directly, as MAF invokes it before a run (<see cref="AIContextProvider.InvokingAsync"/>),
/// so no model and no agent run is timed. Each operation uses a fresh session, so the session-tracking variant pays
/// for a first invocation's account (loading, staging and saving it) and the difference between the two is that cost.
/// </summary>
[MemoryDiagnoser]
public class InjectionBenchmarks
{
    private const int ExpectedRecords = 8;

    private static readonly ChatMessage[] Messages = [new(ChatRole.User, BenchmarkData.TaskText)];

    private ChatClientAgent _agent = null!;
    private ExperienceContextProvider _untracked = null!;
    private ExperienceContextProvider _tracked = null!;
    private ExperienceInjectionResult? _last;

    [ParamsSource(typeof(Stores), nameof(Stores.All))]
    public StoreKind Store { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var dataset = await BenchmarkData.RetrievalAsync(Store, 1_000);
        var retrieval = new ExperienceRetrievalService(dataset.Candidates, RetrievalPolicy.Default, RankingWeights.Default, TimeProvider.System);
        var request = new RetrieveExperienceRequest(BenchmarkData.Authorization, BenchmarkData.Scope, BenchmarkData.TaskText, CorrelationId: "benchmark");

        _agent = new ChatClientAgent(new NeverCalledChatClient(), new ChatClientAgentOptions());
        _untracked = new ExperienceContextProvider(retrieval, dataset.Records, Options(request, sessionLimits: null));
        _tracked = new ExperienceContextProvider(retrieval, dataset.Records, Options(request, ExperienceInjectionSessionLimits.Default));

        // A benchmark whose block came out short would be measuring something other than an 8-record block.
        await Check(_untracked);
        await Check(_tracked);
    }

    [Benchmark(Baseline = true)]
    public Task<AIContext> WithoutSessionTracking() => InvokeAsync(_untracked);

    [Benchmark]
    public Task<AIContext> WithSessionTracking() => InvokeAsync(_tracked);

    private async Task<AIContext> InvokeAsync(ExperienceContextProvider provider)
    {
        var session = await _agent.CreateSessionAsync();

        // MAF marks the context's constructor as evaluation-only (MAAI001): a host never builds one, MAF does, before
        // each run. Building it here is what lets the provider be timed without an agent run around it.
#pragma warning disable MAAI001
        var context = new AIContextProvider.InvokingContext(_agent, session, new AIContext { Messages = Messages });
#pragma warning restore MAAI001
        return await provider.InvokingAsync(context);
    }

    private ExperienceInjectionOptions Options(RetrieveExperienceRequest request, ExperienceInjectionSessionLimits? sessionLimits) => new()
    {
        ResolveRequest = _ => request,
        SessionLimits = sessionLimits,
        OnContextInjected = result => _last = result,
    };

    private async Task Check(ExperienceContextProvider provider)
    {
        _last = null;
        await InvokeAsync(provider);
        if (_last is not { Outcome: InjectionOutcome.Injected, InjectedCount: ExpectedRecords })
        {
            throw new InvalidOperationException(
                $"Injection setup check failed: {_last?.Outcome.ToString() ?? "no result"}, {_last?.InjectedCount ?? 0} record(s) injected, not {ExpectedRecords}.");
        }
    }
}
