package agentexperience

// The lifecycle service's rules the fixtures do not state: its construction, the telemetry it reports, a store that
// fails or breaks its contract, and the transition table as a whole.

import (
	"context"
	"errors"
	"strings"
	"testing"
)

// scriptedStore answers every call with what a test gave it.
type scriptedStore struct {
	read    RecordReadResult
	commit  LifecycleCommitResult
	history []HistoryResult
	err     error
	pages   int
}

func (s *scriptedStore) ReadRecord(context.Context, AuthorizationContext, Scope, UUID) (RecordReadResult, error) {
	return s.read, s.err
}

func (s *scriptedStore) CommitLifecycleEvent(context.Context, AuthorizationContext, Scope,
	LifecycleEvent) (LifecycleCommitResult, error) {
	return s.commit, s.err
}

func (s *scriptedStore) GetHistory(context.Context, AuthorizationContext, HistoryQuery) (HistoryResult, error) {
	page := s.history[min(s.pages, len(s.history)-1)]
	s.pages++
	return page, s.err
}

func (s *scriptedStore) CheckSupersession(context.Context, AuthorizationContext, Scope, UUID,
	UUID) (SupersessionCheckResult, error) {
	return SupersessionCheckResult{}, s.err
}

var (
	lifecycleScope = Scope{TenantID: "acme", ApplicationID: "support", ProjectID: "refunds"}
	lifecycleAuth  = AuthorizationContext{PrincipalID: "reviewer", Bounds: BoundsOf(lifecycleScope)}
)

func validatedRecord() *ExperienceRecord {
	return &ExperienceRecord{ExperienceID: UUID{15: 1}, SourceRunID: UUID{15: 2}, Scope: lifecycleScope,
		Status: StatusValidated, Revision: 1, SupportingValidations: 1, ReuseConfidence: 2.0 / 3}
}

func commitRequest() CommitLifecycleTransitionRequest {
	prior := StatusValidated
	return CommitLifecycleTransitionRequest{EventID: UUID{15: 3}, ExperienceID: UUID{15: 1}, Scope: lifecycleScope,
		PriorStatus: &prior, CurrentStatus: StatusRevoked, Reason: "withdrawn", Producer: "review",
		OccurredAt: MustParseInstant("2026-03-02T09:00:00+00:00"), ExpectedRevision: 1}
}

func evidenceRequest() ApplyConfidenceEvidenceRequest {
	round := UUID{15: 5}
	return ApplyConfidenceEvidenceRequest{EventID: UUID{15: 3}, ExperienceID: UUID{15: 1}, Scope: lifecycleScope,
		EvidenceID: UUID{15: 4}, Kind: EvidenceSupporting, Source: EvidenceMachine, RunID: UUID{15: 6},
		VerificationRoundID: &round, Reason: "reused", Producer: "aggregator",
		OccurredAt: MustParseInstant("2026-03-02T09:00:00+00:00")}
}

func TestNewLifecycleServiceRefusesAsDotNetDoes(t *testing.T) {
	if _, err := NewLifecycleService(nil, LifecycleOptions{}); err == nil {
		t.Fatal("a nil store was accepted")
	}
	var argument *ConfidenceArgumentError
	_, err := NewLifecycleService(&scriptedStore{}, LifecycleOptions{HostTrustedEvidence: "Sometimes",
		ConfidenceEngine: cfHostEngine{"", "1", "bayes"}})
	if !errors.As(err, &argument) || argument.Parameter != "independence" {
		t.Fatalf("an undefined effect: %v", err)
	}
	_, err = NewLifecycleService(&scriptedStore{}, LifecycleOptions{ConfidenceEngine: cfHostEngine{"", "1", "bayes"}})
	if !errors.As(err, &argument) || argument.Parameter != ConfidenceEngineParameter ||
		argument.Message != "The confidence engine's RuleId must be 1 to 64 characters from [A-Za-z0-9._-]." {
		t.Fatalf("a malformed engine: %v", err)
	}
	service, err := NewLifecycleService(&scriptedStore{}, LifecycleOptions{ConfidenceEngine: cfHostEngine{"bayes", "2", "bayes"}})
	if err != nil || service.ConfidenceRule().Recorded() != "bayes/2" ||
		service.IndependenceVerification() != IndependenceTrustHostSuppliedIdentifiers {
		t.Fatalf("%v", err)
	}
}

func TestTheTransitionTableIsDotNets(t *testing.T) {
	allowed := map[ExperienceStatus][]ExperienceStatus{
		StatusCandidate:   {StatusValidated, StatusQuarantined, StatusRevoked},
		StatusValidated:   {StatusReinforced, StatusContested, StatusStale, StatusSuperseded, StatusRevoked},
		StatusReinforced:  {StatusContested, StatusStale, StatusSuperseded, StatusRevoked},
		StatusQuarantined: {StatusRevoked},
		StatusContested:   {StatusRevoked},
		StatusStale:       {StatusRevoked},
		StatusSuperseded:  {StatusRevoked},
		StatusRevoked:     nil,
	}
	for prior, targets := range allowed {
		for _, name := range append(experienceStatuses, "Archived") {
			current := ExperienceStatus(name)
			want := false
			for _, target := range targets {
				want = want || target == current
			}
			if IsTransitionAllowed(prior, current) != want {
				t.Errorf("%s to %s: %v", prior, current, !want)
			}
		}
	}
	if IsTransitionAllowed("Archived", StatusRevoked) || !IsEligible(StatusReinforced) || IsEligible(StatusContested) ||
		FirstEventStatus != StatusCandidate {
		t.Error("an undefined prior status, or eligibility")
	}
}

func TestLifecycleOperationsAreReportedWithTheirAttributes(t *testing.T) {
	observer := &recordingObserver{}
	SetObserver(observer)
	t.Cleanup(func() { SetObserver(nil) })
	ctx := context.Background()

	store := &scriptedStore{read: RecordReadResult{Outcome: StoreFound, Record: validatedRecord()},
		commit: LifecycleCommitResult{Outcome: StoreCommitted, Revision: 2}}
	service, err := NewLifecycleService(store, LifecycleOptions{})
	if err != nil {
		t.Fatal(err)
	}
	if result, err := service.Commit(ctx, lifecycleAuth, commitRequest()); err != nil || result.Outcome != TransitionCommitted {
		t.Fatalf("%+v %v", result, err)
	}
	if result, err := service.ApplyEvidence(ctx, lifecycleAuth, evidenceRequest()); err != nil ||
		result.Outcome != ConfidenceApplied || result.Counted() {
		// A store that reports no stored update is taken at its word that nothing moved.
		t.Fatalf("%+v %v", result, err)
	}
	own := evidenceRequest()
	own.RunID = validatedRecord().SourceRunID
	if result, err := service.ApplyEvidence(ctx, lifecycleAuth, own); err != nil || result.Outcome != ConfidenceUnverified ||
		*result.Refusal != RefusalOwnRun {
		t.Fatalf("%+v %v", result, err)
	}
	store.err = errors.New("the connection dropped")
	if _, err := service.Commit(ctx, lifecycleAuth, commitRequest()); err == nil {
		t.Fatal("the store's failure was swallowed")
	}

	observer.mu.Lock()
	defer observer.mu.Unlock()
	if len(observer.finished) != 4 {
		t.Fatalf("%d operations", len(observer.finished))
	}
	attributes := func(o Observation) map[string]any {
		found := map[string]any{}
		for _, a := range o.Attributes {
			found[a.Key] = a.Value
		}
		return found
	}
	commit, apply, unverified, faulted := observer.finished[0], observer.finished[1], observer.finished[2], observer.finished[3]
	if commit.Operation != OperationLifecycleCommit || commit.Source != SourceCore || commit.Outcome != "Committed" ||
		attributes(commit)[AttributeEventID] != (UUID{15: 3}).String() ||
		attributes(commit)[AttributeExperienceID] != (UUID{15: 1}).String() {
		t.Errorf("commit %+v", commit)
	}
	if apply.Operation != OperationConfidenceApply || apply.Outcome != "Applied" ||
		attributes(apply)[AttributeConfidenceAdmission] != "HostTrusted" ||
		attributes(apply)[AttributeIndependenceRefusal] != nil {
		t.Errorf("apply %+v", apply)
	}
	if unverified.Outcome != "Unverified" || attributes(unverified)[AttributeIndependenceRefusal] != "OwnRun" ||
		attributes(unverified)[AttributeConfidenceAdmission] != nil {
		t.Errorf("unverified %+v", unverified)
	}
	if faulted.Outcome != OutcomeFaulted || faulted.ErrorClass != ErrorClassUnexpected ||
		attributes(faulted)[AttributeEventID] == nil {
		t.Errorf("faulted %+v", faulted)
	}
}

func TestAStoreThatBreaksItsContractIsAnError(t *testing.T) {
	ctx := context.Background()
	store := &scriptedStore{commit: LifecycleCommitResult{Outcome: StoreFound}}
	service, _ := NewLifecycleService(store, LifecycleOptions{})
	if _, err := service.Commit(ctx, lifecycleAuth, commitRequest()); !errors.Is(err, ErrStoreContract) ||
		!strings.Contains(err.Error(), "'Found', which is not a lifecycle commit outcome") {
		t.Fatalf("%v", err)
	}
	store.read = RecordReadResult{Outcome: StoreFound, Record: validatedRecord()}
	if _, err := service.ApplyEvidence(ctx, lifecycleAuth, evidenceRequest()); !errors.Is(err, ErrStoreContract) {
		t.Fatalf("%v", err)
	}
	// A history whose cursor does not move forward cannot be read to its end.
	next := int64(1)
	store.history = []HistoryResult{{Outcome: StoreFound, Revision: 1, NextStartAfterRevision: &next}}
	if _, err := service.ReadConfidence(ctx, lifecycleAuth, lifecycleScope, UUID{15: 1}, FilterAll); !errors.Is(err, ErrStoreContract) {
		t.Fatalf("%v", err)
	}
	// A store that reports Found with no record is NotFound, with a reason.
	store.read = RecordReadResult{Outcome: StoreFound}
	if result, err := service.ApplyEvidence(ctx, lifecycleAuth, evidenceRequest()); err != nil ||
		result.Outcome != ConfidenceNotFound || result.Reason == nil {
		t.Fatalf("%+v %v", result, err)
	}
}

func TestAnEngineFailureIsReturnedAndNothingIsCommitted(t *testing.T) {
	ctx := context.Background()
	store := &scriptedStore{read: RecordReadResult{Outcome: StoreFound, Record: validatedRecord()},
		commit: LifecycleCommitResult{Outcome: StoreFound}}
	service, _ := NewLifecycleService(store, LifecycleOptions{ConfidenceEngine: cfHostEngine{"broken", "1", "throws"},
		HostTrustedEvidence: HostTrustedCounted})
	if _, err := service.ApplyEvidence(ctx, lifecycleAuth, evidenceRequest()); !errors.Is(err, errCfHostEngine) {
		t.Fatalf("%v", err)
	}
	service, _ = NewLifecycleService(store, LifecycleOptions{ConfidenceEngine: cfHostEngine{"high", "1", "Infinity"},
		HostTrustedEvidence: HostTrustedCounted})
	var engine *ConfidenceEngineError
	if _, err := service.ApplyEvidence(ctx, lifecycleAuth, evidenceRequest()); !errors.As(err, &engine) ||
		engine.Rule != "high/1" {
		t.Fatalf("%v", err)
	}
	// Recorded only (explicitly, and by default until the port verifies independence), the engine is not asked, and
	// the event moves nothing but the revision.
	store.commit = LifecycleCommitResult{Outcome: StoreCommitted, Revision: 2}
	for _, effect := range []HostTrustedEvidenceEffect{HostTrustedRecordedOnly, ""} {
		service, _ = NewLifecycleService(store, LifecycleOptions{ConfidenceEngine: cfHostEngine{"broken", "1", "throws"},
			HostTrustedEvidence: effect})
		result, err := service.ApplyEvidence(ctx, lifecycleAuth, evidenceRequest())
		if err != nil || result.Counted() || result.Update.RuleVersion != "broken/1" ||
			result.Event.CurrentStatus != StatusValidated || result.Event.Confidence.NewSupportingValidations != 1 {
			t.Fatalf("%q: %+v %v", effect, result, err)
		}
	}
}

func TestTheAssessmentTokenIsRedacted(t *testing.T) {
	request := evidenceRequest()
	request.AssessmentToken = Ptr("aexat1.secret")
	if text := request.String(); strings.Contains(text, "secret") || !strings.Contains(text, "<redacted>") {
		t.Fatal(text)
	}
}

func TestTheReuseIDsRefuseAnUndefinedKind(t *testing.T) {
	var argument *ConfidenceArgumentError
	if _, err := ReuseEvidenceIDFor(UUID{1}, lifecycleScope, UUID{2}, "Sideways"); !errors.As(err, &argument) ||
		argument.Parameter != "kind" {
		t.Fatalf("%v", err)
	}
	evidence, _ := ReuseEvidenceIDFor(UUID{1}, lifecycleScope, UUID{2}, EvidenceSupporting)
	event, _ := ReuseEventIDFor(UUID{1}, lifecycleScope, UUID{2}, EvidenceSupporting)
	if evidence == event || evidence[6]>>4 != 8 {
		t.Fatal("the two IDs")
	}
}
