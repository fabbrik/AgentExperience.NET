# AgentExperience.NET

[![CI](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/fabbrik/AgentExperience.NET/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)

**Portable, evidence-backed experience memory for .NET agents.**

AgentExperience.NET captures what an AI agent actually tried, verifies whether it worked, and turns the result into an auditable lesson that future runs can reuse safely. It sits between [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) execution and durable storage, without replacing either.

> **Status: early development.** Epic 1 (capture and explain agent experience) is implemented and tested, and so is Epic 2 (reuse relevant experience): a completed run can now be finalized into a durable Experience Record in PostgreSQL in one call, moved through its lifecycle with atomic, audited commits, indexed as an embedding after the fact, retrieved by task text *and* by meaning with bounded, explainable ranking, and injected back into a later MAF invocation as a labeled, bounded Historical Reference. Governance is planned (see [Roadmap](#roadmap)). Nothing is published to NuGet yet, and APIs may change.

## Why

Conversation history and fact memory don't answer the questions that matter when an agent retries similar work:

- Which approaches failed, and which succeeded?
- How was success *verified*, not just claimed?
- In which environment does the lesson apply?
- Is it safe for another agent to reuse?

AgentExperience.NET records observable evidence (tool calls, results, errors, verification checks) and never stores hidden chain-of-thought.

## What works today

| Capability | Where |
| --- | --- |
| Domain contracts: experience runs, attempts, evidence, outcomes, reflections, scope, environment | `AgentExperience.Abstractions` |
| Sanitization before storage: per-kind allowlists, secret redaction, fail-closed rejection | `AgentExperience.Core` |
| Thread-safe in-memory run capture with idempotent appends and size limits | `AgentExperience.Core` |
| Deterministic task verification: exit codes, tests, workflow and human checks; host-closed rounds; no LLM | `AgentExperience.Core` |
| Auditable, template-based reflections traceable to evidence IDs | `AgentExperience.Core` |
| MAF adapter: captures ordinary, streaming, failed, and cancelled runs plus tool calls, without altering results | `AgentExperience.MicrosoftAgentFramework` |
| PostgreSQL Experience Record store: create, get, and scoped query; host authorization checked before database access; exact scope matching in SQL | `AgentExperience.Storage.Postgres` |
| Atomic audited lifecycle commits: the event and the record's projection in one transaction, idempotent by event ID, revision-checked, with append-only history | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Journaled schema migrations: embedded scripts applied once, one transaction per script, serialized across processes by an advisory lock | `AgentExperience.Storage.Postgres` |
| One finalization call: evaluate, gate on authorization and the host's storage decision, reflect, create the record as a `Candidate`, commit the initial event that promotes it — replay-safe and structured at every stage | `AgentExperience.Core` |
| Text retrieval of applicable experience: eligibility decided before ranking, every ranking component and effective weight exposed, bounded by a timeout that is never an exception | `AgentExperience.Core`, `AgentExperience.Storage.Postgres` |
| Embedding ingestion after the canonical commit: only the sanitized retrieval summary is embedded, writes are conditional on the live revision, and every provider failure leaves the record committed and retryable | `AgentExperience.Core`, `AgentExperience.Storage.Postgres.Vectors` |
| Hybrid retrieval: a bounded vector channel merged with the text one under the same eligibility, timeout, and ceiling, with an explicit, flagged text-only fallback whenever the vector channel cannot be trusted | `AgentExperience.Core`, `AgentExperience.Storage.Postgres.Vectors` |
| Historical Reference injection into MAF: a context provider that retrieves, re-checks eligibility immediately before injecting, asks the host's risk policy, and injects one delimited, labeled block within record and byte limits — never throwing into the invocation | `AgentExperience.MicrosoftAgentFramework` |
| Explicit sharing grants: an administrator the host names lets one named record be *read* by a sibling scope until it expires or is revoked; the grant and its audit event commit together, and reads honour it in SQL, never in application code | `AgentExperience.Abstractions`, `AgentExperience.Storage.Postgres` |
| Dependency-injection registration for each package, so a host wires capture, finalization, storage, indexing, and retrieval without knowing concrete types. Injection is the one piece the host constructs itself, because the resolver and risk decision are per-host | `AgentExperience.Core`, `AgentExperience.Storage.Postgres`, `AgentExperience.Storage.Postgres.Vectors` |

## Quick look

```csharp
AIAgent agent = chatClientAgent
    .AsBuilder()
    .UseExperienceCapture(captureService, new ExperienceCaptureOptions
    {
        ResolveRun = context => new ExperienceRunDescriptor(
            TaskId: "triage-ticket",
            Scope: hostScope),   // established by the host, never taken from model output
        OnCaptureFailure = failure => logger.LogWarning("Capture failed at {Stage}", failure.Stage),
    })
    .Build();

await agent.RunAsync("Triage ticket #4812", session);
// The run, its tool calls, and its sanitized outcome are now available from captureService.
```

See the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md) for options, supported agent types, and caveats. See the [PostgreSQL store README](src/AgentExperience.Storage.Postgres/README.md) for the trust boundary, the `ExperienceSchemaMigrator.MigrateAsync` startup call, and data semantics.

## Turning a run into a durable record

A captured run becomes a durable, reusable Experience Record through one Core call.
`ExperienceFinalizationService.FinalizeAsync` runs six stages in order — load the captured snapshot, evaluate it,
check authorization and the host's storage decision, reflect on it, create the record, commit its initial lifecycle
event — and stops at the first stage that ends the call, always returning a structured result rather than throwing.
The two gates precede reflection on purpose: the reflector is the seam a host would plug a model into, so a run that
is about to be refused is never handed to it.

```csharp
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

var result = await finalization.FinalizeAsync(
    new FinalizeExperienceRequest(
        RunId: runId,
        Authorization: authorization,                         // host-established; the run's scope must lie inside it
        ClosedRound: new ClosedVerificationRound(roundId, "rev-7"),
        RequiredChecks: [new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult")],
        Evidence: evidence,                                   // finalization filters and aggregates it itself
        CurrentArtifactRevision: "rev-7",
        StorageDecision: StorageDecision.Permit,              // or StorageDecision.Deny("retention policy")
        FinalizedAt: DateTimeOffset.UtcNow),
    cancellationToken);

if (result.IsDurable)
{
    logger.LogInformation("Experience {Id} is {Status} at revision {Revision}",
        result.ExperienceId, result.Status, result.Revision);
}
else
{
    logger.LogWarning("Finalization ended at {Stage}: {Outcome} — {Reason}",
        result.Stage, result.Outcome, result.Failure?.Reason);
}
```

| Outcome | When | What was written |
| --- | --- | --- |
| `Validated` | Verified, reflection succeeded, storage permitted | The record (reuse confidence 2/3, one supporting validation, no contradictions), created as `Candidate`, plus the initial event that moved it to `Validated` |
| `Quarantined` | Storage permitted, but verification did not pass or the reflector threw | The record, with **no** reflection, created as `Candidate`, plus the initial event that moved it to `Quarantined`. `Failure` names the stage that decided it |
| `AlreadyFinalized` | This run's record already exists *and* is already confirmed | Nothing. The result reports the stored record, status, and revision. (A record left unconfirmed by an earlier call is resumed instead: the retry commits its initial event and returns `Validated`/`Quarantined`.) |
| `StorageDenied` | The host's `StorageDecision` denied | Nothing at all, and no record ID is issued |
| `NotAuthorized` | The run's scope lies outside the authorization | Nothing; denied before any store call |
| `RunNotFound` / `RunNotFinished` | No such captured run, or it has no execution status | Nothing |
| `Failed` | A stage failed (for example the database was unavailable) | Never reported as durable. Any record already created stays a `Candidate`, which is never reusable, and the captured run stays available for a retry |

Three properties make retrying safe. The record is *created* as a `Candidate` and its initial lifecycle event
performs the real transition, so a commit that never lands leaves nothing reusable behind. The record ID, the
reflection ID, and the initial event ID are all derived from the run ID, so a second call cannot create a second
record or a second initial confirmation. And the initial event's fields are a pure function of the stored record, so
a retry re-derives exactly the event the store already deduplicates on.

Finalization never sanitizes — capture already rejected anything unsafe — and never decides storage or risk policy on
the host's behalf: `StorageDecision` travels in the request and Core simply obeys it.

**What "already rejected" means.** Sanitization is the first gate, and it is fail-closed at capture time rather than
at storage time. When content cannot be sanitized, `AppendAttemptAsync` returns
`AppendAttemptOutcome.SanitizationRejected` and the sanitizer's own `Reason`, the attempt is not recorded, the run
stays open, and **nothing is stored anywhere** — there is no database involved, so there is no partial write and no
persisted denial record to reconcile later. The host is told the decision and why, and can correct and resubmit the
same attempt ID; the rejected ID is not tracked, so a corrected resubmission succeeds. Unsafe content therefore never
reaches an Experience Record, and never becomes something a grant could later share.

If an indexing hook is registered, one more thing happens *after* those six stages: the committed record is embedded
and its vector stored. That step is outside the canonical write and can never change the outcome above — see
[Indexing experience for semantic reuse](#indexing-experience-for-semantic-reuse).

## Indexing experience for semantic reuse

A record that is committed is already reusable: it is text-searchable the moment it lands. Indexing gives it a
second way to be found — by meaning — and it is **derived data** throughout. Nothing about the canonical write
depends on an embedding provider being up.

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

**Only the sanitized retrieval summary is embedded** — the task ID, the sanitized task summary, and the reflection's
lesson, the same three fields the text index analyzes. Attempts, tool calls, evidence, provenance, and environment
metadata are never sent to a provider. The summary is read from the database at index time, not from a record the
caller happens to be holding, so what is embedded is what is really stored, at the revision it is really stored at.

The two channels read the same *fields* but not necessarily the same *length*: the embedded summary is capped at
8,192 characters (`ExperienceRetrievalSummary.MaxLength`, so the hashed text and the text sent to a provider are
always identical), while `0003` analyzes the concatenation up to 100,000. A record whose summary and lesson together
run past 8 KB is therefore matched on more of its text by words than by meaning. Both caps are far past any
realistic summary.

**Only records a search could actually return are embedded.** The indexing scan applies the same status filter and
confidence floor the vector search applies, and the post-commit hook checks the record before calling anything, so
a `Quarantined`, `Revoked`, `Superseded`, or `Candidate` record's summary and lesson never leave the database for a
third party — its vector could never be returned anyway.

Each stored vector carries **model ID, dimension, content hash, and source revision**, kept entirely separate from
lifecycle state. None of them ever influences eligibility, status, or reuse confidence; they exist so a write can be
conditional, a re-index can be free, and a query vector is never compared with something it is not comparable with.

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

**Re-indexing is explicit, scoped, and idempotent.** It never runs on its own:

```csharp
var pass = await indexing.ReindexAsync(
    authorization,
    new ReindexExperienceRequest(scope, ExperienceIds: null, Limit: 100),   // bounded; pass again to page
    cancellationToken);

logger.LogInformation("{Examined} examined, {Indexed} re-embedded, {Skipped} unchanged, {Failed} failed",
    pass.Examined, pass.Indexed, pass.Skipped, pass.Failed);
```

A pass is **bounded and resumable**: records are considered in ascending `ExperienceId` order, and `pass.LastExaminedId`
is the cursor to hand to the next pass's `StartAfterId`. Keep going until it comes back `null`, which is how a scope
larger than one page is walked to the end.

The content hash covers the model ID and the normalized summary, so a record whose vector already came from this
model and this text is skipped **before** any provider call — running a pass twice over unchanged records costs one
read and nothing else. Changing the model looks exactly like changing the text, which is the point: two models
produce incomparable vectors, so "same text" alone must never be enough to skip.

**The approximate-nearest-neighbour index is created out of band**, because it needs a dimension no shipped
migration can know:

```csharp
await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(dataSource, dimension: 1536, cancellationToken);
```

It is optional — every search is correct without it, using an exact scan — it makes search *approximate*, and
building it locks the table for the duration, so run it from a maintenance path. See the
[vectors README](src/AgentExperience.Storage.Postgres.Vectors/README.md) for why the `embedding` column is an
unconstrained `vector` and the index is a partial one over `embedding::vector(n)`.

## Retrieving applicable experience

Finding experience that applies to a task is one Core call: `ExperienceRetrievalService.RetrieveAsync`. It asks the
storage adapter for scope-, status- and confidence-filtered text matches — and, when a vector channel is wired in,
for the same thing matched on meaning — decides the remaining eligibility itself, and ranks what survives, always
returning a structured result rather than throwing.

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

**Eligibility is decided before ranking, and nothing is scored before it is known to be reusable.**

| Check | Where it runs | Effect |
| --- | --- | --- |
| Scope | SQL | Only records in the request's *exact* scope; a foreign scope reveals nothing |
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

**Two channels, one answer.** When an embedding index and an embedding generator are both registered, the task text
is also embedded and searched as a vector, concurrently with the text search and inside the same timeout. The two
candidate lists are then deduplicated by `ExperienceId`, and a record found by both keeps the **higher** of its two
normalized relevances. Ranking runs once over the merged list, with the same five weights as before: there is no
sixth axis and no "found by both" bonus. An embedding can only make a record a *candidate* — it never decides
eligibility, status, or confidence.

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

**Ranking is explainable.** Every returned record carries all five normalized components (each in 0–1) and the
effective weight applied to it, so the score is always reproducible from what the result holds.

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

**"Recency" and "expiry" mean last lifecycle activity, not when the lesson was learned.** Both read
`ExperienceRecord.UpdatedAt`, which every lifecycle commit bumps. A years-old lesson reinforced yesterday is one day
old by this measure: it scores as fully recent and never expires. That is deliberate — recent revalidation is
evidence the lesson still holds — but it is not a measure of how old the underlying knowledge is, and a policy that
needs one should not use `MaxAge` for it.

**Bounded, and fail-closed.** The whole call is bounded by `RetrievalPolicy.Timeout` (default 500 ms, maximum one
day), measured with an injected `TimeProvider`.

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

Retrieval returns ranked records and the evidence for their ranking. Turning them into a labeled Historical
Reference and injecting it into an agent is a separate step, described next — and retrieved content never becomes
authority.

## Injecting Historical Reference into MAF

`ExperienceContextProvider` closes the loop. It is a MAF `AIContextProvider` that, before each invocation, retrieves
the applicable experience, re-checks each candidate one last time, asks the host's risk policy, and injects what
survives as **one delimited, labeled Historical Reference message**. The host adds it to the agent itself:

```csharp
using AgentExperience.MicrosoftAgentFramework.Injection;

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions { Tools = tools },
    AIContextProviders =
    [
        new ExperienceContextProvider(retrieval, recordStore, new ExperienceInjectionOptions
        {
            ResolveRequest = context => new RetrieveExperienceRequest(
                Authorization: hostAuthorization,
                Scope: hostScope,
                // Never `Last()`: the list can be empty, and mid-conversation the last message is a
                // tool result, not the task. Retrieval caps task text at
                // `ExperienceCandidateQuery.MaxTaskTextLength` (4096 characters).
                TaskText: context.Messages
                    .LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text
                    ?? taskDescription),
            DecideInjection = d => riskPolicy.Allows(d.Current) ? InjectionDecision.Permit : InjectionDecision.Deny("risk policy"),
            OnContextInjected = result => logger.LogDebug("Injected {Count}, omitted {Omitted}", result.InjectedCount, result.Omitted.Count),
        }),
    ],
});
```

Each record in the block carries its **source** (experience ID, source run ID, task ID), its **confidence**, its
**applicability** (the rank score and every component with the weight applied to it, labeled *as ranked at
retrieval*), **when it was learned and last revalidated**, the **environment** it came from, and an **evidence
summary** — lesson, reuse guidance, preconditions, warnings, verification status, and evidence ID count. Attempts,
tool calls, arguments, results, errors, and evidence detail are never serialized, so a captured payload cannot reach
a model through injection.

**The label is hygiene, not a security control.** The block states that it is untrusted reference material and that
nothing inside it authorizes anything. That wording helps a well-behaved model treat retrieved text as data and
gives a human reading a transcript the provenance — it does not make a model obey, and this project does not claim
it does. What actually stops an unauthorized call is the authorization boundary around tools and policy, which lives
entirely outside the block. An integration test pins that down: a fake model *obeys* an injected instruction to call
a guarded tool, and the approval boundary denies the call anyway.

| Situation | What the agent sees |
| --- | --- |
| Eligible records found | A delimited block, in rank order, within 8 records and 16 KB of UTF-8 (both configurable and validated) |
| Nothing matched, retrieval timed out or failed, or the final check overran its bound | No injected context at all; the agent runs normally, the outcome is reported, and nothing is fabricated |
| The request scope lies outside the host authorization | Nothing, reported as `RetrievalDenied`; no search is issued, and a foreign scope reveals nothing |
| A record revoked, re-scoped, re-scored below the confidence floor, aged past `MaxAge`, environment-mismatched, or unreadable since retrieval | It is absent from the block; the omission is recorded with the rule that dropped it and the stored record is untouched |
| The host's `DecideInjection` denies a record | Absent whatever its stored confidence or status; the denial is recorded and nothing is written |
| More records, or more bytes, than the limits allow | Whole records are dropped — never cut — and each omission is recorded as `OverRecordLimit` or `OverByteBudget` |

The final eligibility check runs immediately before the payload is built and re-applies **every rule retrieval
applies** — status, the reuse-confidence floor, `MaxAge`, and the request's required environment attributes — to the
record as it stands now, so it catches what changed since retrieval. What it cannot do is reach backwards: once a
block has been handed to a model, a later revocation cannot retract it, and the provider says so rather than
implying otherwise.

**Injected blocks accumulate in a reused session.** A block injected on one turn can stay in the `AgentSession`'s
conversation, so a later turn shows the model the fresh block *and* the earlier ones. MAF filters the provider's
input to external messages, so it cannot reliably see or strip its own earlier blocks, and it does not pretend to.
That means `MaxBytes` bounds one injected block rather than a conversation, and revocation only affects injections
that have not happened yet. Use a fresh session per task where either matters.

See the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md#injecting-historical-reference) for
the payload shape, the options, and the failure behaviour.

## Sharing experience across scopes

Scope is otherwise all-or-nothing: a record is readable only from the exact scope that owns it. A **sharing grant**
is the one, audited exception. An administrator names one record, one recipient scope, a reason, and an expiry, and
that recipient can *read* that record until the grant expires or is revoked.

```csharp
using AgentExperience.Storage.Postgres.DependencyInjection;

services.AddAgentExperiencePostgresGrantStore();   // IExperienceGrantStore

// The host decides who may administer sharing. This is a separate, explicit input: it is never
// derived from an AuthorizationContext, from a role string, or from the requesting scope.
var administration = new GrantAdministration(
    AdministratorPrincipalId: currentUser.Id,
    AuthorizedAt: DateTimeOffset.UtcNow);

var result = await grants.CreateAsync(
    hostAuthorization,                                  // the caller's own authority, over the owner scope
    administration,                                     // authority to administer sharing
    new ExperienceGrantRequest(
        GrantId: Guid.NewGuid(),
        ExperienceId: recordId,
        RecordScope: ownerScope,                        // where the record lives: team-a
        RecipientScope: ownerScope with { TeamId = "team-b" },
        Reason: "team-b owns the follow-up work",
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)),
    cancellationToken);
// Created — the grant row and its audit event were written in one transaction.
```

**What a grant permits.** Reading, and only reading: `GetAsync`, the text channel, the vector channel, and
therefore injection, which re-reads through the same call. A granted record comes back exactly as its owner sees
it, still carrying the owner's scope. Creating records, committing lifecycle changes, reading lifecycle history,
listing what a scope holds, and issuing further grants are never inferred from a grant, and still need the caller's
own authority.

**What a grant can never do.**

| Rule | Where it is enforced |
| --- | --- |
| Relaxes only `TeamId`, `AgentId`, `UserId`; tenant, application, and project are always the record's own | Validation with the field path, *and* a `CHECK` constraint, so an unstorable grant is unstorable |
| Confers no write, no lifecycle history, and no enumeration | Every non-read statement keeps the exact-scope predicate |
| Stops permitting reads once `ExpiresAt` passes | The read predicate, against `clock_timestamp()` — the *database's* wall clock, never the caller's, and never the transaction's start time |
| Stops permitting reads the moment it is revoked | The same predicate; revocation appends an event and deletes nothing. At most one grant per (record, recipient scope) may be active at a time, so revoking the grant you know about really is the end of that recipient's access -- a second, overlapping one is refused as `Conflict` rather than stacked |
| Cannot be issued or revoked without administrator authority | `Denied`, before any connection is opened |
| Changes nothing about the record: not its status, confidence, counters, or revision | The grant path never touches `experience_records` |

Grant enforcement lives in SQL, alongside the existing scope predicate, so the database can never return a record
the predicate did not permit and no application code is in a position to widen one. Revoking is an append:

```csharp
await grants.RevokeAsync(
    hostAuthorization,
    administration,
    new ExperienceGrantRevocation(grant.GrantId, ownerScope, "the collaboration ended"),
    cancellationToken);
// Revoked — the next read is denied, and the grant's history keeps both events.

var history = await grants.ListAsync(hostAuthorization, ownerScope, recordId, cancellationToken);
// Every grant over the record, revoked and expired ones included. Owner scope only: a recipient
// cannot enumerate the grants over a record it can read.
```

Nothing about sharing weakens eligibility. A shared record still has to be `Validated` or `Reinforced`, still has
to clear the confidence floor, expiry, and environment checks, and is ranked exactly like an owned one.

**A borrowed lesson is labelled as one.** The adapter is the only layer that knows a record came back through a
grant, so it says so: the flag travels on `ExperienceCandidate.SharedByGrant` and `RankedExperience.SharedByGrant`,
reaches the host's risk policy as `ExperienceInjectionDecisionContext.SharedByGrant`, and the injected Historical
Reference block carries a `Shared:` line (with no scope identifier in it). Everything downstream keeps its strict
"this must be my own record" check for anything that is *not* flagged, so a source that returns a foreign record
without declaring a grant is still dropped.

**What the audit trail is, and is not.** `experience_grant_events` records administration -- who allowed what, under
authority established when, until when, and when they stopped allowing it -- and
`IExperienceGrantStore.GetHistoryAsync` reads one grant's trail. Reads made *through* a grant are not recorded
anywhere: the trail answers "who permitted this?", never "who read it?".

**Two deployment notes.** Reading through a grant needs `SELECT` on `agent_experience.experience_grants`; a role
without it, or a database that has not applied `0005` yet, falls back to the exact-scope predicate -- which narrows
what a read returns rather than failing it -- and reports it once through the reader's optional
`onGrantsUnavailable` callback. And `NotFound` does not mean a `GrantId` is free: the insert reads the record row
first, so a create naming a record that is not in the owner scope selects nothing and reports `NotFound` before the
primary key is ever tested -- even when that `GrantId` is already stored. Only `Created` and `Conflict` say anything
about the ID, so generate a fresh one per attempt rather than inferring availability from `NotFound`.

## Wiring it all together

Each package registers its own services, so a host never names a concrete type:

```csharp
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Storage.Postgres.DependencyInjection;
using AgentExperience.Storage.Postgres.Vectors.DependencyInjection;   // optional: the vector channel

services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddAgentExperiencePostgresStore();                     // IExperienceRecordStore
services.AddAgentExperiencePostgresCandidateSource();           // IExperienceCandidateSource
services.AddAgentExperiencePostgresGrantStore();                // IExperienceGrantStore, optional: only a host
                                                                //    that shares records across scopes needs it
services.AddAgentExperiencePostgresEmbeddingIndex();            // IExperienceEmbeddingIndex
services.AddAgentExperienceEmbeddingGenerator();                // IExperienceEmbeddingGenerator, over a registered
                                                                //    IEmbeddingGenerator<string, Embedding<float>>
services.AddAgentExperienceCore(sanitizationOptions, captureLimits);
// -> ISanitizer, IExperienceCaptureService, IExperienceReflector,
//    ExperienceLifecycleService, ExperienceFinalizationService
services.AddAgentExperienceIndexing();                          // ExperienceIndexingService, and finalization's
                                                                //    post-commit hook, in either registration order
services.AddAgentExperienceRetrieval();                         // ExperienceRetrievalService
// -> defaults to RetrievalPolicy.Default and RankingWeights.Default; pass your own to override
// -> hybrid, because an index *and* a generator are registered; text-only, and flagged, if either is missing

// Injection has no registration of its own: ExperienceContextProvider needs a per-host resolver and
// risk decision, so the host constructs it and adds it to ChatClientAgentOptions.AIContextProviders.
// See "Injecting Historical Reference into MAF" above.
```

Schema comes in two calls, matching that split:

```csharp
await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken);        // 0001-0003 and 0005, always
await ExperienceVectorSchemaMigrator.MigrateAsync(dataSource, cancellationToken);  // 0004, only with the vector channel
```

The vector registrations and the second migration are optional, and genuinely so: leave them out and everything
still works — finalization commits records with no indexing hook, and retrieval answers from text alone with
`TextOnly` set to `NotConfigured`. That is also why the embedding schema is not in the base adapter's script list:
`CREATE EXTENSION vector` needs a superuser, and a text-only deployment must never be made to run it for a feature
it has not enabled.

`AgentExperience.Abstractions` stays BCL-only; only `Core` and the storage adapter take
`Microsoft.Extensions.DependencyInjection.Abstractions`, and every registration uses `TryAdd`, so a host's own
implementation wins.

The MAF adapter can drive finalization for you: set `FinalizationService` and `ResolveFinalization` on
`ExperienceCaptureOptions` and every successfully captured invocation is finalized right after it is completed. See
the [adapter README](src/AgentExperience.MicrosoftAgentFramework/README.md#finalizing-captured-runs).

## Design principles

- **Hexagonal core.** `Abstractions` depends only on the BCL; `Core` adds a redaction primitive and the dependency-injection *abstractions* it needs to register its own services. MAF, databases, models, and telemetry stay in adapters. Dependency-boundary tests enforce this in CI.
- **Failure-preserving capture.** Failed and cancelled runs are recorded through an outer lifecycle path, never only a success callback.
- **Evidence before trust.** Verification is deterministic and bound to a host-closed round and artifact revision. A completion score is never mistaken for reuse confidence.
- **Sanitize before anything is stored.** Unknown payload fields are dropped by default, and secrets are redacted from nested values.
- **Reuse, don't rebuild.** MAF middleware and `Microsoft.Extensions.Compliance.Redaction` are used at the edges, and storage builds on Npgsql and pgvector rather than on a bespoke engine. Each integration was proven with executable compatibility tests before an adapter was built.
- **Derived data never blocks canonical data.** Embeddings are produced after the commit, through a replaceable provider port, and every failure leaves the record committed, text-searchable, and retryable.

## Repository layout

```
src/
  AgentExperience.Abstractions/             domain contracts and ports (BCL only)
  AgentExperience.Core/                     sanitization, capture, verification, reflection, lifecycle transitions, finalization, indexing, retrieval
  AgentExperience.MicrosoftAgentFramework/  MAF adapter: run/tool capture and Historical Reference injection (pinned Microsoft.Agents.AI 1.20.0)
  AgentExperience.Storage.Postgres/         PostgreSQL Experience Record store, text search, sharing grants, and schema migrator (pinned Npgsql 10.0.3, dbup-postgresql 7.0.1, dbup-core 6.1.1)
  AgentExperience.Storage.Postgres.Vectors/ pgvector embedding index, conditional writes, scoped re-index, and vector search (pinned Npgsql 10.0.3, Pgvector 0.3.2, Microsoft.Extensions.AI.Abstractions 10.9.0)
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection, lifecycle, indexing, retrieval tests
  AgentExperience.MicrosoftAgentFramework.Tests/  real ChatClientAgent runs against a scripted fake model
  AgentExperience.Storage.Postgres.Tests/   store tests, mostly against a PostgreSQL container
  AgentExperience.Storage.Postgres.Vectors.Tests/  embedding index and hybrid retrieval, against a pgvector container
  AgentExperience.CompatibilityProof/       executable proofs for MAF hooks, context providers, pgvector, redaction
docs/                                       original production architecture research
_sdlc/                                      product brief, PRD, architecture, epics, and specs
```

## Build and test

Requires the [.NET SDK 10.0.302](https://dotnet.microsoft.com/) or a later feature band (see `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

Unit and MAF adapter tests run in memory, with no network, database, or model credentials. **No test anywhere needs model credentials**: every embedding in the test suite comes from a deterministic in-test generator. `AgentExperience.CompatibilityProof`, the `PostgresExperienceRecordStoreTests`, `PostgresExperienceCandidateSourceTests`, `PostgresLifecycleCommitTests`, `PostgresGrantTests`, `PostgresFinalizationTests`, and `ExperienceSchemaMigratorTests` in `AgentExperience.Storage.Postgres.Tests`, the `PlainPostgresMigrationTests` in the same project (a stock `postgres:16` image, proving the base schema needs nothing pgvector provides), and the `PostgresEmbeddingIndexTests` and `HybridRetrievalIntegrationTests` in `AgentExperience.Storage.Postgres.Vectors.Tests` start a PostgreSQL/pgvector container through Testcontainers, so they need Docker. If Testcontainers' Ryuk container fails to start under your local Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. To skip the container-backed tests:

```bash
dotnet test --filter "FullyQualifiedName!~CompatibilityProof&FullyQualifiedName!~PostgresExperienceRecordStoreTests&FullyQualifiedName!~PostgresExperienceCandidateSourceTests&FullyQualifiedName!~PostgresLifecycleCommitTests&FullyQualifiedName!~PostgresGrantTests&FullyQualifiedName!~PostgresFinalizationTests&FullyQualifiedName!~ExperienceSchemaMigratorTests&FullyQualifiedName!~PlainPostgresMigrationTests&FullyQualifiedName!~PostgresEmbeddingIndexTests&FullyQualifiedName!~HybridRetrievalIntegrationTests"
```

## Roadmap

1. **Capture and explain agent experience** ✅ contracts, sanitization, capture, verification, reflection, MAF adapter
2. **Reuse relevant experience** ✅ PostgreSQL persistence, atomic audited lifecycle commits, one-call finalization of captured runs, bounded text retrieval with explainable ranking, revision-safe embedding ingestion with hybrid retrieval, and historical-reference injection into MAF
3. **Govern experience safely:** explicit sharing grants ✅; the remaining lifecycle transitions and evidence-based confidence updates are next
4. **Operate and measure the learning loop:** OpenTelemetry instrumentation, an end-to-end demo, measured reuse against a baseline, data deletion and expiry

Full requirements and acceptance criteria are in [`_sdlc/planning-artifacts/epics.md`](_sdlc/planning-artifacts/epics.md).

## How this project is built

Development is spec-driven with the [BMAD Method](https://github.com/bmad-code-org/BMAD-METHOD) and AI-assisted implementation. The planning trail is versioned alongside the code:

- **Product brief, PRD, architecture, and epics:** [`_sdlc/planning-artifacts/`](_sdlc/planning-artifacts/)
- **MVP spec and reuse-boundary decisions:** [`_sdlc/specs/`](_sdlc/specs/)

Each story is planned against the architecture, implemented against explicit acceptance criteria, and then reviewed by independent adversarial, edge-case, and verification-gap passes before it is committed.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). To report a vulnerability, follow [SECURITY.md](SECURITY.md).

## License

[Apache-2.0](./LICENSE)
