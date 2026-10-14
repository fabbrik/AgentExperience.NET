package maf

// Contract 1.4: once a block is handed over, the records it delivered are recorded as injected, in the background,
// bounded and fail-open.

import (
	"context"
	"errors"
	"reflect"
	"sync"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

// recordings collects what OnInjectionRecorded is told, from the recordings' own goroutines.
type recordings struct {
	mu      sync.Mutex
	results []InjectionRecordResult
}

func (r *recordings) add(result InjectionRecordResult) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.results = append(r.results, result)
}

func (r *recordings) all() []InjectionRecordResult {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]InjectionRecordResult(nil), r.results...)
}

// learning is an agent whose first run stores a lesson the second run is shown, and its configuration.
func learning(t *testing.T, h *host, store ae.Store, recorded *recordings, timeout time.Duration) (*agent.Agent,
	*agent.Config) {
	t.Helper()
	config := &agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=3", "exit=0")}}
	options := h.options(Options{Store: store, Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: ae.ExitCode("run_tests"), InjectionRecordTimeout: timeout})
	options.OnInjectionRecorded = recorded.add
	if err := UseAgentExperience(config, options); err != nil {
		t.Fatal(err)
	}
	return newAgent(newScripted(2), *config), config
}

func closeRecordings(t *testing.T, config *agent.Config) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := ContextProviderOf(config).Close(ctx); err != nil {
		t.Fatal(err)
	}
}

func TestTheRecordsABlockDeliveredAreRecordedAsInjectedInTheBackground(t *testing.T) {
	h, recorded := newHost(t), &recordings{}
	a, config := learning(t, h, h.store, recorded, 0)
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	closeRecordings(t, config)

	first := h.finalized[0].Record.ExperienceID
	results := recorded.all()
	if len(results) != 1 || results[0].Outcome != InjectionRecorded || results[0].Err != nil ||
		!reflect.DeepEqual(results[0].ExperienceIDs, []ae.UUID{first}) {
		t.Fatalf("results %+v", results)
	}
	injections := h.store.Injections()
	if len(injections) != 1 || injections[first].InjectionCount != 1 {
		t.Fatalf("injections %+v", injections)
	}
}

// searchOnly is a store that does not record injections.
type searchOnly struct{ inner *inmemory.Store }

func (s searchOnly) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, f *ae.FinalizedExperience,
	actor string, at *time.Time) error {
	return s.inner.InsertFinalized(ctx, bounds, f, actor, at)
}

func (s searchOnly) Search(ctx context.Context, bounds ae.AuthorizationBounds, p ae.SearchParameters) ([]ae.SearchRow,
	error) {
	return s.inner.Search(ctx, bounds, p)
}

func TestAStoreThatDoesNotRecordInjectionsIsReportedNotTracked(t *testing.T) {
	h, recorded := newHost(t), &recordings{}
	a, config := learning(t, h, searchOnly{h.store}, recorded, 0)
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	closeRecordings(t, config)
	if results := recorded.all(); len(results) != 1 || results[0].Outcome != InjectionNotTracked {
		t.Fatalf("results %+v", results)
	}
	if len(h.injections) != 1 || len(h.injectionFailures) != 0 {
		t.Fatalf("injections %+v, failures %+v", h.injections, h.injectionFailures)
	}
}

// failingRecorder is the in-memory store whose RecordInjection fails, blocks, panics or counts.
type failingRecorder struct {
	*inmemory.Store
	err    error
	gate   chan struct{}
	panics bool
	mu     *sync.Mutex
	chunks *[]int
}

func (f failingRecorder) RecordInjection(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	ids []ae.UUID, at ae.Instant) error {
	if f.chunks != nil {
		f.mu.Lock()
		*f.chunks = append(*f.chunks, len(ids))
		f.mu.Unlock()
		return nil
	}
	if f.panics {
		panic("record")
	}
	if f.gate != nil {
		<-f.gate // ignores ctx, as a store that does not honour it would
	}
	if f.err != nil {
		return f.err
	}
	return f.Store.RecordInjection(ctx, bounds, scope, ids, at)
}

func TestARecordThatFailsOrPanicsIsReportedNotRecordedAndTheBlockIsStillInjected(t *testing.T) {
	denied := errors.New("permission denied for table experience_injections")
	for _, store := range []failingRecorder{{err: denied}, {panics: true}} {
		h, recorded := newHost(t), &recordings{}
		store.Store = h.store
		a, config := learning(t, h, store, recorded, 0)
		runText(t, a, ticket)
		if response := runText(t, a, nextTicket); response.String() != "done" {
			t.Fatalf("response %q", response.String())
		}
		closeRecordings(t, config)
		results := recorded.all()
		if len(results) != 1 || results[0].Outcome != InjectionNotRecorded || results[0].Err == nil {
			t.Fatalf("results %+v", results)
		}
		if store.err != nil && !errors.Is(results[0].Err, denied) {
			t.Fatalf("the error is %v", results[0].Err)
		}
		if len(h.injections) != 1 || len(h.injectionFailures) != 0 {
			t.Fatalf("injections %+v, failures %+v", h.injections, h.injectionFailures)
		}
	}
}

func TestASlowRecordNeverDelaysTheInvocationAndIsAbandonedAsOutcomeUnknown(t *testing.T) {
	h, recorded := newHost(t), &recordings{}
	gate := make(chan struct{})
	a, config := learning(t, h, failingRecorder{Store: h.store, gate: gate}, recorded, 50*time.Millisecond)
	runText(t, a, ticket)
	// The invocation returns while the record is still blocked.
	if response := runText(t, a, nextTicket); response.String() != "done" {
		t.Fatalf("response %q", response.String())
	}
	provider := ContextProviderOf(config)
	closeRecordings(t, config)
	results := recorded.all()
	if len(results) != 1 || results[0].Outcome != InjectionOutcomeUnknown ||
		!errors.Is(results[0].Err, context.DeadlineExceeded) {
		t.Fatalf("results %+v", results)
	}
	if provider.AbandonedInjectionRecords() != 1 {
		t.Fatalf("%d abandoned", provider.AbandonedInjectionRecords())
	}
	close(gate)
	deadline := time.Now().Add(10 * time.Second)
	for provider.AbandonedInjectionRecords() != 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if provider.AbandonedInjectionRecords() != 0 {
		t.Fatal("the abandoned record is still counted after it ended")
	}
}

func TestMoreIDsThanTheStoreTakesAreRecordedInChunksOf200(t *testing.T) {
	h := newHost(t)
	var mu sync.Mutex
	var chunks []int
	recorded := &recordings{}
	options := h.options(Options{Store: failingRecorder{Store: h.store, mu: &mu, chunks: &chunks}})
	options.OnInjectionRecorded = recorded.add
	provider, err := NewContextProvider(options)
	if err != nil {
		t.Fatal(err)
	}
	ids := make([]ae.UUID, 450)
	for i := range ids {
		ids[i] = ae.NewRandomUUID()
	}
	identity := ae.Identity{Principal: "svc", Scope: testScope}
	provider.startRecording(context.Background(), &identity, ids, ae.Now())
	if err := provider.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(chunks, []int{200, 200, 50}) {
		t.Fatalf("chunks %v", chunks)
	}
	if results := recorded.all(); len(results) != 1 || results[0].Outcome != InjectionRecorded {
		t.Fatalf("results %+v", results)
	}
}

func TestANegativeInjectionRecordTimeoutIsRefused(t *testing.T) {
	h := newHost(t)
	if _, err := NewContextProvider(h.options(Options{InjectionRecordTimeout: -time.Second})); err == nil {
		t.Fatal("a negative timeout was accepted")
	}
}

func TestCloseIsBoundedByItsContext(t *testing.T) {
	h, recorded := newHost(t), &recordings{}
	gate := make(chan struct{})
	defer close(gate)
	a, config := learning(t, h, failingRecorder{Store: h.store, gate: gate}, recorded, time.Minute)
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if err := ContextProviderOf(config).Close(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("Close answered %v", err)
	}
}

func TestTheInMemoryStoreRecordsOnlyItsOwnScopeClampsAndSaturates(t *testing.T) {
	h, recorded := newHost(t), &recordings{}
	a, config := learning(t, h, h.store, recorded, 0)
	runText(t, a, ticket)
	closeRecordings(t, config)
	record := h.finalized[0].Record
	now, _ := ae.ParseInstant("2026-03-02T12:00:00+00:00")
	earlier, _ := ae.ParseInstant("2026-03-02T10:00:00.1234567+00:00")
	later, _ := ae.ParseInstant("2026-03-02T13:00:00+00:00")
	h.store.Clock = func() ae.Instant { return now }
	ctx, bounds := context.Background(), ae.BoundsOf(record.Scope)

	record3 := func(at ae.Instant) {
		t.Helper()
		if err := h.store.RecordInjection(ctx, bounds, record.Scope, []ae.UUID{record.ExperienceID, ae.NewRandomUUID()},
			at); err != nil {
			t.Fatal(err)
		}
	}
	record3(earlier)
	if got := h.store.Injections()[record.ExperienceID]; got != (inmemory.Injection{
		LastInjectedAt: earlier.TruncateToMicroseconds(), InjectionCount: 1}) {
		t.Fatalf("%+v", got)
	}
	record3(later)
	record3(earlier)
	if got := h.store.Injections()[record.ExperienceID]; got != (inmemory.Injection{LastInjectedAt: now,
		InjectionCount: 3}) {
		t.Fatalf("%+v", got)
	}
	other := record.Scope
	other.AgentID = ae.Ptr("someone-else")
	if err := h.store.RecordInjection(ctx, ae.BoundsOf(other), other, []ae.UUID{record.ExperienceID},
		earlier); err != nil || h.store.Injections()[record.ExperienceID].InjectionCount != 3 {
		t.Fatalf("another scope counted: %v", err)
	}
	foreign := record.Scope
	foreign.TenantID = "other-tenant"
	if err := h.store.RecordInjection(ctx, bounds, foreign, []ae.UUID{record.ExperienceID},
		earlier); !errors.Is(err, inmemory.ErrAuthorizationDenied) {
		t.Fatalf("outside the bounds: %v", err)
	}
	h.store.SetInjection(record.ExperienceID, inmemory.Injection{LastInjectedAt: now, InjectionCount: ae.MaxInjectionCount})
	record3(earlier)
	if got := h.store.Injections()[record.ExperienceID].InjectionCount; got != ae.MaxInjectionCount {
		t.Fatalf("count %d", got)
	}
}
