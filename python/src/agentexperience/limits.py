"""The default limits of contract 1.x: what sanitization refuses, and how much capture keeps."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class SanitizationLimits:
    """A payload beyond any of these is refused whole. Lengths are UTF-16 code units."""

    max_depth: int = 4
    max_field_count: int = 50
    max_value_length: int = 1_000_000
    max_field_name_length: int = 100


@dataclass(frozen=True)
class CaptureLimits:
    """How much of a run capture keeps. Lengths are UTF-16 code units."""

    max_attempts_per_run: int = 10
    max_tool_calls_per_attempt: int = 50
    max_result_length: int = 4_000
    max_error_length: int = 4_000

    def __post_init__(self) -> None:
        for name in ("max_attempts_per_run", "max_tool_calls_per_attempt", "max_result_length", "max_error_length"):
            if getattr(self, name) <= 0:
                raise ValueError(f"{name} must be strictly positive.")


DEFAULT_SANITIZATION_LIMITS = SanitizationLimits()
DEFAULT_CAPTURE_LIMITS = CaptureLimits()

TRUNCATION_PLACEHOLDER = "[truncated: value exceeded the configured limit]"
"""What a result or error over its capture limit is replaced by."""

MAX_EXPOSURES_PER_RUN = 256
"""The most stored records one run may be recorded as shown."""
