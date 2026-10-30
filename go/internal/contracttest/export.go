package contracttest

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.14's lesson-export fixtures ----------------------------------------------------------------------

// LessonExportFamily is the lesson-export fixture family's directory name.
const LessonExportFamily = "lesson-export"

// LessonExportCase is one lesson-export case: seed.json, options.json, previous.md, expected.json and expected.md.
type LessonExportCase struct {
	Name        string
	Records     []ae.ExperienceRecord
	Assignments []AssignmentRow
	// Scope is exported; AuthorizedScope is what the identity's bounds cover.
	Scope, AuthorizedScope ae.Scope
	Now                    ae.Instant
	// MinimumRunsPerArm is nil for the policy's default.
	MinimumRunsPerArm *int
	// Previous is the file before the export; nil when there was none.
	Previous []byte
	Expected *ae.Object
	// ExpectedFile is expected.md; empty when no file was written.
	ExpectedFile []byte
}

// LoadLessonExportCase reads a lesson-export case.
func LoadLessonExportCase(name string) (*LessonExportCase, error) {
	dir := filepath.Join(Fixtures, LessonExportFamily, name)
	seed := object(MustReadJSON(filepath.Join(dir, "seed.json")))
	options := object(MustReadJSON(filepath.Join(dir, "options.json")))
	c := &LessonExportCase{Name: name, Scope: ScopeOf(get(options, "scope")),
		AuthorizedScope: ScopeOf(get(options, "authorizedScope")),
		Expected:        object(MustReadJSON(filepath.Join(dir, "expected.json")))}
	for _, item := range list(get(seed, "records")) {
		o := object(item)
		record, err := ae.FromColumns(get(o, "columns"), get(o, "payload"))
		if err != nil {
			return nil, fmt.Errorf("%s: %w", name, err)
		}
		c.Records = append(c.Records, record)
	}
	var err error
	if c.Assignments, err = assignmentRows(get(seed, "assignments")); err != nil {
		return nil, err
	}
	if c.Now, err = ae.ParseInstant(text(get(options, "now"))); err != nil {
		return nil, err
	}
	if minimum := get(options, "minimumRunsPerArm"); minimum != nil {
		m := int(integer(minimum))
		c.MinimumRunsPerArm = &m
	}
	if previous, _ := get(options, "hasPreviousFile").(bool); previous {
		if c.Previous, err = os.ReadFile(filepath.Join(dir, "previous.md")); err != nil {
			return nil, err
		}
	}
	if c.ExpectedFile, err = os.ReadFile(filepath.Join(dir, "expected.md")); err != nil {
		return nil, err
	}
	return c, nil
}

// BenefitOptions are the options the case exports with: nil for the policy's minimum.
func (c *LessonExportCase) BenefitOptions() *ae.BenefitOptions {
	if c.MinimumRunsPerArm == nil {
		return nil
	}
	return &ae.BenefitOptions{MinimumRunsPerArm: *c.MinimumRunsPerArm}
}

// Identity is the case's identity: its scope, under its authorized scope's bounds.
func (c *LessonExportCase) Identity() ae.Identity {
	bounds := ae.BoundsOf(c.AuthorizedScope)
	return ae.Identity{Principal: "contract-principal", Scope: c.Scope, Bounds: &bounds}
}

// LessonExportJSON is an export result in expected.json's form.
func LessonExportJSON(result ae.LessonExportResult) *ae.Object {
	lessons := []any{}
	for _, lesson := range result.Lessons {
		lessons = append(lessons, ae.NewObject(
			ae.Member{Key: "experienceId", Value: lesson.ExperienceID.String()},
			ae.Member{Key: "revision", Value: json.Number(strconv.FormatInt(lesson.Revision, 10))},
			ae.Member{Key: "taskId", Value: lesson.TaskID},
			ae.Member{Key: "lessonText", Value: lesson.LessonText},
			ae.Member{Key: "measuredBenefit", Value: ComparisonJSON(lesson.MeasuredBenefit)},
			ae.Member{Key: "exportedAt", Value: lesson.ExportedAt.String()},
		))
	}
	var sync any
	if result.Sync != nil {
		sync = ae.NewObject(
			ae.Member{Key: "added", Value: json.Number(strconv.Itoa(result.Sync.Added))},
			ae.Member{Key: "updated", Value: json.Number(strconv.Itoa(result.Sync.Updated))},
			ae.Member{Key: "removed", Value: json.Number(strconv.Itoa(result.Sync.Removed))})
	}
	return ae.NewObject(
		ae.Member{Key: "outcome", Value: string(result.Outcome)},
		ae.Member{Key: "holdoutTracked", Value: result.HoldoutTracked},
		ae.Member{Key: "exportedAt", Value: result.ExportedAt.String()},
		ae.Member{Key: "lessons", Value: lessons},
		ae.Member{Key: "sync", Value: sync},
	)
}

// LessonExportDifference is the first difference between the case's expected.json and result; "" when equal. The
// measured comparison compares as an estimate does (within BenefitTolerance), everything else exactly.
func LessonExportDifference(c *LessonExportCase, result ae.LessonExportResult) string {
	return Difference(c.Expected, AsWritten(LessonExportJSON(result)), "$",
		Options{Tolerance: BenefitTolerance, IgnoreKind: true})
}
