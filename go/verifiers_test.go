package agentexperience

// The ready-made verifiers, with the cases of .NET's ExperienceVerifiersTests (story 21.2) as the Python port's
// tests (python/tests/test_verifiers.py) check them too.

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
)

const runTests = "run_tests"

var (
	verifierRound  = MustParseUUID("21212121-0000-0000-0000-000000000002")
	verifierAt     = MustParseInstant("2026-03-02T09:00:00+00:00")
	verifierCaller = Identity{Principal: "svc", Scope: Scope{TenantID: "contoso", ApplicationID: "support",
		ProjectID: "tickets"}}
)

func verifierCall(sequence int64, tool string, err *string) ToolCall {
	return ToolCall{ToolCallID: NewRandomUUID(), SequenceNumber: sequence, ToolName: tool, StartedAt: verifierAt,
		Result: Ptr("out"), Error: err}
}

func verifierAttempt(sequence int64, calls ...ToolCall) Attempt {
	return Attempt{AttemptID: NewRandomUUID(), SequenceNumber: sequence, StartedAt: verifierAt, ToolCalls: calls}
}

func verifierContext(attempts ...Attempt) *VerificationContext {
	return &VerificationContext{
		Run: CapturedRun{RunID: NewRandomUUID(), TaskID: "task", Scope: verifierCaller.Scope, StartedAt: verifierAt,
			Attempts: attempts, ExecutionStatus: RunCompleted},
		RoundID:  verifierRound,
		Identity: verifierCaller,
		Clock:    func() Instant { return verifierAt },
	}
}

func verify(t *testing.T, verifier Verifier, contexts ...*VerificationContext) *Verdict {
	t.Helper()
	c := verifierContext()
	if len(contexts) > 0 {
		c = contexts[0]
	}
	verdict, err := verifier(context.Background(), c)
	if err != nil {
		t.Fatal(err)
	}
	return verdict
}

func verifyErr(verifier Verifier) error {
	_, err := verifier(context.Background(), verifierContext())
	return err
}

func boolPtr(value bool) *bool { return &value }

func fixed(checkID string, passed *bool, ran *[]string, options ...VerifierOption) Verifier {
	return Check(checkID, func(context.Context, *VerificationContext) (*bool, error) {
		if ran != nil {
			*ran = append(*ran, checkID)
		}
		return passed, nil
	}, options...)
}

func mustPanic(t *testing.T, name string, fn func()) {
	t.Helper()
	defer func() {
		if recover() == nil {
			t.Fatalf("%s did not panic", name)
		}
	}()
	fn()
}

func checkIDs(verdict *Verdict) []string {
	var ids []string
	for _, check := range verdict.RequiredChecks {
		ids = append(ids, check.CheckID)
	}
	return ids
}

func TestToolSucceededPassesWhenTheLastCallReturnedAlthoughAnEarlierOneFailed(t *testing.T) {
	c := verifierContext(
		verifierAttempt(0, verifierCall(0, runTests, Ptr("exit 1")), verifierCall(1, "other", nil)),
		verifierAttempt(1, verifierCall(0, runTests, Ptr("exit 1")), verifierCall(1, runTests, nil),
			verifierCall(2, "other", Ptr("exit 9"))),
	)
	verdict := verify(t, ToolSucceeded(runTests), c)
	if !reflect.DeepEqual(verdict.RequiredChecks, []RequiredCheck{{runTests, ToolOutcomeKind}}) {
		t.Fatalf("required checks %v", verdict.RequiredChecks)
	}
	if len(verdict.Evidence) != 1 {
		t.Fatalf("evidence %v", verdict.Evidence)
	}
	evidence := verdict.Evidence[0]
	if evidence.Result != CheckPass || evidence.CheckID != runTests || evidence.Kind != ToolOutcomeKind ||
		evidence.Producer != "tool:run_tests" || evidence.VerificationRoundID != verifierRound ||
		evidence.ArtifactRevision != UnversionedRevision || verdict.ArtifactRevision != "unversioned" {
		t.Fatalf("evidence %+v", evidence)
	}
}

func TestToolSucceededFailsWhenTheLastCallFailedAndOrdersBySequenceNotListOrder(t *testing.T) {
	c := verifierContext(
		verifierAttempt(1, verifierCall(3, runTests, Ptr("exit 1")), verifierCall(0, runTests, nil)),
		verifierAttempt(0, verifierCall(5, runTests, nil)),
	)
	verdict := verify(t, ToolSucceeded(runTests, WithCheckID("tests-pass"), WithArtifactRevision("build-42")), c)
	evidence := verdict.Evidence[0]
	if evidence.Result != CheckFail || evidence.CheckID != "tests-pass" || evidence.ArtifactRevision != "build-42" ||
		verdict.ArtifactRevision != "build-42" || !strings.Contains(*evidence.Detail, "exit 1") {
		t.Fatalf("evidence %+v", evidence)
	}
	if !reflect.DeepEqual(verdict.RequiredChecks, []RequiredCheck{{"tests-pass", ToolOutcomeKind}}) {
		t.Fatalf("required checks %v", verdict.RequiredChecks)
	}
}

func TestToolSucceededReadsOnlyTheErrorSoARedactedResultStillCounts(t *testing.T) {
	call := verifierCall(0, runTests, nil)
	call.Result = nil
	if verify(t, ToolSucceeded(runTests), verifierContext(verifierAttempt(0, call))).Evidence[0].Result != CheckPass {
		t.Fatal("a call with no result did not pass")
	}
}

func TestToolSucceededReturnsNilWhenTheToolWasNeverCalledAndMatchesNamesExactly(t *testing.T) {
	c := verifierContext(verifierAttempt(0, verifierCall(0, "Run_Tests", nil), verifierCall(1, "other", nil)))
	if verify(t, ToolSucceeded(runTests), c) != nil || verify(t, ToolSucceeded(runTests)) != nil {
		t.Fatal("a run without the tool was verified")
	}
}

func TestToolSucceededRefusesBlankArguments(t *testing.T) {
	mustPanic(t, "an empty tool", func() { ToolSucceeded("") })
	mustPanic(t, "a blank tool", func() { ToolSucceeded(" ") })
	mustPanic(t, "a blank check ID", func() { ToolSucceeded(runTests, WithCheckID(" ")) })
	mustPanic(t, "an empty revision", func() { ToolSucceeded(runTests, WithArtifactRevision("")) })
}

func TestCheckTurnsTheHostsVerdictIntoEvidence(t *testing.T) {
	for passed, expected := range map[bool]CheckResult{true: CheckPass, false: CheckFail} {
		verdict := verify(t, fixed("lint", boolPtr(passed), nil))
		if !reflect.DeepEqual(verdict.RequiredChecks, []RequiredCheck{{"lint", HostCheckKind}}) {
			t.Fatalf("required checks %v", verdict.RequiredChecks)
		}
		evidence := verdict.Evidence[0]
		if evidence.Result != expected || evidence.Kind != HostCheckKind || evidence.Producer != "host" ||
			evidence.ArtifactRevision != UnversionedRevision {
			t.Fatalf("evidence %+v", evidence)
		}
	}
	if verify(t, fixed("lint", nil, nil)) != nil {
		t.Fatal("a nil verdict stored something")
	}
	failing := errors.New("the check could not run")
	check := Check("lint", func(context.Context, *VerificationContext) (*bool, error) { return boolPtr(true), failing })
	if err := verifyErr(check); !errors.Is(err, failing) {
		t.Fatalf("the check's error was %v", err)
	}
}

func TestCheckRefusesABlankIDNoFunctionOrACheckIDOption(t *testing.T) {
	pass := func(context.Context, *VerificationContext) (*bool, error) { return boolPtr(true), nil }
	mustPanic(t, "a blank ID", func() { Check(" ", pass) })
	mustPanic(t, "no function", func() { Check("lint", nil) })
	mustPanic(t, "a check ID option", func() { Check("lint", pass, WithCheckID("other")) })
}

func TestApprovalTurnsAPersonsDecisionIntoEvidenceProducedByTheApprover(t *testing.T) {
	for approved, expected := range map[bool]CheckResult{true: CheckPass, false: CheckFail} {
		verdict := verify(t, Approval("reviewed", func(context.Context, *VerificationContext) (*ApprovalDecision, error) {
			return &ApprovalDecision{ApproverID: "user-7f3a", Approved: approved}, nil
		}, WithArtifactRevision("pr-7")))
		if !reflect.DeepEqual(verdict.RequiredChecks, []RequiredCheck{{"reviewed", HumanApprovalKind}}) {
			t.Fatalf("required checks %v", verdict.RequiredChecks)
		}
		evidence := verdict.Evidence[0]
		if evidence.Result != expected || evidence.Kind != HumanApprovalKind || evidence.Producer != "user-7f3a" ||
			verdict.ArtifactRevision != "pr-7" {
			t.Fatalf("evidence %+v", evidence)
		}
	}
}

func TestApprovalReturningNilStoresNothingAndABlankApproverIsAnError(t *testing.T) {
	none := Approval("reviewed", func(context.Context, *VerificationContext) (*ApprovalDecision, error) {
		return nil, nil
	})
	if verify(t, none) != nil {
		t.Fatal("no decision stored something")
	}
	for _, approver := range []string{"", "  "} {
		blank := Approval("reviewed", func(context.Context, *VerificationContext) (*ApprovalDecision, error) {
			return &ApprovalDecision{ApproverID: approver, Approved: true}, nil
		})
		if err := verifyErr(blank); err == nil || !strings.Contains(err.Error(), "names no approver") {
			t.Fatalf("a blank approver %q gave %v", approver, err)
		}
	}
}

func TestAllOfAPassAndAFailIsOneVerdictWithBothChecks(t *testing.T) {
	verdict := verify(t, AllOf(fixed("a", boolPtr(true), nil), fixed("b", boolPtr(false), nil)))
	if !reflect.DeepEqual(checkIDs(verdict), []string{"a", "b"}) || verdict.Evidence[0].Result != CheckPass ||
		verdict.Evidence[1].Result != CheckFail || verdict.ArtifactRevision != UnversionedRevision {
		t.Fatalf("verdict %+v", verdict)
	}
}

func TestAllOfStopsAtTheFirstNilAndReturnsNil(t *testing.T) {
	var ran []string
	verdict := verify(t, AllOf(fixed("a", boolPtr(true), &ran), fixed("b", nil, &ran), fixed("c", boolPtr(true), &ran)))
	if verdict != nil || !reflect.DeepEqual(ran, []string{"a", "b"}) {
		t.Fatalf("verdict %+v, ran %v", verdict, ran)
	}
}

func TestAllOfRefusesTwoDifferentExplicitRevisionsAndDuplicateChecks(t *testing.T) {
	if err := verifyErr(AllOf(fixed("a", boolPtr(true), nil, WithArtifactRevision("build-1")),
		fixed("b", boolPtr(true), nil, WithArtifactRevision("build-2")))); err == nil {
		t.Fatal("two revisions were combined")
	}
	if err := verifyErr(AllOf(fixed("a", boolPtr(true), nil), fixed("a", boolPtr(false), nil))); err == nil {
		t.Fatal("one check was required twice")
	}
}

func TestAllOfTakesTheExplicitRevisionOverTheDefaultAndRestampsTheDefaultedEvidence(t *testing.T) {
	verdict := verify(t, AllOf(fixed("a", boolPtr(true), nil),
		fixed("b", boolPtr(true), nil, WithArtifactRevision("build-2")), fixed("c", boolPtr(false), nil)))
	if verdict.ArtifactRevision != "build-2" {
		t.Fatalf("revision %q", verdict.ArtifactRevision)
	}
	for _, evidence := range verdict.Evidence {
		if evidence.ArtifactRevision != "build-2" || evidence.VerificationRoundID != verifierRound {
			t.Fatalf("evidence %+v", evidence)
		}
	}
}

func TestAnyOfDoesNotCountAVerdictMissingEvidenceForARequiredCheckAsPassed(t *testing.T) {
	incomplete := func(_ context.Context, c *VerificationContext) (*Verdict, error) {
		return &Verdict{
			RequiredChecks: []RequiredCheck{{"a", HostCheckKind}, {"b", HostCheckKind}, {"c", "TestResult"}},
			Evidence: []Evidence{
				c.CreateEvidence("a", HostCheckKind, CheckPass, "host", UnversionedRevision, nil),
				c.CreateEvidence("c", HostCheckKind, CheckPass, "host", UnversionedRevision, nil),
			},
			ArtifactRevision: UnversionedRevision,
		}, nil
	}
	anyKind := func(_ context.Context, c *VerificationContext) (*Verdict, error) {
		return &Verdict{
			RequiredChecks:   []RequiredCheck{{"d", AnyKind}},
			Evidence:         []Evidence{c.CreateEvidence("d", "Whatever", CheckPass, "host", UnversionedRevision, nil)},
			ArtifactRevision: UnversionedRevision,
		}, nil
	}
	if chosen := verify(t, AnyOf(incomplete, anyKind)); !reflect.DeepEqual(checkIDs(chosen), []string{"d"}) {
		t.Fatalf("chose %v", checkIDs(chosen))
	}
	if chosen := verify(t, AnyOf(incomplete)); len(chosen.RequiredChecks) != 3 {
		t.Fatalf("chose %v", checkIDs(chosen))
	}
}

func TestAnyOfReturnsTheFirstPassingVerdictAndStopsThere(t *testing.T) {
	var ran []string
	verdict := verify(t, AnyOf(fixed("a", boolPtr(false), &ran), fixed("b", nil, &ran), fixed("c", boolPtr(true), &ran),
		fixed("d", boolPtr(true), &ran)))
	if !reflect.DeepEqual(checkIDs(verdict), []string{"c"}) || !reflect.DeepEqual(ran, []string{"a", "b", "c"}) {
		t.Fatalf("verdict %v, ran %v", checkIDs(verdict), ran)
	}
}

func TestAnyOfWithNonePassingReturnsTheFirstVerdictAndWithAllNilReturnsNil(t *testing.T) {
	failed := verify(t, AnyOf(fixed("a", nil, nil), fixed("b", boolPtr(false), nil), fixed("c", boolPtr(false), nil)))
	if !reflect.DeepEqual(checkIDs(failed), []string{"b"}) {
		t.Fatalf("chose %v", checkIDs(failed))
	}
	if verify(t, AnyOf(fixed("a", nil, nil), fixed("b", nil, nil))) != nil {
		t.Fatal("all nil stored something")
	}
}

func TestAllOfAndAnyOfRefuseNoVerifiersOrANilOne(t *testing.T) {
	mustPanic(t, "AllOf()", func() { AllOf() })
	mustPanic(t, "AnyOf()", func() { AnyOf() })
	mustPanic(t, "AllOf with nil", func() { AllOf(fixed("a", boolPtr(true), nil), nil) })
}

func TestNewRandomUUIDIsAVersion4UUID(t *testing.T) {
	a, b := NewRandomUUID(), NewRandomUUID()
	if a == b || a[6]>>4 != 4 || a[8]>>6 != 2 {
		t.Fatalf("%v %v", a, b)
	}
}
