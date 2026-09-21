# AgentExperience.NET

[![CI](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)

**Portable, evidence-backed experience memory for .NET agents.**

AgentExperience.NET captures what an AI agent actually tried, verifies whether it worked, and turns the result into an auditable lesson that future runs can reuse safely. It sits between [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) execution and durable storage, without replacing either.

> **Status: early development.** Epic 1 (capture and explain agent experience) is implemented and tested. Epic 2 has started: a completed run can now be finalized into a durable Experience Record in PostgreSQL in one call, moved through its lifecycle with atomic, audited commits, and retrieved by task text with bounded, explainable ranking. Vector retrieval, injection, and governance are planned (see [Roadmap](#roadmap)). Nothing is published to NuGet yet, and APIs may change.

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
| Text retrieval of applicable experience: eligibility decided before ranking, every ranking component and effective weight exposed, bounded by a timeout that is never an exception | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Dependency-injection registration for each package, so a host wires capture, finalization, storage, and retrieval without knowing concrete types | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |

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

## Retrieving applicable experience

Finding experience that applies to a task is one Core call: `ExperienceRetrievalService.RetrieveAsync`. It asks the
storage adapter for scope-, status- and confidence-filtered text matches, decides the remaining eligibility itself,
and ranks what survives — always returning a structured result rather than throwing.

```csharp
using AgentExperience.Core.Retrieval;

var result = await retrieval.RetrieveAsync(
    new RetrieveExperienceRequest(
        Authorization: authorization,            // host-established; the request scope must lie inside it
        Scope: scope,                            // the exact scope to retrieve within, never widened
        TaskText: "refund ticket stuck on a lock",
        RequiredEnvironmentAttributes: new Dictionary<string, string> { ["region"] = "us-east" },
        CorrelationId: traceId),
    cancellationToken);

if (result.TimedOut)
{
    logger.LogInformation("Retrieval timed out for {CorrelationId}; the agent runs without memory", result.CorrelationId);
}

foreach (var ranked in result.Records)          // highest score first, ties by ExperienceId ascending
{
    logger.LogDebug("{Id} scored {Score} from {Components}",
        ranked.Record.ExperienceId,
        ranked.Score,
        string.Join(", ", ranked.Components.Select(c => $"{c.Kind}={c.Value}*{c.Weight}")));
}
```

**Eligibility is decided before ranking, and nothing is scored before it is known to be reusable.**

| Check | Where it runs | Effect |
| --- | --- | --- |
| Scope | SQL | Only records in the request's *exact* scope; a foreign scope reveals nothing |
| Status | SQL | Only `Validated` and `Reinforced`. `Candidate`, `Quarantined`, `Contested`, `Stale`, `Superseded`, and `Revoked` are never returned, whatever their text match |
| Reuse confidence | SQL | Below `RetrievalPolicy.MinimumConfidence` (default 0.5) is excluded |
| Text match | SQL | PostgreSQL full-text search over task ID, task summary, and reflection lesson |
| Expiry | Core | Last lifecycle activity older than `RetrievalPolicy.MaxAge` is excluded. `null` (the default) means no expiry |
| Environment | Core | Every required attribute must equal the record's `EnvironmentFingerprint.Metadata` entry; a missing key excludes the record. A request with no required attributes sets `EnvironmentUnrestricted` on the result |

`result.Excluded` itemizes what the **Core** checks removed — expiry and environment — so "nothing matched" is
distinguishable from "something matched but was not reusable here". It is deliberately not a complete account of
everything filtered: scope, status, and the confidence floor are applied in SQL, so records they exclude never reach
Core and are never listed. That split is the point — a foreign-scope or revoked record must not be observable, even
as a count.

**There is a recall ceiling, and it is visible.** The search returns at most `RetrievalPolicy.CandidateLimit`
candidates (default 50), ordered by *text* relevance, and ranking only ever sees those. So a record with a weaker
text match but strong confidence, recency, or status is not ranked at all once that many stronger text matches exist:
the weighting can only reorder what the ceiling let through. When the ceiling is reached, `result.Truncated` is
`true` — the records beyond it are in no exclusion list either, because no eligibility check ever looked at them.
Raise `CandidateLimit` or narrow the task text when that matters. `request.Limit` may not exceed `CandidateLimit`; a
larger value is rejected rather than quietly capped.

**Ranking is explainable.** Every returned record carries all five normalized components (each in 0–1) and the
effective weight applied to it, so the score is always reproducible from what the result holds.

| Component | Default weight | Normalized as |
| --- | --- | --- |
| Relevance | 0.35 | `ts_rank_cd` of the text match, normalized to 0–1 |
| Confidence | 0.25 | The record's `ReuseConfidence` |
| Recency | 0.15 | `2^(-age / RecencyHalfLife)`, half-life 30 days by default. *Age* is measured from `UpdatedAt` |
| Status | 0.15 | `Reinforced` 1.0, `Validated` 0.5 |
| Environment compatibility | 0.10 | 1.0 for a record that satisfied the request's required attributes — which every ranked record did, since a mismatch excludes it before ranking |

Weights must be finite, non-negative, and sum to 1 (within `RankingWeights.SumTolerance`); anything else throws
`ArgumentOutOfRangeException` at construction, so an invalid weighting can never reach a retrieval call. Ties sort by
`ExperienceId` ascending and ordinal, so the ordering is total and stable, and a golden fixture pins the default
ordering together with every component value.

**"Recency" and "expiry" mean last lifecycle activity, not when the lesson was learned.** Both read
`ExperienceRecord.UpdatedAt`, which every lifecycle commit bumps. A years-old lesson reinforced yesterday is one day
old by this measure: it scores as fully recent and never expires. That is deliberate — recent revalidation is
evidence the lesson still holds — but it is not a measure of how old the underlying knowledge is, and a policy that
needs one should not use `MaxAge` for it.

**Bounded, and fail-closed.** The whole call is bounded by `RetrievalPolicy.Timeout` (default 500 ms, maximum one
day), measured with an injected `TimeProvider`.

| Situation | Outcome | Records |
| --- | --- | --- |
| Ran inside the timeout | `Completed` | Every eligible record among the candidates considered, ranked and cut to the request's limit. Check `result.Truncated`: `true` means more matched than were considered |
| Exceeded the timeout | `TimedOut` (`result.TimedOut`), with the request's `CorrelationId` — never an exception | Empty |
| Request scope outside the authorization | `Denied` | Empty; **no search is issued** |
| Search failed, or a candidate could not be read, came back out of scope, or was returned twice | `Failed`, with `result.Failure` | Empty, never unfiltered |
| Caller cancelled | `OperationCanceledException`, unwrapped and distinct from the timeout | — |

`result.Failure.Reason` is content-free and safe to log. `result.Failure.Exception`, when present, is whatever the
port threw — a driver message can quote SQL text or connection detail, so treat it as local diagnostics rather than
something to pass on.

Retrieval returns ranked records and the evidence for their ranking. Building a labeled Historical Reference payload
and injecting it into an agent is a separate, later step, and retrieved content never becomes authority.

## Wiring it all together

Each package registers its own services, so a host never names a concrete type:

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres.DependencyInjection;

services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddAgentExperiencePostgresStore();                     // IExperienceRecordStore
services.AddAgentExperiencePostgresCandidateSource();           // IExperienceCandidateSource
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService, ExperienceFinalizationService
services.AddAgentExperienceRetrieval();                         // ExperienceRetrievalService
// -> defaults to RetrievalPolicy.Default and RankingWeights.Default; pass your own to override
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
  AgentExperience.Core/                     sanitization, capture, verification, reflection, lifecycle transitions, finalization, retrieval
  AgentExperience.MicrosoftAgentFramework/  MAF adapter (pinned Microsoft.Agents.AI 1.20.0)
  AgentExperience.Storage.Postgres/         PostgreSQL Experience Record store, text search, and schema migrator (pinned Npgsql 10.0.3, dbup-postgresql 7.0.1, dbup-core 6.1.1)
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection, lifecycle, retrieval tests
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

Unit and MAF adapter tests run in memory, with no network, database, or model credentials. `AgentExperience.CompatibilityProof` and the `PostgresExperienceRecordStoreTests`, `PostgresExperienceCandidateSourceTests`, `PostgresLifecycleCommitTests`, `PostgresFinalizationTests`, and `ExperienceSchemaMigratorTests` in `AgentExperience.Storage.Postgres.Tests` start a PostgreSQL/pgvector container through Testcontainers, so they need Docker. If Testcontainers' Ryuk container fails to start under your local Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. To skip the container-backed tests:

```bash
dotnet test --filter "FullyQualifiedName!~CompatibilityProof&FullyQualifiedName!~PostgresExperienceRecordStoreTests&FullyQualifiedName!~PostgresExperienceCandidateSourceTests&FullyQualifiedName!~PostgresLifecycleCommitTests&FullyQualifiedName!~PostgresFinalizationTests&FullyQualifiedName!~ExperienceSchemaMigratorTests"
```

## Roadmap

1. **Capture and explain agent experience** ✅ contracts, sanitization, capture, verification, reflection, MAF adapter
2. **Reuse relevant experience:** PostgreSQL persistence, atomic audited lifecycle commits, one-call finalization of captured runs, and bounded text retrieval with explainable ranking (in place), vector and hybrid retrieval, historical-reference injection into MAF
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
