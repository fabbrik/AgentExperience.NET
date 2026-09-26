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
        CorrelationId: traceId),
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
| Text match | SQL | PostgreSQL full-text search over task ID, task summary, and reflection lesson (analyzed up to 100,000 characters) |
| Vector match | SQL | pgvector cosine distance over the embedding of those same three fields (embedded up to 8,192 characters), filtered to the query's own model and dimension |
| Expiry | Core | Last lifecycle activity older than `RetrievalPolicy.MaxAge` is excluded. `null` (the default) means no expiry |
| Environment | Core | Every required attribute must equal the record's `EnvironmentFingerprint.Metadata` entry; a missing key excludes the record. A request with no required attributes sets `EnvironmentUnrestricted` on the result |

Scope, status, and the confidence floor are pushed into **both** channels as the same predicates, so neither can
return something the other would have filtered out.

`result.Excluded` itemizes what the **Core** checks removed — expiry and environment — so "nothing matched" is
distinguishable from "something matched but was not reusable here". It is deliberately not a complete account of
everything filtered: scope, status, and the confidence floor are applied in SQL, so records they exclude never reach
Core and are never listed. That split is the point — a foreign-scope or revoked record must not be observable, even
as a count.

**"Recency" and "expiry" mean last lifecycle activity, not when the lesson was learned.** Both read
`ExperienceRecord.UpdatedAt`, which every lifecycle commit bumps. A years-old lesson reinforced yesterday is one day
old by this measure: it scores as fully recent and never expires. That is deliberate — recent revalidation is
evidence the lesson still holds — but it is not a measure of how old the underlying knowledge is, and a policy that
needs one should not use `MaxAge` for it.

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
| Confidence | 0.25 | The record's `ReuseConfidence` |
| Recency | 0.15 | `2^(-age / RecencyHalfLife)`, half-life 30 days by default. *Age* is measured from `UpdatedAt` |
| Status | 0.15 | `Reinforced` 1.0, `Validated` 0.5 |
| Environment compatibility | 0.10 | 1.0 for a record that satisfied the request's required attributes — which every ranked record did, since a mismatch excludes it before ranking |

Weights must be finite, non-negative, and sum to 1 (within `RankingWeights.SumTolerance`); anything else throws
`ArgumentOutOfRangeException` at construction, so an invalid weighting can never reach a retrieval call. Ties sort by
`ExperienceId` ascending and ordinal, so the ordering is total and stable, and a golden fixture pins the default
ordering together with every component value.

## Bounded, and fail-closed

The whole call is bounded by `RetrievalPolicy.Timeout` (default 500 ms, maximum one day), measured with an injected
`TimeProvider`.

| Situation | Outcome | Records |
| --- | --- | --- |
| Ran inside the timeout | `Completed` | Every eligible record among the candidates considered, ranked and cut to the request's limit. Check `result.Truncated`: `true` means more matched than were considered |
| Exceeded the timeout | `TimedOut` (`result.TimedOut`), with the request's `CorrelationId` — never an exception | Empty |
| Request scope outside the authorization | `Denied` | Empty; **neither channel is issued a query, and nothing is embedded** |
| The **text** search failed, or a candidate from either channel could not be read, came back out of scope, or was returned twice | `Failed`, with `result.Failure` | Empty, never unfiltered |
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
