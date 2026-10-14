"""
The Experience Record and its stored form: one row of ``agent_experience.experience_records``, as the record columns
plus the ``payload`` column at ``payload_version`` 1 (``contract/schema/experience-record.v1.json``).

Enums are their .NET member names, exactly as written; instants are :class:`~agentexperience.encoding.Instant`;
durations are :class:`~agentexperience.encoding.Duration`.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Any, Literal, cast

from .encoding import Duration, Instant, format_uuid, json_number, json_value, parse_uuid

PAYLOAD_VERSION = 1

ExperienceStatus = Literal[
    "Candidate", "Validated", "Quarantined", "Contested", "Stale", "Superseded", "Revoked", "Reinforced"
]
EXPERIENCE_STATUSES: tuple[ExperienceStatus, ...] = (
    "Candidate",
    "Validated",
    "Quarantined",
    "Contested",
    "Stale",
    "Superseded",
    "Revoked",
    "Reinforced",
)

VerificationStatus = Literal["Unknown", "Verified", "Failed"]
VERIFICATION_STATUSES: tuple[VerificationStatus, ...] = ("Unknown", "Verified", "Failed")

CheckResult = Literal["Pass", "Fail", "Unknown"]
CHECK_RESULTS: tuple[CheckResult, ...] = ("Pass", "Fail", "Unknown")

Authorship = Literal["Deterministic", "Model"]
Origin = Literal["HostWritten", "Finalized"]


@dataclass(frozen=True)
class Scope:
    """Where a record lives. The optional fields are ``None`` when unset, never the empty string."""

    tenant_id: str
    application_id: str
    project_id: str
    team_id: str | None = None
    agent_id: str | None = None
    user_id: str | None = None

    def fields(self) -> tuple[str | None, ...]:
        return (self.tenant_id, self.application_id, self.project_id, self.team_id, self.agent_id, self.user_id)


@dataclass(frozen=True)
class EnvironmentFingerprint:
    host_name: str
    runtime_version: str
    operating_system: str
    application_version: str | None
    metadata: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class RunExposure:
    """A stored record a run was shown, by ID and revision."""

    experience_id: uuid.UUID
    revision: int


@dataclass(frozen=True)
class Provenance:
    source: str
    source_version: str | None
    recorded_at: Instant
    correlation_id: str | None
    exposed_to: tuple[RunExposure, ...] = ()


@dataclass(frozen=True)
class ToolCall:
    tool_call_id: uuid.UUID
    sequence_number: int
    tool_name: str
    arguments: Mapping[str, Any]
    started_at: Instant
    duration: Duration
    result: str | None
    error: str | None


@dataclass(frozen=True)
class Attempt:
    attempt_id: uuid.UUID
    sequence_number: int
    started_at: Instant
    duration: Duration
    tool_calls: tuple[ToolCall, ...]
    result: str | None
    error: str | None


@dataclass(frozen=True)
class Evidence:
    evidence_id: uuid.UUID
    verification_round_id: uuid.UUID
    artifact_revision: str
    check_id: str
    kind: str
    result: CheckResult
    producer: str
    detail: str | None
    captured_at: Instant


@dataclass(frozen=True)
class Outcome:
    status: VerificationStatus
    evidence: tuple[Evidence, ...]
    reason: str | None
    evaluated_at: Instant


@dataclass(frozen=True)
class Reflection:
    reflection_id: uuid.UUID
    experience_run_id: uuid.UUID
    lesson: str
    successful_approaches: tuple[str, ...]
    failed_approaches: tuple[str, ...]
    preconditions: tuple[str, ...]
    warnings: tuple[str, ...]
    reuse_guidance: str | None
    evidence_ids: tuple[uuid.UUID, ...]
    verification_status: VerificationStatus
    completion_score: float
    verification_rule_version: str
    producer: str
    created_at: Instant
    authorship: Authorship = "Deterministic"


@dataclass(frozen=True)
class ExperienceRecord:
    """One stored Experience Record."""

    experience_id: uuid.UUID
    source_run_id: uuid.UUID
    scope: Scope
    task_id: str
    task_summary: str | None
    attempts: tuple[Attempt, ...]
    outcome: Outcome
    completion_score: float
    reflection: Reflection | None
    environment: EnvironmentFingerprint
    provenance: Provenance
    status: ExperienceStatus
    reuse_confidence: float
    supporting_validations: int
    contradictions: int
    revision: int
    created_at: Instant
    updated_at: Instant
    closed_round_id: uuid.UUID | None = None
    origin: Origin = "HostWritten"
    provenance_signature: Mapping[str, Any] | None = None
    """Signing is outside contract 1.x: a record read with a signature carries it unchanged, and a port writes none."""


# ---- The stored form ---------------------------------------------------------------------------------------------

COLUMN_NAMES: tuple[str, ...] = (
    "experience_id",
    "source_run_id",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "task_id",
    "status",
    "reuse_confidence",
    "supporting_validations",
    "contradictions",
    "revision",
    "created_at",
    "updated_at",
    "payload_version",
    "payload",
)
"""The eighteen columns a writer supplies, in the order the contract lists them."""


def to_columns(record: ExperienceRecord) -> dict[str, Any]:
    """The record columns as the contract's JSON writes them (without ``payload``)."""
    return {
        "experience_id": format_uuid(record.experience_id),
        "source_run_id": format_uuid(record.source_run_id),
        "tenant_id": record.scope.tenant_id,
        "application_id": record.scope.application_id,
        "project_id": record.scope.project_id,
        "team_id": record.scope.team_id,
        "agent_id": record.scope.agent_id,
        "user_id": record.scope.user_id,
        "task_id": record.task_id,
        "status": record.status,
        "reuse_confidence": json_number(record.reuse_confidence),
        "supporting_validations": record.supporting_validations,
        "contradictions": record.contradictions,
        "revision": record.revision,
        "created_at": record.created_at.truncate_to_microseconds().format(),
        "updated_at": record.updated_at.truncate_to_microseconds().format(),
        "payload_version": PAYLOAD_VERSION,
    }


def to_payload(record: ExperienceRecord) -> dict[str, Any]:
    """The ``payload`` column's JSON at ``payload_version`` 1."""
    payload: dict[str, Any] = {
        "taskSummary": record.task_summary,
        "attempts": [_attempt_json(attempt) for attempt in record.attempts],
        "outcome": {
            "status": record.outcome.status,
            "evidence": [_evidence_json(evidence) for evidence in record.outcome.evidence],
            "reason": record.outcome.reason,
            "evaluatedAt": record.outcome.evaluated_at.format(),
        },
        "completionScore": json_number(record.completion_score),
        "reflection": None if record.reflection is None else _reflection_json(record.reflection),
        "environment": {
            "hostName": record.environment.host_name,
            "runtimeVersion": record.environment.runtime_version,
            "operatingSystem": record.environment.operating_system,
            "applicationVersion": record.environment.application_version,
            "metadata": dict(record.environment.metadata),
        },
        "provenance": _provenance_json(record.provenance),
    }
    if record.closed_round_id is not None:
        payload["closedRoundId"] = format_uuid(record.closed_round_id)
    if record.origin != "HostWritten":
        payload["origin"] = record.origin
    if record.provenance_signature is not None:
        payload["provenanceSignature"] = dict(record.provenance_signature)
    return payload


def to_stored(record: ExperienceRecord) -> dict[str, Any]:
    """``{"columns": ..., "payload": ...}``: the record as the table holds it, and as the schema describes it."""
    return {"columns": to_columns(record), "payload": to_payload(record)}


def _attempt_json(attempt: Attempt) -> dict[str, Any]:
    return {
        "attemptId": format_uuid(attempt.attempt_id),
        "sequenceNumber": attempt.sequence_number,
        "startedAt": attempt.started_at.format(),
        "duration": attempt.duration.format(),
        "toolCalls": [
            {
                "toolCallId": format_uuid(call.tool_call_id),
                "sequenceNumber": call.sequence_number,
                "toolName": call.tool_name,
                "arguments": json_value(dict(call.arguments)),
                "startedAt": call.started_at.format(),
                "duration": call.duration.format(),
                "result": call.result,
                "error": call.error,
            }
            for call in attempt.tool_calls
        ],
        "result": attempt.result,
        "error": attempt.error,
    }


def _evidence_json(evidence: Evidence) -> dict[str, Any]:
    return {
        "evidenceId": format_uuid(evidence.evidence_id),
        "verificationRoundId": format_uuid(evidence.verification_round_id),
        "artifactRevision": evidence.artifact_revision,
        "checkId": evidence.check_id,
        "kind": evidence.kind,
        "result": evidence.result,
        "producer": evidence.producer,
        "detail": evidence.detail,
        "capturedAt": evidence.captured_at.format(),
    }


def _reflection_json(reflection: Reflection) -> dict[str, Any]:
    value: dict[str, Any] = {
        "reflectionId": format_uuid(reflection.reflection_id),
        "experienceRunId": format_uuid(reflection.experience_run_id),
        "lesson": reflection.lesson,
        "successfulApproaches": list(reflection.successful_approaches),
        "failedApproaches": list(reflection.failed_approaches),
        "preconditions": list(reflection.preconditions),
        "warnings": list(reflection.warnings),
        "reuseGuidance": reflection.reuse_guidance,
        "evidenceIds": [format_uuid(evidence_id) for evidence_id in reflection.evidence_ids],
        "verificationStatus": reflection.verification_status,
        "completionScore": json_number(reflection.completion_score),
        "verificationRuleVersion": reflection.verification_rule_version,
        "producer": reflection.producer,
        "createdAt": reflection.created_at.format(),
    }
    if reflection.authorship != "Deterministic":
        value["authorship"] = reflection.authorship
    return value


def _provenance_json(provenance: Provenance) -> dict[str, Any]:
    value: dict[str, Any] = {
        "source": provenance.source,
        "sourceVersion": provenance.source_version,
        "recordedAt": provenance.recorded_at.format(),
        "correlationId": provenance.correlation_id,
    }
    if provenance.exposed_to:
        value["exposedTo"] = [
            {"experienceId": format_uuid(exposure.experience_id), "revision": exposure.revision}
            for exposure in provenance.exposed_to
        ]
    return value


# ---- Reading the stored form -------------------------------------------------------------------------------------


def _enum(value: Any, allowed: tuple[str, ...], name: str) -> Any:
    if not isinstance(value, str) or value not in allowed:
        raise ValueError(f"{name} is not one of the member names {allowed}.")
    return value


def _number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be a JSON number.")
    return float(value)


def _integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        if isinstance(value, float) and value.is_integer():
            return int(value)
        raise ValueError(f"{name} must be a JSON integer.")
    return value


def _instant(value: Any) -> Instant:
    if isinstance(value, Instant):
        return value
    if not isinstance(value, str):
        raise ValueError("An instant must be an ISO 8601 string.")
    return Instant.parse(value)


def from_stored(stored: Mapping[str, Any]) -> ExperienceRecord:
    """Reads a record from its stored form: ``{"columns": ..., "payload": ...}``."""
    columns = stored["columns"]
    version = _integer(columns["payload_version"], "payload_version")
    if version != PAYLOAD_VERSION:
        raise ValueError(f"payload_version {version} is not supported; this port reads version {PAYLOAD_VERSION} only.")
    return from_columns(columns, stored["payload"])


def from_columns(columns: Mapping[str, Any], payload: Mapping[str, Any]) -> ExperienceRecord:
    """Reads a record from its columns (as JSON values, or as a database driver returns them) and its payload."""

    def text(name: str) -> str:
        value = columns[name]
        if not isinstance(value, str):
            raise ValueError(f"{name} must be text.")
        return value

    def optional_text(name: str) -> str | None:
        value = columns.get(name)
        if value is not None and not isinstance(value, str):
            raise ValueError(f"{name} must be text or null.")
        return value

    def identifier(name: str) -> uuid.UUID:
        value = columns[name]
        return value if isinstance(value, uuid.UUID) else parse_uuid(str(value))

    def instant(name: str) -> Instant:
        value = columns[name]
        if hasattr(value, "tzinfo"):
            return Instant.from_datetime(value)
        return _instant(value)

    environment = payload["environment"]
    provenance = payload["provenance"]
    reflection = payload.get("reflection")
    closed_round = payload.get("closedRoundId")
    origin = payload.get("origin")
    return ExperienceRecord(
        experience_id=identifier("experience_id"),
        source_run_id=identifier("source_run_id"),
        scope=Scope(
            text("tenant_id"),
            text("application_id"),
            text("project_id"),
            optional_text("team_id"),
            optional_text("agent_id"),
            optional_text("user_id"),
        ),
        task_id=text("task_id"),
        task_summary=payload.get("taskSummary"),
        attempts=tuple(_attempt(attempt) for attempt in payload["attempts"]),
        outcome=Outcome(
            status=_enum(payload["outcome"]["status"], VERIFICATION_STATUSES, "outcome.status"),
            evidence=tuple(_evidence(evidence) for evidence in payload["outcome"]["evidence"]),
            reason=payload["outcome"].get("reason"),
            evaluated_at=_instant(payload["outcome"]["evaluatedAt"]),
        ),
        completion_score=_number(payload["completionScore"], "completionScore"),
        reflection=None if reflection is None else _reflection(reflection),
        environment=EnvironmentFingerprint(
            host_name=environment["hostName"],
            runtime_version=environment["runtimeVersion"],
            operating_system=environment["operatingSystem"],
            application_version=environment.get("applicationVersion"),
            metadata=dict(environment["metadata"]),
        ),
        provenance=Provenance(
            source=provenance["source"],
            source_version=provenance.get("sourceVersion"),
            recorded_at=_instant(provenance["recordedAt"]),
            correlation_id=provenance.get("correlationId"),
            exposed_to=tuple(
                RunExposure(parse_uuid(exposure["experienceId"]), _integer(exposure["revision"], "revision"))
                for exposure in provenance.get("exposedTo") or ()
            ),
        ),
        status=_enum(columns["status"], EXPERIENCE_STATUSES, "status"),
        reuse_confidence=_number(columns["reuse_confidence"], "reuse_confidence"),
        supporting_validations=_integer(columns["supporting_validations"], "supporting_validations"),
        contradictions=_integer(columns["contradictions"], "contradictions"),
        revision=_integer(columns["revision"], "revision"),
        created_at=instant("created_at"),
        updated_at=instant("updated_at"),
        closed_round_id=None if closed_round is None else parse_uuid(closed_round),
        origin="HostWritten" if origin is None else cast(Origin, _enum(origin, ("HostWritten", "Finalized"), "origin")),
        provenance_signature=payload.get("provenanceSignature"),
    )


_INT64_MIN, _INT64_MAX = -(2**63), 2**63 - 1


def _read_arguments(arguments: Mapping[str, Any]) -> dict[str, Any]:
    """
    Stored tool-call arguments, their numbers read as the .NET reader reads them: an integer a 64-bit integer holds
    as that integer, and any other number as a double. An integer beyond a double's range is outside the contract
    (.NET reads it as an infinity it cannot write again) and is refused here with a ``ValueError``. PostgreSQL
    writes a ``jsonb`` number back in its own form (``1.5e19`` as ``15000000000000000000``), so reading it as
    written would keep, as an integer, a value .NET holds as a double.
    """
    return {key: _read_argument(value) for key, value in arguments.items()}


def _read_argument(value: Any) -> Any:
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        if _INT64_MIN <= value <= _INT64_MAX:
            return value
        try:
            return float(value)
        except OverflowError:
            raise ValueError(
                "A stored tool-call argument is an integer beyond a double's range, which is outside the contract: "
                ".NET reads it back as an infinity it cannot write again."
            ) from None
    if isinstance(value, Mapping):
        return _read_arguments(value)
    if isinstance(value, list):
        return [_read_argument(item) for item in value]
    return value


def _attempt(value: Mapping[str, Any]) -> Attempt:
    return Attempt(
        attempt_id=parse_uuid(value["attemptId"]),
        sequence_number=_integer(value["sequenceNumber"], "sequenceNumber"),
        started_at=_instant(value["startedAt"]),
        duration=Duration.parse(value["duration"]),
        tool_calls=tuple(
            ToolCall(
                tool_call_id=parse_uuid(call["toolCallId"]),
                sequence_number=_integer(call["sequenceNumber"], "sequenceNumber"),
                tool_name=call["toolName"],
                arguments=_read_arguments(call["arguments"]),
                started_at=_instant(call["startedAt"]),
                duration=Duration.parse(call["duration"]),
                result=call.get("result"),
                error=call.get("error"),
            )
            for call in value["toolCalls"]
        ),
        result=value.get("result"),
        error=value.get("error"),
    )


def _evidence(value: Mapping[str, Any]) -> Evidence:
    return Evidence(
        evidence_id=parse_uuid(value["evidenceId"]),
        verification_round_id=parse_uuid(value["verificationRoundId"]),
        artifact_revision=value["artifactRevision"],
        check_id=value["checkId"],
        kind=value["kind"],
        result=_enum(value["result"], CHECK_RESULTS, "evidence.result"),
        producer=value["producer"],
        detail=value.get("detail"),
        captured_at=_instant(value["capturedAt"]),
    )


def _reflection(value: Mapping[str, Any]) -> Reflection:
    authorship = value.get("authorship")
    return Reflection(
        reflection_id=parse_uuid(value["reflectionId"]),
        experience_run_id=parse_uuid(value["experienceRunId"]),
        lesson=value["lesson"],
        successful_approaches=tuple(value["successfulApproaches"]),
        failed_approaches=tuple(value["failedApproaches"]),
        preconditions=tuple(value["preconditions"]),
        warnings=tuple(value["warnings"]),
        reuse_guidance=value.get("reuseGuidance"),
        evidence_ids=tuple(parse_uuid(evidence_id) for evidence_id in value["evidenceIds"]),
        verification_status=_enum(value["verificationStatus"], VERIFICATION_STATUSES, "verificationStatus"),
        completion_score=_number(value["completionScore"], "completionScore"),
        verification_rule_version=value["verificationRuleVersion"],
        producer=value["producer"],
        created_at=_instant(value["createdAt"]),
        # Any authorship but Deterministic is read as model-authored (the schema writes only "Model").
        authorship="Deterministic" if authorship is None or authorship == "Deterministic" else "Model",
    )
