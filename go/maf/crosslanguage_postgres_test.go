//go:build postgres

package maf

// Story 22.6's cross-language proof on PostgreSQL:
//
//   - Go writes, .NET reads: the Go agent writes its two runs through the Go PostgreSQL store, and they read back
//     exactly as testdata/cross-language/go-agent-records.json, the file the .NET contract tests'
//     GoAgentRecordsPostgresRoundTripTests read back through the .NET store unchanged. The raw rows, the injection the
//     second run recorded included (contract 1.4), are testdata/cross-language/go-agent-rows.json: the .NET test
//     reads the injection through the .NET lessons view and compares every column with what the .NET store records.
//   - .NET writes, Go reads: the records the .NET library finalized for the contract's finalize fixtures (generated
//     from .NET and checked against it on every build) are written into PostgreSQL as stored, and a Go agent on
//     their scope is injected with the one its task matches. No .NET process runs at test time: the fixtures are the
//     .NET-written records.
//
// The server and the databases are the postgres module's pgtest's, as its own PostgreSQL tests use them: each
// database migrated with the .NET migrator's own scripts and given the application role's privileges from the
// contract.

import (
	"bytes"
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/microsoft/agent-framework-go/agent"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

func TestMain(m *testing.M) { os.Exit(pgtest.Run(m)) }

func createDatabase(t *testing.T, admin pgtest.Server, label string) string {
	t.Helper()
	return pgtest.CreateDatabase(t, admin, "maf_"+label)
}

var goldenRows = filepath.Join("testdata", "cross-language", "go-agent-rows.json")

// The columns the Go store writes, in the order it writes them.
var (
	recordColumns = ae.ColumnNames
	eventColumns  = []string{"event_id", "experience_id", "tenant_id", "application_id", "project_id", "team_id",
		"agent_id", "user_id", "prior_status", "current_status", "reason", "producer", "occurred_at", "recorded_at",
		"expected_revision", "applied_revision", "actor"}
	injectionColumns = []string{"experience_id", "tenant_id", "application_id", "project_id", "team_id", "agent_id",
		"user_id", "last_injected_at", "injection_count"}
)

// rawRows are the rows of a table in scope, every given column in PostgreSQL's text form (NULL as nil), read as the
// superuser in UTC.
func rawRows(t *testing.T, admin pgtest.Server, database, table string, columns []string, order string) []any {
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
		" WHERE tenant_id = $1 AND agent_id = $2 ORDER BY "+order, crossScope.TenantID, *crossScope.AgentID)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	var result []any
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

// newStore is the Go PostgreSQL store on a pool, as the application role; the pool closes when the test ends.
func newStore(t *testing.T, admin pgtest.Server, database string) *postgres.Store {
	t.Helper()
	pool, err := pgxpool.New(context.Background(), admin.Application(database))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	store, err := postgres.NewStore(pool)
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func TestTheGoAgentWritesToPostgreSQLWhatTheDotNetStoreReads(t *testing.T) {
	admin := pgtest.Require(t)
	database := createDatabase(t, admin, "go_agent")
	store := newStore(t, admin, database)
	finalized, _ := runGoAgent(t, store)

	// The rows themselves, as the .NET test inserts them.
	rows := ae.NewObject(
		ae.Member{Key: "experience_records", Value: rawRows(t, admin, database, "experience_records", recordColumns,
			"source_run_id")},
		ae.Member{Key: "lifecycle_events", Value: rawRows(t, admin, database, "lifecycle_events", eventColumns,
			"occurred_at, event_id")},
		ae.Member{Key: "experience_injections", Value: rawRows(t, admin, database, "experience_injections",
			injectionColumns, "experience_id")},
	)
	written := goldenText(t, rows)
	if os.Getenv(updateVariable) == "1" {
		if err := os.WriteFile(goldenRows, written, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	checkedIn, err := os.ReadFile(goldenRows)
	if err != nil {
		t.Fatalf("%v (set %s=1 to write it)", err, updateVariable)
	}
	if !bytes.Equal(checkedIn, written) {
		expected, _ := ae.DecodeJSON(checkedIn)
		t.Fatalf("the rows differ from %s: %s (set %s=1 to rewrite it on purpose)", goldenRows,
			ct.Difference(expected, ct.AsWritten(rows), "$", ct.Options{}), updateVariable)
	}
	var decoded struct {
		Records    []map[string]*string `json:"experience_records"`
		Events     []map[string]*string `json:"lifecycle_events"`
		Injections []map[string]*string `json:"experience_injections"`
	}
	if err := json.Unmarshal(written, &decoded); err != nil {
		t.Fatal(err)
	}
	if len(decoded.Records) != len(finalized) || len(decoded.Events) != len(finalized) {
		t.Fatalf("%d records and %d events for %d runs", len(decoded.Records), len(decoded.Events), len(finalized))
	}
	// The second run's block delivered the first run's lesson: one injection row, for it.
	if len(decoded.Injections) != 1 || *decoded.Injections[0]["experience_id"] != finalized[0].Record.ExperienceID.String() {
		t.Fatalf("injections %v", decoded.Injections)
	}

	expected := readGolden(t)
	if len(expected) != len(finalized) {
		t.Fatalf("%d records were finalized, the file holds %d", len(finalized), len(expected))
	}
	bounds := ae.BoundsOf(crossScope)
	for i, f := range finalized {
		read, err := store.Get(context.Background(), bounds, crossScope, f.Record.ExperienceID)
		if err != nil || read == nil {
			t.Fatalf("record %d was not read back: %v", i, err)
		}
		// jsonb keeps a number's value, not its text, so numbers compare by value.
		if difference := ct.Difference(expected[i], ct.AsWritten(read.ToStored()), "$",
			ct.Options{IgnoreKind: true}); difference != "" {
			t.Fatal(difference)
		}
	}
}

// dotnetRecords are every record the .NET library finalized in the contract's finalize fixtures, once per
// experience ID.
func dotnetRecords(t *testing.T) []ae.ExperienceRecord {
	t.Helper()
	var records []ae.ExperienceRecord
	seen := map[ae.UUID]bool{}
	for _, name := range ct.Cases("finalize") {
		stored := ct.MustReadJSON(filepath.Join(ct.Fixtures, "finalize", name, "expected-record.json"))
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if !seen[record.ExperienceID] {
			seen[record.ExperienceID] = true
			records = append(records, record)
		}
	}
	return records
}

func TestALessonTheDotNetLibraryFinalizedIsInjectedIntoTheGoAgent(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "dotnet_lessons")
	store := newStore(t, admin, database)
	seeded := dotnetRecords(t)
	for i := range seeded {
		if err := store.InsertRecord(ctx, ae.BoundsOf(seeded[i].Scope), &seeded[i]); err != nil {
			t.Fatal(err)
		}
	}
	scope := ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project",
		AgentID: ae.Ptr("refund-agent")}
	var deploy *ae.ExperienceRecord
	quarantined := map[ae.UUID]bool{}
	for i, record := range seeded {
		if record.TaskID == "deploy-service" && record.Scope.TeamID == nil {
			deploy = &seeded[i]
		}
		if record.Status == ae.StatusQuarantined {
			quarantined[record.ExperienceID] = true
		}
	}
	if deploy == nil || len(quarantined) == 0 {
		t.Fatal("the fixtures no longer hold the records this test reads")
	}

	wire := func(scope ae.Scope) (*host, *scripted, *agent.Agent, *agent.Config) {
		h, model := &host{}, newScripted(0)
		config := &agent.Config{Name: "refund-agent"}
		if err := UseAgentExperience(config, h.options(Options{Store: store, RetrievalPolicy: postgresRetrieval,
			Identity: ae.Identity{Principal: "go-principal", Scope: scope}})); err != nil {
			t.Fatal(err)
		}
		return h, model, newAgent(model, *config), config
	}

	h, model, a, config := wire(scope)
	runText(t, a, "Deploy service X to prod")

	// The Go agent recorded that it injected the .NET lesson, in the lesson's own scope.
	closeRecordings(t, config)
	conn, err := pgx.Connect(ctx, admin.Admin(database))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	var injected string
	var count int64
	if err := conn.QueryRow(ctx, "SELECT experience_id::text, injection_count FROM agent_experience.experience_injections").
		Scan(&injected, &count); err != nil || injected != deploy.ExperienceID.String() || count != 1 {
		t.Fatalf("the injection row is %s x%d (%v)", injected, count, err)
	}
	if len(h.injectionFailures) != 0 || len(h.injections) != 1 || len(h.injections[0].ExperienceIDs) != 1 ||
		h.injections[0].ExperienceIDs[0] != deploy.ExperienceID {
		t.Fatalf("injections %+v, failures %+v", h.injections, h.injectionFailures)
	}
	blocks := model.blocks()
	if len(blocks) != 1 || model.blockCounts()[0] != 1 {
		t.Fatalf("%d blocks", len(blocks))
	}
	text := blocks[0].String()
	if !strings.HasPrefix(text, ae.BlockBegin) || !strings.Contains(text, "--- RECORD 1: "+deploy.TaskID+" ---") ||
		!strings.Contains(text, "Lesson: "+deploy.Reflection.Lesson) {
		t.Fatalf("the block is:\n%s", text)
	}

	// A quarantined .NET record is never injected: the refund task's block holds only the validated refund lessons.
	runText(t, a, "Refund ticket 43 is stuck in pending")
	if len(h.injections) != 2 || len(h.injections[1].ExperienceIDs) == 0 {
		t.Fatalf("injections %+v", h.injections)
	}
	for _, id := range h.injections[1].ExperienceIDs {
		if quarantined[id] {
			t.Fatalf("the quarantined record %s was injected", id)
		}
	}

	// Another agent's scope, on the same database, sees none of them.
	other := scope
	other.AgentID = ae.Ptr("other-agent")
	h2, model2, a2, _ := wire(other)
	runText(t, a2, "Deploy service X to prod")
	runText(t, a2, "Refund ticket 43 is stuck in pending")
	if len(h2.injections) != 0 || len(model2.blocks()) != 0 || len(h2.injectionFailures) != 0 {
		t.Fatalf("another scope was injected %+v", h2.injections)
	}
}
