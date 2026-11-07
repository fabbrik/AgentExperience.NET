package maf

// Contract 1.10: acting on the benefit estimate. The estimate is cached per scope and principal and refreshed in a
// goroutine, never on the request path; a lesson measured Hurts is omitted as MeasuredHarmful.

import (
	"context"
	"errors"
	"fmt"
	"net"
	"reflect"
	"slices"
	"sync"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

// outcomeStore is the in-memory store whose holdout read answers scripted rows, or an error, or blocks until released.
type outcomeStore struct {
	*inmemory.Store
	mu      sync.Mutex
	rows    []ae.HoldoutOutcomeRow
	err     error
	block   chan struct{}
	queries int
}

func (s *outcomeStore) QueryHoldoutOutcomes(ctx context.Context, _ ae.AuthorizationBounds, _ ae.Scope, _ int,
	_ *ae.HoldoutOutcomeCursor) ([]ae.HoldoutOutcomeRow, error) {
	s.mu.Lock()
	s.queries++
	rows, err, block := slices.Clone(s.rows), s.err, s.block
	s.mu.Unlock()
	if block != nil {
		<-block
	}
	return rows, err
}

func (s *outcomeStore) set(rows []ae.HoldoutOutcomeRow, err error, block chan struct{}) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rows, s.err, s.block = rows, err, block
}

func (s *outcomeStore) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.queries
}

// movingClock is a clock a test moves on.
type movingClock struct {
	mu  sync.Mutex
	now ae.Instant
}

func newMovingClock() *movingClock {
	return &movingClock{now: ae.MustParseInstant("2026-03-02T10:00:00+00:00")}
}

func (c *movingClock) read() ae.Instant { c.mu.Lock(); defer c.mu.Unlock(); return c.now }

func (c *movingClock) advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = later(c.now, d)
}

// later is at plus d.
func later(at ae.Instant, d time.Duration) ae.Instant {
	next, err := at.Add(ae.DurationFromTicks(int64(d / 100)))
	if err != nil {
		panic(err)
	}
	return next
}

// hurts are rows in which lesson hurts: three treatment runs given it failed, three holdout runs denied it verified,
// all assigned an hour before at; with balanced, as many runs of another lesson that measure the reverse, so the scope
// as a whole has no evidence.
func hurts(lesson ae.UUID, at ae.Instant, balanced bool) []ae.HoldoutOutcomeRow {
	verified, failed := ae.VerificationVerified, ae.VerificationFailed
	assigned := later(at, -time.Hour)
	var rows []ae.HoldoutOutcomeRow
	add := func(run byte, experience ae.UUID, arm ae.HoldoutArm, outcome *ae.VerificationStatus) {
		rows = append(rows, ae.HoldoutOutcomeRow{RunID: ae.UUID{0: 0xbe, 15: run}, ExperienceID: experience,
			Arm: arm, AssignedAt: assigned, RunOutcome: outcome})
	}
	for i := byte(0); i < 3; i++ {
		add(1+i, lesson, ae.HoldoutTreatment, &failed)
		add(4+i, lesson, ae.HoldoutWithheld, &verified)
		if balanced {
			add(7+i, ae.UUID{0: 0xaa, 15: 1}, ae.HoldoutTreatment, &verified)
			add(10+i, ae.UUID{0: 0xaa, 15: 1}, ae.HoldoutWithheld, &failed)
		}
	}
	slices.SortFunc(rows, func(a, b ae.HoldoutOutcomeRow) int { return int(a.RunID[15]) - int(b.RunID[15]) })
	return rows
}

// policyAgent is a learning agent with the benefit policy on, over store, on clock.
func policyAgent(t *testing.T, h *host, store ae.Store, clock *movingClock, policy ae.BenefitPolicy,
	configure func(*Options)) (*agent.Agent, *agent.Config, *scripted) {
	t.Helper()
	config := &agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=3", "exit=0")}}
	options := h.options(Options{Store: store, Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: ae.ExitCode("run_tests"), Clock: clock.read, Benefit: policy})
	if configure != nil {
		configure(&options)
	}
	if err := UseAgentExperience(config, options); err != nil {
		t.Fatal(err)
	}
	model := newScripted(2)
	return newAgent(model, *config), config, model
}

func settle(t *testing.T, config *agent.Config) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := ContextProviderOf(config).WaitBenefitRefreshes(ctx); err != nil {
		t.Fatal(err)
	}
}

func (h *host) lastInjection(t *testing.T) InjectionResult {
	t.Helper()
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.injections) == 0 {
		t.Fatal("nothing was injected")
	}
	return h.injections[len(h.injections)-1]
}

// observed collects what the observer is told, by operation.
type observed struct {
	mu   sync.Mutex
	seen []ae.Observation
}

func (o *observed) Start(ctx context.Context, _, _ string, _ bool) (context.Context, any) {
	return ctx, nil
}

func (o *observed) Finish(_ any, observation ae.Observation) {
	o.mu.Lock()
	defer o.mu.Unlock()
	o.seen = append(o.seen, observation)
}

func (o *observed) of(operation string) []ae.Observation {
	o.mu.Lock()
	defer o.mu.Unlock()
	var found []ae.Observation
	for _, observation := range o.seen {
		if observation.Operation == operation {
			found = append(found, observation)
		}
	}
	return found
}

func observe(t *testing.T) *observed {
	t.Helper()
	o := &observed{}
	ae.SetObserver(o)
	t.Cleanup(func() { ae.SetObserver(nil) })
	return o
}

// warm learns two lessons (the first two runs), then makes the store measure the first as hurting and lets the
// cache refresh: the next invocation acts on it.
func warm(t *testing.T, h *host, store *outcomeStore, clock *movingClock, a *agent.Agent, config *agent.Config) (
	harmful, kept ae.UUID) {
	t.Helper()
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	settle(t, config)
	if len(h.finalized) != 2 {
		t.Fatalf("%d lessons learned", len(h.finalized))
	}
	harmful, kept = h.finalized[0].Record.ExperienceID, h.finalized[1].Record.ExperienceID
	store.set(hurts(harmful, clock.read(), true), nil, nil)
	clock.advance(ae.DefaultBenefitRefreshInterval)
	runText(t, a, ticket) // starts the refresh, and is injected from the stale view
	settle(t, config)
	return harmful, kept
}

func TestTheColdCacheChangesNothingAndAWarmOneOmitsTheHarmfulLesson(t *testing.T) {
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: h.store}
	o := observe(t)
	a, config, _ := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3}, nil)
	harmful, kept := warm(t, h, store, clock, a, config)
	// Before the refresh that saw the rows, the harmful lesson was still injected.
	if first := h.injections[len(h.injections)-1]; !slices.Contains(first.ExperienceIDs, harmful) {
		t.Fatalf("the stale view omitted it already: %+v", first)
	}

	runText(t, a, nextTicket)
	result := h.lastInjection(t)
	if slices.Contains(result.ExperienceIDs, harmful) || !slices.Contains(result.ExperienceIDs, kept) {
		t.Fatalf("injected %v", result.ExperienceIDs)
	}
	var omission *ae.OmittedRecord
	for i := range result.Omitted {
		if result.Omitted[i].ExperienceID == harmful {
			omission = &result.Omitted[i]
		}
	}
	if omission == nil || omission.Reason != ae.OmittedMeasuredHarmful || omission.Detail == nil ||
		*omission.Detail != ae.MeasuredHarmfulDetail {
		t.Fatalf("omitted %+v", result.Omitted)
	}
	refreshes := o.of("benefit_refresh")
	if len(refreshes) != 2 {
		t.Fatalf("%d refreshes", len(refreshes))
	}
	for _, refresh := range refreshes {
		if refresh.Outcome != BenefitRefreshed || refresh.ErrorClass != "" || refresh.Nested ||
			refresh.Source != ae.SourceMAF {
			t.Fatalf("%+v", refresh)
		}
	}
	if len(h.injectionFailures) != 0 {
		t.Fatalf("%+v", h.injectionFailures)
	}
}

func TestTheWholeScopeMeasuredHurtingGetsNoBlock(t *testing.T) {
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: h.store}
	a, config, model := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3}, nil)
	runText(t, a, ticket)
	settle(t, config)
	lesson := h.finalized[0].Record.ExperienceID
	store.set(hurts(lesson, clock.read(), false), nil, nil) // the only lesson: the scope hurts too
	clock.advance(ae.DefaultBenefitRefreshInterval)
	runText(t, a, nextTicket)
	settle(t, config)
	blocks := len(model.blocks())
	injections := len(h.injections)
	if injections != 1 {
		t.Fatalf("the stale view's invocation: %d injections", injections)
	}
	runText(t, a, ticket)
	if len(model.blocks()) != blocks || len(h.injections) != injections {
		t.Fatal("a scope measured hurting was given a block")
	}
	// As .NET reports it: OnInjected is told NothingToInject, with every record omitted as MeasuredHarmful and the
	// scope's detail.
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.nothing) == 0 {
		t.Fatal("OnInjected was not told the scope's omissions")
	}
	last := h.nothing[len(h.nothing)-1]
	if last.Outcome != "NothingToInject" || len(last.Omitted) == 0 || len(last.ExperienceIDs) != 0 {
		t.Fatalf("%+v", last)
	}
	for _, omitted := range last.Omitted {
		if omitted.Reason != ae.OmittedMeasuredHarmful || omitted.Detail == nil || *omitted.Detail != ae.ScopeMeasuredHarmfulDetail {
			t.Fatalf("%+v", omitted)
		}
	}
}

func TestTheOnlyCandidateMeasuredHarmfulIsReportedWithNothingToInject(t *testing.T) {
	learned := lesson(t)
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: storeOf(t, learned)}
	// No verify: nothing is captured, so the seeded lesson stays the only candidate.
	a, config, model := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3},
		func(o *Options) { o.Verify = nil })
	store.set(hurts(learned.ExperienceID, clock.read(), true), nil, nil) // the lesson hurts; the scope has no evidence
	runText(t, a, nextTicket)                                            // the cold cache: injected as without the policy, and a refresh starts
	settle(t, config)
	blocks := len(model.blocks())
	runText(t, a, nextTicket)
	if len(model.blocks()) != blocks {
		t.Fatal("the harmful lesson was injected")
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.nothing) != 1 {
		t.Fatalf("OnInjected was told %d NothingToInject results", len(h.nothing))
	}
	last := h.nothing[0]
	if len(last.Omitted) != 1 || last.Omitted[0].ExperienceID != learned.ExperienceID ||
		last.Omitted[0].Reason != ae.OmittedMeasuredHarmful || last.Omitted[0].Detail == nil ||
		*last.Omitted[0].Detail != ae.MeasuredHarmfulDetail {
		t.Fatalf("%+v", last)
	}
}

func TestAFailedRefreshKeepsThePreviousViewAndWaitsOutTheInterval(t *testing.T) {
	for _, tc := range []struct {
		err     error
		outcome string
		class   string
	}{
		{&net.OpError{Op: "read", Net: "tcp", Err: errors.New("reset")}, BenefitNotRefreshed, ae.ErrorClassInfrastructure},
		{errors.New("unexpected"), BenefitNotRefreshed, ae.ErrorClassUnexpected},
		// A store that timed out on its own, well inside the refresh's bound: OutcomeUnknown, as .NET's TimeoutException.
		{fmt.Errorf("query: %w", context.DeadlineExceeded), BenefitOutcomeUnknown, ae.ErrorClassTimeout},
		{&net.OpError{Op: "read", Net: "tcp", Err: timeoutError{}}, BenefitOutcomeUnknown, ae.ErrorClassTimeout},
		{fmt.Errorf("query: %w", context.Canceled), BenefitOutcomeUnknown, ae.ErrorClassCancelled},
	} {
		h, clock := newHost(t), newMovingClock()
		store := &outcomeStore{Store: h.store}
		o := observe(t)
		a, config, _ := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3}, nil)
		harmful, _ := warm(t, h, store, clock, a, config)

		store.set(nil, tc.err, nil)
		clock.advance(ae.DefaultBenefitRefreshInterval)
		runText(t, a, ticket) // starts a refresh that fails
		settle(t, config)
		queries := store.count()
		runText(t, a, nextTicket) // no new refresh: the failure counts as refreshed
		settle(t, config)
		if store.count() != queries {
			t.Fatal("a failed refresh was retried before the interval")
		}
		if slices.Contains(h.lastInjection(t).ExperienceIDs, harmful) {
			t.Fatal("the previous view was not kept")
		}
		refreshes := o.of("benefit_refresh")
		last := refreshes[len(refreshes)-1]
		if last.Outcome != tc.outcome || last.ErrorClass != tc.class {
			t.Fatalf("%+v", last)
		}
		ae.SetObserver(nil)
	}
}

// timeoutError is a network error that timed out.
type timeoutError struct{}

func (timeoutError) Error() string   { return "i/o timeout" }
func (timeoutError) Timeout() bool   { return true }
func (timeoutError) Temporary() bool { return true }

func TestARefreshThatOverrunsItsTimeoutIsAbandonedAndOneRunsPerKey(t *testing.T) {
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: h.store}
	o := observe(t)
	// Warmed under the default timeout, so a slow runner cannot time the warm-up out; lowered for the overrun.
	a, config, _ := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3}, nil)
	harmful, _ := warm(t, h, store, clock, a, config)
	ContextProviderOf(config).settings.benefit.policy.RefreshTimeout = 50 * time.Millisecond // no refresh is running

	release := make(chan struct{})
	defer close(release)
	store.set(nil, nil, release)
	clock.advance(ae.DefaultBenefitRefreshInterval)
	queries := store.count()
	start := time.Now()
	runText(t, a, ticket)
	runText(t, a, nextTicket) // while the first refresh is running: no second one
	if elapsed := time.Since(start); elapsed > 5*time.Second {
		t.Fatalf("an invocation waited on the store: %v", elapsed)
	}
	settle(t, config)
	if store.count() != queries+1 {
		t.Fatalf("%d refreshes started for one key", store.count()-queries)
	}
	if slices.Contains(h.lastInjection(t).ExperienceIDs, harmful) {
		t.Fatal("the previous view was not kept")
	}
	refreshes := o.of("benefit_refresh")
	last := refreshes[len(refreshes)-1]
	if last.Outcome != BenefitOutcomeUnknown || last.ErrorClass != ae.ErrorClassTimeout {
		t.Fatalf("%+v", last)
	}
}

func TestOnlyAssignmentsWithinTheWindowCount(t *testing.T) {
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: h.store}
	a, config, _ := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3,
		Window: 30 * time.Minute}, nil)
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	settle(t, config)
	harmful := h.finalized[0].Record.ExperienceID
	store.set(hurts(harmful, clock.read(), true), nil, nil) // assigned an hour ago: outside a 30-minute window
	clock.advance(ae.DefaultBenefitRefreshInterval)
	runText(t, a, ticket)
	settle(t, config)
	runText(t, a, nextTicket)
	if !slices.Contains(h.lastInjection(t).ExperienceIDs, harmful) {
		t.Fatal("evidence older than the window was acted on")
	}
}

func TestTheHoldoutIsUnchangedByThePolicy(t *testing.T) {
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: h.store}
	held := &holdoutRecordings{}
	a, config, _ := policyAgent(t, h, store, clock, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3},
		func(o *Options) { o.OnWithheld = held.addWithheld; o.OnHoldoutRecorded = held.add })
	harmful, kept := warm(t, h, store, clock, a, config)
	// Now every run is held out: the block is still built, without the harmful lesson, and withheld.
	ContextProviderOf(config).settings.Holdout.Rate = 1
	runText(t, a, nextTicket)
	closeRecordings(t, config)
	_, withheld := held.all()
	if len(withheld) != 1 || slices.Contains(withheld[0].WithheldExperienceIDs, harmful) ||
		!slices.Contains(withheld[0].WithheldExperienceIDs, kept) {
		t.Fatalf("withheld %+v", withheld)
	}
	for _, row := range h.store.HoldoutRows() {
		if row.ExperienceID == harmful {
			t.Fatal("a lesson omitted as harmful was assigned")
		}
	}
}

func TestThePolicyIsValidatedEvenWhenOff(t *testing.T) {
	h := newHost(t)
	for _, policy := range []ae.BenefitPolicy{{Window: -1}, {RefreshInterval: -time.Second},
		{RefreshTimeout: 25 * time.Hour}, {MinimumRunsPerArm: -1}} {
		config := agent.Config{Name: "triage-ticket"}
		if err := UseAgentExperience(&config, h.options(Options{Benefit: policy})); err == nil {
			t.Errorf("%+v was accepted", policy)
		}
	}
	config := agent.Config{Name: "triage-ticket"}
	if err := UseAgentExperience(&config, h.options(Options{})); err != nil ||
		ContextProviderOf(&config).settings.benefit != nil {
		t.Fatalf("off: %v", err)
	}
}

func TestTheInvokingModelIsTheRecordedOneWhenCapturedAndTheClientsOtherwise(t *testing.T) {
	h := newHost(t)
	model := func(options Options, captured bool) *string {
		s, err := newSettings(h.options(options))
		if err != nil {
			t.Fatal(err)
		}
		return s.invokingModel(captured)
	}
	if got := model(Options{Model: "  gpt-x "}, true); got == nil || *got != "gpt-x" {
		t.Fatalf("captured: %v", got)
	}
	if got := model(Options{Model: "  gpt-x "}, false); got == nil || *got != "gpt-x" {
		t.Fatalf("uncaptured: %v", got)
	}
	environment := DefaultEnvironment()
	environment.Metadata[ae.ModelMetadataKey] = "host-deployment"
	if got := model(Options{Model: "gpt-x", Environment: &environment}, true); got == nil || *got != "host-deployment" {
		t.Fatalf("the host's value: %v", got)
	}
	if got := model(Options{Model: "gpt-x", Environment: &environment}, false); got == nil || *got != "gpt-x" {
		t.Fatalf("uncaptured, the client's: %v", got)
	}
	if got := model(Options{}, false); got != nil {
		t.Fatalf("no model: %v", *got)
	}
}

func TestPerModelActsOnTheInvokingModelsVerdicts(t *testing.T) {
	cache := newBenefitCache(nil, ae.BenefitPolicy{Enabled: true, PerModel: true}, ae.Now)
	lesson := ae.UUID{15: 7}
	pair := func(runModel string, treatment, holdout int) ae.ModelPairBenefit {
		return ae.ModelPairBenefit{LessonModel: "m", RunModel: runModel, Comparison: ae.BenefitComparison{
			Treatment: ae.BenefitArm{Runs: 3, Successes: treatment}, Holdout: ae.BenefitArm{Runs: 3, Successes: holdout}}}
	}
	estimate := ae.BenefitEstimate{Tracked: true, Outcome: ae.BenefitFound, MinimumRunsPerArm: 3,
		Lessons: []ae.LessonBenefit{{ExperienceID: lesson, Comparison: ae.BenefitComparison{Verdict: ae.NoEvidence},
			ByModelPair: []ae.ModelPairBenefit{pair("model-a", 0, 3), pair("model-b", 3, 0)}}},
		ByConsumingModel: []ae.ModelBenefit{{Model: "model-a"}, {Model: "model-b"}}}
	identity := &ae.Identity{Principal: "svc", Scope: testScope}
	cache.entries[keyOf(identity)] = &benefitEntry{views: ae.NewBenefitViews(estimate), refreshed: true,
		refreshedAt: ae.Now()}
	a, b := "model-a", "model-b"
	if view := cache.current(context.Background(), identity, &a); !view.Harmful(lesson) {
		t.Fatal("model-a's runs hurt")
	}
	if view := cache.current(context.Background(), identity, &b); view.Harmful(lesson) || !view.Helps[lesson] {
		t.Fatal("model-b's runs helped")
	}
	if view := cache.current(context.Background(), identity, nil); view != nil {
		t.Fatalf("no model: the pooled view, which changes nothing: %+v", view)
	}
	pooled := newBenefitCache(nil, ae.BenefitPolicy{Enabled: true}, ae.Now)
	pooled.entries[keyOf(identity)] = cache.entries[keyOf(identity)]
	if view := pooled.current(context.Background(), identity, &a); view != nil {
		t.Fatal("PerModel off acts on the pooled verdicts whatever the model")
	}
	if !reflect.DeepEqual(keyOf(identity), keyOf(&ae.Identity{Principal: "svc", Scope: testScope})) ||
		keyOf(identity) == keyOf(&ae.Identity{Principal: "other", Scope: testScope}) {
		t.Fatal("two principals share a key")
	}
}

func TestTheCacheKeepsAtMostItsLimitDroppingTheOldest(t *testing.T) {
	cache := newBenefitCache(nil, ae.BenefitPolicy{Enabled: true}, ae.Now)
	oldest := ae.MustParseInstant("2026-01-01T00:00:00+00:00")
	for i := range MaxBenefitCacheEntries {
		identity := &ae.Identity{Principal: string(rune('a'+i%26)) + string(rune(i)), Scope: testScope}
		at := later(oldest, time.Duration(i)*time.Second)
		cache.entries[keyOf(identity)] = &benefitEntry{refreshed: true, refreshedAt: at}
	}
	first := &ae.Identity{Principal: string(rune('a')) + string(rune(0)), Scope: testScope}
	cache.mu.Lock()
	cache.evict()
	cache.mu.Unlock()
	if _, ok := cache.entries[keyOf(first)]; ok || cache.size() != MaxBenefitCacheEntries-1 {
		t.Fatalf("%d entries; the oldest kept: %v", cache.size(), ok)
	}
}

func TestConcurrentReadsOfOneKeyStartOneRefresh(t *testing.T) {
	h, clock := newHost(t), newMovingClock()
	store := &outcomeStore{Store: h.store}
	release := make(chan struct{})
	store.set(nil, nil, release)
	cache := newBenefitCache(store, ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3}, clock.read)
	identity := &ae.Identity{Principal: "svc", Scope: testScope}
	var wg sync.WaitGroup
	for i := 0; i < 64; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if view := cache.current(context.Background(), identity, nil); view != nil {
				t.Error("a cold cache answered a view")
			}
		}()
	}
	wg.Wait()
	close(release)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := cache.refreshs.wait(ctx); err != nil {
		t.Fatal(err)
	}
	if store.count() != 1 {
		t.Fatalf("%d refreshes ran for one key", store.count())
	}
	// And concurrent invocations of one agent, through the provider.
	h2 := newHost(t)
	store2 := &outcomeStore{Store: h2.store}
	release2 := make(chan struct{})
	store2.set(nil, nil, release2)
	a, config, _ := policyAgent(t, h2, store2, newMovingClock(), ae.BenefitPolicy{Enabled: true, MinimumRunsPerArm: 3},
		func(o *Options) { o.Verify = nil })
	var runs sync.WaitGroup
	for i := 0; i < 8; i++ {
		runs.Add(1)
		go func() { defer runs.Done(); runText(t, a, ticket) }()
	}
	runs.Wait()
	close(release2)
	settle(t, config)
	if store2.count() != 1 {
		t.Fatalf("%d refreshes ran for one key across concurrent invocations", store2.count())
	}
}
