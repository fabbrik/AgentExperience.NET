"""
The PostgreSQL leg of the conformance runner: finalize fixtures written through the Python store and read back row
for row, and every search fixture run through the store's text search, then ranked.
"""

from __future__ import annotations

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
