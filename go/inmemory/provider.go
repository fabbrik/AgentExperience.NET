package inmemory

import (
	"context"
	"slices"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// providerHoldoutKey is a provider holdout row's key: the tenant, the run and the provider key, as the table's.
type providerHoldoutKey struct {
	tenant string
	run    ae.UUID
	key    string
}

// ProviderHoldoutRow is one provider holdout assignment row (contract 1.11): a run's arm under a provider key, the
// scope the run was captured in, and when, as agent_experience.experience_provider_holdout_assignments holds it.
type ProviderHoldoutRow struct {
	RunID       ae.UUID
	ProviderKey string
	Arm         ae.HoldoutArm
	Scope       ae.Scope
	AssignedAt  ae.Instant
}

// RecordProviderHoldoutAssignment records a run's arm under a provider key in scope, as
// sql/record-provider-holdout-assignment.sql does: one row per tenant, run and key, its scope exactly scope (no record
// is read for it); a row already there for them is kept as it is, whatever its scope; the instant, truncated to whole
// microseconds, is clamped to the store's Clock. Nothing is written for a run that has records in scope's tenant and
// all of them are erased (the late-write guard). It refuses a done context, an assignment Validate refuses, and a
// scope outside the bounds (ErrAuthorizationDenied).
func (s *Store) RecordProviderHoldoutAssignment(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	assignment ae.ProviderHoldoutAssignment) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if err := assignment.Validate(); err != nil {
		return err
	}
	if !Permits(bounds, scope) {
		return ErrAuthorizationDenied
	}
	clock := s.Clock
	if clock == nil {
		clock = ae.Now
	}
	at, now := assignment.AssignedAt.TruncateToMicroseconds(), clock().TruncateToMicroseconds()
	if now.Before(at) {
		at = now
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.runErased(scope.TenantID, assignment.RunID) {
		return nil
	}
	key := providerHoldoutKey{tenant: scope.TenantID, run: assignment.RunID, key: assignment.ProviderKey}
	if _, exists := s.providerHoldout[key]; exists {
		return nil
	}
	s.providerHoldout[key] = ProviderHoldoutRow{RunID: assignment.RunID, ProviderKey: assignment.ProviderKey,
		Arm: assignment.Arm, Scope: copyScope(scope), AssignedAt: at}
	return nil
}

var _ ae.ProviderHoldoutRecorder = (*Store)(nil)

// copyScope is scope with its optional fields copied, so the caller's pointers are not kept.
func copyScope(scope ae.Scope) ae.Scope {
	copied := scope
	for _, field := range []**string{&copied.TeamID, &copied.AgentID, &copied.UserID} {
		if *field != nil {
			value := **field
			*field = &value
		}
	}
	return copied
}

// ProviderHoldoutRows are every provider holdout row, ordered by tenant, run then key: for inspection in tests.
func (s *Store) ProviderHoldoutRows() []ProviderHoldoutRow {
	s.mu.RLock()
	defer s.mu.RUnlock()
	rows := make([]ProviderHoldoutRow, 0, len(s.providerHoldout))
	for _, row := range s.providerHoldout {
		rows = append(rows, row)
	}
	slices.SortFunc(rows, func(a, b ProviderHoldoutRow) int {
		if a.Scope.TenantID != b.Scope.TenantID {
			if a.Scope.TenantID < b.Scope.TenantID {
				return -1
			}
			return 1
		}
		if c := compareUUID(a.RunID, b.RunID); c != 0 {
			return c
		}
		switch {
		case a.ProviderKey < b.ProviderKey:
			return -1
		case a.ProviderKey > b.ProviderKey:
			return 1
		}
		return 0
	})
	return rows
}

// QueryProviderHoldoutOutcomes reads one page of a provider's holdout rows (contract 1.11), as
// sql/query-provider-holdout-outcomes.sql does: the key's rows in exactly scope, in run order (by the run IDs' bytes),
// after afterRunID (from the start when nil), at most limit, each with the outcome of its run's own record (the record
// held in exactly the row's scope whose source run is the run; the earliest created, then the lowest ID). It never
// writes. It refuses a done context, a limit outside 1 to 1000, a malformed key, and a scope outside the bounds
// (ErrAuthorizationDenied).
func (s *Store) QueryProviderHoldoutOutcomes(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	providerKey string, limit int, afterRunID *ae.UUID) ([]ae.ProviderHoldoutOutcomeRow, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if err := ae.ValidateHoldoutOutcomesLimit(limit); err != nil {
		return nil, err
	}
	if err := ae.ValidateProviderKey(providerKey); err != nil {
		return nil, err
	}
	if !Permits(bounds, scope) {
		return nil, ErrAuthorizationDenied
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	page := make([]ProviderHoldoutRow, 0)
	for _, row := range s.providerHoldout {
		if row.ProviderKey != providerKey || !row.Scope.Equal(scope) {
			continue
		}
		if afterRunID != nil && compareUUID(row.RunID, *afterRunID) <= 0 {
			continue
		}
		page = append(page, row)
	}
	slices.SortFunc(page, func(a, b ProviderHoldoutRow) int { return compareUUID(a.RunID, b.RunID) })
	if len(page) > limit {
		page = page[:limit]
	}
	rows := make([]ae.ProviderHoldoutOutcomeRow, 0, len(page))
	for _, row := range page {
		outcome := ae.ProviderHoldoutOutcomeRow{RunID: row.RunID, ProviderKey: row.ProviderKey, Arm: row.Arm,
			AssignedAt: row.AssignedAt}
		if run := s.runRecord(row.RunID, row.Scope); run != nil {
			status := run.Outcome.Status
			outcome.RunOutcome = &status
		}
		rows = append(rows, outcome)
	}
	return rows, nil
}

var _ ae.ProviderHoldoutOutcomeReader = (*Store)(nil)
