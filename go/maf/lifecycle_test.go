package maf

// The review's cases: panics through the invocation, setup validation, the module version, injection placement and
// ranking, exposures, and how results and arguments are recorded.

import (
	"context"
	"iter"
	"reflect"
	"runtime/debug"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"
	"github.com/microsoft/agent-framework-go/tool"
	"github.com/microsoft/agent-framework-go/tool/functool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

type providerBug struct{ detail string }

// recovered runs fn and returns the value it panicked with.
func recovered(fn func()) (value any) {
	defer func() { value = recover() }()
	fn()
	return nil
}

func panicking(value any) agent.RunFunc {
	return func(context.Context, []*message.Message, ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
		return func(func(*agent.ResponseUpdate, error) bool) { panic(value) }
	}
}

func TestAPanicFromTheProviderOrADownstreamMiddlewareReachesTheCallerAndFailsTheRun(t *testing.T) {
	bug := &providerBug{"secret detail"}
	for name, wire := range map[string]func(*agent.Config) agent.ProviderConfig{
		"provider": func(*agent.Config) agent.ProviderConfig { return agent.ProviderConfig{Run: panicking(bug)} },
		"middleware": func(config *agent.Config) agent.ProviderConfig {
			config.Middlewares = append(config.Middlewares, agent.MiddlewareFunc(func(agent.RunFunc, context.Context,
				[]*message.Message, ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
				return func(func(*agent.ResponseUpdate, error) bool) { panic(bug) }
			}))
			return agent.ProviderConfig{Run: newScripted(0).run}
		},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHost(t)
			checked := &seen{passed: yes()}
			config := agent.Config{Name: "triage-ticket"}
			if err := UseAgentExperience(&config, h.options(Options{Verify: ae.Check("c", checked.check)})); err != nil {
				t.Fatal(err)
			}
			a := agent.New(wire(&config), config)
			value := recovered(func() { _, _ = a.RunText(context.Background(), ticket).Collect() })
			if value != bug {
				t.Fatalf("the caller got %v, not the panic unchanged", value)
			}
			run := checked.only(t)
			want := "panic(github.com/fabbrik/AgentExperience.NET/go/maf.providerBug)"
			if run.ExecutionStatus != ae.RunFailed || *run.Attempts[0].Error != want {
				t.Fatalf("%s %v", run.ExecutionStatus, *run.Attempts[0].Error)
			}
		})
	}
}

// A panic in the caller's own loop over the updates passes through the iterator, so it is recorded as the
// invocation's failure too, and goes on unchanged.
func TestAPanicInTheCallersLoopIsRecordedAsTheInvocationsFailure(t *testing.T) {
	h, model := newHost(t), newScripted(0)
	checked := &seen{passed: yes()}
	a := build(t, model, nil, h.options(Options{Verify: ae.Check("c", checked.check)}))
	value := recovered(func() {
		for range a.RunText(context.Background(), ticket, agent.Stream(true)) {
			panic("the caller's bug")
		}
	})
	if value != "the caller's bug" {
		t.Fatalf("the caller got %v", value)
	}
	if run := checked.only(t); run.ExecutionStatus != ae.RunFailed || *run.Attempts[0].Error != "panic(string)" {
		t.Fatalf("%s", run.ExecutionStatus)
	}
}

func TestAnIdentityResolverThatPanicsIsReportedAndTheAgentRuns(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ResolveIdentity: func(context.Context, IdentityContext) (*ae.Identity, error) { panic("resolver bug") }}))
	if runText(t, a, ticket).String() != "done" {
		t.Fatal("the agent did not run")
	}
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageResolveIdentity}) || len(h.finalized) != 0 {
		t.Fatalf("stages %v", h.stages())
	}
}

func TestTheSetupRefusesABadSanitizationOrEnvironmentAndTwoIdentities(t *testing.T) {
	h := newHost(t)
	identity := ae.Identity{Principal: "svc", Scope: testScope}
	bounds := ae.BoundsOf(testScope)
	resolver := func(context.Context, IdentityContext) (*ae.Identity, error) { return nil, nil }
	for name, options := range map[string]Options{
		"a result policy dropping the value": {Store: h.store, Identity: identity,
			Sanitization: ae.Sanitization{ToolArguments: ae.NewSanitizationPolicy(), ToolResult: ae.NewSanitizationPolicy()}},
		"a blank host":             {Store: h.store, Identity: identity, Environment: &ae.EnvironmentFingerprint{RuntimeVersion: "go", OperatingSystem: "linux"}},
		"a blank metadata key":     {Store: h.store, Identity: identity, Environment: &ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "go", OperatingSystem: "linux", Metadata: map[string]string{" ": "x"}}},
		"invalid UTF-8":            {Store: h.store, Identity: identity, Environment: &ae.EnvironmentFingerprint{HostName: "h\xff", RuntimeVersion: "go", OperatingSystem: "linux"}},
		"a blank app version":      {Store: h.store, Identity: identity, Environment: &ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "go", OperatingSystem: "linux", ApplicationVersion: ae.Ptr(" ")}},
		"a resolver and bounds":    {Store: h.store, Identity: ae.Identity{Bounds: &bounds}, ResolveIdentity: resolver},
		"a resolver and a scope":   {Store: h.store, Identity: ae.Identity{Scope: ae.Scope{ProjectID: "p"}}, ResolveIdentity: resolver},
		"a blank identity's app":   {Store: h.store, Identity: ae.Identity{Principal: "svc", Scope: ae.Scope{TenantID: "t", ProjectID: "p"}}},
		"only bounds, no resolver": {Store: h.store, Identity: ae.Identity{Bounds: &bounds}},
	} {
		if err := UseAgentExperience(&agent.Config{}, options); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	allowing := ae.SanitizationAllowing("suite")
	if err := UseAgentExperience(&agent.Config{}, Options{Store: h.store, Identity: identity, Sanitization: allowing,
		Environment: &ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "go", OperatingSystem: "linux"}}); err != nil {
		t.Fatal(err)
	}
}

func TestTheVersionIsTheModulesAsTheBuildRecordedIt(t *testing.T) {
	module := func(version string) *debug.Module { return &debug.Module{Path: modulePath, Version: version} }
	for _, c := range []struct {
		info     *debug.BuildInfo
		ok       bool
		expected string
	}{
		{nil, false, "0.0.0-dev"},
		{&debug.BuildInfo{Main: *module("(devel)")}, true, "0.0.0-dev"},
		{&debug.BuildInfo{Main: *module("v0.2.0")}, true, "v0.2.0"},
		{&debug.BuildInfo{Main: debug.Module{Path: "example.com/app"}, Deps: []*debug.Module{module("v0.3.1")}}, true, "v0.3.1"},
		{&debug.BuildInfo{Deps: []*debug.Module{{Path: modulePath, Version: "v0.0.0-00010101000000-000000000000",
			Replace: &debug.Module{Path: "../maf"}}}}, true, "v0.0.0-00010101000000-000000000000"},
		{&debug.BuildInfo{Deps: []*debug.Module{{Path: "other", Version: "v1.0.0"}}}, true, "0.0.0-dev"},
	} {
		if actual := moduleVersion(c.info, c.ok); actual != c.expected {
			t.Errorf("%+v: %q, want %q", c.info, actual, c.expected)
		}
	}
	if Version() == "" {
		t.Fatal("no version")
	}
}

// ---- Injection --------------------------------------------------------------------------------------------------

func TestTheBlockGoesBeforeTheNewInputAndNeverAfterIt(t *testing.T) {
	history := func(text string) *message.Message {
		return message.NewText(text).WithSource(message.Source{Type: agent.SourceTypeHistoryProvider, ID: "h"})
	}
	added := func(text string) *message.Message {
		return message.NewText(text).WithSource(message.Source{Type: agent.SourceTypeMiddleware})
	}
	for _, c := range []struct {
		messages []*message.Message
		expected int
	}{
		{[]*message.Message{message.NewText("new")}, 0},
		{[]*message.Message{history("a"), history("b"), message.NewText("new")}, 2},
		{[]*message.Message{history("a"), added("x"), message.NewText("new")}, 2},
		// No message from outside the pipeline after the history: right after the history, not at the end.
		{[]*message.Message{history("a"), added("x"), added("y")}, 1},
		{[]*message.Message{added("x")}, 0},
		{nil, 0},
	} {
		if actual := inputStart(c.messages); actual != c.expected {
			t.Errorf("%d, want %d", actual, c.expected)
		}
	}
}

func TestTheTaskIDIsTheOptionsElseTheAgentsNameElseDefault(t *testing.T) {
	for _, c := range []struct{ option, name, expected string }{
		{"refund-triage", "triage-ticket", "refund-triage"},
		{"", "Triage Ticket", "triage-ticket"},
		{"", "", DefaultTaskID},
		{"", "--- ★ ---", DefaultTaskID},
	} {
		h, model := newHost(t), newScripted(1)
		a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
			TaskID: c.option}), func(config *agent.Config) { config.Name = c.name })
		runText(t, a, ticket)
		if task := h.records()[0].TaskID; task != c.expected {
			t.Errorf("%+v: %q", c, task)
		}
	}
}

// lesson is a stored lesson of the first ticket's run, made by the integration itself.
func lesson(t *testing.T) ae.ExperienceRecord {
	t.Helper()
	h, model := newHost(t), newScripted(2)
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}))
	runText(t, a, ticket)
	return h.records()[0]
}

func storeOf(t *testing.T, records ...ae.ExperienceRecord) *inmemory.Store {
	t.Helper()
	store, err := inmemory.New(records...)
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func injected(t *testing.T, store ae.Store, options Options) (*host, *scripted) {
	t.Helper()
	h, model := newHost(t), newScripted(0)
	options.Store = store
	a := build(t, model, nil, h.options(options))
	runText(t, a, nextTicket)
	if len(h.injectionFailures) != 0 {
		t.Fatalf("%+v", h.injectionFailures)
	}
	return h, model
}

func TestExcludeModelAuthoredLeavesAModelWrittenLessonOut(t *testing.T) {
	authored := lesson(t)
	authored.Reflection.Authorship = ae.AuthorshipModel
	store := storeOf(t, authored)
	if h, _ := injected(t, store, Options{}); len(h.injections) != 1 {
		t.Fatal("the model-authored lesson was not injected without the option")
	}
	if h, model := injected(t, store, Options{ExcludeModelAuthored: true}); len(h.injections) != 0 ||
		len(model.blocks()) != 0 {
		t.Fatal("a model-authored lesson was injected")
	}
}

func TestThePreferredEnvironmentDecidesTheRank(t *testing.T) {
	eu, us := lesson(t), lesson(t)
	eu.ExperienceID, us.ExperienceID = ae.NewRandomUUID(), ae.NewRandomUUID()
	eu.Environment.Metadata = map[string]string{"region": "eu-west"}
	us.Environment.Metadata = map[string]string{"region": "us-east"}
	store := storeOf(t, eu, us)
	for region, first := range map[string]ae.UUID{"eu-west": eu.ExperienceID, "us-east": us.ExperienceID} {
		h, _ := injected(t, store, Options{PreferredEnvironment: map[string]string{"region": region}})
		if ids := h.injections[0].ExperienceIDs; len(ids) != 2 || ids[0] != first {
			t.Errorf("preferring %s ranked %v", region, ids)
		}
	}
}

func TestTheExposureRecordsTheRevisionDelivered(t *testing.T) {
	revised := lesson(t)
	revised.Revision = 3
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Store: storeOf(t, revised),
		Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a, nextTicket)
	exposed := h.records()[0].Provenance.ExposedTo
	if len(exposed) != 1 || exposed[0] != (ae.RunExposure{ExperienceID: revised.ExperienceID, Revision: 3}) {
		t.Fatalf("%+v", exposed)
	}
}

func TestTheInjectionResultSaysWhenTheCandidatesWereTruncated(t *testing.T) {
	base := lesson(t)
	var records []ae.ExperienceRecord
	for range ae.CandidateLimit + 1 {
		record := base
		record.ExperienceID = ae.NewRandomUUID()
		records = append(records, record)
	}
	h, _ := injected(t, storeOf(t, records...), Options{})
	if !h.injections[0].Truncated || len(h.injections[0].ExperienceIDs) == 0 {
		t.Fatalf("%+v", h.injections[0])
	}
	h, _ = injected(t, storeOf(t, records[:ae.CandidateLimit]...), Options{})
	if h.injections[0].Truncated {
		t.Fatal("50 candidates were truncated")
	}
}

// startedScope is a captured scope with a run open, as the capture middleware opens one.
func startedScope(t *testing.T, h *host) *captureScope {
	t.Helper()
	s, err := newSettings(h.options(Options{}))
	if err != nil {
		t.Fatal(err)
	}
	identity := s.Identity
	scope := &captureScope{settings: s, agent: agent.New(agent.ProviderConfig{Run: newScripted(0).run}, agent.Config{}),
		identity: &identity, reported: map[CaptureStage]bool{}}
	scope.start(context.Background(), []*message.Message{message.NewText(ticket)})
	if !scope.captured() {
		t.Fatal("the run did not start")
	}
	return scope
}

func TestAnExposureCaptureRefusesIsReportedAndOneAfterTheRunEndedIsIgnored(t *testing.T) {
	h := newHost(t)
	scope := startedScope(t, h)
	var many []ae.RunExposure
	for range ae.MaxExposuresPerRun + 1 {
		many = append(many, ae.RunExposure{ExperienceID: ae.NewRandomUUID(), Revision: 1})
	}
	scope.recordExposures(many)
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageRecordExposure}) ||
		!strings.Contains(h.captureFailures[0].Reason, string(ae.OutcomeCapacityExceeded)) {
		t.Fatalf("%+v", h.captureFailures)
	}

	h2 := newHost(t)
	finished := startedScope(t, h2)
	finished.finish(context.Background(), ae.RunCompleted, nil, nil)
	finished.recordExposures([]ae.RunExposure{{ExperienceID: ae.NewRandomUUID(), Revision: 1}})
	if len(h2.stages()) != 0 || len(finished.capture.Run().Provenance.ExposedTo) != 0 {
		t.Fatalf("%v", h2.stages())
	}
}

// ---- Abandoned finalization -----------------------------------------------------------------------------------------

type countingStore struct {
	*inmemory.Store
	inserts atomic.Int32
}

func (c *countingStore) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, f *ae.FinalizedExperience,
	actor string, at *time.Time) error {
	c.inserts.Add(1)
	return c.Store.InsertFinalized(ctx, bounds, f, actor, at)
}

func TestAnAbandonedFinalizationStoresNothingAfterItsBoundAndTellsNoOne(t *testing.T) {
	h := newHost(t)
	store := &countingStore{Store: h.store}
	slowVerify := func(context.Context, *ae.VerificationContext) (*bool, error) {
		time.Sleep(300 * time.Millisecond) // ignores its context, so it returns past the bound
		return yes(), nil
	}
	model := newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Store: store,
		Verify: ae.Check("c", slowVerify), FinalizationTimeout: 50 * time.Millisecond}))
	runText(t, a, ticket)
	time.Sleep(400 * time.Millisecond) // the abandoned work ends
	if store.inserts.Load() != 0 || len(h.finalized) != 0 {
		t.Fatalf("the abandoned work stored %d records", store.inserts.Load())
	}
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageFinalize}) ||
		!strings.Contains(h.captureFailures[0].Reason, "may still be stored") {
		t.Fatalf("%+v", h.captureFailures)
	}
}

// ---- Results and arguments ------------------------------------------------------------------------------------------

func TestResultsAreRecordedAsTextAndContentWithoutTextAsItsJSON(t *testing.T) {
	text := func(s string) *message.TextContent { return &message.TextContent{Text: s} }
	data := &message.FunctionCallContent{CallID: "c", Name: "n", Arguments: "{}"}
	encoded, err := data.MarshalJSON()
	if err != nil {
		t.Fatal(err)
	}
	s := "exit=3"
	for _, c := range []struct {
		result   any
		expected *string
	}{
		{data, ae.Ptr(string(encoded))},
		{[]message.Content{data}, ae.Ptr("[" + string(encoded) + "]")},
		{message.Contents{data, text("exit=0")}, ae.Ptr("exit=0")},
		{(*message.TextContent)(nil), nil},
		{&s, ae.Ptr("exit=3")},
		{(*string)(nil), nil},
	} {
		got := resultText(c.result)
		if (got == nil) != (c.expected == nil) || (got != nil && *got != *c.expected) {
			t.Errorf("resultText(%#v) = %v, want %v", c.result, got, c.expected)
		}
	}
	for _, c := range []struct {
		result   any
		expected any
	}{
		{&s, "exit=3"},
		{(*string)(nil), nil},
		{(*message.TextContent)(nil), nil},
		{data, data},
	} {
		if got := classifierResult(c.result); got != c.expected {
			t.Errorf("classifierResult(%#v) = %#v", c.result, got)
		}
	}
	if class, failed := ae.ExitCode()(ae.ToolResultContext{ToolName: "t", Result: classifierResult(&s)}); !failed ||
		class != "exit 3" {
		t.Errorf("%q %v", class, failed)
	}
}

func TestArgumentsThatAreNotAJSONObjectAreReported(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	model.arguments = func(int) string { return `["unit"]` }
	var called bool
	runTests := functool.MustNew(functool.Config{Name: "run_tests"}, func(context.Context, suite) (string, error) {
		called = true
		return "exit=0", nil
	})
	// The arguments reach the tool middleware before the tool validates them; the tool then refuses them.
	a := build(t, model, []tool.Tool{runTests}, h.options(Options{Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a, ticket)
	if called {
		t.Fatal("the tool accepted an array")
	}
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageToolCall}) {
		t.Fatalf("stages %v", h.stages())
	}
	call := h.records()[0].Attempts[0].ToolCalls[0]
	if call.Arguments.Len() != 0 || call.Error == nil {
		t.Fatalf("%+v", call)
	}
}
