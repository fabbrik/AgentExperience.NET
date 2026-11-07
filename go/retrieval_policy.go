package agentexperience

import (
	"errors"
	"math"
	"sort"
	"strconv"
	"time"
)

// The retrieval policy (contract 1.12): .NET's RetrievalPolicy and ConfidenceDecayPolicy, with their defaults and
// validation, and the ranking and filtering they drive.

// Retrieval policy defaults and bounds.
const (
	// DefaultRetrievalTimeout is RetrievalPolicy.Timeout's default.
	DefaultRetrievalTimeout = 500 * time.Millisecond
	// MaxRetrievalTimeout is the largest RetrievalPolicy.Timeout: one day.
	MaxRetrievalTimeout = 24 * time.Hour
	// DefaultRecencyHalfLife is RetrievalPolicy.RecencyHalfLife's default: 30 days.
	DefaultRecencyHalfLife = 30 * 24 * time.Hour
	// MaxCandidateLimit is the largest RetrievalPolicy.CandidateLimit: the store is asked for one more, at most
	// MaxStatementLimit.
	MaxCandidateLimit = MaxStatementLimit - 1
	// MaxStatementLimit is the largest limit a search may be asked for (.NET's ExperienceCandidateQuery.MaxLimit).
	MaxStatementLimit = 200
	// DefaultMaxAbandonedSearches is RetrievalPolicy.MaxAbandonedSearches's default.
	DefaultMaxAbandonedSearches = 16
	// DefaultDomainKey is ConfidenceDecayPolicy.DomainKey's default.
	DefaultDomainKey = "domain"
)

// RetrievalPolicy is .NET's RetrievalPolicy: the bounds and thresholds a retrieval runs under. Go cannot tell an unset
// value from an explicit zero, so a zero Timeout, RecencyHalfLife, CandidateLimit, MaxAbandonedSearches or
// MinimumMatchedTerms means the default and a zero MaxAge means no expiry; a negative value, and every other value
// .NET refuses, is refused (Validate). The zero RetrievalPolicy is .NET's RetrievalPolicy.Default.
type RetrievalPolicy struct {
	// Timeout bounds the whole retrieval (DefaultRetrievalTimeout when 0; at most MaxRetrievalTimeout).
	Timeout time.Duration
	// MinimumConfidence is the search's confidence floor, on stored confidence (DefaultMinConfidence when nil; 0 to 1).
	MinimumConfidence *float64
	// MaxAge excludes a record whose last lifecycle activity (UpdatedAt) is more than this long ago; 0: no expiry.
	MaxAge time.Duration
	// RecencyHalfLife is the age (from UpdatedAt) at which the recency component halves (DefaultRecencyHalfLife when 0).
	RecencyHalfLife time.Duration
	// CandidateLimit is how many candidates each channel's ranking considers; the store is asked for one more, to
	// detect truncation (CandidateLimit when 0; 1 to MaxCandidateLimit).
	CandidateLimit int
	// MaxAbandonedSearches caps the abandoned searches still running per retriever (DefaultMaxAbandonedSearches when 0).
	MaxAbandonedSearches int
	// MinimumMatchedTerms is the search's minimum of matched terms (DefaultMinimumMatchedTerms when 0).
	MinimumMatchedTerms int
	// MinimumVectorRelevance is contract 1.6's vector floor, 0 to 1; nil for none.
	MinimumVectorRelevance *float64
}

// PolicyError is a retrieval or decay policy value .NET refuses. Option is the .NET option's name (Timeout,
// MinimumConfidence, MaxAge, RecencyHalfLife, CandidateLimit, MaxAbandonedSearches, MinimumMatchedTerms,
// MinimumVectorRelevance, DomainKey, HalfLives, DefaultHalfLife).
type PolicyError struct {
	Option string
	Reason string
}

func (e *PolicyError) Error() string { return "the retrieval policy's " + e.Option + " " + e.Reason }

func refuse(option, reason string) error { return &PolicyError{Option: option, Reason: reason} }

func unitInterval(value float64) bool { return value >= 0 && value <= 1 }

// Validate refuses what .NET refuses, naming the option (a *PolicyError), in .NET's order.
func (p RetrievalPolicy) Validate() error {
	switch {
	case p.Timeout < 0:
		return refuse("Timeout", "must be strictly positive")
	case p.Timeout > MaxRetrievalTimeout:
		return refuse("Timeout", "must be at most one day")
	case p.MinimumConfidence != nil && !unitInterval(*p.MinimumConfidence):
		return refuse("MinimumConfidence", "must be between 0 and 1 inclusive")
	case p.MaxAge < 0:
		return refuse("MaxAge", "must be strictly positive")
	case p.RecencyHalfLife < 0:
		return refuse("RecencyHalfLife", "must be strictly positive")
	case p.CandidateLimit < 0 || p.CandidateLimit > MaxCandidateLimit:
		return refuse("CandidateLimit", "must be between 1 and "+strconv.Itoa(MaxCandidateLimit))
	case p.MaxAbandonedSearches < 0:
		return refuse("MaxAbandonedSearches", "must be strictly positive")
	case p.MinimumMatchedTerms < 0 || p.MinimumMatchedTerms > math.MaxInt32:
		return refuse("MinimumMatchedTerms", "must be strictly positive")
	case p.MinimumVectorRelevance != nil && !unitInterval(*p.MinimumVectorRelevance):
		return refuse("MinimumVectorRelevance", "must be between 0 and 1 inclusive")
	}
	return nil
}

// Resolved is the policy validated, with every default filled in (MinimumConfidence set, MaxAge left 0 for none).
func (p RetrievalPolicy) Resolved() (RetrievalPolicy, error) {
	if err := p.Validate(); err != nil {
		return RetrievalPolicy{}, err
	}
	if p.Timeout == 0 {
		p.Timeout = DefaultRetrievalTimeout
	}
	if p.MinimumConfidence == nil {
		p.MinimumConfidence = ptrTo(DefaultMinConfidence)
	} else {
		p.MinimumConfidence = ptrTo(*p.MinimumConfidence)
	}
	if p.RecencyHalfLife == 0 {
		p.RecencyHalfLife = DefaultRecencyHalfLife
	}
	if p.CandidateLimit == 0 {
		p.CandidateLimit = CandidateLimit
	}
	if p.MaxAbandonedSearches == 0 {
		p.MaxAbandonedSearches = DefaultMaxAbandonedSearches
	}
	if p.MinimumMatchedTerms == 0 {
		p.MinimumMatchedTerms = DefaultMinimumMatchedTerms
	}
	if p.MinimumVectorRelevance != nil {
		p.MinimumVectorRelevance = ptrTo(*p.MinimumVectorRelevance)
	}
	return p, nil
}

// SearchParameters are the text search's parameters under the policy: its confidence floor, CandidateLimit + 1 as
// the limit, and its minimum of matched terms. An invalid policy is an error.
func (p RetrievalPolicy) SearchParameters(scope Scope, taskText string, excludeModelAuthored bool) (SearchParameters,
	error) {
	resolved, err := p.Resolved()
	if err != nil {
		return SearchParameters{}, err
	}
	return SearchParameters{Scope: scope, TaskText: taskText, MinConfidence: resolved.MinimumConfidence,
		Limit: resolved.CandidateLimit + 1, ExcludeModelAuthored: excludeModelAuthored,
		MinimumMatchedTerms: resolved.MinimumMatchedTerms}, nil
}

// VectorQuery is the vector search's query under the policy: its confidence floor and CandidateLimit + 1 as the
// limit (the vector floor, MinimumVectorRelevance, is applied by ranking, never sent to the index). An invalid policy
// is an error.
func (p RetrievalPolicy) VectorQuery(scope Scope, modelID string, vector []float32, excludeModelAuthored bool) (
	VectorQuery, error) {
	resolved, err := p.Resolved()
	if err != nil {
		return VectorQuery{}, err
	}
	return VectorQuery{Scope: scope, ModelID: modelID, Vector: vector, MinConfidence: resolved.MinimumConfidence,
		Limit: resolved.CandidateLimit + 1, ExcludeModelAuthored: excludeModelAuthored}, nil
}

// ConfidenceDecayPolicy is .NET's ConfidenceDecayPolicy: how a record's confidence component fades with the age of
// the lesson (from CreatedAt), at a rate set by its domain, the value of its environment metadata entry DomainKey.
// Decay only reorders: it never excludes, and the store's confidence floor is on stored confidence.
type ConfidenceDecayPolicy struct {
	// DomainKey names the metadata entry holding the domain; DefaultDomainKey when empty (Go cannot tell an empty
	// key from an unset one); one that is only white space is refused.
	DomainKey string
	// HalfLives is each domain's half-life, matched ordinally; nil: that domain never decays, whatever
	// DefaultHalfLife says. A set half-life must be strictly positive (zero is refused); a blank domain is refused.
	HalfLives map[string]*time.Duration
	// DefaultHalfLife applies to a record whose domain is missing or not listed; nil: such a record does not decay.
	// Strictly positive when set.
	DefaultHalfLife *time.Duration
}

// Validate refuses what .NET refuses, naming the option (a *PolicyError).
func (p ConfidenceDecayPolicy) Validate() error {
	if p.DomainKey != "" && isNullOrWhitespace(p.DomainKey) {
		return refuse("DomainKey", "must not be blank")
	}
	domains := make([]string, 0, len(p.HalfLives))
	for domain := range p.HalfLives {
		domains = append(domains, domain)
	}
	sort.Strings(domains)
	for _, domain := range domains {
		if isNullOrWhitespace(domain) {
			return refuse("HalfLives", "must not name a blank domain")
		}
		if halfLife := p.HalfLives[domain]; halfLife != nil && *halfLife <= 0 {
			return refuse("HalfLives", "must hold strictly positive half-lives")
		}
	}
	if p.DefaultHalfLife != nil && *p.DefaultHalfLife <= 0 {
		return refuse("DefaultHalfLife", "must be strictly positive")
	}
	return nil
}

// Resolved is the policy validated, its key defaulted and its map and half-lives copied, so a later change to the
// host's map has no effect.
func (p ConfidenceDecayPolicy) Resolved() (ConfidenceDecayPolicy, error) {
	if err := p.Validate(); err != nil {
		return ConfidenceDecayPolicy{}, err
	}
	if p.DomainKey == "" {
		p.DomainKey = DefaultDomainKey
	}
	halfLives := make(map[string]*time.Duration, len(p.HalfLives))
	for domain, halfLife := range p.HalfLives {
		if halfLife != nil {
			halfLife = ptrTo(*halfLife)
		}
		halfLives[domain] = halfLife
	}
	p.HalfLives = halfLives
	if p.DefaultHalfLife != nil {
		p.DefaultHalfLife = ptrTo(*p.DefaultHalfLife)
	}
	return p, nil
}

// HalfLifeFor is the half-life of a record whose domain is domain (nil: none), or nil when it does not decay.
func (p ConfidenceDecayPolicy) HalfLifeFor(domain *string) *time.Duration {
	if domain != nil {
		if halfLife, ok := p.HalfLives[*domain]; ok {
			return halfLife
		}
	}
	return p.DefaultHalfLife
}

// ExclusionReason is .NET's RetrievalExclusionReason: why ranking left out a candidate a store returned.
type ExclusionReason string

// The exclusion reasons a port reports (.NET's EnvironmentMismatch and UnconfirmedContent need features the port
// does not have).
const (
	ExclusionIneligibleStatus ExclusionReason = "IneligibleStatus"
	ExclusionModelAuthored    ExclusionReason = "ModelAuthored"
	ExclusionExpired          ExclusionReason = "Expired"
)

// Exclusion is one candidate an eligibility check left out, and why (.NET's ExcludedExperience).
type Exclusion struct {
	ExperienceID UUID
	Reason       ExclusionReason
}

// RetrievalOutcome is .NET's RetrievalOutcome, the retrieve operation's telemetry outcome.
type RetrievalOutcome string

// The retrieval outcomes.
const (
	RetrievalCompleted RetrievalOutcome = "Completed"
	RetrievalTimedOut  RetrievalOutcome = "TimedOut"
	RetrievalDenied    RetrievalOutcome = "Denied"
	RetrievalFailed    RetrievalOutcome = "Failed"
)

// RankOptions are what ranking runs under: the request's model-authored exclusion, the policy (the zero policy is
// the default) and the confidence decay (nil: none).
type RankOptions struct {
	ExcludeModelAuthored bool
	Policy               RetrievalPolicy
	Decay                *ConfidenceDecayPolicy
}

// resolvedRank is RankOptions validated, with the seconds the arithmetic uses.
type resolvedRank struct {
	excludeModelAuthored bool
	policy               RetrievalPolicy
	decay                *ConfidenceDecayPolicy
	recencyHalfLife      float64
}

func (o RankOptions) resolve() (resolvedRank, error) {
	policy, err := o.Policy.Resolved()
	if err != nil {
		return resolvedRank{}, err
	}
	r := resolvedRank{excludeModelAuthored: o.ExcludeModelAuthored, policy: policy,
		recencyHalfLife: durationSeconds(policy.RecencyHalfLife)}
	if o.Decay != nil {
		decay, err := o.Decay.Resolved()
		if err != nil {
			return resolvedRank{}, err
		}
		r.decay = &decay
	}
	return r, nil
}

// durationSeconds is .NET's TimeSpan.TotalSeconds of d: its whole ticks as a double over ticks per second.
func durationSeconds(d time.Duration) float64 {
	return DurationFromTicks(int64(d / 100)).TotalSeconds()
}

// decayFactor is 2^(-age / halfLife), normalized; 1 when since is now or later. The power is exp2's (correctly
// rounded), not math.Pow's, which is further from .NET's Math.Pow (contract 1.18).
func decayFactor(since, now Instant, halfLifeSeconds float64) float64 {
	age := now.Sub(since)
	if age.Ticks() <= 0 {
		return 1.0
	}
	return normalizeScore(exp2(-age.TotalSeconds() / halfLifeSeconds))
}

// confidenceComponent is the stored confidence, decayed by the record's domain's half-life when one applies (then
// reporting the stored value as UndecayedValue).
func (r resolvedRank) confidenceComponent(record *ExperienceRecord, now Instant) RankingComponent {
	stored := normalizeScore(record.ReuseConfidence)
	component := RankingComponent{Kind: ComponentConfidence, Value: stored, Weight: weightConfidence}
	if r.decay == nil {
		return component
	}
	var domain *string
	if value, ok := record.Environment.Metadata[r.decay.DomainKey]; ok {
		domain = &value
	}
	halfLife := r.decay.HalfLifeFor(domain)
	if halfLife == nil {
		return component
	}
	component.Value = normalizeScore(stored * decayFactor(record.CreatedAt, now, durationSeconds(*halfLife)))
	component.UndecayedValue = ptrTo(stored)
	return component
}

// score scores one row under the options.
func (r resolvedRank) score(row *SearchRow, now Instant, preferredEnvironment map[string]string) RankedRecord {
	record := row.Record
	components := []RankingComponent{
		{Kind: ComponentRelevance, Value: normalizeScore(row.Relevance), Weight: weightRelevance},
		r.confidenceComponent(&record, now),
		{Kind: ComponentRecency, Value: decayFactor(record.UpdatedAt, now, r.recencyHalfLife), Weight: weightRecency},
		{Kind: ComponentStatus, Value: statusScore(record.Status), Weight: weightStatus},
		{Kind: ComponentEnvironmentCompatibility,
			Value:  normalizeScore(EnvironmentCompatibility(record.Environment.Metadata, preferredEnvironment)),
			Weight: weightEnvironment},
	}
	total := 0.0
	for _, component := range components {
		total += component.Contribution()
	}
	return RankedRecord{Record: record, Score: total, Components: components}
}

// rankChecked filters, scores and orders rows that already passed the safety checks and truncation: eligibility in
// .NET's order (status, authorship, expiry), each exclusion reported, then score descending, ties by experience_id
// as text, ascending, ordinal.
func (r resolvedRank) rankChecked(rows []SearchRow, now Instant, preferredEnvironment map[string]string) ([]RankedRecord,
	[]Exclusion) {
	ranked := []RankedRecord{}
	excluded := []Exclusion{}
	for i := range rows {
		row := &rows[i]
		record := &row.Record
		switch {
		case record.Status != StatusValidated && record.Status != StatusReinforced:
			excluded = append(excluded, Exclusion{record.ExperienceID, ExclusionIneligibleStatus})
			continue
		case r.excludeModelAuthored && IsModelAuthored(record.Reflection):
			excluded = append(excluded, Exclusion{record.ExperienceID, ExclusionModelAuthored})
			continue
		case r.policy.MaxAge > 0 && now.Sub(record.UpdatedAt).Ticks() > int64(r.policy.MaxAge/100):
			excluded = append(excluded, Exclusion{record.ExperienceID, ExclusionExpired})
			continue
		}
		ranked = append(ranked, r.score(row, now, preferredEnvironment))
	}
	sort.SliceStable(ranked, func(i, j int) bool {
		if ranked[i].Score != ranked[j].Score {
			return ranked[i].Score > ranked[j].Score
		}
		return ranked[i].Record.ExperienceID.String() < ranked[j].Record.ExperienceID.String()
	})
	return ranked, excluded
}

// RankWith turns rows into ranked records under a policy and a decay: the safety checks over the first
// CandidateLimit rows (an error wrapping ErrSearchSafety), truncation to CandidateLimit, eligibility (each exclusion
// reported in RankedResult.Excluded), then scoring and ordering. An invalid policy is an error (a *PolicyError).
func RankWith(rows []SearchRow, scope Scope, now Instant, preferredEnvironment map[string]string,
	options RankOptions) (RankedResult, error) {
	r, err := options.resolve()
	if err != nil {
		return RankedResult{}, err
	}
	considered, err := checkChannel(rows, scope, r.policy.CandidateLimit)
	if err != nil {
		return RankedResult{}, err
	}
	ranked, excluded := r.rankChecked(considered, now, preferredEnvironment)
	return RankedResult{Ranked: ranked, Truncated: len(rows) > r.policy.CandidateLimit, Excluded: excluded}, nil
}

// RankHybridWith is contract 1.6's hybrid ranking under a policy and a decay: the vector floor
// (Policy.MinimumVectorRelevance), each channel's checks and truncation at CandidateLimit, the merge, then RankWith's
// eligibility and scoring over the merged candidates.
func RankHybridWith(textRows, vectorRows []SearchRow, scope Scope, now Instant, preferredEnvironment map[string]string,
	options RankOptions) (RankedResult, error) {
	r, err := options.resolve()
	if err != nil {
		return RankedResult{}, err
	}
	limit := r.policy.CandidateLimit
	floor := r.policy.MinimumVectorRelevance
	var floored []SearchRow
	// The floor first, over every vector candidate: one it drops is not counted against the candidate limit.
	for i := range vectorRows {
		if floor == nil || vectorRows[i].Relevance >= *floor {
			floored = append(floored, vectorRows[i])
		}
	}
	// The checks are over each channel's first CandidateLimit candidates.
	text, err := checkChannel(textRows, scope, limit)
	if err != nil {
		return RankedResult{}, err
	}
	vector, err := checkChannel(floored, scope, limit)
	if err != nil {
		return RankedResult{}, err
	}
	merged := MergeChannels(text, vector, func(row SearchRow) UUID { return row.Record.ExperienceID },
		func(row SearchRow) float64 { return row.Relevance }, nil)
	rows := make([]SearchRow, len(merged))
	for i, entry := range merged {
		rows[i] = SearchRow{Record: entry.Item.Record, Relevance: entry.Relevance}
	}
	ranked, excluded := r.rankChecked(rows, now, preferredEnvironment)
	// The cut (contract 1.12, step 7): both channels together can hold more than CandidateLimit.
	if len(ranked) > limit {
		ranked = ranked[:limit]
	}
	return RankedResult{Ranked: ranked, Truncated: len(textRows) > limit || len(floored) > limit,
		Excluded: excluded}, nil
}

// checkChannel is the safety checks over a channel's first limit candidates; it returns those candidates.
func checkChannel(rows []SearchRow, scope Scope, limit int) ([]SearchRow, error) {
	considered := rows
	if len(considered) > limit {
		considered = considered[:limit]
	}
	seen := map[UUID]bool{}
	for i := range considered {
		row := &considered[i]
		if row.SharedByGrant || !row.Record.Scope.Equal(scope) {
			return nil, errors.Join(ErrSearchSafety,
				errors.New("a row lies outside the requested scope; nothing is returned"))
		}
		if seen[row.Record.ExperienceID] {
			return nil, errors.Join(ErrSearchSafety,
				errors.New("the same record came back more than once; nothing is returned"))
		}
		seen[row.Record.ExperienceID] = true
	}
	return considered, nil
}
