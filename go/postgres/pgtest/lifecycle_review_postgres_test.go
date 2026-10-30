//go:build postgres

package pgtest_test

// The lifecycle commit against what other writers do meanwhile: a seal landing while a commit waits for the row, a
// sealed row, the host's own transaction's isolation, and a commit cancelled while it waits.

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
)

// seededRecord is the compare-and-set case's first record (Validated, revision 1), inserted through store.
func seededRecord(t *testing.T, store *postgres.Store) ae.ExperienceRecord {
	t.Helper()
	seed := ct.MustReadJSON(fixture("lifecycle-sql", "compare-and-set", "seed.json")).(*ae.Object)
	records, _ := seed.Get("records")
	record, err := ae.FromStored(records.([]any)[0])
	if err != nil {
		t.Fatal(err)
	}
	if err := store.InsertRecord(context.Background(), ae.BoundsOf(record.Scope), &record); err != nil {
		t.Fatal(err)
	}
	return record
}

// waitForLockWaiter returns once a backend of database waits on a lock: the commit under test is blocked.
func waitForLockWaiter(t *testing.T, admin server, database string) {
	t.Helper()
	ctx := context.Background()
	conn, err := connect(ctx, admin.Admin("postgres"))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	deadline := time.Now().Add(2 * time.Minute)
	for time.Now().Before(deadline) {
		var waiting int
		if err := conn.QueryRow(ctx, "SELECT count(*) FROM pg_catalog.pg_stat_activity, pg_catalog.pg_sleep(0.01) "+
			"WHERE datname = $1 AND wait_event_type = 'Lock'", database).Scan(&waiting); err != nil {
			t.Fatal(err)
		}
		if waiting > 0 {
			return
		}
	}
	t.Fatal("the commit never waited on the row lock")
}

func countRows(t *testing.T, owner *pgx.Conn, table string, id ae.UUID) int {
	t.Helper()
	var count int
	if err := owner.QueryRow(context.Background(), "SELECT count(*) FROM "+table+" WHERE experience_id = $1",
		[16]byte(id)).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count
}

func reinforce(record ae.ExperienceRecord, event byte) ae.CommitLifecycleTransitionRequest {
	prior := record.Status
	return ae.CommitLifecycleTransitionRequest{EventID: ae.UUID{0: 0x5e, 6: 0x40, 8: 0x80, 15: event},
		ExperienceID: record.ExperienceID, Scope: record.Scope, PriorStatus: &prior, CurrentStatus: ae.StatusReinforced,
		Reason: "reinforced", Producer: "review", OccurredAt: record.CreatedAt, ExpectedRevision: record.Revision}
}

// A seal that lands while the commit waits for the row: the commit, which checks for a seal only once it holds the
// row, answers NotFound and writes nothing.
func TestASealLandingWhileACommitWaitsMakesItNotFound(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_seal_race")
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	record := seededRecord(t, store)
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	if err != nil {
		t.Fatal(err)
	}

	seal, err := owner.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = seal.Rollback(context.Background()) }()
	scope := record.Scope
	var sealed string
	if err := seal.QueryRow(ctx, "SELECT agent_experience.seal_experience_record($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb)",
		[16]byte(record.ExperienceID), scope.TenantID, scope.ApplicationID, scope.ProjectID, scope.TeamID, scope.AgentID,
		scope.UserID, record.Revision, `{"sealed": "aexp-sealed:v1:AAAA"}`).Scan(&sealed); err != nil || sealed != "Sealed" {
		t.Fatalf("%s %v", sealed, err)
	}

	type outcome struct {
		result ae.CommitLifecycleTransitionResult
		err    error
	}
	done := make(chan outcome, 1)
	go func() {
		result, err := service.Commit(ctx, ae.AuthorizationContext{PrincipalID: "review", Bounds: ae.BoundsOf(scope)},
			reinforce(record, 1))
		done <- outcome{result, err}
	}()
	waitForLockWaiter(t, admin, database)
	if err := seal.Commit(ctx); err != nil {
		t.Fatal(err)
	}
	got := <-done
	if got.err != nil || got.result.Outcome != ae.TransitionNotFound {
		t.Fatalf("%+v %v", got.result, got.err)
	}
	var revision, version int64
	var status string
	if err := owner.QueryRow(ctx, "SELECT revision, status, payload_version FROM agent_experience.experience_records "+
		"WHERE experience_id = $1", [16]byte(record.ExperienceID)).Scan(&revision, &status, &version); err != nil {
		t.Fatal(err)
	}
	if revision != record.Revision || status != string(record.Status) || version != 2 ||
		countRows(t, owner, "agent_experience.lifecycle_events", record.ExperienceID) != 0 {
		t.Fatalf("revision %d status %s version %d", revision, status, version)
	}
}

// A sealed row (payload_version 2), which a port cannot open, is NotFound to the read, the history and the commit.
func TestASealedRowIsNotFoundToTheLifecycle(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_sealed")
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	record := seededRecord(t, store)
	scope := record.Scope
	sealedID := ae.MustParseUUID("0000005f-0000-4000-8000-0000000000a1")
	if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, "+
		"tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, "+
		"supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, "+
		"search_vector_sealed) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, '(sealed)', 'Validated', 0.6666666666666666, 1, "+
		"0, 1, now(), now(), 2, $9::jsonb, to_tsvector('english', 'sealed'))",
		[16]byte(sealedID), [16]byte(ae.MustParseUUID("0000005f-0000-4000-8000-0000000000a2")), scope.TenantID,
		scope.ApplicationID, scope.ProjectID, scope.TeamID, scope.AgentID, scope.UserID,
		`{"sealed": "aexp-sealed:v1:AAAA"}`); err != nil {
		t.Fatal(err)
	}
	authorization := ae.AuthorizationContext{PrincipalID: "review", Bounds: ae.BoundsOf(scope)}
	read, err := store.ReadRecord(ctx, authorization, scope, sealedID)
	if err != nil || read.Outcome != ae.StoreNotFound || read.Record != nil {
		t.Fatalf("read %+v %v", read, err)
	}
	history, err := store.GetHistory(ctx, authorization, ae.HistoryQuery{Scope: scope, ExperienceID: sealedID, Limit: 10})
	if err != nil || history.Outcome != ae.StoreNotFound {
		t.Fatalf("history %+v %v", history, err)
	}
	sealed := record
	sealed.ExperienceID = sealedID
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
	if err != nil {
		t.Fatal(err)
	}
	committed, err := service.Commit(ctx, authorization, reinforce(sealed, 2))
	if err != nil || committed.Outcome != ae.TransitionNotFound {
		t.Fatalf("commit %+v %v", committed, err)
	}
	if countRows(t, owner, "agent_experience.lifecycle_events", sealedID) != 0 {
		t.Fatal("an event was written against the sealed row")
	}
}

// Given the host's own transaction, a commit runs only at READ COMMITTED: under a snapshot isolation its re-read of a
// lost race would never see the revision move.
func TestACommitInTheHostsTransactionNeedsReadCommitted(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_host_tx")
	record := seededRecord(t, newStore(t, admin, database))
	authorization := ae.AuthorizationContext{PrincipalID: "review", Bounds: ae.BoundsOf(record.Scope)}
	for _, level := range []pgx.TxIsoLevel{pgx.ReadCommitted, pgx.RepeatableRead} {
		t.Run(string(level), func(t *testing.T) {
			conn, err := connect(ctx, admin.Application(database))
			if err != nil {
				t.Fatal(err)
			}
			defer conn.Close(ctx)
			host, err := conn.BeginTx(ctx, pgx.TxOptions{IsoLevel: level})
			if err != nil {
				t.Fatal(err)
			}
			defer func() { _ = host.Rollback(context.Background()) }()
			store, err := postgres.NewStore(host)
			if err != nil {
				t.Fatal(err)
			}
			service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{})
			if err != nil {
				t.Fatal(err)
			}
			result, err := service.Commit(ctx, authorization, reinforce(record, 3))
			if level == pgx.ReadCommitted {
				if err != nil || result.Outcome != ae.TransitionCommitted {
					t.Fatalf("%+v %v", result, err)
				}
				return
			}
			if !errors.Is(err, postgres.ErrHostIsolation) {
				t.Fatalf("%+v %v", result, err)
			}
		})
	}
}

// A commit cancelled while it waits for a row another connection holds is rolled back whole: no record change, no
// event, no evidence.
func TestACommitCancelledWhileItWaitsWritesNothing(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_cancel")
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	record := seededRecord(t, store)
	service, err := ae.NewLifecycleService(store, ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted})
	if err != nil {
		t.Fatal(err)
	}

	holder, err := owner.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = holder.Rollback(context.Background()) }()
	if _, err := holder.Exec(ctx, "SELECT 1 FROM agent_experience.experience_records WHERE experience_id = $1 FOR UPDATE",
		[16]byte(record.ExperienceID)); err != nil {
		t.Fatal(err)
	}

	commitCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	done := make(chan error, 1)
	round := ae.UUID{0: 0x5b, 6: 0x40, 8: 0x80, 15: 1}
	go func() {
		_, err := service.ApplyEvidence(commitCtx, ae.AuthorizationContext{PrincipalID: "aggregator",
			Bounds: ae.BoundsOf(record.Scope)}, ae.ApplyConfidenceEvidenceRequest{
			EventID: ae.UUID{0: 0x5c, 6: 0x40, 8: 0x80, 15: 1}, ExperienceID: record.ExperienceID, Scope: record.Scope,
			EvidenceID: ae.UUID{0: 0x5d, 6: 0x40, 8: 0x80, 15: 1}, Kind: ae.EvidenceSupporting,
			Source: ae.EvidenceMachine, RunID: ae.UUID{0: 0x5a, 6: 0x40, 8: 0x80, 15: 1}, VerificationRoundID: &round,
			Reason: "reused", Producer: "aggregator", OccurredAt: record.CreatedAt})
		done <- err
	}()
	waitForLockWaiter(t, admin, database)
	cancel()
	if err := <-done; err == nil {
		t.Fatal("the cancelled commit reported success")
	}
	if err := holder.Rollback(ctx); err != nil {
		t.Fatal(err)
	}
	var revision, supporting int64
	if err := owner.QueryRow(ctx, "SELECT revision, supporting_validations FROM agent_experience.experience_records "+
		"WHERE experience_id = $1", [16]byte(record.ExperienceID)).Scan(&revision, &supporting); err != nil {
		t.Fatal(err)
	}
	if revision != record.Revision || supporting != record.SupportingValidations ||
		countRows(t, owner, "agent_experience.lifecycle_events", record.ExperienceID) != 0 ||
		countRows(t, owner, "agent_experience.confidence_evidence", record.ExperienceID) != 0 {
		t.Fatalf("revision %d supporting %d", revision, supporting)
	}
}

// Contract 1.18's scoped supersession lock. Tenant B's store runs as the table owner, whom row-level security does not
// filter, so only the statement's own scope predicate keeps it off tenant A's row. Another transaction holds A's row
// FOR UPDATE throughout, and B's connections give up on any lock they would wait for (lock_timeout): a lock on A's row
// would fail with 55P03 instead of answering. No clock is read and nothing sleeps.
func TestASupersessionNamingAnotherTenantsRecordTakesNoLockOnIt(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "lifecycle_scoped_lock")
	victim := seededRecord(t, newStore(t, admin, database))

	config, err := pgxpool.ParseConfig(admin.Owner(database))
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["lock_timeout"] = "10000"
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	storeB, err := postgres.NewStore(pool)
	if err != nil {
		t.Fatal(err)
	}
	recordB := victim
	recordB.ExperienceID = ae.UUID{0: 0x72, 6: 0x40, 8: 0x80, 15: 1}
	recordB.SourceRunID = ae.UUID{0: 0x72, 6: 0x40, 8: 0x80, 15: 2}
	recordB.Scope.TenantID = "other-tenant"
	if err := storeB.InsertRecord(ctx, ae.BoundsOf(recordB.Scope), &recordB); err != nil {
		t.Fatal(err)
	}

	owner := ownerConn(t, admin, database)
	holder, err := owner.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = holder.Rollback(context.Background()) }()
	var locked [16]byte
	if err := holder.QueryRow(ctx, "SELECT experience_id FROM agent_experience.experience_records "+
		"WHERE experience_id = $1 FOR UPDATE", [16]byte(victim.ExperienceID)).Scan(&locked); err != nil ||
		ae.UUID(locked) != victim.ExperienceID {
		t.Fatalf("%v %v", locked, err)
	}

	prior := recordB.Status
	replacement := victim.ExperienceID
	result, err := storeB.CommitLifecycleEvent(ctx, ae.AuthorizationContext{PrincipalID: "tenant-b",
		Bounds: ae.BoundsOf(recordB.Scope)}, recordB.Scope, ae.LifecycleEvent{
		EventID: ae.UUID{0: 0x72, 6: 0x40, 8: 0x80, 15: 3}, ExperienceRecordID: recordB.ExperienceID,
		PriorStatus: &prior, CurrentStatus: ae.StatusSuperseded, Reason: "superseded by another tenant's record",
		Producer: "scoped-lock/1.0", OccurredAt: recordB.CreatedAt, ExpectedRevision: recordB.Revision,
		ReplacementExperienceID: &replacement})
	// The outcome is unchanged: another tenant's record reads as not found, and nothing is written.
	if err != nil || result.Outcome != ae.StoreReplacementNotAllowed || result.CurrentStatus != nil {
		t.Fatalf("%+v %v", result, err)
	}
	if err := holder.Rollback(ctx); err != nil {
		t.Fatal(err)
	}
	if countRows(t, owner, "agent_experience.lifecycle_events", recordB.ExperienceID) != 0 {
		t.Fatal("an event was written")
	}
}
