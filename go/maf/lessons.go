package maf

import (
	"errors"

	"github.com/microsoft/agent-framework-go/agent"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// The lessons view (contract 1.7): a read-only list of what the library has learned in one scope, over the same store
// and with the same block options and model-authored flag the injection uses.

// ErrLessonsNotAvailable is a setup whose store cannot answer the lessons view (it is not an ae.LessonsReader; the
// PostgreSQL store and the in-memory store both are), or a configuration UseAgentExperience was not called on.
var ErrLessonsNotAvailable = errors.New("maf: the lessons view needs a store that is an ae.LessonsReader, " +
	"and a configuration UseAgentExperience was called on")

// NewLessons is the lessons view for the options' store, block options, model-authored flag, clock (ae.Now when nil),
// benefit policy (its window, minimum and per-model switch decide the measured benefit, contract 1.10) and retrieval
// policy (its MinimumConfidence and MaxAge decide eligibility, contract 1.12). Nothing else of the options is used,
// or validated. The store must be an ae.LessonsReader.
func NewLessons(options Options) (*ae.Lessons, error) {
	reader, ok := options.Store.(ae.LessonsReader)
	if !ok {
		return nil, ErrLessonsNotAvailable
	}
	return ae.NewLessons(reader, ae.LessonsOptions{BlockOptions: options.BlockOptions,
		ExcludeModelAuthored: options.ExcludeModelAuthored, Clock: options.Clock, Benefit: options.Benefit,
		RetrievalPolicy: options.RetrievalPolicy})
}

// Lessons is the lessons view over the provider's store, with its block options and model-authored flag. List a
// scope with Lessons.List(ctx, bounds, scope, query), or an identity's with Lessons.ListFor.
func (p *ContextProvider) Lessons() (*ae.Lessons, error) { return lessonsOf(p.settings) }

// LessonsOf is the lessons view of the setup UseAgentExperience made on config (injection disabled or not), over its
// store, with its block options and model-authored flag. ErrLessonsNotAvailable when config was not set up, or its
// store cannot answer the view.
func LessonsOf(config *agent.Config) (*ae.Lessons, error) {
	if config == nil {
		return nil, ErrLessonsNotAvailable
	}
	for _, middleware := range config.Middlewares {
		if capture, ok := middleware.(*captureMiddleware); ok {
			return lessonsOf(capture.settings)
		}
	}
	return nil, ErrLessonsNotAvailable
}

func lessonsOf(s *settings) (*ae.Lessons, error) { return NewLessons(s.Options) }
