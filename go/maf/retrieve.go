package maf

import (
	"context"
	"errors"
	"fmt"
	"sync/atomic"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// vectorAnswer is what the vector channel answered: its rows, or why it contributed none. pastTimeout is whether the
// policy's Timeout had expired when it answered: a vector channel still running then times the call out.
type vectorAnswer struct {
	rows        []ae.SearchRow
	fallback    *ae.VectorFallback
	pastTimeout bool
}

// retrieval is a completed retrieval: the ranked result, the vector channel's answer, and the instant it ranked at.
type retrieval struct {
	ranked ae.RankedResult
	vector vectorAnswer
	now    ae.Instant
}

// query is what one invocation retrieves (contract 1.13): the task text, the preferred environment, the model-authored
// exclusion, and the limit ranking is cut to (0 for the policy's candidate limit).
type query struct {
	taskText  string
	preferred map[string]string
	exclude   bool
	limit     int
}

// textAnswer is what the text search answered.
type textAnswer struct {
	rows []ae.SearchRow
	err  error
}

// The states of one text search, for the abandoned-search count: still awaited, answered in time, or abandoned (and
// counted until it ends).
const (
	searchAwaited int32 = iota
	searchAnswered
	searchAbandoned
)

// retrieve is .NET's ExperienceRetrievalService.RetrieveAsync under the retrieval policy (contract 1.12): the
// authorization (Denied: nothing searched or embedded), the abandoned-search cap (TimedOut at once, the failure
// naming the cap), then both channels together under the policy's Timeout (TimedOut: no block, no partial result, no
// failure), and ranking under the policy and the confidence decay. It reports the retrieve operation and, when it ends
// the injection, the inject operation's outcome; ok is false then. err is set only for the caller's own cancellation,
// which propagates: both operations are reported as failed with it (Faulted, Cancelled), and nothing else is told.
func (p *ContextProvider) retrieve(ctx context.Context, op *ae.Operation, identity *ae.Identity,
	q query) (result retrieval, ok bool, err error) {
	taskText := q.taskText
	s := p.settings
	retrieveCtx, observed := ae.Observe(ctx, "retrieve", false)
	defer observed.End()
	stop := func(retrieveOutcome ae.RetrievalOutcome, injectOutcome string, failure *InjectionFailure) (retrieval,
		bool, error) {
		observed.Done(string(retrieveOutcome))
		if failure != nil {
			s.reportInjection(*failure)
		}
		s.nothing(op, injectOutcome)
		return retrieval{}, false, nil
	}
	cancelled := func() (retrieval, bool, error) {
		observed.Fail(ctx.Err())
		op.Fail(ctx.Err())
		return retrieval{}, false, ctx.Err()
	}

	authorization := identity.Authorization()
	if !authorization.Permits(identity.Scope) {
		// Fail-closed, before either channel is touched: nothing is searched or embedded.
		return stop(ae.RetrievalDenied, "RetrievalDenied", nil)
	}
	if limit := s.retrieval.MaxAbandonedSearches; p.searches.Load() >= int64(limit) {
		// A store that hangs on every call would otherwise pile one more abandoned search up per invocation.
		return stop(ae.RetrievalTimedOut, "RetrievalTimedOut", &InjectionFailure{Reason: fmt.Sprintf(
			"The candidate store has too many abandoned searches still running (at least %d), so no search was started.",
			limit)})
	}
	parameters, err := s.retrieval.SearchParameters(identity.Scope, taskText, q.exclude)
	if err != nil {
		observed.Fail(err)
		s.reportInjection(InjectionFailure{Reason: "The retrieval policy refused the search; no block was added.", Err: err})
		s.nothing(op, "Failed")
		return retrieval{}, false, nil
	}

	// The whole retrieval, both channels together, is bounded by the policy's Timeout.
	searchCtx, cancel := context.WithTimeout(retrieveCtx, s.retrieval.Timeout)
	defer cancel()
	policyExpired := func() bool { return errors.Is(searchCtx.Err(), context.DeadlineExceeded) && ctx.Err() == nil }

	// The vector channel never fails (it answers with a fallback instead). VectorTimeout bounds it on its own only when
	// it is strictly shorter than the policy's Timeout; otherwise the Timeout decides, and a vector channel still
	// running then times the call out.
	vectorDone := make(chan vectorAnswer, 1)
	if _, fallback := s.indexOf(); fallback != nil {
		vectorDone <- vectorAnswer{fallback: fallback}
	} else {
		go func() {
			rows, fallback := s.vectorChannel(searchCtx, identity, taskText, q.exclude, s.VectorTimeout < s.retrieval.Timeout)
			vectorDone <- vectorAnswer{rows, fallback, fallback != nil && policyExpired()}
		}()
	}

	// The text search runs in its own goroutine, so a store that ignores its context cannot hold the timeout off: it is
	// abandoned then, and counted until it ends.
	var state atomic.Int32
	textDone := make(chan textAnswer, 1)
	go func() {
		var answer textAnswer
		defer func() {
			if recovered := recover(); recovered != nil {
				answer = textAnswer{err: panicError{recovered}}
			}
			textDone <- answer
			if !state.CompareAndSwap(searchAwaited, searchAnswered) {
				p.searches.Add(-1)
			}
		}()
		answer.rows, answer.err = s.Store.Search(searchCtx, authorization, parameters)
	}()
	abandon := func() {
		if state.CompareAndSwap(searchAwaited, searchAbandoned) {
			p.searches.Add(1)
		}
		cancel()
	}

	// Both answers, or the Timeout (or the caller's cancellation), whichever comes first.
	var text *textAnswer
	var vector *vectorAnswer
	for text == nil || vector == nil {
		select {
		case answer := <-textDone:
			text = &answer
		case answer := <-vectorDone:
			vector = &answer
		case <-searchCtx.Done():
			if text == nil {
				abandon()
			}
			if ctx.Err() != nil {
				return cancelled()
			}
			// A real timeout: reported, never an error, and no failure.
			return stop(ae.RetrievalTimedOut, "RetrievalTimedOut", nil)
		}
	}
	if ctx.Err() != nil && text.err != nil {
		return cancelled()
	}
	if vector.pastTimeout || (text.err != nil && policyExpired()) {
		// The vector channel was still running at the Timeout, or the store failed at or after it: the same timeout.
		return stop(ae.RetrievalTimedOut, "RetrievalTimedOut", nil)
	}
	s.reportVectorFallback(vector.fallback)
	if text.err != nil {
		// Retrieval reports its failure as its outcome, as .NET does; it is not an error it raised.
		return stop(ae.RetrievalFailed, "RetrievalFailed",
			&InjectionFailure{Reason: "The search failed; no block was added.", Err: text.err})
	}
	// The instant the block is built at: ranking's "now", and the injection's instant.
	now := s.Clock()
	options := ae.RankOptions{ExcludeModelAuthored: q.exclude, Policy: s.retrieval, Decay: s.decay}
	var ranked ae.RankedResult
	if len(vector.rows) > 0 {
		ranked, err = ae.RankHybridWith(text.rows, vector.rows, identity.Scope, now, q.preferred, options)
	} else {
		ranked, err = ae.RankWith(text.rows, identity.Scope, now, q.preferred, options)
	}
	if err == nil && q.limit > 0 && len(ranked.Ranked) > q.limit {
		// As .NET's retrieval: the ranked records are cut to the request's own limit (the gated window, contract 1.13).
		ranked.Ranked = ranked.Ranked[:q.limit]
	}
	if err != nil {
		observed.Fail(err)
		s.reportInjection(InjectionFailure{Reason: "Ranking failed; no block was added.", Err: err})
		s.nothing(op, "Failed")
		return retrieval{}, false, nil
	}
	observed.Done(string(ae.RetrievalCompleted))
	return retrieval{ranked: ranked, vector: *vector, now: now}, true, nil
}
