using AgentExperience.Benchmarks.Infrastructure;
using AgentExperience.Core.KeyManagement;
using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.Postgres;
using BenchmarkDotNet.Attributes;

namespace AgentExperience.Benchmarks;

/// <summary>
/// Story 16.3: <see cref="ExperienceRetrievalService.RetrieveAsync"/>, text-only, over 1,000 <em>sealed</em> records
/// on PostgreSQL (crypto-shredding on), with a key-encryption key that adds <see cref="UnwrapLatencyMs"/> to every
/// unwrap, standing in for a remote KMS. The search returns 50 sealed candidates, whose keys the text channel fetches
/// in one <see cref="IExperienceKeyStore.GetKeysAsync"/> call. <see cref="Batched"/> is the shipped envelope store
/// (16 unwraps at a time); <see cref="OneAtATime"/> is the same store with concurrency limited to one. It still makes
/// one batch call with the connection released, so it does not reproduce the exact path before story 16.3. The
/// retrieval timeout is raised to 30 s so the one-at-a-time case measures the unwraps rather than the timeout. PostgreSQL only: the in-memory store has no encryption.
/// </summary>
[MemoryDiagnoser]
public class EncryptedRetrievalBenchmarks
{
    private const int Records = 1_000;

    private static readonly LocalExperienceKeyEncryptionKey Kek = LocalExperienceKeyEncryptionKey.Generate("bench-kek-1");
    private static readonly InMemoryExperienceWrappedKeyRepository Keys = new();
    private static readonly Lazy<Task<Npgsql.NpgsqlDataSource>> Dataset = new(SeedAsync);

    private ExperienceRetrievalService _batched = null!;
    private ExperienceRetrievalService _oneAtATime = null!;
    private RetrieveExperienceRequest _request = null!;

    /// <summary>PostgreSQL alone, or nothing when it could not start.</summary>
    public static IEnumerable<StoreKind> PostgresOnly() =>
        BenchmarkPostgres.Current is null ? [] : [StoreKind.Postgres];

    [ParamsSource(nameof(PostgresOnly))]
    public StoreKind Store { get; set; }

    [Params(0, 10)]
    public int UnwrapLatencyMs { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var dataSource = await Dataset.Value;
        var kek = new LatencyKeyEncryptionKey(Kek, TimeSpan.FromMilliseconds(UnwrapLatencyMs));
        var policy = RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(30) };

        _batched = Service(dataSource, new EnvelopeExperienceKeyStore(kek, Keys), policy);
        _oneAtATime = Service(dataSource, new EnvelopeExperienceKeyStore(kek, Keys, maxConcurrentKeyLookups: 1), policy);
        _request = new RetrieveExperienceRequest(BenchmarkData.Authorization, BenchmarkData.Scope, BenchmarkData.TaskText, CorrelationId: "benchmark");

        // A benchmark that timed out or failed would measure the fallback, not retrieval.
        await Check(_batched, _request);
        await Check(_oneAtATime, _request);
    }

    [Benchmark(Baseline = true)]
    public Task<ExperienceRetrievalResult> Batched() => _batched.RetrieveAsync(_request);

    [Benchmark]
    public Task<ExperienceRetrievalResult> OneAtATime() => _oneAtATime.RetrieveAsync(_request);

    private static ExperienceRetrievalService Service(Npgsql.NpgsqlDataSource dataSource, IExperienceKeyStore keyStore, RetrievalPolicy policy) =>
        new(
            new PostgresExperienceCandidateSource(dataSource, encryption: new ExperienceEncryption(keyStore)),
            policy,
            RankingWeights.Default,
            TimeProvider.System);

    private static async Task<Npgsql.NpgsqlDataSource> SeedAsync()
    {
        var postgres = BenchmarkPostgres.Current
            ?? throw new InvalidOperationException("The PostgreSQL benchmarks were selected, but no container is running.");
        var dataSource = await postgres.CreateDatabaseAsync("aen_bench_retrieval_encrypted").ConfigureAwait(false);
        var store = new PostgresExperienceRecordStore(
            dataSource, encryption: new ExperienceEncryption(new EnvelopeExperienceKeyStore(Kek, Keys)));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, Records),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (index, cancellationToken) =>
            {
                var created = await store.CreateAsync(BenchmarkData.Authorization, BenchmarkData.Record(index), cancellationToken).ConfigureAwait(false);
                if (created.Outcome != ExperienceStoreOutcome.Created)
                {
                    throw new InvalidOperationException($"Seeding the encrypted dataset: record {index} was {created.Outcome}, not Created.");
                }
            }).ConfigureAwait(false);

        await postgres.AnalyzeAsync(dataSource).ConfigureAwait(false);
        return dataSource;
    }

    private static async Task Check(ExperienceRetrievalService service, RetrieveExperienceRequest request)
    {
        var result = await service.RetrieveAsync(request);
        if (result.Outcome != RetrievalOutcome.Completed || result.Records.Count == 0 || !result.Truncated)
        {
            throw new InvalidOperationException(
                $"Encrypted retrieval setup check failed: {result.Outcome}, {result.Records.Count} record(s), truncated {result.Truncated}.");
        }
    }

    /// <summary>A key-encryption key that waits <c>latency</c> before every unwrap: a remote KMS's round trip.</summary>
    private sealed class LatencyKeyEncryptionKey(IExperienceKeyEncryptionKey inner, TimeSpan latency) : IExperienceKeyEncryptionKey
    {
        public string CurrentKeyId => inner.CurrentKeyId;

        public ValueTask<ExperienceWrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.WrapAsync(dataKey, reference, cancellationToken);

        public async ValueTask<byte[]> UnwrapAsync(ExperienceWrappedKey wrappedKey, ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            if (latency > TimeSpan.Zero)
            {
                await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
            }

            return await inner.UnwrapAsync(wrappedKey, reference, cancellationToken).ConfigureAwait(false);
        }
    }
}
