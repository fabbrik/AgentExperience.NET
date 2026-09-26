using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Documentation;

/// <summary>
/// Every link between the repository's own Markdown files resolves: a relative link names a file or directory that
/// exists, and a <c>#anchor</c> names a heading (or an explicit HTML anchor) in the file it points at, slugged the way
/// GitHub slugs headings. Absolute links to this repository on github.com (which the package READMEs use, because
/// relative links break on nuget.org) are resolved against the working tree the same way.
/// </summary>
/// <remarks>
/// Planning history under <c>_sdlc/</c> is not checked, and neither are the released sections of
/// <c>CHANGELOG.md</c>, which record what was true when each version shipped.
/// </remarks>
public sealed class MarkdownLinkTests
{
    private const string RepositoryUrl = "https://github.com/fabbrik/AgentExperience.NET";

    [Fact]
    public void Every_relative_link_and_anchor_in_the_documentation_resolves()
    {
        var files = TrackedMarkdown();
        Assert.Contains("README.md", files);

        var headings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var broken = new List<string>();

        foreach (var file in files)
        {
            foreach (var (line, target) in Links(Checked(file)))
            {
                var problem = Resolve(file, target, headings);
                if (problem is not null)
                {
                    broken.Add($"{file}:{line}: ({target}) {problem}");
                }
            }
        }

        Assert.True(broken.Count == 0, $"{broken.Count} broken documentation link(s):\n" + string.Join("\n", broken));
    }

    [Theory]
    [InlineData("Known limits", "known-limits")]
    [InlineData("Deploying with two roles", "deploying-with-two-roles")]
    [InlineData("Crypto-shredding: erasure that reaches every copy", "crypto-shredding-erasure-that-reaches-every-copy")]
    [InlineData("Reused sessions: a budget, no repeats, and withdrawal notices", "reused-sessions-a-budget-no-repeats-and-withdrawal-notices")]
    [InlineData("The version policy: floors, and one bounded range", "the-version-policy-floors-and-one-bounded-range")]
    [InlineData("1. Tenant isolation — a caller in scope A cannot retrieve", "1-tenant-isolation--a-caller-in-scope-a-cannot-retrieve")]
    [InlineData("`net8.0` only", "net80-only")]
    [InlineData("Why run A does not use `UseExperienceCapture`", "why-run-a-does-not-use-useexperiencecapture")]
    [InlineData("A [linked](https://example.org) heading", "a-linked-heading")]
    [InlineData("error.class and `nested`", "errorclass-and-nested")]
    public void Headings_are_slugged_as_GitHub_slugs_them(string heading, string slug) =>
        Assert.Equal(slug, Slug(heading));

    [Fact]
    public void A_broken_anchor_and_a_missing_file_are_reported()
    {
        var headings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        Assert.Null(Resolve("README.md", "RELEASING.md#the-checks", headings));
        Assert.NotNull(Resolve("README.md", "RELEASING.md#no-such-heading", headings));
        Assert.NotNull(Resolve("README.md", "docs/no-such-file.md", headings));
        Assert.NotNull(Resolve("README.md", RepositoryUrl + "/blob/main/docs/no-such-file.md", headings));
        Assert.Null(Resolve("README.md", RepositoryUrl + "/blob/main/RELEASING.md#the-checks", headings));
    }

    // ---- files ----

    /// <summary>The tracked Markdown files, relative to the root with forward slashes, minus planning history.</summary>
    private static List<string> TrackedMarkdown()
    {
        IEnumerable<string> files;
        try
        {
            var start = new ProcessStartInfo("git", "ls-files -z -- *.md")
            {
                WorkingDirectory = RepositoryRoot.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0)
            {
                throw new InvalidOperationException("git ls-files failed");
            }

            files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No git (a source archive): walk the tree instead, skipping build output.
            files = Directory.EnumerateFiles(RepositoryRoot.Path, "*.md", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(RepositoryRoot.Path, path).Replace('\\', '/'))
                .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or ".git" or "artifacts" or "node_modules" || part.StartsWith('.')));
        }

        return files
            .Where(path => !path.StartsWith("_sdlc/", StringComparison.Ordinal))
            .Where(path => File.Exists(Path.Combine(RepositoryRoot.Path, path)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The part of a file whose links are checked: all of it, except the released sections of the changelog.</summary>
    private static string Checked(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Path, file)).ReplaceLineEndings("\n");
        if (file != "CHANGELOG.md")
        {
            return text;
        }

        // Keep the header and the Unreleased section; blank out every released version's section, keeping line numbers.
        var lines = text.Split('\n');
        var inRelease = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                inRelease = !lines[i].Equals("## Unreleased", StringComparison.Ordinal);
            }

            if (inRelease)
            {
                lines[i] = string.Empty;
            }
        }

        return string.Join('\n', lines);
    }

    // ---- links ----

    private static readonly Regex InlineLink = new(@"!?\[(?:[^\[\]]|\[[^\[\]]*\])*\]\((?<target><[^>]*>|[^()\s]*(?:\([^()\s]*\)[^()\s]*)*)(?:\s+""[^""]*"")?\)", RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceDefinition = new(@"^\s{0,3}\[[^\]]+\]:\s*(?<target>\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex InlineCode = new(@"(`+)(?:(?!\1).)+?\1", RegexOptions.CultureInvariant);

    /// <summary>Every link target outside code, with its 1-based line number.</summary>
    private static IEnumerable<(int Line, string Target)> Links(string markdown)
    {
        var inFence = false;
        var fence = string.Empty;
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = trimmed[..3];
                if (!inFence)
                {
                    inFence = true;
                    fence = marker;
                }
                else if (marker == fence)
                {
                    inFence = false;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            var text = InlineCode.Replace(line, string.Empty);
            foreach (Match link in InlineLink.Matches(text))
            {
                yield return (i + 1, link.Groups["target"].Value.Trim('<', '>'));
            }

            var definition = ReferenceDefinition.Match(text);
            if (definition.Success)
            {
                yield return (i + 1, definition.Groups["target"].Value.Trim('<', '>'));
            }
        }
    }

    /// <summary>Null when the target resolves; otherwise why it does not.</summary>
    private static string? Resolve(string file, string target, Dictionary<string, HashSet<string>> headings)
    {
        string pathPart;
        string? anchor;

        var hash = target.IndexOf('#', StringComparison.Ordinal);
        (pathPart, anchor) = hash < 0 ? (target, null) : (target[..hash], target[(hash + 1)..]);

        string resolved;
        if (pathPart.StartsWith(RepositoryUrl, StringComparison.OrdinalIgnoreCase))
        {
            var rest = pathPart[RepositoryUrl.Length..];
            if (rest is "" or "/")
            {
                resolved = "README.md";
            }
            else
            {
                var repoPath = Regex.Match(rest, "^/(?:blob|tree)/main/(?<path>.+)$", RegexOptions.CultureInvariant);
                if (!repoPath.Success)
                {
                    return null; // actions, issues, releases and the like: not a file in the tree
                }

                resolved = Uri.UnescapeDataString(repoPath.Groups["path"].Value).TrimEnd('/');
            }
        }
        else if (Regex.IsMatch(pathPart, "^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant))
        {
            return null; // another site, or mailto:
        }
        else if (pathPart.Length == 0)
        {
            resolved = file;
        }
        else if (pathPart.StartsWith('/'))
        {
            return "is root-relative; use a path relative to the file";
        }
        else
        {
            var directory = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? string.Empty;
            var combined = Path.GetFullPath(Path.Combine(RepositoryRoot.Path, directory, Uri.UnescapeDataString(pathPart)));
            var root = Path.GetFullPath(RepositoryRoot.Path);
            if (!combined.StartsWith(root, StringComparison.Ordinal))
            {
                return "points outside the repository";
            }

            resolved = Path.GetRelativePath(root, combined).Replace('\\', '/');
        }

        var full = Path.Combine(RepositoryRoot.Path, resolved);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return $"'{resolved}' does not exist";
        }

        if (string.IsNullOrEmpty(anchor))
        {
            return null;
        }

        if (!resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return null; // an anchor into a non-Markdown file (a line number, say) is not checked
        }

        if (!headings.TryGetValue(resolved, out var anchors))
        {
            anchors = Anchors(File.ReadAllText(full).ReplaceLineEndings("\n"));
            headings[resolved] = anchors;
        }

        return anchors.Contains(Uri.UnescapeDataString(anchor)) ? null : $"'{resolved}' has no heading or anchor '#{anchor}'";
    }

    // ---- anchors ----

    private static readonly Regex AtxHeading = new(@"^\s{0,3}(?<level>#{1,6})\s+(?<text>.*?)\s*#*\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex HtmlAnchor = new(@"<a\s+(?:[^>]*\s)?(?:id|name)\s*=\s*""(?<id>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every anchor a file defines, with GitHub's <c>-1</c>, <c>-2</c> suffixes for repeated headings.</summary>
    private static HashSet<string> Anchors(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var inFence = false;
        var fence = string.Empty;

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = trimmed[..3];
                if (!inFence)
                {
                    inFence = true;
                    fence = marker;
                }
                else if (marker == fence)
                {
                    inFence = false;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            foreach (Match html in HtmlAnchor.Matches(line))
            {
                anchors.Add(html.Groups["id"].Value);
            }

            var heading = AtxHeading.Match(line);
            if (!heading.Success)
            {
                continue;
            }

            var slug = Slug(heading.Groups["text"].Value);
            if (seen.TryGetValue(slug, out var count))
            {
                seen[slug] = count + 1;
                anchors.Add($"{slug}-{count + 1}");
            }
            else
            {
                seen[slug] = 0;
                anchors.Add(slug);
            }
        }

        return anchors;
    }

    /// <summary>
    /// GitHub's heading slug: the heading's rendered text (links reduced to their text, code to its content, emphasis
    /// markers dropped), lower-cased, with every character that is not a letter, a digit, a mark, a space, a hyphen or
    /// an underscore removed, and each space turned into a hyphen.
    /// </summary>
    internal static string Slug(string heading)
    {
        var text = Regex.Replace(heading, @"!?\[(?<text>[^\]]*)\]\([^)]*\)", "${text}", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, "<[^>]+>", string.Empty, RegexOptions.CultureInvariant);
        text = text.Replace("`", string.Empty, StringComparison.Ordinal);

        var slug = new StringBuilder(text.Length);
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value == ' ')
            {
                slug.Append('-');
            }
            else if (rune.Value is '-' or '_'
                || Rune.IsLetterOrDigit(rune)
                || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
            {
                slug.Append(rune.ToString());
            }
        }

        return slug.ToString();
    }
}
