package agentexperience_test

// Contract 1.10: the benefit-select fixtures, the policy's options, the views and the windowed estimate.

import (
	"context"
	"testing"
	"time"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestBenefitSelect(t *testing.T) {
	names := ct.Cases(ct.BenefitSelectFamily)
	if len(names) == 0 {
		t.Fatal("no cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadBenefitSelectCase(name)
			if err != nil {
				t.Fatal(err)
			}
			actual, err := c.Run()
			if err != nil {
				t.Fatal(err)
			}
			if d := ct.Difference(c.Expected, ct.AsWritten(actual), "$", ct.Options{}); d != "" {
				t.Fatal(d)
			}
		})
	}
}

func TestTheBenefitPolicysDefaultsAndValidation(t *testing.T) {
	var off ae.BenefitPolicy
	if off.Enabled || off.PreferMeasuredHelpful || off.PerModel {
		t.Fatal("the zero policy is not off")
	}
	d := off.WithDefaults()
	if d.Window != 30*24*time.Hour || d.RefreshInterval != 10*time.Minute || d.RefreshTimeout != 30*time.Second ||
		d.MinimumRunsPerArm != 20 {
		t.Fatalf("defaults %+v", d)
	}
	if err := off.Validate(); err != nil {
		t.Fatal(err)
	}
	for _, bad := range []ae.BenefitPolicy{{Window: -1}, {RefreshInterval: -1}, {RefreshTimeout: -1},
		{RefreshTimeout: 24*time.Hour + 1}, {MinimumRunsPerArm: -1}} {
		if bad.Validate() == nil {
			t.Errorf("%+v was accepted", bad)
		}
	}
	if (ae.BenefitPolicy{RefreshTimeout: 24 * time.Hour}).Validate() != nil {
		t.Error("a one-day timeout was refused")
	}
	options := ae.BenefitPolicy{PerModel: true, MinimumRunsPerArm: 3}.EstimatorOptions()
	if !options.SplitByModel || options.MinimumRunsPerArm != 3 {
		t.Fatalf("%+v", options)
	}
	now := ae.MustParseInstant("2026-03-02T10:00:00+00:00")
	at0 := ae.MustParseInstant("0001-01-02T00:00:00+00:00")
	if from := (ae.BenefitPolicy{}).AssignedFrom(now); from != ae.MustParseInstant("2026-01-31T10:00:00+00:00") {
		t.Fatalf("from %v", from)
	}
	if from := (ae.BenefitPolicy{}).AssignedFrom(at0); !from.IsZero() {
		t.Fatalf("a window past the earliest instant: %v", from)
	}
}

func TestAnEstimateTheStoreCouldNotGiveHasNoViews(t *testing.T) {
	estimate := ae.BenefitEstimate{Tracked: true, Outcome: ae.BenefitFound, Lessons: []ae.LessonBenefit{
		{ExperienceID: ae.UUID{15: 1}, Comparison: ae.BenefitComparison{Verdict: ae.Hurts}}}}
	if view := ae.NewBenefitViews(estimate).For(nil); view == nil || !view.Harmful(ae.UUID{15: 1}) {
		t.Fatal("a usable estimate's harmful lesson")
	}
	estimate.Tracked = false
	if ae.NewBenefitViews(estimate) != nil || ae.NewBenefitViews(estimate).For(nil) != nil {
		t.Fatal("an untracked estimate was acted on")
	}
	estimate.Tracked, estimate.Outcome = true, ae.BenefitDenied
	if ae.NewBenefitViews(estimate) != nil {
		t.Fatal("a denied estimate was acted on")
	}
	var none *ae.BenefitView
	if none.Harmful(ae.UUID{15: 1}) {
		t.Fatal("no view omits nothing")
	}
}

// windowStore answers one page of outcome rows.
type windowStore struct{ rows []ae.HoldoutOutcomeRow }

func (w windowStore) QueryHoldoutOutcomes(context.Context, ae.AuthorizationBounds, ae.Scope, int,
	*ae.HoldoutOutcomeCursor) ([]ae.HoldoutOutcomeRow, error) {
	return w.rows, nil
}

func (windowStore) InsertFinalized(context.Context, ae.AuthorizationBounds, *ae.FinalizedExperience, string,
	*time.Time) error {
	return nil
}

func (windowStore) Search(context.Context, ae.AuthorizationBounds, ae.SearchParameters) ([]ae.SearchRow, error) {
	return nil, nil
}

func TestTheWindowedEstimateCountsOnlyRecentAssignments(t *testing.T) {
	verified := ae.VerificationVerified
	at := func(text string) ae.Instant { return ae.MustParseInstant(text) }
	rows := []ae.HoldoutOutcomeRow{
		{RunID: ae.UUID{15: 1}, Arm: ae.HoldoutTreatment, ExperienceID: ae.UUID{14: 1},
			AssignedAt: at("2026-01-01T00:00:00+00:00"), RunOutcome: &verified},
		{RunID: ae.UUID{15: 2}, Arm: ae.HoldoutTreatment, ExperienceID: ae.UUID{14: 1},
			AssignedAt: at("2026-03-01T00:00:00+00:00"), RunOutcome: &verified},
	}
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	all, err := ae.EstimateBenefit(context.Background(), windowStore{rows}, ae.BoundsOf(scope), scope, ae.BenefitOptions{})
	if err != nil || all.Overall.Treatment.Runs != 2 {
		t.Fatalf("%v %+v", err, all.Overall)
	}
	recent, err := ae.EstimateBenefitAssignedFrom(context.Background(), windowStore{rows}, ae.BoundsOf(scope), scope,
		ae.BenefitOptions{}, at("2026-03-01T00:00:00+00:00"))
	if err != nil || recent.Overall.Treatment.Runs != 1 {
		t.Fatalf("%v %+v", err, recent.Overall)
	}
}

// Contract 1.13: the inject-options fixtures.
func TestInjectOptions(t *testing.T) {
	names := ct.Cases(ct.InjectOptionsFamily)
	if len(names) == 0 {
		t.Fatal("no cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadInjectOptionsCase(name)
			if err != nil {
				t.Fatal(err)
			}
			actual, block, err := c.Run()
			if err != nil {
				t.Fatal(err)
			}
			if d := ct.Difference(c.Expected, ct.AsWritten(actual), "$", ct.Options{}); d != "" {
				t.Fatal(d)
			}
			if block != c.ExpectedBlock {
				t.Fatalf("expected:\n%s\nactual:\n%s", c.ExpectedBlock, block)
			}
		})
	}
}
