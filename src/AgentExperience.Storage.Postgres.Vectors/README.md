# AgentExperience.Storage.Postgres.Vectors

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

The optional pgvector half of [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET)'s PostgreSQL
storage. It stores one embedding per Experience Record so retrieval can find records by meaning as well as by words.
It implements `IExperienceEmbeddingIndex`, adapts any `Microsoft.Extensions.AI` embedding generator to
`IExperienceEmbeddingGenerator`, and is used by Core's `ExperienceIndexingService` and `ExperienceRetrievalService`.

Leave it out and everything still works, text-only: retrieval reports `TextOnly` with reason `NotConfigured`.

| Dependency | Version |
| --- | --- |
| `AgentExperience.Storage.Postgres` | this release |
| `Npgsql` | `10.0.3` or later, within 10.x |
| `Pgvector` | `0.3.2` or later, within 0.3.x |
| `Microsoft.Extensions.AI.Abstractions` | `10.10.0` or later, within 10.x |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.12` or later, within 10.x |

Each is a floor; CI tests the floor and the newest release in the range on every change. Targets `net8.0`, `net9.0`
and `net10.0`; tested against PostgreSQL 15, 16, 17 and 18 with pgvector. It is a separate package so that a
text-only host never takes a vector or model-provider dependency.

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

Apply the schema on every deploy, as the owner role, and grant the application role its privileges last, so the
embedding table is covered:

```csharp
await ExperienceSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);        // the base schema
await ExperienceVectorSchemaMigrator.MigrateAsync(ownerDataSource, cancellationToken);  // 0004, this package's schema
await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
    ownerDataSource, new ExperienceApplicationRoleOptions("agent_experience_app"), cancellationToken);
```

`0004` begins with `CREATE EXTENSION vector`, and pgvector is not a trusted extension, so that statement ordinarily
needs a superuser; if your operators create the extension out of band, the call runs as an ordinary role. Run the
base migration first: `0004` has a foreign key to `experience_records`. No `UseVector()` call is needed on your data
source.

Optionally, create an HNSW index for your model's dimension, from a maintenance path, as the owner:

```csharp
await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(ownerDataSource, dimension: 1536, cancellationToken);
```

Every search is correct without it (an exact scan); with it, search is faster on large tables and *approximate*.
Building it locks the table against writes.

## What to know

- **Embeddings are derived data.** They are written after the record is committed, on their own connection. A failing
  or missing provider never blocks or undoes a record; the record stays committed, text-searchable and retryable.
- **Only the sanitized summary leaves the database**: task ID, task summary and lesson, capped at 8,192 characters,
  and only for records a search could return. Attempts, tool calls, evidence and environment never do.
- **Writes are conditional** on the record's revision and existence, in SQL, so a late write can never overwrite
  newer state or bring back a deleted record.
- **Vectors are compared only within one model and one dimension.** A scope whose vectors all come from another model
  or width falls back to text-only, flagged with the reason.
- **Re-indexing is explicit, scoped, bounded and idempotent.** Unchanged records are skipped without a provider call;
  provider calls are batched (`EmbeddingBatchSize`, 16 by default, at most 128).
- **Crypto-shredding does not seal the embedding.** pgvector has to read it in the clear, so copies of it survive in
  backups after erasure (the KL-2 boundary). Pass the same `ExperienceEncryption` to the index so a sealed record is
  embedded from its opened text.

The full detail — stored columns, outcomes, removal on leaving eligibility, the search statement, bringing your own
generator — is in [Indexing](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/indexing.md) and
[Retrieval](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/retrieval.md#the-vector-channel).

The integration tests embed with a deterministic in-test generator, so no model credentials are ever needed.

## More

- Documentation: [guide and glossary](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/README.md)
- Supported versions: [compatibility evidence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/compatibility-evidence.md#the-version-policy-floors-and-one-bounded-range)
- Changes between previews: [changelog](https://github.com/fabbrik/AgentExperience.NET/blob/main/CHANGELOG.md)
- License: Apache-2.0
