package agentexperience

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
	"unicode/utf8"
)

// Semantic retrieval (contract 1.6): the embedder the caller brings, what is embedded and how it is hashed, the
// validation that runs before any statement, the write and search types a store serves, and the pure functions that
// turn two channels' candidates into one list. No embedding model is a dependency of this module: an Embedder is the
// caller's own object.

// Embedder turns text into a vector: the caller's object, registered with the agent integration's Options.Embedder.
// ModelID and Dimension are known without calling the provider; the vector Embed returns has exactly Dimension
// finite components. Embeddings of different models or dimensions are never compared. Embed should honour ctx: the
// integration cancels it when the vector channel's timeout passes, but never waits for a call that does not return.
type Embedder interface {
	ModelID() string
	Dimension() int
	Embed(ctx context.Context, text string) ([]float32, error)
}

// Limits of contract 1.6.
const (
	// MaxSummaryLength is the longest summary that is embedded, in UTF-16 code units.
	MaxSummaryLength = 8192
	// MaxModelIDLength is the longest model ID, in UTF-16 code units.
	MaxModelIDLength = 256
	// MaxDimension is the most components a vector has (pgvector's vector type accepts up to 16,000).
	MaxDimension = 16_000
)

// VectorFallbackReason is why an injection answered from text alone (contract 1.6).
type VectorFallbackReason string

// The fallback reasons, named exactly as the contract and .NET name them.
const (
	FallbackNotConfigured       VectorFallbackReason = "NotConfigured"
	FallbackProviderUnavailable VectorFallbackReason = "ProviderUnavailable"
	FallbackModelMismatch       VectorFallbackReason = "ModelMismatch"
	FallbackDimensionMismatch   VectorFallbackReason = "DimensionMismatch"
	FallbackVectorSearchFailed  VectorFallbackReason = "VectorSearchFailed"
)

// VectorFallback says the vector channel did not contribute: the contract's reason, a sentence that never quotes
// record or vector content, and the error, when there was one.
type VectorFallback struct {
	Reason VectorFallbackReason
	Detail string
	Err    error
}

// ---- What is embedded ---------------------------------------------------------------------------------------------

// decodeUnits is s as UTF-16 semantics see it: a lone surrogate (written in its three-byte form, as .NET text with
// one is) stays one unit in the surrogate range, and any other invalid byte is U+FFFD.
func decodeUnits(s string) []rune {
	out := make([]rune, 0, len(s))
	for i := 0; i < len(s); {
		r, size := utf8.DecodeRuneInString(s[i:])
		if r == utf8.RuneError && size <= 1 {
			if i+2 < len(s) && s[i] == 0xED && s[i+1] >= 0xA0 && s[i+1] <= 0xBF && s[i+2]&0xC0 == 0x80 {
				out = append(out, rune(0xD000|int(s[i+1]&0x3F)<<6|int(s[i+2]&0x3F)))
				i += 3
				continue
			}
			out = append(out, utf8.RuneError)
			i++
			continue
		}
		out = append(out, r)
		i += size
	}
	return out
}

// validUTF8 is s as .NET's UTF-8 encoder writes it: a lone surrogate, and any other invalid byte, is U+FFFD.
func validUTF8(s string) string { return string(decodeUnits(s)) }

func unitSize(r rune) int {
	if r > 0xFFFF {
		return 2
	}
	return 1
}

func isSeparator(r rune) bool {
	switch category(r) {
	case "Cc", "Zs", "Zl", "Zp":
		return true
	}
	return false
}

// RetrievalSummary is the only text of a record that is embedded: its task ID, task summary and lesson (a nil field
// contributes nothing), each with every run of separators (Unicode categories Cc, Zs, Zl and Zp, such as controls,
// spaces and line separators; format characters are kept) as one space, joined by one space, and at most
// MaxSummaryLength UTF-16 code units: cut there, a high surrogate left last dropped too (so a pair is never split),
// then trailing spaces dropped. The result is valid UTF-8 (a lone surrogate is U+FFFD).
func RetrievalSummary(taskID, taskSummary, lesson *string) string {
	var parts [][]rune
	for _, field := range []*string{taskID, taskSummary, lesson} {
		if field == nil {
			continue
		}
		var words []rune
		pending := false
		for _, r := range decodeUnits(*field) {
			if isSeparator(r) {
				pending = len(words) > 0
				continue
			}
			if pending {
				words = append(words, ' ')
				pending = false
			}
			words = append(words, r)
		}
		if len(words) > 0 {
			parts = append(parts, words)
		}
	}
	var text []rune
	for i, part := range parts {
		if i > 0 {
			text = append(text, ' ')
		}
		text = append(text, part...)
	}
	units := 0
	for _, r := range text {
		units += unitSize(r)
	}
	if units <= MaxSummaryLength {
		return string(text)
	}
	used, keep := 0, 0
	for keep < len(text) && used+unitSize(text[keep]) <= MaxSummaryLength {
		used += unitSize(text[keep])
		keep++
	}
	kept := text[:keep]
	if n := len(kept); n > 0 && kept[n-1] >= 0xD800 && kept[n-1] <= 0xDBFF {
		kept = kept[:n-1]
	}
	for n := len(kept); n > 0 && kept[n-1] == ' '; n = len(kept) {
		kept = kept[:n-1]
	}
	return string(kept)
}

// RecordSummary is RetrievalSummary of a record.
func RecordSummary(record *ExperienceRecord) string {
	var lesson *string
	if record.Reflection != nil {
		lesson = &record.Reflection.Lesson
	}
	return RetrievalSummary(&record.TaskID, record.TaskSummary, lesson)
}

// ContentHash is the lowercase hex SHA-256 of the UTF-8 bytes of the model ID, U+001F and the summary; text that is
// not valid (a lone surrogate) is encoded as U+FFFD, as .NET encodes it.
func ContentHash(modelID, summary string) string {
	digest := sha256.Sum256([]byte(validUTF8(modelID + "\u001f" + summary)))
	return hex.EncodeToString(digest[:])
}

// ---- Validation (before any statement) -----------------------------------------------------------------------------

// ValidateModelID refuses a blank model ID, and one longer than MaxModelIDLength UTF-16 code units.
func ValidateModelID(modelID string) error {
	if isNullOrWhitespace(modelID) {
		return errors.New("the model ID must not be blank")
	}
	if UTF16Len(modelID) > MaxModelIDLength {
		return fmt.Errorf("the model ID is longer than %d UTF-16 code units", MaxModelIDLength)
	}
	return nil
}

// ValidateVector refuses a vector that does not have 1 to MaxDimension finite components, and, when dimension is
// positive, exactly that many.
func ValidateVector(vector []float32, dimension int) error {
	if len(vector) < 1 || len(vector) > MaxDimension {
		return fmt.Errorf("a vector has 1 to %d components", MaxDimension)
	}
	if dimension > 0 && len(vector) != dimension {
		return errors.New("the vector's length is not its dimension")
	}
	for _, component := range vector {
		if math.IsNaN(float64(component)) || math.IsInf(float64(component), 0) {
			return errors.New("a vector component must be finite")
		}
	}
	return nil
}

// VectorLiteral is a pgvector literal, [x,y,...], each component its shortest round-trip text.
func VectorLiteral(vector []float32) string {
	var out strings.Builder
	out.WriteByte('[')
	for i, component := range vector {
		if i > 0 {
			out.WriteByte(',')
		}
		out.WriteString(strconv.FormatFloat(float64(component), 'g', -1, 32))
	}
	out.WriteByte(']')
	return out.String()
}

// EmbeddingWrite is one record's embedding to store: the record (its ID, exact scope and the revision the summary
// was read at) and what the embedding is.
type EmbeddingWrite struct {
	ExperienceID   UUID
	Scope          Scope
	ModelID        string
	Dimension      int
	ContentHash    string
	SourceRevision int64
	Vector         []float32
	// Now is the writer's clock, which becomes created_at and updated_at.
	Now Instant
}

// Validate refuses a write contract 1.6 refuses: a blank or overlong model ID, a dimension outside 1 to
// MaxDimension, a vector that is not Dimension finite components, a blank content hash, a negative revision.
func (w EmbeddingWrite) Validate() error {
	if err := ValidateModelID(w.ModelID); err != nil {
		return err
	}
	if w.Dimension < 1 || w.Dimension > MaxDimension {
		return fmt.Errorf("the dimension must be 1 to %d", MaxDimension)
	}
	if err := ValidateVector(w.Vector, w.Dimension); err != nil {
		return err
	}
	if isNullOrWhitespace(w.ContentHash) {
		return errors.New("the content hash must not be blank")
	}
	if w.SourceRevision < 0 {
		return errors.New("the source revision must not be negative")
	}
	return nil
}

// VectorQuery is a nearest-neighbour search in exactly Scope, over embeddings of ModelID and the vector's length.
type VectorQuery struct {
	Scope   Scope
	ModelID string
	Vector  []float32
	// Statuses are EligibleStatuses() when nil.
	Statuses []ExperienceStatus
	// MinConfidence is DefaultMinConfidence when nil.
	MinConfidence *float64
	// Limit is StatementLimit when 0.
	Limit                int
	ExcludeModelAuthored bool
}

// Validate refuses a search contract 1.6 refuses: a blank or overlong model ID, a vector that is not 1 to
// MaxDimension finite components, an empty or unknown status, a minimum confidence outside 0 to 1, a negative limit.
func (q VectorQuery) Validate() error {
	if err := ValidateModelID(q.ModelID); err != nil {
		return err
	}
	if err := ValidateVector(q.Vector, 0); err != nil {
		return err
	}
	if q.Statuses != nil && len(q.Statuses) == 0 {
		return errors.New("the statuses must not be empty: leave them nil for the eligible statuses")
	}
	for _, status := range q.Statuses {
		if !contains(experienceStatuses, string(status)) {
			return errors.New("the statuses must be experience status member names")
		}
	}
	if q.MinConfidence != nil && !(*q.MinConfidence >= 0 && *q.MinConfidence <= 1) {
		return errors.New("the minimum confidence must be a number from 0 to 1")
	}
	if q.Limit < 0 {
		return errors.New("the limit must be strictly positive")
	}
	return nil
}

// EffectiveStatuses are the statuses searched.
func (q VectorQuery) EffectiveStatuses() []ExperienceStatus {
	if q.Statuses == nil {
		return EligibleStatuses()
	}
	return q.Statuses
}

// EffectiveMinConfidence is the minimum confidence searched.
func (q VectorQuery) EffectiveMinConfidence() float64 {
	if q.MinConfidence == nil {
		return DefaultMinConfidence
	}
	return *q.MinConfidence
}

// EffectiveLimit is the number of rows the search may return.
func (q VectorQuery) EffectiveLimit() int {
	if q.Limit == 0 {
		return StatementLimit
	}
	return q.Limit
}

// EmbeddingWriteOutcome is how a write ended.
type EmbeddingWriteOutcome string

// The write outcomes.
const (
	// EmbeddingWritten: the row now holds this vector and descriptor.
	EmbeddingWritten EmbeddingWriteOutcome = "Written"
	// EmbeddingStale: the record moved on since the summary was read (or a newer embedding is stored); nothing changed.
	EmbeddingStale EmbeddingWriteOutcome = "Stale"
	// EmbeddingMissing: an unknown, other-scope or erased record; nothing was created.
	EmbeddingMissing EmbeddingWriteOutcome = "Missing"
)

// EmbeddingWriteResult is the outcome, and, for Stale, the record's current revision.
type EmbeddingWriteResult struct {
	Outcome         EmbeddingWriteOutcome
	CurrentRevision int64
}

// VectorSearchOutcome is how a vector search ended.
type VectorSearchOutcome string

// The search outcomes.
const (
	VectorFound             VectorSearchOutcome = "Found"
	VectorModelMismatch     VectorSearchOutcome = "ModelMismatch"
	VectorDimensionMismatch VectorSearchOutcome = "DimensionMismatch"
)

// RelevanceOf is clamp(1 - distance / 2, 0, 1), and 0 for NaN.
func RelevanceOf(distance float64) float64 {
	if math.IsNaN(distance) {
		return 0
	}
	return math.Min(math.Max(1.0-distance/2.0, 0), 1)
}

// VectorHit is one record the vector search returned, and its cosine distance (NaN for a zero-magnitude vector).
type VectorHit struct {
	Record   ExperienceRecord
	Distance float64
}

// Relevance is RelevanceOf the hit's distance.
func (h VectorHit) Relevance() float64 { return RelevanceOf(h.Distance) }

// VectorSearchResult is the outcome, and the hits nearest first (ties by experience ID, NaN last); no hits unless
// the outcome is VectorFound.
type VectorSearchResult struct {
	Outcome VectorSearchOutcome
	Hits    []VectorHit
}

// OrderHits orders hits nearest first; equal distances by experience ID as text, ordinally; NaN after every number,
// equal to each other. It sorts hits in place and returns them.
func OrderHits(hits []VectorHit) []VectorHit {
	sort.SliceStable(hits, func(i, j int) bool {
		a, b := hits[i].Distance, hits[j].Distance
		aNaN, bNaN := math.IsNaN(a), math.IsNaN(b)
		switch {
		case aNaN != bNaN:
			return bNaN
		case !aNaN && a != b:
			return a < b
		}
		return hits[i].Record.ExperienceID.String() < hits[j].Record.ExperienceID.String()
	})
	return hits
}

// ---- Merging the two channels --------------------------------------------------------------------------------------

// Channel names where a merged candidate came from.
type Channel string

// The channels.
const (
	ChannelText   Channel = "text"
	ChannelVector Channel = "vector"
)

// Merged is one entry of the merged list: the candidate kept (the first channel's), the relevance kept, and the
// channel the candidate came from.
type Merged[T any] struct {
	Item      T
	Relevance float64
	Source    Channel
}

func clampedRelevance(relevance float64) float64 {
	if math.IsNaN(relevance) {
		return 0
	}
	return math.Min(math.Max(relevance, 0), 1)
}

// MergeChannels is contract 1.6's steps 1 and 3. The floor drops vector candidates below minimumVectorRelevance
// (nil: no floor; one exactly at it stays; a NaN relevance is below any floor; text is never floored). The merge
// lists the text candidates in order, then the vector candidates in theirs; a record already listed keeps its
// position and the text channel's candidate, and takes the vector relevance, as given, only when its clamped value
// (NaN as 0) is higher.
func MergeChannels[T any](text, vector []T, key func(T) UUID, relevance func(T) float64,
	minimumVectorRelevance *float64) []Merged[T] {
	merged := make([]Merged[T], 0, len(text)+len(vector))
	positions := make(map[UUID]int, len(text)+len(vector))
	for _, item := range text {
		if _, held := positions[key(item)]; !held {
			positions[key(item)] = len(merged)
		}
		merged = append(merged, Merged[T]{item, relevance(item), ChannelText})
	}
	for _, item := range vector {
		value := relevance(item)
		if minimumVectorRelevance != nil && !(value >= *minimumVectorRelevance) {
			continue
		}
		position, held := positions[key(item)]
		switch {
		case !held:
			positions[key(item)] = len(merged)
			merged = append(merged, Merged[T]{item, value, ChannelVector})
		case clampedRelevance(value) > clampedRelevance(merged[position].Relevance):
			merged[position].Relevance = value
		}
	}
	return merged
}

// CheckChannel is contract 1.6's step 2 for one channel: over its first CandidateLimit candidates, one outside scope
// or a repeated experience ID is an error wrapping ErrSearchSafety; it returns those first candidates.
func CheckChannel(rows []SearchRow, scope Scope) ([]SearchRow, error) {
	return checkChannel(rows, scope, CandidateLimit)
}

// RankHybrid is the whole of contract 1.6's steps 1 to 4 under the default policy with minimumVectorRelevance as
// the vector floor: RankHybridWith.
func RankHybrid(textRows, vectorRows []SearchRow, scope Scope, now Instant, preferredEnvironment map[string]string,
	minimumVectorRelevance *float64, excludeModelAuthored bool) (RankedResult, error) {
	return RankHybridWith(textRows, vectorRows, scope, now, preferredEnvironment, RankOptions{
		ExcludeModelAuthored: excludeModelAuthored,
		Policy:               RetrievalPolicy{MinimumVectorRelevance: minimumVectorRelevance}})
}
