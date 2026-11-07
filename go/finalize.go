package agentexperience

import (
	"context"
	"errors"
)

// Finalization: a captured run plus the host's verification becomes one stored record and its initial lifecycle
// event (contract steps 2 to 5).

// FinalizationProducer is the producer every initial lifecycle event carries.
const FinalizationProducer = "AgentExperience.ExperienceFinalizationService/1.0.0"

const (
	initialValidatedReuseConfidence = 2.0 / 3.0
	initialSupportingValidations    = 1
)

// InitialLifecycleEvent is the event that moves a new record from Candidate to its status, taking it to revision 1.
// The store adds the record's scope columns, the actor and its own recorded_at.
type InitialLifecycleEvent struct {
	EventID          UUID
	ExperienceID     UUID
	PriorStatus      ExperienceStatus
	CurrentStatus    ExperienceStatus
	Reason           string
	Producer         string
	OccurredAt       Instant
	ExpectedRevision int64
	AppliedRevision  int64
}

// ToJSON is the event columns finalization decides, as expected-event.json holds them.
func (e *InitialLifecycleEvent) ToJSON() *Object {
	return NewObject(
		Member{"event_id", e.EventID.String()},
		Member{"experience_id", e.ExperienceID.String()},
		Member{"prior_status", string(e.PriorStatus)},
		Member{"current_status", string(e.CurrentStatus)},
		Member{"reason", e.Reason},
		Member{"producer", e.Producer},
		Member{"occurred_at", e.OccurredAt.String()},
		Member{"expected_revision", integer(e.ExpectedRevision)},
		Member{"applied_revision", integer(e.AppliedRevision)},
	)
}

// FinalizedExperience is a finalized record, in its final state at revision 1, and the initial event that must be
// written with it.
type FinalizedExperience struct {
	Record     ExperienceRecord
	Event      InitialLifecycleEvent
	Evaluation VerificationResult
	// ScreeningRefusal is why screening refused the reflection, when it did (the record is then Quarantined): a
	// *ScreeningRefusalError (ScreeningRefusalOf), which never holds record content.
	ScreeningRefusal error
	// ReflectionFailure is why the reflector produced no reflection, when it did not (contract 1.14; the record is then
	// Quarantined): the reflector's error (the maf module's model-backed reflector returns a *ReflectionFailedError),
	// a *ReflectorPanicError, or a binding mismatch (ErrReflectionBinding).
	ReflectionFailure error
}

// Verification is the host's verification of a run.
type Verification struct {
	// ClosedRound is the round the host closed; nil when none was.
	ClosedRound             *ClosedVerificationRound
	RequiredChecks          []RequiredCheck
	Evidence                []Evidence
	CurrentArtifactRevision string
	FinalizedAt             Instant
}

// Finalize finalizes a finished run: verification, the deterministic reflection (only for a Verified run), the
// stored record (Validated with a reflection, else Quarantined) and its initial lifecycle event.
func Finalize(run CapturedRun, verification Verification) (FinalizedExperience, error) {
	return FinalizeContext(context.Background(), run, verification)
}

// FinalizeContext is Finalize reporting to the observer (SetObserver) under ctx. Its verification and reflection are
// reported as nested operations of its own.
func FinalizeContext(ctx context.Context, run CapturedRun, verification Verification) (FinalizedExperience, error) {
	return FinalizeContextWith(ctx, run, verification, FinalizeOptions{})
}

// FinalizeContextWith is FinalizeContext reflecting with options.Reflector (contract 1.14): the deterministic
// reflector when nil. A reflector failure, a reflection that does not match its request, or one screening refuses
// stores the record Quarantined, with no reflection (ReflectionFailure, ScreeningRefusal); only the caller's own
// cancellation is returned as an error.
func FinalizeContextWith(ctx context.Context, run CapturedRun, verification Verification, options FinalizeOptions) (
	FinalizedExperience, error) {
	if options.Reflector == nil {
		options.Reflector = DeterministicReflector{}
	}
	ctx, op := Observe(ctx, "finalize", false)
	defer op.End()
	if op != nil {
		op.Set(AttributeRunID, run.RunID.String())
		op.Set(AttributeStage, "Load")
	}
	finalized, err := finalize(ctx, op, run, verification, options.Reflector)
	if err != nil {
		op.Fail(err)
		return finalized, err
	}
	if op != nil {
		op.Set(AttributeExperienceID, finalized.Record.ExperienceID.String())
		if refusal, ok := ScreeningRefusalOf(finalized.ScreeningRefusal); ok {
			op.Set(AttributeScreeningRefusal, string(refusal.Code))
		}
	}
	op.Done(string(finalized.Record.Status))
	return finalized, nil
}

// finalize is the finalization; the stage it has reached is on the operation, also when it fails.
func finalize(ctx context.Context, op *Operation, run CapturedRun, verification Verification, reflector Reflector) (
	FinalizedExperience, error) {
	if run.ExecutionStatus == "" {
		return FinalizedExperience{}, errors.New("the run has not finished and cannot be finalized")
	}
	if run.RunID.IsZero() {
		return FinalizedExperience{}, errors.New("a run must be named by a non-empty UUID")
	}
	if isNullOrWhitespace(run.TaskID) {
		return FinalizedExperience{}, errors.New("a run's task ID must not be blank")
	}
	if verification.FinalizedAt.IsZero() {
		return FinalizedExperience{}, errors.New("the finalization instant must be set")
	}
	at := verification.FinalizedAt.TruncateToMicroseconds()
	if op != nil {
		op.Set(AttributeStage, "Evaluate")
	}
	evaluation, err := aggregate(ctx, true, verification.Evidence, verification.RequiredChecks, verification.ClosedRound,
		verification.CurrentArtifactRevision, at)
	if err != nil {
		return FinalizedExperience{}, err
	}

	var reflection *Reflection
	var refusal, failure error
	if evaluation.Outcome.Status == VerificationVerified {
		if op != nil {
			op.Set(AttributeStage, "Reflect")
		}
		var err error
		reflection, failure, refusal, err = reflectAndScreen(ctx, reflector, ReflectionRequest{Run: run,
			Evaluation: evaluation, ReflectionID: ReflectionIDFor(run.RunID), CreatedAt: at})
		if err != nil {
			return FinalizedExperience{}, err
		}
	}

	if op != nil {
		op.Set(AttributeStage, "CreateRecord")
	}
	status := StatusQuarantined
	confidence, validations := 0.0, int64(0)
	if reflection != nil {
		status = StatusValidated
		confidence, validations = initialValidatedReuseConfidence, initialSupportingValidations
	}
	var roundID *UUID
	if round := verification.ClosedRound; round != nil && !round.RoundID.IsZero() {
		id := round.RoundID
		roundID = &id
	}
	attempts := run.Attempts
	if attempts == nil {
		attempts = []Attempt{}
	}
	record := ExperienceRecord{
		ExperienceID:          ExperienceIDFor(run.RunID, run.Scope),
		SourceRunID:           run.RunID,
		Scope:                 run.Scope,
		TaskID:                run.TaskID,
		TaskSummary:           run.TaskDescription,
		Attempts:              attempts,
		Outcome:               evaluation.Outcome,
		CompletionScore:       evaluation.CompletionScore,
		Reflection:            reflection,
		Environment:           run.Environment,
		Provenance:            run.Provenance,
		Status:                status,
		ReuseConfidence:       confidence,
		SupportingValidations: validations,
		Contradictions:        0,
		Revision:              1,
		CreatedAt:             at,
		UpdatedAt:             at,
		ClosedRoundID:         roundID,
		Origin:                OriginFinalized,
	}
	recorded := "reflection recorded"
	if reflection == nil {
		recorded = "no eligible lesson recorded"
	}
	event := InitialLifecycleEvent{
		EventID:       InitialEventIDFor(run.RunID, run.Scope),
		ExperienceID:  record.ExperienceID,
		PriorStatus:   StatusCandidate,
		CurrentStatus: status,
		Reason: "Initial finalization: verification " + string(evaluation.Outcome.Status) + ", completion score " +
			formatRoundTrip(evaluation.CompletionScore) + ", " + recorded + ".",
		Producer:         FinalizationProducer,
		OccurredAt:       at,
		ExpectedRevision: 0,
		AppliedRevision:  1,
	}
	return FinalizedExperience{Record: record, Event: event, Evaluation: evaluation, ScreeningRefusal: refusal,
		ReflectionFailure: failure}, nil
}
