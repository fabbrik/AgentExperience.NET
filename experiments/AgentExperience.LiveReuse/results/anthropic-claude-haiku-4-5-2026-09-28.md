# Live reuse experiment: anthropic / claude-haiku-4-5 / 2026-09-28

> **EXPLORATORY RUN.** `anthropic` / `claude-haiku-4-5` is not a provider and model the pre-registration registers
> (`registeredModels` in `preregistration.json`), and its rule is that "a run of any other model is exploratory and says so".
> The design, the gate and the verdict are exactly the pre-registered ones, but this run is not a candidate for the confirmatory result.

**Overall conclusion under the pre-registered rule: ReuseBenefitAttributableToContent.** With its own verified experience injected, the model needed fewer failed attempts than with memory disabled and than with the same block with its strategy withheld, by the pre-registered test, and stale experience gave it no such benefit. The model acted on the Approach: line; this does not show that the block's presence alone contributed nothing.

Story 4.4's reuse methodology run against a real model: the model first works two learning tickets per service with memory
disabled; the library captures, verifies, reflects on and stores each verified run; the model then works a different ticket
on each service four times -- with memory disabled, with its own earlier experience injected as a Historical Reference, with
the same block but its strategy withheld (the placebo), and with genuine but stale experience injected (the negative control).
Success is decided by the simulated database's state, never by a model. See `experiments/AgentExperience.LiveReuse/README.md`
for the design and how to read this report.

## Run

| | |
| --- | --- |
| Provider | anthropic, host `api.anthropic.com` (the endpoint's host only) |
| Model requested | `claude-haiku-4-5` |
| Registration | **EXPLORATORY**: this provider and model are not in the pre-registration's `registeredModels`; reported, never confirmatory |
| Model identities the provider reported | `claude-haiku-4-5-20251001` |
| Settings | temperature 0, seed 20260926 not sent: this provider does not accept a seed, max output tokens 4096 per call (the provider requires a cap) |
| Pre-registration | `preregistration.json` at git blob `64a06da847b2311c30fff8c0e8c576b6d8198678` (12409 bytes), registered on 2026-09-26 against commit `981347f`; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.json` |
| Amendments | 1 recorded, 0 made after results existed (listed below) |
| Task set | `live-reuse-migrations@1`, 12 instances, hidden-assignment digest `2850ebc91b6ae0b1aa2dbdc1273d80f117582861db1c54abcb549e4939987619` |
| Attempt limits | learning 8, evaluation 6; at most 6 tool calls per attempt |
| Budget cap | 600 model calls, 2000000 tokens; used 367 calls, 614261 tokens |
| Earlier runs | 2 earlier ledger entries for this provider and model in `results/ledger.tsv`; this model is not registered, so no run of it is a candidate for the confirmatory result |
| Run status | complete: every learning run and every trial ran |
| Started (UTC) | 2026-09-28 04:38:56 |

Amendment 1 (2026-09-27, made before any result existed): The seed is sent only to providers that accept it. Gemini calls carry temperature 0 and no seed; Azure calls are unchanged. Why: Gemini's OpenAI-compatible endpoint rejects a seed field with HTTP 400 (Unknown name "seed"). The first live attempt (ledger run 20260927T190451Z) errored on its first two calls for this reason and was stopped by hand; no trial completed. Effect on published numbers: None: no trial had completed, and no verdict, metric or comparison was computed.

## Verdict

The gate, read from the pre-registration and evaluated once per comparison:

```
mean(failed_attempts | treatment) < mean(failed_attempts | control) AND sign_test_p(failed_attempts: treatment < control) <= 0.05 AND success_rate(treatment) >= success_rate(control) AND sum(unauthorized_tool_requests | treatment) <= sum(unauthorized_tool_requests | control)
```

### Reference comparison: `memory-enabled` against `memory-disabled` -- BenefitDemonstrated

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)` | yes | 0.833 against 2.750 |
| `sign_test_p(failed_attempts: memory-enabled < memory-disabled) <= 0.05` | yes | p = 0.0020 (9 fewer, 0 more, 3 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-disabled)` | yes | 0.917 against 0.750 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 1 against 5 |

### Content comparison: `memory-enabled` against `memory-placebo` -- BenefitDemonstrated (the same record with its strategy withheld: separates what the block says from the fact that a block is there)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-placebo)` | yes | 0.833 against 2.417 |
| `sign_test_p(failed_attempts: memory-enabled < memory-placebo) <= 0.05` | yes | p = 0.0020 (9 fewer, 0 more, 3 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-placebo)` | yes | 0.917 against 0.833 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-placebo)` | yes | 1 against 3 |

### Negative control: `negative-control` against `memory-disabled` -- NoDemonstratedBenefit (stale experience; any model that follows the block pays for it, so this catches a block-presence effect only when the content comparison does not)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | negative-control) < mean(failed_attempts | memory-disabled)` | yes | 2.667 against 2.750 |
| `sign_test_p(failed_attempts: negative-control < memory-disabled) <= 0.05` | **no** | p = 0.8125 (2 fewer, 3 more, 7 tied, of 12 pairs) |
| `success_rate(negative-control) >= success_rate(memory-disabled)` | yes | 0.833 against 0.750 |
| `sum(unauthorized_tool_requests | negative-control) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 4 against 5 |

Excluded instances: none (the limit is 3).

## Metrics by condition

Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.

Failed attempts split three ways: *rejected* (a valid strategy the database refused), *no change* (an attempt that made no
`apply_migration` call), *other* (an unknown service, migration or strategy).

| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | Rejected / no change / other | First-try success | Followed the block | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Mean latency (ms) | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 12 | 12 | 0.750 (9/12) | 2.750 (2.261) | 27 / 6 / 0 | 0.167 (2/12) | - | 5.500 | 5 | 63 | 77041 | 12494 | 12742 | 0.1395 |
| memory-enabled | 12 | 12 | 0.917 (11/12) | 0.833 (1.749) | 8 / 2 / 0 | 0.667 (8/12) | 0.727 (8/11) | 2.917 | 1 | 35 | 58711 | 6524 | 6893 | 0.0913 |
| memory-placebo | 12 | 12 | 0.833 (10/12) | 2.417 (1.881) | 27 / 2 / 0 | 0.083 (1/12) | - | 5.333 | 3 | 55 | 97317 | 12858 | 12816 | 0.1616 |
| negative-control | 12 | 12 | 0.833 (10/12) | 2.667 (2.015) | 28 / 4 / 0 | 0.083 (1/12) | 0.571 (4/7) | 5.083 | 4 | 59 | 95815 | 12795 | 13244 | 0.1598 |

## Learning phase

`learn-current` runs against a database that accepts the strategy the evaluation ticket needs; their records feed
`memory-enabled` and `memory-placebo`. `learn-stale` runs against one that accepts a different strategy; their records feed
`negative-control`. A run that did not verify stored nothing, and its instance's trial ran with nothing to retrieve.

| Seq | Instance | Service | Run | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |
| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |
| 1 | 0 | orders-api | learn-current | online-copy | completed | yes | 0 | online-copy (live) | online-copy | 2 | 2123 | 261 |
| 2 | 0 | orders-api | learn-stale | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | expand-contract | 3 | 3287 | 435 |
| 3 | 1 | billing-ledger | learn-current | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | in-place > blue-green > shadow-table > batched-backfill | 5 | 5741 | 1063 |
| 4 | 1 | billing-ledger | learn-stale | batched-backfill | completed | no | 8 | expand-contract > online-copy > in-place > blue-green | - | 12 | 17835 | 3062 |
| 5 | 2 | inventory-sync | learn-current | batched-backfill | completed | yes | 5 | expand-contract > online-copy > blue-green > in-place > batched-backfill (live) | batched-backfill | 9 | 11791 | 1926 |
| 6 | 2 | inventory-sync | learn-stale | shadow-table | completed | no | 8 | expand-contract > online-copy > blue-green > in-place > batched-backfill | - | 11 | 15278 | 2496 |
| 7 | 3 | customer-profile | learn-current | shadow-table | completed | no | 8 | expand-contract > online-copy > in-place | - | 12 | 17682 | 2997 |
| 8 | 3 | customer-profile | learn-stale | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | in-place > blue-green > shadow-table > batched-backfill | 5 | 5680 | 1054 |
| 9 | 4 | shipment-tracker | learn-current | blue-green | completed | yes | 2 | online-copy > expand-contract > blue-green (live) | blue-green > shadow-table > batched-backfill | 4 | 4505 | 817 |
| 10 | 4 | shipment-tracker | learn-stale | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | in-place | 4 | 4505 | 673 |
| 11 | 5 | payments-gateway | learn-current | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 2118 | 275 |
| 12 | 5 | payments-gateway | learn-stale | batched-backfill | completed | no | 8 | expand-contract > online-copy > blue-green > in-place | - | 12 | 17859 | 3108 |
| 13 | 6 | catalog-search | learn-current | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | online-copy | 3 | 3250 | 465 |
| 14 | 6 | catalog-search | learn-stale | shadow-table | completed | no | 8 | expand-contract > online-copy > in-place > blue-green | - | 11 | 15727 | 2804 |
| 15 | 7 | returns-portal | learn-current | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | in-place > blue-green > shadow-table > batched-backfill | 5 | 5712 | 1063 |
| 16 | 7 | returns-portal | learn-stale | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 2102 | 273 |
| 17 | 8 | loyalty-points | learn-current | batched-backfill | completed | yes | 7 | online-copy > expand-contract > in-place > blue-green > shadow-table > batched-backfill (live) | batched-backfill | 15 | 23087 | 3548 |
| 18 | 8 | loyalty-points | learn-stale | online-copy | completed | yes | 0 | online-copy (live) | online-copy | 2 | 2106 | 253 |
| 19 | 9 | notifications-hub | learn-current | shadow-table | completed | yes | 4 | expand-contract > online-copy > blue-green > shadow-table (live) | shadow-table | 8 | 10376 | 1770 |
| 20 | 9 | notifications-hub | learn-stale | blue-green | completed | yes | 3 | expand-contract > online-copy > in-place > blue-green (live) | blue-green | 9 | 11982 | 1921 |
| 21 | 10 | pricing-engine | learn-current | blue-green | completed | yes | 2 | expand-contract > online-copy > blue-green (live) | blue-green | 4 | 4450 | 613 |
| 22 | 10 | pricing-engine | learn-stale | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | online-copy | 3 | 3247 | 459 |
| 23 | 11 | fraud-scoring | learn-current | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 2103 | 274 |
| 24 | 11 | fraud-scoring | learn-stale | blue-green | completed | no | 8 | expand-contract > online-copy > in-place > batched-backfill > shadow-table | - | 10 | 13997 | 2553 |

## Per-trial results

Every evaluation trial, in the order it ran. None is ever dropped. `Block named` is the strategy on the Approach: line of the
Historical Reference the model was actually shown, read out of the text it was sent.

| Seq | Instance | Service | Condition | Accepted | Status | Live | Failed attempts | Strategies tried | Block named | Followed | Tool calls | Unauthorized | Model calls | Tokens in | Tokens out | Latency (ms) |
| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 25 | 0 | orders-api | memory-disabled | online-copy | completed | yes | 0 | online-copy (live) | - | - | 2 | 0 | 2 | 2105 | 264 | 3795 |
| 26 | 0 | orders-api | memory-enabled | online-copy | completed | yes | 0 | online-copy (live) | online-copy | yes | 2 | 0 | 2 | 3551 | 311 | 3947 |
| 27 | 0 | orders-api | negative-control | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | expand-contract | yes | 3 | 0 | 3 | 5415 | 512 | 6432 |
| 28 | 0 | orders-api | memory-placebo | online-copy | completed | yes | 0 | online-copy (live) | - | - | 2 | 0 | 2 | 3499 | 328 | 4179 |
| 29 | 1 | billing-ledger | memory-enabled | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | in-place > blue-green > shadow-table > batched-backfill | no | 4 | 0 | 4 | 7515 | 796 | 9240 |
| 30 | 1 | billing-ledger | negative-control | in-place | completed | yes | 5 | online-copy > expand-contract > blue-green > batched-backfill > in-place (live) | - | - | 8 | 1 | 8 | 10171 | 1686 | 21205 |
| 31 | 1 | billing-ledger | memory-placebo | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | - | - | 7 | 0 | 4 | 7315 | 1192 | 13357 |
| 32 | 1 | billing-ledger | memory-disabled | in-place | completed | yes | 4 | online-copy > expand-contract > blue-green > batched-backfill > in-place (live) | - | - | 8 | 1 | 7 | 8637 | 1461 | 17437 |
| 33 | 2 | inventory-sync | negative-control | batched-backfill | completed | no | 6 | expand-contract > online-copy > blue-green > in-place | - | - | 8 | 1 | 8 | 10274 | 1894 | 23315 |
| 34 | 2 | inventory-sync | memory-placebo | batched-backfill | completed | yes | 3 | online-copy > expand-contract > blue-green > batched-backfill (live) | - | - | 5 | 0 | 5 | 9222 | 1137 | 13457 |
| 35 | 2 | inventory-sync | memory-disabled | batched-backfill | completed | no | 6 | expand-contract > online-copy > blue-green > in-place | - | - | 9 | 1 | 9 | 11915 | 2046 | 25076 |
| 36 | 2 | inventory-sync | memory-enabled | batched-backfill | completed | yes | 0 | batched-backfill (live) | batched-backfill | yes | 2 | 0 | 2 | 3559 | 350 | 4683 |
| 37 | 3 | customer-profile | memory-placebo | shadow-table | completed | no | 6 | expand-contract > online-copy > blue-green > in-place > batched-backfill | - | - | 9 | 1 | 8 | 10211 | 1756 | 20650 |
| 38 | 3 | customer-profile | memory-disabled | shadow-table | completed | no | 6 | expand-contract > online-copy > blue-green > in-place | - | - | 9 | 1 | 9 | 11829 | 2042 | 25110 |
| 39 | 3 | customer-profile | memory-enabled | shadow-table | completed | no | 6 | expand-contract > online-copy > blue-green > in-place | - | - | 9 | 1 | 9 | 11829 | 2031 | 24876 |
| 40 | 3 | customer-profile | negative-control | shadow-table | completed | yes | 3 | expand-contract > in-place > blue-green > shadow-table (live) | in-place > blue-green > shadow-table > batched-backfill | no | 6 | 0 | 5 | 9487 | 957 | 11359 |
| 41 | 4 | shipment-tracker | memory-disabled | blue-green | completed | yes | 2 | online-copy > expand-contract > blue-green (live) | - | - | 4 | 0 | 4 | 4485 | 624 | 7591 |
| 42 | 4 | shipment-tracker | memory-enabled | blue-green | completed | yes | 0 | blue-green (live) | blue-green > shadow-table > batched-backfill | yes | 2 | 0 | 2 | 3585 | 290 | 3782 |
| 43 | 4 | shipment-tracker | negative-control | blue-green | completed | yes | 3 | online-copy > expand-contract > in-place > blue-green (live) | in-place | no | 6 | 1 | 6 | 11410 | 1403 | 17205 |
| 44 | 4 | shipment-tracker | memory-placebo | blue-green | completed | yes | 2 | online-copy > expand-contract > blue-green (live) | - | - | 7 | 0 | 4 | 7261 | 1040 | 11473 |
| 45 | 5 | payments-gateway | memory-enabled | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | yes | 2 | 0 | 2 | 3548 | 385 | 4646 |
| 46 | 5 | payments-gateway | negative-control | expand-contract | completed | yes | 0 | expand-contract (live) | - | - | 2 | 0 | 2 | 2110 | 259 | 3657 |
| 47 | 5 | payments-gateway | memory-placebo | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | - | - | 3 | 0 | 3 | 5344 | 624 | 8420 |
| 48 | 5 | payments-gateway | memory-disabled | expand-contract | completed | yes | 0 | expand-contract (live) | - | - | 2 | 0 | 2 | 2110 | 259 | 3669 |
| 49 | 6 | catalog-search | negative-control | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | - | - | 3 | 0 | 3 | 3267 | 414 | 5596 |
| 50 | 6 | catalog-search | memory-placebo | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | - | - | 3 | 0 | 3 | 5343 | 544 | 6535 |
| 51 | 6 | catalog-search | memory-disabled | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | - | - | 3 | 0 | 3 | 3267 | 414 | 5583 |
| 52 | 6 | catalog-search | memory-enabled | online-copy | completed | yes | 0 | online-copy (live) | online-copy | yes | 2 | 0 | 2 | 3548 | 292 | 3852 |
| 53 | 7 | returns-portal | memory-placebo | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | - | - | 4 | 0 | 4 | 7270 | 773 | 9529 |
| 54 | 7 | returns-portal | memory-disabled | in-place | completed | yes | 3 | online-copy > expand-contract > blue-green > in-place (live) | - | - | 9 | 1 | 7 | 8581 | 1404 | 16129 |
| 55 | 7 | returns-portal | memory-enabled | in-place | completed | yes | 1 | online-copy > in-place (live) | in-place > blue-green > shadow-table > batched-backfill | no | 3 | 0 | 3 | 5512 | 500 | 6348 |
| 56 | 7 | returns-portal | negative-control | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | expand-contract | yes | 4 | 0 | 4 | 7350 | 934 | 12509 |
| 57 | 8 | loyalty-points | memory-disabled | batched-backfill | completed | yes | 2 | expand-contract > online-copy > batched-backfill (live) | - | - | 4 | 0 | 4 | 4462 | 672 | 8459 |
| 58 | 8 | loyalty-points | memory-enabled | batched-backfill | completed | yes | 0 | batched-backfill (live) | batched-backfill | yes | 2 | 0 | 2 | 3548 | 314 | 4273 |
| 59 | 8 | loyalty-points | negative-control | batched-backfill | completed | yes | 2 | online-copy > expand-contract > batched-backfill (live) | online-copy | yes | 4 | 0 | 4 | 7330 | 871 | 10979 |
| 60 | 8 | loyalty-points | memory-placebo | batched-backfill | completed | yes | 3 | expand-contract > online-copy > in-place > batched-backfill (live) | - | - | 6 | 0 | 5 | 9187 | 1178 | 14114 |
| 61 | 9 | notifications-hub | memory-enabled | shadow-table | completed | yes | 1 | online-copy > shadow-table (live) | shadow-table | no | 3 | 0 | 3 | 5423 | 635 | 8490 |
| 62 | 9 | notifications-hub | negative-control | shadow-table | completed | no | 6 | expand-contract > blue-green > online-copy > in-place > batched-backfill | blue-green | no | 10 | 1 | 9 | 18398 | 2460 | 29555 |
| 63 | 9 | notifications-hub | memory-placebo | shadow-table | completed | no | 6 | online-copy > expand-contract > batched-backfill > in-place > blue-green | - | - | 10 | 1 | 9 | 18126 | 2375 | 28829 |
| 64 | 9 | notifications-hub | memory-disabled | shadow-table | completed | no | 6 | online-copy > expand-contract > batched-backfill > in-place | - | - | 9 | 1 | 9 | 11887 | 2080 | 24252 |
| 65 | 10 | pricing-engine | negative-control | blue-green | completed | yes | 2 | online-copy > expand-contract > blue-green (live) | online-copy | yes | 4 | 0 | 4 | 7339 | 953 | 11280 |
| 66 | 10 | pricing-engine | memory-placebo | blue-green | completed | yes | 2 | expand-contract > online-copy > blue-green (live) | - | - | 5 | 1 | 5 | 9211 | 1310 | 15634 |
| 67 | 10 | pricing-engine | memory-disabled | blue-green | completed | yes | 2 | expand-contract > online-copy > blue-green (live) | - | - | 4 | 0 | 4 | 4499 | 776 | 9983 |
| 68 | 10 | pricing-engine | memory-enabled | blue-green | completed | yes | 0 | blue-green (live) | blue-green | yes | 2 | 0 | 2 | 3556 | 326 | 4438 |
| 69 | 11 | fraud-scoring | memory-placebo | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | - | - | 3 | 0 | 3 | 5328 | 601 | 7609 |
| 70 | 11 | fraud-scoring | memory-disabled | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | - | - | 3 | 0 | 3 | 3264 | 452 | 5825 |
| 71 | 11 | fraud-scoring | memory-enabled | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | yes | 2 | 0 | 2 | 3537 | 294 | 4138 |
| 72 | 11 | fraud-scoring | negative-control | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | - | - | 3 | 0 | 3 | 3264 | 452 | 5843 |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 155 | 206543 | 34163 | 0.3774 |
| Evaluation | 212 | 328884 | 44671 | 0.5522 |
| **Total** | **367** | **535427** | **78834** | **0.9296** |

Prices: USD 1 per million input tokens and USD 5 per million output tokens (Anthropic API pricing, standard rates for Claude Haiku 4.5, input and output, no batch or prompt-cache discount). An estimate from list prices, not a bill.
Every response reported its token usage.

## Limitations

- One model, one provider, one run. Temperature 0 is requested and no seed is sent (this provider does not accept one), so nothing asks for determinism beyond temperature, and a model version change can change every number. Re-run before relying on a result.
- 12 instances. The sign test can detect only a large, consistent effect; NoDemonstratedBenefit here is not evidence that there is no smaller effect.
- The sign test treats the instance pairs as independent. Each hidden strategy is the answer for two services, and a near-deterministic model with a fixed search order will tend to score both alike, so the effective sample is smaller than the instance count and the p-value is optimistic. The inference is over this fixed, pre-committed assignment, not over tasks in general.
- A synthetic task family: a simulated database whose accepted rollout strategy is a stand-in for tacit, environment-specific knowledge. Real tasks usually leave the answer partly inferable, which would shrink the gap between the conditions.
- The working strategy reaches the block verbatim, on the Approach: line, through the story 6.2 ApproachArguments allowlist. This measures whether a model acts on a Historical Reference labelled as untrusted reference material, not whether it can generalize from vaguer lessons.
- The Approach: line lists every call of the stored run's final attempt, in order, and does not mark which one succeeded (tool results are never injected). A redundant extra call a model makes after the migration went live therefore appears on the line after the working strategy; the harness records the full sequence (`Stored record names`, `Block named`, joined with " > "), checks the block against it, and scores `Followed` against the first strategy on the line.
- Retrieval is not under test: records are scoped per service and each scope holds one record, so a memory-enabled trial always retrieves its own service's record. Retrieval quality over a crowded store is a separate question.
- The negative control's record is stale (verified against a database that has since changed), not irrelevant; an irrelevant record is a different control and was not run.
- Tokens are as the provider reports them; whether thinking tokens are included in the output count is the provider's accounting. Cost is an estimate from list prices. Latency includes the network and is never gated.
- The repository is public, and the hidden assignment is in its source. A model trained on it after the registration date could know the answers; check the model's training cutoff against the registration date.

## Raw results

`anthropic-claude-haiku-4-5-2026-09-28.json`, next to this file: every learning run and trial above, with its change sequence and usage. It holds no
prompt, no message text and no key.
