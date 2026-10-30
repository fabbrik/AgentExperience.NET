package agentexperience

// Contract 1.15: every confidence-engine fixture through this port's confidence engine (internal, because the token
// codec is unexported, as .NET's is), and the rules the fixtures do not state.

import (
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"
	"unicode/utf16"
)

const confidenceFamily = "confidence-engine"

var confidenceFixtures = filepath.Join("..", "contract", "fixtures", confidenceFamily)

func cfReadFixture(t *testing.T, path string) *Object {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	value, err := DecodeJSON(data)
	if err != nil {
		t.Fatal(err)
	}
	return value.(*Object)
}

func cfGet(o *Object, key string) any {
	value, ok := o.Get(key)
	if !ok {
		panic("no member " + key)
	}
	return value
}

func cfObj(o *Object, key string) *Object {
	value, _ := cfGet(o, key).(*Object)
	return value
}

func cfText(o *Object, key string) string { return cfGet(o, key).(string) }

func cfOptionalText(o *Object, key string) *string {
	if value, ok := cfGet(o, key).(string); ok {
		return &value
	}
	return nil
}

func cfNumber(o *Object, key string) float64 {
	value, err := cfGet(o, key).(json.Number).Float64()
	if err != nil {
		panic(err)
	}
	return value
}

func cfInteger(o *Object, key string) int64 {
	value, err := cfGet(o, key).(json.Number).Int64()
	if err != nil {
		panic(err)
	}
	return value
}

func cfUuidOf(o *Object, key string) UUID { return MustParseUUID(cfText(o, key)) }

func cfOptionalUUID(o *Object, key string) *UUID {
	if value := cfOptionalText(o, key); value != nil {
		id := MustParseUUID(*value)
		return &id
	}
	return nil
}

var cfErrHostEngine = errors.New("the host engine failed")

// cfHostEngine is a host engine as a case declares it (the .NET generator's HostEngine).
type cfHostEngine struct {
	ruleID, ruleVersion string
	score               any
}

func (e cfHostEngine) RuleID() string      { return e.ruleID }
func (e cfHostEngine) RuleVersion() string { return e.ruleVersion }
func (e cfHostEngine) Score(input ExperienceConfidenceInput) (float64, error) {
	if value, ok := e.score.(json.Number); ok {
		return value.Float64()
	}
	switch e.score {
	case "NaN":
		return math.NaN(), nil
	case "Infinity":
		return math.Inf(1), nil
	case "-Infinity":
		return math.Inf(-1), nil
	case "bayes":
		s, f := float64(input.SupportingValidations), float64(input.Contradictions)
		return (2 + s) / (4 + s + f), nil
	case "stored":
		return input.Record.ReuseConfidence, nil
	case "throws":
		return 0, cfErrHostEngine
	}
	panic(fmt.Sprintf("unknown host engine score %v", e.score))
}

func cfEngineOf(value any) ConfidenceEngine {
	switch engine := value.(type) {
	case nil:
		return nil
	case string:
		if engine == "default" {
			return ReuseConfidenceHeuristicEngine{}
		}
	case *Object:
		var id, version string
		if value := cfOptionalText(engine, "ruleId"); value != nil {
			id = *value
		}
		if value := cfOptionalText(engine, "ruleVersion"); value != nil {
			version = *value
		}
		return cfHostEngine{id, version, cfGet(engine, "score")}
	}
	panic(fmt.Sprintf("unknown engine %v", value))
}

// cfRefusal is an error as the fixtures write it.
type cfRefusal struct {
	exception string
	parameter *string
	message   *string
}

func cfRefusalOf(err error) cfRefusal {
	var argument *ConfidenceArgumentError
	var engine *ConfidenceEngineError
	switch {
	case errors.As(err, &argument):
		return cfRefusal{argument.Exception, &argument.Parameter, &argument.Message}
	case errors.As(err, &engine):
		message := engine.Error()
		return cfRefusal{"InvalidOperationException", nil, &message}
	case errors.Is(err, cfErrHostEngine):
		return cfRefusal{"HostEngineException", nil, nil}
	}
	return cfRefusal{exception: err.Error()}
}

func cfCheckRefusal(t *testing.T, expected any, err error) {
	t.Helper()
	want, _ := expected.(*Object)
	if want == nil {
		if err != nil {
			t.Fatalf("refused: %v", err)
		}
		return
	}
	if err == nil {
		t.Fatalf("not refused; .NET refuses with %v", want)
	}
	got := cfRefusalOf(err)
	if got.exception != cfText(want, "exception") {
		t.Fatalf("exception %s, .NET's %s (%v)", got.exception, cfText(want, "exception"), err)
	}
	if parameter := cfOptionalText(want, "parameter"); (parameter == nil) != (got.parameter == nil) ||
		parameter != nil && *parameter != *got.parameter {
		t.Fatalf("parameter %v, .NET's %v", got.parameter, parameter)
	}
	if message := cfOptionalText(want, "message"); message != nil && (got.message == nil || *got.message != *message) {
		t.Fatalf("message %v, .NET's %q", got.message, *message)
	}
}

func cfClose12(t *testing.T, name string, expected, actual float64) {
	t.Helper()
	if math.Abs(expected-actual) > 1e-12 {
		t.Fatalf("%s %v, .NET's %v", name, actual, expected)
	}
}

func cfOptionalOptions(o *Object) IndependenceOptions {
	now, err := instantOfUnixMilliseconds(cfInteger(o, "nowUnixMs"))
	if err != nil {
		panic(err)
	}
	options := IndependenceOptions{
		Verification:            IndependenceVerification(cfText(o, "verification")),
		AssessmentTokenLifetime: time.Duration(cfInteger(o, "assessmentTokenLifetimeTicks")) * 100,
		Clock:                   func() Instant { return now },
	}
	if key := cfOptionalText(o, "assessmentTokenKey"); key != nil {
		bytes, err := hex.DecodeString(*key)
		if err != nil {
			panic(err)
		}
		options.AssessmentTokenKey = bytes
	}
	return options
}

func cfScopeOf(o *Object) Scope {
	return Scope{cfText(o, "tenantId"), cfText(o, "applicationId"), cfText(o, "projectId"),
		cfOptionalText(o, "teamId"), cfOptionalText(o, "agentId"), cfOptionalText(o, "userId")}
}

// zeroLifetimeIsTheDefault is the cases a zero lifetime refuses in .NET and means the default in Go.
func cfZeroLifetime(input *Object) bool {
	return cfInteger(cfObj(input, "options"), "assessmentTokenLifetimeTicks") == 0
}

func cfRunConfidenceCase(t *testing.T, input, expected *Object) {
	switch cfText(input, "operation") {
	case "score":
		score, err := ConfidenceScore(cfInteger(input, "supportingValidations"), cfInteger(input, "contradictions"))
		cfCheckRefusal(t, cfGet(expected, "refusal"), err)
		if err == nil {
			cfClose12(t, "score", cfNumber(expected, "score"), score)
		}
	case "rule":
		rule, err := NewConfidenceEngineRule(cfEngineOf(cfGet(input, "engine")), ConfidenceEngineParameter)
		cfCheckRefusal(t, cfGet(expected, "refusal"), err)
		if err == nil && rule.Recorded() != cfText(expected, "recorded") {
			t.Fatalf("recorded %q, .NET's %q", rule.Recorded(), cfText(expected, "recorded"))
		}
	case "apply":
		cfRunApplyCase(t, input, expected)
	case "options":
		options := cfOptionalOptions(cfObj(input, "options"))
		_, err := NewAssessmentTokenIssuer(options)
		want := cfGet(expected, "refusal")
		if cfZeroLifetime(input) {
			// Go reads a zero lifetime as the default (DefaultAssessmentTokenLifetime) where .NET refuses it, so
			// with a valid lifetime in its place the next check decides: the mode's refusal stands, and in place of
			// the lifetime's comes the key's (none, or too short), or acceptance.
			if message := cfOptionalText(cfObj(expected, "refusal"), "message"); message != nil && strings.Contains(*message, "Lifetime") {
				want = cfKeyRefusal(options.AssessmentTokenKey)
			}
		}
		cfCheckRefusal(t, want, err)
	case "issue":
		cfRunIssueCase(t, input, expected)
	case "verify":
		codec, err := newAssessmentTokenCodec(cfOptionalOptions(cfObj(input, "options")))
		if err != nil || codec == nil {
			t.Fatalf("codec: %v", err)
		}
		scope, reviewer := cfScopeOf(cfObj(input, "scope")), cfText(input, "reviewer")
		if utf16, ok := input.Get("utf16"); ok { // lone surrogates, which a Go string holds as U+FFFD
			reviewer = cfFromUnits(cfGet(utf16.(*Object), "reviewer"))
			scope.TeamID = nil
			if team := cfGet(utf16.(*Object), "teamId"); team != nil {
				scope.TeamID = Ptr(cfFromUnits(team))
			}
		}
		result := codec.verify(cfStringOrEmpty(cfGet(input, "token")), scope, cfUuidOf(input, "runId"), reviewer,
			ConfidenceEvidenceKind(cfText(input, "kind")))
		want := ""
		if value := cfOptionalText(expected, "refusal"); value != nil {
			want = *value
		}
		if string(result.refusal) != want {
			t.Fatalf("refusal %q, .NET's %q", result.refusal, want)
		}
		if result.assessmentID.String() != cfText(expected, "assessmentId") {
			t.Fatalf("assessment %s, .NET's %s", result.assessmentID, cfText(expected, "assessmentId"))
		}
		covered := cfGet(expected, "covered").([]any)
		if len(covered) != len(result.covered) {
			t.Fatalf("covered %v, .NET's %v", result.covered, covered)
		}
		for i, id := range covered {
			if result.covered[i].String() != id.(string) {
				t.Fatalf("covered %v, .NET's %v", result.covered, covered)
			}
		}
	default:
		t.Fatalf("unknown operation %q", cfText(input, "operation"))
	}
}

func cfFromUnits(units any) string {
	var codes []uint16
	for _, unit := range units.([]any) {
		value, _ := unit.(json.Number).Int64()
		codes = append(codes, uint16(value))
	}
	return string(utf16.Decode(codes))
}

func cfStringOrEmpty(value any) string {
	if s, ok := value.(string); ok {
		return s
	}
	return ""
}

func cfRunApplyCase(t *testing.T, input, expected *Object) {
	record, err := FromStored(cfGet(input, "record"))
	if err != nil {
		t.Fatal(err)
	}
	evidence := cfObj(input, "evidence")
	kind := ConfidenceEvidenceKind(cfText(evidence, "kind"))
	if AcceptsEvidenceIn(record.Status) != cfGet(expected, "acceptsEvidence").(bool) {
		t.Fatalf("accepts evidence: .NET says %v", cfGet(expected, "acceptsEvidence"))
	}
	if string(StatusAfter(record.Status, kind)) != cfText(expected, "statusAfter") {
		t.Fatalf("status after %s, .NET's %s", StatusAfter(record.Status, kind), cfText(expected, "statusAfter"))
	}
	rule, err := NewConfidenceEngineRule(cfEngineOf(cfGet(input, "engine")), ConfidenceEngineParameter)
	if err != nil {
		t.Fatal(err)
	}
	update, err := rule.Apply(record, ConfidenceEvidence{
		EvidenceID:          cfUuidOf(evidence, "evidenceId"),
		Kind:                kind,
		Source:              ConfidenceEvidenceSource(cfText(evidence, "source")),
		RunID:               cfUuidOf(evidence, "runId"),
		VerificationRoundID: cfOptionalUUID(evidence, "verificationRoundId"),
		ReviewerIdentity:    cfOptionalText(evidence, "reviewerIdentity"),
		Detail:              cfOptionalText(evidence, "detail"),
	})
	cfCheckRefusal(t, cfGet(expected, "refusal"), err)
	if err != nil {
		return
	}
	want := cfObj(expected, "update")
	optionalString := func(id *UUID) any {
		if id == nil {
			return nil
		}
		return id.String()
	}
	pointer := func(s *string) any {
		if s == nil {
			return nil
		}
		return *s
	}
	actual := map[string]any{
		"evidenceId": update.EvidenceID.String(), "kind": string(update.Kind), "source": string(update.Source),
		"runId": update.RunID.String(), "verificationRoundId": optionalString(update.VerificationRoundID),
		"reviewerIdentity": pointer(update.ReviewerIdentity), "ruleVersion": update.RuleVersion,
		"priorSupportingValidations": update.PriorSupportingValidations,
		"newSupportingValidations":   update.NewSupportingValidations,
		"priorContradictions":        update.PriorContradictions, "newContradictions": update.NewContradictions,
		"detail": pointer(update.Detail), "counted": update.Counted(),
	}
	for key, value := range actual {
		expectedValue := cfGet(want, key)
		if n, ok := expectedValue.(json.Number); ok {
			expectedValue, _ = n.Int64()
		}
		if expectedValue != value {
			t.Fatalf("%s %v, .NET's %v", key, value, expectedValue)
		}
	}
	cfClose12(t, "priorReuseConfidence", cfNumber(want, "priorReuseConfidence"), update.PriorReuseConfidence)
	cfClose12(t, "newReuseConfidence", cfNumber(want, "newReuseConfidence"), update.NewReuseConfidence)
	only, recordedOnly := cfObj(expected, "recordedOnly"), update.AsRecordedOnly()
	cfClose12(t, "recorded only", cfNumber(only, "newReuseConfidence"), recordedOnly.NewReuseConfidence)
	if recordedOnly.NewSupportingValidations != cfInteger(only, "newSupportingValidations") ||
		recordedOnly.NewContradictions != cfInteger(only, "newContradictions") ||
		recordedOnly.Counted() != cfGet(only, "counted").(bool) {
		t.Fatalf("recorded only %+v, .NET's %v", recordedOnly, only)
	}
	key := cfObj(expected, "independenceKey")
	actualKey, err := IndependenceKeyFor(update)
	cfCheckRefusal(t, cfGet(key, "refusal"), err)
	if err == nil && (string(actualKey.Source) != cfText(key, "source") || actualKey.Value != cfText(key, "value")) {
		t.Fatalf("key %+v, .NET's %v", actualKey, key)
	}
}

// cfKeyRefusal is .NET's refusal of options whose only other fault is the key, as the fixtures write one.
func cfKeyRefusal(key []byte) any {
	refusal := func(message string) any {
		o := NewObject()
		o.Set("exception", "ArgumentException")
		o.Set("parameter", "options")
		o.Set("message", message)
		return o
	}
	switch {
	case key == nil:
		return refusal("An assessment token issuer needs ExperienceIndependenceOptions.AssessmentTokenKey.")
	case len(key) < MinimumAssessmentTokenKeyBytes:
		return refusal("ExperienceIndependenceOptions.AssessmentTokenKey must be at least 32 bytes.")
	}
	return nil
}

func cfRunIssueCase(t *testing.T, input, expected *Object) {
	scope := cfScopeOf(cfObj(input, "scope"))
	reviewer := cfObj(input, "reviewer")
	bounds := AuthorizationBounds{cfText(reviewer, "tenantId"), cfOptionalText(reviewer, "applicationId"),
		cfOptionalText(reviewer, "projectId"), cfOptionalText(reviewer, "teamId"), cfOptionalText(reviewer, "agentId"),
		cfOptionalText(reviewer, "userId")}
	identity := Identity{Principal: cfText(reviewer, "principalId"), Scope: scope, Bounds: &bounds}
	var ids []UUID
	for _, id := range cfGet(input, "experienceIds").([]any) {
		ids = append(ids, MustParseUUID(id.(string)))
	}
	run, kind := cfUuidOf(input, "runId"), ConfidenceEvidenceKind(cfText(input, "kind"))
	issuer, err := NewAssessmentTokenIssuer(cfOptionalOptions(cfObj(input, "options")))
	if err == nil {
		var random IssuedAssessmentToken
		random, err = issuer.Issue(identity, scope, run, kind, ids)
		if err == nil {
			// The issuer draws a fresh assessment ID; the fixture pins the same codec's encoding under the case's.
			issued, encodeErr := issuer.codec.encode(cfUuidOf(input, "assessmentId"), scope, run, identity.Principal, kind, ids)
			if encodeErr != nil {
				t.Fatal(encodeErr)
			}
			if issued.Token != cfText(expected, "token") || issued.AssessmentID.String() != cfText(expected, "assessmentId") ||
				unixMilliseconds(issued.IssuedAt) != cfInteger(expected, "issuedAtUnixMs") ||
				unixMilliseconds(issued.ExpiresAt) != cfInteger(expected, "expiresAtUnixMs") {
				t.Fatalf("issued %s (%s), .NET's %v", issued, issued.Token, expected)
			}
			if random.AssessmentID == issued.AssessmentID || random.IssuedAt != issued.IssuedAt ||
				random.ExpiresAt != issued.ExpiresAt {
				t.Fatalf("issued %s", random)
			}
			// What the issuer itself emitted: bound to the case's scope, run, reviewer and kind, covering its records
			// in order (a token issued before 1970 is invalid by construction).
			verified := issuer.codec.verify(random.Token, scope, run, identity.Principal, kind)
			if cfInteger(expected, "issuedAtUnixMs") < 0 {
				if verified.refusal != RefusalAssessmentTokenInvalid {
					t.Fatalf("a token issued before 1970: %q", verified.refusal)
				}
			} else if verified.refusal != "" || verified.assessmentID != random.AssessmentID ||
				fmt.Sprint(verified.covered) != fmt.Sprint(ids) {
				t.Fatalf("the issued token reads back as %+v", verified)
			}
		}
	}
	cfCheckRefusal(t, cfGet(expected, "refusal"), err)
}

func TestEveryConfidenceEngineFixture(t *testing.T) {
	entries, err := os.ReadDir(confidenceFixtures)
	if err != nil {
		t.Fatal(err)
	}
	var names []string
	for _, entry := range entries {
		if entry.IsDir() {
			names = append(names, entry.Name())
		}
	}
	sort.Strings(names)
	if len(names) < 100 {
		t.Fatalf("only %d confidence-engine cases", len(names))
	}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			input := cfReadFixture(t, filepath.Join(confidenceFixtures, name, "input.json"))
			expected := cfReadFixture(t, filepath.Join(confidenceFixtures, name, "expected.json"))
			cfRunConfidenceCase(t, input, expected)
		})
	}
}

// ---- What the fixtures do not state ---------------------------------------------------------------------------

var (
	cfTestKey   = []byte("0123456789abcdef0123456789abcdef")
	cfTestScope = Scope{TenantID: "tenant", ApplicationID: "app", ProjectID: "project"}
	cfTestRun   = MustParseUUID("000000f6-0000-4000-8000-000000000100")
	cfTestNow   = MustParseInstant("2026-03-02T09:00:00Z")
)

func TestTheDefaultEngineIsTheHeuristicRecordingThePlainRuleVersion(t *testing.T) {
	for _, engine := range []ConfidenceEngine{nil, ReuseConfidenceHeuristicEngine{}, &ReuseConfidenceHeuristicEngine{}} {
		rule, err := NewConfidenceEngineRule(engine, "engine")
		if err != nil || rule != DefaultConfidenceEngineRule() || rule.Recorded() != "1.0.0" {
			t.Fatalf("%v %v", rule, err)
		}
	}
	if !AcceptsEvidenceIn(StatusContested) || AcceptsEvidenceIn(StatusCandidate) {
		t.Fatal("accepts evidence")
	}
}

func TestARefusalNamesTheParameterTheCallerGives(t *testing.T) {
	_, err := NewConfidenceEngineRule(cfHostEngine{"", "1", "bayes"}, "engine")
	var argument *ConfidenceArgumentError
	if !errors.As(err, &argument) || argument.Parameter != "engine" {
		t.Fatal(err)
	}
}

func TestALoneSurrogateReviewerSignsAsTheReplacementCharacter(t *testing.T) {
	codec, err := newAssessmentTokenCodec(IndependenceOptions{AssessmentTokenKey: cfTestKey, Clock: func() Instant { return cfTestNow }})
	if err != nil {
		t.Fatal(err)
	}
	token, err := codec.encode(NewRandomUUID(), cfTestScope, cfTestRun, "rev\xed\xa0\x80iewer", EvidenceSupporting, []UUID{NewRandomUUID()})
	if err != nil {
		t.Fatal(err)
	}
	if result := codec.verify(token.Token, cfTestScope, cfTestRun, "rev�iewer", EvidenceSupporting); result.refusal != "" {
		t.Fatal(result.refusal)
	}
}

func TestAnIssuedTokenVerifiesAndIsNeverPrinted(t *testing.T) {
	key := append([]byte(nil), cfTestKey...)
	options := IndependenceOptions{AssessmentTokenKey: key, Clock: func() Instant { return cfTestNow }}
	issuer, err := NewAssessmentTokenIssuer(options)
	if err != nil {
		t.Fatal(err)
	}
	key[0] ^= 1 // the issuer keeps its own copy
	record := NewRandomUUID()
	issued, err := issuer.Issue(Identity{Principal: "reviewer-1", Scope: cfTestScope}, cfTestScope, cfTestRun, EvidenceContradicting, []UUID{record})
	if err != nil {
		t.Fatal(err)
	}
	for _, printed := range []string{fmt.Sprint(issued), fmt.Sprintf("%+v", issued), fmt.Sprintf("%#v", issued)} {
		if strings.Contains(printed, issued.Token) {
			t.Fatalf("printed the token: %s", printed)
		}
	}
	if strings.Contains(fmt.Sprintf("%#v", options), "0123") || strings.Contains(fmt.Sprint(options), "[") {
		t.Fatalf("printed the key: %v", options)
	}
	codec, _ := newAssessmentTokenCodec(IndependenceOptions{AssessmentTokenKey: cfTestKey, Clock: func() Instant { return cfTestNow }})
	result := codec.verify(issued.Token, cfTestScope, cfTestRun, "reviewer-1", EvidenceContradicting)
	if result.refusal != "" || result.assessmentID != issued.AssessmentID || len(result.covered) != 1 || result.covered[0] != record {
		t.Fatalf("%+v", result)
	}
	if issued.ExpiresAt.Sub(issued.IssuedAt).Ticks() != int64(DefaultAssessmentTokenLifetime/100) {
		t.Fatalf("lifetime %v", issued.ExpiresAt.Sub(issued.IssuedAt))
	}
}

func TestAZeroLifetimeIsTheDefault(t *testing.T) {
	codec, err := newAssessmentTokenCodec(IndependenceOptions{AssessmentTokenKey: cfTestKey})
	if err != nil || codec.lifetime != DefaultAssessmentTokenLifetime {
		t.Fatalf("%v %v", codec, err)
	}
}

func TestARefusedScoreIsWrittenAsDotNetWritesIt(t *testing.T) {
	for value, want := range map[float64]string{
		1e16: "10000000000000000", 1e17: "1E+17", 1.5e-5: "1.5E-05", 0.0001: "0.0001", 2: "2",
		1.2345678901234568e17: "1.2345678901234568E+17", 5e-324: "5E-324",
	} {
		if got := (&ConfidenceEngineError{"r/1", value}).Error(); !strings.Contains(got, " returned "+want+", ") {
			t.Fatalf("%v: %s", value, got)
		}
	}
}

func TestAHostKeyIsTheReviewerVerbatim(t *testing.T) {
	key, err := HumanIndependenceKey("Ω", cfTestRun)
	if err != nil || key.Value != "human:Ω:"+cfTestRun.String() {
		t.Fatalf("%v %v", key, err)
	}
	if _, err := HumanIndependenceKey("r ", cfTestRun); err == nil {
		t.Fatal("a reviewer ending in a no-break space was accepted")
	}
}

func TestEachInvalidUnitIsItsOwnReplacementCharacter(t *testing.T) {
	cases := map[string]string{
		"a\xffb":                     "a\uFFFDb",
		"a\xff\xffb":                 "a\uFFFD\uFFFDb",
		"a\xed\xa0\x80b":             "a\uFFFDb",       // one lone surrogate, one unit
		"a\xed\xa0\x80\xed\xa0\x80b": "a\uFFFD\uFFFDb", // two adjacent lone surrogates
		"a\uFFFDb":                   "a\uFFFDb",
	}
	for input, want := range cases {
		if got := string(utf8Bytes(input)); got != want {
			t.Fatalf("%q: %q, want %q", input, got, want)
		}
	}
	codec, _ := newAssessmentTokenCodec(IndependenceOptions{AssessmentTokenKey: cfTestKey, Clock: func() Instant { return cfTestNow }})
	one, two := codec.mac(nil, cfTestScope, cfTestRun, "a\xffb"), codec.mac(nil, cfTestScope, cfTestRun, "a\xff\xffb")
	if string(one) == string(two) {
		t.Fatal("one and two invalid bytes sign alike")
	}
}

func TestAZeroRuleRefusesRatherThanPanics(t *testing.T) {
	var zero ConfidenceEngineRule
	if _, err := zero.Score(ExperienceRecord{}, 1, 0); !errors.Is(err, errNoEngine) {
		t.Fatal(err)
	}
	var missing *ConfidenceEngineRule
	if _, err := missing.Apply(ExperienceRecord{}, ConfidenceEvidence{Kind: EvidenceSupporting}); !errors.Is(err, errNoEngine) {
		t.Fatal(err)
	}
}

func TestAnUnrepresentableExpiryIsRefusedNotAPanic(t *testing.T) {
	last, _ := instantOfUnixMilliseconds(maxUnixMilliseconds - 1000)
	issuer, err := NewAssessmentTokenIssuer(IndependenceOptions{AssessmentTokenKey: cfTestKey, Clock: func() Instant { return last }})
	if err != nil {
		t.Fatal(err)
	}
	_, err = issuer.Issue(Identity{Principal: "r", Scope: cfTestScope}, cfTestScope, cfTestRun, EvidenceSupporting, []UUID{NewRandomUUID()})
	var argument *ConfidenceArgumentError
	if !errors.As(err, &argument) || argument.Exception != "ArgumentOutOfRangeException" || argument.Parameter != "milliseconds" {
		t.Fatal(err)
	}
}

func TestACountPastDotNetsCountersIsRefused(t *testing.T) {
	if _, err := ConfidenceScore(MaxEvidenceCounter+1, 0); err == nil {
		t.Fatal("accepted")
	}
	if _, err := ApplyConfidenceEvidence(ExperienceRecord{Contradictions: MaxEvidenceCounter + 1}, ConfidenceEvidence{Kind: EvidenceSupporting}); err == nil {
		t.Fatal("accepted")
	}
}

func TestTheIssuerChecksAndEncodesItsOwnCopy(t *testing.T) {
	issuer, _ := NewAssessmentTokenIssuer(IndependenceOptions{AssessmentTokenKey: cfTestKey, Clock: func() Instant { return cfTestNow }})
	ids := []UUID{NewRandomUUID()}
	issued, err := issuer.Issue(Identity{Principal: "r", Scope: cfTestScope}, cfTestScope, cfTestRun, EvidenceSupporting, ids)
	if err != nil {
		t.Fatal(err)
	}
	want := ids[0]
	ids[0] = UUID{}
	if result := issuer.codec.verify(issued.Token, cfTestScope, cfTestRun, "r", EvidenceSupporting); result.covered[0] != want {
		t.Fatal(result.covered)
	}
}

func TestTheKeyIsNeverMarshalled(t *testing.T) {
	data, err := json.Marshal(IndependenceOptions{AssessmentTokenKey: cfTestKey})
	if err != nil || strings.Contains(string(data), "Key") {
		t.Fatalf("%s %v", data, err)
	}
}
