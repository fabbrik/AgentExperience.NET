package agentexperience

import (
	"errors"
	"math"
	"strconv"
)

// Text retrieval: the parameters of the contract's two SQL statements, and how the rows they return are checked,
// truncated, filtered and ranked.

// Retrieval defaults.
const (
	DefaultMinConfidence       = 0.5
	CandidateLimit             = 50
	StatementLimit             = CandidateLimit + 1
	DefaultMinimumMatchedTerms = 3
	weightRelevance            = 0.35
	weightConfidence           = 0.25
	weightRecency              = 0.15
	weightStatus               = 0.15
	weightEnvironment          = 0.10
	reinforcedStatusScore      = 1.0
	validatedStatusScore       = 0.5
)

// EligibleStatuses are the statuses retrieval returns (a new slice each call).
func EligibleStatuses() []ExperienceStatus {
	return []ExperienceStatus{StatusValidated, StatusReinforced}
}

// AuthorizationBounds are the host's authorization bounds, which every transaction declares first: the tenant
// always, and each other field bound to a value or (nil) unbounded. Never a request's scope.
type AuthorizationBounds struct {
	TenantID      string
	ApplicationID *string
	ProjectID     *string
	TeamID        *string
	AgentID       *string
	UserID        *string
}

// Permits reports whether the bounds permit scope: the tenant always, and every bounded field exactly.
func (b AuthorizationBounds) Permits(scope Scope) bool {
	bound := func(bound, value *string) bool {
		return bound == nil || (value != nil && *bound == *value)
	}
	return b.TenantID == scope.TenantID &&
		bound(b.ApplicationID, &scope.ApplicationID) &&
		bound(b.ProjectID, &scope.ProjectID) &&
		bound(b.TeamID, scope.TeamID) &&
		bound(b.AgentID, scope.AgentID) &&
		bound(b.UserID, scope.UserID)
}

// BoundsOf are bounds equal to a scope: its unset optional fields are left unbounded.
func BoundsOf(scope Scope) AuthorizationBounds {
	return AuthorizationBounds{
		TenantID:      scope.TenantID,
		ApplicationID: Ptr(scope.ApplicationID),
		ProjectID:     Ptr(scope.ProjectID),
		TeamID:        scope.TeamID,
		AgentID:       scope.AgentID,
		UserID:        scope.UserID,
	}
}

// Parameters are sql/declare-bounds.sql's parameters: "=" and the value, or the empty string for no bound.
func (b AuthorizationBounds) Parameters() map[string]string {
	bound := func(value *string) string {
		if value == nil {
			return ""
		}
		return "=" + *value
	}
	return map[string]string{
		"auth_tenant":      "=" + b.TenantID,
		"auth_application": bound(b.ApplicationID),
		"auth_project":     bound(b.ProjectID),
		"auth_team":        bound(b.TeamID),
		"auth_agent":       bound(b.AgentID),
		"auth_user":        bound(b.UserID),
	}
}

// SearchParameters are sql/search-experience-text.sql's parameters.
type SearchParameters struct {
	Scope    Scope
	TaskText string
	// Statuses are EligibleStatuses() when nil; when set, a non-empty list of status member names.
	Statuses []ExperienceStatus
	// MinConfidence is DefaultMinConfidence when nil; when set, a finite number from 0 to 1.
	MinConfidence *float64
	// Limit is StatementLimit when 0; when set, 1 to MaxStatementLimit. Ranking detects truncation by the statement
	// returning one row more than the candidate limit, so retrieval asks for the policy's CandidateLimit + 1
	// (RetrievalPolicy.SearchParameters); a limit of 1 can never report truncation.
	Limit                int
	ExcludeModelAuthored bool
	// WithGrants must be false: grants are outside contract 1.x.
	WithGrants bool
	// MinimumMatchedTerms is DefaultMinimumMatchedTerms when 0; when set, from 1 to math.MaxInt32 (the SQL takes an
	// integer).
	MinimumMatchedTerms int
}

// NewSearchParameters are the contract's defaults for a scope and a task text.
func NewSearchParameters(scope Scope, taskText string) SearchParameters {
	return SearchParameters{Scope: scope, TaskText: taskText}
}

// Validate refuses a blank or overlong task text (longer text is refused, not cut), grants, a limit outside 1 to
// MaxStatementLimit, a minimum matched terms outside 1 to math.MaxInt32, a minimum confidence outside 0 to 1, and an
// empty or unknown status.
func (p SearchParameters) Validate() error {
	if isNullOrWhitespace(p.TaskText) {
		return errors.New("the task text must not be blank")
	}
	if UTF16Len(p.TaskText) > MaxTaskTextLength {
		return errors.New("the task text is longer than " + strconv.Itoa(MaxTaskTextLength) +
			" UTF-16 code units; it is refused, not cut")
	}
	if p.WithGrants {
		return errors.New("grants are outside contract 1.x: WithGrants must be false")
	}
	if p.Limit < 0 || p.Limit > MaxStatementLimit {
		return errors.New("the limit must be from 1 to " + strconv.Itoa(MaxStatementLimit))
	}
	if p.MinimumMatchedTerms < 0 || p.MinimumMatchedTerms > math.MaxInt32 {
		return errors.New("the minimum matched terms must be from 1 to 2147483647")
	}
	if p.MinConfidence != nil && !(*p.MinConfidence >= 0 && *p.MinConfidence <= 1) {
		return errors.New("the minimum confidence must be a number from 0 to 1")
	}
	if p.Statuses != nil && len(p.Statuses) == 0 {
		return errors.New("the statuses must not be empty: leave them nil for the eligible statuses")
	}
	for _, status := range p.Statuses {
		if !contains(experienceStatuses, string(status)) {
			return errors.New("the statuses must be experience status member names")
		}
	}
	return nil
}

// Values are the statement's named parameters, as Go values: optional scope fields nil, statuses a []string, the
// limit and the minimum matched terms int32. Parameters that fail Validate are an error.
func (p SearchParameters) Values() (map[string]any, error) {
	if err := p.Validate(); err != nil {
		return nil, err
	}
	statuses := p.Statuses
	if statuses == nil {
		statuses = EligibleStatuses()
	}
	names := make([]string, len(statuses))
	for i, status := range statuses {
		names[i] = string(status)
	}
	minConfidence := DefaultMinConfidence
	if p.MinConfidence != nil {
		minConfidence = *p.MinConfidence
	}
	limit := p.Limit
	if limit == 0 {
		limit = StatementLimit
	}
	terms := p.MinimumMatchedTerms
	if terms == 0 {
		terms = DefaultMinimumMatchedTerms
	}
	optional := func(value *string) any {
		if value == nil {
			return nil
		}
		return *value
	}
	return map[string]any{
		"tenant_id":              p.Scope.TenantID,
		"application_id":         p.Scope.ApplicationID,
		"project_id":             p.Scope.ProjectID,
		"team_id":                optional(p.Scope.TeamID),
		"agent_id":               optional(p.Scope.AgentID),
		"user_id":                optional(p.Scope.UserID),
		"task_text":              p.TaskText,
		"statuses":               names,
		"min_confidence":         minConfidence,
		"limit":                  int32(limit),
		"exclude_model_authored": p.ExcludeModelAuthored,
		"with_grants":            p.WithGrants,
		"minimum_matched_terms":  int32(terms),
	}, nil
}

// WidenReal is a real (float4) column as the double ranking uses: the float4 value widened exactly (0.6 is
// 0.6000000238418579). A double read from the column's shortest decimal text rounds to the same float4, so this
// recovers that float4 and widens it without rounding through decimal text.
func WidenReal(value float64) float64 { return float64(float32(value)) }

// SearchRow is one row the text search returned: the record, and its relevance widened to a double.
type SearchRow struct {
	Record        ExperienceRecord
	Relevance     float64
	SharedByGrant bool
}

// RankedResult is the ranked records, best first, and whether the statement returned more rows than the candidate
// limit.
type RankedResult struct {
	Ranked    []RankedRecord
	Truncated bool
	// Excluded are the candidates an eligibility check left out, in the candidates' order, each with .NET's reason
	// (contract 1.12). Not a complete account: what the search's own predicates filtered never reaches ranking.
	Excluded []Exclusion
}

// ErrSearchSafety is a result that failed a safety check: a row outside the requested scope, or one record twice.
// Nothing is returned.
var ErrSearchSafety = errors.New("the search result failed a safety check")

// EnvironmentCompatibility is the share of preferred attributes the record's metadata matches exactly; 1 with none
// preferred. The run's model (ModelMetadataKey) is bookkeeping: it is dropped from both sides, so a lesson ranks the
// same for an agent on any model.
func EnvironmentCompatibility(metadata, preferred map[string]string) float64 {
	metadata, preferred = withoutModel(metadata), withoutModel(preferred)
	if len(preferred) == 0 {
		return 1.0
	}
	matched := 0
	for key, value := range preferred {
		if held, ok := metadata[key]; ok && held == value {
			matched++
		}
	}
	return float64(matched) / float64(len(preferred))
}

func normalizeScore(value float64) float64 {
	if math.IsNaN(value) {
		return 0
	}
	return math.Min(math.Max(value, 0), 1)
}

func statusScore(status ExperienceStatus) float64 {
	switch status {
	case StatusReinforced:
		return reinforcedStatusScore
	case StatusValidated:
		return validatedStatusScore
	}
	return 0
}

// Score scores one row with the default weights and the default policy, components in the contract's order.
func Score(row *SearchRow, now Instant, preferredEnvironment map[string]string) RankedRecord {
	r, _ := RankOptions{}.resolve()
	return r.score(row, now, preferredEnvironment)
}

// Rank turns rows into ranked records under the default policy (RankWith with zero options but the model-authored
// exclusion): the safety checks over the first 50 rows (an error wrapping ErrSearchSafety), truncation to 50,
// filtering, then scoring and ordering by score, descending, ties by experience_id as text, ascending, ordinal.
func Rank(rows []SearchRow, scope Scope, now Instant, preferredEnvironment map[string]string,
	excludeModelAuthored bool) (RankedResult, error) {
	return RankWith(rows, scope, now, preferredEnvironment, RankOptions{ExcludeModelAuthored: excludeModelAuthored})
}
