# Lifecycle: moving a record through its statuses

**In short.** After finalization, a record's status changes only through `ExperienceLifecycleService`, and only along
a fixed table of transitions: a lesson can be reinforced, contested, marked stale, superseded by a better one, or
revoked. Only `Validated` and `Reinforced` records are reused, so any other move takes a record out of reuse
immediately, without deleting anything. Every change is an append-only event committed in the same transaction as
the record's new status, checked against the record's revision so two writers cannot both win, and idempotent by
event ID so retries are safe. The database refuses edits to the history.

Packages: `AgentExperience.Core` decides which transitions are legal; `AgentExperience.Storage.Postgres` commits them.

## The transition table

| From | To | What it means |
| --- | --- | --- |
| `Candidate` | `Validated`, `Quarantined` | Finalization's own two outcomes |
| `Validated` | `Reinforced` | Reuse was observed to succeed again — **once**; `Reinforced → Reinforced` is refused. Nothing moves a record here automatically: the host commits this transition with `CommitAsync` when its own policy decides |
| `Validated`, `Reinforced` | `Contested` | Later evidence contradicts the lesson. Exits only to `Revoked` |
| `Validated`, `Reinforced` | `Stale` | The lesson is no longer current. Exits only to `Revoked` |
| `Validated`, `Reinforced` | `Superseded` | A better record replaces it — and names which. Exits only to `Revoked` |
| anything except `Revoked` | `Revoked` | Withdrawn by an authorized action. Terminal |

Everything else is `TransitionNotAllowed`, refused by Core before the store is called. That includes an event whose
prior and current status are the same: it would consume a revision and sit in the audit trail claiming a transition
that did not happen. It also includes a *first* event — one with no prior status — that records anything but
`Candidate`: a null prior status is how a record's creation is logged, never a way to move a record without saying
what it moved from.

Three consequences are worth stating outright:

- **Quarantine is a capture-time decision only.** Early development versions accepted `Validated → Quarantined` (and
  `Contested`/`Stale`/`Superseded`/`Reinforced → Quarantined`). Those are refused, at runtime, with no compile-time
  signal. To take a live record out of reuse, `Revoke` it, or contest it.
- **A record can be reinforced once.** `Reinforced → Reinforced` records no transition and is refused, so this
  table cannot express repeated reinforcement. Evidence can:
  [`ApplyEvidenceAsync`](confidence.md) moves the counters without moving the status.
- **`Contested` and `Stale` are one-way.** Nothing resolves a contest or refreshes a stale record back into
  eligibility in this version; both exit only to `Revoked`.

Only `Validated` and `Reinforced` are **eligible**. A record in any other status is never retrieved, never injected,
and never indexed — so contesting, staling, superseding, or revoking a record takes it out of reuse immediately,
through both search channels, without deleting anything.

## Committing a transition

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

## How the PostgreSQL store commits it

`CommitLifecycleEventAsync` is the only way a stored record's status changes. It appends the `LifecycleEvent` to
`lifecycle_events` and updates the record's `status`, `revision`, and `updated_at` **in one transaction on one
connection**: both writes commit together, or neither does. A failure between them leaves no event and no
projection change.

The store persists the decision exactly as given. It never derives a status, a reuse confidence, or a counter — a
confidence update writes the numbers Core computed and nothing else (see [Confidence](confidence.md)) — and it never
invents a transition the command did not carry: deciding which transitions are legal belongs to Core's
`ExperienceLifecycleService`. Authorization is checked against the request scope before the transaction opens,
exactly as for the store's other operations, and the scope predicate is applied in SQL.

- **The revision rule.** `ExpectedRevision` must equal the record's current `Revision`. A successful commit sets
  the revision to `ExpectedRevision + 1` and reports it as `result.Revision`. Any other value is `StaleRevision`,
  writes nothing, and reports the record's *current* revision so you can re-decide against it. Two commits racing
  from the same revision therefore end with exactly one applied event and one revision increment.
- **Idempotency by `EventId`.** Replaying an event whose stored fields are identical — including its scope and its
  microsecond-truncated `OccurredAt` — returns the original outcome (`Committed`, with the revision that commit
  produced) and writes nothing. A stored `EventId` with *any* differing field is `Conflict`, whichever scope owns
  it, and writes nothing. So `EventId` and `OccurredAt` must be stable across retries; regenerating either turns a
  retry into a second transition.
- **The prior-status guard.** The event's `PriorStatus` must equal the record's stored `Status`, matched in the same
  statement as the revision. A null `PriorStatus` — a record's first event — does *not* skip the match: it falls
  back to `CurrentStatus`, so a first event may only record the status the record is already in. That keeps Core's
  transition table enforced against real state rather than against what the caller asserted, and keeps a stored
  event from recording a prior status the record never had. A mismatch is `StatusMismatch`, writes nothing, and
  reports the record's stored status as `result.CurrentStatus` so you can re-decide against it.
- **Missing or foreign records.** A record that does not exist in the request scope is `NotFound`, indistinguishable
  from a missing one, and nothing is written.
- **A lost acknowledgement.** A commit that was cancelled or timed out after PostgreSQL committed it is recovered by
  retrying the *identical* event: the replay path reports the original `Committed` and the revision that commit
  produced, without applying it twice. This is why `EventId` and `OccurredAt` must be stable across retries — unlike
  a create, where a lost acknowledgement surfaces as `Conflict` and has to be resolved with `GetAsync`.
- **Supersession's replacement.** An event that moves a record to `Superseded` carries `ReplacementExperienceId`,
  stored in its own column on the event row. The database states the rule as a `CHECK`, so a superseding event with
  no replacement, a replacement on any other transition, and a row naming itself as its own replacement are all
  unstorable however the write arrives. Which replacements are *acceptable* stays Core's decision; the store answers
  the parts only a scoped query can (see below) and persists what Core decided.
- **The supersession gate is inside the commit.** An event carrying `ReplacementExperienceId` is checked *within the
  commit transaction*, after both record rows are locked `FOR UPDATE` in a deterministic order: the replacement must
  exist in exactly this scope, be eligible for reuse, and not already sit on a chain of `replacement_experience_id`
  links leading back to the record. Otherwise the commit is `ReplacementNotAllowed`, carrying the replacement's
  stored status, and nothing is written. Checking it anywhere else would not hold: two supersessions naming each
  other ("A by B" and "B by A") each pass a check taken outside a transaction and would both commit the cycle the
  contract refuses. The gate also runs *after* replay detection, so retrying a committed supersession still reports
  its original outcome even once the replacement has itself moved on — which is the retry a lost acknowledgement
  calls for.
- **`CheckSupersessionAsync`** asks the same question read-only, on its own connection, so a caller can find out
  before it tries. Its answer is a prediction, not a guarantee; the commit decides. The chain walk is a recursive
  CTE over `lifecycle_events`, scope-qualified like everything else and written with `UNION` rather than `UNION ALL`,
  so it terminates even over a loop some earlier writer managed to store. A replacement outside the request scope is
  `ReplacementNotFound`, identical to one that does not exist. Nothing is written, whatever it answers.

## Reading the trail

`IExperienceRecordStore.GetHistoryAsync` returns one bounded page of a record's events, oldest first, plus the
record's current revision — from a single snapshot, so the two can never disagree. Each stored event carries the
store's own `RecordedAt` (the database's clock, not the caller's) and the `AppliedRevision` it produced. Page with the
keyset cursor:

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

The page is bounded by `Limit` (1–500, default 100). A record whose cursor has walked past its last event still
reports `Found` with its revision and an empty page, so "nothing left to show" stays distinguishable from `NotFound`.
(The cursor is applied in the outer join's `ON` clause rather than in the `WHERE`, which is what keeps an exhausted
history `Found`.) `GetFirstHistoryPageAsync(authorization, scope, id, ct)` is the one-line convenience for the common
case, and is named for what it does: it returns the first page only, and a record with a longer trail has more.

## Append-only, enforced by the database

Migration `0006` installs triggers that reject every way a stored event could stop being what it was:

| Attempt | What stops it |
| --- | --- |
| `UPDATE` or `DELETE` on `lifecycle_events` / `experience_grant_events` | row-level `BEFORE UPDATE OR DELETE` triggers |
| `TRUNCATE` on either log, or on `experience_grants` | statement-level `BEFORE TRUNCATE` triggers — `TRUNCATE` does not fire row triggers at all, so a row-level guard alone would let it erase the whole log with no error |
| Clearing a grant's `revoked_at`, rewording its `revocation_reason`, extending its `expires_at` | `BEFORE UPDATE` trigger on `experience_grants` |
| Deleting a revoked grant and inserting it again unrevoked | `BEFORE DELETE` trigger refusing any grant that has audit events |
| Re-pointing a live grant at another record or recipient | the same `BEFORE UPDATE` trigger, which pins the grant's identity and audit columns |
| Winding a record's `revision` back, or moving its `status` without the revision its event produced | `BEFORE UPDATE` trigger on `experience_records` — an immutable log beside a freely rewritable projection proves nothing |

Later migrations extend the same guards to the other ledgers (`confidence_evidence`, the reuse-feedback tables and
the grant access log). A tamperer gets SQLSTATE `42501`, which the store surfaces as an `ExperienceStoreException`.
No supported code path reaches the guards, so hitting one means something bypassed the store. Be precise about what
that buys:

- It binds ordinary writes **from any role, superusers included**, as long as the triggers are enabled. They are
  created `ENABLE ALWAYS`, so they also fire under `session_replication_role = 'replica'` — the mode logical
  replication appliers and several restore and ETL tools run in, and the mode in which an ordinary trigger is
  skipped silently.
- It does **not** bind anyone who can `ALTER TABLE` these tables: a superuser, or the tables' owner. An owner can
  `DISABLE TRIGGER`, `DROP TRIGGER`, or drop a constraint and then write freely. That is why the supported
  deployment has two roles: an owner that runs the migrators and a separate application role that owns nothing,
  holds no `UPDATE`, `DELETE` or `TRUNCATE` on any log, and so is refused by the privilege system before a trigger is
  even asked (see [Deploying with two roles](deployment.md#deploying-with-two-roles)). The owner and superusers
  remain unbound; that is inherent in PostgreSQL.
- It says nothing about backups, about a restore that recreates the tables without `0006`, or about filesystem
  access to the data directory.

So, with the two roles, it is a guard against a bug, a careless script, a compromised application path — including
one holding the application role's own credentials — or a replication apply that would otherwise rewrite history.
It is not a guard against an administrator holding the owner's or a superuser's credentials who has decided to
tamper. A deployment that needs tamper-evidence against those should ship the log off-box.

**There is exactly one exception: erasure.** The logs carry free-text `reason` and `producer` a host may have filled
with personal data, so migration `0010` gives the guards a transaction-scoped marker that one `SECURITY DEFINER`
purge function sets. Erasing a record can then remove the rows that named it without any trigger ever being
disabled. `UPDATE` and `TRUNCATE` stay refused unconditionally, in every session, including the purging one. That
replaces `0006`'s manual `ALTER TABLE … DISABLE TRIGGER` runbook — which was table-wide, visible to every other
connection in the pool, and left the guard off if anything failed in between. See
[Deletion and retention](deletion-and-retention.md).

**Upgrading an existing database.** `0006` adds every `CHECK` as `NOT VALID`, so it does not scan existing rows and
cannot abort on a pre-`0006` `Superseded` event that has no replacement — one the public port accepted, because the
store never applied Core's table. New and updated rows are checked from that moment on. The script's header carries
the reconciliation query and the `VALIDATE CONSTRAINT` statements to run once it comes back empty.

## For implementers of the ports

If you implement `IExperienceRecordStore` or `IExperienceEmbeddingIndex` yourself, several obligations cannot be
expressed in the type system. Read the port's XML documentation, and these notes:

- **Batch methods are optional to override.** `IExperienceRecordStore.GetManyAsync` and
  `IExperienceEmbeddingGenerator.GenerateBatchAsync` are default interface methods that loop over the single-item
  call, so an out-of-tree implementation keeps compiling and behaving exactly as before. Override them to save the
  round trips; a store's override must answer each ID exactly as its own `GetAsync` would, access rows included.
- **Changes made before `0.1.0-preview.1`** that fail at compile time: `IExperienceRecordStore` gained
  `CheckSupersessionAsync`; `IExperienceRecordStore.GetHistoryAsync` takes an `ExperienceRecordHistoryQuery` and
  returns `StoredLifecycleEvent`s rather than bare `LifecycleEvent`s (`GetFirstHistoryPageAsync` is the convenience
  for the old four-argument shape); `IExperienceEmbeddingIndex` gained `RemoveAsync`; and `ExperienceStoreOutcome`
  gained `ReplacementNotAllowed`, which a commit can return.
- **Confidence payloads do not fail at compile time.** `LifecycleEvent` has an optional `Confidence`,
  `StoredLifecycleEvent` an optional `Actor`, and `ExperienceLifecycleCommitResult` an optional `AppliedConfidence`.
  An out-of-tree store still compiles and still commits — it will simply drop a confidence payload on the floor while
  reporting `Committed`, which is a silently wrong answer rather than a failed one. A store that means to support
  [`ApplyEvidenceAsync`](confidence.md) has to persist the payload, enforce the independence key, and report what it
  stored. It must also persist `ExperienceRecord.ClosedRoundId`, `Provenance.ExposedTo`, `ExperienceRecord.Origin`
  and `ConfidenceUpdate.Admission`, and spend `ConfidenceUpdate.AssessmentId` once per record.
- **A capture service must record exposure.** `IExperienceCaptureService.RecordExposure` is a default interface
  method that records nothing and returns `NotSupported`, so an existing implementation compiles, but its runs are
  exposed to nothing and confidence evidence about them is refused as `NotExposed` (see
  [Confidence](confidence.md#the-keys-inputs-are-verified)).

Every breaking change between previews is listed in the [changelog](../../CHANGELOG.md).
