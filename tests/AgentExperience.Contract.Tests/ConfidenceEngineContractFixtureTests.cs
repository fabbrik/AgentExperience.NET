using System.Text.Json.Nodes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.15's checks, which need no database: every <c>confidence-engine</c> fixture is what .NET's confidence engine
/// classes give for its input, and the cases reach every rule, boundary and refusal they have.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_confidence_engine_fixture_is_what_the_confidence_engine_gives()
    {
        var drift = new List<string>();
        foreach (var (name, input) in ConfidenceEngineContract.Cases)
        {
            var directory = $"fixtures/{ConfidenceEngineContract.Family}/{name}";
            var inputJson = ContractJson.Write(input);
            ContractFiles.Verify($"{directory}/input.json", inputJson, drift);

            // The input as checked in, read as a port reads it.
            var read = JsonNode.Parse(ContractFiles.Read($"{directory}/input.json") ?? inputJson)!.AsObject();
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(ConfidenceEngineContract.Expected(read)), drift);
        }

        ContractFiles.VerifyNoStray(
            ConfidenceEngineContract.Family,
            ConfidenceEngineContract.Cases.Select(entry => entry.Name),
            ConfidenceEngineContract.CaseFiles,
            drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_confidence_engine_cases_reach_every_rule_and_refusal()
    {
        var results = ConfidenceEngineContract.Cases.ToDictionary(
            entry => entry.Name,
            entry => (Input: entry.Input, Expected: ConfidenceEngineContract.Expected(entry.Input)));

        string? Refused(JsonObject expected, string member = "refusal") =>
            expected[member] is JsonObject refusal ? $"{refusal["exception"]}:{refusal["parameter"]}" : null;

        // Score: the prior, the boundaries, and each refusal naming its parameter.
        Assert.Equal(0.5, results["score-prior"].Expected["score"]!.GetValue<double>());
        Assert.Equal(2d / 3d, results["score-initial-validation"].Expected["score"]!.GetValue<double>());
        Assert.True(results["score-largest-support"].Expected["score"]!.GetValue<double>() < 1d);
        Assert.Equal("ArgumentOutOfRangeException:supportingValidations", Refused(results["score-both-negative"].Expected));
        Assert.Equal("ArgumentOutOfRangeException:contradictions", Refused(results["score-negative-contradictions"].Expected));

        // Rule: the default records 1.0.0, a host engine its own rule, and each malformed identity is refused.
        Assert.Equal("1.0.0", results["rule-no-engine"].Expected["recorded"]!.GetValue<string>());
        Assert.Equal("1.0.0", results["rule-default-engine"].Expected["recorded"]!.GetValue<string>());
        Assert.Equal("bayes/2.1", results["rule-host-engine"].Expected["recorded"]!.GetValue<string>());
        Assert.All(
            results.Where(entry => entry.Key.StartsWith("rule-rule-", StringComparison.Ordinal) || entry.Key is "rule-both-malformed" or "rule-host-engine-claims-the-default-id"),
            entry => Assert.Equal($"ArgumentException:{ConfidenceEngineContract.EngineParameter}", Refused(entry.Value.Expected)));

        // Apply: every status, both directions, both sources, every refusal.
        var applied = results.Where(entry => entry.Value.Input["operation"]!.GetValue<string>() == "apply").ToList();
        Assert.Equal(
            ["Candidate", "Contested", "Quarantined", "Reinforced", "Revoked", "Stale", "Superseded", "Validated"],
            applied.Select(entry => entry.Value.Input["record"]!["columns"]!["status"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("Contested", results["apply-contradicting-validated"].Expected["statusAfter"]!.GetValue<string>());
        Assert.Equal("Contested", results["apply-contradicting-reinforced"].Expected["statusAfter"]!.GetValue<string>());
        Assert.Equal("Reinforced", results["apply-supporting-reinforced"].Expected["statusAfter"]!.GetValue<string>());
        Assert.False(results["apply-against-candidate"].Expected["acceptsEvidence"]!.GetValue<bool>());
        Assert.Equal("ArgumentOutOfRangeException:record", Refused(results["apply-supporting-at-the-largest-count"].Expected));
        Assert.Equal("ArgumentOutOfRangeException:record.SupportingValidations", Refused(results["apply-negative-supporting-counter"].Expected));
        Assert.Equal("ArgumentOutOfRangeException:record.Contradictions", Refused(results["apply-negative-contradictions-counter"].Expected));
        Assert.Equal("ArgumentException:update", Refused(results["apply-machine-without-round"].Expected["independenceKey"]!.AsObject()));
        Assert.Equal("ArgumentException:reviewerIdentity", Refused(results["apply-human-padded-reviewer"].Expected["independenceKey"]!.AsObject()));
        Assert.Equal("HostEngineException:", Refused(results["apply-host-engine-throws"].Expected));
        Assert.Equal("ArgumentOutOfRangeException:record", Refused(results["apply-host-engine-counter-refused-first"].Expected));
        foreach (var name in new[] { "apply-host-engine-below-zero", "apply-host-engine-above-one", "apply-host-engine-nan", "apply-host-engine-infinity", "apply-host-engine-negative-infinity", "apply-host-engine-large", "apply-host-engine-just-below-zero", "apply-host-engine-just-above-one" })
        {
            Assert.Equal("InvalidOperationException:", Refused(results[name].Expected));
        }

        Assert.Equal(0d, results["apply-host-engine-zero"].Expected["update"]!["newReuseConfidence"]!.GetValue<double>());
        Assert.Equal(1d, results["apply-host-engine-one"].Expected["update"]!["newReuseConfidence"]!.GetValue<double>());

        // Options, issue and verify: every refusal the token's options, its issuer and its codec have.
        Assert.Equal(
            ["ArgumentException:options", "accepted"],
            results.Where(entry => entry.Value.Input["operation"]!.GetValue<string>() == "options")
                .Select(entry => Refused(entry.Value.Expected) ?? "accepted").Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            ["ArgumentException:experienceIds", "ArgumentException:kind", "ArgumentException:options", "ArgumentException:reviewer", "ArgumentException:runId", "ArgumentException:scope", "ArgumentOutOfRangeException:milliseconds", "issued"],
            results.Where(entry => entry.Value.Input["operation"]!.GetValue<string>() == "issue")
                .Select(entry => Refused(entry.Value.Expected) ?? "issued").Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            ["AssessmentTokenExpired", "AssessmentTokenInvalid", "AssessmentTokenMissing", "genuine"],
            results.Where(entry => entry.Value.Input["operation"]!.GetValue<string>() == "verify")
                .Select(entry => entry.Value.Expected["refusal"]?.GetValue<string>() ?? "genuine").Distinct().Order(StringComparer.Ordinal));

        // The verify cases' genuine tokens are the issue cases' tokens: what the issuer mints, the codec accepts.
        Assert.Equal(
            results["issue-supporting"].Expected["token"]!.GetValue<string>(),
            results["verify-genuine"].Input["token"]!.GetValue<string>());
    }
}
