package maf

// Contract 1.12: the retrieval policy in injection. Timing is not a fixture: a store that answers slowly, or only
// when the test releases it, stands in for a slow database.

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

// hangingStore answers a search only once released, whatever its context says (a store that ignores cancellation).
type hangingStore struct {
	*inmemory.Store
	release chan struct{}
	calls   atomic.Int64
	once    sync.Once
}

func newHangingStore(t *testing.T) *hangingStore {
	t.Helper()
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	s := &hangingStore{Store: store, release: make(chan struct{})}
	t.Cleanup(s.open)
	return s
}

func (s *hangingStore) open() { s.once.Do(func() { close(s.release) }) }

func (s *hangingStore) Search(ctx context.Context, bounds ae.AuthorizationBounds, p ae.SearchParameters) ([]ae.SearchRow,
	error) {
	s.calls.Add(1)
	<-s.release
	return s.Store.Search(context.WithoutCancel(ctx), bounds, p)
}

// eventually waits up to five seconds for condition.
func eventually(t *testing.T, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for !condition() {
		if time.Now().After(deadline) {
			t.Fatal("the condition never held")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func outcomes(o *observed, operation string) []string {
	var result []string
	for _, observation := range o.of(operation) {
		result = append(result, observation.Outcome)
	}
	return result
}

func slowAgent(t *testing.T, h *host, store *hangingStore, policy ae.RetrievalPolicy) (*agent.Agent, *ContextProvider) {
	t.Helper()
	config := &agent.Config{Name: "triage-ticket"}
	if err := UseAgentExperience(config, h.options(Options{Store: store, RetrievalPolicy: policy})); err != nil {
		t.Fatal(err)
	}
	return newAgent(newScripted(0), *config), ContextProviderOf(config)
}

func TestASearchPastTheTimeoutIsTimedOutWithNoBlockAndIsCountedUntilItEnds(t *testing.T) {
	o := observe(t)
	h, store := newHost(t), newHangingStore(t)
	a, provider := slowAgent(t, h, store, ae.RetrievalPolicy{Timeout: 50 * time.Millisecond})
	started := time.Now()
	if runText(t, a, ticket).String() != "done" {
		t.Fatal("the agent did not run")
	}
	if elapsed := time.Since(started); elapsed > 3*time.Second {
		t.Fatalf("the invocation waited %v for the store", elapsed)
	}
	if got := outcomes(o, "inject"); len(got) != 1 || got[0] != "RetrievalTimedOut" {
		t.Fatalf("inject outcomes %v", got)
	}
	if got := outcomes(o, "retrieve"); len(got) != 1 || got[0] != "TimedOut" {
		t.Fatalf("retrieve outcomes %v", got)
	}
	h.mu.Lock()
	failures, injected := len(h.injectionFailures), len(h.injections)+len(h.nothing)
	h.mu.Unlock()
	if failures != 0 || injected != 0 {
		t.Fatalf("a real timeout reported %d failures and %d results", failures, injected)
	}
	if provider.searches.Load() != 1 {
		t.Fatalf("%d abandoned searches counted", provider.searches.Load())
	}
	store.open()
	eventually(t, func() bool { return provider.searches.Load() == 0 })
}

func TestAtTheAbandonedSearchCapRetrievalTimesOutAtOnceWithoutSearching(t *testing.T) {
	o := observe(t)
	h, store := newHost(t), newHangingStore(t)
	a, provider := slowAgent(t, h, store, ae.RetrievalPolicy{Timeout: 30 * time.Millisecond, MaxAbandonedSearches: 1})
	runText(t, a, ticket) // times out, and its search is abandoned
	runText(t, a, ticket) // refused at the cap
	if store.calls.Load() != 1 {
		t.Fatalf("the store was searched %d times", store.calls.Load())
	}
	if got := outcomes(o, "inject"); strings.Join(got, ",") != "RetrievalTimedOut,RetrievalTimedOut" {
		t.Fatalf("inject outcomes %v", got)
	}
	h.mu.Lock()
	failures := append([]InjectionFailure(nil), h.injectionFailures...)
	h.mu.Unlock()
	if len(failures) != 1 || !strings.Contains(failures[0].Reason, "too many abandoned searches") ||
		!strings.Contains(failures[0].Reason, "at least 1") {
		t.Fatalf("failures %+v", failures)
	}

	// Once the abandoned search ends, retrieval reaches the store again.
	store.open()
	eventually(t, func() bool { return provider.searches.Load() == 0 })
	runText(t, a, ticket)
	if store.calls.Load() != 2 {
		t.Fatalf("the store was searched %d times", store.calls.Load())
	}
	if got := outcomes(o, "retrieve"); got[len(got)-1] != "Completed" {
		t.Fatalf("retrieve outcomes %v", got)
	}
}

func TestARequestScopeOutsideTheBoundsIsDeniedWithoutSearching(t *testing.T) {
	o := observe(t)
	h, store := newHost(t), newHangingStore(t)
	store.open()
	config := &agent.Config{Name: "triage-ticket"}
	other := "another-application"
	if err := UseAgentExperience(config, h.options(Options{Store: store, Identity: ae.Identity{Principal: "svc",
		Scope: testScope, Bounds: &ae.AuthorizationBounds{TenantID: testScope.TenantID, ApplicationID: &other}}})); err != nil {
		t.Fatal(err)
	}
	runText(t, newAgent(newScripted(0), *config), ticket)
	if store.calls.Load() != 0 {
		t.Fatal("a denied retrieval searched")
	}
	if got := outcomes(o, "inject"); len(got) != 1 || got[0] != "RetrievalDenied" {
		t.Fatalf("inject outcomes %v", got)
	}
	if got := outcomes(o, "retrieve"); len(got) != 1 || got[0] != "Denied" {
		t.Fatalf("retrieve outcomes %v", got)
	}
}

func TestTheCallersCancellationIsAFailedSearchAndTheSearchIsAbandoned(t *testing.T) {
	h, store := newHost(t), newHangingStore(t)
	_, provider := slowAgent(t, h, store, ae.RetrievalPolicy{Timeout: time.Hour})
	ctx, cancel := context.WithCancel(context.Background())
	identity := ae.Identity{Principal: "svc", Scope: testScope}
	done := make(chan bool)
	go func() {
		_, ok := provider.retrieve(ctx, nil, &identity, "refund stuck on a lock")
		done <- ok
	}()
	eventually(t, func() bool { return store.calls.Load() == 1 })
	cancel()
	if <-done {
		t.Fatal("a cancelled retrieval completed")
	}
	h.mu.Lock()
	failures := append([]InjectionFailure(nil), h.injectionFailures...)
	h.mu.Unlock()
	if len(failures) != 1 || !errors.Is(failures[0].Err, context.Canceled) {
		t.Fatalf("failures %+v", failures)
	}
	if provider.searches.Load() != 1 {
		t.Fatalf("%d abandoned searches counted", provider.searches.Load())
	}
	store.open()
	eventually(t, func() bool { return provider.searches.Load() == 0 })
}

func TestAnInvalidRetrievalPolicyOrDecayIsRefused(t *testing.T) {
	zero := time.Duration(0)
	for name, options := range map[string]Options{
		"negative timeout":   {RetrievalPolicy: ae.RetrievalPolicy{Timeout: -time.Millisecond}},
		"timeout over a day": {RetrievalPolicy: ae.RetrievalPolicy{Timeout: 25 * time.Hour}},
		"candidate limit":    {RetrievalPolicy: ae.RetrievalPolicy{CandidateLimit: 200}},
		"blank domain key":   {ConfidenceDecay: &ae.ConfidenceDecayPolicy{DomainKey: "  "}},
		"zero half-life":     {ConfidenceDecay: &ae.ConfidenceDecayPolicy{DefaultHalfLife: &zero}},
	} {
		h := newHost(t)
		var refused *ae.PolicyError
		if err := UseAgentExperience(&agent.Config{Name: "a"}, h.options(options)); !errors.As(err, &refused) {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func TestTheRetrievalPolicyExpiresAndDecaysLessonsInInjection(t *testing.T) {
	month := 30 * 24 * time.Hour
	run := func(policy ae.RetrievalPolicy, decay *ae.ConfidenceDecayPolicy, age time.Duration) (*host, *scripted) {
		h, clock := newHost(t), newMovingClock()
		config := &agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=3", "exit=0")}}
		options := h.options(Options{Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests"),
			Clock: clock.read, RetrievalPolicy: policy, ConfidenceDecay: decay,
			BlockOptions: ae.BlockOptions{Rendering: ae.RenderingVerbose}})
		if err := UseAgentExperience(config, options); err != nil {
			t.Fatal(err)
		}
		model := newScripted(2)
		a := newAgent(model, *config)
		runText(t, a, ticket)
		if len(h.finalized) != 1 {
			t.Fatalf("%d lessons learned", len(h.finalized))
		}
		clock.advance(age)
		runText(t, a, nextTicket)
		return h, model
	}

	// Past MaxAge: excluded as Expired, so nothing is injected.
	h, model := run(ae.RetrievalPolicy{MaxAge: 90 * 24 * time.Hour}, nil, 91*24*time.Hour)
	h.mu.Lock()
	nothing := append([]InjectionResult(nil), h.nothing...)
	h.mu.Unlock()
	lesson := h.finalized[0].Record
	if len(model.blocks()) != 0 || len(nothing) != 2 || len(nothing[1].Excluded) != 1 ||
		nothing[1].Excluded[0] != (ae.Exclusion{ExperienceID: lesson.ExperienceID, Reason: ae.ExclusionExpired}) {
		t.Fatalf("an expired lesson: %+v", nothing)
	}

	// One half-life old: injected, ranked on half its stored confidence; the Confidence line keeps the stored value.
	h, model = run(ae.RetrievalPolicy{}, &ae.ConfidenceDecayPolicy{DefaultHalfLife: &month}, month)
	lesson = h.finalized[0].Record
	blocks := model.blocks()
	if len(blocks) == 0 {
		t.Fatal("the decayed lesson was not injected")
	}
	text := blocks[0].String()
	decayed := fmt.Sprintf("Confidence %.3f x 0.250", lesson.ReuseConfidence/2)
	stored := fmt.Sprintf("Confidence: %.3f", lesson.ReuseConfidence)
	if !strings.Contains(text, decayed) || !strings.Contains(text, stored) {
		t.Fatalf("expected %q and %q in:\n%s", decayed, stored, text)
	}
}
