package agentexperience

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"strconv"
	"strings"
)

// The deterministic reflector, template version 1.1.0 (contract step 3), and the screening finalization applies to
// every reflection before it is stored.

// The deterministic reflector's identity.
const (
	ReflectorTemplateVersion = "1.1.0"
	ReflectorProducerPrefix  = "AgentExperience.DefaultExperienceReflector/"
	ReflectorProducer        = ReflectorProducerPrefix + ReflectorTemplateVersion
)

const (
	unknownValue                     = "unknown"
	verificationScopeWarning         = "Verification applies only to this run and its captured environment; reuse in another context is not itself verified."
	libraryModelReflectorPrefix      = "AgentExperience.ChatClientExperienceReflector/"
	maxLessonLength                  = 4_000
	maxListItemLength                = 1_000
	maxListItems                     = 32
	maxProducerLength                = 200
	maxQuotedLength                  = 500
	maxLessonFailedAttempts          = 2
	clipMarker                       = "…"
	reflectorPreconditionLabelPrefix = "Environment metadata ["
)

// clip cuts text to length UTF-16 code units, ending in "…", never splitting a surrogate pair.
func clip(text string, length int) string {
	if UTF16Len(text) <= length {
		return text
	}
	return UTF16Prefix(text, length-UTF16Len(clipMarker)) + clipMarker
}

var quoteEscaper = strings.NewReplacer(`\`, `\\`, `"`, `\"`, "\r", `\r`, "\n", `\n`)

func quote(text string) string {
	return `"` + quoteEscaper.Replace(clip(text, maxQuotedLength)) + `"`
}

func quoteOrMissing(text *string) string {
	if text == nil {
		return "none captured"
	}
	return quote(*text)
}

func distinct[T comparable](items []T) []T {
	seen := map[T]bool{}
	var result []T
	for _, item := range items {
		if !seen[item] {
			seen[item] = true
			result = append(result, item)
		}
	}
	return result
}

func describe(attempt *Attempt) string {
	calls := append([]ToolCall(nil), attempt.ToolCalls...)
	sort.SliceStable(calls, func(i, j int) bool { return calls[i].SequenceNumber < calls[j].SequenceNumber })
	if len(calls) == 0 {
		return fmt.Sprintf("Attempt %d with no tool calls", attempt.SequenceNumber)
	}
	names := make([]string, len(calls))
	for i, call := range calls {
		if call.Error == nil {
			names[i] = call.ToolName
		} else {
			names[i] = call.ToolName + " (error: " + quote(*call.Error) + ")"
		}
	}
	return fmt.Sprintf("Attempt %d using tools [%s]", attempt.SequenceNumber, strings.Join(names, ", "))
}

func lessonText(status VerificationStatus, attempts []Attempt, checks []string) string {
	verdict := "Unverified"
	switch status {
	case VerificationVerified:
		verdict = "Verified"
	case VerificationFailed:
		verdict = "Did not verify"
	}
	noun := "attempts"
	if len(attempts) == 1 {
		noun = "attempt"
	}
	lesson := fmt.Sprintf("%s after %d %s.", verdict, len(attempts), noun)
	var failed []Attempt
	for _, attempt := range attempts {
		if attempt.Error != nil {
			failed = append(failed, attempt)
		}
	}
	if len(failed) > maxLessonFailedAttempts {
		last := failed[len(failed)-1]
		lesson += fmt.Sprintf(" Failed: %d attempts; last: attempt %d — %s.", len(failed), last.SequenceNumber,
			ErrorClass(last.Error))
	} else {
		for _, attempt := range failed {
			lesson += fmt.Sprintf(" Failed: attempt %d — %s.", attempt.SequenceNumber, ErrorClass(attempt.Error))
		}
	}
	if status == VerificationVerified && len(attempts) > 0 && attempts[len(attempts)-1].Error == nil {
		lesson += fmt.Sprintf(" Worked: attempt %d.", attempts[len(attempts)-1].SequenceNumber)
	}
	if len(checks) > 0 {
		lesson += " Checks: [" + strings.Join(checks, ", ") + "]."
	}
	return lesson
}

func bound(items []string, noun string) []string {
	if len(items) > maxListItems {
		shown := maxListItems - 1
		items = append(append([]string(nil), items[:shown]...),
			strconv.Itoa(len(items)-shown)+" further "+noun+" are not listed.")
	}
	result := make([]string, len(items))
	for i, item := range items {
		result[i] = clip(item, maxListItemLength)
	}
	return result
}

// Reflect is the deterministic reflection of a finished run. No tool name, argument or error text beyond its class
// reaches the lesson.
func Reflect(run *CapturedRun, evaluation *VerificationResult, reflectionID UUID, createdAt Instant) Reflection {
	return reflectObserved(context.Background(), false, run, evaluation, reflectionID, createdAt)
}

func reflectObserved(ctx context.Context, nested bool, run *CapturedRun, evaluation *VerificationResult,
	reflectionID UUID, createdAt Instant) Reflection {
	_, op := Observe(ctx, "reflect", nested)
	defer op.End()
	if op != nil {
		op.Set(AttributeRunID, run.RunID.String())
		op.Set(AttributeReflectionID, reflectionID.String())
	}
	reflection := reflectRun(run, evaluation, reflectionID, createdAt)
	op.Done(string(evaluation.Outcome.Status))
	return reflection
}

func reflectRun(run *CapturedRun, evaluation *VerificationResult, reflectionID UUID, createdAt Instant) Reflection {
	outcome := evaluation.Outcome
	status := outcome.Status
	var evidenceIDs []UUID
	var passingAll, failingAll []string
	for _, item := range outcome.Evidence {
		evidenceIDs = append(evidenceIDs, item.EvidenceID)
		switch item.Result {
		case CheckPass:
			passingAll = append(passingAll, item.CheckID)
		case CheckFail:
			failingAll = append(failingAll, item.CheckID)
		}
	}
	evidenceIDs = distinct(evidenceIDs)
	passing, failing := distinct(passingAll), distinct(failingAll)
	scoreText := formatRoundTrip(evaluation.CompletionScore)
	attempts := append([]Attempt(nil), run.Attempts...)
	sort.SliceStable(attempts, func(i, j int) bool { return attempts[i].SequenceNumber < attempts[j].SequenceNumber })

	var warnings, successful, failed []string
	var inconclusiveAll []string
	for _, check := range evaluation.RequiredChecks {
		if !contains(passing, check.CheckID) && !contains(failing, check.CheckID) {
			inconclusiveAll = append(inconclusiveAll, check.CheckID)
		}
	}
	inconclusive := distinct(inconclusiveAll)
	checks := inconclusive
	switch status {
	case VerificationVerified:
		checks = passing
	case VerificationFailed:
		checks = failing
	}
	lesson := lessonText(status, attempts, checks)
	switch status {
	case VerificationVerified:
		warnings = append(warnings, verificationScopeWarning)
		if len(evidenceIDs) == 0 {
			warnings = append(warnings, "Verification status is Verified, but the evaluation supplied no evidence.")
		}
	case VerificationFailed:
		warnings = append(warnings, "Not a validated procedure: task verification status is Failed.",
			"Verification failed: required checks ["+strings.Join(failing, ", ")+"] failed.")
	default:
		warnings = append(warnings, "Not a validated procedure: task verification status is Unknown.",
			"Unverified: not every required check reached a conclusive result in the evaluation.")
	}
	if status != VerificationVerified && !isBlank(outcome.Reason) {
		warnings = append(warnings, "Evaluation reason: "+quote(*outcome.Reason)+".")
	}
	if status != VerificationVerified && evaluation.CompletionScore > 0 {
		warnings = append(warnings, "Completion score "+scoreText+" (rule "+evaluation.RuleVersion+
			") is a partial score and is not verification.")
	}
	if run.ExecutionStatus != RunCompleted {
		warnings = append(warnings, "Run execution status is "+string(run.ExecutionStatus)+", not Completed.")
	}
	if len(attempts) == 0 {
		warnings = append(warnings, "No attempts were captured.")
	}

	for index := range attempts {
		attempt := &attempts[index]
		isFinal := index == len(attempts)-1
		description := describe(attempt)
		if attempt.Error != nil {
			failed = append(failed, description+" ended with error: "+quote(*attempt.Error)+".")
			if isFinal && status == VerificationVerified {
				warnings = append(warnings, fmt.Sprintf("Attempt %d (final) ended with an error despite a Verified "+
					"status; no captured attempt is recorded as a successful approach.", attempt.SequenceNumber))
			}
			continue
		}
		if !isFinal {
			warnings = append(warnings, fmt.Sprintf("Attempt %d completed without an error but was followed by a "+
				"later attempt; its contribution to the outcome is unknown.", attempt.SequenceNumber))
			continue
		}
		switch status {
		case VerificationVerified:
			successful = append(successful, description+" completed without an error (result: "+
				quoteOrMissing(attempt.Result)+") as the final attempt of a verified run.")
		case VerificationFailed:
			failed = append(failed, description+" completed without an error (result: "+
				quoteOrMissing(attempt.Result)+") as the final attempt, but verification failed.")
		default:
			warnings = append(warnings, fmt.Sprintf("Attempt %d (final) completed without an error, but "+
				"verification is Unknown; it is not recorded as a successful approach.", attempt.SequenceNumber))
		}
	}

	var preconditions []string
	unknownPrecondition := false
	addPrecondition := func(label string, value *string) {
		if isBlank(value) {
			preconditions = append(preconditions, label+": "+unknownValue)
			warnings = append(warnings, "Precondition '"+label+"' was not captured and is "+unknownValue+".")
			unknownPrecondition = true
			return
		}
		preconditions = append(preconditions, label+": "+*value)
	}
	environment := run.Environment
	addPrecondition("Runtime version", &environment.RuntimeVersion)
	addPrecondition("Operating system", &environment.OperatingSystem)
	addPrecondition("Application version", environment.ApplicationVersion)
	// The run's model is bookkeeping (ModelMetadataKey), never a precondition.
	metadata := withoutModel(environment.Metadata)
	for _, key := range sortedKeys(metadata) {
		value := metadata[key]
		addPrecondition(reflectorPreconditionLabelPrefix+key+"]", &value)
	}

	var guidance string
	if status == VerificationVerified {
		guidance = "Reuse only where the listed preconditions match, and re-run required checks [" +
			strings.Join(passing, ", ") + "] to confirm the outcome in the new context."
		if unknownPrecondition {
			guidance += " Confirm the unknown preconditions before reuse."
		}
	} else {
		guidance = "Do not reuse as a validated procedure: task verification status is " + string(status) +
			". Treat the listed approaches as observed history only, and verify any approach independently before " +
			"relying on it."
	}
	guidance = clip(guidance, maxLessonLength)
	if evidenceIDs == nil {
		evidenceIDs = []UUID{}
	}

	return Reflection{
		ReflectionID:            reflectionID,
		ExperienceRunID:         run.RunID,
		Lesson:                  clip(lesson, maxLessonLength),
		SuccessfulApproaches:    bound(successful, "successful approaches"),
		FailedApproaches:        bound(failed, "failed approaches"),
		Preconditions:           bound(preconditions, "preconditions"),
		Warnings:                bound(warnings, "warnings"),
		ReuseGuidance:           &guidance,
		EvidenceIDs:             evidenceIDs,
		VerificationStatus:      status,
		CompletionScore:         evaluation.CompletionScore,
		VerificationRuleVersion: evaluation.RuleVersion,
		Producer:                ReflectorProducer,
		CreatedAt:               createdAt,
		Authorship:              AuthorshipDeterministic,
	}
}

// sortedKeys are a map's keys in ordinal (UTF-16) order.
func sortedKeys(m map[string]string) []string {
	keys := make([]string, 0, len(m))
	for key := range m {
		keys = append(keys, key)
	}
	sort.Slice(keys, func(i, j int) bool { return utf16Compare(keys[i], keys[j]) < 0 })
	return keys
}

// ---- Screening ---------------------------------------------------------------------------------------------------

// ErrScreeningRefused is a reflection screening refused: the record is stored Quarantined, without it. A refusal's
// message carries no content.
var ErrScreeningRefused = errors.New("screening refused the reflection")

func screeningRefused(format string, args ...any) error {
	return fmt.Errorf("%w: %s", ErrScreeningRefused, fmt.Sprintf(format, args...))
}

// neutralize is screening's hygiene: line and paragraph separators become a space; control, format, private-use,
// unassigned and blank-but-not-space characters are removed (a whitespace one becomes a space); a run of combining
// marks keeps its first four.
func neutralize(value string) string { return visible(value, true, false) }

func cleanText(text string) (string, bool) {
	neutralized := neutralize(text)
	if isNullOrWhitespace(neutralized) {
		return "", false
	}
	return neutralized, true
}

func ensureScreenedLength(text string, limit int, field string) error {
	if UTF16Len(text) > limit {
		return screeningRefused("its %s is longer than the %d-character limit", field, limit)
	}
	return nil
}

// IsModelAuthored reports whether a reflection was written by a model: any authorship but Deterministic, or the
// library's own model-backed reflector.
func IsModelAuthored(reflection *Reflection) bool {
	return reflection != nil && ((reflection.Authorship != "" && reflection.Authorship != AuthorshipDeterministic) ||
		strings.HasPrefix(reflection.Producer, libraryModelReflectorPrefix))
}

// Screen is the screening finalization applies before a reflection is stored, for a deterministic reflection:
// every text neutralized, blank list items dropped, the limits enforced. It returns an error wrapping
// ErrScreeningRefused.
func Screen(reflection Reflection) (Reflection, error) {
	if IsModelAuthored(&reflection) {
		// The model-authored content guard is outside contract 1.x: a port writes deterministic reflections only.
		return Reflection{}, screeningRefused("a model-authored reflection is outside contract 1.x")
	}
	producer, ok := cleanText(reflection.Producer)
	if !ok {
		return Reflection{}, screeningRefused("its Producer is missing, or empty once invisible characters are removed")
	}
	if err := ensureScreenedLength(producer, maxProducerLength, "Producer"); err != nil {
		return Reflection{}, err
	}
	lesson, ok := cleanText(reflection.Lesson)
	if !ok {
		return Reflection{}, screeningRefused("its Lesson is missing, or empty once invisible characters are removed")
	}
	if err := ensureScreenedLength(lesson, maxLessonLength, "Lesson"); err != nil {
		return Reflection{}, err
	}
	var guidance *string
	if reflection.ReuseGuidance != nil {
		if cleaned, ok := cleanText(*reflection.ReuseGuidance); ok {
			if err := ensureScreenedLength(cleaned, maxLessonLength, "ReuseGuidance"); err != nil {
				return Reflection{}, err
			}
			guidance = &cleaned
		}
	}
	screenedList := func(items []string, field string) ([]string, error) {
		if len(items) > maxListItems {
			return nil, screeningRefused("its %s holds more items than the %d-item limit", field, maxListItems)
		}
		kept := []string{}
		for index, item := range items {
			if text, ok := cleanText(item); ok {
				if err := ensureScreenedLength(text, maxListItemLength, fmt.Sprintf("%s[%d]", field, index)); err != nil {
					return nil, err
				}
				kept = append(kept, text)
			}
		}
		return kept, nil
	}
	var err error
	screened := reflection
	screened.Lesson, screened.ReuseGuidance, screened.Producer = lesson, guidance, producer
	if screened.SuccessfulApproaches, err = screenedList(reflection.SuccessfulApproaches, "SuccessfulApproaches"); err != nil {
		return Reflection{}, err
	}
	if screened.FailedApproaches, err = screenedList(reflection.FailedApproaches, "FailedApproaches"); err != nil {
		return Reflection{}, err
	}
	if screened.Preconditions, err = screenedList(reflection.Preconditions, "Preconditions"); err != nil {
		return Reflection{}, err
	}
	if screened.Warnings, err = screenedList(reflection.Warnings, "Warnings"); err != nil {
		return Reflection{}, err
	}
	return screened, nil
}
