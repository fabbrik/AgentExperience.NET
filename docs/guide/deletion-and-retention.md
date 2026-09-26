# Deletion and retention

**In short.** Revoking a record stops it being read; **deleting** it removes its content. `DeleteAsync` erases every
trace of one record across seven tables in one transaction and leaves a *tombstone*: the ID, scope, revision and
deletion time, and nothing else. The ID can never be reused. Nothing expires on its own — there is no default
retention and no timer — but you can sweep records older than an age you choose, on your own schedule, one scope or a
whole subtree. In the default plaintext mode, erasure reaches only this database's live rows: backups, replicas and
WAL keep the text, and so does the old row version until `VACUUM`. To make erasure reach every copy, turn on
[crypto-shredding](crypto-shredding.md).

Package: `AgentExperience.Storage.Postgres` (and `AgentExperience.Storage.Postgres.Vectors` for the embedding). Read
the KL-2 boundary in [Known limits and documented boundaries](../known-limits.md#documented-boundaries).

## Deleting a record

```csharp
var store = new PostgresExperienceRecordStore(dataSource);

// Erase one record, in exactly this scope.
var deleted = await store.DeleteAsync(hostAuthorization, scope, experienceId, cancellationToken);
// deleted.Outcome is Deleted, NotFound, Invalid, or Denied. Deleting again is Deleted, and writes nothing.

// Or erase it only while it is still at the revision you read.
var guarded = await store.DeleteAsync(hostAuthorization, scope, experienceId, expectedRevision: 4, cancellationToken);
// StaleRevision, carrying the record's current revision, when it has moved on.
```

`DeleteAsync` is the only destructive operation on records, and every choice in it is resolved towards "a wrong
delete is refused" rather than "a right delete is convenient". The application role can call it only when the host
opted in with `AllowErasure` (see [Deployment](deployment.md#deploying-with-two-roles)).

- **A tombstone, never a vanishing row.** The `experience_records` row survives with its payload emptied; everything
  else that named the record is removed. That is what makes the ID unusable afterwards rather than free to be written
  again. The access trail (`experience_grant_access`) is deliberately kept: it carries no payload and is the answer to
  "who read this before it was deleted".
- **A tombstone is terminal.** A late create, lifecycle commit, confidence submission, feedback write, index write,
  or grant naming it is refused, never resurrected — and, within its own scope, a host can tell `Deleted` from
  `NotFound`. Across scopes the two collapse: a foreign-scope delete is the same answer as one naming an ID that
  never existed.
- **With two roles, the application's own role cannot get round it.** The purge path is one code path, one
  transaction, and a guard that is never switched off. The marker it sets is a custom setting any session can set, so
  on its own it decides nothing; what binds the application role is that it holds no `DELETE` on any ledger and no
  `UPDATE` on the tombstone's columns, so only the `SECURITY DEFINER` purge functions, running as the owner, can
  erase — and it can call those only when the host opts in (`AllowErasure`, `AllowAccessLogPurge`). The owner role
  and superusers are not bound, which is inherent in PostgreSQL.

### What is retained after a delete, exhaustively

| Column | Why it stays |
| --- | --- |
| `experience_id` | The tombstone itself: an opaque ID nothing can re-create a record under |
| `tenant_id`, `application_id`, `project_id`, `team_id`, `agent_id`, `user_id` | The scope, so the tombstone stays answerable to — and only to — the scope that owned it |
| `revision` | Erasure advances it once, like any other change, so a stale write still loses |
| `deleted_at` | When it was erased |
| `status` | The fixed literal `'Deleted'`. No `ExperienceStatus` member names it, and no read decodes it |
| `task_id` | The fixed literal `'(deleted)'`. The column is `NOT NULL` with a non-blank `CHECK`, so it cannot be emptied |
| `payload_version` | Unchanged. It describes the (now empty) payload envelope's shape and says nothing about the record — but it *does* survive, so it belongs on a list that calls itself exhaustive |

**Nothing else.** `payload` becomes `'{}'`, `source_run_id` becomes the empty UUID, `reuse_confidence`,
`supporting_validations` and `contradictions` become `0`, and `created_at` and `updated_at` are set to `deleted_at`
— a tombstone's only timestamp is the moment it was erased, so it cannot say when the work happened.
`search_vector` is `GENERATED ALWAYS` from `task_id` and two payload fields, so it regenerates from the placeholder
alone and the record's searchable text is gone without any separate index maintenance.

That last sentence is a property of `purge_experience_record` and of every tombstone this library made — not
something the schema can prove about a row somebody else inserted. The tombstone-shape `CHECK` constrains an
existing row's *shape*; a row inserted directly as a tombstone can carry any `created_at` it likes, because there is
no `UPDATE` for the projection guard to refuse. The same goes for its revision.

### What one delete removes

One transaction, this order, all inside `0010`'s `agent_experience.purge_experience_record`:

| # | Table | Why here |
| --- | --- | --- |
| 1 | `experience_records` | `SELECT … FOR UPDATE` with the scope and revision guards. Authorization, and the row pinned for the rest |
| 2 | `confidence_evidence` | **Before the tombstone.** It has no scope columns and no foreign key, so the record row's scope is the only thing that makes it reachable by scope at all |
| 3 | `reuse_feedback_exposures` | Children before parents: the foreign key to `reuse_feedback` is `NO ACTION` |
| 4 | `reuse_feedback` | Only the submissions step 3 emptied. One that also named other records keeps its row and loses only this exposure |
| 5 | `experience_grant_events` | Before the grants: the audited-delete guard refuses a grant that still has events |
| 6 | `experience_grants` | Purged with the record, because `experience_grants` has no foreign key to it and a re-appearing ID would otherwise re-apply them |
| 7 | `lifecycle_events` | The record's own history |
| 8 | `experience_embeddings` | Guarded by `to_regclass`: the table belongs to the vectors package, and a text-only deployment simply skips the step |
| 9 | `experience_records` | The tombstone, last, so every scope-dependent sweep above still had its scope |

**`experience_grant_access` rows are deliberately kept.** They name a grant and a principal, carry no record
payload, and are the answer to "who read this before it was deleted" — which is exactly the question a deletion
makes urgent. They have their own [retention path](#retention-for-the-grant-access-log).

**Authorization is decided once, at step 1, and every step below it follows the record's ID rather than the
caller's scope.** That is true of *all* of steps 2–8, not only the ones without scope columns:

- `confidence_evidence` and `reuse_feedback_exposures` carry no scope columns at all (by design — see `0007` and
  `0008`), so "every row that named this record" is the only thing an erasure could mean for them.
- `experience_grants`, `experience_grant_events` and `experience_embeddings` *do* carry the six owner-scope
  columns, copied from the record row when they were written, and are still matched on the record's ID alone.
  That is a choice. In practice the predicates coincide, because every grant and every vector this library writes
  copies its scope from the record; a row that disagrees was written outside this library, over an ID whose
  content is now gone, and is exactly the row nothing else would ever collect.

One consequence is worth stating plainly: if another scope recorded feedback naming this record's ID — which
`0008` deliberately allows, because a run that saw an ID resolving to nothing must still be recordable — that
exposure row goes with the erasure, and its submission goes too if this record was the only one it named. Erasing
a record's traces is what was asked for; it just is not confined to the scope that asked.

**Two purges sharing one feedback submission cannot orphan it.** A submission may name several records. Step 3
locks the submissions `FOR UPDATE`, in `feedback_id` order, *before* deleting any exposure, so a second purge asks
"are there any exposures left?" after the first has committed. Without that, erasing two records at once could leave
the parent row behind with zero exposures — and that row carries a run ID, a scope, an outcome, a measure and, for a
human assessment, a reviewer identity and a free-text rationale, so an orphan is not a tidiness problem.

### A tombstone is terminal

| A late… | Answer | Enforced by |
| --- | --- | --- |
| `CreateAsync` under the erased ID | `Conflict`, as for any taken ID, revealing nothing about which scope holds it | **Schema** — the primary key collides with the surviving tombstone row |
| `CommitLifecycleEventAsync` | `Deleted`. Nothing is appended | **Both** — the store's predicate, and `0010`'s projection guard, which refuses any `UPDATE` of a tombstone from the database's own side |
| confidence submission | `Deleted`, with no ledger row: an erased record's ID must not go back into a table the erasure emptied | Store |
| reuse-feedback write naming it | `Invalid`, naming the exposure by position. Only tombstones in the submission's own scope are visible to that check | Store |
| embedding write | `Missing`, never `Stale`: no revision of an erased record can ever be indexed | Store |
| grant over it | `NotFound`: there is nothing left to share | Store |
| any `UPDATE` of the tombstone row, marker or not | Refused, `42501` | **Schema** |
| any `DELETE` or `TRUNCATE` of the record row, marker or not | Refused, `42501` | **Schema** |
| `GetAsync` / `GetHistoryAsync` in the owning scope | `Deleted`, with no record and no events | Store |
| `QueryAsync`, text search, vector search | The tombstone is simply absent | Store |
| anything at all from another scope | `NotFound`, exactly as for an ID that never existed | Store |

**The distinction in that last column matters.** Four of the write refusals are *store*-enforced: they are
predicates this library puts in its own statements, and raw SQL from another tool can still `INSERT` a lifecycle
event, a confidence-evidence row, an exposure, an embedding or a grant against a tombstoned ID. None of those tables
has a foreign key to `experience_records`, deliberately (`0002`, `0005`, `0007`, `0008`), and adding one now would
rewrite four journaled tables' shapes for this one rule. What *is* schema-enforced is the part that cannot be worked
around: the ID can never be re-created, the tombstone can never be moved, and the record row can never be removed —
which together mean an ID, once spent, is spent.

**Those store predicates are locked, not merely read.** Every write that gates on "this record is not a tombstone"
takes `FOR KEY SHARE` on the record row in the same statement, so a writer that started before an erasure committed
is parked against the purge's own `FOR UPDATE` and re-checks when it is released, instead of deciding against a
snapshot the purge has already invalidated. Without that, a write issued a moment after a `DeleteAsync` returned
`Deleted` could still land: a stored vector derived from the erased summary and lesson, a live 90-day grant over a
spent ID, or a reviewer's identity and free-text rationale about the erased record, permanently, in an append-only
table. `FOR KEY SHARE` rather than `FOR SHARE` on purpose — it is the weakest mode that still conflicts with the
purge, and it does not block an ordinary lifecycle commit.

### A record whose run was erased can never be finalized again

`ExperienceFinalizationService.ExperienceIdFor` derives a record's ID from the run *and the scope*,
deterministically, so replaying finalization for that run derives the same ID, collides with the tombstone, and
stops — permanently. That is the intended terminal semantics: re-finalizing would recreate exactly what the deletion
removed. It is reachable only by the scope that owns the record, and that scope can see exactly why: `GetAsync`
answers `Deleted` for its own tombstone. (Mixing the scope into the derivation removed the one-argument
`ExperienceIdFor(Guid)` with no compatible overload, before the first preview was published: an `[Obsolete]`
overload could not have been kept honestly, because it would have had to go on deriving an ID any scope could
squat. Callers pass the same `Scope` they finalize under; nothing persisted needs migrating, because a record's ID is
stored, never re-derived.)

## Retention

There is **no default retention and no timer**. Nothing expires unless a host asks for it, and this library ships no
scheduler, no background service and no hosted service: when a sweep runs is the host's decision, because only the
host knows its obligations.

```csharp
// Erase this scope's records older than 90 days, at most 200 at a time.
var sweep = await store.SweepExpiredAsync(hostAuthorization, scope, TimeSpan.FromDays(90), batchSize: 200, cancellationToken);
// sweep.DeletedCount, and sweep.MoreRemain when another pass would find more.

// The same, for the scope and every scope beneath it. Opt-in; the overload above is always exact.
var wide = await store.SweepExpiredAsync(hostAuthorization, projectScope, TimeSpan.FromDays(90), batchSize: 200, ScopeMatch.Subtree, cancellationToken);

// Expired sharing grants, with their audit events. Administrator authority, like every other grant mutation.
var grants = new PostgresExperienceGrantStore(dataSource);
var purged = await grants.PurgeExpiredAsync(hostAuthorization, administration, scope, batchSize: 200, cancellationToken);

// Grant access rows the database recorded more than a year ago, for the project and everything beneath it.
var accessLog = new PostgresExperienceGrantAccessLog(dataSource);
var pruned = await accessLog.PurgeOlderThanAsync(
    hostAuthorization, administration, projectScope, DateTimeOffset.UtcNow.AddDays(-365), ScopeMatch.Subtree, batchSize: 200, cancellationToken);
```

A sweep runs through the same delete. Age is measured from `CreatedAt` on the store's own `TimeProvider`, never from
`UpdatedAt`: age is how long this library has held the data, and a record that is read, ranked, or re-scored does not
thereby become younger. Each record in a batch is erased in its own transaction, so an interrupted sweep leaves every
record it reached wholly erased and every record it did not reach wholly untouched. A non-positive age is `Invalid` —
there is no retention age that means "delete everything" — and so is a batch outside 1…500. `DeletedCount` counts
only the records *this* call erased: when two sweeps race over the same records, the one that finds a record already
a tombstone does not count it again, so their counts sum to exactly what was erased.

### A sweep reaches one scope, or everything beneath it, and you choose which

This is the one operation here whose failure mode is **a missed retention obligation reported as success**, so read
it carefully.

**`ScopeMatch.Exact` — the default, and what the five-argument overload does.** `scope` is matched field for field,
exactly as every other operation in this library matches it. A sweep of `new Scope(tenant, app, project)` — team,
agent and user all null — reaches only the records stored with all three of those fields null. Every record the same
project holds under a team, an agent or a user is a **different scope**: it is not swept, it is not counted, and the
call comes back `Outcome: Deleted, DeletedCount: 0, MoreRemain: false` — which reads exactly like "there was nothing
to delete".

**`ScopeMatch.Subtree` — the scope and everything beneath it.** A host whose policy is "everything in this project
older than N days" says so:

```csharp
var sweep = await store.SweepExpiredAsync(auth, new Scope(tenant, app, project), TimeSpan.FromDays(90), 200, ScopeMatch.Subtree, ct);
while (sweep.MoreRemain)
{
    sweep = await store.SweepExpiredAsync(auth, new Scope(tenant, app, project), TimeSpan.FromDays(90), 200, ScopeMatch.Subtree, ct);
}
```

**What "beneath" means, exactly.** A stored scope is at or beneath a root when:

- its `TenantId`, `ApplicationId` and `ProjectId` equal the root's — always exactly; these three are never a
  wildcard, and a blank one is `Invalid` as everywhere else;
- and, for each of `TeamId`, `AgentId` and `UserId`, the root's value is `null` **or** equals the stored one.

Comparisons are ordinal and case-sensitive. A `null` root field matches any stored value, `null` included; a set
one matches only itself — never `null`, never a prefix, never a different case. A blank root field (`""`, `" "`) is
`Invalid`, never "any": only `null` widens. Concretely, under one project:

| Root (team / agent / user) | Reaches | Never reaches |
| --- | --- | --- |
| `– / – / –` (the project) | every record in the project | another tenant, application or project |
| `t1 / – / –` | `t1`, `t1/a1`, `t1/a1/u1`, `t1/–/u1` | the project root (an ancestor), `t2`, `t2/a1` (siblings) |
| `t1 / a1 / –` | `t1/a1`, `t1/a1/u1`, `t1/a1/u2` | `t1` (an ancestor), `t1/a2`, `t2/a1` |
| `t1 / a1 / u1` | `t1/a1/u1` only | `t1/a1`, `t1/a1/u2` |
| `– / a1 / –` | `a1` under any team or none: `–/a1`, `t1/a1`, `t2/a1`, `t1/a1/u1` | `t1`, `t1/a2`, the project root |
| `– / – / u1` | `u1` under any team and agent: `–/–/u1`, `t1/–/u1`, `t1/a1/u1` | `t1/a1/u2`, `t1/a1` |

The three optional fields are independent dimensions, not a chain: a root that names only a user reaches that
user's records under every team and agent in the project, which is what a per-user erasure needs. It is the same
reading `AuthorizationContext` gives a `null` bound ("a `null` bound leaves that field unrestricted").

**Authorizing the root authorizes the subtree — provably, and with no new authorization shape.** The sweep checks
`authorization.Permits(root)` once, before any connection opens. If an authorization permits the root, every non-null
bound on it equals the root's field; for a field the root leaves `null`, a non-null bound cannot equal it, so that
bound is `null` too — unrestricted. So the same authorization permits every scope beneath the root. The converse is
the protection: a caller bounded to team `t1` is `Denied` a project root outright, so a subtree can never be used to
widen what it may erase. Each candidate is also checked in process against the root and the authorization before it
is erased, and is erased through the unchanged `purge_experience_record` **in its own exact stored scope**, one
record per transaction.

**Bounded and truthful across the subtree.** One page of at most `batchSize + 1` candidates across the whole
subtree, oldest `CreatedAt` first, so `MoreRemain` means "more past the cutoff anywhere beneath the root". The page
is served by `0010`'s `ix_experience_records_live_by_age`.

**What it still cannot do: span projects.** `Scope` has no way to say "any project", and adding one would change the
port and the authorization shape. A host with several projects sweeps each project root — a short list it
configures, rather than the leaf scopes Subtree exists so that nobody has to discover.

### Stopping early

Erasure is the one thing this library cannot undo, so a sweep that stops half-way never throws away how much it
destroyed:

- **Cancellation** between records *returns* the partial result, with `Interrupted: true` and `MoreRemain: true`.
  Cancelling a sweep is a normal way to run one, and a host that asked for it still needs the count for its own
  compliance log.
- **A storage failure** part-way throws `ExperienceRetentionSweepInterruptedException`, which carries the same
  partial result on `.Partial` and is an `ExperienceStoreException` like every other storage failure here — so a
  host that already catches those keeps working and does not have to learn a new type to stay correct.

### Expired grants

`PurgeExpiredAsync` collects a grant once its stored `expires_at` has passed (and any grant naming a record that is
already a tombstone). A revoked grant that has **not** expired is left alone: its revocation is a fact about a
window that is still open, and it is collected when that window closes. The cutoff is
`LEAST(hostClock, clock_timestamp())`: everywhere a grant is *read*, this schema deliberately uses the database's
clock so a host whose clock is wrong cannot widen a permission, and this is the one grant operation that
*destroys* rows — a host skewed a day forward must not be able to erase grants the database still considers live.
The batch bound is applied inside the function too, not only by the validator, because `LIMIT NULL` means "no
limit" in PostgreSQL and a hand-caller passing `NULL` would otherwise get an unbounded destructive sweep.

### Retention for the grant access log

Erasing a record keeps its `experience_grant_access` rows on purpose — they answer "who read this before it was
deleted" — so the ledger needs its own retention path, and `0012` is it:

```csharp
var pruned = await accessLog.PurgeOlderThanAsync(
    auth, administration, ownerScope, cutoff: DateTimeOffset.UtcNow.AddDays(-365), ScopeMatch.Subtree, batchSize: 200, ct);
// pruned.PurgedCount, and pruned.MoreRemain when another pass would find more.
```

- **Authorized like the expired-grant purge.** Administrator authority is required — it removes an audit trail —
  and `auth` must permit the owner scope; both are checked before a connection opens. The application role can call
  it only with `AllowAccessLogPurge`. `ScopeMatch` means exactly what it means for the sweep, over the rows'
  **owner** scope; the recipient scope plays no part.
- **Age is `recorded_at`, the database's clock when the row landed** — never `occurred_at`, the reader's clock,
  which a skewed reader could backdate into the purge window.
- **A row is kept at least 30 days (`MinimumRetentionDays`), by the database's clock.** The purge must not be
  usable, through this library, to erase a read the moment after it happened — a bug, a careless script, or a
  misused call covering one. It does not bind the tables' owner, which can disable the trigger or insert rows with
  any `recorded_at`; in the [two-role deployment](deployment.md#deploying-with-two-roles) the application role is not
  the owner and holds no `DELETE` on the ledger at all. A cutoff later than that floor is **refused, not clamped**:
  `Invalid` on `Cutoff`, nothing removed. A clamp would report a clean purge while rows the host asked about
  survived, which is the failure mode the sweep section above is about. The append-only guard re-checks the floor on
  every row it admits, so even a session that sets the marker by hand cannot delete a younger row. Thirty days is a
  floor, not a policy; the host's retention is the cutoff it passes.
- **Bounded, and one transaction per batch.** At most `batchSize` rows (1…500, and clamped to that inside the
  function too, so a hand-caller's `NULL` is never "no limit"), oldest `recorded_at` first, locked in a fixed order
  so two concurrent purges neither deadlock nor count a row twice. `MoreRemain` is asked after the delete, in the
  same transaction, with the same predicate.
- **Its own marker.** `purge_grant_access` sets `agent_experience.access_purge_authorized`, not `0010`'s
  `purge_authorized`, and the guard admits a `DELETE` on this ledger only under it. `0010`'s marker still admits
  nothing here, so "erasing a record keeps its access rows" stays a property of the schema.

## How erasure gets past the append-only guards

Erasure needs `DELETE` on five append-only tables. The guards themselves recognise one transaction-scoped marker,
`SET LOCAL agent_experience.purge_authorized = 'on'`, set only inside the purge function, and they go on refusing
`UPDATE` and `TRUNCATE` unconditionally in every session, including the purging one. Nothing is ever disabled, and no
other connection's window is widened for an instant. (`0006` documented a manual `ALTER TABLE … DISABLE TRIGGER`
runbook for this; `0010` replaces it rather than automating it, and that runbook must not be used.)

**The marker is not the boundary; the application role's privileges are.** Be precise about which does what:

- A custom setting is settable by any session. A connection that holds `DELETE` on these tables can issue the same
  `SET LOCAL` itself and then delete from them directly. The marker decides whether a *permitted* delete is refused;
  it never decides permission. That is why, in the two-role deployment, the application role holds **no** `DELETE` on
  any ledger and no `UPDATE` on the tombstone's columns: setting the marker by hand gains it nothing, because the
  privilege system refuses the statement before a trigger runs. The tests prove this from the application role's own
  connection, with each marker and both.
- The guards do not bind a role that can `ALTER TABLE`. In the two-role deployment the application role owns
  nothing and cannot; the owner role and superusers can, and that is inherent in PostgreSQL. In a single-role
  deployment the application *is* the owner, and none of this binds it.

What the purge path buys on top of that is a single code path: erasure happens inside **one** transaction, with the
guard never switched off, never left off across a failure, and never visible to another session. It guards against a
bug, a careless script, or a compromised application path — including one holding the application role's
credentials — but not against an administrator holding the owner's or a superuser's credentials.

**`EXECUTE` on the purge functions is the application role's only way to erase, and the host decides it.** All three
are `SECURITY DEFINER`, and PostgreSQL grants `EXECUTE` on a new function to `PUBLIC` by default — which would make
them a universally callable erasure primitive, reachable by any role that can connect, over any tenant whose
`experience_id` and scope it can `SELECT`. `0010` and `0012` therefore revoke `EXECUTE` from `PUBLIC` (and `0013`
restates it), and `ApplyApplicationRolePrivilegesAsync` grants it to the application role only when the host sets
`AllowErasure` (`purge_experience_record`, `purge_expired_grants`) or `AllowAccessLogPurge` (`purge_grant_access`),
and verifies that nothing else can reach a `SECURITY DEFINER` function in the schema. `0013` pins each function's
`search_path` to `pg_catalog, agent_experience, pg_temp`, `pg_temp` last as PostgreSQL recommends for
`SECURITY DEFINER`.

## What deletion does not reach in plaintext mode

Plaintext mode is the default. With [crypto-shredding](crypto-shredding.md) configured, the first and fourth bullets
below shrink to the derived search data, and that page says exactly what remains.

- **Backups, replicas, WAL, and logical-replication streams.** Host-owned, and out of reach of this schema. A
  deployment with a retention obligation has to reach them itself.
- **Exported telemetry.** Spans and metrics this library emitted carry record IDs; erasing a record does not
  retract them. The erasure's own telemetry adds nothing that was erased (see [below](#telemetry)).
- **External artifacts a record merely named.** Tickets, logs, commits: the library never held them.
- **The dead heap tuple — until `VACUUM`, the erased text is still in this database.** The tombstone is written
  with an `UPDATE`, and an `UPDATE` in PostgreSQL writes a new row version and leaves the old one in the heap.
  Until `VACUUM` reclaims it, the previous version of the record row still carries the task summary, the lesson,
  the attempt results and the task ID, readable by anyone who can inspect the page — `pageinspect`, a file-level
  copy, a base backup taken in that window. The same is true of every row the erasure deleted. Autovacuum will
  get there on its own schedule, which is not a schedule anybody promised; an obligation with a deadline has to
  run `VACUUM agent_experience.experience_records` (and the other swept tables) itself. `VACUUM` does not
  overwrite the freed bytes either, so defeating forensic recovery of freed pages needs `VACUUM FULL` — which
  rewrites the table under an `ACCESS EXCLUSIVE` lock — or a storage-level guarantee.
- **Dead index entries.** Entries in the GIN index over `search_vector`, and in the out-of-band HNSW index over
  `experience_embeddings`, persist until `VACUUM` reclaims them. *These* really do point at row versions that no
  longer carry the erased text, so they cannot return it — the distinction from the bullet above is exact.

## Telemetry

Erasure is the one part of the PostgreSQL package that emits telemetry, on the `AgentExperience.Storage.Postgres`
`ActivitySource` and `Meter`, which a host subscribed to `AgentExperience.*` already receives. `DeleteAsync` is the
`delete` operation, `SweepExpiredAsync` is `retention.sweep`, `PurgeExpiredAsync` is `grant.purge`,
`PurgeOlderThanAsync` is `grant.access.purge`, and the crypto-shredding upgrade job, `SealPlaintextRecordsAsync`, is
`record.seal`. Each call produces one span, one count and one duration under the result's `ExperienceStoreOutcome`
name, and `agentexperience.operation.failures` moves only when the call throws. These are the same instruments and
dimensions Core uses. A `delete` span carries the record's ID. A sweep or purge span carries `erased_count`; a sweep
span also carries `interrupted`; sweep and access-log purge spans carry `scope_match`; and a `record.seal` span
carries `sealed_count` and `scope_match`. None of them carries a scope, task ID, payload, grant ID, grant reason,
recipient scope or administrator. The full contract is in [the telemetry contract](../telemetry.md#operations).
