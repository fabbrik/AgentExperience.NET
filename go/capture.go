package agentexperience

import (
	"bytes"
	"context"
	"errors"
	"sort"
)

// Capture: a run as its host observes it, sanitized and bounded as it is recorded (contract step 1). Capture answers
// each attempt with an outcome. Kept attempts are numbered from 0 in the order they were recorded, and an attempt's
// tool calls from 0 in the order given, beyond the 50th dropped.

// AppendOutcome is capture's answer to an attempt.
type AppendOutcome string

// Capture's answers.
const (
	OutcomeRecorded             AppendOutcome = "Recorded"
	OutcomeDuplicateNoOp        AppendOutcome = "DuplicateNoOp"
	OutcomeConflict             AppendOutcome = "Conflict"
	OutcomeCapacityExceeded     AppendOutcome = "CapacityExceeded"
	OutcomeSanitizationRejected AppendOutcome = "SanitizationRejected"
)

// RunExecutionStatus is how a run ended.
type RunExecutionStatus string

// How a run ends.
const (
	RunCompleted RunExecutionStatus = "Completed"
	RunFailed    RunExecutionStatus = "Failed"
	RunCancelled RunExecutionStatus = "Cancelled"
)

// RawToolCall is a tool call as the host observed it, before sanitization.
type RawToolCall struct {
	ToolCallID UUID
	ToolName   string
	// Arguments are the call's arguments: decoded JSON (DecodeJSON) or Go values (see SanitizationPolicy.Sanitize).
	Arguments *Object
	StartedAt Instant
	Duration  Duration
	Result    *string
	Error     *string
}

// RawAttempt is an attempt as the host observed it, before sanitization.
type RawAttempt struct {
	AttemptID UUID
	StartedAt Instant
	Duration  Duration
	ToolCalls []RawToolCall
	Result    *string
	Error     *string
}

// CapturedRun is a run as capture holds it: sanitized attempts, the exposures recorded, and how it ended.
type CapturedRun struct {
	RunID           UUID
	TaskID          string
	TaskDescription *string
	Scope           Scope
	Environment     EnvironmentFingerprint
	Provenance      Provenance
	StartedAt       Instant
	Attempts        []Attempt
	// ExecutionStatus is empty until the run is completed.
	ExecutionStatus RunExecutionStatus
	EndedAt         *Instant
}

// RunCapture captures one run. It is not safe for concurrent use: one run is captured by one caller.
type RunCapture struct {
	run          CapturedRun
	sanitization Sanitization
	limits       CaptureLimits
	seen         map[UUID]attemptSnapshot
}

// StartRun starts capturing a run. It starts exposed to nothing: exposures are recorded with RecordExposure. A zero
// Sanitization means DefaultSanitization() and zero CaptureLimits mean DefaultCaptureLimits(); any other limits must
// be strictly positive. The ToolResult policy must keep the "value" field (allowlisted, not a secret name): one that
// dropped it would silently store every result and error as null, so it is refused.
func StartRun(run CapturedRun, sanitization Sanitization, limits CaptureLimits) (*RunCapture, error) {
	return StartRunContext(context.Background(), run, sanitization, limits)
}

// StartRunContext is StartRun reporting to the observer (SetObserver) under ctx, so the operation's span is a child of
// the span ctx carries.
func StartRunContext(ctx context.Context, run CapturedRun, sanitization Sanitization,
	limits CaptureLimits) (*RunCapture, error) {
	_, op := Observe(ctx, "capture.start_run", false)
	defer op.End()
	if op != nil {
		op.Set(AttributeRunID, run.RunID.String())
	}
	capture, err := startRun(run, sanitization, limits)
	if err != nil {
		op.Fail(err)
		return nil, err
	}
	op.Done("Started")
	return capture, nil
}

func startRun(run CapturedRun, sanitization Sanitization, limits CaptureLimits) (*RunCapture, error) {
	if run.RunID.IsZero() {
		return nil, errors.New("a run must be named by a non-empty UUID")
	}
	if isNullOrWhitespace(run.TaskID) {
		return nil, errors.New("a run's task ID must not be blank")
	}
	if len(run.Provenance.ExposedTo) > 0 {
		return nil, errors.New("a run starts exposed to nothing; record exposures with RecordExposure")
	}
	if len(run.Attempts) > 0 || run.ExecutionStatus != "" || run.EndedAt != nil {
		return nil, errors.New("a run starts with no attempts, not yet completed")
	}
	if isZeroSanitization(sanitization) {
		sanitization = DefaultSanitization()
	}
	for _, policy := range []SanitizationPolicy{sanitization.ToolArguments, sanitization.ToolResult} {
		if err := policy.Limits.Validate(); err != nil {
			return nil, err
		}
	}
	if !sanitization.ToolResult.AllowedFieldNames[ValueField] || sanitization.ToolResult.IsSecret(ValueField) {
		return nil, errors.New("the ToolResult sanitization policy must keep the \"value\" field: allowlist it and " +
			"do not name it a secret, or every result and error would be stored as null")
	}
	if limits == (CaptureLimits{}) {
		limits = DefaultCaptureLimits()
	}
	if err := limits.Validate(); err != nil {
		return nil, err
	}
	return &RunCapture{run: run, sanitization: sanitization, limits: limits, seen: map[UUID]attemptSnapshot{}}, nil
}

func isZeroSanitization(s Sanitization) bool {
	zero := func(p SanitizationPolicy) bool {
		return p.AllowedFieldNames == nil && p.SecretFieldNames == nil && p.Limits == (SanitizationLimits{})
	}
	return zero(s.ToolArguments) && zero(s.ToolResult)
}

// Run is the run as captured so far.
func (c *RunCapture) Run() CapturedRun { return c.run }

// RecordExposure records stored records the run was shown, keeping the lowest revision seen of each.
func (c *RunCapture) RecordExposure(exposures ...RunExposure) (AppendOutcome, error) {
	for _, exposure := range exposures {
		if exposure.ExperienceID.IsZero() {
			return "", errors.New("an exposure must name a record, never an empty UUID")
		}
		if exposure.Revision < 0 {
			return "", errors.New("an exposure's revision must not be negative")
		}
	}
	if c.run.ExecutionStatus != "" {
		return OutcomeConflict, nil
	}
	merged := map[UUID]int64{}
	for _, exposure := range c.run.Provenance.ExposedTo {
		merged[exposure.ExperienceID] = exposure.Revision
	}
	changed := false
	for _, exposure := range exposures {
		held, ok := merged[exposure.ExperienceID]
		if !ok || exposure.Revision < held {
			merged[exposure.ExperienceID] = exposure.Revision
			changed = true
		}
	}
	if !changed {
		return OutcomeDuplicateNoOp, nil
	}
	if len(merged) > MaxExposuresPerRun {
		return OutcomeCapacityExceeded, nil
	}
	ordered := make([]RunExposure, 0, len(merged))
	for id, revision := range merged {
		ordered = append(ordered, RunExposure{id, revision})
	}
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].ExperienceID.String() < ordered[j].ExperienceID.String() })
	c.run.Provenance.ExposedTo = ordered
	return OutcomeRecorded, nil
}

// AppendAttempt sanitizes and records one attempt, and returns capture's answer. An attempt with an empty
// AttemptID is an error: it could never be told apart from another.
func (c *RunCapture) AppendAttempt(attempt RawAttempt) (AppendOutcome, error) {
	return c.AppendAttemptContext(context.Background(), attempt)
}

// AppendAttemptContext is AppendAttempt reporting to the observer (SetObserver) under ctx.
func (c *RunCapture) AppendAttemptContext(ctx context.Context, attempt RawAttempt) (AppendOutcome, error) {
	_, op := Observe(ctx, "capture.append_attempt", false)
	defer op.End()
	if op != nil {
		op.Set(AttributeRunID, c.run.RunID.String())
		op.Set(AttributeAttemptID, attempt.AttemptID.String())
	}
	if attempt.AttemptID.IsZero() {
		err := errors.New("an attempt must be named by a non-empty UUID")
		op.Fail(err)
		return "", err
	}
	outcome := c.appendAttempt(attempt)
	op.Done(string(outcome))
	return outcome, nil
}

func (c *RunCapture) appendAttempt(attempt RawAttempt) AppendOutcome {
	if c.run.ExecutionStatus != "" {
		return OutcomeConflict
	}
	if previous, ok := c.seen[attempt.AttemptID]; ok {
		if previous.matches(attempt) {
			return OutcomeDuplicateNoOp
		}
		return OutcomeConflict
	}
	if len(c.run.Attempts) >= c.limits.MaxAttemptsPerRun {
		return OutcomeCapacityExceeded
	}

	result, err := c.text(attempt.Result, c.limits.MaxResultLength)
	if err != nil {
		return OutcomeSanitizationRejected
	}
	failure, err := c.text(attempt.Error, c.limits.MaxErrorLength)
	if err != nil {
		return OutcomeSanitizationRejected
	}
	calls := attempt.ToolCalls
	if len(calls) > c.limits.MaxToolCallsPerAttempt {
		calls = calls[:c.limits.MaxToolCallsPerAttempt]
	}
	kept := make([]ToolCall, 0, len(calls))
	for sequence, raw := range calls {
		arguments, err := c.sanitization.ToolArguments.Sanitize(raw.Arguments)
		if err != nil {
			return OutcomeSanitizationRejected
		}
		callResult, err := c.text(raw.Result, c.limits.MaxResultLength)
		if err != nil {
			return OutcomeSanitizationRejected
		}
		callError, err := c.text(raw.Error, c.limits.MaxErrorLength)
		if err != nil {
			return OutcomeSanitizationRejected
		}
		kept = append(kept, ToolCall{
			ToolCallID:     raw.ToolCallID,
			SequenceNumber: int64(sequence),
			ToolName:       raw.ToolName,
			Arguments:      arguments,
			StartedAt:      raw.StartedAt,
			Duration:       raw.Duration,
			Result:         callResult,
			Error:          callError,
		})
	}

	c.seen[attempt.AttemptID] = snapshot(attempt)
	c.run.Attempts = append(c.run.Attempts, Attempt{
		AttemptID:      attempt.AttemptID,
		SequenceNumber: int64(len(c.run.Attempts)),
		StartedAt:      attempt.StartedAt,
		Duration:       attempt.Duration,
		ToolCalls:      kept,
		Result:         result,
		Error:          failure,
	})
	return OutcomeRecorded
}

// Complete ends the run, which Run then returns; no attempt or exposure is recorded after this. The status must be
// Completed, Failed or Cancelled, and the run cannot end before it started (both are errors). A run completes once:
// a second call answers Conflict and changes nothing.
func (c *RunCapture) Complete(status RunExecutionStatus, endedAt Instant) (AppendOutcome, error) {
	return c.CompleteContext(context.Background(), status, endedAt)
}

// CompleteContext is Complete reporting to the observer (SetObserver) under ctx.
func (c *RunCapture) CompleteContext(ctx context.Context, status RunExecutionStatus,
	endedAt Instant) (AppendOutcome, error) {
	_, op := Observe(ctx, "capture.complete_run", false)
	defer op.End()
	if op != nil {
		op.Set(AttributeRunID, c.run.RunID.String())
	}
	switch status {
	case RunCompleted, RunFailed, RunCancelled:
	default:
		err := errors.New("a run ends Completed, Failed or Cancelled")
		op.Fail(err)
		return "", err
	}
	if endedAt.Before(c.run.StartedAt) {
		err := errors.New("a run cannot end before it started")
		op.Fail(err)
		return "", err
	}
	if c.run.ExecutionStatus != "" {
		op.Done(string(OutcomeConflict))
		return OutcomeConflict, nil
	}
	c.run.ExecutionStatus = status
	c.run.EndedAt = &endedAt
	op.Done(string(OutcomeRecorded))
	return OutcomeRecorded, nil
}

// text is a result or error, sanitized as the single field "value", and replaced by the placeholder when too long.
func (c *RunCapture) text(raw *string, maxLength int) (*string, error) {
	if raw == nil {
		return nil, nil
	}
	sanitized, err := c.sanitization.ToolResult.Sanitize(NewObject(Member{ValueField, *raw}))
	if err != nil {
		return nil, err
	}
	value, _ := sanitized.Get(ValueField)
	s, ok := value.(string)
	if !ok {
		return nil, nil
	}
	if UTF16Len(s) > maxLength {
		placeholder := UTF16Prefix(TruncationPlaceholder, maxLength)
		return &placeholder, nil
	}
	return &s, nil
}

// attemptSnapshot is a deep copy of an attempt as it was submitted, kept to tell a resubmission of the same content
// (DuplicateNoOp) from a different one (Conflict). The arguments are kept as the JSON they are written as, so a
// caller changing its maps afterwards changes nothing here.
type attemptSnapshot struct {
	attempt   RawAttempt
	arguments [][]byte
}

func copyOptional(s *string) *string {
	if s == nil {
		return nil
	}
	copied := *s
	return &copied
}

func argumentsJSON(arguments *Object) []byte {
	if arguments == nil {
		arguments = &Object{}
	}
	data, err := MarshalJSONValue(arguments)
	if err != nil {
		return nil // Arguments that cannot be written never equal anything.
	}
	return data
}

func snapshot(attempt RawAttempt) attemptSnapshot {
	copied := attempt
	copied.Result, copied.Error = copyOptional(attempt.Result), copyOptional(attempt.Error)
	copied.ToolCalls = make([]RawToolCall, len(attempt.ToolCalls))
	arguments := make([][]byte, len(attempt.ToolCalls))
	for i, call := range attempt.ToolCalls {
		call.Result, call.Error, call.Arguments = copyOptional(call.Result), copyOptional(call.Error), nil
		copied.ToolCalls[i] = call
		arguments[i] = argumentsJSON(attempt.ToolCalls[i].Arguments)
	}
	return attemptSnapshot{attempt: copied, arguments: arguments}
}

// matches reports whether a resubmitted attempt has the same content: its fields, and its tool calls with their
// arguments compared as the JSON they are written as (an empty list equals no list).
func (s attemptSnapshot) matches(b RawAttempt) bool {
	a := s.attempt
	if a.AttemptID != b.AttemptID || a.StartedAt != b.StartedAt || a.Duration != b.Duration ||
		!equalOptional(a.Result, b.Result) || !equalOptional(a.Error, b.Error) || len(a.ToolCalls) != len(b.ToolCalls) {
		return false
	}
	for i := range a.ToolCalls {
		x, y := &a.ToolCalls[i], &b.ToolCalls[i]
		if x.ToolCallID != y.ToolCallID || x.ToolName != y.ToolName || x.StartedAt != y.StartedAt ||
			x.Duration != y.Duration || !equalOptional(x.Result, y.Result) || !equalOptional(x.Error, y.Error) {
			return false
		}
		left, right := s.arguments[i], argumentsJSON(y.Arguments)
		if left == nil || right == nil || !bytes.Equal(left, right) {
			return false
		}
	}
	return true
}
