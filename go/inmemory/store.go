// Package inmemory is an in-memory store for development and tests: no PostgreSQL. It keeps only what the contract
// needs (insert a finalized record, read one back, the text search, when each record was last injected, and the
// embeddings of contract 1.6 with their nearest-neighbour search),
// honouring scope and the authorization
// bounds the way the PostgreSQL store's row-level security does. Its text search is the .NET in-memory store's:
// words normalized to Unicode form KC and lower-cased, the English stopwords PostgreSQL drops left out of a query,
// and a record matching at least the minimum number of query terms (fewer for a shorter query), weighted 3 for the
// task summary and 1 each for the task ID and the lesson. Nothing survives the process.
//
// It is its own module, so the core requires no module: Unicode normalization is golang.org/x/text.
package inmemory

import (
	"context"
	"errors"
	"math"
	"sort"
	"sync"
	"time"
	"unicode"

	"golang.org/x/text/unicode/norm"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// MaxIndexedLength is the most text indexed per record, in UTF-16 code units, across its task ID, task summary and
// lesson in that order: the bound the PostgreSQL store's search column applies.
const MaxIndexedLength = 100_000

const (
	summaryWeight = 3
	taskIDWeight  = 1
	lessonWeight  = 1
	termWeight    = summaryWeight + taskIDWeight + lessonWeight
)

// The Snowball English stopwords PostgreSQL's english text-search configuration drops.
var stopwords = func() map[string]bool {
	words := map[string]bool{}
	for _, word := range []string{
		"i", "me", "my", "myself", "we", "our", "ours", "ourselves", "you", "your", "yours", "yourself", "yourselves",
		"he", "him", "his", "himself", "she", "her", "hers", "herself", "it", "its", "itself", "they", "them", "their",
		"theirs", "themselves", "what", "which", "who", "whom", "this", "that", "these", "those", "am", "is", "are",
		"was", "were", "be", "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing", "a", "an",
		"the", "and", "but", "if", "or", "because", "as", "until", "while", "of", "at", "by", "for", "with", "about",
		"against", "between", "into", "through", "during", "before", "after", "above", "below", "to", "from", "up",
		"down", "in", "out", "on", "off", "over", "under", "again", "further", "then", "once", "here", "there", "when",
		"where", "why", "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such", "no",
		"nor", "not", "only", "own", "same", "so", "than", "too", "very", "s", "t", "can", "will", "just", "don",
		"should", "now",
	} {
		words[word] = true
	}
	return words
}()

// ErrAuthorizationDenied is a record whose scope lies outside the authorization bounds: the core's
// agentexperience.ErrAuthorizationDenied, so errors.Is matches either.
var ErrAuthorizationDenied = ae.ErrAuthorizationDenied

// Permits reports whether bounds permit scope: the tenant always, and every bounded field exactly.
func Permits(bounds ae.AuthorizationBounds, scope ae.Scope) bool { return bounds.Permits(scope) }

// words are text's words: normalized to Unicode form KC and lower-cased, then split into runs of letters and
// decimal digits; a combining mark continues the word it follows.
func words(text string) []string {
	var result []string
	var word []rune
	for _, r := range lower(norm.NFKC.String(text)) {
		switch {
		case unicode.IsLetter(r) || unicode.Is(unicode.Nd, r):
			word = append(word, r)
		case len(word) > 0 && unicode.In(r, unicode.Mn, unicode.Mc):
			word = append(word, r)
		case len(word) > 0:
			result = append(result, string(word))
			word = word[:0]
		}
	}
	if len(word) > 0 {
		result = append(result, string(word))
	}
	return result
}

// lower is .NET's ToLowerInvariant: each character's simple lower-case mapping, except that the dotted capital I
// (U+0130) is its own lower case, as .NET's invariant culture keeps it (Go's unicode.ToLower maps it to i).
func lower(text string) []rune {
	runes := []rune(text)
	for i, r := range runes {
		if r != 0x0130 {
			runes[i] = unicode.ToLower(r)
		}
	}
	return runes
}

// QueryTerms are the distinct terms a query searches for: its words, less stopwords and words shorter than two
// characters. Empty when nothing is left, and then nothing matches.
func QueryTerms(text string) []string {
	var terms []string
	seen := map[string]bool{}
	for _, word := range words(text) {
		if len([]rune(word)) < 2 || stopwords[word] || seen[word] {
			continue
		}
		seen[word] = true
		terms = append(terms, word)
	}
	return terms
}

type indexed struct {
	record                  ae.ExperienceRecord
	taskID, summary, lesson map[string]bool
}

func (x *indexed) contains(term string) bool {
	return x.summary[term] || x.taskID[term] || x.lesson[term]
}

func (x *indexed) relevance(terms []string) float64 {
	score := 0
	for _, term := range terms {
		if x.summary[term] {
			score += summaryWeight
		}
		if x.taskID[term] {
			score += taskIDWeight
		}
		if x.lesson[term] {
			score += lessonWeight
		}
	}
	return float64(score) / float64(termWeight*len(terms))
}

func index(record ae.ExperienceRecord) *indexed {
	budget := MaxIndexedLength
	take := func(text *string) map[string]bool {
		set := map[string]bool{}
		if text == nil || *text == "" || budget <= 0 {
			// The separator between fields counts towards the bound too, as it does in PostgreSQL's concatenation.
			budget--
			return set
		}
		taken := *text
		if ae.UTF16Len(taken) > budget {
			taken = ae.UTF16Prefix(taken, budget)
		}
		budget -= ae.UTF16Len(taken) + 1
		for _, word := range words(taken) {
			set[word] = true
		}
		return set
	}
	var lesson *string
	if record.Reflection != nil {
		lesson = &record.Reflection.Lesson
	}
	x := &indexed{record: record}
	x.taskID = take(&record.TaskID)
	x.summary = take(record.TaskSummary)
	x.lesson = take(lesson)
	return x
}

// Store holds records in memory, for development and tests. It is safe for concurrent use. The zero value is not
// usable: make one with New.
type Store struct {
	// Clock is the store's own clock, which an injection's instant is clamped to, as PostgreSQL clamps it to its own;
	// nil reads agentexperience.Now. Set it before the store is used.
	Clock func() ae.Instant

	mu         sync.RWMutex
	records    map[ae.UUID]*indexed
	order      []ae.UUID
	events     map[ae.UUID]Event
	injections map[ae.UUID]Injection
	embeddings map[ae.UUID]Embedding
}

// Embedding is a record's stored embedding (contract 1.6): the model, the dimension, the content hash of the summary
// that was embedded, the record revision it was read at, and the vector, its components 32-bit floats.
type Embedding struct {
	ModelID        string
	Dimension      int
	ContentHash    string
	SourceRevision int64
	Vector         []float32
}

// Injection is when a record was last injected in its own scope, and how many injected blocks delivered it.
type Injection struct {
	LastInjectedAt ae.Instant
	InjectionCount int64
}

// New is a store holding records (each experience ID once).
func New(records ...ae.ExperienceRecord) (*Store, error) {
	s := &Store{records: map[ae.UUID]*indexed{}, events: map[ae.UUID]Event{}, injections: map[ae.UUID]Injection{},
		embeddings: map[ae.UUID]Embedding{}}
	for _, record := range records {
		if _, held := s.records[record.ExperienceID]; held {
			return nil, errors.New("two records share one experience ID")
		}
		copied, err := clone(record)
		if err != nil {
			return nil, err
		}
		s.add(copied)
	}
	return s, nil
}

func (s *Store) add(record ae.ExperienceRecord) {
	s.records[record.ExperienceID] = index(record)
	s.order = append(s.order, record.ExperienceID)
}

// clone is a deep copy of a record, so the store and its callers never share a pointer, slice or map: the record
// written in its stored form and read back, which the contract makes exact.
func clone(record ae.ExperienceRecord) (ae.ExperienceRecord, error) {
	return ae.FromStored(record.ToStored())
}

func mustClone(record ae.ExperienceRecord) ae.ExperienceRecord {
	copied, err := clone(record)
	if err != nil {
		panic("inmemory: a record held cannot be copied: " + err.Error())
	}
	return copied
}

// Event is a record's initial lifecycle event as InsertFinalized stored it, with its actor and recording time.
type Event struct {
	Event      ae.InitialLifecycleEvent
	Actor      string
	RecordedAt time.Time
}

// Event is the initial lifecycle event stored with a record; ok is false for a record written without one
// (InsertRecord, New) or none at all.
func (s *Store) Event(id ae.UUID) (Event, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	held, ok := s.events[id]
	return held, ok
}

// InsertFinalized writes a copy of a finalized record and its initial lifecycle event. It refuses a done context, a
// blank actor, an event that
// is not the record's own, a record outside the bounds (ErrAuthorizationDenied) and a record already stored.
func (s *Store) InsertFinalized(ctx context.Context, bounds ae.AuthorizationBounds, finalized *ae.FinalizedExperience,
	actor string, recordedAt *time.Time) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if finalized == nil {
		return errors.New("there is no finalized record to write")
	}
	if ae.IsNullOrWhiteSpace(actor) {
		return errors.New("the actor (the authorizing principal) must not be blank")
	}
	record, initial := finalized.Record, finalized.Event
	if initial.ExperienceID != record.ExperienceID || initial.CurrentStatus != record.Status ||
		initial.AppliedRevision != record.Revision {
		return errors.New("the lifecycle event is not the record's own: its experience ID, current status and " +
			"applied revision must be the record's")
	}
	if !Permits(bounds, record.Scope) {
		return ErrAuthorizationDenied
	}
	at := time.Now().UTC()
	if recordedAt != nil {
		at = *recordedAt
	}
	record, err := clone(record)
	if err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, held := s.records[record.ExperienceID]; held {
		return errors.New("a record with this experience ID is already stored")
	}
	s.add(record)
	s.events[record.ExperienceID] = Event{initial, actor, at}
	return nil
}

// InsertRecord writes one record exactly as given, with no lifecycle event: for seeding and tests.
func (s *Store) InsertRecord(ctx context.Context, bounds ae.AuthorizationBounds, record *ae.ExperienceRecord) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if record == nil {
		return errors.New("there is no record to write")
	}
	if !Permits(bounds, record.Scope) {
		return ErrAuthorizationDenied
	}
	copied, err := clone(*record)
	if err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, held := s.records[record.ExperienceID]; held {
		return errors.New("a record with this experience ID is already stored")
	}
	s.add(copied)
	return nil
}

// Get reads one record in exactly scope; (nil, nil) when there is none, or the bounds do not permit it.
func (s *Store) Get(_ context.Context, bounds ae.AuthorizationBounds, scope ae.Scope, id ae.UUID) (*ae.ExperienceRecord, error) {
	if !Permits(bounds, scope) {
		return nil, nil
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	held, ok := s.records[id]
	if !ok || !held.record.Scope.Equal(scope) {
		return nil, nil
	}
	record := mustClone(held.record)
	return &record, nil
}

// Records are copies of every record held, in the order they were written: for inspection in tests.
func (s *Store) Records() []ae.ExperienceRecord {
	s.mu.RLock()
	defer s.mu.RUnlock()
	records := make([]ae.ExperienceRecord, 0, len(s.order))
	for _, id := range s.order {
		records = append(records, mustClone(s.records[id].record))
	}
	return records
}

// Search is the text search: records in exactly the requested scope, of an eligible status and confidence, matching
// enough query terms; by relevance, then experience ID, at most the limit. Nothing outside the bounds. Parameters
// that fail Validate are an error.
func (s *Store) Search(ctx context.Context, bounds ae.AuthorizationBounds, parameters ae.SearchParameters) ([]ae.SearchRow, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if err := parameters.Validate(); err != nil {
		return nil, err
	}
	scope := parameters.Scope
	if !Permits(bounds, scope) {
		return nil, nil
	}
	terms := QueryTerms(parameters.TaskText)
	if len(terms) == 0 {
		return nil, nil
	}
	required := parameters.MinimumMatchedTerms
	if required == 0 {
		required = ae.DefaultMinimumMatchedTerms
	}
	required = min(required, len(terms))
	minConfidence := ae.DefaultMinConfidence
	if parameters.MinConfidence != nil {
		minConfidence = *parameters.MinConfidence
	}
	statuses := parameters.Statuses
	if statuses == nil {
		statuses = ae.EligibleStatuses()
	}
	eligible := map[ae.ExperienceStatus]bool{}
	for _, status := range statuses {
		eligible[status] = true
	}

	s.mu.RLock()
	held := make([]*indexed, 0, len(s.order))
	for _, id := range s.order {
		held = append(held, s.records[id])
	}
	s.mu.RUnlock()

	var rows []ae.SearchRow
	for _, x := range held {
		record := &x.record
		if !record.Scope.Equal(scope) || !eligible[record.Status] || record.ReuseConfidence < minConfidence ||
			(parameters.ExcludeModelAuthored && ae.IsModelAuthored(record.Reflection)) {
			continue
		}
		matched := 0
		for _, term := range terms {
			if x.contains(term) {
				matched++
			}
		}
		if matched < required {
			continue
		}
		rows = append(rows, ae.SearchRow{Record: mustClone(x.record), Relevance: x.relevance(terms)})
	}
	sort.SliceStable(rows, func(i, j int) bool {
		if rows[i].Relevance != rows[j].Relevance {
			return rows[i].Relevance > rows[j].Relevance
		}
		return rows[i].Record.ExperienceID.String() < rows[j].Record.ExperienceID.String()
	})
	if len(rows) > ae.StatementLimit {
		rows = rows[:ae.StatementLimit]
	}
	return rows, nil
}

// RecordInjection records that experienceIDs were injected in scope at injectedAt, as sql/record-injection.sql does:
// only a record held in exactly scope counts, the instant (truncated to whole microseconds) is clamped to the store's
// Clock and only moves forward, and the count rises by one and saturates at agentexperience.MaxInjectionCount. It
// refuses a done context, more than agentexperience.MaxRecordInjectionIDs IDs or a nil one, and a scope outside the
// bounds (ErrAuthorizationDenied).
func (s *Store) RecordInjection(ctx context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	experienceIDs []ae.UUID, injectedAt ae.Instant) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	ids, err := ae.InjectionIDs(experienceIDs)
	if err != nil {
		return err
	}
	if !Permits(bounds, scope) {
		return ErrAuthorizationDenied
	}
	clock := s.Clock
	if clock == nil {
		clock = ae.Now
	}
	at, now := injectedAt.TruncateToMicroseconds(), clock().TruncateToMicroseconds()
	if now.Before(at) {
		at = now
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, id := range ids {
		held, ok := s.records[id]
		if !ok || !held.record.Scope.Equal(scope) {
			continue
		}
		current, seen := s.injections[id]
		if !seen {
			s.injections[id] = Injection{LastInjectedAt: at, InjectionCount: 1}
			continue
		}
		if current.LastInjectedAt.Before(at) {
			current.LastInjectedAt = at
		}
		if current.InjectionCount < ae.MaxInjectionCount {
			current.InjectionCount++
		}
		s.injections[id] = current
	}
	return nil
}

// Injections are when each record was last injected and how often: for inspection in tests.
func (s *Store) Injections() map[ae.UUID]Injection {
	s.mu.RLock()
	defer s.mu.RUnlock()
	copied := make(map[ae.UUID]Injection, len(s.injections))
	for id, injection := range s.injections {
		copied[id] = injection
	}
	return copied
}

// SetInjection replaces a record's injection summary: for seeding tests (a count near saturation, say).
func (s *Store) SetInjection(id ae.UUID, injection Injection) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.injections[id] = injection
}

// WriteEmbedding stores a record's embedding as sql/write-experience-embedding.sql does: only for a record held in
// exactly the write's scope and still at exactly its source revision (Written); otherwise Stale with the record's
// revision, or Missing for an unknown record or one in another scope or outside the bounds. A row stored from a newer
// revision is never replaced by an older one. A write that fails Validate is an error, and stores nothing.
func (s *Store) WriteEmbedding(ctx context.Context, bounds ae.AuthorizationBounds,
	write ae.EmbeddingWrite) (ae.EmbeddingWriteResult, error) {
	if err := ctx.Err(); err != nil {
		return ae.EmbeddingWriteResult{}, err
	}
	if err := write.Validate(); err != nil {
		return ae.EmbeddingWriteResult{}, err
	}
	if !Permits(bounds, write.Scope) {
		return ae.EmbeddingWriteResult{Outcome: ae.EmbeddingMissing}, nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	held, ok := s.records[write.ExperienceID]
	if !ok || !held.record.Scope.Equal(write.Scope) {
		return ae.EmbeddingWriteResult{Outcome: ae.EmbeddingMissing}, nil
	}
	revision := held.record.Revision
	if existing, stored := s.embeddings[write.ExperienceID]; revision != write.SourceRevision ||
		(stored && existing.SourceRevision > write.SourceRevision) {
		return ae.EmbeddingWriteResult{Outcome: ae.EmbeddingStale, CurrentRevision: revision}, nil
	}
	s.embeddings[write.ExperienceID] = Embedding{ModelID: write.ModelID, Dimension: write.Dimension,
		ContentHash: write.ContentHash, SourceRevision: write.SourceRevision,
		Vector: append([]float32(nil), write.Vector...)}
	return ae.EmbeddingWriteResult{Outcome: ae.EmbeddingWritten}, nil
}

// Embedding is a record's stored embedding, for tests; ok is false when it has none.
func (s *Store) Embedding(id ae.UUID) (Embedding, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	held, ok := s.embeddings[id]
	held.Vector = append([]float32(nil), held.Vector...)
	return held, ok
}

// CosineDistance is 1 - cos of two vectors of equal length, clamped to [0, 2]: the components are 32-bit floats,
// the arithmetic double. NaN when either has no magnitude (a zero vector).
func CosineDistance(left, right []float32) float64 {
	var dot, leftSquares, rightSquares float64
	for i := range left {
		a, b := float64(left[i]), float64(right[i])
		dot += a * b
		leftSquares += a * a
		rightSquares += b * b
	}
	norm := math.Sqrt(leftSquares) * math.Sqrt(rightSquares)
	if norm == 0 || math.IsNaN(norm) {
		return math.NaN()
	}
	return math.Min(math.Max(1.0-dot/norm, 0), 2)
}

// SearchVectors is the nearest-neighbour search: embeddings of the query's model and dimension, of records in exactly
// the query's scope with an eligible status and confidence (and not model-authored, when excluded), by cosine
// distance, ties by experience ID, NaN last, at most the limit. With none, the outcome says why: ModelMismatch or
// DimensionMismatch when eligible embeddings exist but none is comparable, else Found and empty. Nothing outside the
// bounds. A query that fails Validate is an error.
func (s *Store) SearchVectors(ctx context.Context, bounds ae.AuthorizationBounds,
	query ae.VectorQuery) (ae.VectorSearchResult, error) {
	if err := ctx.Err(); err != nil {
		return ae.VectorSearchResult{}, err
	}
	if err := query.Validate(); err != nil {
		return ae.VectorSearchResult{}, err
	}
	scope := query.Scope
	if !Permits(bounds, scope) {
		return ae.VectorSearchResult{Outcome: ae.VectorFound}, nil
	}
	eligible := map[ae.ExperienceStatus]bool{}
	for _, status := range query.EffectiveStatuses() {
		eligible[status] = true
	}
	minConfidence := query.EffectiveMinConfidence()

	type candidate struct {
		record    *indexed
		embedding Embedding
	}
	s.mu.RLock()
	var held []candidate
	for _, id := range s.order {
		embedding, ok := s.embeddings[id]
		if !ok {
			continue
		}
		x := s.records[id]
		record := &x.record
		if !record.Scope.Equal(scope) || !eligible[record.Status] || record.ReuseConfidence < minConfidence ||
			(query.ExcludeModelAuthored && ae.IsModelAuthored(record.Reflection)) {
			continue
		}
		held = append(held, candidate{x, embedding})
	}
	s.mu.RUnlock()

	var hits []ae.VectorHit
	modelHeld := false
	for _, c := range held {
		if c.embedding.ModelID != query.ModelID {
			continue
		}
		modelHeld = true
		if c.embedding.Dimension != len(query.Vector) || len(c.embedding.Vector) != len(query.Vector) {
			continue
		}
		hits = append(hits, ae.VectorHit{Record: mustClone(c.record.record),
			Distance: CosineDistance(c.embedding.Vector, query.Vector)})
	}
	switch {
	case len(hits) > 0:
		hits = ae.OrderHits(hits)
		if limit := query.EffectiveLimit(); len(hits) > limit {
			hits = hits[:limit]
		}
		return ae.VectorSearchResult{Outcome: ae.VectorFound, Hits: hits}, nil
	case len(held) == 0:
		return ae.VectorSearchResult{Outcome: ae.VectorFound}, nil
	case !modelHeld:
		return ae.VectorSearchResult{Outcome: ae.VectorModelMismatch}, nil
	}
	return ae.VectorSearchResult{Outcome: ae.VectorDimensionMismatch}, nil
}

var (
	_ ae.Store             = (*Store)(nil)
	_ ae.InjectionRecorder = (*Store)(nil)
	_ ae.EmbeddingIndex    = (*Store)(nil)
)
