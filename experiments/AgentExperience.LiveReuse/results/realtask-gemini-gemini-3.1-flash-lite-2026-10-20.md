# Real-task experiment: gemini / gemini-3.1-flash-lite / 2026-10-20

**Overall conclusion under the pre-registered rule: RealTaskInconclusive.** More services were excluded than the pre-registration allows. No claim either way.

A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | gemini, host `generativelanguage.googleapis.com` (the endpoint's host only) |
| Model requested | `gemini-3.1-flash-lite` |
| Model identities the provider reported | `gemini-3.1-flash-lite` |
| Settings | temperature 0.7; seed not sent: this provider does not accept a seed; 3 repeats |
| Pre-registration | `preregistration.realtask.json` at git blob `ae5e1fe462a5f32d6d1d8c3e0fbbaee218eb000d`; 1 amendment(s) |
| Task set | `realtask@1`: 12 services in 6 clusters; digest `818e5b9fa3e831cb05705ea4a7dca6b4707d992f4bdb40c755b149ccf6c56873` |
| Limits | 30 tool calls per learning run, 30 per trial, 8 test-running attempts |
| Retries | up to 3 tries on HTTP 429 and 5xx before a call is counted |
| Budget cap | 2600 model calls, 11000000 tokens; used 329 calls, 378731 tokens |
| Earlier runs | 0 earlier complete run(s) for this provider and model in `results/realtask-ledger.tsv`; the confirmatory run is the first complete one |
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
| memory-disabled | 36 | 1.000 (1/1) | 9.000 (-) | 5.000 | 0.000 (0/1) | 0 | 44 | 9829 | 217 | 0.0028 |
| memory-enabled | 36 | 1.000 (2/2) | 13.000 (2.828) | 9.500 | 0.000 (0/2) | 0 | 60 | 91502 | 606 | 0.0238 |
| memory-placebo | 36 | 1.000 (1/1) | 14.000 (-) | 11.000 | 0.000 (0/1) | 0 | 49 | 49681 | 327 | 0.0129 |
| mismatched-cluster | 36 | 1.000 (1/1) | 14.000 (-) | 10.000 | 1.000 (1/1) | 0 | 62 | 92417 | 636 | 0.0241 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| shopcart | pricing | 3/3 | 1.0 | 0/3 | 9.0 | 11.0 | 14.0 | 14.0 |
| invoicer | pricing | 3/3 | 1.0 | 0/3 | - | 15.0 | - | - |
| storefront | catalog | 3/3 | 1.0 | 3/3 | - | - | - | - |
| wishlist | catalog | 3/3 | 1.0 | 3/3 | - | - | - | - |
| seatbilling | proration | 3/3 | 1.0 | 3/3 | - | - | - | - |
| rentmeter | proration | 3/3 | 1.0 | 3/3 | - | - | - | - |
| mailqueue | batching | 3/3 | 1.0 | 0/3 | - | - | - | - |
| exportjobs | batching | 3/3 | 1.0 | 3/3 | - | - | - | - |
| signupflow | accounts | 3/3 | 1.0 | 3/3 | - | - | - | - |
| crmsync | accounts | 3/3 | 1.0 | 3/3 | - | - | - | - |
| auditboard | reports | 3/3 | 1.0 | 3/3 | - | - | - | - |
| slatracker | reports | 3/3 | 1.0 | 3/3 | - | - | - | - |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | yes | 8 | 2 | yes | `app/pricing/api.py` |
| invoicer | pricing | completed | yes | 9 | 2 | yes | `app/pricing/api.py` |
| storefront | catalog | completed | yes | 9 | 2 | yes | `app/catalog/helpers.py` |
| wishlist | catalog | completed | yes | 9 | 2 | yes | `app/catalog/helpers.py` |
| seatbilling | proration | completed | yes | 10 | 2 | yes | `app/proration/helpers.py` |
| rentmeter | proration | completed | yes | 10 | 2 | yes | `app/proration/helpers.py` |
| mailqueue | batching | completed | yes | 11 | 2 | yes | `app/batching/helpers.py` |
| exportjobs | batching | completed | yes | 10 | 2 | yes | `app/batching/api.py` |
| signupflow | accounts | completed | yes | 10 | 2 | yes | `app/accounts/helpers.py` |
| crmsync | accounts | completed | yes | 10 | 3 | yes | `app/accounts/helpers.py` |
| auditboard | reports | completed | yes | 9 | 2 | yes | `app/reports/helpers.py` |
| slatracker | reports | completed | yes | 9 | 2 | yes | `app/reports/helpers.py` |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 114 | 130689 | 2827 | 0.0369 |
| Evaluation | 215 | 243429 | 1786 | 0.0635 |
| **Total** | **329** | **374118** | **4613** | **0.1004** |

Prices: USD 0.25 per million input tokens and USD 1.5 per million output tokens (ai.google.dev/gemini-api/docs/pricing, Standard paid tier, text input and output (thinking included), checked 2026-09-26).

## Limitations

- Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.
- The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.
- Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.
- Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.
- The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.

## Raw results

Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.
