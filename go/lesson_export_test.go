package agentexperience_test

// Contract 1.14: the Markdown sink's rules the lesson-export fixtures do not state (their fixtures run in the inmemory
// module, which holds the holdout rows).

import (
	"context"
	"errors"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

func TestSignedNumberIsDotNetsThreeSectionFormat(t *testing.T) {
	for value, expected := range map[float64]string{
		1: "+1.00", -1: "-1.00", 0: "0.00", math.Copysign(0, -1): "0.00", 0.004: "0.00", -0.004: "0.00",
		0.005: "+0.01", -0.005: "-0.01", 0.125: "+0.13", 0.20592328245977842: "+0.21",
		math.NaN(): "(unavailable)", math.Inf(1): "(unavailable)", math.Inf(-1): "(unavailable)",
	} {
		if actual := ae.SignedNumber(value); actual != expected {
			t.Errorf("%v: %s, not %s", value, actual, expected)
		}
	}
}

func exported(id string, revision int64, text string) ae.ExportedLesson {
	rate, zero, one := 1.0, 0.0, 1.0
	return ae.ExportedLesson{ExperienceID: ae.MustParseUUID(id), Revision: revision, TaskID: "task", LessonText: text,
		MeasuredBenefit: ae.BenefitComparison{Treatment: ae.BenefitArm{Runs: 2, Successes: 2, Rate: &rate},
			Holdout: ae.BenefitArm{Runs: 2, Successes: 0, Rate: &zero}, Difference: &one, Lower: &zero, Upper: &one,
			Verdict: ae.Helps}}
}

func TestTheMarkdownSinkRefusesABlankPathAndMakesItAbsolute(t *testing.T) {
	for _, path := range []string{"", "  ", "\t\n"} {
		if _, err := ae.NewMarkdownLessonFileSink(path, nil); err == nil {
			t.Errorf("%q was accepted", path)
		}
	}
	sink, err := ae.NewMarkdownLessonFileSink(filepath.Join("..", "x.md"), nil)
	if err != nil || !filepath.IsAbs(sink.Path) {
		t.Fatalf("%v %v", sink, err)
	}
}

func TestTheMarkdownSinkCountsAgainstACrlfFileAndRewritesItWhole(t *testing.T) {
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	path := filepath.Join(t.TempDir(), "lessons.md")
	at := ae.MustParseInstant("2026-03-02T09:00:00Z")
	sink, err := ae.NewMarkdownLessonFileSink(path, func() ae.Instant { return at })
	if err != nil {
		t.Fatal(err)
	}
	first := []ae.ExportedLesson{exported("00000000-0000-4000-8000-000000000001", 1, "Lesson: one\n"),
		exported("00000000-0000-4000-8000-000000000002", 1, "Lesson: two\r\nsecond line\rthird\n\n")}
	if result, err := sink.Sync(context.Background(), scope, first); err != nil || result != (ae.LessonSyncResult{Added: 2}) {
		t.Fatalf("%+v %v", result, err)
	}
	data, _ := os.ReadFile(path)
	if !strings.Contains(string(data), "    Lesson: two\n    second line\n    third\n") || strings.Contains(string(data), "\r") ||
		!strings.Contains(string(data), "Exported: 2026-03-02T09:00:00.0000000Z\n") {
		t.Fatalf("%s", data)
	}
	// The same file with CRLF line breaks reads the same sections.
	if err := os.WriteFile(path, []byte(strings.ReplaceAll(string(data), "\n", "\r\n")), 0o600); err != nil {
		t.Fatal(err)
	}
	second := []ae.ExportedLesson{first[1], exported("00000000-0000-4000-8000-000000000001", 2, "Lesson: one\n"),
		exported("00000000-0000-4000-8000-000000000003", 1, "Lesson: three\n"), first[1]}
	result, err := sink.Sync(context.Background(), scope, second)
	if err != nil || result != (ae.LessonSyncResult{Added: 1, Updated: 1}) {
		t.Fatalf("%+v %v", result, err)
	}
	result, err = sink.Sync(context.Background(), scope, nil)
	if err != nil || result != (ae.LessonSyncResult{Removed: 3}) {
		t.Fatalf("%+v %v", result, err)
	}
	data, _ = os.ReadFile(path)
	if !strings.HasSuffix(string(data), "Lessons: 0\n") {
		t.Fatalf("%s", data)
	}
}

func TestAFailedWriteLeavesTheFileAndNoTemporaryFile(t *testing.T) {
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	missing, err := ae.NewMarkdownLessonFileSink(filepath.Join(t.TempDir(), "no-such-directory", "lessons.md"), nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := missing.Sync(context.Background(), scope, nil); err == nil {
		t.Fatal("a missing directory was written to")
	}
	// The path is a directory: the rename fails, and the temporary file is removed.
	directory := t.TempDir()
	target := filepath.Join(directory, "lessons.md")
	if err := os.Mkdir(target, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(target, "keep"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	sink, _ := ae.NewMarkdownLessonFileSink(target, nil)
	if _, err := sink.Sync(context.Background(), scope, nil); err == nil && runtime.GOOS != "windows" {
		t.Fatal("a directory was replaced")
	}
	entries, _ := os.ReadDir(directory)
	if len(entries) != 1 {
		t.Fatalf("%v", entries)
	}
}

type failingSink struct{}

func (failingSink) Sync(context.Context, ae.Scope, []ae.ExportedLesson) (ae.LessonSyncResult, error) {
	return ae.LessonSyncResult{}, errors.New("the sink failed")
}

func TestTheExportRefusesANilSinkABadMinimumAndPropagatesTheSinksError(t *testing.T) {
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	lessons, err := ae.NewLessons(pageStore{}, ae.LessonsOptions{})
	if err != nil {
		t.Fatal(err)
	}
	identity := ae.Identity{Principal: "p", Scope: scope}
	if _, err := lessons.ExportMeasured(context.Background(), identity, nil, nil); err == nil {
		t.Fatal("a nil sink was accepted")
	}
	if _, err := lessons.ExportMeasured(context.Background(), identity, failingSink{}, &ae.BenefitOptions{MinimumRunsPerArm: -1}); err == nil {
		t.Fatal("a negative minimum was accepted")
	}
	// A store that does not track the holdout: the set is empty, and the sink is still called.
	if _, err := lessons.ExportMeasured(context.Background(), identity, failingSink{}, nil); err == nil ||
		err.Error() != "the sink failed" {
		t.Fatalf("%v", err)
	}
	path := filepath.Join(t.TempDir(), "lessons.md")
	sink, _ := ae.NewMarkdownLessonFileSink(path, nil)
	result, err := lessons.ExportMeasured(context.Background(), identity, sink, nil)
	if err != nil || result.HoldoutTracked || len(result.Lessons) != 0 || result.Sync == nil {
		t.Fatalf("%+v %v", result, err)
	}
}
