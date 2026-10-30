"""
Story 23.5: port telemetry. The hook the core and the agent integration call, the OpenTelemetry adapter behind the
``otel`` extra, and the pin of every name against ``docs/telemetry-names.json`` (generated from the .NET definitions).
"""

from __future__ import annotations

import asyncio
import json
import subprocess
import sys
import uuid
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import pytest

pytest.importorskip("agent_framework")
pytest.importorskip("opentelemetry.sdk")

from _maf import NEXT_TICKET, TICKET, ScriptedClient
from _sync import run
from agent_framework import Agent, ContextProvider, tool
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import InMemoryMetricReader
from opentelemetry.sdk.trace import ReadableSpan, TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

from agentexperience import BenefitPolicy, Instant, observer, telemetry
from agentexperience.capture import RunCapture
from agentexperience.finalize import finalize
from agentexperience.inmemory import InMemoryExperienceStore
from agentexperience.maf import (
    ExperienceContextProvider,
    ExperienceHoldoutContextProvider,
    HoldoutOptions,
    HoldoutRecordResult,
    InjectionRecordResult,
    ProviderHoldoutRecordResult,
    development_identity,
    exit_code,
    tool_succeeded,
    use_agent_experience,
    use_measured_memory,
)
from agentexperience.records import EnvironmentFingerprint, Provenance, Scope

ROOT = Path(__file__).resolve().parents[2]
NAMES = json.loads((ROOT / "docs" / "telemetry-names.json").read_text(encoding="utf-8"))
OPERATIONS = {item["operation"]: item for item in NAMES["operations"]}
ENVIRONMENT = EnvironmentFingerprint("test-host", "python", "any", "1.0.0", {})
MARKER = "SECRET-MARKER-7f3a"


@pytest.fixture(autouse=True)
def _no_observer() -> Iterator[None]:
    observer.set_observer(None)
    yield
    observer.set_observer(None)


Point = tuple[str, str, str, dict[str, Any], Any]


def attrs(span: ReadableSpan) -> dict[str, Any]:
    return dict(span.attributes or {})


def parent_id(span: ReadableSpan) -> int | None:
    return span.parent.span_id if span.parent is not None else None


class Telemetry:
    """The OpenTelemetry SDK's in-memory exporters, with the adapter registered."""

    def __init__(self) -> None:
        self.spans = InMemorySpanExporter()
        tracer_provider = TracerProvider()
        tracer_provider.add_span_processor(SimpleSpanProcessor(self.spans))
        self.reader = InMemoryMetricReader()
        meter_provider = MeterProvider(metric_readers=[self.reader])
        telemetry.enable(tracer_provider, meter_provider)

    def finished(self) -> list[ReadableSpan]:
        return list(self.spans.get_finished_spans())

    def points(self) -> list[Point]:
        """(meter, instrument name, unit, attributes, point) for every data point."""
        found: list[Point] = []
        data = self.reader.get_metrics_data()
        for resource in data.resource_metrics if data else []:
            for scope in resource.scope_metrics:
                for metric in scope.metrics:
                    for point in metric.data.data_points:
                        found.append(
                            (scope.scope.name, metric.name, metric.unit or "", dict(point.attributes or {}), point)
                        )
        return found


def counts(sink: Telemetry, operation: str) -> list[Point]:
    return [p for p in sink.points() if p[1] == "agentexperience.operation.count" and p[3]["operation"] == operation]


class Embedder:
    model_id = "telemetry-test"
    dimension = 4

    def embed(self, text: str) -> list[float]:
        return [1.0, 0.0, 0.0, 0.0]


def exits(*values: str) -> Any:
    queue = list(values)

    def run_tests() -> str:
        """Runs the tests."""
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return run_tests


def learning_agent(store: Any, recorded: list[InjectionRecordResult] | None = None, **options: Any) -> Agent:
    agent = Agent(
        client=ScriptedClient(),
        name="triage-ticket",
        tools=[tool(exits("exit=3", "exit=0"), name="run_tests")],
    )
    return use_agent_experience(
        agent,
        store=store,
        identity=development_identity(application="tests"),
        environment=ENVIRONMENT,
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        on_injection_recorded=(recorded.append if recorded is not None else None),
        **options,
    )


class Memory(ContextProvider):
    """Another memory provider (contract 1.11): adds one instruction, which the task text never reads."""

    def __init__(self) -> None:
        super().__init__("telemetry-memory")

    async def before_run(self, *, agent: Any, session: Any, context: Any, state: dict[str, Any]) -> None:
        context.extend_instructions(self.source_id, "Remember: the lock is held by the nightly job.")


def drive(agent: Agent, first: str = TICKET, second: str = NEXT_TICKET) -> None:
    async def both() -> None:
        await agent.run(first)
        await agent.run(second)
        (provider,) = [item for item in agent.context_providers if isinstance(item, ExperienceContextProvider)]
        await provider.wait_for_injection_records()
        await provider.wait_for_index_writes()
        await provider.wait_for_benefit_refreshes()
        for item in agent.context_providers:
            if isinstance(item, ExperienceHoldoutContextProvider):
                await item.wait_for_records()

    run(both())


# ---- The names -----------------------------------------------------------------------------------------------------


def test_the_ports_table_of_names_is_in_the_net_generated_file() -> None:
    assert {observer.CORE_SOURCE, observer.MAF_SOURCE} == set(NAMES["sources"])
    assert telemetry.SPAN_NAME_PREFIX == NAMES["spanNamePrefix"]
    assert observer.FAULTED == NAMES["faultedOutcome"]
    assert list(observer.ERROR_CLASSES) == NAMES["errorClasses"]
    assert list(telemetry.DIMENSIONS) == NAMES["dimensions"]
    assert [
        {"name": name, "kind": kind, "unit": unit} for name, (kind, unit) in telemetry.INSTRUMENTS.items()
    ] == NAMES["instruments"]
    # Every span attribute the adapter can write is a documented one.
    assert telemetry.SPAN_ATTRIBUTES <= set(NAMES["attributes"])
    assert set(observer.ATTRIBUTES) <= set(NAMES["attributes"])
    for name, spec in observer.OPERATIONS.items():
        pinned = OPERATIONS[name]
        assert spec.source == pinned["source"], name
        assert spec.span == pinned["span"], name
        assert set(spec.outcomes) <= set(pinned["outcomes"]), name
    assert set(observer.NEVER_NESTED) <= set(observer.OPERATIONS)


def test_the_port_reports_no_operation_it_does_not_perform() -> None:
    forbidden = {
        "retention.sweep",
        "delete",
        "grant.purge",
        "grant.access.purge",
        "record.seal",
        "record.authorship.backfill",
        "reuse_feedback",
        "deindex",
        "reindex",
    }
    assert not forbidden & set(observer.OPERATIONS)
    assert set(observer.OPERATIONS) == {
        "capture.start_run",
        "capture.append_attempt",
        "capture.complete_run",
        "verify",
        "reflect",
        "finalize",
        "retrieve",
        "index",
        "lifecycle.commit",
        "confidence.apply",
        "inject",
        "record_injection",
        "record_holdout",
        "record_provider_holdout",
        "benefit_refresh",
    }


def test_neither_the_core_nor_the_agent_integration_imports_opentelemetry() -> None:
    package = Path(telemetry.__file__).parent
    for source in package.glob("*.py"):
        if source.name == "telemetry.py":
            continue
        assert "opentelemetry" not in source.read_text(encoding="utf-8"), source.name
    code = (
        "import sys\n"
        "sys.modules['opentelemetry'] = None\n"
        "import agentexperience, agentexperience.maf, agentexperience.inmemory, agentexperience.observer\n"
        "assert not any(name.startswith('opentelemetry') and sys.modules[name] for name in sys.modules)\n"
    )
    done = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True, check=False)
    assert done.returncode == 0, done.stderr


# ---- What an operator receives -------------------------------------------------------------------------------------


def test_an_agent_that_injects_captures_and_records_an_injection_emits_exactly_the_pinned_names() -> None:
    sink = Telemetry()
    recorded: list[InjectionRecordResult] = []
    held: list[HoldoutRecordResult] = []
    counter = iter(range(1, 1000))
    agent = learning_agent(
        InMemoryExperienceStore(),
        recorded,
        embedder=Embedder(),
        # Sequential IDs: the second run is 00000000-0000-4000-8000-000000000007, in the treatment arm at 0.25.
        new_id=lambda: uuid.UUID(f"00000000-0000-4000-8000-{next(counter):012d}"),
        holdout=HoldoutOptions(rate=0.25),
        on_holdout_recorded=held.append,
        # Contract 1.10: the benefit policy on, so its background refresh reports benefit_refresh (with no evidence it
        # changes nothing).
        benefit=BenefitPolicy(enabled=True),
    )
    # Contract 1.11: another memory provider measured too, so record_provider_holdout is reported.
    measured: list[ProviderHoldoutRecordResult] = []
    use_measured_memory(agent, Memory(), "stub-memory", on_recorded=measured.append)
    drive(agent, TICKET, NEXT_TICKET)
    assert [item.outcome for item in recorded] == ["Recorded"]
    assert [(item.outcome, item.assignment.arm) for item in held] == [("Recorded", "Treatment")]
    assert [item.outcome for item in measured] == ["Recorded", "Recorded"]

    spans = sink.finished()
    names = {span.name for span in spans}
    assert {
        "agentexperience.inject",
        "agentexperience.retrieve",
        "agentexperience.capture.start_run",
        "agentexperience.capture.append_attempt",
        "agentexperience.capture.complete_run",
        "agentexperience.verify",
        "agentexperience.reflect",
        "agentexperience.finalize",
        "agentexperience.index",
    } <= names
    for span in spans:
        attributes = attrs(span)
        operation = attributes["agentexperience.operation"]
        assert span.name == NAMES["spanNamePrefix"] + operation
        assert OPERATIONS[operation]["span"] is True
        assert attributes["agentexperience.outcome"] in OPERATIONS[operation]["outcomes"] + ["Faulted"]
        assert set(attributes) <= set(NAMES["attributes"])
        assert span.status.status_code.name == "OK"
        assert span.kind.name == "INTERNAL"
    assert "agentexperience.record_injection" not in names  # measurements only, no span

    points = sink.points()
    assert points
    instruments = {item["name"]: item["unit"] for item in NAMES["instruments"]}
    sources = set(NAMES["sources"])
    seen: set[str] = set()
    for meter, name, unit, attributes, _ in points:
        assert meter in sources
        assert instruments[name] == unit
        assert set(attributes) <= {"operation", "outcome", "nested"}, "no failure happened, so no error.class"
        operation = attributes["operation"]
        assert meter == OPERATIONS[operation]["source"]
        assert attributes["outcome"] in OPERATIONS[operation]["outcomes"]
        assert isinstance(attributes["nested"], bool)
        seen.add(operation)
    # An agent never moves a stored lesson's lifecycle (contract 1.16): the host calls the lifecycle service.
    assert seen == set(observer.OPERATIONS) - {"lifecycle.commit", "confidence.apply"}
    assert {name for _, name, _, _, _ in points} == {
        "agentexperience.operation.count",
        "agentexperience.operation.duration",
    }

    # The retrieval inside an injection is the Core source's, not nested; verify and reflect inside finalize are.
    nested = {(attributes["operation"], attributes["nested"]) for _, _, _, attributes, _ in points}
    assert ("retrieve", False) in nested and ("inject", False) in nested and ("record_injection", False) in nested
    assert ("record_holdout", False) in nested
    assert ("record_provider_holdout", False) in nested
    assert ("benefit_refresh", False) in nested
    assert ("verify", True) in nested and ("reflect", True) in nested and ("finalize", False) in nested
    assert ("capture.append_attempt", False) in nested

    # The nested spans are children of the span that ran them.
    by_name = {span.name: span for span in spans}
    finalize_span = by_name["agentexperience.finalize"]
    assert parent_id(by_name["agentexperience.verify"]) == finalize_span.context.span_id
    assert parent_id(by_name["agentexperience.reflect"]) == finalize_span.context.span_id
    assert attrs(finalize_span)["agentexperience.stage"] == "CreateRecord"
    assert parent_id(by_name["agentexperience.retrieve"]) == by_name["agentexperience.inject"].context.span_id


def test_inject_is_nothing_to_inject_the_first_time_and_injected_the_second() -> None:
    sink = Telemetry()
    drive(learning_agent(InMemoryExperienceStore()))
    outcomes = [
        attrs(span)["agentexperience.outcome"] for span in sink.finished() if span.name == "agentexperience.inject"
    ]
    assert outcomes == ["NothingToInject", "Injected"]
    assert [
        attrs(span)["agentexperience.outcome"] for span in sink.finished() if span.name == "agentexperience.retrieve"
    ] == ["Completed", "Completed"]


def test_a_failing_search_is_retrieval_failed_and_reaches_no_failure_counter_because_nothing_threw() -> None:
    class Broken(InMemoryExperienceStore):
        def search(self, bounds: Any, parameters: Any) -> Any:
            raise ConnectionError(f"the database is down {MARKER}")

    sink = Telemetry()
    drive(learning_agent(Broken()))
    inject = [span for span in sink.finished() if span.name == "agentexperience.inject"]
    assert [attrs(span)["agentexperience.outcome"] for span in inject] == ["RetrievalFailed"] * 2
    retrieve = [span for span in sink.finished() if span.name == "agentexperience.retrieve"]
    assert [attrs(span)["agentexperience.outcome"] for span in retrieve] == ["Failed"] * 2
    assert not [point for point in sink.points() if point[1] == "agentexperience.operation.failures"]


def test_an_operation_that_raises_is_faulted_with_its_error_class_and_type_name_only() -> None:
    sink = Telemetry()
    scope = Scope("tenant", "app", "project")
    capture = RunCapture.start(
        uuid.uuid4(), "task", None, scope, ENVIRONMENT, Provenance("test", "1", Instant.now(), None), Instant.now()
    )
    with pytest.raises(ValueError):
        finalize(
            capture.run,  # not finished
            closed_round=None,
            required_checks=[],
            evidence=[],
            current_artifact_revision=f"revision {MARKER}",
            finalized_at=Instant.now(),
        )

    (span,) = [item for item in sink.finished() if item.name == "agentexperience.finalize"]
    assert attrs(span)["agentexperience.outcome"] == "Faulted"
    assert attrs(span)["agentexperience.error.class"] == "Unexpected"
    assert attrs(span)["error.type"] == "ValueError"
    assert attrs(span)["agentexperience.stage"] == "Load"
    assert span.status.status_code.name == "ERROR"
    assert not span.events
    (failure,) = [point for point in sink.points() if point[1] == "agentexperience.operation.failures"]
    assert dict(failure[3]) == {"operation": "finalize", "error.class": "Unexpected", "nested": False}
    assert failure[4].value == 1
    counted = [
        p for p in sink.points() if p[1] == "agentexperience.operation.count" and p[3]["operation"] == "finalize"
    ]
    assert [dict(p[3])["outcome"] for p in counted] == ["Faulted"]


def test_record_injection_failure_classes_follow_the_net_table() -> None:
    class Broken(InMemoryExperienceStore):
        def record_injection(self, bounds: Any, scope: Any, experience_ids: Any, injected_at: Any) -> None:
            raise ConnectionError("down")

    class SearchOnly:
        def __init__(self) -> None:
            self.inner = InMemoryExperienceStore()

        def insert_finalized(self, bounds: Any, finalized: Any, *, actor: str) -> None:
            self.inner.insert_finalized(bounds, finalized, actor=actor)

        def search(self, bounds: Any, parameters: Any) -> Any:
            return self.inner.search(bounds, parameters)

    sink = Telemetry()
    drive(learning_agent(Broken()))
    drive(learning_agent(SearchOnly()))
    counted = sorted(p[3]["outcome"] for p in counts(sink, "record_injection"))
    assert counted == ["NotRecorded", "NotTracked"]
    (failure,) = [p for p in sink.points() if p[1] == "agentexperience.operation.failures"]
    assert dict(failure[3]) == {"operation": "record_injection", "error.class": "Infrastructure", "nested": False}


def test_nothing_of_the_run_reaches_a_span_or_a_metric_dimension() -> None:
    sink = Telemetry()
    drive(learning_agent(InMemoryExperienceStore()), f"Triage {MARKER} please", f"Triage {MARKER} again")
    texts = [str(value) for span in sink.finished() for value in attrs(span).values()]
    texts += [str(value) for _, _, _, attributes, _ in sink.points() for value in attributes.values()]
    texts += [span.name for span in sink.finished()]
    assert texts
    assert not [text for text in texts if MARKER in text]


# ---- Telemetry never changes a result ------------------------------------------------------------------------------


class RaisingObserver:
    def __init__(self) -> None:
        self.calls = 0

    def start(self, source: str, operation: str) -> object | None:
        self.calls += 1
        raise RuntimeError("start")

    def finish(self, *_: Any, **__: Any) -> None:
        self.calls += 1
        raise RuntimeError("finish")


class HalfRaisingObserver:
    def start(self, source: str, operation: str) -> object | None:
        return object()

    def finish(self, *_: Any, **__: Any) -> None:
        raise RuntimeError("finish")


@pytest.mark.parametrize("hook", [RaisingObserver, HalfRaisingObserver])
def test_an_observer_that_raises_changes_nothing(hook: type) -> None:
    def outcome() -> tuple[list[Any], list[str]]:
        store = InMemoryExperienceStore()
        recorded: list[InjectionRecordResult] = []
        agent = learning_agent(store, recorded, embedder=Embedder())
        injected: list[Any] = []
        agent.context_providers[-1].settings.on_injected = injected.append  # type: ignore[attr-defined]
        drive(agent)
        return [(item.byte_count, len(item.experience_ids)) for item in injected], [item.outcome for item in recorded]

    expected = outcome()
    observed = hook()
    observer.set_observer(observed)
    assert outcome() == expected
    if isinstance(observed, RaisingObserver):
        assert observed.calls > 0


def test_with_no_observer_the_hook_allocates_nothing_and_yields_none() -> None:
    assert observer.get_observer() is None
    first = observer.observe("verify")
    second = observer.observe("finalize")
    assert first is second  # one shared no-op
    with first as op:
        assert op is None


def test_classification_mirrors_net() -> None:
    assert observer.classify(asyncio.CancelledError()) == "Cancelled"
    assert observer.classify(TimeoutError()) == "Timeout"
    assert observer.classify(asyncio.TimeoutError()) == "Timeout"
    assert observer.classify(ConnectionError()) == "Infrastructure"
    assert observer.classify(KeyError("x")) == "Unexpected"
    assert observer.type_name(ValueError("secret")) == "ValueError"


def test_a_cancelled_operation_is_faulted_and_cancelled() -> None:
    sink = Telemetry()

    async def cancelled() -> None:
        with observer.observe("inject"):
            raise asyncio.CancelledError

    with pytest.raises(asyncio.CancelledError):
        run(cancelled())
    (failure,) = [p for p in sink.points() if p[1] == "agentexperience.operation.failures"]
    assert dict(failure[3]) == {"operation": "inject", "error.class": "Cancelled", "nested": False}


def test_a_screening_refusal_is_on_the_finalize_span_without_the_text() -> None:
    # Contract 1.14: a model-authored reflection screening refuses names its code on the span, never its text.
    from _contract import FIXTURES, capture_finalize_case, finalize_arguments, read_json

    from agentexperience.reflector import DeterministicReflector, ReflectionRequest

    class ModelReflector:
        def reflect(self, request: ReflectionRequest) -> Any:
            from dataclasses import replace

            reflection = DeterministicReflector().reflect(request)
            return replace(reflection, authorship="Model", lesson=f"See https://{MARKER}.example/now")

    sink = Telemetry()
    case = read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    finalized = finalize(capture_finalize_case(case)[0], **finalize_arguments(case), reflector=ModelReflector())
    assert finalized.screening_refusal_code == "UnsafeContent"
    (span,) = [item for item in sink.finished() if item.name == "agentexperience.finalize"]
    assert attrs(span)["agentexperience.reflection.screening_refusal"] == "UnsafeContent"
    assert attrs(span)["agentexperience.outcome"] == "Quarantined"
    assert all(MARKER.lower() not in str(value).lower() for value in attrs(span).values())
