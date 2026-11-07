"""Shared by the conformance runner and the PostgreSQL tests: where the contract is, and how a fixture runs."""

from __future__ import annotations

import json
import math
import uuid
from pathlib import Path
from typing import Any

from agentexperience import (
    SUPPORTED_CONTRACT_VERSIONS,
    CapturedRun,
    ClosedVerificationRound,
    EnvironmentFingerprint,
    FinalizedExperience,
    Instant,
    Provenance,
    RawAttempt,
    RawToolCall,
    RequiredCheck,
    RunCapture,
    RunExposure,
    Sanitization,
    Scope,
    finalize,
)
from agentexperience.encoding import Duration
from agentexperience.records import Evidence

REPOSITORY = Path(__file__).resolve().parents[2]
CONTRACT = REPOSITORY / "contract"
FIXTURES = CONTRACT / "fixtures"
# Contract 1.19: the 1.0 schema with a tool call's optional recipeExcerpt, which a 1.19 port validates against.
SCHEMA = CONTRACT / "schema" / "experience-record.v1-1.19.json"
MIGRATIONS = REPOSITORY / "src" / "AgentExperience.Storage.Postgres" / "Migrations"


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def cases(family: str) -> list[str]:
    """Every case directory of a fixture family, by name."""
    return sorted(entry.name for entry in (FIXTURES / family).iterdir() if entry.is_dir())


class UnsupportedContractVersion(Exception):
    pass


def check_contract_version(lock: dict[str, Any]) -> str:
    """The lock's contract version, when this package supports it; a clear failure otherwise."""
    version = lock.get("contractVersion")
    if version not in SUPPORTED_CONTRACT_VERSIONS:
        raise UnsupportedContractVersion(
            f"contract/lock.json is contract version {version!r}, but this package supports only "
            f"{', '.join(SUPPORTED_CONTRACT_VERSIONS)}. Implement the new version before running its fixtures."
        )
    return str(version)


def json_difference(expected: Any, actual: Any, path: str = "$", tolerance: float = 0.0) -> str | None:
    """The first difference between two JSON values, compared by value (a boolean is never a number); ``None`` when
    equal. Numbers differ by at most ``tolerance``."""
    if isinstance(expected, bool) or isinstance(actual, bool):
        return None if expected is actual else f"{path}: expected {expected!r}, got {actual!r}"
    if isinstance(expected, (int, float)) and isinstance(actual, (int, float)):
        if expected == actual or (tolerance and math.isclose(expected, actual, rel_tol=0, abs_tol=tolerance)):
            return None
        return f"{path}: expected {expected!r}, got {actual!r}"
    if isinstance(expected, dict) and isinstance(actual, dict):
        if expected.keys() != actual.keys():
            return f"{path}: expected members {sorted(expected)}, got {sorted(actual)}"
        for key in expected:
            difference = json_difference(expected[key], actual[key], f"{path}.{key}", tolerance)
            if difference:
                return difference
        return None
    if isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            return f"{path}: expected {len(expected)} items, got {len(actual)}"
        for index, (left, right) in enumerate(zip(expected, actual, strict=True)):
            difference = json_difference(left, right, f"{path}[{index}]", tolerance)
            if difference:
                return difference
        return None
    if type(expected) is not type(actual) or expected != actual:
        return f"{path}: expected {expected!r}, got {actual!r}"
    return None


def scope_of(value: dict[str, Any]) -> Scope:
    return Scope(
        value["tenantId"],
        value["applicationId"],
        value["projectId"],
        value.get("teamId"),
        value.get("agentId"),
        value.get("userId"),
    )


def evidence_of(value: dict[str, Any]) -> Evidence:
    return Evidence(
        evidence_id=uuid.UUID(value["evidenceId"]),
        verification_round_id=uuid.UUID(value["verificationRoundId"]),
        artifact_revision=value["artifactRevision"],
        check_id=value["checkId"],
        kind=value["kind"],
        result=value["result"],
        producer=value["producer"],
        detail=value.get("detail"),
        captured_at=Instant.parse(value["capturedAt"]),
    )


def run_finalize_case(case: dict[str, Any]) -> tuple[FinalizedExperience, dict[str, Any]]:
    """Runs a finalize case's ``input.json`` through capture and finalization; returns the result and what capture
    answered (``expected-capture.json``'s form)."""
    captured, answers = capture_finalize_case(case)
    return finalize(captured, **finalize_arguments(case)), answers


def capture_finalize_case(case: dict[str, Any]) -> tuple[CapturedRun, dict[str, Any]]:
    """Captures a finalize case's run; returns the finished run and what capture answered."""
    run = case["run"]
    environment = run["environment"]
    provenance = run["provenance"]
    capture = RunCapture.start(
        uuid.UUID(run["runId"]),
        run["taskId"],
        run.get("taskDescription"),
        scope_of(run["scope"]),
        EnvironmentFingerprint(
            environment["hostName"],
            environment["runtimeVersion"],
            environment["operatingSystem"],
            environment.get("applicationVersion"),
            dict(environment["metadata"]),
        ),
        Provenance(
            provenance["source"],
            provenance.get("sourceVersion"),
            Instant.parse(provenance["recordedAt"]),
            provenance.get("correlationId"),
        ),
        Instant.parse(run["startedAt"]),
        sanitization=Sanitization.allowing(case["options"].get("allowedArgumentNames")),
    )

    answers: dict[str, Any] = {}
    exposures = provenance.get("exposedTo") or []
    if exposures:
        answers["exposure"] = capture.record_exposure(
            RunExposure(uuid.UUID(exposure["experienceId"]), int(exposure["revision"])) for exposure in exposures
        )

    outcomes = []
    for attempt in sorted(run["attempts"], key=lambda attempt: attempt["sequenceNumber"]):
        calls = sorted(attempt["toolCalls"], key=lambda call: call["sequenceNumber"])
        outcomes.append(
            capture.append_attempt(
                RawAttempt(
                    attempt_id=uuid.UUID(attempt["attemptId"]),
                    started_at=Instant.parse(attempt["startedAt"]),
                    duration=Duration.parse(attempt["duration"]),
                    tool_calls=tuple(
                        RawToolCall(
                            tool_call_id=uuid.UUID(call["toolCallId"]),
                            tool_name=call["toolName"],
                            arguments=call["arguments"],
                            started_at=Instant.parse(call["startedAt"]),
                            duration=Duration.parse(call["duration"]),
                            result=call.get("result"),
                            error=call.get("error"),
                        )
                        for call in calls
                    ),
                    result=attempt.get("result"),
                    error=attempt.get("error"),
                )
            )
        )
    answers["attempts"] = outcomes
    return capture.complete(run["executionStatus"], Instant.parse(run["endedAt"])), answers


def finalize_arguments(case: dict[str, Any]) -> dict[str, Any]:
    """The verification of a finalize case's ``input.json``, as keyword arguments of ``finalize``."""
    verification = case["verification"]
    closed = verification.get("closedRound")
    return {
        "closed_round": None
        if closed is None
        else ClosedVerificationRound(uuid.UUID(closed["roundId"]), closed["artifactRevision"]),
        "required_checks": [
            RequiredCheck(check["checkId"], check["expectedKind"]) for check in verification["requiredChecks"]
        ],
        "evidence": [evidence_of(item) for item in verification["evidence"]],
        "current_artifact_revision": verification["currentArtifactRevision"],
        "finalized_at": Instant.parse(verification["finalizedAt"]),
    }
