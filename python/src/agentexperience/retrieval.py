"""
The retrieval policy (contract 1.12): .NET's ``RetrievalPolicy`` and ``ConfidenceDecayPolicy``, with .NET's defaults and
validation. An invalid value raises :class:`RetrievalPolicyError` naming the .NET option, when the policy is built.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from datetime import timedelta
from types import MappingProxyType
from typing import Literal

from ._text import is_null_or_whitespace

TICKS_PER_MICROSECOND = 10
TICKS_PER_SECOND = 10_000_000

DEFAULT_TIMEOUT = timedelta(milliseconds=500)
MAX_TIMEOUT = timedelta(days=1)
DEFAULT_MINIMUM_CONFIDENCE = 0.5
DEFAULT_RECENCY_HALF_LIFE = timedelta(days=30)
DEFAULT_CANDIDATE_LIMIT = 50
MIN_CANDIDATE_LIMIT = 1
MAX_CANDIDATE_LIMIT = 199
"""One below the store's largest limit (200): retrieval asks for one more candidate than the limit to detect
truncation."""
DEFAULT_MAX_ABANDONED_SEARCHES = 16
DEFAULT_MINIMUM_MATCHED_TERMS_POLICY = 3
DEFAULT_DOMAIN_KEY = "domain"

RetrievalOutcome = Literal["Completed", "TimedOut", "Denied", "Failed"]
"""How a retrieval ended: .NET's ``RetrievalOutcome`` member names, the ``retrieve`` operation's outcomes."""

ExclusionReason = Literal["IneligibleStatus", "ModelAuthored", "Expired"]
"""Why ranking left a candidate out: .NET's ``RetrievalExclusionReason`` names this port can report (it has no required
environment attributes and no provenance signing, so never ``EnvironmentMismatch`` or ``UnconfirmedContent``)."""


class RetrievalPolicyError(ValueError):
    """An invalid retrieval policy or confidence decay value. ``option`` is the .NET option .NET's
    ``ArgumentException`` names (``Timeout``, ``MinimumConfidence``, ``HalfLives``, ...)."""

    def __init__(self, option: str, message: str) -> None:
        super().__init__(f"{option}: {message}")
        self.option = option


def ticks_of(value: timedelta) -> int:
    """A ``timedelta`` as .NET ticks (100 ns), exactly."""
    return (value // timedelta(microseconds=1)) * TICKS_PER_MICROSECOND


def timedelta_of_ticks(ticks: int) -> timedelta:
    """.NET ticks as a ``timedelta``; refuses ticks that are not whole microseconds rather than rounding them."""
    if ticks % TICKS_PER_MICROSECOND:
        raise ValueError(f"{ticks} ticks is not a whole number of microseconds.")
    return timedelta(microseconds=ticks // TICKS_PER_MICROSECOND)


def seconds_of(value: timedelta) -> float:
    """.NET's ``TimeSpan.TotalSeconds``: the ticks as a double divided by ticks per second."""
    return float(ticks_of(value)) / TICKS_PER_SECOND


def _positive(value: timedelta, option: str) -> None:
    if not isinstance(value, timedelta):
        raise TypeError(f"{option} must be a timedelta.")
    if not value > timedelta(0):
        raise RetrievalPolicyError(option, "retrieval durations must be strictly positive.")


def _count(value: int, option: str) -> None:
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{option} must be an int.")
    if value < 1:
        raise RetrievalPolicyError(option, "retrieval counts must be strictly positive.")


def _unit(value: float, option: str) -> None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{option} must be a number.")
    if not (0.0 <= value <= 1.0):  # NaN fails both comparisons
        raise RetrievalPolicyError(option, "must be between 0 and 1 inclusive.")


@dataclass(frozen=True)
class RetrievalPolicy:
    """
    The bounds and thresholds a retrieval runs under, as .NET's ``RetrievalPolicy``: ``timeout`` (``Timeout``) bounds
    the whole retrieval; ``minimum_confidence`` (``MinimumConfidence``) is the search's confidence floor; ``max_age``
    (``MaxAge``, ``None``: no expiry) excludes a record whose ``updated_at`` is older; ``recency_half_life``
    (``RecencyHalfLife``) is the recency component's half-life; ``candidate_limit`` (``CandidateLimit``) is how many
    candidates ranking considers (the search is asked for one more); ``max_abandoned_searches``
    (``MaxAbandonedSearches``) caps the searches abandoned on a timeout that are still running;
    ``minimum_matched_terms`` (``MinimumMatchedTerms``) is the text search's term minimum; ``minimum_vector_relevance``
    (``MinimumVectorRelevance``) floors the vector channel.
    """

    timeout: timedelta = DEFAULT_TIMEOUT
    minimum_confidence: float = DEFAULT_MINIMUM_CONFIDENCE
    max_age: timedelta | None = None
    recency_half_life: timedelta = DEFAULT_RECENCY_HALF_LIFE
    candidate_limit: int = DEFAULT_CANDIDATE_LIMIT
    max_abandoned_searches: int = DEFAULT_MAX_ABANDONED_SEARCHES
    minimum_matched_terms: int = DEFAULT_MINIMUM_MATCHED_TERMS_POLICY
    minimum_vector_relevance: float | None = None

    def __post_init__(self) -> None:
        _positive(self.timeout, "Timeout")
        if self.timeout > MAX_TIMEOUT:
            raise RetrievalPolicyError("Timeout", f"the retrieval timeout must be at most {MAX_TIMEOUT}.")
        _unit(self.minimum_confidence, "MinimumConfidence")
        if self.max_age is not None:
            _positive(self.max_age, "MaxAge")
        _positive(self.recency_half_life, "RecencyHalfLife")
        if isinstance(self.candidate_limit, bool) or not isinstance(self.candidate_limit, int):
            raise TypeError("CandidateLimit must be an int.")
        if not MIN_CANDIDATE_LIMIT <= self.candidate_limit <= MAX_CANDIDATE_LIMIT:
            raise RetrievalPolicyError(
                "CandidateLimit",
                f"the candidate limit must be between {MIN_CANDIDATE_LIMIT} and {MAX_CANDIDATE_LIMIT}.",
            )
        _count(self.max_abandoned_searches, "MaxAbandonedSearches")
        _count(self.minimum_matched_terms, "MinimumMatchedTerms")
        if self.minimum_vector_relevance is not None:
            _unit(self.minimum_vector_relevance, "MinimumVectorRelevance")

    @property
    def statement_limit(self) -> int:
        """The limit the search is asked for: one more than :attr:`candidate_limit`, to detect truncation."""
        return self.candidate_limit + 1


DEFAULT_POLICY = RetrievalPolicy()


def _frozen_half_lives(value: Mapping[str, timedelta | None]) -> Mapping[str, timedelta | None]:
    if not isinstance(value, Mapping):
        raise TypeError("HalfLives must be a mapping of domain to timedelta or None.")
    copy: dict[str, timedelta | None] = {}
    for domain, half_life in value.items():
        if not isinstance(domain, str) or is_null_or_whitespace(domain):
            raise RetrievalPolicyError("HalfLives", "a domain in the half-life map must be non-blank.")
        if half_life is not None:
            if not isinstance(half_life, timedelta):
                raise TypeError("HalfLives values must be timedelta or None.")
            if not half_life > timedelta(0):
                raise RetrievalPolicyError("HalfLives", "a confidence half-life must be strictly positive.")
        copy[domain] = half_life
    return MappingProxyType(copy)


@dataclass(frozen=True)
class ConfidenceDecayPolicy:
    """
    How a record's confidence fades at ranking time, by its domain, as .NET's ``ConfidenceDecayPolicy``: the domain is
    the record's environment metadata entry named ``domain_key`` (``DomainKey``), matched ordinally against
    ``half_lives`` (``HalfLives``; ``None`` for a domain that never decays); a record whose domain is missing or not
    listed uses ``default_half_life`` (``DefaultHalfLife``; ``None``: no decay). The map is copied, so a later change to
    the one passed in has no effect. Decay only reorders: it never excludes.
    """

    domain_key: str = DEFAULT_DOMAIN_KEY
    half_lives: Mapping[str, timedelta | None] = field(default_factory=dict)
    default_half_life: timedelta | None = None

    def __post_init__(self) -> None:
        if not isinstance(self.domain_key, str) or is_null_or_whitespace(self.domain_key):
            raise RetrievalPolicyError("DomainKey", "the domain key must be non-blank.")
        object.__setattr__(self, "half_lives", _frozen_half_lives(self.half_lives))
        if self.default_half_life is not None:
            if not isinstance(self.default_half_life, timedelta):
                raise TypeError("DefaultHalfLife must be a timedelta or None.")
            if not self.default_half_life > timedelta(0):
                raise RetrievalPolicyError("DefaultHalfLife", "a confidence half-life must be strictly positive.")

    def half_life_for(self, domain: str | None) -> timedelta | None:
        """The half-life for a record whose domain is ``domain``, or ``None`` when it does not decay."""
        if domain is not None and domain in self.half_lives:
            return self.half_lives[domain]
        return self.default_half_life

    def __hash__(self) -> int:
        return hash((self.domain_key, tuple(sorted(self.half_lives.items())), self.default_half_life))
