//go:build postgres

package pgtest_test

// Contract 1.11's provider-holdout-record fixtures, each record step through the store's
// RecordProviderHoldoutAssignment (the embedded record-provider-holdout-assignment.sql), the late-write guard's race
// staged as the contract says; the provider outcome read (query-provider-holdout-outcomes.sql) and the estimate over
// it; and what the store does without the 1.11 grants.

import (
	"context"
	"errors"
	"slices"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

func TestProviderHoldoutRecord(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases(ct.ProviderHoldoutRecordFamily) {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			seed := ct.MustReadJSON(fixture(ct.ProviderHoldoutRecordFamily, name, "seed.json")).(*ae.Object)
			steps := get(ct.MustReadJSON(fixture(ct.ProviderHoldoutRecordFamily, name, "steps.json")).(*ae.Object),
				"steps").([]any)
			expected := get(ct.MustReadJSON(fixture(ct.ProviderHoldoutRecordFamily, name, "expected.json")).(*ae.Object),
				"experience_provider_holdout_assignments").([]any)

			database := createDatabase(t, admin, "provider_"+name)
			store := newStore(t, admin, database)
			owner := ownerConn(t, admin, database)
			for _, stored := range get(seed, "records").([]any) {
				record, err := ae.FromStored(stored)
				if err != nil {
					t.Fatal(err)
				}
				if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
					t.Fatal(err)
				}
			}

			before := clockOf(t, owner)
			for _, item := range steps {
				step := item.(*ae.Object)
				switch {
				case has(step, "record"):
					if err := providerStep(store, get(step, "record").(*ae.Object)); err != nil {
						t.Fatal(err)
					}
				case has(step, "erase"):
					eraseStep(t, owner, get(step, "erase").(*ae.Object))
				default:
					race := get(step, "eraseDuringRecord").(*ae.Object)
					eraseDuringRecord(t, admin, database, get(race, "erase").(*ae.Object), func() error {
						return providerStep(store, get(race, "record").(*ae.Object))
					})
				}
			}
			after := clockOf(t, owner)
			checkProviderRows(t, owner, expected, before, after)
		})
	}
}

// providerStep is a record step: the store's RecordProviderHoldoutAssignment.
func providerStep(store *postgres.Store, record *ae.Object) error {
	scope, assignment, err := ct.ProviderStepOf(record)
	if err != nil {
		return err
	}
	return store.RecordProviderHoldoutAssignment(context.Background(), ae.BoundsOf(scope), scope, assignment)
}

// checkProviderRows compares every row of the provider holdout table, ordered by tenant, run then key, with expected;
// a clamped instant must lie between before and after.
func checkProviderRows(t *testing.T, owner *pgx.Conn, expected []any, before, after time.Time) {
	t.Helper()
	ctx := context.Background()
	if _, err := owner.Exec(ctx, "SET TIME ZONE 'UTC'"); err != nil {
		t.Fatal(err)
	}
	columns := ct.ProviderAssignmentColumns
	selected := ""
	for _, column := range columns {
		selected += pgx.Identifier{column}.Sanitize() + "::text, "
	}
	rows, err := owner.Query(ctx, "SELECT "+selected+"assigned_at FROM "+
		"agent_experience.experience_provider_holdout_assignments ORDER BY tenant_id, run_id, provider_key")
	if err != nil {
		t.Fatal(err)
	}
	actual := []any{}
	index := 0
	for rows.Next() {
		values := make([]*string, len(columns))
		targets := make([]any, 0, len(columns)+1)
		for i := range values {
			targets = append(targets, &values[i])
		}
		var at time.Time
		targets = append(targets, &at)
		if err := rows.Scan(targets...); err != nil {
			t.Fatal(err)
		}
		row := ae.NewObject()
		for i, column := range columns {
			if values[i] == nil {
				row.Set(column, nil)
			} else {
				row.Set(column, *values[i])
			}
		}
		if index < len(expected) {
			if clock, _ := expected[index].(*ae.Object).Get("assigned_at"); clock == databaseClock {
				if at.Before(before) || at.After(after) {
					t.Fatalf("%s: %v is not the database clock during the steps (%v to %v)", *values[0], at, before, after)
				}
				row.Set("assigned_at", databaseClock)
			}
		}
		actual = append(actual, row)
		index++
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if difference := ct.Difference(expected, actual, "$.experience_provider_holdout_assignments", ct.Options{}); difference != "" {
		t.Fatal(difference)
	}
}

func TestWithoutTheContract111GrantsRecordingAndReadingAProviderHoldoutFail(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	privileges := pgtest.PrivilegesOf(t, pgtest.ApplicationRole, pgtest.PrivilegesFile)
	for _, file := range []string{pgtest.Privileges14File, pgtest.Privileges16File, pgtest.Privileges18File} {
		privileges = append(privileges, pgtest.PrivilegesOf(t, pgtest.ApplicationRole, file)...)
	}
	database := pgtest.CreateDatabaseWith(t, admin, "provider_no_grant", privileges)
	store := newStore(t, admin, database)
	scope := ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project",
		AgentID: ae.Ptr("refund-agent")}
	err := store.RecordProviderHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, ae.ProviderHoldoutAssignment{
		RunID: ae.NewRandomUUID(), ProviderKey: "mem0", Arm: ae.HoldoutWithheld, AssignedAt: ae.Now()})
	var pgErr *pgconn.PgError
	if !errors.As(err, &pgErr) || pgErr.Code != "42501" {
		t.Fatalf("recording without the grants answered %v, not insufficient_privilege", err)
	}
	if _, err := ae.EstimateProviderBenefit(ctx, store, ae.BoundsOf(scope), scope, "mem0", ae.BenefitOptions{}); !errors.As(err, &pgErr) ||
		pgErr.Code != "42501" {
		t.Fatalf("estimating without the grants answered %v, not insufficient_privilege", err)
	}
	var count int
	if err := ownerConn(t, admin, database).QueryRow(ctx,
		"SELECT count(*) FROM agent_experience.experience_provider_holdout_assignments").Scan(&count); err != nil || count != 0 {
		t.Fatalf("%d rows, %v", count, err)
	}
}

func TestTheProviderOutcomeReadPagesOneKeyInTheScopeAndASealedRunRecordCountsAsPending(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "provider_outcomes")
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	record := finalized.Record
	scope, bounds := record.Scope, ae.BoundsOf(record.Scope)
	if err := store.InsertFinalized(ctx, bounds, &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}
	sealedRun := ae.MustParseUUID("000000da-0000-4000-8000-000000000002")
	if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, "+
		"tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, "+
		"supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, "+
		"search_vector_sealed) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, '(sealed)', 'Validated', 0.9, 1, 0, 1, "+
		"'2000-01-01T00:00:00Z', '2000-01-01T00:00:00Z', 2, $9::jsonb, to_tsvector('english', 'sealed'))",
		[16]byte(ae.MustParseUUID("000000da-0000-4000-8000-0000000005ea")), [16]byte(sealedRun), scope.TenantID,
		scope.ApplicationID, scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID,
		`{"sealed": "aexp-sealed:v1:AAAA"}`); err != nil {
		t.Fatal(err)
	}
	runs := []ae.UUID{ae.MustParseUUID("000000da-0000-4000-8000-000000000003"), record.SourceRunID, sealedRun,
		ae.MustParseUUID("000000da-0000-4000-8000-000000000001")}
	for i, run := range runs {
		arm := ae.HoldoutTreatment
		if i%2 == 1 {
			arm = ae.HoldoutWithheld
		}
		for _, key := range []string{"mem0", "zep"} {
			if err := store.RecordProviderHoldoutAssignment(ctx, bounds, scope, ae.ProviderHoldoutAssignment{
				RunID: run, ProviderKey: key, Arm: arm, AssignedAt: ae.Now()}); err != nil {
				t.Fatal(err)
			}
		}
	}
	var read []ae.ProviderHoldoutOutcomeRow
	var after *ae.UUID
	for {
		page, err := store.QueryProviderHoldoutOutcomes(ctx, bounds, scope, "mem0", 3, after)
		if err != nil {
			t.Fatal(err)
		}
		read = append(read, page...)
		if len(page) < 3 {
			break
		}
		after = &page[len(page)-1].RunID
	}
	if len(read) != 4 || !slices.IsSortedFunc(read, func(a, b ae.ProviderHoldoutOutcomeRow) int {
		return slices.Compare(a.RunID[:], b.RunID[:])
	}) {
		t.Fatalf("%+v", read)
	}
	for _, row := range read {
		switch {
		case row.ProviderKey != "mem0":
			t.Fatalf("%+v", row)
		case row.RunID == record.SourceRunID:
			if row.RunOutcome == nil || *row.RunOutcome != record.Outcome.Status {
				t.Fatalf("the run's own record: %+v", row)
			}
		case row.RunOutcome != nil:
			t.Fatalf("a run with no readable record has an outcome: %+v", row)
		}
	}
	estimate, err := ae.EstimateProviderBenefit(ctx, store, bounds, scope, "zep", ae.BenefitOptions{})
	if err != nil || !estimate.Tracked || estimate.Outcome != ae.BenefitFound || estimate.Overall.Pending != 3 ||
		estimate.Overall.Holdout.Runs != 1 {
		t.Fatalf("%+v %v", estimate, err)
	}
	foreign := ae.AuthorizationBounds{TenantID: "another-tenant"}
	if _, err := store.QueryProviderHoldoutOutcomes(ctx, foreign, scope, "mem0", 10, nil); !errors.Is(err, ae.ErrAuthorizationDenied) {
		t.Fatalf("foreign scope: %v", err)
	}
	denied, err := ae.EstimateProviderBenefit(ctx, store, foreign, scope, "mem0", ae.BenefitOptions{})
	if err != nil || denied.Outcome != ae.BenefitDenied {
		t.Fatalf("denied: %+v %v", denied, err)
	}
	if err := store.RecordProviderHoldoutAssignment(ctx, foreign, scope, ae.ProviderHoldoutAssignment{
		RunID: runs[0], ProviderKey: "mem0", Arm: ae.HoldoutTreatment, AssignedAt: ae.Now()}); !errors.Is(err, ae.ErrAuthorizationDenied) {
		t.Fatalf("recording in a foreign scope: %v", err)
	}
}
