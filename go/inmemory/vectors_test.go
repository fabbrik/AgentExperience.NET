package inmemory

// Contract 1.6 on the in-memory store: the fixtures that need no database, and the rules the fixtures do not state.

import (
	"context"
	"errors"
	"math"
	"path/filepath"
	"sync"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

const vectorTolerance = 1e-6

func fixtureSteps(t *testing.T, family, name string) ([]ae.ExperienceRecord, []any, []any) {
	t.Helper()
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	var records []ae.ExperienceRecord
	for _, stored := range ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, "seed.json")).([]any) {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		records = append(records, record)
	}
	steps := get(ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, "steps.json")).(*ae.Object), "steps")
	expected := get(ct.MustReadJSON(filepath.Join(ct.Fixtures, family, name, "expected.json")).(*ae.Object), "steps")
	return records, steps.([]any), expected.([]any)
}

func TestTheVectorFixturesThatNeedNoDatabaseGiveTheirOutcomes(t *testing.T) {
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	ran := 0
	for _, family := range []string{"embedding-write", "vector-search"} {
		for _, name := range ct.Cases(family) {
			records, steps, expected := fixtureSteps(t, family, name)
			needsDatabase := false
			for _, item := range steps {
				step := item.(*ae.Object)
				needsDatabase = needsDatabase || get(step, "erase") != nil || get(step, "setRevision") != nil
			}
			if needsDatabase {
				continue
			}
			ran++
			t.Run(family+"/"+name, func(t *testing.T) {
				store, err := New(records...)
				if err != nil {
					t.Fatal(err)
				}
				ctx := context.Background()
				for i, item := range steps {
					step := item.(*ae.Object)
					var actual any
					if write := get(step, "write"); write != nil {
						w := ct.EmbeddingWriteOf(write)
						result, err := store.WriteEmbedding(ctx, ae.BoundsOf(w.Scope), w)
						if err != nil {
							t.Fatal(err)
						}
						actual = ct.WriteOutcome(result)
					} else {
						q := ct.VectorQueryOf(get(step, "search"))
						result, err := store.SearchVectors(ctx, ae.BoundsOf(q.Scope), q)
						if err != nil {
							t.Fatal(err)
						}
						actual = ct.SearchOutcome(result)
					}
					if difference := ct.Difference(expected[i], ct.AsWritten(actual), "$",
						ct.Options{Tolerance: vectorTolerance, IgnoreKind: true}); difference != "" {
						t.Fatalf("step %d: %s", i, difference)
					}
				}
			})
		}
	}
	if ran == 0 {
		t.Fatal("no vector fixture ran")
	}
}

func embedded(t *testing.T, records ...ae.ExperienceRecord) *Store {
	t.Helper()
	store, err := New(records...)
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func writeOf(r ae.ExperienceRecord, model string, revision int64, vector ...float32) ae.EmbeddingWrite {
	return ae.EmbeddingWrite{ExperienceID: r.ExperienceID, Scope: r.Scope, ModelID: model, Dimension: len(vector),
		ContentHash: "h", SourceRevision: revision, Vector: vector, Now: at}
}

func TestANewerEmbeddingIsNeverReplacedByAnOlderOneAndAMovedRecordIsStale(t *testing.T) {
	r := record(1, "rotate-keys", "Rotate the keys", "Rotate first.", ae.StatusValidated, 0.9)
	r.Revision = 2
	store := embedded(t, r)
	ctx, bounds := context.Background(), ae.BoundsOf(scope)

	result, err := store.WriteEmbedding(ctx, bounds, writeOf(r, "m", 2, 0, 1))
	if err != nil || result.Outcome != ae.EmbeddingWritten {
		t.Fatalf("%+v, %v", result, err)
	}
	result, err = store.WriteEmbedding(ctx, bounds, writeOf(r, "m", 1, 1, 0))
	if err != nil || result.Outcome != ae.EmbeddingStale || result.CurrentRevision != 2 {
		t.Fatalf("%+v, %v", result, err)
	}
	if held, _ := store.Embedding(r.ExperienceID); held.SourceRevision != 2 || held.Vector[1] != 1 {
		t.Fatalf("%+v", held)
	}
	other := scope
	other.AgentID = ae.Ptr("other")
	missing := writeOf(r, "m", 2, 0, 1)
	missing.Scope = other
	if result, err = store.WriteEmbedding(ctx, ae.BoundsOf(other), missing); err != nil || result.Outcome != ae.EmbeddingMissing {
		t.Fatalf("%+v, %v", result, err)
	}
	// Outside the bounds the host declared, the record is not there.
	if result, err = store.WriteEmbedding(ctx, ae.BoundsOf(other), writeOf(r, "m", 2, 0, 1)); err != nil ||
		result.Outcome != ae.EmbeddingMissing {
		t.Fatalf("%+v, %v", result, err)
	}
}

func TestVectorsAreCompared32BitAndTiesAreOrderedByIDAndNaNIsLast(t *testing.T) {
	a := record(1, "a", "A summary", "Lesson a", ae.StatusValidated, 0.9)
	b := record(2, "b", "B summary", "Lesson b", ae.StatusValidated, 0.9)
	c := record(3, "c", "C summary", "Lesson c", ae.StatusValidated, 0.9)
	store := embedded(t, c, b, a)
	ctx, bounds := context.Background(), ae.BoundsOf(scope)
	for _, r := range []ae.ExperienceRecord{c, b} {
		if _, err := store.WriteEmbedding(ctx, bounds, writeOf(r, "m", 1, 1, 0)); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := store.WriteEmbedding(ctx, bounds, writeOf(a, "m", 1, 0, 0)); err != nil { // no magnitude
		t.Fatal(err)
	}
	result, err := store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{2, 0}})
	if err != nil || result.Outcome != ae.VectorFound || len(result.Hits) != 3 {
		t.Fatalf("%+v, %v", result, err)
	}
	if result.Hits[0].Record.ExperienceID != b.ExperienceID || result.Hits[1].Record.ExperienceID != c.ExperienceID ||
		result.Hits[2].Record.ExperienceID != a.ExperienceID || !math.IsNaN(result.Hits[2].Distance) ||
		result.Hits[2].Relevance() != 0 || result.Hits[0].Distance != 0 {
		t.Fatalf("%+v", result.Hits)
	}
	// A limit keeps the nearest.
	limited, _ := store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{2, 0}, Limit: 1})
	if len(limited.Hits) != 1 || limited.Hits[0].Record.ExperienceID != b.ExperienceID {
		t.Fatalf("%+v", limited.Hits)
	}
	// The hits are copies: changing one changes nothing stored.
	result.Hits[0].Record.TaskID = "changed"
	if again, _ := store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{2, 0}}); again.Hits[0].Record.TaskID != "b" {
		t.Fatal("a hit shares memory with the store")
	}
	// Nothing outside the bounds.
	narrow := ae.BoundsOf(ae.Scope{TenantID: "acme", ApplicationID: "support", ProjectID: "refunds", AgentID: ae.Ptr("someone-else")})
	if found, err := store.SearchVectors(ctx, narrow, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{2, 0}}); err != nil ||
		len(found.Hits) != 0 {
		t.Fatalf("%+v, %v", found, err)
	}
}

func TestAnEmptyVectorSearchSaysWhy(t *testing.T) {
	r := record(1, "a", "A summary", "Lesson", ae.StatusValidated, 0.9)
	store := embedded(t, r)
	ctx, bounds := context.Background(), ae.BoundsOf(scope)
	search := func(model string, vector ...float32) ae.VectorSearchOutcome {
		result, err := store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: model, Vector: vector})
		if err != nil || len(result.Hits) != 0 {
			t.Fatalf("%+v, %v", result, err)
		}
		return result.Outcome
	}
	if outcome := search("m", 1, 0); outcome != ae.VectorFound {
		t.Fatalf("nothing stored: %s", outcome)
	}
	if _, err := store.WriteEmbedding(ctx, bounds, writeOf(r, "m", 1, 1, 0)); err != nil {
		t.Fatal(err)
	}
	if outcome := search("another", 1, 0); outcome != ae.VectorModelMismatch {
		t.Fatalf("another model: %s", outcome)
	}
	if outcome := search("m", 1, 0, 0); outcome != ae.VectorDimensionMismatch {
		t.Fatalf("another dimension: %s", outcome)
	}
}

func TestVectorCallsRefuseInvalidInputAndADoneContext(t *testing.T) {
	r := record(1, "a", "A summary", "Lesson", ae.StatusValidated, 0.9)
	store := embedded(t, r)
	ctx, bounds := context.Background(), ae.BoundsOf(scope)
	bad := writeOf(r, "m", 1, 1, 0)
	bad.Vector = []float32{1, float32(math.NaN())}
	if _, err := store.WriteEmbedding(ctx, bounds, bad); err == nil {
		t.Fatal("a non-finite vector was stored")
	}
	if _, ok := store.Embedding(r.ExperienceID); ok {
		t.Fatal("a refused write stored something")
	}
	if _, err := store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: "m"}); err == nil {
		t.Fatal("an empty query vector was searched")
	}
	done, cancel := context.WithCancel(ctx)
	cancel()
	if _, err := store.WriteEmbedding(done, bounds, writeOf(r, "m", 1, 1, 0)); !errors.Is(err, context.Canceled) {
		t.Fatalf("write: %v", err)
	}
	if _, err := store.SearchVectors(done, bounds, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{1}}); !errors.Is(err, context.Canceled) {
		t.Fatalf("search: %v", err)
	}
}

func TestVectorCallsAreSafeToRunTogether(t *testing.T) {
	var records []ae.ExperienceRecord
	for n := byte(1); n <= 20; n++ {
		records = append(records, record(n, "task", "Summary", "Lesson", ae.StatusValidated, 0.9))
	}
	store := embedded(t, records...)
	ctx, bounds := context.Background(), ae.BoundsOf(scope)
	var wg sync.WaitGroup
	for _, r := range records {
		wg.Add(2)
		go func() {
			defer wg.Done()
			_, _ = store.WriteEmbedding(ctx, bounds, writeOf(r, "m", 1, 1, 0))
		}()
		go func() {
			defer wg.Done()
			_, _ = store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{1, 0}})
		}()
	}
	wg.Wait()
	result, err := store.SearchVectors(ctx, bounds, ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{1, 0}})
	if err != nil || len(result.Hits) != 20 {
		t.Fatalf("%d hits, %v", len(result.Hits), err)
	}
}
