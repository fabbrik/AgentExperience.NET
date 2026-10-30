"""
Finalization: a captured run plus the host's verification becomes one stored record and its initial lifecycle event
(contract steps 2 to 5).
"""

from __future__ import annotations

import inspect
import uuid
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

from ._text import format_round_trip
from .capture import CapturedRun
from .encoding import Instant, format_uuid
from .ids import experience_id_for, initial_event_id_for, reflection_id_for
from .observer import observe, type_name
from .records import Evidence, ExperienceRecord, ExperienceStatus, Reflection
from .reflector import (
    DeterministicReflector,
    ExperienceReflector,
    ReflectionBindingError,
    ReflectionRequest,
    ScreeningRefusal,
    ScreeningRefused,
    default_run_content,
    screen,
)
from .verification import ClosedVerificationRound, RequiredCheck, VerificationResult, aggregate

PRODUCER = "AgentExperience.ExperienceFinalizationService/1.0.0"
"""The producer every initial lifecycle event carries."""

INITIAL_VALIDATED_REUSE_CONFIDENCE = 2.0 / 3.0
INITIAL_SUPPORTING_VALIDATIONS = 1
CREATED_STATUS: ExperienceStatus = "Candidate"


@dataclass(frozen=True)
class InitialLifecycleEvent:
    """
    The event that moves a new record from ``Candidate`` to its status, taking it to revision 1. The store adds the
    record's scope columns, the actor and its own ``recorded_at``.
    """

    event_id: uuid.UUID
    experience_id: uuid.UUID
    prior_status: ExperienceStatus
    current_status: ExperienceStatus
    reason: str
    producer: str
    occurred_at: Instant
    expected_revision: int = 0
    applied_revision: int = 1

    def to_json(self) -> dict[str, Any]:
        """The event columns finalization decides, as ``expected-event.json`` holds them."""
        return {
            "event_id": format_uuid(self.event_id),
            "experience_id": format_uuid(self.experience_id),
            "prior_status": self.prior_status,
            "current_status": self.current_status,
            "reason": self.reason,
            "producer": self.producer,
            "occurred_at": self.occurred_at.format(),
            "expected_revision": self.expected_revision,
            "applied_revision": self.applied_revision,
        }


@dataclass(frozen=True)
class FinalizedExperience:
    """A finalized record, in its final state at revision 1, and the initial event that must be written with it."""

    record: ExperienceRecord
    event: InitialLifecycleEvent
    evaluation: VerificationResult
    screening_refusal: str | None = None
    """Why screening refused the reflection, when it did (the record is then Quarantined); never record content."""
    screening_refusal_code: ScreeningRefusal | None = None
    """The code of :attr:`screening_refusal` (.NET's ``ReflectionScreeningRefusal``): ``OverLimit``,
    ``MissingLesson``, ``UnsafeContent``, ..."""
    reflection_failure: Exception | None = None
    """What the reflector raised, or the binding error for a reflection that does not match its request, when that is
    what quarantined the record (.NET's ``FinalizationFailure.Exception``). A model-backed reflector raises
    :class:`agentexperience.reflection_prompt.ReflectionFailed`, with a ``kind``; its message holds no model text."""
    screening_exception_type: str | None = None
    """When binding or screening the reflection raised (a reflector that returned something that is not a reflection,
    a reflection missing a bound field, a guard that failed), the type name of what it raised; never its message. The
    record is then Quarantined with :attr:`screening_refusal_code` ``Unreadable``, as .NET's backstop stores it."""


def finalize(
    run: CapturedRun,
    *,
    closed_round: ClosedVerificationRound | None,
    required_checks: Sequence[RequiredCheck],
    evidence: Sequence[Evidence],
    current_artifact_revision: str,
    finalized_at: Instant,
    reflector: ExperienceReflector | None = None,
) -> FinalizedExperience:
    """
    Finalizes a finished run: verification, the reflection (only for a ``Verified`` run; the deterministic reflector
    unless ``reflector`` is given), screening, the stored record (``Validated`` with a reflection, else
    ``Quarantined``) and its initial lifecycle event. A reflector that raises, returns nothing, or returns a reflection
    that does not match its request quarantines the record, as in .NET. A reflector whose ``reflect`` is asynchronous
    needs :func:`finalize_async` (``TypeError`` here).
    """
    with observe("finalize") as op:
        if op is not None:
            op.set("agentexperience.run_id", str(run.run_id))
            op.set("agentexperience.stage", "Load")
        evaluation, at = _evaluate(
            run, closed_round, required_checks, evidence, current_artifact_revision, finalized_at, op
        )
        reflector = reflector or DeterministicReflector()
        reflected = _Reflected()
        if evaluation.outcome.status == "Verified":
            if op is not None:
                op.set("agentexperience.stage", "Reflect")
            request = ReflectionRequest(run, evaluation, reflection_id_for(run.run_id), at)
            answer: object = None
            try:
                answer = reflector.reflect(request)
            except Exception as error:
                reflected.failure = error
            if inspect.isawaitable(answer):
                if inspect.iscoroutine(answer):
                    answer.close()
                raise TypeError("This reflector is asynchronous: finalize with finalize_async.")
            if reflected.failure is None:
                _screen(reflector, request, answer, reflected)
        return _record(run, evaluation, at, closed_round, reflected, op)


async def finalize_async(
    run: CapturedRun,
    *,
    closed_round: ClosedVerificationRound | None,
    required_checks: Sequence[RequiredCheck],
    evidence: Sequence[Evidence],
    current_artifact_revision: str,
    finalized_at: Instant,
    reflector: ExperienceReflector | None = None,
) -> FinalizedExperience:
    """:func:`finalize` for a reflector that may be asynchronous (the model-backed one is): its ``reflect`` is awaited
    when it returns an awaitable. The caller's cancellation propagates; anything else the reflector raises quarantines
    the record."""
    with observe("finalize") as op:
        if op is not None:
            op.set("agentexperience.run_id", str(run.run_id))
            op.set("agentexperience.stage", "Load")
        evaluation, at = _evaluate(
            run, closed_round, required_checks, evidence, current_artifact_revision, finalized_at, op
        )
        reflector = reflector or DeterministicReflector()
        reflected = _Reflected()
        if evaluation.outcome.status == "Verified":
            if op is not None:
                op.set("agentexperience.stage", "Reflect")
            request = ReflectionRequest(run, evaluation, reflection_id_for(run.run_id), at)
            try:
                answer = reflector.reflect(request)
                if inspect.isawaitable(answer):
                    answer = await answer
            except Exception as error:
                reflected.failure = error
            else:
                _screen(reflector, request, answer, reflected)
        return _record(run, evaluation, at, closed_round, reflected, op)


@dataclass
class _Reflected:
    reflection: Reflection | None = None
    refusal: str | None = None
    code: ScreeningRefusal | None = None
    failure: Exception | None = None
    exception_type: str | None = None


def _run_content(reflector: ExperienceReflector, request: ReflectionRequest) -> list[str] | None:
    """What the reflector says it showed the model, or the default; one whose answer raises showed nothing."""
    declared = getattr(reflector, "reflected_run_content", None)
    if not callable(declared):
        return default_run_content(request)
    try:
        content = declared(request)
    except Exception:
        return []
    return [] if content is None else list(content)


def _screen(reflector: ExperienceReflector, request: ReflectionRequest, answer: object, reflected: _Reflected) -> None:
    if answer is None:
        reflected.failure = ValueError("The reflector returned no reflection.")
        return
    try:
        if not isinstance(answer, Reflection):
            raise TypeError("The reflector returned something that is not a Reflection.")
        try:
            request.ensure_matches(answer)
        except ReflectionBindingError as mismatch:
            reflected.failure = mismatch
            return
        reflected.reflection = screen(answer, _run_content(reflector, request))
    except ScreeningRefused as refused:
        reflected.refusal = str(refused)
        reflected.code = refused.refusal
    except Exception as error:
        # The backstop, as .NET's: binding or screening that throws never escapes finalization. Only the type is
        # kept; its message may quote the reflection.
        reflected.reflection = None
        reflected.refusal = "screening the reflection threw"
        reflected.code = "Unreadable"
        reflected.exception_type = type_name(error)


def _evaluate(
    run: CapturedRun,
    closed_round: ClosedVerificationRound | None,
    required_checks: Sequence[RequiredCheck],
    evidence: Sequence[Evidence],
    current_artifact_revision: str,
    finalized_at: Instant,
    op: Any,
) -> tuple[VerificationResult, Instant]:
    """The stage the finalization has reached is on the span (``agentexperience.stage``), also when it throws."""
    if run.execution_status is None:
        raise ValueError("The run has not finished and cannot be finalized.")
    at = finalized_at.truncate_to_microseconds()
    if op is not None:
        op.set("agentexperience.stage", "Evaluate")
    return aggregate(evidence, required_checks, closed_round, current_artifact_revision, at), at


def _record(
    run: CapturedRun,
    evaluation: VerificationResult,
    at: Instant,
    closed_round: ClosedVerificationRound | None,
    reflected: _Reflected,
    op: Any,
) -> FinalizedExperience:
    if op is not None:
        op.set("agentexperience.stage", "CreateRecord")
        if reflected.code is not None:
            op.set("agentexperience.reflection.screening_refusal", reflected.code)
    reflection = reflected.reflection
    status: ExperienceStatus = "Validated" if reflection is not None else "Quarantined"
    round_id = closed_round.round_id if closed_round is not None and closed_round.round_id.int != 0 else None
    record = ExperienceRecord(
        experience_id=experience_id_for(run.run_id, run.scope),
        source_run_id=run.run_id,
        scope=run.scope,
        task_id=run.task_id,
        task_summary=run.task_description,
        attempts=run.attempts,
        outcome=evaluation.outcome,
        completion_score=evaluation.completion_score,
        reflection=reflection,
        environment=run.environment,
        provenance=run.provenance,
        status=status,
        reuse_confidence=INITIAL_VALIDATED_REUSE_CONFIDENCE if reflection is not None else 0.0,
        supporting_validations=INITIAL_SUPPORTING_VALIDATIONS if reflection is not None else 0,
        contradictions=0,
        revision=1,
        created_at=at,
        updated_at=at,
        closed_round_id=round_id,
        origin="Finalized",
    )
    reason = (
        f"Initial finalization: verification {evaluation.outcome.status}, completion score "
        f"{format_round_trip(evaluation.completion_score)}, "
        f"{'no eligible lesson recorded' if reflection is None else 'reflection recorded'}."
    )
    event = InitialLifecycleEvent(
        event_id=initial_event_id_for(run.run_id, run.scope),
        experience_id=record.experience_id,
        prior_status=CREATED_STATUS,
        current_status=status,
        reason=reason,
        producer=PRODUCER,
        occurred_at=at,
    )
    finalized = FinalizedExperience(
        record, event, evaluation, reflected.refusal, reflected.code, reflected.failure, reflected.exception_type
    )
    if op is not None:
        op.set("agentexperience.experience_id", str(record.experience_id))
        op.outcome(record.status)
    return finalized
