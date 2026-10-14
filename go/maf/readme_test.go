package maf

import (
	"os"
	"strings"
	"testing"
)

// The README's quick start is the body of example_test.go's quickStart, which go vet and go test compile.
func TestTheReadmeQuickStartIsTheCompiledExample(t *testing.T) {
	readme, err := os.ReadFile("../README.md")
	if err != nil {
		t.Fatal(err)
	}
	section := string(readme)[strings.Index(string(readme), "## Quick start with Microsoft Agent Framework"):]
	start := strings.Index(section, "```go\n") + len("```go\n")
	snippet := section[start : start+strings.Index(section[start:], "```")]

	example, err := os.ReadFile("example_test.go")
	if err != nil {
		t.Fatal(err)
	}
	text := string(example)
	body := text[strings.Index(text, "func quickStart("):]
	body = body[strings.Index(body, "{\n")+2 : strings.Index(body, "\n}\n")+1]
	var lines []string
	for _, line := range strings.SplitAfter(body, "\n") {
		lines = append(lines, strings.TrimPrefix(line, "\t"))
	}
	if strings.Join(lines, "") != snippet {
		t.Fatalf("the README's snippet differs from quickStart's body:\n%s\n---\n%s", snippet, strings.Join(lines, ""))
	}
}
