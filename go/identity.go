package agentexperience

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
)

// Who a run is for: the identity an invocation runs under, and the development-only identity.
//
// In production the identity comes from the host's own authentication, never from model output. The development
// identity runs every invocation as one fixed caller in one fixed scope, so it refuses outside a development or test
// environment, by the .NET library's rule: the environment is allowed when its name is Development, Test or Testing
// (ignoring case). The name is the one the host passes, else the AGENTEXPERIENCE_ENVIRONMENT variable, else .NET's
// DOTNET_ENVIRONMENT, else ASPNETCORE_ENVIRONMENT; with none of them there is nothing to check, so it is allowed.

// AllowedEnvironments are the environment names the development identity runs in, compared ignoring case.
var AllowedEnvironments = []string{"Development", "Test", "Testing"}

// EnvironmentVariables are the variables the environment name is read from, in order: this port's own first, then
// .NET's.
var EnvironmentVariables = []string{"AGENTEXPERIENCE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT"}

// The development identity's fixed parts.
const (
	DevelopmentTenant      = "local"
	DevelopmentPrincipal   = "local-developer"
	DevelopmentApplication = "app"
	maxReducedNameLength   = 100
)

// ErrDevelopmentOnly is a development-only convenience used outside a development or test environment.
var ErrDevelopmentOnly = errors.New("development and tests only")

// Identity is who an invocation runs under: the principal it is authorized as, the scope experience is read from
// and stored under, and the authorization bounds every store transaction declares (by default, bounds equal to the
// scope). It must come from the host's own authentication, never from model output.
type Identity struct {
	Principal string
	Scope     Scope
	// Bounds are BoundsOf(Scope) when nil.
	Bounds *AuthorizationBounds

	development     bool
	environmentName *string
}

// Authorization is the bounds every transaction declares: Bounds, else bounds equal to the scope.
func (i Identity) Authorization() AuthorizationBounds {
	if i.Bounds != nil {
		return *i.Bounds
	}
	return BoundsOf(i.Scope)
}

// IsDevelopment reports whether this is a development identity (made by DevelopmentIdentity).
func (i Identity) IsDevelopment() bool { return i.development }

// Validate refuses a blank principal, tenant, application or project, and a development identity outside a development or test
// environment (checked against the host environment name it was made with, else the variables, now).
func (i Identity) Validate() error {
	if isNullOrWhitespace(i.Principal) {
		return errors.New("the identity's principal must not be blank")
	}
	if isNullOrWhitespace(i.Scope.TenantID) || isNullOrWhitespace(i.Scope.ApplicationID) ||
		isNullOrWhitespace(i.Scope.ProjectID) {
		return errors.New("the identity's tenant, application and project must not be blank")
	}
	if i.development {
		return EnsureDevelopmentEnvironment(i.environmentName)
	}
	return nil
}

// ReadEnvironmentName is the environment's name and the variable it was read from: AGENTEXPERIENCE_ENVIRONMENT,
// else .NET's variables; ok is false with none set.
func ReadEnvironmentName() (name, variable string, ok bool) {
	for _, variable := range EnvironmentVariables {
		if value, set := os.LookupEnv(variable); set {
			return value, variable, true
		}
	}
	return "", "", false
}

// IsDevelopmentEnvironment reports whether the process runs in a development or test environment. A non-nil name
// is the host's own environment name (like .NET's host environment) and is checked alone; otherwise
// EnvironmentVariables are read in order, and with none set it is allowed.
func IsDevelopmentEnvironment(name *string) bool {
	if name == nil {
		value, _, ok := ReadEnvironmentName()
		if !ok {
			return true
		}
		name = &value
	}
	for _, allowed := range AllowedEnvironments {
		if equalsIgnoreCase(*name, allowed) {
			return true
		}
	}
	return false
}

// EnsureDevelopmentEnvironment is an error wrapping ErrDevelopmentOnly outside a development or test environment.
func EnsureDevelopmentEnvironment(name *string) error {
	if IsDevelopmentEnvironment(name) {
		return nil
	}
	source, value := "the host environment", ""
	if name != nil {
		value = *name
	} else {
		value, source, _ = ReadEnvironmentName()
	}
	return errors.Join(ErrDevelopmentOnly, errors.New("the development identity runs every invocation as one fixed "+
		"caller in one fixed scope, so everything stored is shared by everyone using the process. It runs only when "+
		"the environment is "+strings.Join(AllowedEnvironments, ", ")+", and "+source+" is '"+value+"'. Resolve the "+
		"identity from your own authentication instead"))
}

// ReduceName reduces a name the host did not choose as an identifier (an agent's name, the program's name) to one:
// lower case, ASCII letters, digits, '.' and '_' kept, every run of other characters (dashes included) one '-', no
// separator at either end, at most 100 characters, never fusing two words at the cut. ok is false when nothing is
// kept.
func ReduceName(name string) (reduced string, ok bool) {
	if isNullOrWhitespace(name) {
		return "", false
	}
	var text strings.Builder
	pendingSeparator := false
	for _, r := range name {
		keep := (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '.' || r == '_'
		if !keep {
			pendingSeparator = true
			continue
		}
		needed := 1
		if pendingSeparator && text.Len() > 0 {
			needed = 2
		}
		if text.Len()+needed > maxReducedNameLength {
			// Stop rather than fuse two words or cut the separator off its word.
			break
		}
		if needed == 2 {
			text.WriteByte('-')
		}
		pendingSeparator = false
		if r >= 'A' && r <= 'Z' {
			r += 'a' - 'A'
		}
		text.WriteRune(r)
	}
	reduced = strings.Trim(text.String(), "._-")
	return reduced, reduced != ""
}

// DevelopmentOptions are the development identity's optional parts.
type DevelopmentOptions struct {
	// Tenant is DevelopmentTenant when empty.
	Tenant string
	// Application names the application and the project (reduced to an identifier); when empty, the program's file
	// name, else DevelopmentApplication.
	Application string
	// EnvironmentName is the host's own environment name, when it has one; nil reads EnvironmentVariables.
	EnvironmentName *string
}

func programName() string {
	if len(os.Args) == 0 || os.Args[0] == "" {
		return ""
	}
	base := filepath.Base(os.Args[0])
	return strings.TrimSuffix(base, filepath.Ext(base))
}

// DevelopmentIdentity is one fixed local identity, for development and tests only: tenant "local" (or
// options.Tenant), principal "local-developer", and the application and project named after options.Application,
// else the program's file name, else "app". Outside a development or test environment it is an error wrapping
// ErrDevelopmentOnly.
func DevelopmentIdentity(options DevelopmentOptions) (Identity, error) {
	tenant := options.Tenant
	if tenant == "" {
		tenant = DevelopmentTenant
	} else if isNullOrWhitespace(tenant) {
		return Identity{}, errors.New("the development tenant must not be blank; leave it empty for the default, 'local'")
	}
	if err := EnsureDevelopmentEnvironment(options.EnvironmentName); err != nil {
		return Identity{}, err
	}
	segment, ok := ReduceName(options.Application)
	if !ok {
		if segment, ok = ReduceName(programName()); !ok {
			segment = DevelopmentApplication
		}
	}
	var environmentName *string
	if options.EnvironmentName != nil {
		environmentName = Ptr(*options.EnvironmentName)
	}
	return Identity{
		Principal:       DevelopmentPrincipal,
		Scope:           Scope{TenantID: tenant, ApplicationID: segment, ProjectID: segment},
		development:     true,
		environmentName: environmentName,
	}, nil
}
