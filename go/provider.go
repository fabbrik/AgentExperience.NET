package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"unicode/utf16"
)

// Measuring another memory provider (contract 1.11): a context provider that is not this library (Mem0, Zep, Letta,
// the host's own) is put under the same holdout the lessons run under, keyed by a host-chosen provider key. On a
// deterministic share of captured runs its context is withheld; each such run's arm is recorded, and
// EstimateProviderBenefit compares the arms' verified outcomes, exactly as .NET's
// AgentExperienceBenefit.EstimateProviderAsync does. The provider's items are opaque: only the arm is recorded.

// MaxProviderKeyLength is the longest permitted provider key: .NET's ProviderHoldoutAssignment.MaxProviderKeyLength.
const MaxProviderKeyLength = 100

// ValidProviderKey reports whether key is a valid provider key: 1 to MaxProviderKeyLength characters, each an ASCII
// letter or digit, '.', '_' or '-'.
func ValidProviderKey(key string) bool {
	if key == "" || len(key) > MaxProviderKeyLength {
		return false
	}
	for i := 0; i < len(key); i++ {
		c := key[i]
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-') {
			return false
		}
	}
	return true
}

// ValidateProviderKey is nil for a valid provider key (ValidProviderKey) and an error naming the rule otherwise.
func ValidateProviderKey(key string) error {
	if !ValidProviderKey(key) {
		return fmt.Errorf("the provider key must be 1 to %d characters of A-Z, a-z, 0-9, '.', '_' and '-'",
			MaxProviderKeyLength)
	}
	return nil
}

// ProviderHoldoutSalt is the salt a provider's arms are drawn with (.NET's
// ExperienceHoldoutContextProvider.ProviderSalt): the key's length in UTF-16 code units, in decimal, then ':', the
// key, '/', and the holdout's salt. The length prefix gives every (key, salt) pair its own salt, so each provider is
// randomised independently of the lessons and of every other provider. Pure; any key is derived, valid or not.
func ProviderHoldoutSalt(providerKey, salt string) string {
	units := 0
	for _, r := range providerKey {
		if n := utf16.RuneLen(r); n > 0 {
			units += n
		} else {
			units++ // a rune UTF-16 cannot encode is written as U+FFFD, one unit
		}
	}
	return strconv.Itoa(units) + ":" + providerKey + "/" + salt
}

// AssignProviderHoldout is a run's arm under a provider key: AssignHoldout over the run and the provider salt.
func AssignProviderHoldout(runID UUID, providerKey, salt string, rate float64) HoldoutArm {
	return AssignHoldout(runID, ProviderHoldoutSalt(providerKey, salt), rate)
}

// ProviderHoldoutAssignment is one captured run's arm for another memory provider, as a ProviderHoldoutRecorder
// records it: the run, the host's key for the provider, the arm, and when it was decided. No item, no content.
type ProviderHoldoutAssignment struct {
	RunID       UUID
	ProviderKey string
	Arm         HoldoutArm
	AssignedAt  Instant
}

// Validate refuses a nil run ID, a provider key ValidProviderKey refuses, and an arm other than HoldoutTreatment or
// HoldoutWithheld.
func (a ProviderHoldoutAssignment) Validate() error {
	if a.RunID.IsZero() {
		return errors.New("a provider holdout assignment's run ID must be a non-nil UUID")
	}
	if err := ValidateProviderKey(a.ProviderKey); err != nil {
		return err
	}
	if a.Arm != HoldoutTreatment && a.Arm != HoldoutWithheld {
		return fmt.Errorf("a provider holdout assignment's arm must be %s or %s", HoldoutTreatment, HoldoutWithheld)
	}
	return nil
}

// ProviderHoldoutRecorder is a Store that records provider holdout assignments (contract 1.11). Optional: the agent
// integration records an assignment only through a store that is one, and reports NotTracked for one that is not. The
// PostgreSQL store and the in-memory store both are.
type ProviderHoldoutRecorder interface {
	// RecordProviderHoldoutAssignment records a run's arm under a key in scope, within bounds: one row per tenant, run
	// and key, its scope exactly scope; a row already there for them is kept as it is, whatever its scope; the
	// instant, truncated to whole microseconds, is clamped to the store's clock; nothing is written for a run whose
	// own records in the tenant are all erased. A scope outside bounds is ErrAuthorizationDenied.
	RecordProviderHoldoutAssignment(ctx context.Context, bounds AuthorizationBounds, scope Scope,
		assignment ProviderHoldoutAssignment) error
}

// ProviderHoldoutOutcomeRow is one provider holdout row as a ProviderHoldoutOutcomeReader reads it back: the run,
// the key, its arm, when, and the run's own verified outcome. IDs, a key, an arm, an instant and a status: never
// content.
type ProviderHoldoutOutcomeRow struct {
	RunID       UUID
	ProviderKey string
	Arm         HoldoutArm
	AssignedAt  Instant
	// RunOutcome is the outcome status of the run's own record (the live record in exactly the row's scope whose
	// source run is the run; the earliest created, then the lowest ID); nil when there is none, or it is sealed.
	RunOutcome *VerificationStatus
}

// ProviderHoldoutOutcomeReader is a Store that reads one provider's holdout rows (contract 1.11). Optional:
// EstimateProviderBenefit over a store that is not one gives an untracked estimate. The PostgreSQL store and the
// in-memory store both are.
type ProviderHoldoutOutcomeReader interface {
	// QueryProviderHoldoutOutcomes reads one page of providerKey's rows in exactly scope, in run order (by the run
	// IDs' bytes), after the run afterRunID (from the start when nil), at most limit (HoldoutOutcomesMinLimit to
	// HoldoutOutcomesMaxLimit), each joined to its run's outcome. It never writes. A scope outside bounds is
	// ErrAuthorizationDenied and reads nothing.
	QueryProviderHoldoutOutcomes(ctx context.Context, bounds AuthorizationBounds, scope Scope, providerKey string,
		limit int, afterRunID *UUID) ([]ProviderHoldoutOutcomeRow, error)
}

// ProviderBenefitEstimate is what a provider's holdout measured (.NET's ExperienceProviderBenefitEstimate): one
// comparison over every assigned run. The provider's items are opaque, so there is no per-item comparison.
type ProviderBenefitEstimate struct {
	// ProviderKey is the key read.
	ProviderKey string
	// Overall compares every assigned run, exactly as BenefitEstimate.Overall compares the lessons' runs.
	Overall BenefitComparison
	// MinimumRunsPerArm is the minimum the verdict was decided with.
	MinimumRunsPerArm int
	// Tracked is whether the store reads provider holdout rows at all; false: nothing was read, the estimate is empty.
	Tracked bool
	// Outcome is BenefitFound, or BenefitDenied for a scope outside the authorization (and the estimate is empty).
	Outcome BenefitOutcome
}

// EstimateProviderBenefit estimates whether another memory provider helps in exactly scope (the scope the runs were
// captured in), under the caller's authorization bounds: the runs a wrapper recorded under providerKey, compared by
// arm exactly as EstimateBenefit's Overall compares the lessons'. It reads through store's
// QueryProviderHoldoutOutcomes HoldoutOutcomesMaxLimit rows a page, folding each page into a BenefitAccumulator
// before the next (each row as a run with no lesson). It never writes.
//
// A key ValidProviderKey refuses is an error, then invalid options (BenefitOptions.MinimumRunsPerArm; SplitByModel is
// ignored), before anything is read. A store that is not a ProviderHoldoutOutcomeReader gives an empty estimate with
// Tracked false; a scope outside bounds an empty one whose Outcome is BenefitDenied, never an error. A run whose
// record is sealed (payload_version 2) counts as Pending.
func EstimateProviderBenefit(ctx context.Context, store Store, bounds AuthorizationBounds, scope Scope,
	providerKey string, options BenefitOptions) (ProviderBenefitEstimate, error) {
	return estimateProviderBenefit(ctx, store, bounds, scope, providerKey, options, HoldoutOutcomesMaxLimit)
}

// estimateProviderBenefit is EstimateProviderBenefit reading pageSize rows a page.
func estimateProviderBenefit(ctx context.Context, store any, bounds AuthorizationBounds, scope Scope,
	providerKey string, options BenefitOptions, pageSize int) (ProviderBenefitEstimate, error) {
	if err := ValidateProviderKey(providerKey); err != nil {
		return ProviderBenefitEstimate{}, err
	}
	options.SplitByModel = false
	accumulator, err := NewBenefitAccumulator(options)
	if err != nil {
		return ProviderBenefitEstimate{}, err
	}
	base := accumulator.Estimate()
	empty := ProviderBenefitEstimate{ProviderKey: providerKey, Overall: base.Overall,
		MinimumRunsPerArm: base.MinimumRunsPerArm, Tracked: true, Outcome: BenefitFound}
	reader, ok := store.(ProviderHoldoutOutcomeReader)
	if !ok {
		empty.Tracked = false
		return empty, nil
	}
	if !bounds.Permits(scope) {
		empty.Outcome = BenefitDenied
		return empty, nil
	}
	var after *UUID
	for {
		if err := ctx.Err(); err != nil {
			return ProviderBenefitEstimate{}, err
		}
		rows, err := reader.QueryProviderHoldoutOutcomes(ctx, bounds, scope, providerKey, pageSize, after)
		if errors.Is(err, ErrAuthorizationDenied) {
			empty.Outcome = BenefitDenied
			return empty, nil
		}
		if err != nil {
			return ProviderBenefitEstimate{}, err
		}
		// Each run is one row under a key, so the run's row stands for the run, with no lesson (the nil UUID, as .NET).
		for _, row := range rows {
			if err := accumulator.Add(HoldoutOutcomeRow{RunID: row.RunID, Arm: row.Arm, AssignedAt: row.AssignedAt,
				RunOutcome: row.RunOutcome}); err != nil {
				return ProviderBenefitEstimate{}, err
			}
		}
		if len(rows) < pageSize {
			break
		}
		last := rows[len(rows)-1].RunID
		// A store whose page does not move on would loop forever.
		if after != nil && *after == last {
			break
		}
		after = &last
	}
	result := empty
	result.Overall = accumulator.Estimate().Overall
	return result, nil
}
