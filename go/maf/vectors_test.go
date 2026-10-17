package maf

// Story 23.3: semantic retrieval in Go (contract 1.6), through a real agent.Agent and the in-memory store. The
// Python port's cases (python/tests/test_vectors.py): every row of the fallback matrix, the floor, the bounded calls,
// and the background embedding write after a record is stored. The PostgreSQL cases are in vectors_postgres_test.go.

import (
	"context"
	"errors"
	"math"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

const (
	vectorModel = "contract-embedding"
	paraphrase  = "Renew the cryptographic credentials immediately"
	textQuery   = "Rotate the signing keys for the service"
)

var vectorScope = ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project",
	AgentID: ae.Ptr("refund-agent")}

// ---- The caller's embedder, and stores that misbehave ----------------------------------------------------------

// fixedEmbedder answers one vector for every text, unless it is told to misbehave.
type fixedEmbedder struct {
	vector    []float32
	model     string
	dimension int           // the declared dimension; len(vector) when 0
	fail      bool          // returns an error whose message holds record content
	panics    bool          // panics
	delay     time.Duration // sleeps, ignoring its context
	block     chan struct{} // blocks until closed, ignoring its context
	honour    bool          // blocks until its context is done, then returns its error

	mu        sync.Mutex
	texts     []string
	calls     int
	cancelled chan struct{}
}

func newFixed() *fixedEmbedder {
	return &fixedEmbedder{vector: []float32{1, 0, 0, 0}, model: vectorModel, cancelled: make(chan struct{}, 64)}
}

func (e *fixedEmbedder) ModelID() string { return e.model }

func (e *fixedEmbedder) Dimension() int {
	if e.dimension != 0 {
		return e.dimension
	}
	return len(e.vector)
}

func (e *fixedEmbedder) Embed(ctx context.Context, text string) ([]float32, error) {
	e.mu.Lock()
	e.texts = append(e.texts, text)
	e.calls++
	e.mu.Unlock()
	switch {
	case e.panics:
		panic("secret-record-content")
	case e.fail:
		return nil, errors.New("the provider is down: secret-record-content")
	case e.block != nil:
		<-e.block
	case e.honour:
		<-ctx.Done()
		e.cancelled <- struct{}{}
		return nil, ctx.Err()
	case e.delay > 0:
		time.Sleep(e.delay)
	}
	return append([]float32(nil), e.vector...), nil
}

func (e *fixedEmbedder) seen() []string {
	e.mu.Lock()
	defer e.mu.Unlock()
	return append([]string(nil), e.texts...)
}

func (e *fixedEmbedder) called() int {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.calls
}

// vectorSpy is an in-memory store that counts the vector searches it is asked to run, and can be made to fail.
type vectorSpy struct {
	*inmemory.Store
	mu          sync.Mutex
	searches    int
	searchErr   error
	searchDelay time.Duration
	writeErr    error
}

func (s *vectorSpy) SearchVectors(ctx context.Context, bounds ae.AuthorizationBounds,
	query ae.VectorQuery) (ae.VectorSearchResult, error) {
	s.mu.Lock()
	s.searches++
	s.mu.Unlock()
	if s.searchDelay > 0 {
		time.Sleep(s.searchDelay)
	}
	if s.searchErr != nil {
		return ae.VectorSearchResult{}, s.searchErr
	}
	return s.Store.SearchVectors(ctx, bounds, query)
}

func (s *vectorSpy) WriteEmbedding(ctx context.Context, bounds ae.AuthorizationBounds,
	write ae.EmbeddingWrite) (ae.EmbeddingWriteResult, error) {
	if s.writeErr != nil {
		return ae.EmbeddingWriteResult{}, s.writeErr
	}
	return s.Store.WriteEmbedding(ctx, bounds, write)
}

func (s *vectorSpy) searchCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.searches
}

// textOnlyStore has the text methods only: no vector methods.
type textOnlyStore struct{ inner *inmemory.Store }

func (s textOnlyStore) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, f *ae.FinalizedExperience,
	actor string, recordedAt *time.Time) error {
	return s.inner.InsertFinalized(ctx, bounds, f, actor, recordedAt)
}

func (s textOnlyStore) Search(ctx context.Context, bounds ae.AuthorizationBounds,
	p ae.SearchParameters) ([]ae.SearchRow, error) {
	return s.inner.Search(ctx, bounds, p)
}

// failingText has a text search that fails, and a vector channel that works.
type failingText struct{ *inmemory.Store }

func (failingText) Search(context.Context, ae.AuthorizationBounds, ae.SearchParameters) ([]ae.SearchRow, error) {
	return nil, errors.New("the text index is down")
}

// ---- The host ---------------------------------------------------------------------------------------------------

type vhost struct {
	*host
	fmu       sync.Mutex
	fallbacks []ae.VectorFallback
}

func (v *vhost) options(o Options) Options {
	if o.Identity.Principal == "" && o.ResolveIdentity == nil {
		o.Identity = ae.Identity{Principal: "go-principal", Scope: vectorScope}
	}
	o = v.host.options(o)
	o.OnVectorFallback = func(f ae.VectorFallback) { v.fmu.Lock(); v.fallbacks = append(v.fallbacks, f); v.fmu.Unlock() }
	return o
}

func (v *vhost) reasons() []ae.VectorFallbackReason {
	v.fmu.Lock()
	defer v.fmu.Unlock()
	var reasons []ae.VectorFallbackReason
	for _, f := range v.fallbacks {
		reasons = append(reasons, f.Reason)
	}
	return reasons
}

func seedRecords(t *testing.T, family, name string) []ae.ExperienceRecord {
	t.Helper()
	var records []ae.ExperienceRecord
	for _, stored := range ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, "seed.json")).([]any) {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		records = append(records, record)
	}
	return records
}

// embeddedStore holds the three 'rotate-keys' records of the nearest-first fixture, embedded as it does: 3 is the
// query's own vector, 2 is near, 1 is further.
func embeddedStore(t *testing.T) *inmemory.Store {
	t.Helper()
	records := seedRecords(t, "vector-search", "nearest-first")
	store, err := inmemory.New(records...)
	if err != nil {
		t.Fatal(err)
	}
	vectors := map[byte][]float32{1: {1, 1, 0, 0}, 2: {1, 0.1, 0, 0}, 3: {1, 0, 0, 0}}
	for _, record := range records {
		result, err := store.WriteEmbedding(context.Background(), ae.BoundsOf(vectorScope), ae.EmbeddingWrite{
			ExperienceID: record.ExperienceID, Scope: vectorScope, ModelID: vectorModel, Dimension: 4,
			ContentHash: "h", SourceRevision: record.Revision, Vector: vectors[record.ExperienceID[15]],
			Now: ae.MustParseInstant("2026-03-02T10:00:00+00:00")})
		if err != nil || result.Outcome != ae.EmbeddingWritten {
			t.Fatalf("%+v, %v", result, err)
		}
	}
	return store
}

func wire(t *testing.T, v *vhost, store ae.Store, o Options) (*agent.Agent, *agent.Config, *scripted) {
	t.Helper()
	o.Store = store
	model := newScripted(0)
	config := agent.Config{Name: "refund-agent"}
	if err := UseAgentExperience(&config, v.options(o)); err != nil {
		t.Fatal(err)
	}
	return newAgent(model, config), &config, model
}

func numbers(result InjectionResult) []int {
	numbers := make([]int, len(result.ExperienceIDs))
	for i, id := range result.ExperienceIDs {
		numbers[i] = int(id[15])
	}
	return numbers
}

func sorted(numbers []int) []int {
	out := append([]int(nil), numbers...)
	for i := range out {
		for j := i + 1; j < len(out); j++ {
			if out[j] < out[i] {
				out[i], out[j] = out[j], out[i]
			}
		}
	}
	return out
}

func equalInts(a, b []int) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func onlyInjection(t *testing.T, v *vhost) InjectionResult {
	t.Helper()
	if len(v.injections) != 1 {
		t.Fatalf("%d injections, failures %+v", len(v.injections), v.injectionFailures)
	}
	return v.injections[0]
}

// ---- Hybrid retrieval -------------------------------------------------------------------------------------------

func TestOnlyTheVectorFindsARewordedQueryAndWithoutAnEmbedderItDoesNot(t *testing.T) {
	v := &vhost{host: newHost(t)}
	a, _, model := wire(t, v, embeddedStore(t), Options{Embedder: newFixed()})
	runText(t, a, paraphrase)
	if len(v.reasons()) != 0 || len(v.injectionFailures) != 0 {
		t.Fatalf("fallbacks %v, failures %+v", v.fallbacks, v.injectionFailures)
	}
	result := onlyInjection(t, v)
	if !equalInts(numbers(result), []int{3, 2, 1}) || result.VectorFallback != nil {
		t.Fatalf("%v, fallback %+v", numbers(result), result.VectorFallback)
	}
	blocks := model.blocks()
	if len(blocks) != 1 || !strings.Contains(blocks[0].String(), "--- RECORD 1: rotate-keys ---") {
		t.Fatalf("%d blocks", len(blocks))
	}

	without := &vhost{host: newHost(t)}
	a, _, model = wire(t, without, embeddedStore(t), Options{})
	runText(t, a, paraphrase)
	if len(without.injections) != 0 || len(model.blocks()) != 0 {
		t.Fatalf("injections %+v", without.injections)
	}
	if reasons := without.reasons(); len(reasons) != 1 || reasons[0] != ae.FallbackNotConfigured {
		t.Fatalf("fallbacks %v", reasons)
	}
}

func TestATextMatchAndAVectorMatchAreOneEntry(t *testing.T) {
	v := &vhost{host: newHost(t)}
	a, _, _ := wire(t, v, embeddedStore(t), Options{Embedder: newFixed()})
	runText(t, a, textQuery)
	if got := sorted(numbers(onlyInjection(t, v))); !equalInts(got, []int{1, 2, 3}) {
		t.Fatalf("%v", got)
	}
}

func TestAStoreWithoutVectorMethodsAnswersFromTextAndReportsNotConfigured(t *testing.T) {
	v := &vhost{host: newHost(t)}
	embedder := newFixed()
	a, _, _ := wire(t, v, textOnlyStore{embeddedStore(t)}, Options{Embedder: embedder})
	runText(t, a, textQuery)
	if reasons := v.reasons(); len(reasons) != 1 || reasons[0] != ae.FallbackNotConfigured {
		t.Fatalf("fallbacks %v", reasons)
	}
	result := onlyInjection(t, v)
	if got := sorted(numbers(result)); !equalInts(got, []int{1, 2, 3}) {
		t.Fatalf("%v", got)
	}
	if result.VectorFallback == nil || result.VectorFallback.Reason != ae.FallbackNotConfigured || embedder.called() != 0 {
		t.Fatalf("%+v, %d embeds", result.VectorFallback, embedder.called())
	}
}

func TestABadEmbedderAnswersFromTextAndNoStatementRuns(t *testing.T) {
	for name, configure := range map[string]func(*fixedEmbedder){
		"raises":        func(e *fixedEmbedder) { e.fail = true },
		"panics":        func(e *fixedEmbedder) { e.panics = true },
		"times-out":     func(e *fixedEmbedder) { e.delay = 400 * time.Millisecond },
		"honours-ctx":   func(e *fixedEmbedder) { e.honour = true },
		"blank-model":   func(e *fixedEmbedder) { e.model = " " },
		"empty":         func(e *fixedEmbedder) { e.vector = nil },
		"wrong-length":  func(e *fixedEmbedder) { e.dimension = 3 },
		"bad-dimension": func(e *fixedEmbedder) { e.dimension = -1 },
		"nan":           func(e *fixedEmbedder) { e.vector = []float32{1, float32(math.NaN()), 0, 0} },
		"infinite":      func(e *fixedEmbedder) { e.vector = []float32{1, float32(math.Inf(1)), 0, 0} },
		"long-model-id": func(e *fixedEmbedder) { e.model = strings.Repeat("m", 300) },
	} {
		t.Run(name, func(t *testing.T) {
			embedder := newFixed()
			configure(embedder)
			store := &vectorSpy{Store: embeddedStore(t)}
			v := &vhost{host: newHost(t)}
			a, _, _ := wire(t, v, store, Options{Embedder: embedder, VectorTimeout: 100 * time.Millisecond})
			start := time.Now()
			runText(t, a, textQuery)
			if elapsed := time.Since(start); elapsed > 350*time.Millisecond {
				t.Fatalf("the vector channel held the injection for %v", elapsed)
			}
			if reasons := v.reasons(); len(reasons) != 1 || reasons[0] != ae.FallbackProviderUnavailable {
				t.Fatalf("fallbacks %v", reasons)
			}
			if strings.Contains(v.fallbacks[0].Detail, "secret-record-content") {
				t.Fatalf("the detail quotes content: %q", v.fallbacks[0].Detail)
			}
			if store.searchCount() != 0 || len(v.injectionFailures) != 0 {
				t.Fatalf("%d searches, failures %+v", store.searchCount(), v.injectionFailures)
			}
			if got := sorted(numbers(onlyInjection(t, v))); !equalInts(got, []int{1, 2, 3}) {
				t.Fatalf("%v", got)
			}
		})
	}
}

func TestAnEmbedderThatHonoursItsContextIsCancelledWhenTheTimeoutPasses(t *testing.T) {
	embedder := newFixed()
	embedder.honour = true
	v := &vhost{host: newHost(t)}
	a, _, _ := wire(t, v, embeddedStore(t), Options{Embedder: embedder, VectorTimeout: 50 * time.Millisecond})
	runText(t, a, textQuery)
	select {
	case <-embedder.cancelled:
	case <-time.After(2 * time.Second):
		t.Fatal("the embedder's context was never cancelled")
	}
}

func TestAMismatchAnswersFromTextAndSaysWhich(t *testing.T) {
	for name, tc := range map[string]struct {
		configure func(*fixedEmbedder)
		reason    ae.VectorFallbackReason
	}{
		"model":     {func(e *fixedEmbedder) { e.model = "another-model" }, ae.FallbackModelMismatch},
		"dimension": {func(e *fixedEmbedder) { e.vector = []float32{1, 0, 0} }, ae.FallbackDimensionMismatch},
	} {
		t.Run(name, func(t *testing.T) {
			embedder := newFixed()
			tc.configure(embedder)
			v := &vhost{host: newHost(t)}
			a, _, _ := wire(t, v, embeddedStore(t), Options{Embedder: embedder})
			runText(t, a, paraphrase)
			if reasons := v.reasons(); len(reasons) != 1 || reasons[0] != tc.reason {
				t.Fatalf("fallbacks %v", reasons)
			}
			if len(v.injections) != 0 {
				t.Fatalf("injections %+v", v.injections)
			}
		})
	}
}

func TestAFailingSearchAnswersFromTextAndReportsIt(t *testing.T) {
	for name, store := range map[string]*vectorSpy{
		"raises":    {searchErr: errors.New("the index is down")},
		"times-out": {searchDelay: 500 * time.Millisecond},
	} {
		t.Run(name, func(t *testing.T) {
			store.Store = embeddedStore(t)
			v := &vhost{host: newHost(t)}
			a, _, _ := wire(t, v, store, Options{Embedder: newFixed(), VectorTimeout: 100 * time.Millisecond})
			start := time.Now()
			runText(t, a, textQuery)
			if elapsed := time.Since(start); elapsed > 400*time.Millisecond {
				t.Fatalf("the vector channel held the injection for %v", elapsed)
			}
			if reasons := v.reasons(); len(reasons) != 1 || reasons[0] != ae.FallbackVectorSearchFailed {
				t.Fatalf("fallbacks %v", reasons)
			}
			if len(v.injectionFailures) != 0 {
				t.Fatalf("failures %+v", v.injectionFailures)
			}
			if got := sorted(numbers(onlyInjection(t, v))); !equalInts(got, []int{1, 2, 3}) {
				t.Fatalf("%v", got)
			}
		})
	}
}

func TestAFailingTextSearchFailsTheInjectionWhateverTheVectorFound(t *testing.T) {
	v := &vhost{host: newHost(t)}
	a, _, model := wire(t, v, failingText{embeddedStore(t)}, Options{Embedder: newFixed()})
	runText(t, a, paraphrase)
	if len(v.injectionFailures) != 1 || v.injectionFailures[0].Reason != "The search failed; no block was added." {
		t.Fatalf("failures %+v", v.injectionFailures)
	}
	if len(v.injections) != 0 || len(model.blocks()) != 0 {
		t.Fatalf("injections %+v", v.injections)
	}
}

func TestTheFloorDropsWeakVectorCandidatesAndNeverATextOne(t *testing.T) {
	floor := 0.9
	v := &vhost{host: newHost(t)}
	a, _, _ := wire(t, v, embeddedStore(t), Options{Embedder: newFixed(), MinVectorRelevance: &floor})
	runText(t, a, paraphrase)
	if got := numbers(onlyInjection(t, v)); !equalInts(got, []int{3, 2}) { // record 1's relevance is about 0.85
		t.Fatalf("%v", got)
	}

	// A text match is kept although its vector is below the floor.
	one := 1.0
	far := newFixed()
	far.vector = []float32{0, 0, 0, 1}
	v = &vhost{host: newHost(t)}
	a, _, _ = wire(t, v, embeddedStore(t), Options{Embedder: far, MinVectorRelevance: &one})
	runText(t, a, textQuery)
	if got := sorted(numbers(onlyInjection(t, v))); !equalInts(got, []int{1, 2, 3}) {
		t.Fatalf("%v", got)
	}
}

func TestOptionsRefuseABadFloorOrTimeout(t *testing.T) {
	store, _ := inmemory.New()
	base := Options{Store: store, Identity: ae.Identity{Principal: "p", Scope: vectorScope}}
	for name, mutate := range map[string]func(*Options){
		"floor above one":  func(o *Options) { o.MinVectorRelevance = floatPtr(1.5) },
		"floor below zero": func(o *Options) { o.MinVectorRelevance = floatPtr(-0.1) },
		"floor NaN":        func(o *Options) { o.MinVectorRelevance = floatPtr(math.NaN()) },
		"negative timeout": func(o *Options) { o.VectorTimeout = -time.Second },
	} {
		options := base
		mutate(&options)
		if _, err := newSettings(options); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	if s, err := newSettings(base); err != nil || s.VectorTimeout != DefaultVectorTimeout {
		t.Fatalf("%+v, %v", s, err)
	}
}

// ---- Bounded calls ------------------------------------------------------------------------------------------------

func TestHungVectorCallsFailFastAndNeverStarveTheTextSearch(t *testing.T) {
	release := make(chan struct{})
	embedder := newFixed()
	embedder.block = release
	v := &vhost{host: newHost(t)}
	a, config, _ := wire(t, v, embeddedStore(t), Options{Embedder: embedder, VectorTimeout: 10 * time.Millisecond})
	slots := settingsOf(config).vectorSlots

	start := time.Now()
	for range MaxVectorCalls + 4 {
		runText(t, a, textQuery)
	}
	if elapsed := time.Since(start); elapsed > 5*time.Second {
		t.Fatalf("the injections took %v", elapsed)
	}
	// Every injection was answered from text, and only MaxVectorCalls embedder calls ever started.
	if len(v.injections) != MaxVectorCalls+4 || embedder.called() != MaxVectorCalls || len(slots) != MaxVectorCalls {
		t.Fatalf("%d injections, %d embedder calls, %d slots held", len(v.injections), embedder.called(), len(slots))
	}
	for _, f := range v.fallbacks {
		if f.Reason != ae.FallbackProviderUnavailable {
			t.Fatalf("fallback %+v", f)
		}
	}
	if last := v.fallbacks[len(v.fallbacks)-1]; !errors.Is(last.Err, errTooManyVectorCalls) {
		t.Fatalf("the call past the limit failed with %v", last.Err)
	}

	close(release)
	deadline := time.Now().Add(5 * time.Second)
	for len(slots) != 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if len(slots) != 0 {
		t.Fatalf("%d slots are still held after the calls returned", len(slots))
	}
}

func TestCallBoundedHoldsASlotUntilTheCallReturnsAndRecoversAPanic(t *testing.T) {
	slots := make(chan struct{}, 2)
	release := make(chan struct{})
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	for range 2 {
		_, err := callBounded(ctx, slots, func(context.Context) (int, error) { <-release; return 1, nil })
		if !errors.Is(err, context.DeadlineExceeded) {
			t.Fatalf("a hung call answered %v", err)
		}
	}
	ran := false
	_, err := callBounded(context.Background(), slots, func(context.Context) (int, error) { ran = true; return 1, nil })
	if !errors.Is(err, errTooManyVectorCalls) || ran {
		t.Fatalf("a call past the limit ran (%v, %v)", ran, err)
	}
	close(release)
	for len(slots) != 0 {
		time.Sleep(time.Millisecond)
	}
	if _, err := callBounded(context.Background(), slots, func(context.Context) (int, error) { panic("boom") }); err == nil ||
		errorName(err) != "github.com/fabbrik/AgentExperience.NET/go/maf.panicError" {
		t.Fatalf("a panic answered %v", err)
	}
	if value, err := callBounded(context.Background(), slots, func(context.Context) (int, error) { return 7, nil }); err != nil ||
		value != 7 {
		t.Fatalf("%v, %v", value, err)
	}
}

// ---- The background write after a record is stored --------------------------------------------------------------

func settingsOf(config *agent.Config) *settings {
	for _, middleware := range config.Middlewares {
		if capture, ok := middleware.(*captureMiddleware); ok {
			return capture.settings
		}
	}
	return nil
}

// storing is an agent that stores a lesson from its one passing tool call.
func storing(t *testing.T, v *vhost, store ae.Store, o Options) (*agent.Agent, *agent.Config) {
	t.Helper()
	o.Store = store
	o.Verify = ae.ToolSucceeded("run_tests")
	o.ClassifyToolResult = ae.ExitCode("run_tests")
	config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=0")}}
	if err := UseAgentExperience(&config, v.options(o)); err != nil {
		t.Fatal(err)
	}
	return newAgent(newScripted(1), config), &config
}

func waitForIndex(t *testing.T, config *agent.Config) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := WaitForIndexWrites(ctx, config); err != nil {
		t.Fatal(err)
	}
}

func TestAStoredRecordIsEmbeddedInTheBackgroundFromItsSummary(t *testing.T) {
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	v, embedder := &vhost{host: newHost(t)}, newFixed()
	a, config := storing(t, v, store, Options{Embedder: embedder})
	runText(t, a, ticket)
	waitForIndex(t, config)

	records := store.Records()
	if len(records) != 1 || len(v.captureFailures) != 0 {
		t.Fatalf("%d records, failures %+v", len(records), v.captureFailures)
	}
	record := records[0]
	summary := ae.RecordSummary(&record)
	if summary != ae.RetrievalSummary(&record.TaskID, record.TaskSummary, &record.Reflection.Lesson) {
		t.Fatal("the summary is not the task ID, task summary and lesson")
	}
	if texts := embedder.seen(); texts[len(texts)-1] != summary { // after the query's own embedding
		t.Fatalf("the embedder was given %q, not the summary %q", texts[len(texts)-1], summary)
	}
	stored, ok := store.Embedding(record.ExperienceID)
	if !ok || stored.ModelID != vectorModel || stored.Dimension != 4 || stored.SourceRevision != record.Revision ||
		stored.ContentHash != ae.ContentHash(vectorModel, summary) || len(stored.Vector) != 4 || stored.Vector[0] != 1 {
		t.Fatalf("%+v (%v)", stored, ok)
	}
}

func TestAFailedEmbeddingWriteIsReportedAsIndexAndTheRecordStaysStored(t *testing.T) {
	for name, tc := range map[string]struct {
		embedder func() *fixedEmbedder
		store    func(*inmemory.Store) ae.Store
	}{
		"raises":       {func() *fixedEmbedder { e := newFixed(); e.fail = true; return e }, func(s *inmemory.Store) ae.Store { return s }},
		"panics":       {func() *fixedEmbedder { e := newFixed(); e.panics = true; return e }, func(s *inmemory.Store) ae.Store { return s }},
		"wrong-length": {func() *fixedEmbedder { e := newFixed(); e.dimension = 9; return e }, func(s *inmemory.Store) ae.Store { return s }},
		"store-fails": {newFixed, func(s *inmemory.Store) ae.Store {
			return &vectorSpy{Store: s, writeErr: errors.New("the index is down: secret-record-content")}
		}},
	} {
		t.Run(name, func(t *testing.T) {
			inner, err := inmemory.New()
			if err != nil {
				t.Fatal(err)
			}
			v := &vhost{host: newHost(t)}
			a, config := storing(t, v, tc.store(inner), Options{Embedder: tc.embedder()})
			runText(t, a, ticket)
			waitForIndex(t, config)
			records := inner.Records()
			if len(records) != 1 {
				t.Fatalf("%d records", len(records))
			}
			if stages := v.stages(); len(stages) != 1 || stages[0] != StageIndex {
				t.Fatalf("stages %v", stages)
			}
			if strings.Contains(v.captureFailures[0].Reason, "secret-record-content") {
				t.Fatalf("the reason quotes content: %q", v.captureFailures[0].Reason)
			}
			if _, ok := inner.Embedding(records[0].ExperienceID); ok {
				t.Fatal("an embedding was stored")
			}
		})
	}
}

func TestNoEmbeddingIsWrittenWithoutAnEmbedderOrAStoreThatTakesIt(t *testing.T) {
	store, _ := inmemory.New()
	v := &vhost{host: newHost(t)}
	a, config := storing(t, v, store, Options{})
	runText(t, a, ticket)
	waitForIndex(t, config)
	if _, ok := store.Embedding(store.Records()[0].ExperienceID); ok {
		t.Fatal("an embedding was written without an embedder")
	}

	inner, _ := inmemory.New()
	embedder := newFixed()
	v = &vhost{host: newHost(t)}
	a, config = storing(t, v, textOnlyStore{inner}, Options{Embedder: embedder})
	runText(t, a, ticket)
	waitForIndex(t, config)
	if len(inner.Records()) != 1 || embedder.called() != 0 || len(v.captureFailures) != 0 {
		t.Fatalf("%d records, %d embeds, failures %+v", len(inner.Records()), embedder.called(), v.captureFailures)
	}
}

func TestCloseAwaitsTheBackgroundEmbeddingWrites(t *testing.T) {
	store, _ := inmemory.New()
	embedder := newFixed()
	embedder.delay = 150 * time.Millisecond
	v := &vhost{host: newHost(t)}
	a, config := storing(t, v, store, Options{Embedder: embedder, VectorTimeout: time.Second})
	runText(t, a, ticket)
	closeRecordings(t, config) // ContextProviderOf(config).Close
	if _, ok := store.Embedding(store.Records()[0].ExperienceID); !ok {
		t.Fatal("Close returned before the embedding was written")
	}
}

func TestALessonStoredAndEmbeddedIsFoundByARewordedQueryLater(t *testing.T) {
	store, _ := inmemory.New()
	floor := 0.5
	v := &vhost{host: newHost(t)}
	a, config := storing(t, v, store, Options{Embedder: newFixed(), MinVectorRelevance: &floor})
	runText(t, a, ticket)
	waitForIndex(t, config)

	later := &vhost{host: newHost(t)}
	b, _, model := wire(t, later, store, Options{Embedder: newFixed(), Identity: ae.Identity{Principal: "go-principal",
		Scope: vectorScope}})
	runText(t, b, "Zebra giraffe antelope")
	if len(later.injections) != 1 || len(later.reasons()) != 0 || len(model.blocks()) != 1 {
		t.Fatalf("injections %+v, fallbacks %v", later.injections, later.reasons())
	}
}

func floatPtr(value float64) *float64 { return &value }
