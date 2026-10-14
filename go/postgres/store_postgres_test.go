//go:build postgres

package postgres

// The PostgreSQL leg of the conformance runner: finalize fixtures written through this store and read back row for
// row, and every search fixture run through the store's text search, then ranked.
//
// The schema is never reimplemented here. Each test database is migrated by running the .NET migrator's own scripts
// (src/AgentExperience.Storage.Postgres/Migrations/*.sql) in name order, one transaction per script, as the owner
// role, which is what ExperienceSchemaMigrator.MigrateAsync does. The application role's privileges are the
// contract's sql/application-role-privileges.sql, both modes (row-level security on, what a contract 1.x port runs
// against).
//
// The server comes from PGHOST (with PGPORT, PGUSER, PGPASSWORD: a superuser connection) when set, else from a
// Testcontainers postgres container (AGENTEXPERIENCE_POSTGRES_MAJOR, default 16). With neither, the tests skip, unless
// AGENTEXPERIENCE_REQUIRE_POSTGRES=1, when they fail. testcontainers-go applies TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX
// to every image it starts, the Reaper's (Ryuk) included, and recognises the Reaper by its name's suffix, so a
// prefix (CI's Docker Hub mirror) needs no workaround here, unlike testcontainers-python (python/tests/_postgres.py).

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/testcontainers/testcontainers-go"
	tcpostgres "github.com/testcontainers/testcontainers-go/modules/postgres"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

const (
	ownerRole       = "contract_owner"
	applicationRole = "contract_app"
	rolePassword    = "contract-role-password"
	scoreTolerance  = 1e-9
	actor           = "contract-principal"
)

type server struct {
	host           string
	port           int
	user, password string
}

func (s server) connString(database, user, password string) string {
	u := url.URL{
		Scheme:   "postgres",
		User:     url.UserPassword(user, password),
		Host:     net.JoinHostPort(s.host, strconv.Itoa(s.port)),
		Path:     "/" + database,
		RawQuery: "sslmode=disable",
	}
	return u.String()
}

func (s server) admin(database string) string { return s.connString(database, s.user, s.password) }

var (
	pgServer      *server
	pgUnavailable string
)

func TestMain(m *testing.M) {
	code := func() int {
		ctx := context.Background()
		admin, stop, err := startServer(ctx)
		if err != nil {
			if os.Getenv("AGENTEXPERIENCE_REQUIRE_POSTGRES") == "1" {
				// A CI leg that exists to run these tests must fail rather than skip them.
				fmt.Fprintf(os.Stderr, "PostgreSQL is required but not available: %v\n", err)
				return 1
			}
			pgUnavailable = err.Error()
			return m.Run()
		}
		defer stop()
		if err := checkMajor(ctx, admin); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return 1
		}
		if err := prepareRoles(ctx, admin); err != nil {
			fmt.Fprintf(os.Stderr, "preparing the roles: %v\n", err)
			return 1
		}
		pgServer = &admin
		return m.Run()
	}()
	os.Exit(code)
}

func startServer(ctx context.Context) (server, func(), error) {
	if host := os.Getenv("PGHOST"); host != "" {
		port := 5432
		if text := os.Getenv("PGPORT"); text != "" {
			var err error
			if port, err = strconv.Atoi(text); err != nil {
				return server{}, nil, fmt.Errorf("PGPORT: %w", err)
			}
		}
		user := os.Getenv("PGUSER")
		if user == "" {
			user = "postgres"
		}
		return server{host, port, user, os.Getenv("PGPASSWORD")}, func() {}, nil
	}
	major := os.Getenv("AGENTEXPERIENCE_POSTGRES_MAJOR")
	if major == "" {
		major = "16"
	}
	container, err := runContainer(ctx, "postgres:"+major)
	if err != nil {
		return server{}, nil, fmt.Errorf("no PGHOST, and no Docker for a postgres:%s container: %w", major, err)
	}
	stop := func() { _ = testcontainers.TerminateContainer(container) }
	host, err := container.Host(ctx)
	if err != nil {
		stop()
		return server{}, nil, err
	}
	port, err := container.MappedPort(ctx, "5432/tcp")
	if err != nil {
		stop()
		return server{}, nil, err
	}
	return server{host, port.Int(), "postgres", "postgres"}, stop, nil
}

func runContainer(ctx context.Context, image string) (container *tcpostgres.PostgresContainer, err error) {
	defer func() {
		// testcontainers-go panics, rather than failing, on some hosts without Docker.
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("%v", recovered)
		}
	}()
	return tcpostgres.Run(ctx, image,
		tcpostgres.WithDatabase("postgres"),
		tcpostgres.WithUsername("postgres"),
		tcpostgres.WithPassword("postgres"),
		tcpostgres.BasicWaitStrategies())
}

func requireServer(t *testing.T) server {
	t.Helper()
	if pgServer == nil {
		t.Skipf("PostgreSQL is not available: %s", pgUnavailable)
	}
	return *pgServer
}

func connect(ctx context.Context, connString string) (*pgx.Conn, error) {
	return pgx.Connect(ctx, connString)
}

// checkMajor makes sure a leg does not quietly run another major than the one it names.
func checkMajor(ctx context.Context, admin server) error {
	requested := os.Getenv("AGENTEXPERIENCE_POSTGRES_MAJOR")
	if requested == "" {
		return nil
	}
	conn, err := connect(ctx, admin.admin("postgres"))
	if err != nil {
		return err
	}
	defer conn.Close(ctx)
	var version string
	if err := conn.QueryRow(ctx, "SHOW server_version_num").Scan(&version); err != nil {
		return err
	}
	number, err := strconv.Atoi(version)
	if err != nil {
		return fmt.Errorf("the server reports the version %q: %w", version, err)
	}
	if strconv.Itoa(number/10000) != requested {
		return fmt.Errorf("the server is PostgreSQL %d, not the requested %s", number/10000, requested)
	}
	return nil
}

// prepareRoles makes the owner role that runs the migrations and the application role the stores use, as the .NET
// tests make them.
func prepareRoles(ctx context.Context, admin server) error {
	conn, err := connect(ctx, admin.admin("postgres"))
	if err != nil {
		return err
	}
	defer conn.Close(ctx)
	for _, role := range []string{ownerRole, applicationRole} {
		var exists bool
		if err := conn.QueryRow(ctx, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)", role).Scan(&exists); err != nil {
			return err
		}
		if !exists {
			if _, err := conn.Exec(ctx, "CREATE ROLE "+role+" LOGIN PASSWORD '"+rolePassword+"'"); err != nil {
				return err
			}
		}
	}
	// What a non-superuser owner needs to run the migrations (the erasure scripts set these parameters).
	_, err = conn.Exec(ctx, "GRANT SET ON PARAMETER agent_experience.purge_authorized, "+
		"agent_experience.access_purge_authorized TO "+ownerRole)
	return err
}

var roleName = regexp.MustCompile(`^[a-z_][a-z0-9_]*$`)

// applicationRolePrivileges are the statements of sql/application-role-privileges.sql for role (a plain
// identifier): those under "-- mode: always", then those under "-- mode: row-level-security".
func applicationRolePrivileges(t *testing.T, role string) []string {
	t.Helper()
	if !roleName.MatchString(role) {
		t.Fatalf("%q is not a plain identifier", role)
	}
	data, err := os.ReadFile(filepath.Join(ct.Contract, "sql", "application-role-privileges.sql"))
	if err != nil {
		t.Fatal(err)
	}
	modes := map[string][]string{"always": nil, "row-level-security": nil}
	current := ""
	modeMarker := regexp.MustCompile(`(?i)^--\s*mode\b`)
	for _, line := range strings.Split(string(data), "\n") {
		switch {
		case modeMarker.MatchString(line):
			mode, ok := strings.CutPrefix(line, "-- mode: ")
			if _, known := modes[mode]; !ok || !known {
				t.Fatalf("unknown mode marker %q: the markers are '-- mode: always' and '-- mode: row-level-security'", line)
			}
			current = mode
		case line != "" && !strings.HasPrefix(line, "--"):
			if current == "" {
				t.Fatalf("a statement before any mode marker: %q", line)
			}
			if !strings.HasSuffix(line, ";") || strings.Index(line, ";") != len(line)-1 {
				t.Fatalf("not one statement on one line ending in ';': %q", line)
			}
			modes[current] = append(modes[current],
				strings.ReplaceAll(strings.TrimSuffix(line, ";"), `:"application_role"`, role))
		}
	}
	return append(modes["always"], modes["row-level-security"]...)
}

// databaseName is a unique, valid database name for a label: a readable prefix and a hash of the whole label.
func databaseName(label string) string {
	digest := sha256.Sum256([]byte(label))
	prefix := regexp.MustCompile(`[^a-z0-9_]`).ReplaceAllString(strings.ToLower(label), "_")
	if len(prefix) > 40 {
		prefix = prefix[:40]
	}
	return "go_" + prefix + "_" + hex.EncodeToString(digest[:])[:12]
}

// createDatabase is a fresh database, migrated by the .NET scripts and with the application role's privileges.
func createDatabase(t *testing.T, admin server, label string) string {
	t.Helper()
	ctx := context.Background()
	name := databaseName(label)
	conn, err := connect(ctx, admin.admin("postgres"))
	if err != nil {
		t.Fatal(err)
	}
	identifier := pgx.Identifier{name}.Sanitize()
	for _, statement := range []string{
		"DROP DATABASE IF EXISTS " + identifier + " WITH (FORCE)",
		"CREATE DATABASE " + identifier + " OWNER " + ownerRole,
	} {
		if _, err := conn.Exec(ctx, statement); err != nil {
			conn.Close(ctx)
			t.Fatal(err)
		}
	}
	conn.Close(ctx)
	t.Cleanup(func() {
		// Every database a test made is dropped when it ends (after the stores that use it close).
		conn, err := connect(context.Background(), admin.admin("postgres"))
		if err != nil {
			t.Errorf("dropping %s: %v", name, err)
			return
		}
		defer conn.Close(context.Background())
		if _, err := conn.Exec(context.Background(), "DROP DATABASE IF EXISTS "+identifier+" WITH (FORCE)"); err != nil {
			t.Errorf("dropping %s: %v", name, err)
		}
	})

	scripts, err := filepath.Glob(filepath.Join(ct.Migrations, "*.sql"))
	if err != nil || len(scripts) == 0 {
		t.Fatalf("no migration scripts under %s", ct.Migrations)
	}
	sort.Strings(scripts)
	owner, err := connect(ctx, admin.connString(name, ownerRole, rolePassword))
	if err != nil {
		t.Fatal(err)
	}
	defer owner.Close(ctx)
	for _, script := range scripts {
		text, err := os.ReadFile(script)
		if err != nil {
			t.Fatal(err)
		}
		err = pgx.BeginFunc(ctx, owner, func(tx pgx.Tx) error {
			return tx.Conn().PgConn().Exec(ctx, string(text)).Close()
		})
		if err != nil {
			t.Fatalf("%s: %v", filepath.Base(script), err)
		}
	}
	err = pgx.BeginFunc(ctx, owner, func(tx pgx.Tx) error {
		for _, statement := range applicationRolePrivileges(t, applicationRole) {
			if _, err := tx.Exec(ctx, statement); err != nil {
				return fmt.Errorf("%s: %w", statement, err)
			}
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	return name
}

// newStore is a store on a pool the test owns, as a host would give one; the pool closes when the test ends.
func newStore(t *testing.T, admin server, database string) *Store {
	t.Helper()
	pool, err := pgxpool.New(context.Background(), admin.connString(database, applicationRole, rolePassword))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	store, err := NewStore(pool)
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func finalizeCase(t *testing.T, name string) ae.FinalizedExperience {
	t.Helper()
	finalized, _, err := ct.RunFinalizeCase(ct.MustReadJSON(fixture("finalize", name, "input.json")))
	if err != nil {
		t.Fatal(err)
	}
	return finalized
}

// events are a record's lifecycle events, read as the application role under the embedded declare-bounds.sql.
func events(t *testing.T, store *Store, bounds ae.AuthorizationBounds, id ae.UUID) []*ae.Object {
	t.Helper()
	var result []*ae.Object
	err := store.transaction(context.Background(), bounds, func(tx pgx.Tx) error {
		rows, err := tx.Query(context.Background(), "SELECT event_id, experience_id, prior_status, current_status, "+
			"reason, producer, occurred_at, expected_revision, applied_revision, actor "+
			"FROM agent_experience.lifecycle_events WHERE experience_id = $1", [16]byte(id))
		if err != nil {
			return err
		}
		defer rows.Close()
		for rows.Next() {
			var eventID, experienceID [16]byte
			var prior *string
			var current, reason, producer, rowActor string
			var occurred time.Time
			var expected, applied int64
			if err := rows.Scan(&eventID, &experienceID, &prior, &current, &reason, &producer, &occurred, &expected,
				&applied, &rowActor); err != nil {
				return err
			}
			occurredAt, err := ae.InstantFromTime(occurred)
			if err != nil {
				return err
			}
			var priorValue any
			if prior != nil {
				priorValue = *prior
			}
			result = append(result, ae.NewObject(
				ae.Member{Key: "event_id", Value: ae.UUID(eventID).String()},
				ae.Member{Key: "experience_id", Value: ae.UUID(experienceID).String()},
				ae.Member{Key: "prior_status", Value: priorValue},
				ae.Member{Key: "current_status", Value: current},
				ae.Member{Key: "reason", Value: reason},
				ae.Member{Key: "producer", Value: producer},
				ae.Member{Key: "occurred_at", Value: occurredAt.String()},
				ae.Member{Key: "expected_revision", Value: json.Number(strconv.FormatInt(expected, 10))},
				ae.Member{Key: "applied_revision", Value: json.Number(strconv.FormatInt(applied, 10))},
				ae.Member{Key: "actor", Value: rowActor},
			))
		}
		return rows.Err()
	})
	if err != nil {
		t.Fatal(err)
	}
	return result
}

func TestAFinalizeFixtureWritesAndReadsBackRowForRow(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases("finalize") {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			store := newStore(t, admin, createDatabase(t, admin, "finalize_"+name))
			finalized := finalizeCase(t, name)
			record := finalized.Record
			bounds := ae.BoundsOf(record.Scope)
			if err := store.InsertFinalized(ctx, bounds, &finalized, actor, nil); err != nil {
				t.Fatal(err)
			}

			read, err := store.Get(ctx, bounds, record.Scope, record.ExperienceID)
			if err != nil || read == nil {
				t.Fatalf("the record was not read back: %v", err)
			}
			// jsonb keeps a number's value, not its text (1E2 comes back as 100), so numbers compare by value.
			expected := ct.MustReadJSON(fixture("finalize", name, "expected-record.json"))
			if difference := ct.Difference(expected, ct.AsWritten(read.ToStored()), "$",
				ct.Options{IgnoreKind: true}); difference != "" {
				t.Fatal(difference)
			}

			list := events(t, store, bounds, record.ExperienceID)
			if len(list) != 1 {
				t.Fatalf("the record has %d lifecycle events", len(list))
			}
			event := list[0]
			if value, _ := event.Get("actor"); value != actor {
				t.Fatalf("the event's actor is %v", value)
			}
			actual := ae.NewObject()
			for _, m := range event.Members() {
				if m.Key != "actor" {
					actual.Set(m.Key, m.Value)
				}
			}
			if difference := ct.Difference(ct.MustReadJSON(fixture("finalize", name, "expected-event.json")), actual,
				"$", ct.Options{}); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

func searchParameters(t *testing.T, query *ae.Object) ae.SearchParameters {
	t.Helper()
	get := func(key string) any { value, _ := query.Get(key); return value }
	integer := func(key string) int {
		n, err := strconv.Atoi(string(get(key).(json.Number)))
		if err != nil {
			t.Fatal(err)
		}
		return n
	}
	minConfidence, err := strconv.ParseFloat(string(get("minConfidence").(json.Number)), 64)
	if err != nil {
		t.Fatal(err)
	}
	var statuses []ae.ExperienceStatus
	for _, status := range get("statuses").([]any) {
		statuses = append(statuses, ae.ExperienceStatus(status.(string)))
	}
	return ae.SearchParameters{
		Scope:                ct.ScopeOf(get("scope")),
		TaskText:             get("taskText").(string),
		Statuses:             statuses,
		MinConfidence:        &minConfidence,
		Limit:                integer("limit"),
		ExcludeModelAuthored: get("excludeModelAuthored").(bool),
		WithGrants:           get("withGrants").(bool),
		MinimumMatchedTerms:  integer("minimumMatchedTerms"),
	}
}

func double(t *testing.T, value float64) json.Number {
	t.Helper()
	data, err := ae.MarshalJSONValue(value)
	if err != nil {
		t.Fatal(err)
	}
	return json.Number(data)
}

func TestSearch(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases("search") {
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			seed := ct.MustReadJSON(fixture("search", name, "seed.json")).([]any)
			query := ct.MustReadJSON(fixture("search", name, "query.json")).(*ae.Object)
			expected := ct.MustReadJSON(fixture("search", name, "expected.json")).(*ae.Object)
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }

			store := newStore(t, admin, createDatabase(t, admin, "search_"+name))
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

			parameters := searchParameters(t, query)
			rows, err := store.Search(ctx, ae.BoundsOf(parameters.Scope), parameters)
			if err != nil {
				t.Fatal(err)
			}
			actualRows := []any{}
			for _, row := range rows {
				actualRows = append(actualRows, ae.NewObject(
					ae.Member{Key: "experienceId", Value: row.Record.ExperienceID.String()},
					ae.Member{Key: "relevance", Value: double(t, row.Relevance)},
				))
			}
			expectedRows := get(expected, "rows").([]any)
			if difference := ct.Difference(expectedRows, actualRows, "$.rows",
				ct.Options{Tolerance: scoreTolerance}); difference != "" {
				t.Fatal(difference)
			}
			for i, row := range rows {
				// The relevance is the widened float4, exactly.
				want, _ := strconv.ParseFloat(string(get(expectedRows[i].(*ae.Object), "relevance").(json.Number)), 64)
				if row.Relevance != want {
					t.Fatalf("row %d: relevance %v, want exactly %v", i, row.Relevance, want)
				}
				original := seeded[row.Record.ExperienceID.String()]
				if difference := ct.Difference(original, ct.AsWritten(row.Record.ToStored()), "$",
					ct.Options{IgnoreKind: true}); difference != "" {
					t.Fatalf("row %d read back: %s", i, difference)
				}
			}

			preferred := map[string]string{}
			for _, m := range get(query, "preferredEnvironment").(*ae.Object).Members() {
				preferred[m.Key] = m.Value.(string)
			}
			result, err := ae.Rank(rows, parameters.Scope, ae.MustParseInstant(get(query, "now").(string)), preferred,
				parameters.ExcludeModelAuthored)
			if err != nil {
				t.Fatal(err)
			}
			if result.Truncated != get(expected, "truncated").(bool) {
				t.Fatalf("truncated: %v", result.Truncated)
			}
			actualRanked := []any{}
			for _, entry := range result.Ranked {
				components := []any{}
				for _, component := range entry.Components {
					components = append(components, ae.NewObject(
						ae.Member{Key: "kind", Value: string(component.Kind)},
						ae.Member{Key: "value", Value: double(t, component.Value)},
						ae.Member{Key: "weight", Value: double(t, component.Weight)},
					))
				}
				actualRanked = append(actualRanked, ae.NewObject(
					ae.Member{Key: "experienceId", Value: entry.Record.ExperienceID.String()},
					ae.Member{Key: "score", Value: double(t, entry.Score)},
					ae.Member{Key: "components", Value: components},
				))
			}
			if difference := ct.Difference(get(expected, "ranked"), actualRanked, "$.ranked",
				ct.Options{Tolerance: scoreTolerance}); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

func TestARecordReadThroughAnotherScopeIsNotFound(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "scope"))
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	record := finalized.Record
	if err := store.InsertFinalized(ctx, ae.BoundsOf(record.Scope), &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}
	other := ae.AuthorizationBounds{TenantID: record.Scope.TenantID, ApplicationID: &record.Scope.ApplicationID,
		ProjectID: &record.Scope.ProjectID, AgentID: ae.Ptr("x")}
	if read, err := store.Get(ctx, other, record.Scope, record.ExperienceID); err != nil || read != nil {
		t.Fatalf("read through other bounds: %v, %v", read, err)
	}
	missing := ae.MustParseUUID("0000005f-0000-4000-8000-0000000000ff")
	if read, err := store.Get(ctx, ae.BoundsOf(record.Scope), record.Scope, missing); err != nil || read != nil {
		t.Fatalf("a missing record: %v, %v", read, err)
	}
}

func TestRowLevelSecurityRefusesAWriteOutsideTheBounds(t *testing.T) {
	admin := requireServer(t)
	store := newStore(t, admin, createDatabase(t, admin, "bounds"))
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	err := store.InsertFinalized(context.Background(), ae.AuthorizationBounds{TenantID: "another-tenant"}, &finalized,
		actor, nil)
	var pgErr *pgconn.PgError
	if !errors.As(err, &pgErr) || pgErr.Code != "42501" {
		t.Fatalf("expected insufficient_privilege (42501), got %v", err)
	}
}

func TestASealedRowIsSkippedByGetAndSearch(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "sealed")
	store := newStore(t, admin, database)
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	record := finalized.Record
	bounds := ae.BoundsOf(record.Scope)
	if err := store.InsertFinalized(ctx, bounds, &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}

	// A sealed row as the .NET sealing writes one (payload_version 2, opaque payload), inserted as the owner.
	sealedID := ae.MustParseUUID("0000005f-0000-4000-8000-000000000001")
	owner, err := connect(ctx, admin.connString(database, ownerRole, rolePassword))
	if err != nil {
		t.Fatal(err)
	}
	scope := record.Scope
	_, err = owner.Exec(ctx, "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, "+
		"tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, "+
		"supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, "+
		"search_vector_sealed) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, '(sealed)', 'Validated', 0.9, 1, 0, 1, now(), "+
		"now(), 2, $9::jsonb, to_tsvector('english', 'refund stuck pending ticket lock'))",
		[16]byte(sealedID), [16]byte(ae.MustParseUUID("0000005f-0000-4000-8000-000000000002")), scope.TenantID,
		scope.ApplicationID, scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID,
		`{"sealed": "aexp-sealed:v1:AAAA"}`)
	owner.Close(ctx)
	if err != nil {
		t.Fatal(err)
	}

	if read, err := store.Get(ctx, bounds, scope, sealedID); !errors.Is(err, ErrSealed) || read != nil {
		t.Fatalf("the sealed row: %v, %v", read, err)
	}
	rows, err := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "refund stuck in pending, ticket lock"))
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || rows[0].Record.ExperienceID != record.ExperienceID {
		t.Fatalf("the search returned %d rows", len(rows))
	}
}

func TestGetReadsOnlyTheExactScope(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "exact_scope")
	store := newStore(t, admin, database)
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	record := finalized.Record
	if err := store.InsertFinalized(ctx, ae.BoundsOf(record.Scope), &finalized, actor, nil); err != nil {
		t.Fatal(err)
	}
	// Bounds that admit the whole tenant: the row is visible to them, and still only the exact scope reads it.
	tenant := ae.AuthorizationBounds{TenantID: record.Scope.TenantID}
	if read, err := store.Get(ctx, tenant, record.Scope, record.ExperienceID); err != nil || read == nil {
		t.Fatalf("the exact scope: %v, %v", read, err)
	}
	for name, change := range map[string]func(*ae.Scope){
		"another agent":   func(s *ae.Scope) { s.AgentID = ae.Ptr("other-agent") },
		"no agent":        func(s *ae.Scope) { s.AgentID = nil },
		"a team":          func(s *ae.Scope) { s.TeamID = ae.Ptr("team") },
		"an empty user":   func(s *ae.Scope) { s.UserID = ae.Ptr("") },
		"another project": func(s *ae.Scope) { s.ProjectID = "other-project" },
	} {
		scope := record.Scope
		change(&scope)
		if read, err := store.Get(ctx, tenant, scope, record.ExperienceID); err != nil || read != nil {
			t.Errorf("%s read the record: %v, %v", name, read, err)
		}
	}

	// A tombstone (what the .NET purge leaves: deleted_at set, an empty payload) is not a record.
	owner, err := connect(ctx, admin.connString(database, ownerRole, rolePassword))
	if err != nil {
		t.Fatal(err)
	}
	defer owner.Close(ctx)
	tombstone := ae.MustParseUUID("0000005f-0000-4000-8000-0000000000aa")
	scope := record.Scope
	_, err = owner.Exec(ctx, "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, "+
		"tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, "+
		"supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, deleted_at) "+
		"VALUES ($1, $2, $3, $4, $5, $6, $7, $8, '(deleted)', 'Deleted', 0, 0, 0, 2, now(), now(), 1, '{}'::jsonb, now())",
		[16]byte(tombstone), [16]byte(ae.UUID{}), scope.TenantID,
		scope.ApplicationID, scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID)
	if err != nil {
		t.Fatalf("the tombstone could not be written as the owner: %v", err)
	}
	if read, err := store.Get(ctx, tenant, scope, tombstone); err != nil || read != nil {
		t.Fatalf("the tombstone was read: %v, %v", read, err)
	}
}

func TestInsertFinalizedStoresTheRecordedAtAndRefusesABlankActor(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "recorded_at"))
	finalized := finalizeCase(t, "verified-after-one-failed-attempt")
	bounds := ae.BoundsOf(finalized.Record.Scope)

	if err := store.InsertFinalized(ctx, bounds, &finalized, " ", nil); err == nil {
		t.Fatal("a blank actor was accepted")
	}
	if read, err := store.Get(ctx, bounds, finalized.Record.Scope, finalized.Record.ExperienceID); err != nil || read != nil {
		t.Fatalf("a refused write left a row: %v, %v", read, err)
	}

	recordedAt := time.Date(2026, 3, 2, 10, 11, 12, 345678000, time.UTC)
	if err := store.InsertFinalized(ctx, bounds, &finalized, actor, &recordedAt); err != nil {
		t.Fatal(err)
	}
	var stored time.Time
	err := store.transaction(ctx, bounds, func(tx pgx.Tx) error {
		return tx.QueryRow(ctx, "SELECT recorded_at FROM agent_experience.lifecycle_events WHERE experience_id = $1",
			[16]byte(finalized.Record.ExperienceID)).Scan(&stored)
	})
	if err != nil || !stored.Equal(recordedAt) {
		t.Fatalf("recorded_at is %v (%v), want %v", stored, err, recordedAt)
	}
}

// The store on a pool is used from many goroutines at once (run with -race): writes and searches interleave.
func TestTheStoreIsSafeForConcurrentUseOnAPool(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	store := newStore(t, admin, createDatabase(t, admin, "concurrent"))
	base := finalizeCase(t, "verified-after-one-failed-attempt")
	scope := base.Record.Scope
	bounds := ae.BoundsOf(scope)

	const writers = 8
	errs := make(chan error, 2*writers)
	var group sync.WaitGroup
	for n := 0; n < writers; n++ {
		group.Add(2)
		go func(n int) {
			defer group.Done()
			finalized := base
			id := finalized.Record.ExperienceID
			id[15] ^= byte(n + 1)
			finalized.Record.ExperienceID, finalized.Event.ExperienceID = id, id
			eventID := finalized.Event.EventID
			eventID[15] ^= byte(n + 1)
			finalized.Event.EventID = eventID
			errs <- store.InsertFinalized(ctx, bounds, &finalized, actor, nil)
		}(n)
		go func() {
			defer group.Done()
			_, err := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "refund stuck in pending, ticket lock"))
			errs <- err
		}()
	}
	group.Wait()
	close(errs)
	for err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	rows, err := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "refund stuck in pending, ticket lock"))
	if err != nil || len(rows) != writers {
		t.Fatalf("%d rows after %d writes: %v", len(rows), writers, err)
	}
}
