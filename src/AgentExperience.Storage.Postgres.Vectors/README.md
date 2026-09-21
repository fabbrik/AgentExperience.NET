# AgentExperience.Storage.Postgres.Vectors

The pgvector half of AgentExperience.NET's PostgreSQL adapter: one derived embedding per Experience Record, a
conditional write that can never overwrite newer state or resurrect a deleted record, an explicit scoped re-index,
and a scoped nearest-neighbour search that applies exactly the same eligibility filters as the text channel.

It implements `IExperienceEmbeddingIndex` from `AgentExperience.Abstractions` and is consumed by
`AgentExperience.Core`'s `ExperienceIndexingService` and `ExperienceRetrievalService`.

> **Status: early development.** Nothing is published to NuGet yet, and APIs may change.

## Why this is a separate package

`AgentExperience.Storage.Postgres` deliberately takes **no** vector or model-provider dependency — its dependency
boundary test pins its package set exactly and forbids `Pgvector`, `VectorData`, and `Microsoft.Extensions.AI`. A
host that only wants canonical storage and text retrieval should not pull those in. So the vector work lives here,
with its own exact pins, and references the store package for the column list, scope predicate, row decoder, and
failure translation the two channels must share.

| Package | Version | Why |
| --- | --- | --- |
| `Npgsql` | `[10.0.3]` | Every statement, on the host's own data source |
| `Pgvector` | `[0.3.2]` | The `vector` literal format |
| `Microsoft.Extensions.AI.Abstractions` | `[10.9.0]` | Adapting an `IEmbeddingGenerator` to this library's own port |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `[10.0.11]` | This package's own `Add…` registrations |

Every version was verified by Story 1.7's executable PostgreSQL/pgvector compatibility proof before this package
was written.

## Usage

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres.DependencyInjection;
using AgentExperience.Storage.Postgres.Vectors.DependencyInjection;

services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddAgentExperiencePostgresStore();                  // IExperienceRecordStore
services.AddAgentExperiencePostgresCandidateSource();        // IExperienceCandidateSource (text)
services.AddAgentExperiencePostgresEmbeddingIndex();         // IExperienceEmbeddingIndex  (vectors)
services.AddAgentExperienceEmbeddingGenerator();             // over a registered IEmbeddingGenerator<string, Embedding<float>>
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
services.AddAgentExperienceIndexing();                       // ExperienceIndexingService, and finalization's post-commit hook
services.AddAgentExperienceRetrieval();                      // hybrid, because both halves above are registered
```

Apply the schema once at startup, in two calls:

```csharp
await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken);        // 0001-0003, the base schema
await ExperienceVectorSchemaMigrator.MigrateAsync(dataSource, cancellationToken);  // 0004, this package's schema
```

They are separate on purpose. `0004` begins with `CREATE EXTENSION vector`, and pgvector is **not** a trusted
extension, so that statement ordinarily needs a superuser (on a managed service, whichever role that provider
designates). A text-only deployment never calls the second line and therefore never needs that privilege. If your
operators install the extension out of band, this call runs fine as an ordinary role — `CREATE EXTENSION IF NOT
EXISTS` is a no-op once it exists. Run the base migration first: `0004` has a foreign key to `experience_records`.

Both migrators share the `agent_experience.schema_versions` journal and the same advisory lock, so they serialize
against each other and against another host, and neither can claim the other's journal entries.

**No `UseVector()` needed.** This adapter sends vectors as pgvector's own text literal and casts them in SQL, so it
works on whatever `NpgsqlDataSource` the host built. You can still call `UseVector()` for your own queries.

### Bringing your own generator

`IExperienceEmbeddingGenerator` is three members — `ModelId`, `Dimension`, and `GenerateAsync(string, …)` — and
speaks domain types only. `AiExperienceEmbeddingGenerator` adapts a `Microsoft.Extensions.AI`
`IEmbeddingGenerator<string, Embedding<float>>` to it, taking the model ID and dimension from the generator's
`EmbeddingGeneratorMetadata` unless you pass your own. Both are read **once**, at construction: the content hash
covers the model ID, so "the same text under the same model" has to be recognizable before any provider call. A
generator that reports neither fails at wiring time rather than mid-query.

Tests, and any deployment that wants reproducibility, can implement the port directly with a deterministic
function — that is exactly what this package's own integration tests do, which is why they need no model
credentials.

## What is stored, and what is not

Only the **sanitized retrieval summary** is ever sent to a provider: the task ID, the sanitized task summary, and
the reflection's lesson — the same three fields `0003` indexes for text. Attempts, tool calls, evidence,
provenance, and environment metadata never leave the database.

The same *fields*, but not necessarily the same *length*: the embedded summary is capped at 8,192 characters
(`ExperienceRetrievalSummary.MaxLength`) while `0003` analyzes the concatenation up to 100,000. A record whose
summary and lesson together run past 8 KB is matched on more of its text by words than by meaning. The cap is
applied before the content hash is taken, so the hashed text and the text the provider sees are always identical,
and it never splits a surrogate pair.

A record is only embedded at all when a search could return it: the scan applies the same status filter and
confidence floor the search applies, so a `Quarantined`, `Revoked`, `Superseded`, or `Candidate` record's summary
and lesson never reach a provider.

| Column | What it is |
| --- | --- |
| `experience_id` | Primary key, `REFERENCES experience_records … ON DELETE CASCADE` |
| `tenant_id` … `user_id` | The record's scope, **copied from the record row** inside the write, never from caller input |
| `model_id`, `dimension` | What decides comparability. A query vector is only ever compared with vectors from the same model at the same width |
| `content_hash` | SHA-256 over the model ID and the normalized summary, so an unchanged record can be skipped without calling a provider |
| `source_revision` | The record revision the summary was read at — what makes every write conditional |
| `embedding` | An **unconstrained** `vector`. The dimension belongs to whichever model a host configured, and `CHECK (vector_dims(embedding) = dimension)` keeps the two from ever disagreeing |
| `created_at`, `updated_at` | UTC, truncated to whole microseconds like the rest of the schema |

None of this takes part in a lifecycle decision. Status, revision, and reuse confidence live on the record and are
never read from or written to this table.

## Writes are conditional, in SQL

A write is an `INSERT … SELECT` whose source is the canonical record row itself, matched on the exact scope **and**
on the revision the write names:

| Situation | Outcome | What happened |
| --- | --- | --- |
| Record present at exactly that revision | `Written` | The vector replaces any previous one for that record |
| Record moved to a newer revision | `Stale` | Nothing written; the result carries the record's current revision |
| Record no longer in this scope | `Missing` | Nothing written, **no row created** — an in-flight write cannot resurrect a deleted record |
| Scope outside the authorization | `Denied` | No statement issued at all |
| Malformed request | `Invalid` | No statement issued; every field path is reported |

An `ON CONFLICT` guard additionally stops an older in-flight write from overwriting a vector already stored from a
newer revision, and the foreign key's cascade means an embedding can never outlive its record.

The vector write is **never** inside the canonical transaction. It runs on its own connection, after the record's
lifecycle commit has landed. That is the whole point: embeddings are derived data, and the canonical write must not
depend on a provider being up.

## Re-indexing

`ScanAsync` lists, for one scope (optionally narrowed to specific IDs, always bounded), each record's current
revision, its normalized summary, and the descriptor of whatever vector is already stored for it — all from one
snapshot. It applies the **same status filter and confidence floor the search applies**: a record whose vector could
never be returned is never listed, so its task summary and reflection lesson never leave the database for a
third-party provider.

It is a **keyset walk**, not a repeated first page. Targets come back in ascending `ExperienceId` order and the
result carries `LastExaminedId`; pass that as the next scan's `StartAfterId` (or the next pass's) and a scope larger
than one page is walked to the end. A `null` `LastExaminedId` means the scope is exhausted.

`ExperienceIndexingService` then decides per record:

- same model **and** same content hash → **skipped**: no provider call, no write;
- anything else → re-embed and write conditionally.

So re-running a pass over unchanged records costs one read and nothing else, and running it twice over a changed
record rewrites it exactly once. Re-indexing is always explicit and always scoped; nothing here ever runs on its own.

## Searching

```sql
SELECT <record columns>, (e.embedding::vector(n) <=> CAST(@query_vector AS vector(n))) AS distance
FROM agent_experience.experience_embeddings e
JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id
WHERE <exact scope> AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence
  AND e.model_id = @model_id AND e.dimension = n
ORDER BY distance, r.experience_id LIMIT @limit
```

Scope, status, and the confidence floor are the same predicates the text channel applies — both channels filter
identically, in the database, before anything is ranked. Comparability is a predicate too: a vector from another
model or of another width is excluded by the query, so no incompatible comparison is ever attempted.

The scope predicate is applied to **both** sides of the join. On `r` it is authoritative; on `e` it is redundant
(the embedding's scope columns are copied from the record row inside the write) and exists so
`ix_experience_embeddings_scope_model` can actually serve the query — a btree on
`(tenant_id, application_id, project_id, model_id, dimension)` is useless when the only predicates on the embeddings
table are its trailing two columns.

The distance expression is the **only** sort key. A tie-break on `experience_id` would force the whole join to be
sorted and the HNSW index never to be used, so exact distance ties are broken arbitrarily here — which costs
nothing, because Core re-sorts every candidate by score and breaks its own ties on `ExperienceId`.

Relevance is `1 - distance / 2` (cosine distance lies in `[0, 2]`), so it is already in `[0, 1]`, with 1 for an
exact direction match. Like the text channel's relevance it is a within-search measure.

When a search matches nothing, and only then, one extra statement asks what the scope actually holds, so the caller
can tell "nothing is similar" from "nothing here is comparable". It is two `EXISTS` probes — is there any comparable
population at all, and is there one for this exact model — so the healthy empty case costs two index probes rather
than an aggregate over every in-scope row, and the answer cannot depend on how many distinct models happened to fit
under a limit:

| Result | Meaning |
| --- | --- |
| `Found` (possibly empty) | The search ran over comparable vectors |
| `ModelMismatch` | The scope holds embeddings, all from other models |
| `DimensionMismatch` | The scope holds this model's embeddings, all at another width |

Core turns either mismatch into an explicit text-only retrieval result carrying the reason.

## The HNSW index is created out of band

`0004` cannot create it: an HNSW index needs a dimension, and a shipped migration does not know which model a host
runs. So it is an explicit call:

```csharp
await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(dataSource, dimension: 1536, cancellationToken);
```

which creates, idempotently,

```sql
CREATE INDEX IF NOT EXISTS ix_experience_embeddings_hnsw_1536
    ON agent_experience.experience_embeddings
    USING hnsw ((embedding::vector(1536)) vector_cosine_ops)
    WHERE dimension = 1536;
```

The index is over an *expression* and is *partial* on the dimension — that predicate is what makes the cast safe on
a table that may hold several widths at once, and it lets several dimensions coexist, each with its own index. The
search's own `ORDER BY` uses exactly the same expression, so changing either side alone silently stops the index
being used.

It is **optional**: every search is correct without it (pgvector falls back to an exact scan, which is what a small
deployment wants anyway). It changes latency, and it makes search *approximate* — HNSW may miss a true nearest
neighbour. Building it over many rows takes minutes and locks the table against writes, so run it from a
maintenance path, never from request handling. `DropHnswIndexAsync` removes it again.

## Results and failures

Expected conditions are typed results, exactly as in `AgentExperience.Storage.Postgres`. Infrastructure failures
throw `ExperienceStoreException` with the driver's exception inside; caller cancellation surfaces as an unwrapped
`OperationCanceledException`. Core catches all of it and reports it as a retryable indexing result or an explicit
text-only retrieval — nothing here can fail a canonical write or a finalization.

## Testing

The integration tests run against an ephemeral `pgvector/pgvector:pg16` container through Testcontainers (Docker
required; set `TESTCONTAINERS_RYUK_DISABLED=true` if Ryuk fails under your local Docker setup) and embed through a
deterministic in-test generator, so **no model credentials are ever needed**. One of them runs `EXPLAIN` over the
adapter's real search statement with `enable_seqscan` off and asserts the HNSW index appears in the plan, so "the
index exists" and "the search uses it" stay the same claim.

## License

[Apache-2.0](../../LICENSE)
