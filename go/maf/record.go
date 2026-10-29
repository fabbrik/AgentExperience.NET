package maf

import (
	"context"
	"errors"
	"time"

	"github.com/microsoft/agent-framework-go/agent"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// DefaultInjectionRecordTimeout is how long recording one injected block may take by default, all chunks together:
// .NET's.
const DefaultInjectionRecordTimeout = 5 * time.Second

// MaxAbandonedInjectionRecords is how many injection writes that outlived their timeout may still be running before
// another is not started: .NET's ExperienceInjectionLimits.MaxAbandonedReads default.
const MaxAbandonedInjectionRecords = 16

// InjectionRecordOutcome is how recording an injected block ended, as .NET reports its record_injection operation.
type InjectionRecordOutcome string

const (
	// InjectionRecorded is every chunk recorded.
	InjectionRecorded InjectionRecordOutcome = "Recorded"
	// InjectionNotTracked is a store that does not record injections (not an ae.InjectionRecorder).
	InjectionNotTracked InjectionRecordOutcome = "NotTracked"
	// InjectionNotRecorded is a store that refused or failed (a missing grant included), or too many earlier writes
	// still running.
	InjectionNotRecorded InjectionRecordOutcome = "NotRecorded"
	// InjectionOutcomeUnknown is a write that ran out of time or was cancelled; it may still commit.
	InjectionOutcomeUnknown InjectionRecordOutcome = "OutcomeUnknown"
)

// InjectionRecordResult is how recording one injected block ended: the outcome, the records the block delivered, and
// the error, when there was one (never record content).
type InjectionRecordResult struct {
	Outcome       InjectionRecordOutcome
	ExperienceIDs []ae.UUID
	Err           error
}

// errTooManyAbandoned is the error of a record not started because too many earlier writes are still running.
var errTooManyAbandoned = errors.New("too many earlier injection records are still running against the store")

// ContextProviderOf is the ContextProvider UseAgentExperience added to config (or one the configuration already
// held), or nil when it has none.
func ContextProviderOf(config *agent.Config) *ContextProvider {
	if config == nil {
		return nil
	}
	for _, provider := range config.ContextProviders {
		if p, ok := provider.(*ContextProvider); ok {
			return p
		}
	}
	return nil
}

// Close waits for every injection record the provider started, and every background embedding write and benefit
// refresh its configuration started, to end, or for ctx to be done, whichever is first,
// and returns ctx's error in the second case. Each record is bounded by InjectionRecordTimeout, so a host shutting
// down can call it with a context of a few seconds. A store write that overran that bound was abandoned when its
// record ended and is not awaited: it may still be running (AbandonedInjectionRecords counts them). Close may run
// concurrently with invocations, and the provider stays usable; a record started after Close began may or may not
// be awaited.
func (p *ContextProvider) Close(ctx context.Context) error {
	p.mu.Lock()
	idle := p.idle
	if p.running == 0 {
		idle = nil
	}
	p.mu.Unlock()
	if idle != nil {
		select {
		case <-idle:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	// The background embedding writes of the records stored (contract 1.6) too.
	if err := p.settings.index.wait(ctx); err != nil {
		return err
	}
	// And the benefit refreshes (contract 1.10).
	return p.WaitBenefitRefreshes(ctx)
}

// beginRecording counts a record started.
func (p *ContextProvider) beginRecording() {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.running == 0 {
		p.idle = make(chan struct{})
	}
	p.running++
}

// endRecording counts a record ended, waking Close when none is left.
func (p *ContextProvider) endRecording() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.running--
	if p.running == 0 {
		close(p.idle)
	}
}

// AbandonedInjectionRecords is how many injection writes that outlived their timeout are still running against the
// store.
func (p *ContextProvider) AbandonedInjectionRecords() int { return int(p.abandoned.Load()) }

// startRecording records, in a goroutine, that ids were injected at injectedAt in the identity's scope. It never
// blocks and never panics: the block is handed over and the model called whatever the store does.
func (p *ContextProvider) startRecording(ctx context.Context, identity *ae.Identity, ids []ae.UUID,
	injectedAt ae.Instant) {
	s := p.settings
	recorder, ok := s.Store.(ae.InjectionRecorder)
	if !ok {
		_, op := ae.Observe(ctx, "record_injection", false)
		op.Done(string(InjectionNotTracked))
		s.reportInjectionRecorded(InjectionRecordResult{Outcome: InjectionNotTracked, ExperienceIDs: ids})
		return
	}
	// The invocation's values, never its cancellation: the invocation ends long before the record may.
	base := context.WithoutCancel(ctx)
	scoped := *identity
	p.beginRecording()
	go func() {
		defer p.endRecording()
		p.record(base, recorder, &scoped, ids, injectedAt)
	}()
}

// record is the background record: the IDs in chunks of at most ae.MaxRecordInjectionIDs, all within
// InjectionRecordTimeout. Each call runs in its own goroutine and is awaited against the timeout rather than trusted
// to honour its context: one that overruns it is abandoned, counted until it ends, and while
// MaxAbandonedInjectionRecords are still running no new one starts. The outcome is reported once.
func (p *ContextProvider) record(ctx context.Context, recorder ae.InjectionRecorder, identity *ae.Identity,
	ids []ae.UUID, injectedAt ae.Instant) {
	s := p.settings
	result := InjectionRecordResult{Outcome: InjectionRecorded, ExperienceIDs: ids}
	_, op := ae.Observe(ctx, "record_injection", false)
	defer func() {
		if recovered := recover(); recovered != nil {
			result.Outcome, result.Err = InjectionNotRecorded, panicError{recovered}
		}
		op.DoneWithClass(string(result.Outcome), recordErrorClass(result.Outcome, result.Err))
		s.reportInjectionRecorded(result)
	}()
	result.Outcome, result.Err = p.writeChunks(ctx, len(ids), func(ctx context.Context, start, end int) error {
		return recorder.RecordInjection(ctx, identity.Authorization(), identity.Scope, ids[start:end], injectedAt)
	})
}

// writeChunks runs write over [0, count) in chunks of at most ae.MaxRecordInjectionIDs, all within
// InjectionRecordTimeout: the background write of record_injection and record_holdout. Each call runs in its own
// goroutine and is awaited against the timeout rather than trusted to honour its context: one that overruns it is
// abandoned, counted until it ends, and while MaxAbandonedInjectionRecords are still running no new one starts.
func (p *ContextProvider) writeChunks(ctx context.Context, count int,
	write func(ctx context.Context, start, end int) error) (InjectionRecordOutcome, error) {
	ctx, cancel := context.WithTimeout(ctx, p.settings.InjectionRecordTimeout)
	defer cancel()
	for start := 0; start < count; start += ae.MaxRecordInjectionIDs {
		if p.abandoned.Load() >= MaxAbandonedInjectionRecords {
			return InjectionNotRecorded, errTooManyAbandoned
		}
		end := min(count, start+ae.MaxRecordInjectionIDs)
		done := make(chan error, 1)
		go func() {
			defer func() {
				if recovered := recover(); recovered != nil {
					done <- panicError{recovered}
				}
			}()
			done <- write(ctx, start, end)
		}()
		select {
		case err := <-done:
			if err != nil {
				if ctx.Err() != nil {
					return InjectionOutcomeUnknown, err
				}
				return InjectionNotRecorded, err
			}
		case <-ctx.Done():
			p.abandoned.Add(1)
			go func() {
				<-done
				p.abandoned.Add(-1)
			}()
			return InjectionOutcomeUnknown, ctx.Err()
		}
	}
	return InjectionRecorded, nil
}

// HoldoutRecordResult is how recording one holdout assignment ended (contract 1.8), with the outcomes .NET reports
// for its record_holdout operation (InjectionRecorded, InjectionNotTracked, InjectionNotRecorded,
// InjectionOutcomeUnknown): the run, its arm, the records its block named, and the error, when there was one (never
// record content).
type HoldoutRecordResult struct {
	Outcome       InjectionRecordOutcome
	RunID         ae.UUID
	Arm           ae.HoldoutArm
	ExperienceIDs []ae.UUID
	Err           error
}

// startHoldout records, in a goroutine, a run's holdout assignment in the identity's scope, exactly as startRecording
// records an injection: it never blocks and never panics, and is reported as the record_holdout operation and through
// OnHoldoutRecorded.
func (p *ContextProvider) startHoldout(ctx context.Context, identity *ae.Identity, assignment ae.HoldoutAssignment) {
	s := p.settings
	assignment.Records = append([]ae.RunExposure(nil), assignment.Records...)
	ids := make([]ae.UUID, len(assignment.Records))
	for i, record := range assignment.Records {
		ids[i] = record.ExperienceID
	}
	result := HoldoutRecordResult{Outcome: InjectionRecorded, RunID: assignment.RunID, Arm: assignment.Arm,
		ExperienceIDs: ids}
	recorder, ok := s.Store.(ae.HoldoutRecorder)
	if !ok {
		_, op := ae.Observe(ctx, "record_holdout", false)
		op.Done(string(InjectionNotTracked))
		result.Outcome = InjectionNotTracked
		s.reportHoldoutRecorded(result)
		return
	}
	base := context.WithoutCancel(ctx)
	scoped := *identity
	p.beginRecording()
	go func() {
		defer p.endRecording()
		_, op := ae.Observe(base, "record_holdout", false)
		defer func() {
			if recovered := recover(); recovered != nil {
				result.Outcome, result.Err = InjectionNotRecorded, panicError{recovered}
			}
			op.DoneWithClass(string(result.Outcome), recordErrorClass(result.Outcome, result.Err))
			s.reportHoldoutRecorded(result)
		}()
		result.Outcome, result.Err = p.writeChunks(base, len(assignment.Records),
			func(ctx context.Context, start, end int) error {
				chunk := assignment
				chunk.Records = assignment.Records[start:end]
				return recorder.RecordHoldoutAssignment(ctx, scoped.Authorization(), scoped.Scope, chunk)
			})
	}()
}

// reportWithheld tells OnWithheld, recovering a panic in it.
func (s *settings) reportWithheld(result InjectionResult) {
	if s.OnWithheld == nil {
		return
	}
	defer func() { _ = recover() }()
	s.OnWithheld(result)
}

// reportInjected tells OnInjected, recovering a panic in it.
func (s *settings) reportInjected(result InjectionResult) {
	if s.OnInjected == nil {
		return
	}
	defer func() { _ = recover() }()
	s.OnInjected(result)
}

// reportHoldoutRecorded tells OnHoldoutRecorded, recovering a panic in it.
func (s *settings) reportHoldoutRecorded(result HoldoutRecordResult) {
	if s.OnHoldoutRecorded == nil {
		return
	}
	defer func() { _ = recover() }()
	s.OnHoldoutRecorded(result)
}

// recordErrorClass is the error.class a record_injection or record_holdout that failed counts, as .NET's table has it:
// a timeout or a cancellation is OutcomeUnknown (Timeout, Cancelled); too many earlier writes still running is
// NotRecorded (Timeout); a store that failed is Infrastructure, or Unexpected for anything else; a refusal is an answer
// and counts nothing.
func recordErrorClass(outcome InjectionRecordOutcome, err error) string {
	switch outcome {
	case InjectionRecorded, InjectionNotTracked:
		return ""
	case InjectionOutcomeUnknown:
		if errors.Is(err, context.Canceled) {
			return ae.ErrorClassCancelled
		}
		return ae.ErrorClassTimeout
	}
	switch {
	case err == nil || errors.Is(err, ae.ErrAuthorizationDenied):
		return ""
	case errors.Is(err, errTooManyAbandoned):
		return ae.ErrorClassTimeout
	}
	if _, panicked := err.(panicError); panicked {
		return ae.ErrorClassUnexpected
	}
	if class := ae.ClassifyError(err); class == ae.ErrorClassUnexpected {
		return class
	}
	return ae.ErrorClassInfrastructure
}

// reportInjectionRecorded tells OnInjectionRecorded, recovering a panic in it.
func (s *settings) reportInjectionRecorded(result InjectionRecordResult) {
	if s.OnInjectionRecorded == nil {
		return
	}
	defer func() { _ = recover() }()
	s.OnInjectionRecorded(result)
}
