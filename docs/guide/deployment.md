# Deployment

**In short.** Each package registers its own services, so a host wires the whole loop in a few lines. The schema is
applied explicitly, on every deploy, by a PostgreSQL **owner** role; the application itself connects as a separate
**application** role that owns nothing and gets exactly the privileges the stores need. That two-role shape is the
supported deployment, and it is what makes the append-only guards and the single erasure path bind the application's
own credentials. Every call checks the host's `AuthorizationContext` before touching the database, and scope matching
is exact.

Supported: .NET 8, 9 and 10 (`net8.0`, `net9.0`, `net10.0`); PostgreSQL 15, 16, 17 and 18; `Microsoft.Agents.AI`
`[1.22.0, 2.0.0)`. `net8.0` and `net9.0` leave support on 10 November 2026, and the first preview published after that
date drops them. See [Compatibility evidence](../compatibility-evidence.md#supported-matrix).

## Wiring it all together

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
services.AddAgentExperiencePostgresEncryption(keyStore);        // optional: crypto-shredding for every component
services.AddAgentExperiencePostgresEmbeddingIndex();            // IExperienceEmbeddingIndex
services.AddAgentExperienceEmbeddingGenerator();                // IExperienceEmbeddingGenerator, over a registered
                                                                //    IEmbeddingGenerator<string, Embedding<float>>
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService (verifying independence), ExperienceFinalizationService
services.AddSingleton(new ExperienceIndependenceOptions         // optional: the assessment token key, without
{                                                               //    which human evidence is refused; or the
    AssessmentTokenKey = secrets.AssessmentTokenKey,            //    TrustHostSuppliedIdentifiers opt-out
});
services.AddAgentExperienceIndexing();                          // ExperienceIndexingService, and finalization's
                                                                //    post-commit hook, in either registration order
services.AddAgentExperienceRetrieval();                         // ExperienceRetrievalService
// -> defaults to RetrievalPolicy.Default and RankingWeights.Default; pass your own to override
// -> hybrid, because an index *and* a generator are registered; text-only, and flagged, if either is missing
services.AddAgentExperienceReuseFeedback();                     // ExperienceReuseFeedbackService, over the ledger
                                                                //    above and the lifecycle service

// Injection has no registration of its own: ExperienceContextProvider needs a per-host resolver and
// risk decision, so the host constructs it and adds it to ChatClientAgentOptions.AIContextProviders.
// See the injection guide.
```

The ports are registered independently: a host that only writes experience never has to register the search, one
that only reads never has to register the store, one that never shares a record across scopes never has to register
the grant store — the reads that honour grants do so in SQL either way — and one that never records reuse feedback
never has to register its ledger. Every registration uses `TryAdd`, so a host that has already registered its own
implementation keeps it. `AgentExperience.Abstractions` stays BCL-only; `Core` and the storage packages take only
`Microsoft.Extensions.DependencyInjection.Abstractions` (no container, no hosting) for these extensions.

The vector registrations and the vector migration are optional, and genuinely so: leave them out and everything
still works — finalization commits records with no indexing hook, and retrieval answers from text alone with
`TextOnly` set to `NotConfigured`. That is also why the embedding schema is not in the base package's script list:
`CREATE EXTENSION vector` needs a superuser, and a text-only deployment must never be made to run it for a feature
it has not enabled. See [Indexing](indexing.md).

The MAF adapter can drive finalization for you: set `FinalizationService` and `ResolveFinalization` on
`ExperienceCaptureOptions` and every successfully captured invocation is finalized right after it is completed. See
[Finalization](finalization.md#finalizing-from-the-maf-adapter). `AssessmentTokenIssuer` is deliberately not
registered: construct it in your review flow, where a person decides (see [Confidence](confidence.md)).

## Applying the schema

Schema comes in two calls, matching that split, and neither store ever migrates on its own:

```csharp
// As the owner role, on every deploy. The stores themselves connect as the application role.
await ExperienceSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);        // 0001-0003 and 0005-0018 (no 0014), always
await ExperienceVectorSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);  // 0004, only with the vector channel
await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(                     // last, so it covers both
    ownerDataSource,
    new ExperienceApplicationRoleOptions("agent_experience_app") { AllowErasure = true },
    cancellationToken);
```

How the migrator behaves (journaling, locking, timeouts, permissions) and what each script does is in
[PostgreSQL schema](postgres-schema.md).

## Using the store directly

Records are normally written by Core's `ExperienceFinalizationService`, which creates the record and commits its
initial lifecycle event; `CreateAsync` and `CommitLifecycleEventAsync` stay available for hosts that orchestrate that
themselves.

```csharp
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;

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

// Finding records that could apply to a task. A separate, read-only port.
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

## The trusted host boundary

- `AuthorizationContext` is the authority, and `Scope` only selects within it. The host must build the context from
  its own trusted identity and permission checks, never from model output or request payloads.
- `TenantId` must always match. A non-null bound (`ApplicationId`, `ProjectId`, `TeamId`, `AgentId`, `UserId`) must
  equal the request scope's field exactly. A null bound leaves that field unrestricted. A scope outside the context
  returns `Denied` before any connection opens.
- Scope matching in SQL is exact, ordinal, and case-sensitive. A null optional scope field matches only null
  (`IS NOT DISTINCT FROM`) and never acts as a wildcard. Empty or whitespace scope values are `Invalid`.
- A get for an ID that exists in another scope returns `NotFound`, the same as a missing ID. A create with an
  existing ID returns `Conflict` without record data, whichever scope owns the existing record.
- The store does not evaluate roles. Role-based decisions stay with the host.

## Results and failures

Every port returns a structured result with an outcome rather than throwing for an expected refusal, so a host can
tell "not allowed" from "not there" from "not stored".

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
| The record was erased (in its owning scope) | `Deleted` |
| `UPDATE` or `DELETE` against a stored event row, or a grant revocation cleared, expiry extended, or disclosure level changed | rejected by the database with SQLSTATE `42501`, surfaced as `ExperienceStoreException` |
| Candidate search ran (no text match is still `Found`) | `Found` with the matching candidates, strongest match first |
| Database or driver failure (`NpgsqlException`, `SocketException`, `TimeoutException`) | throws `ExperienceStoreException` with the original as `InnerException` |
| Stored row with an unsupported `payload_version` or an unreadable payload | throws `ExperienceStoreException` |
| Caller cancellation | throws `OperationCanceledException`, unwrapped |

Validation messages never contain record payload content, and the store does not log.

A create whose acknowledgement was lost (cancelled or timed out after PostgreSQL committed it) returns `Conflict`
when retried. After a `Conflict`, call `GetAsync` in your own scope to check whether the stored record is yours.

## Deploying with two roles

This is the supported deployment, and the one every guarantee in these guides about append-only history and erasure
is stated for. It takes two PostgreSQL roles:

- an **owner** role, which owns the `agent_experience` schema and everything in it and runs the migrators. The
  application never holds its credentials;
- an **application** role, which is what every store, search and ledger connects as. It owns nothing and holds
  exactly the privileges the stores need.

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

All three opt-ins are off unless you set them. In one transaction, under the migrator's advisory lock, the call
revokes every privilege the application role holds on the schema and on every table, sequence and function in it,
then grants exactly this:

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
`LOCK TABLE`, `CLUSTER`, `REINDEX` or `VACUUM`), any privilege held `WITH GRANT OPTION`, a sequence, a
`SECURITY DEFINER` function in the schema the role could execute without an opt-in, or a missing grant. What must be
absent is checked on every role the application role is a member of, not only on the ones it inherits from, so a
privilege one `SET ROLE` away (a `NOINHERIT` role, or on PostgreSQL 16 and later a membership granted
`WITH INHERIT FALSE`) is caught too.

Before any of that it refuses a role that does not exist, an unmigrated schema, a superuser, the role running the
call, any role that is — or is a member of — the owner of the database, the schema, or anything in it, and any role
that is or reaches a superuser, one of the server-file roles, `pg_maintain` (PostgreSQL 17 and later), or `SET` on
`session_replication_role`. The message names the violation; nothing is changed.

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
directory is out of reach entirely (see
[What deletion does not reach](deletion-and-retention.md#what-deletion-does-not-reach-in-plaintext-mode)).

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
migrator with the owner's credentials and the privileges call after it, naming the old role as the application role,
and only then start the application again. From that deploy on, the migrator never runs with the application's
credentials again. The package's tests run exactly this SQL against a database migrated by its application role and
prove the old role can no longer disable a guard.
