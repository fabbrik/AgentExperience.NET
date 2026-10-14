using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Versioning;

/// <summary>
/// The one version a release carries, in each language's spelling. <c>Directory.Build.props</c> sets it once
/// (<c>VersionPrefix</c> and <c>VersionSuffix</c>); the Python distribution and the Go modules derive theirs from it.
/// This is the rule's one copy in the tests; <c>release.yml</c>'s <c>derive_versions</c> is its one copy in the
/// workflow, and <see cref="VersionAgreementTests"/> runs that function and holds the two to each other.
/// </summary>
/// <param name="DotNet">The NuGet version, and the tag without its <c>v</c>: <c>P-preview.N</c>, or <c>P</c>.</param>
/// <param name="Python">The PEP 440 version: <c>PaN</c> (an explicit alpha, since PEP 440 normalises "preview" to
/// <c>rc</c>), or <c>P</c>.</param>
/// <param name="Go">The Go module version of every module: <c>vP-preview.N</c>, or <c>vP</c>.</param>
internal sealed partial record ReleaseVersions(string DotNet, string Python, string Go)
{
    /// <summary>The versions derived from a <c>VersionPrefix</c> and a <c>VersionSuffix</c> (empty for none).</summary>
    internal static ReleaseVersions Derive(string prefix, string suffix)
    {
        if (!Prefix().IsMatch(prefix))
        {
            throw new ArgumentException($"VersionPrefix '{prefix}' is not MAJOR.MINOR.PATCH.", nameof(prefix));
        }

        if (suffix.Length == 0)
        {
            return new ReleaseVersions(prefix, prefix, $"v{prefix}");
        }

        var preview = Preview().Match(suffix);
        if (!preview.Success)
        {
            throw new ArgumentException($"VersionSuffix '{suffix}' is neither empty nor preview.N.", nameof(suffix));
        }

        // The digits as written, never parsed: the workflow's copy works on the text too, so the two agree on any number.
        return new ReleaseVersions($"{prefix}-{suffix}", $"{prefix}a{preview.Groups["n"].Value}", $"v{prefix}-{suffix}");
    }

    /// <summary>The versions <c>Directory.Build.props</c> sets for this commit.</summary>
    internal static ReleaseVersions FromProps()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "Directory.Build.props"));
        var prefix = Regex.Match(props, "<VersionPrefix>(?<v>[^<]+)</VersionPrefix>", RegexOptions.CultureInvariant);
        Assert.True(prefix.Success, "Directory.Build.props does not set VersionPrefix.");
        var suffix = Regex.Match(props, "<VersionSuffix>(?<v>[^<]+)</VersionSuffix>", RegexOptions.CultureInvariant);
        return Derive(prefix.Groups["v"].Value, suffix.Success ? suffix.Groups["v"].Value : string.Empty);
    }

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^preview\.(?<n>[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Preview();
}
