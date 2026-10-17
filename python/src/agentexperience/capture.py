"""
Capture: a run as its host observes it, sanitized and bounded as it is recorded (contract step 1).

Capture answers each attempt with an outcome: ``Recorded``; ``SanitizationRejected`` (nothing of the attempt is kept);
``CapacityExceeded`` (the run already holds its maximum of attempts); ``DuplicateNoOp`` or ``Conflict`` for an
attempt ID seen before. Kept attempts are numbered from 0 in the order they were recorded, and an attempt's tool calls
from 0 in the order given, beyond the 50th dropped.
"""

from __future__ import annotations

import uuid
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, field, replace
from typing import Any, Literal

from ._text import is_null_or_whitespace, utf16_len, utf16_prefix
from .encoding import Duration, Instant
from .limits import DEFAULT_CAPTURE_LIMITS, MAX_EXPOSURES_PER_RUN, TRUNCATION_PLACEHOLDER, CaptureLimits
from .observer import observe
from .records import Attempt, EnvironmentFingerprint, Provenance, RunExposure, Scope, ToolCall
from .sanitization import VALUE_FIELD, Sanitization, SanitizationPolicy, SanitizationRejected

AppendOutcome = Literal["Recorded", "DuplicateNoOp", "Conflict", "CapacityExceeded", "SanitizationRejected"]
ExposureOutcome = Literal["Recorded", "DuplicateNoOp", "Conflict", "CapacityExceeded"]
RunExecutionStatus = Literal["Completed", "Failed", "Cancelled"]

_EMPTY = uuid.UUID(int=0)


@dataclass(frozen=True)
class RawToolCall:
    """A tool call as the host observed it, before sanitization."""

    tool_call_id: uuid.UUID
    tool_name: str
    arguments: Mapping[str, Any]
    started_at: Instant
    duration: Duration
    result: str | None = None
    error: str | None = None


@dataclass(frozen=True)
class RawAttempt:
    """An attempt as the host observed it, before sanitization."""

    attempt_id: uuid.UUID
    started_at: Instant
    duration: Duration
    tool_calls: Sequence[RawToolCall] = ()
    result: str | None = None
    error: str | None = None


@dataclass(frozen=True)
class CapturedRun:
    """A run as capture holds it: sanitized attempts, the exposures recorded, and how it ended."""

    run_id: uuid.UUID
    task_id: str
    task_description: str | None
    scope: Scope
    environment: EnvironmentFingerprint
    provenance: Provenance
    started_at: Instant
    attempts: tuple[Attempt, ...] = ()
    execution_status: RunExecutionStatus | None = None
    ended_at: Instant | None = None


@dataclass
class RunCapture:
    """Captures one run. Not thread-safe: one run is captured by one caller."""

    run: CapturedRun
    sanitization: Sanitization = field(default_factory=Sanitization.default)
    limits: CaptureLimits = DEFAULT_CAPTURE_LIMITS
    _seen: dict[uuid.UUID, RawAttempt] = field(default_factory=dict, init=False, repr=False)

    @staticmethod
    def start(
        run_id: uuid.UUID,
        task_id: str,
        task_description: str | None,
        scope: Scope,
        environment: EnvironmentFingerprint,
        provenance: Provenance,
        started_at: Instant,
        *,
        sanitization: Sanitization | None = None,
        limits: CaptureLimits | None = None,
    ) -> RunCapture:
        """Starts a run. It starts exposed to nothing: exposures are recorded with :meth:`record_exposure`."""
        with observe("capture.start_run") as op:
            if op is not None:
                op.set("agentexperience.run_id", str(run_id))
            started = RunCapture._start(
                run_id, task_id, task_description, scope, environment, provenance, started_at, sanitization, limits
            )
            if op is not None:
                op.outcome("Started")
            return started

    @staticmethod
    def _start(
        run_id: uuid.UUID,
        task_id: str,
        task_description: str | None,
        scope: Scope,
        environment: EnvironmentFingerprint,
        provenance: Provenance,
        started_at: Instant,
        sanitization: Sanitization | None,
        limits: CaptureLimits | None,
    ) -> RunCapture:
        if run_id == _EMPTY:
            raise ValueError("A run must be named by a non-empty UUID.")
        if is_null_or_whitespace(task_id):
            raise ValueError("A run's task ID must not be blank.")
        if provenance.exposed_to:
            raise ValueError("A run starts exposed to nothing; record exposures with record_exposure.")
        return RunCapture(
            CapturedRun(run_id, task_id, task_description, scope, environment, provenance, started_at),
            sanitization or Sanitization.default(),
            limits or DEFAULT_CAPTURE_LIMITS,
        )

    def record_exposure(self, exposures: Iterable[RunExposure]) -> ExposureOutcome:
        """Records stored records the run was shown, keeping the lowest revision seen of each."""
        submitted = list(exposures)
        for exposure in submitted:
            if exposure.experience_id == _EMPTY:
                raise ValueError("An exposure must name a record, never an empty UUID.")
            if exposure.revision < 0:
                raise ValueError("An exposure's revision must not be negative.")
        if self.run.execution_status is not None:
            return "Conflict"

        merged = {exposure.experience_id: exposure.revision for exposure in self.run.provenance.exposed_to}
        changed = False
        for exposure in submitted:
            held = merged.get(exposure.experience_id)
            if held is None or exposure.revision < held:
                merged[exposure.experience_id] = exposure.revision
                changed = True
        if not changed:
            return "DuplicateNoOp"
        if len(merged) > MAX_EXPOSURES_PER_RUN:
            return "CapacityExceeded"
        ordered = tuple(RunExposure(key, merged[key]) for key in sorted(merged, key=str))
        self.run = replace(self.run, provenance=replace(self.run.provenance, exposed_to=ordered))
        return "Recorded"

    def append_attempt(self, attempt: RawAttempt) -> AppendOutcome:
        """Sanitizes and records one attempt; returns capture's answer."""
        with observe("capture.append_attempt") as op:
            if op is not None:
                op.set("agentexperience.run_id", str(self.run.run_id))
                op.set("agentexperience.attempt_id", str(attempt.attempt_id))
            outcome = self._append_attempt(attempt)
            if op is not None:
                op.outcome(outcome)
            return outcome

    def _append_attempt(self, attempt: RawAttempt) -> AppendOutcome:
        if self.run.execution_status is not None:
            return "Conflict"
        previous = self._seen.get(attempt.attempt_id)
        if previous is not None:
            return "DuplicateNoOp" if _comparable(previous) == _comparable(attempt) else "Conflict"
        if len(self.run.attempts) >= self.limits.max_attempts_per_run:
            return "CapacityExceeded"

        try:
            result = _text(attempt.result, self.sanitization.tool_result, self.limits.max_result_length)
            error = _text(attempt.error, self.sanitization.tool_result, self.limits.max_error_length)
            calls: list[ToolCall] = []
            for sequence, raw in enumerate(attempt.tool_calls[: self.limits.max_tool_calls_per_attempt]):
                arguments = self.sanitization.tool_arguments.sanitize(raw.arguments)
                calls.append(
                    ToolCall(
                        tool_call_id=raw.tool_call_id,
                        sequence_number=sequence,
                        tool_name=raw.tool_name,
                        arguments=arguments,
                        started_at=raw.started_at,
                        duration=raw.duration,
                        result=_text(raw.result, self.sanitization.tool_result, self.limits.max_result_length),
                        error=_text(raw.error, self.sanitization.tool_result, self.limits.max_error_length),
                    )
                )
        except SanitizationRejected:
            return "SanitizationRejected"

        self._seen[attempt.attempt_id] = attempt
        kept = Attempt(
            attempt_id=attempt.attempt_id,
            sequence_number=len(self.run.attempts),
            started_at=attempt.started_at,
            duration=attempt.duration,
            tool_calls=tuple(calls),
            result=result,
            error=error,
        )
        self.run = replace(self.run, attempts=(*self.run.attempts, kept))
        return "Recorded"

    def complete(self, execution_status: RunExecutionStatus, ended_at: Instant) -> CapturedRun:
        """Ends the run; no attempt or exposure is recorded after this."""
        with observe("capture.complete_run") as op:
            if op is not None:
                op.set("agentexperience.run_id", str(self.run.run_id))
            held = self.run.execution_status
            if held is None:
                self.run = replace(self.run, execution_status=execution_status, ended_at=ended_at)
            if op is not None:
                op.outcome("Recorded" if held is None else "DuplicateNoOp" if held == execution_status else "Conflict")
            return self.run


def _comparable(attempt: RawAttempt) -> RawAttempt:
    """An attempt with its tool calls as a tuple and its arguments as dicts, so a resubmission compares by value."""
    return replace(
        attempt,
        tool_calls=tuple(replace(call, arguments=dict(call.arguments)) for call in attempt.tool_calls),
    )


def _text(raw: str | None, policy: SanitizationPolicy, max_length: int) -> str | None:
    """A result or error, sanitized as the single field ``value``, and replaced by the placeholder when too long."""
    if raw is None:
        return None
    sanitized = policy.sanitize({VALUE_FIELD: raw}).get(VALUE_FIELD)
    if not isinstance(sanitized, str):
        return None
    if utf16_len(sanitized) > max_length:
        return utf16_prefix(TRUNCATION_PLACEHOLDER, max_length)
    return sanitized
