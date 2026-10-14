"""
An in-memory store for development and tests: no PostgreSQL. It keeps only what the contract needs (insert a finalized
record, read one back, text search, and when each record was last injected), honouring scope and the authorization
bounds the way the PostgreSQL store's row-level security does. Its text search is the .NET in-memory store's:
Unicode-normalized, case-folded words, English stopwords dropped from a query, and a record matching at least the
minimum number of query terms (fewer for a shorter query). Nothing survives the process.
"""

from __future__ import annotations

import threading
import unicodedata
import uuid
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from typing import Protocol, runtime_checkable

from .encoding import Instant
from .finalize import FinalizedExperience, InitialLifecycleEvent
from .records import ExperienceRecord, Scope
from .reflector import is_model_authored
from .search import AuthorizationBounds, SearchParameters, SearchRow

MAX_INDEXED_LENGTH = 100_000

_SUMMARY_WEIGHT = 3
_TASK_ID_WEIGHT = 1
_LESSON_WEIGHT = 1
_TERM_WEIGHT = _SUMMARY_WEIGHT + _TASK_ID_WEIGHT + _LESSON_WEIGHT

_STOPWORDS = frozenset(
    """
    i me my myself we our ours ourselves you your yours yourself yourselves he him his himself she her hers herself it
    its itself they them their theirs themselves what which who whom this that these those am is are was were be been
    being have has had having do does did doing a an the and but if or because as until while of at by for with about
    against between into through during before after above below to from up down in out on off over under again further
    then once here there when where why how all any both each few more most other some such no nor not only own same so
    than too very s t can will just don should now
    """.split()
)


class ExperienceStore(Protocol):
    """What the agent integration needs of a store: write a finalized record, and run the text search."""

    def insert_finalized(self, bounds: AuthorizationBounds, finalized: FinalizedExperience, *, actor: str) -> None: ...

    def search(self, bounds: AuthorizationBounds, parameters: SearchParameters) -> list[SearchRow]: ...


@runtime_checkable
class InjectionRecorder(Protocol):
    """
    A store that records when each record was injected (contract 1.4), for the .NET lessons view's "last injected".
    Optional: the agent integration records an injection only through a store that has this method, and reports
    ``NotTracked`` for one that does not.
    """

    def record_injection(
        self,
        bounds: AuthorizationBounds,
        scope: Scope,
        experience_ids: Sequence[uuid.UUID],
        injected_at: Instant,
    ) -> None: ...


MAX_RECORD_INJECTION_IDS = 200
"""The most records one :meth:`InjectionRecorder.record_injection` call names: the .NET store's limit
(``ExperienceRecordGetManyResult.MaxCount``). The agent integration splits a longer list into chunks."""

MAX_INJECTION_COUNT = 2**63 - 1
"""The count an injection row saturates at: PostgreSQL's ``bigint`` maximum."""


def injection_ids(experience_ids: Sequence[uuid.UUID]) -> list[uuid.UUID]:
    """The distinct IDs to record, in order; ``ValueError`` for more than :data:`MAX_RECORD_INJECTION_IDS` IDs or a
    nil one."""
    ids = list(experience_ids)
    if len(ids) > MAX_RECORD_INJECTION_IDS:
        raise ValueError(f"An injection record names at most {MAX_RECORD_INJECTION_IDS} records.")
    for experience_id in ids:
        if not isinstance(experience_id, uuid.UUID) or experience_id.int == 0:
            raise ValueError("Every injected record's ID must be a non-nil UUID.")
    return list(dict.fromkeys(ids))


@dataclass(frozen=True)
class InjectionSummary:
    """When a record was last injected in its own scope, and how many injected blocks delivered it."""

    last_injected_at: Instant
    injection_count: int


class AuthorizationDenied(PermissionError):
    """The scope lies outside the authorization bounds."""


def permits(bounds: AuthorizationBounds, scope: Scope) -> bool:
    """Whether ``bounds`` permit ``scope``: the tenant always, and every bounded field exactly."""
    pairs = (
        (bounds.application_id, scope.application_id),
        (bounds.project_id, scope.project_id),
        (bounds.team_id, scope.team_id),
        (bounds.agent_id, scope.agent_id),
        (bounds.user_id, scope.user_id),
    )
    return bounds.tenant_id == scope.tenant_id and all(bound is None or bound == value for bound, value in pairs)


def _words(text: str) -> list[str]:
    """Normalized to Unicode form KC and lower-cased, then split into runs of letters and digits; a combining mark
    continues the word it follows."""
    words: list[str] = []
    word: list[str] = []
    for ch in unicodedata.normalize("NFKC", text).lower():
        category = unicodedata.category(ch)
        if category in ("Lu", "Ll", "Lt", "Lm", "Lo", "Nd"):
            word.append(ch)
        elif word and category in ("Mn", "Mc"):
            word.append(ch)
        elif word:
            words.append("".join(word))
            word = []
    if word:
        words.append("".join(word))
    return words


def query_terms(text: str) -> list[str]:
    """The distinct terms a query searches for: its words, less stopwords and words shorter than two characters."""
    return list(dict.fromkeys(word for word in _words(text) if len(word) >= 2 and word not in _STOPWORDS))


@dataclass(frozen=True)
class _Indexed:
    record: ExperienceRecord
    task_id: frozenset[str]
    summary: frozenset[str]
    lesson: frozenset[str]

    def contains(self, term: str) -> bool:
        return term in self.summary or term in self.task_id or term in self.lesson

    def relevance(self, terms: list[str]) -> float:
        score = sum(
            (_SUMMARY_WEIGHT if term in self.summary else 0)
            + (_TASK_ID_WEIGHT if term in self.task_id else 0)
            + (_LESSON_WEIGHT if term in self.lesson else 0)
            for term in terms
        )
        return score / (_TERM_WEIGHT * len(terms))


def _index(record: ExperienceRecord) -> _Indexed:
    budget = MAX_INDEXED_LENGTH
    fields: list[frozenset[str]] = []
    for text in (record.task_id, record.task_summary, record.reflection.lesson if record.reflection else None):
        if not text or budget <= 0:
            budget -= 1
            fields.append(frozenset())
            continue
        taken = text[:budget]
        budget -= len(taken) + 1
        fields.append(frozenset(_words(taken)))
    return _Indexed(record, fields[0], fields[1], fields[2])


class InMemoryExperienceStore:
    """Records in memory, for development and tests. Thread-safe. ``clock`` is the store's own clock, which an
    injection's instant is clamped to, as PostgreSQL clamps it to its own."""

    def __init__(self, records: Iterable[ExperienceRecord] = (), *, clock: Callable[[], Instant] = Instant.now) -> None:
        self._lock = threading.Lock()
        self._clock = clock
        self._records: dict[uuid.UUID, _Indexed] = {}
        self._events: dict[uuid.UUID, tuple[InitialLifecycleEvent, str]] = {}
        self._injections: dict[uuid.UUID, InjectionSummary] = {}
        for record in records:
            if record.experience_id in self._records:
                raise ValueError("Two records share one experience ID.")
            self._records[record.experience_id] = _index(record)

    def insert_finalized(self, bounds: AuthorizationBounds, finalized: FinalizedExperience, *, actor: str) -> None:
        """Writes a finalized record and its initial lifecycle event. Raises :class:`AuthorizationDenied` outside the
        bounds, and ``ValueError`` for a record already stored."""
        if not actor or not actor.strip():
            raise ValueError("The actor (the authorizing principal) must not be blank.")
        record = finalized.record
        if not permits(bounds, record.scope):
            raise AuthorizationDenied("The record's scope lies outside the authorization bounds.")
        with self._lock:
            if record.experience_id in self._records:
                raise ValueError("A record with this experience ID is already stored.")
            self._records[record.experience_id] = _index(record)
            self._events[record.experience_id] = (finalized.event, actor)

    def get(self, bounds: AuthorizationBounds, scope: Scope, experience_id: uuid.UUID) -> ExperienceRecord | None:
        """One record in exactly ``scope``; ``None`` when there is none, or the bounds do not permit it."""
        if not permits(bounds, scope):
            return None
        with self._lock:
            indexed = self._records.get(experience_id)
        return indexed.record if indexed is not None and indexed.record.scope == scope else None

    def records(self) -> list[ExperienceRecord]:
        """Every record held, for inspection in tests."""
        with self._lock:
            return [indexed.record for indexed in self._records.values()]

    def record_injection(
        self,
        bounds: AuthorizationBounds,
        scope: Scope,
        experience_ids: Sequence[uuid.UUID],
        injected_at: Instant,
    ) -> None:
        """
        Records that ``experience_ids`` were injected in ``scope`` at ``injected_at``, as ``sql/record-injection.sql``
        does: only a record held in exactly ``scope`` counts, the instant (truncated to whole microseconds) is clamped
        to the store's clock and only moves forward, and the count rises by one and saturates. Raises
        :class:`AuthorizationDenied` outside the bounds, and ``ValueError`` for more than
        :data:`MAX_RECORD_INJECTION_IDS` IDs or a nil one.
        """
        ids = injection_ids(experience_ids)
        if not isinstance(injected_at, Instant):
            raise TypeError("injected_at must be an Instant.")
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        at = min(injected_at.truncate_to_microseconds(), self._clock().truncate_to_microseconds())
        with self._lock:
            for experience_id in ids:
                indexed = self._records.get(experience_id)
                if indexed is None or indexed.record.scope != scope:
                    continue
                current = self._injections.get(experience_id)
                self._injections[experience_id] = (
                    InjectionSummary(at, 1)
                    if current is None
                    else InjectionSummary(
                        max(current.last_injected_at, at), min(current.injection_count + 1, MAX_INJECTION_COUNT)
                    )
                )

    def injections(self) -> dict[uuid.UUID, InjectionSummary]:
        """When each record was last injected and how often, for inspection in tests."""
        with self._lock:
            return dict(self._injections)

    def search(self, bounds: AuthorizationBounds, parameters: SearchParameters) -> list[SearchRow]:
        """The text search: records in exactly the requested scope, of an eligible status and confidence, matching
        enough query terms; by relevance, then ``experience_id``, at most ``limit``. Nothing outside the bounds."""
        scope = parameters.scope
        if not permits(bounds, scope):
            return []
        terms = query_terms(parameters.task_text)
        if not terms:
            return []
        required = min(parameters.minimum_matched_terms, len(terms))
        statuses = set(parameters.statuses)
        with self._lock:
            held = list(self._records.values())
        matches = [
            SearchRow(indexed.record, indexed.relevance(terms))
            for indexed in held
            if indexed.record.scope == scope
            and indexed.record.status in statuses
            and indexed.record.reuse_confidence >= parameters.min_confidence
            and not (parameters.exclude_model_authored and is_model_authored(indexed.record.reflection))
            and sum(1 for term in terms if indexed.contains(term)) >= required
        ]
        matches.sort(key=lambda row: str(row.record.experience_id))
        matches.sort(key=lambda row: row.relevance, reverse=True)
        return matches[: parameters.limit]
