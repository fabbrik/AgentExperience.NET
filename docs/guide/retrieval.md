# Retrieval: finding applicable experience

**In short.** `ExperienceRetrievalService.RetrieveAsync` takes a task description and returns the stored records that
apply to it, best first. Eligibility is decided before anything is ranked: only records in the exact scope, in an
eligible status, above the confidence floor, not expired and matching the required environment are considered. Each
result carries every ranking component and the weight applied to it, so the score is reproducible. The call is
bounded by a timeout (500 ms by default) and never throws for an expected outcome: a timeout or a failure returns
nothing, so the agent simply runs without memory. With the vectors package wired in, a second channel matches by
meaning; if that channel cannot be trusted, you get the text results and an explicit flag saying why.

Packages: `AgentExperience.Core` (the service); `AgentExperience.Storage.Postgres` (the text channel);
`AgentExperience.Storage.Postgres.Vectors` (the optional vector channel).

## The call

```csharp
using AgentExperience.Core.Retrieval;

var result = await retrieval.RetrieveAsync(
    new RetrieveExperienceRequest(
        Authorization: authorization,            // host-established; the request scope must lie inside it
        Scope: scope,                            // the exact scope to retrieve within, never widened
        TaskText: "refund ticket stuck on a lock",
        RequiredEnvironmentAttributes: new Dictionary<string, string> { ["region"] = "us-east" },
        CorrelationId: traceId)
    {
        // Optional: grades ranking, never excludes. See "Preferring an environment" below.
        PreferredEnvironmentAttributes = new Dictionary<string, string> { ["dotnet"] = "10.0" },
    },
    cancellationToken);

if (result.TimedOut)
{
    logger.LogInformation("Retrieval timed out for {CorrelationId}; the agent runs without memory", result.CorrelationId);
}

foreach (var ranked in result.Records)          // highest score first, ties by ExperienceId ascending
{
    logger.LogDebug("{Id} scored {Score} from {Components}",
        ranked.Record.ExperienceId,
        ranked.Score,
        string.Join(", ", ranked.Components.Select(c => $"{c.Kind}={c.Value}*{c.Weight}")));
}
```

`services.AddAgentExperienceRetrieval()` registers the service with `RetrievalPolicy.Default` and
`RankingWeights.Default`; pass your own to override. It is hybrid when an embedding index *and* a generator are
registered, and text-only, flagged, if either is missing.

## Eligibility comes before ranking

Nothing is scored before it is known to be reusable.

| Check | Where it runs | Effect |
| --- | --- | --- |
| Scope | SQL | Only records in the request's *exact* scope (or readable through an active [sharing grant](sharing.md)); a foreign scope reveals nothing |
| Status | SQL | Only `Validated` and `Reinforced`. `Candidate`, `Quarantined`, `Contested`, `Stale`, `Superseded`, and `Revoked` are never returned, whatever their text match |
| Reuse confidence | SQL | Below `RetrievalPolicy.MinimumConfidence` (default 0.5) is excluded |
| Authorship | SQL, then Core | Only when `RetrieveExperienceRequest.ExcludeModelAuthored` is set: a record whose reflection a model wrote (or whose producer is the library's own model-backed reflector) is excluded by each source before its limit, and Core excludes, as `ModelAuthored`, any a source still returned. A PostgreSQL row sealed without its authorship flag fails closed and is left out by its source. Off by default. See [Model-authored lessons](injection.md#model-authored-lessons) |
| Text match | SQL | PostgreSQL full-text search over task ID, task summary, and reflection lesson (analyzed up to 100,000 characters) |
| Vector match | SQL | pgvector cosine distance over the embedding of those same three fields (embedded up to 8,192 characters), filtered to the query's own model and dimension |
| Expiry | Core | Last lifecycle activity older than `RetrievalPolicy.MaxAge` is excluded. `null` (the default) means no expiry |
| Environment | Core | Every required attribute must equal the record's `EnvironmentFingerprint.Metadata` entry; a missing key excludes the record. A request with no required attributes sets `EnvironmentUnrestricted` on the result |

Scope, status, the confidence floor and, when the request asks for it, the authorship exclusion are pushed into
**both** channels as the same predicates, before each channel's limit, so neither can return something the other
would have filtered out.

`result.Excluded` itemizes what the **Core** checks removed — expiry, environment, and a model-authored record a source returned despite the exclusion — so "nothing matched" is
distinguishable from "something matched but was not reusable here". It is deliberately not a complete account of
everything filtered: scope, status, and the confidence floor are applied in SQL, so records they exclude never reach
Core and are never listed. That split is the point — a foreign-scope or revoked record must not be observable, even
as a count.

**"Recency" and "expiry" mean last lifecycle activity, not when the lesson was learned.** Both read
`ExperienceRecord.UpdatedAt`, which every lifecycle commit bumps. A years-old lesson reinforced yesterday is one day
old by this measure: it scores as fully recent and never expires. That is deliberate — recent revalidation is
evidence the lesson still holds — but it is not a measure of how old the underlying knowledge is, and a policy that
needs one should not use `MaxAge` for it. [Confidence decay](#decaying-confidence-by-domain) is the one measure
that reads `CreatedAt`, the age of the lesson itself.

## Two channels, one answer

When an embedding index and an embedding generator are both registered, the task text is also embedded and searched
as a vector, concurrently with the text search and inside the same timeout. The two candidate lists are then
deduplicated by `ExperienceId`, and a record found by both keeps the **higher** of its two normalized relevances.
Ranking runs once over the merged list, with the same five weights: there is no sixth axis and no "found by both"
bonus. An embedding can only make a record a *candidate* — it never decides eligibility, status, or confidence.

**A vector channel that cannot be trusted produces an explicit text-only answer, never a failure.** The text
candidates still come back, and `result.TextOnly` is `true` with `result.VectorFallback.Reason` saying which:

| `TextOnlyReason` | When | Vector comparison attempted? |
| --- | --- | --- |
| `NotConfigured` | No embedding index or no generator is registered — a supported, text-only deployment | No channel exists |
| `ProviderUnavailable` | The provider threw, cancelled for its own reasons (a client-side request timeout), or returned a query vector of the wrong width or with a non-finite component | No — caught before any query is issued |
| `ModelMismatch` | Every embedding stored in this scope came from a different model | No — excluded by the query's own predicate |
| `DimensionMismatch` | Every embedding stored in this scope is a different width | No — excluded by the query's own predicate |
| `VectorSearchFailed` | The vector search threw, was denied, or was refused as malformed | Attempted; nothing usable came back |

`TextOnly` is never set merely because the vector channel matched nothing: "nothing was semantically similar" and
"the vector channel could not be trusted" are different claims, and only the second one is a reason to look at your
wiring.

**There is a recall ceiling, and it is visible.** Each channel returns at most `RetrievalPolicy.CandidateLimit`
candidates (default 50), ordered by its own relevance, and ranking only ever sees those. So a record with a weaker
match but strong confidence, recency, or status is not ranked at all once that many stronger matches exist in both
channels: the weighting can only reorder what the ceiling let through. When *either* channel reaches its ceiling,
`result.Truncated` is `true` — the records beyond it are in no exclusion list either, because no eligibility check
ever looked at them. Raise `CandidateLimit` or narrow the task text when that matters. `request.Limit` may not
exceed `CandidateLimit`; a larger value is rejected rather than quietly capped.

## Ranking is explainable

Every returned record carries all five normalized components (each in 0–1) and the effective weight applied to it,
so the score is always reproducible from what the result holds.

| Component | Default weight | Normalized as |
| --- | --- | --- |
| Relevance | 0.35 | `ts_rank_cd` of the text match, or `1 - cosine_distance / 2` of the vector match — whichever is higher for that record — normalized to 0–1 |
| Confidence | 0.25 | The record's `ReuseConfidence`, or, with a [decay policy](#decaying-confidence-by-domain), that value times `2^(-age / halfLife)` for the record's domain. *Age* is measured from `CreatedAt` |
| Recency | 0.15 | `2^(-age / RecencyHalfLife)`, half-life 30 days by default. *Age* is measured from `UpdatedAt` |
| Status | 0.15 | `Reinforced` 1.0, `Validated` 0.5 |
| Environment compatibility | 0.10 | The environment scorer's value for the record against the request's `PreferredEnvironmentAttributes`. The default is the fraction of preferred attributes the record matches, and 1.0 when nothing is preferred. Every ranked record already satisfied the *required* attributes, since a mismatch excludes it before ranking |

Weights must be finite, non-negative, and sum to 1 (within `RankingWeights.SumTolerance`); anything else throws
`ArgumentOutOfRangeException` at construction, so an invalid weighting can never reach a retrieval call. Ties sort by
`ExperienceId` ascending and ordinal, so the ordering is total and stable, and a golden fixture pins the default
ordering together with every component value.

## Preferring an environment

Required attributes decide eligibility; preferred attributes only decide order. Set
`PreferredEnvironmentAttributes` on the request and a record captured in a closer environment outranks one from a
merely acceptable environment, with every other component equal:

```csharp
var request = new RetrieveExperienceRequest(authorization, scope, "refund ticket stuck on a lock",
    RequiredEnvironmentAttributes: new Dictionary<string, string> { ["region"] = "us-east" })
{
    PreferredEnvironmentAttributes = new Dictionary<string, string> { ["dotnet"] = "10.0", ["db"] = "postgres-17" },
};
```

- **The default scorer, `AttributeMatchEnvironmentScorer`, counts exact matches.** A record whose
  `EnvironmentFingerprint.Metadata` carries `dotnet=10.0` but `db=postgres-16` scores 0.5; one with neither key scores
  0.0 and is still returned. Matching is ordinal: `10.0` does not match `10.0.1`, and there is no version-range or
  semantic comparison.
- **Nothing preferred means nothing changes.** With no preferred attributes, the default scorer returns 1.0 for every
  record (`ExperienceRetrievalService.CompatibleEnvironmentScore`), so scores and ordering are exactly what they were
  before preferences existed.
- **Required attributes come first.** A record that fails a required attribute is excluded as `EnvironmentMismatch`
  and is never scored.
- **Bring your own scorer.** Implement `IEnvironmentCompatibilityScorer` and register it in the container as a
  singleton (`AddAgentExperienceRetrieval` resolves it once, from the root provider, in any registration order), or
  pass it to the `ExperienceRetrievalService` constructor. One instance serves concurrent retrievals, so it must be
  thread-safe. It is called for every eligible record, with an empty dictionary when nothing is preferred, so it can
  grade on the fingerprint alone, version ranges included. Its value is clamped to [0, 1] and NaN counts as 0.
- **A throwing scorer fails closed.** The retrieval is `Failed` with no records, never a partial ranking.
  `result.Failure.Reason` is fixed and content-free; `result.Failure.Exception` is the scorer's own exception, passed
  through as is, so treat it as local diagnostics. An `OperationCanceledException` thrown while the caller's token is
  cancelled propagates unwrapped instead.
- **Keep it cheap.** It runs synchronously after both channels answer and is not bounded by
  `RetrievalPolicy.Timeout`. The caller's cancellation token is checked between records, not during a call.
- **MAF.** Set the property in `ExperienceInjectionOptions.ResolveRequestAsync` (or `ResolveRequest`), where you
  build the request anyway. The injected Historical Reference block's format does not change; only the order of its
  records and the `EnvironmentCompatibility` value in each record's applicability line (verbose rendering) do, and,
  in the default compact rendering, whether a record gets an `Environment:` line.

## Decaying confidence by domain

Stored confidence never fades on its own. A lesson about a fast-moving framework API would otherwise rank as
confidently a year after it was verified as on the day it was, just like a stable business rule. A
`ConfidenceDecayPolicy` makes the confidence component fade at ranking time, at a rate set by the record's domain:

```csharp
services.AddSingleton(new ConfidenceDecayPolicy
{
    HalfLives = new Dictionary<string, TimeSpan?>
    {
        ["framework-api"] = TimeSpan.FromDays(30),   // fast-moving: halves every month
        ["security"] = TimeSpan.FromDays(60),
        ["business-rule"] = TimeSpan.FromDays(365),
        ["math"] = null,                             // an invariant: never decays
    },
    DefaultHalfLife = null,                          // missing or unlisted domain: no decay (the default)
});
services.AddAgentExperienceRetrieval();
```

- **The confidence component becomes `stored * 2^(-age / halfLife)`.** A record verified at 0.8 that is one half-life
  old ranks on 0.4. The factor is always in [0, 1], so decay can only lower a record's confidence, never raise it. It
  reaches exactly 0 only for an age vastly beyond the half-life, where the arithmetic underflows.
- **The domain is a metadata entry.** It is the value of the record's `EnvironmentFingerprint.Metadata[DomainKey]`,
  where `DomainKey` defaults to `"domain"`. It is matched ordinally against `HalfLives`, whatever comparer your
  dictionary used, so `Framework-API` is not `framework-api`. A domain mapped to `null` never decays, even when `DefaultHalfLife` is set. A record with no domain,
  or with one that is not listed, uses `DefaultHalfLife`.
- **Age is the lesson's age, measured from `CreatedAt`.** This differs from recency and expiry, which read `UpdatedAt`.
  Reinforcing a lesson already earns it recency, and a lesson about an API that has since moved on does not become
  current because someone reused it again. A `CreatedAt` in the future (clock skew) decays nothing.
- **It only reorders.** Eligibility, including the `MinimumConfidence` floor applied in SQL, uses the stored value, so
  decay never excludes a record, even one decayed far below the floor. A read never writes, so `ReuseConfidence` is
  never rewritten. Decay only reorders candidates already fetched: the candidate bound (`CandidateLimit`) and the
  store's confidence floor are applied on stored confidence first, so decay cannot bring in a record they left out.
- **It can change what an agent sees first.** Decay can change the order of the records injected into a MAF agent.
  In the Historical Reference block, the `Confidence:` line shows the stored confidence, while the `Applicability`
  components line shows the decayed value that was ranked on, so the two can differ for the same record.
- **Both values are reported.** When a half-life applied, the Confidence component's `Value` is the decayed value that
  was ranked on, and its `UndecayedValue` is the stored confidence. In every other case `UndecayedValue` is `null`.
  Without a policy, nothing changes: scores, components and ordering are exactly as before.
- **Invalid settings fail at construction.** A zero or negative half-life throws `ArgumentOutOfRangeException`, and a
  blank `DomainKey` or a null or blank domain in `HalfLives` throws `ArgumentException`. This also applies to a `with`
  expression. The map is copied into a read-only one, so changing the dictionary afterwards has no effect.
- **Wiring.** `AddAgentExperienceRetrieval` picks up a registered `ConfidenceDecayPolicy` in any registration order, or
  you can pass one to the `ExperienceRetrievalService` constructor. Age is measured with the service's `TimeProvider`.

## Bounded, and fail-closed

The whole call is bounded by `RetrievalPolicy.Timeout` (default 500 ms, maximum one day), measured with an injected
`TimeProvider`.

**Pre-model latency budget.** With injection, retrieval is the first of two bounded steps before the model call: the
worst case is about `RetrievalPolicy.Timeout` plus the provider's `EligibilityCheckTimeout` (default 500 ms each,
the host's `DecideInjection` time included in the latter), plus the host's own resolver callback. A search still
running when the timeout expires is abandoned: it keeps running against the store in the background, so the store
must tolerate concurrent use, and it may hold a pooled connection until its cancellation lands. The timeout is
released by a `TimeProvider` timer, so a starved thread pool can still release it late. See
[Pre-model latency budget](injection.md#pre-model-latency-budget).

**Abandoned searches are capped.** Against a store that hangs on every call, each retrieval would add one more
abandoned search to those already running, until the connection or thread pool ran out. So one
`ExperienceRetrievalService` counts the searches it has abandoned that are still running, and while that count is at
`RetrievalPolicy.MaxAbandonedSearches` (default 16, must be positive) it starts no new search: the call returns
`TimedOut` at once — the same empty result as a real timeout — without touching the store or either channel, and
`result.Failure.Reason` says the store has too many abandoned searches still running. As the abandoned searches end,
the count drops and retrievals reach the store again; there is no breaker state, probe or timer. A search abandoned
because the caller cancelled, or because a channel cancelled it, counts too, until it ends. The count is per service
instance, so the cap only engages when one `ExperienceRetrievalService` is shared and long-lived (for example a DI
singleton); a service built per request never reaches it. An abandoned search
is also detached from the caller's cancellation token the moment it is abandoned, so a long-lived token does not
collect one registration per search that never ends.

| Situation | Outcome | Records |
| --- | --- | --- |
| Ran inside the timeout | `Completed` | Every eligible record among the candidates considered, ranked and cut to the request's limit. Check `result.Truncated`: `true` means more matched than were considered |
| Exceeded the timeout | `TimedOut` (`result.TimedOut`), with the request's `CorrelationId` — never an exception | Empty |
| `MaxAbandonedSearches` abandoned searches still running | `TimedOut` at once, with `result.Failure` saying why; no channel is issued a query | Empty |
| Request scope outside the authorization | `Denied` | Empty; **neither channel is issued a query, and nothing is embedded** |
| The **text** search failed, or a candidate from either channel could not be read, came back out of scope, or was returned twice | `Failed`, with `result.Failure` | Empty, never unfiltered |
| The environment compatibility scorer threw | `Failed`, with `result.Failure` (a fixed reason; the scorer's own exception, passed through as is) | Empty, never a partial ranking |
| The **vector** channel failed, timed out on its own, or was incomparable | `Completed`, with `result.TextOnly` and `result.VectorFallback` | The text channel's eligible records, ranked |
| Caller cancelled | `OperationCanceledException`, unwrapped and distinct from the timeout | — |

`result.Failure.Reason` is content-free and safe to log. `result.Failure.Exception`, when present, is whatever the
port threw — a driver message can quote SQL text or connection detail, so treat it as local diagnostics rather than
something to pass on.

Retrieval returns ranked records and the evidence for their ranking. Turning them into a labeled Historical Reference
and injecting it into an agent is a separate step — see [Injection into MAF](injection.md) — and retrieved content
never becomes authority.

## The text channel

`PostgresExperienceCandidateSource` answers one question — *which stored records look relevant to this task text?* —
and nothing else. It is a separate port from the store on purpose: it only reads, it needs only `SELECT`, and a host
that never retrieves does not have to register it.

It runs in the same order as every store operation: validate the query, check the request scope against the
host-established `AuthorizationContext`, and only then open a connection. A scope outside the context is `Denied`
before any connection opens, and the scope predicate is applied in SQL exactly as it is for reads.

**What runs in the database:** the exact scope predicate (or an active grant), the caller's eligible status set, the
reuse-confidence floor (inclusive), the text match, and the limit (1–200, default 50). Nothing else. Because those
filters run in SQL, records they exclude never reach the caller and are never itemized anywhere — which is the point
for scope, and worth remembering for status and confidence. `EligibleStatuses` must be non-empty: an empty set is
`Invalid` rather than widened to "every status", so a caller can never accidentally ask for records it considers
ineligible.

**What is indexed:** the task ID, the sanitized task summary, and the reflection's lesson — the fields that say what
a record is *about*. Attempts, tool calls, evidence, and environment metadata are deliberately not indexed: matching
on them would make retrieval recall incidental identifiers and error strings rather than applicable experience.

**The query text** goes through `websearch_to_tsquery`, which accepts arbitrary user input — quotes, `or`, `-`,
stray punctuation — and never raises a syntax error, so callers do not escape or sanitize around it. Multiple words
are combined with AND, and it is capped at `ExperienceCandidateQuery.MaxTaskTextLength` (4096) characters; longer is
`Invalid` before a connection opens. The text-search configuration is `english`, fixed by the generated column;
changing it means a new migration that rebuilds the column, because already-indexed rows would otherwise keep the
old analysis.

Because the `english` configuration drops stopwords, **text made only of stopwords matches nothing at all** — `"the
of and"` produces an empty query, and an empty query matches no row by construction. The result is an ordinary
`Found` with no candidates, indistinguishable from "nothing relevant is stored". A caller that wants to tell those
apart has to decide it before calling.

**Relevance** is `ts_rank_cd` with normalization flag 32 (`rank / (rank + 1)`), so it is already in [0, 1). It is a
within-search measure: two candidates' relevances are comparable to each other, never to a relevance from a different
query. Candidates come back in descending relevance, ties broken by `experience_id` in PostgreSQL `uuid` byte order;
Core re-sorts with its own total, ordinal tie-break when it ranks.

In crypto-shredding mode a sealed record is matched on `search_vector_sealed` rather than the generated
`search_vector`, and ranks exactly as its plaintext twin would (see
[Crypto-shredding](crypto-shredding.md#search-and-why-the-residual-is-what-it-is)).

## The vector channel

```sql
SELECT <record columns>, (e.embedding::vector(n) <=> CAST(@query_vector AS vector(n))) AS distance
FROM agent_experience.experience_embeddings e
JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id
WHERE ((<exact scope on e and r>) OR <an active sharing grant naming r and permitting this scope>)
  AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence
  AND e.model_id = @model_id AND e.dimension = n
ORDER BY (e.embedding::vector(n) <=> CAST(@query_vector AS vector(n))) LIMIT @limit
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
so what it proves is that the HNSW index is *reachable* for the ordering — not that the scope filter stays
index-served once the `OR` is there. Measure on your own data before assuming it does.

The alternative to that exact match is an **active sharing grant** — issued, not revoked, and not expired as of the
database's own `clock_timestamp()` — naming the record and permitting the requesting scope. It is the base package's
predicate, composed rather than retyped, so both retrieval channels honour byte-for-byte the same rule about what a
grant does. The grant branch is stated on the record side only: an embedding carries its *owner's* scope, so an
`e`-side exact match would exclude exactly the rows the grant exists to admit. A shared record is still subject to
every other predicate here — status, confidence, model, and width — so sharing widens who may read a record, never
what makes one comparable or eligible.

The distance expression is the **only** sort key. A tie-break on `experience_id` would force the
whole join to be sorted and the HNSW index never to be used, so exact distance ties are broken arbitrarily — which
costs nothing, because Core re-sorts every candidate by score and breaks its own ties on `ExperienceId`.

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
