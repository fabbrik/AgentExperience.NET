"""Shared by the benefit tests: the contract's JSON forms of outcome rows and estimates (contracts 1.9 and 1.10)."""

from __future__ import annotations

import uuid
from typing import Any

from agentexperience import Instant
from agentexperience.benefit import (
    BenefitArm,
    BenefitComparison,
    BenefitEstimate,
    HoldoutOutcomeRow,
    LessonBenefit,
    ModelBenefit,
    ModelPairBenefit,
)

TOLERANCE = 1e-12
"""README-1.9: rate, difference, lower and upper compare within this absolute tolerance; everything else exactly."""


def row_of(value: dict[str, Any]) -> HoldoutOutcomeRow:
    return HoldoutOutcomeRow(
        uuid.UUID(value["runId"]),
        value["arm"],
        uuid.UUID(value["experienceId"]),
        Instant.parse(value["assignedAt"]),
        value["runOutcome"],
        value["runModel"],
        value["lessonModel"],
    )


def row_json(row: HoldoutOutcomeRow) -> dict[str, Any]:
    return {
        "runId": str(row.run_id),
        "arm": row.arm,
        "experienceId": str(row.experience_id),
        "assignedAt": row.assigned_at.format(),
        "runOutcome": row.run_outcome,
        "runModel": row.run_model,
        "lessonModel": row.lesson_model,
    }


def comparison_json(comparison: BenefitComparison) -> dict[str, Any]:
    def arm(value: Any) -> dict[str, Any]:
        return {"runs": value.runs, "successes": value.successes, "rate": value.rate}

    return {
        "treatment": arm(comparison.treatment),
        "holdout": arm(comparison.holdout),
        "difference": comparison.difference,
        "lower": comparison.lower,
        "upper": comparison.upper,
        "verdict": comparison.verdict,
        "unverified": comparison.unverified,
        "pending": comparison.pending,
        "mixedArm": comparison.mixed_arm,
    }


def estimate_json(estimate: BenefitEstimate, *, with_store: bool = False) -> dict[str, Any]:
    """The estimate as the benefit-estimate fixtures hold it; with ``with_store``, also ``tracked`` and ``outcome``."""
    value: dict[str, Any] = {
        "minimumRunsPerArm": estimate.minimum_runs_per_arm,
        "overall": comparison_json(estimate.overall),
        "lessons": [
            {
                "experienceId": str(lesson.experience_id),
                "comparison": comparison_json(lesson.comparison),
                "byModelPair": None
                if lesson.by_model_pair is None
                else [
                    {
                        "lessonModel": pair.lesson_model,
                        "runModel": pair.run_model,
                        "comparison": comparison_json(pair.comparison),
                    }
                    for pair in lesson.by_model_pair
                ],
            }
            for lesson in estimate.lessons
        ],
        "byConsumingModel": None
        if estimate.by_consuming_model is None
        else [
            {"model": model.model, "comparison": comparison_json(model.comparison)}
            for model in estimate.by_consuming_model
        ],
    }
    if with_store:
        value["tracked"] = estimate.tracked
        value["outcome"] = estimate.outcome
    return value


def comparison_of(value: dict[str, Any]) -> BenefitComparison:
    def arm(item: dict[str, Any]) -> BenefitArm:
        return BenefitArm(item["runs"], item["successes"])

    return BenefitComparison(
        arm(value["treatment"]),
        arm(value["holdout"]),
        value["difference"],
        value["lower"],
        value["upper"],
        value["verdict"],
        value["unverified"],
        value["pending"],
        value["mixedArm"],
    )


def estimate_of(value: dict[str, Any]) -> BenefitEstimate:
    """An estimate read back from its contract JSON form (contract 1.10's ``estimate.json``), tracked and ``Found``
    unless the JSON says otherwise."""
    return BenefitEstimate(
        comparison_of(value["overall"]),
        tuple(
            LessonBenefit(
                uuid.UUID(lesson["experienceId"]),
                comparison_of(lesson["comparison"]),
                None
                if lesson["byModelPair"] is None
                else tuple(
                    ModelPairBenefit(pair["lessonModel"], pair["runModel"], comparison_of(pair["comparison"]))
                    for pair in lesson["byModelPair"]
                ),
            )
            for lesson in value["lessons"]
        ),
        None
        if value["byConsumingModel"] is None
        else tuple(
            ModelBenefit(item["model"], comparison_of(item["comparison"])) for item in value["byConsumingModel"]
        ),
        value.get("tracked", True),
        value.get("outcome", "Found"),
        value["minimumRunsPerArm"],
    )
