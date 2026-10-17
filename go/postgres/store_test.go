package postgres

import (
	"context"
	"math"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestTheEmbeddedSQLIsTheContractSQL(t *testing.T) {
	for _, name := range []string{DeclareBoundsSQL, SearchSQL, RecordInjectionSQL, WriteEmbeddingSQL, SearchVectorSQL,
		ListLessonsSQL} {
		embedded, err := ContractSQL(name)
		if err != nil {
			t.Fatal(err)
		}
		contract, err := os.ReadFile(filepath.Join(ct.Contract, "sql", name))
		if err != nil {
			t.Fatal(err)
		}
		if embedded != string(contract) {
			t.Fatalf("postgres/sql/%s differs from contract/sql/%s: copy the contract file", name, name)
		}
	}
}

func TestThePlaceholdersOfTheContractSQLAreItsParameters(t *testing.T) {
	declare, _ := ContractSQL(DeclareBoundsSQL)
	statement := ToDriverPlaceholders(declare)
	want := []string{"auth_tenant", "auth_application", "auth_project", "auth_team", "auth_agent", "auth_user"}
	if !reflect.DeepEqual(statement.Names, want) {
		t.Fatalf("declare-bounds.sql parameters: %v", statement.Names)
	}
	// The comments name every parameter too, and are kept as written.
	if !strings.Contains(statement.SQL, "--   @auth_tenant") || strings.Contains(statement.SQL, "set_config('agent_experience.auth_tenant', @") {
		t.Fatalf("the comments were rewritten, or the code was not:\n%s", statement.SQL)
	}

	search, _ := ContractSQL(SearchSQL)
	statement = ToDriverPlaceholders(search)
	want = []string{"tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id", "task_text",
		"statuses", "min_confidence", "limit", "exclude_model_authored", "with_grants", "minimum_matched_terms"}
	if !reflect.DeepEqual(statement.Names, want) {
		t.Fatalf("search-experience-text.sql parameters: %v", statement.Names)
	}
	if !strings.Contains(statement.SQL, "p_minimum_matched_terms => $13)") {
		t.Fatalf("the search was not numbered:\n%s", statement.SQL)
	}

	record, _ := ContractSQL(RecordInjectionSQL)
	statement = ToDriverPlaceholders(record)
	want = []string{"injected_at", "experience_ids", "tenant_id", "application_id", "project_id", "team_id",
		"agent_id", "user_id"}
	if !reflect.DeepEqual(statement.Names, want) {
		t.Fatalf("record-injection.sql parameters: %v", statement.Names)
	}
}

func TestTheLessonsStatementsAreTheContractsFourWithTheirParameters(t *testing.T) {
	statements, err := ContractStatements(ListLessonsSQL)
	if err != nil {
		t.Fatal(err)
	}
	base := []string{"tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id"}
	for name, want := range map[string][]string{
		"page":                                  append(append([]string(nil), base...), "limit"),
		"page-with-statuses":                    append(append([]string(nil), base...), "statuses", "limit"),
		"page-without-injections":               append(append([]string(nil), base...), "limit"),
		"page-without-injections-with-statuses": append(append([]string(nil), base...), "statuses", "limit"),
	} {
		if statements[name] == "" {
			t.Fatalf("no %q statement", name)
		}
		if got := ToDriverPlaceholders(statements[name]).Names; !reflect.DeepEqual(got, want) {
			t.Errorf("%s parameters: %v, want %v", name, got, want)
		}
	}
	if len(statements) != 4 {
		t.Fatalf("%d statements", len(statements))
	}
}

func TestPlaceholdersSkipLiteralsIdentifiersAndComments(t *testing.T) {
	sql := `SELECT @a, '@b', E'\'@c', "@d", $$@e$$, $tag$ @f $tag$, /* @g /* @h */ */ @a, @i -- @j` + "\n" + `, 50% @_k`
	statement := ToDriverPlaceholders(sql)
	if !reflect.DeepEqual(statement.Names, []string{"a", "i", "_k"}) {
		t.Fatalf("names: %v", statement.Names)
	}
	want := `SELECT $1, '@b', E'\'@c', "@d", $$@e$$, $tag$ @f $tag$, /* @g /* @h */ */ $1, $2 -- @j` + "\n" + `, 50% $3`
	if statement.SQL != want {
		t.Fatalf("got  %s\nwant %s", statement.SQL, want)
	}
	if _, err := statement.Args(map[string]any{"a": 1, "i": 2}); err == nil {
		t.Fatal("a missing parameter was not refused")
	}
}

func fixture(parts ...string) string {
	return filepath.Join(append([]string{ct.Fixtures}, parts...)...)
}

func TestADollarInsideAnIdentifierIsNotADollarQuote(t *testing.T) {
	statement := ToDriverPlaceholders("SELECT a$tag$, @x, b$$ FROM t$1 WHERE c = @y -- $tag$ @z")
	if !reflect.DeepEqual(statement.Names, []string{"x", "y"}) {
		t.Fatalf("names: %v", statement.Names)
	}
	if statement.SQL != "SELECT a$tag$, $1, b$$ FROM t$1 WHERE c = $2 -- $tag$ @z" {
		t.Fatal(statement.SQL)
	}
}

// unreachable fails a test that reaches the database: the store must refuse its arguments first.
type unreachable struct{ t *testing.T }

func (u unreachable) Begin(context.Context) (pgx.Tx, error) {
	u.t.Fatal("the store began a transaction before checking its arguments")
	return nil, nil
}

func TestInsertFinalizedRefusesWhatIsNotARecordAndItsOwnEvent(t *testing.T) {
	store, err := NewStore(unreachable{t})
	if err != nil {
		t.Fatal(err)
	}
	finalized, _, err := ct.RunFinalizeCase(ct.MustReadJSON(fixture("finalize", "verified-after-one-failed-attempt",
		"input.json")))
	if err != nil {
		t.Fatal(err)
	}
	ctx, bounds := context.Background(), ae.BoundsOf(finalized.Record.Scope)
	if store.InsertFinalized(ctx, bounds, nil, "p", nil) == nil {
		t.Error("nil was accepted")
	}
	for _, actor := range []string{"", " ", "\u0085"} {
		if store.InsertFinalized(ctx, bounds, &finalized, actor, nil) == nil {
			t.Errorf("the actor %q was accepted", actor)
		}
	}
	for name, change := range map[string]func(*ae.FinalizedExperience){
		"another record":   func(f *ae.FinalizedExperience) { f.Event.ExperienceID = ae.UUID{15: 1} },
		"another status":   func(f *ae.FinalizedExperience) { f.Event.CurrentStatus = ae.StatusQuarantined },
		"another revision": func(f *ae.FinalizedExperience) { f.Event.AppliedRevision = 2 },
	} {
		changed := finalized
		change(&changed)
		if store.InsertFinalized(ctx, bounds, &changed, "p", nil) == nil {
			t.Errorf("an event for %s was accepted", name)
		}
	}
	if _, err := NewStore(nil); err == nil {
		t.Error("a store with no connection source")
	}
}

func TestTheContractStatementsAreSplitAtTheirMarkers(t *testing.T) {
	writes, err := ContractStatements(WriteEmbeddingSQL)
	if err != nil {
		t.Fatal(err)
	}
	if len(writes) != 2 || !strings.HasPrefix(writes["write"], "INSERT INTO agent_experience.experience_embeddings") ||
		!strings.HasPrefix(writes["probe-revision"], "SELECT r.revision") || strings.Contains(writes["write"], "-- ") {
		t.Fatalf("write statements: %v", writes)
	}
	vectors, err := ContractStatements(SearchVectorSQL)
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"search", "search-excluding-model-authored", "compatibility",
		"compatibility-excluding-model-authored"} {
		if vectors[name] == "" {
			t.Fatalf("no %s statement", name)
		}
	}
	if !strings.Contains(vectors["search"], "{dim}") || strings.Contains(vectors["compatibility"], "{dim}") {
		t.Fatal("only the searches have {dim}")
	}
	names := ToDriverPlaceholders(strings.ReplaceAll(vectors["search"], "{dim}", "4")).Names
	want := []string{"query_vector", "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id",
		"statuses", "min_confidence", "model_id", "limit"}
	if !reflect.DeepEqual(names, want) {
		t.Fatalf("search parameters: %v", names)
	}
}

func TestAnInvalidWriteOrSearchIsRefusedBeforeAnyStatement(t *testing.T) {
	// No connection source can be used: a refused call never reaches it.
	store, err := NewStore(nil)
	if err == nil || store != nil {
		t.Fatal("a store needs a connection source")
	}
	store, err = NewStore(neverBegins{})
	if err != nil {
		t.Fatal(err)
	}
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	good := ae.EmbeddingWrite{ExperienceID: ae.NewRandomUUID(), Scope: scope, ModelID: "m", Dimension: 2,
		ContentHash: "h", SourceRevision: 1, Vector: []float32{1, 0}, Now: ae.Now()}
	bad := []ae.EmbeddingWrite{good, good, good, good, good}
	bad[0].ModelID = " "
	bad[1].Vector = []float32{1}
	bad[2].Vector = []float32{1, float32(math.NaN())}
	bad[3].ContentHash = ""
	bad[4].SourceRevision = -1
	for i, write := range bad {
		if _, err := store.WriteEmbedding(context.Background(), ae.BoundsOf(scope), write); err == nil {
			t.Fatalf("write %d was accepted", i)
		}
	}
	for i, query := range []ae.VectorQuery{{Scope: scope, ModelID: "", Vector: []float32{1}},
		{Scope: scope, ModelID: "m"}, {Scope: scope, ModelID: "m", Vector: []float32{float32(math.Inf(1))}},
		{Scope: scope, ModelID: "m", Vector: []float32{1}, Limit: -1}} {
		if _, err := store.SearchVectors(context.Background(), ae.BoundsOf(scope), query); err == nil {
			t.Fatalf("query %d was accepted", i)
		}
	}
}

// neverBegins fails the test's premise if a statement is ever run: it panics when asked for a transaction.
type neverBegins struct{}

func (neverBegins) Begin(context.Context) (pgx.Tx, error) {
	panic("a refused call began a transaction")
}
