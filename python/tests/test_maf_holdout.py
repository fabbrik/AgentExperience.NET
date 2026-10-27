"""
Story 25.6: the injection holdout in the Microsoft Agent Framework integration (contract 1.8). Mirrors .NET's
``ExperienceContextProvider``: decided once the block is built, only for a captured run of this agent whose block names
records; a held-out run gets no block, no exposure and no injection record, and reports ``Withheld``; a treatment run is
injected as before; both arms are recorded in the background (``record_holdout``); rate 0 changes nothing.
"""

from __future__ import annotations

import uuid
from collections.abc import Callable
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _maf import NEXT_TICKET, TICKET, ScriptedClient
from _sync import run
from agent_framework import Agent, tool

from agentexperience import Duration, EnvironmentFingerprint, Instant, observer
from agentexperience.finalize import FinalizedExperience
from agentexperience.maf import (
    ExperienceContextProvider,
    HoldoutOptions,
    HoldoutRecordResult,
    InjectionFailure,
    InjectionRecordResult,
    InjectionResult,
    InMemoryExperienceStore,
    development_identity,
    exit_code,
    tool_succeeded,
    use_agent_experience,
)

ENVIRONMENT = EnvironmentFingerprint("test-host", "python", "any", "1.0.0", {})
START = Instant.parse("2026-03-02T09:00:00+00:00")


def sequential_ids() -> Callable[[], uuid.UUID]:
    """``00000000-0000-4000-8000-00000000000N``, N = 1, 2, ...: the IDs a run takes (its own, each tool call's, the
    verification's) are deterministic, and the second run is N = 7 (fraction 0.89 under the default salt)."""
    counter = iter(range(1, 10_000))
    return lambda: uuid.UUID(f"00000000-0000-4000-8000-{next(counter):012d}")


SECOND_RUN = uuid.UUID("00000000-0000-4000-8000-000000000007")


def exits(*values: str) -> Callable[[], str]:
    queue = list(values)

    def run_tests() -> str:
        """Runs the tests."""
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return run_tests


class Observed:
    def __init__(self) -> None:
        self.injections: list[InjectionResult] = []
        self.failures: list[Any] = []
        self.finalized: list[FinalizedExperience] = []
        self.recorded: list[InjectionRecordResult] = []
        self.held: list[HoldoutRecordResult] = []
        self.outcomes: list[tuple[str, str]] = []


class OutcomeObserver:
    """Collects each operation's outcome."""

    def __init__(self, into: list[tuple[str, str]]) -> None:
        self.into = into

    def start(self, source: str, operation: str) -> object | None:
        return None

    def finish(self, token: object | None, source: str, operation: str, outcome: str, *_: Any) -> None:
        self.into.append((operation, outcome))


def two_runs(store: Any, **options: Any) -> tuple[Observed, ScriptedClient]:
    """A learning run, then a similar run the lesson applies to; both settled."""
    seen = Observed()
    client = ScriptedClient()
    agent = use_agent_experience(
        Agent(client=client, name="triage-ticket", tools=[tool(exits("exit=3", "exit=0"), name="run_tests")]),
        store=store,
        identity=development_identity(application="tests"),
        environment=ENVIRONMENT,
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        on_injected=seen.injections.append,
        on_injection_failure=seen.failures.append,
        on_capture_failure=seen.failures.append,
        on_finalized=seen.finalized.append,
        on_injection_recorded=seen.recorded.append,
        on_holdout_recorded=seen.held.append,
        new_id=sequential_ids(),
        **options,
    )
    (provider,) = [item for item in agent.context_providers if isinstance(item, ExperienceContextProvider)]

    async def both() -> None:
        await agent.run(TICKET)
        await agent.run(NEXT_TICKET)
        await provider.wait_for_injection_records()

    observer.set_observer(OutcomeObserver(seen.outcomes))
    try:
        run(both())
    finally:
        observer.set_observer(None)
    assert seen.failures == []
    return seen, client


def _inject_outcomes(seen: Observed) -> list[str]:
    return [outcome for operation, outcome in seen.outcomes if operation == "inject"]


def test_a_held_out_run_gets_no_block_no_exposure_and_no_injection_record_and_both_are_recorded() -> None:
    store = InMemoryExperienceStore()
    seen, client = two_runs(store, holdout=HoldoutOptions(rate=1.0))

    lesson = seen.finalized[0].record
    assert client.blocks() == []
    assert _inject_outcomes(seen) == ["NothingToInject", "Withheld"]
    assert [
        (item.outcome, item.experience_ids, item.byte_count, item.withheld_experience_ids) for item in seen.injections
    ] == [("Withheld", (), 0, (lesson.experience_id,))]
    # No exposure on the run, and nothing recorded as injected.
    assert seen.finalized[1].record.provenance.exposed_to == ()
    assert seen.recorded == [] and store.injections() == {}
    # The assignment: the run, the record its block named, at the rendered revision.
    (held,) = seen.held
    assert (held.outcome, held.assignment.run_id, held.assignment.arm) == ("Recorded", SECOND_RUN, "Holdout")
    (row,) = store.holdout_assignments()
    assert (row.run_id, row.experience_id, row.revision, row.arm, row.scope) == (
        SECOND_RUN,
        lesson.experience_id,
        lesson.revision,
        "Holdout",
        lesson.scope,
    )
    assert ("record_holdout", "Recorded") in seen.outcomes


def test_a_treatment_run_is_injected_as_before_and_recorded_too() -> None:
    store = InMemoryExperienceStore()
    seen, client = two_runs(store, holdout=HoldoutOptions(rate=0.25))

    lesson = seen.finalized[0].record
    assert len(client.blocks()) == 3  # the second run's three requests each carry the block
    assert _inject_outcomes(seen) == ["NothingToInject", "Injected"]
    assert [(item.outcome, item.experience_ids, item.withheld_experience_ids) for item in seen.injections] == [
        ("Injected", (lesson.experience_id,), ())
    ]
    assert [exposure.experience_id for exposure in seen.finalized[1].record.provenance.exposed_to] == [
        lesson.experience_id
    ]
    assert [item.outcome for item in seen.recorded] == ["Recorded"]
    assert [(row.run_id, row.experience_id, row.arm) for row in store.holdout_assignments()] == [
        (SECOND_RUN, lesson.experience_id, "Treatment")
    ]


def test_rate_zero_is_what_it_was_without_the_holdout() -> None:
    def outcome(**options: Any) -> tuple[Any, ...]:
        store = InMemoryExperienceStore(clock=lambda: START)
        ticks = iter(range(1, 10_000))
        seen, client = two_runs(store, clock=lambda: START.add(Duration.from_seconds(next(ticks))), **options)
        return (
            [[(message.role, message.text) for message in request] for request in client.requests],
            [item.record for item in seen.finalized],
            seen.injections,
            store.injections(),
            seen.outcomes,
            seen.held,
            store.holdout_assignments(),
        )

    without = outcome()
    assert outcome(holdout=HoldoutOptions(rate=0.0, salt="another")) == without
    assert without[5] == [] and without[6] == []


def test_a_store_that_does_not_record_the_holdout_is_reported_not_tracked_and_still_withholds() -> None:
    class Untracked:
        def __init__(self) -> None:
            self.inner = InMemoryExperienceStore()

        def insert_finalized(self, *args: Any, **kwargs: Any) -> None:
            self.inner.insert_finalized(*args, **kwargs)

        def search(self, *args: Any) -> Any:
            return self.inner.search(*args)

    seen, client = two_runs(Untracked(), holdout=HoldoutOptions(rate=1.0))
    assert client.blocks() == []
    assert [(item.outcome, item.assignment.arm) for item in seen.held] == [("NotTracked", "Holdout")]
    assert ("record_holdout", "NotTracked") in seen.outcomes


def test_a_store_that_fails_the_write_is_reported_not_recorded_and_the_run_is_unaffected() -> None:
    class Failing(InMemoryExperienceStore):
        def record_holdout_assignment(self, *args: Any) -> None:
            raise OSError("the store is down")

    store = Failing()
    seen, client = two_runs(store, holdout=HoldoutOptions(rate=1.0))
    assert client.blocks() == []
    (held,) = seen.held
    assert held.outcome == "NotRecorded" and isinstance(held.exception, OSError)
    assert len(seen.finalized) == 2


def test_an_uncaptured_invocation_is_injected_as_before_and_assigned_nothing() -> None:
    store = InMemoryExperienceStore()
    held: list[HoldoutRecordResult] = []
    failures: list[InjectionFailure] = []
    client = ScriptedClient()
    agent = use_agent_experience(
        Agent(client=client, name="triage-ticket", tools=[tool(exits("exit=3", "exit=0"), name="run_tests")]),
        store=store,
        identity=development_identity(application="tests"),
        environment=ENVIRONMENT,
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        on_injection_failure=failures.append,
        holdout=HoldoutOptions(rate=1.0),
        on_holdout_recorded=held.append,
    )

    async def learn_then_stream() -> None:
        await agent.run(TICKET)
        async for _ in agent.run(NEXT_TICKET, stream=True):
            pass

    run(learn_then_stream())
    assert failures == [] and held == [] and store.holdout_assignments() == []
    assert len(client.blocks()) >= 1  # the streamed invocation was injected


def test_the_holdout_option_is_checked_when_the_agent_is_set_up() -> None:
    with pytest.raises(TypeError, match="holdout"):
        use_agent_experience(
            Agent(client=ScriptedClient(), name="a"),
            store=InMemoryExperienceStore(),
            identity=development_identity(application="tests"),
            holdout=0.5,  # type: ignore[arg-type]
        )
    with pytest.raises(ValueError, match="rate"):
        HoldoutOptions(rate=2.0)
