package agentexperience

import (
	"context"
	"errors"
	"fmt"
)

// Finalization's reuse evidence (contract 1.17): .NET's ExperienceFinalizationOptions.ReuseEvidence and
// ContradictOnFailure, run by ExperienceFinalizationService once a run's record is durable. Finalize here is a pure
// function, so the step is a call of its own: make it once the finalized record is stored (InsertFinalized), and again
// with the stored record when the run turns out to be finalized already, exactly as .NET runs it after the commit and
// on AlreadyFinalized. Its submissions carry derived IDs (tags 4 and 5), so a repeat reports the originals and nothing
// counts twice.

// ReuseEvidenceMode is whether finalization turns a run's reuse of stored lessons into confidence evidence (.NET's
// ReuseEvidenceMode). "" is Off.
type ReuseEvidenceMode string

// The reuse evidence modes.
const (
	ReuseEvidenceOff      ReuseEvidenceMode = "Off"
	ReuseEvidenceSameTask ReuseEvidenceMode = "SameTask"
)

// ReuseEvidenceOptions are .NET's ExperienceFinalizationOptions.ReuseEvidence and ContradictOnFailure. The zero value
// is Off: nothing is submitted.
type ReuseEvidenceOptions struct {
	Mode ReuseEvidenceMode
	// ContradictOnFailure makes a run that failed verification submit contradicting evidence; it has no effect while
	// Mode is Off.
	ContradictOnFailure bool
}

// ReuseEvidenceProducer is the producer every piece of finalization's reuse evidence carries.
const ReuseEvidenceProducer = "AgentExperience.ExperienceFinalizationService/reuse-evidence/1.0.0"

// ReuseEvidenceResult is what the step did for one record the run was given (.NET's ReuseEvidenceResult). Outcome is
// nil when nothing was submitted: the record was Skipped, or reading it or applying the evidence failed (ErrorType
// then names the error's Go type, where .NET names the exception's).
type ReuseEvidenceResult struct {
	ExperienceID UUID
	Kind         ConfidenceEvidenceKind
	Outcome      *ConfidenceUpdateOutcome
	Reason       *string
	Counted      bool
	Skipped      bool
	Replay       bool
	Refusal      *IndependenceRefusal
	ErrorType    *string
}

var errUndefinedReuseEvidenceMode = errors.New("ReuseEvidenceOptions.Mode is not a defined ReuseEvidenceMode")

// ApplyReuseEvidence is finalization's reuse-evidence step for record, a finalized run's stored record (.NET's
// TryApplyReuseEvidenceAsync): for each record the run was given that is readable in its scope and on its task,
// machine evidence through service, bound to the round finalization closed. Best effort: every refusal and every
// error is reported, never retried; truncated reports that ctx ended the step, its last entry the record it stopped
// at. The only error returned is an undefined mode.
func ApplyReuseEvidence(ctx context.Context, service *LifecycleService, authorization AuthorizationContext,
	record ExperienceRecord, options ReuseEvidenceOptions) (results []ReuseEvidenceResult, truncated bool, err error) {
	switch options.Mode {
	case "", ReuseEvidenceOff:
		return []ReuseEvidenceResult{}, false, nil
	case ReuseEvidenceSameTask:
	default:
		return nil, false, errUndefinedReuseEvidenceMode
	}
	if service == nil {
		return nil, false, errors.New("the reuse-evidence step needs a lifecycle service")
	}
	var kind ConfidenceEvidenceKind
	switch {
	case record.Outcome.Status == VerificationVerified:
		kind = EvidenceSupporting
	case record.Outcome.Status == VerificationFailed && options.ContradictOnFailure:
		kind = EvidenceContradicting
	default:
		return []ReuseEvidenceResult{}, false, nil
	}
	if record.ClosedRoundID == nil || len(record.Provenance.ExposedTo) == 0 {
		return []ReuseEvidenceResult{}, false, nil
	}
	roundID := *record.ClosedRoundID

	// Filter, then cap: a padded provenance cannot push real exposures past the bound.
	var candidates []UUID
	seen := map[UUID]bool{}
	for _, exposure := range record.Provenance.ExposedTo {
		id := exposure.ExperienceID
		if id.IsZero() || id == record.ExperienceID || seen[id] {
			continue
		}
		seen[id] = true
		candidates = append(candidates, id)
		if len(candidates) == MaxExposuresPerRun {
			break
		}
	}

	verification := "passed"
	if kind == EvidenceContradicting {
		verification = "failed"
	}
	reason := fmt.Sprintf("Reuse on the same task: run %s was given this record and its verification %s.",
		record.SourceRunID.String(), verification)
	results = []ReuseEvidenceResult{}
	for _, experienceID := range candidates {
		entry, stop := applyOneReuseEvidence(ctx, service, authorization, record, experienceID, kind, roundID, reason)
		results = append(results, entry)
		if stop {
			return results, true, nil
		}
	}
	return results, false, nil
}

func applyOneReuseEvidence(ctx context.Context, service *LifecycleService, authorization AuthorizationContext,
	record ExperienceRecord, experienceID UUID, kind ConfidenceEvidenceKind, roundID UUID,
	reason string) (ReuseEvidenceResult, bool) {
	failed := func(err error) (ReuseEvidenceResult, bool) {
		stopped := isCancellation(ctx, err)
		text := "Applying reuse evidence threw " + fmt.Sprintf("%T", err) +
			"; the run's record is durable and nothing about it changed. Finalize the run again to resubmit."
		if stopped {
			text = "Cancelled before the reuse evidence for this record was confirmed; the run's record is durable. " +
				"Finalize the run again to resubmit."
		}
		errorType := fmt.Sprintf("%T", err)
		return ReuseEvidenceResult{ExperienceID: experienceID, Kind: kind, Reason: &text, ErrorType: &errorType}, stopped
	}
	if err := ctx.Err(); err != nil {
		return failed(err)
	}
	read, err := service.store.ReadRecord(ctx, authorization, record.Scope, experienceID)
	if err != nil {
		return failed(err)
	}
	if skipped := reuseEvidenceSkipReason(read, record); skipped != "" {
		return ReuseEvidenceResult{ExperienceID: experienceID, Kind: kind, Reason: &skipped, Skipped: true}, false
	}
	evidenceID, err := ReuseEvidenceIDFor(record.SourceRunID, record.Scope, experienceID, kind)
	if err != nil {
		return failed(err)
	}
	eventID, err := ReuseEventIDFor(record.SourceRunID, record.Scope, experienceID, kind)
	if err != nil {
		return failed(err)
	}
	round := roundID
	applied, err := service.ApplyEvidence(ctx, authorization, ApplyConfidenceEvidenceRequest{
		EventID: eventID, ExperienceID: experienceID, Scope: record.Scope, EvidenceID: evidenceID, Kind: kind,
		Source: EvidenceMachine, RunID: record.SourceRunID, VerificationRoundID: &round, Reason: reason,
		Producer: ReuseEvidenceProducer,
		// The run's finalization time, read off its stored record, so a repeat sends the same event.
		OccurredAt: record.CreatedAt,
	})
	if err != nil {
		return failed(err)
	}
	// A replay: the store reported evidence it already held under this ID. A fresh commit always lands at the revision
	// after the one it was computed against.
	replay := applied.Outcome == ConfidenceApplied && applied.Counted() && applied.Event != nil &&
		applied.Revision != applied.Event.ExpectedRevision+1
	outcome := applied.Outcome
	return ReuseEvidenceResult{ExperienceID: experienceID, Kind: kind, Outcome: &outcome,
		Reason:  clonePointer(applied.Reason),
		Counted: applied.Outcome == ConfidenceApplied && applied.Counted() && !replay, Replay: replay,
		Refusal: clonePointer(applied.Refusal)}, false
}

// reuseEvidenceSkipReason is why the step does not submit for a record it read, or "" when it does.
func reuseEvidenceSkipReason(read RecordReadResult, run ExperienceRecord) string {
	if read.Outcome != StoreFound {
		return "The record could not be read in the run's scope (" + string(read.Outcome) + "); no evidence was submitted."
	}
	if read.Record == nil {
		return "The store reported the record found but returned none; no evidence was submitted."
	}
	// A port has no sharing grants: no record is readable only through one.
	if read.Record.TaskID != run.TaskID {
		return "The record is on another task; no evidence was submitted."
	}
	if read.Record.SourceRunID == run.SourceRunID {
		return "The record came from this run; no evidence was submitted."
	}
	return ""
}
