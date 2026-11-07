//go:build unix

package agentexperience_test

import (
	"context"
	"os"
	"path/filepath"
	"syscall"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// The exported file is created as .NET creates one: 0666 less the umask, never owner-only.
func TestTheExportedFileIsCreatedWithDotNetsMode(t *testing.T) {
	umask := syscall.Umask(0o022)
	defer syscall.Umask(umask)
	path := filepath.Join(t.TempDir(), "lessons.md")
	sink, err := ae.NewMarkdownLessonFileSink(path, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := sink.Sync(context.Background(), ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}, nil); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o644 {
		t.Fatalf("the file's mode is %v, not 0644", info.Mode().Perm())
	}
}
