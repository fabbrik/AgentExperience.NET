package inmemory

// Contract 1.14 on the in-memory store: every lesson-export fixture, exported to the Markdown sink, gives .NET's
// result and .NET's file, byte for byte. The .NET half of the cross-language export generated the fixtures from the
// same seeds; this is the Go half.

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// exportStore is a store holding a lesson-export case's records and holdout rows, on the case's clock.
func exportStore(t *testing.T, c *ct.LessonExportCase) *Store {
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
	for _, row := range c.Assignments {
		if err := store.RecordHoldoutAssignment(ctx, ae.BoundsOf(row.Scope), row.Scope, ae.HoldoutAssignment{
			RunID: row.RunID, Arm: row.Arm, AssignedAt: row.AssignedAt,
			Records: []ae.RunExposure{{ExperienceID: row.ExperienceID, Revision: row.Revision}}}); err != nil {
			t.Fatal(err)
		}
	}
	return store
}

func TestTheLessonExportFixturesGiveDotNetsFile(t *testing.T) {
	cases := ct.Cases(ct.LessonExportFamily)
	if len(cases) != 8 {
		t.Fatalf("lesson-export cases: %v", cases)
	}
	for _, name := range cases {
		t.Run(name, func(t *testing.T) {
			c, err := ct.LoadLessonExportCase(name)
			if err != nil {
				t.Fatal(err)
			}
			now := c.Now
			clock := func() ae.Instant { return now }
			lessons, err := ae.NewLessons(exportStore(t, c), ae.LessonsOptions{Clock: clock})
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(t.TempDir(), "lessons.md")
			if c.Previous != nil {
				if err := os.WriteFile(path, c.Previous, 0o600); err != nil {
					t.Fatal(err)
				}
			}
			sink, err := ae.NewMarkdownLessonFileSink(path, clock)
			if err != nil {
				t.Fatal(err)
			}
			result, err := lessons.ExportMeasured(context.Background(), c.Identity(), sink, c.BenefitOptions())
			if err != nil {
				t.Fatal(err)
			}
			if difference := ct.LessonExportDifference(c, result); difference != "" {
				t.Fatal(difference)
			}
			written, err := os.ReadFile(path)
			if os.IsNotExist(err) {
				written, err = nil, nil
			}
			if err != nil {
				t.Fatal(err)
			}
			if string(written) != string(c.ExpectedFile) {
				t.Fatalf("the file differs from .NET's:\n--- expected\n%s\n--- actual\n%s", c.ExpectedFile, written)
			}
			entries, _ := os.ReadDir(filepath.Dir(path))
			if want := min(1, len(written)); len(entries) != want {
				t.Fatalf("the directory holds %d entries, not %d: a temporary file was left behind", len(entries), want)
			}
		})
	}
}
