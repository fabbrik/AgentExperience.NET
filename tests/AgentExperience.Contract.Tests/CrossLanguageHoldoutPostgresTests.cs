using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 25.6's cross-language proof, the .NET half: a mixed fleet measures one benefit. A Python MAF agent and a Go MAF
/// agent, each at a holdout rate of 0.5 with the default salt and in the same scope, ran a learning run and then eight
/// measured runs through their own PostgreSQL stores, and finalized every one; each port's tests check that the rows it
/// wrote (records, initial lifecycle events and holdout assignments, every column in PostgreSQL's text form) are exactly
/// its checked-in rows file. Here both files' rows go into one database, and the .NET estimate over that scope must count
/// every finalized run in the arm the assignment function gives it. Contract 1.9: each port also estimated the same rows
/// itself, split by model, and the .NET estimate must equal both.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageHoldoutPostgresTests(ContractPostgresDatabase database)
{
    private const double Rate = 0.5;

    private static readonly Scope FleetScope = new("cross-language-holdout", "holdout-app", "holdout-project", AgentId: "holdout-agent");

    private static readonly string Repository = Path.GetDirectoryName(ContractFiles.Root)!;

    private static readonly (string Port, string Path)[] RowsFiles =
    [
        ("python", Path.Combine(Repository, "python", "tests", "cross-language", "python-agent-holdout-rows.json")),
        ("go", Path.Combine(Repository, "go", "maf", "testdata", "cross-language", "go-agent-holdout-rows.json")),
    ];

    [Fact]
    public async Task The_dotnet_estimate_counts_every_finalized_python_and_go_run_in_its_arm()
    {
        var application = await database.CreateAsync("xl_holdout");
        var expected = new Dictionary<string, Counts>(StringComparer.Ordinal) { ["Treatment"] = new(), ["Holdout"] = new() };
        var problems = new List<string>();

        foreach (var (port, path) in RowsFiles)
        {
            Assert.True(File.Exists(path), $"{path} is missing.");
            var rows = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var records = Rows(rows, "experience_records");
            var events = Rows(rows, "lifecycle_events");
            var assignments = Rows(rows, "experience_holdout_assignments");
            Assert.NotEmpty(records);
            Assert.NotEmpty(assignments);

            foreach (var row in records)
            {
                await PortAgentRowsPostgres.InsertRawAsync(application, "experience_records", row);
            }

            foreach (var row in events)
            {
                await PortAgentRowsPostgres.InsertRawAsync(application, "lifecycle_events", row);
            }

            foreach (var row in assignments)
            {
                await PortAgentRowsPostgres.InsertRawAsync(application, "experience_holdout_assignments", row);
            }

            // What the estimate must find for this port's runs, worked out from the rows alone: each run's arm (one per run,
            // the one the assignment function gives), and its own record's verified outcome.
            var arms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in assignments.GroupBy(row => row["run_id"]!.GetValue<string>()))
            {
                var runId = Guid.Parse(run.Key);
                var arm = run.Select(row => row["arm"]!.GetValue<string>()).Distinct().Single();
                var assigned = HoldoutAssignmentFunction.Assign(runId, ExperienceHoldoutOptions.DefaultSalt, Rate).ToString();
                if (arm != assigned)
                {
                    problems.Add($"{port} run {runId}: recorded in {arm}, but the assignment function puts it in {assigned}.");
                }

                arms.Add(arm);
                Assert.All(run, row => Assert.Equal(FleetScope, PortAgentRowsPostgres.ScopeOf(row)));
                var own = records.Where(row => row["source_run_id"]!.GetValue<string>() == run.Key).ToList();
                if (own.Count != 1)
                {
                    problems.Add($"{port} run {runId}: {own.Count} records name it as their run, not 1: every measured run must be finalized.");
                    continue;
                }

                var status = JsonNode.Parse(own[0]["payload"]!.GetValue<string>())!["outcome"]!["status"]!.GetValue<string>();
                expected[arm].Add(status);
            }

            if (!arms.SetEquals(["Treatment", "Holdout"]))
            {
                problems.Add($"{port}: its runs were assigned to [{string.Join(", ", arms)}], not to both arms.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var services = new ServiceCollection();
        services.AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(null))
            .UsePostgres(application);
        await using var provider = services.BuildServiceProvider();
        var estimate = await AgentExperienceBenefit.EstimateAsync(provider, SearchContractPostgresTests.Authorization(FleetScope), FleetScope);

        Assert.Equal(ExperienceStoreOutcome.Found, estimate.Outcome);
        Assert.True(estimate.Tracked);
        var overall = estimate.Overall;
        Assert.Equal(0, overall.Pending);
        Assert.Equal(0, overall.MixedArm);
        Assert.Equal(expected["Treatment"].Unverified + expected["Holdout"].Unverified, overall.Unverified);
        Assert.Equal(new ExperienceBenefitArm(expected["Treatment"].Runs, expected["Treatment"].Successes), overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(expected["Holdout"].Runs, expected["Holdout"].Successes), overall.Holdout);

        // Every finalized run of both ports is counted once, in its arm.
        Assert.Equal(
            expected.Values.Sum(counts => counts.Runs + counts.Unverified),
            overall.Treatment.Runs + overall.Holdout.Runs + overall.Unverified);
        Assert.NotEmpty(estimate.Lessons);

        // Contract 1.9: each port estimated the same rows itself, split by model, and checked in what it computed. The
        // .NET estimate over the same scope must be that estimate, field for field.
        var split = await AgentExperienceBenefit.EstimateAsync(
            provider,
            SearchContractPostgresTests.Authorization(FleetScope),
            FleetScope,
            new ExperienceBenefitOptions { SplitByModel = true });
        var dotnet = BenefitEstimateContract.EstimateJson(split);
        dotnet["tracked"] = split.Tracked;
        dotnet["outcome"] = split.Outcome.ToString();
        Assert.Contains(split.ByConsumingModel!, model => model.Model is "python-agent-model");
        Assert.Contains(split.ByConsumingModel!, model => model.Model is "go-agent-model");
        foreach (var (port, path) in EstimateFiles)
        {
            Assert.True(File.Exists(path), $"{path} is missing.");
            var computed = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var difference = Difference(dotnet, computed, "$");
            Assert.True(difference is null, $"{port}'s estimate of the fleet differs from .NET's at {difference}.");
        }
    }

    private static double Number(JsonValue value) =>
        double.Parse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    private static readonly (string Port, string Path)[] EstimateFiles =
    [
        ("python", Path.Combine(Repository, "python", "tests", "cross-language", "python-fleet-holdout-estimate.json")),
        ("go", Path.Combine(Repository, "go", "maf", "testdata", "cross-language", "go-fleet-holdout-estimate.json")),
    ];

    /// <summary>
    /// The first place two estimates differ, or <see langword="null"/>: numbers that are not whole within the contract's
    /// tolerance (1e-12), everything else exactly.
    /// </summary>
    private static string? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (JsonObject e, JsonObject a):
                if (!e.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(a.Select(p => p.Key).Order(StringComparer.Ordinal)))
                {
                    return $"{path} (members)";
                }

                return e.Select(p => Difference(p.Value, a[p.Key], $"{path}.{p.Key}")).FirstOrDefault(d => d is not null);
            case (JsonArray e, JsonArray a):
                return e.Count != a.Count ? $"{path} (length)" : e.Select((item, i) => Difference(item, a[i], $"{path}[{i}]")).FirstOrDefault(d => d is not null);
            case (JsonValue e, JsonValue a) when e.GetValueKind() == System.Text.Json.JsonValueKind.Number && a.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                return Math.Abs(Number(e) - Number(a)) <= 1e-12 ? null : path;
            default:
                return JsonNode.DeepEquals(expected, actual) ? null : path;
        }
    }

    private static List<JsonObject> Rows(JsonObject rows, string table) =>
        rows[table] is JsonArray array ? [.. array.Select(row => row!.AsObject())] : throw new Xunit.Sdk.XunitException($"rows file: {table} is not an array.");

    /// <summary>One arm's expected counts: runs with a verified or failed outcome, the verified ones, and the unknown ones.</summary>
    private sealed class Counts
    {
        public int Runs { get; private set; }

        public int Successes { get; private set; }

        public int Unverified { get; private set; }

        public void Add(string status)
        {
            switch (status)
            {
                case nameof(TaskVerificationStatus.Verified):
                    Runs++;
                    Successes++;
                    break;
                case nameof(TaskVerificationStatus.Failed):
                    Runs++;
                    break;
                default:
                    Unverified++;
                    break;
            }
        }
    }
}
