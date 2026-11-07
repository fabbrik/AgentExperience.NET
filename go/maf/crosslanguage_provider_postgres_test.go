//go:build postgres

package maf

// Story 27.3's cross-language proof (contract 1.11), the Go half on PostgreSQL. The Go agent's eight measured runs
// (crosslanguage_provider_test.go) go through the Go PostgreSQL store; the rows it wrote (the records, their initial
// lifecycle events and the provider holdout rows, every column in PostgreSQL's text form) must be exactly
// testdata/cross-language/go-agent-provider-rows.json. Then the .NET, Python and Go agents' rows files go into one
// database, and this port's EstimateProviderBenefit over the fleet's scope and key must be
// testdata/cross-language/go-provider-fleet-estimate.json, which the .NET contract tests compare with the .NET
// estimate over the same rows. Set AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1 to rewrite the files on purpose, then read
// the diff.

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

var (
	goldenProviderFleetEstimate = filepath.Join("testdata", "cross-language", "go-provider-fleet-estimate.json")
	providerFleetRowsFiles      = []string{
		filepath.Join(ct.Repository, "tests", "AgentExperience.Contract.Tests", "cross-language",
			"dotnet-provider-fleet-rows.json"),
		filepath.Join(ct.Repository, "python", "tests", "cross-language", "python-agent-provider-rows.json"),
		goldenProviderRows,
	}
	providerTables = []string{"experience_records", "lifecycle_events", "experience_provider_holdout_assignments"}
)

// providerRowsIn are the rows of a table in providerScope's tenant, every given column in PostgreSQL's text form
// (NULL as nil), read as the superuser in UTC.
func providerRowsIn(t *testing.T, admin pgtest.Server, database, table string, columns []string, order string) []any {
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
		" WHERE tenant_id = $1 ORDER BY "+order, providerScope.TenantID)
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

func TestTheGoAgentWritesTheProviderRowsTheDotNetEstimateReads(t *testing.T) {
	admin := pgtest.Require(t)
	database := createDatabase(t, admin, "go_provider")
	runProviderAgent(t, newStore(t, admin, database))

	rows := ae.NewObject(
		ae.Member{Key: "experience_records", Value: providerRowsIn(t, admin, database, "experience_records",
			recordColumns, "source_run_id")},
		ae.Member{Key: "lifecycle_events", Value: providerRowsIn(t, admin, database, "lifecycle_events", eventColumns,
			"occurred_at, event_id")},
		ae.Member{Key: "experience_provider_holdout_assignments", Value: providerRowsIn(t, admin, database,
			"experience_provider_holdout_assignments", ct.ProviderAssignmentColumns, "run_id, provider_key")},
	)
	written := goldenText(t, rows)
	if os.Getenv(updateVariable) == "1" {
		if err := os.WriteFile(goldenProviderRows, written, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	checkedIn, err := os.ReadFile(goldenProviderRows)
	if err != nil {
		t.Fatalf("%v (set %s=1 to write it)", err, updateVariable)
	}
	if !bytes.Equal(checkedIn, written) {
		expected, _ := ae.DecodeJSON(checkedIn)
		t.Fatalf("the rows differ from %s: %s (set %s=1 to rewrite it on purpose)", goldenProviderRows,
			ct.Difference(expected, ct.AsWritten(rows), "$", ct.Options{}), updateVariable)
	}
}

func TestTheProviderFleetEstimateOverTheDotNetPythonAndGoAgentsRows(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "go_provider_fleet")
	store := newStore(t, admin, database)
	conn, err := pgx.Connect(ctx, admin.Admin(database))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	runs := 0
	for _, path := range providerFleetRowsFiles {
		rows, err := ct.ReadJSON(path)
		if err != nil {
			t.Fatal(err)
		}
		for _, table := range providerTables {
			value, _ := rows.(*ae.Object).Get(table)
			insertRawRows(t, conn, table, value.([]any))
			if table == "experience_provider_holdout_assignments" {
				for _, item := range value.([]any) {
					row := item.(*ae.Object)
					run, _ := row.Get("run_id")
					arm, _ := row.Get("arm")
					want := ae.AssignProviderHoldout(ae.MustParseUUID(run.(string)), fleetProviderKey,
						ae.DefaultHoldoutSalt, 0.5)
					if arm.(string) != string(want) {
						t.Fatalf("%s: run %s recorded in %s, the assignment function gives %s", path, run, arm, want)
					}
					runs++
				}
			}
		}
	}

	estimate, err := ae.EstimateProviderBenefit(ctx, store, ae.BoundsOf(providerScope), providerScope,
		fleetProviderKey, ae.BenefitOptions{})
	if err != nil {
		t.Fatal(err)
	}
	overall := estimate.Overall
	if !estimate.Tracked || estimate.Outcome != ae.BenefitFound || overall.Pending != 0 || overall.MixedArm != 0 ||
		overall.Treatment.Runs+overall.Holdout.Runs+overall.Unverified != runs || runs != 24 {
		t.Fatalf("the fleet's %d runs are not all counted once: %+v", runs, overall)
	}
	actual := ct.ProviderFleetEstimateJSON(estimate)
	written := goldenText(t, actual)
	if os.Getenv(updateVariable) == "1" {
		if err := os.WriteFile(goldenProviderFleetEstimate, written, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	expected, err := ct.ReadJSON(goldenProviderFleetEstimate)
	if err != nil {
		t.Fatalf("%v (set %s=1 to write it)", err, updateVariable)
	}
	if d := ct.EstimateDifference(expected, actual); d != "" {
		t.Fatalf("the estimate differs from %s: %s (set %s=1 to rewrite it on purpose)", goldenProviderFleetEstimate,
			d, updateVariable)
	}
}
