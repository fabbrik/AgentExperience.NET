package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"reflect"
	"sort"
	"strings"
	"sync/atomic"
)

// The observer hook: the one seam through which the core and the Microsoft Agent Framework integration report what
// they do, for telemetry (docs/telemetry.md). It imports no OpenTelemetry. The module go/telemetry is an adapter that
// turns the hook into spans and metrics; any other Observer can be registered the same way.
//
// Until an Observer is registered, every call here is one atomic load and a nil check, and allocates nothing. An
// observer that panics is recovered and never changes what the caller gets.

// The sources and meters operations are reported under.
const (
	// SourceCore is the source of the core operations.
	SourceCore = "AgentExperience.Core"
	// SourceMAF is the source of inject, record_injection, record_holdout, record_provider_holdout and
	// benefit_refresh.
	SourceMAF = "AgentExperience.MicrosoftAgentFramework"
)

// OutcomeFaulted is the outcome of an operation that failed with an error.
const OutcomeFaulted = "Faulted"

// The four error.class values.
const (
	ErrorClassCancelled      = "Cancelled"
	ErrorClassTimeout        = "Timeout"
	ErrorClassInfrastructure = "Infrastructure"
	ErrorClassUnexpected     = "Unexpected"
)

// ErrorClasses are the four error.class values.
func ErrorClasses() []string {
	return []string{ErrorClassCancelled, ErrorClassTimeout, ErrorClassInfrastructure, ErrorClassUnexpected}
}

// OperationSpec is one operation this port performs: where it is reported, whether it has a span, and the outcomes it
// can reach.
type OperationSpec struct {
	Source   string
	Span     bool
	Outcomes []string
}

var observedOperations = map[string]OperationSpec{
	"capture.start_run": {SourceCore, true, []string{"Started"}},
	"capture.append_attempt": {SourceCore, true,
		[]string{"Recorded", "DuplicateNoOp", "Conflict", "SanitizationRejected", "CapacityExceeded"}},
	"capture.complete_run": {SourceCore, true, []string{"Recorded", "Conflict"}},
	"verify":               {SourceCore, true, []string{"Unknown", "Verified", "Failed"}},
	"reflect":              {SourceCore, true, []string{"Unknown", "Verified", "Failed"}},
	"finalize":             {SourceCore, true, []string{"Validated", "Quarantined"}},
	"retrieve":             {SourceCore, true, []string{"Completed", "TimedOut", "Denied", "Failed"}},
	"index": {SourceCore, true,
		[]string{"Indexed", "Stale", "Missing", "ProviderFailed", "IndexFailed"}},
	"inject": {SourceMAF, true,
		[]string{"Injected", "NothingToInject", "Skipped", "RetrievalTimedOut", "RetrievalDenied", "RetrievalFailed",
			"Failed", "Withheld"}},
	"record_injection": {SourceMAF, false, []string{"Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown"}},
	"record_holdout":   {SourceMAF, false, []string{"Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown"}},
	"record_provider_holdout": {SourceMAF, false,
		[]string{"Recorded", "NotTracked", "NotRecorded", "OutcomeUnknown"}},
	"benefit_refresh": {SourceMAF, false, []string{"Refreshed", "NotRefreshed", "OutcomeUnknown"}},
	// Contract 1.16's lifecycle: LifecycleService.Commit and ApplyEvidence.
	"lifecycle.commit": {SourceCore, true,
		[]string{"Committed", "TransitionNotAllowed", "ReplacementNotAllowed", "StaleRevision", "StatusMismatch",
			"Conflict", "NotFound", "Denied", "Invalid", "Deleted"}},
	"confidence.apply": {SourceCore, true,
		[]string{"Applied", "Ineligible", "StaleRevision", "StatusMismatch", "Conflict", "NotFound", "Denied",
			"Invalid", "Deleted", "Unverified"}},
	// Contract 1.17's reuse feedback: ReuseFeedbackService.Record.
	"reuse_feedback": {SourceCore, true, []string{"Recorded", "AlreadyRecorded", "Conflict", "Denied", "Invalid"}},
}

// ObservedOperations is every operation this port reports, by its operation value. The port reports no other: not
// retention.sweep, delete, grants, seal, backfill, deindex or reindex. A new map is returned each
// time.
func ObservedOperations() map[string]OperationSpec {
	result := make(map[string]OperationSpec, len(observedOperations))
	for name, spec := range observedOperations {
		spec.Outcomes = append([]string(nil), spec.Outcomes...)
		result[name] = spec
	}
	return result
}

// ObservedOperation is one operation of ObservedOperations.
func ObservedOperation(name string) (OperationSpec, bool) {
	spec, ok := observedOperations[name]
	spec.Outcomes = append([]string(nil), spec.Outcomes...)
	return spec, ok
}

// The span attributes this port sets beyond the common ones (agentexperience.operation, .outcome, .error.class and
// error.type, which the adapter writes).
const (
	AttributeRunID        = "agentexperience.run_id"
	AttributeAttemptID    = "agentexperience.attempt_id"
	AttributeExperienceID = "agentexperience.experience_id"
	AttributeReflectionID = "agentexperience.reflection_id"
	AttributeStage        = "agentexperience.stage"
	AttributeOmittedCount = "agentexperience.omitted_count"
	// AttributeCorrelationID is the host's correlation ID, on the inject operation when the request carries one
	// (contract 1.13).
	AttributeCorrelationID = "agentexperience.correlation_id"
	// AttributeScreeningRefusal is why screening refused a reflection, on the finalize operation (contract 1.14).
	AttributeScreeningRefusal = "agentexperience.reflection.screening_refusal"
	// AttributeEventID is the lifecycle event's ID, on lifecycle.commit and confidence.apply (contract 1.16).
	AttributeEventID = "agentexperience.event_id"
	// AttributeConfidenceAdmission is the admission applied evidence was stored with, on confidence.apply when it
	// was Applied and the update carries one.
	AttributeConfidenceAdmission = "agentexperience.confidence.admission"
	// AttributeIndependenceRefusal is why confidence.apply refused the evidence's identifiers (Unverified).
	AttributeIndependenceRefusal = "agentexperience.independence.refusal"
)

// ObservedAttributes are the span attributes this port sets beyond the common ones, sorted.
func ObservedAttributes() []string {
	attributes := []string{AttributeAttemptID, AttributeCorrelationID, AttributeExperienceID, AttributeOmittedCount,
		AttributeReflectionID, AttributeScreeningRefusal, AttributeRunID, AttributeStage, AttributeEventID,
		AttributeConfidenceAdmission, AttributeIndependenceRefusal, AttributeFeedbackID}
	sort.Strings(attributes)
	return attributes
}

// Attribute is one span attribute: a string, an int or a bool. Never record content, task text, scopes or error
// messages.
type Attribute struct {
	Key   string
	Value any
}

// Observation is what an operation ended as.
type Observation struct {
	Source    string
	Operation string
	// Outcome is the operation's own outcome, or OutcomeFaulted when it failed with an error.
	Outcome string
	// ErrorClass is set when the operation failed (and for a record_injection that failed without raising).
	ErrorClass string
	// ErrorType is the error's type name and nothing else from it; set only for a failed operation.
	ErrorType  string
	Attributes []Attribute
	// Nested reports whether another instrumented operation of the same source called this one.
	Nested bool
}

// Observer receives what the library does. Start runs when an operation begins and returns the context the operation
// runs in (so a span can parent the operations it calls) and a token handed back to Finish. Both may be called from
// any goroutine.
type Observer interface {
	Start(ctx context.Context, source, operation string, nested bool) (context.Context, any)
	Finish(token any, observation Observation)
}

type observerBox struct{ observer Observer }

var registered atomic.Pointer[observerBox]

// SetObserver registers the observer every operation reports to, or nil to stop reporting.
func SetObserver(observer Observer) {
	if observer == nil {
		registered.Store(nil)
		return
	}
	registered.Store(&observerBox{observer})
}

// CurrentObserver is the registered observer, or nil.
func CurrentObserver() Observer {
	if box := registered.Load(); box != nil {
		return box.observer
	}
	return nil
}

// Operation is one in-flight operation of a registered observer. A nil *Operation (what Observe answers when nothing
// is registered) accepts every method and does nothing.
type Operation struct {
	observer Observer
	token    any
	obs      Observation
	done     atomic.Bool
}

// Observe opens operation (a key of ObservedOperations) for the registered observer and returns the context it runs
// in. With no observer registered it returns ctx and nil, and allocates nothing. nested says another instrumented
// operation of the same source called this one; inject, record_injection, record_holdout, record_provider_holdout and
// benefit_refresh are never nested.
func Observe(ctx context.Context, operation string, nested bool) (context.Context, *Operation) {
	box := registered.Load()
	if box == nil {
		return ctx, nil
	}
	spec := observedOperations[operation]
	op := &Operation{observer: box.observer, obs: Observation{Source: spec.Source, Operation: operation, Nested: nested}}
	func() {
		defer func() { _ = recover() }()
		inner, token := box.observer.Start(ctx, spec.Source, operation, nested)
		if inner != nil {
			ctx = inner
		}
		op.token = token
	}()
	return ctx, op
}

// Set records a span attribute (a string, an int or a bool).
func (o *Operation) Set(key string, value any) {
	if o == nil {
		return
	}
	for i := range o.obs.Attributes {
		if o.obs.Attributes[i].Key == key {
			o.obs.Attributes[i].Value = value
			return
		}
	}
	o.obs.Attributes = append(o.obs.Attributes, Attribute{key, value})
}

// Done reports the outcome the operation returned. Only the first report counts.
func (o *Operation) Done(outcome string) { o.DoneWithClass(outcome, "") }

// DoneWithClass reports a returned outcome that also counts a failure (errorClass non-empty), for an operation that
// reports its failure as an outcome instead of an error (record_injection).
func (o *Operation) DoneWithClass(outcome, errorClass string) {
	if o == nil || !o.done.CompareAndSwap(false, true) {
		return
	}
	o.obs.Outcome, o.obs.ErrorClass = outcome, errorClass
	o.finish()
}

// Fail reports an operation that failed with err: outcome Faulted, the error's class and its type name only.
func (o *Operation) Fail(err error) {
	if o == nil || !o.done.CompareAndSwap(false, true) {
		return
	}
	o.obs.Outcome, o.obs.ErrorClass, o.obs.ErrorType = OutcomeFaulted, ClassifyError(err), errorTypeName(err)
	o.finish()
}

// End closes an operation that reported nothing (a panic unwinding through it, or an exit that forgot to report) as
// Faulted/Unexpected, as .NET closes an unreported injection. Defer it: it does nothing after Done or Fail.
func (o *Operation) End() {
	if o == nil || !o.done.CompareAndSwap(false, true) {
		return
	}
	o.obs.Outcome, o.obs.ErrorClass = OutcomeFaulted, ErrorClassUnexpected
	o.finish()
}

func (o *Operation) finish() {
	defer func() { _ = recover() }()
	o.observer.Finish(o.token, o.obs)
}

// ClassifyError is the error.class of a failed operation, as .NET classifies: the caller's own cancellation is
// Cancelled, a deadline or timeout is Timeout, a network, file-system or database-driver failure is Infrastructure,
// and anything else Unexpected.
func ClassifyError(err error) string {
	switch {
	case err == nil:
		return ErrorClassUnexpected
	case errors.Is(err, context.Canceled):
		return ErrorClassCancelled
	case errors.Is(err, context.DeadlineExceeded):
		return ErrorClassTimeout
	}
	var timeout interface{ Timeout() bool }
	if errors.As(err, &timeout) && timeout.Timeout() {
		return ErrorClassTimeout
	}
	var network net.Error
	var pathError *os.PathError
	var syscallError *os.SyscallError
	if errors.As(err, &network) || errors.As(err, &pathError) || errors.As(err, &syscallError) {
		return ErrorClassInfrastructure
	}
	for current := err; current != nil; current = errors.Unwrap(current) {
		// The PostgreSQL driver's errors, recognised by package so the core imports no driver.
		if name := reflect.TypeOf(current).String(); strings.HasPrefix(name, "*pgconn.") ||
			strings.HasPrefix(name, "pgconn.") || strings.HasPrefix(name, "*pgx.") {
			return ErrorClassInfrastructure
		}
	}
	return ErrorClassUnexpected
}

// errorTypeName is an error's type as package path and name, pointers dereferenced, never its message.
func errorTypeName(err error) string {
	if err == nil {
		return ""
	}
	t := reflect.TypeOf(err)
	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	switch {
	case t.Name() == "":
		return t.String()
	case t.PkgPath() == "":
		return t.Name()
	}
	return fmt.Sprintf("%s.%s", t.PkgPath(), t.Name())
}
