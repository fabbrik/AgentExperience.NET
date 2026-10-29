"""
Story 27.2: a Python agent acts on the measured benefit (contract 1.10). Over the fleet .NET wrote (lesson X measured
``Hurts``, Y ``Helps``), held in the in-memory store: a cold cache injects as without the policy and starts a refresh;
once it settled, X is omitted as ``MeasuredHarmful`` and Y is injected first; the estimate never delays an invocation;
the policy off changes nothing; and the holdout still assigns what the block holds.
"""

from __future__ import annotations

import dataclasses
import threading
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _fleet import FLEET_MINIMUM, FLEET_NOW, FLEET_SCOPE, TASK, X, Y, fleet_holdout_rows, fleet_records
from _maf import ScriptedClient
from _sync import run
from agent_framework import Agent

from agentexperience import BenefitPolicy, EnvironmentFingerprint, HoldoutOptions
from agentexperience.benefit_policy import MEASURED_HARMFUL_DETAIL, SCOPE_MEASURED_HARMFUL_DETAIL
from agentexperience.identity import ExperienceIdentity
from agentexperience.inmemory import InMemoryExperienceStore
from agentexperience.maf import (
    ExperienceContextProvider,
    ExperienceSettings,
    HoldoutRecordResult,
    InjectionResult,
    _invoking_model,
    use_agent_experience,
)
from agentexperience.model import MODEL_METADATA_KEY

IDENTITY = ExperienceIdentity("benefit-principal", FLEET_SCOPE)
POLICY = BenefitPolicy(enabled=True, prefer_measured_helpful=True, minimum_runs_per_arm=FLEET_MINIMUM)


class GatedStore(InMemoryExperienceStore):
    """The fleet's store, whose holdout read waits until the test opens the gate."""

    def __init__(self, without: frozenset[Any] = frozenset(), only_rows_of: Any = None) -> None:
        super().__init__(
            [record for record in fleet_records() if record.experience_id not in without], clock=lambda: FLEET_NOW
        )
        for row in fleet_holdout_rows():
            if only_rows_of is None or row.experience_id == only_rows_of:
                self.seed_holdout_row(row)
        self.gate = threading.Event()
        self.gate.set()
        self.entered = threading.Event()
        self.reads = 0

    def read_holdout_outcomes(self, *args: Any, **kwargs: Any) -> Any:
        self.reads += 1
        self.entered.set()
        assert self.gate.wait(10)
        return super().read_holdout_outcomes(*args, **kwargs)


def agent_with(store: InMemoryExperienceStore, **options: Any) -> tuple[Any, ExperienceContextProvider, list[Any]]:
    results: list[InjectionResult] = []
    agent = use_agent_experience(
        Agent(client=ScriptedClient(calls=0), name="benefit-agent"),
        store=store,
        identity=IDENTITY,
        clock=lambda: FLEET_NOW,
        on_injected=results.append,
        **options,
    )
    (provider,) = [item for item in agent.context_providers if isinstance(item, ExperienceContextProvider)]
    return agent, provider, results


def test_a_lesson_measured_hurting_is_omitted_once_the_cache_is_warm() -> None:
    store = GatedStore()
    agent, provider, results = agent_with(store, benefit=POLICY)

    # Cold: injected as without the policy, and a refresh started in the background.
    run(agent.run(TASK))
    assert set(results[0].experience_ids) == {X, Y}
    assert all(item.reason != "MeasuredHarmful" for item in results[0].omitted)
    run(provider.wait_for_benefit_refreshes())
    assert store.reads == 1

    run(agent.run(TASK))
    assert results[1].experience_ids == (Y,)
    harmful = [item for item in results[1].omitted if item.reason == "MeasuredHarmful"]
    assert [(item.experience_id, item.detail) for item in harmful] == [(X, MEASURED_HARMFUL_DETAIL)]
    # Within the refresh interval nothing is read again.
    run(provider.wait_for_benefit_refreshes())
    assert store.reads == 1


def test_the_estimate_never_delays_an_invocation() -> None:
    store = GatedStore()
    store.gate.clear()
    agent, provider, results = agent_with(store, benefit=POLICY)
    for _ in range(3):
        run(agent.run(TASK))
    assert [set(result.experience_ids) for result in results] == [{X, Y}] * 3
    # The refresh's read has started (and is held at the gate): exactly one, however many invocations ran.
    assert store.entered.wait(10)
    assert store.reads == 1
    store.gate.set()
    run(provider.wait_for_benefit_refreshes())


def test_with_the_policy_off_nothing_is_read_or_omitted() -> None:
    store = GatedStore()
    agent, provider, results = agent_with(store)
    for _ in range(2):
        run(agent.run(TASK))
        run(provider.wait_for_benefit_refreshes())
    assert store.reads == 0
    assert [set(result.experience_ids) for result in results] == [{X, Y}] * 2
    assert all(not result.omitted for result in results)


def test_the_holdout_assigns_only_what_the_block_holds() -> None:
    held: list[HoldoutRecordResult] = []
    store = GatedStore()
    agent, provider, results = agent_with(
        store, benefit=POLICY, holdout=HoldoutOptions(rate=1.0), on_holdout_recorded=held.append
    )
    run(agent.run(TASK))
    run(provider.wait_for_benefit_refreshes())
    run(provider.wait_for_injection_records())
    run(agent.run(TASK))
    run(provider.wait_for_injection_records())
    # Held out both times; the second block (and so its assignment) no longer names the harmful lesson.
    assert [result.outcome for result in results] == ["Withheld", "Withheld"]
    assert set(results[0].withheld_experience_ids) == {X, Y}
    assert results[1].withheld_experience_ids == (Y,)
    assert [{record.experience_id for record in item.assignment.records} for item in held] == [{X, Y}, {Y}]
    assert [item.reason for item in results[1].omitted] == ["MeasuredHarmful"]


def test_the_invoking_model_is_the_recorded_one_when_captured_else_the_clients() -> None:
    client = ScriptedClient(calls=0)
    client.model_id = "client-model"  # type: ignore[attr-defined]
    agent = Agent(client=client, name="benefit-agent")
    settings = ExperienceSettings(store=InMemoryExperienceStore(), identity=IDENTITY)
    assert _invoking_model(settings, agent, captured=False) == "client-model"
    assert _invoking_model(settings, agent, captured=True) == "client-model"
    host = dataclasses.replace(
        settings,
        environment=EnvironmentFingerprint("host", "runtime", "os", None, {MODEL_METADATA_KEY: "host-model"}),
    )
    # The host's value wins for a captured run, as capture records it; an uncaptured one reads the client's.
    assert _invoking_model(host, agent, captured=True) == "host-model"
    assert _invoking_model(host, agent, captured=False) == "client-model"


def test_a_settings_benefit_must_be_a_policy() -> None:
    with pytest.raises(TypeError):
        ExperienceSettings(store=InMemoryExperienceStore(), identity=IDENTITY, benefit=object())  # type: ignore[arg-type]


def test_a_scope_measured_hurting_reports_nothing_to_inject_with_every_omission() -> None:
    # Only X's runs: the scope's lessons together hurt.
    store = GatedStore(only_rows_of=X)
    agent, provider, results = agent_with(store, benefit=POLICY)
    run(agent.run(TASK))
    run(provider.wait_for_benefit_refreshes())
    run(agent.run(TASK))
    last = results[-1]
    assert (last.outcome, last.experience_ids, last.byte_count) == ("NothingToInject", (), 0)
    assert {item.experience_id for item in last.omitted} == {X, Y}
    assert all(
        (item.reason, item.detail) == ("MeasuredHarmful", SCOPE_MEASURED_HARMFUL_DETAIL) for item in last.omitted
    )


def test_the_only_candidate_measured_harmful_reports_nothing_to_inject() -> None:
    # Y's record is gone, so X is the only candidate; Y's runs still balance the scope to no evidence.
    store = GatedStore(without=frozenset({Y}))
    agent, provider, results = agent_with(store, benefit=POLICY)
    run(agent.run(TASK))
    assert results[0].experience_ids == (X,)
    run(provider.wait_for_benefit_refreshes())
    run(agent.run(TASK))
    last = results[-1]
    assert (last.outcome, last.experience_ids) == ("NothingToInject", ())
    assert [(item.experience_id, item.reason, item.detail) for item in last.omitted] == [
        (X, "MeasuredHarmful", MEASURED_HARMFUL_DETAIL)
    ]
