package maf

// The Agent Framework integration, driven through a real agent.Agent with a scripted provider and the SDK's own
// automatic tool invocation (toolautocall), as the OpenAI and Foundry providers wire it. The cases are the Python
// port's (python/tests/test_maf.py), adapted to the Go SDK.

import (
	"context"
	"errors"
	"fmt"
	"iter"
	"reflect"
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

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
)

const (
	ticket     = "Ticket #4812: a refund is stuck on a lock. Triage it."
	nextTicket = "Ticket #4977: a refund is stuck on a lock again. Triage it."
)

var testScope = ae.Scope{TenantID: "contoso", ApplicationID: "support", ProjectID: "tickets"}

// ---- A scripted provider ----------------------------------------------------------------------------------------

// scripted calls tool (with arguments) calls times per run, one call per model turn (parallel calls in one turn
// when parallel is above 1), then answers "done". It keeps every request's messages, so a test can read what the
// model was sent.
type scripted struct {
	tool       string
	calls      int
	arguments  func(n int) string
	parallel   int
	concurrent bool

	mu       sync.Mutex
	requests [][]*message.Message
	counter  int
}

func newScripted(calls int) *scripted { return &scripted{tool: "run_tests", calls: calls} }

func (s *scripted) run(_ context.Context, messages []*message.Message,
	_ ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
	return func(yield func(*agent.ResponseUpdate, error) bool) {
		s.mu.Lock()
		s.requests = append(s.requests, slices.Clone(messages))
		lastUser := -1
		for i, m := range messages {
			if m.Role == message.RoleUser {
				lastUser = i
			}
		}
		made := 0
		for _, m := range messages[lastUser+1:] {
			for _, content := range m.Contents {
				if _, ok := content.(*message.FunctionCallContent); ok {
					made++
				}
			}
		}
		var contents message.Contents
		if made < s.calls {
			for range max(s.parallel, 1) {
				s.counter++
				arguments := "{}"
				if s.arguments != nil {
					arguments = s.arguments(s.counter)
				}
				contents = append(contents, &message.FunctionCallContent{CallID: fmt.Sprintf("call-%d", s.counter),
					Name: s.tool, Arguments: arguments})
			}
		} else {
			contents = message.Contents{&message.TextContent{Text: "done"}}
		}
		s.mu.Unlock()
		yield(&agent.ResponseUpdate{Role: message.RoleAssistant, Contents: contents}, nil)
	}
}

// blocks are the Historical Reference blocks of every request, in order.
func (s *scripted) blocks() []*message.Message {
	s.mu.Lock()
	defer s.mu.Unlock()
	var blocks []*message.Message
	for _, request := range s.requests {
		for _, m := range request {
			if isBlock(m) {
				blocks = append(blocks, m)
			}
		}
	}
	return blocks
}

// blockCounts is how many blocks each request carried.
func (s *scripted) blockCounts() []int {
	s.mu.Lock()
	defer s.mu.Unlock()
	counts := make([]int, len(s.requests))
	for i, request := range s.requests {
		for _, m := range request {
			if isBlock(m) {
				counts[i]++
			}
		}
	}
	return counts
}

// ---- The host ---------------------------------------------------------------------------------------------------

type host struct {
	mu                sync.Mutex
	captureFailures   []CaptureFailure
	injectionFailures []InjectionFailure
	injections        []InjectionResult
	nothing           []InjectionResult // OnInjected's NothingToInject reports
	finalized         []ae.FinalizedExperience
	store             *inmemory.Store
}

func newHost(t *testing.T) *host {
	t.Helper()
	store, err := inmemory.New()
	if err != nil {
		t.Fatal(err)
	}
	return &host{store: store}
}

func (h *host) options(o Options) Options {
	if o.Store == nil {
		o.Store = h.store
	}
	if o.ResolveIdentity == nil && o.Identity.Principal == "" {
		o.Identity = ae.Identity{Principal: "svc", Scope: testScope}
	}
	o.OnCaptureFailure = func(f CaptureFailure) { h.mu.Lock(); h.captureFailures = append(h.captureFailures, f); h.mu.Unlock() }
	o.OnInjectionFailure = func(f InjectionFailure) {
		h.mu.Lock()
		h.injectionFailures = append(h.injectionFailures, f)
		h.mu.Unlock()
	}
	o.OnInjected = func(r InjectionResult) {
		h.mu.Lock()
		defer h.mu.Unlock()
		if r.Outcome == "NothingToInject" {
			h.nothing = append(h.nothing, r)
			return
		}
		h.injections = append(h.injections, r)
	}
	o.OnFinalized = func(f ae.FinalizedExperience) { h.mu.Lock(); h.finalized = append(h.finalized, f); h.mu.Unlock() }
	return o
}

func (h *host) stages() []CaptureStage {
	h.mu.Lock()
	defer h.mu.Unlock()
	var stages []CaptureStage
	for _, failure := range h.captureFailures {
		stages = append(stages, failure.Stage)
	}
	return stages
}

func (h *host) records() []ae.ExperienceRecord {
	h.mu.Lock()
	defer h.mu.Unlock()
	records := make([]ae.ExperienceRecord, len(h.finalized))
	for i, f := range h.finalized {
		records[i] = f.Record
	}
	return records
}

type suite struct {
	Suite string `json:"suite,omitempty"`
}

// exits is a run_tests tool returning each of results in turn (the last one again once they run out).
func exits(results ...string) tool.FuncTool {
	var mu sync.Mutex
	n := 0
	return functool.MustNew(functool.Config{Name: "run_tests", Description: "Runs the tests."},
		func(context.Context, suite) (string, error) {
			mu.Lock()
			defer mu.Unlock()
			result := results[min(n, len(results)-1)]
			n++
			return result, nil
		})
}

func build(t *testing.T, model *scripted, tools []tool.Tool, options Options,
	configure ...func(*agent.Config)) *agent.Agent {
	t.Helper()
	config := agent.Config{Name: "triage-ticket", Tools: tools}
	for _, c := range configure {
		c(&config)
	}
	if err := UseAgentExperience(&config, options); err != nil {
		t.Fatal(err)
	}
	return newAgent(model, config)
}

func newAgent(model *scripted, config agent.Config) *agent.Agent {
	return agent.New(agent.ProviderConfig{ProviderName: "scripted", Run: model.run,
		Middlewares: []agent.Middleware{toolautocall.New(toolautocall.Config{
			AllowConcurrentInvocations: model.concurrent})}}, config)
}

func runText(t *testing.T, a *agent.Agent, text string, options ...agent.Option) *agent.Response {
	t.Helper()
	response, err := a.RunText(context.Background(), text, options...).Collect()
	if err != nil {
		t.Fatal(err)
	}
	return response
}

type callView struct {
	Tool, Result, Error string
}

func view(call ae.ToolCall) callView {
	text := func(s *string) string {
		if s == nil {
			return "<nil>"
		}
		return *s
	}
	return callView{call.ToolName, text(call.Result), text(call.Error)}
}

func calls(record ae.ExperienceRecord) []callView {
	var views []callView
	for _, call := range record.Attempts[0].ToolCalls {
		views = append(views, view(call))
	}
	return views
}

// seen is a host check that records what it was shown: the run, and how long it takes to decide.
type seen struct {
	mu     sync.Mutex
	runs   []ae.CapturedRun
	passed *bool
	delay  time.Duration
}

func (s *seen) check(ctx context.Context, verification *ae.VerificationContext) (*bool, error) {
	s.mu.Lock()
	s.runs = append(s.runs, verification.Run)
	s.mu.Unlock()
	if s.delay > 0 {
		select {
		case <-time.After(s.delay):
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	return s.passed, nil
}

func (s *seen) only(t *testing.T) ae.CapturedRun {
	t.Helper()
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.runs) != 1 {
		t.Fatalf("%d runs were verified", len(s.runs))
	}
	return s.runs[0]
}

func yes() *bool { v := true; return &v }

// ---- Capture, verification, storage and injection ----------------------------------------------------------------

func TestTwoRunsTheSecondGetsTheLessonNamingBothCalls(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}))

	runText(t, a, ticket)
	if len(h.captureFailures) != 0 || len(h.finalized) != 1 {
		t.Fatalf("failures %+v, finalized %d", h.captureFailures, len(h.finalized))
	}
	first := h.finalized[0].Record
	if first.Status != ae.StatusValidated || first.TaskID != "triage-ticket" || *first.TaskSummary != ticket {
		t.Fatalf("%+v", first)
	}
	expected := []callView{{"run_tests", "exit=3", "exit 3"}, {"run_tests", "exit=0", "<nil>"}}
	if !reflect.DeepEqual(calls(first), expected) {
		t.Fatalf("calls %+v", calls(first))
	}
	if len(model.blocks()) != 0 {
		t.Fatal("the first run was handed a block")
	}

	runText(t, a, nextTicket)
	if len(h.injectionFailures) != 0 || len(h.injections) != 1 ||
		!reflect.DeepEqual(h.injections[0].ExperienceIDs, []ae.UUID{first.ExperienceID}) {
		t.Fatalf("injections %+v, failures %+v", h.injections, h.injectionFailures)
	}
	// Each of the second run's three requests carries the one block, as a user message just before the ticket.
	if counts := model.blockCounts(); !reflect.DeepEqual(counts, []int{0, 0, 0, 1, 1, 1}) {
		t.Fatalf("block counts %v", counts)
	}
	request := model.requests[3]
	if len(request) != 2 || !isBlock(request[0]) || request[1].String() != nextTicket {
		t.Fatalf("the request was %v", request)
	}
	block := request[0]
	if block.Role != message.RoleUser || block.Source != (message.Source{Type: agent.SourceTypeContextProvider, ID: SourceID}) ||
		!strings.Contains(block.String(), "run_tests [failed: exit 3], run_tests [returned]") {
		t.Fatalf("block %+v: %s", block, block.String())
	}
	// The second run was told what it was shown.
	second := h.finalized[1].Record
	if len(second.Provenance.ExposedTo) != 1 || second.Provenance.ExposedTo[0].ExperienceID != first.ExperienceID {
		t.Fatalf("exposures %+v", second.Provenance.ExposedTo)
	}
	if *second.Provenance.SourceVersion != Version() || second.Provenance.Source != ProvenanceSource {
		t.Fatalf("provenance %+v", second.Provenance)
	}
}

type brokenStore struct {
	*inmemory.Store
	search, insert error
}

func (b brokenStore) Search(ctx context.Context, bounds ae.AuthorizationBounds, p ae.SearchParameters) ([]ae.SearchRow, error) {
	if b.search != nil {
		return nil, b.search
	}
	return b.Store.Search(ctx, bounds, p)
}

func (b brokenStore) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, f *ae.FinalizedExperience,
	actor string, at *time.Time) error {
	if b.insert != nil {
		return b.insert
	}
	return b.Store.InsertFinalized(ctx, bounds, f, actor, at)
}

func TestAStoreFailingDuringInjectionLeavesTheAgentRunningWithoutABlock(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	down := errors.New("the database is down")
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{
		Store: brokenStore{Store: h.store, search: down}, Verify: ae.ToolSucceeded("run_tests")}))
	response := runText(t, a, ticket)
	if response.String() != "done" || len(model.blocks()) != 0 {
		t.Fatalf("response %q", response.String())
	}
	if len(h.injectionFailures) != 1 || !errors.Is(h.injectionFailures[0].Err, down) ||
		strings.Contains(h.injectionFailures[0].Reason, "down") {
		t.Fatalf("failures %+v", h.injectionFailures)
	}
	// Capture is unaffected.
	if len(h.finalized) != 1 {
		t.Fatalf("finalized %d", len(h.finalized))
	}

	// A store that panics is no different.
	h2, model2 := newHost(t), newScripted(0)
	a2 := build(t, model2, nil, h2.options(Options{Store: panickingStore{}}))
	if runText(t, a2, ticket).String() != "done" || len(h2.injectionFailures) != 1 {
		t.Fatalf("failures %+v", h2.injectionFailures)
	}
}

type panickingStore struct{}

func (panickingStore) InsertFinalized(context.Context, ae.AuthorizationBounds, *ae.FinalizedExperience, string,
	*time.Time) error {
	panic("insert")
}

func (panickingStore) Search(context.Context, ae.AuthorizationBounds, ae.SearchParameters) ([]ae.SearchRow, error) {
	panic("search")
}

func TestAStoreFailingToStoreIsReportedAndNothingElse(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{
		Store: brokenStore{Store: h.store, insert: errors.New("disk full")}, Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a, ticket)
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageStore}) || len(h.finalized) != 0 {
		t.Fatalf("stages %v", h.stages())
	}
	h2, model2 := newHost(t), newScripted(1)
	a2 := build(t, model2, []tool.Tool{exits("exit=0")}, h2.options(Options{
		Store: panickingStore{}, Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a2, ticket)
	if !reflect.DeepEqual(h2.stages(), []CaptureStage{StageStore}) {
		t.Fatalf("stages %v", h2.stages())
	}
}

func TestExit3WithTheExitCodeClassifierIsRecordedAsExit3(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=3")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}))
	runText(t, a, ticket)
	record := h.records()[0]
	if !reflect.DeepEqual(calls(record), []callView{{"run_tests", "exit=3", "exit 3"}}) ||
		record.Status != ae.StatusQuarantined {
		t.Fatalf("%+v %s", calls(record), record.Status)
	}
}

func TestAClassThatIsNotOneTokenIsRecordedAsToolReportedErrorAndReportedOnce(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	a := build(t, model, []tool.Tool{exits("boom")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: func(ae.ToolResultContext) (string, bool) { return "it broke", true }}))
	runText(t, a, ticket)
	expected := []callView{{"run_tests", "boom", ae.ToolReportedErrorClass}, {"run_tests", "boom", ae.ToolReportedErrorClass}}
	if !reflect.DeepEqual(calls(h.records()[0]), expected) {
		t.Fatalf("%+v", calls(h.records()[0]))
	}
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageToolResultClassification}) {
		t.Fatalf("stages %v", h.stages())
	}
}

func TestWhiteSpaceAroundAClassIsTrimmedAndAPanickingClassifierLeavesTheCallReturned(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("x")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: func(ae.ToolResultContext) (string, bool) { return "  exit 4\n", true }}))
	runText(t, a, ticket)
	if !reflect.DeepEqual(calls(h.records()[0]), []callView{{"run_tests", "x", "exit 4"}}) || len(h.stages()) != 0 {
		t.Fatalf("%+v %v", calls(h.records()[0]), h.stages())
	}

	h2, model2 := newHost(t), newScripted(1)
	a2 := build(t, model2, []tool.Tool{exits("x")}, h2.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: func(ae.ToolResultContext) (string, bool) { panic("classifier bug") }}))
	runText(t, a2, ticket)
	if !reflect.DeepEqual(calls(h2.records()[0]), []callView{{"run_tests", "x", "<nil>"}}) ||
		!reflect.DeepEqual(h2.stages(), []CaptureStage{StageToolResultClassification}) {
		t.Fatalf("%+v %v", calls(h2.records()[0]), h2.stages())
	}
}

func TestTheClassifierSeesTheNameTheRawArgumentsAndTheRawResult(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	model.arguments = func(int) string { return `{"suite":"unit","token":"s3cret"}` }
	type outcome struct {
		ExitCode int `json:"exitCode"`
	}
	runTests := functool.MustNew(functool.Config{Name: "run_tests"},
		func(context.Context, struct {
			Suite string `json:"suite,omitempty"`
			Token string `json:"token,omitempty"`
		}) (outcome, error) {
			return outcome{2}, nil
		})
	var seenContext ae.ToolResultContext
	a := build(t, model, []tool.Tool{runTests}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		Sanitization: ae.SanitizationAllowing("suite"),
		ClassifyToolResult: func(c ae.ToolResultContext) (string, bool) {
			seenContext = c
			return ae.ExitCode()(c)
		}}))
	runText(t, a, ticket)
	if seenContext.ToolName != "run_tests" || seenContext.Result != (outcome{2}) {
		t.Fatalf("%+v", seenContext)
	}
	if token, _ := seenContext.Arguments.Get("token"); token != "s3cret" {
		t.Fatalf("the classifier saw arguments %v", seenContext.Arguments)
	}
	record := h.records()[0]
	call := record.Attempts[0].ToolCalls[0]
	// What is stored is sanitized: only the allowlisted value is kept, and the result is the tool's JSON.
	if suite, _ := call.Arguments.Get("suite"); suite != "unit" || fmt.Sprint(call.Arguments.Get("token")) == "s3cret true" ||
		*call.Result != `{"exitCode":2}` || *call.Error != "exit 2" {
		t.Fatalf("%v %s %s", call.Arguments, *call.Result, *call.Error)
	}
}

type lockHeldError struct{}

func (lockHeldError) Error() string { return "the refund lock is held by job 42 (secret detail)" }

func TestAToolReturningAnErrorIsRecordedByItsTypeNameOnlyAndTheRunIsQuarantined(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	runTests := functool.MustNew(functool.Config{Name: "run_tests"},
		func(context.Context, suite) (string, error) { return "", fmt.Errorf("running: %w", lockHeldError{}) })
	a := build(t, model, []tool.Tool{runTests}, h.options(Options{Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a, ticket)
	record := h.records()[0]
	if !reflect.DeepEqual(calls(record), []callView{{"run_tests", "<nil>",
		"github.com/fabbrik/AgentExperience.NET/go/maf.lockHeldError"}}) || record.Status != ae.StatusQuarantined {
		t.Fatalf("%+v %s", calls(record), record.Status)
	}
	if record.Reflection != nil && strings.Contains(record.Reflection.Lesson, "secret") {
		t.Fatal("the error's message reached the lesson")
	}
}

func TestAToolThatPanicsIsRecordedAndThePanicGoesOn(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	runTests := functool.MustNew(functool.Config{Name: "run_tests"},
		func(context.Context, suite) (string, error) { panic(errors.New("index out of range")) })
	a := build(t, model, []tool.Tool{runTests}, h.options(Options{Verify: ae.ToolSucceeded("run_tests")}))
	// The SDK's automatic tool invocation recovers a tool's panic into an error result; the run goes on.
	runText(t, a, ticket)
	if got := calls(h.records()[0]); !reflect.DeepEqual(got, []callView{{"run_tests", "<nil>",
		"panic(errors.errorString)"}}) {
		t.Fatalf("%+v", got)
	}
}

func TestARunThatNeverCalledTheToolIsNotStoredAndNothingIsReported(t *testing.T) {
	h, model := newHost(t), newScripted(0)
	a := build(t, model, nil, h.options(Options{Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a, ticket)
	if len(h.finalized) != 0 || len(h.stages()) != 0 || len(h.store.Records()) != 0 {
		t.Fatal("something was stored or reported")
	}
}

func TestWithoutVerifyNothingIsStored(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{}))
	runText(t, a, ticket)
	if len(h.finalized) != 0 || len(h.store.Records()) != 0 || len(h.stages()) != 0 {
		t.Fatal("something was stored or reported")
	}
}

func TestAVerifyThatFailsOrReturnsForeignEvidenceIsReportedAndNotStored(t *testing.T) {
	for name, verify := range map[string]ae.Verifier{
		"error": func(context.Context, *ae.VerificationContext) (*ae.Verdict, error) {
			return nil, errors.New("the CI is unreachable")
		},
		"panic": func(context.Context, *ae.VerificationContext) (*ae.Verdict, error) { panic("verifier bug") },
		"foreign evidence": func(_ context.Context, c *ae.VerificationContext) (*ae.Verdict, error) {
			evidence := c.CreateEvidence("c", ae.HostCheckKind, ae.CheckPass, "host", "rev-1", nil)
			evidence.VerificationRoundID = ae.NewRandomUUID()
			return &ae.Verdict{RequiredChecks: []ae.RequiredCheck{{CheckID: "c", ExpectedKind: ae.HostCheckKind}},
				Evidence: []ae.Evidence{evidence}, ArtifactRevision: "rev-1"}, nil
		},
		"another revision": func(_ context.Context, c *ae.VerificationContext) (*ae.Verdict, error) {
			evidence := c.CreateEvidence("c", ae.HostCheckKind, ae.CheckPass, "host", "rev-1", nil)
			return &ae.Verdict{RequiredChecks: []ae.RequiredCheck{{CheckID: "c", ExpectedKind: ae.HostCheckKind}},
				Evidence: []ae.Evidence{evidence}, ArtifactRevision: "rev-2"}, nil
		},
	} {
		t.Run(name, func(t *testing.T) {
			h, model := newHost(t), newScripted(1)
			a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: verify}))
			runText(t, a, ticket)
			if !reflect.DeepEqual(h.stages(), []CaptureStage{StageVerify}) || len(h.store.Records()) != 0 {
				t.Fatalf("stages %v", h.stages())
			}
		})
	}
}

func TestAHostCheckStoresALesson(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{
		Verify: ae.Check("refund-released", func(context.Context, *ae.VerificationContext) (*bool, error) {
			return yes(), nil
		})}))
	runText(t, a, ticket)
	if records := h.store.Records(); len(records) != 1 || records[0].Status != ae.StatusValidated {
		t.Fatalf("%+v", records)
	}
}

// ---- Wiring -----------------------------------------------------------------------------------------------------

func TestAnAgentThatAlreadyHoldsAProviderGetsExactlyOneBlock(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	own, err := NewContextProvider(h.options(Options{}))
	if err != nil {
		t.Fatal(err)
	}
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}),
		func(c *agent.Config) { c.ContextProviders = []agent.ContextProvider{own} })
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	if counts := model.blockCounts(); !reflect.DeepEqual(counts, []int{0, 0, 0, 1, 1, 1}) {
		t.Fatalf("block counts %v", counts)
	}
	// The hand-wired provider has its own settings: it resolved its own identity, and recorded no exposure.
	if exposed := h.finalized[1].Record.Provenance.ExposedTo; len(exposed) != 0 {
		t.Fatalf("exposures %+v", exposed)
	}
}

func TestCallingTheSetupTwiceIsRefusedAndInvalidOptionsAreErrors(t *testing.T) {
	h := newHost(t)
	config := agent.Config{Name: "triage-ticket"}
	if err := UseAgentExperience(&config, h.options(Options{})); err != nil {
		t.Fatal(err)
	}
	if err := UseAgentExperience(&config, h.options(Options{})); !errors.Is(err, ErrAlreadyWired) {
		t.Fatalf("a second call gave %v", err)
	}
	if len(config.Middlewares) != 1 || len(config.FunctionMiddlewares) != 1 || len(config.ContextProviders) != 1 {
		t.Fatalf("%+v", config)
	}
	identity := ae.Identity{Principal: "svc", Scope: testScope}
	resolver := func(context.Context, IdentityContext) (*ae.Identity, error) { return nil, nil }
	for name, options := range map[string]Options{
		"no store":           {Identity: identity},
		"no identity":        {Store: h.store},
		"both identities":    {Store: h.store, Identity: identity, ResolveIdentity: resolver},
		"a blank principal":  {Store: h.store, Identity: ae.Identity{Principal: " ", Scope: testScope}},
		"a blank task ID":    {Store: h.store, Identity: identity, TaskID: " "},
		"an unknown role":    {Store: h.store, Identity: identity, MessageRole: message.RoleTool},
		"a negative timeout": {Store: h.store, Identity: identity, FinalizationTimeout: -time.Second},
		"bad block options":  {Store: h.store, Identity: identity, BlockOptions: ae.BlockOptions{MaxRecords: -1}},
	} {
		if err := UseAgentExperience(&agent.Config{}, options); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	if err := UseAgentExperience(nil, h.options(Options{})); err == nil {
		t.Error("a nil configuration was accepted")
	}
}

func TestDisableInjectionCapturesWithoutInjecting(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{DisableInjection: true,
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}))
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	if len(model.blocks()) != 0 || len(h.finalized) != 2 {
		t.Fatalf("blocks %d, finalized %d", len(model.blocks()), len(h.finalized))
	}
}

func TestNoIdentityMeansNeitherCapturedNorInjected(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	var resolved int
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ResolveIdentity: func(context.Context, IdentityContext) (*ae.Identity, error) {
			resolved++
			return nil, nil
		}}))
	runText(t, a, ticket)
	if resolved != 1 || len(h.finalized) != 0 || len(h.stages()) != 0 || len(h.injections) != 0 {
		t.Fatalf("resolved %d times", resolved)
	}
}

func TestAnIdentityResolverScopesWhatIsStoredAndWhatIsRead(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	tenant := "acme"
	var contexts []IdentityContext
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests"),
		ResolveIdentity: func(_ context.Context, c IdentityContext) (*ae.Identity, error) {
			contexts = append(contexts, c)
			scope := testScope
			scope.TenantID = tenant
			return &ae.Identity{Principal: "user-" + tenant, Scope: scope}, nil
		}}))
	runText(t, a, ticket)
	tenant = "globex"
	runText(t, a, nextTicket) // another tenant: nothing to read
	tenant = "acme"
	runText(t, a, nextTicket) // the first tenant again: its lesson
	if len(contexts) != 3 || contexts[0].Agent != a || contexts[0].Session == nil {
		t.Fatalf("contexts %+v", contexts)
	}
	records := h.records()
	if records[0].Scope.TenantID != "acme" || records[1].Scope.TenantID != "globex" || len(h.injections) != 1 ||
		h.injections[0].ExperienceIDs[0] != records[0].ExperienceID {
		t.Fatalf("records %+v, injections %+v", records, h.injections)
	}
}

func TestAnIdentityResolverThatFailsIsReportedAndTheAgentRuns(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ResolveIdentity: func(context.Context, IdentityContext) (*ae.Identity, error) {
			return nil, errors.New("the token expired")
		}}))
	if runText(t, a, ticket).String() != "done" {
		t.Fatal("the agent did not run")
	}
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageResolveIdentity}) || len(h.finalized) != 0 {
		t.Fatalf("stages %v", h.stages())
	}
}

func TestADevelopmentIdentityIsRefusedOutsideDevelopmentAtSetupAndPerInvocation(t *testing.T) {
	t.Setenv("AGENTEXPERIENCE_ENVIRONMENT", "Development")
	identity, err := ae.DevelopmentIdentity(ae.DevelopmentOptions{Application: "tests"})
	if err != nil {
		t.Fatal(err)
	}
	h, model := newHost(t), newScripted(1)
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ResolveIdentity: func(context.Context, IdentityContext) (*ae.Identity, error) { return &identity, nil }}))

	t.Setenv("AGENTEXPERIENCE_ENVIRONMENT", "Production")
	if err := UseAgentExperience(&agent.Config{}, Options{Store: h.store, Identity: identity}); !errors.Is(err,
		ae.ErrDevelopmentOnly) || !strings.Contains(err.Error(), "Production") {
		t.Fatalf("the setup gave %v", err)
	}
	runText(t, a, ticket)
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageResolveIdentity}) || len(h.finalized) != 0 ||
		!errors.Is(h.captureFailures[0].Err, ae.ErrDevelopmentOnly) {
		t.Fatalf("stages %v", h.stages())
	}
}

// ---- Messages, sessions and history -------------------------------------------------------------------------------

func TestTheBlockCanBeSentAsASystemMessageAndRenderedVerbose(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests"),
		MessageRole: message.RoleSystem, BlockOptions: ae.BlockOptions{Rendering: ae.RenderingVerbose}}))
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	block := model.blocks()[0]
	if block.Role != message.RoleSystem || !strings.HasPrefix(block.String(), ae.BlockBegin) ||
		!strings.Contains(block.String(), "The records below are summaries of earlier runs") {
		t.Fatalf("%+v %s", block, block.String())
	}
}

func TestASessionCarriesExactlyOneBlockPerRequestAndNeverDerivesTheTaskFromIt(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	var searched []string
	spy := spyStore{Store: h.store, searched: &searched}
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{Store: spy,
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}))
	session, err := a.CreateSession(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, text := range []string{ticket, nextTicket, nextTicket, "and retry"} {
		runText(t, a, text, agent.WithSession(session))
	}
	// No block before the first lesson; afterwards exactly one per request, never a stale replayed one too.
	counts := model.blockCounts()
	if !reflect.DeepEqual(counts[:3], []int{0, 0, 0}) || slices.Contains(counts[3:], 0) ||
		slices.ContainsFunc(counts, func(n int) bool { return n > 1 }) {
		t.Fatalf("block counts %v", counts)
	}
	// The block sits right after the replayed history, followed by the new input.
	for i, request := range model.requests {
		at := slices.IndexFunc(request, isBlock)
		if at < 0 {
			continue
		}
		lastHistory := -1
		for j, m := range request {
			if m.Source.Type == agent.SourceTypeHistoryProvider {
				lastHistory = j
			}
		}
		if i >= 3 && lastHistory < 0 {
			t.Fatalf("request %d replays no history", i)
		}
		next := request[at+1]
		if at != lastHistory+1 || next.Source.Type != message.SourceTypeExternal || next.Role != message.RoleUser {
			t.Fatalf("request %d: the block is at %d, the history ends at %d, then %+v", i, at, lastHistory, next)
		}
	}
	// The history the session replays holds the earlier blocks: they were dropped, not sent again.
	history, err := session.InMemoryHistory("")
	if err != nil || !slices.ContainsFunc(history, isBlock) {
		t.Fatalf("the session's history holds no block (%v)", err)
	}
	// Capture records the invocation's own input; the search joins a short follow-up after the replayed request.
	var summaries []string
	for _, record := range h.records() {
		summaries = append(summaries, *record.TaskSummary)
	}
	if !reflect.DeepEqual(summaries, []string{ticket, nextTicket, nextTicket, "and retry"}) {
		t.Fatalf("summaries %q", summaries)
	}
	// The search sees the history the SDK replays before the context providers: a request shorter than the
	// follow-up threshold (64) is joined after the previous one, as .NET's ChatClientAgent-level provider joins it.
	if !reflect.DeepEqual(searched, []string{ticket, ticket + " — " + nextTicket, nextTicket + " — " + nextTicket,
		nextTicket + " — and retry"}) {
		t.Fatalf("searched %q", searched)
	}
	for _, text := range searched {
		if strings.Contains(text, "HISTORICAL REFERENCE") {
			t.Fatal("the block was searched for")
		}
	}
}

type spyStore struct {
	*inmemory.Store
	searched *[]string
}

func (s spyStore) Search(ctx context.Context, bounds ae.AuthorizationBounds, p ae.SearchParameters) ([]ae.SearchRow, error) {
	*s.searched = append(*s.searched, p.TaskText)
	return s.Store.Search(ctx, bounds, p)
}

func TestAStreamingInvocationIsInjectedAndCaptured(t *testing.T) {
	h, model := newHost(t), newScripted(2)
	a := build(t, model, []tool.Tool{exits("exit=3", "exit=0")}, h.options(Options{
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}))
	for _, text := range []string{ticket, nextTicket} {
		for _, err := range a.RunText(context.Background(), text, agent.Stream(true)) {
			if err != nil {
				t.Fatal(err)
			}
		}
	}
	if len(h.finalized) != 2 || len(model.blocks()) != 3 {
		t.Fatalf("finalized %d, blocks %d", len(h.finalized), len(model.blocks()))
	}
	if !reflect.DeepEqual(calls(h.records()[0]), []callView{{"run_tests", "exit=3", "exit 3"},
		{"run_tests", "exit=0", "<nil>"}}) {
		t.Fatalf("%+v", calls(h.records()[0]))
	}
}

// ---- The run's end (as .NET's capture middleware and CaptureScope) -------------------------------------------------

func TestAnInvocationThatFailsEndsItsRunFailedAndTheErrorReachesTheCaller(t *testing.T) {
	h := newHost(t)
	checked := &seen{passed: yes()}
	failure := errors.New("the provider is down")
	config := agent.Config{Name: "triage-ticket"}
	if err := UseAgentExperience(&config, h.options(Options{Verify: ae.Check("c", checked.check)})); err != nil {
		t.Fatal(err)
	}
	a := agent.New(agent.ProviderConfig{Run: func(context.Context, []*message.Message,
		...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
		return func(yield func(*agent.ResponseUpdate, error) bool) { yield(nil, failure) }
	}}, config)
	if _, err := a.RunText(context.Background(), ticket).Collect(); !errors.Is(err, failure) {
		t.Fatalf("the caller got %v", err)
	}
	run := checked.only(t)
	if run.ExecutionStatus != ae.RunFailed || *run.Attempts[0].Error != "errors.errorString" {
		t.Fatalf("%s %v", run.ExecutionStatus, run.Attempts[0].Error)
	}
	// A failed run is verified, and stored when the check decides.
	if len(h.store.Records()) != 1 {
		t.Fatal("the failed run was not stored")
	}
}

func TestACancelledInvocationEndsCancelledRecordsTheCancelledCallAndIsNotHeldUp(t *testing.T) {
	scenario := func(t *testing.T, checked *seen) (*host, time.Duration) {
		h, model := newHost(t), newScripted(1)
		started := make(chan struct{})
		slow := functool.MustNew(functool.Config{Name: "run_tests"}, func(ctx context.Context, _ suite) (string, error) {
			close(started)
			select {
			case <-ctx.Done():
				return "", ctx.Err()
			case <-time.After(30 * time.Second):
				return "exit=0", nil
			}
		})
		a := build(t, model, []tool.Tool{slow}, h.options(Options{Verify: ae.Check("c", checked.check),
			FinalizationTimeout: 200 * time.Millisecond}))
		ctx, cancel := context.WithCancel(context.Background())
		var cancelledAt time.Time
		go func() {
			<-started
			cancelledAt = time.Now()
			cancel()
		}()
		_, err := a.RunText(ctx, ticket).Collect()
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("the caller got %v", err)
		}
		return h, time.Since(cancelledAt)
	}

	checked := &seen{passed: yes()}
	scenario(t, checked)
	run := checked.only(t)
	if run.ExecutionStatus != ae.RunCancelled || *run.Attempts[0].Error != CanceledError ||
		!reflect.DeepEqual(calls(ae.ExperienceRecord{Attempts: run.Attempts}),
			[]callView{{"run_tests", "<nil>", CanceledError}}) {
		t.Fatalf("%s %+v", run.ExecutionStatus, run.Attempts)
	}

	// A finalization slower than its bound does not hold the cancellation up: it is abandoned and reported.
	h, took := scenario(t, &seen{passed: yes(), delay: 5 * time.Second})
	if took > 2*time.Second {
		t.Fatalf("the cancellation was held up %v", took)
	}
	if !reflect.DeepEqual(h.stages(), []CaptureStage{StageFinalize}) || len(h.finalized) != 0 {
		t.Fatalf("stages %v", h.stages())
	}
}

func TestACallerThatStopsReadingEndsTheRunCancelled(t *testing.T) {
	h := newHost(t)
	checked := &seen{passed: yes()}
	config := agent.Config{Name: "triage-ticket"}
	if err := UseAgentExperience(&config, h.options(Options{Verify: ae.Check("c", checked.check)})); err != nil {
		t.Fatal(err)
	}
	a := agent.New(agent.ProviderConfig{Run: func(context.Context, []*message.Message,
		...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
		return func(yield func(*agent.ResponseUpdate, error) bool) {
			for _, text := range []string{"one", "two", "three"} {
				if !yield(&agent.ResponseUpdate{Role: message.RoleAssistant,
					Contents: message.Contents{&message.TextContent{Text: text}}}, nil) {
					return
				}
			}
		}
	}}, config)
	for range a.RunText(context.Background(), ticket, agent.Stream(true)) {
		break
	}
	if run := checked.only(t); run.ExecutionStatus != ae.RunCancelled || *run.Attempts[0].Error != StoppedError {
		t.Fatalf("%s", run.ExecutionStatus)
	}
}

func TestAnAgentMiddlewareAnsweringItselfEndsTheRunCompletedWithItsResult(t *testing.T) {
	h, model := newHost(t), newScripted(0)
	checked := &seen{passed: yes()}
	stop := agent.MiddlewareFunc(func(_ agent.RunFunc, _ context.Context, _ []*message.Message,
		_ ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
		return func(yield func(*agent.ResponseUpdate, error) bool) {
			yield(&agent.ResponseUpdate{Role: message.RoleAssistant,
				Contents: message.Contents{&message.TextContent{Text: "stopped by policy"}}}, nil)
		}
	})
	a := build(t, model, nil, h.options(Options{Verify: ae.Check("c", checked.check)}),
		func(c *agent.Config) { c.Middlewares = []agent.Middleware{stop} })
	runText(t, a, ticket)
	run := checked.only(t)
	if run.ExecutionStatus != ae.RunCompleted || *run.Attempts[0].Result != "stopped by policy" ||
		run.Attempts[0].Error != nil {
		t.Fatalf("%+v", run.Attempts[0])
	}
}

func TestAFunctionMiddlewareReplacingACallIsRecordedWithItsResult(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	checked := &seen{passed: yes()}
	pause := func(_ func(context.Context, *agent.FunctionInvocationContext) (any, error), _ context.Context,
		_ *agent.FunctionInvocationContext) (any, error) {
		return "tool calls are paused", nil
	}
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Verify: ae.Check("c", checked.check)}),
		func(c *agent.Config) { c.FunctionMiddlewares = []agent.FunctionInvocationMiddleware{pause} })
	runText(t, a, ticket)
	if got := calls(ae.ExperienceRecord{Attempts: checked.only(t).Attempts}); !reflect.DeepEqual(got,
		[]callView{{"run_tests", "tool calls are paused", "<nil>"}}) {
		t.Fatalf("%+v", got)
	}
}

func TestParallelCallsAreStoredInStartOrderAndTheLastStartedDecides(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	model.parallel, model.concurrent = 2, true
	model.arguments = func(n int) string {
		if n%2 == 1 {
			return `{"suite":"slow"}`
		}
		return `{"suite":"fast"}`
	}
	var mu sync.Mutex
	var finished []string
	runTests := functool.MustNew(functool.Config{Name: "run_tests"}, func(_ context.Context, in suite) (string, error) {
		if in.Suite == "slow" {
			time.Sleep(100 * time.Millisecond)
		}
		mu.Lock()
		finished = append(finished, in.Suite)
		mu.Unlock()
		if in.Suite == "slow" {
			return "exit=3", nil
		}
		return "exit=0", nil
	})
	// The calls start in the provider's order; make sure the slow one has started first.
	ordered := func(next func(context.Context, *agent.FunctionInvocationContext) (any, error), ctx context.Context,
		invocation *agent.FunctionInvocationContext) (any, error) {
		if strings.Contains(invocation.Arguments, "fast") {
			time.Sleep(20 * time.Millisecond)
		}
		return next(ctx, invocation)
	}
	config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{runTests}}
	if err := UseAgentExperience(&config, h.options(Options{Verify: ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: ae.ExitCode("run_tests"), Sanitization: ae.SanitizationAllowing("suite")})); err != nil {
		t.Fatal(err)
	}
	// Outside capture's own middleware, so capture sees the slow call start first.
	config.FunctionMiddlewares = append([]agent.FunctionInvocationMiddleware{ordered}, config.FunctionMiddlewares...)
	a := newAgent(model, config)
	runText(t, a, ticket)

	if !reflect.DeepEqual(finished, []string{"fast", "slow"}) {
		t.Fatalf("finished %v: the calls did not overlap", finished)
	}
	record := h.records()[0]
	var got []string
	for _, call := range record.Attempts[0].ToolCalls {
		suite, _ := call.Arguments.Get("suite")
		got = append(got, fmt.Sprintf("%d %v %s", call.SequenceNumber, suite, view(call).Error))
	}
	if !reflect.DeepEqual(got, []string{"0 slow exit 3", "1 fast <nil>"}) || record.Status != ae.StatusValidated {
		t.Fatalf("%v %s", got, record.Status)
	}
}

func TestToolResultsAreRecordedAsText(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	runTests := functool.MustNew(functool.Config{Name: "run_tests"},
		func(context.Context, suite) (map[string]int, error) { return map[string]int{"exitCode": 0}, nil })
	a := build(t, model, []tool.Tool{runTests}, h.options(Options{Verify: ae.ToolSucceeded("run_tests")}))
	runText(t, a, ticket)
	if result := *h.records()[0].Attempts[0].ToolCalls[0].Result; result != `{"exitCode":0}` {
		t.Fatalf("result %s", result)
	}
	text := func(s string) *message.TextContent { return &message.TextContent{Text: s} }
	for _, c := range []struct {
		result   any
		expected string
	}{
		{message.Contents{text("exit=0")}, "exit=0"},
		{[]message.Content{text("running"), text("exit=3")}, "running\nexit=3"},
		{text("ok"), "ok"},
		{[]message.Content{}, "[]"},
		{[]int{}, "[]"},
		{"plain", "plain"},
	} {
		if got := resultText(c.result); got == nil || *got != c.expected {
			t.Errorf("resultText(%#v) = %v, want %q", c.result, got, c.expected)
		}
	}
	if resultText(nil) != nil {
		t.Error("no result was recorded as text")
	}
	// The classifier reads framework content as its text.
	if class, failed := ae.ExitCode()(ae.ToolResultContext{ToolName: "t",
		Result: classifierResult(message.Contents{text("running"), text("exit=3")})}); !failed || class != "exit 3" {
		t.Errorf("%q %v", class, failed)
	}
}

func TestFinalizationIsBoundedAndReportedWhenAbandoned(t *testing.T) {
	h, model := newHost(t), newScripted(1)
	slowStore := slowStore{Store: h.store, delay: 2 * time.Second}
	a := build(t, model, []tool.Tool{exits("exit=0")}, h.options(Options{Store: slowStore,
		Verify: ae.ToolSucceeded("run_tests"), FinalizationTimeout: 100 * time.Millisecond}))
	began := time.Now()
	runText(t, a, ticket)
	if took := time.Since(began); took > time.Second {
		t.Fatalf("the invocation waited %v", took)
	}
	stages := h.stages()
	if len(stages) < 1 || stages[0] != StageFinalize {
		t.Fatalf("stages %v", stages)
	}
}

type slowStore struct {
	*inmemory.Store
	delay time.Duration
}

func (s slowStore) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, f *ae.FinalizedExperience,
	actor string, at *time.Time) error {
	select {
	case <-time.After(s.delay):
	case <-ctx.Done():
		return ctx.Err() // the abandoned work's own context ends at the bound
	}
	return s.Store.InsertFinalized(ctx, bounds, f, actor, at)
}

func TestErrorAndPanicNamesNeverQuoteTheirMessage(t *testing.T) {
	for _, c := range []struct {
		err      error
		expected string
	}{
		{errors.New("secret"), "errors.errorString"},
		{fmt.Errorf("wrapping: %w", lockHeldError{}), "github.com/fabbrik/AgentExperience.NET/go/maf.lockHeldError"},
		{fmt.Errorf("wrapping: %w", &lockHeldError{}), "github.com/fabbrik/AgentExperience.NET/go/maf.lockHeldError"},
		{errors.Join(lockHeldError{}, errors.New("x")), "github.com/fabbrik/AgentExperience.NET/go/maf.lockHeldError"},
		{fmt.Errorf("%w", context.Canceled), CanceledError},
		{context.DeadlineExceeded, DeadlineExceededError},
		{fmt.Errorf("no wrap: %v", lockHeldError{}), "errors.errorString"},
	} {
		if got := errorName(c.err); got != c.expected {
			t.Errorf("errorName(%v) = %q, want %q", c.err, got, c.expected)
		}
	}
	if panicName("secret") != "panic(string)" || panicName(lockHeldError{}) !=
		"panic(github.com/fabbrik/AgentExperience.NET/go/maf.lockHeldError)" {
		t.Error(panicName("secret"))
	}
}
