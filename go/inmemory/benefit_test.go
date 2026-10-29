package inmemory

// Contract 1.9 on the in-memory store: the holdout-outcomes fixtures, the records held and the assignment rows recorded
// as seed.json names them, read page after page through QueryHoldoutOutcomes; and the estimate over the store.

import (
	"context"
	"errors"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// seededStore holds a holdout-outcomes case's records and assignment rows.
func seededStore(t *testing.T, c *ct.OutcomesCase) *Store {
	t.Helper()
	ctx := context.Background()
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	// A clock past every seeded instant, so none is clamped.
	store.Clock = func() ae.Instant { return ae.MustParseInstant("2100-01-01T00:00:00+00:00") }
	for i := range c.Records {
		if err := store.InsertRecord(ctx, ae.BoundsOf(c.Records[i].Scope), &c.Records[i]); err != nil {
			t.Fatal(err)
		}
	}
	for _, row := range c.Assignments {
		if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(row.Scope), row.Scope, ae.HoldoutAssignment{
			RunID: row.RunID, Arm: row.Arm, AssignedAt: row.AssignedAt,
			Records: []ae.RunExposure{{ExperienceID: row.ExperienceID, Revision: row.Revision}}}); err != nil {
			t.Fatal(err)
		}
	}
	if got := len(store.HoldoutRows()); got != len(c.Assignments) {
		t.Fatalf("%d assignment rows held, %d seeded", got, len(c.Assignments))
	}
	return store
}

func TestHoldoutOutcomesFixtures(t *testing.T) {
	names := ct.Cases(ct.HoldoutOutcomesFamily)
	if len(names) == 0 {
		t.Fatal("no cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadOutcomesCase(name)
			if err != nil {
				t.Fatal(err)
			}
			store := seededStore(t, c)
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

func TestTheOutcomeQueryRefusesAForeignScopeAndABadLimit(t *testing.T) {
	c, err := ct.LoadOutcomesCase("models")
	if err != nil {
		t.Fatal(err)
	}
	store := seededStore(t, c)
	ctx := context.Background()
	foreign := ae.AuthorizationBounds{TenantID: "another-tenant"}
	if _, err := store.QueryHoldoutOutcomes(ctx, foreign, c.Scope, 10, nil); !errors.Is(err, ErrAuthorizationDenied) {
		t.Fatalf("foreign scope: %v", err)
	}
	for _, limit := range []int{0, 1001} {
		if _, err := store.QueryHoldoutOutcomes(ctx, ae.BoundsOf(c.Scope), c.Scope, limit, nil); err == nil {
			t.Fatalf("limit %d was accepted", limit)
		}
	}
	denied, err := ae.EstimateBenefit(ctx, store, foreign, c.Scope, ae.BenefitOptions{})
	if err != nil || denied.Outcome != ae.BenefitDenied || !denied.Tracked || denied.Overall.Pending != 0 {
		t.Fatalf("denied: %+v %v", denied, err)
	}
}

func TestTheEstimateOverTheStoreCountsEveryRunOnce(t *testing.T) {
	c, err := ct.LoadOutcomesCase("exact-scope-and-run-records")
	if err != nil {
		t.Fatal(err)
	}
	store := seededStore(t, c)
	estimate, err := ae.EstimateBenefit(context.Background(), store, ae.BoundsOf(c.Scope), c.Scope,
		ae.BenefitOptions{SplitByModel: true})
	if err != nil {
		t.Fatal(err)
	}
	overall := estimate.Overall
	// Verified (treatment), Failed (holdout), Unknown (treatment), and two runs with no record in the scope.
	if overall.Treatment.Runs != 1 || overall.Treatment.Successes != 1 || overall.Holdout.Runs != 1 ||
		overall.Unverified != 1 || overall.Pending != 2 || overall.MixedArm != 0 || len(estimate.Lessons) != 2 ||
		estimate.Outcome != ae.BenefitFound || !estimate.Tracked {
		t.Fatalf("%+v", estimate)
	}
}
