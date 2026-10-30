"""
Contract 1.17: reading the reuse-feedback fixtures' submissions, and writing this port's answers in the fixtures' JSON.
Shared by the in-memory and PostgreSQL conformance runners and the cross-language writer.
"""

from __future__ import annotations

import math
import uuid
from typing import Any

from _contract import evidence_of, scope_of
from _lifecycle import errors_json, uuid_of

from agentexperience import (
    ComparativeEvaluationResult,
    ExperienceReuseExposure,
    ExperienceReuseFeedback,
    ExperienceReuseFeedbackResult,
    HumanReuseAssessment,
    Instant,
    RecordedExperienceReuseFeedback,
    ReuseMeasure,
    Scope,
)
from agentexperience.encoding import json_number

# ---- Reading -------------------------------------------------------------------------------------------------------


def _ids(value: list[str] | None) -> list[uuid.UUID] | None:
    return None if value is None else [uuid.UUID(item) for item in value]


def _scope(value: dict[str, Any] | None) -> Scope:
    return scope_of(value) if value is not None else None  # type: ignore[return-value]


def measure_of(value: dict[str, Any] | None) -> ReuseMeasure | None:
    if value is None:
        return None
    number = value["value"]
    if number == "NaN":
        parsed = math.nan
    elif number == "Infinity":
        parsed = math.inf
    elif number == "-Infinity":
        parsed = -math.inf
    else:
        parsed = float(number)
    return ReuseMeasure(value["kind"], parsed)


def feedback_of(value: dict[str, Any]) -> ExperienceReuseFeedback:
    human = value.get("humanAssessment")
    comparative = value.get("comparativeEvaluation")
    return ExperienceReuseFeedback(
        feedback_id=uuid.UUID(value["feedbackId"]),
        run_id=uuid.UUID(value["runId"]),
        scope=_scope(value["scope"]),
        exposed_experience_ids=_ids(value["exposedExperienceIds"]),
        run_outcome=value["runOutcome"],
        measure=measure_of(value["measure"]),
        observed_at=Instant.parse(value["observedAt"]),
        claimed_benefit=value["claimedBenefit"],
        human_assessment=None
        if human is None
        else HumanReuseAssessment(
            assessment_id=uuid.UUID(human["assessmentId"]),
            benefit=human["benefit"],
            attributed_experience_ids=_ids(human["attributedExperienceIds"]),
            rationale=human["rationale"],
            assessed_at=Instant.parse(human["assessedAt"]),
            verification_round_id=uuid_of(human["verificationRoundId"]),
            assessment_token=human["assessmentToken"],
        ),
        comparative_evaluation=None
        if comparative is None
        else ComparativeEvaluationResult(
            evaluator_id=comparative["evaluatorId"],
            run_id=uuid.UUID(comparative["runId"]),
            verification_round_id=uuid.UUID(comparative["verificationRoundId"]),
            benefit=comparative["benefit"],
            attributed_experience_ids=_ids(comparative["attributedExperienceIds"]),
            evidence=None
            if comparative["evidence"] is None
            else [None if item is None else evidence_of(item) for item in comparative["evidence"]],
            summary=comparative["summary"],
            evaluated_at=Instant.parse(comparative["evaluatedAt"]),
        ),
        trial_label=value["trialLabel"],
    )


def recorded_of(value: dict[str, Any]) -> RecordedExperienceReuseFeedback:
    exposures = value["exposures"]
    return RecordedExperienceReuseFeedback(
        feedback_id=uuid.UUID(value["feedbackId"]),
        run_id=uuid.UUID(value["runId"]),
        scope=_scope(value["scope"]),
        run_outcome=value["runOutcome"],
        claimed_benefit=value["claimedBenefit"],
        benefit=value["benefit"],
        attribution_source=value["attributionSource"],
        reviewer_identity=value["reviewerIdentity"],
        evaluator_id=value["evaluatorId"],
        verification_round_id=uuid_of(value["verificationRoundId"]),
        assessment_id=uuid_of(value["assessmentId"]),
        rationale=value["rationale"],
        evidence_ids=_ids(value["evidenceIds"]),
        attributed_at=None if value["attributedAt"] is None else Instant.parse(value["attributedAt"]),
        measure=measure_of(value["measure"]),
        trial_label=value["trialLabel"],
        observed_at=Instant.parse(value["observedAt"]),
        exposures=None
        if exposures is None
        else [
            None
            if item is None
            else ExperienceReuseExposure(
                uuid.UUID(item["experienceId"]), item["attributed"], uuid_of(item["evidenceId"])
            )
            for item in exposures
        ],
    )


# ---- Writing -------------------------------------------------------------------------------------------------------


def _id(value: uuid.UUID | None) -> str | None:
    return None if value is None else str(value)


def _scope_json(scope: Scope | None) -> dict[str, Any] | None:
    if scope is None:
        return None
    return {
        "tenantId": scope.tenant_id,
        "applicationId": scope.application_id,
        "projectId": scope.project_id,
        "teamId": scope.team_id,
        "agentId": scope.agent_id,
        "userId": scope.user_id,
    }


def _measure_json(measure: ReuseMeasure | None) -> dict[str, Any] | None:
    if measure is None:
        return None
    value: Any
    if math.isnan(measure.value):
        value = "NaN"
    elif math.isinf(measure.value):
        value = "Infinity" if measure.value > 0 else "-Infinity"
    else:
        value = json_number(measure.value)
    return {"kind": measure.kind, "value": value}


def recorded_json(feedback: RecordedExperienceReuseFeedback | None) -> dict[str, Any] | None:
    """A submission as the ledger stores it, in the fixtures' ``record`` form."""
    if feedback is None:
        return None
    return {
        "feedbackId": str(feedback.feedback_id),
        "runId": str(feedback.run_id),
        "scope": _scope_json(feedback.scope),
        "runOutcome": feedback.run_outcome,
        "claimedBenefit": feedback.claimed_benefit,
        "benefit": feedback.benefit,
        "attributionSource": feedback.attribution_source,
        "reviewerIdentity": feedback.reviewer_identity,
        "evaluatorId": feedback.evaluator_id,
        "verificationRoundId": _id(feedback.verification_round_id),
        "assessmentId": _id(feedback.assessment_id),
        "rationale": feedback.rationale,
        "evidenceIds": None if feedback.evidence_ids is None else [str(item) for item in feedback.evidence_ids],
        "attributedAt": None if feedback.attributed_at is None else feedback.attributed_at.format(),
        "measure": _measure_json(feedback.measure),
        "trialLabel": feedback.trial_label,
        "observedAt": feedback.observed_at.format(),
        "exposures": None
        if feedback.exposures is None
        else [
            None
            if exposure is None
            else {
                "experienceId": str(exposure.experience_id),
                "attributed": exposure.attributed,
                "evidenceId": _id(exposure.evidence_id),
            }
            for exposure in feedback.exposures
        ],
    }


def submit_result_json(result: ExperienceReuseFeedbackResult) -> dict[str, Any]:
    """What the service answered, in the fixtures' ``submit`` result form."""
    return {
        "thrown": None,
        "outcome": result.outcome,
        "feedbackId": str(result.feedback_id),
        "benefit": result.benefit,
        "attributionSource": result.attribution_source,
        "exposures": [
            {
                "experienceId": str(exposure.experience_id),
                "disposition": exposure.disposition,
                "evidenceId": _id(exposure.evidence_id),
                "counted": exposure.counted,
                "reuseConfidence": None
                if exposure.reuse_confidence is None
                else json_number(exposure.reuse_confidence),
                "status": exposure.status,
                "retryable": exposure.retryable,
                "reason": exposure.reason,
            }
            for exposure in result.exposures
        ],
        "errors": errors_json(result.errors),
        "reason": result.reason,
        "isRetryable": result.is_retryable,
    }


def thrown_json(error: BaseException) -> dict[str, Any]:
    """The ledger write's failure, as .NET names it (``ExperienceStoreException``)."""
    return {"thrown": {"exception": "ExperienceStoreException", "message": str(error)}}
