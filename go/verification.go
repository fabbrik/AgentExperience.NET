package agentexperience

import (
	"errors"
	"strings"
)

// Verification aggregation, rule version 1.0.0 (contract step 2).

// VerificationRuleVersion is the aggregation rule's version.
const VerificationRuleVersion = "1.0.0"

// AnyKind is the expected kind that accepts evidence of any kind.
const AnyKind = "*"

// ClosedVerificationRound is the verification round the host closed, for one artifact revision.
type ClosedVerificationRound struct {
	RoundID          UUID
	ArtifactRevision string
}

// RequiredCheck is a check the run must pass, and the evidence kind that satisfies it (AnyKind accepts any).
type RequiredCheck struct {
	CheckID      string
	ExpectedKind string
}

func (c RequiredCheck) accepts(kind string) bool {
	return c.ExpectedKind == AnyKind || c.ExpectedKind == kind
}

// VerificationResult is the aggregated outcome and the inputs it was computed from.
type VerificationResult struct {
	Outcome         Outcome
	CompletionScore float64
	RuleVersion     string
	ClosedRound     *ClosedVerificationRound
	RequiredChecks  []RequiredCheck
}

// Aggregate aggregates the evidence of the closed round and the current artifact revision into an outcome.
func Aggregate(evidence []Evidence, requiredChecks []RequiredCheck, closedRound *ClosedVerificationRound,
	currentArtifactRevision string, evaluatedAt Instant) (VerificationResult, error) {
	if isNullOrWhitespace(currentArtifactRevision) {
		return VerificationResult{}, errors.New("the current artifact revision must not be blank")
	}
	seen := map[string]bool{}
	for _, check := range requiredChecks {
		if isNullOrWhitespace(check.CheckID) {
			return VerificationResult{}, errors.New("required check IDs must not be blank")
		}
		if isNullOrWhitespace(check.ExpectedKind) {
			return VerificationResult{}, errors.New(
				"every required check must name the evidence kind that satisfies it (or '*')")
		}
		if seen[check.CheckID] {
			return VerificationResult{}, errors.New("required check IDs must be unique")
		}
		seen[check.CheckID] = true
	}
	checks := append([]RequiredCheck(nil), requiredChecks...)
	result := func(status VerificationStatus, backing []Evidence, reason string, score float64) VerificationResult {
		if backing == nil {
			backing = []Evidence{}
		}
		return VerificationResult{
			Outcome:         Outcome{Status: status, Evidence: backing, Reason: &reason, EvaluatedAt: evaluatedAt},
			CompletionScore: score,
			RuleVersion:     VerificationRuleVersion,
			ClosedRound:     closedRound,
			RequiredChecks:  checks,
		}
	}

	if closedRound == nil {
		return result(VerificationUnknown, nil,
			"No verification round has been closed by the host; verification is unclosed.", 0), nil
	}
	if closedRound.ArtifactRevision != currentArtifactRevision {
		return result(VerificationUnknown, nil,
			"The host-closed verification round applies to artifact revision '"+closedRound.ArtifactRevision+
				"', which does not match the current artifact revision '"+currentArtifactRevision+
				"'; verification is stale.", 0), nil
	}
	if len(checks) == 0 {
		return result(VerificationUnknown, nil,
			"No required checks were declared for this task; an empty required set can never be conclusively verified.",
			0), nil
	}

	var selected []Evidence
	for _, item := range evidence {
		if item.VerificationRoundID == closedRound.RoundID && item.ArtifactRevision == currentArtifactRevision {
			selected = append(selected, item)
		}
	}
	contributing := map[UUID]bool{}
	passing := 0
	var failed, unknown []string
	for _, check := range checks {
		var matching []Evidence
		for _, item := range selected {
			if item.CheckID == check.CheckID && check.accepts(item.Kind) {
				matching = append(matching, item)
				contributing[item.EvidenceID] = true
			}
		}
		switch resolve(matching) {
		case CheckFail:
			failed = append(failed, check.CheckID)
		case CheckUnknown:
			unknown = append(unknown, check.CheckID)
		default:
			passing++
		}
	}

	score := float64(passing) / float64(len(checks))
	var backing []Evidence
	for _, item := range selected {
		if contributing[item.EvidenceID] {
			backing = append(backing, item)
		}
	}
	if len(failed) > 0 {
		return result(VerificationFailed, backing,
			"Required check(s) resolved to Fail in the host-closed verification round: "+strings.Join(failed, ", ")+
				"; a Fail always dominates a Pass recorded for the same check.", score), nil
	}
	if len(unknown) > 0 {
		return result(VerificationUnknown, backing,
			"Required check(s) have no conclusive Pass/Fail evidence (missing, errored, or genuinely inconclusive) in "+
				"the host-closed verification round: "+strings.Join(unknown, ", ")+".", score), nil
	}
	return result(VerificationVerified, backing,
		"Every required check conclusively resolved to Pass in the host-closed verification round for the current "+
			"artifact revision.", score), nil
}

func resolve(evidence []Evidence) CheckResult {
	if len(evidence) == 0 {
		return CheckUnknown
	}
	for _, item := range evidence {
		if item.Result == CheckFail {
			return CheckFail
		}
	}
	for _, item := range evidence {
		if item.Result != CheckPass {
			return CheckUnknown
		}
	}
	return CheckPass
}
