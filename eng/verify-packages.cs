#!/usr/bin/env dotnet
// Release verification: asserts what `dotnet pack` actually produced, read from the built .nupkg and
// .snupkg files -- never from a csproj, because a csproj says what was intended and a package is what
// ships. Run after packing (RELEASING.md step 7, and CI's pack job):
//
//     dotnet pack -c Release -o artifacts/packages -p:AgentExperienceReleaseBuild=true
//     dotnet run eng/verify-packages.cs -- artifacts/packages
//
// Exit code 0 when every check passes; 1 with every failure listed otherwise. It checks, per package:
//   * exactly the five expected package ids, each with one .nupkg and one .snupkg, all at one version
//   * the version is 0.1.0-preview.N -- never 1.0.0, never a bare 0.x (story 4.3, frozen rule 2)
//   * license expression, readme (declared and present in the package), tags, project and repository URL,
//     and the repository commit SourceLink stamped
//   * description and release notes say "Preview"
//   * the dependency set is EXACTLY the expected one, id and version range -- so Abstractions and Core can
//     never gain an adapter dependency (MAF, Npgsql, DbUp, Pgvector, a model provider, OpenTelemetry)
//     without this list being edited in review
//   * all five packages carry one repository commit, equal to `git rev-parse HEAD` when git is available
//   * the .snupkg holds a portable PDB whose id matches the assembly's CodeView debug entry, every
//     SourceLink target points at the repository, every document path is deterministic-mapped (/_/), and the assembly is marked
//     reproducible (deterministic build)
#:property RestorePackagesWithLockFile=false
#:property TreatWarningsAsErrors=true
#:property GenerateDocumentationFile=false

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: dotnet run eng/verify-packages.cs -- <directory containing the .nupkg and .snupkg files>");
    return 2;
}

const string RepositoryUrl = "https://github.com/fabbrik/AgentExperience.NET";
var versionPattern = new Regex(@"^0\.1\.0-preview\.[1-9][0-9]*$", RegexOptions.CultureInvariant);

// The exact dependency set each package may declare. Editing this is a deliberate, reviewed act; a
// forbidden adapter dependency showing up in Abstractions or Core fails here, from the built nuspec.
// "{self}" is replaced by the version being verified: sibling packages always move in lockstep.
var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
{
    ["AgentExperience.Abstractions"] = [],
    ["AgentExperience.Core"] =
    [
        "AgentExperience.Abstractions {self}",
        "Microsoft.Extensions.Compliance.Redaction [10.10.0]",
        "Microsoft.Extensions.DependencyInjection.Abstractions [10.0.12]",
    ],
    ["AgentExperience.MicrosoftAgentFramework"] =
    [
        "AgentExperience.Core {self}",
        "Microsoft.Agents.AI [1.22.0]",
    ],
    ["AgentExperience.Storage.Postgres"] =
    [
        "AgentExperience.Abstractions {self}",
        "Microsoft.Extensions.DependencyInjection.Abstractions [10.0.12]",
        "Npgsql [10.0.3]",
        "dbup-core [6.1.1]",
        "dbup-postgresql [7.0.1]",
    ],
    ["AgentExperience.Storage.Postgres.Vectors"] =
    [
        "AgentExperience.Storage.Postgres {self}",
        "Microsoft.Extensions.AI.Abstractions [10.10.0]",
        "Microsoft.Extensions.DependencyInjection.Abstractions [10.0.12]",
        "Npgsql [10.0.3]",
        "Pgvector [0.3.2]",
    ],
};

// Belt and braces for the two adapter-independent packages: even if someone edits the table above, these
// substrings may never appear in their dependency ids.
string[] forbiddenInCore =
[
    "Microsoft.Agents", "Microsoft.EntityFrameworkCore", "Npgsql", "dbup", "Pgvector", "OpenTelemetry",
    "Microsoft.Extensions.AI", "Microsoft.SemanticKernel", "OpenAI", "Azure.AI", "Anthropic",
];

var failures = new List<string>();
void Fail(string package, string message) => failures.Add($"{package}: {message}");

var directory = Path.GetFullPath(args[0]);
var nupkgs = Directory.GetFiles(directory, "*.nupkg").Order(StringComparer.Ordinal).ToArray();
var snupkgs = Directory.GetFiles(directory, "*.snupkg").Order(StringComparer.Ordinal).ToArray();

if (nupkgs.Length != expected.Count || snupkgs.Length != expected.Count)
{
    failures.Add($"expected {expected.Count} .nupkg and {expected.Count} .snupkg files, found {nupkgs.Length} and {snupkgs.Length}");
}

var versions = new HashSet<string>(StringComparer.Ordinal);
var commits = new HashSet<string>(StringComparer.Ordinal);

foreach (var nupkgPath in nupkgs)
{
    using var nupkg = ZipFile.OpenRead(nupkgPath);
    var nuspecEntry = nupkg.Entries.SingleOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
    if (nuspecEntry is null)
    {
        failures.Add($"{Path.GetFileName(nupkgPath)}: no nuspec");
        continue;
    }

    XDocument nuspec;
    using (var stream = nuspecEntry.Open())
    {
        nuspec = XDocument.Load(stream);
    }

    var ns = nuspec.Root!.Name.Namespace;
    var metadata = nuspec.Root.Element(ns + "metadata")!;
    string? Meta(string name) => metadata.Element(ns + name)?.Value;

    var id = Meta("id") ?? "(no id)";
    var version = Meta("version") ?? "(no version)";
    versions.Add(version);

    if (!expected.TryGetValue(id, out var expectedDependencies))
    {
        Fail(id, "is not one of the five packages this repository ships");
        continue;
    }

    if (!string.Equals(Path.GetFileName(nupkgPath), $"{id}.{version}.nupkg", StringComparison.Ordinal))
    {
        Fail(id, $"file name '{Path.GetFileName(nupkgPath)}' does not match id and version");
    }

    if (!versionPattern.IsMatch(version))
    {
        Fail(id, $"version '{version}' is not 0.1.0-preview.N");
    }

    var license = metadata.Element(ns + "license");
    if (license?.Attribute("type")?.Value != "expression" || license.Value != "Apache-2.0")
    {
        Fail(id, "license is not the Apache-2.0 expression");
    }

    var readme = Meta("readme");
    if (string.IsNullOrEmpty(readme))
    {
        Fail(id, "declares no readme");
    }
    else if (nupkg.GetEntry(readme) is not { Length: > 200 })
    {
        Fail(id, $"readme '{readme}' is missing from the package or is too short to be one");
    }
    else
    {
        using var reader = new StreamReader(nupkg.GetEntry(readme)!.Open());
        if (!reader.ReadToEnd().Contains("Preview", StringComparison.Ordinal))
        {
            Fail(id, "readme does not say the package is a preview");
        }
    }

    if (string.IsNullOrWhiteSpace(Meta("tags")))
    {
        Fail(id, "declares no tags");
    }

    if (Meta("projectUrl") != RepositoryUrl)
    {
        Fail(id, $"projectUrl is '{Meta("projectUrl")}'");
    }

    var repository = metadata.Element(ns + "repository");
    if (repository?.Attribute("type")?.Value != "git" || repository.Attribute("url")?.Value != RepositoryUrl)
    {
        Fail(id, "repository element is missing or does not name the git repository");
    }
    else if (!Regex.IsMatch(repository.Attribute("commit")?.Value ?? string.Empty, "^[0-9a-f]{40}$"))
    {
        Fail(id, "repository element carries no commit, so SourceLink did not run");
    }
    else
    {
        commits.Add(repository.Attribute("commit")!.Value);
    }

    if (!(Meta("description") ?? string.Empty).StartsWith("Preview", StringComparison.Ordinal))
    {
        Fail(id, "description does not start with 'Preview'");
    }

    if (!(Meta("releaseNotes") ?? string.Empty).Contains("Preview", StringComparison.Ordinal))
    {
        Fail(id, "release notes do not say 'Preview'");
    }

    var dependencies = metadata.Element(ns + "dependencies");
    var groups = dependencies?.Elements(ns + "group").ToList() ?? [];
    if (groups.Any(g => g.Attribute("targetFramework")?.Value != "net10.0"))
    {
        Fail(id, "declares a dependency group for a target framework other than net10.0");
    }

    // Both shapes: dependencies inside a <group>, and legacy ones directly under <dependencies>.
    var declared = groups
        .SelectMany(g => g.Elements(ns + "dependency"))
        .Concat(dependencies?.Elements(ns + "dependency") ?? [])
        .Select(d => $"{d.Attribute("id")?.Value} {d.Attribute("version")?.Value}")
        .Order(StringComparer.Ordinal)
        .ToList();
    var wanted = expectedDependencies
        .Select(d => d.Replace("{self}", version, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    if (!declared.SequenceEqual(wanted, StringComparer.Ordinal))
    {
        Fail(id, $"dependencies are [{string.Join(", ", declared)}], expected [{string.Join(", ", wanted)}]");
    }

    if (id is "AgentExperience.Abstractions" or "AgentExperience.Core")
    {
        foreach (var dependency in declared)
        {
            foreach (var forbidden in forbiddenInCore)
            {
                if (dependency.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    Fail(id, $"depends on '{dependency}', which matches forbidden adapter dependency '{forbidden}'");
                }
            }
        }
    }

    var assemblyEntry = nupkg.GetEntry($"lib/net10.0/{id}.dll");
    if (assemblyEntry is null)
    {
        Fail(id, $"has no lib/net10.0/{id}.dll");
        continue;
    }

    if (nupkg.GetEntry($"lib/net10.0/{id}.xml") is null)
    {
        Fail(id, "ships no XML documentation file");
    }

    BlobContentId? codeViewId = null;
    using (var assemblyBytes = new MemoryStream())
    {
        using (var s = assemblyEntry.Open())
        {
            s.CopyTo(assemblyBytes);
        }

        assemblyBytes.Position = 0;
        using var pe = new PEReader(assemblyBytes);
        var debugDirectory = pe.ReadDebugDirectory();
        if (!debugDirectory.Any(entry => entry.Type == DebugDirectoryEntryType.Reproducible))
        {
            Fail(id, "assembly is not marked reproducible, so the build was not deterministic");
        }

        var codeView = debugDirectory.Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToList();
        if (codeView.Count != 1)
        {
            Fail(id, $"assembly has {codeView.Count} CodeView debug entries, expected exactly one");
        }
        else
        {
            var data = pe.ReadCodeViewDebugDirectoryData(codeView[0]);
            codeViewId = new BlobContentId(data.Guid, codeView[0].Stamp);
        }

        var reader = pe.GetMetadataReader();
        var informational = reader.GetAssemblyDefinition().GetCustomAttributes()
            .Select(reader.GetCustomAttribute)
            .Where(a => a.Constructor.Kind == HandleKind.MemberReference)
            .Select(a => (Attribute: a, Type: reader.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent))
            .Where(t => t.Type.Kind == HandleKind.TypeReference
                && reader.GetString(reader.GetTypeReference((TypeReferenceHandle)t.Type).Name) == "AssemblyInformationalVersionAttribute")
            .Select(t => reader.GetBlobReader(t.Attribute.Value))
            .Select(blob =>
            {
                blob.ReadUInt16(); // prolog
                return blob.ReadSerializedString();
            })
            .SingleOrDefault();

        if (informational is null || !informational.StartsWith(version + "+", StringComparison.Ordinal))
        {
            Fail(id, $"assembly informational version '{informational}' is not '{version}+<commit>'");
        }
    }

    var snupkgPath = Path.Combine(directory, $"{id}.{version}.snupkg");
    if (!File.Exists(snupkgPath))
    {
        Fail(id, "has no matching .snupkg at the same version");
        continue;
    }

    using var snupkg = ZipFile.OpenRead(snupkgPath);
    var pdbEntry = snupkg.GetEntry($"lib/net10.0/{id}.pdb");
    if (pdbEntry is null)
    {
        Fail(id, "symbol package has no portable PDB for the assembly");
        continue;
    }

    using var pdbBytes = new MemoryStream();
    using (var s = pdbEntry.Open())
    {
        s.CopyTo(pdbBytes);
    }

    pdbBytes.Position = 0;
    using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbBytes);
    var pdb = provider.GetMetadataReader();

    // The symbol package must hold the PDB of *this* assembly, not merely a PDB with the right name.
    var pdbId = new BlobContentId(pdb.DebugMetadataHeader!.Id);
    if (codeViewId is not { } expectedPdbId || pdbId != expectedPdbId)
    {
        Fail(id, $"PDB id {pdbId.Guid}/{pdbId.Stamp:X8} does not match the assembly's CodeView entry {codeViewId?.Guid}/{codeViewId?.Stamp:X8}");
    }

    var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    string? sourceLink = null;
    foreach (var handle in pdb.CustomDebugInformation)
    {
        var info = pdb.GetCustomDebugInformation(handle);
        if (pdb.GetGuid(info.Kind) == sourceLinkKind)
        {
            sourceLink = System.Text.Encoding.UTF8.GetString(pdb.GetBlobBytes(info.Value));
        }
    }

    if (sourceLink is null)
    {
        Fail(id, "PDB carries no SourceLink");
    }
    else
    {
        using var json = JsonDocument.Parse(sourceLink);
        var targets = json.RootElement.GetProperty("documents").EnumerateObject().Select(p => p.Value.GetString() ?? string.Empty).ToList();
        if (targets.Count == 0 || !targets.All(t => t.StartsWith("https://raw.githubusercontent.com/fabbrik/AgentExperience.NET/", StringComparison.Ordinal)))
        {
            Fail(id, $"SourceLink does not point at the repository: [{string.Join(", ", targets)}]");
        }
    }

    var unmapped = pdb.Documents
        .Select(d => pdb.GetString(pdb.GetDocument(d).Name))
        .Where(name => !name.StartsWith("/_/", StringComparison.Ordinal))
        .ToList();
    if (unmapped.Count > 0)
    {
        Fail(id, $"{unmapped.Count} PDB document path(s) are not deterministic-mapped to /_/ (build with -p:AgentExperienceReleaseBuild=true), e.g. '{unmapped[0]}'");
    }
}

foreach (var id in expected.Keys)
{
    if (!nupkgs.Any(p => Path.GetFileName(p).StartsWith(id + ".0", StringComparison.Ordinal)))
    {
        failures.Add($"{id}: no package was produced");
    }
}

// One commit for all five packages, and -- when git is available -- the commit checked out here.
if (commits.Count > 1)
{
    failures.Add($"packages were built from different commits: {string.Join(", ", commits.Order(StringComparer.Ordinal))}");
}
else if (commits.Count == 1)
{
    var head = GitHead();
    if (head is not null && !string.Equals(head, commits.Single(), StringComparison.Ordinal))
    {
        failures.Add($"packages were built from commit {commits.Single()}, but HEAD is {head}");
    }
    else if (head is null)
    {
        Console.WriteLine($"git is not available; not comparing the packages' commit {commits.Single()} with HEAD.");
    }
}

if (versions.Count > 1)
{
    failures.Add($"packages disagree on version: {string.Join(", ", versions.Order(StringComparer.Ordinal))}");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Package verification FAILED ({failures.Count}):");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    return 1;
}

Console.WriteLine($"Package verification passed: {nupkgs.Length} packages and {snupkgs.Length} symbol packages at {string.Join(", ", versions)}, in {directory}.");
return 0;

static string? GitHead()
{
    try
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (git is null)
        {
            return null;
        }

        var output = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return git.ExitCode == 0 && Regex.IsMatch(output, "^[0-9a-f]{40}$") ? output : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}
