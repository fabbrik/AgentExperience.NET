"""
Contract 1.16, the PostgreSQL leg: every ``lifecycle-sql`` fixture through this port's PostgreSQL store (the packaged
``sql/lifecycle.sql``), against a database the .NET migrations built, as the application role under row-level security;
then the three tables read back as the owner, every column in PostgreSQL's own text form. Also writers racing at one
revision on separate connections, a sealed record, and the service end to end over PostgreSQL.
"""

from __future__ import annotations

import asyncio
import json
import re
import subprocess
import sys
import threading
import time
import uuid
from dataclasses import replace
from pathlib import Path
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
    ConfidenceUpdate,
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
WRITER = Path(__file__).resolve().parent / "cross-language" / "lifecycle_race.py"


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
    service = ExperienceLifecycleService(store, host_trusted_evidence="Counted")
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


def test_the_cross_language_race_writer_speaks_its_protocol(pg_server: Server, tmp_path: Path) -> None:
    """The writer the .NET suite spawns: two of them race one round behind an advisory lock, then read the history."""
    conninfo, record, _, owner = _seeded(pg_server, "lifecycle_writer")
    dsn = conninfo
    bounds = {
        "principalId": "python-writer",
        "tenantId": record.scope.tenant_id,
        "applicationId": record.scope.application_id,
        "projectId": record.scope.project_id,
        "teamId": record.scope.team_id,
        "agentId": record.scope.agent_id,
        "userId": record.scope.user_id,
    }
    scope = {key: value for key, value in bounds.items() if key != "principalId"}
    lock_key = 1160
    inputs = []
    for writer, status in enumerate(("Stale", "Revoked")):
        request = {
            "eventId": str(uuid.UUID(int=0x7300 + writer)),
            "experienceId": str(record.experience_id),
            "scope": scope,
            "priorStatus": record.status,
            "currentStatus": status,
            "reason": f"writer {writer}",
            "producer": "python-writer/1.0",
            "occurredAt": "2026-03-02T09:00:00+00:00",
            "expectedRevision": record.revision,
            "replacementExperienceId": None,
        }
        path = tmp_path / f"writer-{writer}.json"
        path.write_text(
            json.dumps(
                {
                    "dsn": dsn,
                    "mode": "commit",
                    "rounds": [{"lockKey": lock_key, "authorization": bounds, "request": request}],
                }
            ),
            encoding="utf-8",
        )
        inputs.append(path)

    with psycopg.connect(owner, autocommit=True) as gate:
        gate.execute("SELECT pg_advisory_lock(%s::bigint)", (lock_key,))
        writers = [
            subprocess.Popen(
                [sys.executable, str(WRITER), str(path)],
                cwd=WRITER.parents[2],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
            for path in inputs
        ]
        gate.execute("SELECT pg_advisory_unlock(%s::bigint)", (lock_key,))
    outputs = [writer.communicate(timeout=120) for writer in writers]
    assert all(writer.returncode == 0 for writer in writers), outputs
    results = [json.loads(stdout)["results"][0] for stdout, _ in outputs]
    assert sorted(result["outcome"] for result in results) == ["Committed", "StaleRevision"], results
    assert all(result["revision"] == record.revision + 1 for result in results), results

    history_input = tmp_path / "history.json"
    query = {"scope": scope, "experienceId": str(record.experience_id), "limit": 10, "startAfterRevision": None}
    history_input.write_text(
        json.dumps({"dsn": dsn, "mode": "history", "histories": [{"authorization": bounds, "query": query}]}),
        encoding="utf-8",
    )
    completed = subprocess.run(
        [sys.executable, str(WRITER), str(history_input)],
        cwd=WRITER.parents[2],
        capture_output=True,
        text=True,
        timeout=120,
        check=True,
    )
    (history,) = json.loads(completed.stdout)["histories"]
    assert history["outcome"] == "Found" and history["revision"] == record.revision + 1
    assert [event["applied_revision"] for event in history["events"]] == [record.revision + 1]
    assert history["events"][0]["actor"] == "python-writer"


# ---- Sealing races a commit, and a failure mid-commit ---------------------------------------------------------------


def _evidence_event(record: Any, base: int, revision: int, supporting: int) -> LifecycleEvent:
    """A counted supporting update, as the service would compute it against ``revision`` and ``supporting``."""
    from agentexperience.confidence import score

    prior = score(supporting, record.contradictions) if supporting != record.supporting_validations else None
    update = ConfidenceUpdate(
        evidence_id=uuid.UUID(int=base + 1),
        kind="Supporting",
        source="Machine",
        run_id=uuid.UUID(int=0x9900),
        verification_round_id=uuid.UUID(int=0x9901),
        reviewer_identity=None,
        rule_version="1.0.0",
        prior_reuse_confidence=record.reuse_confidence if prior is None else prior,
        new_reuse_confidence=score(supporting + 1, record.contradictions),
        prior_supporting_validations=supporting,
        new_supporting_validations=supporting + 1,
        prior_contradictions=record.contradictions,
        new_contradictions=record.contradictions,
        admission="HostTrusted",
    )
    return LifecycleEvent(
        event_id=uuid.UUID(int=base + 2),
        experience_record_id=record.experience_id,
        prior_status=record.status,
        current_status=record.status,
        reason="reused",
        producer="seal-race/1.0",
        occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
        expected_revision=revision,
        confidence=update,
    )


def _state(owner: str, experience_id: uuid.UUID) -> tuple[Any, ...]:
    with psycopg.connect(owner) as connection:
        record = connection.execute(
            "SELECT revision, status, payload_version, supporting_validations, updated_at "
            "FROM agent_experience.experience_records WHERE experience_id = %s",
            (experience_id,),
        ).fetchone()
        events = connection.execute(
            "SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = %s", (experience_id,)
        ).fetchone()
        evidence = connection.execute(
            "SELECT count(*) FROM agent_experience.confidence_evidence WHERE experience_id = %s", (experience_id,)
        ).fetchone()
    return record, events, evidence


@pytest.mark.parametrize("path", ["commit", "duplicate"])
def test_a_seal_that_lands_while_a_commit_waits_makes_the_commit_not_found(pg_server: Server, path: str) -> None:
    conninfo, record, authorization, owner = _seeded(pg_server, "lifecycle_seal_" + path)
    store = PostgresExperienceStore(conninfo, clock=lambda: Instant.parse("2026-03-02T10:00:00+00:00"))
    revision = record.revision
    if path == "duplicate":
        # The key is counted first, so the racing submission takes the DUPLICATE path.
        first = _evidence_event(record, 0x9A00, revision, record.supporting_validations)
        assert store.commit_lifecycle_event(authorization, record.scope, first).outcome == "Committed"
        revision += 1
        racing = _evidence_event(record, 0x9B00, revision, record.supporting_validations + 1)
    else:
        racing = _evidence_event(record, 0x9B00, revision, record.supporting_validations)
    before = _state(owner, record.experience_id)

    results: list[Any] = []
    with psycopg.connect(owner) as seal:
        sealed = seal.execute(
            "SELECT agent_experience.seal_experience_record(%s, %s, %s, %s, %s, %s, %s, %s::bigint, %s::jsonb)",
            (record.experience_id, *record.scope.fields(), revision, '{"sealed": "aexp-sealed:v1:AAAA"}'),
        ).fetchone()
        assert sealed == ("Sealed",)
        writer = threading.Thread(
            target=lambda: results.append(store.commit_lifecycle_event(authorization, record.scope, racing))
        )
        writer.start()
        # Wait (bounded) until the commit is blocked on the seal's row lock, then let the seal land.
        with psycopg.connect(pg_server.conninfo(), autocommit=True) as monitor:
            for _ in range(600):
                waiting = monitor.execute("SELECT count(*) FROM pg_catalog.pg_locks WHERE NOT granted").fetchone()
                if waiting is not None and waiting[0] > 0:
                    break
                time.sleep(0.05)
            else:
                pytest.fail("the commit never waited on the seal's row lock")
        seal.commit()
    writer.join(timeout=120)
    assert not writer.is_alive()

    (result,) = results
    assert (result.outcome, result.revision, result.applied_confidence) == ("NotFound", 0, None)
    after_record, after_events, after_evidence = _state(owner, record.experience_id)
    before_record, before_events, before_evidence = before
    assert after_record[2] == 2  # the seal landed
    assert after_record[:2] + after_record[3:] == before_record[:2] + before_record[3:]  # nothing else changed
    assert (after_events, after_evidence) == (before_events, before_evidence)


@pytest.mark.parametrize("failure", [RuntimeError("injected"), asyncio.CancelledError()], ids=["error", "cancelled"])
def test_a_failure_mid_commit_rolls_everything_back(
    pg_server: Server, monkeypatch: pytest.MonkeyPatch, failure: BaseException
) -> None:
    from agentexperience import postgres

    conninfo, record, authorization, owner = _seeded(pg_server, "lifecycle_fault_" + type(failure).__name__)
    store = PostgresExperienceStore(conninfo, clock=lambda: Instant.parse("2026-03-02T10:00:00+00:00"))
    before = _state(owner, record.experience_id)
    execute = postgres._Commit._execute

    def failing(self: Any, name: str, parameters: dict[str, Any] | None = None) -> None:
        execute(self, name, parameters)
        if name == "insert-event":
            raise failure

    monkeypatch.setattr(postgres._Commit, "_execute", failing)
    event = _evidence_event(record, 0x9C00, record.revision, record.supporting_validations)
    with pytest.raises(type(failure)):
        store.commit_lifecycle_event(authorization, record.scope, event)
    monkeypatch.undo()
    assert _state(owner, record.experience_id) == before
    # The store is still usable, and the same commit then lands.
    assert store.commit_lifecycle_event(authorization, record.scope, event).outcome == "Committed"
