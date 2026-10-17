"""Verification aggregation, rule version ``1.0.0`` (contract step 2)."""

from __future__ import annotations

import uuid
from collections.abc import Sequence
from dataclasses import dataclass

from ._text import is_null_or_whitespace
from .encoding import Instant
from .observer import observe
from .records import CheckResult, Evidence, Outcome

RULE_VERSION = "1.0.0"
ANY_KIND = "*"


@dataclass(frozen=True)
class ClosedVerificationRound:
    """The verification round the host closed, for one artifact revision."""

    round_id: uuid.UUID
    artifact_revision: str


@dataclass(frozen=True)
class RequiredCheck:
    """A check the run must pass, and the evidence kind that satisfies it (``*`` accepts any)."""

    check_id: str
    expected_kind: str

    def accepts(self, kind: str) -> bool:
        return self.expected_kind == ANY_KIND or self.expected_kind == kind


@dataclass(frozen=True)
class VerificationResult:
    outcome: Outcome
    completion_score: float
    rule_version: str
    closed_round: ClosedVerificationRound | None
    required_checks: tuple[RequiredCheck, ...]


def aggregate(
    evidence: Sequence[Evidence],
    required_checks: Sequence[RequiredCheck],
    closed_round: ClosedVerificationRound | None,
    current_artifact_revision: str,
    evaluated_at: Instant,
) -> VerificationResult:
    """Aggregates the evidence of the closed round and the current artifact revision into an outcome."""
    with observe("verify") as op:
        result = _aggregate(evidence, required_checks, closed_round, current_artifact_revision, evaluated_at)
        if op is not None:
            op.outcome(result.outcome.status)
        return result


def _aggregate(
    evidence: Sequence[Evidence],
    required_checks: Sequence[RequiredCheck],
    closed_round: ClosedVerificationRound | None,
    current_artifact_revision: str,
    evaluated_at: Instant,
) -> VerificationResult:
    if is_null_or_whitespace(current_artifact_revision):
        raise ValueError("The current artifact revision must not be blank.")
    checks = tuple(required_checks)
    if any(is_null_or_whitespace(check.check_id) for check in checks):
        raise ValueError("Required check IDs must not be blank.")
    if any(is_null_or_whitespace(check.expected_kind) for check in checks):
        raise ValueError(f"Every required check must name the evidence kind that satisfies it (or '{ANY_KIND}').")
    if len({check.check_id for check in checks}) != len(checks):
        raise ValueError("Required check IDs must be unique.")

    def unknown(reason: str) -> VerificationResult:
        return VerificationResult(Outcome("Unknown", (), reason, evaluated_at), 0.0, RULE_VERSION, closed_round, checks)

    if closed_round is None:
        return unknown("No verification round has been closed by the host; verification is unclosed.")
    if closed_round.artifact_revision != current_artifact_revision:
        return unknown(
            f"The host-closed verification round applies to artifact revision '{closed_round.artifact_revision}', "
            f"which does not match the current artifact revision '{current_artifact_revision}'; verification is stale."
        )
    if not checks:
        return unknown(
            "No required checks were declared for this task; an empty required set can never be conclusively verified."
        )

    selected = [
        item
        for item in evidence
        if item.verification_round_id == closed_round.round_id and item.artifact_revision == current_artifact_revision
    ]
    contributing: set[uuid.UUID] = set()
    passing = 0
    failed: list[str] = []
    unknown_ids: list[str] = []
    for check in checks:
        matching = [item for item in selected if item.check_id == check.check_id and check.accepts(item.kind)]
        contributing.update(item.evidence_id for item in matching)
        resolved = _resolve(matching)
        if resolved == "Fail":
            failed.append(check.check_id)
        elif resolved == "Unknown":
            unknown_ids.append(check.check_id)
        else:
            passing += 1

    score = passing / len(checks)
    backing = tuple(item for item in selected if item.evidence_id in contributing)
    if failed:
        reason = (
            f"Required check(s) resolved to Fail in the host-closed verification round: {', '.join(failed)}; "
            "a Fail always dominates a Pass recorded for the same check."
        )
        return VerificationResult(
            Outcome("Failed", backing, reason, evaluated_at), score, RULE_VERSION, closed_round, checks
        )
    if unknown_ids:
        reason = (
            "Required check(s) have no conclusive Pass/Fail evidence (missing, errored, or genuinely inconclusive) in "
            f"the host-closed verification round: {', '.join(unknown_ids)}."
        )
        return VerificationResult(
            Outcome("Unknown", backing, reason, evaluated_at), score, RULE_VERSION, closed_round, checks
        )
    reason = (
        "Every required check conclusively resolved to Pass in the host-closed verification round for the current "
        "artifact revision."
    )
    return VerificationResult(
        Outcome("Verified", backing, reason, evaluated_at), score, RULE_VERSION, closed_round, checks
    )


def _resolve(evidence: Sequence[Evidence]) -> CheckResult:
    if not evidence:
        return "Unknown"
    if any(item.result == "Fail" for item in evidence):
        return "Fail"
    return "Pass" if all(item.result == "Pass" for item in evidence) else "Unknown"
