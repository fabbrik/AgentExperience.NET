package agentexperience

import (
	"bytes"
	"encoding/json"
	"strconv"
	"strings"
	"sync"
	"unicode/utf16"
)

// Ready-made tool-result classifiers, for tools that report failure in their return value (an exit code, an HTTP
// status), with the semantics of .NET's ExperienceToolResultClassifiers. Each names an error class that is a single
// error-class token ("exit 3", "HTTP 503", or ToolReportedErrorClass), or none when the call did not fail.
//
// Each reads only the tool's own raw result:
//
//   - A JSON object (an *Object, a json.RawMessage or a string that parses as one, or any other Go value written as
//     JSON by encoding/json) is read by its top-level members only, whatever its size, so a code quoted inside its
//     output does not count. A JSON array counts as no code.
//   - Any other text is matched against the patterns given on each classifier, and the last match counts, so a final
//     exit code wins over an earlier step's. Text up to 1 MiB (in UTF-16 code units) is read whole; of longer text,
//     the first and last 64 KiB; a match running into the cut that ends the first does not count (it may be a code
//     cut short), and neither does a word match at the start of the last that continues a word before it.
//
// Matching ignores case (ASCII letters only, as .NET's invariant culture does for these patterns), and \b and \s are
// .NET's, through the same matcher the error class uses. With no tool names a classifier applies to every tool's
// result; give the names of the tools that report this way to leave every other tool's result alone (exact match).
// ExitCode and HTTPStatus panic on a blank tool name: a programming error, as an invalid pattern is to
// regexp.MustCompile.

// ToolReportedErrorClass is the class recorded for a returned call whose classifier named a class that is not one
// error-class token.
const ToolReportedErrorClass = "ToolReportedError"

const (
	maxWholeTextLength = 1024 * 1024
	edgeLength         = 64 * 1024
	maxExitCodeDigits  = 6
)

// ToolResultContext is what a tool-result classifier sees for one tool call that returned: the call, the tool, the
// arguments capture recorded for it, and the raw value it returned. Nothing else, in particular no model text other
// than the tool's own result.
//
// The arguments are the raw ones, before sanitization, so secrets the model passed reach the classifier: never log
// or keep them. Result is the live value the model receives: never change it. An integration may hand a framework's
// own content items over as their text (the Agent Framework integration does).
type ToolResultContext struct {
	ToolCallID UUID
	ToolName   string
	Arguments  *Object
	Result     any
}

// ToolResultClassifier names the error class a returned call is recorded as failed with; failed is false when the
// call is recorded as returned. An integration records a class that is not one error-class token (IsSingleToken,
// after trimming surrounding whitespace) as ToolReportedErrorClass.
type ToolResultClassifier func(ToolResultContext) (class string, failed bool)

// ExitCode classifies a result by its exit code: "exit N" for a non-zero code N, not failed for zero or when the
// result names no exit code.
//
// In text it finds exit=N, exit N, exit code N, exit status N, exited with code N, returncode=N, "exitCode": N,
// "exit_code": N, "returnCode": N or "return_code": N; in a JSON object, the first top-level exitCode, exit_code,
// returnCode or return_code member (ignoring case) whose value is a number or a numeric string. A non-zero code that
// no error-class token can carry (more than six digits, or not an integer such as 1.0) is ToolReportedErrorClass.
// It panics on a blank tool name.
func ExitCode(toolNames ...string) ToolResultClassifier { return classifier(toolNames, &exitSpec) }

// HTTPStatus classifies a result by its HTTP status: "HTTP N" for a status N from 400 to 599, not failed otherwise,
// or when the result names none.
//
// In text it finds a leading HTTP N (or HTTP/1.1 N) or status N, else the last "status": N or "statusCode": N pair;
// in a JSON object, the first top-level status, statusCode or status_code member (ignoring case) whose value is a
// number or a numeric string. It panics on a blank tool name.
func HTTPStatus(toolNames ...string) ToolResultClassifier { return classifier(toolNames, &httpSpec) }

type classifierSpec struct {
	properties []string
	fromText   func(text string) (string, bool)
	classify   func(code string) (string, bool)
}

func classifier(toolNames []string, spec *classifierSpec) ToolResultClassifier {
	var tools map[string]bool
	for _, name := range toolNames {
		if isNullOrWhitespace(name) {
			panic("agentexperience: a tool name must not be blank")
		}
		if tools == nil {
			tools = map[string]bool{}
		}
		tools[name] = true
	}
	return func(context ToolResultContext) (string, bool) {
		if tools != nil && !tools[context.ToolName] {
			return "", false
		}
		code, ok := readResult(context.Result, spec)
		if !ok {
			return "", false
		}
		return spec.classify(code)
	}
}

// readResult is the numeric code a result names, as written: from a JSON object's members, else from its text.
func readResult(result any, spec *classifierSpec) (string, bool) {
	switch value := result.(type) {
	case nil:
		return "", false
	case string:
		return fromString(value, spec)
	case *string:
		if value == nil {
			return "", false
		}
		return fromString(*value, spec)
	case json.RawMessage:
		return fromJSON(value, spec)
	case *Object:
		if value == nil {
			return "", false
		}
		for _, member := range value.Members() {
			if code, ok := fromMember(member.Key, member.Value, spec); ok {
				return code, true
			}
		}
		return "", false
	}
	data, err := json.Marshal(result)
	if err != nil {
		return "", false
	}
	return fromJSON(data, spec)
}

// fromJSON reads a JSON value: an object by its members, a string as text, anything else as no code.
func fromJSON(data []byte, spec *classifierSpec) (string, bool) {
	if !json.Valid(data) {
		return "", false
	}
	trimmed := bytes.TrimLeft(data, " \t\r\n")
	switch {
	case len(trimmed) > 0 && trimmed[0] == '{':
		return fromObject(data, spec)
	case len(trimmed) > 0 && trimmed[0] == '"':
		var text string
		if json.Unmarshal(data, &text) != nil {
			return "", false
		}
		return fromString(text, spec)
	}
	return "", false
}

func fromString(text string, spec *classifierSpec) (string, bool) {
	if isNullOrWhitespace(text) {
		return "", false
	}
	trimmed := trimStart(text)
	if strings.HasPrefix(trimmed, "{") || strings.HasPrefix(trimmed, "[") {
		if json.Valid([]byte(text)) {
			if strings.HasPrefix(trimmed, "{") {
				return fromObject([]byte(text), spec)
			}
			return "", false
		}
		// Not JSON after all: read it as text.
	}
	return spec.fromText(text)
}

// fromObject reads a valid JSON object's top-level members in order (a name given twice is read twice, as .NET's
// JsonDocument enumerates it): the first one the spec names whose value is a number or a numeric string.
func fromObject(data []byte, spec *classifierSpec) (string, bool) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if _, err := decoder.Token(); err != nil {
		return "", false
	}
	for decoder.More() {
		token, err := decoder.Token()
		if err != nil {
			return "", false
		}
		name, _ := token.(string)
		var raw json.RawMessage
		if err := decoder.Decode(&raw); err != nil {
			return "", false
		}
		var value any
		switch {
		case len(raw) > 0 && raw[0] == '"':
			var text string
			if json.Unmarshal(raw, &text) != nil {
				continue
			}
			value = text
		case len(raw) > 0 && (raw[0] == '-' || (raw[0] >= '0' && raw[0] <= '9')):
			value = json.Number(raw)
		}
		if code, ok := fromMember(name, value, spec); ok {
			return code, true
		}
	}
	return "", false
}

func fromMember(name string, value any, spec *classifierSpec) (string, bool) {
	named := false
	for _, property := range spec.properties {
		if equalsIgnoreCase(property, name) {
			named = true
			break
		}
	}
	if !named {
		return "", false
	}
	var text string
	switch v := value.(type) {
	case json.Number:
		text = string(v)
	case string:
		text = strings.TrimFunc(v, isWhitespace)
	default:
		return "", false
	}
	return text, isNumeric(text)
}

// isNumeric matches ^-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?$.
func isNumeric(text string) bool {
	i, n := 0, len(text)
	digits := func() int {
		start := i
		for i < n && text[i] >= '0' && text[i] <= '9' {
			i++
		}
		return i - start
	}
	if i < n && text[i] == '-' {
		i++
	}
	if digits() == 0 {
		return false
	}
	if i < n && text[i] == '.' {
		i++
		if digits() == 0 {
			return false
		}
	}
	if i < n && (text[i] == 'e' || text[i] == 'E') {
		i++
		if i < n && (text[i] == '-' || text[i] == '+') {
			i++
		}
		if digits() == 0 {
			return false
		}
	}
	return i == n
}

func exitClass(code string) (string, bool) {
	negative := strings.HasPrefix(code, "-")
	unsigned := strings.TrimPrefix(code, "-")
	if strings.IndexFunc(unsigned, func(r rune) bool { return r < '0' || r > '9' }) >= 0 {
		// Not an integer (1.0, 1e0): zero is success, anything else a failure no token can name.
		if value, err := strconv.ParseFloat(code, 64); err == nil && value == 0 {
			return "", false
		}
		return ToolReportedErrorClass, true
	}
	digits := strings.TrimLeft(unsigned, "0")
	if digits == "" {
		return "", false
	}
	if len(digits) > maxExitCodeDigits {
		return ToolReportedErrorClass, true
	}
	if negative {
		return "exit -" + digits, true
	}
	return "exit " + digits, true
}

func httpClass(code string) (string, bool) {
	if code == "" || strings.IndexFunc(code, func(r rune) bool { return r < '0' || r > '9' }) >= 0 {
		return "", false
	}
	status, err := strconv.ParseInt(code, 10, 32)
	if err != nil || status < 400 || status > 599 {
		return "", false
	}
	return "HTTP " + strconv.FormatInt(status, 10), true
}

// ---- The patterns, run by the error class's matcher -------------------------------------------------------------

type textPattern struct {
	program []instruction
	groups  int
	pool    sync.Pool
}

func newTextPattern(n node, groups int) *textPattern {
	p := &textPattern{program: compile(n), groups: groups}
	p.pool.New = func() any { return &matcher{program: p.program, captures: make([]int, 2*groups)} }
	return p
}

// code is the text of the first of the pattern's groups that matched.
func (m *matcher) code(groups int) (string, bool) {
	for group := 0; group < groups; group++ {
		if m.captures[2*group] >= 0 && m.captures[2*group+1] >= 0 {
			return string(m.text[m.captures[2*group]:m.captures[2*group+1]]), true
		}
	}
	return "", false
}

// last is the code of the last match in the text read: all of it up to 1 MiB, else its first and last 64 KiB.
func (p *textPattern) last(text string) (string, bool) {
	segments := []string{text}
	var beforeTail rune = -1 // the rune before the tail segment, in the whole text
	if UTF16Len(text) > maxWholeTextLength {
		var tail string
		tail, beforeTail = utf16Suffix(text, edgeLength)
		segments = []string{UTF16Prefix(text, edgeLength), tail}
	}
	m := p.pool.Get().(*matcher)
	defer p.pool.Put(m)
	code, found := "", false
	for index, segment := range segments {
		m.reset(segment)
		// The head of a long text ends at a cut, not at the text's end: a match running into the cut may be a code
		// cut short (exit=12 read as exit=1), so it does not count.
		cut := -1
		if len(segments) > 1 && index == 0 {
			cut = len(m.text)
		}
		for start := 0; start < len(m.text); {
			end, ok := m.matchAt(start)
			if !ok {
				start++
				continue
			}
			// The tail of a long text starts at a cut too: a match at its start whose first character continues a
			// word of the whole text (xexit=3) would not have matched there, as \b fails mid-word.
			midWord := index == 1 && start == 0 && beforeTail >= 0 && isBoundaryWordChar(beforeTail) &&
				isBoundaryWordChar(m.text[0])
			if end != cut && !midWord {
				if matched, ok := m.code(p.groups); ok {
					code, found = matched, true
				}
			}
			if end > start {
				start = end
			} else {
				start++
			}
		}
	}
	return code, found
}

// leading is the code of a match at the start of the text.
func (p *textPattern) leading(text string) (string, bool) {
	m := p.pool.Get().(*matcher)
	defer p.pool.Put(m)
	m.reset(text)
	if _, ok := m.matchAt(0); !ok {
		return "", false
	}
	return m.code(p.groups)
}

// utf16Suffix is the last units UTF-16 code units of s, without splitting a surrogate pair, and the rune before it
// (-1 when it is all of s).
func utf16Suffix(s string, units int) (string, rune) {
	runes := []rune(s)
	count := 0
	start := len(runes)
	for start > 0 {
		width := 1
		if utf16.RuneLen(runes[start-1]) == 2 {
			width = 2
		}
		if count+width > units {
			break
		}
		count += width
		start--
	}
	if start == 0 {
		return s, -1
	}
	return string(runes[start:]), runes[start-1]
}

var (
	exitPattern, httpLeadingPattern, httpPairPattern = func() (*textPattern, *textPattern, *textPattern) {
		ws := class(isWhitespace)
		spaces := repeat(ws, 0, -1)
		someSpace := repeat(ws, 1, -1)
		digit := class(func(r rune) bool { return r >= '0' && r <= '9' })
		digits := repeat(digit, 1, -1)
		number := seq(optional(literal("-", false)), digits, optional(seq(literal(".", false), digits)))
		quote := literal(`"`, false)
		separator := class(func(r rune) bool { return r == '=' || r == ':' })
		b := boundary{}
		ic := func(text string) node { return literal(text, true) }
		underscore := optional(literal("_", false))
		returnCode := seq(ic("return"), underscore, ic("code"))

		// \b(?:exit(?:ed)?(?:\s+with)?(?:\s+(?:code|status))?|return_?code)\s*[=:]?\s*(?<code>-?[0-9]+(?:\.[0-9]+)?)\b
		// |"(?:exit_?code|return_?code)"\s*:\s*"?(?<code>-?[0-9]+(?:\.[0-9]+)?)\b
		exit := newTextPattern(alt(
			seq(b, alt(
				seq(ic("exit"), optional(ic("ed")), optional(seq(someSpace, ic("with"))),
					optional(seq(someSpace, alt(ic("code"), ic("status"))))),
				returnCode),
				spaces, optional(separator), spaces, group(0, number), b),
			seq(quote, alt(seq(ic("exit"), underscore, ic("code")), returnCode), quote, spaces, literal(":", false),
				spaces, optional(quote), group(1, number), b),
		), 2)

		// ^\s*(?:HTTP(?:/[0-9](?:\.[0-9])?)?\s+|status(?:\s+code)?\s*[=:]?\s*)(?<code>[0-9]+)\b
		leading := newTextPattern(seq(spaces, alt(
			seq(ic("http"), optional(seq(literal("/", false), digit, optional(seq(literal(".", false), digit)))),
				someSpace),
			seq(ic("status"), optional(seq(someSpace, ic("code"))), spaces, optional(separator), spaces),
		), group(0, digits), b), 1)

		// "status(?:_?code)?"\s*:\s*"?(?<code>-?[0-9]+(?:\.[0-9]+)?)\b
		pair := newTextPattern(seq(quote, ic("status"), optional(seq(underscore, ic("code"))), quote, spaces,
			literal(":", false), spaces, optional(quote), group(0, number), b), 1)
		return exit, leading, pair
	}()

	exitSpec = classifierSpec{
		properties: []string{"exitCode", "exit_code", "returnCode", "return_code"},
		fromText:   func(text string) (string, bool) { return exitPattern.last(text) },
		classify:   exitClass,
	}
	httpSpec = classifierSpec{
		properties: []string{"status", "statusCode", "status_code"},
		fromText: func(text string) (string, bool) {
			if code, ok := httpLeadingPattern.leading(text); ok {
				return code, true
			}
			return httpPairPattern.last(text)
		},
		classify: httpClass,
	}
)
