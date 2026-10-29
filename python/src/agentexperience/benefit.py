"""
The benefit estimate (contract 1.9): whether the lessons help, measured from the injection holdout. With the holdout on,
some captured runs are given their lessons and the rest have them withheld; this compares how often each arm's runs
verified, overall, per lesson and, on request, per model. The same estimate, with the same numbers, as .NET's
``AgentExperienceBenefit.EstimateAsync``.

:func:`estimate_benefit` reads a store's outcome rows a page at a time, under the caller's own authorization and in
exactly the scope passed in, and folds each page into a :class:`BenefitAccumulator`, so it holds one page at a time.
:func:`estimate` is the pure form over rows read any other way. Neither writes, and neither changes ranking,
confidence or injection.

The interval of the difference is Newcombe's hybrid score interval, built from each arm's Wilson score interval at
z = 1.959963984540054: it stays inside [-1, 1] and behaves at rates of 0 and 1.
"""

from __future__ import annotations

import math
import uuid
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from typing import Literal, Protocol, runtime_checkable

from ._text import is_null_or_whitespace, utf16_sort_key
from .holdout import (
    HOLDOUT_ARMS,
    MAX_HOLDOUT_OUTCOME_LIMIT,
    HoldoutArm,
    HoldoutOutcomeCursor,
    HoldoutOutcomeRow,
    check_outcome_limit,
)
from .inmemory import AuthorizationDenied, permits
from .records import Scope
from .search import AuthorizationBounds

Z = 1.959963984540054
"""The two-sided 95% normal quantile: .NET's ``ExperienceBenefitEstimator.Z``."""

DEFAULT_MINIMUM_RUNS_PER_ARM = 20
"""The default for :attr:`BenefitOptions.minimum_runs_per_arm`."""

UNKNOWN_MODEL = "unknown"
"""The model a split estimate groups runs and lessons under when none was recorded."""

BenefitVerdict = Literal["NoEvidence", "Helps", "Hurts"]
"""``NoEvidence``: the data cannot support a claim (too few runs, or the interval spans zero). ``Helps``: the whole
interval lies above zero. ``Hurts``: below."""

EstimateOutcome = Literal["Found", "Denied"]
"""``Found``, or ``Denied`` when the scope lies outside the authorization (nothing was read)."""

VerificationStatus = Literal["Verified", "Failed", "Unknown"]


@runtime_checkable
class HoldoutOutcomeReader(Protocol):
    """
    A store that reads the holdout's outcome rows (contract 1.9): a page of the rows in exactly ``scope``, in (run,
    record) order, after ``after``, at most ``limit`` (1 to 1000). Raises
    :class:`~agentexperience.inmemory.AuthorizationDenied` outside the bounds and ``ValueError`` for a bad limit.
    """

    def read_holdout_outcomes(
        self, bounds: AuthorizationBounds, scope: Scope, limit: int, after: HoldoutOutcomeCursor | None = None
    ) -> list[HoldoutOutcomeRow]: ...


@dataclass(frozen=True)
class BenefitOptions:
    """
    How the verdict is decided: ``minimum_runs_per_arm`` (at least 1; 20 by default) is the fewest runs with a
    verified outcome each arm needs before the verdict can be anything but ``NoEvidence``; ``split_by_model`` also
    compares the runs per model. ``ValueError`` for a minimum below 1.
    """

    minimum_runs_per_arm: int = DEFAULT_MINIMUM_RUNS_PER_ARM
    split_by_model: bool = False

    def __post_init__(self) -> None:
        if isinstance(self.minimum_runs_per_arm, bool) or not isinstance(self.minimum_runs_per_arm, int):
            raise TypeError("minimum_runs_per_arm must be an integer.")
        if self.minimum_runs_per_arm < 1:
            raise ValueError("minimum_runs_per_arm must be at least 1.")


@dataclass(frozen=True)
class BenefitArm:
    """One arm's verified runs: how many (``runs``), and how many of them verified (``successes``)."""

    runs: int
    successes: int

    @property
    def rate(self) -> float | None:
        """``successes / runs``; ``None`` with no runs."""
        return None if self.runs == 0 else self.successes / self.runs


@dataclass(frozen=True)
class BenefitComparison:
    """
    The two arms compared: the counts and rates, the difference (treatment minus holdout) with its 95% interval
    (``None`` while either arm has no runs), the verdict, and the runs left out of the rates.
    """

    treatment: BenefitArm
    holdout: BenefitArm
    difference: float | None
    lower: float | None
    upper: float | None
    verdict: BenefitVerdict
    unverified: int
    pending: int
    mixed_arm: int


@dataclass(frozen=True)
class ModelPairBenefit:
    """For one lesson, its runs of one model (``run_model``), the lesson learned by ``lesson_model``."""

    lesson_model: str
    run_model: str
    comparison: BenefitComparison


@dataclass(frozen=True)
class LessonBenefit:
    """The comparison over the runs whose blocks named one lesson; ``by_model_pair`` only when split by model."""

    experience_id: uuid.UUID
    comparison: BenefitComparison
    by_model_pair: tuple[ModelPairBenefit, ...] | None = None


@dataclass(frozen=True)
class ModelBenefit:
    """The comparison over the runs of one consuming model (``unknown`` when not recorded)."""

    model: str
    comparison: BenefitComparison


@dataclass(frozen=True)
class BenefitEstimate:
    """
    What the estimate found: ``overall``, per lesson (ordered by experience ID), and, when split by model,
    ``by_consuming_model``. ``tracked`` is ``False`` for a store that does not record the holdout (nothing was read);
    ``outcome`` is ``Denied`` for a scope outside the authorization (nothing was read).
    """

    overall: BenefitComparison
    lessons: tuple[LessonBenefit, ...]
    by_consuming_model: tuple[ModelBenefit, ...] | None = None
    tracked: bool = True
    outcome: EstimateOutcome = "Found"
    minimum_runs_per_arm: int = DEFAULT_MINIMUM_RUNS_PER_ARM


def wilson(successes: int, n: int) -> tuple[float, float]:
    """The Wilson score interval for ``successes`` of ``n`` (n > 0), clamped to [0, 1], as .NET computes it."""
    p = successes / n
    z2 = Z * Z
    denominator = 1 + (z2 / n)
    centre = (p + (z2 / (2.0 * n))) / denominator
    half = Z * math.sqrt((p * (1 - p) / n) + (z2 / (4.0 * n * n))) / denominator
    return max(0.0, centre - half), min(1.0, centre + half)


def newcombe(successes1: int, n1: int, successes2: int, n2: int) -> tuple[float, float, float]:
    """The difference p1 - p2 and Newcombe's hybrid score interval for it, clamped to [-1, 1]."""
    p1 = successes1 / n1
    p2 = successes2 / n2
    l1, u1 = wilson(successes1, n1)
    l2, u2 = wilson(successes2, n2)
    d = p1 - p2
    lower = d - math.sqrt(((p1 - l1) * (p1 - l1)) + ((u2 - p2) * (u2 - p2)))
    upper = d + math.sqrt(((u1 - p1) * (u1 - p1)) + ((p2 - l2) * (p2 - l2)))
    return d, max(-1.0, lower), min(1.0, upper)


def compare(
    treatment_runs: int,
    treatment_successes: int,
    holdout_runs: int,
    holdout_successes: int,
    minimum_runs_per_arm: int,
    unverified: int = 0,
    pending: int = 0,
    mixed_arm: int = 0,
) -> BenefitComparison:
    """Compares two arms' verified counts: the rates, the difference, its interval and the verdict."""
    treatment = BenefitArm(treatment_runs, treatment_successes)
    holdout = BenefitArm(holdout_runs, holdout_successes)
    if treatment_runs == 0 or holdout_runs == 0:
        return BenefitComparison(treatment, holdout, None, None, None, "NoEvidence", unverified, pending, mixed_arm)
    difference, lower, upper = newcombe(treatment_successes, treatment_runs, holdout_successes, holdout_runs)
    verdict: BenefitVerdict
    if treatment_runs < minimum_runs_per_arm or holdout_runs < minimum_runs_per_arm:
        verdict = "NoEvidence"
    elif lower > 0:
        verdict = "Helps"
    elif upper < 0:
        verdict = "Hurts"
    else:
        verdict = "NoEvidence"
    return BenefitComparison(treatment, holdout, difference, lower, upper, verdict, unverified, pending, mixed_arm)


def least_model(current: str | None, candidate: str | None) -> str | None:
    """Of two models, the one the estimate reads: the ordinal least (UTF-16 order), blanks ignored."""
    if is_null_or_whitespace(candidate):
        return current
    assert candidate is not None
    if current is None or utf16_sort_key(candidate) < utf16_sort_key(current):
        return candidate
    return current


@dataclass(frozen=True)
class _RunClass:
    arm: HoldoutArm | None  # None: recorded in both arms
    outcome: str | None


@dataclass
class _Counts:
    treatment_runs: int = 0
    treatment_successes: int = 0
    holdout_runs: int = 0
    holdout_successes: int = 0
    unverified: int = 0
    pending: int = 0
    mixed: int = 0

    def count(self, run: _RunClass) -> None:
        if run.arm is None:
            self.mixed += 1
            return
        if run.outcome is None:
            self.pending += 1
            return
        if run.outcome not in ("Verified", "Failed"):
            self.unverified += 1
            return
        success = 1 if run.outcome == "Verified" else 0
        if run.arm == "Treatment":
            self.treatment_runs += 1
            self.treatment_successes += success
        else:
            self.holdout_runs += 1
            self.holdout_successes += success

    def compare(self, minimum: int) -> BenefitComparison:
        return compare(
            self.treatment_runs,
            self.treatment_successes,
            self.holdout_runs,
            self.holdout_successes,
            minimum,
            self.unverified,
            self.pending,
            self.mixed,
        )


def _options(options: BenefitOptions | None) -> BenefitOptions:
    if options is None:
        return BenefitOptions()
    if not isinstance(options, BenefitOptions):
        raise TypeError("options must be BenefitOptions or None.")
    return options


class BenefitAccumulator:
    """
    The paged form of :func:`estimate`: rows are added a run at a time, as a store pages them, and each run is folded
    into running counts once the next run starts. It keeps the run IDs it has seen and one counter set per lesson (and
    per model), never the rows. Every row of a run must be added before any row of the next; a row of a run already
    folded raises ``ValueError``, since folding it again would count the run twice. Not thread-safe.
    """

    def __init__(self, options: BenefitOptions | None = None) -> None:
        resolved = _options(options)
        self._minimum = resolved.minimum_runs_per_arm
        self._split = resolved.split_by_model
        self._folded: set[uuid.UUID] = set()
        self._overall = _Counts()
        self._lessons: dict[uuid.UUID, _Counts] = {}
        self._models: dict[str, _Counts] = {}
        self._pairs: dict[uuid.UUID, dict[tuple[str, str], _Counts]] = {}
        self._open_run: uuid.UUID | None = None
        self._open_arm: HoldoutArm | None = None
        self._open_mixed = False
        self._open_outcome: str | None = None
        self._open_outcome_from = uuid.UUID(int=0)
        self._open_model: str | None = None
        self._open_lessons: dict[uuid.UUID, str | None] = {}

    def add(self, row: HoldoutOutcomeRow | None) -> None:
        """Adds one row; ``None`` is ignored. ``ValueError`` when the row's run was already folded."""
        if row is None:
            return
        if row.arm not in HOLDOUT_ARMS:
            raise ValueError("The arm must be 'Treatment' or 'Holdout'.")
        if self._open_run != row.run_id:
            if row.run_id in self._folded:
                raise ValueError(
                    f"Run {row.run_id} was added again after another run's rows: add every row of a run before the "
                    "next run's."
                )
            self._fold()
            self._open_run = row.run_id
            self._open_arm = row.arm
        elif self._open_arm != row.arm:
            self._open_mixed = True
        # The outcome the pure form reads a run with: the first carried one in record order.
        if row.run_outcome is not None and (self._open_outcome is None or row.experience_id < self._open_outcome_from):
            self._open_outcome = row.run_outcome
            self._open_outcome_from = row.experience_id
        self._open_model = least_model(self._open_model, row.run_model)
        self._open_lessons[row.experience_id] = least_model(self._open_lessons.get(row.experience_id), row.lesson_model)

    def add_all(self, rows: Iterable[HoldoutOutcomeRow | None]) -> None:
        """Adds every row, in order."""
        for row in rows:
            self.add(row)

    def to_estimate(self) -> BenefitEstimate:
        """The estimate over every row added so far; the run being added is folded first."""
        self._fold()
        lessons = []
        for experience_id in sorted(self._lessons):
            pairs = None
            if self._split:
                pairs = tuple(
                    ModelPairBenefit(lesson_model, run_model, counts.compare(self._minimum))
                    for (lesson_model, run_model), counts in sorted(
                        self._pairs[experience_id].items(),
                        key=lambda entry: (utf16_sort_key(entry[0][0]), utf16_sort_key(entry[0][1])),
                    )
                )
            lessons.append(LessonBenefit(experience_id, self._lessons[experience_id].compare(self._minimum), pairs))
        models = None
        if self._split:
            models = tuple(
                ModelBenefit(model, self._models[model].compare(self._minimum))
                for model in sorted(self._models, key=utf16_sort_key)
            )
        return BenefitEstimate(
            self._overall.compare(self._minimum), tuple(lessons), models, minimum_runs_per_arm=self._minimum
        )

    def _fold(self) -> None:
        run = self._open_run
        if run is None:
            return
        run_class = _RunClass(None if self._open_mixed else self._open_arm, self._open_outcome)
        self._overall.count(run_class)
        run_model = self._open_model or UNKNOWN_MODEL
        if self._split:
            self._models.setdefault(run_model, _Counts()).count(run_class)
        for lesson, lesson_model in self._open_lessons.items():
            self._lessons.setdefault(lesson, _Counts()).count(run_class)
            if self._split:
                pair = (lesson_model or UNKNOWN_MODEL, run_model)
                self._pairs.setdefault(lesson, {}).setdefault(pair, _Counts()).count(run_class)
        self._folded.add(run)
        self._open_run = None
        self._open_arm = None
        self._open_mixed = False
        self._open_outcome = None
        self._open_outcome_from = uuid.UUID(int=0)
        self._open_model = None
        self._open_lessons = {}


def estimate(rows: Iterable[HoldoutOutcomeRow | None], options: BenefitOptions | None = None) -> BenefitEstimate:
    """
    The pure estimate over ``rows`` (``None`` rows ignored), in any order: overall, per lesson and, with
    ``options.split_by_model``, per model. Equal to :class:`BenefitAccumulator` over the same rows.
    """
    resolved = _options(options)
    ordered = sorted((row for row in rows if row is not None), key=lambda row: (row.run_id, row.experience_id))
    accumulator = BenefitAccumulator(resolved)
    accumulator.add_all(ordered)
    return accumulator.to_estimate()


def empty_estimate(options: BenefitOptions | None = None) -> BenefitEstimate:
    """An estimate with nothing in it: every count 0 and no verdict."""
    return estimate((), options)


def estimate_benefit(
    store: object,
    authorization: AuthorizationBounds,
    scope: Scope,
    options: BenefitOptions | None = None,
) -> BenefitEstimate:
    """
    Estimates the lessons' benefit in exactly ``scope`` (the scope the blocks were built in), overall, per lesson and,
    with ``options.split_by_model``, per model: .NET's ``AgentExperienceBenefit.EstimateAsync``.

    It reads ``store``'s outcome rows (:class:`HoldoutOutcomeReader`) 1,000 at a time under ``authorization`` and
    folds each page before reading the next. A store that does not read them gives an empty estimate with ``tracked``
    ``False``; a scope outside the authorization gives an empty one whose ``outcome`` is ``Denied``, never an
    exception. ``ValueError`` for invalid options (before anything is read) and for a store that pages a run's rows
    apart. A run whose record is sealed (``payload_version`` 2, unreadable by a port) counts as pending. Reads only.
    The authorization and scope must come from the host's own authentication, never from a request.
    """
    resolved = _options(options)
    empty = empty_estimate(resolved)
    if not isinstance(authorization, AuthorizationBounds):
        raise TypeError("authorization must be AuthorizationBounds.")
    if not isinstance(scope, Scope):
        raise TypeError("scope must be a Scope.")
    if not isinstance(store, HoldoutOutcomeReader):
        return BenefitEstimate(empty.overall, (), empty.by_consuming_model, False, "Found", empty.minimum_runs_per_arm)
    if not permits(authorization, scope):
        return BenefitEstimate(empty.overall, (), empty.by_consuming_model, True, "Denied", empty.minimum_runs_per_arm)
    accumulator = BenefitAccumulator(resolved)
    after: HoldoutOutcomeCursor | None = None
    while True:
        try:
            page: Sequence[HoldoutOutcomeRow] = store.read_holdout_outcomes(
                authorization, scope, MAX_HOLDOUT_OUTCOME_LIMIT, after
            )
        except AuthorizationDenied:
            return BenefitEstimate(
                empty.overall, (), empty.by_consuming_model, True, "Denied", empty.minimum_runs_per_arm
            )
        accumulator.add_all(page)
        if len(page) < MAX_HOLDOUT_OUTCOME_LIMIT:
            break
        last = page[-1]
        cursor = (last.run_id, last.experience_id)
        # A store whose page does not move on would loop forever.
        if cursor == after:
            break
        after = cursor
    return accumulator.to_estimate()


__all__ = [
    "DEFAULT_MINIMUM_RUNS_PER_ARM",
    "MAX_HOLDOUT_OUTCOME_LIMIT",
    "UNKNOWN_MODEL",
    "BenefitAccumulator",
    "BenefitArm",
    "BenefitComparison",
    "BenefitEstimate",
    "BenefitOptions",
    "BenefitVerdict",
    "EstimateOutcome",
    "HoldoutOutcomeCursor",
    "HoldoutOutcomeReader",
    "HoldoutOutcomeRow",
    "LessonBenefit",
    "ModelBenefit",
    "ModelPairBenefit",
    "VerificationStatus",
    "Z",
    "check_outcome_limit",
    "compare",
    "empty_estimate",
    "estimate",
    "estimate_benefit",
    "least_model",
    "newcombe",
    "wilson",
]
