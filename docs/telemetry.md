# Telemetry contract

This is the operator-facing contract for everything AgentExperience.NET emits: source and meter names, span names,
instruments, units, dimensions and their allowed values, span attributes, and what each operation emits. The
definitions in the source are authoritative:

- Core: `src/AgentExperience.Core/Diagnostics/ExperienceDiagnostics.cs`, `ExperienceOperationNames.cs` and
  `ExperienceOperationErrorClass.cs`;
- MAF adapter: `src/AgentExperience.MicrosoftAgentFramework/Diagnostics/InjectionDiagnostics.cs`;
- PostgreSQL storage adapter: `src/AgentExperience.Storage.Postgres/Diagnostics/ErasureDiagnostics.cs`.

These tests pin the names in code, so renaming anything listed here fails the build rather than silently
re-labelling a dashboard:

- `ExperienceTelemetryTests` (the frozen operation table, the exact instrument set, the exact dimension and span
  attribute sets, and the error classification);
- `InjectionTelemetryTests`;
- `ErasureTelemetryTests`;
- `DiagnosticsAgreementTests` and `ErasureDiagnosticsAgreementTests` (each adapter restates Core's wire names, and
  these assert that each copy agrees with Core).

> **Superseded names.** §24 of [`AgentExperience_NET_MAF_Production_Architecture.md`](AgentExperience_NET_MAF_Production_Architecture.md)
> is the pre-implementation research proposal. Its span names (`agentexperience.rank`, `agentexperience.persist`, …)
> and per-operation metric names (`agentexperience.retrieve.count`, …) were never shipped. This document is what
> ships.

## How a host subscribes

The library emits and the host exports. Nothing in the library constructs a tracer or meter provider, an exporter, an
`ActivityListener` or a `MeterListener`, and no library project references an OpenTelemetry package. With the
OpenTelemetry SDK:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("AgentExperience.*"))
    .WithMetrics(metrics => metrics.AddMeter("AgentExperience.*"));
```

With no subscriber, no `Activity` is allocated and no measurement is recorded. An operation returns exactly the same
result whether or not anything is listening, and whether or not a sampler accepts its span. A failure inside
telemetry, such as a listener callback that throws, is swallowed and never reaches the caller.

## Sources and meters

| Name | Kind | Emitted by | Operations |
| --- | --- | --- | --- |
| `AgentExperience.Core` | `ActivitySource` and `Meter` | `AgentExperience.Core` | the thirteen Core operations below |
| `AgentExperience.MicrosoftAgentFramework` | `ActivitySource` and `Meter` | `AgentExperience.MicrosoftAgentFramework` | `inject` only |
| `AgentExperience.Storage.Postgres` | `ActivitySource` and `Meter` | `AgentExperience.Storage.Postgres` | `delete`, `retention.sweep`, `grant.purge`, `grant.access.purge` (erasure and retention) and `record.seal` (the crypto-shredding upgrade) only |

- **One source and meter per assembly.** A text-only host can subscribe to Core without pulling in the adapters, and
  the `AgentExperience.*` wildcard subscribes to all three.
- **Why erasure emits from the storage adapter.** Erasure is a capability of the PostgreSQL adapter, not of a
  Core port, so no Core operation wraps it. The adapter references `AgentExperience.Abstractions` only, and the
  BCL's `ActivitySource` and `Meter` let it emit without taking a Core reference. Like the MAF adapter, it restates
  Core's wire names, and a test asserts that they agree. None of the adapter's other operations (create, get,
  search, lifecycle commits, grants, feedback) is instrumented on this source. Those surface through the Core
  operations that call them.
- **Version.** Each source and meter carries its assembly's informational version, so a signal can be traced back to
  the build that produced it.
- **Lifetime.** Each source and meter is a `static readonly` field created on first use and never disposed. It lives
  as long as the process, or the `AssemblyLoadContext` that loaded the assembly. If a host loads the library into a
  collectible `AssemblyLoadContext`, these instances outlive the unload.
- **What the adapter does not emit.** The MAF adapter opens no span around `RunAsync` or `RunStreamingAsync`, and no
  agent, model or tool span. Those are MAF's to emit. The capture wrapper only *reads* `Activity.Current`, so that a
  captured run can carry the host's trace ID as its provenance correlation. The Core operations the adapter calls
  (`capture.*`, `finalize`, `retrieve`) emit on the Core source.

## Span names

Every span name is `agentexperience.` followed by the operation's `operation` value, for example
`agentexperience.lifecycle.commit`. Every span is `ActivityKind.Internal`.

Status:

- A span whose operation **returned** is `Ok`. This includes a refusal such as `Denied`, `NotFound` or `Deleted` (on
  a write to an erased record): a refusal is an answer, not a failure.
- A span whose operation **threw** is `Error`, and its `outcome` is `Faulted`.

## Instruments

Every meter publishes exactly these three instruments and no others.

| Instrument | Type | Unit | Recorded | Dimensions |
| --- | --- | --- | --- | --- |
| `agentexperience.operation.count` | `Counter<long>` | `{operation}` | exactly once per operation, returned or thrown | `operation`, `outcome`, `nested` |
| `agentexperience.operation.duration` | `Histogram<double>` | `s` (wall-clock seconds) | exactly once per operation, returned or thrown | `operation`, `outcome`, `nested` |
| `agentexperience.operation.failures` | `Counter<long>` | `{failure}` | only when the operation **threw** | `operation`, `error.class`, `nested` |

A returned refusal never increments `agentexperience.operation.failures`. Alert on that counter, not on refusal
outcomes.

## Metric dimensions

These four are the only metric dimensions, and each is a closed set. Identifiers are unbounded, so they appear on
spans only (see [Span attributes](#span-attributes)) and never as a metric dimension.

| Dimension | Values |
| --- | --- |
| `operation` | one of the nineteen values in [Operations](#operations) |
| `outcome` | an enum member name from that operation's own outcome enum, verbatim, or `Faulted` when it threw. See [Operations](#operations) |
| `error.class` | `Cancelled`, `Timeout`, `Infrastructure`, `Unexpected`. See [`error.class`](#errorclass). Only on `agentexperience.operation.failures` |
| `nested` | `true` or `false` (a boolean). See [`nested`](#nested) |

**`outcome` is not unique across operations.** `verify` and `reflect` have no outcome enum of their own and both
report `TaskVerificationStatus`, so they share one value space (`Unknown`, `Verified`, `Failed`). Other operations
also reuse member names such as `Denied`, `Conflict` or `Failed`. `Deleted` even means opposite things: on
`delete`, `retention.sweep`, `grant.purge` and `grant.access.purge` it is the success outcome, while on `lifecycle.commit` and
`confidence.apply` it is a refusal. Always slice `outcome` together with `operation`.

### `error.class`

`error.class` is the public enum `AgentExperience.Core.Diagnostics.ExperienceOperationErrorClass`. It is public
because it is this contract's documented set of dimension values, and it is part of the checked-in public API
baseline. The library emits it; no public method consumes it.

| Value | When | Alert? |
| --- | --- | --- |
| `Cancelled` | The caller's own cancellation token was cancelled | Normally not: the caller asked for it |
| `Timeout` | A `TimeoutException`, or a cancellation caused by a deadline this library imposed on an inner step, such as the post-commit indexing and de-indexing budgets | Yes: a dependency is slow |
| `Infrastructure` | `ExperienceStoreException`, or an `OperationCanceledException` that no token asked for (typically a driver-side or HTTP-client timeout) | Yes: this is the "memory layer is down" signal |
| `Unexpected` | Anything else, such as an argument exception or an unrecognized exception type. On `inject` it also covers an injection that exited without reporting, which is a library bug | Yes: investigate as a bug |

Only the host's token makes a cancellation `Cancelled`. For a nested operation, "the host's token" is the one handed
to the outermost operation, so a budget this library imposed that expires inside a finalization is `Timeout`, not
`Cancelled`.

The storage adapter cannot see Core's enum, because it references `AgentExperience.Abstractions` only. It writes the
same four member names as strings, from the same classification table, and `ErasureDiagnosticsAgreementTests`
asserts that the two agree arm for arm.

### `nested`

`nested` is `false` when the host called the operation directly, and `true` when another instrumented operation in
the **same assembly** called it. Nesting is read from ambient `AsyncLocal` state, and each assembly keeps its own:

- In Core, `sum by (operation)` over `nested=false` is what the host asked Core to do. The unfiltered sum is
  everything Core did.
- `inject` is always `nested=false`, because only the agent pipeline calls it.
- `delete`, `retention.sweep`, `grant.purge` and `grant.access.purge` are always `nested=false`. None of them calls another operation that
  the storage adapter instruments, and nothing in the library calls them. A sweep erases each record through the
  store's private erasure step, not through `DeleteAsync`, so one sweep call is one `retention.sweep` span and never
  a `delete` per record.
- **Nesting does not cross assemblies.** The `retrieve` that an `inject` performs reports `nested=false` on the Core
  meter, because from Core's side the adapter is the host. Summing `nested=false` across both meters therefore counts
  the injection *and* the retrieval inside it. The Core meter alone still answers "what was asked of Core".

Spans need no `nested` attribute, because a span already records its parent.

## Span attributes

These are the only span attributes the library writes.

| Attribute | Value | Written by |
| --- | --- | --- |
| `agentexperience.operation` | the `operation` value | every span |
| `agentexperience.outcome` | the `outcome` value, including `Faulted` | every span |
| `agentexperience.error.class` | an `error.class` value | a span that threw, or an unreported `inject` |
| `error.type` | the exception's type full name, and nothing else from the exception | a span that threw |
| `agentexperience.run_id` | GUID | `capture.start_run`, `capture.append_attempt`, `capture.complete_run`, `reflect`, `finalize`, `reuse_feedback` |
| `agentexperience.attempt_id` | GUID | `capture.append_attempt` |
| `agentexperience.event_id` | GUID | `capture.complete_run` (the completion event), `lifecycle.commit`, `confidence.apply` |
| `agentexperience.experience_id` | GUID | `lifecycle.commit`, `confidence.apply`, `index`, `deindex`, `delete`; `finalize` when its result carries a record |
| `agentexperience.reflection_id` | GUID | `reflect` |
| `agentexperience.feedback_id` | GUID | `reuse_feedback` |
| `agentexperience.stage` | a `FinalizationStage` member: `Load`, `Evaluate`, `Authorize`, `Reflect`, `CreateRecord`, `CommitInitialEvent` | `finalize`, on every outcome, and on a throw (the stage it had reached) |
| `agentexperience.confidence.admission` | a `ConfidenceEvidenceAdmission` member: `Verified` or `HostTrusted` — which verification mode admitted the evidence | `confidence.apply`, only on `Applied` (including a duplicate, and a replay, which reports the admission the original was stored with). Absent on every other outcome, and on a replay of evidence stored with no admission |
| `agentexperience.independence.refusal` | an `IndependenceRefusal` member, such as `UnknownRun`, `NotExposed`, `HostWrittenRun` or `AssessmentTokenInvalid` | `confidence.apply`, only on `Unverified` |
| `agentexperience.correlation_id` | the host-supplied correlation identifier, verbatim | `retrieve`, `inject`; omitted when the host supplied none |
| `agentexperience.omitted_count` | integer: how many ranked records the injection left out. The omission reasons stay on the typed result | `inject` |
| `agentexperience.retracted_count` | integer: how many withdrawal notices the injected block carried, for records delivered earlier in the session. The IDs stay on the typed result | `inject`, only when the count is not zero (session tracking) |
| `agentexperience.erased_count` | integer: how many records the sweep erased, how many grants the purge removed, or how many access rows the access purge removed. Which ones stays in the database | `retention.sweep`, `grant.purge`, `grant.access.purge`, on every returned result (0 on a refusal); `retention.sweep` also when it threw `ExperienceRetentionSweepInterruptedException`, taken from its `Partial` |
| `agentexperience.interrupted` | boolean: whether the sweep stopped before the end of its batch | `retention.sweep`, where `erased_count` is written |
| `agentexperience.scope_match` | `Exact` or `Subtree`: how wide the call was asked to reach (the `ScopeMatch` member's name, never the scope) | `retention.sweep`, `grant.access.purge`, `record.seal`, whenever the caller passed a defined `ScopeMatch`; the five-argument `SweepExpiredAsync` reports `Exact` |
| `agentexperience.sealed_count` | integer: how many plaintext records the upgrade batch sealed. Which ones stays in the database | `record.seal`, on every returned result (0 on a refusal) |

Identifiers that come from the request are written before the operation runs, so they are present on a span that
threw as well.

> **The host owns `agentexperience.correlation_id`.** It is host-controlled free text, echoed verbatim with no length
> cap. Everything else the library writes is content-free by construction. This attribute is the exception: if a host
> puts user input, a prompt, or anything sensitive in the correlation identifier, that value reaches every trace
> exporter. Use an opaque identifier.

**What never reaches telemetry.** Task text, attempt results and errors, tool arguments and results, record payloads,
reflections and lessons, the injected Historical Reference block, and exception messages, stacks and inner
exceptions. A failing span carries the exception's type name and its `error.class`, and nothing else.

**Erasure telemetry does not re-leak what was erased.** `delete` carries the record ID the caller passed in. Like
every identifier from a request, it is written before the operation runs, so it also appears on a `Denied`,
`NotFound` or `Invalid` span. For a record that was erased, it is the ID the tombstone itself keeps and the one the
Core operations on that record already emitted. `retention.sweep`, `grant.purge` and `grant.access.purge` carry a
count, and a sweep also a flag; the sweep and the access purge also say whether they reached `Exact` or `Subtree`.
`record.seal`, the crypto-shredding upgrade, carries only its `sealed_count` and `scope_match`; a key store's failure
reaches a span as its exception type and `error.class`, never its message. In crypto-shredding mode a `delete` span
looks exactly as it does in plaintext mode: whether a key was destroyed is not a telemetry value.
None of them writes a scope identifier, a task ID, record content, the IDs of swept records, purged grants or purged
access rows, a grant's reason or recipient scope, a reading principal, or an administrator principal. `ErasureTelemetryTests` plants a marker
in all of those and asserts that it reaches no span or measurement. Telemetry that a host already exported before a
record was erased is out of the library's reach: see KL-2.

## Operations

There are nineteen `operation` values: thirteen emitted on `AgentExperience.Core`, one on
`AgentExperience.MicrosoftAgentFramework`, and five on `AgentExperience.Storage.Postgres`. The set is closed: adding
a value is a deliberate change that widens every instrument's cardinality.

| `operation` | Emitted by | `outcome` values (besides `Faulted`) | Span attributes beyond the common ones | Nested operations it emits |
| --- | --- | --- | --- | --- |
| `capture.start_run` | `InMemoryExperienceCaptureService.StartRun` | `StartRunOutcome`: `Started`, `Continued`, `Conflict` | `run_id` | — |
| `capture.append_attempt` | `InMemoryExperienceCaptureService.AppendAttemptAsync` | `AppendAttemptOutcome`: `Recorded`, `DuplicateNoOp`, `Conflict`, `SanitizationRejected`, `RunNotFound`, `CapacityExceeded` | `run_id`, `attempt_id` | — |
| `capture.complete_run` | `InMemoryExperienceCaptureService.CompleteRunAsync` | `CompleteRunOutcome`: `Recorded`, `DuplicateNoOp`, `Conflict`, `RunNotFound` | `run_id`, `event_id` | — |
| `verify` | `VerificationAggregator.Aggregate` | `TaskVerificationStatus`: `Unknown`, `Verified`, `Failed` | — | — |
| `reflect` | `DefaultExperienceReflector.ReflectAsync` | `TaskVerificationStatus`: `Unknown`, `Verified`, `Failed` | `run_id`, `reflection_id` | — |
| `finalize` | `ExperienceFinalizationService.FinalizeAsync` | `FinalizationOutcome`: `Validated`, `Quarantined`, `AlreadyFinalized`, `StorageDenied`, `NotAuthorized`, `RunNotFound`, `RunNotFinished`, `Failed` | `run_id`, `stage`, `experience_id` (when a record is returned) | `verify`, `reflect`, `lifecycle.commit`, `index` |
| `lifecycle.commit` | `ExperienceLifecycleService.CommitAsync` | `LifecycleTransitionOutcome`: `Committed`, `TransitionNotAllowed`, `ReplacementNotAllowed`, `StaleRevision`, `StatusMismatch`, `Conflict`, `NotFound`, `Denied`, `Invalid`, `Deleted` | `experience_id`, `event_id` | `deindex` (when the transition leaves eligibility) |
| `confidence.apply` | `ExperienceLifecycleService.ApplyEvidenceAsync` | `ConfidenceUpdateOutcome`: `Applied`, `Ineligible`, `StaleRevision`, `StatusMismatch`, `Conflict`, `NotFound`, `Denied`, `Invalid`, `Deleted`, `Unverified` | `experience_id`, `event_id`; `confidence.admission` on `Applied`; `independence.refusal` on `Unverified` | `deindex` (when a contradiction takes the record out of reuse) |
| `retrieve` | `ExperienceRetrievalService.RetrieveAsync` | `RetrievalOutcome`: `Completed`, `TimedOut`, `Denied`, `Failed` | `correlation_id` | — |
| `index` | `ExperienceIndexingService.IndexAsync` | `ExperienceIndexingOutcome`: `Indexed`, `Skipped`, `Stale`, `Missing`, `Denied`, `Ineligible`, `ProviderFailed`, `IndexFailed` | `experience_id` | `reindex` (a one-record pass) |
| `deindex` | `ExperienceIndexingService.RemoveAsync` | `ExperienceDeindexingOutcome`: `Removed`, `NotIndexed`, `Denied`, `Failed` | `experience_id` | — |
| `reindex` | `ExperienceIndexingService.ReindexAsync` | `ExperienceReindexOutcome`: `Completed`, `Denied`, `Invalid`, `Failed` | none (a pass has no single record) | — |
| `reuse_feedback` | `ExperienceReuseFeedbackService.RecordAsync` | `ExperienceReuseFeedbackOutcome`: `Recorded`, `AlreadyRecorded`, `Conflict`, `Denied`, `Invalid` | `feedback_id`, `run_id` | `confidence.apply` (once per attributed exposed record) |
| `inject` | `ExperienceContextProvider` (MAF `AIContextProvider`) | `InjectionOutcome`: `Injected`, `NothingToInject`, `Skipped`, `RetrievalTimedOut`, `RetrievalDenied`, `RetrievalFailed`, `Failed`, `Retracted` (a block of withdrawal notices only), `SessionBudgetExhausted` | `correlation_id`, `omitted_count`, `retracted_count` | none on its own meter; its `retrieve` is emitted on the Core meter with `nested=false` |
| `delete` | `PostgresExperienceRecordStore.DeleteAsync` (both overloads; one span per call) | `ExperienceStoreOutcome`: `Deleted`, `StaleRevision`, `NotFound`, `Denied`, `Invalid` | `experience_id` | — |
| `retention.sweep` | `PostgresExperienceRecordStore.SweepExpiredAsync` (both overloads; one span per call) | `ExperienceStoreOutcome`: `Deleted`, `Denied`, `Invalid` | `erased_count`, `interrupted`, `scope_match` | — (one span per batch, never one per record, whether `Exact` or `Subtree`) |
| `grant.purge` | `PostgresExperienceGrantStore.PurgeExpiredAsync` | `ExperienceStoreOutcome`: `Deleted`, `Denied`, `Invalid` | `erased_count` | — |
| `grant.access.purge` | `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync` | `ExperienceStoreOutcome`: `Deleted`, `Denied`, `Invalid` (including a cutoff inside the 30-day minimum retention, which the database refuses) | `erased_count`, `scope_match` | — |
| `record.seal` | `PostgresExperienceRecordStore.SealPlaintextRecordsAsync` (the crypto-shredding upgrade; one span per batch) | `ExperienceStoreOutcome`: `Committed`, `Denied`, `Invalid` (including a store with no `ExperienceEncryption`) | `sealed_count`, `scope_match` | — |

Notes:

- **Refusals are outcomes, not failures.** Every non-success `outcome` above is a returned value, recorded on
  `count` and `duration` under its own name, on an `Ok` span, and never on `failures`. The failure counter moves only
  when the operation throws.
- **Finding the verification opt-out in use.** `Applied` `confidence.apply` spans whose `agentexperience.confidence.admission`
  is `HostTrusted` are evidence a host admitted under `IndependenceVerification.TrustHostSuppliedIdentifiers`: every
  identifier taken as given, no exposure checked. Both new attributes are closed sets written as enum member names,
  and they are span attributes only: the four metric dimensions are unchanged, so count them from traces, or from
  the ledger's `admission` column. `ExperienceLifecycleService.ReadConfidenceAsync` is a read, not an operation, and
  emits nothing. Recording a run's exposure (`IExperienceCaptureService.RecordExposure`) emits no span either; a
  failure to record one reaches the host through the MAF adapter's `OnCaptureFailure`, at stage `RecordExposure`.
- **`Deleted`.** `lifecycle.commit` and `confidence.apply` report `Deleted` when the record was erased: the store
  holds only its tombstone. It is terminal and never retryable. Like `NotFound`, it is a refusal. It is reported only
  within the scope that owned the record, and every other scope sees `NotFound`.
- **Erasure outcomes.** `delete` reports `Deleted` both when it erased the record and when the record was already a
  tombstone. `retention.sweep`, `grant.purge` and `grant.access.purge` report `Deleted` whenever the batch ran, including a batch that found
  nothing, so read `erased_count` to see how much went. A sweep that the caller cancelled after it started erasing
  *returns*: its span is `Ok` with `Deleted` and `interrupted=true`, and `failures` does not move. A cancellation
  that arrives before the first erasure, while the sweep is still reading its candidates, throws
  `OperationCanceledException` like any other operation. Its span is `Faulted` and `Cancelled`, and `failures` moves
  under `Cancelled`, the one class that is normally not alertable. A sweep that a storage failure
  stopped part-way *throws* `ExperienceRetentionSweepInterruptedException`. Its span is `Faulted` and
  `Infrastructure`, it still carries the partial `erased_count` and `interrupted=true`, and `failures` moves.
- **`reflect` is emitted by the default reflector only.** A host that supplies its own `IExperienceReflector`
  replaces it, and its reflector emits nothing unless it instruments itself.
- **`capture.start_run` is never `Cancelled`.** `StartRun` is synchronous and takes no cancellation token.
- **Nested emissions are real calls.** A finalization that commits and indexes emits `finalize` (`nested=false`)
  plus `verify`, `reflect`, `lifecycle.commit` and `index` (`nested=true`), and the `reindex` inside that `index`
  (`nested=true`). A hung embedding provider inside the post-commit hook therefore reaches `failures` or a
  `Timeout`-classified span, instead of being absorbed into the outer operation.

## What is not instrumented

- **Storage-adapter internals.** Apart from the four erasure and retention operations above, the storage adapter's methods are
  not wrapped in library spans, and nor are Npgsql commands and connection events. A storage failure surfaces as
  `ExperienceStoreException` from the Core operation that called the store, and is classified `Infrastructure` on
  that operation's span and failure counter.
- **Logs.** The library writes no `ILogger` output. The typed results (`Outcome`, `Reason`, `Detail`, `Failure`) and
  the host callbacks (`ExperienceCaptureOptions.OnCaptureFailure` and `OnRunFinalized`,
  `ExperienceInjectionOptions.OnContextInjected`)
  are the diagnostics surface, and they work with no exporter and no listener at all.
