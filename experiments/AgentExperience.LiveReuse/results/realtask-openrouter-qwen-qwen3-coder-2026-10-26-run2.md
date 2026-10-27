# Real-task experiment: openrouter / qwen/qwen3-coder / 2026-10-26

**Overall conclusion under the pre-registered rule: RealTaskNotEvaluated.** The run stopped at a budget cap before every trial ran. No verdict was evaluated.

A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | openrouter, host `openrouter.ai` (the endpoint's host only) |
| Model requested | `qwen/qwen3-coder` |
| Model identities the provider reported | `qwen/qwen3-coder` |
| Settings | temperature 0.7; seed not sent: this provider does not accept a seed; 3 repeats |
| Pre-registration | `preregistration.realtask.json` at git blob `a9f38487da195936633c0fd4988601509e2c29ec`; 6 amendment(s) |
| Task set | `realtask@2`: 12 services in 6 clusters; digest `252b8b61715be4e72f38df3ce84e59673beda28a997de9b9dced1ff310b056fa` |
| Limits | 30 tool calls per learning run, 30 per trial, 8 test-running attempts |
| Retries | up to 3 tries on HTTP 429 and 5xx before a call is counted |
| Budget cap | 80 model calls, 11000000 tokens; used 80 calls, 508110 tokens |
| Earlier runs | 4 earlier complete run(s) for this provider and model in `results/realtask-ledger.tsv`; this run is exploratory |
| Run status | stopped: the budget cap was reached during the learning phase (The model-call cap of 80 was reached.) |

## Verdict

The gate, evaluated once per comparison. The unit is the **service** (the mean over its repeats), because the repeats of one service are not independent; the trial-level sign test is printed and never gated.

### Reference comparison: `memory-enabled` against `memory-disabled` -- NotEvaluated (memory-enabled against memory-disabled)

Not evaluated: the run stopped before every trial ran.

### Content comparison: `memory-enabled` against `memory-placebo` -- NotEvaluated (the same records with the file path withheld: separates what the block says from the fact that a block is there)

Not evaluated: the run stopped before every trial ran.

### Mismatched-cluster control: `mismatched-cluster` against `memory-disabled` -- NotEvaluated (the store without the sibling's record; expected NoDemonstratedBenefit)

Not evaluated: the run stopped before every trial ran.

## By condition

| Condition | Trials | Verified | Mean tool calls (sd) | Mean reads before the first write | First write was the cluster's fix file | Refused writes | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 0 | - | - (-) | - | - | 0 | 0 | 0 | 0 | 0.0000 |
| memory-enabled | 0 | - | - (-) | - | - | 0 | 0 | 0 | 0 | 0.0000 |
| memory-placebo | 0 | - | - (-) | - | - | 0 | 0 | 0 | 0 | 0.0000 |
| mismatched-cluster | 0 | - | - (-) | - | - | 0 | 0 | 0 | 0 | 0.0000 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | no | 30 | 0 | no | - |
| invoicer | pricing | completed | yes | 9 | 2 | yes | `app/pricing/helpers.py` |
| storefront | catalog | completed | yes | 12 | 2 | yes | `app/catalog/helpers.py` |
| wishlist | catalog | BudgetExhausted: The model-call cap of 80 was reached. | - | 0 | 0 | no | - |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning | 80 | 503278 | 4832 | 0.1558 |
| Evaluation | 0 | 0 | 0 | 0.0000 |
| **Total** | **80** | **503278** | **4832** | **0.1558** |

Prices: USD 0.3 per million input tokens and USD 1 per million output tokens (AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK and AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK, as set for this run).

## Limitations

- Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.
- The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.
- Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.
- Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.
- The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.

## Raw results

Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.
