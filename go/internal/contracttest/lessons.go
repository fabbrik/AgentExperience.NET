package contracttest

import (
	"encoding/json"
	"fmt"
	"path/filepath"
	"regexp"
	"strconv"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.7's lessons-view fixtures ------------------------------------------------------------------------

// LessonsFamily is the lessons-view fixture family's directory name.
const LessonsFamily = "lessons-view"

// InjectionColumns are agent_experience.experience_injections' columns, in the order a seed row holds them.
var InjectionColumns = []string{"experience_id", "tenant_id", "application_id", "project_id", "team_id", "agent_id",
	"user_id", "last_injected_at", "injection_count"}

// InjectionRow is one row of a lessons-view seed's injections: Raw is the row as written (every column in
// PostgreSQL's text form, for inserting it as the schema's owner), the rest is the same row read.
type InjectionRow struct {
	Raw            *ae.Object
	ExperienceID   ae.UUID
	Scope          ae.Scope
	LastInjectedAt ae.Instant
	InjectionCount int64
}

// LessonsCase is one lessons-view case: seed.json, query.json and expected.json.
type LessonsCase struct {
	Name       string
	Records    []ae.ExperienceRecord
	Injections []InjectionRow
	// Scope is the scope listed and also the caller's bounds.
	Scope             ae.Scope
	Statuses          []ae.ExperienceStatus
	Limit             int
	IncludeIneligible bool
	Now               ae.Instant
	Expected          *ae.Object

	// Contract 1.10: the holdout's assignment rows seeded (as the owner), and the measured benefit's query members.
	Assignments            []AssignmentRow
	IncludeMeasuredBenefit bool
	// MinimumRunsPerArm is 0 when the query names none (the default).
	MinimumRunsPerArm int
	PerModel          bool
	// HoldoutReadRevoked: before listing, the owner revokes the application role's SELECT on the assignments; the list
	// must fail, with expected.json's errorClass (ExpectedErrorClass).
	HoldoutReadRevoked bool
	// Policy is contract 1.12's retrieval policy the view runs with: query.json's minimumConfidence and maxAgeTicks
	// (the default when absent).
	Policy ae.RetrievalPolicy
}

// Query is the case's query.
func (c *LessonsCase) Query() ae.LessonQuery {
	include := c.IncludeIneligible
	return ae.LessonQuery{Statuses: c.Statuses, Limit: c.Limit, IncludeIneligible: &include,
		IncludeMeasuredBenefit: c.IncludeMeasuredBenefit}
}

// Options are the lessons view's options for the case: its clock, and the benefit policy it reads the measured
// benefit with.
func (c *LessonsCase) Options() ae.LessonsOptions {
	now := c.Now
	return ae.LessonsOptions{Clock: func() ae.Instant { return now },
		Benefit:         ae.BenefitPolicy{MinimumRunsPerArm: c.MinimumRunsPerArm, PerModel: c.PerModel},
		RetrievalPolicy: c.Policy}
}

// ExpectedErrorClass is the error class a case whose list must fail names; "" for a case that lists a page.
func (c *LessonsCase) ExpectedErrorClass() string {
	class, _ := get(c.Expected, "errorClass").(string)
	return class
}

var timestamp = regexp.MustCompile(`^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}(?:\.\d+)?)([+-]\d{2})(?::?(\d{2}))?$`)

// ParseTimestamptz reads PostgreSQL's text form of a timestamptz ("2026-03-02 08:00:00.123456+00").
func ParseTimestamptz(text string) (ae.Instant, error) {
	m := timestamp.FindStringSubmatch(text)
	if m == nil {
		return ae.Instant{}, fmt.Errorf("%q is not a PostgreSQL timestamptz", text)
	}
	minutes := m[4]
	if minutes == "" {
		minutes = "00"
	}
	return ae.ParseInstant(m[1] + "T" + m[2] + m[3] + ":" + minutes)
}

// LoadLessonsCase reads a lessons-view case.
func LoadLessonsCase(name string) (*LessonsCase, error) {
	dir := filepath.Join(Fixtures, LessonsFamily, name)
	read := func(file string) (*ae.Object, error) {
		value, err := ReadJSON(filepath.Join(dir, file))
		if err != nil {
			return nil, err
		}
		o, _ := value.(*ae.Object)
		if o == nil {
			return nil, fmt.Errorf("%s/%s is not an object", name, file)
		}
		return o, nil
	}
	seed, err := read("seed.json")
	if err != nil {
		return nil, err
	}
	query, err := read("query.json")
	if err != nil {
		return nil, err
	}
	expected, err := read("expected.json")
	if err != nil {
		return nil, err
	}
	c := &LessonsCase{Name: name, Expected: expected}
	for _, item := range list(get(seed, "records")) {
		o := object(item)
		record, err := ae.FromColumns(get(o, "columns"), get(o, "payload"))
		if err != nil {
			return nil, fmt.Errorf("%s: %w", name, err)
		}
		c.Records = append(c.Records, record)
	}
	for _, item := range list(get(seed, "injections")) {
		row := object(item)
		id, err := ae.ParseUUID(text(get(row, "experience_id")))
		if err != nil {
			return nil, err
		}
		at, err := ParseTimestamptz(text(get(row, "last_injected_at")))
		if err != nil {
			return nil, err
		}
		count, err := strconv.ParseInt(text(get(row, "injection_count")), 10, 64)
		if err != nil {
			return nil, err
		}
		c.Injections = append(c.Injections, InjectionRow{Raw: row, ExperienceID: id, LastInjectedAt: at,
			InjectionCount: count, Scope: ae.Scope{TenantID: text(get(row, "tenant_id")),
				ApplicationID: text(get(row, "application_id")), ProjectID: text(get(row, "project_id")),
				TeamID: optionalText(get(row, "team_id")), AgentID: optionalText(get(row, "agent_id")),
				UserID: optionalText(get(row, "user_id"))}})
	}
	c.Scope = ScopeOf(get(query, "scope"))
	for _, status := range list(get(query, "statuses")) {
		c.Statuses = append(c.Statuses, ae.ExperienceStatus(text(status)))
	}
	c.Limit = int(integer(get(query, "limit")))
	c.IncludeIneligible, _ = get(query, "includeIneligible").(bool)
	c.IncludeMeasuredBenefit, _ = get(query, "includeMeasuredBenefit").(bool)
	c.PerModel, _ = get(query, "perModel").(bool)
	c.HoldoutReadRevoked, _ = get(query, "holdoutReadRevoked").(bool)
	if floor := get(query, "minimumConfidence"); floor != nil {
		f := Number(floor)
		c.Policy.MinimumConfidence = &f
	}
	if maxAge := get(query, "maxAgeTicks"); maxAge != nil {
		c.Policy.MaxAge = ticks(maxAge)
	}
	if minimum := get(query, "minimumRunsPerArm"); minimum != nil {
		c.MinimumRunsPerArm = int(integer(minimum))
	}
	if c.Assignments, err = assignmentRows(get(seed, "assignments")); err != nil {
		return nil, err
	}
	if c.Now, err = ae.ParseInstant(text(get(query, "now"))); err != nil {
		return nil, err
	}
	return c, nil
}

// LessonsPageJSON is a page in the fixtures' form. With includeMeasuredBenefit (contract 1.10) each lesson also holds
// measuredBenefit: null, or one lesson of an estimate (LessonBenefitJSON).
func LessonsPageJSON(page ae.LessonsPage, includeMeasuredBenefit bool) *ae.Object {
	instant := func(i ae.Instant) any { return i.String() }
	lessons := []any{}
	for _, lesson := range page.Lessons {
		checks := []any{}
		for _, check := range lesson.Checks {
			checks = append(checks, ae.NewObject(ae.Member{Key: "checkId", Value: check.CheckID},
				ae.Member{Key: "evidenceKind", Value: check.EvidenceKind},
				ae.Member{Key: "result", Value: string(check.Result)}))
		}
		var ineligibility, reason, injected any
		if lesson.Ineligibility != "" {
			ineligibility, reason = string(lesson.Ineligibility), lesson.IneligibleReason
		}
		if lesson.LastInjectedAt != nil {
			injected = instant(*lesson.LastInjectedAt)
		}
		o := ae.NewObject(
			ae.Member{Key: "experienceId", Value: lesson.ExperienceID.String()},
			ae.Member{Key: "taskId", Value: lesson.TaskID},
			ae.Member{Key: "status", Value: string(lesson.Status)},
			ae.Member{Key: "confidence", Value: json.Number(strconv.FormatFloat(lesson.Confidence, 'g', -1, 64))},
			ae.Member{Key: "createdAt", Value: instant(lesson.CreatedAt)},
			ae.Member{Key: "updatedAt", Value: instant(lesson.UpdatedAt)},
			ae.Member{Key: "checks", Value: checks},
			ae.Member{Key: "blockText", Value: lesson.BlockText},
			ae.Member{Key: "isEligible", Value: lesson.IsEligible},
			ae.Member{Key: "ineligibility", Value: ineligibility},
			ae.Member{Key: "ineligibleReason", Value: reason},
			ae.Member{Key: "lastInjectedAt", Value: injected},
			ae.Member{Key: "injectionCount", Value: json.Number(strconv.FormatInt(lesson.InjectionCount, 10))},
		)
		if includeMeasuredBenefit {
			var measured any
			if lesson.MeasuredBenefit != nil {
				measured = LessonBenefitJSON(*lesson.MeasuredBenefit)
			}
			o.Set("measuredBenefit", measured)
		}
		lessons = append(lessons, o)
	}
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(page.Outcome)},
		ae.Member{Key: "generatedAt", Value: instant(page.GeneratedAt)},
		ae.Member{Key: "hasMore", Value: page.HasMore},
		ae.Member{Key: "outOfScopeDropped", Value: json.Number(strconv.Itoa(page.OutOfScopeDropped))},
		ae.Member{Key: "injectionsTracked", Value: page.InjectionsTracked},
		ae.Member{Key: "lessons", Value: lessons},
	)
}

// instantKeys are the page and lesson members that hold instants, which compare as instants rather than as text.
var instantKeys = map[string]bool{"generatedAt": true, "createdAt": true, "updatedAt": true, "lastInjectedAt": true}

// canonicalInstants rewrites every instant member of a page to its canonical text, so that two spellings of one
// instant compare equal (the fixtures hold the microsecond; the instant, not the text, is the contract).
func canonicalInstants(value any) any {
	switch v := value.(type) {
	case *ae.Object:
		copied := ae.NewObject()
		for _, m := range v.Members() {
			if s, ok := m.Value.(string); ok && instantKeys[m.Key] {
				if instant, err := ae.ParseInstant(s); err == nil {
					copied.Set(m.Key, instant.String())
					continue
				}
			}
			copied.Set(m.Key, canonicalInstants(m.Value))
		}
		return copied
	case []any:
		copied := make([]any, len(v))
		for i := range v {
			copied[i] = canonicalInstants(v[i])
		}
		return copied
	}
	return value
}

// LessonsDifference is the first difference between a case's expected page and the page the port answered; "" when
// they are equal. Instants compare as instants, a confidence exactly, a block text byte for byte.
//
// Contract 1.10: each lesson's measuredBenefit compares as an estimate does (rates, differences and bounds within
// BenefitTolerance, everything else exactly); the rest of the page as before.
func LessonsDifference(c *LessonsCase, page ae.LessonsPage) string {
	expected := canonicalInstants(c.Expected)
	actual := canonicalInstants(AsWritten(LessonsPageJSON(page, c.IncludeMeasuredBenefit)))
	expectedBenefit, expectedPage := splitBenefit(expected)
	actualBenefit, actualPage := splitBenefit(actual)
	if d := Difference(expectedPage, actualPage, "$", Options{IgnoreKind: true}); d != "" {
		return d
	}
	return Difference(expectedBenefit, actualBenefit, "$.lessons[*].measuredBenefit",
		Options{Tolerance: BenefitTolerance, IgnoreKind: true})
}

// splitBenefit is a page's lessons' measuredBenefit members, in order (an absent one as the string "absent"), and the
// page without them.
func splitBenefit(page any) ([]any, any) {
	o, ok := page.(*ae.Object)
	if !ok {
		return nil, page
	}
	benefits := []any{}
	copied := ae.NewObject()
	for _, m := range o.Members() {
		if m.Key != "lessons" {
			copied.Set(m.Key, m.Value)
			continue
		}
		lessons := []any{}
		for _, item := range list(m.Value) {
			lesson, ok := item.(*ae.Object)
			if !ok {
				lessons = append(lessons, item)
				continue
			}
			without := ae.NewObject()
			benefit := any("absent")
			for _, member := range lesson.Members() {
				if member.Key == "measuredBenefit" {
					benefit = member.Value
					continue
				}
				without.Set(member.Key, member.Value)
			}
			benefits = append(benefits, benefit)
			lessons = append(lessons, without)
		}
		copied.Set("lessons", lessons)
	}
	return benefits, copied
}

// ---- Contract 1.14's lessons-html fixtures -------------------------------------------------------------------------

// LessonsPageOf reads a lessons page in the lessons-view expected.json form with its scope (a lessons-html
// page.json).
func LessonsPageOf(value any) (ae.LessonsPage, error) {
	o := object(value)
	at := func(v any) (ae.Instant, error) { return ae.ParseInstant(text(v)) }
	generated, err := at(get(o, "generatedAt"))
	if err != nil {
		return ae.LessonsPage{}, err
	}
	page := ae.LessonsPage{Scope: ScopeOf(get(o, "scope")), Outcome: ae.LessonsOutcome(text(get(o, "outcome"))),
		Lessons: []ae.Lesson{}, GeneratedAt: generated, OutOfScopeDropped: int(integer(get(o, "outOfScopeDropped")))}
	page.HasMore, _ = get(o, "hasMore").(bool)
	page.InjectionsTracked, _ = get(o, "injectionsTracked").(bool)
	for _, item := range list(get(o, "lessons")) {
		l := object(item)
		id, err := ae.ParseUUID(text(get(l, "experienceId")))
		if err != nil {
			return ae.LessonsPage{}, err
		}
		created, err := at(get(l, "createdAt"))
		if err != nil {
			return ae.LessonsPage{}, err
		}
		updated, err := at(get(l, "updatedAt"))
		if err != nil {
			return ae.LessonsPage{}, err
		}
		lesson := ae.Lesson{ExperienceID: id, TaskID: text(get(l, "taskId")), Status: ae.ExperienceStatus(text(get(l, "status"))),
			Confidence: Number(get(l, "confidence")), CreatedAt: created, UpdatedAt: updated, Checks: []ae.LessonCheck{},
			BlockText: text(get(l, "blockText")), InjectionCount: integer(get(l, "injectionCount"))}
		lesson.IsEligible, _ = get(l, "isEligible").(bool)
		if code := optionalText(get(l, "ineligibility")); code != nil {
			lesson.Ineligibility = ae.LessonIneligibility(*code)
		}
		if reason := optionalText(get(l, "ineligibleReason")); reason != nil {
			lesson.IneligibleReason = *reason
		}
		if injected := get(l, "lastInjectedAt"); injected != nil {
			instant, err := at(injected)
			if err != nil {
				return ae.LessonsPage{}, err
			}
			lesson.LastInjectedAt = &instant
		}
		for _, c := range list(get(l, "checks")) {
			check := object(c)
			lesson.Checks = append(lesson.Checks, ae.LessonCheck{CheckID: text(get(check, "checkId")),
				EvidenceKind: text(get(check, "evidenceKind")), Result: ae.CheckResult(text(get(check, "result")))})
		}
		page.Lessons = append(page.Lessons, lesson)
	}
	return page, nil
}
