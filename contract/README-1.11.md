# Contract 1.11: additions

This page lists what contract 1.11 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md), [contract 1.4](README-1.4.md),
[contract 1.5](README-1.5.md), [contract 1.6](README-1.6.md), [contract 1.7](README-1.7.md),
[contract 1.8](README-1.8.md), [contract 1.9](README-1.9.md) and [contract 1.10](README-1.10.md). Those pages are
still normative and unchanged: a minor version only adds files, this page among them. The contract's version is the one
`lock.json` records (`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.11 passes every 1.0 to 1.10 fixture and the fixtures below.

## Measuring another memory provider

Up to 1.10 only .NET could measure a memory provider that is not this library (Mem0, Zep, Letta, the host's own): it
wraps the provider in `ExperienceHoldoutContextProvider` (`UseMeasuredMemory`), withholds the provider's context on a
deterministic share of captured runs, records each run's arm, and `AgentExperienceBenefit.EstimateProviderAsync`
compares the arms' verified outcomes ([Injection: measuring another memory
provider](../docs/guide/injection.md#measuring-another-memory-provider)). From 1.11 a port does the same around its own
framework's context provider: it assigns a run to the arm .NET would, withholds what .NET withholds, records the rows
.NET reads, and estimates from them. In a mixed fleet sharing one PostgreSQL store, the runs of every language count
in one provider estimate. Every earlier page's remark that measuring another memory provider is .NET-only is superseded
by this one.

A port that does not implement 1.11 keeps passing 1.10, and its runs are invisible to the provider estimate.

## What 1.11 adds

- **SQL:** [`sql/record-provider-holdout-assignment.sql`](sql/record-provider-holdout-assignment.sql), the statement
  that records a run's arm; [`sql/query-provider-holdout-outcomes.sql`](sql/query-provider-holdout-outcomes.sql), the
  read of one provider's rows, a page at a time; and
  [`sql/application-role-privileges-1.11.sql`](sql/application-role-privileges-1.11.sql), the application role's grants
  on the table they use.
- **Fixtures:** `provider-holdout-assign` (no database), `provider-holdout-record` (PostgreSQL; an in-memory store runs
  it too, see below) and `provider-benefit-estimate` (no database).

Nothing else changes: no schema object is added (the table is .NET migration `0029`'s,
`agent_experience.experience_provider_holdout_assignments`, with its erasure trigger and row-level security policies),
and every existing file is byte-identical. The statements are the .NET `PostgresExperienceRecordStore`'s
(`RecordProviderHoldoutAssignmentSql` and `QueryProviderHoldoutOutcomes*`), line for line: a contract test compares
each with the .NET text. A port never creates or alters schema.

## The key and the options

- **The provider key** is the host's name for the provider (`mem0`, say), the key the estimate reads by: 1 to 100
  characters, each an ASCII letter or digit, `.`, `_` or `-`. A wrapper or an estimate given any other key refuses it
  when it is built or called (.NET throws `ArgumentException`), before anything is read or written.
- **The rate and salt** are 1.8's holdout options, with 1.8's defaults (rate `0`, off; salt
  `agentexperience-holdout-v1`) and validation. .NET's one-call wiring (`UseMeasuredMemory`) takes the injection
  holdout's rate and salt when given none; a port may do the same.

## The assignment

1.8's assignment function, over the run ID and the **provider salt**: the key's length in UTF-16 code units (for a
valid key, its length in characters), in decimal, then `:`, the key, `/`, and the holdout's salt; .NET
`"{providerKey.Length}:{providerKey}/{Salt}"`. The length prefix gives every (key, salt) pair its own salt, so each
provider is randomised independently of the lessons' holdout and of every other provider, and both holdouts can run on
the same agent. Every invocation of the same run under the same key lands in the same arm.

## The wrapper

A port wraps a context provider of its own framework (the README of each port says which and how) under a key and a
holdout. In order, exactly as .NET's
[`ExperienceHoldoutContextProvider`](../src/AgentExperience.MicrosoftAgentFramework/Injection/ExperienceHoldoutContextProvider.cs):

1. **The wrapped provider always runs.** On every invocation, in both arms and whether or not the run takes part, the
   wrapped provider's pre-invocation hook is called, and so is its post-invocation hook after the model has run: it
   keeps learning from every run. Its session state is its own, exactly as without the wrapper. Its exceptions
   propagate unchanged, and nothing is assigned or recorded for an invocation whose hook threw.
2. **At rate 0** the wrapper is a pass-through: the provider's answer is delivered, nothing is inspected, assigned or
   recorded, and no telemetry is emitted.
3. **Who takes part.** Only an invocation that is a captured run of this very agent (the condition under which the
   port records the run's injected block as its exposure, 1.8), that has an authorization to record under (the
   identity the port's capture resolved for the run, or a host callback the port offers; with neither, the run does not
   take part), and whose wrapped provider **added** something: an instruction (non-empty and not the one it was
   handed), or a message or tool it was not handed. Every other invocation, a run with no capture (no run ID) among
   them, gets exactly what the wrapped provider answered and is assigned nothing.
4. **The arm** is decided after the wrapped provider answered: the assignment above, for the captured run's ID.
5. **Holdout.** Nothing the wrapped provider added reaches the model: the invocation sees exactly the context it would
   see without the provider. **Treatment.** What the wrapped provider added is delivered, exactly as without the
   wrapper.
6. **The caller's context is never changed by the wrapper.** In a framework whose providers add to a shared context in
   place, the port gives the wrapped provider a context of its own to add to, and copies the additions into the
   invocation's context only in the treatment arm (or when the run does not take part).
7. **A failure while inspecting** the provider's answer (anything that throws after it answered, before the arm is
   decided) delivers the answer unchanged, records nothing, and is reported as the `record_provider_holdout` operation
   with the outcome `NotRecorded` and the error class `Unexpected`.

**Recording** follows 1.8's rules for a holdout assignment: in the background, after the arm is decided, never
delaying, changing or failing the invocation; one write, bounded by the port's injection record timeout; fail-open,
with the outcome reported as the telemetry operation **`record_provider_holdout`** (no span), whose outcomes are
[`docs/telemetry-names.json`](../docs/telemetry-names.json)'s: `Recorded`, `NotTracked` (the store does not record
provider holdout assignments), `NotRecorded` (it refused or failed: no grant, no table, a denied scope, any error;
error class `Infrastructure` for a store failure, `Unexpected` otherwise, none for a refusal) and `OutcomeUnknown`
(it ran out of time: `Timeout`; it was cancelled: `Cancelled`). The run's outcome the estimate compares is the run's
own finalized record, as in 1.9: the wrapper records only the arm.

## Recording an assignment

In one transaction as the application role: `declare-bounds.sql` for the run's scope, then
`record-provider-holdout-assignment.sql` with that scope, the run, the key, the arm (`Treatment` or `Holdout`) and the
instant truncated to whole microseconds in UTC. The statement:

- writes one row for the run and key, its scope exactly the run's (no record is read for it, and a run with no record
  yet is written);
- keys the row on (`tenant_id`, `run_id`, `provider_key`): a row that already exists for them is left as it is,
  whatever its scope, so a run invoked again keeps its first row, and another tenant's reuse of a run ID never
  suppresses this tenant's row;
- clamps the instant to the database clock;
- writes nothing when the run's own records (those whose `source_run_id` is the run, in the caller's tenant) are all
  erased: 1.8's late-write guard.

Erasing a record (as the owner, `agent_experience.purge_experience_record`) removes every row of the run it was
captured from, in its own tenant, when no other live record of that run remains in that tenant; a record that is no
run's own record removes nothing.

## Reading the rows

The provider estimate reads one key's rows in exactly one scope (the scope the runs were captured in), each joined to
its run's outcome, through [`sql/query-provider-holdout-outcomes.sql`](sql/query-provider-holdout-outcomes.sql): in a
transaction that declared the caller's bounds, run `first-page` with `@limit`, then, while a page is full (it has
`@limit` rows), `next-page` with `@after_run_id` set to the previous page's last row's run. .NET reads 1,000 rows a
page; a port reads at most that many. Each page is its own transaction. Each row is one run: columns 1 to 18 are the
run's own record (the live record in exactly the scope whose `source_run_id` is the run; the earliest created, then the
lowest ID), all NULL when there is none, and columns 19 to 21 the row's `run_id`, `arm` and `assigned_at`. A port turns
it into a **provider outcome row**:

| Field | Value |
| --- | --- |
| `runId`, `arm`, `assignedAt` | the row's columns |
| `providerKey` | the key read |
| `runOutcome` | the run's record's `payload.outcome.status`; `null` when the run has no record, or its record is sealed (`payload_version` 2), as 1.9 reads a run's outcome |

**An in-memory store** answers the same pages from what it holds: the key's rows in exactly the scope, in `runId`
order compared as UUIDs (1.9), after the cursor, at most the limit, each joined to the record in exactly the row's
scope whose source run is the run (the earliest created, then the lowest ID).

## The estimate

`EstimateProviderAsync(store, authorization, scope, providerKey, options)` in .NET; each port names it in its own idiom
(`estimate_provider_benefit` in Python, `EstimateProviderBenefit` in Go) and maps every field one to one in its README.

1. **The call.** The key is checked first (above), then the options as 1.9 checks them. A store that does not record
   provider holdout assignments gives an empty estimate with `tracked` false. A scope outside the authorization reads
   nothing and gives an empty estimate whose outcome is `Denied`, never an exception. Otherwise the outcome is `Found`
   and `tracked` true.
2. **Paging**, as 1.9 pages: each page is folded before the next is read, and a page whose last run equals the cursor
   it was read after ends the read.
3. **The fold.** Each row is its run (the table holds one row per run and key in a tenant): it is added to 1.9's
   estimator as an outcome row of that run and arm, with no lesson (.NET passes the nil UUID as its `experienceId`),
   and `runOutcome` as its outcome. The estimate is 1.9's `overall`, with 1.9's rules for the arms' counts, `pending`,
   `unverified`, `mixedArm`, the rates, the interval and the verdict. There is no per-item comparison: the provider's
   items are opaque.
4. **The result:** `providerKey`, `overall` (a 1.9 comparison), `minimumRunsPerArm`, `tracked` and `outcome`.

It changes nothing about ranking, confidence or injection, and it never writes.

## The application role's grants

[`application-role-privileges-1.11.sql`](sql/application-role-privileges-1.11.sql) is run after the 1.0, 1.4, 1.6 and
1.8 files, in the same way: as the schema's owner, with the role's name in place of `:"application_role"`, the
statements under `-- mode: always` and, with row-level security on, those under `-- mode: row-level-security`. It is
exactly what `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` grants on migration `0029`'s table. A port
on a schema given only the earlier files still runs its wrapper and withholds; it reports each assignment
`NotRecorded`, and its provider estimate fails with its store failure (.NET throws `ExperienceStoreException`).

## The fixtures

```
fixtures/provider-holdout-assign/cases.json      run ID + key + salt -> provider salt, fraction, and the arm at each rate
fixtures/provider-holdout-record/<case>/         seed.json + steps.json -> expected.json (needs PostgreSQL)
fixtures/provider-benefit-estimate/<case>/       rows.json + options.json -> expected.json
```

**provider-holdout-assign.** For each case, derive the provider salt of `providerKey` and `salt` and compare it with
`providerSalt` exactly; compute the fraction of `runId` and that salt and compare it with `fraction` to 15 significant
digits; then, for each entry of `arms`, the arm at `rate` must be `arm`. `validKey` says whether the key is a valid
provider key, which a port's key check must agree with. The pairs whose plain `key/salt` would collide (`a/b` + `c`
against `a` + `b/c`, `a:b` + `c` against `a` + `b:c`) use keys a wrapper refuses; their derivation is pinned all the
same.

**provider-holdout-record.** Against a database the .NET migrator applied, with the 1.0, 1.4 and 1.11 privileges files
applied (row-level security on):

1. Insert `seed.json`'s `records` as a search seed is inserted (the eighteen record columns, as the application role,
   under bounds declared for each record's scope).
2. Read the database clock (`clock_timestamp()`), then run `steps.json`'s `steps` in order:
   - `record`: the port's own record call, with `scope` (which is also the caller's bounds), `runId`, `providerKey`,
     `arm` and `assignedAt`; it runs `record-provider-holdout-assignment.sql` as above.
   - `erase`: as the schema's owner, `SELECT purge_outcome FROM agent_experience.purge_experience_record(experienceId,
     the scope's six fields, NULL, deletedAt)`, which answers `Deleted`.
   - `eraseDuringRecord`: the late-write guard's race, staged exactly as 1.8's `holdout-record` stages it.
3. Read the database clock again, then every row of `agent_experience.experience_provider_holdout_assignments`,
   ordered by `tenant_id`, `run_id` then `provider_key`, each column as `::text` in the time zone UTC, and compare them
   with `expected.json`'s `experience_provider_holdout_assignments`. An `assigned_at` of `database-clock` is an instant
   the clamp chose: it must lie between the two clock readings, and is not compared as text.

A store that is not PostgreSQL runs the same cases in memory: its record call keeps the same rows (keyed on tenant, run
and key, the first row kept, the scope the caller's, the instant clamped to its own clock, `database-clock` meaning
that clock). An `erase` step tombstones the record with the trigger's effect: the rows of its run in its tenant go when
no other live record of that run remains there. An `eraseDuringRecord` step is its `erase` then its `record`, and the
in-memory record call applies the guard: it writes nothing for a run that has records in the tenant and all of them
are tombstones (an in-memory tombstone may keep its run).

**provider-benefit-estimate.** `rows.json` holds `providerKey` and `rows`, provider outcome rows as above, in the order
a store pages them (by `runId`); `options.json` holds `minimumRunsPerArm`. Estimate the rows (feeding them through the
paged form, a page at a time, is the stronger check) and compare with `expected.json`: `providerKey`,
`minimumRunsPerArm` and `overall`, a 1.9 comparison, compared as 1.9's `benefit-estimate` compares it: rates,
differences and bounds within an absolute tolerance of 1e-12, everything else exactly.

**Not fixtured.** The wrapper's behaviour (above) is each port's own tests, against a stub provider: the treatment and
holdout arms, the pass-through at rate 0 and for an uncaptured run, a recording failure that leaves the invocation
unaffected, a wrapped provider that throws, and the context passed in left unchanged. So are a store that does not
record provider holdout assignments, a scope outside the authorization, a malformed key, and the options' validation.
