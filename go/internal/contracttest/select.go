package contracttest

import (
	"encoding/json"
	"fmt"
	"path/filepath"
	"strconv"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.10's benefit-select fixtures ---------------------------------------------------------------------

// BenefitSelectFamily is the benefit-select fixture family's directory name.
const BenefitSelectFamily = "benefit-select"

// BenefitSelectCase is one benefit-select case: records.json, estimate.json, options.json and expected.json.
type BenefitSelectCase struct {
	Name     string
	Ranked   []ae.RankedRecord
	Estimate ae.BenefitEstimate
	// The policy and the invocation.
	Enabled, PreferMeasuredHelpful, PerModel, ExcludeModelAuthored bool
	Model                                                          *string
	RequestLimit, MaxRecords, MaxBytes                             int
	Expected                                                       *ae.Object
}

// LoadBenefitSelectCase reads a benefit-select case.
func LoadBenefitSelectCase(name string) (*BenefitSelectCase, error) {
	dir := filepath.Join(Fixtures, BenefitSelectFamily, name)
	ranked, err := ae.RankedFromJSON(MustReadJSON(filepath.Join(dir, "records.json")))
	if err != nil {
		return nil, fmt.Errorf("%s: %w", name, err)
	}
	options := object(MustReadJSON(filepath.Join(dir, "options.json")))
	c := &BenefitSelectCase{Name: name, Ranked: ranked,
		Estimate: EstimateOf(MustReadJSON(filepath.Join(dir, "estimate.json"))),
		Expected: object(MustReadJSON(filepath.Join(dir, "expected.json"))),
		Model:    optionalText(get(options, "model")), MaxRecords: int(integer(get(options, "maxRecords"))),
		MaxBytes: int(integer(get(options, "maxBytes")))}
	c.Enabled, _ = get(options, "enabled").(bool)
	c.PreferMeasuredHelpful, _ = get(options, "preferMeasuredHelpful").(bool)
	c.PerModel, _ = get(options, "perModel").(bool)
	c.ExcludeModelAuthored, _ = get(options, "excludeModelAuthored").(bool)
	if limit := get(options, "requestLimit"); limit != nil {
		c.RequestLimit = int(integer(limit))
	}
	return c, nil
}

// Run is what the port selects and renders for the case: the view the cache would hand an invocation of the case's
// model (none with the policy off), SelectCandidates, then the block's byte budget. It answers expected.json's form.
func (c *BenefitSelectCase) Run() (*ae.Object, error) {
	var view *ae.BenefitView
	if c.Enabled {
		model := c.Model
		if !c.PerModel {
			model = nil
		}
		view = ae.NewBenefitViews(c.Estimate).For(model)
	}
	selected, omitted := ae.SelectCandidates(c.Ranked, view, ae.SelectOptions{MaxRecords: c.MaxRecords,
		RequestLimit: c.RequestLimit, ExcludeModelAuthored: c.ExcludeModelAuthored,
		PreferMeasuredHelpful: c.PreferMeasuredHelpful})
	options := ae.BlockOptions{MaxRecords: c.MaxRecords, MaxBytes: c.MaxBytes}
	block, err := ae.RenderBlock(selected, options)
	if err != nil {
		return nil, err
	}
	omitted = append(omitted, ae.ByteBudgetOmissions(block, options)...)
	return SelectionJSON(selected, block.Included, omitted), nil
}

// SelectionJSON is a selection in expected.json's form: selected, injected and omitted.
func SelectionJSON(selected []ae.RankedRecord, injected []ae.ExperienceRecord, omitted []ae.OmittedRecord) *ae.Object {
	selectedIDs, injectedIDs, omissions := []any{}, []any{}, []any{}
	for _, r := range selected {
		selectedIDs = append(selectedIDs, r.Record.ExperienceID.String())
	}
	for _, r := range injected {
		injectedIDs = append(injectedIDs, r.ExperienceID.String())
	}
	for _, o := range omitted {
		omissions = append(omissions, ae.NewObject(ae.Member{Key: "experienceId", Value: o.ExperienceID.String()},
			ae.Member{Key: "reason", Value: string(o.Reason)}, ae.Member{Key: "detail", Value: optional(o.Detail)}))
	}
	return ae.NewObject(ae.Member{Key: "selected", Value: selectedIDs},
		ae.Member{Key: "injected", Value: injectedIDs}, ae.Member{Key: "omitted", Value: omissions})
}

// EstimateOf reads an estimate in the benefit-estimate expected.json form (tracked and found, unless the value says
// otherwise with "tracked" and "outcome").
func EstimateOf(value any) ae.BenefitEstimate {
	o := object(value)
	estimate := ae.BenefitEstimate{Tracked: true, Outcome: ae.BenefitFound,
		MinimumRunsPerArm: int(integer(get(o, "minimumRunsPerArm"))), Overall: ComparisonOf(get(o, "overall")),
		Lessons: []ae.LessonBenefit{}}
	if tracked, ok := get(o, "tracked").(bool); ok {
		estimate.Tracked = tracked
	}
	if outcome, ok := get(o, "outcome").(string); ok {
		estimate.Outcome = ae.BenefitOutcome(outcome)
	}
	for _, item := range list(get(o, "lessons")) {
		l := object(item)
		lesson := ae.LessonBenefit{ExperienceID: must(ae.ParseUUID(text(get(l, "experienceId")))),
			Comparison: ComparisonOf(get(l, "comparison"))}
		if pairs := get(l, "byModelPair"); pairs != nil {
			lesson.ByModelPair = []ae.ModelPairBenefit{}
			for _, p := range list(pairs) {
				pair := object(p)
				lesson.ByModelPair = append(lesson.ByModelPair, ae.ModelPairBenefit{
					LessonModel: text(get(pair, "lessonModel")), RunModel: text(get(pair, "runModel")),
					Comparison: ComparisonOf(get(pair, "comparison"))})
			}
		}
		estimate.Lessons = append(estimate.Lessons, lesson)
	}
	if models := get(o, "byConsumingModel"); models != nil {
		estimate.ByConsumingModel = []ae.ModelBenefit{}
		for _, item := range list(models) {
			m := object(item)
			estimate.ByConsumingModel = append(estimate.ByConsumingModel, ae.ModelBenefit{Model: text(get(m, "model")),
				Comparison: ComparisonOf(get(m, "comparison"))})
		}
	}
	return estimate
}

// ComparisonOf reads one comparison in the fixtures' form.
func ComparisonOf(value any) ae.BenefitComparison {
	o := object(value)
	arm := func(value any) ae.BenefitArm {
		a := object(value)
		return ae.BenefitArm{Runs: int(integer(get(a, "runs"))), Successes: int(integer(get(a, "successes"))),
			Rate: optionalFloat(get(a, "rate"))}
	}
	return ae.BenefitComparison{Treatment: arm(get(o, "treatment")), Holdout: arm(get(o, "holdout")),
		Difference: optionalFloat(get(o, "difference")), Lower: optionalFloat(get(o, "lower")),
		Upper: optionalFloat(get(o, "upper")), Verdict: ae.BenefitVerdict(text(get(o, "verdict"))),
		Unverified: int(integer(get(o, "unverified"))), Pending: int(integer(get(o, "pending"))),
		MixedArm: int(integer(get(o, "mixedArm")))}
}

func optionalFloat(value any) *float64 {
	n, ok := value.(json.Number)
	if !ok {
		return nil
	}
	f := must(strconv.ParseFloat(string(n), 64))
	return &f
}
