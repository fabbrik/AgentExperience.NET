# Contract 1.5: additions

This page lists what contract 1.5 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md) and [contract 1.4](README-1.4.md). Those pages are still
normative and unchanged: a minor version only adds files, this page among them. The contract's version is the one
`lock.json` records (`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.5 passes every 1.0, 1.1, 1.2, 1.3 and 1.4 fixture and the fixtures below.

## What 1.5 adds

- **erased:** a new fixture family (needs PostgreSQL): `search-skips-an-erased-best-match`, `get-of-an-erased-record`
  and `record-for-an-erased-record`.

Nothing else changes: no statement, schema or privilege is added, and every existing fixture is byte-identical.

## Erased records are never read

An erased record is a tombstone: `agent_experience.purge_experience_record` keeps the row, sets its `deleted_at`, its
status to `Deleted` and its payload to `{}`, and removes its injection row. A port never reads one, whichever way it
reads:

- **Search.** The text search ([`search-experience-text.sql`](sql/search-experience-text.sql)) never returns an erased
  record, so ranking never sees it; the next match takes its place, its row and score as they were.
- **A read by ID** finds nothing for an erased record, exactly as for an ID that never existed. The contract has no
  statement for it: a port reads the record's columns in exactly the requested scope, under bounds declared for it, and
  only a live row (`deleted_at IS NULL`). This case applies only to a port that reads records by ID; the Python and Go
  ports do (`get` / `Get`).
- **Recording an injection** ([`record-injection.sql`](sql/record-injection.sql)) that names an erased record leaves no
  row for it.

Contract 1.3 left this out because a search seed cannot hold a tombstone (see [README-1.3](README-1.3.md)). 1.5 does
not change that seed: each case seeds live records, as a search seed does, and erases them in a step, through the one
function every erasure path runs.

## The erased fixtures

```
fixtures/erased/<case>/   seed.json + steps.json -> expected.json (needs PostgreSQL)
```

Against a database the .NET migrator applied, with both privileges files applied (row-level security on):

1. Insert `seed.json`'s records exactly as a search seed is inserted (the eighteen record columns, as the application
   role, under bounds declared for each record's scope). `seed.json` has the search seed's shape.
2. Run `steps.json`'s `steps` in order:
   - `erase` and `record`: exactly as in the record-injection family ([README-1.4](README-1.4.md)): `erase` runs
     `purge_experience_record` as the schema's owner, which answers `Deleted`; `record` runs the port's record call.
   - `search`: its value has the shape of a search case's `query.json`. Run the text search and rank its rows as a search
     case does.
   - `get`: read `experienceId` by ID in exactly `scope`, as above.
3. Compare with `expected.json`:
   - `steps` holds one entry per step, in order: `null` for `erase` and `record`; for `search`, the search family's
     `expected.json` shape (`rows`, `truncated`, `ranked`, compared as a search case compares them); for `get`,
     `{"found": true}` or `{"found": false}`.
   - `experience_injections` holds every row of `agent_experience.experience_injections` afterwards, read and compared as
     the record-injection family reads them.
