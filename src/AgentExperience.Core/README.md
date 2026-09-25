# AgentExperience.Core

> **Preview — not production ready.** This is a `0.1.0-preview` package. Public APIs may change between previews,
> and the [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table in the repository README
> lists every unresolved item. Any unresolved item blocks a production-readiness claim.

The adapter-independent engine of AgentExperience.NET: it turns what an agent observably did into an auditable,
verified lesson, keeps that lesson's lifecycle and confidence honest, and finds it again when similar work comes up.

**Dependencies:** `AgentExperience.Abstractions`, `Microsoft.Extensions.Compliance.Redaction`, and
`Microsoft.Extensions.DependencyInjection.Abstractions` (abstractions only — no container, no hosting); on `net8.0`
only, also `System.Text.Json` and `Microsoft.Bcl.Memory` 10.0.12 or later, which supply APIs the .NET 8 shared
framework lacks (`net9.0` and `net10.0` have them built in). No Microsoft
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
| `ExperienceLifecycleService` | The audited transition table, and evidence-based confidence updates whose independence key is verified |
| `AssessmentTokenIssuer` | Mints the HMAC assessment tokens a human assessment must present to move a score (never registered in DI: whoever can call it can mint) |
| `ExperienceIndexingService` | Embedding ingestion after the canonical commit; derived data never blocks canonical data |
| `ExperienceRetrievalService` | Bounded, fail-closed text and hybrid retrieval with explainable ranking |
| `ExperienceReuseFeedbackService` | Records what a run was exposed to, and only lets established evidence move a score |

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

Every registration uses `TryAdd`, so a host's own implementation wins. The storage ports come from an adapter such as
`AgentExperience.Storage.Postgres`. The lifecycle service verifies confidence independence by default and counts the
registered capture service's runs as known; `ExperienceIndependenceOptions` carries the assessment token key, the token
lifetime, the clock, and the opt-out (see KL-11 below).

## What Core never does

- **It never decides policy on the host's behalf.** Authorization (`AuthorizationContext`), the storage decision, and
  the injection risk decision all travel in the request; Core obeys them.
- **It never throws for an expected outcome.** Finalization, retrieval, indexing, and feedback return structured
  results; retrieval is bounded by a timeout that is never an exception.
- **It never makes a completion score into reuse confidence.** Confidence is a versioned `(1 + S) / (2 + S + F)`
  heuristic over independent evidence — useful for ranking, not a calibrated probability.

## Known limits that live here

The two that were Core's, KL-5 and KL-6, are resolved by story 5.5, with breaking changes. What KL-6's fix cannot
check is stated at the end and belongs to KL-11 (host-supplied identifiers), which story 6.6 narrows to an opt-out.

- **KL-11, an independence key's inputs are verified (story 6.6).** `ExperienceLifecycleService.ApplyEvidenceAsync`,
  and so every attributed feedback submission, checks the identifiers before anything is computed or written, and
  refuses a failure with `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal` saying which:
  - **The run** must be one the library knows *in the evidence's scope*: a record finalization derived for it there
    (read through the ordinary scoped `GetAsync`; never through a grant, never a tombstone), or a run the wired
    `IExperienceCaptureService` holds there. The scope is the evidence's own because retrieval is exact-scope and a
    grant never confers writing, so no other run could have been exposed to a record that accepts evidence. A run
    that is the record's own `SourceRunId` is `OwnRun`, in every mode.
  - **The round** (machine evidence) must be the one finalization closed for that run. Finalization now stamps
    `ExperienceRecord.ClosedRoundId` from the evaluation's `VerificationBasis.ClosedRound`; a run with no closed
    round, one only the capture service holds, or any other round is `UnknownRound`. One run yields at most one
    machine key per record.
  - **The assessment** (human evidence) must present an `AssessmentToken` minted by `AssessmentTokenIssuer` under
    `ExperienceIndependenceOptions.AssessmentTokenKey`: HMAC-SHA256 over its ID, issue time, direction and records,
    and over the scope, run and reviewer the verifier supplies. It is compared in constant time before anything it
    claims is read, expires after `AssessmentTokenLifetime` (default a day), covers named records only, and is
    spent once per record by the store, atomically with the evidence (an identical retry still replays). A random
    GUID, a token for another scope, run, reviewer, direction or record, an expired one and a replayed one are all
    refused. Feedback checks the same before writing its ledger, and degrades a failing attribution to benefit
    `Unknown` rather than losing the exposure.

  `IndependenceVerification.TrustHostSuppliedIdentifiers` is the opt-out: the previous behaviour, keeping only the
  own-run rule. **Breaking:** evidence naming an unknown run, a round finalization did not close, or no valid token
  is now refused by default (`Unverified`), including evidence about runs finalized before this version, whose
  records carry no `ClosedRoundId`; evidence naming the record's own run is refused in every mode; a human
  assessment without a token is recorded with benefit `Unknown`; `ConfidenceUpdateOutcome` gains `Unverified` (an
  exhaustive switch needs the arm); `ApplyConfidenceEvidenceResult`, `ApplyConfidenceEvidenceRequest` and
  `HumanReuseAssessment` gain a trailing optional parameter each (a binary break, and a source break for positional
  deconstruction); and an `IExperienceRecordStore` implementation must now persist `ExperienceRecord.ClosedRoundId`
  and spend `ConfidenceUpdate.AssessmentId` once per record — single use is the store's guarantee, which the
  PostgreSQL store makes and a store that ignores the field does not.

  What remains, in the default mode: a run is proven *real and in scope*, not *exposed to the record*, so a caller
  that can choose among real runs can cite one that never saw the lesson -- once per run per key, not once per call.
  Those runs are easy to find: every record in the scope names its `SourceRunId` and `ClosedRoundId`, a run counts
  whatever its own verification concluded (a failed or quarantined run's round vouches too), and a run the
  in-memory capture service holds stays known for as long as it is held. The round is the one the host closed at
  finalization. A record written by hand through `CreateAsync` vouches for its own `SourceRunId` and
  `ClosedRoundId`. Anyone with the key, or with code that calls the issuer, can mint. Verification runs before the
  store's replay check, so a lost-acknowledgement retry made after its token expired (or its key rotated, or its
  run stopped being known) is refused although the original landed, and a feedback retry then degrades and
  conflicts with its own stored row; retry within the token's lifetime.

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
