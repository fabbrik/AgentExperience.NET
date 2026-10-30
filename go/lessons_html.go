package agentexperience

import (
	"bytes"
	"net/http"
	"strconv"
	"strings"
	"unicode/utf8"
)

// The lessons page (contract 1.14): a lessons page as one self-contained HTML document, byte for byte what .NET's
// ExperienceLessonsHtml.Render writes: inline CSS, no script, no external request, light and dark mode. Every value
// drawn from a record or the scope is HTML-encoded, and the document's content security policy allows no script, no
// external resource and no form submission.

// The lessons page's fixed texts.
const (
	// LessonsDevelopmentBanner is the banner every page opens with.
	LessonsDevelopmentBanner = "Development view: these are the lessons stored in this scope. It discloses them, so serve it only behind your own authorization."
	// LessonsNone is what a page with no lessons says.
	LessonsNone = "No lessons yet."
	// LessonsNeverInjected is a lesson's "last injected" value when it was never injected in this scope.
	LessonsNeverInjected = "never"
	// LessonsInjectionsNotTracked is a lesson's "last injected" value when the store does not track injections.
	LessonsInjectionsNotTracked = "not tracked"
)

const lessonsStyle = `:root{color-scheme:light dark;--bg:#ffffff;--fg:#1b1f24;--muted:#59636e;--line:#d1d9e0;--panel:#f6f8fa;--banner:#fff8c5;--banner-line:#d4a72c;--ok:#1a7f37;--warn:#9a6700;--bad:#cf222e}
@media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--line:#3d444d;--panel:#151b23;--banner:#2e2a14;--banner-line:#9e7a1d;--ok:#3fb950;--warn:#d29922;--bad:#f85149}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:960px;margin:0 auto;padding:16px}
h1{font-size:1.4rem;margin:16px 0 4px}
h2{font-size:1.05rem;margin:0;word-break:break-all}
.banner{background:var(--banner);border:1px solid var(--banner-line);border-radius:6px;padding:8px 12px;margin-top:8px}
.muted{color:var(--muted)}
.lesson{border:1px solid var(--line);border-radius:8px;padding:12px 16px;margin:16px 0}
.meta{display:flex;flex-wrap:wrap;gap:4px 16px;margin:6px 0;font-size:.9rem}
.badge{display:inline-block;border:1px solid currentColor;border-radius:999px;padding:0 8px;font-size:.8rem}
.eligible{color:var(--ok)}.ineligible,.warn{color:var(--warn)}.failed{color:var(--bad)}
table{border-collapse:collapse;font-size:.9rem;margin:6px 0}
th,td{border:1px solid var(--line);padding:2px 8px;text-align:left;word-break:break-all}
pre{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:8px;overflow-x:auto;white-space:pre-wrap;word-break:break-word;font-size:.82rem}
code{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}`

// HTMLEncode is .NET's WebUtility.HtmlEncode: <, >, &, " and ' as &lt;, &gt;, &amp;, &quot; and &#39;; every
// character from U+00A0 to U+00FF as a decimal reference; a character outside the Basic Multilingual Plane as one
// decimal reference to its code point; a byte that is not UTF-8 (a lone surrogate, in .NET) as U+FFFD; anything else
// as it is.
func HTMLEncode(value string) string {
	var out strings.Builder
	for i := 0; i < len(value); {
		r, size := utf8.DecodeRuneInString(value[i:])
		i += size
		switch {
		case r == utf8.RuneError && size <= 1:
			out.WriteRune(utf8.RuneError)
		case r == '<':
			out.WriteString("&lt;")
		case r == '>':
			out.WriteString("&gt;")
		case r == '"':
			out.WriteString("&quot;")
		case r == '&':
			out.WriteString("&amp;")
		case r == '\'':
			out.WriteString("&#39;")
		case (r >= 160 && r < 256) || r > 0xFFFF:
			out.WriteString("&#" + strconv.Itoa(int(r)) + ";")
		default:
			out.WriteRune(r)
		}
	}
	return out.String()
}

// htmlTime is .NET's ToString("yyyy-MM-dd HH:mm:ss 'UTC'"): the fraction of a second cut, never rounded.
func htmlTime(at Instant) string {
	text := at.String()
	return text[:10] + " " + text[11:19] + " UTC"
}

func lessonsScopeText(scope Scope) string {
	text := "tenant " + scope.TenantID + " / application " + scope.ApplicationID + " / project " + scope.ProjectID
	if scope.TeamID != nil {
		text += " / team " + *scope.TeamID
	}
	if scope.AgentID != nil {
		text += " / agent " + *scope.AgentID
	}
	if scope.UserID != nil {
		text += " / user " + *scope.UserID
	}
	return text
}

// RenderLessonsHTML is page as one HTML document, starting with <!doctype html>, byte for byte what .NET's
// ExperienceLessonsHtml.Render writes for the same page.
func RenderLessonsHTML(page LessonsPage) string {
	var html strings.Builder
	html.WriteString("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
	html.WriteString("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
	html.WriteString("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'\">\n")
	html.WriteString("<meta name=\"referrer\" content=\"no-referrer\">\n")
	html.WriteString("<title>Agent experience lessons</title>\n<style>\n" + lessonsStyle + "</style>\n</head>\n<body>\n<main>\n")
	html.WriteString("<p class=\"banner\" role=\"note\"><strong>" + HTMLEncode(LessonsDevelopmentBanner) + "</strong></p>\n")
	html.WriteString("<h1>What it learned</h1>\n")
	html.WriteString("<p class=\"muted\">Scope: <code>" + HTMLEncode(lessonsScopeText(page.Scope)) + "</code> · read " +
		HTMLEncode(htmlTime(page.GeneratedAt)) + "</p>\n")
	switch {
	case page.Outcome != LessonsFound:
		html.WriteString("<p class=\"failed\">The store answered <strong>" + HTMLEncode(string(page.Outcome)) +
			"</strong>, so nothing is listed.</p>\n")
	case len(page.Lessons) == 0:
		html.WriteString("<p>" + HTMLEncode(LessonsNone) + " A lesson is stored when a run's own checks verify it.</p>\n")
	default:
		noun := " lessons"
		if len(page.Lessons) == 1 {
			noun = " lesson"
		}
		html.WriteString("<p class=\"muted\">Showing " + strconv.Itoa(len(page.Lessons)) + noun + ", newest first.")
		if page.HasMore {
			html.WriteString(" More exist; raise the limit or filter by status to see them.")
		}
		html.WriteString("</p>\n")
		for i := range page.Lessons {
			lessonHTML(&html, &page.Lessons[i], page.InjectionsTracked)
		}
	}
	if page.OutOfScopeDropped > 0 {
		html.WriteString("<p class=\"warn\">The store answered " + strconv.Itoa(page.OutOfScopeDropped) +
			" record(s) from outside this scope; they were left out.</p>\n")
	}
	html.WriteString("</main>\n</body>\n</html>\n")
	return html.String()
}

func lessonHTML(html *strings.Builder, lesson *Lesson, injectionsTracked bool) {
	html.WriteString("<section class=\"lesson\">\n<h2>" + HTMLEncode(lesson.TaskID) + "</h2>\n")
	html.WriteString("<div class=\"meta\"><span class=\"badge\">" + HTMLEncode(string(lesson.Status)) + "</span>")
	if lesson.IsEligible {
		html.WriteString("<span class=\"badge eligible\">eligible for retrieval</span>")
	} else {
		reason := lesson.IneligibleReason
		if reason == "" {
			reason = "not eligible"
		}
		html.WriteString("<span class=\"badge ineligible\">not eligible for retrieval: " + HTMLEncode(reason) + "</span>")
	}
	injected := LessonsInjectionsNotTracked
	switch {
	case lesson.LastInjectedAt != nil:
		unit := " times)"
		if lesson.InjectionCount == 1 {
			unit = " time)"
		}
		injected = htmlTime(*lesson.LastInjectedAt) + " (" + strconv.FormatInt(lesson.InjectionCount, 10) + unit
	case injectionsTracked:
		injected = LessonsNeverInjected
	}
	html.WriteString("</div>\n<div class=\"meta muted\">" +
		"<span>confidence " + HTMLEncode(shortNumber(lesson.Confidence)) + "</span>" +
		"<span>stored " + HTMLEncode(htmlTime(lesson.CreatedAt)) + "</span>" +
		"<span>last activity " + HTMLEncode(htmlTime(lesson.UpdatedAt)) + "</span>" +
		"<span>last injected " + HTMLEncode(injected) + "</span>" +
		"<span>experience <code>" + HTMLEncode(lesson.ExperienceID.String()) + "</code></span>" +
		"</div>\n")
	if len(lesson.Checks) > 0 {
		html.WriteString("<table>\n<tr><th>Check</th><th>Evidence kind</th><th>Result</th></tr>\n")
		for _, check := range lesson.Checks {
			class := " class=\"warn\""
			switch check.Result {
			case CheckPass:
				class = " class=\"eligible\""
			case CheckFail:
				class = " class=\"failed\""
			}
			html.WriteString("<tr><td><code>" + HTMLEncode(check.CheckID) + "</code></td><td>" +
				HTMLEncode(check.EvidenceKind) + "</td><td" + class + ">" + HTMLEncode(string(check.Result)) + "</td></tr>\n")
		}
		html.WriteString("</table>\n")
	} else {
		html.WriteString("<p class=\"muted\">No verified checks recorded.</p>\n")
	}
	sentence := "What an agent in this scope would be shown for it, were it eligible:"
	if lesson.IsEligible {
		sentence = "What an agent in this scope would be shown, if this lesson is retrieved (its ranking line and record number depend on the task and the other lessons):"
	}
	html.WriteString("<p class=\"muted\">" + sentence + "</p>\n<pre><code>" + HTMLEncode(lesson.BlockText) +
		"</code></pre>\n</section>\n")
}

// LessonsHTMLContentSecurityPolicy is the policy header LessonsHTMLHandler sends: a <meta> policy cannot set
// frame-ancestors, so the header carries it.
const LessonsHTMLContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'"

// LessonsHTMLHandler serves the lessons page of the caller's own scope. It maps nothing itself: mount it where you
// choose, behind your own authorization. Exposing it publicly discloses every lesson in the scope.
//
// Identity resolves the caller from the host's own authentication (the request's authenticated principal), never
// from its query or body: nil, nil answers 403 Forbidden and an error 500, each with no body detail. Query is the
// lessons query (the defaults when zero). The page is sent as text/html; charset=utf-8 with Cache-Control: no-store,
// X-Frame-Options: DENY and LessonsHTMLContentSecurityPolicy; a HEAD request gets the headers alone, and a method
// other than GET or HEAD 405.
type LessonsHTMLHandler struct {
	Lessons  *Lessons
	Identity func(r *http.Request) (*Identity, error)
	Query    LessonQuery
}

// ServeHTTP lists the identity's lessons and writes the page.
func (h LessonsHTMLHandler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.Header().Set("Allow", "GET, HEAD")
		http.Error(w, http.StatusText(http.StatusMethodNotAllowed), http.StatusMethodNotAllowed)
		return
	}
	if h.Lessons == nil || h.Identity == nil {
		http.Error(w, http.StatusText(http.StatusInternalServerError), http.StatusInternalServerError)
		return
	}
	identity, err := h.Identity(r)
	if err != nil {
		http.Error(w, http.StatusText(http.StatusInternalServerError), http.StatusInternalServerError)
		return
	}
	if identity == nil {
		http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
		return
	}
	page, err := h.Lessons.ListFor(r.Context(), *identity, h.Query)
	if err != nil {
		http.Error(w, http.StatusText(http.StatusInternalServerError), http.StatusInternalServerError)
		return
	}
	body := []byte(RenderLessonsHTML(page))
	header := w.Header()
	header.Set("Content-Type", "text/html; charset=utf-8")
	header.Set("Cache-Control", "no-store")
	header.Set("X-Frame-Options", "DENY")
	header.Set("Content-Security-Policy", LessonsHTMLContentSecurityPolicy)
	header.Set("X-Content-Type-Options", "nosniff")
	header.Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(http.StatusOK)
	if r.Method == http.MethodGet {
		_, _ = bytes.NewReader(body).WriteTo(w)
	}
}
