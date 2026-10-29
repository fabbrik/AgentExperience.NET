package agentexperience_test

// Contract 1.11: the provider-holdout-assign and provider-benefit-estimate fixtures, and the provider estimate's own
// rules (the key, the options, a store that does not track, a denied scope, paging).

import (
	"context"
	"encoding/json"
	"errors"
	"slices"
	"strconv"
	"strings"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestProviderHoldoutAssign(t *testing.T) {
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	number := func(value any) float64 {
		f, err := strconv.ParseFloat(string(value.(json.Number)), 64)
		if err != nil {
			t.Fatal(err)
		}
		return f
	}
	cases := get(ct.MustReadJSON(fixture(ct.ProviderHoldoutAssignFamily, "cases.json")).(*ae.Object), "cases").([]any)
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	for _, item := range cases {
		c := item.(*ae.Object)
		runID := ae.MustParseUUID(get(c, "runId").(string))
		key, salt := get(c, "providerKey").(string), get(c, "salt").(string)
		if valid := ae.ValidProviderKey(key); valid != get(c, "validKey").(bool) {
			t.Errorf("%q: valid %v", key, valid)
		}
		providerSalt := ae.ProviderHoldoutSalt(key, salt)
		if providerSalt != get(c, "providerSalt").(string) {
			t.Errorf("%q with %q: provider salt %q, expected %q", key, salt, providerSalt, get(c, "providerSalt"))
		}
		fraction, expected := ae.HoldoutFraction(runID, providerSalt), number(get(c, "fraction"))
		if strconv.FormatFloat(fraction, 'g', 15, 64) != strconv.FormatFloat(expected, 'g', 15, 64) {
			t.Errorf("%s with %q: fraction %v, expected %v", runID, providerSalt, fraction, expected)
		}
		for _, entry := range get(c, "arms").([]any) {
			arm := entry.(*ae.Object)
			rate := number(get(arm, "rate"))
			if got := ae.AssignProviderHoldout(runID, key, salt, rate); string(got) != get(arm, "arm").(string) {
				t.Errorf("%s under %q/%q at rate %v: %s, expected %s", runID, key, salt, rate, got, get(arm, "arm"))
			}
		}
	}
}

func TestTheProviderKeyRule(t *testing.T) {
	for key, valid := range map[string]bool{
		"mem0": true, "Letta.v2_prod-1": true, strings.Repeat("k", 100): true, strings.Repeat("k", 101): false,
		"": false, "a b": false, "a/b": false, "a:b": false, "é": false, "mem0\n": false,
	} {
		if ae.ValidProviderKey(key) != valid || (ae.ValidateProviderKey(key) == nil) != valid {
			t.Errorf("%q: expected valid %v", key, valid)
		}
	}
	// A key past the BMP counts two UTF-16 units, as .NET's string length does.
	if got := ae.ProviderHoldoutSalt("🧪", "s"); got != "2:🧪/s" {
		t.Fatal(got)
	}
}

// pagedProviderStore answers the provider outcome read from rows held in run order, a page at a time.
type pagedProviderStore struct {
	ae.Store
	rows  []ae.ProviderHoldoutOutcomeRow
	reads int
}

func (s *pagedProviderStore) QueryProviderHoldoutOutcomes(_ context.Context, bounds ae.AuthorizationBounds,
	scope ae.Scope, _ string, limit int, after *ae.UUID) ([]ae.ProviderHoldoutOutcomeRow, error) {
	s.reads++
	if !bounds.Permits(scope) {
		return nil, ae.ErrAuthorizationDenied
	}
	page := []ae.ProviderHoldoutOutcomeRow{}
	for _, row := range s.rows {
		if after != nil && slices.Compare(row.RunID[:], after[:]) <= 0 {
			continue
		}
		if len(page) == limit {
			break
		}
		page = append(page, row)
	}
	return page, nil
}

var providerScope = ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p", AgentID: ae.Ptr("agent")}

func TestProviderBenefitEstimate(t *testing.T) {
	names := ct.Cases(ct.ProviderBenefitEstimateFamily)
	if len(names) == 0 {
		t.Fatal("no cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			c := ct.LoadProviderBenefitCase(name)
			for _, size := range []int{1, 3, 1000} {
				store := &pagedProviderStore{rows: c.Rows}
				estimate, err := ae.EstimateProviderBenefitPaged(context.Background(), store,
					ae.BoundsOf(providerScope), providerScope, c.ProviderKey, c.Options, size)
				if err != nil {
					t.Fatal(err)
				}
				if !estimate.Tracked || estimate.Outcome != ae.BenefitFound {
					t.Fatalf("%+v", estimate)
				}
				if d := ct.EstimateDifference(c.Expected, ct.ProviderEstimateJSON(estimate)); d != "" {
					t.Fatalf("%d a page: %s", size, d)
				}
				if want := len(c.Rows)/size + 1; store.reads != want {
					t.Fatalf("%d a page: %d reads, expected %d", size, store.reads, want)
				}
			}
			estimate, err := ae.EstimateProviderBenefit(context.Background(), &pagedProviderStore{rows: c.Rows},
				ae.BoundsOf(providerScope), providerScope, c.ProviderKey, c.Options)
			if err != nil {
				t.Fatal(err)
			}
			if d := ct.EstimateDifference(c.Expected, ct.ProviderEstimateJSON(estimate)); d != "" {
				t.Fatal(d)
			}
		})
	}
}

type untrackedStore struct{ ae.Store }

func TestTheProviderEstimateChecksTheKeyThenTheOptionsAndAnswersUntrackedAndDenied(t *testing.T) {
	ctx := context.Background()
	store := &pagedProviderStore{}
	if _, err := ae.EstimateProviderBenefit(ctx, store, ae.BoundsOf(providerScope), providerScope, "a/b",
		ae.BenefitOptions{MinimumRunsPerArm: -1}); err == nil || !strings.Contains(err.Error(), "provider key") {
		t.Fatalf("a malformed key: %v", err)
	}
	if _, err := ae.EstimateProviderBenefit(ctx, store, ae.BoundsOf(providerScope), providerScope, "mem0",
		ae.BenefitOptions{MinimumRunsPerArm: -1}); err == nil {
		t.Fatal("a negative minimum was accepted")
	}
	if store.reads != 0 {
		t.Fatal("an invalid call read the store")
	}
	untracked, err := ae.EstimateProviderBenefit(ctx, untrackedStore{}, ae.BoundsOf(providerScope), providerScope,
		"mem0", ae.BenefitOptions{})
	if err != nil || untracked.Tracked || untracked.Outcome != ae.BenefitFound || untracked.ProviderKey != "mem0" ||
		untracked.MinimumRunsPerArm != ae.DefaultMinimumRunsPerArm || untracked.Overall.Verdict != ae.NoEvidence {
		t.Fatalf("untracked: %+v %v", untracked, err)
	}
	other := providerScope
	other.AgentID = ae.Ptr("another")
	denied, err := ae.EstimateProviderBenefit(ctx, store, ae.BoundsOf(providerScope), other, "mem0",
		ae.BenefitOptions{MinimumRunsPerArm: 5})
	if err != nil || !denied.Tracked || denied.Outcome != ae.BenefitDenied || denied.MinimumRunsPerArm != 5 ||
		store.reads != 0 {
		t.Fatalf("denied: %+v %v (%d reads)", denied, err, store.reads)
	}
}

type failingProviderStore struct{ ae.Store }

var errProviderRead = errors.New("the read failed")

func (failingProviderStore) QueryProviderHoldoutOutcomes(context.Context, ae.AuthorizationBounds, ae.Scope, string,
	int, *ae.UUID) ([]ae.ProviderHoldoutOutcomeRow, error) {
	return nil, errProviderRead
}

func TestAStoreFailureFailsTheProviderEstimate(t *testing.T) {
	_, err := ae.EstimateProviderBenefit(context.Background(), failingProviderStore{}, ae.BoundsOf(providerScope),
		providerScope, "mem0", ae.BenefitOptions{})
	if !errors.Is(err, errProviderRead) {
		t.Fatal(err)
	}
}

func TestAProviderAssignmentIsValidated(t *testing.T) {
	run := ae.MustParseUUID("000000d0-0000-4000-8000-000000000901")
	if err := (ae.ProviderHoldoutAssignment{RunID: run, ProviderKey: "mem0", Arm: ae.HoldoutWithheld}).Validate(); err != nil {
		t.Fatal(err)
	}
	for name, invalid := range map[string]ae.ProviderHoldoutAssignment{
		"nil run":     {ProviderKey: "mem0", Arm: ae.HoldoutTreatment},
		"bad key":     {RunID: run, ProviderKey: "a b", Arm: ae.HoldoutTreatment},
		"unknown arm": {RunID: run, ProviderKey: "mem0", Arm: "Control"},
	} {
		if invalid.Validate() == nil {
			t.Errorf("%s was accepted", name)
		}
	}
}
