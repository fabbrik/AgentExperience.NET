# AgentExperience.Storage.Postgres

Stores AgentExperience.NET Experience Records in PostgreSQL through the `IExperienceRecordStore` port, using plain
Npgsql.

Pinned to `Npgsql` **10.0.3**, `dbup-postgresql` **7.0.1**, and `dbup-core` **6.1.1** (all exact). Integration tests
run against PostgreSQL 16 (`pgvector/pgvector:pg16`) through `Testcontainers.PostgreSql` 4.15.0. This package does not
use EF Core, Dapper, Pgvector, or the pgvector extension.

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
```

The store never disposes the data source. The host owns it.

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
| Database or driver failure (`NpgsqlException`, `SocketException`, `TimeoutException`) | throws `ExperienceStoreException` with the original as `InnerException` |
| Stored row with an unsupported `payload_version` or an unreadable payload | throws `ExperienceStoreException` |
| Caller cancellation | throws `OperationCanceledException`, unwrapped |

Validation messages never contain record payload content, and the store does not log.

A create whose acknowledgement was lost (cancelled or timed out after PostgreSQL committed it) returns `Conflict`
when retried. After a `Conflict`, call `GetAsync` in your own scope to check whether the stored record is yours.

## Schema

The schema lives in the embedded script `Migrations/0001_create_experience_records.sql`. It creates the
`agent_experience` schema and the `experience_records` table:

- Scope, task, status, confidence, counter, revision, and timestamp columns, with `CHECK` constraints for non-blank
  scope and value ranges.
- A JSONB `payload` column for attempts, outcome, evidence, reflection, environment, and provenance.
- A `payload_version` column. This adapter owns versioning, so the domain types carry no version field.
- An index on `(tenant_id, application_id, project_id)`.

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
  schema (for its tables). The store itself only needs `INSERT` and `SELECT` on
  `agent_experience.experience_records`.
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

- **Create-only.** Each create is a single `INSERT`. Updates, deletes, and lifecycle events belong to later stories.
- **UTC timestamps.** Every timestamp is stored and returned in UTC. `CreatedAt` and `UpdatedAt` are columns, and
  PostgreSQL keeps microsecond precision, so sub-microsecond ticks are truncated on write. Nested timestamps are
  stored in the payload at full precision.
- **Tool-call arguments** are stored as JSON and read back normalized to `string`, `bool`, `long` (integers that
  fit), `double`, `null`, `Dictionary<string, object?>`, or `List<object?>`. Dictionary key order is not preserved.
  Whole-number doubles (for example `1.0`) are written as JSON integers, so they read back as `long`. Values that cannot be serialized to JSON (for
  example `NaN`, infinities, or cyclic graphs) make the create `Invalid`.
- **Query order** is newest `CreatedAt` first, then `ExperienceId` in PostgreSQL `uuid` byte order, which differs
  from .NET `Guid` comparison. `Limit` must be from 1 to 500 (default 50).
  `Statuses` is either null (all statuses) or a non-empty list.
- PostgreSQL cannot store the NUL character (U+0000) in `text` or `jsonb`, so a record or scope containing it is
  `Invalid` and never reaches the database.
