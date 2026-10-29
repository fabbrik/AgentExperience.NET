"""
Contract 1.9: the benefit estimate in Python. The ``benefit-estimate`` fixtures (no database), the
``holdout-outcomes`` fixtures through the in-memory store and, in the PostgreSQL leg, through the PostgreSQL store (the
packaged ``query-holdout-outcomes.sql``), the ``model-metadata`` fixture, the model key's exclusion from what the
finalize and inject fixtures pin, and ``estimate_benefit``'s edges: a store that does not record the holdout, a foreign
scope, a sealed run record, invalid options and a run paged apart.
"""

from __future__ import annotations

import copy
import dataclasses
import json
import uuid
from typing import Any

import pytest
from _benefit import TOLERANCE, estimate_json, row_json, row_of
from _contract import FIXTURES, cases, json_difference, read_json, run_finalize_case, scope_of
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database

from agentexperience import (
    MODEL_METADATA_KEY,
    AuthorizationBounds,
    BenefitAccumulator,
    BenefitOptions,
    BlockOptions,
    EnvironmentFingerprint,
    HoldoutArm,
    HoldoutOutcomeRow,
    Instant,
    Scope,
    estimate,
    estimate_benefit,
    from_stored,
    normalize_model_id,
    render_block,
    to_stored,
)
from agentexperience.block import ranked_from_json
from agentexperience.inmemory import HoldoutRow, InMemoryExperienceStore
from agentexperience.model import reported_model, with_model
from agentexperience.search import environment_compatibility

# ---- benefit-estimate ---------------------------------------------------------------------------------------------


def _options(directory: Any) -> BenefitOptions:
    options = read_json(directory / "options.json")
    return BenefitOptions(options["minimumRunsPerArm"], options["splitByModel"])


@pytest.mark.parametrize("name", cases("benefit-estimate"))
def test_benefit_estimate(name: str) -> None:
    directory = FIXTURES / "benefit-estimate" / name
    rows = [row_of(row) for row in read_json(directory / "rows.json")["rows"]]
    options = _options(directory)
    expected = read_json(directory / "expected.json")

    def check(actual: Any, how: str) -> None:
        difference = json_difference(expected, json.loads(json.dumps(estimate_json(actual))), tolerance=TOLERANCE)
        assert difference is None, f"{how}: {difference}"

    check(estimate(rows, options), "pure")
    check(estimate(reversed(rows), options), "pure, rows reversed")
    for size in (1, 3, 1000):
        accumulator = BenefitAccumulator(options)
        for start in range(0, len(rows), size):
            accumulator.add_all(rows[start : start + size])
        check(accumulator.to_estimate(), f"paged by {size}")


def test_the_paged_form_refuses_a_run_paged_apart() -> None:
    run, other = uuid.uuid4(), uuid.uuid4()
    at = Instant.parse("2026-03-02T09:00:00+00:00")
    accumulator = BenefitAccumulator()
    accumulator.add(HoldoutOutcomeRow(run, "Treatment", uuid.uuid4(), at, "Verified"))
    accumulator.add(HoldoutOutcomeRow(other, "Holdout", uuid.uuid4(), at, "Failed"))
    with pytest.raises(ValueError, match="added again"):
        accumulator.add(HoldoutOutcomeRow(run, "Treatment", uuid.uuid4(), at, "Verified"))


@pytest.mark.parametrize("minimum", [0, -1])
def test_a_minimum_below_one_is_refused_before_anything_is_read(minimum: int) -> None:
    with pytest.raises(ValueError, match="minimum_runs_per_arm"):
        BenefitOptions(minimum_runs_per_arm=minimum)


# ---- model-metadata -----------------------------------------------------------------------------------------------

MODEL_METADATA = read_json(FIXTURES / "model-metadata" / "cases.json")


def test_the_model_key_is_the_contract_one() -> None:
    assert MODEL_METADATA["key"] == MODEL_METADATA_KEY
    assert MODEL_METADATA["maxLength"] == 256


@pytest.mark.parametrize("index", range(len(MODEL_METADATA["normalize"])))
def test_model_metadata_normalize(index: int) -> None:
    case = MODEL_METADATA["normalize"][index]
    assert normalize_model_id(case["modelId"]) == case["recorded"]


@pytest.mark.parametrize("index", range(len(MODEL_METADATA["environment"])))
def test_model_metadata_environment(index: int) -> None:
    case = MODEL_METADATA["environment"][index]
    assert with_model(case["metadata"], normalize_model_id(case["modelId"])) == case["expected"]


class _Client:
    def __init__(self, model: Any) -> None:
        self.model = model


class _Agent:
    def __init__(self, options: dict[str, Any], client: Any) -> None:
        self.default_options = options
        self.client = client


def test_the_reported_model_is_the_agents_option_else_its_clients() -> None:
    assert reported_model(_Agent({"model": "  gpt-4o "}, _Client("other"))) == "gpt-4o"
    assert reported_model(_Agent({}, _Client("client-model"))) == "client-model"
    assert reported_model(_Agent({"model": "  "}, _Client(None))) is None
    assert reported_model(_Agent({}, _Client(42))) is None
    assert reported_model(object()) is None


@pytest.mark.parametrize("name", cases("finalize"))
def test_the_model_key_changes_no_finalize_fixture_but_the_environment(name: str) -> None:
    directory = FIXTURES / "finalize" / name
    case = copy.deepcopy(read_json(directory / "input.json"))
    case["run"]["environment"]["metadata"][MODEL_METADATA_KEY] = "contract-model"
    finalized, _ = run_finalize_case(case)
    expected = read_json(directory / "expected-record.json")
    expected["payload"]["environment"]["metadata"][MODEL_METADATA_KEY] = "contract-model"
    difference = json_difference(expected, json.loads(json.dumps(to_stored(finalized.record))))
    assert difference is None, difference
    difference = json_difference(read_json(directory / "expected-event.json"), finalized.event.to_json())
    assert difference is None, difference


@pytest.mark.parametrize("name", cases("inject"))
def test_the_model_key_changes_no_inject_fixture(name: str) -> None:
    directory = FIXTURES / "inject" / name
    entries = copy.deepcopy(read_json(directory / "records.json"))
    for entry in entries:
        entry["record"]["payload"]["environment"]["metadata"][MODEL_METADATA_KEY] = "contract-model"
    options = read_json(directory / "options.json")
    block = render_block(
        ranked_from_json(entries),
        BlockOptions(
            rendering=options["rendering"],
            failure_detail=options["failureDetail"],
            max_records=options["maxRecords"],
            max_bytes=options["maxBytes"],
            approach_arguments=options["approachArguments"],
        ),
    )
    assert block.text.encode("utf-8") == (directory / "expected-block.txt").read_bytes()


def test_ranking_ignores_the_model_key() -> None:
    assert environment_compatibility({MODEL_METADATA_KEY: "a"}, {MODEL_METADATA_KEY: "b"}) == 1.0
    assert environment_compatibility({MODEL_METADATA_KEY: "a", "region": "eu"}, {MODEL_METADATA_KEY: "a"}) == 1.0
    assert environment_compatibility({"region": "eu"}, {"region": "eu", MODEL_METADATA_KEY: "b"}) == 1.0


# ---- holdout-outcomes ---------------------------------------------------------------------------------------------


def _instant_of_postgres_text(text: str) -> Instant:
    """``2026-03-02 09:01:00.123456+00`` (PostgreSQL's text form in UTC) as an instant."""
    assert text.endswith("+00"), text
    return Instant.parse(text[:-3].replace(" ", "T") + "+00:00")


def _holdout_row(value: dict[str, Any]) -> HoldoutRow:
    return HoldoutRow(
        uuid.UUID(value["run_id"]),
        uuid.UUID(value["experience_id"]),
        int(value["revision"]),
        value["arm"],
        Scope(
            value["tenant_id"],
            value["application_id"],
            value["project_id"],
            value["team_id"],
            value["agent_id"],
            value["user_id"],
        ),
        _instant_of_postgres_text(value["assigned_at"]),
    )


def _pages(store: Any, scope: Scope, limit: int) -> list[dict[str, Any]]:
    pages: list[dict[str, Any]] = []
    after: tuple[uuid.UUID, uuid.UUID] | None = None
    while True:
        rows = store.read_holdout_outcomes(AuthorizationBounds.of(scope), scope, limit, after)
        pages.append(
            {
                "after": None if after is None else {"runId": str(after[0]), "experienceId": str(after[1])},
                "rows": [row_json(row) for row in rows],
            }
        )
        if len(rows) < limit:
            return pages
        after = (rows[-1].run_id, rows[-1].experience_id)


def _check_pages(expected: list[dict[str, Any]], actual: list[dict[str, Any]]) -> None:
    # assignedAt compares as an instant: normalise both sides through Instant.
    def normal(pages: list[dict[str, Any]]) -> list[dict[str, Any]]:
        result = copy.deepcopy(pages)
        for page in result:
            for row in page["rows"]:
                row["assignedAt"] = Instant.parse(row["assignedAt"]).format()
        return result

    difference = json_difference(normal(expected), normal(actual), "$.pages")
    assert difference is None, difference


@pytest.mark.parametrize("name", cases("holdout-outcomes"))
def test_holdout_outcomes_in_memory(name: str) -> None:
    directory = FIXTURES / "holdout-outcomes" / name
    seed = read_json(directory / "seed.json")
    store = InMemoryExperienceStore(from_stored(stored) for stored in seed["records"])
    for row in seed["assignments"]:
        store.seed_holdout_row(_holdout_row(row))
    query = read_json(directory / "query.json")
    _check_pages(
        read_json(directory / "expected.json")["pages"], _pages(store, scope_of(query["scope"]), query["limit"])
    )


_INSERT_ASSIGNMENT = (
    "INSERT INTO agent_experience.experience_holdout_assignments (run_id, experience_id, revision, arm, tenant_id, "
    "application_id, project_id, team_id, agent_id, user_id, assigned_at) VALUES (%s::uuid, %s::uuid, %s::bigint, %s, "
    "%s, %s, %s, %s, %s, %s, %s::timestamptz)"
)
_ASSIGNMENT_COLUMNS = (
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


@pytest.mark.postgres
@pytest.mark.parametrize("name", cases("holdout-outcomes"))
def test_holdout_outcomes_in_postgresql(pg_server: Server, name: str) -> None:
    import psycopg

    from agentexperience.postgres import PostgresExperienceStore

    directory = FIXTURES / "holdout-outcomes" / name
    seed = read_json(directory / "seed.json")
    database = create_database(pg_server, "outcomes_" + name)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for stored in seed["records"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)
    with psycopg.connect(pg_server.conninfo(database, OWNER, PASSWORD)) as owner, owner.transaction():
        for row in seed["assignments"]:
            owner.execute(_INSERT_ASSIGNMENT, tuple(row[column] for column in _ASSIGNMENT_COLUMNS))
    query = read_json(directory / "query.json")
    _check_pages(
        read_json(directory / "expected.json")["pages"], _pages(store, scope_of(query["scope"]), query["limit"])
    )


# ---- estimate_benefit ----------------------------------------------------------------------------------------------

SCOPE = Scope("benefit-tenant", "benefit-app", "benefit-project", agent_id="benefit-agent")


def test_a_store_that_does_not_record_the_holdout_is_not_tracked() -> None:
    estimate_ = estimate_benefit(object(), AuthorizationBounds.of(SCOPE), SCOPE)
    assert not estimate_.tracked
    assert estimate_.outcome == "Found"
    assert estimate_.overall.verdict == "NoEvidence" and estimate_.lessons == ()


def test_a_scope_outside_the_authorization_is_denied_and_reads_nothing() -> None:
    class Spy(InMemoryExperienceStore):
        reads = 0

        def read_holdout_outcomes(self, *args: Any, **kwargs: Any) -> list[HoldoutOutcomeRow]:
            Spy.reads += 1
            return super().read_holdout_outcomes(*args, **kwargs)

    other = Scope("benefit-tenant", "benefit-app", "benefit-project", agent_id="other-agent")
    estimate_ = estimate_benefit(Spy(), AuthorizationBounds.of(SCOPE), other)
    assert estimate_.outcome == "Denied" and estimate_.tracked
    assert estimate_.overall.treatment.runs == 0 and estimate_.lessons == ()
    assert Spy.reads == 0


def test_invalid_options_are_refused_before_anything_is_read() -> None:
    with pytest.raises(TypeError):
        estimate_benefit(InMemoryExperienceStore(), AuthorizationBounds.of(SCOPE), SCOPE, "split")  # type: ignore[arg-type]


def _memory_fleet() -> tuple[InMemoryExperienceStore, list[uuid.UUID]]:
    """A store with one lesson and 1,200 assigned runs (more than one page), each with its own record."""
    stored = read_json(FIXTURES / "holdout-outcomes" / "models" / "seed.json")["records"][0]
    lesson = from_stored(stored)
    lesson = dataclasses.replace(lesson, scope=SCOPE)
    store = InMemoryExperienceStore([lesson])
    runs: list[uuid.UUID] = []
    at = Instant.parse("2026-03-02T09:00:00+00:00")
    for n in range(1200):
        run = uuid.UUID(int=(n + 1) << 64)
        runs.append(run)
        arm: HoldoutArm = "Treatment" if n % 2 else "Holdout"
        store.seed_holdout_row(HoldoutRow(run, lesson.experience_id, 1, arm, SCOPE, at))
    return store, runs


def test_estimate_benefit_pages_through_every_row() -> None:
    store, runs = _memory_fleet()
    estimate_ = estimate_benefit(store, AuthorizationBounds.of(SCOPE), SCOPE)
    # No run has a record: every one is pending, counted once.
    assert estimate_.overall.pending == len(runs)
    assert len(estimate_.lessons) == 1 and estimate_.lessons[0].comparison.pending == len(runs)


@pytest.mark.postgres
def test_a_sealed_run_record_counts_as_pending(pg_server: Server) -> None:
    import psycopg

    from agentexperience.postgres import PostgresExperienceStore

    directory = FIXTURES / "holdout-outcomes" / "models"
    seed = read_json(directory / "seed.json")
    database = create_database(pg_server, "outcomes_sealed")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    lesson = from_stored(seed["records"][0])
    bounds = AuthorizationBounds.of(lesson.scope)
    store.insert_record(bounds, lesson)
    run = uuid.UUID("0000005f-0000-4000-8000-000000000901")
    with psycopg.connect(pg_server.conninfo(database, OWNER, PASSWORD)) as owner, owner.transaction():
        # The run's own record, sealed as the .NET sealing writes one (payload_version 2, opaque payload).
        owner.execute(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, "
            "project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, "
            "contradictions, revision, created_at, updated_at, payload_version, payload, search_vector_sealed) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, '(sealed)', 'Validated', 0.9, 1, 0, 1, now(), now(), 2, "
            "%s::jsonb, to_tsvector('english', 'sealed'))",
            (uuid.uuid4(), run, *lesson.scope.fields(), '{"sealed": "aexp-sealed:v1:AAAA"}'),
        )
        owner.execute(
            _INSERT_ASSIGNMENT,
            (run, lesson.experience_id, "1", "Treatment", *lesson.scope.fields(), "2026-03-02 09:00:00+00"),
        )
    rows = store.read_holdout_outcomes(bounds, lesson.scope, 1000)
    assert [(row.run_id, row.run_outcome, row.run_model) for row in rows] == [(run, None, None)]
    estimate_ = estimate_benefit(store, bounds, lesson.scope)
    assert estimate_.overall.pending == 1 and estimate_.overall.treatment.runs == 0


def test_capture_records_the_reported_model_unless_the_host_names_one() -> None:
    pytest.importorskip("agent_framework")
    from agentexperience.maf import _with_reported_model

    environment = EnvironmentFingerprint("host", "runtime", "os", None, {"region": "eu"})
    agent = _Agent({}, _Client(" gpt-4o "))
    assert _with_reported_model(environment, agent).metadata == {"region": "eu", MODEL_METADATA_KEY: "gpt-4o"}
    hosted = EnvironmentFingerprint("host", "runtime", "os", None, {MODEL_METADATA_KEY: " "})
    assert _with_reported_model(hosted, agent) is hosted
    assert _with_reported_model(environment, _Agent({}, _Client(None))) is environment
