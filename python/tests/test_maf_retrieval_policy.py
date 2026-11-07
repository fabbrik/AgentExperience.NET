"""
Contract 1.12: the retrieval policy in the Microsoft Agent Framework integration. Timing is not a fixture, so the
timeout, the abandoned-search cap and a denied scope are tested here against a store that answers slowly or never.
"""

from __future__ import annotations

import asyncio
import threading
import time
from datetime import timedelta
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _maf import NEXT_TICKET, TICKET, ScriptedClient
from _sync import run
from agent_framework import Agent, tool

from agentexperience import (
    BlockOptions,
    ConfidenceDecayPolicy,
    EnvironmentFingerprint,
    Instant,
    RetrievalPolicy,
    RetrievalPolicyError,
)
from agentexperience.block import _number
from agentexperience.finalize import FinalizedExperience
from agentexperience.maf import (
    ExperienceContextProvider,
    ExperienceIdentity,
    ExperienceSettings,
    InjectionFailure,
    InjectionResult,
    InMemoryExperienceStore,
    development_identity,
    tool_succeeded,
    use_agent_experience,
)
from agentexperience.search import AuthorizationBounds

ENVIRONMENT = EnvironmentFingerprint("test-host", "python", "any", "1.0.0", {"domain": "framework-api"})
T0 = Instant.parse("2026-03-02T09:00:00+00:00")
DAY = 864_000_000_000  # ticks


def _at(days: float) -> Instant:
    return Instant(T0.ticks + int(days * DAY))


class Host:
    def __init__(self) -> None:
        self.failures: list[InjectionFailure] = []
        self.results: list[InjectionResult] = []
        self.finalized: list[FinalizedExperience] = []

    def wire(self, client: ScriptedClient, **options: Any) -> Agent:
        options.setdefault("store", InMemoryExperienceStore())
        options.setdefault("identity", development_identity(application="tests"))
        options.setdefault("environment", ENVIRONMENT)
        return use_agent_experience(
            Agent(client=client, name="triage-ticket", tools=[tool(lambda: "exit=0", name="run_tests")]),
            on_injection_failure=self.failures.append,
            on_injected=self.results.append,
            on_finalized=self.finalized.append,
            verify=tool_succeeded("run_tests"),
            **options,
        )


def provider_of(agent: Agent) -> ExperienceContextProvider:
    return next(item for item in agent.context_providers if isinstance(item, ExperienceContextProvider))


class Gated(InMemoryExperienceStore):
    """A store whose search waits for ``gate``: it answers slowly, or (until the gate opens) never."""

    def __init__(self) -> None:
        super().__init__()
        self.gate = threading.Event()
        self.searches = 0

    def search(self, bounds: Any, parameters: Any) -> Any:
        self.searches += 1
        self.gate.wait(10)
        return super().search(bounds, parameters)


def _wait_until(condition: Any) -> None:
    deadline = time.monotonic() + 5
    while not condition():
        assert time.monotonic() < deadline, "timed out waiting"
        time.sleep(0.01)


FAST = RetrievalPolicy(timeout=timedelta(milliseconds=50))


def test_a_search_past_the_timeout_injects_nothing_reports_timed_out_and_is_abandoned() -> None:
    host, client, store = Host(), ScriptedClient(calls=1), Gated()
    agent = host.wire(client, store=store, retrieval_policy=FAST)

    response = run(agent.run(TICKET))

    assert response.text == "done"
    assert client.blocks() == []
    assert [result.outcome for result in host.results] == ["RetrievalTimedOut"]
    assert host.results[0].experience_ids == ()
    assert host.failures == []  # a real timeout carries no failure
    provider = provider_of(agent)
    assert provider.abandoned_searches == 1

    store.gate.set()
    _wait_until(lambda: provider.abandoned_searches == 0)


def test_at_the_cap_no_search_starts_until_the_abandoned_ones_end() -> None:
    host, client, store = Host(), ScriptedClient(calls=1), Gated()
    policy = RetrievalPolicy(timeout=timedelta(milliseconds=50), max_abandoned_searches=1)
    agent = host.wire(client, store=store, retrieval_policy=policy)
    provider = provider_of(agent)

    run(agent.run(TICKET))
    assert provider.abandoned_searches == 1 and store.searches == 1

    # At the cap: TimedOut at once, the store not called, and a failure that names the cap.
    run(agent.run(NEXT_TICKET))
    assert store.searches == 1
    assert [result.outcome for result in host.results] == ["RetrievalTimedOut", "RetrievalTimedOut"]
    assert len(host.failures) == 1
    assert "too many abandoned searches" in host.failures[0].reason
    assert "at least 1" in host.failures[0].reason

    # Once the abandoned search ends, retrieval reaches the store again.
    store.gate.set()
    _wait_until(lambda: provider.abandoned_searches == 0)
    run(agent.run(NEXT_TICKET))
    assert store.searches == 2
    assert host.results[-1].outcome != "RetrievalTimedOut"


def test_a_scope_outside_the_bounds_is_denied_without_searching() -> None:
    class Counting(InMemoryExperienceStore):
        searches = 0

        def search(self, bounds: Any, parameters: Any) -> Any:
            Counting.searches += 1
            return super().search(bounds, parameters)

    identity = development_identity(application="tests")
    outside = ExperienceIdentity(identity.principal, identity.scope, bounds=AuthorizationBounds("another-tenant"))
    host, client = Host(), ScriptedClient(calls=1)
    agent = host.wire(client, store=Counting(), identity=outside)

    run(agent.run(TICKET))

    assert Counting.searches == 0
    assert [result.outcome for result in host.results] == ["RetrievalDenied"]
    assert client.blocks() == []


def test_the_callers_cancellation_propagates_and_abandons_the_search() -> None:
    host, client, store = Host(), ScriptedClient(calls=1), Gated()
    agent = host.wire(client, store=store, retrieval_policy=RetrievalPolicy(timeout=timedelta(seconds=30)))
    provider = provider_of(agent)

    async def scenario() -> None:
        invocation = asyncio.ensure_future(agent.run(TICKET))
        while store.searches == 0:
            await asyncio.sleep(0.01)
        invocation.cancel()
        with pytest.raises(asyncio.CancelledError):
            await invocation

    asyncio.run(scenario())
    assert provider.abandoned_searches == 1
    assert "RetrievalTimedOut" not in [result.outcome for result in host.results]
    store.gate.set()
    _wait_until(lambda: provider.abandoned_searches == 0)


def _learn(store: InMemoryExperienceStore) -> FinalizedExperience:
    """One run at ``T0`` that stores a lesson in ``store``."""
    host = Host()
    run(host.wire(ScriptedClient(calls=1), store=store, clock=lambda: T0).run(TICKET))
    assert len(host.finalized) == 1
    return host.finalized[0]


def test_max_age_excludes_a_lesson_whose_last_activity_is_older() -> None:
    store = InMemoryExperienceStore()
    lesson = _learn(store).record

    # 89 days later it is injected; 91 days later it is excluded as Expired.
    policy = RetrievalPolicy(max_age=timedelta(days=90))
    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=store, clock=lambda: _at(89), retrieval_policy=policy).run(NEXT_TICKET))
    assert host.results[0].experience_ids == (lesson.experience_id,)

    # (The run 89 days later stored a lesson of its own, which is recent.)
    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=store, clock=lambda: _at(91), retrieval_policy=policy).run(NEXT_TICKET))
    assert lesson.experience_id not in host.results[0].experience_ids
    assert host.results[0].experience_ids
    assert [(entry.experience_id, entry.reason) for entry in host.results[0].excluded] == [
        (lesson.experience_id, "Expired")
    ]


def test_confidence_decays_by_domain_in_the_block_it_ranks() -> None:
    store = InMemoryExperienceStore()
    lesson = _learn(store).record
    stored = lesson.reuse_confidence
    decay = ConfidenceDecayPolicy(half_lives={"framework-api": timedelta(days=30)})
    verbose = BlockOptions(rendering="Verbose")

    host, client = Host(), ScriptedClient(calls=1)
    agent = host.wire(client, store=store, clock=lambda: _at(30), confidence_decay=decay, block_options=verbose)
    run(agent.run(NEXT_TICKET))
    [block] = set(client.blocks())
    assert f"Confidence {_number(stored / 2)} x" in block
    assert host.results[0].excluded == ()


def test_the_settings_refuse_an_invalid_policy_and_the_integration_floor_wins() -> None:
    with pytest.raises(RetrievalPolicyError) as raised:
        RetrievalPolicy(timeout=timedelta(0))
    assert raised.value.option == "Timeout"
    identity = development_identity(application="tests")
    with pytest.raises(TypeError):
        ExperienceSettings(InMemoryExperienceStore(), identity, retrieval_policy="fast")  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        ExperienceSettings(InMemoryExperienceStore(), identity, confidence_decay={})  # type: ignore[arg-type]
    # The integration's own floor replaces the policy's, as .NET's MinimumSemanticSimilarity does.
    both = ExperienceSettings(
        InMemoryExperienceStore(),
        identity,
        min_vector_relevance=0.5,
        retrieval_policy=RetrievalPolicy(minimum_vector_relevance=0.6),
    )
    assert both.vector_floor == 0.5
    settings = ExperienceSettings(
        InMemoryExperienceStore(), identity, retrieval_policy=RetrievalPolicy(minimum_vector_relevance=0.6)
    )
    assert settings.vector_floor == 0.6
    assert ExperienceSettings(InMemoryExperienceStore(), identity, min_vector_relevance=0.4).vector_floor == 0.4


def test_the_lessons_view_applies_the_setups_retrieval_policy() -> None:
    from agentexperience.maf import agent_experience_lessons

    store = InMemoryExperienceStore()
    lesson = _learn(store).record
    policy = RetrievalPolicy(minimum_confidence=0.5, max_age=timedelta(days=90))
    agent = Host().wire(ScriptedClient(calls=1), store=store, clock=lambda: _at(91), retrieval_policy=policy)
    [listed] = agent_experience_lessons(agent).list().lessons
    assert listed.experience_id == lesson.experience_id
    assert listed.ineligibility == "Expired"
    assert listed.ineligible_reason == "its last activity is older than the retrieval policy's maximum age"

    strict = Host().wire(
        ScriptedClient(calls=1),
        store=store,
        clock=lambda: _at(1),
        retrieval_policy=RetrievalPolicy(minimum_confidence=1.0),
    )
    [floored] = agent_experience_lessons(strict).list().lessons
    assert lesson.reuse_confidence < 1.0
    assert floored.ineligibility == "BelowConfidenceFloor"
    assert floored.ineligible_reason is not None and floored.ineligible_reason.endswith("retrieval floor 1.00")
