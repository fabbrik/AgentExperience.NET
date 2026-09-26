# AgentExperience.NET guide

These pages explain each part of the library in depth. Start with the [root README](../../README.md) for what the
project is and a quick start; come here when you need the exact behaviour, the options, or the caveats.

AgentExperience.NET is a **preview**. Read [Known limits and documented boundaries](../known-limits.md) before you
rely on any guarantee described here.

These pages describe the `main` branch. A few things on `main` are not yet in the published `0.1.0-preview.2`
package (for example `ExperienceInjectionOptions.SessionStateKey` and the stripping of invisible characters from
tool names); the [changelog's Unreleased section](../../CHANGELOG.md#unreleased) lists them. Code snippets use
placeholder variables (`authorization`, `scope`, `hostScope`, `sanitizationOptions`, `captureLimits`, `logger`, …)
for values you build as in the [quick start](../../README.md#quick-start).

## The learning loop, page by page

| Step | What happens | Page |
| --- | --- | --- |
| Capture | Record what an agent tried: its attempts, tool calls, results and errors, sanitized before anything is kept | [Capturing runs with MAF](capture.md) |
| Verify and reflect | Check the run against your own required checks, and draw a lesson that points at its evidence | [Finalization](finalization.md) |
| Store | Persist the lesson as an Experience Record, then move it through an audited lifecycle | [Finalization](finalization.md), [Lifecycle](lifecycle.md) |
| Index | Optionally embed the record, after it is stored, for search by meaning | [Indexing](indexing.md) |
| Retrieve | Find the records that apply to a new task, filtered for eligibility and ranked with every weight shown | [Retrieval](retrieval.md) |
| Inject | Put what survives into the agent's context as one labeled Historical Reference block | [Injection into MAF](injection.md) |
| Feedback | Record what a run was given, and let only real evidence move a record's confidence | [Reuse feedback](reuse-feedback.md), [Confidence and independence](confidence.md) |

And around the loop:

| Topic | Page |
| --- | --- |
| Letting another team read one record, for a bounded time, with an audit trail | [Sharing and grants](sharing.md) |
| Deleting a record, and expiring old data on your own schedule | [Deletion and retention](deletion-and-retention.md) |
| Making erasure reach backups, replicas and WAL | [Crypto-shredding](crypto-shredding.md) |
| Wiring the services, the two database roles, and the trust boundary | [Deployment](deployment.md) |
| The PostgreSQL schema, script by script, and how it is applied | [PostgreSQL schema](postgres-schema.md) |
| Spans and metrics | [Telemetry contract](../telemetry.md) |
| The tests behind the security claims | [Security suite](../security-suite.md) |
| Supported versions, and the evidence for every dependency pin | [Compatibility evidence](../compatibility-evidence.md) |
| How a release is verified and published | [RELEASING.md](../../RELEASING.md) |

## Glossary

These terms have a specific meaning in this library.

| Term | Meaning |
| --- | --- |
| **Experience Run** | One attempt, or a series of retries, at one task by an agent, as the library captured it: the attempts, the tool calls with their sanitized arguments and results, errors, and the run's execution status. A run lives in memory until it is finalized. |
| **Attempt** | One try inside a run. With the MAF adapter, one agent invocation is one attempt. |
| **Evidence** | An observable fact that decides whether a check passed: an exit code, a test result, a workflow completion, a human approval. Never the model's own claim, and never hidden reasoning. |
| **Verification round** | A set of evidence the host closed for one artifact revision (for example, "the tests that ran against build 42"). Verification only counts evidence from the round the host closed. |
| **Reflection** | The lesson drawn from a verified run, with reuse guidance, preconditions and warnings. Every reflection points at the evidence IDs it rests on. |
| **Experience Record** | The durable, stored lesson: the run, its verification, its reflection, its scope, its lifecycle status and its reuse confidence. |
| **Scope** | Where a record lives: tenant, application and project (required), and optionally team, agent and user. Matching is exact; a null field is not a wildcard. (Two deliberate exceptions: a null *bound* on an authorization context leaves that field unrestricted, and a subtree retention sweep treats a null root field as "any".) |
| **Authorization context** | What the host's own authentication says the caller may touch. It always comes from the host, never from a request payload or model output. Every operation checks the scope against it first. |
| **Eligible** | A record in `Validated` or `Reinforced` status. Only eligible records are retrieved, injected or indexed. |
| **Reuse confidence** | A score in (0, 1), `(1 + S) / (2 + S + F)` over independent supporting validations `S` and contradictions `F`. A heuristic for ranking, not a probability. |
| **Historical Reference** | The single, delimited, labeled message the MAF adapter injects before an invocation. It carries retrieved lessons as untrusted reference material, never as instructions. |
| **Exposure** | The fact that the library delivered a record into a run. Confidence evidence about a run counts only for records the run was exposed to. |
| **Sharing grant** | An administrator's explicit, expiring permission for one other scope to *read* one record. |
| **Tombstone** | What is left of a deleted record: its ID, scope, revision and deletion time, and nothing else. The ID can never be reused. |
| **Crypto-shredding** | An opt-in mode where each record's text is encrypted under its own key, held outside the database. Deleting the record destroys the key, so every copy becomes unreadable. |

## Design principles

- **Hexagonal core.** `Abstractions` depends only on the BCL; `Core` adds a redaction primitive and the
  dependency-injection *abstractions* it needs to register its own services. MAF, databases, models, and telemetry
  stay in adapters. Dependency-boundary tests enforce this in CI.
- **Failure-preserving capture.** Failed and cancelled runs are recorded through an outer lifecycle path, never only
  a success callback.
- **Evidence before trust.** Verification is deterministic and bound to a host-closed round and artifact revision. A
  completion score is never mistaken for reuse confidence.
- **Sanitize before anything is stored.** Unknown payload fields are dropped by default, and secrets are redacted
  from nested values.
- **Reuse, don't rebuild.** MAF middleware and `Microsoft.Extensions.Compliance.Redaction` are used at the edges, and
  storage builds on Npgsql and pgvector rather than on a bespoke engine. Each integration was proven with executable
  compatibility tests before an adapter was built.
- **Derived data never blocks canonical data.** Embeddings are produced after the commit, through a replaceable
  provider port, and every failure leaves the record committed, text-searchable, and retryable.
- **Hidden reasoning is never stored.** The library records observable evidence (tool calls, results, errors,
  verification checks) and never chain-of-thought.

## What each package provides

| Capability | Package |
| --- | --- |
| Domain contracts: experience runs, attempts, evidence, outcomes, reflections, scope, environment, and every port | `AgentExperience.Abstractions` |
| Sanitization before storage: per-kind allowlists, secret redaction, fail-closed rejection | `AgentExperience.Core` |
| Thread-safe in-memory run capture with idempotent appends and size limits | `AgentExperience.Core` |
| Deterministic task verification: exit codes, tests, workflow and human checks; host-closed rounds; no LLM | `AgentExperience.Core` |
| Auditable, template-based reflections traceable to evidence IDs | `AgentExperience.Core` |
| One finalization call: evaluate, gate on authorization and the host's storage decision, reflect, create the record, commit its first lifecycle event — replay-safe | `AgentExperience.Core` |
| The audited lifecycle transition table, with supersession and database-enforced append-only logs | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Evidence-based reuse confidence with verified, exposure-bound independence | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Reuse feedback that records exposure and lets only established evidence move a score | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Text retrieval and hybrid (text plus vector) retrieval, bounded by a timeout, with explainable ranking | `AgentExperience.Core`, `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |
| Embedding ingestion after the canonical commit, conditional on the record's revision | `AgentExperience.Core`, `AgentExperience.Storage.Postgres.Vectors` |
| MAF adapter: capture of ordinary, streaming, failed and cancelled runs and their tool calls, retries as attempts of one run, and Historical Reference injection with session tracking | `AgentExperience.MicrosoftAgentFramework` |
| Sharing grants with a bounded lifetime, disclosure levels, and an optional access log | `AgentExperience.Abstractions`, `AgentExperience.Storage.Postgres` |
| Deletion, retention sweeps and purges, with append-only guards that are never disabled | `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |
| Opt-in crypto-shredding with an envelope key store for any KMS | `AgentExperience.Abstractions`, `AgentExperience.Core`, `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |
| A supported two-role PostgreSQL deployment, with privileges applied and verified on every deploy | `AgentExperience.Storage.Postgres` |
| Journaled schema migrations, one transaction per script, serialized across processes | `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |
| OpenTelemetry-compatible spans and metrics through the BCL's `ActivitySource` and `Meter` | `AgentExperience.Core`, `AgentExperience.MicrosoftAgentFramework`, `AgentExperience.Storage.Postgres` |
| Dependency-injection registration for each package; every registration uses `TryAdd`, so your own implementation wins | all but `AgentExperience.Abstractions` |
