"""
The conformance runner: every fixture family of ``contract/`` through this package, compared exactly. Blocks and task
text byte for byte; JSON by value; search rows and scores (in ``test_postgres.py``) within the contract's tolerance.
"""

from __future__ import annotations

import hashlib
import json
from functools import cache
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

from agentexperience import (
    CONTRACT_VERSION,
    BlockOptions,
    Message,
    MessagePart,
    derive_task_text,
    error_class,
    from_stored,
    render_block,
    to_stored,
)
from agentexperience.block import ranked_from_json
from agentexperience.postgres import contract_sql


@cache
def validator() -> jsonschema.Draft202012Validator:
    schema = read_json(SCHEMA)
    jsonschema.Draft202012Validator.check_schema(schema)
    return jsonschema.Draft202012Validator(schema)


def assert_valid_record(stored: dict[str, Any]) -> None:
    errors = sorted(validator().iter_errors(stored), key=lambda error: list(error.absolute_path))
    assert not errors, "\n".join(f"{list(error.absolute_path)}: {error.message}" for error in errors)


def assert_json_equal(expected: Any, actual: Any, what: str) -> None:
    # Round-tripped through JSON text, so what is compared is what this package would write.
    difference = json_difference(expected, json.loads(json.dumps(actual)))
    assert difference is None, f"{what}: {difference}"


# ---- The lock and the version -------------------------------------------------------------------------------------


def test_the_contract_version_is_one_this_package_supports() -> None:
    assert check_contract_version(read_json(CONTRACT / "lock.json")) == CONTRACT_VERSION


@pytest.mark.parametrize("version", ["2.0", "1.1", None])
def test_an_unsupported_contract_version_fails_with_a_clear_message(version: str | None) -> None:
    with pytest.raises(UnsupportedContractVersion, match=r"supports only 1\.0"):
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


@pytest.mark.parametrize("name", ["declare-bounds.sql", "search-experience-text.sql"])
def test_the_packaged_sql_is_the_contract_sql(name: str) -> None:
    assert contract_sql(name) == (CONTRACT / "sql" / name).read_text(encoding="utf-8")


def test_every_fixture_family_has_cases() -> None:
    assert {entry.name for entry in FIXTURES.iterdir() if entry.is_dir()} == {
        "error-class",
        "finalize",
        "inject",
        "search",
        "task-text",
    }
    for family in ("finalize", "inject", "search", "task-text"):
        assert cases(family), family


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


def error_cases() -> list[tuple[str | None, str]]:
    return [(case["error"], case["class"]) for case in read_json(FIXTURES / "error-class" / "cases.json")]


@pytest.mark.parametrize(("error", "expected"), error_cases(), ids=lambda value: str(value)[:40])
def test_error_class(error: str | None, expected: str) -> None:
    assert error_class(error) == expected


# ---- search: the stored records (the search itself needs PostgreSQL) ---------------------------------------------


@pytest.mark.parametrize("name", cases("search"))
def test_search_seed_records_validate_and_read_back(name: str) -> None:
    for stored in read_json(FIXTURES / "search" / name / "seed.json"):
        assert_valid_record(stored)
        assert_json_equal(stored, to_stored(from_stored(stored)), "seed.json read back")
