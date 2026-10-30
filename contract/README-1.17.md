# Contract 1.17: additions

This page lists what contract 1.17 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.16
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md),
[1.11](README-1.11.md), [1.12](README-1.12.md), [1.13](README-1.13.md), [1.14](README-1.14.md), [1.15](README-1.15.md)
and [1.16](README-1.16.md)). Those pages are still normative and unchanged: a minor version only adds files, this page
among them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`.

A port that passes contract 1.17 passes every 1.0 to 1.16 fixture and the fixtures below.

## Reuse feedback, now in the ports

Contract 1.16 left out "no reuse feedback (27.6c) and no finalization reuse evidence". **From 1.17 a port records both**,
exactly as .NET does:

- **`ExperienceReuseFeedbackService.RecordAsync`**
  ([source](../src/AgentExperience.Core/Feedback/ExperienceReuseFeedbackService.cs)): a run's feedback (which records it
  was exposed to, how it came out, what the host measured, and, only with evidence, whether reuse helped or hurt) is
  validated, written to the feedback ledger, and then, for each record an accepted attribution names, turned into
  confidence evidence through contract 1.16's `ApplyEvidenceAsync`. Exposure alone moves nothing; a caller's
  `ClaimedBenefit` is recorded and never acted on; improvement supports and harm contradicts (and contests).
- **The ledger**, `IExperienceReuseFeedbackStore.RecordAsync`, for an in-memory store and for PostgreSQL: one submission
  and its exposures written together or not at all, the feedback ID the idempotency key.
- **Finalization's reuse evidence** (`ExperienceFinalizationOptions.ReuseEvidence = SameTask` and `ContradictOnFailure`,
  both off by default in .NET): once a run's record is durable, machine evidence about each record the run was given on
  its own task, bound to the round finalization closed.

The derived IDs feedback submits under are in scope: **`ExperienceReuseFeedbackService.EvidenceIdFor` and `EventIdFor`**,
SHA-256 over 49 bytes, the namespace `3f5a1d62-8c04-4b91-a7e3-6d2f0b48c915`, the feedback ID and the record's ID (each 16
bytes in RFC 4122 order), then a tag byte (**1** for the evidence ID, **2** for the event ID), its first 16 bytes stamped
as a UUIDv8 (version nibble 8, variant `10`). They are not 1.16's tags 4 and 5, which are finalization's reuse evidence
(`ReuseEvidenceIdFor`, `ReuseEventIdFor`), and which 1.17's finalization step uses unchanged.

**A mixed fleet converges.** The ledger's primary key is the arbiter: the same submission from a .NET, a Python and a Go
agent is one row (the others `AlreadyRecorded`), each derived evidence ID one confidence row, and every agent then reads
the same history and confidence. A cross-language PostgreSQL test proves it.

## What a port runs, and what it does not yet

A contract 1.17 port runs the feedback service over contract 1.16's lifecycle service, with its settings:

- **Independence is not verified** (story 27.7): as .NET under `IndependenceVerification.TrustHostSuppliedIdentifiers`,
  the service checks no attribution before the ledger is written (no run lookup, no round check, no assessment token,
  no exposure check). There is no provenance signer, so no run is "tampered". An attribution therefore degrades only
  through validation, and its evidence is admitted `HostTrusted` by the lifecycle.
- **A port's default stays `HostTrustedEvidence = RecordedOnly`.** Attributed evidence is then recorded on an event that
  moves only the revision (the exposure reports it: Applied, not counted, "Host-trusted evidence is recorded without
  being counted (HostTrustedEvidenceEffect.RecordedOnly)."). A host that accepts unverified attribution opts into
  `Counted`. Every fixture names its mode.
- **No key store.** A port writes the rationale in plaintext (`rationale_sealed` NULL), as the .NET store does without
  encryption. A .NET store in crypto-shredding mode keeps the placeholder `(sealed)` in `reuse_feedback.rationale`, so a
  port resubmitting such a submission compares its rationale with the placeholder and is told `Conflict`: nothing is
  written either way.
- **The ledgers follow their .NET counterparts.** The in-memory ledger, like .NET's, is independent of the record store
  and checks nothing about the exposed records; the PostgreSQL ledger refuses an exposure to a record erased in the
  submission's scope (`sql/reuse-feedback.sql`, step 2).
- **Cancellation** (.NET's token) is the port's own: a cancellation once the ledger is written is reported per record
  (Failed, retryable, "Cancelled before this record's evidence was submitted; resubmit the same feedback ID.") where the
  port's language can cancel at all. No fixture depends on it.
- **Finalization's step is a call of its own.** A port's finalization is a pure function, so the reuse-evidence step runs
  once the finalized record is stored, and again on a run already finalized (with the stored record), exactly as .NET's
  `FinalizeAsync` runs it after the commit and on `AlreadyFinalized`. Its time budget (`ReuseEvidenceTimeout`) is the
  caller's deadline where the language has one; the `finalize` span's `reuse_evidence.submitted` attribute is not
  written, because the step is not inside that span.

Everything else is .NET's: the validation, its every error path and message in .NET's order, which errors are fatal and
which degrade the attribution, the ledger's semantics, the order of the steps, every disposition and reason, and what
counts as a failure.

## The service's semantics

The fixtures pin them; in short (the [Reuse feedback](../docs/guide/reuse-feedback.md) guide has the reasons):

1. **Validation.** Fatal errors (no feedback ID or run, an undefined outcome or claim, an unset instant, a blank trial
   label, a missing or malformed measure, no exposures, more than 64, an empty or repeated one, both attributions,
   which ends validation, an attribution naming a record the run was not exposed to, a comparative result about another
   run) are `Invalid` with every fatal error, and nothing is written. Degrading errors (an attribution that fails its
   evidence requirements) drop the attribution: the submission is recorded with benefit `Unknown`, source `None`, and
   the reason "The attribution did not meet its evidence requirements, so the exposure was recorded with benefit
   Unknown and no confidence submission: " followed by each error as `{path} {message}`, joined with `"; "`.
2. **Authorization**, before any store is touched: `Denied`.
3. **The ledger.** The submission goes to the store with Core's decision made: the exposures in experience-ID order, an
   attributed one carrying `EvidenceIdFor(feedback, record)`. `AlreadyRecorded` and `Recorded` go on; `Conflict`
   reports the stored submission's exposures (when the store hands them back) as `Refused`, not retryable;
   `Denied` and `Invalid` end the call. A ledger failure propagates and nothing is recorded.
4. **Evidence, one record at a time**, in experience-ID order. An unattributed exposure is `ExposureOnly`. An attributed
   one is applied through the lifecycle (event `EventIdFor`, evidence `EvidenceIdFor`, kind Contradicting for harm and
   Supporting otherwise, source Human or Machine, the run, the comparative round (never a human's), the reason "Reuse
   feedback {id} attributed {harm|improvement} to this record.", the producer `experience-reuse-feedback/human-assessment`
   or the evaluator, `OccurredAt` the feedback's `ObservedAt`, the rationale as the detail): `Applied` is
   `EvidenceApplied`; `Ineligible` is `Ineligible`; `NotFound` and `Denied` are `Unresolved`; `StaleRevision` and
   `StatusMismatch` are `Failed`, retryable; `Deleted` and everything else are `Refused`. A store failure, or an engine
   failure (an engine that throws, or a score that is not one), is that record's `Failed`, retryable, with the
   failure's message; the other records are still submitted. Anything else propagates.

The ledger: validation, then authorization, then the write. An identical resubmission (exposures compared in record
order, instants truncated to whole microseconds) is `AlreadyRecorded` with the stored submission; one differing in any
stored field is `Conflict`, with the stored submission only when the caller's authorization permits its scope.

## What 1.17 adds

- **sql:** [`reuse-feedback.sql`](sql/reuse-feedback.sql). The application role needs no new privileges: the 1.0
  privileges file already grants `SELECT` and `INSERT` on both ledger tables, and the `FOR KEY SHARE` lock on an exposed
  record is covered by its `UPDATE` grant. No schema object is added.
- **reuse-feedback**, **finalize-reuse-evidence**: two new fixture families, generated through .NET's services over the
  .NET in-memory stores.
- **reuse-feedback-sql**: a new fixture family (needs PostgreSQL), generated through the .NET PostgreSQL ledger.

## The in-memory fixtures

```
fixtures/reuse-feedback/<case>/            input.json -> expected.json
fixtures/finalize-reuse-evidence/<case>/   input.json -> expected.json
```

### reuse-feedback

`input.json` holds a `description`; `service` (as 1.16's: `engine` and `hostTrustedEvidence`); `records`, stored records
in 1.0's form, which an in-memory record store holds at the start; and `steps`, run in order, each with the `clock` the
record store reads during it and one operation:

- **`submit`**: `authorization` (1.16's form) and `feedback`, for the service: `feedbackId`, `runId`, `scope`,
  `exposedExperienceIds` (`null` for none), `runOutcome`, `measure` (`kind` and `value`, a number or `"NaN"`,
  `"Infinity"`, `"-Infinity"`; `null` for none), `observedAt`, `claimedBenefit`, `humanAssessment` (`assessmentId`,
  `benefit`, `attributedExperienceIds`, `rationale`, `assessedAt`, `verificationRoundId`, `assessmentToken`) or `null`,
  `comparativeEvaluation` (`evaluatorId`, `runId`, `verificationRoundId`, `benefit`, `attributedExperienceIds`,
  `evidence`, 1.0's evidence objects, a `null` member allowed, `summary`, `evaluatedAt`) or `null`, and `trialLabel`.
  An enum value no member names is written `"Sideways"`. Optionally `faults`, for this step only: `ledger`, a message the
  ledger write throws as a store failure (nothing written), and `commit`, by record ID, `{"throws": message}` (the
  record's lifecycle commit throws a store failure) or `{"answers": "StaleRevision"}` (it answers that, with the revision
  `expectedRevision + 1` and no status, writing nothing).
- **`record`**: `authorization` and `feedback`, a submission as the ledger stores it, for the ledger alone:
  `feedbackId`, `runId`, `scope`, `runOutcome`, `claimedBenefit`, `benefit`, `attributionSource`, `reviewerIdentity`,
  `evaluatorId`, `verificationRoundId`, `assessmentId`, `rationale`, `evidenceIds`, `attributedAt`, `measure`,
  `trialLabel`, `observedAt` and `exposures` (`experienceId`, `attributed`, `evidenceId`; a `null` member allowed).
- **`apply`**: 1.16's `apply` step, through the lifecycle service.
- **`derive`**: `feedbackId` and `experienceId`: `EvidenceIdFor` and `EventIdFor`.

`expected.json` holds `results`, one per step; `ledger`, every submission the ledger holds afterwards as it stores it
(the `record` form; exposures in record order, instants truncated to whole microseconds), by feedback ID; and
`records` and `histories`, as 1.16's. A result is:

- **submit:** `thrown` (`null`, or `{"exception": "ExperienceStoreException", "message"}` when the ledger write threw),
  and otherwise `outcome`, `feedbackId`, `benefit`, `attributionSource`, `exposures` (`experienceId`, `disposition`,
  `evidenceId`, `counted`, `reuseConfidence`, `status`, `retryable`, `reason`), `errors`, `reason` and `isRetryable`.
- **record:** `outcome`, `feedback` (the submission handed back, or `null`) and `errors`.
- **apply:** 1.16's. **derive:** `evidenceId` and `eventId`.

### finalize-reuse-evidence

`input.json` holds a `description`; `service` (as above); `finalization` (`reuseEvidence`, `Off` or `SameTask`, and
`contradictOnFailure`); `records`, held by the in-memory store at the start; `finalize`, a run in the `finalize`
family's `input.json` form (captured as that family captures it, and finalized with the deterministic reflector under
1.0's fixed authorization); and `finalizations`, how many times to finalize it. The store's clock reads
`finalize.verification.finalizedAt` throughout.

`expected.json` holds `results`, one per finalization: `outcome` (`Validated`, `Quarantined`, then `AlreadyFinalized`),
`experienceId`, `reuseEvidence` (one entry per distinct record the run's provenance names, in its order: `experienceId`,
`kind`, `outcome` (the lifecycle's, or `null`), `reason`, `counted`, `skipped`, `replay`, `refusal`, `exceptionType`)
and `reuseEvidenceTruncated`; and `records` and `histories` of the seeded records, as above. `exceptionType` is the
.NET type's full name (`System.InvalidOperationException` for a score that is not one) and the `reason` names it ("Applying
reuse evidence threw {type}; ..."): a port reports its own error's type name in both, and its harness compares them with
the .NET name put back.

Numbers compare within 1e-12; everything else exactly, every message and reason byte for byte.

## The PostgreSQL fixtures

```
fixtures/reuse-feedback-sql/<case>/   seed.json + steps.json -> expected.json (needs PostgreSQL)
```

Against a database the .NET migrator applied, with the 1.0 privileges file applied (row-level security on):

1. Insert `seed.json`'s `records` as a search seed is inserted.
2. Run `steps.json`'s `steps` in order, each with the ledger's clock set to its `clock`, through the port's PostgreSQL
   ledger (`sql/reuse-feedback.sql`):
   - `record`: as above -> `outcome`, `feedback`, `errors`. The submission handed back is, on `Recorded`, the one given
     with its exposures in record order (its instants as given); on `AlreadyRecorded` and `Conflict`, the stored one as
     the statements read it back;
   - `erase`: as the schema's owner, 1.4's erasure call; its result is `null`.
3. Read, as the schema's owner, in the time zone UTC and each column as `::text`: `reuse_feedback` (`feedback_id`, the
   columns `insert-submission` names, `recorded_at`) ordered by `feedback_id`, and `reuse_feedback_exposures`
   (`feedback_id`, `experience_id`, `ordinal`, `attributed`, `evidence_id`, `rationale_sealed`) ordered by `feedback_id`,
   `ordinal`. Compare them, and the results, with `expected.json`.
