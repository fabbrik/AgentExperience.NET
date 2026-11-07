# Real-task experiment: openrouter / learning anthropic/claude-haiku-4.5 → evaluation openai/gpt-oss-120b / 2026-10-27

**Overall conclusion under the pre-registered rule: RealTaskNotEvaluated.** The run stopped at a budget cap before every trial ran. No verdict was evaluated.

A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.

## Run

| | |
| --- | --- |
| Provider | openrouter, host `openrouter.ai` (the endpoint's host only) |
| Cross-model run | amendment 7: the learning runs used one model and the evaluation trials another; the ledger records the pair `anthropic/claude-haiku-4.5→openai/gpt-oss-120b` |
| Learning model (learning runs only) | `anthropic/claude-haiku-4.5`; prices USD 1 / 5 per million input / output tokens (AGENTEXPERIENCE_LIVE_LEARNING_PRICE_INPUT_PER_MTOK and AGENTEXPERIENCE_LIVE_LEARNING_PRICE_OUTPUT_PER_MTOK, as set for this run) |
| Evaluation model (every trial) | `openai/gpt-oss-120b`; prices USD 0.15 / 0.6 per million input / output tokens (AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK and AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK, as set for this run) |
| Model identities the provider reported | `anthropic/claude-haiku-4.5`, `openai/gpt-oss-120b` |
| Settings | temperature 0.7; seed not sent: this provider does not accept a seed; 3 repeats |
| Pre-registration | `preregistration.realtask.json` at git blob `3a582cfa7bfd3528ea2e66e5a286849fcdc42bdd`; 7 amendment(s) |
| Task set | `realtask@2`: 12 services in 6 clusters; digest `252b8b61715be4e72f38df3ce84e59673beda28a997de9b9dced1ff310b056fa` |
| Limits | 30 tool calls per learning run, 30 per trial, 8 test-running attempts |
| Retries | up to 3 tries on HTTP 429 and 5xx before a call is counted |
| Budget cap | 2600 model calls, 11000000 tokens; used 1502 calls, 11000363 tokens |
| Earlier runs | 0 earlier complete run(s) for this provider and learning→evaluation pair in `results/realtask-ledger.tsv`; the confirmatory run is the first complete one |
| Run status | stopped: the budget cap was reached during the evaluation phase (The token cap of 11000000 was reached (11000363 used).) |

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
| memory-disabled | 13 | 0.231 (3/13) | 25.769 (8.095) | 20.000 | 0.333 (1/3) | 0 | 324 | 1945151 | 16343 | 0.3016 |
| memory-enabled | 13 | 0.385 (5/13) | 25.769 (7.282) | 16.462 | 0.714 (5/7) | 0 | 327 | 2459668 | 27628 | 0.3855 |
| memory-placebo | 14 | 0.308 (4/13) | 26.692 (6.812) | 22.923 | 0.500 (2/4) | 0 | 370 | 3082451 | 20559 | 0.4747 |
| mismatched-cluster | 14 | 0.286 (4/14) | 25.214 (8.002) | 21.357 | 0.500 (2/4) | 0 | 358 | 3063939 | 22264 | 0.4729 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| shopcart | pricing | 2/2 | 1.0 | 2/2 | 30.0 | 30.0 | 30.0 | 30.0 |
| invoicer | pricing | 1/1 | 1.0 | 1/1 | 30.0 | 29.0 | 30.0 | 30.0 |
| storefront | catalog | 1/1 | 1.0 | 1/1 | 30.0 | 30.0 | 30.0 | 30.0 |
| wishlist | catalog | 1/1 | 1.0 | 1/1 | 30.0 | 19.0 | 30.0 | 30.0 |
| seatbilling | proration | 1/1 | 1.0 | 1/1 | 13.0 | 25.0 | 30.0 | 12.0 |
| rentmeter | proration | 1/1 | 1.0 | 1/1 | 30.0 | 30.0 | 23.0 | 18.0 |
| mailqueue | batching | 1/1 | 1.0 | 0/1 | 13.0 | 11.0 | 13.0 | 12.0 |
| exportjobs | batching | 1/1 | 1.0 | 0/1 | 9.0 | 11.0 | 11.0 | 11.0 |
| signupflow | accounts | 1/1 | 1.0 | 1/1 | 30.0 | 30.0 | 30.0 | 30.0 |
| crmsync | accounts | 1/1 | 1.0 | 1/1 | 30.0 | 30.0 | 30.0 | 30.0 |
| auditboard | reports | 1/1 | 1.0 | 0/1 | 30.0 | 30.0 | 30.0 | 30.0 |
| slatracker | reports | 1/1 | 1.0 | 0/1 | 30.0 | 30.0 | 30.0 | 30.0 |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | yes | 16 | 2 | yes | `app/pricing/helpers.py` |
| invoicer | pricing | completed | yes | 14 | 2 | yes | `app/pricing/helpers.py` |
| storefront | catalog | completed | yes | 13 | 2 | yes | `app/catalog/helpers.py` |
| wishlist | catalog | completed | yes | 12 | 2 | yes | `app/catalog/helpers.py` |
| seatbilling | proration | completed | yes | 12 | 2 | yes | `app/proration/helpers.py` |
| rentmeter | proration | completed | yes | 13 | 2 | yes | `app/proration/helpers.py` |
| mailqueue | batching | completed | yes | 10 | 2 | yes | `app/batching/api.py` |
| exportjobs | batching | completed | yes | 11 | 2 | yes | `app/batching/api.py` |
| signupflow | accounts | completed | yes | 11 | 2 | yes | `app/accounts/helpers.py` |
| crmsync | accounts | completed | yes | 12 | 2 | yes | `app/accounts/helpers.py` |
| auditboard | reports | completed | yes | 18 | 2 | yes | `app/reports/api.py`, `app/reports/api.py` |
| slatracker | reports | completed | yes | 18 | 2 | yes | `app/reports/api.py`, `app/reports/api.py`, `app/reports/api.py` |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning (`anthropic/claude-haiku-4.5`) | 123 | 343396 | 18964 | 0.4382 |
| Evaluation (`openai/gpt-oss-120b`) | 1379 | 10551209 | 86794 | 1.6348 |
| **Total** | **1502** | **10894605** | **105758** | **2.0730** |

Learning prices (`anthropic/claude-haiku-4.5`): USD 1 per million input tokens and USD 5 per million output tokens (AGENTEXPERIENCE_LIVE_LEARNING_PRICE_INPUT_PER_MTOK and AGENTEXPERIENCE_LIVE_LEARNING_PRICE_OUTPUT_PER_MTOK, as set for this run).

Evaluation prices (`openai/gpt-oss-120b`): USD 0.15 per million input tokens and USD 0.6 per million output tokens (AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK and AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK, as set for this run).

## Limitations

- Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.
- The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.
- Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.
- Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.
- The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.

## Raw results

Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.
