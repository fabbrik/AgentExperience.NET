# Contract 1.9: additions

This page lists what contract 1.9 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md), [contract 1.4](README-1.4.md),
[contract 1.5](README-1.5.md), [contract 1.6](README-1.6.md), [contract 1.7](README-1.7.md) and
[contract 1.8](README-1.8.md). Those pages are still normative and unchanged: a minor version only adds files, this page
among them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`.

A port that passes contract 1.9 passes every 1.0 to 1.8 fixture and the fixtures below.

## Reading the benefit estimate

From 1.8 a port's agents take part in the injection holdout, but only .NET could read what it measured. From 1.9 a port
reads it too: the same estimate `AgentExperienceBenefit.EstimateAsync` gives
([Injection: reading the estimate](../docs/guide/injection.md#reading-the-estimate)), overall, per lesson and per model,
from the same rows, with the same numbers. It also records the run's model, so a split by model counts its runs. The
benefit policy (acting on the estimate), measuring another memory provider and exporting lessons stay .NET-only.

A port that does not implement 1.9 keeps passing 1.8.

## What 1.9 adds

- **SQL:** [`sql/query-holdout-outcomes.sql`](sql/query-holdout-outcomes.sql), the read of the holdout's outcome rows,
  a page at a time. No privileges file: the 1.0 and 1.8 privileges already grant everything it reads.
- **Fixtures:** `benefit-estimate` (no database), `holdout-outcomes` (PostgreSQL; an in-memory store runs it too, see
  below) and `model-metadata` (no database).

Nothing else changes: no schema object is added, and every existing file is byte-identical. The statements are the
.NET `PostgresExperienceRecordStore.QueryHoldoutOutcomesAsync` statements, line for line: a contract test compares each
with the .NET text.

## The run's model

When an agent's chat client reports a model id, capture records it in the run's environment metadata under the key
`agentexperience.model` (.NET `HoldoutOutcomeRow.ModelMetadataKey`), so a record names the model that learned it and a
run's own record names the model that used its lessons:

- **Normalized.** The id is trimmed of white space at both ends, as .NET's `String.Trim` trims (`char.IsWhiteSpace`:
  U+0009 to U+000D, U+0020, U+0085, U+00A0 and the Unicode space, line and paragraph separators; not U+001C to U+001F),
  then cut to at most 256 UTF-16 code units, one fewer when the 256th is a high surrogate, and trimmed at the end again.
  An absent, empty or all-white-space id records nothing.
- **The host's value wins.** When the environment the host configured already has the key, its value is kept as it is,
  even a blank one, and nothing is added.
- **Only what the client reports.** Nothing is inferred from text or responses. A port reads the id where its framework
  keeps the chat client's model (the README of each port says where); with none, the key is absent.
- **Bookkeeping only.** The key is ignored everywhere but the estimate, exactly as .NET ignores it: the deterministic
  reflector never lists it as a precondition, the block's environment line never shows it, and retrieval drops it from
  a request's required and preferred environment attributes and from a record's metadata when it scores environment
  compatibility. A lesson learned on one model therefore reads, ranks and renders exactly the same for an agent on any
  other.

**Conformance.** The `model-metadata` fixture pins the key, the normalization and the join. For the exclusion, run every
finalize fixture with the key added to the input run's environment metadata: the stored record must be
`expected-record.json` with the key added to its `payload.environment.metadata`, and nothing else changed (the event is
`expected-event.json`). Run every inject fixture with the key added to every record's environment metadata: the block
must be `expected-block.txt`, byte for byte. The .NET suite runs both checks against its own library.

## Reading the outcome rows

The estimate reads the holdout's assignment rows in exactly one scope (the scope the blocks were built in), each joined
to its run's outcome and to the models, through [`sql/query-holdout-outcomes.sql`](sql/query-holdout-outcomes.sql): in
a transaction that declared the caller's bounds, run `first-page` with `@limit`, then, while a page is full (it has
`@limit` rows), `next-page` with `@after_run_id` and `@after_experience_id` set to the previous page's last row. .NET
reads 1,000 rows a page; a port reads at most that many. Each page is its own transaction.

Each row comes back with `part` (column 23):

- **`part = 0`, one per assignment row**, in (`run_id`, `experience_id`) order: columns 19 to 22 are the assignment's
  `run_id`, `arm`, `experience_id` and `assigned_at`; columns 1 to 18 are the run's own record (the live record in
  exactly the scope whose `source_run_id` is the run; the earliest created, then the lowest ID, when there are several),
  all NULL when there is none.
- **`part = 1`, one per lesson the page names** that is live in exactly the scope: columns 1 to 18 are the lesson's
  record, columns 19 to 22 NULL.

A port turns the page into **outcome rows**, one per `part = 0` row:

| Field | Value |
| --- | --- |
| `runId`, `arm`, `experienceId`, `assignedAt` | the assignment's columns |
| `runOutcome` | the run's record's `payload.outcome.status` (`Verified`, `Failed` or `Unknown`); `null` when the run has no record |
| `runModel` | the run's record's `payload.environment.metadata["agentexperience.model"]`; `null` when there is no record, no entry, or an entry that is empty or white space only (.NET `string.IsNullOrWhiteSpace`). Any other value is returned exactly as stored, untrimmed |
| `lessonModel` | the same entry of the `part = 1` record whose `experience_id` is the row's; `null` when the page has no such record |

**A sealed record (`payload_version` 2) is not readable by a port.** A run whose record is sealed has `runOutcome` and
`runModel` `null`, and a sealed lesson has no model. The run is therefore counted `Pending`, exactly as .NET counts a
run whose record is sealed under a destroyed key.

**An in-memory store** answers the same pages from what it holds: the assignment rows in exactly the scope, in
(`runId`, `experienceId`) order compared as UUIDs (by their 16 bytes in RFC 4122 order, as PostgreSQL and .NET compare
them), after the cursor, at most the limit; each joined to the record in exactly the row's scope whose source run is the
run (the earliest created, then the lowest ID), and to the named lesson's record in exactly the row's scope.

## The estimate

`EstimateAsync(store, authorization, scope, options)` in .NET; each port names it in its own idiom and maps every field
one to one in its README.

1. **The call.** The options are checked first: `minimumRunsPerArm` below 1 is refused before anything is read. A store
   that does not record the holdout gives an empty estimate with `tracked` false. A scope outside the authorization
   reads nothing and gives an empty estimate whose outcome is `Denied`, never an exception. Otherwise the outcome is
   `Found` and `tracked` true.
2. **Paging.** The rows are read a page at a time (above), and each page is folded into running counts before the next
   is read, so the read holds one page at a time. Every row of a run must be added before any row of the next run; a
   row of a run already folded is refused (.NET throws `InvalidOperationException`) rather than counted twice. A page
   whose last row equals the cursor it was read after ends the read.
3. **Each run is classified once**, over all its rows:
   - its **arm**, or *mixed* when its rows name both arms;
   - its **outcome**, the `runOutcome` of its row with the lowest `experienceId` among those that carry one (in practice
     every row of a run carries the same);
   - its **model**, the ordinal least non-blank `runModel` of its rows, or `unknown` when none has one;
   - for each lesson its rows name, **that lesson's model**, the ordinal least non-blank `lessonModel` of the run's rows
     for that lesson, or `unknown`.
4. **Tallying a set of runs.** A mixed run counts in `mixedArm`; otherwise a run with no outcome counts in `pending`, one
   whose outcome is neither `Verified` nor `Failed` in `unverified`; otherwise it is one of its arm's `runs`, and a
   `successes` too when `Verified`.
5. **Comparing two arms.** Each arm's `rate` is `successes / runs`, `null` with no runs. With either arm empty,
   `difference`, `lower` and `upper` are `null` and the verdict is `NoEvidence`. Otherwise, with z = 1.959963984540054:
   - Wilson's interval for s of n, p = s / n: `centre = (p + z²/(2n)) / (1 + z²/n)`,
     `half = z·sqrt(p(1−p)/n + z²/(4n²)) / (1 + z²/n)`, `l = max(0, centre − half)`, `u = min(1, centre + half)`;
   - with p1, (l1, u1) the treatment arm's and p2, (l2, u2) the holdout arm's: `difference = p1 − p2`,
     `lower = max(−1, difference − sqrt((p1 − l1)² + (u2 − p2)²))`,
     `upper = min(1, difference + sqrt((u1 − p1)² + (p2 − l2)²))` (Newcombe's hybrid score interval);
   - the **verdict** is `NoEvidence` when either arm has fewer than `minimumRunsPerArm` runs; otherwise `Helps` when
     `lower > 0`, `Hurts` when `upper < 0`, and `NoEvidence` when the interval spans zero.
6. **The estimate.** `overall` compares every run. `lessons` has one entry per lesson any row names, ordered by
   `experienceId`, comparing the runs whose rows named it. With `splitByModel`, `byConsumingModel` compares the runs of
   each run model, ordered ordinally by model, and each lesson's `byModelPair` compares its runs per (lesson model, run
   model), ordered ordinally by lesson model and then run model; `unknown` is a model like any other. Without it both are
   `null`. The estimate also carries `minimumRunsPerArm`.

The estimate is the same for the same rows in any order, and the paged form gives exactly what the pure form gives. It
changes nothing about ranking, confidence or injection, and it never writes.

## The fixtures

```
fixtures/benefit-estimate/<case>/   rows.json + options.json -> expected.json
fixtures/holdout-outcomes/<case>/   seed.json + query.json -> expected.json (needs PostgreSQL)
fixtures/model-metadata/cases.json  the key, the normalization and the join
```

**benefit-estimate.** `rows.json` holds `rows`, outcome rows as above, in the order a store pages them (by `runId`, then
`experienceId`); `options.json` holds `minimumRunsPerArm` and `splitByModel`. Estimate the rows (feeding them through
the paged form, a page at a time, is the stronger check) and compare with `expected.json`: `minimumRunsPerArm`,
`overall`, `lessons` (each `experienceId`, `comparison` and `byModelPair`) and `byConsumingModel` (each `model` and
`comparison`); a comparison is `treatment` and `holdout` (each `runs`, `successes`, `rate`), `difference`, `lower`,
`upper`, `verdict`, `unverified`, `pending` and `mixedArm`. **Numbers that are not counts (`rate`, `difference`, `lower`,
`upper`) compare within an absolute tolerance of 1e-12**; counts, verdicts, IDs, models and `null` compare exactly. The
expected numbers are .NET's doubles, written in their shortest round-trip form.

**holdout-outcomes.** Against a database the .NET migrator applied, with the 1.0, 1.4 and 1.8 privileges files applied
(row-level security on):

1. Insert `seed.json`'s `records` as a search seed is inserted (the eighteen record columns, as the application role,
   under bounds declared for each record's scope), then its `assignments` as rows of
   `agent_experience.experience_holdout_assignments`, every column from its PostgreSQL text form, as the schema's owner.
2. Read `query.json`'s `scope` (also the caller's bounds) with `limit` as the page size, page after page, as above.
3. Compare with `expected.json`'s `pages`: each page's `after` (the cursor it was read after, `null` for the first) and
   its `rows`, outcome rows in order, `assignedAt` as an instant to the microsecond and everything else exactly.

A store that is not PostgreSQL runs the same cases by holding the records and the assignment rows in memory, and must
give the same pages.

**model-metadata.** `key` and `maxLength`; `normalize`, each reported `modelId` (`null`: the client reports none) with
the value `recorded` (`null`: nothing); and `environment`, each host `metadata` and reported `modelId` with the
`expected` metadata the run records.

**Not fixtured.** A store that does not record the holdout, a scope outside the authorization and a sealed record are
each port's own tests, as are the options' validation and the refusal of a run paged apart.
