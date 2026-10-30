package agentexperience_test

// Contract 1.12: the retrieval-policy fixtures and the policy's own rules.

import (
	"errors"
	"math"
	"os"
	"reflect"
	"testing"
	"time"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestRetrievalPolicy(t *testing.T) {
	cases := ct.Cases(ct.RetrievalPolicyFamily)
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	for _, name := range cases {
		t.Run(name, func(t *testing.T) {
			request := ct.RetrievalRequestOf(ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, name, "policy.json")))
			candidates := ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, name, "candidates.json"))
			rows, err := ct.CandidatesOf(candidates)
			if err != nil {
				t.Fatal(err)
			}
			expected := ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, name, "expected.json")).(*ae.Object)

			// What the store is asked for, and what it answers: at most that limit, as the statement's LIMIT cuts.
			parameters, err := request.Policy.SearchParameters(request.Scope, request.TaskText, request.ExcludeModelAuthored)
			if err != nil {
				t.Fatal(err)
			}
			values, err := parameters.Values()
			if err != nil {
				t.Fatal(err)
			}
			query, _ := expected.Get("query")
			q := query.(*ae.Object)
			get := func(key string) any { value, _ := q.Get(key); return value }
			if values["min_confidence"] != ct.Number(get("minConfidence")) ||
				int64(values["limit"].(int32)) != int64(ct.Number(get("limit"))) ||
				int64(values["minimum_matched_terms"].(int32)) != int64(ct.Number(get("minimumMatchedTerms"))) ||
				values["exclude_model_authored"] != get("excludeModelAuthored") {
				t.Fatalf("the search parameters are %v, expected %v", values, query)
			}
			if limit := int(values["limit"].(int32)); len(rows) > limit {
				rows = rows[:limit]
			}

			options := ae.RankOptions{ExcludeModelAuthored: request.ExcludeModelAuthored, Policy: request.Policy,
				Decay: request.Decay}
			var result ae.RankedResult
			if _, err := os.Stat(fixture(ct.RetrievalPolicyFamily, name, "vector-candidates.json")); err == nil {
				// Hybrid: what the index is asked for, and its answer, at most that limit.
				vectorRows, err := ct.CandidatesOf(ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, name, "vector-candidates.json")))
				if err != nil {
					t.Fatal(err)
				}
				vectorQuery, err := request.Policy.VectorQuery(request.Scope, "fixture-model", []float32{1},
					request.ExcludeModelAuthored)
				if err != nil {
					t.Fatal(err)
				}
				wantValue, _ := expected.Get("vectorQuery")
				want := wantValue.(*ae.Object)
				wantGet := func(key string) any { value, _ := want.Get(key); return value }
				if vectorQuery.Validate() != nil || vectorQuery.EffectiveMinConfidence() != ct.Number(wantGet("minConfidence")) ||
					vectorQuery.EffectiveLimit() != int(ct.Number(wantGet("limit"))) ||
					vectorQuery.ExcludeModelAuthored != wantGet("excludeModelAuthored") {
					t.Fatalf("the vector query is %+v, expected %v", vectorQuery, want)
				}
				if len(vectorRows) > vectorQuery.EffectiveLimit() {
					vectorRows = vectorRows[:vectorQuery.EffectiveLimit()]
				}
				result, err = ae.RankHybridWith(rows, vectorRows, request.Scope, request.Now, request.Preferred, options)
				if err != nil {
					t.Fatal(err)
				}
			} else {
				if result, err = ae.RankWith(rows, request.Scope, request.Now, request.Preferred, options); err != nil {
					t.Fatal(err)
				}
			}
			if difference := ct.RankingDifference(expected, ae.RetrievalCompleted, result); difference != "" {
				t.Fatal(difference)
			}

			// The default policy ranks exactly as contract 1.0's Rank does.
			if name == "default-policy" {
				if !reflect.DeepEqual(request.Policy, ae.RetrievalPolicy{MinimumConfidence: request.Policy.MinimumConfidence,
					Timeout: ae.DefaultRetrievalTimeout, RecencyHalfLife: ae.DefaultRecencyHalfLife,
					CandidateLimit: ae.CandidateLimit, MaxAbandonedSearches: ae.DefaultMaxAbandonedSearches,
					MinimumMatchedTerms: ae.DefaultMinimumMatchedTerms}) || *request.Policy.MinimumConfidence != ae.DefaultMinConfidence ||
					request.Decay != nil {
					t.Fatalf("default-policy's policy is %+v", request.Policy)
				}
				plain, err := ae.Rank(rows, request.Scope, request.Now, request.Preferred, request.ExcludeModelAuthored)
				if err != nil || !reflect.DeepEqual(plain, result) {
					t.Fatalf("Rank differs from the default policy: %v", err)
				}
			}
		})
	}
}

// goZeroDefaults are the options a zero means the default (or no expiry) for in Go, where .NET refuses a zero.
var goZeroDefaults = map[string]bool{"Timeout": true, "RecencyHalfLife": true, "CandidateLimit": true,
	"MaxAbandonedSearches": true, "MinimumMatchedTerms": true, "MaxAge": true, "DomainKey": true}

func TestRetrievalPolicyValidation(t *testing.T) {
	entries := ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, "policy-validation.json")).([]any)
	refusals := 0
	for _, item := range entries {
		entry := item.(*ae.Object)
		get := func(key string) any { value, _ := entry.Get(key); return value }
		description := get("description").(string)
		policy := ct.PolicyOf(get("policy"))
		decay := ct.DecayOf(get("confidenceDecay"))
		err := policy.Validate()
		if err == nil && decay != nil {
			err = decay.Validate()
		}
		refusal, _ := get("refusal").(*ae.Object)
		if refusal == nil {
			if err != nil {
				t.Errorf("%s: refused: %v", description, err)
			}
			continue
		}
		parameter, _ := refusal.Get("parameter")
		if err == nil && goZeroDefaults[parameter.(string)] && zeroOf(policy, decay, parameter.(string)) {
			// Go's documented rule: a zero means the default.
			continue
		}
		var refused *ae.PolicyError
		if !errors.As(err, &refused) || refused.Option != parameter {
			t.Errorf("%s: %v, expected a refusal naming %v", description, err, parameter)
		}
		refusals++
	}
	if refusals == 0 {
		t.Fatal("nothing was refused")
	}
}

func zeroOf(policy ae.RetrievalPolicy, decay *ae.ConfidenceDecayPolicy, option string) bool {
	switch option {
	case "Timeout":
		return policy.Timeout == 0
	case "RecencyHalfLife":
		return policy.RecencyHalfLife == 0
	case "CandidateLimit":
		return policy.CandidateLimit == 0
	case "MaxAbandonedSearches":
		return policy.MaxAbandonedSearches == 0
	case "MinimumMatchedTerms":
		return policy.MinimumMatchedTerms == 0
	case "MaxAge":
		return policy.MaxAge == 0
	case "DomainKey":
		return decay != nil && decay.DomainKey == ""
	}
	return false
}

func TestTheZeroPolicyIsTheDefault(t *testing.T) {
	resolved, err := ae.RetrievalPolicy{}.Resolved()
	if err != nil {
		t.Fatal(err)
	}
	if resolved.Timeout != 500*time.Millisecond || *resolved.MinimumConfidence != 0.5 || resolved.MaxAge != 0 ||
		resolved.RecencyHalfLife != 30*24*time.Hour || resolved.CandidateLimit != 50 || resolved.MaxAbandonedSearches != 16 ||
		resolved.MinimumMatchedTerms != 3 || resolved.MinimumVectorRelevance != nil {
		t.Fatalf("%+v", resolved)
	}
	decay, err := ae.ConfidenceDecayPolicy{}.Resolved()
	if err != nil || decay.DomainKey != "domain" {
		t.Fatal(decay, err)
	}
}

func TestADecayPolicyCopiesTheHostsMap(t *testing.T) {
	month := 30 * 24 * time.Hour
	halfLives := map[string]*time.Duration{"framework-api": &month}
	decay, err := ae.ConfidenceDecayPolicy{HalfLives: halfLives}.Resolved()
	if err != nil {
		t.Fatal(err)
	}
	month = time.Hour
	halfLives["math"] = nil
	domain := "framework-api"
	if got := decay.HalfLifeFor(&domain); got == nil || *got != 30*24*time.Hour || len(decay.HalfLives) != 1 {
		t.Fatalf("the resolved map follows the host's: %v", decay.HalfLives)
	}
	other := "Framework-API"
	if decay.HalfLifeFor(&other) != nil || decay.HalfLifeFor(nil) != nil {
		t.Fatal("domains are matched ordinally, and an unlisted one uses the (absent) default")
	}
}

func TestRankWithRefusesAnInvalidPolicy(t *testing.T) {
	nan := math.NaN()
	for _, options := range []ae.RankOptions{
		{Policy: ae.RetrievalPolicy{Timeout: -1}},
		{Policy: ae.RetrievalPolicy{MinimumConfidence: &nan}},
		{Decay: &ae.ConfidenceDecayPolicy{DomainKey: " "}},
	} {
		var refused *ae.PolicyError
		if _, err := ae.RankWith(nil, ae.Scope{}, ae.Instant{}, nil, options); !errors.As(err, &refused) {
			t.Errorf("%+v: %v", options, err)
		}
	}
	if _, err := (ae.RetrievalPolicy{CandidateLimit: 200}).SearchParameters(ae.Scope{}, "x", false); err == nil {
		t.Fatal("a candidate limit of 200 was accepted")
	}
}

func TestHybridRankingAppliesThePolicyToTheMergedCandidates(t *testing.T) {
	request := ct.RetrievalRequestOf(ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, "max-age", "policy.json")))
	rows, err := ct.CandidatesOf(ct.MustReadJSON(fixture(ct.RetrievalPolicyFamily, "max-age", "candidates.json")))
	if err != nil {
		t.Fatal(err)
	}
	options := ae.RankOptions{Policy: request.Policy}
	text, vector := rows[:3], rows[3:]
	result, err := ae.RankHybridWith(text, vector, request.Scope, request.Now, request.Preferred, options)
	if err != nil {
		t.Fatal(err)
	}
	single, err := ae.RankWith(rows, request.Scope, request.Now, request.Preferred, options)
	if err != nil {
		t.Fatal(err)
	}
	// The merge keeps the text channel's order, then the vector channel's: the same candidates in the same order.
	if !reflect.DeepEqual(result, single) || len(result.Excluded) != 3 {
		t.Fatalf("hybrid %+v, single %+v", result, single)
	}

	// Each channel is cut at the candidate limit on its own, and either reaching it truncates.
	options.Policy.CandidateLimit = 2
	result, err = ae.RankHybridWith(text, vector, request.Scope, request.Now, request.Preferred, options)
	if err != nil {
		t.Fatal(err)
	}
	// Each channel keeps 2 (4 merged, one Expired), and the ranked result is cut to the candidate limit.
	if !result.Truncated || len(result.Excluded) != 1 || len(result.Ranked) != 2 {
		t.Fatalf("%+v", result)
	}
}

func TestTheSearchAndVectorLimitsAreDotNetsOneTo200(t *testing.T) {
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	for limit, valid := range map[int]bool{-1: false, 0: true, 1: true, 200: true, 201: false} {
		vector := ae.VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{1}, Limit: limit}
		if (vector.Validate() == nil) != valid {
			t.Errorf("vector limit %d: %v", limit, vector.Validate())
		}
		text := ae.SearchParameters{Scope: scope, TaskText: "rotate the keys", Limit: limit}
		if (text.Validate() == nil) != valid {
			t.Errorf("text limit %d: %v", limit, text.Validate())
		}
	}
	query, err := ae.RetrievalPolicy{CandidateLimit: 199}.VectorQuery(scope, "m", []float32{1}, true)
	if err != nil || query.Validate() != nil || query.EffectiveLimit() != 200 || !query.ExcludeModelAuthored {
		t.Fatal(query, err)
	}
}
