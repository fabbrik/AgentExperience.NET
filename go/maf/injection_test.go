package maf

// Contract 1.13: the host's callbacks and the receiving agent on the agent. The callbacks have no fixture: these tests
// pin them against README-1.13's rules, which are .NET's.

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"
	"github.com/microsoft/agent-framework-go/tool"
	"github.com/microsoft/agent-framework-go/tool/functool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// optionsAgent is an agent over one seeded lesson (whose approach calls run_tests), with configure applied to its
// options; it captures nothing.
func optionsAgent(t *testing.T, configure func(*Options), tools ...tool.Tool) (*host, *scripted, *agent.Agent, ae.ExperienceRecord) {
	t.Helper()
	learned := lesson(t)
	h, model := newHost(t), newScripted(0)
	options := Options{Store: storeOf(t, learned)}
	configure(&options)
	return h, model, build(t, model, tools, h.options(options)), learned
}

func TestDecideInjectionPermitsAndDenies(t *testing.T) {
	for _, c := range []struct {
		name   string
		decide func(DecisionContext) InjectionDecision
		detail string // "" when permitted
	}{
		{"permit", func(DecisionContext) InjectionDecision { return Permit() }, ""},
		{"deny with a reason", func(DecisionContext) InjectionDecision { return Deny("risk policy") }, "risk policy"},
		{"deny with an empty reason reports it, as .NET does", func(DecisionContext) InjectionDecision { return Deny("") }, "\x00"},
		{"a literal with no reason", func(DecisionContext) InjectionDecision { return InjectionDecision{Reason: ""} },
			ae.HostDeniedDetail},
		{"the zero decision fails closed", func(DecisionContext) InjectionDecision { return InjectionDecision{} },
			ae.HostDeniedDetail},
		{"a panic fails closed", func(DecisionContext) InjectionDecision { panic(errors.New("boom")) },
			"The host injection decision threw errors.errorString, so the record was denied."},
	} {
		t.Run(c.name, func(t *testing.T) {
			var seen []DecisionContext
			h, model, a, learned := optionsAgent(t, func(options *Options) {
				options.DecideInjection = func(d DecisionContext) InjectionDecision {
					seen = append(seen, d)
					return c.decide(d)
				}
			})
			o := observe(t)
			runText(t, a, nextTicket)
			if len(seen) != 1 || seen[0].Candidate.Record.ExperienceID != learned.ExperienceID ||
				seen[0].Current.ExperienceID != learned.ExperienceID || seen[0].ModelAuthored {
				t.Fatalf("the decision saw %+v", seen)
			}
			if c.detail == "" {
				if len(h.injections) != 1 || len(model.blocks()) != 1 || outcomes(o, "inject")[0] != "Injected" {
					t.Fatalf("permitted: %+v %v", h.injections, outcomes(o, "inject"))
				}
				return
			}
			if len(model.blocks()) != 0 || len(h.nothing) != 1 || outcomes(o, "inject")[0] != "NothingToInject" {
				t.Fatalf("denied: blocks %d, nothing %+v, %v", len(model.blocks()), h.nothing, outcomes(o, "inject"))
			}
			omitted := h.nothing[0].Omitted
			want := c.detail
			if want == "\x00" {
				want = ""
			}
			if len(omitted) != 1 || omitted[0].Reason != ae.OmittedHostDenied || omitted[0].Detail == nil ||
				*omitted[0].Detail != want {
				t.Fatalf("omitted %+v", omitted)
			}
		})
	}
}

func TestADeniedRecordKeepsItsSlotAndItsOmissionComesBeforeTheByteBudgets(t *testing.T) {
	first, second := lesson(t), lesson(t)
	second.TaskID = "another-ticket"
	h, model := newHost(t), newScripted(0)
	a := build(t, model, nil, h.options(Options{Store: storeOf(t, first, second),
		BlockOptions: ae.BlockOptions{MaxRecords: 1},
		DecideInjection: func(DecisionContext) InjectionDecision {
			return Deny("no")
		}}))
	runText(t, a, nextTicket)
	if len(model.blocks()) != 0 || len(h.nothing) != 1 {
		t.Fatalf("blocks %d, nothing %+v", len(model.blocks()), h.nothing)
	}
	reasons := []ae.OmissionReason{}
	for _, omitted := range h.nothing[0].Omitted {
		reasons = append(reasons, omitted.Reason)
	}
	// The record past the limit is OverRecordLimit (selection first), and the denied one is not backfilled.
	if !slices.Equal(reasons, []ae.OmissionReason{ae.OmittedOverRecordLimit, ae.OmittedHostDenied}) {
		t.Fatalf("reasons %v", reasons)
	}
}

func TestResolveRequestReplacesSkipsAndFails(t *testing.T) {
	t.Run("replace", func(t *testing.T) {
		var given RequestContext
		h, model, a, _ := optionsAgent(t, func(options *Options) {
			options.ResolveRequest = func(_ context.Context, rc RequestContext) (*InjectionRequest, error) {
				given = rc
				return &InjectionRequest{TaskText: "a refund stuck on a lock, triage the ticket", Limit: ptr(1),
					CorrelationID: "trace-7"}, nil
			}
		})
		o := observe(t)
		runText(t, a, "hello")
		if !given.HasDerivedTaskText || given.DerivedTaskText != "hello" || given.Identity.Principal != "svc" ||
			len(given.Messages) == 0 || given.Agent == nil {
			t.Fatalf("the resolver was given %+v", given)
		}
		if len(model.blocks()) != 1 || len(h.injections) != 1 {
			t.Fatal("the replaced task text found nothing")
		}
		observed := o.of("inject")
		if len(observed) != 1 || observed[0].Outcome != "Injected" ||
			!slices.Contains(observed[0].Attributes, ae.Attribute{Key: ae.AttributeCorrelationID, Value: "trace-7"}) {
			t.Fatalf("%+v", observed)
		}
	})
	t.Run("nil skips", func(t *testing.T) {
		h, model, a, _ := optionsAgent(t, func(options *Options) {
			options.ResolveRequest = func(context.Context, RequestContext) (*InjectionRequest, error) { return nil, nil }
		})
		o := observe(t)
		runText(t, a, nextTicket)
		if got := outcomes(o, "inject"); !slices.Equal(got, []string{"Skipped"}) || len(model.blocks()) != 0 ||
			len(h.injections)+len(h.nothing)+len(h.injectionFailures) != 0 || len(o.of("retrieve")) != 0 {
			t.Fatalf("outcomes %v", got)
		}
		// As .NET's OnContextInjected: the skip is reported too, with no omission, and so is the span's count.
		if len(h.attempts) != 1 || h.attempts[0].Outcome != "Skipped" || len(h.attempts[0].Omitted) != 0 ||
			!slices.Contains(o.of("inject")[0].Attributes, ae.Attribute{Key: ae.AttributeOmittedCount, Value: 0}) {
			t.Fatalf("attempts %+v, %+v", h.attempts, o.of("inject"))
		}
	})
	for name, resolve := range map[string]func(context.Context, RequestContext) (*InjectionRequest, error){
		"an error": func(context.Context, RequestContext) (*InjectionRequest, error) { return nil, errors.New("no") },
		"a panic":  func(context.Context, RequestContext) (*InjectionRequest, error) { panic("no") },
	} {
		t.Run(name+" fails", func(t *testing.T) {
			h, model, a, _ := optionsAgent(t, func(options *Options) { options.ResolveRequest = resolve })
			o := observe(t)
			if runText(t, a, nextTicket).String() != "done" {
				t.Fatal("the invocation did not run")
			}
			if got := outcomes(o, "inject"); !slices.Equal(got, []string{"Failed"}) || len(model.blocks()) != 0 ||
				len(h.injectionFailures) != 1 || len(h.attempts) != 1 || h.attempts[0].Outcome != "Failed" {
				t.Fatalf("outcomes %v, failures %+v", got, h.injectionFailures)
			}
		})
	}
	for name, request := range map[string]InjectionRequest{
		"a blank task text": {TaskText: "  "},
		"an overlong one":   {TaskText: strings.Repeat("a", ae.MaxTaskTextLength+1)},
		"a limit too large": {TaskText: "refund", Limit: ptr(ae.CandidateLimit + 1)},
		"a negative limit":  {TaskText: "refund", Limit: ptr(-1)},
		"a zero limit":      {TaskText: "refund", Limit: ptr(0)},
	} {
		t.Run(name+" is RetrievalFailed", func(t *testing.T) {
			h, _, a, _ := optionsAgent(t, func(options *Options) {
				options.ResolveRequest = func(context.Context, RequestContext) (*InjectionRequest, error) {
					r := request
					return &r, nil
				}
			})
			o := observe(t)
			runText(t, a, nextTicket)
			if got := outcomes(o, "inject"); !slices.Equal(got, []string{"RetrievalFailed"}) || len(h.injectionFailures) != 1 ||
				len(h.attempts) != 1 || h.attempts[0].Outcome != "RetrievalFailed" {
				t.Fatalf("outcomes %v", got)
			}
		})
	}
}

func TestTheCallersCancellationInTheResolverPropagates(t *testing.T) {
	learned := lesson(t)
	h := newHost(t)
	ctx, cancel := context.WithCancel(context.Background())
	provider, err := NewContextProvider(h.options(Options{Store: storeOf(t, learned),
		ResolveRequest: func(ctx context.Context, _ RequestContext) (*InjectionRequest, error) {
			cancel()
			return nil, ctx.Err()
		}}))
	if err != nil {
		t.Fatal(err)
	}
	_, _, err = provider.Invoking(ctx, agent.InvokingContext{Messages: []*message.Message{message.NewText(nextTicket)}})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("err %v", err)
	}
	if len(h.injectionFailures) != 0 {
		t.Fatalf("a cancellation was reported as a failure: %+v", h.injectionFailures)
	}
}

func TestTheReceivingAgentGatesOnTheRunsTools(t *testing.T) {
	runTests := functool.MustNew(functool.Config{Name: "run_tests", Description: "Runs the tests."},
		func(context.Context, suite) (string, error) { return "exit=0", nil })
	other := functool.MustNew(functool.Config{Name: "other", Description: "Another tool."},
		func(context.Context, suite) (string, error) { return "", nil })
	for name, c := range map[string]struct {
		tools  []tool.Tool
		reason ae.OmissionReason
	}{
		"offered":     {[]tool.Tool{runTests}, ""},
		"not offered": {[]tool.Tool{other}, ae.OmittedToolUnavailable},
	} {
		t.Run(name, func(t *testing.T) {
			h, model, a, _ := optionsAgent(t, func(options *Options) {
				options.ReceivingAgent = &ae.ReceivingAgent{UseRunTools: true}
			}, c.tools...)
			runText(t, a, nextTicket)
			if c.reason == "" {
				if len(model.blocks()) != 1 {
					t.Fatalf("no block: %+v", h.nothing)
				}
				return
			}
			if len(model.blocks()) != 0 || len(h.nothing) != 1 || h.nothing[0].Omitted[0].Reason != c.reason ||
				h.nothing[0].Omitted[0].Detail != nil {
				t.Fatalf("%+v", h.nothing)
			}
		})
	}
}

func TestTheRiskGateAndTheModelAuthoredPolicyOnTheAgent(t *testing.T) {
	low := ae.RiskLow
	h, model, a, _ := optionsAgent(t, func(options *Options) {
		options.ReceivingAgent = &ae.ReceivingAgent{MaxRiskClass: &low,
			ToolRiskClasses: map[string]ae.ToolRiskClass{"run_tests": ae.RiskMedium}}
	})
	runText(t, a, nextTicket)
	if len(model.blocks()) != 0 || h.nothing[0].Omitted[0].Reason != ae.OmittedRiskClassExceeded {
		t.Fatalf("%+v", h.nothing)
	}

	authored := lesson(t)
	authored.Reflection.Authorship = ae.AuthorshipModel
	h2, model2 := newHost(t), newScripted(0)
	a2 := build(t, model2, nil, h2.options(Options{Store: storeOf(t, authored), ModelAuthoredLessons: ae.ModelAuthoredExclude}))
	runText(t, a2, nextTicket)
	if len(model2.blocks()) != 0 {
		t.Fatal("Exclude injected a model-authored lesson")
	}
}

func TestTheInjectionOptionsAreValidated(t *testing.T) {
	undefined := ae.ToolRiskClass(9)
	for name, options := range map[string]Options{
		"a blank available tool":    {ReceivingAgent: &ae.ReceivingAgent{AvailableTools: []string{" "}}},
		"a blank classed tool":      {ReceivingAgent: &ae.ReceivingAgent{ToolRiskClasses: map[string]ae.ToolRiskClass{"": ae.RiskLow}}},
		"an undefined class":        {ReceivingAgent: &ae.ReceivingAgent{ToolRiskClasses: map[string]ae.ToolRiskClass{"x": 7}}},
		"an undefined maximum":      {ReceivingAgent: &ae.ReceivingAgent{MaxRiskClass: &undefined}},
		"a negative multiplier":     {ReceivingAgent: &ae.ReceivingAgent{GatedRetrievalWindowMultiplier: -1}},
		"an undefined model policy": {ModelAuthoredLessons: "Mark"},
	} {
		h := newHost(t)
		if err := UseAgentExperience(&agent.Config{}, h.options(options)); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	// The declaration is copied: a later change to the host's slice changes nothing.
	tools := []string{"run_tests"}
	h, model, a, _ := optionsAgent(t, func(options *Options) {
		options.ReceivingAgent = &ae.ReceivingAgent{AvailableTools: tools}
	})
	tools[0] = "other"
	runText(t, a, nextTicket)
	if len(model.blocks()) != 1 {
		t.Fatalf("%+v", h.nothing)
	}
}

func TestTheInjectOutcomesAreTheTelemetryNames(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "docs", "telemetry-names.json"))
	if err != nil {
		t.Fatal(err)
	}
	var names struct {
		Operations []struct {
			Operation string   `json:"operation"`
			Outcomes  []string `json:"outcomes"`
		} `json:"operations"`
		Attributes []string `json:"attributes"`
	}
	if err := json.Unmarshal(data, &names); err != nil {
		t.Fatal(err)
	}
	var inject []string
	for _, operation := range names.Operations {
		if operation.Operation == "inject" {
			inject = operation.Outcomes
		}
	}
	for _, outcome := range []string{"Skipped", "Failed", "RetrievalFailed", "Injected", "NothingToInject"} {
		if !slices.Contains(inject, outcome) {
			t.Errorf("%s is not an inject outcome", outcome)
		}
	}
	if !slices.Contains(names.Attributes, ae.AttributeCorrelationID) {
		t.Errorf("%s is not a span attribute", ae.AttributeCorrelationID)
	}
}

func ptr[T any](value T) *T { return &value }

func TestAnAbsentIdentityIsReportedSkipped(t *testing.T) {
	h, model := newHost(t), newScripted(0)
	options := h.options(Options{Store: storeOf(t, lesson(t))})
	options.Identity = ae.Identity{}
	options.ResolveIdentity = func(context.Context, IdentityContext) (*ae.Identity, error) { return nil, nil }
	a := build(t, model, nil, options)
	runText(t, a, nextTicket)
	if len(h.attempts) != 1 || h.attempts[0].Outcome != "Skipped" || len(model.blocks()) != 0 {
		t.Fatalf("attempts %+v", h.attempts)
	}
}

func TestABudgetNoRecordCanFitIsRefused(t *testing.T) {
	for _, budget := range []int{1, ae.BlockOverheadBytes} {
		h := newHost(t)
		if err := UseAgentExperience(&agent.Config{}, h.options(Options{Store: storeOf(t),
			BlockOptions: ae.BlockOptions{MaxBytes: budget}})); err == nil {
			t.Errorf("MaxBytes %d was accepted", budget)
		}
	}
	h := newHost(t)
	if err := UseAgentExperience(&agent.Config{}, h.options(Options{Store: storeOf(t),
		BlockOptions: ae.BlockOptions{MaxBytes: ae.BlockOverheadBytes + 1}})); err != nil {
		t.Errorf("MaxBytes %d was refused: %v", ae.BlockOverheadBytes+1, err)
	}
	if ae.BlockOverheadBytes != 754 {
		t.Errorf("the verbose overhead is %d bytes, not .NET's 754", ae.BlockOverheadBytes)
	}
}

// A gate with a request limit: retrieval is asked for the wider window, the gated record in the host's window is
// reported, the backfill fills the slot, and the injection is cut to the request's own limit.
func TestAGateWithARequestLimitWidensRetrievalAndBackfills(t *testing.T) {
	gated := lesson(t)
	gated.TaskID = "ticket-gated"
	for i := range gated.Attempts {
		for j := range gated.Attempts[i].ToolCalls {
			gated.Attempts[i].ToolCalls[j].ToolName = "drop_ledger"
		}
	}
	first, second := lesson(t), lesson(t)
	first.TaskID, second.TaskID = "ticket-first", "ticket-second"
	h, model := newHost(t), newScripted(0)
	limit := 1
	a := build(t, model, nil, h.options(Options{Store: storeOf(t, gated, first, second),
		ReceivingAgent: &ae.ReceivingAgent{AvailableTools: []string{"run_tests"}, GatedRetrievalWindowMultiplier: 3},
		ResolveRequest: func(_ context.Context, rc RequestContext) (*InjectionRequest, error) {
			return &InjectionRequest{TaskText: rc.DerivedTaskText, Limit: &limit}, nil
		}}))
	runText(t, a, nextTicket)
	if len(h.injections) != 1 || len(h.injections[0].ExperienceIDs) != 1 || len(model.blocks()) != 1 {
		t.Fatalf("injections %+v, nothing %+v", h.injections, h.nothing)
	}
	injected := h.injections[0]
	// Ranked first is reported (inside the window); the backfill below it fills the one slot.
	omittedGated := 0
	for _, omitted := range injected.Omitted {
		if omitted.Reason == ae.OmittedToolUnavailable {
			omittedGated++
		}
	}
	if injected.ExperienceIDs[0] == gated.ExperienceID || omittedGated > 1 {
		t.Fatalf("%+v", injected)
	}
}

// UseRunTools sees a tool passed to one run (agent.WithTool), not only the configuration's.
func TestTheReceivingAgentSeesAToolPassedToOneRun(t *testing.T) {
	runTests := functool.MustNew(functool.Config{Name: "run_tests", Description: "Runs the tests."},
		func(context.Context, suite) (string, error) { return "exit=0", nil })
	h, model, a, _ := optionsAgent(t, func(options *Options) {
		options.ReceivingAgent = &ae.ReceivingAgent{UseRunTools: true}
	})
	runText(t, a, nextTicket)
	if len(model.blocks()) != 0 || len(h.nothing) != 1 || h.nothing[0].Omitted[0].Reason != ae.OmittedToolUnavailable {
		t.Fatalf("without the run's tool: %+v", h.nothing)
	}
	runText(t, a, nextTicket, agent.WithTool(runTests))
	if len(model.blocks()) != 1 {
		t.Fatalf("with the run's tool: %+v", h.nothing)
	}
}
