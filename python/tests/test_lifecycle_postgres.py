"""
Contract 1.16, the PostgreSQL leg: every ``lifecycle-sql`` fixture through this port's PostgreSQL store (the packaged
``sql/lifecycle.sql``), against a database the .NET migrations built, as the application role under row-level security;
then the three tables read back as the owner, every column in PostgreSQL's own text form. Also writers racing at one
revision on separate connections, a sealed record, and the service end to end over PostgreSQL.
"""

from __future__ import annotations

import re
import threading
import uuid
from dataclasses import replace
from typing import Any

import psycopg
import pytest
from _contract import FIXTURES, cases, json_difference, read_json, scope_of
from _lifecycle import (
    authorization_of,
    event_of,
    history_json,
    history_query_of,
    store_commit_json,
    supersession_json,
)
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database

from agentexperience import (
    ApplyConfidenceEvidenceRequest,
    AuthorizationBounds,
    AuthorizationContext,
    CommitLifecycleTransitionRequest,
    ExperienceLifecycleService,
    HistoryQuery,
    Instant,
    LifecycleEvent,
    from_stored,
    to_stored,
)
from agentexperience.postgres import LIFECYCLE_SQL, PostgresExperienceStore, contract_statements

pytestmark = pytest.mark.postgres

TOLERANCE = 1e-12
FAMILY = "lifecycle-sql"


def _columns(statement: str) -> list[str]:
    """The column list of one of ``sql/lifecycle.sql``'s INSERT statements."""
    match = re.search(r"INSERT INTO \S+ \(([^)]*)\)", contract_statements(LIFECYCLE_SQL)[statement])
    assert match is not None, statement
    return [name.strip() for name in match.group(1).split(",")]


RECORD_COLUMNS = (
    "experience_id",
    "status",
    "reuse_confidence",
    "supporting_validations",
    "contradictions",
    "revision",
    "updated_at",
    "deleted_at",
)


def _rows(owner: str, table: str, columns: list[str] | tuple[str, ...], order: str) -> list[dict[str, Any]]:
    with psycopg.connect(owner) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(
            f"SELECT {', '.join(f'{column}::text' for column in columns)} FROM agent_experience.{table} "
            f"ORDER BY {order}"
        ).fetchall()
    return [dict(zip(columns, row, strict=True)) for row in rows]


def _erase(owner: str, erase: dict[str, Any]) -> None:
    """An ``erase`` step: contract 1.4's ``purge_experience_record`` as the schema's owner."""
    with psycopg.connect(owner) as connection:
        purged = connection.execute(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(%s::uuid, %s, %s, %s, %s, %s, "
            "%s, NULL, %s::timestamptz)",
            (erase["experienceId"], *scope_of(erase["scope"]).fields(), erase["deletedAt"]),
        ).fetchone()
    assert purged == ("Deleted",)


class _Clock:
    def __init__(self) -> None:
        self.now = Instant(0)

    def __call__(self) -> Instant:
        return self.now


def _run(store: PostgresExperienceStore, owner: str, step: dict[str, Any]) -> Any:
    if "commit" in step:
        commit = step["commit"]
        return store_commit_json(
            store.commit_lifecycle_event(
                authorization_of(commit["authorization"]), scope_of(commit["scope"]), event_of(commit["event"])
            )
        )
    if "history" in step:
        history = step["history"]
        query = history_query_of(history["query"])
        return history_json(store.get_history(authorization_of(history["authorization"]), query), query.scope)
    if "checkSupersession" in step:
        check = step["checkSupersession"]
        return supersession_json(
            store.check_supersession(
                authorization_of(check["authorization"]),
                scope_of(check["scope"]),
                uuid.UUID(check["experienceId"]),
                uuid.UUID(check["replacementExperienceId"]),
            )
        )
    if "read" in step:
        read = step["read"]
        result = store.read_record(
            authorization_of(read["authorization"]), scope_of(read["scope"]), uuid.UUID(read["experienceId"])
        )
        return {
            "outcome": result.outcome,
            "record": None if result.record is None else to_stored(result.record),
            "errors": [{"path": error.path, "message": error.message} for error in result.errors],
        }
    if "erase" in step:
        _erase(owner, step["erase"])
        return None
    raise AssertionError(f"Unknown step {sorted(step)}.")


@pytest.mark.parametrize("name", cases(FAMILY))
def test_a_lifecycle_sql_fixture(pg_server: Server, name: str) -> None:
    directory = FIXTURES / FAMILY / name
    seed = read_json(directory / "seed.json")
    steps = read_json(directory / "steps.json")["steps"]
    expected = read_json(directory / "expected.json")

    database = create_database(pg_server, "lifecycle_" + name)
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    clock = _Clock()
    store = PostgresExperienceStore(application_conninfo(pg_server, database), clock=clock)
    for stored in seed["records"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    assert len(expected["results"]) == len(steps)
    for index, (step, outcome) in enumerate(zip(steps, expected["results"], strict=True)):
        if "clock" in step:
            clock.now = Instant.parse(step["clock"])
        actual = _run(store, owner, step)
        difference = json_difference(outcome, actual, f"$.results[{index}]", TOLERANCE)
        assert difference is None, difference

    tables = {
        "experience_records": _rows(owner, "experience_records", RECORD_COLUMNS, "experience_id"),
        "lifecycle_events": _rows(
            owner, "lifecycle_events", _columns("insert-event"), "experience_id, applied_revision"
        ),
        "confidence_evidence": _rows(
            owner,
            "confidence_evidence",
            [*_columns("insert-evidence"), "independence_key"],
            "experience_id, evidence_id",
        ),
    }
    for table, rows in tables.items():
        difference = json_difference(expected[table], rows, f"$.{table}")
        assert difference is None, difference


# ---- What the fixtures do not state --------------------------------------------------------------------------------


def _seeded(pg_server: Server, label: str) -> tuple[str, Any, AuthorizationContext, str]:
    stored = read_json(FIXTURES / FAMILY / "compare-and-set" / "seed.json")["records"][0]
    record = from_stored(stored)
    database = create_database(pg_server, label)
    conninfo = application_conninfo(pg_server, database)
    PostgresExperienceStore(conninfo).insert_record(AuthorizationBounds.of(record.scope), record)
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    return conninfo, record, AuthorizationContext("racer", AuthorizationBounds.of(record.scope)), owner


def test_writers_racing_at_one_revision_on_separate_connections_commit_exactly_once(pg_server: Server) -> None:
    conninfo, record, authorization, _ = _seeded(pg_server, "lifecycle_race")
    assert record.status in ("Validated", "Reinforced")
    count = 8
    barrier = threading.Barrier(count)
    results: list[Any] = [None] * count
    failures: list[BaseException] = []

    def run(index: int) -> None:
        try:
            service = ExperienceLifecycleService(PostgresExperienceStore(conninfo))
            request = CommitLifecycleTransitionRequest(
                event_id=uuid.UUID(int=0x7000 + index),
                experience_id=record.experience_id,
                scope=record.scope,
                prior_status=record.status,
                current_status="Revoked" if index % 2 else "Stale",
                reason=f"racer {index}",
                producer="race/1.0",
                occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
                expected_revision=record.revision,
            )
            barrier.wait()
            results[index] = service.commit(authorization, request)
        except BaseException as failure:  # pragma: no cover - reported below
            failures.append(failure)

    threads = [threading.Thread(target=run, args=(index,)) for index in range(count)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert not failures, failures
    winners = [index for index, result in enumerate(results) if result.outcome == "Committed"]
    assert len(winners) == 1, [result.outcome for result in results]
    for index, result in enumerate(results):
        if index != winners[0]:
            assert (result.outcome, result.revision) == ("StaleRevision", record.revision + 1), result

    history = PostgresExperienceStore(conninfo).get_history(
        authorization, HistoryQuery(record.scope, record.experience_id)
    )
    assert history.revision == record.revision + 1
    assert [(stored.event.event_id, stored.applied_revision) for stored in history.events] == [
        (uuid.UUID(int=0x7000 + winners[0]), record.revision + 1)
    ]


def test_a_sealed_record_is_not_found_for_commit_history_and_read(pg_server: Server) -> None:
    conninfo, record, authorization, owner = _seeded(pg_server, "lifecycle_sealed")
    # A sealed row as the .NET sealing writes one (payload_version 2, opaque payload), inserted as the owner.
    sealed_id = uuid.UUID("0000116f-0000-4000-8000-000000000001")
    with psycopg.connect(owner) as connection:
        connection.execute(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, "
            "project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, "
            "contradictions, revision, created_at, updated_at, payload_version, payload, search_vector_sealed) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, '(sealed)', 'Validated', 0.9, 1, 0, 1, now(), now(), 2, "
            "%s::jsonb, to_tsvector('english', 'sealed'))",
            (sealed_id, uuid.uuid4(), *record.scope.fields(), '{"sealed": "aexp-sealed:v1:AAAA"}'),
        )
    record = replace(record, experience_id=sealed_id, status="Validated", revision=1)
    store = PostgresExperienceStore(conninfo)
    event = LifecycleEvent(
        event_id=uuid.UUID(int=0x7100),
        experience_record_id=record.experience_id,
        prior_status=record.status,
        current_status="Revoked",
        reason="sealed",
        producer="sealed/1.0",
        occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
        expected_revision=record.revision,
    )
    assert store.commit_lifecycle_event(authorization, record.scope, event).outcome == "NotFound"
    assert store.read_record(authorization, record.scope, record.experience_id).outcome == "NotFound"
    history = store.get_history(authorization, HistoryQuery(record.scope, record.experience_id))
    assert (history.outcome, history.events) == ("NotFound", ())
    assert _rows(owner, "lifecycle_events", ["event_id"], "event_id") == []


def test_the_service_applies_evidence_and_reads_confidence_over_postgres(pg_server: Server) -> None:
    conninfo, record, authorization, _ = _seeded(pg_server, "lifecycle_service")
    store = PostgresExperienceStore(conninfo, clock=lambda: Instant.parse("2026-03-02T10:00:00.1234567+00:00"))
    service = ExperienceLifecycleService(store)
    request = ApplyConfidenceEvidenceRequest(
        event_id=uuid.UUID(int=0x7201),
        experience_id=record.experience_id,
        scope=record.scope,
        evidence_id=uuid.UUID(int=0x7202),
        kind="Contradicting",
        source="Machine",
        run_id=uuid.UUID(int=0x7203),
        verification_round_id=uuid.UUID(int=0x7204),
        reason="reused and failed",
        producer="service/1.0",
        occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
    )
    applied = service.apply_evidence(authorization, request)
    assert (applied.outcome, applied.counted, applied.status) == ("Applied", True, "Contested")
    assert applied.update is not None and applied.update.admission == "HostTrusted"
    replay = service.apply_evidence(authorization, request)
    assert (replay.outcome, replay.revision, replay.update) == ("Applied", applied.revision, applied.update)

    everything = service.read_confidence(authorization, record.scope, record.experience_id, "All")
    assert everything.report is not None
    assert everything.report.host_trusted.contradicting == 1
    verified = service.read_confidence(authorization, record.scope, record.experience_id, "ExcludeHostTrusted")
    assert verified.report is not None
    assert verified.report.contradictions == record.contradictions
    history = store.get_history(authorization, HistoryQuery(record.scope, record.experience_id))
    assert history.events[-1].recorded_at == Instant.parse("2026-03-02T10:00:00.123456+00:00")
