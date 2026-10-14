"""
Sanitization by field name, as capture applies it (contract step 1).

A field whose name is a secret name is redacted to ``""`` whatever its value, before anything inside it is read. An
allowlisted field (matched exactly) is kept, and an object or array value is walked one level deeper under the same
rules. Every other field is omitted. A payload beyond the limits is refused whole.
"""

from __future__ import annotations

import math
from collections.abc import Iterable, Mapping
from dataclasses import dataclass
from typing import Any

from ._text import ordinal_upper, utf16_len
from .limits import DEFAULT_SANITIZATION_LIMITS, SanitizationLimits

DEFAULT_SECRET_FIELD_NAMES: tuple[str, ...] = (
    "password",
    "secret",
    "token",
    "apiKey",
    "api_key",
    "authorization",
    "connectionString",
)
"""The default secret names, matched ignoring case."""

VALUE_FIELD = "value"
"""The single field a tool result or error is sanitized as."""

_UNSAFE_NAME_CHARACTERS = frozenset(".[]")


class SanitizationRejected(Exception):
    """A payload beyond the limits: refused whole, never partly kept. The message names the rule, never the content."""


def _fold(name: str) -> str:
    return "".join(ordinal_upper(ch) for ch in name)


@dataclass(frozen=True)
class SanitizationPolicy:
    """Which field names are kept, which are secrets, and the limits a payload must stay within."""

    allowed_field_names: frozenset[str]
    secret_field_names: frozenset[str] = frozenset(DEFAULT_SECRET_FIELD_NAMES)
    limits: SanitizationLimits = DEFAULT_SANITIZATION_LIMITS

    def is_secret(self, name: str) -> bool:
        folded = _fold(name)
        return any(_fold(secret) == folded for secret in self.secret_field_names)

    def sanitize(self, fields: Mapping[str, Any]) -> dict[str, Any]:
        """The sanitized fields. Raises :class:`SanitizationRejected` for a payload beyond the limits."""
        self._ensure_depth(1)
        return self._fields(fields, 1)

    def _fields(self, fields: Mapping[str, Any], depth: int) -> dict[str, Any]:
        self._ensure_count(len(fields))
        sanitized: dict[str, Any] = {}
        for key, value in fields.items():
            if not isinstance(key, str):
                continue
            if utf16_len(key) > self.limits.max_field_name_length:
                raise SanitizationRejected("A field name is longer than the configured limit.")
            if any(ch in _UNSAFE_NAME_CHARACTERS for ch in key):
                raise SanitizationRejected("A field name contains '.', '[' or ']'.")
            if isinstance(value, str):
                self._ensure_length(value)
            if self.is_secret(key):
                # Redacted whole, whatever its shape, before anything inside it is read.
                sanitized[key] = ""
                continue
            if key not in self.allowed_field_names:
                continue
            keep, kept = self._value(value, depth)
            if keep:
                sanitized[key] = kept
        return sanitized

    def _value(self, value: Any, depth: int) -> tuple[bool, Any]:
        if isinstance(value, float) and not math.isfinite(value):
            # No JSON number holds it, so .NET could never have captured it: refused, never stored.
            raise SanitizationRejected("A number is not finite (NaN or an infinity).")
        if value is None or isinstance(value, (bool, int, float)):
            return True, value
        if isinstance(value, str):
            self._ensure_length(value)
            return True, value
        if isinstance(value, Mapping):
            self._ensure_depth(depth + 1)
            return True, self._fields(value, depth + 1)
        if isinstance(value, (list, tuple)):
            self._ensure_depth(depth + 1)
            return True, self._list(value, depth + 1)
        # An unrecognised shape is never passed through unexamined.
        return False, None

    def _list(self, items: Iterable[Any], depth: int) -> list[Any]:
        sanitized: list[Any] = []
        for index, item in enumerate(items):
            self._ensure_count(index + 1)
            keep, kept = self._value(item, depth)
            if keep:
                sanitized.append(kept)
        return sanitized

    def _ensure_depth(self, depth: int) -> None:
        if depth > self.limits.max_depth:
            raise SanitizationRejected("The payload is nested deeper than the configured limit.")

    def _ensure_count(self, count: int) -> None:
        if count > self.limits.max_field_count:
            raise SanitizationRejected("An object or array holds more entries than the configured limit.")

    def _ensure_length(self, value: str) -> None:
        if utf16_len(value) > self.limits.max_value_length:
            raise SanitizationRejected("A value is longer than the configured limit.")


@dataclass(frozen=True)
class Sanitization:
    """The two policies capture applies: one to tool-call arguments, one to results and errors."""

    tool_arguments: SanitizationPolicy
    tool_result: SanitizationPolicy

    @staticmethod
    def default() -> Sanitization:
        """No argument value kept (nothing allowlisted); results and errors kept; secret names redacted."""
        return Sanitization.allowing(None)

    @staticmethod
    def allowing(argument_names: Iterable[str] | None) -> Sanitization:
        """The default, with exactly ``argument_names`` allowlisted for arguments (``None`` allowlists nothing)."""
        return Sanitization(
            tool_arguments=SanitizationPolicy(frozenset(argument_names or ())),
            tool_result=SanitizationPolicy(frozenset((VALUE_FIELD,))),
        )
