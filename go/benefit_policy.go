package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strconv"
	"time"
)

// Acting on the benefit estimate (contract 1.10): .NET's ExperienceBenefitPolicy. A lesson the injection holdout
// measured Hurts is not injected, a scope whose lessons together measure Hurts gets none, and with
// PreferMeasuredHelpful the lessons measured Helps go first. This file holds the policy's options, what one estimate
// means for an invocation (BenefitView), and the selection the context provider runs (SelectCandidates); the maf
// module keeps the estimate in a background cache, never on the request path.

// The benefit policy's defaults: .NET's.
const (
	DefaultBenefitWindow          = 30 * 24 * time.Hour
	DefaultBenefitRefreshInterval = 10 * time.Minute
	DefaultBenefitRefreshTimeout  = 30 * time.Second
	// MaxBenefitRefreshTimeout is the largest RefreshTimeout: one day.
	MaxBenefitRefreshTimeout = 24 * time.Hour
)

// BenefitPolicy is acting on measured benefit (.NET's ExperienceBenefitPolicy). The zero value is off: nothing is
// read, cached, reordered or omitted.
//
// Go cannot tell an unset duration or minimum from an explicit zero, so a zero Window, RefreshInterval, RefreshTimeout
// or MinimumRunsPerArm means its default; a negative one, or a RefreshTimeout over MaxBenefitRefreshTimeout, is
// refused (Validate), even with the policy off.
type BenefitPolicy struct {
	// Enabled is whether injection acts on measured benefit at all. Off by default.
	Enabled bool
	// PreferMeasuredHelpful moves the lessons measured Helps ahead of the others, each group keeping its rank order,
	// before the record limit. Off by default.
	PreferMeasuredHelpful bool
	// PerModel acts on the verdicts for the invoking agent's own model rather than the pooled ones: the estimate is
	// split by model, and where that model's group has no evidence (or the model was never measured, or is unknown)
	// the pooled verdict is used. Off by default.
	PerModel bool
	// Window is how far back, from each refresh, holdout assignments count; DefaultBenefitWindow when 0.
	Window time.Duration
	// MinimumRunsPerArm is the estimator's minimum; DefaultMinimumRunsPerArm when 0.
	MinimumRunsPerArm int
	// RefreshInterval is how old a cached estimate may grow before an invocation starts a refresh;
	// DefaultBenefitRefreshInterval when 0.
	RefreshInterval time.Duration
	// RefreshTimeout is how long one refresh may take before it is abandoned and the previous estimate kept;
	// DefaultBenefitRefreshTimeout when 0, at most MaxBenefitRefreshTimeout.
	RefreshTimeout time.Duration
}

// Validate refuses a negative window, interval, timeout or minimum, and a timeout over MaxBenefitRefreshTimeout.
func (p BenefitPolicy) Validate() error {
	switch {
	case p.Window < 0:
		return errors.New("the benefit policy's window must not be negative")
	case p.RefreshInterval < 0:
		return errors.New("the benefit policy's refresh interval must not be negative")
	case p.RefreshTimeout < 0 || p.RefreshTimeout > MaxBenefitRefreshTimeout:
		return fmt.Errorf("the benefit policy's refresh timeout must not be negative or above %v", MaxBenefitRefreshTimeout)
	case p.MinimumRunsPerArm < 0:
		return errors.New("the benefit policy's minimum runs per arm must not be negative")
	}
	return nil
}

// WithDefaults is the policy with every zero duration and minimum replaced by its default.
func (p BenefitPolicy) WithDefaults() BenefitPolicy {
	if p.Window == 0 {
		p.Window = DefaultBenefitWindow
	}
	if p.RefreshInterval == 0 {
		p.RefreshInterval = DefaultBenefitRefreshInterval
	}
	if p.RefreshTimeout == 0 {
		p.RefreshTimeout = DefaultBenefitRefreshTimeout
	}
	if p.MinimumRunsPerArm == 0 {
		p.MinimumRunsPerArm = DefaultMinimumRunsPerArm
	}
	return p
}

// EstimatorOptions are the estimator's options the policy decides verdicts with: its minimum, split by model when
// PerModel.
func (p BenefitPolicy) EstimatorOptions() BenefitOptions {
	return BenefitOptions{MinimumRunsPerArm: p.WithDefaults().MinimumRunsPerArm, SplitByModel: p.PerModel}
}

// AssignedFrom is the earliest assignment instant the policy counts at now: now less the window, or the earliest
// instant when the window reaches past it.
func (p BenefitPolicy) AssignedFrom(now Instant) Instant {
	window := int64(p.WithDefaults().Window / 100)
	if now.Ticks() <= window {
		return Instant{}
	}
	return Instant{now.Ticks() - window}
}

// EstimateBenefitAssignedFrom is EstimateBenefit counting only the rows assigned at or after from: every page is
// still read, and the filter applied to each page's rows before they are folded (.NET's EstimateFromStoreAsync).
func EstimateBenefitAssignedFrom(ctx context.Context, store Store, bounds AuthorizationBounds, scope Scope,
	options BenefitOptions, from Instant) (BenefitEstimate, error) {
	return estimateBenefit(ctx, store, bounds, scope, options, &from)
}

// ---- The view ---------------------------------------------------------------------------------------------------

// BenefitView is what one estimate means for one invocation (.NET's BenefitView): whether the scope's lessons together
// hurt, and which lessons hurt or help. A nil view changes nothing.
type BenefitView struct {
	// ScopeHurts is whether the scope's verdict is Hurts: then every record is omitted.
	ScopeHurts bool
	// Hurts and Helps are the lessons measured Hurts and Helps.
	Hurts map[UUID]bool
	Helps map[UUID]bool
}

// Harmful is whether the record is omitted as measured harmful: the scope hurts, or the lesson does.
func (v *BenefitView) Harmful(id UUID) bool { return v != nil && (v.ScopeHurts || v.Hurts[id]) }

func (v *BenefitView) acts() bool { return v.ScopeHurts || len(v.Hurts) > 0 || len(v.Helps) > 0 }

// usable is whether an estimate may be acted on: tracked and found.
func usable(estimate BenefitEstimate) bool {
	return estimate.Tracked && estimate.Outcome == BenefitFound
}

// buildView is the view for model's runs (nil: the pooled view), or nil when it changes nothing.
func buildView(estimate BenefitEstimate, model *string) *BenefitView {
	view := &BenefitView{Hurts: map[UUID]bool{}, Helps: map[UUID]bool{}}
	for _, lesson := range estimate.Lessons {
		verdict := lesson.Comparison.Verdict
		if model != nil {
			if own, ok := verdictForModel(lesson.ByModelPair, *model, estimate.MinimumRunsPerArm); ok && own != NoEvidence {
				verdict = own
			}
		}
		switch verdict {
		case Hurts:
			view.Hurts[lesson.ExperienceID] = true
		case Helps:
			view.Helps[lesson.ExperienceID] = true
		}
	}
	scope := estimate.Overall.Verdict
	if model != nil {
		for _, group := range estimate.ByConsumingModel {
			if group.Model == *model {
				if group.Comparison.Verdict != NoEvidence {
					scope = group.Comparison.Verdict
				}
				break
			}
		}
	}
	view.ScopeHurts = scope == Hurts
	if !view.acts() {
		return nil
	}
	return view
}

// verdictForModel is one lesson's verdict for model's runs: its pairs whose run model is model, their counts summed and
// decided as the estimator decides any group; false when there are none.
func verdictForModel(pairs []ModelPairBenefit, model string, minimum int) (BenefitVerdict, bool) {
	var treatmentRuns, treatmentSuccesses, holdoutRuns, holdoutSuccesses int
	found := false
	for _, pair := range pairs {
		if pair.RunModel != model {
			continue
		}
		found = true
		treatmentRuns += pair.Comparison.Treatment.Runs
		treatmentSuccesses += pair.Comparison.Treatment.Successes
		holdoutRuns += pair.Comparison.Holdout.Runs
		holdoutSuccesses += pair.Comparison.Holdout.Successes
	}
	if !found {
		return "", false
	}
	return compareArms(treatmentRuns, treatmentSuccesses, holdoutRuns, holdoutSuccesses, max(1, minimum), 0, 0, 0).Verdict, true
}

// BenefitViews are one estimate's views (.NET's BenefitViews): the pooled one, and one per consuming model the estimate
// measured (never the "unknown" group, which is no agent's own).
type BenefitViews struct {
	pooled  *BenefitView
	byModel map[string]*BenefitView
}

// NewBenefitViews are the views of estimate, or nil when it must not be acted on (not tracked, or not found).
func NewBenefitViews(estimate BenefitEstimate) *BenefitViews {
	if !usable(estimate) {
		return nil
	}
	views := &BenefitViews{pooled: buildView(estimate, nil), byModel: map[string]*BenefitView{}}
	for _, group := range estimate.ByConsumingModel {
		if group.Model != UnknownModel {
			model := group.Model
			views.byModel[model] = buildView(estimate, &model)
		}
	}
	return views
}

// For is the view for model, or the pooled one when model is nil or was not measured. A nil receiver answers nil.
func (v *BenefitViews) For(model *string) *BenefitView {
	if v == nil {
		return nil
	}
	if model != nil {
		if view, ok := v.byModel[*model]; ok {
			return view
		}
	}
	return v.pooled
}

// ---- Selection --------------------------------------------------------------------------------------------------

// OmissionReason is why a ranked record was not injected (.NET's InjectionOmissionReason, the reasons this port has).
type OmissionReason string

// The omission reasons.
const (
	// OmittedMeasuredHarmful is a lesson measured Hurts, or every record of a scope whose lessons together hurt.
	OmittedMeasuredHarmful OmissionReason = "MeasuredHarmful"
	// OmittedModelAuthored is a model-authored lesson the settings exclude. It carries no detail.
	OmittedModelAuthored OmissionReason = "ModelAuthored"
	// OmittedOverRecordLimit is a record ranked past the record limit.
	OmittedOverRecordLimit OmissionReason = "OverRecordLimit"
	// OmittedOverByteBudget is a record that did not fit in the block's byte budget.
	OmittedOverByteBudget OmissionReason = "OverByteBudget"
	// OmittedToolUnavailable is a record whose approach calls a tool the receiving agent lacks (contract 1.13). It
	// carries no detail.
	OmittedToolUnavailable OmissionReason = "ToolUnavailable"
	// OmittedRiskClassExceeded is a record whose approach calls a tool above the receiving agent's maximum risk class
	// (contract 1.13). It carries no detail.
	OmittedRiskClassExceeded OmissionReason = "RiskClassExceeded"
	// OmittedHostDenied is a record the host's injection decision denied (contract 1.13).
	OmittedHostDenied OmissionReason = "HostDenied"
)

// The details of a record omitted as measured harmful: .NET's, byte for byte.
const (
	MeasuredHarmfulDetail      = "The injection holdout measured this lesson as hurting: runs given it verified less often than runs it was withheld from."
	ScopeMeasuredHarmfulDetail = "The injection holdout measured this scope's lessons together as hurting, so none is injected."
)

// OmittedRecord is one ranked record that was not injected, and why: never record content.
type OmittedRecord struct {
	ExperienceID UUID
	Reason       OmissionReason
	// Detail is the omission's detail; nil when it carries none.
	Detail *string
}

// SelectOptions are what a selection is bounded by.
type SelectOptions struct {
	// MaxRecords is the record limit; DefaultMaxRecords when 0.
	MaxRecords int
	// RequestLimit is the request's own limit; 0 (or less) for none. Records past it are backfill: selected while a
	// slot is left, and never reported as omitted.
	RequestLimit int
	// ExcludeModelAuthored omits model-authored lessons (ModelAuthored).
	ExcludeModelAuthored bool
	// PreferMeasuredHelpful moves the lessons view helps ahead of the others (within the request's window and within
	// the backfill, separately).
	PreferMeasuredHelpful bool
	// Gate is the capability gate for this invocation (contract 1.13; CapabilityGate.ForInvocation); nil, or one that
	// cannot reject, gates nothing.
	Gate *CapabilityGate
}

// SelectCandidates is .NET's ExperienceContextProvider selection (PreferHelpful, then Select) without a session: the
// records to render, in order, and the omissions in the order .NET reports them. For each record, the first rule that
// applies: MeasuredHarmful, ModelAuthored, the capability gate (contract 1.13), then the record limit; a record omitted
// by the first three takes no slot. view is the cached benefit view (nil for none, which changes nothing).
func SelectCandidates(ranked []RankedRecord, view *BenefitView, options SelectOptions) ([]RankedRecord, []OmittedRecord) {
	ordered := ranked
	if view != nil && options.PreferMeasuredHelpful {
		ordered = preferHelpful(ranked, view, options.RequestLimit)
	}
	maxRecords := options.MaxRecords
	if maxRecords == 0 {
		maxRecords = DefaultMaxRecords
	}
	limit, reported := maxRecords, len(ordered)+1
	if options.RequestLimit > 0 {
		limit, reported = min(limit, options.RequestLimit), options.RequestLimit
	}
	selected := make([]RankedRecord, 0, min(len(ordered), limit))
	omitted := []OmittedRecord{}
	gate := options.Gate
	if !gate.CanReject() {
		gate = nil
	}
	for index := range ordered {
		candidate := &ordered[index]
		id := candidate.Record.ExperienceID
		omit := func(reason OmissionReason, detail *string) {
			// Past the host's own window: backfill only, never reported.
			if index < reported {
				omitted = append(omitted, OmittedRecord{ExperienceID: id, Reason: reason, Detail: detail})
			}
		}
		if view.Harmful(id) {
			detail := MeasuredHarmfulDetail
			if view.ScopeHurts {
				detail = ScopeMeasuredHarmfulDetail
			}
			omit(OmittedMeasuredHarmful, &detail)
			continue
		}
		if options.ExcludeModelAuthored && IsModelAuthored(candidate.Record.Reflection) {
			omit(OmittedModelAuthored, nil)
			continue
		}
		if gate != nil {
			if reason := gate.Check(&candidate.Record); reason != "" {
				omit(reason, nil)
				continue
			}
		}
		if len(selected) < limit {
			selected = append(selected, *candidate)
			continue
		}
		detail := "Ranked " + strconv.Itoa(index+1) + " of " + strconv.Itoa(len(ordered)) + ", beyond the limit of " +
			strconv.Itoa(limit) + " records."
		omit(OmittedOverRecordLimit, &detail)
	}
	return selected, omitted
}

// preferHelpful is ranked with the lessons view helps moved ahead, each group in rank order; the request's window and
// the backfill past it are partitioned separately, so a backfill record never moves into the window.
func preferHelpful(ranked []RankedRecord, view *BenefitView, requestLimit int) []RankedRecord {
	if len(view.Helps) == 0 {
		return ranked
	}
	window := len(ranked)
	if requestLimit > 0 {
		window = min(requestLimit, len(ranked))
	}
	reordered := make([]RankedRecord, 0, len(ranked))
	partition := func(part []RankedRecord) {
		for _, r := range part {
			if view.Helps[r.Record.ExperienceID] {
				reordered = append(reordered, r)
			}
		}
		for _, r := range part {
			if !view.Helps[r.Record.ExperienceID] {
				reordered = append(reordered, r)
			}
		}
	}
	partition(ranked[:window])
	partition(ranked[window:])
	return reordered
}

// ByteBudgetOmissions are the records a block rendered from selected records left out for its byte budget
// (OverByteBudget, with .NET's detail), in order. The block must have been rendered from records SelectCandidates
// selected with the same MaxRecords, so the block's record limit omitted none.
func ByteBudgetOmissions(block Block, options BlockOptions) []OmittedRecord {
	maxBytes := options.withDefaults().MaxBytes
	omitted := make([]OmittedRecord, 0, len(block.Omitted))
	for _, record := range block.Omitted {
		detail := "The record did not fit in the remaining part of the " + strconv.Itoa(maxBytes) +
			"-byte budget, and a record is never cut to fit."
		omitted = append(omitted, OmittedRecord{ExperienceID: record.ExperienceID, Reason: OmittedOverByteBudget,
			Detail: &detail})
	}
	return slices.Clip(omitted)
}
