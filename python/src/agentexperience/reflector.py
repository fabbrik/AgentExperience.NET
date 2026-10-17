"""
The deterministic reflector, template version ``1.1.0`` (contract step 3), and the screening finalization applies to
every reflection before it is stored.
"""

from __future__ import annotations

import uuid
from collections.abc import Sequence
from dataclasses import replace
from typing import TypeVar

from ._text import (
    format_round_trip,
    is_null_or_whitespace,
    utf16_len,
    utf16_prefix,
    utf16_sort_key,
    visible,
)
from .capture import CapturedRun
from .encoding import Instant
from .error_class import error_class
from .observer import observe
from .records import Attempt, Reflection
from .verification import VerificationResult

TEMPLATE_VERSION = "1.1.0"
PRODUCER_PREFIX = "AgentExperience.DefaultExperienceReflector/"
PRODUCER = PRODUCER_PREFIX + TEMPLATE_VERSION
UNKNOWN_VALUE = "unknown"
VERIFICATION_SCOPE_WARNING = (
    "Verification applies only to this run and its captured environment; reuse in another context is not itself "
    "verified."
)
LIBRARY_MODEL_REFLECTOR_PRODUCER_PREFIX = "AgentExperience.ChatClientExperienceReflector/"

MAX_LESSON_LENGTH = 4_000
MAX_LIST_ITEM_LENGTH = 1_000
MAX_LIST_ITEMS = 32
MAX_PRODUCER_LENGTH = 200
MAX_QUOTED_LENGTH = 500
MAX_LESSON_FAILED_ATTEMPTS = 2
CLIP_MARKER = "\u2026"

_T = TypeVar("_T")


def _distinct(items: Sequence[_T]) -> list[_T]:
    seen: set[_T] = set()
    result: list[_T] = []
    for item in items:
        if item not in seen:
            seen.add(item)
            result.append(item)
    return result


def clip(text: str, length: int) -> str:
    """Cuts to ``length`` UTF-16 code units, ending in ``…``, never splitting a surrogate pair."""
    if utf16_len(text) <= length:
        return text
    return utf16_prefix(text, length - utf16_len(CLIP_MARKER)) + CLIP_MARKER


def _quote(text: str) -> str:
    clipped = clip(text, MAX_QUOTED_LENGTH)
    escaped = clipped.replace("\\", "\\\\").replace('"', '\\"').replace("\r", "\\r").replace("\n", "\\n")
    return f'"{escaped}"'


def _quote_or_missing(text: str | None) -> str:
    return "none captured" if text is None else _quote(text)


def _describe(attempt: Attempt) -> str:
    calls = sorted(attempt.tool_calls, key=lambda call: call.sequence_number)
    names = [
        call.tool_name if call.error is None else f"{call.tool_name} (error: {_quote(call.error)})" for call in calls
    ]
    if not names:
        return f"Attempt {attempt.sequence_number} with no tool calls"
    return f"Attempt {attempt.sequence_number} using tools [{', '.join(names)}]"


def _lesson(status: str, attempts: list[Attempt], checks: list[str]) -> str:
    verdict = {"Verified": "Verified", "Failed": "Did not verify"}.get(status, "Unverified")
    lesson = f"{verdict} after {len(attempts)} {'attempt' if len(attempts) == 1 else 'attempts'}."
    failed = [attempt for attempt in attempts if attempt.error is not None]
    if len(failed) > MAX_LESSON_FAILED_ATTEMPTS:
        last = failed[-1]
        lesson += (
            f" Failed: {len(failed)} attempts; last: attempt {last.sequence_number} \u2014 {error_class(last.error)}."
        )
    else:
        for attempt in failed:
            lesson += f" Failed: attempt {attempt.sequence_number} \u2014 {error_class(attempt.error)}."
    if status == "Verified" and attempts and attempts[-1].error is None:
        lesson += f" Worked: attempt {attempts[-1].sequence_number}."
    if checks:
        lesson += f" Checks: [{', '.join(checks)}]."
    return lesson


def _bound(items: list[str], noun: str) -> tuple[str, ...]:
    if len(items) > MAX_LIST_ITEMS:
        shown = MAX_LIST_ITEMS - 1
        items = [*items[:shown], f"{len(items) - shown} further {noun} are not listed."]
    return tuple(clip(item, MAX_LIST_ITEM_LENGTH) for item in items)


def reflect(
    run: CapturedRun,
    evaluation: VerificationResult,
    reflection_id: uuid.UUID,
    created_at: Instant,
) -> Reflection:
    """The deterministic reflection of a finished run. No tool name, argument or error text beyond its class reaches
    the lesson."""
    with observe("reflect") as op:
        if op is not None:
            op.set("agentexperience.run_id", str(run.run_id))
            op.set("agentexperience.reflection_id", str(reflection_id))
        reflection = _reflect(run, evaluation, reflection_id, created_at)
        if op is not None:
            op.outcome(evaluation.outcome.status)
        return reflection


def _reflect(
    run: CapturedRun,
    evaluation: VerificationResult,
    reflection_id: uuid.UUID,
    created_at: Instant,
) -> Reflection:
    outcome = evaluation.outcome
    status = outcome.status
    evidence_ids = _distinct([item.evidence_id for item in outcome.evidence])
    score_text = format_round_trip(evaluation.completion_score)
    passing = _distinct([item.check_id for item in outcome.evidence if item.result == "Pass"])
    failing = _distinct([item.check_id for item in outcome.evidence if item.result == "Fail"])
    attempts = sorted(run.attempts, key=lambda attempt: attempt.sequence_number)

    warnings: list[str] = []
    successful: list[str] = []
    failed: list[str] = []

    inconclusive = _distinct(
        [
            check.check_id
            for check in evaluation.required_checks
            if check.check_id not in passing and check.check_id not in failing
        ]
    )
    lesson = _lesson(
        status, attempts, passing if status == "Verified" else failing if status == "Failed" else inconclusive
    )
    if status == "Verified":
        warnings.append(VERIFICATION_SCOPE_WARNING)
        if not evidence_ids:
            warnings.append("Verification status is Verified, but the evaluation supplied no evidence.")
    elif status == "Failed":
        warnings.append("Not a validated procedure: task verification status is Failed.")
        warnings.append(f"Verification failed: required checks [{', '.join(failing)}] failed.")
    else:
        warnings.append("Not a validated procedure: task verification status is Unknown.")
        warnings.append("Unverified: not every required check reached a conclusive result in the evaluation.")

    if status != "Verified" and outcome.reason is not None and not is_null_or_whitespace(outcome.reason):
        warnings.append(f"Evaluation reason: {_quote(outcome.reason)}.")
    if status != "Verified" and evaluation.completion_score > 0:
        warnings.append(
            f"Completion score {score_text} (rule {evaluation.rule_version}) is a partial score and is not "
            "verification."
        )
    if run.execution_status != "Completed":
        warnings.append(f"Run execution status is {run.execution_status}, not Completed.")
    if not attempts:
        warnings.append("No attempts were captured.")

    for index, attempt in enumerate(attempts):
        is_final = index == len(attempts) - 1
        description = _describe(attempt)
        if attempt.error is not None:
            failed.append(f"{description} ended with error: {_quote(attempt.error)}.")
            if is_final and status == "Verified":
                warnings.append(
                    f"Attempt {attempt.sequence_number} (final) ended with an error despite a Verified status; no "
                    "captured attempt is recorded as a successful approach."
                )
            continue
        if not is_final:
            warnings.append(
                f"Attempt {attempt.sequence_number} completed without an error but was followed by a later attempt; "
                "its contribution to the outcome is unknown."
            )
            continue
        if status == "Verified":
            successful.append(
                f"{description} completed without an error (result: {_quote_or_missing(attempt.result)}) as the final "
                "attempt of a verified run."
            )
        elif status == "Failed":
            failed.append(
                f"{description} completed without an error (result: {_quote_or_missing(attempt.result)}) as the final "
                "attempt, but verification failed."
            )
        else:
            warnings.append(
                f"Attempt {attempt.sequence_number} (final) completed without an error, but verification is Unknown; "
                "it is not recorded as a successful approach."
            )

    preconditions: list[str] = []
    unknown_precondition = False

    def add_precondition(label: str, value: str | None) -> None:
        nonlocal unknown_precondition
        if value is None or is_null_or_whitespace(value):
            preconditions.append(f"{label}: {UNKNOWN_VALUE}")
            warnings.append(f"Precondition '{label}' was not captured and is {UNKNOWN_VALUE}.")
            unknown_precondition = True
        else:
            preconditions.append(f"{label}: {value}")

    environment = run.environment
    add_precondition("Runtime version", environment.runtime_version)
    add_precondition("Operating system", environment.operating_system)
    add_precondition("Application version", environment.application_version)
    for key in sorted(environment.metadata, key=utf16_sort_key):
        add_precondition(f"Environment metadata [{key}]", environment.metadata[key])

    if status == "Verified":
        guidance = (
            f"Reuse only where the listed preconditions match, and re-run required checks [{', '.join(passing)}] to "
            "confirm the outcome in the new context."
        )
        if unknown_precondition:
            guidance += " Confirm the unknown preconditions before reuse."
    else:
        guidance = (
            f"Do not reuse as a validated procedure: task verification status is {status}. Treat the listed approaches "
            "as observed history only, and verify any approach independently before relying on it."
        )

    return Reflection(
        reflection_id=reflection_id,
        experience_run_id=run.run_id,
        lesson=clip(lesson, MAX_LESSON_LENGTH),
        successful_approaches=_bound(successful, "successful approaches"),
        failed_approaches=_bound(failed, "failed approaches"),
        preconditions=_bound(preconditions, "preconditions"),
        warnings=_bound(warnings, "warnings"),
        reuse_guidance=clip(guidance, MAX_LESSON_LENGTH),
        evidence_ids=tuple(evidence_ids),
        verification_status=status,
        completion_score=evaluation.completion_score,
        verification_rule_version=evaluation.rule_version,
        producer=PRODUCER,
        created_at=created_at,
    )


# ---- Screening ----------------------------------------------------------------------------------------------------


class ScreeningRefused(Exception):
    """A reflection screening refused: the record is stored Quarantined, without it. The message carries no content."""


def neutralize(value: str) -> str:
    """
    Screening's hygiene: line and paragraph separators become a space; control, format, private-use, unassigned and
    blank-but-not-space characters are removed (a whitespace one becomes a space); a run of combining marks keeps
    its first four.
    """
    return visible(value, remove=True)


def _clean(text: str | None) -> str | None:
    if text is None:
        return None
    neutralized = neutralize(text)
    return None if is_null_or_whitespace(neutralized) else neutralized


def _ensure_length(text: str, limit: int, field: str) -> None:
    if utf16_len(text) > limit:
        raise ScreeningRefused(f"its {field} is longer than the {limit}-character limit")


def is_model_authored(reflection: Reflection | None) -> bool:
    """Model-authored: any authorship but Deterministic, or the library's own model-backed reflector."""
    return reflection is not None and (
        reflection.authorship != "Deterministic"
        or reflection.producer.startswith(LIBRARY_MODEL_REFLECTOR_PRODUCER_PREFIX)
    )


def screen(reflection: Reflection) -> Reflection:
    """
    The screening finalization applies before a reflection is stored, for a deterministic reflection: every text
    neutralized, blank list items dropped, the limits enforced. Raises :class:`ScreeningRefused`.
    """
    if is_model_authored(reflection):
        # The model-authored content guard is outside contract 1.x: a port writes deterministic reflections only.
        raise ScreeningRefused("a model-authored reflection is outside contract 1.x")
    producer = _clean(reflection.producer)
    if producer is None:
        raise ScreeningRefused("its Producer is missing, or empty once invisible characters are removed")
    _ensure_length(producer, MAX_PRODUCER_LENGTH, "Producer")

    lesson = _clean(reflection.lesson)
    if lesson is None:
        raise ScreeningRefused("its Lesson is missing, or empty once invisible characters are removed")
    _ensure_length(lesson, MAX_LESSON_LENGTH, "Lesson")
    guidance = _clean(reflection.reuse_guidance)
    if guidance is not None:
        _ensure_length(guidance, MAX_LESSON_LENGTH, "ReuseGuidance")

    def screened_list(items: tuple[str, ...], field: str) -> tuple[str, ...]:
        if len(items) > MAX_LIST_ITEMS:
            raise ScreeningRefused(f"its {field} holds more items than the {MAX_LIST_ITEMS}-item limit")
        kept: list[str] = []
        for index, item in enumerate(items):
            text = _clean(item)
            if text is not None:
                _ensure_length(text, MAX_LIST_ITEM_LENGTH, f"{field}[{index}]")
                kept.append(text)
        return tuple(kept)

    return replace(
        reflection,
        lesson=lesson,
        successful_approaches=screened_list(reflection.successful_approaches, "SuccessfulApproaches"),
        failed_approaches=screened_list(reflection.failed_approaches, "FailedApproaches"),
        preconditions=screened_list(reflection.preconditions, "Preconditions"),
        warnings=screened_list(reflection.warnings, "Warnings"),
        reuse_guidance=guidance,
        producer=producer,
    )
