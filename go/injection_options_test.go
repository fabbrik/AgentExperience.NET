package agentexperience_test

// Contract 1.13: the capability gate's rules beyond the fixtures.

import (
	"math"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

func TestTheGatedWindow(t *testing.T) {
	low, critical := ae.RiskLow, ae.RiskCritical
	gate, err := ae.NewCapabilityGate(&ae.ReceivingAgent{MaxRiskClass: &low})
	if err != nil {
		t.Fatal(err)
	}
	none, err := ae.NewCapabilityGate(&ae.ReceivingAgent{MaxRiskClass: &critical})
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		gate                   *ae.CapabilityGate
		limit, maxRecords, got int
	}{
		{gate, 2, 3, 12},  // 3 x the default multiplier 4
		{gate, 0, 3, 0},   // no limit stays none
		{gate, 20, 3, 20}, // a larger limit is kept
		{gate, 2, 20, 50}, // capped by the candidate limit
		{gate, 51, 3, 51}, // left for retrieval to refuse
		{none, 2, 3, 2},   // a gate that cannot reject widens nothing
		{nil, 2, 3, 2},
	} {
		if got := ae.GatedWindow(c.limit, c.gate, c.maxRecords, ae.CandidateLimit); got != c.got {
			t.Errorf("limit %d, max %d: %d, expected %d", c.limit, c.maxRecords, got, c.got)
		}
	}
	if none.CanReject() || !gate.CanReject() {
		t.Fatal("CanReject")
	}
}

func TestTheGateIsValidatedAndCopied(t *testing.T) {
	undefined := ae.ToolRiskClass(4)
	for name, agent := range map[string]ae.ReceivingAgent{
		"blank tool":         {AvailableTools: []string{""}},
		"blank classed tool": {ToolRiskClasses: map[string]ae.ToolRiskClass{" ": ae.RiskLow}},
		"undefined class":    {ToolRiskClasses: map[string]ae.ToolRiskClass{"x": -1}},
		"undefined maximum":  {MaxRiskClass: &undefined},
		"negative window":    {GatedRetrievalWindowMultiplier: -1},
	} {
		if _, err := ae.NewCapabilityGate(&agent); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	if gate, err := ae.NewCapabilityGate(nil); gate != nil || err != nil {
		t.Fatal("no declaration is no gate")
	}
	for _, name := range []string{"Low", "Medium", "High", "Critical"} {
		class, err := ae.ParseToolRiskClass(name)
		if err != nil || class.String() != name {
			t.Fatalf("%s: %v %v", name, class, err)
		}
	}
	if _, err := ae.ParseToolRiskClass("low"); err == nil {
		t.Fatal("names are .NET's, exactly")
	}
	if ae.ModelAuthoredLessonPolicy("Mark").Validate() == nil || ae.ModelAuthoredLessonPolicy("").Validate() != nil {
		t.Fatal("the model-authored policy's validation")
	}
}

func TestTheGatedWindowCannotOverflow(t *testing.T) {
	low := ae.RiskLow
	gate, err := ae.NewCapabilityGate(&ae.ReceivingAgent{MaxRiskClass: &low, GatedRetrievalWindowMultiplier: math.MaxInt})
	if err != nil {
		t.Fatal(err)
	}
	if got := ae.GatedWindow(2, gate, 8, ae.CandidateLimit); got != ae.CandidateLimit {
		t.Fatalf("a huge multiplier gave %d, not the candidate limit", got)
	}
}

// A gate that uses the run's tools, checked without ForInvocation, fails closed: as though no tool were offered.
func TestARunToolsGateCheckedDirectlyFailsClosed(t *testing.T) {
	gate, err := ae.NewCapabilityGate(&ae.ReceivingAgent{UseRunTools: true})
	if err != nil {
		t.Fatal(err)
	}
	record := ae.ExperienceRecord{Status: ae.StatusValidated, Outcome: ae.Outcome{Status: ae.VerificationVerified},
		Attempts: []ae.Attempt{{SequenceNumber: 0, ToolCalls: []ae.ToolCall{{SequenceNumber: 0, ToolName: "run_tests"}}}}}
	if got := gate.Check(&record); got != ae.OmittedToolUnavailable {
		t.Fatalf("checked directly: %q", got)
	}
	if got := gate.ForInvocation([]string{"run_tests"}).Check(&record); got != "" {
		t.Fatalf("narrowed: %q", got)
	}
}
