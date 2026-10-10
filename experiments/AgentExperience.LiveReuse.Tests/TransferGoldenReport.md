# Transfer experiment: scripted / scripted-operator / 2026-09-26

> **SCRIPTED RUN. No model was called.** Every number below is a property of `ScriptedOperatorModel`, a deterministic
> stand-in that follows the first strategy on the block's first working line. It proves the harness and shows what the
> library's retrieval put in each block, not the premise. Quote nothing here as a model result.

**Overall conclusion under the pre-registered rule: TransferBenefitAttributableToContent.** With the library's retrieval over the shared store, the scripted operator needed fewer failed attempts on unseen services than with memory disabled and than with the same records with the strategy withheld, by the pre-registered test, and the store without the same-cluster lesson gave it no such benefit.

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
| Pre-registration | `preregistration.transfer.json` at git blob `2d4407c34c60948f4f7155ed07f9bc15e33143a4` (14133 bytes), as amended; first registered at git blob `f568bcd660433abe6d472271b918ae804c0226d9`, registered in the commit that adds this file (parent c3dcb37), on 2026-10-10; no live results existed at registration; the offline scripted results are part of the design; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.transfer.json` |
| Amendments | 1 recorded, every one after the registered offline scripted results; 0 made after live results existed |
| Task set | `transfer-clusters@1`: 6 clusters, 12 evaluation instances, 24 distractors; trait digest `78d4d01e140a685d83cccfab968e10e7b9d7850ab4bdb9651ea245778243b938` |
| Shared store | one scope, `live-reuse/transfer-desk/shared`: 24 distractor records (12 cache flushes, 12 config rollouts) and 6 learning records; retrieval by `InMemoryExperienceCandidateSource` under `RetrievalPolicy.Default with { Timeout = 15 s }`, injection by `ExperienceContextProvider` (at most 8 records) |
| Attempt limits | learning 8, evaluation 6; at most 6 tool calls per attempt |
| Budget cap | 500 model calls, 4000000 tokens; used 224 calls, 245702 tokens |
| Earlier runs | not recorded (no ledger for this run) |
| Run status | complete: every learning run and every trial ran |
| Started (UTC) | 2026-09-26 12:00:00 |

## Verdict

The gate, read from the pre-registration and evaluated once per comparison:

```
mean(failed_attempts | treatment) < mean(failed_attempts | control) AND sign_test_p(failed_attempts: treatment < control) <= 0.05 AND success_rate(treatment) >= success_rate(control) AND sum(unauthorized_tool_requests | treatment) <= sum(unauthorized_tool_requests | control)
```

### Reference comparison: `memory-enabled` against `memory-disabled` -- BenefitDemonstrated

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)` | yes | 0.417 against 2.500 |
| `sign_test_p(failed_attempts: memory-enabled < memory-disabled) <= 0.05` | yes | p = 0.0020 (9 fewer, 0 more, 3 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-disabled)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 0 against 0 |

### Content comparison: `memory-enabled` against `memory-placebo` -- BenefitDemonstrated (the same records with the strategy withheld: separates what the block says from the fact that a block is there)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-placebo)` | yes | 0.417 against 2.500 |
| `sign_test_p(failed_attempts: memory-enabled < memory-placebo) <= 0.05` | yes | p = 0.0020 (9 fewer, 0 more, 3 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-placebo)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-placebo)` | yes | 0 against 0 |

### Mismatched-trait control: `mismatched-trait` against `memory-disabled` -- NoDemonstratedBenefit (the same store without the service's own cluster's lesson; expected NoDemonstratedBenefit)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | mismatched-trait) < mean(failed_attempts | memory-disabled)` | **no** | 3.000 against 2.500 |
| `sign_test_p(failed_attempts: mismatched-trait < memory-disabled) <= 0.05` | **no** | p = 1.0000 (0 fewer, 6 more, 6 tied, of 12 pairs) |
| `success_rate(mismatched-trait) >= success_rate(memory-disabled)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | mismatched-trait) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 0 against 0 |

Excluded instances: none (the limit is 4).

### Harm: `mismatched-trait` against `memory-disabled` (reported, never gated)

Of 12 pairs, the mismatched-trait trial failed more attempts in 6, fewer in 0, and as many in 6. Mean failed attempts: 3.000 mismatched-trait, 2.500 memory-disabled.

## By instance: retrieval and failed attempts

What the library's retrieval put in each block, read out of the text the model was sent, and each condition's failed
attempts (the primary metric). *Rank* is the position of the same-cluster learning record in the memory-enabled block;
the mismatched-trait columns show the record its block ranked first and the strategy on that record's working line.
Never gated.

| Instance | Service | Cluster | Strategy | Same-cluster record injected | Rank | Records in block | Mismatched: first record | Mismatched: first strategy | Failed: disabled | Failed: enabled | Failed: placebo | Failed: mismatched | Enabled followed the block | Enabled followed the transferred lesson |
| ---: | --- | --- | --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| 0 | refundflow | halyard | online-copy | yes | 1 | 6 | couponvault/learning | batched-backfill | 1 | 0 | 1 | 2 | yes | yes |
| 1 | wishlist | keel | in-place | yes | 1 | 6 | stockpile/learning | shadow-table | 0 | 0 | 0 | 1 | yes | yes |
| 2 | giftcards | mizzen | batched-backfill | yes | 1 | 6 | stockpile/learning | shadow-table | 5 | 0 | 5 | 5 | yes | yes |
| 3 | reviewhub | bowsprit | shadow-table | yes | 1 | 6 | pricebook/learning | expand-contract | 4 | 0 | 4 | 4 | yes | yes |
| 4 | taxcalc | capstan | blue-green | yes | 1 | 6 | basketsync/learning | in-place | 3 | 0 | 3 | 3 | yes | yes |
| 5 | searchindex | taffrail | expand-contract | yes | 1 | 6 | stockpile/learning | shadow-table | 2 | 0 | 2 | 3 | yes | yes |
| 6 | loyaltyclub | halyard | online-copy | yes | 1 | 6 | stockpile/learning | shadow-table | 1 | 0 | 1 | 2 | yes | yes |
| 7 | authgate | keel | in-place | yes | 1 | 6 | parcelroute/learning | blue-green | 0 | 0 | 0 | 1 | yes | yes |
| 8 | mailqueue | mizzen | batched-backfill | yes | 3 | 6 | stockpile/learning | shadow-table | 5 | 5 | 5 | 5 | yes | no |
| 9 | fraudwatch | bowsprit | shadow-table | yes | 1 | 6 | pricebook/learning | expand-contract | 4 | 0 | 4 | 4 | yes | yes |
| 10 | shipquote | capstan | blue-green | yes | 1 | 6 | basketsync/learning | in-place | 3 | 0 | 3 | 3 | yes | yes |
| 11 | returnsdesk | taffrail | expand-contract | yes | 1 | 6 | parcelroute/learning | blue-green | 2 | 0 | 2 | 3 | yes | yes |

The same-cluster record was in the memory-enabled block for 12 of 12 instances, and ranked first for 11.

## Metrics by condition

Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.

| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | First-try success | Followed the block | Followed the transferred lesson | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 12 | 12 | 1.000 (12/12) | 2.500 (1.784) | 0.167 (2/12) | - | 0.167 (2/12) | 4.500 | 0 | 54 | 14583 | 1080 | n/a |
| memory-enabled | 12 | 12 | 1.000 (12/12) | 0.417 (1.443) | 0.917 (11/12) | 1.000 (12/12) | 0.917 (11/12) | 2.417 | 0 | 29 | 47097 | 580 | n/a |
| memory-placebo | 12 | 12 | 1.000 (12/12) | 2.500 (1.784) | 0.167 (2/12) | - | 0.167 (2/12) | 4.500 | 0 | 54 | 84759 | 1080 | n/a |
| mismatched-trait | 12 | 12 | 1.000 (12/12) | 3.000 (1.348) | 0.000 (0/12) | 1.000 (12/12) | 0.000 (0/12) | 5.000 | 0 | 60 | 87260 | 1200 | n/a |

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
| 8 | 0 | refundflow | halyard | memory-enabled | completed | yes | 0 | online-copy (live) | ledgerpost/learning, couponvault/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | online-copy | yes | yes | 2 | 0 | 2 | 3180 | 40 |
| 9 | 0 | refundflow | halyard | mismatched-trait | completed | yes | 2 | batched-backfill > in-place > online-copy (live) | couponvault/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | batched-backfill | yes | no | 4 | 0 | 4 | 5798 | 80 |
| 10 | 0 | refundflow | halyard | memory-placebo | completed | yes | 1 | in-place > online-copy (live) | ledgerpost/learning, couponvault/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | - | - | no | 3 | 0 | 3 | 4532 | 60 |
| 11 | 1 | wishlist | keel | memory-enabled | completed | yes | 0 | in-place (live) | basketsync/learning, stockpile/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | in-place | yes | yes | 2 | 0 | 2 | 3179 | 40 |
| 12 | 1 | wishlist | keel | mismatched-trait | completed | yes | 1 | shadow-table > in-place (live) | stockpile/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | shadow-table | yes | no | 3 | 0 | 3 | 4345 | 60 |
| 13 | 1 | wishlist | keel | memory-placebo | completed | yes | 0 | in-place (live) | basketsync/learning, stockpile/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | - | - | yes | 2 | 0 | 2 | 2959 | 40 |
| 14 | 1 | wishlist | keel | memory-disabled | completed | yes | 0 | in-place (live) | - | - | - | yes | 2 | 0 | 2 | 360 | 40 |
| 15 | 2 | giftcards | mizzen | mismatched-trait | completed | yes | 5 | shadow-table > in-place > online-copy > expand-contract > blue-green > batched-backfill (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | shadow-table | yes | no | 7 | 0 | 7 | 10357 | 140 |
| 16 | 2 | giftcards | mizzen | memory-placebo | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | couponvault/learning, stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | - | - | no | 7 | 0 | 7 | 11336 | 140 |
| 17 | 2 | giftcards | mizzen | memory-disabled | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | - | - | - | no | 7 | 0 | 7 | 2240 | 140 |
| 18 | 2 | giftcards | mizzen | memory-enabled | completed | yes | 0 | batched-backfill (live) | couponvault/learning, stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | batched-backfill | yes | yes | 2 | 0 | 2 | 3180 | 40 |
| 19 | 3 | reviewhub | bowsprit | memory-placebo | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | - | - | no | 6 | 0 | 6 | 9555 | 120 |
| 20 | 3 | reviewhub | bowsprit | memory-disabled | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | - | - | - | no | 6 | 0 | 6 | 1757 | 120 |
| 21 | 3 | reviewhub | bowsprit | memory-enabled | completed | yes | 0 | shadow-table (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | shadow-table | yes | yes | 2 | 0 | 2 | 3180 | 40 |
| 22 | 3 | reviewhub | bowsprit | mismatched-trait | completed | yes | 4 | expand-contract > in-place > online-copy > blue-green > shadow-table (live) | pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | expand-contract | yes | no | 6 | 0 | 6 | 8730 | 120 |
| 23 | 4 | taxcalc | capstan | memory-disabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | - | - | - | no | 5 | 0 | 5 | 1313 | 100 |
| 24 | 4 | taxcalc | capstan | memory-enabled | completed | yes | 0 | blue-green (live) | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | blue-green | yes | yes | 2 | 0 | 2 | 3178 | 40 |
| 25 | 4 | taxcalc | capstan | mismatched-trait | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | in-place | yes | no | 5 | 0 | 5 | 7124 | 100 |
| 26 | 4 | taxcalc | capstan | memory-placebo | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | - | - | no | 5 | 0 | 5 | 7812 | 100 |
| 27 | 5 | searchindex | taffrail | memory-enabled | completed | yes | 0 | expand-contract (live) | pricebook/learning, stockpile/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | expand-contract | yes | yes | 2 | 0 | 2 | 3185 | 40 |
| 28 | 5 | searchindex | taffrail | mismatched-trait | completed | yes | 3 | shadow-table > in-place > online-copy > expand-contract (live) | stockpile/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | shadow-table | yes | no | 5 | 0 | 5 | 7257 | 100 |
| 29 | 5 | searchindex | taffrail | memory-placebo | completed | yes | 2 | in-place > online-copy > expand-contract (live) | pricebook/learning, stockpile/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | - | - | no | 4 | 0 | 4 | 6168 | 80 |
| 30 | 5 | searchindex | taffrail | memory-disabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | - | - | - | no | 4 | 0 | 4 | 972 | 80 |
| 31 | 6 | loyaltyclub | halyard | mismatched-trait | completed | yes | 2 | shadow-table > in-place > online-copy (live) | stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning, couponvault/learning | shadow-table | yes | no | 4 | 0 | 4 | 5807 | 80 |
| 32 | 6 | loyaltyclub | halyard | memory-placebo | completed | yes | 1 | in-place > online-copy (live) | ledgerpost/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning, couponvault/learning | - | - | no | 3 | 0 | 3 | 4540 | 60 |
| 33 | 6 | loyaltyclub | halyard | memory-disabled | completed | yes | 1 | in-place > online-copy (live) | - | - | - | no | 3 | 0 | 3 | 640 | 60 |
| 34 | 6 | loyaltyclub | halyard | memory-enabled | completed | yes | 0 | online-copy (live) | ledgerpost/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning, couponvault/learning | online-copy | yes | yes | 2 | 0 | 2 | 3185 | 40 |
| 35 | 7 | authgate | keel | memory-placebo | completed | yes | 0 | in-place (live) | basketsync/learning, parcelroute/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | - | - | yes | 2 | 0 | 2 | 2959 | 40 |
| 36 | 7 | authgate | keel | memory-disabled | completed | yes | 0 | in-place (live) | - | - | - | yes | 2 | 0 | 2 | 359 | 40 |
| 37 | 7 | authgate | keel | memory-enabled | completed | yes | 0 | in-place (live) | basketsync/learning, parcelroute/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | in-place | yes | yes | 2 | 0 | 2 | 3179 | 40 |
| 38 | 7 | authgate | keel | mismatched-trait | completed | yes | 1 | blue-green > in-place (live) | parcelroute/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | blue-green | yes | no | 3 | 0 | 3 | 4344 | 60 |
| 39 | 8 | mailqueue | mizzen | memory-disabled | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | - | - | - | no | 7 | 0 | 7 | 2234 | 140 |
| 40 | 8 | mailqueue | mizzen | memory-enabled | completed | yes | 5 | shadow-table > in-place > online-copy > expand-contract > blue-green > batched-backfill (live) | stockpile/learning, ledgerpost/learning, couponvault/learning, pricebook/learning, parcelroute/learning, basketsync/learning | shadow-table | yes | no | 7 | 0 | 7 | 12102 | 140 |
| 41 | 8 | mailqueue | mizzen | mismatched-trait | completed | yes | 5 | shadow-table > in-place > online-copy > expand-contract > blue-green > batched-backfill (live) | stockpile/learning, ledgerpost/learning, pricebook/learning, parcelroute/learning, basketsync/learning | shadow-table | yes | no | 7 | 0 | 7 | 10352 | 140 |
| 42 | 8 | mailqueue | mizzen | memory-placebo | completed | yes | 5 | in-place > online-copy > expand-contract > blue-green > shadow-table > batched-backfill (live) | stockpile/learning, ledgerpost/learning, couponvault/learning, pricebook/learning, parcelroute/learning, basketsync/learning | - | - | no | 7 | 0 | 7 | 11331 | 140 |
| 43 | 9 | fraudwatch | bowsprit | memory-enabled | completed | yes | 0 | shadow-table (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | shadow-table | yes | yes | 2 | 0 | 2 | 3182 | 40 |
| 44 | 9 | fraudwatch | bowsprit | mismatched-trait | completed | yes | 4 | expand-contract > in-place > online-copy > blue-green > shadow-table (live) | pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | expand-contract | yes | no | 6 | 0 | 6 | 8740 | 120 |
| 45 | 9 | fraudwatch | bowsprit | memory-placebo | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning, couponvault/learning | - | - | no | 6 | 0 | 6 | 9566 | 120 |
| 46 | 9 | fraudwatch | bowsprit | memory-disabled | completed | yes | 4 | in-place > online-copy > expand-contract > blue-green > shadow-table (live) | - | - | - | no | 6 | 0 | 6 | 1768 | 120 |
| 47 | 10 | shipquote | capstan | mismatched-trait | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | in-place | yes | no | 5 | 0 | 5 | 7143 | 100 |
| 48 | 10 | shipquote | capstan | memory-placebo | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | - | - | no | 5 | 0 | 5 | 7828 | 100 |
| 49 | 10 | shipquote | capstan | memory-disabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | - | - | - | no | 5 | 0 | 5 | 1330 | 100 |
| 50 | 10 | shipquote | capstan | memory-enabled | completed | yes | 0 | blue-green (live) | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning, couponvault/learning | blue-green | yes | yes | 2 | 0 | 2 | 3180 | 40 |
| 51 | 11 | returnsdesk | taffrail | memory-placebo | completed | yes | 2 | in-place > online-copy > expand-contract (live) | pricebook/learning, parcelroute/learning, basketsync/learning, stockpile/learning, ledgerpost/learning, couponvault/learning | - | - | no | 4 | 0 | 4 | 6173 | 80 |
| 52 | 11 | returnsdesk | taffrail | memory-disabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | - | - | - | no | 4 | 0 | 4 | 976 | 80 |
| 53 | 11 | returnsdesk | taffrail | memory-enabled | completed | yes | 0 | expand-contract (live) | pricebook/learning, parcelroute/learning, basketsync/learning, stockpile/learning, ledgerpost/learning, couponvault/learning | expand-contract | yes | yes | 2 | 0 | 2 | 3187 | 40 |
| 54 | 11 | returnsdesk | taffrail | mismatched-trait | completed | yes | 3 | blue-green > in-place > online-copy > expand-contract (live) | parcelroute/learning, basketsync/learning, stockpile/learning, ledgerpost/learning, couponvault/learning | blue-green | yes | no | 5 | 0 | 5 | 7263 | 100 |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 27 | 7523 | 540 | n/a |
| Evaluation | 197 | 233699 | 3940 | n/a |
| **Total** | **224** | **241222** | **4480** | **n/a** |

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
