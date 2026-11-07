"""
The lessons view (contract 1.7): a read-only list of what the library has learned in one scope, as the .NET
``AgentExperienceLessons`` lists it. Each lesson carries its status, confidence and checks (never the evidence's
detail), the block text an agent in that scope would be shown for it, whether and why retrieval would not use it, and
when it was last injected.

It reads through a store's ``list_lessons_page`` under the caller's own authorization bounds and in the caller's own
scope only (a sharing grant is outside contract 1.x), and never writes. A scope outside the bounds is an empty page with
the outcome ``Denied``, not an exception. A record that cannot be rendered is listed as ``Unrenderable`` and the rest
are still listed. Listing lessons discloses them: serve the view only behind the host's own authorization.

One of .NET's ineligibility codes does not exist here: ``ContentUnconfirmed`` (no provenance signing). The port
decides, in .NET's order, ``Status``, ``BelowConfidenceFloor`` (the retrieval policy's ``minimum_confidence``, 0.5 by
default), ``ModelAuthored`` (when the settings exclude model-authored lessons), ``Expired`` (contract 1.12: the last
activity is older than the retrieval policy's ``max_age`` at the view's clock), ``OverByteBudget`` (the block options'
``max_bytes``) and ``Unrenderable``. Contract 1.14: it exports the lessons measured as helping
(:meth:`ExperienceLessons.export_measured`), and :mod:`agentexperience.lessons_html` renders a page as HTML.
"""

from __future__ import annotations

import asyncio
import builtins
import math
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass, replace
from typing import Any, Literal

from ._text import format_fixed
from .benefit import BenefitComparison, BenefitOptions, LessonBenefit, estimate_benefit, lesson_benefit_to_json
from .benefit_policy import BenefitPolicy
from .block import Block, BlockOptions, RankedRecord, render_block, render_lesson_text
from .encoding import Instant
from .export import ExportedLesson, LessonExportResult, LessonSink
from .identity import ExperienceIdentity
from .inmemory import (
    MAX_LESSONS_LIMIT,
    MIN_LESSONS_LIMIT,
    AuthorizationDenied,
    InjectionSummary,
    LessonsReader,
    permits,
)
from .records import EXPERIENCE_STATUSES, CheckResult, ExperienceRecord, ExperienceStatus, Scope
from .reflector import is_model_authored
from .retrieval import DEFAULT_POLICY, RetrievalPolicy, ticks_of
from .search import ELIGIBLE_STATUSES, AuthorizationBounds

DEFAULT_LESSONS_LIMIT = 50
"""How many lessons a query lists by default (.NET's ``ExperienceRecordQuery.DefaultLimit``)."""

LessonsOutcome = Literal["Found", "Denied"]
"""``Found``, or why the page is empty: the scope lies outside the authorization bounds."""

Ineligibility = Literal["Status", "BelowConfidenceFloor", "ModelAuthored", "Expired", "OverByteBudget", "Unrenderable"]
"""Why retrieval would not use a lesson. .NET's ``ContentUnconfirmed`` does not exist in this port."""

_UNAVAILABLE = "(unavailable)"
_UNBOUNDED_BYTES = 2**62


@dataclass(frozen=True)
class LessonsQuery:
    """
    What to list. ``statuses`` is ``None`` for every status, or at least one known status (a repeat counts once).
    ``limit`` is 1 to 500 (default 50); with ``include_ineligible`` off, the lessons left out still count against it.
    ``ValueError`` for an invalid ``limit`` or ``statuses``.
    """

    statuses: Sequence[ExperienceStatus] | None = None
    limit: int = DEFAULT_LESSONS_LIMIT
    include_ineligible: bool = True
    include_measured_benefit: bool = False
    """Contract 1.10: read what the injection holdout measured for each listed lesson into
    :attr:`Lesson.measured_benefit`, from one estimate for the scope over the view's benefit policy's window and
    minimum (30 days and 20 by default, whether or not the policy is on). Off by default: nothing more is read."""

    def __post_init__(self) -> None:
        if isinstance(self.limit, bool) or not isinstance(self.limit, int):
            raise ValueError("limit must be an integer.")
        if not MIN_LESSONS_LIMIT <= self.limit <= MAX_LESSONS_LIMIT:
            raise ValueError(f"limit must be from {MIN_LESSONS_LIMIT} to {MAX_LESSONS_LIMIT}.")
        if self.statuses is not None:
            if isinstance(self.statuses, (str, bytes)) or not self.statuses:
                raise ValueError("statuses must be None, for every status, or hold at least one status.")
            unknown = [status for status in self.statuses if status not in EXPERIENCE_STATUSES]
            if unknown:
                raise ValueError(f"statuses holds an unknown status: {unknown[0]!r}.")
            object.__setattr__(self, "statuses", tuple(dict.fromkeys(self.statuses)))


@dataclass(frozen=True)
class LessonCheck:
    """One check a lesson's outcome was verified by: its ID, the evidence's kind and the result. No evidence detail."""

    check_id: str
    evidence_kind: str
    result: CheckResult


@dataclass(frozen=True)
class Lesson:
    """One stored lesson, as the view shows it."""

    experience_id: uuid.UUID
    task_id: str
    status: ExperienceStatus
    confidence: float
    created_at: Instant
    updated_at: Instant
    checks: tuple[LessonCheck, ...]
    block_text: str
    """The Historical Reference block for this record alone, numbered 1, with no ranking (its ``Matched:`` line says
    so), whatever its status and size. Empty when it could not be rendered. Untrusted text: encode it before use."""
    is_eligible: bool
    ineligible_reason: str | None
    ineligibility: Ineligibility | None
    last_injected_at: Instant | None = None
    """When an agent in this scope was last shown this lesson. ``None`` means never injected when the page's
    ``injections_tracked`` is true, and not known when it is false."""
    injection_count: int = 0
    measured_benefit: LessonBenefit | None = None
    """Contract 1.10: what the injection holdout measured for this lesson (treatment runs given it against holdout
    runs denied it, with the verdict), when the query asked for it; ``None`` otherwise, when no assigned run in the
    window named it, or when the store does not track the holdout."""

    def to_json(self) -> dict[str, Any]:
        """The lesson in the contract's ``expected.json`` form (camelCase, instants as ISO 8601 text)."""
        return {
            "experienceId": str(self.experience_id),
            "taskId": self.task_id,
            "status": self.status,
            "confidence": self.confidence,
            "createdAt": self.created_at.format(),
            "updatedAt": self.updated_at.format(),
            "checks": [
                {"checkId": check.check_id, "evidenceKind": check.evidence_kind, "result": check.result}
                for check in self.checks
            ],
            "blockText": self.block_text,
            "isEligible": self.is_eligible,
            "ineligibility": self.ineligibility,
            "ineligibleReason": self.ineligible_reason,
            "lastInjectedAt": None if self.last_injected_at is None else self.last_injected_at.format(),
            "injectionCount": self.injection_count,
        }


@dataclass(frozen=True)
class LessonsPage:
    """One page of lessons from one scope, newest first."""

    outcome: LessonsOutcome
    scope: Scope
    lessons: tuple[Lesson, ...]
    generated_at: Instant
    has_more: bool = False
    """More lessons matching the query exist past ``lessons``, or may: at most 500 records are read."""
    out_of_scope_dropped: int = 0
    injections_tracked: bool = False
    """True when the page was read with its injections; false when the store tracks none or they could not be read
    (the application role lacks the 1.4 grants, say), and then a ``None`` ``last_injected_at`` means not known."""
    measured_benefit_included: bool = False
    """Whether the query asked for each lesson's measured benefit (contract 1.10)."""

    def to_json(self) -> dict[str, Any]:
        """The page in the contract's ``expected.json`` form; each lesson carries ``measuredBenefit`` only when the
        query asked for it."""
        lessons = []
        for lesson in self.lessons:
            value = lesson.to_json()
            if self.measured_benefit_included:
                benefit = lesson.measured_benefit
                value["measuredBenefit"] = None if benefit is None else lesson_benefit_to_json(benefit)
            lessons.append(value)
        return {
            "outcome": self.outcome,
            "generatedAt": self.generated_at.format(),
            "hasMore": self.has_more,
            "outOfScopeDropped": self.out_of_scope_dropped,
            "injectionsTracked": self.injections_tracked,
            "lessons": lessons,
        }


def render_alone(record: ExperienceRecord, options: BlockOptions | None = None) -> Block:
    """
    ``record`` rendered alone as an injected block would carry it: the same writer and options, numbered 1, with no
    ranking (its ``Matched:`` line says ``no ranking data``) and no record or byte limit, whatever its status and size.
    Compare ``byte_count`` with the options' ``max_bytes``.
    """
    options = replace(options or BlockOptions(), max_records=1, max_bytes=_UNBOUNDED_BYTES)
    return render_block([RankedRecord(record, math.nan, ())], options)


def format_number(value: float) -> str:
    """.NET's ``ToString("0.00")``: the double taken to 15 significant digits, then rounded half away from zero;
    ``(unavailable)`` for a number that is not finite."""
    if not math.isfinite(value):
        return _UNAVAILABLE
    return format_fixed(value, 2)


class ExperienceLessons:
    """
    Lists what has been learned in a scope. Build one from the agent returned by ``use_agent_experience``
    (:func:`agentexperience.maf.agent_experience_lessons`), or directly from a store that has ``list_lessons_page``
    (the in-memory and PostgreSQL stores do) and the block options and exclusion flag the injection uses.

    ``render`` renders one record's block (default :func:`render_alone`); a test can pass one that fails. ``identity``
    is the default for :meth:`list` and :meth:`list_async`.
    """

    def __init__(
        self,
        store: LessonsReader,
        *,
        block_options: BlockOptions | None = None,
        exclude_model_authored: bool = False,
        clock: Callable[[], Instant] = Instant.now,
        identity: ExperienceIdentity | None = None,
        render: Callable[[ExperienceRecord, BlockOptions], Block] | None = None,
        benefit: BenefitPolicy | None = None,
        retrieval_policy: RetrievalPolicy | None = None,
    ) -> None:
        if not isinstance(store, LessonsReader):
            raise TypeError("The store must have a list_lessons_page method (contract 1.7).")
        if not callable(clock):
            raise TypeError("clock must be callable.")
        self._store = store
        self._options = block_options or BlockOptions()
        self._exclude_model_authored = exclude_model_authored
        self._clock = clock
        self._identity = identity
        self._render = render or render_alone
        self._benefit = benefit or BenefitPolicy()
        if retrieval_policy is not None and not isinstance(retrieval_policy, RetrievalPolicy):
            raise TypeError("retrieval_policy must be a RetrievalPolicy.")
        self._policy = retrieval_policy or DEFAULT_POLICY

    def list(self, identity: ExperienceIdentity | None = None, query: LessonsQuery | None = None) -> LessonsPage:
        """Lists the lessons in the identity's scope under its authorization bounds (see :meth:`list_scope`)."""
        identity = identity or self._identity
        if identity is None:
            raise ValueError("An identity is required: pass one, or build the view with identity=.")
        return self.list_scope(identity.authorization, identity.scope, query)

    async def list_async(
        self, identity: ExperienceIdentity | None = None, query: LessonsQuery | None = None
    ) -> LessonsPage:
        """:meth:`list` on a worker thread, so a PostgreSQL read does not block the event loop."""
        return await asyncio.to_thread(self.list, identity, query)

    def list_scope(self, bounds: AuthorizationBounds, scope: Scope, query: LessonsQuery | None = None) -> LessonsPage:
        """
        Lists the lessons in ``scope``, newest first, reading under ``bounds`` (never a request's scope: take both from
        the host's own authentication). A scope outside the bounds runs no read and answers an empty page with the
        outcome ``Denied``. ``has_more`` is set when more lessons matching the query exist or may exist. A store failure
        other than the unreadable injections raises. Never writes.
        """
        return self._read(bounds, scope, query or LessonsQuery())[0]

    def _read(
        self, bounds: AuthorizationBounds, scope: Scope, query: LessonsQuery
    ) -> tuple[LessonsPage, builtins.list[ExperienceRecord]]:
        """The page, with the record each listed lesson was described from, at the same index."""
        if not permits(bounds, scope):
            return LessonsPage("Denied", scope, (), self._clock()), []
        # One record past the limit tells "exactly the limit" from "more exist". Leaving ineligible lessons out may skip
        # any number of records, so then as many as the store answers are read.
        probe = min(query.limit + 1, MAX_LESSONS_LIMIT) if query.include_ineligible else MAX_LESSONS_LIMIT
        try:
            read = self._store.list_lessons_page(bounds, scope, query.statuses, probe)
        except AuthorizationDenied:
            return LessonsPage("Denied", scope, (), self._clock()), []
        generated_at = self._clock()

        lessons: list[Lesson] = []
        described: builtins.list[ExperienceRecord] = []
        out_of_scope = 0
        has_more = False
        for row in read.rows:
            if row.record.scope != scope:
                out_of_scope += 1
                continue
            lesson = self._describe(row.record, generated_at)
            if read.injections_tracked and row.injection is not None:
                lesson = _with_injection(lesson, row.injection)
            if not lesson.is_eligible and not query.include_ineligible:
                continue
            if len(lessons) == query.limit:
                has_more = True
                break
            lessons.append(lesson)
            described.append(row.record)
        # A store that answered as many records as were asked for may hold more than were read.
        has_more = has_more or read.answered >= probe
        # Contract 1.10: opt-in, so the default page reads nothing more and is unchanged.
        if query.include_measured_benefit and lessons:
            lessons = _with_measured_benefit(self._store, self._benefit, bounds, scope, lessons, generated_at)
        page = LessonsPage(
            "Found",
            scope,
            tuple(lessons),
            generated_at,
            has_more,
            out_of_scope,
            read.injections_tracked,
            query.include_measured_benefit,
        )
        return page, described

    def export_measured(
        self,
        sink: LessonSink,
        identity: ExperienceIdentity | None = None,
        options: BenefitOptions | None = None,
    ) -> LessonExportResult:
        """
        Hands ``sink`` every lesson in the identity's scope that the holdout measured as helping (contract 1.14, .NET's
        ``ExportMeasuredAsync``), so a copy kept elsewhere (a Markdown file an agent loads, another memory store) stays
        in step: a sync, not a one-way dump.

        A lesson is in the set when the view lists it in exactly the identity's scope, it is eligible, its record's
        outcome is ``Verified`` and its verdict is ``Helps``, from one estimate for the scope over the benefit policy's
        window, decided with ``options`` (the policy's, 20 runs per arm by default, when ``None``). The set is sorted by
        experience ID. A store that does not track the holdout gives an empty set, and the sink is still called, so
        stale copies are removed. A page or estimate that is not ``Found`` (the scope lies outside the bounds) is a
        result carrying that outcome, and the sink is not called.

        Explicit and read-only: nothing exports automatically, and nothing is written to the store. ``ValueError`` for
        invalid options (before anything is read); ``RuntimeError`` when the scope holds more reusable lessons than one
        read answers (500), or the sink returns nothing. Whatever the sink raises propagates.
        """
        if sink is None or not callable(getattr(sink, "sync", None)):
            raise TypeError("sink must have a sync(scope, current) method.")
        identity = identity or self._identity
        if identity is None:
            raise ValueError("An identity is required: pass one, or build the view with identity=.")
        if options is not None and not isinstance(options, BenefitOptions):
            raise TypeError("options must be BenefitOptions or None.")
        options = options or self._benefit.estimator_options
        bounds, scope = identity.authorization, identity.scope

        page, records = self._read(
            bounds, scope, LessonsQuery(ELIGIBLE_STATUSES, MAX_LESSONS_LIMIT, include_ineligible=False)
        )
        if page.outcome != "Found":
            return LessonExportResult(scope, page.outcome, (), None, page.generated_at)
        if page.has_more:
            raise RuntimeError(
                f"The scope holds more reusable lessons than one store read answers ({MAX_LESSONS_LIMIT}), so the "
                "measured set cannot be read whole, and a sink given part of it would remove copies of lessons that "
                "still help. Nothing was handed to the sink."
            )

        exported_at = page.generated_at
        estimate = estimate_benefit(
            self._store, bounds, scope, options, assigned_from=self._benefit.assigned_from(exported_at)
        )
        if estimate.outcome != "Found":
            return LessonExportResult(scope, estimate.outcome, (), None, exported_at)

        current: builtins.list[ExportedLesson] = []
        if estimate.tracked:
            helps: dict[uuid.UUID, BenefitComparison] = {}
            for entry in estimate.lessons:
                if entry is not None and entry.comparison.verdict == "Helps":
                    helps.setdefault(entry.experience_id, entry.comparison)
            seen: set[uuid.UUID] = set()
            for lesson, record in zip(page.lessons, records, strict=True):
                comparison = helps.get(lesson.experience_id)
                if (
                    not lesson.is_eligible
                    or record.outcome.status != "Verified"
                    or comparison is None
                    or lesson.experience_id in seen
                ):
                    continue
                seen.add(lesson.experience_id)
                current.append(
                    ExportedLesson(
                        record.experience_id,
                        record.revision,
                        lesson.task_id,
                        render_lesson_text(record, self._options),
                        comparison,
                        exported_at,
                    )
                )
            current.sort(key=lambda exported: str(exported.experience_id))

        sync = sink.sync(scope, tuple(current))
        if sync is None:
            raise RuntimeError("The sink's sync returned None.")
        return LessonExportResult(scope, "Found", tuple(current), sync, exported_at, estimate.tracked)

    async def export_measured_async(
        self,
        sink: LessonSink,
        identity: ExperienceIdentity | None = None,
        options: BenefitOptions | None = None,
    ) -> LessonExportResult:
        """:meth:`export_measured` on a worker thread, so a PostgreSQL read or a file write does not block the event
        loop."""
        return await asyncio.to_thread(self.export_measured, sink, identity, options)

    def _describe(self, record: ExperienceRecord, now: Instant) -> Lesson:
        """One record as a lesson. A record that cannot be rendered is a lesson marked so, never an exception."""
        try:
            block = self._render(record, self._options)
            ineligibility, reason = self._eligibility(record, block.byte_count, now)
            return Lesson(
                record.experience_id,
                record.task_id or "",
                record.status,
                record.reuse_confidence,
                record.created_at,
                record.updated_at,
                tuple(LessonCheck(item.check_id, item.kind, item.result) for item in record.outcome.evidence),
                block.text,
                ineligibility is None,
                reason,
                ineligibility,
            )
        except Exception as failure:  # One record that cannot be rendered must not hide the rest.
            return Lesson(
                record.experience_id,
                record.task_id or "",
                record.status,
                record.reuse_confidence,
                record.created_at,
                record.updated_at,
                (),
                "",
                False,
                f"it could not be rendered ({type(failure).__name__})",
                "Unrenderable",
            )

    def _eligibility(
        self, record: ExperienceRecord, byte_count: int, now: Instant
    ) -> tuple[Ineligibility | None, str | None]:
        """.NET's standing ineligibility, in its order (status, floor, authorship, age), then the byte budget."""
        policy = self._policy
        if record.status not in ELIGIBLE_STATUSES:
            return "Status", f"status {record.status} is never retrieved"
        if not record.reuse_confidence >= policy.minimum_confidence:
            return (
                "BelowConfidenceFloor",
                f"confidence {format_number(record.reuse_confidence)} is below the retrieval floor "
                f"{format_number(policy.minimum_confidence)}",
            )
        if self._exclude_model_authored and is_model_authored(record.reflection):
            return "ModelAuthored", "model-authored lessons are excluded"
        if policy.max_age is not None and (now - record.updated_at).ticks > ticks_of(policy.max_age):
            return "Expired", "its last activity is older than the retrieval policy's maximum age"
        if byte_count > self._options.max_bytes:
            return (
                "OverByteBudget",
                f"its block ({byte_count} bytes) is larger than the {self._options.max_bytes}-byte injection budget",
            )
        return None, None


def _with_measured_benefit(
    store: LessonsReader,
    policy: BenefitPolicy,
    bounds: AuthorizationBounds,
    scope: Scope,
    lessons: list[Lesson],
    now: Instant,
) -> list[Lesson]:
    """Each lesson with its entry of one estimate for the scope, over the policy's window before ``now``; unchanged
    when the store does not track the holdout or refused the read. A store failure raises."""
    estimate = estimate_benefit(
        store,
        bounds,
        scope,
        policy.estimator_options,
        assigned_from=policy.assigned_from(now),
    )
    if not (estimate.tracked and estimate.outcome == "Found"):
        return lessons
    measured: dict[uuid.UUID, LessonBenefit] = {}
    for entry in estimate.lessons:
        measured.setdefault(entry.experience_id, entry)
    return [
        replace(lesson, measured_benefit=measured[lesson.experience_id]) if lesson.experience_id in measured else lesson
        for lesson in lessons
    ]


def _with_injection(lesson: Lesson, summary: InjectionSummary) -> Lesson:
    return replace(lesson, last_injected_at=summary.last_injected_at, injection_count=summary.injection_count)


__all__ = [
    "DEFAULT_LESSONS_LIMIT",
    "ExperienceLessons",
    "Ineligibility",
    "Lesson",
    "LessonCheck",
    "LessonsOutcome",
    "LessonsPage",
    "LessonsQuery",
    "format_number",
    "render_alone",
]
