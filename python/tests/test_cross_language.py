"""
Story 22.3's cross-language proof, read from the raw rows since story 22.8.

- **Python writes, .NET reads.** A Python MAF agent, on a fixed clock and fixed identifiers, captures, verifies and
  stores two runs. The records it writes must be exactly ``tests/cross-language/python-agent-records.json`` (checked in
  memory here, and written to and read back from PostgreSQL in the PostgreSQL leg). In the PostgreSQL leg the rows the
  Python store wrote, every column in PostgreSQL's own text form, must also be exactly
  ``tests/cross-language/python-agent-rows.json``. The .NET contract tests insert those raw rows as they are into a
  schema the .NET migrator applied, read every record back through ``PostgresExperienceRecordStore.GetAsync`` (it
  must be the records file), check each initial lifecycle event, and compare every column with what the .NET store
  writes for the same record (``PythonAgentRecordsPostgresRoundTripTests``). So a Python writer that encodes a column
  differently from .NET fails there, even when the Python reader undoes it. Set
  ``AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1`` to rewrite the files on purpose, then read the diff. Since contract 1.4
  the rows include ``experience_injections``, the injection the second run recorded: the .NET test reads it through
  the .NET lessons view and compares every column with what the .NET store records for the same injection.
- **.NET writes, Python reads.** The records the .NET library finalized (the contract's finalize fixtures, generated
  from .NET and checked against it on every build) are written into PostgreSQL as stored, and a Python agent on the
  same scope is injected with them. No .NET process runs at test time: the fixtures are the .NET-written records.
"""

from __future__ import annotations

import datetime
import json
import os
import uuid
from collections.abc import Callable
from dataclasses import replace
from pathlib import Path
from typing import Any
from unittest.mock import patch

import pytest

pytest.importorskip("agent_framework")

from _contract import FIXTURES, cases, json_difference, read_json
from _maf import NEXT_TICKET, TICKET, ScriptedClient
from _postgres import Server
from _sync import run
from agent_framework import Agent, tool
from test_contract import assert_valid_record

import agentexperience.maf
from agentexperience import (
    AuthorizationBounds,
    Duration,
    EnvironmentFingerprint,
    Instant,
    Scope,
    from_stored,
    to_stored,
)
from agentexperience.inmemory import ExperienceStore, InMemoryExperienceStore
from agentexperience.maf import (
    HISTORICAL_REFERENCE_KEY,
    CaptureFailure,
    ExperienceContextProvider,
    ExperienceIdentity,
    InjectionFailure,
    InjectionRecordResult,
    InjectionResult,
    exit_code,
    tool_succeeded,
    use_agent_experience,
)
from agentexperience.records import COLUMN_NAMES

GOLDEN = Path(__file__).resolve().parent / "cross-language" / "python-agent-records.json"
ROWS_GOLDEN = GOLDEN.parent / "python-agent-rows.json"
# The columns the Python store writes to each table, in the order it writes them.
EVENT_COLUMNS = (
    "event_id",
    "experience_id",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "prior_status",
    "current_status",
    "reason",
    "producer",
    "occurred_at",
    "recorded_at",
    "expected_revision",
    "applied_revision",
    "actor",
)
INJECTION_COLUMNS = (
    "experience_id",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "last_injected_at",
    "injection_count",
)
UPDATE_VARIABLE = "AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE"
SCOPE = Scope("contract-tenant", "contract-app", "contract-project", agent_id="python-agent")
IDENTITY = ExperienceIdentity("python-principal", SCOPE)
ENVIRONMENT = EnvironmentFingerprint("python-host", "CPython 3", "linux", "1.0.0", {"region": "eu-west"})
GOLDEN_SOURCE_VERSION = "0.0.0+cross-language"
START = Instant.parse("2026-03-02T09:00:00.1234567+00:00")


def fixed_clock() -> Callable[[], Instant]:
    """A clock that moves 1.25 seconds every time it is read, from a fixed start."""
    ticks = [0]

    def now() -> Instant:
        ticks[0] += 1
        return START.add(Duration.from_seconds(1.25 * ticks[0]))

    return now


def fixed_ids() -> Callable[[], uuid.UUID]:
    counter = [0]

    def new_id() -> uuid.UUID:
        counter[0] += 1
        return uuid.UUID(f"0000c0de-0000-4000-8000-{counter[0]:012d}")

    return new_id


async def run_settled(agent: Any, text: str) -> Any:
    """Runs the agent, then waits for the injection it recorded in the background, on the same event loop."""
    response = await agent.run(text)
    for provider in agent.context_providers:
        if isinstance(provider, ExperienceContextProvider):
            await provider.wait_for_injection_records()
    return response


def run_python_agent(store: ExperienceStore) -> tuple[list[Any], ScriptedClient, list[Any]]:
    """Two runs of a Python agent, deterministic: the first fails a test run once, the second is shown the lesson,
    which is recorded as injected."""
    finalized: list[Any] = []
    problems: list[CaptureFailure | InjectionFailure] = []
    recorded: list[InjectionRecordResult] = []
    exits = iter(["exit=3", "exit=0", "exit=3", "exit=0"])

    def run_tests(suite: str) -> str:
        """Runs a test suite."""
        return next(exits)

    client = ScriptedClient(calls=2, arguments={"suite": "unit"})
    agent = use_agent_experience(
        Agent(client=client, name="Python Agent", tools=[tool(run_tests)]),
        store=store,
        identity=IDENTITY,
        verify=tool_succeeded("run_tests", artifact_revision="build-7"),
        classify_tool_result=exit_code("run_tests"),
        environment=ENVIRONMENT,
        on_capture_failure=problems.append,
        on_injection_failure=problems.append,
        on_finalized=finalized.append,
        on_injection_recorded=recorded.append,
        clock=fixed_clock(),
        new_id=fixed_ids(),
    )
    # The package version is recorded in each run's provenance; pinned here so the checked-in records do not change
    # with every version bump (on either side: the .NET test reads this same file).
    with patch.object(agentexperience.maf, "__version__", GOLDEN_SOURCE_VERSION):
        run(run_settled(agent, TICKET))
        run(run_settled(agent, NEXT_TICKET))
    assert problems == []
    assert [(item.outcome, item.experience_ids) for item in recorded] == [
        ("Recorded", (finalized[0].record.experience_id,))
    ]
    return finalized, client, [to_stored(item.record) for item in finalized]


def _golden_text(records: Any) -> str:
    return json.dumps(records, indent=2, ensure_ascii=False) + "\n"


def test_the_python_agent_writes_exactly_the_checked_in_records() -> None:
    store = InMemoryExperienceStore()
    finalized, client, stored = run_python_agent(store)

    # The second run's block delivered the first run's lesson: recorded once, at the instant the block was built.
    (first_id,) = store.injections()
    assert first_id == finalized[0].record.experience_id
    assert store.injections()[first_id].injection_count == 1

    assert [item.record.status for item in finalized] == ["Validated", "Validated"]
    assert [len(item.record.provenance.exposed_to) for item in finalized] == [0, 1]
    counts = [
        sum(1 for m in request if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True)
        for request in client.requests
    ]
    assert counts == [0, 0, 0, 1, 1, 1]  # the second run's three requests each carry the one block
    assert {item.record.provenance.source_version for item in finalized} == {GOLDEN_SOURCE_VERSION}
    for record in stored:
        assert_valid_record(record)

    if os.environ.get(UPDATE_VARIABLE) == "1":
        GOLDEN.parent.mkdir(parents=True, exist_ok=True)
        GOLDEN.write_text(_golden_text(stored), encoding="utf-8")
    difference = json_difference(read_json(GOLDEN), stored)
    assert difference is None, f"{difference} (set {UPDATE_VARIABLE}=1 to rewrite {GOLDEN.name} on purpose)"


def test_the_checked_in_records_read_back_through_the_python_reader_unchanged() -> None:
    for stored in read_json(GOLDEN):
        assert_valid_record(stored)
        assert json_difference(stored, to_stored(from_stored(stored))) is None


# ---- PostgreSQL --------------------------------------------------------------------------------------------------


def _raw_rows(
    admin: Server, database: str, table: str, columns: tuple[str, ...], order: tuple[str, ...]
) -> list[dict[str, str | None]]:
    """The rows of ``table`` in the agent's scope, every given column in PostgreSQL's text form, read in UTC."""
    import psycopg
    from psycopg import sql

    query = sql.SQL("SELECT {columns} FROM {table} WHERE tenant_id = %s AND agent_id = %s ORDER BY {order}").format(
        columns=sql.SQL(", ").join(sql.SQL("{}::text").format(sql.Identifier(column)) for column in columns),
        table=sql.Identifier("agent_experience", table),
        order=sql.SQL(", ").join(sql.Identifier(column) for column in order),
    )
    with psycopg.connect(admin.conninfo(database)) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(query, (SCOPE.tenant_id, SCOPE.agent_id)).fetchall()
    return [dict(zip(columns, row, strict=True)) for row in rows]


@pytest.mark.postgres
def test_the_python_agent_writes_to_postgresql_what_the_dotnet_store_reads(pg_server: Server) -> None:
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

    database = create_database(pg_server, "python_agent")
    store = RecordedWhenItOccurred(application_conninfo(pg_server, database))
    finalized, _, _ = run_python_agent(store)

    # The rows themselves, as the .NET test inserts them: it reads them back through the .NET store, and compares
    # every column with what the .NET store writes for the same record (PythonAgentRecordsPostgresRoundTripTests).
    rows = {
        "experience_records": _raw_rows(pg_server, database, "experience_records", COLUMN_NAMES, ("source_run_id",)),
        "lifecycle_events": _raw_rows(
            pg_server, database, "lifecycle_events", EVENT_COLUMNS, ("occurred_at", "event_id")
        ),
        "experience_injections": _raw_rows(
            pg_server, database, "experience_injections", INJECTION_COLUMNS, ("experience_id",)
        ),
    }
    assert [row["experience_id"] for row in rows["experience_injections"]] == [str(finalized[0].record.experience_id)]
    assert len(rows["experience_records"]) == len(rows["lifecycle_events"]) == len(finalized)
    if os.environ.get(UPDATE_VARIABLE) == "1":
        ROWS_GOLDEN.write_text(_golden_text(rows), encoding="utf-8")
    difference = json_difference(read_json(ROWS_GOLDEN), rows)
    assert difference is None, f"{difference} (set {UPDATE_VARIABLE}=1 to rewrite {ROWS_GOLDEN.name} on purpose)"

    golden = read_json(GOLDEN)
    bounds = AuthorizationBounds.of(SCOPE)
    for expected, item in zip(golden, finalized, strict=True):
        read = store.get(bounds, SCOPE, item.record.experience_id)
        assert read is not None
        difference = json_difference(expected, to_stored(read))
        assert difference is None, difference


def _dotnet_records() -> list[dict[str, Any]]:
    """Every record the .NET library finalized in the contract's finalize fixtures, once per experience ID."""
    records: dict[str, dict[str, Any]] = {}
    for name in cases("finalize"):
        stored = read_json(FIXTURES / "finalize" / name / "expected-record.json")
        records.setdefault(stored["columns"]["experience_id"], stored)
    return list(records.values())


@pytest.mark.postgres
def test_a_lesson_the_dotnet_library_finalized_is_injected_into_the_python_agent(pg_server: Any) -> None:
    from _postgres import application_conninfo, create_database

    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "dotnet_lessons")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    seeded = _dotnet_records()
    for stored in seeded:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    scope = Scope("contract-tenant", "contract-app", "contract-project", agent_id="refund-agent")
    deploy = next(s for s in seeded if s["columns"]["task_id"] == "deploy-service" and s["columns"]["team_id"] is None)
    injections: list[InjectionResult] = []
    problems: list[InjectionFailure] = []
    recorded: list[InjectionRecordResult] = []
    client = ScriptedClient(calls=0)
    agent = use_agent_experience(
        Agent(client=client, name="refund-agent"),
        store=store,
        identity=ExperienceIdentity("python-principal", scope),
        on_injected=injections.append,
        on_injection_failure=problems.append,
        on_injection_recorded=recorded.append,
    )

    run(run_settled(agent, "Deploy service X to prod"))

    # The Python agent recorded that it injected the .NET lesson, in the lesson's own scope.
    assert [item.outcome for item in recorded] == ["Recorded"]
    import psycopg

    with psycopg.connect(pg_server.conninfo(database)) as connection:
        rows = connection.execute(
            "SELECT experience_id::text, agent_id, injection_count FROM agent_experience.experience_injections"
        ).fetchall()
    assert rows == [(deploy["columns"]["experience_id"], "refund-agent", 1)]

    assert problems == []
    assert [str(experience_id) for experience_id in injections[0].experience_ids] == [
        deploy["columns"]["experience_id"]
    ]
    (block,) = client.blocks()
    assert block.startswith("=== BEGIN HISTORICAL REFERENCE")
    assert f"--- RECORD 1: {deploy['columns']['task_id']} ---" in block
    assert f"Lesson: {deploy['payload']['reflection']['lesson']}" in block
    first = client.first_requests()[0]
    assert sum(1 for m in first if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True) == 1

    # A quarantined .NET record is never injected: the refund task's block holds only the validated refund lessons.
    run(agent.run("Refund ticket 43 is stuck in pending"))
    assert len(injections) == 2
    quarantined = {s["columns"]["experience_id"] for s in seeded if s["columns"]["status"] == "Quarantined"}
    injected = {str(i) for i in injections[-1].experience_ids}
    assert injected and not quarantined & injected

    # Another agent's scope, on the same database, sees none of them.
    other: list[InjectionResult] = []
    other_client = ScriptedClient(calls=0)
    other_agent = use_agent_experience(
        Agent(client=other_client, name="other-agent"),
        store=store,
        identity=ExperienceIdentity("python-principal", replace(scope, agent_id="other-agent")),
        on_injected=other.append,
        on_injection_failure=problems.append,
    )
    run(other_agent.run("Deploy service X to prod"))
    run(other_agent.run("Refund ticket 43 is stuck in pending"))
    assert other == [] and other_client.blocks() == [] and problems == []
