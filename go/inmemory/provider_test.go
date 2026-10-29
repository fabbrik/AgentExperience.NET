package inmemory

// Contract 1.11 on the in-memory store: the provider-holdout-record fixtures, each record step through
// RecordProviderHoldoutAssignment, each erasure through Erase (an eraseDuringRecord step is its erasure, then its
// record), the clamp on the store's own clock; and the provider outcome read.

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func optionalJSON(value *string) any {
	if value == nil {
		return nil
	}
	return *value
}

func TestProviderHoldoutRecordFixtures(t *testing.T) {
	for _, name := range ct.Cases(ct.ProviderHoldoutRecordFamily) {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			read := func(file string) *ae.Object {
				return ct.MustReadJSON(filepath.Join(ct.Fixtures, ct.ProviderHoldoutRecordFamily, name, file)).(*ae.Object)
			}
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
				scope, assignment, err := ct.ProviderStepOf(step)
				if err != nil {
					t.Fatal(err)
				}
				if err := store.RecordProviderHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, assignment); err != nil {
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
			for _, row := range store.ProviderHoldoutRows() {
				at := postgresText(row.AssignedAt)
				if row.AssignedAt == clock.TruncateToMicroseconds() {
					at = "database-clock"
				}
				actual = append(actual, ae.NewObject(
					ae.Member{Key: "run_id", Value: row.RunID.String()},
					ae.Member{Key: "provider_key", Value: row.ProviderKey},
					ae.Member{Key: "arm", Value: string(row.Arm)},
					ae.Member{Key: "tenant_id", Value: row.Scope.TenantID},
					ae.Member{Key: "application_id", Value: row.Scope.ApplicationID},
					ae.Member{Key: "project_id", Value: row.Scope.ProjectID},
					ae.Member{Key: "team_id", Value: optionalJSON(row.Scope.TeamID)},
					ae.Member{Key: "agent_id", Value: optionalJSON(row.Scope.AgentID)},
					ae.Member{Key: "user_id", Value: optionalJSON(row.Scope.UserID)},
					ae.Member{Key: "assigned_at", Value: at},
				))
			}
			expected := get(read("expected.json"), "experience_provider_holdout_assignments")
			if difference := ct.Difference(expected, actual, "$.experience_provider_holdout_assignments",
				ct.Options{}); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

func TestRecordProviderHoldoutAssignmentRefusesAScopeOutsideTheBoundsAnInvalidAssignmentAndADoneContext(t *testing.T) {
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p", AgentID: ae.Ptr("a")}
	other := scope
	other.AgentID = ae.Ptr("b")
	valid := ae.ProviderHoldoutAssignment{RunID: ae.NewRandomUUID(), ProviderKey: "mem0", Arm: ae.HoldoutTreatment,
		AssignedAt: ae.Now()}
	ctx := context.Background()
	if err := store.RecordProviderHoldoutAssignment(ctx, ae.BoundsOf(scope), other, valid); !errors.Is(err, ErrAuthorizationDenied) {
		t.Fatalf("another scope: %v", err)
	}
	for _, invalid := range []ae.ProviderHoldoutAssignment{
		{RunID: valid.RunID, ProviderKey: "a b", Arm: ae.HoldoutTreatment},
		{ProviderKey: "mem0", Arm: ae.HoldoutTreatment},
		{RunID: valid.RunID, ProviderKey: "mem0", Arm: "Control"},
	} {
		if err := store.RecordProviderHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, invalid); err == nil {
			t.Fatalf("%+v was accepted", invalid)
		}
	}
	done, cancel := context.WithCancel(ctx)
	cancel()
	if err := store.RecordProviderHoldoutAssignment(done, ae.BoundsOf(scope), scope, valid); err == nil {
		t.Fatal("a done context was accepted")
	}
	// A run with no record yet is recorded.
	if err := store.RecordProviderHoldoutAssignment(ctx, ae.BoundsOf(scope), scope, valid); err != nil ||
		len(store.ProviderHoldoutRows()) != 1 {
		t.Fatalf("%v, %v", err, store.ProviderHoldoutRows())
	}
}

func TestTheProviderOutcomeReadPagesOneKeyInExactlyTheScopeWithEachRunsOutcome(t *testing.T) {
	ctx := context.Background()
	seed := ct.MustReadJSON(filepath.Join(ct.Fixtures, ct.ProviderHoldoutRecordFamily, "erased-run", "seed.json")).(*ae.Object)
	value, _ := seed.Get("records")
	first, err := ae.FromStored(value.([]any)[0])
	if err != nil {
		t.Fatal(err)
	}
	store, err := New(first)
	if err != nil {
		t.Fatal(err)
	}
	scope, bounds := first.Scope, ae.BoundsOf(first.Scope)
	runs := []ae.UUID{ae.MustParseUUID("000000d9-0000-4000-8000-000000000003"), first.SourceRunID,
		ae.MustParseUUID("000000d9-0000-4000-8000-000000000001")}
	for i, run := range runs {
		arm := ae.HoldoutTreatment
		if i%2 == 1 {
			arm = ae.HoldoutWithheld
		}
		for _, key := range []string{"mem0", "zep"} {
			if err := store.RecordProviderHoldoutAssignment(ctx, bounds, scope, ae.ProviderHoldoutAssignment{RunID: run,
				ProviderKey: key, Arm: arm, AssignedAt: ae.Now()}); err != nil {
				t.Fatal(err)
			}
		}
	}
	other := scope
	other.AgentID = ae.Ptr("another-agent")
	if err := store.RecordProviderHoldoutAssignment(ctx, ae.BoundsOf(other), other, ae.ProviderHoldoutAssignment{
		RunID: ae.NewRandomUUID(), ProviderKey: "mem0", Arm: ae.HoldoutTreatment, AssignedAt: ae.Now()}); err != nil {
		t.Fatal(err)
	}
	var read []ae.ProviderHoldoutOutcomeRow
	var after *ae.UUID
	for {
		page, err := store.QueryProviderHoldoutOutcomes(ctx, bounds, scope, "mem0", 2, after)
		if err != nil {
			t.Fatal(err)
		}
		read = append(read, page...)
		if len(page) < 2 {
			break
		}
		after = &page[len(page)-1].RunID
	}
	if len(read) != 3 {
		t.Fatalf("%+v", read)
	}
	for i := 1; i < len(read); i++ {
		if compareUUID(read[i-1].RunID, read[i].RunID) >= 0 || read[i].ProviderKey != "mem0" {
			t.Fatalf("%+v", read)
		}
	}
	for _, row := range read {
		if (row.RunID == first.SourceRunID) != (row.RunOutcome != nil) {
			t.Fatalf("%s: outcome %v", row.RunID, row.RunOutcome)
		}
		if row.RunOutcome != nil && *row.RunOutcome != first.Outcome.Status {
			t.Fatalf("%v", *row.RunOutcome)
		}
	}
	if _, err := store.QueryProviderHoldoutOutcomes(ctx, bounds, other, "mem0", 10, nil); !errors.Is(err, ErrAuthorizationDenied) {
		t.Fatalf("another scope: %v", err)
	}
	for _, limit := range []int{0, 1001} {
		if _, err := store.QueryProviderHoldoutOutcomes(ctx, bounds, scope, "mem0", limit, nil); err == nil {
			t.Fatalf("limit %d was accepted", limit)
		}
	}
	if _, err := store.QueryProviderHoldoutOutcomes(ctx, bounds, scope, "a/b", 10, nil); err == nil {
		t.Fatal("a malformed key was accepted")
	}
	estimate, err := ae.EstimateProviderBenefit(ctx, store, bounds, scope, "zep", ae.BenefitOptions{})
	if err != nil || !estimate.Tracked || estimate.Overall.Pending != 2 ||
		estimate.Overall.Treatment.Runs+estimate.Overall.Holdout.Runs+estimate.Overall.Unverified != 1 {
		t.Fatalf("%+v %v", estimate, err)
	}
}
