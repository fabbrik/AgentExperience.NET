package inmemory

// Contract 1.8 on the in-memory store: the holdout-record fixtures, each record step through
// RecordHoldoutAssignment, each erasure through Erase (an eraseDuringRecord step is its erasure, then its record), the
// clamp on the store's own clock.

import (
	"context"
	"errors"
	"path/filepath"
	"strconv"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// postgresText is an instant as PostgreSQL writes a timestamptz in UTC: 2026-03-02 10:00:00.123456+00.
func postgresText(instant ae.Instant) string {
	return instant.Time().UTC().Format("2006-01-02 15:04:05.999999") + "+00"
}

func TestHoldoutRecordFixtures(t *testing.T) {
	for _, name := range ct.Cases("holdout-record") {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			read := func(file string) *ae.Object {
				return ct.MustReadJSON(filepath.Join(ct.Fixtures, "holdout-record", name, file)).(*ae.Object)
			}
			// The store's clock: the database clock the clamp reads.
			clock := ae.MustParseInstant("2026-10-27T12:00:00.1234567+00:00")
			store, err := New()
			if err != nil {
				t.Fatal(err)
			}
			store.Clock = func() ae.Instant { return clock }
			for _, stored := range get(read("seed.json"), "records").([]any) {
				record, err := ae.FromStored(stored)
				if err != nil {
					t.Fatal(err)
				}
				if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
					t.Fatal(err)
				}
			}
			record := func(step *ae.Object) {
				scope, assignment, err := ct.HoldoutStepOf(step)
				if err != nil {
					t.Fatal(err)
				}
				if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, assignment); err != nil {
					t.Fatal(err)
				}
			}
			erase := func(step *ae.Object) {
				if !store.Erase(ae.MustParseUUID(get(step, "experienceId").(string))) {
					t.Fatal("nothing to erase")
				}
			}
			for _, item := range get(read("steps.json"), "steps").([]any) {
				step := item.(*ae.Object)
				if value, ok := step.Get("record"); ok {
					record(value.(*ae.Object))
				} else if value, ok := step.Get("erase"); ok {
					erase(value.(*ae.Object))
				} else {
					race := get(step, "eraseDuringRecord").(*ae.Object)
					erase(get(race, "erase").(*ae.Object))
					record(get(race, "record").(*ae.Object))
				}
			}

			actual := []any{}
			for _, row := range store.HoldoutRows() {
				at := postgresText(row.AssignedAt)
				if row.AssignedAt == clock.TruncateToMicroseconds() {
					at = "database-clock"
				}
				var team, agent, user any
				if row.Scope.TeamID != nil {
					team = *row.Scope.TeamID
				}
				if row.Scope.AgentID != nil {
					agent = *row.Scope.AgentID
				}
				if row.Scope.UserID != nil {
					user = *row.Scope.UserID
				}
				actual = append(actual, ae.NewObject(
					ae.Member{Key: "run_id", Value: row.RunID.String()},
					ae.Member{Key: "experience_id", Value: row.ExperienceID.String()},
					ae.Member{Key: "revision", Value: strconv.FormatInt(row.Revision, 10)},
					ae.Member{Key: "arm", Value: string(row.Arm)},
					ae.Member{Key: "tenant_id", Value: row.Scope.TenantID},
					ae.Member{Key: "application_id", Value: row.Scope.ApplicationID},
					ae.Member{Key: "project_id", Value: row.Scope.ProjectID},
					ae.Member{Key: "team_id", Value: team},
					ae.Member{Key: "agent_id", Value: agent},
					ae.Member{Key: "user_id", Value: user},
					ae.Member{Key: "assigned_at", Value: at},
				))
			}
			expected := get(read("expected.json"), "experience_holdout_assignments")
			if difference := ct.Difference(expected, actual, "$.experience_holdout_assignments", ct.Options{}); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

func TestRecordHoldoutAssignmentRefusesAScopeOutsideTheBoundsAnInvalidAssignmentAndADoneContext(t *testing.T) {
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p", AgentID: ae.Ptr("a")}
	other := scope
	other.AgentID = ae.Ptr("b")
	valid := ae.HoldoutAssignment{RunID: ae.NewRandomUUID(), Arm: ae.HoldoutTreatment,
		Records: []ae.RunExposure{{ExperienceID: ae.NewRandomUUID(), Revision: 1}}, AssignedAt: ae.Now()}
	ctx := context.Background()
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), other, valid); !errors.Is(err, ErrAuthorizationDenied) {
		t.Fatalf("another scope: %v", err)
	}
	invalid := valid
	invalid.RunID = ae.UUID{}
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, invalid); err == nil {
		t.Fatal("a nil run was accepted")
	}
	done, cancel := context.WithCancel(ctx)
	cancel()
	if err := store.RecordHoldoutAssignment(done, ae.BoundsOf(scope), scope, valid); err == nil {
		t.Fatal("a done context was accepted")
	}
	if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, valid); err != nil || len(store.HoldoutRows()) != 0 {
		t.Fatalf("an unknown record: %v, %v", err, store.HoldoutRows())
	}
}

func TestAnErasedRecordIsNeitherReadNorStoredAgain(t *testing.T) {
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	stored := ct.MustReadJSON(filepath.Join(ct.Fixtures, "holdout-record", "erased-lesson", "seed.json")).(*ae.Object)
	value, _ := stored.Get("records")
	record, err := ae.FromStored(value.([]any)[0])
	if err != nil {
		t.Fatal(err)
	}
	ctx, bounds := context.Background(), ae.BoundsOf(record.Scope)
	if err := store.InsertRecord(ctx, bounds, &record); err != nil {
		t.Fatal(err)
	}
	if !store.Erase(record.ExperienceID) || store.Erase(record.ExperienceID) {
		t.Fatal("erase")
	}
	if got, _ := store.Get(ctx, bounds, record.Scope, record.ExperienceID); got != nil || len(store.Records()) != 0 {
		t.Fatal("the erased record is still read")
	}
	if err := store.InsertRecord(ctx, bounds, &record); err == nil {
		t.Fatal("an erased record's ID was stored again")
	}
}
