"""
.NET parity and robustness beyond what the contract fixtures reach (contract 1.1 pins several of these cases too).
Each expectation below is what the .NET library does (checked against .NET 10), matched here by construction.
"""

from __future__ import annotations

import datetime as dt
import math
import uuid
from contextlib import contextmanager
from dataclasses import replace
from typing import Any

import pytest
from _contract import FIXTURES, read_json, run_finalize_case

from agentexperience import (
    BlockOptions,
    CaptureLimits,
    Duration,
    EnvironmentFingerprint,
    Instant,
    Provenance,
    RawAttempt,
    RawToolCall,
    RunCapture,
    RunExposure,
    Sanitization,
    SanitizationRejected,
    Scope,
    SearchParameters,
    SearchRow,
    SearchSafetyError,
    error_class,
    from_stored,
    rank,
)
from agentexperience._text import format_round_trip, ordinal_upper
from agentexperience.block import _clean, _value, ranked_from_json
from agentexperience.encoding import json_number
from agentexperience.ids import derive
from agentexperience.limits import TRUNCATION_PLACEHOLDER
from agentexperience.postgres import PostgresExperienceStore, to_driver_placeholders
from agentexperience.search import AuthorizationBounds
from agentexperience.verification import ClosedVerificationRound, RequiredCheck, aggregate

SCOPE = Scope("t", "a", "p")
T0 = Instant.parse("2026-03-02T09:00:00+00:00")
SECOND = Duration.from_seconds(1)


def _capture(limits: CaptureLimits | None = None, sanitization: Sanitization | None = None) -> RunCapture:
    return RunCapture.start(
        uuid.UUID(int=1),
        "task",
        None,
        SCOPE,
        EnvironmentFingerprint("h", "r", "o", None, {}),
        Provenance("s", None, T0, None),
        T0,
        limits=limits,
        sanitization=sanitization,
    )


def _attempt(n: int, **changes: Any) -> RawAttempt:
    attempt = RawAttempt(uuid.UUID(int=0x100 + n), T0, SECOND, [], result="ok")
    return replace(attempt, **changes)


def _call(index: int, **arguments: Any) -> RawToolCall:
    return RawToolCall(uuid.UUID(int=0x200 + index), "tool", arguments, T0, SECOND, result="r")


# ---- Whitespace is .NET's ---------------------------------------------------------------------------------------


def test_whitespace_is_dotnets_for_task_ids_check_ids_and_revisions() -> None:
    # U+001C is whitespace to Python's str.strip, not to .NET.
    assert _capture().run.task_id == "task"
    capture = RunCapture.start(
        uuid.UUID(int=1), "\x1c", None, SCOPE, EnvironmentFingerprint("h", "r", "o", None, {}),
        Provenance("s", None, T0, None), T0,
    )  # fmt: skip
    assert capture.run.task_id == "\x1c"
    with pytest.raises(ValueError):
        RunCapture.start(
            uuid.UUID(int=1), " ", None, SCOPE, EnvironmentFingerprint("h", "r", "o", None, {}),
            Provenance("s", None, T0, None), T0,
        )  # fmt: skip

    round_ = ClosedVerificationRound(uuid.UUID(int=9), "\x1c")
    assert aggregate([], [RequiredCheck("\x1c", "\x1c")], round_, "\x1c", T0).outcome.status == "Unknown"
    for blank in (" ", " "):
        with pytest.raises(ValueError):
            aggregate([], [], round_, blank, T0)
        with pytest.raises(ValueError):
            aggregate([], [RequiredCheck(blank, "k")], round_, "rev", T0)
        with pytest.raises(ValueError):
            aggregate([], [RequiredCheck("c", blank)], round_, "rev", T0)


# ---- The error class matches .NET's regular expression ------------------------------------------------------------


@pytest.mark.parametrize(
    ("error", "expected"),
    [
        ("exit\x1c2", "unclassified error"),  # U+001C is not .NET's \s
        ("exit 2", "exit 2"),
        ("x\x1cENOENT", "ENOENT"),  # nor a word character
        ("́ENOENT", "unclassified error"),  # a non-spacing mark is a word character
        ("ENOENT́", "unclassified error"),
        ("‍ENOENT", "unclassified error"),  # ZWJ counts for \b
        ("ःENOENT", "ENOENT"),  # a spacing mark is not a word character
        ("‿ENOENT", "unclassified error"),  # connector punctuation is
        ("ſtatus 503", "unclassified error"),  # the long s is not s
        ("STATUS 503", "status 503"),
        ("tımed out", "unclassified error"),  # the dotless i is not i
    ],
)
def test_error_class_matches_dotnet(error: str, expected: str) -> None:
    assert error_class(error) == expected


# ---- Ordinal-ignore-case is .NET's --------------------------------------------------------------------------------


def test_ordinal_upper_keeps_the_dotless_i_long_s_and_dotted_capital_i() -> None:
    assert ordinal_upper("ı") == "ı"
    assert ordinal_upper("ſ") == "ſ"
    assert ordinal_upper("İ") == "İ"
    assert ordinal_upper("i") == "I"


def test_labels_and_markers_are_matched_case_insensitively_as_dotnet_does() -> None:
    assert _clean("evidence: x") == "[delimiter removed] x"
    assert _clean("Evıdence: x") == "Evıdence: x"
    assert _clean("--- end withdrawn") == "[delimiter removed]"
    assert _clean("--- END WıTHDRAWN") == "--- END WıTHDRAWN"
    assert _clean("--- END WİTHDRAWN") == "--- END WİTHDRAWN"


def test_a_secret_name_is_matched_as_dotnet_matches_it() -> None:
    policy = Sanitization.allowing(["paſsword"]).tool_arguments
    assert policy.sanitize({"paſsword": "kept", "PASSWORD": "x"}) == {"paſsword": "kept", "PASSWORD": ""}


# ---- IDs, numbers, instants -----------------------------------------------------------------------------------


def test_a_lone_surrogate_in_a_scope_field_is_encoded_as_dotnet_encodes_it() -> None:
    run = uuid.UUID(int=1)
    assert derive(run, 1, Scope("t\ud800", "a", "p")) == derive(run, 1, Scope("t�", "a", "p"))


def test_negative_zero_keeps_its_sign() -> None:
    zero = json_number(-0.0)
    assert isinstance(zero, float) and math.copysign(1.0, zero) < 0
    assert format_round_trip(-0.0) == "-0"
    assert json_number(0.0) == 0 and isinstance(json_number(0.0), int)


@pytest.mark.parametrize(
    "text",
    [
        "2026-03-02T24:00:00+00:00",
        "2026-03-02T09:60:00+00:00",
        "2026-03-02T09:00:60+00:00",
        "2026-03-02T09:00:00+14:01",
        "2026-03-02T09:00:00+15:00",
        "2026-03-02T09:00:00+01:60",
    ],
)
def test_an_instant_out_of_range_is_refused(text: str) -> None:
    with pytest.raises(ValueError):
        Instant.parse(text)


def test_the_extreme_offsets_are_accepted() -> None:
    assert Instant.parse("2026-03-02T23:59:59-14:00").format() == "2026-03-03T13:59:59+00:00"


@pytest.mark.parametrize("text", ["24:00:00", "00:60:00", "00:00:60"])
def test_a_duration_out_of_range_is_refused(text: str) -> None:
    with pytest.raises(ValueError):
        Duration.parse(text)


@pytest.mark.parametrize(
    ("value", "text"),
    [(2**63 - 1, "9223372036854775807"), (10**20, "1E+20"), (10**400, "Infinity"), (-(10**400), "-Infinity")],
)
def test_an_integer_argument_beyond_64_bits_renders_as_dotnet_reads_it(value: int, text: str) -> None:
    assert _value(value) == text


# ---- Sanitization and capture -----------------------------------------------------------------------------------


_MAX_DOUBLE = (2**53 - 1) * 2**971


@pytest.mark.parametrize(
    "value",
    [math.nan, math.inf, -math.inf, 10**400, -(10**400), _MAX_DOUBLE + 2**970],
    ids=["nan", "inf", "-inf", "401-digit-int", "-401-digit-int", "max-double-midpoint"],
)
def test_a_non_finite_number_is_refused_at_capture(value: float) -> None:
    # As .NET does from contract 1.2 (contract/README-1.2.md): a NaN, an infinity, or a number a double cannot hold
    # finitely, an integer literal included, is refused; a secret or omitted field holding one is not read.
    policy = Sanitization.allowing(["n"]).tool_arguments
    with pytest.raises(SanitizationRejected):
        policy.sanitize({"n": value})
    with pytest.raises(SanitizationRejected):
        policy.sanitize({"n": [1, value]})
    # A secret or an omitted field is never read, so it does not refuse the payload.
    assert policy.sanitize({"token": value, "other": value}) == {"token": ""}
    capture = _capture(sanitization=Sanitization.allowing(["n"]))
    assert capture.append_attempt(_attempt(0, tool_calls=[_call(0, n=value)])) == "SanitizationRejected"
    assert capture.run.attempts == ()


def test_a_non_finite_refusal_names_the_path_never_the_value() -> None:
    policy = Sanitization.allowing(["n", "m"]).tool_arguments
    with pytest.raises(SanitizationRejected) as refused:
        policy.sanitize({"n": {"m": [1, 10**400]}})
    assert "'n.m[1]'" in str(refused.value)
    assert "1000" not in str(refused.value)


def test_a_number_just_below_the_rounding_midpoint_above_the_largest_double_is_kept() -> None:
    policy = Sanitization.allowing(["n"]).tool_arguments
    assert policy.sanitize({"n": _MAX_DOUBLE + 1}) == {"n": _MAX_DOUBLE + 1}
    assert policy.sanitize({"n": 1.7976931348623158e308}) == {"n": 1.7976931348623157e308}


def test_capture_answers_each_outcome() -> None:
    capture = _capture(limits=CaptureLimits(max_attempts_per_run=2))
    first = _attempt(0, tool_calls=[_call(0)])
    assert capture.append_attempt(first) == "Recorded"
    # The same attempt again, its tool calls a tuple rather than a list: the same content.
    assert capture.append_attempt(replace(first, tool_calls=(_call(0),))) == "DuplicateNoOp"
    assert capture.append_attempt(replace(first, result="different")) == "Conflict"
    assert capture.append_attempt(_attempt(1)) == "Recorded"
    assert capture.append_attempt(_attempt(2)) == "CapacityExceeded"
    assert [attempt.sequence_number for attempt in capture.run.attempts] == [0, 1]
    capture.complete("Completed", T0)
    assert capture.append_attempt(_attempt(3)) == "Conflict"


def test_an_overlong_result_or_error_is_replaced_by_the_placeholder() -> None:
    capture = _capture(limits=CaptureLimits(max_result_length=10, max_error_length=60))
    long = "x" * 61
    assert capture.append_attempt(_attempt(0, result=long, error=long)) == "Recorded"
    attempt = capture.run.attempts[0]
    assert attempt.result == TRUNCATION_PLACEHOLDER[:10]
    assert attempt.error == TRUNCATION_PLACEHOLDER
    capture = _capture()
    assert capture.append_attempt(_attempt(0, result="x" * 4000)) == "Recorded"
    assert capture.run.attempts[0].result == "x" * 4000


def test_capture_limits_must_be_positive() -> None:
    for name in ("max_attempts_per_run", "max_tool_calls_per_attempt", "max_result_length", "max_error_length"):
        with pytest.raises(ValueError):
            CaptureLimits(**{name: 0})


def test_exposures_keep_the_lowest_revision_and_at_most_256_records() -> None:
    capture = _capture()
    record = uuid.UUID(int=0x51)
    assert capture.record_exposure([RunExposure(record, 3)]) == "Recorded"
    assert capture.record_exposure([RunExposure(record, 5)]) == "DuplicateNoOp"
    assert capture.record_exposure([RunExposure(record, 1)]) == "Recorded"
    assert capture.run.provenance.exposed_to == (RunExposure(record, 1),)

    capture = _capture()
    many = [RunExposure(uuid.UUID(int=0x1000 + n), 1) for n in range(256)]
    assert capture.record_exposure(many) == "Recorded"
    assert capture.record_exposure([RunExposure(uuid.UUID(int=0x9999), 1)]) == "CapacityExceeded"
    assert len(capture.run.provenance.exposed_to) == 256
    with pytest.raises(ValueError):
        capture.record_exposure([RunExposure(uuid.UUID(int=0), 1)])


# ---- Block options ---------------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "arguments",
    [
        {"tool": ["a=b"]},
        {"tool": ["a,b"]},
        {"tool": ["a b"]},
        {"tool": ["k" * 65]},
        {"tool": ["k", "k"]},
        {"tool": [""]},
        {" ": ["k"]},
        {"tool": "key"},
    ],
)
def test_block_options_refuse_unsafe_approach_arguments(arguments: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        BlockOptions(approach_arguments=arguments)


def test_block_options_accept_a_64_unit_key() -> None:
    BlockOptions(approach_arguments={"tool": ["k" * 64, "nested.key"]})


def test_a_ranking_without_a_score_reads_as_nan() -> None:
    stored = read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "expected-record.json")
    ranked = ranked_from_json([{"record": stored, "ranking": {"score": None, "components": []}}, {"record": stored}])
    assert all(math.isnan(entry.score) for entry in ranked)


# ---- Ranking's safety checks and filters ----------------------------------------------------------------------


def _seed() -> list[Any]:
    return [
        from_stored(stored) for stored in read_json(FIXTURES / "search" / "more-than-the-candidate-limit" / "seed.json")
    ]


def test_rank_fails_the_whole_result_for_a_row_outside_the_scope() -> None:
    records = _seed()
    scope = records[0].scope
    rows = [SearchRow(record, 0.5) for record in records[:3]]
    rows[1] = SearchRow(replace(records[1], scope=replace(scope, agent_id="other")), 0.5)
    with pytest.raises(SearchSafetyError):
        rank(rows, scope, T0)
    rows[1] = SearchRow(records[1], 0.5, shared_by_grant=True)
    with pytest.raises(SearchSafetyError):
        rank(rows, scope, T0)


def test_rank_fails_on_a_duplicate_within_the_candidate_limit_only() -> None:
    records = _seed()
    scope = records[0].scope
    rows = [SearchRow(record, 0.5) for record in records[:50]]
    with pytest.raises(SearchSafetyError):
        rank([*rows[:49], rows[0]], scope, T0)
    # Row 51 is never considered: a duplicate there is dropped with it, and the result is truncated.
    result = rank([*rows, rows[0]], scope, T0)
    assert result.truncated
    assert len(result.ranked) == 50


def test_rank_leaves_out_ineligible_and_excluded_model_authored_rows() -> None:
    records = _seed()[:3]
    scope = records[0].scope
    assert records[2].reflection is not None
    rows = [
        SearchRow(records[0], 0.5),
        SearchRow(replace(records[1], status="Quarantined"), 0.5),
        SearchRow(replace(records[2], reflection=replace(records[2].reflection, authorship="Model")), 0.5),
    ]
    assert {entry.record.experience_id for entry in rank(rows, scope, T0).ranked} == {
        records[0].experience_id,
        records[2].experience_id,
    }
    excluded = rank(rows, scope, T0, exclude_model_authored=True)
    assert [entry.record.experience_id for entry in excluded.ranked] == [records[0].experience_id]
    assert not excluded.truncated


# ---- The PostgreSQL store, without a server ----------------------------------------------------------------------


def test_placeholders_skip_literals_identifiers_and_comments() -> None:
    assert to_driver_placeholders("SELECT '@a', @b") == "SELECT '@a', %(b)s"
    assert to_driver_placeholders("SELECT 'it''s @a', @b") == "SELECT 'it''s @a', %(b)s"
    assert to_driver_placeholders('SELECT "@a""x", @b') == 'SELECT "@a""x", %(b)s'
    assert to_driver_placeholders("SELECT /* @a /* @c */ @d */ @b") == "SELECT /* @a /* @c */ @d */ %(b)s"
    assert to_driver_placeholders("SELECT E'\\'@a', $$@c$$, $t$@d$t$, @b -- @e") == (
        "SELECT E'\\'@a', $$@c$$, $t$@d$t$, %(b)s -- @e"
    )
    assert to_driver_placeholders("SELECT '100%' -- 5%\n") == "SELECT '100%%' -- 5%%\n"


def _unreachable() -> Any:
    raise AssertionError("The store connected before checking its arguments.")


def test_insert_finalized_refuses_a_blank_actor_or_a_naive_recorded_at() -> None:
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    store = PostgresExperienceStore(_unreachable)
    bounds = AuthorizationBounds.of(finalized.record.scope)
    for actor in ("", " ", " "):
        with pytest.raises(ValueError):
            store.insert_finalized(bounds, finalized, actor=actor)
    with pytest.raises(ValueError):
        store.insert_finalized(bounds, finalized, actor="p", recorded_at=dt.datetime(2026, 3, 2))


class _Column:
    def __init__(self, name: str) -> None:
        self.name = name


class _FakeConnection:
    """Answers declare-bounds, then the search with one row whose relevance is NULL."""

    def __init__(self, row: tuple[Any, ...], names: list[str]) -> None:
        self.row, self.names = row, names
        self.description: list[_Column] | None = None

    def __enter__(self) -> _FakeConnection:
        return self

    def __exit__(self, *exc: object) -> None:
        return None

    @contextmanager
    def transaction(self) -> Any:
        yield

    def cursor(self) -> _FakeConnection:
        return self

    def execute(self, query: str, params: Any = None) -> None:
        self.description = [_Column(name) for name in self.names]

    def fetchall(self) -> list[tuple[Any, ...]]:
        return [self.row]


def test_search_refuses_a_row_with_no_relevance() -> None:
    from agentexperience.records import COLUMN_NAMES

    stored = read_json(FIXTURES / "search" / "coverage-filters-and-ranking" / "seed.json")[0]
    columns = stored["columns"]
    row = (*(columns.get(name) for name in COLUMN_NAMES[:-1]), stored["payload"], None, False, None, None)
    names = [*COLUMN_NAMES, "relevance", "shared_by_grant", "permitting_grant_id", "permitting_grant_disclosure"]
    store = PostgresExperienceStore(lambda: _FakeConnection(row, names))  # type: ignore[arg-type, return-value]
    scope = from_stored(stored).scope
    with pytest.raises(RuntimeError, match="no relevance"):
        store.search(AuthorizationBounds.of(scope), SearchParameters(scope, "refund"))
