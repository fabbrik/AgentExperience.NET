package maf

import (
	"context"
	"errors"
	"math"
	"reflect"
	"slices"
	"sync"
	"sync/atomic"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"
)

// MeasuredMemoryOptions configures UseMeasuredMemory.
type MeasuredMemoryOptions struct {
	// Holdout is the rate and salt the provider's arms are drawn with; nil takes the injection holdout's
	// (Options.Holdout, off by default). The provider's key is mixed into the salt, so its arms are drawn independently
	// of the lessons' either way. An empty salt means ae.DefaultHoldoutSalt.
	Holdout *HoldoutOptions
	// OnRecorded is told how each assignment's background record ended (and of an inspection that failed). Called on a
	// background goroutine; a panic in it is recovered.
	OnRecorded func(ProviderHoldoutRecordResult)
}

// ProviderHoldoutRecordResult is how recording one run's arm in a memory provider's holdout ended (contract 1.11, the
// record_provider_holdout operation), with the outcomes of record_holdout. Assignment is nil when inspecting the
// provider's answer failed and nothing was assigned.
type ProviderHoldoutRecordResult struct {
	Outcome    InjectionRecordOutcome
	Assignment *ae.ProviderHoldoutAssignment
	Err        error
}

// MeasuredMemory puts another memory provider (Mem0, Zep, Letta, the host's own: any agent.ContextProvider) under the
// same holdout the lessons run under, so its benefit can be measured (contract 1.11; .NET's
// ExperienceHoldoutContextProvider): on a deterministic share of captured runs whose wrapped provider added something,
// what it added is withheld, and each such run's arm is recorded against the provider key. ae.EstimateProviderBenefit
// then compares the arms' verified outcomes.
//
//   - The wrapped provider runs exactly as without the wrapper: its Invoking is always called, in both arms, and so is
//     its Invoked. Its errors are returned unchanged, and nothing is recorded for an invocation whose Invoking failed.
//   - At rate 0 the wrapper is a pass-through: nothing is inspected, assigned or recorded, and no telemetry.
//   - Only an invocation captured by the same UseAgentExperience call (it has a run and the identity capture
//     resolved) whose wrapped provider added an instruction (non-empty, and not one it was handed), a message or a tool
//     it was not handed takes part. Every other invocation gets exactly what the wrapped provider returned.
//   - The arm is ae.AssignProviderHoldout of the run ID. Held out, the invocation gets the very messages and options
//     the wrapper was handed; in the treatment arm, what the wrapped provider returned. The wrapped provider is handed
//     clones of both slices, so the invocation's own are never changed.
//   - The assignment is recorded in a goroutine, within InjectionRecordTimeout, never delaying or failing the
//     invocation, as the record_provider_holdout operation and through OnRecorded. A panic while inspecting the
//     provider's answer returns that answer unchanged, records nothing, and is reported NotRecorded (Unexpected).
type MeasuredMemory struct {
	inner      agent.ContextProvider
	key        string
	holdout    HoldoutOptions
	settings   *settings
	onRecorded func(ProviderHoldoutRecordResult)

	mu        sync.Mutex
	running   int
	idle      chan struct{}
	abandoned atomic.Int64
}

var _ agent.ContextProvider = (*MeasuredMemory)(nil)

// UseMeasuredMemory wraps memory in a MeasuredMemory over the store, clock and InjectionRecordTimeout of the
// UseAgentExperience call already made on config (.NET's UseMeasuredMemory), puts it where memory is in
// config.ContextProviders (or appends it), and returns it. It is an error when UseAgentExperience was not called on
// config first, memory is nil, the key is not 1 to 100 characters of ASCII letters, digits, '.', '_' and '-', or the
// holdout rate is NaN or outside [0, 1]. On PostgreSQL the application role needs the grants of the contract's
// sql/application-role-privileges-1.11.sql; without them every invocation still runs (and withholds) and every
// assignment is reported NotRecorded.
func UseMeasuredMemory(config *agent.Config, memory agent.ContextProvider, providerKey string,
	options MeasuredMemoryOptions) (*MeasuredMemory, error) {
	if config == nil {
		return nil, errors.New("maf: the agent configuration is nil")
	}
	if memory == nil {
		return nil, errors.New("maf: the memory provider is nil")
	}
	if err := ae.ValidateProviderKey(providerKey); err != nil {
		return nil, err
	}
	var s *settings
	for _, middleware := range config.Middlewares {
		if capture, ok := middleware.(*captureMiddleware); ok {
			s = capture.settings
			break
		}
	}
	if s == nil {
		return nil, errors.New("maf: call UseAgentExperience on the configuration before UseMeasuredMemory")
	}
	holdout := s.Holdout
	if options.Holdout != nil {
		holdout = *options.Holdout
	}
	if math.IsNaN(holdout.Rate) || holdout.Rate < 0 || holdout.Rate > 1 {
		return nil, errors.New("maf: the holdout rate must be a number from 0 to 1")
	}
	if holdout.Salt == "" {
		holdout.Salt = ae.DefaultHoldoutSalt
	}
	wrapper := &MeasuredMemory{inner: memory, key: providerKey, holdout: holdout, settings: s,
		onRecorded: options.OnRecorded}
	for i, provider := range config.ContextProviders {
		if same(provider, memory) {
			config.ContextProviders[i] = wrapper
			return wrapper, nil
		}
	}
	config.ContextProviders = append(config.ContextProviders, wrapper)
	return wrapper, nil
}

// ProviderKey is the key the assignments are recorded against.
func (w *MeasuredMemory) ProviderKey() string { return w.key }

// Inner is the wrapped provider.
func (w *MeasuredMemory) Inner() agent.ContextProvider { return w.inner }

// Holdout is the rate and salt the arms are drawn with.
func (w *MeasuredMemory) Holdout() HoldoutOptions { return w.holdout }

// AbandonedRecords is how many writes that outlived their timeout are still running against the store.
func (w *MeasuredMemory) AbandonedRecords() int { return int(w.abandoned.Load()) }

// Close waits for every record the wrapper started to end, or for ctx to be done (and returns its error). Each is
// bounded by InjectionRecordTimeout. The wrapper stays usable.
func (w *MeasuredMemory) Close(ctx context.Context) error {
	w.mu.Lock()
	idle := w.idle
	if w.running == 0 {
		idle = nil
	}
	w.mu.Unlock()
	if idle == nil {
		return nil
	}
	select {
	case <-idle:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// Invoking calls the wrapped provider and, when the run takes part, decides its arm.
func (w *MeasuredMemory) Invoking(ctx context.Context, invoking agent.InvokingContext) ([]*message.Message,
	[]agent.Option, error) {
	if !(w.holdout.Rate > 0) {
		return w.inner.Invoking(ctx, invoking)
	}
	// The wrapped provider's own slices: the invocation's are never changed, whatever it does with them.
	messages, options, err := w.inner.Invoking(ctx, agent.InvokingContext{
		Messages: slices.Clone(invoking.Messages), Options: slices.Clone(invoking.Options)})
	if err != nil {
		return messages, options, err
	}
	assignment, identity, failure := w.decide(ctx, invoking, messages, options)
	if failure != nil {
		_, op := ae.Observe(ctx, "record_provider_holdout", false)
		op.DoneWithClass(string(InjectionNotRecorded), ae.ErrorClassUnexpected)
		w.report(ProviderHoldoutRecordResult{Outcome: InjectionNotRecorded, Err: failure})
		return messages, options, nil
	}
	if assignment == nil {
		return messages, options, nil
	}
	w.start(ctx, identity, *assignment)
	if assignment.Arm == ae.HoldoutWithheld {
		// Held out: exactly what the wrapper was handed, as if the provider were not there.
		return invoking.Messages, invoking.Options, nil
	}
	return messages, options, nil
}

// Invoked forwards to the wrapped provider, in both arms, so it keeps learning from every run.
func (w *MeasuredMemory) Invoked(ctx context.Context, invoked agent.InvokedContext) error {
	return w.inner.Invoked(ctx, invoked)
}

// decide is the arm of a run that takes part, nil for one that does not; a panic is the failure.
func (w *MeasuredMemory) decide(ctx context.Context, invoking agent.InvokingContext, messages []*message.Message,
	options []agent.Option) (assignment *ae.ProviderHoldoutAssignment, identity *ae.Identity, failure error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			assignment, identity, failure = nil, nil, panicError{recovered}
		}
	}()
	scope := scopeFrom(ctx)
	if scope == nil || scope.settings != w.settings || scope.identity == nil {
		return nil, nil, nil
	}
	runID, ok := scope.run()
	if !ok || !added(invoking, messages, options) {
		return nil, nil, nil
	}
	arm := ae.AssignProviderHoldout(runID, w.key, w.holdout.Salt, w.holdout.Rate)
	scoped := *scope.identity
	return &ae.ProviderHoldoutAssignment{RunID: runID, ProviderKey: w.key, Arm: arm, AssignedAt: w.settings.Clock()},
		&scoped, nil
}

// added is whether the wrapped provider added an instruction (non-empty, and more often than it was handed), or a
// message or tool it was not handed (compared by identity).
func added(invoking agent.InvokingContext, messages []*message.Message, options []agent.Option) bool {
	handed := map[string]int{}
	for instruction := range agent.AllOptions(invoking.Options, agent.WithInstructions) {
		handed[instruction]++
	}
	for instruction := range agent.AllOptions(options, agent.WithInstructions) {
		if instruction == "" {
			continue
		}
		if handed[instruction] == 0 {
			return true
		}
		handed[instruction]--
	}
	given := make(map[*message.Message]bool, len(invoking.Messages))
	for _, m := range invoking.Messages {
		given[m] = true
	}
	for _, m := range messages {
		if m != nil && !given[m] {
			return true
		}
	}
	for tool := range agent.AllOptions(options, agent.WithTool) {
		if tool == nil {
			continue
		}
		known := false
		for handedTool := range agent.AllOptions(invoking.Options, agent.WithTool) {
			if same(handedTool, tool) {
				known = true
				break
			}
		}
		if !known {
			return true
		}
	}
	return false
}

// same is identity for pointers and comparable values, deep equality for anything else; it never panics.
func same(a, b any) (equal bool) {
	defer func() {
		if recover() != nil {
			equal = reflect.DeepEqual(a, b)
		}
	}()
	return a == b
}

// start records the assignment in a goroutine, exactly as startHoldout records a lesson assignment.
func (w *MeasuredMemory) start(ctx context.Context, identity *ae.Identity, assignment ae.ProviderHoldoutAssignment) {
	result := ProviderHoldoutRecordResult{Outcome: InjectionRecorded, Assignment: &assignment}
	recorder, ok := w.settings.Store.(ae.ProviderHoldoutRecorder)
	if !ok {
		_, op := ae.Observe(ctx, "record_provider_holdout", false)
		op.Done(string(InjectionNotTracked))
		result.Outcome = InjectionNotTracked
		w.report(result)
		return
	}
	base := context.WithoutCancel(ctx)
	w.begin()
	go func() {
		defer w.end()
		_, op := ae.Observe(base, "record_provider_holdout", false)
		defer func() {
			if recovered := recover(); recovered != nil {
				result.Outcome, result.Err = InjectionNotRecorded, panicError{recovered}
			}
			op.DoneWithClass(string(result.Outcome), recordErrorClass(result.Outcome, result.Err))
			w.report(result)
		}()
		result.Outcome, result.Err = boundedWrite(base, w.settings.InjectionRecordTimeout, &w.abandoned, 1,
			func(ctx context.Context, _, _ int) error {
				return recorder.RecordProviderHoldoutAssignment(ctx, identity.Authorization(), identity.Scope, assignment)
			})
	}()
}

func (w *MeasuredMemory) begin() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.running == 0 {
		w.idle = make(chan struct{})
	}
	w.running++
}

func (w *MeasuredMemory) end() {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.running--
	if w.running == 0 {
		close(w.idle)
	}
}

func (w *MeasuredMemory) report(result ProviderHoldoutRecordResult) {
	if w.onRecorded == nil {
		return
	}
	defer func() { _ = recover() }()
	w.onRecorded(result)
}
