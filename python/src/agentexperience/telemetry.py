"""
OpenTelemetry for AgentExperience (the ``otel`` extra, which depends on ``opentelemetry-api`` only).

.. code-block:: python

    from agentexperience import telemetry

    telemetry.enable()  # uses the global tracer and meter providers your application configured

This turns the library's observer hook (:mod:`agentexperience.observer`) into the spans and metrics that
``docs/telemetry.md`` documents for .NET, under the same source and meter names (``AgentExperience.Core`` and
``AgentExperience.MicrosoftAgentFramework``), span names, instruments, dimensions and attributes, for the operations
this port performs. It configures no SDK and no exporter: the host does, exactly as it does for the .NET library.

A failure in a provider, exporter or this adapter is swallowed and never reaches the agent. Attributes never carry
record content, task text, scopes or exception messages; a failing operation records its exception's type name only.
"""

from __future__ import annotations

import time
from collections.abc import Mapping
from contextvars import ContextVar
from typing import Any

try:
    from opentelemetry import context as otel_context
    from opentelemetry import metrics, trace
    from opentelemetry.metrics import Counter, Histogram, MeterProvider
    from opentelemetry.trace import Span, SpanKind, Status, StatusCode, Tracer, TracerProvider
except ImportError as _missing:  # pragma: no cover - exercised only without the extra
    raise ImportError(
        "agentexperience.telemetry needs the 'otel' extra: pip install 'agenticexperience[otel]'"
    ) from _missing

from . import __version__
from .observer import (
    ATTRIBUTES,
    FAULTED,
    NEVER_NESTED,
    OPERATIONS,
    AttributeValue,
    set_observer,
)

SPAN_NAME_PREFIX = "agentexperience."
OPERATION_COUNT = "agentexperience.operation.count"
OPERATION_DURATION = "agentexperience.operation.duration"
OPERATION_FAILURES = "agentexperience.operation.failures"

INSTRUMENTS: Mapping[str, tuple[str, str]] = {
    OPERATION_COUNT: ("counter", "{operation}"),
    OPERATION_DURATION: ("histogram", "s"),
    OPERATION_FAILURES: ("counter", "{failure}"),
}
"""The three instruments every meter publishes, with their kind and unit."""

DIMENSIONS: tuple[str, ...] = ("operation", "outcome", "error.class", "nested")
"""The only metric dimensions."""

OPERATION_ATTRIBUTE = "agentexperience.operation"
OUTCOME_ATTRIBUTE = "agentexperience.outcome"
ERROR_CLASS_ATTRIBUTE = "agentexperience.error.class"
ERROR_TYPE_ATTRIBUTE = "error.type"

SPAN_ATTRIBUTES: frozenset[str] = frozenset(
    {OPERATION_ATTRIBUTE, OUTCOME_ATTRIBUTE, ERROR_CLASS_ATTRIBUTE, ERROR_TYPE_ATTRIBUTE, *ATTRIBUTES}
)
"""Every span attribute this adapter can write."""

_active_sources: ContextVar[frozenset[str]] = ContextVar("agentexperience_telemetry_sources", default=frozenset())


class _Instruments:
    def __init__(
        self, source: str, tracer_provider: TracerProvider | None, meter_provider: MeterProvider | None
    ) -> None:
        self.tracer: Tracer = trace.get_tracer(source, __version__, tracer_provider=tracer_provider)
        meter = metrics.get_meter(source, __version__, meter_provider=meter_provider)
        self.count: Counter = meter.create_counter(
            OPERATION_COUNT,
            unit="{operation}",
            description="Instrumented Experience operations, by operation and outcome.",
        )
        self.duration: Histogram = meter.create_histogram(
            OPERATION_DURATION, unit="s", description="How long each instrumented Experience operation took."
        )
        self.failures: Counter = meter.create_counter(
            OPERATION_FAILURES, unit="{failure}", description="Instrumented Experience operations that threw."
        )


class _Token:
    __slots__ = ("attach", "nested", "previous_sources", "span", "started")

    def __init__(
        self, span: Span | None, nested: bool, previous_sources: frozenset[str], attach: object | None
    ) -> None:
        self.span = span
        self.nested = nested
        self.previous_sources = previous_sources
        self.attach = attach
        self.started = time.perf_counter()


class OpenTelemetryObserver:
    """The observer that reports to OpenTelemetry. Pass providers to use them instead of the global ones."""

    def __init__(
        self, tracer_provider: TracerProvider | None = None, meter_provider: MeterProvider | None = None
    ) -> None:
        self._instruments: dict[str, _Instruments] = {}
        self._tracer_provider = tracer_provider
        self._meter_provider = meter_provider

    def _for(self, source: str) -> _Instruments:
        instruments = self._instruments.get(source)
        if instruments is None:
            instruments = self._instruments[source] = _Instruments(source, self._tracer_provider, self._meter_provider)
        return instruments

    def start(self, source: str, operation: str) -> object | None:
        instruments = self._for(source)
        sources = _active_sources.get()
        nested = operation not in NEVER_NESTED and source in sources
        span: Span | None = None
        attach: object | None = None
        if OPERATIONS[operation].span:
            span = instruments.tracer.start_span(
                SPAN_NAME_PREFIX + operation,
                kind=SpanKind.INTERNAL,
                attributes={OPERATION_ATTRIBUTE: operation},
            )
            attach = otel_context.attach(trace.set_span_in_context(span))
        if operation not in NEVER_NESTED:
            _active_sources.set(sources | {source})
        return _Token(span, nested, sources, attach)

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
        if not isinstance(token, _Token):
            return
        elapsed = time.perf_counter() - token.started
        try:
            if token.attach is not None:
                otel_context.detach(token.attach)  # type: ignore[arg-type]
        except Exception:
            pass
        _active_sources.set(token.previous_sources)
        instruments = self._for(source)
        try:
            dimensions: dict[str, Any] = {"operation": operation, "outcome": outcome, "nested": token.nested}
            instruments.count.add(1, dimensions)
            instruments.duration.record(elapsed, dimensions)
            if error_class is not None:
                instruments.failures.add(
                    1, {"operation": operation, "error.class": error_class, "nested": token.nested}
                )
        finally:
            span = token.span
            if span is not None:
                try:
                    span.set_attribute(OUTCOME_ATTRIBUTE, outcome)
                    for name, value in attributes.items():
                        if name in ATTRIBUTES:
                            span.set_attribute(name, value)
                    if error_class is not None:
                        span.set_attribute(ERROR_CLASS_ATTRIBUTE, error_class)
                    if error_type is not None:
                        span.set_attribute(ERROR_TYPE_ATTRIBUTE, error_type)
                    span.set_status(Status(StatusCode.ERROR if outcome == FAULTED else StatusCode.OK))
                finally:
                    span.end()


def enable(
    tracer_provider: TracerProvider | None = None, meter_provider: MeterProvider | None = None
) -> OpenTelemetryObserver:
    """Starts reporting to OpenTelemetry (the global providers, or the ones given) and returns the observer."""
    observer = OpenTelemetryObserver(tracer_provider, meter_provider)
    set_observer(observer)
    return observer


def disable() -> None:
    """Stops reporting."""
    set_observer(None)


__all__ = ["OpenTelemetryObserver", "disable", "enable"]
