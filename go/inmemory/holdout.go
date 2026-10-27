package inmemory

import (
	"context"
	"slices"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// holdoutKey is a holdout row's key: the run and the record its block named.
type holdoutKey struct {
	run, experience ae.UUID
}

// tombstone is what is left of an erased record: its tenant and the run it was captured from, which the holdout's
// late-write guard reads. (PostgreSQL's tombstone zeroes its run; an in-memory one keeps it, so the guard applies to a
// run whose own records were all erased, whenever the erasure happened.)
type tombstone struct {
	tenant string
	run    ae.UUID
}

// HoldoutRow is one holdout assignment row (contract 1.8): a run, a record its block named at the revision it was
// rendered from, the arm, the record's own scope, and when, as agent_experience.experience_holdout_assignments holds
// it.
type HoldoutRow struct {
	RunID        ae.UUID
	ExperienceID ae.UUID
	Revision     int64
	Arm          ae.HoldoutArm
	Scope        ae.Scope
	AssignedAt   ae.Instant
}

// RecordHoldoutAssignment records a run's holdout assignment in scope, as sql/record-holdout-assignment.sql does: one
// row per named record held live in exactly scope, at the revision named (a record named twice counts once, at its
// first revision); a row already there for the run and record is kept as it is; the instant, truncated to whole
// microseconds, is clamped to the store's Clock. Nothing is written for a run whose own records (those captured from
// the run, in scope's tenant) are all erased. It refuses a done context, an assignment HoldoutRecords refuses, and a
// scope outside the bounds (ErrAuthorizationDenied).
func (s *Store) RecordHoldoutAssignment(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	assignment ae.HoldoutAssignment) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	records, err := ae.HoldoutRecords(assignment)
	if err != nil {
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
	for _, named := range records {
		held, ok := s.records[named.ExperienceID]
		if !ok || !held.record.Scope.Equal(scope) {
			continue
		}
		key := holdoutKey{assignment.RunID, named.ExperienceID}
		if _, exists := s.holdout[key]; exists {
			continue
		}
		s.holdout[key] = HoldoutRow{RunID: assignment.RunID, ExperienceID: named.ExperienceID, Revision: named.Revision,
			Arm: assignment.Arm, Scope: held.record.Scope, AssignedAt: at}
	}
	return nil
}

// runErased reports whether the run has records in the tenant and every one of them is erased: the late-write guard.
func (s *Store) runErased(tenant string, run ae.UUID) bool {
	for _, held := range s.records {
		if held.record.Scope.TenantID == tenant && held.record.SourceRunID == run {
			return false
		}
	}
	for _, erased := range s.tombstones {
		if erased.tenant == tenant && erased.run == run {
			return true
		}
	}
	return false
}

// erased reports whether a record with this ID was erased.
func (s *Store) erased(id ae.UUID) bool {
	_, ok := s.tombstones[id]
	return ok
}

// Erase erases a record, as the .NET erasure (agent_experience.purge_experience_record) does as the schema's owner,
// for tests: the record is no longer read, searched, listed or recorded, and its ID can never be stored again. Its
// injection summary and embedding go, and so do its holdout rows: every row that names it, and, when no other live
// record of its run remains in its tenant, every row of that run in its tenant. It answers false for an ID it does not
// hold.
func (s *Store) Erase(id ae.UUID) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	held, ok := s.records[id]
	if !ok {
		return false
	}
	record := held.record
	delete(s.records, id)
	s.order = slices.DeleteFunc(s.order, func(other ae.UUID) bool { return other == id })
	delete(s.injections, id)
	delete(s.embeddings, id)
	s.tombstones[id] = tombstone{tenant: record.Scope.TenantID, run: record.SourceRunID}
	runLive := false
	for _, other := range s.records {
		if other.record.Scope.TenantID == record.Scope.TenantID && other.record.SourceRunID == record.SourceRunID {
			runLive = true
			break
		}
	}
	for key, row := range s.holdout {
		if key.experience == id ||
			(!runLive && !record.SourceRunID.IsZero() && key.run == record.SourceRunID && row.Scope.TenantID == record.Scope.TenantID) {
			delete(s.holdout, key)
		}
	}
	return true
}

// HoldoutRows are every holdout assignment row, ordered by run then record: for inspection in tests.
func (s *Store) HoldoutRows() []HoldoutRow {
	s.mu.RLock()
	defer s.mu.RUnlock()
	rows := make([]HoldoutRow, 0, len(s.holdout))
	for _, row := range s.holdout {
		rows = append(rows, row)
	}
	slices.SortFunc(rows, func(a, b HoldoutRow) int {
		if c := compareUUID(a.RunID, b.RunID); c != 0 {
			return c
		}
		return compareUUID(a.ExperienceID, b.ExperienceID)
	})
	return rows
}

// compareUUID orders UUIDs as PostgreSQL's uuid type does: by their bytes.
func compareUUID(a, b ae.UUID) int {
	return slices.Compare(a[:], b[:])
}

var _ ae.HoldoutRecorder = (*Store)(nil)
