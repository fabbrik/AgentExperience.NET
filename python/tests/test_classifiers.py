"""The tool-result classifiers, with the cases of .NET's ``ExperienceVerifiersTests`` (story 21.2)."""

from __future__ import annotations

import dataclasses
import json
import uuid
from types import SimpleNamespace
from typing import Any

import pytest

from agentexperience.classifiers import TOOL_REPORTED_ERROR_CLASS, ToolResultContext, exit_code, http_status
from agentexperience.error_class import is_single_token

RUN_TESTS = "run_tests"


def result(value: Any, tool: str = RUN_TESTS) -> ToolResultContext:
    return ToolResultContext(uuid.uuid4(), tool, {}, value)


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("exit=0", None),
        ("exit=3", "exit 3"),
        ('{"exitCode":2}', "exit 2"),
        ("EXIT CODE 4", "exit 4"),
        ("ok", None),
        ("exit 7", "exit 7"),
        ("tests finished, exit code: 12", "exit 12"),
        ('{"exit_code": 5, "stdout": "exit 0"}', "exit 5"),
        ('{"ExitCode":"0"}', None),
        ('{"stdout":"exit 3"}', None),
        ('Result: {"exitCode": 9, truncated', "exit 9"),
        ("exit=-1", "exit -1"),
        ("exit=007", "exit 7"),
        ("exit=000", None),
        ("exit=1073741819", TOOL_REPORTED_ERROR_CLASS),
        ("exit=123456789012345678901234567890", TOOL_REPORTED_ERROR_CLASS),
        ("exit 1.0", TOOL_REPORTED_ERROR_CLASS),
        ("exit 0.0", None),
        ('{"exitCode":1.0}', TOOL_REPORTED_ERROR_CLASS),
        ('{"exitCode":1e0}', TOOL_REPORTED_ERROR_CLASS),
        ('{"exitCode":0.0}', None),
        ('{"exitCode":123456789012345678901234567890}', TOOL_REPORTED_ERROR_CLASS),
        ("step: exit 0, then more work... final: exit=1", "exit 1"),
        ("exit status 2", "exit 2"),
        ("process exited with code 3", "exit 3"),
        ("returncode=4", "exit 4"),
        ('{"returncode": 5}', "exit 5"),
        ('{"return_code": 6}', "exit 6"),
        ('{"returnCode": "7"}', "exit 7"),
        ('partial {"returnCode": 8, truncated', "exit 8"),
        ('{"exitCode":"n/a","exit_code":3}', "exit 3"),
        ('[{"exitCode":2}]', None),
        ('["exit=3"]', None),
        ("", None),
    ],
)
def test_exit_code_classifies_a_non_zero_exit_code(text: str, expected: str | None) -> None:
    assert exit_code()(result(text)) == expected


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("HTTP 503", "HTTP 503"),
        ('{"status":200}', None),
        ('{"statusCode":404}', "HTTP 404"),
        ("HTTP/1.1 500 Internal Server Error", "HTTP 500"),
        ("status 429", "HTTP 429"),
        ("Status code: 401", "HTTP 401"),
        ("http 302", None),
        ("the page said HTTP 503", None),
        ('{"Status":"502"}', "HTTP 502"),
        ('{"status":"ok"}', None),
        ('{"status":700}', None),
        ('{"status":"error","statusCode":500}', "HTTP 500"),
        ('{"status":"failed","status_code":"503"}', "HTTP 503"),
        ('[{"statusCode":404}]', None),
        ("ok", None),
    ],
)
def test_http_status_classifies_an_error_status(text: str, expected: str | None) -> None:
    assert http_status()(result(text)) == expected


@dataclasses.dataclass
class ProcessOutcome:
    exitCode: int
    stdout: str


@dataclasses.dataclass
class HttpOutcome:
    statusCode: int


@dataclasses.dataclass
class LateOutcome:
    stdout: str
    exitCode: int


def test_both_classifiers_read_a_mapping_and_a_serialized_object() -> None:
    assert exit_code()(result({"exitCode": 2})) == "exit 2"
    assert exit_code()(result(json.dumps("exit=3"))) == "exit 3"
    assert http_status()(result({"statusCode": 404})) == "HTTP 404"
    assert exit_code()(result(ProcessOutcome(6, "boom"))) == "exit 6"
    assert exit_code()(result(ProcessOutcome(0, "exit 3"))) is None
    assert http_status()(result(HttpOutcome(503))) == "HTTP 503"
    assert http_status()(result(HttpOutcome(204))) is None
    assert exit_code()(result(None)) is None
    assert exit_code()(result(42)) is None


def test_a_json_object_is_read_by_its_properties_whatever_its_size_and_text_by_its_last_match() -> None:
    output = "x" * 9000 + " exit 0"
    text = json.dumps({"stdout": output, "exitCode": 1})
    assert len(text) > 8192 and text.index("exitCode") > 8192

    assert exit_code()(result(text)) == "exit 1"
    assert exit_code()(result(json.loads(text))) == "exit 1"
    assert exit_code()(result(LateOutcome(output, 1))) == "exit 1"
    assert exit_code()(result(LateOutcome("x" * 9000 + " exit 3", 0))) is None

    # Text past 1 MiB: the last 64 KiB still holds the final exit code.
    huge = "exit 0\n" + "y" * (2 * 1024 * 1024) + "\nexit=2"
    assert exit_code()(result(huge)) == "exit 2"
    assert exit_code()(result("z" * 20_000 + " exit=2")) == "exit 2"


def test_text_content_is_read_and_a_json_array_names_no_code_in_either_form() -> None:
    def text(value: str) -> Any:
        return SimpleNamespace(type="text", text=value)

    assert exit_code()(result(text("exit=3"))) == "exit 3"
    assert exit_code()(result([text("running"), text("exit=3")])) == "exit 3"
    assert http_status()(result(text("HTTP 502 Bad Gateway"))) == "HTTP 502"
    assert exit_code()(result(SimpleNamespace(type="image", text=None))) is None

    array = '[{"exitCode":2},"exit=3"]'
    assert exit_code()(result(array)) is None
    assert exit_code()(result(json.loads(array))) is None
    assert exit_code()(result([ProcessOutcome(2, "x")])) is None


def test_given_tool_names_a_classifier_applies_to_those_tools_only() -> None:
    exit_ = exit_code(RUN_TESTS, "build")
    http = http_status("fetch")

    assert exit_(result("exit=3", RUN_TESTS)) == "exit 3"
    assert exit_(result("exit=3", "build")) == "exit 3"
    assert exit_(result("exit=3", "Run_Tests")) is None
    assert exit_(result("exit=3", "grep")) is None
    assert http(result("HTTP 404", "fetch")) == "HTTP 404"
    assert http(result("HTTP 404", RUN_TESTS)) is None
    assert exit_code()(result("exit=3", "anything")) == "exit 3"
    with pytest.raises(ValueError):
        exit_code(RUN_TESTS, " ")


def test_every_class_the_classifiers_return_is_a_single_error_class_token() -> None:
    exits = ["exit=1", "exit=-1", "exit=999999", "exit=-999999", "exit=1234567", '{"exitCode":255}', "exit 1.5"]
    exits.append('{"exitCode":1e3}')
    statuses = ["HTTP 400", "HTTP 599", "status 500", '{"status":404}']
    classes = [exit_code()(result(value)) for value in exits] + [http_status()(result(value)) for value in statuses]
    for value in classes:
        assert value is not None and is_single_token(value), value


@pytest.mark.parametrize(
    ("value", "single"),
    [
        ("exit 3", True),
        ("QuotaExceededError", True),
        ("HTTP 503", True),
        ("status 429", True),
        ("ENOENT", True),
        ("Timeout", True),
        ("TimeoutException", True),
        (TOOL_REPORTED_ERROR_CLASS, True),
        ("Exit 3", False),
        ("exit code 3", False),
        ("lockHeldError", False),
        ("http 503", False),
        ("ExitCode3", False),
        ("a sentence", False),
        ("", False),
        ("exit 3, HTTP 503", False),
    ],
)
def test_only_a_whole_single_token_counts_as_an_error_class(value: str, single: bool) -> None:
    assert is_single_token(value) is single


def test_a_match_running_into_the_cut_of_a_long_texts_head_does_not_count() -> None:
    # The head of a text over 1 MiB ends at 64 KiB: "exit=12" cut there reads "exit=1", which must not count.
    head = "x" * (64 * 1024 - len("exit=1")) + "exit=12"
    text = head + "y" * (2 * 1024 * 1024)
    assert exit_code()(result(text)) is None
    # A match that ends at the text's own end still counts.
    assert exit_code()(result("z" * (2 * 1024 * 1024) + " exit=7")) == "exit 7"
