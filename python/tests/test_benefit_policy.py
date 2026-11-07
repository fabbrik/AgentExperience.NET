"""
Story 27.2: acting on the benefit estimate in Python (contract 1.10). The ``benefit-select`` fixtures (the selection,
the block and every omission, byte for byte), the policy's options, the view, and the background cache: a cold cache
has no effect, one refresh runs per key, the interval and the failure backoff, the timeout, the window, and the
``benefit_refresh`` telemetry.
"""

from __future__ import annotations

import datetime
import threading
import uuid
from collections.abc import Callable, Mapping
from typing import Any

import pytest
from _benefit import TOLERANCE, estimate_json, estimate_of
from _contract import FIXTURES, cases, json_difference, read_json

from agentexperience import (
    AuthorizationBounds,
    BenefitPolicy,
    BenefitViews,
    BlockOptions,
    Instant,
    Scope,
    observer,
    select_block,
)
from agentexperience.benefit import HoldoutOutcomeCursor, HoldoutOutcomeRow, estimate
from agentexperience.benefit_policy import (
    MEASURED_HARMFUL_DETAIL,
    SCOPE_MEASURED_HARMFUL_DETAIL,
    BenefitCache,
    BenefitView,
    benefit_view,
    select_candidates,
)
from agentexperience.block import ranked_from_json

FAMILY = FIXTURES / "benefit-select"


# ---- The fixtures ------------------------------------------------------------------------------------------------


def run_select_case(name: str) -> dict[str, Any]:
    directory = FAMILY / name
    ranked = ranked_from_json(read_json(directory / "records.json"))
    the_estimate = estimate_of(read_json(directory / "estimate.json"))
    options = read_json(directory / "options.json")
    views = BenefitViews.of(the_estimate) if options["enabled"] else None
    view = None if views is None else views.for_model(options["model"] if options["perModel"] else None)
    selection = select_block(
        ranked,
        view,
        BlockOptions(max_records=options["maxRecords"], max_bytes=options["maxBytes"]),
        request_limit=options["requestLimit"],
        exclude_model_authored=options["excludeModelAuthored"],
        prefer_measured_helpful=options["preferMeasuredHelpful"],
    )
    return {
        "selected": [str(item.record.experience_id) for item in selection.selected],
        "injected": [str(record.experience_id) for record in selection.block.included],
        "omitted": [
            {"experienceId": str(item.experience_id), "reason": item.reason, "detail": item.detail}
            for item in selection.omitted
        ],
    }


@pytest.mark.parametrize("name", cases("benefit-select"))
def test_benefit_select(name: str) -> None:
    # Everything compares exactly: the order, the omissions and the detail strings, byte for byte.
    assert run_select_case(name) == read_json(FAMILY / name / "expected.json")


@pytest.mark.parametrize("name", cases("benefit-select"))
def test_the_estimate_reads_back_as_written(name: str) -> None:
    value = read_json(FAMILY / name / "estimate.json")
    assert json_difference(value, estimate_json(estimate_of(value)), tolerance=TOLERANCE) is None


def test_the_benefit_select_family_covers_every_case() -> None:
    assert len(cases("benefit-select")) == 15


# ---- The options -------------------------------------------------------------------------------------------------


def test_the_policy_is_off_by_default_with_dotnets_defaults() -> None:
    policy = BenefitPolicy()
    assert not policy.enabled and not policy.prefer_measured_helpful and not policy.per_model
    assert policy.window == datetime.timedelta(days=30)
    assert policy.minimum_runs_per_arm == 20
    assert policy.refresh_interval == datetime.timedelta(minutes=10)
    assert policy.refresh_timeout == datetime.timedelta(seconds=30)


@pytest.mark.parametrize(
    "options",
    [
        {"refresh_interval": datetime.timedelta(0)},
        {"refresh_interval": datetime.timedelta(seconds=-1)},
        {"refresh_timeout": datetime.timedelta(0)},
        {"refresh_timeout": datetime.timedelta(days=1, microseconds=1)},
        {"window": datetime.timedelta(0)},
        {"minimum_runs_per_arm": 0},
    ],
)
def test_an_invalid_policy_is_refused_even_when_off(options: Mapping[str, Any]) -> None:
    with pytest.raises(ValueError):
        BenefitPolicy(**options)


def test_a_duration_that_is_not_a_timedelta_is_refused() -> None:
    with pytest.raises(TypeError):
        BenefitPolicy(window=30)  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        BenefitPolicy(minimum_runs_per_arm=True)


def test_the_longest_timeout_is_one_day() -> None:
    assert BenefitPolicy(refresh_timeout=datetime.timedelta(days=1)).refresh_timeout == datetime.timedelta(days=1)


# ---- The rows a store answers ------------------------------------------------------------------------------------

SCOPE = Scope("policy-tenant", "policy-app", "policy-project", agent_id="policy-agent")
BOUNDS = AuthorizationBounds.of(SCOPE)
T0 = Instant.parse("2026-03-02T09:00:00+00:00")
X = uuid.UUID("00000027-0000-4000-8000-000000000001")
Y = uuid.UUID("00000027-0000-4000-8000-000000000002")


def rows(
    lesson: uuid.UUID,
    *,
    treatment_verified: bool,
    count: int = 3,
    at: Instant = T0,
    first: int = 0,
    run_model: str | None = None,
) -> list[HoldoutOutcomeRow]:
    """``count`` runs per arm naming ``lesson``: treatment runs verified or failed, holdout runs the other way."""
    result = []
    for i in range(count * 2):
        treatment = i < count
        verified = treatment_verified if treatment else not treatment_verified
        result.append(
            HoldoutOutcomeRow(
                uuid.UUID(int=(lesson.int % (1 << 100)) * 1000 + first + i + 1),
                "Treatment" if treatment else "Holdout",
                lesson,
                at,
                "Verified" if verified else "Failed",
                run_model,
                None,
            )
        )
    return sorted(result, key=lambda row: (row.run_id, row.experience_id))


class FakeStore:
    """Answers the holdout's outcome rows; can block, raise, and counts its reads."""

    def __init__(self, answer: list[HoldoutOutcomeRow]) -> None:
        self.answer = answer
        self.reads = 0
        self.failure: BaseException | None = None
        self.gate: threading.Event | None = None
        self._lock = threading.Lock()

    def read_holdout_outcomes(
        self, bounds: AuthorizationBounds, scope: Scope, limit: int, after: HoldoutOutcomeCursor | None = None
    ) -> list[HoldoutOutcomeRow]:
        with self._lock:
            self.reads += 1
        if self.gate is not None:
            self.gate.wait(5)
        if self.failure is not None:
            raise self.failure
        return [] if after is not None else sorted(self.answer, key=lambda row: (row.run_id, row.experience_id))


class Clock:
    def __init__(self, now: Instant) -> None:
        self.now = now

    def __call__(self) -> Instant:
        return self.now

    def advance(self, delta: datetime.timedelta) -> None:
        self.now = Instant(self.now.ticks + int(delta.total_seconds() * 10_000_000))


class Recording:
    """An observer that records every ``benefit_refresh`` it is told of."""

    def __init__(self) -> None:
        self.reports: list[tuple[str, str, str, str | None]] = []

    def start(self, source: str, operation: str) -> object | None:
        return None

    def finish(
        self,
        token: object | None,
        source: str,
        operation: str,
        outcome: str,
        error_class: str | None,
        error_type: str | None,
        attributes: Mapping[str, Any],
    ) -> None:
        if operation == "benefit_refresh":
            self.reports.append((source, operation, outcome, error_class))


@pytest.fixture
def recording() -> Any:
    hook = Recording()
    observer.set_observer(hook)
    try:
        yield hook
    finally:
        observer.set_observer(None)


POLICY = BenefitPolicy(enabled=True, minimum_runs_per_arm=2)


def cache_of(store: object, clock: Callable[[], Instant], policy: BenefitPolicy = POLICY) -> BenefitCache:
    return BenefitCache(store, policy, clock)


# ---- The view ----------------------------------------------------------------------------------------------------


def test_the_view_marks_what_hurts_and_helps_and_nothing_when_there_is_no_evidence() -> None:
    both = estimate(
        rows(X, treatment_verified=False) + rows(Y, treatment_verified=True, first=100), POLICY.estimator_options
    )
    view = benefit_view(both)
    assert view == BenefitView(False, frozenset({X}), frozenset({Y}))
    one = estimate(rows(X, treatment_verified=False, count=1), POLICY.estimator_options)
    assert benefit_view(one) is None
    assert BenefitViews.of(one) == BenefitViews(None, {})


def test_an_estimate_not_tracked_or_not_found_is_never_acted_on() -> None:
    import dataclasses

    hurts = estimate(rows(X, treatment_verified=False), POLICY.estimator_options)
    assert BenefitViews.of(hurts) is not None
    assert BenefitViews.of(dataclasses.replace(hurts, tracked=False)) is None
    assert BenefitViews.of(dataclasses.replace(hurts, outcome="Denied")) is None


def test_with_the_policy_off_or_no_view_the_selection_is_the_block_as_before() -> None:
    ranked = ranked_from_json(read_json(FIXTURES / "inject" / "byte-budget-and-record-limit" / "records.json"))
    options = read_json(FIXTURES / "inject" / "byte-budget-and-record-limit" / "options.json")
    block_options = BlockOptions(max_records=options["maxRecords"], max_bytes=options["maxBytes"])
    selection = select_block(ranked, None, block_options)
    assert (
        selection.block.text.encode("utf-8")
        == (FIXTURES / "inject" / "byte-budget-and-record-limit" / "expected-block.txt").read_bytes()
    )
    assert [item.reason for item in selection.omitted] == ["OverRecordLimit", "OverByteBudget"]


def test_the_details_are_dotnets() -> None:
    assert MEASURED_HARMFUL_DETAIL == (
        "The injection holdout measured this lesson as hurting: runs given it verified less often than runs it was "
        "withheld from."
    )
    assert SCOPE_MEASURED_HARMFUL_DETAIL == (
        "The injection holdout measured this scope's lessons together as hurting, so none is injected."
    )


def test_a_selection_without_a_view_never_reorders() -> None:
    ranked = ranked_from_json(read_json(FAMILY / "prefer-helpful" / "records.json"))
    selected, omitted = select_candidates(ranked, None, max_records=3, prefer_measured_helpful=True)
    assert [item.record.experience_id for item in selected] == [item.record.experience_id for item in ranked[:3]]
    assert [item.reason for item in omitted] == ["OverRecordLimit", "OverRecordLimit"]


# ---- The cache ---------------------------------------------------------------------------------------------------


def test_a_cold_cache_has_no_effect_and_starts_a_refresh(recording: Recording) -> None:
    store = FakeStore(rows(X, treatment_verified=False) + rows(Y, treatment_verified=True, first=100))
    cache = cache_of(store, Clock(T0))
    assert cache.current(BOUNDS, SCOPE, "principal") is None
    cache.wait()
    view = cache.current(BOUNDS, SCOPE, "principal")
    assert view is not None and view.hurts == {X} and view.helps == {Y} and not view.scope_hurts
    assert store.reads == 1
    assert recording.reports == [("AgentExperience.MicrosoftAgentFramework", "benefit_refresh", "Refreshed", None)]


def test_one_refresh_runs_per_key_and_principals_never_share_one() -> None:
    store = FakeStore(rows(X, treatment_verified=False))
    store.gate = threading.Event()
    cache = cache_of(store, Clock(T0))
    for _ in range(5):
        assert cache.current(BOUNDS, SCOPE, "principal") is None
    assert cache.current(BOUNDS, SCOPE, "another") is None
    store.gate.set()
    cache.wait()
    assert store.reads == 2
    assert len(cache) == 2


def test_a_fresh_entry_is_not_refreshed_until_the_interval_has_passed() -> None:
    store = FakeStore(rows(X, treatment_verified=False))
    clock = Clock(T0)
    cache = cache_of(store, clock)
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    clock.advance(datetime.timedelta(minutes=9, seconds=59))
    assert cache.current(BOUNDS, SCOPE, "principal") is not None
    cache.wait()
    assert store.reads == 1
    clock.advance(datetime.timedelta(seconds=1))
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    assert store.reads == 2


@pytest.mark.parametrize(
    ("failure", "error_class"),
    [(OSError("down"), "Infrastructure"), (RuntimeError("bug"), "Unexpected"), (TimeoutError(), "Timeout")],
)
def test_a_failed_refresh_keeps_the_previous_view_and_waits_out_the_interval(
    recording: Recording, failure: BaseException, error_class: str
) -> None:
    store = FakeStore(rows(X, treatment_verified=False))
    clock = Clock(T0)
    cache = cache_of(store, clock)
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    store.failure = failure
    clock.advance(datetime.timedelta(minutes=10))
    before = cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    assert before is not None and cache.current(BOUNDS, SCOPE, "principal") == before
    assert store.reads == 2
    # The failure counts as a refresh: the store is not asked again before another interval.
    clock.advance(datetime.timedelta(minutes=5))
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    assert store.reads == 2
    outcome = "OutcomeUnknown" if error_class == "Timeout" else "NotRefreshed"
    assert [report[2:] for report in recording.reports] == [("Refreshed", None), (outcome, error_class)]


def test_a_refresh_past_its_timeout_is_abandoned_and_reported(recording: Recording) -> None:
    store = FakeStore(rows(X, treatment_verified=False))
    store.gate = threading.Event()
    policy = BenefitPolicy(enabled=True, minimum_runs_per_arm=2, refresh_timeout=datetime.timedelta(milliseconds=50))
    cache = cache_of(store, Clock(T0), policy)
    assert cache.current(BOUNDS, SCOPE, "principal") is None
    cache.wait()
    assert cache.current(BOUNDS, SCOPE, "principal") is None
    assert [report[2:] for report in recording.reports] == [("OutcomeUnknown", "Timeout")]
    store.gate.set()


def test_only_assignments_within_the_window_count() -> None:
    old = rows(X, treatment_verified=False, at=Instant.parse("2026-01-01T00:00:00+00:00"))
    store = FakeStore(old + rows(Y, treatment_verified=True, first=100))
    clock = Clock(T0)
    cache = cache_of(store, clock)
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    view = cache.current(BOUNDS, SCOPE, "principal")
    assert view is not None and view.hurts == frozenset() and view.helps == {Y}
    wide = cache_of(
        store, clock, BenefitPolicy(enabled=True, minimum_runs_per_arm=2, window=datetime.timedelta(days=90))
    )
    wide.current(BOUNDS, SCOPE, "principal")
    wide.wait()
    wide_view = wide.current(BOUNDS, SCOPE, "principal")
    assert wide_view is not None and wide_view.hurts == {X}


def test_a_store_that_does_not_read_the_holdout_has_no_effect(recording: Recording) -> None:
    cache = cache_of(object(), Clock(T0))
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    assert cache.current(BOUNDS, SCOPE, "principal") is None
    assert [report[2:] for report in recording.reports] == [("Refreshed", None)]


def test_per_model_reads_the_invoking_models_view() -> None:
    answer = rows(X, treatment_verified=False, run_model="model-a") + rows(
        X, treatment_verified=True, first=100, run_model="model-b"
    )
    policy = BenefitPolicy(enabled=True, minimum_runs_per_arm=2, per_model=True)
    cache = cache_of(FakeStore(answer), Clock(T0), policy)
    cache.current(BOUNDS, SCOPE, "principal")
    cache.wait()
    a = cache.current(BOUNDS, SCOPE, "principal", "model-a")
    b = cache.current(BOUNDS, SCOPE, "principal", "model-b")
    assert a is not None and a.hurts == {X}
    assert b is not None and b.helps == {X}
    # Pooled the lesson has no evidence, so a model the estimate never measured, or none, sees nothing.
    assert cache.current(BOUNDS, SCOPE, "principal", "model-z") is None
    assert cache.current(BOUNDS, SCOPE, "principal", None) is None


def test_the_cache_drops_the_entry_refreshed_longest_ago(monkeypatch: pytest.MonkeyPatch) -> None:
    import agentexperience.benefit_policy as module

    monkeypatch.setattr(module, "MAX_CACHE_ENTRIES", 2)
    clock = Clock(T0)
    cache = cache_of(FakeStore([]), clock)
    for principal in ("first", "second"):
        cache.current(BOUNDS, SCOPE, principal)
        cache.wait()
        clock.advance(datetime.timedelta(seconds=1))
    cache.current(BOUNDS, SCOPE, "third")
    cache.wait()
    assert len(cache) == 2
    assert ("first" not in {key[2] for key in cache._entries}) and "third" in {key[2] for key in cache._entries}


def test_a_cache_that_cannot_answer_has_no_effect() -> None:
    def broken() -> Instant:
        raise RuntimeError("clock")

    assert cache_of(FakeStore([]), broken).current(BOUNDS, SCOPE, "principal") is None


def test_concurrent_reads_of_one_key_start_one_refresh() -> None:
    store = FakeStore(rows(X, treatment_verified=False))
    store.gate = threading.Event()
    cache = cache_of(store, Clock(T0))
    start = threading.Barrier(32)
    answers: list[BenefitView | None] = []
    answers_lock = threading.Lock()

    def read() -> None:
        start.wait(5)
        answer = cache.current(BOUNDS, SCOPE, "principal")
        with answers_lock:
            answers.append(answer)

    threads = [threading.Thread(target=read) for _ in range(32)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(5)
    assert answers == [None] * 32
    store.gate.set()
    cache.wait(5)
    assert store.reads == 1
    view = cache.current(BOUNDS, SCOPE, "principal")
    assert view is not None and view.hurts == frozenset({X})
