"""
Contract 1.15: every ``confidence-engine`` fixture through this port's confidence engine, and the rules the fixtures do
not state. Each case's ``input.json`` names an operation (``score``, ``rule``, ``apply``, ``options``, ``issue`` or
``verify``); ``expected.json`` is what .NET's classes give for it.
"""

from __future__ import annotations

import math
import uuid
from datetime import timedelta
from typing import Any, cast

import pytest
from _contract import FIXTURES, cases, read_json

from agentexperience import (
    ACCEPTS_EVIDENCE,
    AssessmentTokenIssuer,
    AuthorizationBounds,
    ConfidenceArgumentError,
    ConfidenceEngineError,
    ConfidenceEngineRule,
    ConfidenceIndependenceKey,
    ExperienceConfidenceEngine,
    ExperienceConfidenceInput,
    ExperienceIndependenceOptions,
    Instant,
    IssuedAssessmentToken,
    ReuseConfidenceHeuristicEngine,
    Scope,
    from_stored,
    independence_key_for,
    status_after,
)
from agentexperience import confidence as engine_module
from agentexperience.confidence import (
    _AssessmentTokenCodec,
    _check_issue,
    _instant_of_unix_ms,
    _unix_ms,
    format_score,
)
from agentexperience.identity import ExperienceIdentity

FAMILY = "confidence-engine"
TOLERANCE = 1e-12


class HostEngineException(Exception):
    """The host engine's own failure, which must propagate unchanged."""


class HostEngine:
    """A host engine as a case declares it (the .NET generator's ``HostEngine``)."""

    def __init__(self, rule_id: Any, rule_version: Any, score: Any) -> None:
        self._rule_id = rule_id
        self._rule_version = rule_version
        self._score = score

    @property
    def rule_id(self) -> str:
        return cast(str, self._rule_id)

    @property
    def rule_version(self) -> str:
        return cast(str, self._rule_version)

    def score(self, input: ExperienceConfidenceInput) -> float:
        if isinstance(self._score, (int, float)):
            return float(self._score)
        if self._score == "NaN":
            return math.nan
        if self._score == "Infinity":
            return math.inf
        if self._score == "-Infinity":
            return -math.inf
        if self._score == "bayes":
            return (2.0 + input.supporting_validations) / (4.0 + input.supporting_validations + input.contradictions)
        if self._score == "stored":
            return input.record.reuse_confidence
        if self._score == "throws":
            raise HostEngineException("The host engine failed.")
        raise AssertionError(f"Unknown host engine score {self._score!r}.")


def _engine(value: Any) -> ExperienceConfidenceEngine | None:
    if value is None:
        return None
    if value == "default":
        return ReuseConfidenceHeuristicEngine.INSTANCE
    return HostEngine(value["ruleId"], value["ruleVersion"], value["score"])


def _uuid(value: str | None) -> uuid.UUID | None:
    return None if value is None else uuid.UUID(value)


def _id(value: str) -> uuid.UUID:
    return uuid.UUID(value)


def _lifetime(ticks: int) -> timedelta:
    # A timedelta holds whole microseconds; a tick count that is not one is taken away from zero, so a positive
    # lifetime stays positive and one past the maximum stays past it.
    microseconds = -((-abs(ticks)) // 10) if ticks % 10 else abs(ticks) // 10
    return timedelta(microseconds=microseconds if ticks >= 0 else -microseconds)


def _options(value: dict[str, Any]) -> ExperienceIndependenceOptions:
    now = _instant_of_unix_ms(value["nowUnixMs"])
    key = value["assessmentTokenKey"]
    return ExperienceIndependenceOptions(
        verification=value["verification"],
        assessment_token_key=None if key is None else bytes.fromhex(key),
        assessment_token_lifetime=_lifetime(value["assessmentTokenLifetimeTicks"]),
        clock=lambda: now,
    )


def _fields(value: dict[str, Any]) -> tuple[Any, ...]:
    return tuple(value[name] for name in ("tenantId", "applicationId", "projectId", "teamId", "agentId", "userId"))


def _scope(value: dict[str, Any]) -> Scope:
    return Scope(*_fields(value))


def _refusal(error: BaseException, message_written: bool = True) -> dict[str, Any]:
    if isinstance(error, ConfidenceArgumentError):
        return {"exception": error.exception, "parameter": error.parameter, "message": error.message}
    if isinstance(error, ConfidenceEngineError):
        return {"exception": "InvalidOperationException", "parameter": None, "message": error.message}
    return {"exception": type(error).__name__, "parameter": None, "message": None}


def _assert_refusal(expected: dict[str, Any] | None, actual: dict[str, Any] | None) -> None:
    if expected is None or actual is None:
        assert expected == actual
        return
    assert actual["exception"] == expected["exception"]
    assert actual["parameter"] == expected["parameter"]
    if expected["message"] is not None:
        assert actual["message"] == expected["message"]


def _close(expected: Any, actual: Any) -> None:
    if expected is None:
        assert actual is None
        return
    assert actual == pytest.approx(expected, abs=TOLERANCE, rel=0)


def _run_score(case: dict[str, Any], expected: dict[str, Any]) -> None:
    try:
        value = engine_module.score(case["supportingValidations"], case["contradictions"])
    except ConfidenceArgumentError as error:
        assert expected["score"] is None
        _assert_refusal(expected["refusal"], _refusal(error))
        return
    assert expected["refusal"] is None
    _close(expected["score"], value)


def _run_rule(case: dict[str, Any], expected: dict[str, Any]) -> None:
    try:
        rule = ConfidenceEngineRule.for_engine(_engine(case["engine"]))
    except ConfidenceArgumentError as error:
        assert expected["recorded"] is None
        _assert_refusal(expected["refusal"], _refusal(error))
        return
    assert expected["refusal"] is None
    assert rule.recorded == expected["recorded"]


def _update_json(update: engine_module.ConfidenceUpdate) -> dict[str, Any]:
    return {
        "evidenceId": str(update.evidence_id),
        "kind": update.kind,
        "source": update.source,
        "runId": str(update.run_id),
        "verificationRoundId": None if update.verification_round_id is None else str(update.verification_round_id),
        "reviewerIdentity": update.reviewer_identity,
        "ruleVersion": update.rule_version,
        "priorReuseConfidence": update.prior_reuse_confidence,
        "newReuseConfidence": update.new_reuse_confidence,
        "priorSupportingValidations": update.prior_supporting_validations,
        "newSupportingValidations": update.new_supporting_validations,
        "priorContradictions": update.prior_contradictions,
        "newContradictions": update.new_contradictions,
        "detail": update.detail,
        "counted": update.counted,
    }


def _assert_update(expected: dict[str, Any], actual: dict[str, Any]) -> None:
    assert actual.keys() == expected.keys()
    for name, value in expected.items():
        if name.endswith("ReuseConfidence"):
            _close(value, actual[name])
        else:
            assert actual[name] == value, name


def _run_apply(case: dict[str, Any], expected: dict[str, Any]) -> None:
    record = from_stored(case["record"])
    evidence = case["evidence"]
    rule = ConfidenceEngineRule.for_engine(_engine(case["engine"]))
    assert engine_module.accepts_evidence_in(record.status) == expected["acceptsEvidence"]
    assert status_after(record.status, evidence["kind"]) == expected["statusAfter"]
    try:
        update = rule.apply(
            record,
            _id(evidence["evidenceId"]),
            evidence["kind"],
            evidence["source"],
            _id(evidence["runId"]),
            _uuid(evidence["verificationRoundId"]),
            evidence["reviewerIdentity"],
            evidence["detail"],
        )
    except (ConfidenceArgumentError, ConfidenceEngineError, HostEngineException) as error:
        assert expected["update"] is None
        _assert_refusal(expected["refusal"], _refusal(error))
        return
    assert expected["refusal"] is None
    _assert_update(expected["update"], _update_json(update))
    recorded_only = update.as_recorded_only()
    _close(expected["recordedOnly"]["newReuseConfidence"], recorded_only.new_reuse_confidence)
    assert recorded_only.new_supporting_validations == expected["recordedOnly"]["newSupportingValidations"]
    assert recorded_only.new_contradictions == expected["recordedOnly"]["newContradictions"]
    assert recorded_only.counted == expected["recordedOnly"]["counted"]
    key = expected["independenceKey"]
    try:
        actual = independence_key_for(update)
    except ConfidenceArgumentError as error:
        assert key["value"] is None
        _assert_refusal(key["refusal"], _refusal(error))
        return
    assert key["refusal"] is None
    assert (actual.source, actual.value) == (key["source"], key["value"])


def _run_options(case: dict[str, Any], expected: dict[str, Any]) -> None:
    try:
        AssessmentTokenIssuer(_options(case["options"]))
    except ConfidenceArgumentError as error:
        assert not expected["accepted"]
        _assert_refusal(expected["refusal"], _refusal(error))
        return
    assert expected["accepted"]
    assert expected["refusal"] is None


def _reviewer(value: dict[str, Any], scope: Scope) -> ExperienceIdentity:
    bounds = AuthorizationBounds(*_fields(value))
    return ExperienceIdentity(value["principalId"], scope, bounds)


def _run_issue(case: dict[str, Any], expected: dict[str, Any]) -> None:
    scope = _scope(case["scope"])
    ids = [_id(value) for value in case["experienceIds"]]
    try:
        issuer = AssessmentTokenIssuer(_options(case["options"]))
        reviewer = _reviewer(case["reviewer"], scope)
        random = issuer.issue(reviewer, scope, _id(case["runId"]), case["kind"], ids)
        # The issuer draws a fresh assessment ID; the fixture pins the same codec's encoding under the case's.
        issued = issuer._codec.encode(
            _id(case["assessmentId"]), *_check_issue(reviewer, scope, _id(case["runId"]), case["kind"], ids)
        )
    except ConfidenceArgumentError as error:
        assert expected["token"] is None
        _assert_refusal(expected["refusal"], _refusal(error))
        return
    except ValueError:
        # This port's identity refuses a blank principal itself, before the issuer can.
        assert expected["refusal"]["parameter"] == "reviewer"
        assert case["reviewer"]["principalId"].strip() == ""
        return
    assert expected["refusal"] is None
    assert str(issued.assessment_id) == expected["assessmentId"]
    assert issued.token == expected["token"]
    assert _unix_ms(issued.issued_at) == expected["issuedAtUnixMs"]
    assert _unix_ms(issued.expires_at) == expected["expiresAtUnixMs"]
    assert random.assessment_id != issued.assessment_id
    assert (random.issued_at, random.expires_at) == (issued.issued_at, issued.expires_at)


def _run_verify(case: dict[str, Any], expected: dict[str, Any]) -> None:
    codec = _AssessmentTokenCodec.create(_options(case["options"]))
    assert codec is not None
    result = codec.verify(case["token"], _scope(case["scope"]), _id(case["runId"]), case["reviewer"], case["kind"])
    assert result.refusal == expected["refusal"]
    assert str(result.assessment_id) == expected["assessmentId"]
    assert [str(value) for value in result.covered] == expected["covered"]


_OPERATIONS = {
    "score": _run_score,
    "rule": _run_rule,
    "apply": _run_apply,
    "options": _run_options,
    "issue": _run_issue,
    "verify": _run_verify,
}


@pytest.mark.parametrize("name", cases(FAMILY))
def test_confidence_engine(name: str) -> None:
    case = read_json(FIXTURES / FAMILY / name / "input.json")
    expected = read_json(FIXTURES / FAMILY / name / "expected.json")
    _OPERATIONS[case["operation"]](case, expected)


# ---- What the fixtures do not state ---------------------------------------------------------------------------------

_KEY = bytes(range(32))
_SCOPE = Scope("tenant", "app", "project")
_RUN = uuid.UUID("000000f6-0000-4000-8000-000000000100")


def _codec(now: Instant | None = None) -> _AssessmentTokenCodec:
    at = now or Instant.parse("2026-03-02T09:00:00Z")
    codec = _AssessmentTokenCodec.create(ExperienceIndependenceOptions(assessment_token_key=_KEY, clock=lambda: at))
    assert codec is not None
    return codec


def test_the_default_engine_is_the_heuristic_recording_the_plain_rule_version() -> None:
    assert ConfidenceEngineRule.for_engine(None) is ConfidenceEngineRule.DEFAULT
    assert ConfidenceEngineRule.DEFAULT.recorded == "1.0.0"
    assert isinstance(ReuseConfidenceHeuristicEngine.INSTANCE, ExperienceConfidenceEngine)
    assert ACCEPTS_EVIDENCE == ("Validated", "Reinforced", "Contested")


def test_a_refusal_names_the_parameter_the_caller_gives() -> None:
    with pytest.raises(ConfidenceArgumentError) as refused:
        ConfidenceEngineRule.for_engine(HostEngine("", "1", 0.5), "engine")
    assert refused.value.parameter == "engine"


def test_a_lone_surrogate_reviewer_signs_as_the_replacement_character() -> None:
    codec = _codec()
    token = codec.encode(uuid.uuid4(), _SCOPE, _RUN, "rev\ud800iewer", "Supporting", [uuid.uuid4()]).token
    assert codec.verify(token, _SCOPE, _RUN, "rev�iewer", "Supporting").refusal is None


def test_an_issued_token_verifies_and_is_never_printed() -> None:
    at = Instant.parse("2026-03-02T09:00:00Z")
    options = ExperienceIndependenceOptions(assessment_token_key=_KEY, clock=lambda: at)
    record = uuid.uuid4()
    reviewer = ExperienceIdentity("reviewer-1", _SCOPE)
    issued = AssessmentTokenIssuer(options).issue(reviewer, _SCOPE, _RUN, "Contradicting", [record])
    assert isinstance(issued, IssuedAssessmentToken)
    assert issued.token not in repr(issued)
    assert "assessment_token_key" not in repr(options)
    result = _codec(at).verify(issued.token, _SCOPE, _RUN, "reviewer-1", "Contradicting")
    assert (result.refusal, result.assessment_id, result.covered) == (None, issued.assessment_id, (record,))
    assert issued.expires_at.ticks - issued.issued_at.ticks == 86_400 * 10_000_000


def test_the_issuer_copies_the_key() -> None:
    key = bytearray(_KEY)
    at = Instant.parse("2026-03-02T09:00:00Z")
    issuer = AssessmentTokenIssuer(ExperienceIndependenceOptions(assessment_token_key=bytes(key), clock=lambda: at))
    issued = issuer.issue(ExperienceIdentity("r", _SCOPE), _SCOPE, _RUN, "Supporting", [uuid.uuid4()])
    assert _codec(at).verify(issued.token, _SCOPE, _RUN, "r", "Supporting").refusal is None


def test_a_human_key_is_the_reviewer_verbatim_and_a_machine_key_the_run_and_round() -> None:
    round_id = uuid.UUID("000000f6-0000-4000-8000-000000000200")
    assert ConfidenceIndependenceKey.for_machine(_RUN, round_id).value == f"machine:{_RUN}:{round_id}"
    assert ConfidenceIndependenceKey.for_human("Ω", _RUN).value == f"human:Ω:{_RUN}"
    with pytest.raises(ConfidenceArgumentError) as refused:
        ConfidenceIndependenceKey.for_human("r ", _RUN)
    assert refused.value.parameter == "reviewerIdentity"


@pytest.mark.parametrize(
    ("value", "text"),
    [
        (1e16, "10000000000000000"),
        (1e17, "1E+17"),
        (1.5e-5, "1.5E-05"),
        (0.0001, "0.0001"),
        (-0.0, "-0"),
        (2.0, "2"),
        (1.2345678901234568e17, "1.2345678901234568E+17"),
        (5e-324, "5E-324"),
    ],
)
def test_a_refused_score_is_written_as_dotnet_writes_it(value: float, text: str) -> None:
    assert format_score(value) == text


class _TextEngine:
    rule_id = "text"
    rule_version = "1"

    def score(self, input: ExperienceConfidenceInput) -> float:
        return cast(float, "0.5")


def test_an_engine_returning_a_non_number_is_refused() -> None:
    with pytest.raises(TypeError):
        ConfidenceEngineRule.for_engine(_TextEngine()).score(cast(Any, None), 1, 0)
