# AgentExperience.Storage.Postgres

Stores AgentExperience.NET Experience Records in PostgreSQL through the `IExperienceRecordStore` port, searches
them by task text through the `IExperienceCandidateSource` port, and administers explicit sharing grants through the
`IExperienceGrantStore` port, using plain Npgsql.

Pinned to `Npgsql` **10.0.3**, `dbup-postgresql` **7.0.1**, `dbup-core` **6.1.1**, and
`Microsoft.Extensions.DependencyInjection.Abstractions` **10.0.11** (all exact; the DI package is abstractions only —
no container, no hosting — and exists for this package's own registration extension). Integration tests run against
PostgreSQL 16 (`pgvector/pgvector:pg16`) through `Testcontainers.PostgreSql` 4.15.0. This package does not use EF
Core, Dapper, Pgvector, or the pgvector extension.

## Usage

```csharp
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;

await using var dataSource = NpgsqlDataSource.Create(connectionString);

// Once at startup, before the store is used. See "Schema" below.
await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken);

IExperienceRecordStore store = new PostgresExperienceRecordStore(dataSource);

// Established by the host from its own authentication and authorization. Never built from request input.
var authorization = new AuthorizationContext(
    TenantId: "tenant-1",
    PrincipalId: "svc-support-agent",
    Roles: ["experience:write"],
    IssuedAt: DateTimeOffset.UtcNow,
    ProjectId: "support");          // optional bound: this caller may only touch the "support" project

var created = await store.CreateAsync(authorization, record, cancellationToken);
var read = await store.GetAsync(authorization, record.Scope, record.ExperienceId, cancellationToken);
var page = await store.QueryAsync(
    authorization,
    new ExperienceRecordQuery(record.Scope, Statuses: [ExperienceStatus.Validated], Limit: 20),
    cancellationToken);

// A lifecycle change: the event and the record's projection commit together, or neither does.
var commit = await store.CommitLifecycleEventAsync(
    authorization,
    record.Scope,
    new LifecycleEvent(
        EventId: eventId,                             // this call's idempotency key -- stable across retries
        ExperienceRecordId: record.ExperienceId,
        PriorStatus: ExperienceStatus.Candidate,
        CurrentStatus: ExperienceStatus.Validated,    // decided by Core, never by this adapter
        Reason: "required checks passed",
        Producer: "finalization",
        OccurredAt: decidedAt,
        ExpectedRevision: record.Revision),           // must equal the stored revision
    cancellationToken);
// commit.Revision is record.Revision + 1 when commit.Outcome is Committed.

var history = await store.GetHistoryAsync(
    authorization,
    new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, Limit: 100),
    cancellationToken);   // or GetFirstHistoryPageAsync(...) for the first page and nothing more
// history.Events is one page of transitions, oldest first, each carrying the store's own RecordedAt and the
// AppliedRevision it produced; history.Revision is the record's current revision; history.NextStartAfterRevision
// is the cursor for the next page.

// Finding records that could apply to a task. A separate, read-only port (see "Text search" below).
IExperienceCandidateSource search = new PostgresExperienceCandidateSource(dataSource);

var candidates = await search.SearchAsync(
    authorization,
    new ExperienceCandidateQuery(
        Scope: record.Scope,                                          // exact scope, applied in SQL
        TaskText: "refund ticket stuck on a lock",                    // arbitrary text; no escaping needed
        EligibleStatuses: [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
        MinimumConfidence: 0.5,
        Limit: 50),
    cancellationToken);
// candidates.Candidates is strongest match first, each with a Relevance in [0, 1].
```

Neither the store nor the search disposes the data source. The host owns it.

### Registering it

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres.DependencyInjection;

services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddAgentExperiencePostgresStore();             // or AddAgentExperiencePostgresStore(dataSource)
services.AddAgentExperiencePostgresCandidateSource();   // or ...CandidateSource(dataSource)
services.AddAgentExperiencePostgresGrantStore();        // or ...GrantStore(dataSource) -- only if you share records

// Core's own extensions then supply capture, reflection, lifecycle, finalization, and retrieval over them.
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
services.AddAgentExperienceRetrieval();
```

The ports are registered independently: a host that only writes experience never has to register the search, one
that only reads never has to register the store, and one that never shares a record across scopes never has to
register the grant store — the reads that honour grants do so in SQL either way. Every registration is
`TryAdd`-based, so a host that has already registered its own `IExperienceRecordStore`,
`IExperienceCandidateSource`, or `IExperienceGrantStore` keeps it.
It does **not** apply the schema: call `ExperienceSchemaMigrator.MigrateAsync` once at startup (see
[Schema](#schema)).

Records are normally written by Core's `ExperienceFinalizationService`, which creates the record and commits its
initial lifecycle event; `CreateAsync` and `CommitLifecycleEventAsync` stay available for hosts that orchestrate that
themselves.

## Trusted host boundary

- `AuthorizationContext` is the authority, and `Scope` only selects within it. The host must build the context from
  its own trusted identity and permission checks, never from model output or request payloads.
- `TenantId` must always match. A non-null bound (`ApplicationId`, `ProjectId`, `TeamId`, `AgentId`, `UserId`) must
  equal the request scope's field exactly. A null bound leaves that field unrestricted. A scope outside the context
  returns `Denied` before any connection opens.
- Scope matching in SQL is exact, ordinal, and case-sensitive. A null optional scope field matches only null
  (`IS NOT DISTINCT FROM`) and never acts as a wildcard. Empty or whitespace scope values are `Invalid`.
- A get for an ID that exists in another scope returns `NotFound`, the same as a missing ID. A create with an
  existing ID returns `Conflict` without record data, whichever scope owns the existing record.
- This store does not evaluate roles. Role-based decisions stay with the host.

## Results and failures

| Situation | Result |
| --- | --- |
| Saved | `Created` |
| Read (a query with no matches is still `Found`) | `Found` |
| ID missing, or in another scope | `NotFound` |
| Scope outside the authorization context | `Denied` (no connection opened) |
| Malformed request | `Invalid` with every `StoreValidationError(Path, Message)` (no connection opened) |
| ID already exists in any scope | `Conflict` (stored row unchanged) |
| Lifecycle event and projection committed together | `Committed` |
| Lifecycle `ExpectedRevision` ≠ the record's current `Revision` | `StaleRevision` (nothing written) |
| Lifecycle `PriorStatus` ≠ the record's stored `Status` | `StatusMismatch` with the stored status (nothing written) |
| Supersession check ran | `Allowed` with the replacement's status, or `RecordNotFound` / `ReplacementNotFound` / `Cycle` (nothing written either way) |
| Lifecycle event names a replacement the commit transaction will not accept | `ReplacementNotAllowed` with the replacement's stored status (nothing written) |
| `UPDATE` or `DELETE` against a stored event row, or a grant revocation cleared or expiry extended | rejected by the database with SQLSTATE `42501`, surfaced as `ExperienceStoreException` |
| Candidate search ran (no text match is still `Found`) | `Found` with the matching candidates, strongest match first |
| Database or driver failure (`NpgsqlException`, `SocketException`, `TimeoutException`) | throws `ExperienceStoreException` with the original as `InnerException` |
| Stored row with an unsupported `payload_version` or an unreadable payload | throws `ExperienceStoreException` |
| Caller cancellation | throws `OperationCanceledException`, unwrapped |

Validation messages never contain record payload content, and the store does not log.

A create whose acknowledgement was lost (cancelled or timed out after PostgreSQL committed it) returns `Conflict`
when retried. After a `Conflict`, call `GetAsync` in your own scope to check whether the stored record is yours.

## Lifecycle commits

`CommitLifecycleEventAsync` is the only way a stored record's status changes. It appends the `LifecycleEvent` to
`lifecycle_events` and updates the record's `status`, `revision`, and `updated_at` **in one transaction on one
connection**: both writes commit together, or neither does. A failure between them leaves no event and no
projection change.

The adapter persists the decision exactly as given. It never derives a status, a reuse confidence, or a counter — a
confidence update writes the numbers Core computed and nothing else (see
[Confidence evidence](#confidence-evidence)) — and it never invents a transition the command did not carry:
deciding which transitions are legal belongs to Core's
`ExperienceLifecycleService` (ARCHITECTURE-SPINE AD-6). Authorization is checked against the request scope before
the transaction opens, exactly as for the store's other operations, and the scope predicate is applied in SQL.

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
  back to `CurrentStatus`, so a first event may only record the status the record is already in. Skipping it, which
  this statement used to do, was a hole straight through Core's transition table: omit the prior status and a record
  moved from anywhere to anywhere. That is what keeps Core's transition table enforced
  against real state rather than against what the caller asserted, and keeps a stored event from recording a prior
  status the record never had. A mismatch is `StatusMismatch`, writes nothing, and reports the record's stored
  status as `result.CurrentStatus` so you can re-decide against it.
- **Missing or foreign records.** A record that does not exist in the request scope is `NotFound`, indistinguishable
  from a missing one, and nothing is written.
- **A lost acknowledgement.** A commit that was cancelled or timed out after PostgreSQL committed it is recovered by
  retrying the *identical* event: the replay path reports the original `Committed` and the revision that commit
  produced, without applying it twice. This is why `EventId` and `OccurredAt` must be stable across retries — unlike
  a create, where a lost acknowledgement surfaces as `Conflict` and has to be resolved with `GetAsync`.
- **Supersession's replacement.** An event that moves a record to `Superseded` carries
  `ReplacementExperienceId`, stored in its own column on the event row. The database states the rule as a `CHECK`,
  so a superseding event with no replacement, a replacement on any other transition, and a row naming itself as its
  own replacement are all unstorable however the write arrives. Which replacements are *acceptable* stays Core's
  decision; the adapter answers the parts only a scoped query can (see below) and persists what Core decided.
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
- **History.** `GetHistoryAsync` returns the record's current `Revision` plus **one page** of events, oldest first,
  in a single statement, so the revision can never contradict the events even if a commit lands mid-read. The page
  is bounded by `Limit` (1–500, default 100) and started by the keyset cursor `StartAfterRevision`; pass the
  previous page's `NextStartAfterRevision` to walk a longer history with no gap and no repetition, and stop when a
  page comes back empty. The cursor is applied in the outer join's `ON` clause rather than in the `WHERE`, which is
  what keeps a record whose history is exhausted `Found` with an empty page instead of collapsing into `NotFound`.
  Each event comes back as a `StoredLifecycleEvent`: the `LifecycleEvent` exactly as it was stamped, plus
  `RecordedAt` (when the *database* accepted the row, on its own clock) and `AppliedRevision` (the revision the
  event produced). `GetAsync` and its result are unchanged by this operation.
- **Append-only, enforced.** `0006` installs row-level `BEFORE UPDATE`/`DELETE` triggers on `lifecycle_events` and
  `experience_grant_events`, statement-level `BEFORE TRUNCATE` triggers on those two and on `experience_grants`
  (`TRUNCATE` fires no row triggers, so a row-level guard alone leaves a whole log erasable with no error), a
  `BEFORE DELETE` guard refusing to delete any grant that has audit events (delete-and-reinsert would restore a
  revoked grant unrevoked), and `BEFORE UPDATE` guards that pin a grant's identity and audit columns while keeping
  its revocation permanent and its expiry non-extendable. `experience_records` gets one too: a revision only moves
  forward and a status changes only with it, because an immutable log beside a freely rewritable projection proves
  nothing. All of them raise SQLSTATE `42501`, which the store surfaces as an `ExperienceStoreException` — no
  supported code path reaches them, so hitting one means something bypassed the store.
  **What they bind:** ordinary writes from any role, superusers included, and — because every trigger is created
  `ENABLE ALWAYS` — writes made under `session_replication_role = 'replica'`, which is how logical-replication
  appliers and several restore and ETL tools run and where an ordinary trigger is skipped silently.
  **What they do not bind:** anyone who can `ALTER TABLE` these tables — a superuser, or the tables' own owner,
  which the application role is since it created them — because an owner can `DISABLE TRIGGER`, drop the trigger, or
  drop a constraint first. Row-level security and column-privilege `REVOKE` are no stronger; neither binds an owner
  either. Nor do they say anything about backups, a restore that recreates the tables without `0006`, or filesystem
  access. Treat this as a guard against a bug, a careless script, a compromised application path, or a replication
  apply — not as tamper-proofing against an administrator. A deployment that needs more should ship the log off-box,
  or own these tables with a role the application does not have.
- **Purging, until story 4.5.** Nothing can delete an event row now, and the logs carry free-text `reason` and
  `producer` a host may have filled with personal data. The owner purges explicitly — `DISABLE TRIGGER`, a narrow
  `DELETE`, `ENABLE ALWAYS TRIGGER`, all in one transaction so the guard is never off across a failure — and
  reconciles `experience_records` afterwards, because deleting an event does not move the projection. `0006`'s
  header carries the exact statements.

## Confidence evidence

A `LifecycleEvent` may carry an optional `ConfidenceUpdate`. When it does, the same transaction that appends the
event and updates the projection also writes a row to `confidence_evidence` and sets the record's
`reuse_confidence`, `supporting_validations`, and `contradictions`. Every number in it was computed by Core's
`ReuseConfidenceHeuristic` from the record Core read; this adapter writes them and derives none. The score is
`(1 + S) / (2 + S + F)` — a heuristic, never a calibrated probability — and the `RuleVersion` that produced it
travels on the row.

Two rules are the adapter's, because only the transaction that writes the counters can decide them:

- **Independence is a unique index.** `confidence_evidence.independence_key` is a **generated** column:
  `'machine:' || run_id || ':' || verification_round_id` for machine evidence, `'human:' || reviewer_identity || ':'
  || run_id` for human evidence. A partial unique index on `(experience_id, independence_key) WHERE counted` admits
  the first submission for a key and no other. Generating it here means no writer picks the key *string*; it does
  **not** stop a writer inventing the key's inputs, and there is no foreign key behind `run_id` or
  `verification_round_id` because nothing in this schema knows what a run or a closed round is. Those two are a host
  trust boundary exactly like `reviewer_identity` — see the script header and the
  [main README](../../README.md#updating-confidence-from-evidence). Core computes the same string in
  `ConfidenceIndependenceKey`, and an integration test pins the two against each other.
- **A duplicate is recorded, and changes nothing else.** The first insert claims the key with `counted = true`,
  under a savepoint, because losing that race is an expected outcome the commit has to survive — a unique violation
  would otherwise abort the transaction that is supposed to record the duplicate. On the violation the statement is
  undone, the record is re-read `FOR UPDATE` (so the usual `NotFound`/`StaleRevision`/`StatusMismatch` refusals
  still apply), and the same submission is written again with `counted = false`. That ledger row is *all* the call
  writes: no counters, no status, no revision, no `updated_at`, and no lifecycle event. Refreshing `updated_at`
  would keep a record permanently recent and permanently un-expired under replay; writing the status would contest
  it on evidence that was not counted; and an event is impossible as well as unwanted, since it must claim
  `expected_revision + 1`. `result.AppliedConfidence` reports what was stored and its `Counted` says which happened,
  while `result.Revision` and `result.CurrentStatus` report the record the call left untouched.

`EvidenceId` is a second idempotency key alongside `EventId`. Resubmitting it with identical content — the same
record, kind, source, run and round or reviewer, rule version, and detail — reports the original outcome and the
revision that commit produced, and writes nothing. Resubmitting it with different content is `Conflict` with nothing
written. The counters are deliberately *not* compared: they are derived from whatever the record held when the
submission was first made, so comparing them would report a genuine replay as a conflict for agreeing with itself.
The event ID *is* compared for a stored row that produced one, so a retry under a fresh event ID is a `Conflict`
rather than a `Committed` carrying a lifecycle event that was never written. The lookup joins `experience_records`
and applies the exact scope predicate, so a guessed evidence ID from another scope reads back as no row at all —
the primary key is global, and this is the one statement that finds a row by it alone. Every number a replay
reports comes from that ledger row, so the answer describes one moment rather than a stored revision beside a
freshly read status.

Every commit now also records `lifecycle_events.actor` — the host's `AuthorizationContext.PrincipalId`, taken from
the authorization context and never from anything on the event, and surfaced as `StoredLifecycleEvent.Actor`. For
human evidence the same principal is the reviewer identity, which is what makes "one reviewer, one vote per run"
enforceable at all.

Ordering inside the transaction is not incidental: the evidence goes in **before** the event, because whether its
key was free decides which numbers the event must record, and an event is append-only the moment it is written.

## Text search

`PostgresExperienceCandidateSource` answers one question — *which stored records look relevant to this task text?* —
and nothing else. It is a separate port from the store on purpose: it only reads, it needs only `SELECT`, and a host
that never retrieves does not have to register it.

It runs in the same order as every store operation: validate the query, check the request scope against the
host-established `AuthorizationContext`, and only then open a connection. A scope outside the context is `Denied`
before any connection opens, and the scope predicate is applied in SQL exactly as it is for reads.

**What runs in the database:** the exact scope predicate, the caller's eligible status set, the reuse-confidence
floor (inclusive), the text match, and the limit (1–200, default 50). Nothing else. Because those filters run in SQL,
records they exclude never reach the caller and are never itemized anywhere — which is the point for scope, and worth
remembering for status and confidence. Expiry and environment
compatibility are Core's decisions, made over the candidates that come back, because they depend on the clock and on
the request rather than on stored state alone.

**What is indexed:** the task ID, the sanitized task summary, and the reflection's lesson — the fields that say what
a record is *about*. Attempts, tool calls, evidence, and environment metadata are deliberately not indexed: matching
on them would make retrieval recall incidental identifiers and error strings rather than applicable experience.

**The query text** goes through `websearch_to_tsquery`, which accepts arbitrary user input — quotes, `or`, `-`,
stray punctuation — and never raises a syntax error, so callers do not escape or sanitize around it. Multiple words
are combined with AND, and it is capped at `ExperienceCandidateQuery.MaxTaskTextLength` (4096) characters; longer is
`Invalid` before a connection opens. The text-search configuration is `english`, fixed by the generated column;
changing it means a new migration that rebuilds the column, because already-indexed rows would otherwise keep the
old analysis.

Because the `english` configuration drops stopwords, **text made only of stopwords matches nothing at all** — `"the
of and"` produces an empty query, and an empty query matches no row by construction. The result is an ordinary
`Found` with no candidates, indistinguishable from "nothing relevant is stored". A caller that wants to tell those
apart has to decide it before calling.

**Relevance** is `ts_rank_cd` with normalization flag 32 (`rank / (rank + 1)`), so it is already in [0, 1). It is a
within-search measure: two candidates' relevances are comparable to each other, never to a relevance from a different
query. Candidates come back in descending relevance, ties broken by `experience_id`; Core re-sorts with its own
total, ordinal tie-break when it ranks.

## Sharing grants

Scope is otherwise all-or-nothing. A **grant** is the one, audited exception: an administrator lets one named record
be *read* from one other scope, until it expires or is revoked. `PostgresExperienceGrantStore` administers them
through the `IExperienceGrantStore` port.

```csharp
IExperienceGrantStore grants = new PostgresExperienceGrantStore(dataSource);

// Administrator authority is a distinct, explicit input the host constructs. It is never derived from
// an AuthorizationContext, from AuthorizationContext.Roles, or from the requesting scope.
var administration = new GrantAdministration(AdministratorPrincipalId: "svc-sharing-admin", AuthorizedAt: DateTimeOffset.UtcNow);

var created = await grants.CreateAsync(
    authorization,                                   // the caller's own authority, over the record's owner scope
    administration,
    new ExperienceGrantRequest(
        GrantId: Guid.NewGuid(),
        ExperienceId: recordId,
        RecordScope: ownerScope,
        RecipientScope: ownerScope with { TeamId = "team-b" },
        Reason: "team-b owns the follow-up work",
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)),
    cancellationToken);
```

**Two authorities, never one.** Every grant-mutating call takes the `AuthorizationContext` *and* a
`GrantAdministration`. A `null` administration, or one whose principal is blank, is `Denied` before any connection
opens — administering sharing is not something a role string or a scope can imply.

**What a grant permits.** Reading one named record, and only reading: `GetAsync`, the text channel, and the vector
channel — and therefore injection, which re-reads through `GetAsync`. A granted record comes back exactly as its
owner sees it, still carrying the owner's `Scope`. `CreateAsync`, `CommitLifecycleEventAsync`, `GetHistoryAsync`,
`QueryAsync`'s enumeration, and issuing further grants all keep the exact-scope predicate, so none of them is ever
widened by a grant.

**Enforcement is a SQL predicate.** Reads compose `(exact scope) OR (an active grant naming this record and
permitting this scope)` in the same statement as everything else, so the database can never return a row the
predicate did not permit, and no application code is in a position to widen one. *Active* means issued, not revoked,
and not expired as of `clock_timestamp()` — the database's own wall clock, so a caller whose clock is wrong cannot
widen anything. It is `clock_timestamp()` rather than `now()` because `now()` is fixed at the start of the
surrounding transaction, and inside a long caller-held transaction that would keep admitting a grant that expired
minutes ago.

**Privileges, and deployments without the grant table.** Honouring grants needs `SELECT` on
`agent_experience.experience_grants` in addition to `experience_records`; administering them needs `INSERT`/`UPDATE`
on `experience_grants` and `INSERT` on `experience_grant_events`. The read privilege is **optional**: a role without
it, and a database that has not applied `0005` yet, are both supported. The first read that meets an undefined table
(`42P01`) or an insufficient privilege (`42501`) latches that reader into degraded mode, retries with the
exact-scope predicate alone, and reports it once through the optional `onGrantsUnavailable` callback on
`PostgresExperienceRecordStore`, `PostgresExperienceCandidateSource`, and `PostgresExperienceEmbeddingIndex`.
Degrading only ever **narrows** what a read returns, so it is a configuration problem rather than a safety one.

**Atomicity.** `CreateAsync` writes the grant row and its `Issued` event in one transaction, on one connection;
`RevokeAsync` updates the row and appends a `Revoked` event in another. Both or neither, every time. The insert's
source row is the canonical record itself, matched on the exact owner scope, so a grant over a record that is not
there writes nothing and returns `NotFound`, and a stored grant's owner scope is copied from the record rather than
asserted by the caller.

| Outcome | When |
| --- | --- |
| `Created` / `Revoked` | The grant and its audit event were committed together |
| `Found` | `ListAsync` or `GetHistoryAsync` answered; a listing may legitimately have no grants |
| `NotFound` | No such record, or no such grant, in the requested owner scope — including when it exists elsewhere |
| `Denied` | No administrator authority, or a scope outside the host authorization. Nothing was accessed |
| `Invalid` | Malformed request, with the field path. A recipient scope that changes tenant, application, or project, or that equals the owner's, is reported on that field; so is an undated `GrantAdministration` |
| `Conflict` | That `GrantId` is already stored in some scope, **or** an active grant already permits the same recipient over the same record. Nothing was written |
| `AlreadyRevoked` | The grant was already revoked. Nothing was written and its history is unchanged |

`NotFound` says nothing about whether a `GrantId` is free. The insert reads the record row first, so a create naming
a record that is not in the owner scope selects nothing and reports `NotFound` before the primary key is ever
tested — even when that `GrantId` is already stored. Generate a fresh ID per attempt.

**One active grant per recipient.** `ux_experience_grants_active_recipient` allows at most one *unrevoked* grant per
(record, recipient scope) pair, so revoking the grant an administrator knows about genuinely ends that recipient's
access instead of leaving an overlapping one alive. Re-issuing while one is active is `Conflict`; once it is
revoked, the same recipient can be granted access again. Different recipients are independent of each other.

**Null optional recipient fields are exact, not "one sibling team".** Scope matching is exact everywhere, so a
recipient of `(tenant, application, project, null, null, null)` permits exactly the requests whose scope has all
three optional fields null — the project-level scope, which is usually broader than intended. Name every optional
field the recipient actually uses.

`ListAsync` returns every grant over a record, revoked and expired ones included, oldest first, bounded by its
`limit` (1-500, default 100) — from the **owner** scope only. It is driven from the record, so "this record has no
grants" (`Found`, empty) and "there is no such record here" (`NotFound`) are different answers. A recipient cannot
enumerate the grants over a record it can read, any more than it can issue one.

`GetHistoryAsync` reads one grant's audit trail: the grant as it stands now plus every `Issued`/`Revoked` event,
oldest first, each carrying the administrator, when the host established that administrator's authority, both
scopes, the reason, and the expiry at the time. It mirrors `IExperienceRecordStore.GetHistoryAsync` and is likewise
owner-scope only. **It is an administration trail, not an access log:** reads made through a grant are not recorded
anywhere, so it answers "who permitted this?" and never "who read it?".

A grant never changes the record it names: no status, confidence, counter, revision, or timestamp moves on this
path, and nothing is promoted.

**A borrowed record says so.** A read widened by a grant comes back with `SharedByGrant` set — on
`ExperienceRecordGetResult` and on every `ExperienceCandidate` — because this adapter is the only layer that knows.
Core passes it through on `RankedExperience`, and the MAF provider surfaces it to the host's risk policy and labels
the injected block. Consumers keep a strict "this is my own record" check for anything not flagged, so a source that
returns a foreign record without declaring a grant is still refused downstream.

## Schema

The schema lives in the embedded scripts under `Migrations/`.

`0001_create_experience_records.sql` creates the `agent_experience` schema and the `experience_records` table:

- Scope, task, status, confidence, counter, revision, and timestamp columns, with `CHECK` constraints for non-blank
  scope and value ranges.
- A JSONB `payload` column for attempts, outcome, evidence, reflection, environment, and provenance.
- A `payload_version` column. This adapter owns versioning, so the domain types carry no version field.
- An index on `(tenant_id, application_id, project_id)`.

`0002_create_lifecycle_events.sql` adds the append-only `lifecycle_events` table:

- `event_id` as the primary key (the commit's idempotency key), the record ID, the same scope columns as
  `experience_records`, prior/current status, reason, producer, `occurred_at`/`recorded_at`, and
  `expected_revision`/`applied_revision`.
- `CHECK` constraints mirroring `0001` (non-empty IDs, non-blank scope, reason and producer, non-negative revision)
  plus `applied_revision = expected_revision + 1`, so a row written outside this store cannot desynchronize the log
  from the projection.
- A **unique** index on `(experience_id, applied_revision)`, so exactly one event can ever claim a given revision of
  a record and the log cannot desynchronize from the projection. Two commits racing from the same revision collide
  here; the loser is reported as `StaleRevision`.
- Deliberately no foreign key to `experience_records`: a commit for a record outside the request scope is rolled
  back by the revision-checked projection update, and a foreign-key violation would report that expected condition
  as an infrastructure failure instead.

`0003_add_experience_search.sql` makes those records searchable by text:

- A `search_vector` column, `GENERATED ALWAYS AS ... STORED` over `task_id`, the payload's `taskSummary`, and the
  payload's `reflection.lesson`, analyzed with the `english` configuration. Generated, not a trigger and not a column
  the store writes: it is derived from state that already exists, so it can never disagree with the record it indexes
  and no write path has to maintain it. The store's `INSERT` and its lifecycle `UPDATE` are unchanged.
- A **GIN** index on `search_vector`. The vector is read far more often than written — a record's text never changes
  after it is created, only its status, revision, and `updated_at` do — so GIN's faster `@@` lookups are the right
  trade.
- A composite index on `(tenant_id, application_id, project_id, status, reuse_confidence)`, so a search decides scope,
  status, and the confidence floor from an index rather than scanning foreign scopes. It covers only the three
  *required* scope columns: `team_id`, `agent_id`, and `user_id` are matched with `IS NOT DISTINCT FROM`, which is
  not an indexable btree operator, so including them would not help. A deployment that scopes records by team, agent,
  or user still scans its whole project and filters those three in memory; if that matters at your row counts, add
  your own partial or expression index.
- `0001`'s index on `(tenant_id, application_id, project_id)` is now a prefix of that composite and therefore
  redundant, but it is deliberately left in place: scripts are append-only, and dropping an index `0001` created
  would rewrite history for every database that already applied it. The cost is one extra index maintained on write.
- The concatenated text is bounded with `left(..., 100000)` before it is analyzed. A `tsvector` may not exceed 1 MB,
  and in a *generated* column exceeding it is not a search failure but a failed `INSERT` — and a failed migration on
  a table that already holds such a row. The bound only ever truncates text that would have broken the write.

Adding the generated column rewrites the table, so on a large existing deployment apply this script in a maintenance
window like any other rewriting migration.

`0005_create_experience_grants.sql` adds explicit sharing grants (see [Sharing grants](#sharing-grants)):

- `experience_grants`, keyed by `grant_id`, holding the record it names, the owner scope, the recipient scope, the
  reason, the administrator's principal ID, `issued_at`/`expires_at`, and `revoked_at`/`revocation_reason`.
- `CHECK` constraints mirroring `0002` (non-empty IDs, non-blank scope, reason and administrator) plus two that carry
  the policy itself: `recipient_tenant_id = tenant_id AND recipient_application_id = application_id AND
  recipient_project_id = project_id`, so a grant crossing those boundaries is unstorable however it is written;
  `expires_at > issued_at`, so a grant that was already expired when issued is refused by the database's own clock;
  and `experience_grants_recipient_differs`, so a grant to the scope that already owns the record — which would
  permit nothing while leaving an audit row claiming otherwise — cannot be stored.
- `experience_grant_events`, append-only, with one row per `Issued` or `Revoked` action, carrying both scopes, the
  reason, the administrator, `administrator_authorized_at` (when the host established that authority), and
  `occurred_at`/`recorded_at`. Revoking appends; nothing is ever updated or deleted.
- A **unique partial** index, `ux_experience_grants_active_recipient`, over the record and the full recipient scope
  `WHERE revoked_at IS NULL`, with `NULLS NOT DISTINCT` because a null optional scope field is an exact value here
  rather than a wildcard. It is what makes "revoke the grant you know about" actually end that recipient's access.
- `ix_experience_grants_active`, partial on the same `revoked_at IS NULL`, carrying every column the read predicate
  filters on: the record, the recipient scope in full, the expiry, and the owner scope.
- `ix_experience_grants_record` for listing a record's grants from its owner scope, and, on the event log,
  `(grant_id, recorded_at)` for one grant's trail plus `(experience_id, recorded_at)` and
  `(tenant_id, application_id, project_id, recorded_at)` for the two obvious audit questions.
- Deliberately no foreign key to `experience_records`, for the same reason as `0002`: a grant naming a record that is
  not in the owner scope is a typed `NotFound`, not an infrastructure failure.

It is numbered `0005` because `0004` belongs to the companion vectors package. The two packages apply their own
scripts but share one journal and one number sequence, so a gap in either package's list is expected.

`0006_lifecycle_supersession_and_append_only.sql` records supersession's replacement and turns append-only from a
convention into a rule:

- `lifecycle_events.replacement_experience_id`, a nullable `uuid`, with two `CHECK` constraints:
  `(replacement_experience_id IS NOT NULL) = (current_status = 'Superseded')`, so a superseding event always names a
  replacement and no other event ever does; and `replacement_experience_id <> experience_id`, the one cycle a single
  row can state on its own. It is a column rather than a payload field because the replacement chain has to be
  walked in SQL to reject a cycle, and `payload_version` is still `1` with no multi-version read path.
- Enumeration `CHECK`s on `prior_status` and `current_status`. The replacement rule compares `current_status`
  against the literal `'Superseded'`, and before this the column was constrained only to be non-blank — so a row
  storing `'superseded'` would have dodged the rule entirely.
- A partial index on `(experience_id, replacement_experience_id)` over the superseding rows, which is what the
  recursive chain walk follows.
- `BEFORE UPDATE OR DELETE` triggers on `lifecycle_events` and `experience_grant_events` that raise SQLSTATE `42501`
  on any attempt to rewrite or remove a stored event.
- A `BEFORE UPDATE` trigger on `experience_grants` that refuses to clear or change `revoked_at`, to reword a stored
  `revocation_reason`, or to move `expires_at` further out. Shortening an expiry and performing the revocation
  itself are still ordinary updates: it is the direction of travel that is constrained.

The script adds a nullable column and creates triggers, so it does not rewrite the table. It is written so a rerun
does nothing: the column is `IF NOT EXISTS`, and each constraint and trigger is created only when `pg_constraint` or
`pg_trigger` does not already have it — never dropped and recreated, which would leave a window in which the logs
were unguarded.

**Every `CHECK` is added `NOT VALID`, on purpose.** A database written through `0001`–`0005` can hold a `Superseded`
lifecycle event with no replacement, because the public port has always accepted one — Core's transition table was
never applied by the store. A plain `ADD CONSTRAINT` validates immediately, so the script would abort at startup on
exactly the deployments that most need it. `NOT VALID` still binds every new and updated row; it only skips the scan
of existing ones. `0006`'s header carries the reconciliation query and the `VALIDATE CONSTRAINT` statements to run
once it comes back empty (`VALIDATE` takes only a `SHARE UPDATE EXCLUSIVE` lock, so it blocks neither reads nor
writes).

**Read the limits of those triggers before relying on them.** They bind every writer using the application role,
including one that bypasses this library. They do not bind a superuser, and they do not bind the tables' own owner —
which the application role is, because it created them — since an owner can disable or drop a trigger and then write
freely. Row-level security and column-privilege `REVOKE` would be no stronger; neither binds an owner. This is a
guard against a bug, a careless script, or a compromised application path, not tamper-proofing against an
administrator. A deployment that needs more should ship the log off-box, or own these tables with a role the
application does not have.

`0007_confidence_evidence.sql` adds the evidence ledger and guards the columns it starts moving:

- `confidence_evidence`: `evidence_id` as the primary key, the record and the event it rode in on, the evidence's
  kind and source, the run, the verification round or the reviewer identity, `counted`, `recorded_at`, and
  `applied_revision` — plus `independence_key`, `GENERATED ALWAYS AS ... STORED` from the source, run, round, and
  reviewer. `CHECK` constraints make each source carry exactly the identifiers its key is made of: without them a
  machine row with no round (or a human row with no reviewer) would generate a `NULL` key, which a unique index
  cannot deduplicate, so every such submission would count.
- A **partial** unique index on `(experience_id, independence_key) WHERE counted`. Partial rather than plain,
  because a plain one would have to reject a later submission for a taken key — and the submission belongs in the
  audit trail whether or not it moves a counter.
- Columns on `lifecycle_events` that make an update reconstructable from the log alone: `actor`, the evidence ID,
  kind, source, run, round, reviewer, rule version and detail, and the prior and new score and counters. A `CHECK`
  using `num_nonnulls(...) IN (0, 11)` makes them all present or all absent, because a new score with no prior one
  to compare it against says nothing at all. Another refuses an event that is both a supersession and a confidence
  update.
- `enforce_record_projection` (from `0006`) replaced with a version that guards `reuse_confidence`,
  `supporting_validations`, and `contradictions`: they change only together with a revision that moved forward
  **and** only to the values a lifecycle event already recorded for exactly that revision. Advancing the revision
  alone is not enough, so `UPDATE … SET reuse_confidence = 1, revision = revision + 1` is refused like any other
  direct write, with SQLSTATE `42501`. It is why the store writes the event before the projection.
- The script's header carries a `CREATE UNIQUE INDEX CONCURRENTLY` runbook for
  `ux_lifecycle_events_confidence_evidence`: a plain build takes a `SHARE` lock and blocks appends, which is
  imperceptible on a small log and a write outage on a long one.
- `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` triggers making `confidence_evidence` append-only, reusing
  `0006`'s function. A row that could be edited or removed would free an independence key, and the same observation
  could then be counted twice.

Like `0006`, every `CHECK` it adds to the already-populated `lifecycle_events` is `NOT VALID`: the new columns are
`NULL` on existing rows and would in fact validate, but a scan of a large append-only log at startup is a cost no
deployment asked for. The script's header carries the confirmation query and the `VALIDATE CONSTRAINT` statements.
The new table's own constraints are plain — it starts empty, so there is nothing to scan. The same limits apply to
its triggers as to `0006`'s: read them above before relying on them.

**This package's schema stops there, and that is deliberate.** The derived embedding schema — the `vector`
extension and the `experience_embeddings` table — belongs to the companion package
[`AgentExperience.Storage.Postgres.Vectors`](../AgentExperience.Storage.Postgres.Vectors/README.md) and is applied
by *its* migrator, `ExperienceVectorSchemaMigrator.MigrateAsync`. `CREATE EXTENSION vector` needs a superuser,
because pgvector is not a trusted extension; putting it in this script list would make that privilege a startup
requirement for every host, including text-only ones that never enable the vector channel. Nothing here creates an
extension, and nothing here reads or writes the embedding table.

### Applying it

Call `ExperienceSchemaMigrator.MigrateAsync` explicitly at startup, before using the store. The store never migrates
on its own, and nothing migrates on construction.

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);

var migration = await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken);
// migration.AppliedScripts lists what this call applied; it is empty when the database was already current.
```

- **Journaled.** Applied scripts are recorded in `agent_experience.schema_versions` (created by the runner), so a
  rerun applies nothing. A database whose `0001` was applied by hand is journaled on the next run without losing
  rows, because `0001` is idempotent.
- **One transaction per script.** A failing script rolls back its own transaction; scripts applied before it stay
  applied and journaled.
- **Serialized across processes.** The whole run holds a PostgreSQL session advisory lock on its own connection, so
  two hosts starting at once cannot apply the same script twice. The lock is always released.
- **Permissions.** The migrating role needs `CREATE` on the database (for the `agent_experience` schema) and on that
  schema (for its tables), and has to *own* `lifecycle_events`, `experience_grants`, and `experience_grant_events`
  to create `0006`'s triggers and functions on them — which it does when it created them. It does **not** need to be
  a superuser: no script here creates an extension. The store itself only needs `SELECT`, `INSERT`, and `UPDATE` on
  `agent_experience.experience_records` and `SELECT` and `INSERT` on `agent_experience.lifecycle_events`; the
  candidate source needs only `SELECT` on `agent_experience.experience_records`. To honour sharing grants, both also
  need `SELECT` on `agent_experience.experience_grants` -- optional, because a role without it falls back to the
  exact-scope predicate (see [Sharing grants](#sharing-grants)). Administering grants additionally needs `INSERT`
  and `UPDATE` on `agent_experience.experience_grants` and `INSERT` on `agent_experience.experience_grant_events`.
- **Connections.** The data source must allow at least two concurrent connections: one for the advisory lock and one
  for the scripts. A multiplexing data source (`NpgsqlDataSourceBuilder.EnableMultiplexing`) cannot hold a session
  advisory lock, because its commands do not stay on one physical connection, so it is not supported for migration.
  Build a non-multiplexing data source for the `MigrateAsync` call.
- **Timeouts.** The wait for the advisory lock is deliberately unbounded, so a run can queue behind another host for
  as long as the caller allows; the caller's `CancellationToken` is the only bound on it. Each *script*, by contrast,
  runs under Npgsql's ordinary command timeout (30 seconds by default), so a single script that takes longer fails
  with a timeout. Raise `Command Timeout` on the connection string if a future script needs longer.

| Situation | Result |
| --- | --- |
| Nothing pending | returns an empty `AppliedScripts` |
| A script fails | throws `ExperienceStoreException` naming the failed script (no SQL text or row data), with the original failure as `InnerException` |
| Database unreachable | throws `ExperienceStoreException` |
| Caller cancellation (before the call, or while waiting for the lock) | throws `OperationCanceledException`, unwrapped; no lock left held |
| Caller cancellation once scripts are running | ignored: DbUp's upgrade has no cancellation point, so the run finishes and returns normally |

Scripts are selected only from this package's embedded `Migrations/*.sql` resources, in name order. DbUp variable
substitution is off, so `$body$` and `$1` in a script are left alone, and the runner does not log.

`PostgresExperienceRecordSchema.ScriptNames` and `GetScript` remain available for reading a script's SQL (for review
or for applying it through your own change-management tooling), but the migrator is the supported way to apply it.

### Script naming and ordering

Scripts are **append-only**. Each is named with a zero-padded numeric prefix (`0001_`, `0002_`, ...) and a short
description, and they run in ordinal name order. The journal records a script by name, so **a script that has been
journaled anywhere must never be edited or renamed**: databases that already applied it would silently keep the old
definition, and a rename would reapply it. Change the schema by adding the next-numbered script instead.

## Data semantics

- **One write path per change.** Each create is a single `INSERT`. The only update is a lifecycle commit, which is
  always paired with its event in one transaction (see above). Nothing deletes a record or an event.
- **UTC timestamps.** Every timestamp is stored and returned in UTC. `CreatedAt`, `UpdatedAt`, and a lifecycle
  event's `OccurredAt` are columns, and PostgreSQL keeps microsecond precision, so sub-microsecond ticks are
  truncated on write. Nested timestamps are stored in the payload at full precision.
- **Tool-call arguments** are stored as JSON and read back normalized to `string`, `bool`, `long` (integers that
  fit), `double`, `null`, `Dictionary<string, object?>`, or `List<object?>`. Dictionary key order is not preserved.
  Whole-number doubles (for example `1.0`) are written as JSON integers, so they read back as `long`. Values that cannot be serialized to JSON (for
  example `NaN`, infinities, or cyclic graphs) make the create `Invalid`.
- **Query order** is newest `CreatedAt` first, then `ExperienceId` in PostgreSQL `uuid` byte order, which differs
  from .NET `Guid` comparison. `Limit` must be from 1 to 500 (default 50).
  `Statuses` is either null (all statuses) or a non-empty list.
- **Search order** is descending `ts_rank_cd` relevance, then `ExperienceId` in PostgreSQL `uuid` byte order. `Limit`
  must be from 1 to 200 (default 50), and `EligibleStatuses` must be non-empty — an empty set is `Invalid` rather
  than widened to "every status", so a caller can never accidentally ask for records it considers ineligible.
- PostgreSQL cannot store the NUL character (U+0000) in `text` or `jsonb`, so a record or scope containing it is
  `Invalid` and never reaches the database.
