using System.Globalization;
using System.Net;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Renders an <see cref="ExperienceLessonsPage"/> as one self-contained HTML document: inline CSS, no script, no
/// external request, light and dark mode. For a development view the host serves behind its own authorization.
/// </summary>
/// <remarks>
/// Every value drawn from a record (task IDs, check IDs, evidence kinds, block text, scope segments) is untrusted data,
/// and is HTML-encoded before it is written. The document also carries a content security policy that allows no
/// script, no external resource and no form submission, so even a value that reached the page unencoded could not run.
/// </remarks>
public static class ExperienceLessonsHtml
{
    /// <summary>The banner every page opens with.</summary>
    public const string DevelopmentBanner =
        "Development view: these are the lessons stored in this scope. It discloses them, so serve it only behind your own authorization.";

    /// <summary>What a page with no lessons says.</summary>
    public const string NoLessons = "No lessons yet.";

    /// <summary>A lesson's "last injected" value when it was never injected in this scope.</summary>
    public const string Never = "never";

    /// <summary>A lesson's "last injected" value when the store does not track injections, so whether it was is not known.</summary>
    public const string NotTracked = "not tracked";

    private const string Style = """
        :root{color-scheme:light dark;--bg:#ffffff;--fg:#1b1f24;--muted:#59636e;--line:#d1d9e0;--panel:#f6f8fa;--banner:#fff8c5;--banner-line:#d4a72c;--ok:#1a7f37;--warn:#9a6700;--bad:#cf222e}
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
        code{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}
        """;

    /// <summary>Renders <paramref name="page"/> as one HTML document.</summary>
    /// <param name="page">The page <see cref="AgentExperienceLessons.ListAsync"/> returned.</param>
    /// <returns>The document, starting with <c>&lt;!doctype html&gt;</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    public static string Render(ExperienceLessonsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'\">\n")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
            .Append("<title>Agent experience lessons</title>\n<style>\n").Append(Style).Append("</style>\n</head>\n<body>\n<main>\n");

        html.Append("<p class=\"banner\" role=\"note\"><strong>").Append(Encode(DevelopmentBanner)).Append("</strong></p>\n");
        html.Append("<h1>What it learned</h1>\n");

        var scope = page.Scope;
        html.Append("<p class=\"muted\">Scope: <code>").Append(Encode(ScopeText(scope))).Append("</code> · read ")
            .Append(Encode(Time(page.GeneratedAt))).Append("</p>\n");

        if (page.Outcome != ExperienceStoreOutcome.Found)
        {
            html.Append("<p class=\"failed\">The store answered <strong>").Append(Encode(page.Outcome.ToString()))
                .Append("</strong>, so nothing is listed.</p>\n");
        }
        else if (page.Lessons.Where(lesson => lesson is not null).ToList() is not { Count: > 0 } lessons)
        {
            html.Append("<p>").Append(Encode(NoLessons)).Append(" A lesson is stored when a run's own checks verify it.</p>\n");
        }
        else
        {
            html.Append("<p class=\"muted\">Showing ").Append(lessons.Count.ToString(CultureInfo.InvariantCulture))
                .Append(lessons.Count == 1 ? " lesson" : " lessons").Append(", newest first.");
            if (page.HasMore)
            {
                html.Append(" More exist; raise the limit or filter by status to see them.");
            }

            html.Append("</p>\n");
            foreach (var lesson in lessons)
            {
                Lesson(html, lesson, page.InjectionsTracked);
            }
        }

        if (page.OutOfScopeDropped > 0)
        {
            html.Append("<p class=\"warn\">The store answered ").Append(page.OutOfScopeDropped.ToString(CultureInfo.InvariantCulture))
                .Append(" record(s) from outside this scope; they were left out.</p>\n");
        }

        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void Lesson(StringBuilder html, ExperienceLesson lesson, bool injectionsTracked)
    {
        html.Append("<section class=\"lesson\">\n<h2>").Append(Encode(lesson.TaskId)).Append("</h2>\n");

        html.Append("<div class=\"meta\"><span class=\"badge\">").Append(Encode(lesson.Status.ToString())).Append("</span>");
        if (lesson.IsEligible)
        {
            html.Append("<span class=\"badge eligible\">eligible for retrieval</span>");
        }
        else
        {
            html.Append("<span class=\"badge ineligible\">not eligible for retrieval: ").Append(Encode(lesson.IneligibleReason ?? "not eligible")).Append("</span>");
        }

        if (lesson.ConsolidatedFrom is { } sources)
        {
            html.Append("<span class=\"badge\">consolidated from ").Append(sources.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        }

        html.Append("</div>\n<div class=\"meta muted\">")
            .Append("<span>confidence ").Append(Encode(Number(lesson.Confidence))).Append("</span>")
            .Append("<span>stored ").Append(Encode(Time(lesson.CreatedAt))).Append("</span>")
            .Append("<span>last activity ").Append(Encode(Time(lesson.UpdatedAt))).Append("</span>")
            .Append("<span>last injected ").Append(Encode(LastInjected(lesson, injectionsTracked))).Append("</span>")
            .Append("<span>experience <code>").Append(Encode(lesson.ExperienceId.ToString("D", CultureInfo.InvariantCulture))).Append("</code></span>")
            .Append("</div>\n");

        if (lesson.Checks is { Count: > 0 } checks)
        {
            html.Append("<table>\n<tr><th>Check</th><th>Evidence kind</th><th>Result</th></tr>\n");
            foreach (var check in checks)
            {
                if (check is null)
                {
                    continue;
                }

                html.Append("<tr><td><code>").Append(Encode(check.CheckId)).Append("</code></td><td>")
                    .Append(Encode(check.EvidenceKind)).Append("</td><td")
                    .Append(check.Result switch
                    {
                        CheckResult.Pass => " class=\"eligible\"",
                        CheckResult.Fail => " class=\"failed\"",
                        _ => " class=\"warn\"",
                    }).Append('>')
                    .Append(Encode(check.Result.ToString())).Append("</td></tr>\n");
            }

            html.Append("</table>\n");
        }
        else
        {
            html.Append("<p class=\"muted\">No verified checks recorded.</p>\n");
        }

        html.Append("<p class=\"muted\">")
            .Append(lesson.IsEligible
                ? "What an agent in this scope would be shown, if this lesson is retrieved (its ranking line and record number depend on the task and the other lessons):"
                : "What an agent in this scope would be shown for it, were it eligible:")
            .Append("</p>\n<pre><code>").Append(Encode(lesson.BlockText)).Append("</code></pre>\n</section>\n");
    }

    private static string ScopeText(Scope? scope)
    {
        if (scope is null)
        {
            return "(none)";
        }

        var text = new StringBuilder();
        text.Append("tenant ").Append(scope.TenantId).Append(" / application ").Append(scope.ApplicationId)
            .Append(" / project ").Append(scope.ProjectId);
        if (scope.TeamId is { } team)
        {
            text.Append(" / team ").Append(team);
        }

        if (scope.AgentId is { } agent)
        {
            text.Append(" / agent ").Append(agent);
        }

        if (scope.UserId is { } user)
        {
            text.Append(" / user ").Append(user);
        }

        return text.ToString();
    }

    /// <summary>The "last injected" value: the instant and count, "never", or "not tracked" when the store cannot say.</summary>
    private static string LastInjected(ExperienceLesson lesson, bool injectionsTracked)
    {
        if (lesson.LastInjectedAt is { } at)
        {
            return Time(at) + " (" + lesson.InjectionCount.ToString(CultureInfo.InvariantCulture)
                + (lesson.InjectionCount == 1 ? " time)" : " times)");
        }

        return injectionsTracked ? Never : NotTracked;
    }

    private static string Number(double value) =>
        double.IsFinite(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : "(unavailable)";

    private static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>HTML-encodes <paramref name="value"/>, quotes and apostrophes included, so it is safe in text and in attributes.</summary>
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
