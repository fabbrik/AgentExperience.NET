"""
The receiving agent's capability gate (contract 1.13): .NET's ``ReceivingAgentCapabilities``, ``ToolRiskClass`` and
``CapabilityGate``. A record whose verified approach calls a tool the receiving agent lacks, or a tool riskier than it
may use, is kept out of the block (``ToolUnavailable``, ``RiskClassExceeded``, with no detail), before the record
limit, so it takes no slot. Passing the gate grants nothing: the host's approval boundary still decides every
tool call.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping
from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Any, Literal

from ._text import is_null_or_whitespace
from .block import approach_tool_calls
from .records import ExperienceRecord

ToolRiskClass = Literal["Low", "Medium", "High", "Critical"]
"""How risky the host judges one of its tools: .NET's ``ToolRiskClass``, ordered ``Low`` < ``Medium`` < ``High`` <
``Critical``. The library never infers it; a tool the host does not declare is ``Critical``."""

TOOL_RISK_CLASSES: tuple[ToolRiskClass, ...] = ("Low", "Medium", "High", "Critical")
"""Every ``ToolRiskClass``, in order (.NET's values 0 to 3)."""

ModelAuthoredLessonPolicy = Literal["Include", "Exclude"]
"""Whether model-authored lessons are injected (fenced and labelled) or omitted: .NET's
``ModelAuthoredLessonPolicy``."""

GateReason = Literal["ToolUnavailable", "RiskClassExceeded"]
"""Why the gate keeps a record out: .NET's ``InjectionOmissionReason`` names."""

DEFAULT_GATED_RETRIEVAL_WINDOW_MULTIPLIER = 4
"""``ReceivingAgentCapabilities.GatedRetrievalWindowMultiplier``'s default."""


def _rank(value: ToolRiskClass) -> int:
    return TOOL_RISK_CLASSES.index(value)


@dataclass(frozen=True)
class ReceivingAgent:
    """
    What the host declares about the agent receiving the block (.NET's ``ReceivingAgentCapabilities``).

    - ``available_tools``: the tools it has, compared ordinally; ``None`` (the default) skips the tool check, and an
      empty collection admits only approaches that call no tool.
    - ``max_risk_class``: the riskiest class it may be taught; ``None`` skips the risk check, and ``"Critical"``
      disables it.
    - ``tool_risk_classes``: the host's class per tool (a tool not listed is ``Critical``); read only with
      ``max_risk_class``.
    - ``use_run_tools``: check against the tools the invocation offers, intersected with ``available_tools`` when set.
    - ``gated_retrieval_window_multiplier``: while a gate can reject and the request names its own limit, retrieval is
      asked for ``max_records`` times this many records (at least 1; 4 by default).

    Validated and copied when built: a blank tool name, an undefined risk class or a multiplier below 1 raises
    ``ValueError``, and a later change to the collections passed in has no effect.
    """

    available_tools: Iterable[str] | None = None
    max_risk_class: ToolRiskClass | None = None
    tool_risk_classes: Mapping[str, ToolRiskClass] = field(default_factory=dict)
    use_run_tools: bool = False
    gated_retrieval_window_multiplier: int = DEFAULT_GATED_RETRIEVAL_WINDOW_MULTIPLIER

    def __post_init__(self) -> None:
        if self.available_tools is not None:
            if isinstance(self.available_tools, (str, bytes)):
                raise ValueError("ReceivingAgent.available_tools must be a collection of tool names, not a string.")
            tools = frozenset(self.available_tools)
            if any(not isinstance(tool, str) or is_null_or_whitespace(tool) for tool in tools):
                raise ValueError(
                    "ReceivingAgent.available_tools may not contain a null, empty or whitespace tool name."
                )
            object.__setattr__(self, "available_tools", tools)
        if self.max_risk_class is not None and self.max_risk_class not in TOOL_RISK_CLASSES:
            raise ValueError(f"ReceivingAgent.max_risk_class {self.max_risk_class!r} is not a defined ToolRiskClass.")
        classes: dict[str, ToolRiskClass] = {}
        for tool, risk in dict(self.tool_risk_classes or {}).items():
            if not isinstance(tool, str) or is_null_or_whitespace(tool):
                raise ValueError(
                    "ReceivingAgent.tool_risk_classes may not contain a null, empty or whitespace tool name."
                )
            if risk not in TOOL_RISK_CLASSES:
                raise ValueError(
                    f"ReceivingAgent.tool_risk_classes holds {risk!r}, which is not a defined ToolRiskClass."
                )
            classes[tool] = risk
        object.__setattr__(self, "tool_risk_classes", MappingProxyType(classes))
        if not isinstance(self.use_run_tools, bool):
            raise TypeError("ReceivingAgent.use_run_tools must be a bool.")
        multiplier = self.gated_retrieval_window_multiplier
        if isinstance(multiplier, bool) or not isinstance(multiplier, int) or multiplier < 1:
            raise ValueError(
                f"ReceivingAgent.gated_retrieval_window_multiplier must be at least 1; {multiplier!r} was set."
            )

    @property
    def gate(self) -> CapabilityGate:
        """The gate this declaration configures (before an invocation's run tools are known)."""
        tools = self.available_tools
        return CapabilityGate(
            None if tools is None else frozenset(tools),
            self.max_risk_class,
            self.tool_risk_classes,
            self.use_run_tools,
            self.gated_retrieval_window_multiplier,
        )


@dataclass(frozen=True)
class CapabilityGate:
    """The capability gate, as .NET's ``CapabilityGate`` snapshots it: build one through :attr:`ReceivingAgent.gate`."""

    available_tools: frozenset[str] | None
    max_risk_class: ToolRiskClass | None
    tool_risk_classes: Mapping[str, ToolRiskClass]
    use_run_tools: bool
    window_multiplier: int

    @property
    def can_reject(self) -> bool:
        """Whether it can keep any record out: a tool check (declared, or from the run's tools) or a risk check below
        ``Critical``. One that cannot is no gate: it neither checks a record nor widens retrieval."""
        return (
            self.available_tools is not None
            or self.use_run_tools
            or (self.max_risk_class is not None and _rank(self.max_risk_class) < _rank("Critical"))
        )

    def for_invocation(self, run_tools: Iterable[Any] | None) -> CapabilityGate:
        """The gate for one invocation: this one, unless ``use_run_tools``, in which case the available set is the names
        of ``run_tools`` (a name or an object with a ``name``; blank names ignored), intersected with the declared
        ``available_tools`` when there is one. A sequence that cannot be read counts as no tools."""
        if not self.use_run_tools:
            return self
        available: set[str] = set()
        try:
            for tool in run_tools or ():
                name = tool if isinstance(tool, str) else getattr(tool, "name", None)
                if (
                    isinstance(name, str)
                    and not is_null_or_whitespace(name)
                    and (self.available_tools is None or name in self.available_tools)
                ):
                    available.add(name)
        except Exception:
            available.clear()
        return CapabilityGate(
            frozenset(available), self.max_risk_class, self.tool_risk_classes, False, self.window_multiplier
        )

    def check(self, record: ExperienceRecord) -> GateReason | None:
        """Why ``record`` is kept out, or ``None`` when it passes: the tool check on its approach's calls, then the risk
        check. A record with no approach, or whose approach calls no tool, passes."""
        calls = approach_tool_calls(record)
        if not calls:
            return None
        if self.available_tools is not None:
            for call in calls:
                # A blank recorded name names no tool the agent could have: always unavailable.
                if is_null_or_whitespace(call.tool_name) or call.tool_name not in self.available_tools:
                    return "ToolUnavailable"
        if self.max_risk_class is not None:
            maximum = _rank(self.max_risk_class)
            for call in calls:
                risk = self.tool_risk_classes.get(call.tool_name, "Critical")
                if _rank(risk) > maximum:
                    return "RiskClassExceeded"
        return None


def active_gate(agent: ReceivingAgent | None, run_tools: Iterable[Any] | None = None) -> CapabilityGate | None:
    """The gate an invocation runs under: ``agent``'s for ``run_tools``, or ``None`` when there is no declaration or the
    gate can reject nothing."""
    if agent is None:
        return None
    gate = agent.gate.for_invocation(run_tools)
    return gate if gate.can_reject else None


def gated_window(limit: int | None, gate: CapabilityGate | None, max_records: int, candidate_limit: int) -> int | None:
    """The limit retrieval is asked for (.NET's ``GatedWindow``): ``limit`` itself, unless a gate that can reject is
    active and ``limit`` is within 1..``candidate_limit``, in which case ``min(candidate_limit, max(limit, max_records ×
    multiplier))``, so a gated record can be backfilled."""
    if gate is None or not gate.can_reject or limit is None:
        return limit
    if limit <= 0 or limit > candidate_limit:
        return limit
    return min(candidate_limit, max(limit, max_records * gate.window_multiplier))
