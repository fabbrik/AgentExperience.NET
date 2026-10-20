# Transfer experiment: gemini / gemini-3.1-flash-lite / 2026-10-20

**Overall conclusion under the pre-registered rule: TransferInconclusive.** More instances were excluded than the pre-registration allows. No claim either way.

A lesson learned on one service has to be found by the library's own retrieval, in one shared store that also holds
other clusters' lessons and distractors from two other task families, and has to help on a different, unseen service
that shares only its cluster. Every database proxy in a cluster accepts one rollout strategy. Each unseen service runs
four times: memory disabled; the full store; the full store with the strategy withheld (the placebo); and the store
without its own cluster's lesson (the mismatched-trait control). Success is decided by the simulated database's state,
never by a model. See the Transfer section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | gemini, host `generativelanguage.googleapis.com` (the endpoint's host only) |
| Model requested | `gemini-3.1-flash-lite` |
| Model identities the provider reported | `gemini-3.1-flash-lite` |
| Settings | temperature 0, seed 20260926 not sent: this provider does not accept a seed |
| Pre-registration | `preregistration.transfer.json` at git blob `2d4407c34c60948f4f7155ed07f9bc15e33143a4` (14133 bytes), as amended; first registered at git blob `f568bcd660433abe6d472271b918ae804c0226d9`, registered in the commit that adds this file (parent c3dcb37), on 2026-10-10; no live results existed at registration; the offline scripted results are part of the design; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.transfer.json` |
| Amendments | 1 recorded, every one after the registered offline scripted results; 0 made after live results existed |
| Task set | `transfer-clusters@1`: 6 clusters, 12 evaluation instances, 24 distractors; trait digest `78d4d01e140a685d83cccfab968e10e7b9d7850ab4bdb9651ea245778243b938` |
| Shared store | one scope, `live-reuse/transfer-desk/shared`: 24 distractor records (12 cache flushes, 12 config rollouts) and 4 learning records; retrieval by `InMemoryExperienceCandidateSource` under `RetrievalPolicy.Default with { Timeout = 15 s }`, injection by `ExperienceContextProvider` (at most 8 records) |
| Attempt limits | learning 8, evaluation 6; at most 6 tool calls per attempt |
| Budget cap | 500 model calls, 4000000 tokens; used 134 calls, 147362 tokens |
| Earlier runs | 0 earlier ledger entries for this provider and model in `results/transfer-ledger.tsv`; the confirmatory run is the first complete one |
| Run status | complete: every learning run and every trial ran |
| Started (UTC) | 2026-10-20 16:23:44 |

## Verdict

The gate, read from the pre-registration and evaluated once per comparison:

```
mean(failed_attempts | treatment) < mean(failed_attempts | control) AND sign_test_p(failed_attempts: treatment < control) <= 0.05 AND success_rate(treatment) >= success_rate(control) AND sum(unauthorized_tool_requests | treatment) <= sum(unauthorized_tool_requests | control)
```

### Reference comparison: `memory-enabled` against `memory-disabled` -- Inconclusive

Not evaluated: 10 instance(s) were excluded, more than the pre-registered limit, or no pair remained.

### Content comparison: `memory-enabled` against `memory-placebo` -- Inconclusive (the same records with the strategy withheld: separates what the block says from the fact that a block is there)

Not evaluated: 10 instance(s) were excluded, more than the pre-registered limit, or no pair remained.

### Mismatched-trait control: `mismatched-trait` against `memory-disabled` -- Inconclusive (the same store without the service's own cluster's lesson; expected NoDemonstratedBenefit)

Not evaluated: 10 instance(s) were excluded, more than the pre-registered limit, or no pair remained.

Excluded instances: 0 (an evaluation trial errored); 1 (an evaluation trial errored); 2 (the learning run of cluster mizzen did not verify, so it ran no evaluation trial); 3 (the learning run of cluster bowsprit did not verify, so it ran no evaluation trial); 4 (an evaluation trial errored); 5 (an evaluation trial errored); 6 (an evaluation trial errored); 7 (an evaluation trial errored); 8 (the learning run of cluster mizzen did not verify, so it ran no evaluation trial); 9 (the learning run of cluster bowsprit did not verify, so it ran no evaluation trial) (the limit is 4).

### Harm: `mismatched-trait` against `memory-disabled` (reported, never gated)

Of 2 pairs, the mismatched-trait trial failed more attempts in 0, fewer in 0, and as many in 2. Mean failed attempts: 1.500 mismatched-trait, 1.500 memory-disabled.

## By instance: retrieval and failed attempts

What the library's retrieval put in each block, read out of the text the model was sent, and each condition's failed
attempts (the primary metric). *Rank* is the position of the same-cluster learning record in the memory-enabled block;
the mismatched-trait columns show the record its block ranked first and the strategy on that record's working line.
Never gated.

| Instance | Service | Cluster | Strategy | Same-cluster record injected | Rank | Records in block | Mismatched: first record | Mismatched: first strategy | Failed: disabled | Failed: enabled | Failed: placebo | Failed: mismatched | Enabled followed the block | Enabled followed the transferred lesson |
| ---: | --- | --- | --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| 0 | refundflow | halyard | online-copy | yes | 1 | 4 | pricebook/learning | expand-contract | 1 | 1 | Errored | 2 | no | no |
| 1 | wishlist | keel | in-place | yes | 1 | 4 | parcelroute/learning | blue-green | Errored | 2 | Errored | 2 | no | no |
| 4 | taxcalc | capstan | blue-green | yes | 1 | 4 | basketsync/learning | in-place | 3 | 3 | 3 | Errored | no | no |
| 5 | searchindex | taffrail | expand-contract | yes | 1 | 4 | parcelroute/learning | blue-green | 0 | 0 | 0 | Errored | yes | yes |
| 6 | loyaltyclub | halyard | online-copy | yes | 1 | 4 | pricebook/learning | expand-contract | 1 | 1 | 1 | Errored | no | no |
| 7 | authgate | keel | in-place | yes | 1 | 4 | parcelroute/learning | blue-green | 3 | Errored | 4 | 2 | no | no |
| 10 | shipquote | capstan | blue-green | yes | 1 | 4 | basketsync/learning | in-place | 3 | 4 | 3 | 3 | no | no |
| 11 | returnsdesk | taffrail | expand-contract | yes | 1 | 4 | parcelroute/learning | blue-green | 0 | 2 | 0 | 0 | no | no |

The same-cluster record was in the memory-enabled block for 8 of 8 instances, and ranked first for 8.

## Metrics by condition

Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.

| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | First-try success | Followed the block | Followed the transferred lesson | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 8 | 7 | 1.000 (7/7) | 1.571 (1.397) | 0.286 (2/7) | - | 0.286 (2/7) | 3.571 | 0 | 30 | 16337 | 1071 | 0.0057 |
| memory-enabled | 8 | 7 | 1.000 (7/7) | 1.857 (1.345) | 0.143 (1/7) | 0.143 (1/7) | 0.143 (1/7) | 3.857 | 0 | 28 | 44788 | 1008 | 0.0127 |
| memory-placebo | 8 | 6 | 1.000 (6/6) | 1.833 (1.722) | 0.333 (2/6) | - | 0.333 (2/6) | 3.833 | 0 | 27 | 39318 | 918 | 0.0112 |
| mismatched-trait | 8 | 5 | 1.000 (5/5) | 1.800 (1.095) | 0.200 (1/5) | 0.400 (2/5) | 0.200 (1/5) | 3.800 | 0 | 24 | 28984 | 736 | 0.0084 |

## Learning phase

One memory-disabled run per cluster, on its learning service. A run that verified was finalized into the shared store; one that
did not stored nothing, and its cluster's two evaluation instances are excluded and run no trial.

| Seq | Cluster | Service | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |
| ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |
| 1 | halyard | ledgerpost | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | online-copy | 3 | 1618 | 109 |
| 2 | keel | basketsync | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | in-place | 4 | 2296 | 162 |
| 3 | mizzen | couponvault | batched-backfill | Errored: ClientResultException (HTTP 503) | - | - | expand-contract > online-copy > in-place | - | 5 | 2301 | 152 |
| 4 | bowsprit | stockpile | shadow-table | Errored: ClientResultException (HTTP 503) | - | - | expand-contract | - | 3 | 1012 | 63 |
| 5 | capstan | parcelroute | blue-green | completed | yes | 4 | online-copy > expand-contract > in-place > batched-backfill > blue-green (live) | blue-green | 6 | 3801 | 249 |
| 6 | taffrail | pricebook | expand-contract | completed | yes | 2 | in-place > online-copy > expand-contract (live) | expand-contract | 4 | 2284 | 155 |

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
| 7 | 0 | refundflow | halyard | memory-disabled | completed | yes | 1 | expand-contract > online-copy (live) | - | - | - | no | 3 | 0 | 3 | 1597 | 105 |
| 8 | 0 | refundflow | halyard | memory-enabled | completed | yes | 1 | expand-contract > online-copy (live) | ledgerpost/learning, pricebook/learning, parcelroute/learning, basketsync/learning | online-copy | no | no | 3 | 0 | 3 | 4864 | 105 |
| 9 | 0 | refundflow | halyard | mismatched-trait | completed | yes | 2 | expand-contract > in-place > online-copy (live) | pricebook/learning, parcelroute/learning, basketsync/learning | expand-contract | yes | no | 4 | 0 | 4 | 5706 | 149 |
| 10 | 0 | refundflow | halyard | memory-placebo | Errored: ClientResultException (HTTP 503) | - | - | expand-contract | ledgerpost/learning, pricebook/learning, parcelroute/learning, basketsync/learning | - | - | no | 2 | 0 | 3 | 3003 | 61 |
| 11 | 1 | wishlist | keel | memory-enabled | completed | yes | 2 | expand-contract > online-copy > in-place (live) | basketsync/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | in-place | no | no | 4 | 0 | 4 | 6610 | 149 |
| 12 | 1 | wishlist | keel | mismatched-trait | completed | yes | 2 | expand-contract > online-copy > in-place (live) | parcelroute/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 4 | 0 | 4 | 5562 | 149 |
| 13 | 1 | wishlist | keel | memory-placebo | Errored: ClientResultException (HTTP 503) | - | - | - | basketsync/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | - | - | no | 0 | 0 | 1 | 0 | 0 |
| 14 | 1 | wishlist | keel | memory-disabled | Errored: ClientResultException (HTTP 503) | - | - | online-copy > expand-contract > batched-backfill | - | - | - | no | 4 | 0 | 5 | 2254 | 151 |
| 15 | 4 | taxcalc | capstan | memory-disabled | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | - | - | - | no | 5 | 0 | 5 | 2949 | 193 |
| 16 | 4 | taxcalc | capstan | memory-enabled | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | parcelroute/learning, basketsync/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 5 | 0 | 5 | 8394 | 193 |
| 17 | 4 | taxcalc | capstan | mismatched-trait | Errored: ClientResultException (HTTP 503) | - | - | - | basketsync/learning, pricebook/learning, ledgerpost/learning | in-place | no | no | 0 | 0 | 2 | 1259 | 17 |
| 18 | 4 | taxcalc | capstan | memory-placebo | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | parcelroute/learning, basketsync/learning, pricebook/learning, ledgerpost/learning | - | - | no | 5 | 0 | 5 | 7964 | 193 |
| 19 | 5 | searchindex | taffrail | memory-enabled | completed | yes | 0 | expand-contract (live) | pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | expand-contract | yes | yes | 2 | 0 | 2 | 3177 | 62 |
| 20 | 5 | searchindex | taffrail | mismatched-trait | Errored: ClientResultException (HTTP 503) | - | - | - | parcelroute/learning, ledgerpost/learning, basketsync/learning | blue-green | no | no | 0 | 0 | 2 | 1285 | 17 |
| 21 | 5 | searchindex | taffrail | memory-placebo | completed | yes | 0 | expand-contract (live) | pricebook/learning, parcelroute/learning, ledgerpost/learning, basketsync/learning | - | - | yes | 2 | 0 | 2 | 3005 | 62 |
| 22 | 5 | searchindex | taffrail | memory-disabled | completed | yes | 0 | expand-contract (live) | - | - | - | yes | 2 | 0 | 2 | 999 | 62 |
| 23 | 6 | loyaltyclub | halyard | mismatched-trait | Errored: ClientResultException (HTTP 503) | - | - | - | pricebook/learning, parcelroute/learning, basketsync/learning | expand-contract | no | no | 0 | 0 | 1 | 0 | 0 |
| 24 | 6 | loyaltyclub | halyard | memory-placebo | completed | yes | 1 | expand-contract > online-copy (live) | ledgerpost/learning, pricebook/learning, parcelroute/learning, basketsync/learning | - | - | no | 3 | 0 | 3 | 4626 | 110 |
| 25 | 6 | loyaltyclub | halyard | memory-disabled | completed | yes | 1 | expand-contract > online-copy (live) | - | - | - | no | 3 | 0 | 3 | 1617 | 110 |
| 26 | 6 | loyaltyclub | halyard | memory-enabled | completed | yes | 1 | expand-contract > online-copy (live) | ledgerpost/learning, pricebook/learning, parcelroute/learning, basketsync/learning | online-copy | no | no | 3 | 0 | 3 | 4884 | 110 |
| 27 | 7 | authgate | keel | memory-placebo | completed | yes | 4 | online-copy > expand-contract > blue-green > shadow-table > in-place (live) | basketsync/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | - | - | no | 6 | 0 | 6 | 9749 | 237 |
| 28 | 7 | authgate | keel | memory-disabled | completed | yes | 3 | online-copy > expand-contract > batched-backfill > in-place (live) | - | - | - | no | 5 | 0 | 5 | 2971 | 195 |
| 29 | 7 | authgate | keel | memory-enabled | Errored: ClientResultException (HTTP 503) | - | - | - | basketsync/learning, parcelroute/learning, pricebook/learning, ledgerpost/learning | in-place | no | no | 0 | 0 | 1 | 0 | 0 |
| 30 | 7 | authgate | keel | mismatched-trait | completed | yes | 2 | expand-contract > online-copy > in-place (live) | parcelroute/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 4 | 0 | 4 | 5568 | 149 |
| 31 | 10 | shipquote | capstan | mismatched-trait | completed | yes | 3 | in-place > online-copy > expand-contract > blue-green (live) | basketsync/learning, pricebook/learning, ledgerpost/learning | in-place | yes | no | 5 | 0 | 5 | 6947 | 193 |
| 32 | 10 | shipquote | capstan | memory-placebo | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | parcelroute/learning, basketsync/learning, pricebook/learning, ledgerpost/learning | - | - | no | 5 | 0 | 5 | 7962 | 193 |
| 33 | 10 | shipquote | capstan | memory-disabled | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | - | - | - | no | 5 | 0 | 5 | 2947 | 193 |
| 34 | 10 | shipquote | capstan | memory-enabled | completed | yes | 4 | in-place > online-copy > expand-contract > shadow-table > blue-green (live) | parcelroute/learning, basketsync/learning, pricebook/learning, ledgerpost/learning | blue-green | no | no | 6 | 0 | 6 | 10238 | 237 |
| 35 | 11 | returnsdesk | taffrail | memory-placebo | completed | yes | 0 | expand-contract (live) | pricebook/learning, parcelroute/learning, basketsync/learning, ledgerpost/learning | - | - | yes | 2 | 0 | 2 | 3009 | 62 |
| 36 | 11 | returnsdesk | taffrail | memory-disabled | completed | yes | 0 | expand-contract (live) | - | - | - | yes | 2 | 0 | 2 | 1003 | 62 |
| 37 | 11 | returnsdesk | taffrail | memory-enabled | completed | yes | 2 | in-place > online-copy > expand-contract (live) | pricebook/learning, parcelroute/learning, basketsync/learning, ledgerpost/learning | expand-contract | no | no | 4 | 0 | 4 | 6621 | 152 |
| 38 | 11 | returnsdesk | taffrail | mismatched-trait | completed | yes | 0 | expand-contract (live) | parcelroute/learning, basketsync/learning, ledgerpost/learning | blue-green | no | yes | 2 | 0 | 2 | 2657 | 62 |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 25 | 13312 | 890 | 0.0047 |
| Evaluation | 109 | 129427 | 3733 | 0.0380 |
| **Total** | **134** | **142739** | **4623** | **0.0426** |

Prices: USD 0.25 per million input tokens and USD 1.5 per million output tokens (ai.google.dev/gemini-api/docs/pricing, Standard paid tier, text input and output (thinking included), checked 2026-09-26). An estimate from list prices, not a bill.
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

`transfer-gemini-gemini-3.1-flash-lite-2026-10-20.json`, next to this file: every distractor, learning run and trial above. It holds no prompt, no block
text, no message text and no key.
