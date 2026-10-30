"""Contract 1.19's recipe excerpt: kept on a tool call through read and write, and shown only where allowlisted."""

from __future__ import annotations

from typing import Any

import pytest
from _contract import FIXTURES, read_json

from agentexperience import BlockOptions, from_stored, render_block
from agentexperience.block import CALL_RETURNED_EXCERPT_PREFIX, ranked_from_json, render_lesson_text
from agentexperience.records import to_payload, to_stored

_RECIPE = FIXTURES / "inject" / "recipe-excerpt"


def _recipe_entries() -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = read_json(_RECIPE / "records.json")
    return entries


def test_a_recipe_excerpt_is_read_kept_and_written_back_exactly() -> None:
    stored = _recipe_entries()[0]["record"]
    record = from_stored(stored)
    excerpts = [call.recipe_excerpt for attempt in record.attempts for call in attempt.tool_calls]
    assert excerpts[0] == "pinned lib to 3.0.0"
    assert excerpts[-2] == " \t "
    assert excerpts[-1] == "\u200b\u2060\u00ad\ufe0f"
    assert to_stored(record) == stored


def test_a_call_without_an_excerpt_writes_no_member() -> None:
    stored = read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "expected-record.json")
    record = from_stored(stored)
    assert all(call.recipe_excerpt is None for attempt in record.attempts for call in attempt.tool_calls)
    payload = to_payload(record)
    assert all("recipeExcerpt" not in call for attempt in payload["attempts"] for call in attempt["toolCalls"])
    assert to_stored(record) == stored


@pytest.mark.parametrize(
    "tools",
    [
        [""],
        [" \t"],
        ["t" * 257],
        ["to\x07ol"],
        ["tool\n"],
        ["tool", "tool"],
        "tool",
    ],
)
def test_block_options_refuse_an_unsafe_recipe_allowlist(tools: Any) -> None:
    with pytest.raises(ValueError):
        BlockOptions(recipe_excerpts=tools)


def test_block_options_accept_and_snapshot_a_recipe_allowlist() -> None:
    tools = ["t" * 256, "write_config", "Write_Config"]
    options = BlockOptions(recipe_excerpts=tools)
    tools.append("later")
    assert options.recipe_excerpts == ("t" * 256, "write_config", "Write_Config")


def test_no_excerpt_is_shown_without_the_allowlist() -> None:
    block = render_block(ranked_from_json(_recipe_entries()), BlockOptions(rendering="Verbose"))
    assert CALL_RETURNED_EXCERPT_PREFIX not in block.text


def test_the_allowlist_is_matched_ordinally() -> None:
    block = render_block(ranked_from_json(_recipe_entries()), BlockOptions(recipe_excerpts=["Pin_Dependency"]))
    assert CALL_RETURNED_EXCERPT_PREFIX not in block.text
    block = render_block(ranked_from_json(_recipe_entries()), BlockOptions(recipe_excerpts=["pin_dependency"]))
    assert block.text.count(CALL_RETURNED_EXCERPT_PREFIX) == 1


def test_the_lesson_export_never_shows_an_excerpt() -> None:
    record = from_stored(_recipe_entries()[0]["record"])
    text = render_lesson_text(record, BlockOptions(recipe_excerpts=["pin_dependency", "write_config"]))
    assert "Tried:" in text
    assert CALL_RETURNED_EXCERPT_PREFIX not in text
