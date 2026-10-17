//go:build postgres

package maf

// Story 23.3's cross-language proof on PostgreSQL with pgvector (contract 1.6):
//
//   - .NET writes, Go reads: the records of the vector-search/nearest-first fixture are embedded by the contract's own
//     write statement (write-experience-embedding.sql, run as written, not by the Go store), and a Go agent whose
//     embedder returns the fixture's query vector finds them; without the embedder it finds nothing and says why.
//   - Go writes, Go reads: a Go agent stores a lesson, its embedding is written in the background through the Go store,
//     and a later reworded query finds it.

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

func TestARecordEmbeddedByTheContractsSQLIsFoundByAGoAgent(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "vector_nearest_first")
	store := newStore(t, admin, database)
	for _, record := range seedRecords(t, "vector-search", "nearest-first") {
		record := record
		if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
			t.Fatal(err)
		}
	}

	// The embeddings are written by the fixture's own statement, not by the Go store.
	pool, err := pgxpool.New(ctx, admin.Application(database))
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	statements, err := postgres.ContractStatements(postgres.WriteEmbeddingSQL)
	if err != nil {
		t.Fatal(err)
	}
	write := postgres.ToDriverPlaceholders(statements["write"])
	declareSQL, err := postgres.ContractSQL(postgres.DeclareBoundsSQL)
	if err != nil {
		t.Fatal(err)
	}
	declare := postgres.ToDriverPlaceholders(declareSQL)
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	steps := get(ct.MustReadJSON(filepath.Join(ct.Fixtures, "vector-search", "nearest-first", "steps.json")).(*ae.Object),
		"steps").([]any)
	var query ae.VectorQuery
	written := 0
	for _, item := range steps {
		step := item.(*ae.Object)
		if search := get(step, "search"); search != nil {
			query = ct.VectorQueryOf(search)
			continue
		}
		w := ct.EmbeddingWriteOf(get(step, "write"))
		declared := map[string]any{}
		for name, value := range ae.BoundsOf(w.Scope).Parameters() {
			declared[name] = value
		}
		declareArgs, err := declare.Args(declared)
		if err != nil {
			t.Fatal(err)
		}
		args, err := write.Args(map[string]any{
			"experience_id": [16]byte(w.ExperienceID), "tenant_id": w.Scope.TenantID,
			"application_id": w.Scope.ApplicationID, "project_id": w.Scope.ProjectID, "team_id": w.Scope.TeamID,
			"agent_id": w.Scope.AgentID, "user_id": w.Scope.UserID, "model_id": w.ModelID,
			"dimension": int32(w.Dimension), "content_hash": w.ContentHash, "source_revision": w.SourceRevision,
			"embedding": ae.VectorLiteral(w.Vector), "now": w.Now.Time(),
		})
		if err != nil {
			t.Fatal(err)
		}
		err = pgx.BeginFunc(ctx, pool, func(tx pgx.Tx) error {
			if _, err := tx.Exec(ctx, declare.SQL, declareArgs...); err != nil {
				return err
			}
			tag, err := tx.Exec(ctx, write.SQL, args...)
			if err == nil && tag.RowsAffected() != 1 {
				t.Fatalf("the contract's write wrote %d rows", tag.RowsAffected())
			}
			return err
		})
		if err != nil {
			t.Fatal(err)
		}
		written++
	}
	if written != 3 || query.ModelID == "" {
		t.Fatalf("%d writes, query %+v", written, query)
	}

	embedder := newFixed()
	embedder.vector, embedder.model = query.Vector, query.ModelID
	v := &vhost{host: newHost(t)}
	a, _, _ := wire(t, v, store, Options{Embedder: embedder})
	runText(t, a, paraphrase)
	if len(v.reasons()) != 0 || len(v.injectionFailures) != 0 {
		t.Fatalf("fallbacks %v, failures %+v", v.reasons(), v.injectionFailures)
	}
	if got := numbers(onlyInjection(t, v)); !equalInts(got, []int{3, 2, 1}) {
		t.Fatalf("%v", got)
	}

	// Without the embedder the same query finds nothing, and says why.
	plain := &vhost{host: newHost(t)}
	b, _, _ := wire(t, plain, store, Options{})
	runText(t, b, paraphrase)
	if reasons := plain.reasons(); len(plain.injections) != 0 || len(reasons) != 1 || reasons[0] != ae.FallbackNotConfigured {
		t.Fatalf("injections %+v, fallbacks %v", plain.injections, reasons)
	}

	// A model the rows were not embedded with is a ModelMismatch, not a failure.
	other := newFixed()
	other.vector, other.model = query.Vector, "another-model"
	mismatch := &vhost{host: newHost(t)}
	c, _, _ := wire(t, mismatch, store, Options{Embedder: other})
	runText(t, c, paraphrase)
	if reasons := mismatch.reasons(); len(mismatch.injections) != 0 || len(reasons) != 1 || reasons[0] != ae.FallbackModelMismatch {
		t.Fatalf("injections %+v, fallbacks %v", mismatch.injections, reasons)
	}
}

func TestAGoAgentStoresARecordAndItsEmbeddingInPostgreSQL(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "vector_roundtrip")
	store := newStore(t, admin, database)
	v := &vhost{host: newHost(t)}
	a, config := storing(t, v, store, Options{Embedder: newFixed(),
		Identity: ae.Identity{Principal: "go-principal", Scope: testScope}})
	runText(t, a, ticket)
	waitForIndex(t, config)
	if len(v.captureFailures) != 0 || len(v.records()) != 1 {
		t.Fatalf("failures %+v, %d records", v.captureFailures, len(v.records()))
	}

	conn, err := pgx.Connect(ctx, admin.Admin(database))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	var model, embedding, hash string
	var dimension, revision int
	if err := conn.QueryRow(ctx, "SELECT model_id, dimension, source_revision, embedding::text, content_hash "+
		"FROM agent_experience.experience_embeddings").Scan(&model, &dimension, &revision, &embedding, &hash); err != nil {
		t.Fatal(err)
	}
	record := v.records()[0]
	if model != vectorModel || dimension != 4 || int64(revision) != record.Revision || embedding != "[1,0,0,0]" ||
		hash != ae.ContentHash(vectorModel, ae.RecordSummary(&record)) {
		t.Fatalf("%s %d %d %s %s", model, dimension, revision, embedding, hash)
	}

	later := &vhost{host: newHost(t)}
	b, _, _ := wire(t, later, store, Options{Embedder: newFixed(),
		Identity: ae.Identity{Principal: "go-principal", Scope: testScope}})
	runText(t, b, "Zebra giraffe antelope")
	if len(later.injections) != 1 || len(later.reasons()) != 0 {
		t.Fatalf("injections %+v, fallbacks %v", later.injections, later.reasons())
	}
}
