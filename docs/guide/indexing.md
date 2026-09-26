# Indexing for semantic reuse

**In short.** A stored record is already findable by its words. Indexing adds a second way to find it — by meaning —
by storing an embedding of its short summary in pgvector. It is optional, and it is *derived* data: it is written
after the record is committed, on its own connection, and a failing or missing embedding provider never blocks or
undoes a record. Of a stored record, only the sanitized summary (task ID, task summary, lesson) is ever sent to a
provider, and only for records a search could actually return; at retrieval, the request's task text is embedded
too. Writes are conditional on the record's revision, so a late write can never
overwrite newer state or bring back a deleted record.

Packages: `AgentExperience.Core` (`ExperienceIndexingService`) and `AgentExperience.Storage.Postgres.Vectors` (the
pgvector index). Leave both out and everything still works, text-only.

## Setting it up

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

Apply the schema on every deploy, as the owner role of the [two-role deployment](deployment.md#deploying-with-two-roles),
and grant the application role its privileges last, so the embedding table is covered:

```csharp
await ExperienceSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);        // 0001-0003 and 0005-0018 (no 0014), the base schema
await ExperienceVectorSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);  // 0004, the embedding table
await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
    ownerDataSource, new ExperienceApplicationRoleOptions("agent_experience_app"), cancellationToken);
```

They are separate on purpose. `0004` begins with `CREATE EXTENSION vector`, and pgvector is **not** a trusted
extension, so that statement ordinarily needs a superuser (on a managed service, whichever role that provider
designates). A text-only deployment never calls the second line and therefore never needs that privilege. If your
operators install the extension out of band, this call runs fine as an ordinary role — `CREATE EXTENSION IF NOT
EXISTS` is a no-op once it exists. Run the base migration first: `0004` has a foreign key to `experience_records`.

`ApplyApplicationRolePrivilegesAsync` gives the application role `SELECT`, `INSERT` and `DELETE` on
`agent_experience.experience_embeddings` — the table is derived and rebuildable, not a ledger, and removing a vector
is an ordinary index operation — and `UPDATE` only on the columns the upsert rewrites (`model_id`, `dimension`,
`content_hash`, `source_revision`, `embedding`, `updated_at`), never the record ID or the scope columns a removal
matches on. It never gets `TRUNCATE` or ownership, so it cannot build or drop the out-of-band HNSW index: run
`ExperienceVectorIndexMaintenance` as the owner. A role without `SELECT` on `agent_experience.experience_grants` (or
a database that has not applied `0005`) falls back to the exact-scope predicate and reports it once through the
`onGrantsUnavailable` callback.

Both migrators share the `agent_experience.schema_versions` journal and the same advisory lock, so they serialize
against each other and against another host, and neither can claim the other's journal entries.

**No `UseVector()` needed.** The adapter sends vectors as pgvector's own text literal and casts them in SQL, so it
works on whatever `NpgsqlDataSource` the host built. You can still call `UseVector()` for your own queries.

**Why a separate package.** `AgentExperience.Storage.Postgres` deliberately takes **no** vector or model-provider
dependency — its dependency boundary test pins its package set exactly and forbids `Pgvector`, `VectorData`, and
`Microsoft.Extensions.AI`. A host that only wants canonical storage and text retrieval should not pull those in.

## Indexing after finalization

If an `ExperienceIndexingService` is registered, finalization embeds each record it commits, right after the commit:

```csharp
var result = await finalization.FinalizeAsync(request, cancellationToken);

if (result.Indexing is { IsIndexed: false } indexing)
{
    // Never a reason to treat the record as anything less than durable.
    logger.LogWarning("Experience {Id} is {Status} but not indexed ({Outcome}, retryable: {Retryable}): {Reason}",
        result.ExperienceId, result.Status, indexing.Outcome, indexing.IsRetryable, indexing.Failure?.Reason);
}
```

| Outcome | When | What was written |
| --- | --- | --- |
| `Indexed` | The summary was embedded and stored | The vector and its descriptor |
| `Skipped` | This model already embedded exactly this text | Nothing — and **no provider call was made** |
| `Stale` | The record moved to a newer revision before the write landed | Nothing; the stored vector is unchanged. Retryable |
| `Missing` | The record no longer exists in this scope | Nothing, and **no row is created** — an in-flight write cannot resurrect a deleted record |
| `Ineligible` | The record's status or confidence means a search could never return it | Nothing, and **nothing was sent to a provider** |
| `ProviderFailed` | The provider threw, timed out, or returned a vector of the wrong width or with a non-finite component | Nothing. The record stays committed, durable, and text-searchable. Retryable |
| `IndexFailed` | The index itself failed or refused the write | Nothing. Retryable |
| `Denied` | The scope lies outside the authorization | Nothing was read, embedded, or written |

Infrastructure failures in the index throw `ExperienceStoreException` with the driver's exception inside; caller
cancellation surfaces as an unwrapped `OperationCanceledException`. Core catches all of it and reports it as a
retryable indexing result or an explicit text-only retrieval — nothing here can fail a canonical write or a
finalization.

## What is embedded, and what is stored

**Only the sanitized retrieval summary is embedded** — the task ID, the sanitized task summary, and the reflection's
lesson, the same three fields the text index analyzes. Attempts, tool calls, evidence, provenance, and environment
metadata are never sent to a provider. The summary is read from the database at index time, not from a record the
caller happens to be holding, so what is embedded is what is really stored, at the revision it is really stored at.

The two channels read the same *fields* but not necessarily the same *length*: the embedded summary is capped at
8,192 characters (`ExperienceRetrievalSummary.MaxLength`), while `0003` analyzes the concatenation up to 100,000. A
record whose summary and lesson together run past 8 KB is therefore matched on more of its text by words than by
meaning. Both caps are far past any realistic summary. The cap is applied before the content hash is taken, so the
hashed text and the text the provider sees are always identical, and it never splits a surrogate pair.

**Only records a search could actually return are embedded.** The indexing scan applies the same status filter and
confidence floor the vector search applies, and the post-commit hook checks the record before calling anything, so
a `Quarantined`, `Revoked`, `Superseded`, or `Candidate` record's summary and lesson never leave the database for a
third party — its vector could never be returned anyway.

| Column | What it is |
| --- | --- |
| `experience_id` | Primary key, `REFERENCES experience_records … ON DELETE CASCADE` |
| `tenant_id` … `user_id` | The record's scope, **copied from the record row** inside the write, never from caller input |
| `model_id`, `dimension` | What decides comparability. A query vector is only ever compared with vectors from the same model at the same width |
| `content_hash` | SHA-256 over the model ID and the normalized summary, so an unchanged record can be skipped without calling a provider |
| `source_revision` | The record revision the summary was read at — what makes every write conditional |
| `embedding` | An **unconstrained** `vector`. The dimension belongs to whichever model a host configured, and `CHECK (vector_dims(embedding) = dimension)` keeps the two from ever disagreeing |
| `created_at`, `updated_at` | UTC, truncated to whole microseconds like the rest of the schema |

None of this takes part in a lifecycle decision. Model ID, dimension, content hash and source revision are kept
entirely separate from lifecycle state; they exist so a write can be conditional, a re-index can be free, and a query
vector is never compared with something it is not comparable with. Status, revision, and reuse confidence live on the
record and are never read from or written to this table. Reuse confidence is only ever *read* here, as a floor in the
search predicate. A record whose score drops below the floor stops being returned without anything being rewritten or
re-embedded; the number the search compares is the one the join reads from `experience_records`, so it is never
stale.

**Crypto-shredding does not seal this table.** With an `ExperienceEncryption` (see
[Crypto-shredding](crypto-shredding.md)), pass the same instance to `PostgresExperienceEmbeddingIndex`: the re-index
scan then opens a sealed record's task ID, summary and lesson in process with the record's key, so the summary, its
hash and its vector are exactly what the plaintext record would have produced, and a record whose key was destroyed
is never scanned, embedded or returned again. But pgvector has to read a vector in the clear to search it, so
`embedding` and `content_hash` are stored as before. They are derived from the erased text, erasure deletes them from
the live table, and every copy made before the erasure — backups, replicas, WAL, the dead tuple — still holds them.
That is part of the KL-2 boundary in [Known limits and documented boundaries](../known-limits.md#documented-boundaries).

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

`WriteAsync` takes `FOR KEY SHARE` on the record row in the same statement that checks it is not a tombstone, so a
write already in flight when an erasure commits is parked against the purge and re-checks when it is released,
rather than landing afterwards. That matters more here than anywhere else: a stored vector is a searchable
derivative of exactly the summary and lesson the erasure was asked to destroy, so a write that slipped through
would put a queryable copy of erased content back into the database.

## Removing a vector when a record leaves eligibility

`RemoveAsync` deletes one record's stored vector within exactly one scope. It is the mirror of the conditional
write: only `Validated` and `Reinforced` records may be returned, so a record that is contested, made stale,
superseded or revoked should not keep a row a search could match.

| Situation | Outcome |
| --- | --- |
| A vector was stored in this scope | `Removed` |
| Never indexed, already removed, or in another scope | `NotIndexed` — one outcome for all three, so removal is idempotent and a foreign-scope attempt reveals nothing |
| Scope outside the authorization | `Denied`, before any statement is issued |
| Malformed request | `Invalid`, with the field path |

The `DELETE` matches the embedding row's **own** scope columns, which were copied from the record when the vector
was written, so removal never depends on joining back to the record.

`ExperienceLifecycleService` calls this through `ExperienceIndexingService.RemoveAsync` as a post-commit hook, after
a transition that left eligibility has already landed, on its own budget. What removal buys is storage and index
maintenance cost, not reachability: the search joins the canonical record and filters on its status, so a vector left
behind is *already* unmatchable. That is why removal can never fail the transition that asked for it, and why nothing
retries it: `ScanAsync` lists only records a search *could* return, so a re-index pass never removes anything. A
`Deindexing` outcome other than `Removed` or `NotIndexed` is a work item for the host (see
[Lifecycle](lifecycle.md#committing-a-transition)).

**Erasing the record removes its vector too, from the base package's transaction.** `DeleteAsync` leaves a
payload-free tombstone rather than deleting the record row, so `0004`'s `ON DELETE CASCADE` never fires — the base
package's purge function deletes the embedding explicitly instead, guarded by `to_regclass` so a deployment without
the vectors package simply skips the step. Afterwards the tombstone can never be indexed again: a write against it
reports `Missing` rather than `Stale`, because no revision of an erased record can ever be indexed, and `ScanAsync`
does not offer it, because a tombstone has no summary and no lesson to embed.

**What the erasure leaves behind here.** Dead entries stay in the HNSW index until `VACUUM` reclaims them. Those
entries point at heap tuples that are themselves dead, so a query cannot return them — but the two halves are not the
same: the *index* entry cannot return anything, while the *heap* tuple it points at is still the row, vector and all,
until `VACUUM` reclaims it. A deployment with an erasure deadline has to `VACUUM agent_experience.experience_embeddings`
itself rather than wait for autovacuum, and needs `VACUUM FULL` or a storage-level guarantee if it must also defeat
forensic recovery of freed pages. See [Deletion and retention](deletion-and-retention.md#what-deletion-does-not-reach-in-plaintext-mode).

## Re-indexing

Re-indexing is explicit, scoped, and idempotent. It never runs on its own:

```csharp
var pass = await indexing.ReindexAsync(
    authorization,
    new ReindexExperienceRequest(scope, ExperienceIds: null, Limit: 100),   // bounded; pass again to page
    cancellationToken);

logger.LogInformation("{Examined} examined, {Indexed} re-embedded, {Skipped} unchanged, {Failed} failed",
    pass.Examined, pass.Indexed, pass.Skipped, pass.Failed);
```

A pass is **bounded and resumable**: records are considered in ascending `ExperienceId` order, and
`pass.LastExaminedId` is the cursor to hand to the next pass's `StartAfterId`. Keep going until it comes back `null`,
which is how a scope larger than one page is walked to the end. Underneath, `ScanAsync` lists, for one scope
(optionally narrowed to specific IDs, always bounded), each record's current revision, its normalized summary, and the
descriptor of whatever vector is already stored for it — all from one snapshot — applying the **same status filter
and confidence floor the search applies**.

For each record, the same model **and** the same content hash means **skipped**: no provider call, no write. Anything
else is re-embedded and written conditionally. The content hash covers the model ID and the normalized summary, so
running a pass twice over unchanged records costs one read and nothing else, and running it twice over a changed
record rewrites it exactly once. Changing the model looks exactly like changing the text, which is the point: two
models produce incomparable vectors, so "same text" alone must never be enough to skip.

**Provider calls are batched; writes are not.** The records a pass has to embed go to the provider
`EmbeddingBatchSize` at a time (an init-only property on `ReindexExperienceRequest`: default 16, from 1 to 128)
through `IExperienceEmbeddingGenerator.GenerateBatchAsync`, so a pass of `n` records to embed makes `ceil(n / 16)`
provider calls rather than `n`. Each record is then written on its own, conditionally on the revision it was read at,
so a record that moved on is `Stale` alone and the rest of its batch is written. A provider batch is all or nothing,
so one that throws, or answers with the wrong number of vectors, is retried one record at a time: a record the
provider always refuses (too long, filtered) is `ProviderFailed` alone instead of keeping its batch-mates unindexed on
every pass. An outage costs one extra provider call per failed batch, and a pass that fails partway still says
exactly which records it wrote. `Skipped` records never take a place in a batch. Tune `EmbeddingBatchSize` to your
provider's per-request limit: `AiExperienceEmbeddingGenerator` forwards a batch as it stands.

## Bringing your own generator

`IExperienceEmbeddingGenerator` is `ModelId`, `Dimension`, `GenerateAsync(string, …)` and
`GenerateBatchAsync(IReadOnlyList<string>, …)`, and it speaks domain types only. `GenerateBatchAsync` is shaped like
`Microsoft.Extensions.AI`'s own batch call (a list in, one vector per input out, in order) and has a default
implementation that calls `GenerateAsync` once per text — correct, but with no saving.

`AiExperienceEmbeddingGenerator` adapts a `Microsoft.Extensions.AI` `IEmbeddingGenerator<string, Embedding<float>>`
to the port and overrides the batch with **one** provider call; an answer with the wrong number of embeddings, or one
of the wrong width, fails the whole batch rather than being paired with the wrong record. It forwards the batch as it
stands — Core's indexing pass bounds it (`ReindexExperienceRequest.EmbeddingBatchSize`, at most 128) — so a provider
with a smaller per-request limit is the underlying generator's to split. It takes the model ID and dimension from the
generator's `EmbeddingGeneratorMetadata` unless you pass your own. Both are read **once**, at construction: the
content hash covers the model ID, so "the same text under the same model" has to be recognizable before any provider
call. A generator that reports neither fails at wiring time rather than mid-query.

Tests, and any deployment that wants reproducibility, can implement the port directly with a deterministic
function — that is exactly what this repository's integration tests do, which is why they need no model credentials.

## The HNSW index is created out of band

`0004` cannot create it: an HNSW index needs a dimension, and a shipped migration does not know which model a host
runs. So it is an explicit call:

```csharp
// As the owner role: creating an index needs ownership of the table.
await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(ownerDataSource, dimension: 1536, cancellationToken);
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
being used. That is also why the `embedding` column is an unconstrained `vector`.

It is **optional**: every search is correct without it (pgvector falls back to an exact scan, which is what a small
deployment wants anyway). It changes latency, and it makes search *approximate* — HNSW may miss a true nearest
neighbour. Building it over many rows takes minutes and locks the table against writes, so run it from a
maintenance path, never from request handling. `DropHnswIndexAsync` removes it again.

How the vector search itself works is in [Retrieval](retrieval.md#the-vector-channel).
