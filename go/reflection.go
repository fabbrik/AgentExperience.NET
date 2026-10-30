package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"slices"
)

// The reflector seam (contract 1.14): finalization asks a Reflector for the reflection of a verified run, the
// deterministic one unless the host configures another (the maf module's model-backed ChatClientReflector), binds
// what it returns to the request, and screens it before the record is created.

// ReflectionRequest is what a reflector reflects on: the finished run, its evaluation, and the identity and instant
// the reflection must carry (.NET's ReflectionRequest).
type ReflectionRequest struct {
	Run          CapturedRun
	Evaluation   VerificationResult
	ReflectionID UUID
	CreatedAt    Instant
}

// Reflector writes the reflection of a finished, evaluated run. The reflection must carry the request's identity and
// copy its evaluation (EnsureMatches); finalization checks it and screens its free text. An error stores the record
// Quarantined, with no reflection; the caller's own cancellation is returned as finalization's error.
type Reflector interface {
	Reflect(ctx context.Context, request ReflectionRequest) (Reflection, error)
}

// ReflectionRunContent is an optional Reflector capability (.NET's IReflectionRunContent): the captured text the
// reflector showed whoever wrote the reflection. The content guard compares a model-authored reflection's URLs,
// hostnames and IP addresses with it. A reflector that does not implement it is taken to have been given the whole
// run (DefaultRunContent); one whose answer fails is taken to have been given nothing.
type ReflectionRunContent interface {
	ReflectedRunContent(request ReflectionRequest) ([]string, error)
}

// DeterministicReflector is the default reflector: Reflect's deterministic lesson, which never calls a model.
type DeterministicReflector struct{}

// Reflect is the deterministic reflection of the request's run.
func (DeterministicReflector) Reflect(ctx context.Context, request ReflectionRequest) (Reflection, error) {
	return reflectObserved(ctx, true, &request.Run, &request.Evaluation, request.ReflectionID, request.CreatedAt), nil
}

// ErrReflectionBinding is a reflection that does not carry its request's identity or copy its evaluation.
var ErrReflectionBinding = errors.New("the reflection does not match its request")

// EnsureMatches checks that reflection carries the request's identity and copies its evaluation's verdict, score,
// rule version and evidence IDs (distinct, first occurrence first), as .NET's ReflectionRequest.EnsureMatches does.
// The error wraps ErrReflectionBinding and names the first field that differs, never captured text.
func (r ReflectionRequest) EnsureMatches(reflection Reflection) error {
	outcome := r.Evaluation.Outcome
	ids := make([]UUID, 0, len(outcome.Evidence))
	for _, evidence := range outcome.Evidence {
		if !slices.Contains(ids, evidence.EvidenceID) {
			ids = append(ids, evidence.EvidenceID)
		}
	}
	mismatch := ""
	switch {
	case reflection.ReflectionID != r.ReflectionID:
		mismatch = "ReflectionId"
	case reflection.ExperienceRunID != r.Run.RunID:
		mismatch = "ExperienceRunId"
	case reflection.CreatedAt != r.CreatedAt:
		mismatch = "CreatedAt"
	case reflection.VerificationStatus != outcome.Status:
		mismatch = "VerificationStatus"
	case !(reflection.CompletionScore == r.Evaluation.CompletionScore ||
		(reflection.CompletionScore != reflection.CompletionScore && r.Evaluation.CompletionScore != r.Evaluation.CompletionScore)):
		mismatch = "CompletionScore"
	case reflection.VerificationRuleVersion != r.Evaluation.RuleVersion:
		mismatch = "VerificationRuleVersion"
	case !slices.Equal(reflection.EvidenceIDs, ids) && !(len(reflection.EvidenceIDs) == 0 && len(ids) == 0):
		mismatch = "EvidenceIds"
	}
	if mismatch == "" {
		return nil
	}
	return fmt.Errorf("%w: the reflection's %s does not match the reflection request for run %s; a reflection must "+
		"carry the request's identity and copy its evaluation exactly", ErrReflectionBinding, mismatch, r.Run.RunID)
}

// ---- Screening refusals ------------------------------------------------------------------------------------------

// ScreeningRefusalCode is why screening refused a reflection, as .NET's ReflectionScreeningRefusal names it. A port
// with no host sanitizer layer answers OverLimit, MissingLesson, MissingProducer, UndefinedAuthorship and
// UnsafeContent.
type ScreeningRefusalCode string

// The refusal codes this port answers.
const (
	RefusalOverLimit           ScreeningRefusalCode = "OverLimit"
	RefusalMissingLesson       ScreeningRefusalCode = "MissingLesson"
	RefusalMissingProducer     ScreeningRefusalCode = "MissingProducer"
	RefusalUndefinedAuthorship ScreeningRefusalCode = "UndefinedAuthorship"
	RefusalUnsafeContent       ScreeningRefusalCode = "UnsafeContent"
)

// ScreeningRefusalError is a reflection screening refused: its code and its content-free reason (it names fields,
// limits and rules, never text). It wraps ErrScreeningRefused.
type ScreeningRefusalError struct {
	Code   ScreeningRefusalCode
	Reason string
}

func (e *ScreeningRefusalError) Error() string { return ErrScreeningRefused.Error() + ": " + e.Reason }

// Unwrap is ErrScreeningRefused.
func (e *ScreeningRefusalError) Unwrap() error { return ErrScreeningRefused }

// ScreeningRefusalOf is err's refusal, when it is (or wraps) one.
func ScreeningRefusalOf(err error) (*ScreeningRefusalError, bool) {
	var refusal *ScreeningRefusalError
	ok := errors.As(err, &refusal)
	return refusal, ok
}

// ---- Finalizing with a reflector -----------------------------------------------------------------------------------

// FinalizeOptions are what finalization runs with beyond the run and its verification.
type FinalizeOptions struct {
	// Reflector writes the reflection of a verified run; DeterministicReflector when nil.
	Reflector Reflector
}

// reflectAndScreen asks the reflector, binds and screens what it returns. It answers the screened reflection, or
// why there is none: the reflector's failure (its error, a panic, or a binding mismatch) or screening's refusal. An
// error is only the caller's own cancellation.
func reflectAndScreen(ctx context.Context, reflector Reflector, request ReflectionRequest) (
	reflection *Reflection, failure error, refusal error, err error) {
	written, failure := callReflector(ctx, reflector, request)
	if failure != nil {
		if ctx.Err() != nil && (errors.Is(failure, context.Canceled) || errors.Is(failure, context.DeadlineExceeded)) {
			return nil, nil, nil, failure
		}
		return nil, failure, nil, nil
	}
	written.EvidenceIDs = slices.Clone(written.EvidenceIDs)
	if bindingErr := request.EnsureMatches(written); bindingErr != nil {
		return nil, bindingErr, nil, nil
	}
	var content []string
	if declared, ok := reflector.(ReflectionRunContent); ok {
		content = declaredContent(declared, request)
	} else {
		content = DefaultRunContent(request)
	}
	screened, refused := screen(written, content)
	if refused != nil {
		return nil, nil, refused, nil
	}
	return &screened, nil, nil, nil
}

// ReflectorPanicError is a reflector that panicked: recorded by the panic value's type only.
type ReflectorPanicError struct{ Type string }

func (e *ReflectorPanicError) Error() string { return "the reflector panicked with " + e.Type }

func callReflector(ctx context.Context, reflector Reflector, request ReflectionRequest) (reflection Reflection, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			t := reflect.TypeOf(recovered)
			name := "nil"
			if t != nil {
				name = t.String()
			}
			err = &ReflectorPanicError{Type: name}
		}
	}()
	return reflector.Reflect(ctx, request)
}

func declaredContent(declared ReflectionRunContent, request ReflectionRequest) (content []string) {
	defer func() {
		if recover() != nil {
			content = []string{}
		}
	}()
	content, err := declared.ReflectedRunContent(request)
	if err != nil || content == nil {
		return []string{}
	}
	return content
}
