package inmemory

import (
	"context"
	"errors"
	"sync"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// Writers deciding against one revision race for real: exactly one commits, every other is StaleRevision with the
// revision the winner produced, and the history holds the winner's event alone.
func TestConcurrentCommitsAtOneRevisionLetExactlyOneWin(t *testing.T) {
	const writers = 32
	ctx := context.Background()
	seeded := record(1, "rotate-keys", "Rotate the signing keys", "Rotate them.", ae.StatusValidated, 0.5)
	store, err := New(seeded)
	if err != nil {
		t.Fatal(err)
	}
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	if err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "governance", Bounds: ae.BoundsOf(scope)}
	start := make(chan struct{})
	results := make([]ae.CommitLifecycleTransitionResult, writers)
	failures := make([]error, writers)
	var wg sync.WaitGroup
	for i := range writers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			prior := ae.StatusValidated
			results[i], failures[i] = service.Commit(ctx, authorization, ae.CommitLifecycleTransitionRequest{
				EventID:          ae.UUID{0: 0xe7, 6: 0x40, 8: 0x80, 14: 1, 15: byte(i)},
				ExperienceID:     seeded.ExperienceID,
				Scope:            scope,
				PriorStatus:      &prior,
				CurrentStatus:    ae.StatusRevoked,
				Reason:           "Withdrawn by the governance review.",
				Producer:         "governance-review/1.0",
				OccurredAt:       at,
				ExpectedRevision: 1,
			})
		}()
	}
	close(start)
	wg.Wait()
	committed := 0
	for i := range writers {
		if failures[i] != nil {
			t.Fatal(failures[i])
		}
		switch results[i].Outcome {
		case ae.TransitionCommitted:
			committed++
			if results[i].Revision != 2 {
				t.Errorf("committed at %d", results[i].Revision)
			}
		case ae.TransitionStaleRevision:
			if results[i].Revision != 2 {
				t.Errorf("stale with %d", results[i].Revision)
			}
		default:
			t.Errorf("writer %d: %s", i, results[i].Outcome)
		}
	}
	if committed != 1 {
		t.Fatalf("%d writers committed", committed)
	}
	if history := store.History(seeded.ExperienceID); len(history) != 1 || history[0].AppliedRevision != 2 {
		t.Fatalf("history %+v", history)
	}
}

// Evidence for one independence key under many evidence IDs at once: one is counted, every other is recorded and
// counted zero times, and the counters moved once.
func TestConcurrentEvidenceForOneKeyIsCountedOnce(t *testing.T) {
	const writers = 16
	ctx := context.Background()
	seeded := record(2, "rotate-keys", "Rotate the signing keys", "Rotate them.", ae.StatusValidated, 0.5)
	store, err := New(seeded)
	if err != nil {
		t.Fatal(err)
	}
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	if err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "aggregator", Bounds: ae.BoundsOf(scope)}
	round := ae.UUID{0: 0x50, 6: 0x40, 8: 0x80, 15: 1}
	start := make(chan struct{})
	results := make([]ae.ApplyConfidenceEvidenceResult, writers)
	failures := make([]error, writers)
	var wg sync.WaitGroup
	for i := range writers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			results[i], failures[i] = service.ApplyEvidence(ctx, authorization, ae.ApplyConfidenceEvidenceRequest{
				EventID:             ae.UUID{0: 0xe8, 6: 0x40, 8: 0x80, 15: byte(i)},
				ExperienceID:        seeded.ExperienceID,
				Scope:               scope,
				EvidenceID:          ae.UUID{0: 0xe9, 6: 0x40, 8: 0x80, 15: byte(i)},
				Kind:                ae.EvidenceSupporting,
				Source:              ae.EvidenceMachine,
				RunID:               ae.UUID{0: 0x51, 6: 0x40, 8: 0x80, 15: 9},
				VerificationRoundID: &round,
				Reason:              "the lesson was reused and the checks passed",
				Producer:            "verification-aggregator/1.0.0",
				OccurredAt:          at,
			})
		}()
	}
	close(start)
	wg.Wait()
	counted := 0
	for i := range writers {
		if failures[i] != nil {
			t.Fatal(failures[i])
		}
		switch {
		case results[i].Outcome == ae.ConfidenceApplied && results[i].Counted():
			counted++
		case results[i].Outcome == ae.ConfidenceApplied, results[i].Outcome == ae.ConfidenceStaleRevision:
		default:
			t.Errorf("writer %d: %s", i, results[i].Outcome)
		}
	}
	if counted != 1 {
		t.Fatalf("%d submissions counted", counted)
	}
	stored, _ := store.Get(ctx, ae.BoundsOf(scope), scope, seeded.ExperienceID)
	if stored.SupportingValidations != 1 || stored.Revision != 2 {
		t.Fatalf("supporting %d at revision %d", stored.SupportingValidations, stored.Revision)
	}
}

func TestAFinalizedRecordsInitialEventIsTheFirstOfItsHistory(t *testing.T) {
	ctx := context.Background()
	seeded := record(3, "rotate-keys", "Rotate the signing keys", "Rotate them.", ae.StatusValidated, 0.5)
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	clock := ae.MustParseInstant("2026-03-02T10:00:00.1234567+00:00")
	store.SetClock(func() ae.Instant { return clock })
	initial := ae.InitialLifecycleEvent{EventID: ae.UUID{0: 0x1e, 6: 0x40, 8: 0x80, 15: 3},
		ExperienceID: seeded.ExperienceID, PriorStatus: ae.StatusCandidate, CurrentStatus: ae.StatusValidated,
		Reason: "Initial finalization.", Producer: ae.FinalizationProducer, OccurredAt: at, AppliedRevision: 1}
	if err := store.InsertFinalized(ctx, ae.BoundsOf(scope), &ae.FinalizedExperience{Record: seeded, Event: initial},
		"finalizer", nil); err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "reader", Bounds: ae.BoundsOf(scope)}
	page, err := store.GetHistory(ctx, authorization, ae.HistoryQuery{Scope: scope, ExperienceID: seeded.ExperienceID,
		Limit: 10})
	if err != nil {
		t.Fatal(err)
	}
	if page.Outcome != ae.StoreFound || len(page.Events) != 1 {
		t.Fatalf("%+v", page)
	}
	event := page.Events[0]
	if event.AppliedRevision != 1 || event.RecordedAt != clock.TruncateToMicroseconds() || *event.Actor != "finalizer" ||
		*event.Event.PriorStatus != ae.StatusCandidate || event.Event.CurrentStatus != ae.StatusValidated {
		t.Fatalf("%+v", event)
	}
	if held, _ := store.Event(seeded.ExperienceID); !held.RecordedAt.Equal(clock.TruncateToMicroseconds().Time()) {
		t.Fatalf("recorded at %v", held.RecordedAt)
	}
	// The next commit continues from it, and replaying the initial event is its replay.
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	if err != nil {
		t.Fatal(err)
	}
	prior := ae.StatusValidated
	result, err := service.Commit(ctx, authorization, ae.CommitLifecycleTransitionRequest{EventID: ae.UUID{0: 0x2e, 15: 3},
		ExperienceID: seeded.ExperienceID, Scope: scope, PriorStatus: &prior, CurrentStatus: ae.StatusReinforced,
		Reason: "Reinforced.", Producer: "review", OccurredAt: at, ExpectedRevision: 1})
	if err != nil || result.Outcome != ae.TransitionCommitted || result.Revision != 2 {
		t.Fatalf("%+v %v", result, err)
	}
}

// A commit cancelled while it waits for the store writes nothing: no record change, no event, no evidence.
func TestACommitCancelledWhileWaitingWritesNothing(t *testing.T) {
	seeded := record(4, "rotate-keys", "Rotate the signing keys", "Rotate them.", ae.StatusValidated, 0.5)
	store, err := New(seeded)
	if err != nil {
		t.Fatal(err)
	}
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	if err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "aggregator", Bounds: ae.BoundsOf(scope)}
	round := ae.UUID{0: 0x50, 6: 0x40, 8: 0x80, 15: 4}
	ctx, cancel := context.WithCancel(context.Background())
	store.mu.Lock() // another writer holds the store
	done := make(chan error, 1)
	go func() {
		_, err := service.ApplyEvidence(ctx, authorization, ae.ApplyConfidenceEvidenceRequest{
			EventID: ae.UUID{0: 0xea, 15: 4}, ExperienceID: seeded.ExperienceID, Scope: scope,
			EvidenceID: ae.UUID{0: 0xeb, 15: 4}, Kind: ae.EvidenceSupporting, Source: ae.EvidenceMachine,
			RunID: ae.UUID{0: 0x51, 15: 4}, VerificationRoundID: &round, Reason: "reused", Producer: "aggregator",
			OccurredAt: at})
		done <- err
	}()
	cancel()
	store.mu.Unlock()
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatalf("%v", err)
	}
	stored, _ := store.Get(context.Background(), ae.BoundsOf(scope), scope, seeded.ExperienceID)
	if stored.Revision != 1 || stored.SupportingValidations != seeded.SupportingValidations || stored.UpdatedAt != at {
		t.Fatalf("%+v", stored)
	}
	store.mu.RLock()
	defer store.mu.RUnlock()
	if len(store.lifecycle.history) != 0 || len(store.lifecycle.evidence) != 0 || len(store.lifecycle.eventsByID) != 0 {
		t.Fatal("the cancelled commit wrote something")
	}
}
