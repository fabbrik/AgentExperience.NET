# Reuse feedback: recording what reuse was worth

**In short.** After a run that was given past lessons, you record what happened with
`ExperienceReuseFeedbackService.RecordAsync`: which records the run saw, how it came out, and what you measured. That
is *exposure*, and on its own it moves nothing — a run that saw a lesson and succeeded does not prove the lesson
helped. Only two kinds of *attribution* move a score: a human assessment carrying a token your review flow minted,
or a comparative evaluation result carrying its own evidence. It is deliberately hard to make this say yes. Retrying
the same submission is safe and converges.

Packages: `AgentExperience.Core` (the service); `AgentExperience.Storage.Postgres` (the ledger, registered with
`AddAgentExperiencePostgresReuseFeedbackStore`).

## Recording feedback

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

With the MAF adapter, `InjectedExperienceIds` comes from the injection result, and `RunId` is the one
`UseExperienceCapture` wrote into session state (see [Injection](injection.md#feeding-the-result-back)).

**Exposure is not attribution.** That call records exactly what happened: a run saw these records and came out this
way. It does not record that the records *helped*, because nothing established that. Benefit is `Unknown`, no
confidence evidence is submitted, and no record's score, counters, or status changes. Almost every submission a real
host makes will end here, and it should.

**A bare claim is never attribution.** `ClaimedBenefit` is stored verbatim, so you can later compare what hosts
believed against what evidence established, and it is never acted on. Exactly two shapes move a score:

| Attribution | What it must carry | What it produces |
| --- | --- | --- |
| `HumanReuseAssessment` | improvement or harm, the exposed records it is about, an auditable rationale, the `AssessmentId` and `AssessmentToken` of a **library-minted assessment token** for this scope, run, reviewer, direction and (at least) these records, optionally the verification round it was made against — and **no reviewer field**, because the reviewer is your `AuthorizationContext.PrincipalId` | `Human` evidence, keyed `human:{principal}:{run}` |
| `ComparativeEvaluationResult` | the same records and rationale, plus the run it evaluated (which must be *this* run, finalized in this scope), the verification round that run was finalized with, and the evidence it reached its conclusion from — each piece of which must name that same round | `Machine` evidence, keyed `machine:{run}:{round}` |

**This library does not implement a comparative evaluator**; it defines the contract and verifies the result it is
given. Evidence from another round is not evidence about this comparison, and is refused.

> **Read this before you wire either one up — what the library can and cannot check.**
> Under the default verification, an attribution is accepted only when its `RunId` is a run the library knows in the
> feedback's scope, a comparative result's round is the one that run was finalized with, and a human assessment
> presents an assessment token the library minted — so an agent cannot mint a run, a round or an assessment, and a
> forged one is dropped with the exposure still recorded. The run must also have been *given* every attributed
> record — its provenance must show the library delivered the record into it (the MAF context provider records this
> on the captured run) at or before the record's current revision — so an attribution about a real run that never
> saw the lesson is dropped too. Note the difference: the submission's own `ExposedExperienceIds` is still your
> statement, recorded as given; only an *attributed* record must appear in the run's recorded exposure, because only
> an attribution produces a key. What nothing can check is that the lesson mattered to the run, or that a human made
> the assessment and meant it: the reviewer is your `AuthorizationContext.PrincipalId`, one reviewer's opinion about
> one run counts once, and the token proves your review flow issued it — not what the person thought. The human
> shape is still the weakest boundary here. Take `RunId` from your own run bookkeeping and keep the issuer in your
> review flow, never in code an agent drives. A host that opted out (`TrustHostSuppliedIdentifiers`) is back to
> trusting all three identifiers as given, and no exposure is checked; what it admits is stored as `HostTrusted`.
> See [Confidence and independence](confidence.md#the-keys-inputs-are-verified).

**A failed attribution costs the attribution, not the exposure.** An attribution that does not meet its evidence
requirements — no `AssessmentId`, a missing, invalid, expired or non-covering assessment token, an unknown run, a
round its run was not finalized with, no evidence behind a comparison, a blank rationale, a benefit of `Unknown` — is
dropped: the submission is still recorded, with benefit `Unknown`, no confidence submission, and a `Reason` naming
what was refused. Only a structurally incoherent submission is `Invalid` with nothing written: no feedback ID, no
records, an attribution naming a record the run never saw, or a comparative result about a *different* run. Losing
a true exposure to punish a bad attribution would throw away the one thing that was never in doubt.

**Improvement supports, harm contradicts.** An accepted attribution submits one piece of evidence per attributed
record, through `ApplyEvidenceAsync` and nothing else — so independence keying, duplicate suppression, the revision
guard, the eligibility gate, and the audit trail all apply exactly as described in [Confidence](confidence.md).
Attributed harm therefore contests each record in the same transaction that records the evidence. **Nothing is ever
deleted**: the record stays, and its own history carries the reason.

## Outcomes

| Situation | What happens |
| --- | --- |
| Records injected, no attribution | Exposure stored, benefit `Unknown`, nothing moves |
| Caller claims improvement with no evidence | Same — the claim is recorded, not acted on |
| Authorized human assessment | Supporting evidence per attributed record, counted once each |
| Comparative evaluator result | Supporting evidence per attributed record, as machine evidence |
| Attributed harm | Contradicting evidence per record; each `Contested`; all still present |
| Attribution fails its evidence requirements | Exposure recorded, benefit `Unknown`, `Reason` says what was refused |
| Attribution names a run the library does not know, a round its run was not finalized with, or presents a forged or expired assessment token | Same: exposure recorded, benefit `Unknown`, `Reason` names it |
| Attribution names a real run that was never given an attributed record (no recorded exposure, or only at a later revision), or a run known only through a hand-written record | Same: exposure recorded, benefit `Unknown`, `Reason` names it — the whole attribution is dropped, including records the run was given |
| A second submission presents an assessment token already spent on a record | That record `Refused`, not retryable — the token lands once per record |
| Attribution names a record the run never saw, or a comparative result names another run | `Invalid` — nothing written |
| Same feedback ID, identical content — in any record order | `AlreadyRecorded` — nothing written twice, nothing counted twice |
| Same feedback ID, different content | `Conflict` — nothing written; the stored submission's records are reported back when you are authorized for its scope |
| One record's submission fails | The rest still apply; that one is `Failed` and `Retryable` |
| Cancelled part-way through | What was decided is returned; the rest are `Failed` and `Retryable` — never an exception |
| The same run already produced evidence for a record | `EvidenceApplied` with `Counted: false` — the existing independence rule |
| An exposed record is `Candidate`, `Quarantined`, `Stale`, `Superseded`, or `Revoked` | Exposure recorded, `Ineligible`, nothing written for it |
| An exposed ID does not exist in that scope, or is readable only through a sharing grant | Exposure recorded, `Unresolved` with the reason, nothing written for it |
| An exposed ID is a tombstone in the submission's own scope | `Invalid`, naming the exposure by position |
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

## How the PostgreSQL ledger stores it

`PostgresExperienceReuseFeedbackStore` answers one question — *what did a run that saw these records actually come
to?* — and writes it down. It is a separate port from the record store on purpose: recording feedback is opt-in, and
a host that never does it needs neither table.

It runs in the same order as every other operation: validate the submission, check its scope against the
host-established `AuthorizationContext`, and only then open a connection. A scope outside the context is `Denied`
before any connection opens.

- **The submission and its exposures commit together.** One transaction on one connection inserts the
  `reuse_feedback` row and every `reuse_feedback_exposures` row, so a run's feedback is never half recorded. Core
  writes this ledger **before** submitting any confidence evidence.
- **This store decides nothing about benefit.** It writes the attribution decision Core made. It never promotes
  `Unknown`, never derives an evidence ID, and never reads `claimed_benefit` as attribution. The database enforces
  the same rule from its own side, so a writer bypassing this package is refused too.
- **A human assessment is the weakest trust boundary here.** Nothing in this schema or in Core can check that a
  human made one. `reviewer_identity` is the host's `AuthorizationContext.PrincipalId` rather than anything on the
  submission. `assessment_id` is the ID of an assessment token Core verified (minted under the host's key for this
  scope, run, reviewer, direction and records) before the attribution was recorded, and `run_id` was checked to be a
  run the library knows in the scope, so agent output cannot mint either; the evidence ledger then spends the token
  once per record. A host that opted out of verification is back to what `0008`'s header describes: the caller
  supplies both, and one that lets agent output populate them hands the agent a fresh independence key on every
  call.
- **Idempotency is the feedback ID.** The insert is `ON CONFLICT (feedback_id) DO NOTHING`, so the primary key is
  the arbiter and two hosts submitting at once cannot both decide they were first. A collision is then read back
  inside the same transaction and compared field by field — every stored column, and the exposures in order.
  Identical is `AlreadyRecorded` with nothing written; anything else is `Conflict`, again with nothing written.
  `recorded_at` is excluded from the comparison, because it is this store's own clock reading and comparing it
  would make every replay a conflict. The stored timestamps are compared against the truncated values that were
  actually written, so a sub-microsecond original does not report itself as a conflict.
- **The exposures compare as a set, not as typing order.** Core orders the records by experience ID before deriving
  ordinals, so the positional comparison here is a comparison of record *sets*. Without that, a host that crashed
  mid-submission and retried with its records in a different order would get a permanent `Conflict` — and, since
  retrying is the only way to finish an interrupted fan-out, would be locked out of ever completing it.
- **A conflict reveals nothing it should not.** The lookup is by primary key with no scope predicate — it has to
  be, or the same ID could be recorded once per scope and a retry would not know which one it was replaying. The
  stored submission therefore comes back only when the caller's `AuthorizationContext` permits *its* scope, so a
  host whose retry was refused can still see which records the stored submission named, and a guessed ID from
  another scope still reveals nothing.
- **This store never reads an Experience Record.** There is no join to `experience_records` and no foreign key to
  it. Whether an exposed ID resolves to anything is decided afterwards, by the confidence path, against the record
  itself.

The table layout and its constraints are in [PostgreSQL schema](postgres-schema.md#0008-reuse-feedback).
