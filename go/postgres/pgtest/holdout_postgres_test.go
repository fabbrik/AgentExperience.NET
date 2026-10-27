//go:build postgres

package pgtest_test

// Contract 1.8's holdout-record fixtures, each record step through the store's RecordHoldoutAssignment (the embedded
// record-holdout-assignment.sql), the late-write guard's race staged as the contract says, and what the store does
// without the 1.8 grants.

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

// holdoutColumns are agent_experience.experience_holdout_assignments' columns, in table order.
var holdoutColumns = []string{"run_id", "experience_id", "revision", "arm", "tenant_id", "application_id",
	"project_id", "team_id", "agent_id", "user_id", "assigned_at"}

func TestHoldoutRecord(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases("holdout-record") {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			seed := ct.MustReadJSON(fixture("holdout-record", name, "seed.json")).(*ae.Object)
			steps := get(ct.MustReadJSON(fixture("holdout-record", name, "steps.json")).(*ae.Object), "steps").([]any)
			expected := get(ct.MustReadJSON(fixture("holdout-record", name, "expected.json")).(*ae.Object),
				"experience_holdout_assignments").([]any)

			database := createDatabase(t, admin, "holdout_"+name)
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
					if err := holdoutStep(store, get(step, "record").(*ae.Object)); err != nil {
						t.Fatal(err)
					}
				case has(step, "erase"):
					eraseStep(t, owner, get(step, "erase").(*ae.Object))
				default:
					race := get(step, "eraseDuringRecord").(*ae.Object)
					eraseDuringRecord(t, admin, database, get(race, "erase").(*ae.Object), func() error {
						return holdoutStep(store, get(race, "record").(*ae.Object))
					})
				}
			}
			after := clockOf(t, owner)
			checkHoldoutRows(t, owner, expected, before, after)
		})
	}
}

func has(o *ae.Object, key string) bool { _, ok := o.Get(key); return ok }

// holdoutStep is a record step: the store's RecordHoldoutAssignment.
func holdoutStep(store *postgres.Store, record *ae.Object) error {
	scope, assignment, err := ct.HoldoutStepOf(record)
	if err != nil {
		return err
	}
	return store.RecordHoldoutAssignment(context.Background(), ae.BoundsOf(scope), scope, assignment)
}

// eraseDuringRecord is the late-write guard's race: the erasure runs as the owner in a transaction it does not yet
// commit; the record call starts, and once its backend is blocked by the erasing one, the erasure commits and the
// record call is awaited. The record call must not complete before the commit.
func eraseDuringRecord(t *testing.T, admin server, database string, erase *ae.Object, record func() error) {
	t.Helper()
	ctx := context.Background()
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	purging, err := connect(ctx, admin.Owner(database))
	if err != nil {
		t.Fatal(err)
	}
	defer purging.Close(ctx)
	tx, err := purging.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var purger int32
	if err := tx.QueryRow(ctx, "SELECT pg_catalog.pg_backend_pid()").Scan(&purger); err != nil {
		t.Fatal(err)
	}
	scope := ct.ScopeOf(get(erase, "scope"))
	var outcome string
	if err := tx.QueryRow(ctx, "SELECT purge_outcome FROM agent_experience.purge_experience_record("+
		"$1::text::uuid, $2, $3, $4, $5, $6, $7, NULL, $8::text::timestamptz)",
		get(erase, "experienceId"), scope.TenantID, scope.ApplicationID, scope.ProjectID, scope.TeamID,
		scope.AgentID, scope.UserID, get(erase, "deletedAt")).Scan(&outcome); err != nil || outcome != "Deleted" {
		t.Fatalf("the erasure answered %q, %v", outcome, err)
	}

	done := make(chan error, 1)
	go func() { done <- record() }()
	watcher := ownerConn(t, admin, database)
	deadline := time.Now().Add(30 * time.Second)
	for {
		select {
		case err := <-done:
			t.Fatalf("the record call completed while the erasure held the run's record (%v): it never waited", err)
		default:
		}
		var blocked bool
		if err := watcher.QueryRow(ctx, "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity a "+
			"WHERE a.datname = pg_catalog.current_database() AND $1 = ANY (pg_catalog.pg_blocking_pids(a.pid)))",
			purger).Scan(&blocked); err != nil {
			t.Fatal(err)
		}
		if blocked {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("the record call never blocked on the erasure")
		}
		time.Sleep(20 * time.Millisecond)
	}
	if err := tx.Commit(ctx); err != nil {
		t.Fatal(err)
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

// checkHoldoutRows compares every row of the holdout table with expected; a clamped instant must lie between before
// and after.
func checkHoldoutRows(t *testing.T, owner *pgx.Conn, expected []any, before, after time.Time) {
	t.Helper()
	ctx := context.Background()
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	clamped := map[[2]string]bool{}
	for _, item := range expected {
		row := item.(*ae.Object)
		if get(row, "assigned_at") == databaseClock {
			clamped[[2]string{get(row, "run_id").(string), get(row, "experience_id").(string)}] = true
		}
	}
	if _, err := owner.Exec(ctx, "SET TIME ZONE 'UTC'"); err != nil {
		t.Fatal(err)
	}
	rows, err := owner.Query(ctx, "SELECT run_id::text, experience_id::text, revision::text, arm, tenant_id, "+
		"application_id, project_id, team_id, agent_id, user_id, assigned_at::text, assigned_at "+
		"FROM agent_experience.experience_holdout_assignments ORDER BY run_id, experience_id")
	if err != nil {
		t.Fatal(err)
	}
	actual := []any{}
	for rows.Next() {
		values := make([]*string, len(holdoutColumns))
		targets := make([]any, 0, len(holdoutColumns)+1)
		for i := range values {
			targets = append(targets, &values[i])
		}
		var at time.Time
		targets = append(targets, &at)
		if err := rows.Scan(targets...); err != nil {
			t.Fatal(err)
		}
		row := ae.NewObject()
		for i, column := range holdoutColumns {
			if values[i] == nil {
				row.Set(column, nil)
			} else {
				row.Set(column, *values[i])
			}
		}
		if clamped[[2]string{*values[0], *values[1]}] {
			if at.Before(before) || at.After(after) {
				t.Fatalf("%s/%s: %v is not the database clock during the steps (%v to %v)", *values[0], *values[1],
					at, before, after)
			}
			row.Set("assigned_at", databaseClock)
		}
		actual = append(actual, row)
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if difference := ct.Difference(expected, actual, "$.experience_holdout_assignments", ct.Options{}); difference != "" {
		t.Fatal(difference)
	}
}

func TestWithoutTheContract18GrantsRecordingAHoldoutAssignmentFailsAndWritesNothing(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	privileges := pgtest.PrivilegesOf(t, pgtest.ApplicationRole, pgtest.PrivilegesFile)
	privileges = append(privileges, pgtest.PrivilegesOf(t, pgtest.ApplicationRole, pgtest.Privileges14File)...)
	database := pgtest.CreateDatabaseWith(t, admin, "holdout_no_grant", privileges)
	store := newStore(t, admin, database)
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	record := finalized.Record
	bounds := ae.BoundsOf(record.Scope)
	if err := store.InsertFinalized(ctx, bounds, &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}
	err := store.RecordHoldoutAssignment(ctx, bounds, record.Scope, ae.HoldoutAssignment{RunID: ae.NewRandomUUID(),
		Arm: ae.HoldoutWithheld, Records: []ae.RunExposure{{ExperienceID: record.ExperienceID, Revision: 1}},
		AssignedAt: ae.Now()})
	var pgErr *pgconn.PgError
	if !errors.As(err, &pgErr) || pgErr.Code != "42501" {
		t.Fatalf("recording without the grants answered %v, not insufficient_privilege", err)
	}
	var count int
	if err := ownerConn(t, admin, database).QueryRow(ctx,
		"SELECT count(*) FROM agent_experience.experience_holdout_assignments").Scan(&count); err != nil || count != 0 {
		t.Fatalf("%d rows, %v", count, err)
	}
}

func TestRecordHoldoutAssignmentRefusesAScopeOutsideTheBoundsAndAnInvalidAssignment(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "holdout_refused"))
	scope := ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project",
		AgentID: ae.Ptr("a")}
	other := scope
	other.AgentID = ae.Ptr("b")
	valid := ae.HoldoutAssignment{RunID: ae.NewRandomUUID(), Arm: ae.HoldoutTreatment,
		Records: []ae.RunExposure{{ExperienceID: ae.NewRandomUUID(), Revision: 1}}, AssignedAt: ae.Now()}
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), other, valid); !errors.Is(err, ae.ErrAuthorizationDenied) {
		t.Fatalf("another scope: %v", err)
	}
	invalid := valid
	invalid.Arm = "Control"
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, invalid); err == nil {
		t.Fatal("an unknown arm was accepted")
	}
	// An unknown record writes nothing and is no error.
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, valid); err != nil {
		t.Fatal(err)
	}
	empty := valid
	empty.Records = nil
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, empty); err != nil {
		t.Fatal(err)
	}
}
