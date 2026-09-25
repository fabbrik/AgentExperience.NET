# Changelog

AgentExperience.NET is a **preview**. It is not production ready, and public APIs may change between previews. The
[Known limits](README.md#known-limits) table lists every limit that is still unresolved.

## Unreleased

### Upgrade, in this order

Stop the application first and run steps 1 to 3 in one maintenance window: once ownership moves, the application's
role cannot read or write any table until step 3 grants it the manifest.

1. **Create an owner role and move ownership to it**, as a superuser: the SQL is in
   [Store: upgrading an existing single-role database](src/AgentExperience.Storage.Postgres/README.md#upgrading-an-existing-single-role-database).
   It includes the one superuser-only grant a non-superuser owner needs to migrate: `SET` on the parameters
   `agent_experience.purge_authorized` and `agent_experience.access_purge_authorized`. The role your application
   uses today stays the application role, with the same credentials.
2. **Run the schema migrator as the owner.** It applies `0013_role_separation_hardening`, which only pins
   `search_path` on functions and restates a `REVOKE`. No journaled script is edited.
3. **Call `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` as the owner**, after the vectors migrator
   if you use it, and again on every deploy. Set `AllowErasure` if the application deletes records, sweeps
   retention or purges expired grants, and `AllowAccessLogPurge` if it purges the access log. Without them, those
   calls now fail with a permission error.
4. **Start the application again.** From here on the migrator never runs with the application's credentials.

A host that skips all of this keeps working exactly as before, as a single-role deployment.

### Added

- `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` and `ExperienceApplicationRoleOptions`: in one
  transaction, the call does four things.
  - It refuses a missing role, an unmigrated schema, a superuser, the caller itself, any role that is or is a
    member of an owner of the database, the schema or an object in it, and any role that reaches a superuser, a
    server-file role, `pg_maintain` (PostgreSQL 17 and later) or `SET` on `session_replication_role`.
  - It revokes everything the application role holds in the schema.
  - It grants the stores' manifest. Every `UPDATE` is column-level, including on `experience_embeddings`.
  - It verifies the role's effective privileges, including `CREATE` on the schema and grant options, across every
    role the application role can `SET ROLE` to, and rolls back on any difference. On PostgreSQL 17 and later
    that includes `MAINTAIN` on any table in the schema.
- `0013_role_separation_hardening`: `search_path = pg_catalog, agent_experience, pg_temp` on the three purge
  functions and on every guard trigger function.
- **`ExperienceInjectionOptions.ApproachArguments`: selected argument values on the `Approach:` line** (story 6.2,
  KL-8). A host can allowlist, per tool name, the argument keys whose values the injected `Approach:` line shows, for
  example `ApproachArguments = { ["run_incident_check"] = ["strategy"] }`, which renders
  `run_incident_check(strategy="wait-for-lock")`. It is empty by default, and without it the block is byte for byte
  what it was. `HistoricalReferenceWriter.Write` gains an overload that takes the same allowlist.
  - Only keys named for that exact tool are read, and only the value the capture-time sanitizer stored, so a redacted
    value stays redacted.
  - Only a string, number, boolean, null or enum name is shown. An object or array becomes `(not shown: not a string, number or
    boolean)`.
  - A string has invisible characters (per Unicode scalar) and whitespace collapsed and trimmed, its markers
    neutralized, its double quotes turned into single quotes and `->` broken up, and is cut to 64 characters and
    quoted. A line's
    arguments are capped at 512 characters in total, and the byte budget still drops whole records.
  - A record borrowed through a sharing grant never shows an argument value, under either disclosure level. No
    migration is needed.
  - A malformed allowlist is refused when `ExperienceContextProvider` is constructed, and the provider keeps a copy.

### Fixed

- **The store README said the migrator never needs a superuser.** A non-superuser migrating role has always failed
  at `0010`, with "permission denied to set parameter", until a superuser grants it `SET` on the two marker
  parameters. The README now documents that one grant.

### Resolved

- **KL-4: the purge path was auditability, not a privilege boundary** (story 6.1). The two-role deployment is now
  the documented and supported one. An owner role owns the schema and runs the migrators. A separate application
  role, which the stores connect as, owns nothing and holds exactly what the stores need:
  - no `ALTER TABLE`, so it cannot disable a trigger or replace a guard function;
  - no `DELETE`, `TRUNCATE` or `UPDATE` on any ledger, so a purge marker it sets by hand admits nothing;
  - `UPDATE` only on the columns the store moves, so it cannot write a tombstone by hand;
  - `EXECUTE` on the purge functions only when the host opts in.

  The owner role and superusers remain unbound, which is inherent in PostgreSQL. A single-role deployment still
  works for development, but gets none of this.

### Known limits

- **KL-8 is narrowed, not closed.** Approaches that differ by an allowlisted scalar argument now render differently.
  What remains: an object- or array-valued argument still renders as a marker, and a borrowed record shows no
  argument value (names only under `LessonAndApproach`, no `Approach:` line under `LessonOnly`).

### Reuse baseline

- The reference experiment no longer registers a host `WorkingApproachReflector`. The learning phase runs the shipped
  `DefaultExperienceReflector` alone, and the trials allowlist the `strategy` argument instead. The three golden
  reports changed only in prose: the learned-records line now reads the strategy off the record's final attempt, and
  the paragraph explaining what the harness supplies names the allowlist rather than a reflector. No number moved:
  every trial, mean, verdict, experience ID and confidence is identical. A new test shows the allowlist is
  load-bearing: with it off, the agent reads no strategy from the block and the harness refuses to report reuse.

**Story 6.3** widens the supported matrix to everything CI can prove, and narrows KL-13 to what it cannot (see the
[supported matrix](docs/compatibility-evidence.md#supported-matrix)).

### Supported matrix

- **Target frameworks: `net9.0` and `net10.0`.** Every package ships both builds and declares the same dependencies
  for each; the public API is identical on both, and one baseline per assembly gates it. `net8.0` is not targeted:
  its `System.Text.Json` lacks `JsonElement.DeepEquals` and the strict payload-decoding options the store relies on.
  .NET 9 leaves support on 10 November 2026, and the first preview after that drops `net9.0`.
- **PostgreSQL 15, 16, 17 and 18.** CI runs the store, vector, proof and sample suites on each major (the store, vector
  and proof suites on both frameworks, the sample on `net10.0`). PostgreSQL 14 is not supported: migration `0005`
  uses `NULLS NOT DISTINCT`, which needs 15.

### Dependency policy

- **Every dependency except MAF is now a floor, not an exact pin.** `Microsoft.Extensions.DependencyInjection.Abstractions`
  10.0.12, `Microsoft.Extensions.Compliance.Redaction` 10.10.0, `Microsoft.Extensions.AI.Abstractions` 10.10.0,
  `Npgsql` 10.0.3, `dbup-postgresql` 7.0.1, `dbup-core` 6.1.1 and `Pgvector` 0.3.2 are declared `>=` with no upper
  bound. A host that needs a newer release of any of them, or a MAF that raises one, no longer gets a restore conflict.
- **What is tested:** each floor itself, and the newest release in its major (the same minor for `Pgvector`), on
  every change. The second is the new `floating-dependencies` CI job, `eng/probe-floating-dependencies.sh`. It
  gates pushes and the weekly schedule, and reports without blocking on pull requests. A later major restores but
  is not claimed.
- **`Microsoft.Agents.AI` stays exact at `[1.22.0]`.** MAF ships a minor every week or two, has changed
  adapter-visible behaviour between minors, and keeps `[Experimental]` surface next to the adapter's hooks. The MAF
  probe still reports the newest version without blocking.

### For contributors

- A full local test run needs the .NET 9 runtime beside the pinned SDK. `AGENTEXPERIENCE_POSTGRES_MAJOR` selects the
  PostgreSQL major the container tests start (16 by default; 15 to 18 are accepted, and anything else fails).
- `RELEASING.md` step 3 runs the container suites on every supported major, and step 6 runs the floating-dependency
  probe as a release blocker.

## 0.1.0-preview.2

This preview resolves ten known limits: KL-1, KL-3, KL-5, KL-6, KL-7, KL-9, KL-10, KL-14, KL-15 and KL-16. The six
still in the table (KL-2, KL-4, KL-8, KL-11, KL-12, KL-13) are boundaries of the design, and code alone cannot remove
them.

### Upgrade, in this order

1. **Stop every writer running `0.1.0-preview.1`.** Once `0011` is applied, an old build can no longer write grant
   events or access rows.
2. **Run the schema migrator.** It applies `0011_grant_disclosure` and `0012_grant_access_retention`. Neither edits a
   journaled script, and `0012`'s header describes building its index out of band first, for large tables.
3. **Deploy `0.1.0-preview.2`.** Against a schema without `0011`, this build fails every grant-joined read with
   `42703`.

### Breaking changes

- **`RequiredCheck` must name an evidence kind** (KL-5). `new RequiredCheck("id")` no longer compiles. To accept any
  kind, pass `RequiredCheck.AnyKind`.
- **An evaluation is bound to its run** (KL-6).
  - `VerificationAggregator.Aggregate` now takes `runId` as its first argument.
  - `VerificationResult` can only be produced by the aggregator: it has no public constructor, and its properties are
    get-only.
  - `ReflectionRequest.Run` and `ReflectionRequest.Evaluation` are get-only. A mismatched pair throws
    `ReflectionBindingException`.
  - Finalization quarantines a reflection that does not match its request.

### Behaviour changes

- **Existing sharing grants become `LessonOnly`** (story 3.6, KL-9). A borrowed record loses its `Approach:` line
  until its owner revokes the grant and issues it again as `LessonAndApproach`. The recipient has no access between
  the two calls.
- **Every run is bounded from the moment it opens** (KL-7). This includes the default single-invocation path.
  - An invocation still running after one `MaxOpenRunDuration` period (5 minutes by default) is handed the bound.
  - At twice that duration, its run is completed as `Cancelled` and the closure is reported. The invocation's answer
    is untouched, but its attempt is refused.
- **Retention sweeps count only what they erase.** `DeletedCount` no longer includes records that another caller
  erased first.
- **The injection eligibility check re-checks its time limit before each record and after the last decision**, from
  elapsed time as well as its timer. A slow host decision can no longer slip a record in after the limit.

### Added

- **A grant disclosure level:** `ExperienceGrantDisclosure`, recorded on grants, grant events and every access row
  (KL-9).
- **Retention for subtrees and for the access log** (KL-3, KL-10).
  - `ScopeMatch.Subtree` for retention sweeps.
  - `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync`, which keeps every access row for at least 30 days.
- **Batch reads and embeddings** (KL-1), both as default interface methods, so no implementer breaks.
  - `IExperienceRecordStore.GetManyAsync`.
  - `IExperienceEmbeddingGenerator.GenerateBatchAsync`, with `ReindexExperienceRequest.EmbeddingBatchSize`.
  - One injection re-read now takes 2 database commands instead of 12, for 8 candidates.
- **Telemetry for erasure** (KL-16): `delete`, `retention.sweep`, `grant.purge` and `grant.access.purge`, on the
  `AgentExperience.Storage.Postgres` source and meter.

### Dependencies

These moved (KL-14, KL-15), and every `PackageReference` a shipping project declares is now exact:

| Package | From | To |
| --- | --- | --- |
| `Microsoft.Agents.AI` | `1.20.0` | `1.22.0` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.11` | `10.0.12` |
| `Microsoft.Extensions.Compliance.Redaction` | `10.9.0` (floor) | `[10.10.0]` (exact) |
| `Microsoft.Extensions.AI.Abstractions` | `10.9.0` | `10.10.0` |

## 0.1.0-preview.1

The first preview: Epics 1–4. See the
[release](https://github.com/fabbrik/AgentExperience.NET/releases/tag/v0.1.0-preview.1).
