//go:build postgres

package pgtest_test

// Contract 1.16's lifecycle-sql fixtures through the PostgreSQL store (sql/lifecycle.sql): each step's result, then
// the three tables read as the schema's owner, each column as text in UTC. And writers racing at one revision on a
// pool: exactly one commits.

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
)

var lifecycleTables = []struct {
	name, table string
	columns     []string
	order       string
}{
	{"experience_records", "agent_experience.experience_records", []string{"experience_id", "status",
		"reuse_confidence", "supporting_validations", "contradictions", "revision", "updated_at", "deleted_at"},
		"experience_id"},
	{"lifecycle_events", "agent_experience.lifecycle_events", []string{"event_id", "experience_id", "tenant_id",
		"application_id", "project_id", "team_id", "agent_id", "user_id", "prior_status", "current_status", "reason",
		"producer", "occurred_at", "recorded_at", "expected_revision", "applied_revision", "replacement_experience_id",
		"actor", "confidence_evidence_id", "confidence_kind", "confidence_source", "confidence_run_id",
		"confidence_verification_round_id", "confidence_reviewer_identity", "confidence_rule_version",
		"confidence_detail", "prior_reuse_confidence", "new_reuse_confidence", "prior_supporting_validations",
		"new_supporting_validations", "prior_contradictions", "new_contradictions", "confidence_assessment_id",
		"confidence_admission"}, "experience_id, applied_revision"},
	{"confidence_evidence", "agent_experience.confidence_evidence", []string{"evidence_id", "experience_id",
		"event_id", "kind", "source", "run_id", "verification_round_id", "reviewer_identity", "counted", "actor",
		"rule_version", "detail", "recorded_at", "applied_revision", "applied_status", "prior_reuse_confidence",
		"new_reuse_confidence", "prior_supporting_validations", "new_supporting_validations", "prior_contradictions",
		"new_contradictions", "assessment_id", "admission", "independence_key"}, "experience_id, evidence_id"},
}

// tableAsText reads a table as the schema's owner, every column as text in UTC.
func tableAsText(t *testing.T, owner *pgx.Conn, table string, columns []string, order string) []any {
	t.Helper()
	ctx := context.Background()
	if _, err := owner.Exec(ctx, "SET TIME ZONE 'UTC'"); err != nil {
		t.Fatal(err)
	}
	selected := make([]string, len(columns))
	for i, column := range columns {
		selected[i] = column + "::text"
	}
	rows, err := owner.Query(ctx, "SELECT "+strings.Join(selected, ", ")+" FROM "+table+" ORDER BY "+order)
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
			var value any
			if values[i] != nil {
				value = *values[i]
			}
			row.Set(column, value)
		}
		result = append(result, row)
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	return result
}

func lifecycleSQLStep(t *testing.T, store *postgres.Store, owner *pgx.Conn, step *ae.Object) any {
	t.Helper()
	ctx := context.Background()
	get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
	for _, m := range step.Members() {
		o, _ := m.Value.(*ae.Object)
		switch m.Key {
		case "clock":
			continue
		case "commit":
			result, err := store.CommitLifecycleEvent(ctx, ct.AuthorizationOf(get(o, "authorization")),
				ct.ScopeOf(get(o, "scope")), ct.LifecycleEventOf(get(o, "event")))
			if err != nil {
				t.Fatalf("commit: %v", err)
			}
			return ct.StoreCommitResultJSON(result)
		case "history":
			query := ct.HistoryQueryOf(get(o, "query"))
			result, err := store.GetHistory(ctx, ct.AuthorizationOf(get(o, "authorization")), query)
			if err != nil {
				t.Fatalf("history: %v", err)
			}
			return ct.HistoryResultJSON(query.Scope, result)
		case "checkSupersession":
			result, err := store.CheckSupersession(ctx, ct.AuthorizationOf(get(o, "authorization")),
				ct.ScopeOf(get(o, "scope")), ae.MustParseUUID(get(o, "experienceId").(string)),
				ae.MustParseUUID(get(o, "replacementExperienceId").(string)))
			if err != nil {
				t.Fatalf("checkSupersession: %v", err)
			}
			return ct.SupersessionResultJSON(result)
		case "read":
			result, err := store.ReadRecord(ctx, ct.AuthorizationOf(get(o, "authorization")), ct.ScopeOf(get(o, "scope")),
				ae.MustParseUUID(get(o, "experienceId").(string)))
			if err != nil {
				t.Fatalf("read: %v", err)
			}
			return ct.RecordReadJSON(result)
		case "erase":
			eraseStep(t, owner, o)
			return nil
		}
		t.Fatalf("unknown step %s", m.Key)
	}
	t.Fatal("a step with no operation")
	return nil
}

func TestLifecycleSQL(t *testing.T) {
	admin := requireServer(t)
	names := ct.Cases("lifecycle-sql")
	if len(names) == 0 {
		t.Fatal("no lifecycle-sql cases")
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			seed := ct.MustReadJSON(fixture("lifecycle-sql", name, "seed.json")).(*ae.Object)
			steps := get(ct.MustReadJSON(fixture("lifecycle-sql", name, "steps.json")).(*ae.Object), "steps").([]any)
			expected := ct.MustReadJSON(fixture("lifecycle-sql", name, "expected.json")).(*ae.Object)
			results := get(expected, "results").([]any)
			if len(results) != len(steps) {
				t.Fatalf("%d steps, %d results", len(steps), len(results))
			}

			database := createDatabase(t, admin, "lifecycle_"+name)
			store := newStore(t, admin, database)
			owner := ownerConn(t, admin, database)
			for _, stored := range get(seed, "records").([]any) {
				record, err := ae.FromStored(stored)
				if err != nil {
					t.Fatal(err)
				}
				if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
					t.Fatal(err)
				}
			}
			for i, item := range steps {
				step := item.(*ae.Object)
				if clock, ok := step.Get("clock"); ok && clock != nil {
					at := ae.MustParseInstant(clock.(string))
					store.SetClock(func() ae.Instant { return at })
				}
				var actual any
				if result := lifecycleSQLStep(t, store, owner, step); result != nil {
					actual = ct.AsWritten(result)
				}
				if difference := ct.Difference(results[i], actual, fmt.Sprintf("results[%d]", i),
					ct.LifecycleOptions); difference != "" {
					t.Fatalf("%s", difference)
				}
			}
			for _, table := range lifecycleTables {
				actual := tableAsText(t, owner, table.table, table.columns, table.order)
				if difference := ct.Difference(get(expected, table.name), ct.AsWritten(actual), table.name,
					ct.Options{}); difference != "" {
					t.Fatalf("%s", difference)
				}
			}
		})
	}
}

// Writers deciding against one revision race on a pool: exactly one commits, every other is StaleRevision with the
// revision it produced, and the log holds the winner's event alone.
func TestLifecycleWritersRacingAtOneRevisionOnAPoolLetExactlyOneWin(t *testing.T) {
	const writers = 8
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_race")
	store := newStore(t, admin, database)
	seed := ct.MustReadJSON(fixture("lifecycle-sql", "compare-and-set", "seed.json")).(*ae.Object)
	records, _ := seed.Get("records")
	record, err := ae.FromStored(records.([]any)[0])
	if err != nil {
		t.Fatal(err)
	}
	if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
		t.Fatal(err)
	}
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	if err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "racer", Bounds: ae.BoundsOf(record.Scope)}
	start := make(chan struct{})
	results := make([]ae.CommitLifecycleTransitionResult, writers)
	failures := make([]error, writers)
	var wg sync.WaitGroup
	for i := range writers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			prior := record.Status
			results[i], failures[i] = service.Commit(ctx, authorization, ae.CommitLifecycleTransitionRequest{
				EventID:          ae.UUID{0: 0x7a, 6: 0x40, 8: 0x80, 15: byte(i)},
				ExperienceID:     record.ExperienceID,
				Scope:            record.Scope,
				PriorStatus:      &prior,
				CurrentStatus:    ae.StatusRevoked,
				Reason:           "withdrawn",
				Producer:         "race",
				OccurredAt:       record.CreatedAt,
				ExpectedRevision: record.Revision,
			})
		}()
	}
	close(start)
	wg.Wait()
	committed := 0
	for i := range writers {
		if failures[i] != nil {
			t.Fatal(failures[i])
		}
		switch results[i].Outcome {
		case ae.TransitionCommitted:
			committed++
		case ae.TransitionStaleRevision:
			if results[i].Revision != record.Revision+1 {
				t.Errorf("stale with %d", results[i].Revision)
			}
		default:
			t.Errorf("writer %d: %s", i, results[i].Outcome)
		}
	}
	if committed != 1 {
		t.Fatalf("%d writers committed", committed)
	}
	page, err := store.GetHistory(ctx, authorization, ae.HistoryQuery{Scope: record.Scope,
		ExperienceID: record.ExperienceID, Limit: 100})
	if err != nil || page.Outcome != ae.StoreFound || len(page.Events) != 1 || page.Revision != record.Revision+1 {
		t.Fatalf("%+v %v", page, err)
	}
}

// The cross-language race's Go writer (internal/lifecyclerace), run as the .NET suite runs it: two rounds committed
// against one revision (the second is StaleRevision), then the history, on stdout as JSON and nothing else.
func TestTheLifecycleRaceWriterSpeaksItsProtocol(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_writer")
	store := newStore(t, admin, database)
	seed := ct.MustReadJSON(fixture("lifecycle-sql", "compare-and-set", "seed.json")).(*ae.Object)
	records, _ := seed.Get("records")
	record, err := ae.FromStored(records.([]any)[0])
	if err != nil {
		t.Fatal(err)
	}
	if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
		t.Fatal(err)
	}
	authorization := ct.AuthorizationJSON(ae.AuthorizationContext{PrincipalID: "go-writer", Bounds: ae.BoundsOf(record.Scope)})
	scope := ae.NewObject(ae.Member{Key: "tenantId", Value: record.Scope.TenantID},
		ae.Member{Key: "applicationId", Value: record.Scope.ApplicationID},
		ae.Member{Key: "projectId", Value: record.Scope.ProjectID}, ae.Member{Key: "teamId", Value: optionalJSON(record.Scope.TeamID)},
		ae.Member{Key: "agentId", Value: optionalJSON(record.Scope.AgentID)}, ae.Member{Key: "userId", Value: optionalJSON(record.Scope.UserID)})
	request := func(event string) *ae.Object {
		return ae.NewObject(ae.Member{Key: "eventId", Value: event},
			ae.Member{Key: "experienceId", Value: record.ExperienceID.String()}, ae.Member{Key: "scope", Value: scope},
			ae.Member{Key: "priorStatus", Value: string(record.Status)}, ae.Member{Key: "currentStatus", Value: "Revoked"},
			ae.Member{Key: "reason", Value: "withdrawn"}, ae.Member{Key: "producer", Value: "go-writer"},
			ae.Member{Key: "occurredAt", Value: "2026-03-02T09:00:00+00:00"},
			ae.Member{Key: "expectedRevision", Value: json.Number(strconv.FormatInt(record.Revision, 10))},
			ae.Member{Key: "replacementExperienceId", Value: nil})
	}
	round := func(event string) *ae.Object {
		return ae.NewObject(ae.Member{Key: "lockKey", Value: json.Number("4242")},
			ae.Member{Key: "authorization", Value: authorization}, ae.Member{Key: "request", Value: request(event)})
	}
	dsn := admin.Application(database)
	runWriter := func(input *ae.Object) *ae.Object {
		t.Helper()
		data, err := ae.MarshalJSONValue(input)
		if err != nil {
			t.Fatal(err)
		}
		path := filepath.Join(t.TempDir(), "input.json")
		if err := os.WriteFile(path, data, 0o600); err != nil {
			t.Fatal(err)
		}
		command := exec.Command("go", "run", "./internal/lifecyclerace", path)
		command.Dir = ".."
		var stderr strings.Builder
		command.Stderr = &stderr
		output, err := command.Output()
		if err != nil {
			t.Fatalf("%v: %s", err, stderr.String())
		}
		value, err := ae.DecodeJSON(output)
		if err != nil {
			t.Fatalf("stdout is not one JSON value: %v (%q)", err, output)
		}
		return value.(*ae.Object)
	}
	committed := runWriter(ae.NewObject(ae.Member{Key: "dsn", Value: dsn}, ae.Member{Key: "mode", Value: "commit"},
		ae.Member{Key: "rounds", Value: []any{round("00001162-0000-4000-8000-00000000fe01"),
			round("00001162-0000-4000-8000-00000000fe02")}}))
	expected := ct.MustReadJSON(writeJSON(t, `{"results": [
		{"outcome": "Committed", "revision": 2, "currentStatus": null, "reason": null, "errors": []},
		{"outcome": "StaleRevision", "revision": 2, "currentStatus": null, "reason": null, "errors": []}]}`))
	if difference := ct.Difference(expected, committed, "$", ct.Options{}); difference != "" {
		t.Fatal(difference)
	}
	query := ae.NewObject(ae.Member{Key: "scope", Value: scope},
		ae.Member{Key: "experienceId", Value: record.ExperienceID.String()},
		ae.Member{Key: "limit", Value: json.Number("100")}, ae.Member{Key: "startAfterRevision", Value: nil})
	history := ae.NewObject(ae.Member{Key: "authorization", Value: authorization}, ae.Member{Key: "query", Value: query})
	histories := runWriter(ae.NewObject(ae.Member{Key: "dsn", Value: dsn}, ae.Member{Key: "mode", Value: "history"},
		ae.Member{Key: "histories", Value: []any{history}}))
	list, _ := histories.Get("histories")
	page := list.([]any)[0].(*ae.Object)
	events, _ := page.Get("events")
	outcome, _ := page.Get("outcome")
	if outcome != "Found" || len(events.([]any)) != 1 {
		t.Fatalf("%v", page)
	}
	actor, _ := events.([]any)[0].(*ae.Object).Get("actor")
	if actor != "go-writer" {
		t.Fatalf("actor %v", actor)
	}
}

func optionalJSON(value *string) any {
	if value == nil {
		return nil
	}
	return *value
}

func writeJSON(t *testing.T, text string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "expected.json")
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}
