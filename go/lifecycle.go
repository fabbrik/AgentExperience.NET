package agentexperience

import (
	"context"
	"errors"
	"fmt"
)

// The lifecycle of a stored record (contract 1.16): the types .NET's ExperienceLifecycleService and the store
// operations it uses (CommitLifecycleEventAsync, GetHistoryAsync, CheckSupersessionAsync and the scoped read) speak.
// Enums are their .NET member names; a value no member names (an undefined status, say) passes through, and the
// validation refuses it exactly where .NET does.

// ConfidenceEvidenceAdmission is which mode admitted a piece of evidence (.NET's ConfidenceEvidenceAdmission). A port
// verifies no independence (story 27.7), so everything its service admits is HostTrusted.
type ConfidenceEvidenceAdmission string

// The admissions.
const (
	AdmissionVerified    ConfidenceEvidenceAdmission = "Verified"
	AdmissionHostTrusted ConfidenceEvidenceAdmission = "HostTrusted"
)

// ExperienceStoreOutcome is what a store operation answered (.NET's ExperienceStoreOutcome, the members the lifecycle
// uses).
type ExperienceStoreOutcome string

// The store outcomes.
const (
	StoreFound                 ExperienceStoreOutcome = "Found"
	StoreNotFound              ExperienceStoreOutcome = "NotFound"
	StoreDenied                ExperienceStoreOutcome = "Denied"
	StoreInvalid               ExperienceStoreOutcome = "Invalid"
	StoreConflict              ExperienceStoreOutcome = "Conflict"
	StoreCommitted             ExperienceStoreOutcome = "Committed"
	StoreStaleRevision         ExperienceStoreOutcome = "StaleRevision"
	StoreStatusMismatch        ExperienceStoreOutcome = "StatusMismatch"
	StoreReplacementNotAllowed ExperienceStoreOutcome = "ReplacementNotAllowed"
	StoreDeleted               ExperienceStoreOutcome = "Deleted"
)

// SupersessionOutcome is what a supersession check answered (.NET's ExperienceSupersessionOutcome).
type SupersessionOutcome string

// The supersession outcomes.
const (
	SupersessionAllowed             SupersessionOutcome = "Allowed"
	SupersessionRecordNotFound      SupersessionOutcome = "RecordNotFound"
	SupersessionReplacementNotFound SupersessionOutcome = "ReplacementNotFound"
	SupersessionCycle               SupersessionOutcome = "Cycle"
	SupersessionDenied              SupersessionOutcome = "Denied"
	SupersessionInvalid             SupersessionOutcome = "Invalid"
)

// LifecycleTransitionOutcome is what a transition commit answered (.NET's LifecycleTransitionOutcome).
type LifecycleTransitionOutcome string

// The transition outcomes.
const (
	TransitionCommitted             LifecycleTransitionOutcome = "Committed"
	TransitionNotAllowed            LifecycleTransitionOutcome = "TransitionNotAllowed"
	TransitionReplacementNotAllowed LifecycleTransitionOutcome = "ReplacementNotAllowed"
	TransitionStaleRevision         LifecycleTransitionOutcome = "StaleRevision"
	TransitionStatusMismatch        LifecycleTransitionOutcome = "StatusMismatch"
	TransitionConflict              LifecycleTransitionOutcome = "Conflict"
	TransitionNotFound              LifecycleTransitionOutcome = "NotFound"
	TransitionDenied                LifecycleTransitionOutcome = "Denied"
	TransitionInvalid               LifecycleTransitionOutcome = "Invalid"
	TransitionDeleted               LifecycleTransitionOutcome = "Deleted"
)

// ConfidenceUpdateOutcome is what applying evidence answered (.NET's ConfidenceUpdateOutcome).
type ConfidenceUpdateOutcome string

// The confidence update outcomes.
const (
	ConfidenceApplied        ConfidenceUpdateOutcome = "Applied"
	ConfidenceIneligible     ConfidenceUpdateOutcome = "Ineligible"
	ConfidenceStaleRevision  ConfidenceUpdateOutcome = "StaleRevision"
	ConfidenceStatusMismatch ConfidenceUpdateOutcome = "StatusMismatch"
	ConfidenceConflict       ConfidenceUpdateOutcome = "Conflict"
	ConfidenceNotFound       ConfidenceUpdateOutcome = "NotFound"
	ConfidenceDenied         ConfidenceUpdateOutcome = "Denied"
	ConfidenceInvalid        ConfidenceUpdateOutcome = "Invalid"
	ConfidenceDeleted        ConfidenceUpdateOutcome = "Deleted"
	ConfidenceUnverified     ConfidenceUpdateOutcome = "Unverified"
)

// ConfidenceEvidenceFilter is which evidence a confidence read counts (.NET's ConfidenceEvidenceFilter).
type ConfidenceEvidenceFilter string

// The filters.
const (
	FilterAll                ConfidenceEvidenceFilter = "All"
	FilterExcludeHostTrusted ConfidenceEvidenceFilter = "ExcludeHostTrusted"
	FilterVerifiedOnly       ConfidenceEvidenceFilter = "VerifiedOnly"
)

// The history page bounds (.NET's ExperienceRecordHistoryQuery.MinLimit and MaxLimit).
const (
	HistoryMinLimit = 1
	HistoryMaxLimit = 500
)

// AssessmentIDPath is the error path a spent assessment is reported on (.NET's ConfidenceUpdate.AssessmentIdPath).
const AssessmentIDPath = "Confidence.AssessmentId"

// FirstEventStatus is the only status a record's first lifecycle event (the one with no prior status) may record.
const FirstEventStatus = StatusCandidate

// AuthorizationContext is what the host has established the caller may do (.NET's AuthorizationContext): the
// principal (the actor every event and evidence row records, and the reviewer human evidence is counted under) and
// the bounds every store transaction declares.
type AuthorizationContext struct {
	PrincipalID string
	Bounds      AuthorizationBounds
}

// Permits reports whether the authorization permits scope: its tenant is non-blank and equal, and every bound it sets
// is equal (ordinal). A blank tenant permits nothing.
func (a AuthorizationContext) Permits(scope Scope) bool {
	return !isNullOrWhitespace(a.Bounds.TenantID) && a.Bounds.Permits(scope)
}

// Actor is the principal a row records, or nil when it is blank.
func (a AuthorizationContext) Actor() *string {
	if isNullOrWhitespace(a.PrincipalID) {
		return nil
	}
	principal := a.PrincipalID
	return &principal
}

// StoreValidationError is one field a store or the service refused, by its .NET path, with .NET's message.
type StoreValidationError struct {
	Path    string
	Message string
}

// LifecycleEvent is one lifecycle transition as the service stamps it (.NET's LifecycleEvent). PriorStatus is nil
// for a record's first event; Confidence is set for the event that carries evidence.
type LifecycleEvent struct {
	EventID                 UUID
	ExperienceRecordID      UUID
	PriorStatus             *ExperienceStatus
	CurrentStatus           ExperienceStatus
	Reason                  string
	Producer                string
	OccurredAt              Instant
	ExpectedRevision        int64
	ReplacementExperienceID *UUID
	Confidence              *ConfidenceUpdate
}

// Clone is a deep copy: it shares no pointer with e.
func (e LifecycleEvent) Clone() LifecycleEvent {
	if e.PriorStatus != nil {
		prior := *e.PriorStatus
		e.PriorStatus = &prior
	}
	if e.ReplacementExperienceID != nil {
		replacement := *e.ReplacementExperienceID
		e.ReplacementExperienceID = &replacement
	}
	if e.Confidence != nil {
		update := e.Confidence.Clone()
		e.Confidence = &update
	}
	return e
}

// Equal is .NET's record equality: every field, the confidence update's included.
func (e LifecycleEvent) Equal(other LifecycleEvent) bool {
	return e.EventID == other.EventID && e.ExperienceRecordID == other.ExperienceRecordID &&
		equalPointer(e.PriorStatus, other.PriorStatus) && e.CurrentStatus == other.CurrentStatus &&
		e.Reason == other.Reason && e.Producer == other.Producer && e.OccurredAt == other.OccurredAt &&
		e.ExpectedRevision == other.ExpectedRevision &&
		equalPointer(e.ReplacementExperienceID, other.ReplacementExperienceID) &&
		(e.Confidence == nil) == (other.Confidence == nil) &&
		(e.Confidence == nil || e.Confidence.Equal(*other.Confidence))
}

// Clone is a deep copy: it shares no pointer with u.
func (u ConfidenceUpdate) Clone() ConfidenceUpdate {
	u.VerificationRoundID = clonePointer(u.VerificationRoundID)
	u.ReviewerIdentity = clonePointer(u.ReviewerIdentity)
	u.Detail = clonePointer(u.Detail)
	u.AssessmentID = clonePointer(u.AssessmentID)
	u.Admission = clonePointer(u.Admission)
	return u
}

// Equal is .NET's record equality: every field, the assessment and the admission included.
func (u ConfidenceUpdate) Equal(other ConfidenceUpdate) bool {
	return u.EvidenceID == other.EvidenceID && u.Kind == other.Kind && u.Source == other.Source &&
		u.RunID == other.RunID && equalPointer(u.VerificationRoundID, other.VerificationRoundID) &&
		equalPointer(u.ReviewerIdentity, other.ReviewerIdentity) && u.RuleVersion == other.RuleVersion &&
		u.PriorReuseConfidence == other.PriorReuseConfidence && u.NewReuseConfidence == other.NewReuseConfidence &&
		u.PriorSupportingValidations == other.PriorSupportingValidations &&
		u.NewSupportingValidations == other.NewSupportingValidations &&
		u.PriorContradictions == other.PriorContradictions && u.NewContradictions == other.NewContradictions &&
		equalPointer(u.Detail, other.Detail) && equalPointer(u.AssessmentID, other.AssessmentID) &&
		equalPointer(u.Admission, other.Admission)
}

func equalPointer[T comparable](a, b *T) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

func clonePointer[T any](value *T) *T {
	if value == nil {
		return nil
	}
	copied := *value
	return &copied
}

// StoredLifecycleEvent is one committed event as a history reads it back: the event (its OccurredAt truncated to
// whole microseconds), when the store recorded it (its own clock), the revision it produced and the actor (nil for
// none).
type StoredLifecycleEvent struct {
	Event           LifecycleEvent
	RecordedAt      Instant
	AppliedRevision int64
	Actor           *string
}

// LifecycleCommitResult is what a store's commit answered (.NET's ExperienceLifecycleCommitResult): the revision the
// commit produced (or the record's current one, for StaleRevision and StatusMismatch), the status the store reports
// when it did not move the record, every error of an Invalid commit, and the confidence update as stored.
type LifecycleCommitResult struct {
	Outcome           ExperienceStoreOutcome
	Revision          int64
	CurrentStatus     *ExperienceStatus
	Errors            []StoreValidationError
	AppliedConfidence *ConfidenceUpdate
}

// HistoryQuery is one page of a record's history: Limit (HistoryMinLimit to HistoryMaxLimit) events whose applied
// revision is after StartAfterRevision (nil: from the first).
type HistoryQuery struct {
	Scope              Scope
	ExperienceID       UUID
	Limit              int
	StartAfterRevision *int64
}

// HistoryResult is one page of a history: the record's revision, the events oldest first, and the cursor for the next
// page (nil when the page is empty).
type HistoryResult struct {
	Outcome                ExperienceStoreOutcome
	Revision               int64
	Events                 []StoredLifecycleEvent
	Errors                 []StoreValidationError
	NextStartAfterRevision *int64
}

// SupersessionCheckResult is what a supersession check answered, with the replacement's status when it found it.
type SupersessionCheckResult struct {
	Outcome           SupersessionOutcome
	ReplacementStatus *ExperienceStatus
	Errors            []StoreValidationError
}

// RecordReadResult is one record read in exactly a scope: Found with the record, or why not.
type RecordReadResult struct {
	Outcome ExperienceStoreOutcome
	Record  *ExperienceRecord
	Errors  []StoreValidationError
}

// LifecycleStore is what the lifecycle service needs of a store: the in-memory store and the PostgreSQL store both
// are one. Each operation validates, then checks the authorization, before it touches anything; every refusal is an
// outcome, and an error is an infrastructure failure (or the context's) only.
type LifecycleStore interface {
	// ReadRecord reads one record in exactly scope.
	ReadRecord(ctx context.Context, authorization AuthorizationContext, scope Scope, experienceID UUID) (RecordReadResult, error)
	// CommitLifecycleEvent commits one event and the record's projection atomically: the revision compare-and-set,
	// idempotency by event ID and evidence ID, the independence key, the single-use assessment and the supersession
	// gate.
	CommitLifecycleEvent(ctx context.Context, authorization AuthorizationContext, scope Scope,
		event LifecycleEvent) (LifecycleCommitResult, error)
	// GetHistory reads one page of a record's history.
	GetHistory(ctx context.Context, authorization AuthorizationContext, query HistoryQuery) (HistoryResult, error)
	// CheckSupersession reports whether replacementID may replace experienceID in exactly scope.
	CheckSupersession(ctx context.Context, authorization AuthorizationContext, scope Scope, experienceID,
		replacementID UUID) (SupersessionCheckResult, error)
}

// ErrStoreContract is a store answer the lifecycle cannot interpret (.NET's ExperienceStoreException for a store that
// broke its contract): an outcome the operation cannot produce, or a history cursor that does not move forward.
var ErrStoreContract = errors.New("the store broke its contract")

func storeContractError(format string, args ...any) error {
	return fmt.Errorf("%w: "+format, append([]any{ErrStoreContract}, args...)...)
}

// ---- The service's requests and results -------------------------------------------------------------------------

// CommitLifecycleTransitionRequest is one transition to commit (.NET's CommitLifecycleTransitionRequest).
// PriorStatus is nil for a record's first event; ReplacementExperienceID names the replacement of a supersession.
type CommitLifecycleTransitionRequest struct {
	EventID                 UUID
	ExperienceID            UUID
	Scope                   Scope
	PriorStatus             *ExperienceStatus
	CurrentStatus           ExperienceStatus
	Reason                  string
	Producer                string
	OccurredAt              Instant
	ExpectedRevision        int64
	ReplacementExperienceID *UUID
}

// CommitLifecycleTransitionResult is what a transition commit answered (.NET's CommitLifecycleTransitionResult): the
// event the service stamped (nil when it refused before stamping one), the store's revision and status, its errors,
// and the reason for a refusal the service phrased.
type CommitLifecycleTransitionResult struct {
	Outcome       LifecycleTransitionOutcome
	Event         *LifecycleEvent
	Revision      int64
	CurrentStatus *ExperienceStatus
	Errors        []StoreValidationError
	Reason        *string
}

// ApplyConfidenceEvidenceRequest is one piece of reuse evidence to apply (.NET's ApplyConfidenceEvidenceRequest).
// AssessmentToken is a bearer credential: String and GoString never print it.
type ApplyConfidenceEvidenceRequest struct {
	EventID             UUID
	ExperienceID        UUID
	Scope               Scope
	EvidenceID          UUID
	Kind                ConfidenceEvidenceKind
	Source              ConfidenceEvidenceSource
	RunID               UUID
	VerificationRoundID *UUID
	Reason              string
	Producer            string
	OccurredAt          Instant
	Detail              *string
	AssessmentToken     *string
}

// String redacts the assessment token.
func (r ApplyConfidenceEvidenceRequest) String() string {
	token := "null"
	if r.AssessmentToken != nil {
		token = "<redacted>"
	}
	r.AssessmentToken = nil
	type plain ApplyConfidenceEvidenceRequest
	return fmt.Sprintf("%+v (AssessmentToken: %s)", plain(r), token)
}

// GoString redacts the assessment token.
func (r ApplyConfidenceEvidenceRequest) GoString() string { return r.String() }

// ApplyConfidenceEvidenceResult is what applying evidence answered (.NET's ApplyConfidenceEvidenceResult): the event
// the service stamped, the update as the transaction stored it, the revision and status, the store's errors, the
// reason for a refusal the service phrased, and the independence refusal for Unverified.
type ApplyConfidenceEvidenceResult struct {
	Outcome  ConfidenceUpdateOutcome
	Event    *LifecycleEvent
	Update   *ConfidenceUpdate
	Revision int64
	Status   *ExperienceStatus
	Errors   []StoreValidationError
	Reason   *string
	Refusal  *IndependenceRefusal
	// RecordedOnEvent reports whether an uncounted, applied update was stored on a lifecycle event (host-trusted
	// evidence recorded only) rather than as a duplicate with no event, as the store reported it (.NET's internal
	// ApplyConfidenceEvidenceResult.RecordedOnEvent, contract 1.17).
	RecordedOnEvent bool
}

// Counted reports whether the update moved a counter.
func (r ApplyConfidenceEvidenceResult) Counted() bool { return r.Update != nil && r.Update.Counted() }

// ConfidenceAdmissionCounts are a supporting and a contradicting count.
type ConfidenceAdmissionCounts struct {
	Supporting    int64
	Contradicting int64
}

// ConfidenceReport is a record's confidence as a read with a filter counts it (.NET's ConfidenceReport).
type ConfidenceReport struct {
	ExperienceID                UUID
	Revision                    int64
	Status                      ExperienceStatus
	Filter                      ConfidenceEvidenceFilter
	ReuseConfidence             float64
	SupportingValidations       int64
	Contradictions              int64
	StoredReuseConfidence       float64
	StoredSupportingValidations int64
	StoredContradictions        int64
	Verified                    ConfidenceAdmissionCounts
	HostTrusted                 ConfidenceAdmissionCounts
	Unrecorded                  ConfidenceAdmissionCounts
	Origin                      Origin
	Initial                     ConfidenceAdmissionCounts
	HostTrustedRecordedOnly     ConfidenceAdmissionCounts
}

// ConfidenceReadResult is what a confidence read answered: the report when the record was Found.
type ConfidenceReadResult struct {
	Outcome ExperienceStoreOutcome
	Report  *ConfidenceReport
	Errors  []StoreValidationError
}
