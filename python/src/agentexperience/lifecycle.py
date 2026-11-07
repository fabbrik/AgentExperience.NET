"""
A stored lesson's lifecycle (contract 1.16), exactly as .NET's ``ExperienceLifecycleService`` moves it over its stores:
a transition through the table (:meth:`ExperienceLifecycleService.commit`), one piece of reuse evidence scored by
contract 1.15's engine (:meth:`ExperienceLifecycleService.apply_evidence`), and the score read back from the history
with host-trusted or unverified evidence left out (:meth:`ExperienceLifecycleService.read_confidence`).

The service runs as .NET's does under ``IndependenceVerification.TrustHostSuppliedIdentifiers``, with no provenance
signing, no indexing service and no capture service: only the own-run rule is checked, an assessment token is neither
checked nor spent, and every update is admitted ``HostTrusted``. By default (``host_trusted_evidence="RecordedOnly"``)
such evidence is recorded without moving the record, as .NET does with that effect; ``"Counted"`` is an opt-in.

The store operations it uses are :class:`LifecycleStore`'s, which both stores implement: read a record in exactly its
scope, commit one lifecycle event (the revision compare-and-set, idempotency by event and evidence ID, the independence
key, the single-use assessment, the in-transaction supersession gate), page a history, and check a supersession. Every
request is validated, then authorized, before a store touches anything; every outcome, reason and validation message is
.NET's, byte for byte and in .NET's order.

Outcomes, statuses, kinds, sources, admissions and filters are .NET's member names. A value no member names (a status
``Archived``) is passed through as it is and refused where .NET refuses it.
"""

from __future__ import annotations

import math
import uuid
from collections.abc import Sequence
from dataclasses import dataclass, field, replace
from typing import Literal, Protocol, runtime_checkable

from ._text import is_null_or_whitespace, is_whitespace
from .confidence import (
    ACCEPTS_EVIDENCE,
    ASSESSMENT_ID_PATH,
    CONFIDENCE_EVIDENCE_ADMISSIONS,
    CONFIDENCE_EVIDENCE_KINDS,
    CONFIDENCE_EVIDENCE_SOURCES,
    HOST_TRUSTED_EVIDENCE_EFFECTS,
    MAX_COUNTER,
    ConfidenceArgumentError,
    ConfidenceEngineRule,
    ConfidenceEvidenceAdmission,
    ConfidenceEvidenceKind,
    ConfidenceEvidenceSource,
    ConfidenceUpdate,
    ExperienceConfidenceEngine,
    HostTrustedEvidenceEffect,
    IndependenceRefusal,
    accepts_evidence_in,
    independence_key_for,
    status_after,
)
from .encoding import Instant
from .ids import reuse_event_id_for, reuse_evidence_id_for
from .observer import observe
from .records import EXPERIENCE_STATUSES, ExperienceRecord, ExperienceStatus, Origin, Scope
from .search import ELIGIBLE_STATUSES, AuthorizationBounds

__all__ = [
    "CONFIDENCE_EVIDENCE_FILTERS",
    "FIRST_EVENT_STATUS",
    "HISTORY_DEFAULT_LIMIT",
    "HISTORY_MAX_LIMIT",
    "HISTORY_MIN_LIMIT",
    "ApplyConfidenceEvidenceRequest",
    "ApplyConfidenceEvidenceResult",
    "AuthorizationContext",
    "CommitLifecycleTransitionRequest",
    "CommitLifecycleTransitionResult",
    "ConfidenceAdmissionCounts",
    "ConfidenceEvidenceFilter",
    "ConfidenceReadResult",
    "ConfidenceReport",
    "ConfidenceUpdateOutcome",
    "ExperienceLifecycleService",
    "ExperienceStoreError",
    "ExperienceStoreOutcome",
    "ExperienceSupersessionOutcome",
    "HistoryQuery",
    "HistoryResult",
    "LifecycleCommitResult",
    "LifecycleEvent",
    "LifecycleStore",
    "LifecycleTransitionOutcome",
    "RecordReadResult",
    "StoreValidationError",
    "StoredLifecycleEvent",
    "SupersessionCheckResult",
    "reuse_event_id_for",
    "reuse_evidence_id_for",
]

ExperienceStoreOutcome = Literal[
    "Created",
    "Found",
    "NotFound",
    "Denied",
    "Invalid",
    "Conflict",
    "Committed",
    "StaleRevision",
    "StatusMismatch",
    "ReplacementNotAllowed",
    "Deleted",
]
""".NET's ``ExperienceStoreOutcome``."""

LifecycleTransitionOutcome = Literal[
    "Committed",
    "TransitionNotAllowed",
    "ReplacementNotAllowed",
    "StaleRevision",
    "StatusMismatch",
    "Conflict",
    "NotFound",
    "Denied",
    "Invalid",
    "Deleted",
]
""".NET's ``LifecycleTransitionOutcome``."""

ConfidenceUpdateOutcome = Literal[
    "Applied",
    "Ineligible",
    "StaleRevision",
    "StatusMismatch",
    "Conflict",
    "NotFound",
    "Denied",
    "Invalid",
    "Deleted",
    "Unverified",
]
""".NET's ``ConfidenceUpdateOutcome``."""

ConfidenceEvidenceFilter = Literal["All", "ExcludeHostTrusted", "VerifiedOnly"]
""".NET's ``ConfidenceEvidenceFilter``: which evidence :meth:`ExperienceLifecycleService.read_confidence` counts."""
CONFIDENCE_EVIDENCE_FILTERS: tuple[ConfidenceEvidenceFilter, ...] = ("All", "ExcludeHostTrusted", "VerifiedOnly")

ExperienceSupersessionOutcome = Literal[
    "Allowed", "RecordNotFound", "ReplacementNotFound", "Cycle", "Denied", "Invalid"
]
""".NET's ``ExperienceSupersessionOutcome``."""

FIRST_EVENT_STATUS: ExperienceStatus = "Candidate"
"""The only status a record's first event (the one with no prior status) may record (.NET's ``FirstEventStatus``)."""

HISTORY_MIN_LIMIT = 1
HISTORY_MAX_LIMIT = 500
HISTORY_DEFAULT_LIMIT = 100

_EMPTY = uuid.UUID(int=0)
_MAX_REVISION = 2**63 - 1
_REQUIRED = "is required."
_NOT_BLANK = "must not be empty or whitespace."
_SEALED_PREFIX = "aexp-sealed:v1:"


class ExperienceStoreError(Exception):
    """A store answered something no lifecycle operation can (.NET's ``ExperienceStoreException``)."""


# ---- Requests, events and results ----------------------------------------------------------------------------------


@dataclass(frozen=True)
class StoreValidationError:
    """One reason a request is invalid: the field's path and .NET's message."""

    path: str
    message: str


@dataclass(frozen=True)
class AuthorizationContext:
    """
    What the host has established the caller may do (.NET's ``AuthorizationContext``, as far as the lifecycle uses it):
    the principal (the actor every event and evidence row records, and the reviewer of human evidence) and the bounds
    every transaction declares. It permits a scope when its tenant is non-blank and equal, and every bound it sets is
    equal (ordinal).
    """

    principal_id: str | None
    bounds: AuthorizationBounds

    def permits(self, scope: Scope) -> bool:
        return not is_null_or_whitespace(self.bounds.tenant_id) and self.bounds.permits(scope)

    @property
    def actor(self) -> str | None:
        """The principal, or ``None`` when it is blank: what a store records as an event's actor."""
        return None if is_null_or_whitespace(self.principal_id) else self.principal_id


@dataclass(frozen=True)
class LifecycleEvent:
    """One lifecycle transition, as Core stamps it and the store persists it (.NET's ``LifecycleEvent``)."""

    event_id: uuid.UUID
    experience_record_id: uuid.UUID
    prior_status: ExperienceStatus | None
    current_status: ExperienceStatus
    reason: str
    producer: str
    occurred_at: Instant
    expected_revision: int
    replacement_experience_id: uuid.UUID | None = None
    confidence: ConfidenceUpdate | None = None


@dataclass(frozen=True)
class StoredLifecycleEvent:
    """A committed event, with what only the store knows about it: when it recorded it, the revision it produced, and
    the actor (``None`` for a blank principal)."""

    event: LifecycleEvent
    recorded_at: Instant
    applied_revision: int
    actor: str | None = None


@dataclass(frozen=True)
class LifecycleCommitResult:
    """A store's answer to a lifecycle commit (.NET's ``ExperienceLifecycleCommitResult``)."""

    outcome: ExperienceStoreOutcome
    revision: int
    current_status: ExperienceStatus | None
    errors: tuple[StoreValidationError, ...] = ()
    applied_confidence: ConfidenceUpdate | None = None


@dataclass(frozen=True)
class HistoryQuery:
    """One page of a record's history (.NET's ``ExperienceRecordHistoryQuery``): events after
    ``start_after_revision`` (``None``: from the start), at most ``limit`` (1 to 500)."""

    scope: Scope
    experience_id: uuid.UUID
    limit: int = HISTORY_DEFAULT_LIMIT
    start_after_revision: int | None = None


@dataclass(frozen=True)
class HistoryResult:
    """A page of a history (.NET's ``ExperienceRecordHistoryResult``): the record's revision, the events oldest first,
    and the cursor for the next page (``None`` when this page is empty)."""

    outcome: ExperienceStoreOutcome
    revision: int
    events: tuple[StoredLifecycleEvent, ...]
    errors: tuple[StoreValidationError, ...] = ()
    next_start_after_revision: int | None = None


@dataclass(frozen=True)
class SupersessionCheckResult:
    """.NET's ``ExperienceSupersessionCheckResult``: whether the replacement may replace the record, and its status."""

    outcome: ExperienceSupersessionOutcome
    replacement_status: ExperienceStatus | None
    errors: tuple[StoreValidationError, ...] = ()


@dataclass(frozen=True)
class RecordReadResult:
    """A read of one record in exactly its scope, as the lifecycle makes it: ``Found`` with the record, or why not."""

    outcome: ExperienceStoreOutcome
    record: ExperienceRecord | None
    errors: tuple[StoreValidationError, ...] = ()


@dataclass(frozen=True)
class CommitLifecycleTransitionRequest:
    """A transition to commit (.NET's ``CommitLifecycleTransitionRequest``). ``prior_status`` ``None`` is a record's
    first event; ``replacement_experience_id`` is named exactly when the transition is to ``Superseded``."""

    event_id: uuid.UUID
    experience_id: uuid.UUID
    scope: Scope
    prior_status: ExperienceStatus | None
    current_status: ExperienceStatus
    reason: str
    producer: str
    occurred_at: Instant
    expected_revision: int
    replacement_experience_id: uuid.UUID | None = None


@dataclass(frozen=True)
class CommitLifecycleTransitionResult:
    """What a commit did (.NET's ``CommitLifecycleTransitionResult``, with no de-indexing result: a port has no
    indexing service). ``event`` is the event stamped, ``None`` when it refused before stamping one."""

    outcome: LifecycleTransitionOutcome
    event: LifecycleEvent | None
    revision: int
    current_status: ExperienceStatus | None
    errors: tuple[StoreValidationError, ...] = ()
    reason: str | None = None


@dataclass(frozen=True)
class ApplyConfidenceEvidenceRequest:
    """One piece of reuse evidence (.NET's ``ApplyConfidenceEvidenceRequest``). The reviewer of human evidence is the
    authorization's principal, never the request's. ``assessment_token`` is never printed."""

    event_id: uuid.UUID
    experience_id: uuid.UUID
    scope: Scope
    evidence_id: uuid.UUID
    kind: ConfidenceEvidenceKind
    source: ConfidenceEvidenceSource
    run_id: uuid.UUID
    verification_round_id: uuid.UUID | None
    reason: str
    producer: str
    occurred_at: Instant
    detail: str | None = None
    assessment_token: str | None = field(default=None, repr=False)


@dataclass(frozen=True)
class ApplyConfidenceEvidenceResult:
    """What applying evidence did (.NET's ``ApplyConfidenceEvidenceResult``): the update as the transaction stored it,
    the record's revision and status, or why nothing was written."""

    outcome: ConfidenceUpdateOutcome
    event: LifecycleEvent | None
    update: ConfidenceUpdate | None
    revision: int
    status: ExperienceStatus | None
    errors: tuple[StoreValidationError, ...] = ()
    reason: str | None = None
    refusal: IndependenceRefusal | None = None
    recorded_on_event: bool = False
    """Whether an uncounted, applied update was stored on a lifecycle event (host-trusted evidence recorded only)
    rather than as a duplicate with no event (.NET's internal ``RecordedOnEvent``, contract 1.17)."""

    @property
    def counted(self) -> bool:
        """Whether the evidence moved the record's counters: false for a duplicate or evidence recorded only."""
        return self.update.counted if self.update is not None else False


@dataclass(frozen=True)
class ConfidenceAdmissionCounts:
    """Supporting and contradicting evidence counted under one admission."""

    supporting: int
    contradicting: int


@dataclass(frozen=True)
class ConfidenceReport:
    """A record's confidence with some evidence left out (.NET's ``ConfidenceReport``)."""

    experience_id: uuid.UUID
    revision: int
    status: ExperienceStatus
    filter: ConfidenceEvidenceFilter
    reuse_confidence: float
    supporting_validations: int
    contradictions: int
    stored_reuse_confidence: float
    stored_supporting_validations: int
    stored_contradictions: int
    verified: ConfidenceAdmissionCounts
    host_trusted: ConfidenceAdmissionCounts
    unrecorded: ConfidenceAdmissionCounts
    origin: Origin = "HostWritten"
    initial: ConfidenceAdmissionCounts = ConfidenceAdmissionCounts(0, 0)
    host_trusted_recorded_only: ConfidenceAdmissionCounts = ConfidenceAdmissionCounts(0, 0)


@dataclass(frozen=True)
class ConfidenceReadResult:
    """.NET's ``ConfidenceReadResult``: the read's outcome and, when ``Found``, the report."""

    outcome: ExperienceStoreOutcome
    report: ConfidenceReport | None
    errors: tuple[StoreValidationError, ...] = ()


@runtime_checkable
class LifecycleStore(Protocol):
    """The store operations the lifecycle service uses (.NET's ``IExperienceRecordStore`` members ``GetAsync``,
    ``CommitLifecycleEventAsync``, ``GetHistoryAsync`` and ``CheckSupersessionAsync``)."""

    def read_record(
        self, authorization: AuthorizationContext, scope: Scope, experience_id: uuid.UUID
    ) -> RecordReadResult: ...

    def commit_lifecycle_event(
        self, authorization: AuthorizationContext, scope: Scope, event: LifecycleEvent
    ) -> LifecycleCommitResult: ...

    def get_history(self, authorization: AuthorizationContext, query: HistoryQuery) -> HistoryResult: ...

    def check_supersession(
        self,
        authorization: AuthorizationContext,
        scope: Scope,
        experience_id: uuid.UUID,
        replacement_experience_id: uuid.UUID,
    ) -> SupersessionCheckResult: ...


# ---- The store's validation (.NET's ExperienceRecordValidator) ----------------------------------------------------


def _require_no_nul(value: str, path: str, errors: list[StoreValidationError]) -> None:
    if "\0" in value:
        errors.append(StoreValidationError(path, "must not contain the NUL character (U+0000)."))


def _require_not_blank(value: str | None, path: str, errors: list[StoreValidationError]) -> None:
    if is_null_or_whitespace(value):
        errors.append(StoreValidationError(path, _NOT_BLANK))
    else:
        assert value is not None
        _require_no_nul(value, path, errors)


def _require_null_or_not_blank(value: str | None, path: str, errors: list[StoreValidationError]) -> None:
    if value is not None and is_null_or_whitespace(value):
        errors.append(StoreValidationError(path, "must be null or a non-blank value."))
    elif value is not None:
        _require_no_nul(value, path, errors)


def _require_well_formed_utf16(value: str | None, path: str, errors: list[StoreValidationError]) -> None:
    # A Python string holds code points: any surrogate in it is one with no partner, so no UTF-8 form.
    if value is not None and any(0xD800 <= ord(ch) <= 0xDFFF for ch in value):
        errors.append(StoreValidationError(path, "must be well-formed UTF-16: a lone surrogate has no UTF-8 encoding."))


def _refuse_reserved_text(value: str | None, path: str, errors: list[StoreValidationError]) -> None:
    if value is not None and value.startswith(_SEALED_PREFIX):
        errors.append(
            StoreValidationError(path, f"must not begin with '{_SEALED_PREFIX}', which is reserved for sealed values.")
        )


def _require_unit_interval(value: float, path: str, errors: list[StoreValidationError]) -> None:
    if not (value >= 0.0 and value <= 1.0):
        errors.append(StoreValidationError(path, "must be between 0 and 1 inclusive."))


def _require_defined(value: object, defined: Sequence[str], path: str, errors: list[StoreValidationError]) -> None:
    if value not in defined:
        errors.append(StoreValidationError(path, "is not a defined value."))


def _validate_scope(scope: Scope | None, path: str, errors: list[StoreValidationError]) -> None:
    if scope is None:
        errors.append(StoreValidationError(path, _REQUIRED))
        return
    _require_not_blank(scope.tenant_id, f"{path}.TenantId", errors)
    _require_not_blank(scope.application_id, f"{path}.ApplicationId", errors)
    _require_not_blank(scope.project_id, f"{path}.ProjectId", errors)
    _require_null_or_not_blank(scope.team_id, f"{path}.TeamId", errors)
    _require_null_or_not_blank(scope.agent_id, f"{path}.AgentId", errors)
    _require_null_or_not_blank(scope.user_id, f"{path}.UserId", errors)
    for name, value in zip(
        ("TenantId", "ApplicationId", "ProjectId", "TeamId", "AgentId", "UserId"), scope.fields(), strict=True
    ):
        _require_well_formed_utf16(value, f"{path}.{name}", errors)


def _is_empty(value: uuid.UUID | None) -> bool:
    return value is not None and value.int == 0


def validate_get(scope: Scope, experience_id: uuid.UUID) -> tuple[StoreValidationError, ...]:
    """A record read's errors (.NET's ``ValidateGet``)."""
    errors: list[StoreValidationError] = []
    if _is_empty(experience_id):
        errors.append(StoreValidationError("ExperienceId", "must not be an empty GUID."))
    _validate_scope(scope, "Scope", errors)
    return tuple(errors)


def validate_history_query(query: HistoryQuery) -> tuple[StoreValidationError, ...]:
    """A history read's errors (.NET's ``ValidateHistoryQuery``)."""
    errors: list[StoreValidationError] = []
    if _is_empty(query.experience_id):
        errors.append(StoreValidationError("ExperienceId", "must not be an empty GUID."))
    _validate_scope(query.scope, "Scope", errors)
    if not HISTORY_MIN_LIMIT <= query.limit <= HISTORY_MAX_LIMIT:
        errors.append(StoreValidationError("Limit", f"must be between {HISTORY_MIN_LIMIT} and {HISTORY_MAX_LIMIT}."))
    if query.start_after_revision is not None and query.start_after_revision < 0:
        errors.append(StoreValidationError("StartAfterRevision", "must be null or a non-negative revision."))
    return tuple(errors)


def validate_supersession_check(
    scope: Scope, experience_id: uuid.UUID, replacement_experience_id: uuid.UUID
) -> tuple[StoreValidationError, ...]:
    """A supersession check's errors (.NET's ``ValidateSupersessionCheck``)."""
    errors: list[StoreValidationError] = []
    if _is_empty(experience_id):
        errors.append(StoreValidationError("ExperienceId", "must not be an empty GUID."))
    if _is_empty(replacement_experience_id):
        errors.append(StoreValidationError("ReplacementExperienceId", "must not be an empty GUID."))
    _validate_scope(scope, "Scope", errors)
    return tuple(errors)


def validate_lifecycle_event(
    scope: Scope, event: LifecycleEvent, authorization: AuthorizationContext
) -> tuple[StoreValidationError, ...]:
    """A lifecycle commit's errors (.NET's ``ValidateLifecycleEvent``), in .NET's order."""
    errors: list[StoreValidationError] = []
    if event.confidence is not None and is_null_or_whitespace(authorization.principal_id):
        errors.append(
            StoreValidationError(
                "Authorization.PrincipalId",
                "must be non-blank for a confidence update: it is the actor the update is recorded against, and the "
                "reviewer a human submission is counted under.",
            )
        )
    if _is_empty(event.event_id):
        errors.append(StoreValidationError("EventId", "must not be an empty GUID."))
    if _is_empty(event.experience_record_id):
        errors.append(StoreValidationError("ExperienceRecordId", "must not be an empty GUID."))
    _validate_scope(scope, "Scope", errors)
    if event.prior_status is not None:
        _require_defined(event.prior_status, EXPERIENCE_STATUSES, "PriorStatus", errors)
    _require_defined(event.current_status, EXPERIENCE_STATUSES, "CurrentStatus", errors)
    _require_not_blank(event.reason, "Reason", errors)
    _refuse_reserved_text(event.reason, "Reason", errors)
    _require_not_blank(event.producer, "Producer", errors)
    if event.occurred_at.ticks == 0:
        errors.append(StoreValidationError("OccurredAt", "must be set to when the transition occurred."))
    replacement = event.replacement_experience_id
    if replacement is not None:
        if event.confidence is not None:
            errors.append(
                StoreValidationError(
                    "ReplacementExperienceId",
                    "must be null on an event that carries a confidence update; supersession and evidence are "
                    "separate transitions.",
                )
            )
        if event.current_status != "Superseded":
            errors.append(
                StoreValidationError(
                    "ReplacementExperienceId", "must be null unless the event moves the record to Superseded."
                )
            )
        if replacement.int == 0:
            errors.append(StoreValidationError("ReplacementExperienceId", "must not be an empty GUID."))
        if replacement == event.experience_record_id:
            errors.append(
                StoreValidationError(
                    "ReplacementExperienceId", "must name a record other than the one being superseded."
                )
            )
    elif event.current_status == "Superseded":
        errors.append(
            StoreValidationError(
                "ReplacementExperienceId", "is required when the event moves the record to Superseded."
            )
        )
    _validate_confidence_update(event.confidence, errors)
    _validate_recorded_only_evidence(event, errors)
    if event.expected_revision < 0:
        errors.append(StoreValidationError("ExpectedRevision", "must not be negative."))
    elif event.expected_revision >= _MAX_REVISION - 1:
        errors.append(
            StoreValidationError(
                "ExpectedRevision", "must leave room for the next revision, so a later commit stays possible."
            )
        )
    return tuple(errors)


def _double_equals(left: float, right: float) -> bool:
    """.NET's ``double.Equals``: NaN equals NaN."""
    return left == right or (math.isnan(left) and math.isnan(right))


def _validate_recorded_only_evidence(event: LifecycleEvent, errors: list[StoreValidationError]) -> None:
    update = event.confidence
    if update is None or update.counted:
        return
    if update.admission != "HostTrusted":
        errors.append(
            StoreValidationError(
                "Confidence.Admission",
                "an event whose evidence moves no counter is accepted only for HostTrusted evidence recorded without "
                "being counted.",
            )
        )
    if not _double_equals(update.new_reuse_confidence, update.prior_reuse_confidence):
        errors.append(
            StoreValidationError(
                "Confidence.NewReuseConfidence", "must equal the prior score when the evidence moves no counter."
            )
        )
    if event.prior_status is not None and event.prior_status != event.current_status:
        errors.append(
            StoreValidationError("CurrentStatus", "must equal the prior status when the evidence moves no counter.")
        )


def _validate_confidence_update(update: ConfidenceUpdate | None, errors: list[StoreValidationError]) -> None:
    if update is None:
        return
    path = "Confidence"
    _refuse_reserved_text(update.detail, f"{path}.Detail", errors)
    if _is_empty(update.evidence_id):
        errors.append(StoreValidationError(f"{path}.EvidenceId", "must not be an empty GUID."))
    if _is_empty(update.run_id):
        errors.append(StoreValidationError(f"{path}.RunId", "must name the run the reuse was observed in."))
    _require_defined(update.kind, CONFIDENCE_EVIDENCE_KINDS, f"{path}.Kind", errors)
    if update.admission is not None:
        _require_defined(update.admission, CONFIDENCE_EVIDENCE_ADMISSIONS, f"{path}.Admission", errors)
    _require_not_blank(update.rule_version, f"{path}.RuleVersion", errors)
    _require_unit_interval(update.prior_reuse_confidence, f"{path}.PriorReuseConfidence", errors)
    _require_unit_interval(update.new_reuse_confidence, f"{path}.NewReuseConfidence", errors)
    for count, name in (
        (update.prior_supporting_validations, "PriorSupportingValidations"),
        (update.new_supporting_validations, "NewSupportingValidations"),
        (update.prior_contradictions, "PriorContradictions"),
        (update.new_contradictions, "NewContradictions"),
    ):
        if count < 0:
            errors.append(StoreValidationError(f"{path}.{name}", "must not be negative."))
    if (
        update.new_supporting_validations < update.prior_supporting_validations
        or update.new_contradictions < update.prior_contradictions
    ):
        errors.append(
            StoreValidationError(
                f"{path}.NewSupportingValidations", "evidence only ever moves a counter up, never down."
            )
        )
    if update.source not in CONFIDENCE_EVIDENCE_SOURCES:
        errors.append(StoreValidationError(f"{path}.Source", "must be a defined value."))
        return
    if update.source == "Machine":
        if update.verification_round_id is None or update.verification_round_id.int == 0:
            errors.append(
                StoreValidationError(
                    f"{path}.VerificationRoundId",
                    "is required for Machine evidence, which is counted once per run and round.",
                )
            )
        if update.reviewer_identity is not None:
            errors.append(StoreValidationError(f"{path}.ReviewerIdentity", "must be null for Machine evidence."))
        if update.assessment_id is not None:
            errors.append(StoreValidationError(ASSESSMENT_ID_PATH, "must be null for Machine evidence."))
    else:
        if is_null_or_whitespace(update.reviewer_identity):
            errors.append(
                StoreValidationError(
                    f"{path}.ReviewerIdentity",
                    "is required for Human evidence, which is counted once per reviewer and run.",
                )
            )
        if update.verification_round_id is not None:
            errors.append(StoreValidationError(f"{path}.VerificationRoundId", "must be null for Human evidence."))
        if _is_empty(update.assessment_id):
            errors.append(
                StoreValidationError(ASSESSMENT_ID_PATH, "must be null or name an assessment, never an empty GUID.")
            )


def is_same_evidence(submitted: ConfidenceUpdate | None, stored: ConfidenceUpdate | None) -> bool:
    """Whether two confidence payloads are the same piece of evidence: the same evidence ID (.NET's
    ``IsSameEvidence``)."""
    return submitted is not None and stored is not None and submitted.evidence_id == stored.evidence_id


def as_replay_of(submitted: ConfidenceUpdate, stored: ConfidenceUpdate | None) -> ConfidenceUpdate:
    """``submitted`` as an event-ID replay compares it with ``stored`` (.NET's ``AsReplayOf``): the stored admission,
    and for the same evidence the stored counters and score."""
    if stored is None or submitted.evidence_id != stored.evidence_id:
        return replace(submitted, admission=None if stored is None else stored.admission)
    return replace(
        submitted,
        admission=stored.admission,
        prior_reuse_confidence=stored.prior_reuse_confidence,
        new_reuse_confidence=stored.new_reuse_confidence,
        prior_supporting_validations=stored.prior_supporting_validations,
        new_supporting_validations=stored.new_supporting_validations,
        prior_contradictions=stored.prior_contradictions,
        new_contradictions=stored.new_contradictions,
    )


def stored_independence_key(update: ConfidenceUpdate) -> str:
    """The key a store counts an update under, as the PostgreSQL ledger's generated column derives it."""
    if update.source == "Machine":
        round_id = "" if update.verification_round_id is None else str(update.verification_round_id)
        return f"machine:{update.run_id}:{round_id}"
    return f"human:{update.reviewer_identity or ''}:{update.run_id}"


def assessment_spent_error() -> StoreValidationError:
    """The error a store reports for an assessment already spent on the record under another evidence ID."""
    return StoreValidationError(
        ASSESSMENT_ID_PATH, "this assessment has already landed evidence for this record under another evidence ID."
    )


# ---- The service ---------------------------------------------------------------------------------------------------


def is_transition_allowed(prior_status: str, current_status: str) -> bool:
    """
    Whether the lifecycle allows moving a record from ``prior_status`` to ``current_status`` (.NET's
    ``IsTransitionAllowed``): ``Candidate`` to ``Validated`` or ``Quarantined``; ``Validated`` to ``Reinforced``;
    ``Validated`` or ``Reinforced`` to ``Contested``, ``Stale`` or ``Superseded``; anything but ``Revoked`` to
    ``Revoked``. Never a move to the status the record is in, and never an undefined status.
    """
    return (
        prior_status in EXPERIENCE_STATUSES
        and current_status in EXPERIENCE_STATUSES
        and prior_status != current_status
        and (
            (prior_status == "Candidate" and current_status in ("Validated", "Quarantined"))
            or (prior_status == "Validated" and current_status == "Reinforced")
            or (prior_status in ("Validated", "Reinforced") and current_status in ("Contested", "Stale", "Superseded"))
            or (current_status == "Revoked" and prior_status != "Revoked")
        )
    )


def is_eligible(status: str) -> bool:
    """Whether a record in ``status`` may be retrieved, injected or indexed: ``Validated`` or ``Reinforced``."""
    return status in ELIGIBLE_STATUSES


_TRANSITION_OUTCOMES: dict[str, LifecycleTransitionOutcome] = {
    "Committed": "Committed",
    "StaleRevision": "StaleRevision",
    "StatusMismatch": "StatusMismatch",
    "ReplacementNotAllowed": "ReplacementNotAllowed",
    "Conflict": "Conflict",
    "NotFound": "NotFound",
    "Denied": "Denied",
    "Invalid": "Invalid",
    "Deleted": "Deleted",
}

_CONFIDENCE_OUTCOMES: dict[str, ConfidenceUpdateOutcome] = {
    "Committed": "Applied",
    "StaleRevision": "StaleRevision",
    "StatusMismatch": "StatusMismatch",
    "Conflict": "Conflict",
    "NotFound": "NotFound",
    "Denied": "Denied",
    "Invalid": "Invalid",
    "Deleted": "Deleted",
}


def _to_transition_outcome(outcome: str) -> LifecycleTransitionOutcome:
    mapped = _TRANSITION_OUTCOMES.get(outcome)
    if mapped is None:
        raise ExperienceStoreError(
            f"The Experience Record store returned '{outcome}', which is not a lifecycle commit outcome."
        )
    return mapped


def _to_confidence_outcome(outcome: str) -> ConfidenceUpdateOutcome:
    mapped = _CONFIDENCE_OUTCOMES.get(outcome)
    if mapped is None:
        raise ExperienceStoreError(
            f"The Experience Record store returned '{outcome}', which is not a confidence-update outcome."
        )
    return mapped


def _refused(outcome: LifecycleTransitionOutcome, reason: str) -> CommitLifecycleTransitionResult:
    return CommitLifecycleTransitionResult(outcome, None, 0, None, (), reason)


def _replacement_refusal_reason(replacement_status: str | None) -> str:
    if replacement_status is None:
        return "The named replacement does not exist within the record's exact scope."
    if not is_eligible(replacement_status):
        return (
            f"The named replacement is {replacement_status}, so it is not currently eligible for reuse and cannot "
            "replace anything."
        )
    return (
        "The named replacement is already replaced by this record, directly or transitively, so superseding would "
        "close a cycle."
    )


def _validate_replacement_shape(request: CommitLifecycleTransitionRequest) -> CommitLifecycleTransitionResult | None:
    if request.current_status != "Superseded":
        if request.replacement_experience_id is None:
            return None
        return _refused(
            "ReplacementNotAllowed",
            "Only a transition to Superseded names a replacement; this one moves the record to "
            f"{request.current_status}.",
        )
    replacement = request.replacement_experience_id
    if replacement is None or replacement.int == 0:
        return _refused(
            "ReplacementNotAllowed", "A transition to Superseded must name the record that replaces this one."
        )
    if replacement == request.experience_id:
        return _refused("ReplacementNotAllowed", "A record cannot replace itself.")
    return None


def _validate_evidence_shape(
    request: ApplyConfidenceEvidenceRequest, principal_id: str | None
) -> tuple[StoreValidationError, ...]:
    principal_path = "Authorization.PrincipalId"
    errors: list[StoreValidationError] = []
    if _is_empty(request.event_id):
        errors.append(StoreValidationError("EventId", "must not be an empty GUID."))
    if _is_empty(request.experience_id):
        errors.append(StoreValidationError("ExperienceId", "must not be an empty GUID."))
    if _is_empty(request.evidence_id):
        errors.append(StoreValidationError("EvidenceId", "must not be an empty GUID."))
    if _is_empty(request.run_id):
        errors.append(StoreValidationError("RunId", "must name the run the reuse was observed in."))
    if request.kind not in CONFIDENCE_EVIDENCE_KINDS:
        errors.append(StoreValidationError("Kind", "must be a defined evidence kind."))
    if request.source not in CONFIDENCE_EVIDENCE_SOURCES:
        errors.append(StoreValidationError("Source", "must be a defined evidence source."))
    elif request.source == "Machine":
        if request.verification_round_id is None or request.verification_round_id.int == 0:
            errors.append(
                StoreValidationError(
                    "VerificationRoundId",
                    "is required for Machine evidence, which is counted once per run and round.",
                )
            )
        if request.assessment_token is not None:
            errors.append(
                StoreValidationError(
                    "AssessmentToken",
                    "must be null for Machine evidence, which is verified against the round its run was finalized "
                    "with.",
                )
            )
    else:
        if request.verification_round_id is not None:
            errors.append(
                StoreValidationError(
                    "VerificationRoundId",
                    "must be null for Human evidence, which is counted once per reviewer and run.",
                )
            )
        if is_null_or_whitespace(principal_id):
            errors.append(
                StoreValidationError(
                    principal_path,
                    "must be non-blank for Human evidence: it is the reviewer the submission is counted under.",
                )
            )
        else:
            assert principal_id is not None
            if is_whitespace(principal_id[0]) or is_whitespace(principal_id[-1]):
                errors.append(
                    StoreValidationError(
                        principal_path,
                        "must not have leading or trailing whitespace: it would be counted as a second, independent "
                        "reviewer.",
                    )
                )
    if is_null_or_whitespace(request.reason):
        errors.append(StoreValidationError("Reason", "must be a non-blank, auditable reason."))
    if is_null_or_whitespace(request.producer):
        errors.append(StoreValidationError("Producer", "must be a non-blank producer identity."))
    if request.occurred_at.ticks == 0:
        errors.append(StoreValidationError("OccurredAt", "must be set to when the observation was made."))
    return tuple(errors)


def _validate_stored_counters(record: ExperienceRecord) -> tuple[StoreValidationError, ...]:
    errors: list[StoreValidationError] = []
    for count, path in (
        (record.supporting_validations, "SupportingValidations"),
        (record.contradictions, "Contradictions"),
    ):
        if count < 0:
            errors.append(StoreValidationError(path, "the stored record reports a negative evidence counter."))
        elif count == MAX_COUNTER:
            errors.append(
                StoreValidationError(
                    path,
                    "the stored record's evidence counter is already at its maximum, so no further evidence can be "
                    "counted.",
                )
            )
    return tuple(errors)


def _describe(refusal: IndependenceRefusal) -> str:
    descriptions: dict[str, str] = {
        "OwnRun": "The evidence names the record's own source run.",
        "UnknownRun": "The run is not one the library knows in this scope.",
        "UnknownRound": "The verification round is not the one finalization closed for this run.",
        "AssessmentTokenMissing": "Human evidence must present an assessment token.",
        "AssessmentTokenInvalid": (
            "The assessment token is not one the library minted for this scope, run, reviewer and direction."
        ),
        "AssessmentTokenExpired": "The assessment token has expired.",
        "AssessmentTokenNotForRecord": "The assessment token does not cover this record.",
        "AssessmentTokenReplayed": (
            "The assessment token has already landed evidence for this record under another evidence ID."
        ),
        "AssessmentKeyNotConfigured": "No assessment token key is configured to verify human evidence.",
        "NotExposed": "The run was not exposed to this record at or before its current revision.",
        "HostWrittenRun": "The run is known only through a record written without finalization.",
    }
    return descriptions.get(refusal, f"Independence could not be verified ({refusal}).")


def _key_of_stored(update: ConfidenceUpdate) -> str | None:
    """The key a stored update was deduplicated on (.NET's ``StoredIndependenceKey``); ``None`` when it has none."""
    try:
        return independence_key_for(update).value
    except ConfidenceArgumentError:
        if update.source == "Human" and update.reviewer_identity:
            return f"human:{update.reviewer_identity}:{update.run_id}"
        return None


class ExperienceLifecycleService:
    """
    The lifecycle owner (.NET's ``ExperienceLifecycleService``): it decides whether a transition is legal, stamps the
    event that records it, and hands it to the store to append atomically with the record's projection. It runs as
    .NET's does under ``IndependenceVerification.TrustHostSuppliedIdentifiers`` with no signing, indexing or capture
    service (contract 1.16): only the own-run rule is checked and every update is admitted ``HostTrusted``.

    ``confidence_engine`` scores evidence (``None``: the heuristic); its identity is checked here, once, and a malformed
    one raises :class:`~agentexperience.confidence.ConfidenceArgumentError` naming ``confidenceEngine``.
    ``host_trusted_evidence`` defaults to ``RecordedOnly``: until independence is verified (story 27.7), evidence is
    recorded on its event without moving the record's counters, score or status, so unverified evidence cannot steer
    ranking. ``Counted`` is an explicit opt-in, as .NET's ``TrustHostSuppliedIdentifiers`` with its ``Counted`` effect.
    """

    FIRST_EVENT_STATUS: ExperienceStatus = FIRST_EVENT_STATUS

    def __init__(
        self,
        store: LifecycleStore,
        *,
        confidence_engine: ExperienceConfidenceEngine | None = None,
        host_trusted_evidence: HostTrustedEvidenceEffect = "RecordedOnly",
    ) -> None:
        if store is None:
            raise TypeError("store must not be None.")
        if host_trusted_evidence not in HOST_TRUSTED_EVIDENCE_EFFECTS:
            raise ConfidenceArgumentError(
                "ArgumentException",
                "independence",
                "ExperienceIndependenceOptions.HostTrustedEvidence must be a defined effect.",
            )
        self._store = store
        self._confidence = ConfidenceEngineRule.for_engine(confidence_engine)
        self._host_trusted_evidence: HostTrustedEvidenceEffect = host_trusted_evidence

    @property
    def independence_verification(self) -> str:
        """Always ``TrustHostSuppliedIdentifiers``: a contract 1.16 port verifies no independence key."""
        return "TrustHostSuppliedIdentifiers"

    @property
    def host_trusted_evidence(self) -> HostTrustedEvidenceEffect:
        return self._host_trusted_evidence

    @property
    def store(self) -> LifecycleStore:
        """The store this service reads and commits through (contract 1.17's reuse-evidence step reads with it)."""
        return self._store

    @staticmethod
    def is_transition_allowed(prior_status: str, current_status: str) -> bool:
        """See :func:`is_transition_allowed`."""
        return is_transition_allowed(prior_status, current_status)

    @staticmethod
    def is_eligible(status: str) -> bool:
        """See :func:`is_eligible`."""
        return is_eligible(status)

    # ---- commit ----------------------------------------------------------------------------------------------------

    def commit(
        self, authorization: AuthorizationContext, request: CommitLifecycleTransitionRequest
    ) -> CommitLifecycleTransitionResult:
        """
        Validates the transition, stamps its event and commits it through the store (.NET's ``CommitAsync``). A
        refusal Core reaches is ``TransitionNotAllowed`` or ``ReplacementNotAllowed`` with a reason and no event; every
        other outcome is the store's, surfaced unchanged. A store failure raises.
        """
        with observe("lifecycle.commit") as op:
            if op is not None:
                op.set("agentexperience.experience_id", str(request.experience_id))
                op.set("agentexperience.event_id", str(request.event_id))
            result = self._commit(authorization, request)
            if op is not None:
                op.outcome(result.outcome)
        return result

    def _commit(
        self, authorization: AuthorizationContext, request: CommitLifecycleTransitionRequest
    ) -> CommitLifecycleTransitionResult:
        if authorization is None or request is None or request.scope is None:
            raise TypeError("authorization, request and request.scope must not be None.")
        if request.current_status in EXPERIENCE_STATUSES:
            prior = request.prior_status
            if prior is not None:
                if prior in EXPERIENCE_STATUSES and not is_transition_allowed(prior, request.current_status):
                    return _refused(
                        "TransitionNotAllowed",
                        f"A record is already {request.current_status}; an event whose prior and current status are "
                        "the same records no transition."
                        if prior == request.current_status
                        else f"Moving a record from {prior} to {request.current_status} is not an allowed transition.",
                    )
            elif request.current_status != FIRST_EVENT_STATUS:
                return _refused(
                    "TransitionNotAllowed",
                    f"A record's first event has no prior status, so it may only record the record as "
                    f"{FIRST_EVENT_STATUS}; moving it to {request.current_status} needs the status it is moving from.",
                )

        refusal = _validate_replacement_shape(request)
        if refusal is not None:
            return refusal

        event = LifecycleEvent(
            event_id=request.event_id,
            experience_record_id=request.experience_id,
            prior_status=request.prior_status,
            current_status=request.current_status,
            reason=request.reason,
            producer=request.producer,
            occurred_at=request.occurred_at,
            expected_revision=request.expected_revision,
            replacement_experience_id=request.replacement_experience_id,
        )
        result = self._store.commit_lifecycle_event(authorization, request.scope, event)
        outcome = _to_transition_outcome(result.outcome)
        reason = _replacement_refusal_reason(result.current_status) if outcome == "ReplacementNotAllowed" else None
        return CommitLifecycleTransitionResult(
            outcome, event, result.revision, result.current_status, tuple(result.errors), reason
        )

    # ---- apply_evidence --------------------------------------------------------------------------------------------

    def apply_evidence(
        self, authorization: AuthorizationContext, request: ApplyConfidenceEvidenceRequest
    ) -> ApplyConfidenceEvidenceResult:
        """
        Applies one piece of reuse evidence (.NET's ``ApplyEvidenceAsync``): reads the record, computes its new
        counters and score with the engine, and commits the evidence, the counters, the score, any status change and
        the event in one store transaction. A score that is not one raises
        :class:`~agentexperience.confidence.ConfidenceEngineError`; an exception the engine raises propagates. Nothing
        is written either way.
        """
        with observe("confidence.apply") as op:
            if op is not None:
                op.set("agentexperience.experience_id", str(request.experience_id))
                op.set("agentexperience.event_id", str(request.event_id))
            result = self._apply_evidence(authorization, request)
            if op is not None:
                # Only for evidence in the ledger, and only the admission the store reported it stored with.
                admission = result.update.admission if result.outcome == "Applied" and result.update else None
                if admission is not None:
                    op.set("agentexperience.confidence.admission", admission)
                if result.refusal is not None:
                    op.set("agentexperience.independence.refusal", result.refusal)
                op.outcome(result.outcome)
        return result

    def _apply_evidence(
        self, authorization: AuthorizationContext, request: ApplyConfidenceEvidenceRequest
    ) -> ApplyConfidenceEvidenceResult:
        if authorization is None or request is None or request.scope is None:
            raise TypeError("authorization, request and request.scope must not be None.")

        shape_errors = _validate_evidence_shape(request, authorization.principal_id)
        if shape_errors:
            return ApplyConfidenceEvidenceResult("Invalid", None, None, 0, None, shape_errors)

        read = self._store.read_record(authorization, request.scope, request.experience_id)
        if read.outcome != "Found":
            return ApplyConfidenceEvidenceResult(
                _to_confidence_outcome(read.outcome), None, None, 0, None, tuple(read.errors)
            )
        record = read.record
        if record is None:
            return ApplyConfidenceEvidenceResult(
                "NotFound",
                None,
                None,
                0,
                None,
                (),
                "The store reported the record as found but returned nothing to compute against.",
            )

        if not accepts_evidence_in(record.status):
            return ApplyConfidenceEvidenceResult(
                "Ineligible",
                None,
                None,
                record.revision,
                record.status,
                (),
                f"A {record.status} record does not accept confidence evidence; only "
                f"{', '.join(ACCEPTS_EVIDENCE)} do, and no score may change that.",
            )

        # No verification: under RecordedOnly every piece of evidence is admitted HostTrusted and recorded without
        # moving the record, so a saturated counter is no reason to refuse it.
        records_only = self._host_trusted_evidence == "RecordedOnly"
        if not records_only:
            counter_errors = _validate_stored_counters(record)
            if counter_errors:
                return ApplyConfidenceEvidenceResult(
                    "Invalid",
                    None,
                    None,
                    record.revision,
                    record.status,
                    counter_errors,
                    "The stored record's evidence counters cannot have evidence applied to them.",
                )

        # The independence check under the opt-out: the own-run rule, and nothing else (no signer).
        if request.run_id == record.source_run_id:
            return ApplyConfidenceEvidenceResult(
                "Unverified",
                None,
                None,
                record.revision,
                record.status,
                (),
                "The evidence names the record's own source run; a lesson is not reused in the run it came from.",
                "OwnRun",
            )
        admission: ConfidenceEvidenceAdmission = "HostTrusted"
        assessment_id: uuid.UUID | None = None

        recorded_only = records_only and admission == "HostTrusted"
        reviewer = authorization.principal_id if request.source == "Human" else None

        if recorded_only:
            update = ConfidenceUpdate(
                evidence_id=request.evidence_id,
                kind=request.kind,
                source=request.source,
                run_id=request.run_id,
                verification_round_id=request.verification_round_id,
                reviewer_identity=reviewer,
                rule_version=self._confidence.recorded,
                prior_reuse_confidence=record.reuse_confidence,
                new_reuse_confidence=record.reuse_confidence,
                prior_supporting_validations=record.supporting_validations,
                new_supporting_validations=record.supporting_validations,
                prior_contradictions=record.contradictions,
                new_contradictions=record.contradictions,
                detail=request.detail,
            )
        else:
            update = self._confidence.apply(
                record,
                request.evidence_id,
                request.kind,
                request.source,
                request.run_id,
                request.verification_round_id,
                reviewer,
                request.detail,
            )
        update = replace(update, assessment_id=assessment_id, admission=admission)

        current_status = record.status if recorded_only else status_after(record.status, request.kind)
        event = LifecycleEvent(
            event_id=request.event_id,
            experience_record_id=request.experience_id,
            prior_status=record.status,
            current_status=current_status,
            reason=request.reason,
            producer=request.producer,
            occurred_at=request.occurred_at,
            expected_revision=record.revision,
            replacement_experience_id=None,
            confidence=update,
        )
        result = self._store.commit_lifecycle_event(authorization, request.scope, event)
        outcome = _to_confidence_outcome(result.outcome)

        if (
            outcome == "Conflict"
            and update.assessment_id is not None
            and any(error.path == ASSESSMENT_ID_PATH for error in result.errors)
        ):
            return ApplyConfidenceEvidenceResult(
                "Unverified",
                None,
                None,
                record.revision,
                record.status,
                (),
                _describe("AssessmentTokenReplayed"),
                "AssessmentTokenReplayed",
            )

        if outcome != "Applied":
            return ApplyConfidenceEvidenceResult(
                outcome, event, None, result.revision, result.current_status, tuple(result.errors)
            )

        settled_status = result.current_status if result.current_status is not None else current_status
        stored = result.applied_confidence if result.applied_confidence is not None else update.as_recorded_only()
        return ApplyConfidenceEvidenceResult(
            "Applied",
            event,
            stored,
            result.revision,
            settled_status,
            tuple(result.errors),
            None,
            # A store reports no status exactly when an event moved the record (this call's, or the one an event-ID
            # replay reports); an uncounted update on such an event is evidence recorded only, not a duplicate.
            recorded_on_event=not stored.counted and result.current_status is None,
        )

    # ---- read_confidence -------------------------------------------------------------------------------------------

    def read_confidence(
        self,
        authorization: AuthorizationContext,
        scope: Scope,
        experience_id: uuid.UUID,
        filter: ConfidenceEvidenceFilter,
    ) -> ConfidenceReadResult:
        """
        A record's reuse confidence with ``filter``'s evidence left out (.NET's ``ReadConfidenceAsync``): ``All``
        (which adds host-trusted evidence recorded only, once per key no counted update holds),
        ``ExcludeHostTrusted`` or ``VerifiedOnly``. Pages the history up to the revision the record was read at and
        recomputes the score with the engine when anything was left out or added. Writes nothing.
        """
        if authorization is None or scope is None:
            raise TypeError("authorization and scope must not be None.")
        if _is_empty(experience_id) or filter not in CONFIDENCE_EVIDENCE_FILTERS:
            errors: list[StoreValidationError] = []
            if _is_empty(experience_id):
                errors.append(StoreValidationError("experienceId", "must not be an empty GUID."))
            if filter not in CONFIDENCE_EVIDENCE_FILTERS:
                errors.append(StoreValidationError("filter", "must be a defined ConfidenceEvidenceFilter member."))
            return ConfidenceReadResult("Invalid", None, tuple(errors))

        read = self._store.read_record(authorization, scope, experience_id)
        if read.outcome != "Found" or read.record is None:
            return ConfidenceReadResult(
                "NotFound" if read.outcome == "Found" else read.outcome, None, tuple(read.errors)
            )
        record = read.record

        verified_s = verified_c = trusted_s = trusted_c = unrecorded_s = unrecorded_c = 0
        recorded_only_keys: dict[str, str] = {}
        counted_keys: set[str] = set()
        unkeyed_s = unkeyed_c = 0

        after: int | None = None
        while True:
            page = self._store.get_history(authorization, HistoryQuery(scope, experience_id, HISTORY_MAX_LIMIT, after))
            if page.outcome != "Found":
                return ConfidenceReadResult(page.outcome, None, tuple(page.errors))
            for stored in page.events:
                update = stored.event.confidence
                if stored.applied_revision > record.revision or update is None:
                    continue
                if not update.counted:
                    if update.admission == "HostTrusted":
                        recorded_key = _key_of_stored(update)
                        if recorded_key is not None:
                            recorded_only_keys.setdefault(recorded_key, update.kind)
                        elif update.kind == "Supporting":
                            unkeyed_s += 1
                        else:
                            unkeyed_c += 1
                    continue
                counted_key = _key_of_stored(update)
                if counted_key is not None:
                    counted_keys.add(counted_key)
                supporting = update.new_supporting_validations - update.prior_supporting_validations
                contradicting = update.new_contradictions - update.prior_contradictions
                if update.admission == "Verified":
                    verified_s += supporting
                    verified_c += contradicting
                elif update.admission == "HostTrusted":
                    trusted_s += supporting
                    trusted_c += contradicting
                else:
                    unrecorded_s += supporting
                    unrecorded_c += contradicting
            following = page.next_start_after_revision
            if following is None:
                break
            if after is not None and following <= after:
                raise ExperienceStoreError(
                    "The store's history cursor did not move forward, so the history cannot be read to its end."
                )
            after = following

        recorded_s, recorded_c = unkeyed_s, unkeyed_c
        for key, kind in recorded_only_keys.items():
            if key in counted_keys:
                continue
            if kind == "Supporting":
                recorded_s += 1
            else:
                recorded_c += 1

        initial_s = max(0, record.supporting_validations - verified_s - trusted_s - unrecorded_s)
        initial_c = max(0, record.contradictions - verified_c - trusted_c - unrecorded_c)
        # No provenance signing: a record's initial counters are verified exactly when it was finalized.
        if record.origin != "Finalized":
            unverified_initial_s, unverified_initial_c = initial_s, initial_c
        else:
            unverified_initial_s, unverified_initial_c = 0, 0

        if filter == "ExcludeHostTrusted":
            excluded_s, excluded_c = trusted_s, trusted_c
        elif filter == "VerifiedOnly":
            excluded_s = trusted_s + unrecorded_s + unverified_initial_s
            excluded_c = trusted_c + unrecorded_c + unverified_initial_c
        else:
            excluded_s = excluded_c = 0
        added_s = recorded_s if filter == "All" else 0
        added_c = recorded_c if filter == "All" else 0

        supporting_counted = min(MAX_COUNTER, max(0, record.supporting_validations - excluded_s) + added_s)
        contradictions_counted = min(MAX_COUNTER, max(0, record.contradictions - excluded_c) + added_c)
        adjusted = excluded_s != 0 or excluded_c != 0 or added_s != 0 or added_c != 0

        return ConfidenceReadResult(
            "Found",
            ConfidenceReport(
                experience_id=record.experience_id,
                revision=record.revision,
                status=record.status,
                filter=filter,
                reuse_confidence=self._confidence.score(record, supporting_counted, contradictions_counted)
                if adjusted
                else record.reuse_confidence,
                supporting_validations=supporting_counted,
                contradictions=contradictions_counted,
                stored_reuse_confidence=record.reuse_confidence,
                stored_supporting_validations=record.supporting_validations,
                stored_contradictions=record.contradictions,
                verified=ConfidenceAdmissionCounts(verified_s, verified_c),
                host_trusted=ConfidenceAdmissionCounts(trusted_s, trusted_c),
                unrecorded=ConfidenceAdmissionCounts(unrecorded_s, unrecorded_c),
                origin=record.origin,
                initial=ConfidenceAdmissionCounts(initial_s, initial_c),
                host_trusted_recorded_only=ConfidenceAdmissionCounts(recorded_s, recorded_c),
            ),
            (),
        )
