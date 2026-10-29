"""
The injection holdout (contract 1.8): on a deterministic share of captured runs that have a block to inject, the block
is withheld, and both arms are recorded, so the .NET library's benefit estimate can compare the runs' verified outcomes.
Off by default (rate 0).

The arm is a pure function of the run ID, the salt and the rate: SHA-256 over the run ID's 16 bytes in RFC 4122 order
followed by the salt's UTF-8 bytes; the digest's first 8 bytes, read as a big-endian unsigned integer and divided by
2^64, give the run's fraction; the run is held out when the rate is at least 1 or the fraction is strictly below it.
"""

from __future__ import annotations

import hashlib
import math
import uuid
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal

from .encoding import Instant
from .records import RunExposure

DEFAULT_HOLDOUT_SALT = "agentexperience-holdout-v1"
"""The default salt: .NET's ``ExperienceHoldoutOptions.DefaultSalt``."""

MAX_HOLDOUT_RECORDS = 200
"""The most records one assignment write names: the .NET store's limit. The agent integration splits a longer list."""

HoldoutArm = Literal["Treatment", "Holdout"]
"""``Treatment``: the block was delivered. ``Holdout``: it was built and withheld."""

HOLDOUT_ARMS: tuple[HoldoutArm, ...] = ("Treatment", "Holdout")

_TWO_TO_THE_64 = float(2**64)


@dataclass(frozen=True)
class HoldoutOptions:
    """
    The injection holdout. ``rate`` is the share of eligible runs to hold out, from 0 (off, the default) to 1 (every
    one); ``salt`` is mixed into every assignment, so two experiments over the same runs can draw different arms.
    ``ValueError`` for a rate that is not a number from 0 to 1, or an empty salt.
    """

    rate: float = 0.0
    salt: str = DEFAULT_HOLDOUT_SALT

    def __post_init__(self) -> None:
        if isinstance(self.rate, bool) or not isinstance(self.rate, (int, float)):
            raise TypeError("The holdout rate must be a number from 0 to 1.")
        if math.isnan(self.rate) or not 0.0 <= self.rate <= 1.0:
            raise ValueError("The holdout rate must be a number from 0 to 1.")
        if not isinstance(self.salt, str):
            raise TypeError("The holdout salt must be a string.")
        if not self.salt:
            raise ValueError("The holdout salt must not be empty.")

    @property
    def active(self) -> bool:
        """Whether any run can be held out or assigned at all."""
        return self.rate > 0


def holdout_fraction(run_id: uuid.UUID, salt: str) -> float:
    """The run's position in [0, 1]: SHA-256 over the run ID's RFC 4122 bytes and the salt's UTF-8 bytes, the first 8
    bytes as a big-endian unsigned integer, divided by 2^64 (as a double, as .NET divides)."""
    digest = hashlib.sha256(run_id.bytes + salt.encode("utf-8")).digest()
    return float(int.from_bytes(digest[:8], "big")) / _TWO_TO_THE_64


def assign_holdout(run_id: uuid.UUID, salt: str, rate: float) -> HoldoutArm:
    """The run's arm at ``rate``: ``Holdout`` when the rate is at least 1 or the fraction is strictly below it."""
    return "Holdout" if rate >= 1 or holdout_fraction(run_id, salt) < rate else "Treatment"


@dataclass(frozen=True)
class HoldoutAssignment:
    """
    One captured run's holdout assignment, as a store records it: the run, its arm, the records its block named (each at
    the revision it was rendered from: what was delivered, or would have been), and when the arm was decided.
    """

    run_id: uuid.UUID
    arm: HoldoutArm
    records: tuple[RunExposure, ...]
    assigned_at: Instant


def holdout_records(assignment: HoldoutAssignment) -> list[RunExposure]:
    """
    The assignment's records to write, each once, at the revision first named; ``ValueError`` for a nil run ID, an
    unknown arm, more than :data:`MAX_HOLDOUT_RECORDS` records, a nil record ID or a negative revision, and
    ``TypeError`` for an instant that is not an :class:`Instant`.
    """
    if not isinstance(assignment, HoldoutAssignment):
        raise TypeError("assignment must be a HoldoutAssignment.")
    if not isinstance(assignment.run_id, uuid.UUID) or assignment.run_id.int == 0:
        raise ValueError("The run ID must be a non-nil UUID.")
    if assignment.arm not in HOLDOUT_ARMS:
        raise ValueError("The arm must be 'Treatment' or 'Holdout'.")
    if not isinstance(assignment.assigned_at, Instant):
        raise TypeError("assigned_at must be an Instant.")
    records: Sequence[RunExposure] = assignment.records
    if len(records) > MAX_HOLDOUT_RECORDS:
        raise ValueError(f"A holdout assignment names at most {MAX_HOLDOUT_RECORDS} records.")
    distinct: dict[uuid.UUID, RunExposure] = {}
    for exposure in records:
        if (
            not isinstance(exposure, RunExposure)
            or not isinstance(exposure.experience_id, uuid.UUID)
            or exposure.experience_id.int == 0
        ):
            raise ValueError("Every named record's ID must be a non-nil UUID.")
        if isinstance(exposure.revision, bool) or not isinstance(exposure.revision, int) or exposure.revision < 0:
            raise ValueError("Every named record's revision must be a non-negative integer.")
        distinct.setdefault(exposure.experience_id, exposure)
    return list(distinct.values())


MIN_HOLDOUT_OUTCOME_LIMIT = 1
MAX_HOLDOUT_OUTCOME_LIMIT = 1000
"""The largest page of outcome rows a store reads, and the page size :func:`estimate_benefit` reads with."""


@dataclass(frozen=True)
class HoldoutOutcomeRow:
    """
    One holdout assignment row as a store reads it back (contract 1.9): the run, its arm, one record its block named,
    when the arm was decided, and the run's own outcome (``None`` when the run has no record the port can read: not
    finalized, finalized in another scope, or sealed). ``run_model`` is the run's record's model and ``lesson_model``
    the named lesson's, each ``None`` when not recorded.
    """

    run_id: uuid.UUID
    arm: HoldoutArm
    experience_id: uuid.UUID
    assigned_at: Instant
    run_outcome: str | None
    run_model: str | None = None
    lesson_model: str | None = None


HoldoutOutcomeCursor = tuple[uuid.UUID, uuid.UUID]
"""Where a page resumes: after this (run ID, experience ID)."""


def check_outcome_limit(limit: int) -> None:
    """``ValueError`` for a page size outside 1 to 1000."""
    if (
        isinstance(limit, bool)
        or not isinstance(limit, int)
        or not MIN_HOLDOUT_OUTCOME_LIMIT <= limit <= MAX_HOLDOUT_OUTCOME_LIMIT
    ):
        raise ValueError(f"The limit must be from {MIN_HOLDOUT_OUTCOME_LIMIT} to {MAX_HOLDOUT_OUTCOME_LIMIT}.")


__all__ = [
    "DEFAULT_HOLDOUT_SALT",
    "HOLDOUT_ARMS",
    "MAX_HOLDOUT_OUTCOME_LIMIT",
    "MAX_HOLDOUT_RECORDS",
    "MIN_HOLDOUT_OUTCOME_LIMIT",
    "HoldoutArm",
    "HoldoutAssignment",
    "HoldoutOptions",
    "HoldoutOutcomeCursor",
    "HoldoutOutcomeRow",
    "assign_holdout",
    "check_outcome_limit",
    "holdout_fraction",
    "holdout_records",
]
