# AgentExperience.Core

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

The engine of [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET). It turns what an agent observably
did into a verified, auditable lesson, keeps that lesson's lifecycle and confidence honest, and finds it again when
similar work comes up. It does not talk to a database or a model itself: storage comes from an adapter such as
`AgentExperience.Storage.Postgres`, and agent integration from `AgentExperience.MicrosoftAgentFramework`.

**Dependencies:** `AgentExperience.Abstractions`, `Microsoft.Extensions.Compliance.Redaction`, and
`Microsoft.Extensions.DependencyInjection.Abstractions` (abstractions only — no container, no hosting); on `net8.0`
only, also `System.Text.Json` and `Microsoft.Bcl.Memory` 10.0.12 or later, which supply APIs the .NET 8 shared
framework lacks. No Microsoft Agent Framework, EF Core, Npgsql, DbUp, OpenTelemetry SDK, or model-provider package.
Targets `net8.0`, `net9.0` and `net10.0`.

## What is in it

| Service | What it does | Guide |
| --- | --- | --- |
| `DefaultSanitizer` | Per-kind allowlists, secret-field redaction, recursive traversal, fail-closed rejection — before anything is stored | [Capture](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/capture.md#sanitization-happens-at-capture) |
| `InMemoryExperienceCaptureService` | Thread-safe run capture with idempotent appends and size limits | [Capture](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/capture.md) |
| `VerificationAggregator`, `TaskCheckEvaluators` | Deterministic task verification bound to a host-closed round; no LLM | [Finalization](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/finalization.md#verifying-a-run-and-binding-its-evaluation) |
| `DefaultExperienceReflector` | Template-based reflections traceable to evidence IDs (`IExperienceReflector` is the seam for your own) | [Finalization](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/finalization.md) |
| `ExperienceFinalizationService` | One call from a completed run to a durable record: evaluate, gate, reflect, create, commit — replay-safe | [Finalization](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/finalization.md) |
| `ExperienceLifecycleService` | The audited transition table, and evidence-based confidence updates whose independence key is verified | [Lifecycle](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/lifecycle.md), [Confidence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/confidence.md) |
| `AssessmentTokenIssuer` | Mints the HMAC assessment tokens a human assessment must present to move a score (never registered in DI: whoever can call it can mint) | [Confidence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/confidence.md#the-keys-inputs-are-verified) |
| `ExperienceIndexingService` | Embedding ingestion after the canonical commit; derived data never blocks canonical data | [Indexing](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/indexing.md) |
| `ExperienceRetrievalService` | Bounded, fail-closed text and hybrid retrieval with explainable ranking | [Retrieval](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/retrieval.md) |
| `ExperienceReuseFeedbackService` | Records what a run was exposed to, and lets only established evidence move a score — for records the run was actually given | [Reuse feedback](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/reuse-feedback.md) |
| `EnvelopeExperienceKeyStore` | Per-record data keys wrapped by your KMS key, for crypto-shredding | [Crypto-shredding](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/crypto-shredding.md) |

## Wiring it

```csharp
using AgentExperience.Core.DependencyInjection;

services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService, ExperienceFinalizationService
services.AddSingleton(new ExperienceIndependenceOptions
{
    AssessmentTokenKey = secrets.AssessmentTokenKey,   // >= 32 random bytes, from your secret store
});                                                     // optional: without it, human evidence is refused
// AssessmentTokenIssuer is deliberately not registered: construct it in your review flow, where a person decides.
services.AddAgentExperienceIndexing();       // optional: needs an IExperienceEmbeddingIndex and generator
services.AddAgentExperienceRetrieval();      // ExperienceRetrievalService
services.AddAgentExperienceReuseFeedback();  // needs an IExperienceReuseFeedbackStore
```

Every registration uses `TryAdd`, so a host's own implementation wins. The storage ports come from an adapter. The
lifecycle service verifies confidence independence by default and counts the registered capture service's runs as
known; `ExperienceIndependenceOptions` carries the assessment token key, the token lifetime (a day by default), the
clock, and the opt-out. The full wiring for every package is in
[Deployment](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/deployment.md#wiring-it-all-together).

## What Core never does

- **It never decides policy on the host's behalf.** Authorization (`AuthorizationContext`), the storage decision, and
  the injection risk decision all travel in the request; Core obeys them.
- **It never throws for an expected outcome.** Finalization, retrieval, indexing, and feedback return structured
  results; retrieval is bounded by a timeout (500 ms by default) that is never an exception.
- **It never makes a completion score into reuse confidence.** Confidence is a versioned `(1 + S) / (2 + S + F)`
  heuristic over independent evidence — useful for ranking, not a calibrated probability.
- **It never stores hidden reasoning.** Only observable evidence, sanitized at capture.

## Limits to know

Confidence independence is the one documented boundary that lives mostly in this package (KL-11): verification
proves a run was *given* a lesson, not that the lesson mattered, and the library believes the host's own bookkeeping
and key custody. `IndependenceVerification.TrustHostSuppliedIdentifiers` turns verification off for a host that
cannot adopt it; what it admits is stored as `HostTrusted` and can be excluded on read. Read
[Confidence and independence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/confidence.md)
before relying on a score.

Telemetry is emitted through the BCL's `ActivitySource` and `Meter` named `AgentExperience.Core`; the host subscribes
and exports. Every span, instrument, dimension and attribute is in the
[telemetry contract](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/telemetry.md).

The public surface of this package is pinned by an approval baseline in the repository, so any change to it is a
reviewed diff. Breaking changes between previews are listed in the
[changelog](https://github.com/fabbrik/AgentExperience.NET/blob/main/CHANGELOG.md).

## More

- Documentation: [guide and glossary](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/README.md)
- License: Apache-2.0
