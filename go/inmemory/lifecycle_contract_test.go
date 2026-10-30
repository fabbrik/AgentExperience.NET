package inmemory

// Contract 1.16: every lifecycle-commit and lifecycle-apply fixture through the core's LifecycleService over this
// store: each step's result, every seeded record afterwards in its stored form, and every seeded record's whole
// history as event rows, compared by value (numbers within 1e-12).

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"path/filepath"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

var errLifecycleHostEngine = errors.New("the host engine failed")

// lifecycleHostEngine is a host engine as a case declares it (the .NET generator's HostEngine).
type lifecycleHostEngine struct {
	ruleID, ruleVersion string
	score               any
}

func (e lifecycleHostEngine) RuleID() string      { return e.ruleID }
func (e lifecycleHostEngine) RuleVersion() string { return e.ruleVersion }
func (e lifecycleHostEngine) Score(input ae.ExperienceConfidenceInput) (float64, error) {
	if value, ok := e.score.(json.Number); ok {
		return value.Float64()
	}
	switch e.score {
	case "NaN":
		return math.NaN(), nil
	case "Infinity":
		return math.Inf(1), nil
	case "-Infinity":
		return math.Inf(-1), nil
	case "bayes":
		s, f := float64(input.SupportingValidations), float64(input.Contradictions)
		return (2 + s) / (4 + s + f), nil
	case "stored":
		return input.Record.ReuseConfidence, nil
	case "throws":
		return 0, errLifecycleHostEngine
	}
	panic(fmt.Sprintf("unknown host engine score %v", e.score))
}

func lifecycleEngineOf(value any) ae.ConfidenceEngine {
	switch engine := value.(type) {
	case nil:
		return nil
	case string:
		if engine == "default" {
			return ae.ReuseConfidenceHeuristicEngine{}
		}
	case *ae.Object:
		text := func(key string) string { v, _ := engine.Get(key); s, _ := v.(string); return s }
		score, _ := engine.Get("score")
		return lifecycleHostEngine{text("ruleId"), text("ruleVersion"), score}
	}
	panic(fmt.Sprintf("unknown engine %v", value))
}

func isLifecycleHostEngineError(err error) bool { return errors.Is(err, errLifecycleHostEngine) }

func member(o *ae.Object, key string) any {
	value, ok := o.Get(key)
	if !ok {
		panic("no member " + key)
	}
	return value
}

// runLifecycleStep runs one step and writes its result as the fixture does.
func runLifecycleStep(t *testing.T, ctx context.Context, store *Store, service *ae.LifecycleService,
	step *ae.Object) any {
	t.Helper()
	for _, m := range step.Members() {
		if m.Key == "clock" {
			continue
		}
		o, _ := m.Value.(*ae.Object)
		switch m.Key {
		case "commit":
			result, err := service.Commit(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.CommitRequestOf(member(o, "request")))
			if err != nil {
				t.Fatalf("commit: %v", err)
			}
			return ct.CommitResultJSON(result)
		case "apply":
			result, err := service.ApplyEvidence(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.ApplyRequestOf(member(o, "request")))
			return ct.ApplyResultJSON(result, err, isLifecycleHostEngineError)
		case "checkSupersession":
			result, err := store.CheckSupersession(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.ScopeOf(member(o, "scope")), ae.MustParseUUID(member(o, "experienceId").(string)),
				ae.MustParseUUID(member(o, "replacementExperienceId").(string)))
			if err != nil {
				t.Fatalf("checkSupersession: %v", err)
			}
			return ct.SupersessionResultJSON(result)
		case "history":
			query := ct.HistoryQueryOf(member(o, "query"))
			result, err := store.GetHistory(ctx, ct.AuthorizationOf(member(o, "authorization")), query)
			if err != nil {
				t.Fatalf("history: %v", err)
			}
			return ct.HistoryResultJSON(query.Scope, result)
		case "readConfidence":
			id, err := ae.ParseUUID(member(o, "experienceId").(string))
			if err != nil {
				t.Fatal(err)
			}
			result, err := service.ReadConfidence(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.ScopeOf(member(o, "scope")), id, ae.ConfidenceEvidenceFilter(member(o, "filter").(string)))
			if err != nil {
				t.Fatalf("readConfidence: %v", err)
			}
			return ct.ConfidenceReadJSON(result)
		case "derive":
			result, err := ct.DeriveJSON(o)
			if err != nil {
				t.Fatalf("derive: %v", err)
			}
			return result
		}
		t.Fatalf("unknown step %s", m.Key)
	}
	t.Fatal("a step with no operation")
	return nil
}

func runLifecycleCase(t *testing.T, family, name string) {
	ctx := context.Background()
	input := ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, "input.json")).(*ae.Object)
	expected := ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, "expected.json")).(*ae.Object)

	var seeded []ae.ExperienceRecord
	for _, stored := range member(input, "records").([]any) {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		seeded = append(seeded, record)
	}
	store, err := New(seeded...)
	if err != nil {
		t.Fatal(err)
	}
	service := member(input, "service").(*ae.Object)
	// The case's mode, always explicitly: the Go default (RecordedOnly) is not .NET's (Counted).
	effect := ae.HostTrustedEvidenceEffect(member(service, "hostTrustedEvidence").(string))
	if effect != ae.HostTrustedCounted && effect != ae.HostTrustedRecordedOnly {
		t.Fatalf("the case names no host-trusted evidence effect: %q", effect)
	}
	options := ae.LifecycleOptions{ConfidenceEngine: lifecycleEngineOf(member(service, "engine")),
		HostTrustedEvidence: effect}
	lifecycle, err := ae.NewLifecycleService(store, options)
	if err != nil {
		t.Fatal(err)
	}

	steps := member(input, "steps").([]any)
	results := member(expected, "results").([]any)
	if len(steps) != len(results) {
		t.Fatalf("%d steps, %d results", len(steps), len(results))
	}
	for i, item := range steps {
		step := item.(*ae.Object)
		clock := ae.MustParseInstant(member(step, "clock").(string))
		store.SetClock(func() ae.Instant { return clock })
		actual := ct.AsWritten(runLifecycleStep(t, ctx, store, lifecycle, step))
		if difference := ct.Difference(results[i], actual, fmt.Sprintf("results[%d]", i), ct.LifecycleOptions); difference != "" {
			t.Fatalf("%s", difference)
		}
	}

	records := store.Records()
	expectedRecords := member(expected, "records").([]any)
	if len(records) != len(expectedRecords) {
		t.Fatalf("%d records, %d expected", len(records), len(expectedRecords))
	}
	for i := range records {
		if difference := ct.Difference(expectedRecords[i], ct.AsWritten(records[i].ToStored()),
			fmt.Sprintf("records[%d]", i), ct.LifecycleOptions); difference != "" {
			t.Fatalf("%s", difference)
		}
	}
	histories := member(expected, "histories").([]any)
	if len(histories) != len(seeded) {
		t.Fatalf("%d histories, %d records", len(histories), len(seeded))
	}
	for i, record := range seeded {
		events := []any{}
		for _, stored := range store.History(record.ExperienceID) {
			events = append(events, ct.EventRowJSON(record.Scope, stored))
		}
		actual := ae.NewObject(ae.Member{Key: "experienceId", Value: record.ExperienceID.String()},
			ae.Member{Key: "events", Value: events})
		if difference := ct.Difference(histories[i], ct.AsWritten(actual), fmt.Sprintf("histories[%d]", i),
			ct.LifecycleOptions); difference != "" {
			t.Fatalf("%s", difference)
		}
	}
}

func TestLifecycleCommitFixtures(t *testing.T) {
	names := ct.Cases("lifecycle-commit")
	if len(names) == 0 {
		t.Fatal("no lifecycle-commit cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) { runLifecycleCase(t, "lifecycle-commit", name) })
	}
}

func TestLifecycleApplyFixtures(t *testing.T) {
	names := ct.Cases("lifecycle-apply")
	if len(names) == 0 {
		t.Fatal("no lifecycle-apply cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) { runLifecycleCase(t, "lifecycle-apply", name) })
	}
}
