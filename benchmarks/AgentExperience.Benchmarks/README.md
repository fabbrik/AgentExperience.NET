# Benchmarks

BenchmarkDotNet measurements of the library's hot paths, on the in-memory store and on PostgreSQL. How to read them,
and the committed baseline, are in [docs/benchmarks.md](../../docs/benchmarks.md).

```bash
dotnet run -c Release --project benchmarks/AgentExperience.Benchmarks -- --filter '*'
```

Filter to one class with `--filter '*Retrieval*'` (the filter matches the full benchmark name, so `'*Confidence*'`
also selects `RetrievalBenchmarks.ConfidenceDecay`; use `'*ConfidenceBenchmarks*'` for the confidence class alone).
Results land in `BenchmarkDotNet.Artifacts/results/` under the directory you ran from, which git ignores.

## What it measures

| Class | Operation | Variants |
| --- | --- | --- |
| `RetrievalBenchmarks` | `ExperienceRetrievalService.RetrieveAsync`, text-only, default policy and weights | 1k and 10k records; text only (baseline), a preferred environment, a confidence decay policy |
| `InjectionBenchmarks` | `ExperienceContextProvider` producing an 8-record block over the 1k dataset, invoked as MAF invokes it before a run | without and with session tracking, a fresh session each operation |
| `FinalizationBenchmarks` | `ExperienceFinalizationService.FinalizeAsync` of a completed run with two attempts and four tool calls | capture is set up per iteration and not timed |
| `ConfidenceBenchmarks` | `ExperienceLifecycleService.ApplyEvidenceAsync`, supporting machine evidence from a new run | trusted host identifiers (the verifying mode's run lookup is not timed) |

Every class runs each case on `Store=InMemory` and `Store=Postgres`, with `[MemoryDiagnoser]`.

## How it is built

- **In process, short.** One job: three warmup and ten measured iterations, one launch, BenchmarkDotNet's in-process
  toolchain (`Infrastructure/BenchmarkConfig.cs`). In process is what lets one PostgreSQL container and one seeding of
  each dataset serve every case; a full run takes a few minutes, well under 15.
- **PostgreSQL once.** `Program.cs` starts one `pgvector/pgvector` container (PostgreSQL 16 unless
  `AGENTEXPERIENCE_POSTGRES_MAJOR` names another supported major) before any benchmark. Each dataset is a database of
  its own in it, migrated by `ExperienceSchemaMigrator` and analyzed after seeding; the stores connect as the
  container's superuser. **Without Docker**, the program says so and the `Postgres` cases are simply not generated.
  With Rancher Desktop or another non-default socket, set `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE`, as for the tests.
- **Seeded records** are the conformance suite's `ConformanceData.FullRecord` (linked, not referenced), varied by index
  over ten topics, so the task text matches a tenth of the scope: 100 records of the 1k dataset, 1,000 of the 10k one,
  both past the default candidate limit of 50.
- **Checked before timed.** Each class's setup runs its operation once and fails if it did not do what the benchmark
  claims to time (retrieval completed and truncated, 8 records injected, a validated durable record, a counted
  evidence application), so a number can never be the cost of a fallback.
- **No model.** The only chat client in the project throws if called. Pins: `BenchmarkDotNet` and
  `Testcontainers.PostgreSql`, exact, recorded in [compatibility-evidence.md](../../docs/compatibility-evidence.md#test-infrastructure-not-shipped).
