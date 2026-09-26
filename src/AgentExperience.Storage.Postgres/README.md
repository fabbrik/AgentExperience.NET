# AgentExperience.Storage.Postgres

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

The PostgreSQL storage for [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET), over plain Npgsql.
It implements every storage port: the Experience Record store with its audited lifecycle (`IExperienceRecordStore`),
text search (`IExperienceCandidateSource`), sharing grants and their access log (`IExperienceGrantStore`,
`IExperienceGrantAccessLog`), and the reuse-feedback ledger (`IExperienceReuseFeedbackStore`). It also ships the
journaled schema migrator, deletion and retention, and opt-in crypto-shredding.

Requires `Npgsql` **10.0.3**, `dbup-postgresql` **7.0.1**, `dbup-core` **6.1.1**, and
`Microsoft.Extensions.DependencyInjection.Abstractions` **10.0.12**, or any later release in the same major; on
`net8.0` only, also `System.Text.Json` **10.0.12** or later. Targets `net8.0`, `net9.0` and `net10.0`. Tested against
**PostgreSQL 15, 16, 17 and 18**; PostgreSQL 14 is not supported, because `0005_create_experience_grants` uses
PostgreSQL 15 syntax. No EF Core, Dapper, Pgvector, or pgvector extension: vectors are the separate
`AgentExperience.Storage.Postgres.Vectors` package.

## Setting it up

Two database roles: an **owner** that applies the schema, and an **application** role the stores connect as. The
application role owns nothing, which is what makes the append-only guards and the erasure path bind it.

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres;
using AgentExperience.Storage.Postgres.DependencyInjection;
using Npgsql;

// On every deploy, as the owner role, before the stores are used. Nothing migrates on its own.
await using (var owner = NpgsqlDataSource.Create(ownerConnectionString))
{
    await ExperienceSchemaMigrator.MigrateAsync(owner, cancellationToken);
    await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
        owner,
        new ExperienceApplicationRoleOptions("agent_experience_app") { AllowErasure = true },
        cancellationToken);
}

// The stores, as the application role.
services.AddSingleton(NpgsqlDataSource.Create(applicationConnectionString));
services.AddAgentExperiencePostgresStore();              // IExperienceRecordStore
services.AddAgentExperiencePostgresCandidateSource();    // IExperienceCandidateSource (text search)
services.AddAgentExperiencePostgresGrantStore();         // optional: only if you share records across scopes
services.AddAgentExperiencePostgresReuseFeedbackStore(); // optional: only if you record reuse feedback
services.AddAgentExperiencePostgresGrantAccessLog(       // optional: a trail of who READ shared records
    onNotRecorded: failure => logger.LogError(failure.Failure, "grant access row not written"));
services.AddAgentExperiencePostgresEncryption(keyStore); // optional: crypto-shredding for every component above

// Core's extensions then supply capture, lifecycle, finalization and retrieval over them.
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
services.AddAgentExperienceRetrieval();
```

Every registration uses `TryAdd`, so your own implementation of a port wins. The store, search, grant, feedback and
access-log registrations also have an overload taking an explicit `NpgsqlDataSource` (useful to give the access log
its own pool). The stores never dispose the data source; the host owns it.

`MigrateAsync` applies `0001`–`0003` and `0005`–`0018` (there is no `0014`; `0004` belongs to the vectors package).
It is journaled, runs one transaction per script, and serializes concurrent hosts with an advisory lock. The owner
needs one superuser grant, once: `GRANT SET ON PARAMETER agent_experience.purge_authorized,
agent_experience.access_purge_authorized TO <owner>`.

## What to know

- **The host is the authority.** Every call takes an `AuthorizationContext` the host built from its own identity
  checks, never from model output. A scope outside it is `Denied` before any connection opens. Scope matching is
  exact in SQL; a null scope field is never a wildcard, and another scope's record reads as `NotFound`.
- **Expected refusals are results, not exceptions.** `NotFound`, `Denied`, `Invalid`, `Conflict`, `StaleRevision`,
  `StatusMismatch`, `Deleted` and so on. Database failures throw `ExperienceStoreException`; the store never logs.
- **Lifecycle changes are atomic and append-only.** The event and the record's new status commit in one transaction,
  checked against the record's revision and idempotent by event ID. Database triggers refuse edits to any ledger.
  They do not bind the table owner or a superuser, which is inherent in PostgreSQL.
- **Sharing is an explicit, expiring grant**, enforced in SQL, capped at 90 days by default
  (`PostgresExperienceGrantPolicy`), read-only, and audited. A grant's disclosure level decides whether a borrowed
  lesson's tool names reach a model.
- **Deletion leaves a tombstone** and removes everything else that named the record, in one transaction. There is no
  default retention and no timer: `SweepExpiredAsync` runs when you call it, one scope exactly unless you pass
  `ScopeMatch.Subtree`. In the default plaintext mode, backups, replicas, WAL and the dead row version still hold the
  erased text (the KL-2 boundary); crypto-shredding makes erasure reach every copy except the derived search data.
- **Erasure is the only part that emits telemetry**, as the `delete`, `retention.sweep`, `grant.purge`,
  `grant.access.purge` and `record.seal` operations on the `AgentExperience.Storage.Postgres` source and meter.

## Guides

| Topic | Guide |
| --- | --- |
| Wiring, the two roles, the privilege manifest, the trust boundary, results and failures | [Deployment](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/deployment.md) |
| Lifecycle commits, supersession, history, append-only guards | [Lifecycle](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/lifecycle.md) |
| Confidence evidence and the independence key | [Confidence and independence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/confidence.md#how-the-postgresql-store-enforces-it) |
| The reuse-feedback ledger | [Reuse feedback](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/reuse-feedback.md#how-the-postgresql-ledger-stores-it) |
| Text search | [Retrieval](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/retrieval.md#the-text-channel) |
| Sharing grants, disclosure levels, the access log | [Sharing and grants](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/sharing.md) |
| Deletion, tombstones, retention sweeps, what erasure does not reach | [Deletion and retention](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/deletion-and-retention.md) |
| Crypto-shredding, key custody, upgrading a plaintext deployment | [Crypto-shredding](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/crypto-shredding.md) |
| Every migration, the migrator's behaviour, data semantics | [PostgreSQL schema](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/postgres-schema.md) |

## More

- Documentation: [guide and glossary](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/README.md)
- Supported versions: [compatibility evidence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/compatibility-evidence.md#supported-matrix)
- Changes between previews: [changelog](https://github.com/fabbrik/AgentExperience.NET/blob/main/CHANGELOG.md)
- License: Apache-2.0
