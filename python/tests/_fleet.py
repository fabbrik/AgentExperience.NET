"""
Shared by the benefit policy's agent tests (story 27.2, contract 1.10): the fleet .NET wrote through its PostgreSQL
store (``CrossLanguageBenefitPolicyPostgresTests``), checked in as raw rows, every column in PostgreSQL's text form.
Lesson X measures ``Hurts`` (treatment runs failed, holdout runs verified), lesson Y ``Helps``, at three runs per arm.
"""

from __future__ import annotations

import json
import uuid
from typing import Any

from _contract import REPOSITORY, read_json

from agentexperience import ExperienceRecord, Instant, Scope
from agentexperience.inmemory import HoldoutRow, InMemoryExperienceStore
from agentexperience.records import COLUMN_NAMES, from_columns

FLEET_ROWS = (
    REPOSITORY / "tests" / "AgentExperience.Contract.Tests" / "cross-language" / "dotnet-benefit-fleet-rows.json"
)
FLEET_SCOPE = Scope("cross-language-benefit", "benefit-app", "benefit-project", agent_id="benefit-agent")
FLEET_NOW = Instant.parse("2026-03-02T10:00:00+00:00")
"""An hour after the runs: what the agents' clock reads, so every assignment is inside the 30-day window."""
FLEET_MINIMUM = 3
X = uuid.UUID("000000b0-0000-4000-8000-000000000001")
Y = uuid.UUID("000000b0-0000-4000-8000-000000000002")
TASK = "Rotate the signing keys"

_INTEGERS = ("supporting_validations", "contradictions", "revision", "payload_version")


def pg_instant(text: str) -> Instant:
    """``2026-03-02 09:00:00.123456+00`` (PostgreSQL's text form in UTC) as an instant."""
    assert text.endswith("+00"), text
    return Instant.parse(text[:-3].replace(" ", "T") + "+00:00")


def fleet_rows() -> dict[str, list[dict[str, str | None]]]:
    rows: dict[str, list[dict[str, str | None]]] = read_json(FLEET_ROWS)
    return rows


def _scope(row: dict[str, Any]) -> Scope:
    return Scope(
        row["tenant_id"], row["application_id"], row["project_id"], row["team_id"], row["agent_id"], row["user_id"]
    )


def fleet_records() -> list[ExperienceRecord]:
    """The fleet's records, read from their raw rows as the PostgreSQL store reads its columns."""
    records = []
    for row in fleet_rows()["experience_records"]:
        columns: dict[str, Any] = {name: row[name] for name in COLUMN_NAMES}
        for name in _INTEGERS:
            columns[name] = int(str(row[name]))
        columns["reuse_confidence"] = float(str(row["reuse_confidence"]))
        columns["created_at"] = pg_instant(str(row["created_at"]))
        columns["updated_at"] = pg_instant(str(row["updated_at"]))
        records.append(from_columns(columns, json.loads(str(row["payload"]))))
    return records


def fleet_holdout_rows() -> list[HoldoutRow]:
    return [
        HoldoutRow(
            uuid.UUID(str(row["run_id"])),
            uuid.UUID(str(row["experience_id"])),
            int(str(row["revision"])),
            row["arm"],  # type: ignore[arg-type]
            _scope(row),
            pg_instant(str(row["assigned_at"])),
        )
        for row in fleet_rows()["experience_holdout_assignments"]
    ]


def fleet_memory_store() -> InMemoryExperienceStore:
    """An in-memory store holding the fleet: its records and its holdout rows."""
    store = InMemoryExperienceStore(fleet_records(), clock=lambda: FLEET_NOW)
    for row in fleet_holdout_rows():
        store.seed_holdout_row(row)
    return store
