using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Workflows;

/// <summary>
/// Story 4.3, frozen rule 3, as amended by the trusted-publishing decision: nothing publishes a package except
/// <c>release.yml</c>, and it only under the constraints <see cref="ReleaseViolations"/> enforces (a pushed version
/// tag as its only trigger, a publish job gated by the <c>nuget-release</c> environment's reviewers, a short-lived
/// nuget.org key from NuGet Trusted Publishing, and no stored secret). Every other workflow may carry no step, secret
/// or permission that would let a publish fire. The MAF compatibility probe's latest leg was non-blocking (AD-F);
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

    [Theory]
    [InlineData("permissions: write-all")]
    [InlineData("permissions:\n  contents: write")]
    [InlineData("permissions:\n  packages: \"write\"")]
    [InlineData("permissions:\n  id-token: 'write'")]
    [InlineData("env:\n  AGENTEXPERIENCE_ACCEPT_API_CHANGES: true")]
    [InlineData("run: echo ${{ secrets.ANYTHING }}")]
    [InlineData("run: dotnet nuget push x.nupkg")]
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
        })
        {
            Assert.Contains(project, tokens);
        }

        Assert.Contains("dotnet test \"$project\" --no-build --configuration Release", job, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);

        // Story 6.4 (KL-2): the store and vector suites run a second time, in crypto-shredding mode, on every leg.
        Assert.Contains("AGENTEXPERIENCE_TEST_ENCRYPTION: 'on'", job, StringComparison.Ordinal);
        var encrypted = job[job.IndexOf("AGENTEXPERIENCE_TEST_ENCRYPTION", StringComparison.Ordinal)..];
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Tests", encrypted, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Storage.Postgres.Vectors.Tests", encrypted, StringComparison.Ordinal);
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
    /// The production-readiness gate counts the rows of the README's <c>## Known limits</c> section and nothing else:
    /// a Documented boundaries row keeps its KL number and must not count. RELEASING.md step 9 and release.yml run
    /// the same count, so the gate a maintainer runs by hand is the gate the tag runs.
    /// </summary>
    private const string KnownLimitsCount = "awk '/^## / { section = ($0 == \"## Known limits\") } section' README.md | grep -cE '^\\| KL-[0-9]+ \\|'";

    [Fact]
    public void The_gate_counts_only_the_Known_limits_table_in_RELEASING_and_in_release_yml()
    {
        var releasing = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "RELEASING.md")).ReplaceLineEndings("\n");

        Assert.Contains(KnownLimitsCount, releasing, StringComparison.Ordinal);
        Assert.Contains(KnownLimitsCount, ReadWorkflow(ReleaseWorkflow), StringComparison.Ordinal);
    }

    /// <summary>
    /// The README's two tables keep their shape: a known limit is a three-cell row (number, limit, detail) under
    /// <c>## Known limits</c>, and a documented boundary is a five-cell row under <c>## Documented boundaries</c> whose
    /// boundary, reason no code change can remove it, response and detail are all filled in. A boundary without a
    /// written reason is a known limit, and fails here.
    /// </summary>
    [Fact]
    public void The_README_separates_known_limits_from_documented_boundaries_and_each_boundary_has_its_reason()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "README.md")).ReplaceLineEndings("\n");

        var limits = SectionRows(readme, "## Known limits");
        var boundaries = SectionRows(readme, "## Documented boundaries");

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
        Assert.True(start >= 0, $"README.md has no '{heading}' section.");

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
    /// the same action; the artifact download follows the upload's major.
    /// </summary>
    [Fact]
    public void The_release_workflow_uses_the_same_first_party_actions_as_CI()
    {
        var ci = UsesRefs(ReadWorkflow("ci.yml"));
        var release = UsesRefs(ReadWorkflow(ReleaseWorkflow));

        foreach (var (action, reference) in release.Where(pair => pair.Action.StartsWith("actions/", StringComparison.Ordinal)))
        {
            var expected = action == "actions/download-artifact"
                ? ci.First(pair => pair.Action == "actions/upload-artifact").Ref
                : ci.FirstOrDefault(pair => pair.Action == action).Ref;
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
    [InlineData("awk '/^## / { section = ($0 == \"## Known limits\") } section' README.md | grep -cE", "grep -cE")]
    [InlineData("dotnet run eng/verify-packages.cs -- artifacts/packages", "echo skipped")]
    [InlineData("    steps:\n      - name: Download the verified packages", "    steps:\n      - uses: actions/checkout@v5\n      - name: Download the verified packages")]
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

        // Exactly two jobs.
        var jobs = TopLevelBlock(lines, "jobs")
            .Select(line => Regex.Match(line, @"^  (?<name>[A-Za-z0-9_-]+):\s*$", RegexOptions.CultureInvariant))
            .Where(m => m.Success)
            .Select(m => m.Groups["name"].Value)
            .ToList();
        Require(jobs.SequenceEqual(["verify", "publish"]), $"The jobs must be verify, then publish (found: {string.Join(", ", jobs)}).");
        var verify = jobs.Contains("verify") ? Job(workflow, "verify") : string.Empty;
        var publish = jobs.Contains("publish") ? Job(workflow, "publish") : string.Empty;

        // Write permissions: exactly two in the whole file, both on the publish job, and exactly these.
        Require(
            WritePermission.Matches(workflow).Count == 2 && WritePermission.Matches(publish).Count == 2,
            "The only write permissions must be the publish job's.");
        Require(
            JobPermissions(publish).Order(StringComparer.Ordinal).SequenceEqual(["contents: write", "id-token: write"]),
            "The publish job's permissions must be exactly `id-token: write` and `contents: write`.");
        Require(!Regex.IsMatch(workflow, @"^\s*packages\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant), "No job may be granted `packages`.");
        Require(!verify.Contains("permissions:", StringComparison.Ordinal), "The verify job must not be granted any permission.");

        // The publish job: after verify, behind the environment's reviewers, with no checkout of the tree.
        Require(publish.Contains("\n    needs: verify\n", StringComparison.Ordinal), "The publish job must `needs: verify`.");
        Require(publish.Contains("\n    environment: nuget-release\n", StringComparison.Ordinal), "The publish job must run in the `nuget-release` environment.");
        Require(!verify.Contains("environment:", StringComparison.Ordinal), "The verify job must not use a deployment environment.");
        Require(!publish.Contains("actions/checkout", StringComparison.Ordinal), "The publish job must not check out the tree; it publishes what verify uploaded.");
        Require(Regex.IsMatch(publish, @"uses: actions/download-artifact@\S+\n\s+with:\n\s+name: packages\n", RegexOptions.CultureInvariant), "The publish job must download the verified `packages` artifact.");

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

        // The GitHub release: only after the push, idempotent on a re-run, a prerelease for a suffixed version.
        var pushAt = publish.IndexOf(Push, StringComparison.Ordinal);
        var viewAt = publish.IndexOf("if gh release view \"$TAG\"", StringComparison.Ordinal);
        var createAt = publish.IndexOf("gh release create \"$TAG\"", StringComparison.Ordinal);
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
            (KnownLimitsCount, "count only the rows of the README's Known limits section, never a Documented boundaries row"),
            ("\"rollForward\": \"disable\"", "pin the SDK exactly"),
            ("test \"$actual\" = \"$pinned\"", "assert the pinned SDK"),
            ("8.0.x", "install the 8.0 runtime"),
            ("9.0.x", "install the 9.0 runtime"),
            ("run: dotnet restore --locked-mode\n", "restore in locked mode"),
            ("run: dotnet build --no-restore --configuration Release -p:AgentExperienceReleaseBuild=true\n", "build as a release build"),
            ("run: dotnet test --no-build --configuration Release\n", "run the full test suite"),
            ("dotnet pack --no-build --configuration Release --output artifacts/packages -p:AgentExperienceReleaseBuild=true", "pack to artifacts/packages"),
            ("run: dotnet run eng/verify-packages.cs -- artifacts/packages\n", "verify the packages"),
        })
        {
            Require(verify.Contains(text, StringComparison.Ordinal), $"The verify job must {rule} (`{text}`).");
        }

        var verifiedAt = verify.IndexOf("dotnet run eng/verify-packages.cs", StringComparison.Ordinal);
        var uploadAt = verify.IndexOf("actions/upload-artifact", StringComparison.Ordinal);
        Require(verifiedAt >= 0 && uploadAt > verifiedAt, "The packages must be uploaded only after they are verified.");
        Require(!verify.Contains("continue-on-error", StringComparison.Ordinal) && !publish.Contains("continue-on-error", StringComparison.Ordinal), "No release step may be allowed to fail.");

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
