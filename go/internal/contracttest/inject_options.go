package contracttest

import (
	"fmt"
	"os"
	"path/filepath"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.13's inject-options fixtures ---------------------------------------------------------------------

// InjectOptionsFamily is the inject-options fixture family's directory name.
const InjectOptionsFamily = "inject-options"

// InjectOptionsCase is one inject-options case: records.json, options.json, expected.json and expected-block.txt.
type InjectOptionsCase struct {
	Name   string
	Ranked []ae.RankedRecord
	// Options is options.json as read: the block options, the role ("User" or "System"), the model-authored policy,
	// the request's own limit (0 for none), the receiving agent (nil for none) and the invocation's tool names.
	Block         ae.BlockOptions
	MessageRole   string
	ModelAuthored ae.ModelAuthoredLessonPolicy
	RequestLimit  int
	Agent         *ae.ReceivingAgent
	RunTools      []string
	Expected      *ae.Object
	ExpectedBlock string
}

// ReceivingAgentOf reads a receivingAgent object of the fixtures (nil for null).
func ReceivingAgentOf(value any) (*ae.ReceivingAgent, error) {
	o := object(value)
	if o == nil {
		return nil, nil
	}
	agent := &ae.ReceivingAgent{ToolRiskClasses: map[string]ae.ToolRiskClass{},
		GatedRetrievalWindowMultiplier: int(integer(get(o, "gatedRetrievalWindowMultiplier")))}
	if tools := get(o, "availableTools"); tools != nil {
		agent.AvailableTools = []string{}
		for _, tool := range list(tools) {
			agent.AvailableTools = append(agent.AvailableTools, text(tool))
		}
	}
	if max := get(o, "maxRiskClass"); max != nil {
		class, err := ae.ParseToolRiskClass(text(max))
		if err != nil {
			return nil, err
		}
		agent.MaxRiskClass = &class
	}
	for _, m := range object(get(o, "toolRiskClasses")).Members() {
		class, err := ae.ParseToolRiskClass(text(m.Value))
		if err != nil {
			return nil, err
		}
		agent.ToolRiskClasses[m.Key] = class
	}
	agent.UseRunTools, _ = get(o, "useRunTools").(bool)
	return agent, nil
}

// InjectOptionsOf reads an options.json of the inject-options family (also an agent's options in the cross-language
// file) into a case with no records.
func InjectOptionsOf(value any) (*InjectOptionsCase, error) {
	options := object(value)
	c := &InjectOptionsCase{MessageRole: text(get(options, "messageRole")),
		ModelAuthored: ae.ModelAuthoredLessonPolicy(text(get(options, "modelAuthoredLessons")))}
	c.Block = ae.BlockOptions{Rendering: ae.Rendering(text(get(options, "rendering"))),
		FailureDetail: ae.FailureDetail(text(get(options, "failureDetail"))),
		MaxRecords:    int(integer(get(options, "maxRecords"))), MaxBytes: int(integer(get(options, "maxBytes"))),
		ApproachArguments: map[string][]string{}}
	for _, m := range object(get(options, "approachArguments")).Members() {
		for _, key := range list(m.Value) {
			c.Block.ApproachArguments[m.Key] = append(c.Block.ApproachArguments[m.Key], text(key))
		}
	}
	if limit := get(options, "requestLimit"); limit != nil {
		c.RequestLimit = int(integer(limit))
	}
	var err error
	if c.Agent, err = ReceivingAgentOf(get(options, "receivingAgent")); err != nil {
		return nil, err
	}
	if tools := get(options, "runTools"); tools != nil {
		c.RunTools = []string{}
		for _, tool := range list(tools) {
			c.RunTools = append(c.RunTools, text(tool))
		}
	}
	return c, nil
}

// LoadInjectOptionsCase reads an inject-options case.
func LoadInjectOptionsCase(name string) (*InjectOptionsCase, error) {
	dir := filepath.Join(Fixtures, InjectOptionsFamily, name)
	ranked, err := ae.RankedFromJSON(MustReadJSON(filepath.Join(dir, "records.json")))
	if err != nil {
		return nil, fmt.Errorf("%s: %w", name, err)
	}
	options := object(MustReadJSON(filepath.Join(dir, "options.json")))
	block, err := os.ReadFile(filepath.Join(dir, "expected-block.txt"))
	if err != nil {
		return nil, err
	}
	c, err := InjectOptionsOf(options)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", name, err)
	}
	c.Name, c.Ranked, c.ExpectedBlock = name, ranked, string(block)
	c.Expected = object(MustReadJSON(filepath.Join(dir, "expected.json")))
	return c, nil
}

// Run is what the port injects for the case, with no benefit view and no host decision: the gate for an invocation
// offering the case's run tools, the limit retrieval is asked for (GatedWindow under the default policy's candidate
// limit), SelectCandidates, then the block and its byte budget. It answers expected.json's form and the block text.
func (c *InjectOptionsCase) Run() (*ae.Object, string, error) {
	if err := c.ModelAuthored.Validate(); err != nil {
		return nil, "", err
	}
	gate, err := ae.NewCapabilityGate(c.Agent)
	if err != nil {
		return nil, "", err
	}
	gate = gate.ForInvocation(c.RunTools)
	exclude := c.ModelAuthored == ae.ModelAuthoredExclude
	selected, omitted := ae.SelectCandidates(c.Ranked, nil, ae.SelectOptions{MaxRecords: c.Block.MaxRecords,
		RequestLimit: c.RequestLimit, ExcludeModelAuthored: exclude, Gate: gate})
	block, err := ae.RenderBlock(selected, c.Block)
	if err != nil {
		return nil, "", err
	}
	omitted = append(omitted, ae.ByteBudgetOmissions(block, c.Block)...)
	var limit any
	if window := ae.GatedWindow(c.RequestLimit, gate, c.Block.MaxRecords, ae.CandidateLimit); window > 0 {
		limit = window
	}
	outcome, role := "Injected", any(c.MessageRole)
	if block.IsEmpty() {
		outcome, role = "NothingToInject", nil
	}
	selection := SelectionJSON(selected, block.Included, omitted)
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: outcome},
		ae.Member{Key: "messageRole", Value: role},
		ae.Member{Key: "retrievalRequest", Value: ae.NewObject(ae.Member{Key: "limit", Value: limit},
			ae.Member{Key: "excludeModelAuthored", Value: exclude})},
		ae.Member{Key: "selected", Value: get(selection, "selected")},
		ae.Member{Key: "injected", Value: get(selection, "injected")},
		ae.Member{Key: "byteCount", Value: block.ByteCount},
		ae.Member{Key: "omitted", Value: get(selection, "omitted")},
	), block.Text, nil
}
