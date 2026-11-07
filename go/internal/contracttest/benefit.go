package contracttest

import (
	"fmt"
	"path/filepath"
	"strconv"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.9's benefit-estimate, holdout-outcomes and model-metadata fixtures --------------------------------

// The contract 1.9 fixture families.
const (
	BenefitEstimateFamily = "benefit-estimate"
	HoldoutOutcomesFamily = "holdout-outcomes"
	ModelMetadataFamily   = "model-metadata"
)

// BenefitTolerance is the absolute tolerance the estimate's rates, differences and bounds compare within.
const BenefitTolerance = 1e-12

// OutcomeRowOf reads one outcome row ({runId, arm, experienceId, assignedAt, runOutcome, runModel, lessonModel}).
func OutcomeRowOf(value any) ae.HoldoutOutcomeRow {
	o := object(value)
	row := ae.HoldoutOutcomeRow{
		RunID:        must(ae.ParseUUID(text(get(o, "runId")))),
		Arm:          ae.HoldoutArm(text(get(o, "arm"))),
		ExperienceID: must(ae.ParseUUID(text(get(o, "experienceId")))),
		AssignedAt:   must(ae.ParseInstant(text(get(o, "assignedAt")))),
		RunModel:     optionalText(get(o, "runModel")),
		LessonModel:  optionalText(get(o, "lessonModel")),
	}
	if outcome := optionalText(get(o, "runOutcome")); outcome != nil {
		status := ae.VerificationStatus(*outcome)
		row.RunOutcome = &status
	}
	return row
}

// OutcomeRowJSON is an outcome row in the fixtures' form.
func OutcomeRowJSON(row ae.HoldoutOutcomeRow) *ae.Object {
	var outcome any
	if row.RunOutcome != nil {
		outcome = string(*row.RunOutcome)
	}
	return ae.NewObject(
		ae.Member{Key: "runId", Value: row.RunID.String()},
		ae.Member{Key: "arm", Value: string(row.Arm)},
		ae.Member{Key: "experienceId", Value: row.ExperienceID.String()},
		ae.Member{Key: "assignedAt", Value: row.AssignedAt.String()},
		ae.Member{Key: "runOutcome", Value: outcome},
		ae.Member{Key: "runModel", Value: optional(row.RunModel)},
		ae.Member{Key: "lessonModel", Value: optional(row.LessonModel)},
	)
}

func optional(value *string) any {
	if value == nil {
		return nil
	}
	return *value
}

func optionalNumber(value *float64) any {
	if value == nil {
		return nil
	}
	return *value
}

// BenefitCase is one benefit-estimate case.
type BenefitCase struct {
	Name     string
	Rows     []ae.HoldoutOutcomeRow
	Options  ae.BenefitOptions
	Expected *ae.Object
}

// LoadBenefitCase reads a benefit-estimate case.
func LoadBenefitCase(name string) BenefitCase {
	dir := filepath.Join(Fixtures, BenefitEstimateFamily, name)
	rows := object(MustReadJSON(filepath.Join(dir, "rows.json")))
	options := object(MustReadJSON(filepath.Join(dir, "options.json")))
	c := BenefitCase{Name: name, Expected: object(MustReadJSON(filepath.Join(dir, "expected.json")))}
	for _, item := range list(get(rows, "rows")) {
		c.Rows = append(c.Rows, OutcomeRowOf(item))
	}
	split, _ := get(options, "splitByModel").(bool)
	c.Options = ae.BenefitOptions{MinimumRunsPerArm: int(integer(get(options, "minimumRunsPerArm"))), SplitByModel: split}
	return c
}

// EstimateJSON is an estimate in the benefit-estimate expected.json form (without Tracked and Outcome).
func EstimateJSON(estimate ae.BenefitEstimate) *ae.Object {
	lessons := []any{}
	for _, lesson := range estimate.Lessons {
		lessons = append(lessons, LessonBenefitJSON(lesson))
	}
	var models any
	if estimate.ByConsumingModel != nil {
		items := []any{}
		for _, model := range estimate.ByConsumingModel {
			items = append(items, ae.NewObject(
				ae.Member{Key: "model", Value: model.Model},
				ae.Member{Key: "comparison", Value: ComparisonJSON(model.Comparison)}))
		}
		models = items
	}
	return ae.NewObject(
		ae.Member{Key: "minimumRunsPerArm", Value: estimate.MinimumRunsPerArm},
		ae.Member{Key: "overall", Value: ComparisonJSON(estimate.Overall)},
		ae.Member{Key: "lessons", Value: lessons},
		ae.Member{Key: "byConsumingModel", Value: models},
	)
}

// FleetEstimateJSON is EstimateJSON with "tracked" and "outcome": the cross-language fleet estimate's form.
func FleetEstimateJSON(estimate ae.BenefitEstimate) *ae.Object {
	o := EstimateJSON(estimate)
	o.Set("tracked", estimate.Tracked)
	o.Set("outcome", string(estimate.Outcome))
	return o
}

// ComparisonJSON is one comparison in the fixtures' form.
func ComparisonJSON(c ae.BenefitComparison) *ae.Object {
	arm := func(a ae.BenefitArm) *ae.Object {
		return ae.NewObject(ae.Member{Key: "runs", Value: a.Runs}, ae.Member{Key: "successes", Value: a.Successes},
			ae.Member{Key: "rate", Value: optionalNumber(a.Rate)})
	}
	return ae.NewObject(
		ae.Member{Key: "treatment", Value: arm(c.Treatment)},
		ae.Member{Key: "holdout", Value: arm(c.Holdout)},
		ae.Member{Key: "difference", Value: optionalNumber(c.Difference)},
		ae.Member{Key: "lower", Value: optionalNumber(c.Lower)},
		ae.Member{Key: "upper", Value: optionalNumber(c.Upper)},
		ae.Member{Key: "verdict", Value: string(c.Verdict)},
		ae.Member{Key: "unverified", Value: c.Unverified},
		ae.Member{Key: "pending", Value: c.Pending},
		ae.Member{Key: "mixedArm", Value: c.MixedArm},
	)
}

// EstimateDifference is the first difference between an expected estimate and an actual one ("" when equal): the
// non-count numbers within BenefitTolerance, everything else exactly.
func EstimateDifference(expected any, actual *ae.Object) string {
	return Difference(expected, AsWritten(actual), "$", Options{Tolerance: BenefitTolerance, IgnoreKind: true})
}

// AssignmentColumns are agent_experience.experience_holdout_assignments' columns, in table order.
var AssignmentColumns = []string{"run_id", "experience_id", "revision", "arm", "tenant_id", "application_id",
	"project_id", "team_id", "agent_id", "user_id", "assigned_at"}

// AssignmentRow is one seeded assignment row: Raw as written (PostgreSQL's text form), the rest read.
type AssignmentRow struct {
	Raw          *ae.Object
	RunID        ae.UUID
	ExperienceID ae.UUID
	Revision     int64
	Arm          ae.HoldoutArm
	Scope        ae.Scope
	AssignedAt   ae.Instant
}

// OutcomesCase is one holdout-outcomes case.
type OutcomesCase struct {
	Name        string
	Records     []ae.ExperienceRecord
	Assignments []AssignmentRow
	// Erased are seed.json's erasures, each {experienceId, scope, deletedAt}, applied in order after the rows.
	Erased []*ae.Object
	// Scope is the scope read and also the caller's bounds.
	Scope ae.Scope
	Limit int
	// Pages are expected.json's pages, each {after, rows}.
	Pages []any
}

// LoadOutcomesCase reads a holdout-outcomes case.
func LoadOutcomesCase(name string) (*OutcomesCase, error) {
	dir := filepath.Join(Fixtures, HoldoutOutcomesFamily, name)
	seed := object(MustReadJSON(filepath.Join(dir, "seed.json")))
	query := object(MustReadJSON(filepath.Join(dir, "query.json")))
	expected := object(MustReadJSON(filepath.Join(dir, "expected.json")))
	c := &OutcomesCase{Name: name, Scope: ScopeOf(get(query, "scope")), Limit: int(integer(get(query, "limit"))),
		Pages: list(get(expected, "pages"))}
	for _, item := range list(get(seed, "records")) {
		o := object(item)
		record, err := ae.FromColumns(get(o, "columns"), get(o, "payload"))
		if err != nil {
			return nil, fmt.Errorf("%s: %w", name, err)
		}
		c.Records = append(c.Records, record)
	}
	for _, item := range list(get(seed, "erased")) {
		c.Erased = append(c.Erased, object(item))
	}
	assignments, err := assignmentRows(get(seed, "assignments"))
	if err != nil {
		return nil, err
	}
	c.Assignments = assignments
	return c, nil
}

// assignmentRows reads a seed's assignment rows (PostgreSQL's text form); none for an absent list.
func assignmentRows(value any) ([]AssignmentRow, error) {
	var rows []AssignmentRow
	for _, item := range list(value) {
		row := object(item)
		revision, err := strconv.ParseInt(text(get(row, "revision")), 10, 64)
		if err != nil {
			return nil, err
		}
		at, err := ParseTimestamptz(text(get(row, "assigned_at")))
		if err != nil {
			return nil, err
		}
		rows = append(rows, AssignmentRow{Raw: row,
			RunID:        must(ae.ParseUUID(text(get(row, "run_id")))),
			ExperienceID: must(ae.ParseUUID(text(get(row, "experience_id")))),
			Revision:     revision, Arm: ae.HoldoutArm(text(get(row, "arm"))), AssignedAt: at,
			Scope: ae.Scope{TenantID: text(get(row, "tenant_id")), ApplicationID: text(get(row, "application_id")),
				ProjectID: text(get(row, "project_id")), TeamID: optionalText(get(row, "team_id")),
				AgentID: optionalText(get(row, "agent_id")), UserID: optionalText(get(row, "user_id"))}})
	}
	return rows, nil
}

// LessonBenefitJSON is one lesson of an estimate in the benefit-estimate expected.json form.
func LessonBenefitJSON(lesson ae.LessonBenefit) *ae.Object {
	var pairs any
	if lesson.ByModelPair != nil {
		items := []any{}
		for _, pair := range lesson.ByModelPair {
			items = append(items, ae.NewObject(
				ae.Member{Key: "lessonModel", Value: pair.LessonModel},
				ae.Member{Key: "runModel", Value: pair.RunModel},
				ae.Member{Key: "comparison", Value: ComparisonJSON(pair.Comparison)}))
		}
		pairs = items
	}
	return ae.NewObject(
		ae.Member{Key: "experienceId", Value: lesson.ExperienceID.String()},
		ae.Member{Key: "comparison", Value: ComparisonJSON(lesson.Comparison)},
		ae.Member{Key: "byModelPair", Value: pairs})
}

// CursorOf reads a page's "after" (nil for null).
func CursorOf(value any) *ae.HoldoutOutcomeCursor {
	o := object(value)
	if o == nil {
		return nil
	}
	return &ae.HoldoutOutcomeCursor{RunID: must(ae.ParseUUID(text(get(o, "runId")))),
		ExperienceID: must(ae.ParseUUID(text(get(o, "experienceId"))))}
}

// PageJSON is one page in expected.json's form.
func PageJSON(after *ae.HoldoutOutcomeCursor, rows []ae.HoldoutOutcomeRow) *ae.Object {
	var cursor any
	if after != nil {
		cursor = ae.NewObject(ae.Member{Key: "runId", Value: after.RunID.String()},
			ae.Member{Key: "experienceId", Value: after.ExperienceID.String()})
	}
	items := []any{}
	for _, row := range rows {
		items = append(items, OutcomeRowJSON(row))
	}
	return ae.NewObject(ae.Member{Key: "after", Value: cursor}, ae.Member{Key: "rows", Value: items})
}

// ReadPages pages through a reader as the fixture does (each page after the previous page's last row, until one is
// not full) and returns the pages in expected.json's form.
func ReadPages(read func(after *ae.HoldoutOutcomeCursor) ([]ae.HoldoutOutcomeRow, error), limit int) ([]any, error) {
	var pages []any
	var after *ae.HoldoutOutcomeCursor
	for range 100 {
		rows, err := read(after)
		if err != nil {
			return nil, err
		}
		pages = append(pages, PageJSON(after, rows))
		if len(rows) < limit {
			return pages, nil
		}
		last := rows[len(rows)-1]
		after = &ae.HoldoutOutcomeCursor{RunID: last.RunID, ExperienceID: last.ExperienceID}
	}
	return nil, fmt.Errorf("more than 100 pages")
}

// PagesDifference compares pages read with the case's expected pages: instants as instants (to the microsecond),
// everything else exactly.
func PagesDifference(c *OutcomesCase, pages []any) string {
	return Difference(AsWritten(c.Pages), AsWritten(pages), "$.pages", Options{})
}
