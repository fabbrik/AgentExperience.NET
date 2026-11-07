# Real-task experiment: openrouter / openai/gpt-oss-120b / 2026-10-26

**Overall conclusion under the pre-registered rule: RealTaskInconclusive.** More services were excluded than the pre-registration allows. No claim either way.

A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | openrouter, host `openrouter.ai` (the endpoint's host only) |
| Model requested | `openai/gpt-oss-120b` |
| Model identities the provider reported | `openai/gpt-oss-120b` |
| Settings | temperature 0.7; seed not sent: this provider does not accept a seed; 3 repeats |
| Pre-registration | `preregistration.realtask.json` at git blob `a9f38487da195936633c0fd4988601509e2c29ec`; 6 amendment(s) |
| Task set | `realtask@2`: 12 services in 6 clusters; digest `252b8b61715be4e72f38df3ce84e59673beda28a997de9b9dced1ff310b056fa` |
| Limits | 30 tool calls per learning run, 30 per trial, 8 test-running attempts |
| Retries | up to 3 tries on HTTP 429 and 5xx before a call is counted |
| Budget cap | 2600 model calls, 11000000 tokens; used 1812 calls, 10129393 tokens |
| Earlier runs | 0 earlier complete run(s) for this provider and model in `results/realtask-ledger.tsv`; the confirmatory run is the first complete one |
| Run status | complete: every learning run and every trial ran |

## Verdict

The gate, evaluated once per comparison. The unit is the **service** (the mean over its repeats), because the repeats of one service are not independent; the trial-level sign test is printed and never gated.

### Reference comparison: `memory-enabled` against `memory-disabled` -- Inconclusive (memory-enabled against memory-disabled)

Not evaluated: 6 service(s) were excluded, more than the pre-registered limit, or no pair remained.

Trial level (reported, never gated; the repeats of one service are not independent): 8 fewer, 7 more, 3 tied, of 18 (service, repeat) pairs; p = 0.5000.

### Content comparison: `memory-enabled` against `memory-placebo` -- Inconclusive (the same records with the file path withheld: separates what the block says from the fact that a block is there)

Not evaluated: 6 service(s) were excluded, more than the pre-registered limit, or no pair remained.

Trial level (reported, never gated; the repeats of one service are not independent): 8 fewer, 5 more, 5 tied, of 18 (service, repeat) pairs; p = 0.2905.

### Mismatched-cluster control: `mismatched-cluster` against `memory-disabled` -- Inconclusive (the store without the sibling's record; expected NoDemonstratedBenefit)

Not evaluated: 6 service(s) were excluded, more than the pre-registered limit, or no pair remained.

Trial level (reported, never gated; the repeats of one service are not independent): 5 fewer, 7 more, 6 tied, of 18 (service, repeat) pairs; p = 0.8062.

Excluded services (6; the limit is 4): shopcart (the learning run of invoicer did not verify, so its cluster has no record and shopcart ran no trial); invoicer (the learning run of shopcart did not verify, so its cluster has no record and invoicer ran no trial); wishlist (the learning run of storefront did not verify, so its cluster has no record and wishlist ran no trial); signupflow (the learning run of crmsync did not verify, so its cluster has no record and signupflow ran no trial); auditboard (the learning run of slatracker did not verify, so its cluster has no record and auditboard ran no trial); slatracker (the learning run of auditboard did not verify, so its cluster has no record and slatracker ran no trial).

## By condition

| Condition | Trials | Verified | Mean tool calls (sd) | Mean reads before the first write | First write was the cluster's fix file | Refused writes | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 18 | 0.667 (12/18) | 21.389 (8.016) | 14.333 | 0.571 (8/14) | 0 | 379 | 1653529 | 37360 | 0.2704 |
| memory-enabled | 18 | 0.667 (12/18) | 20.556 (7.994) | 14.444 | 0.571 (8/14) | 0 | 370 | 1978257 | 39117 | 0.3202 |
| memory-placebo | 18 | 0.444 (8/18) | 23.278 (8.152) | 16.667 | 0.400 (4/10) | 0 | 404 | 2625646 | 33606 | 0.4140 |
| mismatched-cluster | 18 | 0.556 (10/18) | 21.889 (8.540) | 14.278 | 0.455 (5/11) | 0 | 399 | 2363420 | 39720 | 0.3783 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| storefront | catalog | 3/3 | 1.0 | 3/3 | 27.0 | 21.0 | 30.0 | 30.0 |
| seatbilling | proration | 3/3 | 1.0 | 3/3 | 24.7 | 21.7 | 19.3 | 18.7 |
| rentmeter | proration | 3/3 | 1.0 | 3/3 | 21.3 | 30.0 | 27.3 | 24.3 |
| mailqueue | batching | 3/3 | 1.0 | 0/3 | 12.3 | 13.7 | 19.7 | 14.0 |
| exportjobs | batching | 3/3 | 1.0 | 0/3 | 13.0 | 13.0 | 13.3 | 14.3 |
| crmsync | accounts | 3/3 | 1.0 | 3/3 | 30.0 | 24.0 | 30.0 | 30.0 |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | no | 30 | 1 | no | - |
| invoicer | pricing | completed | no | 30 | 1 | no | - |
| storefront | catalog | completed | no | 30 | 2 | no | - |
| wishlist | catalog | completed | yes | 8 | 2 | yes | `app/catalog/helpers.py` |
| seatbilling | proration | completed | yes | 14 | 3 | yes | `app/proration/helpers.py` |
| rentmeter | proration | completed | yes | 14 | 2 | yes | `app/proration/helpers.py` |
| mailqueue | batching | completed | yes | 13 | 4 | yes | `app/batching/api.py` |
| exportjobs | batching | completed | yes | 14 | 2 | yes | `app/batching/api.py`, `app/batching/api.py` |
| signupflow | accounts | completed | yes | 19 | 5 | yes | `app/accounts/helpers.py` |
| crmsync | accounts | completed | no | 23 | 3 | no | - |
| auditboard | reports | completed | no | 30 | 3 | no | - |
| slatracker | reports | completed | no | 30 | 4 | no | - |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 260 | 1341152 | 17586 | 0.2117 |
| Evaluation | 1552 | 8620852 | 149803 | 1.3830 |
| **Total** | **1812** | **9962004** | **167389** | **1.5947** |

Prices: USD 0.15 per million input tokens and USD 0.6 per million output tokens (AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK and AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK, as set for this run).

## Limitations

- Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.
- The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.
- Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.
- Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.
- The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.

## Raw results

Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.
