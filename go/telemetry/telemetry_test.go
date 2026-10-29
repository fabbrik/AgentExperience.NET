package telemetry_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"go/parser"
	"go/token"
	"io/fs"
	"iter"
	"net"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/agent/harness/toolautocall"
	"github.com/microsoft/agent-framework-go/message"
	"github.com/microsoft/agent-framework-go/tool"
	"github.com/microsoft/agent-framework-go/tool/functool"
	"go.opentelemetry.io/otel/attribute"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
	"github.com/fabbrik/AgentExperience.NET/go/maf"
	"github.com/fabbrik/AgentExperience.NET/go/telemetry"
)

const marker = "SECRET-MARKER-7f3a"

// ---- docs/telemetry-names.json: generated from the .NET definitions ----------------------------------------------

type namesFile struct {
	Sources        []string `json:"sources"`
	SpanNamePrefix string   `json:"spanNamePrefix"`
	FaultedOutcome string   `json:"faultedOutcome"`
	Instruments    []struct {
		Name string `json:"name"`
		Kind string `json:"kind"`
		Unit string `json:"unit"`
	} `json:"instruments"`
	Dimensions   []string `json:"dimensions"`
	ErrorClasses []string `json:"errorClasses"`
	Attributes   []string `json:"attributes"`
	Operations   []struct {
		Operation string   `json:"operation"`
		Source    string   `json:"source"`
		Span      bool     `json:"span"`
		Outcomes  []string `json:"outcomes"`
	} `json:"operations"`
}

func names(t *testing.T) namesFile {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "docs", "telemetry-names.json"))
	if err != nil {
		t.Fatal(err)
	}
	var file namesFile
	if err := json.Unmarshal(data, &file); err != nil {
		t.Fatal(err)
	}
	return file
}

func (n namesFile) operation(name string) (struct {
	Operation string   `json:"operation"`
	Source    string   `json:"source"`
	Span      bool     `json:"span"`
	Outcomes  []string `json:"outcomes"`
}, bool) {
	for _, item := range n.Operations {
		if item.Operation == name {
			return item, true
		}
	}
	return item0, false
}

var item0 struct {
	Operation string   `json:"operation"`
	Source    string   `json:"source"`
	Span      bool     `json:"span"`
	Outcomes  []string `json:"outcomes"`
}

func TestThePortsTableOfNamesIsInTheNetGeneratedFile(t *testing.T) {
	file := names(t)
	if !slices.Equal(slices.Sorted(slices.Values([]string{ae.SourceCore, ae.SourceMAF})), slices.Sorted(slices.Values(file.Sources))) {
		t.Errorf("sources %v", file.Sources)
	}
	if telemetry.SpanNamePrefix != file.SpanNamePrefix || ae.OutcomeFaulted != file.FaultedOutcome {
		t.Errorf("prefix %q faulted %q", file.SpanNamePrefix, file.FaultedOutcome)
	}
	if !slices.Equal(ae.ErrorClasses(), file.ErrorClasses) {
		t.Errorf("error classes %v", file.ErrorClasses)
	}
	if !slices.Equal(telemetry.Dimensions(), file.Dimensions) {
		t.Errorf("dimensions %v", file.Dimensions)
	}
	var instruments [][3]string
	for _, item := range file.Instruments {
		instruments = append(instruments, [3]string{item.Name, item.Kind, item.Unit})
	}
	if !slices.Equal(telemetry.Instruments(), instruments) {
		t.Errorf("instruments %v", file.Instruments)
	}
	for _, attribute := range telemetry.SpanAttributes() {
		if !slices.Contains(file.Attributes, attribute) {
			t.Errorf("the attribute %s is not in the file", attribute)
		}
	}
	for name, spec := range ae.ObservedOperations() {
		pinned, ok := file.operation(name)
		if !ok {
			t.Errorf("the operation %s is not in the file", name)
			continue
		}
		if pinned.Source != spec.Source || pinned.Span != spec.Span {
			t.Errorf("%s: source %s span %v, file says %s %v", name, spec.Source, spec.Span, pinned.Source, pinned.Span)
		}
		for _, outcome := range spec.Outcomes {
			if !slices.Contains(pinned.Outcomes, outcome) {
				t.Errorf("%s: the outcome %s is not in the file", name, outcome)
			}
		}
	}
}

// ---- A host: the SDK's in-memory exporters, a scripted model, an in-memory store ---------------------------------------

type sink struct {
	spans  *tracetest.InMemoryExporter
	reader *sdkmetric.ManualReader
}

func enable(t *testing.T) *sink {
	t.Helper()
	spans := tracetest.NewInMemoryExporter()
	reader := sdkmetric.NewManualReader()
	disable := telemetry.Enable(
		telemetry.WithTracerProvider(sdktrace.NewTracerProvider(sdktrace.WithSyncer(spans))),
		telemetry.WithMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))))
	t.Cleanup(func() { disable(); ae.SetObserver(nil) })
	return &sink{spans: spans, reader: reader}
}

type point struct {
	meter      string
	name, unit string
	attributes map[string]any
	value      float64
}

func (s *sink) points(t *testing.T) []point {
	t.Helper()
	var data metricdata.ResourceMetrics
	if err := s.reader.Collect(context.Background(), &data); err != nil {
		t.Fatal(err)
	}
	var found []point
	for _, scope := range data.ScopeMetrics {
		for _, m := range scope.Metrics {
			add := func(set attribute.Set, value float64) {
				attributes := map[string]any{}
				for _, kv := range set.ToSlice() {
					attributes[string(kv.Key)] = kv.Value.AsInterface()
				}
				found = append(found, point{scope.Scope.Name, m.Name, m.Unit, attributes, value})
			}
			switch data := m.Data.(type) {
			case metricdata.Sum[int64]:
				for _, p := range data.DataPoints {
					add(p.Attributes, float64(p.Value))
				}
			case metricdata.Histogram[float64]:
				for _, p := range data.DataPoints {
					add(p.Attributes, p.Sum)
				}
			}
		}
	}
	return found
}

type scriptedModel struct {
	mu      sync.Mutex
	counter int
}

func (m *scriptedModel) run(_ context.Context, messages []*message.Message,
	_ ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
	return func(yield func(*agent.ResponseUpdate, error) bool) {
		m.mu.Lock()
		defer m.mu.Unlock()
		lastUser, made := -1, 0
		for i, item := range messages {
			if item.Role == message.RoleUser {
				lastUser = i
			}
		}
		for _, item := range messages[lastUser+1:] {
			for _, content := range item.Contents {
				if _, ok := content.(*message.FunctionCallContent); ok {
					made++
				}
			}
		}
		var contents message.Contents
		if made < 2 {
			m.counter++
			contents = message.Contents{&message.FunctionCallContent{CallID: fmt.Sprintf("call-%d", m.counter),
				Name: "run_tests", Arguments: "{}"}}
		} else {
			contents = message.Contents{&message.TextContent{Text: "done"}}
		}
		yield(&agent.ResponseUpdate{Role: message.RoleAssistant, Contents: contents}, nil)
	}
}

type embedder struct{}

func (embedder) ModelID() string { return "telemetry-test" }
func (embedder) Dimension() int  { return 4 }
func (embedder) Embed(context.Context, string) ([]float32, error) {
	return []float32{1, 0, 0, 0}, nil
}

// brokenSearch is the in-memory store whose search fails; it keeps every optional interface of the store.
type brokenSearch struct{ *inmemory.Store }

func (brokenSearch) Search(context.Context, ae.AuthorizationBounds, ae.SearchParameters) ([]ae.SearchRow, error) {
	return nil, fmt.Errorf("the database is down %s", marker)
}

// searchOnly is a store that does not record injections.
type searchOnly struct{ inner *inmemory.Store }

func (s searchOnly) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, finalized *ae.FinalizedExperience,
	actor string, recordedAt *time.Time) error {
	return s.inner.InsertFinalized(ctx, bounds, finalized, actor, recordedAt)
}

func (s searchOnly) Search(ctx context.Context, bounds ae.AuthorizationBounds,
	parameters ae.SearchParameters) ([]ae.SearchRow, error) {
	return s.inner.Search(ctx, bounds, parameters)
}

// failingRecord is the in-memory store whose injection record fails with a network error.
type failingRecord struct{ *inmemory.Store }

func (failingRecord) RecordInjection(context.Context, ae.AuthorizationBounds, ae.Scope, []ae.UUID, ae.Instant) error {
	return &net.OpError{Op: "write", Err: errors.New("connection reset")}
}

type suite struct {
	Suite string `json:"suite,omitempty"`
}

type outcomes struct {
	mu        sync.Mutex
	injected  []int
	recorded  []maf.InjectionRecordOutcome
	finalized int
}

func newStore(t *testing.T) *inmemory.Store {
	t.Helper()
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	return store
}

// drive runs two tickets through an agent wired with the integration and waits for the background work.
func drive(t *testing.T, st ae.Store, options maf.Options, first, second string) *outcomes {
	t.Helper()
	seen := &outcomes{}
	runs := 0
	runTests := functool.MustNew(functool.Config{Name: "run_tests", Description: "Runs the tests."},
		func(context.Context, suite) (string, error) {
			runs++
			if runs%2 == 1 {
				return "exit=3 1 test failed", nil
			}
			return "exit=0 all tests passed", nil
		})
	identity, err := ae.DevelopmentIdentity(ae.DevelopmentOptions{Application: "telemetry", EnvironmentName: ae.Ptr("Development")})
	if err != nil {
		t.Fatal(err)
	}
	config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{runTests}}
	options.Store, options.Identity = st, identity
	options.Verify, options.ClassifyToolResult = ae.ToolSucceeded("run_tests"), ae.ExitCode("run_tests")
	options.Environment = &ae.EnvironmentFingerprint{HostName: "test", RuntimeVersion: "go", OperatingSystem: "any",
		ApplicationVersion: ae.Ptr("1.0.0"), Metadata: map[string]string{}}
	options.OnInjected = func(r maf.InjectionResult) {
		seen.mu.Lock()
		seen.injected = append(seen.injected, len(r.ExperienceIDs))
		seen.mu.Unlock()
	}
	options.OnInjectionRecorded = func(r maf.InjectionRecordResult) {
		seen.mu.Lock()
		seen.recorded = append(seen.recorded, r.Outcome)
		seen.mu.Unlock()
	}
	options.OnFinalized = func(ae.FinalizedExperience) {
		seen.mu.Lock()
		seen.finalized++
		seen.mu.Unlock()
	}
	if err := maf.UseAgentExperience(&config, options); err != nil {
		t.Fatal(err)
	}
	model := &scriptedModel{}
	triage := agent.New(agent.ProviderConfig{ProviderName: "scripted", Run: model.run,
		Middlewares: []agent.Middleware{toolautocall.New(toolautocall.Config{})}}, config)
	for _, ticket := range []string{first, second} {
		if _, err := triage.RunText(context.Background(), ticket).Collect(); err != nil {
			t.Fatal(err)
		}
	}
	if err := maf.WaitForIndexWrites(context.Background(), &config); err != nil {
		t.Fatal(err)
	}
	if provider := maf.ContextProviderOf(&config); provider != nil {
		if err := provider.Close(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	return seen
}

const (
	ticket     = "Ticket #4812: a refund is stuck on a lock. Triage it."
	nextTicket = "Ticket #4977: a refund is stuck on a lock again. Triage it."
)

func attributes(span tracetest.SpanStub) map[string]any {
	found := map[string]any{}
	for _, kv := range span.Attributes {
		found[string(kv.Key)] = kv.Value.AsInterface()
	}
	return found
}

func spanNamed(t *testing.T, spans tracetest.SpanStubs, name string) tracetest.SpanStub {
	t.Helper()
	for _, span := range spans {
		if span.Name == name {
			return span
		}
	}
	t.Fatalf("no span %s among %d", name, len(spans))
	return tracetest.SpanStub{}
}

func counted(points []point, operation string) []point {
	var found []point
	for _, p := range points {
		if p.name == telemetry.OperationCount && p.attributes["operation"] == operation {
			found = append(found, p)
		}
	}
	return found
}

func TestAnAgentThatInjectsCapturesAndRecordsAnInjectionEmitsExactlyThePinnedNames(t *testing.T) {
	file := names(t)
	s := enable(t)
	// With the holdout on, every ID is one the assignment puts in the treatment arm: the block is injected, and both
	// record_injection and record_holdout are reported.
	// Contract 1.10: the benefit policy on, so its background refresh (benefit_refresh) is reported too.
	seen := drive(t, newStore(t), maf.Options{Embedder: embedder{}, Holdout: maf.HoldoutOptions{Rate: 0.5},
		NewID: treatedIDs, Benefit: ae.BenefitPolicy{Enabled: true}}, ticket, nextTicket)
	if len(seen.recorded) != 1 || seen.recorded[0] != maf.InjectionRecorded {
		t.Fatalf("recorded %v", seen.recorded)
	}

	spans := s.spans.GetSpans()
	for _, want := range []string{"inject", "retrieve", "capture.start_run", "capture.append_attempt",
		"capture.complete_run", "verify", "reflect", "finalize", "index"} {
		spanNamed(t, spans, "agentexperience."+want)
	}
	for _, span := range spans {
		got := attributes(span)
		operation, _ := got[telemetry.AttributeOperation].(string)
		pinned, ok := file.operation(operation)
		if !ok || !pinned.Span || span.Name != file.SpanNamePrefix+operation {
			t.Errorf("span %s (operation %q) is not pinned", span.Name, operation)
			continue
		}
		if outcome, _ := got[telemetry.AttributeOutcome].(string); !slices.Contains(pinned.Outcomes, outcome) {
			t.Errorf("%s: outcome %q is not pinned", span.Name, outcome)
		}
		for key := range got {
			if !slices.Contains(file.Attributes, key) {
				t.Errorf("%s: attribute %s is not pinned", span.Name, key)
			}
		}
		if span.Status.Code.String() != "Ok" || span.SpanKind.String() != "internal" {
			t.Errorf("%s: status %v kind %v", span.Name, span.Status.Code, span.SpanKind)
		}
	}
	if slices.ContainsFunc(spans, func(span tracetest.SpanStub) bool { return span.Name == "agentexperience.record_injection" }) {
		t.Error("record_injection is measurements only, no span")
	}
	if slices.ContainsFunc(spans, func(span tracetest.SpanStub) bool { return span.Name == "agentexperience.benefit_refresh" }) {
		t.Error("benefit_refresh is measurements only, no span")
	}

	points := s.points(t)
	units := map[string]string{}
	for _, item := range file.Instruments {
		units[item.Name] = item.Unit
	}
	seenOperations := map[string]bool{}
	for _, p := range points {
		if units[p.name] != p.unit {
			t.Errorf("%s has unit %q", p.name, p.unit)
		}
		operation, _ := p.attributes["operation"].(string)
		pinned, ok := file.operation(operation)
		if !ok || pinned.Source != p.meter {
			t.Errorf("%s: operation %q on meter %s", p.name, operation, p.meter)
			continue
		}
		if outcome, _ := p.attributes["outcome"].(string); !slices.Contains(pinned.Outcomes, outcome) {
			t.Errorf("%s: outcome %q", operation, outcome)
		}
		for key := range p.attributes {
			if !slices.Contains(file.Dimensions, key) || key == "error.class" {
				t.Errorf("%s: dimension %s on a successful run", p.name, key)
			}
		}
		if _, isBool := p.attributes["nested"].(bool); !isBool {
			t.Errorf("%s: nested is %v", p.name, p.attributes["nested"])
		}
		seenOperations[operation] = true
	}
	for operation := range ae.ObservedOperations() {
		if !seenOperations[operation] {
			t.Errorf("%s was never reported", operation)
		}
	}
	nested := func(operation string) any { return counted(points, operation)[0].attributes["nested"] }
	if nested("verify") != true || nested("reflect") != true || nested("finalize") != false ||
		nested("retrieve") != false || nested("inject") != false || nested("record_injection") != false ||
		nested("benefit_refresh") != false {
		t.Errorf("nested flags: verify %v reflect %v finalize %v retrieve %v", nested("verify"), nested("reflect"),
			nested("finalize"), nested("retrieve"))
	}

	finalize := spanNamed(t, spans, "agentexperience.finalize")
	verify := spanNamed(t, spans, "agentexperience.verify")
	retrieve := spanNamed(t, spans, "agentexperience.retrieve")
	inject := spanNamed(t, spans, "agentexperience.inject")
	if verify.Parent.SpanID() != finalize.SpanContext.SpanID() || retrieve.Parent.SpanID() != inject.SpanContext.SpanID() {
		t.Error("a nested operation is not a child of the span that ran it")
	}
	if attributes(finalize)["agentexperience.stage"] != "CreateRecord" {
		t.Errorf("stage %v", attributes(finalize)["agentexperience.stage"])
	}
}

func TestInjectIsNothingToInjectThenInjected(t *testing.T) {
	s := enable(t)
	drive(t, newStore(t), maf.Options{}, ticket, nextTicket)
	var got []any
	for _, span := range s.spans.GetSpans() {
		if span.Name == "agentexperience.inject" {
			got = append(got, attributes(span)[telemetry.AttributeOutcome])
		}
	}
	if len(got) != 2 || got[0] != "NothingToInject" || got[1] != "Injected" {
		t.Fatalf("inject outcomes %v", got)
	}
}

// treatedIDs are random IDs the holdout assigns to the treatment arm at rate 0.5 with the default salt.
func treatedIDs() ae.UUID {
	for {
		if id := ae.NewRandomUUID(); ae.AssignHoldout(id, ae.DefaultHoldoutSalt, 0.5) == ae.HoldoutTreatment {
			return id
		}
	}
}

func TestAHeldOutRunIsWithheldAndItsAssignmentIsRecordHoldoutWithNoSpan(t *testing.T) {
	s := enable(t)
	file := names(t)
	drive(t, newStore(t), maf.Options{Holdout: maf.HoldoutOptions{Rate: 1}}, ticket, nextTicket)
	var got []any
	for _, span := range s.spans.GetSpans() {
		if span.Name == "agentexperience.inject" {
			got = append(got, attributes(span)[telemetry.AttributeOutcome])
		}
		if span.Name == "agentexperience.record_holdout" {
			t.Error("record_holdout is measurements only, no span")
		}
	}
	if len(got) != 2 || got[0] != "NothingToInject" || got[1] != "Withheld" {
		t.Fatalf("inject outcomes %v", got)
	}
	recorded := counted(s.points(t), "record_holdout")
	if len(recorded) != 1 || recorded[0].attributes["outcome"] != "Recorded" || recorded[0].value != 1 {
		t.Fatalf("record_holdout %v", recorded)
	}
	pinned, ok := file.operation("record_holdout")
	if !ok || !slices.Contains(pinned.Outcomes, "Recorded") || pinned.Span {
		t.Fatalf("record_holdout in the file: %+v", pinned)
	}
	if len(counted(s.points(t), "record_injection")) != 0 {
		t.Error("a withheld block was recorded as injected")
	}
}

func TestAFailingSearchIsRetrievalFailedAndNothingThrew(t *testing.T) {
	s := enable(t)
	drive(t, brokenSearch{newStore(t)}, maf.Options{}, ticket+" "+marker, nextTicket)
	for _, span := range s.spans.GetSpans() {
		switch span.Name {
		case "agentexperience.inject":
			if attributes(span)[telemetry.AttributeOutcome] != "RetrievalFailed" {
				t.Errorf("inject: %v", attributes(span))
			}
		case "agentexperience.retrieve":
			if attributes(span)[telemetry.AttributeOutcome] != "Failed" {
				t.Errorf("retrieve: %v", attributes(span))
			}
		}
	}
	for _, p := range s.points(t) {
		if p.name == telemetry.OperationFailures {
			t.Errorf("nothing threw, yet a failure was counted: %v", p.attributes)
		}
	}
}

func TestAnOperationThatFailsIsFaultedWithItsClassAndTypeNameOnly(t *testing.T) {
	s := enable(t)
	_, err := ae.FinalizeContext(context.Background(), ae.CapturedRun{}, ae.Verification{})
	if err == nil {
		t.Fatal("an unfinished run was finalized")
	}
	span := spanNamed(t, s.spans.GetSpans(), "agentexperience.finalize")
	got := attributes(span)
	if got[telemetry.AttributeOutcome] != "Faulted" || got[telemetry.AttributeErrorClass] != "Unexpected" ||
		got[telemetry.AttributeErrorType] != "errors.errorString" || got["agentexperience.stage"] != "Load" {
		t.Errorf("attributes %v", got)
	}
	if span.Status.Code.String() != "Error" || span.Status.Description != "" || len(span.Events) != 0 {
		t.Errorf("status %v %q events %d", span.Status.Code, span.Status.Description, len(span.Events))
	}
	var failures []point
	for _, p := range s.points(t) {
		if p.name == telemetry.OperationFailures {
			failures = append(failures, p)
		}
	}
	if len(failures) != 1 || failures[0].value != 1 || failures[0].attributes["operation"] != "finalize" ||
		failures[0].attributes["error.class"] != "Unexpected" || failures[0].attributes["nested"] != false ||
		len(failures[0].attributes) != 3 {
		t.Errorf("failures %v", failures)
	}
	if c := counted(s.points(t), "finalize"); len(c) != 1 || c[0].attributes["outcome"] != "Faulted" {
		t.Errorf("count %v", c)
	}
}

func TestRecordInjectionFailureClassesFollowTheNetTable(t *testing.T) {
	s := enable(t)
	drive(t, failingRecord{newStore(t)}, maf.Options{}, ticket, nextTicket)
	drive(t, searchOnly{newStore(t)}, maf.Options{}, ticket, nextTicket)
	var outcomes []string
	for _, p := range counted(s.points(t), "record_injection") {
		outcomes = append(outcomes, p.attributes["outcome"].(string))
	}
	slices.Sort(outcomes)
	if !slices.Equal(outcomes, []string{"NotRecorded", "NotTracked"}) {
		t.Fatalf("outcomes %v", outcomes)
	}
	for _, p := range s.points(t) {
		if p.name == telemetry.OperationFailures {
			if p.attributes["operation"] != "record_injection" || p.attributes["error.class"] != "Infrastructure" {
				t.Errorf("failure %v", p.attributes)
			}
		}
	}
}

func TestNothingOfTheRunReachesASpanOrAMetricDimension(t *testing.T) {
	s := enable(t)
	drive(t, newStore(t), maf.Options{}, "Triage "+marker+" please", "Triage "+marker+" again")
	var texts []string
	for _, span := range s.spans.GetSpans() {
		texts = append(texts, span.Name)
		for _, kv := range span.Attributes {
			texts = append(texts, fmt.Sprint(kv.Value.AsInterface()))
		}
	}
	for _, p := range s.points(t) {
		for _, value := range p.attributes {
			texts = append(texts, fmt.Sprint(value))
		}
	}
	if len(texts) == 0 {
		t.Fatal("nothing was emitted")
	}
	for _, text := range texts {
		if strings.Contains(text, marker) {
			t.Errorf("%q carries the marker", text)
		}
	}
}

// ---- Telemetry never changes a result -------------------------------------------------------------------------------

type panickingObserver struct{}

func (panickingObserver) Start(context.Context, string, string, bool) (context.Context, any) {
	panic("start")
}
func (panickingObserver) Finish(any, ae.Observation) { panic("finish") }

func TestAnObserverThatPanicsChangesNothing(t *testing.T) {
	run := func() ([]int, []maf.InjectionRecordOutcome, int) {
		seen := drive(t, newStore(t), maf.Options{Embedder: embedder{}}, ticket, nextTicket)
		return seen.injected, seen.recorded, seen.finalized
	}
	ae.SetObserver(nil)
	wantInjected, wantRecorded, wantFinalized := run()
	ae.SetObserver(panickingObserver{})
	t.Cleanup(func() { ae.SetObserver(nil) })
	gotInjected, gotRecorded, gotFinalized := run()
	if !slices.Equal(gotInjected, wantInjected) || !slices.Equal(gotRecorded, wantRecorded) || gotFinalized != wantFinalized {
		t.Fatalf("a panicking observer changed the result: %v %v %d, want %v %v %d", gotInjected, gotRecorded,
			gotFinalized, wantInjected, wantRecorded, wantFinalized)
	}
	if wantFinalized == 0 {
		t.Fatal("the control run stored nothing")
	}
}

// ---- No OpenTelemetry in the core or the integration ----------------------------------------------------------------

func TestNeitherTheCoreNorTheIntegrationNorTheStoresImportOpenTelemetry(t *testing.T) {
	for _, directory := range []string{"..", "../maf", "../inmemory", "../postgres"} {
		module, err := os.ReadFile(filepath.Join(directory, "go.mod"))
		if err != nil {
			t.Fatal(err)
		}
		for _, line := range strings.Split(string(module), "\n") {
			if strings.Contains(line, "AgentExperience.NET/go/telemetry") {
				t.Errorf("%s/go.mod requires the telemetry module: %s", directory, line)
			}
			if directory != "../maf" && strings.Contains(line, "go.opentelemetry.io") {
				t.Errorf("%s/go.mod requires OpenTelemetry: %s", directory, line)
			}
		}
		err = filepath.WalkDir(directory, func(path string, entry fs.DirEntry, err error) error {
			if err != nil {
				return err
			}
			if entry.IsDir() && (entry.Name() == "telemetry" || entry.Name() == "testdata") {
				return filepath.SkipDir
			}
			if entry.IsDir() || !strings.HasSuffix(path, ".go") {
				return nil
			}
			file, err := parser.ParseFile(token.NewFileSet(), path, nil, parser.ImportsOnly)
			if err != nil {
				return err
			}
			for _, spec := range file.Imports {
				if strings.Contains(spec.Path.Value, "go.opentelemetry.io") ||
					strings.Contains(spec.Path.Value, "AgentExperience.NET/go/telemetry") {
					t.Errorf("%s imports %s", path, spec.Path.Value)
				}
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
}
