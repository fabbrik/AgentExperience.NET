using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentExperience.Tests.Shared;

namespace AgentExperience.Release.Tests.Compatibility;

/// <summary>
/// Story 6.3 (KL-13): the supported matrix is stated in four places — the build's target frameworks, the
/// package verifier, CI, and the evidence document — and these tests fail when any of them names something
/// the others do not. A claim that one of them made alone would be a claim nothing runs.
/// </summary>
public sealed class SupportMatrixTests
{
    private static readonly string Root = RepositoryRoot.Path;

    [Fact]
    public void The_package_verifier_checks_exactly_the_target_frameworks_the_build_produces()
    {
        var verifier = File.ReadAllText(Path.Combine(Root, "eng", "verify-packages.cs"));
        var literal = Regex.Match(verifier, @"string\[\] frameworks = \[(?<list>[^\]]*)\];", RegexOptions.CultureInvariant);
        Assert.True(literal.Success, "eng/verify-packages.cs no longer declares its frameworks list.");

        var verified = Regex.Matches(literal.Groups["list"].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal);

        Assert.Equal(TargetFrameworks(), verified);
    }

    [Fact]
    public void CI_installs_a_runtime_for_every_target_framework_the_pinned_SDK_does_not_carry()
    {
        var ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml")).ReplaceLineEndings("\n");
        var sdkMajor = Regex.Match(File.ReadAllText(Path.Combine(Root, "global.json")), "\"version\": *\"(?<major>[0-9]+)\\.").Groups["major"].Value;

        // Per job, not per file: one job losing the runtime would lose its net9.0 test runs on its own.
        var jobs = Regex.Split(ci[ci.IndexOf("\njobs:\n", StringComparison.Ordinal)..], @"\n(?=  [a-z-]+:\n)", RegexOptions.CultureInvariant)
            .Where(job => job.Contains("actions/setup-dotnet", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(jobs);

        foreach (var framework in TargetFrameworks().Where(f => f != $"net{sdkMajor}.0"))
        {
            var major = Regex.Match(framework, "^net(?<major>[0-9]+)\\.0$").Groups["major"].Value;
            Assert.False(string.IsNullOrEmpty(major), $"'{framework}' is not a netN.0 framework.");
            Assert.All(jobs, job => Assert.Contains($"dotnet-version: {major}.0.x", job, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The net9.0 builds of the packages are only ever executed by the test projects' net9.0 runs, so every
    /// test project must record every supported framework, except the three demonstrations that are
    /// deliberately single-target (Directory.Build.props says why).
    /// </summary>
    [Fact]
    public void Every_test_project_except_the_demonstrations_runs_on_every_supported_framework()
    {
        string[] singleTarget = ["AgentExperience.ReuseBaseline", "AgentExperience.Sample.EndToEnd.Tests"];
        var projects = Directory.GetDirectories(Path.Combine(Root, "tests"))
            .Where(path => File.Exists(Path.Combine(path, Path.GetFileName(path) + ".csproj")))
            .ToList();
        Assert.True(projects.Count >= 8, $"Only {projects.Count} test projects found.");

        foreach (var project in projects)
        {
            var name = Path.GetFileName(project)!;
            using var lockFile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "packages.lock.json")));
            var recorded = lockFile.RootElement.GetProperty("dependencies").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

            Assert.Equal(singleTarget.Contains(name) ? ["net10.0"] : TargetFrameworks(), recorded);
        }
    }

    [Fact]
    public void The_evidence_document_states_every_supported_framework_and_PostgreSQL_major()
    {
        var evidence = File.ReadAllText(Path.Combine(Root, "docs", "compatibility-evidence.md")).ReplaceLineEndings("\n");
        var matrix = Section(evidence, "## Supported matrix");

        foreach (var framework in TargetFrameworks())
        {
            Assert.Contains($"`{framework}`", matrix, StringComparison.Ordinal);
        }

        foreach (var major in PostgresTestImage.SupportedMajors)
        {
            Assert.Contains($"`pgvector/pgvector:pg{major}`", matrix, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_supported_PostgreSQL_major_is_one_the_default_can_be_and_the_list_is_ascending_and_distinct()
    {
        Assert.Contains(PostgresTestImage.DefaultMajor, PostgresTestImage.SupportedMajors);
        Assert.Equal(PostgresTestImage.SupportedMajors.Distinct().Order(), PostgresTestImage.SupportedMajors);
    }

    private static List<string> TargetFrameworks() =>
        XDocument.Load(Path.Combine(Root, "Directory.Build.props"))
            .Descendants("AgentExperienceTargetFrameworks")
            .Single()
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No '{heading}' section.");
        var next = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }
}
