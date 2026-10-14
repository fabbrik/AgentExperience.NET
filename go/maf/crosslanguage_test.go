package maf

// Story 22.6's cross-language proof, the Go half that needs no database.
//
//   - Go writes, .NET reads. A Go agent, on a fixed clock and fixed identifiers, captures, verifies and stores two
//     runs. The records it writes must be exactly testdata/cross-language/go-agent-records.json (checked in memory
//     here, and written to and read back from PostgreSQL in the PostgreSQL leg). The .NET contract tests insert that
//     same file into a schema the .NET migrator applied and read it back through PostgresExperienceRecordStore
//     (GoAgentRecordsPostgresRoundTripTests), which must return it unchanged. Set
//     AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1 to rewrite the file on purpose, then read the diff.
//   - .NET writes, Go reads: crosslanguage_postgres_test.go.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"
	"sync"
	"testing"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"
	"github.com/santhosh-tekuri/jsonschema/v6"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

const (
	updateVariable = "AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE"
	// goldenSourceVersion is the version recorded in each run's provenance, pinned so the checked-in records do not
	// change with every version (on either side: the .NET test reads this same file).
	goldenSourceVersion = "0.0.0+cross-language"
)

var (
	golden           = filepath.Join("testdata", "cross-language", "go-agent-records.json")
	crossScope       = ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project", AgentID: ae.Ptr("go-agent")}
	crossIdentity    = ae.Identity{Principal: "go-principal", Scope: crossScope}
	crossEnvironment = ae.EnvironmentFingerprint{HostName: "go-host", RuntimeVersion: "go1", OperatingSystem: "linux",
		ApplicationVersion: ae.Ptr("1.0.0"), Metadata: map[string]string{"region": "eu-west"}}
	crossStart = ae.MustParseInstant("2026-03-02T09:00:00.1234567+00:00")
)

// fixedClock moves 1.25 seconds every time it is read, from a fixed start.
func fixedClock() func() ae.Instant {
	var mu sync.Mutex
	ticks := int64(0)
	step := ae.MustParseDuration("00:00:01.2500000").Ticks()
	return func() ae.Instant {
		mu.Lock()
		defer mu.Unlock()
		ticks++
		instant, err := ae.InstantFromTicks(crossStart.Ticks() + ticks*step)
		if err != nil {
			panic(err)
		}
		return instant
	}
}

func fixedIDs() func() ae.UUID {
	var mu sync.Mutex
	counter := 0
	return func() ae.UUID {
		mu.Lock()
		defer mu.Unlock()
		counter++
		return ae.MustParseUUID(fmt.Sprintf("0000900d-0000-4000-8000-%012d", counter))
	}
}

// runGoAgent is two runs of a Go agent, deterministic: the first fails a test run once, the second is shown the
// lesson. It returns what was finalized, in order, and the scripted model.
func runGoAgent(t *testing.T, store ae.Store) ([]ae.FinalizedExperience, *scripted) {
	t.Helper()
	previous := sourceVersion
	sourceVersion = goldenSourceVersion
	t.Cleanup(func() { sourceVersion = previous })

	h := &host{}
	model := newScripted(2)
	model.arguments = func(int) string { return `{"suite":"unit"}` }
	config := agent.Config{Name: "Go Agent", Tools: []tool.Tool{exits("exit=3", "exit=0", "exit=3", "exit=0")}}
	options := h.options(Options{
		Store:              store,
		Identity:           crossIdentity,
		Verify:             ae.ToolSucceeded("run_tests", ae.WithArtifactRevision("build-7")),
		ClassifyToolResult: ae.ExitCode("run_tests"),
		Environment:        &crossEnvironment,
		Clock:              fixedClock(),
		NewID:              fixedIDs(),
	})
	if err := UseAgentExperience(&config, options); err != nil {
		t.Fatal(err)
	}
	a := newAgent(model, config)
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	if len(h.captureFailures) != 0 || len(h.injectionFailures) != 0 {
		t.Fatalf("capture %+v, injection %+v", h.captureFailures, h.injectionFailures)
	}
	return h.finalized, model
}

func storedRecords(finalized []ae.FinalizedExperience) []any {
	stored := make([]any, len(finalized))
	for i := range finalized {
		stored[i] = finalized[i].Record.ToStored()
	}
	return stored
}

func goldenText(t *testing.T, records any) []byte {
	t.Helper()
	data, err := ae.MarshalJSONValue(records)
	if err != nil {
		t.Fatal(err)
	}
	var indented bytes.Buffer
	if err := json.Indent(&indented, data, "", "  "); err != nil {
		t.Fatal(err)
	}
	indented.WriteByte('\n')
	return indented.Bytes()
}

func readGolden(t *testing.T) []any {
	t.Helper()
	value, err := ct.ReadJSON(golden)
	if err != nil {
		t.Fatalf("%v (set %s=1 to write it)", err, updateVariable)
	}
	records, ok := value.([]any)
	if !ok || len(records) == 0 {
		t.Fatalf("%s holds no records", golden)
	}
	return records
}

func TestTheGoAgentWritesExactlyTheCheckedInRecords(t *testing.T) {
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	finalized, model := runGoAgent(t, store)

	var statuses []ae.ExperienceStatus
	var exposures []int
	for _, f := range finalized {
		statuses = append(statuses, f.Record.Status)
		exposures = append(exposures, len(f.Record.Provenance.ExposedTo))
		if *f.Record.Provenance.SourceVersion != goldenSourceVersion {
			t.Fatalf("source version %q", *f.Record.Provenance.SourceVersion)
		}
	}
	if !reflect.DeepEqual(statuses, []ae.ExperienceStatus{ae.StatusValidated, ae.StatusValidated}) ||
		!reflect.DeepEqual(exposures, []int{0, 1}) {
		t.Fatalf("statuses %v, exposures %v", statuses, exposures)
	}
	// The second run's three requests each carry the one block.
	if counts := model.blockCounts(); !reflect.DeepEqual(counts, []int{0, 0, 0, 1, 1, 1}) {
		t.Fatalf("block counts %v", counts)
	}
	stored := storedRecords(finalized)
	for _, record := range stored {
		assertValidRecord(t, record)
	}

	if os.Getenv(updateVariable) == "1" {
		if err := os.MkdirAll(filepath.Dir(golden), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(golden, goldenText(t, stored), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	expected := readGolden(t)
	if difference := ct.Difference(expected, ct.AsWritten(stored), "$", ct.Options{}); difference != "" {
		t.Fatalf("%s (set %s=1 to rewrite %s on purpose)", difference, updateVariable, filepath.Base(golden))
	}
	// The file is exactly what this agent writes, byte for byte, so a rewrite shows only real changes.
	data, err := os.ReadFile(golden)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(data, goldenText(t, stored)) {
		t.Fatalf("%s is not formatted as the test writes it (set %s=1)", golden, updateVariable)
	}
}

func TestTheCheckedInRecordsReadBackThroughTheGoReaderUnchanged(t *testing.T) {
	for _, stored := range readGolden(t) {
		assertValidRecord(t, stored)
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		if difference := ct.Difference(stored, ct.AsWritten(record.ToStored()), "$", ct.Options{}); difference != "" {
			t.Fatal(difference)
		}
	}
}

// ---- The record schema (as the postgres module's tests validate against it) ---------------------------------------

const ecmaWhitespace = `\t\n\v\f\r \x{a0}\x{1680}\x{2000}-\x{200a}\x{2028}\x{2029}\x{202f}\x{205f}\x{3000}\x{feff}`

var unsupportedECMA = regexp.MustCompile(`\\[sS]|\(\?[a-z]*m`)

// ecmaRegexp runs the schema's ECMA-262 patterns with RE2: they agree on every construct the schema uses except
// \s, which is translated (go/README.md says why).
func ecmaRegexp(pattern string) (jsonschema.Regexp, error) {
	translated := strings.ReplaceAll(pattern, `[^\s]`, `[^`+ecmaWhitespace+`]`)
	if found := unsupportedECMA.FindString(translated); found != "" {
		return nil, fmt.Errorf("translate %q in %q for ECMA-262: RE2's meaning differs", found, pattern)
	}
	return regexp.Compile(translated)
}

var recordSchema = sync.OnceValues(func() (*jsonschema.Schema, error) {
	file, err := os.Open(ct.Schema)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	document, err := jsonschema.UnmarshalJSON(file)
	if err != nil {
		return nil, err
	}
	compiler := jsonschema.NewCompiler()
	compiler.UseRegexpEngine(ecmaRegexp)
	if err := compiler.AddResource("experience-record.v1.json", document); err != nil {
		return nil, err
	}
	return compiler.Compile("experience-record.v1.json")
})

func assertValidRecord(t *testing.T, stored any) {
	t.Helper()
	schema, err := recordSchema()
	if err != nil {
		t.Fatalf("the record schema does not compile: %v", err)
	}
	data, err := ae.MarshalJSONValue(stored)
	if err != nil {
		t.Fatal(err)
	}
	instance, err := jsonschema.UnmarshalJSON(bytes.NewReader(data))
	if err != nil {
		t.Fatal(err)
	}
	if err := schema.Validate(instance); err != nil {
		t.Fatalf("the record does not validate against the schema: %v", err)
	}
}
