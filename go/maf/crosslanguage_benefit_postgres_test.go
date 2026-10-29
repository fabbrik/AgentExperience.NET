//go:build postgres

package maf

// Story 27.2's cross-language proof of the benefit policy, the Go half (contract 1.10). The .NET contract tests write a
// fleet through the .NET PostgreSQL store, in which lesson X measures Hurts (treatment runs given it failed, holdout runs
// denied it verified) and lesson Y measures Helps, and check in its rows, every column in PostgreSQL's text form. Here
// those rows go into a database, and a Go agent with the benefit policy on is invoked twice on the task the lessons were
// learned for: the first invocation finds the cache cold and injects both, the second omits X as MeasuredHarmful, with
// .NET's detail, and injects Y.

import (
	"context"
	"path/filepath"
	"slices"
	"sync"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/microsoft/agent-framework-go/agent"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

var (
	dotnetBenefitFleetRows = filepath.Join(ct.Repository, "tests", "AgentExperience.Contract.Tests", "cross-language",
		"dotnet-benefit-fleet-rows.json")
	benefitScope = ae.Scope{TenantID: "cross-language-benefit", ApplicationID: "benefit-app",
		ProjectID: "benefit-project", AgentID: ae.Ptr("benefit-agent")}
	benefitHarmful = ae.MustParseUUID("000000b0-0000-4000-8000-000000000001")
	benefitHelpful = ae.MustParseUUID("000000b0-0000-4000-8000-000000000002")
)

func TestAGoAgentOmitsTheLessonADotNetFleetMeasuredHurting(t *testing.T) {
	admin := pgtest.Require(t)
	ctx := context.Background()
	database := createDatabase(t, admin, "go_benefit")
	store := newStore(t, admin, database)
	conn, err := pgx.Connect(ctx, admin.Admin(database))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close(ctx)
	rows, err := ct.ReadJSON(dotnetBenefitFleetRows)
	if err != nil {
		t.Fatal(err)
	}
	for _, table := range []string{"experience_records", "lifecycle_events", "experience_holdout_assignments"} {
		value, _ := rows.(*ae.Object).Get(table)
		insertRawRows(t, conn, table, value.([]any))
	}

	var mu sync.Mutex
	var injections []InjectionResult
	var failures []InjectionFailure
	now := ae.MustParseInstant("2026-03-02T10:00:00+00:00")
	config := agent.Config{Name: "benefit-agent"}
	if err := UseAgentExperience(&config, Options{
		Store:    store,
		Identity: ae.Identity{Principal: "go-principal", Scope: benefitScope},
		Clock:    func() ae.Instant { return now },
		Benefit:  ae.BenefitPolicy{Enabled: true, PreferMeasuredHelpful: true, MinimumRunsPerArm: 3},
		OnInjected: func(r InjectionResult) {
			mu.Lock()
			defer mu.Unlock()
			injections = append(injections, r)
		},
		OnInjectionFailure: func(f InjectionFailure) {
			mu.Lock()
			defer mu.Unlock()
			failures = append(failures, f)
		},
	}); err != nil {
		t.Fatal(err)
	}
	a := newAgent(newScripted(0), config)
	runText(t, a, "Rotate the signing keys")
	wait, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	if err := ContextProviderOf(&config).WaitBenefitRefreshes(wait); err != nil {
		t.Fatal(err)
	}
	runText(t, a, "Rotate the signing keys")
	if err := ContextProviderOf(&config).Close(wait); err != nil {
		t.Fatal(err)
	}

	mu.Lock()
	defer mu.Unlock()
	if len(failures) != 0 || len(injections) != 2 {
		t.Fatalf("%d injections, failures %+v", len(injections), failures)
	}
	// The cold cache changes nothing.
	if first := injections[0]; !slices.Contains(first.ExperienceIDs, benefitHarmful) ||
		!slices.Contains(first.ExperienceIDs, benefitHelpful) {
		t.Fatalf("the first invocation injected %v", first.ExperienceIDs)
	}
	second := injections[1]
	if !slices.Equal(second.ExperienceIDs, []ae.UUID{benefitHelpful}) {
		t.Fatalf("the second invocation injected %v", second.ExperienceIDs)
	}
	if len(second.Omitted) != 1 || second.Omitted[0].ExperienceID != benefitHarmful ||
		second.Omitted[0].Reason != ae.OmittedMeasuredHarmful || second.Omitted[0].Detail == nil ||
		*second.Omitted[0].Detail != ae.MeasuredHarmfulDetail {
		t.Fatalf("omitted %+v", second.Omitted)
	}
}
