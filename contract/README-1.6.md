# Contract 1.6: additions

This page lists what contract 1.6 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md), [contract 1.4](README-1.4.md) and
[contract 1.5](README-1.5.md). Those pages are still normative and unchanged: a minor version only adds files, this page
among them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`.

A port that passes contract 1.6 passes every 1.0 to 1.5 fixture and the fixtures below.

## Vectors are now in scope

Contract 1.0 says "a port uses text retrieval only", and the 1.0 and 1.1 privileges pages leave the
`agent_experience.experience_embeddings` table out. That stays true of contract 1.0 to 1.5. From 1.6, a port may also
retrieve by meaning: it stores an embedding of each record's summary, searches the stored embeddings for the
nearest neighbours of the task text's embedding, and merges those candidates with the text search's before the
existing ranking. Everything here is fixed vectors and SQL: no embedding model is part of the contract, and a port
brings its own.

A port that does not implement 1.6 keeps passing 1.5. A port that implements it and has no embedder registered answers
from text alone, and says so (`NotConfigured`, below).

## What 1.6 adds

- **SQL:** [`sql/write-experience-embedding.sql`](sql/write-experience-embedding.sql) (the conditional write and the
  revision probe), [`sql/search-experience-vector.sql`](sql/search-experience-vector.sql) (the nearest-neighbour search,
  with and without the model-authored exclusion, and the two compatibility probes) and
  [`sql/application-role-privileges-1.6.sql`](sql/application-role-privileges-1.6.sql) (the application role's grants
  on the embeddings table).
- **Fixtures:** `embedding-summary` and `hybrid-merge` (no database), `embedding-write` and `vector-search` (PostgreSQL
  with pgvector).

Nothing else changes: no schema object or search function is added, and every existing file is byte-identical. The
statements are the .NET embedding index's (`PostgresExperienceEmbeddingIndex`), line for line: a contract test joins
each statement's lines with single spaces and compares it with the .NET text.

## The database

The vector channel needs PostgreSQL with the pgvector extension (the `pgvector/pgvector` images) and the vectors
package's migrations, `0004_add_experience_embeddings.sql` and `0020_embeddings_row_level_security.sql`. They are not
copied into the contract: the .NET migrator applies them, as it applies the base schema.

1. `ExperienceSchemaMigrator.MigrateAsync`, as for 1.0.
2. `CREATE EXTENSION IF NOT EXISTS vector`, as a superuser (pgvector is not a trusted extension), unless an operator
   already installed it.
3. `ExperienceVectorSchemaMigrator.MigrateAsync`, as the schema's owner.
4. The application role's privileges, with row-level security on: the 1.0 file, then the 1.4 file, then the 1.6 file,
   in that order (the 1.0 file's `REVOKE ALL` removes the others' grants). Together they are exactly what
   `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` grants on such a schema, and a contract test keeps them
   so. Running the .NET call instead is equivalent.

`experience_embeddings` holds one row per indexed record: its scope (copied from the record), `model_id`, `dimension`,
`content_hash`, `source_revision`, `embedding` (an unconstrained pgvector `vector`) and `created_at`/`updated_at`. A row
is derived data: it never takes part in a lifecycle decision, and an erasure removes it.

## What is embedded

**The summary** is the only text of a record that is embedded: its task ID, task summary and lesson (the reflection's
`lesson`; nothing when there is no reflection), in that order. Reference:
[`ExperienceRetrievalSummary`](../src/AgentExperience.Abstractions/ExperienceIndex.cs).

- A missing field, and one made only of separators, contributes nothing.
- Inside a field, every run of separators becomes one space; fields are joined by one space; the result has no
  leading or trailing space. A separator is a UTF-16 code unit of Unicode general category `Cc`, `Zs`, `Zl` or `Zp`
  (control characters, among them U+001F and U+0085, and every space and line or paragraph separator). Format
  characters (`Cf`, such as U+200B and U+FEFF) are kept.
- The result is capped at 8,192 UTF-16 code units: cut at 8,192; when the last kept unit is a high surrogate, drop it
  too; then drop trailing spaces.

**The content hash** is the lowercase hex SHA-256 of the UTF-8 bytes of the model ID, U+001F, and the summary. A port
stores it with the vector and may skip re-embedding a record whose stored row has the same model ID and content hash.

**The task text** a port embeds for a search is the one it searches text with (contract 1.0), unchanged.

## Validation

Before any statement runs, and without one when it fails (an invalid write or search is refused, not run). These rules
are normative, not fixtured in 1.6:

- the model ID is non-blank and at most 256 UTF-16 code units;
- a vector has 1 to 16,000 components, each finite (no NaN or infinity); a write's vector has exactly `dimension`
  components, and `dimension` is 1 to 16,000;
- the content hash is non-blank, and the source revision is not negative.

Vectors are stored and compared as given: nothing normalizes them (cosine distance does not depend on length). A
zero-magnitude vector is valid; a search with one gets a NaN distance for every row, and a stored one gets a NaN
distance from every query; either way its relevance is 0.

`@embedding` and `@query_vector` are pgvector literals: `[x,y,...]`, each component a decimal number. Any text pgvector
parses as the same vector is fine; the .NET index writes each component's shortest round-trip text.

## The write

In one transaction that declared the bounds for the record's scope, run `write`; when it wrote no row, run
`probe-revision`; then commit. The parameters are in the file's header.

| Result | Outcome |
| --- | --- |
| `write` wrote 1 row | `Written`: the row now holds this vector and descriptor (an existing row keeps its `created_at`) |
| `write` wrote 0 rows, `probe-revision` returns a revision | `Stale`, with that revision: the record moved on since the summary was read, and nothing changed |
| `write` wrote 0 rows, `probe-revision` returns no row | `Missing`: an unknown ID, a record in another scope, or an erased record; no row is created |

A row stored from a newer revision is never replaced by an older one. Embeddings are derived: a port writes one after
the record's own commit has landed, never inside it, and a failed or stale write never fails anything else.

## The search

In one transaction that declared the bounds for the reader's scope, run `search` (or
`search-excluding-model-authored`, when model-written lessons are excluded) with every `{dim}` replaced by the query
vector's length. When it returns no row, run the matching compatibility probe in the same transaction.

- **Parameters** are the text search's where they overlap (scope, `{Validated,Reinforced}`, `0.5`, a limit of 51), plus
  `@model_id` and `@query_vector`. Only embeddings of the query's model and dimension are compared; the scope is the
  reader's own, as with `p_with_grants => false`.
- **Rows** come back nearest first. The statement has no tie-break (one would stop an index serving the order), so a
  port orders rows of equal `distance` by `experience_id` ascending (the lower-case hyphenated text, ordinally); NaN
  distances sort after every number (PostgreSQL's order) and equal each other.
- **Relevance** is `clamp(1 - distance / 2, 0, 1)` in double precision, and 0 when `distance` is NaN. `distance` is
  pgvector's cosine distance (`<=>`), in [0, 2].
- **An empty search's outcome** is the probe's. The first column false: `Found`, empty (nothing comparable is stored,
  or nothing eligible). The first true and the second false: `ModelMismatch`. Both true: `DimensionMismatch`. A
  mismatch returns no candidates.

## Hybrid retrieval

With an embedder registered, a retrieval runs both channels concurrently under the call's one timeout. Reference:
[`ExperienceRetrievalService.cs`](../src/AgentExperience.Core/Retrieval/ExperienceRetrievalService.cs).

**The vector channel never fails the call.** Whatever goes wrong there, the call answers from text alone and reports
which fallback applied:

| Fallback | When |
| --- | --- |
| `NotConfigured` | no embedder or no embedding store is registered |
| `ProviderUnavailable` | the embedder failed or timed out, reported a blank model ID, or returned a vector that is empty, of a length other than its declared dimension, or not finite; no statement runs |
| `ModelMismatch` | the search's outcome was `ModelMismatch` |
| `DimensionMismatch` | the search's outcome was `DimensionMismatch` |
| `VectorSearchFailed` | the search statement failed, or the store refused or did not answer the search |

**The text channel's failure fails the call**, exactly as in contract 1.0, whatever the vector channel found.

**From two channels to one list**, in this order (the `hybrid-merge` fixtures pin steps 1 and 3):

1. **The floor.** When a minimum vector relevance in [0, 1] is configured, drop every vector candidate whose relevance
   is below it (a candidate exactly at it is kept; a NaN relevance is below any floor). Text candidates are never
   floored. No floor is the default.
2. **Each channel's checks and truncation**, as contract 1.0 states them for the text rows: over each channel's first
   50 candidates, a candidate outside the requested scope or a repeated `experience_id` fails the whole result
   (normative, not fixtured in 1.6); keep
   the first 50 of each, and report the result truncated when either channel returned 51.
3. **The merge.** The text candidates in their order, then the vector candidates in theirs. A record already in the
   list is not added again: it keeps its position and its first channel's record (the text channel's), and takes the
   vector relevance only when that is higher, comparing relevances clamped to [0, 1] with NaN as 0.
4. **Ranking**, exactly as contract 1.0 states it, with each merged candidate's relevance as the `Relevance` input.
   Being found by both channels earns nothing more.

## The fixtures

```
fixtures/embedding-summary/cases.json   taskId + taskSummary + lesson + modelId -> summary + contentHash
fixtures/hybrid-merge/cases.json        minimumVectorRelevance + text + vector -> merged
fixtures/embedding-write/<case>/        seed.json + steps.json -> expected.json (needs PostgreSQL with pgvector)
fixtures/vector-search/<case>/          seed.json + steps.json -> expected.json (needs PostgreSQL with pgvector)
```

**embedding-summary:** for each case, the summary of `taskId`, `taskSummary` and `lesson` (each may be `null`) is
`summary`, exactly, and the content hash under `modelId` is `contentHash`.

**hybrid-merge:** for each case, flooring `vector` at `minimumVectorRelevance` (`null`: no floor) and merging it with
`text` gives `merged`, in order: each entry's `experienceId`, the `relevance` kept, and `from`, the channel whose
record was kept (`text` or `vector`). Relevances compare exactly; a NaN relevance is written as the string `"NaN"`.

**embedding-write and vector-search**, against a database set up as above:

1. Insert `seed.json`'s records exactly as a search seed is inserted (the eighteen record columns, as the application
   role, under bounds declared for each record's scope).
2. Run `steps.json`'s `steps` in order:
   - `write`: the port's write, with `experienceId`, `scope`, `modelId`, `dimension`, `contentHash`, `sourceRevision`,
     `vector` (32-bit floats) and `now` (the write's clock, for `@now`). Its entry in `expected.json` is
     `{"outcome": "Written"}`, `{"outcome": "Missing"}` or `{"outcome": "Stale", "currentRevision": n}`.
   - `search`: the port's search, with `scope`, `modelId`, `vector`, `statuses`, `minConfidence`, `limit` and
     `excludeModelAuthored`. Its entry is the `outcome` and the `rows` in order, each with `experienceId`, `distance`
     (`null` for NaN) and `relevance`. Compare distances and relevances to 1e-9.
   - `setRevision`: as the schema's owner, `UPDATE agent_experience.experience_records SET revision = <revision> WHERE
     experience_id = <experienceId>`, which stands for a lifecycle commit moving the record on. Entry `null`.
   - `erase`: exactly as in the record-injection family ([README-1.4](README-1.4.md)). Entry `null`.
3. Compare with `expected.json`: `steps`, one entry per step, and `experience_embeddings`, every row of the table
   afterwards, read as the owner, ordered by `experience_id`, each column cast to `text` in the time zone UTC.
