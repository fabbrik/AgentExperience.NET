"""
Contract 1.12: the retrieval policy. The ``retrieval-policy`` fixtures (ranking under .NET's ``RetrievalPolicy`` and
``ConfidenceDecayPolicy``) and ``policy-validation.json`` (which policies .NET accepts, and how it refuses the rest).
"""

from __future__ import annotations

import math
import uuid
from datetime import timedelta
from typing import Any

import pytest
from _contract import FIXTURES, cases, json_difference, read_json, scope_of

from agentexperience import (
    ConfidenceDecayPolicy,
    Instant,
    RankedResult,
    RetrievalPolicy,
    RetrievalPolicyError,
    SearchParameters,
    SearchRow,
    from_stored,
    rank,
)
from agentexperience.embedding import rank_hybrid
from agentexperience.retrieval import timedelta_of_ticks

FAMILY = FIXTURES / "retrieval-policy"
TOLERANCE = 1e-12


def _number(value: Any) -> float:
    return math.nan if value == "NaN" else float(value)


def _ticks(value: Any) -> timedelta | None:
    return None if value is None else timedelta_of_ticks(int(value))


def policy_of(value: dict[str, Any]) -> RetrievalPolicy:
    """``policy.json``'s ``policy`` as this port builds it; raises as .NET refuses."""
    floor = value["minimumVectorRelevance"]
    timeout = _ticks(value["timeoutTicks"])
    half_life = _ticks(value["recencyHalfLifeTicks"])
    assert timeout is not None and half_life is not None
    return RetrievalPolicy(
        timeout=timeout,
        minimum_confidence=_number(value["minimumConfidence"]),
        max_age=_ticks(value["maxAgeTicks"]),
        recency_half_life=half_life,
        candidate_limit=int(value["candidateLimit"]),
        max_abandoned_searches=int(value["maxAbandonedSearches"]),
        minimum_matched_terms=int(value["minimumMatchedTerms"]),
        minimum_vector_relevance=None if floor is None else _number(floor),
    )


def decay_of(value: dict[str, Any] | None) -> ConfidenceDecayPolicy | None:
    """``confidenceDecay`` as this port builds it (``None``: none); raises as .NET refuses."""
    if value is None:
        return None
    return ConfidenceDecayPolicy(
        domain_key=value["domainKey"],
        half_lives={domain: _ticks(ticks) for domain, ticks in value["halfLivesTicks"].items()},
        default_half_life=_ticks(value["defaultHalfLifeTicks"]),
    )


def ranked_json(result: RankedResult) -> dict[str, Any]:
    """A ranked result as ``expected.json`` holds one (without ``outcome`` and ``query``)."""
    return {
        "truncated": result.truncated,
        "ranked": [
            {
                "experienceId": str(entry.record.experience_id),
                "score": entry.score,
                "components": [
                    {
                        "kind": component.kind,
                        "value": component.value,
                        "weight": component.weight,
                        "undecayedValue": component.undecayed_value,
                    }
                    for component in entry.components
                ],
            }
            for entry in result.ranked
        ],
        "excluded": [{"experienceId": str(entry.experience_id), "reason": entry.reason} for entry in result.excluded],
    }


def _case(name: str) -> tuple[list[SearchRow], dict[str, Any], dict[str, Any]]:
    directory = FAMILY / name
    rows = [
        SearchRow(from_stored(entry["record"]), float(entry["relevance"]))
        for entry in read_json(directory / "candidates.json")
    ]
    return rows, read_json(directory / "policy.json"), read_json(directory / "expected.json")


@pytest.mark.parametrize("name", cases("retrieval-policy"))
def test_retrieval_policy(name: str) -> None:
    rows, request, expected = _case(name)
    policy = policy_of(request["policy"])
    decay = decay_of(request["confidenceDecay"])
    scope = scope_of(request["scope"])

    # What the store is asked for, under this policy.
    parameters = SearchParameters.for_policy(
        scope, request["taskText"], policy, exclude_model_authored=request["excludeModelAuthored"]
    )
    query = {
        "minConfidence": parameters.min_confidence,
        "limit": parameters.limit,
        "minimumMatchedTerms": parameters.minimum_matched_terms,
        "excludeModelAuthored": parameters.exclude_model_authored,
    }
    assert json_difference(expected["query"], query, "$.query") is None, query

    # The store answers with at most the limit it was asked for, as the statement's LIMIT cuts.
    answered = rows[: parameters.limit]
    result = rank(
        answered,
        scope,
        Instant.parse(request["now"]),
        request["preferredEnvironment"],
        exclude_model_authored=request["excludeModelAuthored"],
        policy=policy,
        confidence_decay=decay,
    )
    assert expected["outcome"] == "Completed"
    actual = ranked_json(result)
    for key in ("truncated", "ranked", "excluded"):
        difference = json_difference(expected[key], actual[key], f"$.{key}", TOLERANCE)
        assert difference is None, difference

    # The hybrid path, with no vector candidates, ranks the same.
    hybrid = rank_hybrid(
        answered,
        [],
        scope,
        Instant.parse(request["now"]),
        request["preferredEnvironment"],
        exclude_model_authored=request["excludeModelAuthored"],
        policy=policy,
        confidence_decay=decay,
    )
    assert ranked_json(hybrid) == actual


def test_the_default_policy_ranks_exactly_as_no_policy() -> None:
    rows, request, _ = _case("default-policy")
    assert policy_of(request["policy"]) == RetrievalPolicy()
    assert request["confidenceDecay"] is None
    now = Instant.parse(request["now"])
    scope = scope_of(request["scope"])
    with_policy = rank(rows, scope, now, request["preferredEnvironment"], policy=RetrievalPolicy())
    without = rank(rows, scope, now, request["preferredEnvironment"])
    assert ranked_json(with_policy) == ranked_json(without)
    assert SearchParameters.for_policy(scope, "rotate keys please") == SearchParameters(scope, "rotate keys please")


def _validation_cases() -> list[dict[str, Any]]:
    cases_: list[dict[str, Any]] = read_json(FAMILY / "policy-validation.json")
    return cases_


@pytest.mark.parametrize("case", _validation_cases(), ids=lambda case: str(case["description"]))
def test_policy_validation(case: dict[str, Any]) -> None:
    refusal = case["refusal"]
    if refusal is None:
        policy_of(case["policy"])
        decay_of(case["confidenceDecay"])
        return
    with pytest.raises(RetrievalPolicyError) as raised:
        policy_of(case["policy"])
        decay_of(case["confidenceDecay"])
    assert raised.value.option == refusal["parameter"]


# ---- Units ----------------------------------------------------------------------------------------------------------


def test_the_decay_map_is_copied_and_read_only() -> None:
    half_lives: dict[str, timedelta | None] = {"framework-api": timedelta(days=30)}
    decay = ConfidenceDecayPolicy(half_lives=half_lives)
    half_lives["framework-api"] = None
    half_lives["security"] = timedelta(days=1)
    assert dict(decay.half_lives) == {"framework-api": timedelta(days=30)}
    with pytest.raises(TypeError):
        decay.half_lives["x"] = None  # type: ignore[index]
    assert decay.half_life_for("Framework-API") is None
    assert decay.half_life_for(None) is None
    assert hash(decay) == hash(ConfidenceDecayPolicy(half_lives={"framework-api": timedelta(days=30)}))


def test_a_mapped_none_never_decays_whatever_the_default() -> None:
    decay = ConfidenceDecayPolicy(half_lives={"math": None}, default_half_life=timedelta(days=1))
    assert decay.half_life_for("math") is None
    assert decay.half_life_for("ops") == timedelta(days=1)


def test_the_statement_limit_is_one_more_than_the_candidate_limit() -> None:
    assert RetrievalPolicy(candidate_limit=3).statement_limit == 4
    assert RetrievalPolicy().statement_limit == 51


def test_ticks_that_are_not_whole_microseconds_are_refused() -> None:
    with pytest.raises(ValueError):
        timedelta_of_ticks(5)


def test_an_exclusion_is_listed_once_in_the_rows_order() -> None:
    rows, request, expected = _case("max-age")
    result = rank(
        rows,
        scope_of(request["scope"]),
        Instant.parse(request["now"]),
        policy=policy_of(request["policy"]),
    )
    assert [str(entry.experience_id) for entry in result.excluded] == [
        entry["experienceId"] for entry in expected["excluded"]
    ]
    assert all(isinstance(entry.experience_id, uuid.UUID) for entry in result.excluded)
