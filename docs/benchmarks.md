# Benchmarks

**In short.** `benchmarks/AgentExperience.Benchmarks` measures the library's hot paths with BenchmarkDotNet: retrieval,
injection, finalization and confidence evidence, each on the in-memory store and on PostgreSQL, with allocations. You
run it by hand; CI never does, and no test asserts a timing. Below is one baseline from one machine. **Numbers compare
only with another run on the same machine**, under the same load. A mean from a different laptop, a CI runner or a
different Docker VM says nothing about a regression.

## Running it

```bash
dotnet run -c Release --project benchmarks/AgentExperience.Benchmarks -- --filter '*'
```

- **Docker** is needed for the PostgreSQL cases. The program starts one `pgvector/pgvector` container (PostgreSQL 16,
  or the major `AGENTEXPERIENCE_POSTGRES_MAJOR` names), creates and seeds each dataset once, and removes the container
  at the end. Without Docker it prints that the PostgreSQL benchmarks are skipped, and runs only the in-memory ones.
  If your Docker socket is not the default one (Rancher Desktop, for example), set
  `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`, as for the tests.
- **One class**: `--filter '*RetrievalBenchmarks*'`, `'*InjectionBenchmarks*'`, `'*FinalizationBenchmarks*'`,
  `'*EncryptedRetrievalBenchmarks*'` or `'*ConfidenceBenchmarks*'`. `'*RetrievalBenchmarks*'` also selects
  `EncryptedRetrievalBenchmarks`; use `'AgentExperience.Benchmarks.RetrievalBenchmarks*'` for the plaintext class
  alone. The filter matches the whole name, so a bare `'*Confidence*'` also selects
  `RetrievalBenchmarks.ConfidenceDecay`.
- **Time.** The baseline run below took 4 minutes 26 seconds, build included. Every case runs in one process with a
  short job (3 warmup and 10 measured iterations of about 250 ms), so the container starts and each dataset is seeded
  once.
- **Row-level security.** By default the stores connect as the container's superuser, which row-level security does
  not bind. `AGENTEXPERIENCE_BENCHMARK_RLS=on` (or `off`) makes each dataset the two-role deployment instead, with
  `EnableRowLevelSecurity` set to that value; see [Row-level security](#row-level-security-story-151) below.
- **Output.** BenchmarkDotNet writes Markdown, CSV and HTML reports to `BenchmarkDotNet.Artifacts/results/` under the
  directory you ran from. Git ignores that directory.

## Comparing two runs

Run the baseline commit and your change back to back on the same machine, with nothing else heavy running (a build,
a test suite or another container skews the PostgreSQL numbers in particular), and compare the Markdown reports.
Treat a difference as real only when it is well outside both runs' `Error` columns. Allocations are deterministic
enough to compare across machines of the same architecture; times are not. The PostgreSQL rows move by 10–30% between
identical runs on the baseline machine, because the database shares a two-CPU Docker VM with everything else Docker
runs. Their `Error` column shows it.

## What is measured

| Class | Operation | Setup |
| --- | --- | --- |
| `RetrievalBenchmarks` | `ExperienceRetrievalService.RetrieveAsync`, text-only, with `RetrievalPolicy.Default` (its 500 ms timeout included) and `RankingWeights.Default` | A scope of 1,000 or 10,000 validated records, each the conformance suite's `FullRecord` varied by index. The task text matches a tenth of them (100 or 1,000), so the candidate limit of 50 truncates every search. `PreferredEnvironment` sets two preferred attributes for the default scorer ([story 10.1](guide/retrieval.md#preferring-an-environment)); `ConfidenceDecay` adds a four-domain decay policy ([story 10.3](guide/retrieval.md#decaying-confidence-by-domain)) |
| `InjectionBenchmarks` | `ExperienceContextProvider` producing one [Historical Reference](guide/injection.md) block of 8 records: retrieval, the one batched re-read of the final eligibility check, the writer | The 1,000-record scope and default limits. The provider is invoked the way MAF invokes it before a run (`InvokingAsync`), so no agent run and no model are timed. Each operation uses a fresh session, so `WithSessionTracking` pays for a first invocation's session account |
| `FinalizationBenchmarks` | `ExperienceFinalizationService.FinalizeAsync` of a completed run with two attempts and four tool calls, verified against a closed round | Capturing the runs happens in each iteration's setup and is not timed. Each iteration gets a new capture service and, in memory, a new store |
| `EncryptedRetrievalBenchmarks` | `ExperienceRetrievalService.RetrieveAsync`, text-only, with crypto-shredding on and the retrieval timeout raised to 30 s, on PostgreSQL only | 1,000 sealed records shaped like `RetrievalBenchmarks`' (50 sealed candidates per search), keys held by `EnvelopeExperienceKeyStore` over a KEK that waits `UnwrapLatencyMs` (0 or 10) before every unwrap, standing in for a remote KMS. `Batched` is the shipped store (16 unwraps at a time); `OneAtATime` is the same store with concurrency limited to one (it does not reproduce the exact path before [story 16.3](guide/crypto-shredding.md#key-custody-the-property-is-only-as-true-as-this)) |
| `ConfidenceBenchmarks` | `ExperienceLifecycleService.ApplyEvidenceAsync`: supporting machine evidence from a run not seen before, so every call is counted and committed | The service trusts host-supplied identifiers. The default, verifying mode's run lookup is not timed |

Every class has `[MemoryDiagnoser]`. Each class's setup runs its operation once and fails the case if it did not do
what the case claims to time: retrieval completed and truncated, 8 records injected, a durable validated record, a
counted evidence application. A mean can therefore never be the cost of a timeout or a fallback. The stores connect
as the container's superuser; a production two-role deployment runs the same statements.

## Baseline

One run of the command above, with `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`, on 2026-09-30 at
commit `d4b5b18` plus this change:

| | |
| --- | --- |
| Machine | Apple M3 Pro, 11 cores, 18 GB |
| OS | macOS Tahoe 26.6.2 (25G83) |
| Runtime | .NET 10.0.10 (Arm64 RyuJIT), SDK 10.0.302, BenchmarkDotNet 0.15.8 |
| PostgreSQL | 16.15 (`pgvector/pgvector:pg16`), in Rancher Desktop's Docker VM (Alpine Linux, 2 CPUs, 4.8 GB) on the same machine |
| Job | In process, 1 launch, 3 warmup and 10 measured iterations of about 250 ms. Finalization: 4,096 operations per iteration |

Mean and P95 are per operation. "Allocated" is managed memory allocated in this process per operation; it does not
include what PostgreSQL allocates.

### Retrieval

| Records | Store | Variant | Mean | P95 | Allocated |
| ---: | --- | --- | ---: | ---: | ---: |
| 1,000 | InMemory | TextOnly | 109.0 μs | 109.4 μs | 170.53 KB |
| 1,000 | InMemory | PreferredEnvironment | 112.7 μs | 113.2 μs | 173.26 KB |
| 1,000 | InMemory | ConfidenceDecay | 114.4 μs | 114.8 μs | 170.53 KB |
| 1,000 | Postgres | TextOnly | 2,942.5 μs | 3,250.6 μs | 943.81 KB |
| 1,000 | Postgres | PreferredEnvironment | 2,921.3 μs | 3,194.4 μs | 946.57 KB |
| 1,000 | Postgres | ConfidenceDecay | 2,790.8 μs | 3,234.0 μs | 944.17 KB |
| 10,000 | InMemory | TextOnly | 1,300.7 μs | 1,384.2 μs | 1341.35 KB |
| 10,000 | InMemory | PreferredEnvironment | 1,273.8 μs | 1,302.6 μs | 1344.09 KB |
| 10,000 | InMemory | ConfidenceDecay | 1,330.7 μs | 1,489.7 μs | 1341.35 KB |
| 10,000 | Postgres | TextOnly | 7,060.8 μs | 7,230.3 μs | 944.18 KB |
| 10,000 | Postgres | PreferredEnvironment | 5,465.3 μs | 5,660.0 μs | 947.12 KB |
| 10,000 | Postgres | ConfidenceDecay | 5,335.6 μs | 5,684.5 μs | 944.51 KB |

### Injection (8-record block)

| Store | Variant | Mean | P95 | Allocated |
| --- | --- | ---: | ---: | ---: |
| InMemory | WithoutSessionTracking | 173.7 μs | 174.1 μs | 338.66 KB |
| InMemory | WithSessionTracking | 177.5 μs | 178.6 μs | 345.73 KB |
| Postgres | WithoutSessionTracking | 3,532.4 μs | 3,972.3 μs | 1248.01 KB |
| Postgres | WithSessionTracking | 3,560.8 μs | 3,866.6 μs | 1255.34 KB |

### Finalization and confidence

| Operation | Store | Mean | P95 | Allocated |
| --- | --- | ---: | ---: | ---: |
| Finalize | InMemory | 18.92 μs | 21.08 μs | 29.86 KB |
| Finalize | Postgres | 2,008.64 μs | 2,033.69 μs | 56.66 KB |
| ApplyEvidence | InMemory | 2.665 μs | 2.789 μs | 3.42 KB |
| ApplyEvidence | Postgres | 2,844.381 μs | 2,926.919 μs | 65.31 KB |

### What the baseline says, and what it does not

- **The 500 ms retrieval timeout has a wide margin here.** The slowest retrieval, 10,000 records on PostgreSQL, took
  about 7 ms. That is one scope on a local database with no concurrent load, not a statement about a production
  server.
- **The preferred-environment scorer and decay cost at most a few percent**: 3% and 5% over 1,000 records in memory,
  where nothing else hides them, and within the noise over 10,000 records and on PostgreSQL. On PostgreSQL the
  10,000-record `TextOnly` row, which runs before the two variants, is slower than both; neither variant does less
  work than it, so read that as run-to-run noise, not as the variants being faster.
- **Session tracking adds about 2% to an injection** and about 7 KB of allocation. This is for a first invocation;
  later invocations of one session also re-read the records the session already holds, which is not measured.
- **The batched re-read keeps injection close to retrieval.** On PostgreSQL an 8-record block costs about 0.6 ms more
  than the retrieval inside it (compare the 1,000-record rows), including the one `GetManyAsync` round trip.
- **In memory, the store costs next to nothing**, so those rows are the library's own CPU and allocation cost, and the
  best rows to watch for a regression in Core or the adapter.

## Encrypted retrieval (story 16.3)

`EncryptedRetrievalBenchmarks` on the baseline machine above on 2026-10-05 (mean ± error, StdDev):

| Unwrap latency | Batched | OneAtATime |
| ---: | ---: | ---: |
| 0 ms | 5.146 ± 1.862 ms (StdDev 1.232) | 4.312 ± 0.245 ms (StdDev 0.162) |
| 10 ms | 62.669 ± 6.231 ms (StdDev 4.121) | 610.405 ± 8.341 ms (StdDev 5.517) |

- **With a 10 ms unwrap, one at a time exceeds the 500 ms retrieval timeout**: 610.4 ms, 9.8 times the batched 62.7
  ms. Under the default policy that retrieval would time out and inject nothing.
- **With no unwrap latency, batching costs a small overhead**: 5.1 ms against 4.3 ms, within the batched row's wide
  error. Allocation is the same (2.8 MB).
- `OneAtATime` is the shipped store with `maxConcurrentKeyLookups: 1`. It limits concurrency to one but still reads the
  rows first and makes one `GetKeysAsync` call with the connection released; it does not reproduce the exact pre-16.3
  path, which looked each key up while the reader held its connection.

## Row-level security (story 15.1)

`RetrievalBenchmarks.TextOnly` on PostgreSQL, four ways, on the baseline machine above on 2026-09-30, each run twice
with the filter `--filter '*RetrievalBenchmarks.TextOnly*'`:

- **before**: commit `a2778eb`, the parent of story 15.1, the stores connecting as the superuser;
- **wrapped**: this change, as the superuser. Every operation now runs in a transaction that first declares its
  authorization bounds, so this is the cost of the wrapping and the declaration alone;
- **RLS off** and **RLS on**: this change, with `AGENTEXPERIENCE_BENCHMARK_RLS` set to `off` or `on`, which makes each
  dataset the two-role deployment instead: an owner migrates and analyzes, and the stores connect as an application
  role given the manifest by `ApplyApplicationRolePrivilegesAsync` with `EnableRowLevelSecurity` set to that value.

| Records | before | wrapped | RLS off | RLS on |
| ---: | ---: | ---: | ---: | ---: |
| 1,000 | 2,400 / 2,232 μs | 3,005 / 3,286 μs | 3,418 / 3,141 μs | 3,990 / 4,009 μs |
| 10,000 | 6,635 / 6,636 μs | 7,370 / 7,367 μs | 7,482 / 7,524 μs | 8,507 / 8,676 μs |

Means of the two runs, per retrieval, with the final policies. Allocation is unchanged within 1% in every column (983
to 989 KB), and the in-memory rows did not move.

- **The transaction and the declaration cost about 0.7 ms per operation** with row-level security off: two more round
  trips (the `set_config` statement and the `COMMIT`) to a database in a Docker VM. It is paid whether or not
  row-level security is on. Running as the application role instead of the superuser costs nothing measurable.
- **Row-level security costs about another 1 ms per text search** on this dataset: 15% on 10,000 records, about 25% on
  1,000. This dataset's query matches a tenth of one tenant's records, so the search is a sequential scan with or
  without the policies, and the difference is the policies' quals and planning.

### The plans behind it: text search loses its full-text index

`EXPLAIN (ANALYZE, BUFFERS)` of the exact statements the text and vector channels send, as the application role with
tenant `t1`'s bounds declared, over 20,000 records (10,000 in each of two tenants), each with an 8-dimension embedding
and an HNSW index, on PostgreSQL 16. The text query matches 10 of `t1`'s records (and 10 of `t2`'s).

| Search | RLS off | RLS on |
| --- | --- | --- |
| Text (`@@`, 10 of 10,000 match) | `BitmapOr` of the two GIN indexes, 20 heap rows; plan 0.4 ms, execution 2.1 ms | `Bitmap Index Scan` on `ix_experience_records_scope` (`tenant_id = $0`), 10,000 heap rows filtered to 10; plan 0.7 ms, execution 5.5 ms |
| Vector (HNSW, limit 50) | HNSW index scan, 80 rows; plan 0.5 ms, execution 0.8 ms | HNSW index scan, the policy's quals as a filter; plan 2.3 ms, execution 1.0 ms |

**With row-level security on, text search does not use its GIN indexes.** PostgreSQL evaluates a query's own
conditions after a policy's unless they are *leakproof*, and the full-text match operator (`@@`, `ts_match_vq`) is not
marked leakproof, so it cannot become an index condition beneath the policy. The planner instead uses the policy's own
`tenant_id` condition on the existing scope index and applies the match to every live record in the declared tenant:
the search is linear in the tenant's size rather than in the number of matches. The policy's condition is what keeps it
to one tenant — without it, the scan would be the whole table. The vector channel keeps its HNSW index; its extra cost
is planning. This is recorded as part of [KL-17](known-limits.md#documented-boundaries). What bounds it is the policy's
tenant condition: a text search never reads another tenant's rows. What a deployment with large tenants can do is leave
row-level security off, or keep it off until it has measured its own tenants. Marking `ts_match_vq` leakproof would
restore the index, but that is a superuser's decision about a built-in function and not one this library makes.

**Since story 17.7 the text channel keeps its GIN indexes with row-level security on.** While row-level security is
enabled, it searches through `agent_experience.search_experience_text` (`0024`), a `SECURITY DEFINER` function that runs
the same statement as the owner, whom the policies do not bind, while applying the read policy's own admission itself
(see [Enabling row-level security](guide/deployment.md#enabling-row-level-security)). The plan is again the GIN indexes'
`Bitmap Index Scan`, which `PostgresTextSearchRowLevelSecurityTests` asserts on through `auto_explain` over a 20,000-record
tenant. The table and the plans above are story 15.1's measurements and were not re-run; the paragraph above describes
the store before story 17.7, and `ts_match_vq` is still not marked leakproof.

## Retrieval quality (story 20.1)

The benchmarks above time retrieval; `tests/AgentExperience.RetrievalQuality` measures whether it finds the right
lesson at all. It is a test project, not a BenchmarkDotNet run: no model, no network, no credentials, and its output is
deterministic, so CI checks it on every build.

**What it measures.** A checked-in corpus (`Corpus/records.json`) of 53 experience records: 30 in five task families
(EF Core migrations, flaky tests, Docker, NuGet, authentication), 16 distractors that share two or three words with a
family but answer none of its tasks, and 7 records no search may return (another project's scope, `Candidate` or
`Quarantined` status, confidence below the floor). 48 queries (`Corpus/queries.json`), each labelled with a category and
the records it should find: `exact` (a record's own words), `paraphrase`, `realistic` (long requests worded the way a
developer types them to an agent), `morphology` (plural and verb forms), `distractor-overlap` and `stopword-heavy`. The
corpus was written before the first run and is not tuned to its results.

Each query goes straight to `IExperienceCandidateSource.SearchAsync` (limit 50, confidence floor 0.5, the reusable
statuses), so the numbers are lexical recall with no clock, environment or ranking policy mixed in. Per adapter,
overall and per category, the report gives recall@1/3/8 and mean reciprocal rank (over the queries that expect a
record), precision@3/8 (divided by k, over every query), the share of queries that got no candidate at all, every miss
and every false positive in the first 8. k = 8 is what injection shows an agent by default.

**Running it.**

```bash
dotnet test tests/AgentExperience.RetrievalQuality                                         # both adapters; Docker
dotnet test tests/AgentExperience.RetrievalQuality --filter "FullyQualifiedName!~Postgres" # in-memory only
```

Each adapter's report is compared byte for byte with its golden, `GoldenInMemoryReport.txt` and
`GoldenPostgresReport.txt`. A change to which records a query finds, or in what order, fails the test until the golden
is regenerated: run the tests with `AGENTEXPERIENCE_RETRIEVALQUALITY_GOLDEN_UPDATE=1`, read the diff, and commit it with
the change. The diff is the change, query by query. The tests also fail if any search returns an ineligible record, if
two runs differ, or if an `exact` query's record is not in the first three.

**Today's numbers** (0.1.0-preview.8 behaviour, recorded 2026-10-10):

| Adapter | Recall@3 | Recall@8 | MRR | Zero candidates | `realistic` zero | `morphology` recall@8 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| In-memory | 0.378 | 0.378 | 0.356 | 0.646 | 1.000 | 0.000 |
| PostgreSQL | 0.511 | 0.511 | 0.500 | 0.521 | 1.000 | 0.857 |

- **Every realistic request finds nothing, on both adapters.** Both require every query term to match (the in-memory
  source's whole-word AND, PostgreSQL's `websearch_to_tsquery`), and since story 18.3 the query is the user's latest
  message. A long request always carries words no lesson contains. Every paraphrase finds nothing for the same reason.
- **Exact keywords work**: every `exact` query has its record first, on both adapters.
- **Stemming is the whole difference between the adapters.** PostgreSQL finds 6 of the 7 morphology queries and the
  in-memory source none; the one PostgreSQL misses asks for "failures", which the English stemmer does not reduce to
  the record's "fail".
- **Precision is low because recall is**, not because of noise: across all 48 queries there are two false positives
  in the first 8 in memory and three on PostgreSQL. Story 20.2's move away from AND matching will show its precision
  cost here.
