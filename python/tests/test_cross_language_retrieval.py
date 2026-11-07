"""
Story 27.4's cross-language proof, the Python half (contract 1.12): the same seeded records and the same non-default
retrieval policy (an expiry and per-domain confidence decay) rank the same in Python as in .NET. The seed
``CrossLanguageRetrievalPolicyPostgresTests`` ranked goes into a fresh database as a search seed is inserted; the
Python PostgreSQL store searches it with the policy's parameters, and Python's ranking under the policy and decay must
give .NET's answer: the outcome, the truncation, the ranked IDs in order with every score and component within 1e-12,
and the exclusions.
"""

from __future__ import annotations

import pytest
from _contract import REPOSITORY, json_difference, read_json, scope_of
from _postgres import Server, application_conninfo, create_database
from test_retrieval_policy import TOLERANCE, decay_of, policy_of, ranked_json

from agentexperience import Instant, SearchParameters, from_stored, rank
from agentexperience.search import AuthorizationBounds

pytestmark = pytest.mark.postgres

DOTNET_RETRIEVAL_POLICY = (
    REPOSITORY / "tests" / "AgentExperience.Contract.Tests" / "cross-language" / "dotnet-retrieval-policy.json"
)


def test_python_ranks_the_dotnet_seed_under_the_same_policy_as_dotnet(pg_server: Server) -> None:
    from agentexperience.postgres import PostgresExperienceStore

    case = read_json(DOTNET_RETRIEVAL_POLICY)
    scope = scope_of(case["scope"])
    policy = policy_of(case["policy"])
    decay = decay_of(case["confidenceDecay"])
    assert policy.max_age is not None and decay is not None  # the non-default policy

    store = PostgresExperienceStore(application_conninfo(pg_server, create_database(pg_server, "python_retrieval")))
    for stored in case["seed"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    parameters = SearchParameters.for_policy(scope, case["taskText"], policy)
    rows = store.search(AuthorizationBounds.of(scope), parameters)
    result = rank(
        rows,
        scope,
        Instant.parse(case["now"]),
        case["preferredEnvironment"],
        policy=policy,
        confidence_decay=decay,
    )

    expected = case["expected"]
    assert expected["outcome"] == "Completed"
    actual = ranked_json(result)
    for key in ("truncated", "ranked", "excluded"):
        difference = json_difference(expected[key], actual[key], f"$.{key}", TOLERANCE)
        assert difference is None, difference
