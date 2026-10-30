"""
Acting on the benefit estimate (contract 1.10): .NET's ``ExperienceBenefitPolicy``. With the policy on, a lesson the
injection holdout measured as hurting is not injected, a scope whose lessons together measured as hurting gets none,
and with ``prefer_measured_helpful`` the lessons measured as helping go first. Off by default: nothing is read, cached,
reordered or omitted.

The estimate is never read on the request path. :class:`BenefitCache` keeps one estimate per scope, authorization
bounds and principal; an invocation uses what is cached as it is (nothing yet means no effect) and, when it is missing
or older than ``refresh_interval``, starts one refresh on a daemon thread. A refresh that fails or overruns
``refresh_timeout`` keeps the previous estimate and is reported only through telemetry (``benefit_refresh``).

:func:`select_block` is the selection an invocation runs, as a pure function: the view's reordering and omissions, the
model-authored exclusion, the record limit, then the block's byte budget, each omission with .NET's reason and detail.
"""

from __future__ import annotations

import asyncio
import datetime
import threading
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Any, Literal

from .benefit import (
    DEFAULT_MINIMUM_RUNS_PER_ARM,
    UNKNOWN_MODEL,
    BenefitEstimate,
    BenefitOptions,
    BenefitVerdict,
    ModelPairBenefit,
    compare,
    estimate_benefit,
)
from .block import Block, BlockOptions, RankedRecord, render_block
from .capabilities import CapabilityGate
from .encoding import TICKS_PER_SECOND, Duration, Instant
from .observer import INFRASTRUCTURE, UNEXPECTED, classify, observe
from .records import Scope
from .reflector import is_model_authored
from .search import AuthorizationBounds

DEFAULT_REFRESH_INTERVAL = datetime.timedelta(minutes=10)
"""How old a cached estimate may grow before an invocation starts a refresh, by default: .NET's."""

DEFAULT_REFRESH_TIMEOUT = datetime.timedelta(seconds=30)
"""How long one refresh may take by default before it is abandoned: .NET's."""

DEFAULT_WINDOW = datetime.timedelta(days=30)
"""How far back, from each refresh, holdout assignments count by default: .NET's."""

MAX_REFRESH_TIMEOUT = datetime.timedelta(days=1)
"""The largest permitted ``refresh_timeout``."""

MAX_CACHE_ENTRIES = 10_000
"""The most keys a cache keeps; past it the entry refreshed longest ago (and not refreshing) is dropped."""

MEASURED_HARMFUL_DETAIL = (
    "The injection holdout measured this lesson as hurting: runs given it verified less often than runs it was "
    "withheld from."
)
"""The omission detail of a lesson measured as hurting (.NET's, byte for byte)."""

SCOPE_MEASURED_HARMFUL_DETAIL = (
    "The injection holdout measured this scope's lessons together as hurting, so none is injected."
)
"""The omission detail of every record in a scope whose lessons together were measured as hurting."""

OmissionReason = Literal[
    "MeasuredHarmful",
    "ModelAuthored",
    "ToolUnavailable",
    "RiskClassExceeded",
    "HostDenied",
    "OverRecordLimit",
    "OverByteBudget",
]
"""Why a ranked record was not injected: .NET's ``InjectionOmissionReason`` names, those this port can give."""


def _ticks(value: datetime.timedelta) -> int:
    return (value // datetime.timedelta(microseconds=1)) * (TICKS_PER_SECOND // 1_000_000)


@dataclass(frozen=True)
class BenefitPolicy:
    """
    Whether and how injection acts on measured benefit (contract 1.10, .NET's ``ExperienceBenefitPolicy``). Off by
    default. ``window`` (30 days), ``refresh_interval`` (10 minutes) and ``refresh_timeout`` (30 seconds, at most one
    day) must be strictly positive and ``minimum_runs_per_arm`` (20) at least 1; ``ValueError`` otherwise, even with
    the policy off.
    """

    enabled: bool = False
    prefer_measured_helpful: bool = False
    """Lessons measured ``Helps`` move ahead of the others, each group keeping its rank order, before the limit."""
    per_model: bool = False
    """Act on the verdicts for the invoking agent's own model (the estimate split by model), not the pooled ones."""
    window: datetime.timedelta = DEFAULT_WINDOW
    minimum_runs_per_arm: int = DEFAULT_MINIMUM_RUNS_PER_ARM
    refresh_interval: datetime.timedelta = DEFAULT_REFRESH_INTERVAL
    refresh_timeout: datetime.timedelta = DEFAULT_REFRESH_TIMEOUT

    def __post_init__(self) -> None:
        for name in ("window", "refresh_interval", "refresh_timeout"):
            if not isinstance(getattr(self, name), datetime.timedelta):
                raise TypeError(f"{name} must be a datetime.timedelta.")
        if self.refresh_interval <= datetime.timedelta(0):
            raise ValueError("The refresh interval must be strictly positive.")
        if self.refresh_timeout <= datetime.timedelta(0) or self.refresh_timeout > MAX_REFRESH_TIMEOUT:
            raise ValueError(f"The refresh timeout must be strictly positive and at most {MAX_REFRESH_TIMEOUT}.")
        if self.window <= datetime.timedelta(0):
            raise ValueError("The window must be strictly positive.")
        if isinstance(self.minimum_runs_per_arm, bool) or not isinstance(self.minimum_runs_per_arm, int):
            raise TypeError("minimum_runs_per_arm must be an integer.")
        if self.minimum_runs_per_arm < 1:
            raise ValueError("The minimum runs per arm must be at least 1.")

    @property
    def estimator_options(self) -> BenefitOptions:
        """The estimator's options this policy decides verdicts with."""
        return BenefitOptions(self.minimum_runs_per_arm, split_by_model=self.per_model)

    def assigned_from(self, now: Instant) -> Instant | None:
        """The earliest assignment the window counts at ``now``; ``None`` (every row) when the window reaches back
        before the first instant."""
        ticks = now.ticks - _ticks(self.window)
        return Instant(ticks) if ticks > 0 else None


# ---- The view ----------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class BenefitView:
    """What one estimate means for an invocation: whether the scope's lessons together hurt, and which hurt or help."""

    scope_hurts: bool
    hurts: frozenset[uuid.UUID]
    helps: frozenset[uuid.UUID]

    @property
    def acts(self) -> bool:
        return self.scope_hurts or bool(self.hurts) or bool(self.helps)

    def harmful(self, experience_id: uuid.UUID) -> bool:
        return self.scope_hurts or experience_id in self.hurts


def usable(estimate: BenefitEstimate | None) -> bool:
    """Whether an estimate may be acted on: tracked, and ``Found``."""
    return estimate is not None and estimate.tracked and estimate.outcome == "Found"


def _for_model(pairs: Sequence[ModelPairBenefit] | None, model: str, minimum: int) -> BenefitVerdict | None:
    treatment_runs = treatment_successes = holdout_runs = holdout_successes = 0
    found = False
    for pair in pairs or ():
        if pair is None or pair.comparison is None or pair.run_model != model:
            continue
        found = True
        treatment_runs += pair.comparison.treatment.runs
        treatment_successes += pair.comparison.treatment.successes
        holdout_runs += pair.comparison.holdout.runs
        holdout_successes += pair.comparison.holdout.successes
    if not found:
        return None
    return compare(treatment_runs, treatment_successes, holdout_runs, holdout_successes, max(1, minimum)).verdict


def benefit_view(estimate: BenefitEstimate, model: str | None = None) -> BenefitView | None:
    """
    The view of ``estimate`` for ``model``'s runs, or the pooled view when ``model`` is ``None``: the scope's verdict
    is that model's group's, and a lesson's is its runs of that model's, pooled over the models that learned it;
    wherever that group has ``NoEvidence`` the pooled verdict stands. ``None`` when the view changes nothing.
    """
    minimum = estimate.minimum_runs_per_arm
    hurts: set[uuid.UUID] = set()
    helps: set[uuid.UUID] = set()
    for lesson in estimate.lessons or ():
        if lesson is None:
            continue
        verdict: BenefitVerdict = lesson.comparison.verdict if lesson.comparison is not None else "NoEvidence"
        if model is not None:
            own = _for_model(lesson.by_model_pair, model, minimum)
            if own is not None and own != "NoEvidence":
                verdict = own
        if verdict == "Hurts":
            hurts.add(lesson.experience_id)
        elif verdict == "Helps":
            helps.add(lesson.experience_id)
    scope_verdict: BenefitVerdict = estimate.overall.verdict if estimate.overall is not None else "NoEvidence"
    if model is not None:
        group = next((item for item in estimate.by_consuming_model or () if item.model == model), None)
        if group is not None and group.comparison.verdict != "NoEvidence":
            scope_verdict = group.comparison.verdict
    view = BenefitView(scope_verdict == "Hurts", frozenset(hurts), frozenset(helps))
    return view if view.acts else None


@dataclass(frozen=True)
class BenefitViews:
    """One estimate's views: the pooled one and, split by model, one per consuming model it measured."""

    pooled: BenefitView | None
    by_model: dict[str, BenefitView | None] = field(default_factory=dict)

    def for_model(self, model: str | None) -> BenefitView | None:
        """The view for ``model``, or the pooled one when it is ``None`` or was not measured."""
        if model is not None and model in self.by_model:
            return self.by_model[model]
        return self.pooled

    @staticmethod
    def of(estimate: BenefitEstimate | None) -> BenefitViews | None:
        """The views of ``estimate``, or ``None`` when it must not be acted on (not tracked, or not ``Found``)."""
        if estimate is None or not usable(estimate):
            return None
        by_model: dict[str, BenefitView | None] = {}
        for group in estimate.by_consuming_model or ():
            # An unrecorded model is never an agent's own: an agent without one reads the pooled view.
            if group is not None and group.model is not None and group.model != UNKNOWN_MODEL:
                by_model[group.model] = benefit_view(estimate, group.model)
        return BenefitViews(benefit_view(estimate, None), by_model)


# ---- Selection ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class OmittedRecord:
    """A ranked record that was not injected, and why: .NET's ``OmittedExperience``. ``detail`` is ``None`` for a
    reason that carries none (``ModelAuthored``)."""

    experience_id: uuid.UUID
    reason: OmissionReason
    detail: str | None = None


@dataclass(frozen=True)
class Selection:
    """What an invocation selects: the records before the byte budget, the block, and every omission in order."""

    selected: tuple[RankedRecord, ...]
    block: Block
    omitted: tuple[OmittedRecord, ...]


def _prefer_helpful(
    ranked: Sequence[RankedRecord], view: BenefitView, request_limit: int | None
) -> Sequence[RankedRecord]:
    if not view.helps:
        return ranked
    window = min(request_limit, len(ranked)) if request_limit is not None and request_limit > 0 else len(ranked)
    reordered: list[RankedRecord] = []
    for start, end in ((0, window), (window, len(ranked))):
        part = ranked[start:end]
        reordered.extend(item for item in part if item.record.experience_id in view.helps)
        reordered.extend(item for item in part if item.record.experience_id not in view.helps)
    return reordered


def select_candidates(
    ranked: Sequence[RankedRecord],
    view: BenefitView | None,
    *,
    max_records: int,
    request_limit: int | None = None,
    exclude_model_authored: bool = False,
    prefer_measured_helpful: bool = False,
    gate: CapabilityGate | None = None,
) -> tuple[list[RankedRecord], list[OmittedRecord]]:
    """
    .NET's ``SelectCandidates``: with a view and ``prefer_measured_helpful``, the lessons measured as helping first
    (within the request's window and within the backfill past it, separately); then, in that order, a record the view
    marks harmful is omitted as ``MeasuredHarmful``, an excluded model-authored record as ``ModelAuthored``, one the
    capability ``gate`` keeps out (contract 1.13) as ``ToolUnavailable`` or ``RiskClassExceeded``, and past
    ``min(max_records, request_limit)`` selections as ``OverRecordLimit``. A record past the request limit is backfill:
    selected if a slot is left, otherwise dropped unreported.
    """
    ordered = _prefer_helpful(ranked, view, request_limit) if view is not None and prefer_measured_helpful else ranked
    has_request_limit = request_limit is not None and request_limit > 0
    limit = min(max_records, request_limit) if has_request_limit and request_limit is not None else max_records
    reported = request_limit if has_request_limit and request_limit is not None else len(ordered)
    selected: list[RankedRecord] = []
    omitted: list[OmittedRecord] = []
    for index, candidate in enumerate(ordered):
        sink: list[OmittedRecord] = omitted if index < reported else []
        record = candidate.record
        if view is not None and view.harmful(record.experience_id):
            sink.append(
                OmittedRecord(
                    record.experience_id,
                    "MeasuredHarmful",
                    SCOPE_MEASURED_HARMFUL_DETAIL if view.scope_hurts else MEASURED_HARMFUL_DETAIL,
                )
            )
            continue
        if exclude_model_authored and is_model_authored(record.reflection):
            sink.append(OmittedRecord(record.experience_id, "ModelAuthored"))
            continue
        if gate is not None and (gated := gate.check(record)) is not None:
            sink.append(OmittedRecord(record.experience_id, gated))
            continue
        if len(selected) < limit:
            selected.append(candidate)
            continue
        sink.append(
            OmittedRecord(
                record.experience_id,
                "OverRecordLimit",
                f"Ranked {index + 1} of {len(ordered)}, beyond the limit of {limit} records.",
            )
        )
    return selected, omitted


def select_block(
    ranked: Sequence[RankedRecord],
    view: BenefitView | None,
    options: BlockOptions,
    *,
    request_limit: int | None = None,
    exclude_model_authored: bool = False,
    prefer_measured_helpful: bool = False,
    gate: CapabilityGate | None = None,
    decide: Callable[[RankedRecord], OmittedRecord | None] | None = None,
) -> Selection:
    """:func:`select_candidates`, then, with ``decide`` (the host's decision, contract 1.13), each selected record in
    order that it answers an omission for is dropped (its slot is not backfilled), then the block rendered from what is
    left: a record the byte budget drops is omitted as ``OverByteBudget``, after every other omission."""
    selected, omitted = select_candidates(
        ranked,
        view,
        max_records=options.max_records,
        request_limit=request_limit,
        exclude_model_authored=exclude_model_authored,
        prefer_measured_helpful=prefer_measured_helpful,
        gate=gate,
    )
    injectable = selected
    if decide is not None:
        injectable = []
        for candidate in selected:
            denied = decide(candidate)
            if denied is None:
                injectable.append(candidate)
            else:
                omitted.append(denied)
    block = render_block(injectable, options)
    detail = (
        f"The record did not fit in the remaining part of the {options.max_bytes}-byte budget, and a record is never "
        "cut to fit."
    )
    omitted.extend(OmittedRecord(record.experience_id, "OverByteBudget", detail) for record in block.omitted)
    return Selection(tuple(selected), block, tuple(omitted))


# ---- The cache ---------------------------------------------------------------------------------------------------


@dataclass
class _Entry:
    views: BenefitViews | None = None
    refreshed_at: Instant | None = None
    refreshing: threading.Thread | None = None


@dataclass
class _Read:
    estimate: BenefitEstimate | None = None
    error: BaseException | None = None
    done: threading.Event = field(default_factory=threading.Event)


class BenefitCache:
    """
    One agent's cached benefit estimates (.NET's ``ExperienceBenefitCache``), keyed by scope, authorization bounds
    and principal. :meth:`current` answers from the cache at once and, when the entry is missing or stale and no
    refresh runs for it, starts one on a daemon thread. Never raises and never waits on the store.
    """

    def __init__(self, store: object, policy: BenefitPolicy, clock: Callable[[], Instant]) -> None:
        self._store = store
        self._policy = policy
        self._options = policy.estimator_options
        self._clock = clock
        self._interval = Duration(_ticks(policy.refresh_interval))
        self._timeout = policy.refresh_timeout.total_seconds()
        self._lock = threading.Lock()
        self._entries: dict[tuple[Scope, AuthorizationBounds, str], _Entry] = {}
        self._threads: set[threading.Thread] = set()

    def current(
        self, bounds: AuthorizationBounds, scope: Scope, principal: str, model: str | None = None
    ) -> BenefitView | None:
        """What the cache holds for this key, or ``None`` when it holds nothing usable; starts a background refresh
        first when needed. With ``per_model``, the view for ``model`` (the invoking agent's). Never raises."""
        try:
            key = (scope, bounds, principal)
            with self._lock:
                entry = self._entries.get(key)
                if entry is None:
                    if len(self._entries) >= MAX_CACHE_ENTRIES:
                        self._evict()
                    entry = self._entries.setdefault(key, _Entry())
                now = self._clock()
                if entry.refreshing is None and (
                    entry.refreshed_at is None or now - entry.refreshed_at >= self._interval
                ):
                    entry.refreshing = self._start(entry, bounds, scope)
                views = entry.views
            return None if views is None else views.for_model(model if self._policy.per_model else None)
        except Exception:
            # A cache that cannot answer has no effect: the invocation is injected as without the policy.
            return None

    def __len__(self) -> int:
        with self._lock:
            return len(self._entries)

    def wait(self, timeout: float | None = None) -> None:
        """Blocks until every refresh started so far has ended (at most ``timeout`` seconds each). For tests."""
        while True:
            with self._lock:
                running = [thread for thread in self._threads if thread.is_alive()]
            if not running:
                return
            for thread in running:
                thread.join(timeout)
            if timeout is not None:
                return

    async def settled(self) -> None:
        """Completes once every refresh started so far has ended (.NET's ``SettledAsync``). For tests and shutdown."""
        await asyncio.to_thread(self.wait)

    def _start(self, entry: _Entry, bounds: AuthorizationBounds, scope: Scope) -> threading.Thread:
        thread = threading.Thread(
            target=self._refresh, args=(entry, bounds, scope), name="agentexperience-benefit-refresh", daemon=True
        )
        self._threads.add(thread)
        thread.start()
        return thread

    def _refresh(self, entry: _Entry, bounds: AuthorizationBounds, scope: Scope) -> None:
        """One refresh: the window's estimate within ``refresh_timeout``; reported as ``benefit_refresh``. Never
        raises."""
        outcome = "Refreshed"
        error_class: str | None = None
        views: BenefitViews | None = None
        replaced = False
        try:
            with observe("benefit_refresh") as op:
                try:
                    read = _Read()
                    assigned_from = self._policy.assigned_from(self._clock())

                    def run() -> None:
                        try:
                            read.estimate = estimate_benefit(
                                self._store, bounds, scope, self._options, assigned_from=assigned_from
                            )
                        except BaseException as ex:  # reported by the refresh, never raised in a thread
                            read.error = ex
                        finally:
                            read.done.set()

                    # On a thread of its own, so a store that blocks cannot hold the timeout off; one that overruns is
                    # abandoned (a thread cannot be stopped) and its answer ignored.
                    threading.Thread(target=run, name="agentexperience-benefit-read", daemon=True).start()
                    if not read.done.wait(self._timeout):
                        outcome, error_class = "OutcomeUnknown", "Timeout"
                    elif read.error is not None:
                        raise read.error
                    else:
                        views = BenefitViews.of(read.estimate)
                        replaced = True
                except (TimeoutError, asyncio.TimeoutError):
                    outcome, error_class = "OutcomeUnknown", "Timeout"
                except asyncio.CancelledError:
                    outcome, error_class = "OutcomeUnknown", "Cancelled"
                except Exception as ex:
                    outcome = "NotRefreshed"
                    error_class = INFRASTRUCTURE if classify(ex) == INFRASTRUCTURE else UNEXPECTED
                finally:
                    with self._lock:
                        if replaced:
                            entry.views = views
                        # A failed refresh waits out the interval too: a failing store is not asked on every invocation.
                        try:
                            entry.refreshed_at = self._clock()
                        except Exception:
                            entry.refreshed_at = None
                        entry.refreshing = None
                if op is not None:
                    op.outcome(outcome, error_class)
        finally:
            # Only once the observation has closed (and the report gone out), so a wait that returns has seen it.
            with self._lock:
                self._threads.discard(threading.current_thread())

    def _evict(self) -> None:
        """Drops the entry refreshed longest ago (one never refreshed counts as oldest), unless it is refreshing."""
        oldest: tuple[Any, _Entry] | None = None
        for key, entry in self._entries.items():
            if entry.refreshing is not None:
                continue
            at = entry.refreshed_at.ticks if entry.refreshed_at is not None else -1
            if oldest is None or at < (oldest[1].refreshed_at.ticks if oldest[1].refreshed_at is not None else -1):
                oldest = (key, entry)
        if oldest is not None:
            del self._entries[oldest[0]]


__all__ = [
    "DEFAULT_REFRESH_INTERVAL",
    "DEFAULT_REFRESH_TIMEOUT",
    "DEFAULT_WINDOW",
    "MAX_CACHE_ENTRIES",
    "MAX_REFRESH_TIMEOUT",
    "MEASURED_HARMFUL_DETAIL",
    "SCOPE_MEASURED_HARMFUL_DETAIL",
    "BenefitCache",
    "BenefitPolicy",
    "BenefitView",
    "BenefitViews",
    "OmissionReason",
    "OmittedRecord",
    "Selection",
    "benefit_view",
    "select_block",
    "select_candidates",
    "usable",
]
