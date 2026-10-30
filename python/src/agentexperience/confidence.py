"""
The confidence engine (contract 1.15): how a record's reuse confidence moves when evidence about reusing it arrives,
exactly as .NET's ``ReuseConfidenceHeuristic``, ``ReuseConfidenceHeuristicEngine``, ``IExperienceConfidenceEngine``,
``ConfidenceEngineRule`` and assessment tokens (``AssessmentTokenIssuer`` and its codec) compute it.

Everything here is a pure function of its arguments: nothing is read from or written to a store. Applying a computed
update to a record (the lifecycle event, the evidence ledger, the revision check) is not part of this module.

- :func:`score` is the default rule, ``(1 + S) / (2 + S + F)``: a heuristic for ranking, not a probability.
- :func:`apply` computes the update one piece of evidence would make; :func:`status_after` the status it moves the
  record to; :func:`independence_key_for` the key the store would count it under.
- :class:`ExperienceConfidenceEngine` is the seam a host replaces the score with; :class:`ConfidenceEngineRule` checks a
  host engine's identity once, refuses a score that is not one, and records ``"{rule_id}/{rule_version}"``.
- :class:`AssessmentTokenIssuer` mints the token a human assessment carries; the codec that checks one is internal, as
  .NET's is.

A refusal .NET reports as an ``ArgumentException`` raises :class:`ConfidenceArgumentError` with .NET's exception type
and parameter name; a score .NET refuses with ``InvalidOperationException`` raises :class:`ConfidenceEngineError` with
.NET's message.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import math
import struct
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field, replace
from datetime import timedelta
from decimal import Decimal
from typing import ClassVar, Literal, Protocol, runtime_checkable

from ._text import is_null_or_whitespace, is_whitespace, utf8_bytes, utf16_len
from .encoding import Instant
from .identity import ExperienceIdentity
from .records import ExperienceRecord, ExperienceStatus, Scope

ConfidenceEvidenceKind = Literal["Supporting", "Contradicting"]
"""Whether evidence supports reuse or contradicts it: .NET's ``ConfidenceEvidenceKind`` member names."""
CONFIDENCE_EVIDENCE_KINDS: tuple[ConfidenceEvidenceKind, ...] = ("Supporting", "Contradicting")

ConfidenceEvidenceSource = Literal["Machine", "Human"]
"""Whether a machine evaluator or a human reviewer observed the evidence: .NET's ``ConfidenceEvidenceSource``."""
CONFIDENCE_EVIDENCE_SOURCES: tuple[ConfidenceEvidenceSource, ...] = ("Machine", "Human")

IndependenceRefusal = Literal[
    "OwnRun",
    "UnknownRun",
    "UnknownRound",
    "AssessmentTokenMissing",
    "AssessmentTokenInvalid",
    "AssessmentTokenExpired",
    "AssessmentTokenNotForRecord",
    "AssessmentTokenReplayed",
    "AssessmentKeyNotConfigured",
    "NotExposed",
    "HostWrittenRun",
]
"""Why the confidence path refused a submission's identifiers: .NET's ``IndependenceRefusal`` member names. The token
codec reports ``AssessmentTokenMissing``, ``AssessmentTokenInvalid`` and ``AssessmentTokenExpired``; the rest are the
verification path's."""

IndependenceVerification = Literal["Verified", "TrustHostSuppliedIdentifiers"]
""".NET's ``IndependenceVerification``."""
INDEPENDENCE_VERIFICATIONS: tuple[IndependenceVerification, ...] = ("Verified", "TrustHostSuppliedIdentifiers")

HostTrustedEvidenceEffect = Literal["Counted", "RecordedOnly"]
""".NET's ``HostTrustedEvidenceEffect``."""
HOST_TRUSTED_EVIDENCE_EFFECTS: tuple[HostTrustedEvidenceEffect, ...] = ("Counted", "RecordedOnly")

RULE_VERSION = "1.0.0"
"""The heuristic's rule version (.NET's ``ReuseConfidenceHeuristic.RuleVersion``): what the default engine records."""

HEURISTIC_RULE_ID = "reuse-heuristic"
"""The default engine's rule ID (.NET's ``ReuseConfidenceHeuristicEngine.HeuristicRuleId``). A host engine may not
use it."""

ACCEPTS_EVIDENCE: tuple[ExperienceStatus, ...] = ("Validated", "Reinforced", "Contested")
"""The statuses in which a record may receive confidence evidence (.NET's ``ReuseConfidenceHeuristic.AcceptsEvidence``):
not the eligible statuses, because a ``Contested`` record keeps accruing evidence."""

MAX_COUNTER = 2**31 - 1
"""The largest evidence counter: .NET's counters are 32-bit, and one at the largest value cannot take one more."""

ENGINE_PARAMETER = "confidenceEngine"
"""The parameter .NET's lifecycle service names when it refuses a host engine's identity."""

_MAX_IDENTITY_LENGTH = 64
_IDENTITY_CHARACTERS = frozenset("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-")


class ConfidenceArgumentError(ValueError):
    """
    An argument .NET refuses with an ``ArgumentException``: ``exception`` is the .NET type's name
    (``ArgumentException`` or ``ArgumentOutOfRangeException``), ``parameter`` the parameter it names.
    """

    def __init__(self, exception: str, parameter: str, message: str) -> None:
        super().__init__(message)
        self.exception = exception
        self.parameter = parameter
        self.message = message


class ConfidenceEngineError(RuntimeError):
    """
    A score an engine returned that is not a score in [0, 1] (.NET's ``InvalidOperationException``). ``rule`` is the
    rule string, ``score`` the value; the message is .NET's, naming both and nothing from the record.
    """

    def __init__(self, rule: str, score: float) -> None:
        self.rule = rule
        self.score = score
        self.message = (
            f"The confidence engine '{rule}' returned {format_score(score)}, which is not a score in [0, 1]. "
            "Nothing was written."
        )
        super().__init__(self.message)


def _out_of_range(parameter: str, message: str) -> ConfidenceArgumentError:
    return ConfidenceArgumentError("ArgumentOutOfRangeException", parameter, message)


def _argument(parameter: str, message: str) -> ConfidenceArgumentError:
    return ConfidenceArgumentError("ArgumentException", parameter, message)


def format_score(value: float) -> str:
    """
    .NET's ``double.ToString("R", CultureInfo.InvariantCulture)`` as ``ToString`` writes it (not as JSON does): the
    shortest text that round-trips, ``NaN``, ``Infinity`` and ``-Infinity``, and scientific notation (``1E-05``,
    ``1E+17``) below 1e-4 and from 1e17.
    """
    if math.isnan(value):
        return "NaN"
    if math.isinf(value):
        return "Infinity" if value > 0 else "-Infinity"
    if value == 0:
        return "-0" if math.copysign(1.0, value) < 0 else "0"
    sign, digits, exponent = Decimal(repr(value)).as_tuple()
    assert isinstance(exponent, int)
    text = "".join(str(d) for d in digits).rstrip("0") or "0"
    point = len(digits) + exponent
    scientific = point - 1
    prefix = "-" if sign else ""
    if scientific < -4 or scientific >= 17:
        mantissa = text[0] + ("." + text[1:] if len(text) > 1 else "")
        return f"{prefix}{mantissa}E{'-' if scientific < 0 else '+'}{abs(scientific):02d}"
    if point <= 0:
        return f"{prefix}0.{'0' * -point}{text}"
    if point >= len(text):
        return f"{prefix}{text}{'0' * (point - len(text))}"
    return f"{prefix}{text[:point]}.{text[point:]}"


# ---- The heuristic -------------------------------------------------------------------------------------------------


def accepts_evidence_in(status: ExperienceStatus) -> bool:
    """Whether a record in ``status`` may receive confidence evidence: one of :data:`ACCEPTS_EVIDENCE`."""
    return status in ACCEPTS_EVIDENCE


def score(supporting_validations: int, contradictions: int) -> float:
    """
    The heuristic's reuse confidence, ``(1 + S) / (2 + S + F)``, in double precision: strictly inside (0, 1). A
    negative count raises :class:`ConfidenceArgumentError` (``ArgumentOutOfRangeException``) naming
    ``supportingValidations`` or ``contradictions``, the supporting count first.
    """
    if supporting_validations < 0:
        raise _out_of_range("supportingValidations", "The supporting validations must not be negative.")
    if contradictions < 0:
        raise _out_of_range("contradictions", "The contradictions must not be negative.")
    return (1.0 + supporting_validations) / (2.0 + supporting_validations + contradictions)


def status_after(status: ExperienceStatus, kind: ConfidenceEvidenceKind) -> ExperienceStatus:
    """The status evidence of ``kind`` moves a record in ``status`` to: a contradiction contests a ``Validated`` or
    ``Reinforced`` record; nothing else moves a status."""
    if kind == "Contradicting" and status in ("Validated", "Reinforced"):
        return "Contested"
    return status


@dataclass(frozen=True)
class ConfidenceUpdate:
    """One accepted piece of evidence and what it did to a record's counters and score: .NET's ``ConfidenceUpdate``."""

    evidence_id: uuid.UUID
    kind: ConfidenceEvidenceKind
    source: ConfidenceEvidenceSource
    run_id: uuid.UUID
    verification_round_id: uuid.UUID | None
    reviewer_identity: str | None
    rule_version: str
    prior_reuse_confidence: float
    new_reuse_confidence: float
    prior_supporting_validations: int
    new_supporting_validations: int
    prior_contradictions: int
    new_contradictions: int
    detail: str | None = None

    @property
    def counted(self) -> bool:
        """Whether the update moved a counter: false for a submission recorded only."""
        return (
            self.new_supporting_validations != self.prior_supporting_validations
            or self.new_contradictions != self.prior_contradictions
        )

    def as_recorded_only(self) -> ConfidenceUpdate:
        """The same submission counted zero times: the new counters and score are the prior ones."""
        return replace(
            self,
            new_reuse_confidence=self.prior_reuse_confidence,
            new_supporting_validations=self.prior_supporting_validations,
            new_contradictions=self.prior_contradictions,
        )


def apply(
    record: ExperienceRecord,
    evidence_id: uuid.UUID,
    kind: ConfidenceEvidenceKind,
    source: ConfidenceEvidenceSource,
    run_id: uuid.UUID,
    verification_round_id: uuid.UUID | None,
    reviewer_identity: str | None,
    detail: str | None = None,
) -> ConfidenceUpdate:
    """
    The update one piece of evidence would make to ``record`` under the heuristic (.NET's
    ``ReuseConfidenceHeuristic.Apply``), assuming its independence key is free. A negative stored counter raises
    (``ArgumentOutOfRangeException``, naming ``record.SupportingValidations`` or ``record.Contradictions``), and so
    does a counter at :data:`MAX_COUNTER` that this evidence would increment (naming ``record``). The record's
    status is not checked here: see :func:`accepts_evidence_in`.
    """
    if record.supporting_validations < 0:
        raise _out_of_range("record.SupportingValidations", "The record's supporting validations are negative.")
    if record.contradictions < 0:
        raise _out_of_range("record.Contradictions", "The record's contradictions are negative.")
    supporting = record.supporting_validations
    contradictions = record.contradictions
    if kind == "Supporting":
        if supporting >= MAX_COUNTER:
            raise _out_of_range("record", "The record's supporting validations cannot take one more.")
        supporting += 1
    else:
        if contradictions >= MAX_COUNTER:
            raise _out_of_range("record", "The record's contradictions cannot take one more.")
        contradictions += 1
    return ConfidenceUpdate(
        evidence_id=evidence_id,
        kind=kind,
        source=source,
        run_id=run_id,
        verification_round_id=verification_round_id,
        reviewer_identity=reviewer_identity,
        rule_version=RULE_VERSION,
        prior_reuse_confidence=record.reuse_confidence,
        new_reuse_confidence=score(supporting, contradictions),
        prior_supporting_validations=record.supporting_validations,
        new_supporting_validations=supporting,
        prior_contradictions=record.contradictions,
        new_contradictions=contradictions,
        detail=detail,
    )


@dataclass(frozen=True)
class ConfidenceIndependenceKey:
    """
    The key an accepted submission is counted under within one record (.NET's ``ConfidenceIndependenceKey``): the first
    submission for a key moves the counters, every later one is recorded and counted zero times.
    """

    source: ConfidenceEvidenceSource
    value: str

    @staticmethod
    def for_machine(run_id: uuid.UUID, verification_round_id: uuid.UUID) -> ConfidenceIndependenceKey:
        """One verification round of one run counts once: ``machine:{run}:{round}``."""
        return ConfidenceIndependenceKey("Machine", f"machine:{run_id}:{verification_round_id}")

    @staticmethod
    def for_human(reviewer_identity: str, run_id: uuid.UUID) -> ConfidenceIndependenceKey:
        """
        One reviewer's opinion about one run counts once: ``human:{reviewer}:{run}``, the reviewer verbatim. A blank
        identity, or one with leading or trailing whitespace, raises (``ArgumentException``, ``reviewerIdentity``).
        """
        if is_null_or_whitespace(reviewer_identity):
            raise _argument(
                "reviewerIdentity", "The value cannot be an empty string or composed entirely of whitespace."
            )
        if is_whitespace(reviewer_identity[0]) or is_whitespace(reviewer_identity[-1]):
            raise _argument(
                "reviewerIdentity",
                "A reviewer identity may not have leading or trailing whitespace: it would key as a second, "
                "independent reviewer.",
            )
        return ConfidenceIndependenceKey("Human", f"human:{reviewer_identity}:{run_id}")


def independence_key_for(update: ConfidenceUpdate) -> ConfidenceIndependenceKey:
    """
    The key ``update`` is deduplicated on (.NET's ``ReuseConfidenceHeuristic.IndependenceKeyFor``): machine evidence on
    its run and round, human evidence on its reviewer and run. Machine evidence without a round, or human evidence
    without a (non-blank) reviewer, raises (``ArgumentException``, ``update``).
    """
    if update.source == "Machine" and update.verification_round_id is not None:
        return ConfidenceIndependenceKey.for_machine(update.run_id, update.verification_round_id)
    if update.source == "Human" and not is_null_or_whitespace(update.reviewer_identity):
        assert update.reviewer_identity is not None
        return ConfidenceIndependenceKey.for_human(update.reviewer_identity, update.run_id)
    raise _argument(
        "update",
        "Machine evidence needs a verification round and human evidence needs a reviewer identity; without one there "
        "is no key to count it independently under.",
    )


# ---- The engine seam -----------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class ExperienceConfidenceInput:
    """
    What an engine scores (.NET's ``ExperienceConfidenceInput``): the record as it was read, and the counters to score.
    When evidence is applied the counters are the ones after it, while the record still carries the prior ones. Treat
    the record as data: it may carry content a host engine must not log.
    """

    record: ExperienceRecord
    supporting_validations: int
    contradictions: int


@runtime_checkable
class ExperienceConfidenceEngine(Protocol):
    """
    Turns a record's evidence counters into its reuse confidence (.NET's ``IExperienceConfidenceEngine``). ``rule_id``
    and ``rule_version`` are each 1 to 64 characters from ``[A-Za-z0-9._-]``; ``score`` returns a value in [0, 1]. It
    must be deterministic and thread-safe, and must not block: it owns only the score. Which statuses accept evidence,
    the status move, the increments and the independence keys stay the library's.
    """

    @property
    def rule_id(self) -> str: ...

    @property
    def rule_version(self) -> str: ...

    def score(self, input: ExperienceConfidenceInput) -> float: ...


class ReuseConfidenceHeuristicEngine:
    """The default engine (.NET's ``ReuseConfidenceHeuristicEngine``): :func:`score` under :data:`RULE_VERSION`. Use
    :data:`ReuseConfidenceHeuristicEngine.INSTANCE`; a host engine can wrap it and delegate to it."""

    INSTANCE: ClassVar[ReuseConfidenceHeuristicEngine]

    @property
    def rule_id(self) -> str:
        return HEURISTIC_RULE_ID

    @property
    def rule_version(self) -> str:
        return RULE_VERSION

    def score(self, input: ExperienceConfidenceInput) -> float:
        return score(input.supporting_validations, input.contradictions)


ReuseConfidenceHeuristicEngine.INSTANCE = ReuseConfidenceHeuristicEngine()


def _well_formed(value: object) -> bool:
    return (
        isinstance(value, str)
        and 0 < len(value) <= _MAX_IDENTITY_LENGTH
        and all(character in _IDENTITY_CHARACTERS for character in value)
    )


class ConfidenceEngineRule:
    """
    An engine with its identity checked, as .NET's lifecycle service checks it once when it is built
    (``ConfidenceEngineRule``). :attr:`recorded` is what every update records: ``"1.0.0"`` for the default engine,
    ``"{rule_id}/{rule_version}"`` for a host engine. :meth:`score` refuses a value that is not a score;
    :meth:`apply` composes the heuristic's counters with the engine's score as the lifecycle service does.
    """

    DEFAULT: ClassVar[ConfidenceEngineRule]

    def __init__(self, engine: ExperienceConfidenceEngine, recorded: str) -> None:
        self._engine = engine
        self.recorded = recorded

    @property
    def engine(self) -> ExperienceConfidenceEngine:
        return self._engine

    @staticmethod
    def for_engine(
        engine: ExperienceConfidenceEngine | None, parameter_name: str = ENGINE_PARAMETER
    ) -> ConfidenceEngineRule:
        """
        The rule for ``engine`` (``None`` or the heuristic: :attr:`DEFAULT`). A malformed ``rule_id`` or
        ``rule_version`` (the ID first), or a host engine claiming :data:`HEURISTIC_RULE_ID`, raises
        :class:`ConfidenceArgumentError` (``ArgumentException``) naming ``parameter_name``.
        """
        if engine is None or isinstance(engine, ReuseConfidenceHeuristicEngine):
            return ConfidenceEngineRule.DEFAULT
        rule_id = engine.rule_id
        rule_version = engine.rule_version
        if not _well_formed(rule_id):
            raise _argument(
                parameter_name,
                f"The confidence engine's RuleId must be 1 to {_MAX_IDENTITY_LENGTH} characters from [A-Za-z0-9._-].",
            )
        if not _well_formed(rule_version):
            raise _argument(
                parameter_name,
                f"The confidence engine's RuleVersion must be 1 to {_MAX_IDENTITY_LENGTH} characters from "
                "[A-Za-z0-9._-].",
            )
        if rule_id == HEURISTIC_RULE_ID:
            raise _argument(
                parameter_name,
                f"A host confidence engine may not use the default engine's RuleId '{HEURISTIC_RULE_ID}': its scores "
                "would be recorded as if the heuristic had produced them.",
            )
        return ConfidenceEngineRule(engine, f"{rule_id}/{rule_version}")

    def score(self, record: ExperienceRecord, supporting_validations: int, contradictions: int) -> float:
        """
        The engine's score for ``record`` with these counters. An exception the engine raises propagates unchanged; a
        value that is NaN, infinite or outside [0, 1] raises :class:`ConfidenceEngineError`, never clamped.
        """
        value = self._engine.score(ExperienceConfidenceInput(record, supporting_validations, contradictions))
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise TypeError("A confidence engine's score must be a float.")
        value = float(value)
        if not math.isfinite(value) or value < 0.0 or value > 1.0:
            raise ConfidenceEngineError(self.recorded, value)
        return value

    def apply(
        self,
        record: ExperienceRecord,
        evidence_id: uuid.UUID,
        kind: ConfidenceEvidenceKind,
        source: ConfidenceEvidenceSource,
        run_id: uuid.UUID,
        verification_round_id: uuid.UUID | None,
        reviewer_identity: str | None,
        detail: str | None = None,
    ) -> ConfidenceUpdate:
        """
        The update one piece of evidence makes, as .NET's lifecycle service computes it: :func:`apply` settles the
        counters (and refuses an overflow before the engine is asked anything), then this rule's engine scores the
        record as read with the new counters, and the update records :attr:`recorded`.
        """
        update = apply(record, evidence_id, kind, source, run_id, verification_round_id, reviewer_identity, detail)
        return replace(
            update,
            new_reuse_confidence=self.score(record, update.new_supporting_validations, update.new_contradictions),
            rule_version=self.recorded,
        )


ConfidenceEngineRule.DEFAULT = ConfidenceEngineRule(ReuseConfidenceHeuristicEngine.INSTANCE, RULE_VERSION)


# ---- Assessment tokens ---------------------------------------------------------------------------------------------

MINIMUM_ASSESSMENT_TOKEN_KEY_BYTES = 32
DEFAULT_ASSESSMENT_TOKEN_LIFETIME = timedelta(days=1)
MAXIMUM_ASSESSMENT_TOKEN_LIFETIME = timedelta(days=30)
MAX_ASSESSED_RECORDS = 64
"""The most records one assessment covers (.NET's ``ExperienceReuseFeedback.MaxExposedRecords``)."""
ASSESSMENT_TOKEN_PREFIX = "aexat1."
_MAX_TOKEN_LENGTH = 4096
_ALLOWED_CLOCK_SKEW_MS = 5 * 60 * 1000
_VERSION = 1
_HEADER_LENGTH = 1 + 16 + 8 + 8 + 1 + 1
_ISSUED_AT_OFFSET = 17
_EXPIRES_AT_OFFSET = 25
_KIND_OFFSET = 33
_COUNT_OFFSET = 34
_ID_LENGTH = 16
_MAC_LENGTH = 32
_DOMAIN_LABEL = b"AgentExperience.AssessmentToken/v1\0"
_BASE64URL = frozenset("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")
_EMPTY = uuid.UUID(int=0)
_UNIX_EPOCH_MS = 62_135_596_800_000
"""The Unix epoch in milliseconds since 0001-01-01."""
_TICKS_PER_MILLISECOND = 10_000


def _unix_ms(instant: Instant) -> int:
    return instant.ticks // _TICKS_PER_MILLISECOND - _UNIX_EPOCH_MS


def _instant_of_unix_ms(milliseconds: int) -> Instant:
    return Instant((milliseconds + _UNIX_EPOCH_MS) * _TICKS_PER_MILLISECOND)


def _whole_ms(duration: timedelta) -> int:
    # .NET's (long)TimeSpan.TotalMilliseconds: truncated toward zero.
    microseconds = duration // timedelta(microseconds=1)
    return int(microseconds / 1000) if microseconds < 0 else microseconds // 1000


@dataclass(frozen=True)
class ExperienceIndependenceOptions:
    """
    .NET's ``ExperienceIndependenceOptions``, as far as assessment tokens use them: the verification mode, what
    host-trusted evidence does, the assessment token key (at least 32 bytes from the host's secret store; ``None``:
    human evidence cannot be verified), the token lifetime (a day by default, at most 30 days) and the clock.
    """

    verification: IndependenceVerification = "Verified"
    host_trusted_evidence: HostTrustedEvidenceEffect = "Counted"
    assessment_token_key: bytes | None = field(default=None, repr=False)
    assessment_token_lifetime: timedelta = DEFAULT_ASSESSMENT_TOKEN_LIFETIME
    clock: Callable[[], Instant] = field(default=Instant.now, compare=False)


@dataclass(frozen=True)
class IssuedAssessmentToken:
    """
    One assessment token, as :meth:`AssessmentTokenIssuer.issue` minted it (.NET's ``IssuedAssessmentToken``):
    ``token`` is a bearer credential for one review, never printed by ``repr``.
    """

    assessment_id: uuid.UUID
    token: str = field(repr=False)
    issued_at: Instant
    expires_at: Instant


class AssessmentTokenIssuer:
    """
    Mints assessment tokens (.NET's ``AssessmentTokenIssuer``): the only way a human assessment can move a confidence
    score under verification. Call it from the host's own review flow, where a person's decision is recorded, never
    from code an agent drives. A token is an HMAC-SHA256, under the options' key, over the assessment's ID, issue time,
    expiry, direction and records, and the scope, run and reviewer it is bound to.
    """

    def __init__(self, options: ExperienceIndependenceOptions) -> None:
        codec = _AssessmentTokenCodec.create(options)
        if codec is None:
            raise _argument(
                "options", "An assessment token issuer needs ExperienceIndependenceOptions.AssessmentTokenKey."
            )
        self._codec = codec

    def issue(
        self,
        reviewer: ExperienceIdentity,
        scope: Scope,
        run_id: uuid.UUID,
        kind: ConfidenceEvidenceKind,
        experience_ids: Sequence[uuid.UUID],
    ) -> IssuedAssessmentToken:
        """
        A token for one human assessment of one run: ``reviewer``'s principal is the reviewer the evidence is counted
        under, and its authorization must permit ``scope``. Refusals, in .NET's order, raise
        :class:`ConfidenceArgumentError` naming ``reviewer``, ``scope``, ``runId``, ``kind`` or ``experienceIds``.
        """
        return self._codec.encode(uuid.uuid4(), *_check_issue(reviewer, scope, run_id, kind, experience_ids))


def _check_issue(
    reviewer: ExperienceIdentity,
    scope: Scope,
    run_id: uuid.UUID,
    kind: ConfidenceEvidenceKind,
    experience_ids: Sequence[uuid.UUID],
) -> tuple[Scope, uuid.UUID, str, ConfidenceEvidenceKind, tuple[uuid.UUID, ...]]:
    principal = reviewer.principal
    if is_null_or_whitespace(principal) or is_whitespace(principal[0]) or is_whitespace(principal[-1]):
        raise _argument(
            "reviewer",
            "The reviewer's PrincipalId must be non-blank and carry no leading or trailing whitespace: it is the "
            "reviewer the evidence is counted under.",
        )
    bounds = reviewer.authorization
    if is_null_or_whitespace(bounds.tenant_id) or not bounds.permits(scope):
        raise _argument("scope", "The scope lies outside the reviewer's authorization.")
    if run_id == _EMPTY:
        raise _argument("runId", "The run must not be an empty GUID.")
    if kind not in CONFIDENCE_EVIDENCE_KINDS:
        raise _argument("kind", "The evidence kind must be defined.")
    ids = tuple(experience_ids)
    if len(ids) == 0 or len(ids) > MAX_ASSESSED_RECORDS:
        raise _argument("experienceIds", f"An assessment covers 1 to {MAX_ASSESSED_RECORDS} records.")
    if any(identifier == _EMPTY for identifier in ids) or len(set(ids)) != len(ids):
        raise _argument("experienceIds", "The records must be distinct and non-empty.")
    return scope, run_id, principal, kind, ids


@dataclass(frozen=True)
class TokenVerification:
    """What the codec found: ``refusal`` ``None`` for a genuine token, with its assessment ID and covered records."""

    refusal: IndependenceRefusal | None
    assessment_id: uuid.UUID
    covered: tuple[uuid.UUID, ...]


_INVALID = TokenVerification("AssessmentTokenInvalid", _EMPTY, ())


class _AssessmentTokenCodec:
    """
    The token format, and the one place a token is signed or checked (.NET's internal ``AssessmentTokenCodec``):
    ``aexat1.`` and the unpadded base64url of a version byte (1), the assessment ID, the issue time and expiry (Unix
    milliseconds, big-endian), the kind, the record count, each record ID, and the 32-byte HMAC-SHA256 over a domain
    label, those bytes, the six scope fields (each a presence byte, then a 4-byte length and its UTF-8), the run and the
    reviewer (a 4-byte length and its UTF-8).
    """

    def __init__(self, key: bytes, lifetime: timedelta, clock: Callable[[], Instant]) -> None:
        self._key = bytes(key)
        self._lifetime = lifetime
        self._clock = clock

    @staticmethod
    def create(options: ExperienceIndependenceOptions) -> _AssessmentTokenCodec | None:
        """Validates the options, in .NET's order: a codec over a copy of the key, or ``None`` with no key."""
        if options.verification not in INDEPENDENCE_VERIFICATIONS:
            raise _argument("options", "ExperienceIndependenceOptions.Verification must be a defined mode.")
        lifetime = options.assessment_token_lifetime
        if lifetime <= timedelta(0) or lifetime > MAXIMUM_ASSESSMENT_TOKEN_LIFETIME:
            raise _argument(
                "options",
                "ExperienceIndependenceOptions.AssessmentTokenLifetime must be strictly positive and at most "
                "30.00:00:00.",
            )
        if options.clock is None:
            raise _argument("options", "ExperienceIndependenceOptions.TimeProvider must be set.")
        key = options.assessment_token_key
        if key is None:
            return None
        if len(key) < MINIMUM_ASSESSMENT_TOKEN_KEY_BYTES:
            raise _argument(
                "options",
                "ExperienceIndependenceOptions.AssessmentTokenKey must be at least "
                f"{MINIMUM_ASSESSMENT_TOKEN_KEY_BYTES} bytes.",
            )
        return _AssessmentTokenCodec(key, lifetime, options.clock)

    def encode(
        self,
        assessment_id: uuid.UUID,
        scope: Scope,
        run_id: uuid.UUID,
        reviewer: str,
        kind: ConfidenceEvidenceKind,
        experience_ids: Sequence[uuid.UUID],
    ) -> IssuedAssessmentToken:
        issued_at = _unix_ms(self._clock())
        expires_at = issued_at + _whole_ms(self._lifetime)
        body = bytearray()
        body.append(_VERSION)
        body += assessment_id.bytes
        body += struct.pack(">qq", issued_at, expires_at)
        body.append(CONFIDENCE_EVIDENCE_KINDS.index(kind))
        body.append(len(experience_ids) & 0xFF)
        for identifier in experience_ids:
            body += identifier.bytes
        body += self._mac(bytes(body), scope, run_id, reviewer)
        return IssuedAssessmentToken(
            assessment_id,
            ASSESSMENT_TOKEN_PREFIX + base64.urlsafe_b64encode(bytes(body)).rstrip(b"=").decode("ascii"),
            _instant_of_unix_ms(issued_at),
            _instant_of_unix_ms(expires_at),
        )

    def verify(
        self, token: str | None, scope: Scope, run_id: uuid.UUID, reviewer: str, kind: ConfidenceEvidenceKind
    ) -> TokenVerification:
        """
        Checks a token against the binding a submission names. The signature is checked, in constant time, before
        anything the token claims is read.
        """
        if not token:
            return TokenVerification("AssessmentTokenMissing", _EMPTY, ())
        if utf16_len(token) > _MAX_TOKEN_LENGTH or not token.startswith(ASSESSMENT_TOKEN_PREFIX):
            return _INVALID
        encoded = token[len(ASSESSMENT_TOKEN_PREFIX) :]
        if any(character not in _BASE64URL for character in encoded) or len(encoded) % 4 == 1:
            return _INVALID
        try:
            data = base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4))
        except (binascii.Error, ValueError):
            return _INVALID
        if len(data) < _HEADER_LENGTH + _ID_LENGTH + _MAC_LENGTH or data[0] != _VERSION:
            return _INVALID
        count = data[_COUNT_OFFSET]
        if count == 0 or count > MAX_ASSESSED_RECORDS or len(data) != _HEADER_LENGTH + count * _ID_LENGTH + _MAC_LENGTH:
            return _INVALID
        # A non-canonical encoding of the same bytes is not a different token.
        if base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii") != encoded:
            return _INVALID
        signed = len(data) - _MAC_LENGTH
        if not hmac.compare_digest(self._mac(data[:signed], scope, run_id, reviewer), data[signed:]):
            return _INVALID
        if kind not in CONFIDENCE_EVIDENCE_KINDS or data[_KIND_OFFSET] != CONFIDENCE_EVIDENCE_KINDS.index(kind):
            return _INVALID
        issued_at, expires_at = struct.unpack_from(">qq", data, _ISSUED_AT_OFFSET)
        now = _unix_ms(self._clock())
        if (
            issued_at < 0
            or expires_at <= issued_at
            or expires_at - issued_at > _whole_ms(MAXIMUM_ASSESSMENT_TOKEN_LIFETIME)
            or issued_at > now + _ALLOWED_CLOCK_SKEW_MS
        ):
            return _INVALID
        if now >= expires_at:
            return TokenVerification("AssessmentTokenExpired", _EMPTY, ())
        covered = tuple(
            uuid.UUID(bytes=data[_HEADER_LENGTH + index * _ID_LENGTH : _HEADER_LENGTH + (index + 1) * _ID_LENGTH])
            for index in range(count)
        )
        return TokenVerification(None, uuid.UUID(bytes=data[1 : 1 + _ID_LENGTH]), covered)

    def _mac(self, signed: bytes, scope: Scope, run_id: uuid.UUID, reviewer: str) -> bytes:
        message = bytearray(_DOMAIN_LABEL)
        message += signed
        for value in scope.fields():
            if value is None:
                message.append(0)
            else:
                message.append(1)
                message += _length_prefixed(value)
        message += run_id.bytes
        message += _length_prefixed(reviewer)
        return hmac.new(self._key, bytes(message), hashlib.sha256).digest()


def _length_prefixed(value: str) -> bytes:
    encoded = utf8_bytes(value)
    return struct.pack(">i", len(encoded)) + encoded
