# Compatibility evidence for every pin

Release verification (`RELEASING.md`, step 5) requires that every SDK, MAF, storage, and telemetry pin — the one exact
pin and every floor — has source-backed compatibility evidence. This is that evidence, in one table a reader can check
without trusting us: each row names where the version is declared, the public source that says the version exists and
what it is, the content hash NuGet restored (from the committed `packages.lock.json`, so a substituted package would
not match), and the executable test that proves this repository works against it.

It is the successor to the Story 1.7 research digest, which lived in a directory excluded from git. Anything a
reader needs to check a pin is here.

**Last verified: 2026-09-25**, against nuget.org's registration API and a full local run of the suite on both target
frameworks and all four PostgreSQL majors, plus the floating-dependency leg. Re-verify — and update the date — whenever
a pin or a floor moves.

## Supported matrix

Everything in this table is built and tested by CI on every change (story 6.3). A support matrix is always finite;
each "everything else" cell says why the line is where it is.

| Dimension | Supported | Everything else |
| --- | --- | --- |
| Target framework | `net9.0` and `net10.0`. All five packages multi-target both, and every test project that exercises them runs on both (the sample, its tests and the reuse baseline, which are demonstrations, run on `net10.0` only) | `net8.0` cannot be built without a new package reference or a per-framework rewrite of the payload decoder (see [Target frameworks](#target-frameworks)). .NET Framework and `netstandard2.0` are ruled out by `Npgsql` 10, which ships `net8.0`+ only, and `Microsoft.Extensions.Compliance.Redaction`, which ships no `netstandard2.0` build |
| .NET SDK | `10.0.302` for a release build (see below) | Development may roll forward to a later feature band; a release may not |
| Microsoft Agent Framework | `Microsoft.Agents.AI` **1.22.0** exactly | Newer versions are *probed* by CI and reported, never claimed (see [the MAF matrix](#the-maf-compatibility-matrix)). The one exact pin left; [the version policy](#the-version-policy-floors-and-one-exact-pin) says why |
| Everything else the packages reference | The floor in each row below, **or any later release in the same major** (the same minor for the 0.x `Pgvector`) | A later major restores (the packages declare no upper bound, so it is never a restore conflict) but is untested until a floor moves. See [the version policy](#the-version-policy-floors-and-one-exact-pin) |
| PostgreSQL | Majors **15, 16, 17 and 18**: `pgvector/pgvector:pg15`, `pgvector/pgvector:pg16`, `pgvector/pgvector:pg17`, `pgvector/pgvector:pg18`, and stock `postgres:15`–`postgres:18` for the text-only schema. 16 is the default for a local run | 14 is still supported upstream (until November 2026), but migration `0005` is PostgreSQL 15 syntax and fails there (see [the PostgreSQL matrix](#the-postgresql-matrix)). 13 and older are past end of life. The image tags float within each major, so the minor version is whatever the tag resolved to on the day |
| pgvector | Whatever each `pgvector/pgvector:pg{N}` image ships (0.8.6 in all four on 2026-09-25), via `Pgvector` 0.3.2 or later within 0.3.x | — |

## The version policy: floors, and one exact pin

Story 6.3 (KL-13) replaced the exact-pin policy. Before it, every shipping `PackageReference` was exact, so a host
whose graph needed a newer `Microsoft.Extensions.*` package, `Npgsql`, or a MAF that brought newer ones, got a restore
conflict until a new preview moved the pins. Now:

| Kind | Declared as | What CI proves | Which packages |
| --- | --- | --- | --- |
| **Floor** | `Version="x.y.z"` — NuGet's `>= x.y.z`, with no upper bound | The floor itself, on every run: the committed lock files resolve exactly the floor, and `CompatibilityPinAgreementTests` fails if they ever resolve anything else. **And** the newest release in the same major (the same minor for a 0.x package), on every run, in the `floating-dependencies` job | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Compliance.Redaction`, `Microsoft.Extensions.AI.Abstractions`, `Npgsql`, `dbup-postgresql`, `dbup-core`, `Pgvector` |
| **Exact** | `Version="[x.y.z]"` | That version, on every run; newer ones are probed and reported, never claimed | `Microsoft.Agents.AI` only |

**What a floor claims.** Every release from the floor up to, but not including, the next major (for `Pgvector`, the
next minor: a 0.x version may break in a minor). Both ends of that range run the full suite on the default PostgreSQL major on every change (the floating end minus the `DeclaredPins` checks, which assert the floor itself); the
releases in between are covered by the package's own semantic-versioning promise, not by a run. A later major is not
claimed, but it is not refused either.

**Why no upper bound.** An upper bound would put the claim in the package metadata, but NuGet reports a host that
resolves past it with NU1608, and a host that builds with warnings as errors — as this repository does — cannot restore
past that warning. That is the conflict KL-13 described, moved one major later. A bare floor is what the
`Microsoft.Extensions.*` packages themselves declare for their own dependencies.

**Why the floating leg gates main, but not pull requests.** The MAF probe's `latest` leg never blocks, because the
pinned MAF makes no claim about the newest one (AD-F). A floor does make that claim: NuGet resolves the floor by
default, but any host whose own references or other dependencies ask for more gets more, up to the newest release in
the major, with no warning. So if that release breaks the library, those hosts are already broken, and `main` must go
red rather than report. The job therefore gates on every push and on the weekly schedule, so a new release is noticed
within a week even when nothing here changes. On a pull request it runs the same way and reports PASSED or FAILED in
the job summary, but is `continue-on-error`: `Microsoft.Extensions.*` ships monthly, and an unrelated pull request
should not go red the morning a release lands — the same reasoning AD-F applies to the MAF probe. A red floating leg
on a pull request is still read, not ignored.

**Triage when it fails.** Read the resolved-versions table in the job summary to see which float moved. Then either:
1. **Fix the library**, with a change that works at both ends of the range (the floor and the newest release), or
2. **Raise the floor** past the broken release (update the shipping csproj, the proof's exact pin and the lock files
   together; `CompatibilityPinAgreementTests` fails until they agree), and record the new floor here.

Never cap the range with an upper bound instead: that is the NU1608 conflict KL-13 describes.

**Why MAF stays exact.** `Microsoft.Agents.AI` ships a minor release every week or two (eight, 1.15.0 to 1.22.0, between
22 July and 18 September 2026),
has changed adapter-visible behaviour between minors (the `ChatClientAgentRunOptions` clone at 1.22.0, below), and
still marks surface next to the hooks the adapter depends on `[Experimental]`. The adapter's correctness rests on
exactly when those hooks fire (see `InvokedCoreAsync` below). A floor would claim every future 1.x on the strength of
one run, and a gating floating leg would go red on Microsoft's release calendar, which is what AD-F refuses. So MAF
stays exact, the probe keeps reporting the newest version, and moving the pin stays a deliberate change. What changed
is that the pin no longer drags the shared packages with it: with them on floors, a newer MAF that raises a shared
`Microsoft.Extensions.*` floor restores beside Core and the stores (the failure mode of KL-14 is gone), and a host that
references a newer MAF directly gets NuGet's NU1608 warning about this adapter's exact pin, not a resolution failure —
unless it builds with warnings as errors, which is what the residual KL-13 row says.

**How it is enforced.** `CompatibilityPinAgreementTests` fails if a shipping reference is anything but a bare floor
(or, for MAF, exact); if a floor is not exactly what the committed lock files resolve, in every target framework; if
two shipping projects declare different versions of one package; or if the compatibility proof stops pinning each
proven package exactly at the shipping floor. The dependency-boundary tests pin each package's declared set, and
`eng/verify-packages.cs` checks the built nuspecs declare exactly those floors in every framework's dependency group.

## The pins

Hashes are NuGet's SHA-512 `contentHash`, exactly as recorded in the lock files (identical in the `net9.0` and
`net10.0` sections, since it is one package). "Published" is nuget.org's registration timestamp for that version.
A bold `[x.y.z]` is an exact pin; a bold `x.y.z+` is a floor, and its row is the evidence for the floor itself — the
newest release in its major is proven by [the floating-dependency leg](#the-floating-dependency-leg).

### SDK

| Pin | Declared in | Source | Executable evidence |
| --- | --- | --- | --- |
| .NET SDK **10.0.302**, `rollForward: latestFeature` | `global.json` | <https://dotnet.microsoft.com/download/dotnet/10.0> | Every CI job that builds first sets `global.json`'s `rollForward` to `disable` in the runner's working copy, so `actions/setup-dotnet` (`global-json-file: global.json`) installs, and the host selects, exactly this version even though the runner image ships newer bands; each such job that builds then asserts `dotnet --version` equals the `global.json` version, failing if a newer feature band on the runner won the roll-forward. `RELEASING.md` step 1 makes the same equality check before a release build and fails with a message if it does not hold |

`rollForward: latestFeature` makes the SDK pin a *floor* for day-to-day local development — a contributor with a
later 10.0 feature band can still build. CI and a release build are held to exactly `10.0.302` by the two equality
checks above; nothing else enforces it.

### Microsoft Agent Framework

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.Agents.AI` **[1.22.0]** | `src/AgentExperience.MicrosoftAgentFramework` | 2026-09-18 | `YdQJbL47n8PJmTGCD6DzapD2TQiCfI1f/669ECcHsKw1po3WhTq2lZoO3X7PJ6sZSGE/I2RReJ/hTGwtBZ7WTA==` | <https://www.nuget.org/packages/Microsoft.Agents.AI/1.22.0>, source at tag [`dotnet-1.22.0`](https://github.com/microsoft/agent-framework/tree/dotnet-1.22.0/dotnet/src). Requires `Microsoft.Extensions.DependencyInjection.Abstractions` ≥ 10.0.12 and the 10.10.0 `Microsoft.Extensions.AI`/`Compliance`/`VectorData`/`AI.Evaluation` train, which is why the pins below moved with it | `CompatibilityProof/MafHooksProof` (invocation hooks, failure visibility through `InvokedCoreAsync`), `CompatibilityProof/ContextProviderFitProof` (custom `AIContextProvider` injection), and the whole `AgentExperience.MicrosoftAgentFramework.Tests` suite driving real `ChatClientAgent` runs |

The pin moved from 1.20.0 to 1.22.0 in story 5.1 (1.21.0 was never pinned). Nothing in the adapter had to change;
one documented caveat went away (the third bullet below).

Facts from MAF's own source that shaped the adapter, re-checked at tag `dotnet-1.22.0` and still true:

- `AIContextProvider`'s default `InvokedCoreAsync` returns **before** calling `StoreAIContextAsync` when
  `InvokedContext.InvokeException` is set, so a simple-tier provider never sees a failed invocation. That is why
  capture does not live in a context provider at all: `UseExperienceCapture` registers agent-run middleware
  (`AIAgentBuilder.Use(runFunc, runStreamingFunc)`) and function-invocation middleware, which see every outcome,
  and the context provider is used only for injection (`ProvideAIContextAsync`), where success is the only case
  that matters. Source: `dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs` at the tag above;
  proven by `MafHooksProof` and by `ExperienceCaptureTests`' failed and cancelled runs.
- Parts of that surface are still marked `[Experimental]` (`MAAI001`) at 1.22.0: in `AIContextProvider.cs`, the
  `InvokingContext` and `InvokedContext` constructors, exactly as at 1.20.0. The adapter uses neither — it builds
  with warnings as errors and no `MAAI001` suppression anywhere under `src/`, and since story 5.1 the proofs carry
  none either — but the neighbouring surface is still settling, which is one reason the pin is exact.
- Function-invocation middleware (`FunctionInvocationDelegatingAgent`) now works on a per-run clone of a
  `ChatClientAgentRunOptions`. At 1.20.0 it wrote its `ChatClientFactory` onto the caller's instance, so a reused
  instance stacked middleware layers and the adapter README told hosts not to reuse one. At 1.22.0 sequential reuse
  is safe. The three `ExperienceCaptureTests.A_reused_ChatClientAgentRunOptions_instance_…` tests pin it on the
  streaming and non-streaming paths, with and without a host-set factory, so a future pin that brings the mutation
  back fails a test rather than silently changing behaviour. Concurrent sharing of one instance is not tested.
  Source: `dotnet/src/Microsoft.Agents.AI/FunctionInvocationDelegatingAgent.cs` at both tags.

### Storage

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `Npgsql` **10.0.3+** | `Storage.Postgres`, `Storage.Postgres.Vectors` | 2026-05-27 | `7nb5YzXuvWWJxB0J8DiyL3we+X4FOctZrt0fIBnucOIaIevFEEwGQVZKtiu9olXdlNAK1eNgqSral6r/jlhI4w==` | <https://www.nuget.org/packages/Npgsql/10.0.3> | Every container-backed test in `AgentExperience.Storage.Postgres.Tests` and `…Vectors.Tests`; `CompatibilityProof/PostgresVectorProof` |
| `dbup-postgresql` **7.0.1+** | `Storage.Postgres` | 2026-02-23 | `mRnmENWWPuuMZ538gOd1mZnzucx6FQk0anmw3EABjGfcbp24FDb9QdGepYrDiaM8K9s5/gd49+5cmBOlniH/lg==` | <https://www.nuget.org/packages/dbup-postgresql/7.0.1> | `ExperienceSchemaMigratorTests` (journal, one transaction per script, advisory lock, failing script), `PlainPostgresMigrationTests`, `MigratorLogSilenceTests` |
| `dbup-core` **6.1.1+** | `Storage.Postgres` (referenced directly, its floor moved in lockstep with `dbup-postgresql`) | 2026-02-23 | `kgpuyJVEFJHoIj/slnc994Go88aoeZqNDfGHDBr4sh7CsEWwJhOTCt/FJqO4ziUImL5L0NEY0kxxOiNgPKI2Fw==` | <https://www.nuget.org/packages/dbup-core/6.1.1> | As above. `MigratorLogSilenceTests` also records that this version's engine builder starts with no logger |
| `Pgvector` **0.3.2+** (within 0.3.x) | `Storage.Postgres.Vectors` | 2025-05-20 | `n7M5LuNejHUmtWky3zCbNO+tP1Gnjiuv9Qtu4LyvB1602dD8RiBxxCQp9jEjM0ZFDxAZF1oOWkNIkXw46KT00Q==` | <https://www.nuget.org/packages/Pgvector/0.3.2> (sole dependency `Npgsql >= 8.0.5`, satisfied by 10.0.3) | `PostgresEmbeddingIndexTests`, `HybridRetrievalIntegrationTests`, `PostgresDeindexingTests`, `CompatibilityProof/PostgresVectorProof` |
| `Microsoft.Extensions.AI.Abstractions` **10.10.0+** | `Storage.Postgres.Vectors` | 2026-09-09 | `frBHj2ckpevA+KdDLikxv8oxGPrJiyTvaiblYp+vtVEt0q6J73XP5m2SICm86nFK6rl+L1GCKxIy4S5Lb3p2kQ==` | <https://www.nuget.org/packages/Microsoft.Extensions.AI.Abstractions/10.10.0> | `OfflineVectorsTests` drives `AiExperienceEmbeddingGenerator` over an in-test `IEmbeddingGenerator`, with no model credentials. MAF 1.22.0 requires ≥ 10.10.0, so this floor moved with it in story 5.1. The same version arrives transitively through MAF 1.22.0, and the release tests assert the two resolve identically. The floating leg resolved **10.10.1** (published 2026-09-25) and passed |

### Core and shared

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.Extensions.DependencyInjection.Abstractions` **10.0.12+** | `Core`, `Storage.Postgres`, `Storage.Postgres.Vectors` | 2026-09-08 | `9/qymSh7hVDMGTGwrLz8MRp5zRyXy9adGDOs4HwRdnLil3oZGYuWeZjbmHgCQ9BL1qBroVfgUK3U/nb61617Cw==` | <https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.12> | `CoreServiceRegistrationTests`, `PostgresServiceRegistrationTests`, and the vectors registrations, each resolved from a real container. Moved from 10.0.11 in all three packages together, because MAF 1.22.0 requires ≥ 10.0.12 |
| `Microsoft.Extensions.Compliance.Redaction` **10.10.0+** | `Core` | 2026-09-09 | `RW0HuSIl5CH/SSLa31MIKc6LPmy3DjtJmf/5LjTTKMmwBeNex6ZnF1Q46vzklAPSQ2BDu/MHL/6G5CBoyWLQAA==` | <https://www.nuget.org/packages/Microsoft.Extensions.Compliance.Redaction/10.10.0> | `DefaultSanitizerTests`, `SanitizerConformanceTests`, `CompatibilityProof/EvaluationRedactionProof`. It was the floor `10.9.0` until story 5.1, then exact, then the floor `10.10.0` again since story 6.3 — this time with CI proving both ends of the range. 10.10.0 rather than 10.9.0 is a preference, not a requirement: Core does not reference MAF. But a host with Core and the MAF adapter resolves `Microsoft.Extensions.Compliance.Abstractions` 10.10.0 through MAF 1.22.0 either way, and Redaction 10.10.0 is the release built against that version, the same train as every other floor here. A host that needs a newer Redaction now simply gets it |

### Telemetry

| Pin | Declared in | Source | Executable evidence |
| --- | --- | --- | --- |
| **No telemetry package.** `ActivitySource` and `Meter` from `System.Diagnostics.DiagnosticSource`, which ships in the shared framework of both `net9.0` and `net10.0`. On `net9.0` a dependency may raise it to a newer `System.Diagnostics.DiagnosticSource` package transitively (MAF 1.22.0 brings 10.0.12); that is the dependency's reference, not one this library declares | Nothing to declare: it is part of the target framework the SDK pin selects | <https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs>, <https://learn.microsoft.com/dotnet/core/diagnostics/metrics-instrumentation> | `ExperienceTelemetryTests`, `TelemetrySourceScanTests`, `InjectionTelemetryTests`, `DiagnosticsAgreementTests` subscribe with the BCL's own `ActivityListener` and `MeterListener`, exactly as an OpenTelemetry exporter would. The dependency-boundary tests forbid `OpenTelemetry` in Abstractions and Core, and `eng/verify-packages.cs` forbids it in their built nuspecs |

A host exports with OpenTelemetry's `AddSource("AgentExperience.*")` and `AddMeter("AgentExperience.*")`; which
OpenTelemetry version it uses is the host's choice, because this library references none.

### Test infrastructure (not shipped)

| Pin | Where | Why it matters |
| --- | --- | --- |
| `Testcontainers.PostgreSql` 4.15.0 | Postgres, vectors, sample and proof test projects | Starts the PostgreSQL containers every storage claim above rests on |
| `pgvector/pgvector:pg{N}`, `postgres:{N}` for N in 15–18 | `tests/Shared/PostgresTestImage.cs`, linked into every container-backed test project | One variable, `AGENTEXPERIENCE_POSTGRES_MAJOR`, picks the major for both images (16 when unset; anything outside 15–18 fails loudly). Each suite asserts the server it reached reports that major |
| `PublicApiGenerator` 11.5.4, `Verify.Xunit` 31.12.5 | `AgentExperience.Release.Tests` only | The public API baseline. No shipping project references either |

## The compatibility proof must resolve what ships

`tests/AgentExperience.CompatibilityProof` is the executable evidence for the MAF, storage, and redaction versions, so
it exact-pins the same versions — the floor, for everything but MAF, so the proof always runs the bottom of what a
host can resolve. `AgentExperience.Release.Tests/Compatibility/CompatibilityPinAgreementTests` fails if they ever
disagree — checked against both the declared csproj versions and the resolved lock files, in every target framework —
or if the proof stops referencing a version it is evidence for. The floating-dependency leg floats the proof's
direct references together with the shipping ones. A package the proof only gets transitively (today
`Microsoft.Extensions.AI.Abstractions`, through MAF and the evaluation and vector-data packages) is not floated there.

## Target frameworks

`net9.0` and `net10.0`, set once as `AgentExperienceTargetFrameworks` in `Directory.Build.props`. Every shipping
package carries `lib/net9.0` and `lib/net10.0` and a dependency group for each, and `eng/verify-packages.cs` checks
both, and only both, in every package and symbol package. The public API baseline is **one file per assembly for
both frameworks**: `AgentExperience.Release.Tests` runs once per framework and compares each against the same
baseline, so the gate also asserts the public surface is identical on both. Warnings stay errors on both.

**Why not `net8.0`.** Every dependency ships a `net8.0` build, but the library does not compile against the `net8.0`
shared framework, whose `System.Text.Json` is 8.0. The first build on 2026-09-25 failed with exactly three errors:

- `src/AgentExperience.Core/Capture/InMemoryExperienceCaptureService.cs`: `JsonElement.DeepEquals` does not exist
  (added in .NET 9). Core has no `System.Text.Json` package to reach a newer one.
- `src/AgentExperience.Storage.Postgres/ExperiencePayload.cs`: `JsonSerializerOptions.RespectNullableAnnotations` and
  `RespectRequiredConstructorParameters` do not exist (added in .NET 9). They make the stored-payload decoder refuse a
  `null` where the type says non-null and a missing required constructor parameter — the strictness the store's
  payload reading depends on.

(The MAF adapter and the vectors package failed only because they reference those two.) There are two ways past it,
and both were rejected: a new `System.Text.Json` `PackageReference` in Core and the store, which changes Core's
reviewed dependency boundary (AD-1) for a framework that leaves support on 10 November 2026; or hand-written
equivalents of the two decoder settings for `net8.0` alone, which would give one framework a different, less-tested
payload decoder. .NET 9 leaves support on the same day, 10 November 2026 (STS releases now get 24 months); the
first preview after that date drops `net9.0`, and the matrix is then `net10.0` again until .NET 11 ships.

## The PostgreSQL matrix

CI's `postgres` job runs `AgentExperience.Storage.Postgres.Tests`, `…Vectors.Tests`, `AgentExperience.CompatibilityProof`
and `AgentExperience.Sample.EndToEnd.Tests` once per supported major (on both target frameworks, except the sample tests, which are `net10.0` only), with
`AGENTEXPERIENCE_POSTGRES_MAJOR` set to the leg's major. `WorkflowTests` fails if the job's list and
`PostgresTestImage.SupportedMajors` ever differ. The default `build-and-test` job runs everything against 16.

**Result on 2026-09-25** (local, `TESTCONTAINERS_RYUK_DISABLED=true`, both frameworks except the sample, images as
resolved that day). The 14 row was measured once, on `net10.0`, on an earlier revision of the suite, before the fixture
started refusing 14 and before the per-major server-version tests took their final shape, so its totals differ from
the other rows by a test or two:

| Major | Server (pgvector image) | pgvector | Postgres tests | Vectors tests | Proof | Sample |
| --- | --- | --- | --- | --- | --- | --- |
| 15 | 15.19 | 0.8.6 | 431 + 431 passed | 65 + 65 passed | 15 + 15 passed | 34 passed (net10.0) |
| 16 | 16.15 | 0.8.6 | 431 + 431 passed | 65 + 65 passed | 15 + 15 passed | 34 passed (net10.0) |
| 17 | 17.11 | 0.8.6 | 431 + 431 passed | 65 + 65 passed | 15 + 15 passed | 34 passed (net10.0) |
| 18 | 18.6 | 0.8.6 | 431 + 431 passed | 65 + 65 passed | 15 + 15 passed | 34 passed (net10.0) |
| 14 | 14.24 | 0.8.6 | **355 failed** of 432 (net10.0 only) | **47 failed** of 65 (net10.0 only) | 14 passed (net10.0 only) | **3 failed** |

**Why not 14.** Every PostgreSQL 14 failure is the same one: the schema migrator stops at
`0005_create_experience_grants`, whose unique index on active grants is declared `NULLS NOT DISTINCT` (`42601: syntax
error at or near "NULLS"`), which PostgreSQL added in 15. Everything that needs the migrated schema then fails; the
proof, which does not use the migrator, passes. Journaled scripts are never edited, and an equivalent PostgreSQL 14
index (an expression index coalescing the nullable columns) would be a second schema for one major that reaches end
of life on 12 November 2026. So 14 is left out, and `PostgresTestImage` refuses it.

## The floating-dependency leg

CI's `floating-dependencies` job runs `eng/probe-floating-dependencies.sh`, which, on a throwaway copy of the tracked
files, rewrites every floor a shipping project declares — and every other reference to the same package in the test
projects, the proof and the sample — to the newest release in its major (`10.*`; `0.3.*` for `Pgvector`), restores
with `--force-evaluate`, builds, and runs the whole suite on both frameworks. The only tests left out are the
`Category=DeclaredPins` ones, which check the versions the committed csproj files declare and so are rewritten on
purpose. It gates pushes and the weekly schedule, and reports without blocking on pull requests (see
[the version policy](#the-version-policy-floors-and-one-exact-pin)). Run it locally
the same way; it never edits your tree.

**Result on 2026-09-25:** PASSED. Every test outside `DeclaredPins` passed on both frameworks. What the floats
resolved to:

| Package | Floor | Floated to | Resolved |
| --- | --- | --- | --- |
| `dbup-core` | 6.1.1 | `6.*` | 6.1.1 |
| `dbup-postgresql` | 7.0.1 | `7.*` | 7.0.1 |
| `Microsoft.Extensions.AI.Abstractions` | 10.10.0 | `10.*` | **10.10.1** |
| `Microsoft.Extensions.Compliance.Redaction` | 10.10.0 | `10.*` | 10.10.0 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.12 | `10.*` | 10.0.12 |
| `Npgsql` | 10.0.3 | `10.*` | 10.0.3 |
| `Pgvector` | 0.3.2 | `0.3.*` | 0.3.2 |

Six of the seven floors are the newest release in their range today, so on the day it landed this leg mostly
re-proves the floors; its value is every later day.

## The MAF compatibility matrix

CI's `maf-compatibility` job runs the MAF adapter's tests and the MAF proofs twice:

| Leg | Microsoft.Agents.AI | Gates the build? |
| --- | --- | --- |
| `pinned` | 1.22.0, as shipped | **Yes.** A failure fails CI |
| `latest` | The newest stable version on nuget.org at run time | **No.** It reports a visible pass or fail in the job summary and never blocks, because a gate that failed whenever Microsoft published a release would make this repository's health a function of someone else's release calendar (story 4.3, AD-F) |

A passing `latest` leg is information, not a support claim. Moving the pin is a deliberate change: update the
csproj files, this document, and the README, and let the pinned leg prove it.

Run it locally with `eng/probe-maf-version.sh` (newest stable), `eng/probe-maf-version.sh 1.21.0` (a specific
version), or `eng/probe-maf-version.sh pinned`. It works on a throwaway copy of the tracked files (HEAD plus
uncommitted changes to them) and never edits your tree; untracked files are not copied, and it warns when `src/` or
`tests/` has any. The pinned leg restores the committed lock files exactly (`--locked-mode`); any other version has
to re-evaluate the graph (`--force-evaluate`), because re-pinning changes what the projects ask for.

**Result on 2026-09-25:** the newest stable version on nuget.org is still **1.22.0**, the pinned version, so the two
legs probe the same version. `eng/probe-maf-version.sh pinned` **PASSED**: 338 adapter tests and 6 MAF proofs, on each
of `net9.0` and `net10.0`. (On 2026-09-24, before story 6.3, both legs passed with 237 and 6 on `net10.0`; later
stories added the adapter tests in between.)

**History.** On 2026-09-23, with 1.20.0 pinned, the probe reported 1.22.0 as **FAILED (dependencies do not
resolve)** before any test ran. MAF 1.22.0 requires `Microsoft.Extensions.DependencyInjection.Abstractions`
≥ 10.0.12 and the 10.10.0 `Microsoft.Extensions.*` train, and Core's exact `[10.0.11]` pin refused the first. That
was KL-14. Story 5.1 resolved it by moving MAF and every shared pin it raises together (see the rows above).
Story 6.3 removed the cause: the shared packages are floors now, so a future MAF that raises one of them restores
beside Core and the stores, and the probe reports on its tests rather than failing at restore. It still re-pins MAF
only; a MAF that needs a new *major* of a shared package would restore too, but run against a floor from the previous
major until one moves.
