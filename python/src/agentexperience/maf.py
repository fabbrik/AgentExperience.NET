"""
Microsoft Agent Framework for Python (``agent-framework-core``, the ``maf`` extra): capture, verify, store and inject
lessons, wired with one call.

.. code-block:: python

    agent = use_agent_experience(
        Agent(client=chat_client, name="triage-ticket", tools=[run_tests]),
        store=InMemoryExperienceStore(),
        identity=development_identity(),
        verify=tool_succeeded("run_tests"),
        classify_tool_result=exit_code("run_tests"),
    )

What it adds to the agent:

- **Capture**, as agent-run middleware plus function middleware. Each non-streaming invocation is one run of one
  attempt: every tool call with its arguments, its result, and its error (a tool that raised, recorded by the
  exception's type name only, or a returned call the classifier named an error class for). When the invocation ends,
  the ``verify`` callback decides from the host's own facts; a verification is finalized through the core (the
  deterministic lesson, the derived IDs) and stored. Without ``verify`` nothing is stored. A streaming invocation is
  injected but not captured.
- **Injection**, as a context provider. It derives the task text from the invocation's messages (the contract's rule),
  searches the store in the identity's scope, ranks with the contract's weights, renders the Historical Reference block
  with the block options, and adds it as one user-role message (``message_role="system"`` sends it as a system
  message), stamped ``additional_properties["AgentExperience.HistoricalReference"] = True``.
- **Injection tracking** (contract 1.4). Once the block is handed over, a store that records injections (the PostgreSQL
  store, the in-memory store) is told which records it delivered, in a background task bounded by
  ``injection_record_timeout``, so the .NET lessons view shows when each lesson was last injected. It never delays or
  fails the invocation; the outcome is reported through ``on_injection_recorded``.

Nothing here raises into the agent: a store, identity, classifier or verifier failure means no block, or no stored
record, and is reported through ``on_injection_failure`` or ``on_capture_failure``. Only the invocation's own
cancellation and the tool's own exceptions propagate, unchanged.
"""

from __future__ import annotations

import asyncio
import dataclasses
import functools
import json
import threading
import uuid
from collections.abc import Awaitable, Callable, Mapping, Sequence
from contextvars import ContextVar
from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Any, Literal, TypeVar

from agent_framework import (
    AgentContext,
    AgentMiddleware,
    ContextProvider,
    FunctionInvocationContext,
    FunctionMiddleware,
    HistoryProvider,
    Message,
    MiddlewareTermination,
)

from . import __version__
from .block import BlockOptions, render_block
from .capture import RawAttempt, RawToolCall, RunCapture, RunExecutionStatus
from .classifiers import (
    TOOL_REPORTED_ERROR_CLASS,
    ToolResultClassifier,
    ToolResultContext,
    exit_code,
    http_status,
)
from .encoding import Duration, Instant
from .error_class import is_single_token
from .finalize import FinalizedExperience, finalize
from .identity import (
    DevelopmentOnlyError,
    ExperienceIdentity,
    development_identity,
    ensure_development_environment,
    is_development_environment,
    reduce_name,
)
from .inmemory import MAX_RECORD_INJECTION_IDS, ExperienceStore, InjectionRecorder, InMemoryExperienceStore
from .records import EnvironmentFingerprint, Provenance, RunExposure
from .sanitization import Sanitization
from .search import SearchParameters, rank
from .task_text import CHAT_HISTORY, EXTERNAL, derive_task_text
from .task_text import Message as TaskMessage
from .task_text import MessagePart as TaskMessagePart
from .verification import ClosedVerificationRound
from .verifiers import (
    UNVERSIONED_REVISION,
    Approval,
    Verification,
    VerificationContext,
    VerifyCallback,
    all_of,
    any_of,
    approval,
    check,
    resolve,
    tool_succeeded,
)

HISTORICAL_REFERENCE_KEY = "AgentExperience.HistoricalReference"
"""The ``additional_properties`` key that stamps the injected message."""

SOURCE_ID = "agentexperience"
"""The context provider's source ID, which MAF records as the injected message's attribution."""

PROVENANCE_SOURCE = "agentexperience.maf"
"""The provenance source every run captured here is recorded with."""

DEFAULT_TASK_ID = "default"
DEFAULT_FINALIZATION_TIMEOUT = 5.0
"""How long, in seconds, an invocation's end waits for finalization by default: .NET's ``FinalizationTimeout``."""

DEFAULT_INJECTION_RECORD_TIMEOUT = 5.0
"""How long, in seconds, recording one injection may take by default, all chunks together: .NET's."""

MAX_ABANDONED_INJECTION_RECORDS = 16
"""How many injection writes may run at once, abandoned ones (that outlived their timeout) included, before another is
not started and is reported ``NotRecorded``: .NET's ``ExperienceInjectionLimits.MaxAbandonedReads`` default. Each runs
on a daemon thread of its own, never the event loop's default executor, and nothing ever waits for one at exit."""


@dataclass
class _Write:
    """One injection write's state, shared with its thread under the provider's lock."""

    finished: bool = False
    abandoned: bool = False


CANCELLED_ERROR = "CancelledError"
"""The error recorded for an invocation or a tool call that was cancelled."""

_abandoned: set[asyncio.Future[None]] = set()
"""Finalizations that outlived their timeout, held until they finish so they are not collected mid-flight."""
"""The task ID runs are captured under when none is given and the agent's name keeps nothing."""

CaptureStage = Literal[
    "ResolveIdentity",
    "StartRun",
    "RecordExposure",
    "ToolCall",
    "ToolResultClassification",
    "AppendAttempt",
    "CompleteRun",
    "Verify",
    "Finalize",
    "Store",
]

AgentT = TypeVar("AgentT")


@dataclass(frozen=True)
class CaptureFailure:
    """A capture problem, reported once per stage per run. ``reason`` never quotes record content; ``exception``
    carries the exception object, when there was one."""

    stage: CaptureStage
    reason: str
    run_id: uuid.UUID | None = None
    exception: BaseException | None = None


@dataclass(frozen=True)
class InjectionFailure:
    """Why an invocation got no block: the reason (never record content), and the exception, when there was one."""

    reason: str
    exception: BaseException | None = None


@dataclass(frozen=True)
class InjectionResult:
    """What one injection delivered: the records in the block, best first, and the block's size in UTF-8 bytes."""

    experience_ids: tuple[uuid.UUID, ...]
    byte_count: int
    truncated: bool


InjectionRecordOutcome = Literal["Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown"]
"""How recording an injection ended, as .NET reports its ``record_injection`` operation: ``Recorded``; ``NotTracked``
(the store does not record injections); ``NotRecorded`` (the store refused or failed, a missing grant included); or
``OutcomeUnknown`` (it ran out of time or was cancelled, and may still commit)."""


@dataclass(frozen=True)
class InjectionRecordResult:
    """How recording one injected block ended: the outcome, the records the block delivered, and the exception, when
    there was one (never record content)."""

    outcome: InjectionRecordOutcome
    experience_ids: tuple[uuid.UUID, ...]
    exception: BaseException | None = None


@dataclass(frozen=True)
class IdentityContext:
    """What an identity resolver is given: the agent and the caller's session. Never model output."""

    agent: Any
    session: Any


IdentityResolver = Callable[[IdentityContext], "ExperienceIdentity | Awaitable[ExperienceIdentity | None] | None"]


def default_environment() -> EnvironmentFingerprint:
    """The environment a run is recorded under by default: this host, this Python, this operating system."""
    import platform

    return EnvironmentFingerprint(
        host_name=platform.node() or "unknown",
        runtime_version=f"{platform.python_implementation()} {platform.python_version()}",
        operating_system=platform.platform(),
        application_version=None,
        metadata={"ProcessArchitecture": platform.machine() or "unknown"},
    )


@dataclass
class ExperienceSettings:
    """
    The configuration capture and injection share. Built by :func:`use_agent_experience`; build one directly only to
    wire :class:`ExperienceContextProvider` and the middleware by hand.
    """

    store: ExperienceStore
    identity: ExperienceIdentity | IdentityResolver
    verify: VerifyCallback | None = None
    classify_tool_result: ToolResultClassifier | None = None
    task_id: str | None = None
    sanitization: Sanitization = field(default_factory=Sanitization.default)
    block_options: BlockOptions = field(default_factory=BlockOptions)
    environment: EnvironmentFingerprint = field(default_factory=default_environment)
    preferred_environment: Mapping[str, str] = field(default_factory=dict)
    exclude_model_authored: bool = False
    message_role: Literal["user", "system"] = "user"
    on_capture_failure: Callable[[CaptureFailure], None] | None = None
    on_injection_failure: Callable[[InjectionFailure], None] | None = None
    on_injected: Callable[[InjectionResult], None] | None = None
    on_finalized: Callable[[FinalizedExperience], None] | None = None
    on_injection_recorded: Callable[[InjectionRecordResult], None] | None = None
    clock: Callable[[], Instant] = Instant.now
    new_id: Callable[[], uuid.UUID] = uuid.uuid4
    finalization_timeout: float = DEFAULT_FINALIZATION_TIMEOUT
    """Seconds an invocation waits at its end for finalization (append, verify, finalize, store), as .NET's
    ``FinalizationTimeout``; past it the work is abandoned and reported, and the invocation returns."""
    injection_record_timeout: float = DEFAULT_INJECTION_RECORD_TIMEOUT
    """Seconds recording one injected block may take in the background, all chunks together; past it the write is
    abandoned and reported ``OutcomeUnknown``. The invocation never waits for it."""

    def __post_init__(self) -> None:
        if self.store is None:
            raise ValueError("A store is required: InMemoryExperienceStore() for development, or the PostgreSQL store.")
        if self.identity is None:
            raise ValueError(
                "An identity is required: development_identity() for development and tests, or a resolver from your "
                "own authentication."
            )
        if isinstance(self.identity, ExperienceIdentity):
            if self.identity.development:
                ensure_development_environment(self.identity.environment_name)
        elif not callable(self.identity):
            raise TypeError("identity must be an ExperienceIdentity or a function returning one (or None).")
        for name in (
            "verify",
            "classify_tool_result",
            "on_capture_failure",
            "on_injection_failure",
            "on_injected",
            "on_finalized",
            "on_injection_recorded",
            "clock",
            "new_id",
        ):
            value = getattr(self, name)
            if value is not None and not callable(value):
                raise TypeError(f"{name} must be callable when given.")
        if not self.finalization_timeout > 0:
            raise ValueError("finalization_timeout must be a positive number of seconds.")
        if not self.injection_record_timeout > 0:
            raise ValueError("injection_record_timeout must be a positive number of seconds.")
        if self.message_role not in ("user", "system"):
            raise ValueError("message_role must be 'user' or 'system'.")
        if self.task_id is not None and not self.task_id.strip():
            raise ValueError("task_id must not be blank; pass None to use the agent's name.")

    async def resolve_identity(self, agent: Any, session: Any) -> ExperienceIdentity | None:
        if isinstance(self.identity, ExperienceIdentity):
            return self.identity
        resolved = await resolve(self.identity(IdentityContext(agent, session)))
        if resolved is not None and not isinstance(resolved, ExperienceIdentity):
            raise TypeError("The identity resolver must return an ExperienceIdentity or None.")
        if resolved is not None and resolved.development:
            # Checked per invocation for a resolver, as .NET checks the development identity it runs: refused (and
            # reported) outside a development or test environment.
            ensure_development_environment(resolved.environment_name)
        return resolved

    def task_id_for(self, agent: Any) -> str:
        return self.task_id or reduce_name(getattr(agent, "name", None)) or DEFAULT_TASK_ID

    def report_injection(self, failure: InjectionFailure) -> None:
        if self.on_injection_failure is None:
            return
        try:
            self.on_injection_failure(failure)
        except Exception:
            pass

    def report_injection_recorded(self, result: InjectionRecordResult) -> None:
        if self.on_injection_recorded is None:
            return
        try:
            self.on_injection_recorded(result)
        except Exception:
            pass


# ---- The per-invocation capture scope ----------------------------------------------------------------------------


@dataclass
class _PendingCall:
    order: int
    call: RawToolCall


class _CaptureScope:
    """One invocation's capture: its identity, its run, and the tool calls observed. Never raises."""

    def __init__(self, settings: ExperienceSettings, agent: Any, identity: ExperienceIdentity | None) -> None:
        self.settings = settings
        self.agent = agent
        self.identity = identity
        self.capture: RunCapture | None = None
        self.started_at: Instant | None = None
        self._calls: list[_PendingCall] = []
        self._next_order = 0
        self._lock = threading.Lock()
        self._reported: set[str] = set()
        self._finalized = False

    @property
    def run_id(self) -> uuid.UUID | None:
        return self.capture.run.run_id if self.capture is not None else None

    def report(self, stage: CaptureStage, reason: str, exception: BaseException | None = None) -> None:
        with self._lock:
            if stage in self._reported:
                return
            self._reported.add(stage)
        if self.settings.on_capture_failure is None:
            return
        try:
            self.settings.on_capture_failure(CaptureFailure(stage, reason, self.run_id, exception))
        except Exception:
            pass

    def start(self, messages: Sequence[Any]) -> None:
        identity = self.identity
        if identity is None:
            return
        settings = self.settings
        try:
            now = settings.clock()
            task_text = derive_task_text(_task_messages(messages, ()))
            self.capture = RunCapture.start(
                run_id=settings.new_id(),
                task_id=settings.task_id_for(self.agent),
                task_description=task_text,
                scope=identity.scope,
                environment=settings.environment,
                provenance=Provenance(PROVENANCE_SOURCE, __version__, now, None),
                started_at=now,
                sanitization=settings.sanitization,
            )
            self.started_at = now
        except Exception as ex:
            self.capture = None
            self.report("StartRun", f"Starting the run raised {_type_name(ex)}; the invocation runs uncaptured.", ex)

    def begin_call(self) -> int:
        with self._lock:
            order = self._next_order
            self._next_order += 1
            return order

    def add_call(self, order: int, call: RawToolCall) -> None:
        with self._lock:
            if self._finalized:
                return
            self._calls.append(_PendingCall(order, call))

    def record_exposures(self, exposures: Sequence[RunExposure]) -> None:
        if self.capture is None or not exposures:
            return
        try:
            outcome = self.capture.record_exposure(exposures)
        except Exception as ex:
            self.report("RecordExposure", f"Recording the delivered records raised {_type_name(ex)}.", ex)
            return
        if outcome not in ("Recorded", "DuplicateNoOp"):
            self.report("RecordExposure", f"Capture answered {outcome} for the delivered records.")

    async def finish(self, status: RunExecutionStatus, result: str | None, error: str | None) -> None:
        """
        Finalizes the run, bounded by the finalization timeout on its own clock, never the invocation's: past it the
        work is abandoned (it may still complete) and reported. Never raises, except the caller's own cancellation
        arriving while it waits.
        """
        work = asyncio.ensure_future(self._finish(status, result, error))
        try:
            await asyncio.wait_for(asyncio.shield(work), self.settings.finalization_timeout)
        except asyncio.TimeoutError as ex:
            _abandoned.add(work)
            work.add_done_callback(_abandoned.discard)
            self.report(
                "Finalize", f"Finalization did not finish within {self.settings.finalization_timeout} seconds.", ex
            )
        except asyncio.CancelledError:
            _abandoned.add(work)
            work.add_done_callback(_abandoned.discard)
            raise
        except Exception as ex:
            self.report("Finalize", f"Finalization raised {_type_name(ex)}.", ex)

    async def _finish(self, status: RunExecutionStatus, result: str | None, error: str | None) -> None:
        """Appends this invocation's attempt, completes the run, and verifies, finalizes and stores it."""
        with self._lock:
            if self._finalized:
                return
            self._finalized = True
            calls = [pending.call for pending in sorted(self._calls, key=lambda pending: pending.order)]
        capture, identity, settings = self.capture, self.identity, self.settings
        if capture is None or identity is None or self.started_at is None:
            return
        try:
            ended = settings.clock()
            appended = capture.append_attempt(
                RawAttempt(settings.new_id(), self.started_at, _elapsed(self.started_at, ended), calls, result, error)
            )
            if appended not in ("Recorded", "DuplicateNoOp"):
                self.report("AppendAttempt", f"Capture answered {appended}; the run is not stored.")
                return
        except Exception as ex:
            self.report("AppendAttempt", f"Appending the attempt raised {_type_name(ex)}; the run is not stored.", ex)
            return
        try:
            run = capture.complete(status, ended)
        except Exception as ex:
            self.report("CompleteRun", f"Completing the run raised {_type_name(ex)}; the run is not stored.", ex)
            return

        # As in .NET, a run whose invocation failed or was cancelled is verified too: verify decides whether it is
        # stored (a failed check stores it Quarantined).
        if settings.verify is None:
            return
        try:
            round_id = settings.new_id()
            context = VerificationContext(run, round_id, identity, settings.clock, settings.new_id)
            verification = await resolve(settings.verify(context))
        except Exception as ex:
            self.report("Verify", f"verify raised {_type_name(ex)}; the run is not stored.", ex)
            return
        if verification is None:
            return
        if not isinstance(verification, Verification):
            self.report("Verify", "verify returned something other than a Verification or None; the run is not stored.")
            return
        for item in verification.evidence:
            if (
                item is None
                or item.verification_round_id != round_id
                or item.artifact_revision != verification.artifact_revision
            ):
                self.report(
                    "Verify",
                    "verify returned evidence that does not name this run's verification round and the returned "
                    "artifact revision; build it with context.create_evidence(...) and the same revision. The run is "
                    "not stored.",
                )
                return

        try:
            finalized = finalize(
                run,
                closed_round=ClosedVerificationRound(round_id, verification.artifact_revision),
                required_checks=verification.required_checks,
                evidence=verification.evidence,
                current_artifact_revision=verification.artifact_revision,
                finalized_at=settings.clock(),
            )
        except Exception as ex:
            self.report("Finalize", f"Finalizing the run raised {_type_name(ex)}; the run is not stored.", ex)
            return

        try:
            await asyncio.to_thread(
                settings.store.insert_finalized, identity.authorization, finalized, actor=identity.principal
            )
        except Exception as ex:
            self.report("Store", f"Storing the record raised {_type_name(ex)}; the run is not stored.", ex)
            return

        if settings.on_finalized is not None:
            try:
                settings.on_finalized(finalized)
            except Exception:
                pass


_current_scope: ContextVar[_CaptureScope | None] = ContextVar("agentexperience_capture_scope", default=None)


def _type_name(exception: BaseException) -> str:
    """An exception's full type name, as a captured error records it: never its message, which may hold model text."""
    kind = type(exception)
    module = kind.__module__
    return kind.__qualname__ if module in ("builtins", "__main__") else f"{module}.{kind.__qualname__}"


def _elapsed(start: Instant, end: Instant) -> Duration:
    elapsed = end - start
    return elapsed if elapsed.ticks > 0 else Duration(0)


def _jsonable(value: Any) -> Any:
    """A tool argument as JSON values (a value JSON cannot hold becomes its text)."""
    try:
        return json.loads(json.dumps(value, default=str, allow_nan=False))
    except (TypeError, ValueError):
        return str(value)


def _arguments(arguments: Any) -> dict[str, Any]:
    if arguments is None:
        return {}
    if callable(getattr(arguments, "model_dump", None)):
        arguments = arguments.model_dump()
    elif dataclasses.is_dataclass(arguments) and not isinstance(arguments, type):
        arguments = dataclasses.asdict(arguments)
    if not isinstance(arguments, Mapping):
        return {}
    return {str(key): value for key, value in arguments.items()}


def _content_text(item: Any) -> str | None:
    text = getattr(item, "text", None)
    return text if getattr(item, "type", None) == "text" and isinstance(text, str) else None


def _result_text(result: Any) -> str | None:
    """A tool's result as the text capture records: the text of its text contents, or the value as JSON text."""
    if result is None:
        return None
    if isinstance(result, str):
        return result
    if isinstance(result, (list, tuple)) and result and all(hasattr(item, "type") for item in result):
        texts = [text for item in result if (text := _content_text(item)) is not None]
        return "\n".join(texts) if texts else None
    if (text := _content_text(result)) is not None:
        return text
    try:
        return json.dumps(result, default=str)
    except (TypeError, ValueError):
        return str(result)


def _role(message: Any) -> str:
    role = getattr(message, "role", "")
    return str(getattr(role, "value", role))


def _task_message(message: Any, source: str | None) -> TaskMessage:
    properties = getattr(message, "additional_properties", None) or {}
    parts = tuple(
        TaskMessagePart(str(getattr(content, "type", "")), getattr(content, "text", None))
        if getattr(content, "type", None) == "text"
        else TaskMessagePart(str(getattr(content, "type", "")))
        for content in (getattr(message, "contents", None) or ())
    )
    return TaskMessage(_role(message), parts, source, properties.get(HISTORICAL_REFERENCE_KEY) is True)


def _task_messages(inputs: Sequence[Any], context: Sequence[tuple[str, Sequence[Any]]]) -> list[TaskMessage]:
    """The invocation's messages for the task-text rule: earlier providers' messages (replayed history marked
    ``ChatHistory``), then the new input (``External``)."""
    messages = [_task_message(message, source) for source, batch in context for message in batch]
    messages.extend(_task_message(message, EXTERNAL) for message in inputs)
    return messages


# ---- Injection ---------------------------------------------------------------------------------------------------


class ExperienceContextProvider(ContextProvider):
    """
    Injects the lessons that apply to an invocation, before the model is called: the task text from the invocation's
    messages, the text search in the identity's scope, the contract's ranking, and the rendered block as one message.
    Never raises into the agent; a failure means no block and is reported through ``on_injection_failure``.

    Once a block is handed over, the records it delivered are recorded as injected (contract 1.4) in a background task
    on the running event loop, bounded by ``injection_record_timeout``; :meth:`wait_for_injection_records` awaits the
    ones still running, for a host shutting down or a test.
    """

    def __init__(self, settings: ExperienceSettings, source_id: str = SOURCE_ID) -> None:
        super().__init__(source_id)
        self.settings = settings
        self._recordings: set[asyncio.Task[None]] = set()
        self._abandoned_records = 0
        self._running_writes = 0
        self._abandoned_lock = threading.Lock()

    async def wait_for_injection_records(self) -> None:
        """Completes once every injection record this provider started on the running event loop has ended. Each is
        bounded by ``injection_record_timeout``, so this is too; it never raises."""
        loop = asyncio.get_running_loop()
        while pending := [task for task in self._recordings if task.get_loop() is loop and not task.done()]:
            await asyncio.gather(*pending, return_exceptions=True)

    @property
    def abandoned_injection_records(self) -> int:
        """How many injection writes that outlived their timeout are still running against the store."""
        with self._abandoned_lock:
            return self._abandoned_records

    def _start_recording(self, identity: ExperienceIdentity, ids: tuple[uuid.UUID, ...], injected_at: Instant) -> None:
        """Starts recording ``ids`` as injected at ``injected_at``, in the background; never raises, never waits."""
        settings = self.settings
        try:
            recorder = settings.store if isinstance(settings.store, InjectionRecorder) else None
            if recorder is None:
                settings.report_injection_recorded(InjectionRecordResult("NotTracked", ids))
                return
            task = asyncio.get_running_loop().create_task(self._record(recorder, identity, ids, injected_at))
        except Exception as ex:
            settings.report_injection_recorded(InjectionRecordResult("NotRecorded", ids, ex))
            return
        self._recordings.add(task)
        task.add_done_callback(self._recordings.discard)

    async def _record(
        self,
        recorder: InjectionRecorder,
        identity: ExperienceIdentity,
        ids: tuple[uuid.UUID, ...],
        injected_at: Instant,
    ) -> None:
        """
        The background record: the IDs in chunks of at most 200, each a call on a worker thread of the provider's own,
        all within ``injection_record_timeout``. A call that overruns it is abandoned (a thread cannot be stopped): it
        is counted until it ends. At most :data:`MAX_ABANDONED_INJECTION_RECORDS` writes run at once, so writes that
        hang can never take more threads than that, and never the event loop's default executor, which the search and
        capture use. Never raises, except the task's own cancellation, which is reported first.
        """
        settings = self.settings
        outcome: InjectionRecordOutcome = "Recorded"
        error: BaseException | None = None
        loop = asyncio.get_running_loop()
        deadline = loop.time() + settings.injection_record_timeout
        try:
            for start in range(0, len(ids), MAX_RECORD_INJECTION_IDS):
                left = deadline - loop.time()
                if left <= 0:
                    raise TimeoutError("The injection record ran out of time.")
                chunk = list(ids[start : start + MAX_RECORD_INJECTION_IDS])
                write = self._start_write(
                    loop,
                    functools.partial(
                        recorder.record_injection, identity.authorization, identity.scope, chunk, injected_at
                    ),
                )
                if write is None:
                    outcome = "NotRecorded"
                    error = RuntimeError("Too many earlier injection records are still running against the store.")
                    break
                future, state = write
                try:
                    await asyncio.wait_for(asyncio.shield(future), left)
                except (asyncio.TimeoutError, asyncio.CancelledError):
                    self._abandon(state)
                    raise
        except (asyncio.TimeoutError, TimeoutError) as ex:
            outcome, error = "OutcomeUnknown", ex
        except asyncio.CancelledError as ex:
            settings.report_injection_recorded(InjectionRecordResult("OutcomeUnknown", ids, ex))
            raise
        except Exception as ex:
            outcome, error = "NotRecorded", ex
        settings.report_injection_recorded(InjectionRecordResult(outcome, ids, error))

    def _start_write(
        self, loop: asyncio.AbstractEventLoop, write: Callable[[], None]
    ) -> tuple[asyncio.Future[None], _Write] | None:
        """
        Runs ``write`` on a daemon thread of its own, or answers ``None`` when :data:`MAX_ABANDONED_INJECTION_RECORDS`
        writes are already running. The thread settles the returned future on ``loop`` (if it is still open) and keeps
        its own count, so a write that outlives the loop, or the interpreter's shutdown, is never waited for.
        """
        with self._abandoned_lock:
            if self._running_writes >= MAX_ABANDONED_INJECTION_RECORDS:
                return None
            self._running_writes += 1
        state = _Write()
        future: asyncio.Future[None] = loop.create_future()

        def settle(failure: BaseException | None) -> None:
            if future.done():
                return
            if failure is None:
                future.set_result(None)
            else:
                future.set_exception(failure)

        def run() -> None:
            failure: BaseException | None = None
            try:
                write()
            except BaseException as ex:  # reported, never raised in a thread
                failure = ex
            finally:
                with self._abandoned_lock:
                    self._running_writes -= 1
                    state.finished = True
                    if state.abandoned:
                        self._abandoned_records -= 1
            try:
                loop.call_soon_threadsafe(settle, failure)
            except RuntimeError:
                pass  # the loop has closed: nobody awaits this write any more

        threading.Thread(target=run, name="agentexperience-injection-record", daemon=True).start()
        future.add_done_callback(lambda done: None if done.cancelled() else done.exception())
        return future, state

    def _abandon(self, state: _Write) -> None:
        """Counts a write that outlived its bound until its thread ends."""
        with self._abandoned_lock:
            if not state.finished and not state.abandoned:
                state.abandoned = True
                self._abandoned_records += 1

    async def before_run(
        self,
        *,
        agent: Any,
        session: Any,
        context: Any,
        state: dict[str, Any],
    ) -> None:
        settings = self.settings
        scope = _current_scope.get()
        captured = scope is not None and scope.agent is agent
        try:
            if captured and scope is not None and scope.settings is settings:
                # Capture resolved the identity for this invocation already: reuse it, never resolve twice.
                identity = scope.identity
            else:
                identity = await settings.resolve_identity(agent, session)
        except Exception as ex:
            settings.report_injection(InjectionFailure(f"Resolving the identity raised {_type_name(ex)}.", ex))
            return
        if identity is None:
            return

        try:
            history = {
                provider.source_id
                for provider in getattr(agent, "context_providers", ())
                if isinstance(provider, HistoryProvider)
            }
            earlier = [
                (CHAT_HISTORY if source in history else source, batch)
                for source, batch in context.context_messages.items()
            ]
            task_text = derive_task_text(_task_messages(context.input_messages, earlier))
            if task_text is None:
                return
            parameters = SearchParameters(
                identity.scope, task_text, exclude_model_authored=settings.exclude_model_authored
            )
            rows = await asyncio.to_thread(settings.store.search, identity.authorization, parameters)
            # The instant the block is built at: ranking's "now", and the injection's instant.
            now = settings.clock()
            ranked = rank(
                rows,
                identity.scope,
                now,
                settings.preferred_environment,
                exclude_model_authored=settings.exclude_model_authored,
            )
            block = render_block(ranked.ranked, settings.block_options)
            if block.is_empty:
                return
            message = Message(
                role=settings.message_role,
                contents=[block.text],
                additional_properties={HISTORICAL_REFERENCE_KEY: True},
            )
            context.extend_messages(self, [message])
        except Exception as ex:
            settings.report_injection(InjectionFailure(f"Injection raised {_type_name(ex)}; no block was added.", ex))
            return

        # The records this block delivers, at the revision each was rendered from, on the run the invocation is
        # captured as. Recorded last, once nothing that could fail remains.
        if captured and scope is not None:
            scope.record_exposures([RunExposure(record.experience_id, record.revision) for record in block.included])
        delivered = tuple(record.experience_id for record in block.included)
        if settings.on_injected is not None:
            try:
                settings.on_injected(InjectionResult(delivered, block.byte_count, ranked.truncated))
            except Exception:
                pass

        # When each delivered record was injected, for the .NET lessons view: started last, in the background, so the
        # block is handed over and the model called without waiting for the store.
        if delivered:
            self._start_recording(identity, delivered, now)


# ---- Capture -----------------------------------------------------------------------------------------------------


class ExperienceCaptureMiddleware(AgentMiddleware):
    """
    Captures each non-streaming invocation of the agent as one run: resolves the identity once (injection reuses it),
    opens the run, and when the invocation ends appends its attempt, verifies, finalizes and stores it.
    """

    def __init__(self, settings: ExperienceSettings) -> None:
        self.settings = settings

    async def process(self, context: AgentContext, call_next: Callable[[], Awaitable[None]]) -> None:
        if context.stream:
            # A streaming invocation completes when its consumer finishes reading, outside this call: not captured.
            await call_next()
            return

        settings = self.settings
        identity: ExperienceIdentity | None = None
        failure: tuple[str, BaseException] | None = None
        try:
            identity = await settings.resolve_identity(context.agent, context.session)
        except Exception as ex:
            failure = (f"Resolving the identity raised {_type_name(ex)}; the invocation runs uncaptured.", ex)

        scope = _CaptureScope(settings, context.agent, identity)
        if failure is not None:
            scope.report("ResolveIdentity", failure[0], failure[1])
        scope.start(context.messages)

        token = _current_scope.set(scope)
        try:
            try:
                await call_next()
            except asyncio.CancelledError:
                # As .NET's StatusOf: the caller's cancellation ends the run Cancelled; finish is bounded by its own
                # timeout, so the cancellation is never held up longer than that.
                await scope.finish("Cancelled", None, CANCELLED_ERROR)
                raise
            except MiddlewareTermination as ex:
                # A middleware ended the run early on purpose, with the result it set: the run completed.
                await scope.finish("Completed", _response_text(context.result) or _response_text(ex.result), None)
                raise
            except Exception as ex:
                await scope.finish("Failed", None, _type_name(ex))
                raise
            await scope.finish("Completed", _response_text(context.result), None)
        finally:
            _current_scope.reset(token)


def _response_text(result: Any) -> str | None:
    text = result if isinstance(result, str) else getattr(result, "text", None)
    return text if isinstance(text, str) and text else None


class ExperienceToolMiddleware(FunctionMiddleware):
    """
    Records each tool call of a captured invocation: its arguments, its result, and its error (the type name of an
    exception it raised, or the class the tool-result classifier named for a call that returned).
    """

    def __init__(self, settings: ExperienceSettings) -> None:
        self.settings = settings

    async def process(self, context: FunctionInvocationContext, call_next: Callable[[], Awaitable[None]]) -> None:
        scope = _current_scope.get()
        if scope is None or scope.settings is not self.settings or scope.capture is None:
            await call_next()
            return

        settings = self.settings
        try:
            order = scope.begin_call()
            call_id = settings.new_id()
            name = str(context.function.name)
            raw_arguments = _arguments(context.arguments)
            started = settings.clock()
        except Exception as ex:
            scope.report("ToolCall", f"Capturing a tool call's start raised {_type_name(ex)}; it is not recorded.", ex)
            await call_next()
            return

        try:
            await call_next()
        except MiddlewareTermination as ex:
            # A middleware stopped the function loop at this call, with the result it set: recorded as returned.
            substituted = context.result if context.result is not None else ex.result
            self._record(scope, order, call_id, name, raw_arguments, started, substituted, None)
            scope.report(
                "ToolCall",
                "A middleware terminated the function loop at a tool call; the call is recorded as returned with the "
                "result the middleware set.",
            )
            raise
        except asyncio.CancelledError:
            self._record(scope, order, call_id, name, raw_arguments, started, None, CANCELLED_ERROR)
            raise
        except Exception as ex:
            self._record(scope, order, call_id, name, raw_arguments, started, None, _type_name(ex))
            raise

        result = context.result
        error = self._classify(scope, call_id, name, raw_arguments, result)
        self._record(scope, order, call_id, name, raw_arguments, started, result, error)

    def _classify(
        self, scope: _CaptureScope, call_id: uuid.UUID, name: str, arguments: dict[str, Any], result: Any
    ) -> str | None:
        classify = self.settings.classify_tool_result
        if classify is None:
            return None
        try:
            named = classify(ToolResultContext(call_id, name, MappingProxyType(arguments), result))
        except Exception as ex:
            scope.report(
                "ToolResultClassification",
                f"classify_tool_result raised {_type_name(ex)}; the tool call is recorded as returned.",
                ex,
            )
            return None
        if named is None:
            return None
        # Surrounding white space is forgiven; case and form are not ("exit 3", never "Exit 3" or "exit code 3").
        named = named.strip() if isinstance(named, str) else ""
        if is_single_token(named):
            return named
        scope.report(
            "ToolResultClassification",
            "classify_tool_result returned a class that is not a single error-class token; the tool call is recorded "
            f"as failed with {TOOL_REPORTED_ERROR_CLASS}.",
        )
        return TOOL_REPORTED_ERROR_CLASS

    def _record(
        self,
        scope: _CaptureScope,
        order: int,
        call_id: uuid.UUID,
        name: str,
        arguments: dict[str, Any],
        started: Instant,
        result: Any,
        error: str | None,
    ) -> None:
        try:
            ended = self.settings.clock()
            call = RawToolCall(
                call_id,
                name,
                {key: _jsonable(value) for key, value in arguments.items()},
                started,
                _elapsed(started, ended),
                result=_result_text(result),
                error=error,
            )
            scope.add_call(order, call)
        except Exception as ex:
            scope.report("ToolCall", f"Recording a tool call raised {_type_name(ex)}; it is not recorded.", ex)


# ---- The one-call setup ------------------------------------------------------------------------------------------


def use_agent_experience(
    agent: AgentT,
    *,
    store: ExperienceStore,
    identity: ExperienceIdentity | IdentityResolver,
    verify: VerifyCallback | None = None,
    classify_tool_result: ToolResultClassifier | None = None,
    task_id: str | None = None,
    sanitization: Sanitization | None = None,
    block_options: BlockOptions | None = None,
    environment: EnvironmentFingerprint | None = None,
    preferred_environment: Mapping[str, str] | None = None,
    exclude_model_authored: bool = False,
    message_role: Literal["user", "system"] = "user",
    inject: bool = True,
    on_capture_failure: Callable[[CaptureFailure], None] | None = None,
    on_injection_failure: Callable[[InjectionFailure], None] | None = None,
    on_injected: Callable[[InjectionResult], None] | None = None,
    on_finalized: Callable[[FinalizedExperience], None] | None = None,
    on_injection_recorded: Callable[[InjectionRecordResult], None] | None = None,
    clock: Callable[[], Instant] | None = None,
    new_id: Callable[[], uuid.UUID] | None = None,
    finalization_timeout: float = DEFAULT_FINALIZATION_TIMEOUT,
    injection_record_timeout: float = DEFAULT_INJECTION_RECORD_TIMEOUT,
) -> AgentT:
    """
    Wires capture and injection into a MAF agent (an ``Agent``, or any agent with ``context_providers`` and
    ``middleware``) and returns it.

    ``identity`` is who every invocation runs for: :func:`~agentexperience.identity.development_identity` for
    development and tests, or a function of an :class:`IdentityContext` returning the identity from your own
    authentication (``None`` leaves the invocation neither captured nor injected). ``verify`` decides from your own
    facts whether a run is stored (see :mod:`agentexperience.verifiers`); without it nothing is stored.
    ``classify_tool_result`` marks a returned tool call as failed (see :mod:`agentexperience.classifiers`).

    The agent keeps every provider and middleware it has. When it already holds an :class:`ExperienceContextProvider`
    (any one, deliberately by type) no second one is added, so it gets exactly one block; ``inject=False`` captures
    without injecting. Calling this twice on the same agent raises ``ValueError``.

    A store with ``record_injection`` (contract 1.4) is told, in the background, which records each block delivered;
    ``on_injection_recorded`` reports how that ended. On PostgreSQL the application role needs the grants of the
    contract's ``sql/application-role-privileges-1.4.sql`` (or the .NET privileges call); without them every injection
    is still made and reported ``NotRecorded``.
    """
    providers = getattr(agent, "context_providers", None)
    if not isinstance(providers, list):
        raise TypeError("The agent must be a MAF agent with a context_providers list (an Agent, for example).")
    middleware = getattr(agent, "middleware", None)
    if middleware is not None and not isinstance(middleware, list):
        raise TypeError("The agent's middleware must be a list.")
    if any(isinstance(item, ExperienceCaptureMiddleware) for item in middleware or ()):
        raise ValueError("use_agent_experience was already called on this agent.")

    settings = ExperienceSettings(
        store=store,
        identity=identity,
        verify=verify,
        classify_tool_result=classify_tool_result,
        task_id=task_id,
        sanitization=sanitization or Sanitization.default(),
        block_options=block_options or BlockOptions(),
        environment=environment or default_environment(),
        preferred_environment=dict(preferred_environment or {}),
        exclude_model_authored=exclude_model_authored,
        message_role=message_role,
        on_capture_failure=on_capture_failure,
        on_injection_failure=on_injection_failure,
        on_injected=on_injected,
        on_finalized=on_finalized,
        on_injection_recorded=on_injection_recorded,
        clock=clock or Instant.now,
        new_id=new_id or uuid.uuid4,
        finalization_timeout=finalization_timeout,
        injection_record_timeout=injection_record_timeout,
    )

    # Capture first, so it wraps everything else on the agent: injection runs inside it and reuses its identity.
    target: Any = agent
    target.middleware = [ExperienceCaptureMiddleware(settings), ExperienceToolMiddleware(settings), *(middleware or [])]
    if inject and not any(isinstance(provider, ExperienceContextProvider) for provider in providers):
        providers.append(ExperienceContextProvider(settings))
    return agent


__all__ = [
    "DEFAULT_INJECTION_RECORD_TIMEOUT",
    "HISTORICAL_REFERENCE_KEY",
    "MAX_ABANDONED_INJECTION_RECORDS",
    "PROVENANCE_SOURCE",
    "SOURCE_ID",
    "TOOL_REPORTED_ERROR_CLASS",
    "UNVERSIONED_REVISION",
    "Approval",
    "CaptureFailure",
    "DevelopmentOnlyError",
    "ExperienceCaptureMiddleware",
    "ExperienceContextProvider",
    "ExperienceIdentity",
    "ExperienceSettings",
    "ExperienceStore",
    "ExperienceToolMiddleware",
    "IdentityContext",
    "InMemoryExperienceStore",
    "InjectionFailure",
    "InjectionRecordOutcome",
    "InjectionRecordResult",
    "InjectionResult",
    "ToolResultContext",
    "Verification",
    "VerificationContext",
    "all_of",
    "any_of",
    "approval",
    "check",
    "default_environment",
    "development_identity",
    "exit_code",
    "http_status",
    "is_development_environment",
    "tool_succeeded",
    "use_agent_experience",
]
