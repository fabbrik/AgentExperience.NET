# AgentExperience.Storage.Postgres.Vectors

> **Preview — not production ready.** This is a `0.1.0-preview` package. Public APIs may change between previews,
> and the [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table in the repository README
> lists every unresolved item. Any unresolved item blocks a production-readiness claim.

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
| `Microsoft.Extensions.AI.Abstractions` | `[10.10.0]` | Adapting an `IEmbeddingGenerator` to this library's own port |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `[10.0.12]` | This package's own `Add…` registrations |

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
await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken);        // 0001-0003 and 0005, base schema
await ExperienceVectorSchemaMigrator.MigrateAsync(dataSource, cancellationToken);  // 0004, this package's schema
```

They are separate on purpose. `0004` begins with `CREATE EXTENSION vector`, and pgvector is **not** a trusted
extension, so that statement ordinarily needs a superuser (on a managed service, whichever role that provider
designates). A text-only deployment never calls the second line and therefore never needs that privilege. If your
operators install the extension out of band, this call runs fine as an ordinary role — `CREATE EXTENSION IF NOT
EXISTS` is a no-op once it exists. Run the base migration first: `0004` has a foreign key to `experience_records`.

The searching role needs `SELECT` on `agent_experience.experience_embeddings` and
`agent_experience.experience_records`, plus `INSERT`/`UPDATE` on the embedding table to index. To honour sharing
grants it also needs `SELECT` on `agent_experience.experience_grants`; that one is optional, and a role without it
(or a database that has not applied `0005`) falls back to the exact-scope predicate and reports it once through the
`onGrantsUnavailable` callback.

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
never read from or written to this table. Reuse confidence does move now — evidence submitted after a lesson is
reused updates it through the canonical store (see
[evidence-based confidence updates](../../README.md#updating-confidence-from-evidence)) — but it moves there and is
only ever *read* here, as a floor in the search predicate. A record whose score drops below the floor stops being
returned without anything being rewritten or re-embedded; the number the search compares is the one the join reads
from `experience_records`, so it is never stale.

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

## Removing a vector when a record leaves eligibility

`RemoveAsync` deletes one record's stored vector within exactly one scope. It is the mirror of the conditional
write, and it is what keeps the vector channel honest when a record stops being reusable: only `Validated` and
`Reinforced` records may be returned, so a record that is contested, made stale, superseded or revoked must not
keep a row a search could match.

| Situation | Outcome |
| --- | --- |
| A vector was stored in this scope | `Removed` |
| Never indexed, already removed, or in another scope | `NotIndexed` — one outcome for all three, so removal is idempotent and a foreign-scope attempt reveals nothing |
| Scope outside the authorization | `Denied`, before any statement is issued |
| Malformed request | `Invalid`, with the field path |

The `DELETE` matches the embedding row's **own** scope columns, which were copied from the record when the vector
was written, so removal never depends on joining back to the record.

`ExperienceLifecycleService` calls this through `ExperienceIndexingService.RemoveAsync` as a post-commit hook, after
a transition that left eligibility has already landed, on its own budget.

**What removal actually buys is storage and index maintenance cost, not reachability.** The search above joins the
canonical record and filters on `r.status`, so a vector left behind by a record that is now contested, stale,
superseded or revoked is *already* unmatchable — and the text channel excludes it by status too. That is why removal
can never fail the transition that asked for it.

**Nothing retries a removal that did not happen.** `ScanAsync` lists only records a search *could* return, so a
re-index pass never sees an ineligible record and never removes anything: there is no sweep. A `Deindexing` outcome
other than `Removed` or `NotIndexed` is a work item for the host — record the experience ID and scope and call
`RemoveAsync` again later. That includes `Denied`, which reports `IsRetryable: false` because repeating the *same*
call changes nothing; it needs a different authorization, not another attempt.

**Erasing the record removes its vector too, from the other package's transaction.** `DeleteAsync` in
`AgentExperience.Storage.Postgres` leaves a payload-free tombstone rather than deleting the record row, so
`0004`'s `ON DELETE CASCADE` never fires — the base package's purge function deletes the embedding explicitly
instead, guarded by `to_regclass` so a deployment without this package simply skips the step. Nothing here has to
be called, and nothing here is depended on. Afterwards the tombstone can never be indexed again: a write against
it reports `Missing` rather than `Stale`, because no revision of an erased record can ever be indexed, and
`ScanAsync` does not offer it, because a tombstone has no summary and no lesson to embed.

`WriteAsync` takes `FOR KEY SHARE` on the record row in the same statement that checks it is not a tombstone, so a
write already in flight when an erasure commits is parked against the purge and re-checks when it is released,
rather than landing afterwards. That matters more here than anywhere else: a stored vector is a searchable
derivative of exactly the summary and lesson the erasure was asked to destroy, so a write that slipped through
would put a queryable copy of erased content back into the database.

**What the erasure leaves behind in this package.** Dead entries stay in the HNSW index until `VACUUM` reclaims
them. Those entries point at heap tuples that are themselves dead, so a query cannot return them — but be precise
about the two halves, because they are not the same: the *index* entry cannot return anything, while the *heap*
tuple it points at is still the row, vector and all, until `VACUUM` reclaims it. A deployment with an erasure
deadline has to `VACUUM agent_experience.experience_embeddings` itself rather than wait for autovacuum, and needs
`VACUUM FULL` or a storage-level guarantee if it must also defeat forensic recovery of freed pages. The base
package's README says the same about the record table's own heap, at more length.

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
WHERE ((<exact scope on e and r>) OR <an active sharing grant naming r and permitting this scope>)
  AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence
  AND e.model_id = @model_id AND e.dimension = n
ORDER BY distance, r.experience_id LIMIT @limit
```

Scope, status, and the confidence floor are the same predicates the text channel applies — both channels filter
identically, in the database, before anything is ranked. Comparability is a predicate too: a vector from another
model or of another width is excluded by the query, so no incompatible comparison is ever attempted.

The exact-scope predicate is applied to **both** sides of the join. On `r` it is authoritative; on `e` it is
redundant (the embedding's scope columns are copied from the record row inside the write) and exists so
`ix_experience_embeddings_scope_model` can actually serve the query — a btree on
`(tenant_id, application_id, project_id, model_id, dimension)` is useless when the only predicates on the embeddings
table are its trailing two columns.

A top-level `OR` is not free: the grant branch is a correlated `EXISTS`, and the planner may well choose a scan
over the join rather than the scope index. The `EXPLAIN` test in this repository runs with `enable_seqscan = off`,
so what it proves is that the HNSW index is *reachable* for the ordering -- not that the scope filter stays
index-served once the `OR` is there. Measure on your own data before assuming it does.

The alternative to that exact match is an **active sharing grant** — issued, not revoked, and not expired as of the
database's own `clock_timestamp()` — naming the record and permitting the requesting scope. It is the base package's predicate,
composed rather than retyped, so both retrieval channels honour byte-for-byte the same rule about what a grant does;
see [Sharing grants](../AgentExperience.Storage.Postgres/README.md#sharing-grants). The grant branch is stated on the
record side only: an embedding carries its *owner's* scope, so an `e`-side exact match would exclude exactly the rows
the grant exists to admit. A shared record is still subject to every other predicate here — status, confidence,
model, and width — so sharing widens who may read a record, never what makes one comparable or eligible.

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
