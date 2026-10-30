package inmemory

// Contract 1.17's edges the fixtures cannot pin: cancellation once the ledger is written, the reuse-evidence step's
// truncation and options, concurrent identical submissions, and a token never printed.

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

var testScope = ae.Scope{TenantID: "tenant", ApplicationID: "app", ProjectID: "project", AgentID: ae.Ptr("agent")}

func testRecord(n byte) ae.ExperienceRecord {
	return ae.ExperienceRecord{
		ExperienceID: ae.UUID{0: 0x11, 6: 0x40, 8: 0x80, 15: n}, SourceRunID: ae.UUID{0: 0x22, 6: 0x40, 8: 0x80, 15: n},
		Scope: testScope, TaskID: "rotate-keys", Outcome: ae.Outcome{Status: ae.VerificationVerified},
		Status: ae.StatusValidated, ReuseConfidence: 2.0 / 3, SupportingValidations: 1, Revision: 1,
		CreatedAt: ae.MustParseInstant("2026-03-02T09:00:00+00:00"), UpdatedAt: ae.MustParseInstant("2026-03-02T09:00:00+00:00"),
	}
}

func testFeedback(records ...ae.ExperienceRecord) ae.ExperienceReuseFeedback {
	ids := make([]ae.UUID, len(records))
	for i, r := range records {
		ids[i] = r.ExperienceID
	}
	round := ae.UUID{0: 0x33, 6: 0x40, 8: 0x80}
	evidence := ae.Evidence{EvidenceID: ae.UUID{0: 0x44, 6: 0x40, 8: 0x80}, VerificationRoundID: round,
		Result: ae.CheckPass, CapturedAt: ae.MustParseInstant("2026-03-02T09:00:00+00:00")}
	run := ae.UUID{0: 0x55, 6: 0x40, 8: 0x80}
	return ae.ExperienceReuseFeedback{FeedbackID: ae.UUID{0: 0x66, 6: 0x40, 8: 0x80}, RunID: run, Scope: testScope,
		ExposedExperienceIDs: ids, RunOutcome: ae.VerificationVerified, Measure: &ae.ReuseMeasure{Kind: "k", Value: 1},
		ObservedAt: ae.MustParseInstant("2026-03-02T10:00:00+00:00"),
		ComparativeEvaluation: &ae.ComparativeEvaluationResult{EvaluatorID: "evaluator", RunID: run,
			VerificationRoundID: round, Benefit: ae.BenefitImproved, AttributedExperienceIDs: ids,
			Evidence: []*ae.Evidence{&evidence}, Summary: "fewer calls",
			EvaluatedAt: ae.MustParseInstant("2026-03-02T10:00:00+00:00")}}
}

var testAuthorization = ae.AuthorizationContext{PrincipalID: "host", Bounds: ae.BoundsOf(testScope)}

// cancellingLedger cancels the call's context once the ledger is written, as a caller's deadline would.
type cancellingLedger struct {
	inner  *ReuseFeedbackLedger
	cancel context.CancelFunc
}

func (l cancellingLedger) RecordReuseFeedback(ctx context.Context, authorization ae.AuthorizationContext,
	feedback ae.RecordedExperienceReuseFeedback) (ae.ReuseFeedbackStoreResult, error) {
	result, err := l.inner.RecordReuseFeedback(ctx, authorization, feedback)
	l.cancel()
	return result, err
}

// contextStore is the store, honouring the context on every lifecycle read.
type contextStore struct{ *Store }

func (s contextStore) ReadRecord(ctx context.Context, authorization ae.AuthorizationContext, scope ae.Scope,
	id ae.UUID) (ae.RecordReadResult, error) {
	if err := ctx.Err(); err != nil {
		return ae.RecordReadResult{}, err
	}
	return s.Store.ReadRecord(ctx, authorization, scope, id)
}

func TestACancellationOnceTheLedgerIsWrittenIsReportedPerRecord(t *testing.T) {
	a, b := testRecord(1), testRecord(2)
	store, err := New(a, b)
	if err != nil {
		t.Fatal(err)
	}
	lifecycle, err := ae.NewLifecycleService(contextStore{store}, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	ledger := NewReuseFeedbackLedger()
	service, err := ae.NewReuseFeedbackService(cancellingLedger{ledger, cancel}, lifecycle)
	if err != nil {
		t.Fatal(err)
	}
	result, err := service.Record(ctx, testAuthorization, testFeedback(a, b))
	if err != nil {
		t.Fatal(err)
	}
	if result.Outcome != ae.FeedbackRecorded || len(result.Exposures) != 2 || !result.IsRetryable() {
		t.Fatalf("%+v", result)
	}
	for _, exposure := range result.Exposures {
		if exposure.Disposition != ae.ExposureFailed || !exposure.Retryable || exposure.EvidenceID == nil ||
			*exposure.Reason != "Cancelled before this record's evidence was submitted; resubmit the same feedback ID." {
			t.Fatalf("%+v", exposure)
		}
	}
	if len(ledger.Submissions()) != 1 {
		t.Fatal("the ledger write is durable")
	}
	// Resubmitting converges.
	service, _ = ae.NewReuseFeedbackService(ledger, lifecycle)
	retried, err := service.Record(context.Background(), testAuthorization, testFeedback(a, b))
	if err != nil || retried.Outcome != ae.FeedbackAlreadyRecorded || retried.IsRetryable() ||
		retried.Exposures[0].Disposition != ae.ExposureEvidenceApplied || !retried.Exposures[0].Counted {
		t.Fatalf("%+v %v", retried, err)
	}
}

func TestALedgerFailureIsReturnedAndNothingMoves(t *testing.T) {
	a := testRecord(1)
	store, _ := New(a)
	lifecycle, _ := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	failing := &faultingLedger{inner: NewReuseFeedbackLedger()}
	message := "unreachable"
	failing.fault = &message
	service, _ := ae.NewReuseFeedbackService(failing, lifecycle)
	if _, err := service.Record(context.Background(), testAuthorization, testFeedback(a)); err == nil || err.Error() != message {
		t.Fatalf("%v", err)
	}
	if records := store.Records(); records[0].Revision != 1 {
		t.Fatal("a record moved")
	}
}

func TestConcurrentIdenticalSubmissionsRecordOnce(t *testing.T) {
	ledger := NewReuseFeedbackLedger()
	a := testRecord(1)
	store, _ := New(a)
	lifecycle, _ := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	service, _ := ae.NewReuseFeedbackService(ledger, lifecycle)
	const writers = 8
	outcomes := make([]ae.ExperienceReuseFeedbackOutcome, writers)
	var wg sync.WaitGroup
	for i := range writers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			result, err := service.Record(context.Background(), testAuthorization, testFeedback(a))
			if err != nil {
				t.Error(err)
			}
			outcomes[i] = result.Outcome
		}()
	}
	wg.Wait()
	recorded := 0
	for _, outcome := range outcomes {
		switch outcome {
		case ae.FeedbackRecorded:
			recorded++
		case ae.FeedbackAlreadyRecorded:
		default:
			t.Errorf("outcome %s", outcome)
		}
	}
	if recorded != 1 || len(ledger.Submissions()) != 1 {
		t.Fatalf("%d recorded", recorded)
	}
	if records := store.Records(); records[0].SupportingValidations != 2 {
		t.Fatalf("counted %d times", records[0].SupportingValidations-1)
	}
}

func TestTheAssessmentTokenIsNeverPrinted(t *testing.T) {
	token := "aexat1.secret-token"
	assessment := ae.HumanReuseAssessment{Benefit: ae.BenefitImproved, AssessmentToken: &token}
	for _, printed := range []string{assessment.String(), fmt.Sprintf("%v", assessment), fmt.Sprintf("%#v", assessment),
		fmt.Sprintf("%+v", ae.ExperienceReuseFeedback{HumanAssessment: &assessment})} {
		if strings.Contains(printed, "secret") {
			t.Fatalf("printed %s", printed)
		}
	}
}

func TestTheReuseEvidenceStepRefusesAnUndefinedModeAndDoesNothingWhenOff(t *testing.T) {
	store, _ := New()
	lifecycle, _ := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	run := testRecord(9)
	round := ae.UUID{0: 0x77, 6: 0x40, 8: 0x80}
	run.ClosedRoundID = &round
	run.Provenance.ExposedTo = []ae.RunExposure{{ExperienceID: testRecord(1).ExperienceID, Revision: 1}}
	if _, _, err := ae.ApplyReuseEvidence(context.Background(), lifecycle, testAuthorization, run,
		ae.ReuseEvidenceOptions{Mode: "Sometimes"}); err == nil {
		t.Fatal("an undefined mode was accepted")
	}
	results, truncated, err := ae.ApplyReuseEvidence(context.Background(), lifecycle, testAuthorization, run,
		ae.ReuseEvidenceOptions{ContradictOnFailure: true})
	if err != nil || truncated || len(results) != 0 {
		t.Fatalf("%v %v %v", results, truncated, err)
	}
}

func TestTheReuseEvidenceStepStopsWhenCancelled(t *testing.T) {
	a, b := testRecord(1), testRecord(2)
	store, _ := New(a, b)
	lifecycle, _ := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	run := testRecord(9)
	round := ae.UUID{0: 0x77, 6: 0x40, 8: 0x80}
	run.ClosedRoundID = &round
	run.Provenance.ExposedTo = []ae.RunExposure{{ExperienceID: a.ExperienceID, Revision: 1},
		{ExperienceID: b.ExperienceID, Revision: 1}}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	results, truncated, err := ae.ApplyReuseEvidence(ctx, lifecycle, testAuthorization, run,
		ae.ReuseEvidenceOptions{Mode: ae.ReuseEvidenceSameTask})
	if err != nil || !truncated || len(results) != 1 || results[0].Outcome != nil || results[0].ErrorType == nil ||
		!strings.HasPrefix(*results[0].Reason, "Cancelled before the reuse evidence") {
		t.Fatalf("%+v %v %v", results, truncated, err)
	}
}

// Exposures are stored and handed back in .NET's Guid order, which is the lowercase text's: unsigned, group by group.
func TestExposuresAreOrderedAsDotnetOrdersGuids(t *testing.T) {
	scrambled := []string{
		"7fffffff-0000-4000-8000-000000000001", "00001170-0001-4000-8000-ffffffffffff",
		"ffffffff-0000-4000-8000-000000000001", "00001170-ffff-4000-8000-000000000001",
		"80000000-0000-4000-8000-000000000001", "00001170-0001-4000-ffff-000000000001",
		"00001170-0001-ffff-8000-000000000001", "00001170-0001-4000-8000-000000000001",
	}
	want := []string{
		"00001170-0001-4000-8000-000000000001", "00001170-0001-4000-8000-ffffffffffff",
		"00001170-0001-4000-ffff-000000000001", "00001170-0001-ffff-8000-000000000001",
		"00001170-ffff-4000-8000-000000000001", "7fffffff-0000-4000-8000-000000000001",
		"80000000-0000-4000-8000-000000000001", "ffffffff-0000-4000-8000-000000000001",
	}
	exposures := make([]*ae.ExperienceReuseExposure, len(scrambled))
	for i, text := range scrambled {
		exposures[i] = &ae.ExperienceReuseExposure{ExperienceID: ae.MustParseUUID(text)}
	}
	scope := testScope
	feedback := ae.RecordedExperienceReuseFeedback{FeedbackID: ae.UUID{0: 0x77, 6: 0x40, 8: 0x80},
		RunID: ae.UUID{0: 0x55, 6: 0x40, 8: 0x80}, Scope: &scope, RunOutcome: ae.VerificationVerified,
		ClaimedBenefit: ae.BenefitUnknown, Benefit: ae.BenefitUnknown, AttributionSource: ae.AttributionNone,
		Measure: &ae.ReuseMeasure{Kind: "k", Value: 1}, ObservedAt: ae.MustParseInstant("2026-03-02T10:00:00+00:00"),
		Exposures: exposures}
	order := func(f ae.RecordedExperienceReuseFeedback) []string {
		ids := make([]string, len(f.Exposures))
		for i, exposure := range f.Exposures {
			ids[i] = exposure.ExperienceID.String()
		}
		return ids
	}
	if got := order(ae.NormalizeReuseFeedback(feedback)); fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("normalized %v", got)
	}
	ledger := NewReuseFeedbackLedger()
	recorded, err := ledger.RecordReuseFeedback(context.Background(), testAuthorization, feedback)
	if err != nil || recorded.Outcome != ae.LedgerRecorded || fmt.Sprint(order(*recorded.Feedback)) != fmt.Sprint(want) {
		t.Fatalf("recorded %v %v %v", recorded.Outcome, err, recorded.Feedback)
	}
	reversed := feedback
	reversed.Exposures = make([]*ae.ExperienceReuseExposure, len(exposures))
	for i, exposure := range exposures {
		reversed.Exposures[len(exposures)-1-i] = exposure
	}
	if again, err := ledger.RecordReuseFeedback(context.Background(), testAuthorization, reversed); err != nil ||
		again.Outcome != ae.LedgerAlreadyRecorded {
		t.Fatalf("again %v %v", again.Outcome, err)
	}
}

// An enum left "" reads as .NET's unset one, its first member: RunOutcome and ClaimedBenefit Unknown are recorded, and
// an attribution whose Benefit is "" degrades as .NET's default Unknown does, not as an undefined value.
func TestEnumsLeftUnsetReadAsTheirFirstMember(t *testing.T) {
	ledger := NewReuseFeedbackLedger()
	a := testRecord(9)
	store, _ := New(a)
	lifecycle, _ := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	service, _ := ae.NewReuseFeedbackService(ledger, lifecycle)
	feedback := testFeedback(a)
	feedback.RunOutcome, feedback.ClaimedBenefit = "", ""
	comparative := *feedback.ComparativeEvaluation
	comparative.Benefit = ""
	feedback.ComparativeEvaluation = &comparative
	result, err := service.Record(context.Background(), testAuthorization, feedback)
	if err != nil || result.Outcome != ae.FeedbackRecorded {
		t.Fatalf("%v %v %v", result.Outcome, err, result.Errors)
	}
	if result.Reason == nil || !strings.Contains(*result.Reason,
		"ComparativeEvaluation.Benefit must attribute improvement or harm; an attribution of Unknown is not an attribution") {
		t.Fatalf("reason %v", result.Reason)
	}
	stored := ledger.Submissions()[0]
	if stored.RunOutcome != ae.VerificationUnknown || stored.ClaimedBenefit != ae.BenefitUnknown {
		t.Fatalf("stored %s %s", stored.RunOutcome, stored.ClaimedBenefit)
	}
	if comparative.Benefit != "" {
		t.Fatal("the caller's attribution was changed")
	}
}
