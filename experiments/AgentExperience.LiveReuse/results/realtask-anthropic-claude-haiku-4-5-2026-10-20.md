# Real-task experiment: anthropic / claude-haiku-4-5 / 2026-10-20

**Overall conclusion under the pre-registered rule: RealTaskInconclusive.** More services were excluded than the pre-registration allows. No claim either way.

A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | anthropic, host `api.anthropic.com` (the endpoint's host only) |
| Model requested | `claude-haiku-4-5` |
| Model identities the provider reported | `claude-haiku-4-5-20251001` |
| Settings | temperature 0.7; seed not sent: this provider does not accept a seed; 3 repeats |
| Pre-registration | `preregistration.realtask.json` at git blob `a70914279493fb45855e2f271e79542149faa5cd`; 0 amendment(s) |
| Task set | `realtask@1`: 12 services in 6 clusters; digest `818e5b9fa3e831cb05705ea4a7dca6b4707d992f4bdb40c755b149ccf6c56873` |
| Limits | 30 tool calls per learning run, 30 per trial, 8 test-running attempts |
| Retries | up to 3 tries on HTTP 429 and 5xx before a call is counted |
| Budget cap | 2600 model calls, 11000000 tokens; used 594 calls, 1906924 tokens |
| Earlier runs | 0 earlier ledger entries for this provider and model in `results/realtask-ledger.tsv`; the confirmatory run is the first complete one |
| Run status | complete: every learning run and every trial ran |

## Verdict

The gate, evaluated once per comparison. The unit is the **service** (the mean over its repeats), because the repeats of one service are not independent; the trial-level sign test is printed and never gated.

### Reference comparison: `memory-enabled` against `memory-disabled` -- Inconclusive (memory-enabled against memory-disabled)

Not evaluated: 12 service(s) were excluded, more than the pre-registered limit, or no pair remained.

Trial level (reported, never gated; the repeats of one service are not independent): 0 fewer, 0 more, 0 tied, of 0 (service, repeat) pairs; p = 1.0000.

### Content comparison: `memory-enabled` against `memory-placebo` -- Inconclusive (the same records with the file path withheld: separates what the block says from the fact that a block is there)

Not evaluated: 12 service(s) were excluded, more than the pre-registered limit, or no pair remained.

Trial level (reported, never gated; the repeats of one service are not independent): 0 fewer, 0 more, 0 tied, of 0 (service, repeat) pairs; p = 1.0000.

### Mismatched-cluster control: `mismatched-cluster` against `memory-disabled` -- Inconclusive (the store without the sibling's record; expected NoDemonstratedBenefit)

Not evaluated: 12 service(s) were excluded, more than the pre-registered limit, or no pair remained.

Trial level (reported, never gated; the repeats of one service are not independent): 0 fewer, 0 more, 0 tied, of 0 (service, repeat) pairs; p = 1.0000.

Excluded services (12; the limit is 4): shopcart (a trial errored); invoicer (a trial errored); storefront (a trial errored); wishlist (a trial errored); seatbilling (a trial errored); rentmeter (a trial errored); mailqueue (a trial errored); exportjobs (a trial errored); signupflow (a trial errored); crmsync (a trial errored); auditboard (a trial errored); slatracker (a trial errored).

## By condition

| Condition | Trials | Verified | Mean tool calls (sd) | Mean reads before the first write | First write was the cluster's fix file | Refused writes | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 36 | 1.000 (17/17) | 10.941 (0.899) | 7.000 | 0.588 (10/17) | 0 | 132 | 230998 | 19704 | 0.3295 |
| memory-enabled | 36 | 1.000 (17/17) | 10.353 (1.272) | 6.294 | 0.647 (11/17) | 0 | 125 | 461121 | 21004 | 0.5661 |
| memory-placebo | 36 | 1.000 (18/18) | 10.000 (0.840) | 6.056 | 0.667 (12/18) | 0 | 127 | 461294 | 22317 | 0.5729 |
| mismatched-cluster | 36 | 1.000 (18/18) | 10.667 (2.544) | 6.111 | 0.722 (13/18) | 0 | 132 | 495777 | 25433 | 0.6229 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| shopcart | pricing | 3/3 | 1.0 | 0/3 | 10.5 | 11.0 | 10.5 | 10.0 |
| invoicer | pricing | 3/3 | 1.0 | 0/3 | 12.0 | 12.5 | 11.0 | 16.5 |
| storefront | catalog | 3/3 | 1.0 | 3/3 | 10.0 | 10.0 | 10.0 | 9.5 |
| wishlist | catalog | 3/3 | 1.0 | 3/3 | 11.0 | 10.5 | 10.0 | 10.0 |
| seatbilling | proration | 3/3 | 1.0 | 3/3 | 11.0 | 10.5 | 10.5 | 11.5 |
| rentmeter | proration | 3/3 | 1.0 | 0/3 | 11.0 | 10.0 | 9.5 | 11.0 |
| mailqueue | batching | 3/3 | 1.0 | 0/3 | 11.0 | 9.0 | 8.0 | 8.0 |
| exportjobs | batching | 3/3 | 1.0 | 0/3 | 12.0 | 9.0 | 9.0 | 8.0 |
| signupflow | accounts | 3/3 | 1.0 | 3/3 | 10.0 | 11.0 | 11.0 | 11.0 |
| crmsync | accounts | 3/3 | 1.0 | 3/3 | 13.0 | 10.0 | 10.0 | 10.0 |
| auditboard | reports | 3/3 | 1.0 | 3/3 | 10.0 | 9.0 | 9.0 | 10.0 |
| slatracker | reports | 3/3 | 1.0 | 0/3 | 10.0 | 9.0 | 10.0 | 8.0 |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | yes | 10 | 2 | yes | `app/pricing/api.py` |
| invoicer | pricing | completed | yes | 11 | 2 | yes | `app/pricing/api.py` |
| storefront | catalog | completed | yes | 10 | 1 | yes | `app/catalog/helpers.py` |
| wishlist | catalog | completed | yes | 9 | 1 | yes | `app/catalog/helpers.py` |
| seatbilling | proration | completed | yes | 9 | 1 | yes | `app/proration/api.py` |
| rentmeter | proration | completed | yes | 11 | 2 | yes | `app/proration/helpers.py` |
| mailqueue | batching | completed | yes | 11 | 2 | yes | `app/batching/api.py` |
| exportjobs | batching | completed | yes | 11 | 2 | yes | `app/batching/api.py` |
| signupflow | accounts | completed | yes | 11 | 2 | yes | `app/accounts/helpers.py` |
| crmsync | accounts | completed | yes | 10 | 2 | yes | `app/accounts/helpers.py` |
| auditboard | reports | completed | yes | 11 | 2 | yes | `app/reports/api.py` |
| slatracker | reports | completed | yes | 10 | 2 | yes | `app/reports/helpers.py` |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 78 | 156349 | 12927 | 0.2210 |
| Evaluation | 516 | 1649190 | 88458 | 2.0915 |
| **Total** | **594** | **1805539** | **101385** | **2.3125** |

Prices: USD 1 per million input tokens and USD 5 per million output tokens (Anthropic API pricing, standard rates for Claude Haiku 4.5, input and output, no batch or prompt-cache discount).

## Limitations

- Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.
- The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.
- Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.
- Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.
- The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.

## Raw results

Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.
