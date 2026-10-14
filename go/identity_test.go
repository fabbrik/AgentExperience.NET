package agentexperience

// The development identity and the environment rule: .NET's (src/Shared/DevelopmentEnvironment.Shared.cs), with
// this port's own variable read first, as the Python port reads its own.

import (
	"errors"
	"os"
	"strings"
	"testing"
)

// clearEnvironment unsets every environment variable the rule reads, for the test.
func clearEnvironment(t *testing.T) {
	t.Helper()
	for _, variable := range EnvironmentVariables {
		if value, set := os.LookupEnv(variable); set {
			t.Cleanup(func() { _ = os.Setenv(variable, value) })
			if err := os.Unsetenv(variable); err != nil {
				t.Fatal(err)
			}
		}
	}
}

func TestTheEnvironmentRuleReadsThisPortsVariableFirstThenDotNets(t *testing.T) {
	clearEnvironment(t)
	if !IsDevelopmentEnvironment(nil) {
		t.Fatal("with no variable set the environment should be allowed")
	}
	for _, c := range []struct {
		variables map[string]string
		allowed   bool
	}{
		{map[string]string{"DOTNET_ENVIRONMENT": "Development"}, true},
		{map[string]string{"DOTNET_ENVIRONMENT": "testing"}, true},
		{map[string]string{"DOTNET_ENVIRONMENT": "Production"}, false},
		{map[string]string{"DOTNET_ENVIRONMENT": ""}, false},
		{map[string]string{"DOTNET_ENVIRONMENT": "Staging"}, false},
		{map[string]string{"ASPNETCORE_ENVIRONMENT": "Production"}, false},
		{map[string]string{"DOTNET_ENVIRONMENT": "Test", "ASPNETCORE_ENVIRONMENT": "Production"}, true},
		{map[string]string{"AGENTEXPERIENCE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Development"}, false},
		{map[string]string{"AGENTEXPERIENCE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Production"}, true},
		// .NET's ordinal ignore-case folds no non-ASCII letter to an ASCII one: the dotless i is not an i.
		{map[string]string{"AGENTEXPERIENCE_ENVIRONMENT": "Testıng"}, false},
	} {
		t.Run("", func(t *testing.T) {
			for variable, value := range c.variables {
				t.Setenv(variable, value)
			}
			if IsDevelopmentEnvironment(nil) != c.allowed {
				t.Fatalf("%v: allowed should be %v", c.variables, c.allowed)
			}
		})
	}
}

func TestTheDevelopmentIdentityRefusesAProductionEnvironment(t *testing.T) {
	clearEnvironment(t)
	t.Setenv("DOTNET_ENVIRONMENT", "Production")
	_, err := DevelopmentIdentity(DevelopmentOptions{})
	if !errors.Is(err, ErrDevelopmentOnly) || !strings.Contains(err.Error(), "DOTNET_ENVIRONMENT is 'Production'") {
		t.Fatalf("expected a refusal naming the variable, got %v", err)
	}
	// The host's own environment name is checked alone, and kept with the identity.
	identity, err := DevelopmentIdentity(DevelopmentOptions{EnvironmentName: Ptr("Development")})
	if err != nil || !identity.IsDevelopment() || identity.Validate() != nil {
		t.Fatalf("%+v %v", identity, err)
	}
	_, err = DevelopmentIdentity(DevelopmentOptions{EnvironmentName: Ptr("Production")})
	if !errors.Is(err, ErrDevelopmentOnly) || !strings.Contains(err.Error(), "the host environment is 'Production'") {
		t.Fatalf("expected a refusal naming the host environment, got %v", err)
	}
}

func TestADevelopmentIdentityIsCheckedAgainWhereverItIsUsed(t *testing.T) {
	clearEnvironment(t)
	identity, err := DevelopmentIdentity(DevelopmentOptions{})
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("AGENTEXPERIENCE_ENVIRONMENT", "Production")
	if err := identity.Validate(); !errors.Is(err, ErrDevelopmentOnly) {
		t.Fatalf("a development identity validated outside development: %v", err)
	}
	// An identity from the host's authentication is never refused for the environment.
	host := Identity{Principal: "svc", Scope: Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}}
	if err := host.Validate(); err != nil || host.IsDevelopment() {
		t.Fatal(err)
	}
}

func TestTheDevelopmentIdentityIsOneFixedLocalCaller(t *testing.T) {
	clearEnvironment(t)
	identity, err := DevelopmentIdentity(DevelopmentOptions{Application: "Quick Start!"})
	if err != nil {
		t.Fatal(err)
	}
	expected := Scope{TenantID: DevelopmentTenant, ApplicationID: "quick-start", ProjectID: "quick-start"}
	if identity.Principal != DevelopmentPrincipal || !identity.Scope.Equal(expected) {
		t.Fatalf("%+v", identity)
	}
	if bounds := identity.Authorization(); bounds.TenantID != "local" || *bounds.ApplicationID != "quick-start" {
		t.Fatalf("%+v", bounds)
	}
	other, err := DevelopmentIdentity(DevelopmentOptions{Tenant: "acme", Application: "---"})
	if err != nil {
		t.Fatal(err)
	}
	// Nothing of the application is kept: the program's name is used ("agentexperience" for this test binary).
	if other.Scope.TenantID != "acme" || other.Scope.ApplicationID == "" || other.Scope.ApplicationID == "---" {
		t.Fatalf("%+v", other)
	}
	if _, err := DevelopmentIdentity(DevelopmentOptions{Tenant: " "}); err == nil {
		t.Fatal("a blank tenant was accepted")
	}
	if err := (Identity{Principal: " ", Scope: expected}).Validate(); err == nil {
		t.Fatal("a blank principal was accepted")
	}
	for _, scope := range []Scope{{TenantID: "t", ProjectID: "p"}, {TenantID: "t", ApplicationID: "a", ProjectID: " "},
		{ApplicationID: "a", ProjectID: "p"}} {
		if err := (Identity{Principal: "svc", Scope: scope}).Validate(); err == nil {
			t.Fatalf("%+v was accepted", scope)
		}
	}
}

func TestReduceNameKeepsAnIdentifier(t *testing.T) {
	for name, expected := range map[string]string{
		"Python Agent":                 "python-agent",
		"triage-ticket":                "triage-ticket",
		"  --Ünïcode__name.v2--  ":     "n-code__name.v2",
		"a—b":                          "a-b",
		strings.Repeat("ab ", 60):      strings.Repeat("ab-", 33) + "a",
		"x" + strings.Repeat("y", 120): "x" + strings.Repeat("y", 99),
	} {
		if actual, _ := ReduceName(name); actual != expected {
			t.Errorf("ReduceName(%q) = %q, want %q", name, actual, expected)
		}
	}
	for _, name := range []string{"", "   ", "---", "ü"} {
		if reduced, ok := ReduceName(name); ok {
			t.Errorf("ReduceName(%q) kept %q", name, reduced)
		}
	}
}
