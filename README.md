# AgentExperience.NET

[![CI](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)

**Portable, evidence-backed experience memory for .NET agents.**

AgentExperience.NET captures what an AI agent actually tried, verifies whether it worked, and turns the result into an auditable lesson that future runs can reuse safely. It sits between [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) execution and durable storage, without replacing either.

> **Status: preview (`0.1.0-preview.2`). Not production ready** — the [Known limits](#known-limits) below are unresolved, and any unresolved item blocks a production-readiness claim.

All four epics are implemented and tested: capture and explain agent experience, reuse it (PostgreSQL persistence,
atomic audited lifecycle, text and hybrid retrieval, Historical Reference injection into MAF), govern it (sharing
grants, confidence from evidence, reuse feedback, deletion and expiry), and operate and measure it (telemetry, an
end-to-end sample, a controlled reuse baseline). Nothing is published to NuGet yet; the packages build, and their
release checks run, from [`RELEASING.md`](RELEASING.md). Public APIs may change between previews — every change to
them is a reviewed diff against a checked-in baseline.

## Known limits

Every unresolved limit this release knows about, one row each, with where the detail lives. **A row leaves this table
only by fixing the limit.** A release that claims production readiness requires this table to be empty and every
item on the maintainers' deferred-work ledger closed; this one ships with the table non-empty, which is why it is a
preview.

| # | Limit | Where the detail lives |
| --- | --- | --- |
| KL-2 | **Erasure reaches only this database's live rows.** Backups, replicas, WAL, exported telemetry and external artifacts are out of reach, and the erased text survives in dead heap tuples until `VACUUM` reclaims them | [Store: the honesty statement, and the limits](src/AgentExperience.Storage.Postgres/README.md#the-honesty-statement-and-the-limits) |
| KL-8 | **An approach shows argument values only for scalars the host allowlisted, and never for a borrowed record.** An object- or array-valued argument renders as a marker, and a record read through a sharing grant shows none — its approach is tool names only under `LessonAndApproach` and withheld under `LessonOnly`; a host whose lessons turn on either needs its own reflector to say so in the lesson | [Adapter: showing selected argument values](src/AgentExperience.MicrosoftAgentFramework/README.md#showing-selected-argument-values) |
| KL-11 | **Confidence independence trusts host-supplied identifiers.** Nothing can check that a `RunId`, `VerificationRoundId` or `AssessmentId` is real, so a host that lets agent output populate them hands the agent a fresh independence key per call. The same trust binds an evaluation to its run: the aggregator records the run ID it is given, and evidence carries none | [Updating confidence from evidence](#updating-confidence-from-evidence); [Recording what reuse was worth](#recording-what-reuse-was-worth); [Verifying a run](#verifying-a-run-and-binding-its-evaluation) |
| KL-12 | **Injected blocks accumulate in a reused session, and a delivered block cannot be retracted.** `MaxBytes` bounds one block, not a conversation; revocation affects only injections that have not happened yet | [Injecting Historical Reference into MAF](#injecting-historical-reference-into-maf) |
| KL-13 | **The supported matrix is narrow.** `net10.0` only, PostgreSQL 16 only, `Microsoft.Agents.AI` 1.22.0 only. Every shipping pin is exact, including the shared `Microsoft.Extensions.*` ones (DI abstractions, redaction, AI abstractions), so a host whose graph needs a newer version of any of them, or a MAF that does, gets a restore conflict until a new preview moves the pins. CI's MAF probe reports on every run when the newest MAF stops resolving | [Compatibility evidence](docs/compatibility-evidence.md#supported-matrix) |

Resolved since `0.1.0-preview.2` (unreleased):

- KL-4 (the purge path being auditability, not a privilege boundary) is resolved by the two-role deployment (story
  6.1), which is now the documented and supported one. An owner role owns the schema and runs the migrators; the
  application role is given exactly what the stores need by `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync`,
  run on every deploy: no ownership, so no `ALTER TABLE`, `DISABLE TRIGGER` or replaced guard function; no `DELETE`,
  `TRUNCATE` or `UPDATE` on any ledger, so a purge marker it sets by hand admits nothing; `UPDATE` on
  `experience_records` and `experience_grants` only on the columns the store moves, so it cannot write a tombstone
  by hand either; and `EXECUTE` on the purge functions only when the host opts in. The call refuses a superuser, the
  owner itself, and any member of an owning role, and verifies the role's effective privileges before it commits.
  The tests prove each refusal from the application role's own connection, and every store test now runs as that
  role. **What remains is inherent in PostgreSQL:** the owner role and superusers are not bound by any of it — they
  can disable a trigger, replace a function, or bypass privileges altogether — so keep the owner's credentials out
  of the application. A deployment that runs the application as the owner (the single-role shape, still fine for
  local development) gets none of this. `0013` also pins `search_path` with `pg_temp` last on every purge and guard
  function. See
  [Store: deploying with two roles](src/AgentExperience.Storage.Postgres/README.md#deploying-with-two-roles).

Resolved in `0.1.0-preview.2`:

- KL-1 (serial round trips on the re-index path and on the invocation's critical path) is resolved by story 5.6.
  `IExperienceEmbeddingGenerator` gained `GenerateBatchAsync`, and a re-index pass embeds the records that need a
  vector `ReindexExperienceRequest.EmbeddingBatchSize` (default 16, at most 128) per provider call, with each record
  still written on its own, conditionally on its own revision. `IExperienceRecordStore` gained `GetManyAsync`, which
  the PostgreSQL store answers with one statement applying exactly `GetAsync`'s scope, grant, disclosure and tombstone
  rules and one audit append; injection's final eligibility check is now that one call. Measured in the tests: eight
  candidates cost one read and one access-row append where they cost twelve commands, and forty records cost three
  provider calls where they cost forty. Both methods have a default implementation that does what the library did
  before, one call per item, so an out-of-tree store or generator keeps compiling and keeps its behaviour; it simply
  saves no round trips until it overrides them. See
  [Adapter: limits and the final eligibility check](src/AgentExperience.MicrosoftAgentFramework/README.md#limits-and-the-final-eligibility-check)
  and [Indexing](#indexing-experience-for-semantic-reuse).
- KL-7 (an invocation that opens a run and never returns holding it with no bound) is resolved by arming the
  open-run duration bound when the run is opened, for every run, with the timer disposed when the invocation
  releases the run (story 5.3). An invocation that never returns now holds its run for at most twice
  `MaxOpenRunDuration`, and the close is reported. See
  [Adapter: retries as attempts of one run](src/AgentExperience.MicrosoftAgentFramework/README.md#retries-as-attempts-of-one-run).
- KL-9 (a borrowed lesson disclosing the lending scope's tool names) is resolved by the grant disclosure level
  (story 3.6); see [Sharing experience across scopes](#sharing-experience-across-scopes).
- KL-14 (exact pins blocking a newer MAF) is resolved by moving the supported pin to `Microsoft.Agents.AI` 1.22.0,
  with `Microsoft.Extensions.DependencyInjection.Abstractions` `[10.0.12]` in Core and both stores and
  `Microsoft.Extensions.AI.Abstractions` `[10.10.0]` in the vectors package (story 5.1). The general hazard remains
  and is part of KL-13: a later MAF can need newer shared pins. See
  [Compatibility evidence](docs/compatibility-evidence.md#the-maf-compatibility-matrix).
- KL-15 (Core's redaction dependency a floor) is resolved by exact-pinning `Microsoft.Extensions.Compliance.Redaction`
  at `[10.10.0]` (story 5.1). Every `PackageReference` a shipping project declares is now exact, and a release test
  fails on a new floor; the dependencies those packages declare in turn are still whatever NuGet floors they carry.
- KL-16 (erasure emitting no library telemetry) is resolved by story 5.2. Deletion, the retention sweep and the grant
  purge are now the `delete`, `retention.sweep` and `grant.purge` operations on the `AgentExperience.Storage.Postgres`
  source and meter, and they carry nothing that was erased; see [`docs/telemetry.md`](docs/telemetry.md#operations).
- KL-3 (a retention sweep matching one scope exactly) is resolved by `ScopeMatch.Subtree` (story 5.4).
  `SweepExpiredAsync(auth, scope, age, batch, ScopeMatch.Subtree, ct)` sweeps the scope and every scope beneath it,
  bounded, one record per transaction, with `MoreRemain` true across the whole subtree. "Beneath" means the same
  tenant, application and project, and each team, agent or user field either left null on the root or equal to it,
  which is the reading `AuthorizationContext` already gives a null bound, so authorizing the root authorizes the
  subtree. The five-argument overload is unchanged and still exact. A subtree never spans projects: a host with
  several sweeps each project root, a list it configures rather than one it has to discover. See
  [Store: a sweep reaches one scope, or everything beneath it](src/AgentExperience.Storage.Postgres/README.md#a-sweep-reaches-one-scope-or-everything-beneath-it-and-you-choose-which).
- KL-10 (no retention path for the grant access log) is resolved by `0012` and
  `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync` (story 5.4): a bounded, administrator-authorized purge of
  access rows the database recorded before a host-given cutoff, within an owner scope or its subtree, through a
  `SECURITY DEFINER` function whose `EXECUTE` is revoked from `PUBLIC`. It never removes a row younger than
  30 days, by the database's clock: a later cutoff is refused rather than clamped, and the append-only guard
  re-checks every row. Erasing a record still keeps its access rows. It is the `grant.access.purge` telemetry
  operation. See [Store: retention for the access log](src/AgentExperience.Storage.Postgres/README.md#retention-for-the-grant-access-log).
- KL-5 (default-deny on evidence kind opt-in per check) is resolved by story 5.5. `RequiredCheck`'s `ExpectedKind` is
  now required; accepting any kind is spelled `RequiredCheck.AnyKind` (`"*"`), and a null or blank kind is refused by
  the aggregator rather than read as "any". **Breaking:** `new RequiredCheck("id")` no longer compiles. See
  [Verifying a run](#verifying-a-run-and-binding-its-evaluation).
- KL-6 (a reflection pairable with the wrong evaluation by a direct caller) is resolved by story 5.5. An evaluation
  now records the run, round, revision and checks it was computed from, only `VerificationAggregator.Aggregate` can
  make one, and a `ReflectionRequest` cannot be constructed from an evaluation computed for another run
  (`ReflectionBindingException`). Finalization also checks every reflection a reflector returns against its request
  and quarantines one that does not match. **Breaking:** `Aggregate` takes the run ID first, `VerificationResult` has
  no public constructor (so it can no longer be deserialized), a request's `Run` and `Evaluation` cannot be replaced
  with `with`, and a run whose own outcome disagrees with the evaluation now fails at request construction rather
  than inside the default reflector. The binding is only as strong as the run ID a host supplies, which is now part
  of KL-11; see [Verifying a run](#verifying-a-run-and-binding-its-evaluation).

`0006`'s header still tells an operator to purge events by disabling a trigger "until the library ships a purge path";
`0010` is that purge path and says so in its own header, and the runbook in `0006` must not be used. Journaled scripts
are never edited, so this and the similar forward references in `0007`–`0009` are corrected in the
[store README](src/AgentExperience.Storage.Postgres/README.md#script-comments-that-were-written-before-the-work-they-point-at-shipped)
instead.

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
| Atomic audited lifecycle commits: the event and the record's projection in one transaction, idempotent by event ID, revision-checked, with bounded, cursored history | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| The full MVP transition table — reinforce, contest, stale, supersede, revoke — with supersession recording its replacement and refusing cycles, event logs made append-only by database triggers, and a record's embedding dropped when it leaves eligibility | `AgentExperience.Core`, `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |
| Evidence-based reuse confidence: a versioned `(1 + S) / (2 + S + F)` heuristic Core computes from the record it read, with independence enforced by a unique index, a duplicate recorded but counted zero times, a contradiction contesting the record in the same transaction, and the confidence columns guarded by the database | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Reuse feedback: one idempotent submission links a run to the records it saw, with an outcome, a measure and a trial label; exposure alone records benefit `Unknown` and moves nothing, an attribution that fails its evidence requirements degrades to `Unknown` rather than losing the exposure, and only a human assessment naming a host-established review or a comparative evaluator result carrying its own round-matched evidence becomes supporting or contradicting evidence | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Journaled schema migrations: embedded scripts applied once, one transaction per script, serialized across processes by an advisory lock | `AgentExperience.Storage.Postgres` |
| One finalization call: evaluate, gate on authorization and the host's storage decision, reflect, create the record as a `Candidate`, commit the initial event that promotes it — replay-safe and structured at every stage | `AgentExperience.Core` |
| Text retrieval of applicable experience: eligibility decided before ranking, every ranking component and effective weight exposed, bounded by a timeout that is never an exception | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Embedding ingestion after the canonical commit: only the sanitized retrieval summary is embedded, writes are conditional on the live revision, and every provider failure leaves the record committed and retryable | `AgentExperience.Core`, `AgentExperience.Storage.Postgres.Vectors` |
| Hybrid retrieval: a bounded vector channel merged with the text one under the same eligibility, timeout, and ceiling, with an explicit, flagged text-only fallback whenever the vector channel cannot be trusted | `AgentExperience.Core`, `AgentExperience.Storage.Postgres.Vectors` |
| Historical Reference injection into MAF: a context provider that retrieves, re-checks eligibility immediately before injecting, asks the host's risk policy, and injects one delimited, labeled block within record and byte limits — never throwing into the invocation | `AgentExperience.MicrosoftAgentFramework` |
| Explicit sharing grants: an administrator the host names lets one named record be *read* by a sibling scope until it expires or is revoked; the grant and its audit event commit together, and reads honour it in SQL, never in application code. A grant's lifetime is bounded by a host-configured maximum, so there is no permanent grant | `AgentExperience.Abstractions`, `AgentExperience.Storage.Postgres` |
| An optional access log answering "who read our team's experience, and when": one append-only row per record a grant *delivered*, naming that grant and the revision disclosed, written outside the read's own statement and batched per search, best-effort or fail-closed as the host chooses, with an owner-scoped reader for the trail | `AgentExperience.Abstractions`, `AgentExperience.Storage.Postgres` |
| Deleting and expiring library-owned data: one authorized, atomic, scope-safe erasure across seven tables leaving a payload-free tombstone, a bounded retention sweep the host schedules, and expired sharing grants collected with their events — with the append-only guards never disabled and the limits stated rather than overclaimed | `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |
| Release verification: preview versioning, SourceLinked deterministic packages checked from their built nuspecs, an approval baseline of every public API, source-backed evidence for every pin, and a MAF compatibility matrix — nothing published by automation | [`RELEASING.md`](RELEASING.md) |
| Dependency-injection registration for each package, so a host wires capture, finalization, storage, indexing, and retrieval without knowing concrete types. Injection is the one piece the host constructs itself, because the resolver and risk decision are per-host | `AgentExperience.Core`, `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |

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
| `Quarantined` | Storage permitted, but verification did not pass, or the reflector threw, returned nothing, or returned a reflection that does not match its request | The record, with **no** reflection, created as `Candidate`, plus the initial event that moved it to `Quarantined`. `Failure` names the stage that decided it |
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

**What "already rejected" means.** Sanitization is the first gate, and it is fail-closed at capture time rather than
at storage time. When content cannot be sanitized, `AppendAttemptAsync` returns
`AppendAttemptOutcome.SanitizationRejected` and the sanitizer's own `Reason`, the attempt is not recorded, the run
stays open, and **nothing is stored anywhere** — there is no database involved, so there is no partial write and no
persisted denial record to reconcile later. The host is told the decision and why, and can correct and resubmit the
same attempt ID; the rejected ID is not tracked, so a corrected resubmission succeeds. Unsafe content therefore never
reaches an Experience Record, and never becomes something a grant could later share.

If an indexing hook is registered, one more thing happens *after* those six stages: the committed record is embedded
and its vector stored. That step is outside the canonical write and can never change the outcome above — see
[Indexing experience for semantic reuse](#indexing-experience-for-semantic-reuse).

### Verifying a run, and binding its evaluation

Every required check names the evidence kind that satisfies it, and matching is default-deny: evidence of any other
kind is ignored, so a check it was not meant for stays `Unknown` rather than passing. A check that really should
accept any producer says so explicitly with `RequiredCheck.AnyKind`; a null or blank kind is an `ArgumentException`
from `VerificationAggregator.Aggregate`, never an implied wildcard.

```csharp
RequiredChecks: [
    new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult"),
    new RequiredCheck("reviewed", ExpectedKind: RequiredCheck.AnyKind),   // explicit, visible opt-in
]
```

An evaluation is bound to the run it was computed for. `VerificationAggregator.Aggregate(runId, ...)` is the only
way to get a `VerificationResult`, and its `Basis` records the run ID, the closed round, the artifact revision and a
copy of the required checks. A `ReflectionRequest` refuses an evaluation whose basis names another run with a
`ReflectionBindingException` (an `ArgumentException`), so every `IExperienceReflector`, the default one or a host's,
only ever receives a run together with its own evaluation. On the way back, `ReflectionRequest.EnsureMatches`
checks that a reflection carries its request's identity and copies its evaluation's verdict, score, rule version
and evidence IDs; finalization applies it to every reflection, and a host that calls a reflector directly can too.

What the binding cannot do: it is exactly as strong as the run ID. Evidence carries no run ID, so the library cannot
tell whether the round and evidence a host aggregated under a run ID really belong to that run, and a host that
re-stamps another run with `run with { RunId = ... }` is refused only when that run's own recorded outcome
contradicts the evaluation. That remains the host's statement, like every other host-supplied identifier (KL-11). A host that calls a reflector and then writes records without finalization is
writing records itself, and nothing but its own call to `EnsureMatches` checks that path.

## Moving a record through its lifecycle

Finalization is only a record's first transition. After it, `ExperienceLifecycleService` is the only way a stored
record's status changes, and it accepts exactly this table:

| From | To | What it means |
| --- | --- | --- |
| `Candidate` | `Validated`, `Quarantined` | Finalization's own two outcomes |
| `Validated` | `Reinforced` | Reuse was observed to succeed again — **once**; `Reinforced → Reinforced` is refused |
| `Validated`, `Reinforced` | `Contested` | Later evidence contradicts the lesson. Exits only to `Revoked` |
| `Validated`, `Reinforced` | `Stale` | The lesson is no longer current. Exits only to `Revoked` |
| `Validated`, `Reinforced` | `Superseded` | A better record replaces it — and names which. Exits only to `Revoked` |
| anything except `Revoked` | `Revoked` | Withdrawn by an authorized action. Terminal |

Everything else is `TransitionNotAllowed`, refused by Core before the store is called. That includes an event whose
prior and current status are the same: it would consume a revision and sit in the audit trail claiming a transition
that did not happen. It also includes a *first* event — one with no prior status — that records anything but
`Candidate`: a null prior status is how a record's creation is logged, never a way to move a record without saying
what it moved from.

Three consequences are worth stating outright rather than leaving to be discovered:

- **Quarantine is now a capture-time decision only.** Earlier versions accepted `Validated → Quarantined` (and
  `Contested`/`Stale`/`Superseded`/`Reinforced → Quarantined`). Those are refused now, at runtime, with no
  compile-time signal — the enum and the request type are unchanged. A host that quarantined a live record must
  `Revoke` it instead, or contest it.
- **A record can be reinforced once.** `Reinforced → Reinforced` records no transition and is refused, so this
  table cannot express repeated reinforcement. Evidence can:
  [`ApplyEvidenceAsync`](#updating-confidence-from-evidence) moves the counters without moving the status, which is
  the counter-that-moves-without-a-status-change answer to this limit rather than a carve-out in the table.
- **`Contested` and `Stale` are one-way.** Nothing resolves a contest or refreshes a stale record back into
  eligibility in this version; both exit only to `Revoked`.

**Port additions in story 5.6 (KL-1)** break nothing: `IExperienceRecordStore.GetManyAsync` and
`IExperienceEmbeddingGenerator.GenerateBatchAsync` are default interface methods that loop over the existing
single-item call, so an out-of-tree implementation keeps compiling and behaving exactly as before. Override them to
save the round trips; a store's override must answer each ID exactly as its own `GetAsync` would, access rows
included.

**Port changes in this version.** Nothing is published to NuGet yet, but anyone implementing the ports out of tree
has four breaks to absorb: `IExperienceRecordStore` gained `CheckSupersessionAsync`;
`IExperienceRecordStore.GetHistoryAsync` now takes an `ExperienceRecordHistoryQuery` and returns
`StoredLifecycleEvent`s rather than bare `LifecycleEvent`s (`GetFirstHistoryPageAsync` is the convenience for the
old four-argument shape); `IExperienceEmbeddingIndex` gained `RemoveAsync`; and `ExperienceStoreOutcome` gained
`ReplacementNotAllowed`, which a commit can now return. All four fail at compile time.

Evidence-based confidence adds three more, and none of them fails at compile time, so read them rather than trusting
the build: `LifecycleEvent` gained an optional `Confidence`, `StoredLifecycleEvent` an optional `Actor`, and
`ExperienceLifecycleCommitResult` an optional `AppliedConfidence`. An out-of-tree store still compiles and still
commits — it will simply drop a confidence payload on the floor while reporting `Committed`, which is a silently
wrong answer rather than a failed one. A store that means to support
[`ApplyEvidenceAsync`](#updating-confidence-from-evidence) has to persist the payload, enforce the independence key,
and report what it stored.

Only `Validated` and `Reinforced` are **eligible**. A record in any other status is never retrieved, never injected,
and never indexed — so contesting, staling, superseding, or revoking a record takes it out of reuse immediately,
through both channels, without deleting anything.

```csharp
var result = await lifecycle.CommitAsync(
    hostAuthorization,
    new CommitLifecycleTransitionRequest(
        EventId: Guid.NewGuid(),          // the idempotency key; reuse it verbatim on a retry
        ExperienceId: supersededId,
        Scope: recordScope,
        PriorStatus: ExperienceStatus.Validated,
        CurrentStatus: ExperienceStatus.Superseded,
        Reason: "replaced by the parallel-warmup lesson",
        Producer: "governance-review/1.0",
        OccurredAt: DateTimeOffset.UtcNow,
        ExpectedRevision: stored.Revision,
        ReplacementExperienceId: replacementId),
    cancellationToken);
```

**Supersession names a replacement.** A move to `Superseded` must carry `ReplacementExperienceId`, and every other
move must not. The replacement has to be a different record, in the record's exact scope, currently eligible, and
not one this record already replaces directly or transitively. The last of those is a walk over the stored
replacement chain, done in SQL in one round trip, so a cycle is refused (`ReplacementNotAllowed`) with nothing
written. A replacement in another scope is reported exactly like one that does not exist, so a cross-scope attempt
reveals nothing. The replacement ID is stored on the event itself, which is what makes the chain auditable.

**Leaving eligibility drops the embedding — as hygiene, not as a boundary.** When an `ExperienceIndexingService` is
wired into the lifecycle service, a commit that moves a record out of `Validated`/`Reinforced` removes its stored
vector afterwards, outside the transaction and on its own budget. What that buys is storage and index maintenance
cost, not correctness: a vector search joins the canonical record and filters on its status, so a surviving vector is
*already* unreachable the moment the transition commits. That is why it is reported on `result.Deindexing` and can
never fail the transition.

Nothing retries it. `ReindexAsync` lists only records a search could return and never removes anything, so there is
no sweep — a `Deindexing` outcome other than `Removed` or `NotIndexed` is a work item for the host: record the
experience ID and scope, and call `ExperienceIndexingService.RemoveAsync` again later. That includes `Denied`, which
reports `IsRetryable: false` because repeating the *same* call changes nothing; it needs a different authorization.

**Reading the trail.** `IExperienceRecordStore.GetHistoryAsync` returns one bounded page of a record's events,
oldest first, plus the record's current revision — from a single snapshot, so the two can never disagree. Each
stored event carries the store's own `RecordedAt` (the database's clock, not the caller's) and the `AppliedRevision`
it produced. Page with the keyset cursor:

```csharp
long? cursor = null;
do
{
    var page = await store.GetHistoryAsync(
        hostAuthorization,
        new ExperienceRecordHistoryQuery(recordScope, experienceId, Limit: 100, StartAfterRevision: cursor),
        cancellationToken);

    if (page.Outcome != ExperienceStoreOutcome.Found)
    {
        // NotFound, Denied or Invalid. Never treat one as an empty history: they mean the record is not
        // readable here, not that it has no trail.
        throw new InvalidOperationException($"History unavailable: {page.Outcome}.");
    }

    foreach (var stored in page.Events)
    {
        Console.WriteLine($"r{stored.AppliedRevision} {stored.Event.PriorStatus} -> {stored.Event.CurrentStatus}");
    }

    cursor = page.NextStartAfterRevision;   // null once the page came back empty
}
while (cursor is not null);
```

A record whose cursor has walked past its last event still reports `Found` with its revision and an empty page, so
"nothing left to show" stays distinguishable from `NotFound`. `GetFirstHistoryPageAsync(authorization, scope, id, ct)`
is the one-line convenience for the common case, and is named for what it does: it returns the first page only, and
a record with a longer trail has more.

**Append-only is enforced by the database, not by convention.** Migration `0006` installs triggers that reject every
way a stored event could stop being what it was:

| Attempt | What stops it |
| --- | --- |
| `UPDATE` or `DELETE` on `lifecycle_events` / `experience_grant_events` | row-level `BEFORE UPDATE OR DELETE` triggers |
| `TRUNCATE` on either log, or on `experience_grants` | statement-level `BEFORE TRUNCATE` triggers — `TRUNCATE` does not fire row triggers at all, so a row-level guard alone would let it erase the whole log with no error |
| Clearing a grant's `revoked_at`, rewording its `revocation_reason`, extending its `expires_at` | `BEFORE UPDATE` trigger on `experience_grants` |
| Deleting a revoked grant and inserting it again unrevoked | `BEFORE DELETE` trigger refusing any grant that has audit events |
| Re-pointing a live grant at another record or recipient | the same `BEFORE UPDATE` trigger, which pins the grant's identity and audit columns |
| Winding a record's `revision` back, or moving its `status` without the revision its event produced | `BEFORE UPDATE` trigger on `experience_records` — an immutable log beside a freely rewritable projection proves nothing |

A tamperer gets SQLSTATE `42501`. Be precise about what that buys:

- It binds ordinary writes **from any role, superusers included**, as long as the triggers are enabled. They are
  created `ENABLE ALWAYS`, so they also fire under `session_replication_role = 'replica'` — the mode logical
  replication appliers and several restore and ETL tools run in, and the mode in which an ordinary trigger is
  skipped silently.
- It does **not** bind anyone who can `ALTER TABLE` these tables: a superuser, or the tables' owner. An owner can
  `DISABLE TRIGGER`, `DROP TRIGGER`, or drop a constraint and then write freely. That is why the supported
  deployment has two roles: an owner that runs the migrators and a separate application role that owns nothing,
  holds no `UPDATE`, `DELETE` or `TRUNCATE` on any log, and so is refused by the privilege system before a trigger is
  even asked (see
  [Store: deploying with two roles](src/AgentExperience.Storage.Postgres/README.md#deploying-with-two-roles)). The
  owner and superusers remain unbound; that is inherent in PostgreSQL.
- It says nothing about backups, about a restore that recreates the tables without `0006`, or about filesystem
  access to the data directory.

So, with the two roles, it is a guard against a bug, a careless script, a compromised application path — including
one holding the application role's own credentials — or a replication apply that would otherwise rewrite history.
It is not a guard against an administrator holding the owner's or a superuser's credentials who has decided to
tamper. A deployment that needs tamper-evidence against those should ship the log off-box.

**There is exactly one exception, and it is the subject of the next section.** Migration `0010` gives the guards a
transaction-scoped marker that one `SECURITY DEFINER` purge function sets, so erasing a record can remove the rows
that named it without any trigger ever being disabled. `UPDATE` and `TRUNCATE` stay refused unconditionally, in
every session, including the purging one. That replaces `0006`'s manual
`ALTER TABLE … DISABLE TRIGGER` runbook — which was table-wide, visible to every other connection in the pool, and
left the guard off if anything failed in between.

## Deleting and expiring data

Revocation stops reads. **Deletion removes payload.** One authorized, atomic, scope-safe operation erases every
payload-bearing trace of one experience across seven tables and leaves a payload-free tombstone behind:

```csharp
var deleted = await store.DeleteAsync(hostAuthorization, scope, experienceId, cancellationToken);

// Or on a schedule the host owns: this library ships no timer.
var sweep = await store.SweepExpiredAsync(hostAuthorization, scope, TimeSpan.FromDays(90), batchSize: 200, cancellationToken);

// The scope and every team, agent and user scope beneath it -- opt-in, never the default.
var wide = await store.SweepExpiredAsync(hostAuthorization, projectScope, TimeSpan.FromDays(90), batchSize: 200, ScopeMatch.Subtree, cancellationToken);
```

- **A tombstone, never a vanishing row.** What is retained is exactly the opaque `ExperienceId`, the six scope
  fields, the revision, the deletion timestamp, a fixed tombstone status, and a fixed `TaskId` placeholder —
  nothing else. Evidence, exposure rows, grants and their events, lifecycle history, and the embedding are
  *removed*. The access trail (`experience_grant_access`) is deliberately kept: it carries no payload and is the
  answer to "who read this before it was deleted".
- **A tombstone is terminal.** A late create, lifecycle commit, confidence submission, feedback write, index write,
  or grant naming it is refused, never resurrected — and, within its own scope, a host can tell `Deleted` from
  `NotFound`. Across scopes the two collapse: a foreign-scope delete is the same answer as one naming an ID that
  never existed. Three of those refusals are enforced by the schema and cannot be worked around at all: the ID can
  never be re-created, the tombstone can never be moved, and the record row can never be deleted or truncated. The
  rest are predicates this library puts in its own statements — binding for everything that goes through the
  library, not for raw SQL from another tool. The adapter README says which is which, table by table, rather than
  claiming the stronger version of both.
- **Retention is indefinite by default.** A sweep runs only when a host passes a positive age, in bounded batches,
  through the same delete. The library ships no timer, no background service, and no hosted service: scheduling
  belongs to the host. **By default a sweep matches one exact scope**, and a sweep of the project root alone
  reports a clean `MoreRemain: false` while every team-, agent- and user-scoped record stays put; pass
  `ScopeMatch.Subtree` to sweep the root and everything beneath it. The grant access trail has its own
  retention path, `PurgeOlderThanAsync`, which never removes a row younger than 30 days.
- **With two roles, the application's own role cannot get round it.** The purge path is one code path, one
  transaction, and a guard that is never switched off. The marker it sets is a custom GUC any session can set, so on
  its own it decides nothing; what binds the application role is that it holds no `DELETE` on any ledger and no
  `UPDATE` on the tombstone's columns, so only the `SECURITY DEFINER` purge functions, running as the owner, can
  erase — and it can call those only when the host opts in (`AllowErasure`, `AllowAccessLogPurge`). `EXECUTE` on them
  is revoked from `PUBLIC`, because PostgreSQL's default would otherwise let any role that can connect erase any
  tenant's record or access trail. The owner role and superusers are not bound, which is inherent in PostgreSQL.
- **The limits are stated, including the uncomfortable one.** Backups, replicas, WAL, exported telemetry and
  external artifacts are host-owned and out of reach — and *inside* this database the erased text survives in the
  dead heap tuple until `VACUUM` reclaims it, which is a schedule nobody promised. The
  [adapter README](src/AgentExperience.Storage.Postgres/README.md#deleting-and-expiring-data) carries that, the
  retained list, the erasure order, and the full outcome table.

**Upgrading an existing database.** `0006` adds every `CHECK` as `NOT VALID`, so it does not scan existing rows and
cannot abort on a pre-`0006` `Superseded` event that has no replacement — one the public port accepted, because the
store never applied Core's table. New and updated rows are checked from that moment on. The script's header carries
the reconciliation query and the `VALIDATE CONSTRAINT` statements to run once it comes back empty.

## Updating confidence from evidence

Finalization stamps a record at 2/3 and stops. `ExperienceLifecycleService.ApplyEvidenceAsync` is how that number
moves afterwards: submit what happened when the lesson was reused, and the evidence, the counters, the score, any
status change, and the audit entry are committed in one transaction.

```csharp
var result = await lifecycle.ApplyEvidenceAsync(
    hostAuthorization,
    new ApplyConfidenceEvidenceRequest(
        EventId: Guid.NewGuid(),              // the commit's idempotency key
        ExperienceId: experienceId,
        Scope: recordScope,
        EvidenceId: Guid.NewGuid(),           // the evidence's own; reuse it verbatim on a retry
        Kind: ConfidenceEvidenceKind.Supporting,      // or Contradicting
        Source: ConfidenceEvidenceSource.Machine,     // or Human
        RunId: runId,                         // the run the *reuse* happened in, not the record's source run
        VerificationRoundId: roundId,         // machine evidence only
        Reason: "the retry-after-lock lesson was applied and the checks passed",
        Producer: "verification-aggregator/1.0.0",
        OccurredAt: DateTimeOffset.UtcNow),
    cancellationToken);

if (result.Outcome == ConfidenceUpdateOutcome.Applied)
{
    logger.LogInformation(
        "Experience {Id} is now {Confidence:F3} ({S} supporting, {F} contradicting){Counted}",
        experienceId, result.ReuseConfidence, result.SupportingValidations, result.Contradictions,
        result.Counted ? "" : " — already counted, recorded only");
}
```

**The score is `(1 + S) / (2 + S + F)`.** `S` counts independent accepted supporting validations, including the one
the record was finalized with; `F` counts independent accepted contradictions. So a fresh validated record is
`2/3`, a first independent confirmation takes it to `3/4`, and a contradiction after that takes it to `3/5`.

**It is a heuristic, not a probability.** Laplace's rule of succession is a monotone, bounded summary of how often
reuse held up — useful for ranking and for a floor. It is not calibrated against anything, and nothing here claims
it is the probability that the next reuse will succeed. The rule is versioned: every accepted update records the
`RuleVersion` that produced it, so a later rule change stays auditable against scores computed under an earlier one.

**It never changes eligibility.** Confidence is independent of the completion score and of status; a number cannot
make an ineligible record eligible. What takes a record out of reuse is the *status*: a contradiction moves a
`Validated` or `Reinforced` record to `Contested` in the same transaction, and a record already `Contested` stays
there while its counters keep moving. Supporting evidence never changes a status by itself — which is how a record
keeps being reinforced through its counters even though `Validated → Reinforced` happens only once. (That is the
known limit the lifecycle table left open above; this is how it is expressed.)

**Independence is keyed, and the database owns the key.** Machine evidence counts once per `(record, run,
verification round)`; human evidence once per `(record, reviewer, run)`. The key is a *generated* column in
`confidence_evidence` with a partial unique index over it, so no caller picks the key **string**: two submissions
describing the same observation collide however they are phrased.

**The key's inputs are a host trust boundary — read this before wiring it up.** Nothing stops a caller that invents
the key's *inputs*. There is no foreign key behind `RunId` or `VerificationRoundId` and nothing in the schema can
check that a run happened or that a round was closed, so a caller passing a fresh `Guid` for both on every
submission gets a fresh key every time and can drive the score as high as it likes. Establish them the way you
establish `AuthorizationContext`: from your own run bookkeeping and your own closed verification rounds, never
passed through from something an agent produced. `ReviewerIdentity` is the same boundary, and is the one the library
can enforce for you — it is taken from `AuthorizationContext.PrincipalId` and the request has no field for it,
because the number of distinct human reviewers is exactly what this rule protects. Principals are compared
ordinally, like every other identity here, and one with leading or trailing whitespace is refused rather than
trimmed. What the rule guarantees, stated exactly: a host that establishes these honestly cannot have its own
observations counted twice.

| Submission | Outcome |
| --- | --- |
| First for its independence key | `Applied`, `Counted: true` — counters and score move |
| Same run and round (or reviewer and run) under a **new** evidence ID | `Applied`, `Counted: false` — a ledger row is written and *nothing else* moves: no counters, no status, no revision, no `UpdatedAt`, and no lifecycle event |
| …and the record moved between the read and the commit | `StaleRevision`, `StatusMismatch` or `NotFound`, with nothing stored at all — a duplicate is still committed against the record it describes |
| Same evidence ID, identical content | `Applied` — the original outcome, reported again; nothing is written twice |
| Same evidence ID, different content | `Conflict` — nothing written |
| Two submissions computed from one revision | Exactly one `Applied`; the other `StaleRevision` with the revision to retry against |
| Against a `Candidate`, `Quarantined`, `Stale`, `Superseded`, or `Revoked` record | `Ineligible` — refused before anything is written |

**A record cannot be created claiming evidence it does not have.** `CreateAsync` refuses a record whose
`ReuseConfidence` is not the one its own counters explain — creation is the single moment the two arrive
independently, and after it every change goes through the guarded path above. A record created with *no* counters
may carry any confidence its host wants to seed it with; the first accepted evidence recomputes from those counters,
so a seeded number never survives contact with evidence.

**Core owns the arithmetic; the adapter owns independence.** Core reads the record, computes the new counters and
the new score from what it read, and submits them with *that* revision, so the arithmetic and the concurrency guard
are about the same version of the record. The adapter writes those numbers and derives none: what it decides is
whether the independence key was free, and whether the revision still holds. Everything else is a fact it was given.

**Why a duplicate must move nothing.** The two obvious exceptions are the harmful ones. Refreshing `UpdatedAt`
would let one observation, replayed under fresh evidence IDs, keep a record permanently recent for ranking and
permanently un-expired — retrieval reads recency and expiry off that column. Writing the status would contest a
record on the strength of an observation the independence rule had just declared already counted, leaving an event
that says nothing moved beside a ledger with zero counted contradictions.

**The counters are guarded like the rest of the projection.** Migration `0007` extends the `experience_records`
trigger so `reuse_confidence`, `supporting_validations`, and `contradictions` move only together with the revision
of the lifecycle event that recorded the evidence for them — and only to the values that event recorded, so
`UPDATE … SET reuse_confidence = 1, revision = revision + 1` is refused too. A direct `UPDATE` on any of them gets
SQLSTATE `42501`, exactly as one on `status` or `revision` does — see the limits stated above for what that guard does and does not
bind. `confidence_evidence` is append-only for the same reason the event logs are: a row that could be edited or
removed would free an independence key, and the same observation could then be counted twice.

**One ordering wart, stated rather than hidden.** Core's eligibility gate runs on the record it read, before the
store is asked anything, so it takes precedence over the store's idempotency check: resubmitting evidence that was
already accepted, *after* the record has since been revoked or quarantined, reports `Ineligible` rather than
replaying `Applied`. Nothing is lost — the original update is durable and in the history — but reconcile retries
against the history rather than reading that as "it never landed".

**History makes an update reconstructable.** Each *counted* update's event carries the prior and new score, the
prior and new counters, the evidence ID, the rule version, and the `Actor` — the principal the commit ran under, recorded by the
store from the host's authorization and never from anything the caller put in the event. Read it through
`GetHistoryAsync` like any other transition; `stored.Event.Confidence` is `null` for the events that carried none.
An *uncounted* submission has no event, by construction — the ledger row is its audit trail. Listing that ledger is
still not a port operation; its retention is covered by a record's erasure, which removes every evidence row that
named it.

## Recording what reuse was worth

`ExperienceLifecycleService.ApplyEvidenceAsync` moves a score once you already know what reuse was worth.
`ExperienceReuseFeedbackService.RecordAsync` is how you find out — and it is deliberately hard to make it say yes.

```csharp
var result = await feedback.RecordAsync(
    hostAuthorization,
    new ExperienceReuseFeedback(
        FeedbackId: feedbackId,                       // the whole submission's idempotency key
        RunId: runId,                                 // the run the records were injected into
        Scope: recordScope,
        ExposedExperienceIds: injection.InjectedExperienceIds,
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new ReuseMeasure("tool-calls", 7),   // a name you chose, and a number
        ObservedAt: DateTimeOffset.UtcNow,
        TrialLabel: "memory-enabled"),                // optional, declared up front
    cancellationToken);

// Outcome: Recorded. Benefit: Unknown. Nothing moved -- and that is the correct answer.
```

**Exposure is not attribution.** That call records exactly what happened: a run saw these records and came out this
way. It does not record that the records *helped*, because nothing established that. Benefit is `Unknown`, no
confidence evidence is submitted, and no record's score, counters, or status changes. Almost every submission a real
host makes will end here, and it should.

**A bare claim is never attribution.** `ClaimedBenefit` is stored verbatim, so you can later compare what hosts
believed against what evidence established, and it is never acted on. Exactly two shapes move a score:

| Attribution | What it must carry | What it produces |
| --- | --- | --- |
| `HumanReuseAssessment` | improvement or harm, the exposed records it is about, an auditable rationale, an `AssessmentId` naming the **host-established review** it came out of, optionally the verification round it was made against — and **no reviewer field**, because the reviewer is your `AuthorizationContext.PrincipalId` | `Human` evidence, keyed `human:{principal}:{run}` |
| `ComparativeEvaluationResult` | the same records and rationale, plus the run it evaluated (which must be *this* run), its verification round, and the evidence it reached its conclusion from — each piece of which must name that same round | `Machine` evidence, keyed `machine:{run}:{round}` |

**This library does not implement a comparative evaluator**; it defines the contract and verifies the result it is
given. Evidence from another round is not evidence about this comparison, and is refused.

> **Read this before you wire either one up — the library cannot check that any of it is true.**
> `RunId`, `AssessmentId`, and `VerificationRoundId` are all host-established identifiers. Nothing here can verify
> that a run happened, that a round was closed, or that a human made an assessment and meant it. What the library
> actually guarantees is narrow: the reviewer is your `AuthorizationContext.PrincipalId` rather than anything on the
> submission, and one reviewer's opinion about one run counts once. Because the *caller* supplies `RunId`, a host
> that lets agent output populate it hands the agent a fresh independence key on every call — and with it the
> ability to contest its own stored lessons over and over. The human shape is the weakest boundary in this library;
> requiring an `AssessmentId` makes a moved score traceable back to a review that exists, and that is all it does.
> Establish these from your own run and review bookkeeping, exactly as you establish `AuthorizationContext`, and
> never from anything an agent produced.

**A failed attribution costs the attribution, not the exposure.** An attribution that does not meet its evidence
requirements — no `AssessmentId`, no evidence behind a comparison, a blank rationale, a benefit of `Unknown` — is
dropped: the submission is still recorded, with benefit `Unknown`, no confidence submission, and a `Reason` naming
what was refused. Only a structurally incoherent submission is `Invalid` with nothing written: no feedback ID, no
records, an attribution naming a record the run never saw, or a comparative result about a *different* run. Losing
a true exposure to punish a bad attribution would throw away the one thing that was never in doubt.

**Improvement supports, harm contradicts.** An accepted attribution submits one piece of evidence per attributed
record, through `ApplyEvidenceAsync` and nothing else — so independence keying, duplicate suppression, the revision
guard, the eligibility gate, and the audit trail all apply exactly as described above. Attributed harm therefore
contests each record in the same transaction that records the evidence. **Nothing is ever deleted**: the record
stays, and its own history carries the reason.

| Situation | What happens |
| --- | --- |
| Records injected, no attribution | Exposure stored, benefit `Unknown`, nothing moves |
| Caller claims improvement with no evidence | Same — the claim is recorded, not acted on |
| Authorized human assessment | Supporting evidence per attributed record, counted once each |
| Comparative evaluator result | Supporting evidence per attributed record, as machine evidence |
| Attributed harm | Contradicting evidence per record; each `Contested`; all still present |
| Attribution fails its evidence requirements | Exposure recorded, benefit `Unknown`, `Reason` says what was refused |
| Attribution names a record the run never saw, or a comparative result names another run | `Invalid` — nothing written |
| Same feedback ID, identical content — in any record order | `AlreadyRecorded` — nothing written twice, nothing counted twice |
| Same feedback ID, different content | `Conflict` — nothing written; the stored submission's records are reported back when you are authorized for its scope |
| One record's submission fails | The rest still apply; that one is `Failed` and `Retryable` |
| Cancelled part-way through | What was decided is returned; the rest are `Failed` and `Retryable` — never an exception |
| The same run already produced evidence for a record | `EvidenceApplied` with `Counted: false` — the existing independence rule |
| An exposed record is `Candidate`, `Quarantined`, `Stale`, `Superseded`, or `Revoked` | Exposure recorded, `Ineligible`, nothing written for it |
| An exposed ID does not exist in that scope, or is readable only through a sharing grant | Exposure recorded, `Unresolved` with the reason, nothing written for it |
| Run scope outside the authorization | `Denied` before any write |
| More than `MaxExposedRecords` (64) exposed records | `Invalid` — nothing written |

**Retrying is how you recover, and it converges.** Each attributed record's evidence ID and event ID are *derived by
hash* from the feedback ID and the experience ID, and the submission's `OccurredAt` is your own `ObservedAt`. So
resubmitting the identical feedback re-derives the identical identifiers: the ledger write is a no-op, and any
outstanding confidence submission replays instead of counting a second time. Read `result.IsRetryable` and resubmit
the same `ExperienceReuseFeedback` — do not build a new one. The *set* of exposed records is what is compared, not
the order you listed them in, so a retry assembled differently from the original still converges rather than
colliding.

**The exposure is written before any score moves.** The feedback ledger commits first, so what a run saw is durable
even if every confidence submission then fails. Each record is then submitted independently, which is what makes a
partial failure partial. One consequence is worth knowing: an attributed exposure's stored `evidence_id` says
*which* ID the submission uses, not that it landed — so an auditor joining the two ledgers uses a `LEFT JOIN`, and
reads a missing row as "attributed, not yet counted", which is exactly the work a retry converges on.

**The fan-out is bounded by size, not by time.** A submission may name at most `MaxExposedRecords` (64) records, and
each attributed one costs a scoped read plus its own transaction, run sequentially, with only your
`CancellationToken` as a time bound. There is deliberately no internal budget, unlike retrieval's: abandoning a
retrieval yields an empty result and the agent runs on, whereas abandoning half a fan-out would leave some records
moved and others not, with no way to tell which from a timeout alone. Pass a token with a deadline if you need one —
what was decided by then is still reported.

**`TrialLabel` is for measuring, not for filtering afterwards.** It names the experimental condition a run was
declared to belong to — `"memory-enabled"`, `"memory-disabled"` — so a later measurement aggregates conditions that
were fixed in advance rather than subsets chosen once the results are in.

## Indexing experience for semantic reuse

A record that is committed is already reusable: it is text-searchable the moment it lands. Indexing gives it a
second way to be found — by meaning — and it is **derived data** throughout. Nothing about the canonical write
depends on an embedding provider being up.

If an `ExperienceIndexingService` is registered, finalization embeds each record it commits, right after the commit:

```csharp
var result = await finalization.FinalizeAsync(request, cancellationToken);

if (result.Indexing is { IsIndexed: false } indexing)
{
    // Never a reason to treat the record as anything less than durable.
    logger.LogWarning("Experience {Id} is {Status} but not indexed ({Outcome}, retryable: {Retryable}): {Reason}",
        result.ExperienceId, result.Status, indexing.Outcome, indexing.IsRetryable, indexing.Failure?.Reason);
}
```

**Only the sanitized retrieval summary is embedded** — the task ID, the sanitized task summary, and the reflection's
lesson, the same three fields the text index analyzes. Attempts, tool calls, evidence, provenance, and environment
metadata are never sent to a provider. The summary is read from the database at index time, not from a record the
caller happens to be holding, so what is embedded is what is really stored, at the revision it is really stored at.

The two channels read the same *fields* but not necessarily the same *length*: the embedded summary is capped at
8,192 characters (`ExperienceRetrievalSummary.MaxLength`, so the hashed text and the text sent to a provider are
always identical), while `0003` analyzes the concatenation up to 100,000. A record whose summary and lesson together
run past 8 KB is therefore matched on more of its text by words than by meaning. Both caps are far past any
realistic summary.

**Only records a search could actually return are embedded.** The indexing scan applies the same status filter and
confidence floor the vector search applies, and the post-commit hook checks the record before calling anything, so
a `Quarantined`, `Revoked`, `Superseded`, or `Candidate` record's summary and lesson never leave the database for a
third party — its vector could never be returned anyway.

Each stored vector carries **model ID, dimension, content hash, and source revision**, kept entirely separate from
lifecycle state. None of them ever influences eligibility, status, or reuse confidence; they exist so a write can be
conditional, a re-index can be free, and a query vector is never compared with something it is not comparable with.

| Outcome | When | What was written |
| --- | --- | --- |
| `Indexed` | The summary was embedded and stored | The vector and its descriptor |
| `Skipped` | This model already embedded exactly this text | Nothing — and **no provider call was made** |
| `Stale` | The record moved to a newer revision before the write landed | Nothing; the stored vector is unchanged. Retryable |
| `Missing` | The record no longer exists in this scope | Nothing, and **no row is created** — an in-flight write cannot resurrect a deleted record |
| `Ineligible` | The record's status or confidence means a search could never return it | Nothing, and **nothing was sent to a provider** |
| `ProviderFailed` | The provider threw, timed out, or returned a vector of the wrong width or with a non-finite component | Nothing. The record stays committed, durable, and text-searchable. Retryable |
| `IndexFailed` | The index itself failed or refused the write | Nothing. Retryable |
| `Denied` | The scope lies outside the authorization | Nothing was read, embedded, or written |

**Re-indexing is explicit, scoped, and idempotent.** It never runs on its own:

```csharp
var pass = await indexing.ReindexAsync(
    authorization,
    new ReindexExperienceRequest(scope, ExperienceIds: null, Limit: 100),   // bounded; pass again to page
    cancellationToken);

logger.LogInformation("{Examined} examined, {Indexed} re-embedded, {Skipped} unchanged, {Failed} failed",
    pass.Examined, pass.Indexed, pass.Skipped, pass.Failed);
```

A pass is **bounded and resumable**: records are considered in ascending `ExperienceId` order, and `pass.LastExaminedId`
is the cursor to hand to the next pass's `StartAfterId`. Keep going until it comes back `null`, which is how a scope
larger than one page is walked to the end.

**Provider calls are batched; writes are not.** The records a pass has to embed go to the provider
`EmbeddingBatchSize` at a time (an init-only property on `ReindexExperienceRequest`: default 16, from 1 to 128)
through `IExperienceEmbeddingGenerator.GenerateBatchAsync`, so a pass of `n` records to embed makes `ceil(n / 16)`
provider calls rather than `n`. Each record is then written on its own, conditionally on the revision it was read at,
so a record that moved on is `Stale` alone and the rest of its batch is written. A provider batch is all or nothing,
so one that throws, or answers with the wrong number of vectors, is retried one record at a time: a record the
provider always refuses (too long, filtered) is `ProviderFailed` alone, exactly as before batching, instead of
keeping its batch-mates unindexed on every pass. An outage costs one extra provider call per failed batch, and a pass
that fails partway still says exactly which records it wrote. `Skipped` records never take a place in a batch. Tune
`EmbeddingBatchSize` to your provider's per-request limit: `AiExperienceEmbeddingGenerator` forwards a batch as it
stands. A generator that implements only `GenerateAsync` gets the port's default `GenerateBatchAsync`,
which calls it once per text — correct, but with no saving; `AiExperienceEmbeddingGenerator` overrides it with one
`Microsoft.Extensions.AI` call per batch.

The content hash covers the model ID and the normalized summary, so a record whose vector already came from this
model and this text is skipped **before** any provider call — running a pass twice over unchanged records costs one
read and nothing else. Changing the model looks exactly like changing the text, which is the point: two models
produce incomparable vectors, so "same text" alone must never be enough to skip.

**The approximate-nearest-neighbour index is created out of band**, because it needs a dimension no shipped
migration can know:

```csharp
await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(dataSource, dimension: 1536, cancellationToken);
```

It is optional — every search is correct without it, using an exact scan — it makes search *approximate*, and
building it locks the table for the duration, so run it from a maintenance path. See the
[vectors README](src/AgentExperience.Storage.Postgres.Vectors/README.md) for why the `embedding` column is an
unconstrained `vector` and the index is a partial one over `embedding::vector(n)`.

## Retrieving applicable experience

Finding experience that applies to a task is one Core call: `ExperienceRetrievalService.RetrieveAsync`. It asks the
storage adapter for scope-, status- and confidence-filtered text matches — and, when a vector channel is wired in,
for the same thing matched on meaning — decides the remaining eligibility itself, and ranks what survives, always
returning a structured result rather than throwing.

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
| Text match | SQL | PostgreSQL full-text search over task ID, task summary, and reflection lesson (analyzed up to 100,000 characters) |
| Vector match | SQL | pgvector cosine distance over the embedding of those same three fields (embedded up to 8,192 characters), filtered to the query's own model and dimension |
| Expiry | Core | Last lifecycle activity older than `RetrievalPolicy.MaxAge` is excluded. `null` (the default) means no expiry |
| Environment | Core | Every required attribute must equal the record's `EnvironmentFingerprint.Metadata` entry; a missing key excludes the record. A request with no required attributes sets `EnvironmentUnrestricted` on the result |

Scope, status, and the confidence floor are pushed into **both** channels as the same predicates, so neither can
return something the other would have filtered out.

`result.Excluded` itemizes what the **Core** checks removed — expiry and environment — so "nothing matched" is
distinguishable from "something matched but was not reusable here". It is deliberately not a complete account of
everything filtered: scope, status, and the confidence floor are applied in SQL, so records they exclude never reach
Core and are never listed. That split is the point — a foreign-scope or revoked record must not be observable, even
as a count.

**Two channels, one answer.** When an embedding index and an embedding generator are both registered, the task text
is also embedded and searched as a vector, concurrently with the text search and inside the same timeout. The two
candidate lists are then deduplicated by `ExperienceId`, and a record found by both keeps the **higher** of its two
normalized relevances. Ranking runs once over the merged list, with the same five weights as before: there is no
sixth axis and no "found by both" bonus. An embedding can only make a record a *candidate* — it never decides
eligibility, status, or confidence.

**A vector channel that cannot be trusted produces an explicit text-only answer, never a failure.** The text
candidates still come back, and `result.TextOnly` is `true` with `result.VectorFallback.Reason` saying which:

| `TextOnlyReason` | When | Vector comparison attempted? |
| --- | --- | --- |
| `NotConfigured` | No embedding index or no generator is registered — a supported, text-only deployment | No channel exists |
| `ProviderUnavailable` | The provider threw, cancelled for its own reasons (a client-side request timeout), or returned a query vector of the wrong width or with a non-finite component | No — caught before any query is issued |
| `ModelMismatch` | Every embedding stored in this scope came from a different model | No — excluded by the query's own predicate |
| `DimensionMismatch` | Every embedding stored in this scope is a different width | No — excluded by the query's own predicate |
| `VectorSearchFailed` | The vector search threw, was denied, or was refused as malformed | Attempted; nothing usable came back |

`TextOnly` is never set merely because the vector channel matched nothing: "nothing was semantically similar" and
"the vector channel could not be trusted" are different claims, and only the second one is a reason to look at your
wiring.

**There is a recall ceiling, and it is visible.** Each channel returns at most `RetrievalPolicy.CandidateLimit`
candidates (default 50), ordered by its own relevance, and ranking only ever sees those. So a record with a weaker
match but strong confidence, recency, or status is not ranked at all once that many stronger matches exist in both
channels: the weighting can only reorder what the ceiling let through. When *either* channel reaches its ceiling,
`result.Truncated` is `true` — the records beyond it are in no exclusion list either, because no eligibility check
ever looked at them. Raise `CandidateLimit` or narrow the task text when that matters. `request.Limit` may not
exceed `CandidateLimit`; a larger value is rejected rather than quietly capped.

**Ranking is explainable.** Every returned record carries all five normalized components (each in 0–1) and the
effective weight applied to it, so the score is always reproducible from what the result holds.

| Component | Default weight | Normalized as |
| --- | --- | --- |
| Relevance | 0.35 | `ts_rank_cd` of the text match, or `1 - cosine_distance / 2` of the vector match — whichever is higher for that record — normalized to 0–1 |
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
| Request scope outside the authorization | `Denied` | Empty; **neither channel is issued a query, and nothing is embedded** |
| The **text** search failed, or a candidate from either channel could not be read, came back out of scope, or was returned twice | `Failed`, with `result.Failure` | Empty, never unfiltered |
| The **vector** channel failed, timed out on its own, or was incomparable | `Completed`, with `result.TextOnly` and `result.VectorFallback` | The text channel's eligible records, ranked |
| Caller cancelled | `OperationCanceledException`, unwrapped and distinct from the timeout | — |

`result.Failure.Reason` is content-free and safe to log. `result.Failure.Exception`, when present, is whatever the
port threw — a driver message can quote SQL text or connection detail, so treat it as local diagnostics rather than
something to pass on.

Retrieval returns ranked records and the evidence for their ranking. Turning them into a labeled Historical
Reference and injecting it into an agent is a separate step, described next — and retrieved content never becomes
authority.

## Injecting Historical Reference into MAF

`ExperienceContextProvider` closes the loop. It is a MAF `AIContextProvider` that, before each invocation, retrieves
the applicable experience, re-checks each candidate one last time, asks the host's risk policy, and injects what
survives as **one delimited, labeled Historical Reference message**. The host adds it to the agent itself:

```csharp
using AgentExperience.MicrosoftAgentFramework.Injection;

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions { Tools = tools },
    AIContextProviders =
    [
        new ExperienceContextProvider(retrieval, recordStore, new ExperienceInjectionOptions
        {
            ResolveRequest = context => new RetrieveExperienceRequest(
                Authorization: hostAuthorization,
                Scope: hostScope,
                // Never `Last()`: the list can be empty, and mid-conversation the last message is a
                // tool result, not the task. Retrieval caps task text at
                // `ExperienceCandidateQuery.MaxTaskTextLength` (4096 characters).
                TaskText: context.Messages
                    .LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text
                    ?? taskDescription),
            DecideInjection = d => riskPolicy.Allows(d.Current) ? InjectionDecision.Permit : InjectionDecision.Deny("risk policy"),
            OnContextInjected = result => logger.LogDebug("Injected {Count}, omitted {Omitted}", result.InjectedCount, result.Omitted.Count),
        }),
    ],
});
```

Each record in the block carries its **source** (experience ID, source run ID, task ID), its **confidence**, its
**applicability** (the rank score and every component with the weight applied to it, labeled *as ranked at
retrieval*), **when it was learned and last revalidated**, the **environment** it came from, an **evidence
summary** — lesson, reuse guidance, preconditions, warnings, verification status, and evidence ID count — and, for a
verified record, the **approach**: the ordered tool *names* its final attempt called. Tool arguments, tool results,
attempt results, attempt errors, and evidence detail are never serialized, so a captured payload cannot reach a model
through injection; the tool names are the one thing that does cross, and they are derived from the record's own
attempts rather than from a reflection's prose, so a host reflector cannot widen what the block emits.

**The label is hygiene, not a security control.** The block states that it is untrusted reference material and that
nothing inside it authorizes anything. That wording helps a well-behaved model treat retrieved text as data and
gives a human reading a transcript the provenance — it does not make a model obey, and this project does not claim
it does. What actually stops an unauthorized call is the authorization boundary around tools and policy, which lives
entirely outside the block. An integration test pins that down: a fake model *obeys* an injected instruction to call
a guarded tool, and the approval boundary denies the call anyway.

| Situation | What the agent sees |
| --- | --- |
| Eligible records found | A delimited block, in rank order, within 8 records and 16 KB of UTF-8 (both configurable and validated) |
| Nothing matched, retrieval timed out or failed, or the final check overran its bound | No injected context at all; the agent runs normally, the outcome is reported, and nothing is fabricated |
| The request scope lies outside the host authorization | Nothing, reported as `RetrievalDenied`; no search is issued, and a foreign scope reveals nothing |
| A record revoked, re-scoped, re-scored below the confidence floor, aged past `MaxAge`, environment-mismatched, or unreadable since retrieval | It is absent from the block; the omission is recorded with the rule that dropped it and the stored record is untouched |
| The host's `DecideInjection` denies a record | Absent whatever its stored confidence or status; the denial is recorded and nothing is written |
| More records, or more bytes, than the limits allow | Whole records are dropped — never cut — and each omission is recorded as `OverRecordLimit` or `OverByteBudget` |

The final eligibility check runs immediately before the payload is built and re-applies **every rule retrieval
applies** — status, the reuse-confidence floor, `MaxAge`, and the request's required environment attributes — to the
record as it stands now, so it catches what changed since retrieval. What it cannot do is reach backwards: once a
block has been handed to a model, a later revocation cannot retract it, and the provider says so rather than
implying otherwise.

**Injected blocks accumulate in a reused session.** A block injected on one turn can stay in the `AgentSession`'s
conversation, so a later turn shows the model the fresh block *and* the earlier ones. MAF filters the provider's
input to external messages, so it cannot reliably see or strip its own earlier blocks, and it does not pretend to.
That means `MaxBytes` bounds one injected block rather than a conversation, and revocation only affects injections
that have not happened yet. Use a fresh session per task where either matters.

See the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md#injecting-historical-reference) for
the payload shape, the options, and the failure behaviour.

## Sharing experience across scopes

Scope is otherwise all-or-nothing: a record is readable only from the exact scope that owns it. A **sharing grant**
is the one, audited exception. An administrator names one record, one recipient scope, a reason, and an expiry, and
that recipient can *read* that record until the grant expires or is revoked.

```csharp
using AgentExperience.Storage.Postgres.DependencyInjection;

services.AddAgentExperiencePostgresGrantStore();   // IExperienceGrantStore

// The host decides who may administer sharing. This is a separate, explicit input: it is never
// derived from an AuthorizationContext, from a role string, or from the requesting scope.
var administration = new GrantAdministration(
    AdministratorPrincipalId: currentUser.Id,
    AuthorizedAt: DateTimeOffset.UtcNow);

var result = await grants.CreateAsync(
    hostAuthorization,                                  // the caller's own authority, over the owner scope
    administration,                                     // authority to administer sharing
    new ExperienceGrantRequest(
        GrantId: Guid.NewGuid(),
        ExperienceId: recordId,
        RecordScope: ownerScope,                        // where the record lives: team-a
        RecipientScope: ownerScope with { TeamId = "team-b" },
        Reason: "team-b owns the follow-up work",
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)),
    cancellationToken);
// Created — the grant row and its audit event were written in one transaction.
```

**A grant's lifetime is bounded, and there is no permanent grant.** `PostgresExperienceGrantPolicy` carries the
maximum lifetime a *new* grant may be issued with — 90 days by default — and an expiry further ahead than that is
`Invalid` on `ExpiresAt` with nothing written. `DateTimeOffset.MaxValue` is refused like any other over-long
expiry; an expiry exactly at the maximum is accepted. The bound is a policy on the store, not a hidden constant:

```csharp
services.AddAgentExperiencePostgresGrantStore(new PostgresExperienceGrantPolicy(TimeSpan.FromDays(30)));
```

It binds a grant when it is created and never afterwards. Raising the maximum does not extend a grant already
issued, and lowering it does not shorten one — end an over-long grant by revoking it. The existing rule that an
expiry may only ever shrink is unchanged. Underneath the policy the database keeps its own fixed, generous ceiling
(`expires_at <= issued_at + interval '10 years'`), so a writer that bypasses this library still cannot store a
grant that never ends.

**What a grant permits.** Reading, and only reading: `GetAsync`, the text channel, the vector channel, and
therefore injection, which re-reads through the same call. A granted record comes back exactly as its owner sees
it, still carrying the owner's scope. Creating records, committing lifecycle changes, reading lifecycle history,
listing what a scope holds, and issuing further grants are never inferred from a grant, and still need the caller's
own authority.

**What a grant can never do.**

| Rule | Where it is enforced |
| --- | --- |
| Relaxes only `TeamId`, `AgentId`, `UserId`; tenant, application, and project are always the record's own | Validation with the field path, *and* a `CHECK` constraint, so an unstorable grant is unstorable |
| Confers no write, no lifecycle history, and no enumeration | Every non-read statement keeps the exact-scope predicate |
| Stops permitting reads once `ExpiresAt` passes | The read predicate, against `clock_timestamp()` — the *database's* wall clock, never the caller's, and never the transaction's start time |
| Stops permitting reads the moment it is revoked | The same predicate; revocation appends an event and deletes nothing. At most one grant per (record, recipient scope) may be active at a time, so revoking the grant you know about really is the end of that recipient's access -- a second, overlapping one is refused as `Conflict` rather than stacked |
| Cannot be issued to last longer than the configured maximum, and can never be permanent | `Invalid` on `ExpiresAt` with nothing written, *and* a fixed `CHECK` ceiling underneath it |
| Cannot be issued or revoked without administrator authority | `Denied`, before any connection is opened |
| Changes nothing about the record: not its status, confidence, counters, or revision | The grant path never touches `experience_records` |

Grant enforcement lives in SQL, alongside the existing scope predicate, so the database can never return a record
the predicate did not permit and no application code is in a position to widen one. Revoking is an append:

```csharp
await grants.RevokeAsync(
    hostAuthorization,
    administration,
    new ExperienceGrantRevocation(grant.GrantId, ownerScope, "the collaboration ended"),
    cancellationToken);
// Revoked — the next read is denied, and the grant's history keeps both events.

var history = await grants.ListAsync(hostAuthorization, ownerScope, recordId, cancellationToken);
// Every grant over the record, revoked and expired ones included. Owner scope only: a recipient
// cannot enumerate the grants over a record it can read.
```

Nothing about sharing weakens eligibility. A shared record still has to be `Validated` or `Reinforced`, still has
to clear the confidence floor, expiry, and environment checks, and is ranked exactly like an owned one.

**A borrowed lesson is labelled as one.** The adapter is the only layer that knows a record came back through a
grant, so it says so: the flag travels on `ExperienceCandidate.SharedByGrant` and `RankedExperience.SharedByGrant`,
reaches the host's risk policy as `ExperienceInjectionDecisionContext.SharedByGrant`, and the injected Historical
Reference block carries a `Shared:` line (with no scope identifier in it). Everything downstream keeps its strict
"this must be my own record" check for anything that is *not* flagged, so a source that returns a foreign record
without declaring a grant is still dropped.

**A grant decides whether a borrowed lesson carries its approach.** A verified record's block includes the
`Approach:` line — the ordered tool names the run called — and for a borrowed record those are the *lending* scope's
tool names, which (`hr_salary_lookup`, `stripe_charge_prod`) are themselves information about its systems. So every
grant carries an immutable disclosure level, `ExperienceGrantRequest.Disclosure`:

```csharp
new ExperienceGrantRequest(grantId, recordId, ownerScope, recipientScope, reason, expiresAt,
    Disclosure: ExperienceGrantDisclosure.LessonAndApproach);   // default: LessonOnly
```

Under `LessonOnly` — the default — the block omits the `Approach:` line and, when the record has one, the `Shared:`
line says the grant withholds it; under `LessonAndApproach` the line is rendered exactly as the owner would see it.
The level governs the `Approach:` line **only**: the lesson, reuse guidance, preconditions and warnings are the
reflector's prose and are rendered unfiltered, so a tool name a reflector wrote into them reaches the model under
either level (see KL-8). Neither level shows a tool argument's value: a borrowed record's `Approach:` line is tool
names only whatever the reader's `ApproachArguments` allowlist says, because a grant was issued as consent to show
names, not argument values (story 6.2). The level is read from the same row that names the permitting grant, reaches the host's risk
policy as `ExperienceInjectionDecisionContext.GrantDisclosure`, and is recorded on the grant's issue and revoke events
and on every access row. The access row records the level the library applied at delivery, not whether an `Approach:`
line actually reached a model: the host may deny the record, the byte budget may drop it, or it may have no approach.
A store that says a record is shared but reports no level is rendered as `LessonOnly`, and the host decision can deny
a record but never widen its level. Only the *block* is governed: the `ExperienceRecord` a store returns to host code
is complete either way. The level cannot be changed in place — the database refuses the `UPDATE` — so to change it,
revoke the grant and issue a new one; the one-active-grant rule means the revoke comes first, so the recipient has no
access in the gap between the two calls.

**Upgrading changes behaviour, and the order matters.** Schema script `0011` gives every existing grant `LessonOnly`,
so a borrowed record's `Approach:` line disappears from injected blocks until the owner revokes the grant and issues a
`LessonAndApproach` replacement. Events and access rows written before `0011` read back with a `null` level: it was
never recorded. **Run `0011`, then deploy this build, and stop older writers first.** This build on a pre-`0011`
schema fails every grant-joined read with `42703` (undefined column), and an older build on a `0011` schema cannot
write grant events or access rows, because both now require a level. Both failures are loud by design; there is no
silent fallback.

**Two trails, and they answer different questions.** `experience_grant_events` records administration -- who
allowed what, under authority established when, until when, and when they stopped allowing it -- and
`IExperienceGrantStore.GetHistoryAsync` reads one grant's trail. It answers "who permitted this?".

"Who read it?" is the **access log**, a separate, optional ledger:

```csharp
services.AddAgentExperiencePostgresGrantAccessLog(
    onNotRecorded: failure => logger.LogError(failure.Failure, "grant access row not written"),
    mode: ExperienceGrantAuditingMode.BestEffort);   // or Required
```

With it wired, every record a grant *delivers* appends a row naming the grant, the record **and the revision that
was disclosed**, the owner scope, the recipient scope, the reading principal, the host's correlation ID for the
work behind the read, and when. The read also tells the caller **which** grant permitted it —
`ExperienceRecordGetResult.PermittingGrantId` and `ExperienceCandidate.PermittingGrantId`, carried on to
`RankedExperience` and to the host's `ExperienceInjectionDecisionContext` — so an injected lesson can be tied back
to the sharing decision behind it. The grant ID is for the host: the injected block still names no grant and no
scope.

**What is audited.** Every read that hands a caller a record it does not own. That is `GetAsync` (the
pre-injection re-read included) *and* both search channels: `ExperienceCandidate.Record` is the record read back
**in full**, so a host consuming `IExperienceCandidateSource` or `IExperienceEmbeddingIndex` directly receives
complete foreign records — a search result is a disclosure, not a notice that something matched. A search's rows
are written in **one** statement, so auditing costs one round trip per search rather than one per row.

Two reads are not deliveries and write nothing. An owner reading its own record: no grant permitted it. And a read
the caller refuses after fetching it *because* a grant is what made it readable — declare it with
`ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck)`, as Core's confidence path does, since a grant never
confers writing.

**Reading the trail** is `IExperienceGrantAccessLog.QueryAsync`: owner scope only, bounded and cursored, mirroring
`IExperienceGrantStore.GetHistoryAsync`. A recipient cannot enumerate who else read a record it can read.

**Auditing never fails silently.** `BestEffort` — the default — returns the records and reports the failed write
through `onNotRecorded`, which is why that callback is required rather than optional. `Required` fails closed: a
`GetAsync` returns `NotFound`, the same answer a record no grant permitted would give, and a search returns no
candidates at all rather than the subset that needed no grant. A blank `AuthorizationContext.PrincipalId` is itself
an audit failure — a row that cannot say *who* read the record does not answer the question the ledger exists for.
Rows are append-only in the database, so a delivery cannot be edited or deleted out of the trail afterwards.

**What it costs.** Every disclosing read does a second, synchronous round trip on a pooled connection before it
returns, and under `Required` read availability becomes a function of *write* availability. That is the mode's
promise, not a bug; the `dataSource` overloads exist largely so the ledger can have its own pool and a slow ledger
cannot exhaust the connections reads depend on. Wire nothing and auditing is off: no extra write, no extra round
trip, no extra failure mode, and a deployment behaves exactly as before. Registrations use `TryAdd`, so a host that
registered its own store or candidate source first keeps it — and takes on the obligation to honour the policy
itself.

**Source-compatible, not binary-compatible.** Everything below is additive at the source level — defaulted
positional parameters, defaulted constructor arguments, and one default interface method — so code recompiles
unchanged. None of it is binary-compatible, so recompile rather than drop in the new assemblies:

| Type | Change |
| --- | --- |
| `ExperienceRecordGetResult` | gained `Guid? PermittingGrantId = null` |
| `ExperienceCandidate` | gained `Guid? PermittingGrantId = null` |
| `ExperienceCandidateQuery`, `ExperienceVectorQuery` | gained `string? CorrelationId = null` |
| `RankedExperience`, `ExperienceInjectionDecisionContext` | gained `Guid? PermittingGrantId = null` |
| `IExperienceRecordStore` | gained a defaulted `GetAsync(..., ExperienceReadOptions, ...)` overload that forwards to the existing one |
| `PostgresExperienceRecordStore`, `PostgresExperienceCandidateSource`, `PostgresExperienceEmbeddingIndex` | constructors gained `ExperienceGrantAuditing? auditing = null` |
| `PostgresExperienceGrantStore` | constructor gained `PostgresExperienceGrantPolicy? policy = null, TimeProvider? timeProvider = null` |

The one *behavioural* break is deliberate: a grant issued with an expiry more than 90 days out — including
`DateTimeOffset.MaxValue` — is now `Invalid`. Configure `PostgresExperienceGrantPolicy` if your deployment needs a
different window.

**Two deployment notes.** Reading through a grant needs `SELECT` on `agent_experience.experience_grants`; a role
without it, or a database that has not applied `0005` yet, falls back to the exact-scope predicate -- which narrows
what a read returns rather than failing it -- and reports it once through the reader's optional
`onGrantsUnavailable` callback. And `NotFound` does not mean a `GrantId` is free: the insert reads the record row
first, so a create naming a record that is not in the owner scope selects nothing and reports `NotFound` before the
primary key is ever tested -- even when that `GrantId` is already stored. Only `Created` and `Conflict` say anything
about the ID, so generate a fresh one per attempt rather than inferring availability from `NotFound`.

## Wiring it all together

Each package registers its own services, so a host never names a concrete type:

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres.DependencyInjection;
using AgentExperience.Storage.Postgres.Vectors.DependencyInjection;   // optional: the vector channel

services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddAgentExperiencePostgresStore();                     // IExperienceRecordStore
services.AddAgentExperiencePostgresCandidateSource();           // IExperienceCandidateSource
services.AddAgentExperiencePostgresGrantStore();                // IExperienceGrantStore, optional: only a host
                                                                //    that shares records across scopes needs it
services.AddAgentExperiencePostgresReuseFeedbackStore();        // IExperienceReuseFeedbackStore, optional: only a
                                                                //    host that records reuse feedback needs it
services.AddAgentExperiencePostgresGrantAccessLog(              // IExperienceGrantAccessLog, optional: only a host
    onNotRecorded: failure => logger.LogError(                  //    that wants to know who read shared records
        failure.Failure, "grant access row not written"));      //    needs it. Off unless wired.
services.AddAgentExperiencePostgresEmbeddingIndex();            // IExperienceEmbeddingIndex
services.AddAgentExperienceEmbeddingGenerator();                // IExperienceEmbeddingGenerator, over a registered
                                                                //    IEmbeddingGenerator<string, Embedding<float>>
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService, ExperienceFinalizationService
services.AddAgentExperienceIndexing();                          // ExperienceIndexingService, and finalization's
                                                                //    post-commit hook, in either registration order
services.AddAgentExperienceRetrieval();                         // ExperienceRetrievalService
// -> defaults to RetrievalPolicy.Default and RankingWeights.Default; pass your own to override
// -> hybrid, because an index *and* a generator are registered; text-only, and flagged, if either is missing
services.AddAgentExperienceReuseFeedback();                     // ExperienceReuseFeedbackService, over the ledger
                                                                //    above and the lifecycle service

// Injection has no registration of its own: ExperienceContextProvider needs a per-host resolver and
// risk decision, so the host constructs it and adds it to ChatClientAgentOptions.AIContextProviders.
// See "Injecting Historical Reference into MAF" above.
```

Schema comes in two calls, matching that split:

```csharp
// As the owner role, on every deploy. The stores themselves connect as the application role.
await ExperienceSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);        // 0001-0003 and 0005-0013, always
await ExperienceVectorSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);  // 0004, only with the vector channel
await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(                     // last, so it covers both
    ownerDataSource,
    new ExperienceApplicationRoleOptions("agent_experience_app") { AllowErasure = true },
    cancellationToken);
```

Creating the two roles, and moving an existing single-role database to them, is in
[Store: deploying with two roles](src/AgentExperience.Storage.Postgres/README.md#deploying-with-two-roles).

The vector registrations and the second migration are optional, and genuinely so: leave them out and everything
still works — finalization commits records with no indexing hook, and retrieval answers from text alone with
`TextOnly` set to `NotConfigured`. That is also why the embedding schema is not in the base adapter's script list:
`CREATE EXTENSION vector` needs a superuser, and a text-only deployment must never be made to run it for a feature
it has not enabled.

`AgentExperience.Abstractions` stays BCL-only; only `Core` and the storage adapter take
`Microsoft.Extensions.DependencyInjection.Abstractions`, and every registration uses `TryAdd`, so a host's own
implementation wins.

The MAF adapter can drive finalization for you: set `FinalizationService` and `ResolveFinalization` on
`ExperienceCaptureOptions` and every successfully captured invocation is finalized right after it is completed. See
the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md#finalizing-captured-runs).

## Telemetry

Core, the MAF adapter and the PostgreSQL adapter's erasure paths emit OpenTelemetry-compatible spans and metrics
through the BCL's `ActivitySource` and `Meter`, under `AgentExperience.Core`, `AgentExperience.MicrosoftAgentFramework`
and `AgentExperience.Storage.Postgres`. The library references no
OpenTelemetry package and never exports anything itself: a host subscribes with `AddSource("AgentExperience.*")` and
`AddMeter("AgentExperience.*")`. Each operation emits one span (`agentexperience.<operation>`) and one count and
duration. A counter of failures, classified into four alertable classes, moves only when an operation throws. Metric
dimensions are bounded, and identifiers appear on spans only. No captured content, payload or exception message
reaches either.

The full contract lists every source, span, instrument, unit, dimension value and span attribute, and what each
operation emits: [`docs/telemetry.md`](docs/telemetry.md). Erasure is instrumented too (`delete`,
`retention.sweep`, `grant.purge`), and its telemetry carries nothing that was erased.

## Design principles

- **Hexagonal core.** `Abstractions` depends only on the BCL; `Core` adds a redaction primitive and the dependency-injection *abstractions* it needs to register its own services. MAF, databases, models, and telemetry stay in adapters. Dependency-boundary tests enforce this in CI.
- **Failure-preserving capture.** Failed and cancelled runs are recorded through an outer lifecycle path, never only a success callback.
- **Evidence before trust.** Verification is deterministic and bound to a host-closed round and artifact revision. A completion score is never mistaken for reuse confidence.
- **Sanitize before anything is stored.** Unknown payload fields are dropped by default, and secrets are redacted from nested values.
- **Reuse, don't rebuild.** MAF middleware and `Microsoft.Extensions.Compliance.Redaction` are used at the edges, and storage builds on Npgsql and pgvector rather than on a bespoke engine. Each integration was proven with executable compatibility tests before an adapter was built.
- **Derived data never blocks canonical data.** Embeddings are produced after the commit, through a replaceable provider port, and every failure leaves the record committed, text-searchable, and retryable.

## Repository layout

```
src/
  AgentExperience.Abstractions/             domain contracts and ports (BCL only)
  AgentExperience.Core/                     sanitization, capture, verification, reflection, lifecycle transitions, finalization, indexing, retrieval, reuse feedback
  AgentExperience.MicrosoftAgentFramework/  MAF adapter: run/tool capture and Historical Reference injection (pinned Microsoft.Agents.AI 1.22.0)
  AgentExperience.Storage.Postgres/         PostgreSQL Experience Record store, text search, sharing grants, reuse feedback ledger, and schema migrator (pinned Npgsql 10.0.3, dbup-postgresql 7.0.1, dbup-core 6.1.1)
  AgentExperience.Storage.Postgres.Vectors/ pgvector embedding index, conditional writes, scoped re-index, and vector search (pinned Npgsql 10.0.3, Pgvector 0.3.2, Microsoft.Extensions.AI.Abstractions 10.10.0)
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection, lifecycle, indexing, retrieval tests
  AgentExperience.MicrosoftAgentFramework.Tests/  real ChatClientAgent runs against a scripted fake model
  AgentExperience.Storage.Postgres.Tests/   store tests, mostly against a PostgreSQL container
  AgentExperience.Storage.Postgres.Vectors.Tests/  embedding index and hybrid retrieval, against a pgvector container
  AgentExperience.CompatibilityProof/       executable proofs for MAF hooks, context providers, pgvector, redaction
  AgentExperience.Sample.EndToEnd.Tests/    asserts the sample's seven stages, its determinism, and what it does not claim
  AgentExperience.ReuseBaseline/            the controlled reuse experiment and its golden reports
  AgentExperience.Release.Tests/            release gates: the public API baseline, pin agreement, the security-suite map, workflow guards
samples/
  AgentExperience.Sample.EndToEnd/          one runnable command: capture a wrong approach and a right one, verify, reflect, persist, retrieve, inject, record reuse
eng/                                        release tooling: package verification and the MAF compatibility probe
docs/                                       compatibility evidence for every pin, the security-suite map, and the original architecture research
_sdlc/                                      product brief, PRD, architecture, epics, and specs
```

## Build and test

Requires the [.NET SDK 10.0.302](https://dotnet.microsoft.com/) or a later feature band (see `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

Unit and MAF adapter tests run in memory, with no network, database, or model credentials. **No test anywhere needs model credentials**: every embedding in the test suite comes from a deterministic in-test generator. `AgentExperience.CompatibilityProof`, the `PostgresExperienceRecordStoreTests`, `PostgresExperienceCandidateSourceTests`, `PostgresLifecycleCommitTests`, `PostgresSupersessionAndAppendOnlyTests`, `PostgresGrantTests`, `PostgresConfidenceEvidenceTests`, `PostgresReuseFeedbackTests`, `PostgresFinalizationTests`, and `ExperienceSchemaMigratorTests`, `MigratorLogSilenceTests`, `PostgresDeletionTests`, and `PostgresApplicationRoleTests` in `AgentExperience.Storage.Postgres.Tests`, the `PlainPostgresMigrationTests` in the same project (a stock `postgres:16` image, proving the base schema needs nothing pgvector provides), and the `PostgresEmbeddingIndexTests` and `HybridRetrievalIntegrationTests` in `AgentExperience.Storage.Postgres.Vectors.Tests` start a PostgreSQL/pgvector container through Testcontainers, so they need Docker. If Testcontainers' Ryuk container fails to start under your local Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. To skip the container-backed tests:

```bash
dotnet test --filter "FullyQualifiedName!~CompatibilityProof&FullyQualifiedName!~PostgresExperienceRecordStoreTests&FullyQualifiedName!~PostgresExperienceCandidateSourceTests&FullyQualifiedName!~PostgresLifecycleCommitTests&FullyQualifiedName!~PostgresSupersessionAndAppendOnlyTests&FullyQualifiedName!~PostgresGrantTests&FullyQualifiedName!~PostgresConfidenceEvidenceTests&FullyQualifiedName!~PostgresReuseFeedbackTests&FullyQualifiedName!~PostgresFinalizationTests&FullyQualifiedName!~ExperienceSchemaMigratorTests&FullyQualifiedName!~MigratorLogSilenceTests&FullyQualifiedName!~PostgresDeletionTests&FullyQualifiedName!~PostgresApplicationRoleTests&FullyQualifiedName!~PlainPostgresMigrationTests&FullyQualifiedName!~PostgresEmbeddingIndexTests&FullyQualifiedName!~HybridRetrievalIntegrationTests"
```

To accept a deliberate public API change, regenerate the baseline and review the diff it leaves before committing
it — the baseline never updates itself:

```bash
AGENTEXPERIENCE_ACCEPT_API_CHANGES=true dotnet test tests/AgentExperience.Release.Tests --filter "FullyQualifiedName~PublicApi"
git diff tests/AgentExperience.Release.Tests/PublicApi/
```

Release verification — packing, package inspection, the pin evidence, and the order the checks run in — is in
[`RELEASING.md`](RELEASING.md).

### Run the sample

```bash
dotnet run --project samples/AgentExperience.Sample.EndToEnd
```

Seven stages, exit code 0, on a fresh clone: no Docker, no PostgreSQL, no model credentials, no network. Run it twice and the two transcripts are byte-identical. Set `AGENTEXPERIENCE_SAMPLE_POSTGRES` to a connection string to run the same seven stages against the real PostgreSQL adapters. See [`samples/AgentExperience.Sample.EndToEnd/README.md`](samples/AgentExperience.Sample.EndToEnd/README.md) for what the sample proves and what it deliberately does not.

## Roadmap

1. **Capture and explain agent experience** ✅ contracts, sanitization, capture, verification, reflection, MAF adapter
2. **Reuse relevant experience** ✅ PostgreSQL persistence, atomic audited lifecycle commits, one-call finalization of captured runs, bounded text retrieval with explainable ranking, revision-safe embedding ingestion with hybrid retrieval, and historical-reference injection into MAF
3. **Govern experience safely** ✅ explicit sharing grants, the full audited lifecycle transition table with supersession and database-enforced append-only logs, evidence-based confidence updates, and recording experience reuse feedback
4. **Operate and measure the learning loop** — the preview release
   - 4.1 ✅ OpenTelemetry-compatible instrumentation through the BCL's `ActivitySource` and `Meter` ([contract](docs/telemetry.md))
   - 4.2 ✅ the end-to-end MAF demonstration (`samples/AgentExperience.Sample.EndToEnd`)
   - 4.4 ✅ reuse measured against a controlled, pre-registered baseline, with a negative control
   - 4.5 ✅ deletion and expiry of library-owned data
   - 4.6 ✅ learn-from-failure through the adapter: retries as attempts of one run, and the approach in the injected block
   - 4.3 (this release) release hardening: versioning, package verification, the public API baseline, pin evidence, the security suite, and the MAF compatibility matrix — **as a preview**. The production-readiness claim is blocked by the [Known limits](#known-limits), by design

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
