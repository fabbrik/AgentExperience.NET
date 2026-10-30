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
from .retrieval import DEFAULT_POLICY, ConfidenceDecayPolicy, ExclusionReason, RetrievalPolicy, seconds_of, ticks_of
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

    def permits(self, scope: Scope) -> bool:
        """Whether these bounds permit ``scope``: the tenant always, and every bounded field exactly."""
        pairs = (
            (self.application_id, scope.application_id),
            (self.project_id, scope.project_id),
            (self.team_id, scope.team_id),
            (self.agent_id, scope.agent_id),
            (self.user_id, scope.user_id),
        )
        return self.tenant_id == scope.tenant_id and all(bound is None or bound == value for bound, value in pairs)

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

    @staticmethod
    def for_policy(
        scope: Scope,
        task_text: str,
        policy: RetrievalPolicy | None = None,
        *,
        exclude_model_authored: bool = False,
    ) -> SearchParameters:
        """The search a retrieval under ``policy`` (contract 1.12; ``None``: the default policy) runs: its confidence
        floor, ``candidate_limit + 1`` and its minimum of matched terms."""
        policy = policy or DEFAULT_POLICY
        return SearchParameters(
            scope,
            task_text,
            min_confidence=policy.minimum_confidence,
            limit=policy.statement_limit,
            exclude_model_authored=exclude_model_authored,
            minimum_matched_terms=policy.minimum_matched_terms,
        )


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
class ExcludedRecord:
    """A candidate an eligibility check left out before ranking, and why (contract 1.12): .NET's
    ``ExcludedExperience``."""

    experience_id: uuid.UUID
    reason: ExclusionReason


@dataclass(frozen=True)
class RankedResult:
    """The ranked records, best first; whether a channel returned more candidates than the candidate limit; and the
    candidates ranking excluded, in order, with their reasons (contract 1.12)."""

    ranked: tuple[RankedRecord, ...]
    truncated: bool
    excluded: tuple[ExcludedRecord, ...] = ()


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


def _decay(since: Instant, now: Instant, half_life_seconds: float) -> float:
    """``2^(-age / halfLife)``, clamped: 1 when ``since`` is now or (clock skew) later."""
    age = now - since
    if age.ticks <= 0:
        return 1.0
    return _normalize(math.pow(2.0, -age.total_seconds / half_life_seconds))


def _recency(updated_at: Instant, now: Instant, half_life_seconds: float = RECENCY_HALF_LIFE_SECONDS) -> float:
    return _decay(updated_at, now, half_life_seconds)


def _confidence(record: ExperienceRecord, now: Instant, decay: ConfidenceDecayPolicy | None) -> RankingComponent:
    """The stored confidence, or, when ``decay`` gives the record's domain a half-life, the stored confidence times
    ``2^(-age / halfLife)`` (age from ``created_at``) with the stored value as ``undecayed_value``."""
    stored = _normalize(record.reuse_confidence)
    if decay is None:
        return RankingComponent("Confidence", stored, WEIGHT_CONFIDENCE)
    half_life = decay.half_life_for(record.environment.metadata.get(decay.domain_key))
    if half_life is None:
        return RankingComponent("Confidence", stored, WEIGHT_CONFIDENCE)
    factor = _decay(record.created_at, now, seconds_of(half_life))
    return RankingComponent("Confidence", _normalize(stored * factor), WEIGHT_CONFIDENCE, undecayed_value=stored)


def _status_score(status: str) -> float:
    return {"Reinforced": REINFORCED_STATUS_SCORE, "Validated": VALIDATED_STATUS_SCORE}.get(status, 0.0)


def score(
    row: SearchRow,
    now: Instant,
    preferred_environment: Mapping[str, str],
    *,
    policy: RetrievalPolicy | None = None,
    confidence_decay: ConfidenceDecayPolicy | None = None,
) -> RankedRecord:
    """Scores one row with the default weights, components in the contract's order; the recency half-life is the
    policy's, and the confidence decays under ``confidence_decay`` (contract 1.12)."""
    record = row.record
    policy = policy or DEFAULT_POLICY
    components = (
        RankingComponent("Relevance", _normalize(row.relevance), WEIGHT_RELEVANCE),
        _confidence(record, now, confidence_decay),
        RankingComponent(
            "Recency", _recency(record.updated_at, now, seconds_of(policy.recency_half_life)), WEIGHT_RECENCY
        ),
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


def exclusion(
    record: ExperienceRecord, now: Instant, policy: RetrievalPolicy, *, exclude_model_authored: bool
) -> ExclusionReason | None:
    """Why ranking leaves ``record`` out, the first rule that applies in .NET's order, or ``None``: a status that is
    not eligible, a model-authored lesson when they are excluded, a last activity older than ``max_age``."""
    if record.status not in ELIGIBLE_STATUSES:
        return "IneligibleStatus"
    if exclude_model_authored and is_model_authored(record.reflection):
        return "ModelAuthored"
    if policy.max_age is not None and (now - record.updated_at).ticks > ticks_of(policy.max_age):
        return "Expired"
    return None


def check_rows(rows: Sequence[SearchRow], scope: Scope, candidate_limit: int = CANDIDATE_LIMIT) -> list[SearchRow]:
    """The safety checks over a channel's first ``candidate_limit`` rows (raising :class:`SearchSafetyError`): one
    outside ``scope`` or a repeated ``experience_id`` fails the result. Returns those rows."""
    considered = list(rows[:candidate_limit])
    seen: set[uuid.UUID] = set()
    for row in considered:
        if row.shared_by_grant or row.record.scope != scope:
            raise SearchSafetyError("A row lies outside the requested scope; nothing is returned.")
        if row.record.experience_id in seen:
            raise SearchSafetyError("The same record came back more than once; nothing is returned.")
        seen.add(row.record.experience_id)
    return considered


def rank_checked(
    considered: Sequence[SearchRow],
    now: Instant,
    preferred_environment: Mapping[str, str] | None,
    *,
    exclude_model_authored: bool,
    policy: RetrievalPolicy | None,
    confidence_decay: ConfidenceDecayPolicy | None,
) -> tuple[tuple[RankedRecord, ...], tuple[ExcludedRecord, ...]]:
    """Eligibility then scoring and ordering over rows that already passed the checks: the ranked records and the
    exclusions, in the rows' order."""
    policy = policy or DEFAULT_POLICY
    preferred = preferred_environment or {}
    ranked: list[RankedRecord] = []
    excluded: list[ExcludedRecord] = []
    for row in considered:
        reason = exclusion(row.record, now, policy, exclude_model_authored=exclude_model_authored)
        if reason is not None:
            excluded.append(ExcludedRecord(row.record.experience_id, reason))
            continue
        ranked.append(score(row, now, preferred, policy=policy, confidence_decay=confidence_decay))

    # Ordered by score descending, then by experience_id as text, ascending, ordinal (a stable sort, applied twice).
    ranked.sort(key=lambda entry: str(entry.record.experience_id))
    ranked.sort(key=lambda entry: entry.score, reverse=True)
    return tuple(ranked), tuple(excluded)


def rank(
    rows: Sequence[SearchRow],
    scope: Scope,
    now: Instant,
    preferred_environment: Mapping[str, str] | None = None,
    *,
    exclude_model_authored: bool = False,
    policy: RetrievalPolicy | None = None,
    confidence_decay: ConfidenceDecayPolicy | None = None,
) -> RankedResult:
    """
    From rows to ranked records: the safety checks over the first ``candidate_limit`` rows (50 by default; raising
    :class:`SearchSafetyError`), truncation to them, eligibility (each exclusion reported with its reason), then
    scoring and ordering by score, descending, ties by ``experience_id`` as text. ``policy`` and ``confidence_decay``
    are contract 1.12's (``None``: the default policy, no decay, which ranks exactly as contract 1.0).
    """
    limit = (policy or DEFAULT_POLICY).candidate_limit
    considered = check_rows(rows, scope, limit)
    ranked, excluded = rank_checked(
        considered,
        now,
        preferred_environment,
        exclude_model_authored=exclude_model_authored,
        policy=policy,
        confidence_decay=confidence_decay,
    )
    return RankedResult(ranked, truncated=len(rows) > limit, excluded=excluded)
