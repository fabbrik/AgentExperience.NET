package agentexperience

// The tool-result classifiers, with the cases of .NET's tests (story 21.2) as the Python port's tests
// (python/tests/test_classifiers.py) check them too.

import (
	"encoding/json"
	"strings"
	"testing"
)

func classify(classifier ToolResultClassifier, result any, tools ...string) string {
	tool := runTests
	if len(tools) > 0 {
		tool = tools[0]
	}
	class, failed := classifier(ToolResultContext{ToolCallID: NewRandomUUID(), ToolName: tool, Arguments: NewObject(),
		Result: result})
	if !failed {
		return "<none>"
	}
	return class
}

const none = "<none>"

func TestExitCodeClassifiesANonZeroExitCode(t *testing.T) {
	for _, c := range []struct{ text, expected string }{
		{"exit=0", none},
		{"exit=3", "exit 3"},
		{`{"exitCode":2}`, "exit 2"},
		{"EXIT CODE 4", "exit 4"},
		{"ok", none},
		{"exit 7", "exit 7"},
		{"tests finished, exit code: 12", "exit 12"},
		{`{"exit_code": 5, "stdout": "exit 0"}`, "exit 5"},
		{`{"ExitCode":"0"}`, none},
		{`{"stdout":"exit 3"}`, none},
		{`Result: {"exitCode": 9, truncated`, "exit 9"},
		{"exit=-1", "exit -1"},
		{"exit=007", "exit 7"},
		{"exit=000", none},
		{"exit=1073741819", ToolReportedErrorClass},
		{"exit=123456789012345678901234567890", ToolReportedErrorClass},
		{"exit 1.0", ToolReportedErrorClass},
		{"exit 0.0", none},
		{`{"exitCode":1.0}`, ToolReportedErrorClass},
		{`{"exitCode":1e0}`, ToolReportedErrorClass},
		{`{"exitCode":0.0}`, none},
		{`{"exitCode":123456789012345678901234567890}`, ToolReportedErrorClass},
		{"step: exit 0, then more work... final: exit=1", "exit 1"},
		{"exit status 2", "exit 2"},
		{"process exited with code 3", "exit 3"},
		{"returncode=4", "exit 4"},
		{`{"returncode": 5}`, "exit 5"},
		{`{"return_code": 6}`, "exit 6"},
		{`{"returnCode": "7"}`, "exit 7"},
		{`partial {"returnCode": 8, truncated`, "exit 8"},
		{`{"exitCode":"n/a","exit_code":3}`, "exit 3"},
		{`{"exitCode":"n/a","exitCode":3}`, "exit 3"},
		{`[{"exitCode":2}]`, none},
		{`["exit=3"]`, none},
		{"", none},
		{"exitcode=3", none},
		{"ſtatus", none},
	} {
		if actual := classify(ExitCode(), c.text); actual != c.expected {
			t.Errorf("ExitCode(%q) = %q, want %q", c.text, actual, c.expected)
		}
	}
}

func TestHTTPStatusClassifiesAnErrorStatus(t *testing.T) {
	for _, c := range []struct{ text, expected string }{
		{"HTTP 503", "HTTP 503"},
		{`{"status":200}`, none},
		{`{"statusCode":404}`, "HTTP 404"},
		{"HTTP/1.1 500 Internal Server Error", "HTTP 500"},
		{"status 429", "HTTP 429"},
		{"Status code: 401", "HTTP 401"},
		{"http 302", none},
		{"the page said HTTP 503", none},
		{`{"Status":"502"}`, "HTTP 502"},
		{`{"status":"ok"}`, none},
		{`{"status":700}`, none},
		{`{"status":"error","statusCode":500}`, "HTTP 500"},
		{`{"status":"failed","status_code":"503"}`, "HTTP 503"},
		{`[{"statusCode":404}]`, none},
		{"ok", none},
		{`body: {"status": 503}`, "HTTP 503"},
		{" HTTP 502", "HTTP 502"},
	} {
		if actual := classify(HTTPStatus(), c.text); actual != c.expected {
			t.Errorf("HTTPStatus(%q) = %q, want %q", c.text, actual, c.expected)
		}
	}
}

type processOutcome struct {
	ExitCode int    `json:"exitCode"`
	Stdout   string `json:"stdout"`
}

type httpOutcome struct {
	StatusCode int
}

type lateOutcome struct {
	Stdout   string `json:"stdout"`
	ExitCode int    `json:"exitCode"`
}

func TestBothClassifiersReadAMapAnObjectAndASerializedValue(t *testing.T) {
	quoted, _ := json.Marshal("exit=3")
	for _, c := range []struct {
		classifier ToolResultClassifier
		result     any
		expected   string
	}{
		{ExitCode(), map[string]any{"exitCode": 2}, "exit 2"},
		{ExitCode(), string(quoted), "exit 3"},
		{ExitCode(), json.RawMessage(quoted), "exit 3"},
		{ExitCode(), json.RawMessage(`{"exitCode": 4}`), "exit 4"},
		{ExitCode(), NewObject(Member{"exitCode", json.Number("5")}), "exit 5"},
		{ExitCode(), Ptr("exit=6"), "exit 6"},
		{HTTPStatus(), map[string]any{"statusCode": 404}, "HTTP 404"},
		{ExitCode(), processOutcome{6, "boom"}, "exit 6"},
		{ExitCode(), &processOutcome{6, "boom"}, "exit 6"},
		{ExitCode(), processOutcome{0, "exit 3"}, none},
		{HTTPStatus(), httpOutcome{503}, "HTTP 503"},
		{HTTPStatus(), httpOutcome{204}, none},
		{ExitCode(), nil, none},
		{ExitCode(), 42, none},
		{ExitCode(), 3.0, none},
		{ExitCode(), []any{map[string]any{"exitCode": 2}, "exit=3"}, none},
		{ExitCode(), []processOutcome{{2, "x"}}, none},
		{ExitCode(), func() {}, none},
	} {
		if actual := classify(c.classifier, c.result); actual != c.expected {
			t.Errorf("%#v gave %q, want %q", c.result, actual, c.expected)
		}
	}
}

func TestAJSONObjectIsReadByItsMembersWhateverItsSizeAndTextByItsLastMatch(t *testing.T) {
	output := strings.Repeat("x", 9000) + " exit 0"
	data, _ := json.Marshal(map[string]any{"stdout": output, "exitCode": 1})
	text := string(data)
	if len(text) <= 8192 || strings.Index(text, "exitCode") > 8192 {
		// encoding/json writes members in name order: exitCode first. Put it last, past 8 KiB, by hand too.
		late, _ := json.Marshal(lateOutcome{output, 1})
		if strings.Index(string(late), "exitCode") < 8192 {
			t.Fatal("the late member is not late")
		}
		if actual := classify(ExitCode(), string(late)); actual != "exit 1" {
			t.Fatalf("late member: %q", actual)
		}
	}
	for _, c := range []struct {
		result   any
		expected string
	}{
		{text, "exit 1"},
		{lateOutcome{output, 1}, "exit 1"},
		{lateOutcome{strings.Repeat("x", 9000) + " exit 3", 0}, none},
		// Text past 1 MiB: the last 64 KiB still holds the final exit code.
		{"exit 0\n" + strings.Repeat("y", 2*1024*1024) + "\nexit=2", "exit 2"},
		{strings.Repeat("z", 20_000) + " exit=2", "exit 2"},
	} {
		if actual := classify(ExitCode(), c.result); actual != c.expected {
			t.Errorf("gave %q, want %q", actual, c.expected)
		}
	}
}

func TestAMatchRunningIntoTheCutOfALongTextsHeadDoesNotCount(t *testing.T) {
	// The head of a text over 1 MiB ends at 64 KiB: "exit=12" cut there reads "exit=1", which must not count.
	head := strings.Repeat("x", 64*1024-len("exit=1")) + "exit=12"
	if actual := classify(ExitCode(), head+strings.Repeat("y", 2*1024*1024)); actual != none {
		t.Fatalf("the cut code counted: %q", actual)
	}
	// The tail starts at a cut too: a word running into it ("xexit=3") is no match, a word starting at it is.
	filler := strings.Repeat("y", 2*1024*1024)
	cutTail := filler + "x" + "exit=3" + strings.Repeat(" ", 64*1024-len("exit=3"))
	if actual := classify(ExitCode(), cutTail); actual != none {
		t.Fatalf("a word running into the tail's cut counted: %q", actual)
	}
	if actual := classify(ExitCode(), filler+" "+"exit=3"+strings.Repeat(" ", 64*1024-len("exit=3"))); actual != "exit 3" {
		t.Fatalf("a word starting at the tail's cut: %q", actual)
	}
	if actual := classify(HTTPStatus(), filler+"x"+`"status": 503`+strings.Repeat(" ", 64*1024-len(`"status": 503`))); actual != "HTTP 503" {
		t.Fatalf("a quoted pair at the tail's cut: %q", actual)
	}
	// A match that ends at the text's own end still counts.
	if actual := classify(ExitCode(), strings.Repeat("z", 2*1024*1024)+" exit=7"); actual != "exit 7" {
		t.Fatalf("the final code: %q", actual)
	}
}

func TestGivenToolNamesAClassifierAppliesToThoseToolsOnly(t *testing.T) {
	exit, http := ExitCode(runTests, "build"), HTTPStatus("fetch")
	for _, c := range []struct {
		classifier     ToolResultClassifier
		text, tool, ok string
	}{
		{exit, "exit=3", runTests, "exit 3"},
		{exit, "exit=3", "build", "exit 3"},
		{exit, "exit=3", "Run_Tests", none},
		{exit, "exit=3", "grep", none},
		{http, "HTTP 404", "fetch", "HTTP 404"},
		{http, "HTTP 404", runTests, none},
		{ExitCode(), "exit=3", "anything", "exit 3"},
	} {
		if actual := classify(c.classifier, c.text, c.tool); actual != c.ok {
			t.Errorf("%s on %s gave %q, want %q", c.text, c.tool, actual, c.ok)
		}
	}
	mustPanic(t, "a blank tool name", func() { ExitCode(runTests, " ") })
}

func TestEveryClassTheClassifiersReturnIsASingleErrorClassToken(t *testing.T) {
	var classes []string
	for _, value := range []string{"exit=1", "exit=-1", "exit=999999", "exit=-999999", "exit=1234567",
		`{"exitCode":255}`, "exit 1.5", `{"exitCode":1e3}`} {
		classes = append(classes, classify(ExitCode(), value))
	}
	for _, value := range []string{"HTTP 400", "HTTP 599", "status 500", `{"status":404}`} {
		classes = append(classes, classify(HTTPStatus(), value))
	}
	for _, class := range classes {
		if class == none || !IsSingleToken(class) {
			t.Errorf("%q is not a single token", class)
		}
	}
}

func TestOnlyAWholeSingleTokenCountsAsAnErrorClass(t *testing.T) {
	for value, single := range map[string]bool{
		"exit 3": true, "QuotaExceededError": true, "HTTP 503": true, "status 429": true, "ENOENT": true,
		"Timeout": true, "TimeoutException": true, ToolReportedErrorClass: true, "Exit 3": false,
		"exit code 3": false, "lockHeldError": false, "http 503": false, "ExitCode3": false, "a sentence": false,
		"": false, "exit 3, HTTP 503": false,
	} {
		if IsSingleToken(value) != single {
			t.Errorf("IsSingleToken(%q) != %v", value, single)
		}
	}
}
