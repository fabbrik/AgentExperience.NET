# AgentExperience for Python

A pure-Python core of AgentExperience that implements the [language-neutral contract](https://github.com/fabbrik/AgentExperience.NET/blob/main/contract/README.md), so a
lesson learned by a Python agent is stored in the format the .NET library reads, and the other way round.

**Status: alpha, contract 1.14, with a Microsoft Agent Framework integration (the `maf` extra).** It passes every
fixture of contract 1.14 (and so every 1.0 to 1.13 fixture). It is published to PyPI as `agenticexperience` (the import name stays `agentexperience`), with every
release of this repository and at the same version as the .NET packages: .NET `0.1.0-preview.N` is
`agenticexperience` `0.1.0aN` (a PEP 440 alpha; while every release is an alpha, pip and uv install the newest one).
Like the .NET packages, it claims no production readiness, and its API may change between alphas.

```bash
pip install "agenticexperience[maf]"        # with Microsoft Agent Framework
pip install "agenticexperience[postgres]"   # with the PostgreSQL store
```

## Quick start with Microsoft Agent Framework

Install with the `maf` extra (`pip install "agenticexperience[maf]"`, which brings `agent-framework-core` 1.0.0 or
later, or `pip install ".[maf]"` from this directory); bring `client` (any MAF chat client) and `run_tests` (your test
run, returning text such as `exit=0`). Development and tests only: see [In production](https://github.com/fabbrik/AgentExperience.NET/blob/main/python/README.md#in-production). After the two imports, five lines: one call that
wires the agent (three lines), and two runs.

```python
from agent_framework import Agent
from agentexperience.maf import InMemoryExperienceStore, development_identity, exit_code, tool_succeeded, use_agent_experience

agent = use_agent_experience(Agent(client=client, name="triage-ticket", tools=[run_tests]),
    store=InMemoryExperienceStore(), identity=development_identity(),  # one local identity: development and tests only
    verify=tool_succeeded("run_tests"), classify_tool_result=exit_code("run_tests"))  # your check, never the model
await agent.run("Ticket #4812: a refund is stuck on a lock. Triage it.")
await agent.run("Ticket #4977: a refund is stuck on a lock again. Triage it.")  # gets the lesson
```

The second run's model is handed the Historical Reference block, as one user-role message, which names both of the
first run's test runs:

```text
Lesson: Verified after 1 attempt. Worked: attempt 0. Checks: [run_tests].
Tried:
  - attempt 0: run_tests [failed: exit 3], run_tests [returned] → completed
```

[`samples/quickstart.py`](https://github.com/fabbrik/AgentExperience.NET/blob/main/python/samples/quickstart.py) is based on this snippet and runs it with no key and no network:
`uv run python samples/quickstart.py`. It differs in what a runnable, repeatable demo needs: the model is a scripted
stand-in (`ScriptedModel`, which calls `run_tests` until a call returns `exit=0`), `run_tests` is a small class whose
first call of each run returns `exit=3`, the identity names its application (`development_identity(application=
"quickstart")`), the recorded environment is fixed (`environment=...`) so the output is the same on every machine, and it
prints each run and the block. Its output is checked in as
[`samples/quickstart.expected.txt`](https://github.com/fabbrik/AgentExperience.NET/blob/main/python/samples/quickstart.expected.txt), and a test compares them.

### What the one call wires

- **Capture**: agent-run middleware and function middleware. Each non-streaming invocation is one run of one attempt:
  every tool call with its arguments (sanitized when stored: only allowlisted values are kept, secrets redacted), its
  result, and its error. A tool that raises is recorded by the exception's type name only, never its message. A tool
  that reports failure in its return value is recorded as failed when `classify_tool_result` names a class for it;
  a class that is not a single error-class token is recorded as `ToolReportedError`. When the invocation ends, `verify`
  decides from your own facts; a verification is finalized through the core (the deterministic lesson, the derived
  IDs) and stored. Without `verify` nothing is stored. A streaming invocation is injected but not captured.
- **Injection**: a context provider. It derives the task text from the invocation's messages (the contract's rule),
  searches the store in the identity's scope, ranks with the contract's weights, renders the block with
  `block_options` (the contract's options and limits), and adds it as one user-role message stamped
  `additional_properties["AgentExperience.HistoricalReference"] = True` (`message_role="system"` sends it as a system
  message). An agent that already holds an `ExperienceContextProvider` gets no second one; `inject=False` captures
  without injecting.
- **Semantic retrieval** (contract 1.6), with an embedder you register (below). Injection then runs the text and the
  vector channel together, merges them and ranks as before; each stored record's summary is embedded in the
  background.
- **Injection tracking** (contract 1.4). Once a block is handed over, a store with `record_injection` (the PostgreSQL
  and in-memory stores) is told which records it delivered, so the .NET lessons view shows when each lesson was last
  injected, by a Python agent too. It runs in a background task on the event loop, bounded by
  `injection_record_timeout` (5 seconds, .NET's), and never delays or fails the invocation; `on_injection_recorded`
  reports how it ended (`Recorded`, `NotTracked`, `NotRecorded` or `OutcomeUnknown`, as .NET's `record_injection`
  operation). `await provider.wait_for_injection_records()` awaits the ones still running, for a host shutting down.
- **The task text, and where history sits.** Capture records the task text of the invocation's own input, as .NET's
  agent-level capture does. Injection derives it from the messages the provider sees: the new input, plus the
  replayed history of a history provider that runs *before* it. The provider is appended to the agent's
  `context_providers`, and the `InMemoryHistoryProvider` MAF adds by itself for a session is appended after it, so by
  default injection sees only the new input, like .NET's agent-level wiring; add a history provider to the agent
  before calling `use_agent_experience` and a short follow-up ("and retry") is searched joined after the previous
  request, like .NET's `ChatClientAgent`-level provider. The stored task summary is then the follow-up alone. Either
  way each request carries exactly one block: a replayed message stamped as the block never counts as a request.
- **The run's end, as in .NET.** An invocation that raises ends its run `Failed` (the error is the exception's type
  name) and one that is cancelled ends it `Cancelled`; `verify` still decides whether that run is stored, and the
  exception or cancellation then reaches the caller unchanged. A middleware raising `MiddlewareTermination` ends the
  run `Completed` with the result it set. Finalization is bounded by `finalization_timeout` (5 seconds, .NET's
  `FinalizationTimeout`), on its own clock: past it the work is abandoned and reported, so a cancellation is never held
  up longer than that.
- **Nothing raises into the agent.** A store, identity, classifier or verifier failure means no block or no stored
  record, reported through `on_capture_failure` or `on_injection_failure` (the reason never quotes record content);
  a failed injection record is reported through `on_injection_recorded`.
  Only the invocation's own cancellation and the tool's own exceptions propagate, unchanged.

**Verifiers** (`agentexperience.verifiers`, re-exported by `agentexperience.maf`) have the semantics of .NET's
`ExperienceVerifiers`: `tool_succeeded(tool)` (the last call to the tool decides), `check(id, fn)` (your own check:
`True`, `False`, or `None` for "do not store"), `approval(id, fn)` (a person's decision, by an opaque approver ID),
`all_of(...)` (every check; stops at the first `None`) and `any_of(...)` (the first that passes). **Classifiers**
(`agentexperience.classifiers`) have the semantics of `ExperienceToolResultClassifiers`: `exit_code(*tools)` and
`http_status(*tools)` read a JSON object's top-level fields, else the last match in the text.

### Semantic retrieval: registering an embedder

Without an embedder retrieval is text only, and a reworded query finds nothing it shares no words with. Register an
embedding callable (any model: none is a dependency) and retrieval becomes hybrid:

```python
class Embedder:
    model_id = "my-embedding-model"  # stored with every vector; vectors of another model are never compared
    dimension = 1536                 # the vector's length, known without calling the provider

    def embed(self, text: str) -> list[float]:  # or `async def`; exactly `dimension` finite numbers
        return my_provider.embed(text)

agent = use_agent_experience(agent, store=store, identity=identity, verify=verify, embedder=Embedder())
```

- **In-memory and PostgreSQL stores.** `InMemoryExperienceStore` compares 32-bit floats by cosine distance.
  `PostgresExperienceStore` needs PostgreSQL with pgvector, the .NET vectors package's migrations (`0004`, `0020`) and
  the grants of `sql/application-role-privileges-1.6.sql` after the 1.0 and 1.4 files (or the .NET privileges call).
- **Fail-open, within the retrieval timeout.** The vector channel never fails the text answer. The retrieval policy's
  `timeout` (500 ms by default) bounds both channels together, as .NET's does: a vector channel still running at it
  times the injection out (`RetrievalTimedOut`). `vector_timeout` (seconds, default 0.5, covering the embedding and the
  search) is the channel's own fallback only when it is strictly shorter than that timeout; past it the injection
  answers from text. When it does not contribute the injection answers from text and reports
  the reason through `on_vector_fallback(VectorFallback)` and `InjectionResult.vector_fallback`: `NotConfigured` (no
  embedder, or a store without `write_embedding` and `search_vectors`), `ProviderUnavailable` (the embedder raised, ran
  out of time, or returned a blank model ID or an empty, wrong-length or non-finite vector; no statement runs),
  `ModelMismatch` or `DimensionMismatch` (every stored vector is of another model or width) and `VectorSearchFailed`.
  A failure of the text search still fails the injection, as without an embedder. `min_vector_relevance` (0 to 1) drops
  vector candidates below it; text candidates are never dropped.
- **Writing embeddings.** After a record is stored, a background task embeds its summary (task ID, task summary and
  lesson) and writes it, only while the record is still at that revision. It never runs inside the record's own commit
  and never fails capture: a failure is reported to `on_capture_failure` with the stage `Index`.
  `await settings.wait_for_index_writes()` (also on the provider) awaits the ones still running. Records stored before
  an embedder was registered have no vector until one is written; the .NET library's re-index does it for a shared
  database.

### The retrieval policy (contract 1.12)

Injection retrieves under .NET's `RetrievalPolicy` and, optionally, its `ConfidenceDecayPolicy`, with .NET's defaults
and validation, so a Python agent configured like a .NET one ranks exactly as it does:

```python
from datetime import timedelta
from agentexperience import ConfidenceDecayPolicy, RetrievalPolicy

agent = use_agent_experience(
    agent, store=store, identity=identity, verify=verify,
    retrieval_policy=RetrievalPolicy(max_age=timedelta(days=180), recency_half_life=timedelta(days=14)),
    confidence_decay=ConfidenceDecayPolicy(
        half_lives={"framework-api": timedelta(days=30), "math": None},  # None: never decays
        default_half_life=timedelta(days=365),
    ),
)
```

| Python (`RetrievalPolicy`) | .NET (`RetrievalPolicy`) | Default |
| --- | --- | --- |
| `timeout` (`timedelta`, more than zero, at most a day) | `Timeout` | 500 ms |
| `minimum_confidence` (0 to 1) | `MinimumConfidence` | 0.5 |
| `max_age` (`timedelta` or `None`) | `MaxAge` | `None`: no expiry |
| `recency_half_life` | `RecencyHalfLife` | 30 days |
| `candidate_limit` (1 to 199) | `CandidateLimit` | 50 |
| `max_abandoned_searches` | `MaxAbandonedSearches` | 16 |
| `minimum_matched_terms` | `MinimumMatchedTerms` | 3 |
| `minimum_vector_relevance` (0 to 1 or `None`) | `MinimumVectorRelevance` | `None` |

| Python (`ConfidenceDecayPolicy`) | .NET (`ConfidenceDecayPolicy`) | Default |
| --- | --- | --- |
| `domain_key` | `DomainKey` | `"domain"` |
| `half_lives` (domain to `timedelta` or `None`; copied, read-only) | `HalfLives` | empty |
| `default_half_life` | `DefaultHalfLife` | `None`: no decay |

- **Validation.** An invalid value raises `RetrievalPolicyError` (a `ValueError`) when the policy is built; its
  `option` is the option .NET's `ArgumentException` names (`Timeout`, `HalfLives`, ...). NaN is refused.
- **The search** is asked for `minimum_confidence`, `candidate_limit + 1` rows (`SearchParameters.for_policy`) and
  `minimum_matched_terms`; the vector channel for the same floor and limit. `min_vector_relevance`, when set,
  replaces the policy's `minimum_vector_relevance`, as .NET's `MinimumSemanticSimilarity` does in its one-call setup.
- **Ranking** (`rank(..., policy=, confidence_decay=)`, and `rank_hybrid`) considers the first `candidate_limit`
  candidates of each channel and reports what it excluded, in order, on `RankedResult.excluded` and
  `InjectionResult.excluded` (.NET's `Excluded`): `IneligibleStatus`, `ModelAuthored`, or `Expired` (last activity,
  `updated_at`, more than `max_age` ago). The `Confidence` component decays by the record's domain (its environment
  metadata entry named `domain_key`) from `created_at`, and then carries the stored value as `undecayed_value` (.NET's
  `UndecayedValue`); decay never excludes. With no arguments `rank` ranks exactly as before.
- **Bounded.** The whole retrieval (the text search and the vector channel together) is bounded by `timeout`: past it
  nothing is injected and the injection reports `RetrievalTimedOut` (`retrieve` outcome `TimedOut`), with no failure.
  The search keeps running on its own daemon thread and counts as abandoned until it ends
  (`provider.abandoned_searches`); while `max_abandoned_searches` are still running, an injection reports
  `RetrievalTimedOut` at once without searching, with an `InjectionFailure` naming the cap. An identity whose bounds do
  not permit its scope is `RetrievalDenied` (`retrieve` outcome `Denied`) before anything is searched or embedded. Both
  are reported to `on_injected` too, with nothing delivered. The caller's own cancellation still propagates.

| .NET | Python |
| --- | --- |
| `RetrievalOutcome` `Completed`, `TimedOut`, `Denied`, `Failed` | the `retrieve` operation's outcome (`RetrievalOutcome`) |
| `InjectionOutcome` `RetrievalTimedOut`, `RetrievalDenied`, `RetrievalFailed` | the `inject` operation's outcome, and `InjectionResult.outcome` |
| `RetrievalExclusionReason` `IneligibleStatus`, `ModelAuthored`, `Expired` | `ExcludedRecord.reason` (`ExclusionReason`); never `EnvironmentMismatch` or `UnconfirmedContent` (no required attributes, no provenance signing) |

The lessons view applies the same policy (`agent_experience_lessons` takes the setup's): a lesson below
`minimum_confidence` is `BelowConfidenceFloor`, naming that floor, and one whose last activity is more than `max_age`
before the view's clock is `Expired`.

### The injection options (contract 1.13)

The rest of .NET's `ExperienceInjectionOptions` that needs no session tracking, with .NET's defaults and validation, so
a Python agent configured like a .NET one injects the same block with the same omissions:

```python
from agentexperience import ReceivingAgent
from agentexperience.maf import InjectionDecision, InjectionRequest

async def resolve(context):  # or a plain function
    if context.derived_task_text is None:
        return None  # skip this invocation
    return InjectionRequest(context.derived_task_text, correlation_id=trace_id)

agent = use_agent_experience(
    agent, store=store, identity=identity, verify=verify,
    receiving_agent=ReceivingAgent(
        available_tools={"read_ledger", "wait_for_lock", "retry_refund"},
        max_risk_class="Medium",
        tool_risk_classes={"read_ledger": "Low", "wait_for_lock": "Low", "retry_refund": "Medium"},
    ),
    model_authored_lessons="Exclude",
    resolve_request=resolve,
    decide_injection=lambda decision: InjectionDecision.permit()
    if risk_policy.allows(decision.current)
    else InjectionDecision.deny("risk policy"),
)
```

| .NET (`ExperienceInjectionOptions`) | Python (`use_agent_experience`) | Default |
| --- | --- | --- |
| `ReceivingAgent` (`ReceivingAgentCapabilities`) | `receiving_agent` (`ReceivingAgent`) | `None`: nothing gated |
| `AvailableTools`, `MaxRiskClass`, `ToolRiskClasses`, `UseRunTools`, `GatedRetrievalWindowMultiplier` | `available_tools`, `max_risk_class`, `tool_risk_classes`, `use_run_tools`, `gated_retrieval_window_multiplier` | `None`, `None`, empty, `False`, 4 |
| `ToolRiskClass` `Low`, `Medium`, `High`, `Critical` | `ToolRiskClass`, the same names as strings, in that order | |
| `ModelAuthoredLessons` (`ModelAuthoredLessonPolicy`) | `model_authored_lessons`, `"Include"` or `"Exclude"` (`exclude_model_authored=True` is the same as `"Exclude"`) | `"Include"` |
| `ResolveRequest`, `ResolveRequestAsync` | `resolve_request`: one callable, sync or async, given a `RequestContext` (agent, session, messages, `derived_task_text`, identity), returning an `InjectionRequest` (`task_text`, `preferred_environment`, `limit`, `exclude_model_authored`, `correlation_id`) or `None` | `None`: the derived task text |
| `DecideInjection` (`InjectionDecision.Permit`, `Deny`) | `decide_injection`: given a `DecisionContext` (`candidate`, `current`, `model_authored`), returning `InjectionDecision.permit()` or `.deny(reason)` | `None`: permit |
| `ApproachArguments`, `FailureDetail`, `Rendering`, `Limits.MaxRecords`, `Limits.MaxBytes` | `block_options` (`approach_arguments`, `failure_detail`, `rendering`, `max_records`, `max_bytes`) | the contract's |
| `MessageRole` | `message_role`, `"user"` or `"system"` | `"user"` |

- **The gate** checks the tools of a record's approach (its verified final attempt's calls, as its `Tried:` line shows
  them): a tool missing from `available_tools` omits it as `ToolUnavailable`, then a tool whose class (a tool not in
  `tool_risk_classes` is `Critical`) is above `max_risk_class` as `RiskClassExceeded`, with no detail. It runs before
  the record limit, so a gated record takes no slot. A blank tool name, an undefined class or a multiplier below 1
  raises `ValueError` when the `ReceivingAgent` is built; it copies the collections it is given. A `max_bytes` that
  does not exceed the verbose block's fixed overhead (754 bytes) is refused when the agent is built, as .NET refuses it.
- **`use_run_tools`** checks against the tools the provider is handed: the agent's own tools and those an earlier
  context provider added. agent-framework does not hand a context provider the `tools=` of one `run` call, nor an MCP
  server's functions, so a record whose approach calls one of those is withheld (where .NET's agent-level provider sees
  a run's own tools).
- **The resolver** is given the messages the task text is derived from (the replayed history, earlier context
  providers' messages, then the new input). `None` skips the invocation (`inject` outcome `Skipped`); a raise injects
  nothing and is reported through `on_injection_failure` (`Failed`); a cancellation propagates only when the invocation
  itself is being cancelled (on Python 3.10, which cannot tell, every cancellation does), otherwise it is `Failed`.
  Every attempt also reaches `on_injected`, as .NET's `OnContextInjected`: `Skipped` (no identity, or the resolver
  returned `None`), `Failed` and `RetrievalFailed` included, with nothing delivered. `InjectionResult` has no
  `CorrelationId` or `Failure` field (.NET's do): the correlation ID is on the span, and the failure goes to
  `on_injection_failure`. A request retrieval refuses (a
  blank task text, one over 4,096 UTF-16 code units, a `limit` outside 1 to the policy's `candidate_limit`) is
  `RetrievalFailed`. The `limit` cuts the ranking (widened while a gate can reject, as .NET's gated window) and caps
  what is injected; `correlation_id` is the `inject` span's `agentexperience.correlation_id`. The authorization and
  scope are the identity's.
- **The decision** is asked once per selected record, after the selection and before the byte budget. A denial omits
  the record as `HostDenied` with its reason (or .NET's default detail); it fails closed (a raise, `None` or anything
  but an `InjectionDecision` denies), and a denied record keeps its slot. It is synchronous, as .NET's: an `async`
  callable is refused when the agent is built, and one that returns an awaitable denies.
- The pure parts are public: `ReceivingAgent.gate`, `CapabilityGate`, `gated_window`, `approach_tool_calls`, and
  `select_block(..., gate=, decide=)`. A `use_run_tools` gate checks only once `for_invocation(tools)` has narrowed it;
  checked directly it knows no tool, so it fails closed (`ToolUnavailable` for any approach that calls a tool).

### See what it learned: the lessons view

A read-only list of what was learned in a scope (contract 1.7), as .NET's `AgentExperienceLessons` lists it:

```python
from agentexperience import LessonsQuery
from agentexperience.maf import agent_experience_lessons

lessons = agent_experience_lessons(agent)               # or its ExperienceSettings
page = lessons.list(query=LessonsQuery(limit=20))       # the setup's own identity; list(identity) for another
for lesson in page.lessons:                             # newest first
    print(lesson.status, lesson.confidence, lesson.is_eligible, lesson.ineligible_reason, lesson.last_injected_at)
```

- **Each lesson** has its status, confidence and `checks` (check ID, evidence kind, result; never the evidence's
  detail), `block_text` (the Historical Reference block for that record alone, numbered 1, with no ranking, byte for
  byte what .NET renders), whether retrieval would use it (`is_eligible`, with `ineligibility` and `ineligible_reason`
  when not), and `last_injected_at` and `injection_count`.
- **Eligibility codes** this port decides, in .NET's order: `Status`, `BelowConfidenceFloor` (the retrieval policy's
  `minimum_confidence`, 0.5 by default), `ModelAuthored` (when the setup's `exclude_model_authored` is on), `Expired`
  (last activity more than the retrieval policy's `max_age` before the view's clock, contract 1.12), `OverByteBudget`
  (the block options' `max_bytes`) and `Unrenderable`. .NET's `ContentUnconfirmed` does not exist here: this port has
  no provenance signing.
- **The page** has `outcome`, `generated_at`, `has_more`, `out_of_scope_dropped` and `injections_tracked`. When it is
  false (a store that records no injections, or a PostgreSQL role without the 1.4 grants or table), a `None`
  `last_injected_at` means "not known", not "never". A scope outside the authorization bounds is an empty page with the
  outcome `Denied`, never an exception; an invalid `LessonsQuery` (`limit` outside 1 to 500, empty or unknown
  `statuses`) raises `ValueError`. One record that cannot be rendered is listed as `Unrenderable`, and the rest still are.
- **Reads only**, under the caller's authorization bounds and in the caller's own scope (no sharing grants in contract
  1.x). Build one without the setup with `ExperienceLessons(store, block_options=..., exclude_model_authored=...)` and
  `list_scope(bounds, scope, query)`. The view discloses lessons: serve it only behind your own authorization. It
  renders as HTML and exports what helps (contract 1.14, below).
- **Stores.** `InMemoryExperienceStore` and `PostgresExperienceStore` have `list_lessons_page`; PostgreSQL runs the
  contract's `sql/list-experience-lessons.sql` as written and needs no grants beyond 1.0 and 1.4.
- **What was measured** (contract 1.10). `LessonsQuery(include_measured_benefit=True)` fills each lesson's
  `measured_benefit` (a `LessonBenefit`: the comparison and, with the policy's `per_model`, the model pairs) from one
  estimate for the scope over the setup's `BenefitPolicy` `window` and `minimum_runs_per_arm` (30 days and 20 by
  default, whether or not the policy is on), so a lesson omitted as `MeasuredHarmful` shows the `Hurts` behind it.
  `None` for a lesson no assigned run in the window named, and for every lesson when the store does not read the
  holdout. A store failure on that read raises the store's own error (on PostgreSQL the driver's, whose telemetry
  error class is `Infrastructure`, as .NET's `ExperienceStoreException`). Off by default: then nothing more is read.

### Exporting measured lessons (contract 1.14)

`export_measured` hands a sink every lesson the injection holdout measured as helping, as .NET's
`AgentExperienceLessons.ExportMeasuredAsync` does, so a copy kept elsewhere (an instruction file an agent loads, another
memory store) stays in step with what was learned. The built-in sink keeps one Markdown file, byte for byte what .NET's
`MarkdownLessonFileSink` writes:

```python
from agentexperience import MarkdownLessonFileSink

result = agent_experience_lessons(agent).export_measured(MarkdownLessonFileSink("AGENTS.lessons.md"))
print(result.outcome, len(result.lessons), result.sync)   # Found 3 LessonSyncResult(added=3, updated=0, removed=0)
```

- **The set**: lessons in exactly the identity's scope, eligible for retrieval, whose record's outcome is `Verified` and
  whose verdict is `Helps` (one estimate over the benefit policy's window, at `options`'s minimum runs per arm, the
  policy's when `None`), sorted by experience ID. A store that does not track the holdout gives an empty set; the sink
  is still called, so stale copies go. A scope outside the bounds returns that outcome and the sink is not called.
  More reusable lessons than one read answers (500) raises `RuntimeError`, as .NET refuses. Nothing exports
  automatically and nothing is written to the store; `export_measured_async` runs it on a worker thread.
- **Your own sink**: anything with `sync(scope, current) -> LessonSyncResult` (`LessonSink`): add what it lacks, update
  what changed, remove what is no longer in `current`. What it raises propagates.
- **The Markdown file** is rewritten whole on every export, atomically (a temporary file in the same directory, then
  `os.replace`; a failure removes it), UTF-8 without a BOM, LF only. Everything drawn from a record is an indented code
  block, so no lesson can add a heading or a link. The file gets .NET's mode, read and write for everyone less the
  umask (`0644` under the usual umask), never owner-only. Reading the file before, a leading byte order mark is dropped
  and a heading's ID is trimmed of white space, as .NET reads them. The path must not be blank and is made absolute;
  like .NET, the sink confines it to nothing, so check a path you take from configuration you do not trust. The
  directory must exist.

### The lessons page as HTML (contract 1.14)

`render_lessons_html(page)` is .NET's `ExperienceLessonsHtml.Render`, byte for byte: one self-contained document (inline
CSS, no script, no external request) with every record value encoded as .NET's `WebUtility.HtmlEncode` encodes it.
`lessons_html_response(lessons, identity, method=...)` builds a response any framework can send, ASGI or WSGI:
`status`, `headers` and `body`, as the .NET endpoint of the deployment guide answers. A `GET` gets the page with
exactly `Content-Type: text/html; charset=utf-8`, `Cache-Control: no-store`, `X-Frame-Options: DENY` and a
`Content-Security-Policy` that allows no script and no framing; any other method, `HEAD` included, is 405 with
`Allow: GET`. `identity` is your own (an `ExperienceIdentity`, or a function resolving it from your authentication):
none is 403, and a resolution or read that raises is 500, neither with a body that says why. **The package maps no route and authorizes nothing**: take the identity
from your own authentication, never from the request, and serve it only behind your own authorization; the page
discloses every lesson in the scope.

```python
from agentexperience import lessons_html_response

async def lessons_page(scope, receive, send):                  # a bare ASGI app, behind your own auth middleware
    response = lessons_html_response(
        agent_experience_lessons(agent), lambda: identity_from(scope), method=scope["method"]
    )
    headers = [(name.lower().encode(), value.encode()) for name, value in response.headers]
    await send({"type": "http.response.start", "status": response.status, "headers": headers})
    await send({"type": "http.response.body", "body": response.body})
```

### Writing lessons with a model (contract 1.14)

The deterministic reflector stays the default. `ChatClientExperienceReflector` (`agentexperience.chat_reflector`, the
`maf` extra) asks an agent_framework chat client for a verified run's lesson, as .NET's
`ChatClientExperienceReflector` does: the same system prompt and data message, request options, reading and failure
classes. It is opt-in:

```python
from agentexperience import ChatReflectorOptions
from agentexperience.chat_reflector import ChatClientExperienceReflector

agent = use_agent_experience(
    agent,
    store=store,
    identity=identity,
    verify=tool_succeeded("run_tests"),
    reflector=ChatClientExperienceReflector(chat_client, ChatReflectorOptions(model_name="gpt-4o", timeout=4.0)),
    finalization_timeout=5.0,                                  # the model call counts against it
)
```

- **Privacy.** It sends sanitized captured run content to your provider: the task text (or ID), each attempt's ordered
  tool names, each call's and attempt's result and error (clipped to `max_quoted_length`), and the verification status
  with the check and evidence IDs. Never tool arguments, evidence detail, the environment or the scope.
  `build_prompt(request)` returns the exact data message.
- **Its lessons are model-authored**: stored with `authorship` `Model`, screened by the content guard
  (`agentexperience.content_guard`: credential shapes, mixed-script words, instruction-override phrasing, `data:` and
  other always-refused links, and any URL, hostname or IP address the run did not show it), fenced at injection, and
  left out under `model_authored_lessons="Exclude"`. A refusal stores the record `Quarantined`, with
  `FinalizedExperience.screening_refusal_code` (`UnsafeContent`, `OverLimit`, ...) and `screening_refusal` (the reason,
  which names fields and rules, never the text), and the `finalize` span's `agentexperience.reflection.screening_refusal`.
- **Failures** (`ReflectionFailed`, with `kind` `ModelCallFailed`, `TimedOut`, `Unparseable`, `EmptyLesson`,
  `ToolCallAttempted`, `OutputTooLarge`, `PromptTooLarge` or `ChatOptionsCallbackFailed`, the cause's type name only)
  store the record `Quarantined` with no lesson; `FinalizedExperience.reflection_failure` holds it. A call still running
  at `timeout` is abandoned, also when the client ignores cancellation; your own cancellation propagates.
- **Your own reflector** plugs into the same seam: anything with `reflect(request)` returning a `Reflection` or an
  awaitable of one (`ExperienceReflector`), passed as `reflector=` here or to `finalize` / `finalize_async`. It must
  copy the request's identity and evaluation (`ReflectionRequest.ensure_matches`) and set `authorship="Model"` when a
  model wrote it, and may declare `reflected_run_content(request)`, what it showed the model.

| .NET (`ChatClientExperienceReflectorOptions`) | Python (`ChatReflectorOptions`) | agent_framework request option |
| --- | --- | --- |
| `ModelName` | `model_name` | `model` (left out when `None`) |
| `Timeout` (30 s) | `timeout` (seconds or a `timedelta`) | |
| `MaxQuotedLength` (500), `MaxPromptLength` (16,000) | `max_quoted_length`, `max_prompt_length` | |
| `Temperature` (0) | `temperature` | `temperature` (left out when `None`) |
| `MaxOutputTokens` (2,048) | `max_output_tokens` | `max_tokens` |
| `ConfigureChatOptions` | `configure_chat_options` (mutates the options dict) | |
| `Tools = null`, `ToolMode = None` | | no `tools`, `tool_choice="none"` |
| `ChatResponseFormat.ForJsonSchema` | | `response_format={"type": "json_schema", "json_schema": {"name", "description", "schema"}}` |

Differences from .NET, each forced by Python or agent_framework:

- A client with function invocation is not refused: agent_framework's function invocation runs only the tools a request
  offers, and the reflector offers none (its `additional_tools` are declaration-only, never run). A response carrying a
  function call or result still fails as `ToolCallAttempted`.
- None in the strict JSON reading, which matches .NET's: a duplicated `lesson`, `reuseGuidance` or list member is
  `Unparseable`, a duplicated member it does not map (at any depth) is accepted, a lone surrogate escape is
  `Unparseable`, 64 levels of nesting are read and 65 are not, and a number of any length in a member it does not map
  is skipped.
- The content guard bounds its work by size rather than by .NET's one-second match timeout: its patterns run in linear
  time, and a text over 1,048,576 UTF-16 code units is treated as one .NET could not read in time (a run-content text
  admits nothing; a field is refused as `text that could not be checked in time`).
- A binding or screening that raises (a reflector returning something that is not a `Reflection`, a reflection missing
  a bound field, a guard that fails) stores the record `Quarantined` with `screening_refusal_code` `Unreadable` and
  `FinalizedExperience.screening_exception_type`, the type name only, as .NET's backstop does.
- A client that blocks the event loop cannot be timed out (asyncio cannot interrupt it); one that awaits is.
- The cause type in `ReflectionFailed` is the Python exception's (`RuntimeError`, `asyncio.exceptions.CancelledError`),
  where .NET names its own.

### Measuring benefit: the injection holdout

Off by default. With `holdout=HoldoutOptions(rate=0.1)` (contract 1.8), about one in ten captured runs that have a
block to inject has it withheld, and both arms are recorded, so the benefit estimate (`estimate_benefit`, below, or
.NET's `AgentExperienceBenefit.EstimateAsync`) counts this agent's runs alongside .NET's and Go's in the same store:

```python
from agentexperience.maf import HoldoutOptions

agent = use_agent_experience(agent, store=store, identity=identity, verify=verify,
                             holdout=HoldoutOptions(rate=0.1), on_holdout_recorded=print)
```

- **Deterministic.** The arm is a pure function of the run ID and `salt` (default `agentexperience-holdout-v1`), the
  same SHA-256 rule .NET uses (`agentexperience.assign_holdout`), so a run lands in the same arm in every language. A
  rate that is not a number from 0 to 1, or an empty salt, raises when `HoldoutOptions` is built.
- **Who takes part.** Only a captured run of this agent whose block names at least one record; a streamed (uncaptured)
  invocation, or one with nothing to inject, is injected as before and assigned nothing.
- **A held-out run** gets no block, records no exposure and no injection, and is reported `Withheld` (the `inject`
  operation's outcome, and `InjectionResult.outcome` with `withheld_experience_ids`). A treatment run is injected exactly
  as before. At rate 0 nothing changes at all.
- **Recording** runs in the background like an injection record, never delaying or failing the invocation, and is
  reported through `on_holdout_recorded` and the `record_holdout` telemetry operation (`Recorded`, `NotTracked`,
  `NotRecorded`, `OutcomeUnknown`). `InMemoryExperienceStore` and `PostgresExperienceStore` have
  `record_holdout_assignment`; PostgreSQL runs the contract's `sql/record-holdout-assignment.sql` as written and needs
  the grants of `sql/application-role-privileges-1.8.sql` (or the .NET privileges call). Without them each assignment is
  reported `NotRecorded` and injection goes on.
- **Same scope for capture and injection**, as in .NET: a run's outcome is its own finalized record in exactly that
  scope, so a run finalized elsewhere is never matched and stays pending in the estimate.

### Reading the estimate

`estimate_benefit` (contract 1.9) is .NET's `AgentExperienceBenefit.EstimateAsync`: the same estimate, with the same
numbers, from the same rows, overall, per lesson and per model. It is synchronous, as the stores are:

```python
from agentexperience import BenefitOptions, estimate_benefit

estimate = estimate_benefit(store, bounds, scope, BenefitOptions(split_by_model=True))
print(estimate.overall.verdict, estimate.overall.treatment.rate, estimate.overall.holdout.rate)
for lesson in estimate.lessons:
    print(lesson.experience_id, lesson.comparison.verdict)
```

- **What it reads.** The store's `read_holdout_outcomes` (both `InMemoryExperienceStore` and `PostgresExperienceStore`
  have it), 1,000 rows a page, under `bounds` and in exactly `scope` (the scope the blocks were built in); each page is
  folded into a `BenefitAccumulator` before the next is read, so memory grows with runs and lessons, not rows.
  PostgreSQL runs the contract's `sql/query-holdout-outcomes.sql` as written and needs no grants beyond 1.0 and 1.8.
  `estimate(rows, options)` is the pure form over rows read any other way. Neither writes.
- **The rules are .NET's** ([README-1.9](https://github.com/fabbrik/AgentExperience.NET/blob/main/contract/README-1.9.md)):
  `Verified` is a success and `Failed` a failure; a run with an `Unknown` outcome counts as `unverified`, one with no
  record yet as `pending`, one recorded in both arms as `mixed_arm`; Wilson intervals per arm and Newcombe's hybrid
  interval for the difference; `Helps` or `Hurts` only when each arm has `minimum_runs_per_arm` runs and the 95%
  interval excludes zero, otherwise `NoEvidence`. The cautions of the .NET guide (many lessons mean many comparisons;
  do not stop the first time it says `Helps`; watch `pending` and `unverified`) apply unchanged.
- **A sealed record** (`payload_version` 2, written by .NET's crypto-shredding) cannot be read by the port: its run has
  no outcome and counts as `pending`, exactly as .NET counts a run whose record is sealed under a destroyed key. A
  sealed lesson names no model either, so in an encrypted store, where .NET opens sealed records, its model reads as
  `unknown` here and the per-model pairs (`by_model_pair`) can differ from the .NET estimate's.
- **The same answer as .NET** for the same rows: the same counts and verdicts, and every rate, difference and bound
  equal within 1e-12.
- **Edges.** `BenefitOptions(minimum_runs_per_arm=0)` raises `ValueError` before anything is read. A store without
  `read_holdout_outcomes` gives an empty estimate with `tracked` `False`; a scope outside `bounds` gives an empty one
  whose `outcome` is `Denied`, never an exception. A store that pages a run's rows apart raises `ValueError` rather
  than count the run twice.
- **The run's model.** Capture records the model the agent's chat client reports under the environment metadata key
  `agentexperience.model`: the agent's default `model` option (Agent Framework sets it from the client), else the chat
  client's `model` or `model_id` attribute; trimmed, cut to 256 UTF-16 code units, nothing when blank. A key the host's
  `environment` already sets is kept. It is bookkeeping only: never a precondition, never in the block, never ranked.

| Python (`BenefitEstimate` and its parts) | .NET (`ExperienceBenefitEstimate` and its parts) |
| --- | --- |
| `overall`, `lessons`, `by_consuming_model` | `Overall`, `Lessons`, `ByConsumingModel` |
| `tracked`, `outcome` (`"Found"`, `"Denied"`), `minimum_runs_per_arm` | `Tracked`, `Outcome`, `MinimumRunsPerArm` |
| `BenefitComparison.treatment`, `.holdout`, `.difference`, `.lower`, `.upper`, `.verdict` | `Treatment`, `Holdout`, `Difference`, `Lower`, `Upper`, `Verdict` |
| `BenefitComparison.unverified`, `.pending`, `.mixed_arm` | `Unverified`, `Pending`, `MixedArm` |
| `BenefitArm.runs`, `.successes`, `.rate` | `ExperienceBenefitArm.Runs`, `Successes`, `Rate` |
| `LessonBenefit.experience_id`, `.comparison`, `.by_model_pair` | `ExperienceLessonBenefit.ExperienceId`, `Comparison`, `ByModelPair` |
| `ModelBenefit.model`, `.comparison` | `ExperienceModelBenefit.Model`, `Comparison` |
| `ModelPairBenefit.lesson_model`, `.run_model`, `.comparison` | `ExperienceModelPairBenefit.LessonModel`, `RunModel`, `Comparison` |
| `BenefitOptions.minimum_runs_per_arm`, `.split_by_model` | `ExperienceBenefitOptions.MinimumRunsPerArm`, `SplitByModel` |
| `"unknown"` (`agentexperience.benefit.UNKNOWN_MODEL`) | `ExperienceBenefitEstimate.UnknownModel` |

### Acting on the estimate

Reading the estimate changes nothing by itself. With `benefit=BenefitPolicy(enabled=True)` (contract 1.10, .NET's
`ExperienceBenefitPolicy`) injection acts on it:

```python
from agentexperience import BenefitPolicy

agent = use_agent_experience(agent, store=store, identity=identity, verify=verify,
                             holdout=HoldoutOptions(rate=0.1),
                             benefit=BenefitPolicy(enabled=True, prefer_measured_helpful=True))
```

- **A lesson measured `Hurts` is omitted** before the record limit, so a lower-ranked record takes its slot; a scope
  whose overall verdict is `Hurts` gets no lessons at all. `InjectionResult.omitted` lists every ranked record that
  was not delivered as an `OmittedRecord` (`experience_id`, `reason`, `detail`): `MeasuredHarmful`, `ModelAuthored`,
  `OverRecordLimit` or `OverByteBudget`, with .NET's details, in .NET's order. With `prefer_measured_helpful` the
  lessons measured `Helps` go first, each group keeping its rank order. `NoEvidence` changes nothing.
- **Per model.** With `per_model=True` the estimate is split by model and an invocation acts on its own model's
  verdicts: the model capture records for the run (the host's environment value when it sets one), or for an
  uncaptured invocation the agent's chat client's. Where that model's group has `NoEvidence`, or the model is unknown or
  unmeasured, the pooled verdict stands.
- **It never waits on the store.** The estimate is cached per scope, authorization bounds and principal. An invocation
  uses what is cached as it is (nothing yet, on the first, means no effect) and, when it is missing or older than
  `refresh_interval`, starts one refresh on a daemon thread; at most one runs per key. A refresh that raises or
  overruns `refresh_timeout` keeps the previous estimate, and the store is asked again only after another interval. Each
  refresh is the `benefit_refresh` telemetry operation (`Refreshed`, `NotRefreshed`, `OutcomeUnknown`).
  `await provider.wait_for_benefit_refreshes()` waits for the ones started so far (tests, shutdown).
- **Only recent evidence counts:** assignments within `window` of a refresh. `minimum_runs_per_arm` is the estimator's.
  An estimate the store could not give (no holdout read, or a scope outside the bounds) is never acted on.
- **The holdout is unchanged.** It assigns and withholds the block the selection produced, so a lesson omitted as
  harmful is neither delivered nor withheld; its verdict returns to `NoEvidence` once its evidence leaves the window.
- **The selection as a function.** `agentexperience.select_block(ranked, view, block_options, ...)` is what an
  invocation runs; `BenefitViews.of(estimate).for_model(model)` is the view it acts on.

| Python (`BenefitPolicy`) | .NET (`ExperienceBenefitPolicy`) | Default |
| --- | --- | --- |
| `enabled` | `Enabled` | `False` |
| `prefer_measured_helpful` | `PreferMeasuredHelpful` | `False` |
| `per_model` | `PerModel` | `False` |
| `window` (`timedelta`) | `Window` | 30 days |
| `minimum_runs_per_arm` | `MinimumRunsPerArm` | 20 |
| `refresh_interval` (`timedelta`) | `RefreshInterval` | 10 minutes |
| `refresh_timeout` (`timedelta`, at most one day) | `RefreshTimeout` | 30 seconds |

An interval, timeout or window that is not strictly positive, a timeout over one day, or a minimum below 1 raises
`ValueError` when the policy is built, even with it off.

### Measuring another memory provider

The lessons are not the only memory an agent can have. `use_measured_memory` (contract 1.11, .NET's
`UseMeasuredMemory`) puts another memory provider (Mem0, Zep, Letta, your own: any Agent Framework `ContextProvider`)
under the same holdout, so its benefit is measured the way the lessons' is:

```python
from agentexperience import estimate_provider_benefit
from agentexperience.maf import HoldoutOptions, use_measured_memory

agent = use_agent_experience(Agent(client=chat_client, context_providers=[mem0]), store=store, identity=identity,
                             verify=verify)
memory = use_measured_memory(agent, mem0, "mem0", holdout=HoldoutOptions(rate=0.1))
...
estimate = estimate_provider_benefit(store, bounds, scope, "mem0")
print(estimate.overall.verdict, estimate.overall.treatment.rate, estimate.overall.holdout.rate)
```

- **The wrapper.** `use_measured_memory` replaces the provider in the agent's `context_providers` (or appends it)
  with an `ExperienceHoldoutContextProvider`, which you can also build yourself
  (`ExperienceHoldoutContextProvider(inner, key, holdout, store, authorize=..., clock=..., record_timeout=...,
  on_recorded=...)`). Without `holdout` it takes the injection holdout's rate and salt (off by default). The key is 1
  to 100 ASCII letters, digits, `.`, `_` or `-`; any other raises `ValueError`.
- **The wrapped provider always runs.** Its `before_run` and `after_run` are called in both arms, under its own
  `source_id` (so its session state is its own); its exceptions propagate unchanged and nothing is recorded for them.
- **Who takes part.** A non-streaming invocation captured by this agent's `use_agent_experience`, whose provider added
  an instruction, a message or a tool. Everything else (rate 0 included, which is a pure pass-through) gets exactly
  what the provider added and records nothing.
- **The arm** is `assign_provider_holdout(run_id, key, holdout)`: the 1.8 assignment over the salt
  `f"{len(key)}:{key}/{salt}"`, so each provider is randomised independently of the lessons. A `Holdout` invocation
  sees exactly the context it would see without the provider. Agent Framework providers add to a shared context in
  place, so the wrapped provider is given a context of its own (messages, instructions, tools and middleware copied;
  `options` and `metadata` shared) and its additions are copied into the invocation's only in the `Treatment` arm or
  when the run does not take part. The context passed in is never changed by the wrapper itself.
- **Recorded in the background** as the holdout is (bounded by `injection_record_timeout`, never delaying or failing
  the invocation): the run, the key, the arm and the instant, through the store's
  `record_provider_holdout_assignment`. The outcome is reported through `on_recorded` (a
  `ProviderHoldoutRecordResult`, called on a worker thread, never on the invocation's path) and the
  `record_provider_holdout` telemetry operation; `await memory.wait_for_records()` waits for the ones started so far,
  and so does the injection provider's `wait_for_injection_records()` for every memory `use_measured_memory` wrapped
  on the same agent. PostgreSQL runs the contract's
  `sql/record-provider-holdout-assignment.sql` as written and needs the grants of
  `sql/application-role-privileges-1.11.sql` (or the .NET privileges call); without them every invocation still runs
  and withholds, and each assignment is reported `NotRecorded`.
- **The estimate.** `estimate_provider_benefit(store, bounds, scope, key, options=None)` reads the key's rows
  (`read_provider_holdout_outcomes`, `sql/query-provider-holdout-outcomes.sql`) 1,000 at a time in exactly `scope`
  and compares the arms' verified outcomes with the 1.9 rules; there is no per-item comparison, since the provider's
  items are opaque. The key is checked first, then the options. A store without the read gives `tracked` `False`; a
  scope outside `bounds` gives `outcome` `Denied`; without the 1.11 grants the store's error is raised.
  `estimate_provider(rows, key, options)` is the pure form.

| Python | .NET |
| --- | --- |
| `use_measured_memory(agent, memory, key, holdout=None)` | `services.UseMeasuredMemory(memory, key, holdout)` |
| `ExperienceHoldoutContextProvider(inner, key, holdout, store, authorize=, clock=)` | `ExperienceHoldoutContextProvider(inner, providerKey, holdout, store, authorize, timeProvider)` |
| `ProviderHoldoutAssignment.run_id`, `.provider_key`, `.arm`, `.assigned_at` | `RunId`, `ProviderKey`, `Arm`, `AssignedAt` |
| `ProviderHoldoutOutcomeRow.run_id`, `.provider_key`, `.arm`, `.assigned_at`, `.run_outcome` | `RunId`, `ProviderKey`, `Arm`, `AssignedAt`, `RunOutcome` |
| `estimate_provider_benefit(store, bounds, scope, key, options)` | `AgentExperienceBenefit.EstimateProviderAsync(services, authorization, scope, providerKey, options)` |
| `ProviderBenefitEstimate.provider_key`, `.overall`, `.minimum_runs_per_arm`, `.tracked`, `.outcome` | `ProviderKey`, `Overall`, `MinimumRunsPerArm`, `Tracked`, `Outcome` |
| `is_valid_provider_key(key)`, `provider_salt(key, salt)` | `ProviderHoldoutAssignment.IsValidProviderKey`, `ExperienceHoldoutContextProvider.ProviderSalt` |

### Telemetry: the same spans and metrics as .NET

With the `otel` extra (`pip install "agenticexperience[otel]"`, which depends on `opentelemetry-api` only), one call
reports the operations the port performs to OpenTelemetry, under the names, dimensions and attributes
[`docs/telemetry.md`](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/telemetry.md#the-python-and-go-ports)
documents for .NET, so one dashboard serves a mixed fleet:

```python
from agentexperience import telemetry

telemetry.enable()  # the global tracer and meter providers your application configured (or pass tracer_provider=, meter_provider=)
```

It emits `inject`, `retrieve`, `record_injection`, `record_holdout`, `record_provider_holdout` (with a measured memory
provider), `benefit_refresh` (with the benefit policy on),
`capture.start_run`, `capture.append_attempt`, `capture.complete_run`, `verify`, `reflect`, `finalize` and `index` (the
background embedding write): the operations this port performs, and no other. The core and the agent integration import no OpenTelemetry; they call the small hook in `agentexperience.observer`
(one `None` check when nothing is registered), and `agentexperience.telemetry` is the adapter. It configures no SDK and
no exporter, and a provider, exporter or observer that raises is swallowed and never reaches the agent. Attributes
carry identifiers and counts only, never record content, task text, scopes or exception messages.

### In production

The identity must come from your own authentication, never from model output: pass a function of an
`IdentityContext` (the agent and the session) that returns an `ExperienceIdentity(principal, scope)`, or `None` to
leave an invocation neither captured nor injected. `development_identity()` runs everything as one fixed caller in
one fixed scope, so it refuses outside Development, Test or Testing: the Python-native `AGENTEXPERIENCE_ENVIRONMENT`
variable is read first, then, by the .NET library's rule, `DOTNET_ENVIRONMENT`, else `ASPNETCORE_ENVIRONMENT`; with
none set it is allowed (pass `environment_name=` when your host has its own name, and it is kept with the identity).
A resolver that returns a development identity is checked on every invocation: outside development the invocation is
reported (`ResolveIdentity`) and runs with no memory. The store is `PostgresExperienceStore` (the `postgres` extra, below),
which shares lessons with .NET agents on the same database.

## What it does

- **Capture and finalize.** `RunCapture` sanitizes a run as it is recorded: secret-named fields are redacted, argument
  values are kept only when allowlisted, and the default limits apply. `finalize` aggregates the host's verification,
  writes the lesson for a verified run (the deterministic reflector's, or a host reflector's: `reflector=`, with
  `finalize_async` for an asynchronous one), screens it, and returns the stored record (`Validated`, or `Quarantined`
  without a lesson) and its initial lifecycle event. The IDs are derived, so a retry derives the same ones.
- **The injected block.** `render_block` turns ranked records into the Historical Reference block, byte for byte as
  the .NET adapter writes it, in the compact or verbose layout, within the record and byte limits, with record text
  neutralized.
- **The task text.** `derive_task_text` derives the text retrieval searches with from an invocation's messages.
- **The error class.** `error_class` reduces a captured error to its short class.
- **PostgreSQL** (the `postgres` extra, psycopg 3). `PostgresExperienceStore` writes a finalized record and its
  initial event as the application role, reads records back, runs the contract's text search, and records injections
  (`record_injection`, contract 1.4). It ships the contract's SQL files and runs them as written. `rank` applies the
  contract's safety checks, truncation, eligibility and ranking to the rows the search returns, under the retrieval
  policy (contract 1.12) when one is given. The store needs a schema the .NET
  migrator applied, with row-level security on; it never creates or alters a schema object. The application role's
  privileges are the .NET privileges call's, or the contract's two files, in this order:
  `sql/application-role-privileges.sql`, then `sql/application-role-privileges-1.4.sql` (without the second, every
  injection is still made and its record is reported `NotRecorded`).

It does nothing outside contract 1.x: no grants, crypto-shredding or provenance signing, and no lifecycle
after finalization (reuse evidence, reinforcement, contest, revocation). It writes no logs at all, so no record
content is ever logged.

The pieces that need no run or database:

```python
from agentexperience import Message, MessagePart, derive_task_text, error_class

messages = [
    Message("user", [MessagePart("text", "Deploy service X to prod")]),
    Message("user", [MessagePart("text", "and retry")]),
]
print(derive_task_text(messages))  # Deploy service X to prod — and retry
print(error_class("System.TimeoutException: The operation has timed out."))  # TimeoutException
```

### End to end

Capture a run, finalize it, store it, search for it, rank the rows and render the block. Steps 1 and 2 need nothing
else; step 3 needs the `postgres` extra (`pip install "agenticexperience[postgres]"`) and a database
the .NET migrator set up.

```python
import os
import uuid

from agentexperience import (
    AuthorizationBounds,
    BlockOptions,
    ClosedVerificationRound,
    Duration,
    EnvironmentFingerprint,
    Evidence,
    Instant,
    Provenance,
    RawAttempt,
    RawToolCall,
    RequiredCheck,
    RunCapture,
    Sanitization,
    Scope,
    SearchParameters,
    finalize,
    rank,
    render_block,
)
from agentexperience.postgres import PostgresExperienceStore

scope = Scope("acme", "support-app", "refunds", agent_id="refund-agent")
start = Instant.parse("2026-03-02T09:00:00+00:00")

# 1. Capture a run as it happens. Only the allowlisted argument "strategy" keeps its value.
capture = RunCapture.start(
    run_id=uuid.uuid4(),
    task_id="refund-stuck-pending",
    task_description="Refund ticket 42 is stuck in pending",
    scope=scope,
    environment=EnvironmentFingerprint("host-1", "python-3.13", "linux", "1.0.0", {"region": "eu-west"}),
    provenance=Provenance("my-agent", "1", start, None),
    started_at=start,
    sanitization=Sanitization.allowing(["strategy"]),
)
second = Duration.from_seconds(1)
capture.append_attempt(
    RawAttempt(
        uuid.uuid4(),
        start,
        second,
        [
            RawToolCall(
                uuid.uuid4(),
                "run_refund_check",
                {"strategy": "retry"},
                start,
                second,
                error="InvalidOperationException: the refund lock is held",
            )
        ],
        error="InvalidOperationException: the refund lock is held",
    )
)
capture.append_attempt(
    RawAttempt(
        uuid.uuid4(),
        start.add(second),
        second,
        [RawToolCall(uuid.uuid4(), "run_refund_check", {"strategy": "wait-for-lock"}, start, second, result="issued")],
        result="Refund issued.",
    )
)
run = capture.complete("Completed", start.add(Duration.from_seconds(20)))

# 2. Finalize against the host's verification: one passing check makes it a Validated lesson.
round_ = ClosedVerificationRound(uuid.uuid4(), "rev-1")
finished = start.add(Duration.from_seconds(30))
finalized = finalize(
    run,
    closed_round=round_,
    required_checks=[RequiredCheck("refund-issued", "integration-test")],
    evidence=[
        Evidence(
            uuid.uuid4(), round_.round_id, "rev-1", "refund-issued", "integration-test", "Pass", "ci", None, finished
        )
    ],
    current_artifact_revision="rev-1",
    finalized_at=finished,
)
print(finalized.record.status, "-", finalized.record.reflection.lesson)

# 3. Store it, search it and render the block (needs the postgres extra and a schema the .NET migrator applied).
store = PostgresExperienceStore(os.environ["AGENTEXPERIENCE_CONNINFO"])  # libpq string, as the application role
bounds = AuthorizationBounds.of(scope)
store.insert_finalized(bounds, finalized, actor="host-principal")
rows = store.search(bounds, SearchParameters(scope, "refund ticket stuck in pending"))
ranked = rank(rows, scope, now=finished)
print(render_block(ranked.ranked, BlockOptions(rendering="Compact")).text)
```

## What the Python port does not do yet

The port covers contract 1.0 to 1.14: capture, finalize, store, text and semantic retrieval under .NET's retrieval
policy (expiry, confidence decay by domain, the timeout and abandoned-search cap), the injected block and every
injection option that needs no session (the receiving agent's gate, the model-authored policy, the request resolver
and the host's decision),
injection recording, the lessons view with its HTML page and the export of measured lessons, the model-backed
reflector and the content guard for model-authored lessons, the injection holdout, its benefit estimate and the benefit policy that acts on
it, measuring another memory provider, and telemetry. These .NET features have no counterpart
here, so a mixed .NET and Python deployment should keep them on the .NET side:

- **Lifecycle after finalization:** reuse feedback, confidence reinforcement, contest and revocation.
- **Session tracking and withdrawal notices** (`SessionLimits`, `SessionStateKey`; story 27.6): the port keeps no
  per-session account of what it injected, so it never deduplicates, budgets a session or retracts a record; a held-out
  invocation is always `Withheld`, never a notices-only `Retracted` block.
- **The final eligibility re-read** (story 27.6): the port renders the record retrieval returned and does not re-read
  it before injecting, so it has no `Limits.EligibilityCheckTimeout` or `MaxAbandonedReads`, and never omits a record
  as `Ineligible` or `Unreadable`. Its decision context's `current` is the ranked record.
- **Required environment attributes** (`RetrieveExperienceRequest.RequiredEnvironmentAttributes`): an
  `InjectionRequest` has none, and retrieval never excludes as `EnvironmentMismatch`.
- **Sharing** (story 27.8): grants, and so the grant-disclosure choices and the decision context's `SharedByGrant`,
  `PermittingGrantId`, `GrantDisclosure` and `GrantApproachArguments`.
- **Performing erasure and crypto-shredding** (the port never reads an erased record, contract 1.5), and **provenance signing** (story 27.7; so no `ContentUnconfirmed` lesson state, and no `UnconfirmedContent` omission).
- **Migrations:** the port never creates or alters a schema object; the .NET migrator does.
- **A reflection sanitizer**: .NET passes a reflection's free text through the host's `ISanitizer` between hygiene and
  the content guard; this port has no host sanitizer for reflections (with .NET's default sanitizer that layer changes
  nothing).

What the port does write or read follows the same contract the .NET library is tested against.

## Running the tests

[uv](https://docs.astral.sh/uv/) manages the environment. From this directory:

```bash
uv sync
uv run ruff check && uv run ruff format --check
uv run mypy --strict src tests
uv run pytest
```

`uv run pytest` (its PostgreSQL leg uses a `pgvector/pgvector` image) runs the unit tests and the conformance runner (`tests/test_contract.py`), which loads every fixture
family from [`../contract/fixtures`](https://github.com/fabbrik/AgentExperience.NET/tree/main/contract/fixtures) and compares this package's output exactly: blocks and task
text byte for byte, JSON by value. It validates every record against the contract's JSON Schema, checks every
contract file against `contract/lock.json`, and fails with a clear message if the lock names a contract version this
package does not support.

The development environment includes `agent-framework-core`, so `uv run pytest` also runs the Agent Framework tests:
`tests/test_maf.py` drives a real `agent_framework.Agent` with a scripted chat client (capture, classification,
verification, storage, injection, failures, sessions, streaming); `tests/test_verifiers.py` and
`tests/test_classifiers.py` port the cases of the .NET verifier and classifier tests; and `tests/test_quickstart.py`
runs the sample (`AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE=1` rewrites its expected output on purpose).

**The cross-language proof** (`tests/test_cross_language.py`):

- *Python writes, .NET reads.* A Python agent on a fixed clock and fixed identifiers stores two runs, and the records
  must be exactly [`tests/cross-language/python-agent-records.json`](https://github.com/fabbrik/AgentExperience.NET/blob/main/python/tests/cross-language/python-agent-records.json)
  (checked in memory, and written to and read back from PostgreSQL in the PostgreSQL leg). In the PostgreSQL leg the
  rows the Python store wrote, every column it writes in PostgreSQL's own text form, must be exactly
  [`tests/cross-language/python-agent-rows.json`](https://github.com/fabbrik/AgentExperience.NET/blob/main/python/tests/cross-language/python-agent-rows.json).
  The .NET contract tests' `PythonAgentRecordsPostgresRoundTripTests` insert those raw rows into a schema the .NET
  migrator applied, each value cast from its text by PostgreSQL, read every record back through the .NET PostgreSQL
  store (it must be `python-agent-records.json`), check each initial event is the one .NET finalization derives,
  compare every column with what the .NET store writes for the same record, read the injection the second run
  recorded through the .NET lessons view and compare its row with what the .NET store records for it, and find the
  Python agent's lessons through the .NET retrieval. So a column the Python store wrote in its own encoding fails there, even when the
  Python reader undoes it. `AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1` rewrites both files on purpose (the rows in the
  PostgreSQL leg).
- *.NET writes, Python reads.* The records the .NET library finalized for the contract's finalize fixtures (generated
  from .NET and checked against it on every build) are written into PostgreSQL, and a Python agent on their scope is
  injected with the one its task matches. No .NET process runs at test time: the fixtures are the .NET-written
  records.

The PostgreSQL tests (`tests/test_postgres.py` and the cross-language tests, marker `postgres`) write each finalize fixture through the store and
read it back row for row, run every search fixture, then rank, and run every record-injection fixture. Each test database is migrated with the .NET
migrator's own scripts from
[`src/AgentExperience.Storage.Postgres/Migrations`](https://github.com/fabbrik/AgentExperience.NET/tree/main/src/AgentExperience.Storage.Postgres/Migrations), in order;
the application role's privileges are the contract's
[`sql/application-role-privileges.sql`](https://github.com/fabbrik/AgentExperience.NET/blob/main/contract/sql/application-role-privileges.sql) then
[`sql/application-role-privileges-1.4.sql`](https://github.com/fabbrik/AgentExperience.NET/blob/main/contract/sql/application-role-privileges-1.4.sql), which a .NET test keeps
identical to the statements the .NET migrator runs. The server is a Testcontainers `postgres` container (`AGENTEXPERIENCE_POSTGRES_MAJOR`, 16 by default), or
the server libpq's `PGHOST`, `PGPORT`, `PGUSER` and `PGPASSWORD` name (a superuser). With neither, they are skipped.
On a Mac with Rancher Desktop or Colima, Testcontainers may need:

```bash
TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock TESTCONTAINERS_RYUK_DISABLED=true uv run pytest -m postgres
```

CI runs the `python` job on CPython 3.10 to 3.14 (lint, `mypy --strict`, the tests, the Agent Framework tests again
on agent-framework-core 1.0.0, the `maf` extra's floor, and an import of the built wheel from a clean environment,
without and then with the `maf` extra), and the PostgreSQL tests on PostgreSQL 16 and 18. To run a PostgreSQL leg as CI does, set
`AGENTEXPERIENCE_POSTGRES_MAJOR` (16 or 18) and `AGENTEXPERIENCE_REQUIRE_POSTGRES=1`, which fails rather than skips
without a server, and run `uv run pytest -m postgres`.
