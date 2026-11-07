"""
Contract 1.17: every ``reuse-feedback`` fixture through this port's feedback service over its in-memory ledger and
record store (results, the ledger, the records afterwards and their histories), every ``finalize-reuse-evidence``
fixture through finalization and its reuse-evidence step, and what the fixtures do not state.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping
from dataclasses import replace
from typing import Any

import pytest
from _contract import FIXTURES, cases, json_difference, read_json, run_finalize_case
from _lifecycle import (
    apply_request_of,
    apply_result_json,
    authorization_of,
    engine_of,
)
from _lifecycle import (
    thrown_json as apply_thrown_json,
)
from _reuse_feedback import feedback_of, recorded_json, recorded_of, submit_result_json, thrown_json
from test_lifecycle import _whole_history

from agentexperience import (
    AuthorizationBounds,
    AuthorizationContext,
    ExperienceLifecycleService,
    ExperienceReuseFeedback,
    ExperienceReuseFeedbackService,
    ExperienceStoreError,
    HistoryQuery,
    HistoryResult,
    Instant,
    LifecycleCommitResult,
    LifecycleEvent,
    RecordedExperienceReuseFeedback,
    RecordReadResult,
    ReuseMeasure,
    Scope,
    SupersessionCheckResult,
    apply_reuse_evidence,
    from_stored,
    to_stored,
)
from agentexperience.confidence import ConfidenceEngineError, is_engine_failure
from agentexperience.inmemory import InMemoryExperienceReuseFeedbackStore, InMemoryExperienceStore
from agentexperience.observer import AttributeValue, set_observer, type_name
from agentexperience.reuse_feedback import (
    ExperienceReuseFeedbackStoreResult,
    is_store_failure,
    normalize_reuse_feedback,
)

TOLERANCE = 1e-12
FAMILY = "reuse-feedback"
FINALIZE_FAMILY = "finalize-reuse-evidence"

# What the .NET fixtures name a .NET exception type, and this port's type in its place.
DOTNET_TYPES = {type_name(ConfidenceEngineError("rule", 2.0)): "System.InvalidOperationException"}


class _Clock:
    def __init__(self) -> None:
        self.now = Instant(0)

    def __call__(self) -> Instant:
        return self.now


class FaultingLedger:
    """The ledger, made to raise a store failure for one step (``faults.ledger``)."""

    def __init__(self, inner: InMemoryExperienceReuseFeedbackStore) -> None:
        self.inner = inner
        self.fault: str | None = None

    def record(
        self, authorization: AuthorizationContext, feedback: RecordedExperienceReuseFeedback
    ) -> ExperienceReuseFeedbackStoreResult:
        if self.fault is not None:
            raise ExperienceStoreError(self.fault)
        return self.inner.record(authorization, feedback)


class FaultingRecordStore:
    """The record store, made to fail one record's lifecycle commit for one step (``faults.commit``)."""

    def __init__(self, inner: InMemoryExperienceStore) -> None:
        self.inner = inner
        self.faults: Mapping[str, Any] = {}

    def read_record(
        self, authorization: AuthorizationContext, scope: Scope, experience_id: uuid.UUID
    ) -> RecordReadResult:
        return self.inner.read_record(authorization, scope, experience_id)

    def commit_lifecycle_event(
        self, authorization: AuthorizationContext, scope: Scope, event: LifecycleEvent
    ) -> LifecycleCommitResult:
        fault = self.faults.get(str(event.experience_record_id))
        if fault is not None:
            if fault.get("throws") is not None:
                raise ExperienceStoreError(fault["throws"])
            return LifecycleCommitResult(fault["answers"], event.expected_revision + 1, None)
        return self.inner.commit_lifecycle_event(authorization, scope, event)

    def get_history(self, authorization: AuthorizationContext, query: HistoryQuery) -> HistoryResult:
        return self.inner.get_history(authorization, query)

    def check_supersession(
        self, authorization: AuthorizationContext, scope: Scope, experience_id: uuid.UUID, replacement_id: uuid.UUID
    ) -> SupersessionCheckResult:
        return self.inner.check_supersession(authorization, scope, experience_id, replacement_id)


def _seeded_store(case: dict[str, Any], clock: _Clock) -> InMemoryExperienceStore:
    return InMemoryExperienceStore([from_stored(stored) for stored in case["records"]], clock=clock)


def _records_and_histories(store: InMemoryExperienceStore, case: dict[str, Any]) -> tuple[list[Any], list[Any]]:
    held = {record.experience_id: record for record in store.records()}
    records = [to_stored(held[from_stored(stored).experience_id]) for stored in case["records"]]
    return records, [_whole_history(store, stored) for stored in case["records"]]


def _compare(expected: Any, actual: Any, path: str) -> None:
    difference = json_difference(expected, actual, path, TOLERANCE)
    assert difference is None, difference


@pytest.mark.parametrize("name", cases(FAMILY))
def test_a_reuse_feedback_fixture_through_the_in_memory_stores(name: str) -> None:
    directory = FIXTURES / FAMILY / name
    case = read_json(directory / "input.json")
    expected = read_json(directory / "expected.json")
    clock = _Clock()
    inner = _seeded_store(case, clock)
    store = FaultingRecordStore(inner)
    ledger = FaultingLedger(InMemoryExperienceReuseFeedbackStore())
    lifecycle = ExperienceLifecycleService(
        store,
        confidence_engine=engine_of(case["service"]["engine"]),
        host_trusted_evidence=case["service"]["hostTrustedEvidence"],
    )
    service = ExperienceReuseFeedbackService(ledger, lifecycle)

    assert len(expected["results"]) == len(case["steps"])
    for index, (step, outcome) in enumerate(zip(case["steps"], expected["results"], strict=True)):
        clock.now = Instant.parse(step["clock"])
        actual: Any
        if "submit" in step:
            submit = step["submit"]
            faults = submit.get("faults") or {}
            ledger.fault = faults.get("ledger")
            store.faults = faults.get("commit") or {}
            try:
                actual = submit_result_json(
                    service.record(authorization_of(submit["authorization"]), feedback_of(submit["feedback"]))
                )
            except ExperienceStoreError as error:
                actual = thrown_json(error)
            finally:
                ledger.fault = None
                store.faults = {}
        elif "record" in step:
            record = step["record"]
            result = ledger.inner.record(authorization_of(record["authorization"]), recorded_of(record["feedback"]))
            actual = {
                "outcome": result.outcome,
                "feedback": recorded_json(result.feedback),
                "errors": [{"path": error.path, "message": error.message} for error in result.errors],
            }
        elif "apply" in step:
            apply = step["apply"]
            try:
                actual = apply_result_json(
                    lifecycle.apply_evidence(
                        authorization_of(apply["authorization"]), apply_request_of(apply["request"])
                    )
                )
            except Exception as error:
                actual = {"thrown": apply_thrown_json(error)}
        elif "derive" in step:
            derive = step["derive"]
            feedback_id, experience_id = uuid.UUID(derive["feedbackId"]), uuid.UUID(derive["experienceId"])
            actual = {
                "evidenceId": str(ExperienceReuseFeedbackService.evidence_id_for(feedback_id, experience_id)),
                "eventId": str(ExperienceReuseFeedbackService.event_id_for(feedback_id, experience_id)),
            }
        else:
            raise AssertionError(f"Unknown step {sorted(step)}.")
        _compare(outcome, actual, f"$.results[{index}]")

    _compare(expected["ledger"], [recorded_json(item) for item in ledger.inner.submissions()], "$.ledger")
    records, histories = _records_and_histories(inner, case)
    _compare(expected["records"], records, "$.records")
    _compare(expected["histories"], histories, "$.histories")


def _dotnet_names(text: str | None) -> str | None:
    if text is None:
        return None
    for port, dotnet in DOTNET_TYPES.items():
        text = text.replace(port, dotnet)
    return text


@pytest.mark.parametrize("name", cases(FINALIZE_FAMILY))
def test_a_finalize_reuse_evidence_fixture(name: str) -> None:
    directory = FIXTURES / FINALIZE_FAMILY / name
    case = read_json(directory / "input.json")
    expected = read_json(directory / "expected.json")
    finalized, _ = run_finalize_case(case["finalize"])
    clock = _Clock()
    clock.now = Instant.parse(case["finalize"]["verification"]["finalizedAt"])
    store = _seeded_store(case, clock)
    service = ExperienceLifecycleService(
        store,
        confidence_engine=engine_of(case["service"]["engine"]),
        host_trusted_evidence=case["service"]["hostTrustedEvidence"],
    )
    scope = finalized.record.scope
    authorization = AuthorizationContext("contract-principal", AuthorizationBounds.of(scope))
    options = case["finalization"]

    results: list[Any] = []
    for _ in range(case["finalizations"]):
        try:
            store.insert_finalized(authorization.bounds, finalized, actor="contract-principal")
            record = finalized.record
            outcome: str = record.status
        except ValueError:
            # A run already finalized: as .NET's AlreadyFinalized, from the stored record.
            stored = store.get(authorization.bounds, scope, finalized.record.experience_id)
            assert stored is not None
            record, outcome = stored, "AlreadyFinalized"
        report = apply_reuse_evidence(
            service,
            authorization,
            record,
            reuse_evidence=options["reuseEvidence"],
            contradict_on_failure=options["contradictOnFailure"],
        )
        results.append(
            {
                "outcome": outcome,
                "experienceId": str(record.experience_id),
                "reuseEvidence": [
                    {
                        "experienceId": str(entry.experience_id),
                        "kind": entry.kind,
                        "outcome": entry.outcome,
                        "reason": _dotnet_names(entry.reason),
                        "counted": entry.counted,
                        "skipped": entry.skipped,
                        "replay": entry.replay,
                        "refusal": entry.refusal,
                        "exceptionType": _dotnet_names(entry.exception_type),
                    }
                    for entry in report.results
                ],
                "reuseEvidenceTruncated": report.truncated,
            }
        )
    _compare(expected["results"], results, "$.results")
    records, histories = _records_and_histories(store, case)
    _compare(expected["records"], records, "$.records")
    _compare(expected["histories"], histories, "$.histories")


# ---- What the fixtures do not state --------------------------------------------------------------------------------


def _one_record() -> tuple[InMemoryExperienceStore, Any, AuthorizationContext]:
    stored = read_json(FIXTURES / FAMILY / "comparative-improved" / "input.json")["records"][0]
    record = from_stored(stored)
    return InMemoryExperienceStore([record]), record, AuthorizationContext("p", AuthorizationBounds.of(record.scope))


def _exposure_only(record: Any) -> ExperienceReuseFeedback:
    return ExperienceReuseFeedback(
        feedback_id=uuid.UUID(int=0xF1),
        run_id=uuid.UUID(int=0xF2),
        scope=record.scope,
        exposed_experience_ids=[record.experience_id],
        run_outcome="Verified",
        measure=ReuseMeasure("tool-calls", 3),
        observed_at=Instant.parse("2026-03-02T09:00:00+00:00"),
    )


def test_recording_reports_the_reuse_feedback_operation() -> None:
    store, record, authorization = _one_record()
    seen: list[tuple[str, str, Mapping[str, AttributeValue]]] = []

    class Recorder:
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
            seen.append((source, operation, attributes))
            seen[-1] = (source, f"{operation}:{outcome}", dict(attributes))

    service = ExperienceReuseFeedbackService(InMemoryExperienceReuseFeedbackStore(), ExperienceLifecycleService(store))
    set_observer(Recorder())
    try:
        service.record(authorization, _exposure_only(record))
        with pytest.raises(TypeError):
            service.record(authorization, None)  # type: ignore[arg-type]
    finally:
        set_observer(None)
    assert seen[0] == (
        "AgentExperience.Core",
        "reuse_feedback:Recorded",
        {"agentexperience.feedback_id": str(uuid.UUID(int=0xF1)), "agentexperience.run_id": str(uuid.UUID(int=0xF2))},
    )
    assert seen[1][1] == "reuse_feedback:Faulted"


def test_an_unexpected_failure_of_one_records_evidence_propagates() -> None:
    store, _, authorization = _one_record()

    class Broken(FaultingRecordStore):
        def commit_lifecycle_event(
            self, authorization: AuthorizationContext, scope: Scope, event: LifecycleEvent
        ) -> LifecycleCommitResult:
            raise KeyError("a bug, not a store failure")

    feedback = read_json(FIXTURES / FAMILY / "comparative-improved" / "input.json")["steps"][0]["submit"]["feedback"]
    service = ExperienceReuseFeedbackService(
        InMemoryExperienceReuseFeedbackStore(),
        ExperienceLifecycleService(Broken(store), host_trusted_evidence="Counted"),
    )
    with pytest.raises(KeyError):
        service.record(authorization, feedback_of(feedback))


def test_store_and_engine_failures_are_classified() -> None:
    assert is_store_failure(ExperienceStoreError("x"))
    assert is_store_failure(ConnectionResetError())
    assert not is_store_failure(ValueError())
    assert not is_engine_failure(ValueError())
    assert not is_engine_failure(ConfidenceEngineError("rule", 2.0)), "only a score the rule refused is marked"


def test_reuse_evidence_is_off_by_default_and_refuses_an_undefined_mode() -> None:
    store, record, authorization = _one_record()
    service = ExperienceLifecycleService(store)
    assert apply_reuse_evidence(service, authorization, record).results == ()
    with pytest.raises(ValueError):
        apply_reuse_evidence(service, authorization, record, reuse_evidence="Always")  # type: ignore[arg-type]


def test_the_ledger_snapshot_is_independent_of_the_callers_lists() -> None:
    ledger = InMemoryExperienceReuseFeedbackStore()
    case = read_json(FIXTURES / FAMILY / "store-idempotency" / "input.json")
    record = case["steps"][0]["record"]
    submitted = recorded_of(record["feedback"])
    exposures = list(submitted.exposures or ())
    submitted = RecordedExperienceReuseFeedback(**{**submitted.__dict__, "exposures": exposures})
    result = ledger.record(authorization_of(record["authorization"]), submitted)
    exposures.clear()
    assert result.feedback is not None and len(result.feedback.exposures or ()) == 2
    assert len(ledger.submissions()[0].exposures or ()) == 2


def test_the_confidence_apply_a_submission_makes_is_nested_under_reuse_feedback() -> None:
    from opentelemetry.sdk.metrics import MeterProvider
    from opentelemetry.sdk.metrics.export import InMemoryMetricReader
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import SimpleSpanProcessor
    from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

    from agentexperience import telemetry

    case = read_json(FIXTURES / FAMILY / "comparative-improved" / "input.json")
    submit = case["steps"][0]["submit"]
    store = InMemoryExperienceStore([from_stored(stored) for stored in case["records"]])
    lifecycle = ExperienceLifecycleService(store)
    service = ExperienceReuseFeedbackService(InMemoryExperienceReuseFeedbackStore(), lifecycle)

    spans = InMemorySpanExporter()
    tracer_provider = TracerProvider()
    tracer_provider.add_span_processor(SimpleSpanProcessor(spans))
    reader = InMemoryMetricReader()
    telemetry.enable(tracer_provider, MeterProvider(metric_readers=[reader]))
    try:
        result = service.record(authorization_of(submit["authorization"]), feedback_of(submit["feedback"]))
    finally:
        set_observer(None)
    assert [exposure.disposition for exposure in result.exposures] == ["EvidenceApplied", "EvidenceApplied"]

    data = reader.get_metrics_data()
    nested = {
        (dict(point.attributes or {})["operation"], dict(point.attributes or {})["nested"])
        for resource in (data.resource_metrics if data else [])
        for scope in resource.scope_metrics
        for metric in scope.metrics
        if metric.name == "agentexperience.operation.count"
        for point in metric.data.data_points
    }
    # As in .NET: the feedback is what the host asked for; each record's evidence is the library's own call inside it.
    assert nested == {("reuse_feedback", False), ("confidence.apply", True)}

    finished = list(spans.get_finished_spans())
    parent = next(span for span in finished if span.name == "agentexperience.reuse_feedback")
    children = [span for span in finished if span.name == "agentexperience.confidence.apply"]
    assert len(children) == 2
    assert all(span.parent is not None and span.parent.span_id == parent.context.span_id for span in children)


def test_exposures_are_ordered_as_dotnet_orders_guids_unsigned_group_by_group() -> None:
    ids = [
        uuid.UUID(text)
        for text in (
            "7fffffff-0000-4000-8000-000000000001",
            "00001170-0001-4000-8000-ffffffffffff",
            "ffffffff-0000-4000-8000-000000000001",
            "00001170-ffff-4000-8000-000000000001",
            "80000000-0000-4000-8000-000000000001",
            "00001170-0001-4000-ffff-000000000001",
            "00001170-0001-ffff-8000-000000000001",
            "00001170-0001-4000-8000-000000000001",
        )
    ]
    expected = [
        "00001170-0001-4000-8000-000000000001",
        "00001170-0001-4000-8000-ffffffffffff",
        "00001170-0001-4000-ffff-000000000001",
        "00001170-0001-ffff-8000-000000000001",
        "00001170-ffff-4000-8000-000000000001",
        "7fffffff-0000-4000-8000-000000000001",
        "80000000-0000-4000-8000-000000000001",
        "ffffffff-0000-4000-8000-000000000001",
    ]
    step = next(
        step["record"]
        for step in read_json(FIXTURES / FAMILY / "exposure-order" / "input.json")["steps"]
        if "record" in step
    )
    submitted = recorded_of(step["feedback"])
    by_id = {exposure.experience_id: exposure for exposure in submitted.exposures or () if exposure is not None}
    scrambled = replace(submitted, exposures=tuple(by_id[id] for id in ids))

    def order(feedback: Any) -> list[str]:
        return [str(exposure.experience_id) for exposure in feedback.exposures]

    assert order(normalize_reuse_feedback(scrambled)) == expected
    ledger = InMemoryExperienceReuseFeedbackStore()
    recorded = ledger.record(authorization_of(step["authorization"]), scrambled)
    assert recorded.outcome == "Recorded"
    assert order(recorded.feedback) == expected
    again = ledger.record(
        authorization_of(step["authorization"]),
        replace(scrambled, exposures=tuple(reversed(scrambled.exposures or ()))),
    )
    assert again.outcome == "AlreadyRecorded"
