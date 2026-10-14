"""The contract's value encodings: instants, durations, UUIDs and numbers, exactly as the stored record writes them."""

from __future__ import annotations

import datetime as _dt
import math
import re
import uuid
from dataclasses import dataclass
from typing import Any

TICKS_PER_SECOND = 10_000_000
TICKS_PER_MICROSECOND = 10
_TICKS_PER_DAY = 86_400 * TICKS_PER_SECOND

_INSTANT = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2})T(?P<h>\d{2}):(?P<m>\d{2}):(?P<s>\d{2})(?:\.(?P<f>\d+))?"
    r"(?P<offset>Z|[+-]\d{2}:\d{2})$"
)
_DURATION = re.compile(r"^(?P<sign>-)?(?:(?P<d>\d+)\.)?(?P<h>\d{2}):(?P<m>\d{2}):(?P<s>\d{2})(?:\.(?P<f>\d{1,7}))?$")


@dataclass(frozen=True, order=True)
class Instant:
    """
    A point in time in UTC, with .NET's precision: whole 100-nanosecond ticks since 0001-01-01T00:00:00Z.

    Payload instants keep up to seven fractional digits, which ``datetime`` cannot hold, so the contract's instants
    are this type rather than ``datetime``.
    """

    ticks: int

    @staticmethod
    def parse(text: str) -> Instant:
        """Reads an ISO 8601 instant with an offset (``+00:00`` or any other, or ``Z``), normalized to UTC."""
        match = _INSTANT.match(text)
        if match is None:
            raise ValueError(f"'{text}' is not an ISO 8601 instant with an offset.")
        date = _dt.date.fromisoformat(match["date"])
        fraction = (match["f"] or "").ljust(7, "0")
        if len(fraction) > 7:
            raise ValueError(f"'{text}' has more than seven fractional digits.")
        _check_time(text, int(match["h"]), int(match["m"]), int(match["s"]))
        seconds = int(match["h"]) * 3600 + int(match["m"]) * 60 + int(match["s"])
        ticks = (date.toordinal() - 1) * _TICKS_PER_DAY + seconds * TICKS_PER_SECOND + int(fraction)
        offset = match["offset"]
        if offset != "Z":
            if int(offset[4:6]) >= 60:
                raise ValueError(f"'{text}' has an offset whose minutes are 60 or more.")
            minutes = int(offset[1:3]) * 60 + int(offset[4:6])
            if minutes > 14 * 60:
                raise ValueError(f"'{text}' has an offset beyond 14:00.")
            ticks -= (1 if offset[0] == "+" else -1) * minutes * 60 * TICKS_PER_SECOND
        return Instant(ticks)

    @staticmethod
    def from_datetime(value: _dt.datetime) -> Instant:
        """An aware ``datetime`` as an instant (microsecond precision)."""
        if value.tzinfo is None:
            raise ValueError("The datetime must be timezone-aware.")
        utc = value.astimezone(_dt.timezone.utc)
        seconds = utc.hour * 3600 + utc.minute * 60 + utc.second
        return Instant(
            (utc.toordinal() - 1) * _TICKS_PER_DAY
            + seconds * TICKS_PER_SECOND
            + utc.microsecond * TICKS_PER_MICROSECOND
        )

    def to_datetime(self) -> _dt.datetime:
        """This instant as an aware UTC ``datetime``; the sub-microsecond part is truncated."""
        days, rest = divmod(self.ticks, _TICKS_PER_DAY)
        date = _dt.date.fromordinal(days + 1)
        seconds, fraction = divmod(rest, TICKS_PER_SECOND)
        return _dt.datetime(
            date.year,
            date.month,
            date.day,
            seconds // 3600,
            seconds // 60 % 60,
            seconds % 60,
            fraction // TICKS_PER_MICROSECOND,
            tzinfo=_dt.timezone.utc,
        )

    def truncate_to_microseconds(self) -> Instant:
        """Whole microseconds, truncated, never rounded: what a ``timestamptz`` column keeps."""
        return Instant(self.ticks - self.ticks % TICKS_PER_MICROSECOND)

    def add(self, duration: Duration) -> Instant:
        return Instant(self.ticks + duration.ticks)

    def __sub__(self, other: Instant) -> Duration:
        return Duration(self.ticks - other.ticks)

    def format(self) -> str:
        """``2026-03-02T09:00:30.25+00:00``: UTC with the offset ``+00:00``, trailing zeros of the fraction dropped."""
        days, rest = divmod(self.ticks, _TICKS_PER_DAY)
        date = _dt.date.fromordinal(days + 1)
        seconds, fraction = divmod(rest, TICKS_PER_SECOND)
        text = f"{date.isoformat()}T{seconds // 3600:02d}:{seconds // 60 % 60:02d}:{seconds % 60:02d}"
        if fraction:
            text += "." + f"{fraction:07d}".rstrip("0")
        return text + "+00:00"

    def format_seconds_z(self) -> str:
        """``2026-03-02T09:00:30Z``: whole seconds, the form the verbose block shows."""
        return self.format()[:19] + "Z"

    def __str__(self) -> str:
        return self.format()


@dataclass(frozen=True, order=True)
class Duration:
    """A duration in .NET ticks, written in the constant ``TimeSpan`` format ``[-][d.]hh:mm:ss[.fffffff]``."""

    ticks: int

    @staticmethod
    def parse(text: str) -> Duration:
        match = _DURATION.match(text)
        if match is None:
            raise ValueError(f"'{text}' is not a duration in the format [-][d.]hh:mm:ss[.fffffff].")
        _check_time(text, int(match["h"]), int(match["m"]), int(match["s"]))
        seconds = int(match["d"] or 0) * 86_400 + int(match["h"]) * 3600 + int(match["m"]) * 60 + int(match["s"])
        ticks = seconds * TICKS_PER_SECOND + int((match["f"] or "").ljust(7, "0"))
        return Duration(-ticks if match["sign"] else ticks)

    @staticmethod
    def from_seconds(seconds: float) -> Duration:
        return Duration(round(seconds * TICKS_PER_SECOND))

    @property
    def total_seconds(self) -> float:
        """.NET's ``TimeSpan.TotalSeconds``: the ticks as a double, divided by ticks per second."""
        return float(self.ticks) / TICKS_PER_SECOND

    def format(self) -> str:
        ticks = abs(self.ticks)
        days, rest = divmod(ticks, _TICKS_PER_DAY)
        seconds, fraction = divmod(rest, TICKS_PER_SECOND)
        text = "-" if self.ticks < 0 else ""
        if days:
            text += f"{days}."
        text += f"{seconds // 3600:02d}:{seconds // 60 % 60:02d}:{seconds % 60:02d}"
        if fraction:
            text += f".{fraction:07d}"
        return text

    def __str__(self) -> str:
        return self.format()


def _check_time(text: str, hours: int, minutes: int, seconds: int) -> None:
    if hours >= 24 or minutes >= 60 or seconds >= 60:
        raise ValueError(f"'{text}' has hours over 23, or minutes or seconds over 59.")


def format_uuid(value: uuid.UUID) -> str:
    """A UUID as the contract writes one: lower-case, hyphenated."""
    return str(value)


def parse_uuid(text: str) -> uuid.UUID:
    return uuid.UUID(text)


def json_number(value: float) -> float | int:
    """
    A double as .NET's serializer writes it: an integral value has no fraction (``1``, not ``1.0``), and negative
    zero keeps its sign (.NET writes ``-0``; it stays the float ``-0.0`` here, the same JSON value). A JSON reader
    reads either the same, but this keeps what Python writes as close in form to what .NET writes as JSON allows.
    """
    if value == 0 and math.copysign(1.0, value) < 0:
        return value
    if math.isfinite(value) and value == int(value) and abs(value) < 2**53:
        return int(value)
    return value


def json_value(value: Any) -> Any:
    """A JSON value with every double written as :func:`json_number` writes it."""
    if isinstance(value, bool) or value is None or isinstance(value, (int, str)):
        return value
    if isinstance(value, float):
        return json_number(value)
    if isinstance(value, dict):
        return {key: json_value(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_value(item) for item in value]
    raise TypeError(f"{type(value).__name__} is not a JSON value.")
