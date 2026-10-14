// Package contracttest is shared by the conformance tests and the PostgreSQL tests: where the contract is, how a
// fixture runs, and how two JSON values are compared.
package contracttest

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"math/big"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// Repository is the repository's root directory.
var Repository = func() string {
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		panic("contracttest: cannot locate the repository")
	}
	return filepath.Clean(filepath.Join(filepath.Dir(file), "..", "..", ".."))
}()

// Paths into the repository.
var (
	Contract   = filepath.Join(Repository, "contract")
	Fixtures   = filepath.Join(Contract, "fixtures")
	Schema     = filepath.Join(Contract, "schema", "experience-record.v1.json")
	Migrations = filepath.Join(Repository, "src", "AgentExperience.Storage.Postgres", "Migrations")
)

// ReadJSON reads a JSON file, keeping every number exactly as written and every object's member order.
func ReadJSON(path string) (any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return ae.DecodeJSON(data)
}

// MustReadJSON is ReadJSON, panicking on failure: for fixtures, which are known to exist.
func MustReadJSON(path string) any {
	value, err := ReadJSON(path)
	if err != nil {
		panic(fmt.Sprintf("contracttest: %s: %v", path, err))
	}
	return value
}

// Cases are every case directory of a fixture family, by name.
func Cases(family string) []string {
	entries, err := os.ReadDir(filepath.Join(Fixtures, family))
	if err != nil {
		panic(err)
	}
	var names []string
	for _, entry := range entries {
		if entry.IsDir() {
			names = append(names, entry.Name())
		}
	}
	sort.Strings(names)
	return names
}

// ErrUnsupportedContractVersion is a lock naming a contract version this package does not support.
var ErrUnsupportedContractVersion = errors.New("unsupported contract version")

// CheckContractVersion is the lock's contract version, when this package supports it; a clear failure otherwise.
func CheckContractVersion(lock any) (string, error) {
	object, _ := lock.(*ae.Object)
	value, _ := object.Get("contractVersion")
	version, _ := value.(string)
	for _, supported := range ae.SupportedContractVersions {
		if version == supported {
			return version, nil
		}
	}
	return "", fmt.Errorf("%w: contract/lock.json is contract version %v, but this package supports only %s. "+
		"Implement the new version before running its fixtures", ErrUnsupportedContractVersion, describe(value),
		strings.Join(ae.SupportedContractVersions, ", "))
}

func describe(value any) string {
	if value == nil {
		return "null"
	}
	if s, ok := value.(string); ok {
		return strconv.Quote(s)
	}
	return fmt.Sprintf("%v", value)
}

// Options change how Difference compares numbers.
type Options struct {
	// Tolerance is the largest difference two numbers may have; 0 compares them exactly.
	Tolerance float64
	// IgnoreKind compares numbers by value only. By default an integer literal (no fraction, no exponent) never
	// equals a number written with a fraction or an exponent, so a double is never written as an integer or the
	// other way round. PostgreSQL's jsonb rewrites numbers in its own form (1E2 as 100), so a value read back from
	// it is compared by value only.
	IgnoreKind bool
}

// Difference is the first difference between two JSON values (as ae.DecodeJSON reads them), compared by value; ""
// when they are equal. Member order is ignored.
func Difference(expected, actual any, path string, options Options) string {
	switch e := expected.(type) {
	case nil:
		if actual != nil {
			return fmt.Sprintf("%s: expected null, got %s", path, show(actual))
		}
		return ""
	case bool:
		if a, ok := actual.(bool); !ok || a != e {
			return fmt.Sprintf("%s: expected %v, got %s", path, e, show(actual))
		}
		return ""
	case string:
		if a, ok := actual.(string); !ok || a != e {
			return fmt.Sprintf("%s: expected %q, got %s", path, e, show(actual))
		}
		return ""
	case json.Number:
		a, ok := actual.(json.Number)
		if !ok {
			return fmt.Sprintf("%s: expected the number %s, got %s", path, e, show(actual))
		}
		if !options.IgnoreKind && isInteger(e) != isInteger(a) {
			return fmt.Sprintf("%s: expected %s (%s), got %s (%s)", path, e, kind(e), a, kind(a))
		}
		if numbersEqual(e, a, options.Tolerance) {
			return ""
		}
		return fmt.Sprintf("%s: expected %s, got %s", path, e, a)
	case *ae.Object:
		a, ok := actual.(*ae.Object)
		if !ok {
			return fmt.Sprintf("%s: expected an object, got %s", path, show(actual))
		}
		expectedKeys, actualKeys := sortedKeys(e), sortedKeys(a)
		if strings.Join(expectedKeys, "\x00") != strings.Join(actualKeys, "\x00") {
			return fmt.Sprintf("%s: expected members %v, got %v", path, expectedKeys, actualKeys)
		}
		for _, m := range e.Members() {
			value, _ := a.Get(m.Key)
			if difference := Difference(m.Value, value, path+"."+m.Key, options); difference != "" {
				return difference
			}
		}
		return ""
	case []any:
		a, ok := actual.([]any)
		if !ok {
			return fmt.Sprintf("%s: expected an array, got %s", path, show(actual))
		}
		if len(e) != len(a) {
			return fmt.Sprintf("%s: expected %d items, got %d", path, len(e), len(a))
		}
		for i := range e {
			if difference := Difference(e[i], a[i], fmt.Sprintf("%s[%d]", path, i), options); difference != "" {
				return difference
			}
		}
		return ""
	}
	return fmt.Sprintf("%s: unexpected expected value %T", path, expected)
}

func isInteger(n json.Number) bool { return !strings.ContainsAny(string(n), ".eE") }

func kind(n json.Number) string {
	if isInteger(n) {
		return "an integer"
	}
	return "a double"
}

func numbersEqual(expected, actual json.Number, tolerance float64) bool {
	e, eok := new(big.Rat).SetString(string(expected))
	a, aok := new(big.Rat).SetString(string(actual))
	if eok && aok && e.Cmp(a) == 0 {
		return true
	}
	if tolerance == 0 {
		return false
	}
	ef, err1 := strconv.ParseFloat(string(expected), 64)
	af, err2 := strconv.ParseFloat(string(actual), 64)
	return err1 == nil && err2 == nil && math.Abs(ef-af) <= tolerance
}

func sortedKeys(o *ae.Object) []string {
	keys := o.Keys()
	sort.Strings(keys)
	return keys
}

func show(value any) string {
	data, err := ae.MarshalJSONValue(value)
	if err != nil {
		return fmt.Sprintf("%v", value)
	}
	if len(data) > 200 {
		return string(data[:200]) + "..."
	}
	return string(data)
}

// AsWritten is a value as this package writes it, read back: what is compared is what the package would write.
func AsWritten(value any) any {
	data, err := ae.MarshalJSONValue(value)
	if err != nil {
		panic(fmt.Sprintf("contracttest: the value cannot be written as JSON: %v", err))
	}
	read, err := ae.DecodeJSON(data)
	if err != nil {
		panic(fmt.Sprintf("contracttest: the written JSON cannot be read back: %v", err))
	}
	return read
}

// ---- Reading fixture inputs --------------------------------------------------------------------------------------

func get(o *ae.Object, key string) any {
	value, _ := o.Get(key)
	return value
}

func object(value any) *ae.Object {
	o, _ := value.(*ae.Object)
	return o
}

func text(value any) string {
	s, _ := value.(string)
	return s
}

func optionalText(value any) *string {
	if s, ok := value.(string); ok {
		return &s
	}
	return nil
}

func list(value any) []any {
	items, _ := value.([]any)
	return items
}

func integer(value any) int64 {
	n, _ := value.(json.Number)
	i, err := strconv.ParseInt(string(n), 10, 64)
	if err != nil {
		panic(fmt.Sprintf("contracttest: %v is not an integer", value))
	}
	return i
}

func must[T any](value T, err error) T {
	if err != nil {
		panic(fmt.Sprintf("contracttest: %v", err))
	}
	return value
}

// ScopeOf reads a fixture's scope ({"tenantId", "applicationId", ...}).
func ScopeOf(value any) ae.Scope {
	o := object(value)
	return ae.Scope{
		TenantID:      text(get(o, "tenantId")),
		ApplicationID: text(get(o, "applicationId")),
		ProjectID:     text(get(o, "projectId")),
		TeamID:        optionalText(get(o, "teamId")),
		AgentID:       optionalText(get(o, "agentId")),
		UserID:        optionalText(get(o, "userId")),
	}
}

// EvidenceOf reads one piece of a fixture's evidence.
func EvidenceOf(value any) ae.Evidence {
	o := object(value)
	return ae.Evidence{
		EvidenceID:          must(ae.ParseUUID(text(get(o, "evidenceId")))),
		VerificationRoundID: must(ae.ParseUUID(text(get(o, "verificationRoundId")))),
		ArtifactRevision:    text(get(o, "artifactRevision")),
		CheckID:             text(get(o, "checkId")),
		Kind:                text(get(o, "kind")),
		Result:              ae.CheckResult(text(get(o, "result"))),
		Producer:            text(get(o, "producer")),
		Detail:              optionalText(get(o, "detail")),
		CapturedAt:          must(ae.ParseInstant(text(get(o, "capturedAt")))),
	}
}

func bySequence(items []any) []any {
	sorted := append([]any(nil), items...)
	sort.SliceStable(sorted, func(i, j int) bool {
		return integer(get(object(sorted[i]), "sequenceNumber")) < integer(get(object(sorted[j]), "sequenceNumber"))
	})
	return sorted
}

// RunFinalizeCase runs a finalize case's input.json through capture and finalization. It returns the result and
// what capture answered, in expected-capture.json's form.
func RunFinalizeCase(input any) (ae.FinalizedExperience, *ae.Object, error) {
	c := object(input)
	run := object(get(c, "run"))
	environment := object(get(run, "environment"))
	provenance := object(get(run, "provenance"))
	metadata := map[string]string{}
	for _, m := range object(get(environment, "metadata")).Members() {
		metadata[m.Key] = text(m.Value)
	}
	var allowed []string
	for _, name := range list(get(object(get(c, "options")), "allowedArgumentNames")) {
		allowed = append(allowed, text(name))
	}
	capture, err := ae.StartRun(ae.CapturedRun{
		RunID:           must(ae.ParseUUID(text(get(run, "runId")))),
		TaskID:          text(get(run, "taskId")),
		TaskDescription: optionalText(get(run, "taskDescription")),
		Scope:           ScopeOf(get(run, "scope")),
		Environment: ae.EnvironmentFingerprint{
			HostName:           text(get(environment, "hostName")),
			RuntimeVersion:     text(get(environment, "runtimeVersion")),
			OperatingSystem:    text(get(environment, "operatingSystem")),
			ApplicationVersion: optionalText(get(environment, "applicationVersion")),
			Metadata:           metadata,
		},
		Provenance: ae.Provenance{
			Source:        text(get(provenance, "source")),
			SourceVersion: optionalText(get(provenance, "sourceVersion")),
			RecordedAt:    must(ae.ParseInstant(text(get(provenance, "recordedAt")))),
			CorrelationID: optionalText(get(provenance, "correlationId")),
		},
		StartedAt: must(ae.ParseInstant(text(get(run, "startedAt")))),
	}, ae.SanitizationAllowing(allowed...), ae.DefaultCaptureLimits())
	if err != nil {
		return ae.FinalizedExperience{}, nil, err
	}

	answers := &ae.Object{}
	if exposures := list(get(provenance, "exposedTo")); len(exposures) > 0 {
		var recorded []ae.RunExposure
		for _, item := range exposures {
			exposure := object(item)
			recorded = append(recorded, ae.RunExposure{
				ExperienceID: must(ae.ParseUUID(text(get(exposure, "experienceId")))),
				Revision:     integer(get(exposure, "revision")),
			})
		}
		outcome, err := capture.RecordExposure(recorded...)
		if err != nil {
			return ae.FinalizedExperience{}, nil, err
		}
		answers.Set("exposure", string(outcome))
	}

	outcomes := []any{}
	for _, item := range bySequence(list(get(run, "attempts"))) {
		attempt := object(item)
		var calls []ae.RawToolCall
		for _, callItem := range bySequence(list(get(attempt, "toolCalls"))) {
			call := object(callItem)
			calls = append(calls, ae.RawToolCall{
				ToolCallID: must(ae.ParseUUID(text(get(call, "toolCallId")))),
				ToolName:   text(get(call, "toolName")),
				Arguments:  object(get(call, "arguments")),
				StartedAt:  must(ae.ParseInstant(text(get(call, "startedAt")))),
				Duration:   must(ae.ParseDuration(text(get(call, "duration")))),
				Result:     optionalText(get(call, "result")),
				Error:      optionalText(get(call, "error")),
			})
		}
		outcome, err := capture.AppendAttempt(ae.RawAttempt{
			AttemptID: must(ae.ParseUUID(text(get(attempt, "attemptId")))),
			StartedAt: must(ae.ParseInstant(text(get(attempt, "startedAt")))),
			Duration:  must(ae.ParseDuration(text(get(attempt, "duration")))),
			ToolCalls: calls,
			Result:    optionalText(get(attempt, "result")),
			Error:     optionalText(get(attempt, "error")),
		})
		if err != nil {
			return ae.FinalizedExperience{}, nil, err
		}
		outcomes = append(outcomes, string(outcome))
	}
	answers.Set("attempts", outcomes)
	if _, err := capture.Complete(ae.RunExecutionStatus(text(get(run, "executionStatus"))),
		must(ae.ParseInstant(text(get(run, "endedAt"))))); err != nil {
		return ae.FinalizedExperience{}, nil, err
	}
	captured := capture.Run()

	verification := object(get(c, "verification"))
	var closed *ae.ClosedVerificationRound
	if round := object(get(verification, "closedRound")); round != nil {
		closed = &ae.ClosedVerificationRound{
			RoundID:          must(ae.ParseUUID(text(get(round, "roundId")))),
			ArtifactRevision: text(get(round, "artifactRevision")),
		}
	}
	var checks []ae.RequiredCheck
	for _, item := range list(get(verification, "requiredChecks")) {
		check := object(item)
		checks = append(checks, ae.RequiredCheck{
			CheckID:      text(get(check, "checkId")),
			ExpectedKind: text(get(check, "expectedKind")),
		})
	}
	var evidence []ae.Evidence
	for _, item := range list(get(verification, "evidence")) {
		evidence = append(evidence, EvidenceOf(item))
	}
	finalized, err := ae.Finalize(captured, ae.Verification{
		ClosedRound:             closed,
		RequiredChecks:          checks,
		Evidence:                evidence,
		CurrentArtifactRevision: text(get(verification, "currentArtifactRevision")),
		FinalizedAt:             must(ae.ParseInstant(text(get(verification, "finalizedAt")))),
	})
	return finalized, answers, err
}
