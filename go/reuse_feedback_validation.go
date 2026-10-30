package agentexperience

import (
	"bytes"
	"math"
	"sort"
	"strconv"
)

// The ledger's own rules (contract 1.17), exactly as .NET's ExperienceRecordValidator.ValidateReuseFeedback states
// them: structural validation of what will be written, every error by path and message in .NET's order. Both ledgers
// run it before they authorize or touch anything.

// SealedRationalePlaceholder is the rationale a .NET ledger in crypto-shredding mode stores in place of the text; a
// submission may not use it.
const SealedRationalePlaceholder = "(sealed)"

// ValidateReuseFeedback validates one submission as the ledger stores it (.NET's ValidateReuseFeedback).
func ValidateReuseFeedback(f RecordedExperienceReuseFeedback) []StoreValidationError {
	var errors validationErrors
	if f.FeedbackID.IsZero() {
		errors.add("FeedbackId", emptyGUID)
	}
	if f.RunID.IsZero() {
		errors.add("RunId", emptyGUID)
	}
	if f.Scope == nil {
		errors.add("Scope", "is required.")
	} else {
		errors.scope(*f.Scope, "Scope")
	}
	errors.requireDefined(isDefinedVerificationStatus(f.RunOutcome), "RunOutcome")
	errors.requireDefined(isDefinedBenefit(f.ClaimedBenefit), "ClaimedBenefit")
	errors.requireDefined(isDefinedBenefit(f.Benefit), "Benefit")
	errors.requireDefined(isDefinedAttributionSource(f.AttributionSource), "AttributionSource")
	if f.ObservedAt.IsZero() {
		errors.add("ObservedAt", "must be set to when the feedback was observed.")
	}
	if f.Measure == nil {
		errors.add("Measure", "is required.")
	} else {
		errors.requireNotBlank(f.Measure.Kind, "Measure.Kind")
		if math.IsNaN(f.Measure.Value) || math.IsInf(f.Measure.Value, 0) {
			errors.add("Measure.Value", "must be a finite number.")
		}
	}
	errors.requireNullOrNotBlank(f.TrialLabel, "TrialLabel")
	if f.AttributedAt != nil && f.AttributedAt.IsZero() {
		errors.add("AttributedAt", "must be set when the submission carries an attribution.")
	}
	validateReuseAttributionShape(f, &errors)
	validateReuseExposures(f, &errors)
	return errors.list()
}

func (e *validationErrors) requireNotBlankPointer(value *string, path string) {
	if value == nil {
		e.add(path, notBlankMessage)
		return
	}
	e.requireNotBlank(*value, path)
}

func (e *validationErrors) requireNullFor(isNull bool, path string) {
	if !isNull {
		e.add(path, "must be null for this attribution source.")
	}
}

func (e *validationErrors) requireEmptyFor(ids []UUID, path string) {
	if len(ids) > 0 {
		e.add(path, "must be empty for this attribution source.")
	}
}

func (e *validationErrors) requireSetFor(value *Instant, path string) {
	if value == nil || value.IsZero() {
		e.add(path, "must be set for this attribution source.")
	}
}

func validateReuseAttributionShape(f RecordedExperienceReuseFeedback, errors *validationErrors) {
	if (f.AttributionSource == AttributionNone) != (f.Benefit == BenefitUnknown) {
		errors.add("Benefit", "must be Unknown exactly when there is no attribution source, and named otherwise.")
	}
	errors.refuseReservedText(f.Rationale, "Rationale")
	if f.Rationale != nil && *f.Rationale == SealedRationalePlaceholder {
		errors.add("Rationale", "must not be exactly '"+SealedRationalePlaceholder+"', which is reserved for a sealed rationale.")
	}
	switch f.AttributionSource {
	case AttributionHumanAssessment:
		errors.requireNotBlankPointer(f.ReviewerIdentity, "ReviewerIdentity")
		errors.requireNullFor(f.EvaluatorID == nil, "EvaluatorId")
		errors.requireNotBlankPointer(f.Rationale, "Rationale")
		errors.requireSetFor(f.AttributedAt, "AttributedAt")
		errors.requireEmptyFor(f.EvidenceIDs, "EvidenceIds")
		if f.AssessmentID == nil || f.AssessmentID.IsZero() {
			errors.add("AssessmentId", "must name the host-established review a human assessment came out of.")
		}
		if f.VerificationRoundID != nil && f.VerificationRoundID.IsZero() {
			errors.add("VerificationRoundId", "must name a verification round or be null.")
		}
	case AttributionComparativeEvaluation:
		errors.requireNotBlankPointer(f.EvaluatorID, "EvaluatorId")
		errors.requireNullFor(f.ReviewerIdentity == nil, "ReviewerIdentity")
		errors.requireNullFor(f.AssessmentID == nil, "AssessmentId")
		errors.requireNotBlankPointer(f.Rationale, "Rationale")
		errors.requireSetFor(f.AttributedAt, "AttributedAt")
		if f.VerificationRoundID == nil || f.VerificationRoundID.IsZero() {
			errors.add("VerificationRoundId", "must name the verification round the comparison was made in.")
		}
		switch {
		case len(f.EvidenceIDs) == 0:
			errors.add("EvidenceIds", "must carry the evidence the comparison was reached from.")
		case containsZero(f.EvidenceIDs):
			errors.add("EvidenceIds", "must not contain an empty GUID.")
		case hasDuplicate(f.EvidenceIDs):
			errors.add("EvidenceIds", "must not name the same piece of evidence twice.")
		}
	default:
		errors.requireNullFor(f.ReviewerIdentity == nil, "ReviewerIdentity")
		errors.requireNullFor(f.EvaluatorID == nil, "EvaluatorId")
		errors.requireNullFor(f.VerificationRoundID == nil, "VerificationRoundId")
		errors.requireNullFor(f.AssessmentID == nil, "AssessmentId")
		errors.requireNullFor(f.Rationale == nil, "Rationale")
		errors.requireNullFor(f.AttributedAt == nil, "AttributedAt")
		errors.requireEmptyFor(f.EvidenceIDs, "EvidenceIds")
	}
}

func containsZero(ids []UUID) bool {
	for _, id := range ids {
		if id.IsZero() {
			return true
		}
	}
	return false
}

func hasDuplicate(ids []UUID) bool {
	seen := map[UUID]bool{}
	for _, id := range ids {
		if seen[id] {
			return true
		}
		seen[id] = true
	}
	return false
}

func validateReuseExposures(f RecordedExperienceReuseFeedback, errors *validationErrors) {
	const path = "Exposures"
	if f.Exposures == nil {
		errors.add(path, "is required.")
		return
	}
	if len(f.Exposures) == 0 {
		errors.add(path, "must name at least one exposed record.")
		return
	}
	if len(f.Exposures) > MaxExposedRecords {
		errors.add(path, "must name at most "+strconv.Itoa(MaxExposedRecords)+" records.")
	}
	seen := map[UUID]bool{}
	attributedWithoutEvidence, unattributedWithEvidence := false, false
	for _, exposure := range f.Exposures {
		if exposure == nil {
			errors.add(path, "must not contain a null exposure.")
			continue
		}
		if exposure.ExperienceID.IsZero() {
			errors.add(path+".ExperienceId", emptyGUID)
		} else if seen[exposure.ExperienceID] {
			errors.add(path, "must not name the same record twice.")
		} else {
			seen[exposure.ExperienceID] = true
		}
		if exposure.Attributed && (exposure.EvidenceID == nil || exposure.EvidenceID.IsZero()) {
			attributedWithoutEvidence = true
		}
		if !exposure.Attributed && exposure.EvidenceID != nil {
			unattributedWithEvidence = true
		}
		if exposure.Attributed && f.AttributionSource == AttributionNone {
			errors.add(path, "must not mark a record attributed when the submission carries no attribution.")
		}
	}
	if attributedWithoutEvidence {
		errors.add(path+".EvidenceId", "is required for an attributed exposure: it is the confidence submission's idempotency key.")
	}
	if unattributedWithEvidence {
		errors.add(path+".EvidenceId", "must be null for an exposure nothing attributed, which produced no confidence submission.")
	}
}

// CompareUUIDs orders two UUIDs as .NET's Guid.CompareTo does, which is the order of their lowercase text.
func CompareUUIDs(a, b UUID) int { return bytes.Compare(a[:], b[:]) }

func sortUUIDs(ids []UUID) {
	sort.SliceStable(ids, func(i, j int) bool { return CompareUUIDs(ids[i], ids[j]) < 0 })
}

// NormalizeReuseFeedback is the submission with its exposures in experience-ID order, the one order a ledger keeps
// them in (.NET's NormalizeReuseFeedback). Only for a submission ValidateReuseFeedback accepted. The exposures are a
// new slice; the submission given is unchanged.
func NormalizeReuseFeedback(f RecordedExperienceReuseFeedback) RecordedExperienceReuseFeedback {
	exposures := append([]*ExperienceReuseExposure(nil), f.Exposures...)
	sort.SliceStable(exposures, func(i, j int) bool {
		return CompareUUIDs(exposures[i].ExperienceID, exposures[j].ExperienceID) < 0
	})
	f.Exposures = exposures
	return f
}

// StoredReuseFeedback is a deep copy of a submission as a ledger stores it: its instants truncated to whole
// microseconds and no nil evidence ID list. Only for a submission ValidateReuseFeedback accepted.
func StoredReuseFeedback(f RecordedExperienceReuseFeedback) RecordedExperienceReuseFeedback {
	copied := CloneReuseFeedback(f)
	copied.ObservedAt = copied.ObservedAt.TruncateToMicroseconds()
	if copied.AttributedAt != nil {
		at := copied.AttributedAt.TruncateToMicroseconds()
		copied.AttributedAt = &at
	}
	if copied.EvidenceIDs == nil {
		copied.EvidenceIDs = []UUID{}
	}
	return copied
}

// CloneReuseFeedback is a deep copy of a submission.
func CloneReuseFeedback(f RecordedExperienceReuseFeedback) RecordedExperienceReuseFeedback {
	if f.Scope != nil {
		scope := cloneScope(*f.Scope)
		f.Scope = &scope
	}
	f.ReviewerIdentity, f.EvaluatorID, f.Rationale, f.TrialLabel = clonePointer(f.ReviewerIdentity),
		clonePointer(f.EvaluatorID), clonePointer(f.Rationale), clonePointer(f.TrialLabel)
	f.VerificationRoundID, f.AssessmentID, f.AttributedAt = clonePointer(f.VerificationRoundID),
		clonePointer(f.AssessmentID), clonePointer(f.AttributedAt)
	f.Measure = clonePointer(f.Measure)
	if f.EvidenceIDs != nil {
		f.EvidenceIDs = append([]UUID{}, f.EvidenceIDs...)
	}
	if f.Exposures != nil {
		exposures := make([]*ExperienceReuseExposure, len(f.Exposures))
		for i, exposure := range f.Exposures {
			if exposure != nil {
				copied := *exposure
				copied.EvidenceID = clonePointer(exposure.EvidenceID)
				exposures[i] = &copied
			}
		}
		f.Exposures = exposures
	}
	return f
}

func cloneScope(scope Scope) Scope {
	scope.TeamID, scope.AgentID, scope.UserID = clonePointer(scope.TeamID), clonePointer(scope.AgentID),
		clonePointer(scope.UserID)
	return scope
}

// ReuseFeedbackMatches reports whether a stored submission is the one in hand, every stored field compared as the
// .NET ledgers compare them: the instants against the submitted ones truncated to whole microseconds, the measure's
// value exactly (NaN equal to NaN), the evidence IDs and the exposures in order. A port holds no key store, so a
// rationale is always compared as stored (a .NET-sealed one, the placeholder, never equals a submitted text).
func ReuseFeedbackMatches(stored, submitted RecordedExperienceReuseFeedback) bool {
	if stored.RunID != submitted.RunID || !equalScopePointer(stored.Scope, submitted.Scope) ||
		stored.RunOutcome != submitted.RunOutcome || stored.ClaimedBenefit != submitted.ClaimedBenefit ||
		stored.Benefit != submitted.Benefit || stored.AttributionSource != submitted.AttributionSource ||
		!equalPointer(stored.ReviewerIdentity, submitted.ReviewerIdentity) ||
		!equalPointer(stored.EvaluatorID, submitted.EvaluatorID) ||
		!equalPointer(stored.VerificationRoundID, submitted.VerificationRoundID) ||
		!equalPointer(stored.AssessmentID, submitted.AssessmentID) ||
		!equalPointer(stored.TrialLabel, submitted.TrialLabel) {
		return false
	}
	if !equalPointer(stored.Rationale, submitted.Rationale) {
		return false
	}
	if len(stored.EvidenceIDs) != len(submitted.EvidenceIDs) {
		return false
	}
	for i := range stored.EvidenceIDs {
		if stored.EvidenceIDs[i] != submitted.EvidenceIDs[i] {
			return false
		}
	}
	var attributedAt *Instant
	if submitted.AttributedAt != nil {
		at := submitted.AttributedAt.TruncateToMicroseconds()
		attributedAt = &at
	}
	if !equalPointer(stored.AttributedAt, attributedAt) || stored.ObservedAt != submitted.ObservedAt.TruncateToMicroseconds() {
		return false
	}
	if (stored.Measure == nil) != (submitted.Measure == nil) {
		return false
	}
	if stored.Measure != nil {
		a, b := stored.Measure.Value, submitted.Measure.Value
		if stored.Measure.Kind != submitted.Measure.Kind || !(a == b || (math.IsNaN(a) && math.IsNaN(b))) {
			return false
		}
	}
	if len(stored.Exposures) != len(submitted.Exposures) {
		return false
	}
	for i := range stored.Exposures {
		a, b := stored.Exposures[i], submitted.Exposures[i]
		if (a == nil) != (b == nil) {
			return false
		}
		if a != nil && (a.ExperienceID != b.ExperienceID || a.Attributed != b.Attributed ||
			!equalPointer(a.EvidenceID, b.EvidenceID)) {
			return false
		}
	}
	return true
}

func equalScopePointer(a, b *Scope) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return a.Equal(*b)
}
