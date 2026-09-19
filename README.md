# AgentExperience.NET

[![CI](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)

**Portable, evidence-backed experience memory for .NET agents.**

AgentExperience.NET captures what an AI agent actually tried, verifies whether it worked, and turns the result into an auditable lesson that future runs can reuse safely. It sits between [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) execution and durable storage, without replacing either.

> **Status: early development.** Epic 1 (capture and explain agent experience) is implemented and tested. Epic 2 has started: a completed run can now be finalized into a durable Experience Record in PostgreSQL in one call, and moved through its lifecycle with atomic, audited commits. Retrieval, injection, and governance are planned (see [Roadmap](#roadmap)). Nothing is published to NuGet yet, and APIs may change.

## Why

Conversation history and fact memory don't answer the questions that matter when an agent retries similar work:

- Which approaches failed, and which succeeded?
- How was success *verified*, not just claimed?
- In which environment does the lesson apply?
- Is it safe for another agent to reuse?

AgentExperience.NET records observable evidence (tool calls, results, errors, verification checks) and never stores hidden chain-of-thought.

## What works today

| Capability | Where |
| --- | --- |
| Domain contracts: experience runs, attempts, evidence, outcomes, reflections, scope, environment | `AgentExperience.Abstractions` |
| Sanitization before storage: per-kind allowlists, secret redaction, fail-closed rejection | `AgentExperience.Core` |
| Thread-safe in-memory run capture with idempotent appends and size limits | `AgentExperience.Core` |
| Deterministic task verification: exit codes, tests, workflow and human checks; host-closed rounds; no LLM | `AgentExperience.Core` |
| Auditable, template-based reflections traceable to evidence IDs | `AgentExperience.Core` |
| MAF adapter: captures ordinary, streaming, failed, and cancelled runs plus tool calls, without altering results | `AgentExperience.MicrosoftAgentFramework` |
| PostgreSQL Experience Record store: create, get, and scoped query; host authorization checked before database access; exact scope matching in SQL | `AgentExperience.Storage.Postgres` |
| Atomic audited lifecycle commits: the event and the record's projection in one transaction, idempotent by event ID, revision-checked, with append-only history | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Journaled schema migrations: embedded scripts applied once, one transaction per script, serialized across processes by an advisory lock | `AgentExperience.Storage.Postgres` |
| One finalization call: evaluate, gate on authorization and the host's storage decision, reflect, create the record as a `Candidate`, commit the initial event that promotes it — replay-safe and structured at every stage | `AgentExperience.Core` |
| Dependency-injection registration for each package, so a host wires capture, finalization, and storage without knowing concrete types | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |

## Quick look

```csharp
AIAgent agent = chatClientAgent
    .AsBuilder()
    .UseExperienceCapture(captureService, new ExperienceCaptureOptions
    {
        ResolveRun = context => new ExperienceRunDescriptor(
            TaskId: "triage-ticket",
            Scope: hostScope),   // established by the host, never taken from model output
        OnCaptureFailure = failure => logger.LogWarning("Capture failed at {Stage}", failure.Stage),
    })
    .Build();

await agent.RunAsync("Triage ticket #4812", session);
// The run, its tool calls, and its sanitized outcome are now available from captureService.
```

See the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md) for options, supported agent types, and caveats. See the [PostgreSQL store README](src/AgentExperience.Storage.Postgres/README.md) for the trust boundary, the `ExperienceSchemaMigrator.MigrateAsync` startup call, and data semantics.

## Turning a run into a durable record

A captured run becomes a durable, reusable Experience Record through one Core call.
`ExperienceFinalizationService.FinalizeAsync` runs six stages in order — load the captured snapshot, evaluate it,
check authorization and the host's storage decision, reflect on it, create the record, commit its initial lifecycle
event — and stops at the first stage that ends the call, always returning a structured result rather than throwing.
The two gates precede reflection on purpose: the reflector is the seam a host would plug a model into, so a run that
is about to be refused is never handed to it.

```csharp
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

var result = await finalization.FinalizeAsync(
    new FinalizeExperienceRequest(
        RunId: runId,
        Authorization: authorization,                         // host-established; the run's scope must lie inside it
        ClosedRound: new ClosedVerificationRound(roundId, "rev-7"),
        RequiredChecks: [new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult")],
        Evidence: evidence,                                   // finalization filters and aggregates it itself
        CurrentArtifactRevision: "rev-7",
        StorageDecision: StorageDecision.Permit,              // or StorageDecision.Deny("retention policy")
        FinalizedAt: DateTimeOffset.UtcNow),
    cancellationToken);

if (result.IsDurable)
{
    logger.LogInformation("Experience {Id} is {Status} at revision {Revision}",
        result.ExperienceId, result.Status, result.Revision);
}
else
{
    logger.LogWarning("Finalization ended at {Stage}: {Outcome} — {Reason}",
        result.Stage, result.Outcome, result.Failure?.Reason);
}
```

| Outcome | When | What was written |
| --- | --- | --- |
| `Validated` | Verified, reflection succeeded, storage permitted | The record (reuse confidence 2/3, one supporting validation, no contradictions), created as `Candidate`, plus the initial event that moved it to `Validated` |
| `Quarantined` | Storage permitted, but verification did not pass or the reflector threw | The record, with **no** reflection, created as `Candidate`, plus the initial event that moved it to `Quarantined`. `Failure` names the stage that decided it |
| `AlreadyFinalized` | This run's record already exists *and* is already confirmed | Nothing. The result reports the stored record, status, and revision. (A record left unconfirmed by an earlier call is resumed instead: the retry commits its initial event and returns `Validated`/`Quarantined`.) |
| `StorageDenied` | The host's `StorageDecision` denied | Nothing at all, and no record ID is issued |
| `NotAuthorized` | The run's scope lies outside the authorization | Nothing; denied before any store call |
| `RunNotFound` / `RunNotFinished` | No such captured run, or it has no execution status | Nothing |
| `Failed` | A stage failed (for example the database was unavailable) | Never reported as durable. Any record already created stays a `Candidate`, which is never reusable, and the captured run stays available for a retry |

Three properties make retrying safe. The record is *created* as a `Candidate` and its initial lifecycle event
performs the real transition, so a commit that never lands leaves nothing reusable behind. The record ID, the
reflection ID, and the initial event ID are all derived from the run ID, so a second call cannot create a second
record or a second initial confirmation. And the initial event's fields are a pure function of the stored record, so
a retry re-derives exactly the event the store already deduplicates on.

Finalization never sanitizes — capture already rejected anything unsafe — and never decides storage or risk policy on
the host's behalf: `StorageDecision` travels in the request and Core simply obeys it.

### Wiring it

Each package registers its own services, so a host never names a concrete type:

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres.DependencyInjection;

services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddAgentExperiencePostgresStore();                     // IExperienceRecordStore
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService, ExperienceFinalizationService
```

`AgentExperience.Abstractions` stays BCL-only; only `Core` and the storage adapter take
`Microsoft.Extensions.DependencyInjection.Abstractions`, and every registration uses `TryAdd`, so a host's own
implementation wins. Call `ExperienceSchemaMigrator.MigrateAsync` once at startup before the store is used.

The MAF adapter can drive finalization for you: set `FinalizationService` and `ResolveFinalization` on
`ExperienceCaptureOptions` and every successfully captured invocation is finalized right after it is completed. See
the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md#finalizing-captured-runs).

## Design principles

- **Hexagonal core.** `Abstractions` depends only on the BCL; `Core` adds a redaction primitive and the dependency-injection *abstractions* it needs to register its own services. MAF, databases, models, and telemetry stay in adapters. Dependency-boundary tests enforce this in CI.
- **Failure-preserving capture.** Failed and cancelled runs are recorded through an outer lifecycle path, never only a success callback.
- **Evidence before trust.** Verification is deterministic and bound to a host-closed round and artifact revision. A completion score is never mistaken for reuse confidence.
- **Sanitize before anything is stored.** Unknown payload fields are dropped by default, and secrets are redacted from nested values.
- **Reuse, don't rebuild.** MAF middleware and `Microsoft.Extensions.Compliance.Redaction` are used at the edges, and planned storage builds on existing pgvector connectors. Each integration was proven with executable compatibility tests before an adapter was built.

## Repository layout

```
src/
  AgentExperience.Abstractions/             domain contracts and ports (BCL only)
  AgentExperience.Core/                     sanitization, capture, verification, reflection, lifecycle transitions, finalization
  AgentExperience.MicrosoftAgentFramework/  MAF adapter (pinned Microsoft.Agents.AI 1.20.0)
  AgentExperience.Storage.Postgres/         PostgreSQL Experience Record store and schema migrator (pinned Npgsql 10.0.3, dbup-postgresql 7.0.1, dbup-core 6.1.1)
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection, lifecycle tests
  AgentExperience.MicrosoftAgentFramework.Tests/  real ChatClientAgent runs against a scripted fake model
  AgentExperience.Storage.Postgres.Tests/   store tests, mostly against a PostgreSQL container
  AgentExperience.CompatibilityProof/       executable proofs for MAF hooks, context providers, pgvector, redaction
docs/                                       original production architecture research
_sdlc/                                      product brief, PRD, architecture, epics, and specs
```

## Build and test

Requires the [.NET SDK 10.0.302](https://dotnet.microsoft.com/) or a later feature band (see `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

Unit and MAF adapter tests run in memory, with no network, database, or model credentials. `AgentExperience.CompatibilityProof` and the `PostgresExperienceRecordStoreTests`, `PostgresLifecycleCommitTests`, `PostgresFinalizationTests`, and `ExperienceSchemaMigratorTests` in `AgentExperience.Storage.Postgres.Tests` start a PostgreSQL/pgvector container through Testcontainers, so they need Docker. If Testcontainers' Ryuk container fails to start under your local Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. To skip the container-backed tests:

```bash
dotnet test --filter "FullyQualifiedName!~CompatibilityProof&FullyQualifiedName!~PostgresExperienceRecordStoreTests&FullyQualifiedName!~PostgresLifecycleCommitTests&FullyQualifiedName!~PostgresFinalizationTests&FullyQualifiedName!~ExperienceSchemaMigratorTests"
```

## Roadmap

1. **Capture and explain agent experience** ✅ contracts, sanitization, capture, verification, reflection, MAF adapter
2. **Reuse relevant experience:** PostgreSQL persistence, atomic audited lifecycle commits, and one-call finalization of captured runs (in place), hybrid text and vector retrieval, historical-reference injection into MAF
3. **Govern experience safely:** sharing grants, the remaining lifecycle transitions, evidence-based confidence updates
4. **Operate and measure the learning loop:** OpenTelemetry instrumentation, an end-to-end demo, measured reuse against a baseline, data deletion and expiry

Full requirements and acceptance criteria are in [`_sdlc/planning-artifacts/epics.md`](_sdlc/planning-artifacts/epics.md).

## How this project is built

Development is spec-driven with the [BMAD Method](https://github.com/bmad-code-org/BMAD-METHOD) and AI-assisted implementation. The planning trail is versioned alongside the code:

- **Product brief, PRD, architecture, and epics:** [`_sdlc/planning-artifacts/`](_sdlc/planning-artifacts/)
- **MVP spec and reuse-boundary decisions:** [`_sdlc/specs/`](_sdlc/specs/)

Each story is planned against the architecture, implemented against explicit acceptance criteria, and then reviewed by independent adversarial, edge-case, and verification-gap passes before it is committed.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). To report a vulnerability, follow [SECURITY.md](SECURITY.md).

## License

[Apache-2.0](./LICENSE)
