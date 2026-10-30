package maf

import (
	"context"
	"errors"
	"fmt"
	"sync/atomic"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// vectorAnswer is what the vector channel answered: its rows, or why it contributed none.
type vectorAnswer struct {
	rows     []ae.SearchRow
	fallback *ae.VectorFallback
}

// retrieval is a completed retrieval: the ranked result, the vector channel's answer, and the instant it ranked at.
type retrieval struct {
	ranked ae.RankedResult
	vector vectorAnswer
	now    ae.Instant
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
// naming the cap), then both channels under the policy's Timeout (TimedOut: no block, no partial result, no failure),
// and ranking under the policy and the confidence decay. It reports the retrieve operation and, when it ends the
// injection, the inject operation's outcome; ok is false then.
func (p *ContextProvider) retrieve(ctx context.Context, op *ae.Operation, identity *ae.Identity,
	taskText string) (result retrieval, ok bool) {
	s := p.settings
	retrieveCtx, observed := ae.Observe(ctx, "retrieve", false)
	defer observed.End()
	stop := func(retrieveOutcome ae.RetrievalOutcome, injectOutcome string, failure *InjectionFailure) (retrieval, bool) {
		observed.Done(string(retrieveOutcome))
		if failure != nil {
			s.reportInjection(*failure)
		}
		op.Done(injectOutcome)
		return retrieval{}, false
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
	parameters, err := s.retrieval.SearchParameters(identity.Scope, taskText, s.ExcludeModelAuthored)
	if err != nil {
		observed.Fail(err)
		s.reportInjection(InjectionFailure{Reason: "The retrieval policy refused the search; no block was added.", Err: err})
		op.Done("Failed")
		return retrieval{}, false
	}

	// The whole retrieval, both channels, is bounded by the policy's Timeout.
	searchCtx, cancel := context.WithTimeout(retrieveCtx, s.retrieval.Timeout)
	defer cancel()

	// Both channels run together; the vector channel never fails (it answers with a fallback instead), and is bounded
	// by VectorTimeout and by the policy's Timeout, so waiting for it after the text search is bounded too.
	vectorDone := make(chan vectorAnswer, 1)
	if _, fallback := s.indexOf(); fallback != nil {
		vectorDone <- vectorAnswer{fallback: fallback}
	} else {
		go func() {
			rows, fallback := s.vectorChannel(searchCtx, identity, taskText)
			vectorDone <- vectorAnswer{rows, fallback}
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

	var text textAnswer
	select {
	case text = <-textDone:
	case <-searchCtx.Done():
		abandon()
		if ctx.Err() != nil {
			// The caller's own cancellation: reported as a failed search, as before the policy.
			observed.Done(string(ae.RetrievalFailed))
			s.reportInjection(InjectionFailure{Reason: "The search failed; no block was added.", Err: ctx.Err()})
			op.Done("RetrievalFailed")
			return retrieval{}, false
		}
		// A real timeout: reported, never an error, and no failure.
		return stop(ae.RetrievalTimedOut, "RetrievalTimedOut", nil)
	}
	vector := <-vectorDone
	if errors.Is(text.err, context.DeadlineExceeded) && ctx.Err() == nil && searchCtx.Err() != nil {
		// The store honoured the policy's deadline and answered with it: the same timeout.
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
	options := ae.RankOptions{ExcludeModelAuthored: s.ExcludeModelAuthored, Policy: s.retrieval, Decay: s.decay}
	var ranked ae.RankedResult
	if len(vector.rows) > 0 {
		ranked, err = ae.RankHybridWith(text.rows, vector.rows, identity.Scope, now, s.PreferredEnvironment, options)
	} else {
		ranked, err = ae.RankWith(text.rows, identity.Scope, now, s.PreferredEnvironment, options)
	}
	if err != nil {
		observed.Fail(err)
		s.reportInjection(InjectionFailure{Reason: "Ranking failed; no block was added.", Err: err})
		op.Done("Failed")
		return retrieval{}, false
	}
	observed.Done(string(ae.RetrievalCompleted))
	return retrieval{ranked: ranked, vector: vector, now: now}, true
}
