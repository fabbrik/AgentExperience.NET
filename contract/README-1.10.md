# Contract 1.10: additions

This page lists what contract 1.10 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md),
[contract 1.2](README-1.2.md), [contract 1.3](README-1.3.md), [contract 1.4](README-1.4.md),
[contract 1.5](README-1.5.md), [contract 1.6](README-1.6.md), [contract 1.7](README-1.7.md),
[contract 1.8](README-1.8.md) and [contract 1.9](README-1.9.md). Those pages are still normative and unchanged: a minor
version only adds files, this page among them. The contract's version is the one `lock.json` records
(`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.10 passes every 1.0 to 1.9 fixture and the fixtures below.

## Acting on the estimate

From 1.9 a port reads the benefit estimate; from 1.10 it acts on it, as .NET's benefit policy
(`ExperienceBenefitPolicy`, [Injection: acting on the estimate](../docs/guide/injection.md#acting-on-the-estimate))
does: a lesson measured `Hurts` is not injected, a scope whose lessons together measure `Hurts` gets none, and with
`PreferMeasuredHelpful` the lessons measured `Helps` go first. The lessons view shows what was measured for each lesson.
Wrapping another memory provider stays .NET-only.

A port that does not implement 1.10 keeps passing 1.9.

## What 1.10 adds

- **Fixtures:** `benefit-select` (no database), and four `lessons-view` cases (PostgreSQL) whose names start with
  `measured-benefit`.

Nothing else changes: no SQL file, no schema object, and every existing file is byte-identical. The estimate is read
through 1.9's `sql/query-holdout-outcomes.sql`.

## The policy

| Option (.NET `ExperienceBenefitPolicy`) | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Off: nothing is read, cached, reordered or omitted, and injection is exactly what it is without it. |
| `PreferMeasuredHelpful` | `false` | Lessons measured `Helps` move ahead of the others. |
| `PerModel` | `false` | Act on the invoking agent's model's verdicts instead of the pooled ones. |
| `Window` | 30 days | Only assignments made within it of a refresh count. |
| `MinimumRunsPerArm` | 20 | The estimator's minimum (1.9). |
| `RefreshInterval` | 10 minutes | How old a cached estimate may grow before an invocation starts a refresh. |
| `RefreshTimeout` | 30 seconds | How long one refresh may take before it is abandoned. |

An interval, timeout or window that is not strictly positive, a timeout over one day, and a minimum below 1 are refused
when the agent is built, even with the policy off. Where a language cannot tell an unset value from an explicit zero, a
zero means the default: Go treats a zero `Window`, `RefreshInterval`, `RefreshTimeout` or `MinimumRunsPerArm` as the
default and refuses only negative values (and a timeout over one day); its README says so.

### The estimate the policy acts on

One estimate per key: the request scope, and the authorization's tenant and principal (two principals never share one).
A port may key more strictly, never more loosely: the Python and Go ports key on the scope, the principal and the full
authorization bounds, so two callers with different bounds never share an estimate either.
It is 1.9's estimate over the rows of that scope read under that authorization, with two differences: only rows whose
`assignedAt` is at or after the refresh's instant minus `Window` are folded (every page is still read; the filter is
applied to each page's rows before they are added), and the estimator's options are `MinimumRunsPerArm` and, with
`PerModel`, `splitByModel`. An estimate that is not `tracked`, or whose outcome is not `Found`, is never acted on.

### The view

What an estimate means for one invocation, from its `overall`, `lessons`, `byConsumingModel` and `minimumRunsPerArm`
(.NET `BenefitView` and `BenefitViews`):

- **Pooled** (`PerModel` off, or the invoking agent has no recorded model): the scope hurts when `overall.verdict` is `Hurts`; a
  lesson hurts or helps when its `comparison.verdict` is `Hurts` or `Helps`.
- **Per model** (`PerModel` on and the invoking agent's model is `m`): only when `byConsumingModel` names `m` and `m` is
  not `unknown`; otherwise the pooled view. The `unknown` consuming group (runs that recorded no model) is never an
  agent's own: an agent with no recorded model, or whose model is literally `unknown`, reads the pooled view, as in
  .NET. Then:
  - the scope's verdict is `m`'s group's verdict, or the pooled one when that is `NoEvidence`;
  - a lesson's verdict is decided over its `byModelPair` entries whose `runModel` is `m`: their treatment runs and
    successes and holdout runs and successes summed, compared as 1.9 compares two arms with the estimate's
    `minimumRunsPerArm` (at least 1). With no such entry, or a `NoEvidence` result, the lesson's pooled verdict stands.
- A view in which nothing hurts or helps changes nothing, exactly as no view.

The invoking agent's model is the one capture records for the run (1.9: `agentexperience.model`, the host's value when
its environment sets one) when the invocation is captured, and otherwise the model the agent's chat client reports,
normalized as 1.9 normalizes it.

### Selection

With a view, before anything else, in the order .NET's `ExperienceContextProvider` applies it:

1. **Prefer helpful** (`PreferMeasuredHelpful` on, and the view has a lesson that helps). The ranked records are split
   into the request's own window (the first `requestLimit` records, all of them with no request limit) and the
   backfill past it; within each, the lessons that help come first and every group keeps its rank order. A backfill
   record never moves into the window.
2. **Select**, walking the reordered list with `index` from 0, where `limit` is the smaller of `maxRecords` and the
   request limit (when there is one) and only a record whose `index` is below the request limit is *reported* (a record
   past it was fetched only as backfill, and an omission of it is dropped):
   1. a record the view marks harmful (the scope hurts, or the lesson hurts) is omitted as `MeasuredHarmful`, detail
      `The injection holdout measured this scope's lessons together as hurting, so none is injected.` when the scope
      hurts and `The injection holdout measured this lesson as hurting: runs given it verified less often than runs it
      was withheld from.` otherwise;
   2. with model-authored lessons excluded, a model-authored record is omitted as `ModelAuthored`, with no detail;
   3. while fewer than `limit` are selected, the record is selected;
   4. otherwise it is omitted as `OverRecordLimit`, detail `Ranked {index + 1} of {count}, beyond the limit of {limit}
      records.`, `count` being the number of ranked records.
3. **The byte budget**, as the block is rendered (1.0): the first selected record that does not fit and every one after
   it are omitted as `OverByteBudget`, detail `The record did not fit in the remaining part of the {maxBytes}-byte
   budget, and a record is never cut to fit.`

The omissions are reported in that order: the selection's in walking order, then the budget's. When everything is
omitted nothing is injected; the holdout assigns nothing for such an invocation, as for any invocation with nothing to
inject.

**The holdout is unchanged.** Assignment and withholding work on the block the selection produced, exactly as in 1.8:
a lesson omitted as harmful is not in the block, so it is neither delivered nor withheld, and no row names it.

### The cache

The estimate is never read on the request path:

- An invocation reads what is cached for its key, as it is: nothing yet (the first invocation) means no view, and the
  invocation is injected as with the policy off.
- When the key's entry is missing, or was last refreshed `RefreshInterval` or more ago, and no refresh is running for
  it, the invocation starts one in the background and does not wait for it. At most one refresh per key runs at a time.
- A refresh reads the estimate within `RefreshTimeout`. When the store answered, the entry's view is replaced (an
  estimate not tracked or not `Found` replaces it with none). When it threw or ran out of time, the previous view is
  kept. Either way the entry's refresh instant becomes the end of the refresh, so a failing store is asked again only
  after another `RefreshInterval`.
- Each refresh is reported as the `benefit_refresh` operation (no span, never nested) with the outcome `Refreshed`,
  `NotRefreshed` (it threw: `error.class` `Infrastructure` for a store failure, `Unexpected` otherwise) or
  `OutcomeUnknown` (it ran out of time: `Timeout`; it was cancelled: `Cancelled`), as
  [`docs/telemetry-names.json`](../docs/telemetry-names.json) names them.
- .NET keeps at most 10,000 keys; past that it drops the entry refreshed longest ago that is not refreshing.

Nothing about the cache is fixtured: each port tests it with an injectable clock and a way to wait for the refreshes
it started (.NET `BenefitRefreshesSettledAsync`).

## The lessons view's measured benefit

With `IncludeMeasuredBenefit` (default off: then nothing more is read and the page is exactly 1.7's), the view reads
one estimate for the page's scope under the caller's authorization, after the page, over the policy's `Window` before
the page's `generatedAt` and with the policy's `MinimumRunsPerArm` and, with `PerModel`, split by model (whether or not
the policy is on). Each listed lesson's `measuredBenefit` is that estimate's entry for its ID, or `null` when the
estimate has none (no assigned run in the window named it), and every lesson's is `null` when the estimate is not
tracked or not `Found`. The read is not made when the page lists no lesson. A store failure on the read fails the list:
.NET throws `ExperienceStoreException`, whose telemetry error class is `Infrastructure`; a port raises its own store
failure, which its telemetry must classify as `Infrastructure` too.

## The fixtures

```
fixtures/benefit-select/<case>/   records.json + estimate.json + options.json -> expected.json
fixtures/lessons-view/measured-benefit*/   seed.json + query.json -> expected.json (needs PostgreSQL)
```

**benefit-select.** `records.json` is the ranked records, best first, in an inject fixture's `records.json` form (each
stored `record` with its `ranking`). `estimate.json` is the cached estimate, in a 1.9 benefit-estimate `expected.json`'s
form (it was tracked and `Found`). `options.json` holds `enabled`, `preferMeasuredHelpful`, `perModel`, `model` (the
invoking agent's, `null` for none), `requestLimit` (`null` for none), `maxRecords`, `maxBytes` and
`excludeModelAuthored`; every other injection option is the default (the compact rendering, failure detail
`ErrorClass`, no argument allowlist). Build the view as the cache would hand it to an invocation of that model (none
with the policy off), select, and render; compare with `expected.json`: `selected` (the records selected, in order,
before the byte budget), `injected` (the records the block holds, in order) and `omitted` (`experienceId`, `reason` and
`detail`, `null` when none, in order). Everything compares exactly; the detail strings byte for byte.

**lessons-view.** The 1.7 runner, with three additions. `seed.json` may hold `assignments`, holdout rows in 1.9's
holdout-outcomes seed form, inserted as the schema's owner after the injections. `query.json` may hold
`includeMeasuredBenefit`, `minimumRunsPerArm` (20 when absent) and `perModel` (`false` when absent), the policy the
view reads with, and `holdoutReadRevoked`: when `true`, before listing, the schema's owner runs `REVOKE SELECT ON
agent_experience.experience_holdout_assignments FROM` the application role, and the list must fail; `expected.json` is
then `{"errorClass": "Infrastructure"}` and no page. Otherwise `expected.json` is the page, each lesson with
`measuredBenefit` added when the query includes it: `null`, or one lesson of a 1.9 benefit-estimate `expected.json`
(`experienceId`, `comparison`, `byModelPair`), whose rates, differences and bounds compare within 1e-12 and everything
else exactly. A store that is not PostgreSQL runs the same cases from the same seed, and a failing holdout read is its
own test.

**Not fixtured.** The cache (above), the options' validation, and which model an invocation acts for are each port's
own tests.
