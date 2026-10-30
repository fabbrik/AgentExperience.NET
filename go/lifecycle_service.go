package agentexperience

import (
	"context"
	"errors"
	"strings"
)

// The lifecycle service (contract 1.16): .NET's ExperienceLifecycleService, run as a port runs it. Independence is
// not verified (.NET's IndependenceVerification.TrustHostSuppliedIdentifiers): only the own-run rule is checked, an
// assessment token is neither checked nor spent, and every update is admitted HostTrusted. There is no provenance
// signing (a record's initial counters are "verified" exactly when it was finalized), no indexing service (no
// embedding is removed when a record leaves eligibility) and no capture service lookup.

// LifecycleOptions configure a LifecycleService. The zero value is the default engine, with evidence recorded only:
// until the port verifies independence (story 27.7), everything it admits is host-trusted, and by default such evidence
// moves no counter, score or status.
type LifecycleOptions struct {
	// ConfidenceEngine scores evidence; nil is the heuristic (ReuseConfidenceHeuristicEngine).
	ConfidenceEngine ConfidenceEngine
	// HostTrustedEvidence is what host-trusted evidence (everything a port admits) does to its record: RecordedOnly
	// (the default, "" too), which commits it on an event that moves the revision and nothing else, or Counted, an
	// explicit opt-in that moves the counters, the score and the status as .NET does under
	// IndependenceVerification.TrustHostSuppliedIdentifiers with HostTrustedEvidenceEffect.Counted.
	HostTrustedEvidence HostTrustedEvidenceEffect
}

// LifecycleService decides lifecycle transitions and confidence updates and commits them through a LifecycleStore.
// It is safe for concurrent use when its store and engine are.
type LifecycleService struct {
	store        LifecycleStore
	rule         *ConfidenceEngineRule
	recordedOnly bool
}

// The lifecycle telemetry operations and their attributes.
const (
	OperationLifecycleCommit = "lifecycle.commit"
	OperationConfidenceApply = "confidence.apply"
)

// NewLifecycleService is a service over store. An undefined HostTrustedEvidence is refused (naming "independence",
// as .NET names its options), then the engine's identity is checked as NewConfidenceEngineRule checks it (naming
// ConfidenceEngineParameter); both are *ConfidenceArgumentError.
func NewLifecycleService(store LifecycleStore, options LifecycleOptions) (*LifecycleService, error) {
	if store == nil {
		return nil, &ConfidenceArgumentError{Exception: "ArgumentNullException", Parameter: "store",
			Message: "Value cannot be null."}
	}
	switch options.HostTrustedEvidence {
	case "", HostTrustedCounted, HostTrustedRecordedOnly:
	default:
		return nil, argumentError("independence", "ExperienceIndependenceOptions.HostTrustedEvidence must be a defined effect.")
	}
	rule, err := NewConfidenceEngineRule(options.ConfidenceEngine, ConfidenceEngineParameter)
	if err != nil {
		return nil, err
	}
	return &LifecycleService{store: store, rule: rule, recordedOnly: options.HostTrustedEvidence != HostTrustedCounted}, nil
}

// IndependenceVerification is the mode this service runs in: always TrustHostSuppliedIdentifiers (contract 1.16).
func (s *LifecycleService) IndependenceVerification() IndependenceVerification {
	return IndependenceTrustHostSuppliedIdentifiers
}

// ConfidenceRule is the service's checked engine.
func (s *LifecycleService) ConfidenceRule() *ConfidenceEngineRule { return s.rule }

// IsTransitionAllowed reports whether the lifecycle allows moving a record from prior to current: Candidate to
// Validated or Quarantined; Validated to Reinforced; Validated or Reinforced to Contested, Stale or Superseded; and
// any status but Revoked to Revoked. A move to the same status, or an undefined status, is never allowed.
func IsTransitionAllowed(prior, current ExperienceStatus) bool {
	if !IsDefinedStatus(prior) || !IsDefinedStatus(current) || prior == current {
		return false
	}
	switch {
	case prior == StatusCandidate && (current == StatusValidated || current == StatusQuarantined):
		return true
	case prior == StatusValidated && current == StatusReinforced:
		return true
	case (prior == StatusValidated || prior == StatusReinforced) &&
		(current == StatusContested || current == StatusStale || current == StatusSuperseded):
		return true
	}
	return current == StatusRevoked && prior != StatusRevoked
}

// IsEligible reports whether a record in status may be retrieved, injected or indexed: Validated or Reinforced.
func IsEligible(status ExperienceStatus) bool {
	return status == StatusValidated || status == StatusReinforced
}

func refused(outcome LifecycleTransitionOutcome, reason string) CommitLifecycleTransitionResult {
	return CommitLifecycleTransitionResult{Outcome: outcome, Errors: []StoreValidationError{}, Reason: &reason}
}

// Commit validates the requested transition, stamps its event and commits it through the store (.NET's CommitAsync).
// The store's outcome is surfaced one-to-one; an error is the store's infrastructure failure (or the context's), or
// ErrStoreContract for an outcome a commit cannot produce. It is reported as the lifecycle.commit operation.
func (s *LifecycleService) Commit(ctx context.Context, authorization AuthorizationContext,
	request CommitLifecycleTransitionRequest) (CommitLifecycleTransitionResult, error) {
	ctx, op := Observe(ctx, OperationLifecycleCommit, false)
	defer op.End()
	op.Set(AttributeExperienceID, request.ExperienceID.String())
	op.Set(AttributeEventID, request.EventID.String())
	result, err := s.commit(ctx, authorization, request)
	if err != nil {
		op.Fail(err)
		return CommitLifecycleTransitionResult{}, err
	}
	op.Done(string(result.Outcome))
	return result, nil
}

func (s *LifecycleService) commit(ctx context.Context, authorization AuthorizationContext,
	request CommitLifecycleTransitionRequest) (CommitLifecycleTransitionResult, error) {
	// Only a well-formed status can be judged here; an undefined one is the store's to refuse as Invalid.
	if IsDefinedStatus(request.CurrentStatus) {
		if request.PriorStatus != nil {
			prior := *request.PriorStatus
			if IsDefinedStatus(prior) && !IsTransitionAllowed(prior, request.CurrentStatus) {
				if prior == request.CurrentStatus {
					return refused(TransitionNotAllowed, "A record is already "+string(request.CurrentStatus)+
						"; an event whose prior and current status are the same records no transition."), nil
				}
				return refused(TransitionNotAllowed, "Moving a record from "+string(prior)+" to "+
					string(request.CurrentStatus)+" is not an allowed transition."), nil
			}
		} else if request.CurrentStatus != FirstEventStatus {
			return refused(TransitionNotAllowed, "A record's first event has no prior status, so it may only record "+
				"the record as "+string(FirstEventStatus)+"; moving it to "+string(request.CurrentStatus)+
				" needs the status it is moving from."), nil
		}
	}
	if refusal := replacementShapeRefusal(request); refusal != nil {
		return *refusal, nil
	}
	event := LifecycleEvent{
		EventID:                 request.EventID,
		ExperienceRecordID:      request.ExperienceID,
		PriorStatus:             clonePointer(request.PriorStatus),
		CurrentStatus:           request.CurrentStatus,
		Reason:                  request.Reason,
		Producer:                request.Producer,
		OccurredAt:              request.OccurredAt,
		ExpectedRevision:        request.ExpectedRevision,
		ReplacementExperienceID: clonePointer(request.ReplacementExperienceID),
	}
	result, err := s.store.CommitLifecycleEvent(ctx, authorization, request.Scope, event.Clone())
	if err != nil {
		return CommitLifecycleTransitionResult{}, err
	}
	outcome, err := toTransitionOutcome(result.Outcome)
	if err != nil {
		return CommitLifecycleTransitionResult{}, err
	}
	var reason *string
	if outcome == TransitionReplacementNotAllowed {
		text := replacementRefusalReason(result.CurrentStatus)
		reason = &text
	}
	return CommitLifecycleTransitionResult{Outcome: outcome, Event: &event, Revision: result.Revision,
		CurrentStatus: result.CurrentStatus, Errors: nonNilErrors(result.Errors), Reason: reason}, nil
}

func nonNilErrors(errors []StoreValidationError) []StoreValidationError {
	if errors == nil {
		return []StoreValidationError{}
	}
	return errors
}

func replacementShapeRefusal(request CommitLifecycleTransitionRequest) *CommitLifecycleTransitionResult {
	var result CommitLifecycleTransitionResult
	switch {
	case request.CurrentStatus != StatusSuperseded:
		if request.ReplacementExperienceID == nil {
			return nil
		}
		result = refused(TransitionReplacementNotAllowed, "Only a transition to Superseded names a replacement; this "+
			"one moves the record to "+string(request.CurrentStatus)+".")
	case request.ReplacementExperienceID == nil || request.ReplacementExperienceID.IsZero():
		result = refused(TransitionReplacementNotAllowed, "A transition to Superseded must name the record that "+
			"replaces this one.")
	case *request.ReplacementExperienceID == request.ExperienceID:
		result = refused(TransitionReplacementNotAllowed, "A record cannot replace itself.")
	default:
		return nil
	}
	return &result
}

func replacementRefusalReason(status *ExperienceStatus) string {
	switch {
	case status == nil:
		return "The named replacement does not exist within the record's exact scope."
	case !IsEligible(*status):
		return "The named replacement is " + string(*status) + ", so it is not currently eligible for reuse and " +
			"cannot replace anything."
	}
	return "The named replacement is already replaced by this record, directly or transitively, so superseding " +
		"would close a cycle."
}

func toTransitionOutcome(outcome ExperienceStoreOutcome) (LifecycleTransitionOutcome, error) {
	switch outcome {
	case StoreCommitted, StoreStaleRevision, StoreStatusMismatch, StoreReplacementNotAllowed, StoreConflict,
		StoreNotFound, StoreDenied, StoreInvalid, StoreDeleted:
		return LifecycleTransitionOutcome(outcome), nil
	}
	return "", storeContractError("The Experience Record store returned '%s', which is not a lifecycle commit outcome.",
		outcome)
}

func toConfidenceOutcome(outcome ExperienceStoreOutcome) (ConfidenceUpdateOutcome, error) {
	switch outcome {
	case StoreCommitted:
		return ConfidenceApplied, nil
	case StoreStaleRevision, StoreStatusMismatch, StoreConflict, StoreNotFound, StoreDenied, StoreInvalid, StoreDeleted:
		return ConfidenceUpdateOutcome(outcome), nil
	}
	return "", storeContractError("The Experience Record store returned '%s', which is not a confidence-update outcome.",
		outcome)
}

// ApplyEvidence applies one piece of reuse evidence (.NET's ApplyEvidenceAsync): it reads the record, computes the new
// counters and score with the service's engine, and commits the evidence, the counters, the score, any status change
// and the event in one store transaction. A duplicate observation under a new evidence ID is Applied and not
// counted; a resubmitted evidence ID with the same content reports the original. The engine's error is returned
// unchanged, and a value that is not a score is a *ConfidenceEngineError; nothing is written either way. It is
// reported as the confidence.apply operation.
func (s *LifecycleService) ApplyEvidence(ctx context.Context, authorization AuthorizationContext,
	request ApplyConfidenceEvidenceRequest) (ApplyConfidenceEvidenceResult, error) {
	return s.applyEvidenceObserved(ctx, authorization, request, false)
}

// applyEvidenceObserved is ApplyEvidence reported with nested set: true when reuse_feedback runs it (as .NET reports
// the confidence.apply a feedback submission makes), false for a host's own call.
func (s *LifecycleService) applyEvidenceObserved(ctx context.Context, authorization AuthorizationContext,
	request ApplyConfidenceEvidenceRequest, nested bool) (ApplyConfidenceEvidenceResult, error) {
	ctx, op := Observe(ctx, OperationConfidenceApply, nested)
	defer op.End()
	op.Set(AttributeExperienceID, request.ExperienceID.String())
	op.Set(AttributeEventID, request.EventID.String())
	result, err := s.applyEvidence(ctx, authorization, request)
	if err != nil {
		op.Fail(err)
		return ApplyConfidenceEvidenceResult{}, err
	}
	if result.Outcome == ConfidenceApplied && result.Update != nil && result.Update.Admission != nil {
		op.Set(AttributeConfidenceAdmission, string(*result.Update.Admission))
	}
	if result.Refusal != nil {
		op.Set(AttributeIndependenceRefusal, string(*result.Refusal))
	}
	op.Done(string(result.Outcome))
	return result, nil
}

func (s *LifecycleService) applyEvidence(ctx context.Context, authorization AuthorizationContext,
	request ApplyConfidenceEvidenceRequest) (ApplyConfidenceEvidenceResult, error) {
	if shape := evidenceShapeErrors(request, authorization.PrincipalID); len(shape) > 0 {
		return ApplyConfidenceEvidenceResult{Outcome: ConfidenceInvalid, Errors: shape}, nil
	}
	read, err := s.store.ReadRecord(ctx, authorization, request.Scope, request.ExperienceID)
	if err != nil {
		return ApplyConfidenceEvidenceResult{}, err
	}
	if read.Outcome != StoreFound {
		outcome, err := toConfidenceOutcome(read.Outcome)
		if err != nil {
			return ApplyConfidenceEvidenceResult{}, err
		}
		return ApplyConfidenceEvidenceResult{Outcome: outcome, Errors: nonNilErrors(read.Errors)}, nil
	}
	if read.Record == nil {
		reason := "The store reported the record as found but returned nothing to compute against."
		return ApplyConfidenceEvidenceResult{Outcome: ConfidenceNotFound, Errors: []StoreValidationError{},
			Reason: &reason}, nil
	}
	record := *read.Record
	status := record.Status
	if !AcceptsEvidenceIn(record.Status) {
		names := make([]string, 0, 3)
		for _, accepted := range AcceptsEvidence() {
			names = append(names, string(accepted))
		}
		reason := "A " + string(record.Status) + " record does not accept confidence evidence; only " +
			strings.Join(names, ", ") + " do, and no score may change that."
		return ApplyConfidenceEvidenceResult{Outcome: ConfidenceIneligible, Revision: record.Revision, Status: &status,
			Errors: []StoreValidationError{}, Reason: &reason}, nil
	}
	if !s.recordedOnly {
		if counters := storedCounterErrors(record); len(counters) > 0 {
			reason := "The stored record's evidence counters cannot have evidence applied to them."
			return ApplyConfidenceEvidenceResult{Outcome: ConfidenceInvalid, Revision: record.Revision, Status: &status,
				Errors: counters, Reason: &reason}, nil
		}
	}
	// The own-run rule, in every mode; under the opt-out nothing else is verified, and evidence is host-trusted.
	if request.RunID == record.SourceRunID {
		reason := "The evidence names the record's own source run; a lesson is not reused in the run it came from."
		refusal := RefusalOwnRun
		return ApplyConfidenceEvidenceResult{Outcome: ConfidenceUnverified, Revision: record.Revision, Status: &status,
			Errors: []StoreValidationError{}, Reason: &reason, Refusal: &refusal}, nil
	}
	admission := AdmissionHostTrusted
	recordedOnly := s.recordedOnly
	var reviewer *string
	if request.Source == EvidenceHuman {
		principal := authorization.PrincipalID
		reviewer = &principal
	}
	var update ConfidenceUpdate
	if recordedOnly {
		update = ConfidenceUpdate{
			EvidenceID:                 request.EvidenceID,
			Kind:                       request.Kind,
			Source:                     request.Source,
			RunID:                      request.RunID,
			VerificationRoundID:        clonePointer(request.VerificationRoundID),
			ReviewerIdentity:           reviewer,
			RuleVersion:                s.rule.Recorded(),
			PriorReuseConfidence:       record.ReuseConfidence,
			NewReuseConfidence:         record.ReuseConfidence,
			PriorSupportingValidations: record.SupportingValidations,
			NewSupportingValidations:   record.SupportingValidations,
			PriorContradictions:        record.Contradictions,
			NewContradictions:          record.Contradictions,
			Detail:                     clonePointer(request.Detail),
		}
	} else {
		update, err = s.rule.Apply(record, ConfidenceEvidence{
			EvidenceID:          request.EvidenceID,
			Kind:                request.Kind,
			Source:              request.Source,
			RunID:               request.RunID,
			VerificationRoundID: clonePointer(request.VerificationRoundID),
			ReviewerIdentity:    reviewer,
			Detail:              clonePointer(request.Detail),
		})
		if err != nil {
			return ApplyConfidenceEvidenceResult{}, err
		}
	}
	update.AssessmentID = nil
	update.Admission = &admission
	currentStatus := record.Status
	if !recordedOnly {
		currentStatus = StatusAfter(record.Status, request.Kind)
	}
	prior := record.Status
	event := LifecycleEvent{
		EventID:            request.EventID,
		ExperienceRecordID: request.ExperienceID,
		PriorStatus:        &prior,
		CurrentStatus:      currentStatus,
		Reason:             request.Reason,
		Producer:           request.Producer,
		OccurredAt:         request.OccurredAt,
		ExpectedRevision:   record.Revision,
		Confidence:         &update,
	}
	result, err := s.store.CommitLifecycleEvent(ctx, authorization, request.Scope, event.Clone())
	if err != nil {
		return ApplyConfidenceEvidenceResult{}, err
	}
	outcome, err := toConfidenceOutcome(result.Outcome)
	if err != nil {
		return ApplyConfidenceEvidenceResult{}, err
	}
	if outcome == ConfidenceConflict && update.AssessmentID != nil {
		for _, refusedField := range result.Errors {
			if refusedField.Path == AssessmentIDPath {
				reason := "The assessment token has already landed evidence for this record under another evidence ID."
				refusal := RefusalAssessmentTokenReplayed
				return ApplyConfidenceEvidenceResult{Outcome: ConfidenceUnverified, Revision: record.Revision,
					Status: &status, Errors: []StoreValidationError{}, Reason: &reason, Refusal: &refusal}, nil
			}
		}
	}
	if outcome != ConfidenceApplied {
		return ApplyConfidenceEvidenceResult{Outcome: outcome, Event: &event, Revision: result.Revision,
			Status: result.CurrentStatus, Errors: nonNilErrors(result.Errors)}, nil
	}
	settled := currentStatus
	if result.CurrentStatus != nil {
		settled = *result.CurrentStatus
	}
	var stored ConfidenceUpdate
	if result.AppliedConfidence != nil {
		stored = result.AppliedConfidence.Clone()
	} else {
		stored = update.AsRecordedOnly()
	}
	// A store reports no status exactly when an event moved the record (this call's, or the one an event-ID replay
	// reports); an uncounted update on such an event is evidence recorded only, not a duplicate.
	return ApplyConfidenceEvidenceResult{Outcome: ConfidenceApplied, Event: &event, Update: &stored,
		Revision: result.Revision, Status: &settled, Errors: nonNilErrors(result.Errors),
		RecordedOnEvent: !stored.Counted() && result.CurrentStatus == nil}, nil
}

func storedCounterErrors(record ExperienceRecord) []StoreValidationError {
	var errors validationErrors
	for _, counter := range []struct {
		count int64
		path  string
	}{{record.SupportingValidations, "SupportingValidations"}, {record.Contradictions, "Contradictions"}} {
		if counter.count < 0 {
			errors.add(counter.path, "the stored record reports a negative evidence counter.")
		} else if counter.count >= MaxEvidenceCounter {
			errors.add(counter.path, "the stored record's evidence counter is already at its maximum, so no further "+
				"evidence can be counted.")
		}
	}
	return errors
}

func evidenceShapeErrors(request ApplyConfidenceEvidenceRequest, principal string) []StoreValidationError {
	const principalPath = "Authorization.PrincipalId"
	var errors validationErrors
	if request.EventID.IsZero() {
		errors.add("EventId", emptyGUID)
	}
	if request.ExperienceID.IsZero() {
		errors.add("ExperienceId", emptyGUID)
	}
	if request.EvidenceID.IsZero() {
		errors.add("EvidenceId", emptyGUID)
	}
	if request.RunID.IsZero() {
		errors.add("RunId", "must name the run the reuse was observed in.")
	}
	if !isDefinedKind(request.Kind) {
		errors.add("Kind", "must be a defined evidence kind.")
	}
	switch {
	case !isDefinedSource(request.Source):
		errors.add("Source", "must be a defined evidence source.")
	case request.Source == EvidenceMachine:
		if request.VerificationRoundID == nil || request.VerificationRoundID.IsZero() {
			errors.add("VerificationRoundId", "is required for Machine evidence, which is counted once per run and round.")
		}
		if request.AssessmentToken != nil {
			errors.add("AssessmentToken", "must be null for Machine evidence, which is verified against the round its "+
				"run was finalized with.")
		}
	default:
		if request.VerificationRoundID != nil {
			errors.add("VerificationRoundId", "must be null for Human evidence, which is counted once per reviewer and run.")
		}
		if isNullOrWhitespace(principal) {
			errors.add(principalPath, "must be non-blank for Human evidence: it is the reviewer the submission is "+
				"counted under.")
		} else if padded(principal) {
			errors.add(principalPath, "must not have leading or trailing whitespace: it would be counted as a second, "+
				"independent reviewer.")
		}
	}
	if isNullOrWhitespace(request.Reason) {
		errors.add("Reason", "must be a non-blank, auditable reason.")
	}
	if isNullOrWhitespace(request.Producer) {
		errors.add("Producer", "must be a non-blank producer identity.")
	}
	if request.OccurredAt.IsZero() {
		errors.add("OccurredAt", "must be set to when the observation was made.")
	}
	return errors
}

// ReadConfidence reads a record's reuse confidence, optionally leaving out evidence the verification opt-out admitted
// or everything not known to have been verified (.NET's ReadConfidenceAsync). It pages the record's history up to the
// revision it read, counts counted updates by admission, and recomputes the score with the service's engine from the
// stored counters less the excluded ones (adding, for FilterAll, host-trusted evidence recorded only, once per
// independence key no counted update holds). It writes nothing. An engine's error is returned unchanged; a history
// cursor that does not move forward is ErrStoreContract.
func (s *LifecycleService) ReadConfidence(ctx context.Context, authorization AuthorizationContext, scope Scope,
	experienceID UUID, filter ConfidenceEvidenceFilter) (ConfidenceReadResult, error) {
	if experienceID.IsZero() || !isDefinedFilter(filter) {
		var errors validationErrors
		if experienceID.IsZero() {
			errors.add("experienceId", emptyGUID)
		}
		if !isDefinedFilter(filter) {
			errors.add("filter", "must be a defined ConfidenceEvidenceFilter member.")
		}
		return ConfidenceReadResult{Outcome: StoreInvalid, Errors: errors}, nil
	}
	read, err := s.store.ReadRecord(ctx, authorization, scope, experienceID)
	if err != nil {
		return ConfidenceReadResult{}, err
	}
	if read.Outcome != StoreFound || read.Record == nil {
		outcome := read.Outcome
		if outcome == StoreFound {
			outcome = StoreNotFound
		}
		return ConfidenceReadResult{Outcome: outcome, Errors: nonNilErrors(read.Errors)}, nil
	}
	record := *read.Record

	var verified, trusted, unrecorded ConfidenceAdmissionCounts
	recordedOnlyKeys := map[string]ConfidenceEvidenceKind{}
	var recordedOnlyOrder []string
	countedKeys := map[string]bool{}
	var unkeyed ConfidenceAdmissionCounts
	var after *int64
	for {
		page, err := s.store.GetHistory(ctx, authorization, HistoryQuery{Scope: scope, ExperienceID: experienceID,
			Limit: HistoryMaxLimit, StartAfterRevision: after})
		if err != nil {
			return ConfidenceReadResult{}, err
		}
		if page.Outcome != StoreFound {
			return ConfidenceReadResult{Outcome: page.Outcome, Errors: nonNilErrors(page.Errors)}, nil
		}
		for _, stored := range page.Events {
			update := stored.Event.Confidence
			if stored.AppliedRevision > record.Revision || update == nil {
				continue
			}
			if !update.Counted() {
				if update.Admission != nil && *update.Admission == AdmissionHostTrusted {
					if key, ok := readIndependenceKey(*update); ok {
						if _, seen := recordedOnlyKeys[key]; !seen {
							recordedOnlyKeys[key] = update.Kind
							recordedOnlyOrder = append(recordedOnlyOrder, key)
						}
					} else if update.Kind == EvidenceSupporting {
						unkeyed.Supporting++
					} else {
						unkeyed.Contradicting++
					}
				}
				continue
			}
			if key, ok := readIndependenceKey(*update); ok {
				countedKeys[key] = true
			}
			supporting := update.NewSupportingValidations - update.PriorSupportingValidations
			contradicting := update.NewContradictions - update.PriorContradictions
			switch {
			case update.Admission != nil && *update.Admission == AdmissionVerified:
				verified.Supporting += supporting
				verified.Contradicting += contradicting
			case update.Admission != nil && *update.Admission == AdmissionHostTrusted:
				trusted.Supporting += supporting
				trusted.Contradicting += contradicting
			default:
				unrecorded.Supporting += supporting
				unrecorded.Contradicting += contradicting
			}
		}
		if page.NextStartAfterRevision == nil {
			break
		}
		next := *page.NextStartAfterRevision
		if after != nil && next <= *after {
			return ConfidenceReadResult{}, storeContractError(
				"The store's history cursor did not move forward, so the history cannot be read to its end.")
		}
		after = &next
	}

	recordedOnly := unkeyed
	for _, key := range recordedOnlyOrder {
		if countedKeys[key] {
			continue
		}
		if recordedOnlyKeys[key] == EvidenceSupporting {
			recordedOnly.Supporting++
		} else {
			recordedOnly.Contradicting++
		}
	}

	initial := ConfidenceAdmissionCounts{
		Supporting:    max(0, record.SupportingValidations-verified.Supporting-trusted.Supporting-unrecorded.Supporting),
		Contradicting: max(0, record.Contradictions-verified.Contradicting-trusted.Contradicting-unrecorded.Contradicting),
	}
	origin := record.Origin
	if origin == "" {
		origin = OriginHostWritten
	}
	// No provenance signing: a finalized record's initial counters are verified, anything else's are its writer's.
	var unverifiedInitial ConfidenceAdmissionCounts
	if origin != OriginFinalized {
		unverifiedInitial = initial
	}
	var excluded ConfidenceAdmissionCounts
	switch filter {
	case FilterExcludeHostTrusted:
		excluded = trusted
	case FilterVerifiedOnly:
		excluded = ConfidenceAdmissionCounts{
			Supporting:    trusted.Supporting + unrecorded.Supporting + unverifiedInitial.Supporting,
			Contradicting: trusted.Contradicting + unrecorded.Contradicting + unverifiedInitial.Contradicting,
		}
	}
	var added ConfidenceAdmissionCounts
	if filter == FilterAll {
		added = recordedOnly
	}
	supportingCounted := saturatingAdd(max(0, record.SupportingValidations-excluded.Supporting), added.Supporting)
	contradictionsCounted := saturatingAdd(max(0, record.Contradictions-excluded.Contradicting), added.Contradicting)
	adjusted := excluded != (ConfidenceAdmissionCounts{}) || added != (ConfidenceAdmissionCounts{})
	score := record.ReuseConfidence
	if adjusted {
		if score, err = s.rule.Score(record, supportingCounted, contradictionsCounted); err != nil {
			return ConfidenceReadResult{}, err
		}
	}
	return ConfidenceReadResult{Outcome: StoreFound, Errors: []StoreValidationError{}, Report: &ConfidenceReport{
		ExperienceID:                record.ExperienceID,
		Revision:                    record.Revision,
		Status:                      record.Status,
		Filter:                      filter,
		ReuseConfidence:             score,
		SupportingValidations:       supportingCounted,
		Contradictions:              contradictionsCounted,
		StoredReuseConfidence:       record.ReuseConfidence,
		StoredSupportingValidations: record.SupportingValidations,
		StoredContradictions:        record.Contradictions,
		Verified:                    verified,
		HostTrusted:                 trusted,
		Unrecorded:                  unrecorded,
		Origin:                      origin,
		Initial:                     initial,
		HostTrustedRecordedOnly:     recordedOnly,
	}}, nil
}

func saturatingAdd(value, added int64) int64 {
	if value > MaxEvidenceCounter-added {
		return MaxEvidenceCounter
	}
	return value + added
}

// readIndependenceKey is the key a stored update was deduplicated on, as the stores derive it; false for one that
// does not carry the identifiers its source requires. It never refuses: a reviewer with surrounding whitespace, which
// a store keys verbatim, is keyed verbatim.
func readIndependenceKey(update ConfidenceUpdate) (string, bool) {
	key, err := IndependenceKeyFor(update)
	if err == nil {
		return key.Value, true
	}
	var argument *ConfidenceArgumentError
	if errors.As(err, &argument) && update.Source == EvidenceHuman && update.ReviewerIdentity != nil &&
		*update.ReviewerIdentity != "" {
		return "human:" + *update.ReviewerIdentity + ":" + update.RunID.String(), true
	}
	return "", false
}
