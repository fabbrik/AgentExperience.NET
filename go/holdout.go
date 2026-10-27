package agentexperience

import (
	"context"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"fmt"
)

// The injection holdout (contract 1.8): on a deterministic share of captured runs that have a block to inject, the
// block is withheld, and both arms are recorded, so the .NET benefit estimate can compare the runs' verified outcomes.

// DefaultHoldoutSalt is the salt every assignment is drawn with unless another is given: .NET's
// ExperienceHoldoutOptions.DefaultSalt.
const DefaultHoldoutSalt = "agentexperience-holdout-v1"

// HoldoutArm is which arm of the injection holdout a captured run was assigned to.
type HoldoutArm string

const (
	// HoldoutTreatment is a run whose block was injected as usual.
	HoldoutTreatment HoldoutArm = "Treatment"
	// HoldoutWithheld is a run whose block was built and then withheld (the arm .NET names Holdout).
	HoldoutWithheld HoldoutArm = "Holdout"
)

// twoToThe64 is 2^64, exactly, as a double.
const twoToThe64 = 18446744073709551616.0

// HoldoutFraction is a run's position in [0, 1]: SHA-256 over the run ID's 16 bytes in RFC 4122 order followed by
// the salt's UTF-8 bytes; the digest's first 8 bytes, read as a big-endian unsigned integer, divided by 2^64 as a
// double. Pure.
func HoldoutFraction(runID UUID, salt string) float64 {
	hash := sha256.New()
	hash.Write(runID[:])
	hash.Write([]byte(salt))
	return float64(binary.BigEndian.Uint64(hash.Sum(nil)[:8])) / twoToThe64
}

// AssignHoldout is a run's arm at rate: HoldoutWithheld when the rate is at least 1, or the run's fraction is strictly
// below the rate; HoldoutTreatment otherwise (always, at rate 0). Pure: the same run, salt and rate always give the
// same arm.
func AssignHoldout(runID UUID, salt string, rate float64) HoldoutArm {
	if rate >= 1 || HoldoutFraction(runID, salt) < rate {
		return HoldoutWithheld
	}
	return HoldoutTreatment
}

// HoldoutAssignment is one captured run's assignment, as a HoldoutRecorder records it: the run, its arm, the records
// its block named (each at the revision it was rendered from: what was delivered, or would have been), and when the
// arm was decided. IDs and revisions only, never content.
type HoldoutAssignment struct {
	RunID      UUID
	Arm        HoldoutArm
	Records    []RunExposure
	AssignedAt Instant
}

// HoldoutRecorder is a Store that records holdout assignments (contract 1.8), for the .NET benefit estimate.
// Optional: the agent integration records an assignment only through a store that is one, and reports NotTracked for
// one that is not. The PostgreSQL store and the in-memory store both are.
type HoldoutRecorder interface {
	// RecordHoldoutAssignment records a run's assignment in scope, within bounds: one row per named record live in
	// exactly scope, at the revision named; a row already there for the run and record is kept as it is; the instant,
	// truncated to whole microseconds, is clamped to the store's clock; nothing is written for a run whose own records
	// are all erased. At most MaxRecordInjectionIDs records.
	RecordHoldoutAssignment(ctx context.Context, bounds AuthorizationBounds, scope Scope,
		assignment HoldoutAssignment) error
}

// HoldoutRecords validates an assignment and returns its records to write: each record once, at the revision it was
// first named with, in order. An empty run ID, an arm other than HoldoutTreatment or HoldoutWithheld, more than
// MaxRecordInjectionIDs records, a nil record ID or a negative revision is an error.
func HoldoutRecords(assignment HoldoutAssignment) ([]RunExposure, error) {
	if assignment.RunID.IsZero() {
		return nil, errors.New("a holdout assignment's run ID must be a non-nil UUID")
	}
	if assignment.Arm != HoldoutTreatment && assignment.Arm != HoldoutWithheld {
		return nil, fmt.Errorf("a holdout assignment's arm must be %s or %s", HoldoutTreatment, HoldoutWithheld)
	}
	if len(assignment.Records) > MaxRecordInjectionIDs {
		return nil, fmt.Errorf("a holdout assignment names at most %d records", MaxRecordInjectionIDs)
	}
	seen := make(map[UUID]bool, len(assignment.Records))
	records := make([]RunExposure, 0, len(assignment.Records))
	for _, record := range assignment.Records {
		if record.ExperienceID.IsZero() {
			return nil, errors.New("every record a holdout assignment names must have a non-nil UUID")
		}
		if record.Revision < 0 {
			return nil, errors.New("a holdout assignment's revisions must not be negative")
		}
		if !seen[record.ExperienceID] {
			seen[record.ExperienceID] = true
			records = append(records, record)
		}
	}
	return records, nil
}
