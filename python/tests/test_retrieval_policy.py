"""
Contract 1.12: the retrieval policy. The ``retrieval-policy`` fixtures (ranking under .NET's ``RetrievalPolicy`` and
``ConfidenceDecayPolicy``) and ``policy-validation.json`` (which policies .NET accepts, and how it refuses the rest).
"""

from __future__ import annotations

import math
import uuid
from datetime import timedelta
from pathlib import Path
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
from agentexperience.embedding import VectorQuery, rank_hybrid, vector_query_for_policy
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


def _rows(path: Path) -> list[SearchRow]:
    return [SearchRow(from_stored(entry["record"]), float(entry["relevance"])) for entry in read_json(path)]


def _case(name: str) -> tuple[list[SearchRow], dict[str, Any], dict[str, Any]]:
    directory = FAMILY / name
    return (
        _rows(directory / "candidates.json"),
        read_json(directory / "policy.json"),
        read_json(directory / "expected.json"),
    )


def _vector_rows(name: str) -> list[SearchRow] | None:
    """A hybrid case's ``vector-candidates.json``, or ``None`` for a text-only case."""
    path = FAMILY / name / "vector-candidates.json"
    return _rows(path) if path.exists() else None


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
    now = Instant.parse(request["now"])
    options: dict[str, Any] = {
        "exclude_model_authored": request["excludeModelAuthored"],
        "policy": policy,
        "confidence_decay": decay,
    }
    vector = _vector_rows(name)
    if vector is None:
        assert "vectorQuery" not in expected
        result = rank(answered, scope, now, request["preferredEnvironment"], **options)
        # The hybrid path, with no vector candidates, ranks the same.
        assert ranked_json(rank_hybrid(answered, [], scope, now, request["preferredEnvironment"], **options)) == (
            ranked_json(result)
        )
    else:
        # Hybrid: what the index is asked for, and an index that answers at most that limit.
        vector_query = vector_query_for_policy(
            scope, "fixture-model", (1.0,), policy, exclude_model_authored=request["excludeModelAuthored"]
        )
        asked = {
            "minConfidence": vector_query.min_confidence,
            "limit": vector_query.limit,
            "excludeModelAuthored": vector_query.exclude_model_authored,
        }
        assert json_difference(expected["vectorQuery"], asked, "$.vectorQuery") is None, asked
        assert vector, "a hybrid case has vector candidates"
        result = rank_hybrid(
            answered, vector[: vector_query.limit], scope, now, request["preferredEnvironment"], **options
        )
    assert expected["outcome"] == "Completed"
    actual = ranked_json(result)
    for key in ("truncated", "ranked", "excluded"):
        difference = json_difference(expected[key], actual[key], f"$.{key}", TOLERANCE)
        assert difference is None, difference


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


def test_the_hybrid_cases_merge_real_vector_rows() -> None:
    hybrid = [name for name in cases("retrieval-policy") if _vector_rows(name) is not None]
    assert sorted(hybrid) == [
        "hybrid-floor-before-truncation",
        "hybrid-per-channel-candidate-limit",
        "hybrid-vector-floor-from-the-policy",
    ]
    for name in hybrid:
        text, _, expected = _case(name)
        vector = _vector_rows(name)
        assert vector is not None
        text_ids = {str(row.record.experience_id) for row in text}
        # Some record ranks from the vector channel alone: the merge is exercised, not only the text.
        assert any(entry["experienceId"] not in text_ids for entry in expected["ranked"]), name


def test_the_floor_is_applied_before_the_cut_and_the_ranked_result_is_cut_to_the_limit() -> None:
    _, _, expected = _case("hybrid-floor-before-truncation")
    assert len(expected["ranked"]) == 2 and expected["truncated"] is False


@pytest.mark.parametrize("limit", [1, 200])
def test_a_search_limit_from_1_to_200_is_accepted(limit: int) -> None:
    scope = scope_of(_case("default-policy")[1]["scope"])
    assert SearchParameters(scope, "rotate keys", limit=limit).limit == limit
    assert VectorQuery(scope, "m", (1.0,), limit=limit).limit == limit


@pytest.mark.parametrize("limit", [0, -1, 201])
def test_a_search_or_vector_limit_outside_1_to_200_is_refused(limit: int) -> None:
    scope = scope_of(_case("default-policy")[1]["scope"])
    with pytest.raises(ValueError):
        SearchParameters(scope, "rotate keys", limit=limit)
    with pytest.raises(ValueError):
        VectorQuery(scope, "m", (1.0,), limit=limit)


def test_the_minimum_matched_terms_is_1_to_the_largest_integer() -> None:
    scope = scope_of(_case("default-policy")[1]["scope"])
    assert SearchParameters(scope, "rotate keys", minimum_matched_terms=2_147_483_647).minimum_matched_terms
    for terms in (0, 2_147_483_648):
        with pytest.raises(ValueError):
            SearchParameters(scope, "rotate keys", minimum_matched_terms=terms)
