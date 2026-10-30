"""
Reuse feedback (contract 1.17), exactly as .NET's ``ExperienceReuseFeedbackService`` records it over its stores: what
happened in a run that stored lessons were injected into (which records it saw, how it came out, what the host
measured) and, only when an accepted attribution says so, whether reuse helped or hurt
(:meth:`ExperienceReuseFeedbackService.record`). Also finalization's opt-in reuse evidence
(:func:`apply_reuse_evidence`, .NET's ``ExperienceFinalizationOptions.ReuseEvidence`` and ``ContradictOnFailure``).

**Exposure is not attribution.** A submission with no human assessment and no comparative result is recorded with
benefit ``Unknown`` and moves nothing; ``claimed_benefit`` is recorded and never acted on. Only an accepted attribution
becomes confidence evidence, through contract 1.16's :meth:`~agentexperience.ExperienceLifecycleService.apply_evidence`
and nothing else, one record at a time: improvement supports, harm contradicts (and contests).

The service runs over the 1.16 lifecycle service, as .NET's does under ``TrustHostSuppliedIdentifiers`` with no signer:
no attribution is verified before the ledger is written (no run lookup, no token check, no exposure check), so an
attribution degrades only through validation, and the lifecycle admits its evidence ``HostTrusted``. Under the lifecycle
service's default (``host_trusted_evidence="RecordedOnly"``) that evidence is recorded without moving the record.

The ledger is :class:`ExperienceReuseFeedbackStore`: the in-memory one
(:class:`~agentexperience.inmemory.InMemoryExperienceReuseFeedbackStore`) and the PostgreSQL one
(:class:`~agentexperience.postgres.PostgresExperienceReuseFeedbackStore`, running ``sql/reuse-feedback.sql``). Every
outcome, disposition, reason and validation message is .NET's, byte for byte and in .NET's order. A value no member
names is passed through as it is and refused where .NET refuses it.
"""

from __future__ import annotations

import hashlib
import math
import uuid
from collections.abc import Sequence
from dataclasses import dataclass, field, replace
from typing import Literal, Protocol, runtime_checkable

from ._text import is_null_or_whitespace, is_whitespace
from .confidence import ConfidenceEvidenceKind, IndependenceRefusal, is_engine_failure
from .encoding import Instant
from .ids import reuse_event_id_for, reuse_evidence_id_for
from .lifecycle import (
    ApplyConfidenceEvidenceRequest,
    AuthorizationContext,
    ConfidenceUpdateOutcome,
    ExperienceLifecycleService,
    ExperienceStoreError,
    StoreValidationError,
    _refuse_reserved_text,
    _require_defined,
    _require_not_blank,
    _require_null_or_not_blank,
    _validate_scope,
)
from .limits import MAX_EXPOSURES_PER_RUN
from .observer import observe, type_name
from .records import Evidence, ExperienceRecord, ExperienceStatus, Scope
from .records import VerificationStatus as TaskVerificationStatus

__all__ = [
    "EXPERIENCE_REUSE_BENEFITS",
    "HUMAN_ASSESSMENT_PRODUCER",
    "MAX_EXPOSED_RECORDS",
    "REUSE_ATTRIBUTION_SOURCES",
    "REUSE_EVIDENCE_MODES",
    "REUSE_EVIDENCE_PRODUCER",
    "ComparativeEvaluationResult",
    "ExperienceExposureDisposition",
    "ExperienceExposureResult",
    "ExperienceReuseBenefit",
    "ExperienceReuseExposure",
    "ExperienceReuseFeedback",
    "ExperienceReuseFeedbackOutcome",
    "ExperienceReuseFeedbackResult",
    "ExperienceReuseFeedbackService",
    "ExperienceReuseFeedbackStore",
    "ExperienceReuseFeedbackStoreOutcome",
    "ExperienceReuseFeedbackStoreResult",
    "HumanReuseAssessment",
    "RecordedExperienceReuseFeedback",
    "ReuseAttributionSource",
    "ReuseEvidenceMode",
    "ReuseEvidenceReport",
    "ReuseEvidenceResult",
    "ReuseMeasure",
    "apply_reuse_evidence",
    "is_store_failure",
    "normalize_reuse_feedback",
    "validate_reuse_feedback",
]

ExperienceReuseBenefit = Literal["Unknown", "Improved", "Harmed"]
""".NET's ``ExperienceReuseBenefit``."""
EXPERIENCE_REUSE_BENEFITS: tuple[ExperienceReuseBenefit, ...] = ("Unknown", "Improved", "Harmed")

ReuseAttributionSource = Literal["None", "HumanAssessment", "ComparativeEvaluation"]
""".NET's ``ReuseAttributionSource``."""
REUSE_ATTRIBUTION_SOURCES: tuple[ReuseAttributionSource, ...] = ("None", "HumanAssessment", "ComparativeEvaluation")

ExperienceReuseFeedbackStoreOutcome = Literal["Recorded", "AlreadyRecorded", "Conflict", "Denied", "Invalid"]
""".NET's ``ExperienceReuseFeedbackStoreOutcome``."""

ExperienceReuseFeedbackOutcome = Literal["Recorded", "AlreadyRecorded", "Conflict", "Denied", "Invalid"]
""".NET's ``ExperienceReuseFeedbackOutcome``."""

ExperienceExposureDisposition = Literal[
    "ExposureOnly", "EvidenceApplied", "Ineligible", "Unresolved", "Refused", "Failed"
]
""".NET's ``ExperienceExposureDisposition``."""

ReuseEvidenceMode = Literal["Off", "SameTask"]
""".NET's ``ReuseEvidenceMode``."""
REUSE_EVIDENCE_MODES: tuple[ReuseEvidenceMode, ...] = ("Off", "SameTask")

MAX_EXPOSED_RECORDS = 64
"""The most records one submission may name as exposed (.NET's ``ExperienceReuseFeedback.MaxExposedRecords``)."""

HUMAN_ASSESSMENT_PRODUCER = "experience-reuse-feedback/human-assessment"
"""The producer recorded on evidence a human assessment produced."""

REUSE_EVIDENCE_PRODUCER = "AgentExperience.ExperienceFinalizationService/reuse-evidence/1.0.0"
"""The producer on every piece of evidence finalization's reuse-evidence step submits."""

_VERIFICATION_STATUSES = ("Unknown", "Verified", "Failed")
_DERIVATION_NAMESPACE = uuid.UUID("3f5a1d62-8c04-4b91-a7e3-6d2f0b48c915")
_EVIDENCE_ID_TAG = 1
_EVENT_ID_TAG = 2
_SEALED_PLACEHOLDER = "(sealed)"
_DENIED_REASON = "The feedback's scope lies outside the host-established authorization."


# ---- The submission and what the ledger stores ---------------------------------------------------------------------


@dataclass(frozen=True)
class ReuseMeasure:
    """What the host measured about a run: a host-chosen kind and a finite number, never interpreted here."""

    kind: str
    value: float


@dataclass(frozen=True)
class HumanReuseAssessment:
    """
    An authorized human's judgement that reuse of named records helped or hurt a run (.NET's ``HumanReuseAssessment``).
    There is no reviewer field: the reviewer is the authorization's principal. Without independence verification
    (contract 1.17) ``assessment_token`` is neither checked nor spent; it is never printed.
    """

    assessment_id: uuid.UUID
    benefit: ExperienceReuseBenefit
    attributed_experience_ids: Sequence[uuid.UUID] | None
    rationale: str | None
    assessed_at: Instant
    verification_round_id: uuid.UUID | None = None
    assessment_token: str | None = field(default=None, repr=False)


@dataclass(frozen=True)
class ComparativeEvaluationResult:
    """A comparative evaluator's result about one run (.NET's ``ComparativeEvaluationResult``). This library does not
    implement an evaluator; it checks the result it is given."""

    evaluator_id: str | None
    run_id: uuid.UUID
    verification_round_id: uuid.UUID
    benefit: ExperienceReuseBenefit
    attributed_experience_ids: Sequence[uuid.UUID] | None
    evidence: Sequence[Evidence | None] | None
    summary: str | None
    evaluated_at: Instant


@dataclass(frozen=True)
class ExperienceReuseFeedback:
    """
    One submission about a run (.NET's ``ExperienceReuseFeedback``). ``feedback_id`` makes it idempotent;
    ``observed_at`` is the derived evidence's ``OccurredAt``, so it must not be regenerated on a retry.
    """

    feedback_id: uuid.UUID
    run_id: uuid.UUID
    scope: Scope
    exposed_experience_ids: Sequence[uuid.UUID] | None
    run_outcome: TaskVerificationStatus
    measure: ReuseMeasure | None
    observed_at: Instant
    claimed_benefit: ExperienceReuseBenefit = "Unknown"
    human_assessment: HumanReuseAssessment | None = None
    comparative_evaluation: ComparativeEvaluationResult | None = None
    trial_label: str | None = None


@dataclass(frozen=True)
class ExperienceReuseExposure:
    """One exposed record as the ledger stores it; ``evidence_id`` is set exactly when ``attributed``."""

    experience_id: uuid.UUID
    attributed: bool
    evidence_id: uuid.UUID | None


@dataclass(frozen=True)
class RecordedExperienceReuseFeedback:
    """A submission as the ledger stores it, with Core's attribution decision made (.NET's
    ``RecordedExperienceReuseFeedback``)."""

    feedback_id: uuid.UUID
    run_id: uuid.UUID
    scope: Scope
    run_outcome: TaskVerificationStatus
    claimed_benefit: ExperienceReuseBenefit
    benefit: ExperienceReuseBenefit
    attribution_source: ReuseAttributionSource
    reviewer_identity: str | None
    evaluator_id: str | None
    verification_round_id: uuid.UUID | None
    assessment_id: uuid.UUID | None
    rationale: str | None
    evidence_ids: Sequence[uuid.UUID] | None
    attributed_at: Instant | None
    measure: ReuseMeasure | None
    trial_label: str | None
    observed_at: Instant
    exposures: Sequence[ExperienceReuseExposure | None] | None


@dataclass(frozen=True)
class ExperienceReuseFeedbackStoreResult:
    """What a ledger write did (.NET's ``ExperienceReuseFeedbackStoreResult``)."""

    outcome: ExperienceReuseFeedbackStoreOutcome
    feedback: RecordedExperienceReuseFeedback | None
    errors: tuple[StoreValidationError, ...] = ()


@runtime_checkable
class ExperienceReuseFeedbackStore(Protocol):
    """The append-only feedback ledger (.NET's ``IExperienceReuseFeedbackStore``). Expected conditions are results; an
    infrastructure failure raises."""

    def record(
        self, authorization: AuthorizationContext, feedback: RecordedExperienceReuseFeedback
    ) -> ExperienceReuseFeedbackStoreResult: ...


@dataclass(frozen=True)
class ExperienceExposureResult:
    """What one exposed record came to (.NET's ``ExperienceExposureResult``)."""

    experience_id: uuid.UUID
    disposition: ExperienceExposureDisposition
    evidence_id: uuid.UUID | None
    counted: bool
    reuse_confidence: float | None
    status: ExperienceStatus | None
    retryable: bool
    reason: str | None


@dataclass(frozen=True)
class ExperienceReuseFeedbackResult:
    """What recording a submission did (.NET's ``ExperienceReuseFeedbackResult``)."""

    outcome: ExperienceReuseFeedbackOutcome
    feedback_id: uuid.UUID
    benefit: ExperienceReuseBenefit
    attribution_source: ReuseAttributionSource
    exposures: tuple[ExperienceExposureResult, ...]
    errors: tuple[StoreValidationError, ...] = ()
    reason: str | None = None

    @property
    def is_retryable(self) -> bool:
        """Whether any exposed record's evidence could still land by resubmitting the identical feedback."""
        return any(exposure.retryable for exposure in self.exposures)


# ---- The ledger's validation (.NET's ExperienceRecordValidator.ValidateReuseFeedback) ------------------------------


def _is_empty(value: uuid.UUID | None) -> bool:
    return value is not None and value.int == 0


def _unset(value: Instant) -> bool:
    return value.ticks == 0


def _require_null(value: object, path: str, errors: list[StoreValidationError]) -> None:
    if value is not None:
        errors.append(StoreValidationError(path, "must be null for this attribution source."))


def _require_empty(value: Sequence[object] | None, path: str, errors: list[StoreValidationError]) -> None:
    if value:
        errors.append(StoreValidationError(path, "must be empty for this attribution source."))


def _require_set(value: Instant | None, path: str, errors: list[StoreValidationError]) -> None:
    if value is None or _unset(value):
        errors.append(StoreValidationError(path, "must be set for this attribution source."))


def validate_reuse_feedback(feedback: RecordedExperienceReuseFeedback) -> tuple[StoreValidationError, ...]:
    """
    The ledger's own validation of what it will write, every error in .NET's order: the identifiers, the scope, every
    enum, the instants, the measure, the trial label, the attribution's shape, and the exposures.
    """
    errors: list[StoreValidationError] = []
    if _is_empty(feedback.feedback_id):
        errors.append(StoreValidationError("FeedbackId", "must not be an empty GUID."))
    if _is_empty(feedback.run_id):
        errors.append(StoreValidationError("RunId", "must not be an empty GUID."))
    _validate_scope(feedback.scope, "Scope", errors)
    _require_defined(feedback.run_outcome, _VERIFICATION_STATUSES, "RunOutcome", errors)
    _require_defined(feedback.claimed_benefit, EXPERIENCE_REUSE_BENEFITS, "ClaimedBenefit", errors)
    _require_defined(feedback.benefit, EXPERIENCE_REUSE_BENEFITS, "Benefit", errors)
    _require_defined(feedback.attribution_source, REUSE_ATTRIBUTION_SOURCES, "AttributionSource", errors)
    if _unset(feedback.observed_at):
        errors.append(StoreValidationError("ObservedAt", "must be set to when the feedback was observed."))
    if feedback.measure is None:
        errors.append(StoreValidationError("Measure", "is required."))
    else:
        _require_not_blank(feedback.measure.kind, "Measure.Kind", errors)
        if not math.isfinite(feedback.measure.value):
            errors.append(StoreValidationError("Measure.Value", "must be a finite number."))
    _require_null_or_not_blank(feedback.trial_label, "TrialLabel", errors)
    if feedback.attributed_at is not None and _unset(feedback.attributed_at):
        errors.append(StoreValidationError("AttributedAt", "must be set when the submission carries an attribution."))
    _validate_attribution_shape(feedback, errors)
    _validate_exposures(feedback, errors)
    return tuple(errors)


def _validate_attribution_shape(feedback: RecordedExperienceReuseFeedback, errors: list[StoreValidationError]) -> None:
    if (feedback.attribution_source == "None") != (feedback.benefit == "Unknown"):
        errors.append(
            StoreValidationError(
                "Benefit", "must be Unknown exactly when there is no attribution source, and named otherwise."
            )
        )
    _refuse_reserved_text(feedback.rationale, "Rationale", errors)
    if feedback.rationale == _SEALED_PLACEHOLDER:
        errors.append(
            StoreValidationError(
                "Rationale",
                f"must not be exactly '{_SEALED_PLACEHOLDER}', which is reserved for a sealed rationale.",
            )
        )
    evidence_ids = list(feedback.evidence_ids or ())
    if feedback.attribution_source == "HumanAssessment":
        _require_not_blank(feedback.reviewer_identity, "ReviewerIdentity", errors)
        _require_null(feedback.evaluator_id, "EvaluatorId", errors)
        _require_not_blank(feedback.rationale, "Rationale", errors)
        _require_set(feedback.attributed_at, "AttributedAt", errors)
        _require_empty(evidence_ids, "EvidenceIds", errors)
        if feedback.assessment_id is None or _is_empty(feedback.assessment_id):
            errors.append(
                StoreValidationError(
                    "AssessmentId", "must name the host-established review a human assessment came out of."
                )
            )
        if _is_empty(feedback.verification_round_id):
            errors.append(StoreValidationError("VerificationRoundId", "must name a verification round or be null."))
    elif feedback.attribution_source == "ComparativeEvaluation":
        _require_not_blank(feedback.evaluator_id, "EvaluatorId", errors)
        _require_null(feedback.reviewer_identity, "ReviewerIdentity", errors)
        _require_null(feedback.assessment_id, "AssessmentId", errors)
        _require_not_blank(feedback.rationale, "Rationale", errors)
        _require_set(feedback.attributed_at, "AttributedAt", errors)
        if feedback.verification_round_id is None or _is_empty(feedback.verification_round_id):
            errors.append(
                StoreValidationError(
                    "VerificationRoundId", "must name the verification round the comparison was made in."
                )
            )
        if not evidence_ids:
            errors.append(
                StoreValidationError("EvidenceIds", "must carry the evidence the comparison was reached from.")
            )
        elif any(_is_empty(item) for item in evidence_ids):
            errors.append(StoreValidationError("EvidenceIds", "must not contain an empty GUID."))
        elif len(set(evidence_ids)) != len(evidence_ids):
            errors.append(StoreValidationError("EvidenceIds", "must not name the same piece of evidence twice."))
    else:
        _require_null(feedback.reviewer_identity, "ReviewerIdentity", errors)
        _require_null(feedback.evaluator_id, "EvaluatorId", errors)
        _require_null(feedback.verification_round_id, "VerificationRoundId", errors)
        _require_null(feedback.assessment_id, "AssessmentId", errors)
        _require_null(feedback.rationale, "Rationale", errors)
        _require_null(feedback.attributed_at, "AttributedAt", errors)
        _require_empty(evidence_ids, "EvidenceIds", errors)


def _validate_exposures(feedback: RecordedExperienceReuseFeedback, errors: list[StoreValidationError]) -> None:
    path = "Exposures"
    exposures = feedback.exposures
    if exposures is None:
        errors.append(StoreValidationError(path, "is required."))
        return
    if len(exposures) == 0:
        errors.append(StoreValidationError(path, "must name at least one exposed record."))
        return
    if len(exposures) > MAX_EXPOSED_RECORDS:
        errors.append(StoreValidationError(path, f"must name at most {MAX_EXPOSED_RECORDS} records."))
    seen: set[uuid.UUID] = set()
    attributed_without_evidence = False
    unattributed_with_evidence = False
    for exposure in exposures:
        if exposure is None:
            errors.append(StoreValidationError(path, "must not contain a null exposure."))
            continue
        if _is_empty(exposure.experience_id):
            errors.append(StoreValidationError(f"{path}.ExperienceId", "must not be an empty GUID."))
        elif exposure.experience_id in seen:
            errors.append(StoreValidationError(path, "must not name the same record twice."))
        else:
            seen.add(exposure.experience_id)
        if exposure.attributed and (exposure.evidence_id is None or _is_empty(exposure.evidence_id)):
            attributed_without_evidence = True
        if not exposure.attributed and exposure.evidence_id is not None:
            unattributed_with_evidence = True
        if exposure.attributed and feedback.attribution_source == "None":
            errors.append(
                StoreValidationError(
                    path, "must not mark a record attributed when the submission carries no attribution."
                )
            )
    if attributed_without_evidence:
        errors.append(
            StoreValidationError(
                f"{path}.EvidenceId",
                "is required for an attributed exposure: it is the confidence submission's idempotency key.",
            )
        )
    if unattributed_with_evidence:
        errors.append(
            StoreValidationError(
                f"{path}.EvidenceId",
                "must be null for an exposure nothing attributed, which produced no confidence submission.",
            )
        )


def normalize_reuse_feedback(feedback: RecordedExperienceReuseFeedback) -> RecordedExperienceReuseFeedback:
    """The submission with its exposures in experience-ID order (.NET's ``Guid`` order, which is the lowercase text's),
    the one order a ledger keeps them in. Only for a submission :func:`validate_reuse_feedback` accepted."""
    exposures: list[ExperienceReuseExposure] = [
        exposure for exposure in feedback.exposures or () if exposure is not None
    ]
    exposures.sort(key=_exposure_order)
    return replace(feedback, exposures=tuple(exposures))


def _exposure_order(exposure: ExperienceReuseExposure) -> uuid.UUID:
    return exposure.experience_id


def same_reuse_feedback(stored: RecordedExperienceReuseFeedback, submitted: RecordedExperienceReuseFeedback) -> bool:
    """Whether the stored submission is the one in hand: every stored field, the instants as stored (whole
    microseconds), the two lists as sequences. Both must be normalized."""

    def truncated(value: Instant | None) -> Instant | None:
        return None if value is None else value.truncate_to_microseconds()

    stored_measure, submitted_measure = stored.measure, submitted.measure
    if stored_measure is None or submitted_measure is None:
        # A validated submission always has a measure; one without it is never the same as one with it.
        same_measure = stored_measure is None and submitted_measure is None
    else:
        same_measure = stored_measure.kind == submitted_measure.kind and stored_measure.value == submitted_measure.value
    return (
        stored.run_id == submitted.run_id
        and stored.scope == submitted.scope
        and stored.run_outcome == submitted.run_outcome
        and stored.claimed_benefit == submitted.claimed_benefit
        and stored.benefit == submitted.benefit
        and stored.attribution_source == submitted.attribution_source
        and stored.reviewer_identity == submitted.reviewer_identity
        and stored.evaluator_id == submitted.evaluator_id
        and stored.verification_round_id == submitted.verification_round_id
        and stored.assessment_id == submitted.assessment_id
        and stored.rationale == submitted.rationale
        and tuple(stored.evidence_ids or ()) == tuple(submitted.evidence_ids or ())
        and truncated(stored.attributed_at) == truncated(submitted.attributed_at)
        and same_measure
        and stored.trial_label == submitted.trial_label
        and stored.observed_at.truncate_to_microseconds() == submitted.observed_at.truncate_to_microseconds()
        and tuple(stored.exposures or ()) == tuple(submitted.exposures or ())
    )


def is_store_failure(error: BaseException) -> bool:
    """
    Whether ``error`` is a storage infrastructure failure (.NET's ``ExperienceStoreException``): an
    :class:`~agentexperience.ExperienceStoreError`, a psycopg error, or an ``OSError`` (a connection that broke).
    """
    return isinstance(error, (ExperienceStoreError, OSError)) or type(error).__module__.split(".", 1)[0] == "psycopg"


# ---- The service ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class _Attribution:
    source: ReuseAttributionSource
    benefit: ExperienceReuseBenefit
    attributed: tuple[uuid.UUID, ...]
    reviewer_identity: str | None
    evaluator_id: str | None
    verification_round_id: uuid.UUID | None
    assessment_id: uuid.UUID | None
    rationale: str | None
    evidence_ids: tuple[uuid.UUID, ...]
    attributed_at: Instant | None
    producer: str | None
    assessment_token: str | None = field(default=None, repr=False)

    def attributes(self, experience_id: uuid.UUID) -> bool:
        return self.source != "None" and experience_id in self.attributed

    @property
    def evidence_round_id(self) -> uuid.UUID | None:
        """The round the evidence is keyed on: a comparative result's, never a human's (audit only)."""
        return self.verification_round_id if self.source == "ComparativeEvaluation" else None


_NO_ATTRIBUTION = _Attribution("None", "Unknown", (), None, None, None, None, None, (), None, None)


def _attribution_of(feedback: ExperienceReuseFeedback, authorization: AuthorizationContext) -> _Attribution:
    human = feedback.human_assessment
    if human is not None:
        return _Attribution(
            "HumanAssessment",
            human.benefit,
            tuple(human.attributed_experience_ids or ()),
            authorization.principal_id,
            None,
            human.verification_round_id,
            human.assessment_id,
            human.rationale,
            (),
            human.assessed_at,
            HUMAN_ASSESSMENT_PRODUCER,
            human.assessment_token,
        )
    comparative = feedback.comparative_evaluation
    if comparative is not None:
        return _Attribution(
            "ComparativeEvaluation",
            comparative.benefit,
            tuple(comparative.attributed_experience_ids or ()),
            None,
            comparative.evaluator_id,
            comparative.verification_round_id,
            None,
            comparative.summary,
            tuple(item.evidence_id for item in comparative.evidence or () if item is not None),
            comparative.evaluated_at,
            comparative.evaluator_id,
        )
    return _NO_ATTRIBUTION


def _derive(feedback_id: uuid.UUID, experience_id: uuid.UUID, tag: int) -> uuid.UUID:
    digest = bytearray(
        hashlib.sha256(_DERIVATION_NAMESPACE.bytes + feedback_id.bytes + experience_id.bytes + bytes([tag])).digest()[
            :16
        ]
    )
    digest[6] = (digest[6] & 0x0F) | 0x80
    digest[8] = (digest[8] & 0x3F) | 0x80
    return uuid.UUID(bytes=bytes(digest))


class ExperienceReuseFeedbackService:
    """
    Records reuse feedback (.NET's ``ExperienceReuseFeedbackService``) into ``store`` and applies each accepted
    attribution's evidence through ``lifecycle_service``, the one path that moves confidence.
    """

    HUMAN_ASSESSMENT_PRODUCER = HUMAN_ASSESSMENT_PRODUCER

    def __init__(self, store: ExperienceReuseFeedbackStore, lifecycle_service: ExperienceLifecycleService) -> None:
        if store is None or lifecycle_service is None:
            raise TypeError("store and lifecycle_service must not be None.")
        self._store = store
        self._lifecycle = lifecycle_service

    @staticmethod
    def evidence_id_for(feedback_id: uuid.UUID, experience_id: uuid.UUID) -> uuid.UUID:
        """The evidence ID feedback ``feedback_id`` submits for ``experience_id`` (SHA-256, tag 1, UUIDv8)."""
        return _derive(feedback_id, experience_id, _EVIDENCE_ID_TAG)

    @staticmethod
    def event_id_for(feedback_id: uuid.UUID, experience_id: uuid.UUID) -> uuid.UUID:
        """The event ID feedback ``feedback_id`` commits for ``experience_id`` (SHA-256, tag 2, UUIDv8)."""
        return _derive(feedback_id, experience_id, _EVENT_ID_TAG)

    def record(
        self, authorization: AuthorizationContext, feedback: ExperienceReuseFeedback
    ) -> ExperienceReuseFeedbackResult:
        """
        Validates the submission, decides whether it carries an attribution this library accepts, writes the ledger,
        then submits evidence for each attributed record (.NET's ``RecordAsync``). A failure of the ledger write
        raises, nothing recorded; a store or engine failure of one record's evidence is that record's ``Failed``,
        retryable, and the other records are still submitted. It is reported as the ``reuse_feedback`` operation.
        """
        with observe("reuse_feedback") as op:
            if feedback is None:
                raise TypeError("feedback must not be None.")
            if op is not None:
                op.set("agentexperience.feedback_id", str(feedback.feedback_id))
                op.set("agentexperience.run_id", str(feedback.run_id))
            result = self._record(authorization, feedback)
            if op is not None:
                op.outcome(result.outcome)
        return result

    def _record(
        self, authorization: AuthorizationContext, feedback: ExperienceReuseFeedback
    ) -> ExperienceReuseFeedbackResult:
        if authorization is None or feedback.scope is None:
            raise TypeError("authorization and feedback.scope must not be None.")

        fatal, degrading = _validate(feedback, authorization)
        if fatal:
            return _ended("Invalid", feedback.feedback_id, tuple(fatal))
        if not authorization.permits(feedback.scope):
            return _ended("Denied", feedback.feedback_id, (), _DENIED_REASON)

        # No verification (contract 1.17): no run lookup and no signer, so an attribution degrades only by validation.
        attribution = _NO_ATTRIBUTION if degrading else _attribution_of(feedback, authorization)
        degraded_reason = (
            "The attribution did not meet its evidence requirements, so the exposure was recorded with benefit "
            "Unknown and no confidence submission: " + "; ".join(f"{error.path} {error.message}" for error in degrading)
            if degrading
            else None
        )
        submission = self._ledger_submission(feedback, attribution)
        stored = self._store.record(authorization, submission)

        if stored.outcome == "Conflict":
            conflicting = stored.feedback
            exposures = (
                tuple(
                    ExperienceExposureResult(
                        exposure.experience_id,
                        "Refused",
                        exposure.evidence_id,
                        False,
                        None,
                        None,
                        False,
                        "Recorded by the submission already stored under this feedback ID, not by this call.",
                    )
                    for exposure in conflicting.exposures or ()
                    if exposure is not None
                )
                if conflicting is not None
                else ()
            )
            return ExperienceReuseFeedbackResult(
                "Conflict",
                feedback.feedback_id,
                "Unknown",
                "None",
                exposures,
                (),
                "This feedback ID is already recorded with different content; nothing was written.",
            )
        if stored.outcome == "Denied":
            return _ended("Denied", feedback.feedback_id, (), _DENIED_REASON)
        if stored.outcome not in ("Recorded", "AlreadyRecorded"):
            return _ended("Invalid", feedback.feedback_id, tuple(stored.errors))

        results: list[ExperienceExposureResult] = []
        for exposure in submission.exposures or ():
            assert exposure is not None
            if not exposure.attributed:
                results.append(
                    ExperienceExposureResult(
                        exposure.experience_id,
                        "ExposureOnly",
                        None,
                        False,
                        None,
                        None,
                        False,
                        (degraded_reason or "Exposure without attribution evidence moves nothing.")
                        if attribution.source == "None"
                        else "The accepted attribution did not name this record.",
                    )
                )
                continue
            results.append(self._submit_evidence(authorization, feedback, attribution, exposure))

        return ExperienceReuseFeedbackResult(
            "AlreadyRecorded" if stored.outcome == "AlreadyRecorded" else "Recorded",
            feedback.feedback_id,
            attribution.benefit,
            attribution.source,
            tuple(results),
            (),
            degraded_reason,
        )

    def _submit_evidence(
        self,
        authorization: AuthorizationContext,
        feedback: ExperienceReuseFeedback,
        attribution: _Attribution,
        exposure: ExperienceReuseExposure,
    ) -> ExperienceExposureResult:
        # The ledger's ID, never re-derived: an attributed exposure always carries it.
        evidence_id = exposure.evidence_id
        assert evidence_id is not None and attribution.producer is not None
        request = ApplyConfidenceEvidenceRequest(
            event_id=self.event_id_for(feedback.feedback_id, exposure.experience_id),
            experience_id=exposure.experience_id,
            scope=feedback.scope,
            evidence_id=evidence_id,
            kind="Contradicting" if attribution.benefit == "Harmed" else "Supporting",
            source="Human" if attribution.source == "HumanAssessment" else "Machine",
            run_id=feedback.run_id,
            verification_round_id=attribution.evidence_round_id,
            reason=f"Reuse feedback {feedback.feedback_id} attributed "
            f"{'harm' if attribution.benefit == 'Harmed' else 'improvement'} to this record.",
            producer=attribution.producer,
            occurred_at=feedback.observed_at,
            detail=attribution.rationale,
            assessment_token=attribution.assessment_token,
        )
        try:
            applied = self._lifecycle.apply_evidence(authorization, request)
        except Exception as error:
            if not (is_store_failure(error) or is_engine_failure(error)):
                raise
            # The exposure is durable, so this is one record's retryable failure, not the submission's.
            return ExperienceExposureResult(
                exposure.experience_id, "Failed", evidence_id, False, None, None, True, str(error)
            )

        outcome = applied.outcome
        if outcome == "Applied":
            return ExperienceExposureResult(
                exposure.experience_id,
                "EvidenceApplied",
                evidence_id,
                applied.counted,
                None if applied.update is None else applied.update.new_reuse_confidence,
                applied.status,
                False,
                None
                if applied.counted
                else "Host-trusted evidence is recorded without being counted (HostTrustedEvidenceEffect.RecordedOnly)."
                if applied.recorded_on_event
                else "The same run already produced evidence for this record; recorded, not counted.",
            )
        if outcome == "Ineligible":
            return ExperienceExposureResult(
                exposure.experience_id, "Ineligible", evidence_id, False, None, applied.status, False, applied.reason
            )
        if outcome in ("NotFound", "Denied"):
            return ExperienceExposureResult(
                exposure.experience_id,
                "Unresolved",
                evidence_id,
                False,
                None,
                None,
                False,
                applied.reason or "No such record within the feedback's scope.",
            )
        if outcome in ("StaleRevision", "StatusMismatch"):
            return ExperienceExposureResult(
                exposure.experience_id,
                "Failed",
                evidence_id,
                False,
                None,
                applied.status,
                True,
                "The record moved between the read and the commit; resubmit the same feedback ID.",
            )
        if outcome == "Deleted":
            return ExperienceExposureResult(
                exposure.experience_id,
                "Refused",
                evidence_id,
                False,
                None,
                None,
                False,
                applied.reason
                or "The record was erased after this exposure was recorded; no evidence can be applied to it.",
            )
        return ExperienceExposureResult(
            exposure.experience_id,
            "Refused",
            evidence_id,
            False,
            None,
            applied.status,
            False,
            "; ".join(f"{error.path}: {error.message}" for error in applied.errors)
            if applied.errors
            else applied.reason or "The confidence path refused this submission.",
        )

    def _ledger_submission(
        self, feedback: ExperienceReuseFeedback, attribution: _Attribution
    ) -> RecordedExperienceReuseFeedback:
        exposures = []
        for experience_id in sorted(feedback.exposed_experience_ids or ()):
            attributed = attribution.attributes(experience_id)
            exposures.append(
                ExperienceReuseExposure(
                    experience_id,
                    attributed,
                    self.evidence_id_for(feedback.feedback_id, experience_id) if attributed else None,
                )
            )
        return RecordedExperienceReuseFeedback(
            feedback_id=feedback.feedback_id,
            run_id=feedback.run_id,
            scope=feedback.scope,
            run_outcome=feedback.run_outcome,
            claimed_benefit=feedback.claimed_benefit,
            benefit=attribution.benefit,
            attribution_source=attribution.source,
            reviewer_identity=attribution.reviewer_identity,
            evaluator_id=attribution.evaluator_id,
            verification_round_id=attribution.verification_round_id,
            assessment_id=attribution.assessment_id,
            rationale=attribution.rationale,
            evidence_ids=attribution.evidence_ids,
            attributed_at=attribution.attributed_at,
            measure=feedback.measure,
            trial_label=feedback.trial_label,
            observed_at=feedback.observed_at,
            exposures=tuple(exposures),
        )


def _ended(
    outcome: ExperienceReuseFeedbackOutcome,
    feedback_id: uuid.UUID,
    errors: tuple[StoreValidationError, ...],
    reason: str | None = None,
) -> ExperienceReuseFeedbackResult:
    return ExperienceReuseFeedbackResult(outcome, feedback_id, "Unknown", "None", (), errors, reason)


# ---- The service's validation --------------------------------------------------------------------------------------


def _validate(
    feedback: ExperienceReuseFeedback, authorization: AuthorizationContext
) -> tuple[list[StoreValidationError], list[StoreValidationError]]:
    fatal: list[StoreValidationError] = []
    degrading: list[StoreValidationError] = []
    if feedback.feedback_id.int == 0:
        fatal.append(
            StoreValidationError("FeedbackId", "must not be an empty GUID: it is the submission's idempotency key.")
        )
    if feedback.run_id.int == 0:
        fatal.append(StoreValidationError("RunId", "must name the run the records were injected into."))
    if feedback.run_outcome not in _VERIFICATION_STATUSES:
        fatal.append(StoreValidationError("RunOutcome", "must be a defined task verification status."))
    if feedback.claimed_benefit not in EXPERIENCE_REUSE_BENEFITS:
        fatal.append(StoreValidationError("ClaimedBenefit", "must be a defined benefit."))
    if _unset(feedback.observed_at):
        fatal.append(StoreValidationError("ObservedAt", "must be set to when the feedback was observed."))
    if feedback.trial_label is not None and is_null_or_whitespace(feedback.trial_label):
        fatal.append(StoreValidationError("TrialLabel", "must be non-blank when supplied; omit it instead."))
    _validate_measure(feedback.measure, fatal)
    exposed = _validate_exposed(feedback.exposed_experience_ids, fatal)

    if feedback.human_assessment is not None and feedback.comparative_evaluation is not None:
        fatal.append(
            StoreValidationError(
                "HumanAssessment",
                "a submission carries at most one attribution: a human assessment or a comparative evaluation, never "
                "both.",
            )
        )
        return fatal, degrading
    if feedback.human_assessment is not None:
        _validate_human(feedback.human_assessment, exposed, authorization, fatal, degrading)
    elif feedback.comparative_evaluation is not None:
        _validate_comparative(feedback.comparative_evaluation, feedback.run_id, exposed, fatal, degrading)
    return fatal, degrading


def _validate_measure(measure: ReuseMeasure | None, fatal: list[StoreValidationError]) -> None:
    if measure is None:
        fatal.append(StoreValidationError("Measure", "must name what was measured about the run."))
        return
    if is_null_or_whitespace(measure.kind):
        fatal.append(StoreValidationError("Measure.Kind", "must be a non-blank name for what was measured."))
    if not math.isfinite(measure.value):
        fatal.append(StoreValidationError("Measure.Value", "must be a finite number."))


def _validate_exposed(ids: Sequence[uuid.UUID] | None, fatal: list[StoreValidationError]) -> set[uuid.UUID]:
    path = "ExposedExperienceIds"
    exposed: set[uuid.UUID] = set()
    if ids is None:
        fatal.append(StoreValidationError(path, "must name the records the run was exposed to."))
        return exposed
    if len(ids) == 0:
        fatal.append(
            StoreValidationError(path, "must name at least one record: feedback about no exposure records nothing.")
        )
        return exposed
    if len(ids) > MAX_EXPOSED_RECORDS:
        fatal.append(StoreValidationError(path, f"must name at most {MAX_EXPOSED_RECORDS} records."))
    for experience_id in ids:
        if experience_id.int == 0:
            fatal.append(StoreValidationError(path, "must not contain an empty GUID."))
        elif experience_id in exposed:
            fatal.append(StoreValidationError(path, "must not name the same record twice."))
        else:
            exposed.add(experience_id)
    return exposed


def _validate_human(
    assessment: HumanReuseAssessment,
    exposed: set[uuid.UUID],
    authorization: AuthorizationContext,
    fatal: list[StoreValidationError],
    degrading: list[StoreValidationError],
) -> None:
    path = "HumanAssessment"
    _validate_attributed(
        assessment.attributed_experience_ids, exposed, f"{path}.AttributedExperienceIds", fatal, degrading
    )
    _validate_benefit(assessment.benefit, f"{path}.Benefit", degrading)
    if assessment.assessment_id.int == 0:
        degrading.append(
            StoreValidationError(
                f"{path}.AssessmentId", "must name the host-established review this judgement came out of."
            )
        )
    if _is_empty(assessment.verification_round_id):
        degrading.append(
            StoreValidationError(
                f"{path}.VerificationRoundId", "must name a verification round or be omitted; an empty GUID is neither."
            )
        )
    if is_null_or_whitespace(assessment.rationale):
        degrading.append(StoreValidationError(f"{path}.Rationale", "must be a non-blank, auditable rationale."))
    if _unset(assessment.assessed_at):
        degrading.append(StoreValidationError(f"{path}.AssessedAt", "must be set to when the assessment was made."))
    principal = authorization.principal_id
    if is_null_or_whitespace(principal):
        degrading.append(
            StoreValidationError(
                "Authorization.PrincipalId",
                "must be non-blank for a human assessment: it is the reviewer the attribution is counted under.",
            )
        )
    else:
        assert principal is not None
        if is_whitespace(principal[0]) or is_whitespace(principal[-1]):
            degrading.append(
                StoreValidationError(
                    "Authorization.PrincipalId",
                    "must not have leading or trailing whitespace: it would be counted as a second, independent "
                    "reviewer.",
                )
            )


def _validate_comparative(
    comparative: ComparativeEvaluationResult,
    run_id: uuid.UUID,
    exposed: set[uuid.UUID],
    fatal: list[StoreValidationError],
    degrading: list[StoreValidationError],
) -> None:
    path = "ComparativeEvaluation"
    _validate_attributed(
        comparative.attributed_experience_ids, exposed, f"{path}.AttributedExperienceIds", fatal, degrading
    )
    _validate_benefit(comparative.benefit, f"{path}.Benefit", degrading)
    if comparative.run_id != run_id:
        fatal.append(StoreValidationError(f"{path}.RunId", "must be the run this feedback is about."))
    if is_null_or_whitespace(comparative.evaluator_id):
        degrading.append(StoreValidationError(f"{path}.EvaluatorId", "must be a non-blank evaluator identity."))
    if comparative.verification_round_id.int == 0:
        degrading.append(
            StoreValidationError(
                f"{path}.VerificationRoundId",
                "must name the verification round the comparison was made in: it is half of the machine independence "
                "key.",
            )
        )
    evidence_path = f"{path}.Evidence"
    if not comparative.evidence:
        degrading.append(
            StoreValidationError(evidence_path, "must carry the evidence the comparison was reached from.")
        )
    else:
        seen: set[uuid.UUID] = set()
        for item in comparative.evidence:
            if item is None:
                degrading.append(StoreValidationError(evidence_path, "must not contain a null piece of evidence."))
                continue
            if item.evidence_id.int == 0:
                degrading.append(StoreValidationError(f"{evidence_path}.EvidenceId", "must not be an empty GUID."))
            elif item.evidence_id in seen:
                degrading.append(
                    StoreValidationError(
                        f"{evidence_path}.EvidenceId", "must not name the same piece of evidence twice."
                    )
                )
            else:
                seen.add(item.evidence_id)
            if item.verification_round_id != comparative.verification_round_id:
                degrading.append(
                    StoreValidationError(
                        f"{evidence_path}.VerificationRoundId",
                        "must be the round the result names: evidence from another round is not evidence about this "
                        "comparison.",
                    )
                )
    if is_null_or_whitespace(comparative.summary):
        degrading.append(StoreValidationError(f"{path}.Summary", "must be a non-blank, auditable summary."))
    if _unset(comparative.evaluated_at):
        degrading.append(StoreValidationError(f"{path}.EvaluatedAt", "must be set to when the comparison was made."))


def _validate_benefit(benefit: str, path: str, degrading: list[StoreValidationError]) -> None:
    if benefit not in EXPERIENCE_REUSE_BENEFITS:
        degrading.append(StoreValidationError(path, "must be a defined benefit."))
    elif benefit == "Unknown":
        degrading.append(
            StoreValidationError(
                path,
                "must attribute improvement or harm; an attribution of Unknown is not an attribution, so omit it "
                "instead.",
            )
        )


def _validate_attributed(
    ids: Sequence[uuid.UUID] | None,
    exposed: set[uuid.UUID],
    path: str,
    fatal: list[StoreValidationError],
    degrading: list[StoreValidationError],
) -> None:
    if not ids:
        degrading.append(StoreValidationError(path, "must name at least one exposed record."))
        return
    seen: set[uuid.UUID] = set()
    for experience_id in ids:
        if experience_id in seen:
            degrading.append(StoreValidationError(path, "must not name the same record twice."))
            continue
        seen.add(experience_id)
        if experience_id not in exposed:
            fatal.append(StoreValidationError(path, "must name only records the run was exposed to."))


# ---- Finalization's reuse evidence (.NET's ExperienceFinalizationOptions.ReuseEvidence) ----------------------------


@dataclass(frozen=True)
class ReuseEvidenceResult:
    """What the reuse-evidence step did for one record the run was given (.NET's ``ReuseEvidenceResult``).
    ``exception_type`` is this port's own type name of what the step caught."""

    experience_id: uuid.UUID
    kind: ConfidenceEvidenceKind
    outcome: ConfidenceUpdateOutcome | None
    reason: str | None
    counted: bool = False
    skipped: bool = False
    replay: bool = False
    refusal: IndependenceRefusal | None = None
    exception_type: str | None = None


@dataclass(frozen=True)
class ReuseEvidenceReport:
    """Every record the step reported on, and whether it stopped early (never, in this synchronous port)."""

    results: tuple[ReuseEvidenceResult, ...] = ()
    truncated: bool = False


def apply_reuse_evidence(
    service: ExperienceLifecycleService,
    authorization: AuthorizationContext,
    record: ExperienceRecord,
    *,
    reuse_evidence: ReuseEvidenceMode = "Off",
    contradict_on_failure: bool = False,
) -> ReuseEvidenceReport:
    """
    Finalization's opt-in reuse-evidence step (.NET's ``TryApplyReuseEvidenceAsync``), for a run's durable ``record``:
    under ``reuse_evidence="SameTask"``, Machine evidence about each record the run was given (its provenance's
    exposures) that is readable in its scope and on its task, bound to the round finalization closed, under the derived
    IDs of tags 4 and 5: Supporting for a verified run, Contradicting for a failed one when ``contradict_on_failure``.

    A port's finalization is a pure function, so call this once the finalized record is stored (after
    ``insert_finalized``), and again, with the stored record, for a run already finalized: a replay submits the same
    evidence and the store reports the originals (``replay``), counting nothing twice. Best effort: every refusal and
    every exception is reported, never raised. ``Off`` (the default) submits nothing. There is no time budget: this
    port's lifecycle calls are synchronous.
    """
    if reuse_evidence not in REUSE_EVIDENCE_MODES:
        raise ValueError("reuse_evidence must be 'Off' or 'SameTask'.")
    if reuse_evidence == "Off":
        return ReuseEvidenceReport()
    status = record.outcome.status
    kind: ConfidenceEvidenceKind
    if status == "Verified":
        kind = "Supporting"
    elif status == "Failed" and contradict_on_failure:
        kind = "Contradicting"
    else:
        return ReuseEvidenceReport()
    round_id = record.closed_round_id
    exposures = record.provenance.exposed_to if record.provenance is not None else ()
    if round_id is None or not exposures:
        return ReuseEvidenceReport()

    candidates: list[uuid.UUID] = []
    for exposure in exposures:
        if exposure is None or exposure.experience_id.int == 0 or exposure.experience_id == record.experience_id:
            continue
        if exposure.experience_id not in candidates:
            candidates.append(exposure.experience_id)
    candidates = candidates[:MAX_EXPOSURES_PER_RUN]

    results: list[ReuseEvidenceResult] = []
    for experience_id in candidates:
        try:
            read = service.store.read_record(authorization, record.scope, experience_id)
            skipped = _skip_reason(read.outcome, read.record, record)
            if skipped is not None:
                results.append(ReuseEvidenceResult(experience_id, kind, None, skipped, skipped=True))
                continue
            applied = service.apply_evidence(
                authorization,
                ApplyConfidenceEvidenceRequest(
                    event_id=reuse_event_id_for(record.source_run_id, record.scope, experience_id, kind),
                    experience_id=experience_id,
                    scope=record.scope,
                    evidence_id=reuse_evidence_id_for(record.source_run_id, record.scope, experience_id, kind),
                    kind=kind,
                    source="Machine",
                    run_id=record.source_run_id,
                    verification_round_id=round_id,
                    reason=f"Reuse on the same task: run {record.source_run_id} was given this record and its "
                    f"verification {'passed' if kind == 'Supporting' else 'failed'}.",
                    producer=REUSE_EVIDENCE_PRODUCER,
                    occurred_at=record.created_at,
                ),
            )
            replay = (
                applied.outcome == "Applied"
                and applied.counted
                and applied.event is not None
                and applied.revision != applied.event.expected_revision + 1
            )
            results.append(
                ReuseEvidenceResult(
                    experience_id,
                    kind,
                    applied.outcome,
                    applied.reason,
                    counted=applied.outcome == "Applied" and applied.counted and not replay,
                    replay=replay,
                    refusal=applied.refusal,
                )
            )
        except Exception as error:
            name = type_name(error)
            results.append(
                ReuseEvidenceResult(
                    experience_id,
                    kind,
                    None,
                    f"Applying reuse evidence threw {name}; the run's record is durable and nothing about it changed. "
                    "Finalize the run again to resubmit.",
                    exception_type=name,
                )
            )
    return ReuseEvidenceReport(tuple(results), False)


def _skip_reason(outcome: str, target: ExperienceRecord | None, run: ExperienceRecord) -> str | None:
    if outcome != "Found":
        return f"The record could not be read in the run's scope ({outcome}); no evidence was submitted."
    if target is None:
        return "The store reported the record found but returned none; no evidence was submitted."
    if target.task_id != run.task_id:
        return "The record is on another task; no evidence was submitted."
    if target.source_run_id == run.source_run_id:
        return "The record came from this run; no evidence was submitted."
    return None
