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
}

// Query is the case's query.
func (c *LessonsCase) Query() ae.LessonQuery {
	include := c.IncludeIneligible
	return ae.LessonQuery{Statuses: c.Statuses, Limit: c.Limit, IncludeIneligible: &include}
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
	if c.Now, err = ae.ParseInstant(text(get(query, "now"))); err != nil {
		return nil, err
	}
	return c, nil
}

// LessonsPageJSON is a page in the fixtures' form.
func LessonsPageJSON(page ae.LessonsPage) *ae.Object {
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
		lessons = append(lessons, ae.NewObject(
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
		))
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
func LessonsDifference(c *LessonsCase, page ae.LessonsPage) string {
	expected := canonicalInstants(c.Expected)
	actual := canonicalInstants(AsWritten(LessonsPageJSON(page)))
	return Difference(expected, actual, "$", Options{IgnoreKind: true})
}
