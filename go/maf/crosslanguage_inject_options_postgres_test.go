//go:build postgres

package maf

// Story 27.4b's cross-language proof of the injection options, the Go half (contract 1.13). The .NET contract tests
// insert a seed as a search seed is inserted and run a .NET agent over the .NET PostgreSQL store once per configuration
// (a receiving agent and a model-authored policy), and check in the seed, the configurations and what .NET injected.
// Here the same seed goes into a database through the Go store, and a Go agent with each configuration, at the same
// instant, is run on the same one user message: it must inject the same block, byte for byte, with the same outcome,
// message role, IDs, byte count, omissions and exclusions.

import (
	"context"
	"path/filepath"
	"sync"
	"testing"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

var dotnetInjectOptions = filepath.Join(ct.Repository, "tests", "AgentExperience.Contract.Tests", "cross-language",
	"dotnet-inject-options.json")

func TestGoAgentsInjectWhatDotNetAgentsInjectWithTheSameOptions(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	file := ct.MustReadJSON(dotnetInjectOptions).(*ae.Object)
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	scope := ct.ScopeOf(get(file, "scope"))
	taskText := get(file, "taskText").(string)
	now := ae.MustParseInstant(get(file, "now").(string))

	store := newStore(t, admin, createDatabase(t, admin, "xl_inject_options"))
	for _, stored := range get(file, "seed").([]any) {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
			t.Fatal(err)
		}
	}

	for _, item := range get(file, "agents").([]any) {
		entry := item.(*ae.Object)
		name := get(entry, "name").(string)
		t.Run(name, func(t *testing.T) {
			options, err := ct.InjectOptionsOf(get(entry, "options"))
			if err != nil {
				t.Fatal(err)
			}
			expected := get(entry, "expected").(*ae.Object)
			role := message.RoleUser
			if options.MessageRole == "System" {
				role = message.RoleSystem
			}
			var mu sync.Mutex
			var results []InjectionResult
			var failures []InjectionFailure
			config := agent.Config{Name: "inject-agent"}
			if err := UseAgentExperience(&config, Options{
				Store:                store,
				Identity:             ae.Identity{Principal: "go-principal", Scope: scope},
				Clock:                func() ae.Instant { return now },
				BlockOptions:         options.Block,
				MessageRole:          role,
				ModelAuthoredLessons: options.ModelAuthored,
				ReceivingAgent:       options.Agent,
				RetrievalPolicy:      postgresRetrieval,
				OnInjected: func(r InjectionResult) {
					mu.Lock()
					defer mu.Unlock()
					results = append(results, r)
				},
				OnInjectionFailure: func(f InjectionFailure) {
					mu.Lock()
					defer mu.Unlock()
					failures = append(failures, f)
				},
			}); err != nil {
				t.Fatal(err)
			}
			model := newScripted(0)
			runText(t, newAgent(model, config), taskText)
			if err := ContextProviderOf(&config).Close(ctx); err != nil {
				t.Fatal(err)
			}

			mu.Lock()
			defer mu.Unlock()
			if len(failures) != 0 || len(results) != 1 {
				t.Fatalf("%d results, failures %+v", len(results), failures)
			}
			result := results[0]
			blocks := model.blocks()
			var block, messageRole any
			if len(blocks) == 1 {
				block = blocks[0].Contents[0].(*message.TextContent).Text
				messageRole = "User"
				if blocks[0].Role == message.RoleSystem {
					messageRole = "System"
				}
			}
			injected, omitted, excluded := []any{}, []any{}, []any{}
			for _, id := range result.ExperienceIDs {
				injected = append(injected, id.String())
			}
			for _, o := range result.Omitted {
				var detail any
				if o.Detail != nil {
					detail = *o.Detail
				}
				omitted = append(omitted, ae.NewObject(ae.Member{Key: "experienceId", Value: o.ExperienceID.String()},
					ae.Member{Key: "reason", Value: string(o.Reason)}, ae.Member{Key: "detail", Value: detail}))
			}
			for _, e := range result.Excluded {
				excluded = append(excluded, ae.NewObject(ae.Member{Key: "experienceId", Value: e.ExperienceID.String()},
					ae.Member{Key: "reason", Value: string(e.Reason)}))
			}
			actual := ae.NewObject(
				ae.Member{Key: "outcome", Value: result.Outcome},
				ae.Member{Key: "messageRole", Value: messageRole},
				ae.Member{Key: "injected", Value: injected},
				ae.Member{Key: "byteCount", Value: result.ByteCount},
				ae.Member{Key: "omitted", Value: omitted},
				ae.Member{Key: "excluded", Value: excluded},
				ae.Member{Key: "block", Value: block},
			)
			if difference := ct.Difference(expected, ct.AsWritten(actual), "$", ct.Options{}); difference != "" {
				t.Fatalf("%s\nGo's block:\n%v", difference, block)
			}
		})
	}
}
