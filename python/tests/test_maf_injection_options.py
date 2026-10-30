"""
Contract 1.13: the injection options in the Microsoft Agent Framework integration. The host's callbacks are not
fixtures, so the request resolver (sync and async: replace, skip, raise, cancel, refused) and the injection decision
(permit, deny, ``None``, raise, no backfill) are tested here, with the receiving agent's gate over a real agent and the
``inject`` outcomes against ``docs/telemetry-names.json``.
"""

from __future__ import annotations

import asyncio
import json
from collections.abc import Iterator, Mapping
from pathlib import Path
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _maf import NEXT_TICKET, TICKET, ScriptedClient
from _sync import run
from agent_framework import Agent, tool

from agentexperience import BlockOptions, EnvironmentFingerprint, Instant, ReceivingAgent, observer
from agentexperience.finalize import FinalizedExperience
from agentexperience.maf import (
    HOST_DENIED_DETAIL,
    DecisionContext,
    ExperienceSettings,
    InjectionDecision,
    InjectionFailure,
    InjectionRequest,
    InjectionResult,
    InMemoryExperienceStore,
    RequestContext,
    development_identity,
    tool_succeeded,
    use_agent_experience,
)

ENVIRONMENT = EnvironmentFingerprint("test-host", "python", "any", "1.0.0", {})
T0 = Instant.parse("2026-03-02T09:00:00+00:00")
NAMES = json.loads((Path(__file__).resolve().parents[2] / "docs" / "telemetry-names.json").read_text(encoding="utf-8"))
INJECT_OUTCOMES = next(item["outcomes"] for item in NAMES["operations"] if item["operation"] == "inject")


class Host:
    def __init__(self) -> None:
        self.failures: list[InjectionFailure] = []
        self.results: list[InjectionResult] = []
        self.finalized: list[FinalizedExperience] = []

    def wire(self, client: ScriptedClient, **options: Any) -> Agent:
        options.setdefault("store", InMemoryExperienceStore())
        options.setdefault("identity", development_identity(application="tests"))
        options.setdefault("environment", ENVIRONMENT)
        options.setdefault("clock", lambda: T0)
        return use_agent_experience(
            Agent(client=client, name="triage-ticket", tools=[tool(lambda: "exit=0", name="run_tests")]),
            on_injection_failure=self.failures.append,
            on_injected=self.results.append,
            on_finalized=self.finalized.append,
            verify=tool_succeeded("run_tests"),
            **options,
        )


class Recording:
    def __init__(self) -> None:
        self.injects: list[tuple[str, Mapping[str, Any]]] = []

    def start(self, source: str, operation: str) -> object | None:
        return None

    def finish(
        self,
        token: object | None,
        source: str,
        operation: str,
        outcome: str,
        error_class: str | None,
        error_type: str | None,
        attributes: Mapping[str, Any],
    ) -> None:
        if operation == "inject":
            self.injects.append((outcome, dict(attributes)))


@pytest.fixture
def recording() -> Iterator[Recording]:
    hook = Recording()
    observer.set_observer(hook)
    try:
        yield hook
    finally:
        observer.set_observer(None)


def learned(*tickets: str) -> InMemoryExperienceStore:
    """A store holding one lesson per ticket, each learned with injection off."""
    store = InMemoryExperienceStore()
    for ticket in tickets:
        host = Host()
        run(host.wire(ScriptedClient(calls=1), store=store, inject=False).run(ticket))
        assert len(host.finalized) == 1
    return store


# ---- The injection decision ----------------------------------------------------------------------------------------


def test_a_permitting_decision_injects_and_is_given_the_record() -> None:
    seen: list[DecisionContext] = []

    def decide(context: DecisionContext) -> InjectionDecision:
        seen.append(context)
        return InjectionDecision.permit()

    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), decide_injection=decide).run(NEXT_TICKET))

    assert client.blocks()
    assert [result.outcome for result in host.results] == ["Injected"]
    assert len(seen) == 1
    assert seen[0].current is seen[0].candidate.record
    assert seen[0].model_authored is False


@pytest.mark.parametrize(
    ("decision", "detail"),
    [
        (lambda _: InjectionDecision.deny("risk policy"), "risk policy"),
        (lambda _: InjectionDecision.deny(), HOST_DENIED_DETAIL),
        (lambda _: None, HOST_DENIED_DETAIL),
        (lambda _: "yes", HOST_DENIED_DETAIL),
    ],
)
def test_a_denial_or_no_decision_omits_the_record_as_host_denied(decision: Any, detail: str) -> None:
    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), decide_injection=decision).run(NEXT_TICKET))

    assert client.blocks() == []
    assert [result.outcome for result in host.results] == ["NothingToInject"]
    assert [(item.reason, item.detail) for item in host.results[0].omitted] == [("HostDenied", detail)]


def test_a_decision_that_raises_denies() -> None:
    def decide(_: DecisionContext) -> InjectionDecision:
        raise RuntimeError("the risk service is down")

    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), decide_injection=decide).run(NEXT_TICKET))

    assert client.blocks() == []
    assert [(item.reason, item.detail) for item in host.results[0].omitted] == [
        ("HostDenied", "The host injection decision threw RuntimeError, so the record was denied.")
    ]


def test_a_denied_record_keeps_its_slot_and_nothing_backfills_it() -> None:
    denied: list[str] = []

    def decide(context: DecisionContext) -> InjectionDecision:
        if not denied:
            denied.append(str(context.candidate.record.experience_id))
            return InjectionDecision.deny("first")
        return InjectionDecision.permit()

    host, client = Host(), ScriptedClient(calls=1)
    agent = host.wire(
        client,
        store=learned(TICKET, NEXT_TICKET),
        decide_injection=decide,
        block_options=BlockOptions(max_records=1),
    )
    run(agent.run(NEXT_TICKET))

    assert client.blocks() == []
    reasons = [item.reason for item in host.results[0].omitted]
    # The selection's omissions come first, then the host's: the record limit, then the denial.
    assert reasons == ["OverRecordLimit", "HostDenied"]
    assert str(host.results[0].omitted[1].experience_id) == denied[0]


# ---- The request resolver ------------------------------------------------------------------------------------------


def test_a_sync_resolver_replaces_the_request() -> None:
    contexts: list[RequestContext] = []

    def resolve(context: RequestContext) -> InjectionRequest:
        contexts.append(context)
        return InjectionRequest("Nothing about tickets or locks matches this text")

    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), resolve_request=resolve).run(NEXT_TICKET))

    assert client.blocks() == []
    assert [result.outcome for result in host.results] == ["NothingToInject"]
    assert contexts[0].derived_task_text == NEXT_TICKET
    assert contexts[0].identity.principal
    assert contexts[0].messages[-1].text == NEXT_TICKET


def test_an_async_resolver_is_awaited() -> None:
    async def resolve(context: RequestContext) -> InjectionRequest | None:
        await asyncio.sleep(0)
        assert context.derived_task_text is not None
        return InjectionRequest(context.derived_task_text, correlation_id="trace-7")

    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), resolve_request=resolve).run(NEXT_TICKET))

    assert client.blocks()
    assert [result.outcome for result in host.results] == ["Injected"]


def test_a_resolver_returning_none_skips_the_invocation(recording: Recording) -> None:
    class Counting(InMemoryExperienceStore):
        searches = 0

        def search(self, bounds: Any, parameters: Any) -> Any:
            Counting.searches += 1
            return super().search(bounds, parameters)

    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=Counting(), resolve_request=lambda _: None).run(NEXT_TICKET))

    assert Counting.searches == 0
    assert client.blocks() == [] and host.results == [] and host.failures == []
    assert [outcome for outcome, _ in recording.injects] == ["Skipped"]


@pytest.mark.parametrize("asynchronous", [False, True])
def test_a_resolver_that_raises_injects_nothing_and_fails(recording: Recording, asynchronous: bool) -> None:
    def failing(_: RequestContext) -> InjectionRequest:
        raise LookupError("no caller")

    async def failing_async(_: RequestContext) -> InjectionRequest:
        raise LookupError("no caller")

    host, client = Host(), ScriptedClient(calls=1)
    response = run(
        host.wire(client, store=learned(TICKET), resolve_request=failing_async if asynchronous else failing).run(
            NEXT_TICKET
        )
    )

    assert response.text == "done"
    assert client.blocks() == []
    assert [outcome for outcome, _ in recording.injects] == ["Failed"]
    assert len(host.failures) == 1
    assert "resolve_request" in host.failures[0].reason
    assert isinstance(host.failures[0].exception, LookupError)


def test_the_invocations_cancellation_propagates_from_an_async_resolver() -> None:
    started = asyncio.Event()

    async def resolve(_: RequestContext) -> InjectionRequest:
        started.set()
        await asyncio.sleep(3600)
        raise AssertionError("unreachable")

    host, client = Host(), ScriptedClient(calls=1)
    agent = host.wire(client, resolve_request=resolve)

    async def scenario() -> None:
        invocation = asyncio.ensure_future(agent.run(TICKET))
        await started.wait()
        invocation.cancel()
        with pytest.raises(asyncio.CancelledError):
            await invocation

    asyncio.run(scenario())
    assert host.failures == [] and client.blocks() == []


@pytest.mark.parametrize(
    "request_",
    [
        InjectionRequest("   "),
        InjectionRequest("x" * 4097),
        InjectionRequest(TICKET, limit=0),
        InjectionRequest(TICKET, limit=51),
    ],
)
def test_a_request_retrieval_refuses_is_retrieval_failed(recording: Recording, request_: InjectionRequest) -> None:
    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), resolve_request=lambda _: request_).run(NEXT_TICKET))

    assert client.blocks() == []
    assert [outcome for outcome, _ in recording.injects] == ["RetrievalFailed"]
    assert len(host.failures) == 1 and host.failures[0].reason.startswith("Retrieval refused the request")


def test_the_requests_limit_caps_what_is_injected() -> None:
    host, client = Host(), ScriptedClient(calls=1)
    agent = host.wire(
        client,
        store=learned(TICKET, NEXT_TICKET),
        resolve_request=lambda context: InjectionRequest(context.derived_task_text or "", limit=1),
    )
    run(agent.run(NEXT_TICKET))

    assert len(host.results[0].experience_ids) == 1
    # The other record was cut by retrieval to the request's limit: not an omission of it.
    assert host.results[0].omitted == ()


def test_the_correlation_id_is_on_the_span(recording: Recording) -> None:
    host, client = Host(), ScriptedClient(calls=1)
    run(
        host.wire(client, resolve_request=lambda context: InjectionRequest(TICKET, correlation_id="trace-42")).run(
            NEXT_TICKET
        )
    )

    assert recording.injects[0][1].get("agentexperience.correlation_id") == "trace-42"


# ---- The receiving agent and the model-authored policy -------------------------------------------------------------


@pytest.mark.parametrize(
    ("agent", "reason"),
    [
        (ReceivingAgent(available_tools=[]), "ToolUnavailable"),
        (ReceivingAgent(available_tools=["read_ledger"]), "ToolUnavailable"),
        (ReceivingAgent(max_risk_class="High"), "RiskClassExceeded"),
        (ReceivingAgent(max_risk_class="Low", tool_risk_classes={"run_tests": "Medium"}), "RiskClassExceeded"),
        (ReceivingAgent(use_run_tools=True, available_tools=["read_ledger"]), "ToolUnavailable"),
    ],
)
def test_the_gate_keeps_out_an_approach_the_agent_cannot_carry_out(agent: ReceivingAgent, reason: str) -> None:
    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), receiving_agent=agent).run(NEXT_TICKET))

    assert client.blocks() == []
    assert [(item.reason, item.detail) for item in host.results[0].omitted] == [(reason, None)]


@pytest.mark.parametrize(
    "agent",
    [
        ReceivingAgent(available_tools=["run_tests"]),
        ReceivingAgent(max_risk_class="Low", tool_risk_classes={"run_tests": "Low"}),
        ReceivingAgent(max_risk_class="Critical"),
        ReceivingAgent(use_run_tools=True),
        ReceivingAgent(use_run_tools=True, available_tools=["run_tests", "other"]),
    ],
)
def test_the_gate_passes_an_approach_the_agent_can_carry_out(agent: ReceivingAgent) -> None:
    host, client = Host(), ScriptedClient(calls=1)
    run(host.wire(client, store=learned(TICKET), receiving_agent=agent).run(NEXT_TICKET))

    assert client.blocks()
    assert host.results[0].omitted == ()


def test_run_tools_are_the_agents_own_and_not_a_run_level_list() -> None:
    # agent-framework hands a context provider the agent's tools, not the tools= list of one run (it pops them from
    # the options it gives providers), so a run-level tool is not seen and its approach stays gated.
    host, client = Host(), ScriptedClient(calls=1)
    store = learned(TICKET)
    agent = use_agent_experience(
        Agent(client=client, name="triage-ticket"),
        store=store,
        identity=development_identity(application="tests"),
        environment=ENVIRONMENT,
        on_injected=host.results.append,
        receiving_agent=ReceivingAgent(use_run_tools=True),
    )
    run(agent.run(NEXT_TICKET))
    assert [item.reason for item in host.results[0].omitted] == ["ToolUnavailable"]

    run(agent.run(NEXT_TICKET, tools=[tool(lambda: "exit=0", name="run_tests")]))
    assert [item.reason for item in host.results[1].omitted] == ["ToolUnavailable"]


def test_the_model_authored_policy_is_validated_and_either_option_excludes() -> None:
    store, identity = InMemoryExperienceStore(), development_identity(application="tests")
    with pytest.raises(ValueError):
        ExperienceSettings(store=store, identity=identity, model_authored_lessons="Mark")  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        ExperienceSettings(store=store, identity=identity, receiving_agent={"available_tools": []})  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        ExperienceSettings(store=store, identity=identity, decide_injection="permit")  # type: ignore[arg-type]
    assert not ExperienceSettings(store=store, identity=identity).excludes_model_authored
    assert ExperienceSettings(store=store, identity=identity, model_authored_lessons="Exclude").excludes_model_authored
    assert ExperienceSettings(store=store, identity=identity, exclude_model_authored=True).excludes_model_authored


def test_every_inject_outcome_is_a_dotnet_name(recording: Recording) -> None:
    host = Host()
    store = learned(TICKET)
    for options in (
        {},
        {"resolve_request": lambda _: None},
        {"resolve_request": lambda _: 1 / 0},
        {"resolve_request": lambda _: InjectionRequest(" ")},
        {"decide_injection": lambda _: InjectionDecision.deny()},
    ):
        run(host.wire(ScriptedClient(calls=1), store=store, **options).run(NEXT_TICKET))

    outcomes = [outcome for outcome, _ in recording.injects]
    assert outcomes == ["Injected", "Skipped", "Failed", "RetrievalFailed", "NothingToInject"]
    assert set(outcomes) <= set(INJECT_OUTCOMES)
