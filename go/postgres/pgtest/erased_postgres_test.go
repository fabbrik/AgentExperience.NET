//go:build postgres

package pgtest_test

// Contract 1.5's erased fixtures: an erased record is never read. Search, Get and RecordInjection run through the
// store; erasure runs purge_experience_record as the schema's owner.

import (
	"context"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestErased(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases("erased") {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			seed := ct.MustReadJSON(fixture("erased", name, "seed.json")).([]any)
			steps := get(ct.MustReadJSON(fixture("erased", name, "steps.json")).(*ae.Object), "steps").([]any)
			expected := ct.MustReadJSON(fixture("erased", name, "expected.json")).(*ae.Object)
			outcomes := get(expected, "steps").([]any)
			if len(outcomes) != len(steps) {
				t.Fatalf("%d steps, %d expected outcomes", len(steps), len(outcomes))
			}

			database := createDatabase(t, admin, "erased_"+name)
			store := newStore(t, admin, database)
			owner := ownerConn(t, admin, database)
			seeded := map[string]any{}
			for _, stored := range seed {
				record, err := ae.FromStored(stored)
				if err != nil {
					t.Fatal(err)
				}
				if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
					t.Fatal(err)
				}
				seeded[record.ExperienceID.String()] = stored
			}

			before := clockOf(t, owner)
			for i, item := range steps {
				step := item.(*ae.Object)
				outcome := outcomes[i]
				if search, ok := step.Get("search"); ok {
					checkSearch(t, store, seeded, search.(*ae.Object), outcome.(*ae.Object))
					continue
				}
				if read, ok := step.Get("get"); ok {
					read := read.(*ae.Object)
					scope := ct.ScopeOf(get(read, "scope"))
					id, err := ae.ParseUUID(get(read, "experienceId").(string))
					if err != nil {
						t.Fatal(err)
					}
					record, err := store.Get(ctx, ae.BoundsOf(scope), scope, id)
					if err != nil {
						t.Fatal(err)
					}
					if found := get(outcome.(*ae.Object), "found").(bool); found != (record != nil) {
						t.Fatalf("step %d: found %v, want %v", i, record != nil, found)
					}
					if record != nil {
						if difference := ct.Difference(seeded[id.String()], ct.AsWritten(record.ToStored()), "$",
							ct.Options{IgnoreKind: true}); difference != "" {
							t.Fatalf("step %d read back: %s", i, difference)
						}
					}
					continue
				}
				if outcome != nil {
					t.Fatalf("step %d: expected %v for a step with no result", i, outcome)
				}
				if record, ok := step.Get("record"); ok {
					recordStep(t, store, record.(*ae.Object))
				} else {
					eraseStep(t, owner, get(step, "erase").(*ae.Object))
				}
			}
			after := clockOf(t, owner)
			checkInjectionRows(t, owner, get(expected, "experience_injections").([]any), before, after)
		})
	}
}
