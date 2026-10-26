# Real-task experiment: ollama / qwen2.5:14b / 2026-10-24

**Overall conclusion under the pre-registered rule: RealTaskInconclusive.** More services were excluded than the pre-registration allows. No claim either way.

A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | ollama, host `localhost` (the endpoint's host only) |
| Model requested | `qwen2.5:14b` |
| Model identities the provider reported | `qwen2.5:14b` |
| Settings | temperature 0.7; seed 20261020 + repeat; 1 repeats |
| Pre-registration | `preregistration.realtask.json` at git blob `7e2177c91ed87aacdd69adc536ff45abbfc2167a`; 4 amendment(s) |
| Task set | `realtask@2`: 12 services in 6 clusters; digest `252b8b61715be4e72f38df3ce84e59673beda28a997de9b9dced1ff310b056fa` |
| Limits | 30 tool calls per learning run, 30 per trial, 8 test-running attempts |
| Retries | up to 3 tries on HTTP 429 and 5xx before a call is counted |
| Budget cap | 1500 model calls, 60000000 tokens; used 49 calls, 72420 tokens |
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

Excluded services (12; the limit is 4): shopcart (the learning run of invoicer did not verify, so its cluster has no record and shopcart ran no trial); invoicer (the learning run of shopcart did not verify, so its cluster has no record and invoicer ran no trial); storefront (the learning run of wishlist did not verify, so its cluster has no record and storefront ran no trial); wishlist (a trial errored); seatbilling (the learning run of rentmeter did not verify, so its cluster has no record and seatbilling ran no trial); rentmeter (the learning run of seatbilling did not verify, so its cluster has no record and rentmeter ran no trial); mailqueue (the learning run of exportjobs did not verify, so its cluster has no record and mailqueue ran no trial); exportjobs (the learning run of mailqueue did not verify, so its cluster has no record and exportjobs ran no trial); signupflow (the learning run of crmsync did not verify, so its cluster has no record and signupflow ran no trial); crmsync (the learning run of signupflow did not verify, so its cluster has no record and crmsync ran no trial); auditboard (the learning run of slatracker did not verify, so its cluster has no record and auditboard ran no trial); slatracker (the learning run of auditboard did not verify, so its cluster has no record and slatracker ran no trial).

## By condition

| Condition | Trials | Verified | Mean tool calls (sd) | Mean reads before the first write | First write was the cluster's fix file | Refused writes | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 1 | - | - (-) | - | - | 0 | 1 | 0 | 0 | 0.0000 |
| memory-enabled | 1 | - | - (-) | - | - | 0 | 1 | 0 | 0 | 0.0000 |
| memory-placebo | 1 | - | - (-) | - | - | 0 | 1 | 0 | 0 | 0.0000 |
| mismatched-cluster | 1 | - | - (-) | - | - | 0 | 1 | 0 | 0 | 0.0000 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| wishlist | catalog | 1/1 | 1.0 | 1/1 | - | - | - | - |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | no | 21 | 7 | no | - |
| invoicer | pricing | completed | no | 21 | 8 | no | - |
| storefront | catalog | completed | yes | 7 | 1 | yes | `app/catalog/helpers.py`, `app/catalog/helpers.py` |
| wishlist | catalog | Errored: ClientResultException | - | 0 | 0 | no | - |
| seatbilling | proration | Errored: ClientResultException | - | 0 | 0 | no | - |
| rentmeter | proration | Errored: ClientResultException | - | 0 | 0 | no | - |
| mailqueue | batching | Errored: ClientResultException | - | 0 | 0 | no | - |
| exportjobs | batching | Errored: ClientResultException | - | 0 | 0 | no | - |
| signupflow | accounts | Errored: ClientResultException | - | 0 | 0 | no | - |
| crmsync | accounts | Errored: ClientResultException | - | 0 | 0 | no | - |
| auditboard | reports | Errored: ClientResultException | - | 0 | 0 | no | - |
| slatracker | reports | Errored: ClientResultException | - | 0 | 0 | no | - |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 45 | 64783 | 7637 | 0.0000 |
| Evaluation | 4 | 0 | 0 | 0.0000 |
| **Total** | **49** | **64783** | **7637** | **0.0000** |

Prices: USD 0 per million input tokens and USD 0 per million output tokens (local model on the host's own hardware: no per-token price (the cost is time and electricity, not estimated)).

## Limitations

- Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.
- The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.
- Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.
- Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.
- The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.

## Raw results

Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.
