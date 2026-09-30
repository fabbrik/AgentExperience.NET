using System.Runtime.CompilerServices;

namespace AgentExperience.Upgrade.Tests;

/// <summary>
/// One published preview whose databases this suite upgrades, and the seeder that creates them. Adding a preview that
/// ships a migration means a seeder project under <c>tests/AgentExperience.Upgrade.Seeders</c> and an entry here (see
/// README.md, "Adding a preview").
/// </summary>
/// <param name="Version">The published version the seeder pins, exactly.</param>
/// <param name="SeederProject">The seeder's project directory, relative to <c>tests/AgentExperience.Upgrade.Seeders</c>.</param>
/// <param name="TwoRoles">
/// Whether that preview deployed with two roles. <see langword="false"/> is <c>0.1.0-preview.1</c>'s single role, which the
/// CHANGELOG's <c>0.1.0-preview.2</c> runbook moves to two roles before the new migrator runs.
/// </param>
/// <param name="LastScript">
/// The last base-package script that preview's migrator applies: where its schema ends. Today's migrators must apply
/// exactly the scripts that preview did not.
/// </param>
/// <param name="SourceCommit">
/// The commit the published packages were built from (<c>git rev-list -n1 v&lt;version&gt;</c>), which every assembly
/// carries in its informational version: the proof that the seeder ran the published packages.
/// </param>
/// <param name="SupportsEncryption">Whether that preview has crypto-shredding, so the suite also seeds it encrypted.</param>
public sealed record PublishedPreview(string Version, string SeederProject, bool TwoRoles, string LastScript, string SourceCommit, bool SupportsEncryption)
{
    /// <summary><c>0.1.0-preview.1</c>: schema <c>0001</c> to <c>0010</c> (and <c>0004</c>), one role, no crypto-shredding.</summary>
    public static PublishedPreview Preview1 { get; } = new(
        "0.1.0-preview.1",
        "Preview1",
        TwoRoles: false,
        "0010_delete_and_expire.sql",
        SourceCommit: "91181f9988e01dd920ace470e360931c6f092795",
        SupportsEncryption: false);

    /// <summary>
    /// <c>0.1.0-preview.2</c>: schema <c>0001</c> to <c>0018</c>, two roles, crypto-shredding. <c>0.1.0-preview.3</c>
    /// and <c>0.1.0-preview.4</c> added no migration, so this is their schema too.
    /// </summary>
    public static PublishedPreview Preview2 { get; } = new(
        "0.1.0-preview.2",
        "Preview2",
        TwoRoles: true,
        "0018_evidence_admission.sql",
        SourceCommit: "50aa5de466815ca3fdb3378caab3b19c7156b3ba",
        SupportsEncryption: true);

    /// <summary>Every preview the suite covers.</summary>
    public static IReadOnlyList<PublishedPreview> All { get; } = [Preview1, Preview2];

    public static PublishedPreview Named(string version) => All.Single(preview => preview.Version == version);

    /// <summary>The seeder's project file.</summary>
    public string SeederProjectFile =>
        Path.Combine(RepositoryRoot.Path, "tests", "AgentExperience.Upgrade.Seeders", SeederProject, $"AgentExperience.Upgrade.Seeders.{SeederProject}.csproj");

    public override string ToString() => Version;
}

/// <summary>The repository root, located from this file's own compile-time path.</summary>
internal static class RepositoryRoot
{
    internal static string Path { get; } = Locate();

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "..", ".."));
        if (!File.Exists(System.IO.Path.Combine(root, "AgentExperience.NET.sln")))
        {
            throw new InvalidOperationException($"'{root}' is not the repository root: no AgentExperience.NET.sln there.");
        }

        return root;
    }
}
