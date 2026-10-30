"""
Contract 1.17, the PostgreSQL leg: every ``reuse-feedback-sql`` fixture through this port's PostgreSQL ledger (the
packaged ``sql/reuse-feedback.sql``), against a database the .NET migrations built, as the application role under
row-level security; then both ledger tables read back as the owner, every column in PostgreSQL's own text form. Also
the service end to end over PostgreSQL, through the cross-language writer's protocol.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

import pytest
from _contract import FIXTURES, cases, json_difference, read_json
from _lifecycle import authorization_of
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database
from _reuse_feedback import recorded_json, recorded_of
from test_lifecycle_postgres import _erase, _rows

from agentexperience import AuthorizationBounds, Instant, from_stored
from agentexperience.postgres import (
    REUSE_FEEDBACK_SQL,
    PostgresExperienceReuseFeedbackStore,
    PostgresExperienceStore,
    contract_statements,
)

pytestmark = pytest.mark.postgres

FAMILY = "reuse-feedback-sql"
WRITER = Path(__file__).resolve().parent / "cross-language" / "reuse_feedback.py"
EXPOSURE_COLUMNS = ("feedback_id", "experience_id", "ordinal", "attributed", "evidence_id", "rationale_sealed")


def _submission_columns() -> list[str]:
    match = re.search(r"INSERT INTO \S+ \(([^)]*)\)", contract_statements(REUSE_FEEDBACK_SQL)["insert-submission"])
    assert match is not None
    return [name.strip() for name in match.group(1).split(",")]


class _Clock:
    def __init__(self) -> None:
        self.now = Instant(0)

    def __call__(self) -> Instant:
        return self.now


@pytest.mark.parametrize("name", cases(FAMILY))
def test_a_reuse_feedback_sql_fixture(pg_server: Server, name: str) -> None:
    directory = FIXTURES / FAMILY / name
    seed = read_json(directory / "seed.json")
    steps = read_json(directory / "steps.json")["steps"]
    expected = read_json(directory / "expected.json")

    database = create_database(pg_server, "reuse_" + name)
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    conninfo = application_conninfo(pg_server, database)
    for stored in seed["records"]:
        record = from_stored(stored)
        PostgresExperienceStore(conninfo).insert_record(AuthorizationBounds.of(record.scope), record)
    clock = _Clock()
    ledger = PostgresExperienceReuseFeedbackStore(conninfo, clock=clock)

    assert len(expected["results"]) == len(steps)
    for index, (step, outcome) in enumerate(zip(steps, expected["results"], strict=True)):
        clock.now = Instant.parse(step["clock"])
        actual: Any
        if "record" in step:
            record_step = step["record"]
            result = ledger.record(authorization_of(record_step["authorization"]), recorded_of(record_step["feedback"]))
            actual = {
                "outcome": result.outcome,
                "feedback": recorded_json(result.feedback),
                "errors": [{"path": error.path, "message": error.message} for error in result.errors],
            }
        elif "erase" in step:
            _erase(owner, step["erase"])
            actual = None
        else:
            raise AssertionError(f"Unknown step {sorted(step)}.")
        difference = json_difference(outcome, actual, f"$.results[{index}]", 1e-12)
        assert difference is None, difference

    tables = {
        "reuse_feedback": _rows(owner, "reuse_feedback", _submission_columns(), "feedback_id"),
        "reuse_feedback_exposures": _rows(owner, "reuse_feedback_exposures", EXPOSURE_COLUMNS, "feedback_id, ordinal"),
    }
    for table, rows in tables.items():
        difference = json_difference(expected[table], rows, f"$.{table}")
        assert difference is None, difference


def test_the_cross_language_writer_records_and_reads_over_postgres(pg_server: Server, tmp_path: Path) -> None:
    case = read_json(FIXTURES / "reuse-feedback" / "human-improved" / "input.json")
    expected = read_json(FIXTURES / "reuse-feedback" / "human-improved" / "expected.json")
    database = create_database(pg_server, "reuse_writer")
    conninfo = application_conninfo(pg_server, database)
    for stored in case["records"]:
        record = from_stored(stored)
        PostgresExperienceStore(conninfo).insert_record(AuthorizationBounds.of(record.scope), record)
    first = case["steps"][0]["submit"]
    target = from_stored(case["records"][0])
    authorization = first["authorization"]
    scope = {
        "tenantId": target.scope.tenant_id,
        "applicationId": target.scope.application_id,
        "projectId": target.scope.project_id,
        "teamId": target.scope.team_id,
        "agentId": target.scope.agent_id,
        "userId": target.scope.user_id,
    }
    steps = [
        {"submit": {"clock": case["steps"][0]["clock"], "authorization": authorization, "feedback": first["feedback"]}},
        {"submit": {"clock": case["steps"][0]["clock"], "authorization": authorization, "feedback": first["feedback"]}},
        {"read": {"authorization": authorization, "scope": scope, "experienceId": str(target.experience_id)}},
    ]
    path = tmp_path / "input.json"
    path.write_text(
        json.dumps({"dsn": conninfo, "hostTrustedEvidence": case["service"]["hostTrustedEvidence"], "steps": steps}),
        encoding="utf-8",
    )
    done = subprocess.run([sys.executable, str(WRITER), str(path)], capture_output=True, text=True, check=False)
    assert done.returncode == 0, done.stderr
    results = json.loads(done.stdout)["results"]
    assert len(results) == 3
    difference = json_difference(expected["results"][0], results[0], "$.results[0]", 1e-12)
    assert difference is None, difference
    assert results[1]["outcome"] == "AlreadyRecorded"
    assert results[1]["exposures"] == results[0]["exposures"]
    read = results[2]
    assert read["history"]["outcome"] == "Found"
    # Only the first fixture step ran: its one event, and the record at the revision it produced.
    assert [event["event_id"] for event in read["history"]["events"]] == [
        expected["histories"][0]["events"][0]["event_id"]
    ]
    assert read["confidence"]["outcome"] == "Found"
    assert read["confidence"]["report"]["revision"] == read["history"]["revision"] == 2


def test_writers_racing_on_one_feedback_id_record_it_exactly_once(pg_server: Server) -> None:
    database = create_database(pg_server, "reuse_feedback_race")
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    conninfo = application_conninfo(pg_server, database)
    step = read_json(FIXTURES / FAMILY / "record-replay-and-conflict" / "steps.json")["steps"][0]["record"]
    authorization, submission = authorization_of(step["authorization"]), recorded_of(step["feedback"])
    count = 8
    barrier = threading.Barrier(count)
    outcomes: list[str | None] = [None] * count
    failures: list[BaseException] = []

    def run(index: int) -> None:
        try:
            ledger = PostgresExperienceReuseFeedbackStore(conninfo)
            barrier.wait()
            outcomes[index] = ledger.record(authorization, submission).outcome
        except BaseException as failure:  # pragma: no cover - reported below
            failures.append(failure)

    threads = [threading.Thread(target=run, args=(index,)) for index in range(count)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert not failures, failures
    # The primary key is the arbiter: one writer records, every other one finds its own submission stored.
    assert sorted(outcome or "" for outcome in outcomes) == ["AlreadyRecorded"] * (count - 1) + ["Recorded"]
    assert len(_rows(owner, "reuse_feedback", ["feedback_id"], "feedback_id")) == 1
    exposures = _rows(owner, "reuse_feedback_exposures", ["experience_id", "ordinal"], "ordinal")
    assert len(exposures) == len(submission.exposures or ())
