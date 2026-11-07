"""
Story 27.5 (contract 1.14) through the one-call setup: an agent finalizing with the model-backed reflector stores a
model-authored lesson, fenced at the next injection; and the setup's lessons view exports the lesson-export fixture's
seed to the same Markdown bytes .NET wrote (the cross-language export, through the agent's own view).
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _contract import FIXTURES, read_json, scope_of
from _maf import NEXT_TICKET, TICKET, ScriptedClient, delivered
from _sync import run
from agent_framework import Agent, tool
from test_chat_reflector import ReplyClient
from test_lessons import memory_store

from agentexperience import BenefitOptions, EnvironmentFingerprint, Instant, MarkdownLessonFileSink
from agentexperience.chat_reflector import ChatClientExperienceReflector
from agentexperience.finalize import FinalizedExperience
from agentexperience.identity import ExperienceIdentity
from agentexperience.maf import (
    InjectionResult,
    InMemoryExperienceStore,
    agent_experience_lessons,
    development_identity,
    exit_code,
    tool_succeeded,
    use_agent_experience,
)
from agentexperience.reflector import LIBRARY_MODEL_REFLECTOR_PRODUCER_PREFIX, is_model_authored

ENVIRONMENT = EnvironmentFingerprint("test-host", "CPython 3", "linux", "1.0.0", {"region": "eu-west"})
ANSWER = {
    "modelId": "gpt-4o-2024-08-06",
    "messages": [
        {
            "role": "assistant",
            "contents": [
                {
                    "type": "text",
                    "text": '{"lesson":"Run the tests again after a lock failure.","warnings":["The cause is not '
                    'known."]}',
                }
            ],
        }
    ],
}


def exits(*values: str) -> Any:
    queue = list(values)

    def run_tests() -> str:
        """Runs the tests."""
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return run_tests


def test_an_agent_with_the_chat_reflector_stores_a_model_authored_lesson_fenced_at_injection() -> None:
    finalized: list[FinalizedExperience] = []
    injections: list[InjectionResult] = []
    model = ReplyClient(ANSWER)
    client = ScriptedClient()
    agent = use_agent_experience(
        Agent(client=client, name="triage-ticket", tools=[tool(exits("exit=3", "exit=0"), name="run_tests")]),
        store=InMemoryExperienceStore(),
        identity=development_identity(application="tests"),
        environment=ENVIRONMENT,
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        reflector=ChatClientExperienceReflector(model),
        on_finalized=finalized.append,
        on_injected=delivered(injections.append),
    )

    run(agent.run(TICKET))
    assert len(finalized) == 1
    record = finalized[0].record
    assert record.status == "Validated"
    reflection = record.reflection
    assert reflection is not None and reflection.authorship == "Model" and is_model_authored(reflection)
    assert reflection.lesson == "Run the tests again after a lock failure."
    assert reflection.producer == LIBRARY_MODEL_REFLECTOR_PRODUCER_PREFIX + "1.0.0 (gpt-4o-2024-08-06)"
    assert model.messages is not None and model.messages[0].role == "system"
    assert "run_tests" in model.messages[1].text

    run(agent.run(NEXT_TICKET))
    blocks = client.blocks()
    assert blocks and "Authored: by a model from captured run output" in blocks[-1]


def test_without_a_reflector_the_lesson_is_deterministic() -> None:
    finalized: list[FinalizedExperience] = []
    agent = use_agent_experience(
        Agent(client=ScriptedClient(), name="triage-ticket", tools=[tool(exits("exit=0"), name="run_tests")]),
        store=InMemoryExperienceStore(),
        identity=development_identity(application="tests"),
        environment=ENVIRONMENT,
        verify=tool_succeeded("run_tests"),
        on_finalized=finalized.append,
    )
    run(agent.run(TICKET))
    reflection = finalized[0].record.reflection
    assert reflection is not None and reflection.authorship == "Deterministic"


def test_a_reflector_without_reflect_is_refused() -> None:
    with pytest.raises(TypeError):
        use_agent_experience(
            Agent(client=ScriptedClient(), name="triage-ticket"),
            store=InMemoryExperienceStore(),
            identity=development_identity(application="tests"),
            reflector=object(),  # type: ignore[arg-type]
        )


def test_the_setups_lessons_view_exports_what_dotnet_exported(tmp_path: Path) -> None:
    case = FIXTURES / "lesson-export" / "measured-helps-only"
    options = read_json(case / "options.json")
    now = Instant.parse(options["now"])
    store: Any = memory_store(read_json(case / "seed.json"))
    agent = use_agent_experience(
        Agent(client=ScriptedClient(), name="triage-ticket"),
        store=store,
        identity=ExperienceIdentity("principal", scope_of(options["scope"])),
        clock=lambda: now,
    )
    path = tmp_path / "AGENTS.lessons.md"
    result = agent_experience_lessons(agent).export_measured(
        MarkdownLessonFileSink(path, clock=lambda: now), options=BenefitOptions(options["minimumRunsPerArm"])
    )
    assert result.outcome == "Found" and len(result.lessons) == 3
    assert path.read_bytes() == (case / "expected.md").read_bytes()
