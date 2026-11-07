package postgres

import (
	"context"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgtype"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// RecordProviderHoldoutAssignment records a run's arm under a provider key in scope (contract 1.11): it runs
// sql/record-provider-holdout-assignment.sql once, in one transaction that declared bounds. One row per tenant, run
// and key, its scope exactly scope (no record is read for it, so a run with no record yet is recorded); a row already
// there for them is kept as it is; the instant is truncated to whole microseconds and clamped to the database clock;
// nothing is written for a run whose own records in the tenant are all erased (the late-write guard). It refuses an
// assignment Validate refuses, and a scope outside the bounds (agentexperience.ErrAuthorizationDenied); otherwise a
// failure is the driver's (insufficient_privilege, 42501, until the role holds the grants of
// sql/application-role-privileges-1.11.sql). The agent integration calls it in the background, fail-open.
//
// Every parameter is sent with its type (providerHoldoutTypes), as .NET's driver sends them.
func (s *Store) RecordProviderHoldoutAssignment(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	assignment ae.ProviderHoldoutAssignment) error {
	if err := assignment.Validate(); err != nil {
		return err
	}
	if !bounds.Permits(scope) {
		return ae.ErrAuthorizationDenied
	}
	args, err := s.providerHoldout.Args(map[string]any{
		"run_id":         pgtype.UUID{Bytes: assignment.RunID, Valid: true},
		"provider_key":   assignment.ProviderKey,
		"arm":            string(assignment.Arm),
		"assigned_at":    assignment.AssignedAt.TruncateToMicroseconds().Time(),
		"tenant_id":      scope.TenantID,
		"application_id": scope.ApplicationID,
		"project_id":     scope.ProjectID,
		"team_id":        scope.TeamID,
		"agent_id":       scope.AgentID,
		"user_id":        scope.UserID,
	})
	if err != nil {
		return err
	}
	return s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		return execTyped(ctx, tx, s.providerHoldout, args, providerHoldoutTypes)
	})
}

// providerHoldoutTypes are the types of record-provider-holdout-assignment.sql's parameters, by name.
var providerHoldoutTypes = map[string]uint32{
	"run_id": pgtype.UUIDOID, "provider_key": pgtype.TextOID, "arm": pgtype.TextOID,
	"assigned_at": pgtype.TimestamptzOID, "tenant_id": pgtype.TextOID, "application_id": pgtype.TextOID,
	"project_id": pgtype.TextOID, "team_id": pgtype.TextOID, "agent_id": pgtype.TextOID, "user_id": pgtype.TextOID,
}

var _ ae.ProviderHoldoutRecorder = (*Store)(nil)

// QueryProviderHoldoutOutcomes reads one page of a provider's holdout rows (contract 1.11): in one transaction that
// declared bounds, "first-page" (or, after a run, "next-page") from sql/query-provider-holdout-outcomes.sql. Each row
// of the key in exactly scope comes back with its run's own record, whose outcome status it reads from the payload; a
// sealed record (payload_version 2), which a contract 1.x port cannot read, gives no outcome, so its run counts as
// pending. A limit outside 1 to 1000 or a malformed key is refused, and a scope outside bounds is
// agentexperience.ErrAuthorizationDenied; none runs a statement. Otherwise a failure is the driver's
// (insufficient_privilege, 42501, until the role holds the grants of sql/application-role-privileges-1.11.sql). It
// never writes.
func (s *Store) QueryProviderHoldoutOutcomes(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	providerKey string, limit int, afterRunID *ae.UUID) ([]ae.ProviderHoldoutOutcomeRow, error) {
	if err := ae.ValidateHoldoutOutcomesLimit(limit); err != nil {
		return nil, err
	}
	if err := ae.ValidateProviderKey(providerKey); err != nil {
		return nil, err
	}
	if !bounds.Permits(scope) {
		return nil, ae.ErrAuthorizationDenied
	}
	values := map[string]any{
		"tenant_id":      scope.TenantID,
		"application_id": scope.ApplicationID,
		"project_id":     scope.ProjectID,
		"team_id":        scope.TeamID,
		"agent_id":       scope.AgentID,
		"user_id":        scope.UserID,
		"provider_key":   providerKey,
		"limit":          int32(limit),
	}
	statement := s.providerOutcomesFirst
	if afterRunID != nil {
		statement = s.providerOutcomesNext
		values["after_run_id"] = pgtype.UUID{Bytes: *afterRunID, Valid: true}
	}
	args, err := statement.Args(values)
	if err != nil {
		return nil, err
	}
	page := []ae.ProviderHoldoutOutcomeRow{}
	err = s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		rows, err := tx.Query(ctx, statement.SQL, args...)
		if err != nil {
			return err
		}
		defer rows.Close()
		for rows.Next() {
			var recordID, runID pgtype.UUID
			var payloadVersion *int64
			var payload []byte
			var arm *string
			var assignedAt *time.Time
			// Columns 1 to 18 are the run's own record (only its ID, payload version and payload are read), 19 to 21
			// the row's run, arm and instant.
			targets := make([]any, 21)
			targets[0], targets[16], targets[17] = &recordID, &payloadVersion, &payload
			targets[18], targets[19], targets[20] = &runID, &arm, &assignedAt
			if err := rows.Scan(targets...); err != nil {
				return err
			}
			if !runID.Valid || arm == nil || assignedAt == nil {
				return errors.New("the provider holdout outcome read returned a row with a null column")
			}
			outcome, _, err := readOutcomeRecord(recordID.Valid, payloadVersion, payload)
			if err != nil {
				return err
			}
			at, err := ae.InstantFromTime(*assignedAt)
			if err != nil {
				return err
			}
			page = append(page, ae.ProviderHoldoutOutcomeRow{RunID: runID.Bytes, ProviderKey: providerKey,
				Arm: ae.HoldoutArm(*arm), AssignedAt: at, RunOutcome: outcome})
		}
		return rows.Err()
	})
	if err != nil {
		return nil, err
	}
	return page, nil
}

var _ ae.ProviderHoldoutOutcomeReader = (*Store)(nil)
