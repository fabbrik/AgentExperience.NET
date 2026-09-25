# AgentExperience.Storage.Postgres

> **Preview — not production ready.** This is a `0.1.0-preview` package. Public APIs may change between previews,
> and the [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table in the repository README
> lists every unresolved item. Any unresolved item blocks a production-readiness claim.

Stores AgentExperience.NET Experience Records in PostgreSQL through the `IExperienceRecordStore` port, searches
them by task text through the `IExperienceCandidateSource` port, administers explicit sharing grants through the
`IExperienceGrantStore` port, and records reuse feedback through the `IExperienceReuseFeedbackStore` port, using
plain Npgsql.

Requires `Npgsql` **10.0.3**, `dbup-postgresql` **7.0.1**, `dbup-core` **6.1.1**, and
`Microsoft.Extensions.DependencyInjection.Abstractions` **10.0.12**, or any later release in the same major. Each is a
floor: CI tests the floor itself and the newest release in its major (the DI package is abstractions only — no
container, no hosting — and exists for this package's own registration extension). Built for `net8.0`, `net9.0` and
`net10.0`; on `net8.0` only it also requires `System.Text.Json` **10.0.12** or later in its major, for the strict
payload decoding the .NET 8 shared framework cannot do.
Integration tests run against PostgreSQL 15, 16, 17 and 18 (`pgvector/pgvector:pg{N}`, and stock `postgres:{N}` for
the text-only schema) through `Testcontainers.PostgreSql` 4.15.0; PostgreSQL 14 is not supported, because
`0005_create_experience_grants` uses PostgreSQL 15 syntax. See [the supported matrix](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/compatibility-evidence.md#supported-matrix). This
package does not use EF Core, Dapper, Pgvector, or the pgvector extension.

## Usage

```csharp
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;

// Two roles: see "Deploying with two roles" below. The owner migrates; the application role is what the stores use.
await using (var ownerDataSource = NpgsqlDataSource.Create(ownerConnectionString))
{
    // On every deploy, before the store is used. See "Schema" below.
    await ExperienceSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);
    await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
        ownerDataSource,
        new ExperienceApplicationRoleOptions("agent_experience_app") { AllowErasure = true },
        cancellationToken);
}

await using var dataSource = NpgsqlDataSource.Create(applicationConnectionString);

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
services.AddAgentExperiencePostgresReuseFeedbackStore(); // or ...ReuseFeedbackStore(dataSource) -- only if you record feedback
services.AddAgentExperiencePostgresGrantAccessLog(      // or ...GrantAccessLog(dataSource, ...) -- only if you want
    onNotRecorded: failure => logger.LogError(          //    a trail of who READ shared records. Off unless wired.
        failure.Failure, "grant access row not written"));
services.AddAgentExperiencePostgresEncryption(keyStore); // optional: crypto-shredding for every component above

// Core's own extensions then supply capture, reflection, lifecycle, finalization, and retrieval over them.
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
services.AddAgentExperienceRetrieval();
services.AddAgentExperienceReuseFeedback();             // needs the feedback ledger above
```

The ports are registered independently: a host that only writes experience never has to register the search, one
that only reads never has to register the store, and one that never shares a record across scopes never has to
register the grant store — the reads that honour grants do so in SQL either way — and one that never records
reuse feedback never has to register its ledger. Every registration is `TryAdd`-based, so a host that has already
registered its own `IExperienceRecordStore`, `IExperienceCandidateSource`, `IExperienceGrantStore`, or
`IExperienceReuseFeedbackStore` keeps it.
It does **not** apply the schema: call `ExperienceSchemaMigrator.MigrateAsync` and then
`ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` as the owner role on every deploy (see
[Deploying with two roles](#deploying-with-two-roles) and [Schema](#schema)).

Records are normally written by Core's `ExperienceFinalizationService`, which creates the record and commits its
initial lifecycle event; `CreateAsync` and `CommitLifecycleEventAsync` stay available for hosts that orchestrate that
themselves.

## Deploying with two roles

This is the supported deployment, and the one every guarantee in this README about append-only history and erasure
is stated for. It takes two PostgreSQL roles:

- an **owner** role, which owns the `agent_experience` schema and everything in it and runs the migrators. The
  application never holds its credentials;
- an **application** role, which is what every store, search and ledger in this package connects as. It owns
  nothing and holds exactly the privileges the stores need.

Why it matters: PostgreSQL's triggers do not bind a table's owner, who can `ALTER TABLE … DISABLE TRIGGER`, drop a
trigger or replace a function and then write freely; and the purge markers are custom settings any session can set.
If the application runs as the owner — which it does whenever it runs the migrator itself — the append-only guards
and the single erasure path are only as strong as the application's own code. With two roles they bind the
application's own credentials.

### Creating the roles

Once, as a superuser (rename to taste; the passwords are placeholders):

```sql
-- The owner: runs the migrators. Never configured in the application.
CREATE ROLE agent_experience_owner LOGIN PASSWORD 'change-me';
-- The application: what the stores connect as. Owns nothing.
CREATE ROLE agent_experience_app LOGIN PASSWORD 'change-me-too';

-- A database the owner owns. On an existing database, GRANT CREATE ON DATABASE ... TO agent_experience_owner
-- instead -- and the database's own owner must not be the application role either: a database owner can drop it.
CREATE DATABASE agent_experience OWNER agent_experience_owner;

-- 0010 and 0012 create functions whose SET clause names the two purge markers, and PostgreSQL 15+ lets a
-- non-superuser name a custom setting there only when granted SET on it. This is the one superuser-only step
-- the base schema has.
GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized
    TO agent_experience_owner;

-- Only with the vector channel: pgvector is not a trusted extension, so a superuser creates it, in that database.
-- The vectors migrator's own CREATE EXTENSION IF NOT EXISTS is then a no-op.
\c agent_experience
CREATE EXTENSION IF NOT EXISTS vector;
```

Do not make the application role a member of the owner role, of `pg_write_all_data`, of a superuser role, of
`pg_write_server_files`, `pg_read_server_files` or `pg_execute_server_program`, or of any role that owns something in
the schema, and do not grant it `SET` on `session_replication_role`, or (PostgreSQL 17 and later) membership in
`pg_maintain` or the `MAINTAIN` privilege on any table in the schema. The call below refuses each of these or fails
its verification. Do not give it `CREATEROLE` either: that is not checked, and a role that can create and grant roles
is an administrator.

### Applying the privileges, on every deploy

As the owner, after `MigrateAsync` — and after `ExperienceVectorSchemaMigrator.MigrateAsync`, when the host uses the
vector channel, so the embedding table is covered:

```csharp
await using var owner = NpgsqlDataSource.Create(ownerConnectionString);

await ExperienceSchemaMigrator.MigrateAsync(owner, cancellationToken);
await ExperienceVectorSchemaMigrator.MigrateAsync(owner, cancellationToken);   // only with the vector channel
await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
    owner,
    new ExperienceApplicationRoleOptions("agent_experience_app")
    {
        AllowErasure = true,          // DeleteAsync, SweepExpiredAsync, PurgeExpiredAsync
        AllowAccessLogPurge = false,  // PurgeOlderThanAsync on the access log
        AllowSealing = false,         // SealPlaintextRecordsAsync, the crypto-shredding upgrade job
    },
    cancellationToken);
```

In one transaction, under the migrator's advisory lock, the call revokes every privilege the application role holds
on the schema and on every table, sequence and function in it, then grants exactly this:

| Object | The application role gets |
| --- | --- |
| schema `agent_experience` | `USAGE` — never `CREATE` |
| `experience_records` | `SELECT`, `INSERT`, and `UPDATE` on `status`, `revision`, `updated_at`, `reuse_confidence`, `supporting_validations`, `contradictions` only |
| `experience_grants` | `SELECT`, `INSERT`, and `UPDATE` on `revoked_at`, `revocation_reason` only |
| `lifecycle_events`, `experience_grant_events`, `confidence_evidence`, `reuse_feedback`, `reuse_feedback_exposures`, `experience_grant_access` | `SELECT`, `INSERT` |
| `experience_embeddings` (when the vectors package created it) | `SELECT`, `INSERT`, `DELETE`, and `UPDATE` on `model_id`, `dimension`, `content_hash`, `source_revision`, `embedding`, `updated_at` only — derived, rebuildable data, not a ledger, but never its record ID or scope columns |
| `purge_experience_record`, `purge_expired_grants` | `EXECUTE` only with `AllowErasure` |
| `purge_grant_access` | `EXECUTE` only with `AllowAccessLogPurge` |
| `seal_experience_record` (`0016`) | `EXECUTE` only with `AllowSealing` |
| `schema_versions` (the journal), its sequence, and anything else | nothing |

Then it checks the role's **effective** privileges — which also see grants to `PUBLIC`, grants made by another
grantor, memberships and predefined roles — and throws `ExperienceStoreException`, rolling everything back, on any
difference: `CREATE` on the schema, a `DELETE` or `TRUNCATE` on a ledger, an `UPDATE` on any other column, a
`TRIGGER` or `REFERENCES` privilege, on PostgreSQL 17 and later `MAINTAIN` on a table (no trigger fires on
`LOCK TABLE`, `CLUSTER`, `REINDEX` or `VACUUM`), any privilege held `WITH GRANT OPTION`, a sequence, a `SECURITY DEFINER`
function in the schema the role could execute without an opt-in, or a missing grant. What must be absent is checked
on every role the application role is a member of, not only on the ones it inherits from, so a privilege one
`SET ROLE` away (a `NOINHERIT` role, or on PostgreSQL 16 and later a membership granted `WITH INHERIT FALSE`) is
caught too.

Before any of that it refuses a role that does not exist, an unmigrated schema, a superuser, the role running the
call, any role that is — or is a member of — the owner of the database, the schema, or anything in it, and any role
that is or reaches a superuser, one of the server-file roles, `pg_maintain` (PostgreSQL 17 and later), or `SET` on
`session_replication_role`. The message
names the violation; nothing is changed.

**Why it is an API and not a migration.** A migration runs once and is journaled, so it could never re-grant on an
object a later migration adds, and the role name is host configuration. Revoke-everything-then-grant-the-list is
idempotent, so running it on every deploy is what keeps the set exact: an object a later migration adds gets nothing
until this list names it, and a stray `GRANT ALL` an operator made is taken away on the next deploy. Between a
later script committing and this call running, a host's own `ALTER DEFAULT PRIVILEGES` for the application role
would be in effect — do not configure one.

**Row locks and column grants.** The store takes `FOR UPDATE` and `FOR KEY SHARE` locks on `experience_records`,
which PostgreSQL permits only to a role holding `UPDATE` on at least one column; the projection columns are that
column. The column list is also what closes the last hand-written erasure path: `0010`'s projection guard admits
one marked `UPDATE`, a live record into its tombstone, and a role that could run it by hand could erase a record's
payload while leaving every ledger row that names it. The application role cannot write `deleted_at`, `payload`,
`task_id`, `created_at` or `source_run_id` at all.

### What this binds, and what it cannot

With the two roles, **the application role** — and so a bug, a careless script, or a compromised application
holding its credentials:

- cannot `ALTER TABLE`, `DISABLE TRIGGER`, `DROP TRIGGER`, drop a constraint, or replace or re-pin a guard
  function, because it owns nothing; and cannot create anything in the schema, because the verification fails on
  `CREATE` from any source;
- cannot `UPDATE`, `DELETE` or `TRUNCATE` any ledger, nor `DELETE` or `TRUNCATE` a record or a grant, **whatever
  purge marker it sets by hand** — the privilege system refuses the statement (`42501`, `permission denied for
  table …`) before any trigger is consulted, so the marker gains it nothing;
- cannot write a tombstone, or any column of a record other than the six the store moves, or of a grant other than
  its revocation;
- cannot call a purge function unless the host opted in — and when it did, what it gets is exactly that function's
  one bounded, scope-checked path.

**What it cannot bind, stated precisely:** the owner role and any superuser. The owner can disable or drop a trigger,
replace a function, or grant itself anything; a superuser bypasses privilege checks altogether. That is inherent in
PostgreSQL, not a gap in this schema: keep the owner's credentials out of the application and its configuration,
and treat them as the administrator credentials they are. The application role can also still `INSERT` a fabricated
row into a ledger — append-only is not authenticity — and anyone who can reach backups, replicas or the data
directory is out of reach entirely (see [the honesty statement](#the-honesty-statement-and-the-limits)).

**A single-role deployment** — the application role runs the migrator and so owns the tables — still works, and is
fine for local development and tests. It gets none of the above: the application is the owner, so every guard is
only as strong as its code. It is not a supported production deployment.

### Upgrading an existing single-role database

The role the application used to migrate as owns the database, the schema and every object in it. Keep it as the
application role — its credentials do not change — and move ownership to a new owner.

**Stop the application first, and run everything below through the next deploy in one maintenance window.** The
moment ownership moves, the old role loses the implicit rights it had as owner: it cannot read or write a single
table until `ApplyApplicationRolePrivilegesAsync` grants the manifest, and an old build that still runs the migrator
with its credentials fails. As a superuser, connected to that database (the transfer also takes a brief
`ACCESS EXCLUSIVE` lock on each table):

```sql
CREATE ROLE agent_experience_owner LOGIN PASSWORD 'change-me';
GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized
    TO agent_experience_owner;

DO $transfer$
DECLARE
    obj record;
BEGIN
    EXECUTE format('ALTER DATABASE %I OWNER TO %I', current_database(), 'agent_experience_owner');
    EXECUTE format('ALTER SCHEMA agent_experience OWNER TO %I', 'agent_experience_owner');
    FOR obj IN
        SELECT c.oid::regclass AS name, c.relkind
        FROM pg_class c
        WHERE c.relnamespace = 'agent_experience'::regnamespace
          AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
          AND NOT EXISTS (
              SELECT 1 FROM pg_depend d
              WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('a', 'i'))
    LOOP
        EXECUTE format(
            CASE WHEN obj.relkind = 'S' THEN 'ALTER SEQUENCE %s OWNER TO %I' ELSE 'ALTER TABLE %s OWNER TO %I' END,
            obj.name, 'agent_experience_owner');
    END LOOP;
    FOR obj IN SELECT p.oid::regprocedure AS name FROM pg_proc p WHERE p.pronamespace = 'agent_experience'::regnamespace
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO %I', obj.name, 'agent_experience_owner');
    END LOOP;
END
$transfer$;
```

Sequences owned by a column, and indexes, move with their table. The purge functions stay `SECURITY DEFINER` and now
run as the new owner; the `EXECUTE` that `0010` and `0012` granted to the old owner moves to the new owner with the
ownership, so the application role has none until the next step grants it. Then, still inside the window, run the
migrator with the owner's credentials and the call above after it, naming the old role as the application role, and
only then start the application again. From that
deploy on, the migrator never runs with the application's credentials again. The package's tests run exactly this
SQL against a database migrated by its application role and prove the old role can no longer disable a guard.

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
| `UPDATE` or `DELETE` against a stored event row, or a grant revocation cleared, expiry extended, or disclosure level changed | rejected by the database with SQLSTATE `42501`, surfaced as `ExperienceStoreException` |
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
  **What they do not bind:** anyone who can `ALTER TABLE` these tables — a superuser, or the tables' own owner —
  because an owner can `DISABLE TRIGGER`, drop the trigger, or drop a constraint first. In the supported
  [two-role deployment](#deploying-with-two-roles) the application role is neither, and it holds no `UPDATE`,
  `DELETE` or `TRUNCATE` on the logs either, so it is refused before a trigger is asked. Nor do they say anything
  about backups, a restore that recreates the tables without `0006`, or filesystem access. Treat this as a guard
  against a bug, a careless script, a compromised application path, or a replication apply — not as tamper-proofing
  against an administrator holding the owner's or a superuser's credentials. A deployment that needs that should
  ship the log off-box.
- **Purging is the one exception, and it never disables anything.** The logs carry free-text `reason` and
  `producer` a host may have filled with personal data, so `0010` replaced `0006`'s manual
  `DISABLE TRIGGER` runbook with a single purge function whose transaction-scoped marker the guards themselves
  recognise: `UPDATE` and `TRUNCATE` stay refused in every session, `DELETE` is admitted only inside that one
  function, and no other connection's window is widened for an instant. What that does and does not buy is stated
  in full under [Deleting and expiring data](#deleting-and-expiring-data) — it is a single code path, not a
  privilege boundary.

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
  the first submission for a key and no other. Generating it here means no writer picks the key *string*; the index
  itself does **not** stop a writer inventing the key's inputs, and there is no foreign key behind `run_id` or
  `verification_round_id` because nothing in this schema knows what a run or a closed round is. Since story 6.6 Core
  checks the inputs before anything reaches this store: the run must be finalized into a record in the evidence's
  scope (or held by the capture service), a machine round must be the `closedRoundId` that record's payload carries,
  and human evidence must present a library-minted assessment token — see the
  [main README](../../README.md#updating-confidence-from-evidence). A writer that bypasses Core is still unchecked
  here, exactly as it always was. Core computes the same string in `ConfidenceIndependenceKey`, and an integration
  test pins the two against each other.
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
  writes: no counters, no status, no revision, no `updated_at`, and no lifecycle event. Refreshing `updated_at`
  would keep a record permanently recent and permanently un-expired under replay; writing the status would contest
  it on evidence that was not counted; and an event is impossible as well as unwanted, since it must claim
  `expected_revision + 1`. `result.AppliedConfidence` reports what was stored and its `Counted` says which happened,
  while `result.Revision` and `result.CurrentStatus` report the record the call left untouched.

`EvidenceId` is a second idempotency key alongside `EventId`. Resubmitting it with identical content — the same
record, kind, source, run and round or reviewer, assessment, rule version, and detail — reports the original outcome and the
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

## Reuse feedback

`PostgresExperienceReuseFeedbackStore` answers one question — *what did a run that saw these records actually come
to?* — and writes it down. It is a separate port from the record store on purpose: recording feedback is opt-in, and
a host that never does it needs neither table.

It runs in the same order as every other operation here: validate the submission, check its scope against the
host-established `AuthorizationContext`, and only then open a connection. A scope outside the context is `Denied`
before any connection opens.

- **The submission and its exposures commit together.** One transaction on one connection inserts the
  `reuse_feedback` row and every `reuse_feedback_exposures` row, so a run's feedback is never half recorded. Core
  writes this ledger **before** submitting any confidence evidence, so what the run saw is durable even if every
  score submission then fails.
- **This store decides nothing about benefit.** It writes the attribution decision Core made. It never promotes
  `Unknown`, never derives an evidence ID, and never reads `claimed_benefit` as attribution. The database enforces
  the same rule from its own side, so a writer bypassing this package is refused too.
- **A human assessment is the weakest trust boundary here.** Nothing in this schema or in Core can check that a
  human made one. `reviewer_identity` is the host's `AuthorizationContext.PrincipalId` rather than anything on the
  submission. Since story 6.6, `assessment_id` is the ID of an assessment token Core verified (minted under the
  host's key for this scope, run, reviewer, direction and records) before the attribution was recorded, and `run_id`
  was checked to be a run the library knows in the scope, so agent output can no longer mint either; the evidence
  ledger then spends the token once per record. A host that opted out of verification is back to 0008's header:
  the caller supplies both, and one that lets agent output populate them hands the agent a fresh independence key
  on every call.
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

**What a grant permits.** Reading one named record, and only reading: `GetAsync` (and `GetManyAsync`, which is
`GetAsync` for several named records in one statement), the text channel, and the vector channel — and therefore
injection, which re-reads through `GetManyAsync`. A granted record comes back exactly as its
owner sees it, still carrying the owner's `Scope`. `CreateAsync`, `CommitLifecycleEventAsync`, `GetHistoryAsync`,
`QueryAsync`'s enumeration, and issuing further grants all keep the exact-scope predicate, so none of them is ever
widened by a grant.

**The batched read is the single read, widened only in its ID match.** `GetManyAsync` (story 5.6, KL-1) is built
from the same select list, the same lateral join that names the permitting grant and its disclosure level, and the
same readability predicate as `GetAsync`'s statement — the text is shared, not retyped, and a test asserts that the
batched statement is the single one with its ID match replaced and nothing else — with `experience_id = @experience_id` replaced by `experience_id = ANY(@experience_ids)`. Each row is
answered by the same code as a single read: a tombstone is `Deleted` to the scope that owned it and `NotFound` to
anyone who reached it through a grant, a missing ID is `NotFound`, and an empty GUID is `Invalid` at its own position
without reaching the database. The grant fallback narrows it exactly as it narrows `GetAsync`. The batch is read by
one statement, where the per-record loop read each record at its own instant; grant expiry is still decided per row
by `clock_timestamp()`, exactly as in the single read. A scope outside the authorization refuses the whole request as
`Denied`, before any position is looked at. Under a failing ledger the host's `OnNotRecorded` callback is called once
for the batch, carrying every row it could not write, where N single reads called it N times.

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
owner-scope only. **It is an administration trail:** it answers "who permitted this?".
"Who read it?" is the separate, optional access log below.

A grant never changes the record it names: no status, confidence, counter, revision, or timestamp moves on this
path, and nothing is promoted.

**A borrowed record says so, and says which grant.** A read widened by a grant comes back with `SharedByGrant` set
— on `ExperienceRecordGetResult` and on every `ExperienceCandidate` — because this adapter is the only layer that
knows. All three read paths additionally return `PermittingGrantId`: *which* grant permitted it, produced by the
same `LEFT JOIN LATERAL` that decided readability, so it can never name a grant that did not permit the read. Where two
active grants would both admit it, the join orders by `grant_id` and takes one, so the answer is stable per read
rather than whatever the planner returned first. Core passes both through on `RankedExperience`, and the MAF
provider surfaces them to the host's risk policy and labels the injected block. Consumers keep a strict "this is my
own record" check for anything not flagged, so a source that returns a foreign record without declaring a grant is
still refused downstream.

**A grant says how much of the record a model may see.** `ExperienceGrantRequest.Disclosure` is
`ExperienceGrantDisclosure.LessonOnly` by default, or `LessonAndApproach`. It governs only the injected block: under
`LessonOnly` the MAF adapter omits the `Approach:` line — and only that line; the reflection's prose is rendered
unfiltered — and says the grant withholds it; the `ExperienceRecord` this store returns is complete either way. The level is read from the same lateral join
that names the permitting grant and comes back on `ExperienceRecordGetResult.GrantDisclosure` (`null` for the
reader's own record); the search channels read it too, but put it only on the access row, never on an
`ExperienceCandidate`. It is stored on the grant, copied onto its `Issued` and `Revoked` events, and **cannot be
changed**: the database refuses an `UPDATE` of it, so to widen or narrow it, revoke the grant and issue a new one —
the one-active-grant index allows that once the old one is revoked, so the recipient has no access in the gap. A level
the enum does not define is `Invalid` on `Disclosure`, and nothing is written. Upgrading to `0011` makes every existing
grant `LessonOnly`.

### Bounding a grant's lifetime

`PostgresExperienceGrantPolicy` is the policy this store administers grants under. Its one rule today is
`MaxLifetime`, the longest a *new* grant may be issued for — 90 days by default:

```csharp
IExperienceGrantStore grants = new PostgresExperienceGrantStore(
    dataSource,
    new PostgresExperienceGrantPolicy(TimeSpan.FromDays(30)));
// or services.AddAgentExperiencePostgresGrantStore(new PostgresExperienceGrantPolicy(TimeSpan.FromDays(30)));
```

An expiry further ahead than the maximum is `Invalid` on `ExpiresAt` with nothing written; one exactly at the
maximum is accepted. There is no unbounded option — `TimeSpan.Zero`, `Timeout.InfiniteTimeSpan`, and anything past
3650 days all throw at construction — so `DateTimeOffset.MaxValue`, which used to buy a permanent grant, is now
refused like any other over-long expiry. A bound that would run off the end of `DateTimeOffset` is treated as
*exceeded* rather than saturated, because saturating would make the comparison vacuously true and admit exactly
the value the bound exists to refuse.

**It is checked twice, against two clocks, on purpose.** The client check produces the message naming the bound,
but it measures from the caller's clock. The insert statement carries the same bound as
`expires_at <= now() + @max_lifetime`, measured from the clock that stamps `issued_at`, so a caller whose clock
runs behind cannot buy itself a longer grant; it is reported on the same field, and nothing is written either way.

The bound binds a grant when it is **created**, and never afterwards. Raising the maximum does not extend a grant
already issued; lowering it does not shorten one. End an over-long grant by revoking it. The existing rule that an
expiry may only ever shrink (`0006`'s `experience_grants_monotonic` trigger) is unchanged.

Underneath the policy, `0009` adds `experience_grants_lifetime_bounded`:
`CHECK (revoked_at IS NOT NULL OR expires_at <= issued_at + interval '10 years')`. A `CHECK` cannot express
"whatever interval this deployment configured", so the two do different jobs: the policy is the deployment's rule,
the constraint is a fixed, generous floor that binds even a writer bypassing this library. It is added `NOT VALID`;
see the script's header for the confirm-then-`VALIDATE` step and what to do about a grant already issued beyond it.

The **revoked exemption matters**: PostgreSQL re-checks a `CHECK` on every `UPDATE`, so without it, revoking a
grant stored before `0009` with an unbounded expiry — the one remedy the runbook prescribes — would be refused by
the very constraint that made it a problem, leaving it permanent forever. Because the ceiling is relative to
`issued_at`, `0009` also adds a `BEFORE INSERT` trigger refusing a grant dated in the future: "ten years from 2126"
outlives everyone the trail is for, and a `CHECK` cannot call `now()`.

### Recording who read a shared record

The grant trail answers "who permitted this?". `IExperienceGrantAccessLog` answers "who read it?", and it is a
separate, optional ledger — a deployment can keep grants without paying for access rows:

```csharp
services.AddAgentExperiencePostgresGrantAccessLog(
    onNotRecorded: failure => logger.LogError(failure.Failure, "grant access row not written"),
    mode: ExperienceGrantAuditingMode.BestEffort);   // or Required

// Outside a container:
var store = new PostgresExperienceRecordStore(
    dataSource,
    onGrantsUnavailable: null,
    auditing: new ExperienceGrantAuditing(
        new PostgresExperienceGrantAccessLog(dataSource),
        onNotRecorded: failure => logger.LogError(failure.Failure, "grant access row not written"),
        ExperienceGrantAuditingMode.Required));
```

Each row names the grant, the record **and the revision that was disclosed**, the grant's disclosure level at
delivery (kept on the row after the grant itself is purged; `null` on rows written before `0011`; the level the
library applied, not proof that an `Approach:` line reached a model), the owner scope,
the recipient scope, the reading principal (the host's `AuthorizationContext.PrincipalId`, never anything a caller passed as
data), the host's correlation ID for the work that caused the read, and both `occurred_at` (the reader's clock,
which is `ExperienceGrantAuditing.Clock`) and `recorded_at` (the database's `clock_timestamp()`). The revision and
the correlation ID are on the row rather than joined in later because a record is a mutable projection and the
table is append-only: neither can ever be backfilled.

| Read | Recorded? | Why |
| --- | --- | --- |
| `GetAsync` widened by a grant | **Yes** | The record was handed to a caller who could only see it through that grant |
| `GetManyAsync` positions a grant delivered | **Yes** | One row per delivered position, exactly the row a `GetAsync` of that ID would write. The batch's rows are written in **one** statement, so under `Required` they land together or not at all: a failed append drops every grant-delivered position and leaves the owner's own positions as read |
| The MAF provider's pre-injection re-read | **Yes** | It is one `GetManyAsync`, and it is a delivery |
| A text or vector candidate a grant admitted | **Yes** | `ExperienceCandidate.Record` is the record read back *in full*, so returning one across a scope boundary is a disclosure, not a notice that something matched. A search's rows are written in **one** statement, so auditing costs one round trip per search rather than one per row |
| An owner reading its own record | **No** | No grant permitted it, so there is no access to attribute to one |
| A read that found nothing | **No** | Nothing was delivered |
| A read the caller refuses *because* a grant is what made it readable | **No** | Nothing was handed over. Declare it with `ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck)`; Core's confidence path does, because a grant never confers writing |

**The rows are never written inside the read's own statement.** That would take a write lock on every read and stop
reads running on a replica. The append is a separate statement afterwards, which is why what happens when it fails
is a policy rather than an accident:

- `BestEffort` (the default): the records are still returned and the failure goes to `onNotRecorded`, carrying
  every row that did not land. That callback is **required**, not optional — under best effort it is the only place
  a missing row is visible, and an audit that can fail silently is worse than none.
- `Required`: the read returns nothing. A `GetAsync` returns `NotFound`, which is the same answer a record no grant
  permitted would give, so failing closed tells a caller nothing it would not otherwise have; a search returns *no*
  candidates rather than the subset that needed no grant, because a partly-returned page would quietly be a
  different search than the caller asked for. The failure is still reported.
- A blank `AuthorizationContext.PrincipalId` is itself an audit failure: a row that cannot say **who** read the
  record does not answer the question the ledger exists for, so it is reported and, under `Required`, fails closed.
  Establish a principal, or do not require auditing.
- Cancellation arriving during the append is a failure like any other rather than an exception thrown over a
  completed read — the records were already read, so the mode decides whether they may be returned.
- A throwing `onNotRecorded` is swallowed: the host's own logging never changes what a read returns.

**Reading the trail.** `IExperienceGrantAccessLog.QueryAsync` answers the question the ledger exists for, without
hand-written SQL. It is owner-scope only and cursored, mirroring `IExperienceGrantStore.GetHistoryAsync`:

```csharp
var page = await accessLog.QueryAsync(
    authorization,
    new ExperienceGrantAccessQuery(ownerScope),            // or (ownerScope, recordId) for one record
    cancellationToken);
// page.Accesses — oldest first, bounded by Limit (1-500, default 100).
// page.NextCursor — pass as StartAfter for the next page; the cursor is (occurred_at, access_id), so
//                   several deliveries sharing an instant page correctly.
```

A recipient cannot enumerate who else read a record it can read, any more than it can list the grants over one.

**What turning it on costs.** Every read that discloses something across a scope boundary now does a second,
synchronous round trip on a pooled connection before it returns — one per `GetAsync`, one per `GetManyAsync` or
search however many rows it disclosed. Under `Required`, read availability becomes a function of *write* availability: if the ledger is
unreachable, grant-widened reads return nothing. That is the mode's promise, not a bug, but it is a real coupling.
The `dataSource` overloads exist largely for this: pointing `PostgresExperienceGrantAccessLog` at its own
`NpgsqlDataSource` keeps the audit writes off the read pool, so a slow ledger cannot exhaust the connections reads
depend on.

`experience_grant_access` is append-only in the database, like every other ledger here, so a delivery cannot be
edited or deleted out of the trail afterwards — except by its one retention path, `PurgeOlderThanAsync`, which never
removes a row younger than 30 days (see [Retention for the grant access log](#retention-for-the-grant-access-log)). Wire nothing and auditing is off entirely: no extra write, no extra
round trip, no extra failure mode, `0009`'s table simply stays empty, and reads behave exactly as they did before.

**Auditing binds the implementation that was registered.** Every registration here uses `TryAdd`, so a host that
registers its own `IExperienceRecordStore`, `IExperienceCandidateSource`, or `IExperienceEmbeddingIndex` *before*
calling these extensions keeps its own — and takes on the obligation to honour a configured
`ExperienceGrantAuditing` itself. Registering the access log does not make somebody else's store audit.

## Deleting and expiring data

Revocation stops reads. **Deletion removes payload.** `DeleteAsync` is the only destructive operation this library
has, and every choice in it is resolved towards "a wrong delete is refused" rather than "a right delete is
convenient".

```csharp
var store = new PostgresExperienceRecordStore(dataSource);

// Erase one record, in exactly this scope.
var deleted = await store.DeleteAsync(hostAuthorization, scope, experienceId, cancellationToken);
// deleted.Outcome is Deleted, NotFound, Invalid, or Denied. Deleting again is Deleted, and writes nothing.

// Or erase it only while it is still at the revision you read.
var guarded = await store.DeleteAsync(hostAuthorization, scope, experienceId, expectedRevision: 4, cancellationToken);
// StaleRevision, carrying the record's current revision, when it has moved on.
```

**Deletion is payload erasure plus a tombstone, never a row vanishing.** The `experience_records` row survives with
its payload emptied; everything else that named the record is removed. That is what makes the ID unusable
afterwards rather than free to be written again.

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
`search_vector` is `GENERATED ALWAYS` from `task_id` and two payload fields, so it regenerates from the
placeholder alone and the record's searchable text is gone without any separate index maintenance.

That last sentence is a property of `purge_experience_record` and of every tombstone this library made — not
something the schema can prove about a row somebody else inserted. The tombstone-shape `CHECK` constrains an
existing row's *shape*; a row INSERTed directly as a tombstone can carry any `created_at` it likes, because there
is no `UPDATE` for the projection guard to refuse. The same goes for its revision.

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
makes urgent.

**Authorization is decided once, at step 1, and every step below it follows the record's ID rather than the
caller's scope.** That is not an oversight, and it is true of *all* of steps 2–8, not only the ones without scope
columns:

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

**Two purges sharing one feedback submission cannot orphan it.** A submission may name several records; erasing
two of them at once used to leave the parent row behind with zero exposures, because each purge's "are there any
exposures left?" still saw the other's uncommitted delete. Step 3 now locks the submissions `FOR UPDATE`, in
`feedback_id` order, *before* deleting any exposure, so the second purge asks its question after the first has
committed. That row carries a run ID, a scope, an outcome, a measure and — for a human assessment — a reviewer
identity and a free-text rationale, so an orphan is not a tidiness problem.

### A tombstone is terminal

| A late… | Answer | Enforced by |
| --- | --- | --- |
| `CreateAsync` under the erased ID | `Conflict`, as for any taken ID, revealing nothing about which scope holds it | **Schema** — the primary key collides with the surviving tombstone row |
| `CommitLifecycleEventAsync` | `Deleted`. Nothing is appended | **Both** — the adapter's predicate, and `0010`'s projection guard, which refuses any `UPDATE` of a tombstone from the database's own side |
| confidence submission | `Deleted`, with no ledger row: an erased record's ID must not go back into a table the erasure emptied | Adapter |
| reuse-feedback write naming it | `Invalid`, naming the exposure by position. Only tombstones in the submission's own scope are visible to that check | Adapter |
| embedding write | `Missing`, never `Stale`: no revision of an erased record can ever be indexed | Adapter |
| grant over it | `NotFound`: there is nothing left to share | Adapter |
| any `UPDATE` of the tombstone row, marker or not | Refused, `42501` | **Schema** |
| any `DELETE` or `TRUNCATE` of the record row, marker or not | Refused, `42501` | **Schema** |
| `GetAsync` / `GetHistoryAsync` in the owning scope | `Deleted`, with no record and no events | Adapter |
| `QueryAsync`, text search, vector search | The tombstone is simply absent | Adapter |
| anything at all from another scope | `NotFound`, exactly as for an ID that never existed | Adapter |

**The distinction in that last column matters, so read it rather than the summary.** Four of the write refusals
are *adapter*-enforced: they are predicates this library puts in its own statements, and raw SQL from another
tool can still `INSERT` a lifecycle event, a confidence-evidence row, an exposure, an embedding or a grant
against a tombstoned ID. None of those tables has a foreign key to `experience_records`, deliberately (`0002`,
`0005`, `0007`, `0008`), and adding one now would rewrite four journaled tables' shapes for this one rule. What
*is* schema-enforced is the part that cannot be worked around: the ID can never be re-created, the tombstone can
never be moved, and the record row can never be removed — which together mean an ID, once spent, is spent.

**Those adapter predicates are locked, not merely read.** Every write that gates on "this record is not a
tombstone" takes `FOR KEY SHARE` on the record row in the same statement, so a writer that started before an
erasure committed is parked against the purge's own `FOR UPDATE` and re-checks when it is released, instead of
deciding against a snapshot the purge has already invalidated. Without that, a write issued a moment after a
`DeleteAsync` returned `Deleted` could still land: a stored vector derived from the erased summary and lesson, a
live 90-day grant over a spent ID, or a reviewer's identity and free-text rationale about the erased record,
permanently, in an append-only table. `FOR KEY SHARE` rather than `FOR SHARE` on purpose — it is the weakest mode
that still conflicts with the purge, and it does not block an ordinary lifecycle commit.

### Retention

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

Age is measured from `CreatedAt` on the store's own `TimeProvider`, never from `UpdatedAt`: age is how long this
library has held the data, and a record that is read, ranked, or re-scored does not thereby become younger. Each
record in a batch is erased in its own transaction, so an interrupted sweep leaves every record it reached wholly
erased and every record it did not reach wholly untouched. A non-positive age is `Invalid` — there is no retention
age that means "delete everything" — and so is a batch outside 1…500. `DeletedCount` counts only the records *this*
call erased: when two sweeps race over the same records, the one that finds a record already a tombstone does not
count it again, so their counts sum to exactly what was erased.

#### A sweep reaches one scope, or everything beneath it, and you choose which

This is the one operation here whose failure mode is **a missed retention obligation reported as success**, so it
gets its own heading rather than a clause.

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
`authorization.Permits(root)` once, before any connection opens, exactly as before. If an authorization permits the
root, every non-null bound on it equals the root's field; for a field the root leaves `null`, a non-null bound
cannot equal it, so that bound is `null` too — unrestricted. So the same authorization permits every scope beneath
the root. The converse is the protection: a caller bounded to team `t1` is `Denied` a project root outright, so a
subtree can never be used to widen what it may erase. Each candidate is also checked in process against the root
and the authorization before it is erased, and is erased through the unchanged `purge_experience_record` **in its
own exact stored scope**, one record per transaction.

**Bounded and truthful across the subtree.** One page of at most `batchSize + 1` candidates across the whole
subtree, oldest `CreatedAt` first, so `MoreRemain` means "more past the cutoff anywhere beneath the root". The page
is served by `0010`'s `ix_experience_records_live_by_age`; nothing new is indexed.

**What it still cannot do: span projects.** `Scope` has no way to say "any project", and adding one would change the
port and the authorization shape. A host with several projects sweeps each project root — a short list it
configures, rather than the leaf scopes Subtree exists so that nobody has to discover.

#### Stopping early

Erasure is the one thing this library cannot undo, so a sweep that stops half-way never throws away how much it
destroyed:

- **Cancellation** between records *returns* the partial result, with `Interrupted: true` and `MoreRemain: true`.
  Cancelling a sweep is a normal way to run one, and a host that asked for it still needs the count for its own
  compliance log.
- **A storage failure** part-way throws `ExperienceRetentionSweepInterruptedException`, which carries the same
  partial result on `.Partial` and is an `ExperienceStoreException` like every other storage failure here — so a
  host that already catches those keeps working and does not have to learn a new type to stay correct.

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
  and `auth` must permit the owner scope; both are checked before a connection opens. `ScopeMatch` means exactly
  what it means for the sweep, over the rows' **owner** scope; the recipient scope plays no part.
- **Age is `recorded_at`, the database's clock when the row landed** — never `occurred_at`, the reader's clock,
  which a skewed reader could backdate into the purge window.
- **A row is kept at least 30 days (`MinimumRetentionDays`), by the database's clock.** The purge must not be
  usable, through this library, to erase a read the moment after it happened — a bug, a careless script, or a
  misused call covering one. It does not bind the tables' owner, which can disable the trigger or insert rows with
  any `recorded_at` (see the honesty statement below); in the [two-role deployment](#deploying-with-two-roles) the
  application role is not the owner and holds no `DELETE` on the ledger at all. A cutoff later than that floor is **refused, not clamped**: `Invalid` on `Cutoff`, nothing removed. A
  clamp would report a clean purge while rows the host asked about survived, which is the failure mode the sweep
  heading above is about. The append-only guard re-checks the floor on every row it admits, so even a session that
  sets the marker by hand cannot delete a younger row. Thirty days is a floor, not a policy; the host's retention
  is the cutoff it passes.
- **Bounded, and one transaction per batch.** At most `batchSize` rows (1…500, and clamped to that inside the
  function too, so a hand-caller's `NULL` is never "no limit"), oldest `recorded_at` first, locked in a fixed order
  so two concurrent purges neither deadlock nor count a row twice. `MoreRemain` is asked after the delete, in the
  same transaction, with the same predicate.
- **Its own marker.** `purge_grant_access` sets `agent_experience.access_purge_authorized`, not `0010`'s
  `purge_authorized`, and the guard admits a `DELETE` on this ledger only under it. `0010`'s marker still admits
  nothing here, so "erasing a record keeps its access rows" stays a property of the schema.

### The honesty statement, and the limits

Erasure needs `DELETE` on five append-only tables. `0006` documented a manual runbook for that —
`ALTER TABLE … DISABLE TRIGGER`, delete, re-enable — and `0010` replaces it rather than automating it: the guards
themselves recognise one transaction-scoped marker, `SET LOCAL agent_experience.purge_authorized = 'on'`, set only
inside the purge function, and they go on refusing `UPDATE` and `TRUNCATE` unconditionally in every session,
including the purging one. Nothing is ever disabled, and no other connection's window is widened for an instant.

**The marker is not the boundary; the application role's privileges are.** Be precise about which does what:

- A custom GUC is settable by any session. A connection that holds `DELETE` on these tables can issue the same
  `SET LOCAL` itself and then delete from them directly. The marker decides whether a *permitted* delete is refused;
  it never decides permission. That is why, in the [two-role deployment](#deploying-with-two-roles), the application
  role holds **no** `DELETE` on any ledger and no `UPDATE` on the tombstone's columns: setting the marker by hand
  gains it nothing, because the privilege system refuses the statement before a trigger runs. The tests prove this
  from the application role's own connection, with each marker and both.
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

**A record whose run was erased can never be finalized again.** `ExperienceFinalizationService.ExperienceIdFor`
derives a record's ID from the run *and the scope*, deterministically, so replaying finalization for that run
derives the same ID, collides with the tombstone, and stops — permanently. That is the intended terminal
semantics: re-finalizing would recreate exactly what the deletion removed. It is the same *shape* of permanent
dead end that mixing the scope into the derivation just closed, and the difference is what matters — the old one
was reachable from any scope and undiagnosable, because `CreateAsync`'s conflict is deliberately scope-blind,
while this one is reachable only by the scope that owns the record and that scope can see exactly why:
`GetAsync` answers `Deleted` for its own tombstone. (That change removed the one-argument
`ExperienceIdFor(Guid)` with no compatible overload. The library is pre-1.0 and unpublished, and an `[Obsolete]`
overload could not have been kept honestly — it would have to go on deriving the squattable ID. Callers pass the
same `Scope` they finalize under; nothing persisted needs migrating, because a record's ID is stored, never
re-derived.)

**What deletion does not reach in plaintext mode**, stated rather than buried. Plaintext mode is the default. With
[crypto-shredding](#crypto-shredding-erasure-that-reaches-every-copy) configured, the first and fourth bullets below
shrink to the derived search data, and the next section says exactly what remains.

- **Backups, replicas, WAL, and logical-replication streams.** Host-owned, and out of reach of this schema. A
  deployment with a retention obligation has to reach them itself.
- **Exported telemetry.** Spans and metrics this library emitted carry record IDs; erasing a record does not
  retract them. The erasure's own telemetry adds nothing that was erased (see below).
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
  longer carry the erased text, so they cannot return it — the distinction from the bullet above is exact, and
  was worth stating both ways round.

### Crypto-shredding: erasure that reaches every copy

A delete in plaintext mode removes the live rows and nothing else (see the list above). **Crypto-shredding** makes
erasure reach every copy of a record's text — backups, replicas, WAL, logical-replication streams, `pg_dump` files,
the dead heap tuple — by never storing the text in the clear at all. Each record gets its own random 256-bit data key
from a key store that lives **outside this database's backup domain**; every free-text column erasure removes is
stored as AES-256-GCM ciphertext under that key; and erasure destroys the key. A copy of the ciphertext made at any
time, anywhere, is then unreadable, because nothing that still exists can decrypt it.

It is **opt-in per deployment**. Plaintext mode keeps working exactly as before, and a deployment can switch with the
[upgrade](#upgrading-a-plaintext-deployment) below.

#### What each mode guarantees

| After `DeleteAsync` returns `Deleted` | Plaintext mode (the default) | Encrypted mode |
| --- | --- | --- |
| Live rows in this database | Erased: the tombstone and the list above | Erased, the same way |
| The dead heap tuple, before `VACUUM` | **Readable**: it still holds the text | Holds only ciphertext nobody can open |
| Backups, replicas, WAL, `pg_dump`, replication streams | **Readable** in every copy made before the erasure | Hold only ciphertext nobody can open |
| The derived search data: `search_vector_sealed` (the task ID, summary and lesson as lexemes with positions — words as stems, an identifier-like task ID whole) and the vectors package's embedding and its content hash | Deleted from live rows; readable in every copy | **The same as plaintext mode**: deleted from live rows, readable in every copy. PostgreSQL has to read these in the clear to search, so they are never sealed |
| Identifiers and metadata (IDs — record, run, round, event, evidence, grant, feedback and assessment IDs — scope, statuses, scores and counters, timestamps, principal, reviewer, evaluator and administrator identities, measure kinds and values, trial labels, disclosure levels) | Deleted from live rows (the tombstone keeps its IDs and scope); readable in every copy | The same as plaintext mode: never sealed |
| Rows written before the deployment switched to encrypted mode | — | Record payloads: sealed by the upgrade job, but every copy made *before* it ran is plaintext. Append-only ledger rows (lifecycle reasons, evidence detail, feedback rationale, grant events) and grant reasons written before the switch: **stay plaintext**, in live rows until erased and in every copy |
| Exported telemetry, the server's own logs, external artifacts a record named | Out of reach | Out of reach. The text is sent to the server as statement parameters (the full-text vector is computed there), so a server that logs parameters (`log_statement`, `log_min_duration_statement`, `auto_explain`) writes them to its own log |
| A key store whose keys live in, or are backed up with, this database | — | **Nothing is guaranteed.** A restore brings back the key with the ciphertext |

The two claims the tests check with a real `pg_dump` and a raw `pageinspect` read of the dead tuple: in encrypted
mode neither holds the text, and after the erasure neither can be opened with any key the key store still holds; in
plaintext mode both still hold the text after the erasure.

#### What is sealed

| Column | Sealed under | Stored in the clear instead |
| --- | --- | --- |
| `experience_records.payload` **and** `task_id` — task summary, attempts and tool calls, outcome and evidence detail, reflection and lesson, environment, provenance | the record's key, together as one value | `payload = {"sealed": "aexp-sealed:v1:…"}`, `payload_version = 2`, `task_id = '(sealed)'`, and the derived `search_vector_sealed` |
| `lifecycle_events.reason`, `lifecycle_events.confidence_detail` | the record's key | — |
| `confidence_evidence.detail` | the record's key | — |
| `experience_grants.reason`, `experience_grants.revocation_reason`, `experience_grant_events.reason` | the key of the record the grant is over | — |
| a reuse-feedback rationale | the key of **each** exposed record live in the submission's own scope, once per exposure, in `reuse_feedback_exposures.rationale_sealed` | `reuse_feedback.rationale = '(sealed)'` |

The feedback rule reproduces plaintext mode's erasure exactly: a submission survives while it names any record that
survives, and so does a readable copy of its rationale; once every record it was about is erased, no copy opens. A
rationale that names no live record in the submission's own scope — including one about a record read through a
sharing grant, which lives in its owner's scope — has no key to be sealed under and is not retained, although the
submission is recorded. A
replay is compared against the rationale opened from any surviving copy; when none opens, only its presence is
compared.

**The format.** `aexp-sealed:v1:` followed by base64 of a 96-bit random nonce, the ciphertext and a 128-bit tag.
The associated data binds each value to its column, its record ID, its row (event, evidence, grant, grant event or
feedback ID), and all six scope fields, so a value moved to another record, row, column or scope fails its tag
rather than being read there. A failed tag throws `ExperienceStoreException`; nothing decrypted is ever returned. A
data key encrypts only its own record's handful of values, far inside the 2³² random-nonce bound per key.

#### Key custody: the property is only as true as this

Keys come from an `IExperienceKeyStore` (`AgentExperience.Abstractions`): `CreateKeyAsync` (get-or-create),
`GetKeyAsync`, and `DestroyKeyAsync`, each for one record — its ID and its own scope. **A destroyed reference is
destroyed for ever**: the store never gives it a key again, which is what stops a late writer from making a
half-erased record look live. A sealed row whose key the store has **never** held is a configuration failure (the
wrong or an empty key store) and throws; only a destroyed key reads as erased.

`AgentExperience.Core` ships `EnvelopeExperienceKeyStore`: each data key is stored only *wrapped* by your
key-encryption key, through two small ports you implement over your KMS and your storage.

| Port | Implement it over | Obligation |
| --- | --- | --- |
| `IExperienceKeyEncryptionKey` | Azure Key Vault `wrapKey`/`unwrapKey`, AWS KMS `Encrypt`/`Decrypt` with an encryption context, HashiCorp Vault transit | Bind the wrap to the `ExperienceKeyReference` (associated data or encryption context); keep old KEK versions unwrappable until re-wrapped |
| `IExperienceWrappedKeyRepository` | a table in a **different** database, a secrets store, a blob container | Atomic per record; keep a destroyed marker with no key material; **outside this database's backup domain** |

```csharp
using AgentExperience.Core.KeyManagement;
using AgentExperience.Storage.Postgres.DependencyInjection;

// Production: your KMS-backed IExperienceKeyEncryptionKey and your own repository. The two classes below are the
// reference implementations, for tests and local development only: an in-process KEK, and keys in memory -- a
// restart loses every key, which crypto-shreds the whole store.
var keyStore = new EnvelopeExperienceKeyStore(
    LocalExperienceKeyEncryptionKey.Generate("kek-2026-09"),
    new InMemoryExperienceWrappedKeyRepository());

services.AddAgentExperiencePostgresEncryption(keyStore);   // or new ExperienceEncryption(keyStore) per component
```

Rules for the production key store, each of which the property depends on:

- **Never in this database, on its replicas, or in its backups.** A key restored alongside its ciphertext protects
  nothing.
- **Its own backups bound the erasure.** A destroyed key survives in every key-store backup taken before the
  destruction. The retention of those backups is how long an erased record stays recoverable by someone holding both
  a database copy and a key-store copy — choose it as your erasure deadline.
- **Rotate the KEK and retire the old version to close that window early.** `RewrapAsync(batchSize)` moves every live
  data key under the KEK's current version, bounded and resumable (call it until `MoreRemain` is `false`), and
  never writes back a key destroyed meanwhile. Once it is done, retire the old KEK version in your KMS: every
  key-store backup wrapped under it becomes unusable too.
- **No caching.** The adapter asks the key store on every read and never caches a key; a production key store that
  caches extends its own erasure window by the cache's lifetime. The cost is one key-store call per sealed record
  read, made in turn while the reader holds its connection: a search returning twenty sealed records makes twenty.
- **It is on the erasure's critical path.** An unreachable key store makes every delete fail closed (below), and
  every read of a sealed record fail. So does a sealed row that will not open (a tampered value, or a key the store
  never held): the whole read, search or scan it is part of throws, loudly, rather than dropping the row. A failed
  rotation stops the same way: `RewrapAsync` throws on a key it cannot unwrap, and keeps throwing on it, until the
  KEK version that wrapped it is available again.

#### The erasure's consistency rule

"A sealed record is erased once its key is destroyed." In encrypted mode `DeleteAsync`, and the sweep, which erases
through the same path, run in **one transaction**: the unchanged `purge_experience_record` first — scope and revision
guards, `EXECUTE` on the function, the tombstone, all uncommitted — then `DestroyKeyAsync`, then the commit. A refusal
of any kind (another scope, a stale revision, no `EXECUTE`, a failing statement) destroys no key. Once the key store
has been asked to destroy, the caller's cancellation token no longer applies: the destruction and the commit run to
the end, so a cancelled request cannot leave the key gone and the row live.

| What fails | What you see | State afterwards |
| --- | --- | --- |
| The key store, destroying the key | `ExperienceStoreException` (a sweep: `ExperienceRetentionSweepInterruptedException` with its partial count) | The transaction rolled back: the record is live, readable, and its key alive. **It never looks erased while its key survives** |
| The commit, after the key is destroyed (a lost connection, a crash) | `ExperienceStoreException` | The row is still there, but every read treats a sealed row whose key is destroyed as a tombstone (`Deleted` to the owner, `NotFound` through a grant, absent from query, text and vector search, history, grants and the re-index scan), and every write is refused (`Deleted`, `Conflict`, `NotFound`, `Missing` or `Invalid`, as for a tombstone), because the key store will not create its key again. **A sealed record never looks live while its key is gone.** Its derived search data, its grants and its ledger rows are still in the live tables until the tombstone is written: **retry the delete** that threw (it is idempotent) — the next sweep past its age, or the upgrade job, also finishes it. A plaintext row written before the upgrade is not protected by its key; it reads as live until the retry erases it |
| A process with no `ExperienceEncryption` deletes a sealed record | `ExperienceStoreException` (`42501`) | Nothing erased, key alive. `0016`'s guard refuses to tombstone a sealed row unless the transaction declares that it destroys the key (`SET LOCAL agent_experience.erasure_destroys_key = 'on'`, which the encrypted-mode store sets). Like `0010`'s marker it guards against a mistake, not an adversary: any session can set it |

A record that is already a tombstone has its key destroyed again, idempotently, on every repeat delete — so a
tombstone written around the library still loses its key. Key destruction runs while the transaction holds the row,
so it costs a row lock for the length of one key-store call.

**Other things to know.** Text in the sealed format (`aexp-sealed:v1:…`) is refused on write in both modes, as is a
feedback rationale that is exactly `(sealed)`: a stored value with that prefix is opened as ciphertext. The reverse is
not detected: a writer that can bypass the library can store plaintext in a sealed column, and it reads back as
stored, because a row written before the upgrade looks the same. A search whose `LIMIT` reaches records in the
half-erased state above returns fewer than it could. A key is created for a write before the database confirms the
record, so a write that then fails (a foreign or unknown ID) leaves an unused key in the key store; it encrypts
nothing. And a host that turns on Npgsql's `Include Error Detail` gets row values, including sealed search lexemes
and every plaintext row value, in constraint-violation messages: leave it off.

#### Search, and why the residual is what it is

Sealing the search data would mean giving up full-text and vector retrieval in encrypted mode, which is the product.
So encrypted mode keeps them, and the residual above is exactly them:

- **Full text.** A sealed record's `search_vector_sealed` is computed, at write time, from exactly the expression
  `0003`'s generated `search_vector` uses — task ID, summary and lesson, bounded to 100000 characters — so a sealed
  record ranks exactly as its plaintext twin. The raw text is never stored; the tsvector's lexemes and positions are.
  The generated `search_vector` of a sealed row holds only the placeholder. Search matches `search_vector` for a
  plaintext row and `search_vector_sealed` for a sealed one, through two GIN indexes, and ranks on
  `coalesce(search_vector_sealed, search_vector)`. Erasure clears `search_vector_sealed` (a `0016` trigger on the
  tombstone transition).
- **Vectors.** The embedding is computed from the summary the re-index scan opens in process with the record's key;
  it and its SHA-256 content hash are stored as before. See the vectors package's README.

#### Upgrading a plaintext deployment

1. **Migrate** as the owner: `0016_crypto_shredding` adds two nullable columns, two partial indexes, three
   `NOT VALID` checks, one trigger and the sealing function. It touches no row. On a large table build the two
   indexes out of band first; the script's header has the `CONCURRENTLY` statements.
2. **Stand up the key store** outside the database's backup domain (above).
3. **Configure every component with the same `ExperienceEncryption`** and deploy. From here on every new record and
   every new ledger row is sealed. A process left in plaintext mode keeps writing plaintext, and fails loudly on the
   first sealed record it reads.
4. **Grant the job its function**: re-run `ApplyApplicationRolePrivilegesAsync` with the **same options as today
   plus** `AllowSealing = true` — the call is declarative, so leaving out `AllowErasure` would take erasure away.
   `AllowSealing` is a content-rewrite power: the function checks the shape of what it stores, never its meaning
   (it holds no key), so a role with it can replace any live plaintext record's payload with a well-formed seal of
   anything. Grant it for the upgrade only, or run the job under a separate, short-lived role.
5. **Seal the existing records**, oldest first, in bounded, resumable, authorized batches, until `MoreRemain` is
   `false` for every project:

   ```csharp
   ExperienceSealingResult batch;
   do
   {
       batch = await store.SealPlaintextRecordsAsync(auth, projectScope, batchSize: 200, ScopeMatch.Subtree, ct);
   }
   while (batch.MoreRemain);
   ```

   Each record is sealed in its own transaction: locked, keyed, sealed, the seal opened again under the record's own
   associated data, compared with the stored text and decoded as a payload, and only then written by
   `seal_experience_record`, which admits one transition — a live plaintext row
   at the revision read into its sealed shape — and copies the row's own full-text vector. Nothing about the record
   changes: its revision, status, ranking, embedding and history read exactly as before. `auth` must permit the root
   scope, as for a sweep; without `AllowSealing` the call fails with a permission error and seals nothing. A record
   whose key is already destroyed (a delete that did not commit) is erased instead, finishing that delete, which needs
   `AllowErasure`. A record that cannot be sealed stops the batch with an exception; the records sealed before it
   stay sealed, and a re-run starts from it. It is the `record.seal` telemetry operation.
6. **Take `AllowSealing` away again** on the next deploy: re-apply the same options without it.
7. **Deal with the copies the job cannot reach**: every backup, replica, WAL archive and dump taken before step 5
   still holds the plaintext, and so does each sealed record's dead tuple until `VACUUM`. Run `VACUUM` on
   `agent_experience.experience_records`, and age out the pre-upgrade backups on your normal schedule. Ledger rows
   and grant reasons written before step 3 stay plaintext until their record is erased; the library does not open
   an `UPDATE` path on its append-only audit trail to re-encrypt them.

### Telemetry

Erasure is the one part of this package that emits telemetry, on the `AgentExperience.Storage.Postgres`
`ActivitySource` and `Meter`, which a host subscribed to `AgentExperience.*` already receives. `DeleteAsync` is the
`delete` operation, `SweepExpiredAsync` is `retention.sweep`, `PurgeExpiredAsync` is `grant.purge`, and the
crypto-shredding upgrade job, `SealPlaintextRecordsAsync`, is `record.seal`. Each call
produces one span, one count and one duration under the result's `ExperienceStoreOutcome` name, and
`agentexperience.operation.failures` moves only when the call throws. These are the same instruments and dimensions
Core uses. A `delete` span carries the record's ID. A sweep or purge span carries `erased_count`, and a sweep span
also carries `interrupted`; a `record.seal` span carries `sealed_count` and `scope_match`. None of them carries a scope, task ID, payload, grant ID, grant reason, recipient scope or
administrator. The full contract is in
[`docs/telemetry.md`](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/telemetry.md).
The package still references no OpenTelemetry package: `ActivitySource` and `Meter` are part of the BCL.

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

**Read the limits of those triggers before relying on them.** They bind every writer that holds the privilege to
write, including one that bypasses this library. They do not bind a superuser, and they do not bind the tables' own
owner, since an owner can disable or drop a trigger and then write freely. In the
[two-role deployment](#deploying-with-two-roles) the application role is not the owner and holds no `UPDATE`,
`DELETE` or `TRUNCATE` on the logs, so it is refused by the privilege system first. This is a guard against a bug, a
careless script, or a compromised application path, not tamper-proofing against an administrator holding the owner's
or a superuser's credentials. A deployment that needs that should ship the log off-box.

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

`0008_reuse_feedback.sql` adds the append-only reuse feedback ledger:

- `reuse_feedback`: `feedback_id` as the primary key (the submission's idempotency key), the run, the same scope
  columns as `experience_records`, the run's outcome, `claimed_benefit` and `benefit`, `attribution_source`, the
  reviewer identity *or* the evaluator and verification round, the rationale, the measure's kind and value, the
  trial label, and `observed_at`/`recorded_at`.
- The `CHECK` that carries the whole story: `(attribution_source = 'None') = (benefit = 'Unknown')`. "Nothing
  attributed this" and "benefit unknown" are one fact, so they cannot drift into a row claiming an improvement
  nothing evidenced. `claimed_benefit` sits beside it, recorded and never promoted: what a caller believes is data
  about the caller, not evidence about a record.
- Further `CHECK`s making each attribution shape carry exactly what it must: a human row a reviewer, an
  `assessment_id` naming the host-established review it came out of, and no evaluator or evidence list; a
  comparative row an evaluator, a round, and a non-empty `evidence_ids`, and no reviewer or assessment. Both carry
  `attributed_at`. The round is the machine independence key's second half for a comparative row and **audit only**
  for a human one, whose evidence is keyed on the reviewer and the run instead.
- `evidence_ids` is stored so an auditor sees what a moved score rested on rather than only the evaluator's own
  summary of it — Core cross-checks each piece against the round the result names before it is accepted.
- `reuse_feedback_exposures`: one row per record the run saw, keyed `(feedback_id, experience_id)`, carrying
  `ordinal` and `attributed`, plus the `evidence_id` derived from `(feedback_id, experience_id)` — present exactly
  when `attributed`. `ordinal` is the *normalized* order (by experience ID), not the caller's, and is bounded by
  `CHECK (ordinal >= 0 AND ordinal < 64)`, which mirrors `ExperienceReuseFeedback.MaxExposedRecords` and is the
  schema's half of the only bound on a submission's fan-out.
- **`evidence_id` says which ID, not that it landed.** It is written with the exposure, before any confidence
  submission is attempted, because the exposure must be durable first. An attributed exposure whose record turned
  out ineligible or unresolved, or whose commit failed, therefore has an `evidence_id` with no row in
  `confidence_evidence`. That is the outstanding work, not a dangling reference: read it with a `LEFT JOIN` — the
  script's header carries the query — and an inner join would silently drop exactly the rows worth looking at.
- Unique indexes on `evidence_id` (partial, where present) and on `(feedback_id, ordinal)`. The derivation is a
  pure function of the feedback and the record, so a collision on the first means it was bypassed rather than that
  two observations coincided. The script's header carries the `CREATE UNIQUE INDEX CONCURRENTLY` runbook for both,
  for a database whose schema was applied by hand and may already hold rows.
- The exposures-to-submissions foreign key is added `ALTER TABLE … NOT VALID`, with the confirmation query and the
  `VALIDATE CONSTRAINT` statement in the script's header — for the same hand-applied case. Everything else is a
  constraint on a table this script creates, so it starts empty and there is nothing to scan.
- Deliberately **no** foreign key to `experience_records`, matching `0007`: a run that saw an ID resolving to
  nothing in its scope must still be recordable, and a foreign key would turn that fact into a write failure.
- `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` triggers on both tables, reusing `0006`'s function. Promoting a
  recorded exposure into an attribution after the fact is exactly what they stop. The same limits apply as to
  `0006`'s: read them above before relying on them.

`0009_grant_access_log.sql` adds the append-only grant access ledger and the database's own grant-lifetime ceiling:

- `experience_grant_access`: `access_id` as the primary key, the `grant_id` the read predicate actually used, the
  record and the `record_revision` that was disclosed, the record's owner scope, the recipient scope the read was
  made in, the reading `principal_id` (**`NOT NULL`** — a row that cannot say who does not answer the question),
  the optional `correlation_id`, and `occurred_at`/`recorded_at`. One row per record a grant **delivered** — see
  the table above for exactly what is and is not recorded.
- `CHECK`s mirroring `0005`'s: non-empty IDs, non-blank scope, and — restated on the row that claims a grant
  carried a record across a boundary — `experience_grant_access_same_boundary` and
  `experience_grant_access_recipient_differs`. A row saying a record left its tenant, application, or project is
  unstorable here even if some other writer managed to store the grant that would have allowed it.
- Indexes on `(grant_id, occurred_at)` — "who read anything through this grant" — and on
  `(tenant_id, application_id, project_id, experience_id, occurred_at)` — "who saw our team's experience, and
  when". The script's header carries the `CREATE INDEX CONCURRENTLY` runbook for a hand-applied database.
- Deliberately **no** foreign key to `experience_grants` or `experience_records`, matching `0002`, `0007`, and
  `0008`: the trail must outlive whatever it describes. `grant_id` joins to `experience_grants` and
  `experience_grant_events` by hand, which is how an auditor moves from "who read it" to "who permitted it"; the
  script's header carries that query.
- `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` triggers, reusing `0006`'s function. Erasing that a record was
  handed to someone is exactly what they stop, and the same limits apply as to `0006`'s.
- `experience_grants_lifetime_bounded` on the **existing** grants table, added `ALTER TABLE … NOT VALID` because a
  database issuing grants since `0005` may already hold an unbounded one. It exempts a revoked row
  (`revoked_at IS NOT NULL OR …`), because PostgreSQL re-checks a `CHECK` on every `UPDATE` and revoking such a
  grant is the one remedy the runbook prescribes — without the exemption it would be permanent and unrevocable.
  The header carries the query that finds them and the `VALIDATE CONSTRAINT` statement; `0006`'s trigger still
  refuses any `UPDATE` that moves `expires_at` outward, so they are ended by revoking rather than shortened.
- `experience_grants_issued_not_future`, a `BEFORE INSERT` trigger refusing a grant dated more than a minute ahead.
  The ceiling above is relative to `issued_at`, so without it a bypassing writer could store an effectively
  permanent grant simply by dating it a century forward; a `CHECK` cannot say this, because it may not call
  `now()`.
- Retention: this ledger is deliberately **not** swept by a record erasure, because who read a record before it was
  deleted outlives the record. It is the table most likely to grow without bound in a deployment that shares
  heavily, so plan its retention — which is the host's, on host-owned terms — before enabling auditing at scale.

`0010_delete_and_expire.sql` adds the one erasure path (see [Deleting and expiring data](#deleting-and-expiring-data)):

- `experience_records.deleted_at`, nullable, so no existing row is rewritten, plus
  `experience_records_tombstone_shape` — added `ALTER TABLE … NOT VALID` like every other `CHECK` on an existing
  table — which makes "erased" one shape rather than a flag a writer could set over a payload that is still there.
- `agent_experience.purge_experience_record`, a `SECURITY DEFINER` function holding the whole erasure: the scope
  and revision guards, the seven tables it sweeps in the order above, and the tombstone.
  `agent_experience.purge_expired_grants` does the same for expired grants and their events.
- `agent_experience.purge_authorized()`, which reads the transaction-scoped marker the guards recognise, and
  replacements for `0006`'s `reject_event_log_mutation` and `reject_audited_grant_delete` and `0007`'s
  `enforce_record_projection`. They are replaced with `CREATE OR REPLACE`, so every `ENABLE ALWAYS` binding
  survives and no table is unguarded for an instant; nothing is dropped, disabled, or recreated. `UPDATE` and
  `TRUNCATE` stay refused unconditionally, and `DELETE` is admitted only under the marker and only on the five
  tables an erasure sweeps — `experience_grant_access` is deliberately not one of them.
- `agent_experience.reject_record_removal`, and the only two triggers this script creates:
  `experience_records_no_delete` and `experience_records_no_truncate`, both `ENABLE ALWAYS`. A bare
  `DELETE FROM agent_experience.experience_records` used to succeed from any session with `DELETE` on the table —
  orphaning the whole audit trail, none of which has a foreign key back to the record, and **freeing the ID**, so
  that a record re-created under it inherits every grant issued over the old content. It is refused now with no
  marker clause and no exception at all, because the erasure never deletes that row: it updates it into a
  tombstone, which is the point.
- The projection guard gains two rules: a tombstone can never be updated again, by anyone, and `deleted_at` can be
  set only inside the purge. The marked exception is shape-checked rather than merely marker-checked — a live row,
  to an empty-payload tombstone, in its own scope, with its own `payload_version` and a `created_at` equal to the
  deletion instant, one revision forward — so a marked transaction may make that one transition and no other. The
  scope columns are part of that check for a concrete reason: without them one marked `UPDATE` could tombstone a
  record *into another tenant's scope*, leaving the owning scope seeing `NotFound` for its own erased record.
- `ix_experience_records_live_by_age`, partial on `deleted_at IS NULL`, which is the retention sweep's whole
  predicate; plus the `confidence_evidence (experience_id)` and `reuse_feedback_exposures (experience_id)` indexes
  `0007` and `0008` each deferred to this story, because this is the query that justifies them. **All three are
  built with plain `CREATE INDEX` inside the migrator's per-script transaction**, which takes a `SHARE` lock and
  blocks writes to those tables for the duration — and one of them is over `experience_records`, the table this
  library writes most, so on an established database this is a larger write outage than `0007`'s, `0008`'s or
  `0009`'s. `CREATE INDEX CONCURRENTLY` cannot run in a transaction block at all, so it cannot simply be swapped;
  the script's header carries the out-of-band runbook (add the column, build all three `CONCURRENTLY`, then
  migrate, at which point `IF NOT EXISTS` makes the script's own statements no-ops), exactly as `0007`, `0008`
  and `0009` do for theirs.
- `REVOKE ALL … FROM PUBLIC` on both purge functions, and `GRANT EXECUTE … TO CURRENT_USER`. Without it,
  PostgreSQL's default `EXECUTE`-to-`PUBLIC` on a `SECURITY DEFINER` function would make erasure available to
  every role that can connect.
- The script's header carries the honesty statement, the retained list (including `payload_version`), the
  privilege note, the `CONCURRENTLY` runbook, the dead-heap-tuple limit, and the confirm-then-`VALIDATE` step.

`0011_grant_disclosure.sql` adds a grant's disclosure level (see [Sharing grants](#sharing-grants)):

- `experience_grants.disclosure text NOT NULL DEFAULT 'LessonOnly'`, with `experience_grants_disclosure_known`
  (`'LessonOnly'` or `'LessonAndApproach'`). The default covers every existing grant and any writer that bypasses
  this library. **This changes behaviour on upgrade:** every existing grant becomes `LessonOnly`, so a borrowed
  record's `Approach:` line stops being injected until the owner revokes the grant and issues a `LessonAndApproach`
  one.
- **Deployment order:** run `0011`, then deploy this build, and stop older writers first. This build on a pre-`0011`
  schema fails every grant-joined read with `42703`; an older build on a `0011` schema cannot write grant events or
  access rows, because both now require a level. Both failures are loud on purpose.
- A nullable `disclosure` on `experience_grant_events` and on `experience_grant_access`, each with a
  `*_disclosure_known` `CHECK` and a `*_disclosure_recorded` `CHECK (disclosure IS NOT NULL)` added `NOT VALID`.
  New rows must carry a level; rows written before `0011` stay `NULL` — "not recorded" — and are never re-checked.
  Leave both `NOT VALID`: those rows cannot pass a `VALIDATE`, and nothing can backfill an append-only trail. A
  hand-written event or access row now has to name a level too.
- `enforce_grant_monotonicity()` replaced in place with `0006`'s whole body plus `disclosure` among the identity
  pins, so a live grant's level cannot be changed by `UPDATE` (`42501`); revoke it and issue a new one instead.

`0012_grant_access_retention.sql` adds the access ledger's retention path (see
[Retention for the grant access log](#retention-for-the-grant-access-log)):

- `agent_experience.purge_grant_access`, `SECURITY DEFINER` with `search_path` pinned, `EXECUTE` revoked from
  `PUBLIC` and granted to the migrating role: bounded (1…500, `NULL` is 500), scoped to an owner scope exactly or
  as a subtree, age on `recorded_at`, and refusing (`CutoffTooRecent`) any cutoff later than 30 days before
  `clock_timestamp()`, or a `NULL` one.
- `agent_experience.access_purge_authorized()`, its own marker (reset when the function returns), and `reject_event_log_mutation()`
  replaced in place with `0010`'s body unchanged plus one exception: a `DELETE` on `experience_grant_access` under
  that marker of a row at least 30 days old. `UPDATE` and `TRUNCATE` stay refused; `0010`'s marker still admits
  nothing on that table; nothing is dropped, disabled or recreated.
- `ix_experience_grant_access_retention (tenant_id, application_id, project_id, recorded_at, access_id)`, built with
  plain `CREATE INDEX` inside the migrator's transaction, which blocks appends to the ledger while it builds; the
  header carries the `CONCURRENTLY` runbook for building it out of band first.

`0013_role_separation_hardening.sql` hardens the schema for the [two-role deployment](#deploying-with-two-roles):

- `ALTER FUNCTION … SET search_path = pg_catalog, agent_experience, pg_temp` on the three `SECURITY DEFINER` purge
  functions and on every guard trigger function (`reject_event_log_mutation`, `enforce_grant_monotonicity`,
  `reject_audited_grant_delete`, `enforce_record_projection`, `reject_record_removal`,
  `reject_future_grant_issue`). No body is restated and no trigger recreated, so no table is unguarded while it
  applies; the purge functions keep their marker-reset `SET` clauses.
- `REVOKE ALL … FROM PUBLIC` on the three purge functions, restated so a hand-widened ACL is narrowed back.
- It grants nothing to a named role: that is `ApplyApplicationRolePrivilegesAsync`'s job, on every deploy.

`0015_verified_independence.sql` makes an assessment single-use (story 6.6, KL-11; see
[Confidence evidence](#confidence-evidence)):

- `confidence_evidence.assessment_id uuid NULL` and `lifecycle_events.confidence_assessment_id uuid NULL`, each with
  a `CHECK` keeping it on human rows only and off the empty UUID, added `NOT VALID` so neither ledger is scanned; the
  header has the `VALIDATE` statements (every existing row is `NULL`, so both pass).
- `ux_confidence_evidence_assessment`, unique on `(experience_id, assessment_id) WHERE assessment_id IS NOT NULL`,
  built with plain `CREATE UNIQUE INDEX`, which blocks evidence appends while it builds; the header carries the
  `CONCURRENTLY` runbook for building it out of band first.
- No table, function, trigger or grant, so the application role's manifest is unchanged: its table-level `INSERT`
  and `SELECT` on both ledgers cover the new columns, and it has no `UPDATE` on either.
- The closed round a finalized record vouches for needs no schema: it travels in the payload as `closedRoundId`,
  written only when finalization closed a round (a payload with none is byte for byte what it was), and read back as
  `ExperienceRecord.ClosedRoundId`. The payload version stays `1`; an older reader ignores the field. A record
  written before this version has none, so machine evidence about its run is refused unless the host opts out.

`0016_crypto_shredding.sql` gives [crypto-shredding](#crypto-shredding-erasure-that-reaches-every-copy) its database
side, and changes nothing for a deployment that stays in plaintext mode:

- `experience_records.search_vector_sealed tsvector NULL`, with a partial GIN index on it, and
  `reuse_feedback_exposures.rationale_sealed text NULL`.
- `ix_experience_records_unsealed`, a partial index over live `payload_version = 1` rows: the upgrade job's worklist,
  which empties as the job runs.
- Three `NOT VALID` checks: a live sealed row (`payload_version = 2`) has exactly the sealed shape — one sealed
  property, the `(sealed)` task ID, a sealed search vector; only a live sealed row has a sealed search vector; and
  `rationale_sealed` holds only the sealed format.
- `guard_sealed_record_erasure`, a `BEFORE UPDATE` trigger function on the tombstone transition: it refuses to
  tombstone a sealed row unless the transaction declares, with `agent_experience.erasure_destroys_key`, that it
  destroys the key, and it clears `search_vector_sealed`, so `0010`'s purge function is not restated.
- `agent_experience.seal_experience_record`, `SECURITY DEFINER` with `search_path` pinned and `pg_temp` last, and
  `EXECUTE` revoked from `PUBLIC`: the upgrade job's one write, admitting only a live plaintext row at the expected
  revision into its sealed shape. `ApplyApplicationRolePrivilegesAsync` grants it only with `AllowSealing`.

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
- **Permissions.** Run the migrator as the **owner** role of the [two-role deployment](#deploying-with-two-roles). It
  needs `CREATE` on the database (for the `agent_experience` schema) and on that schema (for its tables), and it has
  to *own* every table whose triggers and guard functions a script creates or replaces — which it does, because it
  created them. It does **not** need to be a superuser, with one exception granted once by a superuser: `0010` and
  `0012` create functions whose `SET` clause names the two purge markers, and PostgreSQL 15+ lets a non-superuser
  name a custom setting there only with `GRANT SET ON PARAMETER agent_experience.purge_authorized,
  agent_experience.access_purge_authorized TO <owner>`. No script here creates an extension. The stores never need
  more than `ApplyApplicationRolePrivilegesAsync` grants the application role — the table there is exhaustive — and
  a reader without `SELECT` on `experience_grants` falls back to the exact-scope predicate (see
  [Sharing grants](#sharing-grants)).
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
substitution is off, so `$body$` and `$1` in a script are left alone, and the runner does not log: nothing reaches
the console, a `Trace`/`Debug` listener, an `ILogger`, or an `AgentExperience.*` activity source, on a clean run or a
failing script (`MigratorLogSilenceTests` captures all four). The one thing outside the runner's reach is your own
driver logging: if you built the data source with `NpgsqlDataSourceBuilder.UseLoggerFactory`, Npgsql's
`Npgsql.Command` category logs each script's SQL text at `Information`, exactly as it logs every other command on
that data source. The scripts carry no row data; filter that category if you do not want DDL in your logs.

`PostgresExperienceRecordSchema.ScriptNames` and `GetScript` remain available for reading a script's SQL (for review
or for applying it through your own change-management tooling), but the migrator is the supported way to apply it.

### Script naming and ordering

Scripts are **append-only**. Each is named with a zero-padded numeric prefix (`0001_`, `0002_`, ...) and a short
description, and they run in ordinal name order. The journal records a script by name, so **a script that has been
journaled anywhere must never be edited or renamed**: databases that already applied it would silently keep the old
definition, and a rename would reapply it. Change the schema by adding the next-numbered script instead.

### Script comments that were written before the work they point at shipped

Because a journaled script is never edited, a few script *comments* still describe later roadmap stories as future
work. They ship inside this package as embedded resources, so here is what each one now means. None of them changes
what a script does; they are comments only.

| Script | Its comment says | What actually shipped |
| --- | --- | --- |
| `0006` | Purging an event is an operator action (`ALTER TABLE … DISABLE TRIGGER`) "until the library ships a purge path (roadmap story 4.5 …)" | Story 4.5 shipped it as `0010`, whose header says so and supersedes that runbook: one `SECURITY DEFINER` purge function under a transaction-scoped marker, no trigger ever disabled. Do not use `0006`'s runbook — see [Deleting and expiring data](#deleting-and-expiring-data) |
| `0007` | Listing the evidence ledger, a foreign key to `experience_records`, and retention over `confidence_evidence` "all belong to roadmap story 4.5" | Retention shipped in `0010`: erasing a record removes every evidence row naming it, with the index that needs. The foreign key was deliberately **not** added (`0010` explains why). Listing the ledger through the port was decided against in story 4.3 (AD-C): lifecycle history already carries each counted update's prior and new values, and no acceptance criterion needs uncounted duplicates |
| `0008` | Retention of the feedback ledger is "deferred to roadmap story 4.5", to be done with `0006`'s runbook | Shipped in `0010`: erasing a record removes its exposure rows and any submission left empty, with the index that needs. `0006`'s runbook is superseded as above |
| `0008` | The aggregations "roadmap story 4.4 needs — by run, by trial label, by scope" will come with their own indexes | Story 4.4 measured reuse through in-memory port doubles, not SQL over this ledger, so no aggregation query and no index was added. Add one with the first query that needs it |
| `0006`, `0010`, `0012` | The guards do not bind the tables' owner, "which the application role is because it created them"; the purge path is "an auditability mechanism, not a privilege boundary"; an application role that is not the migrating role must be granted `EXECUTE` by hand | Story 6.1 made the two-role deployment the supported one: the application role owns nothing and holds no `DELETE` on any ledger, so neither the owner's escape hatch nor a hand-set marker is available to it, and `ApplyApplicationRolePrivilegesAsync` grants `EXECUTE` when the host opts in. The owner and superusers remain unbound. See [Deploying with two roles](#deploying-with-two-roles) |
| `0010` | "Backups, replicas, WAL and logical-replication streams … are host-owned and out of reach of this schema", and the dead tuple "STILL CARRIES THE ERASED TEXT" | Still exactly true in plaintext mode. In encrypted mode (story 6.4) every one of those copies holds only ciphertext whose key the erasure destroyed; what stays readable is the derived search data. See [Crypto-shredding](#crypto-shredding-erasure-that-reaches-every-copy) |
| `0009` | Retention of the grant access log is "deferred to roadmap story 4.5", to be done with `0006`'s runbook | Story 4.5 decided to **keep** access rows when a record is erased — they carry no payload and answer "who read this before it was deleted". Their retention shipped in `0012` (story 5.4) as its own path, by age, never younger than 30 days, under its own marker — not `0006`'s runbook. See [Retention for the grant access log](#retention-for-the-grant-access-log) |

## Data semantics

- **One write path per change.** Each create is a single `INSERT`. The only update is a lifecycle commit, which is
  always paired with its event in one transaction (see above). The only deletion is `DeleteAsync` and the retention
  sweep that runs it, which erase payload and leave a tombstone (see
  [Deleting and expiring data](#deleting-and-expiring-data)); apart from the two purges — expired grants with their
  events, and grant access rows past their retention (`0012`) — no other path *in this library* removes a record,
  an event, or a ledger row, and for the record row itself `0010`'s removal guard makes that true of the schema
  rather than only of the library — a bare `DELETE` or `TRUNCATE` is refused from every session, marker or not.
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
