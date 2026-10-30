package maf

import (
	"context"
	"sync"
	"sync/atomic"

	"github.com/microsoft/agent-framework-go/agent"
	"github.com/microsoft/agent-framework-go/message"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ContextProvider injects the lessons that apply to an invocation, before the model is called: the task text from
// the invocation's messages, the text search in the identity's scope, the contract's ranking, and the rendered block
// as one message just before the new input. It never returns an error or panics into the agent; a failure means no
// block and is reported through OnInjectionFailure.
//
// The messages it sees are the agent's history (a history provider always runs before the context providers in the
// Go SDK) and then the new input; history counts as replayed history for the task text, so a short follow-up ("and
// retry") is searched joined after the previous request, as .NET's ChatClientAgent-level provider does. A block a
// history provider replays (the SDK persists context messages with the request) is dropped, so each request carries
// exactly one block, and the block never counts as a request.
//
// Once a block is handed over, the records it delivered are recorded as injected (contract 1.4) in a goroutine;
// Close awaits the ones still running.
type ContextProvider struct {
	settings *settings
	// mu guards running and idle: the records started and not yet ended, and a channel closed when that count
	// reaches zero (a fresh one each time it leaves zero), so Close may run concurrently with invocations.
	mu        sync.Mutex
	running   int
	idle      chan struct{}
	abandoned atomic.Int64
	// searches counts the text searches retrieval abandoned (past the policy's Timeout, or on the caller's
	// cancellation) that are still running against the store: at the policy's MaxAbandonedSearches no new one starts.
	searches atomic.Int64
}

var _ agent.ContextProvider = (*ContextProvider)(nil)

// Invoking adds the block, when one applies. It returns no error but the caller's own cancellation during retrieval
// (ctx.Err(), context.Canceled), which propagates as .NET's OperationCanceledException does; every failure is reported
// and leaves the invocation without a block.
func (p *ContextProvider) Invoking(ctx context.Context, invoking agent.InvokingContext) (messages []*message.Message,
	options []agent.Option, err error) {
	messages, options = withoutBlocks(invoking.Messages), invoking.Options
	defer func() {
		if recovered := recover(); recovered != nil {
			p.settings.reportInjection(InjectionFailure{Reason: "Injection panicked; no block was added.",
				Err: panicError{recovered}})
			messages, options, err = withoutBlocks(invoking.Messages), invoking.Options, nil
		}
	}()
	messages, err = p.inject(ctx, messages, options)
	return messages, options, err
}

// Invoked does nothing: capture is the agent middleware's.
func (p *ContextProvider) Invoked(context.Context, agent.InvokedContext) error { return nil }

func (p *ContextProvider) inject(ctx context.Context, messages []*message.Message,
	options []agent.Option) ([]*message.Message, error) {
	ctx, op := ae.Observe(ctx, "inject", false)
	defer op.End()
	s := p.settings
	scope := scopeFrom(ctx)
	captured := scope != nil && scope.settings == s
	var identity *ae.Identity
	if captured {
		// Capture resolved the identity for this invocation already: reuse it, never resolve twice.
		identity = scope.identity
	} else {
		running, _ := agent.AgentFromContext(ctx)
		session, _ := agent.GetOption(options, agent.WithSession)
		var err error
		identity, err = s.resolveIdentity(ctx, IdentityContext{Agent: running, Session: session})
		if err != nil {
			s.reportInjection(InjectionFailure{Reason: "Resolving the identity failed; no block was added.", Err: err})
			op.Done("Failed")
			return messages, nil
		}
	}
	if identity == nil {
		op.Done("Skipped")
		return messages, nil
	}

	taskText, ok, err := ae.DeriveTaskText(taskMessages(messages), ae.DefaultTaskTextMaxLength, ae.FollowUpThreshold)
	if err != nil {
		s.reportInjection(InjectionFailure{Reason: "Deriving the task text failed; no block was added.", Err: err})
		op.Done("Failed")
		return messages, nil
	}
	if !ok {
		s.reportInjected(InjectionResult{Outcome: "NothingToInject"})
		op.Done("NothingToInject")
		return messages, nil
	}
	retrieved, ok, err := p.retrieve(ctx, op, identity, taskText)
	if !ok {
		// err is only ever the caller's own cancellation, which propagates (as .NET's does).
		return messages, err
	}
	ranked, vector, now := retrieved.ranked, retrieved.vector, retrieved.now
	// Contract 1.10: acting on measured benefit (off by default), from the cached view, which never waits on the store.
	var view *ae.BenefitView
	if s.benefit != nil {
		view = s.benefit.current(ctx, identity, s.invokingModel(captured))
	}
	selected, omitted := ae.SelectCandidates(ranked.Ranked, view, ae.SelectOptions{
		MaxRecords: s.BlockOptions.MaxRecords, ExcludeModelAuthored: s.ExcludeModelAuthored,
		PreferMeasuredHelpful: s.Benefit.PreferMeasuredHelpful})
	block, err := ae.RenderBlock(selected, s.BlockOptions)
	if err != nil {
		s.reportInjection(InjectionFailure{Reason: "Rendering the block failed; no block was added.", Err: err})
		op.Done("Failed")
		return messages, nil
	}
	omitted = append(omitted, ae.ByteBudgetOmissions(block, s.BlockOptions)...)
	if op != nil {
		op.Set(ae.AttributeOmittedCount, len(omitted))
	}
	if block.IsEmpty() {
		// As .NET reports it: nothing injected, and every omission (a scope measured Hurts omits them all).
		s.reportInjected(InjectionResult{Outcome: "NothingToInject", Truncated: ranked.Truncated,
			VectorFallback: vector.fallback, Omitted: omitted, Excluded: ranked.Excluded})
		op.Done("NothingToInject")
		return messages, nil
	}

	// The records this block delivers, at the revision each was rendered from.
	ids := make([]ae.UUID, len(block.Included))
	exposures := make([]ae.RunExposure, len(block.Included))
	for i, record := range block.Included {
		ids[i] = record.ExperienceID
		exposures[i] = ae.RunExposure{ExperienceID: record.ExperienceID, Revision: record.Revision}
	}

	// Contract 1.8: the injection holdout. Decided once the block is built, so what a held-out run is denied is exactly
	// what it would have been given, and before anything is handed over. Only a run this agent's own capture captures
	// takes part, and only with records to deliver; everything else is injected as it always was, and records nothing.
	var assignment *ae.HoldoutAssignment
	if s.Holdout.Rate > 0 && len(exposures) > 0 && captured {
		if runID, ok := scope.run(); ok {
			assignment = &ae.HoldoutAssignment{RunID: runID, Arm: ae.AssignHoldout(runID, s.Holdout.Salt, s.Holdout.Rate),
				Records: exposures, AssignedAt: now}
		}
	}
	if assignment != nil && assignment.Arm == ae.HoldoutWithheld {
		s.reportWithheld(InjectionResult{Outcome: "Withheld", Truncated: ranked.Truncated, VectorFallback: vector.fallback,
			Excluded:              ranked.Excluded,
			WithheldExperienceIDs: append([]ae.UUID(nil), ids...), Omitted: omitted})
		p.startHoldout(ctx, identity, *assignment)
		op.Done("Withheld")
		return messages, nil
	}
	injected := &message.Message{
		Role:                 s.MessageRole,
		Contents:             message.Contents{&message.TextContent{Text: block.Text}},
		AdditionalProperties: map[string]any{HistoricalReferenceKey: true},
		Source:               message.Source{Type: agent.SourceTypeContextProvider, ID: SourceID},
	}
	at := inputStart(messages)
	out := make([]*message.Message, 0, len(messages)+1)
	out = append(out, messages[:at]...)
	out = append(out, injected)
	out = append(out, messages[at:]...)

	// The records this block delivers, on the run the invocation is captured as. Recorded last, once nothing that could
	// fail remains.
	if captured {
		scope.recordExposures(exposures)
	}
	s.reportInjected(InjectionResult{Outcome: "Injected", ExperienceIDs: ids, ByteCount: block.ByteCount,
		Truncated: ranked.Truncated, VectorFallback: vector.fallback, Omitted: omitted, Excluded: ranked.Excluded})

	// When each delivered record was injected, for the .NET lessons view: started last, in the background, so the
	// block is handed over and the model called without waiting for the store.
	if len(ids) > 0 {
		p.startRecording(ctx, identity, append([]ae.UUID(nil), ids...), now)
	}
	// Contract 1.8: the treatment arm's assignment, recorded the same way.
	if assignment != nil {
		p.startHoldout(ctx, identity, *assignment)
	}
	op.Done("Injected")
	return out, nil
}

// isBlock reports whether a message is stamped as carrying an injected block.
func isBlock(m *message.Message) bool {
	if m == nil {
		return false
	}
	stamped, _ := m.AdditionalProperties[HistoricalReferenceKey].(bool)
	return stamped
}

// withoutBlocks is the messages less any block a history provider replayed.
func withoutBlocks(messages []*message.Message) []*message.Message {
	for i, m := range messages {
		if !isBlock(m) {
			continue
		}
		kept := make([]*message.Message, 0, len(messages)-1)
		kept = append(kept, messages[:i]...)
		for _, rest := range messages[i+1:] {
			if !isBlock(rest) {
				kept = append(kept, rest)
			}
		}
		return kept
	}
	return messages
}

// inputStart is where the new input begins: the first message from outside the agent pipeline after the last
// replayed history message. The block goes just before it, after the history, as the .NET and Python integrations
// place their context before the request. With no such message (the input was all added by middleware or an earlier
// provider), it goes right after the history: never after the input.
func inputStart(messages []*message.Message) int {
	start := 0
	for i, m := range messages {
		if m != nil && m.Source.Type == agent.SourceTypeHistoryProvider {
			start = i + 1
		}
	}
	for i := start; i < len(messages); i++ {
		if m := messages[i]; m != nil && m.Source.Type == message.SourceTypeExternal {
			return i
		}
	}
	return start
}

// taskMessages are an invocation's messages for the task-text rule: replayed history marked ChatHistory, the new
// input External, and anything else by the type of its source.
func taskMessages(messages []*message.Message) []ae.Message {
	result := make([]ae.Message, 0, len(messages))
	for _, m := range messages {
		if m == nil {
			continue
		}
		source := string(m.Source.Type)
		switch m.Source.Type {
		case message.SourceTypeExternal:
			source = ae.SourceExternal
		case agent.SourceTypeHistoryProvider:
			source = ae.SourceChatHistory
		}
		parts := make([]ae.MessagePart, 0, len(m.Contents))
		for _, content := range m.Contents {
			if text, ok := content.(*message.TextContent); ok && text != nil {
				value := text.Text
				parts = append(parts, ae.MessagePart{Type: "text", Text: &value})
			} else {
				parts = append(parts, ae.MessagePart{Type: "other"})
			}
		}
		result = append(result, ae.Message{Role: string(m.Role), Contents: parts, Source: source,
			CarriesBlock: isBlock(m)})
	}
	return result
}
