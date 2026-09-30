using AgentExperience.Benchmarks.Infrastructure;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.Conformance;
using BenchmarkDotNet.Attributes;

namespace AgentExperience.Benchmarks;

/// <summary>
/// <see cref="ExperienceLifecycleService.ApplyEvidenceAsync"/>: one piece of supporting machine evidence about a
/// validated record, from a run not seen before, so every call reads the record, computes the new counters and score,
/// and commits the evidence, the counters and the lifecycle event in one store transaction. The service trusts the
/// host's run and round identifiers (<see cref="IndependenceVerification.TrustHostSuppliedIdentifiers"/>): the
/// default, verifying mode also looks the run up before anything is computed, which is not timed here.
/// </summary>
[MemoryDiagnoser]
public class ConfidenceBenchmarks
{
    private ExperienceLifecycleService _lifecycle = null!;
    private Guid _experienceId;

    [ParamsSource(typeof(Stores), nameof(Stores.All))]
    public StoreKind Store { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var dataset = await BenchmarkData.WritesAsync(Store);
        _lifecycle = new ExperienceLifecycleService(
            dataset.Records,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });

        // A record of its own, so the counters this benchmark moves are nobody else's.
        var record = BenchmarkData.Record(0) with { ExperienceId = Guid.NewGuid() };
        var created = await dataset.Records.CreateAsync(BenchmarkData.Authorization, record, CancellationToken.None);
        if (created.Outcome != ExperienceStoreOutcome.Created)
        {
            throw new InvalidOperationException($"Confidence setup: the record was {created.Outcome}, not Created.");
        }

        _experienceId = record.ExperienceId;

        var check = await ApplyEvidence();
        if (check.Outcome != ConfidenceUpdateOutcome.Applied || !check.Counted)
        {
            throw new InvalidOperationException($"Confidence setup check failed: {check.Outcome}, counted {check.Counted}.");
        }
    }

    [Benchmark]
    public Task<ApplyConfidenceEvidenceResult> ApplyEvidence() => _lifecycle.ApplyEvidenceAsync(
        BenchmarkData.Authorization,
        new ApplyConfidenceEvidenceRequest(
            EventId: Guid.NewGuid(),
            ExperienceId: _experienceId,
            Scope: BenchmarkData.Scope,
            EvidenceId: Guid.NewGuid(),
            Kind: ConfidenceEvidenceKind.Supporting,
            Source: ConfidenceEvidenceSource.Machine,
            RunId: Guid.NewGuid(),
            VerificationRoundId: Guid.NewGuid(),
            Reason: "the lesson was reused and the checks passed",
            Producer: "benchmarks",
            OccurredAt: ConformanceData.Time.AddDays(1)),
        CancellationToken.None);
}
