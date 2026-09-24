# Telemetry contract

This is the operator-facing contract for everything AgentExperience.NET emits: source and meter names, span names,
instruments, units, dimensions and their allowed values, span attributes, and what each operation emits. The
definitions in the source are authoritative:

- Core: `src/AgentExperience.Core/Diagnostics/ExperienceDiagnostics.cs`, `ExperienceOperationNames.cs` and
  `ExperienceOperationErrorClass.cs`;
- MAF adapter: `src/AgentExperience.MicrosoftAgentFramework/Diagnostics/InjectionDiagnostics.cs`.

These tests pin the names in code, so renaming anything listed here fails the build rather than silently
re-labelling a dashboard:

- `ExperienceTelemetryTests` (the frozen operation table, the exact instrument set, the exact dimension and span
  attribute sets, and the error classification);
- `InjectionTelemetryTests`;
- `DiagnosticsAgreementTests` (the MAF adapter restates Core's wire names, and this asserts the two agree).

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

- **One source and meter per assembly.** A text-only host can subscribe to Core without pulling in the adapter, and
  the `AgentExperience.*` wildcard subscribes to both.
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

- A span whose operation **returned** is `Ok`. This includes a refusal such as `Denied`, `NotFound` or `Deleted`: a
  refusal is an answer, not a failure.
- A span whose operation **threw** is `Error`, and its `outcome` is `Faulted`.

## Instruments

Both meters publish exactly these three instruments and no others.

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
| `operation` | one of the fourteen values in [Operations](#operations) |
| `outcome` | an enum member name from that operation's own outcome enum, verbatim, or `Faulted` when it threw. See [Operations](#operations) |
| `error.class` | `Cancelled`, `Timeout`, `Infrastructure`, `Unexpected`. See [`error.class`](#errorclass). Only on `agentexperience.operation.failures` |
| `nested` | `true` or `false` (a boolean). See [`nested`](#nested) |

**`outcome` is not unique across operations.** `verify` and `reflect` have no outcome enum of their own and both
report `TaskVerificationStatus`, so they share one value space (`Unknown`, `Verified`, `Failed`). Other operations
also reuse member names such as `Denied`, `Conflict` or `Failed`. Always slice `outcome` together with `operation`.

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

### `nested`

`nested` is `false` when the host called the operation directly, and `true` when another instrumented operation in
the **same assembly** called it. Nesting is read from ambient `AsyncLocal` state, and each assembly keeps its own:

- In Core, `sum by (operation)` over `nested=false` is what the host asked Core to do. The unfiltered sum is
  everything Core did.
- `inject` is always `nested=false`, because only the agent pipeline calls it.
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
| `agentexperience.experience_id` | GUID | `lifecycle.commit`, `confidence.apply`, `index`, `deindex`; `finalize` when its result carries a record |
| `agentexperience.reflection_id` | GUID | `reflect` |
| `agentexperience.feedback_id` | GUID | `reuse_feedback` |
| `agentexperience.stage` | a `FinalizationStage` member: `Load`, `Evaluate`, `Authorize`, `Reflect`, `CreateRecord`, `CommitInitialEvent` | `finalize`, on every outcome, and on a throw (the stage it had reached) |
| `agentexperience.correlation_id` | the host-supplied correlation identifier, verbatim | `retrieve`, `inject`; omitted when the host supplied none |
| `agentexperience.omitted_count` | integer: how many ranked records the injection left out. The omission reasons stay on the typed result | `inject` |

Identifiers that come from the request are written before the operation runs, so they are present on a span that
threw as well.

> **The host owns `agentexperience.correlation_id`.** It is host-controlled free text, echoed verbatim with no length
> cap. Everything else the library writes is content-free by construction. This attribute is the exception: if a host
> puts user input, a prompt, or anything sensitive in the correlation identifier, that value reaches every trace
> exporter. Use an opaque identifier.

**What never reaches telemetry.** Task text, attempt results and errors, tool arguments and results, record payloads,
reflections and lessons, the injected Historical Reference block, and exception messages, stacks and inner
exceptions. A failing span carries the exception's type name and its `error.class`, and nothing else.

## Operations

There are fourteen `operation` values. Thirteen are emitted on `AgentExperience.Core` and one on
`AgentExperience.MicrosoftAgentFramework`. The set is closed: adding a value is a deliberate change that widens every
instrument's cardinality.

| `operation` | Emitted by | `outcome` values (besides `Faulted`) | Span attributes beyond the common ones | Nested operations it emits |
| --- | --- | --- | --- | --- |
| `capture.start_run` | `InMemoryExperienceCaptureService.StartRun` | `StartRunOutcome`: `Started`, `Continued`, `Conflict` | `run_id` | — |
| `capture.append_attempt` | `InMemoryExperienceCaptureService.AppendAttemptAsync` | `AppendAttemptOutcome`: `Recorded`, `DuplicateNoOp`, `Conflict`, `SanitizationRejected`, `RunNotFound`, `CapacityExceeded` | `run_id`, `attempt_id` | — |
| `capture.complete_run` | `InMemoryExperienceCaptureService.CompleteRunAsync` | `CompleteRunOutcome`: `Recorded`, `DuplicateNoOp`, `Conflict`, `RunNotFound` | `run_id`, `event_id` | — |
| `verify` | `VerificationAggregator.Aggregate` | `TaskVerificationStatus`: `Unknown`, `Verified`, `Failed` | — | — |
| `reflect` | `DefaultExperienceReflector.ReflectAsync` | `TaskVerificationStatus`: `Unknown`, `Verified`, `Failed` | `run_id`, `reflection_id` | — |
| `finalize` | `ExperienceFinalizationService.FinalizeAsync` | `FinalizationOutcome`: `Validated`, `Quarantined`, `AlreadyFinalized`, `StorageDenied`, `NotAuthorized`, `RunNotFound`, `RunNotFinished`, `Failed` | `run_id`, `stage`, `experience_id` (when a record is returned) | `verify`, `reflect`, `lifecycle.commit`, `index` |
| `lifecycle.commit` | `ExperienceLifecycleService.CommitAsync` | `LifecycleTransitionOutcome`: `Committed`, `TransitionNotAllowed`, `ReplacementNotAllowed`, `StaleRevision`, `StatusMismatch`, `Conflict`, `NotFound`, `Denied`, `Invalid`, `Deleted` | `experience_id`, `event_id` | `deindex` (when the transition leaves eligibility) |
| `confidence.apply` | `ExperienceLifecycleService.ApplyEvidenceAsync` | `ConfidenceUpdateOutcome`: `Applied`, `Ineligible`, `StaleRevision`, `StatusMismatch`, `Conflict`, `NotFound`, `Denied`, `Invalid`, `Deleted` | `experience_id`, `event_id` | `deindex` (when a contradiction takes the record out of reuse) |
| `retrieve` | `ExperienceRetrievalService.RetrieveAsync` | `RetrievalOutcome`: `Completed`, `TimedOut`, `Denied`, `Failed` | `correlation_id` | — |
| `index` | `ExperienceIndexingService.IndexAsync` | `ExperienceIndexingOutcome`: `Indexed`, `Skipped`, `Stale`, `Missing`, `Denied`, `Ineligible`, `ProviderFailed`, `IndexFailed` | `experience_id` | `reindex` (a one-record pass) |
| `deindex` | `ExperienceIndexingService.RemoveAsync` | `ExperienceDeindexingOutcome`: `Removed`, `NotIndexed`, `Denied`, `Failed` | `experience_id` | — |
| `reindex` | `ExperienceIndexingService.ReindexAsync` | `ExperienceReindexOutcome`: `Completed`, `Denied`, `Invalid`, `Failed` | none (a pass has no single record) | — |
| `reuse_feedback` | `ExperienceReuseFeedbackService.RecordAsync` | `ExperienceReuseFeedbackOutcome`: `Recorded`, `AlreadyRecorded`, `Conflict`, `Denied`, `Invalid` | `feedback_id`, `run_id` | `confidence.apply` (once per attributed exposed record) |
| `inject` | `ExperienceContextProvider` (MAF `AIContextProvider`) | `InjectionOutcome`: `Injected`, `NothingToInject`, `Skipped`, `RetrievalTimedOut`, `RetrievalDenied`, `RetrievalFailed`, `Failed` | `correlation_id`, `omitted_count` | none on its own meter; its `retrieve` is emitted on the Core meter with `nested=false` |

Notes:

- **Refusals are outcomes, not failures.** Every non-success `outcome` above is a returned value, recorded on
  `count` and `duration` under its own name, on an `Ok` span, and never on `failures`. The failure counter moves only
  when the operation throws.
- **`Deleted`.** `lifecycle.commit` and `confidence.apply` report `Deleted` when the record was erased: the store
  holds only its tombstone. It is terminal and never retryable. Like `NotFound`, it is a refusal. It is reported only
  within the scope that owned the record, and every other scope sees `NotFound`.
- **`reflect` is emitted by the default reflector only.** A host that supplies its own `IExperienceReflector`
  replaces it, and its reflector emits nothing unless it instruments itself.
- **`capture.start_run` is never `Cancelled`.** `StartRun` is synchronous and takes no cancellation token.
- **Nested emissions are real calls.** A finalization that commits and indexes emits `finalize` (`nested=false`)
  plus `verify`, `reflect`, `lifecycle.commit` and `index` (`nested=true`), and the `reindex` inside that `index`
  (`nested=true`). A hung embedding provider inside the post-commit hook therefore reaches `failures` or a
  `Timeout`-classified span, instead of being absorbed into the outer operation.

## What is not instrumented

- **Erasure, the retention sweep and the grant purge emit no library telemetry.** These are
  `PostgresExperienceRecordStore.DeleteAsync`, `SweepExpiredAsync` and `PostgresExperienceGrantStore.PurgeExpiredAsync`.
  They live on the concrete PostgreSQL adapter rather than on a Core port, no Core operation wraps them, and none of
  them is in the operation table. The host observes them through the typed results they return (`Outcome` on
  each; the sweep's `DeletedCount`, `MoreRemain` and `Interrupted`; the purge's `PurgedCount` and `MoreRemain`) and through the database itself. This
  is Known limit **KL-16** in the [repository README](../README.md#known-limits).
- **Storage-adapter internals.** Npgsql commands and connection events are not wrapped in library spans. A storage
  failure surfaces as `ExperienceStoreException` from the Core operation that called the store, and is classified
  `Infrastructure` on that operation's span and failure counter.
- **Logs.** The library writes no `ILogger` output. The typed results (`Outcome`, `Reason`, `Detail`, `Failure`) and
  the host callbacks (`ExperienceCaptureOptions.OnCaptureFailure` and `OnRunFinalized`,
  `ExperienceInjectionOptions.OnContextInjected`)
  are the diagnostics surface, and they work with no exporter and no listener at all.
