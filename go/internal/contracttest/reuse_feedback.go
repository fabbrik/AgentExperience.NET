package contracttest

// Contract 1.17's reuse-feedback fixtures as JSON: reading submissions and ledger submissions, and writing results the
// way .NET's generator writes them. Shared by the in-memory runner, the PostgreSQL leg and the cross-language writer.

import (
	"encoding/json"
	"math"
	"strconv"
	"strings"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

func uuidList(value any) []ae.UUID {
	if value == nil {
		return nil
	}
	items := list(value)
	ids := make([]ae.UUID, len(items))
	for i, item := range items {
		ids[i] = uuidOf(item)
	}
	return ids
}

func uuidListJSON(ids []ae.UUID) any {
	if ids == nil {
		return nil
	}
	items := make([]any, len(ids))
	for i, id := range ids {
		items[i] = id.String()
	}
	return items
}

// measureValue reads a measure's value: a number, or "NaN", "Infinity", "-Infinity".
func measureValue(value any) float64 {
	switch value {
	case "NaN":
		return math.NaN()
	case "Infinity":
		return math.Inf(1)
	case "-Infinity":
		return math.Inf(-1)
	}
	return number(value)
}

// MeasureValueJSON writes a measure's value as the fixtures do.
func MeasureValueJSON(value float64) any {
	switch {
	case math.IsNaN(value):
		return "NaN"
	case math.IsInf(value, 1):
		return "Infinity"
	case math.IsInf(value, -1):
		return "-Infinity"
	}
	return json.Number(strconv.FormatFloat(value, 'g', -1, 64))
}

func measureOf(value any) *ae.ReuseMeasure {
	o := object(value)
	if o == nil {
		return nil
	}
	return &ae.ReuseMeasure{Kind: text(get(o, "kind")), Value: measureValue(get(o, "value"))}
}

func measureJSON(m *ae.ReuseMeasure) any {
	if m == nil {
		return nil
	}
	return ae.NewObject(ae.Member{Key: "kind", Value: m.Kind}, ae.Member{Key: "value", Value: MeasureValueJSON(m.Value)})
}

func optionalScopeOf(value any) *ae.Scope {
	if value == nil {
		return nil
	}
	scope := ScopeOf(value)
	return &scope
}

// ScopeJSON writes a scope as the fixtures do.
func ScopeJSON(scope ae.Scope) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "tenantId", Value: scope.TenantID},
		ae.Member{Key: "applicationId", Value: scope.ApplicationID},
		ae.Member{Key: "projectId", Value: scope.ProjectID},
		ae.Member{Key: "teamId", Value: optional(scope.TeamID)},
		ae.Member{Key: "agentId", Value: optional(scope.AgentID)},
		ae.Member{Key: "userId", Value: optional(scope.UserID)},
	)
}

func optionalInstant(value any) *ae.Instant {
	if value == nil {
		return nil
	}
	instant := instantOf(value)
	return &instant
}

func optionalInstantJSON(value *ae.Instant) any {
	if value == nil {
		return nil
	}
	return value.String()
}

// FeedbackOf reads a submit step's feedback. An enum value no member names is kept as written (undefined).
func FeedbackOf(value any) ae.ExperienceReuseFeedback {
	o := object(value)
	feedback := ae.ExperienceReuseFeedback{
		FeedbackID:           uuidOf(get(o, "feedbackId")),
		RunID:                uuidOf(get(o, "runId")),
		Scope:                ScopeOf(get(o, "scope")),
		ExposedExperienceIDs: uuidList(get(o, "exposedExperienceIds")),
		RunOutcome:           ae.VerificationStatus(text(get(o, "runOutcome"))),
		Measure:              measureOf(get(o, "measure")),
		ObservedAt:           instantOf(get(o, "observedAt")),
		ClaimedBenefit:       ae.ExperienceReuseBenefit(text(get(o, "claimedBenefit"))),
		TrialLabel:           optionalText(get(o, "trialLabel")),
	}
	if human := object(get(o, "humanAssessment")); human != nil {
		feedback.HumanAssessment = &ae.HumanReuseAssessment{
			AssessmentID:            uuidOf(get(human, "assessmentId")),
			Benefit:                 ae.ExperienceReuseBenefit(text(get(human, "benefit"))),
			AttributedExperienceIDs: uuidList(get(human, "attributedExperienceIds")),
			Rationale:               text(get(human, "rationale")),
			AssessedAt:              instantOf(get(human, "assessedAt")),
			VerificationRoundID:     optionalUUID(get(human, "verificationRoundId")),
			AssessmentToken:         optionalText(get(human, "assessmentToken")),
		}
	}
	if comparative := object(get(o, "comparativeEvaluation")); comparative != nil {
		result := &ae.ComparativeEvaluationResult{
			EvaluatorID:             text(get(comparative, "evaluatorId")),
			RunID:                   uuidOf(get(comparative, "runId")),
			VerificationRoundID:     uuidOf(get(comparative, "verificationRoundId")),
			Benefit:                 ae.ExperienceReuseBenefit(text(get(comparative, "benefit"))),
			AttributedExperienceIDs: uuidList(get(comparative, "attributedExperienceIds")),
			Summary:                 text(get(comparative, "summary")),
			EvaluatedAt:             instantOf(get(comparative, "evaluatedAt")),
		}
		if evidence := get(comparative, "evidence"); evidence != nil {
			result.Evidence = []*ae.Evidence{}
			for _, item := range list(evidence) {
				if item == nil {
					result.Evidence = append(result.Evidence, nil)
					continue
				}
				piece := EvidenceOf(item)
				result.Evidence = append(result.Evidence, &piece)
			}
		}
		feedback.ComparativeEvaluation = result
	}
	return feedback
}

// FeedbackJSON writes a submission as a step names it.
func FeedbackJSON(f ae.ExperienceReuseFeedback) *ae.Object {
	var human, comparative any
	if h := f.HumanAssessment; h != nil {
		human = ae.NewObject(
			ae.Member{Key: "assessmentId", Value: h.AssessmentID.String()},
			ae.Member{Key: "benefit", Value: string(h.Benefit)},
			ae.Member{Key: "attributedExperienceIds", Value: uuidListJSON(h.AttributedExperienceIDs)},
			ae.Member{Key: "rationale", Value: h.Rationale},
			ae.Member{Key: "assessedAt", Value: h.AssessedAt.String()},
			ae.Member{Key: "verificationRoundId", Value: optionalUUIDJSON(h.VerificationRoundID)},
			ae.Member{Key: "assessmentToken", Value: optional(h.AssessmentToken)},
		)
	}
	if c := f.ComparativeEvaluation; c != nil {
		var evidence any
		if c.Evidence != nil {
			items := make([]any, len(c.Evidence))
			for i, piece := range c.Evidence {
				if piece != nil {
					items[i] = EvidenceJSON(*piece)
				}
			}
			evidence = items
		}
		comparative = ae.NewObject(
			ae.Member{Key: "evaluatorId", Value: c.EvaluatorID},
			ae.Member{Key: "runId", Value: c.RunID.String()},
			ae.Member{Key: "verificationRoundId", Value: c.VerificationRoundID.String()},
			ae.Member{Key: "benefit", Value: string(c.Benefit)},
			ae.Member{Key: "attributedExperienceIds", Value: uuidListJSON(c.AttributedExperienceIDs)},
			ae.Member{Key: "evidence", Value: evidence},
			ae.Member{Key: "summary", Value: c.Summary},
			ae.Member{Key: "evaluatedAt", Value: c.EvaluatedAt.String()},
		)
	}
	claimed := f.ClaimedBenefit
	if claimed == "" {
		claimed = ae.BenefitUnknown
	}
	return ae.NewObject(
		ae.Member{Key: "feedbackId", Value: f.FeedbackID.String()},
		ae.Member{Key: "runId", Value: f.RunID.String()},
		ae.Member{Key: "scope", Value: ScopeJSON(f.Scope)},
		ae.Member{Key: "exposedExperienceIds", Value: uuidListJSON(f.ExposedExperienceIDs)},
		ae.Member{Key: "runOutcome", Value: string(f.RunOutcome)},
		ae.Member{Key: "measure", Value: measureJSON(f.Measure)},
		ae.Member{Key: "observedAt", Value: f.ObservedAt.String()},
		ae.Member{Key: "claimedBenefit", Value: string(claimed)},
		ae.Member{Key: "humanAssessment", Value: human},
		ae.Member{Key: "comparativeEvaluation", Value: comparative},
		ae.Member{Key: "trialLabel", Value: optional(f.TrialLabel)},
	)
}

// EvidenceJSON writes a piece of verification evidence as the fixtures do.
func EvidenceJSON(e ae.Evidence) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "evidenceId", Value: e.EvidenceID.String()},
		ae.Member{Key: "verificationRoundId", Value: e.VerificationRoundID.String()},
		ae.Member{Key: "artifactRevision", Value: e.ArtifactRevision},
		ae.Member{Key: "checkId", Value: e.CheckID},
		ae.Member{Key: "kind", Value: e.Kind},
		ae.Member{Key: "result", Value: string(e.Result)},
		ae.Member{Key: "producer", Value: e.Producer},
		ae.Member{Key: "detail", Value: optional(e.Detail)},
		ae.Member{Key: "capturedAt", Value: e.CapturedAt.String()},
	)
}

// RecordedFeedbackOf reads a submission in the ledger's form (a record step's, or the ledger's).
func RecordedFeedbackOf(value any) ae.RecordedExperienceReuseFeedback {
	o := object(value)
	feedback := ae.RecordedExperienceReuseFeedback{
		FeedbackID:          uuidOf(get(o, "feedbackId")),
		RunID:               uuidOf(get(o, "runId")),
		Scope:               optionalScopeOf(get(o, "scope")),
		RunOutcome:          ae.VerificationStatus(text(get(o, "runOutcome"))),
		ClaimedBenefit:      ae.ExperienceReuseBenefit(text(get(o, "claimedBenefit"))),
		Benefit:             ae.ExperienceReuseBenefit(text(get(o, "benefit"))),
		AttributionSource:   ae.ReuseAttributionSource(text(get(o, "attributionSource"))),
		ReviewerIdentity:    optionalText(get(o, "reviewerIdentity")),
		EvaluatorID:         optionalText(get(o, "evaluatorId")),
		VerificationRoundID: optionalUUID(get(o, "verificationRoundId")),
		AssessmentID:        optionalUUID(get(o, "assessmentId")),
		Rationale:           optionalText(get(o, "rationale")),
		EvidenceIDs:         uuidList(get(o, "evidenceIds")),
		AttributedAt:        optionalInstant(get(o, "attributedAt")),
		Measure:             measureOf(get(o, "measure")),
		TrialLabel:          optionalText(get(o, "trialLabel")),
		ObservedAt:          instantOf(get(o, "observedAt")),
	}
	if exposures := get(o, "exposures"); exposures != nil {
		feedback.Exposures = []*ae.ExperienceReuseExposure{}
		for _, item := range list(exposures) {
			if item == nil {
				feedback.Exposures = append(feedback.Exposures, nil)
				continue
			}
			e := object(item)
			feedback.Exposures = append(feedback.Exposures, &ae.ExperienceReuseExposure{
				ExperienceID: uuidOf(get(e, "experienceId")),
				Attributed:   get(e, "attributed").(bool),
				EvidenceID:   optionalUUID(get(e, "evidenceId")),
			})
		}
	}
	return feedback
}

// RecordedFeedbackJSON writes a submission in the ledger's form, or null.
func RecordedFeedbackJSON(f *ae.RecordedExperienceReuseFeedback) any {
	if f == nil {
		return nil
	}
	var scope, exposures any
	if f.Scope != nil {
		scope = ScopeJSON(*f.Scope)
	}
	if f.Exposures != nil {
		items := make([]any, len(f.Exposures))
		for i, e := range f.Exposures {
			if e != nil {
				items[i] = ae.NewObject(
					ae.Member{Key: "experienceId", Value: e.ExperienceID.String()},
					ae.Member{Key: "attributed", Value: e.Attributed},
					ae.Member{Key: "evidenceId", Value: optionalUUIDJSON(e.EvidenceID)},
				)
			}
		}
		exposures = items
	}
	return ae.NewObject(
		ae.Member{Key: "feedbackId", Value: f.FeedbackID.String()},
		ae.Member{Key: "runId", Value: f.RunID.String()},
		ae.Member{Key: "scope", Value: scope},
		ae.Member{Key: "runOutcome", Value: string(f.RunOutcome)},
		ae.Member{Key: "claimedBenefit", Value: string(f.ClaimedBenefit)},
		ae.Member{Key: "benefit", Value: string(f.Benefit)},
		ae.Member{Key: "attributionSource", Value: string(f.AttributionSource)},
		ae.Member{Key: "reviewerIdentity", Value: optional(f.ReviewerIdentity)},
		ae.Member{Key: "evaluatorId", Value: optional(f.EvaluatorID)},
		ae.Member{Key: "verificationRoundId", Value: optionalUUIDJSON(f.VerificationRoundID)},
		ae.Member{Key: "assessmentId", Value: optionalUUIDJSON(f.AssessmentID)},
		ae.Member{Key: "rationale", Value: optional(f.Rationale)},
		ae.Member{Key: "evidenceIds", Value: uuidListJSON(f.EvidenceIDs)},
		ae.Member{Key: "attributedAt", Value: optionalInstantJSON(f.AttributedAt)},
		ae.Member{Key: "measure", Value: measureJSON(f.Measure)},
		ae.Member{Key: "trialLabel", Value: optional(f.TrialLabel)},
		ae.Member{Key: "observedAt", Value: f.ObservedAt.String()},
		ae.Member{Key: "exposures", Value: exposures},
	)
}

// LedgerResultJSON writes a record step's result.
func LedgerResultJSON(r ae.ReuseFeedbackStoreResult) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "feedback", Value: RecordedFeedbackJSON(r.Feedback)},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
	)
}

// LedgerThrownJSON writes a submit step whose ledger write failed: the exception .NET throws, with the message.
func LedgerThrownJSON(err error) *ae.Object {
	return ae.NewObject(ae.Member{Key: "thrown", Value: ae.NewObject(
		ae.Member{Key: "exception", Value: "ExperienceStoreException"},
		ae.Member{Key: "message", Value: err.Error()})})
}

// SubmitResultJSON writes a submit step's result.
func SubmitResultJSON(r ae.ExperienceReuseFeedbackResult) *ae.Object {
	exposures := make([]any, len(r.Exposures))
	for i, e := range r.Exposures {
		var confidence any
		if e.ReuseConfidence != nil {
			confidence = Double(*e.ReuseConfidence)
		}
		exposures[i] = ae.NewObject(
			ae.Member{Key: "experienceId", Value: e.ExperienceID.String()},
			ae.Member{Key: "disposition", Value: string(e.Disposition)},
			ae.Member{Key: "evidenceId", Value: optionalUUIDJSON(e.EvidenceID)},
			ae.Member{Key: "counted", Value: e.Counted},
			ae.Member{Key: "reuseConfidence", Value: confidence},
			ae.Member{Key: "status", Value: optionalStatusJSON(e.Status)},
			ae.Member{Key: "retryable", Value: e.Retryable},
			ae.Member{Key: "reason", Value: optional(e.Reason)},
		)
	}
	return ae.NewObject(
		ae.Member{Key: "thrown", Value: nil},
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "feedbackId", Value: r.FeedbackID.String()},
		ae.Member{Key: "benefit", Value: string(r.Benefit)},
		ae.Member{Key: "attributionSource", Value: string(r.AttributionSource)},
		ae.Member{Key: "exposures", Value: exposures},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
		ae.Member{Key: "reason", Value: optional(r.Reason)},
		ae.Member{Key: "isRetryable", Value: r.IsRetryable()},
	)
}

// FeedbackDeriveJSON writes a reuse-feedback derive step's result.
func FeedbackDeriveJSON(step any) *ae.Object {
	o := object(step)
	feedback, experience := uuidOf(get(o, "feedbackId")), uuidOf(get(o, "experienceId"))
	return ae.NewObject(
		ae.Member{Key: "evidenceId", Value: ae.FeedbackEvidenceIDFor(feedback, experience).String()},
		ae.Member{Key: "eventId", Value: ae.FeedbackEventIDFor(feedback, experience).String()})
}

// ReuseEvidenceJSON writes the reuse-evidence step's entries, each error type named as .NET names the exception it
// stands for (dotnetType maps this port's type name to .NET's, the reason's mention of it included).
func ReuseEvidenceJSON(results []ae.ReuseEvidenceResult, dotnetType func(string) string) []any {
	items := make([]any, len(results))
	for i, r := range results {
		var outcome, refusal, errorType any
		if r.Outcome != nil {
			outcome = string(*r.Outcome)
		}
		if r.Refusal != nil {
			refusal = string(*r.Refusal)
		}
		reason := optional(r.Reason)
		if r.ErrorType != nil {
			mapped := dotnetType(*r.ErrorType)
			errorType = mapped
			if r.Reason != nil {
				reason = strings.Replace(*r.Reason, *r.ErrorType, mapped, 1)
			}
		}
		items[i] = ae.NewObject(
			ae.Member{Key: "experienceId", Value: r.ExperienceID.String()},
			ae.Member{Key: "kind", Value: string(r.Kind)},
			ae.Member{Key: "outcome", Value: outcome},
			ae.Member{Key: "reason", Value: reason},
			ae.Member{Key: "counted", Value: r.Counted},
			ae.Member{Key: "skipped", Value: r.Skipped},
			ae.Member{Key: "replay", Value: r.Replay},
			ae.Member{Key: "refusal", Value: refusal},
			ae.Member{Key: "exceptionType", Value: errorType},
		)
	}
	return items
}
