//go:build postgres

package maf

// Contract 1.9's cross-language proof of the estimate, the Go half: the Go agent's and the Python agent's holdout rows
// (records, lifecycle events and assignments, every column in PostgreSQL's text form) go into one database, and this
// port's EstimateBenefit over the fleet's scope, split by model, must be
// testdata/cross-language/go-fleet-holdout-estimate.json, in the benefit-estimate fixtures' form plus tracked and
// outcome. The .NET contract tests estimate the same rows with AgentExperienceBenefit.EstimateAsync and compare the
// result with this file (and with the Python port's). Set AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1 to rewrite it on
// purpose, then read the diff.

import (
	"context"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

var (
	goldenFleetEstimate = filepath.Join("testdata", "cross-language", "go-fleet-holdout-estimate.json")
	fleetRowsFiles      = []string{
		goldenHoldoutRows,
		filepath.Join(ct.Repository, "python", "tests", "cross-language", "python-agent-holdout-rows.json"),
	}
)

// insertRawRows inserts rows (every column in PostgreSQL's text form, NULL as null) into an agent_experience table as
// the superuser, each value cast from text to its column's type.
func insertRawRows(t *testing.T, conn *pgx.Conn, table string, rows []any) {
	t.Helper()
	ctx := context.Background()
	types := map[string]string{}
	described, err := conn.Query(ctx, "SELECT column_name, format_type(a.atttypid, a.atttypmod) FROM "+
		"information_schema.columns c JOIN pg_catalog.pg_attribute a ON a.attrelid = ('agent_experience.' || "+
		"c.table_name)::regclass AND a.attname = c.column_name WHERE c.table_schema = 'agent_experience' AND "+
		"c.table_name = $1", table)
	if err != nil {
		t.Fatal(err)
	}
	for described.Next() {
		var name, kind string
		if err := described.Scan(&name, &kind); err != nil {
			t.Fatal(err)
		}
		types[name] = kind
	}
	described.Close()
	for _, item := range rows {
		row := item.(*ae.Object)
		columns, placeholders, values := []string{}, []string{}, []any{}
		for i, m := range row.Members() {
			kind, ok := types[m.Key]
			if !ok {
				t.Fatalf("%s has no column %s", table, m.Key)
			}
			columns = append(columns, pgx.Identifier{m.Key}.Sanitize())
			placeholders = append(placeholders, "$"+strconv.Itoa(i+1)+"::text::"+kind)
			values = append(values, m.Value)
		}
		if _, err := conn.Exec(ctx, "INSERT INTO agent_experience."+pgx.Identifier{table}.Sanitize()+" ("+
			strings.Join(columns, ", ")+") VALUES ("+strings.Join(placeholders, ", ")+")", values...); err != nil {
			t.Fatalf("%s: %v", table, err)
		}
	}
}

func TestTheFleetEstimateOverTheGoAndPythonAgentsRows(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "go_fleet")
	store := newStore(t, admin, database)
	conn, err := pgx.Connect(ctx, admin.Admin(database))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	for _, path := range fleetRowsFiles {
		rows, err := ct.ReadJSON(path)
		if err != nil {
			t.Fatal(err)
		}
		for _, table := range []string{"experience_records", "lifecycle_events", "experience_holdout_assignments"} {
			value, _ := rows.(*ae.Object).Get(table)
			insertRawRows(t, conn, table, value.([]any))
		}
	}

	estimate, err := ae.EstimateBenefit(ctx, store, ae.BoundsOf(holdoutScope), holdoutScope,
		ae.BenefitOptions{SplitByModel: true})
	if err != nil {
		t.Fatal(err)
	}
	if !estimate.Tracked || estimate.Outcome != ae.BenefitFound || estimate.Overall.Pending != 0 ||
		estimate.Overall.Treatment.Runs+estimate.Overall.Holdout.Runs+estimate.Overall.Unverified != 16 {
		t.Fatalf("the fleet's 16 measured runs are not all counted: %+v", estimate.Overall)
	}
	actual := ct.FleetEstimateJSON(estimate)
	written := goldenText(t, actual)
	if os.Getenv(updateVariable) == "1" {
		if err := os.WriteFile(goldenFleetEstimate, written, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	expected, err := ct.ReadJSON(goldenFleetEstimate)
	if err != nil {
		t.Fatalf("%v (set %s=1 to write it)", err, updateVariable)
	}
	if d := ct.EstimateDifference(expected, actual); d != "" {
		t.Fatalf("the estimate differs from %s: %s (set %s=1 to rewrite it on purpose)", goldenFleetEstimate, d,
			updateVariable)
	}
}
