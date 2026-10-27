// Package postgres is the PostgreSQL store: it writes finalized records and their initial event, reads records
// back, runs the contract's text search, records injections and (contract 1.8) holdout assignments, and (contract
// 1.6, with pgvector) writes and searches embeddings, all as the application role inside a transaction
// that first declared its bounds.
//
// It runs against a schema the .NET migrator applied (ExperienceSchemaMigrator.MigrateAsync, then
// ApplyApplicationRolePrivilegesAsync with row-level security on, or the contract's two privileges files) and never
// creates or alters a schema object. The bounds, the search and the injection record are the contract's SQL files,
// embedded and run as written: only their @name placeholders are spelled the way the driver spells them ($1, $2,
// ...).
package postgres

import (
	"context"
	"embed"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgtype"
	"github.com/jackc/pgx/v5/pgxpool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// The contract's SQL files, as this package embeds them.
const (
	DeclareBoundsSQL   = "declare-bounds.sql"
	SearchSQL          = "search-experience-text.sql"
	RecordInjectionSQL = "record-injection.sql"
	// WriteEmbeddingSQL and SearchVectorSQL are contract 1.6's: each holds several statements, each starting at its
	// "-- statement: <name>" line (ContractStatements).
	WriteEmbeddingSQL = "write-experience-embedding.sql"
	SearchVectorSQL   = "search-experience-vector.sql"
	// ListLessonsSQL is contract 1.7's: four statements (page, page-with-statuses, page-without-injections,
	// page-without-injections-with-statuses), each starting at its "-- statement: <name>" line (ContractStatements).
	ListLessonsSQL = "list-experience-lessons.sql"
	// RecordHoldoutSQL is contract 1.8's: the statement that records a run's holdout assignment.
	RecordHoldoutSQL = "record-holdout-assignment.sql"
)

const (
	recordsTable = "agent_experience.experience_records"
	eventsTable  = "agent_experience.lifecycle_events"
)

//go:embed sql/declare-bounds.sql sql/search-experience-text.sql sql/record-injection.sql
//go:embed sql/write-experience-embedding.sql sql/search-experience-vector.sql sql/list-experience-lessons.sql
//go:embed sql/record-holdout-assignment.sql
var contractFiles embed.FS

// ContractSQL is one of the contract's SQL files, as this package embeds it (DeclareBoundsSQL, SearchSQL,
// RecordInjectionSQL). A test keeps the embedded copies identical to contract/sql/.
func ContractSQL(name string) (string, error) {
	data, err := contractFiles.ReadFile("sql/" + name)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

// ContractStatements are the statements of a contract SQL file that holds several, by name: each starts at its
// "-- statement: <name>" line and runs to the next one (or the end); the header before the first is not a statement.
func ContractStatements(name string) (map[string]string, error) {
	text, err := ContractSQL(name)
	if err != nil {
		return nil, err
	}
	lines := map[string][]string{}
	current := ""
	started := false
	for _, line := range strings.Split(strings.ReplaceAll(text, "\r\n", "\n"), "\n") {
		if label, ok := strings.CutPrefix(line, "-- statement: "); ok {
			current, started = strings.TrimSpace(label), true
			lines[current] = nil
			continue
		}
		if started {
			lines[current] = append(lines[current], line)
		}
	}
	statements := make(map[string]string, len(lines))
	for key, body := range lines {
		statements[key] = strings.TrimSpace(strings.Join(body, "\n"))
	}
	return statements, nil
}

// Statement is a SQL text with its @name placeholders numbered for the driver, and the parameter each number
// stands for.
type Statement struct {
	SQL   string
	Names []string
}

// Args are the statement's positional arguments, taken by name from values. A name with no value is an error.
func (s Statement) Args(values map[string]any) ([]any, error) {
	args := make([]any, len(s.Names))
	for i, name := range s.Names {
		value, ok := values[name]
		if !ok {
			return nil, fmt.Errorf("the statement's parameter @%s has no value", name)
		}
		args[i] = value
	}
	return args, nil
}

// ToDriverPlaceholders numbers the contract's @name placeholders as $1, $2, ... (one number per distinct name).
// Only code is rewritten: a -- or /* */ comment, a single-quoted literal (E” escapes included), a double-quoted
// identifier and a dollar-quoted string are kept as written, so a parameter named only inside one of them is never
// asked for. An @ followed by a letter or _ in code is always a placeholder, so PostgreSQL's prefix @ operator
// must be written with a space after it.
func ToDriverPlaceholders(sql string) Statement {
	var out strings.Builder
	var names []string
	numbers := map[string]int{}
	length := len(sql)
	until := func(end string, start int) int {
		found := strings.Index(sql[start:], end)
		if found < 0 {
			return length
		}
		return start + found + len(end)
	}
	isWordByte := func(b byte) bool {
		return b == '_' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
	}
	for index := 0; index < length; {
		ch := sql[index]
		stop := -1
		switch {
		case strings.HasPrefix(sql[index:], "--"):
			stop = until("\n", index)
		case strings.HasPrefix(sql[index:], "/*"):
			depth, at := 1, index+2
			for at < length && depth > 0 {
				switch {
				case strings.HasPrefix(sql[at:], "/*"):
					depth, at = depth+1, at+2
				case strings.HasPrefix(sql[at:], "*/"):
					depth, at = depth-1, at+2
				default:
					at++
				}
			}
			stop = at
		case ch == '\'':
			escaped := index > 0 && (sql[index-1] == 'e' || sql[index-1] == 'E') && (index < 2 || !isWordByte(sql[index-2]))
			at := index + 1
			for at < length {
				if escaped && sql[at] == '\\' {
					at += 2
				} else if sql[at] == '\'' {
					if strings.HasPrefix(sql[at:], "''") {
						at += 2
					} else {
						at++
						break
					}
				} else {
					at++
				}
			}
			stop = min(at, length)
		case ch == '"':
			at := index + 1
			for at < length {
				if strings.HasPrefix(sql[at:], `""`) {
					at += 2
				} else if sql[at] == '"' {
					at++
					break
				} else {
					at++
				}
			}
			stop = min(at, length)
		case ch == '$' && (index == 0 || !isIdentifierByte(sql[index-1])):
			// A $ inside an identifier (a$b) or a parameter ($1) never starts a dollar quote.
			if tag := dollarTag(sql[index:]); tag != "" {
				stop = until(tag, index+len(tag))
			}
		}
		if stop >= 0 {
			out.WriteString(sql[index:stop])
			index = stop
			continue
		}
		if ch == '@' && index+1 < length && (sql[index+1] == '_' || isLetter(sql[index+1])) {
			end := index + 1
			for end < length && isWordByte(sql[end]) {
				end++
			}
			name := sql[index+1 : end]
			number, ok := numbers[name]
			if !ok {
				names = append(names, name)
				number = len(names)
				numbers[name] = number
			}
			out.WriteString("$" + strconv.Itoa(number))
			index = end
			continue
		}
		out.WriteByte(ch)
		index++
	}
	return Statement{SQL: out.String(), Names: names}
}

// isIdentifierByte reports a byte that can continue an identifier (or a $n parameter): then a $ after it does not
// start a dollar quote.
func isIdentifierByte(b byte) bool {
	return b == '_' || b == '$' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') ||
		b >= 0x80
}

func isLetter(b byte) bool { return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') }

// dollarTag is the dollar-quote tag that starts s ($$ or $tag$), or "".
func dollarTag(s string) string {
	if len(s) < 2 || s[0] != '$' {
		return ""
	}
	if s[1] == '$' {
		return "$$"
	}
	if s[1] != '_' && !isLetter(s[1]) {
		return ""
	}
	for i := 2; i < len(s); i++ {
		switch b := s[i]; {
		case b == '$':
			return s[:i+1]
		case b == '_' || isLetter(b) || (b >= '0' && b <= '9'):
		default:
			return ""
		}
	}
	return ""
}

// Beginner starts the transactions the store runs in. *pgxpool.Pool, *pgx.Conn and pgx.Tx (each transaction is
// then a savepoint inside it) all satisfy it. Every connection it hands out must be the application role's.
type Beginner interface {
	Begin(ctx context.Context) (pgx.Tx, error)
}

// ErrSealed is Get's answer for a record that exists in the scope but is sealed (payload_version 2, crypto-shredding),
// which a contract 1.x port cannot read. A record that does not exist is (nil, nil).
var ErrSealed = errors.New("the record is sealed (payload_version 2) and a contract 1.x port cannot read it")

// Store keeps records in PostgreSQL through pgx. Each operation runs one transaction, begun on the connection
// source the host gave it. A Store is safe for concurrent use exactly when its source is: a *pgxpool.Pool is, a
// single *pgx.Conn or pgx.Tx is not (use it from one goroutine at a time).
type Store struct {
	db      Beginner
	pool    *pgxpool.Pool // set when the store made the pool itself (Open), and closes it
	declare Statement
	search  Statement
	record  Statement
	// Contract 1.6's: the embedding write (its two statements), and the vector search statements, which keep their
	// {dim} until the query vector's length is known.
	writeEmbedding Statement
	probeRevision  Statement
	vectorSQL      map[string]string
	probes         map[string]Statement
	// Contract 1.7's: the lessons view's four statements, by name.
	lessons map[string]Statement
	// Contract 1.8's: the holdout assignment record.
	holdout Statement
}

// NewStore is a store that runs its transactions on db, which the host controls (and closes).
func NewStore(db Beginner) (*Store, error) {
	if db == nil {
		return nil, errors.New("the store needs a connection source")
	}
	declare, err := ContractSQL(DeclareBoundsSQL)
	if err != nil {
		return nil, err
	}
	search, err := ContractSQL(SearchSQL)
	if err != nil {
		return nil, err
	}
	record, err := ContractSQL(RecordInjectionSQL)
	if err != nil {
		return nil, err
	}
	writes, err := ContractStatements(WriteEmbeddingSQL)
	if err != nil {
		return nil, err
	}
	vectors, err := ContractStatements(SearchVectorSQL)
	if err != nil {
		return nil, err
	}
	lessonStatements, err := ContractStatements(ListLessonsSQL)
	if err != nil {
		return nil, err
	}
	holdout, err := ContractSQL(RecordHoldoutSQL)
	if err != nil {
		return nil, err
	}
	lessons := map[string]Statement{}
	for _, name := range []string{"page", "page-with-statuses", "page-without-injections",
		"page-without-injections-with-statuses"} {
		if lessonStatements[name] == "" {
			return nil, fmt.Errorf("%s has no %q statement", ListLessonsSQL, name)
		}
		lessons[name] = ToDriverPlaceholders(lessonStatements[name])
	}
	store := &Store{db: db, lessons: lessons, declare: ToDriverPlaceholders(declare), search: ToDriverPlaceholders(search),
		record: ToDriverPlaceholders(record), writeEmbedding: ToDriverPlaceholders(writes["write"]),
		probeRevision: ToDriverPlaceholders(writes["probe-revision"]), vectorSQL: vectors,
		probes: map[string]Statement{}, holdout: ToDriverPlaceholders(holdout)}
	for _, name := range []string{"write", "probe-revision"} {
		if writes[name] == "" {
			return nil, fmt.Errorf("%s has no %q statement", WriteEmbeddingSQL, name)
		}
	}
	for _, name := range []string{"search", "search-excluding-model-authored", "compatibility",
		"compatibility-excluding-model-authored"} {
		if vectors[name] == "" {
			return nil, fmt.Errorf("%s has no %q statement", SearchVectorSQL, name)
		}
	}
	for _, name := range []string{"compatibility", "compatibility-excluding-model-authored"} {
		store.probes[name] = ToDriverPlaceholders(vectors[name])
	}
	return store, nil
}

// Open is a store on a new connection pool for a connection string (as the application role). Close it with Close.
func Open(ctx context.Context, connString string) (*Store, error) {
	pool, err := pgxpool.New(ctx, connString)
	if err != nil {
		return nil, err
	}
	store, err := NewStore(pool)
	if err != nil {
		pool.Close()
		return nil, err
	}
	store.pool = pool
	return store, nil
}

// Close closes the pool Open made. It does nothing for a store on a source the host gave NewStore.
func (s *Store) Close() {
	if s.pool != nil {
		s.pool.Close()
	}
}

// transaction runs fn in one transaction that first declared bounds, and commits when fn succeeds.
func (s *Store) transaction(ctx context.Context, bounds ae.AuthorizationBounds, fn func(tx pgx.Tx) error) error {
	return pgx.BeginFunc(ctx, s.db, func(tx pgx.Tx) error {
		parameters := bounds.Parameters()
		values := make(map[string]any, len(parameters))
		for name, value := range parameters {
			values[name] = value
		}
		args, err := s.declare.Args(values)
		if err != nil {
			return err
		}
		if _, err := tx.Exec(ctx, s.declare.SQL, args...); err != nil {
			return err
		}
		return fn(tx)
	})
}

var insertRecordSQL = func() string {
	placeholders := make([]string, len(ae.ColumnNames))
	for i := range placeholders {
		placeholders[i] = "$" + strconv.Itoa(i+1)
	}
	return "INSERT INTO " + recordsTable + " (" + strings.Join(ae.ColumnNames, ", ") + ") VALUES (" +
		strings.Join(placeholders, ", ") + ")"
}()

var eventColumns = []string{"event_id", "experience_id", "tenant_id", "application_id", "project_id", "team_id",
	"agent_id", "user_id", "prior_status", "current_status", "reason", "producer", "occurred_at", "recorded_at",
	"expected_revision", "applied_revision", "actor"}

var insertEventSQL = func() string {
	placeholders := make([]string, len(eventColumns))
	for i := range placeholders {
		placeholders[i] = "$" + strconv.Itoa(i+1)
	}
	return "INSERT INTO " + eventsTable + " (" + strings.Join(eventColumns, ", ") + ") VALUES (" +
		strings.Join(placeholders, ", ") + ")"
}()

const selectRecordSQL = "SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, " +
	"agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, " +
	"created_at, updated_at, payload_version, payload FROM " + recordsTable + " WHERE experience_id = $1 " +
	"AND tenant_id = $2 AND application_id = $3 AND project_id = $4 AND team_id IS NOT DISTINCT FROM $5 " +
	"AND agent_id IS NOT DISTINCT FROM $6 AND user_id IS NOT DISTINCT FROM $7 AND deleted_at IS NULL"

func recordArgs(record *ae.ExperienceRecord) ([]any, error) {
	payload, err := ae.MarshalJSONValue(record.Payload())
	if err != nil {
		return nil, err
	}
	scope := record.Scope
	return []any{
		[16]byte(record.ExperienceID), [16]byte(record.SourceRunID), scope.TenantID, scope.ApplicationID,
		scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID, record.TaskID, string(record.Status),
		record.ReuseConfidence, record.SupportingValidations, record.Contradictions, record.Revision,
		record.CreatedAt.TruncateToMicroseconds().Time(), record.UpdatedAt.TruncateToMicroseconds().Time(),
		int64(ae.PayloadVersion), payload,
	}, nil
}

// InsertFinalized writes a finalized record in its final state at revision 1, and the initial lifecycle event that
// took it there, in one transaction. actor is the authorizing principal; recordedAt defaults to now. The event must
// be the record's own (its experience ID, its status and its revision); anything else is refused before writing.
func (s *Store) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, finalized *ae.FinalizedExperience,
	actor string, recordedAt *time.Time) error {
	if finalized == nil {
		return errors.New("there is no finalized record to write")
	}
	if ae.IsNullOrWhiteSpace(actor) {
		return errors.New("the actor (the authorizing principal) must not be blank")
	}
	record, event := &finalized.Record, &finalized.Event
	if event.ExperienceID != record.ExperienceID || event.CurrentStatus != record.Status ||
		event.AppliedRevision != record.Revision {
		return errors.New("the lifecycle event is not the record's own: its experience ID, current status and " +
			"applied revision must be the record's")
	}
	args, err := recordArgs(record)
	if err != nil {
		return err
	}
	at := time.Now().UTC()
	if recordedAt != nil {
		at = *recordedAt
	}
	scope := record.Scope
	return s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		if _, err := tx.Exec(ctx, insertRecordSQL, args...); err != nil {
			return err
		}
		_, err := tx.Exec(ctx, insertEventSQL,
			[16]byte(event.EventID), [16]byte(event.ExperienceID), scope.TenantID, scope.ApplicationID,
			scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID, string(event.PriorStatus),
			string(event.CurrentStatus), event.Reason, event.Producer, event.OccurredAt.Time(), at,
			event.ExpectedRevision, event.AppliedRevision, actor)
		return err
	})
}

// InsertRecord writes one record's row exactly as given, with no lifecycle event. It is for seeding and tests: a
// record this package finalizes is written with InsertFinalized, or it has no history.
func (s *Store) InsertRecord(ctx context.Context, bounds ae.AuthorizationBounds, record *ae.ExperienceRecord) error {
	args, err := recordArgs(record)
	if err != nil {
		return err
	}
	return s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		_, err := tx.Exec(ctx, insertRecordSQL, args...)
		return err
	})
}

// Get reads one record in exactly scope (every scope field equal, an unset one unset), within bounds. It returns
// (nil, nil) when there is none (or it was deleted), and ErrSealed when it is sealed (payload_version 2, which a
// contract 1.x port cannot read).
func (s *Store) Get(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope, id ae.UUID) (*ae.ExperienceRecord, error) {
	var record *ae.ExperienceRecord
	err := s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		rows, err := tx.Query(ctx, selectRecordSQL, [16]byte(id), scope.TenantID, scope.ApplicationID,
			scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID)
		if err != nil {
			return err
		}
		defer rows.Close()
		if rows.Next() {
			var row recordRow
			if err := rows.Scan(row.targets()...); err != nil {
				return err
			}
			if row.payloadVersion != ae.PayloadVersion {
				return ErrSealed
			}
			read, err := row.record()
			if err != nil {
				return err
			}
			record = &read
		}
		return rows.Err()
	})
	return record, err
}

// Search runs sql/search-experience-text.sql and returns its rows in the order returned, each relevance widened
// from real. A sealed row (payload_version 2) is skipped. Rank them with agentexperience.Rank.
func (s *Store) Search(ctx context.Context, bounds ae.AuthorizationBounds, parameters ae.SearchParameters) ([]ae.SearchRow, error) {
	values, err := parameters.Values()
	if err != nil {
		return nil, err
	}
	args, err := s.search.Args(values)
	if err != nil {
		return nil, err
	}
	var result []ae.SearchRow
	err = s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		rows, err := tx.Query(ctx, s.search.SQL, args...)
		if err != nil {
			return err
		}
		defer rows.Close()
		fields := rows.FieldDescriptions()
		count := len(ae.ColumnNames)
		if len(fields) < count+2 {
			return errors.New("the text search returned fewer columns than expected")
		}
		for i, name := range ae.ColumnNames {
			if fields[i].Name != name {
				return errors.New("the text search returned columns in an unexpected order")
			}
		}
		if fields[count].Name != "relevance" || fields[count+1].Name != "shared_by_grant" {
			return errors.New("the text search returned columns in an unexpected order")
		}
		for rows.Next() {
			var row recordRow
			var relevance *float32
			var shared *bool
			targets := append(row.targets(), &relevance, &shared)
			for range fields[count+2:] {
				targets = append(targets, new(any))
			}
			if err := rows.Scan(targets...); err != nil {
				return err
			}
			if row.payloadVersion != ae.PayloadVersion {
				continue
			}
			if relevance == nil {
				return errors.New("the text search returned a row with no relevance")
			}
			record, err := row.record()
			if err != nil {
				return err
			}
			result = append(result, ae.SearchRow{
				Record:        record,
				Relevance:     float64(*relevance),
				SharedByGrant: shared != nil && *shared,
			})
		}
		return rows.Err()
	})
	return result, err
}

// RecordInjection records that experienceIDs were injected in scope at injectedAt: it runs sql/record-injection.sql
// (contract 1.4) once, in one transaction that declared bounds. Only a record live in exactly scope gets a row (one
// borrowed through a grant, or in another scope, is skipped by the statement); the instant is truncated to whole
// microseconds and clamped to the database clock. It refuses more than agentexperience.MaxRecordInjectionIDs IDs or
// a nil one, and a scope outside the bounds (agentexperience.ErrAuthorizationDenied); otherwise a failure is the
// driver's (insufficient_privilege, 42501, until the role holds the grants of
// sql/application-role-privileges-1.4.sql). The agent integration calls it in the background, fail-open.
func (s *Store) RecordInjection(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	experienceIDs []ae.UUID, injectedAt ae.Instant) error {
	ids, err := ae.InjectionIDs(experienceIDs)
	if err != nil {
		return err
	}
	if !bounds.Permits(scope) {
		return ae.ErrAuthorizationDenied
	}
	if len(ids) == 0 {
		return nil
	}
	values := make([][16]byte, len(ids))
	for i, id := range ids {
		values[i] = [16]byte(id)
	}
	args, err := s.record.Args(map[string]any{
		"experience_ids": values,
		"injected_at":    injectedAt.TruncateToMicroseconds().Time(),
		"tenant_id":      scope.TenantID,
		"application_id": scope.ApplicationID,
		"project_id":     scope.ProjectID,
		"team_id":        scope.TeamID,
		"agent_id":       scope.AgentID,
		"user_id":        scope.UserID,
	})
	if err != nil {
		return err
	}
	return s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		_, err := tx.Exec(ctx, s.record.SQL, args...)
		return err
	})
}

var _ ae.InjectionRecorder = (*Store)(nil)

// RecordHoldoutAssignment records a run's holdout assignment in scope (contract 1.8): it runs
// sql/record-holdout-assignment.sql once, in one transaction that declared bounds. Only a record live in exactly scope
// gets a row, its scope copied from the record, at the revision named (a record named twice counts once, at the
// revision first named); a row already there for the run and record is kept; the instant is truncated to whole
// microseconds and clamped to the database clock; nothing is written for a run whose own records are all erased (the
// late-write guard). It refuses an assignment agentexperience.HoldoutRecords refuses, and a scope outside the bounds
// (agentexperience.ErrAuthorizationDenied); otherwise a failure is the driver's (insufficient_privilege, 42501, until
// the role holds the grants of sql/application-role-privileges-1.8.sql). The agent integration calls it in the
// background, fail-open.
//
// The statement passes its arrays to unnest, which PostgreSQL cannot type from an untyped parameter, so every parameter
// is sent with its type (holdoutTypes), as .NET's driver sends them.
func (s *Store) RecordHoldoutAssignment(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	assignment ae.HoldoutAssignment) error {
	records, err := ae.HoldoutRecords(assignment)
	if err != nil {
		return err
	}
	if !bounds.Permits(scope) {
		return ae.ErrAuthorizationDenied
	}
	if len(records) == 0 {
		return nil
	}
	ids := make([]pgtype.UUID, len(records))
	revisions := make([]int64, len(records))
	for i, record := range records {
		ids[i], revisions[i] = pgtype.UUID{Bytes: record.ExperienceID, Valid: true}, record.Revision
	}
	args, err := s.holdout.Args(map[string]any{
		"run_id":         pgtype.UUID{Bytes: assignment.RunID, Valid: true},
		"arm":            string(assignment.Arm),
		"experience_ids": ids,
		"revisions":      revisions,
		"assigned_at":    assignment.AssignedAt.TruncateToMicroseconds().Time(),
		"tenant_id":      scope.TenantID,
		"application_id": scope.ApplicationID,
		"project_id":     scope.ProjectID,
		"team_id":        scope.TeamID,
		"agent_id":       scope.AgentID,
		"user_id":        scope.UserID,
	})
	if err != nil {
		return err
	}
	return s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		return execTyped(ctx, tx, s.holdout, args, holdoutTypes)
	})
}

// holdoutTypes are the types of record-holdout-assignment.sql's parameters, by name.
var holdoutTypes = map[string]uint32{
	"run_id": pgtype.UUIDOID, "arm": pgtype.TextOID, "experience_ids": pgtype.UUIDArrayOID,
	"revisions": pgtype.Int8ArrayOID, "assigned_at": pgtype.TimestamptzOID, "tenant_id": pgtype.TextOID,
	"application_id": pgtype.TextOID, "project_id": pgtype.TextOID, "team_id": pgtype.TextOID,
	"agent_id": pgtype.TextOID, "user_id": pgtype.TextOID,
}

// execTyped runs a statement with each parameter sent as its named type, encoded in the text format by the
// connection's type map: for a statement PostgreSQL cannot type from its text alone.
func execTyped(ctx context.Context, tx pgx.Tx, statement Statement, args []any, types map[string]uint32) error {
	typeMap := tx.Conn().TypeMap()
	values := make([][]byte, len(args))
	oids := make([]uint32, len(args))
	for i, name := range statement.Names {
		oid, ok := types[name]
		if !ok {
			return fmt.Errorf("the statement's parameter @%s has no type", name)
		}
		encoded, err := typeMap.Encode(oid, pgtype.TextFormatCode, args[i], nil)
		if err != nil {
			return fmt.Errorf("the statement's parameter @%s: %w", name, err)
		}
		values[i], oids[i] = encoded, oid
	}
	return tx.Conn().PgConn().ExecParams(ctx, statement.SQL, values, oids, nil, nil).Read().Err
}

var _ ae.HoldoutRecorder = (*Store)(nil)

// WriteEmbedding stores a record's embedding (contract 1.6): in one transaction that declared bounds, "write" from
// sql/write-experience-embedding.sql and, when it wrote no row, "probe-revision". Written; Stale with the record's
// current revision; Missing for an unknown ID, a record in another scope or an erased one. The write is validated
// first (an error, and nothing runs). Needs the grants of sql/application-role-privileges-1.6.sql and the vectors
// migrations.
func (s *Store) WriteEmbedding(ctx context.Context, bounds ae.AuthorizationBounds,
	write ae.EmbeddingWrite) (ae.EmbeddingWriteResult, error) {
	if err := write.Validate(); err != nil {
		return ae.EmbeddingWriteResult{}, err
	}
	scope := write.Scope
	values := map[string]any{
		"experience_id":   [16]byte(write.ExperienceID),
		"tenant_id":       scope.TenantID,
		"application_id":  scope.ApplicationID,
		"project_id":      scope.ProjectID,
		"team_id":         scope.TeamID,
		"agent_id":        scope.AgentID,
		"user_id":         scope.UserID,
		"model_id":        write.ModelID,
		"dimension":       int32(write.Dimension),
		"content_hash":    write.ContentHash,
		"source_revision": write.SourceRevision,
		"embedding":       ae.VectorLiteral(write.Vector),
		"now":             write.Now.TruncateToMicroseconds().Time(),
	}
	writeArgs, err := s.writeEmbedding.Args(values)
	if err != nil {
		return ae.EmbeddingWriteResult{}, err
	}
	probeArgs, err := s.probeRevision.Args(values)
	if err != nil {
		return ae.EmbeddingWriteResult{}, err
	}
	result := ae.EmbeddingWriteResult{Outcome: ae.EmbeddingWritten}
	err = s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		tag, err := tx.Exec(ctx, s.writeEmbedding.SQL, writeArgs...)
		if err != nil {
			return err
		}
		if tag.RowsAffected() == 1 {
			return nil
		}
		var revision int64
		switch err := tx.QueryRow(ctx, s.probeRevision.SQL, probeArgs...).Scan(&revision); {
		case errors.Is(err, pgx.ErrNoRows):
			result = ae.EmbeddingWriteResult{Outcome: ae.EmbeddingMissing}
		case err != nil:
			return err
		default:
			result = ae.EmbeddingWriteResult{Outcome: ae.EmbeddingStale, CurrentRevision: revision}
		}
		return nil
	})
	if err != nil {
		return ae.EmbeddingWriteResult{}, err
	}
	return result, nil
}

// SearchVectors is the nearest-neighbour search (contract 1.6): in one transaction that declared bounds, "search" (or
// "search-excluding-model-authored") from sql/search-experience-vector.sql with {dim} the query vector's length and,
// when it returns no row, the matching compatibility probe. Hits come back nearest first, ties by experience ID,
// NaN last; a sealed row (payload_version 2) is skipped. The query is validated first (an error, and nothing runs).
func (s *Store) SearchVectors(ctx context.Context, bounds ae.AuthorizationBounds,
	query ae.VectorQuery) (ae.VectorSearchResult, error) {
	if err := query.Validate(); err != nil {
		return ae.VectorSearchResult{}, err
	}
	searchName, probeName := "search", "compatibility"
	if query.ExcludeModelAuthored {
		searchName, probeName = "search-excluding-model-authored", "compatibility-excluding-model-authored"
	}
	search := ToDriverPlaceholders(strings.ReplaceAll(s.vectorSQL[searchName], "{dim}",
		strconv.Itoa(len(query.Vector))))
	probe := s.probes[probeName]
	scope := query.Scope
	statuses := query.EffectiveStatuses()
	names := make([]string, len(statuses))
	for i, status := range statuses {
		names[i] = string(status)
	}
	values := map[string]any{
		"tenant_id":      scope.TenantID,
		"application_id": scope.ApplicationID,
		"project_id":     scope.ProjectID,
		"team_id":        scope.TeamID,
		"agent_id":       scope.AgentID,
		"user_id":        scope.UserID,
		"statuses":       names,
		"min_confidence": query.EffectiveMinConfidence(),
		"model_id":       query.ModelID,
		"query_vector":   ae.VectorLiteral(query.Vector),
		"limit":          int32(query.EffectiveLimit()),
	}
	searchArgs, err := search.Args(values)
	if err != nil {
		return ae.VectorSearchResult{}, err
	}
	probeArgs, err := probe.Args(values)
	if err != nil {
		return ae.VectorSearchResult{}, err
	}
	result := ae.VectorSearchResult{Outcome: ae.VectorFound}
	err = s.transaction(ctx, bounds, func(tx pgx.Tx) error {
		rows, err := tx.Query(ctx, search.SQL, searchArgs...)
		if err != nil {
			return err
		}
		defer rows.Close()
		fields := rows.FieldDescriptions()
		count := len(ae.ColumnNames)
		if len(fields) != count+4 || fields[count+3].Name != "distance" {
			return errors.New("the vector search returned columns in an unexpected order")
		}
		for i, name := range ae.ColumnNames {
			if fields[i].Name != name {
				return errors.New("the vector search returned columns in an unexpected order")
			}
		}
		returned := 0
		for rows.Next() {
			returned++
			var row recordRow
			var distance *float64
			targets := append(row.targets(), new(any), new(any), new(any), &distance)
			if err := rows.Scan(targets...); err != nil {
				return err
			}
			if row.payloadVersion != ae.PayloadVersion {
				continue
			}
			if distance == nil {
				return errors.New("the vector search returned a row with no distance")
			}
			record, err := row.record()
			if err != nil {
				return err
			}
			result.Hits = append(result.Hits, ae.VectorHit{Record: record, Distance: *distance})
		}
		if err := rows.Err(); err != nil {
			return err
		}
		rows.Close()
		if returned > 0 {
			return nil
		}
		var comparable, sameModel bool
		if err := tx.QueryRow(ctx, probe.SQL, probeArgs...).Scan(&comparable, &sameModel); err != nil {
			return err
		}
		switch {
		case !comparable:
		case sameModel:
			result.Outcome = ae.VectorDimensionMismatch
		default:
			result.Outcome = ae.VectorModelMismatch
		}
		return nil
	})
	if err != nil {
		return ae.VectorSearchResult{}, err
	}
	result.Hits = ae.OrderHits(result.Hits)
	return result, nil
}

var _ ae.EmbeddingIndex = (*Store)(nil)

// ListLessonRecords is the lessons view's read (contract 1.7): in one transaction that declared bounds, "page" (or
// "page-with-statuses") from sql/list-experience-lessons.sql, the scope's live records newest first, each with its last
// injection. When that fails with undefined_table (42P01) or insufficient_privilege (42501), the injections cannot be
// read: the transaction is rolled back, a new one declares the same bounds and runs "page-without-injections" (or
// "...-with-statuses"), and the answer says its injections are not tracked. Any other failure is the call's. A scope
// outside bounds is ErrAuthorizationDenied and runs no statement; a limit outside 1 to 500 and empty or undefined
// statuses are refused first. A sealed row (payload_version 2) is left out. It never writes.
func (s *Store) ListLessonRecords(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	statuses []ae.ExperienceStatus, limit int) (ae.LessonRecords, error) {
	if err := ae.ValidateLessonsRead(statuses, limit); err != nil {
		return ae.LessonRecords{}, err
	}
	if !bounds.Permits(scope) {
		return ae.LessonRecords{}, ae.ErrAuthorizationDenied
	}
	values := map[string]any{
		"tenant_id":      scope.TenantID,
		"application_id": scope.ApplicationID,
		"project_id":     scope.ProjectID,
		"team_id":        scope.TeamID,
		"agent_id":       scope.AgentID,
		"user_id":        scope.UserID,
		"limit":          int32(limit),
	}
	suffix := ""
	if statuses != nil {
		names := make([]string, len(statuses))
		for i, status := range statuses {
			names[i] = string(status)
		}
		values["statuses"] = names
		suffix = "-with-statuses"
	}
	read := func(name string, injections bool) (ae.LessonRecords, error) {
		statement := s.lessons[name+suffix]
		args, err := statement.Args(values)
		if err != nil {
			return ae.LessonRecords{}, err
		}
		result := ae.LessonRecords{InjectionsTracked: injections}
		err = s.transaction(ctx, bounds, func(tx pgx.Tx) error {
			rows, err := tx.Query(ctx, statement.SQL, args...)
			if err != nil {
				return err
			}
			defer rows.Close()
			fields := rows.FieldDescriptions()
			if len(fields) == 0 {
				// The statement failed (the driver reports it once the rows are closed, not when it sends them).
				rows.Close()
				if err := rows.Err(); err != nil {
					return err
				}
			}
			count := len(ae.ColumnNames)
			want := count
			if injections {
				want += 2
			}
			if len(fields) != want {
				return errors.New("the lessons read returned columns in an unexpected order")
			}
			for i, name := range ae.ColumnNames {
				if fields[i].Name != name {
					return errors.New("the lessons read returned columns in an unexpected order")
				}
			}
			for rows.Next() {
				result.Answered++
				var row recordRow
				var lastInjectedAt *time.Time
				var injectionCount *int64
				targets := row.targets()
				if injections {
					targets = append(targets, &lastInjectedAt, &injectionCount)
				}
				if err := rows.Scan(targets...); err != nil {
					return err
				}
				if row.payloadVersion != ae.PayloadVersion {
					continue
				}
				record, err := row.record()
				if err != nil {
					return err
				}
				lesson := ae.LessonRecord{Record: record}
				if lastInjectedAt != nil && injectionCount != nil {
					at, err := ae.InstantFromTime(*lastInjectedAt)
					if err != nil {
						return err
					}
					lesson.Injection = &ae.InjectionSummary{LastInjectedAt: at, InjectionCount: *injectionCount}
				}
				result.Records = append(result.Records, lesson)
			}
			return rows.Err()
		})
		if err != nil {
			return ae.LessonRecords{}, err
		}
		return result, nil
	}
	result, err := read("page", true)
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && (pgErr.Code == "42P01" || pgErr.Code == "42501") {
		return read("page-without-injections", false)
	}
	return result, err
}

var _ ae.LessonsReader = (*Store)(nil)

// recordRow is the eighteen record columns as the driver reads them.
type recordRow struct {
	experienceID, sourceRunID                  [16]byte
	tenantID, applicationID, projectID, taskID string
	teamID, agentID, userID                    *string
	status                                     string
	reuseConfidence                            float64
	supportingValidations, contradictions      int64
	revision, payloadVersion                   int64
	createdAt, updatedAt                       time.Time
	payload                                    []byte
}

func (r *recordRow) targets() []any {
	return []any{&r.experienceID, &r.sourceRunID, &r.tenantID, &r.applicationID, &r.projectID, &r.teamID,
		&r.agentID, &r.userID, &r.taskID, &r.status, &r.reuseConfidence, &r.supportingValidations,
		&r.contradictions, &r.revision, &r.createdAt, &r.updatedAt, &r.payloadVersion, &r.payload}
}

func (r *recordRow) record() (ae.ExperienceRecord, error) {
	optional := func(value *string) any {
		if value == nil {
			return nil
		}
		return *value
	}
	createdAt, err := ae.InstantFromTime(r.createdAt)
	if err != nil {
		return ae.ExperienceRecord{}, err
	}
	updatedAt, err := ae.InstantFromTime(r.updatedAt)
	if err != nil {
		return ae.ExperienceRecord{}, err
	}
	reuse, err := ae.MarshalJSONValue(r.reuseConfidence)
	if err != nil {
		return ae.ExperienceRecord{}, err
	}
	columns := ae.NewObject(
		ae.Member{Key: "experience_id", Value: ae.UUID(r.experienceID).String()},
		ae.Member{Key: "source_run_id", Value: ae.UUID(r.sourceRunID).String()},
		ae.Member{Key: "tenant_id", Value: r.tenantID},
		ae.Member{Key: "application_id", Value: r.applicationID},
		ae.Member{Key: "project_id", Value: r.projectID},
		ae.Member{Key: "team_id", Value: optional(r.teamID)},
		ae.Member{Key: "agent_id", Value: optional(r.agentID)},
		ae.Member{Key: "user_id", Value: optional(r.userID)},
		ae.Member{Key: "task_id", Value: r.taskID},
		ae.Member{Key: "status", Value: r.status},
		ae.Member{Key: "reuse_confidence", Value: json.Number(reuse)},
		ae.Member{Key: "supporting_validations", Value: json.Number(strconv.FormatInt(r.supportingValidations, 10))},
		ae.Member{Key: "contradictions", Value: json.Number(strconv.FormatInt(r.contradictions, 10))},
		ae.Member{Key: "revision", Value: json.Number(strconv.FormatInt(r.revision, 10))},
		ae.Member{Key: "created_at", Value: createdAt.String()},
		ae.Member{Key: "updated_at", Value: updatedAt.String()},
		ae.Member{Key: "payload_version", Value: json.Number(strconv.FormatInt(r.payloadVersion, 10))},
	)
	payload, err := ae.DecodeJSON(r.payload)
	if err != nil {
		return ae.ExperienceRecord{}, fmt.Errorf("the payload is not JSON: %w", err)
	}
	return ae.FromColumns(columns, payload)
}
