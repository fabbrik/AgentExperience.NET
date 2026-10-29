"""
Story 25.6's cross-language proof, the Python half: a Python MAF agent takes part in the injection holdout, and the
rows it writes are the ones the .NET benefit estimate reads.

A Python agent in one scope, on a fixed clock and fixed identifiers, with the holdout at rate 0.5 and the default
salt, runs a learning run (``5079a000-0000-4000-8000-000000000000``, verified: the lesson) and then eight measured runs
(``...-00000000000N``, N = 1 to 8) on a similar task, each finalized, some verified and some failed. Runs 1, 2 and 6 are
held out at 0.5; the others are the treatment arm. In the PostgreSQL leg the rows the Python store wrote (the records,
their initial lifecycle events and the holdout assignments), every column in PostgreSQL's own text form, must be
exactly ``tests/cross-language/python-agent-holdout-rows.json``. The .NET contract tests insert those rows, with the Go
agent's, into one database and run ``AgentExperienceBenefit.EstimateAsync`` over the scope: every finalized run counts
in its arm. Set ``AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1`` to rewrite the file on purpose, then read the diff.

Contract 1.9: the agent's chat client reports the model ``python-agent-model``, which capture records in every run's
environment. In the PostgreSQL leg the Python and the Go agents' rows files go into one database, and the Python
``estimate_benefit`` over the fleet's scope, split by model, must be exactly
``tests/cross-language/python-fleet-holdout-estimate.json``; the .NET contract tests estimate the same rows with
``AgentExperienceBenefit.EstimateAsync`` and must find the same numbers.
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

from _benefit import TOLERANCE, estimate_json
from _contract import REPOSITORY, json_difference, read_json
from _maf import ScriptedClient
from _postgres import Server
from _sync import run
from agent_framework import Agent, tool

import agentexperience.maf
from agentexperience import (
    DEFAULT_HOLDOUT_SALT,
    AuthorizationBounds,
    Duration,
    EnvironmentFingerprint,
    Instant,
    Scope,
    assign_holdout,
)
from agentexperience.inmemory import ExperienceStore, InMemoryExperienceStore
from agentexperience.maf import (
    CaptureFailure,
    ExperienceContextProvider,
    ExperienceIdentity,
    HoldoutOptions,
    HoldoutRecordResult,
    InjectionFailure,
    exit_code,
    tool_succeeded,
    use_agent_experience,
)
from agentexperience.model import MODEL_METADATA_KEY
from agentexperience.records import COLUMN_NAMES

ROWS_GOLDEN = Path(__file__).resolve().parent / "cross-language" / "python-agent-holdout-rows.json"
GO_ROWS = REPOSITORY / "go" / "maf" / "testdata" / "cross-language" / "go-agent-holdout-rows.json"
FLEET_ESTIMATE_GOLDEN = Path(__file__).resolve().parent / "cross-language" / "python-fleet-holdout-estimate.json"
MODEL = "python-agent-model"
UPDATE_VARIABLE = "AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE"
SCOPE = Scope("cross-language-holdout", "holdout-app", "holdout-project", agent_id="holdout-agent")
IDENTITY = ExperienceIdentity("python-principal", SCOPE)
ENVIRONMENT = EnvironmentFingerprint("python-host", "CPython 3", "linux", "1.0.0", {"region": "eu-west"})
GOLDEN_SOURCE_VERSION = "0.0.0+cross-language"
START = Instant.parse("2026-03-02T09:00:00.1234567+00:00")
RATE = 0.5
LEARNING_RUN = uuid.UUID("5079a000-0000-4000-8000-000000000000")
MEASURED_RUNS = tuple(uuid.UUID(f"5079a000-0000-4000-8000-{n:012x}") for n in range(1, 9))
# Whether each measured run's tests pass (exit 0) or fail (exit 3): a mix of verified and failed outcomes in each arm.
PASSES = (True, False, True, False, True, True, False, True)
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
HOLDOUT_COLUMNS = (
    "run_id",
    "experience_id",
    "revision",
    "arm",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "assigned_at",
)


class ModelClient(ScriptedClient):
    """The scripted client, reporting the model it runs: what capture records as the run's model (contract 1.9)."""

    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self.model = MODEL
        self.model_id = MODEL


def task(n: int) -> str:
    return f"Ticket #{5100 + n}: a refund is stuck on a lock. Triage it."


def fixed_clock() -> Callable[[], Instant]:
    """A clock that moves 1.25 seconds every time it is read, from a fixed start."""
    ticks = [0]

    def now() -> Instant:
        ticks[0] += 1
        return START.add(Duration.from_seconds(1.25 * ticks[0]))

    return now


def run_holdout_agent(store: ExperienceStore) -> tuple[list[Any], list[HoldoutRecordResult]]:
    """The learning run and the eight measured runs, deterministic, each settled before the next."""
    finalized: list[Any] = []
    problems: list[CaptureFailure | InjectionFailure] = []
    held: list[HoldoutRecordResult] = []
    outcomes = iter(["exit=0", *("exit=0" if passes else "exit=3" for passes in PASSES)])
    planned: list[uuid.UUID | None] = [None]
    counter = [0]

    def new_id() -> uuid.UUID:
        # A run's first ID is its run ID (capture opens the run before anything else asks for one).
        if planned[0] is not None:
            run_id, planned[0] = planned[0], None
            return run_id
        counter[0] += 1
        return uuid.UUID(f"5079c0de-0000-4000-8000-{counter[0]:012d}")

    def run_tests(suite: str) -> str:
        """Runs a test suite."""
        return next(outcomes)

    agent = use_agent_experience(
        Agent(client=ModelClient(calls=1, arguments={"suite": "unit"}), name="Holdout Agent", tools=[tool(run_tests)]),
        store=store,
        identity=IDENTITY,
        verify=tool_succeeded("run_tests", artifact_revision="build-7"),
        classify_tool_result=exit_code("run_tests"),
        environment=ENVIRONMENT,
        on_capture_failure=problems.append,
        on_injection_failure=problems.append,
        on_finalized=finalized.append,
        on_holdout_recorded=held.append,
        holdout=HoldoutOptions(rate=RATE),
        clock=fixed_clock(),
        new_id=new_id,
    )
    (provider,) = [item for item in agent.context_providers if isinstance(item, ExperienceContextProvider)]

    async def settled(run_id: uuid.UUID, text: str) -> None:
        planned[0] = run_id
        await agent.run(text)
        await provider.wait_for_injection_records()

    with patch.object(agentexperience.maf, "__version__", GOLDEN_SOURCE_VERSION):
        for n, run_id in enumerate((LEARNING_RUN, *MEASURED_RUNS)):
            run(settled(run_id, task(n)))
    assert problems == []
    assert [item.record.source_run_id for item in finalized] == [LEARNING_RUN, *MEASURED_RUNS]
    assert finalized[0].record.status == "Validated"
    assert all(item.record.environment.metadata.get(MODEL_METADATA_KEY) == MODEL for item in finalized)
    assert [item.record.outcome.status for item in finalized[1:]] == [
        "Verified" if passes else "Failed" for passes in PASSES
    ]
    # Every measured run had a block, so every one was assigned, in the arm the assignment function gives; the
    # learning run had nothing to inject and was assigned nothing.
    assert [(item.outcome, item.assignment.run_id, item.assignment.arm) for item in held] == [
        ("Recorded", run_id, assign_holdout(run_id, DEFAULT_HOLDOUT_SALT, RATE)) for run_id in MEASURED_RUNS
    ]
    assert [item.assignment.arm for item in held] == [
        "Holdout",
        "Holdout",
        "Treatment",
        "Treatment",
        "Treatment",
        "Holdout",
        "Treatment",
        "Treatment",
    ]
    # A held-out run was shown nothing; a treatment run was shown what its block named.
    for item, record in zip(held, finalized[1:], strict=True):
        exposed = {exposure.experience_id for exposure in record.record.provenance.exposed_to}
        named = {exposure.experience_id for exposure in item.assignment.records}
        assert exposed == (set() if item.assignment.arm == "Holdout" else named)
    return finalized, held


def test_the_python_agent_assigns_each_run_to_the_arm_dotnet_would_and_names_what_the_rows_file_names() -> None:
    store = InMemoryExperienceStore()
    run_holdout_agent(store)
    rows = [(str(row.run_id), str(row.experience_id), row.arm) for row in store.holdout_assignments()]
    golden = [
        (row["run_id"], row["experience_id"], row["arm"])
        for row in read_json(ROWS_GOLDEN)["experience_holdout_assignments"]
    ]
    assert rows == golden


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
def test_the_python_agent_writes_the_holdout_rows_the_dotnet_estimate_reads(pg_server: Server) -> None:
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

    database = create_database(pg_server, "python_holdout_agent")
    store = RecordedWhenItOccurred(application_conninfo(pg_server, database))
    finalized, held = run_holdout_agent(store)

    rows = {
        "experience_records": _raw_rows(pg_server, database, "experience_records", COLUMN_NAMES, ("source_run_id",)),
        "lifecycle_events": _raw_rows(
            pg_server, database, "lifecycle_events", EVENT_COLUMNS, ("occurred_at", "event_id")
        ),
        "experience_holdout_assignments": _raw_rows(
            pg_server, database, "experience_holdout_assignments", HOLDOUT_COLUMNS, ("run_id", "experience_id")
        ),
    }
    assert len(rows["experience_records"]) == len(rows["lifecycle_events"]) == len(finalized) == 9
    # Every row carries its run's arm, and every measured run has rows: one per record its block named.
    arms = {str(item.assignment.run_id): item.assignment.arm for item in held}
    assert {row["run_id"] for row in rows["experience_holdout_assignments"]} == set(arms)
    for row in rows["experience_holdout_assignments"]:
        assert row["arm"] == arms[str(row["run_id"])]
    assert len(rows["experience_holdout_assignments"]) == sum(len(item.assignment.records) for item in held)

    if os.environ.get(UPDATE_VARIABLE) == "1":
        ROWS_GOLDEN.write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    difference = json_difference(read_json(ROWS_GOLDEN), rows)
    assert difference is None, f"{difference} (set {UPDATE_VARIABLE}=1 to rewrite {ROWS_GOLDEN.name} on purpose)"


def _insert_raw(conninfo: str, table: str, row: dict[str, str | None]) -> None:
    """One row of a rows file, every column from its PostgreSQL text form, as the application role under bounds
    declared for the row's scope: how the .NET contract tests insert a port agent's rows."""
    import psycopg
    from psycopg import sql

    from agentexperience.postgres import DECLARE_BOUNDS_SQL, contract_sql, to_driver_placeholders

    scope = Scope(
        str(row["tenant_id"]),
        str(row["application_id"]),
        str(row["project_id"]),
        row["team_id"],
        row["agent_id"],
        row["user_id"],
    )
    with psycopg.connect(conninfo) as connection, connection.transaction():
        connection.execute("SET LOCAL TIME ZONE 'UTC'")
        connection.execute(
            to_driver_placeholders(contract_sql(DECLARE_BOUNDS_SQL)), AuthorizationBounds.of(scope).parameters()
        )
        types: dict[str, str] = {
            str(name): str(kind)
            for name, kind in connection.execute(
                "SELECT attname, format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = "
                "('agent_experience.' || %s)::regclass AND attnum > 0 AND NOT attisdropped",
                (table,),
            ).fetchall()
        }
        names = list(row)
        assert all(name in types for name in names), (table, names)
        connection.execute(
            sql.SQL("INSERT INTO {table} ({columns}) VALUES ({values})").format(
                table=sql.Identifier("agent_experience", table),
                columns=sql.SQL(", ").join(sql.Identifier(name) for name in names),
                values=sql.SQL(", ").join([sql.SQL("CAST(%s AS {})").format(sql.SQL(types[name])) for name in names]),
            ),
            [row[name] for name in names],
        )


@pytest.mark.postgres
def test_the_python_estimate_of_the_python_and_go_fleet_is_the_checked_in_one(pg_server: Server) -> None:
    from _postgres import application_conninfo, create_database

    from agentexperience import BenefitOptions, estimate_benefit
    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "python_fleet_estimate")
    conninfo = application_conninfo(pg_server, database)
    for path in (ROWS_GOLDEN, GO_ROWS):
        rows = read_json(path)
        for table in ("experience_records", "lifecycle_events", "experience_holdout_assignments"):
            for row in rows[table]:
                _insert_raw(conninfo, table, row)

    estimate = estimate_benefit(
        PostgresExperienceStore(conninfo), AuthorizationBounds.of(SCOPE), SCOPE, BenefitOptions(split_by_model=True)
    )
    assert estimate.outcome == "Found" and estimate.tracked
    assert estimate.overall.pending == 0 and estimate.overall.mixed_arm == 0
    actual = json.loads(json.dumps(estimate_json(estimate, with_store=True)))
    if os.environ.get(UPDATE_VARIABLE) == "1":
        FLEET_ESTIMATE_GOLDEN.write_text(json.dumps(actual, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    difference = json_difference(read_json(FLEET_ESTIMATE_GOLDEN), actual, tolerance=TOLERANCE)
    assert difference is None, (
        f"{difference} (set {UPDATE_VARIABLE}=1 to rewrite {FLEET_ESTIMATE_GOLDEN.name} on purpose)"
    )
