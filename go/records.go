package agentexperience

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"strconv"
)

// The Experience Record and its stored form: one row of agent_experience.experience_records, as the record columns
// plus the payload column at payload_version 1 (contract/schema/experience-record.v1-1.19.json since contract 1.19). Enums are their .NET
// member names, exactly as written.

// PayloadVersion is the payload version this package reads and writes: plaintext.
const PayloadVersion = 1

// ExperienceStatus is a record's lifecycle status.
type ExperienceStatus string

// The lifecycle statuses.
const (
	StatusCandidate   ExperienceStatus = "Candidate"
	StatusValidated   ExperienceStatus = "Validated"
	StatusQuarantined ExperienceStatus = "Quarantined"
	StatusContested   ExperienceStatus = "Contested"
	StatusStale       ExperienceStatus = "Stale"
	StatusSuperseded  ExperienceStatus = "Superseded"
	StatusRevoked     ExperienceStatus = "Revoked"
	StatusReinforced  ExperienceStatus = "Reinforced"
)

var experienceStatuses = []string{"Candidate", "Validated", "Quarantined", "Contested", "Stale", "Superseded",
	"Revoked", "Reinforced"}

// VerificationStatus is a run's verification outcome.
type VerificationStatus string

// The verification statuses.
const (
	VerificationUnknown  VerificationStatus = "Unknown"
	VerificationVerified VerificationStatus = "Verified"
	VerificationFailed   VerificationStatus = "Failed"
)

var verificationStatuses = []string{"Unknown", "Verified", "Failed"}

// CheckResult is one piece of evidence's result.
type CheckResult string

// The check results.
const (
	CheckPass    CheckResult = "Pass"
	CheckFail    CheckResult = "Fail"
	CheckUnknown CheckResult = "Unknown"
)

var checkResults = []string{"Pass", "Fail", "Unknown"}

// Authorship says who wrote a reflection.
type Authorship string

// The authorships.
const (
	AuthorshipDeterministic Authorship = "Deterministic"
	AuthorshipModel         Authorship = "Model"
)

// Origin says how a record was written.
type Origin string

// The origins.
const (
	OriginHostWritten Origin = "HostWritten"
	OriginFinalized   Origin = "Finalized"
)

// Scope is where a record lives. The optional fields are nil when unset, never the empty string.
type Scope struct {
	TenantID      string
	ApplicationID string
	ProjectID     string
	TeamID        *string
	AgentID       *string
	UserID        *string
}

// Fields are the six scope fields in the contract's order; nil for an unset one.
func (s Scope) Fields() []*string {
	return []*string{&s.TenantID, &s.ApplicationID, &s.ProjectID, s.TeamID, s.AgentID, s.UserID}
}

// Equal reports whether two scopes are exactly the same.
func (s Scope) Equal(other Scope) bool {
	a, b := s.Fields(), other.Fields()
	for i := range a {
		if !equalOptional(a[i], b[i]) {
			return false
		}
	}
	return true
}

func equalOptional(a, b *string) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

// Ptr is a pointer to s, for the optional fields.
func Ptr(s string) *string { return &s }

// EnvironmentFingerprint is the environment a run ran in.
type EnvironmentFingerprint struct {
	HostName           string
	RuntimeVersion     string
	OperatingSystem    string
	ApplicationVersion *string
	Metadata           map[string]string
}

// RunExposure is a stored record a run was shown, by ID and revision.
type RunExposure struct {
	ExperienceID UUID
	Revision     int64
}

// Provenance is where a run came from.
type Provenance struct {
	Source        string
	SourceVersion *string
	RecordedAt    Instant
	CorrelationID *string
	ExposedTo     []RunExposure
}

// ToolCall is one stored tool call. Its arguments are the sanitized arguments, their numbers kept as written.
type ToolCall struct {
	ToolCallID     UUID
	SequenceNumber int64
	ToolName       string
	Arguments      *Object
	StartedAt      Instant
	Duration       Duration
	Result         *string
	Error          *string
	// RecipeExcerpt is the excerpt of what the call returned that a host vouched for at capture (contract 1.19),
	// nil when absent: the payload then has no recipeExcerpt member. This port reads and writes it back; it never
	// captures one.
	RecipeExcerpt *string
}

// Attempt is one stored attempt.
type Attempt struct {
	AttemptID      UUID
	SequenceNumber int64
	StartedAt      Instant
	Duration       Duration
	ToolCalls      []ToolCall
	Result         *string
	Error          *string
}

// Evidence is one piece of verification evidence.
type Evidence struct {
	EvidenceID          UUID
	VerificationRoundID UUID
	ArtifactRevision    string
	CheckID             string
	Kind                string
	Result              CheckResult
	Producer            string
	Detail              *string
	CapturedAt          Instant
}

// Outcome is a run's verification outcome.
type Outcome struct {
	Status      VerificationStatus
	Evidence    []Evidence
	Reason      *string
	EvaluatedAt Instant
}

// Reflection is the lesson a record carries.
type Reflection struct {
	ReflectionID            UUID
	ExperienceRunID         UUID
	Lesson                  string
	SuccessfulApproaches    []string
	FailedApproaches        []string
	Preconditions           []string
	Warnings                []string
	ReuseGuidance           *string
	EvidenceIDs             []UUID
	VerificationStatus      VerificationStatus
	CompletionScore         float64
	VerificationRuleVersion string
	Producer                string
	CreatedAt               Instant
	// Authorship is Deterministic when empty.
	Authorship Authorship
}

// ExperienceRecord is one stored Experience Record.
type ExperienceRecord struct {
	ExperienceID          UUID
	SourceRunID           UUID
	Scope                 Scope
	TaskID                string
	TaskSummary           *string
	Attempts              []Attempt
	Outcome               Outcome
	CompletionScore       float64
	Reflection            *Reflection
	Environment           EnvironmentFingerprint
	Provenance            Provenance
	Status                ExperienceStatus
	ReuseConfidence       float64
	SupportingValidations int64
	Contradictions        int64
	Revision              int64
	CreatedAt             Instant
	UpdatedAt             Instant
	// ClosedRoundID is the closed verification round, when the record was finalized with one.
	ClosedRoundID *UUID
	// Origin is HostWritten when empty.
	Origin Origin
	// ProvenanceSignature is outside contract 1.x: a record read with one carries it unchanged, and a port writes
	// none.
	ProvenanceSignature any
}

// ColumnNames are the eighteen columns a writer supplies, in the order the contract lists them.
var ColumnNames = []string{
	"experience_id", "source_run_id", "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id",
	"task_id", "status", "reuse_confidence", "supporting_validations", "contradictions", "revision", "created_at",
	"updated_at", "payload_version", "payload",
}

// ---- The stored form ---------------------------------------------------------------------------------------------

func optionalString(s *string) any {
	if s == nil {
		return nil
	}
	return *s
}

// double is a double as the record writes it. A NaN or an infinity stays a float64, which MarshalJSONValue refuses
// to write: it is not JSON.
func double(value float64) any {
	number, err := doubleNumber(value)
	if err != nil {
		return value
	}
	return number
}

func integer(value int64) json.Number {
	return json.Number(strconv.FormatInt(value, 10))
}

func stringList(items []string) []any {
	list := make([]any, len(items))
	for i, item := range items {
		list[i] = item
	}
	return list
}

// Columns are the record columns as the contract's JSON writes them (without payload).
func (r *ExperienceRecord) Columns() *Object {
	return NewObject(
		Member{"experience_id", r.ExperienceID.String()},
		Member{"source_run_id", r.SourceRunID.String()},
		Member{"tenant_id", r.Scope.TenantID},
		Member{"application_id", r.Scope.ApplicationID},
		Member{"project_id", r.Scope.ProjectID},
		Member{"team_id", optionalString(r.Scope.TeamID)},
		Member{"agent_id", optionalString(r.Scope.AgentID)},
		Member{"user_id", optionalString(r.Scope.UserID)},
		Member{"task_id", r.TaskID},
		Member{"status", string(r.Status)},
		Member{"reuse_confidence", double(r.ReuseConfidence)},
		Member{"supporting_validations", integer(r.SupportingValidations)},
		Member{"contradictions", integer(r.Contradictions)},
		Member{"revision", integer(r.Revision)},
		Member{"created_at", r.CreatedAt.TruncateToMicroseconds().String()},
		Member{"updated_at", r.UpdatedAt.TruncateToMicroseconds().String()},
		Member{"payload_version", integer(PayloadVersion)},
	)
}

// Payload is the payload column's JSON at payload_version 1.
func (r *ExperienceRecord) Payload() *Object {
	attempts := make([]any, len(r.Attempts))
	for i := range r.Attempts {
		attempts[i] = attemptJSON(&r.Attempts[i])
	}
	evidence := make([]any, len(r.Outcome.Evidence))
	for i := range r.Outcome.Evidence {
		evidence[i] = evidenceJSON(&r.Outcome.Evidence[i])
	}
	var reflection any
	if r.Reflection != nil {
		reflection = reflectionJSON(r.Reflection)
	}
	metadata := ObjectFromMap(r.Environment.Metadata)
	payload := NewObject(
		Member{"taskSummary", optionalString(r.TaskSummary)},
		Member{"attempts", attempts},
		Member{"outcome", NewObject(
			Member{"status", string(r.Outcome.Status)},
			Member{"evidence", evidence},
			Member{"reason", optionalString(r.Outcome.Reason)},
			Member{"evaluatedAt", r.Outcome.EvaluatedAt.String()},
		)},
		Member{"completionScore", double(r.CompletionScore)},
		Member{"reflection", reflection},
		Member{"environment", NewObject(
			Member{"hostName", r.Environment.HostName},
			Member{"runtimeVersion", r.Environment.RuntimeVersion},
			Member{"operatingSystem", r.Environment.OperatingSystem},
			Member{"applicationVersion", optionalString(r.Environment.ApplicationVersion)},
			Member{"metadata", metadata},
		)},
		Member{"provenance", provenanceJSON(&r.Provenance)},
	)
	if r.ClosedRoundID != nil {
		payload.Set("closedRoundId", r.ClosedRoundID.String())
	}
	if r.Origin != "" && r.Origin != OriginHostWritten {
		payload.Set("origin", string(r.Origin))
	}
	if r.ProvenanceSignature != nil {
		payload.Set("provenanceSignature", r.ProvenanceSignature)
	}
	return payload
}

// ToStored is the record as the table holds it, and as the schema describes it: {"columns": ..., "payload": ...}.
func (r *ExperienceRecord) ToStored() *Object {
	return NewObject(Member{"columns", r.Columns()}, Member{"payload", r.Payload()})
}

// MarshalJSON writes the stored form.
func (r *ExperienceRecord) MarshalJSON() ([]byte, error) {
	return MarshalJSONValue(r.ToStored())
}

func attemptJSON(a *Attempt) *Object {
	calls := make([]any, len(a.ToolCalls))
	for i := range a.ToolCalls {
		call := &a.ToolCalls[i]
		arguments := call.Arguments
		if arguments == nil {
			arguments = &Object{}
		}
		members := []Member{
			{"toolCallId", call.ToolCallID.String()},
			{"sequenceNumber", integer(call.SequenceNumber)},
			{"toolName", call.ToolName},
			{"arguments", arguments},
			{"startedAt", call.StartedAt.String()},
			{"duration", call.Duration.String()},
			{"result", optionalString(call.Result)},
			{"error", optionalString(call.Error)},
		}
		if call.RecipeExcerpt != nil {
			members = append(members, Member{"recipeExcerpt", *call.RecipeExcerpt})
		}
		calls[i] = NewObject(members...)
	}
	return NewObject(
		Member{"attemptId", a.AttemptID.String()},
		Member{"sequenceNumber", integer(a.SequenceNumber)},
		Member{"startedAt", a.StartedAt.String()},
		Member{"duration", a.Duration.String()},
		Member{"toolCalls", calls},
		Member{"result", optionalString(a.Result)},
		Member{"error", optionalString(a.Error)},
	)
}

func evidenceJSON(e *Evidence) *Object {
	return NewObject(
		Member{"evidenceId", e.EvidenceID.String()},
		Member{"verificationRoundId", e.VerificationRoundID.String()},
		Member{"artifactRevision", e.ArtifactRevision},
		Member{"checkId", e.CheckID},
		Member{"kind", e.Kind},
		Member{"result", string(e.Result)},
		Member{"producer", e.Producer},
		Member{"detail", optionalString(e.Detail)},
		Member{"capturedAt", e.CapturedAt.String()},
	)
}

func reflectionJSON(r *Reflection) *Object {
	ids := make([]any, len(r.EvidenceIDs))
	for i, id := range r.EvidenceIDs {
		ids[i] = id.String()
	}
	value := NewObject(
		Member{"reflectionId", r.ReflectionID.String()},
		Member{"experienceRunId", r.ExperienceRunID.String()},
		Member{"lesson", r.Lesson},
		Member{"successfulApproaches", stringList(r.SuccessfulApproaches)},
		Member{"failedApproaches", stringList(r.FailedApproaches)},
		Member{"preconditions", stringList(r.Preconditions)},
		Member{"warnings", stringList(r.Warnings)},
		Member{"reuseGuidance", optionalString(r.ReuseGuidance)},
		Member{"evidenceIds", ids},
		Member{"verificationStatus", string(r.VerificationStatus)},
		Member{"completionScore", double(r.CompletionScore)},
		Member{"verificationRuleVersion", r.VerificationRuleVersion},
		Member{"producer", r.Producer},
		Member{"createdAt", r.CreatedAt.String()},
	)
	if r.Authorship != "" && r.Authorship != AuthorshipDeterministic {
		value.Set("authorship", string(r.Authorship))
	}
	return value
}

func provenanceJSON(p *Provenance) *Object {
	value := NewObject(
		Member{"source", p.Source},
		Member{"sourceVersion", optionalString(p.SourceVersion)},
		Member{"recordedAt", p.RecordedAt.String()},
		Member{"correlationId", optionalString(p.CorrelationID)},
	)
	if len(p.ExposedTo) > 0 {
		exposures := make([]any, len(p.ExposedTo))
		for i, exposure := range p.ExposedTo {
			exposures[i] = NewObject(
				Member{"experienceId", exposure.ExperienceID.String()},
				Member{"revision", integer(exposure.Revision)},
			)
		}
		value.Set("exposedTo", exposures)
	}
	return value
}

// ---- Reading the stored form -------------------------------------------------------------------------------------

// readError is a stored form this package cannot read; the readers below panic with it, and the exported functions
// recover it into an error.
type readError struct{ message string }

func failRead(format string, args ...any) {
	panic(readError{fmt.Sprintf(format, args...)})
}

func recoverRead(err *error) {
	if recovered := recover(); recovered != nil {
		failure, ok := recovered.(readError)
		if !ok {
			panic(recovered)
		}
		*err = errors.New(failure.message)
	}
}

func asObject(value any, name string) *Object {
	o, ok := value.(*Object)
	if !ok || o == nil {
		failRead("%s must be a JSON object", name)
	}
	return o
}

func member(o *Object, key string) any {
	value, ok := o.Get(key)
	if !ok {
		failRead("the member %s is missing", key)
	}
	return value
}

func readString(o *Object, key string) string {
	value, ok := member(o, key).(string)
	if !ok {
		failRead("%s must be a string", key)
	}
	return value
}

func readOptionalString(o *Object, key string) *string {
	value, _ := o.Get(key)
	if value == nil {
		return nil
	}
	s, ok := value.(string)
	if !ok {
		failRead("%s must be a string or null", key)
	}
	return &s
}

func readList(o *Object, key string) []any {
	value, ok := member(o, key).([]any)
	if !ok {
		failRead("%s must be a JSON array", key)
	}
	return value
}

func readStrings(o *Object, key string) []string {
	items := readList(o, key)
	list := make([]string, len(items))
	for i, item := range items {
		s, ok := item.(string)
		if !ok {
			failRead("%s must hold strings", key)
		}
		list[i] = s
	}
	return list
}

func readNumber(o *Object, key string) float64 {
	n, ok := member(o, key).(json.Number)
	if !ok {
		failRead("%s must be a JSON number", key)
	}
	value, err := strconv.ParseFloat(string(n), 64)
	if err != nil || math.IsInf(value, 0) {
		failRead("%s must be a finite JSON number", key)
	}
	return value
}

func readInteger(o *Object, key string) int64 {
	n, ok := member(o, key).(json.Number)
	if !ok {
		failRead("%s must be a JSON integer", key)
	}
	if value, err := strconv.ParseInt(string(n), 10, 64); err == nil {
		return value
	}
	value, err := strconv.ParseFloat(string(n), 64)
	if err != nil || value != math.Trunc(value) || math.Abs(value) >= 1<<63 {
		failRead("%s must be a JSON integer", key)
	}
	return int64(value)
}

func readUUID(o *Object, key string) UUID {
	id, err := ParseUUID(readString(o, key))
	if err != nil {
		failRead("%s: %v", key, err)
	}
	return id
}

func readInstant(o *Object, key string) Instant {
	instant, err := ParseInstant(readString(o, key))
	if err != nil {
		failRead("%s: %v", key, err)
	}
	return instant
}

func readDuration(o *Object, key string) Duration {
	duration, err := ParseDuration(readString(o, key))
	if err != nil {
		failRead("%s: %v", key, err)
	}
	return duration
}

func readEnum(o *Object, key string, allowed []string) string {
	value := readString(o, key)
	for _, name := range allowed {
		if value == name {
			return value
		}
	}
	failRead("%s is not one of the member names %v", key, allowed)
	return ""
}

// FromStored reads a record from its stored form: {"columns": ..., "payload": ...}, as DecodeJSON reads it.
func FromStored(stored any) (record ExperienceRecord, err error) {
	defer recoverRead(&err)
	o := asObject(stored, "the stored record")
	columns := asObject(member(o, "columns"), "columns")
	if version := readInteger(columns, "payload_version"); version != PayloadVersion {
		return ExperienceRecord{}, fmt.Errorf(
			"payload_version %d is not supported; this package reads version %d only", version, PayloadVersion)
	}
	return FromColumns(columns, member(o, "payload"))
}

// UnmarshalStored reads a record from the JSON text of its stored form.
func UnmarshalStored(data []byte) (ExperienceRecord, error) {
	value, err := DecodeJSON(data)
	if err != nil {
		return ExperienceRecord{}, err
	}
	return FromStored(value)
}

// FromColumns reads a record from its columns, as the contract's JSON writes them, and its payload. Every number in
// a tool call's arguments is kept as written; one beyond a double's range is refused (.NET reads it as an infinity
// it cannot write again).
func FromColumns(columnsValue any, payloadValue any) (record ExperienceRecord, err error) {
	defer recoverRead(&err)
	columns := asObject(columnsValue, "columns")
	payload := asObject(payloadValue, "payload")
	environment := asObject(member(payload, "environment"), "environment")
	provenance := asObject(member(payload, "provenance"), "provenance")
	outcome := asObject(member(payload, "outcome"), "outcome")

	record = ExperienceRecord{
		ExperienceID: readUUID(columns, "experience_id"),
		SourceRunID:  readUUID(columns, "source_run_id"),
		Scope: Scope{
			TenantID:      readString(columns, "tenant_id"),
			ApplicationID: readString(columns, "application_id"),
			ProjectID:     readString(columns, "project_id"),
			TeamID:        readOptionalString(columns, "team_id"),
			AgentID:       readOptionalString(columns, "agent_id"),
			UserID:        readOptionalString(columns, "user_id"),
		},
		TaskID:                readString(columns, "task_id"),
		TaskSummary:           readOptionalString(payload, "taskSummary"),
		CompletionScore:       readNumber(payload, "completionScore"),
		Status:                ExperienceStatus(readEnum(columns, "status", experienceStatuses)),
		ReuseConfidence:       readNumber(columns, "reuse_confidence"),
		SupportingValidations: readInteger(columns, "supporting_validations"),
		Contradictions:        readInteger(columns, "contradictions"),
		Revision:              readInteger(columns, "revision"),
		CreatedAt:             readInstant(columns, "created_at"),
		UpdatedAt:             readInstant(columns, "updated_at"),
		Origin:                OriginHostWritten,
	}
	for _, item := range readList(payload, "attempts") {
		record.Attempts = append(record.Attempts, readAttempt(asObject(item, "an attempt")))
	}
	record.Outcome = Outcome{
		Status:      VerificationStatus(readEnum(outcome, "status", verificationStatuses)),
		Reason:      readOptionalString(outcome, "reason"),
		EvaluatedAt: readInstant(outcome, "evaluatedAt"),
	}
	for _, item := range readList(outcome, "evidence") {
		record.Outcome.Evidence = append(record.Outcome.Evidence, readEvidence(asObject(item, "evidence")))
	}
	if value, _ := payload.Get("reflection"); value != nil {
		reflection := readReflection(asObject(value, "reflection"))
		record.Reflection = &reflection
	}
	metadata := asObject(member(environment, "metadata"), "metadata")
	record.Environment = EnvironmentFingerprint{
		HostName:           readString(environment, "hostName"),
		RuntimeVersion:     readString(environment, "runtimeVersion"),
		OperatingSystem:    readString(environment, "operatingSystem"),
		ApplicationVersion: readOptionalString(environment, "applicationVersion"),
		Metadata:           make(map[string]string, metadata.Len()),
	}
	for _, m := range metadata.Members() {
		value, ok := m.Value.(string)
		if !ok {
			failRead("environment metadata values must be strings")
		}
		record.Environment.Metadata[m.Key] = value
	}
	record.Provenance = Provenance{
		Source:        readString(provenance, "source"),
		SourceVersion: readOptionalString(provenance, "sourceVersion"),
		RecordedAt:    readInstant(provenance, "recordedAt"),
		CorrelationID: readOptionalString(provenance, "correlationId"),
	}
	if value, _ := provenance.Get("exposedTo"); value != nil {
		list, ok := value.([]any)
		if !ok {
			failRead("exposedTo must be a JSON array")
		}
		for _, item := range list {
			exposure := asObject(item, "an exposure")
			record.Provenance.ExposedTo = append(record.Provenance.ExposedTo, RunExposure{
				ExperienceID: readUUID(exposure, "experienceId"),
				Revision:     readInteger(exposure, "revision"),
			})
		}
	}
	if value, _ := payload.Get("closedRoundId"); value != nil {
		id := readUUID(payload, "closedRoundId")
		record.ClosedRoundID = &id
	}
	if value, _ := payload.Get("origin"); value != nil {
		record.Origin = Origin(readEnum(payload, "origin", []string{"HostWritten", "Finalized"}))
	}
	if value, _ := payload.Get("provenanceSignature"); value != nil {
		record.ProvenanceSignature = value
	}
	return record, nil
}

func readAttempt(o *Object) Attempt {
	attempt := Attempt{
		AttemptID:      readUUID(o, "attemptId"),
		SequenceNumber: readInteger(o, "sequenceNumber"),
		StartedAt:      readInstant(o, "startedAt"),
		Duration:       readDuration(o, "duration"),
		Result:         readOptionalString(o, "result"),
		Error:          readOptionalString(o, "error"),
	}
	for _, item := range readList(o, "toolCalls") {
		call := asObject(item, "a tool call")
		arguments := asObject(member(call, "arguments"), "arguments")
		checkStoredArguments(arguments)
		attempt.ToolCalls = append(attempt.ToolCalls, ToolCall{
			ToolCallID:     readUUID(call, "toolCallId"),
			SequenceNumber: readInteger(call, "sequenceNumber"),
			ToolName:       readString(call, "toolName"),
			Arguments:      arguments,
			StartedAt:      readInstant(call, "startedAt"),
			Duration:       readDuration(call, "duration"),
			Result:         readOptionalString(call, "result"),
			Error:          readOptionalString(call, "error"),
			RecipeExcerpt:  readOptionalString(call, "recipeExcerpt"),
		})
	}
	return attempt
}

// checkStoredArguments refuses a stored argument number beyond a double's range, which is outside the contract.
func checkStoredArguments(value any) {
	switch v := value.(type) {
	case json.Number:
		if !isJSONNumber(string(v)) {
			failRead("a stored tool-call argument is not a JSON number")
		}
		if !isFiniteNumber(v) {
			failRead("a stored tool-call argument is a number beyond a double's range, which is outside the " +
				"contract: .NET reads it back as an infinity it cannot write again")
		}
	case *Object:
		for _, m := range v.Members() {
			checkStoredArguments(m.Value)
		}
	case []any:
		for _, item := range v {
			checkStoredArguments(item)
		}
	}
}

func readEvidence(o *Object) Evidence {
	return Evidence{
		EvidenceID:          readUUID(o, "evidenceId"),
		VerificationRoundID: readUUID(o, "verificationRoundId"),
		ArtifactRevision:    readString(o, "artifactRevision"),
		CheckID:             readString(o, "checkId"),
		Kind:                readString(o, "kind"),
		Result:              CheckResult(readEnum(o, "result", checkResults)),
		Producer:            readString(o, "producer"),
		Detail:              readOptionalString(o, "detail"),
		CapturedAt:          readInstant(o, "capturedAt"),
	}
}

func readReflection(o *Object) Reflection {
	reflection := Reflection{
		ReflectionID:            readUUID(o, "reflectionId"),
		ExperienceRunID:         readUUID(o, "experienceRunId"),
		Lesson:                  readString(o, "lesson"),
		SuccessfulApproaches:    readStrings(o, "successfulApproaches"),
		FailedApproaches:        readStrings(o, "failedApproaches"),
		Preconditions:           readStrings(o, "preconditions"),
		Warnings:                readStrings(o, "warnings"),
		ReuseGuidance:           readOptionalString(o, "reuseGuidance"),
		VerificationStatus:      VerificationStatus(readEnum(o, "verificationStatus", verificationStatuses)),
		CompletionScore:         readNumber(o, "completionScore"),
		VerificationRuleVersion: readString(o, "verificationRuleVersion"),
		Producer:                readString(o, "producer"),
		CreatedAt:               readInstant(o, "createdAt"),
		Authorship:              AuthorshipDeterministic,
	}
	for _, item := range readList(o, "evidenceIds") {
		s, ok := item.(string)
		if !ok {
			failRead("evidenceIds must hold UUIDs")
		}
		id, err := ParseUUID(s)
		if err != nil {
			failRead("evidenceIds: %v", err)
		}
		reflection.EvidenceIDs = append(reflection.EvidenceIDs, id)
	}
	// Any authorship but Deterministic is read as model-authored (the schema writes only "Model").
	if value, _ := o.Get("authorship"); value != nil && value != "Deterministic" {
		reflection.Authorship = AuthorshipModel
	}
	return reflection
}
