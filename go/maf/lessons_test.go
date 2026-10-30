package maf

// Contract 1.7: the lessons view reachable from the one-call setup and from the options alone, over the store the
// injection uses.

import (
	"context"
	"errors"
	"testing"

	"github.com/microsoft/agent-framework-go/agent"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

func listAll(t *testing.T, lessons *ae.Lessons, scope ae.Scope) ae.LessonsPage {
	t.Helper()
	page, err := lessons.List(context.Background(), ae.BoundsOf(scope), scope, ae.LessonQuery{})
	if err != nil {
		t.Fatal(err)
	}
	return page
}

func TestTheLessonsViewListsWhatTheSetupStoredAndWhenItWasInjected(t *testing.T) {
	h, recorded := newHost(t), &recordings{}
	a, config := learning(t, h, h.store, recorded, 0)
	runText(t, a, ticket)
	runText(t, a, nextTicket)
	closeRecordings(t, config)

	lessons, err := LessonsOf(config)
	if err != nil {
		t.Fatal(err)
	}
	page := listAll(t, lessons, testScope)
	first := h.finalized[0].Record.ExperienceID
	if page.Outcome != ae.LessonsFound || !page.InjectionsTracked || len(page.Lessons) == 0 {
		t.Fatalf("%v tracked %v, %d lessons", page.Outcome, page.InjectionsTracked, len(page.Lessons))
	}
	var found bool
	for _, lesson := range page.Lessons {
		if lesson.ExperienceID != first {
			continue
		}
		found = true
		if lesson.LastInjectedAt == nil || lesson.InjectionCount != 1 || !lesson.IsEligible ||
			lesson.TaskID != "triage-ticket" || lesson.Status != ae.StatusValidated || len(lesson.Checks) == 0 {
			t.Fatalf("%s %v %d", lesson.ExperienceID, lesson.IsEligible, lesson.InjectionCount)
		}
		for _, check := range lesson.Checks {
			if check.CheckID == "" {
				t.Fatal("a check without an ID")
			}
		}
	}
	if !found {
		t.Fatal("the stored lesson is not listed")
	}

	// The provider's accessor is the same view, and an identity lists its own scope.
	provider, err := ContextProviderOf(config).Lessons()
	if err != nil {
		t.Fatal(err)
	}
	identity := ae.Identity{Principal: "svc", Scope: testScope}
	again, err := provider.ListFor(context.Background(), identity, ae.LessonQuery{})
	if err != nil || len(again.Lessons) != len(page.Lessons) {
		t.Fatalf("%d lessons, %v", len(again.Lessons), err)
	}
}

func TestTheViewIsReachableWhenInjectionIsDisabledAndUsesTheSetupsFlags(t *testing.T) {
	h := newHost(t)
	config := &agent.Config{Name: "triage-ticket"}
	options := h.options(Options{DisableInjection: true, ExcludeModelAuthored: true,
		BlockOptions: ae.BlockOptions{MaxBytes: 800}, Clock: func() ae.Instant {
			return ae.MustParseInstant("2026-03-02T15:00:00+00:00")
		}})
	if err := UseAgentExperience(config, options); err != nil {
		t.Fatal(err)
	}
	if ContextProviderOf(config) != nil {
		t.Fatal("a provider was added")
	}
	lessons, err := LessonsOf(config)
	if err != nil {
		t.Fatal(err)
	}
	page := listAll(t, lessons, testScope)
	if page.Outcome != ae.LessonsFound || len(page.Lessons) != 0 || page.GeneratedAt.String() != "2026-03-02T15:00:00+00:00" {
		t.Fatalf("%v %d %v", page.Outcome, len(page.Lessons), page.GeneratedAt)
	}
}

func TestTheViewSeesTheSetupsByteBudget(t *testing.T) {
	h := newHost(t)
	a, config := learning(t, h, h.store, &recordings{}, 0)
	runText(t, a, ticket)
	closeRecordings(t, config)
	small, err := NewLessons(Options{Store: h.store, BlockOptions: ae.BlockOptions{MaxBytes: 60}})
	if err != nil {
		t.Fatal(err)
	}
	page := listAll(t, small, testScope)
	if len(page.Lessons) != 1 || page.Lessons[0].Ineligibility != ae.IneligibleOverByteBudget {
		t.Fatalf("%d lessons", len(page.Lessons))
	}
	normal, err := NewLessons(Options{Store: h.store})
	if err != nil {
		t.Fatal(err)
	}
	if page := listAll(t, normal, testScope); len(page.Lessons) != 1 || !page.Lessons[0].IsEligible {
		t.Fatal("the lesson is not eligible under the defaults")
	}
}

func TestAScopeOutsideTheCallersBoundsIsDeniedAndAnotherScopeIsNotListed(t *testing.T) {
	h := newHost(t)
	a, config := learning(t, h, h.store, &recordings{}, 0)
	runText(t, a, ticket)
	closeRecordings(t, config)
	lessons, _ := LessonsOf(config)
	other := testScope
	other.AgentID = ae.Ptr("someone-else")
	page, err := lessons.List(context.Background(), ae.BoundsOf(other), testScope, ae.LessonQuery{})
	if err != nil || page.Outcome != ae.LessonsDenied || len(page.Lessons) != 0 {
		t.Fatalf("%v %d %v", page.Outcome, len(page.Lessons), err)
	}
	page = listAll(t, lessons, other)
	if page.Outcome != ae.LessonsFound || len(page.Lessons) != 0 {
		t.Fatalf("another scope's page: %v %d", page.Outcome, len(page.Lessons))
	}
}

func TestAStoreThatCannotAnswerTheViewIsRefused(t *testing.T) {
	h := newHost(t)
	if _, err := NewLessons(Options{Store: searchOnly{h.store}}); !errors.Is(err, ErrLessonsNotAvailable) {
		t.Fatalf("%v", err)
	}
	config := &agent.Config{Name: "x"}
	if _, err := LessonsOf(config); !errors.Is(err, ErrLessonsNotAvailable) {
		t.Fatalf("an unwired configuration: %v", err)
	}
	if _, err := LessonsOf(nil); !errors.Is(err, ErrLessonsNotAvailable) {
		t.Fatalf("nil: %v", err)
	}
	wired := &agent.Config{Name: "x"}
	if err := UseAgentExperience(wired, h.options(Options{Store: searchOnly{h.store}})); err != nil {
		t.Fatal(err)
	}
	if _, err := LessonsOf(wired); !errors.Is(err, ErrLessonsNotAvailable) {
		t.Fatalf("a store that is not a reader: %v", err)
	}
	if _, err := NewLessons(Options{Store: h.store, BlockOptions: ae.BlockOptions{MaxBytes: -1}}); err == nil {
		t.Fatal("invalid block options were accepted")
	}
}
