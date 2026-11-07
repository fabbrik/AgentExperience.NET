package maf

import (
	"context"
	"errors"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

// Story 27.3: measuring another memory provider (contract 1.11), mirroring .NET's ExperienceHoldoutContextProvider
// tests. A stub provider stands in for Mem0 or Zep.

const (
	memoryKey  = "stub-memory"
	memoryText = "MEMORY-7f3a: the lock is held by the nightly job."
)

var errMemoryDown = errors.New("the memory service is down")

// stubMemory adds one message (or an instruction, a tool, or nothing), counts both hooks, and may fail or replace the
// slices it was handed in place.
type stubMemory struct {
	adds    string
	fails   bool
	scribes bool
	tool    tool.Tool

	before, after atomic.Int32
}

func (m *stubMemory) Invoking(_ context.Context, invoking agent.InvokingContext) ([]*message.Message, []agent.Option,
	error) {
	m.before.Add(1)
	if m.fails {
		return nil, nil, errMemoryDown
	}
	messages, options := invoking.Messages, invoking.Options
	if m.scribes && len(messages) > 0 {
		// A provider that ignores the read-only rule and writes into the slice it was handed.
		messages[0] = message.NewText("scribbled")
	}
	switch m.adds {
	case "message":
		messages = append(messages, message.NewText(memoryText))
	case "instruction":
		options = append(options, agent.WithInstructions(memoryText))
	case "tool":
		options = append(options, agent.WithTool(m.tool))
	}
	return messages, options, nil
}

func (m *stubMemory) Invoked(context.Context, agent.InvokedContext) error {
	m.after.Add(1)
	return nil
}

// providerRecordings collects what OnRecorded is told.
type providerRecordings struct {
	mu      sync.Mutex
	results []ProviderHoldoutRecordResult
}

func (r *providerRecordings) add(result ProviderHoldoutRecordResult) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.results = append(r.results, result)
}

func (r *providerRecordings) all() []ProviderHoldoutRecordResult {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]ProviderHoldoutRecordResult(nil), r.results...)
}

type measuredSetup struct {
	agent    *agent.Agent
	model    *scripted
	wrapper  *MeasuredMemory
	memory   *stubMemory
	ids      *runIDs
	recorded *providerRecordings
	host     *host
}

// measuredAgent is a capturing agent (no injection) whose memory is wrapped under memoryKey at rate.
func measuredAgent(t *testing.T, memory *stubMemory, store ae.Store, rate float64, configure ...func(*Options)) *measuredSetup {
	t.Helper()
	h := newHost(t)
	if store == nil {
		store = h.store
	}
	ids, recorded := &runIDs{}, &providerRecordings{}
	options := h.options(Options{Store: store, Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: ae.ExitCode("run_tests"), NewID: ids.newID, DisableInjection: true})
	for _, c := range configure {
		c(&options)
	}
	config := &agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=0")},
		ContextProviders: []agent.ContextProvider{memory}}
	if err := UseAgentExperience(config, options); err != nil {
		t.Fatal(err)
	}
	holdout := HoldoutOptions{Rate: rate}
	wrapper, err := UseMeasuredMemory(config, memory, memoryKey,
		MeasuredMemoryOptions{Holdout: &holdout, OnRecorded: recorded.add})
	if err != nil {
		t.Fatal(err)
	}
	if len(config.ContextProviders) != 1 || config.ContextProviders[0] != wrapper {
		t.Fatalf("the memory was not replaced in place: %+v", config.ContextProviders)
	}
	model := newScripted(1)
	return &measuredSetup{agent: newAgent(model, *config), model: model, wrapper: wrapper, memory: memory, ids: ids,
		recorded: recorded, host: h}
}

func (s *measuredSetup) run(t *testing.T, run ae.UUID) {
	t.Helper()
	s.ids.next(run)
	runText(t, s.agent, ticket)
	s.settle(t)
}

func (s *measuredSetup) settle(t *testing.T) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := s.wrapper.Close(ctx); err != nil {
		t.Fatal(err)
	}
}

// memorySeen reports, for each run's first request, whether the model was shown the memory's message.
func (s *measuredSetup) memorySeen() []bool {
	s.model.mu.Lock()
	defer s.model.mu.Unlock()
	var seen []bool
	for _, request := range s.model.requests {
		first := true
		for _, m := range request {
			for _, content := range m.Contents {
				if _, ok := content.(*message.FunctionResultContent); ok {
					first = false
				}
			}
		}
		if !first {
			continue
		}
		found := false
		for _, m := range request {
			if strings.Contains(m.String(), memoryText) {
				found = true
			}
		}
		seen = append(seen, found)
	}
	return seen
}

func hooks(m *stubMemory) [2]int32 { return [2]int32{m.before.Load(), m.after.Load()} }

func providerOutcomes(o *observed) []string {
	var outcomes []string
	for _, observation := range o.of("record_provider_holdout") {
		outcomes = append(outcomes, observation.Outcome+"/"+observation.ErrorClass)
	}
	return outcomes
}

// At rate 0.5 under memoryKey and the default salt: the runs the tests use.
var (
	providerHeldOut = ae.MustParseUUID("60b0b000-0000-4000-8000-000000000001")
	providerTreated = ae.MustParseUUID("60b0b000-0000-4000-8000-000000000003")
)

func TestTheProviderRunsAreInTheArmsTheTestsAssume(t *testing.T) {
	if ae.AssignProviderHoldout(providerHeldOut, memoryKey, ae.DefaultHoldoutSalt, 0.5) != ae.HoldoutWithheld ||
		ae.AssignProviderHoldout(providerTreated, memoryKey, ae.DefaultHoldoutSalt, 0.5) != ae.HoldoutTreatment {
		t.Fatal("the test's runs are not in the arms it assumes")
	}
}

func TestATreatedRunGetsTheMemoryAndIsRecordedAsTreatment(t *testing.T) {
	o := observe(t)
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 0.5)
	s.run(t, providerTreated)
	rows := s.host.store.ProviderHoldoutRows()
	if got := s.memorySeen(); len(got) != 1 || !got[0] {
		t.Fatalf("memory seen %v", got)
	}
	if hooks(s.memory) != [2]int32{1, 1} {
		t.Fatalf("hooks %v", hooks(s.memory))
	}
	if len(rows) != 1 || rows[0].RunID != providerTreated || rows[0].Arm != ae.HoldoutTreatment ||
		rows[0].ProviderKey != memoryKey || rows[0].Scope != testScope {
		t.Fatalf("rows %+v", rows)
	}
	results := s.recorded.all()
	if len(results) != 1 || results[0].Outcome != InjectionRecorded || results[0].Assignment.Arm != ae.HoldoutTreatment {
		t.Fatalf("results %+v", results)
	}
	if got := providerOutcomes(o); len(got) != 1 || got[0] != "Recorded/" {
		t.Fatalf("telemetry %v", got)
	}
}

func TestAHeldOutRunSeesNoneOfTheMemoryYetTheMemoryRunsBothHooksAndTheArmIsRecorded(t *testing.T) {
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 0.5)
	s.run(t, providerHeldOut)
	if got := s.memorySeen(); len(got) != 1 || got[0] {
		t.Fatalf("memory seen %v", got)
	}
	if hooks(s.memory) != [2]int32{1, 1} {
		t.Fatalf("hooks %v", hooks(s.memory))
	}
	rows := s.host.store.ProviderHoldoutRows()
	if len(rows) != 1 || rows[0].Arm != ae.HoldoutWithheld {
		t.Fatalf("rows %+v", rows)
	}
}

func TestAnInstructionOrAToolCountsAsAnAdditionAndIsWithheldToo(t *testing.T) {
	for _, adds := range []string{"instruction", "tool"} {
		t.Run(adds, func(t *testing.T) {
			memory := &stubMemory{adds: adds, tool: exits("recall")}
			s := measuredAgent(t, memory, nil, 1)
			s.run(t, providerTreated)
			if rows := s.host.store.ProviderHoldoutRows(); len(rows) != 1 || rows[0].Arm != ae.HoldoutWithheld {
				t.Fatalf("rows %+v", rows)
			}
		})
	}
}

func TestAtRateZeroTheWrapperIsAPassThrough(t *testing.T) {
	o := observe(t)
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 0)
	s.run(t, providerHeldOut)
	if got := s.memorySeen(); len(got) != 1 || !got[0] {
		t.Fatalf("memory seen %v", got)
	}
	if hooks(s.memory) != [2]int32{1, 1} || len(s.host.store.ProviderHoldoutRows()) != 0 ||
		len(s.recorded.all()) != 0 || len(providerOutcomes(o)) != 0 {
		t.Fatalf("hooks %v, rows %+v, results %+v", hooks(s.memory), s.host.store.ProviderHoldoutRows(), s.recorded.all())
	}
}

func TestAnEmptyAnswerIsPassedThroughAndNotRecorded(t *testing.T) {
	s := measuredAgent(t, &stubMemory{}, nil, 1)
	s.run(t, providerHeldOut)
	if hooks(s.memory) != [2]int32{1, 1} || len(s.host.store.ProviderHoldoutRows()) != 0 || len(s.recorded.all()) != 0 {
		t.Fatalf("hooks %v, rows %+v", hooks(s.memory), s.host.store.ProviderHoldoutRows())
	}
}

func TestAnUncapturedRunIsPassedThroughAndNotRecorded(t *testing.T) {
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 1, func(o *Options) {
		o.Identity = ae.Identity{}
		o.ResolveIdentity = func(context.Context, IdentityContext) (*ae.Identity, error) { return nil, nil }
	})
	s.run(t, providerHeldOut)
	if got := s.memorySeen(); len(got) != 1 || !got[0] {
		t.Fatalf("memory seen %v", got)
	}
	if hooks(s.memory) != [2]int32{1, 1} || len(s.host.store.ProviderHoldoutRows()) != 0 || len(s.recorded.all()) != 0 {
		t.Fatalf("hooks %v, rows %+v", hooks(s.memory), s.host.store.ProviderHoldoutRows())
	}
}

func TestAMemoryThatFailsFailsTheSameWayAndNothingIsRecorded(t *testing.T) {
	s := measuredAgent(t, &stubMemory{fails: true}, nil, 1)
	s.ids.next(providerHeldOut)
	_, err := s.agent.RunText(context.Background(), ticket).Collect()
	s.settle(t)
	if !errors.Is(err, errMemoryDown) {
		t.Fatalf("error %v", err)
	}
	if len(s.host.store.ProviderHoldoutRows()) != 0 || len(s.recorded.all()) != 0 {
		t.Fatalf("rows %+v", s.host.store.ProviderHoldoutRows())
	}
}

// failingProviderRecorder records nothing: every provider holdout write fails.
type failingProviderRecorder struct {
	*inmemory.Store
	err error
}

func (f failingProviderRecorder) RecordProviderHoldoutAssignment(context.Context, ae.AuthorizationBounds, ae.Scope,
	ae.ProviderHoldoutAssignment) error {
	return f.err
}

func TestARecordingFailureLeavesTheInvocationUnaffected(t *testing.T) {
	for _, c := range []struct {
		name, want string
		err        error
	}{
		{"unexpected", "NotRecorded/Unexpected", errors.New("the grant is missing")},
		{"refused", "NotRecorded/", ae.ErrAuthorizationDenied},
	} {
		t.Run(c.name, func(t *testing.T) {
			o := observe(t)
			store, err := inmemory.New()
			if err != nil {
				t.Fatal(err)
			}
			s := measuredAgent(t, &stubMemory{adds: "message"}, failingProviderRecorder{store, c.err}, 1)
			s.run(t, providerHeldOut)
			if got := s.memorySeen(); len(got) != 1 || got[0] {
				t.Fatalf("memory seen %v", got)
			}
			results := s.recorded.all()
			if len(results) != 1 || results[0].Outcome != InjectionNotRecorded || !errors.Is(results[0].Err, c.err) {
				t.Fatalf("results %+v", results)
			}
			if got := providerOutcomes(o); len(got) != 1 || got[0] != c.want {
				t.Fatalf("telemetry %v", got)
			}
		})
	}
}

func TestAStoreThatDoesNotRecordProviderHoldoutIsNotTrackedAndStillWithholds(t *testing.T) {
	h := newHost(t)
	s := measuredAgent(t, &stubMemory{adds: "message"}, searchOnly{h.store}, 1)
	s.run(t, providerHeldOut)
	results := s.recorded.all()
	if got := s.memorySeen(); len(got) != 1 || got[0] {
		t.Fatalf("memory seen %v", got)
	}
	if len(results) != 1 || results[0].Outcome != InjectionNotTracked {
		t.Fatalf("results %+v", results)
	}
}

// slowRecorder blocks every write until released.
type slowRecorder struct {
	*inmemory.Store
	release chan struct{}
}

func (s slowRecorder) RecordProviderHoldoutAssignment(ctx context.Context, _ ae.AuthorizationBounds, _ ae.Scope,
	_ ae.ProviderHoldoutAssignment) error {
	<-s.release
	return nil
}

func TestARecordThatRunsOutOfTimeIsOutcomeUnknownAndTheWriteIsCountedUntilItEnds(t *testing.T) {
	o := observe(t)
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	slow := slowRecorder{store, make(chan struct{})}
	s := measuredAgent(t, &stubMemory{adds: "message"}, slow, 1, func(o *Options) {
		o.InjectionRecordTimeout = 20 * time.Millisecond
	})
	s.run(t, providerHeldOut)
	results := s.recorded.all()
	if len(results) != 1 || results[0].Outcome != InjectionOutcomeUnknown {
		t.Fatalf("results %+v", results)
	}
	if got := providerOutcomes(o); len(got) != 1 || got[0] != "OutcomeUnknown/Timeout" {
		t.Fatalf("telemetry %v", got)
	}
	if s.wrapper.AbandonedRecords() != 1 {
		t.Fatalf("abandoned %d", s.wrapper.AbandonedRecords())
	}
	close(slow.release)
	deadline := time.Now().Add(5 * time.Second)
	for s.wrapper.AbandonedRecords() != 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	if s.wrapper.AbandonedRecords() != 0 {
		t.Fatal("the abandoned write was never counted out")
	}
}

func TestAPanicWhileInspectingDeliversTheAnswerAndRecordsNothing(t *testing.T) {
	o := observe(t)
	var armed atomic.Bool
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 1, func(o *Options) {
		o.Clock = func() ae.Instant {
			if armed.Load() {
				panic("clock broke")
			}
			return ae.Now()
		}
	})
	// The clock is read by capture before the provider runs; arm it only for the wrapper's read.
	s.wrapper.inner = &armingMemory{stubMemory: s.memory, armed: &armed}
	s.run(t, providerHeldOut)
	armed.Store(false)
	if got := s.memorySeen(); len(got) != 1 || !got[0] {
		t.Fatalf("memory seen %v", got)
	}
	results := s.recorded.all()
	if len(results) != 1 || results[0].Outcome != InjectionNotRecorded || results[0].Assignment != nil {
		t.Fatalf("results %+v", results)
	}
	if len(s.host.store.ProviderHoldoutRows()) != 0 {
		t.Fatalf("rows %+v", s.host.store.ProviderHoldoutRows())
	}
	if got := providerOutcomes(o); len(got) != 1 || got[0] != "NotRecorded/Unexpected" {
		t.Fatalf("telemetry %v", got)
	}
}

// armingMemory arms the clock to panic as soon as the stub has answered, and disarms it after the invocation.
type armingMemory struct {
	*stubMemory
	armed *atomic.Bool
}

func (a *armingMemory) Invoking(ctx context.Context, invoking agent.InvokingContext) ([]*message.Message,
	[]agent.Option, error) {
	messages, options, err := a.stubMemory.Invoking(ctx, invoking)
	a.armed.Store(true)
	return messages, options, err
}

func (a *armingMemory) Invoked(ctx context.Context, invoked agent.InvokedContext) error {
	a.armed.Store(false)
	return a.stubMemory.Invoked(ctx, invoked)
}

func TestTheInvocationsOwnSlicesAreNeverChangedAndAHeldOutRunGetsExactlyThem(t *testing.T) {
	h := newHost(t)
	memory := &stubMemory{adds: "message", scribes: true}
	s := measuredAgent(t, memory, h.store, 1)
	handed := message.NewText(ticket)
	messages := []*message.Message{handed}
	options := []agent.Option{agent.WithInstructions("be terse")}
	scope := &captureScope{settings: s.wrapper.settings, identity: &ae.Identity{Principal: "svc", Scope: testScope},
		reported: map[CaptureStage]bool{}}
	run := providerHeldOut
	scope.capture, scope.runID = &ae.RunCapture{}, &run
	ctx := context.WithValue(context.Background(), scopeKey{}, scope)
	gotMessages, gotOptions, err := s.wrapper.Invoking(ctx, agent.InvokingContext{Messages: messages, Options: options})
	s.settle(t)
	if err != nil {
		t.Fatal(err)
	}
	if len(messages) != 1 || messages[0] != handed || len(options) != 1 {
		t.Fatalf("the invocation's slices changed: %v %v", messages, options)
	}
	if len(gotMessages) != 1 || gotMessages[0] != handed || len(gotOptions) != 1 {
		t.Fatalf("a held-out invocation got %v %v", gotMessages, gotOptions)
	}
	if rows := h.store.ProviderHoldoutRows(); len(rows) != 1 || rows[0].Arm != ae.HoldoutWithheld {
		t.Fatalf("rows %+v", rows)
	}
}

func TestUseMeasuredMemoryRefusesWhatItCannotMeasure(t *testing.T) {
	memory := &stubMemory{adds: "message"}
	if _, err := UseMeasuredMemory(&agent.Config{}, memory, memoryKey, MeasuredMemoryOptions{}); err == nil {
		t.Fatal("no UseAgentExperience first, yet accepted")
	}
	h := newHost(t)
	config := &agent.Config{}
	if err := UseAgentExperience(config, h.options(Options{})); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"", "a/b", "mem 0", strings.Repeat("k", 101), "mém0"} {
		if _, err := UseMeasuredMemory(config, memory, key, MeasuredMemoryOptions{}); err == nil {
			t.Fatalf("key %q accepted", key)
		}
	}
	for _, rate := range []float64{-0.1, 1.1} {
		if _, err := UseMeasuredMemory(config, memory, memoryKey,
			MeasuredMemoryOptions{Holdout: &HoldoutOptions{Rate: rate}}); err == nil {
			t.Fatalf("rate %v accepted", rate)
		}
	}
	if _, err := UseMeasuredMemory(config, nil, memoryKey, MeasuredMemoryOptions{}); err == nil {
		t.Fatal("a nil memory accepted")
	}
	// Without a holdout of its own it takes the injection holdout's (off), and appends itself.
	wrapper, err := UseMeasuredMemory(config, memory, memoryKey, MeasuredMemoryOptions{})
	if err != nil || wrapper.Holdout() != (HoldoutOptions{Salt: ae.DefaultHoldoutSalt}) ||
		config.ContextProviders[len(config.ContextProviders)-1] != wrapper || wrapper.Inner() != memory ||
		wrapper.ProviderKey() != memoryKey {
		t.Fatalf("%v %+v", err, wrapper)
	}
}

func TestAtRateOneHalfEachFinalizedRunIsCountedOnceInItsArm(t *testing.T) {
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 0.5)
	const runs = 24
	treatment := 0
	for n := 1; n <= runs; n++ {
		run := ae.MustParseUUID("60b0e000-0000-4000-8000-" + strings.Repeat("0", 10) + twoDigits(n))
		s.run(t, run)
		if ae.AssignProviderHoldout(run, memoryKey, ae.DefaultHoldoutSalt, 0.5) == ae.HoldoutTreatment {
			treatment++
		}
	}
	if treatment == 0 || treatment == runs {
		t.Fatalf("%d of %d in treatment", treatment, runs)
	}
	estimate, err := ae.EstimateProviderBenefit(context.Background(), s.host.store,
		ae.Identity{Principal: "svc", Scope: testScope}.Authorization(), testScope, memoryKey, ae.BenefitOptions{})
	if err != nil {
		t.Fatal(err)
	}
	overall := estimate.Overall
	if estimate.Outcome != "Found" || !estimate.Tracked || overall.Treatment.Runs != treatment ||
		overall.Holdout.Runs != runs-treatment || overall.Treatment.Successes != treatment ||
		overall.Pending != 0 || overall.MixedArm != 0 || overall.Unverified != 0 {
		t.Fatalf("estimate %+v", estimate)
	}
}

func twoDigits(n int) string { return string(rune('0'+n/10)) + string(rune('0'+n%10)) }

// funcTool is a tool value that is not comparable (it holds a func), as a hand-written tool may be.
type funcTool struct {
	name string
	call func() string
}

func (f funcTool) Name() string        { return f.name }
func (f funcTool) Description() string { return "Recalls what the memory holds." }

func TestAToolIsComparedByIdentityAndAFuncBearingToolNeverPanics(t *testing.T) {
	handed := tool.Tool(funcTool{name: "recall", call: func() string { return "x" }})
	equal := tool.Tool(funcTool{name: "recall"})
	invoking := agent.InvokingContext{Options: []agent.Option{agent.WithTool(handed)}}
	// The very tool it was handed, copied into a fresh slice as a provider would: not an addition.
	if added(invoking, nil, slices.Clone(invoking.Options)) {
		t.Fatal("the handed tool counted as added")
	}
	// A different tool, however equal its fields: an addition.
	if !added(invoking, nil, append(slices.Clone(invoking.Options), agent.WithTool(equal))) {
		t.Fatal("a new tool did not count as added")
	}
	pointer, other := &stubMemory{}, &stubMemory{}
	if !same(pointer, pointer) || same(pointer, other) || same(handed, equal) || !same(handed, handed) {
		t.Fatal("same is not identity")
	}
}

func TestACallbackThatBlocksNeverDelaysTheInvocation(t *testing.T) {
	for _, c := range []struct {
		name  string
		store func(h *host) ae.Store
	}{
		{"not tracked", func(h *host) ae.Store { return searchOnly{h.store} }},
		{"recorded", func(h *host) ae.Store { return h.store }},
	} {
		t.Run(c.name, func(t *testing.T) {
			h := newHost(t)
			s := measuredAgent(t, &stubMemory{adds: "message"}, c.store(h), 1)
			gate, called := make(chan struct{}), make(chan struct{}, 1)
			s.wrapper.onRecorded = func(ProviderHoldoutRecordResult) {
				called <- struct{}{}
				<-gate
			}
			done := make(chan struct{})
			go func() {
				defer close(done)
				s.ids.next(providerHeldOut)
				_, _ = s.agent.RunText(context.Background(), ticket).Collect()
			}()
			select {
			case <-done:
			case <-time.After(5 * time.Second):
				close(gate)
				t.Fatal("the invocation waited for the callback")
			}
			<-called
			close(gate)
			s.settle(t)
		})
	}
}

func TestACapturedRunAlwaysCarriesAnIdentityAndOneWithoutIsReportedNotRecorded(t *testing.T) {
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 1)
	// Capture starts a run only for an invocation with an identity.
	without := &captureScope{settings: s.wrapper.settings, reported: map[CaptureStage]bool{}}
	without.start(context.Background(), nil)
	if _, ok := without.run(); ok {
		t.Fatal("a run started with no identity")
	}
	with := &captureScope{settings: s.wrapper.settings, identity: &ae.Identity{Principal: "svc", Scope: testScope},
		reported: map[CaptureStage]bool{}}
	with.start(context.Background(), nil)
	if _, ok := with.run(); !ok {
		t.Fatal("a run with an identity did not start")
	}
	// Were a run captured with none, it is reported rather than passed over in silence, and passed through.
	o := observe(t)
	run := providerHeldOut
	without.capture, without.runID = &ae.RunCapture{}, &run
	ctx := context.WithValue(context.Background(), scopeKey{}, without)
	messages, _, err := s.wrapper.Invoking(ctx, agent.InvokingContext{Messages: []*message.Message{message.NewText(ticket)}})
	s.settle(t)
	if err != nil || len(messages) != 2 {
		t.Fatalf("%v, %d messages", err, len(messages))
	}
	results := s.recorded.all()
	if len(results) != 1 || results[0].Outcome != InjectionNotRecorded || !errors.Is(results[0].Err, errNoIdentity) {
		t.Fatalf("results %+v", results)
	}
	if got := providerOutcomes(o); len(got) != 1 || got[0] != "NotRecorded/Unexpected" {
		t.Fatalf("telemetry %v", got)
	}
	if len(s.host.store.ProviderHoldoutRows()) != 0 {
		t.Fatalf("rows %+v", s.host.store.ProviderHoldoutRows())
	}
}

func TestTwoInvocationsOfOneRunRecordOneRowInOneArm(t *testing.T) {
	s := measuredAgent(t, &stubMemory{adds: "message"}, nil, 0.5)
	scope := &captureScope{settings: s.wrapper.settings, identity: &ae.Identity{Principal: "svc", Scope: testScope},
		reported: map[CaptureStage]bool{}}
	run := providerHeldOut
	scope.capture, scope.runID = &ae.RunCapture{}, &run
	ctx := context.WithValue(context.Background(), scopeKey{}, scope)
	for range 2 {
		messages, _, err := s.wrapper.Invoking(ctx, agent.InvokingContext{Messages: []*message.Message{message.NewText(ticket)}})
		if err != nil || len(messages) != 1 {
			t.Fatalf("%v, %d messages: not withheld", err, len(messages))
		}
	}
	s.settle(t)
	results := s.recorded.all()
	if len(results) != 2 || results[0].Assignment.Arm != ae.HoldoutWithheld || results[1].Assignment.Arm != ae.HoldoutWithheld {
		t.Fatalf("results %+v", results)
	}
	if rows := s.host.store.ProviderHoldoutRows(); len(rows) != 1 || rows[0].Arm != ae.HoldoutWithheld {
		t.Fatalf("rows %+v", rows)
	}
}

func TestTheContextProvidersCloseWaitsForTheWrappersRecords(t *testing.T) {
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	slow := slowRecorder{store, make(chan struct{})}
	h, ids, recorded, memory := newHost(t), &runIDs{}, &providerRecordings{}, &stubMemory{adds: "message"}
	config := &agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=0")},
		ContextProviders: []agent.ContextProvider{memory}}
	if err := UseAgentExperience(config, h.options(Options{Store: slow, NewID: ids.newID})); err != nil {
		t.Fatal(err)
	}
	if _, err := UseMeasuredMemory(config, memory, memoryKey,
		MeasuredMemoryOptions{Holdout: &HoldoutOptions{Rate: 1}, OnRecorded: recorded.add}); err != nil {
		t.Fatal(err)
	}
	ids.next(providerHeldOut)
	runText(t, newAgent(newScripted(1), *config), ticket)
	go func() {
		time.Sleep(50 * time.Millisecond)
		close(slow.release)
	}()
	closeRecordings(t, config)
	if results := recorded.all(); len(results) != 1 || results[0].Outcome != InjectionRecorded {
		t.Fatalf("Close returned before the wrapper's record ended: %+v", results)
	}
}
