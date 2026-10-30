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
- **Semantic retrieval** (contract 1.6), with ``embedder=``. Injection then runs the text and the vector channel
  together, merges them and ranks as before; every stored record's summary is embedded in the background. Without an
  embedder, or when the vector channel fails, it answers from text alone and reports why (``on_vector_fallback``).
- **Injection tracking** (contract 1.4). Once the block is handed over, a store that records injections (the PostgreSQL
  store, the in-memory store) is told which records it delivered, in a background task bounded by
  ``injection_record_timeout``, so the .NET lessons view shows when each lesson was last injected. It never delays or
  fails the invocation; the outcome is reported through ``on_injection_recorded``.
- **The lessons view** (contract 1.7), ``agent_experience_lessons(agent)``: a read-only list of what was learned in a
  scope, with each lesson's block, eligibility and last injection.
- **The injection holdout** (contract 1.8), with ``holdout=HoldoutOptions(rate=...)``: a deterministic, run-ID-derived
  share of captured runs that have a block has it withheld (``Withheld``), and both arms are recorded in the background,
  so the benefit estimate (``agentexperience.estimate_benefit``, or .NET's) counts this agent's runs. Off by default.
- **The run's model** (contract 1.9): capture records the model the agent's chat client reports under the environment
  metadata key ``agentexperience.model``, for the estimate's split by model; a key the host's environment sets wins.
- **The benefit policy** (contract 1.10), with ``benefit=BenefitPolicy(enabled=True)``: a lesson the holdout measured
  as hurting is not injected (``InjectionResult.omitted`` says ``MeasuredHarmful``), and with
  ``prefer_measured_helpful`` the helpful ones go first. The estimate is cached and refreshed in the background, never
  on the request path. Off by default.
- **Measuring another memory provider** (contract 1.11), ``use_measured_memory(agent, mem0, "mem0")``: the provider is
  wrapped in :class:`ExperienceHoldoutContextProvider`, which withholds what it adds on a deterministic share of
  captured runs and records each run's arm, so ``agentexperience.estimate_provider_benefit`` (or .NET's) measures it.

Nothing here raises into the agent: a store, identity, classifier or verifier failure means no block, or no stored
record, and is reported through ``on_injection_failure`` or ``on_capture_failure``. Only the invocation's own
cancellation and the tool's own exceptions propagate, unchanged.
"""

from __future__ import annotations

import asyncio
import contextvars
import copy
import dataclasses
import functools
import json
import threading
import uuid
from collections import Counter
from collections.abc import Awaitable, Callable, Mapping, Sequence
from contextvars import ContextVar
from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Any, Literal, TypeVar, cast

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
from .benefit_policy import BenefitCache, BenefitPolicy, OmittedRecord, select_block
from .block import BlockOptions
from .capture import RawAttempt, RawToolCall, RunCapture, RunExecutionStatus
from .classifiers import (
    TOOL_REPORTED_ERROR_CLASS,
    ToolResultClassifier,
    ToolResultContext,
    exit_code,
    http_status,
)
from .embedding import (
    Embedder,
    EmbeddingWrite,
    EmbeddingWriteResult,
    VectorFallback,
    VectorSearchResult,
    content_hash,
    embed_text,
    rank_hybrid,
    record_summary,
    run_on_vector_thread,
    validate_model_id,
    validate_vector,
    vector_query_for_policy,
)
from .encoding import Duration, Instant
from .error_class import is_single_token
from .finalize import FinalizedExperience, finalize
from .holdout import (
    MAX_HOLDOUT_RECORDS,
    HoldoutArm,
    HoldoutAssignment,
    HoldoutOptions,
    ProviderHoldoutAssignment,
    assign_holdout,
    check_provider_key,
    provider_salt,
)
from .identity import (
    DevelopmentOnlyError,
    ExperienceIdentity,
    development_identity,
    ensure_development_environment,
    is_development_environment,
    reduce_name,
)
from .inmemory import (
    MAX_RECORD_INJECTION_IDS,
    AuthorizationDenied,
    EmbeddingIndex,
    ExperienceStore,
    HoldoutRecorder,
    InjectionRecorder,
    InMemoryExperienceStore,
    LessonsReader,
    ProviderHoldoutRecorder,
)
from .lessons import ExperienceLessons
from .model import MODEL_METADATA_KEY, reported_model, with_model
from .observer import classify as _classify_error
from .observer import observe
from .records import EnvironmentFingerprint, Provenance, RunExposure, Scope
from .retrieval import ConfidenceDecayPolicy, RetrievalPolicy
from .sanitization import Sanitization
from .search import AuthorizationBounds, ExcludedRecord, SearchParameters, SearchRow, rank
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

DEFAULT_VECTOR_TIMEOUT = 0.5
"""How long, in seconds, the vector channel (embedding the task text, then searching the stored vectors) may take by
default; past it the injection answers from text alone."""

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
    "Index",
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
    """What one injection delivered: the records in the block, best first, and the block's size in UTF-8 bytes. With
    the holdout (contract 1.8) a held-out invocation is reported too: ``outcome`` ``Withheld``, nothing delivered
    (``experience_ids`` empty, ``byte_count`` 0), and the records it was denied in ``withheld_experience_ids``."""

    experience_ids: tuple[uuid.UUID, ...]
    byte_count: int
    truncated: bool
    vector_fallback: VectorFallback | None = None
    """Why the injection answered from text alone (contract 1.6), or ``None`` when the vector channel contributed."""
    outcome: Literal["Injected", "Withheld", "NothingToInject", "RetrievalTimedOut", "RetrievalDenied"] = "Injected"
    """``Injected``; ``Withheld`` when the holdout withheld the block (contract 1.8); or, as .NET reports it,
    ``NothingToInject`` when there was no task text or every ranked record was omitted (``omitted`` says why: a scope
    measured ``Hurts`` omits them all as ``MeasuredHarmful``); ``RetrievalTimedOut`` when retrieval ran past the
    retrieval policy's timeout or was refused at its abandoned-search cap, and ``RetrievalDenied`` when the identity's
    bounds do not permit its scope (contract 1.12; nothing delivered)."""
    withheld_experience_ids: tuple[uuid.UUID, ...] = ()
    """The records a held-out invocation's block named and did not deliver; empty otherwise."""
    omitted: tuple[OmittedRecord, ...] = ()
    """Every ranked record that was not delivered, and why (contract 1.10): ``MeasuredHarmful`` (the benefit policy),
    ``ModelAuthored``, ``OverRecordLimit`` or ``OverByteBudget``, each with .NET's detail, in .NET's order."""
    excluded: tuple[ExcludedRecord, ...] = ()
    """The candidates retrieval's eligibility checks left out before ranking, and why (contract 1.12, .NET's
    ``ExperienceInjectionResult.Excluded``): ``IneligibleStatus``, ``ModelAuthored`` or ``Expired``."""


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
class HoldoutRecordResult:
    """How recording one run's holdout assignment ended (contract 1.8, the ``record_holdout`` operation): the outcome
    (as for an injection record), the assignment, and the exception, when there was one (never record content)."""

    outcome: InjectionRecordOutcome
    assignment: HoldoutAssignment
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


def _with_reported_model(environment: EnvironmentFingerprint, agent: Any) -> EnvironmentFingerprint:
    """``environment`` with the model ``agent``'s chat client reports under ``agentexperience.model`` (contract 1.9);
    unchanged when it reports none, or when the host's environment already names one."""
    model = reported_model(agent)
    if model is None or MODEL_METADATA_KEY in environment.metadata:
        return environment
    return dataclasses.replace(environment, metadata=with_model(environment.metadata, model))


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
    embedder: Embedder | None = None
    """Turns text into a vector, for hybrid retrieval and for embedding each stored record (contract 1.6)."""
    min_vector_relevance: float | None = None
    """When set (0 to 1), vector candidates whose relevance is below it are dropped; text candidates never are."""
    on_vector_fallback: Callable[[VectorFallback], None] | None = None
    retrieval_policy: RetrievalPolicy = field(default_factory=RetrievalPolicy)
    """The retrieval policy (contract 1.12): the timeout bounding a retrieval, the confidence floor, the expiry, the
    recency half-life, the candidate limit, the abandoned-search cap, the minimum of matched terms and the vector
    floor, with .NET's defaults."""
    confidence_decay: ConfidenceDecayPolicy | None = None
    """Decays each record's confidence at ranking time by its domain (contract 1.12); ``None``: no decay."""
    holdout: HoldoutOptions = field(default_factory=HoldoutOptions)
    """The injection holdout (contract 1.8): off by default (rate 0)."""
    benefit: BenefitPolicy = field(default_factory=BenefitPolicy)
    """Acting on the measured benefit (contract 1.10): off by default."""
    on_holdout_recorded: Callable[[HoldoutRecordResult], None] | None = None
    clock: Callable[[], Instant] = Instant.now
    new_id: Callable[[], uuid.UUID] = uuid.uuid4
    finalization_timeout: float = DEFAULT_FINALIZATION_TIMEOUT
    """Seconds an invocation waits at its end for finalization (append, verify, finalize, store), as .NET's
    ``FinalizationTimeout``; past it the work is abandoned and reported, and the invocation returns."""
    injection_record_timeout: float = DEFAULT_INJECTION_RECORD_TIMEOUT
    """Seconds recording one injected block may take in the background, all chunks together; past it the write is
    abandoned and reported ``OutcomeUnknown``. The invocation never waits for it."""
    vector_timeout: float = DEFAULT_VECTOR_TIMEOUT
    """Seconds the vector channel may take in all (embedding the task text and searching); past it the injection
    answers from text alone and reports ``ProviderUnavailable`` or ``VectorSearchFailed``. Only when strictly shorter
    than the retrieval policy's ``timeout``: otherwise the policy's timeout decides, and a vector channel still running
    then times the retrieval out (``RetrievalTimedOut``), as .NET's one bound on both channels does."""
    _index_tasks: set[asyncio.Task[None]] = field(default_factory=set, init=False, repr=False, compare=False)
    # Contract 1.11: the measured memory providers use_measured_memory wrapped on the agent, whose records
    # ExperienceContextProvider.wait_for_injection_records awaits too.
    _measured: list[Any] = field(default_factory=list, init=False, repr=False, compare=False)

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
            "on_vector_fallback",
            "on_holdout_recorded",
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
        if not self.vector_timeout > 0:
            raise ValueError("vector_timeout must be a positive number of seconds.")
        if self.min_vector_relevance is not None and not 0.0 <= self.min_vector_relevance <= 1.0:
            raise ValueError("min_vector_relevance must be between 0 and 1.")
        if not isinstance(self.retrieval_policy, RetrievalPolicy):
            raise TypeError("retrieval_policy must be a RetrievalPolicy.")
        if self.confidence_decay is not None and not isinstance(self.confidence_decay, ConfidenceDecayPolicy):
            raise TypeError("confidence_decay must be a ConfidenceDecayPolicy or None.")
        if self.embedder is not None and not (
            isinstance(getattr(self.embedder, "model_id", None), str)
            and hasattr(self.embedder, "dimension")
            and callable(getattr(self.embedder, "embed", None))
        ):
            raise TypeError("embedder must have model_id, dimension and an embed(text) method.")
        if not isinstance(self.holdout, HoldoutOptions):
            raise TypeError("holdout must be a HoldoutOptions.")
        if not isinstance(self.benefit, BenefitPolicy):
            raise TypeError("benefit must be a BenefitPolicy.")
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

    @property
    def vector_floor(self) -> float | None:
        """The vector channel's floor: ``min_vector_relevance`` when set, which replaces the retrieval policy's
        ``minimum_vector_relevance`` (as .NET's ``MinimumSemanticSimilarity`` does), else the policy's."""
        if self.min_vector_relevance is not None:
            return self.min_vector_relevance
        return self.retrieval_policy.minimum_vector_relevance

    def task_id_for(self, agent: Any) -> str:
        return self.task_id or reduce_name(getattr(agent, "name", None)) or DEFAULT_TASK_ID

    def report_injection(self, failure: InjectionFailure) -> None:
        if self.on_injection_failure is None:
            return
        try:
            self.on_injection_failure(failure)
        except Exception:
            pass

    def report_injected(self, result: InjectionResult) -> None:
        if self.on_injected is None:
            return
        try:
            self.on_injected(result)
        except Exception:
            pass

    def report_vector_fallback(self, fallback: VectorFallback) -> None:
        if self.on_vector_fallback is None:
            return
        try:
            self.on_vector_fallback(fallback)
        except Exception:
            pass

    async def wait_for_index_writes(self) -> None:
        """Completes once every background embedding write this configuration started on the running event loop has
        ended. Each is bounded by ``finalization_timeout``; never raises."""
        loop = asyncio.get_running_loop()
        while pending := [task for task in self._index_tasks if task.get_loop() is loop and not task.done()]:
            await asyncio.gather(*pending, return_exceptions=True)

    def report_injection_recorded(self, result: InjectionRecordResult) -> None:
        if self.on_injection_recorded is None:
            return
        try:
            self.on_injection_recorded(result)
        except Exception:
            pass

    def report_holdout_recorded(self, result: HoldoutRecordResult) -> None:
        if self.on_holdout_recorded is None:
            return
        try:
            self.on_holdout_recorded(result)
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
                # Contract 1.9: the model the agent's chat client reports, unless the host's environment names one.
                environment=_with_reported_model(settings.environment, self.agent),
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

        # The embedding is derived data: written after the record's own commit, in the background, never failing it.
        self._start_index(finalized, identity)

        if settings.on_finalized is not None:
            try:
                settings.on_finalized(finalized)
            except Exception:
                pass

    def _start_index(self, finalized: FinalizedExperience, identity: ExperienceIdentity) -> None:
        """Starts embedding the stored record's summary in the background; never raises, never waits."""
        settings = self.settings
        if settings.embedder is None or not isinstance(settings.store, EmbeddingIndex):
            return
        try:
            task = asyncio.get_running_loop().create_task(self._index(finalized, identity))
        except Exception as ex:
            self.report("Index", f"Starting the embedding write raised {_type_name(ex)}.", ex)
            return
        settings._index_tasks.add(task)
        task.add_done_callback(settings._index_tasks.discard)

    async def _index(self, finalized: FinalizedExperience, identity: ExperienceIdentity) -> None:
        settings = self.settings
        with observe("index") as op:
            if op is not None:
                op.set("agentexperience.experience_id", str(finalized.record.experience_id))
            try:
                await asyncio.wait_for(self._write_embedding(finalized, identity, op), settings.finalization_timeout)
            except (asyncio.TimeoutError, TimeoutError) as ex:
                self.report(
                    "Index", f"Embedding the record did not finish within {settings.finalization_timeout} seconds.", ex
                )
            except asyncio.CancelledError as ex:
                self.report("Index", "Embedding the record was cancelled.", ex)
                raise
            except Exception as ex:
                self.report(
                    "Index", f"Embedding the record raised {_type_name(ex)}; its text retrieval is unaffected.", ex
                )

    async def _write_embedding(self, finalized: FinalizedExperience, identity: ExperienceIdentity, op: Any) -> None:
        settings = self.settings
        embedder, store = settings.embedder, settings.store
        assert embedder is not None and isinstance(store, EmbeddingIndex)
        record = finalized.record
        summary = record_summary(record)
        # What the index reports if it stops here: the provider failed, then (past the embedding) the store did.
        if op is not None:
            op.outcome("ProviderFailed")
        model_id, dimension = embedder.model_id, embedder.dimension
        validate_model_id(model_id)
        vector = validate_vector(await embed_text(embedder, summary), dimension)
        if op is not None:
            op.outcome("IndexFailed")
        write = EmbeddingWrite(
            record.experience_id,
            record.scope,
            model_id,
            dimension,
            content_hash(model_id, summary),
            record.revision,
            vector,
            settings.clock(),
        )
        written = cast(
            EmbeddingWriteResult, await run_on_vector_thread(store.write_embedding, identity.authorization, write)
        )
        if op is not None:
            # Only the contract's outcomes become a label; a custom index's own word never does.
            op.outcome(
                "Indexed"
                if written.outcome == "Written"
                else written.outcome
                if written.outcome in ("Stale", "Missing")
                else "IndexFailed"
            )


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


class _AbandonedSearches:
    """
    The text searches one provider abandoned (contract 1.12, .NET's ``MaxAbandonedSearches``): a search still running
    past the retrieval policy's timeout, or when the caller cancelled, is not stopped (a thread cannot be) but counted
    until its thread actually ends. While the count is at the cap no new search starts.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._count = 0

    @property
    def count(self) -> int:
        with self._lock:
            return self._count

    def start(self, call: Callable[[], Any]) -> _Search:
        """Starts ``call`` on a thread; the returned search is awaited for its answer and abandoned on a timeout."""
        return _Search(self, call)


class _Search:
    """One text search on a daemon thread of its own (so an abandoned one never holds the event loop's shutdown up):
    its thread always runs to its end, which is when an abandoned search stops counting."""

    def __init__(self, owner: _AbandonedSearches, call: Callable[[], Any]) -> None:
        self._owner = owner
        self._ended = False
        self._abandoned = False
        loop = asyncio.get_running_loop()
        self._future: asyncio.Future[Any] = loop.create_future()
        context = contextvars.copy_context()

        def settle(value: Any, error: BaseException | None) -> None:
            if not self._future.done():
                if error is None:
                    self._future.set_result(value)
                else:
                    self._future.set_exception(error)

        def run() -> None:
            try:
                try:
                    value, error = context.run(call), None
                except BaseException as ex:
                    value, error = None, ex
                try:
                    loop.call_soon_threadsafe(settle, value, error)
                except RuntimeError:  # the loop closed first
                    pass
            finally:
                with owner._lock:
                    self._ended = True
                    if self._abandoned:
                        owner._count -= 1

        threading.Thread(target=run, name="agentexperience-search", daemon=True).start()

    async def answer(self) -> Any:
        return await self._future

    def abandon(self) -> None:
        """Counts the search as abandoned until its thread ends (at once, when it already has)."""
        with self._owner._lock:
            if not self._ended and not self._abandoned:
                self._abandoned = True
                self._owner._count += 1


class _BackgroundRecords:
    """
    The background records of one provider (contract 1.4's injection records, 1.8's holdout assignments, 1.11's
    provider holdout assignments): each a task on the running event loop whose writes run on daemon threads of its own,
    bounded by ``timeout()`` seconds, at most :data:`MAX_ABANDONED_INJECTION_RECORDS` writes at once. Never raises into
    the invocation.
    """

    def __init__(self, timeout: Callable[[], float]) -> None:
        self._timeout = timeout
        self._recordings: set[asyncio.Task[Any]] = set()
        self._abandoned_records = 0
        self._running_writes = 0
        self._abandoned_lock = threading.Lock()

    async def settled(self) -> None:
        """Completes once every record started on the running event loop has ended; never raises."""
        loop = asyncio.get_running_loop()
        while pending := [task for task in self._recordings if task.get_loop() is loop and not task.done()]:
            await asyncio.gather(*pending, return_exceptions=True)

    @property
    def abandoned(self) -> int:
        with self._abandoned_lock:
            return self._abandoned_records

    def track(self, task: asyncio.Task[Any]) -> None:
        """Counts ``task`` among the records :meth:`settled` awaits."""
        self._recordings.add(task)
        task.add_done_callback(self._recordings.discard)

    def start(
        self,
        operation: str,
        writes: Callable[[], list[Callable[[], None]]] | None,
        report: Callable[[InjectionRecordOutcome, BaseException | None], None],
    ) -> None:
        """Starts one background record (``writes`` is ``None`` for a store that does not track it: ``NotTracked``);
        never raises, never waits."""
        try:
            if writes is None:
                with observe(operation) as op:
                    if op is not None:
                        op.outcome("NotTracked")
                report("NotTracked", None)
                return
            task = asyncio.get_running_loop().create_task(self._record(operation, writes(), report))
        except Exception as ex:
            with observe(operation) as op:
                if op is not None:
                    op.outcome("NotRecorded", "Unexpected")
            report("NotRecorded", ex)
            return
        self._recordings.add(task)
        task.add_done_callback(self._recordings.discard)

    async def _record(
        self,
        operation: str,
        writes: list[Callable[[], None]],
        report: Callable[[InjectionRecordOutcome, BaseException | None], None],
    ) -> None:
        """
        The background record: one write per chunk of at most 200 records, each a call on a worker thread of the
        provider's own, all within ``injection_record_timeout``. A call that overruns it is abandoned (a thread cannot
        be stopped): it is counted until it ends. At most :data:`MAX_ABANDONED_INJECTION_RECORDS` writes run at once,
        so writes that hang can never take more threads than that, and never the event loop's default executor, which
        the search and capture use. Never raises, except the task's own cancellation, which is reported first.
        """
        with observe(operation) as op:
            await self._record_observed(writes, report, op)

    async def _record_observed(
        self,
        writes: list[Callable[[], None]],
        report: Callable[[InjectionRecordOutcome, BaseException | None], None],
        op: Any,
    ) -> None:
        outcome: InjectionRecordOutcome = "Recorded"
        error: BaseException | None = None
        error_class: str | None = None
        loop = asyncio.get_running_loop()
        deadline = loop.time() + self._timeout()
        try:
            for chunk_write in writes:
                left = deadline - loop.time()
                if left <= 0:
                    raise TimeoutError("The injection record ran out of time.")
                write = self._start_write(loop, chunk_write)
                if write is None:
                    outcome = "NotRecorded"
                    error_class = "Timeout"
                    error = RuntimeError("Too many earlier injection records are still running against the store.")
                    break
                future, state = write
                try:
                    await asyncio.wait_for(asyncio.shield(future), left)
                except (asyncio.TimeoutError, asyncio.CancelledError):
                    self._abandon(state)
                    raise
        except (asyncio.TimeoutError, TimeoutError) as ex:
            outcome, error, error_class = "OutcomeUnknown", ex, "Timeout"
        except asyncio.CancelledError as ex:
            if op is not None:
                op.outcome("OutcomeUnknown", "Cancelled")
                op.close()
            report("OutcomeUnknown", ex)
            raise
        except Exception as ex:
            # A refusal (the grant is missing) is an answer, not a failure: only a store or unexpected failure counts.
            outcome, error = "NotRecorded", ex
            error_class = None if isinstance(ex, AuthorizationDenied) else _classify_error(ex)
        if op is not None:
            op.outcome(outcome, error_class)
        report(outcome, error)

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
        self._background = _BackgroundRecords(lambda: self.settings.injection_record_timeout)
        self._searches = _AbandonedSearches()
        # Contract 1.10: the cached benefit estimates, only with the policy on.
        self._benefit = (
            BenefitCache(settings.store, settings.benefit, settings.clock) if settings.benefit.enabled else None
        )

    async def wait_for_benefit_refreshes(self) -> None:
        """Completes once every benefit refresh this provider started so far has ended (.NET's
        ``BenefitRefreshesSettledAsync``). Each is bounded by the policy's ``refresh_timeout``; it never raises."""
        if self._benefit is not None:
            await self._benefit.settled()

    async def wait_for_injection_records(self) -> None:
        """Completes once every injection record this provider started on the running event loop has ended, and
        every record of a memory provider :func:`use_measured_memory` wrapped on the same agent (contract 1.11). Each
        is bounded by ``injection_record_timeout``, so this is too; it never raises."""
        await self._background.settled()
        for measured in list(self.settings._measured):
            await measured.wait_for_records()

    @property
    def abandoned_searches(self) -> int:
        """How many text searches abandoned on a retrieval timeout (or a cancellation) are still running against the
        store (contract 1.12); at the retrieval policy's ``max_abandoned_searches`` no new search starts."""
        return self._searches.count

    @property
    def abandoned_injection_records(self) -> int:
        """How many injection writes that outlived their timeout are still running against the store."""
        return self._background.abandoned

    def _start_recording(self, identity: ExperienceIdentity, ids: tuple[uuid.UUID, ...], injected_at: Instant) -> None:
        """Starts recording ``ids`` as injected at ``injected_at``, in the background; never raises, never waits."""
        settings = self.settings
        recorder = settings.store if isinstance(settings.store, InjectionRecorder) else None

        def writes() -> list[Callable[[], None]]:
            assert recorder is not None
            return [
                functools.partial(
                    recorder.record_injection,
                    identity.authorization,
                    identity.scope,
                    list(ids[start : start + MAX_RECORD_INJECTION_IDS]),
                    injected_at,
                )
                for start in range(0, len(ids), MAX_RECORD_INJECTION_IDS)
            ]

        def report(outcome: InjectionRecordOutcome, error: BaseException | None) -> None:
            settings.report_injection_recorded(InjectionRecordResult(outcome, ids, error))

        self._background.start("record_injection", None if recorder is None else writes, report)

    def _start_recording_holdout(self, identity: ExperienceIdentity, assignment: HoldoutAssignment) -> None:
        """Starts recording the run's holdout ``assignment`` (contract 1.8), in the background, exactly as an injection
        record is (the ``record_holdout`` operation); never raises, never waits."""
        settings = self.settings
        recorder = settings.store if isinstance(settings.store, HoldoutRecorder) else None
        records = assignment.records

        def writes() -> list[Callable[[], None]]:
            assert recorder is not None
            return [
                functools.partial(
                    recorder.record_holdout_assignment,
                    identity.authorization,
                    identity.scope,
                    dataclasses.replace(assignment, records=records[start : start + MAX_HOLDOUT_RECORDS]),
                )
                for start in range(0, len(records), MAX_HOLDOUT_RECORDS)
            ]

        def report(outcome: InjectionRecordOutcome, error: BaseException | None) -> None:
            settings.report_holdout_recorded(HoldoutRecordResult(outcome, assignment, error))

        self._background.start("record_holdout", None if recorder is None else writes, report)

    async def wait_for_index_writes(self) -> None:
        """Completes once the embedding writes started after records were stored have ended; see
        :meth:`ExperienceSettings.wait_for_index_writes`."""
        await self.settings.wait_for_index_writes()

    async def _vector_channel(
        self, task_text: str, identity: ExperienceIdentity
    ) -> tuple[list[SearchRow], VectorFallback | None]:
        """
        The vector channel (contract 1.6): embed the task text, search the stored vectors, both within
        ``vector_timeout``. Never raises (apart from the caller's cancellation): whatever goes wrong is the fallback it
        answers with, and its text never quotes record or vector content.
        """
        settings = self.settings
        embedder, store = settings.embedder, settings.store
        if embedder is None:
            return [], VectorFallback("NotConfigured", "No embedder is registered, so only text retrieval ran.")
        if not isinstance(store, EmbeddingIndex):
            return [], VectorFallback(
                "NotConfigured", "The store does not serve vector search, so only text retrieval ran."
            )
        loop = asyncio.get_running_loop()
        # Contract 1.12: vector_timeout is the channel's own fallback only when strictly shorter than the retrieval
        # policy's timeout; otherwise the policy's timeout alone decides (and a channel still running times the
        # retrieval out), so two equal bounds never race.
        own = settings.vector_timeout < settings.retrieval_policy.timeout.total_seconds()
        deadline = loop.time() + settings.vector_timeout if own else None

        def remaining() -> float | None:
            return None if deadline is None else max(deadline - loop.time(), 0.0)

        try:
            model_id, dimension = embedder.model_id, embedder.dimension
            raw = await asyncio.wait_for(embed_text(embedder, task_text), remaining())
            if not isinstance(model_id, str) or not model_id.strip():
                return [], VectorFallback("ProviderUnavailable", "The embedder reported no model ID.")
            validate_model_id(model_id)
            vector = validate_vector(raw, dimension)
            query = vector_query_for_policy(
                identity.scope,
                model_id,
                vector,
                settings.retrieval_policy,
                exclude_model_authored=settings.exclude_model_authored,
            )
        except (asyncio.TimeoutError, TimeoutError) as ex:
            return [], VectorFallback("ProviderUnavailable", "The embedder did not answer in time.", ex)
        except Exception as ex:
            return [], VectorFallback(
                "ProviderUnavailable", f"The embedder failed or returned an unusable vector ({_type_name(ex)}).", ex
            )
        try:
            result = await asyncio.wait_for(
                run_on_vector_thread(store.search_vectors, identity.authorization, query),
                remaining(),
            )
        except (asyncio.TimeoutError, TimeoutError) as ex:
            return [], VectorFallback("VectorSearchFailed", "The vector search did not answer in time.", ex)
        except Exception as ex:
            return [], VectorFallback("VectorSearchFailed", f"The vector search raised {_type_name(ex)}.", ex)
        if not isinstance(result, VectorSearchResult):
            return [], VectorFallback("VectorSearchFailed", "The store answered the vector search with something else.")
        if result.outcome == "ModelMismatch":
            return [], VectorFallback("ModelMismatch", "Every stored embedding in this scope is of another model.")
        if result.outcome == "DimensionMismatch":
            return [], VectorFallback("DimensionMismatch", "Every stored embedding in this scope is of another width.")
        if result.outcome != "Found":
            return [], VectorFallback("VectorSearchFailed", "The vector search answered with an unknown outcome.")
        return [SearchRow(hit.record, hit.relevance) for hit in result.hits], None

    async def before_run(
        self,
        *,
        agent: Any,
        session: Any,
        context: Any,
        state: dict[str, Any],
    ) -> None:
        with observe("inject") as op:
            outcome = await self._inject(agent, session, context, op)
            if op is not None:
                op.outcome(outcome)

    async def _inject(self, agent: Any, session: Any, context: Any, op: Any) -> str:
        """The injection; answers its outcome (``InjectionOutcome``'s member name). A raise is the invocation's own
        cancellation, which the observer reports as ``Faulted``/``Cancelled``."""
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
            return "Failed"
        if identity is None:
            return "Skipped"

        retrieval_failed = False
        assignment: HoldoutAssignment | None = None
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
                settings.report_injected(InjectionResult((), 0, False, outcome="NothingToInject"))
                return "NothingToInject"
            policy = settings.retrieval_policy
            parameters = SearchParameters.for_policy(
                identity.scope, task_text, policy, exclude_model_authored=settings.exclude_model_authored
            )
            stopped: Literal["RetrievalTimedOut", "RetrievalDenied"] | None = None
            with observe("retrieve") as retrieval:
                if not identity.authorization.permits(identity.scope):
                    # Contract 1.12, as .NET: a scope outside the bounds is Denied before anything is searched or
                    # embedded.
                    if retrieval is not None:
                        retrieval.outcome("Denied")
                    stopped = "RetrievalDenied"
                elif self._searches.count >= policy.max_abandoned_searches:
                    # Too many earlier searches are still running against the store: none is started, and the call
                    # ends as the timeout it would almost certainly become.
                    if retrieval is not None:
                        retrieval.outcome("TimedOut")
                    stopped = "RetrievalTimedOut"
                    settings.report_injection(
                        InjectionFailure(
                            "The candidate store has too many abandoned searches still running (at least "
                            f"{policy.max_abandoned_searches}), so no search was started."
                        )
                    )
                else:
                    search = self._searches.start(
                        functools.partial(settings.store.search, identity.authorization, parameters)
                    )
                    text_search = asyncio.ensure_future(search.answer())
                    vector_search = asyncio.ensure_future(self._vector_channel(task_text, identity))
                    try:
                        # Both channels run together, bounded together by the policy's timeout; the vector channel
                        # never raises (it answers with a fallback instead).
                        text_found, vector_found = await asyncio.wait_for(
                            asyncio.gather(text_search, vector_search, return_exceptions=True),
                            policy.timeout.total_seconds(),
                        )
                    except (asyncio.TimeoutError, TimeoutError):
                        search.abandon()
                        if retrieval is not None:
                            retrieval.outcome("TimedOut")
                        stopped = "RetrievalTimedOut"
                    except BaseException:
                        # The caller's own cancellation: it propagates, and the search is abandoned too.
                        search.abandon()
                        raise
                    if stopped is None:
                        if isinstance(vector_found, BaseException):
                            vector_found = (
                                [],
                                VectorFallback("VectorSearchFailed", "The vector channel failed unexpectedly."),
                            )
                        vector_rows, fallback = vector_found
                        if fallback is not None:
                            settings.report_vector_fallback(fallback)
                        if isinstance(text_found, BaseException):
                            # The text channel's failure fails the call, whatever the vector channel found.
                            # Retrieval reports it as its outcome, as .NET does; it is not thrown.
                            if retrieval is not None:
                                retrieval.outcome("Failed")
                            retrieval_failed = True
                        else:
                            rows = text_found
                            # The instant the block is built at: ranking's "now", and the injection's instant.
                            now = settings.clock()
                            if vector_rows:
                                ranked = rank_hybrid(
                                    rows,
                                    vector_rows,
                                    identity.scope,
                                    now,
                                    settings.preferred_environment,
                                    minimum_vector_relevance=settings.vector_floor,
                                    exclude_model_authored=settings.exclude_model_authored,
                                    policy=policy,
                                    confidence_decay=settings.confidence_decay,
                                )
                            else:
                                ranked = rank(
                                    rows,
                                    identity.scope,
                                    now,
                                    settings.preferred_environment,
                                    exclude_model_authored=settings.exclude_model_authored,
                                    policy=policy,
                                    confidence_decay=settings.confidence_decay,
                                )
                            if retrieval is not None:
                                retrieval.outcome("Completed")
            if stopped is not None:
                settings.report_injected(InjectionResult((), 0, False, outcome=stopped))
                return stopped
            if retrieval_failed:
                assert isinstance(text_found, BaseException)
                raise text_found
            # Contract 1.10: the benefit policy's cached view (never waits on the store), then the selection.
            view = None
            if self._benefit is not None:
                model = _invoking_model(settings, agent, captured)
                view = self._benefit.current(identity.authorization, identity.scope, identity.principal, model)
            selection = select_block(
                ranked.ranked,
                view,
                settings.block_options,
                exclude_model_authored=settings.exclude_model_authored,
                prefer_measured_helpful=settings.benefit.prefer_measured_helpful,
            )
            block = selection.block
            if op is not None:
                op.set("agentexperience.omitted_count", len(selection.omitted))
            if block.is_empty:
                # As .NET reports it: nothing injected, and every omission.
                settings.report_injected(
                    InjectionResult(
                        (),
                        0,
                        ranked.truncated,
                        fallback,
                        outcome="NothingToInject",
                        omitted=selection.omitted,
                        excluded=ranked.excluded,
                    )
                )
                return "NothingToInject"
            # The injection holdout (contract 1.8). Decided once the block is built, so what a held-out run is denied is
            # exactly what it would have been given, and before anything is handed over. Only a run this agent's own
            # capture captures takes part, and only with records to deliver; everything else is injected as it always
            # was, and records nothing.
            run_id = scope.run_id if captured and scope is not None else None
            holdout = settings.holdout
            if holdout.active and block.included and run_id is not None:
                arm: HoldoutArm = assign_holdout(run_id, holdout.salt, holdout.rate)
                assignment = HoldoutAssignment(
                    run_id,
                    arm,
                    tuple(RunExposure(record.experience_id, record.revision) for record in block.included),
                    now,
                )
            if assignment is None or assignment.arm == "Treatment":
                message = Message(
                    role=settings.message_role,
                    contents=[block.text],
                    additional_properties={HISTORICAL_REFERENCE_KEY: True},
                )
                context.extend_messages(self, [message])
        except Exception as ex:
            settings.report_injection(InjectionFailure(f"Injection raised {_type_name(ex)}; no block was added.", ex))
            return "RetrievalFailed" if retrieval_failed else "Failed"

        if assignment is not None and assignment.arm == "Holdout":
            # The holdout arm: nothing handed over, no exposure, no injection record; the assignment is recorded in the
            # background. This port delivers no withdrawal notices, so a held-out invocation is never a retraction.
            withheld = tuple(record.experience_id for record in block.included)
            if settings.on_injected is not None:
                try:
                    settings.on_injected(
                        InjectionResult(
                            (),
                            0,
                            ranked.truncated,
                            fallback,
                            "Withheld",
                            withheld,
                            selection.omitted,
                            ranked.excluded,
                        )
                    )
                except Exception:
                    pass
            self._start_recording_holdout(identity, assignment)
            return "Withheld"

        # The records this block delivers, at the revision each was rendered from, on the run the invocation is
        # captured as. Recorded last, once nothing that could fail remains.
        if captured and scope is not None:
            scope.record_exposures([RunExposure(record.experience_id, record.revision) for record in block.included])
        delivered = tuple(record.experience_id for record in block.included)
        if settings.on_injected is not None:
            try:
                settings.on_injected(
                    InjectionResult(
                        delivered,
                        block.byte_count,
                        ranked.truncated,
                        fallback,
                        omitted=selection.omitted,
                        excluded=ranked.excluded,
                    )
                )
            except Exception:
                pass

        # When each delivered record was injected, for the .NET lessons view: started last, in the background, so the
        # block is handed over and the model called without waiting for the store.
        if delivered:
            self._start_recording(identity, delivered, now)

        # The treatment arm's assignment, recorded the same way.
        if assignment is not None:
            self._start_recording_holdout(identity, assignment)
        return "Injected"


def _invoking_model(settings: ExperienceSettings, agent: Any, captured: bool) -> str | None:
    """The model the benefit policy acts for (contract 1.10): the model capture records for the run (the host's
    environment value when it sets one) when the invocation is captured, otherwise the one the agent's chat client
    reports."""
    if captured:
        return _with_reported_model(settings.environment, agent).metadata.get(MODEL_METADATA_KEY)
    return reported_model(agent)


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


# ---- The lessons view --------------------------------------------------------------------------------------------


def agent_experience_lessons(source: Any) -> ExperienceLessons:
    """
    The read-only lessons view (contract 1.7) for an agent set up with :func:`use_agent_experience`, or for its
    :class:`ExperienceSettings`: it reads the same store, renders blocks with the same block options, excludes
    model-authored lessons when the setup does, applies the setup's retrieval policy (its confidence floor and
    ``max_age``, contract 1.12) and uses the setup's clock. When the setup has one fixed identity (the
    development identity, say), ``list()`` lists its scope; otherwise pass the identity from your own authentication:
    ``list(identity)``. ``TypeError`` for an agent that was not set up, or a store that cannot list lessons.
    """
    settings = source
    if not isinstance(source, ExperienceSettings):
        middleware = getattr(source, "middleware", None) or ()
        found = next((item.settings for item in middleware if isinstance(item, ExperienceCaptureMiddleware)), None)
        if found is None:
            raise TypeError("The agent was not set up with use_agent_experience; pass it, or its ExperienceSettings.")
        settings = found
    return ExperienceLessons(
        cast("LessonsReader", settings.store),
        block_options=settings.block_options,
        exclude_model_authored=settings.exclude_model_authored,
        clock=settings.clock,
        identity=settings.identity if isinstance(settings.identity, ExperienceIdentity) else None,
        benefit=settings.benefit,
        retrieval_policy=settings.retrieval_policy,
    )


# ---- Measuring another memory provider (contract 1.11) ------------------------------------------------------------


@dataclass(frozen=True)
class ProviderHoldoutRecordResult:
    """How recording one run's arm in a memory provider's holdout ended (contract 1.11, the
    ``record_provider_holdout`` operation): the outcome (as for an injection record), the assignment (``None`` when
    inspecting the provider's answer failed and nothing was assigned), and the exception, when there was one."""

    outcome: InjectionRecordOutcome
    assignment: ProviderHoldoutAssignment | None
    exception: BaseException | None = None


ProviderAuthorize = Callable[[Scope], "AuthorizationBounds | None"]
"""The host's answer for a captured run with no identity resolved: the bounds to record under, or ``None``."""


def _private_context(context: Any) -> Any:
    """A context of the wrapped provider's own: the invocation's, with every container it adds to copied, so nothing it
    adds reaches the invocation unless the wrapper delivers it. ``options`` and ``metadata`` are shared, as they are
    without the wrapper (the model never sees them)."""
    private = copy.copy(context)
    private.input_messages = list(context.input_messages)
    private.context_messages = {source: list(batch) for source, batch in context.context_messages.items()}
    private.instructions = list(context.instructions)
    private.tools = list(context.tools)
    middleware = getattr(context, "middleware", None)
    if isinstance(middleware, dict):
        private.middleware = {source: list(batch) for source, batch in middleware.items()}
    return private


def _same(left: Sequence[Any], right: Sequence[Any]) -> bool:
    return len(left) == len(right) and all(a is b for a, b in zip(left, right, strict=True))


def _deliver_batches(target: dict[str, list[Any]], source: dict[str, list[Any]]) -> None:
    for key in [key for key in target if key not in source]:
        del target[key]
    for key, batch in source.items():
        if key not in target:
            target[key] = list(batch)
        elif not _same(target[key], batch):
            target[key][:] = batch


def _deliver(context: Any, private: Any) -> None:
    """Makes the invocation's context what the wrapped provider left its own: exactly as without the wrapper."""
    if not _same(context.input_messages, private.input_messages):
        context.input_messages[:] = private.input_messages
    _deliver_batches(context.context_messages, private.context_messages)
    if context.instructions != private.instructions:
        context.instructions[:] = private.instructions
    if not _same(context.tools, private.tools):
        context.tools[:] = private.tools
    middleware = getattr(context, "middleware", None)
    if isinstance(middleware, dict) and isinstance(getattr(private, "middleware", None), dict):
        _deliver_batches(middleware, private.middleware)


def _messages(context: Any) -> list[Any]:
    return [*context.input_messages, *(message for batch in context.context_messages.values() for message in batch)]


def _added(context: Any, private: Any) -> bool:
    """Whether the wrapped provider added anything: an instruction (non-empty, and not one it was handed), or a message
    or tool it was not handed (compared by identity)."""
    handed_instructions = Counter(instruction for instruction in context.instructions)
    answered_instructions = Counter(instruction for instruction in private.instructions)
    if any(
        isinstance(instruction, str) and instruction and count > handed_instructions[instruction]
        for instruction, count in answered_instructions.items()
    ):
        return True
    handed_messages = {id(message) for message in _messages(context)}
    if any(message is not None and id(message) not in handed_messages for message in _messages(private)):
        return True
    handed_tools = {id(item) for item in context.tools}
    return any(item is not None and id(item) not in handed_tools for item in private.tools)


class ExperienceHoldoutContextProvider(ContextProvider):
    """
    Puts another memory provider (Mem0, Zep, Letta, the host's own: any MAF ``ContextProvider``) under the same
    holdout the lessons run under, so its benefit can be measured (contract 1.11): on a deterministic share of captured
    runs whose wrapped provider added something, what it added is withheld, and each such run's arm is recorded against
    ``provider_key``. :func:`agentexperience.estimate_provider_benefit` then compares the arms' verified outcomes. Off
    by default (rate 0). .NET's ``ExperienceHoldoutContextProvider``.

    - **The wrapped provider runs exactly as without the wrapper.** Its ``before_run`` is always called, in both arms,
      and so is its ``after_run`` (with the invocation's own context); it keeps its ``source_id``, so its session
      state is its own. Its exceptions propagate unchanged, and nothing is recorded for an invocation whose hook raised.
    - **At rate 0** the wrapper is a pass-through: nothing is inspected, assigned or recorded, and no telemetry.
    - **Who takes part.** Only an invocation captured by this agent's own :func:`use_agent_experience` (it has a run
      ID), with an authorization (the identity capture resolved, else ``authorize(scope)``; a callback that raises
      counts as no answer), whose wrapped provider added an instruction, a message or a tool. Every other invocation
      gets exactly what the wrapped provider added, and is assigned nothing.
    - **The arm** is :func:`~agentexperience.holdout.assign_provider_holdout` of the run ID: a ``Holdout`` invocation
      sees exactly the context it would without the provider; a ``Treatment`` one gets what the provider added.
    - **The context passed in is never changed by the wrapper.** The wrapped provider adds to a context of its own
      (the invocation's, with its messages, instructions, tools and middleware copied; ``options`` and ``metadata``
      shared), and its additions are copied into the invocation's context in the treatment arm, or when the run does
      not take part.
    - **What is recorded**, in the background, bounded by ``record_timeout`` and never delaying or failing the
      invocation: the run, the key, the arm and the instant, through the store's
      ``record_provider_holdout_assignment``, in the scope the run is captured in. Its outcome is the
      ``record_provider_holdout`` operation in telemetry and ``on_recorded``. A failure while inspecting the provider's
      answer delivers the answer unchanged, records nothing, and is reported ``NotRecorded`` (``Unexpected``).

    ``ValueError`` for a malformed key; ``TypeError`` for a provider without ``before_run``/``after_run``, holdout
    options that are not :class:`HoldoutOptions`, or a callback that is not callable.
    """

    def __init__(
        self,
        inner: Any,
        provider_key: str,
        holdout: HoldoutOptions,
        store: object,
        *,
        authorize: ProviderAuthorize | None = None,
        clock: Callable[[], Instant] = Instant.now,
        record_timeout: float = DEFAULT_INJECTION_RECORD_TIMEOUT,
        on_recorded: Callable[[ProviderHoldoutRecordResult], None] | None = None,
    ) -> None:
        if (
            inner is None
            or not callable(getattr(inner, "before_run", None))
            or not callable(getattr(inner, "after_run", None))
        ):
            raise TypeError("inner must be a MAF context provider (with before_run and after_run).")
        source_id = getattr(inner, "source_id", None)
        if not isinstance(source_id, str):
            raise TypeError("inner must have a source_id.")
        check_provider_key(provider_key)
        if not isinstance(holdout, HoldoutOptions):
            raise TypeError("holdout must be a HoldoutOptions.")
        if store is None:
            raise ValueError("A store is required.")
        for name, value in (("authorize", authorize), ("clock", clock), ("on_recorded", on_recorded)):
            if value is not None and not callable(value):
                raise TypeError(f"{name} must be callable when given.")
        if not record_timeout > 0:
            raise ValueError("record_timeout must be a positive number of seconds.")
        # The wrapped provider's own source ID: MAF hands the wrapper its session state, and attributes nothing to the
        # wrapper.
        super().__init__(source_id)
        self.after_run_once_per_turn = bool(getattr(inner, "after_run_once_per_turn", False))
        self._inner = inner
        self._provider_key = provider_key
        self._holdout = holdout
        self._salt = provider_salt(provider_key, holdout.salt)
        self._store = store
        self._authorize = authorize
        self._clock = clock
        self._on_recorded = on_recorded
        self._background = _BackgroundRecords(lambda: record_timeout)

    @property
    def provider_key(self) -> str:
        """The key the assignments are recorded against."""
        return self._provider_key

    @property
    def inner(self) -> Any:
        """The wrapped provider."""
        return self._inner

    @property
    def holdout(self) -> HoldoutOptions:
        """The rate and salt the arms are drawn with."""
        return self._holdout

    async def wait_for_records(self) -> None:
        """Completes once every background record this wrapper started on the running event loop has ended. Each is
        bounded by ``record_timeout``, so this is too; it never raises."""
        await self._background.settled()

    @property
    def abandoned_records(self) -> int:
        """How many writes that outlived their timeout are still running against the store."""
        return self._background.abandoned

    async def before_run(
        self,
        *,
        agent: Any,
        session: Any,
        context: Any,
        state: dict[str, Any],
    ) -> None:
        if not self._holdout.active:
            await self._inner.before_run(agent=agent, session=session, context=context, state=state)
            return

        private = _private_context(context)
        await self._inner.before_run(agent=agent, session=session, context=private, state=state)
        try:
            scope = _current_scope.get()
            run_id = scope.run_id if scope is not None and scope.agent is agent else None
            authorized = None
            if run_id is not None and scope is not None and _added(context, private):
                authorized = self._authorization(scope)
            if run_id is None or authorized is None:
                _deliver(context, private)
                return
            bounds, run_scope = authorized
            arm: HoldoutArm = assign_holdout(run_id, self._salt, self._holdout.rate)
            assignment = ProviderHoldoutAssignment(run_id, self._provider_key, arm, self._clock())
        except Exception as ex:
            # A check that raised after the wrapped provider answered: its answer, unchanged, and no row.
            with observe("record_provider_holdout") as op:
                if op is not None:
                    op.outcome("NotRecorded", "Unexpected")
            self._report_later(ProviderHoldoutRecordResult("NotRecorded", None, ex))
            _deliver(context, private)
            return

        self._start_recording(bounds, run_scope, assignment)
        if arm == "Treatment":
            _deliver(context, private)

    async def after_run(
        self,
        *,
        agent: Any,
        session: Any,
        context: Any,
        state: dict[str, Any],
    ) -> None:
        """Forwards to the wrapped provider, in both arms, so it keeps learning from every run."""
        await self._inner.after_run(agent=agent, session=session, context=context, state=state)

    def _authorization(self, scope: _CaptureScope) -> tuple[AuthorizationBounds, Scope] | None:
        """The identity capture resolved for this invocation, else the host's callback's answer (``None``: the run
        does not take part; a callback that raises is no answer)."""
        identity = scope.identity
        if identity is not None:
            return identity.authorization, identity.scope
        capture = scope.capture
        if self._authorize is None or capture is None:
            return None
        run_scope = capture.run.scope
        try:
            bounds = self._authorize(run_scope)
        except Exception:
            return None
        return (bounds, run_scope) if isinstance(bounds, AuthorizationBounds) else None

    def _report(self, result: ProviderHoldoutRecordResult) -> None:
        if self._on_recorded is None:
            return
        try:
            self._on_recorded(result)
        except Exception:
            pass

    def _report_later(self, result: ProviderHoldoutRecordResult) -> None:
        """Calls ``on_recorded`` on a worker thread, never on the invocation's path (a callback that blocks never
        delays the model call); :meth:`wait_for_records` awaits it. Never raises."""
        if self._on_recorded is None:
            return
        try:
            task = asyncio.get_running_loop().create_task(asyncio.to_thread(self._report, result))
        except Exception:
            return
        self._background.track(task)

    def _start_recording(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: ProviderHoldoutAssignment
    ) -> None:
        """Starts recording ``assignment`` in the background (the ``record_provider_holdout`` operation); never
        raises, never waits."""
        store = self._store
        recorder = store if isinstance(store, ProviderHoldoutRecorder) else None

        def writes() -> list[Callable[[], None]]:
            assert recorder is not None
            return [functools.partial(recorder.record_provider_holdout_assignment, bounds, scope, assignment)]

        def report(outcome: InjectionRecordOutcome, error: BaseException | None) -> None:
            self._report_later(ProviderHoldoutRecordResult(outcome, assignment, error))

        self._background.start("record_provider_holdout", None if recorder is None else writes, report)


def use_measured_memory(
    agent: Any,
    memory: Any,
    provider_key: str,
    *,
    holdout: HoldoutOptions | None = None,
    authorize: ProviderAuthorize | None = None,
    on_recorded: Callable[[ProviderHoldoutRecordResult], None] | None = None,
) -> ExperienceHoldoutContextProvider:
    """
    Wraps ``memory`` (another memory provider: any MAF ``ContextProvider``) in an
    :class:`ExperienceHoldoutContextProvider` on an agent :func:`use_agent_experience` was called on, and returns the
    wrapper: .NET's ``UseMeasuredMemory``. The wrapper takes the agent's store, clock and ``injection_record_timeout``,
    and, without ``holdout``, the injection holdout's rate and salt (off by default); the key is mixed into the salt,
    so the provider's arms are drawn independently of the lessons' either way. ``memory`` is replaced in the agent's
    ``context_providers`` where it is, or the wrapper is appended. A run is recorded under the identity
    :func:`use_agent_experience` resolved for it, in the scope it is captured in.

    On PostgreSQL the application role needs the grants of the contract's ``sql/application-role-privileges-1.11.sql``;
    without them every invocation still runs (and withholds) and every assignment is reported ``NotRecorded``.
    ``ValueError`` when :func:`use_agent_experience` was not called on ``agent``, or for a malformed key.
    """
    middleware = getattr(agent, "middleware", None)
    capture = next(
        (item for item in middleware or () if isinstance(item, ExperienceCaptureMiddleware)),
        None,
    )
    providers = getattr(agent, "context_providers", None)
    if capture is None or not isinstance(providers, list):
        raise ValueError("Call use_agent_experience on the agent before use_measured_memory.")
    settings = capture.settings
    wrapper = ExperienceHoldoutContextProvider(
        memory,
        provider_key,
        settings.holdout if holdout is None else holdout,
        settings.store,
        authorize=authorize,
        clock=settings.clock,
        record_timeout=settings.injection_record_timeout,
        on_recorded=on_recorded,
    )
    for index, provider in enumerate(providers):
        if provider is memory:
            providers[index] = wrapper
            break
    else:
        providers.append(wrapper)
    settings._measured.append(wrapper)
    return wrapper


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
    embedder: Embedder | None = None,
    min_vector_relevance: float | None = None,
    vector_timeout: float = DEFAULT_VECTOR_TIMEOUT,
    on_vector_fallback: Callable[[VectorFallback], None] | None = None,
    holdout: HoldoutOptions | None = None,
    on_holdout_recorded: Callable[[HoldoutRecordResult], None] | None = None,
    benefit: BenefitPolicy | None = None,
    retrieval_policy: RetrievalPolicy | None = None,
    confidence_decay: ConfidenceDecayPolicy | None = None,
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

    ``embedder`` (contract 1.6) turns text into a vector: with one, injection runs the text and the vector channel
    together (in-memory and PostgreSQL+pgvector stores serve it) and merges them, and every stored record's summary is
    embedded in the background. Without one, or when the vector channel fails, the injection answers from text and
    reports why (``on_vector_fallback``, ``InjectionResult.vector_fallback``). ``vector_timeout``, when shorter than the
    retrieval policy's timeout, bounds the embedding and the search of one injection; ``min_vector_relevance`` drops
    weaker vector candidates (replacing the policy's ``minimum_vector_relevance``).

    ``holdout`` (contract 1.8), ``HoldoutOptions(rate=0.1)`` say, withholds the block from a deterministic share of
    captured runs that have one, and records both arms (in the background, reported through ``on_holdout_recorded``)
    for the .NET benefit estimate. Off by default. On PostgreSQL the application role needs the grants of the
    contract's ``sql/application-role-privileges-1.8.sql``; without them every assignment is reported ``NotRecorded``.

    ``benefit`` (contract 1.10), ``BenefitPolicy(enabled=True)`` say, acts on the holdout's measured benefit: a lesson
    measured as hurting is not injected (``InjectionResult.omitted`` says ``MeasuredHarmful``), a scope whose lessons
    together hurt gets none, and with ``prefer_measured_helpful`` the helpful ones go first. The estimate is cached and
    refreshed in the background, never on the request path. Off by default.

    ``retrieval_policy`` (contract 1.12), .NET's ``RetrievalPolicy`` with its defaults, bounds each retrieval by its
    ``timeout`` (500 ms; past it nothing is injected, ``RetrievalTimedOut``), caps the searches abandoned on a timeout
    that are still running, and sets the confidence floor, the expiry (``max_age``, records excluded as ``Expired``),
    the recency half-life, the candidate limit, the minimum of matched terms and the vector floor.
    ``confidence_decay``, a :class:`~agentexperience.retrieval.ConfidenceDecayPolicy`, fades each record's confidence
    by its domain at ranking time. Neither ever changes a stored record.
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
        embedder=embedder,
        min_vector_relevance=min_vector_relevance,
        vector_timeout=vector_timeout,
        on_vector_fallback=on_vector_fallback,
        holdout=HoldoutOptions() if holdout is None else holdout,
        on_holdout_recorded=on_holdout_recorded,
        benefit=BenefitPolicy() if benefit is None else benefit,
        retrieval_policy=RetrievalPolicy() if retrieval_policy is None else retrieval_policy,
        confidence_decay=confidence_decay,
    )

    # Capture first, so it wraps everything else on the agent: injection runs inside it and reuses its identity.
    target: Any = agent
    target.middleware = [ExperienceCaptureMiddleware(settings), ExperienceToolMiddleware(settings), *(middleware or [])]
    if inject and not any(isinstance(provider, ExperienceContextProvider) for provider in providers):
        providers.append(ExperienceContextProvider(settings))
    return agent


__all__ = [
    "DEFAULT_INJECTION_RECORD_TIMEOUT",
    "DEFAULT_VECTOR_TIMEOUT",
    "HISTORICAL_REFERENCE_KEY",
    "MAX_ABANDONED_INJECTION_RECORDS",
    "PROVENANCE_SOURCE",
    "SOURCE_ID",
    "TOOL_REPORTED_ERROR_CLASS",
    "UNVERSIONED_REVISION",
    "Approval",
    "BenefitPolicy",
    "CaptureFailure",
    "DevelopmentOnlyError",
    "Embedder",
    "ExperienceCaptureMiddleware",
    "ExperienceContextProvider",
    "ExperienceHoldoutContextProvider",
    "ExperienceIdentity",
    "ExperienceLessons",
    "ExperienceSettings",
    "ExperienceStore",
    "ExperienceToolMiddleware",
    "HoldoutOptions",
    "HoldoutRecordResult",
    "IdentityContext",
    "InMemoryExperienceStore",
    "InjectionFailure",
    "InjectionRecordOutcome",
    "InjectionRecordResult",
    "InjectionResult",
    "OmittedRecord",
    "ProviderAuthorize",
    "ProviderHoldoutAssignment",
    "ProviderHoldoutRecordResult",
    "ToolResultContext",
    "VectorFallback",
    "Verification",
    "VerificationContext",
    "agent_experience_lessons",
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
    "use_measured_memory",
]
