using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Security;

/// <summary>
/// Keeps <c>docs/security-suite.md</c> honest. That page is the one place a reader sees that all four
/// security properties -- tenant isolation, sanitization, revoked records, untrusted context -- are
/// covered, and by which tests. A map that can name a test which was since renamed or deleted is worse
/// than no map, so every row is checked against the source tree here.
/// </summary>
public sealed partial class SecuritySuiteMapTests
{
    /// <summary>
    /// The four properties, the heading each section starts with, and the fewest tests each may list.
    /// Lowering a minimum is a reviewed change to this file, not an accident of deleting rows.
    /// </summary>
    public static TheoryData<string, int> Properties() => new()
    {
        { "## 1. Tenant isolation", 20 },
        { "## 2. Sanitization", 8 },
        { "## 3. Revoked and superseded records", 8 },
        { "## 4. Untrusted context", 6 },
    };

    [Theory]
    [MemberData(nameof(Properties))]
    public void Every_property_is_covered_by_at_least_its_minimum_number_of_tests(string heading, int minimum)
    {
        var rows = Rows(Section(heading));

        Assert.True(rows.Count >= minimum, $"'{heading}' lists {rows.Count} tests; at least {minimum} are required.");
    }

    [Fact]
    public void Every_test_the_map_names_exists_in_the_project_it_names()
    {
        var all = Rows(Map());
        Assert.True(all.Count >= 42, $"Only {all.Count} rows parsed from the map, so the parser is probably broken.");

        var missing = all.Where(row => !Exists(row)).Select(row => $"{row.Project}: {row.Class}.{row.Method}").ToList();

        Assert.True(missing.Count == 0, "The security suite map names tests that do not exist:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void A_skipped_test_does_not_count_as_coverage()
    {
        const string Source = """
            public class IsolationTests
            {
                [Fact(Skip = "flaky")]
                public async Task Foreign_scope_is_refused() { }
            }
            """;

        Assert.False(DeclaresRunnableTest(Source, "IsolationTests", "Foreign_scope_is_refused"));
        Assert.True(DeclaresRunnableTest(Source.Replace("(Skip = \"flaky\")", "", StringComparison.Ordinal), "IsolationTests", "Foreign_scope_is_refused"));
    }

    [Fact]
    public void A_method_in_a_different_class_does_not_satisfy_the_row()
    {
        const string Source = """
            public class IsolationTests
            {
                [Fact]
                public void Something_else() { }
            }

            public class OtherTests
            {
                [Fact]
                public void Foreign_scope_is_refused() { }
            }
            """;

        Assert.False(DeclaresRunnableTest(Source, "IsolationTests", "Foreign_scope_is_refused"));
        Assert.True(DeclaresRunnableTest(Source, "OtherTests", "Foreign_scope_is_refused"));
    }

    [Fact]
    public void An_undecorated_method_is_not_a_test()
    {
        const string Source = """
            public class IsolationTests
            {
                [Obsolete]
                public void Foreign_scope_is_refused() { }
            }
            """;

        Assert.False(DeclaresRunnableTest(Source, "IsolationTests", "Foreign_scope_is_refused"));
    }

    [Fact]
    public void No_test_is_listed_twice_under_one_property()
    {
        foreach (var heading in Properties().Select(row => (string)row[0]))
        {
            var duplicates = Rows(Section(heading))
                .GroupBy(row => $"{row.Class}.{row.Method}", StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, $"'{heading}' lists {string.Join(", ", duplicates)} more than once.");
        }
    }

    private sealed record Row(string Project, string Class, string Method);

    private static string Map() => File.ReadAllText(Path.Combine(RepositoryRoot.Path, "docs", "security-suite.md"));

    private static string Section(string heading)
    {
        var map = Map();
        var start = map.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/security-suite.md has no '{heading}' section.");

        var next = map.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? map[start..] : map[start..next];
    }

    private static List<Row> Rows(string markdown) =>
        RowPattern().Matches(markdown)
            .Select(match => new Row(match.Groups["project"].Value.Trim(), match.Groups["class"].Value, match.Groups["method"].Value))
            .ToList();

    private static bool Exists(Row row)
    {
        var directory = Path.Combine(RepositoryRoot.Path, "tests", row.Project == "ReuseBaseline" ? "AgentExperience.ReuseBaseline" : $"AgentExperience.{row.Project}");
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .Any(source => DeclaresRunnableTest(source, row.Class, row.Method));
    }

    /// <summary>
    /// True when <paramref name="source"/> declares class <paramref name="className"/> and, inside that
    /// class's own body, a <c>[Fact]</c> or <c>[Theory]</c> method named <paramref name="methodName"/>
    /// that is not skipped. A skipped test runs nothing, so it cannot count as coverage; and a method of
    /// the same name in a different class is not the test the map names.
    /// </summary>
    internal static bool DeclaresRunnableTest(string source, string className, string methodName)
    {
        var methodPattern = new Regex(
            $@"(?<attributes>(\[[^\]]*\]\s*)+)public\s+(async\s+Task|void|Task)\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.CultureInvariant);

        foreach (Match declaration in new Regex($@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.CultureInvariant).Matches(source))
        {
            var body = ClassBody(source, declaration.Index + declaration.Length);
            foreach (Match method in methodPattern.Matches(body))
            {
                var attributes = method.Groups["attributes"].Value;
                if (Regex.IsMatch(attributes, @"\[(Fact|Theory)\b", RegexOptions.CultureInvariant)
                    && !attributes.Contains("Skip", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The text between the first <c>{</c> after <paramref name="from"/> and its matching <c>}</c>.</summary>
    private static string ClassBody(string source, int from)
    {
        var open = source.IndexOf('{', from);
        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[(open + 1)..i];
            }
        }

        return source[(open + 1)..];
    }

    /// <summary>A table row: <c>| Project | `Class.Method` | ... |</c>.</summary>
    [GeneratedRegex(@"^\|\s*(?<project>[A-Za-z.]+)\s*\|\s*`(?<class>[A-Za-z_][A-Za-z0-9_]*)\.(?<method>[A-Za-z_][A-Za-z0-9_]*)`\s*\|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();
}
