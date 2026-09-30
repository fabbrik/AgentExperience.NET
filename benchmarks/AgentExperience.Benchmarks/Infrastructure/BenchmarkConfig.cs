using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;

namespace AgentExperience.Benchmarks.Infrastructure;

/// <summary>
/// The one job every benchmark runs under: short, so a full local run stays well inside ~15 minutes, and in process,
/// so the one PostgreSQL container <see cref="BenchmarkPostgres"/> starts, and the datasets <see cref="BenchmarkData"/>
/// seeds, serve every benchmark instead of being rebuilt in a child process per case.
/// </summary>
/// <remarks>
/// Three warmup and ten measured iterations of about 250 ms each, one launch. Ten iterations are enough for a mean
/// and a rough P95 on one machine, not for a publication-grade distribution; <c>docs/benchmarks.md</c> says numbers
/// compare only on the same machine. In-process means the benchmarks share one runtime and one heap with each other and with BenchmarkDotNet
/// itself, which is the trade for starting and seeding PostgreSQL once.
/// </remarks>
internal static class BenchmarkConfig
{
    internal static IConfig Create() => DefaultConfig.Instance
        .AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(10)
            .WithIterationTime(TimeInterval.FromMilliseconds(250))
            .WithId("Short-InProcess"))
        .AddColumn(StatisticColumn.P95);
}
