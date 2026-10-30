package inmemory

// Contract 1.17: the reuse-feedback ledger in memory, exactly as .NET's InMemoryExperienceReuseFeedbackStore keeps it.
// For development and tests. It is independent of the record store and checks nothing about the exposed records; a
// submission is validated with the ledger's own rules (agentexperience.ValidateReuseFeedback), then authorized, then
// stored as a snapshot with its exposures in record order and its instants truncated to whole microseconds.

import (
	"context"
	"sort"
	"sync"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ReuseFeedbackLedger is an in-memory agentexperience.ReuseFeedbackStore. Safe for concurrent use: each submission is
// recorded atomically under one lock.
type ReuseFeedbackLedger struct {
	mu          sync.Mutex
	submissions map[ae.UUID]ae.RecordedExperienceReuseFeedback
}

// NewReuseFeedbackLedger is an empty ledger.
func NewReuseFeedbackLedger() *ReuseFeedbackLedger {
	return &ReuseFeedbackLedger{submissions: map[ae.UUID]ae.RecordedExperienceReuseFeedback{}}
}

var _ ae.ReuseFeedbackStore = (*ReuseFeedbackLedger)(nil)

// RecordReuseFeedback writes one submission and all of its exposures, or nothing: Invalid (every error), Denied, then
// the feedback ID as the idempotency key: an identical resubmission is AlreadyRecorded with the stored one, a
// differing one Conflict, with the stored one only when the authorization permits its scope.
func (l *ReuseFeedbackLedger) RecordReuseFeedback(ctx context.Context, authorization ae.AuthorizationContext,
	feedback ae.RecordedExperienceReuseFeedback) (ae.ReuseFeedbackStoreResult, error) {
	if errors := ae.ValidateReuseFeedback(feedback); len(errors) > 0 {
		return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerInvalid, Errors: errors}, nil
	}
	if !authorization.Permits(*feedback.Scope) {
		return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerDenied, Errors: []ae.StoreValidationError{}}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.ReuseFeedbackStoreResult{}, err
	}
	submitted := ae.StoredReuseFeedback(ae.NormalizeReuseFeedback(feedback))

	l.mu.Lock()
	defer l.mu.Unlock()
	if stored, ok := l.submissions[submitted.FeedbackID]; ok {
		if ae.ReuseFeedbackMatches(stored, submitted, false) {
			copied := ae.CloneReuseFeedback(stored)
			return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerAlreadyRecorded, Feedback: &copied,
				Errors: []ae.StoreValidationError{}}, nil
		}
		result := ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerConflict, Errors: []ae.StoreValidationError{}}
		if authorization.Permits(*stored.Scope) {
			copied := ae.CloneReuseFeedback(stored)
			result.Feedback = &copied
		}
		return result, nil
	}
	l.submissions[submitted.FeedbackID] = submitted
	copied := ae.CloneReuseFeedback(submitted)
	return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerRecorded, Feedback: &copied,
		Errors: []ae.StoreValidationError{}}, nil
}

// Submissions is every submission the ledger holds, as it stores them, by feedback ID (its text order): for tests.
func (l *ReuseFeedbackLedger) Submissions() []ae.RecordedExperienceReuseFeedback {
	l.mu.Lock()
	defer l.mu.Unlock()
	list := make([]ae.RecordedExperienceReuseFeedback, 0, len(l.submissions))
	for _, stored := range l.submissions {
		list = append(list, ae.CloneReuseFeedback(stored))
	}
	sort.Slice(list, func(i, j int) bool { return ae.CompareUUIDs(list[i].FeedbackID, list[j].FeedbackID) < 0 })
	return list
}
