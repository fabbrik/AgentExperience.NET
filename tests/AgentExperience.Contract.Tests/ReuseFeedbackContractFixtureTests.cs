using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.17's in-memory checks: every <c>reuse-feedback</c> fixture is what .NET's <see cref="ExperienceReuseFeedbackService"/>
/// leaves over the .NET in-memory stores, every <c>finalize-reuse-evidence</c> fixture what .NET's
/// <see cref="ExperienceFinalizationService"/> reports with reuse evidence on, and the cases reach every outcome and
/// disposition they claim to.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_reuse_feedback_fixture_is_what_the_dotnet_feedback_service_leaves() =>
        VerifyReuseFamily(ReuseFeedbackContract.Family, ReuseFeedbackContract.Cases, ReuseFeedbackContract.Expected);

    [Fact]
    public void Every_finalize_reuse_evidence_fixture_is_what_the_dotnet_finalization_service_reports() =>
        VerifyReuseFamily(ReuseFeedbackContract.FinalizeFamily, ReuseFeedbackContract.FinalizeCases, ReuseFeedbackContract.FinalizeExpected);

    [Fact]
    public void The_reuse_feedback_cases_reach_every_outcome_and_disposition()
    {
        var outcomes = new List<string>();
        var dispositions = new List<string>();
        var stores = new List<string>();
        var thrown = 0;
        foreach (var (_, input) in ReuseFeedbackContract.Cases)
        {
            var expected = ReuseFeedbackContract.Expected(input);
            var steps = input["steps"]!.AsArray();
            for (var i = 0; i < steps.Count; i++)
            {
                var result = expected["results"]![i]!;
                if (steps[i]!["submit"] is not null)
                {
                    if (result["thrown"] is not null)
                    {
                        thrown++;
                        continue;
                    }

                    outcomes.Add(result["outcome"]!.GetValue<string>());
                    dispositions.AddRange(result["exposures"]!.AsArray().Select(exposure => exposure!["disposition"]!.GetValue<string>()));
                }
                else if (steps[i]!["record"] is not null)
                {
                    stores.Add(result["outcome"]!.GetValue<string>());
                }
            }
        }

        Assert.Equal(Enum.GetNames<ExperienceReuseFeedbackOutcome>().Order(StringComparer.Ordinal), outcomes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetNames<ExperienceExposureDisposition>().Order(StringComparer.Ordinal), dispositions.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetNames<ExperienceReuseFeedbackStoreOutcome>().Order(StringComparer.Ordinal), stores.Distinct().Order(StringComparer.Ordinal));
        Assert.True(thrown > 0, "No case throws from the ledger.");

        var finalized = new List<string>();
        foreach (var (_, input) in ReuseFeedbackContract.FinalizeCases)
        {
            var expected = ReuseFeedbackContract.FinalizeExpected(input);
            foreach (var result in expected["results"]!.AsArray())
            {
                Assert.True(result!["outcome"]!.GetValue<string>() is "Validated" or "Quarantined" or "AlreadyFinalized", result.ToJsonString());
                finalized.AddRange(result["reuseEvidence"]!.AsArray().Select(entry =>
                    entry!["skipped"]!.GetValue<bool>() ? "Skipped"
                    : entry["replay"]!.GetValue<bool>() ? "Replay"
                    : entry["exceptionType"] is not null ? "Threw"
                    : entry["outcome"]!.GetValue<string>()));
            }
        }

        Assert.Equal(
            ["Applied", "Ineligible", "Invalid", "Replay", "Skipped", "Threw"],
            finalized.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_reuse_feedback_statements_are_the_dotnet_postgres_ledgers_own()
    {
        var drift = new List<string>();
        ContractFiles.Verify(ReuseFeedbackSqlContract.File, ReuseFeedbackSqlContract.Render(), drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    private static void VerifyReuseFamily(string family, IReadOnlyList<(string Name, JsonObject Input)> cases, Func<JsonObject, JsonObject> expected)
    {
        var drift = new List<string>();
        foreach (var (name, input) in cases)
        {
            var directory = $"fixtures/{family}/{name}";
            var inputJson = ContractJson.Write(input);
            ContractFiles.Verify($"{directory}/input.json", inputJson, drift);

            // The input as checked in, read as a port reads it.
            var read = JsonNode.Parse(ContractFiles.Read($"{directory}/input.json") ?? inputJson)!.AsObject();
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(expected(read)), drift);
        }

        ContractFiles.VerifyNoStray(family, cases.Select(entry => entry.Name), ReuseFeedbackContract.CaseFiles, drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }
}
