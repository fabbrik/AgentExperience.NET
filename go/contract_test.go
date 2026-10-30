package agentexperience_test

// The conformance runner: every fixture family of contract/ through this package, compared exactly. Blocks and task
// text byte for byte; JSON by value, an integer never equal to a double; search rows and scores (in the postgres
// package's tests, which need PostgreSQL) within the contract's tolerance.

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"go/parser"
	"go/token"
	"io/fs"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"testing"
	"unicode/utf16"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func assertJSONEqual(t *testing.T, expected, actual any, what string) {
	t.Helper()
	if difference := ct.Difference(expected, ct.AsWritten(actual), "$", ct.Options{}); difference != "" {
		t.Fatalf("%s: %s", what, difference)
	}
}

func fixture(parts ...string) string {
	return filepath.Join(append([]string{ct.Fixtures}, parts...)...)
}

// ---- The lock and the version ----------------------------------------------------------------------------------

func TestTheContractVersionIsOneThisPackageSupports(t *testing.T) {
	version, err := ct.CheckContractVersion(ct.MustReadJSON(filepath.Join(ct.Contract, "lock.json")))
	if err != nil {
		t.Fatal(err)
	}
	if version != ae.ContractVersion {
		t.Fatalf("the lock is contract %s, this package implements %s", version, ae.ContractVersion)
	}
	if strings.Join(ae.SupportedContractVersions, ", ") != "1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 1.10, 1.11, 1.12, 1.13" {
		t.Fatalf("supported versions: %v", ae.SupportedContractVersions)
	}
}

func TestAnUnsupportedContractVersionFailsWithAClearMessage(t *testing.T) {
	for _, version := range []any{"2.0", "1.14", nil} {
		_, err := ct.CheckContractVersion(ae.NewObject(ae.Member{Key: "contractVersion", Value: version}))
		if !errors.Is(err, ct.ErrUnsupportedContractVersion) || !strings.Contains(err.Error(), "supports only 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 1.10, 1.11, 1.12, 1.13") {
			t.Fatalf("version %v: %v", version, err)
		}
	}
}

func TestEveryContractFileMatchesTheLock(t *testing.T) {
	lock := ct.MustReadJSON(filepath.Join(ct.Contract, "lock.json")).(*ae.Object)
	filesValue, _ := lock.Get("files")
	files := filesValue.(*ae.Object)
	var onDisk []string
	err := filepath.WalkDir(ct.Contract, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		relative, _ := filepath.Rel(ct.Contract, path)
		if entry.IsDir() {
			if relative != "." && strings.HasPrefix(entry.Name(), ".") {
				return filepath.SkipDir
			}
			return nil
		}
		if relative != "lock.json" && !strings.HasPrefix(entry.Name(), ".") {
			onDisk = append(onDisk, filepath.ToSlash(relative))
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	locked := files.Keys()
	sort.Strings(locked)
	sort.Strings(onDisk)
	if strings.Join(locked, "\n") != strings.Join(onDisk, "\n") {
		t.Fatalf("the lock lists %v, the contract holds %v", locked, onDisk)
	}
	for _, m := range files.Members() {
		data, err := os.ReadFile(filepath.Join(ct.Contract, filepath.FromSlash(m.Key)))
		if err != nil {
			t.Fatal(err)
		}
		digest := sha256.Sum256(data)
		if hex.EncodeToString(digest[:]) != m.Value {
			t.Errorf("%s does not match its digest in the lock", m.Key)
		}
	}
}

func TestEveryFixtureFamilyHasCases(t *testing.T) {
	entries, err := os.ReadDir(ct.Fixtures)
	if err != nil {
		t.Fatal(err)
	}
	var families []string
	for _, entry := range entries {
		if entry.IsDir() {
			families = append(families, entry.Name())
		}
	}
	if strings.Join(families, ",") != "benefit-estimate,benefit-select,embedding-summary,embedding-write,encoding,erased,error-class,finalize,holdout-assign,"+
		"holdout-outcomes,holdout-record,hybrid-merge,inject,inject-options,lessons-view,model-metadata,provider-benefit-estimate,"+
		"provider-holdout-assign,provider-holdout-record,record-injection,retrieval-policy,search,task-text,vector-search" {
		t.Fatalf("families: %v", families)
	}
	for _, family := range []string{"benefit-estimate", "benefit-select", "erased", "finalize", "holdout-outcomes", "holdout-record", "inject", "inject-options",
		"lessons-view", "provider-benefit-estimate", "provider-holdout-record", "record-injection", "search", "task-text"} {
		if len(ct.Cases(family)) == 0 {
			t.Fatalf("%s has no cases", family)
		}
	}
}

// ---- Contract 1.6: the vector families that need no database ----------------------------------------------------

func TestEmbeddingSummary(t *testing.T) {
	cases := ct.MustReadJSON(fixture("embedding-summary", "cases.json")).([]any)
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	text := func(o *ae.Object, key string) *string {
		value, _ := o.Get(key)
		s, ok := value.(string)
		if !ok {
			return nil
		}
		return &s
	}
	for i, item := range cases {
		c := item.(*ae.Object)
		description := *text(c, "description")
		t.Run(fmt.Sprintf("%d", i), func(t *testing.T) {
			summary := ae.RetrievalSummary(text(c, "taskId"), text(c, "taskSummary"), text(c, "lesson"))
			if summary != *text(c, "summary") {
				t.Fatalf("%s: summary %q, expected %q", description, summary, *text(c, "summary"))
			}
			if hash := ae.ContentHash(*text(c, "modelId"), summary); hash != *text(c, "contentHash") {
				t.Fatalf("%s: hash %s, expected %s", description, hash, *text(c, "contentHash"))
			}
		})
	}
}

func relevanceOf(t *testing.T, value any) float64 {
	t.Helper()
	if s, ok := value.(string); ok {
		if s != "NaN" {
			t.Fatalf("relevance %q", s)
		}
		return math.NaN()
	}
	f, err := strconv.ParseFloat(string(value.(json.Number)), 64)
	if err != nil {
		t.Fatal(err)
	}
	return f
}

func TestHybridMerge(t *testing.T) {
	cases := ct.MustReadJSON(fixture("hybrid-merge", "cases.json")).([]any)
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	type candidate struct {
		id        ae.UUID
		relevance float64
	}
	entries := func(c *ae.Object, key string) []candidate {
		value, _ := c.Get(key)
		var result []candidate
		for _, item := range value.([]any) {
			o := item.(*ae.Object)
			id, _ := o.Get("experienceId")
			relevance, _ := o.Get("relevance")
			result = append(result, candidate{ae.MustParseUUID(id.(string)), relevanceOf(t, relevance)})
		}
		return result
	}
	for i, item := range cases {
		c := item.(*ae.Object)
		t.Run(fmt.Sprintf("%d", i), func(t *testing.T) {
			var floor *float64
			if value, _ := c.Get("minimumVectorRelevance"); value != nil {
				f := relevanceOf(t, value)
				floor = &f
			}
			merged := ae.MergeChannels(entries(c, "text"), entries(c, "vector"),
				func(x candidate) ae.UUID { return x.id }, func(x candidate) float64 { return x.relevance }, floor)
			expectedValue, _ := c.Get("merged")
			expected := expectedValue.([]any)
			description, _ := c.Get("description")
			if len(merged) != len(expected) {
				t.Fatalf("%v: %d merged, expected %d", description, len(merged), len(expected))
			}
			for j, entry := range merged {
				e := expected[j].(*ae.Object)
				id, _ := e.Get("experienceId")
				from, _ := e.Get("from")
				relevance, _ := e.Get("relevance")
				want := relevanceOf(t, relevance)
				same := entry.Relevance == want || (math.IsNaN(want) && math.IsNaN(entry.Relevance))
				if entry.Item.id.String() != id.(string) || string(entry.Source) != from.(string) || !same {
					t.Fatalf("%v: entry %d is %v %v %v, expected %v %v %v", description, j, entry.Item.id,
						entry.Source, entry.Relevance, id, from, want)
				}
			}
		})
	}
}

func TestALoneSurrogateIsHashedAsTheReplacementCharacter(t *testing.T) {
	// A lone surrogate in three-byte form (what text carrying one looks like in a Go string), and any invalid byte.
	for _, bad := range []string{"a\xed\xa0\x80b", "a\xffb"} {
		if ae.ContentHash("m", bad) != ae.ContentHash("m", "a\uFFFDb") {
			t.Fatalf("%q is not hashed as U+FFFD", bad)
		}
	}
	if ae.ContentHash("m", "a\xed\xa0\x80\xed\xb0\x80b") != ae.ContentHash("m", "a\uFFFD\uFFFDb") {
		t.Fatal("two lone surrogates are two U+FFFD")
	}
}

func TestTheSummaryCapBacksOffASplitPairAndATrailingHighSurrogate(t *testing.T) {
	lesson := func(s string) string { return ae.RetrievalSummary(nil, nil, &s) }
	// 8,191 units and then an astral character (two units): the pair does not fit, so it is dropped whole.
	astral := strings.Repeat("a", 8191) + "\U0001F600"
	if got := lesson(astral); got != strings.Repeat("a", 8191) {
		t.Fatalf("%d units kept", ae.UTF16Len(got))
	}
	// 8,191 units and then a lone high surrogate and one more unit: the cut ends on the surrogate, which goes too.
	high := strings.Repeat("a", 8191) + "\xed\xa0\x80" + "b"
	if got := lesson(high); got != strings.Repeat("a", 8191) {
		t.Fatalf("%d units kept", ae.UTF16Len(got))
	}
	// A cut that leaves trailing spaces drops them.
	spaced := strings.Repeat("a", 8190) + " b c"
	if got := lesson(spaced); got != strings.Repeat("a", 8190)+" b" {
		t.Fatalf("%q", got[len(got)-4:])
	}
	if got := lesson(strings.Repeat("a", 8192)); ae.UTF16Len(got) != 8192 {
		t.Fatal("a summary of exactly 8,192 units is kept whole")
	}
}

// ---- finalize --------------------------------------------------------------------------------------------------

func TestFinalize(t *testing.T) {
	for _, name := range ct.Cases("finalize") {
		t.Run(name, func(t *testing.T) {
			finalized, capture, err := ct.RunFinalizeCase(ct.MustReadJSON(fixture("finalize", name, "input.json")))
			if err != nil {
				t.Fatal(err)
			}
			stored := finalized.Record.ToStored()
			assertJSONEqual(t, ct.MustReadJSON(fixture("finalize", name, "expected-capture.json")), capture,
				"expected-capture.json")
			assertJSONEqual(t, ct.MustReadJSON(fixture("finalize", name, "expected-record.json")), stored,
				"expected-record.json")
			assertJSONEqual(t, ct.MustReadJSON(fixture("finalize", name, "expected-event.json")),
				finalized.Event.ToJSON(), "expected-event.json")
			// The schema validation of every record is in the postgres module (schema_test.go), which may depend on a
			// JSON Schema validator: this module lists no third-party requirement.

			// What this package wrote reads back, and is written again, identical.
			read, err := ae.FromStored(ct.AsWritten(stored))
			if err != nil {
				t.Fatal(err)
			}
			assertJSONEqual(t, ct.AsWritten(stored), read.ToStored(), "the written record read back")
		})
	}
}

func TestAFinalizedRecordReadsBackIdentical(t *testing.T) {
	for _, name := range ct.Cases("finalize") {
		t.Run(name, func(t *testing.T) {
			expected := ct.MustReadJSON(fixture("finalize", name, "expected-record.json"))
			record, err := ae.FromStored(expected)
			if err != nil {
				t.Fatal(err)
			}
			assertJSONEqual(t, expected, record.ToStored(), "read back")
		})
	}
}

func TestArgumentNumbersReadBackAsDotNetReadsThem(t *testing.T) {
	stored := ct.MustReadJSON(fixture("finalize", "argument-numbers", "expected-record.json"))
	record, err := ae.FromStored(stored)
	if err != nil {
		t.Fatal(err)
	}
	arguments := record.Attempts[0].ToolCalls[0].Arguments
	expected := map[string]any{
		"negativeZero":         int64(0),
		"int64Max":             int64(9223372036854775807),
		"int64Min":             int64(-9223372036854775808),
		"beyondInt64":          1.5e19,
		"belowInt64":           -1e19,
		"twentyOneDigits":      1e20,
		"largestDouble":        1.7976931348623157e308,
		"fraction":             0.1,
		"exponent":             100.0,
		"negativeZeroFraction": 0.0,
	}
	for key, want := range expected {
		value, _ := arguments.Get(key)
		got, err := ae.ReadNumber(value.(json.Number))
		if err != nil || got != want {
			t.Errorf("%s: read %v (%T, %v), want %v (%T)", key, got, got, err, want, want)
		}
	}
	// Kept exactly as written.
	for key, literal := range map[string]string{"negativeZero": "-0", "negativeZeroFraction": "-0.0",
		"beyondInt64": "15000000000000000000", "exponent": "1E2"} {
		if value, _ := arguments.Get(key); value != json.Number(literal) {
			t.Errorf("%s is %v, not %s as written", key, value, literal)
		}
	}

	beyond := ct.MustReadJSON(fixture("finalize", "argument-numbers", "expected-record.json")).(*ae.Object)
	payload, _ := beyond.Get("payload")
	attempts, _ := payload.(*ae.Object).Get("attempts")
	calls, _ := attempts.([]any)[0].(*ae.Object).Get("toolCalls")
	calls.([]any)[0].(*ae.Object).Set("arguments", ae.NewObject(ae.Member{Key: "huge",
		Value: ae.NewObject(ae.Member{Key: "items", Value: []any{json.Number("1e400")}})}))
	if _, err := ae.FromStored(beyond); err == nil || !strings.Contains(err.Error(), "outside the contract") {
		t.Fatalf("a stored number beyond a double's range was read: %v", err)
	}
}

// ---- inject ----------------------------------------------------------------------------------------------------

func TestInject(t *testing.T) {
	for _, name := range ct.Cases("inject") {
		t.Run(name, func(t *testing.T) {
			entries := ct.MustReadJSON(fixture("inject", name, "records.json"))
			options := ct.MustReadJSON(fixture("inject", name, "options.json")).(*ae.Object)
			for _, entry := range entries.([]any) {
				stored, _ := entry.(*ae.Object).Get("record")
				record, err := ae.FromStored(stored)
				if err != nil {
					t.Fatal(err)
				}
				assertJSONEqual(t, stored, record.ToStored(), "records.json read back")
			}
			ranked, err := ae.RankedFromJSON(entries)
			if err != nil {
				t.Fatal(err)
			}
			get := func(key string) any { value, _ := options.Get(key); return value }
			number := func(key string) int {
				var n int
				if _, err := fmt.Sscan(string(get(key).(json.Number)), &n); err != nil {
					t.Fatal(err)
				}
				return n
			}
			approach := map[string][]string{}
			for _, m := range get("approachArguments").(*ae.Object).Members() {
				for _, key := range m.Value.([]any) {
					approach[m.Key] = append(approach[m.Key], key.(string))
				}
			}
			block, err := ae.RenderBlock(ranked, ae.BlockOptions{
				Rendering:         ae.Rendering(get("rendering").(string)),
				FailureDetail:     ae.FailureDetail(get("failureDetail").(string)),
				MaxRecords:        number("maxRecords"),
				MaxBytes:          number("maxBytes"),
				ApproachArguments: approach,
			})
			if err != nil {
				t.Fatal(err)
			}
			expected, err := os.ReadFile(fixture("inject", name, "expected-block.txt"))
			if err != nil {
				t.Fatal(err)
			}
			if block.Text != string(expected) {
				t.Fatalf("expected:\n%s\nactual:\n%s", expected, block.Text)
			}
		})
	}
}

// ---- task-text -------------------------------------------------------------------------------------------------

func TestTaskText(t *testing.T) {
	for _, name := range ct.Cases("task-text") {
		t.Run(name, func(t *testing.T) {
			c := ct.MustReadJSON(fixture("task-text", name, "messages.json")).(*ae.Object)
			get := func(o *ae.Object, key string) any { value, _ := o.Get(key); return value }
			var messages []ae.Message
			for _, item := range get(c, "messages").([]any) {
				m := item.(*ae.Object)
				message := ae.Message{Role: get(m, "role").(string)}
				if source, ok := get(m, "source").(string); ok {
					message.Source = source
				}
				if stamp, ok := get(m, "historicalReferenceStamp").(bool); ok {
					message.CarriesBlock = stamp
				}
				for _, partItem := range get(m, "contents").([]any) {
					part := partItem.(*ae.Object)
					content := ae.MessagePart{Type: get(part, "type").(string)}
					if text, ok := get(part, "text").(string); ok {
						content.Text = &text
					}
					message.Contents = append(message.Contents, content)
				}
				messages = append(messages, message)
			}
			var maxLength, threshold int
			fmt.Sscan(string(get(c, "maxLength").(json.Number)), &maxLength)
			fmt.Sscan(string(get(c, "followUpThreshold").(json.Number)), &threshold)
			actual, ok, err := ae.DeriveTaskText(messages, maxLength, threshold)
			if err != nil {
				t.Fatal(err)
			}
			expected := get(ct.MustReadJSON(fixture("task-text", name, "expected.json")).(*ae.Object), "taskText")
			if expected == nil {
				if ok {
					t.Fatalf("expected no task text, got %q", actual)
				}
				return
			}
			if !ok || actual != expected.(string) {
				t.Fatalf("expected %q, got %q (%v)", expected, actual, ok)
			}
		})
	}
}

// ---- error-class -----------------------------------------------------------------------------------------------

// errorCaseFiles are every cases*.json of the error-class family, in version order: cases.json is 1.0's, and each
// later minor version N adds cases-1.N.json. A file for a version this package does not support fails clearly.
func errorCaseFiles(t *testing.T) []string {
	t.Helper()
	byName := map[string]string{}
	var order []string
	for _, version := range ae.SupportedContractVersions {
		name := "cases-" + version + ".json"
		if strings.HasSuffix(version, ".0") {
			name = "cases.json"
		}
		byName[name] = version
		order = append(order, name)
	}
	matches, err := filepath.Glob(fixture("error-class", "cases*.json"))
	if err != nil {
		t.Fatal(err)
	}
	onDisk := map[string]bool{}
	for _, match := range matches {
		name := filepath.Base(match)
		if _, ok := byName[name]; !ok {
			t.Fatalf("error-class holds %s, for a contract version this package does not support (%v)", name,
				ae.SupportedContractVersions)
		}
		onDisk[name] = true
	}
	var files []string
	for _, name := range order {
		if onDisk[name] {
			files = append(files, name)
		}
	}
	return files
}

func TestTheErrorClassFilesAreOnePerSupportedVersion(t *testing.T) {
	if files := errorCaseFiles(t); strings.Join(files, ",") != "cases.json,cases-1.1.json" {
		t.Fatalf("files: %v", files)
	}
}

func TestErrorClass(t *testing.T) {
	for _, file := range errorCaseFiles(t) {
		for index, item := range ct.MustReadJSON(fixture("error-class", file)).([]any) {
			c := item.(*ae.Object)
			errValue, _ := c.Get("error")
			class, _ := c.Get("class")
			var err *string
			if s, ok := errValue.(string); ok {
				err = &s
			}
			if actual := ae.ErrorClass(err); actual != class {
				t.Errorf("%s[%d] %q: expected %q, got %q", file, index, truncate(errValue), class, actual)
			}
		}
	}
}

func truncate(value any) string {
	s, _ := value.(string)
	if len(s) > 60 {
		return s[:60] + "..."
	}
	return s
}

// ---- search: the stored records (the search itself needs PostgreSQL) -------------------------------------------

func TestSearchSeedRecordsValidateAndReadBack(t *testing.T) {
	for _, family := range []string{"search", "erased"} {
		for _, name := range ct.Cases(family) {
			t.Run(family+"/"+name, func(t *testing.T) {
				for _, stored := range ct.MustReadJSON(fixture(family, name, "seed.json")).([]any) {
					record, err := ae.FromStored(stored)
					if err != nil {
						t.Fatal(err)
					}
					assertJSONEqual(t, stored, record.ToStored(), "seed.json read back")
				}
			})
		}
	}
}

// ---- encoding (contract 1.1) -----------------------------------------------------------------------------------

// fromUTF16 is a scope field given as UTF-16 code units. A Go string cannot hold a lone surrogate: one decodes as
// U+FFFD, which is exactly how .NET's UTF-8 encoding writes it.
func fromUTF16(t *testing.T, value any) *string {
	if value == nil {
		return nil
	}
	var units []uint16
	for _, item := range value.([]any) {
		var unit int
		fmt.Sscan(string(item.(json.Number)), &unit)
		if unit < 0 || unit > 0xFFFF {
			t.Fatalf("%d is not a UTF-16 code unit (0 to 65535)", unit)
		}
		units = append(units, uint16(unit))
	}
	s := string(utf16.Decode(units))
	return &s
}

var (
	t0        = ae.MustParseInstant("2026-03-02T09:00:00+00:00")
	testRound = ae.ClosedVerificationRound{RoundID: ae.MustParseUUID("0000007e-0000-4000-8000-000000000000"),
		ArtifactRevision: "rev-1"}
)

func encode(t *testing.T, kind string, value any) (string, error) {
	switch kind {
	case "instant":
		instant, err := ae.ParseInstant(value.(string))
		return instant.String(), err
	case "duration":
		duration, err := ae.ParseDuration(value.(string))
		return duration.String(), err
	case "non-blank":
		// Accepted, and the check it names is the one the evidence satisfies.
		id := value.(string)
		evidence := ae.Evidence{EvidenceID: ae.MustParseUUID("000000e1-0000-4000-8000-000000000000"),
			VerificationRoundID: testRound.RoundID, ArtifactRevision: "rev-1", CheckID: id, Kind: "integration-test",
			Result: ae.CheckPass, Producer: "contract-ci", CapturedAt: t0}
		result, err := ae.Aggregate([]ae.Evidence{evidence}, []ae.RequiredCheck{{CheckID: id,
			ExpectedKind: "integration-test"}}, &testRound, "rev-1", t0)
		if err != nil {
			return "", err
		}
		if result.Outcome.Status != ae.VerificationVerified || len(result.Outcome.Evidence) != 1 ||
			result.Outcome.Evidence[0].CheckID != id {
			t.Fatalf("the check %q was not satisfied by its evidence", id)
		}
		return id, nil
	case "task-id":
		capture, err := ae.StartRun(ae.CapturedRun{RunID: ae.MustParseUUID("00000001-0000-4000-8000-000000000000"),
			TaskID: value.(string), Scope: ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"},
			Environment: ae.EnvironmentFingerprint{HostName: "h", RuntimeVersion: "r", OperatingSystem: "o"},
			Provenance:  ae.Provenance{Source: "s", RecordedAt: t0}, StartedAt: t0}, ae.DefaultSanitization(),
			ae.DefaultCaptureLimits())
		if err != nil {
			return "", err
		}
		return capture.Run().TaskID, nil
	case "experience-id":
		input := value.(*ae.Object)
		runID, _ := input.Get("runId")
		scopeValue, _ := input.Get("scopeUtf16")
		scope := scopeValue.(*ae.Object)
		field := func(name string) *string { v, _ := scope.Get(name); return fromUTF16(t, v) }
		required := func(name string) string {
			v := field(name)
			if v == nil {
				t.Fatalf("%s is null", name)
			}
			return *v
		}
		return ae.ExperienceIDFor(ae.MustParseUUID(runID.(string)), ae.Scope{
			TenantID: required("tenantId"), ApplicationID: required("applicationId"), ProjectID: required("projectId"),
			TeamID: field("teamId"), AgentID: field("agentId"), UserID: field("userId"),
		}).String(), nil
	}
	t.Fatalf("unknown encoding kind %q", kind)
	return "", nil
}

// refusal is what the refusal of an invalid case must say.
func refusal(t *testing.T, kind string, value any) string {
	switch kind {
	case "instant", "duration":
		return "'" + value.(string) + "'"
	case "non-blank":
		return "required check IDs must not be blank"
	case "task-id":
		return "task ID must not be blank"
	}
	t.Fatalf("an %s case is never invalid", kind)
	return ""
}

func TestEncoding(t *testing.T) {
	for index, item := range ct.MustReadJSON(fixture("encoding", "cases.json")).([]any) {
		c := item.(*ae.Object)
		get := func(key string) any { value, _ := c.Get(key); return value }
		kind, input, valid := get("kind").(string), get("input"), get("valid").(bool)
		t.Run(fmt.Sprintf("%d-%s", index, kind), func(t *testing.T) {
			actual, err := encode(t, kind, input)
			if valid {
				if err != nil || actual != get("canonical") {
					t.Fatalf("%v: expected %v, got %q (%v)", input, get("canonical"), actual, err)
				}
				return
			}
			if err == nil {
				t.Fatalf("%v was accepted as %q", input, actual)
			}
			if want := refusal(t, kind, input); !strings.Contains(err.Error(), want) {
				t.Fatalf("the refusal %q does not say %q", err, want)
			}
		})
	}
}

// ---- Dependencies ----------------------------------------------------------------------------------------------

// The core module requires nothing: its go.mod has no require directive, and no file of it (tests included) imports
// anything outside the standard library and the module itself.
func TestTheCoreModuleRequiresNothing(t *testing.T) {
	mod, err := os.ReadFile("go.mod")
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(mod), "require") {
		t.Fatalf("go.mod requires a module:\n%s", mod)
	}
	const self = "github.com/fabbrik/AgentExperience.NET/go"
	for _, pattern := range []string{"*.go", filepath.Join("internal", "*", "*.go")} {
		files, err := filepath.Glob(pattern)
		if err != nil {
			t.Fatal(err)
		}
		for _, file := range files {
			parsed, err := parser.ParseFile(token.NewFileSet(), file, nil, parser.ImportsOnly)
			if err != nil {
				t.Fatal(err)
			}
			for _, spec := range parsed.Imports {
				path := strings.Trim(spec.Path.Value, `"`)
				first, _, _ := strings.Cut(path, "/")
				if strings.Contains(first, ".") && path != self && !strings.HasPrefix(path, self+"/") {
					t.Errorf("%s imports %s", file, path)
				}
			}
		}
	}
}
