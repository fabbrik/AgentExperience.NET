package agentexperience_test

// Contract 1.14: the lessons page, byte for byte .NET's, and its handler.

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"

	ae "github.com/fabbrik/AgentExperience.NET/go"
	ct "github.com/fabbrik/AgentExperience.NET/go/internal/contracttest"
)

func TestLessonsHTML(t *testing.T) {
	cases := ct.Cases("lessons-html")
	if len(cases) != 5 {
		t.Fatalf("lessons-html cases: %v", cases)
	}
	for _, name := range cases {
		t.Run(name, func(t *testing.T) {
			page, err := ct.LessonsPageOf(ct.MustReadJSON(fixture("lessons-html", name, "page.json")))
			if err != nil {
				t.Fatal(err)
			}
			expected, err := os.ReadFile(fixture("lessons-html", name, "expected.html"))
			if err != nil {
				t.Fatal(err)
			}
			if actual := ae.RenderLessonsHTML(page); actual != string(expected) {
				t.Fatalf("the page differs from .NET's:\n%s", firstDifference(string(expected), actual))
			}
		})
	}
}

// firstDifference shows where two texts first differ.
func firstDifference(expected, actual string) string {
	i := 0
	for i < len(expected) && i < len(actual) && expected[i] == actual[i] {
		i++
	}
	from := max(0, i-80)
	return "expected …" + expected[from:min(len(expected), i+80)] + "\nactual   …" + actual[from:min(len(actual), i+80)]
}

func TestHTMLEncodeIsWebUtilityHtmlEncode(t *testing.T) {
	for input, expected := range map[string]string{
		`<a href="x">'&'</a>`: "&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;",
		"\u00a0é\u00ff\u0100": "&#160;&#233;&#255;\u0100",
		"😀 雪 \u009f":          "&#128512; 雪 \u009f",
		"bad\xffbyte":         "bad\uFFFDbyte",
	} {
		if actual := ae.HTMLEncode(input); actual != expected {
			t.Errorf("%q: %q, not %q", input, actual, expected)
		}
	}
}

type pageStore struct{ records []ae.LessonRecord }

func (s pageStore) ListLessonRecords(_ context.Context, bounds ae.AuthorizationBounds, scope ae.Scope,
	_ []ae.ExperienceStatus, _ int) (ae.LessonRecords, error) {
	if !bounds.Permits(scope) {
		return ae.LessonRecords{}, ae.ErrAuthorizationDenied
	}
	return ae.LessonRecords{Records: s.records, Answered: len(s.records), InjectionsTracked: true}, nil
}

func TestTheLessonsHandlerServesThePageWithItsHeaders(t *testing.T) {
	scope := ae.Scope{TenantID: "t", ApplicationID: "a", ProjectID: "p"}
	lessons, err := ae.NewLessons(pageStore{}, ae.LessonsOptions{})
	if err != nil {
		t.Fatal(err)
	}
	identity := &ae.Identity{Principal: "someone", Scope: scope}
	handler := ae.LessonsHTMLHandler{Lessons: lessons, Identity: func(*http.Request) (*ae.Identity, error) { return identity, nil }}
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/lessons?scope=other", nil))
	if response.Code != http.StatusOK || !strings.HasPrefix(response.Body.String(), "<!doctype html>") ||
		!strings.Contains(response.Body.String(), "tenant t / application a / project p") {
		t.Fatalf("%d %s", response.Code, response.Body.String())
	}
	for key, value := range map[string]string{"Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store",
		"X-Frame-Options": "DENY", "Content-Security-Policy": ae.LessonsHTMLContentSecurityPolicy} {
		if response.Header().Get(key) != value {
			t.Errorf("%s: %q", key, response.Header().Get(key))
		}
	}

	head := httptest.NewRecorder()
	handler.ServeHTTP(head, httptest.NewRequest(http.MethodHead, "/lessons", nil))
	if head.Code != http.StatusOK || head.Body.Len() != 0 {
		t.Fatalf("HEAD: %d, %d bytes", head.Code, head.Body.Len())
	}
	post := httptest.NewRecorder()
	handler.ServeHTTP(post, httptest.NewRequest(http.MethodPost, "/lessons", nil))
	if post.Code != http.StatusMethodNotAllowed {
		t.Fatalf("POST: %d", post.Code)
	}

	// The host decides who may see it: no identity is 403, a failure 500, and neither says why.
	denied := ae.LessonsHTMLHandler{Lessons: lessons, Identity: func(*http.Request) (*ae.Identity, error) { return nil, nil }}
	forbidden := httptest.NewRecorder()
	denied.ServeHTTP(forbidden, httptest.NewRequest(http.MethodGet, "/lessons", nil))
	failing := ae.LessonsHTMLHandler{Lessons: lessons, Identity: func(*http.Request) (*ae.Identity, error) {
		return nil, errors.New("token secret-123 expired")
	}}
	failed := httptest.NewRecorder()
	failing.ServeHTTP(failed, httptest.NewRequest(http.MethodGet, "/lessons", nil))
	if forbidden.Code != http.StatusForbidden || failed.Code != http.StatusInternalServerError ||
		strings.Contains(failed.Body.String(), "secret") {
		t.Fatalf("%d, %d %q", forbidden.Code, failed.Code, failed.Body.String())
	}

	// Bounds outside the scope: the page says what the store answered.
	other := ae.BoundsOf(ae.Scope{TenantID: "other", ApplicationID: "a", ProjectID: "p"})
	outside := ae.LessonsHTMLHandler{Lessons: lessons, Identity: func(*http.Request) (*ae.Identity, error) {
		return &ae.Identity{Principal: "someone", Scope: scope, Bounds: &other}, nil
	}}
	page := httptest.NewRecorder()
	outside.ServeHTTP(page, httptest.NewRequest(http.MethodGet, "/lessons", nil))
	if !strings.Contains(page.Body.String(), "The store answered <strong>Denied</strong>") {
		t.Fatal(page.Body.String())
	}
}
