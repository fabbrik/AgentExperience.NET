"""
Contract 1.17's cross-language reuse feedback, the Python writer: the .NET suite spawns it (``uv run --no-sync python
tests/cross-language/reuse_feedback.py <input.json>`` from ``python/``) beside a .NET and a Go writer, each recording
feedback on the same lessons through its own feedback service over one PostgreSQL database (or one each).

The input is ``{"dsn", "hostTrustedEvidence", "steps"}``. One lifecycle service (the default engine, the given
``hostTrustedEvidence``) runs over :class:`~agentexperience.postgres.PostgresExperienceStore`, and the feedback service
over it and :class:`~agentexperience.postgres.PostgresExperienceReuseFeedbackStore`; both stores' clocks read the
current ``submit`` step's ``clock``. Each step is ``{"submit": {"clock", "authorization", "feedback"}}`` (the
``reuse-feedback`` fixtures' form) or ``{"read": {"authorization", "scope", "experienceId"}}``. It prints
``{"results": [...]}``, one per step: a submit's result exactly as the ``reuse-feedback`` fixtures write it; a read's
``{"history": <the record's history, limit 500, as the 1.16 fixtures write it>, "confidence": <its confidence with the
filter All, as the 1.16 fixtures write it>}``. Nothing else is printed.
"""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from _contract import scope_of
from _lifecycle import authorization_of, history_json, read_confidence_json
from _reuse_feedback import feedback_of, submit_result_json, thrown_json

from agentexperience import (
    ExperienceLifecycleService,
    ExperienceReuseFeedbackService,
    ExperienceStoreError,
    HistoryQuery,
    Instant,
)
from agentexperience.postgres import PostgresExperienceReuseFeedbackStore, PostgresExperienceStore


class _Clock:
    def __init__(self) -> None:
        self.now = Instant(0)

    def __call__(self) -> Instant:
        return self.now


def run(case: dict[str, Any]) -> dict[str, Any]:
    clock = _Clock()
    store = PostgresExperienceStore(case["dsn"], clock=clock)
    lifecycle = ExperienceLifecycleService(store, host_trusted_evidence=case["hostTrustedEvidence"])
    service = ExperienceReuseFeedbackService(PostgresExperienceReuseFeedbackStore(case["dsn"], clock=clock), lifecycle)
    results: list[dict[str, Any]] = []
    for step in case["steps"]:
        if "submit" in step:
            submit = step["submit"]
            clock.now = Instant.parse(submit["clock"])
            try:
                results.append(
                    submit_result_json(
                        service.record(authorization_of(submit["authorization"]), feedback_of(submit["feedback"]))
                    )
                )
            except ExperienceStoreError as error:
                results.append(thrown_json(error))
        elif "read" in step:
            read = step["read"]
            authorization = authorization_of(read["authorization"])
            scope = scope_of(read["scope"])
            experience_id = uuid.UUID(read["experienceId"])
            history = store.get_history(authorization, HistoryQuery(scope, experience_id, 500, None))
            results.append(
                {
                    "history": history_json(history, scope),
                    "confidence": read_confidence_json(
                        lifecycle.read_confidence(authorization, scope, experience_id, "All")
                    ),
                }
            )
        else:
            raise ValueError(f"unknown step {sorted(step)}")
    return {"results": results}


def main(arguments: list[str]) -> int:
    if len(arguments) != 1:
        print("usage: reuse_feedback.py <input.json>", file=sys.stderr)
        return 2
    case = json.loads(Path(arguments[0]).read_text(encoding="utf-8"))
    sys.stdout.write(json.dumps(run(case), ensure_ascii=False))
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
