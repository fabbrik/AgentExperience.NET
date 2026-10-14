"""
The PostgreSQL leg of the conformance runner: finalize fixtures written through the Python store and read back row
for row, and every search fixture run through the store's text search, then ranked.
"""

from __future__ import annotations

import datetime as dt
import re
import uuid
from typing import Any

import psycopg
import pytest
from _contract import FIXTURES, cases, json_difference, read_json, run_finalize_case, scope_of
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database

from agentexperience import Instant, SearchParameters, from_stored, rank, to_stored
from agentexperience.postgres import DECLARE_BOUNDS_SQL, PostgresExperienceStore, contract_sql, to_driver_placeholders
from agentexperience.search import AuthorizationBounds

pytestmark = pytest.mark.postgres

SCORE_TOLERANCE = 1e-9


def _events(conninfo: str, bounds: AuthorizationBounds, experience_id: uuid.UUID) -> list[tuple[Any, ...]]:
    """A record's lifecycle events, read as the application role under the packaged declare-bounds.sql."""
    with psycopg.connect(conninfo) as connection, connection.transaction():
        connection.execute(to_driver_placeholders(contract_sql(DECLARE_BOUNDS_SQL)), bounds.parameters())
        return connection.execute(
            "SELECT event_id, experience_id, prior_status, current_status, reason, producer, occurred_at, "
            "expected_revision, applied_revision, actor FROM agent_experience.lifecycle_events "
            "WHERE experience_id = %s",
            (experience_id,),
        ).fetchall()


@pytest.mark.parametrize("name", cases("finalize"))
def test_a_finalize_fixture_writes_and_reads_back_row_for_row(pg_server: Server, name: str) -> None:
    conninfo = application_conninfo(pg_server, create_database(pg_server, "finalize_" + name))
    store = PostgresExperienceStore(conninfo)
    directory = FIXTURES / "finalize" / name
    finalized, _ = run_finalize_case(read_json(directory / "input.json"))
    record = finalized.record
    bounds = AuthorizationBounds.of(record.scope)
    store.insert_finalized(bounds, finalized, actor="contract-principal")

    read = store.get(bounds, record.scope, record.experience_id)
    assert read is not None
    difference = json_difference(read_json(directory / "expected-record.json"), to_stored(read))
    assert difference is None, difference

    events = _events(conninfo, bounds, record.experience_id)
    assert len(events) == 1, f"the record has {len(events)} lifecycle events"
    event = events[0]
    actual_event = {
        "event_id": str(event[0]),
        "experience_id": str(event[1]),
        "prior_status": event[2],
        "current_status": event[3],
        "reason": event[4],
        "producer": event[5],
        "occurred_at": Instant.from_datetime(event[6]).format(),
        "expected_revision": event[7],
        "applied_revision": event[8],
    }
    assert json_difference(read_json(directory / "expected-event.json"), actual_event) is None, actual_event
    assert event[9] == "contract-principal"


def _parameters(query: dict[str, Any]) -> SearchParameters:
    return SearchParameters(
        scope=scope_of(query["scope"]),
        task_text=query["taskText"],
        statuses=tuple(query["statuses"]),
        min_confidence=query["minConfidence"],
        limit=query["limit"],
        exclude_model_authored=query["excludeModelAuthored"],
        with_grants=query["withGrants"],
        minimum_matched_terms=query["minimumMatchedTerms"],
    )


@pytest.mark.parametrize("name", cases("search"))
def test_search(pg_server: Server, name: str) -> None:
    directory = FIXTURES / "search" / name
    seed = read_json(directory / "seed.json")
    query = read_json(directory / "query.json")
    expected = read_json(directory / "expected.json")

    database = create_database(pg_server, "search_" + name)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for stored in seed:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    parameters = _parameters(query)
    rows = store.search(AuthorizationBounds.of(parameters.scope), parameters)

    actual_rows = [{"experienceId": str(row.record.experience_id), "relevance": row.relevance} for row in rows]
    difference = json_difference(expected["rows"], actual_rows, "$.rows", SCORE_TOLERANCE)
    assert difference is None, difference
    # The relevance is the widened float4, exactly.
    assert [row["relevance"] for row in expected["rows"]] == [row.relevance for row in rows]
    for row, stored in zip(rows, expected["rows"], strict=True):
        original = next(item for item in seed if item["columns"]["experience_id"] == stored["experienceId"])
        assert json_difference(original, to_stored(row.record)) is None

    result = rank(
        rows,
        parameters.scope,
        Instant.parse(query["now"]),
        query["preferredEnvironment"],
        exclude_model_authored=parameters.exclude_model_authored,
    )
    assert result.truncated == expected["truncated"]
    actual_ranked = [
        {
            "experienceId": str(entry.record.experience_id),
            "score": entry.score,
            "components": [
                {"kind": component.kind, "value": component.value, "weight": component.weight}
                for component in entry.components
            ],
        }
        for entry in result.ranked
    ]
    difference = json_difference(expected["ranked"], actual_ranked, "$.ranked", SCORE_TOLERANCE)
    assert difference is None, difference


def test_a_record_read_through_another_scope_is_not_found(pg_server: Server) -> None:
    database = create_database(pg_server, "scope")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    record = finalized.record
    store.insert_finalized(AuthorizationBounds.of(record.scope), finalized, actor="contract-principal")

    other = AuthorizationBounds(
        record.scope.tenant_id, record.scope.application_id, record.scope.project_id, agent_id="x"
    )
    assert store.get(other, record.scope, record.experience_id) is None
    assert store.get(AuthorizationBounds.of(record.scope), record.scope, uuid.uuid4()) is None


def test_row_level_security_refuses_a_write_outside_the_bounds(pg_server: Server) -> None:
    database = create_database(pg_server, "bounds")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    with pytest.raises(psycopg.errors.InsufficientPrivilege):
        store.insert_finalized(AuthorizationBounds("another-tenant"), finalized, actor="contract-principal")


def test_insert_finalized_records_the_event_now_in_utc_when_no_recorded_at_is_given(pg_server: Server) -> None:
    # The cross-language proof pins recorded_at to the event's own instant so its rows are deterministic; this is the
    # store's default path, which the Python agent integration takes.
    conninfo = application_conninfo(pg_server, create_database(pg_server, "recorded_at_default"))
    store = PostgresExperienceStore(conninfo)
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    record = finalized.record
    bounds = AuthorizationBounds.of(record.scope)

    before = dt.datetime.now(dt.timezone.utc)
    store.insert_finalized(bounds, finalized, actor="contract-principal")
    after = dt.datetime.now(dt.timezone.utc)

    with psycopg.connect(conninfo) as connection, connection.transaction():
        connection.execute(to_driver_placeholders(contract_sql(DECLARE_BOUNDS_SQL)), bounds.parameters())
        connection.execute("SET LOCAL TIME ZONE 'UTC'")
        row = connection.execute(
            "SELECT recorded_at, recorded_at::text FROM agent_experience.lifecycle_events WHERE experience_id = %s",
            (record.experience_id,),
        ).fetchone()
    assert row is not None
    recorded, text = row
    assert isinstance(recorded, dt.datetime) and recorded.utcoffset() == dt.timedelta(0)
    assert re.fullmatch(r"\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(\.\d{1,6})?\+00", text), text
    assert before <= recorded <= after


def test_a_sealed_row_is_skipped_by_get_and_search(pg_server: Server) -> None:
    database = create_database(pg_server, "sealed")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    record = finalized.record
    bounds = AuthorizationBounds.of(record.scope)
    store.insert_finalized(bounds, finalized, actor="contract-principal")

    # A sealed row as the .NET sealing writes one (payload_version 2, opaque payload), inserted as the owner.
    sealed_id = uuid.UUID("0000005f-0000-4000-8000-000000000001")
    with psycopg.connect(pg_server.conninfo(database, OWNER, PASSWORD)) as owner, owner.transaction():
        owner.execute(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, "
            "project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, "
            "contradictions, revision, created_at, updated_at, payload_version, payload, search_vector_sealed) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, '(sealed)', 'Validated', 0.9, 1, 0, 1, now(), now(), 2, "
            "%s::jsonb, to_tsvector('english', 'refund stuck pending ticket lock'))",
            (
                sealed_id,
                uuid.uuid4(),
                *record.scope.fields(),
                '{"sealed": "aexp-sealed:v1:AAAA"}',
            ),
        )

    assert store.get(bounds, record.scope, sealed_id) is None
    rows = store.search(bounds, SearchParameters(record.scope, "refund stuck in pending, ticket lock"))
    assert [row.record.experience_id for row in rows] == [record.experience_id]


# ---- Contract 1.4: recording injections ---------------------------------------------------------------------------

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
DATABASE_CLOCK = "database-clock"


def _clock(conninfo: str) -> dt.datetime:
    with psycopg.connect(conninfo) as connection:
        row = connection.execute("SELECT pg_catalog.clock_timestamp()").fetchone()
    assert row is not None and isinstance(row[0], dt.datetime)
    return row[0]


@pytest.mark.parametrize("name", cases("record-injection"))
def test_record_injection(pg_server: Server, name: str) -> None:
    """A record-injection fixture, each step through the store's record call (the packaged record-injection.sql)."""
    directory = FIXTURES / "record-injection" / name
    seed = read_json(directory / "seed.json")
    steps = read_json(directory / "steps.json")["steps"]
    expected = read_json(directory / "expected.json")

    database = create_database(pg_server, "inject_" + name)
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for stored in seed["records"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)
    with psycopg.connect(owner) as connection, connection.transaction():
        for row in seed["injections"]:
            connection.execute(
                "INSERT INTO agent_experience.experience_injections (experience_id, tenant_id, application_id, "
                "project_id, team_id, agent_id, user_id, last_injected_at, injection_count) VALUES (%s::uuid, %s, %s, "
                "%s, %s, %s, %s, %s::timestamptz, %s::bigint)",
                tuple(row[column] for column in INJECTION_COLUMNS),
            )

    before = _clock(owner)
    for step in steps:
        if "record" in step:
            scope = scope_of(step["record"]["scope"])
            store.record_injection(
                AuthorizationBounds.of(scope),
                scope,
                [uuid.UUID(text) for text in step["record"]["experienceIds"]],
                Instant.parse(step["record"]["injectedAt"]),
            )
        else:
            erase = step["erase"]
            with psycopg.connect(owner) as connection:
                purged = connection.execute(
                    "SELECT purge_outcome FROM agent_experience.purge_experience_record(%s::uuid, %s, %s, %s, %s, %s, "
                    "%s, NULL, %s::timestamptz)",
                    (erase["experienceId"], *scope_of(erase["scope"]).fields(), erase["deletedAt"]),
                ).fetchone()
            assert purged == ("Deleted",)
    after = _clock(owner)

    with psycopg.connect(owner) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(
            "SELECT "
            + ", ".join(f"{column}::text" for column in INJECTION_COLUMNS)
            + ", last_injected_at FROM agent_experience.experience_injections ORDER BY experience_id"
        ).fetchall()
    clamped = {
        item["experience_id"]
        for item in expected["experience_injections"]
        if item["last_injected_at"] == DATABASE_CLOCK
    }
    actual: list[dict[str, Any]] = []
    for row in rows:
        values = dict(zip(INJECTION_COLUMNS, row[:-1], strict=True))
        if values["experience_id"] in clamped:
            assert before <= row[-1] <= after, (values, before, after)
            values["last_injected_at"] = DATABASE_CLOCK
        actual.append(values)
    difference = json_difference(expected["experience_injections"], actual, "$.experience_injections")
    assert difference is None, difference


def test_without_the_1_4_grants_recording_an_injection_fails_and_writes_nothing(pg_server: Server) -> None:
    from _postgres import APPLICATION_ROLE_PRIVILEGES_1_0

    database = create_database(pg_server, "inject_no_grant", privileges=APPLICATION_ROLE_PRIVILEGES_1_0)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    record = finalized.record
    bounds = AuthorizationBounds.of(record.scope)
    store.insert_finalized(bounds, finalized, actor="contract-principal")
    with pytest.raises(psycopg.errors.InsufficientPrivilege):
        store.record_injection(bounds, record.scope, [record.experience_id], Instant.now())
    with psycopg.connect(pg_server.conninfo(database, OWNER, PASSWORD)) as connection:
        count = connection.execute("SELECT count(*) FROM agent_experience.experience_injections").fetchone()
    assert count == (0,)


def test_record_injection_refuses_a_scope_outside_the_bounds_and_more_than_200_ids(pg_server: Server) -> None:
    from dataclasses import replace

    from agentexperience.inmemory import AuthorizationDenied

    database = create_database(pg_server, "inject_refused")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    scope = scope_of(
        {
            "tenantId": "contract-tenant",
            "applicationId": "contract-app",
            "projectId": "contract-project",
            "agentId": "a",
        }
    )
    bounds = AuthorizationBounds.of(scope)
    with pytest.raises(AuthorizationDenied):
        store.record_injection(bounds, replace(scope, agent_id="b"), [uuid.uuid4()], Instant.now())
    with pytest.raises(ValueError):
        store.record_injection(bounds, scope, [uuid.uuid4() for _ in range(201)], Instant.now())
    with pytest.raises(ValueError):
        store.record_injection(bounds, scope, [uuid.UUID(int=0)], Instant.now())
    store.record_injection(bounds, scope, [], Instant.now())
