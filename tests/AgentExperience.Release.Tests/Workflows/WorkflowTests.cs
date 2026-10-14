using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Workflows;

/// <summary>
/// Story 4.3, frozen rule 3, as amended by the trusted-publishing decision: nothing publishes a package except
/// <c>release.yml</c>, and it only under the constraints <see cref="ReleaseViolations"/> enforces (a pushed version
/// tag as its only trigger, a publish job gated by the <c>nuget-release</c> environment's reviewers, a short-lived
/// nuget.org key from NuGet Trusted Publishing, and no stored secret). Story 22.7 adds the Python distribution and the
/// Go modules to the same tag: a <c>publish-pypi</c> job behind the <c>pypi-release</c> environment, publishing through
/// PyPI Trusted Publishing, and the Go tags created by the publish job. Every other workflow may carry no step, secret
/// or permission that would let a publish fire, a PyPI upload or a tag push included. The MAF compatibility probe's latest leg was non-blocking (AD-F);
/// story 6.3 added the floating-dependency leg, which gates outside pull requests, and story 7.2 made the MAF latest
/// leg gate the same way, once MAF became a range the newest 1.x belongs to.
/// </summary>
public sealed class WorkflowTests
{
    /// <summary>Anything that could publish a package, or hand a job the credentials or permissions to.</summary>
    private static readonly string[] PublishingMarkers =
    [
        "nuget push",
        "dotnet nuget",
        "NUGET_API_KEY",
        "NUGET_KEY",
        "nuget.org/api/v2/package",
        "gh release",
        "softprops/action-gh-release",
        "actions/create-release",
        // Story 22.7: PyPI, and the Go modules, which a tag releases.
        "pypa/gh-action-pypi-publish",
        "uv publish",
        "twine upload",
        "upload.pypi.org",
        "pypi-release",
        "git push",
        "git tag",
        "git/refs",
        // The public API baseline's accept switch: a workflow that set it would accept every API change.
        "AGENTEXPERIENCE_ACCEPT_API_CHANGES",
        // No workflow here needs a secret, so none may reference one (a publish key would arrive this way).
        "secrets.",
    ];

    /// <summary>Any permission scope granted <c>write</c>, quoted or not, and the blanket <c>write-all</c>.</summary>
    private static readonly Regex WritePermission = new(
        @"(\b[a-z-]+\s*:\s*['""]?write['""]?\s*(#.*)?$)|write-all",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The one workflow allowed to publish.</summary>
    private const string ReleaseWorkflow = "release.yml";

    private static IEnumerable<string> AllWorkflows() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot.Path, ".github", "workflows")).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal);

    /// <summary>Every workflow except <c>release.yml</c>, which <see cref="ReleaseViolations"/> holds to its own rules.</summary>
    public static TheoryData<string> Workflows()
    {
        var data = new TheoryData<string>();
        foreach (var name in AllWorkflows().Where(name => name != ReleaseWorkflow))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_workflow_but_release_can_publish_a_package(string workflow)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", workflow));

        Assert.False(ForbiddenIn(text, out var found), $"{workflow} contains '{found}'.");
    }

    /// <summary>
    /// Story 12.2: the benchmarks are compiled by the solution build and never run by a workflow. A timing taken on a
    /// shared runner compares with nothing, and a threshold on one would fail on noise.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryWorkflow))]
    public void No_workflow_runs_the_benchmarks(string workflow)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", workflow));

        Assert.DoesNotContain("benchmarks/", text.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AgentExperience.Benchmarks", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BenchmarkDotNet", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every workflow, <c>release.yml</c> included.</summary>
    public static TheoryData<string> EveryWorkflow() => new(AllWorkflows());

    [Theory]
    [InlineData("permissions: write-all")]
    [InlineData("permissions:\n  contents: write")]
    [InlineData("permissions:\n  packages: \"write\"")]
    [InlineData("permissions:\n  id-token: 'write'")]
    [InlineData("env:\n  AGENTEXPERIENCE_ACCEPT_API_CHANGES: true")]
    [InlineData("run: echo ${{ secrets.ANYTHING }}")]
    [InlineData("run: dotnet nuget push x.nupkg")]
    [InlineData("      - uses: pypa/gh-action-pypi-publish@dc37677b2e1c63e2034f94d8a5b11f265b73ba33 # v1.14.2")]
    [InlineData("run: uv publish --trusted-publishing always")]
    [InlineData("run: uvx twine upload dist/*")]
    [InlineData("    environment: pypi-release")]
    [InlineData("run: git tag go/v0.1.0 && git push origin go/v0.1.0")]
    [InlineData("run: git push --tags")]
    [InlineData("run: gh api repos/fabbrik/AgentExperience.NET/git/refs -f ref=refs/tags/go/v0.1.0 -f sha=$GITHUB_SHA")]
    public void The_guard_catches_each_way_a_workflow_could_gain_publishing_power(string workflowText)
    {
        Assert.True(ForbiddenIn(workflowText, out _), $"The guard missed: {workflowText}");
    }

    [Fact]
    public void The_guard_allows_read_permissions()
    {
        Assert.False(ForbiddenIn("permissions:\n  contents: read\n", out var found), $"False positive: '{found}'.");
    }

    private static bool ForbiddenIn(string text, out string found)
    {
        foreach (var marker in PublishingMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                found = marker;
                return true;
            }
        }

        var match = WritePermission.Match(text.ReplaceLineEndings("\n"));
        found = match.Success ? match.Value.Trim() : string.Empty;
        return match.Success;
    }

    [Fact]
    public void The_CI_workflow_packs_verifies_and_probes_MAF_with_the_latest_leg_non_blocking_only_on_pull_requests()
    {
        var ci = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", "ci.yml"));

        Assert.Contains("permissions:\n  contents: read", ci.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("dotnet pack", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet run eng/verify-packages.cs -- artifacts/packages", ci, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Release.Tests", ci, StringComparison.Ordinal);
        Assert.Contains("leg: [pinned, latest]", ci, StringComparison.Ordinal);
        // Story 7.2 (KL-13): MAF is a range now, so the newest 1.x is a support claim and the latest leg gates on
        // push and on the schedule. Only a pull request may soften it, and only it: the pinned leg always gates.
        var job = Job(ci.ReplaceLineEndings("\n"), "maf-compatibility");
        var softeners = job.Split('\n').Where(line => line.Contains("continue-on-error", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
        Assert.Equal(["continue-on-error: ${{ matrix.leg == 'latest' && github.event_name == 'pull_request' }}"], softeners);
        Assert.Contains("eng/probe-maf-version.sh", ci, StringComparison.Ordinal);
    }

    /// <summary>
    /// Story 6.3 (KL-13): the PostgreSQL matrix CI runs is exactly the set the test fixtures accept, so the
    /// supported list in the evidence document cannot name a major no leg runs, or the other way round.
    /// </summary>
    [Fact]
    public void The_CI_PostgreSQL_matrix_runs_exactly_the_supported_majors_and_passes_each_one_to_the_tests()
    {
        var ci = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", "ci.yml"));

        var legs = Regex.Match(ci, @"^\s*postgres: \[(?<legs>[0-9, ]+)\]\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(legs.Success, "ci.yml has no 'postgres: [..]' matrix.");
        Assert.Equal(
            AgentExperience.Tests.Shared.PostgresTestImage.SupportedMajors,
            legs.Groups["legs"].Value.Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).ToList());

        Assert.Contains(
            $"{AgentExperience.Tests.Shared.PostgresTestImage.MajorVariable}: ${{{{ matrix.postgres }}}}",
            ci,
            StringComparison.Ordinal);
        Assert.Contains(AgentExperience.Tests.Shared.PostgresTestImage.DefaultMajor, AgentExperience.Tests.Shared.PostgresTestImage.SupportedMajors);

        // Every container-backed suite runs on every leg: dropping one from the loop would leave the evidence
        // document claiming coverage CI no longer produces.
        var job = Job(ci.ReplaceLineEndings("\n"), "postgres");
        var tokens = job.Split([' ', '\n', '\\'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimEnd(';'));
        foreach (var project in new[]
        {
            "tests/AgentExperience.Storage.Postgres.Tests",
            "tests/AgentExperience.Storage.Postgres.Vectors.Tests",
            "tests/AgentExperience.CompatibilityProof",
            "tests/AgentExperience.Sample.EndToEnd.Tests",
            // Story 12.1: upgrades from databases the published previews created, on every major.
            "tests/AgentExperience.Upgrade.Tests",
        })
        {
            Assert.Contains(project, tokens);
        }

        Assert.Contains("dotnet test \"$project\" --no-build --configuration Release", job, StringComparison.Ordinal);

        // Story 12.1: the upgrade suite's seeders are restored, locked, before the tests, so the test step needs no network.
        Assert.Contains("for seeder in tests/AgentExperience.Upgrade.Seeders/Preview*/; do", job, StringComparison.Ordinal);
        Assert.Contains("dotnet restore \"$seeder\" --locked-mode", job, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);

        // Story 6.4 (KL-2): the store and vector suites run a second time, in crypto-shredding mode, on every leg.
        Assert.Contains("AGENTEXPERIENCE_TEST_ENCRYPTION: 'on'", job, StringComparison.Ordinal);
        var encrypted = job[job.IndexOf("AGENTEXPERIENCE_TEST_ENCRYPTION", StringComparison.Ordinal)..];
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Tests", encrypted, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Vectors.Tests", encrypted, StringComparison.Ordinal);

        // Story 15.1: the store, vector and upgrade suites run a third time, with row-level security on, on every leg.
        Assert.Contains("AGENTEXPERIENCE_TEST_RLS: 'on'", job, StringComparison.Ordinal);
        var secured = job[job.IndexOf("AGENTEXPERIENCE_TEST_RLS", StringComparison.Ordinal)..];
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Tests", secured, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Vectors.Tests", secured, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Upgrade.Tests", secured, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Sample.EndToEnd.Tests", secured, StringComparison.Ordinal);

        // And a fourth time with both switches on: row-level security and crypto-shredding together.
        var both = job[job.IndexOf("with row-level security and crypto-shredding", StringComparison.Ordinal)..];
        Assert.Contains("AGENTEXPERIENCE_TEST_RLS: 'on'", both, StringComparison.Ordinal);
        Assert.Contains("AGENTEXPERIENCE_TEST_ENCRYPTION: 'on'", both, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Tests", both, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Vectors.Tests", both, StringComparison.Ordinal);
    }

    /// <summary>The text of one top-level job in a workflow, from its key to the next job's key.</summary>
    private static string Job(string workflow, string name)
    {
        var start = workflow.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The workflow has no {name} job.");
        var next = Regex.Match(workflow[(start + 1)..], @"\n  [a-z-]+:\n", RegexOptions.CultureInvariant);
        return next.Success ? workflow.Substring(start, next.Index + 1) : workflow[start..];
    }

    /// <summary>
    /// Story 6.3 (KL-13): the floors are a support claim for every later release in their major, so the leg
    /// that tests the newest one must gate the build on push and on the schedule. On a pull request it may
    /// only report (AD-F), and nothing else may soften it: the one allowed <c>continue-on-error</c> is keyed
    /// on the pull_request event.
    /// </summary>
    [Fact]
    public void The_CI_floating_dependency_leg_exists_and_gates_the_build_outside_pull_requests()
    {
        var ci = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", "ci.yml")).ReplaceLineEndings("\n");

        var job = Job(ci, "floating-dependencies");

        Assert.Contains("run: eng/probe-floating-dependencies.sh", job, StringComparison.Ordinal);
        var softeners = job.Split('\n').Where(line => line.Contains("continue-on-error", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
        Assert.Equal(["continue-on-error: ${{ github.event_name == 'pull_request' }}"], softeners);
        Assert.Contains("push:", ci, StringComparison.Ordinal);
        Assert.Contains("schedule:", ci, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot.Path, "eng", "probe-floating-dependencies.sh")));
    }

    // ---- release.yml: the one workflow that publishes, and only like this ----

    private static string ReadWorkflow(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", name)).ReplaceLineEndings("\n");

    /// <summary>
    /// Exactly one workflow carries anything that could publish, and it is <c>release.yml</c>. A publish step, a key
    /// or a write permission added to any other workflow fails here as well as in the per-workflow theory.
    /// </summary>
    [Fact]
    public void Exactly_one_workflow_can_publish_and_it_is_release_yml()
    {
        var publishers = AllWorkflows().Where(name => ForbiddenIn(ReadWorkflow(name), out _)).ToList();

        Assert.Equal([ReleaseWorkflow], publishers);
    }

    /// <summary>
    /// The production-readiness gate counts the rows of the <c>## Known limits</c> section of <c>docs/known-limits.md</c>
    /// and nothing else: a Documented boundaries row keeps its KL number and must not count. RELEASING.md step 9 and
    /// release.yml run the same count, so the gate a maintainer runs by hand is the gate the tag runs.
    /// </summary>
    private const string KnownLimitsCount = "awk '/^## / { section = ($0 == \"## Known limits\") } section' docs/known-limits.md | grep -cE '^\\| KL-[0-9]+ \\|'";

    /// <summary>The file the gate reads and the release notes quote, relative to the repository root.</summary>
    private const string KnownLimitsFile = "docs/known-limits.md";

    [Fact]
    public void The_gate_counts_only_the_Known_limits_table_in_RELEASING_and_in_release_yml()
    {
        var releasing = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "RELEASING.md")).ReplaceLineEndings("\n");

        Assert.Contains(KnownLimitsCount, releasing, StringComparison.Ordinal);
        Assert.Contains(KnownLimitsCount, ReadWorkflow(ReleaseWorkflow), StringComparison.Ordinal);
    }

    /// <summary>
    /// The two tables in <c>docs/known-limits.md</c> keep their shape: a known limit is a three-cell row (number, limit,
    /// detail) under <c>## Known limits</c>, and a documented boundary is a five-cell row under
    /// <c>## Documented boundaries</c> whose boundary, reason no code change can remove it, response and detail are all
    /// filled in. A boundary without a written reason is a known limit, and fails here.
    /// </summary>
    [Fact]
    public void The_limits_file_separates_known_limits_from_documented_boundaries_and_each_boundary_has_its_reason()
    {
        var limitsFile = File.ReadAllText(Path.Combine(RepositoryRoot.Path, KnownLimitsFile)).ReplaceLineEndings("\n");

        var limits = SectionRows(limitsFile, "## Known limits");
        var boundaries = SectionRows(limitsFile, "## Documented boundaries");

        Assert.All(limits, row => Assert.Equal(3, row.Length));
        Assert.NotEmpty(boundaries);
        Assert.All(boundaries, row =>
        {
            Assert.Equal(5, row.Length);
            Assert.All(row, cell => Assert.False(string.IsNullOrWhiteSpace(cell), $"{row[0]} has an empty cell."));
        });
        Assert.Empty(limits.Select(row => row[0]).Intersect(boundaries.Select(row => row[0]), StringComparer.Ordinal));
    }

    /// <summary>The KL rows of one <c>## </c> section, each split into its cells.</summary>
    private static List<string[]> SectionRows(string markdown, string heading)
    {
        var lines = markdown.Split('\n');
        var start = Array.IndexOf(lines, heading);
        Assert.True(start >= 0, $"{KnownLimitsFile} has no '{heading}' section.");

        return lines.Skip(start + 1)
            .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
            .Where(line => Regex.IsMatch(line, @"^\| KL-[0-9]+ \|", RegexOptions.CultureInvariant))
            .Select(line => Regex.Split(line.Trim().Trim('|'), @"(?<!\\)\|", RegexOptions.CultureInvariant).Select(cell => cell.Trim()).ToArray())
            .ToList();
    }

    [Fact]
    public void The_release_workflow_meets_every_publishing_constraint()
    {
        var violations = ReleaseViolations(ReadWorkflow(ReleaseWorkflow));

        Assert.True(violations.Count == 0, "release.yml breaks its constraints:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// First-party actions stay on the refs ci.yml uses, so the two workflows cannot drift onto different majors of
    /// the same action; the artifact download follows the upload's major. The verify job uses the same refs; the
    /// publish jobs pin every action by commit SHA (story 22.7), so there the version comment's major must match.
    /// </summary>
    [Fact]
    public void The_release_workflow_uses_the_same_first_party_actions_as_CI()
    {
        var ci = UsesRefs(ReadWorkflow("ci.yml"));
        var workflow = ReadWorkflow(ReleaseWorkflow);
        var verify = UsesRefs(Job(workflow, "verify"));
        var published = Uses.Matches(Job(workflow, "publish") + Job(workflow, "publish-pypi"))
            .Select(m => (Action: m.Groups["action"].Value, Major: Regex.Match(m.Groups["comment"].Value, @"#\s*(v[0-9]+)", RegexOptions.CultureInvariant).Groups[1].Value))
            .Where(pair => pair.Action.StartsWith("actions/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(published);

        string? Expected(string action) => action == "actions/download-artifact"
            ? ci.First(pair => pair.Action == "actions/upload-artifact").Ref
            : ci.FirstOrDefault(pair => pair.Action == action).Ref;

        foreach (var (action, major) in published)
        {
            var expected = Expected(action);
            Assert.True(expected is not null, $"release.yml uses {action}, which ci.yml does not.");
            Assert.Equal(expected, major);
        }

        foreach (var (action, reference) in verify.Where(pair => pair.Action.StartsWith("actions/", StringComparison.Ordinal)))
        {
            var expected = Expected(action);
            Assert.True(expected is not null, $"release.yml uses {action}, which ci.yml does not.");
            Assert.Equal(expected, reference);
        }
    }

    /// <summary>
    /// The rules are proven to bite: each mutation below is one way the release workflow could gain a trigger,
    /// a permission, a secret or an unpinned action, or lose a check, and each must be reported.
    /// </summary>
    [Theory]
    [InlineData("  push:\n    tags: ['v*']\n", "  push:\n    tags: ['v*']\n  workflow_dispatch:\n")]
    [InlineData("  push:\n    tags: ['v*']\n", "  push:\n    tags: ['v*']\n    branches: [main]\n")]
    [InlineData("  push:\n    tags: ['v*']\n", "  push:\n    tags: ['v*']\n  pull_request:\n")]
    [InlineData("  push:\n    tags: ['v*']\n", "  push:\n    tags: ['v*']\n  schedule:\n    - cron: '0 6 * * 1'\n")]
    [InlineData("permissions:\n  contents: read\n", "permissions:\n  contents: write\n")]
    [InlineData("permissions:\n  contents: read\n", "permissions: write-all\n")]
    [InlineData("      id-token: write\n", "      id-token: write\n      packages: write\n")]
    [InlineData("      id-token: write\n", "      id-token: write\n      actions: write\n")]
    [InlineData("    environment: nuget-release\n", "    environment: nuget\n")]
    [InlineData("    environment: nuget-release\n", "")]
    [InlineData("    needs: verify\n", "")]
    [InlineData("    timeout-minutes: 90\n", "    timeout-minutes: 90\n    permissions:\n      id-token: write\n")]
    [InlineData("--skip-duplicate", "")]
    [InlineData("git merge-base --is-ancestor \"$GITHUB_SHA\" origin/main", "true")]
    [InlineData("\"$GITHUB_REF_NAME\" != \"v$version\"", "-z \"\"")]
    [InlineData("fetch-depth: 0", "fetch-depth: 1")]
    [InlineData("user: fabbrik76", "user: ${{ secrets.NUGET_USER }}")]
    [InlineData("GH_TOKEN: ${{ github.token }}", "GH_TOKEN: ${{ secrets.RELEASE_PAT }}")]
    [InlineData("NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0", "NuGet/login@v1")]
    [InlineData("--api-key \"$NUGET_API_KEY\"", "--api-key \"${{ steps.login.outputs.NUGET_API_KEY }}\"")]
    [InlineData("if gh release view", "if false && gh release view")]
    [InlineData("dotnet restore --locked-mode", "dotnet restore")]
    [InlineData("          global-json-file: global.json\n", "          global-json-file: global.json\n          dotnet-version: 9.0.x\n")]
    [InlineData("          global-json-file: global.json\n", "          dotnet-version: 10.0.x\n")]
    [InlineData("awk '/^## / { section = ($0 == \"## Known limits\") } section' docs/known-limits.md | grep -cE", "grep -cE")]
    [InlineData("table { exit }' docs/known-limits.md; }", "table { exit }' README.md; }")]
    [InlineData("dotnet run eng/verify-packages.cs -- artifacts/packages", "echo skipped")]
    [InlineData("    steps:\n      - name: Download the verified packages", "    steps:\n      - uses: actions/checkout@v5\n      - name: Download the verified packages")]
    // Story 22.7: the PyPI job.
    [InlineData("    environment: pypi-release\n", "")]
    [InlineData("    environment: pypi-release\n", "    environment: nuget-release\n")]
    [InlineData("    needs: [verify, publish]\n", "    needs: verify\n")]
    [InlineData("    environment: pypi-release\n    permissions:\n      id-token: write\n", "    environment: pypi-release\n    permissions:\n      id-token: write\n      contents: write\n")]
    [InlineData("    environment: pypi-release\n    permissions:\n      id-token: write\n", "    environment: pypi-release\n")]
    [InlineData("pypa/gh-action-pypi-publish@dc37677b2e1c63e2034f94d8a5b11f265b73ba33 # v1.14.2", "pypa/gh-action-pypi-publish@release/v1")]
    [InlineData("pypa/gh-action-pypi-publish@dc37677b2e1c63e2034f94d8a5b11f265b73ba33 # v1.14.2", "pypa/gh-action-pypi-publish@dc37677b2e1c63e2034f94d8a5b11f265b73ba33")]
    [InlineData("skip-existing: true", "skip-existing: false")]
    [InlineData("          skip-existing: true\n", "          skip-existing: true\n          password: ${{ secrets.PYPI_API_TOKEN }}\n")]
    [InlineData("    steps:\n      - name: Download the verified Python distribution", "    steps:\n      - uses: actions/checkout@v5\n      - name: Download the verified Python distribution")]
    [InlineData("    steps:\n      - name: Download the verified Python distribution", "    steps:\n      - run: uv build\n      - name: Download the verified Python distribution")]
    [InlineData("          name: python-dist\n          path: dist\n", "          name: packages\n          path: dist\n")]
    // Story 22.7: the Go tags.
    [InlineData("if [ \"$sha\" != \"$GITHUB_SHA\" ]; then", "if false; then")]
    [InlineData("for module in go go/inmemory go/postgres go/postgres/pgtest go/maf; do", "for module in go; do")]
    [InlineData("https://proxy.golang.org/$escaped/@v/$GO_VERSION.info", "https://example.invalid/$escaped")]
    [InlineData("-f \"sha=$GITHUB_SHA\"", "-f \"sha=$(git rev-parse HEAD)\"")]
    // Story 22.7: the verify job's version agreement, Python and Go checks, and that it publishes nothing.
    [InlineData("test \"$(derive_versions 1.0.0 '')\" = \"1.0.0 1.0.0 v1.0.0\"", "true")]
    [InlineData("if [ \"$declared\" != \"$python\" ]; then", "if false; then")]
    [InlineData("if [ \"$required\" != \"$go\" ]; then", "if false; then")]
    [InlineData("uv run pytest -m \"not postgres\"\n", "true\n")]
    [InlineData("uvx --no-cache twine==7.0.0 check --strict dist/*.whl dist/*.tar.gz\n", "true\n")]
    [InlineData("          rm -f dist/.gitignore\n", "")]
    [InlineData("if [ \"$wheels\" -ne 1 ] || [ \"$sdists\" -ne 1 ]; then", "if false; then")]
    [InlineData("          sha256sum --check --strict \"$RUNNER_TEMP/python-dist.sha256\"\n", "          true\n")]
    [InlineData("(cd dist && sha256sum -- *.whl *.tar.gz) > \"$RUNNER_TEMP/python-dist.sha256\"", "true")]
    [InlineData("      - name: Upload the verified packages\n", "      - name: Late step\n        run: echo late\n\n      - name: Upload the verified packages\n")]
    [InlineData("retention-days: 90\n", "retention-days: 30\n")]
    [InlineData("            python/dist/*.whl\n            python/dist/*.tar.gz\n", "            python/dist/\n")]
    [InlineData("if [ \"$found\" != \"$expected\" ]; then", "if false; then")]
    // Story 22.7: every action in the publish jobs is SHA-pinned.
    [InlineData("uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4.3.0\n        with:\n          name: python-dist", "uses: actions/download-artifact@v4\n        with:\n          name: python-dist")]
    [InlineData("uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4.3.0\n        with:\n          name: packages", "uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093\n        with:\n          name: packages")]
    [InlineData("actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5.4.0", "actions/setup-dotnet@v5")]
    // Story 22.7: the Go tags and the proxy check.
    [InlineData("for module in go go/inmemory go/postgres go/postgres/pgtest go/maf; do\n            tag=", "for module in go go/maf; do\n            tag=")]
    [InlineData("for module in go go/inmemory go/postgres go/postgres/pgtest go/maf; do\n            path=", "for module in go; do\n            path=")]
    [InlineData("elif grep -q 'HTTP 404' \"$error\"; then", "elif true; then")]
    [InlineData("--connect-timeout 10 --max-time 30 ", "")]
    [InlineData("if [ \"$attempt\" -lt 10 ]; then sleep 30; fi", "sleep 30")]
    [InlineData("    timeout-minutes: 75\n", "    timeout-minutes: 20\n")]
    [InlineData("grep -qx \"Version: $PYTHON_VERSION\"", "true")]
    [InlineData("              go test ./...\n", "")]
    [InlineData("              go vet ./...\n", "")]
    [InlineData("astral-sh/setup-uv@d0cc045d04ccac9d8b7881df0226f9e82c39688e # v6.8.0", "astral-sh/setup-uv@v6")]
    [InlineData("          version: '0.12.24'\n", "")]
    [InlineData("enable-cache: false", "enable-cache: true")]
    [InlineData("uv build --out-dir dist\n", "uv build --out-dir dist\n          uv publish dist/*\n")]
    [InlineData("uv build --out-dir dist\n", "uv build --out-dir dist\n          git tag go/v1 && git push origin go/v1\n")]
    public void The_release_rules_catch_each_way_the_workflow_could_weaken(string original, string replacement)
    {
        var workflow = ReadWorkflow(ReleaseWorkflow);
        Assert.Contains(original, workflow, StringComparison.Ordinal);

        var mutated = workflow.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEmpty(ReleaseViolations(mutated));
    }

    private static readonly Regex Uses = new(
        @"^\s*(?:-\s+)?uses:\s*(?<action>[^@\s]+)@(?<ref>\S+)(?<comment>\s+#.*)?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static List<(string Action, string Ref)> UsesRefs(string workflow) =>
        Uses.Matches(workflow).Select(m => (m.Groups["action"].Value, m.Groups["ref"].Value)).ToList();

    /// <summary>Every rule release.yml must meet. Empty means it meets them all.</summary>
    private static List<string> ReleaseViolations(string workflow)
    {
        var violations = new List<string>();
        void Require(bool condition, string rule)
        {
            if (!condition)
            {
                violations.Add(rule);
            }
        }

        var lines = workflow.Split('\n');

        // Top level: nothing but these keys; the trigger and the default permissions exactly as below.
        var topLevel = lines.Where(line => line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#')
            .Select(line => line.Split(':')[0].Trim('"', '\''))
            .ToList();
        Require(
            topLevel.All(key => key is "name" or "on" or "permissions" or "concurrency" or "jobs") && topLevel.Distinct().Count() == topLevel.Count,
            $"Top-level keys must be name, on, permissions, concurrency and jobs, once each (found: {string.Join(", ", topLevel)}).");
        Require(
            TopLevelBlock(lines, "on").SequenceEqual(["on:", "  push:", "    tags: ['v*']"]),
            "The only trigger must be `on: push: tags: ['v*']`.");
        Require(
            TopLevelBlock(lines, "permissions").SequenceEqual(["permissions:", "  contents: read"]),
            "Workflow-level permissions must be exactly `contents: read`.");

        // Exactly three jobs.
        var jobs = TopLevelBlock(lines, "jobs")
            .Select(line => Regex.Match(line, @"^  (?<name>[A-Za-z0-9_-]+):\s*$", RegexOptions.CultureInvariant))
            .Where(m => m.Success)
            .Select(m => m.Groups["name"].Value)
            .ToList();
        Require(jobs.SequenceEqual(["verify", "publish", "publish-pypi"]), $"The jobs must be verify, publish, then publish-pypi (found: {string.Join(", ", jobs)}).");
        var verify = jobs.Contains("verify") ? Job(workflow, "verify") : string.Empty;
        var publish = jobs.Contains("publish") ? Job(workflow, "publish") : string.Empty;
        var pypi = jobs.Contains("publish-pypi") ? Job(workflow, "publish-pypi") : string.Empty;

        // Write permissions: exactly three in the whole file, two on the publish job and one on publish-pypi, and
        // exactly these.
        Require(
            WritePermission.Matches(workflow).Count == 3 && WritePermission.Matches(publish).Count == 2 && WritePermission.Matches(pypi).Count == 1,
            "The only write permissions must be the publish job's and the publish-pypi job's.");
        Require(
            JobPermissions(publish).Order(StringComparer.Ordinal).SequenceEqual(["contents: write", "id-token: write"]),
            "The publish job's permissions must be exactly `id-token: write` and `contents: write`.");
        Require(JobPermissions(pypi).SequenceEqual(["id-token: write"]), "The publish-pypi job's permissions must be exactly `id-token: write`.");
        Require(!Regex.IsMatch(workflow, @"^\s*packages\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant), "No job may be granted `packages`.");
        Require(!verify.Contains("permissions:", StringComparison.Ordinal), "The verify job must not be granted any permission.");

        // The publish jobs: after verify, behind their environment's reviewers, with no checkout of the tree.
        Require(publish.Contains("\n    needs: verify\n", StringComparison.Ordinal), "The publish job must `needs: verify`.");
        Require(publish.Contains("\n    environment: nuget-release\n", StringComparison.Ordinal), "The publish job must run in the `nuget-release` environment.");
        Require(!verify.Contains("environment:", StringComparison.Ordinal), "The verify job must not use a deployment environment.");
        foreach (var (name, job) in new[] { ("publish", publish), ("publish-pypi", pypi) })
        {
            Require(!job.Contains("actions/checkout", StringComparison.Ordinal), $"The {name} job must not check out the tree; it publishes what verify uploaded.");
            Require(!Regex.IsMatch(job, @"\b(dotnet (build|pack|restore)|uv (build|sync|run)|python -m build|go (build|test|vet))\b", RegexOptions.CultureInvariant), $"The {name} job must build nothing.");
        }

        Require(Regex.IsMatch(publish, @"uses: actions/download-artifact@\S+(?: #[^\n]*)?\n\s+with:\n\s+name: packages\n", RegexOptions.CultureInvariant), "The publish job must download the verified `packages` artifact.");

        // publish-pypi: only after the NuGet push succeeded, behind its own environment, publishing what verify built
        // through PyPI Trusted Publishing (no token, no password), safe to re-run.
        Require(pypi.Contains("\n    needs: [verify, publish]\n", StringComparison.Ordinal), "The publish-pypi job must `needs: [verify, publish]`.");
        Require(pypi.Contains("\n    environment: pypi-release\n", StringComparison.Ordinal), "The publish-pypi job must run in the `pypi-release` environment.");
        Require(
            Regex.Matches(workflow, @"\n\s+environment:", RegexOptions.CultureInvariant).Count == 2,
            "Only the two publish jobs may use a deployment environment.");
        Require(Regex.IsMatch(pypi, @"uses: actions/download-artifact@\S+(?: #[^\n]*)?\n\s+with:\n\s+name: python-dist\n", RegexOptions.CultureInvariant), "The publish-pypi job must download the verified `python-dist` artifact.");
        Require(
            Regex.IsMatch(pypi, @"\n\s+uses: pypa/gh-action-pypi-publish@[0-9a-f]{40} # v[0-9][^\n]*\n\s+with:\n\s+packages-dir: dist/\n\s+skip-existing: true\n", RegexOptions.CultureInvariant),
            "The publish-pypi job must publish dist/ with a SHA-pinned pypa/gh-action-pypi-publish and skip-existing: true.");
        Require(Regex.Matches(workflow, "pypa/gh-action-pypi-publish", RegexOptions.CultureInvariant).Count == 1, "There must be exactly one PyPI publish.");
        Require(!Regex.IsMatch(pypi, @"\b(password|user|repository-url|repository_url)\s*:", RegexOptions.CultureInvariant), "The PyPI publish must use Trusted Publishing: no password, user or other repository.");
        Require(!Regex.IsMatch(workflow, @"\b(uv publish|twine upload|git push|git tag)\b", RegexOptions.CultureInvariant), "Nothing may upload to PyPI, or push a tag, but the steps described here.");

        // Secrets: none but GITHUB_TOKEN, however spelled.
        var secrets = Regex.Matches(workflow, @"secrets\s*(?:\.\s*|\[\s*['""])(?<name>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant)
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .ToList();
        Require(secrets.All(name => name == "GITHUB_TOKEN"), $"No secret but GITHUB_TOKEN may be referenced (found: {string.Join(", ", secrets)}).");
        Require(!workflow.Contains("AGENTEXPERIENCE_ACCEPT_API_CHANGES", StringComparison.OrdinalIgnoreCase), "The API baseline's accept switch must not appear.");

        // Actions: third-party ones pinned to a full commit SHA with a version comment.
        foreach (Match use in Uses.Matches(workflow))
        {
            var action = use.Groups["action"].Value;
            var reference = use.Groups["ref"].Value;
            if (action.StartsWith("actions/", StringComparison.Ordinal))
            {
                Require(Regex.IsMatch(reference, @"^(v[0-9]+|[0-9a-f]{40})$", RegexOptions.CultureInvariant), $"{action}@{reference} must be a major tag or a SHA.");
            }
            else
            {
                Require(
                    Regex.IsMatch(reference, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) && Regex.IsMatch(use.Groups["comment"].Value, @"^\s+#\s*v[0-9]", RegexOptions.CultureInvariant),
                    $"{action}@{reference} must be pinned to a full commit SHA with a version comment.");
            }
        }

        // Story 22.7: in the jobs that hold publishing credentials, every action, first-party ones included, is pinned to
        // a full commit SHA with a version comment.
        foreach (var (name, job) in new[] { ("publish", publish), ("publish-pypi", pypi) })
        {
            var uses = Uses.Matches(job).ToList();
            Require(uses.Count > 0 && job.Split('\n').Count(line => Regex.IsMatch(line, @"^\s*(-\s+)?uses:", RegexOptions.CultureInvariant)) == uses.Count, $"Every action in the {name} job must be written as `uses: owner/action@ref`.");
            foreach (var use in uses)
            {
                Require(
                    Regex.IsMatch(use.Groups["ref"].Value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) && Regex.IsMatch(use.Groups["comment"].Value, @"^\s+#\s*v[0-9]", RegexOptions.CultureInvariant),
                    $"{use.Groups["action"].Value}@{use.Groups["ref"].Value} in the {name} job must be pinned to a full commit SHA with a version comment.");
            }
        }

        // Trusted Publishing: NuGet/login in the publish job only, as the nuget.org profile, and the key only through env.
        Require(Regex.IsMatch(publish, @"\n\s+id: login\n\s+uses: NuGet/login@[0-9a-f]{40} # v[0-9]", RegexOptions.CultureInvariant), "The publish job must log in with a SHA-pinned NuGet/login step whose id is `login`.");
        Require(publish.Contains("\n          user: fabbrik76\n", StringComparison.Ordinal), "NuGet/login's user must be the nuget.org profile fabbrik76.");
        Require(!verify.Contains("NuGet/login", StringComparison.Ordinal) && !verify.Contains("nuget push", StringComparison.OrdinalIgnoreCase), "The verify job must not log in or push.");
        Require(
            Regex.Matches(workflow, @"steps\.login\.outputs\.NUGET_API_KEY", RegexOptions.CultureInvariant).Count == 1
                && publish.Contains("\n          NUGET_API_KEY: ${{ steps.login.outputs.NUGET_API_KEY }}\n", StringComparison.Ordinal),
            "The key must reach the push only through the step's environment, never rendered into a script.");
        Require(!Regex.IsMatch(workflow, @"(echo|printf|cat)\b[^\n]*NUGET_API_KEY|set -x|set -o xtrace", RegexOptions.CultureInvariant), "Nothing may echo the key or trace the script.");

        const string Push = "dotnet nuget push \"artifacts/packages/*.nupkg\" --source https://api.nuget.org/v3/index.json --api-key \"$NUGET_API_KEY\" --skip-duplicate";
        Require(publish.Contains(Push, StringComparison.Ordinal), $"The publish job must run exactly: {Push}");
        Require(Regex.Matches(workflow, "nuget push", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count == 1, "There must be exactly one push.");

        // The Go tags: after the push, before the GitHub release, one per module at the verified version, created at the
        // tagged commit through the API (no checkout, no git push), idempotent only for a tag already at that commit, and
        // confirmed by the Go module proxy.
        const string GoModules = "for module in go go/inmemory go/postgres go/postgres/pgtest go/maf; do";
        const string CreateTag = "gh api \"repos/$GH_REPO/git/refs\" -f \"ref=refs/tags/$tag\" -f \"sha=$GITHUB_SHA\"";
        var tagsAt = publish.IndexOf(CreateTag, StringComparison.Ordinal);
        var proxyAt = publish.IndexOf("https://proxy.golang.org/$escaped/@v/$GO_VERSION.info", StringComparison.Ordinal);
        foreach (var (text, rule) in new[]
        {
            ("GO_VERSION: ${{ needs.verify.outputs.go-version }}", "take the Go version verify derived"),
            (GoModules, "tag exactly the five Go modules"),
            ("tag=\"$module/$GO_VERSION\"", "name each tag module/version"),
            ("gh api \"repos/$GH_REPO/git/ref/tags/$tag\"", "look each tag up first"),
            ("if [ \"$sha\" != \"$GITHUB_SHA\" ]; then", "refuse a tag that exists at another commit"),
            (CreateTag, "create each tag at the tagged commit through the API"),
            ("elif grep -q 'HTTP 404' \"$error\"; then", "create a tag only when the lookup says 404"),
            ("echo \"::error::Could not look up the tag $tag: $(cat \"$error\")\"", "fail on any other lookup error"),
            ("# tag-go-modules:begin\n", "mark the tag script, which the release tests run"),
            ("# tag-go-modules:end\n", "mark the tag script's end"),
            ("sed -E 's/([A-Z])/!\\L\\1/g'", "case-escape the module path for the proxy"),
            ("path=\"github.com/fabbrik/AgentExperience.NET/$module\"", "ask the proxy for the repository's module paths"),
            ("curl -fsS --connect-timeout 10 --max-time 30 \"$url\"", "bound each request to the proxy"),
            ("if [ \"$attempt\" -lt 10 ]; then sleep 30; fi", "not sleep after the last attempt"),
            // Worst case, the push to nuget.org plus 47.5 minutes of proxy retries.
            ("\n    timeout-minutes: 75\n", "allow the push and the whole proxy wait"),
        })
        {
            Require(publish.Contains(text, StringComparison.Ordinal), $"The publish job must {rule} (`{text}`).");
        }

        Require(
            Regex.Matches(publish, Regex.Escape(GoModules), RegexOptions.CultureInvariant).Count == 2,
            "The publish job must loop over exactly the five Go modules twice: once to tag them and once to ask the proxy.");

        Require(Regex.Matches(workflow, @"git/refs", RegexOptions.CultureInvariant).Count == 1, "Only the publish job's Go tag step may create a ref.");

        // The GitHub release: only after the push and the Go tags, idempotent on a re-run, a prerelease for a suffixed version.
        var pushAt = publish.IndexOf(Push, StringComparison.Ordinal);
        var viewAt = publish.IndexOf("if gh release view \"$TAG\"", StringComparison.Ordinal);
        var createAt = publish.IndexOf("gh release create \"$TAG\"", StringComparison.Ordinal);
        Require(pushAt >= 0 && tagsAt > pushAt && proxyAt > tagsAt && viewAt > proxyAt, "The Go modules must be tagged, and found on the proxy, after the push and before the GitHub release.");
        Require(pushAt >= 0 && viewAt > pushAt && createAt > viewAt, "The GitHub release must be looked up, then created, only after the push.");
        Require(publish.Contains("flags+=(--prerelease)", StringComparison.Ordinal) && publish.Contains("--notes-file artifacts/release-notes/notes.md", StringComparison.Ordinal), "The GitHub release must be a prerelease for a suffixed version, with the built notes.");

        // The verify job: every check before anything is uploaded.
        foreach (var (text, rule) in new[]
        {
            ("fetch-depth: 0", "check out the full history"),
            ("<VersionPrefix>", "read VersionPrefix"),
            ("<VersionSuffix>", "read VersionSuffix"),
            ("if [ \"$GITHUB_REF_NAME\" != \"v$version\" ]; then", "refuse a tag that is not v + the version"),
            ("if ! git merge-base --is-ancestor \"$GITHUB_SHA\" origin/main; then", "refuse a commit not on main"),
            ("'<VersionSuffix>preview\\.[1-9][0-9]*</VersionSuffix>'", "hold the version to preview while known limits remain"),
            (KnownLimitsCount, "count only the rows of the Known limits section of docs/known-limits.md, never a Documented boundaries row"),
            ("table { exit }' docs/known-limits.md; }", "quote both tables of docs/known-limits.md in the release notes"),
            ("\"rollForward\": \"disable\"", "pin the SDK exactly"),
            ("test \"$actual\" = \"$pinned\"", "assert the pinned SDK"),
            ("global-json-file: global.json", "install the SDK global.json pins"),
            ("run: dotnet restore --locked-mode\n", "restore in locked mode"),
            ("run: dotnet build --no-restore --configuration Release -p:AgentExperienceReleaseBuild=true\n", "build as a release build"),
            ("run: dotnet test --no-build --configuration Release\n", "run the full test suite"),
            ("dotnet pack --no-build --configuration Release --output artifacts/packages -p:AgentExperienceReleaseBuild=true", "pack to artifacts/packages"),
            ("run: dotnet run eng/verify-packages.cs -- artifacts/packages\n", "verify the packages"),
            // Story 22.7: one version for every language, and the Python and Go checks on the tagged commit.
            ("# derive-versions:begin\n", "derive the Python and Go versions in one function"),
            ("test \"$(derive_versions 0.1.0 preview.14)\" = \"0.1.0-preview.14 0.1.0a14 v0.1.0-preview.14\"", "test the derivation on a preview"),
            ("test \"$(derive_versions 1.0.0 '')\" = \"1.0.0 1.0.0 v1.0.0\"", "test the derivation on a version with no suffix"),
            ("if [ \"$declared\" != \"$python\" ]; then", "refuse a pyproject.toml version that is not the derived one"),
            ("if [ \"$required\" != \"$go\" ]; then", "refuse a Go module requirement that is not the derived version"),
            ("if [ \"$found\" != \"$expected\" ]; then", "refuse a Go module that does not require exactly its siblings"),
            ("has no replace for github.com/fabbrik/AgentExperience.NET/$sibling", "refuse a Go module without a replace for each sibling"),
            ("uv sync --locked --extra maf --extra postgres\n", "install the Python project from its lock file"),
            ("uv run pytest -m \"not postgres\"\n", "run the Python tests"),
            ("uv build --out-dir dist\n", "build the Python sdist and wheel"),
            ("rm -f dist/.gitignore\n", "drop the .gitignore uv writes into the output directory"),
            ("(cd dist && sha256sum -- *.whl *.tar.gz) > \"$RUNNER_TEMP/python-dist.sha256\"", "record the built files' hashes"),
            ("sha256sum --check --strict \"$RUNNER_TEMP/python-dist.sha256\"", "check the hashes again before the upload"),
            (" check --strict dist/*.whl dist/*.tar.gz\n", "check the Python distribution as PyPI would, with twine check --strict"),
            ("if [ \"$wheels\" -ne 1 ] || [ \"$sdists\" -ne 1 ]; then", "require exactly one wheel and one sdist"),
            ("            python/dist/*.whl\n            python/dist/*.tar.gz\n", "upload only the wheel and the sdist"),
            ("retention-days: 90\n", "keep the Python distribution as long as GitHub allows"),
            ("grep -qx \"Name: agentexperience\"", "check the wheel names agentexperience"),
            ("grep -qx \"Version: $PYTHON_VERSION\"", "check the wheel carries the derived version"),
            ("PYTHON_VERSION: ${{ steps.version.outputs.python-version }}", "check the distribution against the derived Python version"),
            ("for module in . inmemory postgres postgres/pgtest maf; do", "check every Go module"),
            ("go vet ./...\n", "vet the Go modules"),
            ("go test ./...\n", "test the Go modules"),
        })
        {
            Require(verify.Contains(text, StringComparison.Ordinal), $"The verify job must {rule} (`{text}`).");
        }

        // The verify job publishes nothing.
        Require(
            !Regex.IsMatch(verify, @"gh-action-pypi-publish|uv publish|twine upload|git/refs|gh release|nuget push", RegexOptions.CultureInvariant),
            "The verify job must publish nothing.");
        Require(Regex.IsMatch(verify, @"\n\s+uses: astral-sh/setup-uv@[0-9a-f]{40} # v[0-9][^\n]*\n\s+with:\n\s+version: '[0-9]+\.[0-9]+\.[0-9]+'\n", RegexOptions.CultureInvariant), "The verify job must install a pinned uv through a SHA-pinned astral-sh/setup-uv.");
        Require(!Regex.IsMatch(workflow, @"enable-cache: true|cache: true", RegexOptions.CultureInvariant), "No release job may restore a cache.");
        Require(Regex.IsMatch(verify, @"\n\s+uvx --no-cache twine==[0-9.]+ check --strict dist/\*\.whl dist/\*\.tar\.gz\n", RegexOptions.CultureInvariant), "The verify job must run a pinned twine's check --strict on the wheel and the sdist.");
        var pythonDistAt = verify.IndexOf(" check --strict dist/*", StringComparison.Ordinal);
        var pythonUploadAt = verify.IndexOf("name: python-dist", StringComparison.Ordinal);
        Require(pythonDistAt >= 0 && pythonUploadAt > pythonDistAt, "The Python distribution must be uploaded only after it is checked.");

        // The hashes are recorded right after the build, and checked again after everything else has run (twine, Go)
        // and before the first upload.
        var buildAt = verify.IndexOf("uv build --out-dir dist", StringComparison.Ordinal);
        var recordAt = verify.IndexOf("sha256sum -- *.whl *.tar.gz", StringComparison.Ordinal);
        var twineAt = verify.IndexOf("twine==", StringComparison.Ordinal);
        var goTestAt = verify.IndexOf("go test ./...", StringComparison.Ordinal);
        var recheckAt = verify.IndexOf("sha256sum --check --strict", StringComparison.Ordinal);
        var firstUploadAt = verify.IndexOf("actions/upload-artifact", StringComparison.Ordinal);
        Require(
            buildAt >= 0 && recordAt > buildAt && twineAt > recordAt && goTestAt > twineAt && recheckAt > goTestAt && firstUploadAt > recheckAt
                // Between the re-check and the first upload, only the upload step's own start: no other step.
                && Regex.Matches(verify[recheckAt..firstUploadAt], @"\n      - ", RegexOptions.CultureInvariant).Count == 1,
            "The Python files' hashes must be recorded right after the build and re-checked as the last step before the uploads.");

        // net10.0 is the only target framework, and the pinned SDK carries its runtime: another SDK or runtime installed
        // beside it would be one nothing needs (SupportMatrixTests requires one again for any framework added later).
        Require(!verify.Contains("dotnet-version:", StringComparison.Ordinal), "The verify job must install only the SDK global.json pins, with no dotnet-version beside it.");

        var verifiedAt = verify.IndexOf("dotnet run eng/verify-packages.cs", StringComparison.Ordinal);
        var uploadAt = verify.IndexOf("actions/upload-artifact", StringComparison.Ordinal);
        Require(verifiedAt >= 0 && uploadAt > verifiedAt, "The packages must be uploaded only after they are verified.");
        Require(!workflow.Contains("continue-on-error", StringComparison.Ordinal), "No release step may be allowed to fail.");

        return violations;
    }

    /// <summary>A top-level block's significant lines (no blank or comment lines), from its key to the next top-level key.</summary>
    private static List<string> TopLevelBlock(string[] lines, string key)
    {
        var block = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                if (inside)
                {
                    break;
                }

                inside = line.Split(':')[0].Trim('"', '\'') == key;
            }

            if (inside)
            {
                block.Add(line.TrimEnd());
            }
        }

        return block;
    }

    /// <summary>The scopes a job's own <c>permissions:</c> block grants, as trimmed <c>scope: level</c> lines.</summary>
    private static List<string> JobPermissions(string job)
    {
        var lines = job.Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == "    permissions:");
        return start < 0
            ? []
            : lines.Skip(start + 1).TakeWhile(line => line.StartsWith("      ", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
    }
}
