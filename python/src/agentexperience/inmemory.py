"""
An in-memory store for development and tests: no PostgreSQL. It keeps only what the contract needs (insert a finalized
record, read one back, text search, and when each record was last injected), honouring scope and the authorization
bounds the way the PostgreSQL store's row-level security does. Its text search is the .NET in-memory store's:
Unicode-normalized, case-folded words, English stopwords dropped from a query, and a record matching at least the
minimum number of query terms (fewer for a shorter query). Nothing survives the process.
"""

from __future__ import annotations

import dataclasses
import math
import threading
import unicodedata
import uuid
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from typing import Protocol, runtime_checkable

from .confidence import ConfidenceUpdate
from .embedding import (
    EmbeddingWrite,
    EmbeddingWriteResult,
    VectorHit,
    VectorQuery,
    VectorSearchResult,
    float32,
    order_hits,
)
from .encoding import Instant
from .finalize import FinalizedExperience, InitialLifecycleEvent
from .holdout import (
    HoldoutArm,
    HoldoutAssignment,
    HoldoutOutcomeCursor,
    HoldoutOutcomeRow,
    ProviderHoldoutAssignment,
    ProviderHoldoutOutcomeRow,
    check_outcome_limit,
    check_provider_assignment,
    check_provider_key,
    holdout_records,
)
from .lifecycle import (
    AuthorizationContext,
    HistoryQuery,
    HistoryResult,
    LifecycleCommitResult,
    LifecycleEvent,
    RecordReadResult,
    StoredLifecycleEvent,
    SupersessionCheckResult,
    as_replay_of,
    assessment_spent_error,
    is_same_evidence,
    stored_independence_key,
    validate_get,
    validate_history_query,
    validate_lifecycle_event,
    validate_supersession_check,
)
from .model import model_of_metadata
from .records import EXPERIENCE_STATUSES, ExperienceRecord, ExperienceStatus, Scope
from .reflector import is_model_authored
from .search import ELIGIBLE_STATUSES, AuthorizationBounds, SearchParameters, SearchRow

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


@runtime_checkable
class HoldoutRecorder(Protocol):
    """
    A store that records the injection holdout's assignments (contract 1.8), which the .NET benefit estimate reads.
    Optional: the agent integration records an assignment only through a store that has this method, and reports
    ``NotTracked`` for one that does not.
    """

    def record_holdout_assignment(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: HoldoutAssignment
    ) -> None: ...


@runtime_checkable
class ProviderHoldoutRecorder(Protocol):
    """
    A store that records another memory provider's holdout assignments (contract 1.11), which the provider estimate
    reads. Optional: a wrapped provider's assignment is recorded only through a store that has this method, and is
    reported ``NotTracked`` for one that does not.
    """

    def record_provider_holdout_assignment(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: ProviderHoldoutAssignment
    ) -> None: ...


@dataclass(frozen=True)
class ProviderHoldoutRow:
    """One provider holdout row (contract 1.11): a run, the provider's key, the arm, the scope the run was captured
    in, and the instant (truncated to whole microseconds, clamped to the store's clock)."""

    run_id: uuid.UUID
    provider_key: str
    arm: HoldoutArm
    scope: Scope
    assigned_at: Instant


@dataclass(frozen=True)
class HoldoutRow:
    """One holdout assignment row: a run, one record its block named at the revision named, the arm, the record's own
    scope, and the instant (truncated to whole microseconds, clamped to the store's clock)."""

    run_id: uuid.UUID
    experience_id: uuid.UUID
    revision: int
    arm: HoldoutArm
    scope: Scope
    assigned_at: Instant


@runtime_checkable
class LessonsReader(Protocol):
    """
    A store that reads a scope's records newest first, each with its last injection (contract 1.7), for the lessons
    view. Reads only; raises :class:`AuthorizationDenied` when the scope lies outside the bounds.
    """

    def list_lessons_page(
        self, bounds: AuthorizationBounds, scope: Scope, statuses: Sequence[str] | None, limit: int
    ) -> LessonsRead: ...


@runtime_checkable
class EmbeddingIndex(Protocol):
    """
    A store that keeps embeddings and searches them by cosine distance (contract 1.6). Optional: the agent integration
    runs the vector channel only through a store that has both methods, and reports ``NotConfigured`` for one that
    does not.
    """

    def write_embedding(self, bounds: AuthorizationBounds, write: EmbeddingWrite) -> EmbeddingWriteResult: ...

    def search_vectors(self, bounds: AuthorizationBounds, query: VectorQuery) -> VectorSearchResult: ...


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


@dataclass(frozen=True)
class LessonRow:
    """One record of a lessons page, with its injection summary (``None``: never injected, or not tracked)."""

    record: ExperienceRecord
    injection: InjectionSummary | None = None


@dataclass(frozen=True)
class LessonsRead:
    """
    A store's answer to :meth:`LessonsReader.list_lessons_page`: the records newest first, whether the injections
    could be read (``injections_tracked``), and how many rows the read answered (``answered``, including any a store
    skipped), which the lessons view compares with its probe.
    """

    rows: tuple[LessonRow, ...]
    injections_tracked: bool
    answered: int = -1

    def __post_init__(self) -> None:
        if self.answered < 0:
            object.__setattr__(self, "answered", len(self.rows))


MIN_LESSONS_LIMIT = 1
MAX_LESSONS_LIMIT = 500


def check_lessons_read(statuses: Sequence[str] | None, limit: int) -> None:
    """``ValueError`` for a ``limit`` outside 1 to 500, or ``statuses`` that is empty or names an unknown status."""
    if isinstance(limit, bool) or not isinstance(limit, int) or not MIN_LESSONS_LIMIT <= limit <= MAX_LESSONS_LIMIT:
        raise ValueError(f"The limit must be from {MIN_LESSONS_LIMIT} to {MAX_LESSONS_LIMIT}.")
    if statuses is not None and (
        isinstance(statuses, str) or not statuses or any(status not in EXPERIENCE_STATUSES for status in statuses)
    ):
        raise ValueError("statuses must be None, for every status, or hold at least one known status.")


class AuthorizationDenied(PermissionError):
    """The scope lies outside the authorization bounds."""


def permits(bounds: AuthorizationBounds, scope: Scope) -> bool:
    """Whether ``bounds`` permit ``scope``: the tenant always, and every bounded field exactly."""
    return bounds.permits(scope)


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
class _Embedding:
    model_id: str
    dimension: int
    content_hash: str
    source_revision: int
    vector: tuple[float, ...]  # 32-bit float values


def cosine_distance(left: Sequence[float], right: Sequence[float]) -> float:
    """Cosine distance (``1 - cos``, in [0, 2]) of two vectors of equal length; NaN when either has no magnitude."""
    dot = sum(a * b for a, b in zip(left, right, strict=True))
    norm = math.sqrt(sum(a * a for a in left)) * math.sqrt(sum(b * b for b in right))
    if norm == 0.0 or math.isnan(norm):
        return math.nan
    return min(max(1.0 - dot / norm, 0.0), 2.0)


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


@dataclass(frozen=True)
class _StoredEvent:
    """One committed lifecycle event, with what only the store knows about it."""

    event: LifecycleEvent
    scope: Scope
    applied_revision: int
    recorded_at: Instant
    actor: str | None

    def public(self) -> StoredLifecycleEvent:
        return StoredLifecycleEvent(self.event, self.recorded_at, self.applied_revision, self.actor)


@dataclass(frozen=True)
class _StoredEvidence:
    """One piece of confidence evidence in the ledger, counted or recorded only."""

    experience_id: uuid.UUID
    event_id: uuid.UUID | None
    update: ConfidenceUpdate
    applied_revision: int
    applied_status: ExperienceStatus


class InMemoryExperienceStore:
    """Records in memory, for development and tests. Thread-safe. ``clock`` is the store's own clock, which an
    injection's instant is clamped to, as PostgreSQL clamps it to its own, and which a lifecycle commit stamps on the
    event and the record (contract 1.16).

    It moves a record through its lifecycle exactly as .NET's ``InMemoryExperienceRecordStore`` does
    (:class:`~agentexperience.lifecycle.LifecycleStore`): every operation atomic under one lock, so concurrent commits
    from one revision have exactly one winner. A record seeded through the constructor has no history; one written by
    :meth:`insert_finalized` has its initial event at revision 1."""

    def __init__(self, records: Iterable[ExperienceRecord] = (), *, clock: Callable[[], Instant] = Instant.now) -> None:
        self._lock = threading.Lock()
        self._clock = clock
        self._records: dict[uuid.UUID, _Indexed] = {}
        # Contract 1.16: the event log, by event and by record, the evidence ledger, and the keys it holds.
        self._events: dict[uuid.UUID, _StoredEvent] = {}
        self._history: dict[uuid.UUID, list[_StoredEvent]] = {}
        self._evidence: dict[uuid.UUID, _StoredEvidence] = {}
        self._counted_keys: set[tuple[uuid.UUID, str]] = set()
        self._recorded_only_keys: set[tuple[uuid.UUID, str]] = set()
        self._spent_assessments: set[tuple[uuid.UUID, uuid.UUID]] = set()
        self._injections: dict[uuid.UUID, InjectionSummary] = {}
        self._embeddings: dict[uuid.UUID, _Embedding] = {}
        self._holdout: dict[tuple[uuid.UUID, uuid.UUID], HoldoutRow] = {}
        # Contract 1.11: keyed on (tenant, run, provider key), as the table is.
        self._provider_holdout: dict[tuple[str, uuid.UUID, str], ProviderHoldoutRow] = {}
        # Erased records: each one's scope and the run it was captured from (an in-memory tombstone keeps its run).
        self._tombstones: dict[uuid.UUID, tuple[Scope, uuid.UUID]] = {}
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
            if record.experience_id in self._records or record.experience_id in self._tombstones:
                raise ValueError("A record with this experience ID is already stored.")
            self._records[record.experience_id] = _index(record)
            self._append(_initial_event(finalized.event, record.scope, self._clock(), actor))

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

    def record_holdout_assignment(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: HoldoutAssignment
    ) -> None:
        """
        Records a run's holdout assignment as ``sql/record-holdout-assignment.sql`` does (contract 1.8): one row per
        named record held (not erased) in exactly ``scope``, at the revision named (a record named twice counts once,
        at the revision first named); a row that already exists for the run and record is kept as it is; the instant,
        truncated to whole microseconds, is clamped to the store's clock. Nothing is written for a run whose own
        records in the tenant (``source_run_id`` is the run) are all erased. Raises :class:`AuthorizationDenied`
        outside the bounds, and ``ValueError`` for an invalid assignment.
        """
        records = holdout_records(assignment)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        at = min(assignment.assigned_at.truncate_to_microseconds(), self._clock().truncate_to_microseconds())
        run_id = assignment.run_id
        with self._lock:
            if self._run_erased(run_id, scope.tenant_id):
                return
            for exposure in records:
                indexed = self._records.get(exposure.experience_id)
                if indexed is None or indexed.record.scope != scope:
                    continue
                self._holdout.setdefault(
                    (run_id, exposure.experience_id),
                    HoldoutRow(run_id, exposure.experience_id, exposure.revision, assignment.arm, scope, at),
                )

    def _run_erased(self, run_id: uuid.UUID, tenant_id: str) -> bool:
        """The late-write guard (called under the lock): the run has records in the tenant and all are erased."""
        live = any(
            indexed.record.source_run_id == run_id and indexed.record.scope.tenant_id == tenant_id
            for indexed in self._records.values()
        )
        erased = any(
            erased_run == run_id and erased_scope.tenant_id == tenant_id
            for erased_scope, erased_run in self._tombstones.values()
        )
        return erased and not live

    def record_provider_holdout_assignment(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: ProviderHoldoutAssignment
    ) -> None:
        """
        Records a run's arm in a memory provider's holdout as ``sql/record-provider-holdout-assignment.sql`` does
        (contract 1.11): one row per tenant, run and key, in exactly ``scope`` (no record is read for it); a row that
        already exists for them is kept as it is, whatever its scope; the instant, truncated to whole microseconds, is
        clamped to the store's clock. Nothing is written for a run whose own records in the tenant are all erased.
        Raises :class:`AuthorizationDenied` outside the bounds, and ``ValueError`` for an invalid assignment.
        """
        check_provider_assignment(assignment)
        if not isinstance(scope, Scope):
            raise TypeError("scope must be a Scope.")
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        at = min(assignment.assigned_at.truncate_to_microseconds(), self._clock().truncate_to_microseconds())
        with self._lock:
            if self._run_erased(assignment.run_id, scope.tenant_id):
                return
            self._provider_holdout.setdefault(
                (scope.tenant_id, assignment.run_id, assignment.provider_key),
                ProviderHoldoutRow(assignment.run_id, assignment.provider_key, assignment.arm, scope, at),
            )

    def provider_holdout_assignments(self) -> list[ProviderHoldoutRow]:
        """Every provider holdout row, ordered by tenant, run then key, for inspection in tests."""
        with self._lock:
            rows = list(self._provider_holdout.values())
        return sorted(rows, key=lambda row: (row.scope.tenant_id, row.run_id, row.provider_key))

    def _seed_provider_holdout_row(self, row: ProviderHoldoutRow) -> None:
        """Holds one provider holdout row exactly as given (no checks, no clamp): test-only seeding, not part of the
        store's API. A row that already exists for the tenant, run and key is replaced."""
        if not isinstance(row, ProviderHoldoutRow):
            raise TypeError("row must be a ProviderHoldoutRow.")
        with self._lock:
            self._provider_holdout[(row.scope.tenant_id, row.run_id, row.provider_key)] = row

    def read_provider_holdout_outcomes(
        self,
        bounds: AuthorizationBounds,
        scope: Scope,
        provider_key: str,
        limit: int,
        after: uuid.UUID | None = None,
    ) -> list[ProviderHoldoutOutcomeRow]:
        """
        A page of one provider's holdout rows (contract 1.11), as ``sql/query-provider-holdout-outcomes.sql`` reads
        them: the key's rows in exactly ``scope``, in run order (compared as UUIDs), after the run ``after``, at most
        ``limit`` (1 to 1000), each with its run's outcome (the record held in exactly the row's scope whose source run
        is the run, the earliest created, then the lowest ID). Raises :class:`AuthorizationDenied` outside the bounds
        and ``ValueError`` for a bad limit or key. Reads only.
        """
        check_provider_key(provider_key)
        check_outcome_limit(limit)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        with self._lock:
            page = sorted(
                (
                    row
                    for row in self._provider_holdout.values()
                    if row.scope == scope and row.provider_key == provider_key and (after is None or row.run_id > after)
                ),
                key=lambda row: row.run_id,
            )[:limit]
            result: list[ProviderHoldoutOutcomeRow] = []
            for row in page:
                own = sorted(
                    (
                        indexed.record
                        for indexed in self._records.values()
                        if indexed.record.source_run_id == row.run_id and indexed.record.scope == row.scope
                    ),
                    key=lambda record: (record.created_at.truncate_to_microseconds(), record.experience_id),
                )
                result.append(
                    ProviderHoldoutOutcomeRow(
                        row.run_id, row.provider_key, row.arm, row.assigned_at, own[0].outcome.status if own else None
                    )
                )
        return result

    def seed_holdout_row(self, row: HoldoutRow) -> None:
        """Holds one assignment row exactly as given, as the schema's owner inserts one (no checks, no clamp): for
        seeding and tests. A row that already exists for the run and record is replaced."""
        if not isinstance(row, HoldoutRow):
            raise TypeError("row must be a HoldoutRow.")
        with self._lock:
            self._holdout[(row.run_id, row.experience_id)] = row

    def holdout_assignments(self) -> list[HoldoutRow]:
        """Every holdout assignment row, ordered by run then record, for inspection in tests."""
        with self._lock:
            rows = list(self._holdout.values())
        return sorted(rows, key=lambda row: (row.run_id, row.experience_id))

    def read_holdout_outcomes(
        self, bounds: AuthorizationBounds, scope: Scope, limit: int, after: HoldoutOutcomeCursor | None = None
    ) -> list[HoldoutOutcomeRow]:
        """
        A page of the holdout's outcome rows (contract 1.9), as ``sql/query-holdout-outcomes.sql`` reads them: the
        assignment rows in exactly ``scope``, in (run, record) order, after ``after``, at most ``limit`` (1 to 1000),
        each with its run's outcome and model (the record held in exactly the row's scope whose source run is the run,
        the earliest created, then the lowest ID) and the named lesson's model (its record in exactly the row's
        scope). Raises :class:`AuthorizationDenied` outside the bounds and ``ValueError`` for a bad limit. Reads only.
        """
        check_outcome_limit(limit)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        with self._lock:
            page = sorted(
                (
                    row
                    for row in self._holdout.values()
                    if row.scope == scope and (after is None or (row.run_id, row.experience_id) > after)
                ),
                key=lambda row: (row.run_id, row.experience_id),
            )[:limit]
            runs: dict[uuid.UUID, tuple[str | None, str | None]] = {}
            lessons: dict[uuid.UUID, str | None] = {}
            result: list[HoldoutOutcomeRow] = []
            for row in page:
                if row.run_id not in runs:
                    own = sorted(
                        (
                            indexed.record
                            for indexed in self._records.values()
                            if indexed.record.source_run_id == row.run_id and indexed.record.scope == row.scope
                        ),
                        key=lambda record: (record.created_at.truncate_to_microseconds(), record.experience_id),
                    )
                    runs[row.run_id] = (
                        (own[0].outcome.status, model_of_metadata(own[0].environment.metadata)) if own else (None, None)
                    )
                if row.experience_id not in lessons:
                    lesson = self._records.get(row.experience_id)
                    lessons[row.experience_id] = (
                        model_of_metadata(lesson.record.environment.metadata)
                        if lesson is not None and lesson.record.scope == row.scope
                        else None
                    )
                outcome, run_model = runs[row.run_id]
                result.append(
                    HoldoutOutcomeRow(
                        row.run_id,
                        row.arm,
                        row.experience_id,
                        row.assigned_at,
                        outcome,
                        run_model,
                        lessons[row.experience_id],
                    )
                )
        return result

    def erase(self, experience_id: uuid.UUID) -> bool:
        """
        Erases a record, as the .NET erasure does to what this store holds: the record is no longer read, searched,
        listed or recorded; its injection summary and embedding go; every holdout row naming it goes, and so does
        every row of the run it was captured from when no other record of that run is live in its tenant, the run's
        provider holdout rows (contract 1.11) among them. The tombstone keeps the record's scope and run, so a later
        holdout write for that run records nothing. Answers
        whether a record was erased. For tests and development: the contract leaves erasure to .NET.
        """
        with self._lock:
            indexed = self._records.pop(experience_id, None)
            if indexed is None:
                return False
            record = indexed.record
            self._tombstones[experience_id] = (record.scope, record.source_run_id)
            self._injections.pop(experience_id, None)
            self._embeddings.pop(experience_id, None)
            # Its events and evidence go with it, as an erasure removes them in PostgreSQL.
            for stored in self._history.pop(experience_id, []):
                self._events.pop(stored.event.event_id, None)
            for evidence_id in [key for key, row in self._evidence.items() if row.experience_id == experience_id]:
                del self._evidence[evidence_id]
            for keys in (self._counted_keys, self._recorded_only_keys):
                keys.difference_update({key for key in keys if key[0] == experience_id})
            self._spent_assessments.difference_update(
                {key for key in self._spent_assessments if key[0] == experience_id}
            )
            run_live = any(
                other.record.source_run_id == record.source_run_id
                and other.record.scope.tenant_id == record.scope.tenant_id
                for other in self._records.values()
            )
            for key, row in list(self._holdout.items()):
                if row.experience_id == experience_id or (
                    not run_live
                    and row.run_id == record.source_run_id
                    and row.scope.tenant_id == record.scope.tenant_id
                ):
                    del self._holdout[key]
            if not run_live:
                # Contract 1.11: the provider holdout rows of the run, in its tenant, go with its last live record.
                for provider_key in [
                    key
                    for key in self._provider_holdout
                    if key[0] == record.scope.tenant_id and key[1] == record.source_run_id
                ]:
                    del self._provider_holdout[provider_key]
        return True

    def injections(self) -> dict[uuid.UUID, InjectionSummary]:
        """When each record was last injected and how often, for inspection in tests."""
        with self._lock:
            return dict(self._injections)

    def list_lessons_page(
        self, bounds: AuthorizationBounds, scope: Scope, statuses: Sequence[str] | None, limit: int
    ) -> LessonsRead:
        """
        The lessons page (contract 1.7): the records held in exactly ``scope`` (all statuses, or those named), newest
        first (``created_at`` descending, then ``experience_id`` ascending), at most ``limit``, each with its last
        injection. This store tracks injections, so the page always reports them tracked. Raises
        :class:`AuthorizationDenied` outside the bounds, and ``ValueError`` for an invalid ``limit`` or ``statuses``.
        """
        check_lessons_read(statuses, limit)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        wanted = None if statuses is None else set(statuses)
        with self._lock:
            held = [
                (indexed.record, self._injections.get(experience_id))
                for experience_id, indexed in self._records.items()
                if indexed.record.scope == scope and (wanted is None or indexed.record.status in wanted)
            ]
        held.sort(key=lambda item: str(item[0].experience_id))
        held.sort(key=lambda item: item[0].created_at.truncate_to_microseconds(), reverse=True)
        return LessonsRead(tuple(LessonRow(record, summary) for record, summary in held[:limit]), True)

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

    def write_embedding(self, bounds: AuthorizationBounds, write: EmbeddingWrite) -> EmbeddingWriteResult:
        """
        Stores a record's embedding as ``sql/write-experience-embedding.sql`` does: only for a record held in exactly
        the write's scope and still at exactly ``source_revision`` (``Written``); otherwise ``Stale`` with the record's
        revision, or ``Missing`` for an unknown record or one in another scope or outside the bounds. A row stored
        from a newer revision is never replaced by an older one. Components are kept as 32-bit floats.
        """
        vector = tuple(float32(component) for component in write.vector)
        if not permits(bounds, write.scope):
            return EmbeddingWriteResult("Missing")
        with self._lock:
            indexed = self._records.get(write.experience_id)
            if indexed is None or indexed.record.scope != write.scope:
                return EmbeddingWriteResult("Missing")
            revision = indexed.record.revision
            existing = self._embeddings.get(write.experience_id)
            if revision != write.source_revision or (
                existing is not None and existing.source_revision > write.source_revision
            ):
                return EmbeddingWriteResult("Stale", revision)
            self._embeddings[write.experience_id] = _Embedding(
                write.model_id, write.dimension, write.content_hash, write.source_revision, vector
            )
        return EmbeddingWriteResult("Written")

    def embedding(self, experience_id: uuid.UUID) -> tuple[str, int, str, int, tuple[float, ...]] | None:
        """A record's stored embedding (model ID, dimension, content hash, source revision, vector), for tests."""
        with self._lock:
            stored = self._embeddings.get(experience_id)
        return (
            None
            if stored is None
            else (stored.model_id, stored.dimension, stored.content_hash, stored.source_revision, stored.vector)
        )

    def search_vectors(self, bounds: AuthorizationBounds, query: VectorQuery) -> VectorSearchResult:
        """
        The nearest-neighbour search: embeddings of the query's model and dimension, of records in exactly the query's
        scope with an eligible status and confidence (and not model-authored, when excluded), by cosine distance
        (components compared as 32-bit floats), ties by experience ID, NaN last, at most ``limit``. With none, the
        outcome says why: ``ModelMismatch`` or ``DimensionMismatch`` when eligible embeddings exist but none is
        comparable, else ``Found`` and empty. Nothing outside the bounds.
        """
        scope = query.scope
        if not permits(bounds, scope):
            return VectorSearchResult("Found")
        vector = tuple(float32(component) for component in query.vector)
        statuses = set(query.statuses)
        with self._lock:
            held = [
                (self._records[experience_id].record, embedding)
                for experience_id, embedding in self._embeddings.items()
                if experience_id in self._records
            ]
        eligible = [
            (record, embedding)
            for record, embedding in held
            if record.scope == scope
            and record.status in statuses
            and record.reuse_confidence >= query.min_confidence
            and not (query.exclude_model_authored and is_model_authored(record.reflection))
        ]
        hits = [
            VectorHit(record, cosine_distance(embedding.vector, vector))
            for record, embedding in eligible
            if embedding.model_id == query.model_id and embedding.dimension == len(vector)
        ]
        if hits:
            return VectorSearchResult("Found", tuple(order_hits(hits)[: query.limit]))
        if not eligible:
            return VectorSearchResult("Found")
        if not any(embedding.model_id == query.model_id for _, embedding in eligible):
            return VectorSearchResult("ModelMismatch")
        return VectorSearchResult("DimensionMismatch")

    # ---- Contract 1.16: the lifecycle (.NET's InMemoryExperienceRecordStore) ---------------------------------------

    def read_record(
        self, authorization: AuthorizationContext, scope: Scope, experience_id: uuid.UUID
    ) -> RecordReadResult:
        """One record in exactly ``scope``, as the lifecycle reads it: validated, then authorized."""
        errors = validate_get(scope, experience_id)
        if errors:
            return RecordReadResult("Invalid", None, errors)
        if not authorization.permits(scope):
            return RecordReadResult("Denied", None)
        with self._lock:
            record = self._in_scope(scope, experience_id)
        return RecordReadResult("NotFound", None) if record is None else RecordReadResult("Found", record)

    def commit_lifecycle_event(
        self, authorization: AuthorizationContext, scope: Scope, event: LifecycleEvent
    ) -> LifecycleCommitResult:
        """
        Commits one lifecycle event (.NET's ``CommitLifecycleEventAsync``): validated, then authorized, then under the
        lock in the PostgreSQL store's order: a resubmitted evidence ID, a spent assessment and a taken independence
        key; a resubmitted event ID; the supersession gate; the revision and prior-status guards; then the write,
        stamped with the store's clock (truncated to whole microseconds).
        """
        errors = validate_lifecycle_event(scope, event, authorization)
        if errors:
            return LifecycleCommitResult("Invalid", 0, None, errors)
        if not authorization.permits(scope):
            return LifecycleCommitResult("Denied", 0, None)
        stored = dataclasses.replace(event, occurred_at=event.occurred_at.truncate_to_microseconds())
        with self._lock:
            return self._commit(authorization, scope, stored)

    def get_history(self, authorization: AuthorizationContext, query: HistoryQuery) -> HistoryResult:
        """One page of a record's history (.NET's ``GetHistoryAsync``): its events after the cursor, oldest first."""
        errors = validate_history_query(query)
        if errors:
            return HistoryResult("Invalid", 0, (), errors)
        if not authorization.permits(query.scope):
            return HistoryResult("Denied", 0, ())
        with self._lock:
            record = self._in_scope(query.scope, query.experience_id)
            if record is None:
                return HistoryResult("NotFound", 0, ())
            cursor = query.start_after_revision
            events = tuple(
                stored.public()
                for stored in self._history.get(record.experience_id, [])
                if cursor is None or stored.applied_revision > cursor
            )[: query.limit]
        return HistoryResult("Found", record.revision, events, (), events[-1].applied_revision if events else None)

    def check_supersession(
        self,
        authorization: AuthorizationContext,
        scope: Scope,
        experience_id: uuid.UUID,
        replacement_experience_id: uuid.UUID,
    ) -> SupersessionCheckResult:
        """Whether the replacement may replace the record (.NET's ``CheckSupersessionAsync``)."""
        errors = validate_supersession_check(scope, experience_id, replacement_experience_id)
        if errors:
            return SupersessionCheckResult("Invalid", None, errors)
        if not authorization.permits(scope):
            return SupersessionCheckResult("Denied", None)
        with self._lock:
            return self._check_supersession(scope, experience_id, replacement_experience_id)

    def _in_scope(self, scope: Scope, experience_id: uuid.UUID) -> ExperienceRecord | None:
        indexed = self._records.get(experience_id)
        return indexed.record if indexed is not None and indexed.record.scope == scope else None

    def _append(self, stored: _StoredEvent) -> None:
        self._events[stored.event.event_id] = stored
        self._history.setdefault(stored.event.experience_record_id, []).append(stored)

    def _commit(
        self, authorization: AuthorizationContext, scope: Scope, event: LifecycleEvent
    ) -> LifecycleCommitResult:
        record_id = event.experience_record_id
        submitted = event.confidence
        if submitted is not None:
            stored_evidence = self._evidence.get(submitted.evidence_id)
            if stored_evidence is not None:
                return self._compare_stored_evidence(scope, event, submitted, stored_evidence)
            if submitted.assessment_id is not None and (record_id, submitted.assessment_id) in self._spent_assessments:
                return LifecycleCommitResult("Conflict", 0, None, (assessment_spent_error(),))
            key = (record_id, stored_independence_key(submitted))
            if key in self._counted_keys or (not submitted.counted and key in self._recorded_only_keys):
                return self._record_duplicate(scope, event, submitted)

        stored_event = self._events.get(event.event_id)
        if stored_event is not None:
            return _compare_stored_event(scope, event, stored_event)

        record = self._in_scope(scope, record_id)
        if record is None:
            return LifecycleCommitResult("NotFound", 0, None)

        if event.replacement_experience_id is not None:
            check = self._check_supersession(scope, record_id, event.replacement_experience_id)
            if (
                check.outcome != "Allowed"
                or check.replacement_status is None
                or check.replacement_status not in ELIGIBLE_STATUSES
            ):
                return LifecycleCommitResult("ReplacementNotAllowed", 0, check.replacement_status)

        if record.revision != event.expected_revision:
            return LifecycleCommitResult("StaleRevision", record.revision, None)
        if record.status != (event.prior_status if event.prior_status is not None else event.current_status):
            return LifecycleCommitResult("StatusMismatch", record.revision, record.status)

        now = self._clock().truncate_to_microseconds()
        applied_revision = event.expected_revision + 1
        confidence = event.confidence
        recorded_only = confidence is not None and not confidence.counted
        moved = dataclasses.replace(
            record,
            status=event.current_status,
            revision=applied_revision,
            updated_at=record.updated_at if recorded_only else now,
        )
        if confidence is not None and confidence.counted:
            moved = dataclasses.replace(
                moved,
                reuse_confidence=confidence.new_reuse_confidence,
                supporting_validations=confidence.new_supporting_validations,
                contradictions=confidence.new_contradictions,
            )
        self._records[record_id] = dataclasses.replace(self._records[record_id], record=moved)
        self._append(_StoredEvent(event, scope, applied_revision, now, authorization.actor))
        if confidence is not None:
            self._add_evidence(record_id, event.event_id, confidence, applied_revision, event.current_status)
        return LifecycleCommitResult("Committed", applied_revision, None, (), confidence)

    def _record_duplicate(
        self, scope: Scope, event: LifecycleEvent, submitted: ConfidenceUpdate
    ) -> LifecycleCommitResult:
        """A submission whose key was taken: recorded for audit with nothing moved, and nothing else written."""
        record = self._in_scope(scope, event.experience_record_id)
        if record is None:
            return LifecycleCommitResult("NotFound", 0, None)
        if record.revision != event.expected_revision:
            return LifecycleCommitResult("StaleRevision", record.revision, None)
        if event.prior_status is not None and record.status != event.prior_status:
            return LifecycleCommitResult("StatusMismatch", record.revision, record.status)
        recorded_only = submitted.as_recorded_only()
        self._add_evidence(record.experience_id, None, recorded_only, record.revision, record.status)
        return LifecycleCommitResult("Committed", record.revision, record.status, (), recorded_only)

    def _add_evidence(
        self,
        record_id: uuid.UUID,
        event_id: uuid.UUID | None,
        update: ConfidenceUpdate,
        applied_revision: int,
        applied_status: ExperienceStatus,
    ) -> None:
        self._evidence[update.evidence_id] = _StoredEvidence(
            record_id, event_id, update, applied_revision, applied_status
        )
        if update.counted:
            self._counted_keys.add((record_id, stored_independence_key(update)))
        elif event_id is not None:
            self._recorded_only_keys.add((record_id, stored_independence_key(update)))
        if update.assessment_id is not None:
            self._spent_assessments.add((record_id, update.assessment_id))

    def _compare_stored_evidence(
        self, scope: Scope, event: LifecycleEvent, submitted: ConfidenceUpdate, stored: _StoredEvidence
    ) -> LifecycleCommitResult:
        same_content = (
            stored.experience_id == event.experience_record_id
            and self._in_scope(scope, stored.experience_id) is not None
            and (stored.event_id is None or stored.event_id == event.event_id)
            and stored.update.kind == submitted.kind
            and stored.update.source == submitted.source
            and stored.update.run_id == submitted.run_id
            and stored.update.verification_round_id == submitted.verification_round_id
            and stored.update.reviewer_identity == submitted.reviewer_identity
            and stored.update.rule_version == submitted.rule_version
            and stored.update.detail == submitted.detail
            and stored.update.assessment_id == submitted.assessment_id
        )
        if not same_content:
            return LifecycleCommitResult("Conflict", 0, None)
        replayed = dataclasses.replace(
            submitted,
            prior_reuse_confidence=stored.update.prior_reuse_confidence,
            new_reuse_confidence=stored.update.new_reuse_confidence,
            prior_supporting_validations=stored.update.prior_supporting_validations,
            new_supporting_validations=stored.update.new_supporting_validations,
            prior_contradictions=stored.update.prior_contradictions,
            new_contradictions=stored.update.new_contradictions,
            admission=stored.update.admission,
        )
        return LifecycleCommitResult("Committed", stored.applied_revision, stored.applied_status, (), replayed)

    def _check_supersession(
        self, scope: Scope, experience_id: uuid.UUID, replacement_id: uuid.UUID
    ) -> SupersessionCheckResult:
        if self._in_scope(scope, experience_id) is None:
            return SupersessionCheckResult("RecordNotFound", None)
        replacement = self._in_scope(scope, replacement_id)
        if replacement is None:
            return SupersessionCheckResult("ReplacementNotFound", None)
        reached = {replacement_id}
        pending = [replacement_id]
        while pending:
            current = pending.pop(0)
            for stored in self._history.get(current, []):
                following = stored.event.replacement_experience_id
                if stored.scope == scope and following is not None and following not in reached:
                    reached.add(following)
                    pending.append(following)
        return SupersessionCheckResult("Cycle" if experience_id in reached else "Allowed", replacement.status)


def _compare_stored_event(scope: Scope, event: LifecycleEvent, stored: _StoredEvent) -> LifecycleCommitResult:
    """A resubmitted event ID: the same event, every field and the scope included, is the original replayed."""
    if stored.event.experience_record_id != event.experience_record_id or stored.scope != scope:
        return LifecycleCommitResult("Conflict", 0, None)
    resubmitted = dataclasses.replace(
        event,
        confidence=None if event.confidence is None else as_replay_of(event.confidence, stored.event.confidence),
    )
    if is_same_evidence(event.confidence, stored.event.confidence):
        resubmitted = dataclasses.replace(resubmitted, current_status=stored.event.current_status)
    if stored.event == resubmitted:
        return LifecycleCommitResult("Committed", stored.applied_revision, None, (), stored.event.confidence)
    return LifecycleCommitResult("Conflict", 0, None)


def _initial_event(event: InitialLifecycleEvent, scope: Scope, recorded_at: Instant, actor: str) -> _StoredEvent:
    """A finalized record's initial event, as the store holds it in the record's history."""
    return _StoredEvent(
        LifecycleEvent(
            event_id=event.event_id,
            experience_record_id=event.experience_id,
            prior_status=event.prior_status,
            current_status=event.current_status,
            reason=event.reason,
            producer=event.producer,
            occurred_at=event.occurred_at.truncate_to_microseconds(),
            expected_revision=event.expected_revision,
        ),
        scope,
        event.applied_revision,
        recorded_at.truncate_to_microseconds(),
        actor,
    )
