package inmemory

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

var (
	scope = ae.Scope{TenantID: "acme", ApplicationID: "support", ProjectID: "refunds", AgentID: ae.Ptr("agent")}
	at    = ae.MustParseInstant("2026-03-02T09:00:00+00:00")
)

func record(n byte, taskID, summary, lesson string, status ae.ExperienceStatus, confidence float64) ae.ExperienceRecord {
	return ae.ExperienceRecord{
		ExperienceID: ae.UUID{0: 0xae, 6: 0x40, 8: 0x80, 15: n},
		SourceRunID:  ae.UUID{0: 0x0e, 6: 0x40, 8: 0x80, 15: n},
		Scope:        scope,
		TaskID:       taskID,
		TaskSummary:  ae.Ptr(summary),
		Outcome:      ae.Outcome{Status: ae.VerificationVerified, EvaluatedAt: at},
		Reflection: &ae.Reflection{ReflectionID: ae.UUID{0: 0x3e, 6: 0x40, 8: 0x80, 15: n}, Lesson: lesson,
			ExperienceRunID: ae.UUID{0: 0x0e, 6: 0x40, 8: 0x80, 15: n}, VerificationStatus: ae.VerificationVerified,
			VerificationRuleVersion: ae.VerificationRuleVersion, Producer: "test", CreatedAt: at},
		Environment: ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "go", OperatingSystem: "linux",
			Metadata: map[string]string{"region": "eu"}},
		Provenance:      ae.Provenance{Source: "test", RecordedAt: at},
		Status:          status,
		ReuseConfidence: confidence,
		Revision:        1,
		CreatedAt:       at,
		UpdatedAt:       at,
	}
}

func TestQueryTermsAreNormalizedLowerCasedWordsLessStopwordsAndSingleCharacters(t *testing.T) {
	for text, expected := range map[string][]string{
		"Ticket #4977: a refund is stuck on a lock again. Triage it.": {"ticket", "4977", "refund", "stuck", "lock",
			"triage"},
		"Deploy service X to prod": {"deploy", "service", "prod"},
		"ＦＵＬＬＷＩＤＴＨ ｆｉｌｅ ﬁle":       {"fullwidth", "file"},
		"café café CAFÉ":          {"café"},
		"the and of":               nil,
		"run_tests e2e":            {"run", "tests", "e2e"},
	} {
		if actual := QueryTerms(text); !reflect.DeepEqual(actual, expected) {
			t.Errorf("QueryTerms(%q) = %q, want %q", text, actual, expected)
		}
	}
}

func TestSearchFindsEligibleRecordsInExactlyTheScopeByRelevance(t *testing.T) {
	ctx := context.Background()
	bounds := ae.BoundsOf(scope)
	refund := record(1, "triage-ticket", "Ticket #4812: a refund is stuck on a lock. Triage it.",
		"Verified after 1 attempt.", ae.StatusValidated, 0.67)
	deploy := record(2, "deploy-service", "Deploy service X to prod", "Deploy after the lock", ae.StatusValidated, 0.9)
	quarantined := record(3, "triage-ticket", "Ticket #1: a refund is stuck on a lock. Triage it.", "",
		ae.StatusQuarantined, 0.9)
	unconfident := record(4, "triage-ticket", "Ticket #2: a refund is stuck on a lock. Triage it.", "",
		ae.StatusReinforced, 0.2)
	elsewhere := record(5, "triage-ticket", "Ticket #3: a refund is stuck on a lock. Triage it.", "",
		ae.StatusValidated, 0.9)
	elsewhere.Scope.AgentID = ae.Ptr("other-agent")
	store, err := New(refund, deploy, quarantined, unconfident)
	if err != nil {
		t.Fatal(err)
	}
	otherBounds := ae.BoundsOf(elsewhere.Scope)
	if err := store.InsertRecord(ctx, otherBounds, &elsewhere); err != nil {
		t.Fatal(err)
	}

	rows, err := store.Search(ctx, bounds, ae.NewSearchParameters(scope,
		"Ticket #4977: a refund is stuck on a lock again. Triage it."))
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || rows[0].Record.ExperienceID != refund.ExperienceID {
		t.Fatalf("rows %+v", rows)
	}
	// 5 of 6 terms in the summary (3 each), "ticket" and "triage" in the task ID (1 each): 17 / 30.
	if rows[0].Relevance != 17.0/30.0 {
		t.Fatalf("relevance %v", rows[0].Relevance)
	}

	// A short query needs fewer matched terms; the bounds of another scope see nothing here.
	if rows, _ := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "lock")); len(rows) != 2 ||
		rows[0].Record.ExperienceID != refund.ExperienceID || rows[0].Relevance != 0.6 ||
		rows[1].Record.ExperienceID != deploy.ExperienceID || rows[1].Relevance != 0.2 {
		t.Fatalf("rows %+v", rows)
	}
	if rows, _ := store.Search(ctx, otherBounds, ae.NewSearchParameters(scope, "lock")); rows != nil {
		t.Fatalf("other bounds read %+v", rows)
	}
	if _, err := store.Search(ctx, bounds, ae.NewSearchParameters(scope, " ")); err == nil {
		t.Fatal("a blank task text was searched")
	}
	if rows, _ := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "the of and")); rows != nil {
		t.Fatalf("stopwords matched %+v", rows)
	}
}

func TestInsertFinalizedHonoursTheBoundsAndTheRecordsOwnEvent(t *testing.T) {
	ctx := context.Background()
	store, _ := New()
	refund := record(1, "triage-ticket", "a refund", "", ae.StatusValidated, 0.67)
	finalized := ae.FinalizedExperience{Record: refund, Event: ae.InitialLifecycleEvent{
		ExperienceID: refund.ExperienceID, CurrentStatus: refund.Status, AppliedRevision: refund.Revision}}

	other := ae.BoundsOf(scope)
	other.AgentID = ae.Ptr("other-agent")
	if err := store.InsertFinalized(ctx, other, &finalized, "svc", nil); !errors.Is(err, ErrAuthorizationDenied) {
		t.Fatalf("outside the bounds: %v", err)
	}
	if err := store.InsertFinalized(ctx, ae.BoundsOf(scope), &finalized, " ", nil); err == nil {
		t.Fatal("a blank actor was accepted")
	}
	foreign := finalized
	foreign.Event.CurrentStatus = ae.StatusQuarantined
	if err := store.InsertFinalized(ctx, ae.BoundsOf(scope), &foreign, "svc", nil); err == nil {
		t.Fatal("an event that is not the record's own was accepted")
	}
	tenantWide := ae.AuthorizationBounds{TenantID: "acme"}
	if err := store.InsertFinalized(ctx, tenantWide, &finalized, "svc", nil); err != nil {
		t.Fatal(err)
	}
	if err := store.InsertFinalized(ctx, tenantWide, &finalized, "svc", nil); err == nil {
		t.Fatal("one record was stored twice")
	}

	read, err := store.Get(ctx, tenantWide, scope, refund.ExperienceID)
	if err != nil || read == nil || read.ExperienceID != refund.ExperienceID {
		t.Fatalf("%+v %v", read, err)
	}
	unscoped := scope
	unscoped.AgentID = nil
	if read, _ := store.Get(ctx, tenantWide, unscoped, refund.ExperienceID); read != nil {
		t.Fatal("a record was read through another scope")
	}
	if read, _ := store.Get(ctx, other, scope, refund.ExperienceID); read != nil {
		t.Fatal("a record was read outside the bounds")
	}
	if records := store.Records(); len(records) != 1 {
		t.Fatalf("records %+v", records)
	}
}

func TestTheIndexIsBoundedAsPostgreSQLsSearchColumnIs(t *testing.T) {
	long := strings.Repeat("a", MaxIndexedLength) + " needle"
	x := index(record(1, "task", long, "needle", ae.StatusValidated, 1))
	if x.summary["needle"] || x.lesson["needle"] {
		t.Fatal("text past the bound was indexed")
	}
	if _, err := New(record(1, "a", "b", "c", ae.StatusValidated, 1), record(1, "a", "b", "c", ae.StatusValidated, 1)); err == nil {
		t.Fatal("two records with one ID were accepted")
	}
}

func TestTheStoreSharesNoPointerSliceOrMapWithItsCallers(t *testing.T) {
	ctx := context.Background()
	bounds := ae.BoundsOf(scope)
	refund := record(1, "triage-ticket", "a refund is stuck on a lock", "lesson", ae.StatusValidated, 0.9)
	finalized := ae.FinalizedExperience{Record: refund, Event: ae.InitialLifecycleEvent{
		ExperienceID: refund.ExperienceID, CurrentStatus: refund.Status, AppliedRevision: refund.Revision}}
	store, _ := New()
	recordedAt := time.Date(2026, 3, 2, 9, 0, 0, 0, time.UTC)
	if err := store.InsertFinalized(ctx, bounds, &finalized, "svc", &recordedAt); err != nil {
		t.Fatal(err)
	}
	// Changing what was written changes nothing held.
	*finalized.Record.TaskSummary = "changed"
	finalized.Record.Reflection.Lesson = "changed"
	finalized.Record.Environment.Metadata["region"] = "changed"
	read, _ := store.Get(ctx, bounds, scope, refund.ExperienceID)
	if *read.TaskSummary == "changed" || read.Reflection.Lesson == "changed" || read.Environment.Metadata["region"] == "changed" {
		t.Fatal("the store shares the written record")
	}
	// Changing what was read changes nothing held either.
	*read.TaskSummary = "changed"
	read.Environment.Metadata["region"] = "changed"
	rows, _ := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "refund stuck lock"))
	again := store.Records()[0]
	if *rows[0].Record.TaskSummary == "changed" || *again.TaskSummary == "changed" ||
		again.Environment.Metadata["region"] == "changed" {
		t.Fatal("the store shares a read record")
	}
	held, ok := store.Event(refund.ExperienceID)
	if !ok || held.Actor != "svc" || !held.RecordedAt.Equal(recordedAt) || held.Event.ExperienceID != refund.ExperienceID {
		t.Fatalf("%+v %v", held, ok)
	}
	if _, ok := store.Event(ae.UUID{1}); ok {
		t.Fatal("an event for no record")
	}
}

func TestTheStoreHonoursADoneContext(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	store, _ := New()
	refund := record(1, "t", "a refund", "", ae.StatusValidated, 0.9)
	finalized := ae.FinalizedExperience{Record: refund, Event: ae.InitialLifecycleEvent{
		ExperienceID: refund.ExperienceID, CurrentStatus: refund.Status, AppliedRevision: refund.Revision}}
	if err := store.InsertFinalized(ctx, ae.BoundsOf(scope), &finalized, "svc", nil); !errors.Is(err, context.Canceled) {
		t.Fatalf("insert: %v", err)
	}
	if err := store.InsertRecord(ctx, ae.BoundsOf(scope), &refund); !errors.Is(err, context.Canceled) {
		t.Fatalf("insert record: %v", err)
	}
	if _, err := store.Search(ctx, ae.BoundsOf(scope), ae.NewSearchParameters(scope, "refund")); !errors.Is(err, context.Canceled) {
		t.Fatalf("search: %v", err)
	}
	if len(store.Records()) != 0 {
		t.Fatal("a record was written on a done context")
	}
}

// The words are .NET's (checked against the .NET in-memory store's SearchText.QueryTerms on 20,000 random strings):
// the dotted capital I keeps its case, and a term's length is counted in characters (runes), so one character
// outside the Basic Multilingual Plane is too short.
func TestQueryTermsLowerAndCountAsDotNetDoes(t *testing.T) {
	for text, expected := range map[string][]string{
		"İx ẞ":      {"İx"},
		"𐐀 𐐀𐐀":      {"𐐨𐐨"},
		"Ǆthe Σ":    {"džthe"},
		"ⅫX ﬁ ﬀ ²³": {"xiix", "fi", "ff", "23"},
	} {
		if actual := QueryTerms(text); !reflect.DeepEqual(actual, expected) {
			t.Errorf("QueryTerms(%q) = %q, want %q", text, actual, expected)
		}
	}
}
