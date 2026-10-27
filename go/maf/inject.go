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
}

var _ agent.ContextProvider = (*ContextProvider)(nil)

// Invoking adds the block, when one applies. It never returns an error.
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
	return p.inject(ctx, messages, options), options, nil
}

// Invoked does nothing: capture is the agent middleware's.
func (p *ContextProvider) Invoked(context.Context, agent.InvokedContext) error { return nil }

func (p *ContextProvider) inject(ctx context.Context, messages []*message.Message,
	options []agent.Option) []*message.Message {
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
			return messages
		}
	}
	if identity == nil {
		op.Done("Skipped")
		return messages
	}

	taskText, ok, err := ae.DeriveTaskText(taskMessages(messages), ae.DefaultTaskTextMaxLength, ae.FollowUpThreshold)
	if err != nil {
		s.reportInjection(InjectionFailure{Reason: "Deriving the task text failed; no block was added.", Err: err})
		op.Done("Failed")
		return messages
	}
	if !ok {
		op.Done("NothingToInject")
		return messages
	}
	parameters := ae.NewSearchParameters(identity.Scope, taskText)
	parameters.ExcludeModelAuthored = s.ExcludeModelAuthored
	// Both channels run together; the vector channel never fails (it answers with a fallback instead), and is bounded
	// by VectorTimeout, so waiting for it after the text search is bounded too.
	type vectorAnswer struct {
		rows     []ae.SearchRow
		fallback *ae.VectorFallback
	}
	vectorDone := make(chan vectorAnswer, 1)
	if _, fallback := s.indexOf(); fallback != nil {
		vectorDone <- vectorAnswer{fallback: fallback}
	} else {
		go func() {
			vectorRows, fallback := s.vectorChannel(ctx, identity, taskText)
			vectorDone <- vectorAnswer{vectorRows, fallback}
		}()
	}
	retrieveCtx, retrieval := ae.Observe(ctx, "retrieve", false)
	defer retrieval.End()
	rows, err := s.Store.Search(retrieveCtx, identity.Authorization(), parameters)
	vector := <-vectorDone
	s.reportVectorFallback(vector.fallback)
	if err != nil {
		// Retrieval reports its failure as its outcome, as .NET does; it is not an error it raised.
		retrieval.Done("Failed")
		s.reportInjection(InjectionFailure{Reason: "The search failed; no block was added.", Err: err})
		op.Done("RetrievalFailed")
		return messages
	}
	// The instant the block is built at: ranking's "now", and the injection's instant.
	now := s.Clock()
	var ranked ae.RankedResult
	if len(vector.rows) > 0 {
		ranked, err = ae.RankHybrid(rows, vector.rows, identity.Scope, now, s.PreferredEnvironment,
			s.MinVectorRelevance, s.ExcludeModelAuthored)
	} else {
		ranked, err = ae.Rank(rows, identity.Scope, now, s.PreferredEnvironment, s.ExcludeModelAuthored)
	}
	if err != nil {
		retrieval.Fail(err)
		s.reportInjection(InjectionFailure{Reason: "Ranking failed; no block was added.", Err: err})
		op.Done("Failed")
		return messages
	}
	retrieval.Done("Completed")
	block, err := ae.RenderBlock(ranked.Ranked, s.BlockOptions)
	if err != nil {
		s.reportInjection(InjectionFailure{Reason: "Rendering the block failed; no block was added.", Err: err})
		op.Done("Failed")
		return messages
	}
	if op != nil {
		op.Set(ae.AttributeOmittedCount, len(block.Omitted))
	}
	if block.IsEmpty() {
		op.Done("NothingToInject")
		return messages
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
		s.reportWithheld(InjectionResult{Truncated: ranked.Truncated, VectorFallback: vector.fallback,
			WithheldExperienceIDs: append([]ae.UUID(nil), ids...)})
		p.startHoldout(ctx, identity, *assignment)
		op.Done("Withheld")
		return messages
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
	if s.OnInjected != nil {
		func() {
			defer func() { _ = recover() }()
			s.OnInjected(InjectionResult{ExperienceIDs: ids, ByteCount: block.ByteCount, Truncated: ranked.Truncated,
				VectorFallback: vector.fallback})
		}()
	}

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
	return out
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
