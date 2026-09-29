# AgentExperience.Storage.InMemory

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

> **For development and tests only.** Everything these stores hold is lost when the process ends, and none of the
> PostgreSQL store's guarantees apply: no database-enforced append-only logs, no erasure (so no erasure reach), no
> backups, no two database roles, no crypto-shredding. Its registration refuses to run in any environment but
> `Development`, `Test` or `Testing` unless you override it explicitly.

In-memory storage for [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET), so you can run capture,
finalization, retrieval, injection and reuse feedback without provisioning PostgreSQL. It implements the three core
storage ports: the Experience Record store with its audited lifecycle (`InMemoryExperienceRecordStore`,
`IExperienceRecordStore`), word search over that same store (`InMemoryExperienceCandidateSource`,
`IExperienceCandidateSource`), and the reuse-feedback ledger (`InMemoryExperienceReuseFeedbackStore`,
`IExperienceReuseFeedbackStore`). All three are thread-safe.

Requires `Microsoft.Extensions.DependencyInjection.Abstractions` **10.0.12** and
`Microsoft.Extensions.Hosting.Abstractions` **10.0.3**, or any later release in the same major. Targets
`net10.0`. No dependency on Core, Npgsql or MAF.

## Registering it

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.InMemory.DependencyInjection;

builder.Services.AddAgentExperienceCore();
builder.Services.AddAgentExperienceInMemoryStorageForDevelopment();
```

That is the only registration, and its name says what it is for. It registers the three ports as singletons, sharing
one record store. A candidate source or feedback store you registered first keeps your implementation, but a
different `IExperienceRecordStore` registered first is refused at registration, because the in-memory candidate
source would find nothing that store writes. Calling it a second time changes nothing; a second call with an options
delegate throws, because the first call's options are already in force.

**The environment guard.** The stores run only when the environment is `Development`, `Test` or `Testing`
(case-insensitive); `Production`, `Staging`, any other name and a blank one are refused. The name is the registered
`IHostEnvironment`'s; with none registered (a plain console app, a test) it is `DOTNET_ENVIRONMENT`, else
`ASPNETCORE_ENVIRONMENT`, and with neither set there is nothing to check. A refused environment is caught twice: a
hosted service the registration adds throws from `StartAsync`, so a Generic Host refuses to start, and resolving any
of the stores throws `InvalidOperationException`. A demo that really must run elsewhere opts out explicitly:

```csharp
builder.Services.AddAgentExperienceInMemoryStorageForDevelopment(options => options.AllowProductionEnvironment = true);
```

In tests, construct the stores directly; direct construction is not guarded:

```csharp
var records = new InMemoryExperienceRecordStore();
var candidates = new InMemoryExperienceCandidateSource(records);
var feedback = new InMemoryExperienceReuseFeedbackStore();
```

## What it guarantees

It passes the same [store conformance suite](https://github.com/fabbrik/AgentExperience.NET/tree/main/tests/AgentExperience.Storage.Conformance)
as the PostgreSQL store: create-only records with IDs unique across every scope;
exact-scope reads that reveal nothing about another scope; idempotent lifecycle commits keyed on the event ID; the
optimistic revision, prior-status and supersession guards, under concurrency too; ordered, paged history; and feedback
idempotency. It validates every request with the PostgreSQL store's own rules, compiled from the same source file, so
it refuses exactly the requests that store refuses. The confidence ledger's rules hold as well: an evidence ID is
idempotent, independent evidence is counted once per independence key, and an assessment lands once per record.

Everything is copied on write: a record or feedback submission is stored as a deep copy whose collections are
read-only, so changing your instance afterwards changes nothing stored, and what a read returns is safe to share. As
in PostgreSQL, a record's `CreatedAt` and `UpdatedAt`, a lifecycle event's `OccurredAt` and a submission's
`ObservedAt` and `AttributedAt` are stored in UTC, truncated to whole microseconds.

## What it does not do

- **Search is close to PostgreSQL's, not the same.** The candidate source matches words in the task ID, the task
  summary and the reflection lesson: text is normalized to Unicode form KC and case-folded, and a word is a run of
  letters and digits (a combining mark stays in its word). A query's terms are its distinct words less common English
  stopwords (the list PostgreSQL's `english` configuration drops) and less terms shorter than two characters, and a
  record matches only when it contains **every** term, as PostgreSQL ANDs them; a query with no terms left matches
  nothing. Only the first 100,000 characters of a record's text are indexed, PostgreSQL's bound. Candidates come
  strongest first, then by ID, after the scope, status and confidence filters and before the limit. There is no
  stemming ("invoices" does not match "invoice") and no query syntax, so the two stores can return different records
  for the same query, and the **relevance values still differ from PostgreSQL's**: here it is the fraction of the
  terms a record contains, weighted towards task-summary hits, in (0, 1], so retrieval scores differ between the
  two stores.
- **No grants.** Sharing grants and the grant access log are not provided: every read behaves as if no grant
  exists, so a record is readable only in its own exact scope.
- **No embedding index**, so no hybrid (vector) retrieval.
- **No deletion, retention or encryption.**
- **No durability.** Restarting the process empties every store.

Move to [`AgentExperience.Storage.Postgres`](https://www.nuget.org/packages/AgentExperience.Storage.Postgres) for
anything you want to keep.
