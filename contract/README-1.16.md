# Contract 1.16: additions

This page lists what contract 1.16 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.15
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md),
[1.11](README-1.11.md), [1.12](README-1.12.md), [1.13](README-1.13.md), [1.14](README-1.14.md) and
[1.15](README-1.15.md)). Those pages are still normative and unchanged: a minor version only adds files, this page among
them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`.

A port that passes contract 1.16 passes every 1.0 to 1.15 fixture and the fixtures below.

## The lifecycle, now in the ports

Contract 1.0 kept "the lifecycle after finalization" out of scope: a port finalized, injected and searched, and never
moved a stored record's status, counters or revision. **From 1.16 a port may.** It moves a stored lesson through its
lifecycle exactly as .NET's [`ExperienceLifecycleService`](../src/AgentExperience.Core/Lifecycle/ExperienceLifecycleService.cs)
does over its stores:

- **`CommitAsync`**: a transition through the table in [Lifecycle](../docs/guide/lifecycle.md#the-transition-table)
  (reinforce, contest, mark stale, supersede, revoke; and a record's first event), with the supersession rules;
- **`ApplyEvidenceAsync`**: one piece of reuse evidence, with contract 1.15's engine, committed with its evidence row,
  counters, score, status change and event in one transaction;
- **`ReadConfidenceAsync`**: the score with host-trusted or unverified evidence left out, read from the history;
- and the store operations they use: **`CommitLifecycleEventAsync`** (the revision compare-and-set, idempotency by event
  ID and evidence ID, the independence key, the single-use assessment, the in-transaction supersession gate),
  **`GetHistoryAsync`** (keyset pages) and **`CheckSupersessionAsync`**, for an in-memory store and for PostgreSQL.

The other derived IDs 1.0 left out are in scope too: the reuse evidence and event IDs finalization derives
(`ExperienceFinalizationService.ReuseEvidenceIdFor` and `ReuseEventIdFor`, tags 4 and 5).

**Mixed fleets stay safe** because every write is a revision compare-and-set in the database: a .NET, a Python and a Go
writer that decide a transition against the same revision race on it, exactly one commits, and the others are
`StaleRevision` with the revision to re-decide against. A cross-language PostgreSQL test races all three.

## What a port runs, and what it does not yet

A contract 1.16 port's lifecycle service is .NET's with these settings, and passes the fixtures with them:

- **Independence is not verified** (story 27.7 brings the verifier): the service runs as .NET does under
  `IndependenceVerification.TrustHostSuppliedIdentifiers`. Only the own-run rule is checked (`Unverified`, `OwnRun`);
  every other identifier is taken as given, an assessment token a human submission presents is neither checked nor
  spent, and every update is admitted **`HostTrusted`**, which is stored with it. A .NET reader therefore sees port
  evidence for what it is: `ReadConfidenceAsync` with `ExcludeHostTrusted` or `VerifiedOnly` leaves it out.
- **A port's default is `HostTrustedEvidence = RecordedOnly`**, not .NET's `Counted`. .NET's own default is verified
  evidence; a port cannot verify yet, so by default every piece of evidence it applies is recorded on an event that
  moves the revision and nothing else (not the counters, the score, the status or `UpdatedAt`), exactly as .NET under
  `RecordedOnly`: a caller of a port cannot inflate or contest a lesson with invented run IDs. A host that accepts that
  risk opts into **`Counted`** explicitly, which is .NET's behaviour under `TrustHostSuppliedIdentifiers`. Every
  fixture names its mode, so the fixtures pin both.
- **No provenance signing** (27.7): a record's initial counters are "verified" for `ReadConfidenceAsync` exactly when
  its origin is `Finalized`, as in .NET with no signing options.
- **No indexing service** (27.6d): a commit that takes a record out of eligibility removes no embedding and reports no
  de-indexing result. As in .NET, a vector search already excludes it by status.
- **No reuse feedback** (27.6c) and **no finalization reuse evidence** (`ReuseEvidenceMode.SameTask`, off by default in
  .NET): a port finalizes as before, submitting nothing.
- **No capture service lookup**: with verification off, .NET's lifecycle service never asks one.
- **A host transaction must be READ COMMITTED.** A port whose PostgreSQL store can run inside a transaction the host
  opened (Go's `pgx.Tx`) refuses one at another isolation level: under `REPEATABLE READ` or `SERIALIZABLE` a lost race
  would re-read the record in the host's old snapshot and report a revision that never moves. .NET pins the commit to
  `READ COMMITTED` itself.

Where .NET would need one of these to decide, the port decides exactly as .NET does with it at its default or off
setting. Everything else is .NET's: the transition table, every refusal and its reason, the validation and its every
error path and message, the order of the checks, the engine's identity and score checks (1.15), and the store's
semantics below.

## The store's semantics

The fixtures pin them; in short (the [Lifecycle](../docs/guide/lifecycle.md) and
[Confidence](../docs/guide/confidence.md) guides have the reasons):

- **Validation, then authorization, then the store.** An invalid request is `Invalid` with every error (a path and a
  message, byte for byte, in .NET's order) and touches nothing; a scope the authorization does not permit is `Denied`.
  An authorization permits a scope when its tenant is non-blank and equal, and each bound it sets is equal (ordinal).
- **The revision.** `ExpectedRevision` must be the record's; a commit sets `ExpectedRevision + 1`. Otherwise
  `StaleRevision` with the current revision, nothing written. The prior status must be the stored one
  (`StatusMismatch`, with it); a first event's (none) falls back to its current status.
- **Idempotency.** An event ID already stored: every field equal (its `OccurredAt` compared truncated to whole
  microseconds, its scope included) is the original replayed (`Committed` with the revision it produced); otherwise
  `Conflict`. An evidence ID already stored with the same content (record, event ID when the row has one, kind, source,
  run, round, reviewer, rule version, detail, assessment) is the original replayed, with the numbers it was stored
  with; otherwise `Conflict`. A replay reports the admission the original was stored with.
- **Independence.** The first evidence for a record's independence key counts; a later one under a new evidence ID is
  recorded (not counted) and changes nothing else: no event, counters, status, revision or `UpdatedAt`. A
  recorded-only event (`RecordedOnly`) claims no key, but a second recorded-only submission for a key an event already
  recorded is a duplicate.
- **The assessment** a human update carries is spent once per record: another evidence ID presenting it is `Conflict`
  with an error on `Confidence.AssessmentId` (the service reports it as `Unverified`, `AssessmentTokenReplayed`). A
  port's own service never sets one (no verification), but its store honours one another writer set.
- **Supersession** is decided by the store inside the commit: the replacement must be in the record's exact scope,
  `Validated` or `Reinforced`, and not on a replacement chain leading back to the record. The two .NET stores order
  this gate differently, and each port's store follows its .NET counterpart: the in-memory store checks it before the
  revision, the PostgreSQL store after the event insert (so a stale supersession whose revision another event already
  took is `StaleRevision` in PostgreSQL and `ReplacementNotAllowed` in memory).
- **The clock.** A commit stamps the store's own clock on the event (`RecordedAt`) and, unless the evidence is recorded
  only, on the record (`UpdatedAt`), truncated to whole microseconds. A port's in-memory store takes its clock as a
  parameter.
- **The actor** stored on every event and evidence row is the authorization's principal, or none when it is blank.

**PostgreSQL.** [`sql/lifecycle.sql`](sql/lifecycle.sql) is the .NET store's own statements, generated from its source
and checked against it on every build, with the order a commit runs them in, the constraint whose violation each branch
recognises, and what each answers. A port runs them as written. The application role needs no new privileges: the 1.0
file already grants `INSERT` on both logs and `UPDATE` of the record's lifecycle columns. No schema object is added.

**Sealed records.** A port holds no key store, so it neither reads a sealed record (1.0) nor writes one's events: the
one statement `sql/lifecycle.sql` adds of its own, `sealed-record`, makes a port's commit against a sealed record
`NotFound`, and its history and read of one `NotFound` too. A port runs it only once its own transaction holds the
record's row lock (after the projection update, or on the duplicate path after `lock-revision-and-status`), so the .NET
sealing job, which locks the row `FOR UPDATE`, can never seal a record between the port's check and its write. (.NET in crypto-shredding mode seals an event's reason and
detail under the record's key; a port cannot.) This is the one place a port answers differently from .NET, and only
for a record .NET sealed.

## What 1.16 adds

- **sql:** [`lifecycle.sql`](sql/lifecycle.sql).
- **lifecycle-commit**, **lifecycle-apply**: two new fixture families, generated through .NET's lifecycle service over
  the .NET in-memory store.
- **lifecycle-sql**: a new fixture family (needs PostgreSQL), generated through the .NET PostgreSQL store.
- **The 1.15 `apply` cases now come from the production path.** Their generator computes every case
  `ApplyEvidenceAsync` can express through it (over the in-memory store, as a port runs it) and composes the rest (a
  status that refuses evidence, a submission the service refuses before computing, a reviewer on machine evidence, a
  stored score the store would not hold) from the same classes; the published 1.15 files are byte for byte unchanged.
  One thing they do not say: **the service refuses evidence against a record whose other counter is saturated too.**
  1.15's "the other counter may be anything" describes the heuristic alone; `ApplyEvidenceAsync` checks both stored
  counters and answers `Invalid` for either at `int.MaxValue` (`lifecycle-apply/saturated-counter`).

## The in-memory fixtures

```
fixtures/lifecycle-commit/<case>/   input.json -> expected.json
fixtures/lifecycle-apply/<case>/    input.json -> expected.json
```

`input.json` holds a `description`; `service`, the service's options (`engine`, as in 1.15's `apply`: `null`,
`"default"` or a host engine whose `score` is a number, `"NaN"`, `"Infinity"`, `"-Infinity"`, `"bayes"`, `"stored"` or
`"throws"`; and `hostTrustedEvidence`, `Counted` or `RecordedOnly`); `records`, stored records in 1.0's form, which an
in-memory store holds at the start, with no history; and `steps`, run in order, each with the `clock` the store reads
during it and one operation:

- **`commit`**: `authorization` and `request` (`eventId`, `experienceId`, `scope`, `priorStatus` (`null` for none),
  `currentStatus`, `reason`, `producer`, `occurredAt`, `expectedRevision`, `replacementExperienceId`) for the service's
  commit. A status no member names (`Archived`) is passed through to the store, which refuses it.
- **`apply`**: `authorization` and `request` (`eventId`, `experienceId`, `scope`, `evidenceId`, `kind`, `source`,
  `runId`, `verificationRoundId`, `reason`, `producer`, `occurredAt`, `detail`, `assessmentToken`) for the service's
  apply. A kind or source no member names (`Sideways`, `Oracle`) is refused by the service.
- **`checkSupersession`**: `authorization`, `scope`, `experienceId` and `replacementExperienceId`, for the store.
- **`history`**: `authorization` and `query` (`scope`, `experienceId`, `limit`, `startAfterRevision`), for the store.
- **`readConfidence`**: `authorization`, `scope`, `experienceId` and `filter` (`All`, `ExcludeHostTrusted`,
  `VerifiedOnly`, or a name no member has), for the service.
- **`derive`**: `runId`, `scope`, `experienceId` and `kind`: the reuse evidence ID (tag 4) and event ID (tag 5).

An `authorization` is `principalId` and the bounds (`tenantId`, then `applicationId` ... `userId`, `null` for
unbounded). An `occurredAt` of `0001-01-01T00:00:00+00:00` is unset.

`expected.json` holds `results`, one per step; `records`, every seeded record afterwards in its stored form, in input
order; and `histories`, every seeded record's whole history (`experienceId`, `events`), each event a row of
`agent_experience.lifecycle_events` with JSON values (instants as 1.0 writes them, `occurred_at` and `recorded_at`
truncated to whole microseconds; scores as numbers; `null` for none). A result is:

- **commit:** `outcome`, `event` (the event the service stamped, as given: `eventId`, `experienceRecordId`,
  `priorStatus`, `currentStatus`, `reason`, `producer`, `occurredAt` untruncated, `expectedRevision`,
  `replacementExperienceId`, `confidence`; `null` when it refused before stamping one), `revision`, `currentStatus`,
  `errors` (`path`, `message`) and `reason`.
- **apply:** `thrown` (`null`, or the exception the call threw: `exception`, `parameter`, `message` as in 1.15, a
  `HostEngineException` being the host engine's own), and otherwise `outcome`, `event`, `update` (1.15's update and
  `assessmentId`, `admission`), `revision`, `status`, `errors`, `reason`, `refusal` and `counted`.
- **checkSupersession:** `outcome`, `replacementStatus`, `errors`.
- **history:** `outcome`, `revision`, `events` (rows, as above), `errors`, `nextStartAfterRevision`.
- **readConfidence:** `outcome`, `report` (`experienceId`, `revision`, `status`, `filter`, `reuseConfidence`,
  `supportingValidations`, `contradictions`, the three `stored...` values, `verified`, `hostTrusted`, `unrecorded`,
  `origin`, `initial` and `hostTrustedRecordedOnly`, each count a `supporting` and `contradicting` pair) and `errors`.
- **derive:** `evidenceId` and `eventId`.

Numbers compare within 1e-12; everything else exactly, every message and reason byte for byte.

## The PostgreSQL fixtures

```
fixtures/lifecycle-sql/<case>/   seed.json + steps.json -> expected.json (needs PostgreSQL)
```

Against a database the .NET migrator applied, with the 1.0 privileges file applied (row-level security on):

1. Insert `seed.json`'s `records` as a search seed is inserted.
2. Run `steps.json`'s `steps` in order, each with the store's clock set to its `clock`, through the port's PostgreSQL
   store (`sql/lifecycle.sql`):
   - `commit`: `authorization`, `scope` and `event` (an event as above, its `confidence` an update with `assessmentId`
     and `admission`), for the store's commit -> `outcome`, `revision`, `currentStatus`, `errors`, `appliedConfidence`;
   - `history`, `checkSupersession`: as above;
   - `read`: `authorization`, `scope`, `experienceId`, for the record read -> `outcome`, `record` (stored form) and
     `errors`;
   - `erase`: as the schema's owner, 1.4's erasure call; its result is `null`.
3. Read, as the schema's owner, in the time zone UTC and each column as `::text`: `experience_records` (`experience_id`,
   `status`, `reuse_confidence`, `supporting_validations`, `contradictions`, `revision`, `updated_at`, `deleted_at`)
   ordered by `experience_id`; `lifecycle_events` (every column `insert-event` names) ordered by `experience_id`,
   `applied_revision`; and `confidence_evidence` (every column `insert-evidence` names, then `independence_key`)
   ordered by `experience_id`, `evidence_id`. Compare them, and the results, with `expected.json`.
