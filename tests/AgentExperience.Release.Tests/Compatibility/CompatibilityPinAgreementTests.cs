using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentExperience.Release.Tests.Compatibility;

/// <summary>
/// Story 4.3, frozen rule 5: <c>tests/AgentExperience.CompatibilityProof</c> is the executable evidence
/// behind the shipping packages' dependency versions, so it must resolve exactly the versions they declare.
/// Before this test the proof floated its <c>PackageReference</c> versions while the shipping projects
/// exact-pinned theirs, so the evidence could silently detach from what ships.
/// </summary>
/// <remarks>
/// <para>
/// Checked twice, because either alone can be fooled: the declared versions in the csproj files (what a
/// restore is asked for) and the resolved versions in the committed <c>packages.lock.json</c> files (what
/// a restore actually produced), in every target framework the lock files record. Only packages a
/// shipping project references <em>directly</em> are compared; transitive infrastructure (logging
/// abstractions, for example) legitimately resolves higher in a test project that also pulls in
/// Testcontainers, and is not a version anyone made a compatibility claim about.
/// </para>
/// <para>
/// Story 6.3 (KL-13) changed the policy these tests enforce. Every shipping reference is a <b>floor</b>
/// (<c>Version="x.y.z"</c>) except <c>Microsoft.Agents.AI</c>, which story 7.2 moved from an exact pin to a
/// <b>tested range</b> bounded at its next major (<c>[x.y.z, N.0.0)</c>). A floor (or a range's lower bound) is only
/// evidence-backed if the committed lock files resolve the floor itself, so the default run tests the
/// lowest version a host can get; CI's floating leg (<c>eng/probe-floating-dependencies.sh</c>) tests the
/// newest in the same major. The proof pins each floor exactly, so it always exercises the bottom of the
/// range. The whole class carries the <c>DeclaredPins</c> trait: the floating leg rewrites these declared
/// versions on purpose, and filters it out.
/// </para>
/// </remarks>
[Trait("Category", "DeclaredPins")]
public sealed class CompatibilityPinAgreementTests
{
    private const string ProofProject = "AgentExperience.CompatibilityProof";

    /// <summary>
    /// The shipping references declared as a range bounded at their next major, and why. Everything else must be
    /// a bare floor, and nothing may be exact any more (story 7.2, KL-13: an exact pin is a restore failure for any
    /// host on a newer version). Adding a package here is a deliberate, reviewed act, argued for in
    /// <c>docs/compatibility-evidence.md</c>.
    /// </summary>
    private static readonly Dictionary<string, string> MajorBoundedRanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Agents.AI"] = "no stated SemVer promise and caller-visible behaviour changes between minors, so the next major is where a break is expected",
    };

    private static readonly Regex BareVersion = new(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary><c>[x.y.z, N.0.0)</c>: inclusive floor, exclusive upper bound at a major.</summary>
    private static readonly Regex MajorBoundedRange = new(@"^\[(?<floor>(?<major>[0-9]+)\.[0-9]+\.[0-9]+), (?<next>[0-9]+)\.0\.0\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The pins the proof exists to back. If the proof stopped referencing one of these, the pin would
    /// have no executable evidence left, and the agreement check below would pass vacuously.
    /// </summary>
    public static TheoryData<string> ProvenPins() => new(
        "Microsoft.Agents.AI",
        "Npgsql",
        "Pgvector",
        "Microsoft.Extensions.Compliance.Redaction");

    [Theory]
    [MemberData(nameof(ProvenPins))]
    public void The_proof_references_every_pin_it_is_evidence_for_and_pins_it_exactly(string package)
    {
        var proof = DeclaredReferences(ProofProject);

        Assert.True(proof.TryGetValue(package, out var version), $"{ProofProject} no longer references {package}, so that pin has no executable compatibility evidence.");
        Assert.True(IsExact(version), $"{ProofProject} references {package} as '{version}', a floating range; pin it exactly, at the shipping floor, so the proof always runs the bottom of the range.");
    }

    [Fact]
    public void Every_version_the_proof_declares_matches_the_shipping_version_for_the_same_package()
    {
        var proof = DeclaredReferences(ProofProject);
        var disagreements = new List<string>();

        foreach (var shipping in ShippingProjects())
        {
            foreach (var (package, version) in DeclaredReferences(shipping))
            {
                if (proof.TryGetValue(package, out var proofVersion) && LowerBound(proofVersion) != LowerBound(version))
                {
                    disagreements.Add($"{package}: {shipping} declares {version}, {ProofProject} declares {proofVersion}");
                }
            }
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));
    }

    [Fact]
    public void Every_version_the_proof_resolves_matches_what_the_shipping_package_resolves_for_its_direct_references()
    {
        var disagreements = new List<string>();
        var compared = 0;

        foreach (var shipping in ShippingProjects())
        {
            foreach (var (framework, packages) in ResolvedPackages(shipping))
            {
                var proof = ResolvedPackages(ProofProject).GetValueOrDefault(framework);
                if (proof is null)
                {
                    disagreements.Add($"{ProofProject} has no {framework} section in its lock file, but {shipping} ships {framework}");
                    continue;
                }

                foreach (var (package, resolved) in packages.Where(p => p.Value.Direct).Select(p => (p.Key, p.Value.Version)))
                {
                    if (!proof.TryGetValue(package, out var proofResolved))
                    {
                        continue;
                    }

                    compared++;
                    if (proofResolved.Version != resolved)
                    {
                        disagreements.Add($"{package} ({framework}): {shipping} resolves {resolved}, {ProofProject} resolves {proofResolved.Version}");
                    }
                }
            }
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));

        // Per framework: MAF, Npgsql (twice), Pgvector, the redaction package, the model-provider
        // abstractions, and the DI abstractions (three times) at least.
        Assert.True(compared >= 9 * ShippedFrameworks().Count, $"Only {compared} shipping references were found in the proof's lock file, so this comparison proves little.");
    }

    /// <summary>
    /// Every shipping reference is a bare floor, except the few <see cref="MajorBoundedRanges"/> names, which
    /// are a floor with an upper bound at exactly the next major. No exact pin, no floating version, and no other
    /// range: an upper bound below the next major turns every newer minor into a warning a host with warnings as
    /// errors cannot restore past, which is the conflict KL-13 described.
    /// </summary>
    [Fact]
    public void Every_shipping_reference_is_a_floor_except_the_documented_major_bounded_ranges()
    {
        var wrong = new List<string>();

        foreach (var project in ShippingProjects())
        {
            foreach (var (package, version) in DeclaredReferences(project))
            {
                if (MajorBoundedRanges.ContainsKey(package))
                {
                    var range = MajorBoundedRange.Match(version);
                    if (!range.Success || int.Parse(range.Groups["next"].Value) != int.Parse(range.Groups["major"].Value) + 1)
                    {
                        wrong.Add($"{project}: {package} '{version}' must be [x.y.z, (x+1).0.0) ({MajorBoundedRanges[package]})");
                    }
                }
                else if (!BareVersion.IsMatch(version))
                {
                    wrong.Add($"{project}: {package} '{version}' must be a bare floor (x.y.z)");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Every_documented_major_bounded_range_is_still_referenced()
    {
        var declared = ShippingProjects().SelectMany(project => DeclaredReferences(project).Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(MajorBoundedRanges.Keys, package => Assert.Contains(package, declared));
    }

    /// <summary>
    /// A floor is evidence only if the default run tests the floor itself. NuGet resolves the lowest
    /// version a range admits, unless something else in the graph asks for more; if that ever happens, the
    /// floor would claim a version no committed lock file ever restored, so it has to move up to meet it.
    /// </summary>
    [Fact]
    public void Every_floor_is_exactly_what_the_committed_lock_files_resolve_in_every_framework()
    {
        var wrong = new List<string>();
        var checkedFloors = 0;

        foreach (var project in ShippingProjects())
        {
            var declared = DeclaredReferences(project);
            var only = DeclaredFrameworkConditions(project);
            foreach (var (framework, packages) in ResolvedPackages(project))
            {
                foreach (var (package, version) in declared
                    .Where(d => (BareVersion.IsMatch(d.Value) || MajorBoundedRange.IsMatch(d.Value)) && AppliesTo(only, d.Key, framework))
                    .Select(d => (d.Key, LowerBound(d.Value))))
                {
                    checkedFloors++;
                    if (!packages.TryGetValue(package, out var resolved) || !resolved.Direct)
                    {
                        wrong.Add($"{project} ({framework}): {package} has no direct entry in the lock file");
                    }
                    else if (resolved.Version != version)
                    {
                        wrong.Add($"{project} ({framework}): {package} declares the floor {version}, but the lock file resolves {resolved.Version}");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(checkedFloors > 0, "No floors were checked.");
    }

    /// <summary>
    /// The tests run from the test projects' lock files, not the shipping ones. A test-only dependency
    /// (Testcontainers, a newer DI container) could lift a floored package above its floor there, and the
    /// default run would then test a version the floor does not name. So every lock file under
    /// <c>tests/</c>, <c>samples/</c> and <c>experiments/</c> must resolve each floored package, where it appears at all, to the
    /// floor itself, in every framework it records.
    /// </summary>
    [Fact]
    public void Every_test_and_sample_lock_file_resolves_each_floored_package_to_the_floor()
    {
        var floors = ShippingProjects()
            .SelectMany(project => DeclaredReferences(project))
            .Where(reference => BareVersion.IsMatch(reference.Value) || MajorBoundedRange.IsMatch(reference.Value))
            .GroupBy(reference => reference.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => LowerBound(group.First().Value), StringComparer.OrdinalIgnoreCase);

        // A floor declared for one framework only (the net8.0 System.Text.Json and Microsoft.Bcl.Memory) is a
        // claim about that framework only; elsewhere the package is the shared framework's, or a dependency's.
        // A package some shipping project declares unconditionally is a floor on every framework, whatever another
        // project conditions; only a package every declaring project conditions is limited to those frameworks.
        var unconditional = ShippingProjects()
            .SelectMany(project => DeclaredReferences(project).Keys.Where(package => !DeclaredFrameworkConditions(project).ContainsKey(package)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var only = ShippingProjects()
            .SelectMany(project => DeclaredFrameworkConditions(project))
            .Where(reference => !unconditional.Contains(reference.Key))
            .GroupBy(reference => reference.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.SelectMany(reference => reference.Value).ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);

        var lockFiles = new[] { "tests", "samples", "experiments" }
            .SelectMany(root => Directory.GetDirectories(Path.Combine(RepositoryRoot.Path, root)))
            .Select(directory => Path.Combine(directory, "packages.lock.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToList();

        var wrong = new List<string>();
        var checkedEntries = 0;

        foreach (var lockFile in lockFiles)
        {
            var name = Path.GetFileName(Path.GetDirectoryName(lockFile)!);
            foreach (var (framework, packages) in ReadLockFile(lockFile))
            {
                foreach (var (package, floor) in floors)
                {
                    if (!AppliesTo(only, package, framework) || !packages.TryGetValue(package, out var resolved))
                    {
                        continue;
                    }

                    checkedEntries++;
                    if (resolved.Version != floor)
                    {
                        wrong.Add($"{name} ({framework}): {package} resolves {resolved.Version}, above the shipping floor {floor}; the tests there no longer run the floor");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(lockFiles.Count >= 5 && checkedEntries > 0, $"Only {lockFiles.Count} lock files and {checkedEntries} floored entries were checked, so this proves little.");
    }

    [Fact]
    public void A_package_referenced_by_several_shipping_projects_has_one_version_in_all_of_them()
    {
        var byPackage = ShippingProjects()
            .SelectMany(project => DeclaredReferences(project).Select(reference => (Project: project, Package: reference.Key, Version: reference.Value)))
            .GroupBy(r => r.Package, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Version).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key}: " + string.Join(", ", g.Select(r => $"{r.Project} {r.Version}")))
            .ToList();

        Assert.True(byPackage.Count == 0, string.Join(Environment.NewLine, byPackage));
    }

    /// <summary>
    /// Story 7.2 (KL-13): a shipping reference may be conditioned on one target framework (the net8.0-only
    /// packages that supply APIs the .NET 8 shared framework lacks), but only in the one shape the floor checks
    /// above understand, naming a framework the build targets. Anything else would make a floor apply, or not
    /// apply, somewhere these tests do not look. And such a reference must be a direct reference in exactly its
    /// framework's section of the lock file, and in no other.
    /// </summary>
    [Fact]
    public void Every_conditional_shipping_reference_names_one_target_framework_and_resolves_only_there()
    {
        var wrong = new List<string>();
        var conditional = 0;

        foreach (var project in ShippingProjects())
        {
            var document = XDocument.Load(Path.Combine(ProjectDirectory(project), project + ".csproj"));
            foreach (var reference in document.Descendants("PackageReference"))
            {
                var condition = ConditionOf(reference);
                if (condition is null)
                {
                    continue;
                }

                conditional++;
                var package = reference.Attribute("Include")!.Value;
                var match = FrameworkCondition.Match(condition);
                if (!match.Success || !ShippedFrameworks().Contains(match.Groups["framework"].Value))
                {
                    wrong.Add($"{project}: {package} has the condition \"{condition}\"; only '$(TargetFramework)' == '<a target framework>' is understood");
                    continue;
                }

                var only = match.Groups["framework"].Value;
                foreach (var (framework, packages) in ResolvedPackages(project))
                {
                    var direct = packages.TryGetValue(package, out var resolved) && resolved.Direct;
                    if (framework == only && !direct)
                    {
                        wrong.Add($"{project} ({framework}): {package} is declared for {framework}, but is not a direct reference there");
                    }
                    else if (framework != only && direct)
                    {
                        wrong.Add($"{project} ({framework}): {package} is declared for {only} only, but is a direct reference in {framework}");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(conditional >= 3, $"Only {conditional} conditional references were found; Core and the store declare three net8.0-only floors between them.");
    }

    [Fact]
    public void Every_shipping_lock_file_records_exactly_the_target_frameworks_the_build_targets()
    {
        foreach (var project in ShippingProjects().Append(ProofProject))
        {
            Assert.Equal(ShippedFrameworks(), ResolvedPackages(project).Keys.Order(StringComparer.Ordinal).ToList());
        }
    }

    private static List<string> ShippedFrameworks()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot.Path, "Directory.Build.props"));
        var value = props.Descendants("AgentExperienceTargetFrameworks").Single().Value;
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList();
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

    private static readonly Regex FrameworkCondition = new(@"^\s*'\$\(TargetFramework\)'\s*==\s*'(?<framework>net[0-9]+\.[0-9]+)'\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one condition on a reference, on it or its ItemGroup. A condition anywhere further up (a Choose/When, a
    /// conditioned ancestor) is reported as a shape the checks do not understand, rather than silently ignored.
    /// </summary>
    private static string? ConditionOf(XElement packageReference)
    {
        var conditions = packageReference.AncestorsAndSelf()
            .Select(element => element.Name.LocalName is "When" ? element.Attribute("Condition")?.Value ?? "When" : element.Attribute("Condition")?.Value)
            .Where(condition => condition is not null)
            .ToList();
        var own = (packageReference.Attribute("Condition") ?? packageReference.Parent?.Attribute("Condition"))?.Value;
        return conditions.Count switch
        {
            0 => null,
            1 when own is not null => own,
            _ => string.Join(" AND ", conditions),
        };
    }

    /// <summary>
    /// The shipping references declared for one target framework only, to that framework. A condition in any
    /// other shape maps to an empty string, which applies nowhere, and
    /// <see cref="Every_conditional_shipping_reference_names_one_target_framework_and_resolves_only_there"/> fails.
    /// </summary>
    private static Dictionary<string, HashSet<string>> DeclaredFrameworkConditions(string project) =>
        XDocument.Load(Path.Combine(ProjectDirectory(project), project + ".csproj"))
            .Descendants("PackageReference")
            .Where(element => ConditionOf(element) is not null)
            .GroupBy(element => element.Attribute("Include")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(element => FrameworkCondition.Match(ConditionOf(element)!) is { Success: true } match ? match.Groups["framework"].Value : string.Empty)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

    private static bool AppliesTo(Dictionary<string, HashSet<string>> only, string package, string framework) =>
        !only.TryGetValue(package, out var frameworks) || frameworks.Contains(framework);

    /// <summary>Framework, then package, to the resolved version and whether the reference is direct.</summary>
    private static Dictionary<string, Dictionary<string, (string Version, bool Direct)>> ResolvedPackages(string project) =>
        ReadLockFile(Path.Combine(ProjectDirectory(project), "packages.lock.json"));

    private static Dictionary<string, Dictionary<string, (string Version, bool Direct)>> ReadLockFile(string path)
    {
        using var lockFile = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, Dictionary<string, (string, bool)>>(StringComparer.Ordinal);

        foreach (var framework in lockFile.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            var packages = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in framework.Value.EnumerateObject())
            {
                var type = package.Value.GetProperty("type").GetString();
                if (type is "Project" || !package.Value.TryGetProperty("resolved", out var resolved))
                {
                    continue;
                }

                packages[package.Name] = (resolved.GetString()!, type == "Direct");
            }

            result[framework.Name] = packages;
        }

        return result;
    }

    private static bool IsExact(string version) =>
        version.StartsWith('[') && version.EndsWith(']') && !version.Contains(',');

    /// <summary>The lowest version a declaration admits: the bare floor, an exact pin's version, or a range's lower bound.</summary>
    private static string LowerBound(string version) => version.TrimStart('[').Split(',')[0].TrimEnd(']').Trim();
}
