package agentexperience

import (
	"context"
	"time"
)

// Store is what an agent integration needs of a store: write a finalized record with its initial lifecycle event,
// and run the text search. The PostgreSQL store (the postgres module) and the in-memory store (the inmemory module)
// both are one. Every call declares the host's authorization bounds, never a request's scope.
type Store interface {
	// InsertFinalized writes a finalized record and its initial lifecycle event; actor is the authorizing principal,
	// and recordedAt (now when nil) the time the event is recorded.
	InsertFinalized(ctx context.Context, bounds AuthorizationBounds, finalized *FinalizedExperience, actor string,
		recordedAt *time.Time) error
	// Search runs the contract's text search and returns its rows in order, for Rank.
	Search(ctx context.Context, bounds AuthorizationBounds, parameters SearchParameters) ([]SearchRow, error)
}
