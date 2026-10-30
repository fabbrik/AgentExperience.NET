package contracttest

// Contract 1.16's lifecycle fixtures as JSON: reading their requests and writing the results the way .NET's generator
// writes them. Shared by the in-memory conformance runner, the PostgreSQL leg and the cross-language race writer.

import (
	"encoding/json"
	"errors"
	"math"
	"strconv"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// LifecycleTolerance is how far apart two numbers of the lifecycle fixtures may be.
const LifecycleTolerance = 1e-12

// LifecycleOptions compare lifecycle JSON: numbers by value within LifecycleTolerance.
var LifecycleOptions = Options{Tolerance: LifecycleTolerance, IgnoreKind: true}

// ---- Reading ----------------------------------------------------------------------------------------------------

func uuidOf(value any) ae.UUID { return must(ae.ParseUUID(text(value))) }

func optionalUUID(value any) *ae.UUID {
	if value == nil {
		return nil
	}
	id := uuidOf(value)
	return &id
}

func optionalStatus(value any) *ae.ExperienceStatus {
	if value == nil {
		return nil
	}
	status := ae.ExperienceStatus(text(value))
	return &status
}

func instantOf(value any) ae.Instant { return must(ae.ParseInstant(text(value))) }

func number(value any) float64 {
	n, _ := value.(json.Number)
	f, err := strconv.ParseFloat(string(n), 64)
	if err != nil {
		panic("contracttest: " + err.Error())
	}
	return f
}

// AuthorizationOf reads a fixture's authorization: principalId, then tenantId to userId (null for unbounded).
func AuthorizationOf(value any) ae.AuthorizationContext {
	o := object(value)
	return ae.AuthorizationContext{
		PrincipalID: text(get(o, "principalId")),
		Bounds: ae.AuthorizationBounds{
			TenantID:      text(get(o, "tenantId")),
			ApplicationID: optionalText(get(o, "applicationId")),
			ProjectID:     optionalText(get(o, "projectId")),
			TeamID:        optionalText(get(o, "teamId")),
			AgentID:       optionalText(get(o, "agentId")),
			UserID:        optionalText(get(o, "userId")),
		},
	}
}

// AuthorizationJSON writes an authorization as the fixtures do.
func AuthorizationJSON(a ae.AuthorizationContext) *ae.Object {
	b := a.Bounds
	return ae.NewObject(
		ae.Member{Key: "principalId", Value: a.PrincipalID},
		ae.Member{Key: "tenantId", Value: b.TenantID},
		ae.Member{Key: "applicationId", Value: optional(b.ApplicationID)},
		ae.Member{Key: "projectId", Value: optional(b.ProjectID)},
		ae.Member{Key: "teamId", Value: optional(b.TeamID)},
		ae.Member{Key: "agentId", Value: optional(b.AgentID)},
		ae.Member{Key: "userId", Value: optional(b.UserID)},
	)
}

// CommitRequestOf reads a commit step's request.
func CommitRequestOf(value any) ae.CommitLifecycleTransitionRequest {
	o := object(value)
	return ae.CommitLifecycleTransitionRequest{
		EventID:                 uuidOf(get(o, "eventId")),
		ExperienceID:            uuidOf(get(o, "experienceId")),
		Scope:                   ScopeOf(get(o, "scope")),
		PriorStatus:             optionalStatus(get(o, "priorStatus")),
		CurrentStatus:           ae.ExperienceStatus(text(get(o, "currentStatus"))),
		Reason:                  text(get(o, "reason")),
		Producer:                text(get(o, "producer")),
		OccurredAt:              instantOf(get(o, "occurredAt")),
		ExpectedRevision:        integer(get(o, "expectedRevision")),
		ReplacementExperienceID: optionalUUID(get(o, "replacementExperienceId")),
	}
}

// ApplyRequestOf reads an apply step's request.
func ApplyRequestOf(value any) ae.ApplyConfidenceEvidenceRequest {
	o := object(value)
	return ae.ApplyConfidenceEvidenceRequest{
		EventID:             uuidOf(get(o, "eventId")),
		ExperienceID:        uuidOf(get(o, "experienceId")),
		Scope:               ScopeOf(get(o, "scope")),
		EvidenceID:          uuidOf(get(o, "evidenceId")),
		Kind:                ae.ConfidenceEvidenceKind(text(get(o, "kind"))),
		Source:              ae.ConfidenceEvidenceSource(text(get(o, "source"))),
		RunID:               uuidOf(get(o, "runId")),
		VerificationRoundID: optionalUUID(get(o, "verificationRoundId")),
		Reason:              text(get(o, "reason")),
		Producer:            text(get(o, "producer")),
		OccurredAt:          instantOf(get(o, "occurredAt")),
		Detail:              optionalText(get(o, "detail")),
		AssessmentToken:     optionalText(get(o, "assessmentToken")),
	}
}

// HistoryQueryOf reads a history step's query.
func HistoryQueryOf(value any) ae.HistoryQuery {
	o := object(value)
	query := ae.HistoryQuery{
		Scope:        ScopeOf(get(o, "scope")),
		ExperienceID: uuidOf(get(o, "experienceId")),
		Limit:        int(integer(get(o, "limit"))),
	}
	if after := get(o, "startAfterRevision"); after != nil {
		revision := integer(after)
		query.StartAfterRevision = &revision
	}
	return query
}

// UpdateOf reads a confidence update (its counted member is derived, and ignored).
func UpdateOf(value any) *ae.ConfidenceUpdate {
	o := object(value)
	if o == nil {
		return nil
	}
	update := &ae.ConfidenceUpdate{
		EvidenceID:                 uuidOf(get(o, "evidenceId")),
		Kind:                       ae.ConfidenceEvidenceKind(text(get(o, "kind"))),
		Source:                     ae.ConfidenceEvidenceSource(text(get(o, "source"))),
		RunID:                      uuidOf(get(o, "runId")),
		VerificationRoundID:        optionalUUID(get(o, "verificationRoundId")),
		ReviewerIdentity:           optionalText(get(o, "reviewerIdentity")),
		RuleVersion:                text(get(o, "ruleVersion")),
		PriorReuseConfidence:       number(get(o, "priorReuseConfidence")),
		NewReuseConfidence:         number(get(o, "newReuseConfidence")),
		PriorSupportingValidations: integer(get(o, "priorSupportingValidations")),
		NewSupportingValidations:   integer(get(o, "newSupportingValidations")),
		PriorContradictions:        integer(get(o, "priorContradictions")),
		NewContradictions:          integer(get(o, "newContradictions")),
		Detail:                     optionalText(get(o, "detail")),
		AssessmentID:               optionalUUID(get(o, "assessmentId")),
	}
	if admission := get(o, "admission"); admission != nil {
		value := ae.ConfidenceEvidenceAdmission(text(admission))
		update.Admission = &value
	}
	return update
}

// LifecycleEventOf reads an event (a lifecycle-sql commit step's).
func LifecycleEventOf(value any) ae.LifecycleEvent {
	o := object(value)
	return ae.LifecycleEvent{
		EventID:                 uuidOf(get(o, "eventId")),
		ExperienceRecordID:      uuidOf(get(o, "experienceRecordId")),
		PriorStatus:             optionalStatus(get(o, "priorStatus")),
		CurrentStatus:           ae.ExperienceStatus(text(get(o, "currentStatus"))),
		Reason:                  text(get(o, "reason")),
		Producer:                text(get(o, "producer")),
		OccurredAt:              instantOf(get(o, "occurredAt")),
		ExpectedRevision:        integer(get(o, "expectedRevision")),
		ReplacementExperienceID: optionalUUID(get(o, "replacementExperienceId")),
		Confidence:              UpdateOf(get(o, "confidence")),
	}
}

// ---- Writing ----------------------------------------------------------------------------------------------------

// Double is a double as a JSON number (NaN and the infinities as .NET's strings, which no fixture holds).
func Double(value float64) any {
	if math.IsNaN(value) || math.IsInf(value, 0) {
		return strconv.FormatFloat(value, 'g', -1, 64)
	}
	return json.Number(strconv.FormatFloat(value, 'g', -1, 64))
}

func integerJSON(value int64) json.Number { return json.Number(strconv.FormatInt(value, 10)) }

func optionalInteger(value *int64) any {
	if value == nil {
		return nil
	}
	return integerJSON(*value)
}

func optionalUUIDJSON(value *ae.UUID) any {
	if value == nil {
		return nil
	}
	return value.String()
}

func optionalStatusJSON(value *ae.ExperienceStatus) any {
	if value == nil {
		return nil
	}
	return string(*value)
}

// UpdateJSON writes a confidence update, with its assessment, admission and counted.
func UpdateJSON(u *ae.ConfidenceUpdate) any {
	if u == nil {
		return nil
	}
	var admission any
	if u.Admission != nil {
		admission = string(*u.Admission)
	}
	return ae.NewObject(
		ae.Member{Key: "evidenceId", Value: u.EvidenceID.String()},
		ae.Member{Key: "kind", Value: string(u.Kind)},
		ae.Member{Key: "source", Value: string(u.Source)},
		ae.Member{Key: "runId", Value: u.RunID.String()},
		ae.Member{Key: "verificationRoundId", Value: optionalUUIDJSON(u.VerificationRoundID)},
		ae.Member{Key: "reviewerIdentity", Value: optional(u.ReviewerIdentity)},
		ae.Member{Key: "ruleVersion", Value: u.RuleVersion},
		ae.Member{Key: "priorReuseConfidence", Value: Double(u.PriorReuseConfidence)},
		ae.Member{Key: "newReuseConfidence", Value: Double(u.NewReuseConfidence)},
		ae.Member{Key: "priorSupportingValidations", Value: integerJSON(u.PriorSupportingValidations)},
		ae.Member{Key: "newSupportingValidations", Value: integerJSON(u.NewSupportingValidations)},
		ae.Member{Key: "priorContradictions", Value: integerJSON(u.PriorContradictions)},
		ae.Member{Key: "newContradictions", Value: integerJSON(u.NewContradictions)},
		ae.Member{Key: "detail", Value: optional(u.Detail)},
		ae.Member{Key: "assessmentId", Value: optionalUUIDJSON(u.AssessmentID)},
		ae.Member{Key: "admission", Value: admission},
		ae.Member{Key: "counted", Value: u.Counted()},
	)
}

// EventJSON writes an event as the service stamped it (occurredAt untruncated).
func EventJSON(e *ae.LifecycleEvent) any {
	if e == nil {
		return nil
	}
	return ae.NewObject(
		ae.Member{Key: "eventId", Value: e.EventID.String()},
		ae.Member{Key: "experienceRecordId", Value: e.ExperienceRecordID.String()},
		ae.Member{Key: "priorStatus", Value: optionalStatusJSON(e.PriorStatus)},
		ae.Member{Key: "currentStatus", Value: string(e.CurrentStatus)},
		ae.Member{Key: "reason", Value: e.Reason},
		ae.Member{Key: "producer", Value: e.Producer},
		ae.Member{Key: "occurredAt", Value: e.OccurredAt.String()},
		ae.Member{Key: "expectedRevision", Value: integerJSON(e.ExpectedRevision)},
		ae.Member{Key: "replacementExperienceId", Value: optionalUUIDJSON(e.ReplacementExperienceID)},
		ae.Member{Key: "confidence", Value: UpdateJSON(e.Confidence)},
	)
}

// ErrorsJSON writes validation errors.
func ErrorsJSON(errors []ae.StoreValidationError) []any {
	list := make([]any, len(errors))
	for i, e := range errors {
		list[i] = ae.NewObject(ae.Member{Key: "path", Value: e.Path}, ae.Member{Key: "message", Value: e.Message})
	}
	return list
}

// CommitResultJSON writes a service commit result.
func CommitResultJSON(r ae.CommitLifecycleTransitionResult) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "event", Value: EventJSON(r.Event)},
		ae.Member{Key: "revision", Value: integerJSON(r.Revision)},
		ae.Member{Key: "currentStatus", Value: optionalStatusJSON(r.CurrentStatus)},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
		ae.Member{Key: "reason", Value: optional(r.Reason)},
	)
}

// ThrownJSON writes an error the service returned as the exception .NET throws: a *ConfidenceArgumentError as its
// exception, a *ConfidenceEngineError as InvalidOperationException, and an error hostEngine recognises as the host
// engine's own (HostEngineException, no message compared).
func ThrownJSON(err error, hostEngine func(error) bool) *ae.Object {
	var argument *ae.ConfidenceArgumentError
	var engine *ae.ConfidenceEngineError
	switch {
	case errors.As(err, &argument):
		return ae.NewObject(ae.Member{Key: "exception", Value: argument.Exception},
			ae.Member{Key: "parameter", Value: argument.Parameter}, ae.Member{Key: "message", Value: argument.Message})
	case errors.As(err, &engine):
		return ae.NewObject(ae.Member{Key: "exception", Value: "InvalidOperationException"},
			ae.Member{Key: "parameter", Value: nil}, ae.Member{Key: "message", Value: engine.Error()})
	case hostEngine != nil && hostEngine(err):
		return ae.NewObject(ae.Member{Key: "exception", Value: "HostEngineException"})
	}
	return ae.NewObject(ae.Member{Key: "exception", Value: err.Error()})
}

// ApplyResultJSON writes an apply result, or the error the call returned.
func ApplyResultJSON(r ae.ApplyConfidenceEvidenceResult, err error, hostEngine func(error) bool) *ae.Object {
	if err != nil {
		return ae.NewObject(ae.Member{Key: "thrown", Value: ThrownJSON(err, hostEngine)})
	}
	var refusal any
	if r.Refusal != nil {
		refusal = string(*r.Refusal)
	}
	return ae.NewObject(
		ae.Member{Key: "thrown", Value: nil},
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "event", Value: EventJSON(r.Event)},
		ae.Member{Key: "update", Value: UpdateJSON(r.Update)},
		ae.Member{Key: "revision", Value: integerJSON(r.Revision)},
		ae.Member{Key: "status", Value: optionalStatusJSON(r.Status)},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
		ae.Member{Key: "reason", Value: optional(r.Reason)},
		ae.Member{Key: "refusal", Value: refusal},
		ae.Member{Key: "counted", Value: r.Counted()},
	)
}

// SupersessionResultJSON writes a supersession check's result.
func SupersessionResultJSON(r ae.SupersessionCheckResult) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "replacementStatus", Value: optionalStatusJSON(r.ReplacementStatus)},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
	)
}

// EventRowJSON writes a stored event as a row of agent_experience.lifecycle_events, its scope columns scope's (the
// record's: every event is in its record's scope).
func EventRowJSON(scope ae.Scope, stored ae.StoredLifecycleEvent) *ae.Object {
	e := stored.Event
	c := e.Confidence
	if c == nil {
		c = &ae.ConfidenceUpdate{}
	}
	present := e.Confidence != nil
	pick := func(value any) any {
		if !present {
			return nil
		}
		return value
	}
	var admission any
	if c.Admission != nil {
		admission = string(*c.Admission)
	}
	return ae.NewObject(
		ae.Member{Key: "event_id", Value: e.EventID.String()},
		ae.Member{Key: "experience_id", Value: e.ExperienceRecordID.String()},
		ae.Member{Key: "tenant_id", Value: scope.TenantID},
		ae.Member{Key: "application_id", Value: scope.ApplicationID},
		ae.Member{Key: "project_id", Value: scope.ProjectID},
		ae.Member{Key: "team_id", Value: optional(scope.TeamID)},
		ae.Member{Key: "agent_id", Value: optional(scope.AgentID)},
		ae.Member{Key: "user_id", Value: optional(scope.UserID)},
		ae.Member{Key: "prior_status", Value: optionalStatusJSON(e.PriorStatus)},
		ae.Member{Key: "current_status", Value: string(e.CurrentStatus)},
		ae.Member{Key: "reason", Value: e.Reason},
		ae.Member{Key: "producer", Value: e.Producer},
		ae.Member{Key: "occurred_at", Value: e.OccurredAt.TruncateToMicroseconds().String()},
		ae.Member{Key: "recorded_at", Value: stored.RecordedAt.TruncateToMicroseconds().String()},
		ae.Member{Key: "expected_revision", Value: integerJSON(e.ExpectedRevision)},
		ae.Member{Key: "applied_revision", Value: integerJSON(stored.AppliedRevision)},
		ae.Member{Key: "replacement_experience_id", Value: optionalUUIDJSON(e.ReplacementExperienceID)},
		ae.Member{Key: "actor", Value: optional(stored.Actor)},
		ae.Member{Key: "confidence_evidence_id", Value: pick(c.EvidenceID.String())},
		ae.Member{Key: "confidence_kind", Value: pick(string(c.Kind))},
		ae.Member{Key: "confidence_source", Value: pick(string(c.Source))},
		ae.Member{Key: "confidence_run_id", Value: pick(c.RunID.String())},
		ae.Member{Key: "confidence_verification_round_id", Value: optionalUUIDJSON(c.VerificationRoundID)},
		ae.Member{Key: "confidence_reviewer_identity", Value: optional(c.ReviewerIdentity)},
		ae.Member{Key: "confidence_rule_version", Value: pick(c.RuleVersion)},
		ae.Member{Key: "confidence_detail", Value: optional(c.Detail)},
		ae.Member{Key: "prior_reuse_confidence", Value: pick(Double(c.PriorReuseConfidence))},
		ae.Member{Key: "new_reuse_confidence", Value: pick(Double(c.NewReuseConfidence))},
		ae.Member{Key: "prior_supporting_validations", Value: pick(integerJSON(c.PriorSupportingValidations))},
		ae.Member{Key: "new_supporting_validations", Value: pick(integerJSON(c.NewSupportingValidations))},
		ae.Member{Key: "prior_contradictions", Value: pick(integerJSON(c.PriorContradictions))},
		ae.Member{Key: "new_contradictions", Value: pick(integerJSON(c.NewContradictions))},
		ae.Member{Key: "confidence_assessment_id", Value: optionalUUIDJSON(c.AssessmentID)},
		ae.Member{Key: "confidence_admission", Value: admission},
	)
}

// HistoryResultJSON writes a history page, its events as rows in scope.
func HistoryResultJSON(scope ae.Scope, r ae.HistoryResult) *ae.Object {
	events := make([]any, len(r.Events))
	for i, stored := range r.Events {
		events[i] = EventRowJSON(scope, stored)
	}
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "revision", Value: integerJSON(r.Revision)},
		ae.Member{Key: "events", Value: events},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
		ae.Member{Key: "nextStartAfterRevision", Value: optionalInteger(r.NextStartAfterRevision)},
	)
}

func countsJSON(c ae.ConfidenceAdmissionCounts) *ae.Object {
	return ae.NewObject(ae.Member{Key: "supporting", Value: integerJSON(c.Supporting)},
		ae.Member{Key: "contradicting", Value: integerJSON(c.Contradicting)})
}

// ConfidenceReadJSON writes a confidence read's result.
func ConfidenceReadJSON(r ae.ConfidenceReadResult) *ae.Object {
	var report any
	if p := r.Report; p != nil {
		report = ae.NewObject(
			ae.Member{Key: "experienceId", Value: p.ExperienceID.String()},
			ae.Member{Key: "revision", Value: integerJSON(p.Revision)},
			ae.Member{Key: "status", Value: string(p.Status)},
			ae.Member{Key: "filter", Value: string(p.Filter)},
			ae.Member{Key: "reuseConfidence", Value: Double(p.ReuseConfidence)},
			ae.Member{Key: "supportingValidations", Value: integerJSON(p.SupportingValidations)},
			ae.Member{Key: "contradictions", Value: integerJSON(p.Contradictions)},
			ae.Member{Key: "storedReuseConfidence", Value: Double(p.StoredReuseConfidence)},
			ae.Member{Key: "storedSupportingValidations", Value: integerJSON(p.StoredSupportingValidations)},
			ae.Member{Key: "storedContradictions", Value: integerJSON(p.StoredContradictions)},
			ae.Member{Key: "verified", Value: countsJSON(p.Verified)},
			ae.Member{Key: "hostTrusted", Value: countsJSON(p.HostTrusted)},
			ae.Member{Key: "unrecorded", Value: countsJSON(p.Unrecorded)},
			ae.Member{Key: "origin", Value: string(p.Origin)},
			ae.Member{Key: "initial", Value: countsJSON(p.Initial)},
			ae.Member{Key: "hostTrustedRecordedOnly", Value: countsJSON(p.HostTrustedRecordedOnly)},
		)
	}
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "report", Value: report},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
	)
}

// StoreCommitResultJSON writes a store commit's result (lifecycle-sql).
func StoreCommitResultJSON(r ae.LifecycleCommitResult) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "revision", Value: integerJSON(r.Revision)},
		ae.Member{Key: "currentStatus", Value: optionalStatusJSON(r.CurrentStatus)},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
		ae.Member{Key: "appliedConfidence", Value: UpdateJSON(r.AppliedConfidence)},
	)
}

// RecordReadJSON writes a record read's result, the record in its stored form.
func RecordReadJSON(r ae.RecordReadResult) *ae.Object {
	var record any
	if r.Record != nil {
		record = r.Record.ToStored()
	}
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(r.Outcome)},
		ae.Member{Key: "record", Value: record},
		ae.Member{Key: "errors", Value: ErrorsJSON(r.Errors)},
	)
}

// DeriveJSON writes a derive step's result: the reuse evidence and event IDs.
func DeriveJSON(step any) (*ae.Object, error) {
	o := object(step)
	run, experience := uuidOf(get(o, "runId")), uuidOf(get(o, "experienceId"))
	scope, kind := ScopeOf(get(o, "scope")), ae.ConfidenceEvidenceKind(text(get(o, "kind")))
	evidence, err := ae.ReuseEvidenceIDFor(run, scope, experience, kind)
	if err != nil {
		return nil, err
	}
	event, err := ae.ReuseEventIDFor(run, scope, experience, kind)
	if err != nil {
		return nil, err
	}
	return ae.NewObject(ae.Member{Key: "evidenceId", Value: evidence.String()},
		ae.Member{Key: "eventId", Value: event.String()}), nil
}
