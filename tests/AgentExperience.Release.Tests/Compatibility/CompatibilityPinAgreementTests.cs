using System.Text.Json;
using System.Xml.Linq;

namespace AgentExperience.Release.Tests.Compatibility;

/// <summary>
/// Story 4.3, frozen rule 5: <c>tests/AgentExperience.CompatibilityProof</c> is the executable evidence
/// behind the shipping packages' pins, so it must resolve exactly the versions they pin. Before this
/// test the proof floated its <c>PackageReference</c> versions while the shipping projects exact-pinned
/// theirs, so the evidence could silently detach from what ships.
/// </summary>
/// <remarks>
/// Checked twice, because either alone can be fooled: the declared versions in the csproj files (what a
/// restore is asked for) and the resolved versions in the committed <c>packages.lock.json</c> files (what
/// a restore actually produced). Only packages a shipping project pins <em>directly</em> are compared;
/// transitive infrastructure (logging abstractions, for example) legitimately resolves higher in a test
/// project that also pulls in Testcontainers, and is not a pin anyone made a compatibility claim about.
/// </remarks>
public sealed class CompatibilityPinAgreementTests
{
    private const string ProofProject = "AgentExperience.CompatibilityProof";

    /// <summary>
    /// The pins the proof exists to back. If the proof stopped referencing one of these, the pin would
    /// have no executable evidence left, and the agreement check below would pass vacuously.
    /// </summary>
    public static TheoryData<string> ProvenPins() => new(
        "Microsoft.Agents.AI",
        "Npgsql",
        "Pgvector",
        "Microsoft.Extensions.Compliance.Redaction");

    /// <summary>
    /// The one shipping reference that is a floor rather than an exact pin, named so the exception is
    /// visible rather than tolerated by accident. It is listed in the README's Known limits table.
    /// </summary>
    private static readonly HashSet<string> DocumentedFloors = new(StringComparer.OrdinalIgnoreCase)
    {
        "AgentExperience.Core|Microsoft.Extensions.Compliance.Redaction",
    };

    [Theory]
    [MemberData(nameof(ProvenPins))]
    public void The_proof_references_every_pin_it_is_evidence_for_and_pins_it_exactly(string package)
    {
        var proof = DeclaredReferences(ProofProject);

        Assert.True(proof.TryGetValue(package, out var version), $"{ProofProject} no longer references {package}, so that pin has no executable compatibility evidence.");
        Assert.True(IsExact(version), $"{ProofProject} references {package} as '{version}', a floating range; pin it exactly, as the shipping project does.");
    }

    [Fact]
    public void Every_version_the_proof_declares_matches_the_shipping_pin_for_the_same_package()
    {
        var proof = DeclaredReferences(ProofProject);
        var disagreements = new List<string>();

        foreach (var shipping in ShippingProjects())
        {
            foreach (var (package, version) in DeclaredReferences(shipping))
            {
                if (proof.TryGetValue(package, out var proofVersion) && Bare(proofVersion) != Bare(version))
                {
                    disagreements.Add($"{package}: {shipping} pins {version}, {ProofProject} declares {proofVersion}");
                }
            }
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));
    }

    [Fact]
    public void Every_version_the_proof_resolves_matches_what_the_shipping_package_resolves_for_its_direct_pins()
    {
        var proof = ResolvedPackages(ProofProject);
        var disagreements = new List<string>();
        var compared = 0;

        foreach (var shipping in ShippingProjects())
        {
            foreach (var (package, resolved) in ResolvedPackages(shipping).Where(p => p.Value.Direct).Select(p => (p.Key, p.Value.Version)))
            {
                if (!proof.TryGetValue(package, out var proofResolved))
                {
                    continue;
                }

                compared++;
                if (proofResolved.Version != resolved)
                {
                    disagreements.Add($"{package}: {shipping} resolves {resolved}, {ProofProject} resolves {proofResolved.Version}");
                }
            }
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));

        // MAF, Npgsql (twice), Pgvector, the redaction package, and the model-provider abstractions at least.
        Assert.True(compared >= 6, $"Only {compared} shipping pins were found in the proof's lock file, so this comparison proves little.");
    }

    [Fact]
    public void Every_shipping_package_reference_is_an_exact_pin_except_the_documented_floor()
    {
        var floating = ShippingProjects()
            .SelectMany(project => DeclaredReferences(project).Select(reference => (Project: project, reference.Key, reference.Value)))
            .Where(r => !IsExact(r.Value) && !DocumentedFloors.Contains($"{r.Project}|{r.Key}"))
            .Select(r => $"{r.Project}: {r.Key} '{r.Value}'")
            .ToList();

        Assert.True(floating.Count == 0, "Floating shipping references with no compatibility claim behind them:" + Environment.NewLine + string.Join(Environment.NewLine, floating));

        foreach (var floor in DocumentedFloors)
        {
            var parts = floor.Split('|');
            Assert.True(
                DeclaredReferences(parts[0]).TryGetValue(parts[1], out var version) && !IsExact(version),
                $"{floor} is listed as a documented floor but is now exact-pinned or gone; remove it from DocumentedFloors and the README's Known limits table.");
        }
    }

    private static IEnumerable<string> ShippingProjects() =>
        Directory.GetDirectories(Path.Combine(RepositoryRoot.Path, "src"))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal);

    private static string ProjectDirectory(string project) =>
        project == ProofProject
            ? Path.Combine(RepositoryRoot.Path, "tests", project)
            : Path.Combine(RepositoryRoot.Path, "src", project);

    private static Dictionary<string, string> DeclaredReferences(string project) =>
        XDocument.Load(Path.Combine(ProjectDirectory(project), project + ".csproj"))
            .Descendants("PackageReference")
            .ToDictionary(
                element => element.Attribute("Include")!.Value,
                element => element.Attribute("Version")!.Value,
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, (string Version, bool Direct)> ResolvedPackages(string project)
    {
        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectDirectory(project), "packages.lock.json")));
        var result = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in lockFile.RootElement.GetProperty("dependencies").GetProperty("net10.0").EnumerateObject())
        {
            var type = package.Value.GetProperty("type").GetString();
            if (type is "Project" || !package.Value.TryGetProperty("resolved", out var resolved))
            {
                continue;
            }

            result[package.Name] = (resolved.GetString()!, type == "Direct");
        }

        return result;
    }

    private static bool IsExact(string version) =>
        version.StartsWith('[') && version.EndsWith(']') && !version.Contains(',');

    private static string Bare(string version) => version.Trim('[', ']');
}
