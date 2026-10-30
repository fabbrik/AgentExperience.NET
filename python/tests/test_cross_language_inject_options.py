"""
Story 27.4b's cross-language proof, the Python half (contract 1.13): agents configured with the same receiving agent
and model-authored policy inject the same block, with the same omissions, from the same rows as .NET. The seed
``CrossLanguageInjectOptionsPostgresTests`` injected from goes into a fresh database as a search seed is inserted; a
Python agent over the PostgreSQL store, at the same instant and with each configuration's options, runs on the same
user message, and its injection must be .NET's: the outcome, the role, the injected IDs, the byte count, the omissions
with their reasons and details, retrieval's exclusions, and the block byte for byte.
"""

from __future__ import annotations

from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _contract import REPOSITORY, read_json, scope_of
from _maf import ScriptedClient
from _postgres import PG_RETRIEVAL_POLICY, Server, application_conninfo, create_database
from _sync import run
from agent_framework import Agent

from agentexperience import BlockOptions, Instant, ReceivingAgent, from_stored
from agentexperience.identity import ExperienceIdentity
from agentexperience.maf import (
    HISTORICAL_REFERENCE_KEY,
    ExperienceContextProvider,
    InjectionResult,
    use_agent_experience,
)
from agentexperience.search import AuthorizationBounds

pytestmark = pytest.mark.postgres

DOTNET_INJECT_OPTIONS = (
    REPOSITORY / "tests" / "AgentExperience.Contract.Tests" / "cross-language" / "dotnet-inject-options.json"
)


def _receiving_agent(declared: dict[str, Any] | None) -> ReceivingAgent | None:
    if declared is None:
        return None
    return ReceivingAgent(
        available_tools=declared["availableTools"],
        max_risk_class=declared["maxRiskClass"],
        tool_risk_classes=declared["toolRiskClasses"],
        use_run_tools=declared["useRunTools"],
        gated_retrieval_window_multiplier=declared["gatedRetrievalWindowMultiplier"],
    )


def test_python_agents_inject_what_the_dotnet_agents_injected(pg_server: Server) -> None:
    from agentexperience.postgres import PostgresExperienceStore

    case = read_json(DOTNET_INJECT_OPTIONS)
    scope = scope_of(case["scope"])
    now = Instant.parse(case["now"])
    store = PostgresExperienceStore(application_conninfo(pg_server, create_database(pg_server, "python_inject_opts")))
    for stored in case["seed"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    assert len(case["agents"]) == 2
    for configuration in case["agents"]:
        options = configuration["options"]
        assert options["requestLimit"] is None and options["runTools"] is None
        results: list[InjectionResult] = []
        client = ScriptedClient(calls=0)
        agent: Any = use_agent_experience(
            Agent(client=client, name="inject-agent"),
            store=store,
            retrieval_policy=PG_RETRIEVAL_POLICY,
            identity=ExperienceIdentity("inject-principal", scope),
            clock=lambda: now,
            on_injected=results.append,
            block_options=BlockOptions(
                rendering=options["rendering"],
                failure_detail=options["failureDetail"],
                max_records=options["maxRecords"],
                max_bytes=options["maxBytes"],
                approach_arguments=options["approachArguments"],
            ),
            message_role="system" if options["messageRole"] == "System" else "user",
            model_authored_lessons=options["modelAuthoredLessons"],
            receiving_agent=_receiving_agent(options["receivingAgent"]),
        )
        (provider,) = [item for item in agent.context_providers if isinstance(item, ExperienceContextProvider)]

        run(agent.run(case["taskText"]))
        run(provider.wait_for_injection_records())

        blocks = [
            message
            for message in client.requests[0]
            if message.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True
        ]
        (result,) = results
        actual = {
            "outcome": result.outcome,
            "messageRole": None if not blocks else ("System" if blocks[0].role == "system" else "User"),
            "injected": [str(item) for item in result.experience_ids],
            "byteCount": result.byte_count,
            "omitted": [
                {"experienceId": str(item.experience_id), "reason": item.reason, "detail": item.detail}
                for item in result.omitted
            ],
            "excluded": [{"experienceId": str(item.experience_id), "reason": item.reason} for item in result.excluded],
            "block": blocks[0].text if blocks else None,
        }
        assert actual == configuration["expected"], configuration["name"]
