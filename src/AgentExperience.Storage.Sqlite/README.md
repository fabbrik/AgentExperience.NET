# AgentExperience.Storage.Sqlite

Local, durable storage in one SQLite file for memory of what *worked*, verified by your own checks, for .NET agents:
a coding agent, a CLI, an MCP server, a single agent on one machine. Set it up with the
[quick start](https://github.com/fabbrik/AgentExperience.NET#quick-start) in the repository README and `.UseSqlite(path)`.

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

It implements, over one database file, the storage ports the learning loop needs: the Experience Record store with its
audited lifecycle (`SqliteExperienceRecordStore`), full-text search over it with SQLite's FTS5
(`SqliteExperienceCandidateSource`), failure-signature lookup (`SqliteExperienceSignatureSource`), the reuse-feedback
ledger, and the run store (`SqliteExperienceRunStore`) that keeps each completed run so it can be finalized in a later
process. Each passes the same store conformance suite as the PostgreSQL adapter.

Requires `Microsoft.Data.Sqlite` **10.0.12** (which brings the bundled native SQLite, built with FTS5) and
`Microsoft.Extensions.DependencyInjection.Abstractions` **10.0.12**, or any later release in the same major. Targets
`net10.0`. No dependency on Core, Npgsql or MAF.

## Registering it

```csharp
await ExperienceSqliteSchema.MigrateAsync("experience.db");   // once per start; applies nothing when current

builder.Services
    .AddAgentExperience(...)
    .UseSqlite("experience.db");
```

Nothing migrates the schema on its own: call `ExperienceSqliteSchema.MigrateAsync` first, or opt in with
`.UseSqlite("experience.db", options => options.MigrateSchemaOnFirstUse = true)`, which migrates the first time a store
is resolved. Migrating is journaled (`agent_experience_schema_versions`), each script in one transaction with its
journal row, so it is safe to repeat and safe to run from two processes at once. It puts the file in write-ahead-log
mode, so readers never wait for the writer.

Without the one-call setup, `services.AddAgentExperienceSqliteStorage("experience.db")` makes the same registration.
It refuses to sit beside another `IExperienceRecordStore`, because its candidate and signature sources search its own
file.

## What it keeps

- **The ports' rules.** Create-only records with IDs unique across every scope, exact-scope reads that reveal nothing
  across scopes, idempotent lifecycle commits, the revision, prior-status and supersession guards, the confidence
  ledger's evidence-ID idempotency and independence key, injection tracking and both holdouts. Every write runs in one
  `BEGIN IMMEDIATE` transaction with a busy timeout, so two connections or two processes committing from the same
  revision have exactly one winner.
- **The PostgreSQL payload.** A record's payload is the PostgreSQL store's version-1 JSON.
- **Erasure.** `SqliteExperienceRecordStore.DeleteAsync` leaves a payload-free tombstone and removes the record's
  search text, signatures, events, evidence, injection summary and holdout rows in one transaction; reads in its own
  scope then answer `Deleted`. SQLite may keep erased bytes in free pages until `VACUUM`, and in the write-ahead log
  until the next checkpoint.

## How it searches

A query's terms are read exactly as the in-memory source reads them (distinct words, less English stopwords and
one-character terms); each is looked up in an FTS5 index over the task ID, task summary and lesson, tokenized with
`unicode61` and stemmed with FTS5's **Porter** stemmer. Stemming makes it closer to the PostgreSQL source than to the
in-memory one: "invoices" matches "invoice". A record matches when it holds at least `MinimumMatchedTerms` of the
terms; relevance is the in-memory source's coverage, weighted towards the task summary.

## What it leaves out

Sharing grants and the grant access log (a read behaves as if no grant exists), encryption and crypto-shredding,
row-level security and database roles (anything that can open the file can read and write all of it), vectors, and
retention sweeps. For a team, a shared service or any of those, use `AgentExperience.Storage.Postgres`.
