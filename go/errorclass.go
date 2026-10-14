package agentexperience

import (
	"strings"
	"sync"
)

// The error class: a captured error reduced to at most three distinct tokens it recognises, in the order they first
// appear, joined with ", "; "unclassified error" when there is none.
//
// .NET's rule is a regular expression (src/Shared/ErrorClass.Shared.cs) whose \b, \s and case-insensitive parts are
// .NET's: \b counts letters, non-spacing marks, decimal digits, connector punctuation and the zero-width joiner and
// non-joiner as word characters; \s is .NET's whitespace; and ignoring case folds no non-ASCII letter to an ASCII
// one. Go's regexp has no Unicode \b and folds the long s and the Kelvin sign, so the expression is matched here by
// a small backtracking matcher, with .NET's leftmost-first semantics, over the same pattern.

// Unclassified is the class of an error in which no token is recognised.
const Unclassified = "unclassified error"

const (
	maxErrorTokens      = 3
	maxErrorTokenLength = 40
	maxScannedLength    = 8192
	timeoutToken        = "Timeout"
)

var errnoNames = func() map[string]bool {
	names := map[string]bool{}
	for _, name := range strings.Fields(`
		E2BIG EACCES EADDRINUSE EADDRNOTAVAIL EAFNOSUPPORT EAGAIN EALREADY EBADF EBADMSG EBUSY ECANCELED ECHILD
		ECONNABORTED ECONNREFUSED ECONNRESET EDEADLK EDESTADDRREQ EDOM EDQUOT EEXIST EFAULT EFBIG EHOSTUNREACH EIDRM
		EILSEQ EINPROGRESS EINTR EINVAL EIO EISCONN EISDIR ELOOP EMFILE EMLINK EMSGSIZE EMULTIHOP ENAMETOOLONG ENETDOWN
		ENETRESET ENETUNREACH ENFILE ENOBUFS ENODATA ENODEV ENOENT ENOEXEC ENOLCK ENOLINK ENOMEM ENOMSG ENOPROTOOPT
		ENOSPC ENOSR ENOSTR ENOSYS ENOTCONN ENOTDIR ENOTEMPTY ENOTRECOVERABLE ENOTSOCK ENOTSUP ENOTTY ENXIO EOPNOTSUPP
		EOVERFLOW EOWNERDEAD EPERM EPIPE EPROTO EPROTONOSUPPORT EPROTOTYPE ERANGE EROFS ESPIPE ESRCH ESTALE ETIME
		ETIMEDOUT ETXTBSY EWOULDBLOCK EXDEV`) {
		names[name] = true
	}
	return names
}()

// The capture groups of the token pattern.
const (
	groupType = iota
	groupExit
	groupExitCode
	groupHTTP
	groupHTTPCode
	groupDotnet
	groupDotnetCode
	groupStatus
	groupStatusCode
	groupErrno
	groupTimeout
	groupCount
)

var tokenProgram = func() []instruction {
	ws := class(isWhitespace)
	digit := class(func(r rune) bool { return r >= '0' && r <= '9' })
	code := seq(class(func(r rune) bool { return r >= '1' && r <= '5' }), digit, digit)
	separator := class(func(r rune) bool { return r == ':' || r == '=' })
	alnum := class(func(r rune) bool {
		return (r >= 'A' && r <= 'Z') || (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9')
	})
	upperOrDigit := class(func(r rune) bool { return (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') })
	b := boundary{}
	return compile(alt(
		group(groupType, seq(b, class(func(r rune) bool { return r >= 'A' && r <= 'Z' }), repeat(alnum, 0, 38),
			alt(literal("Exception", false), literal("Error", false)), b)),
		group(groupExit, seq(b, literal("exit", true), optional(literal("ed", true)),
			optional(seq(repeat(ws, 1, -1), literal("with", true))),
			optional(seq(repeat(ws, 1, -1), literal("code", true))),
			repeat(ws, 0, -1), optional(separator), repeat(ws, 0, -1),
			group(groupExitCode, seq(optional(literal("-", false)), repeat(digit, 1, 6))), b)),
		group(groupHTTP, seq(b, literal("http", true),
			optional(seq(literal("/", false), digit, optional(seq(literal(".", false), digit)))),
			repeat(ws, 0, -1), group(groupHTTPCode, code), b)),
		group(groupDotnet, seq(literal("status", true), repeat(ws, 1, -1), literal("code", true), repeat(ws, 1, -1),
			literal("does", true), repeat(ws, 1, -1), literal("not", true), repeat(ws, 1, -1),
			literal("indicate", true), repeat(ws, 1, -1), literal("success", true), repeat(ws, 0, -1),
			literal(":", false), repeat(ws, 0, -1), group(groupDotnetCode, code), b)),
		group(groupStatus, seq(b, literal("status", true), optional(seq(repeat(ws, 1, -1), literal("code", true))),
			repeat(ws, 0, -1), optional(separator), repeat(ws, 0, -1), group(groupStatusCode, code), b)),
		group(groupErrno, seq(b, literal("E", false), repeat(upperOrDigit, 2, 15), b)),
		group(groupTimeout, seq(b, alt(
			seq(literal("time", true), optional(class(func(r rune) bool { return r == ' ' || r == '-' })),
				literal("out", true)),
			seq(literal("timed", true), repeat(ws, 1, -1), literal("out", true))), b)),
	))
}()

// ErrorClass is the class of an error; Unclassified for a blank one or one with no recognised token. Only the first
// 8,192 UTF-16 code units are scanned.
func ErrorClass(err *string) string {
	if err == nil || isNullOrWhitespace(*err) {
		return Unclassified
	}
	m := matchers.Get().(*matcher)
	defer matchers.Put(m)
	m.reset(UTF16Prefix(*err, maxScannedLength))
	text := m.text
	var found []string
	for start := 0; start < len(text); {
		end, ok := m.matchAt(start)
		if !ok {
			start++
			continue
		}
		if token, ok := m.token(); ok && len(token) <= maxErrorTokenLength && !contains(found, token) {
			found = append(found, token)
		}
		if end > start {
			start = end
		} else {
			start++
		}
	}

	// A timeout exception already says it timed out: "TimeoutException, Timeout" would say it twice.
	for _, token := range found {
		if token != timeoutToken && containsIgnoreCase(token, timeoutToken) &&
			(strings.HasSuffix(token, "Exception") || strings.HasSuffix(token, "Error")) {
			found = remove(found, timeoutToken)
			break
		}
	}
	if len(found) == 0 {
		return Unclassified
	}
	if len(found) > maxErrorTokens {
		found = found[:maxErrorTokens]
	}
	return strings.Join(found, ", ")
}

// ErrorClassOf is ErrorClass of a present error.
func ErrorClassOf(err string) string { return ErrorClass(&err) }

// FirstToken is the first token of a class: what a call's "[failed: ...]" marker shows.
func FirstToken(class string) string {
	first, _, _ := strings.Cut(class, ",")
	return first
}

// IsSingleToken reports whether value is exactly one token: the first token ErrorClass finds in it is all of it, in
// its normalized form ("exit 3", not "Exit 3" or "exit code 3"), at most 40 characters. Such a value is its own
// class's first token, so a "Tried:" line's call marker shows it unchanged.
func IsSingleToken(value string) bool {
	if value == "" || UTF16Len(value) > maxErrorTokenLength {
		return false
	}
	class := ErrorClassOf(value)
	return class != Unclassified && FirstToken(class) == value
}

func (m *matcher) token() (string, bool) {
	text := func(group int) string { return string(m.text[m.captures[2*group]:m.captures[2*group+1]]) }
	matched := func(group int) bool { return m.captures[2*group] >= 0 && m.captures[2*group+1] >= 0 }
	switch {
	case matched(groupType):
		return text(groupType), true
	case matched(groupExit):
		return "exit " + text(groupExitCode), true
	case matched(groupHTTP):
		return "HTTP " + text(groupHTTPCode), true
	case matched(groupDotnet):
		return "HTTP " + text(groupDotnetCode), true
	case matched(groupStatus):
		return "status " + text(groupStatusCode), true
	case matched(groupErrno):
		name := text(groupErrno)
		return name, errnoNames[name]
	case matched(groupTimeout):
		return timeoutToken, true
	}
	return "", false
}

func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

func remove(list []string, value string) []string {
	kept := list[:0]
	for _, item := range list {
		if item != value {
			kept = append(kept, item)
		}
	}
	return kept
}

// ---- A small backtracking matcher --------------------------------------------------------------------------------

// isBoundaryWordChar is what .NET's \b counts as a word character: letters, non-spacing marks, decimal digits,
// connector punctuation, and the zero-width joiner and non-joiner. A character outside the Basic Multilingual Plane
// is two surrogates to .NET, neither of them a word character.
func isBoundaryWordChar(r rune) bool {
	if r > 0xFFFF {
		return false
	}
	if r == 0x200C || r == 0x200D {
		return true
	}
	switch category(r) {
	case "Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Nd", "Pc":
		return true
	}
	return false
}

type node interface{}

type (
	classNode    struct{ match func(rune) bool }
	sequenceNode []node
	choiceNode   []node
	repeatNode   struct {
		node     node
		min, max int // max < 0: unbounded
	}
	boundary  struct{}
	groupNode struct {
		index int
		node  node
	}
)

func class(match func(rune) bool) node { return classNode{match} }
func seq(nodes ...node) node           { return sequenceNode(nodes) }
func alt(nodes ...node) node           { return choiceNode(nodes) }
func optional(n node) node             { return repeatNode{n, 0, 1} }
func repeat(n node, min, max int) node { return repeatNode{n, min, max} }
func group(index int, n node) node     { return groupNode{index, n} }

// literal is text, each ASCII letter matching either case when ignoreCase (and no other letter).
func literal(text string, ignoreCase bool) node {
	nodes := make([]node, 0, len(text))
	for _, r := range text {
		r := r
		lower, upper := r, r
		if ignoreCase && r >= 'a' && r <= 'z' {
			upper = r - 'a' + 'A'
		} else if ignoreCase && r >= 'A' && r <= 'Z' {
			lower = r - 'A' + 'a'
		}
		nodes = append(nodes, class(func(c rune) bool { return c == lower || c == upper }))
	}
	return seq(nodes...)
}

type opcode uint8

const (
	opRune opcode = iota
	opSplit
	opJump
	opBoundary
	opSave
	opMatch
)

type instruction struct {
	op    opcode
	match func(rune) bool
	x, y  int
}

type compiler struct{ program []instruction }

func compile(n node) []instruction {
	c := &compiler{}
	c.emit(n)
	c.program = append(c.program, instruction{op: opMatch})
	return c.program
}

func (c *compiler) add(i instruction) int {
	c.program = append(c.program, i)
	return len(c.program) - 1
}

func (c *compiler) emit(n node) {
	switch v := n.(type) {
	case classNode:
		c.add(instruction{op: opRune, match: v.match})
	case sequenceNode:
		for _, item := range v {
			c.emit(item)
		}
	case choiceNode:
		var jumps []int
		for i, item := range v {
			if i == len(v)-1 {
				c.emit(item)
				break
			}
			split := c.add(instruction{op: opSplit})
			c.emit(item)
			jumps = append(jumps, c.add(instruction{op: opJump}))
			c.program[split].x, c.program[split].y = split+1, len(c.program)
		}
		for _, jump := range jumps {
			c.program[jump].x = len(c.program)
		}
	case repeatNode:
		for i := 0; i < v.min; i++ {
			c.emit(v.node)
		}
		if v.max < 0 {
			loop := c.add(instruction{op: opSplit})
			c.emit(v.node)
			c.add(instruction{op: opJump, x: loop})
			c.program[loop].x, c.program[loop].y = loop+1, len(c.program)
			return
		}
		var splits []int
		for i := v.min; i < v.max; i++ {
			split := c.add(instruction{op: opSplit})
			splits = append(splits, split)
			c.program[split].x = split + 1
			c.emit(v.node)
		}
		for _, split := range splits {
			c.program[split].y = len(c.program)
		}
	case boundary:
		c.add(instruction{op: opBoundary})
	case groupNode:
		c.add(instruction{op: opSave, x: 2 * v.index})
		c.emit(v.node)
		c.add(instruction{op: opSave, x: 2*v.index + 1})
	default:
		panic("agentexperience: unknown pattern node")
	}
}

// matcher runs a program over one text with leftmost-first (backtracking) priority. A state (instruction,
// position) that failed once fails again, so it is never explored twice: matching is linear in the text for a fixed
// program.
type matcher struct {
	program  []instruction
	text     []rune
	captures []int
	failed   []uint64
	touched  []int
}

// matchers keep their buffers between calls: a classification allocates no memo of its own once one of the size
// it needs has been used.
var matchers = sync.Pool{New: func() any {
	return &matcher{program: tokenProgram, captures: make([]int, 2*groupCount)}
}}

// reset readies the matcher for text, reusing its buffers.
func (m *matcher) reset(text string) {
	m.text = m.text[:0]
	for _, r := range text {
		m.text = append(m.text, r)
	}
	words := (len(m.program)*(len(m.text)+1) + 63) / 64
	if cap(m.failed) < words {
		m.failed = make([]uint64, words)
	} else {
		m.failed = m.failed[:words]
		clear(m.failed)
	}
}

// matchAt matches the program at start; on success it returns the end, with the captures set.
func (m *matcher) matchAt(start int) (int, bool) {
	for i := range m.captures {
		m.captures[i] = -1
	}
	m.touched = m.touched[:0]
	end, ok := m.run(0, start)
	if ok {
		// States visited on the way to a match are not known failures.
		for _, state := range m.touched {
			m.failed[state/64] &^= 1 << (state % 64)
		}
	}
	return end, ok
}

func (m *matcher) isBoundary(pos int) bool {
	before := pos > 0 && isBoundaryWordChar(m.text[pos-1])
	after := pos < len(m.text) && isBoundaryWordChar(m.text[pos])
	return before != after
}

func (m *matcher) run(pc, pos int) (int, bool) {
	for {
		state := pc*(len(m.text)+1) + pos
		if m.failed[state/64]&(1<<(state%64)) != 0 {
			return 0, false
		}
		m.failed[state/64] |= 1 << (state % 64)
		m.touched = append(m.touched, state)
		i := &m.program[pc]
		switch i.op {
		case opRune:
			if pos < len(m.text) && i.match(m.text[pos]) {
				pc, pos = pc+1, pos+1
				continue
			}
			return 0, false
		case opSplit:
			if end, ok := m.run(i.x, pos); ok {
				return end, true
			}
			pc = i.y
		case opJump:
			pc = i.x
		case opBoundary:
			if !m.isBoundary(pos) {
				return 0, false
			}
			pc++
		case opSave:
			previous := m.captures[i.x]
			m.captures[i.x] = pos
			if end, ok := m.run(pc+1, pos); ok {
				return end, true
			}
			m.captures[i.x] = previous
			return 0, false
		case opMatch:
			return pos, true
		}
	}
}
