"""
The optional model-backed reflector (contract 1.14): .NET's ``ChatClientExperienceReflector`` over an agent_framework
chat client. It asks the model, through structured output, for a reflection's free text -- the lesson, the four lists
and the reuse guidance -- and copies every bound field from the request. The deterministic reflector stays the default:
nothing uses this one unless the host passes it (``use_agent_experience(..., reflector=...)``, or ``reflector=`` to
:func:`agentexperience.finalize_async`). Needs the ``maf`` extra.

**Privacy.** It sends sanitized captured run content to whatever provider the chat client talks to (see
:mod:`agentexperience.reflection_prompt`). Whether that may leave the process is the host's decision.

**No tools.** Every call offers none (no ``tools``, ``tool_choice`` ``none``, re-asserted after the host's callback),
and a response carrying a function call or result fails the reflection (``ToolCallAttempted``). An agent_framework
client's function invocation runs only the tools a request offers (its ``additional_tools`` are declaration-only), so
unlike .NET's ``FunctionInvokingChatClient`` a client with function invocation is not refused.

**Failure.** Anything short of a readable answer with a lesson raises :class:`ReflectionFailed` with a ``kind``; its
message carries no model text. Finalization then stores the record ``Quarantined``. A call still running at
``timeout`` is ``TimedOut``, also on a client that ignores cancellation (the call is abandoned, its outcome
observed); the caller's own cancellation propagates.

**Not deterministic**, and it emits no telemetry of its own; no prompt or answer text reaches the package's telemetry.
The host's own chat client middleware is another matter.
"""

from __future__ import annotations

import asyncio
import copy
import inspect
from collections.abc import Sequence
from typing import Any

from agent_framework import Message

from ._text import is_null_or_whitespace
from .observer import type_name
from .records import Reflection
from .reflection_prompt import (
    OUTPUT_SCHEMA,
    OUTPUT_SCHEMA_DESCRIPTION,
    OUTPUT_SCHEMA_NAME,
    SYSTEM_PROMPT,
    UNKNOWN_MODEL,
    ChatReflectorOptions,
    ReflectionFailed,
    assemble,
    build_prompt,
    producer_for,
    read_answer,
    reflected_run_content,
    usable_model_name,
    validate_request,
)
from .reflector import ReflectionRequest

__all__ = ["ChatClientExperienceReflector", "ChatReflectorOptions", "ReflectionFailed"]

_abandoned: set[asyncio.Future[Any]] = set()


def _observe(task: asyncio.Future[Any]) -> None:
    """Retrieves an abandoned call's outcome, so its exception is never reported as never retrieved."""
    _abandoned.discard(task)
    if not task.cancelled():
        task.exception()


class ChatClientExperienceReflector:
    """
    A model-backed reflector over ``chat_client``, an agent_framework chat client (anything with
    ``get_response(messages, *, options=...)``). ``options`` are copied when it is built; ``ValueError`` when
    ``max_quoted_length`` is not below ``max_prompt_length``.
    """

    def __init__(self, chat_client: Any, options: ChatReflectorOptions | None = None) -> None:
        if chat_client is None or not callable(getattr(chat_client, "get_response", None)):
            raise TypeError("chat_client must be an agent_framework chat client (it has get_response).")
        options = options or ChatReflectorOptions()
        if not isinstance(options, ChatReflectorOptions):
            raise TypeError("options must be ChatReflectorOptions.")
        options.validate_combination()
        self._client = chat_client
        self._options = copy.copy(options)

    @property
    def options(self) -> ChatReflectorOptions:
        return self._options

    def build_prompt(self, request: ReflectionRequest) -> str:
        """The data message this reflector would send for ``request``, so a host can review what leaves the process."""
        validate_request(request)
        return build_prompt(request, self._options.max_quoted_length, self._options.max_prompt_length)

    def reflected_run_content(self, request: ReflectionRequest) -> list[str]:
        """Exactly the captured text the data message quotes, unescaped and cut where its spans are cut, plus the
        environment metadata keys: what screening's content guard compares links with."""
        return reflected_run_content(request, self._options)

    def chat_options(self) -> dict[str, Any]:
        """A fresh options dict for one call: the reflector's settings, the host's callback, then no tools and the
        output cap re-asserted. ``ReflectionFailed`` (``ChatOptionsCallbackFailed``) when the callback raises."""
        options = self._options
        chat_options: dict[str, Any] = {
            "response_format": {
                "type": "json_schema",
                "json_schema": {
                    "name": OUTPUT_SCHEMA_NAME,
                    "description": OUTPUT_SCHEMA_DESCRIPTION,
                    "schema": copy.deepcopy(dict(OUTPUT_SCHEMA)),
                },
            },
            "max_tokens": options.max_output_tokens,
        }
        if options.model_name is not None:
            chat_options["model"] = options.model_name
        if options.temperature is not None:
            chat_options["temperature"] = float(options.temperature)
        if options.configure_chat_options is not None:
            try:
                options.configure_chat_options(chat_options)
            except Exception as error:
                raise ReflectionFailed("ChatOptionsCallbackFailed", type_name(error)) from None
        chat_options.pop("tools", None)
        chat_options["tool_choice"] = "none"
        chat_options["max_tokens"] = options.max_output_tokens
        return chat_options

    async def reflect(self, request: ReflectionRequest) -> Reflection:
        """The reflection for ``request``, or :class:`ReflectionFailed`; the caller's cancellation propagates."""
        validate_request(request)
        prompt = build_prompt(request, self._options.max_quoted_length, self._options.max_prompt_length)
        messages = [Message(role="system", contents=[SYSTEM_PROMPT]), Message(role="user", contents=[prompt])]
        chat_options = self.chat_options()
        response = await self._call(messages, chat_options)
        output = read_answer(_answer(response))
        if is_null_or_whitespace(output.lesson):
            raise ReflectionFailed("EmptyLesson")
        model = (
            usable_model_name(_response_model(response)) or usable_model_name(self._options.model_name) or UNKNOWN_MODEL
        )
        return assemble(request, output, producer_for(model))

    async def _call(self, messages: Sequence[Any], chat_options: dict[str, Any]) -> Any:
        try:
            pending = self._client.get_response(messages, options=chat_options)
            if not inspect.isawaitable(pending):
                raise TypeError("The chat client's get_response returned no awaitable.")
            task: asyncio.Future[Any] = asyncio.ensure_future(pending)
        except Exception as error:
            raise ReflectionFailed("ModelCallFailed", type_name(error)) from None
        try:
            done, _ = await asyncio.wait({task}, timeout=self._options.timeout_seconds)
        except asyncio.CancelledError:
            # The caller's own cancellation: the call is cancelled and abandoned, never quietly turned into a failure.
            self._abandon(task)
            raise
        if not done:
            self._abandon(task)
            raise ReflectionFailed("TimedOut")
        if task.cancelled():
            # The call cancelled itself while neither the caller nor the timeout had.
            raise ReflectionFailed("ModelCallFailed", type_name(asyncio.CancelledError()))
        fault = task.exception()
        if fault is not None:
            if isinstance(fault, ReflectionFailed):
                raise fault
            raise ReflectionFailed("ModelCallFailed", type_name(fault))
        response = task.result()
        if response is None:
            raise ReflectionFailed("Unparseable")
        return response

    @staticmethod
    def _abandon(task: asyncio.Future[Any]) -> None:
        task.cancel()
        _abandoned.add(task)
        task.add_done_callback(_observe)


def _role(message: Any) -> str:
    role = getattr(message, "role", None)
    return str(getattr(role, "value", role))


def _answer(response: Any) -> str:
    """The text of the response's assistant messages, in order. A function call or result anywhere is
    ``ToolCallAttempted``; reasoning content and other roles' text are ignored."""
    messages = list(getattr(response, "messages", None) or [])
    for message in messages:
        for content in getattr(message, "contents", None) or []:
            if getattr(content, "type", None) in ("function_call", "function_result"):
                raise ReflectionFailed("ToolCallAttempted")
    texts: list[str] = []
    for message in messages:
        if _role(message) != "assistant":
            continue
        for content in getattr(message, "contents", None) or []:
            if getattr(content, "type", None) == "text":
                texts.append(getattr(content, "text", None) or "")
    return "".join(texts)


def _response_model(response: Any) -> str | None:
    model = getattr(response, "model", None)
    if model is None:
        model = getattr(response, "model_id", None)
    return model if isinstance(model, str) else None
