"""
Finalization: a captured run plus the host's verification becomes one stored record and its initial lifecycle event
(contract steps 2 to 5).
"""

from __future__ import annotations

import uuid
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

from ._text import format_round_trip
from .capture import CapturedRun
from .encoding import Instant, format_uuid
from .ids import experience_id_for, initial_event_id_for, reflection_id_for
from .observer import observe
from .records import Evidence, ExperienceRecord, ExperienceStatus, Reflection
from .reflector import ScreeningRefused, reflect, screen
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


def finalize(
    run: CapturedRun,
    *,
    closed_round: ClosedVerificationRound | None,
    required_checks: Sequence[RequiredCheck],
    evidence: Sequence[Evidence],
    current_artifact_revision: str,
    finalized_at: Instant,
) -> FinalizedExperience:
    """
    Finalizes a finished run: verification, the deterministic reflection (only for a ``Verified`` run), the stored
    record (``Validated`` with a reflection, else ``Quarantined``) and its initial lifecycle event.
    """
    with observe("finalize") as op:
        if op is not None:
            op.set("agentexperience.run_id", str(run.run_id))
            op.set("agentexperience.stage", "Load")
        finalized = _finalize(run, closed_round, required_checks, evidence, current_artifact_revision, finalized_at, op)
        if op is not None:
            op.set("agentexperience.experience_id", str(finalized.record.experience_id))
            op.outcome(finalized.record.status)
        return finalized


def _finalize(
    run: CapturedRun,
    closed_round: ClosedVerificationRound | None,
    required_checks: Sequence[RequiredCheck],
    evidence: Sequence[Evidence],
    current_artifact_revision: str,
    finalized_at: Instant,
    op: Any,
) -> FinalizedExperience:
    """The stage the finalization has reached is on the span (``agentexperience.stage``), also when it throws."""
    if run.execution_status is None:
        raise ValueError("The run has not finished and cannot be finalized.")

    at = finalized_at.truncate_to_microseconds()
    if op is not None:
        op.set("agentexperience.stage", "Evaluate")
    evaluation = aggregate(evidence, required_checks, closed_round, current_artifact_revision, at)

    reflection: Reflection | None = None
    refusal: str | None = None
    if evaluation.outcome.status == "Verified":
        if op is not None:
            op.set("agentexperience.stage", "Reflect")
        try:
            reflection = screen(reflect(run, evaluation, reflection_id_for(run.run_id), at))
        except ScreeningRefused as refused:
            refusal = str(refused)

    if op is not None:
        op.set("agentexperience.stage", "CreateRecord")
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
    return FinalizedExperience(record, event, evaluation, refusal)
