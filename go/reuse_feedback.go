package agentexperience

import (
	"context"
	"crypto/sha256"
	"errors"
	"math"
	"strconv"
	"strings"
)

// Reuse feedback (contract 1.17): .NET's ExperienceReuseFeedbackService over a feedback ledger and the contract 1.16
// lifecycle service. A run's feedback (the records it was exposed to, how it came out, what the host measured, and,
// only with evidence, whether reuse helped or hurt) is validated, written to the ledger, and then, for each record an
// accepted attribution names, turned into confidence evidence through LifecycleService.ApplyEvidence.
//
// Exposure is not attribution: a submission with no human assessment and no comparative result is recorded with
// benefit Unknown and moves nothing, whatever ClaimedBenefit says. Independence is not verified (story 27.7): as .NET
// under IndependenceVerification.TrustHostSuppliedIdentifiers, no attribution's run, round or assessment token is
// checked before the ledger is written, so an attribution degrades only through validation; the evidence it produces
// is admitted HostTrusted, and with the lifecycle's default (RecordedOnly) recorded without moving the record.

// ExperienceReuseBenefit is whether reuse helped a run, hurt it, or neither (.NET's ExperienceReuseBenefit). A value no
// member names is undefined, and refused where .NET refuses it.
type ExperienceReuseBenefit string

// The reuse benefits.
const (
	BenefitUnknown  ExperienceReuseBenefit = "Unknown"
	BenefitImproved ExperienceReuseBenefit = "Improved"
	BenefitHarmed   ExperienceReuseBenefit = "Harmed"
)

func isDefinedBenefit(b ExperienceReuseBenefit) bool {
	return b == BenefitUnknown || b == BenefitImproved || b == BenefitHarmed
}

// ReuseAttributionSource is which attribution shape a submission carried, if either (.NET's ReuseAttributionSource).
type ReuseAttributionSource string

// The attribution sources.
const (
	AttributionNone                  ReuseAttributionSource = "None"
	AttributionHumanAssessment       ReuseAttributionSource = "HumanAssessment"
	AttributionComparativeEvaluation ReuseAttributionSource = "ComparativeEvaluation"
)

func isDefinedAttributionSource(s ReuseAttributionSource) bool {
	return s == AttributionNone || s == AttributionHumanAssessment || s == AttributionComparativeEvaluation
}

func isDefinedVerificationStatus(s VerificationStatus) bool {
	return s == VerificationUnknown || s == VerificationVerified || s == VerificationFailed
}

// ReuseMeasure is what the host measured about a run: a kind it chose, and a finite number nothing interprets.
type ReuseMeasure struct {
	Kind  string
	Value float64
}

// HumanReuseAssessment is an authorized human's judgement that reuse of named records helped or hurt a run. The
// reviewer is the authorization's principal, never a field here. AssessmentToken is a bearer credential: String and
// GoString never print it (and, until story 27.7, it is neither checked nor spent).
type HumanReuseAssessment struct {
	AssessmentID            UUID
	Benefit                 ExperienceReuseBenefit
	AttributedExperienceIDs []UUID
	Rationale               string
	AssessedAt              Instant
	VerificationRoundID     *UUID
	AssessmentToken         *string
}

// String prints the assessment without its token.
func (a HumanReuseAssessment) String() string {
	token := "null"
	if a.AssessmentToken != nil {
		token = "<redacted>"
	}
	round := "null"
	if a.VerificationRoundID != nil {
		round = a.VerificationRoundID.String()
	}
	ids := make([]string, len(a.AttributedExperienceIDs))
	for i, id := range a.AttributedExperienceIDs {
		ids[i] = id.String()
	}
	return "HumanReuseAssessment { AssessmentId = " + a.AssessmentID.String() + ", Benefit = " + string(a.Benefit) +
		", AttributedExperienceIds = [" + strings.Join(ids, ", ") + "], Rationale = " + a.Rationale +
		", AssessedAt = " + a.AssessedAt.String() + ", VerificationRoundId = " + round + ", AssessmentToken = " + token + " }"
}

// GoString is String: the token is never printed.
func (a HumanReuseAssessment) GoString() string { return a.String() }

// ComparativeEvaluationResult is a comparative evaluator's result about one run. This package implements no evaluator:
// it validates the result it is given. A nil piece of evidence is refused as .NET refuses a null one.
type ComparativeEvaluationResult struct {
	EvaluatorID             string
	RunID                   UUID
	VerificationRoundID     UUID
	Benefit                 ExperienceReuseBenefit
	AttributedExperienceIDs []UUID
	Evidence                []*Evidence
	Summary                 string
	EvaluatedAt             Instant
}

// MaxExposedRecords is the most records one submission may name as exposed (.NET's
// ExperienceReuseFeedback.MaxExposedRecords).
const MaxExposedRecords = 64

// ExperienceReuseFeedback is one submission about a run stored experience was injected into (.NET's
// ExperienceReuseFeedback). FeedbackID is its idempotency key. ClaimedBenefit "" is Unknown (.NET's default); it is
// recorded and never acted on. Measure nil and ExposedExperienceIDs nil are .NET's null, refused as such.
type ExperienceReuseFeedback struct {
	FeedbackID            UUID
	RunID                 UUID
	Scope                 Scope
	ExposedExperienceIDs  []UUID
	RunOutcome            VerificationStatus
	Measure               *ReuseMeasure
	ObservedAt            Instant
	ClaimedBenefit        ExperienceReuseBenefit
	HumanAssessment       *HumanReuseAssessment
	ComparativeEvaluation *ComparativeEvaluationResult
	TrialLabel            *string
}

// ExperienceReuseExposure is one exposed record as the ledger stores it: whether attribution named it, and the evidence
// ID derived for it when it did.
type ExperienceReuseExposure struct {
	ExperienceID UUID
	Attributed   bool
	EvidenceID   *UUID
}

// RecordedExperienceReuseFeedback is a submission as the ledger stores it, with Core's attribution decision made
// (.NET's RecordedExperienceReuseFeedback). Its pointers and slices are .NET's nullable members: nil is null, which
// the ledger's validation refuses where .NET's does (a nil exposure among Exposures included).
type RecordedExperienceReuseFeedback struct {
	FeedbackID          UUID
	RunID               UUID
	Scope               *Scope
	RunOutcome          VerificationStatus
	ClaimedBenefit      ExperienceReuseBenefit
	Benefit             ExperienceReuseBenefit
	AttributionSource   ReuseAttributionSource
	ReviewerIdentity    *string
	EvaluatorID         *string
	VerificationRoundID *UUID
	AssessmentID        *UUID
	Rationale           *string
	EvidenceIDs         []UUID
	AttributedAt        *Instant
	Measure             *ReuseMeasure
	TrialLabel          *string
	ObservedAt          Instant
	Exposures           []*ExperienceReuseExposure
}

// ReuseFeedbackStoreOutcome is what a ledger write reached (.NET's ExperienceReuseFeedbackStoreOutcome).
type ReuseFeedbackStoreOutcome string

// The ledger outcomes.
const (
	LedgerRecorded        ReuseFeedbackStoreOutcome = "Recorded"
	LedgerAlreadyRecorded ReuseFeedbackStoreOutcome = "AlreadyRecorded"
	LedgerConflict        ReuseFeedbackStoreOutcome = "Conflict"
	LedgerDenied          ReuseFeedbackStoreOutcome = "Denied"
	LedgerInvalid         ReuseFeedbackStoreOutcome = "Invalid"
)

// ReuseFeedbackStoreResult is one ledger write's result: the stored submission on Recorded and AlreadyRecorded, and on
// Conflict when the caller's authorization permits its scope; the errors on Invalid.
type ReuseFeedbackStoreResult struct {
	Outcome  ReuseFeedbackStoreOutcome
	Feedback *RecordedExperienceReuseFeedback
	Errors   []StoreValidationError
}

// ReuseFeedbackStore is the append-only feedback ledger (.NET's IExperienceReuseFeedbackStore): one submission and its
// exposures written together or not at all. Expected conditions are typed results; a returned error is a store
// failure, and nothing was written.
type ReuseFeedbackStore interface {
	RecordReuseFeedback(ctx context.Context, authorization AuthorizationContext,
		feedback RecordedExperienceReuseFeedback) (ReuseFeedbackStoreResult, error)
}

// ExperienceExposureDisposition is what became of one exposed record (.NET's ExperienceExposureDisposition).
type ExperienceExposureDisposition string

// The exposure dispositions.
const (
	ExposureOnly            ExperienceExposureDisposition = "ExposureOnly"
	ExposureEvidenceApplied ExperienceExposureDisposition = "EvidenceApplied"
	ExposureIneligible      ExperienceExposureDisposition = "Ineligible"
	ExposureUnresolved      ExperienceExposureDisposition = "Unresolved"
	ExposureRefused         ExperienceExposureDisposition = "Refused"
	ExposureFailed          ExperienceExposureDisposition = "Failed"
)

// ExperienceExposureResult is what one exposed record came to (.NET's ExperienceExposureResult).
type ExperienceExposureResult struct {
	ExperienceID    UUID
	Disposition     ExperienceExposureDisposition
	EvidenceID      *UUID
	Counted         bool
	ReuseConfidence *float64
	Status          *ExperienceStatus
	Retryable       bool
	Reason          *string
}

// ExperienceReuseFeedbackOutcome is what one submission reached (.NET's ExperienceReuseFeedbackOutcome).
type ExperienceReuseFeedbackOutcome string

// The feedback outcomes.
const (
	FeedbackRecorded        ExperienceReuseFeedbackOutcome = "Recorded"
	FeedbackAlreadyRecorded ExperienceReuseFeedbackOutcome = "AlreadyRecorded"
	FeedbackConflict        ExperienceReuseFeedbackOutcome = "Conflict"
	FeedbackDenied          ExperienceReuseFeedbackOutcome = "Denied"
	FeedbackInvalid         ExperienceReuseFeedbackOutcome = "Invalid"
)

// ExperienceReuseFeedbackResult is the result of recording one submission (.NET's ExperienceReuseFeedbackResult).
type ExperienceReuseFeedbackResult struct {
	Outcome           ExperienceReuseFeedbackOutcome
	FeedbackID        UUID
	Benefit           ExperienceReuseBenefit
	AttributionSource ReuseAttributionSource
	Exposures         []ExperienceExposureResult
	Errors            []StoreValidationError
	Reason            *string
}

// IsRetryable reports whether any exposed record's evidence can still land by resubmitting the identical feedback.
func (r ExperienceReuseFeedbackResult) IsRetryable() bool {
	for _, exposure := range r.Exposures {
		if exposure.Retryable {
			return true
		}
	}
	return false
}

// HumanAssessmentProducer is the producer evidence from a human assessment records.
const HumanAssessmentProducer = "experience-reuse-feedback/human-assessment"

// OperationReuseFeedback is the telemetry operation a submission is reported as; AttributeFeedbackID its ID.
const (
	OperationReuseFeedback = "reuse_feedback"
	AttributeFeedbackID    = "agentexperience.feedback_id"
)

var feedbackDerivationNamespace = MustParseUUID("3f5a1d62-8c04-4b91-a7e3-6d2f0b48c915")

const (
	feedbackEvidenceIDTag byte = 1
	feedbackEventIDTag    byte = 2
)

// FeedbackEvidenceIDFor is the evidence ID feedbackID submits for experienceID (.NET's
// ExperienceReuseFeedbackService.EvidenceIdFor): a retry re-derives it, and converges.
func FeedbackEvidenceIDFor(feedbackID, experienceID UUID) UUID {
	return deriveFeedbackID(feedbackID, experienceID, feedbackEvidenceIDTag)
}

// FeedbackEventIDFor is the lifecycle event ID feedbackID commits for experienceID (.NET's EventIdFor).
func FeedbackEventIDFor(feedbackID, experienceID UUID) UUID {
	return deriveFeedbackID(feedbackID, experienceID, feedbackEventIDTag)
}

// deriveFeedbackID is SHA-256 over the namespace, the feedback and the record (RFC 4122 byte order) and the tag,
// stamped as a UUIDv8.
func deriveFeedbackID(feedbackID, experienceID UUID, tag byte) UUID {
	input := make([]byte, 0, 49)
	input = append(input, feedbackDerivationNamespace[:]...)
	input = append(input, feedbackID[:]...)
	input = append(input, experienceID[:]...)
	input = append(input, tag)
	hash := sha256.Sum256(input)
	var id UUID
	copy(id[:], hash[:16])
	id[6] = (id[6] & 0x0F) | 0x80
	id[8] = (id[8] & 0x3F) | 0x80
	return id
}

// ReuseFeedbackService records reuse feedback (.NET's ExperienceReuseFeedbackService). Safe for concurrent use when
// its ledger and lifecycle service are.
type ReuseFeedbackService struct {
	store     ReuseFeedbackStore
	lifecycle *LifecycleService
}

var errNoFeedbackDependencies = errors.New("the reuse-feedback service needs a ledger and a lifecycle service")

// NewReuseFeedbackService is a service over the ledger the exposure is written to and the lifecycle service that
// applies every attributed record's evidence.
func NewReuseFeedbackService(store ReuseFeedbackStore, lifecycle *LifecycleService) (*ReuseFeedbackService, error) {
	if store == nil || lifecycle == nil {
		return nil, errNoFeedbackDependencies
	}
	return &ReuseFeedbackService{store: store, lifecycle: lifecycle}, nil
}

// Record records one submission (.NET's RecordAsync): validates it, decides whether it carries attribution this
// library accepts, writes the ledger, then submits evidence for each attributed record. A ledger failure is returned
// and nothing was recorded; one record's failure (a store failure, or the confidence engine's) is that record's
// Failed, and a cancellation once the ledger is written is reported per record rather than returned. It is reported
// as the reuse_feedback operation.
func (s *ReuseFeedbackService) Record(ctx context.Context, authorization AuthorizationContext,
	feedback ExperienceReuseFeedback) (ExperienceReuseFeedbackResult, error) {
	ctx, op := Observe(ctx, OperationReuseFeedback, false)
	defer op.End()
	op.Set(AttributeFeedbackID, feedback.FeedbackID.String())
	op.Set(AttributeRunID, feedback.RunID.String())
	result, err := s.record(ctx, authorization, feedback)
	if err != nil {
		op.Fail(err)
		return ExperienceReuseFeedbackResult{}, err
	}
	op.Done(string(result.Outcome))
	return result, nil
}

// attribution is the decision made once per submission.
type attribution struct {
	source              ReuseAttributionSource
	benefit             ExperienceReuseBenefit
	attributed          []UUID
	reviewerIdentity    *string
	evaluatorID         *string
	verificationRoundID *UUID
	assessmentID        *UUID
	rationale           *string
	evidenceIDs         []UUID
	attributedAt        *Instant
	producer            string
}

func noAttribution() attribution {
	return attribution{source: AttributionNone, benefit: BenefitUnknown, evidenceIDs: []UUID{}}
}

func attributionOf(feedback ExperienceReuseFeedback, authorization AuthorizationContext) attribution {
	if human := feedback.HumanAssessment; human != nil {
		reviewer, id, rationale, at := authorization.PrincipalID, human.AssessmentID, human.Rationale, human.AssessedAt
		return attribution{source: AttributionHumanAssessment, benefit: human.Benefit,
			attributed: human.AttributedExperienceIDs, reviewerIdentity: &reviewer,
			verificationRoundID: clonePointer(human.VerificationRoundID), assessmentID: &id, rationale: &rationale,
			evidenceIDs: []UUID{}, attributedAt: &at, producer: HumanAssessmentProducer}
	}
	if comparative := feedback.ComparativeEvaluation; comparative != nil {
		evaluator, round, summary, at := comparative.EvaluatorID, comparative.VerificationRoundID, comparative.Summary,
			comparative.EvaluatedAt
		ids := make([]UUID, 0, len(comparative.Evidence))
		for _, evidence := range comparative.Evidence {
			ids = append(ids, evidence.EvidenceID)
		}
		return attribution{source: AttributionComparativeEvaluation, benefit: comparative.Benefit,
			attributed: comparative.AttributedExperienceIDs, evaluatorID: &evaluator, verificationRoundID: &round,
			rationale: &summary, evidenceIDs: ids, attributedAt: &at, producer: evaluator}
	}
	return noAttribution()
}

func (a attribution) attributes(id UUID) bool {
	if a.source == AttributionNone {
		return false
	}
	for _, attributed := range a.attributed {
		if attributed == id {
			return true
		}
	}
	return false
}

func stringPointer(text string) *string { return &text }

const deniedReason = "The feedback's scope lies outside the host-established authorization."

func endedFeedback(outcome ExperienceReuseFeedbackOutcome, feedbackID UUID, errors []StoreValidationError,
	reason *string) ExperienceReuseFeedbackResult {
	if errors == nil {
		errors = []StoreValidationError{}
	}
	return ExperienceReuseFeedbackResult{Outcome: outcome, FeedbackID: feedbackID, Benefit: BenefitUnknown,
		AttributionSource: AttributionNone, Exposures: []ExperienceExposureResult{}, Errors: errors, Reason: reason}
}

func isCancellation(ctx context.Context, err error) bool {
	return ctx.Err() != nil || errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded)
}

func (s *ReuseFeedbackService) record(ctx context.Context, authorization AuthorizationContext,
	feedback ExperienceReuseFeedback) (ExperienceReuseFeedbackResult, error) {
	if feedback.ClaimedBenefit == "" {
		feedback.ClaimedBenefit = BenefitUnknown
	}
	fatal, degrading := validateReuseFeedbackSubmission(feedback, authorization)
	if len(fatal) > 0 {
		return endedFeedback(FeedbackInvalid, feedback.FeedbackID, fatal, nil), nil
	}
	if !authorization.Permits(feedback.Scope) {
		return endedFeedback(FeedbackDenied, feedback.FeedbackID, nil, stringPointer(deniedReason)), nil
	}
	// Under the opt-out (the only mode a port runs) nothing is verified before the ledger, and there is no signer, so
	// no run is "tampered": an attribution degrades only through validation.

	chosen := noAttribution()
	var degradedReason *string
	if len(degrading) > 0 {
		parts := make([]string, len(degrading))
		for i, e := range degrading {
			parts[i] = e.Path + " " + e.Message
		}
		degradedReason = stringPointer("The attribution did not meet its evidence requirements, so the exposure was " +
			"recorded with benefit " + string(BenefitUnknown) + " and no confidence submission: " +
			strings.Join(parts, "; "))
	} else {
		chosen = attributionOf(feedback, authorization)
	}

	submission := ledgerSubmission(feedback, chosen)
	stored, err := s.store.RecordReuseFeedback(ctx, authorization, submission)
	if err != nil {
		return ExperienceReuseFeedbackResult{}, err
	}
	switch stored.Outcome {
	case LedgerRecorded, LedgerAlreadyRecorded:
	case LedgerConflict:
		exposures := []ExperienceExposureResult{}
		if stored.Feedback != nil {
			for _, exposure := range stored.Feedback.Exposures {
				if exposure == nil {
					continue
				}
				exposures = append(exposures, ExperienceExposureResult{ExperienceID: exposure.ExperienceID,
					Disposition: ExposureRefused, EvidenceID: clonePointer(exposure.EvidenceID),
					Reason: stringPointer("Recorded by the submission already stored under this feedback ID, not by this call.")})
			}
		}
		return ExperienceReuseFeedbackResult{Outcome: FeedbackConflict, FeedbackID: feedback.FeedbackID,
			Benefit: BenefitUnknown, AttributionSource: AttributionNone, Exposures: exposures,
			Errors: []StoreValidationError{},
			Reason: stringPointer("This feedback ID is already recorded with different content; nothing was written.")}, nil
	case LedgerDenied:
		return endedFeedback(FeedbackDenied, feedback.FeedbackID, nil, stringPointer(deniedReason)), nil
	default:
		return endedFeedback(FeedbackInvalid, feedback.FeedbackID, stored.Errors, nil), nil
	}

	results := make([]ExperienceExposureResult, 0, len(submission.Exposures))
	for index, exposure := range submission.Exposures {
		if !exposure.Attributed {
			reason := "The accepted attribution did not name this record."
			if chosen.source == AttributionNone {
				reason = "Exposure without attribution evidence moves nothing."
				if degradedReason != nil {
					reason = *degradedReason
				}
			}
			results = append(results, ExperienceExposureResult{ExperienceID: exposure.ExperienceID,
				Disposition: ExposureOnly, Reason: &reason})
			continue
		}
		result, err := s.submitEvidence(ctx, authorization, feedback, chosen, *exposure)
		if err != nil {
			if isCancellation(ctx, err) {
				// The ledger is durable and some records may already have moved: report what was decided, and mark
				// the rest retryable.
				for _, abandoned := range submission.Exposures[index:] {
					results = append(results, ExperienceExposureResult{ExperienceID: abandoned.ExperienceID,
						Disposition: ExposureFailed, EvidenceID: clonePointer(abandoned.EvidenceID), Retryable: true,
						Reason: stringPointer("Cancelled before this record's evidence was submitted; resubmit the same feedback ID.")})
				}
				break
			}
			return ExperienceReuseFeedbackResult{}, err
		}
		results = append(results, result)
	}

	outcome := FeedbackRecorded
	if stored.Outcome == LedgerAlreadyRecorded {
		outcome = FeedbackAlreadyRecorded
	}
	return ExperienceReuseFeedbackResult{Outcome: outcome, FeedbackID: feedback.FeedbackID, Benefit: chosen.benefit,
		AttributionSource: chosen.source, Exposures: results, Errors: []StoreValidationError{},
		Reason: degradedReason}, nil
}

// submitEvidence applies one attributed record's evidence and maps the answer onto its disposition. A store failure
// or an engine failure is that record's Failed; a cancellation, or an error that is neither (an argument the
// lifecycle refuses outright), is returned.
func (s *ReuseFeedbackService) submitEvidence(ctx context.Context, authorization AuthorizationContext,
	feedback ExperienceReuseFeedback, chosen attribution, exposure ExperienceReuseExposure) (ExperienceExposureResult, error) {
	evidenceID := *exposure.EvidenceID
	kind := EvidenceSupporting
	if chosen.benefit == BenefitHarmed {
		kind = EvidenceContradicting
	}
	source := EvidenceMachine
	var round *UUID
	if chosen.source == AttributionHumanAssessment {
		source = EvidenceHuman
	} else {
		round = clonePointer(chosen.verificationRoundID)
	}
	direction := "improvement"
	if chosen.benefit == BenefitHarmed {
		direction = "harm"
	}
	var token *string
	if feedback.HumanAssessment != nil && chosen.source == AttributionHumanAssessment {
		token = clonePointer(feedback.HumanAssessment.AssessmentToken)
	}
	applied, err := s.lifecycle.applyEvidenceObserved(ctx, authorization, ApplyConfidenceEvidenceRequest{
		EventID:             FeedbackEventIDFor(feedback.FeedbackID, exposure.ExperienceID),
		ExperienceID:        exposure.ExperienceID,
		Scope:               feedback.Scope,
		EvidenceID:          evidenceID,
		Kind:                kind,
		Source:              source,
		RunID:               feedback.RunID,
		VerificationRoundID: round,
		Reason:              "Reuse feedback " + feedback.FeedbackID.String() + " attributed " + direction + " to this record.",
		Producer:            chosen.producer,
		OccurredAt:          feedback.ObservedAt,
		Detail:              clonePointer(chosen.rationale),
		AssessmentToken:     token,
	}, true)
	failed := func(reason string, status *ExperienceStatus) ExperienceExposureResult {
		return ExperienceExposureResult{ExperienceID: exposure.ExperienceID, Disposition: ExposureFailed,
			EvidenceID: &evidenceID, Status: status, Retryable: true, Reason: &reason}
	}
	if err != nil {
		var argument *ConfidenceArgumentError
		switch {
		case IsConfidenceEngineFailure(err):
			return failed(err.Error(), nil), nil
		case isCancellation(ctx, err) || errors.As(err, &argument):
			return ExperienceExposureResult{}, err
		}
		// A store failure: the exposure is durable, so this is one record's retryable failure.
		return failed(err.Error(), nil), nil
	}
	refused := func(status *ExperienceStatus, reason string) ExperienceExposureResult {
		return ExperienceExposureResult{ExperienceID: exposure.ExperienceID, Disposition: ExposureRefused,
			EvidenceID: &evidenceID, Status: status, Reason: &reason}
	}
	orDefault := func(reason *string, fallback string) string {
		if reason != nil {
			return *reason
		}
		return fallback
	}
	switch applied.Outcome {
	case ConfidenceApplied:
		result := ExperienceExposureResult{ExperienceID: exposure.ExperienceID, Disposition: ExposureEvidenceApplied,
			EvidenceID: &evidenceID, Counted: applied.Counted(), Status: clonePointer(applied.Status)}
		if applied.Update != nil {
			score := applied.Update.NewReuseConfidence
			result.ReuseConfidence = &score
		}
		if !result.Counted {
			if applied.RecordedOnEvent {
				result.Reason = stringPointer("Host-trusted evidence is recorded without being counted (HostTrustedEvidenceEffect.RecordedOnly).")
			} else {
				result.Reason = stringPointer("The same run already produced evidence for this record; recorded, not counted.")
			}
		}
		return result, nil
	case ConfidenceIneligible:
		return ExperienceExposureResult{ExperienceID: exposure.ExperienceID, Disposition: ExposureIneligible,
			EvidenceID: &evidenceID, Status: clonePointer(applied.Status), Reason: clonePointer(applied.Reason)}, nil
	case ConfidenceNotFound, ConfidenceDenied:
		reason := orDefault(applied.Reason, "No such record within the feedback's scope.")
		return ExperienceExposureResult{ExperienceID: exposure.ExperienceID, Disposition: ExposureUnresolved,
			EvidenceID: &evidenceID, Reason: &reason}, nil
	case ConfidenceStaleRevision, ConfidenceStatusMismatch:
		return failed("The record moved between the read and the commit; resubmit the same feedback ID.",
			clonePointer(applied.Status)), nil
	case ConfidenceDeleted:
		return refused(nil, orDefault(applied.Reason,
			"The record was erased after this exposure was recorded; no evidence can be applied to it.")), nil
	}
	if len(applied.Errors) > 0 {
		parts := make([]string, len(applied.Errors))
		for i, e := range applied.Errors {
			parts[i] = e.Path + ": " + e.Message
		}
		return refused(clonePointer(applied.Status), strings.Join(parts, "; ")), nil
	}
	return refused(clonePointer(applied.Status), orDefault(applied.Reason, "The confidence path refused this submission.")), nil
}

// ledgerSubmission is the submission in the ledger's shape: the exposures in record order, each attributed one with
// its derived evidence ID.
func ledgerSubmission(feedback ExperienceReuseFeedback, chosen attribution) RecordedExperienceReuseFeedback {
	ordered := append([]UUID(nil), feedback.ExposedExperienceIDs...)
	sortUUIDs(ordered)
	exposures := make([]*ExperienceReuseExposure, 0, len(ordered))
	for _, id := range ordered {
		exposure := &ExperienceReuseExposure{ExperienceID: id, Attributed: chosen.attributes(id)}
		if exposure.Attributed {
			evidence := FeedbackEvidenceIDFor(feedback.FeedbackID, id)
			exposure.EvidenceID = &evidence
		}
		exposures = append(exposures, exposure)
	}
	scope := feedback.Scope
	return RecordedExperienceReuseFeedback{
		FeedbackID: feedback.FeedbackID, RunID: feedback.RunID, Scope: &scope, RunOutcome: feedback.RunOutcome,
		ClaimedBenefit: feedback.ClaimedBenefit, Benefit: chosen.benefit, AttributionSource: chosen.source,
		ReviewerIdentity: chosen.reviewerIdentity, EvaluatorID: chosen.evaluatorID,
		VerificationRoundID: chosen.verificationRoundID, AssessmentID: chosen.assessmentID, Rationale: chosen.rationale,
		EvidenceIDs: chosen.evidenceIDs, AttributedAt: chosen.attributedAt, Measure: feedback.Measure,
		TrialLabel: feedback.TrialLabel, ObservedAt: feedback.ObservedAt, Exposures: exposures,
	}
}

// ---- The service's validation ------------------------------------------------------------------------------------

// validateReuseFeedbackSubmission is .NET's Validate: the fatal errors (nothing is written) and the degrading ones
// (the attribution is dropped), each in .NET's order.
func validateReuseFeedbackSubmission(f ExperienceReuseFeedback, a AuthorizationContext) (fatal, degrading validationErrors) {
	if f.FeedbackID.IsZero() {
		fatal.add("FeedbackId", "must not be an empty GUID: it is the submission's idempotency key.")
	}
	if f.RunID.IsZero() {
		fatal.add("RunId", "must name the run the records were injected into.")
	}
	if !isDefinedVerificationStatus(f.RunOutcome) {
		fatal.add("RunOutcome", "must be a defined task verification status.")
	}
	if !isDefinedBenefit(f.ClaimedBenefit) {
		fatal.add("ClaimedBenefit", "must be a defined benefit.")
	}
	if f.ObservedAt.IsZero() {
		fatal.add("ObservedAt", "must be set to when the feedback was observed.")
	}
	if f.TrialLabel != nil && isNullOrWhitespace(*f.TrialLabel) {
		fatal.add("TrialLabel", "must be non-blank when supplied; omit it instead.")
	}
	if f.Measure == nil {
		fatal.add("Measure", "must name what was measured about the run.")
	} else {
		if isNullOrWhitespace(f.Measure.Kind) {
			fatal.add("Measure.Kind", "must be a non-blank name for what was measured.")
		}
		if math.IsNaN(f.Measure.Value) || math.IsInf(f.Measure.Value, 0) {
			fatal.add("Measure.Value", "must be a finite number.")
		}
	}
	exposed := validateExposedIDs(f.ExposedExperienceIDs, &fatal)
	if f.HumanAssessment != nil && f.ComparativeEvaluation != nil {
		fatal.add("HumanAssessment",
			"a submission carries at most one attribution: a human assessment or a comparative evaluation, never both.")
		return fatal, degrading
	}
	if human := f.HumanAssessment; human != nil {
		const path = "HumanAssessment"
		validateAttributedIDs(human.AttributedExperienceIDs, exposed, path+".AttributedExperienceIds", &fatal, &degrading)
		validateAttributionBenefit(human.Benefit, path+".Benefit", &degrading)
		if human.AssessmentID.IsZero() {
			degrading.add(path+".AssessmentId", "must name the host-established review this judgement came out of.")
		}
		if human.VerificationRoundID != nil && human.VerificationRoundID.IsZero() {
			degrading.add(path+".VerificationRoundId",
				"must name a verification round or be omitted; an empty GUID is neither.")
		}
		if isNullOrWhitespace(human.Rationale) {
			degrading.add(path+".Rationale", "must be a non-blank, auditable rationale.")
		}
		if human.AssessedAt.IsZero() {
			degrading.add(path+".AssessedAt", "must be set to when the assessment was made.")
		}
		if isNullOrWhitespace(a.PrincipalID) {
			degrading.add("Authorization.PrincipalId",
				"must be non-blank for a human assessment: it is the reviewer the attribution is counted under.")
		} else if padded(a.PrincipalID) {
			degrading.add("Authorization.PrincipalId",
				"must not have leading or trailing whitespace: it would be counted as a second, independent reviewer.")
		}
	} else if comparative := f.ComparativeEvaluation; comparative != nil {
		const path = "ComparativeEvaluation"
		validateAttributedIDs(comparative.AttributedExperienceIDs, exposed, path+".AttributedExperienceIds", &fatal, &degrading)
		validateAttributionBenefit(comparative.Benefit, path+".Benefit", &degrading)
		if comparative.RunID != f.RunID {
			fatal.add(path+".RunId", "must be the run this feedback is about.")
		}
		if isNullOrWhitespace(comparative.EvaluatorID) {
			degrading.add(path+".EvaluatorId", "must be a non-blank evaluator identity.")
		}
		if comparative.VerificationRoundID.IsZero() {
			degrading.add(path+".VerificationRoundId",
				"must name the verification round the comparison was made in: it is half of the machine independence key.")
		}
		validateComparativeEvidence(comparative, path+".Evidence", &degrading)
		if isNullOrWhitespace(comparative.Summary) {
			degrading.add(path+".Summary", "must be a non-blank, auditable summary.")
		}
		if comparative.EvaluatedAt.IsZero() {
			degrading.add(path+".EvaluatedAt", "must be set to when the comparison was made.")
		}
	}
	return fatal, degrading
}

func validateExposedIDs(ids []UUID, fatal *validationErrors) map[UUID]bool {
	const path = "ExposedExperienceIds"
	exposed := map[UUID]bool{}
	if ids == nil {
		fatal.add(path, "must name the records the run was exposed to.")
		return exposed
	}
	if len(ids) == 0 {
		fatal.add(path, "must name at least one record: feedback about no exposure records nothing.")
		return exposed
	}
	if len(ids) > MaxExposedRecords {
		fatal.add(path, "must name at most "+strconv.Itoa(MaxExposedRecords)+" records.")
	}
	for _, id := range ids {
		switch {
		case id.IsZero():
			fatal.add(path, "must not contain an empty GUID.")
		case exposed[id]:
			fatal.add(path, "must not name the same record twice.")
		default:
			exposed[id] = true
		}
	}
	return exposed
}

func validateAttributedIDs(ids []UUID, exposed map[UUID]bool, path string, fatal, degrading *validationErrors) {
	if len(ids) == 0 {
		degrading.add(path, "must name at least one exposed record.")
		return
	}
	seen := map[UUID]bool{}
	for _, id := range ids {
		if seen[id] {
			degrading.add(path, "must not name the same record twice.")
			continue
		}
		seen[id] = true
		if !exposed[id] {
			fatal.add(path, "must name only records the run was exposed to.")
		}
	}
}

func validateAttributionBenefit(benefit ExperienceReuseBenefit, path string, degrading *validationErrors) {
	if !isDefinedBenefit(benefit) {
		degrading.add(path, "must be a defined benefit.")
	} else if benefit == BenefitUnknown {
		degrading.add(path,
			"must attribute improvement or harm; an attribution of Unknown is not an attribution, so omit it instead.")
	}
}

func validateComparativeEvidence(comparative *ComparativeEvaluationResult, path string, degrading *validationErrors) {
	if len(comparative.Evidence) == 0 {
		degrading.add(path, "must carry the evidence the comparison was reached from.")
		return
	}
	seen := map[UUID]bool{}
	for _, evidence := range comparative.Evidence {
		if evidence == nil {
			degrading.add(path, "must not contain a null piece of evidence.")
			continue
		}
		if evidence.EvidenceID.IsZero() {
			degrading.add(path+".EvidenceId", "must not be an empty GUID.")
		} else if seen[evidence.EvidenceID] {
			degrading.add(path+".EvidenceId", "must not name the same piece of evidence twice.")
		} else {
			seen[evidence.EvidenceID] = true
		}
		if evidence.VerificationRoundID != comparative.VerificationRoundID {
			degrading.add(path+".VerificationRoundId",
				"must be the round the result names: evidence from another round is not evidence about this comparison.")
		}
	}
}
