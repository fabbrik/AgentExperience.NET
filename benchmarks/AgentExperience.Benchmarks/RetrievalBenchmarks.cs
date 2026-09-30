using AgentExperience.Benchmarks.Infrastructure;
using AgentExperience.Core.Retrieval;
using BenchmarkDotNet.Attributes;

namespace AgentExperience.Benchmarks;

/// <summary>
/// <see cref="ExperienceRetrievalService.RetrieveAsync"/>, text-only, over a scope of 1k or 10k validated records, with
/// the default policy (the 500 ms timeout included) and weights. <see cref="TextOnly"/> is the baseline; the other two
/// add a preferred environment (story 10.1, graded by the default <see cref="AttributeMatchEnvironmentScorer"/>) and a
/// confidence decay policy (story 10.3), so the ratio column shows what each costs.
/// </summary>
[MemoryDiagnoser]
public class RetrievalBenchmarks
{
    private static readonly IReadOnlyDictionary<string, string> Preferred = new Dictionary<string, string>
    {
        ["dotnet"] = "10.0",
        ["region"] = "us-east",
    };

    private static readonly ConfidenceDecayPolicy Decay = new()
    {
        HalfLives = new Dictionary<string, TimeSpan?>
        {
            ["framework-api"] = TimeSpan.FromDays(30),
            ["security"] = TimeSpan.FromDays(60),
            ["business-rule"] = TimeSpan.FromDays(365),
            ["math"] = null,
        },
    };

    private ExperienceRetrievalService _plain = null!;
    private ExperienceRetrievalService _decaying = null!;
    private RetrieveExperienceRequest _request = null!;
    private RetrieveExperienceRequest _preferring = null!;

    [ParamsSource(typeof(Stores), nameof(Stores.All))]
    public StoreKind Store { get; set; }

    [Params(1_000, 10_000)]
    public int Records { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var dataset = await BenchmarkData.RetrievalAsync(Store, Records);
        _plain = new ExperienceRetrievalService(dataset.Candidates, RetrievalPolicy.Default, RankingWeights.Default, TimeProvider.System);
        _decaying = new ExperienceRetrievalService(
            dataset.Candidates, RetrievalPolicy.Default, RankingWeights.Default, TimeProvider.System,
            embeddingIndex: null, embeddingGenerator: null, environmentScorer: null, confidenceDecay: Decay);
        _request = new RetrieveExperienceRequest(BenchmarkData.Authorization, BenchmarkData.Scope, BenchmarkData.TaskText, CorrelationId: "benchmark");
        _preferring = _request with { PreferredEnvironmentAttributes = Preferred };

        // A benchmark that timed out or failed would measure the fallback, not retrieval.
        await Check(_plain, _request);
        await Check(_plain, _preferring);
        await Check(_decaying, _request);
    }

    [Benchmark(Baseline = true)]
    public Task<ExperienceRetrievalResult> TextOnly() => _plain.RetrieveAsync(_request);

    [Benchmark]
    public Task<ExperienceRetrievalResult> PreferredEnvironment() => _plain.RetrieveAsync(_preferring);

    [Benchmark]
    public Task<ExperienceRetrievalResult> ConfidenceDecay() => _decaying.RetrieveAsync(_request);

    private static async Task Check(ExperienceRetrievalService service, RetrieveExperienceRequest request)
    {
        var result = await service.RetrieveAsync(request);
        if (result.Outcome != RetrievalOutcome.Completed || result.Records.Count == 0 || !result.Truncated)
        {
            throw new InvalidOperationException(
                $"Retrieval setup check failed: {result.Outcome}, {result.Records.Count} record(s), truncated {result.Truncated}.");
        }
    }
}
