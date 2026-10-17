package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"sort"
	"strconv"
)

// The lessons view (contract 1.7): a read-only list of what the library has learned in one scope, each lesson with its
// status, confidence and checks, the block text an agent in that scope would be shown for it, why retrieval would not
// use it when it would not, and when it was last injected. It is the .NET AgentExperienceLessons, less its HTML.

// The lessons query's limits: .NET's ExperienceRecordQuery.
const (
	LessonsMinLimit     = 1
	LessonsDefaultLimit = 50
	LessonsMaxLimit     = 500
)

// LessonsOutcome says how a lessons read ended. Only Found carries lessons.
type LessonsOutcome string

// The outcomes.
const (
	// LessonsFound is a page that was read (it may hold no lesson).
	LessonsFound LessonsOutcome = "Found"
	// LessonsDenied is a scope that lies outside the caller's authorization bounds: an empty page, and no statement ran.
	LessonsDenied LessonsOutcome = "Denied"
)

// LessonIneligibility is why retrieval would not currently use a lesson. A port decides these five; .NET's Expired
// (a retrieval policy's maximum age) and ContentUnconfirmed (provenance signing) do not exist here, because this port
// has neither a maximum-age policy nor provenance signing, and are never answered.
type LessonIneligibility string

// The ineligibility codes, in the order they are decided.
const (
	// IneligibleStatus is a status that is not Validated or Reinforced.
	IneligibleStatus LessonIneligibility = "Status"
	// IneligibleBelowConfidenceFloor is a confidence below the retrieval minimum (DefaultMinConfidence), or NaN.
	IneligibleBelowConfidenceFloor LessonIneligibility = "BelowConfidenceFloor"
	// IneligibleModelAuthored is a model-authored lesson when the settings exclude them.
	IneligibleModelAuthored LessonIneligibility = "ModelAuthored"
	// IneligibleOverByteBudget is a block larger than the block options' maximum bytes.
	IneligibleOverByteBudget LessonIneligibility = "OverByteBudget"
	// IneligibleUnrenderable is a record whose block could not be rendered.
	IneligibleUnrenderable LessonIneligibility = "Unrenderable"
)

// LessonQuery is what Lessons.List lists.
type LessonQuery struct {
	// Statuses are the statuses to list; nil lists every status. When set: at least one, each a defined status (a
	// repeated one counts once).
	Statuses []ExperienceStatus
	// Limit is how many lessons to list at most, LessonsMinLimit to LessonsMaxLimit; LessonsDefaultLimit when 0. With
	// IncludeIneligible off, the lessons left out still count against the records read.
	Limit int
	// IncludeIneligible is whether to list the lessons retrieval would not return, each marked with why; true when nil.
	IncludeIneligible *bool
}

// Validate refuses a limit outside LessonsMinLimit..LessonsMaxLimit (0 is the default) and statuses that are empty or
// hold an undefined one. Nothing runs against the store for an invalid query.
func (q LessonQuery) Validate() error {
	if q.Limit != 0 && (q.Limit < LessonsMinLimit || q.Limit > LessonsMaxLimit) {
		return fmt.Errorf("the lessons limit must be from %d to %d, not %d", LessonsMinLimit, LessonsMaxLimit, q.Limit)
	}
	if q.Statuses != nil {
		if len(q.Statuses) == 0 {
			return errors.New("the lessons statuses must be nil, for every status, or hold at least one")
		}
		for _, status := range q.Statuses {
			if !definedStatus(status) {
				return fmt.Errorf("the lessons statuses hold an undefined status %q", string(status))
			}
		}
	}
	return nil
}

// EffectiveLimit is the limit asked for: LessonsDefaultLimit when 0.
func (q LessonQuery) EffectiveLimit() int {
	if q.Limit == 0 {
		return LessonsDefaultLimit
	}
	return q.Limit
}

func (q LessonQuery) includeIneligible() bool {
	return q.IncludeIneligible == nil || *q.IncludeIneligible
}

func definedStatus(status ExperienceStatus) bool {
	switch status {
	case StatusCandidate, StatusValidated, StatusQuarantined, StatusContested, StatusStale, StatusSuperseded,
		StatusRevoked, StatusReinforced:
		return true
	}
	return false
}

// distinctStatuses are the statuses once each, in order; nil for nil.
func distinctStatuses(statuses []ExperienceStatus) []ExperienceStatus {
	if statuses == nil {
		return nil
	}
	seen := map[ExperienceStatus]bool{}
	distinct := make([]ExperienceStatus, 0, len(statuses))
	for _, status := range statuses {
		if !seen[status] {
			seen[status] = true
			distinct = append(distinct, status)
		}
	}
	return distinct
}

// InjectionSummary is when a record was last injected in its own scope, and how many injected blocks delivered it.
type InjectionSummary struct {
	LastInjectedAt Instant
	InjectionCount int64
}

// LessonRecord is a record a store answered for the lessons view, with its injection summary (nil for a record never
// injected, or when the injections are not tracked).
type LessonRecord struct {
	Record    ExperienceRecord
	Injection *InjectionSummary
}

// LessonRecords is a store's answer to ListLessonRecords.
type LessonRecords struct {
	// Records are the scope's live records, newest first (created_at descending, then experience ID ascending,
	// ordinally), at most the limit asked for.
	Records []LessonRecord
	// Answered is how many records the store answered, which is len(Records) unless it left some out (a sealed
	// payload, which a contract 1.x port cannot read). The lessons view reads "as many as were asked for" from it.
	Answered int
	// InjectionsTracked is true when the injections were read with the records: a store that tracks them and was able
	// to read them. False otherwise, and then a nil Injection means "not known", not "never".
	InjectionsTracked bool
}

// LessonsReader is a Store that answers the lessons view's read (contract 1.7). Optional: the PostgreSQL store and the
// in-memory store both are.
type LessonsReader interface {
	// ListLessonRecords reads the live records in exactly scope, newest first, filtered to statuses (nil: every
	// status), at most limit (1 to LessonsMaxLimit), each with its last injection when the store tracks injections. It
	// never writes. A scope outside bounds is ErrAuthorizationDenied and runs nothing; a statuses list that is empty
	// or undefined, or a limit out of range, is an error.
	ListLessonRecords(ctx context.Context, bounds AuthorizationBounds, scope Scope, statuses []ExperienceStatus,
		limit int) (LessonRecords, error)
}

// LessonCheck is one check a lesson's outcome was verified by: never the evidence's detail.
type LessonCheck struct {
	CheckID      string
	EvidenceKind string
	Result       CheckResult
}

// Lesson is one stored lesson, as the lessons view shows it.
type Lesson struct {
	ExperienceID UUID
	// TaskID is the task it was captured under; empty when it has none.
	TaskID     string
	Status     ExperienceStatus
	Confidence float64
	CreatedAt  Instant
	UpdatedAt  Instant
	// Checks are the checks its outcome was verified by, in stored order (empty when it holds no evidence).
	Checks []LessonCheck
	// BlockText is the Historical Reference block an agent in this scope would be shown for this record alone (RenderAlone):
	// numbered 1, with no ranking, whatever its status and size. Empty when it could not be rendered. Untrusted text.
	BlockText string
	// IsEligible is whether retrieval would currently consider it, setting the task aside, and its block fits the budget.
	IsEligible bool
	// Ineligibility and IneligibleReason say why not, as a code and in words; empty when eligible.
	Ineligibility    LessonIneligibility
	IneligibleReason string
	// LastInjectedAt is when an agent in this scope was last shown this lesson; nil when never (the page's
	// InjectionsTracked is then true) or not known (it is false).
	LastInjectedAt *Instant
	// InjectionCount is how many injected blocks delivered it in this scope; 0 when never or not tracked.
	InjectionCount int64
}

// LessonsPage is one page of lessons from one scope.
type LessonsPage struct {
	// Scope is the scope listed: the caller's own.
	Scope   Scope
	Outcome LessonsOutcome
	// Lessons are newest first; never nil.
	Lessons []Lesson
	// GeneratedAt is when the page was read, by the view's clock.
	GeneratedAt Instant
	// HasMore is whether more lessons matching the query exist past Lessons, or may: the store answers at most
	// LessonsMaxLimit records, and when it answered as many as were asked for, more may be stored.
	HasMore bool
	// OutOfScopeDropped is how many records the store answered from outside the scope, which were left out (a
	// conforming store answers none).
	OutOfScopeDropped int
	// InjectionsTracked is whether the page was read with its injections (see LessonRecords).
	InjectionsTracked bool
}

// LessonsOptions are the lessons view's settings.
type LessonsOptions struct {
	// BlockOptions are the block's layout and limits: the ones the injection uses (the contract's defaults when zero).
	// MaxBytes is the byte budget a lesson's block is held against.
	BlockOptions BlockOptions
	// ExcludeModelAuthored marks model-authored lessons ineligible: the same flag the injection searches with.
	ExcludeModelAuthored bool
	// Clock stamps a page's GeneratedAt; Now when nil.
	Clock func() Instant
}

// Lessons is the read-only lessons view over a store. It is safe for concurrent use when its store is.
type Lessons struct {
	store   LessonsReader
	options LessonsOptions
	// render writes the block of one record alone; RenderAlone, but for a test.
	render func(*ExperienceRecord, BlockOptions) (string, error)
}

// NewLessons is the lessons view over store, with the block options and model-authored flag the injection uses. It
// refuses a nil store and block options the block cannot honour.
func NewLessons(store LessonsReader, options LessonsOptions) (*Lessons, error) {
	if store == nil {
		return nil, errors.New("the lessons view needs a store that is a LessonsReader")
	}
	if err := options.BlockOptions.Validate(); err != nil {
		return nil, err
	}
	if options.Clock == nil {
		options.Clock = Now
	}
	return &Lessons{store: store, options: options, render: RenderAlone}, nil
}

// ListFor lists the lessons in identity's scope, under its authorization bounds: List(identity.Authorization(),
// identity.Scope). The identity must come from your own authentication, never from a request's query or body.
func (l *Lessons) ListFor(ctx context.Context, identity Identity, query LessonQuery) (LessonsPage, error) {
	return l.List(ctx, identity.Authorization(), identity.Scope, query)
}

// List lists the lessons in exactly scope, newest first, under the caller's authorization bounds. It reads only: the
// scope's own live records (what another scope lends through a sharing grant is not listed), and never writes.
//
// An invalid query is an error, before anything is read. A scope outside bounds is an empty page with the outcome
// Denied, never an error. A record that cannot be rendered is listed as IneligibleUnrenderable, and the rest are
// still listed. The work is bounded: at most LessonsMaxLimit records are read and rendered.
//
// Listing lessons discloses them: serve the view only behind the host's own authorization.
func (l *Lessons) List(ctx context.Context, bounds AuthorizationBounds, scope Scope, query LessonQuery) (LessonsPage, error) {
	if err := query.Validate(); err != nil {
		return LessonsPage{}, err
	}
	limit := query.EffectiveLimit()
	if !bounds.Permits(scope) {
		return LessonsPage{Scope: scope, Outcome: LessonsDenied, Lessons: []Lesson{}, GeneratedAt: l.options.Clock()}, nil
	}
	// One record past the limit tells "exactly the limit" from "more exist". Leaving ineligible lessons out may skip
	// any number of records, so then as many as the store answers are read.
	probe := LessonsMaxLimit
	if query.includeIneligible() {
		probe = min(limit+1, LessonsMaxLimit)
	}
	read, err := l.store.ListLessonRecords(ctx, bounds, scope, distinctStatuses(query.Statuses), probe)
	if err != nil {
		if errors.Is(err, ErrAuthorizationDenied) {
			return LessonsPage{Scope: scope, Outcome: LessonsDenied, Lessons: []Lesson{}, GeneratedAt: l.options.Clock()}, nil
		}
		return LessonsPage{}, err
	}
	page := LessonsPage{Scope: scope, Outcome: LessonsFound, Lessons: []Lesson{}, GeneratedAt: l.options.Clock(),
		InjectionsTracked: read.InjectionsTracked}
	for i := range read.Records {
		if err := ctx.Err(); err != nil {
			return LessonsPage{}, err
		}
		held := &read.Records[i]
		// Only the caller's own scope: a store that answered with anything else is not shown it.
		if !held.Record.Scope.Equal(scope) {
			page.OutOfScopeDropped++
			continue
		}
		lesson := l.describe(&held.Record)
		if read.InjectionsTracked && held.Injection != nil {
			at := held.Injection.LastInjectedAt
			lesson.LastInjectedAt, lesson.InjectionCount = &at, held.Injection.InjectionCount
		}
		if !lesson.IsEligible && !query.includeIneligible() {
			continue
		}
		if len(page.Lessons) == limit {
			page.HasMore = true
			break
		}
		page.Lessons = append(page.Lessons, lesson)
	}
	// A store that answered as many records as were asked for may hold more than were read.
	answered := read.Answered
	if answered == 0 {
		answered = len(read.Records)
	}
	page.HasMore = page.HasMore || answered >= probe
	return page, nil
}

// describe is one record as a lesson. A record that cannot be rendered is a lesson marked so, never a failure.
func (l *Lessons) describe(record *ExperienceRecord) (lesson Lesson) {
	lesson = Lesson{
		ExperienceID: record.ExperienceID,
		TaskID:       record.TaskID,
		Status:       record.Status,
		Confidence:   record.ReuseConfidence,
		CreatedAt:    record.CreatedAt,
		UpdatedAt:    record.UpdatedAt,
		Checks:       []LessonCheck{},
	}
	defer func() {
		if recovered := recover(); recovered != nil {
			lesson.Checks, lesson.BlockText, lesson.IsEligible = []LessonCheck{}, "", false
			lesson.Ineligibility = IneligibleUnrenderable
			lesson.IneligibleReason = "it could not be rendered (" + failureName(recovered) + ")"
		}
	}()
	text, err := l.render(record, l.options.BlockOptions)
	if err != nil {
		panic(err)
	}
	for i := range record.Outcome.Evidence {
		evidence := &record.Outcome.Evidence[i]
		lesson.Checks = append(lesson.Checks, LessonCheck{CheckID: evidence.CheckID, EvidenceKind: evidence.Kind,
			Result: evidence.Result})
	}
	lesson.BlockText = text
	maxBytes := l.options.BlockOptions.withDefaults().MaxBytes
	switch {
	case record.Status != StatusValidated && record.Status != StatusReinforced:
		lesson.Ineligibility = IneligibleStatus
		lesson.IneligibleReason = "status " + string(record.Status) + " is never retrieved"
	case !(record.ReuseConfidence >= DefaultMinConfidence):
		lesson.Ineligibility = IneligibleBelowConfidenceFloor
		lesson.IneligibleReason = "confidence " + shortNumber(record.ReuseConfidence) +
			" is below the retrieval floor " + shortNumber(DefaultMinConfidence)
	case l.options.ExcludeModelAuthored && IsModelAuthored(record.Reflection):
		lesson.Ineligibility = IneligibleModelAuthored
		lesson.IneligibleReason = "model-authored lessons are excluded"
	case len(text) > maxBytes:
		lesson.Ineligibility = IneligibleOverByteBudget
		lesson.IneligibleReason = "its block (" + strconv.Itoa(len(text)) + " bytes) is larger than the " +
			strconv.Itoa(maxBytes) + "-byte injection budget"
	default:
		lesson.IsEligible = true
	}
	return lesson
}

// failureName is the short name of the type of a recovered panic's value (.NET's GetType().Name).
func failureName(recovered any) string {
	t := reflect.TypeOf(recovered)
	if t == nil {
		return "nil"
	}
	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	if t.Name() != "" {
		return t.Name()
	}
	return t.String()
}

// SortLessonRecords orders records newest first, as the contract's lessons statements do: created_at descending (to
// whole microseconds, as a stored instant is), then experience ID ascending, ordinally on its lower-case hyphenated
// text.
func SortLessonRecords(records []LessonRecord) {
	sort.SliceStable(records, func(i, j int) bool {
		a, b := records[i].Record, records[j].Record
		if at, bt := a.CreatedAt.TruncateToMicroseconds().Ticks(), b.CreatedAt.TruncateToMicroseconds().Ticks(); at != bt {
			return at > bt
		}
		return a.ExperienceID.String() < b.ExperienceID.String()
	})
}

// ValidateLessonsRead refuses what a LessonsReader refuses: a limit outside 1 to LessonsMaxLimit, and statuses that
// are empty or hold an undefined one.
func ValidateLessonsRead(statuses []ExperienceStatus, limit int) error {
	if limit < LessonsMinLimit || limit > LessonsMaxLimit {
		return fmt.Errorf("the lessons limit must be from %d to %d, not %d", LessonsMinLimit, LessonsMaxLimit, limit)
	}
	return LessonQuery{Statuses: statuses}.Validate()
}
