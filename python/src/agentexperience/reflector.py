"""
The deterministic reflector, template version ``1.1.0`` (contract step 3), and the screening finalization applies to
every reflection before it is stored.
"""

from __future__ import annotations

import uuid
from collections.abc import Awaitable, Sequence
from dataclasses import dataclass, replace
from typing import Literal, Protocol, TypeVar, runtime_checkable

from ._text import (
    format_round_trip,
    is_null_or_whitespace,
    utf16_len,
    utf16_prefix,
    utf16_sort_key,
    visible,
)
from .capture import CapturedRun
from .content_guard import Links, check_combined, links_of
from .content_guard import check as guard_check
from .encoding import Instant
from .error_class import error_class
from .model import without_model
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
    # The run's model is bookkeeping for the benefit estimate (contract 1.9), never a precondition.
    for key in sorted(without_model(environment.metadata), key=utf16_sort_key):
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


# ---- The reflector seam (contract 1.14) -------------------------------------------------------------------------


class ReflectionBindingError(ValueError):
    """A reflection that does not carry its request's identity or copy its evaluation exactly (.NET's
    ``ReflectionBindingException``). ``field`` names the first field that differs; the message holds no captured
    text."""

    def __init__(self, field: str, run_id: uuid.UUID) -> None:
        super().__init__(
            f"The reflection's {field} does not match the reflection request for run {run_id}; a reflection must carry "
            "the request's identity and copy its evaluation exactly."
        )
        self.field = field


@dataclass(frozen=True)
class ReflectionRequest:
    """What a reflector is asked to reflect on (.NET's ``ReflectionRequest``): the finished, sanitized run, its own
    evaluation, and the identifier and instant the reflection must carry."""

    run: CapturedRun
    evaluation: VerificationResult
    reflection_id: uuid.UUID
    created_at: Instant

    def ensure_matches(self, reflection: Reflection) -> None:
        """Raises :class:`ReflectionBindingError` unless ``reflection`` carries this request's identity and copies its
        evaluation's verdict, score, rule version and evidence IDs (distinct, in the order produced)."""
        evaluation = self.evaluation
        evidence = _distinct([item.evidence_id for item in evaluation.outcome.evidence])
        mismatch = (
            "ReflectionId"
            if reflection.reflection_id != self.reflection_id
            else "ExperienceRunId"
            if reflection.experience_run_id != self.run.run_id
            else "CreatedAt"
            if reflection.created_at != self.created_at
            else "VerificationStatus"
            if reflection.verification_status != evaluation.outcome.status
            else "CompletionScore"
            if not _same_number(reflection.completion_score, evaluation.completion_score)
            else "VerificationRuleVersion"
            if reflection.verification_rule_version != evaluation.rule_version
            else "EvidenceIds"
            if list(reflection.evidence_ids) != evidence
            else None
        )
        if mismatch is not None:
            raise ReflectionBindingError(mismatch, self.run.run_id)


def _same_number(left: float, right: float) -> bool:
    """.NET's ``double.Equals``: NaN equals NaN."""
    return left == right or (left != left and right != right)


@runtime_checkable
class ExperienceReflector(Protocol):
    """
    The seam a reflector plugs into finalization by (.NET's ``IExperienceReflector``): ``reflect`` returns the
    reflection for the request, or an awaitable of it (then finalize with :func:`agentexperience.finalize_async`).
    Whatever it raises quarantines the record, as in .NET; the caller's cancellation propagates.

    A reflector may also have ``reflected_run_content(request)``, the captured text it showed a model (.NET's
    ``IReflectionRunContent``): screening's content guard compares a model-authored reflection's links with it. Without
    it, the guard takes the task text (or ID), every tool name, every result and error, and the check IDs, in full.
    """

    def reflect(self, request: ReflectionRequest) -> Reflection | Awaitable[Reflection]: ...


class DeterministicReflector:
    """The default reflector: :func:`reflect`, template version ``1.1.0``. It never calls a model."""

    def reflect(self, request: ReflectionRequest) -> Reflection:
        return reflect(request.run, request.evaluation, request.reflection_id, request.created_at)


def default_run_content(request: ReflectionRequest) -> list[str]:
    """The run content a reflector without ``reflected_run_content`` is taken to have been given: the task text (or
    the task ID when there is none), each tool name, each tool call's and each attempt's result and error, and the
    check IDs, in full (.NET's ``ModelAuthoredContentGuard.DefaultRunContent``)."""
    content: list[str] = []

    def add(value: str | None) -> None:
        if value:
            content.append(value)

    run = request.run
    add(run.task_description if run.task_description is not None else run.task_id)
    for attempt in run.attempts:
        for call in attempt.tool_calls:
            add(call.tool_name)
            add(call.result)
            add(call.error)
        add(attempt.result)
        add(attempt.error)
    for item in request.evaluation.outcome.evidence:
        add(item.check_id)
    return content


# ---- Screening ----------------------------------------------------------------------------------------------------

ScreeningRefusal = Literal[
    "OverLimit",
    "MissingLesson",
    "MissingProducer",
    "MissingField",
    "Unreadable",
    "SanitizerRejected",
    "SanitizerFailed",
    "SanitizerTimedOut",
    "FieldOmitted",
    "UnsafeContent",
    "UndefinedAuthorship",
]
"""Why screening refused a reflection, as .NET's ``ReflectionScreeningRefusal`` names it. A port has no reflection
sanitizer, so the sanitizer codes never occur here."""


class ScreeningRefused(Exception):
    """
    A reflection screening refused: the record is stored Quarantined, without it. ``refusal`` is the code; the message
    is .NET's content-free reason (it names the field and the rule or limit, never the text).
    """

    def __init__(self, reason: str, refusal: ScreeningRefusal = "OverLimit") -> None:
        super().__init__(reason)
        self.refusal: ScreeningRefusal = refusal


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
        raise ScreeningRefused(f"its {field} is longer than the {limit}-character limit", "OverLimit")


def is_model_authored(reflection: Reflection | None) -> bool:
    """Model-authored: any authorship but Deterministic, or the library's own model-backed reflector."""
    return reflection is not None and (
        reflection.authorship != "Deterministic"
        or reflection.producer.startswith(LIBRARY_MODEL_REFLECTOR_PRODUCER_PREFIX)
    )


_LIST_FIELDS = ("SuccessfulApproaches", "FailedApproaches", "Preconditions", "Warnings")


def screen(reflection: Reflection, run_content: Sequence[str] | None = None) -> Reflection:
    """
    The screening finalization applies before a reflection is stored, in .NET's order: the authorship must be defined;
    the producer and every free-text field neutralized and held to their limits (a list over 32 items, then a missing
    lesson, then an over-long field refuses; blank list items are dropped and blank reuse guidance is ``None``); then,
    for a model-authored reflection, the content guard (:mod:`agentexperience.content_guard`), which compares links
    with ``run_content``, what the reflector showed the model (``None``: nothing, so every link is refused). Raises
    :class:`ScreeningRefused`.
    """
    if reflection.authorship not in ("Deterministic", "Model"):
        raise ScreeningRefused("its Authorship is not a defined ReflectionAuthorship value", "UndefinedAuthorship")
    producer = _clean(reflection.producer)
    if producer is None:
        raise ScreeningRefused(
            "its Producer is missing, or empty once invisible characters are removed", "MissingProducer"
        )
    _ensure_length(producer, MAX_PRODUCER_LENGTH, "Producer")

    lists = (
        reflection.successful_approaches,
        reflection.failed_approaches,
        reflection.preconditions,
        reflection.warnings,
    )
    for items, field in zip(lists, _LIST_FIELDS, strict=True):
        if len(items) > MAX_LIST_ITEMS:
            raise ScreeningRefused(f"its {field} holds more items than the {MAX_LIST_ITEMS}-item limit", "OverLimit")

    lesson = _clean(reflection.lesson)
    if lesson is None:
        raise ScreeningRefused("its Lesson is missing, or empty once invisible characters are removed", "MissingLesson")
    _ensure_length(lesson, MAX_LESSON_LENGTH, "Lesson")
    guidance = _clean(reflection.reuse_guidance)
    if guidance is not None:
        _ensure_length(guidance, MAX_LESSON_LENGTH, "ReuseGuidance")

    screened: list[list[str]] = []
    sources: list[list[int]] = []
    for items, field in zip(lists, _LIST_FIELDS, strict=True):
        kept: list[str] = []
        indexes: list[int] = []
        for index, item in enumerate(items):
            text = _clean(item)
            if text is not None:
                _ensure_length(text, MAX_LIST_ITEM_LENGTH, f"{field}[{index}]")
                kept.append(text)
                indexes.append(index)
        screened.append(kept)
        sources.append(indexes)

    result = replace(
        reflection,
        lesson=lesson,
        successful_approaches=tuple(screened[0]),
        failed_approaches=tuple(screened[1]),
        preconditions=tuple(screened[2]),
        warnings=tuple(screened[3]),
        reuse_guidance=guidance,
        producer=producer,
    )
    if is_model_authored(result):
        _guard(result, screened, sources, links_of(run_content))
    return result


def _guard(reflection: Reflection, lists: list[list[str]], sources: list[list[int]], run: Links) -> None:
    """Refuses, as ``UnsafeContent``, the first screened field that breaks a content-guard rule, naming the field (a
    list item by the reflector's own index) and the rule, never the text."""

    def check_one(text: str | None, field: str) -> None:
        if text is not None and (rule := guard_check(text, run)) is not None:
            raise ScreeningRefused(f"its {field} contains {rule}", "UnsafeContent")

    check_one(reflection.lesson, "Lesson")
    for items, indexes, field in zip(lists, sources, _LIST_FIELDS, strict=True):
        for item, index in zip(items, indexes, strict=True):
            check_one(item, f"{field}[{index}]")
    check_one(reflection.reuse_guidance, "ReuseGuidance")
    combined = check_combined(
        [reflection.lesson, *(item for items in lists for item in items), reflection.reuse_guidance]
    )
    if combined is not None:
        raise ScreeningRefused(f"the text across its fields contains {combined}", "UnsafeContent")
