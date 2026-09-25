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
//   * the dependency set is EXACTLY the expected one, id and version range, in the dependency group of EVERY
//     supported target framework -- so Abstractions and Core can never gain an adapter dependency (MAF,
//     Npgsql, DbUp, Pgvector, a model provider, OpenTelemetry) without this list being edited in review --
//     plus, in one framework's group only, the framework-only dependencies listed for it (story 7.2: the
//     net8.0 System.Text.Json and Microsoft.Bcl.Memory floors), and nothing else
//   * lib/ holds exactly the supported target frameworks (story 6.3), and each one below is checked on its own
//   * all five packages carry one repository commit, equal to `git rev-parse HEAD` when git is available
//   * per framework, the .snupkg holds a portable PDB whose id matches the assembly's CodeView debug entry, every
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

// The supported target frameworks: AgentExperienceTargetFrameworks in Directory.Build.props. Kept as a literal
// here so the check reads what shipped against what was intended, rather than against the same build input;
// a release test fails if the two lists ever disagree.
string[] frameworks = ["net8.0", "net9.0", "net10.0"];

// The exact dependency set each package may declare. Editing this is a deliberate, reviewed act; a
// forbidden adapter dependency showing up in Abstractions or Core fails here, from the built nuspec.
// "{self}" is replaced by the version being verified: sibling packages always move in lockstep. A bare version
// is NuGet's floor (">= x.y.z"), which is every third-party reference except Microsoft.Agents.AI since story 6.3
// (KL-13); "[x.y.z, N.0.0)" is a range bounded at the next major, which is Microsoft.Agents.AI since story 7.2
// (it was exact, "[x.y.z]", before). The version string is compared as the nuspec writes it.
var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
{
    ["AgentExperience.Abstractions"] = [],
    ["AgentExperience.Core"] =
    [
        "AgentExperience.Abstractions {self}",
        "Microsoft.Extensions.Compliance.Redaction 10.10.0",
        "Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12",
    ],
    ["AgentExperience.MicrosoftAgentFramework"] =
    [
        "AgentExperience.Core {self}",
        "Microsoft.Agents.AI [1.22.0, 2.0.0)",
    ],
    ["AgentExperience.Storage.Postgres"] =
    [
        "AgentExperience.Abstractions {self}",
        "Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12",
        "Npgsql 10.0.3",
        "dbup-core 6.1.1",
        "dbup-postgresql 7.0.1",
    ],
    ["AgentExperience.Storage.Postgres.Vectors"] =
    [
        "AgentExperience.Storage.Postgres {self}",
        "Microsoft.Extensions.AI.Abstractions 10.10.0",
        "Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12",
        "Npgsql 10.0.3",
        "Pgvector 0.3.2",
    ],
};

// Dependencies declared for one target framework only, added to that framework's group (and only that one) on top
// of the set above. Story 7.2 (KL-13): the .NET 8 shared framework lacks System.Text.Json APIs (JsonElement.DeepEquals,
// RespectNullableAnnotations, RespectRequiredConstructorParameters) and Base64Url, so net8.0 takes them from the
// .NET 10 train's packages, floors like every other reference. net9.0 and net10.0 get them from the shared framework.
var frameworkOnly = new Dictionary<(string Package, string Framework), string[]>
{
    [("AgentExperience.Core", "net8.0")] =
    [
        "Microsoft.Bcl.Memory 10.0.12",
        "System.Text.Json 10.0.12",
    ],
    [("AgentExperience.Storage.Postgres", "net8.0")] =
    [
        "System.Text.Json 10.0.12",
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

foreach (var ((package, framework), _) in frameworkOnly)
{
    if (!expected.ContainsKey(package) || !frameworks.Contains(framework))
    {
        failures.Add($"the framework-only dependency list names {package} ({framework}), which is not a shipped package and framework");
    }
    else if (frameworkOnly[(package, framework)].Select(d => d.Split(' ')[0]).Intersect(expected[package].Select(d => d.Split(' ')[0]), StringComparer.Ordinal).Any())
    {
        failures.Add($"the framework-only dependency list for {package} ({framework}) repeats a dependency every framework already has");
    }
}

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
    var groupFrameworks = groups.Select(g => g.Attribute("targetFramework")?.Value ?? "(none)").Order(StringComparer.Ordinal).ToList();
    if (!groupFrameworks.SequenceEqual(frameworks.Order(StringComparer.Ordinal), StringComparer.Ordinal))
    {
        Fail(id, $"declares dependency groups for [{string.Join(", ", groupFrameworks)}], expected exactly [{string.Join(", ", frameworks)}]");
    }

    if (dependencies?.Elements(ns + "dependency").Any() == true)
    {
        Fail(id, "declares dependencies outside a target-framework group, which would apply to every framework");
    }

    var wanted = expectedDependencies
        .Select(d => d.Replace("{self}", version, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    // Every framework's group must hold exactly the expected set: one framework quietly gaining (or losing) a
    // dependency is as much a change as all of them doing so.
    var declared = new List<string>();
    foreach (var group in groups)
    {
        var inGroup = group.Elements(ns + "dependency")
            .Select(d => $"{d.Attribute("id")?.Value} {d.Attribute("version")?.Value}")
            .Order(StringComparer.Ordinal)
            .ToList();
        declared.AddRange(inGroup);

        var groupFramework = group.Attribute("targetFramework")?.Value ?? "(none)";
        var wantedHere = wanted
            .Concat(frameworkOnly.GetValueOrDefault((id, groupFramework)) ?? [])
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!inGroup.SequenceEqual(wantedHere, StringComparer.Ordinal))
        {
            Fail(id, $"{groupFramework} dependencies are [{string.Join(", ", inGroup)}], expected [{string.Join(", ", wantedHere)}]");
        }
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

    // Exactly the supported frameworks under lib/, nothing more and nothing less.
    var libFrameworks = nupkg.Entries
        .Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 2)
        .Select(e => e.FullName.Split('/')[1])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();
    if (!libFrameworks.SequenceEqual(frameworks.Order(StringComparer.Ordinal), StringComparer.Ordinal))
    {
        Fail(id, $"ships lib/ folders for [{string.Join(", ", libFrameworks)}], expected exactly [{string.Join(", ", frameworks)}]");
    }

    var snupkgPath = Path.Combine(directory, $"{id}.{version}.snupkg");
    // A missing .snupkg fails the package, but the assembly checks below still run, so one missing file
    // does not hide what else is wrong with the .nupkg; only the PDB checks are skipped.
    using var snupkg = File.Exists(snupkgPath) ? ZipFile.OpenRead(snupkgPath) : null;
    if (snupkg is null)
    {
        Fail(id, "has no matching .snupkg at the same version");
    }
    else
    {
        var symbolFrameworks = snupkg.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 2)
            .Select(e => e.FullName.Split('/')[1])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!symbolFrameworks.SequenceEqual(frameworks.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            Fail(id, $"symbol package has lib/ folders for [{string.Join(", ", symbolFrameworks)}], expected exactly [{string.Join(", ", frameworks)}]");
        }
    }

    foreach (var framework in frameworks)
    {
        VerifyFramework(id, version, framework, nupkg, snupkg);
    }
}

// The assembly, its documentation, and its symbols, for one target framework of one package.
void VerifyFramework(string id, string version, string framework, ZipArchive nupkg, ZipArchive? snupkg)
{
    var tag = $"{id} ({framework})";
    var assemblyEntry = nupkg.GetEntry($"lib/{framework}/{id}.dll");
    if (assemblyEntry is null)
    {
        Fail(tag, $"has no lib/{framework}/{id}.dll");
        return;
    }

    if (nupkg.GetEntry($"lib/{framework}/{id}.xml") is null)
    {
        Fail(tag, "ships no XML documentation file");
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
            Fail(tag, "assembly is not marked reproducible, so the build was not deterministic");
        }

        var codeView = debugDirectory.Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToList();
        if (codeView.Count != 1)
        {
            Fail(tag, $"assembly has {codeView.Count} CodeView debug entries, expected exactly one");
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
            Fail(tag, $"assembly informational version '{informational}' is not '{version}+<commit>'");
        }
    }

    if (snupkg is null)
    {
        return; // already reported; nothing to compare the assembly's CodeView entry against
    }

    var pdbEntry = snupkg.GetEntry($"lib/{framework}/{id}.pdb");
    if (pdbEntry is null)
    {
        Fail(tag, "symbol package has no portable PDB for the assembly");
        return;
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
        Fail(tag, $"PDB id {pdbId.Guid}/{pdbId.Stamp:X8} does not match the assembly's CodeView entry {codeViewId?.Guid}/{codeViewId?.Stamp:X8}");
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
        Fail(tag, "PDB carries no SourceLink");
    }
    else
    {
        using var json = JsonDocument.Parse(sourceLink);
        var targets = json.RootElement.GetProperty("documents").EnumerateObject().Select(p => p.Value.GetString() ?? string.Empty).ToList();
        if (targets.Count == 0 || !targets.All(t => t.StartsWith("https://raw.githubusercontent.com/fabbrik/AgentExperience.NET/", StringComparison.Ordinal)))
        {
            Fail(tag, $"SourceLink does not point at the repository: [{string.Join(", ", targets)}]");
        }
    }

    var unmapped = pdb.Documents
        .Select(d => pdb.GetString(pdb.GetDocument(d).Name))
        .Where(name => !name.StartsWith("/_/", StringComparison.Ordinal))
        .ToList();
    if (unmapped.Count > 0)
    {
        Fail(tag, $"{unmapped.Count} PDB document path(s) are not deterministic-mapped to /_/ (build with -p:AgentExperienceReleaseBuild=true), e.g. '{unmapped[0]}'");
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
