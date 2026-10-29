package contracttest

import (
	"errors"
	"path/filepath"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.11's provider-holdout-assign, provider-holdout-record and provider-benefit-estimate fixtures -------

// The contract 1.11 fixture families.
const (
	ProviderHoldoutAssignFamily   = "provider-holdout-assign"
	ProviderHoldoutRecordFamily   = "provider-holdout-record"
	ProviderBenefitEstimateFamily = "provider-benefit-estimate"
)

// ProviderAssignmentColumns are agent_experience.experience_provider_holdout_assignments' columns, in table order.
var ProviderAssignmentColumns = []string{"run_id", "provider_key", "arm", "tenant_id", "application_id", "project_id",
	"team_id", "agent_id", "user_id", "assigned_at"}

// ProviderStepOf reads a provider-holdout-record "record" step (or an eraseDuringRecord step's "record"): the scope,
// which is also the caller's bounds, and the assignment.
func ProviderStepOf(record *ae.Object) (ae.Scope, ae.ProviderHoldoutAssignment, error) {
	if record == nil {
		return ae.Scope{}, ae.ProviderHoldoutAssignment{}, errors.New("no record step")
	}
	runID, err := ae.ParseUUID(text(get(record, "runId")))
	if err != nil {
		return ae.Scope{}, ae.ProviderHoldoutAssignment{}, err
	}
	at, err := ae.ParseInstant(text(get(record, "assignedAt")))
	if err != nil {
		return ae.Scope{}, ae.ProviderHoldoutAssignment{}, err
	}
	return ScopeOf(get(record, "scope")), ae.ProviderHoldoutAssignment{RunID: runID,
		ProviderKey: text(get(record, "providerKey")), Arm: ae.HoldoutArm(text(get(record, "arm"))), AssignedAt: at}, nil
}

// ProviderOutcomeRowOf reads one provider outcome row ({runId, providerKey, arm, assignedAt, runOutcome}).
func ProviderOutcomeRowOf(value any) ae.ProviderHoldoutOutcomeRow {
	o := object(value)
	row := ae.ProviderHoldoutOutcomeRow{
		RunID:       must(ae.ParseUUID(text(get(o, "runId")))),
		ProviderKey: text(get(o, "providerKey")),
		Arm:         ae.HoldoutArm(text(get(o, "arm"))),
		AssignedAt:  must(ae.ParseInstant(text(get(o, "assignedAt")))),
	}
	if outcome := optionalText(get(o, "runOutcome")); outcome != nil {
		status := ae.VerificationStatus(*outcome)
		row.RunOutcome = &status
	}
	return row
}

// ProviderBenefitCase is one provider-benefit-estimate case.
type ProviderBenefitCase struct {
	Name        string
	ProviderKey string
	Rows        []ae.ProviderHoldoutOutcomeRow
	Options     ae.BenefitOptions
	Expected    *ae.Object
}

// LoadProviderBenefitCase reads a provider-benefit-estimate case.
func LoadProviderBenefitCase(name string) ProviderBenefitCase {
	dir := filepath.Join(Fixtures, ProviderBenefitEstimateFamily, name)
	rows := object(MustReadJSON(filepath.Join(dir, "rows.json")))
	options := object(MustReadJSON(filepath.Join(dir, "options.json")))
	c := ProviderBenefitCase{Name: name, ProviderKey: text(get(rows, "providerKey")),
		Expected: object(MustReadJSON(filepath.Join(dir, "expected.json"))),
		Options:  ae.BenefitOptions{MinimumRunsPerArm: int(integer(get(options, "minimumRunsPerArm")))}}
	for _, item := range list(get(rows, "rows")) {
		c.Rows = append(c.Rows, ProviderOutcomeRowOf(item))
	}
	return c
}

// ProviderEstimateJSON is a provider estimate in the provider-benefit-estimate expected.json form: providerKey,
// minimumRunsPerArm and overall.
func ProviderEstimateJSON(estimate ae.ProviderBenefitEstimate) *ae.Object {
	return ae.NewObject(
		ae.Member{Key: "providerKey", Value: estimate.ProviderKey},
		ae.Member{Key: "minimumRunsPerArm", Value: estimate.MinimumRunsPerArm},
		ae.Member{Key: "overall", Value: ComparisonJSON(estimate.Overall)},
	)
}

// ProviderFleetEstimateJSON is ProviderEstimateJSON with "tracked" and "outcome": the cross-language provider fleet
// estimate's form, which the .NET contract tests compare member for member with .NET's.
func ProviderFleetEstimateJSON(estimate ae.ProviderBenefitEstimate) *ae.Object {
	o := ProviderEstimateJSON(estimate)
	o.Set("tracked", estimate.Tracked)
	o.Set("outcome", string(estimate.Outcome))
	return o
}
