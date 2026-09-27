# Live reuse experiment: gemini / gemini-3.1-flash-lite / 2026-09-27

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
| Provider | gemini, host `generativelanguage.googleapis.com` (the endpoint's host only) |
| Model requested | `gemini-3.1-flash-lite` |
| Model identities the provider reported | `gemini-3.1-flash-lite` |
| Settings | temperature 0, seed 20260926 not sent: this provider rejects the field (amendment 1) |
| Pre-registration | `preregistration.json` at git blob `64a06da847b2311c30fff8c0e8c576b6d8198678` (12409 bytes), registered on 2026-09-26 against commit `981347f`; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.json` |
| Amendments | 1 recorded, 0 made after results existed (listed below) |
| Task set | `live-reuse-migrations@1`, 12 instances, hidden-assignment digest `2850ebc91b6ae0b1aa2dbdc1273d80f117582861db1c54abcb549e4939987619` |
| Attempt limits | learning 8, evaluation 6; at most 6 tool calls per attempt |
| Budget cap | 600 model calls, 2000000 tokens; used 288 calls, 276929 tokens |
| Earlier runs | 1 earlier ledger entry for this provider and model in `results/ledger.tsv`; the pre-registered confirmatory run is the first complete one |
| Run status | complete: every learning run and every trial ran |
| Started (UTC) | 2026-09-27 19:07:14 |

Amendment 1 (2026-09-27, made before any result existed): The seed is sent only to providers that accept it. Gemini calls carry temperature 0 and no seed; Azure calls are unchanged. Why: Gemini's OpenAI-compatible endpoint rejects a seed field with HTTP 400 (Unknown name "seed"). The first live attempt (ledger run 20260927T190451Z) errored on its first two calls for this reason and was stopped by hand; no trial completed. Effect on published numbers: None: no trial had completed, and no verdict, metric or comparison was computed.

## Verdict

The gate, read from the pre-registration and evaluated once per comparison:

```
mean(failed_attempts | treatment) < mean(failed_attempts | control) AND sign_test_p(failed_attempts: treatment < control) <= 0.05 AND success_rate(treatment) >= success_rate(control) AND sum(unauthorized_tool_requests | treatment) <= sum(unauthorized_tool_requests | control)
```

### Reference comparison: `memory-enabled` against `memory-disabled` -- BenefitDemonstrated

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)` | yes | 0.000 against 2.167 |
| `sign_test_p(failed_attempts: memory-enabled < memory-disabled) <= 0.05` | yes | p = 0.0010 (10 fewer, 0 more, 2 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-disabled)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-disabled)` | yes | 0 against 0 |

### Content comparison: `memory-enabled` against `memory-placebo` -- BenefitDemonstrated (the same record with its strategy withheld: separates what the block says from the fact that a block is there)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-placebo)` | yes | 0.000 against 2.417 |
| `sign_test_p(failed_attempts: memory-enabled < memory-placebo) <= 0.05` | yes | p = 0.0005 (11 fewer, 0 more, 1 tied, of 12 pairs) |
| `success_rate(memory-enabled) >= success_rate(memory-placebo)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | memory-enabled) <= sum(unauthorized_tool_requests | memory-placebo)` | yes | 0 against 0 |

### Negative control: `negative-control` against `memory-disabled` -- NoDemonstratedBenefit (stale experience; any model that follows the block pays for it, so this catches a block-presence effect only when the content comparison does not)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean(failed_attempts | negative-control) < mean(failed_attempts | memory-disabled)` | **no** | 2.750 against 2.167 |
| `sign_test_p(failed_attempts: negative-control < memory-disabled) <= 0.05` | **no** | p = 0.9375 (2 fewer, 5 more, 5 tied, of 12 pairs) |
| `success_rate(negative-control) >= success_rate(memory-disabled)` | yes | 1.000 against 1.000 |
| `sum(unauthorized_tool_requests | negative-control) <= sum(unauthorized_tool_requests | memory-disabled)` | **no** | 1 against 0 |

Excluded instances: none (the limit is 3).

## Metrics by condition

Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.

Failed attempts split three ways: *rejected* (a valid strategy the database refused), *no change* (an attempt that made no
`apply_migration` call), *other* (an unknown service, migration or strategy).

| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | Rejected / no change / other | First-try success | Followed the block | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Mean latency (ms) | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 12 | 12 | 1.000 (12/12) | 2.167 (1.528) | 26 / 0 / 0 | 0.167 (2/12) | - | 4.167 | 0 | 50 | 29301 | 1982 | 26982 | 0.0103 |
| memory-enabled | 12 | 12 | 1.000 (12/12) | 0.000 (0.000) | 0 / 0 / 0 | 1.000 (12/12) | 1.000 (12/12) | 2.000 | 0 | 24 | 29485 | 776 | 15960 | 0.0085 |
| memory-placebo | 12 | 12 | 1.000 (12/12) | 2.417 (1.621) | 29 / 0 / 0 | 0.083 (1/12) | - | 4.417 | 0 | 53 | 68638 | 2125 | 34452 | 0.0203 |
| negative-control | 12 | 12 | 1.000 (12/12) | 2.750 (1.215) | 33 / 0 / 0 | 0.000 (0/12) | 0.917 (11/12) | 4.833 | 1 | 58 | 77057 | 2340 | 31015 | 0.0228 |

## Learning phase

`learn-current` runs against a database that accepts the strategy the evaluation ticket needs; their records feed
`memory-enabled` and `memory-placebo`. `learn-stale` runs against one that accepts a different strategy; their records feed
`negative-control`. A run that did not verify stored nothing, and its instance's trial ran with nothing to retrieve.

| Seq | Instance | Service | Run | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |
| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |
| 1 | 0 | orders-api | learn-current | online-copy | completed | yes | 0 | online-copy (live) | online-copy | 2 | 1017 | 66 |
| 2 | 0 | orders-api | learn-stale | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | expand-contract | 3 | 1630 | 114 |
| 3 | 1 | billing-ledger | learn-current | in-place | completed | yes | 3 | online-copy > expand-contract > batched-backfill > in-place (live) | in-place | 5 | 2964 | 204 |
| 4 | 1 | billing-ledger | learn-stale | batched-backfill | completed | yes | 2 | online-copy > expand-contract > batched-backfill (live) | batched-backfill | 4 | 2252 | 158 |
| 5 | 2 | inventory-sync | learn-current | batched-backfill | completed | yes | 2 | expand-contract > online-copy > batched-backfill (live) | batched-backfill | 4 | 2276 | 155 |
| 6 | 2 | inventory-sync | learn-stale | shadow-table | completed | yes | 5 | expand-contract > online-copy > batched-backfill > in-place > blue-green > shadow-table (live) | shadow-table | 7 | 4586 | 290 |
| 7 | 3 | customer-profile | learn-current | shadow-table | completed | yes | 4 | expand-contract > online-copy > in-place > blue-green > shadow-table (live) | shadow-table | 6 | 3677 | 238 |
| 8 | 3 | customer-profile | learn-stale | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | in-place | 4 | 2226 | 150 |
| 9 | 4 | shipment-tracker | learn-current | blue-green | completed | yes | 4 | online-copy > expand-contract > shadow-table > in-place > blue-green (live) | blue-green | 6 | 3809 | 259 |
| 10 | 4 | shipment-tracker | learn-stale | in-place | completed | yes | 3 | online-copy > expand-contract > shadow-table > in-place (live) | in-place | 5 | 3025 | 211 |
| 11 | 5 | payments-gateway | learn-current | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 1006 | 66 |
| 12 | 5 | payments-gateway | learn-stale | batched-backfill | completed | yes | 2 | expand-contract > online-copy > batched-backfill (live) | batched-backfill | 4 | 2277 | 164 |
| 13 | 6 | catalog-search | learn-current | online-copy | completed | yes | 0 | online-copy (live) | online-copy | 2 | 992 | 64 |
| 14 | 6 | catalog-search | learn-stale | shadow-table | completed | yes | 4 | online-copy > expand-contract > in-place > blue-green > shadow-table (live) | shadow-table | 6 | 3710 | 248 |
| 15 | 7 | returns-portal | learn-current | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | in-place | 4 | 2242 | 156 |
| 16 | 7 | returns-portal | learn-stale | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 993 | 64 |
| 17 | 8 | loyalty-points | learn-current | batched-backfill | completed | yes | 5 | online-copy > expand-contract > in-place > blue-green > shadow-table > batched-backfill (live) | batched-backfill | 7 | 4616 | 309 |
| 18 | 8 | loyalty-points | learn-stale | online-copy | completed | yes | 0 | online-copy (live) | online-copy | 2 | 1006 | 67 |
| 19 | 9 | notifications-hub | learn-current | shadow-table | completed | yes | 5 | expand-contract > online-copy > batched-backfill > in-place > blue-green > shadow-table (live) | shadow-table | 7 | 4536 | 296 |
| 20 | 9 | notifications-hub | learn-stale | blue-green | completed | yes | 4 | expand-contract > online-copy > batched-backfill > in-place > blue-green (live) | blue-green | 6 | 3716 | 250 |
| 21 | 10 | pricing-engine | learn-current | blue-green | completed | yes | 2 | expand-contract > online-copy > blue-green (live) | blue-green | 4 | 2240 | 156 |
| 22 | 10 | pricing-engine | learn-stale | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | online-copy | 3 | 1588 | 110 |
| 23 | 11 | fraud-scoring | learn-current | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | 2 | 997 | 64 |
| 24 | 11 | fraud-scoring | learn-stale | blue-green | completed | yes | 4 | expand-contract > online-copy > batched-backfill > in-place > blue-green (live) | blue-green | 6 | 3735 | 250 |

## Per-trial results

Every evaluation trial, in the order it ran. None is ever dropped. `Block named` is the strategy on the Approach: line of the
Historical Reference the model was actually shown, read out of the text it was sent.

| Seq | Instance | Service | Condition | Accepted | Status | Live | Failed attempts | Strategies tried | Block named | Followed | Tool calls | Unauthorized | Model calls | Tokens in | Tokens out | Latency (ms) |
| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 25 | 0 | orders-api | memory-disabled | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | - | - | 3 | 0 | 3 | 1601 | 110 | 12274 |
| 26 | 0 | orders-api | memory-enabled | online-copy | completed | yes | 0 | online-copy (live) | online-copy | yes | 2 | 0 | 2 | 2463 | 64 | 14134 |
| 27 | 0 | orders-api | negative-control | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | expand-contract | yes | 3 | 0 | 3 | 3779 | 110 | 9765 |
| 28 | 0 | orders-api | memory-placebo | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | - | - | 3 | 0 | 3 | 3722 | 110 | 16155 |
| 29 | 1 | billing-ledger | memory-enabled | in-place | completed | yes | 0 | in-place (live) | in-place | yes | 2 | 0 | 2 | 2443 | 64 | 6763 |
| 30 | 1 | billing-ledger | negative-control | in-place | completed | yes | 3 | batched-backfill > online-copy > expand-contract > in-place (live) | batched-backfill | yes | 5 | 0 | 5 | 6613 | 204 | 27915 |
| 31 | 1 | billing-ledger | memory-placebo | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | - | - | 4 | 0 | 4 | 5044 | 156 | 18207 |
| 32 | 1 | billing-ledger | memory-disabled | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | - | - | 4 | 0 | 4 | 2260 | 156 | 16575 |
| 33 | 2 | inventory-sync | negative-control | batched-backfill | completed | yes | 5 | shadow-table > expand-contract > online-copy > in-place > blue-green > batched-backfill (live) | shadow-table | yes | 7 | 0 | 7 | 9656 | 296 | 29790 |
| 34 | 2 | inventory-sync | memory-placebo | batched-backfill | completed | yes | 5 | expand-contract > online-copy > blue-green > in-place > shadow-table > batched-backfill (live) | - | - | 7 | 0 | 7 | 9467 | 296 | 37272 |
| 35 | 2 | inventory-sync | memory-disabled | batched-backfill | completed | yes | 2 | expand-contract > online-copy > batched-backfill (live) | - | - | 4 | 0 | 4 | 2259 | 158 | 20323 |
| 36 | 2 | inventory-sync | memory-enabled | batched-backfill | completed | yes | 0 | batched-backfill (live) | batched-backfill | yes | 2 | 0 | 2 | 2456 | 66 | 6947 |
| 37 | 3 | customer-profile | memory-placebo | shadow-table | completed | yes | 4 | expand-contract > in-place > online-copy > blue-green > shadow-table (live) | - | - | 6 | 0 | 6 | 7915 | 248 | 43199 |
| 38 | 3 | customer-profile | memory-disabled | shadow-table | completed | yes | 4 | expand-contract > in-place > online-copy > blue-green > shadow-table (live) | - | - | 6 | 0 | 6 | 3721 | 248 | 48976 |
| 39 | 3 | customer-profile | memory-enabled | shadow-table | completed | yes | 0 | shadow-table (live) | shadow-table | yes | 2 | 0 | 2 | 2443 | 64 | 22976 |
| 40 | 3 | customer-profile | negative-control | shadow-table | completed | yes | 4 | in-place > expand-contract > online-copy > blue-green > shadow-table (live) | in-place | yes | 6 | 0 | 6 | 8101 | 248 | 46345 |
| 41 | 4 | shipment-tracker | memory-disabled | blue-green | completed | yes | 4 | expand-contract > online-copy > batched-backfill > in-place > blue-green (live) | - | - | 6 | 0 | 6 | 3777 | 261 | 49665 |
| 42 | 4 | shipment-tracker | memory-enabled | blue-green | completed | yes | 0 | blue-green (live) | blue-green | yes | 2 | 0 | 2 | 2445 | 67 | 14710 |
| 43 | 4 | shipment-tracker | negative-control | blue-green | completed | yes | 3 | expand-contract > in-place > online-copy > blue-green (live) | in-place | no | 5 | 0 | 5 | 6640 | 211 | 46077 |
| 44 | 4 | shipment-tracker | memory-placebo | blue-green | completed | yes | 3 | expand-contract > online-copy > in-place > blue-green (live) | - | - | 5 | 0 | 5 | 6475 | 211 | 74714 |
| 45 | 5 | payments-gateway | memory-enabled | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | yes | 2 | 0 | 2 | 2472 | 64 | 38320 |
| 46 | 5 | payments-gateway | negative-control | expand-contract | completed | yes | 1 | batched-backfill > expand-contract (live) | batched-backfill | yes | 3 | 0 | 3 | 3782 | 112 | 25980 |
| 47 | 5 | payments-gateway | memory-placebo | expand-contract | completed | yes | 0 | expand-contract (live) | - | - | 2 | 0 | 2 | 2422 | 64 | 20405 |
| 48 | 5 | payments-gateway | memory-disabled | expand-contract | completed | yes | 0 | expand-contract (live) | - | - | 2 | 0 | 2 | 1004 | 64 | 22942 |
| 49 | 6 | catalog-search | negative-control | online-copy | completed | yes | 3 | shadow-table > expand-contract > in-place > online-copy (live) | shadow-table | yes | 5 | 0 | 5 | 6617 | 198 | 42760 |
| 50 | 6 | catalog-search | memory-placebo | online-copy | completed | yes | 1 | expand-contract > online-copy (live) | - | - | 3 | 0 | 3 | 3733 | 108 | 29484 |
| 51 | 6 | catalog-search | memory-disabled | online-copy | completed | yes | 0 | online-copy (live) | - | - | 2 | 0 | 2 | 1012 | 63 | 13061 |
| 52 | 6 | catalog-search | memory-enabled | online-copy | completed | yes | 0 | online-copy (live) | online-copy | yes | 2 | 0 | 2 | 2472 | 63 | 13928 |
| 53 | 7 | returns-portal | memory-placebo | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | - | - | 4 | 0 | 4 | 5066 | 156 | 45009 |
| 54 | 7 | returns-portal | memory-disabled | in-place | completed | yes | 2 | online-copy > expand-contract > in-place (live) | - | - | 4 | 0 | 4 | 2254 | 156 | 36166 |
| 55 | 7 | returns-portal | memory-enabled | in-place | completed | yes | 0 | in-place (live) | in-place | yes | 2 | 0 | 2 | 2455 | 64 | 12428 |
| 56 | 7 | returns-portal | negative-control | in-place | completed | yes | 2 | expand-contract > online-copy > in-place (live) | expand-contract | yes | 4 | 0 | 4 | 5190 | 156 | 38433 |
| 57 | 8 | loyalty-points | memory-disabled | batched-backfill | completed | yes | 2 | expand-contract > online-copy > batched-backfill (live) | - | - | 4 | 0 | 4 | 2275 | 165 | 27178 |
| 58 | 8 | loyalty-points | memory-enabled | batched-backfill | completed | yes | 0 | batched-backfill (live) | batched-backfill | yes | 2 | 0 | 2 | 2454 | 69 | 19461 |
| 59 | 8 | loyalty-points | negative-control | batched-backfill | completed | yes | 2 | online-copy > expand-contract > batched-backfill (live) | online-copy | yes | 4 | 0 | 4 | 5195 | 165 | 27741 |
| 60 | 8 | loyalty-points | memory-placebo | batched-backfill | completed | yes | 5 | expand-contract > online-copy > in-place > blue-green > shadow-table > batched-backfill (live) | - | - | 7 | 0 | 7 | 9495 | 309 | 69584 |
| 61 | 9 | notifications-hub | memory-enabled | shadow-table | completed | yes | 0 | shadow-table (live) | shadow-table | yes | 2 | 0 | 2 | 2458 | 64 | 10154 |
| 62 | 9 | notifications-hub | negative-control | shadow-table | completed | yes | 3 | blue-green > online-copy > expand-contract > shadow-table (live) | blue-green | yes | 5 | 0 | 5 | 6599 | 202 | 30197 |
| 63 | 9 | notifications-hub | memory-placebo | shadow-table | completed | yes | 3 | online-copy > expand-contract > batched-backfill > shadow-table (live) | - | - | 5 | 0 | 5 | 6496 | 204 | 23224 |
| 64 | 9 | notifications-hub | memory-disabled | shadow-table | completed | yes | 4 | online-copy > expand-contract > in-place > blue-green > shadow-table (live) | - | - | 6 | 0 | 6 | 3742 | 248 | 35023 |
| 65 | 10 | pricing-engine | negative-control | blue-green | completed | yes | 4 | online-copy > expand-contract > shadow-table > in-place > blue-green (live) | online-copy | yes | 7 | 1 | 7 | 9723 | 282 | 32631 |
| 66 | 10 | pricing-engine | memory-placebo | blue-green | completed | yes | 2 | expand-contract > online-copy > blue-green (live) | - | - | 4 | 0 | 4 | 5093 | 153 | 14260 |
| 67 | 10 | pricing-engine | memory-disabled | blue-green | completed | yes | 4 | expand-contract > online-copy > shadow-table > in-place > blue-green (live) | - | - | 6 | 0 | 6 | 3789 | 243 | 27870 |
| 68 | 10 | pricing-engine | memory-enabled | blue-green | completed | yes | 0 | blue-green (live) | blue-green | yes | 2 | 0 | 2 | 2469 | 63 | 9143 |
| 69 | 11 | fraud-scoring | memory-placebo | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | - | - | 3 | 0 | 3 | 3710 | 110 | 21905 |
| 70 | 11 | fraud-scoring | memory-disabled | expand-contract | completed | yes | 1 | online-copy > expand-contract (live) | - | - | 3 | 0 | 3 | 1607 | 110 | 13726 |
| 71 | 11 | fraud-scoring | memory-enabled | expand-contract | completed | yes | 0 | expand-contract (live) | expand-contract | yes | 2 | 0 | 2 | 2455 | 64 | 22552 |
| 72 | 11 | fraud-scoring | negative-control | expand-contract | completed | yes | 2 | blue-green > online-copy > expand-contract (live) | blue-green | yes | 4 | 0 | 4 | 5162 | 156 | 14551 |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 103 | 61116 | 4109 | 0.0214 |
| Evaluation | 185 | 204481 | 7223 | 0.0620 |
| **Total** | **288** | **265597** | **11332** | **0.0834** |

Prices: USD 0.25 per million input tokens and USD 1.5 per million output tokens (ai.google.dev/gemini-api/docs/pricing, Standard paid tier, text input and output (thinking included), checked 2026-09-26). An estimate from list prices, not a bill.
Every response reported its token usage.

## Limitations

- One model, one provider, one run. Temperature 0 and a fixed seed are requested, but neither provider guarantees determinism, and a model version change can change every number. Re-run before relying on a result.
- 12 instances. The sign test can detect only a large, consistent effect; NoDemonstratedBenefit here is not evidence that there is no smaller effect.
- The sign test treats the instance pairs as independent. Each hidden strategy is the answer for two services, and a near-deterministic model with a fixed search order will tend to score both alike, so the effective sample is smaller than the instance count and the p-value is optimistic. The inference is over this fixed, pre-committed assignment, not over tasks in general.
- A synthetic task family: a simulated database whose accepted rollout strategy is a stand-in for tacit, environment-specific knowledge. Real tasks usually leave the answer partly inferable, which would shrink the gap between the conditions.
- The working strategy reaches the block verbatim, on the Approach: line, through the story 6.2 ApproachArguments allowlist. This measures whether a model acts on a Historical Reference labelled as untrusted reference material, not whether it can generalize from vaguer lessons.
- Retrieval is not under test: records are scoped per service and each scope holds one record, so a memory-enabled trial always retrieves its own service's record. Retrieval quality over a crowded store is a separate question.
- The negative control's record is stale (verified against a database that has since changed), not irrelevant; an irrelevant record is a different control and was not run.
- Tokens are as the provider reports them; whether thinking tokens are included in the output count is the provider's accounting. Cost is an estimate from list prices. Latency includes the network and is never gated.
- The repository is public, and the hidden assignment is in its source. A model trained on it after the registration date could know the answers; check the model's training cutoff against the registration date.

## Raw results

`gemini-gemini-3.1-flash-lite-2026-09-27.json`, next to this file: every learning run and trial above, with its change sequence and usage. It holds no
prompt, no message text and no key.
