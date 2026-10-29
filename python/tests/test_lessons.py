"""
Story 23.4: the lessons view in Python (contract 1.7). The ``lessons-view`` fixture family through the in-memory store
and the PostgreSQL store (the packaged ``list-experience-lessons.sql`` run as written), then every row of the story's
I/O matrix on both stores. Story 27.2 (contract 1.10): the cases with each lesson's measured benefit, whose holdout
rows the seed holds, and the case whose holdout read fails.
"""

from __future__ import annotations

import copy
import uuid
from collections.abc import Callable
from dataclasses import replace
from typing import Any

import pytest
from _benefit import TOLERANCE
from _contract import FIXTURES, cases, json_difference, read_json, scope_of

from agentexperience import (
    AuthorizationBounds,
    BlockOptions,
    ExperienceLessons,
    ExperienceRecord,
    Instant,
    LessonsQuery,
    Scope,
    from_stored,
)
from agentexperience.benefit import DEFAULT_MINIMUM_RUNS_PER_ARM
from agentexperience.benefit_policy import BenefitPolicy
from agentexperience.block import render_block
from agentexperience.inmemory import InMemoryExperienceStore, LessonsRead, LessonsReader
from agentexperience.lessons import format_number, render_alone
from agentexperience.observer import classify

FAMILY = FIXTURES / "lessons-view"
NOW = Instant.parse("2026-03-02T15:00:00+00:00")
BASE = "newest-first-with-eligibility"


def _instant_text(text: str) -> str:
    # PostgreSQL's text form ("2026-03-02 08:00:00.123456+00") as an ISO 8601 instant.
    iso = text.replace(" ", "T", 1)
    return iso + ":00" if len(iso) > 3 and iso[-3] in "+-" else iso


def _normalized(page: dict[str, Any]) -> dict[str, Any]:
    """A page with every instant in one text form, so instants compare as instants."""
    result = copy.deepcopy(page)
    result["generatedAt"] = Instant.parse(result["generatedAt"]).format()
    for lesson in result["lessons"]:
        for name in ("createdAt", "updatedAt", "lastInjectedAt"):
            if lesson[name] is not None:
                lesson[name] = Instant.parse(lesson[name]).format()
    return result


def memory_store(seed: dict[str, Any]) -> InMemoryExperienceStore:
    """An in-memory store holding a seed's records, with its injections recorded at the instants the seed names, and
    its holdout rows (contract 1.10)."""
    from test_benefit import _holdout_row

    store = InMemoryExperienceStore([from_stored(stored) for stored in seed["records"]], clock=lambda: NOW)
    for row in seed.get("assignments", ()):
        store.seed_holdout_row(_holdout_row(row))
    for row in seed["injections"]:
        scope = scope_of(
            {
                "tenantId": row["tenant_id"],
                "applicationId": row["application_id"],
                "projectId": row["project_id"],
                "teamId": row["team_id"],
                "agentId": row["agent_id"],
                "userId": row["user_id"],
            }
        )
        for _ in range(int(row["injection_count"])):
            store.record_injection(
                AuthorizationBounds.of(scope),
                scope,
                [uuid.UUID(row["experience_id"])],
                Instant.parse(_instant_text(row["last_injected_at"])),
            )
    return store


def postgres_store(pg_server: Any, label: str, seed: dict[str, Any]) -> Any:
    """A PostgreSQL store over a fresh database holding a seed's records, and its injections as the schema's owner."""
    import psycopg
    from _postgres import OWNER, PASSWORD, application_conninfo, create_database
    from test_benefit import _ASSIGNMENT_COLUMNS, _INSERT_ASSIGNMENT
    from test_postgres import INJECTION_COLUMNS

    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "lessons_" + label)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for stored in seed["records"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)
    with psycopg.connect(pg_server.conninfo(database, OWNER, PASSWORD)) as connection, connection.transaction():
        for row in seed["injections"]:
            connection.execute(
                "INSERT INTO agent_experience.experience_injections (experience_id, tenant_id, application_id, "
                "project_id, team_id, agent_id, user_id, last_injected_at, injection_count) VALUES (%s::uuid, %s, %s, "
                "%s, %s, %s, %s, %s::timestamptz, %s::bigint)",
                tuple(row[column] for column in INJECTION_COLUMNS),
            )
        for row in seed.get("assignments", ()):
            connection.execute(_INSERT_ASSIGNMENT, tuple(row[column] for column in _ASSIGNMENT_COLUMNS))
    store.database = database  # type: ignore[attr-defined]
    return store


def owner_execute(pg_server: Any, store: Any, statement: str) -> None:
    import psycopg
    from _postgres import OWNER, PASSWORD

    with psycopg.connect(pg_server.conninfo(store.database, OWNER, PASSWORD), autocommit=True) as connection:
        connection.execute(statement.encode("utf-8"))


@pytest.fixture(params=["memory", pytest.param("postgres", marks=pytest.mark.postgres)])
def make_store(request: pytest.FixtureRequest) -> Callable[[dict[str, Any]], Any]:
    """Builds a store of the parametrized kind from a seed."""
    counter = iter(range(10_000))

    def make(seed: dict[str, Any]) -> Any:
        if request.param == "memory":
            return memory_store(seed)
        pg_server = request.getfixturevalue("pg_server")
        return postgres_store(pg_server, f"{request.node.name}_{next(counter)}", seed)

    return make


def base_seed() -> dict[str, Any]:
    seed: dict[str, Any] = read_json(FAMILY / BASE / "seed.json")
    return seed


def the_scope() -> Scope:
    return scope_of(read_json(FAMILY / BASE / "query.json")["scope"])


def view(store: Any, **options: Any) -> ExperienceLessons:
    return ExperienceLessons(store, clock=lambda: NOW, **options)


# ---- The fixtures ------------------------------------------------------------------------------------------------


def run_case(store: Any, name: str, options: dict[str, Any] | None = None) -> None:
    query = read_json(FAMILY / name / "query.json")
    expected = read_json(FAMILY / name / "expected.json")
    scope = scope_of(query["scope"])
    # Contract 1.10: the benefit policy the view reads with (its window is the default).
    policy = BenefitPolicy(
        minimum_runs_per_arm=query.get("minimumRunsPerArm") or DEFAULT_MINIMUM_RUNS_PER_ARM,
        per_model=query.get("perModel", False),
    )
    lessons = ExperienceLessons(store, clock=lambda: Instant.parse(query["now"]), benefit=policy, **(options or {}))
    list_query = LessonsQuery(
        query["statuses"],
        query["limit"],
        query["includeIneligible"],
        include_measured_benefit=query.get("includeMeasuredBenefit", False),
    )
    if query.get("holdoutReadRevoked", False):
        # The holdout read fails, and the list with it, as a store failure.
        with pytest.raises(Exception) as failure:
            lessons.list_scope(AuthorizationBounds.of(scope), scope, list_query)
        assert classify(failure.value) == expected["errorClass"]
        return
    page = lessons.list_scope(AuthorizationBounds.of(scope), scope, list_query)
    difference = json_difference(_normalized(expected), _normalized(page.to_json()), tolerance=TOLERANCE)
    assert difference is None, difference


class HoldoutReadFails(InMemoryExperienceStore):
    """An in-memory store whose holdout read fails as a store failure: the in-memory form of ``holdoutReadRevoked``."""

    def read_holdout_outcomes(self, *args: Any, **kwargs: Any) -> Any:
        raise OSError("the holdout read failed")


@pytest.mark.parametrize("name", cases("lessons-view"))
def test_lessons_view_in_memory(name: str) -> None:
    seed = read_json(FAMILY / name / "seed.json")
    store = memory_store(seed)
    if read_json(FAMILY / name / "query.json").get("holdoutReadRevoked", False):
        failing = HoldoutReadFails([from_stored(stored) for stored in seed["records"]], clock=lambda: NOW)
        store = failing
    run_case(store, name)


@pytest.mark.postgres
@pytest.mark.parametrize("name", cases("lessons-view"))
def test_lessons_view_postgres(pg_server: Any, name: str) -> None:
    store = postgres_store(pg_server, name, read_json(FAMILY / name / "seed.json"))
    if read_json(FAMILY / name / "query.json").get("holdoutReadRevoked", False):
        from _postgres import APPLICATION

        owner_execute(
            pg_server, store, f'REVOKE SELECT ON agent_experience.experience_holdout_assignments FROM "{APPLICATION}"'
        )
    run_case(store, name)


def test_the_lessons_view_has_twelve_cases() -> None:
    # Contract 1.7's eight, and contract 1.10's four with the measured benefit.
    assert len(cases("lessons-view")) == 12
    assert len([name for name in cases("lessons-view") if name.startswith("measured-benefit")]) == 4


def test_without_the_query_asking_nothing_more_is_read() -> None:
    seed = read_json(FAMILY / "measured-benefit" / "seed.json")
    failing = HoldoutReadFails([from_stored(stored) for stored in seed["records"]], clock=lambda: NOW)
    scope = scope_of(read_json(FAMILY / "measured-benefit" / "query.json")["scope"])
    page = view(failing).list_scope(AuthorizationBounds.of(scope), scope)
    assert page.lessons and all(lesson.measured_benefit is None for lesson in page.lessons)
    assert all("measuredBenefit" not in lesson for lesson in page.to_json()["lessons"])


def test_a_store_that_does_not_read_the_holdout_gives_no_measured_benefit() -> None:
    scope = the_scope()
    store = NoHoldout(memory_store(base_seed()))
    page = view(store).list_scope(AuthorizationBounds.of(scope), scope, LessonsQuery(include_measured_benefit=True))
    assert page.lessons and all(lesson.measured_benefit is None for lesson in page.lessons)
    assert all(lesson["measuredBenefit"] is None for lesson in page.to_json()["lessons"])


class NoHoldout:
    """A lessons reader that does not read the holdout."""

    def __init__(self, inner: InMemoryExperienceStore) -> None:
        self._inner = inner

    def list_lessons_page(self, *args: Any, **kwargs: Any) -> LessonsRead:
        return self._inner.list_lessons_page(*args, **kwargs)


# ---- The matrix, on both stores ----------------------------------------------------------------------------------


def test_default_list_is_every_status_newest_first_with_eligibility(make_store: Callable[..., Any]) -> None:
    page = view(make_store(base_seed())).list_scope(AuthorizationBounds.of(the_scope()), the_scope())
    assert page.outcome == "Found"
    assert page.generated_at == NOW
    assert len(page.lessons) == 9 and not page.has_more and page.injections_tracked
    keys = [(lesson.created_at, str(lesson.experience_id)) for lesson in page.lessons]
    assert keys == sorted(keys, key=lambda key: key[0], reverse=True)
    assert {lesson.status for lesson in page.lessons} >= {"Validated", "Reinforced", "Quarantined", "Stale"}
    assert all(check.result in ("Pass", "Fail", "Unknown") for lesson in page.lessons for check in lesson.checks)


def test_ineligible_lessons_are_hidden_and_the_limit_counts_what_is_listed(make_store: Callable[..., Any]) -> None:
    lessons = view(make_store(base_seed()))
    bounds, scope = AuthorizationBounds.of(the_scope()), the_scope()
    page = lessons.list_scope(bounds, scope, LessonsQuery(include_ineligible=False))
    assert page.lessons and all(lesson.is_eligible for lesson in page.lessons) and not page.has_more
    two = lessons.list_scope(bounds, scope, LessonsQuery(limit=2, include_ineligible=False))
    assert len(two.lessons) == 2 and two.has_more


def test_limit_and_has_more(make_store: Callable[..., Any]) -> None:
    lessons = view(make_store(base_seed()))
    bounds, scope = AuthorizationBounds.of(the_scope()), the_scope()
    assert lessons.list_scope(bounds, scope, LessonsQuery(limit=4)).has_more
    exact = lessons.list_scope(bounds, scope, LessonsQuery(limit=9))
    assert len(exact.lessons) == 9 and not exact.has_more


@pytest.mark.parametrize("limit", [0, -1, 501])
def test_an_invalid_limit_is_refused(limit: int) -> None:
    with pytest.raises(ValueError):
        LessonsQuery(limit=limit)


@pytest.mark.parametrize("statuses", [[], ["Bogus"], ["Validated", "Nope"], "Validated"])
def test_invalid_statuses_are_refused(statuses: Any) -> None:
    with pytest.raises(ValueError):
        LessonsQuery(statuses=statuses)


def test_a_repeated_status_counts_once() -> None:
    assert LessonsQuery(statuses=["Stale", "Stale", "Validated"]).statuses == ("Stale", "Validated")


def test_a_scope_outside_the_bounds_is_a_denied_empty_page_and_runs_no_read(make_store: Callable[..., Any]) -> None:
    store = make_store(base_seed())
    scope = the_scope()
    calls: list[object] = []
    original = store.list_lessons_page

    def spy(*args: Any) -> LessonsRead:
        calls.append(args)
        read: LessonsRead = original(*args)
        return read

    store.list_lessons_page = spy
    page = view(store).list_scope(replace(AuthorizationBounds.of(scope), agent_id="someone-else"), scope)
    assert (page.outcome, page.lessons, page.has_more, page.out_of_scope_dropped, page.injections_tracked) == (
        "Denied",
        (),
        False,
        0,
        False,
    )
    assert calls == []
    # Another scope the bounds permit lists only that scope's records.
    other = replace(scope, agent_id="someone-else")
    empty = view(store).list_scope(AuthorizationBounds.of(other), other)
    assert empty.outcome == "Found" and empty.lessons == ()


def test_the_store_refuses_a_scope_outside_the_bounds(make_store: Callable[..., Any]) -> None:
    from agentexperience.inmemory import AuthorizationDenied

    scope = the_scope()
    with pytest.raises(AuthorizationDenied):
        make_store(base_seed()).list_lessons_page(replace(AuthorizationBounds.of(scope), agent_id="x"), scope, None, 10)


def test_never_injected_and_injected(make_store: Callable[..., Any]) -> None:
    seed = base_seed()
    seed["injections"] = seed["injections"][:1]
    page = view(make_store(seed)).list_scope(AuthorizationBounds.of(the_scope()), the_scope())
    assert page.injections_tracked
    first, second = page.lessons[0], page.lessons[1]
    assert first.injection_count == 3 and first.last_injected_at == Instant.parse("2026-03-02T08:00:00.123456+00:00")
    assert second.injection_count == 0 and second.last_injected_at is None


def test_an_in_memory_store_without_injections_is_not_tracked() -> None:
    class Untracked:
        def __init__(self, inner: InMemoryExperienceStore) -> None:
            self.inner = inner

        def list_lessons_page(
            self, bounds: AuthorizationBounds, scope: Scope, statuses: Any, limit: int
        ) -> LessonsRead:
            read = self.inner.list_lessons_page(bounds, scope, statuses, limit)
            return LessonsRead(tuple(replace(row, injection=None) for row in read.rows), False)

    assert isinstance(Untracked(InMemoryExperienceStore()), LessonsReader)
    page = view(Untracked(memory_store(base_seed()))).list_scope(AuthorizationBounds.of(the_scope()), the_scope())
    assert not page.injections_tracked and len(page.lessons) == 9
    assert all(lesson.last_injected_at is None and lesson.injection_count == 0 for lesson in page.lessons)


@pytest.mark.postgres
@pytest.mark.parametrize("how", ["revoke", "drop"])
def test_postgres_without_the_injections_is_not_tracked(pg_server: Any, how: str) -> None:
    store = postgres_store(pg_server, "untracked_" + how, base_seed())
    if how == "revoke":
        from _postgres import APPLICATION

        owner_execute(pg_server, store, f"REVOKE SELECT ON agent_experience.experience_injections FROM {APPLICATION}")
    else:
        owner_execute(pg_server, store, "DROP TABLE agent_experience.experience_injections CASCADE")
    for query in (LessonsQuery(), LessonsQuery(statuses=["Validated", "Reinforced"])):
        page = view(store).list_scope(AuthorizationBounds.of(the_scope()), the_scope(), query)
        assert page.outcome == "Found" and page.lessons and not page.injections_tracked
        assert all(lesson.last_injected_at is None and lesson.injection_count == 0 for lesson in page.lessons)


def test_an_unrenderable_record_is_listed_and_the_rest_still_are(make_store: Callable[..., Any]) -> None:
    broken = uuid.UUID(base_seed()["records"][1]["columns"]["experience_id"])

    def render(record: ExperienceRecord, options: BlockOptions) -> Any:
        if record.experience_id == broken:
            raise KeyError("boom")
        return render_alone(record, options)

    page = view(make_store(base_seed()), render=render).list_scope(AuthorizationBounds.of(the_scope()), the_scope())
    assert len(page.lessons) == 9
    lesson = next(item for item in page.lessons if item.experience_id == broken)
    assert (lesson.is_eligible, lesson.ineligibility, lesson.block_text, lesson.checks) == (
        False,
        "Unrenderable",
        "",
        (),
    )
    assert lesson.ineligible_reason == "it could not be rendered (KeyError)"
    assert sum(item.is_eligible for item in page.lessons) == 4
    hidden = view(make_store(base_seed()), render=render).list_scope(
        AuthorizationBounds.of(the_scope()), the_scope(), LessonsQuery(include_ineligible=False)
    )
    assert all(item.experience_id != broken for item in hidden.lessons)


def test_a_block_over_the_budget_is_listed_over_budget(make_store: Callable[..., Any]) -> None:
    page = view(make_store(base_seed()), block_options=BlockOptions(max_bytes=400)).list_scope(
        AuthorizationBounds.of(the_scope()), the_scope()
    )
    lesson = page.lessons[0]
    size = len(lesson.block_text.encode("utf-8"))
    assert size > 400
    assert (lesson.is_eligible, lesson.ineligibility) == (False, "OverByteBudget")
    assert lesson.ineligible_reason == f"its block ({size} bytes) is larger than the 400-byte injection budget"


def test_a_model_authored_lesson_is_excluded_only_when_the_setup_excludes_them(make_store: Callable[..., Any]) -> None:
    seed = base_seed()
    seed["records"][0]["payload"]["reflection"]["authorship"] = "Model"
    first = uuid.UUID(seed["records"][0]["columns"]["experience_id"])
    bounds, scope = AuthorizationBounds.of(the_scope()), the_scope()
    store = make_store(seed)

    included = next(lesson for lesson in view(store).list_scope(bounds, scope).lessons if lesson.experience_id == first)
    assert included.is_eligible
    excluded = next(
        lesson
        for lesson in view(store, exclude_model_authored=True).list_scope(bounds, scope).lessons
        if lesson.experience_id == first
    )
    assert (excluded.is_eligible, excluded.ineligibility) == (False, "ModelAuthored")
    assert excluded.ineligible_reason == "model-authored lessons are excluded"
    assert "Authored: by a model" in excluded.block_text


def test_the_view_never_writes(make_store: Callable[..., Any]) -> None:
    store = make_store(base_seed())
    before = store.injections() if isinstance(store, InMemoryExperienceStore) else None
    view(store).list_scope(AuthorizationBounds.of(the_scope()), the_scope())
    if before is not None:
        assert store.injections() == before


# ---- Units -------------------------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("value", "text"),
    [
        (0.5, "0.50"),
        (0.4, "0.40"),
        (0.125, "0.13"),
        (0.845, "0.85"),  # the double below 0.845: the exact value rounds down
        (0.875, "0.88"),
        (0.0, "0.00"),
        (1.0, "1.00"),
        (float("nan"), "(unavailable)"),
        (float("inf"), "(unavailable)"),
    ],
)
def test_two_decimals_from_15_significant_digits(value: float, text: str) -> None:
    assert format_number(value) == text


def test_a_nan_confidence_is_below_the_floor() -> None:
    seed = base_seed()
    stored = seed["records"][0]
    record = replace(from_stored(stored), reuse_confidence=float("nan"))
    store = InMemoryExperienceStore([record])
    page = view(store).list_scope(AuthorizationBounds.of(record.scope), record.scope)
    assert page.lessons[0].ineligibility == "BelowConfidenceFloor"
    assert page.lessons[0].ineligible_reason == "confidence (unavailable) is below the retrieval floor 0.50"


def test_render_alone_is_the_block_of_one_record_numbered_one_without_limits() -> None:
    record = from_stored(base_seed()["records"][0])
    block = render_alone(record)
    assert block.text == render_alone(record, BlockOptions(max_records=1, max_bytes=1)).text
    assert "--- RECORD 1:" in block.text and "Matched: no ranking data" in block.text
    assert block.byte_count == len(block.text.encode("utf-8"))
    assert render_block([], BlockOptions()).text == ""


def test_the_view_needs_a_store_that_lists_lessons() -> None:
    with pytest.raises(TypeError):
        ExperienceLessons(object())  # type: ignore[arg-type]


def test_list_needs_an_identity() -> None:
    with pytest.raises(ValueError):
        view(InMemoryExperienceStore()).list()


def test_a_fixture_pages_ordering_is_created_at_descending_then_id() -> None:
    seed = base_seed()
    twin = copy.deepcopy(seed["records"][0])
    twin["columns"]["experience_id"] = "00000070-0000-4000-8000-0000000000ff"
    twin["payload"]["reflection"]["reflectionId"] = "00000070-0000-4000-8000-0000000002ff"
    seed["records"].append(twin)
    store = memory_store(seed)
    scope = the_scope()
    ids = [str(lesson.experience_id) for lesson in view(store).list_scope(AuthorizationBounds.of(scope), scope).lessons]
    assert ids[:2] == ["00000070-0000-4000-8000-000000000001", "00000070-0000-4000-8000-0000000000ff"]


def test_the_one_call_setup_reaches_the_lessons_view() -> None:
    pytest.importorskip("agent_framework")
    from _maf import ScriptedClient
    from agent_framework import Agent

    from agentexperience.identity import development_identity
    from agentexperience.maf import ExperienceSettings, agent_experience_lessons, use_agent_experience

    store = InMemoryExperienceStore([from_stored(stored) for stored in base_seed()["records"]], clock=lambda: NOW)
    identity = replace(development_identity(), scope=the_scope())
    agent = use_agent_experience(
        Agent(client=ScriptedClient(), name="triage"),
        store=store,
        identity=identity,
        block_options=BlockOptions(max_bytes=400),
        clock=lambda: NOW,
    )
    page = agent_experience_lessons(agent).list()
    assert page.generated_at == NOW and len(page.lessons) == 9
    assert page.lessons[0].ineligibility == "OverByteBudget"  # the setup's block options
    settings = ExperienceSettings(store=store, identity=identity)
    assert len(agent_experience_lessons(settings).list().lessons) == 9
    with pytest.raises(TypeError):
        agent_experience_lessons(Agent(client=ScriptedClient(), name="plain"))
