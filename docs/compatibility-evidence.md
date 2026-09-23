# Compatibility evidence for every pin

Release verification (`RELEASING.md`, step 5) requires that every exact SDK, MAF, storage, and telemetry pin has
source-backed compatibility evidence. This is that evidence, in one table a reader can check without trusting us:
each row names where the pin is declared, the public source that says the version exists and what it is, the
content hash NuGet restored (from the committed `packages.lock.json`, so a substituted package would not match), and
the executable test that proves this repository works against it.

It is the successor to the Story 1.7 research digest, which lived in a directory excluded from git. Anything a
reader needs to check a pin is here.

**Last verified: 2026-09-23**, against nuget.org's registration API and a full local run of the suite. Re-verify —
and update the date — whenever a pin moves.

## Supported matrix

| Dimension | Supported | Everything else |
| --- | --- | --- |
| Target framework | `net10.0` | Not built, not tested. There is no multi-targeting |
| .NET SDK | `10.0.302` for a release build (see below) | Development may roll forward to a later feature band; a release may not |
| Microsoft Agent Framework | `Microsoft.Agents.AI` **1.20.0** exactly | Newer versions are *probed* by CI and reported, never claimed (see [the MAF matrix](#the-maf-compatibility-matrix)) |
| PostgreSQL | Major version **16** (`pgvector/pgvector:pg16`, and stock `postgres:16` for the text-only schema) | Other majors are untested. The image tags float within 16, so the minor version is whatever the tag resolved to on the day |
| pgvector | Whatever `pgvector/pgvector:pg16` ships, via `Pgvector` 0.3.2 | — |

## The pins

Hashes are NuGet's SHA-512 `contentHash`, exactly as recorded in the lock files. "Published" is nuget.org's
registration timestamp for that version.

### SDK

| Pin | Declared in | Source | Executable evidence |
| --- | --- | --- | --- |
| .NET SDK **10.0.302**, `rollForward: latestFeature` | `global.json` | <https://dotnet.microsoft.com/download/dotnet/10.0> | CI installs this version with `actions/setup-dotnet` (`global-json-file: global.json`), and every CI job that builds then asserts `dotnet --version` equals the `global.json` version, failing if a newer feature band on the runner won the roll-forward. `RELEASING.md` step 1 makes the same equality check before a release build and fails with a message if it does not hold |

`rollForward: latestFeature` makes the SDK pin a *floor* for day-to-day local development — a contributor with a
later 10.0 feature band can still build. CI and a release build are held to exactly `10.0.302` by the two equality
checks above; nothing else enforces it.

### Microsoft Agent Framework

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.Agents.AI` **[1.20.0]** | `src/AgentExperience.MicrosoftAgentFramework` | 2026-08-31 | `/nNbNNistrhtdpaqj4u83gbBjnU88nKNLtUs7e5vpyGXnsdh22mDd/FAvYO/T47FTMbRiTOXPWmcVXhGEry68Q==` | <https://www.nuget.org/packages/Microsoft.Agents.AI/1.20.0>, source at tag [`dotnet-1.20.0`](https://github.com/microsoft/agent-framework/tree/dotnet-1.20.0/dotnet/src) | `CompatibilityProof/MafHooksProof` (invocation hooks, failure visibility through `InvokedCoreAsync`), `CompatibilityProof/ContextProviderFitProof` (custom `AIContextProvider` injection), and the whole `AgentExperience.MicrosoftAgentFramework.Tests` suite driving real `ChatClientAgent` runs |

Two facts from MAF's own source that shaped the adapter, and are still true at 1.20.0:

- `AIContextProvider`'s default `InvokedCoreAsync` returns **before** calling `StoreAIContextAsync` when
  `InvokedContext.InvokeException` is set, so a simple-tier provider never sees a failed invocation. That is why
  capture does not live in a context provider at all: `UseExperienceCapture` registers agent-run middleware
  (`AIAgentBuilder.Use(runFunc, runStreamingFunc)`) and function-invocation middleware, which see every outcome,
  and the context provider is used only for injection (`ProvideAIContextAsync`), where success is the only case
  that matters. Source: `dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs` at the tag above;
  proven by `MafHooksProof` and by `ExperienceCaptureTests`' failed and cancelled runs.
- Parts of that surface are still marked `[Experimental]` (`MAAI001`) at 1.20.0. The adapter uses none of them —
  it builds with warnings as errors and no `MAAI001` suppression anywhere under `src/` — but the neighbouring
  surface is still settling, which is one reason the pin is exact.

### Storage

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `Npgsql` **[10.0.3]** | `Storage.Postgres`, `Storage.Postgres.Vectors` | 2026-05-27 | `7nb5YzXuvWWJxB0J8DiyL3we+X4FOctZrt0fIBnucOIaIevFEEwGQVZKtiu9olXdlNAK1eNgqSral6r/jlhI4w==` | <https://www.nuget.org/packages/Npgsql/10.0.3> | Every container-backed test in `AgentExperience.Storage.Postgres.Tests` and `…Vectors.Tests`; `CompatibilityProof/PostgresVectorProof` |
| `dbup-postgresql` **[7.0.1]** | `Storage.Postgres` | 2026-02-23 | `mRnmENWWPuuMZ538gOd1mZnzucx6FQk0anmw3EABjGfcbp24FDb9QdGepYrDiaM8K9s5/gd49+5cmBOlniH/lg==` | <https://www.nuget.org/packages/dbup-postgresql/7.0.1> | `ExperienceSchemaMigratorTests` (journal, one transaction per script, advisory lock, failing script), `PlainPostgresMigrationTests`, `MigratorLogSilenceTests` |
| `dbup-core` **[6.1.1]** | `Storage.Postgres` (pinned directly, in lockstep with `dbup-postgresql`) | 2026-02-23 | `kgpuyJVEFJHoIj/slnc994Go88aoeZqNDfGHDBr4sh7CsEWwJhOTCt/FJqO4ziUImL5L0NEY0kxxOiNgPKI2Fw==` | <https://www.nuget.org/packages/dbup-core/6.1.1> | As above. `MigratorLogSilenceTests` also records that this version's engine builder starts with no logger |
| `Pgvector` **[0.3.2]** | `Storage.Postgres.Vectors` | 2025-05-20 | `n7M5LuNejHUmtWky3zCbNO+tP1Gnjiuv9Qtu4LyvB1602dD8RiBxxCQp9jEjM0ZFDxAZF1oOWkNIkXw46KT00Q==` | <https://www.nuget.org/packages/Pgvector/0.3.2> (sole dependency `Npgsql >= 8.0.5`, satisfied by 10.0.3) | `PostgresEmbeddingIndexTests`, `HybridRetrievalIntegrationTests`, `PostgresDeindexingTests`, `CompatibilityProof/PostgresVectorProof` |
| `Microsoft.Extensions.AI.Abstractions` **[10.9.0]** | `Storage.Postgres.Vectors` | 2026-08-11 | `//nASHMCJVxnYfE/WSzfaLOao6/q816kPpgB9rxU0gfSmAny1u3rfQT0D4xAmcIo4yQqJs7rAeBB+M/dIMdZYA==` | <https://www.nuget.org/packages/Microsoft.Extensions.AI.Abstractions/10.9.0> | `OfflineVectorsTests` drives `AiExperienceEmbeddingGenerator` over an in-test `IEmbeddingGenerator`, with no model credentials. The same version arrives transitively through MAF 1.20.0, and the release tests assert the two resolve identically |

### Core and shared

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.Extensions.DependencyInjection.Abstractions` **[10.0.11]** | `Core`, `Storage.Postgres`, `Storage.Postgres.Vectors` | 2026-08-11 | `/a1aJz4m7ylhEDf25ugQChLQoN5XwoGjWw/BoR/ZWWKsO1v4DdJElS1uyngahz4B/eOzjFk1KNTkarRLE5wsIg==` | <https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.11> | `CoreServiceRegistrationTests`, `PostgresServiceRegistrationTests`, and the vectors registrations, each resolved from a real container |
| `Microsoft.Extensions.Compliance.Redaction` **10.9.0** — a **floor**, not an exact pin | `Core` | 2026-08-11 | `2P0WFFq9WAyhOAZqb0FjTeKW86yL4M2vymSGyuBu5XEBWwDCiEI+BoR4TuAFtyFurDRZs5wG3JtysUy8Svlnmw==` | <https://www.nuget.org/packages/Microsoft.Extensions.Compliance.Redaction/10.9.0> | `DefaultSanitizerTests`, `SanitizerConformanceTests`, `CompatibilityProof/EvaluationRedactionProof`. The lock file resolves exactly 10.9.0 today; a consumer may resolve a later one. Listed in the root README's Known limits |

### Telemetry

| Pin | Declared in | Source | Executable evidence |
| --- | --- | --- | --- |
| **No telemetry package.** `ActivitySource` and `Meter` from `System.Diagnostics.DiagnosticSource`, which ships in the `net10.0` shared framework | Nothing to declare: it is part of the target framework the SDK pin selects | <https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs>, <https://learn.microsoft.com/dotnet/core/diagnostics/metrics-instrumentation> | `ExperienceTelemetryTests`, `TelemetrySourceScanTests`, `InjectionTelemetryTests`, `DiagnosticsAgreementTests` subscribe with the BCL's own `ActivityListener` and `MeterListener`, exactly as an OpenTelemetry exporter would. The dependency-boundary tests forbid `OpenTelemetry` in Abstractions and Core, and `eng/verify-packages.cs` forbids it in their built nuspecs |

A host exports with OpenTelemetry's `AddSource("AgentExperience.*")` and `AddMeter("AgentExperience.*")`; which
OpenTelemetry version it uses is the host's choice, because this library references none.

### Test infrastructure (not shipped)

| Pin | Where | Why it matters |
| --- | --- | --- |
| `Testcontainers.PostgreSql` 4.15.0 | Postgres, vectors, sample and proof test projects | Starts the PostgreSQL 16 containers every storage claim above rests on |
| `pgvector/pgvector:pg16`, `postgres:16` | Test fixtures | The PostgreSQL major version this library supports |
| `PublicApiGenerator` 11.5.4, `Verify.Xunit` 31.12.5 | `AgentExperience.Release.Tests` only | The public API baseline. No shipping project references either |

## The compatibility proof must resolve what ships

`tests/AgentExperience.CompatibilityProof` is the executable evidence for the MAF, storage, and redaction pins, so it
exact-pins the same versions. `AgentExperience.Release.Tests/Compatibility/CompatibilityPinAgreementTests` fails if
they ever disagree — checked against both the declared csproj versions and the resolved lock files — or if the proof
stops referencing a pin it is evidence for.

## The MAF compatibility matrix

CI's `maf-compatibility` job runs the MAF adapter's tests and the MAF proofs twice:

| Leg | Microsoft.Agents.AI | Gates the build? |
| --- | --- | --- |
| `pinned` | 1.20.0, as shipped | **Yes.** A failure fails CI |
| `latest` | The newest stable version on nuget.org at run time | **No.** It reports a visible pass or fail in the job summary and never blocks, because a gate that failed whenever Microsoft published a release would make this repository's health a function of someone else's release calendar (story 4.3, AD-F) |

A passing `latest` leg is information, not a support claim. Moving the pin is a deliberate change: update the
csproj files, this document, and the README, and let the pinned leg prove it.

Run it locally with `eng/probe-maf-version.sh` (newest stable), `eng/probe-maf-version.sh 1.21.0` (a specific
version), or `eng/probe-maf-version.sh pinned`. It works on a throwaway copy of the tracked files (HEAD plus
uncommitted changes to them) and never edits your tree; untracked files are not copied, and it warns when `src/` or
`tests/` has any. The pinned leg restores the committed lock files exactly (`--locked-mode`); any other version has
to re-evaluate the graph (`--force-evaluate`), because re-pinning changes what the projects ask for.

**Result on 2026-09-23:** the newest stable version was **1.22.0**, and the probe reported **FAILED (dependencies do
not resolve)** before any test ran: MAF 1.22.0 requires `Microsoft.Extensions.DependencyInjection.Abstractions`
≥ 10.0.12 and `Microsoft.Extensions.AI.Evaluation`/`VectorData.Abstractions` ≥ 10.10.0, and Core's exact
`[10.0.11]` pin refuses the first. That is the probe doing its job — the result is visible, the build is not
blocked, and it is recorded as KL-14 in the root README's Known limits. The pinned leg (1.20.0) passed: 218 adapter
tests and 6 MAF proofs.
