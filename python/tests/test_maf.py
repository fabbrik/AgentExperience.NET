"""
Story 22.3: the Microsoft Agent Framework integration, end to end through a real ``agent_framework.Agent`` and a
scripted chat client: capture, classification, verification, storage and injection, wired with one call.
"""

from __future__ import annotations

import asyncio
import uuid
from collections.abc import Callable
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _maf import NEXT_TICKET, TICKET, ScriptedClient
from agent_framework import Agent, tool

from agentexperience import BlockOptions, EnvironmentFingerprint, Scope
from agentexperience.finalize import FinalizedExperience
from agentexperience.maf import (
    HISTORICAL_REFERENCE_KEY,
    SOURCE_ID,
    TOOL_REPORTED_ERROR_CLASS,
    CaptureFailure,
    ExperienceContextProvider,
    ExperienceIdentity,
    ExperienceSettings,
    InjectionFailure,
    InjectionResult,
    InMemoryExperienceStore,
    Verification,
    VerificationContext,
    check,
    development_identity,
    exit_code,
    tool_succeeded,
    use_agent_experience,
)

ENVIRONMENT = EnvironmentFingerprint("test-host", "python", "any", "1.0.0", {})


class Host:
    """What the host observes: failures, injections and finalized records."""

    def __init__(self) -> None:
        self.capture_failures: list[CaptureFailure] = []
        self.injection_failures: list[InjectionFailure] = []
        self.injections: list[InjectionResult] = []
        self.finalized: list[FinalizedExperience] = []

    def wire(self, agent: Agent, **options: Any) -> Agent:
        options.setdefault("store", InMemoryExperienceStore())
        if "identity" not in options:
            options["identity"] = development_identity(application="tests")
        options.setdefault("environment", ENVIRONMENT)
        return use_agent_experience(
            agent,
            on_capture_failure=self.capture_failures.append,
            on_injection_failure=self.injection_failures.append,
            on_injected=self.injections.append,
            on_finalized=self.finalized.append,
            **options,
        )


def exits(*values: str) -> Callable[[], str]:
    queue = list(values)

    def run_tests() -> str:
        """Runs the tests."""
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return run_tests


def agent_with(client: ScriptedClient, *tools: Any, name: str = "triage-ticket") -> Agent:
    return Agent(client=client, name=name, tools=list(tools))


def run(coroutine: Any) -> Any:
    return asyncio.run(coroutine)


def test_two_runs_the_second_gets_the_lesson_naming_both_calls() -> None:
    host = Host()
    client = ScriptedClient()
    agent = host.wire(
        agent_with(client, tool(exits("exit=3", "exit=0"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )

    run(agent.run(TICKET))
    assert host.capture_failures == []
    assert len(host.finalized) == 1
    first = host.finalized[0].record
    assert first.status == "Validated"
    assert first.task_id == "triage-ticket"
    assert first.task_summary == TICKET
    calls = first.attempts[0].tool_calls
    assert [(call.tool_name, call.error, call.result) for call in calls] == [
        ("run_tests", "exit 3", "exit=3"),
        ("run_tests", None, "exit=0"),
    ]
    assert client.blocks() == []

    run(agent.run(NEXT_TICKET))
    assert host.injection_failures == []
    assert host.injections[-1].experience_ids == (first.experience_id,)
    second_first_request = client.first_requests()[1]
    blocks = [m for m in second_first_request if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True]
    assert len(blocks) == 1
    assert blocks[0].role == "user"
    assert "run_tests [failed: exit 3], run_tests [returned]" in blocks[0].text
    # The second run was told what it was shown.
    second = host.finalized[1].record
    assert [exposure.experience_id for exposure in second.provenance.exposed_to] == [first.experience_id]


def test_a_store_that_raises_during_injection_leaves_the_agent_running_without_a_block() -> None:
    class BrokenStore(InMemoryExperienceStore):
        def search(self, bounds: Any, parameters: Any) -> Any:
            raise ConnectionError("the database is down")

    host = Host()
    client = ScriptedClient(calls=1)
    agent = host.wire(
        agent_with(client, tool(exits("exit=0"), name="run_tests")),
        store=BrokenStore(),
        verify=tool_succeeded("run_tests"),
    )

    response = run(agent.run(TICKET))

    assert response.text == "done"
    assert client.blocks() == []
    assert len(host.injection_failures) == 1
    failure = host.injection_failures[0]
    assert isinstance(failure.exception, ConnectionError)
    assert "the database is down" not in failure.reason
    assert len(host.finalized) == 1  # capture is independent of injection


def test_exit_3_with_the_exit_code_classifier_is_recorded_as_exit_3() -> None:
    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=1), tool(exits("exit=3 the lock is held"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )

    run(agent.run(TICKET))

    record = host.finalized[0].record
    call = record.attempts[0].tool_calls[0]
    assert call.error == "exit 3"
    assert record.status == "Quarantined"  # the last run_tests call failed


def test_a_class_that_is_not_one_token_is_recorded_as_tool_reported_error_and_reported_once() -> None:
    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=2), tool(exits("whatever"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=lambda context: "exit code 3",
    )

    run(agent.run(TICKET))

    calls = host.finalized[0].record.attempts[0].tool_calls
    assert [call.error for call in calls] == [TOOL_REPORTED_ERROR_CLASS, TOOL_REPORTED_ERROR_CLASS]
    assert [failure.stage for failure in host.capture_failures] == ["ToolResultClassification"]
    assert "exit code 3" not in host.capture_failures[0].reason


def test_white_space_around_a_class_is_trimmed_and_a_throwing_classifier_leaves_the_call_returned() -> None:
    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=1), tool(exits("x"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=lambda context: "  LockHeldError\n",
    )
    run(agent.run(TICKET))
    assert host.finalized[0].record.attempts[0].tool_calls[0].error == "LockHeldError"

    def boom(context: Any) -> str:
        raise RuntimeError("classifier bug")

    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=1), tool(exits("x"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=boom,
    )
    run(agent.run(TICKET))
    assert host.finalized[0].record.attempts[0].tool_calls[0].error is None
    assert [failure.stage for failure in host.capture_failures] == ["ToolResultClassification"]


def test_the_classifier_sees_the_name_raw_arguments_and_raw_result_read_only() -> None:
    seen: list[Any] = []
    host = Host()

    def run_tests(suite: str, token: str) -> str:
        """Runs a suite."""
        return "exit=0"

    agent = host.wire(
        agent_with(ScriptedClient(calls=1, arguments={"suite": "unit", "token": "s3cret"}), tool(run_tests)),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=lambda context: seen.append(context) or None,
    )
    run(agent.run(TICKET))

    assert len(seen) == 1
    context = seen[0]
    assert context.tool_name == "run_tests"
    assert dict(context.arguments) == {"suite": "unit", "token": "s3cret"}
    with pytest.raises(TypeError):
        context.arguments["suite"] = "x"
    # Sanitization runs when the attempt is stored: the secret is redacted, the unlisted value omitted.
    stored = host.finalized[0].record.attempts[0].tool_calls[0].arguments
    assert dict(stored) == {"token": ""}


def test_a_tool_that_raises_is_recorded_by_its_type_name_only_and_the_run_is_quarantined() -> None:
    class LockHeldError(Exception):
        pass

    def run_tests() -> str:
        """Runs the tests."""
        raise LockHeldError("secret model text in the message")

    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=1), tool(run_tests)),
        verify=tool_succeeded("run_tests"),
    )
    run(agent.run(TICKET))

    record = host.finalized[0].record
    error = record.attempts[0].tool_calls[0].error
    assert error is not None and error.endswith("LockHeldError")
    assert "secret" not in error
    assert record.status == "Quarantined"


def test_a_run_that_never_called_the_tool_is_not_stored_and_nothing_is_reported() -> None:
    host = Host()
    store = InMemoryExperienceStore()
    agent = host.wire(agent_with(ScriptedClient(calls=0)), store=store, verify=tool_succeeded("run_tests"))
    run(agent.run(TICKET))
    assert host.finalized == [] and host.capture_failures == [] and store.records() == []


def test_without_verify_nothing_is_stored() -> None:
    host = Host()
    store = InMemoryExperienceStore()
    agent = host.wire(agent_with(ScriptedClient(calls=1), tool(exits("exit=0"), name="run_tests")), store=store)
    run(agent.run(TICKET))
    assert store.records() == [] and host.capture_failures == []


def test_a_verify_that_raises_or_returns_foreign_evidence_is_reported_and_not_stored() -> None:
    def raises(context: VerificationContext) -> Verification | None:
        raise RuntimeError("verifier bug")

    def foreign(context: VerificationContext) -> Verification | None:
        other = VerificationContext(context.run, uuid.uuid4(), context.identity)
        evidence = other.create_evidence("c", "HostCheck", "Pass", "host", "rev")
        from agentexperience import RequiredCheck

        return Verification([RequiredCheck("c", "HostCheck")], [evidence], "rev")

    def not_a_verification(context: VerificationContext) -> Any:
        return {"passed": True}

    reasons = []
    for verify in (raises, foreign, not_a_verification, check("c", lambda _: "yes")):  # type: ignore[arg-type]
        host = Host()
        store = InMemoryExperienceStore()
        agent = host.wire(agent_with(ScriptedClient(calls=0)), store=store, verify=verify)
        run(agent.run(TICKET))
        assert store.records() == []
        assert [failure.stage for failure in host.capture_failures] == ["Verify"]
        reasons.append(host.capture_failures[0].reason)
    assert reasons[0] == "verify raised RuntimeError; the run is not stored."
    assert reasons[1].startswith("verify returned evidence that does not name this run's verification round")
    assert reasons[2] == "verify returned something other than a Verification or None; the run is not stored."
    assert reasons[3] == "verify raised TypeError; the run is not stored."  # a check returning neither bool nor None


def test_a_sync_host_check_stores_a_lesson() -> None:
    host = Host()
    agent = host.wire(agent_with(ScriptedClient(calls=0)), verify=check("released", lambda context: True))
    run(agent.run(TICKET))
    assert host.finalized[0].record.status == "Validated"


def test_an_agent_that_already_holds_a_provider_gets_exactly_one_block() -> None:
    host = Host()
    store = InMemoryExperienceStore()
    identity = development_identity(application="tests")
    own = ExperienceContextProvider(ExperienceSettings(store=store, identity=identity, environment=ENVIRONMENT))
    client = ScriptedClient()
    agent = Agent(client=client, name="triage-ticket", tools=[tool(exits("exit=3", "exit=0"), name="run_tests")])
    agent.context_providers.append(own)
    host.wire(
        agent,
        store=store,
        identity=identity,
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )
    assert [p for p in agent.context_providers if isinstance(p, ExperienceContextProvider)] == [own]

    run(agent.run(TICKET))
    run(agent.run(NEXT_TICKET))

    first = client.first_requests()[1]
    assert sum(1 for m in first if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True) == 1
    # The hand-wired provider still reports exposure to the run capture opened.
    assert host.finalized[1].record.provenance.exposed_to


def test_calling_the_setup_twice_is_refused() -> None:
    host = Host()
    agent = host.wire(agent_with(ScriptedClient()))
    with pytest.raises(ValueError, match="already"):
        host.wire(agent)


def test_inject_false_captures_without_injecting() -> None:
    host = Host()
    client = ScriptedClient()
    agent = host.wire(
        agent_with(client, tool(exits("exit=3", "exit=0"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        inject=False,
    )
    run(agent.run(TICKET))
    run(agent.run(NEXT_TICKET))
    assert len(host.finalized) == 2
    assert client.blocks() == []


def test_no_identity_means_neither_captured_nor_injected() -> None:
    host = Host()
    store = InMemoryExperienceStore()
    calls: list[Any] = []

    def nobody(context: Any) -> ExperienceIdentity | None:
        calls.append(context)
        return None

    client = ScriptedClient(calls=1)
    agent = host.wire(
        agent_with(client, tool(exits("exit=0"), name="run_tests")),
        store=store,
        identity=nobody,
        verify=tool_succeeded("run_tests"),
    )
    run(agent.run(TICKET))
    assert store.records() == [] and client.blocks() == []
    assert len(calls) == 1  # resolved once per invocation; injection reused it


def test_an_async_identity_resolver_scopes_what_is_stored_and_what_is_read() -> None:
    store = InMemoryExperienceStore()

    def identity_for(user: str) -> Any:
        async def resolve(context: Any) -> ExperienceIdentity:
            return ExperienceIdentity(user, Scope("acme", "support", "refunds", user_id=user))

        return resolve

    alice, bob = Host(), Host()
    alice_client, bob_client = ScriptedClient(), ScriptedClient()
    alice_agent = alice.wire(
        agent_with(alice_client, tool(exits("exit=3", "exit=0"), name="run_tests")),
        store=store,
        identity=identity_for("alice"),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )
    bob_agent = bob.wire(
        agent_with(bob_client, tool(exits("exit=0"), name="run_tests")),
        store=store,
        identity=identity_for("bob"),
    )
    run(alice_agent.run(TICKET))
    run(bob_agent.run(NEXT_TICKET))
    run(alice_agent.run(NEXT_TICKET))

    assert alice.finalized[0].record.scope.user_id == "alice"
    assert bob_client.blocks() == []  # another user's scope
    assert len(alice_client.blocks()) >= 1


def test_an_identity_resolver_that_raises_is_reported_and_the_agent_runs() -> None:
    def broken(context: Any) -> ExperienceIdentity | None:
        raise LookupError("no such user")

    host = Host()
    agent = host.wire(agent_with(ScriptedClient(calls=0)), identity=broken, verify=check("c", lambda _: True))
    response = run(agent.run(TICKET))
    assert response.text == "done"
    assert [failure.stage for failure in host.capture_failures] == ["ResolveIdentity"]
    assert host.finalized == []


def test_a_streaming_invocation_is_injected_but_not_captured() -> None:
    host = Host()
    store = InMemoryExperienceStore()
    client = ScriptedClient()
    agent = host.wire(
        agent_with(client, tool(exits("exit=3", "exit=0"), name="run_tests")),
        store=store,
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )
    run(agent.run(TICKET))

    async def stream() -> None:
        async for _ in agent.run(NEXT_TICKET, stream=True):
            pass

    run(stream())
    assert host.capture_failures == []
    assert len(store.records()) == 1  # the streamed invocation stored no run
    assert len(host.finalized) == 1
    assert host.finalized[0].record.provenance.exposed_to == ()  # and no exposure landed on any run
    counts = [
        sum(1 for m in request if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True)
        for request in client.requests
    ]
    assert counts == [0, 0, 0, 1, 1, 1]  # one block in each request of the streamed invocation, none before
    assert host.injections[-1].experience_ids == (host.finalized[0].record.experience_id,)


def test_a_session_with_history_derives_the_task_from_the_new_input_and_never_from_the_block() -> None:
    host = Host()
    client = ScriptedClient()
    agent = host.wire(
        agent_with(client, tool(exits("exit=3", "exit=0"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )
    session = agent.create_session()
    run(agent.run(TICKET, session=session))
    run(agent.run(NEXT_TICKET, session=session))
    run(agent.run("and retry", session=session))

    summaries = [finalized.record.task_summary for finalized in host.finalized]
    assert summaries[0] == TICKET
    assert summaries[1] == NEXT_TICKET
    # A short follow-up is joined after the previous user message of this invocation's own input only.
    assert summaries[2] == "and retry"
    assert all("HISTORICAL REFERENCE" not in (summary or "") for summary in summaries)


def test_the_block_can_be_sent_as_a_system_message_and_rendered_verbose() -> None:
    host = Host()
    client = ScriptedClient()
    agent = host.wire(
        agent_with(client, tool(exits("exit=3", "exit=0"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        message_role="system",
        block_options=BlockOptions(rendering="Verbose"),
    )
    run(agent.run(TICKET))
    run(agent.run(NEXT_TICKET))
    block = next(m for m in client.first_requests()[1] if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True)
    assert block.role == "system"
    assert block.additional_properties["_attribution"]["source_id"] == SOURCE_ID
    assert block.text.startswith("=== BEGIN HISTORICAL REFERENCE")
    assert "The records below are summaries of earlier runs" in block.text  # the verbose preamble


def test_the_development_identity_refuses_a_production_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    from agentexperience.maf import DevelopmentOnlyError

    identity = development_identity(application="tests")
    monkeypatch.setenv("DOTNET_ENVIRONMENT", "Production")
    with pytest.raises(DevelopmentOnlyError, match="Production"):
        development_identity()
    with pytest.raises(DevelopmentOnlyError):
        Host().wire(agent_with(ScriptedClient()), identity=identity)


def test_replayed_history_joins_a_short_follow_up_with_the_previous_request() -> None:
    from agent_framework import InMemoryHistoryProvider

    searched: list[str] = []

    class SpyStore(InMemoryExperienceStore):
        def search(self, bounds: Any, parameters: Any) -> Any:
            searched.append(parameters.task_text)
            return super().search(bounds, parameters)

    host = Host()
    agent = agent_with(ScriptedClient(calls=0))
    agent.context_providers.append(InMemoryHistoryProvider())  # history first: injection sees the replayed turns
    host.wire(agent, store=SpyStore())
    session = agent.create_session()
    run(agent.run(TICKET, session=session))
    run(agent.run("and retry", session=session))

    assert searched == [TICKET, TICKET + " — and retry"]
    assert host.injection_failures == []


# ---- The run lifecycle (as .NET's ExperienceCaptureMiddleware and CaptureScope) ---------------------------------


class _Seen:
    """A host check that records what it was shown: the run's status and its calls."""

    def __init__(self, passed: bool | None = True, delay: float = 0.0) -> None:
        self.runs: list[Any] = []
        self.passed = passed
        self.delay = delay

    async def __call__(self, context: VerificationContext) -> bool | None:
        self.runs.append(context.run)
        if self.delay:
            await asyncio.sleep(self.delay)
        return self.passed


def test_an_invocation_that_raises_ends_its_run_failed_and_the_exception_reaches_the_caller() -> None:
    class Broken(ScriptedClient):
        def _inner_get_response(self, **_: Any) -> Any:
            raise ConnectionError("the model endpoint is down")

    seen = _Seen()
    host = Host()
    agent = host.wire(agent_with(Broken()), verify=check("c", seen))
    with pytest.raises(ConnectionError):
        run(agent.run(TICKET))

    (captured,) = seen.runs
    assert captured.execution_status == "Failed"
    assert captured.attempts[0].error == "ConnectionError"
    assert captured.attempts[0].result is None
    # As in .NET, verify decides whether a failed invocation's run is stored.
    assert host.finalized[0].record.status == "Validated"
    assert host.capture_failures == []


def test_a_cancelled_invocation_ends_cancelled_records_the_cancelled_tool_call_and_is_not_held_up() -> None:
    started: list[asyncio.Event] = []

    async def run_tests() -> str:
        """Runs the tests, slowly."""
        started[-1].set()
        await asyncio.sleep(30)
        return "exit=0"

    async def scenario(seen: _Seen, host: Host) -> float:
        agent = host.wire(
            agent_with(ScriptedClient(calls=1), tool(run_tests)), verify=check("c", seen), finalization_timeout=0.2
        )
        started.append(asyncio.Event())
        task = asyncio.ensure_future(agent.run(TICKET))
        await started[-1].wait()
        loop = asyncio.get_running_loop()
        cancelled_at = loop.time()
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        return loop.time() - cancelled_at

    seen, host = _Seen(), Host()
    asyncio.run(scenario(seen, host))
    (captured,) = seen.runs
    assert captured.execution_status == "Cancelled"
    assert captured.attempts[0].error == "CancelledError"
    assert [call.error for call in captured.attempts[0].tool_calls] == ["CancelledError"]

    # A finalization slower than its bound does not hold the cancellation up: it is abandoned and reported.
    slow, host = _Seen(delay=5.0), Host()
    elapsed = asyncio.run(scenario(slow, host))
    assert elapsed < 2.0
    assert [failure.stage for failure in host.capture_failures] == ["Finalize"]
    assert host.finalized == []


def test_a_middleware_terminating_the_run_ends_it_completed_with_its_result() -> None:
    from agent_framework import AgentMiddleware, AgentResponse, Message, MiddlewareTermination

    class Stop(AgentMiddleware):
        async def process(self, context: Any, call_next: Any) -> None:
            context.result = AgentResponse(messages=[Message(role="assistant", contents=["stopped by policy"])])
            raise MiddlewareTermination()

    seen = _Seen()
    host = Host()
    agent = host.wire(agent_with(ScriptedClient(calls=0)), verify=check("c", seen))
    agent.middleware.append(Stop())
    run(agent.run(TICKET))

    (captured,) = seen.runs
    assert captured.execution_status == "Completed"
    assert captured.attempts[0].result == "stopped by policy"
    assert captured.attempts[0].error is None


def test_a_middleware_terminating_the_function_loop_at_a_call_is_recorded_and_reported() -> None:
    from agent_framework import FunctionMiddleware, MiddlewareTermination

    class StopTools(FunctionMiddleware):
        async def process(self, context: Any, call_next: Any) -> None:
            raise MiddlewareTermination(result="tool calls are paused")

    seen = _Seen()
    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=1), tool(exits("exit=0"), name="run_tests")), verify=check("c", seen)
    )
    agent.middleware.append(StopTools())
    run(agent.run(TICKET))

    calls = seen.runs[0].attempts[0].tool_calls
    assert [(call.tool_name, call.result, call.error) for call in calls] == [
        ("run_tests", "tool calls are paused", None)
    ]
    assert [failure.stage for failure in host.capture_failures] == ["ToolCall"]


def test_parallel_calls_are_stored_in_start_order_and_the_last_started_decides() -> None:
    class TwoAtOnce(ScriptedClient):
        def _inner_get_response(self, *, messages: Any, stream: bool, options: Any, **_: Any) -> Any:
            from agent_framework import ChatResponse, Content, Message

            self.requests.append(list(messages))
            if any(c.type == "function_result" for m in messages for c in m.contents):
                reply = Message(role="assistant", contents=["done"])
            else:
                reply = Message(
                    role="assistant",
                    contents=[
                        Content.from_function_call(call_id="slow", name="run_tests", arguments={"suite": "slow"}),
                        Content.from_function_call(call_id="fast", name="run_tests", arguments={"suite": "fast"}),
                    ],
                )

            async def respond() -> Any:
                return ChatResponse(messages=[reply])

            return respond()

    finished: list[str] = []

    async def run_tests(suite: str) -> str:
        """Runs a suite."""
        await asyncio.sleep(0.1 if suite == "slow" else 0)
        finished.append(suite)
        return "exit=3" if suite == "slow" else "exit=0"

    host = Host()
    agent = host.wire(
        agent_with(TwoAtOnce(), tool(run_tests)),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        sanitization=__import__("agentexperience").Sanitization.allowing(["suite"]),
    )
    run(agent.run(TICKET))

    assert finished == ["fast", "slow"]  # they did overlap: the first started finished last
    calls = host.finalized[0].record.attempts[0].tool_calls
    assert [(call.sequence_number, call.arguments["suite"], call.error) for call in calls] == [
        (0, "slow", "exit 3"),
        (1, "fast", None),
    ]
    assert host.finalized[0].record.status == "Validated"  # decided by the last started call, which returned


def test_tool_results_are_recorded_as_text() -> None:
    from agent_framework import Content

    from agentexperience.maf import _result_text

    def run_tests() -> dict[str, int]:
        """Runs the tests."""
        return {"exitCode": 0}

    host = Host()
    agent = host.wire(agent_with(ScriptedClient(calls=1), tool(run_tests)), verify=tool_succeeded("run_tests"))
    run(agent.run(TICKET))
    assert host.finalized[0].record.attempts[0].tool_calls[0].result == '{"exitCode": 0}'

    assert _result_text([Content.from_text("exit=0")]) == "exit=0"
    assert _result_text(Content.from_text("ok")) == "ok"
    assert _result_text([]) == "[]"
    assert _result_text(None) is None


# ---- Identity, reporting and settings ---------------------------------------------------------------------------


def test_a_development_identity_carries_its_host_environment_name(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("DOTNET_ENVIRONMENT", "Production")
    identity = development_identity(application="tests", environment_name="Development")
    host = Host()
    agent = host.wire(agent_with(ScriptedClient(calls=0)), identity=identity, verify=check("c", lambda _: True))
    run(agent.run(TICKET))
    assert len(host.finalized) == 1


def test_a_resolver_returning_a_development_identity_outside_development_is_refused_per_invocation(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    identity = development_identity(application="tests")
    host = Host()
    client = ScriptedClient(calls=0)
    store = InMemoryExperienceStore()
    agent = host.wire(agent_with(client), store=store, identity=lambda _: identity, verify=check("c", lambda _: True))
    monkeypatch.setenv("AGENTEXPERIENCE_ENVIRONMENT", "Production")

    response = run(agent.run(TICKET))

    assert response.text == "done"
    assert [failure.stage for failure in host.capture_failures] == ["ResolveIdentity"]
    assert "DevelopmentOnlyError" in host.capture_failures[0].reason
    assert store.records() == [] and client.blocks() == []


def test_settings_refuse_a_callback_that_is_not_callable() -> None:
    for name in ("verify", "classify_tool_result", "on_capture_failure", "on_injection_failure", "on_finalized"):
        with pytest.raises(TypeError, match=name):
            ExperienceSettings(store=InMemoryExperienceStore(), identity=lambda _: None, **{name: "not callable"})


def test_completion_exposure_and_identifier_failures_are_reported_never_raised(monkeypatch: pytest.MonkeyPatch) -> None:
    from agentexperience.capture import RunCapture

    def broken_complete(self: Any, status: Any, ended: Any) -> Any:
        raise RuntimeError("capture is broken")

    host = Host()
    agent = host.wire(agent_with(ScriptedClient(calls=0)), verify=check("c", lambda _: True))
    with monkeypatch.context() as patch:
        patch.setattr(RunCapture, "complete", broken_complete)
        run(agent.run(TICKET))
    assert [failure.stage for failure in host.capture_failures] == ["CompleteRun"]
    assert host.finalized == []

    # A delivered record capture refuses to note is reported.
    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(), tool(exits("exit=3", "exit=0"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )
    run(agent.run(TICKET))
    with monkeypatch.context() as patch:
        patch.setattr(RunCapture, "record_exposure", lambda self, exposures: "Conflict")
        run(agent.run(NEXT_TICKET))
    assert [failure.stage for failure in host.capture_failures] == ["RecordExposure"]

    # An identifier source that fails after the run opened: reported, the agent unaffected.
    issued = [0]

    def new_id() -> uuid.UUID:
        issued[0] += 1
        if issued[0] > 1:
            raise OSError("no entropy")
        return uuid.uuid4()

    host = Host()
    agent = host.wire(
        agent_with(ScriptedClient(calls=1), tool(exits("exit=0"), name="run_tests")),
        verify=tool_succeeded("run_tests"),
        new_id=new_id,
    )
    assert run(agent.run(TICKET)).text == "done"
    assert [failure.stage for failure in host.capture_failures] == ["ToolCall", "AppendAttempt"]
    assert host.finalized == []


def test_a_hand_wired_provider_with_its_own_settings_resolves_its_own_identity() -> None:
    resolved: list[Any] = []
    identity = development_identity(application="tests")

    def own(context: Any) -> ExperienceIdentity:
        resolved.append(context)
        return identity

    store = InMemoryExperienceStore()
    agent = agent_with(ScriptedClient(calls=0))
    agent.context_providers.append(ExperienceContextProvider(ExperienceSettings(store=store, identity=own)))
    Host().wire(agent, store=store, identity=identity)
    run(agent.run(TICKET))
    run(agent.run(NEXT_TICKET))
    assert len(resolved) == 2  # never capture's identity, which belongs to other settings


def test_a_session_request_carries_exactly_one_block_whatever_the_history_provider_order() -> None:
    from agent_framework import InMemoryHistoryProvider

    for history_first in (True, False, None):
        host = Host()
        client = ScriptedClient()
        agent = agent_with(client, tool(exits("exit=3", "exit=0", "exit=3", "exit=0"), name="run_tests"))
        if history_first:
            agent.context_providers.append(InMemoryHistoryProvider())
        host.wire(agent, verify=tool_succeeded("run_tests"), classify_tool_result=exit_code("run_tests"))
        if history_first is False:
            agent.context_providers.append(InMemoryHistoryProvider())
        session = agent.create_session()
        for ticket in (TICKET, NEXT_TICKET, NEXT_TICKET):
            run(agent.run(ticket, session=session))
        counts = [
            sum(1 for m in request if m.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True)
            for request in client.requests
        ]
        # No block before the first lesson; afterwards exactly one per request, never a stale replayed one.
        assert counts[:3] == [0, 0, 0], history_first
        assert all(count == 1 for count in counts[3:]), (history_first, counts)
