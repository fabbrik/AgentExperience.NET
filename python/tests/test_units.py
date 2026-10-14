"""Unit tests for the primitives the contract fixtures exercise only indirectly."""

from __future__ import annotations

import uuid

import pytest

from agentexperience import Duration, Instant, SearchParameters, error_class
from agentexperience._text import format_fixed, format_round_trip, utf16_len, utf16_prefix
from agentexperience.ids import derive
from agentexperience.postgres import to_driver_placeholders
from agentexperience.records import Scope
from agentexperience.sanitization import Sanitization, SanitizationRejected
from agentexperience.search import AuthorizationBounds, widen_real


@pytest.mark.parametrize(
    ("value", "text"),
    [
        (1.0, "1"),
        (0.5, "0.5"),
        (2 / 3, "0.6666666666666666"),
        (0.0001, "0.0001"),
        (0.00001, "1E-05"),
        (123456789012345.0, "123456789012345"),
        (1e15, "1E+15"),
        (-2.5, "-2.5"),
        (1.5e-7, "1.5E-07"),
    ],
)
def test_round_trip_format_is_dotnets(value: float, text: str) -> None:
    assert format_round_trip(value) == text


@pytest.mark.parametrize(
    ("value", "decimals", "text"),
    [(0.8374999999999999, 3, "0.838"), (0.125, 2, "0.13"), (2 / 3, 2, "0.67"), (1.0, 3, "1.000")],
)
def test_fixed_format_rounds_like_dotnet(value: float, decimals: int, text: str) -> None:
    assert format_fixed(value, decimals) == text


def test_lengths_and_cuts_are_in_utf16_code_units() -> None:
    assert utf16_len("a🧾b") == 4
    assert utf16_prefix("ab🧾", 3) == "ab"
    assert utf16_prefix("ab🧾", 4) == "ab🧾"


def test_instants_keep_seven_digits_and_write_the_utc_offset() -> None:
    instant = Instant.parse("2026-03-02T10:00:30.1234567+01:00")
    assert instant.format() == "2026-03-02T09:00:30.1234567+00:00"
    assert instant.truncate_to_microseconds().format() == "2026-03-02T09:00:30.123456+00:00"
    assert Instant.parse("2026-03-02T09:00:30Z").format() == "2026-03-02T09:00:30+00:00"
    assert Instant.from_datetime(instant.to_datetime()) == instant.truncate_to_microseconds()


@pytest.mark.parametrize("text", ["00:00:03.5000000", "00:00:01", "1.02:03:04", "-00:00:00.0000001"])
def test_durations_round_trip_in_the_constant_format(text: str) -> None:
    assert Duration.parse(text).format() == text


def test_a_null_scope_field_and_an_empty_one_derive_different_ids() -> None:
    run = uuid.UUID("000000f1-0000-4000-8000-000000000000")
    absent = derive(run, 1, Scope("t", "a", "p"))
    empty = derive(run, 1, Scope("t", "a", "p", team_id=""))
    assert absent != empty
    assert absent.version == 8


def test_sanitization_redacts_secrets_and_refuses_what_is_too_deep() -> None:
    policy = Sanitization.allowing(["a", "headers"]).tool_arguments
    assert policy.sanitize({"API_KEY": {"x": 1}, "headers": {"Token": "t", "other": "o"}, "b": 1}) == {
        "API_KEY": "",
        "headers": {"Token": ""},
    }
    with pytest.raises(SanitizationRejected):
        policy.sanitize({"a": {"a": {"a": {"a": {"a": "deep"}}}}})


def test_error_class_scans_only_the_first_8192_code_units() -> None:
    assert error_class("x" * 8192 + " ENOENT") == "unclassified error"
    assert error_class("x" * 8191 + " ENOENT") == "unclassified error"
    assert error_class("x" * 8180 + " ENOENT") == "ENOENT"


def test_search_parameters_refuse_overlong_or_blank_text_and_grants() -> None:
    scope = Scope("t", "a", "p")
    with pytest.raises(ValueError, match="refused, not cut"):
        SearchParameters(scope, "x" * 4097)
    with pytest.raises(ValueError):
        SearchParameters(scope, "   ")
    with pytest.raises(ValueError):
        SearchParameters(scope, "text", with_grants=True)
    assert SearchParameters(scope, "x" * 4096).parameters()["limit"] == 51


def test_bounds_are_declared_with_an_equals_prefix() -> None:
    assert AuthorizationBounds("t", "a").parameters() == {
        "auth_tenant": "=t",
        "auth_application": "=a",
        "auth_project": "",
        "auth_team": "",
        "auth_agent": "",
        "auth_user": "",
    }


def test_relevance_is_widened_from_float4_without_decimal_rounding() -> None:
    assert widen_real(0.6) == 0.6000000238418579
    assert widen_real(0.6000000238418579) == 0.6000000238418579


def test_placeholders_are_rewritten_in_code_only() -> None:
    sql = "-- @name stays, 100%\nSELECT f(p => @value) -- @other\nWHERE x LIKE 'a%'"
    assert (
        to_driver_placeholders(sql) == "-- @name stays, 100%%\nSELECT f(p => %(value)s) -- @other\nWHERE x LIKE 'a%%'"
    )


def test_the_version_is_the_installed_distributions() -> None:
    import re
    from pathlib import Path

    import agentexperience

    # pyproject.toml's version, which a release test holds to Directory.Build.props (0.1.0-preview.N is 0.1.0aN).
    pyproject = (Path(__file__).resolve().parents[1] / "pyproject.toml").read_text(encoding="utf-8")
    declared = re.search(r'^version = "([^"]+)"$', pyproject, re.MULTILINE)
    assert declared is not None
    assert agentexperience.__version__ == declared.group(1)
    assert re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(a[1-9][0-9]*)?", agentexperience.__version__)
