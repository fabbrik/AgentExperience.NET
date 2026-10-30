package postgres

// Contract 1.16: the lifecycle store operations, running sql/lifecycle.sql's statements as written, in the order its
// header gives: the commit (one READ COMMITTED transaction, the revision compare-and-set, the evidence ledger under a
// savepoint, the named unique violations, the in-transaction supersession gate), a history page, the supersession
// check, and the scoped read. Every transaction first declares the authorization's bounds. A request is validated,
// then authorized, before any statement runs. A sealed record (payload_version other than 1) is NotFound to all of
// them: a port holds no key store.

import (
	"context"
	"errors"
	"fmt"
	"math"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgtype"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// LifecycleSQL is contract 1.16's: the lifecycle statements, each starting at its "-- statement: <name>" line.
const LifecycleSQL = "lifecycle.sql"

// LifecycleScopedLockSQL is contract 1.18's: the supersession lock scoped to the request, which a commit runs in place
// of LifecycleSQL's unscoped "lock-supersession-rows".
const LifecycleScopedLockSQL = "lifecycle-1.18.sql"

// The lifecycle statements a commit, a history, a supersession check and a read run.
var lifecycleStatementNames = []string{"read-record", "sealed-record", "savepoint", "rollback-to-savepoint",
	"release-savepoint", "insert-evidence", "recorded-only-key-taken", "evidence-id-taken", "select-evidence",
	"insert-event", "select-event", "check-supersession", "update-projection",
	"update-projection-with-confidence", "update-projection-recorded-only", "select-revision-and-status",
	"lock-revision-and-status", "read-history"}

// The constraints whose violation a commit recognises, and the errors it reads as a lost race.
const (
	uniqueViolation           = "23505"
	serializationFailure      = "40001"
	deadlockDetected          = "40P01"
	eventPrimaryKey           = "lifecycle_events_pkey"
	eventRevisionIndex        = "ix_lifecycle_events_record_revision"
	evidencePrimaryKey        = "confidence_evidence_pkey"
	evidenceIndependenceIndex = "ux_confidence_evidence_independence"
	evidenceAssessmentIndex   = "ux_confidence_evidence_assessment"
)

func loadLifecycleStatements() (map[string]Statement, error) {
	loaded := make(map[string]Statement, len(lifecycleStatementNames)+1)
	for _, file := range []struct {
		name       string
		statements []string
	}{
		{LifecycleSQL, lifecycleStatementNames},
		{LifecycleScopedLockSQL, []string{"lock-supersession-rows-scoped"}},
	} {
		statements, err := ContractStatements(file.name)
		if err != nil {
			return nil, err
		}
		for _, name := range file.statements {
			if statements[name] == "" {
				return nil, fmt.Errorf("%s has no %q statement", file.name, name)
			}
			loaded[name] = ToDriverPlaceholders(statements[name])
		}
	}
	return loaded, nil
}

// lifecycleClock is the store's clock, which a commit stamps on the event and the record.
type lifecycleClock struct{ now func() ae.Instant }

// SetClock replaces the clock a lifecycle commit stamps (recorded_at, and updated_at unless the evidence is recorded
// only), truncated to whole microseconds; nil is agentexperience.Now. Safe while the store is in use.
func (s *Store) SetClock(clock func() ae.Instant) {
	if clock == nil {
		s.clock.Store(nil)
		return
	}
	s.clock.Store(&lifecycleClock{clock})
}

func (s *Store) now() ae.Instant {
	if held := s.clock.Load(); held != nil {
		return held.now().TruncateToMicroseconds()
	}
	return ae.Now().TruncateToMicroseconds()
}

// readCommittedBeginner is a source that can pin a transaction's isolation level (*pgxpool.Pool and *pgx.Conn).
type readCommittedBeginner interface {
	BeginTx(ctx context.Context, options pgx.TxOptions) (pgx.Tx, error)
}

// ErrHostIsolation is a lifecycle commit asked of a store whose source is the host's own transaction (a pgx.Tx)
// running at an isolation level other than READ COMMITTED: a commit decides a lost race by re-reading the record,
// which under a snapshot isolation would read the host's stale snapshot and never see the revision move.
var ErrHostIsolation = errors.New("a lifecycle commit needs READ COMMITTED, and the host's transaction runs at " +
	"another isolation level: give the store a pool or a connection, or begin the transaction READ COMMITTED")

// begin starts a transaction and declares the authorization's bounds. For a commit (readCommitted) on a pool or a
// connection it pins READ COMMITTED; on a host's pgx.Tx (a savepoint inside it) it checks the host's transaction runs
// at READ COMMITTED, and refuses anything else with ErrHostIsolation.
func (s *Store) begin(ctx context.Context, bounds ae.AuthorizationBounds, readCommitted bool) (pgx.Tx, error) {
	var tx pgx.Tx
	var err error
	pinned, canPin := s.db.(readCommittedBeginner)
	if canPin && readCommitted {
		tx, err = pinned.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted})
	} else {
		tx, err = s.db.Begin(ctx)
	}
	if err != nil {
		return nil, err
	}
	if readCommitted && !canPin {
		var level string
		if err := tx.QueryRow(ctx, "SELECT current_setting('transaction_isolation')").Scan(&level); err != nil {
			rollback(tx)
			return nil, err
		}
		if level != "read committed" {
			rollback(tx)
			return nil, fmt.Errorf("%w (it is %s)", ErrHostIsolation, level)
		}
	}
	parameters := bounds.Parameters()
	values := make(map[string]any, len(parameters))
	for name, value := range parameters {
		values[name] = value
	}
	args, err := s.declare.Args(values)
	if err == nil {
		_, err = tx.Exec(ctx, s.declare.SQL, args...)
	}
	if err != nil {
		rollback(tx)
		return nil, err
	}
	return tx, nil
}

func rollback(tx pgx.Tx) { _ = tx.Rollback(context.Background()) }

// inTransaction runs fn in a new transaction that declared bounds, and rolls it back: for the reads a lost race reports.
func (s *Store) readTransaction(ctx context.Context, bounds ae.AuthorizationBounds, fn func(tx pgx.Tx) error) error {
	tx, err := s.begin(ctx, bounds, false)
	if err != nil {
		return err
	}
	defer rollback(tx)
	if err := fn(tx); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func uuidValue(id ae.UUID) pgtype.UUID { return pgtype.UUID{Bytes: id, Valid: true} }

func optionalUUIDValue(id *ae.UUID) pgtype.UUID {
	if id == nil {
		return pgtype.UUID{}
	}
	return uuidValue(*id)
}

func optionalStatusValue(status *ae.ExperienceStatus) *string {
	if status == nil {
		return nil
	}
	text := string(*status)
	return &text
}

func scopeValues(scope ae.Scope, values map[string]any) map[string]any {
	values["tenant_id"] = scope.TenantID
	values["application_id"] = scope.ApplicationID
	values["project_id"] = scope.ProjectID
	values["team_id"] = scope.TeamID
	values["agent_id"] = scope.AgentID
	values["user_id"] = scope.UserID
	return values
}

// counter is an evidence counter as the integer columns hold it: .NET's counters are 32-bit.
func counter(value int64) (int32, error) {
	if value < math.MinInt32 || value > math.MaxInt32 {
		return 0, fmt.Errorf("the evidence counter %d does not fit the ledger's integer columns", value)
	}
	return int32(value), nil
}

func (s *Store) run(ctx context.Context, tx pgx.Tx, name string, values map[string]any) (pgconn.CommandTag, error) {
	statement := s.lifecycle[name]
	args, err := statement.Args(values)
	if err != nil {
		return pgconn.CommandTag{}, err
	}
	return tx.Exec(ctx, statement.SQL, args...)
}

func (s *Store) query(ctx context.Context, tx pgx.Tx, name string, values map[string]any) (pgx.Rows, error) {
	statement := s.lifecycle[name]
	args, err := statement.Args(values)
	if err != nil {
		return nil, err
	}
	return tx.Query(ctx, statement.SQL, args...)
}

func (s *Store) exists(ctx context.Context, tx pgx.Tx, name string, values map[string]any) (bool, error) {
	statement := s.lifecycle[name]
	args, err := statement.Args(values)
	if err != nil {
		return false, err
	}
	var found bool
	err = tx.QueryRow(ctx, statement.SQL, args...).Scan(&found)
	return found, err
}

// violation is the unique constraint a statement violated, or "".
func violation(err error) string {
	var pgError *pgconn.PgError
	if errors.As(err, &pgError) && pgError.Code == uniqueViolation {
		return pgError.ConstraintName
	}
	return ""
}

func lostRace(err error) bool {
	var pgError *pgconn.PgError
	return errors.As(err, &pgError) && (pgError.Code == serializationFailure || pgError.Code == deadlockDetected)
}

func noErrors() []ae.StoreValidationError { return []ae.StoreValidationError{} }

func decodeStatus(text string) (ae.ExperienceStatus, error) {
	status := ae.ExperienceStatus(text)
	if !ae.IsDefinedStatus(status) {
		return "", errors.New("a stored row has an unrecognized status")
	}
	return status, nil
}

// ---- The scoped read ---------------------------------------------------------------------------------------------

// ReadRecord reads one record in exactly scope ("read-record"): Invalid (agentexperience.ValidateGet), Denied,
// NotFound (none, or sealed), Deleted (a tombstone) or Found.
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
	result := ae.RecordReadResult{Outcome: ae.StoreNotFound, Errors: noErrors()}
	err := s.readTransaction(ctx, authorization.Bounds, func(tx pgx.Tx) error {
		rows, err := s.query(ctx, tx, "read-record", scopeValues(scope, map[string]any{
			"experience_id": uuidValue(experienceID)}))
		if err != nil {
			return err
		}
		defer rows.Close()
		if !rows.Next() {
			return rows.Err()
		}
		var row nullableRecordRow
		var deletedAt *time.Time
		targets := append(row.targets(), new(any), new(any), new(any), new(any), &deletedAt)
		if err := rows.Scan(targets...); err != nil {
			return err
		}
		switch {
		case deletedAt != nil:
			result.Outcome = ae.StoreDeleted
		case row.payloadVersion == nil || *row.payloadVersion != ae.PayloadVersion:
			// Sealed: a port holds no key store, so it neither reads nor writes one.
		default:
			record, err := row.record()
			if err != nil {
				return err
			}
			result = ae.RecordReadResult{Outcome: ae.StoreFound, Record: &record, Errors: noErrors()}
		}
		return rows.Err()
	})
	if err != nil {
		return ae.RecordReadResult{}, err
	}
	return result, nil
}

// nullableRecordRow is the eighteen record columns, each of which a tombstone may hold as NULL.
type nullableRecordRow struct {
	experienceID, sourceRunID                       pgtype.UUID
	tenantID, applicationID, projectID, taskID      *string
	teamID, agentID, userID, status                 *string
	reuseConfidence                                 *float64
	supportingValidations, contradictions, revision *int64
	createdAt, updatedAt                            *time.Time
	payloadVersion                                  *int64
	payload                                         []byte
}

func (r *nullableRecordRow) targets() []any {
	return []any{&r.experienceID, &r.sourceRunID, &r.tenantID, &r.applicationID, &r.projectID, &r.teamID,
		&r.agentID, &r.userID, &r.taskID, &r.status, &r.reuseConfidence, &r.supportingValidations,
		&r.contradictions, &r.revision, &r.createdAt, &r.updatedAt, &r.payloadVersion, &r.payload}
}

func (r *nullableRecordRow) record() (ae.ExperienceRecord, error) {
	if !r.experienceID.Valid || !r.sourceRunID.Valid || r.tenantID == nil || r.applicationID == nil ||
		r.projectID == nil || r.taskID == nil || r.status == nil || r.reuseConfidence == nil ||
		r.supportingValidations == nil || r.contradictions == nil || r.revision == nil || r.createdAt == nil ||
		r.updatedAt == nil || r.payload == nil {
		return ae.ExperienceRecord{}, errors.New("a stored record has a null column")
	}
	row := recordRow{experienceID: r.experienceID.Bytes, sourceRunID: r.sourceRunID.Bytes, tenantID: *r.tenantID,
		applicationID: *r.applicationID, projectID: *r.projectID, taskID: *r.taskID, teamID: r.teamID,
		agentID: r.agentID, userID: r.userID, status: *r.status, reuseConfidence: *r.reuseConfidence,
		supportingValidations: *r.supportingValidations, contradictions: *r.contradictions, revision: *r.revision,
		createdAt: *r.createdAt, updatedAt: *r.updatedAt, payloadVersion: *r.payloadVersion, payload: r.payload}
	return row.record()
}

// ---- The commit --------------------------------------------------------------------------------------------------

// commitState is what one commit carries between its statements.
type commitState struct {
	store         *Store
	ctx           context.Context
	authorization ae.AuthorizationContext
	scope         ae.Scope
	event         ae.LifecycleEvent
	occurredAt    ae.Instant
	recordedAt    ae.Instant
	applied       int64
}

// CommitLifecycleEvent commits one lifecycle event and the record's projection in one READ COMMITTED transaction,
// exactly as sql/lifecycle.sql's header lays it out. Invalid and Denied run nothing.
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
	c := &commitState{store: s, ctx: ctx, authorization: authorization, scope: scope, event: event.Clone(),
		occurredAt: event.OccurredAt.TruncateToMicroseconds(), recordedAt: s.now(), applied: event.ExpectedRevision + 1}
	return c.commit()
}

func (c *commitState) values(extra map[string]any) map[string]any {
	values := map[string]any{"experience_id": uuidValue(c.event.ExperienceRecordID)}
	for key, value := range extra {
		values[key] = value
	}
	return scopeValues(c.scope, values)
}

func (c *commitState) commit() (ae.LifecycleCommitResult, error) {
	s, ctx := c.store, c.ctx
	tx, err := s.begin(ctx, c.authorization.Bounds, true)
	if err != nil {
		return ae.LifecycleCommitResult{}, err
	}
	committed := false
	defer func() {
		if !committed {
			rollback(tx)
		}
	}()

	// 1. The evidence first: whether its independence key was free decides whether there is anything else to write.
	stored := c.event.Confidence
	if submitted := c.event.Confidence; submitted != nil {
		settled, commitSettled, err := c.insertEvidence(tx, *submitted)
		if err != nil {
			return ae.LifecycleCommitResult{}, err
		}
		if settled != nil {
			if commitSettled {
				if err := tx.Commit(ctx); err != nil {
					return ae.LifecycleCommitResult{}, err
				}
				committed = true
			}
			return *settled, nil
		}
	}

	// 2. The event.
	eventValues, err := c.eventValues(stored)
	if err != nil {
		return ae.LifecycleCommitResult{}, err
	}
	if _, err := s.run(ctx, tx, "insert-event", eventValues); err != nil {
		switch violation(err) {
		case eventPrimaryKey:
			rollback(tx)
			committed = true
			return c.eventReplay()
		case eventRevisionIndex:
			rollback(tx)
			committed = true
			return c.staleOrMissing()
		}
		return ae.LifecycleCommitResult{}, err
	}

	// 3. The supersession gate, with both rows locked, after the insert so a replay never reaches it.
	if c.event.ReplacementExperienceID != nil {
		refusal, err := c.supersessionGate(tx, *c.event.ReplacementExperienceID)
		if err != nil {
			return ae.LifecycleCommitResult{}, err
		}
		if refusal != nil {
			return *refusal, nil
		}
	}

	// 4. The projection, guarded by the revision, the prior status and the scope.
	name, values := "update-projection", c.values(map[string]any{
		"current_status":    string(c.event.CurrentStatus),
		"prior_status":      optionalStatusValue(c.event.PriorStatus),
		"expected_revision": c.event.ExpectedRevision,
		"applied_revision":  c.applied,
		"recorded_at":       c.recordedAt.Time(),
	})
	if stored != nil && stored.Counted() {
		name = "update-projection-with-confidence"
		supporting, err := counter(stored.NewSupportingValidations)
		if err != nil {
			return ae.LifecycleCommitResult{}, err
		}
		contradictions, err := counter(stored.NewContradictions)
		if err != nil {
			return ae.LifecycleCommitResult{}, err
		}
		values["new_reuse_confidence"] = stored.NewReuseConfidence
		values["new_supporting_validations"] = supporting
		values["new_contradictions"] = contradictions
	} else if stored != nil {
		name = "update-projection-recorded-only"
		delete(values, "recorded_at")
	}
	tag, err := s.run(ctx, tx, name, values)
	if err != nil {
		if lostRace(err) && ctx.Err() == nil {
			rollback(tx)
			committed = true
			return c.staleOrMissing()
		}
		return ae.LifecycleCommitResult{}, err
	}
	if tag.RowsAffected() == 0 {
		current, err := c.readRevisionAndStatus(tx, "select-revision-and-status")
		if err != nil {
			return ae.LifecycleCommitResult{}, err
		}
		switch {
		case current == nil:
			return ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}, nil
		case current.deleted:
			return ae.LifecycleCommitResult{Outcome: ae.StoreDeleted, Revision: current.revision, Errors: noErrors()}, nil
		case current.revision != c.event.ExpectedRevision:
			return ae.LifecycleCommitResult{Outcome: ae.StoreStaleRevision, Revision: current.revision,
				Errors: noErrors()}, nil
		}
		status, err := current.decodedStatus()
		if err != nil {
			return ae.LifecycleCommitResult{}, err
		}
		return ae.LifecycleCommitResult{Outcome: ae.StoreStatusMismatch, Revision: current.revision,
			CurrentStatus: &status, Errors: noErrors()}, nil
	}

	// 5. A port neither reads nor writes a sealed record: asked only now that this transaction holds the row lock, so
	// a seal cannot land between the check and the write.
	sealed, err := s.exists(ctx, tx, "sealed-record", c.values(nil))
	if err != nil {
		return ae.LifecycleCommitResult{}, err
	}
	if sealed {
		return ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}, nil
	}

	// 6. Commit.
	if err := tx.Commit(ctx); err != nil {
		return ae.LifecycleCommitResult{}, err
	}
	committed = true
	var applied *ae.ConfidenceUpdate
	if stored != nil {
		update := stored.Clone()
		applied = &update
	}
	return ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: c.applied, Errors: noErrors(),
		AppliedConfidence: applied}, nil
}

// evidenceValues are insert-evidence's parameters for update, written against eventID, revision and status.
func (c *commitState) evidenceValues(update ae.ConfidenceUpdate, eventID *ae.UUID, revision int64,
	status ae.ExperienceStatus) (map[string]any, error) {
	counters := make([]int32, 4)
	for i, value := range []int64{update.PriorSupportingValidations, update.NewSupportingValidations,
		update.PriorContradictions, update.NewContradictions} {
		converted, err := counter(value)
		if err != nil {
			return nil, err
		}
		counters[i] = converted
	}
	var admission *string
	if update.Admission != nil {
		text := string(*update.Admission)
		admission = &text
	}
	return map[string]any{
		"evidence_id":                      uuidValue(update.EvidenceID),
		"experience_id":                    uuidValue(c.event.ExperienceRecordID),
		"event_id":                         optionalUUIDValue(eventID),
		"confidence_kind":                  string(update.Kind),
		"confidence_source":                string(update.Source),
		"confidence_run_id":                uuidValue(update.RunID),
		"confidence_verification_round_id": optionalUUIDValue(update.VerificationRoundID),
		"confidence_reviewer_identity":     update.ReviewerIdentity,
		"counted":                          update.Counted(),
		"actor":                            c.authorization.Actor(),
		"confidence_rule_version":          update.RuleVersion,
		"confidence_detail":                update.Detail,
		"recorded_at":                      c.recordedAt.Time(),
		"applied_revision":                 revision,
		"applied_status":                   string(status),
		"prior_reuse_confidence":           update.PriorReuseConfidence,
		"new_reuse_confidence":             update.NewReuseConfidence,
		"prior_supporting_validations":     counters[0],
		"new_supporting_validations":       counters[1],
		"prior_contradictions":             counters[2],
		"new_contradictions":               counters[3],
		"confidence_assessment_id":         optionalUUIDValue(update.AssessmentID),
		"confidence_admission":             admission,
	}, nil
}

// insertEvidence writes the evidence row under the savepoint and decides what the submission is. A settled result
// is returned instead of writing an event and a projection; commitSettled says whether the transaction holds a ledger
// row worth keeping (a duplicate) or nothing at all.
func (c *commitState) insertEvidence(tx pgx.Tx, submitted ae.ConfidenceUpdate) (settled *ae.LifecycleCommitResult,
	commitSettled bool, err error) {
	s, ctx := c.store, c.ctx
	if _, err := s.run(ctx, tx, "savepoint", nil); err != nil {
		return nil, false, err
	}
	eventID := c.event.EventID
	values, err := c.evidenceValues(submitted, &eventID, c.applied, c.event.CurrentStatus)
	if err != nil {
		return nil, false, err
	}
	if _, err := s.run(ctx, tx, "insert-evidence", values); err != nil {
		switch violation(err) {
		case evidenceIndependenceIndex:
			if _, err := s.run(ctx, tx, "rollback-to-savepoint", nil); err != nil {
				return nil, false, err
			}
			return c.recordDuplicate(tx, submitted)
		case evidencePrimaryKey:
			result, err := c.evidenceReplay(tx, submitted)
			return result, false, err
		case evidenceAssessmentIndex:
			result, err := c.replayOrSpent(tx, submitted)
			return result, false, err
		}
		return nil, false, err
	}
	if !submitted.Counted() {
		// Host-trusted evidence recorded only claims no key in the index, so whether an event already holds its key is
		// asked of the ledger.
		taken, err := s.exists(ctx, tx, "recorded-only-key-taken", map[string]any{
			"evidence_id": uuidValue(submitted.EvidenceID)})
		if err != nil {
			return nil, false, err
		}
		if taken {
			if _, err := s.run(ctx, tx, "rollback-to-savepoint", nil); err != nil {
				return nil, false, err
			}
			return c.recordDuplicate(tx, submitted)
		}
	}
	if _, err := s.run(ctx, tx, "release-savepoint", nil); err != nil {
		return nil, false, err
	}
	return nil, false, nil
}

// recordDuplicate records a submission whose independence key was taken: a ledger row, not counted, and nothing else.
func (c *commitState) recordDuplicate(tx pgx.Tx, submitted ae.ConfidenceUpdate) (*ae.LifecycleCommitResult, bool, error) {
	s, ctx := c.store, c.ctx
	current, err := c.readRevisionAndStatus(tx, "lock-revision-and-status")
	if err != nil {
		return nil, false, err
	}
	switch {
	case current == nil:
		return &ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}, false, nil
	case current.deleted:
		return &ae.LifecycleCommitResult{Outcome: ae.StoreDeleted, Revision: current.revision, Errors: noErrors()}, false, nil
	case current.revision != c.event.ExpectedRevision:
		return &ae.LifecycleCommitResult{Outcome: ae.StoreStaleRevision, Revision: current.revision,
			Errors: noErrors()}, false, nil
	}
	status, err := current.decodedStatus()
	if err != nil {
		return nil, false, err
	}
	if c.event.PriorStatus != nil && status != *c.event.PriorStatus {
		return &ae.LifecycleCommitResult{Outcome: ae.StoreStatusMismatch, Revision: current.revision,
			CurrentStatus: &status, Errors: noErrors()}, false, nil
	}
	// The row is locked: a sealed record is NotFound to a port, and nothing is recorded against it.
	sealed, err := s.exists(ctx, tx, "sealed-record", c.values(nil))
	if err != nil {
		return nil, false, err
	}
	if sealed {
		return &ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}, false, nil
	}
	recordedOnly := submitted.AsRecordedOnly()
	values, err := c.evidenceValues(recordedOnly, nil, current.revision, status)
	if err != nil {
		return nil, false, err
	}
	if _, err := s.run(ctx, tx, "insert-evidence", values); err != nil {
		switch violation(err) {
		case evidencePrimaryKey:
			result, err := c.evidenceReplay(tx, submitted)
			return result, false, err
		case evidenceAssessmentIndex:
			result, err := c.replayOrSpent(tx, submitted)
			return result, false, err
		}
		return nil, false, err
	}
	if _, err := s.run(ctx, tx, "release-savepoint", nil); err != nil {
		return nil, false, err
	}
	stored := recordedOnly.Clone()
	return &ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: current.revision, CurrentStatus: &status,
		Errors: noErrors(), AppliedConfidence: &stored}, true, nil
}

// replayOrSpent decides an assessment already spent on this record: the same evidence ID is a replay; another is a
// replayed token, refused.
func (c *commitState) replayOrSpent(tx pgx.Tx, submitted ae.ConfidenceUpdate) (*ae.LifecycleCommitResult, error) {
	s, ctx := c.store, c.ctx
	if _, err := s.run(ctx, tx, "rollback-to-savepoint", nil); err != nil {
		return nil, err
	}
	taken, err := s.exists(ctx, tx, "evidence-id-taken", scopeValues(c.scope, map[string]any{
		"evidence_id": uuidValue(submitted.EvidenceID)}))
	if err != nil {
		return nil, err
	}
	if taken {
		return c.compareStoredEvidence(tx, submitted)
	}
	return &ae.LifecycleCommitResult{Outcome: ae.StoreConflict, Errors: []ae.StoreValidationError{{
		Path:    ae.AssessmentIDPath,
		Message: "this assessment has already landed evidence for this record under another evidence ID.",
	}}}, nil
}

// evidenceReplay decides a resubmitted evidence ID, after undoing the failed insert to the savepoint.
func (c *commitState) evidenceReplay(tx pgx.Tx, submitted ae.ConfidenceUpdate) (*ae.LifecycleCommitResult, error) {
	if _, err := c.store.run(c.ctx, tx, "rollback-to-savepoint", nil); err != nil {
		return nil, err
	}
	return c.compareStoredEvidence(tx, submitted)
}

// compareStoredEvidence reads the ledger row under the evidence ID ("select-evidence") and decides replay or conflict.
func (c *commitState) compareStoredEvidence(tx pgx.Tx, submitted ae.ConfidenceUpdate) (*ae.LifecycleCommitResult, error) {
	s, ctx := c.store, c.ctx
	rows, err := s.query(ctx, tx, "select-evidence", scopeValues(c.scope, map[string]any{
		"evidence_id": uuidValue(submitted.EvidenceID)}))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	conflict := &ae.LifecycleCommitResult{Outcome: ae.StoreConflict, Errors: noErrors()}
	if !rows.Next() {
		return conflict, rows.Err()
	}
	var experienceID, eventID, runID, roundID, assessmentID pgtype.UUID
	var kind, source, appliedStatus, ruleVersion string
	var reviewer, detail, admission *string
	var counted bool
	var appliedRevision int64
	var priorScore, newScore float64
	var priorSupporting, newSupporting, priorContradictions, newContradictions int64
	if err := rows.Scan(&experienceID, &eventID, &kind, &source, &runID, &roundID, &reviewer, &counted,
		&appliedRevision, &appliedStatus, &ruleVersion, &detail, &priorScore, &newScore, &priorSupporting,
		&newSupporting, &priorContradictions, &newContradictions, &assessmentID, &admission); err != nil {
		return nil, err
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}
	if ae.UUID(experienceID.Bytes) != c.event.ExperienceRecordID {
		return conflict, nil
	}
	stored := ae.ConfidenceUpdate{Kind: ae.ConfidenceEvidenceKind(kind), Source: ae.ConfidenceEvidenceSource(source),
		RunID: runID.Bytes, VerificationRoundID: nullableUUID(roundID), ReviewerIdentity: reviewer,
		RuleVersion: ruleVersion, PriorReuseConfidence: priorScore, NewReuseConfidence: newScore,
		PriorSupportingValidations: priorSupporting, NewSupportingValidations: newSupporting,
		PriorContradictions: priorContradictions, NewContradictions: newContradictions, Detail: detail,
		AssessmentID: nullableUUID(assessmentID), Admission: nullableAdmission(admission)}
	if !ae.SameStoredEvidence(c.event, submitted, experienceID.Bytes, nullableUUID(eventID), stored) {
		return conflict, nil
	}
	status, err := decodeStatus(appliedStatus)
	if err != nil {
		return nil, err
	}
	replayed := ae.EvidenceReplay(submitted, stored)
	return &ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: appliedRevision, CurrentStatus: &status,
		Errors: noErrors(), AppliedConfidence: &replayed}, nil
}

func nullableUUID(value pgtype.UUID) *ae.UUID {
	if !value.Valid {
		return nil
	}
	id := ae.UUID(value.Bytes)
	return &id
}

func nullableAdmission(value *string) *ae.ConfidenceEvidenceAdmission {
	if value == nil {
		return nil
	}
	admission := ae.ConfidenceEvidenceAdmission(*value)
	return &admission
}

// eventValues are insert-event's parameters, the confidence ones NULL for an event without an update.
func (c *commitState) eventValues(confidence *ae.ConfidenceUpdate) (map[string]any, error) {
	e := c.event
	values := c.values(map[string]any{
		"event_id":                  uuidValue(e.EventID),
		"prior_status":              optionalStatusValue(e.PriorStatus),
		"current_status":            string(e.CurrentStatus),
		"reason":                    e.Reason,
		"producer":                  e.Producer,
		"occurred_at":               c.occurredAt.Time(),
		"recorded_at":               c.recordedAt.Time(),
		"expected_revision":         e.ExpectedRevision,
		"applied_revision":          c.applied,
		"replacement_experience_id": optionalUUIDValue(e.ReplacementExperienceID),
		"actor":                     c.authorization.Actor(),
	})
	names := []string{"confidence_evidence_id", "confidence_kind", "confidence_source", "confidence_run_id",
		"confidence_verification_round_id", "confidence_reviewer_identity", "confidence_rule_version",
		"confidence_detail", "prior_reuse_confidence", "new_reuse_confidence", "prior_supporting_validations",
		"new_supporting_validations", "prior_contradictions", "new_contradictions", "confidence_assessment_id",
		"confidence_admission"}
	if confidence == nil {
		for _, name := range names {
			switch name {
			case "confidence_evidence_id", "confidence_run_id", "confidence_verification_round_id",
				"confidence_assessment_id":
				values[name] = pgtype.UUID{}
			case "prior_reuse_confidence", "new_reuse_confidence":
				values[name] = (*float64)(nil)
			case "prior_supporting_validations", "new_supporting_validations", "prior_contradictions",
				"new_contradictions":
				values[name] = (*int32)(nil)
			default:
				values[name] = (*string)(nil)
			}
		}
		return values, nil
	}
	evidence, err := c.evidenceValues(*confidence, nil, 0, e.CurrentStatus)
	if err != nil {
		return nil, err
	}
	values["confidence_evidence_id"] = evidence["evidence_id"]
	for _, name := range names[1:] {
		values[name] = evidence[name]
	}
	return values, nil
}

// supersessionGate locks both rows and re-decides the replacement inside the commit: a refusal, or nil to proceed.
// Only rows of the request scope are locked (contract 1.18), so a replacement naming another tenant's record takes no
// lock on it; the check then reads it as not found.
func (c *commitState) supersessionGate(tx pgx.Tx, replacementID ae.UUID) (*ae.LifecycleCommitResult, error) {
	s, ctx := c.store, c.ctx
	if _, err := s.run(ctx, tx, "lock-supersession-rows-scoped", scopeValues(c.scope, map[string]any{
		"lock_ids": []pgtype.UUID{uuidValue(c.event.ExperienceRecordID), uuidValue(replacementID)}})); err != nil {
		return nil, err
	}
	check, err := s.readSupersession(ctx, tx, c.scope, c.event.ExperienceRecordID, replacementID)
	if err != nil {
		return nil, err
	}
	if (check.Outcome == ae.SupersessionAllowed || check.Outcome == ae.SupersessionRecordNotFound) &&
		check.ReplacementStatus != nil && ae.IsEligible(*check.ReplacementStatus) {
		return nil, nil
	}
	return &ae.LifecycleCommitResult{Outcome: ae.StoreReplacementNotAllowed, CurrentStatus: check.ReplacementStatus,
		Errors: noErrors()}, nil
}

// recordState is select-revision-and-status's row.
type recordState struct {
	revision int64
	status   *string
	deleted  bool
}

func (r *recordState) decodedStatus() (ae.ExperienceStatus, error) {
	if r.status == nil {
		return "", errors.New("a stored record has no status")
	}
	return decodeStatus(*r.status)
}

func (c *commitState) readRevisionAndStatus(tx pgx.Tx, name string) (*recordState, error) {
	rows, err := c.store.query(c.ctx, tx, name, c.values(nil))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	if !rows.Next() {
		return nil, rows.Err()
	}
	var state recordState
	var deletedAt *time.Time
	if err := rows.Scan(&state.revision, &state.status, &deletedAt); err != nil {
		return nil, err
	}
	state.deleted = deletedAt != nil
	rows.Close()
	return &state, rows.Err()
}

// staleOrMissing reports a lost race, in a new transaction: NotFound, Deleted or StaleRevision with the revision.
func (c *commitState) staleOrMissing() (ae.LifecycleCommitResult, error) {
	var result ae.LifecycleCommitResult
	err := c.store.readTransaction(c.ctx, c.authorization.Bounds, func(tx pgx.Tx) error {
		current, err := c.readRevisionAndStatus(tx, "select-revision-and-status")
		if err != nil {
			return err
		}
		switch {
		case current == nil:
			result = ae.LifecycleCommitResult{Outcome: ae.StoreNotFound, Errors: noErrors()}
		case current.deleted:
			result = ae.LifecycleCommitResult{Outcome: ae.StoreDeleted, Revision: current.revision, Errors: noErrors()}
		default:
			result = ae.LifecycleCommitResult{Outcome: ae.StoreStaleRevision, Revision: current.revision,
				Errors: noErrors()}
		}
		return nil
	})
	return result, err
}

// eventReplay decides a resubmitted event ID, in a new transaction ("select-event").
func (c *commitState) eventReplay() (ae.LifecycleCommitResult, error) {
	var result ae.LifecycleCommitResult
	err := c.store.readTransaction(c.ctx, c.authorization.Bounds, func(tx pgx.Tx) error {
		rows, err := c.store.query(c.ctx, tx, "select-event", map[string]any{"event_id": uuidValue(c.event.EventID)})
		if err != nil {
			return err
		}
		defer rows.Close()
		result = ae.LifecycleCommitResult{Outcome: ae.StoreConflict, Errors: noErrors()}
		if !rows.Next() {
			return rows.Err()
		}
		var row eventRow
		if err := rows.Scan(row.targets()...); err != nil {
			return err
		}
		rows.Close()
		if err := rows.Err(); err != nil {
			return err
		}
		if !row.experienceID.Valid || ae.UUID(row.experienceID.Bytes) != c.event.ExperienceRecordID ||
			!row.scope().Equal(c.scope) {
			return nil
		}
		stored, err := row.stored()
		if err != nil {
			return err
		}
		submitted := c.event.Clone()
		submitted.OccurredAt = c.occurredAt
		if stored.Event.Equal(ae.ReplayedEvent(submitted, stored.Event)) {
			result = ae.LifecycleCommitResult{Outcome: ae.StoreCommitted, Revision: stored.AppliedRevision,
				Errors: noErrors(), AppliedConfidence: stored.Event.Confidence}
		}
		return nil
	})
	return result, err
}

// eventRow is one row of agent_experience.lifecycle_events (every column insert-event names), each nullable: the
// history's outer join reads a record with no events as one row of NULLs.
type eventRow struct {
	eventID, experienceID                                                  pgtype.UUID
	tenantID, applicationID, projectID, teamID, agentID, userID            *string
	priorStatus, currentStatus, reason, producer                           *string
	occurredAt, recordedAt                                                 *time.Time
	expectedRevision, appliedRevision                                      *int64
	replacementID                                                          pgtype.UUID
	actor                                                                  *string
	evidenceID                                                             pgtype.UUID
	kind, source                                                           *string
	runID, roundID                                                         pgtype.UUID
	reviewer, ruleVersion, detail                                          *string
	priorScore, newScore                                                   *float64
	priorSupporting, newSupporting, priorContradictions, newContradictions *int64
	assessmentID                                                           pgtype.UUID
	admission                                                              *string
}

func (r *eventRow) targets() []any {
	return []any{&r.eventID, &r.experienceID, &r.tenantID, &r.applicationID, &r.projectID, &r.teamID, &r.agentID,
		&r.userID, &r.priorStatus, &r.currentStatus, &r.reason, &r.producer, &r.occurredAt, &r.recordedAt,
		&r.expectedRevision, &r.appliedRevision, &r.replacementID, &r.actor, &r.evidenceID, &r.kind, &r.source,
		&r.runID, &r.roundID, &r.reviewer, &r.ruleVersion, &r.detail, &r.priorScore, &r.newScore,
		&r.priorSupporting, &r.newSupporting, &r.priorContradictions, &r.newContradictions, &r.assessmentID,
		&r.admission}
}

func (r *eventRow) scope() ae.Scope {
	text := func(value *string) string {
		if value == nil {
			return ""
		}
		return *value
	}
	return ae.Scope{TenantID: text(r.tenantID), ApplicationID: text(r.applicationID), ProjectID: text(r.projectID),
		TeamID: r.teamID, AgentID: r.agentID, UserID: r.userID}
}

var errNullEventColumn = errors.New("a stored lifecycle event has a null column")

func (r *eventRow) stored() (ae.StoredLifecycleEvent, error) {
	if !r.eventID.Valid || !r.experienceID.Valid || r.currentStatus == nil || r.reason == nil || r.producer == nil ||
		r.occurredAt == nil || r.recordedAt == nil || r.expectedRevision == nil || r.appliedRevision == nil {
		return ae.StoredLifecycleEvent{}, errNullEventColumn
	}
	current, err := decodeStatus(*r.currentStatus)
	if err != nil {
		return ae.StoredLifecycleEvent{}, err
	}
	var prior *ae.ExperienceStatus
	if r.priorStatus != nil {
		status, err := decodeStatus(*r.priorStatus)
		if err != nil {
			return ae.StoredLifecycleEvent{}, err
		}
		prior = &status
	}
	occurredAt, err := ae.InstantFromTime(*r.occurredAt)
	if err != nil {
		return ae.StoredLifecycleEvent{}, err
	}
	recordedAt, err := ae.InstantFromTime(*r.recordedAt)
	if err != nil {
		return ae.StoredLifecycleEvent{}, err
	}
	event := ae.LifecycleEvent{EventID: r.eventID.Bytes, ExperienceRecordID: r.experienceID.Bytes, PriorStatus: prior,
		CurrentStatus: current, Reason: *r.reason, Producer: *r.producer, OccurredAt: occurredAt,
		ExpectedRevision: *r.expectedRevision, ReplacementExperienceID: nullableUUID(r.replacementID)}
	if r.evidenceID.Valid {
		if r.kind == nil || r.source == nil || !r.runID.Valid || r.ruleVersion == nil || r.priorScore == nil ||
			r.newScore == nil || r.priorSupporting == nil || r.newSupporting == nil || r.priorContradictions == nil ||
			r.newContradictions == nil {
			return ae.StoredLifecycleEvent{}, errNullEventColumn
		}
		event.Confidence = &ae.ConfidenceUpdate{EvidenceID: r.evidenceID.Bytes, Kind: ae.ConfidenceEvidenceKind(*r.kind),
			Source: ae.ConfidenceEvidenceSource(*r.source), RunID: r.runID.Bytes, VerificationRoundID: nullableUUID(r.roundID),
			ReviewerIdentity: r.reviewer, RuleVersion: *r.ruleVersion, PriorReuseConfidence: *r.priorScore,
			NewReuseConfidence: *r.newScore, PriorSupportingValidations: *r.priorSupporting,
			NewSupportingValidations: *r.newSupporting, PriorContradictions: *r.priorContradictions,
			NewContradictions: *r.newContradictions, Detail: r.detail, AssessmentID: nullableUUID(r.assessmentID),
			Admission: nullableAdmission(r.admission)}
	}
	return ae.StoredLifecycleEvent{Event: event, RecordedAt: recordedAt, AppliedRevision: *r.appliedRevision,
		Actor: r.actor}, nil
}

// ---- The history and the supersession check ----------------------------------------------------------------------

// GetHistory reads one page of a record's history ("read-history", one statement): Invalid
// (agentexperience.ValidateHistoryQuery), Denied, NotFound (none, or sealed), Deleted (a tombstone) or Found with the
// record's revision, its events after the cursor oldest first, at most the limit.
func (s *Store) GetHistory(ctx context.Context, authorization ae.AuthorizationContext,
	query ae.HistoryQuery) (ae.HistoryResult, error) {
	empty := []ae.StoredLifecycleEvent{}
	if errors := ae.ValidateHistoryQuery(query); len(errors) > 0 {
		return ae.HistoryResult{Outcome: ae.StoreInvalid, Events: empty, Errors: errors}, nil
	}
	if !authorization.Permits(query.Scope) {
		return ae.HistoryResult{Outcome: ae.StoreDenied, Events: empty, Errors: noErrors()}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.HistoryResult{}, err
	}
	result := ae.HistoryResult{Outcome: ae.StoreNotFound, Events: empty, Errors: noErrors()}
	err := s.readTransaction(ctx, authorization.Bounds, func(tx pgx.Tx) error {
		// PostgreSQL cannot type "@start_after_revision IS NULL" from the statement alone, so every parameter is sent
		// with its type (historyTypes: bigint for the cursor), as .NET's driver sends it.
		statement := s.lifecycle["read-history"]
		args, err := statement.Args(scopeValues(query.Scope, map[string]any{
			"experience_id":        uuidValue(query.ExperienceID),
			"start_after_revision": query.StartAfterRevision,
			"limit":                int32(query.Limit),
		}))
		if err != nil {
			return err
		}
		rows, err := queryTyped(ctx, tx, statement, args, historyTypes)
		if err != nil {
			return err
		}
		defer rows.Close()
		var events []ae.StoredLifecycleEvent
		first := true
		for rows.Next() {
			var row eventRow
			var revision, payloadVersion *int64
			var deletedAt *time.Time
			if err := rows.Scan(append(row.targets(), &revision, &deletedAt, &payloadVersion)...); err != nil {
				return err
			}
			if first {
				first = false
				switch {
				case deletedAt != nil:
					result.Outcome = ae.StoreDeleted
					return nil
				case payloadVersion == nil || *payloadVersion != ae.PayloadVersion || revision == nil:
					return nil
				}
				result = ae.HistoryResult{Outcome: ae.StoreFound, Revision: *revision, Events: empty, Errors: noErrors()}
			}
			if !row.eventID.Valid {
				// The outer join's single row for a record with no events (or an exhausted cursor).
				continue
			}
			stored, err := row.stored()
			if err != nil {
				return err
			}
			events = append(events, stored)
		}
		if err := rows.Err(); err != nil {
			return err
		}
		if len(events) > 0 {
			result.Events = events
			next := events[len(events)-1].AppliedRevision
			result.NextStartAfterRevision = &next
		}
		return nil
	})
	if err != nil {
		return ae.HistoryResult{}, err
	}
	return result, nil
}

// historyTypes are the types of read-history's parameters, by name.
var historyTypes = map[string]uint32{
	"experience_id": pgtype.UUIDOID, "tenant_id": pgtype.TextOID, "application_id": pgtype.TextOID,
	"project_id": pgtype.TextOID, "team_id": pgtype.TextOID, "agent_id": pgtype.TextOID, "user_id": pgtype.TextOID,
	"start_after_revision": pgtype.Int8OID, "limit": pgtype.Int4OID,
}

// queryTyped runs a query with each parameter sent as its named type, encoded in the text format by the connection's
// type map, its results read in the binary format: for a statement PostgreSQL cannot type from its text alone.
func queryTyped(ctx context.Context, tx pgx.Tx, statement Statement, args []any, types map[string]uint32) (pgx.Rows, error) {
	typeMap := tx.Conn().TypeMap()
	values := make([][]byte, len(args))
	oids := make([]uint32, len(args))
	formats := make([]int16, len(args))
	for i, name := range statement.Names {
		oid, ok := types[name]
		if !ok {
			return nil, fmt.Errorf("the statement's parameter @%s has no type", name)
		}
		encoded, err := typeMap.Encode(oid, pgtype.TextFormatCode, args[i], nil)
		if err != nil {
			return nil, fmt.Errorf("the statement's parameter @%s: %w", name, err)
		}
		values[i], oids[i], formats[i] = encoded, oid, pgtype.TextFormatCode
	}
	reader := tx.Conn().PgConn().ExecParams(ctx, statement.SQL, values, oids, formats, []int16{pgtype.BinaryFormatCode})
	return pgx.RowsFromResultReader(typeMap, reader), nil
}

// CheckSupersession reports whether replacementID may replace experienceID in exactly scope ("check-supersession"):
// Invalid, Denied, RecordNotFound, ReplacementNotFound, Cycle (with the replacement's status) or Allowed (with it).
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
	var result ae.SupersessionCheckResult
	err := s.readTransaction(ctx, authorization.Bounds, func(tx pgx.Tx) error {
		var err error
		result, err = s.readSupersession(ctx, tx, scope, experienceID, replacementID)
		return err
	})
	if err != nil {
		return ae.SupersessionCheckResult{}, err
	}
	return result, nil
}

func (s *Store) readSupersession(ctx context.Context, tx pgx.Tx, scope ae.Scope, experienceID,
	replacementID ae.UUID) (ae.SupersessionCheckResult, error) {
	rows, err := s.query(ctx, tx, "check-supersession", scopeValues(scope, map[string]any{
		"experience_id":  uuidValue(experienceID),
		"replacement_id": uuidValue(replacementID),
	}))
	if err != nil {
		return ae.SupersessionCheckResult{}, err
	}
	defer rows.Close()
	result := ae.SupersessionCheckResult{Outcome: ae.SupersessionRecordNotFound, Errors: noErrors()}
	if !rows.Next() {
		return result, rows.Err()
	}
	var recordStatus, replacementStatus *string
	var cycle bool
	if err := rows.Scan(&recordStatus, &replacementStatus, &cycle); err != nil {
		return ae.SupersessionCheckResult{}, err
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return ae.SupersessionCheckResult{}, err
	}
	switch {
	case recordStatus == nil:
		return result, nil
	case replacementStatus == nil:
		result.Outcome = ae.SupersessionReplacementNotFound
		return result, nil
	}
	status, err := decodeStatus(*replacementStatus)
	if err != nil {
		return ae.SupersessionCheckResult{}, err
	}
	result.ReplacementStatus = &status
	result.Outcome = ae.SupersessionAllowed
	if cycle {
		result.Outcome = ae.SupersessionCycle
	}
	return result, nil
}

var _ ae.LifecycleStore = (*Store)(nil)
