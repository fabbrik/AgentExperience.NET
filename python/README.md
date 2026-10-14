# AgentExperience for Python

A pure-Python core of AgentExperience that implements the [language-neutral contract](../contract/README.md), so a
lesson learned by a Python agent is stored in the format the .NET library reads, and the other way round.

**Status: pre-release, contract 1.0, no Microsoft Agent Framework integration yet.** It passes every fixture of
contract 1.0. It is not published to PyPI. The distribution and import name `agentexperience` are provisional until
the package is named, and both live in one place (`pyproject.toml` and `src/agentexperience/`).

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

The PostgreSQL tests (`tests/test_postgres.py`, marker `postgres`) write each finalize fixture through the store and
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

CI runs the `python` job on CPython 3.10 to 3.14 (lint, `mypy --strict`, the tests, and an import of the built wheel
from a clean environment), and the PostgreSQL tests on PostgreSQL 16 and 18. To run a PostgreSQL leg as CI does, set
`AGENTEXPERIENCE_POSTGRES_MAJOR` (16 or 18) and `AGENTEXPERIENCE_REQUIRE_POSTGRES=1`, which fails rather than skips
without a server, and run `uv run pytest -m postgres`.
