# Changelog

AgentExperience.NET is a **preview**. It is not production ready, and public APIs may change between previews. The
[Known limits](README.md#known-limits) table lists every limit that is still unresolved.

## 0.1.0-preview.2

This preview resolves ten known limits: KL-1, KL-3, KL-5, KL-6, KL-7, KL-9, KL-10, KL-14, KL-15 and KL-16. The six
still in the table (KL-2, KL-4, KL-8, KL-11, KL-12, KL-13) are boundaries of the design, and code alone cannot remove
them.

### Upgrade, in this order

1. **Stop every writer running `0.1.0-preview.1`.** Once `0011` is applied, an old build can no longer write grant
   events or access rows.
2. **Run the schema migrator.** It applies `0011_grant_disclosure` and `0012_grant_access_retention`. Neither edits a
   journaled script, and `0012`'s header describes building its index out of band first, for large tables.
3. **Deploy `0.1.0-preview.2`.** Against a schema without `0011`, this build fails every grant-joined read with
   `42703`.

### Breaking changes

- **`RequiredCheck` must name an evidence kind** (KL-5). `new RequiredCheck("id")` no longer compiles. To accept any
  kind, pass `RequiredCheck.AnyKind`.
- **An evaluation is bound to its run** (KL-6).
  - `VerificationAggregator.Aggregate` now takes `runId` as its first argument.
  - `VerificationResult` can only be produced by the aggregator: it has no public constructor, and its properties are
    get-only.
  - `ReflectionRequest.Run` and `ReflectionRequest.Evaluation` are get-only. A mismatched pair throws
    `ReflectionBindingException`.
  - Finalization quarantines a reflection that does not match its request.

### Behaviour changes

- **Existing sharing grants become `LessonOnly`** (story 3.6, KL-9). A borrowed record loses its `Approach:` line
  until its owner revokes the grant and issues it again as `LessonAndApproach`. The recipient has no access between
  the two calls.
- **Every run is bounded from the moment it opens** (KL-7). This includes the default single-invocation path.
  - An invocation still running after one `MaxOpenRunDuration` period (5 minutes by default) is handed the bound.
  - At twice that duration, its run is completed as `Cancelled` and the closure is reported. The invocation's answer
    is untouched, but its attempt is refused.
- **Retention sweeps count only what they erase.** `DeletedCount` no longer includes records that another caller
  erased first.
- **The injection eligibility check re-checks its time limit before each record and after the last decision**, from
  elapsed time as well as its timer. A slow host decision can no longer slip a record in after the limit.

### Added

- **A grant disclosure level:** `ExperienceGrantDisclosure`, recorded on grants, grant events and every access row
  (KL-9).
- **Retention for subtrees and for the access log** (KL-3, KL-10).
  - `ScopeMatch.Subtree` for retention sweeps.
  - `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync`, which keeps every access row for at least 30 days.
- **Batch reads and embeddings** (KL-1), both as default interface methods, so no implementer breaks.
  - `IExperienceRecordStore.GetManyAsync`.
  - `IExperienceEmbeddingGenerator.GenerateBatchAsync`, with `ReindexExperienceRequest.EmbeddingBatchSize`.
  - One injection re-read now takes 2 database commands instead of 12, for 8 candidates.
- **Telemetry for erasure** (KL-16): `delete`, `retention.sweep`, `grant.purge` and `grant.access.purge`, on the
  `AgentExperience.Storage.Postgres` source and meter.

### Dependencies

These moved (KL-14, KL-15), and every `PackageReference` a shipping project declares is now exact:

| Package | From | To |
| --- | --- | --- |
| `Microsoft.Agents.AI` | `1.20.0` | `1.22.0` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.11` | `10.0.12` |
| `Microsoft.Extensions.Compliance.Redaction` | `10.9.0` (floor) | `[10.10.0]` (exact) |
| `Microsoft.Extensions.AI.Abstractions` | `10.9.0` | `10.10.0` |

## 0.1.0-preview.1

The first preview: Epics 1–4. See the
[release](https://github.com/fabbrik/AgentExperience.NET/releases/tag/v0.1.0-preview.1).
