package agentexperience

// Contract 1.13: the injection options whose effect is deterministic (.NET's ExperienceInjectionOptions,
// ReceivingAgentCapabilities, ToolRiskClass and ModelAuthoredLessonPolicy).

import (
	"errors"
	"fmt"
	"maps"
	"slices"
)

// ToolRiskClass is how risky the host judges one of its tools to be (.NET's ToolRiskClass), for the capability gate.
// Ordered: a higher value is riskier. The library never infers a tool's class: the host declares it in
// ReceivingAgent.ToolRiskClasses, and a tool it does not declare counts as Critical.
type ToolRiskClass int

// The risk classes, with .NET's values.
const (
	// RiskLow is read-only or otherwise harmless.
	RiskLow ToolRiskClass = 0
	// RiskMedium changes state in a limited, recoverable way.
	RiskMedium ToolRiskClass = 1
	// RiskHigh changes state in a way that is hard to undo or reaches other parties.
	RiskHigh ToolRiskClass = 2
	// RiskCritical is irreversible or high-impact; also the class of every tool the host did not declare.
	RiskCritical ToolRiskClass = 3
)

var riskClassNames = [...]string{"Low", "Medium", "High", "Critical"}

// Defined reports whether c is one of the four classes.
func (c ToolRiskClass) Defined() bool { return c >= RiskLow && c <= RiskCritical }

// String is .NET's member name ("Low", "Medium", "High", "Critical"), or the number for an undefined class.
func (c ToolRiskClass) String() string {
	if c.Defined() {
		return riskClassNames[c]
	}
	return fmt.Sprintf("ToolRiskClass(%d)", int(c))
}

// ParseToolRiskClass reads .NET's member name.
func ParseToolRiskClass(name string) (ToolRiskClass, error) {
	for i, n := range riskClassNames {
		if n == name {
			return ToolRiskClass(i), nil
		}
	}
	return 0, fmt.Errorf("%q is not a ToolRiskClass (Low, Medium, High or Critical)", name)
}

// ModelAuthoredLessonPolicy is whether records whose lesson a model wrote are injected (.NET's
// ModelAuthoredLessonPolicy). The empty value is Include.
type ModelAuthoredLessonPolicy string

// The policies.
const (
	// ModelAuthoredInclude injects them, inside the fixed Authored:/End authored: fence. The default.
	ModelAuthoredInclude ModelAuthoredLessonPolicy = "Include"
	// ModelAuthoredExclude asks retrieval to leave them out, and omits any that arrives (OmittedModelAuthored).
	ModelAuthoredExclude ModelAuthoredLessonPolicy = "Exclude"
)

// Validate refuses a value that is not a policy ("" is Include).
func (p ModelAuthoredLessonPolicy) Validate() error {
	switch p {
	case "", ModelAuthoredInclude, ModelAuthoredExclude:
		return nil
	}
	return fmt.Errorf("the model-authored lesson policy must be Include or Exclude, not %q", string(p))
}

// DefaultGatedRetrievalWindowMultiplier is .NET's ReceivingAgentCapabilities.GatedRetrievalWindowMultiplier default.
const DefaultGatedRetrievalWindowMultiplier = 4

// ReceivingAgent is what the host declares about the agent receiving the block (.NET's ReceivingAgentCapabilities), so
// a record whose verified approach the agent cannot, or must not, carry out is not injected into it (contract 1.13).
//
// What is checked: the tool names of the record's approach, the attempt its Worked: line names (its verified final
// attempt's calls, in call order, cut to 20). A record with no approach passes. The tool check first: an approach tool
// missing from the available set (or with a blank name) omits the record as ToolUnavailable; then the risk check: an
// approach tool whose class (ToolRiskClasses, else Critical) is above MaxRiskClass omits it as RiskClassExceeded.
// Neither omission carries a detail, so no tool name leaves the provider.
type ReceivingAgent struct {
	// AvailableTools are the tools the agent has, compared ordinally. nil skips the tool check; a non-nil empty slice
	// admits only approaches that call no tool.
	AvailableTools []string
	// MaxRiskClass is the riskiest class the agent may be taught. nil skips the risk check; RiskCritical disables it.
	MaxRiskClass *ToolRiskClass
	// ToolRiskClasses are the host's class per tool; a tool not listed is Critical. Read only with MaxRiskClass.
	ToolRiskClasses map[string]ToolRiskClass
	// UseRunTools checks against the names of the tools the invocation offers (blank names ignored), intersected with
	// AvailableTools when that is set too.
	UseRunTools bool
	// GatedRetrievalWindowMultiplier widens retrieval while a gate can reject and the request names its own limit:
	// MaxRecords times this many records, capped by the candidate limit. DefaultGatedRetrievalWindowMultiplier when 0
	// (Go cannot tell an unset value from zero); a negative one is refused.
	GatedRetrievalWindowMultiplier int
}

// CapabilityGate is a ReceivingAgent as validated and copied (.NET's internal CapabilityGate): a later change to the
// host's slices and maps changes nothing.
type CapabilityGate struct {
	available  map[string]bool // nil: no tool check
	maxRisk    *ToolRiskClass
	riskClass  map[string]ToolRiskClass
	useRun     bool
	multiplier int
}

// NewCapabilityGate validates and copies a declaration; nil gives a nil gate (nothing is gated). A blank tool name in
// either collection, an undefined risk class or a negative multiplier is refused.
func NewCapabilityGate(agent *ReceivingAgent) (*CapabilityGate, error) {
	if agent == nil {
		return nil, nil
	}
	gate := &CapabilityGate{useRun: agent.UseRunTools, riskClass: map[string]ToolRiskClass{}}
	if agent.AvailableTools != nil {
		gate.available = map[string]bool{}
		for _, tool := range agent.AvailableTools {
			if isNullOrWhitespace(tool) {
				return nil, errors.New("ReceivingAgent.AvailableTools may not contain an empty or whitespace tool name")
			}
			gate.available[tool] = true
		}
	}
	if agent.MaxRiskClass != nil {
		if !agent.MaxRiskClass.Defined() {
			return nil, fmt.Errorf("ReceivingAgent.MaxRiskClass %d is not a defined ToolRiskClass", int(*agent.MaxRiskClass))
		}
		max := *agent.MaxRiskClass
		gate.maxRisk = &max
	}
	for _, tool := range slices.Sorted(maps.Keys(agent.ToolRiskClasses)) {
		class := agent.ToolRiskClasses[tool]
		if isNullOrWhitespace(tool) {
			return nil, errors.New("ReceivingAgent.ToolRiskClasses may not contain an empty or whitespace tool name")
		}
		if !class.Defined() {
			return nil, fmt.Errorf("ReceivingAgent.ToolRiskClasses holds %d, which is not a defined ToolRiskClass", int(class))
		}
		gate.riskClass[tool] = class
	}
	switch {
	case agent.GatedRetrievalWindowMultiplier < 0:
		return nil, fmt.Errorf("ReceivingAgent.GatedRetrievalWindowMultiplier must be at least 1; %d was set",
			agent.GatedRetrievalWindowMultiplier)
	case agent.GatedRetrievalWindowMultiplier == 0:
		gate.multiplier = DefaultGatedRetrievalWindowMultiplier
	default:
		gate.multiplier = agent.GatedRetrievalWindowMultiplier
	}
	return gate, nil
}

// UsesRunTools reports whether the gate checks against the invocation's own tools.
func (g *CapabilityGate) UsesRunTools() bool { return g != nil && g.useRun }

// CanReject reports whether the gate can keep any record out: a tool check (declared, or from the run's tools), or a
// risk check below Critical. One that cannot is no gate at all: it neither checks a record nor widens retrieval. A nil
// gate cannot.
func (g *CapabilityGate) CanReject() bool {
	return g != nil && (g.available != nil || g.useRun || (g.maxRisk != nil && *g.maxRisk < RiskCritical))
}

// ForInvocation is the gate for one invocation: g itself unless it uses the run's tools, in which case the available
// set is runTools' names (blank names ignored), intersected with AvailableTools when that is set. A nil gate answers nil.
func (g *CapabilityGate) ForInvocation(runTools []string) *CapabilityGate {
	if g == nil || !g.useRun {
		return g
	}
	available := map[string]bool{}
	for _, name := range runTools {
		if !isNullOrWhitespace(name) && (g.available == nil || g.available[name]) {
			available[name] = true
		}
	}
	return &CapabilityGate{available: available, maxRisk: g.maxRisk, riskClass: g.riskClass, multiplier: g.multiplier}
}

// Check is the reason the gate keeps record out (OmittedToolUnavailable or OmittedRiskClassExceeded), or "" when it
// passes: the tools of its approach, the attempt its Worked: line names. A nil gate passes everything.
func (g *CapabilityGate) Check(record *ExperienceRecord) OmissionReason {
	if g == nil {
		return ""
	}
	calls := ApproachToolCalls(record)
	if len(calls) == 0 {
		return ""
	}
	if g.available != nil {
		for _, call := range calls {
			if isNullOrWhitespace(call.ToolName) || !g.available[call.ToolName] {
				return OmittedToolUnavailable
			}
		}
	}
	if g.maxRisk != nil {
		for _, call := range calls {
			class, ok := g.riskClass[call.ToolName]
			if !ok {
				class = RiskCritical
			}
			if class > *g.maxRisk {
				return OmittedRiskClassExceeded
			}
		}
	}
	return ""
}

// GatedWindow is the limit retrieval is asked for (.NET's ExperienceContextProvider.GatedWindow): requestLimit itself
// (0 for none, which stays none), unless the gate can reject and the limit is within 1..candidateLimit, in which case
// min(candidateLimit, max(requestLimit, maxRecords x the gate's multiplier)).
func GatedWindow(requestLimit int, gate *CapabilityGate, maxRecords, candidateLimit int) int {
	if !gate.CanReject() || requestLimit <= 0 || requestLimit > candidateLimit {
		return requestLimit
	}
	return min(candidateLimit, max(requestLimit, maxRecords*gate.multiplier))
}

// ApproachToolCalls are the calls of record's approach, the attempt its Worked: line names: its verified final
// attempt's calls in call order, cut to 20, exactly what its Tried: line shows. nil when it has none (not Verified,
// Quarantined, no attempts, duplicated attempt sequence numbers, or a final attempt with an error).
func ApproachToolCalls(record *ExperienceRecord) []ToolCall {
	final := finalVerifiedAttempt(record)
	if final == nil {
		return nil
	}
	calls, _ := orderedCalls(final)
	return calls
}

// finalVerifiedAttempt is the attempt a Worked: line names, or nil.
func finalVerifiedAttempt(record *ExperienceRecord) *Attempt {
	if record.Status == StatusQuarantined || record.Outcome.Status != VerificationVerified {
		return nil
	}
	attempts := orderedAttempts(record)
	if len(attempts) == 0 || attempts[len(attempts)-1].Error != nil {
		return nil
	}
	return &attempts[len(attempts)-1]
}

// The details of a record the host's decision denied: .NET's, byte for byte.
const (
	HostDeniedDetail = "The host injection decision denied the record."
)

// HostDeniedPanicDetail is the detail of a record whose host decision panicked (.NET: threw), naming the panic's type.
func HostDeniedPanicDetail(typeName string) string {
	return "The host injection decision threw " + typeName + ", so the record was denied."
}
