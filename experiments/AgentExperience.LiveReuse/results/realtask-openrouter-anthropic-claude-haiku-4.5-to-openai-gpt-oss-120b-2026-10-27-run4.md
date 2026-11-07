# Real-task experiment: openrouter / learning anthropic/claude-haiku-4.5 → evaluation openai/gpt-oss-120b / 2026-10-27

**Overall conclusion under the pre-registered rule: RealTaskNoDemonstratedBenefit.** Memory-enabled did not pass the pre-registered gate against memory-disabled. No benefit is claimed.

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
| Budget cap | 4000 model calls, 28000000 tokens; used 3433 calls, 24860377 tokens |
| Earlier runs | 0 earlier complete run(s) for this provider and learning→evaluation pair in `results/realtask-ledger.tsv`; the confirmatory run is the first complete one |
| Run status | complete: every learning run and every trial ran |

## Verdict

The gate, evaluated once per comparison. The unit is the **service** (the mean over its repeats), because the repeats of one service are not independent; the trial-level sign test is printed and never gated.

### Reference comparison: `memory-enabled` against `memory-disabled` -- NoDemonstratedBenefit (memory-enabled against memory-disabled)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean over services of tool_calls(memory-enabled) < mean over services of tool_calls(memory-disabled)` | yes | 23.303 against 24.727 |
| `sign_test_p(service mean tool_calls: memory-enabled < memory-disabled) <= 0.05` | **no** | p = 0.5000 (5 fewer, 4 more, 2 tied, of 11 services) |
| `success_rate(memory-enabled) >= success_rate(memory-disabled)` | yes | 0.576 against 0.455 |
| `sum(refused_writes | memory-enabled) <= sum(refused_writes | memory-disabled)` | yes | 0 against 0 |

Trial level (reported, never gated; the repeats of one service are not independent): 14 fewer, 8 more, 11 tied, of 33 (service, repeat) pairs; p = 0.1431.

### Content comparison: `memory-enabled` against `memory-placebo` -- BenefitDemonstrated (the same records with the file path withheld: separates what the block says from the fact that a block is there)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean over services of tool_calls(memory-enabled) < mean over services of tool_calls(memory-placebo)` | yes | 23.303 against 25.606 |
| `sign_test_p(service mean tool_calls: memory-enabled < memory-placebo) <= 0.05` | yes | p = 0.0352 (7 fewer, 1 more, 3 tied, of 11 services) |
| `success_rate(memory-enabled) >= success_rate(memory-placebo)` | yes | 0.576 against 0.364 |
| `sum(refused_writes | memory-enabled) <= sum(refused_writes | memory-placebo)` | yes | 0 against 0 |

Trial level (reported, never gated; the repeats of one service are not independent): 13 fewer, 5 more, 15 tied, of 33 (service, repeat) pairs; p = 0.0481.

### Mismatched-cluster control: `mismatched-cluster` against `memory-disabled` -- NoDemonstratedBenefit (the store without the sibling's record; expected NoDemonstratedBenefit)

| Term | Holds | Observed |
| --- | --- | --- |
| `mean over services of tool_calls(mismatched-cluster) < mean over services of tool_calls(memory-disabled)` | **no** | 25.970 against 24.727 |
| `sign_test_p(service mean tool_calls: mismatched-cluster < memory-disabled) <= 0.05` | **no** | p = 0.8906 (2 fewer, 4 more, 5 tied, of 11 services) |
| `success_rate(mismatched-cluster) >= success_rate(memory-disabled)` | **no** | 0.303 against 0.455 |
| `sum(refused_writes | mismatched-cluster) <= sum(refused_writes | memory-disabled)` | yes | 0 against 0 |

Trial level (reported, never gated; the repeats of one service are not independent): 5 fewer, 10 more, 18 tied, of 33 (service, repeat) pairs; p = 0.9408.

Excluded services (1; the limit is 4): crmsync (the learning run of signupflow did not verify, so its cluster has no record and crmsync ran no trial).

## By condition

| Condition | Trials | Verified | Mean tool calls (sd) | Mean reads before the first write | First write was the cluster's fix file | Refused writes | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| memory-disabled | 33 | 0.455 (15/33) | 24.727 (7.580) | 16.667 | 0.667 (12/18) | 0 | 796 | 4188620 | 61383 | 0.6651 |
| memory-enabled | 33 | 0.576 (19/33) | 23.303 (7.239) | 16.545 | 0.619 (13/21) | 0 | 772 | 5493433 | 69416 | 0.8657 |
| memory-placebo | 33 | 0.364 (12/33) | 25.606 (6.442) | 21.182 | 0.500 (8/16) | 0 | 850 | 6924188 | 66788 | 1.0787 |
| mismatched-cluster | 33 | 0.303 (10/33) | 25.970 (6.673) | 21.424 | 0.417 (5/12) | 0 | 868 | 7420165 | 60405 | 1.1493 |

A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.

## By service: retrieval and tool calls

What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.

| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| shopcart | pricing | 3/3 | 1.0 | 3/3 | 30.0 | 30.0 | 30.0 | 30.0 |
| invoicer | pricing | 3/3 | 1.0 | 3/3 | 30.0 | 26.7 | 30.0 | 30.0 |
| storefront | catalog | 3/3 | 1.0 | 3/3 | 30.0 | 21.0 | 27.0 | 30.0 |
| wishlist | catalog | 3/3 | 1.0 | 3/3 | 30.0 | 24.0 | 30.0 | 30.0 |
| seatbilling | proration | 3/3 | 1.0 | 3/3 | 27.7 | 20.3 | 27.7 | 18.0 |
| rentmeter | proration | 3/3 | 1.0 | 3/3 | 21.0 | 20.0 | 21.7 | 27.3 |
| mailqueue | batching | 3/3 | 1.0 | 3/3 | 10.3 | 15.7 | 13.3 | 17.7 |
| exportjobs | batching | 3/3 | 1.0 | 0/3 | 13.3 | 14.0 | 16.0 | 12.7 |
| signupflow | accounts | 3/3 | 1.0 | 3/3 | 23.3 | 24.7 | 26.0 | 30.0 |
| auditboard | reports | 3/3 | 1.0 | 3/3 | 26.3 | 30.0 | 30.0 | 30.0 |
| slatracker | reports | 3/3 | 1.0 | 0/3 | 30.0 | 30.0 | 30.0 | 30.0 |

## Learning runs

| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |
| --- | --- | --- | --- | ---: | ---: | --- | --- |
| shopcart | pricing | completed | yes | 23 | 2 | yes | `app/pricing/helpers.py` |
| invoicer | pricing | completed | yes | 13 | 2 | yes | `app/pricing/helpers.py` |
| storefront | catalog | completed | yes | 13 | 2 | yes | `app/catalog/helpers.py` |
| wishlist | catalog | completed | yes | 13 | 2 | yes | `app/catalog/helpers.py` |
| seatbilling | proration | completed | yes | 13 | 2 | yes | `app/proration/helpers.py` |
| rentmeter | proration | completed | yes | 13 | 2 | yes | `app/proration/helpers.py` |
| mailqueue | batching | completed | yes | 19 | 2 | yes | `app/batching/api.py`, `app/batching/api.py` |
| exportjobs | batching | completed | yes | 13 | 2 | yes | `app/batching/helpers.py` |
| signupflow | accounts | completed | no | 30 | 1 | no | - |
| crmsync | accounts | completed | yes | 14 | 2 | yes | `app/accounts/helpers.py` |
| auditboard | reports | completed | yes | 15 | 2 | yes | `app/reports/api.py` |
| slatracker | reports | completed | yes | 22 | 2 | yes | `app/reports/helpers.py`, `app/reports/helpers.py` |

## Tokens and cost

| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |
| --- | ---: | ---: | ---: | ---: |
| Learning (`anthropic/claude-haiku-4.5`) | 147 | 553356 | 22623 | 0.6665 |
| Evaluation (`openai/gpt-oss-120b`) | 3286 | 24026406 | 257992 | 3.7588 |
| **Total** | **3433** | **24579762** | **280615** | **4.4252** |

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
