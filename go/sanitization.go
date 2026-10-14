package agentexperience

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"strconv"
	"strings"
)

// Sanitization by field name, as capture applies it (contract step 1). A field whose name is a secret name is
// redacted to "" whatever its value, before anything inside it is read. An allowlisted field (matched exactly) is
// kept, and an object or array value is walked one level deeper under the same rules. Every other field is
// omitted. A payload beyond the limits, or holding a number a double cannot hold finitely, is refused whole.

// DefaultSecretFieldNames are the default secret names, matched ignoring case ordinally (a new slice each call).
func DefaultSecretFieldNames() []string {
	return []string{"password", "secret", "token", "apiKey", "api_key", "authorization", "connectionString"}
}

var defaultSecretFieldNames = DefaultSecretFieldNames()

// ValueField is the single field a tool result or error is sanitized as.
const ValueField = "value"

// ErrSanitizationRejected is a payload beyond the limits: refused whole, never partly kept. A refusal's message
// names the rule and the path of the field, object or array that broke it ("$" is the payload itself; a field
// name that breaks a rule is named by its position, never quoted), and never any content.
var ErrSanitizationRejected = errors.New("sanitization rejected the payload")

func rejected(format string, args ...any) error {
	return fmt.Errorf("%w: %s", ErrSanitizationRejected, fmt.Sprintf(format, args...))
}

// SanitizationPolicy is which field names are kept, which are secrets, and the limits a payload must stay within.
type SanitizationPolicy struct {
	// AllowedFieldNames are kept, matched exactly.
	AllowedFieldNames map[string]bool
	// SecretFieldNames are redacted, matched ignoring case ordinally; nil means DefaultSecretFieldNames().
	SecretFieldNames []string
	// Limits are the limits a payload must stay within; the zero value means
	// DefaultSanitizationLimits(), and any other must pass SanitizationLimits.Validate.
	Limits SanitizationLimits
}

// NewSanitizationPolicy is the default policy with exactly allowed allowlisted.
func NewSanitizationPolicy(allowed ...string) SanitizationPolicy {
	names := make(map[string]bool, len(allowed))
	for _, name := range allowed {
		names[name] = true
	}
	return SanitizationPolicy{AllowedFieldNames: names}
}

func (p *SanitizationPolicy) limits() SanitizationLimits {
	if p.Limits == (SanitizationLimits{}) {
		return DefaultSanitizationLimits()
	}
	return p.Limits
}

// IsSecret reports whether name is a secret name, ignoring case ordinally (as .NET's OrdinalIgnoreCase does: the
// long s is not s, and the dotless i is not i).
func (p *SanitizationPolicy) IsSecret(name string) bool {
	secrets := p.SecretFieldNames
	if secrets == nil {
		secrets = defaultSecretFieldNames
	}
	for _, secret := range secrets {
		if equalsIgnoreCase(secret, name) {
			return true
		}
	}
	return false
}

// Sanitize is the sanitized fields. It returns an error wrapping ErrSanitizationRejected for a payload beyond the
// limits. A field's value may be any JSON value DecodeJSON reads, or a Go one: a map with string keys, a slice, an
// integer or a float (a NaN or an infinity is refused). Any other value is omitted, never passed through
// unexamined.
// Limits that fail SanitizationLimits.Validate are an error that does not wrap ErrSanitizationRejected.
func (p *SanitizationPolicy) Sanitize(fields *Object) (*Object, error) {
	if err := p.Limits.Validate(); err != nil {
		return nil, err
	}
	if err := p.ensureDepth(1, ""); err != nil {
		return nil, err
	}
	return p.fields(fields, 1, "")
}

// pathOf is how a refusal names a path: "$" for the payload itself.
func pathOf(path string) string {
	if path == "" {
		return "$"
	}
	return path
}

func (p *SanitizationPolicy) fields(fields *Object, depth int, prefix string) (*Object, error) {
	limits := p.limits()
	if err := p.ensureCount(fields.Len(), prefix); err != nil {
		return nil, err
	}
	sanitized := &Object{}
	for index, m := range fields.Members() {
		key := m.Key
		if UTF16Len(key) > limits.MaxFieldNameLength {
			return nil, rejected("the name of field %d of '%s' is longer than the configured limit", index, pathOf(prefix))
		}
		if strings.ContainsAny(key, ".[]") {
			return nil, rejected("the name of field %d of '%s' contains '.', '[' or ']'", index, pathOf(prefix))
		}
		path := key
		if prefix != "" {
			path = prefix + "." + key
		}
		if s, ok := m.Value.(string); ok {
			if err := p.ensureLength(s, path); err != nil {
				return nil, err
			}
		}
		if p.IsSecret(key) {
			// Redacted whole, whatever its shape, before anything inside it is read.
			sanitized.Set(key, "")
			continue
		}
		if !p.AllowedFieldNames[key] {
			continue
		}
		kept, keep, err := p.value(m.Value, depth, path)
		if err != nil {
			return nil, err
		}
		if keep {
			sanitized.Set(key, kept)
		}
	}
	return sanitized, nil
}

func (p *SanitizationPolicy) value(value any, depth int, path string) (any, bool, error) {
	switch v := value.(type) {
	case nil:
		return nil, true, nil
	case bool:
		return v, true, nil
	case json.Number:
		if !isJSONNumber(string(v)) {
			return nil, false, nil
		}
		if !isFiniteNumber(v) {
			return nil, false, notFinite(path)
		}
		return v, true, nil
	case float64:
		if math.IsNaN(v) || math.IsInf(v, 0) {
			return nil, false, notFinite(path)
		}
		return json.Number(formatRoundTrip(v)), true, nil
	case float32:
		if math.IsNaN(float64(v)) || math.IsInf(float64(v), 0) {
			return nil, false, notFinite(path)
		}
		return json.Number(formatShortest(float64(v), 32)), true, nil
	case string:
		if err := p.ensureLength(v, path); err != nil {
			return nil, false, err
		}
		return v, true, nil
	case *Object:
		if v == nil {
			return nil, true, nil
		}
		if err := p.ensureDepth(depth+1, path); err != nil {
			return nil, false, err
		}
		fields, err := p.fields(v, depth+1, path)
		return fields, err == nil, err
	case []any:
		if err := p.ensureDepth(depth+1, path); err != nil {
			return nil, false, err
		}
		items, err := p.list(v, depth+1, path)
		return items, err == nil, err
	}
	if normalized, ok := normalizeGoValue(value); ok {
		return p.value(normalized, depth, path)
	}
	// An unrecognised shape is never passed through unexamined.
	return nil, false, nil
}

func notFinite(path string) error {
	// Contract 1.2: .NET reads a number beyond a double's range back as an infinity it cannot write again, so it is
	// refused. The reason names the path, never the value.
	return rejected("a number at field '%s' is not finite as a double", path)
}

func (p *SanitizationPolicy) list(items []any, depth int, path string) ([]any, error) {
	sanitized := []any{}
	for index, item := range items {
		if err := p.ensureCount(index+1, path); err != nil {
			return nil, err
		}
		kept, keep, err := p.value(item, depth, path+"["+strconv.Itoa(index)+"]")
		if err != nil {
			return nil, err
		}
		if keep {
			sanitized = append(sanitized, kept)
		}
	}
	return sanitized, nil
}

func (p *SanitizationPolicy) ensureDepth(depth int, path string) error {
	if depth > p.limits().MaxDepth {
		return rejected("'%s' is nested deeper than the configured limit", pathOf(path))
	}
	return nil
}

func (p *SanitizationPolicy) ensureCount(count int, path string) error {
	if count > p.limits().MaxFieldCount {
		return rejected("'%s' holds more entries than the configured limit", pathOf(path))
	}
	return nil
}

func (p *SanitizationPolicy) ensureLength(value, path string) error {
	if UTF16Len(value) > p.limits().MaxValueLength {
		return rejected("the value of '%s' is longer than the configured limit", pathOf(path))
	}
	return nil
}

// Sanitization is the two policies capture applies: one to tool-call arguments, one to results and errors.
type Sanitization struct {
	ToolArguments SanitizationPolicy
	ToolResult    SanitizationPolicy
}

// DefaultSanitization keeps no argument value (nothing allowlisted), keeps results and errors, and redacts secret
// names.
func DefaultSanitization() Sanitization { return SanitizationAllowing() }

// SanitizationAllowing is the default, with exactly argumentNames allowlisted for tool-call arguments.
func SanitizationAllowing(argumentNames ...string) Sanitization {
	return Sanitization{
		ToolArguments: NewSanitizationPolicy(argumentNames...),
		ToolResult:    NewSanitizationPolicy(ValueField),
	}
}
