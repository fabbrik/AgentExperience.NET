"""
Ready-made tool-result classifiers, for tools that report failure in their return value (an exit code, an HTTP
status). Each returns an error class that is a single error-class token (``exit 3``, ``HTTP 503``, or
:data:`TOOL_REPORTED_ERROR_CLASS`), or ``None`` when the call did not fail. The semantics are .NET's
``ExperienceToolResultClassifiers``.

Each reads only the tool's own raw result:

- A JSON object (a mapping, a string that parses as one, or any other object serialized to JSON) is read by its
  top-level properties only, whatever its size, so a code quoted inside its output does not count. A JSON array counts
  as no code.
- Any other text (a string, a text content item or a list of them) is matched against the patterns given on each
  classifier, and the **last** match counts, so a final exit code wins over an earlier step's. Text up to 1 MiB is
  read whole; of longer text, the first and last 64 KiB.

Matching ignores case. With no tool names a classifier applies to every tool's result; give the names of the tools
that report this way to leave every other tool's result alone (exact match on the tool name).
"""

from __future__ import annotations

import dataclasses
import json
import math
import re
import uuid
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from typing import Any

from ._text import is_null_or_whitespace

TOOL_REPORTED_ERROR_CLASS = "ToolReportedError"
"""The class recorded for a returned call whose classifier named a class that is not one error-class token."""

_MAX_WHOLE_TEXT_LENGTH = 1024 * 1024
_EDGE_LENGTH = 64 * 1024
_MAX_EXIT_CODE_DIGITS = 6

_FLAGS = re.IGNORECASE
_NUMBER = r"-?[0-9]+(?:\.[0-9]+)?"
_EXIT_TEXT = re.compile(
    rf"\b(?:exit(?:ed)?(?:\s+with)?(?:\s+(?:code|status))?|return_?code)\s*[=:]?\s*(?P<a>{_NUMBER})\b"
    rf'|"(?:exit_?code|return_?code)"\s*:\s*"?(?P<b>{_NUMBER})\b',
    _FLAGS,
)
_HTTP_LEADING_TEXT = re.compile(
    r"^\s*(?:HTTP(?:/[0-9](?:\.[0-9])?)?\s+|status(?:\s+code)?\s*[=:]?\s*)(?P<a>[0-9]+)\b", _FLAGS
)
_HTTP_PAIR_TEXT = re.compile(rf'"status(?:_?code)?"\s*:\s*"?(?P<a>{_NUMBER})\b', _FLAGS)
_NUMERIC = re.compile(r"^-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?\Z")


@dataclass(frozen=True)
class ToolResultContext:
    """
    What a tool-result classifier sees for one tool call that returned: the call, the tool, the arguments capture
    recorded for it, and the raw value it returned. Nothing else, in particular no model text other than the tool's
    own result.

    The arguments are the raw ones, *before* sanitization, so secrets the model passed reach the classifier: never log
    or keep them. ``result`` is the live object the model receives: never change it.
    """

    tool_call_id: uuid.UUID
    tool_name: str
    arguments: Mapping[str, Any]
    result: Any


ToolResultClassifier = Callable[[ToolResultContext], "str | None"]
"""A classifier: ``None`` when the call is recorded as returned, else the error class it is recorded as failed with."""


class _RawNumber(str):
    """A JSON number, kept as the text it was written as."""


@dataclass(frozen=True)
class _Spec:
    properties: tuple[str, ...]
    from_text: Callable[[str], str | None]
    classify: Callable[[str], str | None]


def _last(pattern: re.Pattern[str], text: str) -> str | None:
    """The code of the last match in the text read: all of it up to 1 MiB, else its first and last 64 KiB."""
    segments = [text] if len(text) <= _MAX_WHOLE_TEXT_LENGTH else [text[:_EDGE_LENGTH], text[-_EDGE_LENGTH:]]
    code: str | None = None
    for index, segment in enumerate(segments):
        # The head of a long text ends at a cut, not at the text's end: a match running into the cut may be a code
        # cut short (exit=12 read as exit=1), so it does not count.
        cut = len(segment) if len(segments) > 1 and index == 0 else -1
        for match in pattern.finditer(segment):
            if match.end() == cut:
                continue
            groups = match.groupdict()
            code = groups.get("a") or groups.get("b")
    return code


def _exit_class(code: str) -> str | None:
    negative = code.startswith("-")
    unsigned = code[1:] if negative else code
    if any(ch < "0" or ch > "9" for ch in unsigned):
        # Not an integer (1.0, 1e0): zero is success, anything else a failure no token can name.
        try:
            return None if float(code) == 0 else TOOL_REPORTED_ERROR_CLASS
        except ValueError:
            return TOOL_REPORTED_ERROR_CLASS
    digits = unsigned.lstrip("0")
    if not digits:
        return None
    if len(digits) > _MAX_EXIT_CODE_DIGITS:
        return TOOL_REPORTED_ERROR_CLASS
    return "exit " + ("-" if negative else "") + digits


def _http_class(code: str) -> str | None:
    if not code or any(ch < "0" or ch > "9" for ch in code):
        return None
    status = int(code)
    return f"HTTP {status}" if 400 <= status <= 599 else None


def _http_from_text(text: str) -> str | None:
    leading = _HTTP_LEADING_TEXT.match(text)
    return leading.group("a") if leading is not None else _last(_HTTP_PAIR_TEXT, text)


_EXIT = _Spec(("exitCode", "exit_code", "returnCode", "return_code"), lambda text: _last(_EXIT_TEXT, text), _exit_class)
_HTTP = _Spec(("status", "statusCode", "status_code"), _http_from_text, _http_class)


def _number_text(value: Any) -> str | None:
    """A JSON number's text: as written when read from JSON, else as a JSON serializer writes the number."""
    if isinstance(value, _RawNumber):
        return str(value)
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            return None
        return str(int(value)) if value.is_integer() else repr(value)
    return None


def _from_pairs(pairs: Sequence[tuple[Any, Any]], spec: _Spec) -> str | None:
    folded = {name.casefold() for name in spec.properties}
    for name, value in pairs:
        if not isinstance(name, str) or name.casefold() not in folded:
            continue
        text = value.strip() if isinstance(value, str) and not isinstance(value, _RawNumber) else _number_text(value)
        if text is not None and _NUMERIC.match(text):
            return text
    return None


class _Pairs(list[tuple[Any, Any]]):
    """A JSON object's members in order, duplicates kept."""


def _refuse_constant(name: str) -> Any:
    raise ValueError(f"{name} is not JSON.")


def _from_string(text: str | None, spec: _Spec) -> str | None:
    if text is None or is_null_or_whitespace(text):
        return None
    trimmed = text.lstrip()
    if trimmed.startswith(("{", "[")):
        try:
            parsed = json.loads(
                text,
                object_pairs_hook=_Pairs,
                parse_int=_RawNumber,
                parse_float=_RawNumber,
                parse_constant=_refuse_constant,
            )
        except ValueError:
            pass  # Not JSON after all: read it as text.
        else:
            return _from_pairs(parsed, spec) if isinstance(parsed, _Pairs) else None
    return spec.from_text(text)


def _content_text(item: Any) -> str | None:
    """The text of a framework text content item (anything with ``type == "text"`` and a ``text``), else ``None``."""
    if getattr(item, "type", None) == "text":
        text = getattr(item, "text", None)
        return text if isinstance(text, str) else None
    return None


def _is_content(item: Any) -> bool:
    return hasattr(item, "type") and not isinstance(item, (str, bytes, Mapping))


def _to_json_value(result: Any) -> Any:
    """Any other object as JSON values: a dataclass or a pydantic-style model by its fields, else as ``json``
    writes it."""
    if dataclasses.is_dataclass(result) and not isinstance(result, type):
        result = dataclasses.asdict(result)
    elif callable(getattr(result, "model_dump", None)):
        result = result.model_dump(mode="json")
    return json.loads(json.dumps(result, allow_nan=False), object_pairs_hook=_Pairs)


def _read(result: Any, spec: _Spec) -> str | None:
    """The numeric code a result names, as written: from a JSON object's properties, else from its text."""
    if result is None:
        return None
    if isinstance(result, str):
        return _from_string(result, spec)
    if isinstance(result, Mapping):
        return _from_pairs(list(result.items()), spec)
    if isinstance(result, (list, tuple)) and result and all(_is_content(item) for item in result):
        texts = [text for item in result if (text := _content_text(item)) is not None]
        return _from_string("\n".join(texts), spec)
    if _is_content(result):
        return _from_string(_content_text(result), spec)
    try:
        value = _to_json_value(result)
    except (TypeError, ValueError):
        return None
    if isinstance(value, _Pairs):
        return _from_pairs(value, spec)
    if isinstance(value, str):
        return _from_string(value, spec)
    return None


def _classifier(tool_names: Sequence[str], spec: _Spec) -> ToolResultClassifier:
    for name in tool_names:
        if not isinstance(name, str) or is_null_or_whitespace(name):
            raise ValueError("A tool name must not be blank.")
    tools = frozenset(tool_names) if tool_names else None

    def classify(context: ToolResultContext) -> str | None:
        if tools is not None and context.tool_name not in tools:
            return None
        code = _read(context.result, spec)
        return spec.classify(code) if code is not None else None

    return classify


def exit_code(*tool_names: str) -> ToolResultClassifier:
    """
    Classifies a result by its exit code: ``exit N`` for a non-zero code N, ``None`` for zero or when the result names
    no exit code.

    In text it finds ``exit=N``, ``exit N``, ``exit code N``, ``exit status N``, ``exited with code N``,
    ``returncode=N``, ``"exitCode": N``, ``"exit_code": N``, ``"returnCode": N`` or ``"return_code": N``; in a JSON
    object, the first top-level ``exitCode``, ``exit_code``, ``returnCode`` or ``return_code`` property whose value is
    a number or a numeric string. A non-zero code that no error-class token can carry (more than six digits, or not an
    integer such as ``1.0``) is classed :data:`TOOL_REPORTED_ERROR_CLASS`.
    """
    return _classifier(tool_names, _EXIT)


def http_status(*tool_names: str) -> ToolResultClassifier:
    """
    Classifies a result by its HTTP status: ``HTTP N`` for a status N from 400 to 599, ``None`` otherwise, or when the
    result names none.

    In text it finds a leading ``HTTP N`` (or ``HTTP/1.1 N``) or ``status N``, else the last ``"status": N`` or
    ``"statusCode": N`` pair; in a JSON object, the first top-level ``status``, ``statusCode`` or ``status_code``
    property whose value is a number or a numeric string.
    """
    return _classifier(tool_names, _HTTP)
