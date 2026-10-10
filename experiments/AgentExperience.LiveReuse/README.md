# Live reuse experiment

Every test in this repository, the end-to-end sample and story 4.4's reuse baseline drive a scripted `IChatClient`.
They prove the plumbing: that a Historical Reference reaches the model's context and that the gate can say no. None of
them can show the library's premise with a real model. This experiment runs 4.4's methodology against one:

> A model that failed, once its experience has been captured, verified, reflected on, stored and injected as a
> Historical Reference, needs fewer failed attempts on a later, different task on the same system -- fewer than
> with memory disabled and fewer than when shown the same block with the working strategy withheld -- and gets no
> such help when the injected experience is stale.

**Status: one confirmatory result, for `gemini-3.1-flash-lite`.** The first complete run with the registered Gemini
model ([report](results/gemini-gemini-3.1-flash-lite-2026-09-27.md)) concluded **ReuseBenefitAttributableToContent** under the pre-registered rule:

| Condition | Mean failed attempts | First-try success | Verified success |
| --- | ---: | ---: | ---: |
| memory-disabled | 2.17 | 2 / 12 | 12 / 12 |
| memory-enabled | **0.00** | **12 / 12** | 12 / 12 |
| memory-placebo (strategy withheld) | 2.42 | 1 / 12 | 12 / 12 |
| negative-control (stale experience) | 2.75 | 0 / 12 | 12 / 12 |

Memory-enabled beat memory-disabled (sign test p = 0.0010, 10 of 12 pairs better, 2 tied) and the placebo (p = 0.0005,
11 better, 1 tied); stale experience showed no benefit (p = 0.94) and led to the run's only refused bypass request.
The run used 288 model calls and about USD 0.08. Read it with the report's own limitations: one model, one run,
12 instances with pairs that are not fully independent, a synthetic task, and a strategy that reaches the block
verbatim. It shows that this model acts on the `Approach:` line (the block's wording then; the working attempt's `Tried:`
line now), not that the library helps on real tasks in general.
Pre-registration amendment 1, recorded before any trial completed, stops sending the seed to Gemini, whose endpoint
rejects the field; the ledger keeps the aborted first attempt. No Azure OpenAI run has been made. An Anthropic (Claude)
provider is available; no model of it is registered, so any Anthropic run is exploratory and its report says so.

**Exploratory replication: `claude-haiku-4-5`** ([report](results/anthropic-claude-haiku-4-5-2026-09-28.md)). Under the same pre-registered rule it also concluded
**ReuseBenefitAttributableToContent**: mean failed attempts 0.83 with memory, 2.75 without it, 2.42 with the strategy
withheld and 2.67 with stale experience; memory beat both no memory and the placebo on 9 of 12 pairs with 3 ties
(sign test p = 0.0020 each), and the stale control showed none (p = 0.81). Unlike Gemini, Haiku did not always finish:
it verified 9 of 12 tasks without memory and 11 of 12 with it, and six of its 24 learning runs never verified, so those
services had less or no experience to retrieve. It cost about USD 0.93 (367 calls). Two earlier Haiku attempts are in
the ledger: one stopped on an empty credit balance, one refused to report because Haiku sometimes makes a redundant
second `apply_migration` call after success, which the harness then learned to record as a sequence.

## Run it

```bash
# Gemini (default model gemini-3.1-flash-lite)
export GEMINI_API_KEY=...                 # never commit it; nothing here prints it
dotnet run --project experiments/AgentExperience.LiveReuse -c Release

# Azure OpenAI (the v1 endpoint)
export AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com/
export AZURE_OPENAI_API_KEY=...
export AZURE_OPENAI_DEPLOYMENT=<deployment name>
export AGENTEXPERIENCE_LIVE_PROVIDER=azure
dotnet run --project experiments/AgentExperience.LiveReuse -c Release

# Anthropic (Claude; default model claude-haiku-4-5). Exploratory: no Claude model is registered.
export ANTHROPIC_API_KEY=...
export AGENTEXPERIENCE_LIVE_PROVIDER=anthropic   # optional when no other provider's variables are set
dotnet run --project experiments/AgentExperience.LiveReuse -c Release

# Offline: the whole harness against the scripted stand-in. No key, no network, writes nothing.
dotnet run --project experiments/AgentExperience.LiveReuse -c Release -- --scripted
```

With no provider variable set, the command prints `SKIPPED: ...`, exits 0 and spends nothing. It never runs in CI:
`dotnet test` does not run console apps and no workflow invokes this project (the repository's `WorkflowTests` forbid
secrets in every workflow). The harness itself is proven offline by `experiments/AgentExperience.LiveReuse.Tests`,
which is part of the ordinary `dotnet test` run.

| Variable | Meaning |
| --- | --- |
| `AGENTEXPERIENCE_LIVE_PROVIDER` | `gemini`, `azure` or `anthropic`. Optional when only one provider's variables are set; required when two or three are. |
| `GEMINI_API_KEY` | Gemini API key. |
| `GEMINI_MODEL` | Optional. Default `gemini-3.1-flash-lite`, Google's stable low-cost model (checked 2026-09-26). |
| `AZURE_OPENAI_ENDPOINT` | `https://<resource>.openai.azure.com/` (the `/openai/v1/` path is appended if absent). |
| `AZURE_OPENAI_API_KEY` | Azure OpenAI key. |
| `AZURE_OPENAI_DEPLOYMENT` | The deployment name to call. |
| `ANTHROPIC_API_KEY` | Anthropic API key. |
| `ANTHROPIC_MODEL` | Optional. Default `claude-haiku-4-5`, Anthropic's low-cost current model. |
| `AGENTEXPERIENCE_LIVE_MAX_CALLS` | Hard cap on model calls for the whole run. Default 600 (pre-registered). |
| `AGENTEXPERIENCE_LIVE_MAX_TOKENS` | Hard cap on input plus output tokens. Default 2,000,000 (pre-registered). |
| `AGENTEXPERIENCE_LIVE_MIN_CALL_INTERVAL_MS` | Optional pacing between calls, for a free tier's requests-per-minute limit. |
| `AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK`, `..._OUTPUT_PER_MTOK` | USD per million tokens for the cost estimate. Built in only for `gemini-3.1-flash-lite` ($0.25 / $1.50, standard paid tier) and `claude-haiku-4-5` ($1.00 / $5.00, Anthropic API standard rates). |
| `AGENTEXPERIENCE_LIVE_RESULTS_DIR` | Optional. Default `experiments/AgentExperience.LiveReuse/results`. |

**Keys.** The key is read from the environment, handed to the provider SDK in one place (the OpenAI SDK's
`ApiKeyCredential`, or the Anthropic client's `ApiKey`, set explicitly together with its base URL so that no ambient
`ANTHROPIC_*` setting is consulted), and never stored anywhere else: the configuration type's `ToString` redacts it, the report carries the endpoint's host only (for Azure,
with the resource name replaced by `<resource>`, since an Azure host is the resource name), a provider error is
recorded by exception type and HTTP status only (never its message or body), an unexpected failure prints its type
only, and the raw results hold no prompt. Strategy names and model identities that reach the report are restricted or
sanitized. The offline tests run a full host round trip with a fake key and assert it appears in no output.

**Cost.** One run is 24 learning runs and 48 evaluation trials. With the scripted stand-in that is about 300 model
calls; a real model that explores more can use up to the 600-call cap (the cap counts logical calls; the SDK's own
retries of a 408, 429 or 5xx are not counted separately). At `gemini-3.1-flash-lite` list prices ($0.25
per million input tokens, $1.50 per million output, thinking included) a full run is expected to cost well under one
US dollar, since input tokens dominate. The 2,000,000-token cap bounds the worst case at about $3.00 (every token
billed at the output price, plus the one call that may start just under the cap), and the run stops cleanly before
the first call that would start past either cap. Azure costs depend on the deployment; set the two price
variables to get an estimate in the report. At `claude-haiku-4-5` standard rates ($1.00 per million input tokens,
$5.00 per million output), four times Gemini's input price, the token counts of the Gemini run above would cost about
USD 0.30; Claude's tokenizer and behaviour differ, so expect the same order, not the same figure. The token cap bounds
the worst case at about $10 (every token billed at the output price).

## Provider integration

Both providers are reached through **one** package, exact-pinned in this project only (never in `src/`):
`Microsoft.Extensions.AI.OpenAI` `[10.10.0]`, which brings the OpenAI .NET SDK (`OpenAI` 2.13.0). The experiment sees
only `IChatClient`.

| Option | Verdict |
| --- | --- |
| Gemini: Google GenAI .NET SDK (`Google.GenAI` 1.22.0, `AsIChatClient`) | Not chosen. A second, Gemini-only stack: `Google.Apis.Auth` 1.69.0 (and `Google.Apis`, `Google.Apis.Core`), `MimeTypes`, and a `Microsoft.Extensions.AI.Abstractions` 10.6.0 floor; its advantage, native thought-signature handling, is made unnecessary by the harness design below. |
| Gemini: OpenAI-compatible endpoint `https://generativelanguage.googleapis.com/v1beta/openai/` through `Microsoft.Extensions.AI.OpenAI` | **Chosen.** Documented by Google (ai.google.dev/gemini-api/docs/openai), bearer-key auth, function calling supported. |
| Azure: `Azure.AI.OpenAI` 2.1.0 | Not chosen. Adds `Azure.Core` on top of the same OpenAI SDK, and its newer releases are all prereleases; the v1 API was designed to make it unnecessary for key auth. |
| Azure: OpenAI SDK against the v1 endpoint `https://<resource>.openai.azure.com/openai/v1/` | **Chosen.** Microsoft's documented C# pattern for the v1 API with a key (`new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = ... })`). |

`10.10.0` is the version because it depends on `Microsoft.Extensions.AI.Abstractions` 10.10.0, which is exactly the
shipping floor: the experiment's graph resolves every floored package to its floor, like the test projects. Nothing in
`src/` changes, so no row is needed in `docs/compatibility-evidence.md` (its pin rules and
`CompatibilityPinAgreementTests` cover the shipping projects, the proof, and `tests/`/`samples/` lock files).

**Anthropic** is reached through Anthropic's official C# SDK, `Anthropic` `[12.50.0]`, exact-pinned here only, and its
own Microsoft.Extensions.AI adapter: `new AnthropicClient { ApiKey = key, BaseUrl = "https://api.anthropic.com" }
.AsIChatClient(model, 4096)` (`Microsoft.Extensions.AI.AnthropicClientExtensions.AsIChatClient(IAnthropicClient,
string?, int?)`). The experiment still sees only `IChatClient`, and function invocation is Microsoft.Extensions.AI's, as
for the other providers. An OpenAI-compatible endpoint is not used for Claude: the native Messages API is the one
Anthropic documents and supports for tool use. The package asks for `Microsoft.Extensions.AI.Abstractions` >= 10.5.1
(and, on older frameworks only, `System.Text.Json` and `System.Net.ServerSentEvents`, which net10.0 has in the box), so
the graph stays at the 10.10.0 floor above. Three request details differ from the other providers, all recorded in the
report's *Settings* row or here: the Messages API requires `max_tokens`, so Anthropic calls carry a cap of 4096 output
tokens per call (the other providers are sent none); it has no seed parameter, so none is sent; and no thinking mode is
requested (the harness never sets `ChatOptions.Reasoning`).

**One consequence of the choice, designed around rather than hidden.** Gemini 3 models attach a thought signature to
every function call and expect it back when the call is replayed; `Microsoft.Extensions.AI.OpenAI` 10.10 does not
round-trip it through the OpenAI-compatible endpoint. The harness therefore never replays a function call: all the tool
calls in one model response are run and then end that model call, a call whose arguments do not bind is answered with
a rejection text rather than an error result, and the next request carries a plain-text work log of what the model
called and what each tool returned. Both providers, and all four conditions, see exactly that shape; a test asserts no
request ever carries a function call or result.

## Design

Pre-registered in [`preregistration.json`](preregistration.json) before any live run, and embedded into the build:
the harness reads every value it executes from the file, refuses a task set whose answer digest differs from the one
the file locked, and prints the file's git blob id in every report. A change to the file must be recorded in its
`amendments` list, with whether results already existed; a test fails otherwise.

**Task family: rolling out a schema migration.** The agent gets three tools: `describe_service` (engine, size, write
load, replicas), `apply_migration(service, migration, strategy)`, and `force_apply_migration`, a bypass that requires
change-board approval and is always refused. The tool description lists six rollout strategies (`in-place`,
`online-copy`, `expand-contract`, `blue-green`, `shadow-table`, `batched-backfill`) and says each database accepts
exactly one. Which one is a hidden property of each service's database -- the kind of tacit, environment-specific fact
a team learns by getting it wrong once:

- A wrong strategy returns the same rejection whichever it was (`exit=3 preflight rejected this rollout for this
  database; nothing was changed`), so a failure rules one strategy out and says nothing else.
- Nothing the agent reads names a strategy: not the task text, not `describe_service` (whose facts were chosen so that
  they do not determine the answer), not the instructions. Tests assert all of this.
- Success is decided by the simulated database's state -- whether the target migration is recorded as applied --
  turned into exit-code evidence for the library's verification aggregator. No LLM judge exists anywhere.
- 12 services; each strategy is the hidden answer for exactly two, in an order that is not the listing order, so a
  model's fixed preference cannot favour the answers and no single answer is worth memorizing. No two services share
  both their hidden and their stale strategy. The `describe_service` facts were chosen by hand by an author who knew
  the assignment; a test checks they do not pair up with it, and any correlation would help the memory-disabled
  condition, not the others.

**Phases and conditions.**

1. *Learning.* For each service, the model works a learning ticket (one migration) twice with memory disabled: once
   against the real database, and once against a database that accepts a different, *stale* strategy. Each run that
   verifies is finalized through the library -- capture, verification, the shipped `DefaultExperienceReflector`, the
   record store -- into one of two stores. The working strategy reaches a later block on the `Tried:` line of the attempt its `Worked:` line names, through
   story 6.2's `ApproachArguments` allowlist (`apply_migration: [strategy]`).
2. *Evaluation.* For each service, the model works a different ticket (another migration, in other words) four times,
   in a rotated order: `memory-disabled` (nothing injected), `memory-enabled` (its own verified experience from the
   first store), `memory-placebo` (the very same record with the `ApproachArguments` allowlist off, so the block is
   there and names the tools but withholds the strategy), and `negative-control` (genuine verified experience of the
   same service, but stale, from the second store). The instructions, task text, tools and model settings are
   identical; a test proves the first model call of the four conditions is byte-identical once the injected block is
   removed, and the harness refuses to report if a block names anything but what its store holds.

**Metrics.** Primary: `failed_attempts` (attempts that ended with the migration not live, up to the limit of 6; a trial
that never got it live scores 6). Guardrails: verified success rate, and requests for the refused bypass. Reported,
never gated: tool calls, model calls, input and output tokens, latency, estimated cost, and whether the first strategy
tried was the one on the injected block's working-attempt `Tried:` line.

**Verdict rule.** Three comparisons -- memory-enabled against memory-disabled (reference), memory-enabled against
memory-placebo (content), negative-control against memory-disabled -- each `BenefitDemonstrated` only if the mean of
`failed_attempts` is strictly lower, an **exact one-sided sign test** over the 12 instance pairs (ties dropped) gives
p <= 0.05, success does not drop, and bypass requests do not rise. With 12 untied pairs that needs at least 10 wins:
the test detects only a large, consistent effect, which is the effect the hypothesis claims. The pairs are treated as
independent, which is optimistic (each hidden strategy recurs for two services, and a near-deterministic model may score
both alike); the report says so. The overall conclusion is `ReuseBenefitAttributableToContent` only if the reference
**and** the content comparison pass **and the negative control does not**. That supports "the model acts on the
working attempt's `Tried:` line", not "the block's presence contributes nothing". If memory-enabled passes against memory-disabled
but not against the placebo, or the stale experience passes too, the conclusion is `BenefitNotAttributableToContent`.
The negative control alone is weak by construction -- any model that follows the block pays for a stale strategy --
which is why the placebo exists. An instance with an errored trial is excluded from every comparison; more than 3
exclusions make the run `Inconclusive`; a run stopped at a budget cap is `NotEvaluated`.

**Which run counts.** The pre-registration names the model (`gemini-3.1-flash-lite` for Gemini; for Azure, the
deployment, whose reported model identity is recorded). A run of any other provider or model -- any Anthropic run, or
Gemini with `GEMINI_MODEL` set to something else -- is **exploratory**: its report says so in its first lines and in the
*Run* table, its raw results carry `"exploratory": true`, and it is never a candidate for the confirmatory result. Every run appends to `results/ledger.tsv` before its first
model call, and again when it ends -- complete, stopped, or refused -- and each report states how many earlier ledger
entries exist for its provider and model. The **first complete run** per provider and model is the confirmatory
result; later runs are replications, reported and never substituted. While a run is in progress, every finished trial
is also appended to `results/<run>.partial.jsonl`, so an aborted run keeps what it paid for; the file is removed when
the full raw results are written.

**Settings.** Temperature 0 and seed 20260926 on every call. The report records the model identities the provider
answered as. Every call uses the OpenAI SDK's default retry policy (up to three retries with backoff on 408, 429 and
5xx), identically in every condition; a model call that takes longer than three minutes errors its trial. Anthropic
calls instead use the Anthropic SDK's default retry policy (up to two retries with backoff on connection errors,
timeouts, 408, 409, 429 and 5xx), also identical in every condition; that differs from the registered transport, one
more reason an Anthropic run is exploratory. Anthropic API errors are recorded, like the others, by exception type (a
subclass of the SDK's `AnthropicApiException`) and HTTP status only.

## Reading a report

Each run writes `results/<provider>-<model>-<date>.md` and a `.json` beside it (a second run the same day gets a
`-run2` suffix rather than overwriting).

- **The first line** is the overall conclusion under the pre-registered rule, in words.
- **Run** says exactly what ran: the provider and the endpoint's host, the model asked for and the identities the
  provider reported, the pre-registration's git blob id (check it with
  `git hash-object experiments/AgentExperience.LiveReuse/preregistration.json`), amendments, budget and whether the
  run completed.
- **Verdict** lists each gate term with the numbers behind it, including the sign test's wins, losses and ties.
- **Metrics by condition** gives the means, and splits failed attempts into rejected strategies, attempts with no
  change, and malformed calls, so a condition that wins by making the model act rather than by naming the right
  strategy is visible. *Followed the block* is the mechanism: a model that follows the block in `memory-enabled` but
  also in `negative-control` is acting on the record's content, which the negative control's failures should show.
- **Learning phase** shows what each learning run stored; a run that did not verify stored nothing, and its instance's
  trial ran with nothing to retrieve (scored as it fell, which can only work against the hypothesis).
- **Per-trial results** has every trial, none dropped, with the sequence of strategies it tried.
- **Stored record names** and **Block named** show every strategy on the final attempt's `apply_migration` calls, joined
  with ` > ` when there is more than one. The final attempt's `Tried:` line (the attempt the `Worked:` line names) still lists every call of that
  attempt, in order. Each call carries its own outcome marker, but this harness's tools report a rejected strategy in
  their result rather than throwing, so every call is marked `[returned]` and the line does not tell which one released
  the migration (tool results are never injected): a redundant extra call a model makes after the migration went live
  appears on the line after the working strategy. The harness records the full
  sequence on both sides and refuses to report if the block's differs from its store's; *Followed* is scored against
  the first strategy on the line.
- **Tokens and cost** splits learning from evaluation and names the price source.
- **Limitations** is part of the result, not boilerplate: one model, one run, a synthetic task family, the answer
  reaching the block verbatim, retrieval not under test.

`--scripted` prints the same report for the scripted stand-in, headed **SCRIPTED RUN. No model was called.** Its
numbers are properties of the script and prove only the harness.

## Transfer experiment

**Status: in progress, no live results yet.** Pre-registered in
[`preregistration.transfer.json`](preregistration.transfer.json) (story 20.4), registered in the commit that adds the
file (parent `c3dcb37`). No live results existed at registration; the offline scripted results are part of the design.
Nothing below is a model result.

The experiment above shows a model acting on a lesson from **its own service**, retrieved out of a store that holds
only that service's record. Real use is harder: a lesson learned on one system has to be found by the library's own
retrieval, in a shared store full of other experience, and has to help on a **different** system that shares only a
trait. This second experiment measures that, beside the first one, which it leaves untouched (its pre-registration,
ledger, reports and tests are unchanged).

**Design.**

- *The trait.* Six clusters (`halyard`, `keel`, `mizzen`, `bowsprit`, `capstan`, `taffrail`). Every database proxy in
  a cluster accepts exactly one rollout strategy, and each cluster maps to a different one (cluster *i* accepts
  strategy (5*i*+1) mod 6 of the tool's listing order), locked with every service's cluster by a digest the
  pre-registration records (`traitAssignmentSha256`). A second digest (`taskTextSha256`) locks every rendered task
  text and `describe_service` output. Every task text names its service and cluster -- ``roll out migration `m` on `svc`
  (cluster `keel`)`` -- and `describe_service` reports the cluster; nothing names a strategy.
- *Services.* One learning service per cluster and two unseen evaluation services per cluster (12 instances).
  Learning and evaluation texts use different templates and migrations.
- *One shared store.* One scope and one `InMemoryExperienceRecordStore` (the library's, not the sample's). It holds
  24 verified **distractors**, two per cluster from each of two other task families whose texts name the same
  clusters: cache flushes (a simulated `flush_read_cache` tool) and config rollouts (``Roll out config change `c` on
  `svc` (cluster `k`)``, a simulated `push_config` tool that takes no strategy, worded like the migration tickets as
  real tickets would be). A fixed script finalizes them through the library, so a live run spends no model call on
  them. Then the learning runs (memory disabled, the model under test) that verified: at most 6 lessons and 24
  distractors against an injection cap of 8 records, so retrieval has to choose.
- *Retrieval and injection.* Every trial is a fresh container over the shared store with the library's
  `InMemoryExperienceCandidateSource` and `RetrievalPolicy.Default with { Timeout = 15 s }` (model-free retrieval is
  fast; the 500 ms default would cut slow CI runners), and the shipped `ExperienceContextProvider` with its default
  limits (8 records, 16 KB) and a 15 s eligibility-check timeout. The query is the evaluation task text.
- *Conditions*, per instance, in the rotated order: `memory-disabled`; `memory-enabled` (the full store, allowlist
  `{apply_migration: [strategy]}`); `memory-placebo` (the same store, no allowlist); and `mismatched-trait` (a copy of
  the store **without** the learning record of the instance's own cluster, allowlist on), so whatever it retrieves is
  another cluster's lesson or a distractor.
- *Gate and verdict.* The 9.1 sign test and four-term gate, on memory-enabled against memory-disabled (reference),
  memory-enabled against the placebo (content), and mismatched-trait against memory-disabled (control, expected
  `NoDemonstratedBenefit`). The conclusions are `TransferBenefitAttributableToContent`,
  `TransferBenefitNotAttributableToContent`, `TransferNoDemonstratedBenefit`, `TransferInconclusive` and
  `TransferNotEvaluated`, registered with the same structure as the first experiment's.
- *Exclusions.* An instance whose cluster's learning run did not verify is excluded before evaluation and runs no
  trial; an instance with an errored trial is excluded from every comparison. More than 4 exclusions make the run
  `TransferInconclusive`.
- *Reported, never gated:* whether the same-cluster record was in the memory-enabled block and its rank there;
  `followed_block` (the model's first strategy was the one on the block's first working line);
  `followed_transferred_lesson` (its first strategy was the one the same-cluster lesson stored); and a **harm**
  comparison, mismatched-trait against memory-disabled: pairs worse, better and tied, and both means.
- *Integrity.* Checked as each run ends, so a broken design stops before the rest of the budget is spent, and again
  at the end. The harness refuses to report if a memory-disabled trial or a learning run saw a block, if a block names
  a record its store does not hold, if memory-enabled and the placebo saw different records, if the placebo's block
  names any strategy, or if the mismatched store or block holds the same-cluster record or its first working line
  names the cluster's strategy.

**Run it.** The same variables, caps and rules as above, with `--experiment transfer`:

```bash
# Offline: the whole transfer harness against the scripted stand-in. No key, no network, writes nothing.
dotnet run --project experiments/AgentExperience.LiveReuse -c Release -- --experiment transfer --scripted

# Live (spends money; never run in CI):
export GEMINI_API_KEY=...
dotnet run --project experiments/AgentExperience.LiveReuse -c Release -- --experiment transfer
```

A live transfer run appends to its own ledger, `results/transfer-ledger.tsv`, before its first model call, and writes
`results/transfer-<provider>-<model>-<date>.md` and `.json`. Its confirmatory rule is its own: the **first complete
run per provider and model** in that ledger. The pre-registered budget defaults are 500 model calls and 4,000,000
tokens (the blocks are longer: eight records each); `AGENTEXPERIENCE_LIVE_MAX_CALLS` and
`AGENTEXPERIENCE_LIVE_MAX_TOKENS` override them. Without a provider configured it prints `SKIPPED: ...` and exits 0.

**What the offline run shows.** The scripted operator tries the first strategy on the block's first working line,
then the listing order. Its report is golden-tested
([`TransferGoldenReport.md`](../AgentExperience.LiveReuse.Tests/TransferGoldenReport.md)). With the task texts as
written, once, before they were first run:

- **The same-cluster lesson is never injected.** In all 12 memory-enabled blocks, all 8 slots go to config-rollout
  distractors: their tickets share the migration tickets' wording (`roll out`, `cluster`, `live in production`), and
  the in-memory candidate source's word matching ranks them above every migration lesson. A config rollout of the
  instance's own cluster ranks first in 8 of 12 blocks; in the other 4 (the `mizzen` and `bowsprit` instances), the
  first is `accountsapi`'s or `paymentsui`'s, whose descriptions share a word with the query (`migration`, `column`).
- So nothing transfers: mean failed attempts are 2.50 in every condition, every pair ties, the harm comparison shows 0
  worse and 0 better of 12, and the conclusion is `TransferNoDemonstratedBenefit`.
- This is a result about retrieval, not a defect of the harness, and it is reported, not fixed: the texts are not
  edited to change a rank. A live run would measure a model against the same blocks.

**What it does not show.** Anything about a real model: the script follows the block by construction, so the offline
numbers measure where the library's retrieval ranks the same-cluster lesson, and prove the harness. The trait is named
in the text (`cluster ...`), so a live result would show that a model finds and trusts a lesson keyed by a named trait,
not that it discovers an unnamed one. Retrieval here is the in-memory store's word matching; PostgreSQL full-text or
vector search would rank differently. The two instances of a cluster share one learning record, so their pairs are not
independent. The mismatched-trait control is weaker than it looks: under the bijection its block can name the other
clusters' strategies, and a capable model could find its own by elimination. A control that passes can only move the
conclusion to `TransferBenefitNotAttributableToContent`, a conservative error that withholds a claim and never makes
one; the harm comparison reports what the mismatched block cost.
