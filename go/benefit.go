package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"math"
	"slices"
	"sort"
)

// The benefit estimate (contract 1.9): whether the lessons help, measured from the injection holdout. It reads the
// holdout's assignment rows in one scope, each joined to its run's verified outcome, a page at a time, and compares how
// often each arm's runs verified: the .NET AgentExperienceBenefit.EstimateAsync, with the same numbers for the same rows.
// It never writes, and it changes nothing about ranking, confidence or injection.

// The outcome read's page limits: .NET's HoldoutOutcomeQuery.
const (
	HoldoutOutcomesMinLimit = 1
	HoldoutOutcomesMaxLimit = 1000
)

// DefaultMinimumRunsPerArm is the fewest verified runs each arm needs before a verdict can be Helps or Hurts.
const DefaultMinimumRunsPerArm = 20

// UnknownModel is the model a split estimate groups runs and lessons under when none was recorded.
const UnknownModel = "unknown"

// BenefitZ is the two-sided 95% normal quantile the intervals are built with.
const BenefitZ = 1.959963984540054

// HoldoutOutcomeRow is one holdout assignment row as a HoldoutOutcomeReader reads it back: the run, its arm, one record
// its block named, when, and the run's own verified outcome and the models. IDs, an arm, an instant, a status and
// model ids: never content.
type HoldoutOutcomeRow struct {
	RunID        UUID
	Arm          HoldoutArm
	ExperienceID UUID
	AssignedAt   Instant
	// RunOutcome is the outcome status of the run's own record (the live record in exactly the row's scope whose
	// source run is the run; the earliest created, then the lowest ID); nil when there is none, or it is sealed.
	RunOutcome *VerificationStatus
	// RunModel is the model the run's own record names (RecordedModel); nil when none.
	RunModel *string
	// LessonModel is the model the named lesson's record names, in exactly the row's scope; nil when none.
	LessonModel *string
}

// HoldoutOutcomeCursor is where a page of outcome rows resumes: after this run and record.
type HoldoutOutcomeCursor struct {
	RunID        UUID
	ExperienceID UUID
}

// HoldoutOutcomeReader is a Store that reads the holdout's outcome rows (contract 1.9). Optional: EstimateBenefit
// over a store that is not one gives an untracked estimate. The PostgreSQL store and the in-memory store both are.
type HoldoutOutcomeReader interface {
	// QueryHoldoutOutcomes reads one page of the assignment rows in exactly scope, in (run, record) order, after the
	// cursor (from the start when nil), at most limit (HoldoutOutcomesMinLimit to HoldoutOutcomesMaxLimit), each
	// joined to its run's outcome and the models. It never writes. A scope outside bounds is ErrAuthorizationDenied
	// and reads nothing.
	QueryHoldoutOutcomes(ctx context.Context, bounds AuthorizationBounds, scope Scope, limit int,
		after *HoldoutOutcomeCursor) ([]HoldoutOutcomeRow, error)
}

// ValidateHoldoutOutcomesLimit refuses a page limit outside HoldoutOutcomesMinLimit to HoldoutOutcomesMaxLimit.
func ValidateHoldoutOutcomesLimit(limit int) error {
	if limit < HoldoutOutcomesMinLimit || limit > HoldoutOutcomesMaxLimit {
		return fmt.Errorf("the holdout outcomes limit must be from %d to %d, not %d", HoldoutOutcomesMinLimit,
			HoldoutOutcomesMaxLimit, limit)
	}
	return nil
}

// BenefitVerdict is what a comparison claims, if anything.
type BenefitVerdict string

// The verdicts.
const (
	// NoEvidence: either arm has fewer than the minimum verified runs, or the 95% interval includes zero. Not "no
	// effect": the data cannot yet support a claim.
	NoEvidence BenefitVerdict = "NoEvidence"
	// Helps: the whole 95% interval of the difference lies above zero.
	Helps BenefitVerdict = "Helps"
	// Hurts: the whole 95% interval of the difference lies below zero.
	Hurts BenefitVerdict = "Hurts"
)

// BenefitOutcome is how an estimate's read ended.
type BenefitOutcome string

// The outcomes.
const (
	// BenefitFound is an estimate over what was read (possibly nothing).
	BenefitFound BenefitOutcome = "Found"
	// BenefitDenied is a scope outside the caller's authorization: nothing was read, and the estimate is empty.
	BenefitDenied BenefitOutcome = "Denied"
)

// BenefitOptions decide a verdict.
type BenefitOptions struct {
	// MinimumRunsPerArm is the fewest verified runs (Verified or Failed) each arm needs before the verdict can be
	// anything but NoEvidence: DefaultMinimumRunsPerArm when 0; a negative value is refused.
	MinimumRunsPerArm int
	// SplitByModel also compares the runs per consuming model (ByConsumingModel) and each lesson's runs per (producing
	// model, consuming model) (LessonBenefit.ByModelPair). Off: both are nil.
	SplitByModel bool
}

// minimum is the effective minimum, or an error for a negative one.
func (o BenefitOptions) minimum() (int, error) {
	switch {
	case o.MinimumRunsPerArm < 0:
		return 0, fmt.Errorf("the minimum runs per arm must be at least 1, not %d", o.MinimumRunsPerArm)
	case o.MinimumRunsPerArm == 0:
		return DefaultMinimumRunsPerArm, nil
	}
	return o.MinimumRunsPerArm, nil
}

// BenefitArm is one arm's verified runs.
type BenefitArm struct {
	// Runs is how many runs in this arm have a verified outcome (Verified or Failed).
	Runs int
	// Successes is how many of them verified.
	Successes int
	// Rate is Successes / Runs; nil with no runs.
	Rate *float64
}

// BenefitComparison is the two arms compared.
type BenefitComparison struct {
	// Treatment is the runs given the lessons; Holdout the runs they were withheld from.
	Treatment BenefitArm
	Holdout   BenefitArm
	// Difference is the treatment rate minus the holdout rate, with its 95% Newcombe hybrid score interval Lower to
	// Upper; all three nil when either arm has no runs.
	Difference *float64
	Lower      *float64
	Upper      *float64
	Verdict    BenefitVerdict
	// Unverified are runs whose record's outcome is Unknown; Pending runs with no record (not finalized, or sealed);
	// MixedArm runs recorded in both arms. All are left out of the rates.
	Unverified int
	Pending    int
	MixedArm   int
}

// LessonBenefit is the comparison over the runs whose blocks named one lesson.
type LessonBenefit struct {
	ExperienceID UUID
	Comparison   BenefitComparison
	// ByModelPair is, with SplitByModel, this lesson's runs per (lesson model, run model), ordered ordinally; nil
	// otherwise.
	ByModelPair []ModelPairBenefit
}

// ModelBenefit is the comparison over the runs of one consuming model.
type ModelBenefit struct {
	Model      string
	Comparison BenefitComparison
}

// ModelPairBenefit is, for one lesson, the comparison over its runs of one consuming model, the lesson having been
// learned by one producing model.
type ModelPairBenefit struct {
	LessonModel string
	RunModel    string
	Comparison  BenefitComparison
}

// BenefitEstimate is what the holdout measured: overall, per lesson and, split, per model.
type BenefitEstimate struct {
	// Overall compares every assigned run.
	Overall BenefitComparison
	// Lessons has one comparison per lesson any row named, ordered by experience ID; never nil.
	Lessons []LessonBenefit
	// ByConsumingModel is, with SplitByModel, every run compared per run model, ordered ordinally; nil otherwise.
	ByConsumingModel []ModelBenefit
	// Tracked is whether the store reads the holdout at all; false: nothing was read, and the estimate is empty.
	Tracked bool
	// Outcome is BenefitFound, or BenefitDenied for a scope outside the authorization (and the estimate is empty).
	Outcome BenefitOutcome
	// MinimumRunsPerArm is the minimum the verdicts were decided with.
	MinimumRunsPerArm int
}

// EstimateBenefit estimates the lessons' benefit in exactly scope (the scope the blocks were built in), under the
// caller's authorization bounds, overall and per lesson (and per model, with SplitByModel). It reads through store's
// QueryHoldoutOutcomes HoldoutOutcomesMaxLimit rows a page, folding each page into running counts before the next, so
// one page is held at a time. It never writes.
//
// Invalid options are an error, before anything is read. A store that is not a HoldoutOutcomeReader gives an empty
// estimate with Tracked false; a scope outside bounds an empty one whose Outcome is BenefitDenied, never an error. A
// store that pages a run's rows apart is an error rather than counting the run twice. A run whose record is sealed
// (payload_version 2), which a contract 1.x port cannot read, counts as Pending.
//
// The bounds and scope must come from your own authentication: the estimate names lessons by ID and counts runs.
func EstimateBenefit(ctx context.Context, store Store, bounds AuthorizationBounds, scope Scope,
	options BenefitOptions) (BenefitEstimate, error) {
	return estimateBenefit(ctx, store, bounds, scope, options, nil)
}

// estimateBenefit is EstimateBenefit, counting only the rows assigned at or after from when it is set.
func estimateBenefit(ctx context.Context, store any, bounds AuthorizationBounds, scope Scope,
	options BenefitOptions, from *Instant) (BenefitEstimate, error) {
	accumulator, err := NewBenefitAccumulator(options)
	if err != nil {
		return BenefitEstimate{}, err
	}
	empty := accumulator.Estimate()
	reader, ok := store.(HoldoutOutcomeReader)
	if !ok {
		empty.Tracked = false
		return empty, nil
	}
	if !bounds.Permits(scope) {
		empty.Outcome = BenefitDenied
		return empty, nil
	}
	var after *HoldoutOutcomeCursor
	for {
		if err := ctx.Err(); err != nil {
			return BenefitEstimate{}, err
		}
		rows, err := reader.QueryHoldoutOutcomes(ctx, bounds, scope, HoldoutOutcomesMaxLimit, after)
		if errors.Is(err, ErrAuthorizationDenied) {
			empty.Outcome = BenefitDenied
			return empty, nil
		}
		if err != nil {
			return BenefitEstimate{}, err
		}
		for _, row := range rows {
			if from != nil && row.AssignedAt.Before(*from) {
				continue
			}
			if err := accumulator.Add(row); err != nil {
				return BenefitEstimate{}, err
			}
		}
		if len(rows) < HoldoutOutcomesMaxLimit {
			break
		}
		next := HoldoutOutcomeCursor{RunID: rows[len(rows)-1].RunID, ExperienceID: rows[len(rows)-1].ExperienceID}
		// A store whose page does not move on would loop forever.
		if after != nil && *after == next {
			break
		}
		after = &next
	}
	return accumulator.Estimate(), nil
}

// EstimateBenefitFromRows is the estimate over rows read any other way: pure, and the same for the same rows in any
// order. Invalid options are an error.
func EstimateBenefitFromRows(rows []HoldoutOutcomeRow, options BenefitOptions) (BenefitEstimate, error) {
	accumulator, err := NewBenefitAccumulator(options)
	if err != nil {
		return BenefitEstimate{}, err
	}
	sorted := slices.Clone(rows)
	sort.SliceStable(sorted, func(i, j int) bool {
		if c := compareUUIDs(sorted[i].RunID, sorted[j].RunID); c != 0 {
			return c < 0
		}
		return compareUUIDs(sorted[i].ExperienceID, sorted[j].ExperienceID) < 0
	})
	if err := accumulator.AddAll(sorted); err != nil {
		return BenefitEstimate{}, err
	}
	return accumulator.Estimate(), nil
}

// compareUUIDs orders UUIDs as PostgreSQL and .NET do: by their 16 bytes in RFC 4122 order.
func compareUUIDs(a, b UUID) int { return slices.Compare(a[:], b[:]) }

// BenefitAccumulator is the paged form of the estimate (.NET's ExperienceBenefitAccumulator): rows are added a run at
// a time, as a HoldoutOutcomeReader pages them, and each run is folded into running counts once the next run starts.
// It holds the IDs of the runs folded and one counter set per lesson and model, never the rows. Every row of a run
// must be added before any row of the next; within a run the rows may come in any order. It gives exactly what
// EstimateBenefitFromRows gives over the same rows. Not safe for concurrent use.
type BenefitAccumulator struct {
	minimum int
	split   bool
	folded  map[UUID]bool
	overall benefitCounts
	lessons map[UUID]*benefitCounts
	models  map[string]*benefitCounts
	pairs   map[UUID]map[[2]string]*benefitCounts

	open        bool
	openRun     UUID
	openArm     HoldoutArm
	openMixed   bool
	openOutcome *VerificationStatus
	openFrom    UUID
	openModel   *string
	openLessons map[UUID]*string
}

// NewBenefitAccumulator starts an empty accumulation; invalid options are an error.
func NewBenefitAccumulator(options BenefitOptions) (*BenefitAccumulator, error) {
	minimum, err := options.minimum()
	if err != nil {
		return nil, err
	}
	return &BenefitAccumulator{minimum: minimum, split: options.SplitByModel, folded: map[UUID]bool{},
		lessons: map[UUID]*benefitCounts{}, models: map[string]*benefitCounts{},
		pairs: map[UUID]map[[2]string]*benefitCounts{}, openLessons: map[UUID]*string{}}, nil
}

// ErrRunPagedApart is a row of a run whose rows were already folded: the run's rows were not contiguous.
var ErrRunPagedApart = errors.New("a run's rows were added again after another run's rows")

// Add adds one row. A row of a run already folded is ErrRunPagedApart, and nothing is counted for it.
func (a *BenefitAccumulator) Add(row HoldoutOutcomeRow) error {
	if !a.open || a.openRun != row.RunID {
		if a.folded[row.RunID] {
			return fmt.Errorf("%w: run %s", ErrRunPagedApart, row.RunID)
		}
		a.fold()
		a.open, a.openRun, a.openArm = true, row.RunID, row.Arm
	} else if a.openArm != row.Arm {
		a.openMixed = true
	}
	// The outcome the pure form reads a run with: the first carried one in record order.
	if row.RunOutcome != nil && (a.openOutcome == nil || compareUUIDs(row.ExperienceID, a.openFrom) < 0) {
		outcome := *row.RunOutcome
		a.openOutcome, a.openFrom = &outcome, row.ExperienceID
	}
	a.openModel = leastModel(a.openModel, row.RunModel)
	a.openLessons[row.ExperienceID] = leastModel(a.openLessons[row.ExperienceID], row.LessonModel)
	return nil
}

// AddAll adds every row, in order, stopping at the first error.
func (a *BenefitAccumulator) AddAll(rows []HoldoutOutcomeRow) error {
	for _, row := range rows {
		if err := a.Add(row); err != nil {
			return err
		}
	}
	return nil
}

// Estimate is the estimate over every row added so far (Tracked, Outcome BenefitFound). The run being added is folded
// first, so no further row of it may be added afterwards.
func (a *BenefitAccumulator) Estimate() BenefitEstimate {
	a.fold()
	estimate := BenefitEstimate{Overall: a.overall.compare(a.minimum), Lessons: []LessonBenefit{}, Tracked: true,
		Outcome: BenefitFound, MinimumRunsPerArm: a.minimum}
	ids := make([]UUID, 0, len(a.lessons))
	for id := range a.lessons {
		ids = append(ids, id)
	}
	slices.SortFunc(ids, compareUUIDs)
	for _, id := range ids {
		lesson := LessonBenefit{ExperienceID: id, Comparison: a.lessons[id].compare(a.minimum)}
		if a.split {
			lesson.ByModelPair = []ModelPairBenefit{}
			keys := make([][2]string, 0, len(a.pairs[id]))
			for key := range a.pairs[id] {
				keys = append(keys, key)
			}
			slices.SortFunc(keys, func(x, y [2]string) int {
				if c := utf16Compare(x[0], y[0]); c != 0 {
					return c
				}
				return utf16Compare(x[1], y[1])
			})
			for _, key := range keys {
				lesson.ByModelPair = append(lesson.ByModelPair, ModelPairBenefit{LessonModel: key[0], RunModel: key[1],
					Comparison: a.pairs[id][key].compare(a.minimum)})
			}
		}
		estimate.Lessons = append(estimate.Lessons, lesson)
	}
	if a.split {
		estimate.ByConsumingModel = []ModelBenefit{}
		models := make([]string, 0, len(a.models))
		for model := range a.models {
			models = append(models, model)
		}
		slices.SortFunc(models, utf16Compare)
		for _, model := range models {
			estimate.ByConsumingModel = append(estimate.ByConsumingModel,
				ModelBenefit{Model: model, Comparison: a.models[model].compare(a.minimum)})
		}
	}
	return estimate
}

// fold counts the open run, if any.
func (a *BenefitAccumulator) fold() {
	if !a.open {
		return
	}
	run := runClass{arm: a.openArm, mixed: a.openMixed, outcome: a.openOutcome}
	a.overall.count(run)
	runModel := modelOrUnknown(a.openModel)
	if a.split {
		counts(a.models, runModel).count(run)
	}
	for lesson, lessonModel := range a.openLessons {
		counts(a.lessons, lesson).count(run)
		if a.split {
			pairs := a.pairs[lesson]
			if pairs == nil {
				pairs = map[[2]string]*benefitCounts{}
				a.pairs[lesson] = pairs
			}
			counts(pairs, [2]string{modelOrUnknown(lessonModel), runModel}).count(run)
		}
	}
	a.folded[a.openRun] = true
	a.open, a.openMixed, a.openOutcome, a.openFrom, a.openModel = false, false, nil, UUID{}, nil
	a.openLessons = map[UUID]*string{}
}

func counts[K comparable](m map[K]*benefitCounts, key K) *benefitCounts {
	c := m[key]
	if c == nil {
		c = &benefitCounts{}
		m[key] = c
	}
	return c
}

func modelOrUnknown(model *string) string {
	if model == nil {
		return UnknownModel
	}
	return *model
}

// leastModel is, of the models a run's (or a lesson's) rows carry, the ordinal least, blanks ignored, so the answer
// never depends on the order the rows came in.
func leastModel(current, candidate *string) *string {
	if candidate == nil || isNullOrWhitespace(*candidate) {
		return current
	}
	if current == nil || utf16Compare(*candidate, *current) < 0 {
		model := *candidate
		return &model
	}
	return current
}

// runClass is one run: its arm (unless mixed) and outcome.
type runClass struct {
	arm     HoldoutArm
	mixed   bool
	outcome *VerificationStatus
}

// benefitCounts are one comparison's running counts.
type benefitCounts struct {
	treatmentRuns, treatmentSuccesses, holdoutRuns, holdoutSuccesses, unverified, pending, mixed int
}

func (c *benefitCounts) count(run runClass) {
	switch {
	case run.mixed:
		c.mixed++
		return
	case run.outcome == nil:
		c.pending++
		return
	case *run.outcome != VerificationVerified && *run.outcome != VerificationFailed:
		c.unverified++
		return
	}
	success := 0
	if *run.outcome == VerificationVerified {
		success = 1
	}
	if run.arm == HoldoutTreatment {
		c.treatmentRuns++
		c.treatmentSuccesses += success
	} else {
		c.holdoutRuns++
		c.holdoutSuccesses += success
	}
}

func (c *benefitCounts) compare(minimum int) BenefitComparison {
	return compareArms(c.treatmentRuns, c.treatmentSuccesses, c.holdoutRuns, c.holdoutSuccesses, minimum,
		c.unverified, c.pending, c.mixed)
}

// compareArms compares two arms' verified counts: the rates, the difference, its interval and the verdict.
func compareArms(treatmentRuns, treatmentSuccesses, holdoutRuns, holdoutSuccesses, minimum, unverified, pending,
	mixed int) BenefitComparison {
	comparison := BenefitComparison{Treatment: arm(treatmentRuns, treatmentSuccesses),
		Holdout: arm(holdoutRuns, holdoutSuccesses), Verdict: NoEvidence, Unverified: unverified, Pending: pending,
		MixedArm: mixed}
	if treatmentRuns == 0 || holdoutRuns == 0 {
		return comparison
	}
	difference, lower, upper := newcombe(treatmentSuccesses, treatmentRuns, holdoutSuccesses, holdoutRuns)
	comparison.Difference, comparison.Lower, comparison.Upper = &difference, &lower, &upper
	switch {
	case treatmentRuns < minimum || holdoutRuns < minimum:
	case lower > 0:
		comparison.Verdict = Helps
	case upper < 0:
		comparison.Verdict = Hurts
	}
	return comparison
}

func arm(runs, successes int) BenefitArm {
	a := BenefitArm{Runs: runs, Successes: successes}
	if runs > 0 {
		rate := float64(successes) / float64(runs)
		a.Rate = &rate
	}
	return a
}

// wilson is the Wilson score interval for successes of n (n > 0), clamped to [0, 1], in .NET's order of operations.
// Every product is rounded on its own (float64 conversions), so no multiply-add is fused.
func wilson(successes, n int) (float64, float64) {
	nf := float64(n)
	p := float64(successes) / nf
	z := float64(BenefitZ)
	z2 := float64(z * z)
	denominator := 1 + z2/nf
	centre := (p + z2/float64(2*nf)) / denominator
	variance := float64(p*(1-p))/nf + z2/float64(float64(4*nf)*nf)
	half := float64(z*math.Sqrt(variance)) / denominator
	return math.Max(0, centre-half), math.Min(1, centre+half)
}

// newcombe is p1 - p2 and Newcombe's hybrid score interval for it (both arms non-empty), clamped to [-1, 1].
func newcombe(successes1, n1, successes2, n2 int) (float64, float64, float64) {
	p1 := float64(successes1) / float64(n1)
	p2 := float64(successes2) / float64(n2)
	l1, u1 := wilson(successes1, n1)
	l2, u2 := wilson(successes2, n2)
	d := p1 - p2
	lower := d - math.Sqrt(float64((p1-l1)*(p1-l1))+float64((u2-p2)*(u2-p2)))
	upper := d + math.Sqrt(float64((u1-p1)*(u1-p1))+float64((p2-l2)*(p2-l2)))
	return d, math.Max(-1, lower), math.Min(1, upper)
}
