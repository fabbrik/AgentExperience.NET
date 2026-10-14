//go:build postgres

package pgtest_test

// Contract 1.4's record-injection fixtures, each step through the store's RecordInjection (the embedded
// record-injection.sql), and what the store does without the 1.4 grants.

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

// injectionColumns are agent_experience.experience_injections' columns, in table order.
var injectionColumns = []string{"experience_id", "tenant_id", "application_id", "project_id", "team_id", "agent_id",
	"user_id", "last_injected_at", "injection_count"}

// databaseClock is an expected last_injected_at the database clock chose.
const databaseClock = "database-clock"

func ownerConn(t *testing.T, admin server, database string) *pgx.Conn {
	t.Helper()
	conn, err := connect(context.Background(), admin.Owner(database))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close(context.Background()) })
	return conn
}

func clockOf(t *testing.T, conn *pgx.Conn) time.Time {
	t.Helper()
	var now time.Time
	if err := conn.QueryRow(context.Background(), "SELECT pg_catalog.clock_timestamp()").Scan(&now); err != nil {
		t.Fatal(err)
	}
	return now
}

func TestRecordInjection(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases("record-injection") {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			seed := ct.MustReadJSON(fixture("record-injection", name, "seed.json")).(*ae.Object)
			steps := get(ct.MustReadJSON(fixture("record-injection", name, "steps.json")).(*ae.Object), "steps").([]any)
			expected := get(ct.MustReadJSON(fixture("record-injection", name, "expected.json")).(*ae.Object),
				"experience_injections").([]any)

			database := createDatabase(t, admin, "inject_"+name)
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
			for _, item := range get(seed, "injections").([]any) {
				row := item.(*ae.Object)
				values := make([]any, len(injectionColumns))
				for i, column := range injectionColumns {
					values[i] = get(row, column)
				}
				if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_injections (experience_id, "+
					"tenant_id, application_id, project_id, team_id, agent_id, user_id, last_injected_at, "+
					"injection_count) VALUES ($1::text::uuid, $2, $3, $4, $5, $6, $7, $8::text::timestamptz, "+
					"$9::text::bigint)", values...); err != nil {
					t.Fatal(err)
				}
			}

			before := clockOf(t, owner)
			for _, item := range steps {
				step := item.(*ae.Object)
				if record, ok := step.Get("record"); ok {
					record := record.(*ae.Object)
					scope := ct.ScopeOf(get(record, "scope"))
					var ids []ae.UUID
					for _, text := range get(record, "experienceIds").([]any) {
						id, err := ae.ParseUUID(text.(string))
						if err != nil {
							t.Fatal(err)
						}
						ids = append(ids, id)
					}
					at, err := ae.ParseInstant(get(record, "injectedAt").(string))
					if err != nil {
						t.Fatal(err)
					}
					if err := store.RecordInjection(ctx, ae.BoundsOf(scope), scope, ids, at); err != nil {
						t.Fatal(err)
					}
					continue
				}
				erase := get(step, "erase").(*ae.Object)
				scope := ct.ScopeOf(get(erase, "scope"))
				var outcome string
				if err := owner.QueryRow(ctx, "SELECT purge_outcome FROM agent_experience.purge_experience_record("+
					"$1::text::uuid, $2, $3, $4, $5, $6, $7, NULL, $8::text::timestamptz)",
					get(erase, "experienceId"), scope.TenantID, scope.ApplicationID, scope.ProjectID, scope.TeamID,
					scope.AgentID, scope.UserID, get(erase, "deletedAt")).Scan(&outcome); err != nil {
					t.Fatal(err)
				}
				if outcome != "Deleted" {
					t.Fatalf("the erasure answered %s", outcome)
				}
			}
			after := clockOf(t, owner)

			clamped := map[string]bool{}
			for _, item := range expected {
				row := item.(*ae.Object)
				if get(row, "last_injected_at") == databaseClock {
					clamped[get(row, "experience_id").(string)] = true
				}
			}
			if _, err := owner.Exec(ctx, "SET TIME ZONE 'UTC'"); err != nil {
				t.Fatal(err)
			}
			rows, err := owner.Query(ctx, "SELECT experience_id::text, tenant_id, application_id, project_id, "+
				"team_id, agent_id, user_id, last_injected_at::text, injection_count::text, last_injected_at "+
				"FROM agent_experience.experience_injections ORDER BY experience_id")
			if err != nil {
				t.Fatal(err)
			}
			actual := []any{}
			for rows.Next() {
				values := make([]*string, len(injectionColumns))
				targets := make([]any, 0, len(injectionColumns)+1)
				for i := range values {
					targets = append(targets, &values[i])
				}
				var at time.Time
				targets = append(targets, &at)
				if err := rows.Scan(targets...); err != nil {
					t.Fatal(err)
				}
				row := ae.NewObject()
				for i, column := range injectionColumns {
					if values[i] == nil {
						row.Set(column, nil)
					} else {
						row.Set(column, *values[i])
					}
				}
				if clamped[*values[0]] {
					if at.Before(before) || at.After(after) {
						t.Fatalf("%s: %v is not the database clock during the steps (%v to %v)", *values[0], at, before, after)
					}
					row.Set("last_injected_at", databaseClock)
				}
				actual = append(actual, row)
			}
			if err := rows.Err(); err != nil {
				t.Fatal(err)
			}
			if difference := ct.Difference(expected, actual, "$.experience_injections", ct.Options{}); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

func TestWithoutTheContract14GrantsRecordingAnInjectionFailsAndWritesNothing(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := pgtest.CreateDatabaseWith(t, admin, "inject_no_grant",
		pgtest.PrivilegesOf(t, pgtest.ApplicationRole, pgtest.PrivilegesFile))
	store := newStore(t, admin, database)
	finalized, _, err := ct.RunFinalizeCase(ct.MustReadJSON(fixture("finalize", "verified-after-one-failed-attempt",
		"input.json")))
	if err != nil {
		t.Fatal(err)
	}
	record := finalized.Record
	bounds := ae.BoundsOf(record.Scope)
	if err := store.InsertFinalized(ctx, bounds, &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}
	err = store.RecordInjection(ctx, bounds, record.Scope, []ae.UUID{record.ExperienceID}, ae.Now())
	var pgErr *pgconn.PgError
	if !errors.As(err, &pgErr) || pgErr.Code != "42501" {
		t.Fatalf("recording without the grants answered %v, not insufficient_privilege", err)
	}
	var count int
	if err := ownerConn(t, admin, database).QueryRow(ctx,
		"SELECT count(*) FROM agent_experience.experience_injections").Scan(&count); err != nil || count != 0 {
		t.Fatalf("%d rows, %v", count, err)
	}
}

func TestRecordInjectionRefusesAScopeOutsideTheBoundsAndMoreThan200IDs(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "inject_refused"))
	scope := ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project",
		AgentID: ae.Ptr("a")}
	other := scope
	other.AgentID = ae.Ptr("b")
	if err := store.RecordInjection(ctx, ae.BoundsOf(scope), other, []ae.UUID{ae.NewRandomUUID()},
		ae.Now()); !errors.Is(err, ae.ErrAuthorizationDenied) {
		t.Fatalf("another scope: %v", err)
	}
	many := make([]ae.UUID, ae.MaxRecordInjectionIDs+1)
	for i := range many {
		many[i] = ae.NewRandomUUID()
	}
	if err := store.RecordInjection(ctx, ae.BoundsOf(scope), scope, many, ae.Now()); err == nil {
		t.Fatal("201 IDs were accepted")
	}
	if err := store.RecordInjection(ctx, ae.BoundsOf(scope), scope, []ae.UUID{{}}, ae.Now()); err == nil {
		t.Fatal("a nil UUID was accepted")
	}
	if err := store.RecordInjection(ctx, ae.BoundsOf(scope), scope, nil, ae.Now()); err != nil {
		t.Fatal(err)
	}
}
