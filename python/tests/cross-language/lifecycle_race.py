"""
Contract 1.16's cross-language race, the Python writer: the .NET suite spawns it (``uv run --no-sync python
tests/cross-language/lifecycle_race.py <input.json>`` from ``python/``) next to a .NET and a Go writer, all committing
transitions on one record at one revision through their own lifecycle services over one PostgreSQL database.

The input is ``{"dsn", "mode", "rounds", "histories"}``. In ``commit`` mode, for each round in order: on a dedicated
connection, ``pg_advisory_lock_shared(lockKey)`` (which blocks until the orchestrator releases its exclusive lock on the
key, so every writer starts the round together) and ``pg_advisory_unlock_shared(lockKey)``; then the round's request,
in the ``lifecycle-commit`` fixtures' form, through :class:`~agentexperience.ExperienceLifecycleService` over
:class:`~agentexperience.postgres.PostgresExperienceStore`. It prints ``{"results": [...]}`` (``outcome``,
``revision``, ``currentStatus``, ``reason``, ``errors``). In ``history`` mode it reads each history query and prints
``{"histories": [...]}``, each as the ``lifecycle-commit`` fixtures write a history result. Nothing else is printed.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import psycopg
from _lifecycle import (
    authorization_of,
    commit_request_of,
    errors_json,
    history_json,
    history_query_of,
)

from agentexperience import ExperienceLifecycleService
from agentexperience.postgres import PostgresExperienceStore


def _wait_for_start(dsn: str, lock_key: int) -> None:
    """Blocks until the orchestrator releases its exclusive advisory lock on ``lock_key``."""
    with psycopg.connect(dsn, autocommit=True) as connection:
        connection.execute("SELECT pg_advisory_lock_shared(%s::bigint)", (lock_key,))
        connection.execute("SELECT pg_advisory_unlock_shared(%s::bigint)", (lock_key,))


def _commit(dsn: str, rounds: list[dict[str, Any]]) -> dict[str, Any]:
    service = ExperienceLifecycleService(PostgresExperienceStore(dsn))
    results: list[dict[str, Any]] = []
    for round in rounds:
        _wait_for_start(dsn, int(round["lockKey"]))
        result = service.commit(authorization_of(round["authorization"]), commit_request_of(round["request"]))
        results.append(
            {
                "outcome": result.outcome,
                "revision": result.revision,
                "currentStatus": result.current_status,
                "reason": result.reason,
                "errors": errors_json(result.errors),
            }
        )
    return {"results": results}


def _histories(dsn: str, histories: list[dict[str, Any]]) -> dict[str, Any]:
    store = PostgresExperienceStore(dsn)
    answers: list[dict[str, Any]] = []
    for history in histories:
        query = history_query_of(history["query"])
        answers.append(history_json(store.get_history(authorization_of(history["authorization"]), query), query.scope))
    return {"histories": answers}


def main(arguments: list[str]) -> int:
    if len(arguments) != 1:
        print("usage: lifecycle_race.py <input.json>", file=sys.stderr)
        return 2
    case = json.loads(Path(arguments[0]).read_text(encoding="utf-8"))
    mode = case["mode"]
    if mode == "commit":
        output = _commit(case["dsn"], case.get("rounds") or [])
    elif mode == "history":
        output = _histories(case["dsn"], case.get("histories") or [])
    else:
        print(f"unknown mode {mode!r}", file=sys.stderr)
        return 2
    sys.stdout.write(json.dumps(output, ensure_ascii=False))
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
