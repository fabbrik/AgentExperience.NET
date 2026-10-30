package inmemory

// Contract 1.16: the lifecycle store operations, exactly as .NET's InMemoryExperienceRecordStore runs them, under the
// store's mutex: the scoped read, the commit (the revision compare-and-set, idempotency by event ID and by evidence ID,
// the independence key, the single-use assessment and the supersession gate, in .NET's in-memory store's order: the
// supersession gate before the revision check, where the PostgreSQL store asks it after the event insert), a
// history page and the supersession check. A commit stamps the store's clock (Clock, or SetClock), truncated to whole
// microseconds, on the event and (unless the evidence is recorded only) on the record.

import (
	"context"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// storedEvent is one committed lifecycle event, with what only the store knows about it.
type storedEvent struct {
	event           ae.LifecycleEvent
	scope           ae.Scope
	appliedRevision int64
	recordedAt      ae.Instant
	actor           *string
}

// storedEvidence is one piece of confidence evidence in the ledger, counted or recorded only.
type storedEvidence struct {
	experienceID    ae.UUID
	eventID         *ae.UUID
	update          ae.ConfidenceUpdate
	appliedRevision int64
	appliedStatus   ae.ExperienceStatus
}

type independenceKey struct {
	experienceID ae.UUID
	key          string
}

type spentAssessment struct {
	experienceID ae.UUID
	assessmentID ae.UUID
}

// lifecycleState is the event log, the evidence ledger and the key sets.
type lifecycleState struct {
	eventsByID       map[ae.UUID]*storedEvent
	history          map[ae.UUID][]*storedEvent
	evidence         map[ae.UUID]*storedEvidence
	countedKeys      map[independenceKey]bool
	recordedOnlyKeys map[independenceKey]bool
	spent            map[spentAssessment]bool
}

func newLifecycleState() lifecycleState {
	return lifecycleState{eventsByID: map[ae.UUID]*storedEvent{}, history: map[ae.UUID][]*storedEvent{},
		evidence: map[ae.UUID]*storedEvidence{}, countedKeys: map[independenceKey]bool{},
		recordedOnlyKeys: map[independenceKey]bool{}, spent: map[spentAssessment]bool{}}
}

// SetClock replaces the store's clock (nil: agentexperience.Now) while the store is in use.
func (s *Store) SetClock(clock func() ae.Instant) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.Clock = clock
}

// nowLocked is the store's clock, truncated to whole microseconds; the caller holds the mutex.
func (s *Store) nowLocked() ae.Instant {
	clock := s.Clock
	if clock == nil {
		clock = ae.Now
	}
	return clock().TruncateToMicroseconds()
}

// now is the store's clock, truncated to whole microseconds.
func (s *Store) now() ae.Instant {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.nowLocked()
}

// inScopeLocked is the record held in exactly scope; the caller holds the mutex.
func (s *Store) inScopeLocked(scope ae.Scope, id ae.UUID) (*indexed, bool) {
	held, ok := s.records[id]
	if !ok || !held.record.Scope.Equal(scope) {
		return nil, false
	}
	return held, true
}

func noErrors() []ae.StoreValidationError { return []ae.StoreValidationError{} }

// ReadRecord reads one record in exactly scope: Invalid (ValidateGet), Denied, NotFound (missing, in another scope,
// or erased) or Found with a copy.
func (s *Store) ReadRecord(ctx context.Context, authorization ae.AuthorizationContext, scope ae.Scope,
	experienceID ae.UUID) (ae.RecordReadResult, error) {
	if errors := ae.ValidateGet(scope, experienceID); len(errors) > 0 {
		return ae.RecordReadResult{Outcome: ae.StoreInvalid, Errors: errors}, nil
	}
	if !authorization.Permits(scope) {
		return ae.RecordReadResult{Outcome: ae.StoreDenied, Errors: noErrors()}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.RecordReadResult{}, err
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	held, ok := s.inScopeLocked(scope, experienceID)
	if !ok {
		return ae.RecordReadResult{Outcome: ae.StoreNotFound, Errors: noErrors()}, nil
	}
	record := mustClone(held.record)
	return ae.RecordReadResult{Outcome: ae.StoreFound, Record: &record, Errors: noErrors()}, nil
}

// CommitLifecycleEvent commits one event and the record's projection atomically, as .NET's in-memory store does.
func (s *Store) CommitLifecycleEvent(ctx context.Context, authorization ae.AuthorizationContext, scope ae.Scope,
	event ae.LifecycleEvent) (ae.LifecycleCommitResult, error) {
	if errors := ae.ValidateLifecycleEvent(scope, event, authorization); len(errors) > 0 {
		return ae.LifecycleCommitResult{Outcome: ae.StoreInvalid, Errors: errors}, nil
	}
	if !authorization.Permits(scope) {
		return ae.LifecycleCommitResult{Outcome: ae.StoreDenied, Errors: noErrors()}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.LifecycleCommitResult{}, err
	}
	// Compared and stored with OccurredAt truncated, as the PostgreSQL store stores and compares it.
	event = event.Clone()
	event.OccurredAt = event.OccurredAt.TruncateToMicroseconds()
	s.mu.Lock()
	defer s.mu.Unlock()
	// Cancelled while waiting for the store: nothing is written. Once decided, a commit is applied whole.
	if err := ctx.Err(); err != nil {
		return ae.LifecycleCommitResult{}, err
	}
	return s.commitLocked(authorization, scope, event), nil
}

func (s *Store) commitLocked(authorization ae.AuthorizationContext, scope ae.Scope,
	event ae.LifecycleEvent) ae.LifecycleCommitResult {
	recordID := event.ExperienceRecordID
	if submitted := event.Confidence; submitted != nil {
		if stored, ok := s.lifecycle.evidence[submitted.EvidenceID]; ok {
			return s.compareStoredEvidenceLocked(scope, event, *submitted, stored)
		}
		if submitted.AssessmentID != nil && s.lifecycle.spent[spentAssessment{recordID, *submitted.AssessmentID}] {
			return ae.LifecycleCommitResult{Outcome: ae.StoreConflict, Errors: []ae.StoreValidationError{{
				Path:    ae.AssessmentIDPath,
				Message: "this assessment has already landed evidence for this record under another evidence ID.",
			}}}
		}
		key := independenceKey{recordID, ae.StoredIndependenceKey(*submitted)}
		if s.lifecycle.countedKeys[key] || (!submitted.Counted() && s.lifecycle.recordedOnlyKeys[key]) {
			return s.recordDuplicateLocked(scope, event, *submitted)
		}
	}
	if stored, ok := s.lifecycle.eventsByID[event.EventID]; ok {
		return compareStoredEvent(scope, event, stored)
	}
	held, ok := s.inScopeLocked(scope, recordID)
	if !ok {
		return ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}
	}
	// After replay detection, so retrying a committed supersession reports its original outcome even once the
	// replacement has moved on.
	if event.ReplacementExperienceID != nil {
		check := s.checkSupersessionLocked(scope, recordID, *event.ReplacementExperienceID)
		if check.Outcome != ae.SupersessionAllowed || check.ReplacementStatus == nil ||
			!ae.IsEligible(*check.ReplacementStatus) {
			return ae.LifecycleCommitResult{Outcome: ae.StoreReplacementNotAllowed,
				CurrentStatus: check.ReplacementStatus, Errors: noErrors()}
		}
	}
	record := held.record
	if record.Revision != event.ExpectedRevision {
		return ae.LifecycleCommitResult{Outcome: ae.StoreStaleRevision, Revision: record.Revision, Errors: noErrors()}
	}
	expectedStatus := event.CurrentStatus
	if event.PriorStatus != nil {
		expectedStatus = *event.PriorStatus
	}
	if record.Status != expectedStatus {
		status := record.Status
		return ae.LifecycleCommitResult{Outcome: ae.StoreStatusMismatch, Revision: record.Revision,
			CurrentStatus: &status, Errors: noErrors()}
	}

	now := s.nowLocked()
	applied := event.ExpectedRevision + 1
	recordedOnly := event.Confidence != nil && !event.Confidence.Counted()
	moved := mustClone(record)
	moved.Status = event.CurrentStatus
	moved.Revision = applied
	if !recordedOnly {
		moved.UpdatedAt = now
	}
	if event.Confidence != nil && event.Confidence.Counted() {
		moved.ReuseConfidence = event.Confidence.NewReuseConfidence
		moved.SupportingValidations = event.Confidence.NewSupportingValidations
		moved.Contradictions = event.Confidence.NewContradictions
	}
	// A new snapshot with the same words: a search may still be reading the old one outside the mutex.
	s.records[recordID] = &indexed{record: moved, taskID: held.taskID, summary: held.summary, lesson: held.lesson}

	stored := &storedEvent{event: event.Clone(), scope: scope, appliedRevision: applied, recordedAt: now,
		actor: authorization.Actor()}
	s.lifecycle.eventsByID[event.EventID] = stored
	s.lifecycle.history[recordID] = append(s.lifecycle.history[recordID], stored)
	if event.Confidence != nil {
		eventID := event.EventID
		s.addEvidenceLocked(recordID, &eventID, *event.Confidence, applied, event.CurrentStatus)
	}
	var confidence *ae.ConfidenceUpdate
	if event.Confidence != nil {
		update := event.Confidence.Clone()
		confidence = &update
	}
	return ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: applied, Errors: noErrors(),
		AppliedConfidence: confidence}
}

// recordDuplicateLocked records a submission whose independence key was already taken: a ledger entry and nothing
// else (no event, counters, status, revision or UpdatedAt).
func (s *Store) recordDuplicateLocked(scope ae.Scope, event ae.LifecycleEvent,
	submitted ae.ConfidenceUpdate) ae.LifecycleCommitResult {
	held, ok := s.inScopeLocked(scope, event.ExperienceRecordID)
	if !ok {
		return ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}
	}
	record := held.record
	if record.Revision != event.ExpectedRevision {
		return ae.LifecycleCommitResult{Outcome: ae.StoreStaleRevision, Revision: record.Revision, Errors: noErrors()}
	}
	status := record.Status
	if event.PriorStatus != nil && record.Status != *event.PriorStatus {
		return ae.LifecycleCommitResult{Outcome: ae.StoreStatusMismatch, Revision: record.Revision,
			CurrentStatus: &status, Errors: noErrors()}
	}
	recordedOnly := submitted.AsRecordedOnly()
	s.addEvidenceLocked(record.ExperienceID, nil, recordedOnly, record.Revision, record.Status)
	stored := recordedOnly.Clone()
	return ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: record.Revision, CurrentStatus: &status,
		Errors: noErrors(), AppliedConfidence: &stored}
}

func (s *Store) addEvidenceLocked(recordID ae.UUID, eventID *ae.UUID, update ae.ConfidenceUpdate, applied int64,
	status ae.ExperienceStatus) {
	s.lifecycle.evidence[update.EvidenceID] = &storedEvidence{experienceID: recordID, eventID: eventID,
		update: update.Clone(), appliedRevision: applied, appliedStatus: status}
	key := independenceKey{recordID, ae.StoredIndependenceKey(update)}
	if update.Counted() {
		s.lifecycle.countedKeys[key] = true
	} else if eventID != nil {
		s.lifecycle.recordedOnlyKeys[key] = true
	}
	if update.AssessmentID != nil {
		s.lifecycle.spent[spentAssessment{recordID, *update.AssessmentID}] = true
	}
}

// compareStoredEvidenceLocked decides a resubmitted evidence ID: the same evidence about the same observation is the
// original replayed, with the numbers it was stored with; anything else, or evidence for a record outside this scope,
// is a conflict.
func (s *Store) compareStoredEvidenceLocked(scope ae.Scope, event ae.LifecycleEvent, submitted ae.ConfidenceUpdate,
	stored *storedEvidence) ae.LifecycleCommitResult {
	_, inScope := s.inScopeLocked(scope, stored.experienceID)
	if !inScope || !ae.SameStoredEvidence(event, submitted, stored.experienceID, stored.eventID, stored.update) {
		return ae.LifecycleCommitResult{Outcome: ae.StoreConflict, Errors: noErrors()}
	}
	replayed := ae.EvidenceReplay(submitted, stored.update)
	status := stored.appliedStatus
	return ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: stored.appliedRevision, CurrentStatus: &status,
		Errors: noErrors(), AppliedConfidence: &replayed}
}

// compareStoredEvent decides a resubmitted event ID: the same event, every field and the scope included (the
// confidence admission aside), is the original commit replayed; anything else is a conflict.
func compareStoredEvent(scope ae.Scope, event ae.LifecycleEvent, stored *storedEvent) ae.LifecycleCommitResult {
	if stored.event.ExperienceRecordID != event.ExperienceRecordID || !stored.scope.Equal(scope) ||
		!stored.event.Equal(ae.ReplayedEvent(event, stored.event)) {
		return ae.LifecycleCommitResult{Outcome: ae.StoreConflict, Errors: noErrors()}
	}
	var confidence *ae.ConfidenceUpdate
	if stored.event.Confidence != nil {
		update := stored.event.Confidence.Clone()
		confidence = &update
	}
	return ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: stored.appliedRevision, Errors: noErrors(),
		AppliedConfidence: confidence}
}

// GetHistory reads one page of a record's history, oldest first: Invalid (ValidateHistoryQuery), Denied, NotFound
// (missing or in another scope) or Found with the record's revision.
func (s *Store) GetHistory(ctx context.Context, authorization ae.AuthorizationContext,
	query ae.HistoryQuery) (ae.HistoryResult, error) {
	if errors := ae.ValidateHistoryQuery(query); len(errors) > 0 {
		return ae.HistoryResult{Outcome: ae.StoreInvalid, Events: []ae.StoredLifecycleEvent{}, Errors: errors}, nil
	}
	if !authorization.Permits(query.Scope) {
		return ae.HistoryResult{Outcome: ae.StoreDenied, Events: []ae.StoredLifecycleEvent{}, Errors: noErrors()}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.HistoryResult{}, err
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	held, ok := s.inScopeLocked(query.Scope, query.ExperienceID)
	if !ok {
		return ae.HistoryResult{Outcome: ae.StoreNotFound, Events: []ae.StoredLifecycleEvent{}, Errors: noErrors()}, nil
	}
	events := []ae.StoredLifecycleEvent{}
	for _, stored := range s.lifecycle.history[query.ExperienceID] {
		if len(events) == query.Limit {
			break
		}
		if query.StartAfterRevision != nil && stored.appliedRevision <= *query.StartAfterRevision {
			continue
		}
		events = append(events, ae.StoredLifecycleEvent{Event: stored.event.Clone(), RecordedAt: stored.recordedAt,
			AppliedRevision: stored.appliedRevision, Actor: copyOf(stored.actor)})
	}
	result := ae.HistoryResult{Outcome: ae.StoreFound, Revision: held.record.Revision, Events: events, Errors: noErrors()}
	if len(events) > 0 {
		next := events[len(events)-1].AppliedRevision
		result.NextStartAfterRevision = &next
	}
	return result, nil
}

// CheckSupersession reports whether replacementID may replace experienceID in exactly scope: both there, and the
// replacement not on a replacement chain (this scope's events only) leading back to the record.
func (s *Store) CheckSupersession(ctx context.Context, authorization ae.AuthorizationContext, scope ae.Scope,
	experienceID, replacementID ae.UUID) (ae.SupersessionCheckResult, error) {
	if errors := ae.ValidateSupersessionCheck(scope, experienceID, replacementID); len(errors) > 0 {
		return ae.SupersessionCheckResult{Outcome: ae.SupersessionInvalid, Errors: errors}, nil
	}
	if !authorization.Permits(scope) {
		return ae.SupersessionCheckResult{Outcome: ae.SupersessionDenied, Errors: noErrors()}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.SupersessionCheckResult{}, err
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.checkSupersessionLocked(scope, experienceID, replacementID), nil
}

func (s *Store) checkSupersessionLocked(scope ae.Scope, experienceID, replacementID ae.UUID) ae.SupersessionCheckResult {
	if _, ok := s.inScopeLocked(scope, experienceID); !ok {
		return ae.SupersessionCheckResult{Outcome: ae.SupersessionRecordNotFound, Errors: noErrors()}
	}
	replacement, ok := s.inScopeLocked(scope, replacementID)
	if !ok {
		return ae.SupersessionCheckResult{Outcome: ae.SupersessionReplacementNotFound, Errors: noErrors()}
	}
	reached := map[ae.UUID]bool{replacementID: true}
	pending := []ae.UUID{replacementID}
	for len(pending) > 0 {
		current := pending[0]
		pending = pending[1:]
		for _, stored := range s.lifecycle.history[current] {
			if next := stored.event.ReplacementExperienceID; next != nil && stored.scope.Equal(scope) && !reached[*next] {
				reached[*next] = true
				pending = append(pending, *next)
			}
		}
	}
	status := replacement.record.Status
	outcome := ae.SupersessionAllowed
	if reached[experienceID] {
		outcome = ae.SupersessionCycle
	}
	return ae.SupersessionCheckResult{Outcome: outcome, ReplacementStatus: &status, Errors: noErrors()}
}

// History is a record's whole history, oldest first, regardless of scope: for inspection in tests.
func (s *Store) History(id ae.UUID) []ae.StoredLifecycleEvent {
	s.mu.RLock()
	defer s.mu.RUnlock()
	events := make([]ae.StoredLifecycleEvent, 0, len(s.lifecycle.history[id]))
	for _, stored := range s.lifecycle.history[id] {
		events = append(events, ae.StoredLifecycleEvent{Event: stored.event.Clone(), RecordedAt: stored.recordedAt,
			AppliedRevision: stored.appliedRevision, Actor: copyOf(stored.actor)})
	}
	return events
}

// eraseLifecycleLocked drops an erased record's events and evidence, as the PostgreSQL erasure does.
func (s *Store) eraseLifecycleLocked(id ae.UUID) {
	for _, stored := range s.lifecycle.history[id] {
		delete(s.lifecycle.eventsByID, stored.event.EventID)
	}
	delete(s.lifecycle.history, id)
	for evidenceID, stored := range s.lifecycle.evidence {
		if stored.experienceID == id {
			delete(s.lifecycle.evidence, evidenceID)
		}
	}
	for _, keys := range []map[independenceKey]bool{s.lifecycle.countedKeys, s.lifecycle.recordedOnlyKeys} {
		for key := range keys {
			if key.experienceID == id {
				delete(keys, key)
			}
		}
	}
	for key := range s.lifecycle.spent {
		if key.experienceID == id {
			delete(s.lifecycle.spent, key)
		}
	}
}

func copyOf[T any](value *T) *T {
	if value == nil {
		return nil
	}
	copied := *value
	return &copied
}

var _ ae.LifecycleStore = (*Store)(nil)
