using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 27.4's cross-language proof, the .NET half: the same seeded records and the same non-default policy (an expiry
/// and per-domain confidence decay) rank the same in every language. The records are inserted as a search seed is (the
/// eighteen record columns, as the application role), retrieved through the .NET PostgreSQL candidate source and
/// ranked by <see cref="ExperienceRetrievalService"/> under <see cref="RetrievalPolicyContract.CrossLanguagePolicy"/> and
/// <see cref="RetrievalPolicyContract.CrossLanguageDecay"/>. The seed, the request, the policy and what .NET answered
/// must be exactly the checked-in <see cref="File"/> (rewritten with the update switch). The Python and Go ports'
/// PostgreSQL tests insert the same seed into their own database, search it through their own store and rank it with
/// their own policy: each must answer the same ordered IDs, exclusions and scores, within 1e-12.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageRetrievalPolicyPostgresTests(ContractPostgresDatabase database)
{
    /// <summary>The seed's scope.</summary>
    internal static readonly Scope SeedScope = new("cross-language-retrieval", "retrieval-app", "retrieval-project", AgentId: "retrieval-agent");

    private const int F = 0xc1;

    private const string TaskText = "Rotate the signing keys before the release";

    private static readonly DateTimeOffset Now = Fixed.T0;

    private static readonly string File = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "tests", "AgentExperience.Contract.Tests", "cross-language", "dotnet-retrieval-policy.json");

    [Fact]
    public async Task The_same_policy_ranks_the_same_seed_the_same_in_every_language()
    {
        ExperienceRecord Lesson(int n, int createdDaysAgo, int updatedDaysAgo, string? domain, double confidence, ExperienceStatus status = ExperienceStatus.Validated, string region = "eu-west") =>
            RetrievalPolicyContract.Lesson(n, Now.AddDays(-createdDaysAgo), Now.AddDays(-updatedDaysAgo), status, confidence, domain, region: region, family: F, scope: SeedScope);

        ExperienceRecord[] seed =
        [
            Lesson(1, 60, 2, "framework-api", 0.95),
            Lesson(2, 60, 20, "security", 0.8, ExperienceStatus.Reinforced),
            Lesson(3, 500, 179, "math", 0.7),
            Lesson(4, 200, 181, "math", 0.99),
            Lesson(5, 30, 30, null, 0.6, region: "us-east"),
            Lesson(6, 400, 3, "business-rule", 0.75),
            Lesson(7, 10, 1, "framework-api", 0.4),
            Lesson(8, 10, 1, null, 0.9, ExperienceStatus.Quarantined),
            Lesson(9, 1, 1, "ops", 0.55, ExperienceStatus.Reinforced),
        ];

        var policyJson = RetrievalPolicyContract.CrossLanguagePolicy();
        var decayJson = RetrievalPolicyContract.CrossLanguageDecay();
        var preferred = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "us-east" };

        var application = await database.CreateAsync("xl_retrieval_policy");
        foreach (var record in seed)
        {
            await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(record));
        }

        var service = new ExperienceRetrievalService(
            new PostgresExperienceCandidateSource(application),
            RetrievalPolicyContract.ReadPolicy(policyJson),
            RankingWeights.Default,
            new FixedTimeProvider(Now),
            embeddingIndex: null,
            embeddingGenerator: null,
            environmentScorer: null,
            confidenceDecay: RetrievalPolicyContract.ReadDecay(decayJson));
        var result = await service.RetrieveAsync(
            new RetrieveExperienceRequest(SearchContractPostgresTests.Authorization(SeedScope), SeedScope, TaskText) { PreferredEnvironmentAttributes = preferred },
            CancellationToken.None);
        await application.DisposeAsync();

        // The policy did something: an expiry excluded a record, decay changed a confidence, and the store's own floor and
        // status predicates left the rest out before ranking, so they are not listed.
        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Contains(result.Excluded, entry => entry.Reason == RetrievalExclusionReason.Expired);
        Assert.Contains(result.Records, ranked => ranked.Components.Any(component => component.UndecayedValue is not null));
        Assert.DoesNotContain(result.Records, ranked => ranked.Record.ExperienceId == seed[6].ExperienceId || ranked.Record.ExperienceId == seed[7].ExperienceId);
        Assert.DoesNotContain(result.Excluded, entry => entry.ExperienceId == seed[6].ExperienceId || entry.ExperienceId == seed[7].ExperienceId);

        var expected = RetrievalPolicyContract.ExpectedJson(new RetrievalPolicyRun(Query: null, result));
        expected.Remove("query");
        var file = new JsonObject
        {
            ["description"] =
                "Story 27.4: insert seed as a search seed is inserted, search it for taskText in scope through the port's own " +
                "store, and retrieve at now with policy, confidenceDecay and preferredEnvironment: the outcome, truncated, the " +
                "ranked IDs in order with every score and component (within 1e-12) and the exclusions must be expected's.",
            ["scope"] = JsonNode.Parse(ContractJson.Write(SeedScope)),
            ["taskText"] = TaskText,
            ["now"] = ContractJson.Timestamp(Now),
            ["preferredEnvironment"] = new JsonObject { ["region"] = "us-east" },
            ["policy"] = policyJson,
            ["confidenceDecay"] = decayJson,
            ["seed"] = SearchContract.ToSeedJson(seed),
            ["expected"] = expected,
        };

        var text = ContractJson.Write(file);
        if (ContractFiles.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            await System.IO.File.WriteAllTextAsync(File, text);
        }

        Assert.True(System.IO.File.Exists(File), $"{File} is missing; run with {ContractFiles.UpdateVariable}=1.");
        Assert.True(
            text == await System.IO.File.ReadAllTextAsync(File),
            $"{File} is not what .NET retrieves now; run with {ContractFiles.UpdateVariable}=1 and read the diff.");
    }
}
