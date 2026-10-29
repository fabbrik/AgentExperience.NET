package maf

// Story 27.3's cross-language proof (contract 1.11), the Go agent: it wraps a stub memory provider under "stub-memory"
// at rate 0.5 with the default salt, in the fleet scope the .NET and Python agents share, and runs eight captured runs,
// each finalized (some verified, some failed). Here, on the in-memory store, the runs, keys and arms it records must
// be the ones testdata/cross-language/go-agent-provider-rows.json holds; the PostgreSQL leg
// (crosslanguage_provider_postgres_test.go) writes that file and estimates the fleet.

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

var (
	goldenProviderRows = filepath.Join("testdata", "cross-language", "go-agent-provider-rows.json")
	providerScope      = ae.Scope{TenantID: "cross-language-provider", ApplicationID: "provider-app",
		ProjectID: "provider-project", AgentID: ae.Ptr("provider-agent")}
	// Each measured run's exit: runs 2, 5 and 7 fail. At rate 0.5 runs 1, 2, 5, 7 and 8 are held out, so both arms
	// have verified and failed runs.
	providerExits = []string{"exit=0", "exit=3", "exit=0", "exit=0", "exit=3", "exit=0", "exit=3", "exit=0"}
)

const fleetProviderKey = "stub-memory"

// providerRunID is the Go agent's Nth measured run, 1 to 8.
func providerRunID(n int) ae.UUID {
	return ae.MustParseUUID(fmt.Sprintf("60b0b000-0000-4000-8000-%012x", n))
}

// providerIDs is a NewID whose first ID after next(run) is run, and every other ID a fixed Go-only sequence.
type providerIDs struct {
	mu      sync.Mutex
	pending *ae.UUID
	counter int
}

func (p *providerIDs) next(run ae.UUID) { p.mu.Lock(); p.pending = &run; p.mu.Unlock() }

func (p *providerIDs) newID() ae.UUID {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.pending != nil {
		id := *p.pending
		p.pending = nil
		return id
	}
	p.counter++
	return ae.MustParseUUID(fmt.Sprintf("60b0c0de-0000-4000-8000-%012d", p.counter))
}

// runProviderAgent runs the eight measured runs through store, each record settled before the next run.
func runProviderAgent(t *testing.T, store ae.Store) (*host, *stubMemory, []ProviderHoldoutRecordResult) {
	t.Helper()
	previous := sourceVersion
	sourceVersion = goldenSourceVersion
	t.Cleanup(func() { sourceVersion = previous })

	h, ids, recorded, memory := &host{}, &providerIDs{}, &providerRecordings{}, &stubMemory{adds: "message"}
	config := agent.Config{Name: "Provider Agent", Tools: []tool.Tool{exits(providerExits...)},
		ContextProviders: []agent.ContextProvider{memory}}
	options := h.options(Options{
		Store:              store,
		Identity:           ae.Identity{Principal: "go-principal", Scope: providerScope},
		Verify:             ae.ToolSucceeded("run_tests", ae.WithArtifactRevision("build-7")),
		ClassifyToolResult: ae.ExitCode("run_tests"),
		Environment:        &crossEnvironment,
		Clock:              fixedClock(),
		NewID:              ids.newID,
		DisableInjection:   true,
	})
	if err := UseAgentExperience(&config, options); err != nil {
		t.Fatal(err)
	}
	wrapper, err := UseMeasuredMemory(&config, memory, fleetProviderKey,
		MeasuredMemoryOptions{Holdout: &HoldoutOptions{Rate: 0.5}, OnRecorded: recorded.add})
	if err != nil {
		t.Fatal(err)
	}
	model := newScripted(1)
	model.arguments = func(int) string { return `{"suite":"unit"}` }
	a := newAgent(model, config)
	for n := 1; n <= len(providerExits); n++ {
		ids.next(providerRunID(n))
		runText(t, a, fmt.Sprintf("Ticket #%d: a refund is stuck on a lock. Triage it.", 6200+n))
		if err := wrapper.Close(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(h.captureFailures) != 0 || len(h.finalized) != len(providerExits) {
		t.Fatalf("capture %+v, %d finalized", h.captureFailures, len(h.finalized))
	}
	results := recorded.all()
	for i, result := range results {
		run := providerRunID(i + 1)
		if result.Outcome != InjectionRecorded || result.Assignment.RunID != run ||
			result.Assignment.Arm != ae.AssignProviderHoldout(run, fleetProviderKey, ae.DefaultHoldoutSalt, 0.5) {
			t.Fatalf("run %d: %+v", i+1, result)
		}
	}
	if len(results) != len(providerExits) || hooks(memory) != [2]int32{8, 8} {
		t.Fatalf("%d records, hooks %v", len(results), hooks(memory))
	}
	return h, memory, results
}

func TestTheGoAgentWrapsItsProviderAsTheRowsFileSays(t *testing.T) {
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	runProviderAgent(t, store)
	var actual []string
	for _, row := range store.ProviderHoldoutRows() {
		actual = append(actual, strings.Join([]string{row.RunID.String(), row.ProviderKey, string(row.Arm)}, " "))
	}
	slices.Sort(actual)
	if _, err := os.Stat(goldenProviderRows); err != nil {
		t.Skipf("%s is written by the PostgreSQL leg: %v", goldenProviderRows, err)
	}
	golden, err := ct.ReadJSON(goldenProviderRows)
	if err != nil {
		t.Fatal(err)
	}
	value, _ := golden.(*ae.Object).Get("experience_provider_holdout_assignments")
	var expected []string
	for _, item := range value.([]any) {
		row := item.(*ae.Object)
		run, _ := row.Get("run_id")
		key, _ := row.Get("provider_key")
		arm, _ := row.Get("arm")
		expected = append(expected, strings.Join([]string{run.(string), key.(string), arm.(string)}, " "))
	}
	if !slices.Equal(actual, expected) {
		t.Fatalf("rows %v, the rows file %v", actual, expected)
	}
}
