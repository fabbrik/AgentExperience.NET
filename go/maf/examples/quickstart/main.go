// Command quickstart is the Go quick start: the README's setup, run twice, with a scripted stand-in for the model.
//
//	go run ./examples/quickstart
//
// No key, no network, no database. The agent triages a stuck refund by running the tests: in the first run the first
// test run fails (exit=3) and the second passes (exit=0). ae.ToolSucceeded("run_tests") verifies the run by the last
// test run, which passed, so the run is stored as a lesson. The second run, on a similar ticket, finds the lesson and
// its model is handed the Historical Reference block, which names both calls:
// run_tests [failed: exit 3], run_tests [returned].
//
// The model is a script, not a model, and the output says so: it calls run_tests until a call exits 0. It shows the
// loop working (capture, verification, storage, retrieval and injection), not that a lesson helps a real model.
package main

import (
	"context"
	"fmt"
	"io"
	"iter"
	"os"
	"strings"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/agent/harness/toolautocall"
	"github.com/microsoft/agent-framework-go/message"
	"github.com/microsoft/agent-framework-go/tool"
	"github.com/microsoft/agent-framework-go/tool/functool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
	"github.com/fabbrik/AgentExperience.NET/go/maf"
)

const (
	firstTicket  = "Ticket #4812: a refund is stuck on a lock. Triage it."
	secondTicket = "Ticket #4977: a refund is stuck on a lock again. Triage it."
	standIn      = "a scripted stand-in, not a real model"
)

// scriptedModel is a scripted stand-in for a chat model: it calls run_tests until a call returns exit=0, then
// answers. It reads only what a real model is sent, and keeps the Historical Reference block it was handed, to print
// it.
type scriptedModel struct {
	block string
	calls int
}

func (m *scriptedModel) run(_ context.Context, messages []*message.Message,
	options ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
	return func(yield func(*agent.ResponseUpdate, error) bool) {
		if stream, _ := agent.GetOption(options, agent.Stream); stream {
			yield(nil, fmt.Errorf("the scripted stand-in does not stream"))
			return
		}
		passed := false
		for _, m2 := range messages {
			if stamped, _ := m2.AdditionalProperties[maf.HistoricalReferenceKey].(bool); stamped && m.block == "" {
				m.block = m2.String()
			}
			for _, content := range m2.Contents {
				if result, ok := content.(*message.FunctionResultContent); ok &&
					strings.Contains(fmt.Sprint(result.Result), "exit=0") {
					passed = true
				}
			}
		}
		if passed {
			yield(&agent.ResponseUpdate{Role: message.RoleAssistant,
				Contents: message.Contents{&message.TextContent{Text: "The tests pass; the refund is released."}}}, nil)
			return
		}
		m.calls++
		yield(&agent.ResponseUpdate{Role: message.RoleAssistant, Contents: message.Contents{
			&message.FunctionCallContent{CallID: fmt.Sprintf("call-%d", m.calls), Name: "run_tests", Arguments: "{}"},
		}}, nil)
	}
}

// testRunner is the tool's state: the first test run of each ticket fails with exit code 3, every later one passes.
type testRunner struct{ runs int }

func (r *testRunner) runTests(context.Context, struct{}) (string, error) {
	r.runs++
	if r.runs == 1 {
		return "exit=3 1 test failed: the refund lock is still held", nil
	}
	return "exit=0 all tests passed", nil
}

func run(out io.Writer) error {
	runner := &testRunner{}
	model := &scriptedModel{}
	runTests := functool.MustNew(functool.Config{Name: "run_tests",
		Description: "Runs the test suite and returns its exit status."}, runner.runTests)

	// The README's setup: a store, a development identity, your own check, and the classifier that reads "exit=3" in
	// the tool's result as a failure.
	store, err := inmemory.New()
	if err != nil {
		return err
	}
	identity, err := ae.DevelopmentIdentity(ae.DevelopmentOptions{Application: "quickstart"})
	if err != nil {
		return err
	}
	config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{runTests}}
	err = maf.UseAgentExperience(&config, maf.Options{
		Store:              store,
		Identity:           identity,
		Verify:             ae.ToolSucceeded("run_tests"),
		ClassifyToolResult: ae.ExitCode("run_tests"),
		// Not part of the setup: a fixed environment, so the output is the same on every machine. Keep the default.
		Environment: &ae.EnvironmentFingerprint{HostName: "quick-start", RuntimeVersion: "go", OperatingSystem: "any",
			ApplicationVersion: ae.Ptr("1.0.0-quick-start"), Metadata: map[string]string{}},
	})
	if err != nil {
		return err
	}
	// A provider's constructor (openaiprovider.NewChatCompletionsAgent, for one) takes the same configuration; the
	// stand-in is wired the way they are, with automatic tool invocation.
	triage := agent.New(agent.ProviderConfig{ProviderName: "scripted", Run: model.run,
		Middlewares: []agent.Middleware{toolautocall.New(toolautocall.Config{})}}, config)

	fmt.Fprintf(out, "Model: %s\n", standIn)
	var blocks []string
	for number, ticket := range []string{firstTicket, secondTicket} {
		runner.runs, model.block = 0, ""
		if _, err := triage.RunText(context.Background(), ticket).Collect(); err != nil {
			return err
		}
		blocks = append(blocks, model.block)
		memory := "no memory yet"
		if model.block != "" {
			memory = "lesson injected"
		}
		fmt.Fprintf(out, "Run %d (%s): %s  (%d test runs)\n", number+1, memory, ticket, runner.runs)
	}

	fmt.Fprintln(out)
	fmt.Fprintln(out, "What run 2's model was handed, besides the ticket:")
	fmt.Fprintln(out)
	block := blocks[1]
	if block == "" {
		block = "(nothing: no lesson was injected)"
	}
	fmt.Fprintln(out, strings.TrimRight(block, "\n"))
	if blocks[0] != "" || blocks[1] == "" {
		return fmt.Errorf("the second run, and only the second, should have been handed the lesson")
	}
	return nil
}

func main() {
	// The development identity refuses outside Development/Test; a plain program has no environment set, so it runs.
	if err := run(os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
