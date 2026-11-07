package agentexperience

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"time"
)

// The confidence engine (contract 1.15): how a record's reuse confidence moves when evidence about reusing it
// arrives, exactly as .NET's ReuseConfidenceHeuristic, ReuseConfidenceHeuristicEngine, IExperienceConfidenceEngine,
// ConfidenceEngineRule and assessment tokens (AssessmentTokenIssuer and its codec) compute it. Everything here is a
// pure function of its arguments: nothing is read from or written to a store.

// ConfidenceEvidenceKind is whether evidence supports reuse or contradicts it (.NET's ConfidenceEvidenceKind).
type ConfidenceEvidenceKind string

// The evidence kinds.
const (
	EvidenceSupporting    ConfidenceEvidenceKind = "Supporting"
	EvidenceContradicting ConfidenceEvidenceKind = "Contradicting"
)

// ConfidenceEvidenceSource is whether a machine evaluator or a human reviewer observed the evidence.
type ConfidenceEvidenceSource string

// The evidence sources.
const (
	EvidenceMachine ConfidenceEvidenceSource = "Machine"
	EvidenceHuman   ConfidenceEvidenceSource = "Human"
)

// IndependenceRefusal is why the confidence path refused a submission's identifiers (.NET's IndependenceRefusal).
// The token codec reports the three AssessmentToken refusals below; the rest are the verification path's.
type IndependenceRefusal string

// The independence refusals.
const (
	RefusalOwnRun                      IndependenceRefusal = "OwnRun"
	RefusalUnknownRun                  IndependenceRefusal = "UnknownRun"
	RefusalUnknownRound                IndependenceRefusal = "UnknownRound"
	RefusalAssessmentTokenMissing      IndependenceRefusal = "AssessmentTokenMissing"
	RefusalAssessmentTokenInvalid      IndependenceRefusal = "AssessmentTokenInvalid"
	RefusalAssessmentTokenExpired      IndependenceRefusal = "AssessmentTokenExpired"
	RefusalAssessmentTokenNotForRecord IndependenceRefusal = "AssessmentTokenNotForRecord"
	RefusalAssessmentTokenReplayed     IndependenceRefusal = "AssessmentTokenReplayed"
	RefusalAssessmentKeyNotConfigured  IndependenceRefusal = "AssessmentKeyNotConfigured"
	RefusalNotExposed                  IndependenceRefusal = "NotExposed"
	RefusalHostWrittenRun              IndependenceRefusal = "HostWrittenRun"
)

// Confidence engine constants.
const (
	// ConfidenceRuleVersion is the heuristic's rule version (.NET's ReuseConfidenceHeuristic.RuleVersion): what the
	// default engine records.
	ConfidenceRuleVersion = "1.0.0"
	// HeuristicRuleID is the default engine's rule ID. A host engine may not use it.
	HeuristicRuleID = "reuse-heuristic"
	// MaxEvidenceCounter is the largest evidence counter: .NET's counters are 32-bit, and one at the largest value
	// cannot take one more.
	MaxEvidenceCounter = math.MaxInt32
	// ConfidenceEngineParameter is the parameter .NET's lifecycle service names when it refuses an engine's identity.
	ConfidenceEngineParameter = "confidenceEngine"

	maxEngineIdentityLength = 64
)

// ConfidenceArgumentError is an argument .NET refuses with an ArgumentException: Exception is the .NET type's name
// (ArgumentException or ArgumentOutOfRangeException), Parameter the parameter it names.
type ConfidenceArgumentError struct {
	Exception string
	Parameter string
	Message   string
}

func (e *ConfidenceArgumentError) Error() string { return e.Message + " (" + e.Parameter + ")" }

func outOfRange(parameter, message string) error {
	return &ConfidenceArgumentError{Exception: "ArgumentOutOfRangeException", Parameter: parameter, Message: message}
}

func argumentError(parameter, message string) error {
	return &ConfidenceArgumentError{Exception: "ArgumentException", Parameter: parameter, Message: message}
}

// ConfidenceEngineError is a score an engine returned that is not a score in [0, 1] (.NET's
// InvalidOperationException): Error is .NET's message, naming the rule and the value and nothing from the record.
type ConfidenceEngineError struct {
	Rule  string
	Score float64
}

func (e *ConfidenceEngineError) Error() string {
	return "The confidence engine '" + e.Rule + "' returned " + formatShortestFrom(e.Score, 64, 17) +
		", which is not a score in [0, 1]. Nothing was written."
}

// ---- The heuristic ----------------------------------------------------------------------------------------------

// AcceptsEvidence is the statuses in which a record may receive confidence evidence (a new slice each call): not
// the eligible statuses, because a Contested record keeps accruing evidence.
func AcceptsEvidence() []ExperienceStatus {
	return []ExperienceStatus{StatusValidated, StatusReinforced, StatusContested}
}

// AcceptsEvidenceIn reports whether a record in status may receive confidence evidence.
func AcceptsEvidenceIn(status ExperienceStatus) bool {
	for _, accepted := range AcceptsEvidence() {
		if status == accepted {
			return true
		}
	}
	return false
}

// ConfidenceScore is the heuristic's reuse confidence, (1 + S) / (2 + S + F): strictly inside (0, 1). A negative
// count, or one past .NET's 32-bit counters (MaxEvidenceCounter), is refused (a *ConfidenceArgumentError naming
// supportingValidations or contradictions, the first first).
func ConfidenceScore(supportingValidations, contradictions int64) (float64, error) {
	if supportingValidations < 0 || supportingValidations > MaxEvidenceCounter {
		return 0, outOfRange("supportingValidations", "the supporting validations must be 0 to 2147483647")
	}
	if contradictions < 0 || contradictions > MaxEvidenceCounter {
		return 0, outOfRange("contradictions", "the contradictions must be 0 to 2147483647")
	}
	s, f := float64(supportingValidations), float64(contradictions)
	return (1 + s) / (2 + s + f), nil
}

// StatusAfter is the status evidence of kind moves a record in status to: a contradiction contests a Validated or
// Reinforced record; nothing else moves a status.
func StatusAfter(status ExperienceStatus, kind ConfidenceEvidenceKind) ExperienceStatus {
	if kind == EvidenceContradicting && (status == StatusValidated || status == StatusReinforced) {
		return StatusContested
	}
	return status
}

// ConfidenceUpdate is one accepted piece of evidence and what it did to a record's counters and score (.NET's
// ConfidenceUpdate).
type ConfidenceUpdate struct {
	EvidenceID                 UUID
	Kind                       ConfidenceEvidenceKind
	Source                     ConfidenceEvidenceSource
	RunID                      UUID
	VerificationRoundID        *UUID
	ReviewerIdentity           *string
	RuleVersion                string
	PriorReuseConfidence       float64
	NewReuseConfidence         float64
	PriorSupportingValidations int64
	NewSupportingValidations   int64
	PriorContradictions        int64
	NewContradictions          int64
	Detail                     *string
	// AssessmentID is the assessment a human update spends (once per record), when a verifying writer set one. A
	// port's own service never sets it (contract 1.16), but its stores honour one another writer set.
	AssessmentID *UUID
	// Admission is which mode admitted the update, when it was recorded (nil for evidence stored before admission
	// was recorded).
	Admission *ConfidenceEvidenceAdmission
}

// Counted reports whether the update moved a counter: false for a submission recorded only.
func (u ConfidenceUpdate) Counted() bool {
	return u.NewSupportingValidations != u.PriorSupportingValidations || u.NewContradictions != u.PriorContradictions
}

// AsRecordedOnly is the same submission counted zero times: the new counters and score are the prior ones.
func (u ConfidenceUpdate) AsRecordedOnly() ConfidenceUpdate {
	u.NewReuseConfidence = u.PriorReuseConfidence
	u.NewSupportingValidations = u.PriorSupportingValidations
	u.NewContradictions = u.PriorContradictions
	return u
}

// ConfidenceEvidence is one piece of evidence about reusing a record: what .NET's ReuseConfidenceHeuristic.Apply
// takes besides the record.
type ConfidenceEvidence struct {
	EvidenceID UUID
	Kind       ConfidenceEvidenceKind
	Source     ConfidenceEvidenceSource
	// RunID is the run the reuse was observed in.
	RunID UUID
	// VerificationRoundID is the verification round, for machine evidence.
	VerificationRoundID *UUID
	// ReviewerIdentity is the reviewing principal, for human evidence.
	ReviewerIdentity *string
	// Detail is optional sanitized detail.
	Detail *string
}

// ApplyConfidenceEvidence is the update evidence would make to record under the heuristic (.NET's
// ReuseConfidenceHeuristic.Apply), assuming its independence key is free. A negative stored counter is refused
// (naming record.SupportingValidations or record.Contradictions), and so is a counter at MaxEvidenceCounter this
// evidence would increment (naming record). The record's status is not checked here: see AcceptsEvidenceIn.
func ApplyConfidenceEvidence(record ExperienceRecord, evidence ConfidenceEvidence) (ConfidenceUpdate, error) {
	if record.SupportingValidations < 0 || record.SupportingValidations > MaxEvidenceCounter {
		return ConfidenceUpdate{}, outOfRange("record.SupportingValidations", "the record's supporting validations are out of range")
	}
	if record.Contradictions < 0 || record.Contradictions > MaxEvidenceCounter {
		return ConfidenceUpdate{}, outOfRange("record.Contradictions", "the record's contradictions are out of range")
	}
	supporting, contradictions := record.SupportingValidations, record.Contradictions
	if evidence.Kind == EvidenceSupporting {
		if supporting >= MaxEvidenceCounter {
			return ConfidenceUpdate{}, outOfRange("record", "the record's supporting validations cannot take one more")
		}
		supporting++
	} else {
		if contradictions >= MaxEvidenceCounter {
			return ConfidenceUpdate{}, outOfRange("record", "the record's contradictions cannot take one more")
		}
		contradictions++
	}
	score, _ := ConfidenceScore(supporting, contradictions)
	return ConfidenceUpdate{
		EvidenceID:                 evidence.EvidenceID,
		Kind:                       evidence.Kind,
		Source:                     evidence.Source,
		RunID:                      evidence.RunID,
		VerificationRoundID:        evidence.VerificationRoundID,
		ReviewerIdentity:           evidence.ReviewerIdentity,
		RuleVersion:                ConfidenceRuleVersion,
		PriorReuseConfidence:       record.ReuseConfidence,
		NewReuseConfidence:         score,
		PriorSupportingValidations: record.SupportingValidations,
		NewSupportingValidations:   supporting,
		PriorContradictions:        record.Contradictions,
		NewContradictions:          contradictions,
		Detail:                     evidence.Detail,
	}, nil
}

// ConfidenceIndependenceKey is the key an accepted submission is counted under within one record: the first
// submission for a key moves the counters, every later one is recorded and counted zero times.
type ConfidenceIndependenceKey struct {
	Source ConfidenceEvidenceSource
	Value  string
}

// MachineIndependenceKey is machine:{run}:{round}: one verification round of one run counts once.
func MachineIndependenceKey(runID, verificationRoundID UUID) ConfidenceIndependenceKey {
	return ConfidenceIndependenceKey{EvidenceMachine, "machine:" + runID.String() + ":" + verificationRoundID.String()}
}

// HumanIndependenceKey is human:{reviewer}:{run}, the reviewer verbatim: one reviewer's opinion about one run counts
// once. A blank identity, or one with leading or trailing whitespace, is refused (naming reviewerIdentity).
func HumanIndependenceKey(reviewerIdentity string, runID UUID) (ConfidenceIndependenceKey, error) {
	if isNullOrWhitespace(reviewerIdentity) {
		return ConfidenceIndependenceKey{}, argumentError("reviewerIdentity",
			"The value cannot be an empty string or composed entirely of whitespace.")
	}
	if padded(reviewerIdentity) {
		return ConfidenceIndependenceKey{}, argumentError("reviewerIdentity",
			"A reviewer identity may not have leading or trailing whitespace: it would key as a second, independent reviewer.")
	}
	return ConfidenceIndependenceKey{EvidenceHuman, "human:" + reviewerIdentity + ":" + runID.String()}, nil
}

// padded reports whether text starts or ends with what .NET's Trim removes.
func padded(text string) bool {
	runes := []rune(text)
	return len(runes) > 0 && (isWhitespace(runes[0]) || isWhitespace(runes[len(runes)-1]))
}

// IndependenceKeyFor is the key update is deduplicated on: machine evidence on its run and round, human evidence on
// its reviewer and run. Machine evidence without a round, or human evidence without a (non-blank) reviewer, is
// refused (naming update).
func IndependenceKeyFor(update ConfidenceUpdate) (ConfidenceIndependenceKey, error) {
	switch {
	case update.Source == EvidenceMachine && update.VerificationRoundID != nil:
		return MachineIndependenceKey(update.RunID, *update.VerificationRoundID), nil
	case update.Source == EvidenceHuman && update.ReviewerIdentity != nil && !isNullOrWhitespace(*update.ReviewerIdentity):
		return HumanIndependenceKey(*update.ReviewerIdentity, update.RunID)
	}
	return ConfidenceIndependenceKey{}, argumentError("update",
		"Machine evidence needs a verification round and human evidence needs a reviewer identity; without one there is no key to count it independently under.")
}

// ---- The engine seam --------------------------------------------------------------------------------------------

// ExperienceConfidenceInput is what an engine scores: the record as it was read, and the counters to score. When
// evidence is applied the counters are the ones after it, while the record still carries the prior ones. Treat the
// record as data: it may carry content a host engine must not log.
type ExperienceConfidenceInput struct {
	Record                ExperienceRecord
	SupportingValidations int64
	Contradictions        int64
}

// ConfidenceEngine turns a record's evidence counters into its reuse confidence (.NET's
// IExperienceConfidenceEngine). RuleID and RuleVersion are each 1 to 64 characters from [A-Za-z0-9._-]; Score
// returns a value in [0, 1], or an error that is passed on unchanged. It must be deterministic, safe for concurrent
// use, and must not block: it owns only the score. Which statuses accept evidence, the status move, the increments
// and the independence keys stay the library's.
type ConfidenceEngine interface {
	RuleID() string
	RuleVersion() string
	Score(input ExperienceConfidenceInput) (float64, error)
}

// ReuseConfidenceHeuristicEngine is the default engine: ConfidenceScore under ConfidenceRuleVersion. A host engine
// can wrap it and delegate to it.
type ReuseConfidenceHeuristicEngine struct{}

// RuleID is HeuristicRuleID.
func (ReuseConfidenceHeuristicEngine) RuleID() string { return HeuristicRuleID }

// RuleVersion is ConfidenceRuleVersion.
func (ReuseConfidenceHeuristicEngine) RuleVersion() string { return ConfidenceRuleVersion }

// Score is ConfidenceScore over the input's counters.
func (ReuseConfidenceHeuristicEngine) Score(input ExperienceConfidenceInput) (float64, error) {
	return ConfidenceScore(input.SupportingValidations, input.Contradictions)
}

// ConfidenceEngineRule is an engine with its identity checked, as .NET's lifecycle service checks it once when it is
// built. Recorded is what every update records: "1.0.0" for the default engine, "{RuleID}/{RuleVersion}" for a host
// engine.
type ConfidenceEngineRule struct {
	engine   ConfidenceEngine
	recorded string
}

var defaultConfidenceEngineRule = &ConfidenceEngineRule{ReuseConfidenceHeuristicEngine{}, ConfidenceRuleVersion}

// DefaultConfidenceEngineRule is the heuristic's rule, recording "1.0.0" (always the same one: it cannot be replaced).
func DefaultConfidenceEngineRule() *ConfidenceEngineRule { return defaultConfidenceEngineRule }

// errNoEngine is a ConfidenceEngineRule that NewConfidenceEngineRule did not build: the zero value has no engine.
var errNoEngine = errors.New("the confidence engine rule has no engine: build it with NewConfidenceEngineRule")

// NewConfidenceEngineRule checks engine's identity (nil or the heuristic: DefaultConfidenceEngineRule()). A malformed
// RuleID or RuleVersion (the ID first), or a host engine claiming HeuristicRuleID, is refused with a
// *ConfidenceArgumentError naming parameter (.NET's lifecycle service names ConfidenceEngineParameter).
func NewConfidenceEngineRule(engine ConfidenceEngine, parameter string) (*ConfidenceEngineRule, error) {
	switch engine.(type) {
	case nil, ReuseConfidenceHeuristicEngine, *ReuseConfidenceHeuristicEngine:
		return defaultConfidenceEngineRule, nil
	}
	ruleID, ruleVersion := engine.RuleID(), engine.RuleVersion()
	if !wellFormedEngineIdentity(ruleID) {
		return nil, argumentError(parameter, "The confidence engine's RuleId must be 1 to 64 characters from [A-Za-z0-9._-].")
	}
	if !wellFormedEngineIdentity(ruleVersion) {
		return nil, argumentError(parameter, "The confidence engine's RuleVersion must be 1 to 64 characters from [A-Za-z0-9._-].")
	}
	if ruleID == HeuristicRuleID {
		return nil, argumentError(parameter, "A host confidence engine may not use the default engine's RuleId '"+
			HeuristicRuleID+"': its scores would be recorded as if the heuristic had produced them.")
	}
	return &ConfidenceEngineRule{engine, ruleID + "/" + ruleVersion}, nil
}

func wellFormedEngineIdentity(value string) bool {
	if value == "" || len(value) > maxEngineIdentityLength {
		return false
	}
	for _, c := range []byte(value) {
		if !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-') {
			return false
		}
	}
	return true
}

// Engine is the rule's engine (nil for a rule NewConfidenceEngineRule did not build).
func (r *ConfidenceEngineRule) Engine() ConfidenceEngine {
	if r == nil {
		return nil
	}
	return r.engine
}

// Recorded is the rule string every update records ("" for a rule NewConfidenceEngineRule did not build).
func (r *ConfidenceEngineRule) Recorded() string {
	if r == nil {
		return ""
	}
	return r.recorded
}

// ConfidenceEngineFailure is an error a confidence engine returned, marked as the engine's (.NET marks the exception
// for ConfidenceEngineRule.IsEngineFailure). It unwraps to the engine's own error and reads as it.
type ConfidenceEngineFailure struct{ Err error }

func (e *ConfidenceEngineFailure) Error() string { return e.Err.Error() }

// Unwrap is the engine's own error.
func (e *ConfidenceEngineFailure) Unwrap() error { return e.Err }

// IsConfidenceEngineFailure reports whether err is a confidence engine's failure: an error the engine returned (a
// *ConfidenceEngineFailure) or a value that is not a score (a *ConfidenceEngineError). .NET's
// ConfidenceEngineRule.IsEngineFailure.
func IsConfidenceEngineFailure(err error) bool {
	var failure *ConfidenceEngineFailure
	var refused *ConfidenceEngineError
	return errors.As(err, &failure) || errors.As(err, &refused)
}

// Score is the engine's score for record with these counters. An error the engine returns is passed on marked as the
// engine's (*ConfidenceEngineFailure, which unwraps to it; a cancellation is passed on as it is); a value that is NaN,
// infinite or outside [0, 1] is refused with a *ConfidenceEngineError, never clamped.
func (r *ConfidenceEngineRule) Score(record ExperienceRecord, supportingValidations, contradictions int64) (float64, error) {
	if r == nil || r.engine == nil {
		return 0, errNoEngine
	}
	score, err := r.engine.Score(ExperienceConfidenceInput{record, supportingValidations, contradictions})
	if err != nil {
		if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
			// A cancellation is the caller's, and is handled as one: left unmarked, as .NET leaves it.
			return 0, err
		}
		return 0, &ConfidenceEngineFailure{Err: err}
	}
	if math.IsNaN(score) || math.IsInf(score, 0) || score < 0 || score > 1 {
		return 0, &ConfidenceEngineError{Rule: r.recorded, Score: score}
	}
	return score, nil
}

// Apply is the update evidence makes, as .NET's lifecycle service computes it: ApplyConfidenceEvidence settles the
// counters (and refuses an overflow before the engine is asked anything), then the rule's engine scores the record
// as read with the new counters, and the update records Recorded. LifecycleService.ApplyEvidence computes the same
// update and also commits it.
func (r *ConfidenceEngineRule) Apply(record ExperienceRecord, evidence ConfidenceEvidence) (ConfidenceUpdate, error) {
	if r == nil || r.engine == nil {
		return ConfidenceUpdate{}, errNoEngine
	}
	update, err := ApplyConfidenceEvidence(record, evidence)
	if err != nil {
		return ConfidenceUpdate{}, err
	}
	score, err := r.Score(record, update.NewSupportingValidations, update.NewContradictions)
	if err != nil {
		return ConfidenceUpdate{}, err
	}
	update.NewReuseConfidence = score
	update.RuleVersion = r.recorded
	return update, nil
}

// ---- Assessment tokens ------------------------------------------------------------------------------------------

// IndependenceVerification is .NET's IndependenceVerification; "" is Verified.
type IndependenceVerification string

// The verification modes.
const (
	IndependenceVerified                     IndependenceVerification = "Verified"
	IndependenceTrustHostSuppliedIdentifiers IndependenceVerification = "TrustHostSuppliedIdentifiers"
)

// HostTrustedEvidenceEffect is .NET's HostTrustedEvidenceEffect; "" is Counted.
type HostTrustedEvidenceEffect string

// The host-trusted evidence effects.
const (
	HostTrustedCounted      HostTrustedEvidenceEffect = "Counted"
	HostTrustedRecordedOnly HostTrustedEvidenceEffect = "RecordedOnly"
)

// Assessment token constants.
const (
	MinimumAssessmentTokenKeyBytes = 32
	DefaultAssessmentTokenLifetime = 24 * time.Hour
	MaximumAssessmentTokenLifetime = 30 * 24 * time.Hour
	// MaxAssessedRecords is the most records one assessment covers (.NET's ExperienceReuseFeedback.MaxExposedRecords).
	MaxAssessedRecords    = 64
	AssessmentTokenPrefix = "aexat1."

	maxTokenLength        = 4096
	allowedClockSkewMs    = int64(5 * 60 * 1000)
	tokenVersion          = 1
	tokenHeaderLength     = 1 + 16 + 8 + 8 + 1 + 1
	tokenIssuedAtOffset   = 17
	tokenExpiresAtOffset  = 25
	tokenKindOffset       = 33
	tokenCountOffset      = 34
	tokenIDLength         = 16
	tokenMACLength        = 32
	unixEpochMilliseconds = int64(62_135_596_800_000)
	ticksPerMillisecond   = int64(10_000)
)

var tokenDomainLabel = []byte("AgentExperience.AssessmentToken/v1\x00")

// IndependenceOptions are .NET's ExperienceIndependenceOptions, as far as assessment tokens use them. Go cannot tell
// an unset value from an explicit zero, so a zero Verification is Verified, a zero HostTrustedEvidence Counted, a
// zero AssessmentTokenLifetime DefaultAssessmentTokenLifetime and a nil Clock Now; a negative lifetime, one past
// MaximumAssessmentTokenLifetime, an undefined mode and a key shorter than MinimumAssessmentTokenKeyBytes are refused.
// A nil AssessmentTokenKey means human evidence cannot be verified.
type IndependenceOptions struct {
	Verification            IndependenceVerification
	HostTrustedEvidence     HostTrustedEvidenceEffect
	AssessmentTokenKey      []byte `json:"-"`
	AssessmentTokenLifetime time.Duration
	Clock                   func() Instant `json:"-"`
}

// String never prints the key.
func (o IndependenceOptions) String() string {
	return fmt.Sprintf("IndependenceOptions{Verification: %s, AssessmentTokenLifetime: %s, key set: %t}",
		o.Verification, o.AssessmentTokenLifetime, o.AssessmentTokenKey != nil)
}

// GoString never prints the key.
func (o IndependenceOptions) GoString() string { return o.String() }

// IssuedAssessmentToken is one assessment token, as AssessmentTokenIssuer.Issue minted it. Token is a bearer
// credential for one review: String and GoString never print it.
type IssuedAssessmentToken struct {
	AssessmentID UUID
	Token        string
	IssuedAt     Instant
	ExpiresAt    Instant
}

// String redacts the token.
func (t IssuedAssessmentToken) String() string {
	return "IssuedAssessmentToken{AssessmentID: " + t.AssessmentID.String() + ", Token: <redacted>, IssuedAt: " +
		t.IssuedAt.String() + ", ExpiresAt: " + t.ExpiresAt.String() + "}"
}

// GoString redacts the token.
func (t IssuedAssessmentToken) GoString() string { return t.String() }

// AssessmentTokenIssuer mints assessment tokens (.NET's AssessmentTokenIssuer): the only way a human assessment can
// move a confidence score under verification. Call it from the host's own review flow, where a person's decision is
// recorded, never from code an agent drives.
type AssessmentTokenIssuer struct {
	codec   *assessmentTokenCodec
	newUUID func() UUID
}

// NewAssessmentTokenIssuer is an issuer over options, which must carry a key; invalid options are refused (naming
// options).
func NewAssessmentTokenIssuer(options IndependenceOptions) (*AssessmentTokenIssuer, error) {
	codec, err := newAssessmentTokenCodec(options)
	if err != nil {
		return nil, err
	}
	if codec == nil {
		return nil, argumentError("options", "An assessment token issuer needs ExperienceIndependenceOptions.AssessmentTokenKey.")
	}
	return &AssessmentTokenIssuer{codec: codec, newUUID: NewRandomUUID}, nil
}

// Issue mints a token for one human assessment of one run: reviewer's principal is the reviewer the evidence is
// counted under, and its authorization must permit scope. Refusals, in .NET's order, name reviewer, scope, runId,
// kind or experienceIds.
func (i *AssessmentTokenIssuer) Issue(reviewer Identity, scope Scope, runID UUID, kind ConfidenceEvidenceKind,
	experienceIDs []UUID) (IssuedAssessmentToken, error) {
	ids := append([]UUID(nil), experienceIDs...) // checked and encoded from one copy, as .NET does
	if err := checkIssue(reviewer, scope, runID, kind, ids); err != nil {
		return IssuedAssessmentToken{}, err
	}
	return i.codec.encode(i.newUUID(), scope, runID, reviewer.Principal, kind, ids)
}

func checkIssue(reviewer Identity, scope Scope, runID UUID, kind ConfidenceEvidenceKind, ids []UUID) error {
	if isNullOrWhitespace(reviewer.Principal) || padded(reviewer.Principal) {
		return argumentError("reviewer", "The reviewer's PrincipalId must be non-blank and carry no leading or trailing whitespace: it is the reviewer the evidence is counted under.")
	}
	bounds := reviewer.Authorization()
	if isNullOrWhitespace(bounds.TenantID) || !bounds.Permits(scope) {
		return argumentError("scope", "The scope lies outside the reviewer's authorization.")
	}
	if runID.IsZero() {
		return argumentError("runId", "The run must not be an empty GUID.")
	}
	if kind != EvidenceSupporting && kind != EvidenceContradicting {
		return argumentError("kind", "The evidence kind must be defined.")
	}
	if len(ids) == 0 || len(ids) > MaxAssessedRecords {
		return argumentError("experienceIds", "An assessment covers 1 to 64 records.")
	}
	seen := make(map[UUID]bool, len(ids))
	for _, id := range ids {
		if id.IsZero() || seen[id] {
			return argumentError("experienceIds", "The records must be distinct and non-empty.")
		}
		seen[id] = true
	}
	return nil
}

// assessmentTokenCodec is the token format, and the one place a token is signed or checked (.NET's internal
// AssessmentTokenCodec): "aexat1." and the unpadded base64url of a version byte (1), the assessment ID, the issue
// time and expiry (Unix milliseconds, big-endian), the kind, the record count, each record ID, and the 32-byte
// HMAC-SHA256 over a domain label, those bytes, the six scope fields (each a presence byte, then a 4-byte length
// and its UTF-8), the run and the reviewer (a 4-byte length and its UTF-8).
type assessmentTokenCodec struct {
	key      []byte
	lifetime time.Duration
	clock    func() Instant
}

// newAssessmentTokenCodec validates options in .NET's order: a codec over a copy of the key, or nil with no key.
func newAssessmentTokenCodec(options IndependenceOptions) (*assessmentTokenCodec, error) {
	if options.Verification != "" && options.Verification != IndependenceVerified &&
		options.Verification != IndependenceTrustHostSuppliedIdentifiers {
		return nil, argumentError("options", "ExperienceIndependenceOptions.Verification must be a defined mode.")
	}
	lifetime := options.AssessmentTokenLifetime
	if lifetime == 0 {
		lifetime = DefaultAssessmentTokenLifetime
	}
	if lifetime < 0 || lifetime > MaximumAssessmentTokenLifetime {
		return nil, argumentError("options", "ExperienceIndependenceOptions.AssessmentTokenLifetime must be strictly positive and at most 30.00:00:00.")
	}
	clock := options.Clock
	if clock == nil {
		clock = Now
	}
	if options.AssessmentTokenKey == nil {
		return nil, nil
	}
	if len(options.AssessmentTokenKey) < MinimumAssessmentTokenKeyBytes {
		return nil, argumentError("options", "ExperienceIndependenceOptions.AssessmentTokenKey must be at least 32 bytes.")
	}
	return &assessmentTokenCodec{append([]byte(nil), options.AssessmentTokenKey...), lifetime, clock}, nil
}

func unixMilliseconds(instant Instant) int64 {
	return instant.Ticks()/ticksPerMillisecond - unixEpochMilliseconds
}

// maxUnixMilliseconds is the last millisecond .NET's DateTimeOffset represents: 9999-12-31T23:59:59.999Z.
const maxUnixMilliseconds = int64(253_402_300_799_999)

// instantOfUnixMilliseconds refuses, as .NET's DateTimeOffset.FromUnixTimeMilliseconds does, an instant before year
// 1 or after year 9999.
func instantOfUnixMilliseconds(milliseconds int64) (Instant, error) {
	if milliseconds < -unixEpochMilliseconds || milliseconds > maxUnixMilliseconds {
		return Instant{}, outOfRange("milliseconds", "the token's times cannot be represented")
	}
	return InstantFromTicks((milliseconds + unixEpochMilliseconds) * ticksPerMillisecond)
}

func kindByte(kind ConfidenceEvidenceKind) int {
	switch kind {
	case EvidenceSupporting:
		return 0
	case EvidenceContradicting:
		return 1
	}
	return -1
}

func (c *assessmentTokenCodec) encode(assessmentID UUID, scope Scope, runID UUID, reviewer string,
	kind ConfidenceEvidenceKind, ids []UUID) (IssuedAssessmentToken, error) {
	issuedAt := unixMilliseconds(c.clock())
	expiresAt := issuedAt + c.lifetime.Milliseconds()
	issued, err := instantOfUnixMilliseconds(issuedAt)
	if err != nil {
		return IssuedAssessmentToken{}, err
	}
	expires, err := instantOfUnixMilliseconds(expiresAt)
	if err != nil {
		return IssuedAssessmentToken{}, err
	}
	body := make([]byte, 0, tokenHeaderLength+len(ids)*tokenIDLength+tokenMACLength)
	body = append(body, tokenVersion)
	body = append(body, assessmentID[:]...)
	body = binary.BigEndian.AppendUint64(body, uint64(issuedAt))
	body = binary.BigEndian.AppendUint64(body, uint64(expiresAt))
	body = append(body, byte(kindByte(kind)), byte(len(ids)))
	for _, id := range ids {
		body = append(body, id[:]...)
	}
	body = append(body, c.mac(body, scope, runID, reviewer)...)
	return IssuedAssessmentToken{
		AssessmentID: assessmentID,
		Token:        AssessmentTokenPrefix + base64.RawURLEncoding.EncodeToString(body),
		IssuedAt:     issued,
		ExpiresAt:    expires,
	}, nil
}

// tokenVerification is what the codec found: an empty refusal for a genuine token, with its assessment ID and
// covered records.
type tokenVerification struct {
	refusal      IndependenceRefusal
	assessmentID UUID
	covered      []UUID
}

func isBase64URL(c rune) bool {
	return c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_'
}

// verify checks a token against the binding a submission names. The signature is checked, in constant time, before
// anything the token claims is read.
func (c *assessmentTokenCodec) verify(token string, scope Scope, runID UUID, reviewer string,
	kind ConfidenceEvidenceKind) tokenVerification {
	invalid := tokenVerification{refusal: RefusalAssessmentTokenInvalid, covered: []UUID{}}
	if token == "" {
		return tokenVerification{refusal: RefusalAssessmentTokenMissing, covered: []UUID{}}
	}
	if UTF16Len(token) > maxTokenLength || len(token) < len(AssessmentTokenPrefix) ||
		token[:len(AssessmentTokenPrefix)] != AssessmentTokenPrefix {
		return invalid
	}
	encoded := token[len(AssessmentTokenPrefix):]
	for _, character := range encoded {
		if !isBase64URL(character) {
			return invalid
		}
	}
	data, err := base64.RawURLEncoding.DecodeString(encoded)
	if err != nil {
		return invalid
	}
	if len(data) < tokenHeaderLength+tokenIDLength+tokenMACLength || data[0] != tokenVersion {
		return invalid
	}
	count := int(data[tokenCountOffset])
	if count == 0 || count > MaxAssessedRecords || len(data) != tokenHeaderLength+count*tokenIDLength+tokenMACLength {
		return invalid
	}
	// A non-canonical encoding of the same bytes is not a different token.
	if base64.RawURLEncoding.EncodeToString(data) != encoded {
		return invalid
	}
	signed := len(data) - tokenMACLength
	if subtle.ConstantTimeCompare(c.mac(data[:signed], scope, runID, reviewer), data[signed:]) != 1 {
		return invalid
	}
	if int(data[tokenKindOffset]) != kindByte(kind) {
		return invalid
	}
	issuedAt := int64(binary.BigEndian.Uint64(data[tokenIssuedAtOffset:]))
	expiresAt := int64(binary.BigEndian.Uint64(data[tokenExpiresAtOffset:]))
	now := unixMilliseconds(c.clock())
	if issuedAt < 0 || expiresAt <= issuedAt || expiresAt-issuedAt > MaximumAssessmentTokenLifetime.Milliseconds() ||
		issuedAt > now+allowedClockSkewMs {
		return invalid
	}
	if now >= expiresAt {
		return tokenVerification{refusal: RefusalAssessmentTokenExpired, covered: []UUID{}}
	}
	covered := make([]UUID, count)
	for index := range covered {
		copy(covered[index][:], data[tokenHeaderLength+index*tokenIDLength:])
	}
	var assessmentID UUID
	copy(assessmentID[:], data[1:1+tokenIDLength])
	return tokenVerification{assessmentID: assessmentID, covered: covered}
}

func (c *assessmentTokenCodec) mac(signed []byte, scope Scope, runID UUID, reviewer string) []byte {
	hash := hmac.New(sha256.New, c.key)
	hash.Write(tokenDomainLabel)
	hash.Write(signed)
	for _, field := range scope.Fields() {
		if field == nil {
			hash.Write([]byte{0})
			continue
		}
		hash.Write([]byte{1})
		hash.Write(lengthPrefixed(*field))
	}
	hash.Write(runID[:])
	hash.Write(lengthPrefixed(reviewer))
	return hash.Sum(nil)
}

func lengthPrefixed(value string) []byte {
	encoded := utf8Bytes(value)
	return append(binary.BigEndian.AppendUint32(nil, uint32(len(encoded))), encoded...)
}
