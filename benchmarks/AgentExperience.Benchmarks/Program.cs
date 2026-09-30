using AgentExperience.Benchmarks.Infrastructure;
using BenchmarkDotNet.Running;

// The explicit command, and the only way these benchmarks ever run (CI never does):
//
//   dotnet run -c Release --project benchmarks/AgentExperience.Benchmarks -- --filter '*'
//
// PostgreSQL starts once, here, before any benchmark: every case runs in this process (see BenchmarkConfig), so the
// container and the seeded datasets serve them all. Without Docker the PostgreSQL cases are skipped with a message.
await using var postgres = await BenchmarkPostgres.StartAsync(Console.Out);

var summaries = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, BenchmarkConfig.Create());

Console.WriteLine();
Console.WriteLine(postgres is null
    ? "PostgreSQL: not run (Docker unavailable)."
    : $"PostgreSQL: server_version {postgres.ServerVersion}.");

return summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
