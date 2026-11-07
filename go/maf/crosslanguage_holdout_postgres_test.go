//go:build postgres

package maf

// Story 25.6's cross-language proof of the holdout, the Go half (contract 1.8). A Go agent with the holdout at rate
// 0.5 learns a lesson in one run, then runs eight measured runs on a similar task, each finalized (some verified, some
// failed), through the Go PostgreSQL store. The rows it wrote (the records, their initial lifecycle events and the
// holdout assignments, every column in PostgreSQL's text form) must be exactly
// testdata/cross-language/go-agent-holdout-rows.json. The .NET contract tests insert that file, with the Python
// agent's, into one database and run the .NET benefit estimate over the scope: every finalized run counts in its arm.
// Set AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1 to rewrite the file on purpose, then read the diff.
//
// Contract 1.9: every run records its model ("go-agent-model"). The fleet test inserts this file and the Python
// agent's into one database and runs this port's EstimateBenefit over the scope, split by model; the estimate must be
// testdata/cross-language/go-fleet-holdout-estimate.json, which the .NET contract tests compare with the .NET
// estimate over the same rows (and the Python port's file with its own).

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"github.com/jackc/pgx/v5"
	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/tool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

var (
	goldenHoldoutRows = filepath.Join("testdata", "cross-language", "go-agent-holdout-rows.json")
	holdoutScope      = ae.Scope{TenantID: "cross-language-holdout", ApplicationID: "holdout-app",
		ProjectID: "holdout-project", AgentID: ae.Ptr("holdout-agent")}
	holdoutColumns = []string{"run_id", "experience_id", "revision", "arm", "tenant_id", "application_id",
		"project_id", "team_id", "agent_id", "user_id", "assigned_at"}
)

// holdoutRunID is the Go agent's Nth run: 0 learns the lesson, 1 to 8 are measured.
func holdoutRunID(n int) ae.UUID {
	return ae.MustParseUUID(fmt.Sprintf("60a0a000-0000-4000-8000-%012x", n))
}

// fixedRunIDs is a NewID whose first ID after next(run) is run (the ID capture starts the invocation's run with), and
// every other ID a fixed sequence.
type fixedRunIDs struct {
	mu      sync.Mutex
	pending *ae.UUID
	counter int
}

func (f *fixedRunIDs) next(run ae.UUID) { f.mu.Lock(); f.pending = &run; f.mu.Unlock() }

func (f *fixedRunIDs) newID() ae.UUID {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.pending != nil {
		id := *f.pending
		f.pending = nil
		return id
	}
	f.counter++
	return ae.MustParseUUID(fmt.Sprintf("60a0900d-0000-4000-8000-%012d", f.counter))
}

// rowsIn are the rows of a table in holdoutScope's tenant and agent, every given column in PostgreSQL's text form
// (NULL as nil), read as the superuser in UTC.
func rowsIn(t *testing.T, admin pgtest.Server, database, table string, columns []string, order string) []any {
	t.Helper()
	ctx := context.Background()
	conn, err := pgx.Connect(ctx, admin.Admin(database))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	if _, err := conn.Exec(ctx, "SET TIME ZONE 'UTC'"); err != nil {
		t.Fatal(err)
	}
	selected := make([]string, len(columns))
	for i, column := range columns {
		selected[i] = pgx.Identifier{column}.Sanitize() + "::text"
	}
	rows, err := conn.Query(ctx, "SELECT "+strings.Join(selected, ", ")+" FROM agent_experience."+table+
		" WHERE tenant_id = $1 AND agent_id = $2 ORDER BY "+order, holdoutScope.TenantID, *holdoutScope.AgentID)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	result := []any{}
	for rows.Next() {
		values := make([]*string, len(columns))
		targets := make([]any, len(columns))
		for i := range values {
			targets[i] = &values[i]
		}
		if err := rows.Scan(targets...); err != nil {
			t.Fatal(err)
		}
		row := ae.NewObject()
		for i, column := range columns {
			if values[i] == nil {
				row.Set(column, nil)
			} else {
				row.Set(column, *values[i])
			}
		}
		result = append(result, row)
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	return result
}

func TestTheGoAgentRecordsTheHoldoutTheDotNetEstimateReads(t *testing.T) {
	admin := pgtest.Require(t)
	database := createDatabase(t, admin, "go_holdout")
	store := newStore(t, admin, database)

	previous := sourceVersion
	sourceVersion = goldenSourceVersion
	t.Cleanup(func() { sourceVersion = previous })

	// The learning run passes; the measured runs fail on 2, 5 and 7.
	results := []string{"exit=0", "exit=0", "exit=3", "exit=0", "exit=0", "exit=3", "exit=0", "exit=3", "exit=0"}
	h, ids, held := &host{}, &fixedRunIDs{}, &holdoutRecordings{}
	config := agent.Config{Name: "Holdout Agent", Tools: []tool.Tool{exits(results...)}}
	options := h.options(Options{
		Store:              store,
		Identity:           ae.Identity{Principal: "go-principal", Scope: holdoutScope},
		Verify:             ae.ToolSucceeded("run_tests", ae.WithArtifactRevision("build-7")),
		ClassifyToolResult: ae.ExitCode("run_tests"),
		Environment:        &crossEnvironment,
		Clock:              fixedClock(),
		NewID:              ids.newID,
		Holdout:            HoldoutOptions{Rate: 0.5},
		RetrievalPolicy:    postgresRetrieval,
		// Contract 1.9: every run records the model, so the fleet's estimate splits by it.
		Model: "go-agent-model",
	})
	options.OnHoldoutRecorded = held.add
	options.OnWithheld = held.addWithheld
	if err := UseAgentExperience(&config, options); err != nil {
		t.Fatal(err)
	}
	model := newScripted(1)
	model.arguments = func(int) string { return `{"suite":"unit"}` }
	a := newAgent(model, config)
	for n := 0; n <= 8; n++ {
		ids.next(holdoutRunID(n))
		runText(t, a, fmt.Sprintf("Ticket #%d: a refund is stuck on a lock. Triage it.", 6100+n))
		// Each run's assignment is recorded before the next run starts, so the rows are the same every time.
		closeRecordings(t, &config)
	}
	if len(h.captureFailures) != 0 || len(h.injectionFailures) != 0 || len(h.finalized) != 9 {
		t.Fatalf("capture %+v, injection %+v, %d finalized", h.captureFailures, h.injectionFailures, len(h.finalized))
	}
	recorded, withheld := held.all()
	for _, result := range recorded {
		if result.Outcome != InjectionRecorded {
			t.Fatalf("holdout record %+v", result)
		}
	}

	rows := ae.NewObject(
		ae.Member{Key: "experience_records", Value: rowsIn(t, admin, database, "experience_records", recordColumns,
			"source_run_id")},
		ae.Member{Key: "lifecycle_events", Value: rowsIn(t, admin, database, "lifecycle_events", eventColumns,
			"occurred_at, event_id")},
		ae.Member{Key: "experience_holdout_assignments", Value: rowsIn(t, admin, database,
			"experience_holdout_assignments", holdoutColumns, "run_id, experience_id")},
	)
	written := goldenText(t, rows)
	if os.Getenv(updateVariable) == "1" {
		if err := os.WriteFile(goldenHoldoutRows, written, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	checkedIn, err := os.ReadFile(goldenHoldoutRows)
	if err != nil {
		t.Fatalf("%v (set %s=1 to write it)", err, updateVariable)
	}
	if !bytes.Equal(checkedIn, written) {
		expected, _ := ae.DecodeJSON(checkedIn)
		t.Fatalf("the rows differ from %s: %s (set %s=1 to rewrite it on purpose)", goldenHoldoutRows,
			ct.Difference(expected, ct.AsWritten(rows), "$", ct.Options{}), updateVariable)
	}

	var decoded struct {
		Records []map[string]*string `json:"experience_records"`
		Events  []map[string]*string `json:"lifecycle_events"`
		Holdout []map[string]*string `json:"experience_holdout_assignments"`
	}
	if err := json.Unmarshal(written, &decoded); err != nil {
		t.Fatal(err)
	}
	if len(decoded.Records) != 9 || len(decoded.Events) != 9 {
		t.Fatalf("%d records and %d events for 9 runs", len(decoded.Records), len(decoded.Events))
	}
	// Every measured run has rows, all in the arm the assignment function gives it; the learning run has none.
	arms := map[string]map[string]bool{}
	for _, row := range decoded.Holdout {
		run := *row["run_id"]
		if arms[run] == nil {
			arms[run] = map[string]bool{}
		}
		arms[run][*row["arm"]] = true
	}
	held0, treated := 0, 0
	for n := 1; n <= 8; n++ {
		run := holdoutRunID(n)
		want := ae.AssignHoldout(run, ae.DefaultHoldoutSalt, 0.5)
		if len(arms[run.String()]) != 1 || !arms[run.String()][string(want)] {
			t.Fatalf("run %d: arms %v, the assignment function gives %s", n, arms[run.String()], want)
		}
		if want == ae.HoldoutWithheld {
			held0++
		} else {
			treated++
		}
	}
	if arms[holdoutRunID(0).String()] != nil || len(arms) != 8 || held0 != 3 || treated != 5 || len(withheld) != 3 {
		t.Fatalf("arms %v, %d held out, %d treated, %d withheld", arms, held0, treated, len(withheld))
	}
}
