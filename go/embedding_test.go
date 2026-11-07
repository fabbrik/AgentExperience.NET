package agentexperience

import (
	"errors"
	"math"
	"strings"
	"testing"
)

// Contract 1.6's pure rules beyond the fixtures: validation, the literal, relevance, ordering, and the hybrid ranking.

func TestVectorValidation(t *testing.T) {
	nan, inf := float32(math.NaN()), float32(math.Inf(-1))
	for name, vector := range map[string][]float32{"empty": nil, "nan": {1, nan}, "infinite": {inf}, "too long": make([]float32, MaxDimension+1)} {
		if ValidateVector(vector, 0) == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	if ValidateVector([]float32{1, 2}, 3) == nil || ValidateVector([]float32{1, 2}, 2) != nil ||
		ValidateVector(make([]float32, MaxDimension), 0) != nil || ValidateVector([]float32{0, 0}, 0) != nil {
		t.Fatal("dimension and zero-magnitude rules")
	}
	if ValidateModelID("") == nil || ValidateModelID(" \u0085") == nil || ValidateModelID(strings.Repeat("m", 257)) == nil ||
		ValidateModelID(strings.Repeat("m", 256)) != nil || ValidateModelID(strings.Repeat("\U0001F600", 129)) == nil {
		t.Fatal("model ID rules (256 UTF-16 code units)")
	}
}

func TestTheVectorLiteralIsPgvectorsFormWithShortestRoundTripComponents(t *testing.T) {
	if got := VectorLiteral([]float32{1, 0.1, -2.5, 1e-7, 3e10}); got != "[1,0.1,-2.5,1e-07,3e+10]" {
		t.Fatal(got)
	}
}

func TestRelevanceAndHitOrder(t *testing.T) {
	if RelevanceOf(math.NaN()) != 0 || RelevanceOf(-1) != 1 || RelevanceOf(3) != 0 || RelevanceOf(1) != 0.5 {
		t.Fatal("relevance is clamp(1 - d/2, 0, 1), NaN as 0")
	}
	records := seedRows(t)
	hit := func(i int, distance float64) VectorHit { return VectorHit{Record: records[i], Distance: distance} }
	ordered := OrderHits([]VectorHit{hit(0, math.NaN()), hit(2, 0.5), hit(1, 0.5), hit(3, math.NaN()), hit(4, 0.1)})
	want := []ExperienceRecord{records[4]}
	pair := []ExperienceRecord{records[1], records[2]}
	if pair[0].ExperienceID.String() > pair[1].ExperienceID.String() {
		pair[0], pair[1] = pair[1], pair[0]
	}
	want = append(want, pair...)
	nans := []ExperienceRecord{records[0], records[3]}
	if nans[0].ExperienceID.String() > nans[1].ExperienceID.String() {
		nans[0], nans[1] = nans[1], nans[0]
	}
	want = append(want, nans...)
	for i, h := range ordered {
		if h.Record.ExperienceID != want[i].ExperienceID {
			t.Fatalf("hit %d is %s, not %s", i, h.Record.ExperienceID, want[i].ExperienceID)
		}
	}
}

func rowsOf(records []ExperienceRecord, relevance float64) []SearchRow {
	rows := make([]SearchRow, len(records))
	for i, record := range records {
		rows[i] = SearchRow{Record: record, Relevance: relevance}
	}
	return rows
}

// manyRecords are n copies of a seed record, each with an ID of its own.
func manyRecords(t *testing.T, n int) []ExperienceRecord {
	t.Helper()
	base := seedRows(t)[0]
	records := make([]ExperienceRecord, n)
	for i := range records {
		records[i] = base
		records[i].ExperienceID = UUID{0: 0xab, 6: 0x40, 8: 0x80, 14: byte(i >> 8), 15: byte(i)}
	}
	return records
}

func TestRankHybridScoresEveryMergedCandidateAndKeepsTheBestFifty(t *testing.T) {
	records := manyRecords(t, 100)
	scope := records[0].Scope
	result, err := RankHybrid(rowsOf(records[:50], 0.2), rowsOf(records[50:], 0.9), scope, t0, nil, nil, false)
	// Contract 1.12's cut: every merged candidate is scored, and the ranked result is cut to the candidate limit.
	if err != nil || len(result.Ranked) != 50 || result.Truncated {
		t.Fatalf("%v %d %v", err, len(result.Ranked), result.Truncated)
	}
	// The vector candidates' higher relevance puts them first: the 50 kept are exactly they.
	for i, entry := range result.Ranked {
		if (i < 50) != (entry.Record.ExperienceID[15] >= 50) {
			t.Fatalf("entry %d is record %d", i, entry.Record.ExperienceID[15])
		}
	}
	for i := 1; i < len(result.Ranked); i++ {
		a, b := result.Ranked[i-1], result.Ranked[i]
		if a.Score < b.Score || (a.Score == b.Score && a.Record.ExperienceID.String() > b.Record.ExperienceID.String()) {
			t.Fatalf("entries %d and %d are out of order", i-1, i)
		}
	}
	// A record in both channels is one entry, with the higher relevance.
	both, err := RankHybrid(rowsOf(records[:3], 0.2), rowsOf(records[2:4], 0.9), scope, t0, nil, nil, false)
	if err != nil || len(both.Ranked) != 4 {
		t.Fatalf("%v %d", err, len(both.Ranked))
	}
	// Truncated when either channel returned 51.
	more, err := RankHybrid(rowsOf(manyRecords(t, 51), 0.2), nil, scope, t0, nil, nil, false)
	if err != nil || !more.Truncated || len(more.Ranked) != 50 {
		t.Fatalf("%v %v %d", err, more.Truncated, len(more.Ranked))
	}
}

func TestRankHybridWithNoVectorCandidatesIsRank(t *testing.T) {
	records := manyRecords(t, 20)
	scope := records[0].Scope
	for i := range records {
		records[i].ReuseConfidence = float64(i) / 20
	}
	rows := rowsOf(records, 0.4)
	hybrid, err := RankHybrid(rows, nil, scope, t0, nil, nil, false)
	plain, plainErr := Rank(rows, scope, t0, nil, false)
	if err != nil || plainErr != nil || len(hybrid.Ranked) != len(plain.Ranked) {
		t.Fatalf("%v %v", err, plainErr)
	}
	for i := range plain.Ranked {
		if hybrid.Ranked[i].Record.ExperienceID != plain.Ranked[i].Record.ExperienceID || hybrid.Ranked[i].Score != plain.Ranked[i].Score {
			t.Fatalf("entry %d differs", i)
		}
	}
}

func TestRankHybridFloorsOnlyVectorCandidates(t *testing.T) {
	records := manyRecords(t, 4)
	scope := records[0].Scope
	floor := 0.5
	text := rowsOf(records[:2], 0.1) // below the floor, never floored
	vector := []SearchRow{{Record: records[2], Relevance: 0.5}, {Record: records[3], Relevance: 0.4999}}
	result, err := RankHybrid(text, vector, scope, t0, nil, &floor, false)
	if err != nil || len(result.Ranked) != 3 {
		t.Fatalf("%v %d", err, len(result.Ranked))
	}
	for _, entry := range result.Ranked {
		if entry.Record.ExperienceID == records[3].ExperienceID {
			t.Fatal("a vector candidate below the floor was kept")
		}
	}
}

func TestRankHybridFailsTheWholeResultForARowOutsideTheScopeOrADuplicateInAChannel(t *testing.T) {
	records := manyRecords(t, 4)
	scope := records[0].Scope
	foreign := records[3]
	foreign.Scope.AgentID = Ptr("other")
	for name, run := range map[string]func() error{
		"foreign vector row": func() error {
			_, err := RankHybrid(rowsOf(records[:1], 0.1), []SearchRow{{Record: foreign, Relevance: 0.9}}, scope, t0, nil, nil, false)
			return err
		},
		"foreign text row": func() error {
			_, err := RankHybrid([]SearchRow{{Record: foreign, Relevance: 0.9}}, rowsOf(records[:1], 0.1), scope, t0, nil, nil, false)
			return err
		},
		"shared by grant": func() error {
			_, err := RankHybrid(nil, []SearchRow{{Record: records[0], Relevance: 0.9, SharedByGrant: true}}, scope, t0, nil, nil, false)
			return err
		},
		"repeated in the vector channel": func() error {
			_, err := RankHybrid(nil, rowsOf([]ExperienceRecord{records[0], records[0]}, 0.9), scope, t0, nil, nil, false)
			return err
		},
	} {
		if err := run(); !errors.Is(err, ErrSearchSafety) {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func TestEmbeddingWriteAndQueryValidate(t *testing.T) {
	good := EmbeddingWrite{ExperienceID: NewRandomUUID(), Scope: scope, ModelID: "m", Dimension: 2, ContentHash: "h",
		SourceRevision: 0, Vector: []float32{1, 0}, Now: t0}
	if err := good.Validate(); err != nil {
		t.Fatal(err)
	}
	for name, mutate := range map[string]func(*EmbeddingWrite){
		"blank model":     func(w *EmbeddingWrite) { w.ModelID = " " },
		"zero dimension":  func(w *EmbeddingWrite) { w.Dimension = 0; w.Vector = nil },
		"wrong length":    func(w *EmbeddingWrite) { w.Dimension = 3 },
		"huge dimension":  func(w *EmbeddingWrite) { w.Dimension = MaxDimension + 1 },
		"blank hash":      func(w *EmbeddingWrite) { w.ContentHash = "" },
		"negative source": func(w *EmbeddingWrite) { w.SourceRevision = -1 },
	} {
		w := good
		mutate(&w)
		if w.Validate() == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	confidence := 1.5
	for name, q := range map[string]VectorQuery{
		"empty statuses": {Scope: scope, ModelID: "m", Vector: []float32{1}, Statuses: []ExperienceStatus{}},
		"bad status":     {Scope: scope, ModelID: "m", Vector: []float32{1}, Statuses: []ExperienceStatus{"Bogus"}},
		"confidence":     {Scope: scope, ModelID: "m", Vector: []float32{1}, MinConfidence: &confidence},
		"limit":          {Scope: scope, ModelID: "m", Vector: []float32{1}, Limit: -1},
	} {
		if q.Validate() == nil {
			t.Errorf("%s was accepted", name)
		}
	}
	q := VectorQuery{Scope: scope, ModelID: "m", Vector: []float32{1}}
	if q.Validate() != nil || q.EffectiveLimit() != StatementLimit || q.EffectiveMinConfidence() != DefaultMinConfidence ||
		len(q.EffectiveStatuses()) != 2 {
		t.Fatal("defaults")
	}
}
