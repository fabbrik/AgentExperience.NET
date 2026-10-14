"""The development identity's environment rule, name reduction, and the in-memory store's scope and search."""

from __future__ import annotations

from dataclasses import replace

import pytest
from _contract import FIXTURES, cases, read_json, run_finalize_case

from agentexperience import AuthorizationBounds, Scope, SearchParameters, rank
from agentexperience.identity import (
    DevelopmentOnlyError,
    development_identity,
    is_development_environment,
    reduce_name,
)
from agentexperience.inmemory import AuthorizationDenied, InMemoryExperienceStore, query_terms


@pytest.mark.parametrize(
    ("variables", "allowed"),
    [
        ({}, True),
        ({"DOTNET_ENVIRONMENT": "Development"}, True),
        ({"DOTNET_ENVIRONMENT": "test"}, True),
        ({"DOTNET_ENVIRONMENT": "TESTING"}, True),
        ({"DOTNET_ENVIRONMENT": "Production"}, False),
        ({"DOTNET_ENVIRONMENT": "Staging"}, False),
        ({"DOTNET_ENVIRONMENT": ""}, False),
        ({"ASPNETCORE_ENVIRONMENT": "Production"}, False),
        ({"ASPNETCORE_ENVIRONMENT": "Development"}, True),
        # DOTNET_ENVIRONMENT is read first, as in .NET.
        ({"DOTNET_ENVIRONMENT": "Development", "ASPNETCORE_ENVIRONMENT": "Production"}, True),
        ({"DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_ENVIRONMENT": "Development"}, False),
    ],
)
def test_the_environment_rule_is_the_dotnet_one(variables: dict[str, str], allowed: bool) -> None:
    assert is_development_environment(environment=variables) is allowed


def test_a_host_environment_name_is_checked_alone() -> None:
    assert is_development_environment("Development", environment={"DOTNET_ENVIRONMENT": "Production"})
    assert not is_development_environment("Production", environment={})


def test_the_development_identity_refuses_with_a_clear_message(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("DOTNET_ENVIRONMENT", raising=False)
    monkeypatch.setenv("ASPNETCORE_ENVIRONMENT", "Production")
    with pytest.raises(DevelopmentOnlyError) as refused:
        development_identity()
    assert "ASPNETCORE_ENVIRONMENT is 'Production'" in str(refused.value)
    with pytest.raises(DevelopmentOnlyError, match="the host environment is 'Staging'"):
        development_identity(environment_name="Staging")


def test_the_development_identity_is_one_fixed_local_caller(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("DOTNET_ENVIRONMENT", raising=False)
    monkeypatch.delenv("ASPNETCORE_ENVIRONMENT", raising=False)
    identity = development_identity(application="Refund Desk")
    assert identity.principal == "local-developer"
    assert identity.scope == Scope("local", "refund-desk", "refund-desk")
    assert identity.authorization == AuthorizationBounds("local", "refund-desk", "refund-desk")
    assert development_identity("acme", application="x").scope.tenant_id == "acme"
    with pytest.raises(ValueError):
        development_identity(" ")


@pytest.mark.parametrize(
    ("name", "reduced"),
    [
        ("Triage - Bot", "triage-bot"),
        ("Triage Bot", "triage-bot"),
        ("triage-bot", "triage-bot"),
        ("  --Ünïcode__Agent.v2-- ", "n-code__agent.v2"),
        ("!!!", None),
        (None, None),
        (" ", None),
        ("a" * 120, "a" * 100),
        ("a" * 99 + " b", "a" * 99),
    ],
)
def test_names_reduce_as_in_dotnet(name: str | None, reduced: str | None) -> None:
    assert reduce_name(name) == reduced


def test_the_in_memory_store_honours_bounds_and_scope_and_finds_a_lesson_by_its_task_text() -> None:
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    record = finalized.record
    store = InMemoryExperienceStore()
    bounds = AuthorizationBounds.of(record.scope)

    with pytest.raises(AuthorizationDenied):
        store.insert_finalized(AuthorizationBounds("another-tenant"), finalized, actor="p")
    store.insert_finalized(bounds, finalized, actor="p")
    with pytest.raises(ValueError):
        store.insert_finalized(bounds, finalized, actor="p")

    assert store.get(bounds, record.scope, record.experience_id) == record
    assert store.get(AuthorizationBounds("another-tenant"), record.scope, record.experience_id) is None
    other_scope = replace(record.scope, user_id="someone-else")
    assert store.get(bounds, other_scope, record.experience_id) is None

    rows = store.search(bounds, SearchParameters(record.scope, "refund ticket stuck in pending"))
    assert [row.record.experience_id for row in rows] == [record.experience_id]
    ranked = rank(rows, record.scope, record.updated_at)
    assert ranked.ranked[0].record == record

    # Another scope, another tenant's bounds, too few terms, or nothing searchable finds nothing.
    assert store.search(bounds, SearchParameters(other_scope, "refund ticket stuck in pending")) == []
    assert store.search(AuthorizationBounds("x"), SearchParameters(record.scope, "refund ticket stuck")) == []
    assert store.search(bounds, SearchParameters(record.scope, "refund weather forecast tomorrow")) == []
    assert store.search(bounds, SearchParameters(record.scope, "the and of")) == []


def test_the_in_memory_store_never_returns_a_quarantined_record() -> None:
    store = InMemoryExperienceStore()
    for name in cases("finalize"):
        finalized, _ = run_finalize_case(read_json(FIXTURES / "finalize" / name / "input.json"))
        if finalized.record.experience_id not in {r.experience_id for r in store.records()}:
            store.insert_finalized(AuthorizationBounds.of(finalized.record.scope), finalized, actor="p")
    scope = Scope("contract-tenant", "contract-app", "contract-project", agent_id="refund-agent")
    rows = store.search(AuthorizationBounds.of(scope), SearchParameters(scope, "Refund ticket is stuck in pending"))
    assert rows and all(row.record.status == "Validated" for row in rows)


def test_query_terms_drop_stopwords_short_words_and_duplicates() -> None:
    assert query_terms("The refund is STUCK, the refund: a 4812 x") == ["refund", "stuck", "4812"]


def test_the_python_variable_is_read_first_and_the_dotnet_ones_are_fallbacks() -> None:
    assert not is_development_environment(
        environment={"AGENTEXPERIENCE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Development"}
    )
    assert is_development_environment(
        environment={"AGENTEXPERIENCE_ENVIRONMENT": "Testing", "ASPNETCORE_ENVIRONMENT": "Production"}
    )


def test_the_in_memory_store_refuses_two_initial_records_under_one_id() -> None:
    finalized, _ = run_finalize_case(
        read_json(FIXTURES / "finalize" / "verified-after-one-failed-attempt" / "input.json")
    )
    with pytest.raises(ValueError):
        InMemoryExperienceStore([finalized.record, finalized.record])
