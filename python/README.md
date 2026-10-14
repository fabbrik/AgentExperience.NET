# AgentExperience for Python

A pure-Python core of AgentExperience that implements the [language-neutral contract](../contract/README.md), so a
lesson learned by a Python agent is stored in the format the .NET library reads, and the other way round.

**Status: pre-release, contract 1.0, with a Microsoft Agent Framework integration (the `maf` extra).** It passes
every fixture of contract 1.0. It is not published to PyPI. The distribution and import name `agentexperience` are
provisional until the package is named, and both live in one place (`pyproject.toml` and `src/agentexperience/`).

## Quick start with Microsoft Agent Framework

Install from this directory with the `maf` extra (`pip install ".[maf]"`, which brings `agent-framework-core` 1.0.0 or
later); bring `client` (any MAF chat client) and `run_tests` (your test run, returning text such as `exit=0`).
Development and tests only: see [In production](#in-production). After the two imports, five lines: one call that
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

[`samples/quickstart.py`](samples/quickstart.py) is based on this snippet and runs it with no key and no network:
`uv run python samples/quickstart.py`. It differs in what a runnable, repeatable demo needs: the model is a scripted
stand-in (`ScriptedModel`, which calls `run_tests` until a call returns `exit=0`), `run_tests` is a small class whose
first call of each run returns `exit=3`, the identity names its application (`development_identity(application=
"quickstart")`), the recorded environment is fixed (`environment=...`) so the output is the same on every machine, and it
prints each run and the block. Its output is checked in as
[`samples/quickstart.expected.txt`](samples/quickstart.expected.txt), and a test compares them.

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
  record, reported through `on_capture_failure` or `on_injection_failure` (the reason never quotes record content).
  Only the invocation's own cancellation and the tool's own exceptions propagate, unchanged.

**Verifiers** (`agentexperience.verifiers`, re-exported by `agentexperience.maf`) have the semantics of .NET's
`ExperienceVerifiers`: `tool_succeeded(tool)` (the last call to the tool decides), `check(id, fn)` (your own check:
`True`, `False`, or `None` for "do not store"), `approval(id, fn)` (a person's decision, by an opaque approver ID),
`all_of(...)` (every check; stops at the first `None`) and `any_of(...)` (the first that passes). **Classifiers**
(`agentexperience.classifiers`) have the semantics of `ExperienceToolResultClassifiers`: `exit_code(*tools)` and
`http_status(*tools)` read a JSON object's top-level fields, else the last match in the text.

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
  initial event as the application role, reads records back, and runs the contract's text search. It ships the
  contract's SQL files and runs them as written. `rank` applies the contract's safety checks, truncation and ranking
  to the rows the search returns. The store needs a schema the .NET migrator applied, with row-level security on; it
  never creates or alters a schema object.

It does nothing outside contract 1.x: no grants, crypto-shredding, provenance signing or vectors, and no lifecycle
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
else; step 3 needs the `postgres` extra (`pip install "agentexperience[postgres]"` from this directory) and a database
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

## Running the tests

[uv](https://docs.astral.sh/uv/) manages the environment. From this directory:

```bash
uv sync
uv run ruff check && uv run ruff format --check
uv run mypy --strict src
uv run pytest
```

`uv run pytest` runs the unit tests and the conformance runner (`tests/test_contract.py`), which loads every fixture
family from [`../contract/fixtures`](../contract/fixtures) and compares this package's output exactly: blocks and task
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
  must be exactly [`tests/cross-language/python-agent-records.json`](tests/cross-language/python-agent-records.json)
  (checked in memory, and written to and read back from PostgreSQL in the PostgreSQL leg). The .NET contract tests'
  `PythonAgentRecordsPostgresRoundTripTests` insert that file into a schema the .NET migrator applied and read each
  record back through the .NET PostgreSQL store: it must come back unchanged. `AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1`
  rewrites the file on purpose.
- *.NET writes, Python reads.* The records the .NET library finalized for the contract's finalize fixtures (generated
  from .NET and checked against it on every build) are written into PostgreSQL, and a Python agent on their scope is
  injected with the one its task matches. No .NET process runs at test time: the fixtures are the .NET-written
  records.

The PostgreSQL tests (`tests/test_postgres.py` and the cross-language tests, marker `postgres`) write each finalize fixture through the store and
read it back row for row, and run every search fixture, then rank. Each test database is migrated with the .NET
migrator's own scripts from
[`src/AgentExperience.Storage.Postgres/Migrations`](../src/AgentExperience.Storage.Postgres/Migrations), in order;
the application role's privileges are replayed by a small helper in `tests/_postgres.py`, whose docstring says what
it mirrors. The server is a Testcontainers `postgres` container (`AGENTEXPERIENCE_POSTGRES_MAJOR`, 16 by default), or
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
