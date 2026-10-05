using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Compatibility;

/// <summary>
/// Package validation compares every shipping package with the last published preview when it is packed
/// (<c>Directory.Build.props</c>). These tests hold that baseline to the CHANGELOG, so it cannot silently point at a
/// release that never shipped, at one newer than the build, or at an old one that would let a break made since the
/// last release through; and they keep every declared break next to a labelled CHANGELOG entry.
/// </summary>
public sealed class PackageValidationBaselineTests
{
    private static readonly string Root = RepositoryRoot.Path;

    private static readonly Regex PreviewVersion = new(@"^0\.1\.0-preview\.(?<n>[1-9][0-9]*)$", RegexOptions.CultureInvariant);

    [Fact]
    public void Package_validation_is_on_for_the_shipping_projects_against_the_baseline_property()
    {
        var props = Props();

        var group = Regex.Match(
            props,
            @"<PropertyGroup Condition=""'\$\(AgentExperienceShippingProject\)' == 'true'"">(?<body>(?:(?!</PropertyGroup>).)*)</PropertyGroup>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        Assert.True(group.Success, "Directory.Build.props has no property group for the shipping projects.");

        var body = group.Groups["body"].Value;
        Assert.Contains("<EnablePackageValidation>true</EnablePackageValidation>", body, StringComparison.Ordinal);
        Assert.Contains(
            "<PackageValidationBaselineVersion>$(AgentExperiencePackageValidationBaseline)</PackageValidationBaselineVersion>",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PackageValidationBaselineVersion", props.Replace(group.Value, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_baseline_is_the_newest_release_no_later_than_the_current_version()
    {
        var baseline = Preview(Baseline(), "the package validation baseline");
        var current = Preview(CurrentVersion(), "the version in Directory.Build.props");
        var released = ReleasedPreviews();

        Assert.True(released.Contains(baseline), $"The baseline 0.1.0-preview.{baseline} has no CHANGELOG section: it was never released.");
        Assert.True(baseline <= current, $"The baseline 0.1.0-preview.{baseline} is later than the version being built, 0.1.0-preview.{current}.");

        // A release between the baseline and the version being built means the baseline was not moved after it, and
        // a break made since that release would be compared with the wrong package.
        var skipped = released.Where(n => n > baseline && n < current).ToList();
        Assert.True(
            skipped.Count == 0,
            $"0.1.0-preview.{string.Join(", 0.1.0-preview.", skipped)} shipped after the baseline 0.1.0-preview.{baseline}: move the baseline (RELEASING.md).");
    }

    [Fact]
    public void Breaks_are_declared_only_in_shipping_projects_and_are_labelled_in_the_changelog()
    {
        var suppressions = Directory
            .EnumerateFiles(Root, "CompatibilitySuppressions.xml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.All(suppressions, path => Assert.Matches(@"^src/AgentExperience\.[A-Za-z.]+/CompatibilitySuppressions\.xml$", path));

        if (suppressions.Count > 0)
        {
            // Everything above the baseline's section is what changed since the baseline shipped.
            var changelog = File.ReadAllText(Path.Combine(Root, "CHANGELOG.md")).ReplaceLineEndings("\n");
            var end = changelog.IndexOf($"\n## {Baseline()}\n", StringComparison.Ordinal);
            Assert.True(end >= 0, $"The CHANGELOG has no section for the baseline {Baseline()}.");
            var since = changelog[..end];
            // A bullet or heading that starts with the label, as breaks in the CHANGELOG are written ("- **Breaking.**",
            // "- **Breaking (binary).** ...", "### Breaking: ..."), not the word anywhere in prose. This checks that a
            // break is announced at all; that it is the right one is the review of the suppression file's diff.
            Assert.True(
                Regex.IsMatch(since, @"^(?: *- \*\*|#{3,} )Breaking\b", RegexOptions.Multiline | RegexOptions.CultureInvariant),
                $"{string.Join(", ", suppressions)} declare(s) a break against {Baseline()}, but no CHANGELOG bullet or heading since then starts with Breaking.");
        }
    }

    [Fact]
    public void No_project_or_targets_file_switches_the_gate_off_or_moves_its_baseline()
    {
        // Only Directory.Build.props sets these. DisablePackageBaselineValidation is the one exception, for a package's
        // first release (there is no published package to compare with yet); RELEASING.md's post-release step removes it.
        string[] overrides =
        [
            "EnablePackageValidation", "PackageValidationBaselineVersion", "PackageValidationBaselinePath",
            "PackageValidationBaselineName", "ApiCompatGenerateSuppressionFile", "ApiCompatSuppressionFile",
            "EnableStrictModeForBaselineValidation", "RunPackageValidationWithoutWarnings",
        ];

        var files = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Root, "Directory.Build.*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.targets", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.props", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path) && Path.GetFullPath(path) != Path.Combine(Root, "Directory.Build.props"))
            .Distinct()
            .ToList();
        Assert.NotEmpty(files);

        Assert.All(files, path =>
        {
            var text = File.ReadAllText(path);
            Assert.All(overrides, name => Assert.False(
                text.Contains($"<{name}", StringComparison.Ordinal),
                $"{Path.GetRelativePath(Root, path)} sets {name}; package validation is configured in Directory.Build.props only."));
        });
    }

    private static string Props() => File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

    private static string Baseline() => Property("AgentExperiencePackageValidationBaseline");

    private static string CurrentVersion() => $"{Property("VersionPrefix")}-{Property("VersionSuffix")}";

    private static string Property(string name)
    {
        var match = Regex.Match(Props(), $"<{name}>(?<value>[^<]+)</{name}>", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Directory.Build.props does not set {name}.");
        return match.Groups["value"].Value;
    }

    private static int Preview(string version, string what)
    {
        var match = PreviewVersion.Match(version);
        Assert.True(match.Success, $"{what} ({version}) is not a 0.1.0-preview.N version.");
        return int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HashSet<int> ReleasedPreviews() =>
        Regex.Matches(File.ReadAllText(Path.Combine(Root, "CHANGELOG.md")), @"^## 0\.1\.0-preview\.(?<n>[1-9][0-9]*)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(m => int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();

    private static bool IsBuildOutput(string path)
    {
        var parts = Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part is "bin" or "obj" or "artifacts" or ".git");
    }
}
