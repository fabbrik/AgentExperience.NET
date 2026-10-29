//go:build postgres

package pgtest_test

// Contract 1.7's lessons-view fixtures through the store's ListLessonRecords (the embedded list-experience-lessons.sql,
// run as written) and the lessons view over it, and what the store does when the injections cannot be read.

import (
	"context"
	"reflect"
	"testing"

	"github.com/jackc/pgx/v5"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
	"github.com/fabbrik/AgentExperience.NET/go/postgres"
	"github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest"
)

// seedLessons inserts a case's records as the application role under their own scope's bounds and its injections as
// the schema's owner.
func seedLessons(t *testing.T, store *postgres.Store, owner *pgx.Conn, c *ct.LessonsCase) {
	t.Helper()
	ctx := context.Background()
	for i := range c.Records {
		if err := store.InsertRecord(ctx, ae.BoundsOf(c.Records[i].Scope), &c.Records[i]); err != nil {
			t.Fatal(err)
		}
	}
	for _, row := range c.Injections {
		values := make([]any, len(ct.InjectionColumns))
		for i, column := range ct.InjectionColumns {
			values[i], _ = row.Raw.Get(column)
		}
		if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_injections (experience_id, "+
			"tenant_id, application_id, project_id, team_id, agent_id, user_id, last_injected_at, "+
			"injection_count) VALUES ($1::text::uuid, $2, $3, $4, $5, $6, $7, $8::text::timestamptz, "+
			"$9::text::bigint)", values...); err != nil {
			t.Fatal(err)
		}
	}
	// Contract 1.10: the holdout's rows, from their PostgreSQL text form, as the owner.
	for _, row := range c.Assignments {
		values := make([]any, len(ct.AssignmentColumns))
		for i, column := range ct.AssignmentColumns {
			values[i], _ = row.Raw.Get(column)
		}
		if _, err := owner.Exec(ctx, "INSERT INTO agent_experience.experience_holdout_assignments (run_id, "+
			"experience_id, revision, arm, tenant_id, application_id, project_id, team_id, agent_id, user_id, "+
			"assigned_at) VALUES ($1::text::uuid, $2::text::uuid, $3::text::bigint, $4, $5, $6, $7, $8, $9, $10, "+
			"$11::text::timestamptz)", values...); err != nil {
			t.Fatal(err)
		}
	}
}

func lessonsView(t *testing.T, store ae.LessonsReader, now ae.Instant, options ae.LessonsOptions) *ae.Lessons {
	t.Helper()
	options.Clock = func() ae.Instant { return now }
	lessons, err := ae.NewLessons(store, options)
	if err != nil {
		t.Fatal(err)
	}
	return lessons
}

func TestLessonsView(t *testing.T) {
	admin := requireServer(t)
	for _, name := range ct.Cases(ct.LessonsFamily) {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadLessonsCase(name)
			if err != nil {
				t.Fatal(err)
			}
			database := createDatabase(t, admin, "lessons_"+name)
			store := newStore(t, admin, database)
			owner := ownerConn(t, admin, database)
			seedLessons(t, store, owner, c)
			if c.HoldoutReadRevoked {
				// Contract 1.10: the holdout read fails, and the list with it, as a store failure.
				if _, err := owner.Exec(context.Background(), "REVOKE SELECT ON "+
					"agent_experience.experience_holdout_assignments FROM "+pgtest.ApplicationRole); err != nil {
					t.Fatal(err)
				}
			}
			options := c.Options()
			page, err := lessonsView(t, store, c.Now, options).List(context.Background(),
				ae.BoundsOf(c.Scope), c.Scope, c.Query())
			if class := c.ExpectedErrorClass(); class != "" {
				if err == nil || ae.ClassifyError(err) != class {
					t.Fatalf("the list answered %v (%s), not a failure of class %s", err, ae.ClassifyError(err), class)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if difference := ct.LessonsDifference(c, page); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

func loadedStore(t *testing.T, admin server, label string) (*ct.LessonsCase, *postgres.Store, *pgx.Conn) {
	t.Helper()
	c, err := ct.LoadLessonsCase("newest-first-with-eligibility")
	if err != nil {
		t.Fatal(err)
	}
	database := createDatabase(t, admin, label)
	store := newStore(t, admin, database)
	owner := ownerConn(t, admin, database)
	seedLessons(t, store, owner, c)
	return c, store, owner
}

func TestTheLessonsReadIsTheScopesOwnRecordsNewestFirstWithStatusesAndLimit(t *testing.T) {
	admin := requireServer(t)
	c, store, _ := loadedStore(t, admin, "lessons_read")
	ctx := context.Background()
	bounds := ae.BoundsOf(c.Scope)
	read, err := store.ListLessonRecords(ctx, bounds, c.Scope, nil, 500)
	if err != nil {
		t.Fatal(err)
	}
	if !read.InjectionsTracked || read.Answered != len(read.Records) || len(read.Records) == 0 {
		t.Fatalf("tracked %v, answered %d, %d records", read.InjectionsTracked, read.Answered, len(read.Records))
	}
	injected, never := 0, 0
	for i, held := range read.Records {
		if !held.Record.Scope.Equal(c.Scope) {
			t.Fatalf("a record of another scope")
		}
		if i > 0 && read.Records[i-1].Record.CreatedAt.Before(held.Record.CreatedAt) {
			t.Fatal("not newest first")
		}
		if held.Injection != nil {
			injected++
		} else {
			never++
		}
	}
	if injected == 0 || never == 0 {
		t.Fatalf("%d injected, %d never", injected, never)
	}
	read, err = store.ListLessonRecords(ctx, bounds, c.Scope, []ae.ExperienceStatus{ae.StatusStale}, 1)
	if err != nil || len(read.Records) != 1 || read.Records[0].Record.Status != ae.StatusStale {
		t.Fatalf("%v %d", err, len(read.Records))
	}
}

func TestTheLessonsReadRefusesAScopeOutsideTheBoundsAndInvalidReadsWithoutAStatement(t *testing.T) {
	admin := requireServer(t)
	c, store, _ := loadedStore(t, admin, "lessons_refused")
	ctx := context.Background()
	other := c.Scope
	other.AgentID = ae.Ptr("someone-else")
	if _, err := store.ListLessonRecords(ctx, ae.BoundsOf(c.Scope), other, nil, 10); err != ae.ErrAuthorizationDenied {
		t.Fatalf("a scope outside the bounds: %v", err)
	}
	for _, limit := range []int{0, 501} {
		if _, err := store.ListLessonRecords(ctx, ae.BoundsOf(c.Scope), c.Scope, nil, limit); err == nil {
			t.Errorf("limit %d was accepted", limit)
		}
	}
	for _, statuses := range [][]ae.ExperienceStatus{{}, {"Nope"}} {
		if _, err := store.ListLessonRecords(ctx, ae.BoundsOf(c.Scope), c.Scope, statuses, 10); err == nil {
			t.Errorf("statuses %v were accepted", statuses)
		}
	}
	// Through the view: a Denied page, no error.
	page, err := lessonsView(t, store, c.Now, ae.LessonsOptions{}).List(ctx, ae.BoundsOf(c.Scope), other,
		ae.LessonQuery{})
	if err != nil || page.Outcome != ae.LessonsDenied || len(page.Lessons) != 0 {
		t.Fatalf("%v %d %v", page.Outcome, len(page.Lessons), err)
	}
	// A narrower bound than the scope listed denies too (the tenant's other agent).
	narrow := ae.BoundsOf(other)
	page, err = lessonsView(t, store, c.Now, ae.LessonsOptions{}).List(ctx, narrow, c.Scope, ae.LessonQuery{})
	if err != nil || page.Outcome != ae.LessonsDenied {
		t.Fatalf("%v %v", page.Outcome, err)
	}
}

// notTracked checks a page read when the injections cannot be: every lesson listed, none with an injection.
func notTracked(t *testing.T, store *postgres.Store, c *ct.LessonsCase, statuses []ae.ExperienceStatus) {
	t.Helper()
	ctx := context.Background()
	lessons := lessonsView(t, store, c.Now, ae.LessonsOptions{})
	page, err := lessons.List(ctx, ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{Statuses: statuses})
	if err != nil {
		t.Fatal(err)
	}
	if page.Outcome != ae.LessonsFound || page.InjectionsTracked || len(page.Lessons) == 0 {
		t.Fatalf("%v tracked %v, %d lessons", page.Outcome, page.InjectionsTracked, len(page.Lessons))
	}
	for _, lesson := range page.Lessons {
		if lesson.LastInjectedAt != nil || lesson.InjectionCount != 0 {
			t.Fatal("an injection was shown when they could not be read")
		}
	}
}

func TestWithoutTheInjectionsGrantThePageIsReadWithoutThemAndNotTracked(t *testing.T) {
	admin := requireServer(t)
	c, store, owner := loadedStore(t, admin, "lessons_no_grant")
	if _, err := owner.Exec(context.Background(), "REVOKE SELECT ON agent_experience.experience_injections FROM "+
		pgtest.ApplicationRole); err != nil {
		t.Fatal(err)
	}
	notTracked(t, store, c, nil)
	notTracked(t, store, c, []ae.ExperienceStatus{ae.StatusValidated, ae.StatusReinforced})
	read, err := store.ListLessonRecords(context.Background(), ae.BoundsOf(c.Scope), c.Scope, nil, 10)
	if err != nil || read.InjectionsTracked || len(read.Records) == 0 {
		t.Fatalf("tracked %v, %d records, %v", read.InjectionsTracked, len(read.Records), err)
	}
}

func TestWithoutTheInjectionsTableThePageIsReadWithoutThemAndNotTracked(t *testing.T) {
	admin := requireServer(t)
	c, store, owner := loadedStore(t, admin, "lessons_no_table")
	if _, err := owner.Exec(context.Background(), "DROP TABLE agent_experience.experience_injections"); err != nil {
		t.Fatal(err)
	}
	notTracked(t, store, c, nil)
}

func TestWithoutTheContract14GrantsThePageIsNotTracked(t *testing.T) {
	admin := requireServer(t)
	c, err := ct.LoadLessonsCase("newest-first-with-eligibility")
	if err != nil {
		t.Fatal(err)
	}
	database := pgtest.CreateDatabaseWith(t, admin, "lessons_1_0_only",
		pgtest.PrivilegesOf(t, pgtest.ApplicationRole, pgtest.PrivilegesFile))
	store := newStore(t, admin, database)
	for i := range c.Records {
		if err := store.InsertRecord(context.Background(), ae.BoundsOf(c.Records[i].Scope), &c.Records[i]); err != nil {
			t.Fatal(err)
		}
	}
	notTracked(t, store, c, nil)
}

func TestTheLessonsViewNeverWritesAndHoldsOptionsAgainstTheStoredRecords(t *testing.T) {
	admin := requireServer(t)
	c, store, owner := loadedStore(t, admin, "lessons_options")
	ctx := context.Background()
	snapshot := func() []string {
		var rows []string
		for _, query := range []string{
			"SELECT experience_id::text || ':' || revision::text || ':' || updated_at::text FROM agent_experience.experience_records ORDER BY 1",
			"SELECT experience_id::text || ':' || injection_count::text || ':' || last_injected_at::text FROM agent_experience.experience_injections ORDER BY 1",
			"SELECT event_id::text FROM agent_experience.lifecycle_events ORDER BY 1",
		} {
			result, err := owner.Query(ctx, query)
			if err != nil {
				t.Fatal(err)
			}
			for result.Next() {
				var row string
				if err := result.Scan(&row); err != nil {
					t.Fatal(err)
				}
				rows = append(rows, row)
			}
			result.Close()
		}
		return rows
	}
	before := snapshot()
	small := lessonsView(t, store, c.Now, ae.LessonsOptions{BlockOptions: ae.BlockOptions{MaxBytes: 50}})
	page, err := small.List(ctx, ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil {
		t.Fatal(err)
	}
	over := 0
	for _, lesson := range page.Lessons {
		if lesson.Ineligibility == ae.IneligibleOverByteBudget {
			over++
		}
	}
	if over == 0 {
		t.Fatal("no lesson was over a 50-byte budget")
	}
	if _, err := lessonsView(t, store, c.Now, ae.LessonsOptions{ExcludeModelAuthored: true}).List(ctx,
		ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{}); err != nil {
		t.Fatal(err)
	}
	if after := snapshot(); !reflect.DeepEqual(before, after) {
		t.Fatalf("the view changed the database:\n%v\n%v", before, after)
	}
}

func TestAModelAuthoredLessonIsIneligibleWhenTheSettingsExcludeThemAndBounded(t *testing.T) {
	admin := requireServer(t)
	c, err := ct.LoadLessonsCase("newest-first-with-eligibility")
	if err != nil {
		t.Fatal(err)
	}
	database := createDatabase(t, admin, "lessons_model")
	store := newStore(t, admin, database)
	ctx := context.Background()
	var model ae.ExperienceRecord
	for _, record := range c.Records {
		if record.Scope.Equal(c.Scope) && record.Status == ae.StatusReinforced {
			model = record
		}
	}
	reflection := *model.Reflection
	reflection.Authorship = ae.AuthorshipModel
	model.Reflection = &reflection
	if err := store.InsertRecord(ctx, ae.BoundsOf(model.Scope), &model); err != nil {
		t.Fatal(err)
	}
	for _, exclude := range []bool{false, true} {
		page, err := lessonsView(t, store, c.Now, ae.LessonsOptions{ExcludeModelAuthored: exclude}).List(ctx,
			ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
		if err != nil || len(page.Lessons) != 1 {
			t.Fatalf("%v %d", err, len(page.Lessons))
		}
		lesson := page.Lessons[0]
		if exclude && (lesson.Ineligibility != ae.IneligibleModelAuthored || lesson.IsEligible) {
			t.Fatalf("excluded: %s %v", lesson.Ineligibility, lesson.IsEligible)
		}
		if !exclude && !lesson.IsEligible {
			t.Fatalf("included: %s", lesson.Ineligibility)
		}
	}
}
