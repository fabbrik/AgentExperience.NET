"""
Contract 1.16: every ``lifecycle-commit`` and ``lifecycle-apply`` fixture through this port's lifecycle service over its
in-memory store (results, the records afterwards in their stored form, and every record's whole history), and the
in-memory store's conflict semantics under real concurrency.
"""

from __future__ import annotations

import threading
import uuid
from collections.abc import Callable, Mapping
from dataclasses import replace
from typing import Any

import pytest
from _contract import FIXTURES, cases, json_difference, read_json, scope_of
from _lifecycle import (
    apply_request_of,
    apply_result_json,
    authorization_of,
    commit_request_of,
    commit_result_json,
    engine_of,
    history_json,
    history_query_of,
    read_confidence_json,
    supersession_json,
    thrown_json,
)

from agentexperience import (
    AuthorizationBounds,
    AuthorizationContext,
    CommitLifecycleTransitionRequest,
    ExperienceLifecycleService,
    HistoryQuery,
    Instant,
    from_stored,
    reuse_event_id_for,
    reuse_evidence_id_for,
    to_stored,
)
from agentexperience.inmemory import InMemoryExperienceStore
from agentexperience.observer import AttributeValue, set_observer

TOLERANCE = 1e-12


class _Clock:
    def __init__(self) -> None:
        self.now = Instant(0)

    def __call__(self) -> Instant:
        return self.now


def _run_step(
    service: ExperienceLifecycleService, store: InMemoryExperienceStore, step: dict[str, Any]
) -> dict[str, Any]:
    if "commit" in step:
        commit = step["commit"]
        return commit_result_json(
            service.commit(authorization_of(commit["authorization"]), commit_request_of(commit["request"]))
        )
    if "apply" in step:
        apply = step["apply"]
        try:
            result = service.apply_evidence(
                authorization_of(apply["authorization"]), apply_request_of(apply["request"])
            )
        except Exception as error:
            return {"thrown": thrown_json(error)}
        return apply_result_json(result)
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
    if "history" in step:
        history = step["history"]
        query = history_query_of(history["query"])
        return history_json(store.get_history(authorization_of(history["authorization"]), query), query.scope)
    if "readConfidence" in step:
        read = step["readConfidence"]
        return read_confidence_json(
            service.read_confidence(
                authorization_of(read["authorization"]),
                scope_of(read["scope"]),
                uuid.UUID(read["experienceId"]),
                read["filter"],
            )
        )
    if "derive" in step:
        derive = step["derive"]
        arguments = (
            uuid.UUID(derive["runId"]),
            scope_of(derive["scope"]),
            uuid.UUID(derive["experienceId"]),
            derive["kind"],
        )
        return {
            "evidenceId": str(reuse_evidence_id_for(*arguments)),
            "eventId": str(reuse_event_id_for(*arguments)),
        }
    raise AssertionError(f"Unknown step {sorted(step)}.")


def _whole_history(store: InMemoryExperienceStore, stored: dict[str, Any]) -> dict[str, Any]:
    record = from_stored(stored)
    authorization = AuthorizationContext("contract-reader", AuthorizationBounds.of(record.scope))
    events: list[dict[str, Any]] = []
    after: int | None = None
    while True:
        page = store.get_history(authorization, HistoryQuery(record.scope, record.experience_id, 500, after))
        assert page.outcome == "Found", page
        events.extend(history_json(page, record.scope)["events"])
        if page.next_start_after_revision is None:
            break
        after = page.next_start_after_revision
    return {"experienceId": str(record.experience_id), "events": events}


@pytest.mark.parametrize(
    ("family", "name"), [(family, name) for family in ("lifecycle-commit", "lifecycle-apply") for name in cases(family)]
)
def test_a_lifecycle_fixture_through_the_in_memory_store(family: str, name: str) -> None:
    directory = FIXTURES / family / name
    case = read_json(directory / "input.json")
    expected = read_json(directory / "expected.json")
    clock = _Clock()
    store = InMemoryExperienceStore([from_stored(stored) for stored in case["records"]], clock=clock)
    service = ExperienceLifecycleService(
        store,
        confidence_engine=engine_of(case["service"]["engine"]),
        host_trusted_evidence=case["service"]["hostTrustedEvidence"],
    )
    assert len(expected["results"]) == len(case["steps"])
    for index, (step, outcome) in enumerate(zip(case["steps"], expected["results"], strict=True)):
        clock.now = Instant.parse(step["clock"])
        actual = _run_step(service, store, step)
        difference = json_difference(outcome, actual, f"$.results[{index}]", TOLERANCE)
        assert difference is None, difference

    held = {record.experience_id: record for record in store.records()}
    records = [to_stored(held[from_stored(stored).experience_id]) for stored in case["records"]]
    difference = json_difference(expected["records"], records, "$.records", TOLERANCE)
    assert difference is None, difference

    histories = [_whole_history(store, stored) for stored in case["records"]]
    difference = json_difference(expected["histories"], histories, "$.histories", TOLERANCE)
    assert difference is None, difference


# ---- Real concurrency ----------------------------------------------------------------------------------------------


def _seed() -> tuple[InMemoryExperienceStore, Any, AuthorizationContext]:
    stored = read_json(FIXTURES / "lifecycle-commit" / "stale-revision" / "input.json")["records"][0]
    record = from_stored(stored)
    store = InMemoryExperienceStore([record], clock=lambda: Instant.parse("2026-03-02T10:00:00+00:00"))
    return store, record, AuthorizationContext("racer", AuthorizationBounds.of(record.scope))


def _race(count: int, attempt: Callable[[int], Any]) -> list[Any]:
    barrier = threading.Barrier(count)
    results: list[Any] = [None] * count
    failures: list[BaseException] = []

    def run(index: int) -> None:
        try:
            barrier.wait()
            results[index] = attempt(index)
        except BaseException as failure:  # pragma: no cover - reported below
            failures.append(failure)

    threads = [threading.Thread(target=run, args=(index,)) for index in range(count)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert not failures, failures
    return results


def test_writers_racing_at_one_revision_commit_exactly_once() -> None:
    store, record, authorization = _seed()
    assert record.status == "Validated"
    service = ExperienceLifecycleService(store)
    targets = ["Reinforced", "Contested", "Stale", "Revoked"] * 4

    def attempt(index: int) -> Any:
        return service.commit(
            authorization,
            CommitLifecycleTransitionRequest(
                event_id=uuid.UUID(int=0x1000 + index),
                experience_id=record.experience_id,
                scope=record.scope,
                prior_status="Validated",
                current_status=targets[index],
                reason=f"racer {index}",
                producer="race/1.0",
                occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
                expected_revision=record.revision,
            ),
        )

    results = _race(len(targets), attempt)
    winners = [index for index, result in enumerate(results) if result.outcome == "Committed"]
    assert len(winners) == 1, [result.outcome for result in results]
    for index, result in enumerate(results):
        if index != winners[0]:
            assert (result.outcome, result.revision) == ("StaleRevision", record.revision + 1)

    history = store.get_history(authorization, HistoryQuery(record.scope, record.experience_id))
    assert history.revision == record.revision + 1
    assert [stored.event.event_id for stored in history.events] == [uuid.UUID(int=0x1000 + winners[0])]
    assert history.events[0].applied_revision == record.revision + 1
    held = next(item for item in store.records() if item.experience_id == record.experience_id)
    assert (held.status, held.revision) == (targets[winners[0]], record.revision + 1)


def test_evidence_racing_for_one_key_is_counted_exactly_once() -> None:
    store, record, authorization = _seed()
    service = ExperienceLifecycleService(store)
    run_id, round_id = uuid.UUID(int=0x2000), uuid.UUID(int=0x2001)
    count = 12

    def attempt(index: int) -> Any:
        from agentexperience import ApplyConfidenceEvidenceRequest

        return service.apply_evidence(
            authorization,
            ApplyConfidenceEvidenceRequest(
                event_id=uuid.UUID(int=0x3000 + index),
                experience_id=record.experience_id,
                scope=record.scope,
                evidence_id=uuid.UUID(int=0x4000 + index),
                kind="Supporting",
                source="Machine",
                run_id=run_id,
                verification_round_id=round_id,
                reason="reused",
                producer="race/1.0",
                occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
            ),
        )

    results = _race(count, attempt)
    counted = [result for result in results if result.outcome == "Applied" and result.counted]
    assert len(counted) == 1, [(result.outcome, result.counted) for result in results]
    # Every loser either lost the revision or found the key counted: never a second count.
    assert all(result.outcome in ("Applied", "StaleRevision") for result in results)
    held = next(item for item in store.records() if item.experience_id == record.experience_id)
    assert (held.supporting_validations, held.revision) == (record.supporting_validations + 1, record.revision + 1)


# ---- What the fixtures do not state --------------------------------------------------------------------------------


class _Recorder:
    def __init__(self) -> None:
        self.finished: list[tuple[str, str, dict[str, AttributeValue]]] = []

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
        attributes: Mapping[str, AttributeValue],
    ) -> None:
        self.finished.append((operation, outcome, dict(attributes)))


def test_commit_and_apply_report_their_operations() -> None:
    store, record, authorization = _seed()
    service = ExperienceLifecycleService(store)
    recorder = _Recorder()
    set_observer(recorder)
    try:
        from agentexperience import ApplyConfidenceEvidenceRequest

        request = ApplyConfidenceEvidenceRequest(
            event_id=uuid.UUID(int=0x5001),
            experience_id=record.experience_id,
            scope=record.scope,
            evidence_id=uuid.UUID(int=0x5002),
            kind="Supporting",
            source="Machine",
            run_id=uuid.UUID(int=0x5003),
            verification_round_id=uuid.UUID(int=0x5004),
            reason="reused",
            producer="telemetry/1.0",
            occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
        )
        assert service.apply_evidence(authorization, request).outcome == "Applied"
        own_run = replace(request, event_id=uuid.UUID(int=0x5005), run_id=record.source_run_id)
        assert service.apply_evidence(authorization, own_run).outcome == "Unverified"
        commit = CommitLifecycleTransitionRequest(
            event_id=uuid.UUID(int=0x5006),
            experience_id=record.experience_id,
            scope=record.scope,
            prior_status="Revoked",
            current_status="Revoked",
            reason="no",
            producer="telemetry/1.0",
            occurred_at=Instant.parse("2026-03-02T09:00:00+00:00"),
            expected_revision=0,
        )
        assert service.commit(authorization, commit).outcome == "TransitionNotAllowed"
    finally:
        set_observer(None)
    ids = {"agentexperience.experience_id": str(record.experience_id)}
    assert recorder.finished == [
        (
            "confidence.apply",
            "Applied",
            {
                **ids,
                "agentexperience.event_id": str(uuid.UUID(int=0x5001)),
                "agentexperience.confidence.admission": "HostTrusted",
            },
        ),
        (
            "confidence.apply",
            "Unverified",
            {
                **ids,
                "agentexperience.event_id": str(uuid.UUID(int=0x5005)),
                "agentexperience.independence.refusal": "OwnRun",
            },
        ),
        ("lifecycle.commit", "TransitionNotAllowed", {**ids, "agentexperience.event_id": str(uuid.UUID(int=0x5006))}),
    ]


def test_a_finalized_record_has_its_initial_event_in_its_history() -> None:
    from _contract import run_finalize_case

    finalized, _ = run_finalize_case(read_json(FIXTURES / "finalize" / cases("finalize")[0] / "input.json"))
    clock = Instant.parse("2026-03-02T10:00:00.1234567+00:00")
    store = InMemoryExperienceStore(clock=lambda: clock)
    bounds = AuthorizationBounds.of(finalized.record.scope)
    store.insert_finalized(bounds, finalized, actor="finalizer")
    history = store.get_history(
        AuthorizationContext("reader", bounds), HistoryQuery(finalized.record.scope, finalized.record.experience_id)
    )
    assert history.outcome == "Found"
    assert [(stored.event.event_id, stored.applied_revision, stored.actor) for stored in history.events] == [
        (finalized.event.event_id, 1, "finalizer")
    ]
    assert history.events[0].recorded_at == clock.truncate_to_microseconds()
    assert history.events[0].event.prior_status == finalized.event.prior_status
    assert history.events[0].event.current_status == finalized.event.current_status


def test_an_undefined_host_trusted_evidence_effect_is_refused() -> None:
    from agentexperience import ConfidenceArgumentError

    with pytest.raises(ConfidenceArgumentError) as refused:
        ExperienceLifecycleService(InMemoryExperienceStore(), host_trusted_evidence="Sometimes")  # type: ignore[arg-type]
    assert (refused.value.exception, refused.value.parameter) == ("ArgumentException", "independence")
