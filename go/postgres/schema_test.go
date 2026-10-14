package postgres

// The record schema check. It lives in this module, not the core's, because it needs a JSON Schema validator and the
// core module lists no third-party requirement. Every stored record in the fixtures, and every record the core
// writes for a finalize fixture, validates against contract/schema/experience-record.v1.json.

import (
	"bytes"
	"fmt"
	"os"
	"regexp"
	"strings"
	"sync"
	"testing"

	"github.com/santhosh-tekuri/jsonschema/v6"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

// ---- The schema ------------------------------------------------------------------------------------------------

// JSON Schema's patterns are ECMA-262 regular expressions, and the validator's default engine is Go's regexp
// (RE2). The two agree on everything the schema uses except \s: RE2's is ASCII only ([\t\n\f\r ]), ECMA-262's adds
// \v and the Unicode spaces (but not U+0085 or U+001C to U+001F). The schema's one \s (a task ID must hold a
// non-whitespace character) is translated into ECMA-262's class; any other construct whose meaning differs (\s and
// \S elsewhere, or a multiline $) fails the test, so a schema change cannot be evaluated with the wrong meaning. ^
// and $ without flags anchor at the very start and end in both.
const ecmaWhitespace = `\t\n\v\f\r \x{a0}\x{1680}\x{2000}-\x{200a}\x{2028}\x{2029}\x{202f}\x{205f}\x{3000}\x{feff}`

var unsupportedECMA = regexp.MustCompile(`\\[sS]|\(\?[a-z]*m`)

func ecmaRegexp(pattern string) (jsonschema.Regexp, error) {
	translated := strings.ReplaceAll(pattern, `[^\s]`, `[^`+ecmaWhitespace+`]`)
	if found := unsupportedECMA.FindString(translated); found != "" {
		return nil, fmt.Errorf("translate %q in %q for ECMA-262: RE2's meaning differs", found, pattern)
	}
	return regexp.Compile(translated)
}

var (
	schemaOnce     sync.Once
	recordSchema   *jsonschema.Schema
	recordSchemaEr error
)

func schema(t *testing.T) *jsonschema.Schema {
	t.Helper()
	schemaOnce.Do(func() {
		file, err := os.Open(ct.Schema)
		if err != nil {
			recordSchemaEr = err
			return
		}
		defer file.Close()
		document, err := jsonschema.UnmarshalJSON(file)
		if err != nil {
			recordSchemaEr = err
			return
		}
		compiler := jsonschema.NewCompiler()
		compiler.UseRegexpEngine(ecmaRegexp)
		if err := compiler.AddResource("experience-record.v1.json", document); err != nil {
			recordSchemaEr = err
			return
		}
		recordSchema, recordSchemaEr = compiler.Compile("experience-record.v1.json")
	})
	if recordSchemaEr != nil {
		t.Fatalf("the record schema does not compile: %v", recordSchemaEr)
	}
	return recordSchema
}

// assertValidRecord validates a stored record, as this package writes it, against the schema.
func assertValidRecord(t *testing.T, stored any) {
	t.Helper()
	data, err := ae.MarshalJSONValue(stored)
	if err != nil {
		t.Fatalf("the record cannot be written: %v", err)
	}
	instance, err := jsonschema.UnmarshalJSON(bytes.NewReader(data))
	if err != nil {
		t.Fatalf("the record cannot be read: %v", err)
	}
	if err := schema(t).Validate(instance); err != nil {
		t.Fatalf("the record does not validate against the schema: %v", err)
	}
}

func TestEveryFixtureRecordAndEveryRecordTheCoreWritesValidates(t *testing.T) {
	for _, name := range ct.Cases("finalize") {
		assertValidRecord(t, ct.MustReadJSON(fixture("finalize", name, "expected-record.json")))
		finalized, _, err := ct.RunFinalizeCase(ct.MustReadJSON(fixture("finalize", name, "input.json")))
		if err != nil {
			t.Fatal(err)
		}
		assertValidRecord(t, finalized.Record.ToStored())
	}
	for _, name := range ct.Cases("inject") {
		for _, entry := range ct.MustReadJSON(fixture("inject", name, "records.json")).([]any) {
			stored, _ := entry.(*ae.Object).Get("record")
			assertValidRecord(t, stored)
		}
	}
	for _, name := range ct.Cases("search") {
		for _, stored := range ct.MustReadJSON(fixture("search", name, "seed.json")).([]any) {
			assertValidRecord(t, stored)
		}
	}
}

func TestTheSchemaRefusesARecordThatBreaksIt(t *testing.T) {
	stored := ct.MustReadJSON(fixture("finalize", "verified-after-one-failed-attempt", "expected-record.json")).(*ae.Object)
	columns, _ := stored.Get("columns")
	// U+3000 is ECMA-262 whitespace, so a task ID of it alone breaks the schema's [^\s].
	columns.(*ae.Object).Set("task_id", "　")
	data, _ := ae.MarshalJSONValue(stored)
	instance, err := jsonschema.UnmarshalJSON(bytes.NewReader(data))
	if err != nil {
		t.Fatal(err)
	}
	if schema(t).Validate(instance) == nil {
		t.Fatal("a whitespace task ID validated")
	}
}
