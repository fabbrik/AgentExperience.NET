"""Text primitives the contract counts in: UTF-16 code units, .NET's character classes, and .NET's number formats.

The contract is generated from .NET, where a string is UTF-16 and every length, cut and limit is in code units. A
Python ``str`` is a sequence of code points, so every length the contract states is measured here, never with ``len``.
"""

from __future__ import annotations

import math
import re
import unicodedata
from decimal import ROUND_HALF_UP, Decimal

HIGH_SURROGATES = range(0xD800, 0xDC00)
SURROGATES = range(0xD800, 0xE000)


def utf16_len(text: str) -> int:
    """The length of ``text`` in UTF-16 code units, as .NET's ``string.Length`` counts it."""
    return len(text) + sum(1 for ch in text if ord(ch) > 0xFFFF)


def utf16_prefix(text: str, units: int) -> str:
    """
    The longest prefix of ``text`` that fits in ``units`` UTF-16 code units without splitting a surrogate pair.

    This is .NET's ``text[..cut]`` after ``if (char.IsHighSurrogate(text[cut - 1])) cut--``, the cut every bounded
    text in the contract uses.
    """
    if units <= 0:
        return ""
    used = 0
    end = 0
    for ch in text:
        size = 2 if ord(ch) > 0xFFFF else 1
        if used + size > units:
            break
        used += size
        end += 1
    prefix = text[:end]
    if used == units and prefix and ord(prefix[-1]) in HIGH_SURROGATES:
        prefix = prefix[:-1]
    return prefix


def utf16_sort_key(text: str) -> bytes:
    """A key that orders strings as .NET's ordinal comparison does: by UTF-16 code unit."""
    return text.encode("utf-16-be", "surrogatepass")


def utf8_bytes(text: str) -> bytes:
    """
    ``text`` as .NET's ``Encoding.UTF8.GetBytes`` encodes it: a surrogate pair held as two code points is one
    character, and a lone surrogate is U+FFFD.
    """
    return text.encode("utf-16-le", "surrogatepass").decode("utf-16-le", "replace").encode("utf-8")


SPACE_CLASS = "[\t\n\v\f\r\x85\x20\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]"
"""A regular-expression class for .NET's ``\\s``: the space separators plus TAB to CR and NEL (Python's ``\\s``
also matches U+001C to U+001F)."""


def category(ch: str) -> str:
    """The Unicode general category of one code point; a lone surrogate is ``Cs``."""
    return unicodedata.category(ch)


_DOTNET_WHITESPACE_CONTROLS = frozenset("\t\n\v\f\r\x85")


def is_whitespace(ch: str) -> bool:
    """.NET's ``char.IsWhiteSpace`` / ``Rune.IsWhiteSpace``: the space separators plus TAB to CR and NEL."""
    return ch in _DOTNET_WHITESPACE_CONTROLS or unicodedata.category(ch) in ("Zs", "Zl", "Zp")


def is_null_or_whitespace(text: str | None) -> bool:
    """.NET's ``string.IsNullOrWhiteSpace``."""
    return text is None or all(is_whitespace(ch) for ch in text)


def trim_start(text: str) -> str:
    """.NET's ``string.TrimStart()``."""
    index = 0
    while index < len(text) and is_whitespace(text[index]):
        index += 1
    return text[index:]


_NOT_UPPERCASED = frozenset("\u0131\u017f")


def ordinal_upper(ch: str) -> str:
    """
    One character's simple upper-case mapping, as .NET's ordinal-ignore-case comparison uses it: a mapping to more
    than one character is none, and the dotless i (U+0131) and the long s (U+017F) keep their case, so neither equals
    ``I`` or ``S``. The dotted capital I (U+0130) is its own upper case, so it equals neither ``i`` nor ``I``.
    """
    if ch in _NOT_UPPERCASED:
        return ch
    upper = ch.upper()
    return upper if len(upper) == 1 else ch


def starts_with_ignore_case(text: str, prefix: str, start: int = 0) -> bool:
    """.NET's ``StartsWith(prefix, StringComparison.OrdinalIgnoreCase)`` from ``start``."""
    segment = text[start : start + len(prefix)]
    if len(segment) != len(prefix):
        return False
    return all(ordinal_upper(a) == ordinal_upper(b) for a, b in zip(segment, prefix, strict=True))


def contains_ignore_case(text: str, value: str) -> bool:
    """.NET's ``Contains(value, StringComparison.OrdinalIgnoreCase)``."""
    upper_text = "".join(ordinal_upper(ch) for ch in text)
    upper_value = "".join(ordinal_upper(ch) for ch in value)
    return upper_value in upper_text


def format_fixed(value: float, decimals: int) -> str:
    """
    .NET's custom format ``0.00`` (``decimals`` zeros): the double is first taken to 15 significant digits (.NET's
    precision for a custom format), then rounded half away from zero (``0.8374999999999999`` is ``0.838``).
    """
    quantum = Decimal(1).scaleb(-decimals)
    return f"{Decimal(format(value, '.15g')).quantize(quantum, rounding=ROUND_HALF_UP):f}"


def format_round_trip(value: float) -> str:
    """
    .NET's ``double.ToString("R", CultureInfo.InvariantCulture)``: the shortest text that round-trips, with no
    trailing ``.0``, and scientific notation (``1E-05``, ``1E+15``) below 1e-4 and from 1e15.
    """
    if math.isnan(value):
        return "NaN"
    if math.isinf(value):
        return "Infinity" if value > 0 else "-Infinity"
    if value == 0:
        return "-0" if math.copysign(1.0, value) < 0 else "0"
    sign, digits, exponent = Decimal(repr(value)).as_tuple()
    assert isinstance(exponent, int)
    text = "".join(str(d) for d in digits).rstrip("0") or "0"
    # The value is 0.text * 10^point, i.e. text[0].text[1:] * 10^(point - 1).
    point = len(digits) + exponent
    scientific = point - 1
    prefix = "-" if sign else ""
    if scientific < -4 or scientific >= 15:
        mantissa = text[0] + ("." + text[1:] if len(text) > 1 else "")
        return f"{prefix}{mantissa}E{'-' if scientific < 0 else '+'}{abs(scientific):02d}"
    if point <= 0:
        return f"{prefix}0.{'0' * -point}{text}"
    if point >= len(text):
        return f"{prefix}{text}{'0' * (point - len(text))}"
    return f"{prefix}{text[:point]}.{text[point:]}"


_WORD_CATEGORIES = frozenset(("Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Nd", "Pc"))
_word_class: str | None = None


def word_class() -> str:
    """
    A regular-expression class for the characters .NET's ``\\b`` counts as word characters: letters, non-spacing
    marks, decimal digits and connector punctuation (.NET's ``\\w``), plus the zero-width joiner and non-joiner.
    Built once, on first use.
    """
    global _word_class
    if _word_class is None:
        ranges: list[str] = []
        start = -1
        for code in range(0x110000 + 1):
            inside = code <= 0x10FFFF and (
                code in (0x200C, 0x200D) or unicodedata.category(chr(code)) in _WORD_CATEGORIES
            )
            if inside and start < 0:
                start = code
            elif not inside and start >= 0:
                end = code - 1
                ranges.append(_escape_code(start) if start == end else f"{_escape_code(start)}-{_escape_code(end)}")
                start = -1
        _word_class = "[" + "".join(ranges) + "]"
    return _word_class


def _escape_code(code: int) -> str:
    return f"\\U{code:08x}"


def is_blank_ignorable(code: int) -> bool:
    """Characters that render blank but are not whitespace (variation selectors, Hangul fillers, braille blank)."""
    return (
        0xFE00 <= code <= 0xFE0F
        or 0xE0100 <= code <= 0xE01EF
        or code in (0x034F, 0x115F, 0x1160, 0x3164, 0xFFA0, 0x2800)
    )


MAX_CONSECUTIVE_COMBINING_MARKS = 4
QUOTE_LOOK_ALIKES = frozenset(
    (
        0x22,
        0x201C,
        0x201D,
        0x201E,
        0x201F,
        0x2033,
        0x2036,
        0x02BA,
        0x02DD,
        0x02EE,
        0x3003,
        0x301D,
        0x301E,
        0x301F,
        0xFF02,
    )
)


def visible(value: str, *, remove: bool, quote_look_alikes: bool = False) -> str:
    """
    Invisible characters made harmless, as the .NET writer's ``Visible`` and screening's ``Neutralize`` do. Line and
    paragraph separators become a space. Control, format, private-use, unassigned and blank-but-not-space characters
    (and a lone surrogate) become a space, or with ``remove`` are removed unless they are whitespace. A run of combining
    marks keeps its first four. With ``quote_look_alikes``, a double quote or a look-alike becomes ``'``.
    """
    out: list[str] = []
    marks = 0
    for ch in value:
        code = ord(ch)
        is_mark = False
        removed = False
        keep: str | None = ch
        if 0xD800 <= code <= 0xDFFF:
            if remove:
                keep, removed = None, True
            else:
                keep = " "
        else:
            cat = unicodedata.category(ch)
            if cat in ("Zl", "Zp"):
                keep = " "
            elif cat in ("Cc", "Cf", "Co", "Cn", "Cs") or is_blank_ignorable(code):
                if remove and not is_whitespace(ch):
                    keep, removed = None, True
                else:
                    keep = " "
            elif cat in ("Mn", "Mc", "Me"):
                is_mark = marks < MAX_CONSECUTIVE_COMBINING_MARKS
                if not is_mark:
                    keep, removed = None, True
            elif quote_look_alikes and code in QUOTE_LOOK_ALIKES:
                keep = "'"
        if not removed:
            marks = marks + 1 if is_mark else 0
        if keep is not None:
            out.append(keep)
    return "".join(out)


def ignore_case(ch: str) -> str:
    """
    One character as a case-insensitive regular-expression piece, the way .NET's ``IgnoreCase | CultureInvariant``
    matches it: an ASCII letter matches its two cases (and ``k`` the Kelvin sign), never the dotless i, the dotted
    capital I or the long s; anything else matches itself.
    """
    if "a" <= ch.lower() <= "z" and ch.isascii():
        cases = ch.lower() + ch.upper() + ("\u212a" if ch.lower() == "k" else "")
        return f"[{cases}]"
    return re.escape(ch)
