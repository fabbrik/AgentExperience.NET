package postgres

// Contract 1.17: the reuse-feedback ledger, running sql/reuse-feedback.sql's statements as written, in the order its
// header gives: one READ COMMITTED transaction that inserts the submission (the primary key the arbiter), on a taken
// ID reads the stored one back and compares it, refuses an exposure to a record erased in the submission's scope, and
// inserts the exposures in record order. A submission is validated, then authorized, before any statement runs. The
// rationale is written in plaintext (rationale_sealed NULL): a port holds no key store.

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgtype"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ReuseFeedbackSQL is contract 1.17's: the ledger's statements, each starting at its "-- statement: <name>" line.
const ReuseFeedbackSQL = "reuse-feedback.sql"

var reuseFeedbackStatementNames = []string{"insert-submission", "select-submission", "select-exposures",
	"select-exposed-record-state", "insert-exposure"}

func loadReuseFeedbackStatements() (map[string]Statement, error) {
	statements, err := ContractStatements(ReuseFeedbackSQL)
	if err != nil {
		return nil, err
	}
	loaded := make(map[string]Statement, len(reuseFeedbackStatementNames))
	for _, name := range reuseFeedbackStatementNames {
		if statements[name] == "" {
			return nil, fmt.Errorf("%s has no %q statement", ReuseFeedbackSQL, name)
		}
		loaded[name] = ToDriverPlaceholders(statements[name])
	}
	return loaded, nil
}

var _ ae.ReuseFeedbackStore = (*Store)(nil)

func (s *Store) feedbackArgs(name string, values map[string]any) (string, []any, error) {
	statement := s.reuseFeedback[name]
	args, err := statement.Args(values)
	return statement.SQL, args, err
}

func uuidArray(ids []ae.UUID) []pgtype.UUID {
	values := make([]pgtype.UUID, len(ids))
	for i, id := range ids {
		values[i] = uuidValue(id)
	}
	return values
}

func storedTime(instant ae.Instant) time.Time { return instant.TruncateToMicroseconds().Time() }

// RecordReuseFeedback writes one submission and all of its exposures in one transaction, or nothing (.NET's
// PostgresExperienceReuseFeedbackStore.RecordAsync). Invalid (agentexperience.ValidateReuseFeedback) and Denied run no
// statement. A taken feedback ID is AlreadyRecorded with the stored submission when it is the same, otherwise Conflict
// (with the stored one only when the authorization permits its scope); an exposure to a record erased in the
// submission's scope is Invalid, naming its position as the caller listed it. A database error is returned.
func (s *Store) RecordReuseFeedback(ctx context.Context, authorization ae.AuthorizationContext,
	feedback ae.RecordedExperienceReuseFeedback) (ae.ReuseFeedbackStoreResult, error) {
	if errors := ae.ValidateReuseFeedback(feedback); len(errors) > 0 {
		return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerInvalid, Errors: errors}, nil
	}
	if !authorization.Permits(*feedback.Scope) {
		return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerDenied, Errors: noErrors()}, nil
	}
	if err := ctx.Err(); err != nil {
		return ae.ReuseFeedbackStoreResult{}, err
	}
	// Stored, compared and handed back in record order; the caller's own order names an erased exposure.
	submitted := feedback
	feedback = ae.NormalizeReuseFeedback(feedback)

	tx, err := s.begin(ctx, authorization.Bounds, true)
	if err != nil {
		return ae.ReuseFeedbackStoreResult{}, err
	}
	defer rollback(tx)

	inserted, err := s.insertSubmission(ctx, tx, feedback)
	if err != nil {
		return ae.ReuseFeedbackStoreResult{}, err
	}
	if !inserted {
		// The ID is taken: read inside this transaction, which is then rolled back.
		stored, err := s.readStoredFeedback(ctx, tx, feedback.FeedbackID)
		if err != nil {
			return ae.ReuseFeedbackStoreResult{}, err
		}
		rollback(tx)
		if stored != nil && ae.ReuseFeedbackMatches(*stored, feedback, false) {
			return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerAlreadyRecorded, Feedback: stored, Errors: noErrors()}, nil
		}
		result := ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerConflict, Errors: noErrors()}
		if stored != nil && authorization.Permits(*stored.Scope) {
			result.Feedback = stored
		}
		return result, nil
	}

	erased, err := s.erasedExposures(ctx, tx, submitted)
	if err != nil {
		return ae.ReuseFeedbackStoreResult{}, err
	}
	if len(erased) > 0 {
		rollback(tx)
		return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerInvalid, Errors: erased}, nil
	}
	for ordinal, exposure := range feedback.Exposures {
		sql, args, err := s.feedbackArgs("insert-exposure", map[string]any{
			"feedback_id":      uuidValue(feedback.FeedbackID),
			"experience_id":    uuidValue(exposure.ExperienceID),
			"ordinal":          int32(ordinal),
			"attributed":       exposure.Attributed,
			"evidence_id":      optionalUUIDValue(exposure.EvidenceID),
			"rationale_sealed": (*string)(nil),
		})
		if err != nil {
			return ae.ReuseFeedbackStoreResult{}, err
		}
		if _, err := tx.Exec(ctx, sql, args...); err != nil {
			return ae.ReuseFeedbackStoreResult{}, err
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return ae.ReuseFeedbackStoreResult{}, err
	}
	recorded := ae.CloneReuseFeedback(feedback)
	return ae.ReuseFeedbackStoreResult{Outcome: ae.LedgerRecorded, Feedback: &recorded, Errors: noErrors()}, nil
}

// insertSubmission runs "insert-submission"; false when the feedback ID is taken.
func (s *Store) insertSubmission(ctx context.Context, tx pgx.Tx, f ae.RecordedExperienceReuseFeedback) (bool, error) {
	var evidenceIDs any
	if len(f.EvidenceIDs) > 0 {
		evidenceIDs = uuidArray(f.EvidenceIDs)
	}
	var attributedAt *time.Time
	if f.AttributedAt != nil {
		at := storedTime(*f.AttributedAt)
		attributedAt = &at
	}
	values := scopeValues(*f.Scope, map[string]any{
		"feedback_id":           uuidValue(f.FeedbackID),
		"run_id":                uuidValue(f.RunID),
		"run_outcome":           string(f.RunOutcome),
		"claimed_benefit":       string(f.ClaimedBenefit),
		"benefit":               string(f.Benefit),
		"attribution_source":    string(f.AttributionSource),
		"reviewer_identity":     f.ReviewerIdentity,
		"evaluator_id":          f.EvaluatorID,
		"verification_round_id": optionalUUIDValue(f.VerificationRoundID),
		"assessment_id":         optionalUUIDValue(f.AssessmentID),
		"rationale":             f.Rationale,
		"evidence_ids":          evidenceIDs,
		"attributed_at":         attributedAt,
		"measure_kind":          f.Measure.Kind,
		"measure_value":         f.Measure.Value,
		"trial_label":           f.TrialLabel,
		"observed_at":           storedTime(f.ObservedAt),
		"recorded_at":           s.now().Time(),
	})
	sql, args, err := s.feedbackArgs("insert-submission", values)
	if err != nil {
		return false, err
	}
	var id pgtype.UUID
	switch err := tx.QueryRow(ctx, sql, args...).Scan(&id); {
	case errors.Is(err, pgx.ErrNoRows):
		return false, nil
	case err != nil:
		return false, err
	}
	return true, nil
}

func instantOf(t time.Time) (ae.Instant, error) { return ae.InstantFromTime(t) }

// readStoredFeedback reads the submission stored under feedbackID and its exposures in ordinal order; nil when no row
// is visible (row-level security hides another scope's).
func (s *Store) readStoredFeedback(ctx context.Context, tx pgx.Tx, feedbackID ae.UUID) (*ae.RecordedExperienceReuseFeedback, error) {
	sql, args, err := s.feedbackArgs("select-submission", map[string]any{"feedback_id": uuidValue(feedbackID)})
	if err != nil {
		return nil, err
	}
	var (
		runID, round, assessment                          pgtype.UUID
		tenant, application, project                      string
		team, agent, user, reviewer, evaluator, rationale *string
		runOutcome, claimed, benefit, source, measureKind string
		evidenceIDs                                       []pgtype.UUID
		attributedAt                                      *time.Time
		measureValue                                      float64
		trialLabel                                        *string
		observedAt                                        time.Time
	)
	err = tx.QueryRow(ctx, sql, args...).Scan(&runID, &tenant, &application, &project, &team, &agent, &user,
		&runOutcome, &claimed, &benefit, &source, &reviewer, &evaluator, &round, &assessment, &rationale, &evidenceIDs,
		&attributedAt, &measureKind, &measureValue, &trialLabel, &observedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	observed, err := instantOf(observedAt)
	if err != nil {
		return nil, err
	}
	scope := ae.Scope{TenantID: tenant, ApplicationID: application, ProjectID: project, TeamID: team, AgentID: agent,
		UserID: user}
	stored := &ae.RecordedExperienceReuseFeedback{
		FeedbackID: feedbackID, RunID: runID.Bytes, Scope: &scope, RunOutcome: ae.VerificationStatus(runOutcome),
		ClaimedBenefit: ae.ExperienceReuseBenefit(claimed), Benefit: ae.ExperienceReuseBenefit(benefit),
		AttributionSource: ae.ReuseAttributionSource(source), ReviewerIdentity: reviewer, EvaluatorID: evaluator,
		VerificationRoundID: optionalUUIDOf(round), AssessmentID: optionalUUIDOf(assessment), Rationale: rationale,
		EvidenceIDs: []ae.UUID{}, Measure: &ae.ReuseMeasure{Kind: measureKind, Value: measureValue},
		TrialLabel: trialLabel, ObservedAt: observed, Exposures: []*ae.ExperienceReuseExposure{},
	}
	for _, id := range evidenceIDs {
		stored.EvidenceIDs = append(stored.EvidenceIDs, id.Bytes)
	}
	if attributedAt != nil {
		at, err := instantOf(*attributedAt)
		if err != nil {
			return nil, err
		}
		stored.AttributedAt = &at
	}

	sql, args, err = s.feedbackArgs("select-exposures", map[string]any{"feedback_id": uuidValue(feedbackID)})
	if err != nil {
		return nil, err
	}
	rows, err := tx.Query(ctx, sql, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var experienceID, evidenceID pgtype.UUID
		var attributed bool
		var sealed *string
		if err := rows.Scan(&experienceID, &attributed, &evidenceID, &sealed); err != nil {
			return nil, err
		}
		stored.Exposures = append(stored.Exposures, &ae.ExperienceReuseExposure{ExperienceID: experienceID.Bytes,
			Attributed: attributed, EvidenceID: optionalUUIDOf(evidenceID)})
	}
	return stored, rows.Err()
}

func optionalUUIDOf(value pgtype.UUID) *ae.UUID {
	if !value.Valid {
		return nil
	}
	id := ae.UUID(value.Bytes)
	return &id
}

// erasedExposures runs "select-exposed-record-state" (which locks every named record in scope) and names each
// exposure, in the caller's order, whose record is erased in the submission's scope.
func (s *Store) erasedExposures(ctx context.Context, tx pgx.Tx,
	feedback ae.RecordedExperienceReuseFeedback) ([]ae.StoreValidationError, error) {
	var ids []ae.UUID
	seen := map[ae.UUID]bool{}
	for _, exposure := range feedback.Exposures {
		if !seen[exposure.ExperienceID] {
			seen[exposure.ExperienceID] = true
			ids = append(ids, exposure.ExperienceID)
		}
	}
	sql, args, err := s.feedbackArgs("select-exposed-record-state",
		scopeValues(*feedback.Scope, map[string]any{"experience_ids": uuidArray(ids)}))
	if err != nil {
		return nil, err
	}
	rows, err := tx.Query(ctx, sql, args...)
	if err != nil {
		return nil, err
	}
	erased := map[ae.UUID]bool{}
	for rows.Next() {
		var id pgtype.UUID
		var deletedAt *time.Time
		if err := rows.Scan(&id, &deletedAt); err != nil {
			rows.Close()
			return nil, err
		}
		if deletedAt != nil {
			erased[id.Bytes] = true
		}
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}
	var errors []ae.StoreValidationError
	for ordinal, exposure := range feedback.Exposures {
		if erased[exposure.ExperienceID] {
			errors = append(errors, ae.StoreValidationError{Path: "Exposures[" + strconv.Itoa(ordinal) + "].ExperienceId",
				Message: "names an Experience Record that has been erased; nothing may be recorded against it again."})
		}
	}
	return errors, nil
}
