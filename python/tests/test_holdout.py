"""
Story 25.6: the injection holdout in Python (contract 1.8). The ``holdout-assign`` fixture (no database), the
``holdout-record`` fixtures through the in-memory store and, in the PostgreSQL leg, through the PostgreSQL store (the
packaged ``record-holdout-assignment.sql``), and the agent integration: a held-out run gets no block, no exposure and
no injection record; a treatment run is injected as before; both arms are recorded; rate 0 changes nothing.
"""

from __future__ import annotations

import datetime as dt
import functools
import math
import threading
import time
import uuid
from collections.abc import Callable
from typing import Any

import pytest
from _contract import FIXTURES, cases, json_difference, read_json, scope_of
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database

from agentexperience import (
    DEFAULT_HOLDOUT_SALT,
    AuthorizationBounds,
    HoldoutAssignment,
    HoldoutOptions,
    Instant,
    RunExposure,
    assign_holdout,
    from_stored,
    holdout_fraction,
)
from agentexperience.inmemory import HoldoutRecorder, HoldoutRow, InMemoryExperienceStore

DATABASE_CLOCK = "database-clock"
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


# ---- The assignment function -------------------------------------------------------------------------------------


ASSIGN = read_json(FIXTURES / "holdout-assign" / "cases.json")["cases"]


@pytest.mark.parametrize("index", range(len(ASSIGN)))
def test_holdout_assign(index: int) -> None:
    case = ASSIGN[index]
    run_id = uuid.UUID(case["runId"])
    fraction = holdout_fraction(run_id, case["salt"])
    assert f"{fraction:.15g}" == f"{case['fraction']:.15g}"
    assert fraction == case["fraction"]  # the same double .NET divides out, in fact
    for arm in case["arms"]:
        assert assign_holdout(run_id, case["salt"], arm["rate"]) == arm["arm"], arm


def test_the_six_runs_dotnet_pins_land_where_dotnet_puts_them() -> None:
    pinned = {
        "00000000-0000-0000-0000-000000000001": (0.3139116302163824, "Holdout"),
        "11111111-2222-3333-4444-555555555555": (0.24520626598382958, "Holdout"),
        "6f9619ff-8b86-d011-b42d-00c04fc964ff": (0.47486808769227573, "Holdout"),
        "f47ac10b-58cc-4372-a567-0e02b2c3d479": (0.25656011927796707, "Holdout"),
        "a3bb189e-8bf9-3888-9912-ace4e6543002": (0.8913670481238475, "Treatment"),
        "c9a646d3-9c61-4cb7-bfcd-ee2522c8f633": (0.8851032471291671, "Treatment"),
    }
    for text, (fraction, arm) in pinned.items():
        run_id = uuid.UUID(text)
        assert holdout_fraction(run_id, DEFAULT_HOLDOUT_SALT) == pytest.approx(fraction, abs=1e-15)
        assert assign_holdout(run_id, DEFAULT_HOLDOUT_SALT, 0.5) == arm


def test_rate_zero_never_holds_a_run_out_and_rate_one_always_does() -> None:
    for _ in range(500):
        run_id = uuid.uuid4()
        assert assign_holdout(run_id, DEFAULT_HOLDOUT_SALT, 0) == "Treatment"
        assert assign_holdout(run_id, DEFAULT_HOLDOUT_SALT, 1) == "Holdout"
        assert 0.0 <= holdout_fraction(run_id, DEFAULT_HOLDOUT_SALT) < 1.0


@pytest.mark.parametrize("rate", [math.nan, -0.01, 1.01, math.inf, -math.inf])
def test_a_rate_outside_zero_to_one_is_refused(rate: float) -> None:
    with pytest.raises(ValueError, match="rate"):
        HoldoutOptions(rate=rate)


def test_an_empty_salt_or_a_rate_that_is_not_a_number_is_refused() -> None:
    with pytest.raises(ValueError, match="salt"):
        HoldoutOptions(rate=0.1, salt="")
    with pytest.raises(TypeError):
        HoldoutOptions(rate="0.1")  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        HoldoutOptions(rate=True)
    assert HoldoutOptions() == HoldoutOptions(0.0, DEFAULT_HOLDOUT_SALT)
    assert not HoldoutOptions().active and HoldoutOptions(rate=1).active


# ---- The holdout-record fixtures ---------------------------------------------------------------------------------


def _assignment(record: dict[str, Any]) -> HoldoutAssignment:
    return HoldoutAssignment(
        uuid.UUID(record["runId"]),
        record["arm"],
        tuple(RunExposure(uuid.UUID(item["experienceId"]), item["revision"]) for item in record["records"]),
        Instant.parse(record["assignedAt"]),
    )


def _record_in(store: HoldoutRecorder, record: dict[str, Any]) -> None:
    scope = scope_of(record["scope"])
    store.record_holdout_assignment(AuthorizationBounds.of(scope), scope, _assignment(record))


def postgres_text(instant: Instant) -> str:
    """An instant as PostgreSQL writes a ``timestamptz`` in the time zone UTC: ``2026-03-02 10:00:00.123456+00``."""
    value = instant.to_datetime()
    text = value.strftime("%Y-%m-%d %H:%M:%S")
    if value.microsecond:
        text += f".{value.microsecond:06d}".rstrip("0")
    return text + "+00"


def _memory_rows(rows: list[HoldoutRow], clock: Instant) -> list[dict[str, Any]]:
    return [
        {
            "run_id": str(row.run_id),
            "experience_id": str(row.experience_id),
            "revision": str(row.revision),
            "arm": row.arm,
            "tenant_id": row.scope.tenant_id,
            "application_id": row.scope.application_id,
            "project_id": row.scope.project_id,
            "team_id": row.scope.team_id,
            "agent_id": row.scope.agent_id,
            "user_id": row.scope.user_id,
            "assigned_at": DATABASE_CLOCK if row.assigned_at == clock else postgres_text(row.assigned_at),
        }
        for row in rows
    ]


@pytest.mark.parametrize("name", cases("holdout-record"))
def test_holdout_record_in_memory(name: str) -> None:
    """A holdout-record fixture through the in-memory store: an ``eraseDuringRecord`` step is its erasure, then its
    record call, which the guard then refuses."""
    directory = FIXTURES / "holdout-record" / name
    clock = Instant.parse("2027-01-01T00:00:00+00:00")
    store = InMemoryExperienceStore(
        (from_stored(stored) for stored in read_json(directory / "seed.json")["records"]), clock=lambda: clock
    )
    for step in read_json(directory / "steps.json")["steps"]:
        if "record" in step:
            _record_in(store, step["record"])
        elif "erase" in step:
            assert store.erase(uuid.UUID(step["erase"]["experienceId"]))
        else:
            race = step["eraseDuringRecord"]
            assert store.erase(uuid.UUID(race["erase"]["experienceId"]))
            _record_in(store, race["record"])
    actual = _memory_rows(store.holdout_assignments(), clock)
    expected = read_json(directory / "expected.json")["experience_holdout_assignments"]
    difference = json_difference(expected, actual, "$.experience_holdout_assignments")
    assert difference is None, difference


def test_an_assignment_is_refused_outside_the_bounds_or_when_malformed() -> None:
    store = InMemoryExperienceStore()
    from agentexperience import Scope
    from agentexperience.inmemory import AuthorizationDenied

    scope = Scope("t", "a", "p", agent_id="x")
    good = HoldoutAssignment(uuid.uuid4(), "Holdout", (RunExposure(uuid.uuid4(), 1),), Instant.now())
    with pytest.raises(AuthorizationDenied):
        store.record_holdout_assignment(AuthorizationBounds.of(Scope("t", "a", "p", agent_id="y")), scope, good)
    bounds = AuthorizationBounds.of(scope)
    for bad in (
        HoldoutAssignment(uuid.UUID(int=0), "Holdout", (), Instant.now()),
        HoldoutAssignment(uuid.uuid4(), "Control", (), Instant.now()),  # type: ignore[arg-type]
        HoldoutAssignment(uuid.uuid4(), "Holdout", (RunExposure(uuid.UUID(int=0), 1),), Instant.now()),
        HoldoutAssignment(uuid.uuid4(), "Holdout", (RunExposure(uuid.uuid4(), -1),), Instant.now()),
        HoldoutAssignment(
            uuid.uuid4(), "Holdout", tuple(RunExposure(uuid.uuid4(), 1) for _ in range(201)), Instant.now()
        ),
    ):
        with pytest.raises(ValueError):
            store.record_holdout_assignment(bounds, scope, bad)
    assert store.holdout_assignments() == []


# ---- PostgreSQL ----------------------------------------------------------------------------------------------------


def _clock(conninfo: str) -> dt.datetime:
    import psycopg

    with psycopg.connect(conninfo) as connection:
        row = connection.execute("SELECT pg_catalog.clock_timestamp()").fetchone()
    assert row is not None and isinstance(row[0], dt.datetime)
    return row[0]


_PURGE = (
    "SELECT purge_outcome FROM agent_experience.purge_experience_record(%s::uuid, %s, %s, %s, %s, %s, %s, NULL, "
    "%s::timestamptz)"
)


def _purge_parameters(erase: dict[str, Any]) -> tuple[Any, ...]:
    return (erase["experienceId"], *scope_of(erase["scope"]).fields(), erase["deletedAt"])


def _erase(owner: str, erase: dict[str, Any]) -> None:
    import psycopg

    with psycopg.connect(owner) as connection:
        assert connection.execute(_PURGE, _purge_parameters(erase)).fetchone() == ("Deleted",)


def _erase_during(owner: str, erase: dict[str, Any], record: Callable[[], None]) -> None:
    """
    The late-write guard's race: the erasure runs as the owner in a transaction not yet committed; the record call
    starts on another thread and, once it is blocked by the erasing backend (``pg_blocking_pids``), the erasure
    commits and the record call is awaited. It must not complete before the commit.
    """
    import psycopg

    with psycopg.connect(owner) as purging:
        assert purging.execute(_PURGE, _purge_parameters(erase)).fetchone() == ("Deleted",)
        pid_row = purging.execute("SELECT pg_catalog.pg_backend_pid()").fetchone()
        assert pid_row is not None
        failures: list[BaseException] = []

        def call() -> None:
            try:
                record()
            except BaseException as ex:
                failures.append(ex)

        thread = threading.Thread(target=call, daemon=True)
        thread.start()
        deadline = time.monotonic() + 30
        with psycopg.connect(owner, autocommit=True) as watcher:
            while True:
                assert thread.is_alive(), "The record call completed while the erasure held the run's record."
                blocked = watcher.execute(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE a.datname = "
                    "pg_catalog.current_database() AND %s = ANY (pg_catalog.pg_blocking_pids(a.pid)))",
                    (pid_row[0],),
                ).fetchone()
                if blocked == (True,):
                    break
                assert time.monotonic() < deadline, "The record call never blocked on the erasure."
                time.sleep(0.02)
        purging.commit()
        thread.join(30)
        assert not thread.is_alive()
        assert failures == []


def _postgres_rows(owner: str, expected: Any, before: dt.datetime, after: dt.datetime) -> list[dict[str, Any]]:
    import psycopg

    with psycopg.connect(owner) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(
            "SELECT "
            + ", ".join(f"{column}::text" for column in HOLDOUT_COLUMNS)
            + ", assigned_at FROM agent_experience.experience_holdout_assignments ORDER BY run_id, experience_id"
        ).fetchall()
    clamped = {(item["run_id"], item["experience_id"]) for item in expected if item["assigned_at"] == DATABASE_CLOCK}
    actual: list[dict[str, Any]] = []
    for row in rows:
        values = dict(zip(HOLDOUT_COLUMNS, row[:-1], strict=True))
        if (values["run_id"], values["experience_id"]) in clamped:
            assert before <= row[-1] <= after, (values, before, after)
            values["assigned_at"] = DATABASE_CLOCK
        actual.append(values)
    return actual


@pytest.mark.postgres
@pytest.mark.parametrize("name", cases("holdout-record"))
def test_holdout_record_in_postgresql(pg_server: Server, name: str) -> None:
    """A holdout-record fixture, each record step through the store's record call (the packaged statement)."""
    from agentexperience.postgres import PostgresExperienceStore

    directory = FIXTURES / "holdout-record" / name
    database = create_database(pg_server, "holdout_" + name)
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for stored in read_json(directory / "seed.json")["records"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    before = _clock(owner)
    for step in read_json(directory / "steps.json")["steps"]:
        if "record" in step:
            _record_in(store, step["record"])
        elif "erase" in step:
            _erase(owner, step["erase"])
        else:
            race = step["eraseDuringRecord"]
            _erase_during(owner, race["erase"], functools.partial(_record_in, store, race["record"]))
    after = _clock(owner)
    expected = read_json(directory / "expected.json")["experience_holdout_assignments"]
    difference = json_difference(
        expected, _postgres_rows(owner, expected, before, after), "$.experience_holdout_assignments"
    )
    assert difference is None, difference


@pytest.mark.postgres
def test_without_the_1_8_grants_recording_an_assignment_fails_and_writes_nothing(pg_server: Server) -> None:
    import psycopg
    from _postgres import APPLICATION_ROLE_PRIVILEGES_1_6

    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "holdout_no_grant", privileges=APPLICATION_ROLE_PRIVILEGES_1_6)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    stored = read_json(FIXTURES / "holdout-record" / "own-scope-only" / "seed.json")["records"][0]
    record = from_stored(stored)
    bounds = AuthorizationBounds.of(record.scope)
    store.insert_record(bounds, record)
    assignment = HoldoutAssignment(uuid.uuid4(), "Holdout", (RunExposure(record.experience_id, 1),), Instant.now())
    with pytest.raises(psycopg.errors.InsufficientPrivilege):
        store.record_holdout_assignment(bounds, record.scope, assignment)
    with psycopg.connect(pg_server.conninfo(database)) as connection:
        count = connection.execute("SELECT count(*) FROM agent_experience.experience_holdout_assignments").fetchone()
    assert count == (0,)
