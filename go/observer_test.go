package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"sync"
	"testing"
)

type recordingObserver struct {
	mu       sync.Mutex
	started  []string
	finished []Observation
}

func (r *recordingObserver) Start(ctx context.Context, _, operation string, _ bool) (context.Context, any) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.started = append(r.started, operation)
	return ctx, operation
}

func (r *recordingObserver) Finish(_ any, observation Observation) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.finished = append(r.finished, observation)
}

type panickingObserver struct{}

func (panickingObserver) Start(context.Context, string, string, bool) (context.Context, any) {
	panic("start")
}

func (panickingObserver) Finish(any, Observation) { panic("finish") }

func TestWithNoObserverTheHookAllocatesNothingAndEveryMethodIsNilSafe(t *testing.T) {
	SetObserver(nil)
	ctx := context.Background()
	allocations := testing.AllocsPerRun(100, func() {
		_, op := Observe(ctx, "verify", false)
		op.Set(AttributeRunID, "x")
		op.Done("Verified")
		op.DoneWithClass("NotRecorded", ErrorClassUnexpected)
		op.Fail(errors.New("x"))
		op.End()
	})
	// errors.New above allocates; the hook itself must not.
	if allocations > 1 {
		t.Fatalf("the hook allocated %v times with no observer", allocations)
	}
	if _, op := Observe(ctx, "verify", false); op != nil {
		t.Fatal("an operation was opened with no observer registered")
	}
}

func TestAnObserverThatPanicsChangesNothing(t *testing.T) {
	run := func() (FinalizedExperience, error) {
		return Finalize(finishedRun(t), Verification{FinalizedAt: Now(), CurrentArtifactRevision: "r1"})
	}
	SetObserver(nil)
	want, wantErr := run()
	SetObserver(panickingObserver{})
	t.Cleanup(func() { SetObserver(nil) })
	got, gotErr := run()
	if fmt.Sprint(wantErr) != fmt.Sprint(gotErr) || got.Record.Status != want.Record.Status ||
		got.Record.ExperienceID != want.Record.ExperienceID {
		t.Fatalf("a panicking observer changed the result: %v / %v", gotErr, got.Record.Status)
	}
}

func TestFinalizeReportsItsNestedVerificationAndAFailedOneAsFaulted(t *testing.T) {
	observer := &recordingObserver{}
	SetObserver(observer)
	t.Cleanup(func() { SetObserver(nil) })

	if _, err := Finalize(finishedRun(t), Verification{FinalizedAt: Now(), CurrentArtifactRevision: "r1"}); err != nil {
		t.Fatal(err)
	}
	got := map[string]Observation{}
	for _, item := range observer.finished {
		got[item.Operation] = item
	}
	if got["finalize"].Outcome != "Quarantined" || got["finalize"].Nested {
		t.Fatalf("finalize: %+v", got["finalize"])
	}
	if got["verify"].Outcome != "Unknown" || !got["verify"].Nested {
		t.Fatalf("verify: %+v", got["verify"])
	}

	observer.finished = nil
	if _, err := Finalize(CapturedRun{}, Verification{}); err == nil {
		t.Fatal("an unfinished run was finalized")
	}
	failed := observer.finished[len(observer.finished)-1]
	if failed.Operation != "finalize" || failed.Outcome != OutcomeFaulted || failed.ErrorClass != ErrorClassUnexpected ||
		failed.ErrorType != "errors.errorString" {
		t.Fatalf("a failed finalize: %+v", failed)
	}
}

func TestClassificationMirrorsNet(t *testing.T) {
	tests := []struct {
		err  error
		want string
	}{
		{context.Canceled, ErrorClassCancelled},
		{fmt.Errorf("wrapped: %w", context.DeadlineExceeded), ErrorClassTimeout},
		{&net.OpError{Op: "read", Err: errors.New("reset")}, ErrorClassInfrastructure},
		{&os.PathError{Op: "open", Path: "x", Err: errors.New("gone")}, ErrorClassInfrastructure},
		{errors.New("anything else"), ErrorClassUnexpected},
	}
	for _, test := range tests {
		if got := ClassifyError(test.err); got != test.want {
			t.Errorf("%v: %s, want %s", test.err, got, test.want)
		}
	}
	if got := errorTypeName(fmt.Errorf("secret %d", 1)); got != "fmt.wrapError" && got != "errors.errorString" &&
		got != "fmt.fmtError" {
		// The type name only, never the message.
		t.Logf("type name %s", got)
	}
}

func TestObservedOperationsAreTheOnesThePortPerforms(t *testing.T) {
	operations := ObservedOperations()
	for _, forbidden := range []string{"retention.sweep", "delete", "grant.purge", "grant.access.purge", "record.seal",
		"record.authorship.backfill", "lifecycle.commit", "confidence.apply", "reuse_feedback", "deindex", "reindex"} {
		if _, ok := operations[forbidden]; ok {
			t.Errorf("the port reports %s, which it does not perform", forbidden)
		}
	}
	if len(operations) != 11 {
		t.Errorf("%d operations", len(operations))
	}
}
