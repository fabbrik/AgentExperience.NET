# Live reuse experiment

Every test in this repository, the end-to-end sample and story 4.4's reuse baseline drive a scripted `IChatClient`.
They prove the plumbing: that a Historical Reference reaches the model's context and that the gate can say no. None of
them can show the library's premise with a real model. This experiment runs 4.4's methodology against one:

> A model that failed, once its experience has been captured, verified, reflected on, stored and injected as a
> Historical Reference, needs fewer failed attempts on a later, different task on the same system -- fewer than
> with memory disabled and fewer than when shown the same block with the working strategy withheld -- and gets no
> such help when the injected experience is stale.

**Status: no live result has been recorded yet.** The harness, the pre-registration and the offline tests are in place;
`results/` will hold the first report once someone runs it with a key. Nothing in this repository claims a live result
until a report under `results/` says so.

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

# Offline: the whole harness against the scripted stand-in. No key, no network, writes nothing.
dotnet run --project experiments/AgentExperience.LiveReuse -c Release -- --scripted
```

With no provider variable set, the command prints `SKIPPED: ...`, exits 0 and spends nothing. It never runs in CI:
`dotnet test` does not run console apps and no workflow invokes this project (the repository's `WorkflowTests` forbid
secrets in every workflow). The harness itself is proven offline by `experiments/AgentExperience.LiveReuse.Tests`,
which is part of the ordinary `dotnet test` run.

| Variable | Meaning |
| --- | --- |
| `AGENTEXPERIENCE_LIVE_PROVIDER` | `gemini` or `azure`. Optional when only one provider's variables are set. |
| `GEMINI_API_KEY` | Gemini API key. |
| `GEMINI_MODEL` | Optional. Default `gemini-3.1-flash-lite`, Google's stable low-cost model (checked 2026-09-26). |
| `AZURE_OPENAI_ENDPOINT` | `https://<resource>.openai.azure.com/` (the `/openai/v1/` path is appended if absent). |
| `AZURE_OPENAI_API_KEY` | Azure OpenAI key. |
| `AZURE_OPENAI_DEPLOYMENT` | The deployment name to call. |
| `AGENTEXPERIENCE_LIVE_MAX_CALLS` | Hard cap on model calls for the whole run. Default 600 (pre-registered). |
| `AGENTEXPERIENCE_LIVE_MAX_TOKENS` | Hard cap on input plus output tokens. Default 2,000,000 (pre-registered). |
| `AGENTEXPERIENCE_LIVE_MIN_CALL_INTERVAL_MS` | Optional pacing between calls, for a free tier's requests-per-minute limit. |
| `AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK`, `..._OUTPUT_PER_MTOK` | USD per million tokens for the cost estimate. Built in only for `gemini-3.1-flash-lite` ($0.25 / $1.50, standard paid tier). |
| `AGENTEXPERIENCE_LIVE_RESULTS_DIR` | Optional. Default `experiments/AgentExperience.LiveReuse/results`. |

**Keys.** The key is read from the environment, handed to the OpenAI SDK's `ApiKeyCredential`, and never stored
anywhere else: the configuration type's `ToString` redacts it, the report carries the endpoint's host only (for Azure,
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
variables to get an estimate in the report.

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
   record store -- into one of two stores. The working strategy reaches a later block on the `Approach:` line through
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
tried was the one on the injected block's `Approach:` line.

**Verdict rule.** Three comparisons -- memory-enabled against memory-disabled (reference), memory-enabled against
memory-placebo (content), negative-control against memory-disabled -- each `BenefitDemonstrated` only if the mean of
`failed_attempts` is strictly lower, an **exact one-sided sign test** over the 12 instance pairs (ties dropped) gives
p <= 0.05, success does not drop, and bypass requests do not rise. With 12 untied pairs that needs at least 10 wins:
the test detects only a large, consistent effect, which is the effect the hypothesis claims. The pairs are treated as
independent, which is optimistic (each hidden strategy recurs for two services, and a near-deterministic model may score
both alike); the report says so. The overall conclusion is `ReuseBenefitAttributableToContent` only if the reference
**and** the content comparison pass **and the negative control does not**. That supports "the model acts on the
`Approach:` line", not "the block's presence contributes nothing". If memory-enabled passes against memory-disabled
but not against the placebo, or the stale experience passes too, the conclusion is `BenefitNotAttributableToContent`.
The negative control alone is weak by construction -- any model that follows the block pays for a stale strategy --
which is why the placebo exists. An instance with an errored trial is excluded from every comparison; more than 3
exclusions make the run `Inconclusive`; a run stopped at a budget cap is `NotEvaluated`.

**Which run counts.** The pre-registration names the model (`gemini-3.1-flash-lite` for Gemini; for Azure, the
deployment, whose reported model identity is recorded). Every run appends to `results/ledger.tsv` before its first
model call, and again when it ends -- complete, stopped, or refused -- and each report states how many earlier ledger
entries exist for its provider and model. The **first complete run** per provider and model is the confirmatory
result; later runs are replications, reported and never substituted. While a run is in progress, every finished trial
is also appended to `results/<run>.partial.jsonl`, so an aborted run keeps what it paid for; the file is removed when
the full raw results are written.

**Settings.** Temperature 0 and seed 20260926 on every call. The report records the model identities the provider
answered as. Every call uses the OpenAI SDK's default retry policy (up to three retries with backoff on 408, 429 and
5xx), identically in every condition; a model call that takes longer than three minutes errors its trial.

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
- **Tokens and cost** splits learning from evaluation and names the price source.
- **Limitations** is part of the result, not boilerplate: one model, one run, a synthetic task family, the answer
  reaching the block verbatim, retrieval not under test.

`--scripted` prints the same report for the scripted stand-in, headed **SCRIPTED RUN. No model was called.** Its
numbers are properties of the script and prove only the harness.
