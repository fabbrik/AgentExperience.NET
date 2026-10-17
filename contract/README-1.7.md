# Contract 1.7: additions

This page lists what contract 1.7 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md), [contract 1.4](README-1.4.md),
[contract 1.5](README-1.5.md) and [contract 1.6](README-1.6.md). Those pages are still normative and unchanged: a minor
version only adds files, this page among them. The contract's version is the one `lock.json` records
(`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.7 passes every 1.0 to 1.6 fixture and the fixtures below.

## The lessons view

The .NET library lists what it has learned in a scope: `AgentExperienceLessons.ListAsync`
([`AgentExperienceLessons.cs`](../src/AgentExperience.MicrosoftAgentFramework/AgentExperienceLessons.cs)). From 1.7, a
port may list the same lessons, and prove that it answers what .NET answers for the same rows: each lesson's status,
confidence and checks, the block text an agent in that scope would be shown for it, why retrieval would not use it
when it would not, and when it was last injected.

A port that does not implement 1.7 keeps passing 1.6.

## What 1.7 adds

- **SQL:** [`sql/list-experience-lessons.sql`](sql/list-experience-lessons.sql): the page of records with each record's
  last injection in one statement, and the same page without the injections. No privileges file: the 1.0 and 1.4
  privileges already grant everything these statements read.
- **Fixtures:** `lessons-view` (PostgreSQL; an in-memory store runs it too, see below).

Nothing else changes: no schema object is added, and every existing file is byte-identical. The statements are the
.NET `PostgresExperienceRecordStore`'s `QueryWithInjectionsAsync` and `QueryAsync` statements, line for line: a contract
test joins each statement's lines with single spaces and compares it with the .NET text.

## The view is read-only and in the caller's own scope

A port's list call takes the caller's authorization bounds and the scope to list, and never writes. The scope listed is
the caller's own: records another scope lends through a sharing grant are not listed (there are no grants in contract
1.x). When the scope lies outside the bounds the call runs no statement and answers an empty page with the outcome
`Denied`; a query that is refused (below) answers `Invalid` or raises, as the port's idiom has it. No outcome other than
`Found` is an exception, and none carries a lesson.

**The query** has three options:

| Option | Meaning | Default | Valid |
| --- | --- | --- | --- |
| `statuses` | the lifecycle statuses to list | every status | absent, or at least one, each one of `Candidate`, `Validated`, `Quarantined`, `Contested`, `Stale`, `Superseded`, `Revoked`, `Reinforced` (a repeated status counts once) |
| `limit` | how many lessons to list at most | 50 | 1 to 500 |
| `includeIneligible` | whether to list lessons retrieval would not return | true | |

An invalid `statuses` or `limit` is refused before any statement runs.

## The page

**The read.** In one transaction that declared the bounds for the scope, run `page` (`page-with-statuses` when the
query names statuses), with `@limit` the **probe**:

- `includeIneligible`: `min(limit + 1, 500)`, one record past the limit, which tells "exactly the limit" from "more
  exist";
- otherwise 500, because leaving ineligible lessons out can skip any number of records.

The statement answers the scope's live records, newest first (`created_at` descending, then `experience_id`
ascending), each with its last injection (columns 19 and 20, both NULL for a record never injected). An in-memory store
orders the same way.

**When the injections cannot be read** -- the statement fails with `undefined_table` (SQLSTATE `42P01`, the injections
table is not migrated) or `insufficient_privilege` (`42501`, the application role has not been granted it: re-run the
privileges, see [1.4](README-1.4.md)) -- roll that transaction back, open a new one that declares the same bounds, and run
`page-without-injections` (`page-without-injections-with-statuses`): the same page with no injection summary. The page
then says its **injections are not tracked** (below). Any other failure is the call's failure. A store with no
injections table at all (an in-memory store of a port that records none) is not tracked in the same way.

**From records to lessons**, over the records in the order read:

1. A record whose scope is not exactly the scope listed is not shown, and counts in `outOfScopeDropped`. (A conforming
   store never answers one; the check is defence.)
2. The record is described as a lesson (next section), and when its injection summary exists the lesson takes its
   `lastInjectedAt` and `injectionCount`. A record with no summary, or any record of a page whose injections are not
   tracked, has `lastInjectedAt` null and `injectionCount` 0.
3. With `includeIneligible` false, an ineligible lesson is skipped.
4. When `limit` lessons are already listed, the record proves more exist: `hasMore` is true and the walk stops.
   Otherwise the lesson is listed.

After the walk, `hasMore` is also true when the read answered as many records as the probe asked for (the store may
hold more than were read).

**The page** is: `outcome`, `generatedAt` (the port's clock when the page was read), `hasMore`, `outOfScopeDropped`,
`injectionsTracked` (true when the page was read with its injections: an in-memory or PostgreSQL store that tracks
them and was able to read them; false otherwise, and then a null `lastInjectedAt` means "not known", not "never") and
`lessons`. A page that is not `Found` has no lessons, `hasMore` false, `outOfScopeDropped` 0 and `injectionsTracked`
false.

## One lesson

| Field | Value |
| --- | --- |
| `experienceId` | the record's ID, lower-case hyphenated |
| `taskId` | the record's task ID; empty when it has none |
| `status` | the status, by name |
| `confidence` | the record's reuse confidence, as stored |
| `createdAt`, `updatedAt` | the record's instants |
| `checks` | the outcome's evidence, in stored order, each `checkId`, `evidenceKind` (the evidence's `kind`) and `result` (`Pass`, `Fail`, ...); never the evidence's detail. None when the outcome holds no evidence |
| `blockText` | the Historical Reference block for this record alone, below |
| `isEligible` | true when no ineligibility applies |
| `ineligibility`, `ineligibleReason` | the code and the words, null when eligible |
| `lastInjectedAt`, `injectionCount` | from the injection summary, above |

**The block** is what an agent in this scope would be shown for the record, rendered by the same writer the
[inject](README.md) family fixtures, with the port's injection options, for **this record only, numbered 1, with no
ranking and no record or byte limit**: the block of a one-record list whose record has no score and no components, so its
`Matched:` line reads `no ranking data`. It is rendered whatever the record's status and size, and its bytes are the
bytes the writer produces: `blockText` is byte-equal to .NET's (`ExperienceContextProvider.RenderAlone`).

**Eligibility** is decided in this order, and the first rule that applies is the lesson's `ineligibility` (the code is
the name below):

| Code | Applies when | `ineligibleReason` |
| --- | --- | --- |
| `Status` | the status is not `Validated` or `Reinforced` | `status <Status> is never retrieved` |
| `BelowConfidenceFloor` | the confidence is not at least 0.5 (a NaN is below it) | `confidence <c> is below the retrieval floor 0.50` |
| `ModelAuthored` | the lesson's reflection was written by a model and the port's settings exclude model-authored lessons | `model-authored lessons are excluded` |
| `OverByteBudget` | none of the above, and the block's UTF-8 length is more than the injection block's maximum bytes (16384 by default) | `its block (<n> bytes) is larger than the <max>-byte injection budget` |
| `Unrenderable` | rendering the block raised | `it could not be rendered (<error type>)` |

`<c>` and the floor are written with two decimals, taken to 15 significant digits first and then rounded half away from zero
(.NET's `ToString("0.00")`), and `(unavailable)` for a number that is not finite. `<n>` and `<max>` are decimal integers.
An unrenderable record is listed with no checks, an empty `blockText`, `isEligible` false and its `Unrenderable` code,
and never hides the others; its `<error type>` is the port's own name for the failure and is not fixtured.

`.NET` has two more codes, `Expired` (a retrieval policy's maximum age) and `ContentUnconfirmed` (provenance signing).
**They do not exist in a port**, which has neither a maximum-age policy nor provenance signing, so a port never answers
them; the fixtures use .NET's defaults, under which they never occur, and the confidence floor (0.5) and block budget
(16384 bytes) are .NET's defaults too. The fixtures also leave model-authored lessons included, so `ModelAuthored` is a
port's own test.

## HTML

The .NET library renders a page as HTML (`ExperienceLessonsHtml`). A port does not; the contract does not cover it.

## The fixtures

```
fixtures/lessons-view/<case>/   seed.json + query.json -> expected.json (needs PostgreSQL)
```

Each case is run against a database set up as for 1.0 (with the 1.4 privileges, so the injections table is readable):

1. Insert `seed.json`'s `records` exactly as a search seed is inserted (the eighteen record columns, as the application
   role, under bounds declared for each record's scope), and its `injections` as record-injection seeds are inserted
   ([README-1.4](README-1.4.md): rows of `agent_experience.experience_injections`, every column in PostgreSQL's text form,
   as the schema's owner).
2. Call the port's list with `query.json`: `scope` (which is also the caller's bounds), `statuses` (`null`: every
   status), `limit`, `includeIneligible`, and `now`, the clock the port reads the page with.
3. The page must equal `expected.json`: `outcome`, `generatedAt`, `hasMore`, `outOfScopeDropped`, `injectionsTracked`
   and `lessons` in order, each lesson with the fields above (`confidence` compares exactly; `lastInjectedAt` and the
   other instants compare as instants, to the microsecond; `blockText` compares byte for byte).

A store that is not PostgreSQL runs the same cases by holding the records and injections in memory: a port's in-memory
store must give the pages above too, with its injections recorded at the instants `seed.json` names and counted as it
names.
