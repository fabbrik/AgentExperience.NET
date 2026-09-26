# Compatibility evidence for every pin

Release verification (`RELEASING.md`, step 5) requires that every SDK, MAF, storage, and telemetry pin — MAF's
major-bounded range and every floor — has source-backed compatibility evidence. This is that evidence, in one table a reader can check
without trusting us: each row names where the version is declared, the public source that says the version exists and
what it is, the content hash NuGet restored (from the committed `packages.lock.json`, so a substituted package would
not match), and the executable test that proves this repository works against it.

It is the successor to the Story 1.7 research digest, which lived in a directory excluded from git. Anything a
reader needs to check a pin is here.

**Last verified: 2026-09-25**, against nuget.org's registration API and a full local run of the suite on all three target
frameworks and all four PostgreSQL majors, plus the floating-dependency leg and both MAF probe legs. Re-verify — and update the date — whenever
a pin or a floor moves.

## Supported matrix

Everything in this table is built and tested by CI on every change (story 6.3). A support matrix is always finite;
each "everything else" cell says why the line is where it is.

| Dimension | Supported | Everything else |
| --- | --- | --- |
| Target framework | `net8.0`, `net9.0` and `net10.0`. All five packages multi-target all three, and every test project that exercises them runs on all three (the sample, its tests and the reuse baseline, which are demonstrations, run on `net10.0` only). On `net8.0` only, Core and the store take `System.Text.Json` 10.0.12+ and Core `Microsoft.Bcl.Memory` 10.0.12+, for APIs the .NET 8 shared framework lacks (see [Target frameworks](#target-frameworks)) | .NET Framework and `netstandard2.0` are ruled out by `Npgsql` 10, which ships `net8.0`+ only, and `Microsoft.Extensions.Compliance.Redaction`, which ships no `netstandard2.0` build. `net8.0` and `net9.0` both leave support on 10 November 2026; the first preview after that date drops them |
| .NET SDK | `10.0.302` for a release build (see below) | Development may roll forward to a later feature band; a release may not |
| Microsoft Agent Framework | `Microsoft.Agents.AI` **1.22.0 or any later 1.x**, declared `[1.22.0, 2.0.0)`. CI tests the floor and the newest 1.x on every change, and both gate pushes and the weekly schedule (see [the MAF matrix](#the-maf-compatibility-matrix)) | 2.0 and later are outside the range: a host on one gets NuGet's NU1608 warning about this adapter. MAF states no SemVer promise and changed caller-visible behaviour five times between 1.15 and 1.22, so its next major is where a break is expected; [the version policy](#the-version-policy-floors-and-one-bounded-range) says why the bound sits there and nowhere lower |
| Everything else the packages reference | The floor in each row below, **or any later release in the same major** (the same minor for the 0.x `Pgvector`) | A later major restores (the packages declare no upper bound, so it is never a restore conflict) but is untested until a floor moves. See [the version policy](#the-version-policy-floors-and-one-bounded-range) |
| PostgreSQL | Majors **15, 16, 17 and 18**: `pgvector/pgvector:pg15`, `pgvector/pgvector:pg16`, `pgvector/pgvector:pg17`, `pgvector/pgvector:pg18`, and stock `postgres:15`–`postgres:18` for the text-only schema. 16 is the default for a local run | 14 reaches end of life upstream on 12 November 2026, and migration `0005` is PostgreSQL 15 syntax that fails there; no PostgreSQL 14 path exists that leaves the journaled `0005` untouched and keeps one schema (see [Why not 14](#why-not-14)). 13 and older are past end of life. The image tags float within each major, so the minor version is whatever the tag resolved to on the day |
| pgvector | Whatever each `pgvector/pgvector:pg{N}` image ships (0.8.6 in all four on 2026-09-25), via `Pgvector` 0.3.2 or later within 0.3.x | — |

## The version policy: floors, and one bounded range

Story 6.3 (KL-13) replaced the exact-pin policy. Before it, every shipping `PackageReference` was exact, so a host
whose graph needed a newer `Microsoft.Extensions.*` package, `Npgsql`, or a MAF that brought newer ones, got a restore
conflict until a new preview moved the pins. Story 7.2 removed the last exact pin, MAF's. Now:

| Kind | Declared as | What CI proves | Which packages |
| --- | --- | --- | --- |
| **Floor** | `Version="x.y.z"` — NuGet's `>= x.y.z`, with no upper bound | The floor itself, on every run: the committed lock files resolve exactly the floor, and `CompatibilityPinAgreementTests` fails if they ever resolve anything else. **And** the newest release in the same major (the same minor for a 0.x package), on every run, in the `floating-dependencies` job | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Compliance.Redaction`, `Microsoft.Extensions.AI.Abstractions`, `Npgsql`, `dbup-postgresql`, `dbup-core`, `Pgvector`; and, on `net8.0` only, `System.Text.Json` and `Microsoft.Bcl.Memory` |
| **Range to the next major** | `Version="[x.y.z, N.0.0)"`, where N is x + 1 | Exactly what a floor gets: the lower bound on every run (the lock files resolve it), and the newest release below the bound on every run, in the `floating-dependencies` job **and** in the `maf-compatibility (latest)` leg | `Microsoft.Agents.AI` only |
| **Exact** | `Version="[x.y.z]"` | — | None since story 7.2. `CompatibilityPinAgreementTests` refuses one in a shipping project: it is a restore failure for any host on a newer version |

**What a floor claims.** Every release from the floor up to, but not including, the next major (for `Pgvector`, the
next minor: a 0.x version may break in a minor). Both ends of that range run the full suite on the default PostgreSQL major on every change (the floating end minus the `DeclaredPins` checks, which assert the floor itself); the
releases in between are covered by the package's own semantic-versioning promise, not by a run. A later major is not
claimed, but it is not refused either.

**Why no upper bound.** An upper bound would put the claim in the package metadata, but NuGet reports a host that
resolves past it with NU1608, and a host that builds with warnings as errors — as this repository does — cannot restore
past that warning. That is the conflict KL-13 described, moved one major later. A bare floor is what the
`Microsoft.Extensions.*` packages themselves declare for their own dependencies.

**Why the floating leg gates main, but not pull requests.** Until story 7.2 the MAF probe's `latest` leg never
blocked, because an exact MAF pin made no claim about the newest one (AD-F). A floor does make that claim, and so
does MAF's range now: NuGet resolves the floor by
default, but any host whose own references or other dependencies ask for more gets more, up to the newest release in
the major, with no warning. So if that release breaks the library, those hosts are already broken, and `main` must go
red rather than report. The job therefore gates on every push and on the weekly schedule, so a new release is noticed
within a week even when nothing here changes. On a pull request it runs the same way and reports PASSED or FAILED in
the job summary, but is `continue-on-error`: `Microsoft.Extensions.*` ships monthly and MAF every week or two, and an
unrelated pull request should not go red the morning a release lands — AD-F's reasoning, which still holds for pull
requests. The MAF probe's `latest` leg follows exactly the same rule since story 7.2. A red floating or `latest` leg on
a pull request is still read, not ignored.

**Triage when it fails.** Read the resolved-versions table in the job summary to see which float moved. Then either:
1. **Fix the library**, with a change that works at both ends of the range (the floor and the newest release), or
2. **Raise the floor** past the broken release (update the shipping csproj, the proof's exact pin and the lock files
   together; `CompatibilityPinAgreementTests` fails until they agree), and record the new floor here.

Never cap the range with an upper bound below the next major instead: that is the NU1608 conflict KL-13 described,
for every host on the newer release. The one exception would be a proven break that neither fix can reach, recorded
here with the failing test; there has been none.

**Why MAF is a range to the next major, not an exact pin and not a bare floor.** Story 7.2 read MAF's own source
at every tag from `dotnet-1.15.0` to `dotnet-1.22.0` (eight minors, 22 July to 18 September 2026) for the surfaces the
adapter uses: agent-run and function-invocation middleware (`AIAgentBuilder.Use`, `FunctionInvocationDelegatingAgent`),
context providers (`AIContextProvider`, `InvokingContext`, `InvokedContext`), `AgentSession` and its `StateBag`,
`ChatClientAgent`, and `ChatClientAgentRunOptions`. What it found:

- **No binary or signature break** on any of those surfaces. `AIContextProvider.cs`, `AIContext.cs`, `AgentSession.cs`,
  the `StateBag` and its JSON converters, `AgentRunOptions.cs`, `AIAgentBuilder.cs`, `AnonymousDelegatingAIAgent.cs`
  and `ChatClientAgentRunOptions.cs` did not change at all. MAF runs package validation (ApiCompat) against 1.0.0, and
  its `CONTRIBUTING.md` requires "API signature and behavioral compatibility".
- **Five caller-visible behaviour changes** nonetheless, in `AIAgent.cs`, `ChatClientAgent.cs` and
  `FunctionInvocationDelegatingAgent.cs`: 1.16 (a session `ConversationId` now also disables the chat-history
  provider, [#7284](https://github.com/microsoft/agent-framework/pull/7284)); 1.19 (`RunAsync` became `async`, so
  `CurrentRunContext` no longer leaks out of an inner run and a synchronous throw surfaces through the task,
  [#7641](https://github.com/microsoft/agent-framework/pull/7641); AG-UI history handling,
  [#7741](https://github.com/microsoft/agent-framework/pull/7741)); 1.22 (function middleware clones the caller's
  `ChatClientAgentRunOptions` and `ChatOptions` per run, wraps tools added mid-run, and runs stacked middleware once
  each, [#8402](https://github.com/microsoft/agent-framework/pull/8402); construction-time tools travel only in each
  run's `ChatOptions`, [#8531](https://github.com/microsoft/agent-framework/pull/8531)). Only #8402 touched a hook
  the adapter uses, and it was a fix the adapter's tests caught and pin (below). Other [BREAKING] pull requests in
  the window (#7671, #8032, #8375, #8425, #7991) are outside the adapter's surface.
- **`[Experimental]` surface** next to the hooks: the `InvokingContext`/`InvokedContext` constructors (`MAAI001`) and
  the continuation tokens, unchanged from 1.15 to 1.22. The adapter uses none of it, and builds with warnings as
  errors and no `MAAI001` suppression.

So an exact pin is not justified: every one of those minors kept the adapter's surface compatible, and the one change
that mattered was caught by a test. But MAF makes no SemVer promise, ships behaviour changes in minors, and marks
neighbouring surface experimental, so a bare floor, which would also admit a MAF 2.0 nobody has built against, claims
more than the evidence shows. The range admits every 1.x at or above 1.22.0, which a host needing a newer minor
resolves with no warning, and stops at 2.0, the version at which MAF is most likely to break and a host should be
told. Every 1.x the range admits is covered the way a floor's major is: the floor on every run, and the newest 1.x on
every run, gating pushes and the weekly schedule. A MAF minor that breaks the adapter therefore turns `main` red
on the next push or weekly run, and is triaged as above. The KL-14 failure mode stays gone: with the shared packages on floors, a newer
MAF that raises a shared `Microsoft.Extensions.*` floor restores beside Core and the stores.

**How it is enforced.** `CompatibilityPinAgreementTests` fails if a shipping reference is anything but a bare floor
(or, for MAF, `[x.y.z, (x+1).0.0)`), and so refuses an exact pin; if a floor or range's lower bound is not exactly
what the committed lock files resolve, in every target framework it applies to; if a reference is conditioned on
anything but one target framework the build produces, or is a direct reference in another framework's section of the
lock file; if two
shipping projects declare different versions of one package; or if the compatibility proof stops pinning each proven
package exactly at the shipping floor. The dependency-boundary tests pin each package's declared set, conditions
included, and `eng/verify-packages.cs` checks the built nuspecs declare exactly those versions in every framework's
dependency group, and the `net8.0`-only ones in the `net8.0` group and nowhere else.

## The pins

Hashes are NuGet's SHA-512 `contentHash`, exactly as recorded in the lock files (identical in every target
framework's section that has the package, since it is one package). "Published" is nuget.org's registration timestamp
for that version. A bold `x.y.z+` is a floor and a bold `[x.y.z, N.0.0)` a range to the next major; either way the row
is the evidence for the lower bound itself — the newest release below the bound is proven by
[the floating-dependency leg](#the-floating-dependency-leg) (and, for MAF, [the MAF probe](#the-maf-compatibility-matrix)).

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
| `Microsoft.Agents.AI` **[1.22.0, 2.0.0)** | `src/AgentExperience.MicrosoftAgentFramework` | 2026-09-18 | `YdQJbL47n8PJmTGCD6DzapD2TQiCfI1f/669ECcHsKw1po3WhTq2lZoO3X7PJ6sZSGE/I2RReJ/hTGwtBZ7WTA==` | <https://www.nuget.org/packages/Microsoft.Agents.AI/1.22.0>, source at tag [`dotnet-1.22.0`](https://github.com/microsoft/agent-framework/tree/dotnet-1.22.0/dotnet/src). Requires `Microsoft.Extensions.DependencyInjection.Abstractions` ≥ 10.0.12 and the 10.10.0 `Microsoft.Extensions.AI`/`Compliance`/`VectorData`/`AI.Evaluation` train, which is why the pins below moved with it. Every version from 1.15.0 to 1.22.0 ships `net8.0`, `net9.0` and `net10.0` builds | `CompatibilityProof/MafHooksProof` (invocation hooks, failure visibility through `InvokedCoreAsync`), `CompatibilityProof/ContextProviderFitProof` (custom `AIContextProvider` injection), and the whole `AgentExperience.MicrosoftAgentFramework.Tests` suite driving real `ChatClientAgent` runs |

The pin moved from 1.20.0 to 1.22.0 in story 5.1 (1.21.0 was never pinned). Nothing in the adapter had to change;
one documented caveat went away (the third bullet below). Story 7.2 turned the exact pin into the range
`[1.22.0, 2.0.0)` with the same lower bound, so the default run and its lock files did not change.

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
  none either — but the neighbouring surface is still settling, which is one reason the range stops at 2.0 rather
  than being a bare floor.
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

### `net8.0` only

These two exist only in the `net8.0` dependency group, declared under `Condition="'$(TargetFramework)' == 'net8.0'"`.
The .NET 8 shared framework lacks APIs Core and the store compile against (see [Target frameworks](#target-frameworks));
`net9.0` and `net10.0` take them from the shared framework and reference nothing. Both are the .NET 10 train's
out-of-band packages at the servicing release the DI abstractions floor is at, and `Microsoft.Agents.AI` 1.22.0 and
`Microsoft.Extensions.AI.Abstractions` 10.10.0 already require `System.Text.Json` ≥ 10.0.12 on `net8.0`, so a host with
the adapter or the vectors package resolves that version whatever Core asks.

| Pin | Declared in | Published | Content hash | Source | Executable evidence |
| --- | --- | --- | --- | --- | --- |
| `System.Text.Json` **10.0.12+** (`net8.0` only) | `Core`, `Storage.Postgres` | 2026-09-08 | `AHlVpUiY96eC6Oar1Gf0YbafU7E2NUU9D80O2Xk5v7ryL3kpSsbI0EusJBE8kQfwPuDdUDoKUItArzXQHxomCg==` | <https://www.nuget.org/packages/System.Text.Json/10.0.12>. Supplies `JsonElement.DeepEquals` (Core's capture merge), and `JsonSerializerOptions.RespectNullableAnnotations` and `RespectRequiredConstructorParameters` (the store's strict payload decoder), all added in .NET 9. 9.0.0 would compile, but the floor is 10.0.12 so it never sits below what the adapter and vectors package already bring, and so the test lock files, which resolve 10.0.12 through them, run the floor | The whole `net8.0` run: `InMemoryExperienceCaptureServiceTests` (capture merge), the `ExperiencePayload` decoder's refusal tests and every container-backed store test in `AgentExperience.Storage.Postgres.Tests`, on the same assertions as the other frameworks |
| `Microsoft.Bcl.Memory` **10.0.12+** (`net8.0` only) | `Core` | 2026-09-08 | `nFYTvCdZYqQnSKzhw3V1wWjESFrN8sYk1WAXYQSAkC0vDoHYySouF569g/hx9RzUXiZjTWxZR7gr7zDodG+Ufg==` | <https://www.nuget.org/packages/Microsoft.Bcl.Memory/10.0.12>. Supplies `System.Buffers.Text.Base64Url`, added to the shared framework in .NET 9, which the assessment tokens of story 6.6 encode and strictly decode with. The package is the same source as the shared framework's type, and has no dependency on `net8.0` | `AssessmentTokenTests` and every verified-independence test in `AgentExperience.Core.Tests` and `AgentExperience.Storage.Postgres.Tests`, on `net8.0`: forged, non-canonical, expired and replayed tokens are refused exactly as on the other frameworks |

### Telemetry

| Pin | Declared in | Source | Executable evidence |
| --- | --- | --- | --- |
| **No telemetry package.** `ActivitySource` and `Meter` from `System.Diagnostics.DiagnosticSource`, which ships in the shared framework of `net8.0`, `net9.0` and `net10.0`. On `net8.0` and `net9.0` a dependency may raise it to a newer `System.Diagnostics.DiagnosticSource` package transitively (MAF 1.22.0 brings 10.0.12); that is the dependency's reference, not one this library declares | Nothing to declare: it is part of the target framework the SDK pin selects | <https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs>, <https://learn.microsoft.com/dotnet/core/diagnostics/metrics-instrumentation> | `ExperienceTelemetryTests`, `TelemetrySourceScanTests`, `InjectionTelemetryTests`, `DiagnosticsAgreementTests` subscribe with the BCL's own `ActivityListener` and `MeterListener`, exactly as an OpenTelemetry exporter would. The dependency-boundary tests forbid `OpenTelemetry` in Abstractions and Core, and `eng/verify-packages.cs` forbids it in their built nuspecs |

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
it exact-pins the same versions — the floor, or for MAF the range's lower bound, so the proof always runs the bottom
of what a host can resolve. `AgentExperience.Release.Tests/Compatibility/CompatibilityPinAgreementTests` fails if they ever
disagree — checked against both the declared csproj versions and the resolved lock files, in every target framework —
or if the proof stops referencing a version it is evidence for. The floating-dependency leg floats the proof's
direct references together with the shipping ones. A package the proof only gets transitively (today
`Microsoft.Extensions.AI.Abstractions`, through MAF and the evaluation and vector-data packages) is not floated there.

## Target frameworks

`net8.0`, `net9.0` and `net10.0`, set once as `AgentExperienceTargetFrameworks` in `Directory.Build.props`. Every
shipping package carries `lib/net8.0`, `lib/net9.0` and `lib/net10.0` and a dependency group for each, and
`eng/verify-packages.cs` checks all three, and only those, in every package and symbol package. The public API
baseline is **one file per assembly for every framework**: `AgentExperience.Release.Tests` runs once per framework and
compares each against the same baseline, so the gate also asserts the public surface is identical on all three.
Warnings stay errors on all three.

**How `net8.0` builds (story 7.2).** Every dependency ships a `net8.0` build, but the .NET 8 shared framework lacks
four APIs the library uses, all added in .NET 9. Adding `net8.0` on 2026-09-25 failed with exactly these:

- `src/AgentExperience.Core/Capture/InMemoryExperienceCaptureService.cs`: `JsonElement.DeepEquals`.
- `src/AgentExperience.Storage.Postgres/ExperiencePayload.cs`: `JsonSerializerOptions.RespectNullableAnnotations` and
  `RespectRequiredConstructorParameters`. They make the stored-payload decoder refuse a `null` where the type says
  non-null and a missing required constructor parameter — the strictness the store's payload reading depends on.
- `src/AgentExperience.Core/Confidence/AssessmentTokens.cs` (story 6.6): `System.Buffers.Text.Base64Url`.
- `src/AgentExperience.Abstractions/ExperienceIndex.cs`: `Convert.ToHexStringLower`.

(The MAF adapter and the vectors package failed only because they reference those.) The first three are supplied on
`net8.0` by the .NET 10 train's own packages, `System.Text.Json` and `Microsoft.Bcl.Memory` 10.0.12, referenced
under a `net8.0` condition in Core and the store and nowhere else — the same thing `Microsoft.Extensions.AI` and MAF
do for their own `net8.0` builds. The fourth is one line behind `#if NET9_0_OR_GREATER`:
`Convert.ToHexString(...).ToLowerInvariant()`, which gives the identical 64 lowercase hexadecimal characters, so
Abstractions keeps no package dependency at all.

The alternative, a `net8.0`-only rewrite, was rejected as the larger and less safe option: the strict decoder would
need a hand-written null-and-required-member validator for every payload type, and assessment-token decoding a
hand-written base64url codec on the one framework — security-relevant code that exists only where fewer people run
it. The packages are Microsoft's own implementations of exactly the APIs the other frameworks use, so every
framework runs the same decoder and the same codec, under the same tests. The cost is two conditional references in
Core's dependency boundary (AD-1), which `DependencyBoundaryTests` and `eng/verify-packages.cs` pin, condition
included; neither is a forbidden adapter dependency.

.NET 8 (LTS) and .NET 9 (STS; STS releases now get 24 months) both leave support on 10 November 2026. The first
preview after that date drops `net8.0` and `net9.0`, and the matrix is then `net10.0` again until .NET 11 ships.

**Planned removal.** The first preview published after 2026-11-10 removes `net8.0` and `net9.0` from
`AgentExperienceTargetFrameworks`, and with them everything that exists only for them: the `net8.0`-only references
to `System.Text.Json` and `Microsoft.Bcl.Memory` in Core and the store (and their rows in the dependency tables above),
the `#if NET9_0_OR_GREATER` branch, and the `net8.0` and `net9.0` runs of every test project. That preview's
CHANGELOG entry says so under its breaking changes. Until then both stay targeted, built, packed and tested exactly as
now; nothing is removed early. PostgreSQL 14 is not affected by this: it is already outside the matrix (see
[Why not 14](#why-not-14)), and stays out when it reaches end of life upstream on 12 November 2026.

## The PostgreSQL matrix

CI's `postgres` job runs `AgentExperience.Storage.Postgres.Tests`, `…Vectors.Tests`, `AgentExperience.CompatibilityProof`
and `AgentExperience.Sample.EndToEnd.Tests` once per supported major (on every target framework, except the sample tests, which are `net10.0` only), with
`AGENTEXPERIENCE_POSTGRES_MAJOR` set to the leg's major. `WorkflowTests` fails if the job's list and
`PostgresTestImage.SupportedMajors` ever differ. The default `build-and-test` job runs everything against 16.

**Result on 2026-09-25** (story 7.2; local, `TESTCONTAINERS_RYUK_DISABLED=true`, all three frameworks except the
sample, images as resolved that day, the same locally cached images as story 6.3's run). The 14 row was measured
once, by story 6.3, on `net10.0`, on an earlier revision of the suite, before the fixture started refusing 14, so its
totals differ from the other rows:

| Major | Server (pgvector image) | pgvector | Postgres tests (`net8.0` + `net9.0` + `net10.0`) | Vectors tests | Proof | Sample |
| --- | --- | --- | --- | --- | --- | --- |
| 15 | 15.19 | 0.8.6 | 489 + 489 + 489 passed | 67 + 67 + 67 passed | 15 + 15 + 15 passed | 34 passed (net10.0) |
| 16 | 16.15 | 0.8.6 | 489 + 489 + 489 passed | 67 + 67 + 67 passed | 15 + 15 + 15 passed | 34 passed (net10.0) |
| 17 | 17.11 | 0.8.6 | 489 + 489 + 489 passed | 67 + 67 + 67 passed | 15 + 15 + 15 passed | 34 passed (net10.0) |
| 18 | 18.6 | 0.8.6 | 489 + 489 + 489 passed | 67 + 67 + 67 passed | 15 + 15 + 15 passed | 34 passed (net10.0) |
| 14 | 14.24 | 0.8.6 | **355 failed** of 432 (net10.0 only) | **47 failed** of 65 (net10.0 only) | 14 passed (net10.0 only) | **3 failed** |

### Why not 14

Every PostgreSQL 14 failure is the same one: the schema migrator stops at `0005_create_experience_grants`, whose
unique index on active grants is declared `NULLS NOT DISTINCT` (`42601: syntax error at or near "NULLS"`), which
PostgreSQL added in 15. Everything that needs the migrated schema then fails; the proof, which does not use the
migrator, passes.

Story 7.2 asked whether 14 can be reached **without editing `0005`**, and it cannot, for a reason in how DbUp works
rather than in effort. The migrator journals each script by its name alone
(`agent_experience.schema_versions`, one row per applied script name, no content hash), and applies every embedded
script whose name is not journaled, in name order. `0005` is one statement file: PostgreSQL 14 rejects the whole
file at parse time, so nothing placed *before* it (a `0004b` script, a session setting) can make it run, and every
later script depends on the table it creates. That leaves three shapes, and each breaks a rule this schema keeps:

1. **Serve different text under the name `0005` on 14** (a script provider that swaps the index for a PostgreSQL 14
   equivalent: a unique expression index over `COALESCE`d scope columns, under the same index name the grant store
   maps a conflict from). That *is* editing a journaled script, per server: the journal would say `0005` on two
   databases that hold different schemas, and nothing could tell them apart afterwards.
2. **Skip `0005` on 14 and apply a `0005_pg14` script instead.** The journals then differ by name, which is honest,
   but a PostgreSQL 14 database upgraded in place to 15 or later (`pg_upgrade` keeps the data and the journal) would
   find `0005` unjournaled and run it; its `CREATE ... IF NOT EXISTS` statements would then skip silently over the
   PostgreSQL 14 objects, leaving the expression index in place of the `NULLS NOT DISTINCT` one for the life of that
   database. Every later migration that touches grants would need a second variant, tested on 14, forever.
3. **A version-conditional `0005`** (`DO` blocks choosing the index by `server_version_num`). This is option 1 by
   another route: `0005` is already journaled on every existing database, so the only way a conditional version
   runs is as new text under the old name.

Each is a second schema lineage for a major that reaches end of life upstream on **12 November 2026**, seven weeks
after this was written, with no security fixes after that date. A deployment on 14 has to upgrade within weeks
anyway, and one on 15 or later has nothing to gain from a second lineage. So 14 stays out, `PostgresTestImage`
refuses it, and the reason is the end-of-life date and the journal, not a missing feature.

## The floating-dependency leg

CI's `floating-dependencies` job runs `eng/probe-floating-dependencies.sh`, which, on a throwaway copy of the tracked
files, rewrites every floor a shipping project declares — and every other reference to the same package in the test
projects, the proof and the sample — to the newest release in its major (`10.*`; `0.3.*` for `Pgvector`; `1.*` for
`Microsoft.Agents.AI`, whose range stops at 2.0), restores with `--force-evaluate`, builds, and runs the whole suite
on every framework. The only tests left out are the
`Category=DeclaredPins` ones, which check the versions the committed csproj files declare and so are rewritten on
purpose. It gates pushes and the weekly schedule, and reports without blocking on pull requests (see
[the version policy](#the-version-policy-floors-and-one-bounded-range)). Run it locally
the same way; it never edits your tree.

**Result on 2026-09-25** (story 7.2, with `net8.0` and the MAF range): PASSED. Every test outside `DeclaredPins`
passed on all three frameworks. What the floats resolved to:

| Package | Floor | Floated to | Resolved |
| --- | --- | --- | --- |
| `dbup-core` | 6.1.1 | `6.*` | 6.1.1 |
| `dbup-postgresql` | 7.0.1 | `7.*` | 7.0.1 |
| `Microsoft.Agents.AI` | 1.22.0 | `1.*` | 1.22.0 |
| `Microsoft.Bcl.Memory` (`net8.0`) | 10.0.12 | `10.*` | 10.0.12 |
| `Microsoft.Extensions.AI.Abstractions` | 10.10.0 | `10.*` | **10.10.1** |
| `Microsoft.Extensions.Compliance.Redaction` | 10.10.0 | `10.*` | 10.10.0 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.12 | `10.*` | 10.0.12 |
| `Npgsql` | 10.0.3 | `10.*` | 10.0.3 |
| `Pgvector` | 0.3.2 | `0.3.*` | 0.3.2 |
| `System.Text.Json` (`net8.0`) | 10.0.12 | `10.*` | 10.0.12 |

Nine of the ten floors are the newest release in their range today, so on the day it landed this leg mostly
re-proves the floors; its value is every later day.

## The MAF compatibility matrix

CI's `maf-compatibility` job runs the MAF adapter's tests and the MAF proofs twice, on every target framework:

| Leg | Microsoft.Agents.AI | Gates the build? |
| --- | --- | --- |
| `pinned` | The range's floor, 1.22.0, from the committed lock files | **Yes**, always |
| `latest` | The newest stable version on nuget.org **inside the declared range** (below 2.0.0) at run time | **Yes on push and on the weekly schedule; reports only on a pull request** (story 7.2). Before story 7.2 it never blocked (story 4.3, AD-F), because the exact pin made no claim about the newest version. The range does: a host whose graph asks for a newer 1.x gets it with no warning, so a 1.x that breaks the adapter breaks those hosts, and `main` must go red. On a pull request it is `continue-on-error`, so an unrelated change is not held hostage to a MAF release that shipped that morning — the part of AD-F that still holds |

A failing `latest` leg is triaged like a failing floating leg ([the version policy](#the-version-policy-floors-and-one-bounded-range)):
fix the adapter so it works at both ends of the range, or raise the floor past the broken release. A version outside
the range (a 2.x, once one exists) is probed only on request, and a pass there is information, not a claim: widening
the range is a deliberate change to the csproj files, this document and the README. The floating-dependency leg floats
MAF too, within the major, so the newest 1.x also runs the *whole* suite (the store, the sample and the reuse
baseline, which reach MAF through the adapter), not only the adapter's.

Run it locally with `eng/probe-maf-version.sh` (the newest stable version inside the range; it notes a newer one
outside), `eng/probe-maf-version.sh 1.23.0` (a specific version), or `eng/probe-maf-version.sh pinned`. It works on a
throwaway copy of the tracked files (HEAD plus uncommitted changes to them) and never edits your tree; untracked
files are not copied, and it warns when `src/` or `tests/` has any. The floor restores the committed lock files
exactly (`--locked-mode`); any other version is pinned exactly in the copy and re-evaluates the graph
(`--force-evaluate`), and skips the `Category=DeclaredPins` tests, which assert the committed versions and which the
re-pin changes on purpose. On that path the proof's other exact pins (`Npgsql`, `Pgvector`, the redaction package)
become floors in the copy, so a newer MAF that raises a shared floor fails the leg only if the adapter breaks, not on
a test-only pin no host has; and the probe fails unless the adapter's tests actually resolved the version it names.
`MAF_PROBE_INDEX_URL` replaces nuget.org's version list (a `file://` URL works); on 2026-09-25 a list holding
`2.0.0`, `2.0.0-preview.1` and `10.1.0` beside 1.22.0 made the probe note the out-of-range versions and probe 1.22.0.

**Result on 2026-09-25:** the newest stable version on nuget.org is still **1.22.0**, the floor, so the two legs
probe the same version. Both **PASSED**: 406 adapter tests and 6 MAF proofs, on each of `net8.0`, `net9.0` and
`net10.0`. (On 2026-09-24, before story 6.3, both legs passed with 237 and 6 on `net10.0`; later stories added the
adapter tests in between.) The path a newer version takes (exact re-pin, `--force-evaluate`, `DeclaredPins`
skipped) was exercised the same day with `eng/probe-maf-version.sh 1.21.0`, below the range: it restored, ran 404 adapter tests per framework (the two `DeclaredPins` tests skipped), and reported **FAILED (tests)**: exactly the three `A_reused_ChatClientAgentRunOptions_instance_…` tests fail on every framework, because 1.21.0 still writes its `ChatClientFactory` onto the caller's options, the behaviour #8402 fixed in 1.22.0. That is the evidence the adapter's tests see a MAF behaviour change on its hooks, rather than passing through it.

**History.** On 2026-09-23, with 1.20.0 pinned, the probe reported 1.22.0 as **FAILED (dependencies do not
resolve)** before any test ran. MAF 1.22.0 requires `Microsoft.Extensions.DependencyInjection.Abstractions`
≥ 10.0.12 and the 10.10.0 `Microsoft.Extensions.*` train, and Core's exact `[10.0.11]` pin refused the first. That
was KL-14. Story 5.1 resolved it by moving MAF and every shared pin it raises together (see the rows above).
Story 6.3 removed the cause: the shared packages are floors now, so a future MAF that raises one of them restores
beside Core and the stores, and the probe reports on its tests rather than failing at restore. It still re-pins MAF
only; a MAF that needs a new *major* of a shared package would restore too, but run against a floor from the previous
major until one moves. Story 7.2 removed the last part: MAF itself is a range now, so a host that needs a newer 1.x no longer
gets NU1608 (an error under warnings as errors) or NU1107 from this adapter, and the newest 1.x is tested, and
gates, instead of being reported.
