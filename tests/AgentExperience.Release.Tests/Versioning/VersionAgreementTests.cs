using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Versioning;

/// <summary>
/// Story 22.7: one tag releases the six NuGet packages, the Python distribution and the five Go modules at one version.
/// <c>Directory.Build.props</c> sets it; <c>python/pyproject.toml</c> (and the lock file's record of it) and every Go
/// module's requirement on another of this repository's modules carry the version derived from it, never a
/// pseudo-version. <c>release.yml</c>'s verify job runs the same check on the tagged commit.
/// </summary>
public sealed class VersionAgreementTests
{
    private const string ModulePrefix = "github.com/fabbrik/AgentExperience.NET/go";

    /// <summary>Each Go module, by directory, and the modules of this repository it requires.</summary>
    private static readonly Dictionary<string, string[]> GoModules = new(StringComparer.Ordinal)
    {
        ["go"] = [],
        ["go/inmemory"] = ["go"],
        ["go/postgres"] = ["go"],
        ["go/postgres/pgtest"] = ["go", "go/postgres"],
        ["go/maf"] = ["go", "go/inmemory", "go/postgres", "go/postgres/pgtest"],
    };

    [Theory]
    [InlineData("0.1.0", "preview.14", "0.1.0-preview.14", "0.1.0a14", "v0.1.0-preview.14")]
    [InlineData("1.0.0", "", "1.0.0", "1.0.0", "v1.0.0")]
    [InlineData("0.2.3", "preview.1", "0.2.3-preview.1", "0.2.3a1", "v0.2.3-preview.1")]
    public void The_versions_derive_from_the_props_version(string prefix, string suffix, string dotnet, string python, string go)
    {
        Assert.Equal(new ReleaseVersions(dotnet, python, go), ReleaseVersions.Derive(prefix, suffix));
    }

    [Theory]
    [InlineData("0.1.0", "rc.1")]
    [InlineData("0.1.0", "preview.0")]
    [InlineData("0.1.0", "preview.1x")]
    [InlineData("0.1", "preview.1")]
    public void A_version_the_rule_cannot_spell_in_every_language_is_refused(string prefix, string suffix)
    {
        Assert.Throws<ArgumentException>(() => ReleaseVersions.Derive(prefix, suffix));
    }

    /// <summary>
    /// The workflow's own copy of the rule, run in bash, agrees with this one on both the release shapes (a preview and
    /// a version with no suffix) and refuses what this one refuses.
    /// </summary>
    [Theory]
    [InlineData("0.1.0", "preview.14")]
    [InlineData("1.0.0", "")]
    [InlineData("0.2.3", "preview.1")]
    public void The_release_workflow_derives_the_same_versions(string prefix, string suffix)
    {
        var expected = ReleaseVersions.Derive(prefix, suffix);

        var (exitCode, output) = RunDeriveVersions(prefix, suffix);

        Assert.Equal(0, exitCode);
        Assert.Equal($"{expected.DotNet} {expected.Python} {expected.Go}", output.Trim());
    }

    [Theory]
    [InlineData("0.1.0", "rc.1")]
    [InlineData("0.1.0", "preview.1x")]
    [InlineData("0.1", "preview.1")]
    public void The_release_workflow_refuses_what_the_rule_refuses(string prefix, string suffix)
    {
        var (exitCode, _) = RunDeriveVersions(prefix, suffix);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void The_Python_distribution_is_the_props_version()
    {
        var expected = ReleaseVersions.FromProps();

        var pyproject = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "python", "pyproject.toml")).ReplaceLineEndings("\n");
        Assert.Empty(PythonViolations(pyproject, expected.Python));

        // The lock file records the project's own version; CI's `uv sync --locked` fails when it is stale.
        var lockFile = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "python", "uv.lock")).ReplaceLineEndings("\n");
        var locked = Regex.Match(lockFile, "\n\\[\\[package\\]\\]\nname = \"agentexperience\"\nversion = \"(?<v>[^\"]+)\"\n", RegexOptions.CultureInvariant);
        Assert.True(locked.Success, "python/uv.lock has no agentexperience package.");
        Assert.True(
            locked.Groups["v"].Value == expected.Python,
            $"python/uv.lock records agentexperience {locked.Groups["v"].Value}, but Directory.Build.props versions this commit as {expected.DotNet} (expected {expected.Python}).");
    }

    [Fact]
    public void Every_Go_module_requires_its_siblings_at_the_props_version_and_builds_them_from_the_tree()
    {
        var expected = ReleaseVersions.FromProps();

        var violations = GoModules.SelectMany(module => GoViolations(
                $"{module.Key}/go.mod",
                File.ReadAllText(Path.Combine(RepositoryRoot.Path, module.Key, "go.mod")),
                expected.Go,
                module.Value))
            .ToList();

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>The checks are proven to bite on the I/O matrix's drift cases.</summary>
    [Fact]
    public void The_checks_catch_a_drifted_pyproject_and_a_pseudo_version()
    {
        var pyproject = "[project]\nname = \"agentexperience\"\nversion = \"0.1.0.dev0\"\n";
        var python = Assert.Single(PythonViolations(pyproject, "0.1.0a14"));
        Assert.Contains("python/pyproject.toml", python, StringComparison.Ordinal);
        Assert.Contains("0.1.0a14", python, StringComparison.Ordinal);

        Assert.NotEmpty(PythonViolations("[project]\nname = \"agentexperience-preview\"\nversion = \"0.1.0a14\"\n", "0.1.0a14"));

        var goMod = $"module {ModulePrefix}/inmemory\n\ngo 1.25.0\n\nrequire {ModulePrefix} v0.0.0-00010101000000-000000000000\n\nreplace {ModulePrefix} => ../\n";
        var go = Assert.Single(GoViolations("go/inmemory/go.mod", goMod, "v0.1.0-preview.14", ["go"]));
        Assert.Contains("go/inmemory/go.mod", go, StringComparison.Ordinal);
        Assert.Contains("v0.1.0-preview.14", go, StringComparison.Ordinal);

        // A requirement dropped, or the replace that keeps in-repository builds on the tree.
        Assert.NotEmpty(GoViolations("go/inmemory/go.mod", $"module {ModulePrefix}/inmemory\n\nreplace {ModulePrefix} => ../\n", "v0.1.0-preview.14", ["go"]));
        Assert.NotEmpty(GoViolations("go/inmemory/go.mod", $"module {ModulePrefix}/inmemory\n\nrequire {ModulePrefix} v0.1.0-preview.14\n", "v0.1.0-preview.14", ["go"]));
        Assert.Empty(GoViolations("go/inmemory/go.mod", goMod.Replace("v0.0.0-00010101000000-000000000000", "v0.1.0-preview.14", StringComparison.Ordinal), "v0.1.0-preview.14", ["go"]));
    }

    private static List<string> PythonViolations(string pyproject, string expected)
    {
        var violations = new List<string>();
        var name = Regex.Match(pyproject, "^name = \"(?<v>[^\"]+)\"$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!name.Success || name.Groups["v"].Value != "agentexperience")
        {
            violations.Add("python/pyproject.toml must name the distribution agentexperience.");
        }

        var version = Regex.Match(pyproject, "^version = \"(?<v>[^\"]+)\"$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!version.Success || version.Groups["v"].Value != expected)
        {
            violations.Add($"python/pyproject.toml's version is '{(version.Success ? version.Groups["v"].Value : "(none)")}', expected {expected} (the version Directory.Build.props sets).");
        }

        return violations;
    }

    /// <summary>
    /// What is wrong with one module's <c>go.mod</c>: a requirement on another module of this repository at any version
    /// but <paramref name="expected"/>, a sibling it should require and does not, or one without a <c>replace</c> to its
    /// directory (so builds in the repository use the tree; consumers ignore it).
    /// </summary>
    private static List<string> GoViolations(string file, string goMod, string expected, string[] siblings)
    {
        var violations = new List<string>();
        var requires = new Dictionary<string, string>(StringComparer.Ordinal);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        string? block = null;
        foreach (var raw in goMod.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Split("//")[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line is "require (" or "replace (")
            {
                block = line[..7];
                continue;
            }

            if (line == ")")
            {
                block = null;
                continue;
            }

            var directive = block;
            if (line.StartsWith("require ", StringComparison.Ordinal) || line.StartsWith("replace ", StringComparison.Ordinal))
            {
                directive = line[..7];
                line = line[8..].Trim();
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !IsSibling(parts[0]))
            {
                continue;
            }

            if (directive == "require" && parts.Length >= 2)
            {
                requires[parts[0]] = parts[1];
            }
            else if (directive == "replace" && line.Contains("=>", StringComparison.Ordinal))
            {
                replaced.Add(parts[0]);
            }
        }

        foreach (var (path, version) in requires.Where(pair => pair.Value != expected))
        {
            violations.Add($"{file} requires {path} {version}, expected {expected} (the version Directory.Build.props sets).");
        }

        foreach (var sibling in siblings.Select(directory => ModulePrefix + directory["go".Length..]))
        {
            if (!requires.ContainsKey(sibling))
            {
                violations.Add($"{file} does not require {sibling} {expected}.");
            }

            if (!replaced.Contains(sibling))
            {
                violations.Add($"{file} has no replace for {sibling}: builds in this repository must use its directory.");
            }
        }

        foreach (var extra in requires.Keys.Where(path => !siblings.Select(directory => ModulePrefix + directory["go".Length..]).Contains(path, StringComparer.Ordinal)))
        {
            violations.Add($"{file} requires {extra}, which the release tests do not expect; add it to {nameof(GoModules)}.");
        }

        return violations;
    }

    private static bool IsSibling(string path) =>
        path == ModulePrefix || path.StartsWith(ModulePrefix + "/", StringComparison.Ordinal);

    /// <summary>Runs <c>derive_versions</c>, as <c>release.yml</c> defines it, in bash.</summary>
    private static (int ExitCode, string Output) RunDeriveVersions(string prefix, string suffix)
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", "release.yml")).ReplaceLineEndings("\n");
        var function = Regex.Match(workflow, @"# derive-versions:begin\n(?<body>.*?)\n\s*# derive-versions:end\n", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(function.Success, "release.yml has no derive-versions:begin ... derive-versions:end block.");

        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(function.Groups["body"].Value + "\nderive_versions \"$1\" \"$2\"");
        start.ArgumentList.Add("derive-versions");
        start.ArgumentList.Add(prefix);
        start.ArgumentList.Add(suffix);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("bash did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = stderr.Result;
        return (process.ExitCode, output);
    }
}
