using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.12's checks, which need no database: every <c>retrieval-policy</c> fixture is what
/// <see cref="ExperienceRetrievalService"/> gives under the case's policy and decay, and <c>policy-validation.json</c> is
/// which policies .NET accepts and how it refuses the rest.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_retrieval_policy_fixture_is_what_the_retrieval_service_gives()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in RetrievalPolicyContract.Cases)
        {
            var directory = $"fixtures/{RetrievalPolicyContract.Family}/{name}";
            var candidatesJson = ContractJson.Write(RetrievalPolicyContract.CandidatesJson(definition.Candidates));
            var vectorJson = definition.VectorCandidates is { } vector ? ContractJson.Write(RetrievalPolicyContract.CandidatesJson(vector)) : null;
            if (vectorJson is not null)
            {
                ContractFiles.Verify($"{directory}/vector-candidates.json", vectorJson, drift);
            }

            var policyJson = ContractJson.Write(RetrievalPolicyContract.PolicyJson(definition));
            ContractFiles.Verify($"{directory}/candidates.json", candidatesJson, drift);
            ContractFiles.Verify($"{directory}/policy.json", policyJson, drift);

            // The inputs as checked in, read as a port reads them.
            var candidates = RetrievalPolicyContract.FromCandidatesJson(ContractFiles.Read($"{directory}/candidates.json") ?? candidatesJson);
            var input = JsonNode.Parse(ContractFiles.Read($"{directory}/policy.json") ?? policyJson)!.AsObject();
            var preferred = input["preferredEnvironment"]!.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>(), StringComparer.Ordinal);
            var run = RetrievalPolicyContract.Run(
                candidates,
                DateTimeOffset.Parse(input["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                RetrievalPolicyContract.ReadPolicy(input["policy"]!.AsObject()),
                RetrievalPolicyContract.ReadDecay(input["confidenceDecay"]),
                preferred,
                input["excludeModelAuthored"]!.GetValue<bool>(),
                vectorJson is null ? null : RetrievalPolicyContract.FromCandidatesJson(ContractFiles.Read($"{directory}/vector-candidates.json") ?? vectorJson));
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(RetrievalPolicyContract.ExpectedJson(run)), drift);
        }

        ContractFiles.Verify(
            $"fixtures/{RetrievalPolicyContract.Family}/{RetrievalPolicyContract.ValidationFile}",
            ContractJson.Write(RetrievalPolicyContract.ValidationJson()),
            drift);

        ContractFiles.VerifyNoStray(
            RetrievalPolicyContract.Family,
            RetrievalPolicyContract.Cases.Select(entry => entry.Name),
            ["candidates.json", "vector-candidates.json", "policy.json", "expected.json"],
            drift,
            RetrievalPolicyContract.ValidationFile);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_retrieval_policy_cases_cover_every_rule()
    {
        var runs = RetrievalPolicyContract.Cases.ToDictionary(
            entry => entry.Name,
            entry => RetrievalPolicyContract.Run(
                entry.Case.Candidates,
                entry.Case.Now,
                RetrievalPolicyContract.ReadPolicy(entry.Case.Policy),
                RetrievalPolicyContract.ReadDecay(entry.Case.ConfidenceDecay),
                entry.Case.PreferredEnvironment,
                entry.Case.ExcludeModelAuthored,
                entry.Case.VectorCandidates));

        Assert.All(runs.Values, run => Assert.Equal(RetrievalOutcome.Completed, run.Result.Outcome));

        // The default policy is RetrievalPolicy.Default, and ranks exactly as with no policy given at all.
        Assert.Equal(RetrievalPolicy.Default, RetrievalPolicyContract.ReadPolicy(RetrievalPolicyContract.Default()));
        var defaults = RetrievalPolicyContract.Cases.Single(entry => entry.Name == "default-policy").Case;
        Assert.Null(defaults.ConfidenceDecay);
        Assert.All(runs["default-policy"].Result.Records, ranked => Assert.All(ranked.Components, component => Assert.Null(component.UndecayedValue)));
        Assert.Equal([RetrievalExclusionReason.IneligibleStatus], runs["default-policy"].Result.Excluded.Select(entry => entry.Reason));

        // MaxAge: under and at are kept, over is Expired; status is checked first.
        var maxAge = runs["max-age"].Result;
        Assert.Equal(
            [RetrievalExclusionReason.Expired, RetrievalExclusionReason.Expired, RetrievalExclusionReason.IneligibleStatus],
            maxAge.Excluded.Select(entry => entry.Reason));
        Assert.Equal(3, maxAge.Records.Count);

        // Decay never excludes, and reports the stored value when a half-life applied.
        foreach (var name in new[] { "decay-and-minimum-confidence", "minimum-confidence-after-decay", "decay-one-domain", "decay-several-domains" })
        {
            Assert.Empty(runs[name].Result.Excluded);
            Assert.Equal(RetrievalPolicyContract.Cases.Single(entry => entry.Name == name).Case.Candidates.Count, runs[name].Result.Records.Count);
        }

        var belowFloor = runs["decay-and-minimum-confidence"].Result.Records;
        Assert.True(Confidence(belowFloor[^1]).Value < RetrievalPolicy.DefaultMinimumConfidence);
        Assert.Equal(0.75, runs["minimum-confidence-after-decay"].Query!.MinimumConfidence);
        Assert.Contains(runs["decay-one-domain"].Result.Records, ranked => Confidence(ranked).UndecayedValue is not null && Confidence(ranked).Value == Confidence(ranked).UndecayedValue);
        Assert.Equal(5, runs["decay-several-domains"].Result.Records.Count(ranked => Confidence(ranked).UndecayedValue is not null));

        // The candidate limit: asked for one more, truncated only past it.
        Assert.Equal(4, runs["candidate-limit-truncates"].Query!.Limit);
        Assert.True(runs["candidate-limit-truncates"].Result.Truncated);
        Assert.Equal(3, runs["candidate-limit-truncates"].Result.Records.Count);
        Assert.False(runs["candidate-limit-exactly-reached"].Result.Truncated);
        Assert.Equal(3, runs["candidate-limit-exactly-reached"].Result.Records.Count);

        // A custom recency half-life.
        Assert.Contains(runs["recency-half-life"].Result.Records, ranked => ranked.Components.Single(c => c.Kind == RankingComponentKind.Recency).Value == 0.25);

        // Model-authored: each exclusion by the first rule, in .NET's order.
        Assert.Equal(
            [RetrievalExclusionReason.ModelAuthored, RetrievalExclusionReason.IneligibleStatus, RetrievalExclusionReason.ModelAuthored, RetrievalExclusionReason.Expired, RetrievalExclusionReason.ModelAuthored],
            runs["model-authored-excluded"].Result.Excluded.Select(entry => entry.Reason));

        // Hybrid: the policy's floor, per-channel limits, the floor before the count.
        Assert.All(runs.Where(entry => entry.Key.StartsWith("hybrid-", StringComparison.Ordinal)), entry =>
        {
            Assert.False(entry.Value.Result.TextOnly);
            Assert.NotNull(entry.Value.VectorQuery);
        });
        Assert.Equal(3, runs["hybrid-vector-floor-from-the-policy"].Result.Records.Count);
        Assert.True(runs["hybrid-per-channel-candidate-limit"].Result.Truncated);
        Assert.Equal(3, runs["hybrid-per-channel-candidate-limit"].VectorQuery!.Limit);
        Assert.False(runs["hybrid-floor-before-truncation"].Result.Truncated);
        Assert.Equal(2, runs["hybrid-floor-before-truncation"].Result.Records.Count);

        // The near tie (contract 1.18): each pair one ulp apart, the higher score on the larger experience ID.
        var nearTie = runs["ranking-near-tie"].Result.Records;
        Assert.Equal([2, 1, 4, 3], nearTie.Select(ranked => (int)(ranked.Record.ExperienceId.ToByteArray(bigEndian: true)[15])));
        Assert.Equal(1, BitConverter.DoubleToInt64Bits(nearTie[0].Score) - BitConverter.DoubleToInt64Bits(nearTie[1].Score));
        Assert.Equal(1, BitConverter.DoubleToInt64Bits(nearTie[2].Score) - BitConverter.DoubleToInt64Bits(nearTie[3].Score));

        // The cross-language policy: an expiry and decay together.
        Assert.Contains(runs["max-age-and-decay"].Result.Excluded, entry => entry.Reason == RetrievalExclusionReason.Expired);
        Assert.Contains(runs["max-age-and-decay"].Result.Records, ranked => Confidence(ranked).UndecayedValue is not null);
    }

    [Fact]
    public void The_policy_validation_fixture_names_every_rule_dotnet_refuses()
    {
        var cases = RetrievalPolicyContract.ValidationJson();
        var refused = cases
            .Select(entry => entry!["refusal"])
            .OfType<JsonObject>()
            .Select(refusal => refusal["parameter"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "CandidateLimit", "DefaultHalfLife", "DomainKey", "HalfLives", "MaxAbandonedSearches", "MaxAge",
                "MinimumConfidence", "MinimumMatchedTerms", "MinimumVectorRelevance", "RecencyHalfLife", "Timeout",
            },
            refused.Order(StringComparer.Ordinal));
        Assert.Contains(cases, entry => entry!["refusal"] is null);
    }

    private static RankingComponent Confidence(RankedExperience ranked) => ranked.Components.Single(component => component.Kind == RankingComponentKind.Confidence);
}
