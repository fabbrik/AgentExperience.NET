package agentexperience

import (
	"math"
	"math/big"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"unicode"
	"unicode/utf16"
	"unicode/utf8"
)

// Text primitives the contract counts in: UTF-16 code units, .NET's character classes, and .NET's number formats.
// A Go string is UTF-8, so every length the contract states is measured here, never with len.

// UTF16Len is the length of s in UTF-16 code units, as .NET's string.Length counts it.
func UTF16Len(s string) int {
	n := 0
	for _, r := range s {
		if r > 0xFFFF {
			n += 2
		} else {
			n++
		}
	}
	return n
}

// UTF16Prefix is the longest prefix of s that fits in units UTF-16 code units without splitting a surrogate pair:
// .NET's text[..cut] after "if (char.IsHighSurrogate(text[cut - 1])) cut--".
func UTF16Prefix(s string, units int) string {
	if units <= 0 {
		return ""
	}
	used := 0
	for i, r := range s {
		size := 1
		if r > 0xFFFF {
			size = 2
		}
		if used+size > units {
			return s[:i]
		}
		used += size
	}
	return s
}

// utf16Compare orders strings as .NET's ordinal comparison does: by UTF-16 code unit.
func utf16Compare(a, b string) int {
	ua, ub := utf16.Encode([]rune(a)), utf16.Encode([]rune(b))
	for i := 0; i < len(ua) && i < len(ub); i++ {
		if ua[i] != ub[i] {
			if ua[i] < ub[i] {
				return -1
			}
			return 1
		}
	}
	switch {
	case len(ua) < len(ub):
		return -1
	case len(ua) > len(ub):
		return 1
	}
	return 0
}

// utf8Bytes is s as .NET's Encoding.UTF8.GetBytes encodes it: anything that is not a valid character is U+FFFD.
func utf8Bytes(s string) []byte {
	return []byte(strings.ToValidUTF8(s, "\uFFFD"))
}

// ---- Unicode general categories --------------------------------------------------------------------------------

var categoryTables = []struct {
	name  string
	table *unicode.RangeTable
}{
	{"Lu", unicode.Lu}, {"Ll", unicode.Ll}, {"Lt", unicode.Lt}, {"Lm", unicode.Lm}, {"Lo", unicode.Lo},
	{"Mn", unicode.Mn}, {"Mc", unicode.Mc}, {"Me", unicode.Me},
	{"Nd", unicode.Nd}, {"Nl", unicode.Nl}, {"No", unicode.No},
	{"Pc", unicode.Pc}, {"Pd", unicode.Pd}, {"Ps", unicode.Ps}, {"Pe", unicode.Pe}, {"Pi", unicode.Pi},
	{"Pf", unicode.Pf}, {"Po", unicode.Po},
	{"Sm", unicode.Sm}, {"Sc", unicode.Sc}, {"Sk", unicode.Sk}, {"So", unicode.So},
	{"Zs", unicode.Zs}, {"Zl", unicode.Zl}, {"Zp", unicode.Zp},
	{"Cc", unicode.Cc}, {"Cf", unicode.Cf}, {"Cs", unicode.Cs}, {"Co", unicode.Co},
}

var (
	bmpCategoriesOnce sync.Once
	bmpCategories     []uint8
)

// category is the Unicode general category of r; an unassigned code point (and an invalid rune) is "Cn".
func category(r rune) string {
	if r >= 0 && r <= 0xFFFF {
		bmpCategoriesOnce.Do(func() {
			bmpCategories = make([]uint8, 0x10000)
			for code := range bmpCategories {
				bmpCategories[code] = categoryIndex(rune(code))
			}
		})
		index := bmpCategories[r]
		if int(index) == len(categoryTables) {
			return "Cn"
		}
		return categoryTables[index].name
	}
	index := categoryIndex(r)
	if int(index) == len(categoryTables) {
		return "Cn"
	}
	return categoryTables[index].name
}

func categoryIndex(r rune) uint8 {
	if r < 0 || r > unicode.MaxRune {
		return uint8(len(categoryTables))
	}
	for i, entry := range categoryTables {
		if unicode.Is(entry.table, r) {
			return uint8(i)
		}
	}
	return uint8(len(categoryTables))
}

// isWhitespace is .NET's char.IsWhiteSpace: the space, line and paragraph separators plus TAB to CR and NEL. The
// information separators U+001C to U+001F are not whitespace. (unicode.IsSpace agrees in the Basic Multilingual
// Plane; regexp's \s, which is ASCII only, does not.)
func isWhitespace(r rune) bool {
	switch r {
	case '\t', '\n', '\v', '\f', '\r', 0x85:
		return true
	}
	switch category(r) {
	case "Zs", "Zl", "Zp":
		return true
	}
	return false
}

// isNullOrWhitespace is .NET's string.IsNullOrWhiteSpace for a present string.
func isNullOrWhitespace(s string) bool {
	for _, r := range s {
		if !isWhitespace(r) {
			return false
		}
	}
	return true
}

// IsNullOrWhiteSpace is .NET's string.IsNullOrWhiteSpace: true when every character is .NET whitespace (U+0085
// is; U+001C to U+001F are not).
func IsNullOrWhiteSpace(s string) bool { return isNullOrWhitespace(s) }

// isBlank is .NET's string.IsNullOrWhiteSpace for an optional string.
func isBlank(s *string) bool {
	return s == nil || isNullOrWhitespace(*s)
}

// trimStart is .NET's string.TrimStart().
func trimStart(s string) string {
	return strings.TrimLeftFunc(s, isWhitespace)
}

// ordinalUpper is one character's simple upper-case mapping, as .NET's ordinal-ignore-case comparison uses it. The
// dotless i (U+0131) and the long s (U+017F) keep their case, so neither equals I or S (Go's unicode.ToUpper maps
// them to ASCII); the dotted capital I (U+0130) and the Kelvin sign (U+212A) are their own upper case.
func ordinalUpper(r rune) rune {
	if r == 0x0131 || r == 0x017F {
		return r
	}
	return unicode.ToUpper(r)
}

// equalsIgnoreCase is .NET's string.Equals(a, b, StringComparison.OrdinalIgnoreCase).
func equalsIgnoreCase(a, b string) bool {
	ra, rb := []rune(a), []rune(b)
	if len(ra) != len(rb) {
		return false
	}
	for i := range ra {
		if ordinalUpper(ra[i]) != ordinalUpper(rb[i]) {
			return false
		}
	}
	return true
}

// prefixIgnoreCase reports whether s starts with prefix, ignoring case ordinally, and how many bytes of s it spans.
func prefixIgnoreCase(s, prefix string) (int, bool) {
	at := 0
	for _, p := range prefix {
		if at >= len(s) {
			return 0, false
		}
		r, size := utf8.DecodeRuneInString(s[at:])
		if ordinalUpper(r) != ordinalUpper(p) {
			return 0, false
		}
		at += size
	}
	return at, true
}

// containsIgnoreCase is .NET's Contains(value, StringComparison.OrdinalIgnoreCase).
func containsIgnoreCase(s, value string) bool {
	return strings.Contains(strings.Map(ordinalUpper, s), strings.Map(ordinalUpper, value))
}

// ---- .NET number formats ---------------------------------------------------------------------------------------

// formatFixed is .NET's custom format "0.00" (decimals zeros): the double is first taken to 15 significant digits
// (.NET's precision for a custom format), then rounded half away from zero (0.8374999999999999 is "0.838").
func formatFixed(value float64, decimals int) string {
	fifteen := strconv.FormatFloat(math.Abs(value), 'e', 14, 64)
	exact, _ := new(big.Rat).SetString(fifteen)
	scale := new(big.Int).Exp(big.NewInt(10), big.NewInt(int64(decimals)), nil)
	exact.Mul(exact, new(big.Rat).SetInt(scale))
	exact.Add(exact, big.NewRat(1, 2))
	rounded := new(big.Int).Quo(exact.Num(), exact.Denom())
	digits := rounded.String()
	if len(digits) <= decimals {
		digits = strings.Repeat("0", decimals-len(digits)+1) + digits
	}
	text := digits
	if decimals > 0 {
		text = digits[:len(digits)-decimals] + "." + digits[len(digits)-decimals:]
	}
	if math.Signbit(value) {
		text = "-" + text
	}
	return text
}

// formatRoundTrip is .NET's double.ToString("R", CultureInfo.InvariantCulture): the shortest text that
// round-trips, with no trailing ".0", and scientific notation ("1E-05", "1E+15") below 1e-4 and from 1e15.
func formatRoundTrip(value float64) string {
	return formatShortest(value, 64)
}

func formatShortest(value float64, bits int) string {
	return formatShortestFrom(value, bits, 15)
}

// formatShortestFrom is formatShortest with scientific notation from 10^largest (formatShortest uses 15; .NET's
// double.ToString("R") itself switches at 17: 1E+17, but 10000000000000000).
func formatShortestFrom(value float64, bits, largest int) string {
	switch {
	case math.IsNaN(value):
		return "NaN"
	case math.IsInf(value, 1):
		return "Infinity"
	case math.IsInf(value, -1):
		return "-Infinity"
	case value == 0:
		if math.Signbit(value) {
			return "-0"
		}
		return "0"
	}
	prefix := ""
	if value < 0 {
		prefix = "-"
		value = -value
	}
	text := strconv.FormatFloat(value, 'e', -1, bits)
	mantissa, exponentText, _ := strings.Cut(text, "e")
	exponent, _ := strconv.Atoi(exponentText)
	digits := strings.TrimRight(strings.Replace(mantissa, ".", "", 1), "0")
	if digits == "" {
		digits = "0"
	}
	if exponent < -4 || exponent >= largest {
		body := digits[:1]
		if len(digits) > 1 {
			body += "." + digits[1:]
		}
		sign := "+"
		if exponent < 0 {
			sign = "-"
			exponent = -exponent
		}
		return prefix + body + "E" + sign + leftPad(strconv.Itoa(exponent), 2)
	}
	point := exponent + 1
	switch {
	case point <= 0:
		return prefix + "0." + strings.Repeat("0", -point) + digits
	case point >= len(digits):
		return prefix + digits + strings.Repeat("0", point-len(digits))
	}
	return prefix + digits[:point] + "." + digits[point:]
}

func leftPad(s string, width int) string {
	if len(s) >= width {
		return s
	}
	return strings.Repeat("0", width-len(s)) + s
}

// ---- Invisible characters ----------------------------------------------------------------------------------------

// isBlankIgnorable reports characters that render blank but are not whitespace (variation selectors, Hangul
// fillers, the braille blank).
func isBlankIgnorable(r rune) bool {
	return (r >= 0xFE00 && r <= 0xFE0F) || (r >= 0xE0100 && r <= 0xE01EF) ||
		r == 0x034F || r == 0x115F || r == 0x1160 || r == 0x3164 || r == 0xFFA0 || r == 0x2800
}

const maxConsecutiveCombiningMarks = 4

var quoteLookAlikes = map[rune]bool{
	0x22: true, 0x201C: true, 0x201D: true, 0x201E: true, 0x201F: true, 0x2033: true, 0x2036: true, 0x02BA: true,
	0x02DD: true, 0x02EE: true, 0x3003: true, 0x301D: true, 0x301E: true, 0x301F: true, 0xFF02: true,
}

// visible makes invisible characters harmless, as the .NET writer's Visible and screening's Neutralize do. A byte of
// invalid UTF-8 is a lone surrogate's: a space, or with remove removed. Line and
// paragraph separators become a space. Control, format, private-use, unassigned and blank-but-not-space characters
// become a space, or with remove are removed unless they are whitespace. A run of combining marks keeps its first
// four. With quoteLookAlikes, a double quote or a look-alike becomes "'".
func visible(value string, remove, quoteLookAlikesToo bool) string {
	var out strings.Builder
	marks := 0
	for i := 0; i < len(value); {
		r, size := utf8.DecodeRuneInString(value[i:])
		i += size
		isMark, removed := false, false
		keep, kept := r, true
		cat := category(r)
		if r == utf8.RuneError && size <= 1 {
			// A byte of invalid UTF-8 is what a lone surrogate becomes in Go: a surrogate, not a character.
			cat = "Cs"
		}
		switch {
		case cat == "Zl" || cat == "Zp":
			keep = ' '
		case cat == "Cc" || cat == "Cf" || cat == "Co" || cat == "Cn" || cat == "Cs" || isBlankIgnorable(r):
			if remove && !isWhitespace(r) {
				kept, removed = false, true
			} else {
				keep = ' '
			}
		case cat == "Mn" || cat == "Mc" || cat == "Me":
			isMark = marks < maxConsecutiveCombiningMarks
			if !isMark {
				kept, removed = false, true
			}
		case quoteLookAlikesToo && quoteLookAlikes[r]:
			keep = '\''
		}
		if !removed {
			if isMark {
				marks++
			} else {
				marks = 0
			}
		}
		if kept {
			out.WriteRune(keep)
		}
	}
	return out.String()
}

// ---- Regular-expression pieces -----------------------------------------------------------------------------------

// spaceClass is a regular-expression class for .NET's \s: the space separators plus TAB to CR and NEL (Go's \s is
// ASCII only, and leaves out \v).
const spaceClass = `[\t\n\v\f\r\x{85} \x{a0}\x{1680}\x{2000}-\x{200a}\x{2028}\x{2029}\x{202f}\x{205f}\x{3000}]`

// ignoreCasePiece is one character as a case-insensitive regular-expression piece, the way .NET's IgnoreCase |
// CultureInvariant matches it: an ASCII letter matches its two cases (and k the Kelvin sign), never the dotless i,
// the dotted capital I or the long s (Go's (?i) folds the long s to s and the Kelvin sign to k); anything else
// matches itself.
func ignoreCasePiece(r rune) string {
	lower := unicode.ToLower(r)
	if r < utf8.RuneSelf && lower >= 'a' && lower <= 'z' {
		cases := string(lower) + string(unicode.ToUpper(lower))
		if lower == 'k' {
			cases += `\x{212a}`
		}
		return "[" + cases + "]"
	}
	return regexp.QuoteMeta(string(r))
}
