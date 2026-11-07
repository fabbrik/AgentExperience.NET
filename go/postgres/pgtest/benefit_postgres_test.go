//go:build postgres

package pgtest_test

// Contract 1.9's holdout-outcomes fixtures through the store's QueryHoldoutOutcomes (the embedded
// query-holdout-outcomes.sql), and the estimate over a sealed run record.

import (
	"context"
	"errors"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
)

// seedOutcomes inserts a case's records as the application role under their own scope's bounds and its assignment
// rows, from their PostgreSQL text form, as the schema's owner.
func seedOutcomes(t *testing.T, store *postgres.Store, owner *pgx.Conn, c *ct.OutcomesCase) {
	t.Helper()
	ctx := context.Background()
	for i := range c.Records {
		if err := store.InsertRecord(ctx, ae.BoundsOf(c.Records[i].Scope), &c.Records[i]); err != nil {
			t.Fatal(err)
		}
	}
	for _, row := range c.Assignments {
		values := make([]any, len(ct.AssignmentColumns))
		for i, column := range ct.AssignmentColumns {
			values[i], _ = row.Raw.Get(column)
		}
		if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_holdout_assignments (run_id, "+
			"experience_id, revision, arm, tenant_id, application_id, project_id, team_id, agent_id, user_id, "+
			"assigned_at) VALUES ($1::text::uuid, $2::text::uuid, $3::text::bigint, $4, $5, $6, $7, $8, $9, $10, "+
			"$11::text::timestamptz)", values...); err != nil {
			t.Fatal(err)
		}
	}
	for _, erased := range c.Erased {
		eraseStep(t, owner, erased)
	}
}

func TestHoldoutOutcomes(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases(ct.HoldoutOutcomesFamily) {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadOutcomesCase(name)
			if err != nil {
				t.Fatal(err)
			}
			database := createDatabase(t, admin, "outcomes_"+name)
			store := newStore(t, admin, database)
			seedOutcomes(t, store, ownerConn(t, admin, database), c)
			pages, err := ct.ReadPages(func(after *ae.HoldoutOutcomeCursor) ([]ae.HoldoutOutcomeRow, error) {
				return store.QueryHoldoutOutcomes(context.Background(), ae.BoundsOf(c.Scope), c.Scope, c.Limit, after)
			}, c.Limit)
			if err != nil {
				t.Fatal(err)
			}
			if d := ct.PagesDifference(c, pages); d != "" {
				t.Fatal(d)
			}
		})
	}
}

func TestASealedRunRecordCountsAsPendingAndAForeignScopeIsDenied(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	c, err := ct.LoadOutcomesCase("models")
	if err != nil {
		t.Fatal(err)
	}
	database := createDatabase(t, admin, "outcomes_sealed")
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	seedOutcomes(t, store, owner, c)

	before, err := ae.EstimateBenefit(ctx, store, ae.BoundsOf(c.Scope), c.Scope, ae.BenefitOptions{})
	if err != nil {
		t.Fatal(err)
	}
	// A sealed record (payload_version 2, opaque payload, as .NET's sealing writes one), created before the run's own
	// record, becomes the run's record: the port cannot read it, so the run has no outcome and counts as pending.
	run := ae.MustParseUUID("00000081-0000-4000-8000-00000000010b")
	scope := c.Scope
	if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, "+
		"tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, "+
		"supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, "+
		"search_vector_sealed) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, '(sealed)', 'Validated', 0.9, 1, 0, 1, "+
		"'2000-01-01T00:00:00Z', '2000-01-01T00:00:00Z', 2, $9::jsonb, to_tsvector('english', 'sealed'))",
		[16]byte(ae.MustParseUUID("00000081-0000-4000-8000-0000000005ea")), [16]byte(run), scope.TenantID,
		scope.ApplicationID, scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID,
		`{"sealed": "aexp-sealed:v1:AAAA"}`); err != nil {
		t.Fatal(err)
	}
	rows, err := store.QueryHoldoutOutcomes(ctx, ae.BoundsOf(c.Scope), c.Scope, 1000, nil)
	if err != nil {
		t.Fatal(err)
	}
	sealedRows := 0
	for _, row := range rows {
		if row.RunID == run {
			sealedRows++
			if row.RunOutcome != nil || row.RunModel != nil {
				t.Fatalf("a sealed record was read: %+v", row)
			}
		}
	}
	after, err := ae.EstimateBenefit(ctx, store, ae.BoundsOf(c.Scope), c.Scope, ae.BenefitOptions{})
	if err != nil {
		t.Fatal(err)
	}
	if sealedRows == 0 || after.Overall.Pending != before.Overall.Pending+1 ||
		after.Overall.Treatment.Runs != before.Overall.Treatment.Runs-1 {
		t.Fatalf("before %+v, after %+v", before.Overall, after.Overall)
	}

	foreign := ae.AuthorizationBounds{TenantID: "another-tenant"}
	if _, err := store.QueryHoldoutOutcomes(ctx, foreign, c.Scope, 10, nil); !errors.Is(err, ae.ErrAuthorizationDenied) {
		t.Fatalf("foreign scope: %v", err)
	}
	denied, err := ae.EstimateBenefit(ctx, store, foreign, c.Scope, ae.BenefitOptions{})
	if err != nil || denied.Outcome != ae.BenefitDenied {
		t.Fatalf("denied: %+v %v", denied, err)
	}
}
