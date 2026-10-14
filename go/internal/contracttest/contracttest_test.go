package contracttest

import (
	"encoding/json"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
)

func TestNumbersCompareByValueAndKind(t *testing.T) {
	for _, c := range []struct {
		expected, actual string
		options          Options
		equal            bool
	}{
		{"1", "1", Options{}, true},
		{"1", "1.0", Options{}, false},
		{"1", "1.0", Options{IgnoreKind: true}, true},
		{"100", "1E2", Options{IgnoreKind: true}, true},
		{"1E2", "100.0", Options{}, true},
		{"-0", "0", Options{}, true},
		{"-0.0", "0.0", Options{}, true},
		{"0.6000000238418579", "0.6", Options{}, false},
		{"0.6000000238418579", "0.6", Options{Tolerance: 1e-6}, true},
		{"15000000000000000000", "15000000000000000001", Options{}, false},
	} {
		difference := Difference(json.Number(c.expected), json.Number(c.actual), "$", c.options)
		if (difference == "") != c.equal {
			t.Errorf("%s vs %s (%+v): %q", c.expected, c.actual, c.options, difference)
		}
	}
}

func TestValuesCompareByMembersNotOrder(t *testing.T) {
	expected := ae.NewObject(ae.Member{Key: "a", Value: []any{true, nil}}, ae.Member{Key: "b", Value: "x"})
	actual := ae.NewObject(ae.Member{Key: "b", Value: "x"}, ae.Member{Key: "a", Value: []any{true, nil}})
	if difference := Difference(expected, actual, "$", Options{}); difference != "" {
		t.Fatal(difference)
	}
	for _, other := range []any{
		ae.NewObject(ae.Member{Key: "a", Value: []any{true, nil}}),
		ae.NewObject(ae.Member{Key: "a", Value: []any{true}}, ae.Member{Key: "b", Value: "x"}),
		ae.NewObject(ae.Member{Key: "a", Value: []any{false, nil}}, ae.Member{Key: "b", Value: "x"}),
		ae.NewObject(ae.Member{Key: "a", Value: []any{true, ""}}, ae.Member{Key: "b", Value: "x"}),
		"x",
	} {
		if Difference(expected, other, "$", Options{}) == "" {
			t.Errorf("%v equals %v", expected, other)
		}
	}
}
