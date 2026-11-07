"""
The lessons page as HTML (contract 1.14): .NET's ``ExperienceLessonsHtml.Render``, byte for byte, and a handler a
host serves it with.

The document is self-contained: inline CSS, no script, no external request, light and dark mode. Every value drawn from
a record or the scope is HTML-encoded as .NET's ``WebUtility.HtmlEncode`` encodes it, and the document carries a content
security policy that allows no script, no external resource and no form submission.

**Authorization is the host's.** The page discloses every lesson in the scope. Serve it only behind your own
authorization, with the identity from your own authentication, never from the request's query string or body. The
package maps no route: :func:`lessons_html_response` builds the response, and the host's framework (ASGI or WSGI, any)
sends it.
"""

from __future__ import annotations

import asyncio
from collections.abc import Callable
from dataclasses import dataclass

from ._text import utf8_bytes
from .encoding import Instant
from .identity import ExperienceIdentity
from .lessons import ExperienceLessons, Lesson, LessonsPage, LessonsQuery, format_number
from .records import Scope

__all__ = [
    "CONTENT_SECURITY_POLICY",
    "DEVELOPMENT_BANNER",
    "NEVER",
    "NOT_TRACKED",
    "NO_LESSONS",
    "LessonsHtmlResponse",
    "html_encode",
    "lessons_html_response",
    "lessons_html_response_async",
    "render_lessons_html",
]

DEVELOPMENT_BANNER = (
    "Development view: these are the lessons stored in this scope. It discloses them, so serve it only behind your own "
    "authorization."
)
"""The banner every page opens with."""

NO_LESSONS = "No lessons yet."
"""What a page with no lessons says."""

NEVER = "never"
"""A lesson's "last injected" value when it was never injected in this scope."""

NOT_TRACKED = "not tracked"
"""A lesson's "last injected" value when the store does not track injections."""

CONTENT_SECURITY_POLICY = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'"
"""The ``Content-Security-Policy`` response header :func:`lessons_html_response` sends (a ``<meta>`` policy cannot set
``frame-ancestors``)."""

_STYLE = """:root{color-scheme:light dark;--bg:#ffffff;--fg:#1b1f24;--muted:#59636e;--line:#d1d9e0;--panel:#f6f8fa;\
--banner:#fff8c5;--banner-line:#d4a72c;--ok:#1a7f37;--warn:#9a6700;--bad:#cf222e}
@media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--line:#3d444d;--panel:#151b23;\
--banner:#2e2a14;--banner-line:#9e7a1d;--ok:#3fb950;--warn:#d29922;--bad:#f85149}}
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
pre{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:8px;overflow-x:auto;\
white-space:pre-wrap;word-break:break-word;font-size:.82rem}
code{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}"""

_ENTITIES = {"<": "&lt;", ">": "&gt;", "&": "&amp;", '"': "&quot;", "'": "&#39;"}


def html_encode(value: str | None) -> str:
    """
    .NET's ``WebUtility.HtmlEncode``: ``<``, ``>``, ``&``, ``"`` and ``'`` as entities (``&#39;``), every character from
    U+00A0 to U+00FF as a decimal reference, a character outside the Basic Multilingual Plane (or a surrogate pair held
    as two code points) as one decimal reference to its code point, a lone surrogate as U+FFFD, and everything else as
    it is.
    """
    if not value:
        return ""
    out: list[str] = []
    index = 0
    length = len(value)
    while index < length:
        ch = value[index]
        code = ord(ch)
        entity = _ENTITIES.get(ch)
        if entity is not None:
            out.append(entity)
        elif 160 <= code < 256 or code > 0xFFFF:
            out.append(f"&#{code};")
        elif 0xD800 <= code <= 0xDBFF and index + 1 < length and 0xDC00 <= ord(value[index + 1]) <= 0xDFFF:
            low = ord(value[index + 1])
            out.append(f"&#{0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)};")
            index += 1
        elif 0xD800 <= code <= 0xDFFF:
            out.append("\ufffd")
        else:
            out.append(ch)
        index += 1
    return "".join(out)


def _time(value: Instant) -> str:
    """``yyyy-MM-dd HH:mm:ss UTC``: the fraction of a second cut, never rounded."""
    text = value.format()
    return f"{text[:10]} {text[11:19]} UTC"


def _scope_text(scope: Scope) -> str:
    text = f"tenant {scope.tenant_id} / application {scope.application_id} / project {scope.project_id}"
    if scope.team_id is not None:
        text += f" / team {scope.team_id}"
    if scope.agent_id is not None:
        text += f" / agent {scope.agent_id}"
    if scope.user_id is not None:
        text += f" / user {scope.user_id}"
    return text


def _last_injected(lesson: Lesson, injections_tracked: bool) -> str:
    if lesson.last_injected_at is not None:
        count = lesson.injection_count
        return f"{_time(lesson.last_injected_at)} ({count} {'time)' if count == 1 else 'times)'}"
    return NEVER if injections_tracked else NOT_TRACKED


def render_lessons_html(page: LessonsPage) -> str:
    """``page`` as one HTML document, starting with ``<!doctype html>``, byte-identical to .NET's."""
    if page is None:
        raise TypeError("page is required.")
    html = [
        '<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n',
        '<meta name="viewport" content="width=device-width, initial-scale=1">\n',
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; "
        "form-action 'none'; base-uri 'none'\">\n",
        '<meta name="referrer" content="no-referrer">\n',
        "<title>Agent experience lessons</title>\n<style>\n",
        _STYLE,
        "</style>\n</head>\n<body>\n<main>\n",
        f'<p class="banner" role="note"><strong>{html_encode(DEVELOPMENT_BANNER)}</strong></p>\n',
        "<h1>What it learned</h1>\n",
        f'<p class="muted">Scope: <code>{html_encode(_scope_text(page.scope))}</code> · read '
        f"{html_encode(_time(page.generated_at))}</p>\n",
    ]
    lessons = [lesson for lesson in page.lessons if lesson is not None]
    if page.outcome != "Found":
        html.append(
            f'<p class="failed">The store answered <strong>{html_encode(page.outcome)}</strong>, so nothing is '
            "listed.</p>\n"
        )
    elif not lessons:
        html.append(f"<p>{html_encode(NO_LESSONS)} A lesson is stored when a run's own checks verify it.</p>\n")
    else:
        html.append(
            f'<p class="muted">Showing {len(lessons)}{" lesson" if len(lessons) == 1 else " lessons"}, newest first.'
        )
        if page.has_more:
            html.append(" More exist; raise the limit or filter by status to see them.")
        html.append("</p>\n")
        for lesson in lessons:
            html.append(_lesson(lesson, page.injections_tracked))
    if page.out_of_scope_dropped > 0:
        html.append(
            f'<p class="warn">The store answered {page.out_of_scope_dropped} record(s) from outside this scope; they '
            "were left out.</p>\n"
        )
    html.append("</main>\n</body>\n</html>\n")
    return "".join(html)


def _lesson(lesson: Lesson, injections_tracked: bool) -> str:
    html = [f'<section class="lesson">\n<h2>{html_encode(lesson.task_id)}</h2>\n']
    html.append(f'<div class="meta"><span class="badge">{html_encode(lesson.status)}</span>')
    if lesson.is_eligible:
        html.append('<span class="badge eligible">eligible for retrieval</span>')
    else:
        reason = lesson.ineligible_reason if lesson.ineligible_reason is not None else "not eligible"
        html.append(f'<span class="badge ineligible">not eligible for retrieval: {html_encode(reason)}</span>')
    html.append(
        '</div>\n<div class="meta muted">'
        f"<span>confidence {html_encode(format_number(lesson.confidence))}</span>"
        f"<span>stored {html_encode(_time(lesson.created_at))}</span>"
        f"<span>last activity {html_encode(_time(lesson.updated_at))}</span>"
        f"<span>last injected {html_encode(_last_injected(lesson, injections_tracked))}</span>"
        f"<span>experience <code>{html_encode(str(lesson.experience_id))}</code></span>"
        "</div>\n"
    )
    checks = [check for check in lesson.checks if check is not None]
    if lesson.checks:
        html.append("<table>\n<tr><th>Check</th><th>Evidence kind</th><th>Result</th></tr>\n")
        for check in checks:
            css = (
                ' class="eligible"'
                if check.result == "Pass"
                else ' class="failed"'
                if check.result == "Fail"
                else (' class="warn"')
            )
            html.append(
                f"<tr><td><code>{html_encode(check.check_id)}</code></td><td>{html_encode(check.evidence_kind)}</td>"
                f"<td{css}>{html_encode(check.result)}</td></tr>\n"
            )
        html.append("</table>\n")
    else:
        html.append('<p class="muted">No verified checks recorded.</p>\n')
    html.append(
        '<p class="muted">'
        + (
            "What an agent in this scope would be shown, if this lesson is retrieved (its ranking line and record "
            "number depend on the task and the other lessons):"
            if lesson.is_eligible
            else "What an agent in this scope would be shown for it, were it eligible:"
        )
        + f"</p>\n<pre><code>{html_encode(lesson.block_text)}</code></pre>\n</section>\n"
    )
    return "".join(html)


@dataclass(frozen=True)
class LessonsHtmlResponse:
    """
    A response any web framework can send: the status, the headers (for a page exactly ``Content-Type: text/html;
    charset=utf-8``, ``Cache-Control: no-store``, ``X-Frame-Options: DENY`` and the :data:`CONTENT_SECURITY_POLICY`;
    for a 405 only ``Allow: GET``; none for a 403 or a 500) and the body (the document in UTF-8; empty otherwise).
    In an ASGI app send ``status`` and ``headers`` (encoded) in ``http.response.start`` and ``body`` in
    ``http.response.body``; in a WSGI app pass the status line (``"200 OK"``, ``"405 Method Not Allowed"``) and
    ``headers`` to ``start_response`` and return ``[body]``.
    """

    status: int
    headers: tuple[tuple[str, str], ...]
    body: bytes


HEADERS: tuple[tuple[str, str], ...] = (
    ("Content-Type", "text/html; charset=utf-8"),
    ("Cache-Control", "no-store"),
    ("X-Frame-Options", "DENY"),
    ("Content-Security-Policy", CONTENT_SECURITY_POLICY),
)
"""The headers every response carries: no caching, no framing, no script."""


def lessons_html_response(
    lessons: ExperienceLessons,
    identity: ExperienceIdentity | Callable[[], ExperienceIdentity | None] | None,
    query: LessonsQuery | None = None,
    *,
    method: str = "GET",
) -> LessonsHtmlResponse:
    """
    The response to one request for the lessons page, independent of ASGI and WSGI, as the .NET endpoint of the
    deployment guide (``MapGet``) answers:

    - ``GET`` only: any other ``method``, ``HEAD`` included, is 405 with ``Allow: GET``;
    - ``identity`` is the host's own: an :class:`ExperienceIdentity`, or a function resolving it from the host's
      authentication. None is 403; a resolution or a read that raises is 500. Neither body says why;
    - otherwise 200, the page with :data:`HEADERS`, and nothing else. A scope outside the identity's bounds is a page
      that says so, as in .NET.

    This function authorizes nothing: the page discloses every lesson in the scope, so serve it only behind your own
    authorization, with the identity from your own authentication, never from the request's query or body.
    """
    if method != "GET":
        return LessonsHtmlResponse(405, (("Allow", "GET"),), b"")
    try:
        resolved = identity() if callable(identity) else identity
        if resolved is None:
            return LessonsHtmlResponse(403, (), b"")
        page = lessons.list(resolved, query)
        body = utf8_bytes(render_lessons_html(page))
    except Exception:
        return LessonsHtmlResponse(500, (), b"")
    return LessonsHtmlResponse(200, HEADERS, body)


async def lessons_html_response_async(
    lessons: ExperienceLessons,
    identity: ExperienceIdentity | Callable[[], ExperienceIdentity | None] | None,
    query: LessonsQuery | None = None,
    *,
    method: str = "GET",
) -> LessonsHtmlResponse:
    """:func:`lessons_html_response` on a worker thread, so a PostgreSQL read does not block the event loop."""
    return await asyncio.to_thread(lessons_html_response, lessons, identity, query, method=method)
