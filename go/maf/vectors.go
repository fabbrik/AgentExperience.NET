package maf

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/microsoft/agent-framework-go/agent"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// Semantic retrieval (contract 1.6): the vector channel of an injection, and the background embedding write after a
// record is stored. Nothing here fails the invocation or the capture.

// DefaultVectorTimeout is how long the vector channel of one injection (embedding the task text, then searching the
// stored vectors) may take by default; past it the injection answers from text alone.
const DefaultVectorTimeout = 500 * time.Millisecond

// MaxVectorCalls is how many embedder or vector-store calls may be in flight at once, per UseAgentExperience call,
// abandoned (timed-out) ones included: a goroutine cannot be stopped, so a provider that never returns holds its slot.
// Past it a call fails at once (ProviderUnavailable, VectorSearchFailed), so a hung provider can never starve the text
// search or pile up goroutines.
const MaxVectorCalls = 16

// errTooManyVectorCalls is the error of a call not started because MaxVectorCalls are already in flight.
var errTooManyVectorCalls = errors.New("too many earlier embedding or vector-search calls are still running")

// callBounded runs fn in a goroutine of its own and waits for it, or for ctx to be done, whichever is first. It holds
// one of slots until fn returns (even when it was abandoned), and fails at once, running nothing, when none is free.
// A panic in fn is an error.
func callBounded[T any](ctx context.Context, slots chan struct{}, fn func(context.Context) (T, error)) (T, error) {
	var zero T
	select {
	case slots <- struct{}{}:
	default:
		return zero, errTooManyVectorCalls
	}
	type outcome struct {
		value T
		err   error
	}
	done := make(chan outcome, 1)
	go func() {
		defer func() { <-slots }()
		defer func() {
			if recovered := recover(); recovered != nil {
				done <- outcome{err: panicError{recovered}}
			}
		}()
		value, err := fn(ctx)
		done <- outcome{value, err}
	}()
	select {
	case result := <-done:
		return result.value, result.err
	case <-ctx.Done():
		select {
		case result := <-done:
			return result.value, result.err
		default:
		}
		return zero, ctx.Err()
	}
}

// tracker counts background work started and not yet ended, so a host shutting down can wait for it.
type tracker struct {
	mu      sync.Mutex
	running int
	idle    chan struct{}
}

func (t *tracker) begin() {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.running == 0 {
		t.idle = make(chan struct{})
	}
	t.running++
}

func (t *tracker) end() {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.running--
	if t.running == 0 {
		close(t.idle)
	}
}

// wait returns when nothing is running, or with ctx's error when it is done first.
func (t *tracker) wait(ctx context.Context) error {
	t.mu.Lock()
	if t.running == 0 {
		t.mu.Unlock()
		return nil
	}
	idle := t.idle
	t.mu.Unlock()
	select {
	case <-idle:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// WaitForIndexWrites waits for every background embedding write the configuration's agent started (after a record was
// stored) to end, or for ctx to be done, whichever is first, and returns ctx's error in the second case. Each write is
// bounded by FinalizationTimeout. It does nothing for a configuration UseAgentExperience was not called on.
func WaitForIndexWrites(ctx context.Context, config *agent.Config) error {
	if config == nil {
		return nil
	}
	for _, middleware := range config.Middlewares {
		if capture, ok := middleware.(*captureMiddleware); ok {
			return capture.settings.index.wait(ctx)
		}
	}
	return nil
}

func (s *settings) reportVectorFallback(fallback *ae.VectorFallback) {
	if fallback == nil || s.OnVectorFallback == nil {
		return
	}
	defer func() { _ = recover() }()
	s.OnVectorFallback(*fallback)
}

// embedded is a text's vector, and the model that made it.
type embedded struct {
	modelID string
	vector  []float32
}

// embed asks the embedder for text's vector and checks what it answered: a non-blank model ID, and a vector of
// exactly its dimension and finite. It runs inside a bounded call.
func (s *settings) embed(ctx context.Context, text string) (embedded, error) {
	modelID, dimension := s.Embedder.ModelID(), s.Embedder.Dimension()
	if err := ae.ValidateModelID(modelID); err != nil {
		return embedded{}, err
	}
	if dimension < 1 || dimension > ae.MaxDimension {
		return embedded{}, fmt.Errorf("the embedder declares a dimension outside 1 to %d", ae.MaxDimension)
	}
	vector, err := s.Embedder.Embed(ctx, text)
	if err != nil {
		return embedded{}, err
	}
	if err := ae.ValidateVector(vector, dimension); err != nil {
		return embedded{}, err
	}
	return embedded{modelID, vector}, nil
}

// indexOf is the store's embedding index, when it is one and an embedder is registered.
func (s *settings) indexOf() (ae.EmbeddingIndex, *ae.VectorFallback) {
	if s.Embedder == nil {
		return nil, &ae.VectorFallback{Reason: ae.FallbackNotConfigured,
			Detail: "No embedder is registered, so only text retrieval ran."}
	}
	index, ok := s.Store.(ae.EmbeddingIndex)
	if !ok {
		return nil, &ae.VectorFallback{Reason: ae.FallbackNotConfigured,
			Detail: "The store does not serve vector search, so only text retrieval ran."}
	}
	return index, nil
}

// vectorChannel is the vector channel (contract 1.6): embed the task text, search the stored vectors, both within
// VectorTimeout. It never panics and never fails the invocation: whatever goes wrong is the fallback it answers with
// (and no rows), and the fallback's text never quotes record or vector content.
func (s *settings) vectorChannel(ctx context.Context, identity *ae.Identity, taskText string) (rows []ae.SearchRow,
	fallback *ae.VectorFallback) {
	defer func() {
		if recovered := recover(); recovered != nil {
			rows, fallback = nil, &ae.VectorFallback{Reason: ae.FallbackVectorSearchFailed,
				Detail: "The vector channel failed unexpectedly.", Err: panicError{recovered}}
		}
	}()
	index, fallback := s.indexOf()
	if fallback != nil {
		return nil, fallback
	}
	ctx, cancel := context.WithTimeout(ctx, s.VectorTimeout)
	defer cancel()

	query, err := callBounded(ctx, s.vectorSlots, func(ctx context.Context) (ae.VectorQuery, error) {
		e, err := s.embed(ctx, taskText)
		if err != nil {
			return ae.VectorQuery{}, err
		}
		query := ae.VectorQuery{Scope: identity.Scope, ModelID: e.modelID, Vector: e.vector,
			ExcludeModelAuthored: s.ExcludeModelAuthored}
		return query, query.Validate()
	})
	if err != nil {
		if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, context.Canceled) {
			return nil, &ae.VectorFallback{Reason: ae.FallbackProviderUnavailable,
				Detail: "The embedder did not answer in time.", Err: err}
		}
		return nil, &ae.VectorFallback{Reason: ae.FallbackProviderUnavailable,
			Detail: fmt.Sprintf("The embedder failed or returned an unusable vector (%s).", errorName(err)), Err: err}
	}

	result, err := callBounded(ctx, s.vectorSlots, func(ctx context.Context) (ae.VectorSearchResult, error) {
		return index.SearchVectors(ctx, identity.Authorization(), query)
	})
	if err != nil {
		if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, context.Canceled) {
			return nil, &ae.VectorFallback{Reason: ae.FallbackVectorSearchFailed,
				Detail: "The vector search did not answer in time.", Err: err}
		}
		return nil, &ae.VectorFallback{Reason: ae.FallbackVectorSearchFailed,
			Detail: fmt.Sprintf("The vector search failed (%s).", errorName(err)), Err: err}
	}
	switch result.Outcome {
	case ae.VectorFound:
	case ae.VectorModelMismatch:
		return nil, &ae.VectorFallback{Reason: ae.FallbackModelMismatch,
			Detail: "Every stored embedding in this scope is of another model."}
	case ae.VectorDimensionMismatch:
		return nil, &ae.VectorFallback{Reason: ae.FallbackDimensionMismatch,
			Detail: "Every stored embedding in this scope is of another width."}
	default:
		return nil, &ae.VectorFallback{Reason: ae.FallbackVectorSearchFailed,
			Detail: "The vector search answered with an unknown outcome."}
	}
	rows = make([]ae.SearchRow, len(result.Hits))
	for i, hit := range result.Hits {
		rows[i] = ae.SearchRow{Record: hit.Record, Relevance: hit.Relevance()}
	}
	return rows, nil
}

// startIndex starts embedding a stored record's summary in the background: derived data, written after the record's
// own commit, never inside it, and never failing it. It never blocks and never panics.
func (c *captureScope) startIndex(ctx context.Context, record ae.ExperienceRecord) {
	s := c.settings
	index, fallback := s.indexOf()
	if fallback != nil {
		return
	}
	// The invocation's values, never its cancellation: the invocation ends long before the write may.
	base := context.WithoutCancel(ctx)
	s.index.begin()
	go func() {
		defer s.index.end()
		c.writeEmbedding(base, index, &record)
	}()
}

// writeEmbedding embeds record's summary and stores the vector, all within FinalizationTimeout. A failure is reported
// as StageIndex (once per run); a Stale or Missing write is not one: the record moved on, or is gone.
func (c *captureScope) writeEmbedding(ctx context.Context, index ae.EmbeddingIndex, record *ae.ExperienceRecord) {
	s := c.settings
	defer func() {
		if recovered := recover(); recovered != nil {
			c.report(StageIndex, "Embedding the record panicked; its text retrieval is unaffected.",
				panicError{recovered})
		}
	}()
	ctx, cancel := context.WithTimeout(ctx, s.FinalizationTimeout)
	defer cancel()
	fail := func(err error) {
		if errors.Is(err, context.DeadlineExceeded) {
			c.report(StageIndex, fmt.Sprintf("Embedding the record did not finish within %v.", s.FinalizationTimeout), err)
			return
		}
		c.report(StageIndex, fmt.Sprintf("Embedding the record failed (%s); its text retrieval is unaffected.",
			errorName(err)), err)
	}
	summary := ae.RecordSummary(record)
	e, err := callBounded(ctx, s.vectorSlots, func(ctx context.Context) (embedded, error) {
		return s.embed(ctx, summary)
	})
	if err != nil {
		fail(err)
		return
	}
	write := ae.EmbeddingWrite{ExperienceID: record.ExperienceID, Scope: record.Scope, ModelID: e.modelID,
		Dimension: len(e.vector), ContentHash: ae.ContentHash(e.modelID, summary), SourceRevision: record.Revision,
		Vector: e.vector, Now: s.Clock()}
	_, err = callBounded(ctx, s.vectorSlots, func(ctx context.Context) (ae.EmbeddingWriteResult, error) {
		return index.WriteEmbedding(ctx, c.identity.Authorization(), write)
	})
	if err != nil {
		fail(err)
	}
}
