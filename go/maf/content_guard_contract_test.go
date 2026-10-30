package maf

// Contract 1.14's content-guard corpus: every field through this port's guard (on the NFKC this module registers),
// compared with what .NET's guard answers: the folded text, the links found in it, the rule it breaks, and the rule
// all the fields joined break. A case Go cannot express is skipped by name.

import (
	"path/filepath"
	"slices"
	"sort"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func strings_(value any) []string {
	var list []string
	for _, item := range value.([]any) {
		list = append(list, item.(string))
	}
	return list
}

func sortedSet(set map[string]bool) []string {
	list := []string{}
	for item := range set {
		list = append(list, item)
	}
	sort.Slice(list, func(i, j int) bool { return utf16Less(list[i], list[j]) })
	return list
}

func TestTheContentGuardCorpusIsWhatDotNetsGuardAnswers(t *testing.T) {
	corpus := ct.MustReadJSON(filepath.Join(ct.Fixtures, "content-guard", "cases.json")).(*ae.Object)
	cases := member(corpus, "cases").([]any)
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	skipped := 0
	for _, item := range cases {
		c := item.(*ae.Object)
		name := member(c, "name").(string)
		if languages, ok := member(c, "notRepresentableIn").([]any); ok && slices.Contains(languages, any("Go")) {
			skipped++
			continue
		}
		t.Run(name, func(t *testing.T) {
			var run []string
			if content := member(c, "runContent"); content != nil {
				run = strings_(content)
			}
			var values []string
			for _, fieldItem := range member(c, "fields").([]any) {
				field := fieldItem.(*ae.Object)
				value := member(field, "value").(string)
				values = append(values, value)
				folded, ok := ae.ModelTextFold(value)
				if !ok || folded != member(field, "folded") {
					t.Errorf("%q folds to %q, not %q", value, folded, member(field, "folded"))
				}
				links, ok := ae.ModelTextLinksIn(folded)
				expected := member(field, "links").(*ae.Object)
				if !ok || !slices.Equal(sortedSet(links.URLs), orEmpty(strings_(member(expected, "urls")))) ||
					!slices.Equal(sortedSet(links.Hosts), orEmpty(strings_(member(expected, "hosts")))) ||
					!slices.Equal(sortedSet(links.Addresses), orEmpty(strings_(member(expected, "addresses")))) ||
					links.AlwaysRefused != member(expected, "alwaysRefused") {
					t.Errorf("%q: links %v %v %v %v, not %v", value, sortedSet(links.URLs), sortedSet(links.Hosts),
						sortedSet(links.Addresses), links.AlwaysRefused, expected)
				}
				rule, _ := member(field, "rule").(string)
				if actual := ae.ModelTextCheck(value, run); actual != rule {
					t.Errorf("%q: rule %q, not %q", value, actual, rule)
				}
			}
			combined, _ := member(c, "combinedRule").(string)
			if actual := ae.ModelTextCheckCombined(values); actual != combined {
				t.Errorf("combined rule %q, not %q", actual, combined)
			}
		})
	}
	if skipped != 2 {
		t.Fatalf("skipped %d cases, not the two Go cannot express", skipped)
	}
}

func orEmpty(list []string) []string {
	if list == nil {
		return []string{}
	}
	return list
}
