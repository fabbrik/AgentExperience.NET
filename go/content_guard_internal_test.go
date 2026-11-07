package agentexperience

// The content guard's rules the corpus (run in the maf module, which registers NFKC) does not state: the invariant
// lower case, the work bound and its two outcomes, and the screening backstop.

import (
	"context"
	"strings"
	"testing"
	"time"
)

func TestLowerInvariantLeavesTheDottedCapitalIAsDotNetDoes(t *testing.T) {
	if lowerInvariant("İ") != "İ" || lowerInvariant("K") != "k" || lowerInvariant("ABC") != "abc" {
		t.Fatalf("%q %q", lowerInvariant("İ"), lowerInvariant("K"))
	}
}

func identityNormalizer(t *testing.T) {
	SetUnicodeNormalizer(func(s string) string { return s })
	t.Cleanup(func() { SetUnicodeNormalizer(nil) })
}

func TestALargeWhitespaceHeavyRunIsReadInLinearTime(t *testing.T) {
	identityNormalizer(t)
	// The bound is not what this measures (the race detector slows every step): the work is linear.
	limit := guardTimeLimit
	guardTimeLimit = time.Minute
	t.Cleanup(func() { guardTimeLimit = limit })
	var text strings.Builder
	for range 20_000 {
		text.WriteString("    [ \t ( { sk-sk-sk- eyJeyJ pwdpwd dot  ")
	}
	text.WriteString(" see status.example.com")
	started := time.Now()
	links := linksOf([]string{text.String()})
	if !links.hosts["status.example.com"] {
		t.Fatalf("the run's hostname was not read: %v", links.hosts)
	}
	if rule := guardCheck(text.String(), links); rule != "" {
		t.Fatalf("%q", rule)
	}
	if elapsed := time.Since(started); elapsed > 30*time.Second {
		t.Fatalf("%v for %d characters", elapsed, text.Len())
	}
}

func TestPastTheWorkBoundRunContentAdmitsNothingAndAFieldIsRefused(t *testing.T) {
	identityNormalizer(t)
	limit := guardTimeLimit
	guardTimeLimit = -time.Second
	t.Cleanup(func() { guardTimeLimit = limit })
	text := strings.Repeat("see status.example.com ", 200)
	if links := linksOf([]string{text}); len(links.hosts) != 0 {
		t.Fatalf("a run content text read past its bound admitted %v", links.hosts)
	}
	if rule := guardCheck(text, runLinks{map[string]bool{}, map[string]bool{}, map[string]bool{}}); rule != "text that could not be checked in time" {
		t.Fatalf("%q", rule)
	}
	if rule := guardCheckCombined([]string{text}); rule != "text that could not be checked in time" {
		t.Fatalf("%q", rule)
	}
	if !ModelTextHasLink(text) {
		t.Fatal("a model name read past its bound was not dropped")
	}
}

func TestAScreeningThatPanicsIsRefusedUnreadable(t *testing.T) {
	SetUnicodeNormalizer(func(string) string { panic("the normalizer failed") })
	t.Cleanup(func() { SetUnicodeNormalizer(nil) })
	run, verification := verifiedRun(t)
	finalized, err := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: reflectorFunc(
		func(_ context.Context, request ReflectionRequest) (Reflection, error) {
			return modelWritten(request, "Wait."), nil
		})})
	refusal, ok := ScreeningRefusalOf(finalized.ScreeningRefusal)
	if err != nil || !ok || refusal.Code != RefusalUnreadable || refusal.ExceptionType != "string" ||
		finalized.Record.Status != StatusQuarantined || finalized.Record.Reflection != nil ||
		strings.Contains(refusal.Error(), "normalizer failed") {
		t.Fatalf("%+v %v", finalized.ScreeningRefusal, err)
	}
}

func TestNilEvidenceIDsDoNotMatchTheRequest(t *testing.T) {
	run, verification := verifiedRun(t)
	finalized, err := FinalizeContextWith(context.Background(), run, verification, FinalizeOptions{Reflector: reflectorFunc(
		func(_ context.Context, request ReflectionRequest) (Reflection, error) {
			reflection := modelWritten(request, "Wait.")
			reflection.EvidenceIDs = nil
			return reflection, nil
		})})
	if err != nil || finalized.Record.Status != StatusQuarantined || !strings.Contains(finalized.ReflectionFailure.Error(), "EvidenceIds") {
		t.Fatalf("%+v %v", finalized.ReflectionFailure, err)
	}
}
