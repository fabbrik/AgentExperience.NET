package agentexperience

import (
	"context"
	"errors"
	"fmt"
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

// InjectionRecorder is a Store that records when each record was injected (contract 1.4), for the .NET lessons
// view's "last injected". Optional: the agent integration records an injection only through a store that is one, and
// reports NotTracked for one that is not. The PostgreSQL store and the in-memory store both are.
type InjectionRecorder interface {
	// RecordInjection records that experienceIDs (at most MaxRecordInjectionIDs) were injected in scope at
	// injectedAt, within bounds: only a record live in exactly scope counts; the instant, truncated to whole
	// microseconds, is clamped to the store's clock and only moves forward; the count rises by one and saturates.
	RecordInjection(ctx context.Context, bounds AuthorizationBounds, scope Scope, experienceIDs []UUID,
		injectedAt Instant) error
}

// EmbeddingIndex is a Store that keeps embeddings and searches them by cosine distance (contract 1.6), for semantic
// retrieval. Optional: the agent integration runs the vector channel only through a store that is one, and reports
// NotConfigured for one that is not. The PostgreSQL store (with pgvector) and the in-memory store both are.
type EmbeddingIndex interface {
	// WriteEmbedding stores a record's embedding, only for a record live in exactly the write's scope and still at
	// exactly its source revision: Written; Stale (with the record's revision) when it moved on, or a newer
	// embedding is stored; Missing for an unknown, other-scope or erased record. A write that fails Validate is an
	// error and runs nothing.
	WriteEmbedding(ctx context.Context, bounds AuthorizationBounds, write EmbeddingWrite) (EmbeddingWriteResult, error)
	// SearchVectors runs the nearest-neighbour search and returns its hits nearest first (ties by experience ID,
	// NaN last). A query that fails Validate is an error and runs nothing.
	SearchVectors(ctx context.Context, bounds AuthorizationBounds, query VectorQuery) (VectorSearchResult, error)
}

// MaxRecordInjectionIDs is the most records one RecordInjection call names: the .NET store's limit
// (ExperienceRecordGetManyResult.MaxCount). The agent integration splits a longer list into chunks.
const MaxRecordInjectionIDs = 200

// MaxInjectionCount is the count an injection row saturates at: PostgreSQL's bigint maximum.
const MaxInjectionCount = int64(9223372036854775807)

// ErrAuthorizationDenied is a scope that lies outside the authorization bounds.
var ErrAuthorizationDenied = errors.New("the record's scope lies outside the authorization bounds")

// InjectionIDs are the distinct IDs to record, in order. More than MaxRecordInjectionIDs, or a nil UUID, is an error.
func InjectionIDs(experienceIDs []UUID) ([]UUID, error) {
	if len(experienceIDs) > MaxRecordInjectionIDs {
		return nil, fmt.Errorf("an injection record names at most %d records", MaxRecordInjectionIDs)
	}
	seen := make(map[UUID]bool, len(experienceIDs))
	ids := make([]UUID, 0, len(experienceIDs))
	for _, id := range experienceIDs {
		if id.IsZero() {
			return nil, errors.New("every injected record's ID must be a non-nil UUID")
		}
		if !seen[id] {
			seen[id] = true
			ids = append(ids, id)
		}
	}
	return ids, nil
}
