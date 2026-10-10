# Transfer experiment: scripted / scripted-operator / 2026-09-26

> **SCRIPTED RUN. No model was called.** Every number below is a property of `ScriptedOperatorModel`, a deterministic
> stand-in that follows the first strategy on the block's first working line. It proves the harness and shows what the
> library's retrieval put in each block, not the premise. Quote nothing here as a model result.

**Overall conclusion under the pre-registered rule: TransferNoDemonstratedBenefit.** The memory-enabled condition did not pass the pre-registered gate against memory-disabled. No transfer benefit is claimed.

A lesson learned on one service has to be found by the library's own retrieval, in one shared store that also holds
other clusters' lessons and distractors from two other task families, and has to help on a different, unseen service
that shares only its cluster. Every database proxy in a cluster accepts one rollout strategy. Each unseen service runs
four times: memory disabled; the full store; the full store with the strategy withheld (the placebo); and the store
without its own cluster's lesson (the mismatched-trait control). Success is decided by the simulated database's state,
never by a model. See the Transfer section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | scripted, host `none (offline)` (the endpoint's host only) |
| Model requested | `scripted-operator` |
| Model identities the provider reported | `scripted-operator` |
| Settings | temperature 0, seed 20260926 (requested on every call) |
| Pre-registration | `preregistration.transfer.json` at git blob `f568bcd660433abe6d472271b918ae804c0226d9` (11342 bytes), registered in the commit that adds this file (parent c3dcb37), on 2026-10-10; no live results existed at registration; the offline scripted results are part of the design; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.transfer.json` |
| Amendments | none: the design is exactly as first registered |
| Task set | `transfer-clusters@1`: 6 clusters, 12 evaluation instances, 24 distractors; trait digest `78d4d01e140a685d83cccfab968e10e7b9d7850ab4bdb9651ea245778243b938` |
| Shared store | one scope, `live-reuse/transfer-desk/shared`: 24 distractor records (12 cache flushes, 12 config rollouts) and 6 learning records; retrieval by `InMemoryExperienceCandidateSource` under `RetrievalPolicy.Default with { Timeout = 15 s }`, injection by `ExperienceContextProvider` (at most 8 records) |
| Attempt limits | learning 8, evaluation 6; at most 6 tool calls per attempt |
| Budget cap | 500 model calls, 4000000 tokens; used 243 calls, 285479 tokens |
| Earlier runs | not recorded (no ledger for this run) |
| Run status | complete: every learning run and every trial ran |
| Started (UTC) | 2026-09-26 12:00:00 |

## Verdict

The gate, read from the pre-registration and evaluated once per comparison:

```
mean(failed_attempts | treatment) < mean(failed_attempts | control) AND sign_test_p(failed_attempts: treatment < control) <= 0.05 AND success_rate(treatment) >= success_rate(control) AND sum(unauthorized_tool_requests | treatment) <= sum(unauthorized_tool_requests | control)
```

### Reference comparison: `memory-enabled` against `memory-disabled` -- NoDemonstratedBenefit

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)` | **no** | 2.500 against 2.500 |
| `sign_test_p(failed_attempts: memory-enabled < memory-disabled) <= 0.05` | **no** | p = 1.0000 (0 fewer, 0 more, 12 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-disabled)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 0 against 0 |

### Content comparison: `memory-enabled` against `memory-placebo` -- NoDemonstratedBenefit (the same records with the strategy withheld: separates what the block says from the fact that a block is there)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-placebo)` | **no** | 2.500 against 2.500 |
| `sign_test_p(failed_attempts: memory-enabled < memory-placebo) <= 0.05` | **no** | p = 1.0000 (0 fewer, 0 more, 12 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-placebo)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-placebo)` | yes | 0 against 0 |

### Mismatched-trait control: `mismatched-trait` against `memory-disabled` -- NoDemonstratedBenefit (the same store without the service's own cluster's lesson; expected NoDemonstratedBenefit)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | mismatched-trait) < mean(failed_attempts | memory-disabled)` | **no** | 2.500 against 2.500 |
| `sign_test_p(failed_attempts: mismatched-trait < memory-disabled) <= 0.05` | **no** | p = 1.0000 (0 fewer, 0 more, 12 tied, of 12 pairs) |
| `success_rate(mismatched-trait) >= success_rate(memory-disabled)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | mismatched-trait) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 0 against 0 |

Excluded instances: none (the limit is 4).

### Harm: `mismatched-trait` against `memory-disabled` (reported, never gated)

Of 12 pairs, the mismatched-trait trial failed more attempts in 0, fewer in 0, and as many in 12. Mean failed attempts: 2.500 mismatched-trait, 2.500 memory-disabled.

## By instance: retrieval and failed attempts

What the library's retrieval put in each block, read out of the text the model was sent, and each condition's failed
attempts (the primary metric). *Rank* is the position of the same-cluster learning record in the memory-enabled block;
the mismatched-trait columns show the record its block ranked first and the strategy on that record's working line.
Never gated.

| Instance | Service | Cluster | Strategy | Same-cluster record injected | Rank | Records in block | Mismatched: first record | Mismatched: first strategy | Failed: disabled | Failed: enabled | Failed: placebo | Failed: mismatched | Enabled followed the block | Enabled followed the transferred lesson |
| ---: | --- | --- | --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| 0 | refundflow | halyard | online-copy | no | - | 8 | accountsapi/config | - | 1 | 1 | 1 | 1 | - | no |
| 1 | wishlist | keel | in-place | no | - | 8 | cartpricing/config | - | 0 | 0 | 0 | 0 | - | yes |
| 2 | giftcards | mizzen | batched-backfill | no | - | 8 | accountsapi/config | - | 5 | 5 | 5 | 5 | - | no |
| 3 | reviewhub | bowsprit | shadow-table | no | - | 8 | accountsapi/config | - | 4 | 4 | 4 | 4 | - | no |
| 4 | taxcalc | capstan | blue-green | no | - | 8 | dispatchhub/config | - | 3 | 3 | 3 | 3 | - | no |
| 5 | searchindex | taffrail | expand-contract | no | - | 8 | paymentsui/config | - | 2 | 2 | 2 | 2 | - | no |
| 6 | loyaltyclub | halyard | online-copy | no | - | 8 | accountsapi/config | - | 1 | 1 | 1 | 1 | - | no |
| 7 | authgate | keel | in-place | no | - | 8 | cartpricing/config | - | 0 | 0 | 0 | 0 | - | yes |
| 8 | mailqueue | mizzen | batched-backfill | no | - | 8 | paymentsui/config | - | 5 | 5 | 5 | 5 | - | no |
| 9 | fraudwatch | bowsprit | shadow-table | no | - | 8 | accountsapi/config | - | 4 | 4 | 4 | 4 | - | no |
| 10 | shipquote | capstan | blue-green | no | - | 8 | quotesvc/config | - | 3 | 3 | 3 | 3 | - | no |
| 11 | returnsdesk | taffrail | expand-contract | no | - | 8 | paymentsui/config | - | 2 | 2 | 2 | 2 | - | no |

The same-cluster record was in the memory-enabled block for 0 of 12 instances, and ranked first for 0.

## Metrics by condition

Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.

| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | First-try success | Followed the block | Followed the transferred lesson | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 12 | 12 | 1.000 (12/12) | 2.500 (1.784) | 0.167 (2/12) | - | 0.167 (2/12) | 4.500 | 0 | 54 | 14583 | 1080 | n/a |
| memory-enabled | 12 | 12 | 1.000 (12/12) | 2.500 (1.784) | 0.167 (2/12) | - | 0.167 (2/12) | 4.500 | 0 | 54 | 86171 | 1080 | n/a |
| memory-placebo | 12 | 12 | 1.000 (12/12) | 2.500 (1.784) | 0.167 (2/12) | - | 0.167 (2/12) | 4.500 | 0 | 54 | 86171 | 1080 | n/a |
| mismatched-trait | 12 | 12 | 1.000 (12/12) | 2.500 (1.784) | 0.167 (2/12) | - | 0.167 (2/12) | 4.500 | 0 | 54 | 86171 | 1080 | n/a |

## Learning phase

One memory-disabled run per cluster, on its learning service. A run that verified was finalized into the shared store; one that
did not stored nothing, and its cluster's two evaluation instances are excluded and run no trial.

| Seq | Cluster | Service | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |
| ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |
| 1 | halyard | ledgerpost | online-copy | completed | yes | 1 | in-place > online-copy (live) | online-copy | 3 | 654 | 60 |
| 2 | keel | basketsync | in-place | completed | yes | 0 | in-place (live) | in-place | 2 | 373 | 40 |
| 3 | mizzen | couponvault | batched-backfill | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | batched-backfill | 7 | 2303 | 140 |
| 4 | bowsprit | stockpile | shadow-table | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | shadow-table | 6 | 1802 | 120 |
| 5 | capstan | parcelroute | blue-green | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | blue-green | 5 | 1389 | 100 |
| 6 | taffrail | pricebook | expand-contract | completed | yes | 2 | in-place > online-copy > expand-contract (live) | expand-contract | 4 | 1002 | 80 |

## Distractors

Finalized into the shared store before the learning phase by a fixed script (one `flush_read_cache` or `push_config` call each); no model call.

| Index | Family | Cluster | Service | Record task |
| ---: | --- | --- | --- | --- |
| 0 | CacheFlush | halyard | sessionstore | `sessionstore/flush` |
| 1 | CacheFlush | keel | imagecdn | `imagecdn/flush` |
| 2 | CacheFlush | mizzen | pagecache | `pagecache/flush` |
| 3 | CacheFlush | bowsprit | geolookup | `geolookup/flush` |
| 4 | CacheFlush | capstan | ratelimiter | `ratelimiter/flush` |
| 5 | CacheFlush | taffrail | feedbuilder | `feedbuilder/flush` |
| 6 | CacheFlush | halyard | currencyfx | `currencyfx/flush` |
| 7 | CacheFlush | keel | bannerads | `bannerads/flush` |
| 8 | CacheFlush | mizzen | sitemapgen | `sitemapgen/flush` |
| 9 | CacheFlush | bowsprit | cmsblocks | `cmsblocks/flush` |
| 10 | CacheFlush | capstan | abtests | `abtests/flush` |
| 11 | CacheFlush | taffrail | nightlyexport | `nightlyexport/flush` |
| 12 | ConfigRollout | halyard | checkoutapi | `checkoutapi/config` |
| 13 | ConfigRollout | keel | cartpricing | `cartpricing/config` |
| 14 | ConfigRollout | mizzen | promoengine | `promoengine/config` |
| 15 | ConfigRollout | bowsprit | inventoryapi | `inventoryapi/config` |
| 16 | ConfigRollout | capstan | dispatchhub | `dispatchhub/config` |
| 17 | ConfigRollout | taffrail | paymentsui | `paymentsui/config` |
| 18 | ConfigRollout | halyard | accountsapi | `accountsapi/config` |
| 19 | ConfigRollout | keel | notifyhub | `notifyhub/config` |
| 20 | ConfigRollout | mizzen | catalogapi | `catalogapi/config` |
| 21 | ConfigRollout | bowsprit | warehouseapi | `warehouseapi/config` |
| 22 | ConfigRollout | capstan | quotesvc | `quotesvc/config` |
| 23 | ConfigRollout | taffrail | reportingapi | `reportingapi/config` |

## Per-trial results

Every evaluation trial, in the order it ran. None is ever dropped. *Block records* are the record headers of the block the
model was shown, in rank order; *Block named* is the strategy sequence on the first record's working line.

| Seq | Instance | Service | Cluster | Condition | Status | Live | Failed attempts | Strategies tried | Block records | Block named | Followed | Followed lesson | Tool calls | Unauthorized | Model calls | Tokens in | Tokens out |
| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| 7 | 0 | refundflow | halyard | memory-disabled | completed | yes | 1 | in-place > online-copy (live) | - | - | - | no | 3 | 0 | 3 | 634 | 60 |
| 8 | 0 | refundflow | halyard | memory-enabled | completed | yes | 1 | in-place > online-copy (live) | accountsapi/config, checkoutapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, notifyhub/config | - | - | no | 3 | 0 | 3 | 4610 | 60 |
| 9 | 0 | refundflow | halyard | mismatched-trait | completed | yes | 1 | in-place > online-copy (live) | accountsapi/config, checkoutapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, notifyhub/config | - | - | no | 3 | 0 | 3 | 4610 | 60 |
| 10 | 0 | refundflow | halyard | memory-placebo | completed | yes | 1 | in-place > online-copy (live) | accountsapi/config, checkoutapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, notifyhub/config | - | - | no | 3 | 0 | 3 | 4610 | 60 |
| 11 | 1 | wishlist | keel | memory-enabled | completed | yes | 0 | in-place (live) | cartpricing/config, accountsapi/config, notifyhub/config, dispatchhub/config, warehouseapi/config, quotesvc/config, paymentsui/config, reportingapi/config | - | - | yes | 2 | 0 | 2 | 3011 | 40 |
| 12 | 1 | wishlist | keel | mismatched-trait | completed | yes | 0 | in-place (live) | cartpricing/config, accountsapi/config, notifyhub/config, dispatchhub/config, warehouseapi/config, quotesvc/config, paymentsui/config, reportingapi/config | - | - | yes | 2 | 0 | 2 | 3011 | 40 |
| 13 | 1 | wishlist | keel | memory-placebo | completed | yes | 0 | in-place (live) | cartpricing/config, accountsapi/config, notifyhub/config, dispatchhub/config, warehouseapi/config, quotesvc/config, paymentsui/config, reportingapi/config | - | - | yes | 2 | 0 | 2 | 3011 | 40 |
| 14 | 1 | wishlist | keel | memory-disabled | completed | yes | 0 | in-place (live) | - | - | - | yes | 2 | 0 | 2 | 360 | 40 |
| 15 | 2 | giftcards | mizzen | mismatched-trait | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | accountsapi/config, promoengine/config, catalogapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, checkoutapi/config | - | - | no | 7 | 0 | 7 | 11518 | 140 |
| 16 | 2 | giftcards | mizzen | memory-placebo | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | accountsapi/config, promoengine/config, catalogapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, checkoutapi/config | - | - | no | 7 | 0 | 7 | 11518 | 140 |
| 17 | 2 | giftcards | mizzen | memory-disabled | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | - | - | - | no | 7 | 0 | 7 | 2240 | 140 |
| 18 | 2 | giftcards | mizzen | memory-enabled | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | accountsapi/config, promoengine/config, catalogapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, checkoutapi/config | - | - | no | 7 | 0 | 7 | 11518 | 140 |
| 19 | 3 | reviewhub | bowsprit | memory-placebo | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | accountsapi/config, inventoryapi/config, warehouseapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config | - | - | no | 6 | 0 | 6 | 9715 | 120 |
| 20 | 3 | reviewhub | bowsprit | memory-disabled | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | - | - | - | no | 6 | 0 | 6 | 1757 | 120 |
| 21 | 3 | reviewhub | bowsprit | memory-enabled | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | accountsapi/config, inventoryapi/config, warehouseapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config | - | - | no | 6 | 0 | 6 | 9715 | 120 |
| 22 | 3 | reviewhub | bowsprit | mismatched-trait | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | accountsapi/config, inventoryapi/config, warehouseapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config | - | - | no | 6 | 0 | 6 | 9715 | 120 |
| 23 | 4 | taxcalc | capstan | memory-disabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | - | - | - | no | 5 | 0 | 5 | 1313 | 100 |
| 24 | 4 | taxcalc | capstan | memory-enabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | dispatchhub/config, quotesvc/config, accountsapi/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, checkoutapi/config | - | - | no | 5 | 0 | 5 | 7943 | 100 |
| 25 | 4 | taxcalc | capstan | mismatched-trait | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | dispatchhub/config, quotesvc/config, accountsapi/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, checkoutapi/config | - | - | no | 5 | 0 | 5 | 7943 | 100 |
| 26 | 4 | taxcalc | capstan | memory-placebo | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | dispatchhub/config, quotesvc/config, accountsapi/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, checkoutapi/config | - | - | no | 5 | 0 | 5 | 7943 | 100 |
| 27 | 5 | searchindex | taffrail | memory-enabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | paymentsui/config, reportingapi/config, accountsapi/config, dispatchhub/config, quotesvc/config, cartpricing/config, promoengine/config, checkoutapi/config | - | - | no | 4 | 0 | 4 | 6274 | 80 |
| 28 | 5 | searchindex | taffrail | mismatched-trait | completed | yes | 2 | in-place > online-copy > expand-contract (live) | paymentsui/config, reportingapi/config, accountsapi/config, dispatchhub/config, quotesvc/config, cartpricing/config, promoengine/config, checkoutapi/config | - | - | no | 4 | 0 | 4 | 6274 | 80 |
| 29 | 5 | searchindex | taffrail | memory-placebo | completed | yes | 2 | in-place > online-copy > expand-contract (live) | paymentsui/config, reportingapi/config, accountsapi/config, dispatchhub/config, quotesvc/config, cartpricing/config, promoengine/config, checkoutapi/config | - | - | no | 4 | 0 | 4 | 6274 | 80 |
| 30 | 5 | searchindex | taffrail | memory-disabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | - | - | - | no | 4 | 0 | 4 | 972 | 80 |
| 31 | 6 | loyaltyclub | halyard | mismatched-trait | completed | yes | 1 | in-place > online-copy (live) | accountsapi/config, checkoutapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, notifyhub/config | - | - | no | 3 | 0 | 3 | 4617 | 60 |
| 32 | 6 | loyaltyclub | halyard | memory-placebo | completed | yes | 1 | in-place > online-copy (live) | accountsapi/config, checkoutapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, notifyhub/config | - | - | no | 3 | 0 | 3 | 4617 | 60 |
| 33 | 6 | loyaltyclub | halyard | memory-disabled | completed | yes | 1 | in-place > online-copy (live) | - | - | - | no | 3 | 0 | 3 | 640 | 60 |
| 34 | 6 | loyaltyclub | halyard | memory-enabled | completed | yes | 1 | in-place > online-copy (live) | accountsapi/config, checkoutapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, notifyhub/config | - | - | no | 3 | 0 | 3 | 4617 | 60 |
| 35 | 7 | authgate | keel | memory-placebo | completed | yes | 0 | in-place (live) | cartpricing/config, accountsapi/config, notifyhub/config, dispatchhub/config, quotesvc/config, paymentsui/config, reportingapi/config, promoengine/config | - | - | yes | 2 | 0 | 2 | 3011 | 40 |
| 36 | 7 | authgate | keel | memory-disabled | completed | yes | 0 | in-place (live) | - | - | - | yes | 2 | 0 | 2 | 359 | 40 |
| 37 | 7 | authgate | keel | memory-enabled | completed | yes | 0 | in-place (live) | cartpricing/config, accountsapi/config, notifyhub/config, dispatchhub/config, quotesvc/config, paymentsui/config, reportingapi/config, promoengine/config | - | - | yes | 2 | 0 | 2 | 3011 | 40 |
| 38 | 7 | authgate | keel | mismatched-trait | completed | yes | 0 | in-place (live) | cartpricing/config, accountsapi/config, notifyhub/config, dispatchhub/config, quotesvc/config, paymentsui/config, reportingapi/config, promoengine/config | - | - | yes | 2 | 0 | 2 | 3011 | 40 |
| 39 | 8 | mailqueue | mizzen | memory-disabled | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | - | - | - | no | 7 | 0 | 7 | 2234 | 140 |
| 40 | 8 | mailqueue | mizzen | memory-enabled | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | paymentsui/config, accountsapi/config, promoengine/config, notifyhub/config, catalogapi/config, quotesvc/config, cartpricing/config, reportingapi/config | - | - | no | 7 | 0 | 7 | 11509 | 140 |
| 41 | 8 | mailqueue | mizzen | mismatched-trait | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | paymentsui/config, accountsapi/config, promoengine/config, notifyhub/config, catalogapi/config, quotesvc/config, cartpricing/config, reportingapi/config | - | - | no | 7 | 0 | 7 | 11509 | 140 |
| 42 | 8 | mailqueue | mizzen | memory-placebo | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | paymentsui/config, accountsapi/config, promoengine/config, notifyhub/config, catalogapi/config, quotesvc/config, cartpricing/config, reportingapi/config | - | - | no | 7 | 0 | 7 | 11509 | 140 |
| 43 | 9 | fraudwatch | bowsprit | memory-enabled | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | accountsapi/config, inventoryapi/config, warehouseapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config | - | - | no | 6 | 0 | 6 | 9726 | 120 |
| 44 | 9 | fraudwatch | bowsprit | mismatched-trait | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | accountsapi/config, inventoryapi/config, warehouseapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config | - | - | no | 6 | 0 | 6 | 9726 | 120 |
| 45 | 9 | fraudwatch | bowsprit | memory-placebo | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | accountsapi/config, inventoryapi/config, warehouseapi/config, quotesvc/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config | - | - | no | 6 | 0 | 6 | 9726 | 120 |
| 46 | 9 | fraudwatch | bowsprit | memory-disabled | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | - | - | - | no | 6 | 0 | 6 | 1768 | 120 |
| 47 | 10 | shipquote | capstan | mismatched-trait | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | quotesvc/config, dispatchhub/config, accountsapi/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, checkoutapi/config | - | - | no | 5 | 0 | 5 | 7960 | 100 |
| 48 | 10 | shipquote | capstan | memory-placebo | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | quotesvc/config, dispatchhub/config, accountsapi/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, checkoutapi/config | - | - | no | 5 | 0 | 5 | 7960 | 100 |
| 49 | 10 | shipquote | capstan | memory-disabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | - | - | - | no | 5 | 0 | 5 | 1330 | 100 |
| 50 | 10 | shipquote | capstan | memory-enabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | quotesvc/config, dispatchhub/config, accountsapi/config, paymentsui/config, cartpricing/config, reportingapi/config, promoengine/config, checkoutapi/config | - | - | no | 5 | 0 | 5 | 7960 | 100 |
| 51 | 11 | returnsdesk | taffrail | memory-placebo | completed | yes | 2 | in-place > online-copy > expand-contract (live) | paymentsui/config, reportingapi/config, accountsapi/config, quotesvc/config, cartpricing/config, promoengine/config, checkoutapi/config, notifyhub/config | - | - | no | 4 | 0 | 4 | 6277 | 80 |
| 52 | 11 | returnsdesk | taffrail | memory-disabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | - | - | - | no | 4 | 0 | 4 | 976 | 80 |
| 53 | 11 | returnsdesk | taffrail | memory-enabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | paymentsui/config, reportingapi/config, accountsapi/config, quotesvc/config, cartpricing/config, promoengine/config, checkoutapi/config, notifyhub/config | - | - | no | 4 | 0 | 4 | 6277 | 80 |
| 54 | 11 | returnsdesk | taffrail | mismatched-trait | completed | yes | 2 | in-place > online-copy > expand-contract (live) | paymentsui/config, reportingapi/config, accountsapi/config, quotesvc/config, cartpricing/config, promoengine/config, checkoutapi/config, notifyhub/config | - | - | no | 4 | 0 | 4 | 6277 | 80 |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 27 | 7523 | 540 | n/a |
| Evaluation | 216 | 273096 | 4320 | n/a |
| **Total** | **243** | **280619** | **4860** | **n/a** |

Cost not estimated: none: scripted run, nothing is billed.
Every response reported its token usage.
Latency is in the raw results only: it is a property of one machine and network, and is never gated.

## Limitations

- This is a scripted run. The scripted operator follows the first strategy on the block's first working line by construction, so any benefit here is a property of the script and of where the library's retrieval ranked the same-cluster record.
- 12 instances in 6 clusters. One learning record serves both instances of a cluster, so the pairs are not independent and the sign test's p-value is optimistic.
- The trait is named in every task text (`cluster ...`) and by `describe_service`. Transfer here means finding and trusting a lesson keyed by a named trait, not discovering an unnamed one.
- Retrieval is the in-memory candidate source's word matching, not PostgreSQL full-text or vector search; ranks under another store would differ. The task texts were written once, before the first offline run, and are not tuned to rank the same-cluster record first.
- The working strategy reaches the block verbatim through the story 6.2 allowlist, as in the reuse experiment. A block can also name other strategies on failed attempts' Tried: lines and on other records; `Block named` and `Followed` read the first record's working line only.
- The mismatched-trait control is weaker than it looks. Under the bijection, its block can show the strategies of the other clusters, which a capable model can rule out to find the answer by elimination; and its records are the other clusters' lessons, which a model that follows the block pays for. A control that passes the gate can only move the verdict to `TransferBenefitNotAttributableToContent`, a conservative error: it can withhold a claim, never make one. `Harm` above reports how often it cost attempts.
- A synthetic task family with a simulated database; real tasks usually leave the answer partly inferable.
- The repository is public, and the trait mapping is in its source. Check a model's training cutoff against the registration date.

## Raw results

None: a scripted run writes no file. The command prints this report only.
