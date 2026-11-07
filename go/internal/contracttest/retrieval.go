package contracttest

import (
	"encoding/json"
	"fmt"
	"math"
	"strconv"
	"time"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.12's retrieval-policy fixtures, and the cross-language file that shares their shape -----------------

// RetrievalPolicyFamily is the contract 1.12 fixture family.
const RetrievalPolicyFamily = "retrieval-policy"

// RetrievalTolerance is the absolute tolerance every ranking number is compared within.
const RetrievalTolerance = 1e-12

func ticks(value any) time.Duration { return time.Duration(integer(value) * 100) }

// Number reads a fixture number: a JSON number, or the string "NaN".
func Number(value any) float64 {
	if s, ok := value.(string); ok && s == "NaN" {
		return math.NaN()
	}
	n, _ := value.(json.Number)
	f, err := strconv.ParseFloat(string(n), 64)
	if err != nil {
		panic(fmt.Sprintf("contracttest: %v is not a number", value))
	}
	return f
}

// PolicyOf reads a fixture's "policy" as the Go policy: each duration from its ticks (a zero stays zero, which Go reads
// as the default, or no expiry), null as nil or zero. It is not validated here.
func PolicyOf(value any) ae.RetrievalPolicy {
	o := object(value)
	policy := ae.RetrievalPolicy{
		Timeout:              ticks(get(o, "timeoutTicks")),
		RecencyHalfLife:      ticks(get(o, "recencyHalfLifeTicks")),
		CandidateLimit:       int(integer(get(o, "candidateLimit"))),
		MaxAbandonedSearches: int(integer(get(o, "maxAbandonedSearches"))),
		MinimumMatchedTerms:  int(integer(get(o, "minimumMatchedTerms"))),
	}
	confidence := Number(get(o, "minimumConfidence"))
	policy.MinimumConfidence = &confidence
	if maxAge := get(o, "maxAgeTicks"); maxAge != nil {
		policy.MaxAge = ticks(maxAge)
	}
	if floor := get(o, "minimumVectorRelevance"); floor != nil {
		f := Number(floor)
		policy.MinimumVectorRelevance = &f
	}
	return policy
}

// DecayOf reads a fixture's "confidenceDecay": nil for null. It is not validated here.
func DecayOf(value any) *ae.ConfidenceDecayPolicy {
	o := object(value)
	if o == nil {
		return nil
	}
	decay := &ae.ConfidenceDecayPolicy{DomainKey: text(get(o, "domainKey")), HalfLives: map[string]*time.Duration{}}
	for _, m := range object(get(o, "halfLivesTicks")).Members() {
		if m.Value == nil {
			decay.HalfLives[m.Key] = nil
			continue
		}
		halfLife := ticks(m.Value)
		decay.HalfLives[m.Key] = &halfLife
	}
	if fallback := get(o, "defaultHalfLifeTicks"); fallback != nil {
		halfLife := ticks(fallback)
		decay.DefaultHalfLife = &halfLife
	}
	return decay
}

// PreferredOf reads a fixture's "preferredEnvironment".
func PreferredOf(value any) map[string]string {
	preferred := map[string]string{}
	for _, m := range object(value).Members() {
		preferred[m.Key] = text(m.Value)
	}
	return preferred
}

// RetrievalRequest is a retrieval-policy case's policy.json (or the cross-language file's request).
type RetrievalRequest struct {
	Scope                ae.Scope
	TaskText             string
	Now                  ae.Instant
	Preferred            map[string]string
	ExcludeModelAuthored bool
	Policy               ae.RetrievalPolicy
	Decay                *ae.ConfidenceDecayPolicy
}

// RetrievalRequestOf reads a policy.json (or the cross-language file).
func RetrievalRequestOf(value any) RetrievalRequest {
	o := object(value)
	exclude, _ := get(o, "excludeModelAuthored").(bool)
	return RetrievalRequest{
		Scope:                ScopeOf(get(o, "scope")),
		TaskText:             text(get(o, "taskText")),
		Now:                  must(ae.ParseInstant(text(get(o, "now")))),
		Preferred:            PreferredOf(get(o, "preferredEnvironment")),
		ExcludeModelAuthored: exclude,
		Policy:               PolicyOf(get(o, "policy")),
		Decay:                DecayOf(get(o, "confidenceDecay")),
	}
}

// CandidatesOf reads candidates.json as search rows, in the store's order.
func CandidatesOf(value any) ([]ae.SearchRow, error) {
	var rows []ae.SearchRow
	for _, item := range list(value) {
		o := object(item)
		record, err := ae.FromStored(get(o, "record"))
		if err != nil {
			return nil, err
		}
		rows = append(rows, ae.SearchRow{Record: record, Relevance: Number(get(o, "relevance"))})
	}
	return rows, nil
}

func within(expected any, actual float64) bool {
	if expected == nil {
		return false
	}
	want := Number(expected)
	return math.Abs(want-actual) <= RetrievalTolerance
}

// RankingDifference compares a ranked result with an expected.json's outcome, truncated, ranked and excluded:
// IDs, order, kinds and reasons exactly, every number within RetrievalTolerance. It is "" when they agree.
func RankingDifference(expected *ae.Object, outcome ae.RetrievalOutcome, result ae.RankedResult) string {
	if want := text(get(expected, "outcome")); want != string(outcome) {
		return fmt.Sprintf("outcome %s, expected %s", outcome, want)
	}
	if truncated, _ := get(expected, "truncated").(bool); truncated != result.Truncated {
		return fmt.Sprintf("truncated %v, expected %v", result.Truncated, truncated)
	}
	ranked := list(get(expected, "ranked"))
	if len(ranked) != len(result.Ranked) {
		return fmt.Sprintf("%d ranked, expected %d", len(result.Ranked), len(ranked))
	}
	for i, item := range ranked {
		e, actual := object(item), result.Ranked[i]
		if id := text(get(e, "experienceId")); id != actual.Record.ExperienceID.String() {
			return fmt.Sprintf("ranked[%d] is %s, expected %s", i, actual.Record.ExperienceID, id)
		}
		if !within(get(e, "score"), actual.Score) {
			return fmt.Sprintf("ranked[%d] scores %v, expected %v", i, actual.Score, get(e, "score"))
		}
		components := list(get(e, "components"))
		if len(components) != len(actual.Components) {
			return fmt.Sprintf("ranked[%d] has %d components, expected %d", i, len(actual.Components), len(components))
		}
		for j, value := range components {
			c, component := object(value), actual.Components[j]
			where := fmt.Sprintf("ranked[%d].components[%d]", i, j)
			if text(get(c, "kind")) != string(component.Kind) {
				return fmt.Sprintf("%s is %s, expected %v", where, component.Kind, get(c, "kind"))
			}
			if !within(get(c, "value"), component.Value) || !within(get(c, "weight"), component.Weight) {
				return fmt.Sprintf("%s is %v × %v, expected %v × %v", where, component.Value, component.Weight,
					get(c, "value"), get(c, "weight"))
			}
			undecayed := get(c, "undecayedValue")
			switch {
			case undecayed == nil && component.UndecayedValue != nil:
				return fmt.Sprintf("%s has undecayedValue %v, expected none", where, *component.UndecayedValue)
			case undecayed != nil && (component.UndecayedValue == nil || !within(undecayed, *component.UndecayedValue)):
				return fmt.Sprintf("%s has undecayedValue %v, expected %v", where, component.UndecayedValue, undecayed)
			}
		}
	}
	excluded := list(get(expected, "excluded"))
	if len(excluded) != len(result.Excluded) {
		return fmt.Sprintf("%d excluded (%v), expected %d", len(result.Excluded), result.Excluded, len(excluded))
	}
	for i, item := range excluded {
		e, actual := object(item), result.Excluded[i]
		if text(get(e, "experienceId")) != actual.ExperienceID.String() || text(get(e, "reason")) != string(actual.Reason) {
			return fmt.Sprintf("excluded[%d] is %s %s, expected %v %v", i, actual.ExperienceID, actual.Reason,
				get(e, "experienceId"), get(e, "reason"))
		}
	}
	return ""
}
