// Package pgtest is the PostgreSQL server the Go modules' PostgreSQL tests share (the postgres module's own and the
// Agent Framework integration's cross-language tests): it starts or finds the server, makes the roles, and creates
// databases migrated as the .NET library migrates them. It is a module of its own (Go 1.26), with the store's own
// PostgreSQL tests, so the store's module requires no container library. For tests only; nothing else imports it.
//
// The schema is never reimplemented here. Each test database is migrated by running the .NET migrator's own scripts
// (src/AgentExperience.Storage.Postgres/Migrations/*.sql) in name order, one transaction per script, as the owner
// role, which is what ExperienceSchemaMigrator.MigrateAsync does. The application role's privileges are the
// contract's sql/application-role-privileges.sql then, from contract 1.4, sql/application-role-privileges-1.4.sql (the
// injection table's) and, from contract 1.6, sql/application-role-privileges-1.6.sql (the embeddings table's), both
// modes (row-level security on, what a contract 1.x port runs against). From contract 1.6 the schema is also given
// the vectors package's migrations (src/AgentExperience.Storage.Postgres.Vectors/Migrations), after a superuser ran
// CREATE EXTENSION vector (pgvector is not a trusted extension), so the server must have pgvector: the container is
// the pgvector/pgvector image; a server named by PGHOST must have the extension installed.
//
// The server comes from PGHOST (with PGPORT, PGUSER, PGPASSWORD: a superuser connection) when set, else from a
// Testcontainers pgvector container (AGENTEXPERIENCE_POSTGRES_MAJOR, default 16). With neither, the tests skip, unless
// AGENTEXPERIENCE_REQUIRE_POSTGRES=1, when they fail. testcontainers-go applies TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX
// to every image it starts, the Reaper's (Ryuk) included, and recognises the Reaper by its name's suffix, so a
// prefix (CI's Docker Hub mirror) needs no workaround here, unlike testcontainers-python (python/tests/_postgres.py).
package pgtest

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"
	"github.com/testcontainers/testcontainers-go"
	tcpostgres "github.com/testcontainers/testcontainers-go/modules/postgres"

	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// The roles every test database uses.
const (
	OwnerRole       = "contract_owner"
	ApplicationRole = "contract_app"
	RolePassword    = "contract-role-password"
)

// Server is a PostgreSQL server and its superuser.
type Server struct {
	Host           string
	Port           int
	User, Password string
}

// ConnString is a connection string to database as user.
func (s Server) ConnString(database, user, password string) string {
	u := url.URL{
		Scheme:   "postgres",
		User:     url.UserPassword(user, password),
		Host:     net.JoinHostPort(s.Host, strconv.Itoa(s.Port)),
		Path:     "/" + database,
		RawQuery: "sslmode=disable",
	}
	return u.String()
}

// Admin is a connection string to database as the superuser.
func (s Server) Admin(database string) string { return s.ConnString(database, s.User, s.Password) }

// Application is a connection string to database as the application role.
func (s Server) Application(database string) string {
	return s.ConnString(database, ApplicationRole, RolePassword)
}

// Owner is a connection string to database as the owner role.
func (s Server) Owner(database string) string { return s.ConnString(database, OwnerRole, RolePassword) }

var (
	current     *Server
	unavailable string
)

// Run is a TestMain's body: it starts or finds the server, prepares the roles, runs the tests, and stops the server.
// Without a server the tests run and Require skips them, unless AGENTEXPERIENCE_REQUIRE_POSTGRES=1, when Run fails.
func Run(m *testing.M) int {
	ctx := context.Background()
	admin, stop, err := start(ctx)
	if err != nil {
		if os.Getenv("AGENTEXPERIENCE_REQUIRE_POSTGRES") == "1" {
			// A CI leg that exists to run these tests must fail rather than skip them.
			fmt.Fprintf(os.Stderr, "PostgreSQL is required but not available: %v\n", err)
			return 1
		}
		unavailable = err.Error()
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
	current = &admin
	return m.Run()
}

// Require is the server, or skips the test when there is none.
func Require(t *testing.T) Server {
	t.Helper()
	if current == nil {
		t.Skipf("PostgreSQL is not available: %s", unavailable)
	}
	return *current
}

func start(ctx context.Context) (Server, func(), error) {
	if host := os.Getenv("PGHOST"); host != "" {
		port := 5432
		if text := os.Getenv("PGPORT"); text != "" {
			var err error
			if port, err = strconv.Atoi(text); err != nil {
				return Server{}, nil, fmt.Errorf("PGPORT: %w", err)
			}
		}
		user := os.Getenv("PGUSER")
		if user == "" {
			user = "postgres"
		}
		return Server{host, port, user, os.Getenv("PGPASSWORD")}, func() {}, nil
	}
	major := os.Getenv("AGENTEXPERIENCE_POSTGRES_MAJOR")
	if major == "" {
		major = "16"
	}
	container, err := runContainer(ctx, "pgvector/pgvector:pg"+major)
	if err != nil {
		return Server{}, nil, fmt.Errorf("no PGHOST, and no Docker for a pgvector/pgvector:pg%s container: %w", major, err)
	}
	stop := func() { _ = testcontainers.TerminateContainer(container) }
	host, err := container.Host(ctx)
	if err != nil {
		stop()
		return Server{}, nil, err
	}
	port, err := container.MappedPort(ctx, "5432/tcp")
	if err != nil {
		stop()
		return Server{}, nil, err
	}
	return Server{host, int(port.Num()), "postgres", "postgres"}, stop, nil
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

// checkMajor makes sure a leg does not quietly run another major than the one it names.
func checkMajor(ctx context.Context, admin Server) error {
	requested := os.Getenv("AGENTEXPERIENCE_POSTGRES_MAJOR")
	if requested == "" {
		return nil
	}
	conn, err := pgx.Connect(ctx, admin.Admin("postgres"))
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

// roleLock is the advisory lock that serializes role preparation: test binaries of several packages or modules may
// share one server (PGHOST) and prepare the roles at once.
const roleLock = 0x6167_6578_7072_6f6c // "agexprol"

// prepareRoles makes the owner role that runs the migrations and the application role the stores use, as the .NET
// tests make them. It is safe to run concurrently on one server: one transaction under an advisory lock, and a
// role made by someone else in between is no failure.
func prepareRoles(ctx context.Context, admin Server) error {
	conn, err := pgx.Connect(ctx, admin.Admin("postgres"))
	if err != nil {
		return err
	}
	defer conn.Close(ctx)
	return pgx.BeginFunc(ctx, conn, func(tx pgx.Tx) error {
		if _, err := tx.Exec(ctx, "SELECT pg_advisory_xact_lock($1)", int64(roleLock)); err != nil {
			return err
		}
		for _, role := range []string{OwnerRole, ApplicationRole} {
			statement := "DO $$ BEGIN CREATE ROLE " + role + " LOGIN PASSWORD '" + RolePassword + "'; " +
				"EXCEPTION WHEN duplicate_object THEN NULL; END $$"
			if _, err := tx.Exec(ctx, statement); err != nil {
				return err
			}
		}
		// What a non-superuser owner needs to run the migrations (the erasure scripts set these parameters).
		_, err := tx.Exec(ctx, "GRANT SET ON PARAMETER agent_experience.purge_authorized, "+
			"agent_experience.access_purge_authorized TO "+OwnerRole)
		return err
	})
}

var roleName = regexp.MustCompile(`^[a-z_][a-z0-9_]*$`)

// The contract's privileges files: 1.0's, 1.4's for the injection table and 1.6's for the embeddings table.
const (
	PrivilegesFile   = "application-role-privileges.sql"
	Privileges14File = "application-role-privileges-1.4.sql"
	Privileges16File = "application-role-privileges-1.6.sql"
)

// ApplicationRolePrivileges are the statements of the contract's three privileges files for role (a plain
// identifier), the 1.0 file, the 1.4 file then the 1.6 file: what the .NET privileges call grants on a schema with
// the vectors migrations applied.
func ApplicationRolePrivileges(t *testing.T, role string) []string {
	t.Helper()
	privileges := PrivilegesOf(t, role, PrivilegesFile)
	privileges = append(privileges, PrivilegesOf(t, role, Privileges14File)...)
	return append(privileges, PrivilegesOf(t, role, Privileges16File)...)
}

// PrivilegesOf are the statements of one contract privileges file (under contract/sql/) for role (a plain
// identifier): those under "-- mode: always", then those under "-- mode: row-level-security".
func PrivilegesOf(t *testing.T, role, file string) []string {
	t.Helper()
	if !roleName.MatchString(role) {
		t.Fatalf("%q is not a plain identifier", role)
	}
	data, err := os.ReadFile(filepath.Join(ct.Contract, "sql", file))
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

// DatabaseName is a unique, valid database name for a label: a readable prefix and a hash of the whole label.
func DatabaseName(label string) string {
	digest := sha256.Sum256([]byte(label))
	prefix := regexp.MustCompile(`[^a-z0-9_]`).ReplaceAllString(strings.ToLower(label), "_")
	if len(prefix) > 40 {
		prefix = prefix[:40]
	}
	return "go_" + prefix + "_" + hex.EncodeToString(digest[:])[:12]
}

// CreateDatabase is a fresh database, migrated by the .NET scripts (the vectors package's too) and with the
// application role's privileges,
// dropped when the test ends (after the stores that use it close). Give each module's labels their own prefix: a
// server may be shared.
func CreateDatabase(t *testing.T, admin Server, label string) string {
	t.Helper()
	return CreateDatabaseWith(t, admin, label, ApplicationRolePrivileges(t, ApplicationRole))
}

// CreateDatabaseWith is CreateDatabase with the application role given exactly privileges: PrivilegesOf the 1.0 file
// alone, say, for a schema whose role was never granted the injection table.
func CreateDatabaseWith(t *testing.T, admin Server, label string, privileges []string) string {
	t.Helper()
	ctx := context.Background()
	name := DatabaseName(label)
	conn, err := pgx.Connect(ctx, admin.Admin("postgres"))
	if err != nil {
		t.Fatal(err)
	}
	identifier := pgx.Identifier{name}.Sanitize()
	for _, statement := range []string{
		"DROP DATABASE IF EXISTS " + identifier + " WITH (FORCE)",
		"CREATE DATABASE " + identifier + " OWNER " + OwnerRole,
	} {
		if _, err := conn.Exec(ctx, statement); err != nil {
			conn.Close(ctx)
			t.Fatal(err)
		}
	}
	conn.Close(ctx)
	t.Cleanup(func() {
		conn, err := pgx.Connect(context.Background(), admin.Admin("postgres"))
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
	vectorScripts, err := filepath.Glob(filepath.Join(ct.VectorMigrations, "*.sql"))
	if err != nil || len(vectorScripts) == 0 {
		t.Fatalf("no migration scripts under %s", ct.VectorMigrations)
	}
	sort.Strings(vectorScripts)
	scripts = append(scripts, vectorScripts...)
	// pgvector is not a trusted extension: a superuser installs it, as an operator does (contract 1.6).
	superuser, err := pgx.Connect(ctx, admin.Admin(name))
	if err != nil {
		t.Fatal(err)
	}
	_, err = superuser.Exec(ctx, "CREATE EXTENSION IF NOT EXISTS vector")
	superuser.Close(ctx)
	if err != nil {
		t.Fatal(err)
	}
	owner, err := pgx.Connect(ctx, admin.Owner(name))
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
		for _, statement := range privileges {
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
