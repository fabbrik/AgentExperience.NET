# Contract 1.4: additions

This page lists what contract 1.4 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md) and [contract 1.3](README-1.3.md). Those pages are still normative and unchanged: a minor
version only adds files, this page among them. The contract's version is the one `lock.json` records
(`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.4 passes every 1.0, 1.1, 1.2 and 1.3 fixture and the fixtures below.

## What 1.4 adds

- **sql:** [`record-injection.sql`](sql/record-injection.sql), the statement that records an injected block, and
  [`application-role-privileges-1.4.sql`](sql/application-role-privileges-1.4.sql), the application role's grants on
  the table it writes.
- **record-injection:** a new fixture family (needs PostgreSQL): `repeat-and-earlier-instant`, `own-scope-only`,
  `clamped-to-the-database-clock`, `saturated-count` and `erased-records`.

Contract 1.0 left the lessons view's "last injected" to .NET: `agent_experience.experience_injections` (migration
`0026`) holds, per record, when it was last injected in its own scope and how many times. From 1.4 a port records its
injections there too, so a lesson only a Python or Go agent uses no longer reads "never". The record itself, the block
and the search are unchanged.

## Recording an injection

- **When.** After a block of records is handed over to the model call, once per block, naming the records the block
  delivered (not the withdrawal notices, and nothing when the block is empty or not built). Never before the block is
  handed over, so a block that is not delivered is never counted.
- **Off the critical path.** The write runs in the background and is bounded (the .NET adapter allows it five seconds,
  all chunks together). The injection never waits for it, and nothing it does can delay, change or fail the injection.
- **Fail-open.** Any failure (no grant, no table, a timeout, a refused scope, an error) is reported through the port's
  own telemetry or callback and otherwise ignored.
- **Own scope only.** The statement runs in a transaction that declared the bounds for the injecting agent's scope
  (`declare-bounds.sql`), with the same scope as its parameters. It counts only records live in exactly that scope; a
  record borrowed through a grant, or in any other scope, is skipped by the statement, so a port may name every record
  it injected.
- **Chunks.** At most 200 IDs per statement (the .NET store's limit, `ExperienceRecordGetManyResult.MaxCount`); a
  longer list is split into chunks run one after another, each its own statement and transaction.
- **The instant** is the injection's, as the port's clock reads it, truncated to whole microseconds in UTC. The
  statement clamps it to the database clock and keeps the latest.
- **Encrypted mode** is .NET's: before the statement the .NET store drops from the IDs every record whose key is
  destroyed. A port has no key store and never reads a sealed row, so it never injects one; for a port that step is a
  no-op.

**Outcomes.** A port reports one of four, as the .NET adapter reports its `record_injection` operation:

| Outcome | When |
| --- | --- |
| `Recorded` | every chunk's statement committed |
| `NotTracked` | the store does not record injections (an in-memory or custom store without it) |
| `NotRecorded` | the store refused or failed: no grant (`42501`), no table (`42P01`), a denied scope, or any other error |
| `OutcomeUnknown` | the write ran out of time or was cancelled; it may still commit |

## The application role's grants

[`application-role-privileges-1.4.sql`](sql/application-role-privileges-1.4.sql) is run after the 1.0 file
[`application-role-privileges.sql`](sql/application-role-privileges.sql), whose `REVOKE ALL` removes them, in the same
way: as the schema's owner, with the role's name in place of `:"application_role"`, the statements under `-- mode:
always` and, with row-level security on, those under `-- mode: row-level-security`. The two files together are exactly
what `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` grants on a schema migrated to `0026` or later. So
the documented order for a mixed fleet is: the 1.0 file then the 1.4 file, or the .NET call (which grants both). A port
on a schema given only the 1.0 file still injects; it reports each injection `NotRecorded`.

## The record-injection fixtures

```
fixtures/record-injection/<case>/   seed.json + steps.json -> expected.json (needs PostgreSQL)
```

Against a database the .NET migrator applied, with both privileges files applied (row-level security on):

1. Insert `seed.json`'s `records` as a search seed is inserted (the eighteen record columns, as the application role,
   under bounds declared for each record's scope), then its `injections` rows as the schema's owner, each value cast
   from PostgreSQL's text form to its column's type.
2. Read the database clock (`clock_timestamp()`), then run `steps.json`'s `steps` in order:
   - `record`: in one transaction as the application role, `declare-bounds.sql` for `scope` (each bound the scope's
     field), then `record-injection.sql` with `experienceIds`, `injectedAt` (truncated to whole microseconds) and
     `scope`. A port runs it through its own store's record call.
   - `erase`: as the schema's owner, `SELECT purge_outcome FROM agent_experience.purge_experience_record(experienceId,
     the scope's six fields, NULL, deletedAt)`, which answers `Deleted`.
3. Read the database clock again, then every row of `agent_experience.experience_injections`, ordered by
   `experience_id`, each column as `::text` in the time zone UTC, and compare them with `expected.json`'s
   `experience_injections`. A `last_injected_at` of `database-clock` is an instant the clamp chose: it must lie between
   the two clock readings, and is not compared as text.
