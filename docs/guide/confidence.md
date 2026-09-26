# Confidence and independence

**In short.** Every record carries a reuse confidence: a score that starts at 2/3 when the record is finalized and
moves as evidence about reuse arrives. Supporting evidence raises it; contradicting evidence lowers it and contests
the record, which takes it out of reuse. The same observation can only count once: each piece of evidence has an
*independence key* (one per run and verification round, or one per reviewer and run), and the database enforces it.
By default the library also checks that the key's inputs are real: the run must be one it knows, the round must be
the one that run was finalized with, a human assessment must carry a token your review flow minted, and the run must
actually have been given the record. The score is a heuristic for ranking, not a probability.

Packages: `AgentExperience.Core` computes and verifies; `AgentExperience.Storage.Postgres` stores and enforces the
key. Read the KL-11 boundary in [Known limits and documented boundaries](../known-limits.md#documented-boundaries)
before relying on independence.

## Applying evidence

`ExperienceLifecycleService.ApplyEvidenceAsync` is how the number moves after finalization: submit what happened when
the lesson was reused, and the evidence, the counters, the score, any status change, and the audit entry are
committed in one transaction.

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
        RunId: runId,                         // the run the *reuse* happened in -- finalized, or captured, here
        VerificationRoundId: roundId,         // machine only: the round that run was finalized with
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

**It never changes eligibility by itself.** Confidence is independent of the completion score and of status; a number
cannot make an ineligible record eligible. What takes a record out of reuse is the *status*: a contradiction moves a
`Validated` or `Reinforced` record to `Contested` in the same transaction, and a record already `Contested` stays
there while its counters keep moving. Supporting evidence never changes a status by itself — which is how a record
keeps being reinforced through its counters even though `Validated → Reinforced` happens only once (see
[Lifecycle](lifecycle.md#the-transition-table)). Retrieval does apply a confidence floor
(`RetrievalPolicy.MinimumConfidence`, 0.5 by default), so a low score stops a record being *returned*.

## Independence is keyed, and the database owns the key

Machine evidence counts once per `(record, run, verification round)`; human evidence once per `(record, reviewer,
run)`. The key is a *generated* column in `confidence_evidence` with a partial unique index over it, so no caller
picks the key **string**: two submissions describing the same observation collide however they are phrased.

## The key's inputs are verified

A key is only worth anything if its inputs cannot be invented, so before anything is computed or written the
submission is checked, and refused with `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal` if it
fails:

- **`RunId`** must be a run the library knows **in the evidence's scope**: one finalized into a record there (the
  record finalization derives for that run and scope, read through the ordinary scoped `GetAsync` — not through a
  grant, not a tombstone), or one the capture service wired into the lifecycle service holds there (`UnknownRun`
  otherwise). Retrieval is exact-scope and a grant never confers writing, so no run in another scope could have been
  exposed to a record that accepts evidence. It is never the record's own `SourceRunId` (`OwnRun`, refused in every
  mode). A record under the derived ID counts only if finalization wrote it (`ExperienceRecordOrigin.Finalized`); one
  written by hand through `CreateAsync` vouches for nothing (`HostWrittenRun`).
- **Exposure.** The run must have been *given* the record: its provenance (`Provenance.ExposedTo`, on the finalized
  record or on the run the capture service holds) must name the record at a revision at or before the one the
  evidence is computed against, or the submission is refused as `NotExposed`. The MAF adapter records this for you —
  its context provider records, on the captured run, every record it injects, at the revision it rendered — and
  finalization copies it onto the run's record. What a reused session's history carries from an *earlier* run's turns
  is deliberately not credited to a later run: the session account lives in host session storage, unauthenticated,
  so a later run in the same session is exposed only to what it is given itself. An exposure recorded at a *later*
  revision than the record's current one cannot have happened, and is refused. A host that delivers records some
  other way calls `IExperienceCaptureService.RecordExposure(runId, exposures)` from the code that delivers them; it
  keeps one entry per record at the earliest revision, is refused once the run is completed, and stops at
  `RunExposure.MaxPerRun` (256). `StartRun` refuses a provenance that already carries exposures. The exposure check
  runs last, after the run, round and token, and it applies to machine and human evidence alike, because every
  submission is a claim that reusing the record helped or hurt the named run. The machine evidence that is about a
  record's *own* quality — its source run's evaluation, which seeds its first supporting validation — is bound by
  finalization and never comes through this path.
- **`VerificationRoundId`** (machine) must be the round that run was finalized with: finalization stamps
  `ExperienceRecord.ClosedRoundId` from the evaluation it computed. A run only the capture service holds, or one
  finalized with no closed round, has no round to vouch for (`UnknownRound`). So one run yields at most one machine
  key per record.
- **Human evidence** must carry an `AssessmentToken` from `AssessmentTokenIssuer.Issue(reviewer, scope, runId, kind,
  experienceIds)`, which your review flow calls when a person records a decision. It is an HMAC-SHA256 over the
  assessment's ID, issue time, direction and records and over the scope, run and reviewer, under
  `ExperienceIndependenceOptions.AssessmentTokenKey` (at least 32 bytes from your secret store). It is compared in
  constant time before anything it claims is read, expires after `AssessmentTokenLifetime` (a day by default), must
  cover the record, and is spent once per record by the store, in the same transaction as the evidence: another
  evidence ID presenting it is refused (`AssessmentTokenReplayed`), while resubmitting the same evidence still
  replays. A random GUID, a tampered token, or one minted for another scope, run, reviewer, direction or record is
  `AssessmentTokenInvalid` or `AssessmentTokenNotForRecord`; an expired one is `AssessmentTokenExpired`; with no key
  configured, human evidence is refused.

```csharp
services.AddSingleton(new ExperienceIndependenceOptions { AssessmentTokenKey = secrets.AssessmentTokenKey });

// In your review flow, where a person decided -- never in code an agent drives. The issuer is deliberately not
// registered in DI: anything that can resolve it can mint.
var issuer = new AssessmentTokenIssuer(independenceOptions);
var assessment = issuer.Issue(reviewerAuthorization, recordScope, runId, ConfidenceEvidenceKind.Supporting, [experienceId]);
// ...then submit Human evidence with AssessmentToken: assessment.Token, under the same reviewer's authorization.
```

`ExperienceIndependenceOptions` carries the assessment token key, the token lifetime, the clock, and the opt-out.
`ReviewerIdentity` is enforced for you — it is taken from `AuthorizationContext.PrincipalId` and the request has no
field for it, because the number of distinct human reviewers is exactly what this rule protects. Principals are
compared ordinally, like every other identity here, and one with leading or trailing whitespace is refused rather
than trimmed.

## What verification does not prove

Read this before relying on it. Verification proves a run is *real, in scope, and was given the record* — not that
the record mattered to it: every run the library delivered a lesson into is one key, whatever the lesson did there.
Exposure is what the library recorded delivering, and the library believes a host that calls `RecordExposure`
itself, or that writes a record through `CreateAsync` marked `ExperienceRecordOrigin.Finalized`: the store port
cannot tell the library's writes from the host's. The round is the one the host closed at finalization. Real runs
are easy to name: every record in the scope carries its `SourceRunId` and `ClosedRoundId`, a run's round vouches
whatever its own verification concluded (a failed or quarantined run's round vouches too), and a run the capture
service holds stays known while it is held. The token is only as secret as the key and as guarded as the code that
can call the issuer, and single use is the store's guarantee (the PostgreSQL store makes it; an
`IExperienceRecordStore` that ignores `ConfidenceUpdate.AssessmentId` does not).

Verification runs before the store's replay check, so retry a lost acknowledgement within the token's lifetime:
after it (or after its key rotated, or its run stopped being known), the retry is refused (`AssessmentTokenExpired`)
although the original is durable, and a feedback retry then degrades and conflicts with its own stored row. Keep
taking `RunId` from your own run bookkeeping (the adapter's session state), never from agent output.

**The opt-out**, `IndependenceVerification.TrustHostSuppliedIdentifiers`, is the older behaviour for a host that
cannot adopt verification yet — one that captures and retrieves in different scopes, finalizes nothing, or must
accept evidence about runs finalized before verification existed: every identifier is trusted as given (only the
own-run rule stays, and no exposure is checked), and a host that lets agent output populate them hands the agent a
fresh key per call. That is part of the KL-11 boundary.

## What the opt-out admitted is kept visible

Every update Core submits carries `ConfidenceUpdate.Admission` — `Verified` when the checks above ran, `HostTrusted`
when the host opted out — and the PostgreSQL store keeps it on the evidence ledger and on the counted event (`0018`),
append-only like the rest of the row; evidence stored before that reads back with no admission. `confidence.apply`
spans carry it as `agentexperience.confidence.admission`, and a refusal as `agentexperience.independence.refusal`, so
an operator can see the opt-out in use without reading the ledger. To read a score without it:

```csharp
var read = await lifecycle.ReadConfidenceAsync(authorization, scope, experienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, ct);
// read.Report.ReuseConfidence: the heuristic over the counters less the host-trusted evidence.
// read.Report.HostTrusted / .Verified / .Unrecorded: what each admission counted. VerifiedOnly also drops Unrecorded
// (no admission recorded: stored before 0018, or written by something other than Core) and, for a record written by
// hand, the initial counters its writer chose (read.Report.Initial, read.Report.Origin).
```

It pages the record's history (a counted update is always an event), counts what each admission moved, and recomputes
the score from the stored counters less the excluded ones; it writes nothing. The stored score — the one retrieval
ranks on and injection shows — still counts everything; the exclusion is a read, not a rewrite. It is only as good as
the store's history: an `IExperienceRecordStore` that does not persist `ConfidenceUpdate.Admission` reads everything
back as unrecorded, which `ExcludeHostTrusted` keeps.

## Outcomes

| Submission | Outcome |
| --- | --- |
| First for its independence key | `Applied`, `Counted: true` — counters and score move |
| Same run and round (or reviewer and run) under a **new** evidence ID | `Applied`, `Counted: false` — a ledger row is written and *nothing else* moves: no counters, no status, no revision, no `UpdatedAt`, and no lifecycle event |
| …and the record moved between the read and the commit | `StaleRevision`, `StatusMismatch` or `NotFound`, with nothing stored at all — a duplicate is still committed against the record it describes |
| Same evidence ID, identical content | `Applied` — the original outcome, reported again; nothing is written twice |
| Same evidence ID, different content | `Conflict` — nothing written |
| Two submissions computed from one revision | Exactly one `Applied`; the other `StaleRevision` with the revision to retry against |
| Against a `Candidate`, `Quarantined`, `Stale`, `Superseded`, or `Revoked` record | `Ineligible` — refused before anything is written |
| An unknown or own run, a round finalization did not close, or a missing, invalid, expired, other-record or spent assessment token | `Unverified`, with `Refusal` naming which — nothing written (checked after `Ineligible`) |
| A real run that was never given the record, or given it only at a later revision; or a run known only through a hand-written record | `Unverified`, `Refusal: NotExposed` or `HostWrittenRun` — nothing written, and a token it presented is not spent |
| Against an erased record | `Deleted`, with no ledger row: an erased record's ID must not go back into a table the erasure emptied |

**A record cannot be created claiming evidence it does not have.** `CreateAsync` refuses a record whose
`ReuseConfidence` is not the one its own counters explain — creation is the single moment the two arrive
independently, and after it every change goes through the guarded path above. A record created with *no* counters
may carry any confidence its host wants to seed it with; the first accepted evidence recomputes from those counters,
so a seeded number never survives contact with evidence.

**Core owns the arithmetic; the store owns independence.** Core reads the record, computes the new counters and the
new score from what it read, and submits them with *that* revision, so the arithmetic and the concurrency guard are
about the same version of the record. The store writes those numbers and derives none: what it decides is whether
the independence key was free, and whether the revision still holds. Everything else is a fact it was given.

**Why a duplicate must move nothing.** The two obvious exceptions are the harmful ones. Refreshing `UpdatedAt`
would let one observation, replayed under fresh evidence IDs, keep a record permanently recent for ranking and
permanently un-expired — retrieval reads recency and expiry off that column. Writing the status would contest a
record on the strength of an observation the independence rule had just declared already counted, leaving an event
that says nothing moved beside a ledger with zero counted contradictions.

**One ordering wart, stated rather than hidden.** Core's eligibility gate runs on the record it read, before the
store is asked anything, so it takes precedence over the store's idempotency check: resubmitting evidence that was
already accepted, *after* the record has since been revoked or quarantined, reports `Ineligible` rather than
replaying `Applied`. Nothing is lost — the original update is durable and in the history — but reconcile retries
against the history rather than reading that as "it never landed".

**History makes an update reconstructable.** Each *counted* update's event carries the prior and new score, the
prior and new counters, the evidence ID, the rule version, and the `Actor` — the principal the commit ran under,
recorded by the store from the host's authorization and never from anything the caller put in the event. Read it
through `GetHistoryAsync` like any other transition; `stored.Event.Confidence` is `null` for the events that carried
none. An *uncounted* submission has no event, by construction — the ledger row is its audit trail. Listing that
ledger is not a port operation; its retention is covered by a record's erasure, which removes every evidence row that
named it.

## How the PostgreSQL store enforces it

A `LifecycleEvent` may carry an optional `ConfidenceUpdate`. When it does, the same transaction that appends the
event and updates the projection also writes a row to `confidence_evidence` and sets the record's
`reuse_confidence`, `supporting_validations`, and `contradictions`. Every number in it was computed by Core's
`ReuseConfidenceHeuristic` from the record Core read; the store writes them and derives none. The `RuleVersion` that
produced the score travels on the row.

- **Independence is a unique index.** `confidence_evidence.independence_key` is a **generated** column:
  `'machine:' || run_id || ':' || verification_round_id` for machine evidence, `'human:' || reviewer_identity || ':'
  || run_id` for human evidence. A partial unique index on `(experience_id, independence_key) WHERE counted` admits
  the first submission for a key and no other. Generating it here means no writer picks the key *string*; the index
  itself does **not** stop a writer inventing the key's inputs, and there is no foreign key behind `run_id` or
  `verification_round_id` because nothing in this schema knows what a run or a closed round is. Core checks the
  inputs before anything reaches the store (above); a writer that bypasses Core is unchecked here. Core computes the
  same string in `ConfidenceIndependenceKey`, and an integration test pins the two against each other.
- **Exposure and origin travel in the payload.** The run's record must carry the record in its provenance's
  `exposedTo` (record IDs and revisions only, written by finalization from what the capture service recorded) at a
  revision no later than the record's current one, and it must carry `"origin": "Finalized"`. A record written
  without finalization reads back as `ExperienceRecordOrigin.HostWritten` and vouches for no run. Like
  `closedRoundId`, both are optional version-1 payload fields, written only when set, needing no column; in
  crypto-shredding mode they are sealed with the rest of the payload, and the application role cannot rewrite them
  either way, because it has no `UPDATE` on `payload` — except through `0016`'s sealing function while
  `AllowSealing` is granted, which replaces a plaintext payload with whatever sealed envelope the application
  produces (take `AllowSealing` away once the upgrade is done). The store refuses a record whose
  `Provenance.ExposedTo` names an empty ID, a negative revision, a record twice, or more than `RunExposure.MaxPerRun`
  records.
- **The admission is recorded, append-only.** `confidence_evidence.admission` and
  `lifecycle_events.confidence_admission` (from `0018`) keep `ConfidenceUpdate.Admission` — `Verified` or
  `HostTrusted`, `NULL` on rows written before `0018` — exactly as Core gave it, and read it back on a replay and in
  history. It is not part of a replay's content comparison: resubmitting evidence reports the admission the original
  was stored with. Neither ledger grants `UPDATE`, and `0007`'s triggers refuse one, so host-trusted evidence cannot
  be relabelled after the fact.
- **An assessment is spent once per record.** `confidence_evidence.assessment_id` (from `0015`) records the
  assessment token a human submission presented, and a unique index on `(experience_id, assessment_id) WHERE
  assessment_id IS NOT NULL` — not partial on `counted` — lets one assessment land one piece of evidence per record.
  Another evidence ID presenting it is refused with nothing written: `Conflict`, with an error on
  `ConfidenceUpdate.AssessmentIdPath`, which Core reports as `Unverified`/`AssessmentTokenReplayed`. A resubmission
  of the *same* evidence ID is still a replay: because PostgreSQL does not promise which unique index a statement
  that violates several reports first, the store answers an assessment violation by looking for the evidence ID and
  compares the stored row exactly as the primary-key path does. The counted human event carries the same ID in
  `lifecycle_events.confidence_assessment_id`, surfaced as `ConfidenceUpdate.AssessmentId`.
- **A duplicate is recorded, and changes nothing else.** The first insert claims the key with `counted = true`,
  under a savepoint, because losing that race is an expected outcome the commit has to survive — a unique violation
  would otherwise abort the transaction that is supposed to record the duplicate. On the violation the statement is
  undone, the record is re-read `FOR UPDATE` (so the usual `NotFound`/`StaleRevision`/`StatusMismatch` refusals
  still apply), and the same submission is written again with `counted = false`. That ledger row is *all* the call
  writes. An event is impossible as well as unwanted, since it must claim `expected_revision + 1`.
  `result.AppliedConfidence` reports what was stored and its `Counted` says which happened, while `result.Revision`
  and `result.CurrentStatus` report the record the call left untouched.
- **The counters are guarded like the rest of the projection.** Migration `0007` extends the `experience_records`
  trigger so `reuse_confidence`, `supporting_validations`, and `contradictions` move only together with the revision
  of the lifecycle event that recorded the evidence for them — and only to the values that event recorded, so
  `UPDATE … SET reuse_confidence = 1, revision = revision + 1` is refused too, with SQLSTATE `42501`. See
  [Lifecycle](lifecycle.md#append-only-enforced-by-the-database) for what such a guard does and does not bind.
  `confidence_evidence` is append-only for the same reason the event logs are: a row that could be edited or
  removed would free an independence key, and the same observation could then be counted twice.

`EvidenceId` is a second idempotency key alongside `EventId`. Resubmitting it with identical content — the same
record, kind, source, run and round or reviewer, assessment, rule version, and detail — reports the original outcome
and the revision that commit produced, and writes nothing. Resubmitting it with different content is `Conflict` with
nothing written. The counters are deliberately *not* compared: they are derived from whatever the record held when
the submission was first made, so comparing them would report a genuine replay as a conflict for agreeing with
itself. The event ID *is* compared for a stored row that produced one, so a retry under a fresh event ID is a
`Conflict` rather than a `Committed` carrying a lifecycle event that was never written. The lookup joins
`experience_records` and applies the exact scope predicate, so a guessed evidence ID from another scope reads back as
no row at all — the primary key is global, and this is the one statement that finds a row by it alone. Every number
a replay reports comes from that ledger row, so the answer describes one moment rather than a stored revision beside
a freshly read status.

Every commit also records `lifecycle_events.actor` — the host's `AuthorizationContext.PrincipalId`, taken from the
authorization context and never from anything on the event, and surfaced as `StoredLifecycleEvent.Actor`. For human
evidence the same principal is the reviewer identity, which is what makes "one reviewer, one vote per run"
enforceable at all.

Ordering inside the transaction is not incidental: the evidence goes in **before** the event, because whether its
key was free decides which numbers the event must record, and an event is append-only the moment it is written.
