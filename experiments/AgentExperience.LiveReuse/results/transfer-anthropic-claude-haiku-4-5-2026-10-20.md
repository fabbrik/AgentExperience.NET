# Transfer experiment: anthropic / claude-haiku-4-5 / 2026-10-20

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
| Provider | anthropic, host `api.anthropic.com` (the endpoint's host only) |
| Model requested | `claude-haiku-4-5` |
| Model identities the provider reported | `claude-haiku-4-5-20251001` |
| Settings | temperature 0, seed 20260926 not sent: this provider does not accept a seed, max output tokens 4096 per call |
| Pre-registration | `preregistration.transfer.json` at git blob `2d4407c34c60948f4f7155ed07f9bc15e33143a4` (14133 bytes), as amended; first registered at git blob `f568bcd660433abe6d472271b918ae804c0226d9`, registered in the commit that adds this file (parent c3dcb37), on 2026-10-10; no live results existed at registration; the offline scripted results are part of the design; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.transfer.json` |
| Amendments | 1 recorded, every one after the registered offline scripted results; 0 made after live results existed |
| Task set | `transfer-clusters@1`: 6 clusters, 12 evaluation instances, 24 distractors; trait digest `78d4d01e140a685d83cccfab968e10e7b9d7850ab4bdb9651ea245778243b938` |
| Shared store | one scope, `live-reuse/transfer-desk/shared`: 24 distractor records (12 cache flushes, 12 config rollouts) and 5 learning records; retrieval by `InMemoryExperienceCandidateSource` under `RetrievalPolicy.Default with { Timeout = 15 s }`, injection by `ExperienceContextProvider` (at most 8 records) |
| Attempt limits | learning 8, evaluation 6; at most 6 tool calls per attempt |
| Budget cap | 500 model calls, 4000000 tokens; used 245 calls, 572133 tokens |
| Earlier runs | 0 earlier ledger entries for this provider and model in `results/transfer-ledger.tsv`; the confirmatory run is the first complete one |
| Run status | complete: every learning run and every trial ran |
| Started (UTC) | 2026-10-20 17:24:47 |

## Verdict

The gate, read from the pre-registration and evaluated once per comparison:

```
mean(failed_attempts | treatment) < mean(failed_attempts | control) AND sign_test_p(failed_attempts: treatment < control) <= 0.05 AND success_rate(treatment) >= success_rate(control) AND sum(unauthorized_tool_requests | treatment) <= sum(unauthorized_tool_requests | control)
```

### Reference comparison: `memory-enabled` against `memory-disabled` -- NoDemonstratedBenefit

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)` | yes | 2.400 against 3.500 |
| `sign_test_p(failed_attempts: memory-enabled < memory-disabled) <= 0.05` | **no** | p = 0.0625 (6 fewer, 1 more, 3 tied, of 10 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-disabled)` | yes | 0.800 against 0.500 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 3 against 5 |

### Content comparison: `memory-enabled` against `memory-placebo` -- NoDemonstratedBenefit (the same records with the strategy withheld: separates what the block says from the fact that a block is there)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-placebo)` | yes | 2.400 against 3.100 |
| `sign_test_p(failed_attempts: memory-enabled < memory-placebo) <= 0.05` | **no** | p = 0.0625 (6 fewer, 1 more, 3 tied, of 10 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-placebo)` | yes | 0.800 against 0.800 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-placebo)` | yes | 3 against 5 |

### Mismatched-trait control: `mismatched-trait` against `memory-disabled` -- NoDemonstratedBenefit (the same store without the service's own cluster's lesson; expected NoDemonstratedBenefit)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | mismatched-trait) < mean(failed_attempts | memory-disabled)` | yes | 2.400 against 3.500 |
| `sign_test_p(failed_attempts: mismatched-trait < memory-disabled) <= 0.05` | **no** | p = 0.3633 (5 fewer, 3 more, 2 tied, of 10 pairs) |
| `success_rate(mismatched-trait) >= success_rate(memory-disabled)` | yes | 0.900 against 0.500 |
| `sum(unauthorized_tool_requests | mismatched-trait) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 2 against 5 |

Excluded instances: 2 (the learning run of cluster mizzen did not verify, so it ran no evaluation trial); 8 (the learning run of cluster mizzen did not verify, so it ran no evaluation trial) (the limit is 4).

### Harm: `mismatched-trait` against `memory-disabled` (reported, never gated)

Of 10 pairs, the mismatched-trait trial failed more attempts in 3, fewer in 5, and as many in 2. Mean failed attempts: 2.400 mismatched-trait, 3.500 memory-disabled.

## By instance: retrieval and failed attempts

What the library's retrieval put in each block, read out of the text the model was sent, and each condition's failed
attempts (the primary metric). *Rank* is the position of the same-cluster learning record in the memory-enabled block;
the mismatched-trait columns show the record its block ranked first and the strategy on that record's working line.
Never gated.

| Instance | Service | Cluster | Strategy | Same-cluster record injected | Rank | Records in block | Mismatched: first record | Mismatched: first strategy | Failed: disabled | Failed: enabled | Failed: placebo | Failed: mismatched | Enabled followed the block | Enabled followed the transferred lesson |
| ---: | --- | --- | --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| 0 | refundflow | halyard | online-copy | yes | 1 | 5 | stockpile/learning | shadow-table | 1 | 0 | 1 | 1 | yes | yes |
| 1 | wishlist | keel | in-place | yes | 1 | 5 | stockpile/learning | shadow-table | 6 | 2 | 5 | 2 | no | no |
| 3 | reviewhub | bowsprit | shadow-table | yes | 1 | 5 | pricebook/learning | expand-contract | 6 | 6 | 6 | 4 | no | no |
| 4 | taxcalc | capstan | blue-green | yes | 1 | 5 | basketsync/learning | in-place | 6 | 3 | 6 | 3 | no | no |
| 5 | searchindex | taffrail | expand-contract | yes | 1 | 5 | stockpile/learning | shadow-table | 0 | 0 | 0 | 1 | yes | yes |
| 6 | loyaltyclub | halyard | online-copy | yes | 1 | 5 | stockpile/learning | shadow-table | 2 | 0 | 1 | 1 | yes | yes |
| 7 | authgate | keel | in-place | yes | 1 | 5 | parcelroute/learning | blue-green | 6 | 4 | 5 | 2 | no | no |
| 9 | fraudwatch | bowsprit | shadow-table | yes | 1 | 5 | pricebook/learning | expand-contract | 6 | 3 | 4 | 6 | no | no |
| 10 | shipquote | capstan | blue-green | yes | 1 | 5 | basketsync/learning | in-place | 2 | 6 | 3 | 3 | no | no |
| 11 | returnsdesk | taffrail | expand-contract | yes | 1 | 5 | parcelroute/learning | blue-green | 0 | 0 | 0 | 1 | yes | yes |

The same-cluster record was in the memory-enabled block for 10 of 10 instances, and ranked first for 10.

## Metrics by condition

Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.

| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | First-try success | Followed the block | Followed the transferred lesson | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 10 | 10 | 0.500 (5/10) | 3.500 (2.718) | 0.200 (2/10) | - | 0.200 (2/10) | 5.900 | 5 | 58 | 73222 | 12664 | 0.1365 |
| memory-enabled | 10 | 10 | 0.800 (8/10) | 2.400 (2.413) | 0.400 (4/10) | 0.400 (4/10) | 0.400 (4/10) | 5.200 | 3 | 46 | 127570 | 10252 | 0.1788 |
| memory-placebo | 10 | 10 | 0.800 (8/10) | 3.100 (2.424) | 0.200 (2/10) | - | 0.200 (2/10) | 6.700 | 5 | 57 | 152254 | 14147 | 0.2230 |
| mismatched-trait | 10 | 10 | 0.900 (9/10) | 2.400 (1.647) | 0.000 (0/10) | 0.200 (2/10) | 0.000 (0/10) | 5.700 | 2 | 48 | 117448 | 10242 | 0.1687 |

## Learning phase

One memory-disabled run per cluster, on its learning service. A run that verified was finalized into the shared store; one that
did not stored nothing, and its cluster's two evaluation instances are excluded and run no trial.

| Seq | Cluster | Service | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |
| ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |
| 1 | halyard | ledgerpost | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | online-copy | 3 | 3288 | 507 |
| 2 | keel | basketsync | in-place | completed | yes | 2 | online-copy > batched-backfill > in-place (live) | in-place > expand-contract > blue-green > shadow-table | 5 | 5794 | 1076 |
| 3 | mizzen | couponvault | batched-backfill | completed | no | 8 | expand-contract > online-copy > blue-green > in-place | - | 11 | 15636 | 2776 |
| 4 | bowsprit | stockpile | shadow-table | completed | yes | 3 | expand-contract > online-copy > in-place > shadow-table (live) | shadow-table | 6 | 7192 | 1219 |
| 5 | capstan | parcelroute | blue-green | completed | yes | 4 | online-copy > expand-contract > in-place > blue-green (live) | blue-green | 9 | 12421 | 2034 |
| 6 | taffrail | pricebook | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 2129 | 262 |

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
| 7 | 0 | refundflow | halyard | memory-disabled | completed | yes | 1 | expand-contract > online-copy (live) | - | - | - | no | 3 | 0 | 3 | 3269 | 457 |
| 8 | 0 | refundflow | halyard | memory-enabled | completed | yes | 0 | online-copy (live) | ledgerpost/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | online-copy | yes | yes | 2 | 0 | 2 | 5247 | 319 |
| 9 | 0 | refundflow | halyard | mismatched-trait | completed | yes | 1 | expand-contract > online-copy (live) | stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | shadow-table | no | no | 3 | 0 | 3 | 7220 | 483 |
| 10 | 0 | refundflow | halyard | memory-placebo | completed | yes | 1 | expand-contract > online-copy (live) | ledgerpost/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | - | - | no | 3 | 0 | 3 | 7535 | 554 |
| 11 | 1 | wishlist | keel | memory-enabled | completed | yes | 2 | online-copy > expand-contract > in-place (live) | basketsync/learning, stockpile/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | in-place > expand-contract > blue-green > shadow-table | no | no | 4 | 0 | 4 | 10741 | 724 |
| 12 | 1 | wishlist | keel | mismatched-trait | completed | yes | 2 | online-copy > shadow-table > in-place (live) | stockpile/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | shadow-table | no | no | 4 | 0 | 4 | 9385 | 691 |
| 13 | 1 | wishlist | keel | memory-placebo | completed | yes | 5 | online-copy > expand-contract > blue-green > shadow-table > batched-backfill > in-place (live) | basketsync/learning, stockpile/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | - | - | no | 12 | 1 | 9 | 24781 | 2510 |
| 14 | 1 | wishlist | keel | memory-disabled | completed | no | 6 | online-copy > expand-contract > blue-green | - | - | - | no | 8 | 1 | 8 | 10604 | 2311 |
| 15 | 3 | reviewhub | bowsprit | memory-placebo | completed | no | 6 | expand-contract > online-copy > batched-backfill > in-place > blue-green | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | - | - | no | 10 | 1 | 9 | 24687 | 2439 |
| 16 | 3 | reviewhub | bowsprit | memory-disabled | completed | no | 6 | expand-contract > online-copy > blue-green > in-place | - | - | - | no | 8 | 1 | 8 | 10409 | 1918 |
| 17 | 3 | reviewhub | bowsprit | memory-enabled | completed | no | 6 | expand-contract > online-copy > in-place > batched-backfill > blue-green | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | shadow-table | no | no | 10 | 1 | 8 | 23252 | 2297 |
| 18 | 3 | reviewhub | bowsprit | mismatched-trait | completed | yes | 4 | expand-contract > online-copy > blue-green > in-place > shadow-table (live) | pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | expand-contract | yes | no | 12 | 1 | 10 | 25918 | 2600 |
| 19 | 4 | taxcalc | capstan | memory-disabled | completed | no | 6 | online-copy > expand-contract > in-place > shadow-table | - | - | - | no | 9 | 1 | 9 | 12043 | 2074 |
| 20 | 4 | taxcalc | capstan | memory-enabled | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 7 | 0 | 5 | 13622 | 989 |
| 21 | 4 | taxcalc | capstan | mismatched-trait | completed | yes | 3 | expand-contract > online-copy > in-place > blue-green (live) | basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | in-place > expand-contract > blue-green > shadow-table | no | no | 6 | 0 | 5 | 11772 | 1067 |
| 22 | 4 | taxcalc | capstan | memory-placebo | completed | no | 6 | online-copy > expand-contract > in-place > online-copy > online-copy | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | - | - | no | 10 | 1 | 8 | 22094 | 2160 |
| 23 | 5 | searchindex | taffrail | memory-enabled | completed | yes | 0 | expand-contract (live) | pricebook/learning, stockpile/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | expand-contract | yes | yes | 2 | 0 | 2 | 5246 | 324 |
| 24 | 5 | searchindex | taffrail | mismatched-trait | completed | yes | 1 | online-copy > expand-contract (live) | stockpile/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | shadow-table | no | no | 3 | 0 | 3 | 7331 | 514 |
| 25 | 5 | searchindex | taffrail | memory-placebo | completed | yes | 0 | expand-contract (live) | pricebook/learning, stockpile/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | - | - | yes | 2 | 0 | 2 | 4954 | 344 |
| 26 | 5 | searchindex | taffrail | memory-disabled | completed | yes | 0 | expand-contract (live) | - | - | - | yes | 2 | 0 | 2 | 2110 | 272 |
| 27 | 6 | loyaltyclub | halyard | mismatched-trait | completed | yes | 1 | expand-contract > online-copy (live) | stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | shadow-table | no | no | 3 | 0 | 3 | 7239 | 435 |
| 28 | 6 | loyaltyclub | halyard | memory-placebo | completed | yes | 1 | expand-contract > online-copy (live) | ledgerpost/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | - | - | no | 3 | 0 | 3 | 7554 | 519 |
| 29 | 6 | loyaltyclub | halyard | memory-disabled | completed | yes | 2 | expand-contract > blue-green > online-copy (live) | - | - | - | no | 5 | 0 | 4 | 4512 | 766 |
| 30 | 6 | loyaltyclub | halyard | memory-enabled | completed | yes | 0 | online-copy (live) | ledgerpost/learning, stockpile/learning, pricebook/learning, parcelroute/learning, basketsync/learning | online-copy | yes | yes | 2 | 0 | 2 | 5257 | 341 |
| 31 | 7 | authgate | keel | memory-placebo | completed | yes | 5 | online-copy > expand-contract > blue-green > shadow-table > batched-backfill > in-place (live) | basketsync/learning, parcelroute/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | - | - | no | 11 | 1 | 9 | 24444 | 2474 |
| 32 | 7 | authgate | keel | memory-disabled | completed | no | 6 | online-copy > expand-contract > blue-green > shadow-table | - | - | - | no | 9 | 1 | 9 | 11827 | 2046 |
| 33 | 7 | authgate | keel | memory-enabled | completed | yes | 4 | online-copy > expand-contract > blue-green > batched-backfill > in-place (live) | basketsync/learning, parcelroute/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | in-place > expand-contract > blue-green > shadow-table | no | no | 8 | 1 | 7 | 19549 | 1619 |
| 34 | 7 | authgate | keel | mismatched-trait | completed | yes | 2 | online-copy > expand-contract > in-place (live) | parcelroute/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 7 | 0 | 4 | 9387 | 955 |
| 35 | 9 | fraudwatch | bowsprit | memory-enabled | completed | yes | 3 | expand-contract > online-copy > in-place > shadow-table (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | shadow-table | no | no | 6 | 0 | 5 | 13581 | 952 |
| 36 | 9 | fraudwatch | bowsprit | mismatched-trait | completed | no | 6 | expand-contract > online-copy > in-place > blue-green > batched-backfill | pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | expand-contract | yes | no | 9 | 1 | 8 | 20098 | 1936 |
| 37 | 9 | fraudwatch | bowsprit | memory-placebo | completed | yes | 4 | expand-contract > online-copy > batched-backfill > in-place > shadow-table (live) | stockpile/learning, pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | - | - | no | 7 | 1 | 7 | 18380 | 1625 |
| 38 | 9 | fraudwatch | bowsprit | memory-disabled | completed | no | 6 | expand-contract > online-copy > batched-backfill > in-place | - | - | - | no | 9 | 1 | 9 | 11842 | 2009 |
| 39 | 10 | shipquote | capstan | mismatched-trait | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | in-place > expand-contract > blue-green > shadow-table | no | no | 7 | 0 | 5 | 11732 | 1014 |
| 40 | 10 | shipquote | capstan | memory-placebo | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | - | - | no | 7 | 0 | 5 | 12852 | 1155 |
| 41 | 10 | shipquote | capstan | memory-disabled | completed | yes | 2 | online-copy > expand-contract > blue-green (live) | - | - | - | no | 4 | 0 | 4 | 4477 | 547 |
| 42 | 10 | shipquote | capstan | memory-enabled | completed | no | 6 | expand-contract > online-copy > in-place > shadow-table > batched-backfill | parcelroute/learning, basketsync/learning, stockpile/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 9 | 1 | 9 | 25810 | 2275 |
| 43 | 11 | returnsdesk | taffrail | memory-placebo | completed | yes | 0 | expand-contract (live) | pricebook/learning, parcelroute/learning, basketsync/learning, stockpile/learning, ledgerpost/learning | - | - | yes | 2 | 0 | 2 | 4973 | 367 |
| 44 | 11 | returnsdesk | taffrail | memory-disabled | completed | yes | 0 | expand-contract (live) | - | - | - | yes | 2 | 0 | 2 | 2129 | 264 |
| 45 | 11 | returnsdesk | taffrail | memory-enabled | completed | yes | 0 | expand-contract (live) | pricebook/learning, parcelroute/learning, basketsync/learning, stockpile/learning, ledgerpost/learning | expand-contract | yes | yes | 2 | 0 | 2 | 5265 | 412 |
| 46 | 11 | returnsdesk | taffrail | mismatched-trait | completed | yes | 1 | online-copy > expand-contract (live) | parcelroute/learning, basketsync/learning, stockpile/learning, ledgerpost/learning | blue-green | no | no | 3 | 0 | 3 | 7366 | 547 |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 36 | 46460 | 7874 | 0.0858 |
| Evaluation | 209 | 470494 | 47305 | 0.7070 |
| **Total** | **245** | **516954** | **55179** | **0.7928** |

Prices: USD 1 per million input tokens and USD 5 per million output tokens (Anthropic API pricing, standard rates for Claude Haiku 4.5, input and output, no batch or prompt-cache discount). An estimate from list prices, not a bill.
Every response reported its token usage.
Latency is in the raw results only: it is a property of one machine and network, and is never gated.

## Limitations

- One model, one provider, one run, at temperature 0. Re-run before relying on a result.
- 12 instances in 6 clusters. One learning record serves both instances of a cluster, so the pairs are not independent and the sign test's p-value is optimistic.
- The trait is named in every task text (`cluster ...`) and by `describe_service`. Transfer here means finding and trusting a lesson keyed by a named trait, not discovering an unnamed one.
- Retrieval is the in-memory candidate source's word matching, not PostgreSQL full-text or vector search; ranks under another store would differ. The task texts were written once, before the first offline run, and are not tuned to rank the same-cluster record first.
- The working strategy reaches the block verbatim through the story 6.2 allowlist, as in the reuse experiment. A block can also name other strategies on failed attempts' Tried: lines and on other records; `Block named` and `Followed` read the first record's working line only.
- The mismatched-trait control is weaker than it looks. Under the bijection, its block can show the strategies of the other clusters, which a capable model can rule out to find the answer by elimination; and its records are the other clusters' lessons, which a model that follows the block pays for. A control that passes the gate can only move the verdict to `TransferBenefitNotAttributableToContent`, a conservative error: it can withhold a claim, never make one. `Harm` above reports how often it cost attempts.
- A synthetic task family with a simulated database; real tasks usually leave the answer partly inferable.
- The repository is public, and the trait mapping is in its source. Check a model's training cutoff against the registration date.

## Raw results

`transfer-anthropic-claude-haiku-4-5-2026-10-20.json`, next to this file: every distractor, learning run and trial above. It holds no prompt, no block
text, no message text and no key.
