# AgentExperience.Core

> **Preview — not production ready.** This is a `0.1.0-preview` package. Public APIs may change between previews,
> and the [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table in the repository README
> lists every unresolved item. Any unresolved item blocks a production-readiness claim.

The adapter-independent engine of AgentExperience.NET: it turns what an agent observably did into an auditable,
verified lesson, keeps that lesson's lifecycle and confidence honest, and finds it again when similar work comes up.

**Dependencies:** `AgentExperience.Abstractions`, `Microsoft.Extensions.Compliance.Redaction`, and
`Microsoft.Extensions.DependencyInjection.Abstractions` (abstractions only — no container, no hosting). No Microsoft
Agent Framework, EF Core, Npgsql, DbUp, OpenTelemetry SDK, or model-provider package. Telemetry is emitted through the
BCL's `ActivitySource` and `Meter` named `AgentExperience.Core`; the host subscribes and exports. Every span, instrument,
dimension and attribute is listed in the
[telemetry contract](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/telemetry.md).

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

The two that were Core's, KL-5 and KL-6, are resolved by story 5.5, with breaking changes. What KL-6's fix cannot
check is stated at the end and belongs to KL-11 (host-supplied identifiers).

- **KL-5, evidence kind is default-deny.** `RequiredCheck(CheckId, ExpectedKind)` has no default for the kind. A
  check that accepts any kind says so with `RequiredCheck.AnyKind` (`"*"`); a null or blank kind is refused by
  `VerificationAggregator.Aggregate` (and so by finalization) with an `ArgumentException`, and `Accepts` returns
  `false` for it. Migrate `new RequiredCheck("id")` to the kind your evaluator produces (`TaskCheckEvaluators` produce
  `ToolExitCode`, `TestResult`, `WorkflowCompletion`, `HumanApproval` and `HumanCorrection`), or to `AnyKind` if any
  producer really may answer the check.
- **KL-6, an evaluation is bound to its run.** `VerificationAggregator.Aggregate(runId, evidence, ...)` takes the run
  it evaluates and records it, with the closed round, the artifact revision and a copy of the required checks, on
  `VerificationResult.Basis`. `VerificationResult` has no public constructor and no settable property, so only the
  aggregator makes one (and it cannot be deserialized). `ReflectionRequest` throws `ReflectionBindingException` (an `ArgumentException`) when the
  evaluation's basis names a different run, or when the run's own recorded outcome disagrees with it, and its `Run`
  and `Evaluation` are get-only, so no reflector receives a run paired with an evaluation made under another run ID. `ReflectionRequest.EnsureMatches(reflection)` checks a reflector's output against the
  request (identity, run, created-at, verdict, score, rule version, evidence IDs); `ExperienceFinalizationService`
  applies it and quarantines a record whose reflection fails it, exactly as if the reflector had thrown.

  What remains: the binding is as strong as the run ID. The aggregator records the run ID it is given and evidence
  carries none, so whether a round's evidence belongs to that run is the host's statement; a run re-stamped with
  another run's ID is refused only if its own recorded outcome contradicts the evaluation. Both are KL-11's trust. A
  host that calls a reflector and writes records without finalization has only its own call to `EnsureMatches`.

The public surface of this package is pinned by an approval baseline
(`tests/AgentExperience.Release.Tests/PublicApi/`), so any change to it is a reviewed diff.

## More

- Repository and full documentation: <https://github.com/fabbrik/AgentExperience.NET>
- Release procedure and verification checks: `RELEASING.md` in the repository
- License: Apache-2.0
