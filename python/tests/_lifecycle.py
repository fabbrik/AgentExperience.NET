"""
Contract 1.16: reading the lifecycle fixtures' requests, and writing this port's answers in the fixtures' JSON. Shared
by the in-memory and PostgreSQL conformance runners and the cross-language race writer.
"""

from __future__ import annotations

import math
import uuid
from typing import Any, cast

from _contract import scope_of

from agentexperience import (
    ApplyConfidenceEvidenceRequest,
    ApplyConfidenceEvidenceResult,
    AuthorizationBounds,
    AuthorizationContext,
    CommitLifecycleTransitionRequest,
    CommitLifecycleTransitionResult,
    ConfidenceArgumentError,
    ConfidenceEngineError,
    ConfidenceReadResult,
    ConfidenceUpdate,
    ExperienceConfidenceEngine,
    ExperienceConfidenceInput,
    HistoryQuery,
    HistoryResult,
    Instant,
    LifecycleCommitResult,
    LifecycleEvent,
    ReuseConfidenceHeuristicEngine,
    Scope,
    StoredLifecycleEvent,
    StoreValidationError,
    SupersessionCheckResult,
)
from agentexperience.encoding import json_number


class HostEngineException(Exception):
    """The host engine's own failure, which must propagate unchanged."""


class HostEngine:
    """A host engine as a case declares it (the .NET generator's ``HostEngine``)."""

    def __init__(self, rule_id: Any, rule_version: Any, score: Any) -> None:
        self._rule_id = rule_id
        self._rule_version = rule_version
        self._score = score

    @property
    def rule_id(self) -> str:
        return cast(str, self._rule_id)

    @property
    def rule_version(self) -> str:
        return cast(str, self._rule_version)

    def score(self, input: ExperienceConfidenceInput) -> float:
        if isinstance(self._score, (int, float)):
            return float(self._score)
        if self._score == "NaN":
            return math.nan
        if self._score == "Infinity":
            return math.inf
        if self._score == "-Infinity":
            return -math.inf
        if self._score == "bayes":
            return (2.0 + input.supporting_validations) / (4.0 + input.supporting_validations + input.contradictions)
        if self._score == "stored":
            return input.record.reuse_confidence
        if self._score == "throws":
            raise HostEngineException("The host engine failed.")
        raise AssertionError(f"Unknown host engine score {self._score!r}.")


def engine_of(value: Any) -> ExperienceConfidenceEngine | None:
    if value is None:
        return None
    if value == "default":
        return ReuseConfidenceHeuristicEngine.INSTANCE
    return HostEngine(value["ruleId"], value["ruleVersion"], value["score"])


def thrown_json(error: BaseException) -> dict[str, Any]:
    """An exception as a fixture's ``thrown`` writes it."""
    if isinstance(error, ConfidenceArgumentError):
        return {"exception": error.exception, "parameter": error.parameter, "message": error.message}
    if isinstance(error, ConfidenceEngineError):
        return {"exception": "InvalidOperationException", "parameter": None, "message": error.message}
    return {"exception": type(error).__name__, "parameter": None, "message": str(error)}


# ---- Reading requests ----------------------------------------------------------------------------------------------


def uuid_of(value: str | None) -> uuid.UUID | None:
    return None if value is None else uuid.UUID(value)


def authorization_of(value: dict[str, Any]) -> AuthorizationContext:
    return AuthorizationContext(
        value["principalId"],
        AuthorizationBounds(
            value["tenantId"],
            value["applicationId"],
            value["projectId"],
            value["teamId"],
            value["agentId"],
            value["userId"],
        ),
    )


def commit_request_of(value: dict[str, Any]) -> CommitLifecycleTransitionRequest:
    return CommitLifecycleTransitionRequest(
        event_id=uuid.UUID(value["eventId"]),
        experience_id=uuid.UUID(value["experienceId"]),
        scope=scope_of(value["scope"]),
        prior_status=value["priorStatus"],
        current_status=value["currentStatus"],
        reason=value["reason"],
        producer=value["producer"],
        occurred_at=Instant.parse(value["occurredAt"]),
        expected_revision=value["expectedRevision"],
        replacement_experience_id=uuid_of(value["replacementExperienceId"]),
    )


def apply_request_of(value: dict[str, Any]) -> ApplyConfidenceEvidenceRequest:
    return ApplyConfidenceEvidenceRequest(
        event_id=uuid.UUID(value["eventId"]),
        experience_id=uuid.UUID(value["experienceId"]),
        scope=scope_of(value["scope"]),
        evidence_id=uuid.UUID(value["evidenceId"]),
        kind=value["kind"],
        source=value["source"],
        run_id=uuid.UUID(value["runId"]),
        verification_round_id=uuid_of(value["verificationRoundId"]),
        reason=value["reason"],
        producer=value["producer"],
        occurred_at=Instant.parse(value["occurredAt"]),
        detail=value["detail"],
        assessment_token=value["assessmentToken"],
    )


def update_of(value: dict[str, Any] | None) -> ConfidenceUpdate | None:
    if value is None:
        return None
    return ConfidenceUpdate(
        evidence_id=uuid.UUID(value["evidenceId"]),
        kind=value["kind"],
        source=value["source"],
        run_id=uuid.UUID(value["runId"]),
        verification_round_id=uuid_of(value["verificationRoundId"]),
        reviewer_identity=value["reviewerIdentity"],
        rule_version=value["ruleVersion"],
        prior_reuse_confidence=float(value["priorReuseConfidence"]),
        new_reuse_confidence=float(value["newReuseConfidence"]),
        prior_supporting_validations=value["priorSupportingValidations"],
        new_supporting_validations=value["newSupportingValidations"],
        prior_contradictions=value["priorContradictions"],
        new_contradictions=value["newContradictions"],
        detail=value["detail"],
        assessment_id=uuid_of(value.get("assessmentId")),
        admission=value.get("admission"),
    )


def event_of(value: dict[str, Any]) -> LifecycleEvent:
    return LifecycleEvent(
        event_id=uuid.UUID(value["eventId"]),
        experience_record_id=uuid.UUID(value["experienceRecordId"]),
        prior_status=value["priorStatus"],
        current_status=value["currentStatus"],
        reason=value["reason"],
        producer=value["producer"],
        occurred_at=Instant.parse(value["occurredAt"]),
        expected_revision=value["expectedRevision"],
        replacement_experience_id=uuid_of(value["replacementExperienceId"]),
        confidence=update_of(value.get("confidence")),
    )


def history_query_of(value: dict[str, Any]) -> HistoryQuery:
    return HistoryQuery(
        scope_of(value["scope"]), uuid.UUID(value["experienceId"]), value["limit"], value["startAfterRevision"]
    )


# ---- Writing answers -----------------------------------------------------------------------------------------------


def _id(value: uuid.UUID | None) -> str | None:
    return None if value is None else str(value)


def errors_json(errors: tuple[StoreValidationError, ...]) -> list[dict[str, Any]]:
    return [{"path": error.path, "message": error.message} for error in errors]


def update_json(update: ConfidenceUpdate | None) -> dict[str, Any] | None:
    if update is None:
        return None
    return {
        "evidenceId": str(update.evidence_id),
        "kind": update.kind,
        "source": update.source,
        "runId": str(update.run_id),
        "verificationRoundId": _id(update.verification_round_id),
        "reviewerIdentity": update.reviewer_identity,
        "ruleVersion": update.rule_version,
        "priorReuseConfidence": json_number(update.prior_reuse_confidence),
        "newReuseConfidence": json_number(update.new_reuse_confidence),
        "priorSupportingValidations": update.prior_supporting_validations,
        "newSupportingValidations": update.new_supporting_validations,
        "priorContradictions": update.prior_contradictions,
        "newContradictions": update.new_contradictions,
        "detail": update.detail,
        "assessmentId": _id(update.assessment_id),
        "admission": update.admission,
        "counted": update.counted,
    }


def event_json(event: LifecycleEvent | None) -> dict[str, Any] | None:
    if event is None:
        return None
    return {
        "eventId": str(event.event_id),
        "experienceRecordId": str(event.experience_record_id),
        "priorStatus": event.prior_status,
        "currentStatus": event.current_status,
        "reason": event.reason,
        "producer": event.producer,
        "occurredAt": event.occurred_at.format(),
        "expectedRevision": event.expected_revision,
        "replacementExperienceId": _id(event.replacement_experience_id),
        "confidence": update_json(event.confidence),
    }


def _number(value: float | None) -> float | int | None:
    return None if value is None else json_number(value)


def event_row(stored: StoredLifecycleEvent, scope: Scope) -> dict[str, Any]:
    """An event as a row of ``agent_experience.lifecycle_events``, with JSON values."""
    event = stored.event
    update = event.confidence
    return {
        "event_id": str(event.event_id),
        "experience_id": str(event.experience_record_id),
        "tenant_id": scope.tenant_id,
        "application_id": scope.application_id,
        "project_id": scope.project_id,
        "team_id": scope.team_id,
        "agent_id": scope.agent_id,
        "user_id": scope.user_id,
        "prior_status": event.prior_status,
        "current_status": event.current_status,
        "reason": event.reason,
        "producer": event.producer,
        "occurred_at": event.occurred_at.truncate_to_microseconds().format(),
        "recorded_at": stored.recorded_at.truncate_to_microseconds().format(),
        "expected_revision": event.expected_revision,
        "applied_revision": stored.applied_revision,
        "replacement_experience_id": _id(event.replacement_experience_id),
        "actor": stored.actor,
        "confidence_evidence_id": None if update is None else str(update.evidence_id),
        "confidence_kind": None if update is None else update.kind,
        "confidence_source": None if update is None else update.source,
        "confidence_run_id": None if update is None else str(update.run_id),
        "confidence_verification_round_id": None if update is None else _id(update.verification_round_id),
        "confidence_reviewer_identity": None if update is None else update.reviewer_identity,
        "confidence_rule_version": None if update is None else update.rule_version,
        "confidence_detail": None if update is None else update.detail,
        "prior_reuse_confidence": None if update is None else _number(update.prior_reuse_confidence),
        "new_reuse_confidence": None if update is None else _number(update.new_reuse_confidence),
        "prior_supporting_validations": None if update is None else update.prior_supporting_validations,
        "new_supporting_validations": None if update is None else update.new_supporting_validations,
        "prior_contradictions": None if update is None else update.prior_contradictions,
        "new_contradictions": None if update is None else update.new_contradictions,
        "confidence_assessment_id": None if update is None else _id(update.assessment_id),
        "confidence_admission": None if update is None else update.admission,
    }


def commit_result_json(result: CommitLifecycleTransitionResult) -> dict[str, Any]:
    return {
        "outcome": result.outcome,
        "event": event_json(result.event),
        "revision": result.revision,
        "currentStatus": result.current_status,
        "errors": errors_json(result.errors),
        "reason": result.reason,
    }


def apply_result_json(result: ApplyConfidenceEvidenceResult) -> dict[str, Any]:
    return {
        "thrown": None,
        "outcome": result.outcome,
        "event": event_json(result.event),
        "update": update_json(result.update),
        "revision": result.revision,
        "status": result.status,
        "errors": errors_json(result.errors),
        "reason": result.reason,
        "refusal": result.refusal,
        "counted": result.counted,
    }


def store_commit_json(result: LifecycleCommitResult) -> dict[str, Any]:
    return {
        "outcome": result.outcome,
        "revision": result.revision,
        "currentStatus": result.current_status,
        "errors": errors_json(result.errors),
        "appliedConfidence": update_json(result.applied_confidence),
    }


def supersession_json(result: SupersessionCheckResult) -> dict[str, Any]:
    return {
        "outcome": result.outcome,
        "replacementStatus": result.replacement_status,
        "errors": errors_json(result.errors),
    }


def history_json(result: HistoryResult, scope: Scope) -> dict[str, Any]:
    return {
        "outcome": result.outcome,
        "revision": result.revision,
        "events": [event_row(stored, scope) for stored in result.events],
        "errors": errors_json(result.errors),
        "nextStartAfterRevision": result.next_start_after_revision,
    }


def read_confidence_json(result: ConfidenceReadResult) -> dict[str, Any]:
    report = result.report
    report_json: dict[str, Any] | None = None
    if report is not None:

        def counts(value: Any) -> dict[str, int]:
            return {"supporting": value.supporting, "contradicting": value.contradicting}

        report_json = {
            "experienceId": str(report.experience_id),
            "revision": report.revision,
            "status": report.status,
            "filter": report.filter,
            "reuseConfidence": json_number(report.reuse_confidence),
            "supportingValidations": report.supporting_validations,
            "contradictions": report.contradictions,
            "storedReuseConfidence": json_number(report.stored_reuse_confidence),
            "storedSupportingValidations": report.stored_supporting_validations,
            "storedContradictions": report.stored_contradictions,
            "verified": counts(report.verified),
            "hostTrusted": counts(report.host_trusted),
            "unrecorded": counts(report.unrecorded),
            "origin": report.origin,
            "initial": counts(report.initial),
            "hostTrustedRecordedOnly": counts(report.host_trusted_recorded_only),
        }
    return {"outcome": result.outcome, "report": report_json, "errors": errors_json(result.errors)}
