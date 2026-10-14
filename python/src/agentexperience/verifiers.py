"""
Verification for the one-call setup: the context a ``verify`` callback is given, the verdict it returns, and the
ready-made verifiers (the semantics of .NET's ``ExperienceVerifiers``).

A verifier decides only from facts the host's code produced (the captured outcome of a tool call, or the host's own
function), never from message text or model output. Returning ``None`` means the run is not stored. A verifier that
raises leaves the run unstored, and the failure is reported through the setup's capture-failure callback.

Each ready-made verifier declares one required check, with an evidence kind of its own (``ToolOutcome``,
``HostCheck`` or ``HumanApproval``), and evidence for it built with :meth:`VerificationContext.create_evidence`. The
artifact revision defaults to :data:`UNVERSIONED_REVISION`; pass a real one (a build identifier or a commit) when the
artifact the agent works on changes between runs.
"""

from __future__ import annotations

import inspect
import uuid
from collections.abc import Awaitable, Callable, Sequence
from dataclasses import dataclass, field, replace
from typing import TYPE_CHECKING, Union

from ._text import is_null_or_whitespace
from .capture import CapturedRun
from .encoding import Instant
from .error_class import error_class
from .records import CheckResult, Evidence, ToolCall
from .verification import RequiredCheck

if TYPE_CHECKING:
    from .identity import ExperienceIdentity

UNVERSIONED_REVISION = "unversioned"
"""The artifact revision a verifier names when it is given none."""

TOOL_OUTCOME_KIND = "ToolOutcome"
HOST_CHECK_KIND = "HostCheck"
HUMAN_APPROVAL_KIND = "HumanApproval"


@dataclass(frozen=True)
class VerificationContext:
    """What a ``verify`` callback is given: the completed, sanitized run, the round it is checked in, and the
    identity it ran under."""

    run: CapturedRun
    round_id: uuid.UUID
    identity: ExperienceIdentity
    clock: Callable[[], Instant] = field(default=Instant.now, repr=False)
    new_id: Callable[[], uuid.UUID] = field(default=uuid.uuid4, repr=False)

    def create_evidence(
        self,
        check_id: str,
        kind: str,
        result: CheckResult,
        producer: str,
        artifact_revision: str,
        detail: str | None = None,
    ) -> Evidence:
        """A piece of evidence for this round: a new identifier, :attr:`round_id`, and the current time. The result
        must come from the host's own check, never from the model."""
        return Evidence(
            evidence_id=self.new_id(),
            verification_round_id=self.round_id,
            artifact_revision=artifact_revision,
            check_id=check_id,
            kind=kind,
            result=result,
            producer=producer,
            detail=detail,
            captured_at=self.clock(),
        )


@dataclass(frozen=True)
class Verification:
    """The host's verdict on one run: the checks it had to pass, the evidence from the host's own checks, and the
    artifact revision they were run against (it closes the round and is the current revision)."""

    required_checks: Sequence[RequiredCheck]
    evidence: Sequence[Evidence]
    artifact_revision: str


@dataclass(frozen=True)
class Approval:
    """A person's decision on a run, as the host's own approval flow recorded it. ``approver_id`` is stored verbatim as
    the evidence's producer, so use an opaque identifier (a user's object ID), never an email address or a name."""

    approver_id: str
    approved: bool


_MaybeAwaitable = Union["Verification | None", Awaitable["Verification | None"]]
Verifier = Callable[[VerificationContext], Awaitable["Verification | None"]]
"""A ready-made verifier: an async function of the verification context."""
VerifyCallback = Callable[[VerificationContext], _MaybeAwaitable]
"""What the setup accepts as ``verify``: a ready-made verifier, or any function returning a verification (or an
awaitable of one)."""


async def resolve(value: object) -> object:
    """``value``, awaited when it is awaitable."""
    return await value if inspect.isawaitable(value) else value


def _revision_of(artifact_revision: str | None) -> str:
    if artifact_revision is None:
        return UNVERSIONED_REVISION
    if is_null_or_whitespace(artifact_revision):
        raise ValueError("The artifact revision must not be blank; pass None for the default.")
    return artifact_revision


def _single(check_id: str, kind: str, evidence: Evidence, revision: str) -> Verification:
    return Verification((RequiredCheck(check_id, kind),), (evidence,), revision)


def tool_succeeded(tool_name: str, check_id: str | None = None, artifact_revision: str | None = None) -> Verifier:
    """
    Verifies a run by the outcome of its **last** call to ``tool_name``: the calls are ordered by attempt sequence,
    then by call sequence within the attempt, across all of the run's attempts, and the last one decides. A call that
    returned passes; a call that failed (it raised, or the classifier named an error class for it) fails, and the run
    is stored ``Quarantined``. A run with no such call is not stored.

    It decides from each captured call's tool name (exact match), sequence numbers and error, never from a result, so
    it works when sanitization redacts results. A tool that reports failure in its return value (an exit code, an HTTP
    status) counts as returned unless a classifier classifies it, for example
    :func:`~agentexperience.classifiers.exit_code`. The evidence is of kind ``ToolOutcome``, produced by
    ``"tool:" + tool_name``; its detail names only the failed call's error class.
    """
    if not isinstance(tool_name, str) or is_null_or_whitespace(tool_name):
        raise ValueError("The tool name must not be blank.")
    check = tool_name if check_id is None else check_id
    if is_null_or_whitespace(check):
        raise ValueError("The check ID must not be blank; pass None for the tool name.")
    revision = _revision_of(artifact_revision)
    producer = "tool:" + tool_name

    async def verify(context: VerificationContext) -> Verification | None:
        last: ToolCall | None = None
        for attempt in sorted(context.run.attempts, key=lambda attempt: attempt.sequence_number):
            for call in sorted(attempt.tool_calls, key=lambda call: call.sequence_number):
                if call.tool_name == tool_name:
                    last = call
        if last is None:
            return None
        failed = last.error is not None
        detail = (
            f"The last {tool_name} call failed: {error_class(last.error)}."
            if failed
            else f"The last {tool_name} call returned."
        )
        evidence = context.create_evidence(
            check, TOOL_OUTCOME_KIND, "Fail" if failed else "Pass", producer, revision, detail
        )
        return _single(check, TOOL_OUTCOME_KIND, evidence, revision)

    return verify


def check(
    check_id: str,
    fn: Callable[[VerificationContext], bool | Awaitable[bool | None] | None],
    artifact_revision: str | None = None,
) -> Verifier:
    """
    Verifies a run by the host's own check: ``True`` passes, ``False`` fails (the run is stored ``Quarantined``), and
    ``None`` means the run is not stored. ``fn`` may be sync or async. The evidence is of kind ``HostCheck``, produced
    by ``"host"``. The verdict must come from the host's own code (a test run, an exit code, a query), never from the
    model's claim.
    """
    if not isinstance(check_id, str) or is_null_or_whitespace(check_id):
        raise ValueError("The check ID must not be blank.")
    if fn is None:
        raise TypeError("The check function must not be None.")
    revision = _revision_of(artifact_revision)

    async def verify(context: VerificationContext) -> Verification | None:
        passed = await resolve(fn(context))
        if passed is None:
            return None
        if not isinstance(passed, bool):
            raise TypeError(f"The check '{check_id}' must return True, False or None, never another value.")
        evidence = context.create_evidence(check_id, HOST_CHECK_KIND, "Pass" if passed else "Fail", "host", revision)
        return _single(check_id, HOST_CHECK_KIND, evidence, revision)

    return verify


def approval(
    check_id: str,
    fn: Callable[[VerificationContext], Approval | Awaitable[Approval | None] | None],
    artifact_revision: str | None = None,
) -> Verifier:
    """
    Verifies a run by a person's decision: approved passes, rejected fails (the run is stored ``Quarantined``), and
    ``None`` means the run is not stored. The evidence is of kind ``HumanApproval``, and the approver's identifier is
    stored verbatim as its producer. A blank approver raises from the verifier, so the run is not stored and the
    failure is reported. The approver must come from the host's own authentication.
    """
    if not isinstance(check_id, str) or is_null_or_whitespace(check_id):
        raise ValueError("The check ID must not be blank.")
    if fn is None:
        raise TypeError("The approval function must not be None.")
    revision = _revision_of(artifact_revision)

    async def verify(context: VerificationContext) -> Verification | None:
        decided = await resolve(fn(context))
        if decided is None:
            return None
        if not isinstance(decided, Approval):
            raise TypeError("The approval function must return an Approval or None.")
        if not isinstance(decided.approver_id, str) or is_null_or_whitespace(decided.approver_id):
            raise RuntimeError(
                f"The approval for check '{check_id}' names no approver; an approval must name who decided. "
                "The run is not stored."
            )
        evidence = context.create_evidence(
            check_id, HUMAN_APPROVAL_KIND, "Pass" if decided.approved else "Fail", decided.approver_id, revision
        )
        return _single(check_id, HUMAN_APPROVAL_KIND, evidence, revision)

    return verify


def _validate(verifiers: Sequence[VerifyCallback]) -> tuple[VerifyCallback, ...]:
    if not verifiers:
        raise ValueError("At least one verifier is required.")
    if any(verifier is None for verifier in verifiers):
        raise ValueError("A verifier is None.")
    return tuple(verifiers)


async def _run(verifier: VerifyCallback, context: VerificationContext) -> Verification | None:
    result = await resolve(verifier(context))
    if result is not None and not isinstance(result, Verification):
        raise TypeError("A verifier must return a Verification or None.")
    return result


def all_of(*verifiers: VerifyCallback) -> Verifier:
    """
    Verifies a run by every one of ``verifiers``: it runs them in order and returns one verification with all their
    required checks and evidence, so the run is stored ``Validated`` only when every check passes. When one returns
    ``None`` it stops there (the rest are not run, so no person is asked for an approval needlessly) and the run is not
    stored.

    The combined revision is the one revision the verifiers name other than :data:`UNVERSIONED_REVISION` (evidence of
    a verifier given no revision is restamped with it), else :data:`UNVERSIONED_REVISION`. Two different explicit
    revisions, two verifiers requiring the same check, or a ``None`` required check raise from the verifier, so the
    run is not stored and the failure is reported.
    """
    every = _validate(verifiers)

    async def verify(context: VerificationContext) -> Verification | None:
        results: list[Verification] = []
        for verifier in every:
            result = await _run(verifier, context)
            if result is None:
                return None
            results.append(result)

        explicit = list(
            dict.fromkeys(
                result.artifact_revision for result in results if result.artifact_revision != UNVERSIONED_REVISION
            )
        )
        if len(explicit) > 1:
            raise RuntimeError(
                "all_of combined verifiers that name different artifact revisions; give them all the same revision. "
                "The run is not stored."
            )
        revision = explicit[0] if explicit else UNVERSIONED_REVISION
        checks: list[RequiredCheck] = []
        evidence: list[Evidence] = []
        ids: set[str] = set()
        for result in results:
            for required in result.required_checks:
                if required is None:
                    raise RuntimeError(
                        "all_of combined a verification with a None required check. The run is not stored."
                    )
                if required.check_id in ids:
                    raise RuntimeError(
                        f"all_of combined verifiers that both require the check '{required.check_id}'; give each its "
                        "own check identifier. The run is not stored."
                    )
                ids.add(required.check_id)
                checks.append(required)
            # Evidence of a verifier that took the default revision is restamped with the explicit one; any other
            # mismatch is left as it is, for the setup's own check to refuse.
            for item in result.evidence:
                if (
                    item is not None
                    and result.artifact_revision == UNVERSIONED_REVISION
                    and item.artifact_revision == UNVERSIONED_REVISION
                ):
                    item = replace(item, artifact_revision=revision)
                evidence.append(item)
        return Verification(tuple(checks), tuple(evidence), revision)

    return verify


def _passed(verification: Verification, round_id: uuid.UUID) -> bool:
    """Whether every required check has matching evidence for the round and revision, and all of it passed."""
    if not verification.required_checks:
        return False
    for required in verification.required_checks:
        if required is None:
            return False
        matching = [
            item
            for item in verification.evidence
            if item is not None
            and item.verification_round_id == round_id
            and item.artifact_revision == verification.artifact_revision
            and item.check_id == required.check_id
            and required.accepts(item.kind)
        ]
        if not matching or any(item.result != "Pass" for item in matching):
            return False
    return True


def any_of(*verifiers: VerifyCallback) -> Verifier:
    """
    Verifies a run by the first of ``verifiers`` that passes: it runs them in order and returns the first verification
    that passed, without running the rest. A verification passed when it requires at least one check and every
    required check has evidence for this round and the verification's revision, with that check's identifier and a
    kind it accepts (``*`` accepts any), all of it ``Pass``. When none passes, it returns the first one that returned a
    verification, so the failure is stored ``Quarantined``; when all return ``None``, the run is not stored.
    """
    every = _validate(verifiers)

    async def verify(context: VerificationContext) -> Verification | None:
        first: Verification | None = None
        for verifier in every:
            result = await _run(verifier, context)
            if result is None:
                continue
            if _passed(result, context.round_id):
                return result
            if first is None:
                first = result
        return first

    return verify
