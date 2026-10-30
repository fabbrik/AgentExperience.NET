package agentexperience

// .NET parity and robustness beyond what the contract fixtures reach: each expectation is what the .NET library
// does, as the Python port's tests (python/tests/test_units.py, test_parity.py) check it too.

import (
	"encoding/json"
	"errors"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestRoundTripFormatIsDotNets(t *testing.T) {
	for value, text := range map[float64]string{
		1.0: "1", 0.5: "0.5", 2.0 / 3.0: "0.6666666666666666", 0.0001: "0.0001", 0.00001: "1E-05",
		123456789012345.0: "123456789012345", 1e15: "1E+15", -2.5: "-2.5", 1.5e-7: "1.5E-07", 1e20: "1E+20",
		1.5e19: "1.5E+19", 1.7976931348623157e308: "1.7976931348623157E+308",
	} {
		if actual := formatRoundTrip(value); actual != text {
			t.Errorf("%v: %q, want %q", value, actual, text)
		}
	}
	if formatRoundTrip(math.Copysign(0, -1)) != "-0" || formatRoundTrip(0) != "0" {
		t.Error("zero keeps its sign")
	}
	if formatRoundTrip(math.Inf(1)) != "Infinity" || formatRoundTrip(math.Inf(-1)) != "-Infinity" {
		t.Error("the infinities")
	}
}

func TestFixedFormatRoundsLikeDotNet(t *testing.T) {
	for _, c := range []struct {
		value    float64
		decimals int
		text     string
	}{{0.8374999999999999, 3, "0.838"}, {0.125, 2, "0.13"}, {2.0 / 3.0, 2, "0.67"}, {1.0, 3, "1.000"},
		{0, 2, "0.00"}, {0.005, 2, "0.01"}, {12.3456, 0, "12"}} {
		if actual := formatFixed(c.value, c.decimals); actual != c.text {
			t.Errorf("%v (%d): %q, want %q", c.value, c.decimals, actual, c.text)
		}
	}
}

func TestLengthsAndCutsAreInUTF16CodeUnits(t *testing.T) {
	if UTF16Len("a🧾b") != 4 || UTF16Prefix("ab🧾", 3) != "ab" || UTF16Prefix("ab🧾", 4) != "ab🧾" {
		t.Fatal("UTF-16 lengths")
	}
	if clip("ab🧾cd", 4) != "ab…" {
		t.Fatalf("clip: %q", clip("ab🧾cd", 4))
	}
}

func TestUTF16OrderIsOrdinal(t *testing.T) {
	// U+FF5E sorts after U+1F600 in UTF-8 (code point) order, before it in UTF-16 order (a surrogate is 0xD83D).
	if utf16Compare("\U0001F600", "\uFF5E") >= 0 {
		t.Fatal("a supplementary character sorts before U+FF5E in UTF-16")
	}
}

func TestInstantsKeepSevenDigitsAndWriteTheUTCOffset(t *testing.T) {
	instant := MustParseInstant("2026-03-02T10:00:30.1234567+01:00")
	if instant.String() != "2026-03-02T09:00:30.1234567+00:00" {
		t.Fatal(instant)
	}
	if instant.TruncateToMicroseconds().String() != "2026-03-02T09:00:30.123456+00:00" {
		t.Fatal(instant.TruncateToMicroseconds())
	}
	if MustParseInstant("2026-03-02T09:00:30Z").String() != "2026-03-02T09:00:30+00:00" {
		t.Fatal("Z")
	}
	if back, err := InstantFromTime(instant.Time()); err != nil || back != instant {
		t.Fatal("time.Time keeps the ticks")
	}
	if MustParseInstant("2026-03-02T23:59:59-14:00").String() != "2026-03-03T13:59:59+00:00" {
		t.Fatal("extreme offset")
	}
	for _, text := range []string{"2026-03-02T24:00:00+00:00", "2026-03-02T09:60:00+00:00", "2026-03-02T09:00:60+00:00",
		"2026-03-02T09:00:00+14:01", "2026-03-02T09:00:00+15:00", "2026-03-02T09:00:00+01:60",
		"0000-01-01T00:00:00+00:00", "0001-01-01T00:00:00+01:00", "2026-03-02T09:00:00", "2026-03-02T09:00:00+00:00\n",
		"\u0662026-03-02T09:00:00+00:00"} {
		if _, err := ParseInstant(text); err == nil {
			t.Errorf("%q was accepted", text)
		}
	}
	if MustParseInstant("9999-12-31T23:59:59.9999999+00:00").String() != "9999-12-31T23:59:59.9999999+00:00" {
		t.Error("the last instant")
	}
}

func TestDurationsRoundTripInTheConstantFormat(t *testing.T) {
	for _, text := range []string{"00:00:03.5000000", "00:00:01", "1.02:03:04", "-00:00:00.0000001",
		"10675199.02:48:05.4775807", "-10675199.02:48:05.4775808"} {
		if actual := MustParseDuration(text).String(); actual != text {
			t.Errorf("%q: %q", text, actual)
		}
	}
	for _, text := range []string{"24:00:00", "00:60:00", "00:00:60", "10675199.02:48:05.4775808",
		"99999999999999999999.00:00:00", "00:00:00.12345678"} {
		if _, err := ParseDuration(text); err == nil {
			t.Errorf("%q was accepted", text)
		}
	}
}

func TestANullScopeFieldAndAnEmptyOneDeriveDifferentIDs(t *testing.T) {
	run := MustParseUUID("000000f1-0000-4000-8000-000000000000")
	absent := DeriveID(run, ExperienceIDTag, &Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"})
	empty := DeriveID(run, ExperienceIDTag, &Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p", TeamID: Ptr("")})
	if absent == empty || absent[6]>>4 != 8 || absent[8]&0xC0 != 0x80 {
		t.Fatalf("%v %v", absent, empty)
	}
	// Anything that is not a valid character is U+FFFD, as .NET encodes a lone surrogate.
	invalid := DeriveID(run, ExperienceIDTag, &Scope{TenantID: "t\xff", ApplicationID: "a", ProjectID: "p"})
	replaced := DeriveID(run, ExperienceIDTag, &Scope{TenantID: "t\uFFFD", ApplicationID: "a", ProjectID: "p"})
	if invalid != replaced {
		t.Fatal("an invalid byte is not U+FFFD")
	}
}

func TestOrdinalIgnoreCaseIsDotNets(t *testing.T) {
	for r, upper := range map[rune]rune{'ı': 'ı', 'ſ': 'ſ', 'İ': 'İ', 'i': 'I', 'K': 'K', 'ǅ': 'Ǆ'} {
		if ordinalUpper(r) != upper {
			t.Errorf("%c: %c", r, ordinalUpper(r))
		}
	}
	if equalsIgnoreCase("paſsword", "PASSWORD") || !equalsIgnoreCase("ApiKey", "APIKEY") {
		t.Fatal("ordinal ignore case")
	}
}

func TestLabelsAndMarkersAreMatchedCaseInsensitivelyAsDotNetDoes(t *testing.T) {
	for in, want := range map[string]string{
		"evidence: x":       "[delimiter removed] x",
		"Evıdence: x":       "Evıdence: x",
		"--- end withdrawn": "[delimiter removed]",
		"--- END WıTHDRAWN": "--- END WıTHDRAWN",
		"--- END WİTHDRAWN": "--- END WİTHDRAWN",
		"--- ReCoRd 1":      "[delimiter removed] 1",
		"a\r\n  tried: x":   "a\n  [delimiter removed] x",
		"———":               "———",
	} {
		if actual := cleanBlockText(&in); actual != want {
			t.Errorf("%q: %q, want %q", in, actual, want)
		}
	}
}

func TestSanitizationRedactsSecretsAndRefusesWhatIsTooDeep(t *testing.T) {
	policy := SanitizationAllowing("a", "headers").ToolArguments
	sanitized, err := policy.Sanitize(ObjectFromMap(map[string]any{
		"API_KEY": map[string]any{"x": 1},
		"headers": map[string]any{"Token": "t", "other": "o"},
		"b":       1,
	}))
	if err != nil {
		t.Fatal(err)
	}
	if text, _ := MarshalJSONValue(sanitized); string(text) != `{"API_KEY":"","headers":{"Token":""}}` {
		t.Fatalf("%s", text)
	}
	deep := ObjectFromMap(map[string]any{"a": map[string]any{"a": map[string]any{"a": map[string]any{
		"a": map[string]any{"a": "deep"}}}}})
	if _, err := policy.Sanitize(deep); !errors.Is(err, ErrSanitizationRejected) {
		t.Fatalf("too deep: %v", err)
	}
	for _, name := range []string{"a.b", "a[0]", strings.Repeat("n", 101)} {
		if _, err := policy.Sanitize(NewObject(Member{name, 1})); !errors.Is(err, ErrSanitizationRejected) {
			t.Errorf("%q: %v", name, err)
		}
	}
	wide := &Object{}
	for i := 0; i < 51; i++ {
		wide.Set(strings.Repeat("k", i+1), 1)
	}
	if _, err := policy.Sanitize(wide); !errors.Is(err, ErrSanitizationRejected) {
		t.Errorf("too wide: %v", err)
	}
	// A secret name is matched ordinally: the long s is not s.
	secrets := SanitizationAllowing("paſsword").ToolArguments
	kept, err := secrets.Sanitize(NewObject(Member{"paſsword", "kept"}, Member{"PASSWORD", "x"}))
	if text, _ := MarshalJSONValue(kept); err != nil || string(text) != `{"paſsword":"kept","PASSWORD":""}` {
		t.Fatalf("%s %v", text, err)
	}
}

func TestANonFiniteNumberIsRefusedAtCapture(t *testing.T) {
	// As .NET does from contract 1.2: a NaN, an infinity, or a number a double cannot hold finitely, an integer
	// literal included, is refused; a secret or omitted field holding one is never read.
	midpoint := "179769313486231580793728971405303415079934132710037826936173778980444968292764750946649017977587207096330286416692887910946555547851940402630657488671505820681908902000708383676273854845817711531764475730270069855571366959622842914819860834936475292719074168444365510704342711559699508093042880177904174497792"
	policy := SanitizationAllowing("n").ToolArguments
	for _, value := range []any{math.NaN(), math.Inf(1), math.Inf(-1), float32(math.Inf(1)), json.Number("1e400"),
		json.Number("-1e400"), json.Number("1" + strings.Repeat("0", 400)), json.Number(midpoint)} {
		if _, err := policy.Sanitize(NewObject(Member{"n", value})); !errors.Is(err, ErrSanitizationRejected) {
			t.Errorf("%v: %v", value, err)
		}
		if _, err := policy.Sanitize(NewObject(Member{"n", []any{1, value}})); !errors.Is(err, ErrSanitizationRejected) {
			t.Errorf("[1, %v]: %v", value, err)
		}
		kept, err := policy.Sanitize(NewObject(Member{"token", value}, Member{"other", value}))
		if text, _ := MarshalJSONValue(kept); err != nil || string(text) != `{"token":""}` {
			t.Errorf("a secret or omitted %v: %s %v", value, text, err)
		}
	}
	nested := SanitizationAllowing("n", "m").ToolArguments
	_, err := nested.Sanitize(NewObject(Member{"n", NewObject(Member{"m", []any{1, json.Number("1e400")}})}))
	if err == nil || !strings.Contains(err.Error(), "'n.m[1]'") || strings.Contains(err.Error(), "400") {
		t.Fatalf("the refusal names the path, never the value: %v", err)
	}
	// Just below the rounding midpoint above the largest double, and one that rounds down to it, are kept as given.
	below := midpoint[:len(midpoint)-1] + "1"
	for _, literal := range []string{below, "1.7976931348623158e308", "1.5e19", "1e-400"} {
		kept, err := policy.Sanitize(NewObject(Member{"n", json.Number(literal)}))
		if value, _ := kept.Get("n"); err != nil || value != json.Number(literal) {
			t.Errorf("%s: %v %v", literal, value, err)
		}
	}
}

var (
	t0     = MustParseInstant("2026-03-02T09:00:00+00:00")
	second = MustParseDuration("00:00:01")
	scope  = Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
)

func testCapture(t *testing.T, limits CaptureLimits, sanitization Sanitization) *RunCapture {
	t.Helper()
	capture, err := StartRun(CapturedRun{RunID: MustParseUUID("00000001-0000-4000-8000-000000000000"), TaskID: "task",
		Scope: scope, Environment: EnvironmentFingerprint{HostName: "h", RuntimeVersion: "r", OperatingSystem: "o"},
		Provenance: Provenance{Source: "s", RecordedAt: t0}, StartedAt: t0}, sanitization, limits)
	if err != nil {
		t.Fatal(err)
	}
	return capture
}

func testAttempt(n byte, calls ...RawToolCall) RawAttempt {
	id := UUID{15: n, 14: 1}
	return RawAttempt{AttemptID: id, StartedAt: t0, Duration: second, ToolCalls: calls, Result: Ptr("ok")}
}

func testCall(n byte, arguments *Object) RawToolCall {
	return RawToolCall{ToolCallID: UUID{15: n, 14: 2}, ToolName: "tool", Arguments: arguments, StartedAt: t0,
		Duration: second, Result: Ptr("r")}
}

func TestWhitespaceIsDotNetsForTaskIDsCheckIDsAndRevisions(t *testing.T) {
	if testCapture(t, CaptureLimits{}, DefaultSanitization()).Run().TaskID != "task" {
		t.Fatal("task")
	}
	for _, blank := range []string{" ", "\u0085", "\u3000", ""} {
		_, err := StartRun(CapturedRun{RunID: UUID{15: 1}, TaskID: blank}, DefaultSanitization(), CaptureLimits{})
		if err == nil {
			t.Errorf("the task ID %q was accepted", blank)
		}
	}
	if _, err := StartRun(CapturedRun{RunID: UUID{15: 1}, TaskID: "\x1c"}, DefaultSanitization(), CaptureLimits{}); err != nil {
		t.Fatalf("U+001C is not whitespace: %v", err)
	}
	round := ClosedVerificationRound{RoundID: UUID{15: 9}, ArtifactRevision: "\x1c"}
	result, err := Aggregate(nil, []RequiredCheck{{"\x1c", "\x1c"}}, &round, "\x1c", t0)
	if err != nil || result.Outcome.Status != VerificationUnknown {
		t.Fatalf("%v %v", result.Outcome.Status, err)
	}
	for _, blank := range []string{" ", "\u0085"} {
		if _, err := Aggregate(nil, nil, &round, blank, t0); err == nil {
			t.Error("a blank revision")
		}
		if _, err := Aggregate(nil, []RequiredCheck{{blank, "k"}}, &round, "rev", t0); err == nil {
			t.Error("a blank check ID")
		}
		if _, err := Aggregate(nil, []RequiredCheck{{"c", blank}}, &round, "rev", t0); err == nil {
			t.Error("a blank kind")
		}
	}
	if _, err := Aggregate(nil, []RequiredCheck{{"c", "k"}, {"c", "k"}}, &round, "rev", t0); err == nil {
		t.Error("a duplicate check ID")
	}
}

func TestCaptureAnswersEachOutcome(t *testing.T) {
	capture := testCapture(t, CaptureLimits{MaxAttemptsPerRun: 2, MaxToolCallsPerAttempt: 50, MaxResultLength: 4000,
		MaxErrorLength: 4000}, DefaultSanitization())
	first := testAttempt(0, testCall(0, nil))
	if capture.appendAttempt(first) != OutcomeRecorded {
		t.Fatal("first")
	}
	again := testAttempt(0, testCall(0, &Object{}))
	if outcome := capture.appendAttempt(again); outcome != OutcomeDuplicateNoOp {
		t.Fatalf("the same content again: %s", outcome)
	}
	changed := first
	changed.Result = Ptr("different")
	if capture.appendAttempt(changed) != OutcomeConflict {
		t.Fatal("changed")
	}
	if capture.appendAttempt(testAttempt(1)) != OutcomeRecorded || capture.appendAttempt(testAttempt(2)) != OutcomeCapacityExceeded {
		t.Fatal("capacity")
	}
	if attempts := capture.Run().Attempts; len(attempts) != 2 || attempts[1].SequenceNumber != 1 {
		t.Fatal("numbering")
	}
	capture.Complete(RunCompleted, t0)
	if capture.appendAttempt(testAttempt(3)) != OutcomeConflict {
		t.Fatal("after completion")
	}
	rejected := testCapture(t, CaptureLimits{}, SanitizationAllowing("n"))
	if rejected.appendAttempt(testAttempt(0, testCall(0, NewObject(Member{"n", math.NaN()})))) != OutcomeSanitizationRejected ||
		len(rejected.Run().Attempts) != 0 {
		t.Fatal("a NaN argument")
	}
}

func TestAnOverlongResultOrErrorIsReplacedByThePlaceholder(t *testing.T) {
	capture := testCapture(t, CaptureLimits{MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 10,
		MaxErrorLength: 60}, DefaultSanitization())
	long := strings.Repeat("x", 61)
	attempt := testAttempt(0)
	attempt.Result, attempt.Error = &long, &long
	capture.appendAttempt(attempt)
	kept := capture.Run().Attempts[0]
	if *kept.Result != TruncationPlaceholder[:10] || *kept.Error != TruncationPlaceholder {
		t.Fatalf("%q %q", *kept.Result, *kept.Error)
	}
	if err := (CaptureLimits{MaxAttemptsPerRun: 1}).Validate(); err == nil {
		t.Fatal("a zero limit")
	}
}

func TestExposuresKeepTheLowestRevisionAndAtMost256Records(t *testing.T) {
	capture := testCapture(t, CaptureLimits{}, DefaultSanitization())
	record := UUID{15: 0x51}
	for _, c := range []struct {
		revision int64
		want     AppendOutcome
	}{{3, OutcomeRecorded}, {5, OutcomeDuplicateNoOp}, {1, OutcomeRecorded}} {
		if outcome, err := capture.RecordExposure(RunExposure{record, c.revision}); err != nil || outcome != c.want {
			t.Fatalf("revision %d: %s %v", c.revision, outcome, err)
		}
	}
	if exposed := capture.Run().Provenance.ExposedTo; len(exposed) != 1 || exposed[0].Revision != 1 {
		t.Fatal(exposed)
	}
	capture = testCapture(t, CaptureLimits{}, DefaultSanitization())
	var many []RunExposure
	for n := 0; n < 256; n++ {
		many = append(many, RunExposure{UUID{14: byte(n >> 8), 15: byte(n), 0: 1}, 1})
	}
	if outcome, _ := capture.RecordExposure(many...); outcome != OutcomeRecorded {
		t.Fatal(outcome)
	}
	if outcome, _ := capture.RecordExposure(RunExposure{UUID{0: 9}, 1}); outcome != OutcomeCapacityExceeded {
		t.Fatal(outcome)
	}
	if _, err := capture.RecordExposure(RunExposure{UUID{}, 1}); err == nil {
		t.Fatal("an empty UUID")
	}
}

func TestErrorClassMatchesDotNet(t *testing.T) {
	for in, want := range map[string]string{
		"exit\x1c2":                  "unclassified error",
		"exit 2":                     "exit 2",
		"Exited with code: -3":       "exit -3",
		"x\x1cENOENT":                "ENOENT",
		"\u0301ENOENT":               "unclassified error",
		"ENOENT\u0301":               "unclassified error",
		"\u200dENOENT":               "unclassified error",
		"\u0903ENOENT":               "ENOENT",
		"\u203fENOENT":               "unclassified error",
		"ſtatus 503":                 "unclassified error",
		"STATUS 503":                 "status 503",
		"tımed out":                  "unclassified error",
		"\U0001D400ENOENT":           "ENOENT",
		"EFOO ENOENT":                "ENOENT",
		"HTTP/1.1 503 then http 404": "HTTP 503, HTTP 404",
		"Response status code does not indicate success: 404 (Not Found).": "HTTP 404",
		"System.TimeoutException: timed out":                               "TimeoutException",
		"a timeout, ETIMEDOUT, IOError, exit 1":                            "Timeout, ETIMEDOUT, IOError",
		"ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOException":               "unclassified error",
	} {
		if actual := ErrorClassOf(in); actual != want {
			t.Errorf("%q: %q, want %q", in, actual, want)
		}
	}
	long := strings.Repeat("x", 8180) + " ENOENT"
	if ErrorClassOf(strings.Repeat("x", 8192)+" ENOENT") != Unclassified || ErrorClassOf(long) != "ENOENT" {
		t.Fatal("only the first 8,192 code units are scanned")
	}
	if !IsSingleToken("exit 3") || IsSingleToken("Exit 3") || IsSingleToken("exit code 3") || IsSingleToken("") {
		t.Fatal("single tokens")
	}
	// Linear, whatever the input: a long run of whitespace after a token start is not explored twice.
	spaces := "exit" + strings.Repeat(" ", 8000) + "x"
	if ErrorClassOf(spaces) != Unclassified {
		t.Fatal("spaces")
	}
}

func TestAnArgumentNumberRendersAsDotNetReadsIt(t *testing.T) {
	for literal, text := range map[string]string{
		"9223372036854775807": "9223372036854775807", "100000000000000000000": "1E+20",
		"1" + strings.Repeat("0", 400): "Infinity", "-1" + strings.Repeat("0", 400): "-Infinity",
		"-0": "0", "-0.0": "-0", "1E2": "100", "0.1": "0.1",
	} {
		if actual := argumentValue(json.Number(literal)); actual != text {
			t.Errorf("%s: %q, want %q", literal[:min(len(literal), 30)], actual, text)
		}
	}
	if argumentValue(NewObject()) != argumentNotShown || argumentValue([]any{}) != argumentNotShown {
		t.Fatal("not shown")
	}
}

func TestBlockOptionsRefuseUnsafeApproachArguments(t *testing.T) {
	for _, arguments := range []map[string][]string{
		{"tool": {"a=b"}}, {"tool": {"a,b"}}, {"tool": {"a b"}}, {"tool": {strings.Repeat("k", 65)}},
		{"tool": {"k", "k"}}, {"tool": {""}}, {" ": {"k"}}, {"tool": {"k\u200b"}}, {"tool": {"\U0001F600"}},
	} {
		if err := (BlockOptions{ApproachArguments: arguments}).Validate(); err == nil {
			t.Errorf("%v was accepted", arguments)
		}
	}
	if err := (BlockOptions{ApproachArguments: map[string][]string{"tool": {strings.Repeat("k", 64), "nested.key"}}}).Validate(); err != nil {
		t.Fatal(err)
	}
	if err := (BlockOptions{Rendering: "Fancy"}).Validate(); err == nil {
		t.Fatal("an unknown rendering")
	}
}

func repository() string {
	_, file, _, _ := runtime.Caller(0)
	return filepath.Join(filepath.Dir(file), "..")
}

func readFixture(t *testing.T, parts ...string) any {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(append([]string{repository(), "contract", "fixtures"}, parts...)...))
	if err != nil {
		t.Fatal(err)
	}
	value, err := DecodeJSON(data)
	if err != nil {
		t.Fatal(err)
	}
	return value
}

func TestARankingWithoutAScoreReadsAsNaN(t *testing.T) {
	stored := readFixture(t, "finalize", "verified-after-one-failed-attempt", "expected-record.json")
	ranked, err := RankedFromJSON([]any{
		NewObject(Member{"record", stored}, Member{"ranking", NewObject(Member{"score", nil},
			Member{"components", []any{}})}),
		NewObject(Member{"record", stored}),
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range ranked {
		if !math.IsNaN(entry.Score) {
			t.Fatal(entry.Score)
		}
	}
	block, err := RenderBlock(ranked[:1], BlockOptions{Rendering: RenderingVerbose})
	if err != nil || !strings.Contains(block.Text, "score (unavailable) from (none recorded)") {
		t.Fatalf("%v\n%s", err, block.Text)
	}
}

func seedRows(t *testing.T) []ExperienceRecord {
	t.Helper()
	var records []ExperienceRecord
	for _, stored := range readFixture(t, "search", "more-than-the-candidate-limit", "seed.json").([]any) {
		record, err := FromStored(stored)
		if err != nil {
			t.Fatal(err)
		}
		records = append(records, record)
	}
	return records
}

func TestRankFailsTheWholeResultForARowOutsideTheScope(t *testing.T) {
	records := seedRows(t)
	scope := records[0].Scope
	rows := []SearchRow{{Record: records[0], Relevance: 0.5}, {Record: records[1], Relevance: 0.5},
		{Record: records[2], Relevance: 0.5}}
	other := records[1]
	other.Scope.AgentID = Ptr("other")
	rows[1] = SearchRow{Record: other, Relevance: 0.5}
	if _, err := Rank(rows, scope, t0, nil, false); !errors.Is(err, ErrSearchSafety) {
		t.Fatal(err)
	}
	rows[1] = SearchRow{Record: records[1], Relevance: 0.5, SharedByGrant: true}
	if _, err := Rank(rows, scope, t0, nil, false); !errors.Is(err, ErrSearchSafety) {
		t.Fatal(err)
	}
}

func TestRankFailsOnADuplicateWithinTheCandidateLimitOnly(t *testing.T) {
	records := seedRows(t)
	scope := records[0].Scope
	var rows []SearchRow
	for _, record := range records[:50] {
		rows = append(rows, SearchRow{Record: record, Relevance: 0.5})
	}
	duplicate := append(append([]SearchRow(nil), rows[:49]...), rows[0])
	if _, err := Rank(duplicate, scope, t0, nil, false); !errors.Is(err, ErrSearchSafety) {
		t.Fatal(err)
	}
	// Row 51 is never considered: a duplicate there is dropped with it, and the result is truncated.
	result, err := Rank(append(append([]SearchRow(nil), rows...), rows[0]), scope, t0, nil, false)
	if err != nil || !result.Truncated || len(result.Ranked) != 50 {
		t.Fatalf("%v %v %d", err, result.Truncated, len(result.Ranked))
	}
}

func TestRankLeavesOutIneligibleAndExcludedModelAuthoredRows(t *testing.T) {
	records := seedRows(t)[:3]
	scope := records[0].Scope
	quarantined := records[1]
	quarantined.Status = StatusQuarantined
	modelAuthored := records[2]
	reflection := *modelAuthored.Reflection
	reflection.Authorship = AuthorshipModel
	modelAuthored.Reflection = &reflection
	rows := []SearchRow{{Record: records[0], Relevance: 0.5}, {Record: quarantined, Relevance: 0.5},
		{Record: modelAuthored, Relevance: 0.5}}
	result, err := Rank(rows, scope, t0, nil, false)
	if err != nil || len(result.Ranked) != 2 {
		t.Fatalf("%v %d", err, len(result.Ranked))
	}
	excluded, err := Rank(rows, scope, t0, nil, true)
	if err != nil || len(excluded.Ranked) != 1 || excluded.Ranked[0].Record.ExperienceID != records[0].ExperienceID ||
		excluded.Truncated {
		t.Fatalf("%v %d", err, len(excluded.Ranked))
	}
}

func TestSearchParametersRefuseOverlongOrBlankTextAndGrants(t *testing.T) {
	if err := NewSearchParameters(scope, strings.Repeat("x", 4097)).Validate(); err == nil ||
		!strings.Contains(err.Error(), "refused, not cut") {
		t.Fatal(err)
	}
	if NewSearchParameters(scope, "   ").Validate() == nil || NewSearchParameters(scope, "\u0085").Validate() == nil {
		t.Fatal("blank text")
	}
	grants := NewSearchParameters(scope, "text")
	grants.WithGrants = true
	if grants.Validate() == nil {
		t.Fatal("grants")
	}
	parameters := NewSearchParameters(scope, strings.Repeat("x", 4096))
	values, err := parameters.Values()
	if err != nil || values["limit"] != int32(51) || values["minimum_matched_terms"] != int32(3) ||
		values["min_confidence"] != 0.5 || values["team_id"] != nil {
		t.Fatal(values)
	}
}

func TestBoundsAreDeclaredWithAnEqualsPrefix(t *testing.T) {
	parameters := AuthorizationBounds{TenantID: "t", ApplicationID: Ptr("a")}.Parameters()
	want := map[string]string{"auth_tenant": "=t", "auth_application": "=a", "auth_project": "", "auth_team": "",
		"auth_agent": "", "auth_user": ""}
	for key, value := range want {
		if parameters[key] != value {
			t.Fatalf("%s: %q", key, parameters[key])
		}
	}
	if BoundsOf(Scope{TenantID: "t", ApplicationID: "", ProjectID: "p"}).Parameters()["auth_application"] != "=" {
		t.Fatal("an empty bound is not no bound")
	}
}

func TestRelevanceIsWidenedFromFloat4WithoutDecimalRounding(t *testing.T) {
	if WidenReal(0.6) != 0.6000000238418579 || WidenReal(0.6000000238418579) != 0.6000000238418579 {
		t.Fatal(WidenReal(0.6))
	}
}

func TestJSONKeepsNumbersAndOrderAsWritten(t *testing.T) {
	text := `{"b":1E2,"a":[-0.0,15000000000000000000,"😀` + `\u2028` + `"],"c":{"z":null,"y":true}}`
	value, err := DecodeJSON([]byte(text))
	if err != nil {
		t.Fatal(err)
	}
	written, err := MarshalJSONValue(value)
	if err != nil || string(written) != `{"b":1E2,"a":[-0.0,15000000000000000000,"😀`+`\u2028`+`"],"c":{"z":null,"y":true}}` {
		t.Fatalf("%s %v", written, err)
	}
	if _, err := DecodeJSON([]byte(`{} {}`)); err == nil {
		t.Fatal("trailing data")
	}
	if _, err := MarshalJSONValue(math.NaN()); err == nil {
		t.Fatal("NaN written")
	}
	if _, err := MarshalJSONValue(json.Number("NaN")); err == nil {
		t.Fatal("a non-number written")
	}
}

func TestARecordHoldingANonFiniteNumberIsNotWritten(t *testing.T) {
	record := ExperienceRecord{ReuseConfidence: math.NaN()}
	if _, err := record.MarshalJSON(); err == nil {
		t.Fatal("NaN was written")
	}
}

func TestTheDefaultsAreCopies(t *testing.T) {
	DefaultSecretFieldNames()[0] = "changed"
	EligibleStatuses()[0] = StatusRevoked
	if DefaultSecretFieldNames()[0] != "password" || EligibleStatuses()[0] != StatusValidated {
		t.Fatal("a default was changed through a returned value")
	}
	if policy := SanitizationAllowing().ToolArguments; !policy.IsSecret("PASSWORD") {
		t.Fatal("the default secret names")
	}
}

func TestStartRunDefaultsAZeroSanitizationAndRefusesOneThatDropsResults(t *testing.T) {
	capture := testCapture(t, CaptureLimits{}, Sanitization{})
	if outcome, err := capture.AppendAttempt(testAttempt(0)); err != nil || outcome != OutcomeRecorded {
		t.Fatal(outcome, err)
	}
	if result := capture.Run().Attempts[0].Result; result == nil || *result != "ok" {
		t.Fatal("a zero Sanitization dropped the result")
	}
	run := CapturedRun{RunID: UUID{15: 1}, TaskID: "task", StartedAt: t0}
	for _, sanitization := range []Sanitization{
		{ToolArguments: NewSanitizationPolicy(), ToolResult: NewSanitizationPolicy()},
		{ToolArguments: NewSanitizationPolicy(), ToolResult: SanitizationPolicy{
			AllowedFieldNames: map[string]bool{ValueField: true}, SecretFieldNames: []string{"VALUE"}}},
		{ToolArguments: SanitizationPolicy{Limits: SanitizationLimits{MaxDepth: 4}}, ToolResult: NewSanitizationPolicy(ValueField)},
	} {
		if _, err := StartRun(run, sanitization, CaptureLimits{}); err == nil {
			t.Errorf("%+v was accepted", sanitization)
		}
	}
	if _, err := StartRun(run, DefaultSanitization(), CaptureLimits{MaxAttemptsPerRun: 2}); err == nil {
		t.Error("partial capture limits were accepted")
	}
	if err := (SanitizationLimits{MaxDepth: -1, MaxFieldCount: 1, MaxValueLength: 1, MaxFieldNameLength: 1}).Validate(); err == nil {
		t.Error("a negative sanitization limit")
	}
	policy := SanitizationPolicy{AllowedFieldNames: map[string]bool{"n": true}, Limits: SanitizationLimits{MaxDepth: 1}}
	if _, err := policy.Sanitize(NewObject(Member{"n", 1})); err == nil || errors.Is(err, ErrSanitizationRejected) {
		t.Errorf("invalid limits are a configuration error, not a refusal: %v", err)
	}
}

func TestCompleteValidatesAndCompletesOnce(t *testing.T) {
	capture := testCapture(t, CaptureLimits{}, DefaultSanitization())
	if _, err := capture.Complete("Done", t0); err == nil {
		t.Fatal("an unknown status")
	}
	before, _ := t0.Add(DurationFromTicks(-1))
	if _, err := capture.Complete(RunCompleted, before); err == nil {
		t.Fatal("an end before the start")
	}
	later, _ := t0.Add(second)
	if outcome, err := capture.Complete(RunFailed, later); err != nil || outcome != OutcomeRecorded {
		t.Fatal(outcome, err)
	}
	if outcome, err := capture.Complete(RunCompleted, t0); err != nil || outcome != OutcomeConflict {
		t.Fatal(outcome, err)
	}
	if run := capture.Run(); run.ExecutionStatus != RunFailed || *run.EndedAt != later {
		t.Fatal("a second Complete changed the run")
	}
}

func TestAppendAttemptRefusesAnEmptyIDAndKeepsItsOwnCopy(t *testing.T) {
	capture := testCapture(t, CaptureLimits{}, SanitizationAllowing("k"))
	if _, err := capture.AppendAttempt(RawAttempt{StartedAt: t0}); err == nil {
		t.Fatal("an empty attempt ID")
	}
	arguments := NewObject(Member{"k", "v"})
	attempt := testAttempt(0, testCall(0, arguments))
	if outcome, _ := capture.AppendAttempt(attempt); outcome != OutcomeRecorded {
		t.Fatal(outcome)
	}
	// The caller changes what it submitted; capture compared against its own copy, so the original content is
	// still a duplicate, and the changed content a conflict.
	*attempt.Result = "changed"
	arguments.Set("k", "changed")
	original := testAttempt(0, testCall(0, NewObject(Member{"k", "v"})))
	if outcome, _ := capture.AppendAttempt(original); outcome != OutcomeDuplicateNoOp {
		t.Fatalf("the original content: %s", outcome)
	}
	if outcome, _ := capture.AppendAttempt(attempt); outcome != OutcomeConflict {
		t.Fatalf("the changed content: %s", outcome)
	}
}

func finishedRun(t *testing.T) CapturedRun {
	t.Helper()
	capture := testCapture(t, CaptureLimits{}, DefaultSanitization())
	if _, err := capture.AppendAttempt(testAttempt(0)); err != nil {
		t.Fatal(err)
	}
	if _, err := capture.Complete(RunCompleted, t0); err != nil {
		t.Fatal(err)
	}
	return capture.Run()
}

func TestFinalizeRefusesAnUnnamedRunOrAnUnsetInstant(t *testing.T) {
	round := ClosedVerificationRound{RoundID: UUID{15: 7}, ArtifactRevision: "rev"}
	verification := Verification{ClosedRound: &round, RequiredChecks: []RequiredCheck{{"c", "k"}},
		CurrentArtifactRevision: "rev", FinalizedAt: t0}
	unset := verification
	unset.FinalizedAt = Instant{}
	if _, err := Finalize(finishedRun(t), unset); err == nil {
		t.Error("an unset finalization instant")
	}
	unnamed := finishedRun(t)
	unnamed.RunID = UUID{}
	if _, err := Finalize(unnamed, verification); err == nil {
		t.Error("an empty run ID")
	}
	blank := finishedRun(t)
	blank.TaskID = "\u0085"
	if _, err := Finalize(blank, verification); err == nil {
		t.Error("a blank task ID")
	}
}

func TestFinalizationTruncatesItsInstantToMicroseconds(t *testing.T) {
	at := MustParseInstant("2026-03-02T09:00:30.1234567+00:00")
	round := ClosedVerificationRound{RoundID: UUID{15: 7}, ArtifactRevision: "rev"}
	finalized, err := Finalize(finishedRun(t), Verification{ClosedRound: &round,
		RequiredChecks: []RequiredCheck{{"c", "k"}}, CurrentArtifactRevision: "rev", FinalizedAt: at,
		Evidence: []Evidence{{EvidenceID: UUID{15: 8}, VerificationRoundID: round.RoundID, ArtifactRevision: "rev",
			CheckID: "c", Kind: "k", Result: CheckPass, Producer: "p", CapturedAt: t0}}})
	if err != nil || finalized.Record.Reflection == nil {
		t.Fatal(err)
	}
	const want = "2026-03-02T09:00:30.123456+00:00"
	record := finalized.Record
	for name, instant := range map[string]Instant{"created_at": record.CreatedAt, "updated_at": record.UpdatedAt,
		"evaluatedAt": record.Outcome.EvaluatedAt, "reflection createdAt": record.Reflection.CreatedAt,
		"occurred_at": finalized.Event.OccurredAt} {
		if instant.String() != want {
			t.Errorf("%s is %s, want %s", name, instant, want)
		}
	}
}

func TestInstantsStayInDotNetsRange(t *testing.T) {
	for _, ticks := range []int64{-1, maxInstantTicks + 1} {
		if _, err := InstantFromTicks(ticks); err == nil {
			t.Errorf("%d ticks", ticks)
		}
	}
	if last, err := InstantFromTicks(maxInstantTicks); err != nil || last.String() != "9999-12-31T23:59:59.9999999+00:00" {
		t.Fatal(last, err)
	}
	for _, year := range []int{0, 10000} {
		if _, err := InstantFromTime(time.Date(year, 6, 1, 0, 0, 0, 0, time.UTC)); err == nil {
			t.Errorf("year %d", year)
		}
	}
	last, _ := InstantFromTicks(maxInstantTicks)
	if _, err := last.Add(DurationFromTicks(1)); err == nil {
		t.Error("past the last instant")
	}
	first, _ := InstantFromTicks(0)
	if _, err := first.Add(DurationFromTicks(-1)); err == nil {
		t.Error("before the first instant")
	}
	if _, err := last.Add(DurationFromTicks(math.MaxInt64)); err == nil {
		t.Error("an overflowing duration")
	}
}

func TestSearchParametersAcceptOnlyTheContractsShapes(t *testing.T) {
	invalid := func(change func(*SearchParameters)) SearchParameters {
		p := NewSearchParameters(scope, "refund")
		change(&p)
		return p
	}
	nan, high, negative := math.NaN(), 1.5, -0.1
	for name, p := range map[string]SearchParameters{
		"limit 201":          invalid(func(p *SearchParameters) { p.Limit = 201 }),
		"negative limit":     invalid(func(p *SearchParameters) { p.Limit = -1 }),
		"terms beyond int32": invalid(func(p *SearchParameters) { p.MinimumMatchedTerms = math.MaxInt32 + 1 }),
		"negative terms":     invalid(func(p *SearchParameters) { p.MinimumMatchedTerms = -1 }),
		"NaN confidence":     invalid(func(p *SearchParameters) { p.MinConfidence = &nan }),
		"confidence 1.5":     invalid(func(p *SearchParameters) { p.MinConfidence = &high }),
		"negative":           invalid(func(p *SearchParameters) { p.MinConfidence = &negative }),
		"no statuses":        invalid(func(p *SearchParameters) { p.Statuses = []ExperienceStatus{} }),
		"unknown status":     invalid(func(p *SearchParameters) { p.Statuses = []ExperienceStatus{"Bogus"} }),
	} {
		if _, err := p.Values(); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	if values, err := invalid(func(p *SearchParameters) { p.Limit = StatementLimit; p.MinimumMatchedTerms = math.MaxInt32 }).Values(); err != nil ||
		values["minimum_matched_terms"] != int32(math.MaxInt32) {
		t.Fatal(values, err)
	}
}

func TestEveryRefusalNamesItsPathNeverTheValue(t *testing.T) {
	policy := SanitizationAllowing("a", "n").ToolArguments
	deep := NewObject(Member{"a", NewObject(Member{"a", NewObject(Member{"a", NewObject(Member{"a",
		NewObject(Member{"a", "secret-ish content"})})})})})
	wide := &Object{}
	for i := 0; i < 51; i++ {
		wide.Set(strings.Repeat("k", i+1), 1)
	}
	long := strings.Repeat("x", 1_000_001)
	list := make([]any, 51)
	for name, c := range map[string]struct {
		fields *Object
		path   string
	}{
		"depth":       {deep, "'a.a.a.a'"},
		"width":       {wide, "'$'"},
		"array width": {NewObject(Member{"n", list}), "'n'"},
		"name length": {NewObject(Member{"ok", 1}, Member{strings.Repeat("n", 101), 1}), "field 1 of '$'"},
		"name dot":    {NewObject(Member{"a", NewObject(Member{"x.y", 1})}), "field 0 of 'a'"},
		"length":      {NewObject(Member{"n", long}), "'n'"},
		"not finite":  {NewObject(Member{"n", []any{json.Number("1e400")}}), "'n[0]'"},
	} {
		_, err := policy.Sanitize(c.fields)
		if !errors.Is(err, ErrSanitizationRejected) || !strings.Contains(err.Error(), c.path) ||
			strings.Contains(err.Error(), "content") || strings.Contains(err.Error(), "xxx") {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func TestJSONRefusesDuplicatesCyclesAndWritesANilObjectAsNull(t *testing.T) {
	if _, err := DecodeJSON([]byte(`{"a":1,"b":{"c":1,"c":2}}`)); err == nil {
		t.Fatal("a duplicate member was read")
	}
	if text, err := MarshalJSONValue(NewObject(Member{"o", (*Object)(nil)})); err != nil || string(text) != `{"o":null}` {
		t.Fatalf("%s %v", text, err)
	}
	cyclic := map[string]any{}
	cyclic["self"] = cyclic
	if _, err := MarshalJSONValue(cyclic); err == nil {
		t.Fatal("a cycle was written")
	}
	var pointer any
	pointer = &pointer
	if _, err := MarshalJSONValue(pointer); err == nil {
		t.Fatal("a pointer to itself was written")
	}
	nested := any("leaf")
	for i := 0; i < MaxJSONDepth+1; i++ {
		nested = []any{nested}
	}
	if _, err := MarshalJSONValue(nested); err == nil {
		t.Fatal("too deep was written")
	}
}

func TestTaskTextDropsInvalidUTF8AsDotNetDropsALoneSurrogate(t *testing.T) {
	text := func(s string) []MessagePart { return []MessagePart{{Type: "text", Text: &s}} }
	for in, want := range map[string]string{
		"Refund\xed\xa0\x80 ticket 42 is stuck in pending and has been for a long while now": "Refund ticket 42 is stuck in pending and has been for a long while now",
		"a�b is a real replacement character, kept as text, in this long enough task":        "a�b is a real replacement character, kept as text, in this long enough task",
		"\xff\xfe": "",
	} {
		got, ok, err := DeriveTaskText([]Message{{Role: "user", Contents: text(in)}}, 512, 64)
		if err != nil || got != want || ok != (want != "") {
			t.Errorf("%q: %q %v %v", in, got, ok, err)
		}
	}
}

func TestErrorClassReusesItsMemory(t *testing.T) {
	if raceEnabled {
		t.Skip("the race detector makes sync.Pool drop entries at random")
	}
	long := strings.Repeat("exit ", 1638) + "ENOENT"
	ErrorClassOf(long)
	allocations := testing.AllocsPerRun(20, func() { ErrorClassOf(long) })
	if allocations > 8 {
		t.Fatalf("%v allocations per classification of 8,192 characters", allocations)
	}
}

func BenchmarkErrorClass8192(b *testing.B) {
	long := strings.Repeat("exit ", 1638) + "ENOENT"
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		ErrorClassOf(long)
	}
}
