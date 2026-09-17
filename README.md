# AgentExperience.NET

[![CI](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)

**Portable, evidence-backed experience memory for .NET agents.**

AgentExperience.NET captures what an AI agent actually tried, verifies whether it worked, and turns the result into an auditable lesson that future runs can reuse safely. It sits between [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) execution and durable storage, without replacing either.

> **Status: early development.** Epic 1 (capture and explain agent experience) is implemented and tested. Epic 2 has started: Experience Records can be stored in PostgreSQL. Retrieval, injection, and governance are planned (see [Roadmap](#roadmap)). Nothing is published to NuGet yet, and APIs may change.

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

See the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md) for options, supported agent types, and caveats. See the [PostgreSQL store README](src/AgentExperience.Storage.Postgres/README.md) for the trust boundary, schema script, and data semantics.

## Design principles

- **Hexagonal core.** `Abstractions` and `Core` depend only on the BCL and a redaction primitive. MAF, databases, models, and telemetry stay in adapters. Dependency-boundary tests enforce this in CI.
- **Failure-preserving capture.** Failed and cancelled runs are recorded through an outer lifecycle path, never only a success callback.
- **Evidence before trust.** Verification is deterministic and bound to a host-closed round and artifact revision. A completion score is never mistaken for reuse confidence.
- **Sanitize before anything is stored.** Unknown payload fields are dropped by default, and secrets are redacted from nested values.
- **Reuse, don't rebuild.** MAF middleware and `Microsoft.Extensions.Compliance.Redaction` are used at the edges, and planned storage builds on existing pgvector connectors. Each integration was proven with executable compatibility tests before an adapter was built.

## Repository layout

```
src/
  AgentExperience.Abstractions/             domain contracts and ports (BCL only)
  AgentExperience.Core/                     sanitization, capture, verification, reflection
  AgentExperience.MicrosoftAgentFramework/  MAF adapter (pinned Microsoft.Agents.AI 1.20.0)
  AgentExperience.Storage.Postgres/         PostgreSQL Experience Record store (pinned Npgsql 10.0.3)
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection tests
  AgentExperience.MicrosoftAgentFramework.Tests/  real ChatClientAgent runs against a scripted fake model
  AgentExperience.Storage.Postgres.Tests/   store tests, mostly against a PostgreSQL container
  AgentExperience.CompatibilityProof/       executable proofs for MAF hooks, context providers, pgvector, redaction
docs/                                       original production architecture research
_bmad-output/                               product brief, PRD, architecture, epics, and specs
```

## Build and test

Requires the [.NET SDK 10.0.302](https://dotnet.microsoft.com/) or a later feature band (see `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

Unit and MAF adapter tests run in memory, with no network, database, or model credentials. `AgentExperience.CompatibilityProof` and the `PostgresExperienceRecordStoreTests` in `AgentExperience.Storage.Postgres.Tests` start a PostgreSQL/pgvector container through Testcontainers, so they need Docker. If Testcontainers' Ryuk container fails to start under your local Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. To skip the container-backed tests:

```bash
dotnet test --filter "FullyQualifiedName!~CompatibilityProof&FullyQualifiedName!~PostgresExperienceRecordStoreTests"
```

## Roadmap

1. **Capture and explain agent experience** ✅ contracts, sanitization, capture, verification, reflection, MAF adapter
2. **Reuse relevant experience:** PostgreSQL persistence (Experience Record store in place), hybrid text and vector retrieval, historical-reference injection into MAF
3. **Govern experience safely:** sharing grants, audited lifecycle transitions, evidence-based confidence updates
4. **Operate and measure the learning loop:** OpenTelemetry instrumentation, an end-to-end demo, measured reuse against a baseline, data deletion and expiry

Full requirements and acceptance criteria are in [`_bmad-output/planning-artifacts/epics.md`](_bmad-output/planning-artifacts/epics.md).

## How this project is built

Development is spec-driven with the [BMAD Method](https://github.com/bmad-code-org/BMAD-METHOD) and AI-assisted implementation. The planning trail is versioned alongside the code:

- **Product brief, PRD, architecture, and epics:** [`_bmad-output/planning-artifacts/`](_bmad-output/planning-artifacts/)
- **MVP spec and reuse-boundary decisions:** [`_bmad-output/specs/`](_bmad-output/specs/)

Each story is planned against the architecture, implemented against explicit acceptance criteria, and then reviewed by independent adversarial, edge-case, and verification-gap passes before it is committed.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). To report a vulnerability, follow [SECURITY.md](SECURITY.md).

## License

[Apache-2.0](./LICENSE)
