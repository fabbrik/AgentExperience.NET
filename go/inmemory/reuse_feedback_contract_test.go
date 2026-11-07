package inmemory

// Contract 1.17: every reuse-feedback fixture through the core's ReuseFeedbackService over this package's ledger and
// record store (with the README's fault decorators), and every finalize-reuse-evidence fixture through Finalize, this
// store and ApplyReuseEvidence: each step's result, the ledger, every seeded record and its whole history.

import (
	"context"
	"fmt"
	"path/filepath"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// storeFault is a store failure a fixture injects, with its message.
type storeFault string

func (f storeFault) Error() string { return string(f) }

// faultingLedger is the ledger, made to fail for one step.
type faultingLedger struct {
	inner *ReuseFeedbackLedger
	fault *string
}

func (l *faultingLedger) RecordReuseFeedback(ctx context.Context, authorization ae.AuthorizationContext,
	feedback ae.RecordedExperienceReuseFeedback) (ae.ReuseFeedbackStoreResult, error) {
	if l.fault != nil {
		return ae.ReuseFeedbackStoreResult{}, storeFault(*l.fault)
	}
	return l.inner.RecordReuseFeedback(ctx, authorization, feedback)
}

// faultingStore is the record store, made to fail one record's lifecycle commit for one step.
type faultingStore struct {
	*Store
	faults map[ae.UUID]*ae.Object
}

func (s *faultingStore) CommitLifecycleEvent(ctx context.Context, authorization ae.AuthorizationContext, scope ae.Scope,
	event ae.LifecycleEvent) (ae.LifecycleCommitResult, error) {
	if fault, ok := s.faults[event.ExperienceRecordID]; ok {
		if message, ok := fault.Get("throws"); ok {
			return ae.LifecycleCommitResult{}, storeFault(message.(string))
		}
		answer, _ := fault.Get("answers")
		return ae.LifecycleCommitResult{Outcome: ae.ExperienceStoreOutcome(answer.(string)),
			Revision: event.ExpectedRevision + 1, Errors: []ae.StoreValidationError{}}, nil
	}
	return s.Store.CommitLifecycleEvent(ctx, authorization, scope, event)
}

func lifecycleServiceOf(t *testing.T, store ae.LifecycleStore, service *ae.Object) *ae.LifecycleService {
	t.Helper()
	effect := ae.HostTrustedEvidenceEffect(member(service, "hostTrustedEvidence").(string))
	if effect != ae.HostTrustedCounted && effect != ae.HostTrustedRecordedOnly {
		t.Fatalf("the case names no host-trusted evidence effect: %q", effect)
	}
	lifecycle, err := ae.NewLifecycleService(store, ae.LifecycleOptions{
		ConfidenceEngine: lifecycleEngineOf(member(service, "engine")), HostTrustedEvidence: effect})
	if err != nil {
		t.Fatal(err)
	}
	return lifecycle
}

func seedRecords(t *testing.T, input *ae.Object) []ae.ExperienceRecord {
	t.Helper()
	var seeded []ae.ExperienceRecord
	for _, stored := range member(input, "records").([]any) {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		seeded = append(seeded, record)
	}
	return seeded
}

// compareRecordsAndHistories compares every seeded record and its whole history with expected's.
func compareRecordsAndHistories(t *testing.T, store *Store, seeded []ae.ExperienceRecord, expected *ae.Object) {
	t.Helper()
	expectedRecords := member(expected, "records").([]any)
	histories := member(expected, "histories").([]any)
	if len(expectedRecords) != len(seeded) || len(histories) != len(seeded) {
		t.Fatalf("%d records, %d histories, %d seeded", len(expectedRecords), len(histories), len(seeded))
	}
	ctx := context.Background()
	for i, record := range seeded {
		read, err := store.ReadRecord(ctx, ae.AuthorizationContext{PrincipalID: "contract-principal",
			Bounds: ae.BoundsOf(record.Scope)}, record.Scope, record.ExperienceID)
		if err != nil || read.Record == nil {
			t.Fatalf("reading %s: %+v %v", record.ExperienceID, read, err)
		}
		if difference := ct.Difference(expectedRecords[i], ct.AsWritten(read.Record.ToStored()),
			fmt.Sprintf("records[%d]", i), ct.LifecycleOptions); difference != "" {
			t.Fatalf("%s", difference)
		}
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

func runReuseFeedbackStep(t *testing.T, ctx context.Context, service *ae.ReuseFeedbackService,
	lifecycle *ae.LifecycleService, ledger *faultingLedger, store *faultingStore, step *ae.Object) any {
	t.Helper()
	for _, m := range step.Members() {
		if m.Key == "clock" {
			continue
		}
		o := m.Value.(*ae.Object)
		switch m.Key {
		case "submit":
			ledger.fault, store.faults = nil, nil
			if faults, ok := o.Get("faults"); ok {
				f := faults.(*ae.Object)
				if message, _ := f.Get("ledger"); message != nil {
					text := message.(string)
					ledger.fault = &text
				}
				if commit, _ := f.Get("commit"); commit != nil {
					store.faults = map[ae.UUID]*ae.Object{}
					for _, entry := range commit.(*ae.Object).Members() {
						store.faults[ae.MustParseUUID(entry.Key)] = entry.Value.(*ae.Object)
					}
				}
			}
			defer func() { ledger.fault, store.faults = nil, nil }()
			result, err := service.Record(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.FeedbackOf(member(o, "feedback")))
			if err != nil {
				return ct.LedgerThrownJSON(err)
			}
			return ct.SubmitResultJSON(result)
		case "record":
			result, err := ledger.inner.RecordReuseFeedback(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.RecordedFeedbackOf(member(o, "feedback")))
			if err != nil {
				t.Fatalf("record: %v", err)
			}
			return ct.LedgerResultJSON(result)
		case "apply":
			result, err := lifecycle.ApplyEvidence(ctx, ct.AuthorizationOf(member(o, "authorization")),
				ct.ApplyRequestOf(member(o, "request")))
			return ct.ApplyResultJSON(result, err, isLifecycleHostEngineError)
		case "derive":
			return ct.FeedbackDeriveJSON(o)
		}
		t.Fatalf("unknown step %s", m.Key)
	}
	t.Fatal("a step with no operation")
	return nil
}

func runReuseFeedbackCase(t *testing.T, name string) {
	ctx := context.Background()
	input := ct.MustReadJSON(filepath.Join(ct.Fixtures, "reuse-feedback", name, "input.json")).(*ae.Object)
	expected := ct.MustReadJSON(filepath.Join(ct.Fixtures, "reuse-feedback", name, "expected.json")).(*ae.Object)
	seeded := seedRecords(t, input)
	inner, err := New(seeded...)
	if err != nil {
		t.Fatal(err)
	}
	store := &faultingStore{Store: inner}
	ledger := &faultingLedger{inner: NewReuseFeedbackLedger()}
	lifecycle := lifecycleServiceOf(t, store, member(input, "service").(*ae.Object))
	service, err := ae.NewReuseFeedbackService(ledger, lifecycle)
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
		inner.SetClock(func() ae.Instant { return clock })
		actual := ct.AsWritten(runReuseFeedbackStep(t, ctx, service, lifecycle, ledger, store, step))
		if difference := ct.Difference(results[i], actual, fmt.Sprintf("results[%d]", i), ct.LifecycleOptions); difference != "" {
			t.Fatalf("%s", difference)
		}
	}
	stored := []any{}
	for _, submission := range ledger.inner.Submissions() {
		stored = append(stored, ct.RecordedFeedbackJSON(&submission))
	}
	if difference := ct.Difference(member(expected, "ledger"), ct.AsWritten(stored), "ledger", ct.LifecycleOptions); difference != "" {
		t.Fatalf("%s", difference)
	}
	compareRecordsAndHistories(t, inner, seeded, expected)
}

func TestReuseFeedbackFixtures(t *testing.T) {
	names := ct.Cases("reuse-feedback")
	if len(names) == 0 {
		t.Fatal("no reuse-feedback cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) { runReuseFeedbackCase(t, name) })
	}
}

// dotnetErrorType is the .NET exception a Go error type the reuse-evidence step reports stands for.
func dotnetErrorType(goType string) string {
	switch goType {
	case "*agentexperience.ConfidenceEngineError":
		return "System.InvalidOperationException"
	}
	return goType
}

func runFinalizeReuseEvidenceCase(t *testing.T, name string) {
	ctx := context.Background()
	input := ct.MustReadJSON(filepath.Join(ct.Fixtures, "finalize-reuse-evidence", name, "input.json")).(*ae.Object)
	expected := ct.MustReadJSON(filepath.Join(ct.Fixtures, "finalize-reuse-evidence", name, "expected.json")).(*ae.Object)
	seeded := seedRecords(t, input)
	store, err := New(seeded...)
	if err != nil {
		t.Fatal(err)
	}
	finalize := member(input, "finalize").(*ae.Object)
	finalizedAt := ae.MustParseInstant(member(member(finalize, "verification").(*ae.Object), "finalizedAt").(string))
	store.SetClock(func() ae.Instant { return finalizedAt })
	lifecycle := lifecycleServiceOf(t, store, member(input, "service").(*ae.Object))
	options := member(input, "finalization").(*ae.Object)
	reuse := ae.ReuseEvidenceOptions{Mode: ae.ReuseEvidenceMode(member(options, "reuseEvidence").(string)),
		ContradictOnFailure: member(options, "contradictOnFailure").(bool)}

	finalized, _, err := ct.RunFinalizeCase(finalize)
	if err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "contract-principal",
		Bounds: ae.BoundsOf(finalized.Record.Scope)}
	count, _ := member(input, "finalizations").(interface{ Int64() (int64, error) }).Int64()
	results := []any{}
	for i := int64(0); i < count; i++ {
		var record ae.ExperienceRecord
		var outcome string
		if err := store.InsertFinalized(ctx, authorization.Bounds, &finalized, authorization.PrincipalID, nil); err == nil {
			record, outcome = finalized.Record, "Validated"
			if record.Status == ae.StatusQuarantined {
				outcome = "Quarantined"
			}
		} else {
			// The run is finalized already: its stored record, as .NET's AlreadyFinalized replays it.
			read, err := store.ReadRecord(ctx, authorization, finalized.Record.Scope, finalized.Record.ExperienceID)
			if err != nil || read.Record == nil {
				t.Fatalf("reading the finalized record: %+v %v", read, err)
			}
			record, outcome = *read.Record, "AlreadyFinalized"
		}
		evidence, truncated, err := ae.ApplyReuseEvidence(ctx, lifecycle, authorization, record, reuse)
		if err != nil {
			t.Fatal(err)
		}
		results = append(results, ae.NewObject(
			ae.Member{Key: "outcome", Value: outcome},
			ae.Member{Key: "experienceId", Value: record.ExperienceID.String()},
			ae.Member{Key: "reuseEvidence", Value: ct.ReuseEvidenceJSON(evidence, dotnetErrorType)},
			ae.Member{Key: "reuseEvidenceTruncated", Value: truncated},
		))
	}
	if difference := ct.Difference(member(expected, "results"), ct.AsWritten(results), "results", ct.LifecycleOptions); difference != "" {
		t.Fatalf("%s", difference)
	}
	compareRecordsAndHistories(t, store, seeded, expected)
}

func TestFinalizeReuseEvidenceFixtures(t *testing.T) {
	names := ct.Cases("finalize-reuse-evidence")
	if len(names) == 0 {
		t.Fatal("no finalize-reuse-evidence cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) { runFinalizeReuseEvidenceCase(t, name) })
	}
}
