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
		{"deny without one", func(DecisionContext) InjectionDecision { return Deny("") }, ae.HostDeniedDetail},
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
			if len(omitted) != 1 || omitted[0].Reason != ae.OmittedHostDenied || omitted[0].Detail == nil ||
				*omitted[0].Detail != c.detail {
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
				return &InjectionRequest{TaskText: "a refund stuck on a lock, triage the ticket", Limit: 1,
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
				len(h.injectionFailures) != 1 {
				t.Fatalf("outcomes %v, failures %+v", got, h.injectionFailures)
			}
		})
	}
	for name, request := range map[string]InjectionRequest{
		"a blank task text": {TaskText: "  "},
		"an overlong one":   {TaskText: strings.Repeat("a", ae.MaxTaskTextLength+1)},
		"a limit too large": {TaskText: "refund", Limit: ae.CandidateLimit + 1},
		"a negative limit":  {TaskText: "refund", Limit: -1},
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
			if got := outcomes(o, "inject"); !slices.Equal(got, []string{"RetrievalFailed"}) || len(h.injectionFailures) != 1 {
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
