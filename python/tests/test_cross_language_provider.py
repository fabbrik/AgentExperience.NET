"""
Story 27.3's cross-language proof (contract 1.11), the Python half: a Python MAF agent measures another memory
provider, and the rows it writes are the ones the .NET and Go provider estimates read.

A Python agent in the fleet's scope (the same in every language), on a fixed clock and fixed identifiers, wraps a stub
memory provider (it adds one message and counts both its hooks) under the key ``stub-memory`` with the holdout at rate
0.5 and the default salt, and runs eight captured runs (``5079b000-0000-4000-8000-00000000000N``, N = 1 to 8), each
finalized, some verified and some failed, in both arms. In the PostgreSQL leg the rows the Python store wrote (the
records, their initial lifecycle events and the provider holdout assignments), every column in PostgreSQL's own text
form, must be exactly ``tests/cross-language/python-agent-provider-rows.json``. Then the .NET, Python and Go agents'
rows files go into one database, and the Python ``estimate_provider_benefit`` over the fleet's scope and key must be
exactly ``tests/cross-language/python-provider-fleet-estimate.json``, which the .NET contract tests compare with their
own estimate of the same rows. Set ``AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1`` to rewrite both files on purpose, then
read the diff.
"""

from __future__ import annotations

import datetime
import json
import os
import uuid
from collections.abc import Callable
from pathlib import Path
from typing import Any
from unittest.mock import patch

import pytest

pytest.importorskip("agent_framework")

from _benefit import TOLERANCE
from _contract import REPOSITORY, json_difference, read_json
from _maf import ScriptedClient
from _postgres import PG_RETRIEVAL_POLICY, Server
from _sync import run
from agent_framework import Agent, ContextProvider, Message, tool
from test_cross_language_holdout import EVENT_COLUMNS, _insert_raw

import agentexperience.maf
from agentexperience import (
    AuthorizationBounds,
    Duration,
    EnvironmentFingerprint,
    HoldoutOptions,
    Instant,
    Scope,
    assign_provider_holdout,
)
from agentexperience.benefit import provider_estimate_to_json
from agentexperience.inmemory import ExperienceStore, InMemoryExperienceStore
from agentexperience.maf import (
    CaptureFailure,
    ExperienceIdentity,
    InjectionFailure,
    ProviderHoldoutRecordResult,
    exit_code,
    tool_succeeded,
    use_agent_experience,
    use_measured_memory,
)
from agentexperience.records import COLUMN_NAMES

HERE = Path(__file__).resolve().parent / "cross-language"
ROWS_GOLDEN = HERE / "python-agent-provider-rows.json"
FLEET_ESTIMATE_GOLDEN = HERE / "python-provider-fleet-estimate.json"
DOTNET_ROWS = (
    REPOSITORY / "tests" / "AgentExperience.Contract.Tests" / "cross-language" / "dotnet-provider-fleet-rows.json"
)
GO_ROWS = REPOSITORY / "go" / "maf" / "testdata" / "cross-language" / "go-agent-provider-rows.json"
UPDATE_VARIABLE = "AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE"
KEY = "stub-memory"
RATE = 0.5
HOLDOUT = HoldoutOptions(rate=RATE)
SCOPE = Scope("cross-language-provider", "provider-app", "provider-project", agent_id="provider-agent")
IDENTITY = ExperienceIdentity("python-principal", SCOPE)
ENVIRONMENT = EnvironmentFingerprint("python-host", "CPython 3", "linux", "1.0.0", {"region": "eu-west"})
GOLDEN_SOURCE_VERSION = "0.0.0+cross-language"
START = Instant.parse("2026-03-02T11:00:00.1234567+00:00")
MEASURED_RUNS = tuple(uuid.UUID(f"5079b000-0000-4000-8000-{n:012x}") for n in range(1, 9))
# Whether each measured run's tests pass (exit 0) or fail (exit 3): verified and failed outcomes in each arm.
PASSES = (True, False, True, True, False, True, False, True)
MEMORY = "Memory: the customer was refunded twice before."
PROVIDER_COLUMNS = (
    "run_id",
    "provider_key",
    "arm",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "assigned_at",
)
TABLES = ("experience_records", "lifecycle_events", "experience_provider_holdout_assignments")


class StubMemory(ContextProvider):
    """A memory provider that remembers one thing about every customer, and counts its two hooks."""

    def __init__(self) -> None:
        super().__init__("stub-memory-provider")
        self.before = 0
        self.after = 0

    async def before_run(self, *, agent: Any, session: Any, context: Any, state: dict[str, Any]) -> None:
        self.before += 1
        context.extend_messages(self, [Message(role="user", contents=[MEMORY])])

    async def after_run(self, *, agent: Any, session: Any, context: Any, state: dict[str, Any]) -> None:
        self.after += 1


def task(n: int) -> str:
    return f"Ticket #{5300 + n}: a refund is stuck on a lock. Triage it."


def fixed_clock() -> Callable[[], Instant]:
    """A clock that moves 1.25 seconds every time it is read, from a fixed start."""
    ticks = [0]

    def now() -> Instant:
        ticks[0] += 1
        return START.add(Duration.from_seconds(1.25 * ticks[0]))

    return now


def run_provider_agent(store: ExperienceStore) -> tuple[list[Any], list[ProviderHoldoutRecordResult]]:
    """The eight measured runs, deterministic, each settled before the next."""
    finalized: list[Any] = []
    problems: list[CaptureFailure | InjectionFailure] = []
    recorded: list[ProviderHoldoutRecordResult] = []
    outcomes = iter("exit=0" if passes else "exit=3" for passes in PASSES)
    planned: list[uuid.UUID | None] = [None]
    counter = [0]

    def new_id() -> uuid.UUID:
        # A run's first ID is its run ID (capture opens the run before anything else asks for one).
        if planned[0] is not None:
            run_id, planned[0] = planned[0], None
            return run_id
        counter[0] += 1
        return uuid.UUID(f"5079b0de-0000-4000-8000-{counter[0]:012d}")

    def run_tests(suite: str) -> str:
        """Runs a test suite."""
        return next(outcomes)

    client = ScriptedClient(calls=1, arguments={"suite": "unit"})
    agent = use_agent_experience(
        Agent(client=client, name="Provider Agent", tools=[tool(run_tests)]),
        store=store,
        retrieval_policy=PG_RETRIEVAL_POLICY,
        identity=IDENTITY,
        verify=tool_succeeded("run_tests", artifact_revision="build-7"),
        classify_tool_result=exit_code("run_tests"),
        environment=ENVIRONMENT,
        inject=False,
        on_capture_failure=problems.append,
        on_injection_failure=problems.append,
        on_finalized=finalized.append,
        clock=fixed_clock(),
        new_id=new_id,
    )
    memory = StubMemory()
    agent.context_providers.append(memory)
    wrapper = use_measured_memory(agent, memory, KEY, holdout=HOLDOUT, on_recorded=recorded.append)

    async def settled(run_id: uuid.UUID, text: str) -> None:
        planned[0] = run_id
        await agent.run(text)
        await wrapper.wait_for_records()

    with patch.object(agentexperience.maf, "__version__", GOLDEN_SOURCE_VERSION):
        for n, run_id in enumerate(MEASURED_RUNS, start=1):
            run(settled(run_id, task(n)))
    assert problems == []
    assert (memory.before, memory.after) == (len(MEASURED_RUNS), len(MEASURED_RUNS))
    assert [item.record.source_run_id for item in finalized] == list(MEASURED_RUNS)
    assert [item.record.outcome.status for item in finalized] == [
        "Verified" if passes else "Failed" for passes in PASSES
    ]
    # Every run was assigned, in the arm the assignment function gives, and both arms are represented.
    arms = [assign_provider_holdout(run_id, KEY, HOLDOUT) for run_id in MEASURED_RUNS]
    assert set(arms) == {"Treatment", "Holdout"}
    assert [(item.outcome, item.assignment and (item.assignment.run_id, item.assignment.arm)) for item in recorded] == [
        ("Recorded", (run_id, arm)) for run_id, arm in zip(MEASURED_RUNS, arms, strict=True)
    ]
    # A held-out run was shown nothing of the memory; a treatment run was shown it.
    for request, arm in zip(client.first_requests(), arms, strict=True):
        assert any(MEMORY in (message.text or "") for message in request) is (arm == "Treatment")
    return finalized, recorded


def test_the_python_agent_assigns_each_run_to_the_arm_dotnet_would_and_names_what_the_rows_file_names() -> None:
    store = InMemoryExperienceStore()
    run_provider_agent(store)
    rows = [(str(row.run_id), row.provider_key, row.arm) for row in store.provider_holdout_assignments()]
    golden = [
        (row["run_id"], row["provider_key"], row["arm"])
        for row in read_json(ROWS_GOLDEN)["experience_provider_holdout_assignments"]
    ]
    assert rows == golden


# ---- PostgreSQL --------------------------------------------------------------------------------------------------


def _raw_rows(
    admin: Server, database: str, table: str, columns: tuple[str, ...], order: tuple[str, ...]
) -> list[dict[str, str | None]]:
    """The rows of ``table`` in the fleet's tenant, every given column in PostgreSQL's text form, read in UTC."""
    import psycopg
    from psycopg import sql

    query = sql.SQL("SELECT {columns} FROM {table} WHERE tenant_id = %s ORDER BY {order}").format(
        columns=sql.SQL(", ").join(sql.SQL("{}::text").format(sql.Identifier(column)) for column in columns),
        table=sql.Identifier("agent_experience", table),
        order=sql.SQL(", ").join(sql.Identifier(column) for column in order),
    )
    with psycopg.connect(admin.conninfo(database)) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(query, (SCOPE.tenant_id,)).fetchall()
    return [dict(zip(columns, row, strict=True)) for row in rows]


@pytest.mark.postgres
def test_the_python_agent_writes_the_provider_holdout_rows_the_dotnet_estimate_reads(pg_server: Server) -> None:
    from _postgres import application_conninfo, create_database

    from agentexperience.finalize import FinalizedExperience
    from agentexperience.postgres import PostgresExperienceStore

    class RecordedWhenItOccurred(PostgresExperienceStore):
        """The store, recording each initial event when it occurred, so the rows written are deterministic."""

        def insert_finalized(
            self,
            bounds: AuthorizationBounds,
            finalized: FinalizedExperience,
            *,
            actor: str,
            recorded_at: datetime.datetime | None = None,
        ) -> None:
            occurred = finalized.event.occurred_at.to_datetime()
            super().insert_finalized(bounds, finalized, actor=actor, recorded_at=recorded_at or occurred)

    database = create_database(pg_server, "python_provider_agent")
    store = RecordedWhenItOccurred(application_conninfo(pg_server, database))
    finalized, recorded = run_provider_agent(store)

    rows = {
        "experience_records": _raw_rows(pg_server, database, "experience_records", COLUMN_NAMES, ("source_run_id",)),
        "lifecycle_events": _raw_rows(
            pg_server, database, "lifecycle_events", EVENT_COLUMNS, ("occurred_at", "event_id")
        ),
        "experience_provider_holdout_assignments": _raw_rows(
            pg_server,
            database,
            "experience_provider_holdout_assignments",
            PROVIDER_COLUMNS,
            ("run_id", "provider_key"),
        ),
    }
    assert len(rows["experience_records"]) == len(rows["lifecycle_events"]) == len(finalized) == len(MEASURED_RUNS)
    arms = {str(item.assignment.run_id): item.assignment.arm for item in recorded if item.assignment is not None}
    assert [(row["run_id"], row["provider_key"], row["arm"]) for row in rows[TABLES[2]]] == sorted(
        (run_id, KEY, arm) for run_id, arm in arms.items()
    )

    if os.environ.get(UPDATE_VARIABLE) == "1":
        ROWS_GOLDEN.write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    difference = json_difference(read_json(ROWS_GOLDEN), rows)
    assert difference is None, f"{difference} (set {UPDATE_VARIABLE}=1 to rewrite {ROWS_GOLDEN.name} on purpose)"


@pytest.mark.postgres
def test_the_python_estimate_of_the_dotnet_python_and_go_fleet_is_the_checked_in_one(pg_server: Server) -> None:
    from _postgres import application_conninfo, create_database

    from agentexperience import estimate_provider_benefit
    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "python_provider_fleet_estimate")
    conninfo = application_conninfo(pg_server, database)
    runs = 0
    for path in (DOTNET_ROWS, ROWS_GOLDEN, GO_ROWS):
        assert path.is_file(), f"{path.relative_to(REPOSITORY)} is missing."
        rows = read_json(path)
        for table in TABLES:
            for row in rows[table]:
                _insert_raw(conninfo, table, row)
        assignments = rows[TABLES[2]]
        assert assignments, path.name
        # Every run of every language is in the arm the assignment function gives it, under the key, in the scope.
        for row in assignments:
            assert row["provider_key"] == KEY, (path.name, row)
            assert row["arm"] == assign_provider_holdout(uuid.UUID(str(row["run_id"])), KEY, HOLDOUT), (path.name, row)
            fields = ("tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id")
            assert tuple(row[name] for name in fields) == SCOPE.fields(), (path.name, row)
        assert {row["arm"] for row in assignments} == {"Treatment", "Holdout"}, path.name
        runs += len(assignments)

    estimate = estimate_provider_benefit(PostgresExperienceStore(conninfo), AuthorizationBounds.of(SCOPE), SCOPE, KEY)
    assert estimate.outcome == "Found" and estimate.tracked
    overall = estimate.overall
    assert overall.pending == 0 and overall.mixed_arm == 0
    assert overall.treatment.runs + overall.holdout.runs + overall.unverified == runs
    actual = json.loads(json.dumps(provider_estimate_to_json(estimate)))
    assert list(actual) == ["providerKey", "minimumRunsPerArm", "overall", "tracked", "outcome"]
    if os.environ.get(UPDATE_VARIABLE) == "1":
        FLEET_ESTIMATE_GOLDEN.write_text(json.dumps(actual, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    difference = json_difference(read_json(FLEET_ESTIMATE_GOLDEN), actual, tolerance=TOLERANCE)
    assert difference is None, (
        f"{difference} (set {UPDATE_VARIABLE}=1 to rewrite {FLEET_ESTIMATE_GOLDEN.name} on purpose)"
    )
