# Contract 1.12: additions

This page lists what contract 1.12 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.11
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md) and
[1.11](README-1.11.md)). Those pages are still normative and unchanged: a minor version only adds files, this page among
them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`.

A port that passes contract 1.12 passes every 1.0 to 1.11 fixture and the fixtures below.

## The retrieval policy

Up to 1.11 a port ranked with a fixed policy: contract 1.0's confidence floor (0.5), candidate limit (50) and 30-day
recency half-life, 1.6's optional vector relevance floor and 1.0's minimum of matched terms (3). .NET's
[`RetrievalPolicy`](../src/AgentExperience.Core/Retrieval/RetrievalPolicy.cs) and
[`ConfidenceDecayPolicy`](../src/AgentExperience.Core/Retrieval/ConfidenceDecayPolicy.cs) offer more: an expiry, decay of
confidence by the lesson's domain, a configurable recency half-life, candidate limit and timeout, and outcomes and
exclusion reasons that say why a result is what it is. From 1.12 a port takes the same options, with .NET's defaults and
validation, and a Python or Go agent configured like a .NET one ranks exactly as it does.

**With the default policy and no decay nothing changes:** every 1.0 to 1.11 fixture is byte-identical, and contract 1.0's
ranking is this page's ranking with the defaults. A port that does not implement 1.12 keeps passing 1.11.

## What 1.12 adds

- **Fixtures:** `retrieval-policy` (no database): twelve cases and `policy-validation.json`.
- **Rules:** the options below, the order retrieval applies them in, the exclusions it reports and its outcomes.

Nothing else changes: no SQL, no schema object, no privilege. The confidence floor, the minimum of matched terms and the
statement's `limit` were always parameters of 1.0's `search-experience-text.sql`; 1.12 says where they come from.

## The options

Each port names them in its own idiom and maps them one to one in its README. A duration is a span of time; the
fixtures write one as a whole number of .NET ticks (100 ns), every one of them a whole number of microseconds.

**`RetrievalPolicy`**

| Option | Default | Valid | Used for |
| --- | --- | --- | --- |
| `Timeout` | 500 ms | more than zero, at most one day | bounds the whole retrieval (below) |
| `MinimumConfidence` | 0.5 | 0 to 1 (not NaN) | the search's `min_confidence` |
| `MaxAge` | none | more than zero when set | the expiry (below) |
| `RecencyHalfLife` | 30 days | more than zero | the `Recency` component |
| `CandidateLimit` | 50 | 1 to 199 | the search's `limit` is this plus one; ranking considers this many |
| `MaxAbandonedSearches` | 16 | more than zero | the abandoned-search cap (below) |
| `MinimumMatchedTerms` | 3 | at least 1 | the search's `minimum_matched_terms` |
| `MinimumVectorRelevance` | none | 0 to 1 when set | 1.6's vector floor |

**`ConfidenceDecayPolicy`** (optional; none is the default and decays nothing)

| Option | Default | Valid |
| --- | --- | --- |
| `DomainKey` | `domain` | not empty and not only white space |
| `HalfLives` | empty | each domain not empty and not only white space; each half-life more than zero, or none (the domain never decays) |
| `DefaultHalfLife` | none | more than zero when set |

**An invalid value is refused** when the policy is built, before anything is retrieved: .NET throws
`ArgumentOutOfRangeException` naming the option, or `ArgumentException` for a blank domain key or a blank domain in
`HalfLives`. A port raises or returns its own error naming the same option. Domains are matched ordinally, whatever map
the host passed; a port copies the map, so a later change to the host's map has no effect.

**A language whose zero value cannot mean "unset"** (Go) may read a zero `Timeout`, `RecencyHalfLife`,
`CandidateLimit`, `MaxAbandonedSearches` or `MinimumMatchedTerms` as the default, a zero `MaxAge` as no expiry and an
empty `DomainKey` as `domain`, and must say so in its README; a negative value, and every other invalid value, it
refuses as .NET does. The `policy-validation.json` runner then expects those zero cases to be accepted as the default.

## Retrieval, in order

As [`ExperienceRetrievalService`](../src/AgentExperience.Core/Retrieval/ExperienceRetrievalService.cs) runs it:

1. **Authorization.** A request scope the authorization does not permit is `Denied`: nothing is searched or embedded,
   and the result is empty.
2. **The abandoned-search cap.** While the retriever has `MaxAbandonedSearches` abandoned searches still running, the
   call is `TimedOut` at once, without searching, its failure reason naming the cap (below).
3. **The search**, with `min_confidence` = `MinimumConfidence`, `limit` = `CandidateLimit + 1`,
   `minimum_matched_terms` = `MinimumMatchedTerms` and the model-authored exclusion the request asked for; with 1.6's
   vector channel alongside it, floored at `MinimumVectorRelevance`. The whole of it is bounded by `Timeout` (below).
4. **Each channel's checks and truncation**, as 1.0 and 1.6 state them, with `CandidateLimit` in place of 50: over each
   channel's first `CandidateLimit` candidates, one outside the scope or a repeated ID fails the result; keep the first
   `CandidateLimit` of each; the result is truncated when either channel returned more. Then 1.6's merge.
5. **Eligibility**, for each merged candidate in order, the first rule that applies excluding it, with the reason .NET
   names:

   | Reason | When |
   | --- | --- |
   | `IneligibleStatus` | its status is not `Validated` or `Reinforced` |
   | `ModelAuthored` | the request excludes model-authored lessons and the record's reflection is one |
   | `Expired` | `MaxAge` is set and now − `updated_at` is **more than** `MaxAge` (exactly `MaxAge` old is kept) |

   (.NET also has `EnvironmentMismatch`, for a required environment attribute, and `UnconfirmedContent`, under
   provenance signing; a port that has neither feature never reports them.) The exclusions are listed in that order,
   the merged order. They are not a complete account: what the search's own predicates filter never reaches ranking and
   is never listed. **Decay never excludes**: the confidence floor is the search's, on stored confidence.
6. **Ranking**, as 1.0 states it, with two components changed:

   | Component | Value |
   | --- | --- |
   | `Confidence` | `reuse_confidence` clamped to [0, 1] (NaN as 0); with a decay policy that gives the record a half-life, that value × `2^(-age / halfLife)`, clamped, where age = now − `created_at`, and the factor is 1 when age is not positive |
   | `Recency` | `2^(-age / RecencyHalfLife)`, age = now − `updated_at`; 1 when not positive |

   **The half-life of a record** is that of its domain, the value of its environment metadata entry named `DomainKey`:
   the domain's entry in `HalfLives` when it is listed (none: it never decays, whatever `DefaultHalfLife` says), and
   `DefaultHalfLife` when it is not listed or the record has no domain. When a half-life applied, the `Confidence`
   component also carries **`undecayedValue`**, the clamped stored confidence; otherwise it has none. Every age and
   half-life is in seconds as a double: the span's ticks divided by 10,000,000. The score, the order and the tie-break
   are 1.0's.
7. **The outcome** is `Completed`.

## Timing out

The whole retrieval (the authorization check aside) is bounded by `Timeout`. A search still running when it expires is
**abandoned**: the call returns `TimedOut` at once, empty and not truncated, never an error and never a partial result;
the search is asked to stop (its context or task is cancelled) but may keep running against the store in the
background. A caller's own cancellation is not a timeout: it propagates as the language's cancellation.

**The abandoned-search cap.** One retriever counts the searches it abandoned that are still running; each is counted
until it actually ends. While the count is at `MaxAbandonedSearches`, a call returns `TimedOut` at once without
searching, with a failure reason that names the cap, so a store that hangs on every call cannot pile up searches until
the connection pool runs out. As the abandoned searches end the count drops and calls reach the store again. The count
is per retriever (per configured agent or provider in the ports), so it only engages for a long-lived one.

## Outcomes and telemetry

The outcomes are .NET's `RetrievalOutcome` member names, which are the `retrieve` operation's outcomes in
[`docs/telemetry-names.json`](../docs/telemetry-names.json): `Completed`, `TimedOut`, `Denied` and `Failed`. Injection
reports them as `RetrievalTimedOut`, `RetrievalDenied` and `RetrievalFailed`, the `inject` operation's outcomes; a
`TimedOut` refused at the cap carries the failure reason, a real timeout none.

## The fixtures

```
fixtures/retrieval-policy/<case>/            candidates.json + policy.json -> expected.json
fixtures/retrieval-policy/policy-validation.json   policy + confidenceDecay -> refusal
```

**`<case>`.** `candidates.json` is what a store returned, in its order: each entry a stored `record` (as 1.0's seeds
hold one) and its `relevance`, already widened. `policy.json` holds the `scope` and `taskText` of the request, `now`,
`preferredEnvironment`, `excludeModelAuthored`, the `policy` (each option above, camelCase; durations as `…Ticks`, none
as `null`) and `confidenceDecay` (`domainKey`, `halfLivesTicks`, `defaultHalfLifeTicks`; `null` for none). Build the
policy and decay, retrieve at `now` against a store that answers with the candidates (at most the `limit` it is asked
for), and compare with `expected.json`:

- `outcome`, exactly;
- `query`, what the store was asked for: `minConfidence`, `limit`, `minimumMatchedTerms`, `excludeModelAuthored`;
- `truncated`;
- `ranked`, in order: each `experienceId`, its `score` and its five `components` (`kind`, `value`, `weight`,
  `undecayedValue`, `null` for none), every number within an absolute tolerance of 1e-12;
- `excluded`, in order: each `experienceId` and `reason`.

`default-policy` is the default policy with no decay: it must rank exactly as 1.0's ranking does.

**`policy-validation.json`.** For each entry, build `policy` and `confidenceDecay` (`null`: none). A `refusal` of `null`
means the port accepts it; otherwise the port refuses it, naming the option .NET names in `parameter`
(`exception` is .NET's exception type, for the record). A number that is not one is written as the string `"NaN"`.

**Not fixtured.** Timing: the timeout, abandoning a search and the cap are each port's own tests, against a store that
answers slowly or never, as is a `Denied` request.
