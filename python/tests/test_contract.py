"""
The conformance runner: every fixture family of ``contract/`` through this package, compared exactly. Blocks and task
text byte for byte; JSON by value; search rows and scores (in ``test_postgres.py``) within the contract's tolerance.
"""

from __future__ import annotations

import copy
import hashlib
import json
import math
import re
import uuid
from collections.abc import Iterator
from functools import cache
from pathlib import Path
from typing import Any

import jsonschema
import pytest
from _contract import (
    CONTRACT,
    FIXTURES,
    SCHEMA,
    UnsupportedContractVersion,
    cases,
    check_contract_version,
    json_difference,
    read_json,
    run_finalize_case,
)
from jsonschema.exceptions import ValidationError

from agentexperience import (
    CONTRACT_VERSION,
    SUPPORTED_CONTRACT_VERSIONS,
    BlockOptions,
    ClosedVerificationRound,
    Duration,
    EnvironmentFingerprint,
    Instant,
    Message,
    MessagePart,
    Provenance,
    RequiredCheck,
    RunCapture,
    Scope,
    content_hash,
    derive_task_text,
    error_class,
    from_stored,
    merge_channels,
    render_block,
    retrieval_summary,
    to_stored,
)
from agentexperience.block import ranked_from_json
from agentexperience.ids import experience_id_for
from agentexperience.postgres import contract_sql
from agentexperience.records import Evidence
from agentexperience.verification import aggregate

# JSON Schema's patterns are ECMA-262 regular expressions, whose \s is not Python's: Python's also matches the
# information separators U+001C to U+001F (and U+0085), ECMA-262's does not. The schema's one \s (a task ID must hold
# a non-whitespace character) is evaluated as ECMA-262 defines it.
_ECMA_WHITESPACE = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff"


def _ecma(pattern: str) -> str:
    """``pattern`` (ECMA-262) as a Python pattern, for the constructs the schema uses; anything else is refused."""
    translated = pattern.replace("[^\\s]", f"[^{_ECMA_WHITESPACE}]")
    # ECMA-262's $ (without the multiline flag) matches only at the very end; Python's also before a final line break.
    if translated.endswith("$") and not translated.endswith("\\$"):
        translated = translated[:-1] + "\\Z"
    unsupported = re.findall(r"\\[sSdDwWbB]|(?<!\\)\$", translated)
    assert not unsupported, f"Translate {unsupported} in {pattern!r} for ECMA-262: Python's meaning differs."
    return translated


def _ecma_pattern(validator: Any, pattern: str, instance: Any, schema: Any) -> Iterator[ValidationError]:
    if not validator.is_type(instance, "string"):
        return
    if not re.search(_ecma(pattern), instance):
        yield ValidationError(f"{instance!r} does not match {pattern!r}")


_Validator = jsonschema.validators.extend(jsonschema.Draft202012Validator, {"pattern": _ecma_pattern})


@cache
def validator() -> Any:
    schema = read_json(SCHEMA)
    jsonschema.Draft202012Validator.check_schema(schema)
    return _Validator(schema)


def assert_valid_record(stored: dict[str, Any]) -> None:
    errors = sorted(validator().iter_errors(stored), key=lambda error: list(error.absolute_path))
    assert not errors, "\n".join(f"{list(error.absolute_path)}: {error.message}" for error in errors)


def assert_json_equal(expected: Any, actual: Any, what: str) -> None:
    # Round-tripped through JSON text, so what is compared is what this package would write.
    difference = json_difference(expected, json.loads(json.dumps(actual)))
    assert difference is None, f"{what}: {difference}"


# ---- The lock and the version -------------------------------------------------------------------------------------


def test_the_contract_version_is_one_this_package_supports() -> None:
    version = check_contract_version(read_json(CONTRACT / "lock.json"))
    assert version == CONTRACT_VERSION


@pytest.mark.parametrize("version", ["2.0", "1.15", None])
def test_an_unsupported_contract_version_fails_with_a_clear_message(version: str | None) -> None:
    with pytest.raises(
        UnsupportedContractVersion,
        match=r"supports only 1\.0, 1\.1, 1\.2, 1\.3, 1\.4, 1\.5, 1\.6, 1\.7, 1\.8, 1\.9, 1\.10, 1\.11, 1\.12, 1\.13, "
        r"1\.14",
    ):
        check_contract_version({"contractVersion": version})


def test_every_contract_file_matches_the_lock() -> None:
    lock = read_json(CONTRACT / "lock.json")
    files = lock["files"]
    on_disk = sorted(
        path.relative_to(CONTRACT).as_posix()
        for path in CONTRACT.rglob("*")
        if path.is_file()
        and path.name != "lock.json"
        and not any(part.startswith(".") for part in path.relative_to(CONTRACT).parts)
    )
    assert sorted(files) == on_disk
    for name, digest in files.items():
        assert hashlib.sha256((CONTRACT / name).read_bytes()).hexdigest() == digest, name


@pytest.mark.parametrize(
    "name",
    [
        "declare-bounds.sql",
        "search-experience-text.sql",
        "record-injection.sql",
        "write-experience-embedding.sql",
        "search-experience-vector.sql",
        "list-experience-lessons.sql",
        "record-holdout-assignment.sql",
        "query-holdout-outcomes.sql",
        "record-provider-holdout-assignment.sql",
        "query-provider-holdout-outcomes.sql",
    ],
)
def test_the_packaged_sql_is_the_contract_sql(name: str) -> None:
    assert contract_sql(name) == (CONTRACT / "sql" / name).read_text(encoding="utf-8")


def test_every_fixture_family_has_cases() -> None:
    assert {entry.name for entry in FIXTURES.iterdir() if entry.is_dir()} == {
        "benefit-estimate",
        "benefit-select",
        "holdout-outcomes",
        "model-metadata",
        "content-guard",
        "embedding-summary",
        "embedding-write",
        "encoding",
        "erased",
        "error-class",
        "finalize",
        "hybrid-merge",
        "holdout-assign",
        "holdout-record",
        "inject",
        "inject-options",
        "lesson-export",
        "lessons-html",
        "lessons-view",
        "provider-benefit-estimate",
        "provider-holdout-assign",
        "provider-holdout-record",
        "record-injection",
        "reflection-output",
        "reflection-prompt",
        "retrieval-policy",
        "search",
        "task-text",
        "vector-search",
    }
    for family in (
        "benefit-estimate",
        "benefit-select",
        "erased",
        "finalize",
        "holdout-outcomes",
        "holdout-record",
        "inject",
        "provider-benefit-estimate",
        "provider-holdout-record",
        "record-injection",
        "retrieval-policy",
        "search",
        "task-text",
    ):
        assert cases(family), family


# ---- Contract 1.6: the vector families that need no database -----------------------------------------------------


@pytest.mark.parametrize("index", range(len(read_json(FIXTURES / "embedding-summary" / "cases.json"))))
def test_embedding_summary(index: int) -> None:
    case = read_json(FIXTURES / "embedding-summary" / "cases.json")[index]
    summary = retrieval_summary(case["taskId"], case["taskSummary"], case["lesson"])
    assert summary == case["summary"], case["description"]
    assert content_hash(case["modelId"], summary) == case["contentHash"], case["description"]


def _nan(value: Any) -> float:
    return float("nan") if value == "NaN" else float(value)


def _same_relevance(expected: Any, actual: float) -> bool:
    return math.isnan(actual) if expected == "NaN" else actual == expected


@pytest.mark.parametrize("index", range(len(read_json(FIXTURES / "hybrid-merge" / "cases.json"))))
def test_hybrid_merge(index: int) -> None:
    case = read_json(FIXTURES / "hybrid-merge" / "cases.json")[index]

    def entries(channel: list[dict[str, Any]]) -> list[tuple[uuid.UUID, float]]:
        return [(uuid.UUID(item["experienceId"]), _nan(item["relevance"])) for item in channel]

    merged = merge_channels(
        entries(case["text"]),
        entries(case["vector"]),
        lambda item: item[0],
        lambda item: item[1],
        case["minimumVectorRelevance"],
    )
    assert [str(entry.item[0]) for entry in merged] == [item["experienceId"] for item in case["merged"]], case[
        "description"
    ]
    assert [entry.source for entry in merged] == [item["from"] for item in case["merged"]]
    for entry, expected in zip(merged, case["merged"], strict=True):
        assert _same_relevance(expected["relevance"], entry.relevance), (case["description"], expected, entry)


# ---- finalize ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("name", cases("finalize"))
def test_finalize(name: str) -> None:
    directory = FIXTURES / "finalize" / name
    finalized, capture = run_finalize_case(read_json(directory / "input.json"))
    stored = to_stored(finalized.record)

    assert_json_equal(read_json(directory / "expected-capture.json"), capture, "expected-capture.json")
    assert_json_equal(read_json(directory / "expected-record.json"), stored, "expected-record.json")
    assert_json_equal(read_json(directory / "expected-event.json"), finalized.event.to_json(), "expected-event.json")
    assert_valid_record(json.loads(json.dumps(stored)))


@pytest.mark.parametrize("name", cases("finalize"))
def test_a_finalized_record_reads_back_identical(name: str) -> None:
    expected = read_json(FIXTURES / "finalize" / name / "expected-record.json")
    assert_json_equal(expected, to_stored(from_stored(expected)), "read back")


def test_argument_numbers_read_back_as_dotnet_reads_them() -> None:
    stored = read_json(FIXTURES / "finalize" / "argument-numbers" / "expected-record.json")

    def arguments_of(record: dict[str, Any]) -> dict[str, Any]:
        return dict(from_stored(record).attempts[0].tool_calls[0].arguments)

    def with_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        changed: dict[str, Any] = copy.deepcopy(stored)
        changed["payload"]["attempts"][0]["toolCalls"][0]["arguments"] = arguments
        return changed

    arguments = arguments_of(stored)
    assert type(arguments["int64Max"]) is int and arguments["int64Max"] == 2**63 - 1
    assert type(arguments["int64Min"]) is int and arguments["int64Min"] == -(2**63)
    for name in ("beyondInt64", "belowInt64", "twentyOneDigits"):
        assert type(arguments[name]) is float, name
    written = to_stored(from_stored(stored))["payload"]["attempts"][0]["toolCalls"][0]["arguments"]
    assert json.dumps(written["beyondInt64"]) == "1.5e+19"
    assert json.dumps(written["belowInt64"]) == "-1e+19"
    assert json.dumps(written["twentyOneDigits"]) == "1e+20"
    assert json.dumps(written["int64Max"]) == "9223372036854775807"

    nested = arguments_of(with_arguments({"outer": {"big": 10**20, "items": [2**63, 1, {"small": -(2**63)}]}}))
    assert nested == {"outer": {"big": 1e20, "items": [9.223372036854776e18, 1, {"small": -(2**63)}]}}
    assert type(nested["outer"]["big"]) is float
    assert type(nested["outer"]["items"][0]) is float
    assert type(nested["outer"]["items"][1]) is int
    assert type(nested["outer"]["items"][2]["small"]) is int

    with pytest.raises(ValueError, match="outside the contract"):
        arguments_of(with_arguments({"huge": {"items": [10**400]}}))


# ---- inject -----------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("name", cases("inject"))
def test_inject(name: str) -> None:
    directory = FIXTURES / "inject" / name
    entries = read_json(directory / "records.json")
    options = read_json(directory / "options.json")
    for entry in entries:
        assert_valid_record(entry["record"])
        assert_json_equal(entry["record"], to_stored(from_stored(entry["record"])), "records.json read back")

    block = render_block(
        ranked_from_json(entries),
        BlockOptions(
            rendering=options["rendering"],
            failure_detail=options["failureDetail"],
            max_records=options["maxRecords"],
            max_bytes=options["maxBytes"],
            approach_arguments=options["approachArguments"],
        ),
    )
    expected = (directory / "expected-block.txt").read_bytes()
    assert block.text.encode("utf-8") == expected, f"expected:\n{expected.decode('utf-8')}\nactual:\n{block.text}"


# ---- task-text --------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("name", cases("task-text"))
def test_task_text(name: str) -> None:
    directory = FIXTURES / "task-text" / name
    case = read_json(directory / "messages.json")
    messages = [
        Message(
            role=message["role"],
            source=message.get("source"),
            contents=tuple(MessagePart(part["type"], part.get("text")) for part in message["contents"]),
            carries_block=bool(message.get("historicalReferenceStamp")),
        )
        for message in case["messages"]
    ]
    actual = derive_task_text(messages, case["maxLength"], case["followUpThreshold"])
    expected = read_json(directory / "expected.json")["taskText"]
    if expected is None:
        assert actual is None
    else:
        assert actual is not None
        assert actual.encode("utf-16-le", "surrogatepass") == expected.encode("utf-16-le", "surrogatepass")


# ---- error-class ------------------------------------------------------------------------------------------------


def _version_key(version: str) -> tuple[int, int]:
    major, minor = version.split(".")
    return int(major), int(minor)


def error_case_files() -> list[Path]:
    """
    Every ``cases*.json`` of the error-class family, in version order: ``cases.json`` is 1.0's, and each later minor
    version N adds ``cases-1.N.json``. A file for a version this package does not support fails clearly.
    """
    by_name = {
        ("cases.json" if version.endswith(".0") else f"cases-{version}.json"): version
        for version in SUPPORTED_CONTRACT_VERSIONS
    }
    on_disk = {file.name: file for file in (FIXTURES / "error-class").glob("cases*.json")}
    unknown = sorted(set(on_disk) - set(by_name))
    assert not unknown, (
        f"error-class holds {unknown}, for a contract version this package does not support "
        f"({', '.join(SUPPORTED_CONTRACT_VERSIONS)})."
    )
    return [on_disk[name] for name in sorted(on_disk, key=lambda name: _version_key(by_name[name]))]


def error_cases() -> list[tuple[str | None, str]]:
    return [(case["error"], case["class"]) for file in error_case_files() for case in read_json(file)]


def test_the_error_class_files_are_one_per_supported_version() -> None:
    assert [file.name for file in error_case_files()] == ["cases.json", "cases-1.1.json"]


@pytest.mark.parametrize(("error", "expected"), error_cases(), ids=lambda value: str(value)[:40])
def test_error_class(error: str | None, expected: str) -> None:
    assert error_class(error) == expected


# ---- search: the stored records (the search itself needs PostgreSQL) ---------------------------------------------


@pytest.mark.parametrize("family,name", [(family, name) for family in ("search", "erased") for name in cases(family)])
def test_search_seed_records_validate_and_read_back(family: str, name: str) -> None:
    for stored in read_json(FIXTURES / family / name / "seed.json"):
        assert_valid_record(stored)
        assert_json_equal(stored, to_stored(from_stored(stored)), "seed.json read back")


# ---- encoding (contract 1.1) ---------------------------------------------------------------------------------------


def encoding_cases() -> list[dict[str, Any]]:
    return list(read_json(FIXTURES / "encoding" / "cases.json"))


def _utf16(units: list[int] | None) -> str | None:
    """A scope field given as UTF-16 code units: a surrogate pair is one character, a lone surrogate stays one."""
    if units is None:
        return None
    for unit in units:
        if not 0 <= unit <= 0xFFFF:
            raise AssertionError(f"{unit} is not a UTF-16 code unit (0 to 65535).")
    return b"".join(unit.to_bytes(2, "little") for unit in units).decode("utf-16-le", "surrogatepass")


_T0 = Instant.parse("2026-03-02T09:00:00+00:00")
_ROUND = ClosedVerificationRound(uuid.UUID(int=0x7E), "rev-1")


def _encode(kind: str, value: Any) -> str:
    if kind == "instant":
        return Instant.parse(value).format()
    if kind == "duration":
        return Duration.parse(value).format()
    if kind == "non-blank":
        # Accepted, and the check it names is the one the evidence satisfies.
        evidence = Evidence(
            evidence_id=uuid.UUID(int=0xE1),
            verification_round_id=_ROUND.round_id,
            artifact_revision="rev-1",
            check_id=value,
            kind="integration-test",
            result="Pass",
            producer="contract-ci",
            detail=None,
            captured_at=_T0,
        )
        result = aggregate([evidence], [RequiredCheck(value, "integration-test")], _ROUND, "rev-1", _T0)
        assert result.outcome.status == "Verified"
        assert [item.check_id for item in result.outcome.evidence] == [value]
        return str(value)
    if kind == "task-id":
        capture = RunCapture.start(
            uuid.UUID(int=1), value, None, Scope("t", "a", "p"), EnvironmentFingerprint("h", "r", "o", None, {}),
            Provenance("s", None, _T0, None), _T0,
        )  # fmt: skip
        return capture.run.task_id
    if kind == "experience-id":
        scope = value["scopeUtf16"]
        # Passed through as the fixture gives them, a null included: Scope's own checks decide, as before.
        fields: list[Any] = [
            _utf16(scope[name]) for name in ("tenantId", "applicationId", "projectId", "teamId", "agentId", "userId")
        ]
        return str(experience_id_for(uuid.UUID(value["runId"]), Scope(*fields)))
    raise AssertionError(f"Unknown encoding kind {kind!r}.")


def _refusal(kind: str, value: Any) -> str:
    """What the refusal of an invalid case must say."""
    if kind in ("instant", "duration"):
        return re.escape(f"'{value}'")
    if kind == "non-blank":
        return "Required check IDs must not be blank"
    if kind == "task-id":
        return "task ID must not be blank"
    raise AssertionError(f"An {kind} case is never invalid.")


@pytest.mark.parametrize("case", encoding_cases(), ids=lambda case: f"{case['kind']}-{str(case['input'])[:40]}")
def test_encoding(case: dict[str, Any]) -> None:
    if case["valid"]:
        assert _encode(case["kind"], case["input"]) == case["canonical"]
    else:
        with pytest.raises(ValueError, match=_refusal(case["kind"], case["input"])):
            _encode(case["kind"], case["input"])
