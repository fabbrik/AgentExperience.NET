"""
Story 27.2's cross-language proof, the Python half (contract 1.10): a fleet .NET wrote acts the same in Python. The
rows ``CrossLanguageBenefitPolicyPostgresTests`` wrote through the .NET PostgreSQL store (lesson X measured ``Hurts``,
Y ``Helps``) go into a fresh database as the application role, each value cast from its text; a Python agent with the
benefit policy on, over the PostgreSQL store, omits X as ``MeasuredHarmful`` and injects Y, once its cache is warm.
"""

from __future__ import annotations

from typing import Any

import pytest

pytest.importorskip("agent_framework")

from _fleet import FLEET_MINIMUM, FLEET_NOW, FLEET_SCOPE, TASK, X, Y, fleet_rows
from _maf import ScriptedClient
from _postgres import Server, application_conninfo, create_database
from _sync import run
from agent_framework import Agent
from test_cross_language_holdout import _insert_raw

from agentexperience import BenefitPolicy
from agentexperience.benefit_policy import MEASURED_HARMFUL_DETAIL
from agentexperience.identity import ExperienceIdentity
from agentexperience.maf import ExperienceContextProvider, InjectionResult, use_agent_experience

pytestmark = pytest.mark.postgres


def test_a_python_agent_omits_the_lesson_the_dotnet_fleet_measured_hurting(pg_server: Server) -> None:
    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "python_benefit_fleet")
    conninfo = application_conninfo(pg_server, database)
    rows = fleet_rows()
    for table in ("experience_records", "lifecycle_events", "experience_holdout_assignments"):
        assert rows[table], table
        for row in rows[table]:
            _insert_raw(conninfo, table, row)

    results: list[InjectionResult] = []
    agent: Any = use_agent_experience(
        Agent(client=ScriptedClient(calls=0), name="benefit-agent"),
        store=PostgresExperienceStore(conninfo),
        identity=ExperienceIdentity("benefit-principal", FLEET_SCOPE),
        clock=lambda: FLEET_NOW,
        on_injected=results.append,
        benefit=BenefitPolicy(enabled=True, prefer_measured_helpful=True, minimum_runs_per_arm=FLEET_MINIMUM),
    )
    (provider,) = [item for item in agent.context_providers if isinstance(item, ExperienceContextProvider)]

    run(agent.run(TASK))
    run(provider.wait_for_injection_records())
    assert set(results[0].experience_ids) == {X, Y}
    run(provider.wait_for_benefit_refreshes())
    run(agent.run(TASK))
    run(provider.wait_for_injection_records())

    assert results[1].experience_ids == (Y,)
    assert [(item.experience_id, item.reason, item.detail) for item in results[1].omitted] == [
        (X, "MeasuredHarmful", MEASURED_HARMFUL_DETAIL)
    ]
