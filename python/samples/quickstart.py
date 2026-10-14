"""
The Python quick start: the README's five lines, run twice, with a scripted stand-in for the model.

    uv run --extra maf python samples/quickstart.py

No key, no network, no database. The agent triages a stuck refund by running the tests: in the first run the first
test run fails (``exit=3``) and the second passes (``exit=0``). ``tool_succeeded("run_tests")`` verifies the run by the
last test run, which passed, so the run is stored as a lesson. The second run, on a similar ticket, finds the lesson
and its model is handed the Historical Reference block, which names both calls:
``run_tests [failed: exit 3], run_tests [returned]``.

The model is a script, not a model, and the output says so: it calls ``run_tests`` until a call exits 0. It shows the
loop working (capture, verification, storage, retrieval and injection), not that a lesson helps a real model.
"""

from __future__ import annotations

import asyncio
import sys
from collections.abc import Sequence
from typing import Any

from agent_framework import (
    Agent,
    BaseChatClient,
    ChatMiddlewareLayer,
    ChatResponse,
    Content,
    FunctionInvocationLayer,
    Message,
    tool,
)

from agentexperience import EnvironmentFingerprint
from agentexperience.maf import (
    HISTORICAL_REFERENCE_KEY,
    InMemoryExperienceStore,
    development_identity,
    exit_code,
    tool_succeeded,
    use_agent_experience,
)

FIRST_TICKET = "Ticket #4812: a refund is stuck on a lock. Triage it."
SECOND_TICKET = "Ticket #4977: a refund is stuck on a lock again. Triage it."
STAND_IN = "a scripted stand-in, not a real model"


class ScriptedModel(FunctionInvocationLayer[Any], ChatMiddlewareLayer[Any], BaseChatClient[Any]):
    """
    A scripted stand-in for a chat model: it calls ``run_tests`` until a call returns ``exit=0``, then answers. It
    reads only what a real model is sent, and keeps the Historical Reference block it was handed, to print it.
    """

    def __init__(self) -> None:
        super().__init__()
        self.block: str | None = None
        self._calls = 0

    def reset(self) -> None:
        self.block = None

    async def _inner_get_response(self, *, messages: Sequence[Message], stream: bool, options: Any, **_: Any) -> Any:
        if stream:
            raise NotImplementedError("The scripted stand-in does not stream.")
        for message in messages:
            if message.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True and self.block is None:
                self.block = message.text
        results = [content for message in messages for content in message.contents if content.type == "function_result"]
        if any("exit=0" in str(content.result) for content in results):
            return ChatResponse(
                messages=[Message(role="assistant", contents=["The tests pass; the refund is released."])]
            )
        self._calls += 1
        call = Content.from_function_call(call_id=f"call-{self._calls}", name="run_tests", arguments={})
        return ChatResponse(messages=[Message(role="assistant", contents=[call])])


class TestRunner:
    """The tool's state: the first test run of each ticket fails with exit code 3, every later one passes."""

    def __init__(self) -> None:
        self.runs = 0

    def reset(self) -> None:
        self.runs = 0

    def run_tests(self) -> str:
        """Runs the test suite and returns its exit status."""
        self.runs += 1
        return "exit=3 1 test failed: the refund lock is still held" if self.runs == 1 else "exit=0 all tests passed"


async def main() -> int:
    runner = TestRunner()
    model = ScriptedModel()

    # The README's five lines: the agent, a store, a development identity, your own check, and the classifier that
    # reads "exit=3" in the tool's result as a failure.
    agent = use_agent_experience(
        Agent(client=model, name="triage-ticket", tools=[tool(runner.run_tests, name="run_tests")]),
        store=InMemoryExperienceStore(),
        identity=development_identity(application="quickstart"),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
        # Not one of the five: a fixed environment, so the output is the same on every machine. Keep the default.
        environment=EnvironmentFingerprint("quick-start", "python", "any", "1.0.0-quick-start", {}),
    )

    print(f"Model: {STAND_IN}")
    blocks: list[str | None] = []
    for number, ticket in enumerate((FIRST_TICKET, SECOND_TICKET), start=1):
        runner.reset()
        model.reset()
        await agent.run(ticket)
        blocks.append(model.block)
        memory = "lesson injected" if model.block else "no memory yet"
        print(f"Run {number} ({memory}): {ticket}  ({runner.runs} test runs)")

    print()
    print("What run 2's model was handed, besides the ticket:")
    print()
    print((blocks[1] or "(nothing: no lesson was injected)").rstrip())
    return 0 if blocks[0] is None and blocks[1] else 1


if __name__ == "__main__":
    # The development identity refuses outside Development/Test; a plain script has no environment set, so it runs.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(asyncio.run(main()))
