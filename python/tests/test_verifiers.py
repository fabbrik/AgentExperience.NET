"""The built-in verifiers, with the cases of .NET's ``ExperienceVerifiersTests`` (story 21.2)."""

from __future__ import annotations

import uuid
from collections.abc import Awaitable, Callable
from typing import Any

import pytest
from _sync import run

from agentexperience import (
    Attempt,
    CapturedRun,
    Duration,
    EnvironmentFingerprint,
    Instant,
    Provenance,
    RequiredCheck,
    Scope,
    ToolCall,
)
from agentexperience.identity import ExperienceIdentity
from agentexperience.verifiers import (
    UNVERSIONED_REVISION,
    Approval,
    Verification,
    VerificationContext,
    all_of,
    any_of,
    approval,
    check,
    tool_succeeded,
)

RUN_TESTS = "run_tests"
ROUND_ID = uuid.UUID("21212121-0000-0000-0000-000000000002")
AT = Instant.parse("2026-03-02T09:00:00+00:00")
CALLER = ExperienceIdentity("svc", Scope("contoso", "support", "tickets"))


def call(sequence: int, tool: str, error: str | None, result: str | None = "out") -> ToolCall:
    return ToolCall(uuid.uuid4(), sequence, tool, {}, AT, Duration(0), result, error)


def attempt(sequence: int, *calls: ToolCall) -> Attempt:
    return Attempt(uuid.uuid4(), sequence, AT, Duration(0), tuple(calls), None, None)


def context(*attempts: Attempt) -> VerificationContext:
    run = CapturedRun(
        uuid.uuid4(),
        "task",
        None,
        CALLER.scope,
        EnvironmentFingerprint("h", "r", "o", None),
        Provenance("test", None, AT, None),
        AT,
        tuple(attempts),
        "Completed",
        AT,
    )
    return VerificationContext(run, ROUND_ID, CALLER, lambda: AT)


def verify(verifier: Callable[[VerificationContext], Awaitable[Verification | None]], ctx: Any = None) -> Any:
    return run(verifier(ctx or context()))


def fixed(
    check_id: str, passed: bool | None, ran: list[str] | None = None, revision: str | None = None
) -> Callable[[VerificationContext], Awaitable[Verification | None]]:
    def decide(_: VerificationContext) -> bool | None:
        if ran is not None:
            ran.append(check_id)
        return passed

    return check(check_id, decide, revision)


def test_tool_succeeded_passes_when_the_last_call_returned_although_an_earlier_one_failed() -> None:
    ctx = context(
        attempt(0, call(0, RUN_TESTS, "exit 1"), call(1, "other", None)),
        attempt(1, call(0, RUN_TESTS, "exit 1"), call(1, RUN_TESTS, None), call(2, "other", "exit 9")),
    )
    verification = verify(tool_succeeded(RUN_TESTS), ctx)
    assert list(verification.required_checks) == [RequiredCheck(RUN_TESTS, "ToolOutcome")]
    (evidence,) = verification.evidence
    assert evidence.result == "Pass"
    assert (evidence.check_id, evidence.kind, evidence.producer) == (RUN_TESTS, "ToolOutcome", "tool:run_tests")
    assert evidence.verification_round_id == ROUND_ID
    assert evidence.artifact_revision == UNVERSIONED_REVISION == verification.artifact_revision == "unversioned"


def test_tool_succeeded_fails_when_the_last_call_failed_and_orders_by_sequence_not_list_order() -> None:
    ctx = context(
        attempt(1, call(3, RUN_TESTS, "exit 1"), call(0, RUN_TESTS, None)),
        attempt(0, call(5, RUN_TESTS, None)),
    )
    verification = verify(tool_succeeded(RUN_TESTS, check_id="tests-pass", artifact_revision="build-42"), ctx)
    (evidence,) = verification.evidence
    assert evidence.result == "Fail"
    assert evidence.check_id == "tests-pass"
    assert evidence.artifact_revision == "build-42" == verification.artifact_revision
    assert list(verification.required_checks) == [RequiredCheck("tests-pass", "ToolOutcome")]
    assert "exit 1" in (evidence.detail or "")


def test_tool_succeeded_reads_only_the_error_so_a_redacted_result_still_counts() -> None:
    verification = verify(tool_succeeded(RUN_TESTS), context(attempt(0, call(0, RUN_TESTS, None, result=None))))
    assert verification.evidence[0].result == "Pass"


def test_tool_succeeded_returns_none_when_the_tool_was_never_called_and_matches_names_exactly() -> None:
    ctx = context(attempt(0, call(0, "Run_Tests", None), call(1, "other", None)))
    assert verify(tool_succeeded(RUN_TESTS), ctx) is None
    assert verify(tool_succeeded(RUN_TESTS)) is None


@pytest.mark.parametrize(
    ("tool", "check_id", "revision"),
    [(None, None, None), ("", None, None), (" ", None, None), (RUN_TESTS, " ", None), (RUN_TESTS, None, "")],
)
def test_tool_succeeded_refuses_blank_arguments(tool: Any, check_id: Any, revision: Any) -> None:
    with pytest.raises(ValueError):
        tool_succeeded(tool, check_id, revision)


@pytest.mark.parametrize(("passed", "expected"), [(True, "Pass"), (False, "Fail")])
def test_check_turns_the_hosts_verdict_into_evidence(passed: bool, expected: str) -> None:
    verification = verify(check("lint", lambda _: passed))
    assert list(verification.required_checks) == [RequiredCheck("lint", "HostCheck")]
    (evidence,) = verification.evidence
    assert (evidence.result, evidence.kind, evidence.producer) == (expected, "HostCheck", "host")
    assert evidence.artifact_revision == UNVERSIONED_REVISION


def test_check_accepts_an_async_function_and_none_stores_nothing() -> None:
    async def passed(_: VerificationContext) -> bool | None:
        return True

    assert verify(check("lint", passed)).evidence[0].result == "Pass"
    assert verify(check("lint", lambda _: None)) is None


def test_check_refuses_a_blank_id_or_no_function() -> None:
    with pytest.raises(ValueError):
        check(" ", lambda _: True)
    with pytest.raises(TypeError):
        check("lint", None)  # type: ignore[arg-type]


@pytest.mark.parametrize(("approved", "expected"), [(True, "Pass"), (False, "Fail")])
def test_approval_turns_a_persons_decision_into_evidence_produced_by_the_approver(
    approved: bool, expected: str
) -> None:
    verification = verify(approval("reviewed", lambda _: Approval("user-7f3a", approved), artifact_revision="pr-7"))
    assert list(verification.required_checks) == [RequiredCheck("reviewed", "HumanApproval")]
    (evidence,) = verification.evidence
    assert (evidence.result, evidence.kind, evidence.producer) == (expected, "HumanApproval", "user-7f3a")
    assert verification.artifact_revision == "pr-7"


def test_approval_returning_none_stores_nothing_and_a_blank_approver_raises() -> None:
    assert verify(approval("reviewed", lambda _: None)) is None
    for approver in ("", "  "):

        def approve(context: VerificationContext, who: str = approver) -> Approval:
            return Approval(who, True)

        with pytest.raises(RuntimeError):
            verify(approval("reviewed", approve))


def test_all_of_a_pass_and_a_fail_is_one_verification_with_both_checks() -> None:
    verification = verify(all_of(fixed("a", True), fixed("b", False)))
    assert [c.check_id for c in verification.required_checks] == ["a", "b"]
    assert [e.result for e in verification.evidence] == ["Pass", "Fail"]
    assert verification.artifact_revision == UNVERSIONED_REVISION


def test_all_of_stops_at_the_first_none_and_returns_none() -> None:
    ran: list[str] = []
    assert verify(all_of(fixed("a", True, ran), fixed("b", None, ran), fixed("c", True, ran))) is None
    assert ran == ["a", "b"]


def test_all_of_refuses_two_different_explicit_revisions_and_duplicate_checks() -> None:
    with pytest.raises(RuntimeError):
        verify(all_of(fixed("a", True, revision="build-1"), fixed("b", True, revision="build-2")))
    with pytest.raises(RuntimeError):
        verify(all_of(fixed("a", True), fixed("a", False)))


def test_all_of_takes_the_explicit_revision_over_the_default_and_restamps_the_defaulted_evidence() -> None:
    verification = verify(all_of(fixed("a", True), fixed("b", True, revision="build-2"), fixed("c", False)))
    assert verification.artifact_revision == "build-2"
    assert all(e.artifact_revision == "build-2" for e in verification.evidence)
    assert all(e.verification_round_id == ROUND_ID for e in verification.evidence)


def test_all_of_refuses_a_none_required_check() -> None:
    def broken(_: VerificationContext) -> Verification:
        return Verification([None], [], UNVERSIONED_REVISION)  # type: ignore[list-item]

    with pytest.raises(RuntimeError):
        verify(all_of(fixed("a", True), broken))


def test_any_of_does_not_count_a_verification_missing_evidence_for_a_required_check_as_passed() -> None:
    def incomplete(ctx: VerificationContext) -> Verification:
        return Verification(
            [RequiredCheck("a", "HostCheck"), RequiredCheck("b", "HostCheck"), RequiredCheck("c", "TestResult")],
            [
                ctx.create_evidence("a", "HostCheck", "Pass", "host", UNVERSIONED_REVISION),
                ctx.create_evidence("c", "HostCheck", "Pass", "host", UNVERSIONED_REVISION),
            ],
            UNVERSIONED_REVISION,
        )

    def any_kind(ctx: VerificationContext) -> Verification:
        return Verification(
            [RequiredCheck("d", "*")],
            [ctx.create_evidence("d", "Whatever", "Pass", "host", UNVERSIONED_REVISION)],
            UNVERSIONED_REVISION,
        )

    chosen = verify(any_of(incomplete, any_kind))
    assert [c.check_id for c in chosen.required_checks] == ["d"]
    assert len(verify(any_of(incomplete)).required_checks) == 3


def test_any_of_returns_the_first_passing_verification_and_stops_there() -> None:
    ran: list[str] = []
    verification = verify(
        any_of(fixed("a", False, ran), fixed("b", None, ran), fixed("c", True, ran), fixed("d", True, ran))
    )
    assert [c.check_id for c in verification.required_checks] == ["c"]
    assert ran == ["a", "b", "c"]


def test_any_of_with_none_passing_returns_the_first_verification_and_with_all_none_returns_none() -> None:
    failed = verify(any_of(fixed("a", None), fixed("b", False), fixed("c", False)))
    assert [c.check_id for c in failed.required_checks] == ["b"]
    assert verify(any_of(fixed("a", None), fixed("b", None))) is None


def test_all_of_and_any_of_refuse_no_verifiers_or_a_none_one() -> None:
    with pytest.raises(ValueError):
        all_of()
    with pytest.raises(ValueError):
        any_of()
    with pytest.raises(ValueError):
        all_of(fixed("a", True), None)  # type: ignore[arg-type]


def test_check_refuses_a_verdict_that_is_neither_bool_nor_none() -> None:
    verdicts: tuple[object, ...] = ("yes", 1, 0, [])
    for value in verdicts:

        def verdict(context: VerificationContext, value: object = value) -> Any:
            return value

        with pytest.raises(TypeError):
            verify(check("lint", verdict))
