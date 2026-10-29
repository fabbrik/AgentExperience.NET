package agentexperience_test

import (
	"context"
	"errors"
	"fmt"
	"math"
	"strconv"
	"strings"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// fakeReader is a LessonsReader over a case's rows, answering as contract 1.7's statements do: the scope's records,
// newest first, at most the limit, each with its injection summary.
type fakeReader struct {
	records    []ae.ExperienceRecord
	injections map[ae.UUID]ae.InjectionSummary
	untracked  bool
	calls      int
	lastLimit  int
	err        error
}

func (f *fakeReader) ListLessonRecords(_ context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	statuses []ae.ExperienceStatus, limit int) (ae.LessonRecords, error) {
	f.calls++
	f.lastLimit = limit
	if f.err != nil {
		return ae.LessonRecords{}, f.err
	}
	if err := ae.ValidateLessonsRead(statuses, limit); err != nil {
		return ae.LessonRecords{}, err
	}
	if !bounds.Permits(scope) {
		return ae.LessonRecords{}, ae.ErrAuthorizationDenied
	}
	wanted := map[ae.ExperienceStatus]bool{}
	for _, status := range statuses {
		wanted[status] = true
	}
	var records []ae.LessonRecord
	for _, record := range f.records {
		if !record.Scope.Equal(scope) || (statuses != nil && !wanted[record.Status]) {
			continue
		}
		held := ae.LessonRecord{Record: record}
		if summary, ok := f.injections[record.ExperienceID]; ok && !f.untracked {
			held.Injection = &summary
		}
		records = append(records, held)
	}
	ae.SortLessonRecords(records)
	if len(records) > limit {
		records = records[:limit]
	}
	return ae.LessonRecords{Records: records, Answered: len(records), InjectionsTracked: !f.untracked}, nil
}

func readerOf(c *ct.LessonsCase) *fakeReader {
	f := &fakeReader{records: c.Records, injections: map[ae.UUID]ae.InjectionSummary{}}
	for _, row := range c.Injections {
		for _, record := range c.Records {
			if record.ExperienceID == row.ExperienceID && record.Scope.Equal(row.Scope) {
				f.injections[row.ExperienceID] = ae.InjectionSummary{LastInjectedAt: row.LastInjectedAt,
					InjectionCount: row.InjectionCount}
			}
		}
	}
	return f
}

// brief is a lesson without its block, for a failure message.
func brief(l ae.Lesson) string {
	return fmt.Sprintf("%s %s %v eligible=%v %q %q", l.ExperienceID, l.Status, l.Confidence, l.IsEligible,
		l.Ineligibility, l.IneligibleReason)
}

func lessonsOf(t *testing.T, store ae.LessonsReader, options ae.LessonsOptions) *ae.Lessons {
	t.Helper()
	lessons, err := ae.NewLessons(store, options)
	if err != nil {
		t.Fatal(err)
	}
	return lessons
}

func TestLessonsViewFixtures(t *testing.T) {
	for _, name := range ct.Cases(ct.LessonsFamily) {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadLessonsCase(name)
			if err != nil {
				t.Fatal(err)
			}
			if len(c.Assignments) > 0 {
				// Contract 1.10's measured-benefit cases read the holdout, which this fake does not hold: the in-memory
				// and PostgreSQL stores run them.
				t.Skip("needs a store that reads the holdout")
			}
			now := c.Now
			lessons := lessonsOf(t, readerOf(c), ae.LessonsOptions{Clock: func() ae.Instant { return now }})
			page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, c.Query())
			if err != nil {
				t.Fatal(err)
			}
			if difference := ct.LessonsDifference(c, page); difference != "" {
				t.Fatal(difference)
			}
		})
	}
}

// base is a case with a few lessons of several statuses, to build the matrix's scenarios on.
func base(t *testing.T) (*ct.LessonsCase, *fakeReader) {
	t.Helper()
	c, err := ct.LoadLessonsCase("newest-first-with-eligibility")
	if err != nil {
		t.Fatal(err)
	}
	return c, readerOf(c)
}

// inScope are the case's records in the scope listed.
func inScope(c *ct.LessonsCase) []ae.ExperienceRecord {
	var records []ae.ExperienceRecord
	for _, record := range c.Records {
		if record.Scope.Equal(c.Scope) {
			records = append(records, record)
		}
	}
	return records
}

func TestTheQueryDefaultsAreTheDotNetOnes(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil {
		t.Fatal(err)
	}
	if page.Outcome != ae.LessonsFound || len(page.Lessons) != len(inScope(c)) || page.HasMore {
		t.Fatalf("default list: %v, %d lessons, hasMore %v", page.Outcome, len(page.Lessons), page.HasMore)
	}
	if reader.lastLimit != ae.LessonsDefaultLimit+1 {
		t.Fatalf("the probe is %d, not the default limit plus one", reader.lastLimit)
	}
	for i := 1; i < len(page.Lessons); i++ {
		if page.Lessons[i-1].CreatedAt.Before(page.Lessons[i].CreatedAt) {
			t.Fatal("not newest first")
		}
	}
	if page.GeneratedAt.IsZero() {
		t.Fatal("no generation time")
	}
}

func TestTheProbeIsOnePastTheLimitOrTheMaximumWhenIneligibleLessonsAreLeftOut(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	no := false
	for _, test := range []struct {
		query ae.LessonQuery
		probe int
	}{
		{ae.LessonQuery{Limit: 3}, 4},
		{ae.LessonQuery{Limit: 500}, 500},
		{ae.LessonQuery{Limit: 3, IncludeIneligible: &no}, 500},
	} {
		if _, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, test.query); err != nil {
			t.Fatal(err)
		}
		if reader.lastLimit != test.probe {
			t.Errorf("%+v: probe %d, want %d", test.query, reader.lastLimit, test.probe)
		}
	}
}

func TestIneligibleLessonsAreHiddenOnRequestAndHasMoreCountsTheProbe(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	no := false
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope,
		ae.LessonQuery{IncludeIneligible: &no})
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Lessons) == 0 || page.HasMore {
		t.Fatalf("%d lessons, hasMore %v", len(page.Lessons), page.HasMore)
	}
	for _, lesson := range page.Lessons {
		if !lesson.IsEligible || lesson.Ineligibility != "" || lesson.IneligibleReason != "" {
			t.Fatal("an ineligible lesson was listed: " + brief(lesson))
		}
	}
	// The records skipped still count against what was read.
	page, err = lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope,
		ae.LessonQuery{IncludeIneligible: &no, Limit: 1})
	if err != nil || len(page.Lessons) != 1 || !page.HasMore {
		t.Fatalf("limit 1: %d lessons, hasMore %v, %v", len(page.Lessons), page.HasMore, err)
	}
}

func TestALimitListsThatManyAndSaysThereIsMore(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{Limit: 2})
	if err != nil || len(page.Lessons) != 2 || !page.HasMore {
		t.Fatalf("%d lessons, hasMore %v, %v", len(page.Lessons), page.HasMore, err)
	}
	page, err = lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope,
		ae.LessonQuery{Limit: len(inScope(c))})
	if err != nil || len(page.Lessons) != len(inScope(c)) || page.HasMore {
		t.Fatalf("exactly the records: %d lessons, hasMore %v, %v", len(page.Lessons), page.HasMore, err)
	}
}

func TestAnInvalidQueryIsAnErrorBeforeAnythingIsRead(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	for name, query := range map[string]ae.LessonQuery{
		"negative limit": {Limit: -1},
		"limit 501":      {Limit: 501},
		"empty statuses": {Statuses: []ae.ExperienceStatus{}},
		"unknown status": {Statuses: []ae.ExperienceStatus{ae.StatusValidated, "Nonsense"}},
	} {
		if _, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, query); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	if reader.calls != 0 {
		t.Fatalf("the store was read %d times", reader.calls)
	}
	// A repeated status counts once.
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope,
		ae.LessonQuery{Statuses: []ae.ExperienceStatus{ae.StatusStale, ae.StatusStale}})
	if err != nil {
		t.Fatal(err)
	}
	for _, lesson := range page.Lessons {
		if lesson.Status != ae.StatusStale {
			t.Fatalf("status %s listed", lesson.Status)
		}
	}
}

func TestAScopeOutsideTheBoundsIsADeniedEmptyPageAndNoRead(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	other := c.Scope
	other.AgentID = ae.Ptr("someone-else")
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), other, ae.LessonQuery{})
	if err != nil {
		t.Fatal(err)
	}
	if page.Outcome != ae.LessonsDenied || len(page.Lessons) != 0 || page.Lessons == nil || page.HasMore ||
		page.OutOfScopeDropped != 0 || page.InjectionsTracked || reader.calls != 0 {
		t.Fatalf("%v %d %v, %d reads", page.Outcome, len(page.Lessons), page.HasMore, reader.calls)
	}
	// A store that refuses the scope itself answers the same.
	reader.err = ae.ErrAuthorizationDenied
	page, err = lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil || page.Outcome != ae.LessonsDenied {
		t.Fatalf("%v, %v", page.Outcome, err)
	}
	// Any other store failure is the call's.
	reader.err = errors.New("boom")
	if _, err = lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{}); err == nil {
		t.Fatal("a store failure was swallowed")
	}
}

func TestAnotherScopesRecordsAreNeverListedAndCountAsDropped(t *testing.T) {
	c, reader := base(t)
	other := c.Records[0]
	other.Scope.AgentID = ae.Ptr("someone-else")
	lessons := lessonsOf(t, &leakyReader{fakeReader: reader, extra: other}, ae.LessonsOptions{})
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil || page.OutOfScopeDropped != 1 || len(page.Lessons) != len(inScope(c)) {
		t.Fatalf("dropped %d, %d lessons, %v", page.OutOfScopeDropped, len(page.Lessons), err)
	}
}

type leakyReader struct {
	*fakeReader
	extra ae.ExperienceRecord
}

func (l *leakyReader) ListLessonRecords(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	statuses []ae.ExperienceStatus, limit int) (ae.LessonRecords, error) {
	read, err := l.fakeReader.ListLessonRecords(ctx, bounds, scope, statuses, limit)
	read.Records = append(read.Records, ae.LessonRecord{Record: l.extra})
	return read, err
}

func TestNeverInjectedTrackedAndInjected(t *testing.T) {
	c, reader := base(t)
	var neverInjected ae.UUID
	for _, record := range c.Records {
		if _, ok := reader.injections[record.ExperienceID]; !ok {
			neverInjected = record.ExperienceID
		}
	}
	reader.injections[c.Records[0].ExperienceID] = ae.InjectionSummary{
		LastInjectedAt: ae.MustParseInstant("2026-03-02T08:00:00.123456+00:00"), InjectionCount: 3}
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil || !page.InjectionsTracked {
		t.Fatal(err)
	}
	var sawInjected, sawNever bool
	for _, lesson := range page.Lessons {
		switch {
		case lesson.ExperienceID == c.Records[0].ExperienceID:
			sawInjected = lesson.LastInjectedAt != nil && lesson.InjectionCount == 3 &&
				lesson.LastInjectedAt.String() == "2026-03-02T08:00:00.123456+00:00"
		case lesson.ExperienceID == neverInjected && neverInjected != (ae.UUID{}):
			sawNever = lesson.LastInjectedAt == nil && lesson.InjectionCount == 0
		}
	}
	if !sawInjected {
		t.Fatal("the injection was not shown")
	}
	if neverInjected != (ae.UUID{}) && !sawNever {
		t.Fatal("a never-injected lesson was not shown as such")
	}

	// Not tracked: the instants are not known, whatever the rows hold.
	reader.untracked = true
	page, err = lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil || page.InjectionsTracked {
		t.Fatalf("tracked %v, %v", page.InjectionsTracked, err)
	}
	for _, lesson := range page.Lessons {
		if lesson.LastInjectedAt != nil || lesson.InjectionCount != 0 {
			t.Fatal("an injection shown when not tracked: " + brief(lesson))
		}
	}
}

func TestAnUnrenderableRecordIsListedAndTheOthersStill(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{})
	bad := inScope(c)[1].ExperienceID
	lessons.SetRenderForTest(func(record *ae.ExperienceRecord, options ae.BlockOptions) (string, error) {
		if record.ExperienceID == bad {
			var nothing []int
			_ = nothing[3]
		}
		return ae.RenderAlone(record, options)
	})
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil || len(page.Lessons) != len(inScope(c)) {
		t.Fatalf("%d lessons, %v", len(page.Lessons), err)
	}
	for _, lesson := range page.Lessons {
		if lesson.ExperienceID != bad {
			if lesson.Ineligibility == ae.IneligibleUnrenderable || lesson.BlockText == "" {
				t.Fatal("a good record was hidden: " + brief(lesson))
			}
			continue
		}
		if lesson.IsEligible || lesson.Ineligibility != ae.IneligibleUnrenderable || lesson.BlockText != "" ||
			len(lesson.Checks) != 0 || !strings.HasPrefix(lesson.IneligibleReason, "it could not be rendered (") ||
			!strings.HasSuffix(lesson.IneligibleReason, ")") || lesson.TaskID == "" {
			t.Fatal(brief(lesson))
		}
	}
	// A writer that fails is unrenderable too.
	lessons.SetRenderForTest(func(*ae.ExperienceRecord, ae.BlockOptions) (string, error) {
		return "", errors.New("no")
	})
	page, _ = lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	for _, lesson := range page.Lessons {
		if lesson.Ineligibility != ae.IneligibleUnrenderable {
			t.Fatal(brief(lesson))
		}
	}
}

func TestTheByteBudgetIsTheBlockOptionsMaximum(t *testing.T) {
	c, reader := base(t)
	lessons := lessonsOf(t, reader, ae.LessonsOptions{BlockOptions: ae.BlockOptions{MaxBytes: 100}})
	page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
	if err != nil {
		t.Fatal(err)
	}
	seen := 0
	for _, lesson := range page.Lessons {
		if lesson.Ineligibility == ae.IneligibleStatus || lesson.Ineligibility == ae.IneligibleBelowConfidenceFloor {
			continue
		}
		seen++
		want := "its block (" + strconv.Itoa(len(lesson.BlockText)) + " bytes) is larger than the 100-byte injection budget"
		if lesson.IsEligible || lesson.Ineligibility != ae.IneligibleOverByteBudget || lesson.IneligibleReason != want {
			t.Fatal(brief(lesson))
		}
	}
	if seen == 0 {
		t.Fatal("no lesson reached the budget rule")
	}
}

func TestAModelAuthoredLessonIsIneligibleOnlyWhenTheSettingsExcludeThem(t *testing.T) {
	c, _ := base(t)
	records := append([]ae.ExperienceRecord(nil), c.Records...)
	for i := range records {
		if records[i].Status == ae.StatusValidated {
			records[i].ReuseConfidence = 0.9
			reflection := *records[i].Reflection
			reflection.Authorship = ae.AuthorshipModel
			records[i].Reflection = &reflection
		}
	}
	reader := &fakeReader{records: records}
	for _, exclude := range []bool{false, true} {
		lessons := lessonsOf(t, reader, ae.LessonsOptions{ExcludeModelAuthored: exclude})
		page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope,
			ae.LessonQuery{Statuses: []ae.ExperienceStatus{ae.StatusValidated}})
		if err != nil || len(page.Lessons) == 0 {
			t.Fatalf("%v, %d", err, len(page.Lessons))
		}
		for _, lesson := range page.Lessons {
			if exclude {
				if lesson.IsEligible || lesson.Ineligibility != ae.IneligibleModelAuthored ||
					lesson.IneligibleReason != "model-authored lessons are excluded" {
					t.Fatal("excluded: " + brief(lesson))
				}
			} else if !lesson.IsEligible && lesson.Ineligibility != ae.IneligibleOverByteBudget {
				t.Fatal("included: " + brief(lesson))
			}
		}
	}
}

func TestEligibilityIsDecidedInTheContractsOrderWithItsReasons(t *testing.T) {
	c, _ := base(t)
	template := c.Records[0]
	make := func(status ae.ExperienceStatus, confidence float64, model bool) ae.ExperienceRecord {
		record := template
		record.ExperienceID = ae.NewRandomUUID()
		record.Status, record.ReuseConfidence = status, confidence
		if model {
			reflection := *record.Reflection
			reflection.Authorship = ae.AuthorshipModel
			record.Reflection = &reflection
		}
		return record
	}
	for _, test := range []struct {
		record ae.ExperienceRecord
		code   ae.LessonIneligibility
		reason string
	}{
		{make(ae.StatusStale, 0.1, true), ae.IneligibleStatus, "status Stale is never retrieved"},
		{make(ae.StatusValidated, 0.4, true), ae.IneligibleBelowConfidenceFloor,
			"confidence 0.40 is below the retrieval floor 0.50"},
		{make(ae.StatusValidated, 0.499, false), ae.IneligibleBelowConfidenceFloor,
			"confidence 0.50 is below the retrieval floor 0.50"},
		{make(ae.StatusValidated, 0.125, false), ae.IneligibleBelowConfidenceFloor,
			"confidence 0.13 is below the retrieval floor 0.50"},
		{make(ae.StatusValidated, 0.135, false), ae.IneligibleBelowConfidenceFloor,
			"confidence 0.14 is below the retrieval floor 0.50"},
		{make(ae.StatusValidated, math.NaN(), false), ae.IneligibleBelowConfidenceFloor,
			"confidence (unavailable) is below the retrieval floor 0.50"},
		{make(ae.StatusReinforced, 0.5, true), ae.IneligibleModelAuthored, "model-authored lessons are excluded"},
		{make(ae.StatusReinforced, 0.5, false), "", ""},
	} {
		reader := &fakeReader{records: []ae.ExperienceRecord{test.record}}
		lessons := lessonsOf(t, reader, ae.LessonsOptions{ExcludeModelAuthored: true})
		page, err := lessons.List(context.Background(), ae.BoundsOf(c.Scope), c.Scope, ae.LessonQuery{})
		if err != nil || len(page.Lessons) != 1 {
			t.Fatalf("%v, %d", err, len(page.Lessons))
		}
		lesson := page.Lessons[0]
		if lesson.Ineligibility != test.code || lesson.IneligibleReason != test.reason ||
			lesson.IsEligible != (test.code == "") {
			t.Errorf("%s %v: %q %q, want %q %q", test.record.Status, test.record.ReuseConfidence,
				lesson.Ineligibility, lesson.IneligibleReason, test.code, test.reason)
		}
	}
}

func TestTheLessonsViewNeedsAStoreAndValidBlockOptions(t *testing.T) {
	if _, err := ae.NewLessons(nil, ae.LessonsOptions{}); err == nil {
		t.Fatal("a nil store was accepted")
	}
	if _, err := ae.NewLessons(&fakeReader{}, ae.LessonsOptions{BlockOptions: ae.BlockOptions{MaxBytes: -1}}); err == nil {
		t.Fatal("invalid block options were accepted")
	}
}

func TestTheBlockAloneIsOneNumberedRecordWithNoRankingAndNoLimit(t *testing.T) {
	c, _ := base(t)
	record := c.Records[0]
	text, err := ae.RenderAlone(&record, ae.BlockOptions{MaxBytes: 1, MaxRecords: 1})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(text, ae.BlockBegin+"\n") || !strings.HasSuffix(text, ae.BlockEnd+"\n") ||
		!strings.Contains(text, "--- RECORD 1: ") || !strings.Contains(text, "Matched: no ranking data\n") {
		t.Fatal(text)
	}
	verbose, err := ae.RenderAlone(&record, ae.BlockOptions{Rendering: ae.RenderingVerbose})
	if err != nil || !strings.Contains(verbose, "--- RECORD 1 ---\n") || !strings.Contains(verbose, "--- END RECORD 1 ---\n") {
		t.Fatalf("%v\n%s", err, verbose)
	}
	if _, err := ae.RenderAlone(&record, ae.BlockOptions{Rendering: "Fancy"}); err == nil {
		t.Fatal("invalid options were accepted")
	}
}
