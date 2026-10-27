package contracttest

import (
	"errors"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

// ---- Contract 1.8's holdout-record fixtures -----------------------------------------------------------------------

// HoldoutStepOf reads a holdout-record "record" step (or an eraseDuringRecord step's "record"): the scope, which is
// also the caller's bounds, and the assignment.
func HoldoutStepOf(record *ae.Object) (ae.Scope, ae.HoldoutAssignment, error) {
	if record == nil {
		return ae.Scope{}, ae.HoldoutAssignment{}, errors.New("no record step")
	}
	runID, err := ae.ParseUUID(text(get(record, "runId")))
	if err != nil {
		return ae.Scope{}, ae.HoldoutAssignment{}, err
	}
	at, err := ae.ParseInstant(text(get(record, "assignedAt")))
	if err != nil {
		return ae.Scope{}, ae.HoldoutAssignment{}, err
	}
	assignment := ae.HoldoutAssignment{RunID: runID, Arm: ae.HoldoutArm(text(get(record, "arm"))), AssignedAt: at}
	for _, item := range list(get(record, "records")) {
		named := object(item)
		id, err := ae.ParseUUID(text(get(named, "experienceId")))
		if err != nil {
			return ae.Scope{}, ae.HoldoutAssignment{}, err
		}
		assignment.Records = append(assignment.Records, ae.RunExposure{ExperienceID: id,
			Revision: integer(get(named, "revision"))})
	}
	return ScopeOf(get(record, "scope")), assignment, nil
}
