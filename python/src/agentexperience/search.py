"""
Text retrieval: the parameters of the contract's two SQL statements, and how the rows they return are checked,
truncated, filtered and ranked.
"""

from __future__ import annotations

import math
import struct
import uuid
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any

from ._text import is_null_or_whitespace, utf16_len
from .block import RankedRecord, RankingComponent
from .encoding import Instant
from .model import without_model
from .records import ExperienceRecord, ExperienceStatus, Scope
from .reflector import is_model_authored
from .task_text import MAX_TASK_TEXT_LENGTH

ELIGIBLE_STATUSES: tuple[ExperienceStatus, ...] = ("Validated", "Reinforced")
DEFAULT_MIN_CONFIDENCE = 0.5
CANDIDATE_LIMIT = 50
STATEMENT_LIMIT = CANDIDATE_LIMIT + 1
DEFAULT_MINIMUM_MATCHED_TERMS = 3
RECENCY_HALF_LIFE_SECONDS = 30 * 86_400.0

WEIGHT_RELEVANCE = 0.35
WEIGHT_CONFIDENCE = 0.25
WEIGHT_RECENCY = 0.15
WEIGHT_STATUS = 0.15
WEIGHT_ENVIRONMENT = 0.10
REINFORCED_STATUS_SCORE = 1.0
VALIDATED_STATUS_SCORE = 0.5


@dataclass(frozen=True)
class AuthorizationBounds:
    """
    The host's authorization bounds, which every transaction declares first: the tenant always, and each other field
    bound to a value or (``None``) unbounded. Never a request's scope.
    """

    tenant_id: str
    application_id: str | None = None
    project_id: str | None = None
    team_id: str | None = None
    agent_id: str | None = None
    user_id: str | None = None

    @staticmethod
    def of(scope: Scope) -> AuthorizationBounds:
        """Bounds equal to a scope: its unset optional fields are left unbounded."""
        return AuthorizationBounds(
            scope.tenant_id, scope.application_id, scope.project_id, scope.team_id, scope.agent_id, scope.user_id
        )

    def parameters(self) -> dict[str, str]:
        """``sql/declare-bounds.sql``'s parameters: ``=`` and the value, or the empty string for no bound."""

        def bound(value: str | None) -> str:
            return "" if value is None else "=" + value

        return {
            "auth_tenant": "=" + self.tenant_id,
            "auth_application": bound(self.application_id),
            "auth_project": bound(self.project_id),
            "auth_team": bound(self.team_id),
            "auth_agent": bound(self.agent_id),
            "auth_user": bound(self.user_id),
        }


@dataclass(frozen=True)
class SearchParameters:
    """``sql/search-experience-text.sql``'s parameters."""

    scope: Scope
    task_text: str
    statuses: tuple[ExperienceStatus, ...] = ELIGIBLE_STATUSES
    min_confidence: float = DEFAULT_MIN_CONFIDENCE
    limit: int = STATEMENT_LIMIT
    exclude_model_authored: bool = False
    with_grants: bool = False
    minimum_matched_terms: int = DEFAULT_MINIMUM_MATCHED_TERMS

    def __post_init__(self) -> None:
        if is_null_or_whitespace(self.task_text):
            raise ValueError("The task text must not be blank.")
        if utf16_len(self.task_text) > MAX_TASK_TEXT_LENGTH:
            raise ValueError(
                f"The task text is longer than {MAX_TASK_TEXT_LENGTH} UTF-16 code units; it is refused, not cut."
            )
        if self.with_grants:
            raise ValueError("Grants are outside contract 1.x: with_grants must be false.")
        if self.limit < 1 or self.minimum_matched_terms < 1:
            raise ValueError("limit and minimum_matched_terms must be strictly positive.")

    def parameters(self) -> dict[str, Any]:
        scope = self.scope
        return {
            "tenant_id": scope.tenant_id,
            "application_id": scope.application_id,
            "project_id": scope.project_id,
            "team_id": scope.team_id,
            "agent_id": scope.agent_id,
            "user_id": scope.user_id,
            "task_text": self.task_text,
            "statuses": list(self.statuses),
            "min_confidence": float(self.min_confidence),
            "limit": int(self.limit),
            "exclude_model_authored": bool(self.exclude_model_authored),
            "with_grants": bool(self.with_grants),
            "minimum_matched_terms": int(self.minimum_matched_terms),
        }


def widen_real(value: float) -> float:
    """
    A ``real`` (float4) column as the double ranking uses: the float4 value widened exactly (``0.6`` is
    ``0.6000000238418579``). A driver that read the column through its shortest decimal text gives back a double that
    rounds to the same float4, so this recovers that float4 and widens it without rounding through decimal text.
    """
    return float(struct.unpack("<f", struct.pack("<f", value))[0])


@dataclass(frozen=True)
class SearchRow:
    """One row the text search returned: the record, and its relevance widened to a double."""

    record: ExperienceRecord
    relevance: float
    shared_by_grant: bool = False


@dataclass(frozen=True)
class RankedResult:
    """The ranked records, best first, and whether the statement returned more rows than the candidate limit."""

    ranked: tuple[RankedRecord, ...]
    truncated: bool


class SearchSafetyError(Exception):
    """The rows failed a safety check: a row outside the requested scope, or one record twice. Nothing is returned."""


def environment_compatibility(metadata: Mapping[str, str], preferred: Mapping[str, str]) -> float:
    """The share of preferred attributes the record's metadata matches exactly; 1 with none preferred. The run's model
    (contract 1.9) is ignored on both sides: it is bookkeeping, never graded."""
    preferred = without_model(preferred)
    metadata = without_model(metadata)
    if not preferred:
        return 1.0
    matched = sum(1 for key, value in preferred.items() if metadata.get(key) == value)
    return matched / len(preferred)


def _normalize(value: float) -> float:
    return 0.0 if math.isnan(value) else min(max(value, 0.0), 1.0)


def _recency(updated_at: Instant, now: Instant) -> float:
    age = now - updated_at
    if age.ticks <= 0:
        return 1.0
    return _normalize(math.pow(2.0, -age.total_seconds / RECENCY_HALF_LIFE_SECONDS))


def _status_score(status: str) -> float:
    return {"Reinforced": REINFORCED_STATUS_SCORE, "Validated": VALIDATED_STATUS_SCORE}.get(status, 0.0)


def score(row: SearchRow, now: Instant, preferred_environment: Mapping[str, str]) -> RankedRecord:
    """Scores one row with the default weights, components in the contract's order."""
    record = row.record
    components = (
        RankingComponent("Relevance", _normalize(row.relevance), WEIGHT_RELEVANCE),
        RankingComponent("Confidence", _normalize(record.reuse_confidence), WEIGHT_CONFIDENCE),
        RankingComponent("Recency", _recency(record.updated_at, now), WEIGHT_RECENCY),
        RankingComponent("Status", _status_score(record.status), WEIGHT_STATUS),
        RankingComponent(
            "EnvironmentCompatibility",
            _normalize(environment_compatibility(record.environment.metadata, preferred_environment)),
            WEIGHT_ENVIRONMENT,
        ),
    )
    total = 0.0
    for component in components:
        total += component.contribution
    return RankedRecord(record, total, components)


def rank(
    rows: Sequence[SearchRow],
    scope: Scope,
    now: Instant,
    preferred_environment: Mapping[str, str] | None = None,
    *,
    exclude_model_authored: bool = False,
) -> RankedResult:
    """
    From rows to ranked records: the safety checks over the first 50 rows (raising :class:`SearchSafetyError`),
    truncation to 50, filtering, then scoring and ordering by score, descending, ties by ``experience_id`` as text.
    """
    considered = list(rows[:CANDIDATE_LIMIT])
    seen: set[uuid.UUID] = set()
    for row in considered:
        if row.shared_by_grant or row.record.scope != scope:
            raise SearchSafetyError("A row lies outside the requested scope; nothing is returned.")
        if row.record.experience_id in seen:
            raise SearchSafetyError("The same record came back more than once; nothing is returned.")
        seen.add(row.record.experience_id)

    preferred = preferred_environment or {}
    ranked: list[RankedRecord] = []
    for row in considered:
        if row.record.status not in ELIGIBLE_STATUSES:
            continue
        if exclude_model_authored and is_model_authored(row.record.reflection):
            continue
        ranked.append(score(row, now, preferred))

    # Ordered by score descending, then by experience_id as text, ascending, ordinal (a stable sort, applied twice).
    ranked.sort(key=lambda entry: str(entry.record.experience_id))
    ranked.sort(key=lambda entry: entry.score, reverse=True)
    return RankedResult(tuple(ranked), truncated=len(rows) > CANDIDATE_LIMIT)
