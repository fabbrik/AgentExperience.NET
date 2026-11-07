//go:build postgres

package pgtest_test

// Story 27.4's cross-language proof of the retrieval policy, the Go half (contract 1.12). The .NET contract tests insert
// a seed as a search seed is inserted, retrieve it through the .NET PostgreSQL candidate source under a non-default
// policy (an expiry and per-domain confidence decay) and check in the seed, the request and what .NET answered. Here
// the same seed goes into a database through the Go store, the Go store searches it with the policy's parameters, and
// the Go ranking must answer the same ordered IDs, exclusions and scores (within 1e-12).

import (
	"context"
	"path/filepath"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

var dotnetRetrievalPolicy = filepath.Join(ct.Repository, "tests", "AgentExperience.Contract.Tests", "cross-language",
	"dotnet-retrieval-policy.json")

func TestTheSamePolicyRanksADotNetSeedTheSameInGo(t *testing.T) {
	admin := requireServer(t)
	ctx := context.Background()
	file := ct.MustReadJSON(dotnetRetrievalPolicy).(*ae.Object)
	request := ct.RetrievalRequestOf(file)
	seed, _ := file.Get("seed")
	expected, _ := file.Get("expected")

	store := newStore(t, admin, createDatabase(t, admin, "xl_retrieval_policy"))
	for _, stored := range seed.([]any) {
		record, err := ae.FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		if err := store.InsertRecord(ctx, ae.BoundsOf(record.Scope), &record); err != nil {
			t.Fatal(err)
		}
	}

	parameters, err := request.Policy.SearchParameters(request.Scope, request.TaskText, request.ExcludeModelAuthored)
	if err != nil {
		t.Fatal(err)
	}
	rows, err := store.Search(ctx, ae.BoundsOf(request.Scope), parameters)
	if err != nil {
		t.Fatal(err)
	}
	result, err := ae.RankWith(rows, request.Scope, request.Now, request.Preferred, ae.RankOptions{
		ExcludeModelAuthored: request.ExcludeModelAuthored, Policy: request.Policy, Decay: request.Decay})
	if err != nil {
		t.Fatal(err)
	}
	if difference := ct.RankingDifference(expected.(*ae.Object), ae.RetrievalCompleted, result); difference != "" {
		t.Fatal(difference)
	}
}
