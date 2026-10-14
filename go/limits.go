package agentexperience

import "errors"

// SanitizationLimits are what sanitization refuses: a payload beyond any of them is refused whole. Lengths are
// UTF-16 code units. The zero value means DefaultSanitizationLimits(); any other value must have every field
// strictly positive.
type SanitizationLimits struct {
	MaxDepth           int
	MaxFieldCount      int
	MaxValueLength     int
	MaxFieldNameLength int
}

// DefaultSanitizationLimits are contract 1.x's.
func DefaultSanitizationLimits() SanitizationLimits {
	return SanitizationLimits{MaxDepth: 4, MaxFieldCount: 50, MaxValueLength: 1_000_000, MaxFieldNameLength: 100}
}

// Validate refuses limits that are not the zero value (the defaults) and have a field that is not strictly
// positive.
func (l SanitizationLimits) Validate() error {
	if l == (SanitizationLimits{}) {
		return nil
	}
	if l.MaxDepth <= 0 || l.MaxFieldCount <= 0 || l.MaxValueLength <= 0 || l.MaxFieldNameLength <= 0 {
		return errors.New("every sanitization limit must be strictly positive")
	}
	return nil
}

// CaptureLimits are how much of a run capture keeps. Lengths are UTF-16 code units. The zero value means
// DefaultCaptureLimits(); any other value must have every field strictly positive.
type CaptureLimits struct {
	MaxAttemptsPerRun      int
	MaxToolCallsPerAttempt int
	MaxResultLength        int
	MaxErrorLength         int
}

// DefaultCaptureLimits are contract 1.x's.
func DefaultCaptureLimits() CaptureLimits {
	return CaptureLimits{MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 4_000, MaxErrorLength: 4_000}
}

// Validate refuses limits that are not the zero value (the defaults) and have a field that is not strictly
// positive.
func (l CaptureLimits) Validate() error {
	if l == (CaptureLimits{}) {
		return nil
	}
	if l.MaxAttemptsPerRun <= 0 || l.MaxToolCallsPerAttempt <= 0 || l.MaxResultLength <= 0 || l.MaxErrorLength <= 0 {
		return errors.New("every capture limit must be strictly positive")
	}
	return nil
}

// TruncationPlaceholder is what a result or error over its capture limit is replaced by.
const TruncationPlaceholder = "[truncated: value exceeded the configured limit]"

// MaxExposuresPerRun is the most stored records one run may be recorded as shown.
const MaxExposuresPerRun = 256
