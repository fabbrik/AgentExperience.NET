package agentexperience

import (
	"regexp"
	"strconv"
	"strings"
	"sync"
	"unicode"
)

// The content guard (contract 1.14): the fixed, deterministic filter screening applies to a model-authored reflection
// after its hygiene, as .NET's ModelAuthoredContentGuard and ModelTextPatterns do. It refuses credential-shaped text,
// a word mixing Latin with Cyrillic or Greek letters, instruction-override phrasing, a data:, javascript:, vbscript: or
// file: link or a UNC path, and a URL, hostname or IP address that is not, as a whole token, in the run content the
// reflector was given. It is a heuristic, not a boundary (see the finalization guide's "Limits of model-authored
// lessons"). It never touches a deterministic reflection.
//
// .NET applies its patterns to UTF-16 code units, so a character outside the Basic Multilingual Plane is neither a
// letter, a digit nor a mark to them; this port classifies such a rune the same way.

// ---- Unicode normalization ---------------------------------------------------------------------------------------

var (
	normalizerMu sync.RWMutex
	normalizer   func(string) string
)

// SetUnicodeNormalizer registers the Unicode NFKC normalization the content guard folds model-authored text with
// (golang.org/x/text/unicode/norm's norm.NFKC.String). The core module requires no other module, so it carries no
// normalization tables: the maf module registers it when it is imported (its init), and a host that screens
// model-authored reflections without it registers it itself. Until one is registered, the guard refuses every
// model-authored field as "text that cannot be normalized" (fail closed). nil unregisters it.
func SetUnicodeNormalizer(nfkc func(string) string) {
	normalizerMu.Lock()
	defer normalizerMu.Unlock()
	normalizer = nfkc
}

func currentNormalizer() func(string) string {
	normalizerMu.RLock()
	defer normalizerMu.RUnlock()
	return normalizer
}

// fold is .NET's ModelAuthoredContentGuard.Fold: invisible characters removed, NFKC, invisible characters removed
// again. ok is false when no normalizer is registered.
func fold(value string) (string, bool) {
	nfkc := currentNormalizer()
	if nfkc == nil {
		return "", false
	}
	return neutralize(nfkc(neutralize(value))), true
}

// ---- Character classes, per UTF-16 code unit as .NET's -----------------------------------------------------------

func bmp(r rune) bool { return r >= 0 && r <= 0xFFFF }

// isLetterU is .NET's char.IsLetter (and \p{L}) for one rune: false outside the Basic Multilingual Plane.
func isLetterU(r rune) bool {
	if !bmp(r) {
		return false
	}
	switch category(r) {
	case "Lu", "Ll", "Lt", "Lm", "Lo":
		return true
	}
	return false
}

// isNumberU is \p{N}.
func isNumberU(r rune) bool {
	if !bmp(r) {
		return false
	}
	switch category(r) {
	case "Nd", "Nl", "No":
		return true
	}
	return false
}

// isMarkU is \p{M}.
func isMarkU(r rune) bool {
	if !bmp(r) {
		return false
	}
	switch category(r) {
	case "Mn", "Mc", "Me":
		return true
	}
	return false
}

// isDecimalU is .NET's regex \d: a Unicode decimal digit.
func isDecimalU(r rune) bool { return bmp(r) && category(r) == "Nd" }

func isLN(r rune) bool { return isLetterU(r) || isNumberU(r) }

// lowerInvariant is .NET's ToLowerInvariant: each character's simple lower-case mapping.
func lowerInvariant(text string) string {
	return strings.Map(unicode.ToLower, text)
}

// ---- The fixed lists -------------------------------------------------------------------------------------------

var genericTLDs = []string{
	"com", "net", "org", "edu", "gov", "mil", "arpa", "info", "biz", "pro", "mobi", "aero", "asia", "coop",
	"jobs", "museum", "tel", "travel", "xxx", "xyz", "online", "site", "website", "top", "club", "tech", "cloud",
	"app", "dev", "icu", "vip", "win", "bid", "loan", "example", "localhost", "onion",
}

var countryTLDs = []string{
	"ad", "ae", "af", "ag", "ai", "al", "ao", "aq", "ar", "at", "au", "aw", "ax", "az", "ba", "bb", "bd", "be",
	"bf", "bg", "bh", "bi", "bj", "bl", "bm", "bn", "bo", "bq", "br", "bs", "bt", "bv", "bw", "by", "bz", "ca",
	"cd", "cf", "cg", "ch", "ci", "ck", "cl", "cm", "cn", "co", "cr", "cu", "cv", "cw", "cx", "cy", "cz", "de",
	"dj", "dk", "dm", "do", "dz", "ec", "ee", "eg", "eh", "er", "es", "et", "fi", "fj", "fk", "fm", "fo", "fr",
	"ga", "gb", "ge", "gf", "gg", "gh", "gi", "gl", "gm", "gn", "gp", "gq", "gr", "gs", "gt", "gu", "gw", "gy",
	"hk", "hm", "hn", "hr", "ht", "hu", "ie", "il", "im", "io", "iq", "ir", "it", "je", "jm", "jo", "jp", "ke",
	"kg", "kh", "ki", "km", "kn", "kp", "kr", "kw", "ky", "kz", "la", "lb", "lc", "li", "lk", "lr", "ls", "lt",
	"lu", "lv", "ly", "ma", "mc", "me", "mf", "mg", "mh", "mm", "mn", "mp", "mq", "mr", "ms", "mt", "mu", "mv",
	"mw", "mx", "my", "mz", "na", "nc", "ne", "nf", "ng", "ni", "nl", "no", "np", "nr", "nu", "nz", "om", "pa",
	"pe", "pf", "pg", "ph", "pk", "pn", "pr", "pt", "pw", "qa", "re", "ro", "ru", "rw", "sa", "sb", "sc", "sd",
	"se", "sg", "si", "sj", "sk", "sl", "sm", "sn", "sr", "ss", "st", "sv", "sx", "sy", "sz", "tc", "td", "tg",
	"th", "tj", "tk", "tl", "tm", "tn", "to", "tr", "tt", "tv", "tw", "tz", "ua", "ug", "um", "us", "uy", "uz",
	"va", "vc", "ve", "vg", "vi", "vn", "vu", "wf", "ws", "ye", "yt", "za", "zm", "zw", "uk", "eu", "su", "ac",
}

var tlds = func() map[string]bool {
	set := map[string]bool{}
	for _, tld := range genericTLDs {
		set[tld] = true
	}
	for _, tld := range countryTLDs {
		if tld != "mm" {
			set[tld] = true
		}
	}
	return set
}()

var collapsedPhrases = []string{
	"ignorepreviousinstructions", "ignoreallpreviousinstructions", "ignorepriorinstructions", "ignoreallpriorinstructions",
	"ignoreyourinstructions", "ignoretheaboveinstructions", "disregardpreviousinstructions",
	"disregardallpreviousinstructions", "disregardpriorinstructions", "disregardyourinstructions",
	"disregardallinstructions", "forgetpreviousinstructions", "forgetallpreviousinstructions",
	"overrideyourinstructions", "overridetheinstructions", "donottelltheuser", "donttelltheuser",
	"revealthesystemprompt", "revealyoursystemprompt", "printthesystemprompt", "printyoursystemprompt",
}

var confusables = map[rune]rune{
	0x0430: 'a', 0x0435: 'e', 0x0451: 'e', 0x0456: 'i', 0x0457: 'i', 0x0458: 'j',
	0x043A: 'k', 0x043E: 'o', 0x0440: 'p', 0x0441: 'c', 0x0455: 's', 0x0443: 'y',
	0x0445: 'x', 0x04BB: 'h', 0x0501: 'd', 0x051B: 'q', 0x051D: 'w', 0x0131: 'i',
	0x0261: 'g', 0x03B1: 'a', 0x03B5: 'e', 0x03B9: 'i', 0x03BA: 'k', 0x03BD: 'v',
	0x03BF: 'o', 0x03C1: 'p', 0x03C4: 't', 0x03C5: 'u', 0x03C7: 'x',
}

const overrideRoles = "assistant|admin|administrator|operator|root|superuser|system|developer|agent|ai|bot|model|hacker|jailbroken|unrestricted|unfiltered|dan"

// phrasePattern is .NET's PhrasePattern on the phrase form, whose characters are letters and single spaces only. Its
// lookarounds, (?<![a-z]) and (?![a-z]), become a non-[a-z] character or an end, which answers the same IsMatch.
var phrasePattern = regexp.MustCompile(`(?:^|[^a-z])(?:` +
	`(?:ignore|disregard|forget|override|bypass) (?:(?:all|any|the|your|my|these|those|every) )?(?:(?:the )?(?:previous|prior|earlier|above|preceding|former|original|system|existing) )?(?:instructions?|directions|directives|prompts?)` +
	`|(?:ignore|disregard|forget) (?:all (?:the )?(?:previous|prior|earlier|above|preceding)|(?:the|everything) above|everything (?:before|previous|prior))` +
	`|you are now (?:a|an|the) (?:[a-z]+ )?(?:` + overrideRoles + `)` +
	`|you are now in (?:[a-z]+ ){0,2}mode` +
	`|you are now (?:unrestricted|jailbroken|unfiltered|root|admin)` +
	`|(?:reveal|print|ignore|output|show|repeat|leak|display|dump|disclose) (?:the|your|my) (?:(?:full|entire|original|hidden|initial|secret) )?system prompts?` +
	`|(?:do not|don t|dont|never) (?:tell|inform|notify|alert) the user` +
	`)(?:[^a-z]|$)`)

// ---- Links -----------------------------------------------------------------------------------------------------

// ModelTextLinks are the links found in one text: whole URLs, whole hostnames and IP addresses, lower-cased, and
// whether it holds a link that is always refused.
type ModelTextLinks struct {
	URLs, Hosts, Addresses map[string]bool
	AlwaysRefused          bool
}

func isRegexSpace(r rune) bool { return isWhitespace(r) }

// deobfuscate is .NET's Deobfuscate on lower-cased text: the ideographic and full-width full stops become '.', and
// so do [.], (.), {.}, [dot], (dot), {dot} with any spaces inside or around them, and a spelled-out dot between two
// words.
func deobfuscate(lower string) string {
	text := []rune(strings.NewReplacer("。", ".", "｡", ".", "．", ".").Replace(lower))
	text = replaceBracketedDots(text)
	return string(replaceSpelledDots(text))
}

func skipSpaces(text []rune, i int) int {
	for i < len(text) && isRegexSpace(text[i]) {
		i++
	}
	return i
}

// replaceBracketedDots is \s*[\[\(\{]\s*(?:\.|dot)\s*[\]\)\}]\s* replaced by ".".
func replaceBracketedDots(text []rune) []rune {
	match := func(i int) int {
		j := skipSpaces(text, i)
		if j >= len(text) || (text[j] != '[' && text[j] != '(' && text[j] != '{') {
			return -1
		}
		j = skipSpaces(text, j+1)
		switch {
		case j < len(text) && text[j] == '.':
			j++
		case j+3 <= len(text) && string(text[j:j+3]) == "dot":
			j += 3
		default:
			return -1
		}
		j = skipSpaces(text, j)
		if j >= len(text) || (text[j] != ']' && text[j] != ')' && text[j] != '}') {
			return -1
		}
		return skipSpaces(text, j+1)
	}
	var out []rune
	for i := 0; i < len(text); {
		if end := match(i); end >= 0 {
			out = append(out, '.')
			i = end
			continue
		}
		out = append(out, text[i])
		i++
	}
	return out
}

// replaceSpelledDots is (?<=[\p{L}\p{N}])\s+dot\s+(?=[\p{L}\p{N}]) replaced by ".", the lookarounds read on the text
// as it was.
func replaceSpelledDots(text []rune) []rune {
	match := func(i int) int {
		if i == 0 || !isLN(text[i-1]) {
			return -1
		}
		j := skipSpaces(text, i)
		if j == i || j+3 > len(text) || string(text[j:j+3]) != "dot" {
			return -1
		}
		k := skipSpaces(text, j+3)
		if k == j+3 || k >= len(text) || !isLN(text[k]) {
			return -1
		}
		return k
	}
	var out []rune
	for i := 0; i < len(text); {
		if end := match(i); end >= 0 {
			out = append(out, '.')
			i = end
			continue
		}
		out = append(out, text[i])
		i++
	}
	return out
}

func isURLChar(r rune) bool {
	return !isRegexSpace(r) && r != '<' && r != '>' && r != '"' && r != '\'' && r != '`'
}

func isSchemeChar(r rune) bool {
	return (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '+' || r == '.' || r == '-'
}

// urlAt is the end of a URL match starting at i (.NET's UrlPattern), or -1.
func urlAt(text []rune, i int) int {
	// A scheme and ://.
	if (i == 0 || !isLN(text[i-1])) && text[i] >= 'a' && text[i] <= 'z' {
		j := i + 1
		for j < len(text) && isSchemeChar(text[j]) {
			j++
		}
		if j+3 <= len(text) && string(text[j:j+3]) == "://" {
			k := j + 3
			for k < len(text) && isURLChar(text[k]) {
				k++
			}
			if k > j+3 {
				return k
			}
		}
	}
	// www.
	if i == 0 || !(isLN(text[i-1]) || isMarkU(text[i-1]) || text[i-1] == '_' || text[i-1] == '-' || text[i-1] == '.') {
		if i+4 <= len(text) && string(text[i:i+4]) == "www." {
			k := i + 4
			for k < len(text) && isURLChar(text[k]) {
				k++
			}
			if k > i+4 {
				return k
			}
		}
	}
	return -1
}

func isHostChar(r rune) bool { return isLN(r) || isMarkU(r) || r == '-' }

// dottedTokenAt is the end of a dotted token starting at i (.NET's DottedTokenPattern), or -1.
func dottedTokenAt(text []rune, i int) int {
	if i > 0 && isHostChar(text[i-1]) {
		return -1
	}
	j := i
	for j < len(text) && isHostChar(text[j]) {
		j++
	}
	if j == i {
		return -1
	}
	parts := 0
	for j+1 < len(text) && text[j] == '.' && isHostChar(text[j+1]) {
		j++
		for j < len(text) && isHostChar(text[j]) {
			j++
		}
		parts++
	}
	if parts == 0 {
		return -1
	}
	return j
}

// asHost is .NET's AsHost: the token trimmed of dots and hyphens, when its last label is a top-level domain.
func asHost(token string) (string, bool) {
	host := strings.Trim(token, ".-")
	dot := strings.LastIndexByte(host, '.')
	if dot <= 0 {
		return "", false
	}
	tld := strings.Trim(host[dot+1:], "-")
	if tld == "" {
		return "", false
	}
	if tlds[tld] || strings.HasPrefix(tld, "xn--") {
		return host, true
	}
	nonASCII, letter := false, false
	for _, r := range tld {
		if r > 0x7F {
			nonASCII = true
		}
		if isLetterU(r) {
			letter = true
		}
	}
	return host, nonASCII && letter
}

func isHex(r rune) bool { return (r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') }

// ipAt is the end of an IP match starting at i (.NET's IpPattern, its three alternatives in order), or -1.
func ipAt(text []rune, i int) int {
	digits := func(j int) int {
		k := j
		for k < len(text) && isDecimalU(text[k]) {
			k++
		}
		return k - j
	}
	// A dotted quad.
	if i == 0 || !(isLN(text[i-1]) || text[i-1] == '.') {
		j, ok := i, true
		for group := 0; group < 3 && ok; group++ {
			n := digits(j)
			if n < 1 || n > 3 || j+n >= len(text) || text[j+n] != '.' {
				ok = false
				break
			}
			j += n + 1
		}
		if ok {
			n := digits(j)
			if n >= 1 {
				// Greedy up to three digits, then (?![\p{L}\p{N}]|\.\d); a shorter group is followed by a digit.
				end := j + min(n, 3)
				if n <= 3 && !(end < len(text) && isLN(text[end])) &&
					!(end+1 < len(text) && text[end] == '.' && isDecimalU(text[end+1])) {
					return end
				}
			}
		}
	}
	if i > 0 && isLN(text[i-1]) {
		return -1
	}
	hex := func(j int) int {
		k := j
		for k < len(text) && isHex(text[k]) {
			k++
		}
		return k - j
	}
	notLNAt := func(j int) bool { return !(j < len(text) && isLN(text[j])) }
	if i+2 <= len(text) && text[i] == '0' && text[i+1] == 'x' {
		// 0x and eight hex digits.
		if n := hex(i + 2); n >= 8 && notLNAt(i+10) {
			return i + 10
		}
		// 0xh(h) and three more .0xh(h).
		j, ok := i+2, true
		for group := 0; group < 4 && ok; group++ {
			if group > 0 {
				if j+3 > len(text) || text[j] != '.' || text[j+1] != '0' || text[j+2] != 'x' {
					ok = false
					break
				}
				j += 3
			}
			n := hex(j)
			if n < 1 || n > 2 {
				ok = false
				break
			}
			j += n
		}
		if ok && notLNAt(j) {
			return j
		}
	}
	return -1
}

// isAddress is .NET's IsAddress: a hex form, or a dotted quad whose four parts are each at most 255.
func isAddress(match string) bool {
	if strings.HasPrefix(match, "0x") {
		return true
	}
	for _, part := range strings.Split(match, ".") {
		if part == "" {
			return false
		}
		for _, r := range part {
			if r < '0' || r > '9' {
				return false
			}
		}
		octet, err := strconv.Atoi(part)
		if err != nil || octet > 255 {
			return false
		}
	}
	return true
}

// scan collects the non-overlapping matches of at, leftmost first, as .NET's Matches does.
func scan(text []rune, at func([]rune, int) int, each func(string)) {
	for i := 0; i < len(text); {
		if end := at(text, i); end > i {
			each(string(text[i:end]))
			i = end
			continue
		}
		i++
	}
}

func isNotSpace(r rune) bool { return !isRegexSpace(r) }

// alwaysRefused is .NET's AlwaysRefusedLinkPattern, IsMatch.
func alwaysRefused(text []rune) bool {
	has := func(i int, word string) bool {
		w := []rune(word)
		return i+len(w) <= len(text) && string(text[i:i+len(w)]) == word
	}
	for i := range text {
		if i == 0 || !isLN(text[i-1]) {
			for _, scheme := range []string{"javascript:", "vbscript:"} {
				if has(i, scheme) && i+len(scheme) < len(text) && isNotSpace(text[i+len(scheme)]) {
					return true
				}
			}
			if has(i, "data:") {
				j := i + 5
				if j < len(text) && text[j] == ',' {
					return true
				}
				k := j
				for k < len(text) && text[k] >= 'a' && text[k] <= 'z' {
					k++
				}
				if k > j && k < len(text) && text[k] == '/' {
					return true
				}
			}
			if has(i, "file:/") {
				return true
			}
		}
		if has(i, `\\`) {
			j := i + 2
			if j < len(text) && isLN(text[j]) {
				k := j + 1
				for k < len(text) && (isLN(text[k]) || text[k] == '.' || text[k] == '-') {
					k++
				}
				if k+1 < len(text) && text[k] == '\\' && (isLN(text[k+1]) || text[k+1] == '$') {
					return true
				}
			}
		}
	}
	return false
}

// ModelTextLinksIn are the links in text, which the caller has folded (.NET's ModelTextPatterns.LinksIn).
func ModelTextLinksIn(text string) ModelTextLinks {
	deobfuscated := []rune(deobfuscate(lowerInvariant(text)))
	links := ModelTextLinks{URLs: map[string]bool{}, Hosts: map[string]bool{}, Addresses: map[string]bool{}}
	scan(deobfuscated, urlAt, func(url string) { links.URLs[strings.TrimRight(url, ".,;:!?)]}>")] = true })
	scan(deobfuscated, dottedTokenAt, func(token string) {
		if host, ok := asHost(token); ok {
			links.Hosts[host] = true
		}
	})
	scan(deobfuscated, ipAt, func(match string) {
		if isAddress(match) {
			links.Addresses[match] = true
		}
	})
	links.AlwaysRefused = alwaysRefused(deobfuscated)
	return links
}

// ModelTextHasLink reports whether text holds any URL, hostname, IP address, colon-only scheme or UNC path (.NET's
// ModelTextPatterns.HasLink, on text as given).
func ModelTextHasLink(text string) bool {
	links := ModelTextLinksIn(text)
	return links.AlwaysRefused || len(links.URLs) > 0 || len(links.Hosts) > 0 || len(links.Addresses) > 0
}

// ---- Credentials, scripts and phrasing -----------------------------------------------------------------------------

func runOf(text string, i int, allowed func(byte) bool) int {
	j := i
	for j < len(text) && allowed(text[j]) {
		j++
	}
	return j - i
}

func isUpperOrDigit(b byte) bool { return (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') }
func isAlnum(b byte) bool {
	return (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
}
func isAlnumUnderscoreDash(b byte) bool { return isAlnum(b) || b == '_' || b == '-' }
func isAlnumUnderscore(b byte) bool     { return isAlnum(b) || b == '_' }

// hasCredential is .NET's CredentialPattern, IsMatch, on folded text. Every piece is ASCII, so it is scanned on bytes.
func hasCredential(text string) bool {
	for i := 0; i < len(text); i++ {
		rest := text[i:]
		switch {
		case strings.HasPrefix(rest, "-----BEGIN "):
			j := i + len("-----BEGIN ")
			for {
				if strings.HasPrefix(text[j:], "PRIVATE KEY-----") {
					return true
				}
				n := runOf(text, j, isUpperOrDigit)
				if n == 0 || j+n >= len(text) || text[j+n] != ' ' {
					break
				}
				j += n + 1
			}
		case strings.HasPrefix(rest, "AKIA") || strings.HasPrefix(rest, "ASIA"):
			if runOf(text, i+4, isUpperOrDigit) >= 16 {
				return true
			}
		case strings.HasPrefix(rest, "sk-"):
			if runOf(text, i+3, isAlnumUnderscoreDash) >= 20 {
				return true
			}
		case len(rest) >= 4 && rest[0] == 'g' && rest[1] == 'h' && strings.IndexByte("opusr", rest[2]) >= 0 && rest[3] == '_':
			if runOf(text, i+4, isAlnum) >= 36 {
				return true
			}
		case strings.HasPrefix(rest, "github_pat_"):
			if runOf(text, i+11, isAlnumUnderscore) >= 22 {
				return true
			}
		case strings.HasPrefix(rest, "glpat-"):
			if runOf(text, i+6, isAlnumUnderscoreDash) >= 20 {
				return true
			}
		case strings.HasPrefix(rest, "AIza"):
			if runOf(text, i+4, isAlnumUnderscoreDash) >= 35 {
				return true
			}
		case len(rest) >= 5 && strings.HasPrefix(rest, "xox") && strings.IndexByte("abposr", rest[3]) >= 0 && rest[4] == '-':
			return true
		case strings.HasPrefix(rest, "eyJ"):
			j := i + 3
			ok := true
			for segment := 0; segment < 3; segment++ {
				if segment > 0 {
					if j >= len(text) || text[j] != '.' {
						ok = false
						break
					}
					j++
				}
				n := runOf(text, j, isAlnumUnderscoreDash)
				if n == 0 {
					ok = false
					break
				}
				j += n
			}
			if ok {
				return true
			}
		}
		if credentialAssignmentAt(text, i) {
			return true
		}
	}
	return false
}

// credentialAssignmentAt is (?i:(?<![a-z])(?:AccountKey|SharedAccessSignature|password|pwd)\s*=[^\s;"',]) at byte i.
func credentialAssignmentAt(text string, i int) bool {
	if i > 0 {
		if b := text[i-1] | 0x20; b >= 'a' && b <= 'z' {
			return false
		}
	}
	for _, name := range []string{"accountkey", "sharedaccesssignature", "password", "pwd"} {
		if len(text)-i < len(name) || !strings.EqualFold(text[i:i+len(name)], name) {
			continue
		}
		runes := []rune(text[i+len(name):])
		j := 0
		for j < len(runes) && isRegexSpace(runes[j]) {
			j++
		}
		if j+1 < len(runes) && runes[j] == '=' {
			next := runes[j+1]
			if !isRegexSpace(next) && next != ';' && next != '"' && next != '\'' && next != ',' {
				return true
			}
		}
	}
	return false
}

func isLatinU(r rune) bool {
	return (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= 0x00C0 && r <= 0x024F) || (r >= 0x0250 && r <= 0x02AF) ||
		(r >= 0x1E00 && r <= 0x1EFF) || (r >= 0x2C60 && r <= 0x2C7F) || (r >= 0xA720 && r <= 0xA7FF) || (r >= 0xAB30 && r <= 0xAB6F)
}

func isCyrillicOrGreekU(r rune) bool {
	return r != 0x03BC && ((r >= 0x0370 && r <= 0x03FF) || (r >= 0x1F00 && r <= 0x1FFF) || (r >= 0x0400 && r <= 0x052F) ||
		(r >= 0x1C80 && r <= 0x1C8F) || (r >= 0x2DE0 && r <= 0x2DFF) || (r >= 0xA640 && r <= 0xA69F))
}

// hasMixedScriptWord is .NET's HasMixedScriptWord.
func hasMixedScriptWord(text string) bool {
	latin, other := false, false
	for _, r := range text {
		if !isLetterU(r) && !(bmp(r) && (category(r) == "Mn" || category(r) == "Mc")) {
			latin, other = false, false
			continue
		}
		if isLatinU(r) {
			latin = true
		} else if isCyrillicOrGreekU(r) {
			other = true
		}
		if latin && other {
			return true
		}
	}
	return false
}

// phraseForm is .NET's PhraseForm: lower case, confusables folded, every run of non-letters one space, trimmed.
func phraseForm(text string) string {
	var out strings.Builder
	space := false
	for _, r := range lowerInvariant(text) {
		if latin, ok := confusables[r]; ok {
			r = latin
		}
		if isLetterU(r) {
			if space && out.Len() > 0 {
				out.WriteByte(' ')
			}
			space = false
			out.WriteRune(r)
		} else {
			space = true
		}
	}
	return out.String()
}

func collapseWhitespaceDotNet(text string) string {
	var out strings.Builder
	space := false
	for _, r := range text {
		if isWhitespace(r) {
			space = true
			continue
		}
		if space && out.Len() > 0 {
			out.WriteByte(' ')
		}
		space = false
		out.WriteRune(r)
	}
	return out.String()
}

// hasInstructionOverride is .NET's HasInstructionOverride.
func hasInstructionOverride(text string) bool {
	phrase := phraseForm(text)
	if phrasePattern.MatchString(phrase) {
		return true
	}
	collapsed := strings.ReplaceAll(phrase, " ", "")
	for _, core := range collapsedPhrases {
		if strings.Contains(collapsed, core) {
			return true
		}
	}
	// (?<![\p{L}\p{N}])new instructions\s*:
	lowered := []rune(collapseWhitespaceDotNet(lowerInvariant(text)))
	target := []rune("new instructions")
	for i := 0; i+len(target) <= len(lowered); i++ {
		if string(lowered[i:i+len(target)]) != "new instructions" || (i > 0 && isLN(lowered[i-1])) {
			continue
		}
		j := skipSpaces(lowered, i+len(target))
		if j < len(lowered) && lowered[j] == ':' {
			return true
		}
	}
	return false
}

// ---- The guard ---------------------------------------------------------------------------------------------------

// runLinks are the links of a run's content: what a model-authored field's links must be among.
type runLinks struct{ urls, hosts, addresses map[string]bool }

// linksOf are the whole URLs, hostnames and IP addresses in content (.NET's LinksOf); none for nil content, or when
// no normalizer is registered (content that cannot be read admits nothing).
func linksOf(content []string) runLinks {
	links := runLinks{map[string]bool{}, map[string]bool{}, map[string]bool{}}
	for _, text := range content {
		if text == "" {
			continue
		}
		folded, ok := fold(text)
		if !ok {
			continue
		}
		found := ModelTextLinksIn(folded)
		for url := range found.URLs {
			links.urls[url] = true
		}
		for host := range found.Hosts {
			links.hosts[host] = true
		}
		for address := range found.Addresses {
			links.addresses[address] = true
		}
	}
	return links
}

func subset(found, run map[string]bool) bool {
	for item := range found {
		if !run[item] {
			return false
		}
	}
	return true
}

// guardCheck is the rule value breaks, as a content-free phrase, or "" (.NET's ModelAuthoredContentGuard.Check).
func guardCheck(value string, run runLinks) string {
	folded, ok := fold(value)
	if !ok {
		return "text that cannot be normalized"
	}
	switch {
	case hasCredential(folded):
		return "credential-shaped text"
	case hasMixedScriptWord(folded):
		return "a word mixing Latin with Cyrillic or Greek letters"
	case hasInstructionOverride(folded):
		return "instruction-override phrasing"
	}
	links := ModelTextLinksIn(folded)
	switch {
	case links.AlwaysRefused:
		return "a data:, javascript:, vbscript: or file: link or a UNC path"
	case !subset(links.URLs, run.urls):
		return "a URL that is not in the captured run"
	case !subset(links.Hosts, run.hosts):
		return "a hostname that is not in the captured run"
	case !subset(links.Addresses, run.addresses):
		return "an IP address that is not in the captured run"
	}
	return ""
}

// guardCheckCombined is instruction-override phrasing in every field joined by spaces (.NET's CheckCombined).
func guardCheckCombined(fields []string) string {
	folded, ok := fold(strings.Join(fields, " "))
	if !ok {
		return "text that cannot be normalized"
	}
	if hasInstructionOverride(folded) {
		return "instruction-override phrasing"
	}
	return ""
}

// DefaultRunContent is the run content a reflector that does not declare its own is taken to have been given (.NET's
// ModelAuthoredContentGuard.DefaultRunContent): the task text (or the task ID when there is none), each tool name,
// each tool call's and each attempt's result and error, and the check IDs, in full.
func DefaultRunContent(request ReflectionRequest) []string {
	var content []string
	add := func(value *string) {
		if value != nil && *value != "" {
			content = append(content, *value)
		}
	}
	run := &request.Run
	if run.TaskDescription != nil {
		add(run.TaskDescription)
	} else {
		add(&run.TaskID)
	}
	for i := range run.Attempts {
		attempt := &run.Attempts[i]
		for j := range attempt.ToolCalls {
			call := &attempt.ToolCalls[j]
			add(&call.ToolName)
			add(call.Result)
			add(call.Error)
		}
		add(attempt.Result)
		add(attempt.Error)
	}
	for i := range request.Evaluation.Outcome.Evidence {
		add(&request.Evaluation.Outcome.Evidence[i].CheckID)
	}
	return content
}
