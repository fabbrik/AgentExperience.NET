"""
The observer hook: the one seam through which the core and the Microsoft Agent Framework integration report what they
do, for telemetry (``docs/telemetry.md``). It imports no OpenTelemetry. ``agentexperience.telemetry`` (the ``otel``
extra) is an adapter that turns the hook into spans and metrics; any other observer can be registered the same way.

Until an observer is registered, every call here is one ``None`` check and allocates nothing. An observer that raises is
swallowed, and never changes what the caller gets.

The names in :data:`OPERATIONS` are the operations this port performs, with the sources, outcomes and span flags that
``docs/telemetry-names.json`` (generated from the .NET definitions) pins by test.
"""

from __future__ import annotations

import asyncio
from collections.abc import Mapping
from dataclasses import dataclass
from types import TracebackType
from typing import Any, Protocol

CORE_SOURCE = "AgentExperience.Core"
"""The source and meter the core operations are reported under (``docs/telemetry.md``)."""

MAF_SOURCE = "AgentExperience.MicrosoftAgentFramework"
"""The source and meter ``inject``, ``record_injection``, ``record_holdout``, ``record_provider_holdout`` and
``benefit_refresh`` are reported under."""

FAULTED = "Faulted"
"""The outcome of an operation that threw."""

CANCELLED = "Cancelled"
TIMEOUT = "Timeout"
INFRASTRUCTURE = "Infrastructure"
UNEXPECTED = "Unexpected"
ERROR_CLASSES: tuple[str, ...] = (CANCELLED, TIMEOUT, INFRASTRUCTURE, UNEXPECTED)
"""The four ``error.class`` values."""

AttributeValue = str | int | bool


@dataclass(frozen=True)
class OperationSpec:
    """One operation this port performs: where it is reported, whether it has a span, and the outcomes it can reach."""

    source: str
    span: bool
    outcomes: tuple[str, ...]


OPERATIONS: Mapping[str, OperationSpec] = {
    "capture.start_run": OperationSpec(CORE_SOURCE, True, ("Started",)),
    "capture.append_attempt": OperationSpec(
        CORE_SOURCE, True, ("Recorded", "DuplicateNoOp", "Conflict", "SanitizationRejected", "CapacityExceeded")
    ),
    "capture.complete_run": OperationSpec(CORE_SOURCE, True, ("Recorded", "DuplicateNoOp", "Conflict")),
    "verify": OperationSpec(CORE_SOURCE, True, ("Unknown", "Verified", "Failed")),
    "reflect": OperationSpec(CORE_SOURCE, True, ("Unknown", "Verified", "Failed")),
    "finalize": OperationSpec(CORE_SOURCE, True, ("Validated", "Quarantined")),
    "retrieve": OperationSpec(CORE_SOURCE, True, ("Completed", "TimedOut", "Denied", "Failed")),
    "index": OperationSpec(CORE_SOURCE, True, ("Indexed", "Stale", "Missing", "ProviderFailed", "IndexFailed")),
    "lifecycle.commit": OperationSpec(
        CORE_SOURCE,
        True,
        (
            "Committed",
            "TransitionNotAllowed",
            "ReplacementNotAllowed",
            "StaleRevision",
            "StatusMismatch",
            "Conflict",
            "NotFound",
            "Denied",
            "Invalid",
            "Deleted",
        ),
    ),
    "confidence.apply": OperationSpec(
        CORE_SOURCE,
        True,
        (
            "Applied",
            "Ineligible",
            "StaleRevision",
            "StatusMismatch",
            "Conflict",
            "NotFound",
            "Denied",
            "Invalid",
            "Deleted",
            "Unverified",
        ),
    ),
    "inject": OperationSpec(
        MAF_SOURCE,
        True,
        (
            "Injected",
            "NothingToInject",
            "Skipped",
            "RetrievalTimedOut",
            "RetrievalDenied",
            "RetrievalFailed",
            "Failed",
            "Withheld",
        ),
    ),
    "record_injection": OperationSpec(MAF_SOURCE, False, ("Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown")),
    "record_holdout": OperationSpec(MAF_SOURCE, False, ("Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown")),
    "record_provider_holdout": OperationSpec(
        MAF_SOURCE, False, ("Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown")
    ),
    "benefit_refresh": OperationSpec(MAF_SOURCE, False, ("Refreshed", "NotRefreshed", "OutcomeUnknown")),
}
"""Every operation this port reports, by its ``operation`` value. The port reports no other: not ``retention.sweep``,
``delete``, grants, seal, backfill, reuse-feedback, ``deindex`` or ``reindex``. ``lifecycle.commit`` and
``confidence.apply`` are contract 1.16's."""

NEVER_NESTED: frozenset[str] = frozenset(
    {"inject", "record_injection", "record_holdout", "record_provider_holdout", "benefit_refresh"}
)
"""Operations whose ``nested`` is always ``false``, as in .NET."""

ATTRIBUTES: tuple[str, ...] = (
    "agentexperience.attempt_id",
    "agentexperience.confidence.admission",
    "agentexperience.correlation_id",
    "agentexperience.event_id",
    "agentexperience.experience_id",
    "agentexperience.independence.refusal",
    "agentexperience.omitted_count",
    "agentexperience.reflection.screening_refusal",
    "agentexperience.reflection_id",
    "agentexperience.run_id",
    "agentexperience.stage",
)
"""The span attributes this port sets beyond the common ones (``agentexperience.operation``, ``.outcome``,
``.error.class`` and ``error.type``, which the adapter writes)."""


class Observer(Protocol):
    """
    What an observer implements. ``start`` runs when an operation begins and returns a token (or ``None``);
    ``finish`` runs once when it ends, with that token. Neither may be relied on to be called on the same thread.
    """

    def start(self, source: str, operation: str) -> object | None: ...

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
        """``outcome`` is the operation's own outcome, or ``Faulted`` when it threw. ``error_class`` is set when the
        operation threw (and for a ``record_injection`` that failed); ``error_type`` is the exception's type name and
        nothing else from it."""
        ...


_observer: Observer | None = None


def set_observer(observer: Observer | None) -> None:
    """Registers the observer every operation reports to, or ``None`` to stop reporting."""
    global _observer
    _observer = observer


def get_observer() -> Observer | None:
    """The registered observer, or ``None``."""
    return _observer


def type_name(exception: BaseException) -> str:
    """An exception's full type name, never its message, which may hold record content."""
    kind = type(exception)
    module = kind.__module__
    return kind.__qualname__ if module in ("builtins", "__main__") else f"{module}.{kind.__qualname__}"


def classify(exception: BaseException) -> str:
    """The ``error.class`` of a thrown exception, as .NET classifies: the host's own cancellation is ``Cancelled``, a
    timeout is ``Timeout``, a store or driver failure is ``Infrastructure``, anything else ``Unexpected``."""
    if isinstance(exception, asyncio.CancelledError):
        return CANCELLED
    if isinstance(exception, (TimeoutError, asyncio.TimeoutError)):
        return TIMEOUT
    if isinstance(exception, OSError) or type(exception).__module__.split(".", 1)[0] == "psycopg":
        return INFRASTRUCTURE
    return UNEXPECTED


class _Operation:
    """One in-flight operation of a registered observer. Used as a context manager: leaving it without an outcome, or
    with an exception, reports ``Faulted``."""

    __slots__ = ("_attributes", "_done", "_error_class", "_observer", "_operation", "_outcome", "_source", "_token")

    def __init__(self, observer: Observer, source: str, operation: str) -> None:
        self._observer = observer
        self._source = source
        self._operation = operation
        self._attributes: dict[str, AttributeValue] = {}
        self._outcome: str | None = None
        self._error_class: str | None = None
        self._done = False
        self._token: object | None = None
        try:
            self._token = observer.start(source, operation)
        except Exception:
            self._token = None

    def __enter__(self) -> _Operation:
        return self

    def __exit__(
        self, exc_type: type[BaseException] | None, exc: BaseException | None, traceback: TracebackType | None
    ) -> None:
        if exc is not None:
            self.fault(exc)
        elif self._outcome is None:
            self._report(FAULTED, UNEXPECTED, None)
        else:
            self._report(self._outcome, self._error_class, None)

    def set(self, name: str, value: AttributeValue) -> None:
        """An attribute of the span. Never record content, task text, scopes or exception messages."""
        self._attributes[name] = value

    def outcome(self, outcome: str, error_class: str | None = None) -> None:
        """The outcome the operation returned. ``error_class`` counts a failure for an operation that reports its
        failure as an outcome instead of raising (``record_injection``)."""
        self._outcome = outcome
        self._error_class = error_class

    def close(self) -> None:
        """Reports the outcome set so far now, so an exception propagating afterwards is not reported as a fault (the
        operation returned its outcome; the exception is its caller's own)."""
        self._report(self._outcome or FAULTED, self._error_class if self._outcome else UNEXPECTED, None)

    def fault(self, exception: BaseException) -> None:
        self._report(FAULTED, classify(exception), type_name(exception))

    def _report(self, outcome: str, error_class: str | None, error_type: str | None) -> None:
        if self._done:
            return
        self._done = True
        try:
            self._observer.finish(
                self._token, self._source, self._operation, outcome, error_class, error_type, self._attributes
            )
        except Exception:
            pass


class _NoOperation:
    """What :func:`observe` answers when no observer is registered: entering it yields ``None``."""

    __slots__ = ()

    def __enter__(self) -> None:
        return None

    def __exit__(self, *_: Any) -> None:
        return None


_NO_OPERATION = _NoOperation()


def observe(operation: str) -> _Operation | _NoOperation:
    """
    Opens ``operation`` (a key of :data:`OPERATIONS`) for the registered observer. Use it as a context manager, and
    guard every use of what it yields: ``with observe("verify") as op:`` then ``if op is not None: op.outcome(...)``.
    With no observer registered it allocates nothing and yields ``None``.
    """
    observer = _observer
    if observer is None:
        return _NO_OPERATION
    spec = OPERATIONS[operation]
    return _Operation(observer, spec.source, operation)
