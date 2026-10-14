package maf_test

import (
	"context"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/provider/openaiprovider"
	"github.com/microsoft/agent-framework-go/tool"
	"github.com/openai/openai-go/v3"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/fabbrik/AgentExperience.NET/go/inmemory"
	"github.com/fabbrik/AgentExperience.NET/go/maf"
)

// quickStart is the README's quick start, word for word between its first and last line, so it keeps compiling.
func quickStart(ctx context.Context, client openai.Client, model string, runTests tool.FuncTool) error {
	store, err := inmemory.New()
	if err != nil {
		return err
	}
	identity, err := ae.DevelopmentIdentity(ae.DevelopmentOptions{}) // one local identity: development and tests only
	if err != nil {
		return err
	}
	config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{runTests}}
	if err := maf.UseAgentExperience(&config, maf.Options{Store: store, Identity: identity,
		Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}); err != nil { // your check, never the model
		return err
	}
	triage := openaiprovider.NewChatCompletionsAgent(client, openaiprovider.AgentConfig{Config: config, Model: model})
	if _, err := triage.RunText(ctx, "Ticket #4812: a refund is stuck on a lock. Triage it.").Collect(); err != nil {
		return err
	}
	_, err = triage.RunText(ctx, "Ticket #4977: a refund is stuck on a lock again. Triage it.").Collect() // gets the lesson
	return err
}

// The quick start with an OpenAI model: compiled, not run, as it needs a key. examples/quickstart runs the same setup
// with a scripted model and checks its output.
func Example() {
	_ = quickStart
}
