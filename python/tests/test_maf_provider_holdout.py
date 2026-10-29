"""
Contract 1.11: measuring another memory provider in the Microsoft Agent Framework integration. Mirrors .NET's
``ExperienceHoldoutContextProvider`` tests against a stub provider (no memory vendor): the treatment and holdout arms,
the pass-through at rate 0 and for an uncaptured run, a recording failure that leaves the invocation unaffected, a
wrapped provider that raises, the caller's context left unchanged, an empty answer, a malformed key, and the estimate
over the finalized runs.
"""

from __future__ import annotations

import asyncio
import threading
import uuid
from collections.abc import Callable
from types import SimpleNamespace
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _maf import ScriptedClient
from _sync import run
from agent_framework import Agent, ContextProvider, Message, SessionContext, tool

import agentexperience.maf as maf
from agentexperience import (
    AuthorizationBounds,
    EnvironmentFingerprint,
    HoldoutOptions,
    Scope,
    assign_provider_holdout,
    estimate_provider_benefit,
    holdout_fraction,
    observer,
    provider_salt,
)
from agentexperience.inmemory import AuthorizationDenied
from agentexperience.maf import (
    ExperienceHoldoutContextProvider,
    InMemoryExperienceStore,
    ProviderHoldoutRecordResult,
    development_identity,
    exit_code,
    tool_succeeded,
    use_agent_experience,
    use_measured_memory,
)

KEY = "stub-memory"
MEMORY = "MEMORY-7f3a: the lock is held by the nightly job."
ENVIRONMENT = EnvironmentFingerprint("test-host", "python", "any", "1.0.0", {})
IDENTITY = development_identity(application="tests")
FIRST_RUN = uuid.UUID("00000000-0000-4000-8000-000000000001")
TASK = "Ticket #4812: a refund is stuck on a lock. Triage it."


class Boom(Exception):
    pass


class StubMemory(ContextProvider):
    """A memory provider: adds one message (or an instruction, or a tool, or nothing), counts both hooks, and keeps a
    count in its own session state."""

    def __init__(self, adds: str | None = "message", raises: bool = False) -> None:
        super().__init__("stub-memory-provider")
        self.adds = adds
        self.raises = raises
        self.before = 0
        self.after = 0
        self.before_contexts: list[Any] = []
        self.after_contexts: list[Any] = []
        self.states: list[dict[str, Any]] = []

    async def before_run(self, *, agent: Any, session: Any, context: Any, state: dict[str, Any]) -> None:
        self.before += 1
        self.before_contexts.append(context)
        self.states.append(state)
        state["count"] = state.get("count", 0) + 1
        if self.raises:
            raise Boom("the memory service is down")
        if self.adds == "message":
            context.extend_messages(self, [Message(role="user", contents=[MEMORY])])
        elif self.adds == "instruction":
            context.extend_instructions(self.source_id, MEMORY)
        elif self.adds == "empty-instruction":
            context.extend_instructions(self.source_id, "")
        elif self.adds == "handed-tool":
            context.extend_tools(self.source_id, list(context.tools))

    async def after_run(self, *, agent: Any, session: Any, context: Any, state: dict[str, Any]) -> None:
        self.after += 1
        self.after_contexts.append(context)


class OutcomeObserver:
    """Collects each operation's outcome and error class."""

    def __init__(self) -> None:
        self.seen: list[tuple[str, str, str | None]] = []

    def start(self, source: str, operation: str) -> object | None:
        return None

    def finish(
        self, token: object | None, source: str, operation: str, outcome: str, error_class: str | None, *_: Any
    ) -> None:
        self.seen.append((operation, outcome, error_class))

    def provider(self) -> list[tuple[str, str | None]]:
        return [(outcome, error) for operation, outcome, error in self.seen if operation == "record_provider_holdout"]


def sequential_ids() -> Callable[[], uuid.UUID]:
    counter = iter(range(1, 10_000))
    return lambda: uuid.UUID(f"00000000-0000-4000-8000-{next(counter):012d}")


def rate_for(arm: str, run_id: uuid.UUID = FIRST_RUN) -> float:
    """A rate that puts ``run_id`` in ``arm`` under the default salt."""
    fraction = holdout_fraction(run_id, provider_salt(KEY, HoldoutOptions().salt))
    assert 0.001 < fraction < 0.999
    return (fraction + 1) / 2 if arm == "Holdout" else fraction / 2


def exits(*values: str) -> Callable[[str], str]:
    queue = list(values)

    def run_tests(suite: str) -> str:
        """Runs the tests."""
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return run_tests


class Setup:
    """An agent wired with ``use_agent_experience``, the stub memory wrapped with ``use_measured_memory``."""

    def __init__(
        self,
        memory: StubMemory,
        rate: float,
        *,
        store: Any = None,
        identity: Any = IDENTITY,
        outcomes: tuple[str, ...] = ("exit=0",),
    ) -> None:
        self.memory = memory
        self.store = store if store is not None else InMemoryExperienceStore()
        self.client = ScriptedClient(calls=1, arguments={"suite": "unit"})
        self.recorded: list[ProviderHoldoutRecordResult] = []
        self.finalized: list[Any] = []
        self.failures: list[Any] = []
        self.agent = use_agent_experience(
            Agent(client=self.client, name="measured", tools=[tool(exits(*outcomes), name="run_tests")]),
            store=self.store,
            identity=identity,
            environment=ENVIRONMENT,
            verify=tool_succeeded("run_tests"),
            classify_tool_result=exit_code("run_tests"),
            on_finalized=self.finalized.append,
            on_capture_failure=self.failures.append,
            on_injection_failure=self.failures.append,
            new_id=sequential_ids(),
        )
        self.agent.context_providers.append(memory)
        self.wrapper = use_measured_memory(
            self.agent, memory, KEY, holdout=HoldoutOptions(rate=rate), on_recorded=self.recorded.append
        )
        self.observer = OutcomeObserver()

    def run(self, *texts: str) -> None:
        async def go() -> None:
            for text in texts:
                await self.agent.run(text)
                await self.wrapper.wait_for_records()

        observer.set_observer(self.observer)
        try:
            run(go())
        finally:
            observer.set_observer(None)

    def delivered(self, request: int = 0) -> bool:
        return any(MEMORY in (message.text or "") for message in self.client.first_requests()[request])

    def rows(self) -> list[tuple[uuid.UUID, str, str]]:
        return [(row.run_id, row.provider_key, row.arm) for row in self.store.provider_holdout_assignments()]


# ---- The arms ------------------------------------------------------------------------------------------------------


def test_treatment_delivers_the_providers_context_and_records_the_run() -> None:
    memory = StubMemory()
    setup = Setup(memory, rate_for("Treatment"))
    assert setup.agent.context_providers[-1] is setup.wrapper
    setup.run(TASK)
    assert setup.delivered()
    assert setup.rows() == [(FIRST_RUN, KEY, "Treatment")]
    assert [(item.outcome, item.assignment and item.assignment.arm) for item in setup.recorded] == [
        ("Recorded", "Treatment")
    ]
    assert setup.observer.provider() == [("Recorded", None)]
    assert (memory.before, memory.after) == (1, 1)
    # The wrapper keeps the wrapped provider's source ID, so its session state is its own.
    assert setup.wrapper.source_id == memory.source_id and memory.states[0] == {"count": 1}
    assert setup.failures == []


def test_holdout_withholds_the_providers_context_but_calls_both_its_hooks_and_records_the_run() -> None:
    memory = StubMemory()
    setup = Setup(memory, rate_for("Holdout"))
    setup.run(TASK)
    assert not setup.delivered()
    assert setup.rows() == [(FIRST_RUN, KEY, "Holdout")]
    assert (memory.before, memory.after) == (1, 1)
    # The post-invocation hook is given the invocation's own context, which never held what was withheld.
    assert memory.after_contexts[0] is not memory.before_contexts[0]
    assert memory.source_id not in memory.after_contexts[0].context_messages
    assert setup.observer.provider() == [("Recorded", None)]


def test_an_instruction_counts_as_an_addition() -> None:
    held = Setup(StubMemory("instruction"), rate_for("Holdout"))
    held.run(TASK)
    assert held.rows() == [(FIRST_RUN, KEY, "Holdout")]
    given = Setup(StubMemory("instruction"), rate_for("Treatment"))
    given.run(TASK)
    assert given.rows() == [(FIRST_RUN, KEY, "Treatment")]


@pytest.mark.parametrize("adds", [None, "empty-instruction", "handed-tool"])
def test_a_provider_that_adds_nothing_takes_no_part(adds: str | None) -> None:
    memory = StubMemory(adds)
    setup = Setup(memory, 1.0)
    setup.run(TASK)
    assert setup.rows() == [] and setup.recorded == [] and setup.observer.provider() == []
    assert (memory.before, memory.after) == (1, 1)


def test_each_run_is_assigned_by_its_run_id() -> None:
    setup = Setup(StubMemory(), 0.5)
    setup.run(TASK, TASK, TASK)
    assert len(setup.rows()) == 3
    for run_id, key, arm in setup.rows():
        assert key == KEY and arm == assign_provider_holdout(run_id, KEY, HoldoutOptions(rate=0.5))


# ---- Pass-through --------------------------------------------------------------------------------------------------


def test_at_rate_zero_the_wrapper_is_a_pass_through() -> None:
    memory = StubMemory()
    setup = Setup(memory, 0.0)
    setup.run(TASK)
    assert setup.delivered()
    assert setup.rows() == [] and setup.recorded == [] and setup.observer.provider() == []
    # Nothing is copied: the wrapped provider is handed the invocation's own context.
    assert memory.before_contexts[0] is memory.after_contexts[0]


def test_an_uncaptured_invocation_gets_what_the_provider_added_and_records_nothing() -> None:
    memory = StubMemory()
    setup = Setup(memory, 1.0, identity=lambda _: None)
    setup.run(TASK)
    assert setup.delivered()
    assert setup.rows() == [] and setup.recorded == [] and setup.observer.provider() == []
    assert (memory.before, memory.after) == (1, 1)


def test_a_streaming_invocation_is_not_captured_so_it_is_passed_through() -> None:
    memory = StubMemory()
    setup = Setup(memory, 1.0)

    async def go() -> None:
        async for _ in setup.agent.run(TASK, stream=True):
            pass
        await setup.wrapper.wait_for_records()

    run(go())
    assert setup.rows() == [] and memory.before == 1


# ---- Failures ------------------------------------------------------------------------------------------------------


def test_a_wrapped_provider_that_raises_propagates_unchanged_and_nothing_is_recorded() -> None:
    memory = StubMemory(raises=True)
    setup = Setup(memory, 1.0)
    with pytest.raises(Boom, match="memory service is down"):
        setup.run(TASK)
    assert setup.rows() == [] and setup.recorded == [] and setup.observer.provider() == []


class FailingStore(InMemoryExperienceStore):
    def __init__(self, error: BaseException) -> None:
        super().__init__()
        self.error = error

    def record_provider_holdout_assignment(self, *args: Any, **kwargs: Any) -> None:
        raise self.error


@pytest.mark.parametrize(
    ("error", "error_class"),
    [
        (OSError("the store is unreachable"), "Infrastructure"),
        (RuntimeError("unexpected"), "Unexpected"),
        (AuthorizationDenied("not granted"), None),
    ],
)
def test_a_recording_failure_leaves_the_invocation_unaffected(error: BaseException, error_class: str | None) -> None:
    setup = Setup(StubMemory(), rate_for("Treatment"), store=FailingStore(error))
    setup.run(TASK)
    assert setup.delivered()
    assert len(setup.finalized) == 1
    assert [(item.outcome, item.exception) for item in setup.recorded] == [("NotRecorded", error)]
    assert setup.observer.provider() == [("NotRecorded", error_class)]


# ---- The context passed in, with capture stood in for --------------------------------------------------------------

SCOPE = IDENTITY.scope


class _Captured:
    """Stands in for capture's per-invocation scope: a captured run (``run_id``) of the agent it yields."""

    def __init__(self, run_id: uuid.UUID | None, identity: Any = IDENTITY) -> None:
        self.agent = object()
        self.scope = SimpleNamespace(
            agent=self.agent,
            run_id=run_id,
            identity=identity,
            capture=SimpleNamespace(run=SimpleNamespace(scope=SCOPE)),
        )

    def __enter__(self) -> object:
        self.token = maf._current_scope.set(self.scope)  # type: ignore[arg-type]
        return self.agent

    def __exit__(self, *_: Any) -> None:
        maf._current_scope.reset(self.token)


HANDED_INPUT = Message(role="user", contents=["Triage the ticket."])
HANDED_CONTEXT = Message(role="user", contents=["Earlier context."])
HANDED_TOOL = object()


def _context() -> SessionContext:
    return SessionContext(
        input_messages=[HANDED_INPUT],
        context_messages={"history": [HANDED_CONTEXT]},
        instructions=["Be brief."],
        tools=[HANDED_TOOL],
    )


def _snapshot(context: Any) -> Any:
    return (
        [id(message) for message in context.input_messages],
        {key: [id(message) for message in batch] for key, batch in context.context_messages.items()},
        list(context.instructions),
        [id(item) for item in context.tools],
    )


def test_a_store_that_does_not_record_provider_holdout_is_not_tracked() -> None:
    seen: list[ProviderHoldoutRecordResult] = []
    wrapper = ExperienceHoldoutContextProvider(
        StubMemory(), KEY, HoldoutOptions(rate=1.0), object(), on_recorded=seen.append
    )
    context = _context()
    with _Captured(FIRST_RUN) as agent:
        run(wrapper.before_run(agent=agent, session=None, context=context, state={}))
    assert [(item.outcome, item.assignment and item.assignment.arm) for item in seen] == [("NotTracked", "Holdout")]
    assert _snapshot(context) == _snapshot(_context())


def test_a_record_that_runs_out_of_time_is_outcome_unknown() -> None:
    gate = threading.Event()

    class Hung(InMemoryExperienceStore):
        def record_provider_holdout_assignment(self, *args: Any, **kwargs: Any) -> None:
            gate.wait(10)

    seen: list[ProviderHoldoutRecordResult] = []
    wrapper = ExperienceHoldoutContextProvider(
        StubMemory(), KEY, HoldoutOptions(rate=1.0), Hung(), record_timeout=0.02, on_recorded=seen.append
    )

    async def go() -> None:
        with _Captured(FIRST_RUN) as agent:
            await wrapper.before_run(agent=agent, session=None, context=_context(), state={})
        await wrapper.wait_for_records()

    try:
        run(go())
        assert [item.outcome for item in seen] == ["OutcomeUnknown"]
        assert wrapper.abandoned_records == 1
    finally:
        gate.set()


def test_a_failure_while_inspecting_delivers_the_answer_and_records_nothing() -> None:
    def broken_clock() -> Any:
        raise RuntimeError("the clock failed")

    store = InMemoryExperienceStore()
    seen: list[ProviderHoldoutRecordResult] = []
    wrapper = ExperienceHoldoutContextProvider(
        StubMemory(), KEY, HoldoutOptions(rate=1.0), store, clock=broken_clock, on_recorded=seen.append
    )
    context = _context()
    watcher = OutcomeObserver()
    observer.set_observer(watcher)
    try:
        with _Captured(FIRST_RUN) as agent:
            run(wrapper.before_run(agent=agent, session=None, context=context, state={}))
    finally:
        observer.set_observer(None)
    assert [message.text for message in context.context_messages["stub-memory-provider"]] == [MEMORY]
    assert [(item.outcome, item.assignment) for item in seen] == [("NotRecorded", None)]
    assert watcher.provider() == [("NotRecorded", "Unexpected")]
    assert store.provider_holdout_assignments() == []


@pytest.mark.parametrize("adds", ["message", "instruction"])
def test_a_held_out_invocations_context_is_exactly_what_it_was_handed(adds: str) -> None:
    store = InMemoryExperienceStore()
    wrapper = ExperienceHoldoutContextProvider(StubMemory(adds), KEY, HoldoutOptions(rate=1.0), store)
    context = _context()
    containers = (context.input_messages, context.context_messages, context.instructions, context.tools)
    before = _snapshot(context)

    async def go() -> None:
        with _Captured(FIRST_RUN) as agent:
            await wrapper.before_run(agent=agent, session=None, context=context, state={})
        await wrapper.wait_for_records()

    run(go())
    assert _snapshot(context) == before
    after = (context.input_messages, context.context_messages, context.instructions, context.tools)
    assert all(a is b for a, b in zip(after, containers, strict=True))
    assert [(row.run_id, row.arm, row.scope) for row in store.provider_holdout_assignments()] == [
        (FIRST_RUN, "Holdout", SCOPE)
    ]


def test_a_treatment_invocation_gets_exactly_what_the_provider_added() -> None:
    store = InMemoryExperienceStore()
    wrapper = ExperienceHoldoutContextProvider(StubMemory(), KEY, HoldoutOptions(rate=rate_for("Treatment")), store)
    context = _context()
    with _Captured(FIRST_RUN) as agent:
        run(wrapper.before_run(agent=agent, session=None, context=context, state={}))
    assert context.input_messages == [HANDED_INPUT] and context.context_messages["history"] == [HANDED_CONTEXT]
    assert [message.text for message in context.context_messages["stub-memory-provider"]] == [MEMORY]
    assert context.instructions == ["Be brief."] and context.tools == [HANDED_TOOL]


def test_another_agents_run_or_no_run_takes_no_part() -> None:
    store = InMemoryExperienceStore()
    wrapper = ExperienceHoldoutContextProvider(StubMemory(), KEY, HoldoutOptions(rate=1.0), store)
    for run_id, foreign in ((None, False), (FIRST_RUN, True)):
        context = _context()
        with _Captured(run_id) as captured:
            agent = object() if foreign else captured
            run(wrapper.before_run(agent=agent, session=None, context=context, state={}))
        assert [message.text for message in context.context_messages["stub-memory-provider"]] == [MEMORY]
    assert store.provider_holdout_assignments() == []


def test_without_an_identity_the_hosts_authorize_callback_decides() -> None:
    def answer(bounds: Any) -> Callable[[Scope], Any]:
        def authorize(scope: Scope) -> Any:
            assert scope == SCOPE
            if isinstance(bounds, Exception):
                raise bounds
            return bounds

        return authorize

    for authorize, recorded in (
        (None, False),
        (answer(None), False),
        (answer(RuntimeError("no answer")), False),
        (answer(AuthorizationBounds.of(SCOPE)), True),
    ):
        store = InMemoryExperienceStore()
        wrapper = ExperienceHoldoutContextProvider(
            StubMemory(), KEY, HoldoutOptions(rate=1.0), store, authorize=authorize
        )
        context = _context()

        async def go(target: ExperienceHoldoutContextProvider = wrapper, into: Any = context) -> None:
            with _Captured(FIRST_RUN, identity=None) as agent:
                await target.before_run(agent=agent, session=None, context=into, state={})
            await target.wait_for_records()

        run(go())
        assert bool(store.provider_holdout_assignments()) is recorded
        # Held out when it takes part; delivered when it does not.
        assert ("stub-memory-provider" in context.context_messages) is not recorded


# ---- Construction --------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("key", ["", "a/b", "a:b", "mém0", "k" * 101])
def test_a_malformed_key_is_refused(key: str) -> None:
    with pytest.raises(ValueError, match="provider key"):
        ExperienceHoldoutContextProvider(StubMemory(), key, HoldoutOptions(), InMemoryExperienceStore())
    agent = use_agent_experience(Agent(client=ScriptedClient()), store=InMemoryExperienceStore(), identity=IDENTITY)
    with pytest.raises(ValueError, match="provider key"):
        use_measured_memory(agent, StubMemory(), key)


def test_the_wrapper_refuses_what_is_not_a_provider_and_use_measured_memory_needs_the_one_call_setup() -> None:
    with pytest.raises(TypeError):
        ExperienceHoldoutContextProvider(object(), KEY, HoldoutOptions(), InMemoryExperienceStore())
    with pytest.raises(TypeError):
        ExperienceHoldoutContextProvider(StubMemory(), KEY, 0.5, InMemoryExperienceStore())  # type: ignore[arg-type]
    with pytest.raises(ValueError, match="use_agent_experience"):
        use_measured_memory(Agent(client=ScriptedClient()), StubMemory(), KEY)


def test_use_measured_memory_takes_the_injection_holdout_and_replaces_the_provider_where_it_is() -> None:
    memory, other = StubMemory(), StubMemory()
    other.source_id = "other"
    agent = use_agent_experience(
        Agent(client=ScriptedClient(), context_providers=[memory, other]),
        store=InMemoryExperienceStore(),
        identity=IDENTITY,
        holdout=HoldoutOptions(rate=0.25, salt="experiment-2"),
    )
    wrapper = use_measured_memory(agent, memory, KEY)
    assert agent.context_providers[0] is wrapper and agent.context_providers[1] is other
    assert wrapper.inner is memory and wrapper.provider_key == KEY
    assert wrapper.holdout == HoldoutOptions(rate=0.25, salt="experiment-2")


# ---- The estimate --------------------------------------------------------------------------------------------------


def test_the_estimate_counts_each_finalized_run_in_exactly_one_arm() -> None:
    passes = (True, False, True, True, False, True, False, True, True, False)
    memory = StubMemory()
    setup = Setup(memory, 0.5, outcomes=tuple("exit=0" if ok else "exit=3" for ok in passes))
    setup.run(*(f"Ticket #{5200 + n}: a refund is stuck on a lock. Triage it." for n in range(len(passes))))
    runs = [item.record.source_run_id for item in setup.finalized]
    assert len(runs) == len(passes) and memory.before == memory.after == len(passes)
    arms = {run_id: assign_provider_holdout(run_id, KEY, HoldoutOptions(rate=0.5)) for run_id in runs}
    assert set(arms.values()) == {"Treatment", "Holdout"}
    assert setup.rows() == sorted((run_id, KEY, arm) for run_id, arm in arms.items())

    estimate_ = estimate_provider_benefit(setup.store, IDENTITY.authorization, IDENTITY.scope, KEY)
    overall = estimate_.overall
    assert estimate_.outcome == "Found" and estimate_.tracked and estimate_.provider_key == KEY
    assert overall.pending == 0 and overall.mixed_arm == 0 and overall.unverified == 0
    verified = {item.record.source_run_id: item.record.outcome.status == "Verified" for item in setup.finalized}
    for arm, counted in (("Treatment", overall.treatment), ("Holdout", overall.holdout)):
        members = [run_id for run_id in runs if arms[run_id] == arm]
        assert counted.runs == len(members)
        assert counted.successes == sum(verified[run_id] for run_id in members)
    # The treatment runs were shown the memory; the held-out ones were not.
    for index, run_id in enumerate(runs):
        assert setup.delivered(index) is (arms[run_id] == "Treatment")


def test_no_background_record_is_left_running() -> None:
    setup = Setup(StubMemory(), 1.0)
    setup.run(TASK)

    async def none_left() -> int:
        await setup.wrapper.wait_for_records()
        return len([task for task in asyncio.all_tasks() if not task.done()])

    assert run(none_left()) == 1  # only this one
