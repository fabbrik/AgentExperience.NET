"""A scripted chat client for the MAF integration tests: no key, no network."""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any

from agent_framework import (
    BaseChatClient,
    ChatMiddlewareLayer,
    ChatResponse,
    ChatResponseUpdate,
    Content,
    FunctionInvocationLayer,
    Message,
)

from agentexperience.maf import HISTORICAL_REFERENCE_KEY

TICKET = "Ticket #4812: a refund is stuck on a lock. Triage it."
NEXT_TICKET = "Ticket #4977: a refund is stuck on a lock again. Triage it."


class ScriptedClient(FunctionInvocationLayer[Any], ChatMiddlewareLayer[Any], BaseChatClient[Any]):
    """
    Calls ``tool`` (with ``arguments``) ``calls`` times per run, one call per model turn, then answers ``done``. Keeps
    every request's messages, so a test can read what the model was sent.
    """

    def __init__(self, tool: str = "run_tests", calls: int = 2, arguments: dict[str, Any] | None = None) -> None:
        super().__init__()
        self.tool = tool
        self.calls = calls
        self.arguments = arguments or {}
        self.requests: list[list[Message]] = []
        self._counter = 0

    def blocks(self) -> list[str]:
        """The Historical Reference blocks of every request, in order."""
        return [
            message.text
            for request in self.requests
            for message in request
            if message.additional_properties.get(HISTORICAL_REFERENCE_KEY) is True
        ]

    def first_requests(self) -> list[list[Message]]:
        """The first request of each run (the one with no tool result yet)."""
        return [
            request
            for request in self.requests
            if not any(content.type == "function_result" for message in request for content in message.contents)
        ]

    def _inner_get_response(self, *, messages: Sequence[Message], stream: bool, options: Any, **_: Any) -> Any:
        self.requests.append(list(messages))
        last_user = max((index for index, message in enumerate(messages) if message.role == "user"), default=-1)
        made = sum(
            1
            for message in messages[last_user + 1 :]
            for content in message.contents
            if content.type == "function_call"
        )
        if made < self.calls:
            self._counter += 1
            reply = Message(
                role="assistant",
                contents=[
                    Content.from_function_call(
                        call_id=f"call-{self._counter}", name=self.tool, arguments=dict(self.arguments)
                    )
                ],
            )
        else:
            reply = Message(role="assistant", contents=["done"])
        if stream:

            async def updates() -> Any:
                yield ChatResponseUpdate(role="assistant", contents=list(reply.contents))

            return self._build_response_stream(updates())

        async def respond() -> ChatResponse:
            return ChatResponse(messages=[reply])

        return respond()


def delivered(sink: Any) -> Any:
    """An ``on_injected`` that passes ``sink`` only what an invocation delivered or withheld: not the
    ``NothingToInject`` reports (contract 1.10, as .NET reports them), nor the ``Skipped``, ``Failed`` and
    ``RetrievalFailed`` ones (contract 1.13), which the tests that count injections ignore."""

    def report(result: Any) -> None:
        if result.outcome not in ("NothingToInject", "Skipped", "Failed", "RetrievalFailed"):
            sink(result)

    return report
