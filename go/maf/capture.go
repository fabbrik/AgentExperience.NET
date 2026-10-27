package maf

import (
	"context"
	"encoding/json"
	"fmt"
	"iter"
	"sort"
	"strings"
	"sync"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- The per-invocation capture scope ----------------------------------------------------------------------------

type scopeKey struct{}

type pendingCall struct {
	order int
	call  ae.RawToolCall
}

// captureScope is one invocation's capture: its identity, its run, and the tool calls observed. Safe for concurrent
// use (tools may run in parallel). Never panics or returns an error.
type captureScope struct {
	settings *settings
	agent    *agent.Agent
	identity *ae.Identity

	mu        sync.Mutex
	capture   *ae.RunCapture
	runID     *ae.UUID
	startedAt ae.Instant
	calls     []pendingCall
	nextOrder int
	reported  map[CaptureStage]bool
	finalized bool
}

func scopeFrom(ctx context.Context) *captureScope {
	scope, _ := ctx.Value(scopeKey{}).(*captureScope)
	return scope
}

func (c *captureScope) report(stage CaptureStage, reason string, err error) {
	c.mu.Lock()
	if c.reported[stage] {
		c.mu.Unlock()
		return
	}
	c.reported[stage] = true
	runID := c.runID
	c.mu.Unlock()
	if c.settings.OnCaptureFailure == nil {
		return
	}
	defer func() { _ = recover() }()
	c.settings.OnCaptureFailure(CaptureFailure{Stage: stage, Reason: reason, RunID: runID, Err: err})
}

// run is the invocation's run ID, when its run started and is not finalized yet.
func (c *captureScope) run() (ae.UUID, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.capture == nil || c.runID == nil || c.finalized {
		return ae.UUID{}, false
	}
	return *c.runID, true
}

// captured reports whether the invocation is being captured (it has an identity, and its run started).
func (c *captureScope) captured() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.capture != nil
}

// start opens the run, its task text the invocation's own input.
func (c *captureScope) start(ctx context.Context, messages []*message.Message) {
	if c.identity == nil {
		return
	}
	s := c.settings
	var err error
	func() {
		defer recoverInto(&err)
		now := s.Clock()
		var description *string
		if text, ok, derr := ae.DeriveTaskText(taskMessages(messages), ae.DefaultTaskTextMaxLength,
			ae.FollowUpThreshold); derr == nil && ok {
			description = &text
		}
		version := sourceVersion
		run := ae.CapturedRun{
			RunID:           s.NewID(),
			TaskID:          s.taskIDFor(c.agent),
			TaskDescription: description,
			Scope:           c.identity.Scope,
			Environment:     s.environment,
			Provenance:      ae.Provenance{Source: ProvenanceSource, SourceVersion: &version, RecordedAt: now},
			StartedAt:       now,
		}
		var capture *ae.RunCapture
		capture, err = ae.StartRunContext(ctx, run, s.Sanitization, ae.CaptureLimits{})
		if err != nil {
			return
		}
		c.mu.Lock()
		c.capture, c.runID, c.startedAt = capture, &run.RunID, now
		c.mu.Unlock()
	}()
	if err != nil {
		c.report(StageStartRun, "Starting the run failed; the invocation runs uncaptured.", err)
	}
}

func (c *captureScope) beginCall() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	order := c.nextOrder
	c.nextOrder++
	return order
}

func (c *captureScope) addCall(order int, call ae.RawToolCall) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.finalized {
		c.calls = append(c.calls, pendingCall{order, call})
	}
}

func (c *captureScope) recordExposures(exposures []ae.RunExposure) {
	if len(exposures) == 0 {
		return
	}
	var outcome ae.AppendOutcome
	var err error
	func() {
		defer recoverInto(&err)
		c.mu.Lock()
		defer c.mu.Unlock()
		if c.capture == nil || c.finalized {
			outcome = ae.OutcomeDuplicateNoOp
			return
		}
		outcome, err = c.capture.RecordExposure(exposures...)
	}()
	switch {
	case err != nil:
		c.report(StageRecordExposure, "Recording the delivered records failed.", err)
	case outcome != ae.OutcomeRecorded && outcome != ae.OutcomeDuplicateNoOp:
		c.report(StageRecordExposure, "Capture answered "+string(outcome)+" for the delivered records.", nil)
	}
}

// finish finalizes the run, bounded by the finalization timeout on its own clock, never the invocation's context:
// past it the work is abandoned (it may still complete, and its context is then canceled) and reported.
func (c *captureScope) finish(ctx context.Context, status ae.RunExecutionStatus, result, failure *string) {
	if !c.captured() {
		return
	}
	timeout := c.settings.FinalizationTimeout
	work, cancel := context.WithTimeout(context.WithoutCancel(ctx), timeout)
	done := make(chan struct{})
	settled := false // written before done closes, read after
	go func() {
		defer cancel()
		defer close(done)
		var err error
		func() {
			defer recoverInto(&err)
			settled = c.finishWork(work, status, result, failure)
		}()
		if err != nil && work.Err() == nil {
			settled = true
			c.report(StageFinalize, "Finalization panicked; the run is not stored.", err)
		}
	}()
	select {
	case <-done:
	case <-work.Done():
		select {
		case <-done:
		default:
		}
	}
	select {
	case <-done:
		if settled {
			return
		}
	default:
	}
	c.report(StageFinalize, fmt.Sprintf("Finalization did not finish within %v; it was abandoned, and the record "+
		"may still be stored.", timeout), context.DeadlineExceeded)
}

// finishWork appends this invocation's attempt, completes the run, and verifies, finalizes and stores it. It reports
// that it settled the run (stored it, or decided or failed not to, and reported why) before its context ended; once
// its context has ended it reports nothing, stores nothing more and tells no one: the work was abandoned, which
// finish reports.
func (c *captureScope) finishWork(ctx context.Context, status ae.RunExecutionStatus, result, failure *string) bool {
	s := c.settings
	report := func(stage CaptureStage, reason string, err error) bool {
		if ctx.Err() != nil {
			return false
		}
		c.report(stage, reason, err)
		return true
	}
	c.mu.Lock()
	if c.finalized {
		c.mu.Unlock()
		return true
	}
	c.finalized = true
	sort.SliceStable(c.calls, func(i, j int) bool { return c.calls[i].order < c.calls[j].order })
	calls := make([]ae.RawToolCall, len(c.calls))
	for i := range c.calls {
		calls[i] = c.calls[i].call
	}
	capture, started := c.capture, c.startedAt
	c.mu.Unlock()
	if capture == nil || c.identity == nil {
		return true
	}

	ended := s.Clock()
	outcome, err := capture.AppendAttemptContext(ctx, ae.RawAttempt{AttemptID: s.NewID(), StartedAt: started,
		Duration: elapsed(started, ended), ToolCalls: calls, Result: result, Error: failure})
	if err != nil {
		return report(StageAppendAttempt, "Appending the attempt failed; the run is not stored.", err)
	}
	if outcome != ae.OutcomeRecorded && outcome != ae.OutcomeDuplicateNoOp {
		return report(StageAppendAttempt, "Capture answered "+string(outcome)+"; the run is not stored.", nil)
	}
	if !ended.Before(started) {
		_, err = capture.CompleteContext(ctx, status, ended)
	} else {
		_, err = capture.CompleteContext(ctx, status, started)
	}
	if err != nil {
		return report(StageCompleteRun, "Completing the run failed; the run is not stored.", err)
	}

	// As in .NET, a run whose invocation failed or was cancelled is verified too: Verify decides whether it is
	// stored (a failed check stores it Quarantined).
	if s.Verify == nil {
		return true
	}
	run := capture.Run()
	roundID := s.NewID()
	var verdict *ae.Verdict
	func() {
		defer recoverInto(&err)
		verdict, err = s.Verify(ctx, &ae.VerificationContext{Run: run, RoundID: roundID, Identity: *c.identity,
			Clock: s.Clock, NewID: s.NewID})
	}()
	if err != nil {
		return report(StageVerify, "Verify failed; the run is not stored.", err)
	}
	if verdict == nil {
		return ctx.Err() == nil
	}
	for _, item := range verdict.Evidence {
		if item.VerificationRoundID != roundID || item.ArtifactRevision != verdict.ArtifactRevision {
			return report(StageVerify, "Verify returned evidence that does not name this run's verification round "+
				"and the returned artifact revision; build it with VerificationContext.CreateEvidence and the same "+
				"revision. The run is not stored.", nil)
		}
	}

	finalizedAt := s.Clock()
	finalized, err := ae.FinalizeContext(ctx, run, ae.Verification{
		ClosedRound:             &ae.ClosedVerificationRound{RoundID: roundID, ArtifactRevision: verdict.ArtifactRevision},
		RequiredChecks:          verdict.RequiredChecks,
		Evidence:                verdict.Evidence,
		CurrentArtifactRevision: verdict.ArtifactRevision,
		FinalizedAt:             finalizedAt,
	})
	if err != nil {
		return report(StageFinalize, "Finalizing the run failed; the run is not stored.", err)
	}

	// Abandoned before storing: store nothing.
	if ctx.Err() != nil {
		return false
	}
	// The event is recorded at the finalization's own time, on the integration's clock.
	recordedAt := finalizedAt.Time()
	func() {
		defer recoverInto(&err)
		err = s.Store.InsertFinalized(ctx, c.identity.Authorization(), &finalized, c.identity.Principal, &recordedAt)
	}()
	if err != nil {
		return report(StageStore, "Storing the record failed; the run is not stored.", err)
	}
	// The embedding is derived data: written after the record's own commit, in the background, never failing it.
	c.startIndex(ctx, finalized.Record)
	// Stored. Told only while the invocation still waits: an abandoned work was reported as such.
	if ctx.Err() != nil {
		return false
	}
	if s.OnFinalized != nil {
		func() {
			defer func() { _ = recover() }()
			s.OnFinalized(finalized)
		}()
	}
	return true
}

func elapsed(start, end ae.Instant) ae.Duration {
	if d := end.Sub(start); d.Ticks() > 0 {
		return d
	}
	return ae.DurationFromTicks(0)
}

// ---- Capture: the agent middleware -------------------------------------------------------------------------------

// captureMiddleware captures each invocation of the agent as one run: it resolves the identity once (injection
// reuses it), opens the run, and when the invocation ends appends its attempt, verifies, finalizes and stores it.
type captureMiddleware struct {
	settings *settings
}

func (m *captureMiddleware) Run(next agent.RunFunc, ctx context.Context, messages []*message.Message,
	options ...agent.Option) iter.Seq2[*agent.ResponseUpdate, error] {
	return func(yield func(*agent.ResponseUpdate, error) bool) {
		s := m.settings
		running, _ := agent.AgentFromContext(ctx)
		session, _ := agent.GetOption(options, agent.WithSession)
		identity, err := s.resolveIdentity(ctx, IdentityContext{Agent: running, Session: session})
		scope := &captureScope{settings: s, agent: running, identity: identity, reported: map[CaptureStage]bool{}}
		if err != nil {
			scope.report(StageResolveIdentity, "Resolving the identity failed; the invocation runs with no memory.",
				err)
		}
		scope.start(ctx, messages)
		ctx = context.WithValue(ctx, scopeKey{}, scope)

		var response agent.Response
		var runErr error
		stopped, ended := false, false
		defer func() {
			if ended {
				return
			}
			// A panic through the invocation ends its run Failed, then goes on unchanged.
			if recovered := recover(); recovered != nil {
				failure := panicName(recovered)
				scope.finish(ctx, ae.RunFailed, nil, &failure)
				panic(recovered)
			}
		}()
		for update, err := range next(ctx, messages, options...) {
			if update != nil {
				response.Update(update)
			}
			if err != nil {
				runErr = err
			}
			if !yield(update, err) {
				stopped = true
				break
			}
		}
		ended = true

		switch {
		case runErr != nil && ctx.Err() != nil:
			// As .NET's StatusOf: the caller's cancellation ends the run Cancelled.
			failure := errorName(ctx.Err())
			scope.finish(ctx, ae.RunCancelled, nil, &failure)
		case runErr != nil:
			failure := errorName(runErr)
			scope.finish(ctx, ae.RunFailed, nil, &failure)
		case stopped:
			failure := StoppedError
			scope.finish(ctx, ae.RunCancelled, nil, &failure)
		default:
			response.Coalesce()
			var result *string
			if text := response.String(); text != "" {
				result = &text
			}
			scope.finish(ctx, ae.RunCompleted, result, nil)
		}
	}
}

// ---- Capture: the function invocation middleware -----------------------------------------------------------------

// toolMiddleware records each tool call of a captured invocation: its arguments, its result, and its error (the
// type name of an error it returned, the type of a panic, re-raised, or the class the tool-result classifier named
// for a call that returned).
func (s *settings) toolMiddleware(next func(context.Context, *agent.FunctionInvocationContext) (any, error),
	ctx context.Context, invocation *agent.FunctionInvocationContext) (any, error) {
	scope := scopeFrom(ctx)
	if scope == nil || scope.settings != s || !scope.captured() || invocation == nil || invocation.Function == nil {
		return next(ctx, invocation)
	}

	var order int
	var callID ae.UUID
	var name string
	var arguments *ae.Object
	var started ae.Instant
	var err error
	func() {
		defer recoverInto(&err)
		order = scope.beginCall()
		callID = s.NewID()
		name = invocation.Function.Name()
		var ok bool
		if arguments, ok = decodeArguments(invocation.Arguments); !ok {
			scope.report(StageToolCall, "A tool call's arguments are not a JSON object; it is recorded with none.", nil)
		}
		started = s.Clock()
	}()
	if err != nil {
		scope.report(StageToolCall, "Capturing a tool call's start failed; it is not recorded.", err)
		return next(ctx, invocation)
	}

	returned := false
	defer func() {
		if returned {
			return
		}
		if recovered := recover(); recovered != nil {
			failure := panicName(recovered)
			s.record(scope, order, callID, name, arguments, started, nil, &failure)
			panic(recovered)
		}
	}()
	result, callErr := next(ctx, invocation)
	returned = true
	if callErr != nil {
		failure := errorName(callErr)
		s.record(scope, order, callID, name, arguments, started, nil, &failure)
		return result, callErr
	}
	failure := s.classify(scope, callID, name, arguments, result)
	s.record(scope, order, callID, name, arguments, started, result, failure)
	return result, nil
}

func (s *settings) classify(scope *captureScope, callID ae.UUID, name string, arguments *ae.Object,
	result any) *string {
	if s.ClassifyToolResult == nil {
		return nil
	}
	var class string
	var failed bool
	var err error
	func() {
		defer recoverInto(&err)
		class, failed = s.ClassifyToolResult(ae.ToolResultContext{ToolCallID: callID, ToolName: name,
			Arguments: arguments, Result: classifierResult(result)})
	}()
	if err != nil {
		scope.report(StageToolResultClassification, "ClassifyToolResult panicked; the tool call is recorded as "+
			"returned.", err)
		return nil
	}
	if !failed {
		return nil
	}
	// Surrounding white space is forgiven; case and form are not ("exit 3", never "Exit 3" or "exit code 3").
	class = strings.TrimFunc(class, func(r rune) bool { return ae.IsNullOrWhiteSpace(string(r)) })
	if ae.IsSingleToken(class) {
		return &class
	}
	scope.report(StageToolResultClassification, "ClassifyToolResult named a class that is not a single error-class "+
		"token; the tool call is recorded as failed with "+ae.ToolReportedErrorClass+".", nil)
	reported := ae.ToolReportedErrorClass
	return &reported
}

func (s *settings) record(scope *captureScope, order int, callID ae.UUID, name string, arguments *ae.Object,
	started ae.Instant, result any, failure *string) {
	var err error
	func() {
		defer recoverInto(&err)
		ended := s.Clock()
		scope.addCall(order, ae.RawToolCall{ToolCallID: callID, ToolName: name, Arguments: arguments,
			StartedAt: started, Duration: elapsed(started, ended), Result: resultText(result), Error: failure})
	}()
	if err != nil {
		scope.report(StageToolCall, "Recording a tool call failed; it is not recorded.", err)
	}
}

// decodeArguments are a call's raw JSON arguments as an object; ok is false for arguments that are not one (they
// are then recorded as none, and reported).
func decodeArguments(arguments string) (*ae.Object, bool) {
	if strings.TrimSpace(arguments) == "" {
		return ae.NewObject(), true
	}
	decoded, err := ae.DecodeJSON([]byte(arguments))
	if object, ok := decoded.(*ae.Object); ok && err == nil {
		return object, true
	}
	return ae.NewObject(), false
}

// contentText is the text of framework content: a text item's text, or the text items of a list of content joined
// by a newline, as .NET reads a list of AIContent. ok is false for anything else, a content item that is not text
// and a list with no text item included: those are recorded as the JSON they are written as. A nil text item is no
// result.
func contentText(result any) (text string, isNil, ok bool) {
	var contents []message.Content
	switch value := result.(type) {
	case *message.TextContent:
		if value == nil {
			return "", true, true
		}
		return value.Text, false, true
	case message.Contents:
		contents = value
	case []message.Content:
		contents = value
	default:
		return "", false, false
	}
	var texts []string
	for _, content := range contents {
		if item, ok := content.(*message.TextContent); ok && item != nil {
			texts = append(texts, item.Text)
		}
	}
	if len(texts) == 0 {
		return "", false, false
	}
	return strings.Join(texts, "\n"), false, true
}

// classifierResult is what a classifier reads: the raw result, except that framework text content is its text, a
// *string its string, and a nil one nothing.
func classifierResult(result any) any {
	switch value := result.(type) {
	case *string:
		if value == nil {
			return nil
		}
		return *value
	}
	if text, isNil, ok := contentText(result); ok {
		if isNil {
			return nil
		}
		return text
	}
	return result
}

// resultText is a tool's result as capture records it: text as it is (a *string unwrapped, nil being no result),
// framework text content as its text, anything else as the JSON encoding/json writes for it.
func resultText(result any) *string {
	switch value := result.(type) {
	case nil:
		return nil
	case string:
		return &value
	case *string:
		if value == nil {
			return nil
		}
		text := *value
		return &text
	case json.RawMessage:
		text := string(value)
		return &text
	}
	if text, isNil, ok := contentText(result); ok {
		if isNil {
			return nil
		}
		return &text
	}
	data, err := json.Marshal(result)
	if err != nil {
		text := fmt.Sprint(result)
		return &text
	}
	text := string(data)
	return &text
}
