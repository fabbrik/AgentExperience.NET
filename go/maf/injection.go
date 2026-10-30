package maf

// Contract 1.13: the host's injection callbacks (.NET's ExperienceInjectionOptions.ResolveRequest / ResolveRequestAsync
// and DecideInjection).

import (
	"context"
	"errors"
	"strconv"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// RequestContext is what Options.ResolveRequest is given for one invocation (.NET's ExperienceInjectionContext): the
// running agent and the caller's session (each nil when the SDK did not say), the invocation's messages (history
// first, then the new input; the block a history provider replayed left out; read-only), the task text the contract's
// rule derives from them (DerivedTaskText, "" when HasDerivedTaskText is false), and the identity the invocation runs
// for, which carries the authorization and scope the port searches in.
type RequestContext struct {
	Agent              *agent.Agent
	Session            *agent.Session
	Messages           []*message.Message
	DerivedTaskText    string
	HasDerivedTaskText bool
	Identity           ae.Identity
}

// InjectionRequest is what the host asks retrieval for (.NET's RetrieveExperienceRequest, the fields a port takes; the
// authorization and scope are the identity's).
type InjectionRequest struct {
	// TaskText is matched against the stored lessons: not blank, at most ae.MaxTaskTextLength UTF-16 code units.
	TaskText string
	// PreferredEnvironment, when not nil, replaces Options.PreferredEnvironment for this invocation.
	PreferredEnvironment map[string]string
	// Limit is the request's own limit: at most this many records are injected, and ranking is cut to it (to the
	// capability gate's wider window while a gate can reject). 0 for none; otherwise 1 to the retrieval policy's
	// CandidateLimit, or retrieval refuses the request (RetrievalFailed).
	Limit int
	// ExcludeModelAuthored asks retrieval to leave model-authored lessons out; ModelAuthoredLessons Exclude sets it
	// whatever the host returns, and never clears it.
	ExcludeModelAuthored bool
	// CorrelationID, when set, is put on the inject operation's span as agentexperience.correlation_id.
	CorrelationID string
}

// DecisionContext is what Options.DecideInjection is asked about one selected record (.NET's
// ExperienceInjectionDecisionContext, without the grant fields this port does not have): the record as ranked, the
// record as it stands (this port re-reads nothing, so it is the ranked one), and whether the provider counts it as
// model-authored (decide on this, never on the record's declared authorship).
type DecisionContext struct {
	Candidate     ae.RankedRecord
	Current       ae.ExperienceRecord
	ModelAuthored bool
}

// InjectionDecision is the host's decision on one record (.NET's InjectionDecision). The zero value denies: a
// decision that says nothing fails closed, as .NET's null does.
type InjectionDecision struct {
	Permitted bool
	// Reason is the content-free explanation reported as a denial's detail; when empty,
	// ae.HostDeniedDetail is.
	Reason string
}

// Permit is a decision that permits the record.
func Permit() InjectionDecision { return InjectionDecision{Permitted: true} }

// Deny is a decision that denies the record, with a content-free reason.
func Deny(reason string) InjectionDecision { return InjectionDecision{Reason: reason} }

// errInvalidRequest is a request the resolver returned that retrieval refuses.
var errInvalidRequest = errors.New("maf: the injection request is invalid")

// validateRequest refuses what .NET's retrieval refuses: a blank or overlong task text, and a limit outside
// 1..candidateLimit (0 is none).
func validateRequest(request *InjectionRequest, candidateLimit int) error {
	if ae.IsNullOrWhiteSpace(request.TaskText) {
		return errors.Join(errInvalidRequest, errors.New("the task text is blank"))
	}
	if ae.UTF16Len(request.TaskText) > ae.MaxTaskTextLength {
		return errors.Join(errInvalidRequest, errors.New("the task text is longer than "+
			strconv.Itoa(ae.MaxTaskTextLength)+" UTF-16 code units"))
	}
	if request.Limit < 0 || request.Limit > candidateLimit {
		return errors.Join(errInvalidRequest, errors.New("the limit must be from 1 to the policy's candidate limit ("+
			strconv.Itoa(candidateLimit)+"), or 0 for none"))
	}
	return nil
}

// resolveRequest calls the host's resolver; a panic is returned as an error.
func (s *settings) resolveRequest(ctx context.Context, rc RequestContext) (request *InjectionRequest, err error) {
	defer recoverInto(&err)
	return s.ResolveRequest(ctx, rc)
}

// decide asks the host about each selected record, in order (.NET's DecideInjection, after the final eligibility check):
// the records it permits, and a HostDenied omission for each it denies. A panic or a zero decision denies; a denied
// record has taken its slot, so nothing backfills it.
func (s *settings) decide(selected []ae.RankedRecord) ([]ae.RankedRecord, []ae.OmittedRecord) {
	if s.DecideInjection == nil {
		return selected, nil
	}
	permitted := make([]ae.RankedRecord, 0, len(selected))
	var denied []ae.OmittedRecord
	for _, candidate := range selected {
		decision, panicked := s.decideOne(candidate)
		if decision.Permitted && panicked == nil {
			permitted = append(permitted, candidate)
			continue
		}
		detail := ae.HostDeniedDetail
		switch {
		case panicked != nil:
			name := typeName(panicked)
			if err, ok := panicked.(error); ok {
				name = errorName(err)
			}
			detail = ae.HostDeniedPanicDetail(name)
		case decision.Reason != "":
			detail = decision.Reason
		}
		denied = append(denied, ae.OmittedRecord{ExperienceID: candidate.Record.ExperienceID,
			Reason: ae.OmittedHostDenied, Detail: &detail})
	}
	return permitted, denied
}

func (s *settings) decideOne(candidate ae.RankedRecord) (decision InjectionDecision, panicked any) {
	defer func() {
		if recovered := recover(); recovered != nil {
			decision, panicked = InjectionDecision{}, recovered
		}
	}()
	return s.DecideInjection(DecisionContext{Candidate: candidate, Current: candidate.Record,
		ModelAuthored: ae.IsModelAuthored(candidate.Record.Reflection)}), nil
}

// runTools are the names of the tools the invocation offers: the agent configuration's tools and the run's own, as the
// SDK hands them to the context providers in its options. Options that cannot be read offer no tool (fail closed).
func runTools(options []agent.Option) (names []string) {
	defer func() {
		if recover() != nil {
			names = []string{}
		}
	}()
	names = []string{}
	for t := range agent.AllOptions(options, agent.WithTool) {
		if t != nil {
			names = append(names, t.Name())
		}
	}
	return names
}
