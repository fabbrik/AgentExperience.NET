//go:build postgres

package pgtest_test

// Contract 1.6's embedding-write and vector-search fixtures, each step through the store's WriteEmbedding and
// SearchVectors (the embedded write-experience-embedding.sql and search-experience-vector.sql), then every row of the
// embeddings table as the owner reads it.

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

const vectorTolerance = 1e-9

// runVectorSteps seeds a fixture case, runs its steps and compares them and the embeddings table with expected.json.
func runVectorSteps(t *testing.T, family, name string) {
	t.Helper()
	admin := requireServer(t)
	ctx := context.Background()
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	seed := ct.MustReadJSON(fixture(family, name, "seed.json")).([]any)
	steps := get(ct.MustReadJSON(fixture(family, name, "steps.json")).(*ae.Object), "steps").([]any)
	expected := ct.MustReadJSON(fixture(family, name, "expected.json")).(*ae.Object)

	database := createDatabase(t, admin, family+"_"+name)
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	for _, stored := range seed {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
			t.Fatal(err)
		}
	}
	actual := []any{}
	for _, item := range steps {
		step := item.(*ae.Object)
		switch {
		case get(step, "write") != nil:
			write := ct.EmbeddingWriteOf(get(step, "write"))
			result, err := store.WriteEmbedding(ctx, ae.BoundsOf(write.Scope), write)
			if err != nil {
				t.Fatal(err)
			}
			actual = append(actual, ct.WriteOutcome(result))
		case get(step, "search") != nil:
			query := ct.VectorQueryOf(get(step, "search"))
			result, err := store.SearchVectors(ctx, ae.BoundsOf(query.Scope), query)
			if err != nil {
				t.Fatal(err)
			}
			actual = append(actual, ct.SearchOutcome(result))
		case get(step, "setRevision") != nil:
			revision := get(step, "setRevision").(*ae.Object)
			if _, err := owner.Exec(ctx, "UPDATE agent_experience.experience_records SET revision = $1::text::bigint "+
				"WHERE experience_id = $2::text::uuid", string(get(revision, "revision").(json.Number)),
				get(revision, "experienceId")); err != nil {
				t.Fatal(err)
			}
			actual = append(actual, nil)
		default:
			eraseStep(t, owner, get(step, "erase").(*ae.Object))
			actual = append(actual, nil)
		}
	}
	if difference := ct.Difference(get(expected, "steps"), ct.AsWritten(actual), "$.steps",
		ct.Options{Tolerance: vectorTolerance, IgnoreKind: true}); difference != "" {
		t.Fatal(difference)
	}

	expectedRows := get(expected, "experience_embeddings").([]any)
	columns := []string{"experience_id"}
	if len(expectedRows) > 0 {
		columns = expectedRows[0].(*ae.Object).Keys()
	}
	if _, err := owner.Exec(ctx, "SET TIME ZONE 'UTC'"); err != nil {
		t.Fatal(err)
	}
	selected := make([]string, len(columns))
	for i, column := range columns {
		selected[i] = pgx.Identifier{column}.Sanitize() + "::text"
	}
	rows, err := owner.Query(ctx, "SELECT "+strings.Join(selected, ", ")+
		" FROM agent_experience.experience_embeddings ORDER BY experience_id")
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	stored := []any{}
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
		stored = append(stored, row)
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if difference := ct.Difference(expectedRows, stored, "$.experience_embeddings", ct.Options{}); difference != "" {
		t.Fatal(difference)
	}
}

func TestEmbeddingWrite(t *testing.T) {
	for _, name := range ct.Cases("embedding-write") {
		t.Run(name, func(t *testing.T) { runVectorSteps(t, "embedding-write", name) })
	}
}

func TestVectorSearch(t *testing.T) {
	for _, name := range ct.Cases("vector-search") {
		t.Run(name, func(t *testing.T) { runVectorSteps(t, "vector-search", name) })
	}
}

func TestAnInvalidEmbeddingWriteOrSearchRunsNoStatementAgainstPostgres(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "vector_invalid"))
	scope := ae.Scope{TenantID: "contract-tenant", ApplicationID: "contract-app", ProjectID: "contract-project"}
	if _, err := store.WriteEmbedding(ctx, ae.BoundsOf(scope), ae.EmbeddingWrite{ExperienceID: ae.NewRandomUUID(),
		Scope: scope, ModelID: "m", Dimension: 3, ContentHash: "h", Vector: []float32{1, 2}, Now: ae.Now()}); err == nil {
		t.Fatal("a vector of the wrong length was written")
	}
	if _, err := store.SearchVectors(ctx, ae.BoundsOf(scope),
		ae.VectorQuery{Scope: scope, ModelID: " ", Vector: []float32{1}}); err == nil {
		t.Fatal("a blank model ID was searched")
	}
}

func TestAWriteOutsideTheBoundsIsMissing(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "vector_bounds"))
	finalized, _, err := ct.RunFinalizeCase(ct.MustReadJSON(fixture("finalize", "verified-after-one-failed-attempt",
		"input.json")))
	if err != nil {
		t.Fatal(err)
	}
	record := finalized.Record
	if err := store.InsertFinalized(ctx, ae.BoundsOf(record.Scope), &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}
	other := record.Scope
	other.TenantID = "another-tenant"
	write := ae.EmbeddingWrite{ExperienceID: record.ExperienceID, Scope: record.Scope, ModelID: "m", Dimension: 2,
		ContentHash: "h", SourceRevision: record.Revision, Vector: []float32{1, 0}, Now: ae.Now()}
	// The record's scope is outside the bounds the host declared: row-level security hides it.
	result, err := store.WriteEmbedding(ctx, ae.BoundsOf(other), write)
	if err != nil || result.Outcome != ae.EmbeddingMissing {
		t.Fatalf("%+v, %v", result, err)
	}
	result, err = store.WriteEmbedding(ctx, ae.BoundsOf(record.Scope), write)
	if err != nil || result.Outcome != ae.EmbeddingWritten {
		t.Fatalf("%+v, %v", result, err)
	}
}
