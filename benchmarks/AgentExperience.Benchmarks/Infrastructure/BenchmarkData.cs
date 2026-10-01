using System.Collections.Concurrent;
using AgentExperience.Storage.Conformance;
using AgentExperience.Storage.InMemory;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Benchmarks.Infrastructure;

/// <summary>Which store a benchmark case runs on.</summary>
public enum StoreKind
{
    /// <summary><c>AgentExperience.Storage.InMemory</c>: no I/O, so what is left is the library's own cost.</summary>
    InMemory,

    /// <summary><c>AgentExperience.Storage.Postgres</c> against the run's one container.</summary>
    Postgres,
}

/// <summary>The store kinds a benchmark's <c>Store</c> parameter takes in this run.</summary>
public static class Stores
{
    /// <summary>Both kinds, or the in-memory one alone when <see cref="BenchmarkPostgres"/> could not start.</summary>
    public static IEnumerable<StoreKind> All() =>
        BenchmarkPostgres.Current is null ? [StoreKind.InMemory] : [StoreKind.InMemory, StoreKind.Postgres];
}

/// <summary>One dataset's two ports, over one store.</summary>
/// <param name="Records">The record store.</param>
/// <param name="Candidates">The candidate source reading the same records.</param>
internal sealed record BenchmarkDataset(IExperienceRecordStore Records, IExperienceCandidateSource Candidates);

/// <summary>
/// The seeded datasets, each built once per run and shared by every benchmark case that asks for it, so a case's
/// setup costs nothing after the first. Each dataset is a store of its own (an in-memory store, or a database of its
/// own in the one container), so the 1k and 10k retrieval datasets never see each other's records, and the writing
/// benchmarks never grow the ones retrieval reads.
/// </summary>
internal static class BenchmarkData
{
    /// <summary>The tenant every benchmark record lives in.</summary>
    internal const string Tenant = "benchmarks";

    /// <summary>The one scope retrieval and injection search, and finalization and confidence write, in.</summary>
    internal static readonly Scope Scope = ConformanceData.Scope(Tenant);

    /// <summary>A host-established authorization for the whole tenant.</summary>
    internal static readonly AuthorizationContext Authorization = ConformanceData.Authorize(Tenant);

    /// <summary>
    /// The task text retrieval searches with. Every record carries one of <see cref="Topics"/>; this text matches the
    /// tenth of them about refunds, so a 1k dataset has 100 matches and a 10k one has 1,000, both past the default
    /// candidate limit of 50, which is the shape a busy scope has.
    /// </summary>
    internal const string TaskText = "refund ticket stuck on a lock";

    private static readonly string[] Topics = ["refund", "invoice", "shipping", "login", "export", "billing", "upload", "search", "report", "backup"];

    private static readonly string[] Domains = ["framework-api", "security", "business-rule", "math"];

    private static readonly ConcurrentDictionary<(StoreKind, string), Lazy<Task<BenchmarkDataset>>> Datasets = new();

    /// <summary>The retrieval dataset of <paramref name="size"/> validated records.</summary>
    internal static Task<BenchmarkDataset> RetrievalAsync(StoreKind kind, int size) =>
        GetAsync(kind, $"retrieval_{size}", size);

    /// <summary>
    /// An empty dataset for finalization and confidence to write into. In memory it is a new store on every call, so
    /// what a writing benchmark accumulates (hundreds of thousands of evidence rows) is garbage once it lets go of it,
    /// rather than a heap every later benchmark in this process pays for in collections. On PostgreSQL it is one shared
    /// database, which holds nothing in this process.
    /// </summary>
    internal static Task<BenchmarkDataset> WritesAsync(StoreKind kind) =>
        kind == StoreKind.InMemory ? CreateAsync(kind, "writes", 0) : GetAsync(kind, "writes", 0);

    /// <summary>
    /// Record <paramref name="index"/> of a dataset: the conformance suite's <see cref="ConformanceData.FullRecord"/>
    /// (two attempts, tool calls with nested arguments, evidence, a full reflection, validated at 5/7), varied by index
    /// so text search, the environment scorer and confidence decay each have something to tell apart.
    /// </summary>
    internal static ExperienceRecord Record(int index)
    {
        var topic = Topics[index % Topics.Length];
        var full = ConformanceData.FullRecord(Scope);
        var at = ConformanceData.Time.AddMinutes(index);
        return full with
        {
            TaskId = $"{topic}-ticket-resolution",
            TaskSummary = $"The {topic} ticket {index} is stuck on a lock",
            Reflection = full.Reflection! with { Lesson = $"Retry the {topic} ticket update after the lock clears." },
            Environment = full.Environment with
            {
                Metadata = new Dictionary<string, string>
                {
                    ["region"] = index % 2 == 0 ? "us-east" : "eu-west",
                    ["dotnet"] = index % 3 == 0 ? "10.0" : "9.0",
                    ["domain"] = Domains[index % Domains.Length],
                },
            },
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    private static Task<BenchmarkDataset> GetAsync(StoreKind kind, string name, int size) =>
        Datasets.GetOrAdd((kind, name), _ => new Lazy<Task<BenchmarkDataset>>(() => CreateAsync(kind, name, size))).Value;

    private static async Task<BenchmarkDataset> CreateAsync(StoreKind kind, string name, int size)
    {
        BenchmarkDataset dataset;
        Npgsql.NpgsqlDataSource? dataSource = null;
        if (kind == StoreKind.InMemory)
        {
            var store = new InMemoryExperienceRecordStore();
            dataset = new BenchmarkDataset(store, new InMemoryExperienceCandidateSource(store));
        }
        else
        {
            var postgres = BenchmarkPostgres.Current
                ?? throw new InvalidOperationException("The PostgreSQL benchmarks were selected, but no container is running.");
            dataSource = await postgres.CreateDatabaseAsync($"aen_bench_{name}").ConfigureAwait(false);
            dataset = new BenchmarkDataset(new PostgresExperienceRecordStore(dataSource), new PostgresExperienceCandidateSource(dataSource));
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, size),
            new ParallelOptions { MaxDegreeOfParallelism = kind == StoreKind.Postgres ? 8 : 1 },
            async (index, cancellationToken) =>
            {
                var created = await dataset.Records.CreateAsync(Authorization, Record(index), cancellationToken).ConfigureAwait(false);
                if (created.Outcome != ExperienceStoreOutcome.Created)
                {
                    throw new InvalidOperationException($"Seeding {kind} {name}: record {index} was {created.Outcome}, not Created.");
                }
            }).ConfigureAwait(false);

        if (dataSource is not null && size > 0)
        {
            // Fresh statistics, so the planner sees the table it will be timed against rather than an empty one.
            await BenchmarkPostgres.Current!.AnalyzeAsync(dataSource).ConfigureAwait(false);
        }

        return dataset;
    }
}
