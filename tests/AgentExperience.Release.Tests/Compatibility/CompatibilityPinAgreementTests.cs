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

    /// <summary>
    /// The one directory under <c>tests/</c> the floor rule below does not apply to, and why: story 12.1's seeders
    /// (<c>tests/AgentExperience.Upgrade.Seeders/Preview1</c>, <c>Preview2</c>, ...) each exact-pin a <em>published</em>
    /// preview's packages, so their lock files resolve what that preview shipped with (<c>0.1.0-preview.1</c> pinned
    /// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> 10.0.11, for example, below today's floor). That is the
    /// point of them: they recreate a database the way an old release wrote it, and never run today's code. No other
    /// project may be excluded. Their lock files sit one level further down, so the scan below would not reach them
    /// anyway; the name is excluded explicitly so that stays a stated decision rather than an accident of depth. What the
    /// seeders must pin instead is checked positively, by
    /// <see cref="The_upgrade_seeders_pin_exactly_the_published_previews_the_upgrade_suite_covers"/>.
    /// </summary>
    private const string DeliberatelyOldPins = "AgentExperience.Upgrade.Seeders";

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
            foreach (var (framework, packages) in ResolvedPackages(project))
            {
                foreach (var (package, version) in declared
                    .Where(d => BareVersion.IsMatch(d.Value) || MajorBoundedRange.IsMatch(d.Value))
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
    /// floor itself, in every framework it records. The one exception is <see cref="DeliberatelyOldPins"/>, whose
    /// seeders pin published previews on purpose.
    /// </summary>
    [Fact]
    public void Every_test_and_sample_lock_file_resolves_each_floored_package_to_the_floor()
    {
        var floors = ShippingProjects()
            .SelectMany(project => DeclaredReferences(project))
            .Where(reference => BareVersion.IsMatch(reference.Value) || MajorBoundedRange.IsMatch(reference.Value))
            .GroupBy(reference => reference.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => LowerBound(group.First().Value), StringComparer.OrdinalIgnoreCase);

        var lockFiles = new[] { "tests", "samples", "experiments" }
            .SelectMany(root => Directory.GetDirectories(Path.Combine(RepositoryRoot.Path, root)))
            .Where(directory => !string.Equals(Path.GetFileName(directory), DeliberatelyOldPins, StringComparison.Ordinal))
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
                    if (!packages.TryGetValue(package, out var resolved))
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

    /// <summary>
    /// Story 12.1: the seeders are exempt from the floor rule, so what they pin is held to its own rule. The
    /// <see cref="DeliberatelyOldPins"/> directory holds only <c>PreviewN</c> projects (plus their shared source and the
    /// nuget.config); each exact-pins the four packages it seeds with at <c>[0.1.0-preview.N]</c>; its lock file resolves
    /// every AgentExperience package to exactly that version; and the set of previews equals the one the upgrade suite's
    /// <c>PublishedPreview</c> list covers, so a seeder can neither drift to another version nor exist untested.
    /// </summary>
    [Fact]
    public void The_upgrade_seeders_pin_exactly_the_published_previews_the_upgrade_suite_covers()
    {
        var root = Path.Combine(RepositoryRoot.Path, "tests", DeliberatelyOldPins);
        string[] packages = ["AgentExperience.Abstractions", "AgentExperience.Core", "AgentExperience.Storage.Postgres", "AgentExperience.Storage.Postgres.Vectors"];
        var wrong = new List<string>();
        var seeded = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var directory in Directory.GetDirectories(root).Select(path => Path.GetFileName(path)!).Where(name => name is not ("bin" or "obj")))
        {
            if (directory == "Shared")
            {
                continue;
            }

            var match = Regex.Match(directory, @"^Preview(?<n>[1-9][0-9]*)$", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                wrong.Add($"{directory}: only PreviewN seeder projects (and Shared) belong in {DeliberatelyOldPins}");
                continue;
            }

            var version = $"0.1.0-preview.{match.Groups["n"].Value}";
            seeded.Add(version);
            var project = Path.Combine(root, directory, $"{DeliberatelyOldPins}.{directory}.csproj");
            if (!File.Exists(project))
            {
                wrong.Add($"{directory}: no {Path.GetFileName(project)}");
                continue;
            }

            var declared = XDocument.Load(project).Descendants("PackageReference")
                .ToDictionary(element => element.Attribute("Include")!.Value, element => element.Attribute("Version")!.Value, StringComparer.Ordinal);
            if (!declared.Keys.Order(StringComparer.Ordinal).SequenceEqual(packages) || declared.Values.Any(v => v != $"[{version}]"))
            {
                wrong.Add($"{directory}: must reference exactly {string.Join(", ", packages)} at [{version}]; it references {string.Join(", ", declared.Select(d => $"{d.Key} {d.Value}"))}");
            }

            foreach (var (framework, resolved) in ReadLockFile(Path.Combine(root, directory, "packages.lock.json")))
            {
                foreach (var (package, entry) in resolved.Where(p => p.Key.StartsWith("AgentExperience.", StringComparison.Ordinal)))
                {
                    if (entry.Version != version)
                    {
                        wrong.Add($"{directory} ({framework}): {package} resolves {entry.Version}, not {version}");
                    }
                }

                var direct = resolved.Where(p => p.Value.Direct).Select(p => p.Key).Order(StringComparer.Ordinal);
                if (!direct.SequenceEqual(packages))
                {
                    wrong.Add($"{directory} ({framework}): the lock file's direct packages are {string.Join(", ", direct)}");
                }
            }
        }

        var covered = Regex.Matches(
                File.ReadAllText(Path.Combine(RepositoryRoot.Path, "tests", "AgentExperience.Upgrade.Tests", "PublishedPreview.cs")),
                @"new\(\s*""(?<version>0\.1\.0-preview\.[0-9]+)"",\s*""Preview[0-9]+""",
                RegexOptions.CultureInvariant)
            .Select(m => m.Groups["version"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(seeded.Count >= 2, $"Only {seeded.Count} seeders were found under tests/{DeliberatelyOldPins}.");
        Assert.True(covered.SetEquals(seeded), $"The seeders pin [{string.Join(", ", seeded)}], but PublishedPreview covers [{string.Join(", ", covered.Order(StringComparer.Ordinal))}].");
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
    /// No shipping reference is conditional: every package targets the same frameworks, and every floor applies to
    /// all of them, so a floor is never limited to somewhere the checks above do not look. The last conditional ones,
    /// the net8.0-only System.Text.Json and Microsoft.Bcl.Memory, went when the packages became net10.0 only. A
    /// condition on the reference, its ItemGroup or anything further up (a Choose/When) counts.
    /// </summary>
    [Fact]
    public void No_shipping_reference_is_conditional()
    {
        var wrong = new List<string>();
        var checkedReferences = 0;

        foreach (var project in ShippingProjects())
        {
            var document = XDocument.Load(Path.Combine(ProjectDirectory(project), project + ".csproj"));
            foreach (var reference in document.Descendants("PackageReference"))
            {
                checkedReferences++;
                if (ConditionOf(reference) is { } condition)
                {
                    wrong.Add($"{project}: {reference.Attribute("Include")!.Value} has the condition \"{condition}\"");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(checkedReferences > 0, "No shipping references were checked.");
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

    /// <summary>Every directory under src/ holding its own csproj; src/Shared holds linked source files only.</summary>
    private static IEnumerable<string> ShippingProjects() =>
        Directory.GetDirectories(Path.Combine(RepositoryRoot.Path, "src"))
            .Where(path => File.Exists(Path.Combine(path, Path.GetFileName(path) + ".csproj")))
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


    /// <summary>
    /// Every condition on a reference: on it, its ItemGroup, or anything further up (a Choose/When, a conditioned
    /// ancestor), joined; <see langword="null"/> when there is none.
    /// </summary>
    private static string? ConditionOf(XElement packageReference)
    {
        var conditions = packageReference.AncestorsAndSelf()
            .Select(element => element.Name.LocalName is "When" ? element.Attribute("Condition")?.Value ?? "When" : element.Attribute("Condition")?.Value)
            .Where(condition => condition is not null)
            .ToList();
        return conditions.Count == 0 ? null : string.Join(" AND ", conditions);
    }

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
