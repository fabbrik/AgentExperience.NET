"""
AgentExperience for Python: a pure-Python core that implements the AgentExperience.NET language-neutral contract 1.5.

Pre-release. It finalizes a captured run into a stored Experience Record, renders the injected Historical Reference
block, derives the task text, classifies errors, and (with the ``postgres`` extra) writes records to and searches them
in a PostgreSQL schema the .NET migrator applied. ``agentexperience.maf`` (the ``maf`` extra) wires it into a Microsoft
Agent Framework for Python agent with one call.
"""

from __future__ import annotations

from importlib import metadata as _metadata

from .block import Block, BlockOptions, RankedRecord, RankingComponent, render_block
from .capture import CapturedRun, RawAttempt, RawToolCall, RunCapture
from .encoding import Duration, Instant
from .error_class import error_class
from .finalize import FinalizedExperience, InitialLifecycleEvent, finalize
from .ids import experience_id_for, initial_event_id_for, reflection_id_for
from .limits import CaptureLimits, SanitizationLimits
from .records import (
    Attempt,
    EnvironmentFingerprint,
    Evidence,
    ExperienceRecord,
    Outcome,
    Provenance,
    Reflection,
    RunExposure,
    Scope,
    ToolCall,
    from_stored,
    to_stored,
)
from .sanitization import Sanitization, SanitizationPolicy, SanitizationRejected
from .search import (
    AuthorizationBounds,
    RankedResult,
    SearchParameters,
    SearchRow,
    SearchSafetyError,
    rank,
)
from .task_text import Message, MessagePart, derive_task_text
from .verification import ClosedVerificationRound, RequiredCheck

try:
    __version__ = _metadata.version("agenticexperience")
except _metadata.PackageNotFoundError:  # running from a source tree that was never installed
    __version__ = "0.0.0+unknown"

CONTRACT_VERSION = "1.5"
"""The contract version this package passes: every fixture of contract 1.5, and so every 1.0, 1.1, 1.2, 1.3 and 1.4
fixture."""

SUPPORTED_CONTRACT_VERSIONS: tuple[str, ...] = ("1.0", "1.1", "1.2", "1.3", "1.4", "1.5")
"""Every contract version whose fixtures this package passes."""

__all__ = [
    "CONTRACT_VERSION",
    "SUPPORTED_CONTRACT_VERSIONS",
    "Attempt",
    "AuthorizationBounds",
    "Block",
    "BlockOptions",
    "CaptureLimits",
    "CapturedRun",
    "ClosedVerificationRound",
    "Duration",
    "EnvironmentFingerprint",
    "Evidence",
    "ExperienceRecord",
    "FinalizedExperience",
    "InitialLifecycleEvent",
    "Instant",
    "Message",
    "MessagePart",
    "Outcome",
    "Provenance",
    "RankedRecord",
    "RankedResult",
    "RankingComponent",
    "RawAttempt",
    "RawToolCall",
    "Reflection",
    "RequiredCheck",
    "RunCapture",
    "RunExposure",
    "Sanitization",
    "SanitizationLimits",
    "SanitizationPolicy",
    "SanitizationRejected",
    "Scope",
    "SearchParameters",
    "SearchRow",
    "SearchSafetyError",
    "ToolCall",
    "derive_task_text",
    "error_class",
    "experience_id_for",
    "finalize",
    "from_stored",
    "initial_event_id_for",
    "rank",
    "reflection_id_for",
    "render_block",
    "to_stored",
]
