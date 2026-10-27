# AgentExperience for Python

A pure-Python core of AgentExperience that implements the [language-neutral contract](https://github.com/fabbrik/AgentExperience.NET/blob/main/contract/README.md), so a
lesson learned by a Python agent is stored in the format the .NET library reads, and the other way round.

**Status: alpha, contract 1.8, with a Microsoft Agent Framework integration (the `maf` extra).** It passes every
fixture of contract 1.8 (and so every 1.0 to 1.7 fixture). It is published to PyPI as `agenticexperience` (the import name stays `agentexperience`), with every
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
- **Fail-open.** The vector channel never fails or delays past `vector_timeout` (seconds, default 0.5, covering the
  embedding and the search) the text answer. When it does not contribute the injection answers from text and reports
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
- **Eligibility codes** this port decides: `Status`, `BelowConfidenceFloor` (retrieval's 0.5), `ModelAuthored` (when the
  setup's `exclude_model_authored` is on), `OverByteBudget` (the block options' `max_bytes`) and `Unrenderable`. .NET's
  `Expired` and `ContentUnconfirmed` do not exist here: this port has no maximum-age policy and no provenance signing.
- **The page** has `outcome`, `generated_at`, `has_more`, `out_of_scope_dropped` and `injections_tracked`. When it is
  false (a store that records no injections, or a PostgreSQL role without the 1.4 grants or table), a `None`
  `last_injected_at` means "not known", not "never". A scope outside the authorization bounds is an empty page with the
  outcome `Denied`, never an exception; an invalid `LessonsQuery` (`limit` outside 1 to 500, empty or unknown
  `statuses`) raises `ValueError`. One record that cannot be rendered is listed as `Unrenderable`, and the rest still are.
- **Reads only**, under the caller's authorization bounds and in the caller's own scope (no sharing grants in contract
  1.x). Build one without the setup with `ExperienceLessons(store, block_options=..., exclude_model_authored=...)` and
  `list_scope(bounds, scope, query)`. The view discloses lessons: serve it only behind your own authorization. This
  port renders no HTML and maps no endpoint.
- **Stores.** `InMemoryExperienceStore` and `PostgresExperienceStore` have `list_lessons_page`; PostgreSQL runs the
  contract's `sql/list-experience-lessons.sql` as written and needs no grants beyond 1.0 and 1.4.

### Measuring benefit: the injection holdout

Off by default. With `holdout=HoldoutOptions(rate=0.1)` (contract 1.8), about one in ten captured runs that have a
block to inject has it withheld, and both arms are recorded, so the .NET library's benefit estimate
(`AgentExperienceBenefit.EstimateAsync`) counts this agent's runs alongside .NET's and Go's in the same store:

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

### Telemetry: the same spans and metrics as .NET

With the `otel` extra (`pip install "agenticexperience[otel]"`, which depends on `opentelemetry-api` only), one call
reports the operations the port performs to OpenTelemetry, under the names, dimensions and attributes
[`docs/telemetry.md`](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/telemetry.md#the-python-and-go-ports)
documents for .NET, so one dashboard serves a mixed fleet:

```python
from agentexperience import telemetry

telemetry.enable()  # the global tracer and meter providers your application configured (or pass tracer_provider=, meter_provider=)
```

It emits `inject`, `retrieve`, `record_injection`, `capture.start_run`, `capture.append_attempt`, `capture.complete_run`,
`verify`, `reflect`, `finalize` and `index` (the background embedding write): the operations this port performs, and no
other. The core and the agent integration import no OpenTelemetry; they call the small hook in `agentexperience.observer`
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
  writes the deterministic lesson for a verified run, and returns the stored record (`Validated`, or `Quarantined`
  without a lesson) and its initial lifecycle event. The IDs are derived, so a retry derives the same ones.
- **The injected block.** `render_block` turns ranked records into the Historical Reference block, byte for byte as
  the .NET adapter writes it, in the compact or verbose layout, within the record and byte limits, with record text
  neutralized.
- **The task text.** `derive_task_text` derives the text retrieval searches with from an invocation's messages.
- **The error class.** `error_class` reduces a captured error to its short class.
- **PostgreSQL** (the `postgres` extra, psycopg 3). `PostgresExperienceStore` writes a finalized record and its
  initial event as the application role, reads records back, runs the contract's text search, and records injections
  (`record_injection`, contract 1.4). It ships the contract's SQL files and runs them as written. `rank` applies the
  contract's safety checks, truncation and ranking to the rows the search returns. The store needs a schema the .NET
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

The port covers contract 1.0 to 1.8: capture, finalize, store, text and semantic retrieval, the injected block,
injection recording, the lessons view, the injection holdout and telemetry. These .NET features have no counterpart
here, so a mixed .NET and Python deployment should keep them on the .NET side:

- **Lifecycle after finalization:** reuse feedback, confidence reinforcement, contest and revocation.
- **Reading the holdout:** the benefit estimate, the benefit policy, measuring another memory provider, exporting
  lessons, and recording the run's model. The port records its arms; .NET estimates over them.
- **Withdrawal notices:** the port keeps no per-session account of what it injected, so it never retracts a record;
  a held-out invocation is always `Withheld`, never a notices-only `Retracted` block.
- **Sharing:** grants, and so the grant-disclosure choices.
- **Performing erasure and crypto-shredding** (the port never reads an erased record, contract 1.5), and **provenance signing** (so no `Expired` or `ContentUnconfirmed` lesson state).
- **Migrations:** the port never creates or alters a schema object; the .NET migrator does.
- **Lessons view HTML and endpoint mapping.**

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
