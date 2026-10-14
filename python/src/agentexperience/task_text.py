"""
The task text: how a request's messages become the text retrieval searches with.

The latest external user message with text after the last replayed one, cleaned of control and format characters,
joined after the previous user message with `` — `` when shorter than the follow-up threshold, and cut to the maximum
length without splitting a surrogate pair. Lengths are UTF-16 code units.
"""

from __future__ import annotations

from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field

from ._text import category, is_null_or_whitespace, is_whitespace, utf16_len, utf16_prefix
from .block import BLOCK_BEGIN

DEFAULT_MAX_LENGTH = 512
FOLLOW_UP_THRESHOLD = 64
FOLLOW_UP_SEPARATOR = " — "
MAX_TASK_TEXT_LENGTH = 4096
"""The longest task text the search accepts; longer text is refused, not cut."""


EXTERNAL = "External"
CHAT_HISTORY = "ChatHistory"

_ZWNJ = 0x200C
_ZWJ = 0x200D
_WAVING_BLACK_FLAG = 0x1F3F4
_TAG_FIRST = 0xE0020
_TAG_LAST = 0xE007E
_CANCEL_TAG = 0xE007F
_MAX_FLAG_TAGS = 8

_JOINABLE = frozenset(("Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "So", "Sk", "Sm", "Sc"))


@dataclass(frozen=True)
class MessagePart:
    """One part of a message: ``text`` with its text, or any other type (an image, a file), which carries none."""

    type: str
    text: str | None = None


@dataclass(frozen=True)
class Message:
    """
    One message of an invocation. ``source`` is where the framework says it came from: ``None`` (which counts as
    ``External``), ``External`` (new input), ``ChatHistory`` (replayed history), ``AIContextProvider``, or any other.
    ``carries_block`` marks a message stamped as carrying an injected block.
    """

    role: str
    contents: Sequence[MessagePart] = field(default_factory=tuple)
    source: str | None = None
    carries_block: bool = False


def derive_task_text(
    messages: Iterable[Message],
    max_length: int = DEFAULT_MAX_LENGTH,
    follow_up_threshold: int = FOLLOW_UP_THRESHOLD,
) -> str | None:
    """The task text, or ``None`` when no message qualifies."""
    if max_length < 1 or max_length > MAX_TASK_TEXT_LENGTH:
        raise ValueError(f"The maximum length must be between 1 and {MAX_TASK_TEXT_LENGTH}.")
    if follow_up_threshold < 0:
        raise ValueError("The follow-up threshold must not be negative.")

    latest: str | None = None
    before_latest: str | None = None
    last_seen: str | None = None
    for message in messages:
        source = message.source or EXTERNAL
        external = source == EXTERNAL
        history = source == CHAT_HISTORY
        if history:
            # The latest request is never older than the last replayed message.
            latest = None
            before_latest = None
        if message.role.lower() != "user" or not (external or history):
            continue
        text = _usable_text(message)
        if text is None:
            continue
        if external:
            latest = text
            before_latest = last_seen
        last_seen = text

    if latest is None:
        return None
    length = utf16_len(latest)
    if length >= max_length:
        return _clip(latest, max_length)
    if length >= follow_up_threshold or before_latest is None:
        return latest
    room = max_length - length - utf16_len(FOLLOW_UP_SEPARATOR)
    previous = _clip(before_latest, room) if room > 0 else None
    return latest if previous is None else previous + FOLLOW_UP_SEPARATOR + latest


def _usable_text(message: Message) -> str | None:
    if message.carries_block:
        return None
    raw = " ".join(part.text for part in message.contents if part.type == "text" and part.text)
    if not raw or BLOCK_BEGIN in raw:
        return None
    cleaned = _clean(raw)
    if not cleaned:
        return None
    # A marker split by an invisible character reads as the marker once that character is gone.
    probe = "".join(ch for ch in cleaned if category(ch) != "Cf")
    return None if BLOCK_BEGIN in probe else cleaned


def _joinable(ch: str) -> bool:
    return category(ch) in _JOINABLE


def _clean(text: str) -> str:
    out: list[str] = []
    pending_space = False
    pending_joiner: str | None = None
    last_kept: str | None = None
    index = 0
    while index < len(text):
        ch = text[index]
        index += 1
        code = ord(ch)
        if 0xD800 <= code <= 0xDFFF:
            continue  # An unpaired surrogate: not text.
        if is_whitespace(ch):
            pending_space = bool(out)
            pending_joiner = None
            last_kept = None
            continue
        if category(ch) in ("Cc", "Cf"):
            # A joiner is decided on the next character that is kept.
            if code in (_ZWJ, _ZWNJ) and last_kept is not None and _joinable(last_kept) and pending_joiner is None:
                pending_joiner = ch
            continue
        if pending_space:
            out.append(" ")
            pending_space = False
        if pending_joiner is not None and _joinable(ch):
            out.append(pending_joiner)
        pending_joiner = None
        out.append(ch)
        last_kept = ch
        if code == _WAVING_BLACK_FLAG:
            consumed = _flag_tags(text, index)
            out.append(text[index : index + consumed])
            index += consumed
    return "".join(out)


def _flag_tags(text: str, start: int) -> int:
    """How many tag characters after a waving black flag form a valid subdivision flag (0 when they do not)."""
    index = start
    tags = 0
    while index < len(text):
        code = ord(text[index])
        if 0xD800 <= code <= 0xDFFF:
            return 0
        index += 1
        if code == _CANCEL_TAG:
            return 0 if tags == 0 else index - start
        tags += 1
        if code < _TAG_FIRST or code > _TAG_LAST or tags > _MAX_FLAG_TAGS:
            return 0
    return 0


def _clip(text: str, max_length: int) -> str | None:
    if utf16_len(text) > max_length:
        text = _trim_cut_end(utf16_prefix(text, max_length))
    return None if is_null_or_whitespace(text) else text


def _trim_cut_end(text: str) -> str:
    end = len(text)
    while end > 0:
        code = ord(text[end - 1])
        if code in (0x20, _ZWNJ, _ZWJ) or _TAG_FIRST <= code <= _TAG_LAST:
            end -= 1
            continue
        break
    return text[:end]
