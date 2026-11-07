"""
Story 27.4b: the injection options in Python (contract 1.13). Every ``inject-options`` fixture (the request retrieval is
asked for, the selection, the block byte for byte, the role, every omission and the outcome), and the receiving
agent's validation and gate.
"""

from __future__ import annotations

from typing import Any

import pytest
from _benefit import estimate_of
from _contract import FIXTURES, cases, read_json

from agentexperience import (
    BenefitViews,
    BlockOptions,
    ReceivingAgent,
    RetrievalPolicy,
    gated_window,
    select_block,
)
from agentexperience.benefit_policy import OmittedRecord
from agentexperience.block import RankedRecord, ranked_from_json
from agentexperience.capabilities import active_gate
from agentexperience.maf import HOST_DENIED_DETAIL
from agentexperience.reflector import is_model_authored

FAMILY = FIXTURES / "inject-options"


def receiving_agent(declared: dict[str, Any] | None) -> ReceivingAgent | None:
    if declared is None:
        return None
    return ReceivingAgent(
        available_tools=declared["availableTools"],
        max_risk_class=declared["maxRiskClass"],
        tool_risk_classes=declared["toolRiskClasses"],
        use_run_tools=declared["useRunTools"],
        gated_retrieval_window_multiplier=declared["gatedRetrievalWindowMultiplier"],
    )


def run_case(name: str) -> tuple[dict[str, Any], str]:
    directory = FAMILY / name
    ranked = ranked_from_json(read_json(directory / "records.json"))
    options = read_json(directory / "options.json")
    block_options = BlockOptions(
        rendering=options["rendering"],
        failure_detail=options["failureDetail"],
        max_records=options["maxRecords"],
        max_bytes=options["maxBytes"],
        approach_arguments=options["approachArguments"],
    )
    gate = active_gate(receiving_agent(options["receivingAgent"]), options["runTools"])
    exclude = options["modelAuthoredLessons"] == "Exclude"
    view = None
    if (directory / "estimate.json").exists():
        views = BenefitViews.of(estimate_of(read_json(directory / "estimate.json")))
        view = views.for_model(None) if views is not None else None
    decisions: list[dict[str, Any]] = []
    host = options["hostDecisions"]

    def decide(candidate: RankedRecord) -> OmittedRecord | None:
        record = candidate.record
        decisions.append(
            {"experienceId": str(record.experience_id), "modelAuthored": is_model_authored(record.reflection)}
        )
        answer = host.get(str(record.experience_id))
        if answer is None or answer["permitted"]:
            return None
        return OmittedRecord(record.experience_id, "HostDenied", answer["reason"] or HOST_DENIED_DETAIL)

    selection = select_block(
        ranked,
        view,
        block_options,
        request_limit=options["requestLimit"],
        exclude_model_authored=exclude,
        prefer_measured_helpful=options["preferMeasuredHelpful"],
        gate=gate,
        decide=None if host is None else decide,
    )
    block = selection.block
    expected = {
        "outcome": "NothingToInject" if block.is_empty else "Injected",
        "messageRole": None if block.is_empty else options["messageRole"],
        "retrievalRequest": {
            "limit": gated_window(
                options["requestLimit"], gate, options["maxRecords"], RetrievalPolicy().candidate_limit
            ),
            "excludeModelAuthored": exclude,
        },
        "decisions": decisions,
        "injected": [str(record.experience_id) for record in block.included],
        "byteCount": block.byte_count,
        "omitted": [
            {"experienceId": str(item.experience_id), "reason": item.reason, "detail": item.detail}
            for item in selection.omitted
        ],
    }
    return expected, block.text


@pytest.mark.parametrize("name", cases("inject-options"))
def test_inject_options(name: str) -> None:
    actual, text = run_case(name)
    assert actual == read_json(FAMILY / name / "expected.json")
    expected_block = (FAMILY / name / "expected-block.txt").read_bytes()
    assert text.encode("utf-8") == expected_block, f"expected:\n{expected_block.decode('utf-8')}\nactual:\n{text}"


def test_the_inject_options_family_covers_every_case() -> None:
    assert len(cases("inject-options")) == 26


# ---- The receiving agent -------------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "options",
    [
        {"available_tools": ["read_ledger", " "]},
        {"available_tools": [""]},
        {"available_tools": "read_ledger"},
        {"max_risk_class": "Severe"},
        {"tool_risk_classes": {"": "Low"}},
        {"tool_risk_classes": {"read_ledger": "Severe"}},
        {"gated_retrieval_window_multiplier": 0},
        {"gated_retrieval_window_multiplier": -1},
    ],
)
def test_an_invalid_receiving_agent_is_refused(options: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        ReceivingAgent(**options)


def test_the_declaration_is_copied_when_built() -> None:
    tools = ["read_ledger"]
    classes = {"read_ledger": "Low"}
    agent = ReceivingAgent(available_tools=tools, max_risk_class="Low", tool_risk_classes=classes)  # type: ignore[arg-type]
    tools.append("drop_ledger")
    classes["drop_ledger"] = "Low"
    assert agent.available_tools == frozenset({"read_ledger"})
    assert dict(agent.tool_risk_classes) == {"read_ledger": "Low"}


def test_defaults_are_dotnets() -> None:
    agent = ReceivingAgent()
    assert agent.available_tools is None and agent.max_risk_class is None and not agent.use_run_tools
    assert agent.gated_retrieval_window_multiplier == 4
    # A declaration that can reject nothing is no gate at all.
    assert active_gate(agent) is None
    assert active_gate(ReceivingAgent(max_risk_class="Critical")) is None
    assert active_gate(ReceivingAgent(max_risk_class="High")) is not None
    assert active_gate(ReceivingAgent(use_run_tools=True)) is not None


def test_the_gated_window() -> None:
    gate = active_gate(ReceivingAgent(max_risk_class="Low"))
    assert gated_window(None, gate, 8, 50) is None
    assert gated_window(2, gate, 8, 50) == 32
    assert gated_window(2, gate, 20, 50) == 50
    assert gated_window(40, gate, 8, 50) == 40
    assert gated_window(0, gate, 8, 50) == 0
    assert gated_window(51, gate, 8, 50) == 51
    assert gated_window(2, None, 8, 50) == 2


def test_run_tools_read_names_and_objects() -> None:
    class Tool:
        def __init__(self, name: str) -> None:
            self.name = name

    gate = ReceivingAgent(use_run_tools=True).gate.for_invocation([Tool("a"), "b", " ", Tool("")])
    assert gate.available_tools == frozenset({"a", "b"})
    assert not gate.use_run_tools


def test_a_run_tools_gate_not_narrowed_to_an_invocation_fails_closed() -> None:
    from agentexperience.capabilities import CapabilityGate

    gate = ReceivingAgent(use_run_tools=True).gate
    assert isinstance(gate, CapabilityGate)
    case = FAMILY / "run-tools"
    ranked = ranked_from_json(read_json(case / "records.json"))
    with_tools = [item for item in ranked if item.record.attempts and item.record.attempts[-1].tool_calls]
    assert with_tools and all(gate.check(item.record) == "ToolUnavailable" for item in with_tools)
    # Narrowed to an invocation offering the tools, the same gate passes them.
    names = {call.tool_name for item in with_tools for call in item.record.attempts[-1].tool_calls}
    assert all(gate.for_invocation(names).check(item.record) is None for item in with_tools)
