using System.Text.RegularExpressions;

namespace AgentExperience.Release.Tests.Workflows;

/// <summary>
/// Story 4.3, frozen rule 3: nothing is published by automation. Pushing a package to NuGet is the
/// maintainer's manual step in <c>RELEASING.md</c>, so no workflow in this repository may carry a step,
/// a secret, or a permission that would let one fire on its own -- and the MAF compatibility probe's
/// floating leg must stay non-blocking (AD-F). Story 6.3 adds the floating-dependency leg, which gates
/// outside pull requests.
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

    public static TheoryData<string> Workflows()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(RepositoryRoot.Path, ".github", "workflows")).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_workflow_can_publish_a_package(string workflow)
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
    public void The_CI_workflow_packs_verifies_and_probes_MAF_with_only_the_latest_leg_non_blocking()
    {
        var ci = File.ReadAllText(Path.Combine(RepositoryRoot.Path, ".github", "workflows", "ci.yml"));

        Assert.Contains("permissions:\n  contents: read", ci.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("dotnet pack", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet run eng/verify-packages.cs -- artifacts/packages", ci, StringComparison.Ordinal);
        Assert.Contains("tests/AgentExperience.Release.Tests", ci, StringComparison.Ordinal);
        Assert.Contains("leg: [pinned, latest]", ci, StringComparison.Ordinal);
        Assert.Contains("continue-on-error: ${{ matrix.leg == 'latest' }}", ci, StringComparison.Ordinal);
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
}
