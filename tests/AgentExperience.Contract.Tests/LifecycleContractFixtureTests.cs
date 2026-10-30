using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.16's in-memory checks: every <c>lifecycle-commit</c> and <c>lifecycle-apply</c> fixture is what .NET's
/// <see cref="ExperienceLifecycleService"/> leaves over the .NET in-memory store, and the cases reach every outcome,
/// transition and refusal they claim to.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_lifecycle_commit_fixture_is_what_the_dotnet_lifecycle_service_leaves() =>
        VerifyLifecycleFamily(LifecycleContract.CommitFamily, LifecycleContract.CommitCases);

    [Fact]
    public void Every_lifecycle_apply_fixture_is_what_the_dotnet_lifecycle_service_leaves() =>
        VerifyLifecycleFamily(LifecycleContract.ApplyFamily, LifecycleContract.ApplyCases);

    [Fact]
    public void The_lifecycle_cases_reach_every_transition_outcome_and_refusal()
    {
        var commits = Outcomes(LifecycleContract.CommitCases, "commit");
        Assert.Equal(
            Enum.GetNames<LifecycleTransitionOutcome>().Where(name => name != nameof(LifecycleTransitionOutcome.Deleted)).Order(StringComparer.Ordinal),
            commits.Distinct().Order(StringComparer.Ordinal));

        var applies = Outcomes(LifecycleContract.ApplyCases, "apply");
        Assert.Equal(
            ["Applied", "Conflict", "Denied", "Ineligible", "Invalid", "NotFound", "Unverified"],
            applies.Distinct().Order(StringComparer.Ordinal));

        Assert.Equal(
            ["Allowed", "Cycle", "Denied", "Invalid", "RecordNotFound", "ReplacementNotFound"],
            Outcomes(LifecycleContract.CommitCases, "checkSupersession").Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Denied", "Found", "Invalid", "NotFound"],
            Outcomes(LifecycleContract.ApplyCases, "readConfidence").Distinct().Order(StringComparer.Ordinal));

        // Every allowed transition commits, from every status, and every other one is refused by the table.
        var statuses = Enum.GetValues<ExperienceStatus>();
        var committed = new HashSet<(string, string)>();
        var refused = new HashSet<(string, string)>();
        foreach (var (name, input) in LifecycleContract.CommitCases.Where(entry => entry.Name.StartsWith("allowed-from-", StringComparison.Ordinal) || entry.Name.StartsWith("refused-from-", StringComparison.Ordinal)))
        {
            var expected = LifecycleContract.Expected(input);
            var steps = input["steps"]!.AsArray();
            for (var i = 0; i < steps.Count; i++)
            {
                var request = steps[i]!["commit"]!["request"]!;
                var pair = (request["priorStatus"]!.GetValue<string>(), request["currentStatus"]!.GetValue<string>());
                var outcome = expected["results"]![i]!["outcome"]!.GetValue<string>();
                (outcome == "Committed" ? committed : outcome == "TransitionNotAllowed" ? refused : throw new InvalidOperationException($"{name}: {outcome}")).Add(pair);
            }
        }

        foreach (var from in statuses)
        {
            foreach (var to in statuses)
            {
                Assert.Contains((from.ToString(), to.ToString()), ExperienceLifecycleService.IsTransitionAllowed(from, to) ? committed : refused);
            }
        }

        // The 1.15 apply cases the service can express now come from it.
        foreach (var (_, input) in ConfidenceEngineContract.Cases)
        {
            ConfidenceEngineContract.Expected(input);
        }

        Assert.True(ConfidenceEngineContract.ServedByTheService.Distinct().Count() >= 15, $"Only {ConfidenceEngineContract.ServedByTheService.Distinct().Count()} 1.15 apply cases came from the service.");
    }

    [Fact]
    public void The_lifecycle_statements_are_the_dotnet_postgres_stores_own()
    {
        var drift = new List<string>();
        ContractFiles.Verify(LifecycleSqlContract.File, LifecycleSqlContract.Render(), drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    private static List<string> Outcomes(IReadOnlyList<(string Name, JsonObject Input)> cases, string operation)
    {
        var outcomes = new List<string>();
        foreach (var (_, input) in cases)
        {
            var expected = LifecycleContract.Expected(input);
            var steps = input["steps"]!.AsArray();
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i]![operation] is not null && expected["results"]![i]!["outcome"] is { } outcome)
                {
                    outcomes.Add(outcome.GetValue<string>());
                }
            }
        }

        return outcomes;
    }

    private static void VerifyLifecycleFamily(string family, IReadOnlyList<(string Name, JsonObject Input)> cases)
    {
        var drift = new List<string>();
        foreach (var (name, input) in cases)
        {
            var directory = $"fixtures/{family}/{name}";
            var inputJson = ContractJson.Write(input);
            ContractFiles.Verify($"{directory}/input.json", inputJson, drift);

            // The input as checked in, read as a port reads it.
            var read = JsonNode.Parse(ContractFiles.Read($"{directory}/input.json") ?? inputJson)!.AsObject();
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(LifecycleContract.Expected(read)), drift);
        }

        ContractFiles.VerifyNoStray(family, cases.Select(entry => entry.Name), LifecycleContract.CaseFiles, drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }
}
