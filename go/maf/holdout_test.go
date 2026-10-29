package maf

// Contract 1.8: the injection holdout. On a deterministic share of captured runs whose block names a record, the block
// is withheld; both arms are recorded in the background.

import (
	"context"
	"math"
	"reflect"
	"strings"
	"sync"
	"testing"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// At rate 0.5 with the default salt, these runs are held out and treated.
var (
	heldOutRun = ae.MustParseUUID("60a0a000-0000-4000-8000-000000000004")
	treatedRun = ae.MustParseUUID("60a0a000-0000-4000-8000-000000000001")
)

// runIDs is a NewID whose first ID after next(run) is run, the ID capture starts an invocation's run with (the first
// ID an invocation asks for); every other ID is random.
type runIDs struct {
	mu      sync.Mutex
	pending *ae.UUID
}

func (r *runIDs) next(run ae.UUID) { r.mu.Lock(); r.pending = &run; r.mu.Unlock() }

func (r *runIDs) newID() ae.UUID {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.pending != nil {
		id := *r.pending
		r.pending = nil
		return id
	}
	return ae.NewRandomUUID()
}

// holdoutRecordings collects what OnHoldoutRecorded and OnWithheld are told.
type holdoutRecordings struct {
	mu       sync.Mutex
	results  []HoldoutRecordResult
	withheld []InjectionResult
}

func (r *holdoutRecordings) add(result HoldoutRecordResult) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.results = append(r.results, result)
}

func (r *holdoutRecordings) addWithheld(result InjectionResult) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.withheld = append(r.withheld, result)
}

func (r *holdoutRecordings) all() ([]HoldoutRecordResult, []InjectionResult) {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]HoldoutRecordResult(nil), r.results...), append([]InjectionResult(nil), r.withheld...)
}

// holdoutAgent is a learning agent with the holdout at rate, its run IDs given by ids.
func holdoutAgent(t *testing.T, h *host, store ae.Store, rate float64) (*agent.Agent, *agent.Config, *scripted,
	*runIDs, *recordings, *holdoutRecordings) {
	t.Helper()
	ids, injected, held := &runIDs{}, &recordings{}, &holdoutRecordings{}
	config := &agent.Config{Name: "triage-ticket", Tools: []tool.Tool{exits("exit=3", "exit=0")}}
	options := h.options(Options{Store: store, Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: ae.ExitCode("run_tests"), NewID: ids.newID, Holdout: HoldoutOptions{Rate: rate}})
	options.OnInjectionRecorded = injected.add
	options.OnHoldoutRecorded = held.add
	options.OnWithheld = held.addWithheld
	if err := UseAgentExperience(config, options); err != nil {
		t.Fatal(err)
	}
	model := newScripted(2)
	return newAgent(model, *config), config, model, ids, injected, held
}

func TestTheAssignmentRunsAreTheOnesTheFixturesPin(t *testing.T) {
	if ae.AssignHoldout(heldOutRun, ae.DefaultHoldoutSalt, 0.5) != ae.HoldoutWithheld ||
		ae.AssignHoldout(treatedRun, ae.DefaultHoldoutSalt, 0.5) != ae.HoldoutTreatment {
		t.Fatal("the test's runs are not in the arms it assumes")
	}
}

func TestAHeldOutRunGetsNoBlockNoExposureAndNoInjectionRecordAndIsRecordedAsHoldout(t *testing.T) {
	h := newHost(t)
	a, config, model, ids, injected, held := holdoutAgent(t, h, h.store, 0.5)
	runText(t, a, ticket) // the lesson: no block yet, so no assignment
	lesson := h.finalized[0].Record

	ids.next(heldOutRun)
	runText(t, a, nextTicket)
	closeRecordings(t, config)

	if len(model.blocks()) != 0 || len(h.injections) != 0 || len(injected.all()) != 0 {
		t.Fatalf("a held-out run was given a block: %d blocks, %+v, %+v", len(model.blocks()), h.injections, injected.all())
	}
	results, withheld := held.all()
	if len(withheld) != 1 || !reflect.DeepEqual(withheld[0].WithheldExperienceIDs, []ae.UUID{lesson.ExperienceID}) ||
		len(withheld[0].ExperienceIDs) != 0 {
		t.Fatalf("withheld %+v", withheld)
	}
	second := h.finalized[1].Record
	if second.SourceRunID != heldOutRun || len(second.Provenance.ExposedTo) != 0 {
		t.Fatalf("run %s, exposures %+v", second.SourceRunID, second.Provenance.ExposedTo)
	}
	if len(results) != 1 || results[0].Outcome != InjectionRecorded || results[0].Arm != ae.HoldoutWithheld ||
		results[0].RunID != heldOutRun {
		t.Fatalf("holdout records %+v", results)
	}
	rows := h.store.HoldoutRows()
	if len(rows) != 1 || rows[0].RunID != heldOutRun || rows[0].ExperienceID != lesson.ExperienceID ||
		rows[0].Arm != ae.HoldoutWithheld || rows[0].Revision != lesson.Revision || !rows[0].Scope.Equal(testScope) {
		t.Fatalf("rows %+v", rows)
	}
	if len(h.store.Injections()) != 0 {
		t.Fatalf("injections %+v", h.store.Injections())
	}
	if len(h.injectionFailures) != 0 || len(h.captureFailures) != 0 {
		t.Fatalf("failures %+v %+v", h.injectionFailures, h.captureFailures)
	}
}

func TestATreatedRunIsInjectedAsBeforeAndRecordedAsTreatment(t *testing.T) {
	h := newHost(t)
	a, config, model, ids, injected, held := holdoutAgent(t, h, h.store, 0.5)
	runText(t, a, ticket)
	lesson := h.finalized[0].Record

	ids.next(treatedRun)
	runText(t, a, nextTicket)
	closeRecordings(t, config)

	if counts := model.blockCounts(); !reflect.DeepEqual(counts, []int{0, 0, 0, 1, 1, 1}) {
		t.Fatalf("block counts %v", counts)
	}
	if len(h.injections) != 1 || len(injected.all()) != 1 ||
		len(h.finalized[1].Record.Provenance.ExposedTo) != 1 {
		t.Fatalf("injections %+v, records %+v", h.injections, injected.all())
	}
	results, withheld := held.all()
	if len(withheld) != 0 || len(results) != 1 || results[0].Arm != ae.HoldoutTreatment ||
		results[0].Outcome != InjectionRecorded {
		t.Fatalf("withheld %+v, records %+v", withheld, results)
	}
	rows := h.store.HoldoutRows()
	if len(rows) != 1 || rows[0].RunID != treatedRun || rows[0].ExperienceID != lesson.ExperienceID ||
		rows[0].Arm != ae.HoldoutTreatment {
		t.Fatalf("rows %+v", rows)
	}
}

func TestAtRateZeroNothingIsAssignedOrRecorded(t *testing.T) {
	h := newHost(t)
	a, config, model, ids, _, held := holdoutAgent(t, h, h.store, 0)
	runText(t, a, ticket)
	ids.next(heldOutRun)
	runText(t, a, nextTicket)
	closeRecordings(t, config)
	results, withheld := held.all()
	if len(model.blocks()) != 3 || len(results) != 0 || len(withheld) != 0 || len(h.store.HoldoutRows()) != 0 {
		t.Fatalf("%d blocks, %+v, %+v, %+v", len(model.blocks()), results, withheld, h.store.HoldoutRows())
	}
}

func TestAnUncapturedInvocationIsInjectedAsBeforeAndAssignedNothing(t *testing.T) {
	// The provider alone, wired by hand, with no capture: rate 1 withholds nothing.
	h := newHost(t)
	a, config, _, _, _, _ := holdoutAgent(t, h, h.store, 0)
	runText(t, a, ticket)
	closeRecordings(t, config)

	held := &holdoutRecordings{}
	provider, err := NewContextProvider(h.options(Options{Holdout: HoldoutOptions{Rate: 1},
		OnHoldoutRecorded: held.add}))
	if err != nil {
		t.Fatal(err)
	}
	provider.settings.OnHoldoutRecorded, provider.settings.OnWithheld = held.add, held.addWithheld
	model := newScripted(0)
	bare := agent.New(agent.ProviderConfig{ProviderName: "scripted", Run: model.run},
		agent.Config{Name: "triage-ticket", ContextProviders: []agent.ContextProvider{provider}})
	runText(t, bare, nextTicket)
	if err := provider.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	results, withheld := held.all()
	if len(model.blocks()) != 1 || len(results) != 0 || len(withheld) != 0 || len(h.store.HoldoutRows()) != 0 {
		t.Fatalf("%d blocks, %+v, %+v", len(model.blocks()), results, withheld)
	}
}

func TestAStoreThatDoesNotRecordTheHoldoutIsReportedNotTrackedAndTheRunIsStillWithheld(t *testing.T) {
	h := newHost(t)
	a, config, model, ids, _, held := holdoutAgent(t, h, searchOnly{h.store}, 1) // records neither injections nor the holdout
	runText(t, a, ticket)
	ids.next(heldOutRun)
	runText(t, a, nextTicket)
	closeRecordings(t, config)
	results, withheld := held.all()
	if len(model.blocks()) != 0 || len(withheld) != 1 || len(results) != 1 || results[0].Outcome != InjectionNotTracked {
		t.Fatalf("%d blocks, %+v, %+v", len(model.blocks()), withheld, results)
	}
}

func TestAnInvalidHoldoutIsRefusedAndAnEmptySaltIsTheDefault(t *testing.T) {
	h := newHost(t)
	for _, rate := range []float64{math.NaN(), -0.1, 1.1, math.Inf(1)} {
		config := &agent.Config{}
		if err := UseAgentExperience(config, h.options(Options{Holdout: HoldoutOptions{Rate: rate}})); err == nil {
			t.Errorf("rate %v was accepted", rate)
		}
	}
	provider, err := NewContextProvider(h.options(Options{Holdout: HoldoutOptions{Rate: 0.25}}))
	if err != nil || provider.settings.Holdout.Salt != ae.DefaultHoldoutSalt {
		t.Fatalf("%v %+v", err, provider)
	}
	provider, err = NewContextProvider(h.options(Options{Holdout: HoldoutOptions{Rate: 1, Salt: "experiment-2"}}))
	if err != nil || provider.settings.Holdout.Salt != "experiment-2" {
		t.Fatalf("%v %+v", err, provider)
	}
}

func TestACapturedRunRecordsTheModelAndTheHostsOwnValueWins(t *testing.T) {
	for _, c := range []struct {
		name, model string
		environment *ae.EnvironmentFingerprint
		want        *string
	}{
		{"no model", "", nil, nil},
		{"blank model", " \t ", nil, nil},
		{"the model, normalized", "  go-agent-model ", nil, ae.Ptr("go-agent-model")},
		{"the host's value", "go-agent-model", &ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "r",
			OperatingSystem: "o", Metadata: map[string]string{ae.ModelMetadataKey: "host-deployment"}}, ae.Ptr("host-deployment")},
	} {
		t.Run(c.name, func(t *testing.T) {
			h, model := newHost(t), newScripted(1)
			a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{
				Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests"),
				Model: c.model, Environment: c.environment}))
			runText(t, a, ticket)
			if len(h.finalized) != 1 {
				t.Fatalf("finalized %d, failures %+v", len(h.finalized), h.captureFailures)
			}
			record := h.finalized[0].Record
			got, ok := record.Environment.Metadata[ae.ModelMetadataKey]
			if (c.want == nil) != !ok || (ok && got != *c.want) {
				t.Fatalf("recorded %q (%v), want %v", got, ok, c.want)
			}
			// Bookkeeping only: never a precondition.
			for _, precondition := range record.Reflection.Preconditions {
				if strings.Contains(precondition, ae.ModelMetadataKey) {
					t.Fatalf("the model is a precondition: %q", precondition)
				}
			}
		})
	}
}
