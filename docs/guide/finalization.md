# Finalization: turning a run into a record

**In short.** A captured run becomes a durable Experience Record through one call,
`ExperienceFinalizationService.FinalizeAsync`. It checks the run against the checks you declared, using only evidence
from the verification round you closed; asks whether the caller may store it and whether your storage policy allows
it; draws a lesson; and stores the record. A verified run becomes `Validated` and reusable. A run that failed
verification is stored as `Quarantined`, never reusable. The call never throws for an expected outcome, and retrying
it is always safe: the same run can never produce two records.

Package: `AgentExperience.Core`. The MAF adapter can call it for you after each invocation
([below](#finalizing-from-the-maf-adapter)).

## The call

`FinalizeAsync` runs six stages in order — load the captured snapshot, evaluate it, check authorization and the
host's storage decision, reflect on it, create the record, commit its initial lifecycle event — and stops at the
first stage that ends the call, always returning a structured result rather than throwing. The two gates precede
reflection on purpose: the reflector is the seam a host would plug a model into, so a run that is about to be refused
is never handed to it.

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
| `Quarantined` | Storage permitted, but verification did not pass, or the reflector threw, returned nothing, or returned a reflection that does not match its request | The record, with **no** reflection, created as `Candidate`, plus the initial event that moved it to `Quarantined`. `Failure` names the stage that decided it |
| `AlreadyFinalized` | This run's record already exists *and* is already confirmed | Nothing. The result reports the stored record, status, and revision. (A record left unconfirmed by an earlier call is resumed instead: the retry commits its initial event and returns `Validated`/`Quarantined`.) |
| `StorageDenied` | The host's `StorageDecision` denied | Nothing at all, and no record ID is issued |
| `NotAuthorized` | The run's scope lies outside the authorization | Nothing; denied before any store call |
| `RunNotFound` / `RunNotFinished` | No such captured run, or it has no execution status | Nothing |
| `Failed` | A stage failed (for example the database was unavailable) | Never reported as durable. Any record already created stays a `Candidate`, which is never reusable, and the captured run stays available for a retry |

## Why retrying is safe

Three properties make retrying safe. The record is *created* as a `Candidate` and its initial lifecycle event
performs the real transition, so a commit that never lands leaves nothing reusable behind. The record ID, the
reflection ID, and the initial event ID are all derived from the run ID (and the record ID from the scope too), so a
second call cannot create a second record or a second initial confirmation. And the initial event's fields are a pure
function of the stored record, so a retry re-derives exactly the event the store already deduplicates on.

Finalization never sanitizes — capture already rejected anything unsafe (see
[Sanitization happens at capture](capture.md#sanitization-happens-at-capture)) — and never decides storage or risk
policy on the host's behalf: `StorageDecision` travels in the request and Core simply obeys it.

A record whose run was later erased can never be finalized again: the derived ID collides with the tombstone (see
[Deletion and retention](deletion-and-retention.md#a-record-whose-run-was-erased-can-never-be-finalized-again)).

If an indexing hook is registered, one more thing happens *after* those six stages: the committed record is embedded
and its vector stored. That step is outside the canonical write and can never change the outcome above — see
[Indexing](indexing.md).

## Verifying a run, and binding its evaluation

Every required check names the evidence kind that satisfies it, and matching is default-deny: evidence of any other
kind is ignored, so a check it was not meant for stays `Unknown` rather than passing. A check that really should
accept any producer says so explicitly with `RequiredCheck.AnyKind` (`"*"`); a null or blank kind is an
`ArgumentException` from `VerificationAggregator.Aggregate` (and so from finalization), never an implied wildcard,
and `RequiredCheck.Accepts` returns `false` for it. The kinds the built-in
`TaskCheckEvaluators` produce are `ToolExitCode`, `TestResult`, `WorkflowCompletion`, `HumanApproval` and
`HumanCorrection`.

```csharp
RequiredChecks: [
    new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult"),
    new RequiredCheck("reviewed", ExpectedKind: RequiredCheck.AnyKind),   // explicit, visible opt-in
]
```

Verification is deterministic and uses no model. Aggregation resolves each required check independently, against only
the evidence carrying its `CheckId` and belonging to the closed round and the current artifact revision. An empty set
of required checks can never verify, and no closed round evaluates to `Unknown`.

An evaluation is bound to the run it was computed for. `VerificationAggregator.Aggregate(runId, ...)` is the only
way to get a `VerificationResult` (it has no public constructor, so it cannot be deserialized), and its `Basis`
records the run ID, the closed round, the artifact revision and a copy of the required checks. A `ReflectionRequest`
refuses an evaluation whose basis names another run, or a run whose own recorded outcome disagrees with it, with a
`ReflectionBindingException` (an `ArgumentException`); its `Run` and `Evaluation` are get-only. So every
`IExperienceReflector`, the default one or a host's, only ever receives a run together with its own evaluation. On
the way back, `ReflectionRequest.EnsureMatches` checks that a reflection carries its request's identity and copies its
evaluation's verdict, score, rule version and evidence IDs; finalization applies it to every reflection and
quarantines a record whose reflection fails it, exactly as if the reflector had thrown. A host that calls a reflector
directly can call it too.

What the binding cannot do: it is exactly as strong as the run ID. Evidence carries no run ID, so the library cannot
tell whether the round and evidence a host aggregated under a run ID really belong to that run, and a host that
re-stamps another run with `run with { RunId = ... }` is refused only when that run's own recorded outcome
contradicts the evaluation. That remains the host's statement (the KL-11 boundary in
[Known limits and documented boundaries](../known-limits.md#documented-boundaries)): finalization binds the capture
service's own run and records the round it closed, which is what confidence evidence is later checked against, but a
direct caller of `Aggregate` names its own run ID. A host that calls a reflector and then writes records without
finalization is writing records itself, and nothing but its own call to `EnsureMatches` checks that path.

That path does not reach confidence independence by default. Finalization marks the records it writes
`ExperienceRecordOrigin.Finalized`; a record written any other way is `HostWritten` (the default), and a run known
only through one is refused as `IndependenceRefusal.HostWrittenRun`. So a direct aggregator result stored by hand
vouches for no run, round or exposure unless the host marks it `Finalized` itself — which is then the host's
statement, and part of what the KL-11 boundary says. See [Confidence and independence](confidence.md).

## Finalizing from the MAF adapter

Capture alone keeps the run in memory. To turn each invocation into a durable Experience Record, give the adapter
Core's finalization service and a resolver that supplies what only the host knows — the required checks, the
verification evidence and the round it was closed in, the authorization context, and the storage decision:

```csharp
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

var options = new ExperienceCaptureOptions
{
    ResolveRun = context => new ExperienceRunDescriptor("triage-ticket", hostScope),

    FinalizationService = finalization,          // AgentExperience.Core.Finalization.ExperienceFinalizationService
    ResolveFinalization = context => new FinalizeExperienceRequest(
        RunId: context.Run.RunId,
        Authorization: hostAuthorization,
        ClosedRound: new ClosedVerificationRound(roundId, artifactRevision),
        RequiredChecks: [new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult")],
        Evidence: evidenceFor(context.Run),
        CurrentArtifactRevision: artifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: DateTimeOffset.UtcNow),

    OnRunFinalized = result => logger.LogInformation(
        "Experience {Id} is {Status}", result.ExperienceId, result.Status),
};
```

- **When it runs.** Immediately after the run's attempt and completion were both recorded, inside the same once-only
  step and the same `FinalizationTimeout`. A run whose capture reported a problem is never finalized, so a
  half-captured run is never persisted as if it were whole. A run kept open for a further attempt is finalized only
  when it completes.
- **Opting out per run.** `ResolveFinalization` returning `null` skips that run, and is not a failure.
- **Failures.** A throwing resolver, a throwing `FinalizeAsync`, or a non-durable outcome (`StorageDenied`,
  `NotAuthorized`, `Failed`, …) is reported through `OnCaptureFailure` with stage `Finalization` and never thrown.
  The captured run is left untouched, so the host can retry finalization itself from the capture service.
- **Latency.** Finalization is a database round trip and is awaited inside `FinalizationTimeout` (5 s by default), so
  it adds caller-visible latency. Leave `FinalizationService` unset and finalize out of band if that is not
  acceptable.
- **Setting `FinalizationService` without `ResolveFinalization` throws** at `UseExperienceCapture`, rather than
  silently doing nothing.
