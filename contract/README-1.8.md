# Contract 1.8: additions

This page lists what contract 1.8 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md), [contract 1.4](README-1.4.md),
[contract 1.5](README-1.5.md), [contract 1.6](README-1.6.md) and [contract 1.7](README-1.7.md). Those pages are still
normative and unchanged: a minor version only adds files, this page among them. The contract's version is the one
`lock.json` records (`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.8 passes every 1.0 to 1.7 fixture and the fixtures below.

## The injection holdout

The .NET library can measure whether its lessons help: on a deterministic share of captured runs that have a block to
inject, the block is withheld, both arms are recorded, and `AgentExperienceBenefit.EstimateAsync` compares the runs'
verified outcomes ([Injection: measuring benefit](../docs/guide/injection.md#measuring-benefit-the-holdout)). From 1.8 a
port takes part: it assigns a run to the same arm .NET would, withholds the block the same way, and records the
assignment rows .NET reads. In a mixed fleet sharing one PostgreSQL store, the runs of every language then count in one
estimate. The estimate itself, the benefit policy, measuring another memory provider, exporting lessons and recording
the run's model stay .NET-only.

A port that does not implement 1.8 keeps passing 1.7, and its runs are invisible to the estimate.

## What 1.8 adds

- **SQL:** [`sql/record-holdout-assignment.sql`](sql/record-holdout-assignment.sql), the statement that records a run's
  assignment, and [`sql/application-role-privileges-1.8.sql`](sql/application-role-privileges-1.8.sql), the
  application role's grants on the table it writes.
- **Fixtures:** `holdout-assign` (no database) and `holdout-record` (PostgreSQL; an in-memory store runs it too, see
  below).

Nothing else changes: no schema object is added (the table is .NET migration `0027`'s, with `0028`'s index), and every
existing file is byte-identical. The statement is the .NET `PostgresExperienceRecordStore.RecordHoldoutAssignmentSql`,
line for line: a contract test compares it with the .NET text.

## The option

The holdout is off by default. A port exposes it as its idiom has it, with the .NET defaults:

| Setting | Default | Valid |
| --- | --- | --- |
| rate | `0` (off) | a number from 0 to 1; NaN, below 0 or above 1 is refused when the integration is built |
| salt | `agentexperience-holdout-v1` | any non-empty string; empty is refused when the integration is built |

At rate 0 a port behaves exactly as without 1.8: no assignment is decided, no row is written, and the telemetry is
unchanged.

## The assignment

A pure function of the run ID, the salt and the rate:

1. **The fraction.** SHA-256 over the run ID's 16 bytes in RFC 4122 order (as the UUID is written, the same order the
   1.0 ID derivation uses), followed by the salt's UTF-8 bytes. The digest's first 8 bytes, read as a big-endian
   unsigned 64-bit integer and divided by 2^64 as a double, give a fraction in [0, 1].
2. **The arm.** `Holdout` when the rate is at least 1, or the fraction is strictly below the rate; otherwise
   `Treatment`. At rate 0 every run is `Treatment` (and, above, no run is assigned at all).

Every invocation of the same run lands in the same arm. A different salt draws different arms.

## The injection path

The holdout changes only what happens between building the block and handing it over. In order, exactly as .NET
([`ExperienceContextProvider.cs`](../src/AgentExperience.MicrosoftAgentFramework/Injection/ExperienceContextProvider.cs)):

1. **Who takes part.** Only an invocation that is a captured run of this very agent (the condition under which a port
   records an injected block as the run's exposure), whose built block names at least one record. Every other
   invocation is injected as before and assigned nothing.
2. **When.** After the block is built (so what is withheld is exactly what would have been delivered) and before
   anything is staged in the session or handed over. The assignment's records are the records the block names, each at
   the revision it was rendered from; its instant is the port's clock then.
3. **Holdout.** No block is handed over, nothing is staged, no exposure is recorded on the run, no injection is
   recorded (README-1.4), and the result's outcome is `Withheld`, naming the withheld records. **Withdrawal notices are
   never withheld**: when the invocation owes any, the block is rebuilt with the notices alone and delivered, staged as
   any block is, with the outcome `Retracted`, its retracted records, and the withheld records still named; still no
   exposure and no injection record. The assignment is recorded either way.
4. **Treatment.** The block is delivered exactly as before (exposure, injection record), and the assignment is recorded
   too.

**Recording** follows README-1.4's rules for an injection record: in the background, after the arm is decided, never
delaying, changing or failing the invocation; in chunks of at most 200 records, each its own statement and
transaction; fail-open, with the outcome reported as the telemetry operation **`record_holdout`**, whose outcomes are
.NET's: `Recorded`, `NotTracked` (the store does not record the holdout), `NotRecorded` (it refused or failed: no grant,
no table, a denied scope, any error) and `OutcomeUnknown` (it ran out of time or was cancelled). The injection's own
outcome `Withheld` joins the `inject` operation's outcomes.

## Recording an assignment

In one transaction as the application role: `declare-bounds.sql` for the injecting agent's scope, then
`record-holdout-assignment.sql` with that scope, the run, the arm (`Treatment` or `Holdout`), the records and their
revisions (a record named twice counts once, at the revision first named), and the instant truncated to whole
microseconds in UTC. The statement:

- writes one row per named record that is live in exactly the scope, its scope copied from the record: a record in
  another scope, an erased one or an unknown ID gets none;
- keeps the revision named, not the record's current one;
- leaves a row that already exists for the run and record as it is, so a run invoked again keeps its first rows;
- clamps the instant to the database clock;
- writes nothing when the run's own records (those whose `source_run_id` is the run, in the caller's tenant) are all
  erased: the late-write guard. A run with no record yet, or one still live, is written.

Erasing a record (as the owner, `agent_experience.purge_experience_record`) removes every row that names it as a
lesson, and, when it was the run's last live record in its tenant, every row of the run it was captured from.

## The application role's grants

[`application-role-privileges-1.8.sql`](sql/application-role-privileges-1.8.sql) is run after the 1.0, 1.4 and 1.6
files, in the same way: as the schema's owner, with the role's name in place of `:"application_role"`, the statements
under `-- mode: always` and, with row-level security on, those under `-- mode: row-level-security`. It is exactly what
`ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` grants on migration `0027`'s table. A port on a schema
given only the earlier files still injects and withholds; it reports each assignment `NotRecorded`.

## The fixtures

```
fixtures/holdout-assign/cases.json      run ID + salt -> fraction, and the arm at each rate
fixtures/holdout-record/<case>/         seed.json + steps.json -> expected.json (needs PostgreSQL)
```

**holdout-assign.** For each case, compute the fraction of `runId` and `salt` and compare it with `fraction` to 15
significant digits; then, for each entry of `arms`, the arm at `rate` must be `arm`. Each case's last rate is its own
fraction, which pins "strictly below".

**holdout-record.** Against a database the .NET migrator applied, with the 1.0, 1.4, 1.6 and 1.8 privileges files
applied (row-level security on):

1. Insert `seed.json`'s `records` as a search seed is inserted (the eighteen record columns, as the application role,
   under bounds declared for each record's scope).
2. Read the database clock (`clock_timestamp()`), then run `steps.json`'s `steps` in order:
   - `record`: the port's own record call, with `scope` (which is also the caller's bounds), `runId`, `arm`, `records`
     (each `experienceId` with its `revision`) and `assignedAt`; it runs `record-holdout-assignment.sql` as above.
   - `erase`: as the schema's owner, `SELECT purge_outcome FROM agent_experience.purge_experience_record(experienceId,
     the scope's six fields, NULL, deletedAt)`, which answers `Deleted`.
   - `eraseDuringRecord`: the late-write guard's race. Run its `erase` as the owner in a transaction, and do not commit
     it yet; start its `record` concurrently (another connection), and wait until that record's backend is blocked by
     the erasing one (the erasing backend's `pg_backend_pid()` is in `pg_blocking_pids(pid)` of a backend of the same
     database, read from `pg_stat_activity`); then commit the erasure and wait for the record call to complete. The
     record call must not complete before the commit.
3. Read the database clock again, then every row of `agent_experience.experience_holdout_assignments`, ordered by
   `run_id` then `experience_id`, each column as `::text` in the time zone UTC, and compare them with `expected.json`'s
   `experience_holdout_assignments`. An `assigned_at` of `database-clock` is an instant the clamp chose: it must lie
   between the two clock readings, and is not compared as text.

The guard only sees an erasure that overlaps the write: a tombstone's `source_run_id` is zeroed, so a write that starts
after the erasure of the run's record has committed cannot tell, and records its rows (the .NET known limit; such rows
never join an outcome and count as pending). The fixtures do not pin that case.

A store that is not PostgreSQL runs the same cases in memory: its record call keeps the same rows (only records in
exactly the scope, live ones only, first row kept, the instant clamped to its own clock, `database-clock` meaning that
clock). An `erase` step tombstones the record with the trigger's effect: the rows naming it go, and the rows of its run
when no other live record of that run remains in its tenant. An `eraseDuringRecord` step is its `erase` then its
`record`, and the in-memory record call applies the guard: it writes nothing for a run that has records in the tenant
and all of them are tombstones (an in-memory tombstone may keep its run).
