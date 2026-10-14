package main

import (
	"bytes"
	"os"
	"strings"
	"testing"
)

const updateVariable = "AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE"

// The quick start runs, and its second run is handed the lesson naming both calls. Its output is checked in as
// quickstart.expected.txt; AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE=1 rewrites it on purpose.
func TestTheQuickStartHandsTheSecondRunTheLessonNamingBothCalls(t *testing.T) {
	// The development identity refuses outside development: name the environment, whatever the machine's variables.
	t.Setenv("AGENTEXPERIENCE_ENVIRONMENT", "Development")
	var out bytes.Buffer
	if err := run(&out); err != nil {
		t.Fatal(err)
	}
	output := out.String()
	for _, want := range []string{
		"Run 1 (no memory yet)",
		"Run 2 (lesson injected)",
		"  - attempt 0: run_tests [failed: exit 3], run_tests [returned] → completed\n",
	} {
		if !strings.Contains(output, want) {
			t.Fatalf("the output does not contain %q:\n%s", want, output)
		}
	}
	if os.Getenv(updateVariable) == "1" {
		if err := os.WriteFile("quickstart.expected.txt", []byte(output), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	expected, err := os.ReadFile("quickstart.expected.txt")
	if err != nil {
		t.Fatal(err)
	}
	if output != string(expected) {
		t.Fatalf("the output differs from quickstart.expected.txt (set %s=1 to rewrite it on purpose):\n%s",
			updateVariable, output)
	}
}

// The development identity refuses a production environment, so the quick start does not run there.
func TestTheQuickStartRefusesToRunInProduction(t *testing.T) {
	t.Setenv("AGENTEXPERIENCE_ENVIRONMENT", "Production")
	if err := run(&bytes.Buffer{}); err == nil || !strings.Contains(err.Error(), "Production") {
		t.Fatalf("expected a refusal naming the environment, got %v", err)
	}
}
