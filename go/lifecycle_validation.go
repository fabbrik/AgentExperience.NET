package agentexperience

import (
	"strconv"
	"strings"
	"unicode/utf8"
)

// The lifecycle's request validation, exactly as .NET's ExperienceRecordValidator states it: every error, by path and
// message, in .NET's order. The stores call these before they authorize or touch anything.

// SealedPrefix is the format sealed values begin with: free text that begins with it is refused, in both modes.
const SealedPrefix = "aexp-sealed:v1:"

const (
	notBlankMessage = "must not be empty or whitespace."
	emptyGUID       = "must not be an empty GUID."
)

type validationErrors []StoreValidationError

func (e *validationErrors) add(path, message string) {
	*e = append(*e, StoreValidationError{Path: path, Message: message})
}

func (e *validationErrors) requireNoNul(value, path string) {
	if strings.ContainsRune(value, 0) {
		e.add(path, "must not contain the NUL character (U+0000).")
	}
}

func (e *validationErrors) requireNotBlank(value, path string) {
	if isNullOrWhitespace(value) {
		e.add(path, notBlankMessage)
	} else {
		e.requireNoNul(value, path)
	}
}

func (e *validationErrors) requireNullOrNotBlank(value *string, path string) {
	if value != nil && isNullOrWhitespace(*value) {
		e.add(path, "must be null or a non-blank value.")
	} else if value != nil {
		e.requireNoNul(*value, path)
	}
}

// requireWellFormed refuses text with no UTF-8 form: in .NET a lone surrogate, which reaches Go as invalid UTF-8.
func (e *validationErrors) requireWellFormed(value *string, path string) {
	if value != nil && !utf8.ValidString(*value) {
		e.add(path, "must be well-formed UTF-16: a lone surrogate has no UTF-8 encoding.")
	}
}

func (e *validationErrors) refuseReservedText(value *string, path string) {
	if value != nil && strings.HasPrefix(*value, SealedPrefix) {
		e.add(path, "must not begin with '"+SealedPrefix+"', which is reserved for sealed values.")
	}
}

func (e *validationErrors) requireDefined(defined bool, path string) {
	if !defined {
		e.add(path, "is not a defined value.")
	}
}

func (e *validationErrors) requireUnitInterval(value float64, path string) {
	if !(value >= 0 && value <= 1) {
		e.add(path, "must be between 0 and 1 inclusive.")
	}
}

func (e *validationErrors) scope(scope Scope, path string) {
	e.requireNotBlank(scope.TenantID, path+".TenantId")
	e.requireNotBlank(scope.ApplicationID, path+".ApplicationId")
	e.requireNotBlank(scope.ProjectID, path+".ProjectId")
	e.requireNullOrNotBlank(scope.TeamID, path+".TeamId")
	e.requireNullOrNotBlank(scope.AgentID, path+".AgentId")
	e.requireNullOrNotBlank(scope.UserID, path+".UserId")
	e.requireWellFormed(&scope.TenantID, path+".TenantId")
	e.requireWellFormed(&scope.ApplicationID, path+".ApplicationId")
	e.requireWellFormed(&scope.ProjectID, path+".ProjectId")
	e.requireWellFormed(scope.TeamID, path+".TeamId")
	e.requireWellFormed(scope.AgentID, path+".AgentId")
	e.requireWellFormed(scope.UserID, path+".UserId")
}

func (e validationErrors) list() []StoreValidationError {
	if e == nil {
		return []StoreValidationError{}
	}
	return e
}

// IsDefinedStatus reports whether status is a member of ExperienceStatus.
func IsDefinedStatus(status ExperienceStatus) bool {
	for _, name := range experienceStatuses {
		if string(status) == name {
			return true
		}
	}
	return false
}

func isDefinedKind(kind ConfidenceEvidenceKind) bool {
	return kind == EvidenceSupporting || kind == EvidenceContradicting
}

func isDefinedSource(source ConfidenceEvidenceSource) bool {
	return source == EvidenceMachine || source == EvidenceHuman
}

func isDefinedAdmission(admission ConfidenceEvidenceAdmission) bool {
	return admission == AdmissionVerified || admission == AdmissionHostTrusted
}

func isDefinedFilter(filter ConfidenceEvidenceFilter) bool {
	return filter == FilterAll || filter == FilterExcludeHostTrusted || filter == FilterVerifiedOnly
}

// ValidateGet validates a scoped read of one record (.NET's ValidateGet).
func ValidateGet(scope Scope, experienceID UUID) []StoreValidationError {
	var errors validationErrors
	if experienceID.IsZero() {
		errors.add("ExperienceId", emptyGUID)
	}
	errors.scope(scope, "Scope")
	return errors.list()
}

// ValidateHistoryQuery validates one page of a history (.NET's ValidateHistoryQuery).
func ValidateHistoryQuery(query HistoryQuery) []StoreValidationError {
	var errors validationErrors
	if query.ExperienceID.IsZero() {
		errors.add("ExperienceId", emptyGUID)
	}
	errors.scope(query.Scope, "Scope")
	if query.Limit < HistoryMinLimit || query.Limit > HistoryMaxLimit {
		errors.add("Limit", "must be between "+strconv.Itoa(HistoryMinLimit)+" and "+strconv.Itoa(HistoryMaxLimit)+".")
	}
	if query.StartAfterRevision != nil && *query.StartAfterRevision < 0 {
		errors.add("StartAfterRevision", "must be null or a non-negative revision.")
	}
	return errors.list()
}

// ValidateSupersessionCheck validates a supersession check (.NET's ValidateSupersessionCheck). A record naming itself
// is not refused here: the service refuses it before any store call.
func ValidateSupersessionCheck(scope Scope, experienceID, replacementID UUID) []StoreValidationError {
	var errors validationErrors
	if experienceID.IsZero() {
		errors.add("ExperienceId", emptyGUID)
	}
	if replacementID.IsZero() {
		errors.add("ReplacementExperienceId", emptyGUID)
	}
	errors.scope(scope, "Scope")
	return errors.list()
}

// ValidateLifecycleEvent validates a lifecycle commit: the event's own fields and the request scope (.NET's
// ValidateLifecycleEvent). The authorization's principal is checked only for an event that carries a confidence
// update.
func ValidateLifecycleEvent(scope Scope, event LifecycleEvent, authorization AuthorizationContext) []StoreValidationError {
	var errors validationErrors
	if event.Confidence != nil && isNullOrWhitespace(authorization.PrincipalID) {
		errors.add("Authorization.PrincipalId", "must be non-blank for a confidence update: it is the actor the update "+
			"is recorded against, and the reviewer a human submission is counted under.")
	}
	if event.EventID.IsZero() {
		errors.add("EventId", emptyGUID)
	}
	if event.ExperienceRecordID.IsZero() {
		errors.add("ExperienceRecordId", emptyGUID)
	}
	errors.scope(scope, "Scope")
	if event.PriorStatus != nil {
		errors.requireDefined(IsDefinedStatus(*event.PriorStatus), "PriorStatus")
	}
	errors.requireDefined(IsDefinedStatus(event.CurrentStatus), "CurrentStatus")
	errors.requireNotBlank(event.Reason, "Reason")
	errors.refuseReservedText(&event.Reason, "Reason")
	errors.requireNotBlank(event.Producer, "Producer")
	if event.OccurredAt.IsZero() {
		errors.add("OccurredAt", "must be set to when the transition occurred.")
	}
	if event.ReplacementExperienceID != nil {
		replacement := *event.ReplacementExperienceID
		if event.Confidence != nil {
			errors.add("ReplacementExperienceId", "must be null on an event that carries a confidence update; "+
				"supersession and evidence are separate transitions.")
		}
		if event.CurrentStatus != StatusSuperseded {
			errors.add("ReplacementExperienceId", "must be null unless the event moves the record to Superseded.")
		}
		if replacement.IsZero() {
			errors.add("ReplacementExperienceId", emptyGUID)
		}
		if replacement == event.ExperienceRecordID {
			errors.add("ReplacementExperienceId", "must name a record other than the one being superseded.")
		}
	} else if event.CurrentStatus == StatusSuperseded {
		errors.add("ReplacementExperienceId", "is required when the event moves the record to Superseded.")
	}
	errors.confidenceUpdate(event.Confidence)
	errors.recordedOnlyEvidence(event)
	if event.ExpectedRevision < 0 {
		errors.add("ExpectedRevision", "must not be negative.")
	} else if event.ExpectedRevision >= maxInt64-1 {
		errors.add("ExpectedRevision", "must leave room for the next revision, so a later commit stays possible.")
	}
	return errors.list()
}

const maxInt64 = int64(^uint64(0) >> 1)

func (e *validationErrors) recordedOnlyEvidence(event LifecycleEvent) {
	update := event.Confidence
	if update == nil || update.Counted() {
		return
	}
	if update.Admission == nil || *update.Admission != AdmissionHostTrusted {
		e.add("Confidence.Admission", "an event whose evidence moves no counter is accepted only for HostTrusted "+
			"evidence recorded without being counted.")
	}
	if update.NewReuseConfidence != update.PriorReuseConfidence {
		e.add("Confidence.NewReuseConfidence", "must equal the prior score when the evidence moves no counter.")
	}
	if event.PriorStatus != nil && *event.PriorStatus != event.CurrentStatus {
		e.add("CurrentStatus", "must equal the prior status when the evidence moves no counter.")
	}
}

func (e *validationErrors) confidenceUpdate(update *ConfidenceUpdate) {
	if update == nil {
		return
	}
	const path = "Confidence"
	e.refuseReservedText(update.Detail, path+".Detail")
	if update.EvidenceID.IsZero() {
		e.add(path+".EvidenceId", emptyGUID)
	}
	if update.RunID.IsZero() {
		e.add(path+".RunId", "must name the run the reuse was observed in.")
	}
	e.requireDefined(isDefinedKind(update.Kind), path+".Kind")
	if update.Admission != nil {
		e.requireDefined(isDefinedAdmission(*update.Admission), path+".Admission")
	}
	e.requireNotBlank(update.RuleVersion, path+".RuleVersion")
	e.requireUnitInterval(update.PriorReuseConfidence, path+".PriorReuseConfidence")
	e.requireUnitInterval(update.NewReuseConfidence, path+".NewReuseConfidence")
	for _, counter := range []struct {
		count int64
		name  string
	}{
		{update.PriorSupportingValidations, "PriorSupportingValidations"},
		{update.NewSupportingValidations, "NewSupportingValidations"},
		{update.PriorContradictions, "PriorContradictions"},
		{update.NewContradictions, "NewContradictions"},
	} {
		if counter.count < 0 {
			e.add(path+"."+counter.name, "must not be negative.")
		}
	}
	if update.NewSupportingValidations < update.PriorSupportingValidations ||
		update.NewContradictions < update.PriorContradictions {
		e.add(path+".NewSupportingValidations", "evidence only ever moves a counter up, never down.")
	}
	if !isDefinedSource(update.Source) {
		e.add(path+".Source", "must be a defined value.")
		return
	}
	if update.Source == EvidenceMachine {
		if update.VerificationRoundID == nil || update.VerificationRoundID.IsZero() {
			e.add(path+".VerificationRoundId", "is required for Machine evidence, which is counted once per run and round.")
		}
		if update.ReviewerIdentity != nil {
			e.add(path+".ReviewerIdentity", "must be null for Machine evidence.")
		}
		if update.AssessmentID != nil {
			e.add(AssessmentIDPath, "must be null for Machine evidence.")
		}
		return
	}
	if update.ReviewerIdentity == nil || isNullOrWhitespace(*update.ReviewerIdentity) {
		e.add(path+".ReviewerIdentity", "is required for Human evidence, which is counted once per reviewer and run.")
	}
	if update.VerificationRoundID != nil {
		e.add(path+".VerificationRoundId", "must be null for Human evidence.")
	}
	if update.AssessmentID != nil && update.AssessmentID.IsZero() {
		e.add(AssessmentIDPath, "must be null or name an assessment, never an empty GUID.")
	}
}

// IsSameEvidence reports whether two confidence payloads are the same piece of evidence: the same evidence ID.
func IsSameEvidence(submitted, stored *ConfidenceUpdate) bool {
	return submitted != nil && stored != nil && submitted.EvidenceID == stored.EvidenceID
}

// AsReplayOf is submitted as an event-ID replay compares it with stored: the admission is the stored one, and for the
// same evidence so are the counters and the score.
func AsReplayOf(submitted ConfidenceUpdate, stored *ConfidenceUpdate) ConfidenceUpdate {
	replay := submitted.Clone()
	if stored == nil {
		replay.Admission = nil
		return replay
	}
	replay.Admission = clonePointer(stored.Admission)
	if submitted.EvidenceID != stored.EvidenceID {
		return replay
	}
	replay.PriorReuseConfidence, replay.NewReuseConfidence = stored.PriorReuseConfidence, stored.NewReuseConfidence
	replay.PriorSupportingValidations = stored.PriorSupportingValidations
	replay.NewSupportingValidations = stored.NewSupportingValidations
	replay.PriorContradictions, replay.NewContradictions = stored.PriorContradictions, stored.NewContradictions
	return replay
}

// StoredIndependenceKey is the key a store deduplicates an update on, exactly as .NET's stores (and the PostgreSQL
// ledger's generated column) spell it: machine:{run}:{round}, or human:{reviewer}:{run} with the reviewer verbatim.
// It never refuses: a missing round or reviewer is spelled empty.
func StoredIndependenceKey(update ConfidenceUpdate) string {
	if update.Source == EvidenceMachine {
		round := ""
		if update.VerificationRoundID != nil {
			round = update.VerificationRoundID.String()
		}
		return "machine:" + update.RunID.String() + ":" + round
	}
	reviewer := ""
	if update.ReviewerIdentity != nil {
		reviewer = *update.ReviewerIdentity
	}
	return "human:" + reviewer + ":" + update.RunID.String()
}

// ReplayedEvent is the event a resubmission of event is compared with stored as: its confidence update as
// AsReplayOf makes it, and for the same evidence the stored current status. Equal to stored (Equal) is the original
// commit replayed; anything else a conflict.
func ReplayedEvent(event, stored LifecycleEvent) LifecycleEvent {
	resubmitted := event.Clone()
	if event.Confidence != nil {
		replay := AsReplayOf(*event.Confidence, stored.Confidence)
		resubmitted.Confidence = &replay
	}
	if IsSameEvidence(event.Confidence, stored.Confidence) {
		resubmitted.CurrentStatus = stored.CurrentStatus
	}
	return resubmitted
}

// SameStoredEvidence reports whether a resubmitted evidence ID is the stored evidence replayed, as .NET's stores
// compare it: the same record, the event it rode in on (when the stored row has one), and the same kind, source, run,
// round, reviewer, rule version, detail and assessment. The counters and the score are not compared.
func SameStoredEvidence(event LifecycleEvent, submitted ConfidenceUpdate, storedRecord UUID, storedEvent *UUID,
	stored ConfidenceUpdate) bool {
	return storedRecord == event.ExperienceRecordID &&
		(storedEvent == nil || *storedEvent == event.EventID) &&
		stored.Kind == submitted.Kind && stored.Source == submitted.Source && stored.RunID == submitted.RunID &&
		equalPointer(stored.VerificationRoundID, submitted.VerificationRoundID) &&
		equalPointer(stored.ReviewerIdentity, submitted.ReviewerIdentity) &&
		stored.RuleVersion == submitted.RuleVersion && equalPointer(stored.Detail, submitted.Detail) &&
		equalPointer(stored.AssessmentID, submitted.AssessmentID)
}

// EvidenceReplay is submitted as the evidence ledger replays it: with the stored numbers and admission.
func EvidenceReplay(submitted, stored ConfidenceUpdate) ConfidenceUpdate {
	replay := submitted.Clone()
	replay.PriorReuseConfidence, replay.NewReuseConfidence = stored.PriorReuseConfidence, stored.NewReuseConfidence
	replay.PriorSupportingValidations = stored.PriorSupportingValidations
	replay.NewSupportingValidations = stored.NewSupportingValidations
	replay.PriorContradictions, replay.NewContradictions = stored.PriorContradictions, stored.NewContradictions
	replay.Admission = clonePointer(stored.Admission)
	return replay
}
