"""
Story 27.5 (contract 1.14): the lessons page as HTML. The ``lessons-html`` fixtures, rendered by .NET's
``ExperienceLessonsHtml``, through this package's renderer byte for byte; then the encoding rules and the handler.
"""

from __future__ import annotations

import uuid
from typing import Any

import pytest
from _contract import FIXTURES, cases, read_json, scope_of
from test_lessons import memory_store

from agentexperience import ExperienceLessons, Instant
from agentexperience.identity import ExperienceIdentity
from agentexperience.lessons import Lesson, LessonCheck, LessonsPage
from agentexperience.lessons_html import (
    CONTENT_SECURITY_POLICY,
    html_encode,
    lessons_html_response,
    render_lessons_html,
)

FAMILY = FIXTURES / "lessons-html"


def page_of(value: dict[str, Any]) -> LessonsPage:
    """A page in the lessons-view ``expected.json`` form, with its scope."""

    def instant(text: str | None) -> Instant | None:
        return None if text is None else Instant.parse(text)

    lessons = tuple(
        Lesson(
            uuid.UUID(lesson["experienceId"]),
            lesson["taskId"],
            lesson["status"],
            lesson["confidence"],
            Instant.parse(lesson["createdAt"]),
            Instant.parse(lesson["updatedAt"]),
            tuple(LessonCheck(check["checkId"], check["evidenceKind"], check["result"]) for check in lesson["checks"]),
            lesson["blockText"],
            lesson["isEligible"],
            lesson["ineligibleReason"],
            lesson["ineligibility"],
            instant(lesson["lastInjectedAt"]),
            lesson["injectionCount"],
        )
        for lesson in value["lessons"]
    )
    return LessonsPage(
        value["outcome"],
        scope_of(value["scope"]),
        lessons,
        Instant.parse(value["generatedAt"]),
        value["hasMore"],
        value["outOfScopeDropped"],
        value["injectionsTracked"],
    )


@pytest.mark.parametrize("name", cases("lessons-html"))
def test_lessons_html(name: str) -> None:
    html = render_lessons_html(page_of(read_json(FAMILY / name / "page.json")))
    assert html.encode("utf-8") == (FAMILY / name / "expected.html").read_bytes()


def test_the_lessons_html_cases() -> None:
    assert cases("lessons-html") == ["denied", "escaping", "has-more-not-tracked", "listed-lessons", "no-lessons"]


@pytest.mark.parametrize(
    ("value", "encoded"),
    [
        ("<a href=\"x\">'&'</a>", "&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;"),
        ("\u009f\u00a0ÿĀ", "\u009f&#160;&#255;Ā"),
        ("\U0001f600", "&#128512;"),
        (chr(0xD83D) + chr(0xDE00), "&#128512;"),
        ("a\ud800b", "a\ufffdb"),
        ("x\udc00", "x\ufffd"),
        ("", ""),
        (None, ""),
    ],
)
def test_html_encoding_is_webutilitys(value: str | None, encoded: str) -> None:
    assert html_encode(value) == encoded


def test_the_handler_sends_the_page_with_its_headers() -> None:
    seed = read_json(FIXTURES / "lesson-export" / "measured-helps-only" / "seed.json")
    now = Instant.parse("2026-03-03T09:00:00+00:00")
    store = memory_store(seed)
    scope = scope_of(read_json(FIXTURES / "lesson-export" / "measured-helps-only" / "options.json")["scope"])
    view = ExperienceLessons(store, clock=lambda: now)
    response = lessons_html_response(view, ExperienceIdentity("principal", scope))
    assert response.status == 200
    headers = dict(response.headers)
    assert headers == {
        "Content-Type": "text/html; charset=utf-8",
        "Cache-Control": "no-store",
        "X-Frame-Options": "DENY",
        "Content-Security-Policy": CONTENT_SECURITY_POLICY,
    }
    assert "frame-ancestors 'none'" in CONTENT_SECURITY_POLICY
    assert response.body == render_lessons_html(view.list(ExperienceIdentity("principal", scope))).encode("utf-8")
    assert response.body.startswith(b"<!doctype html>\n")
    with pytest.raises(ValueError):
        lessons_html_response(view, None)  # type: ignore[arg-type]
