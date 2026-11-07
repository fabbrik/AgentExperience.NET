//go:build postgres

package pgtest_test

// Contract 1.17's reuse-feedback-sql fixtures through the PostgreSQL ledger (sql/reuse-feedback.sql): each step's
// result, then the two ledger tables read as the schema's owner, each column as text in UTC. And the cross-language
// writer (internal/reusefeedback) speaking its protocol.

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
)

var reuseFeedbackTables = []struct {
	name, table string
	columns     []string
	order       string
}{
	{"reuse_feedback", "agent_experience.reuse_feedback", []string{"feedback_id", "run_id", "tenant_id",
		"application_id", "project_id", "team_id", "agent_id", "user_id", "run_outcome", "claimed_benefit", "benefit",
		"attribution_source", "reviewer_identity", "evaluator_id", "verification_round_id", "assessment_id", "rationale",
		"evidence_ids", "attributed_at", "measure_kind", "measure_value", "trial_label", "observed_at", "recorded_at"},
		"feedback_id"},
	{"reuse_feedback_exposures", "agent_experience.reuse_feedback_exposures", []string{"feedback_id",
		"experience_id", "ordinal", "attributed", "evidence_id", "rationale_sealed"}, "feedback_id, ordinal"},
}

func TestReuseFeedbackSQL(t *testing.T) {
	admin := requireServer(t)
	names := ct.Cases("reuse-feedback-sql")
	if len(names) == 0 {
		t.Fatal("no reuse-feedback-sql cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			seed := ct.MustReadJSON(fixture("reuse-feedback-sql", name, "seed.json")).(*ae.Object)
			steps := get(ct.MustReadJSON(fixture("reuse-feedback-sql", name, "steps.json")).(*ae.Object), "steps").([]any)
			expected := ct.MustReadJSON(fixture("reuse-feedback-sql", name, "expected.json")).(*ae.Object)
			results := get(expected, "results").([]any)
			if len(results) != len(steps) {
				t.Fatalf("%d steps, %d results", len(steps), len(results))
			}
			database := createDatabase(t, admin, "feedback_"+name)
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
			for i, item := range steps {
				step := item.(*ae.Object)
				at := ae.MustParseInstant(get(step, "clock").(string))
				store.SetClock(func() ae.Instant { return at })
				var actual any
				if o, ok := get(step, "record").(*ae.Object); ok {
					result, err := store.RecordReuseFeedback(ctx, ct.AuthorizationOf(get(o, "authorization")),
						ct.RecordedFeedbackOf(get(o, "feedback")))
					if err != nil {
						t.Fatalf("record: %v", err)
					}
					actual = ct.AsWritten(ct.LedgerResultJSON(result))
				} else if o, ok := get(step, "erase").(*ae.Object); ok {
					eraseStep(t, owner, o)
				} else {
					t.Fatalf("unknown step %v", step.Keys())
				}
				if difference := ct.Difference(results[i], actual, fmt.Sprintf("results[%d]", i),
					ct.LifecycleOptions); difference != "" {
					t.Fatalf("%s", difference)
				}
			}
			for _, table := range reuseFeedbackTables {
				actual := tableAsText(t, owner, table.table, table.columns, table.order)
				if difference := ct.Difference(get(expected, table.name), ct.AsWritten(actual), table.name,
					ct.Options{}); difference != "" {
					t.Fatalf("%s", difference)
				}
			}
		})
	}
}

// The cross-language Go writer (internal/reusefeedback), run as the .NET suite runs it: a human-attributed submission,
// the same again (AlreadyRecorded), then a read; stdout is one JSON value and nothing else.
func TestTheReuseFeedbackWriterSpeaksItsProtocol(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "feedback_writer")
	store := newStore(t, admin, database)
	seed := ct.MustReadJSON(fixture("lifecycle-sql", "compare-and-set", "seed.json")).(*ae.Object)
	records, _ := seed.Get("records")
	record, err := ae.FromStored(records.([]any)[0])
	if err != nil {
		t.Fatal(err)
	}
	if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
		t.Fatal(err)
	}
	authorization := ct.AuthorizationJSON(ae.AuthorizationContext{PrincipalID: "go-writer", Bounds: ae.BoundsOf(record.Scope)})
	feedback := ct.FeedbackJSON(ae.ExperienceReuseFeedback{
		FeedbackID: ae.MustParseUUID("00001173-0000-4000-8000-00000000f001"), RunID: ae.MustParseUUID("00001173-0000-4000-8000-000000005100"),
		Scope: record.Scope, ExposedExperienceIDs: []ae.UUID{record.ExperienceID}, RunOutcome: ae.VerificationVerified,
		Measure: &ae.ReuseMeasure{Kind: "tool-calls", Value: 7}, ObservedAt: ae.MustParseInstant("2026-03-02T09:30:00+00:00"),
		HumanAssessment: &ae.HumanReuseAssessment{AssessmentID: ae.MustParseUUID("00001173-0000-4000-8000-00000000a001"),
			Benefit: ae.BenefitImproved, AttributedExperienceIDs: []ae.UUID{record.ExperienceID},
			Rationale: "it helped", AssessedAt: ae.MustParseInstant("2026-03-02T09:35:00+00:00")},
	})
	submit := ae.NewObject(ae.Member{Key: "submit", Value: ae.NewObject(
		ae.Member{Key: "clock", Value: "2026-03-02T12:00:00+00:00"},
		ae.Member{Key: "authorization", Value: authorization}, ae.Member{Key: "feedback", Value: feedback})})
	read := ae.NewObject(ae.Member{Key: "read", Value: ae.NewObject(
		ae.Member{Key: "authorization", Value: authorization}, ae.Member{Key: "scope", Value: ct.ScopeJSON(record.Scope)},
		ae.Member{Key: "experienceId", Value: record.ExperienceID.String()})})
	input := ae.NewObject(ae.Member{Key: "dsn", Value: admin.Application(database)},
		ae.Member{Key: "hostTrustedEvidence", Value: "Counted"}, ae.Member{Key: "steps", Value: []any{submit, submit, read}})
	data, err := ae.MarshalJSONValue(input)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "input.json")
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatal(err)
	}
	command := exec.Command("go", "run", "./internal/reusefeedback", path)
	command.Dir = ".."
	var stderr strings.Builder
	command.Stderr = &stderr
	output, err := command.Output()
	if err != nil {
		t.Fatalf("%v: %s", err, stderr.String())
	}
	value, err := ae.DecodeJSON(output)
	if err != nil {
		t.Fatalf("stdout is not one JSON value: %v (%q)", err, output)
	}
	results, _ := value.(*ae.Object).Get("results")
	list := results.([]any)
	if len(list) != 3 {
		t.Fatalf("%v", value)
	}
	for i, want := range []string{"Recorded", "AlreadyRecorded"} {
		outcome, _ := list[i].(*ae.Object).Get("outcome")
		exposures, _ := list[i].(*ae.Object).Get("exposures")
		disposition, _ := exposures.([]any)[0].(*ae.Object).Get("disposition")
		if outcome != want || disposition != "EvidenceApplied" {
			t.Fatalf("submission %d: %v", i, list[i])
		}
	}
	readResult := list[2].(*ae.Object)
	history, _ := readResult.Get("history")
	confidence, _ := readResult.Get("confidence")
	events, _ := history.(*ae.Object).Get("events")
	outcome, _ := confidence.(*ae.Object).Get("outcome")
	if len(events.([]any)) != 1 || outcome != "Found" {
		t.Fatalf("read %v", readResult)
	}
	reviewer, _ := events.([]any)[0].(*ae.Object).Get("confidence_reviewer_identity")
	if reviewer != "go-writer" {
		t.Fatalf("reviewer %v", reviewer)
	}
}

// Writers racing on one feedback ID, each on its own pool: the primary key is the arbiter, so exactly one records it,
// every other one finds its own submission stored (AlreadyRecorded), and the ledger holds one row.
func TestWritersRacingOnOneFeedbackIDRecordItExactlyOnce(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	steps := get(ct.MustReadJSON(fixture("reuse-feedback-sql", "record-replay-and-conflict", "steps.json")).(*ae.Object),
		"steps").([]any)
	first := get(steps[0].(*ae.Object), "record").(*ae.Object)
	authorization := ct.AuthorizationOf(get(first, "authorization"))
	database := createDatabase(t, admin, "feedback_race")
	owner := ownerConn(t, admin, database)

	const writers = 8
	stores := make([]*postgres.Store, writers)
	for i := range stores {
		stores[i] = newStore(t, admin, database)
	}
	start := make(chan struct{})
	outcomes := make([]ae.ReuseFeedbackStoreOutcome, writers)
	failures := make([]error, writers)
	var wg sync.WaitGroup
	for i := range writers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			result, err := stores[i].RecordReuseFeedback(ctx, authorization, ct.RecordedFeedbackOf(get(first, "feedback")))
			outcomes[i], failures[i] = result.Outcome, err
		}()
	}
	close(start)
	wg.Wait()
	recorded := 0
	for i, outcome := range outcomes {
		if failures[i] != nil {
			t.Fatalf("writer %d: %v", i, failures[i])
		}
		switch outcome {
		case ae.LedgerRecorded:
			recorded++
		case ae.LedgerAlreadyRecorded:
		default:
			t.Fatalf("writer %d: %s", i, outcome)
		}
	}
	if recorded != 1 {
		t.Fatalf("%d writers recorded it: %v", recorded, outcomes)
	}
	if rows := tableAsText(t, owner, "agent_experience.reuse_feedback", []string{"feedback_id"}, "feedback_id"); len(rows) != 1 {
		t.Fatalf("%d submission rows", len(rows))
	}
	exposures := tableAsText(t, owner, "agent_experience.reuse_feedback_exposures", []string{"experience_id"}, "ordinal")
	if len(exposures) != len(ct.RecordedFeedbackOf(get(first, "feedback")).Exposures) {
		t.Fatalf("%d exposure rows", len(exposures))
	}
}
