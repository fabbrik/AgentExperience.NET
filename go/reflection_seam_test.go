package agentexperience

// Contract 1.14: the reflector seam and the content guard's fail-closed normalization.

import (
	"context"
	"errors"
	"strings"
	"testing"
)

func verifiedRun(t *testing.T) (CapturedRun, Verification) {
	t.Helper()
	round := ClosedVerificationRound{RoundID: UUID{15: 7}, ArtifactRevision: "rev"}
	evidence := Evidence{EvidenceID: UUID{15: 9}, VerificationRoundID: round.RoundID, ArtifactRevision: "rev",
		CheckID: "c", Kind: "k", Result: CheckPass, Producer: "ci", CapturedAt: t0}
	return finishedRun(t), Verification{ClosedRound: &round, RequiredChecks: []RequiredCheck{{"c", "k"}},
		Evidence: []Evidence{evidence}, CurrentArtifactRevision: "rev", FinalizedAt: t0}
}

// reflectorFunc is a Reflector over a function.
type reflectorFunc func(ctx context.Context, request ReflectionRequest) (Reflection, error)

func (f reflectorFunc) Reflect(ctx context.Context, request ReflectionRequest) (Reflection, error) {
	return f(ctx, request)
}

// modelWritten is a model-authored reflection bound to request.
func modelWritten(request ReflectionRequest, lesson string) Reflection {
	reflection := Reflect(&request.Run, &request.Evaluation, request.ReflectionID, request.CreatedAt)
	reflection.Lesson, reflection.Authorship, reflection.Producer = lesson, AuthorshipModel, "host-model/1"
	return reflection
}

func TestTheDefaultReflectorIsTheDeterministicOne(t *testing.T) {
	run, verification := verifiedRun(t)
	plain, err := Finalize(run, verification)
	if err != nil {
		t.Fatal(err)
	}
	seam, err := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: DeterministicReflector{}})
	if err != nil || plain.Record.Status != StatusValidated || plain.Record.Reflection.Producer != ReflectorProducer ||
		seam.Record.Reflection.Lesson != plain.Record.Reflection.Lesson {
		t.Fatalf("%+v %v", seam.Record.Reflection, err)
	}
}

func TestAReflectorFailurePanicOrMismatchQuarantinesTheRecord(t *testing.T) {
	run, verification := verifiedRun(t)
	for name, reflector := range map[string]Reflector{
		"error": reflectorFunc(func(context.Context, ReflectionRequest) (Reflection, error) {
			return Reflection{}, errors.New("model unavailable")
		}),
		"panic": reflectorFunc(func(context.Context, ReflectionRequest) (Reflection, error) { panic("x") }),
		"another run": reflectorFunc(func(_ context.Context, request ReflectionRequest) (Reflection, error) {
			reflection := modelWritten(request, "Wait.")
			reflection.ExperienceRunID = UUID{1}
			return reflection, nil
		}),
		"other evidence": reflectorFunc(func(_ context.Context, request ReflectionRequest) (Reflection, error) {
			reflection := modelWritten(request, "Wait.")
			reflection.EvidenceIDs = []UUID{{2}}
			return reflection, nil
		}),
	} {
		finalized, err := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: reflector})
		if err != nil || finalized.Record.Status != StatusQuarantined || finalized.Record.Reflection != nil ||
			finalized.ReflectionFailure == nil || finalized.ScreeningRefusal != nil ||
			!strings.Contains(finalized.Event.Reason, "no eligible lesson recorded") {
			t.Errorf("%s: %+v %v", name, finalized, err)
		}
	}
	var panicked *ReflectorPanicError
	finalized, _ := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: reflectorFunc(
		func(context.Context, ReflectionRequest) (Reflection, error) { panic(errors.New("secret")) })})
	if !errors.As(finalized.ReflectionFailure, &panicked) || strings.Contains(panicked.Error(), "secret") {
		t.Fatalf("%v", finalized.ReflectionFailure)
	}
	finalized, _ = FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: reflectorFunc(
		func(_ context.Context, request ReflectionRequest) (Reflection, error) {
			reflection := modelWritten(request, "Wait.")
			reflection.CreatedAt = Now()
			return reflection, nil
		})})
	if !errors.Is(finalized.ReflectionFailure, ErrReflectionBinding) || !strings.Contains(finalized.ReflectionFailure.Error(), "CreatedAt") {
		t.Fatalf("%v", finalized.ReflectionFailure)
	}
}

func TestTheCallersCancellationThroughAReflectorIsFinalizationsError(t *testing.T) {
	run, verification := verifiedRun(t)
	ctx, cancel := context.WithCancel(context.Background())
	_, err := FinalizeContextWith(ctx, run, verification, FinalizeOptions{Reflector: reflectorFunc(
		func(ctx context.Context, _ ReflectionRequest) (Reflection, error) {
			cancel()
			return Reflection{}, ctx.Err()
		})})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("%v", err)
	}
}

func TestAModelAuthoredReflectionIsGuardedAndTheRefusalTagged(t *testing.T) {
	SetUnicodeNormalizer(func(s string) string { return s }) // ASCII only here; the maf module registers NFKC
	t.Cleanup(func() { SetUnicodeNormalizer(nil) })
	observer := &recordingObserver{}
	SetObserver(observer)
	t.Cleanup(func() { SetObserver(nil) })
	run, verification := verifiedRun(t)
	writing := func(lesson string) Reflector {
		return reflectorFunc(func(_ context.Context, request ReflectionRequest) (Reflection, error) {
			return modelWritten(request, lesson), nil
		})
	}
	finalized, err := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: writing("See evil.com.")})
	refusal, ok := ScreeningRefusalOf(finalized.ScreeningRefusal)
	if err != nil || !ok || refusal.Code != RefusalUnsafeContent || finalized.Record.Status != StatusQuarantined ||
		!errors.Is(finalized.ScreeningRefusal, ErrScreeningRefused) {
		t.Fatalf("%+v %v", finalized.ScreeningRefusal, err)
	}
	var tagged bool
	for _, observation := range observer.finished {
		for _, attribute := range observation.Attributes {
			if observation.Operation == "finalize" && attribute == (Attribute{AttributeScreeningRefusal, "UnsafeContent"}) {
				tagged = true
			}
		}
	}
	if !tagged {
		t.Fatal("the finalize span does not say why screening refused")
	}
	// A host reflector that declares no run content is taken to have shown the whole run.
	accepted, err := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: writing("Wait for the lock.")})
	if err != nil || accepted.Record.Status != StatusValidated || accepted.Record.Reflection.Authorship != AuthorshipModel {
		t.Fatalf("%+v %v", accepted, err)
	}
}

func TestWithNoNormalizerAModelAuthoredReflectionIsRefused(t *testing.T) {
	SetUnicodeNormalizer(nil)
	_, err := ScreenWith(Reflection{Lesson: "Wait.", Producer: "p", Authorship: AuthorshipModel}, nil)
	refusal, ok := ScreeningRefusalOf(err)
	if !ok || refusal.Reason != "its Lesson contains text that cannot be normalized" {
		t.Fatalf("%v", err)
	}
	if _, err := ScreenWith(Reflection{Lesson: "Wait.", Producer: "p"}, nil); err != nil {
		t.Fatalf("a deterministic reflection: %v", err)
	}
}
