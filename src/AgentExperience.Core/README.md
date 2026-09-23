# AgentExperience.Core

> **Preview — not production ready.** This is a `0.1.0-preview` package. Public APIs may change between previews,
> and the [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table in the repository README
> lists every unresolved item. Any unresolved item blocks a production-readiness claim.

The adapter-independent engine of AgentExperience.NET: it turns what an agent observably did into an auditable,
verified lesson, keeps that lesson's lifecycle and confidence honest, and finds it again when similar work comes up.

**Dependencies:** `AgentExperience.Abstractions`, `Microsoft.Extensions.Compliance.Redaction`, and
`Microsoft.Extensions.DependencyInjection.Abstractions` (abstractions only — no container, no hosting). No Microsoft
Agent Framework, EF Core, Npgsql, DbUp, OpenTelemetry SDK, or model-provider package. Telemetry is emitted through the
BCL's `ActivitySource` and `Meter` named `AgentExperience.Core`; the host subscribes and exports.

## What is in it

| Service | What it does |
| --- | --- |
| `DefaultSanitizer` | Per-kind allowlists, secret-field redaction, recursive traversal, fail-closed rejection — before anything is stored |
| `InMemoryExperienceCaptureService` | Thread-safe run capture with idempotent appends and size limits |
| `VerificationAggregator`, `TaskCheckEvaluators` | Deterministic task verification bound to a host-closed round; no LLM |
| `DefaultExperienceReflector` | Template-based reflections traceable to evidence IDs (`IExperienceReflector` is the seam for your own) |
| `ExperienceFinalizationService` | One call from a completed run to a durable record: evaluate, gate, reflect, create, commit — replay-safe |
| `ExperienceLifecycleService` | The audited transition table, and evidence-based confidence updates |
| `ExperienceIndexingService` | Embedding ingestion after the canonical commit; derived data never blocks canonical data |
| `ExperienceRetrievalService` | Bounded, fail-closed text and hybrid retrieval with explainable ranking |
| `ExperienceReuseFeedbackService` | Records what a run was exposed to, and only lets established evidence move a score |

## Wiring it

```csharp
using AgentExperience.Core.DependencyInjection;

services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService, ExperienceFinalizationService
services.AddAgentExperienceIndexing();       // optional: needs an IExperienceEmbeddingIndex and generator
services.AddAgentExperienceRetrieval();      // ExperienceRetrievalService
services.AddAgentExperienceReuseFeedback();  // needs an IExperienceReuseFeedbackStore
```

Every registration uses `TryAdd`, so a host's own implementation wins. The storage ports come from an adapter such as
`AgentExperience.Storage.Postgres`.

## What Core never does

- **It never decides policy on the host's behalf.** Authorization (`AuthorizationContext`), the storage decision, and
  the injection risk decision all travel in the request; Core obeys them.
- **It never throws for an expected outcome.** Finalization, retrieval, indexing, and feedback return structured
  results; retrieval is bounded by a timeout that is never an exception.
- **It never makes a completion score into reuse confidence.** Confidence is a versioned `(1 + S) / (2 + S + F)`
  heuristic over independent evidence — useful for ranking, not a calibrated probability.

## Known limits that live here

Two limits from the repository's [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table are
Core's:

- A `RequiredCheck` with a null `ExpectedKind` accepts evidence of any kind, so default-deny on evidence kind is
  opt-in per check.
- `ExperienceFinalizationService` computes the evaluation a reflection is built from, so the two always match. A host
  that calls `IExperienceReflector` directly can still pair a reflection with the wrong evaluation.

The public surface of this package is pinned by an approval baseline
(`tests/AgentExperience.Release.Tests/PublicApi/`), so any change to it is a reviewed diff.

## More

- Repository and full documentation: <https://github.com/fabbrik/AgentExperience.NET>
- Release procedure and verification checks: `RELEASING.md` in the repository
- License: Apache-2.0
