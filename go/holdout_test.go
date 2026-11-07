package agentexperience_test

// Contract 1.8's holdout-assign fixtures: every run ID and salt gives .NET's fraction (to 15 significant digits) and
// arm at every rate.

import (
	"encoding/json"
	"strconv"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestHoldoutAssign(t *testing.T) {
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	number := func(value any) float64 {
		f, err := strconv.ParseFloat(string(value.(json.Number)), 64)
		if err != nil {
			t.Fatal(err)
		}
		return f
	}
	cases := get(ct.MustReadJSON(fixture("holdout-assign", "cases.json")).(*ae.Object), "cases").([]any)
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	for _, item := range cases {
		c := item.(*ae.Object)
		runID := ae.MustParseUUID(get(c, "runId").(string))
		salt := get(c, "salt").(string)
		fraction := ae.HoldoutFraction(runID, salt)
		expected := number(get(c, "fraction"))
		if strconv.FormatFloat(fraction, 'g', 15, 64) != strconv.FormatFloat(expected, 'g', 15, 64) {
			t.Errorf("%s with salt %q: fraction %v, expected %v", runID, salt, fraction, expected)
		}
		for _, entry := range get(c, "arms").([]any) {
			arm := entry.(*ae.Object)
			rate := number(get(arm, "rate"))
			if got := ae.AssignHoldout(runID, salt, rate); string(got) != get(arm, "arm").(string) {
				t.Errorf("%s with salt %q at rate %v: %s, expected %s", runID, salt, rate, got, get(arm, "arm"))
			}
		}
	}
}

func TestHoldoutRecordsAreValidatedAndEachRecordCountsOnceAtItsFirstRevision(t *testing.T) {
	a, b := ae.MustParseUUID("00000070-0000-4000-8000-000000000001"), ae.MustParseUUID("00000070-0000-4000-8000-000000000002")
	run := ae.MustParseUUID("00000070-0000-4000-8000-000000000901")
	records, err := ae.HoldoutRecords(ae.HoldoutAssignment{RunID: run, Arm: ae.HoldoutTreatment,
		Records: []ae.RunExposure{{ExperienceID: a, Revision: 2}, {ExperienceID: b, Revision: 1}, {ExperienceID: a, Revision: 3}}})
	if err != nil || len(records) != 2 || records[0] != (ae.RunExposure{ExperienceID: a, Revision: 2}) || records[1].ExperienceID != b {
		t.Fatalf("%v %v", records, err)
	}
	many := make([]ae.RunExposure, ae.MaxRecordInjectionIDs+1)
	for i := range many {
		many[i] = ae.RunExposure{ExperienceID: ae.NewRandomUUID()}
	}
	for name, invalid := range map[string]ae.HoldoutAssignment{
		"nil run":      {Arm: ae.HoldoutTreatment},
		"unknown arm":  {RunID: run, Arm: "Control"},
		"201 records":  {RunID: run, Arm: ae.HoldoutWithheld, Records: many},
		"nil record":   {RunID: run, Arm: ae.HoldoutWithheld, Records: []ae.RunExposure{{}}},
		"negative rev": {RunID: run, Arm: ae.HoldoutWithheld, Records: []ae.RunExposure{{ExperienceID: a, Revision: -1}}},
	} {
		if _, err := ae.HoldoutRecords(invalid); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
}

func TestRateZeroNeverHoldsARunOutAndRateOneAlwaysDoes(t *testing.T) {
	for range 500 {
		id := ae.NewRandomUUID()
		if ae.AssignHoldout(id, ae.DefaultHoldoutSalt, 0) != ae.HoldoutTreatment ||
			ae.AssignHoldout(id, ae.DefaultHoldoutSalt, 1) != ae.HoldoutWithheld {
			t.Fatalf("%s", id)
		}
		if f := ae.HoldoutFraction(id, ae.DefaultHoldoutSalt); f < 0 || f > 1 {
			t.Fatalf("%s: %v", id, f)
		}
	}
}
