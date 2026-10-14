package agentexperience

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"sort"
)

// Verification for an agent integration: the context a verifier is given, the verdict it returns, and the
// ready-made verifiers, with the semantics of .NET's ExperienceVerifiers.
//
// A verifier decides only from facts the host's code produced (the captured outcome of a tool call, or the host's
// own function), never from message text or model output. A nil verdict means the run is not stored; an error
// leaves it unstored too, and the integration reports it. Each ready-made verifier declares one required check, with
// an evidence kind of its own (ToolOutcome, HostCheck or HumanApproval), and evidence for it built with
// VerificationContext.CreateEvidence. The artifact revision is UnversionedRevision unless WithArtifactRevision names
// one (a build identifier or a commit), for an artifact that changes between runs.
//
// The constructors panic on a blank tool name or check ID, or on no verifier: those are programming errors, as an
// invalid regular expression is to regexp.MustCompile.

// UnversionedRevision is the artifact revision a verifier names when it is given none.
const UnversionedRevision = "unversioned"

// The evidence kinds of the ready-made verifiers.
const (
	ToolOutcomeKind   = "ToolOutcome"
	HostCheckKind     = "HostCheck"
	HumanApprovalKind = "HumanApproval"
)

// NewRandomUUID is a random (version 4) UUID.
func NewRandomUUID() UUID {
	var u UUID
	_, _ = rand.Read(u[:]) // crypto/rand.Read never fails (Go 1.24).
	u[6] = u[6]&0x0f | 0x40
	u[8] = u[8]&0x3f | 0x80
	return u
}

// VerificationContext is what a verifier is given: the completed, sanitized run, the round it is checked in, the
// identity it ran under, and the clock and identifier source evidence is made with.
type VerificationContext struct {
	Run      CapturedRun
	RoundID  UUID
	Identity Identity
	// Clock is Now when nil.
	Clock func() Instant
	// NewID is NewRandomUUID when nil.
	NewID func() UUID
}

// CreateEvidence is a piece of evidence for this round: a new identifier, RoundID, and the current time. The result
// must come from the host's own check, never from the model.
func (c *VerificationContext) CreateEvidence(checkID, kind string, result CheckResult, producer,
	artifactRevision string, detail *string) Evidence {
	clock, newID := c.Clock, c.NewID
	if clock == nil {
		clock = Now
	}
	if newID == nil {
		newID = NewRandomUUID
	}
	return Evidence{
		EvidenceID:          newID(),
		VerificationRoundID: c.RoundID,
		ArtifactRevision:    artifactRevision,
		CheckID:             checkID,
		Kind:                kind,
		Result:              result,
		Producer:            producer,
		Detail:              detail,
		CapturedAt:          clock(),
	}
}

// Verdict is the host's verdict on one run: the checks it had to pass, the evidence from the host's own checks, and
// the artifact revision they were run against (it closes the round and is the current revision).
type Verdict struct {
	RequiredChecks   []RequiredCheck
	Evidence         []Evidence
	ArtifactRevision string
}

// Verifier decides whether a run is stored, and how: a verdict, or nil for "do not store".
type Verifier func(ctx context.Context, verification *VerificationContext) (*Verdict, error)

// ApprovalDecision is a person's decision on a run, as the host's own approval flow recorded it. ApproverID is
// stored verbatim as the evidence's producer, so use an opaque identifier (a user's object ID), never an email
// address or a name.
type ApprovalDecision struct {
	ApproverID string
	Approved   bool
}

// VerifierOption changes a ready-made verifier.
type VerifierOption func(*verifierSettings)

type verifierSettings struct {
	checkID  *string
	revision *string
}

// WithArtifactRevision names the artifact revision the verifier's evidence is for (not blank).
func WithArtifactRevision(revision string) VerifierOption {
	return func(s *verifierSettings) { s.revision = &revision }
}

// WithCheckID names ToolSucceeded's check (not blank); by default it is the tool's name. The other verifiers take
// their check ID as an argument and refuse this option.
func WithCheckID(checkID string) VerifierOption {
	return func(s *verifierSettings) { s.checkID = &checkID }
}

func settingsOf(options []VerifierOption) verifierSettings {
	var s verifierSettings
	for _, option := range options {
		if option != nil {
			option(&s)
		}
	}
	return s
}

func (s verifierSettings) artifactRevision() string {
	if s.revision == nil {
		return UnversionedRevision
	}
	if isNullOrWhitespace(*s.revision) {
		panic("agentexperience: the artifact revision must not be blank; leave WithArtifactRevision out for the default")
	}
	return *s.revision
}

func single(checkID, kind string, evidence Evidence, revision string) *Verdict {
	return &Verdict{
		RequiredChecks:   []RequiredCheck{{CheckID: checkID, ExpectedKind: kind}},
		Evidence:         []Evidence{evidence},
		ArtifactRevision: revision,
	}
}

func resultOf(passed bool) CheckResult {
	if passed {
		return CheckPass
	}
	return CheckFail
}

// ToolSucceeded verifies a run by the outcome of its last call to toolName: the calls are ordered by attempt
// sequence, then by call sequence within the attempt, across all of the run's attempts, and the last one decides. A
// call that returned passes; a call that failed (its tool returned an error, or the classifier named an error class
// for it) fails, and the run is stored Quarantined. A run with no such call is not stored.
//
// It decides from each captured call's tool name (exact match), sequence numbers and error, never from a result, so
// it works when sanitization redacts results. A tool that reports failure in its return value (an exit code, an
// HTTP status) counts as returned unless a classifier classifies it, for example ExitCode. The evidence is of kind
// ToolOutcome, produced by "tool:" + toolName; its detail names only the failed call's error class.
func ToolSucceeded(toolName string, options ...VerifierOption) Verifier {
	if isNullOrWhitespace(toolName) {
		panic("agentexperience: the tool name must not be blank")
	}
	settings := settingsOf(options)
	checkID := toolName
	if settings.checkID != nil {
		if isNullOrWhitespace(*settings.checkID) {
			panic("agentexperience: the check ID must not be blank; leave WithCheckID out for the tool's name")
		}
		checkID = *settings.checkID
	}
	revision := settings.artifactRevision()
	producer := "tool:" + toolName
	return func(_ context.Context, verification *VerificationContext) (*Verdict, error) {
		attempts := append([]Attempt(nil), verification.Run.Attempts...)
		sort.SliceStable(attempts, func(i, j int) bool { return attempts[i].SequenceNumber < attempts[j].SequenceNumber })
		var last *ToolCall
		for _, attempt := range attempts {
			calls := append([]ToolCall(nil), attempt.ToolCalls...)
			sort.SliceStable(calls, func(i, j int) bool { return calls[i].SequenceNumber < calls[j].SequenceNumber })
			for i := range calls {
				if calls[i].ToolName == toolName {
					last = &calls[i]
				}
			}
		}
		if last == nil {
			return nil, nil
		}
		failed := last.Error != nil
		detail := "The last " + toolName + " call returned."
		if failed {
			detail = "The last " + toolName + " call failed: " + ErrorClass(last.Error) + "."
		}
		evidence := verification.CreateEvidence(checkID, ToolOutcomeKind, resultOf(!failed), producer, revision, &detail)
		return single(checkID, ToolOutcomeKind, evidence, revision), nil
	}
}

func refuseCheckIDOption(settings verifierSettings) {
	if settings.checkID != nil {
		panic("agentexperience: WithCheckID is for ToolSucceeded; this verifier takes its check ID as an argument")
	}
}

// Check verifies a run by the host's own check: true passes, false fails (the run is stored Quarantined), and nil
// means the run is not stored. The evidence is of kind HostCheck, produced by "host". The verdict must come from the
// host's own code (a test run, an exit code, a query), never from the model's claim.
func Check(checkID string, check func(context.Context, *VerificationContext) (*bool, error),
	options ...VerifierOption) Verifier {
	if isNullOrWhitespace(checkID) {
		panic("agentexperience: the check ID must not be blank")
	}
	if check == nil {
		panic("agentexperience: the check function must not be nil")
	}
	settings := settingsOf(options)
	refuseCheckIDOption(settings)
	revision := settings.artifactRevision()
	return func(ctx context.Context, verification *VerificationContext) (*Verdict, error) {
		passed, err := check(ctx, verification)
		if err != nil || passed == nil {
			return nil, err
		}
		evidence := verification.CreateEvidence(checkID, HostCheckKind, resultOf(*passed), "host", revision, nil)
		return single(checkID, HostCheckKind, evidence, revision), nil
	}
}

// Approval verifies a run by a person's decision: approved passes, rejected fails (the run is stored Quarantined),
// and nil means the run is not stored. The evidence is of kind HumanApproval, and the approver's identifier is
// stored verbatim as its producer. A decision naming no approver is an error, so the run is not stored and the
// failure is reported. The approver must come from the host's own authentication.
func Approval(checkID string, decide func(context.Context, *VerificationContext) (*ApprovalDecision, error),
	options ...VerifierOption) Verifier {
	if isNullOrWhitespace(checkID) {
		panic("agentexperience: the check ID must not be blank")
	}
	if decide == nil {
		panic("agentexperience: the approval function must not be nil")
	}
	settings := settingsOf(options)
	refuseCheckIDOption(settings)
	revision := settings.artifactRevision()
	return func(ctx context.Context, verification *VerificationContext) (*Verdict, error) {
		decided, err := decide(ctx, verification)
		if err != nil || decided == nil {
			return nil, err
		}
		if isNullOrWhitespace(decided.ApproverID) {
			return nil, fmt.Errorf("the approval for check '%s' names no approver; an approval must name who "+
				"decided. The run is not stored", checkID)
		}
		evidence := verification.CreateEvidence(checkID, HumanApprovalKind, resultOf(decided.Approved),
			decided.ApproverID, revision, nil)
		return single(checkID, HumanApprovalKind, evidence, revision), nil
	}
}

func validateVerifiers(verifiers []Verifier) []Verifier {
	if len(verifiers) == 0 {
		panic("agentexperience: at least one verifier is required")
	}
	for _, verifier := range verifiers {
		if verifier == nil {
			panic("agentexperience: a verifier is nil")
		}
	}
	return append([]Verifier(nil), verifiers...)
}

// AllOf verifies a run by every one of verifiers: it runs them in order and returns one verdict with all their
// required checks and evidence, so the run is stored Validated only when every check passes. When one returns nil it
// stops there (the rest are not run, so no person is asked for an approval needlessly) and the run is not stored.
//
// The combined revision is the one revision the verifiers name other than UnversionedRevision (evidence of a
// verifier given no revision is restamped with it), else UnversionedRevision. Two different explicit revisions or
// two verifiers requiring the same check are an error, so the run is not stored and the failure is reported.
func AllOf(verifiers ...Verifier) Verifier {
	every := validateVerifiers(verifiers)
	return func(ctx context.Context, verification *VerificationContext) (*Verdict, error) {
		var results []*Verdict
		for _, verifier := range every {
			result, err := verifier(ctx, verification)
			if err != nil || result == nil {
				return nil, err
			}
			results = append(results, result)
		}
		revision := UnversionedRevision
		for _, result := range results {
			if result.ArtifactRevision == UnversionedRevision {
				continue
			}
			if revision != UnversionedRevision && revision != result.ArtifactRevision {
				return nil, errors.New("AllOf combined verifiers that name different artifact revisions; give them " +
					"all the same revision. The run is not stored")
			}
			revision = result.ArtifactRevision
		}
		combined := &Verdict{ArtifactRevision: revision}
		ids := map[string]bool{}
		for _, result := range results {
			for _, required := range result.RequiredChecks {
				if ids[required.CheckID] {
					return nil, fmt.Errorf("AllOf combined verifiers that both require the check '%s'; give each "+
						"its own check identifier. The run is not stored", required.CheckID)
				}
				ids[required.CheckID] = true
				combined.RequiredChecks = append(combined.RequiredChecks, required)
			}
			// Evidence of a verifier that took the default revision is restamped with the explicit one; any other
			// mismatch is left as it is, for the integration's own check to refuse.
			for _, item := range result.Evidence {
				if result.ArtifactRevision == UnversionedRevision && item.ArtifactRevision == UnversionedRevision {
					item.ArtifactRevision = revision
				}
				combined.Evidence = append(combined.Evidence, item)
			}
		}
		return combined, nil
	}
}

// passed reports whether every required check has matching evidence for the round and revision, and all of it
// passed.
func passed(verdict *Verdict, roundID UUID) bool {
	if len(verdict.RequiredChecks) == 0 {
		return false
	}
	for _, required := range verdict.RequiredChecks {
		matching := 0
		for _, item := range verdict.Evidence {
			if item.VerificationRoundID != roundID || item.ArtifactRevision != verdict.ArtifactRevision ||
				item.CheckID != required.CheckID || !required.accepts(item.Kind) {
				continue
			}
			if item.Result != CheckPass {
				return false
			}
			matching++
		}
		if matching == 0 {
			return false
		}
	}
	return true
}

// AnyOf verifies a run by the first of verifiers that passes: it runs them in order and returns the first verdict
// that passed, without running the rest. A verdict passed when it requires at least one check and every required
// check has evidence for this round and the verdict's revision, with that check's identifier and a kind it accepts
// (AnyKind accepts any), all of it Pass. When none passes, it returns the first verdict any returned, so the failure
// is stored Quarantined; when all return nil, the run is not stored.
func AnyOf(verifiers ...Verifier) Verifier {
	every := validateVerifiers(verifiers)
	return func(ctx context.Context, verification *VerificationContext) (*Verdict, error) {
		var first *Verdict
		for _, verifier := range every {
			result, err := verifier(ctx, verification)
			if err != nil {
				return nil, err
			}
			if result == nil {
				continue
			}
			if passed(result, verification.RoundID) {
				return result, nil
			}
			if first == nil {
				first = result
			}
		}
		return first, nil
	}
}
