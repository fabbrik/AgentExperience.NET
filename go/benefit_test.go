package agentexperience_test

// Contract 1.9: the benefit-estimate and model-metadata fixtures, the model key's exclusion from the finalize and
// inject fixtures, and the estimate's own rules.

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"testing"
	"time"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestBenefitEstimate(t *testing.T) {
	names := ct.Cases(ct.BenefitEstimateFamily)
	if len(names) == 0 {
		t.Fatal("no cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			c := ct.LoadBenefitCase(name)
			pure, err := ae.EstimateBenefitFromRows(c.Rows, c.Options)
			if err != nil {
				t.Fatal(err)
			}
			if d := ct.EstimateDifference(c.Expected, ct.EstimateJSON(pure)); d != "" {
				t.Fatalf("pure: %s", d)
			}
			// The paged form, whatever the page size, and the pure form over the rows reversed give the same.
			for _, size := range []int{1, 3, 1000} {
				accumulator, err := ae.NewBenefitAccumulator(c.Options)
				if err != nil {
					t.Fatal(err)
				}
				for start := 0; start < len(c.Rows); start += size {
					if err := accumulator.AddAll(c.Rows[start:min(start+size, len(c.Rows))]); err != nil {
						t.Fatal(err)
					}
				}
				if d := ct.EstimateDifference(c.Expected, ct.EstimateJSON(accumulator.Estimate())); d != "" {
					t.Fatalf("%d a page: %s", size, d)
				}
			}
			reversed := make([]ae.HoldoutOutcomeRow, len(c.Rows))
			for i, row := range c.Rows {
				reversed[len(c.Rows)-1-i] = row
			}
			again, err := ae.EstimateBenefitFromRows(reversed, c.Options)
			if err != nil {
				t.Fatal(err)
			}
			if d := ct.EstimateDifference(c.Expected, ct.EstimateJSON(again)); d != "" {
				t.Fatalf("reversed: %s", d)
			}
		})
	}
}

func TestModelMetadata(t *testing.T) {
	file := ct.MustReadJSON(fixture(ct.ModelMetadataFamily, "cases.json")).(*ae.Object)
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	if get(file, "key") != ae.ModelMetadataKey || get(file, "maxLength") != json.Number("256") ||
		ae.ModelMaxLength != 256 {
		t.Fatalf("key %v, max length %v", get(file, "key"), get(file, "maxLength"))
	}
	text := func(value any) string { s, _ := value.(string); return s }
	for i, item := range get(file, "normalize").([]any) {
		c := item.(*ae.Object)
		if got, want := ae.NormalizeModel(text(get(c, "modelId"))), text(get(c, "recorded")); got != want {
			t.Errorf("normalize[%d]: %q, want %q", i, got, want)
		}
	}
	for i, item := range get(file, "environment").([]any) {
		c := item.(*ae.Object)
		metadata := map[string]string{}
		for _, m := range get(c, "metadata").(*ae.Object).Members() {
			metadata[m.Key] = m.Value.(string)
		}
		environment := ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "r", OperatingSystem: "o", Metadata: metadata}
		got := ae.EnvironmentWithModel(environment, ae.NormalizeModel(text(get(c, "modelId"))))
		assertJSONEqual(t, get(c, "expected"), got.Metadata, "environment")
		if len(metadata) != get(c, "metadata").(*ae.Object).Len() {
			t.Errorf("environment[%d]: the host's metadata was changed in place", i)
		}
	}
}

// withModel adds the model key to a stored record's (or a run's) environment metadata, in place.
func withModel(t *testing.T, holder *ae.Object, path ...string) {
	t.Helper()
	o := holder
	for _, key := range path {
		value, _ := o.Get(key)
		o = value.(*ae.Object)
	}
	o.Set(ae.ModelMetadataKey, "contract-model")
}

func TestTheModelKeyChangesNoFinalizeFixtureButTheEnvironment(t *testing.T) {
	for _, name := range ct.Cases("finalize") {
		t.Run(name, func(t *testing.T) {
			input := ct.MustReadJSON(fixture("finalize", name, "input.json")).(*ae.Object)
			withModel(t, input, "run", "environment", "metadata")
			finalized, _, err := ct.RunFinalizeCase(input)
			if err != nil {
				t.Fatal(err)
			}
			expected := ct.MustReadJSON(fixture("finalize", name, "expected-record.json")).(*ae.Object)
			withModel(t, expected, "payload", "environment", "metadata")
			assertJSONEqual(t, expected, finalized.Record.ToStored(), "expected-record.json with the model")
			assertJSONEqual(t, ct.MustReadJSON(fixture("finalize", name, "expected-event.json")),
				finalized.Event.ToJSON(), "expected-event.json")
		})
	}
}

func TestTheModelKeyChangesNoInjectFixture(t *testing.T) {
	for _, name := range ct.Cases("inject") {
		t.Run(name, func(t *testing.T) {
			entries := ct.MustReadJSON(fixture("inject", name, "records.json"))
			for _, entry := range entries.([]any) {
				record, _ := entry.(*ae.Object).Get("record")
				withModel(t, record.(*ae.Object), "payload", "environment", "metadata")
			}
			ranked, err := ae.RankedFromJSON(entries)
			if err != nil {
				t.Fatal(err)
			}
			options := ct.MustReadJSON(fixture("inject", name, "options.json")).(*ae.Object)
			get := func(key string) any { value, _ := options.Get(key); return value }
			approach := map[string][]string{}
			for _, m := range get("approachArguments").(*ae.Object).Members() {
				for _, key := range m.Value.([]any) {
					approach[m.Key] = append(approach[m.Key], key.(string))
				}
			}
			number := func(key string) int {
				n, err := ae.ReadNumber(get(key).(json.Number))
				if err != nil {
					t.Fatal(err)
				}
				return int(n.(int64))
			}
			block, err := ae.RenderBlock(ranked, ae.BlockOptions{Rendering: ae.Rendering(get("rendering").(string)),
				FailureDetail: ae.FailureDetail(get("failureDetail").(string)), MaxRecords: number("maxRecords"),
				MaxBytes: number("maxBytes"), ApproachArguments: approach})
			if err != nil {
				t.Fatal(err)
			}
			expected, err := os.ReadFile(fixture("inject", name, "expected-block.txt"))
			if err != nil {
				t.Fatal(err)
			}
			if block.Text != string(expected) {
				t.Fatalf("the model key changed the block:\n%s", block.Text)
			}
		})
	}
}

func TestTheModelKeyIsIgnoredByEnvironmentCompatibility(t *testing.T) {
	if got := ae.EnvironmentCompatibility(map[string]string{"region": "eu"},
		map[string]string{ae.ModelMetadataKey: "gpt-4o"}); got != 1 {
		t.Fatalf("a preferred model alone: %v", got)
	}
	if got := ae.EnvironmentCompatibility(map[string]string{"region": "eu", ae.ModelMetadataKey: "a"},
		map[string]string{"region": "eu", ae.ModelMetadataKey: "b"}); got != 1 {
		t.Fatalf("a different model: %v", got)
	}
}

func TestTheEstimateRefusesANegativeMinimumAndARunPagedApart(t *testing.T) {
	if _, err := ae.NewBenefitAccumulator(ae.BenefitOptions{MinimumRunsPerArm: -1}); err == nil {
		t.Fatal("a negative minimum was accepted")
	}
	if _, err := ae.EstimateBenefit(context.Background(), outcomeStore{}, ae.AuthorizationBounds{TenantID: "t"},
		ae.Scope{TenantID: "t"}, ae.BenefitOptions{MinimumRunsPerArm: -1}); err == nil {
		t.Fatal("EstimateBenefit accepted a negative minimum")
	}
	estimate, err := ae.EstimateBenefitFromRows(nil, ae.BenefitOptions{})
	if err != nil || estimate.MinimumRunsPerArm != ae.DefaultMinimumRunsPerArm || estimate.Lessons == nil ||
		estimate.ByConsumingModel != nil {
		t.Fatalf("%+v %v", estimate, err)
	}
	run1, run2 := ae.MustParseUUID("00000001-0000-4000-8000-000000000001"), ae.MustParseUUID("00000001-0000-4000-8000-000000000002")
	lesson := ae.MustParseUUID("00000001-0000-4000-8000-000000000009")
	accumulator, _ := ae.NewBenefitAccumulator(ae.BenefitOptions{})
	for _, run := range []ae.UUID{run1, run2} {
		if err := accumulator.Add(ae.HoldoutOutcomeRow{RunID: run, Arm: ae.HoldoutTreatment, ExperienceID: lesson}); err != nil {
			t.Fatal(err)
		}
	}
	if err := accumulator.Add(ae.HoldoutOutcomeRow{RunID: run1, Arm: ae.HoldoutTreatment, ExperienceID: lesson}); !errors.Is(err, ae.ErrRunPagedApart) {
		t.Fatalf("a run paged apart: %v", err)
	}
	if got := accumulator.Estimate().Overall.Pending; got != 2 {
		t.Fatalf("pending %d", got)
	}
}

// outcomeStore is a Store that is a HoldoutOutcomeReader answering pages from rows, or a fixed error.
type outcomeStore struct {
	rows []ae.HoldoutOutcomeRow
	err  error
}

func (outcomeStore) InsertFinalized(context.Context, ae.AuthorizationBounds, *ae.FinalizedExperience, string, *time.Time) error {
	return nil
}

func (outcomeStore) Search(context.Context, ae.AuthorizationBounds, ae.SearchParameters) ([]ae.SearchRow, error) {
	return nil, nil
}

func (s outcomeStore) QueryHoldoutOutcomes(_ context.Context, _ ae.AuthorizationBounds, _ ae.Scope, limit int,
	after *ae.HoldoutOutcomeCursor) ([]ae.HoldoutOutcomeRow, error) {
	if s.err != nil {
		return nil, s.err
	}
	start := 0
	if after != nil {
		for i, row := range s.rows {
			if row.RunID == after.RunID && row.ExperienceID == after.ExperienceID {
				start = i + 1
			}
		}
	}
	return s.rows[start:min(start+limit, len(s.rows))], nil
}

// plainStore is a Store that does not read the holdout.
type plainStore struct{}

func (plainStore) InsertFinalized(context.Context, ae.AuthorizationBounds, *ae.FinalizedExperience, string, *time.Time) error {
	return nil
}

func (plainStore) Search(context.Context, ae.AuthorizationBounds, ae.SearchParameters) ([]ae.SearchRow, error) {
	return nil, nil
}

func TestEstimateBenefitOverAStore(t *testing.T) {
	ctx := context.Background()
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	bounds := ae.AuthorizationBounds{TenantID: "t"}

	untracked, err := ae.EstimateBenefit(ctx, plainStore{}, bounds, scope, ae.BenefitOptions{})
	if err != nil || untracked.Tracked || untracked.Outcome != ae.BenefitFound || untracked.Overall.Pending != 0 {
		t.Fatalf("untracked: %+v %v", untracked, err)
	}
	denied, err := ae.EstimateBenefit(ctx, outcomeStore{}, ae.AuthorizationBounds{TenantID: "other"}, scope, ae.BenefitOptions{})
	if err != nil || !denied.Tracked || denied.Outcome != ae.BenefitDenied {
		t.Fatalf("denied: %+v %v", denied, err)
	}
	refused, err := ae.EstimateBenefit(ctx, outcomeStore{err: ae.ErrAuthorizationDenied}, bounds, scope, ae.BenefitOptions{})
	if err != nil || refused.Outcome != ae.BenefitDenied {
		t.Fatalf("refused by the store: %+v %v", refused, err)
	}
	if _, err := ae.EstimateBenefit(ctx, outcomeStore{err: errors.New("down")}, bounds, scope, ae.BenefitOptions{}); err == nil {
		t.Fatal("a store failure was swallowed")
	}

	// 2,500 rows read 1,000 a page give the pure estimate.
	c := ct.LoadBenefitCase("split-by-model")
	var rows []ae.HoldoutOutcomeRow
	for len(rows) < 2500 {
		rows = append(rows, c.Rows...)
	}
	// Distinct runs per copy, kept in (run, record) order: the copy number goes in the run ID's first byte.
	for i := range rows {
		rows[i].RunID[0] = byte(i / len(c.Rows))
	}
	paged, err := ae.EstimateBenefit(ctx, outcomeStore{rows: rows}, bounds, scope, c.Options)
	if err != nil {
		t.Fatal(err)
	}
	pure, _ := ae.EstimateBenefitFromRows(rows, c.Options)
	if d := ct.EstimateDifference(ct.AsWritten(ct.EstimateJSON(pure)), ct.EstimateJSON(paged)); d != "" {
		t.Fatal(d)
	}
	if paged.Overall.Treatment.Runs+paged.Overall.Holdout.Runs < 1800 {
		t.Fatalf("%+v", paged.Overall)
	}
}
