using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Documentation;

/// <summary>
/// User-facing text names releases, never the internal planning items they were built under: no "story 17.2",
/// "Story 4", or "epic 18" in the root README, the guides, the package READMEs, or the XML doc comments of the shipped
/// source (which become the IntelliSense a consumer reads).
/// </summary>
/// <remarks>
/// Historical records keep their numbers and are not checked: <c>CHANGELOG.md</c>, <c>docs/limits-history.md</c>,
/// <c>RELEASING.md</c>, migration scripts, <c>_sdlc/</c>, and the tests. Consecutive <c>///</c> lines are joined
/// before matching, so a reference wrapped across two lines is still found.
/// </remarks>
public sealed class StoryReferenceTests
{
    private static readonly Regex Reference = new(
        @"\b(?:stor(?:y|ies)|epics?)\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void No_user_facing_text_names_a_story_or_an_epic()
    {
        var files = UserFacingFiles();
        Assert.Contains("README.md", files);
        Assert.Contains("docs/guide/concepts.md", files);
        Assert.Contains(files, file => file.StartsWith("src/", StringComparison.Ordinal) && file.EndsWith(".cs", StringComparison.Ordinal));

        var found = new List<string>();
        foreach (var file in files)
        {
            var text = UserFacingText(file);
            foreach (Match match in Reference.Matches(text))
            {
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                found.Add($"{file}:{line}: '{match.Value.ReplaceLineEndings(" ")}'");
            }
        }

        Assert.True(found.Count == 0, $"{found.Count} internal planning reference(s) in user-facing text; name the release instead:\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("since story 17.2", true)]
    [InlineData("Story 4's harness", true)]
    [InlineData("before story\n17.4", true)]
    [InlineData("between stories 14.2 and 14.3", true)]
    [InlineData("Epic 2 owns storage", true)]
    [InlineData("since `0.1.0-preview.5`", false)]
    [InlineData("the story's whole point", false)]
    [InlineData("history 17 times", false)]
    public void The_pattern_catches_a_reference_and_nothing_else(string text, bool matches) =>
        Assert.Equal(matches, Reference.IsMatch(text));

    /// <summary>Repository-relative paths, with forward slashes, of every file the gate reads.</summary>
    private static List<string> UserFacingFiles()
    {
        var root = RepositoryRoot.Path;
        IEnumerable<string> Under(string directory, string pattern, SearchOption option) =>
            Directory.Exists(Path.Combine(root, directory))
                ? Directory.EnumerateFiles(Path.Combine(root, directory), pattern, option)
                : [];

        var files = new List<string> { Path.Combine(root, "README.md") };
        files.AddRange(Under(Path.Combine("docs", "guide"), "*.md", SearchOption.TopDirectoryOnly));
        files.AddRange(Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Select(directory => Path.Combine(directory, "README.md"))
            .Where(File.Exists));
        files.AddRange(Under("src", "*.cs", SearchOption.AllDirectories));

        return files
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(file => !file.Contains("/bin/", StringComparison.Ordinal) && !file.Contains("/obj/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A Markdown file whole; for C#, only the XML doc comment lines, joined, with every other line kept as an empty
    /// line so the reported line numbers still match the file.
    /// </summary>
    private static string UserFacingText(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Path, file));
        if (!file.EndsWith(".cs", StringComparison.Ordinal))
        {
            return text;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return string.Join('\n', lines.Select(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("///", StringComparison.Ordinal) ? trimmed[3..] : string.Empty;
        }));
    }
}
