package inmemory

// Contract 1.7 on the in-memory store: the lessons-view fixtures (they need no database features, so the pages are
// the PostgreSQL store's), and the rules the fixtures do not state.

import (
	"context"
	"errors"
	"net"
	"reflect"
	"strconv"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// seeded is a store holding a lessons-view case: its records, and its injections recorded as the case names them
// (RecordInjection once per count, at the row's instant), the store's clock being the case's.
func seeded(t *testing.T, c *ct.LessonsCase) *Store {
	t.Helper()
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	for i := range c.Records {
		if err := store.InsertRecord(ctx, ae.BoundsOf(c.Records[i].Scope), &c.Records[i]); err != nil {
			t.Fatal(err)
		}
	}
	now := c.Now
	store.Clock = func() ae.Instant { return now }
	// Contract 1.10: the holdout's rows, each as recorded (every seeded instant is before the case's clock).
	for _, row := range c.Assignments {
		if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(row.Scope), row.Scope, ae.HoldoutAssignment{
			RunID: row.RunID, Arm: row.Arm, AssignedAt: row.AssignedAt,
			Records: []ae.RunExposure{{ExperienceID: row.ExperienceID, Revision: row.Revision}}}); err != nil {
			t.Fatal(err)
		}
	}
	for _, row := range c.Injections {
		for n := int64(0); n < row.InjectionCount; n++ {
			if err := store.RecordInjection(ctx, ae.BoundsOf(row.Scope), row.Scope, []ae.UUID{row.ExperienceID},
				row.LastInjectedAt); err != nil {
				t.Fatal(err)
			}
		}
	}
	return store
}

func TestTheLessonsViewFixturesGiveTheirPages(t *testing.T) {
	for _, name := range ct.Cases(ct.LessonsFamily) {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadLessonsCase(name)
			if err != nil {
				t.Fatal(err)
			}
			store := seeded(t, c)
			var reader ae.LessonsReader = store
			if c.HoldoutReadRevoked {
				// Contract 1.10: the holdout read fails; the list fails with it, as a store failure.
				reader = failingHoldout{store}
			}
			lessons, err := ae.NewLessons(reader, c.Options())
			if err != nil {
				t.Fatal(err)
			}
			page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, c.Query())
			if class := c.ExpectedErrorClass(); class != "" {
				if err == nil || ae.ClassifyError(err) != class {
					t.Fatalf("the list answered %v (%v), not a failure of class %s", err, ae.ClassifyError(err), class)
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

// failingHoldout is the store whose holdout read fails as a database failure does (a network error: Infrastructure).
type failingHoldout struct{ *Store }

func (failingHoldout) QueryHoldoutOutcomes(context.Context, ae.AuthorizationBounds, ae.Scope, int,
	*ae.HoldoutOutcomeCursor) ([]ae.HoldoutOutcomeRow, error) {
	return nil, &net.OpError{Op: "read", Net: "tcp", Err: errors.New("connection reset")}
}

func fixtureStore(t *testing.T) (*ct.LessonsCase, *Store) {
	t.Helper()
	c, err := ct.LoadLessonsCase("newest-first-with-eligibility")
	if err != nil {
		t.Fatal(err)
	}
	return c, seeded(t, c)
}

func TestListLessonRecordsAnswersTheScopesRecordsNewestFirstWithTheirInjections(t *testing.T) {
	c, store := fixtureStore(t)
	read, err := store.ListLessonRecords(context.Background(), ae.BoundsOf(c.Scope), c.Scope, nil, 500)
	if err != nil {
		t.Fatal(err)
	}
	if !read.InjectionsTracked || read.Answered != len(read.Records) || len(read.Records) == 0 {
		t.Fatalf("tracked %v, answered %d, %d records", read.InjectionsTracked, read.Answered, len(read.Records))
	}
	injected, never := 0, 0
	for i, held := range read.Records {
		if !held.Record.Scope.Equal(c.Scope) {
			t.Fatalf("a record of another scope: %v", held.Record.Scope)
		}
		if i > 0 {
			previous := read.Records[i-1].Record
			if previous.CreatedAt.Before(held.Record.CreatedAt) {
				t.Fatal("not newest first")
			}
		}
		if held.Injection != nil {
			injected++
		} else {
			never++
		}
	}
	if injected == 0 || never == 0 {
		t.Fatalf("%d injected, %d never: the case should hold both", injected, never)
	}

	// Statuses, and the limit.
	read, err = store.ListLessonRecords(context.Background(), ae.BoundsOf(c.Scope), c.Scope,
		[]ae.ExperienceStatus{ae.StatusStale}, 1)
	if err != nil || len(read.Records) != 1 || read.Records[0].Record.Status != ae.StatusStale {
		t.Fatalf("%v %d", err, len(read.Records))
	}
}

func TestTiesInCreatedAtAreOrderedByExperienceIDOrdinally(t *testing.T) {
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	var ids []ae.UUID
	for _, n := range []byte{3, 1, 2} {
		r := record(n, "task", "summary", "lesson", ae.StatusValidated, 0.9)
		ids = append(ids, r.ExperienceID)
		if err := store.InsertRecord(context.Background(), ae.BoundsOf(scope), &r); err != nil {
			t.Fatal(err)
		}
	}
	read, err := store.ListLessonRecords(context.Background(), ae.BoundsOf(scope), scope, nil, 10)
	if err != nil {
		t.Fatal(err)
	}
	var got []string
	for _, held := range read.Records {
		got = append(got, held.Record.ExperienceID.String())
	}
	want := []string{ids[1].String(), ids[2].String(), ids[0].String()}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%v, want %v", got, want)
	}
}

func TestListLessonRecordsHonoursTheBoundsAndRefusesInvalidReads(t *testing.T) {
	c, store := fixtureStore(t)
	ctx := context.Background()
	other := c.Scope
	other.AgentID = ae.Ptr("someone-else")
	if _, err := store.ListLessonRecords(ctx, ae.BoundsOf(c.Scope), other, nil, 10); !errors.Is(err, ErrAuthorizationDenied) {
		t.Fatalf("a scope outside the bounds: %v", err)
	}
	for name, test := range map[string]struct {
		statuses []ae.ExperienceStatus
		limit    int
	}{
		"limit 0":        {nil, 0},
		"limit 501":      {nil, 501},
		"empty statuses": {[]ae.ExperienceStatus{}, 10},
		"bad status":     {[]ae.ExperienceStatus{"Nope"}, 10},
	} {
		if _, err := store.ListLessonRecords(ctx, ae.BoundsOf(c.Scope), c.Scope, test.statuses, test.limit); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	done, cancel := context.WithCancel(ctx)
	cancel()
	if _, err := store.ListLessonRecords(done, ae.BoundsOf(c.Scope), c.Scope, nil, 10); err == nil {
		t.Fatal("a done context was accepted")
	}
}

func TestTheLessonsViewNeverWrites(t *testing.T) {
	c, store := fixtureStore(t)
	records, injections := store.Records(), store.Injections()
	lessons, err := ae.NewLessons(store, ae.LessonsOptions{})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{}); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(records, store.Records()) || !reflect.DeepEqual(injections, store.Injections()) {
		t.Fatal("listing the lessons changed the store")
	}
}

func TestTheViewReadsAScopeOutsideTheBoundsAsADeniedPage(t *testing.T) {
	c, store := fixtureStore(t)
	lessons, _ := ae.NewLessons(store, ae.LessonsOptions{})
	other := c.Scope
	other.AgentID = ae.Ptr("someone-else")
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), other, ae.LessonQuery{})
	if err != nil || page.Outcome != ae.LessonsDenied || len(page.Lessons) != 0 || page.InjectionsTracked {
		t.Fatalf("%v %d %v", page.Outcome, len(page.Lessons), err)
	}
}

// untracking is a store that holds no injections table: it answers records without them, and says so.
type untracking struct{ *Store }

func (u untracking) ListLessonRecords(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	statuses []ae.ExperienceStatus, limit int) (ae.LessonRecords, error) {
	read, err := u.Store.ListLessonRecords(ctx, bounds, scope, statuses, limit)
	for i := range read.Records {
		read.Records[i].Injection = nil
	}
	read.InjectionsTracked = false
	return read, err
}

func TestAStoreWithoutInjectionsAnswersAPageThatIsNotTracked(t *testing.T) {
	c, store := fixtureStore(t)
	lessons, err := ae.NewLessons(untracking{store}, ae.LessonsOptions{})
	if err != nil {
		t.Fatal(err)
	}
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil || page.InjectionsTracked || len(page.Lessons) == 0 {
		t.Fatalf("%v %d %v", page.InjectionsTracked, len(page.Lessons), err)
	}
	for _, lesson := range page.Lessons {
		if lesson.LastInjectedAt != nil || lesson.InjectionCount != 0 {
			t.Fatal("an injection shown when not tracked")
		}
	}
}

func TestInjectionsRecordedThroughTheStoreShowInTheView(t *testing.T) {
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	r := record(1, "task", "summary", "lesson", ae.StatusValidated, 0.9)
	ctx := context.Background()
	if err := store.InsertRecord(ctx, ae.BoundsOf(scope), &r); err != nil {
		t.Fatal(err)
	}
	lessons, _ := ae.NewLessons(store, ae.LessonsOptions{})
	list := func() ae.Lesson {
		page, err := lessons.List(ctx, ae.BoundsOf(scope), scope, ae.LessonQuery{})
		if err != nil || len(page.Lessons) != 1 || !page.InjectionsTracked {
			t.Fatalf("%d lessons, tracked %v, %v", len(page.Lessons), page.InjectionsTracked, err)
		}
		return page.Lessons[0]
	}
	if lesson := list(); lesson.LastInjectedAt != nil || lesson.InjectionCount != 0 || !lesson.IsEligible {
		t.Fatalf("never injected: %v", brief(lesson))
	}
	when := ae.MustParseInstant("2026-03-02T09:00:00+00:00")
	for range 2 {
		if err := store.RecordInjection(ctx, ae.BoundsOf(scope), scope, []ae.UUID{r.ExperienceID}, when); err != nil {
			t.Fatal(err)
		}
	}
	if lesson := list(); lesson.LastInjectedAt == nil || *lesson.LastInjectedAt != when || lesson.InjectionCount != 2 {
		t.Fatalf("injected: %v", brief(lesson))
	}
}

func brief(l ae.Lesson) string {
	return l.ExperienceID.String() + " " + strconv.FormatBool(l.IsEligible) + " " + string(l.Ineligibility)
}

func TestTheViewHoldsBlockBudgetAndModelAuthoredFlagsAgainstTheStoresRecords(t *testing.T) {
	store, err := New()
	if err != nil {
		t.Fatal(err)
	}
	r := record(1, "task", "summary", "lesson", ae.StatusValidated, 0.9)
	reflection := *r.Reflection
	reflection.Authorship = ae.AuthorshipModel
	r.Reflection = &reflection
	ctx := context.Background()
	if err := store.InsertRecord(ctx, ae.BoundsOf(scope), &r); err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		options ae.LessonsOptions
		code    ae.LessonIneligibility
	}{
		{ae.LessonsOptions{}, ""},
		{ae.LessonsOptions{ExcludeModelAuthored: true}, ae.IneligibleModelAuthored},
		{ae.LessonsOptions{BlockOptions: ae.BlockOptions{MaxBytes: 50}}, ae.IneligibleOverByteBudget},
	} {
		lessons, err := ae.NewLessons(store, test.options)
		if err != nil {
			t.Fatal(err)
		}
		page, err := lessons.List(ctx, ae.BoundsOf(scope), scope, ae.LessonQuery{})
		if err != nil || len(page.Lessons) != 1 || page.Lessons[0].Ineligibility != test.code {
			t.Fatalf("%+v: %v %d", test.options, err, len(page.Lessons))
		}
	}
}
