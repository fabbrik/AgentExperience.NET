using AgentExperience.RetrievalQuality.Harness;
using AgentExperience.Storage.Postgres.Vectors;
using AgentExperience.Tests.Shared;
using Xunit.Abstractions;

namespace AgentExperience.RetrievalQuality;

/// <summary>
/// Story 21.3: the benchmark's opt-in hybrid column. It runs against a real embedding provider only when
/// <see cref="EmbeddingProviderConfiguration.ProviderVariable"/> names one, and otherwise says it was skipped; the
/// checked-in goldens are text-only and never change because of it. Offline, a deterministic test generator exercises the
/// same plumbing, and nothing it produces is rendered as a quality number.
/// </summary>
public sealed class HybridRetrievalQualityTests(ITestOutputHelper output)
{
    private const string Adapter = "in-memory candidate source";

    [Fact]
    public async Task The_hybrid_column_runs_only_against_a_configured_provider_and_otherwise_says_it_was_skipped()
    {
        var (configuration, message) = EmbeddingProviderConfiguration.Read(Environment.GetEnvironmentVariable);
        if (configuration is null)
        {
            // Unset is a skip, said out loud; a provider that was asked for but is incomplete is a failure.
            Assert.True(
                Environment.GetEnvironmentVariable(EmbeddingProviderConfiguration.ProviderVariable) is null or "",
                message);
            output.WriteLine(message);
            return;
        }

        using var generator = configuration.CreateGenerator();
        var result = await HybridRetrievalRun.RunAsync(Adapter, RetrievalCorpus.Load(), new AiExperienceEmbeddingGenerator(generator));
        var rendered = HybridComparisonReport.Render(result, configuration.Describe());
        output.WriteLine(rendered);
        if (Environment.GetEnvironmentVariable(EmbeddingProviderConfiguration.ReportPathVariable) is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, rendered);
        }

        // The text column is the golden's own measurement, whatever the provider did.
        Assert.Equal(GoldenFile.Read("GoldenInMemoryReport.txt"), RetrievalQualityReport.Render(result.Text));
        AssertNoIneligibleRecord(result);
    }

    [Fact]
    public void Without_the_provider_variable_nothing_is_configured_even_when_a_key_is_set()
    {
        var variables = new Dictionary<string, string>
        {
            [EmbeddingProviderConfiguration.OpenAiKeyVariable] = "sk-test",
            [EmbeddingProviderConfiguration.AzureKeyVariable] = "azure-test",
        };

        var (configuration, message) = EmbeddingProviderConfiguration.Read(name => variables.GetValueOrDefault(name));

        Assert.Null(configuration);
        Assert.StartsWith("Hybrid column skipped", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai", null, null, "needs OPENAI_API_KEY")]
    [InlineData("openai", "custom-embed", null, "AGENTEXPERIENCE_EMBEDDING_DIMENSIONS is required")]
    [InlineData("azure", null, null, "needs AZURE_OPENAI_ENDPOINT")]
    [InlineData("other", null, null, "must be openai or azure")]
    public void An_incomplete_provider_is_refused_with_what_is_missing(string provider, string? model, string? dimensions, string expected)
    {
        var variables = new Dictionary<string, string?>
        {
            [EmbeddingProviderConfiguration.ProviderVariable] = provider,
            [EmbeddingProviderConfiguration.OpenAiModelVariable] = model,
            [EmbeddingProviderConfiguration.DimensionsVariable] = dimensions,
        };
        if (model is not null)
        {
            variables[EmbeddingProviderConfiguration.OpenAiKeyVariable] = "sk-test";
        }

        var (configuration, message) = EmbeddingProviderConfiguration.Read(name => variables.GetValueOrDefault(name));

        Assert.Null(configuration);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_provider_never_describes_its_key_or_an_Azure_resource_name()
    {
        var variables = new Dictionary<string, string>
        {
            [EmbeddingProviderConfiguration.ProviderVariable] = "azure",
            [EmbeddingProviderConfiguration.AzureEndpointVariable] = "https://my-secret-resource.openai.azure.com/",
            [EmbeddingProviderConfiguration.AzureKeyVariable] = "azure-key-123",
            [EmbeddingProviderConfiguration.AzureDeploymentVariable] = "text-embedding-3-small",
        };

        var (configuration, _) = EmbeddingProviderConfiguration.Read(name => variables.GetValueOrDefault(name));

        Assert.NotNull(configuration);
        Assert.Equal(1536, configuration.Dimensions);
        Assert.Equal(new Uri("https://my-secret-resource.openai.azure.com/openai/v1/"), configuration.Endpoint);
        Assert.DoesNotContain("azure-key-123", configuration.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("my-secret-resource", configuration.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The hybrid plumbing, offline: every eligible record is embedded, the text column is exactly the golden's, the
    /// merged column keeps every text candidate, and no ineligible record comes back. The deterministic generator's
    /// numbers are deliberately not rendered or compared: they would measure its synonym table, not semantic retrieval.
    /// </summary>
    [Fact]
    public async Task The_hybrid_plumbing_runs_offline_with_a_deterministic_generator()
    {
        var corpus = RetrievalCorpus.Load();
        using var concepts = new ConceptEmbeddingGenerator();

        var result = await HybridRetrievalRun.RunAsync(Adapter, corpus, new AiExperienceEmbeddingGenerator(concepts));

        Assert.Equal(corpus.Records.Count(r => r.IsEligible), result.Embedded);
        Assert.Equal(GoldenFile.Read("GoldenInMemoryReport.txt"), RetrievalQualityReport.Render(result.Text));
        for (var i = 0; i < corpus.Queries.Count; i++)
        {
            var text = result.Text.Outcomes[i].Returned;
            var hybrid = result.Hybrid.Outcomes[i].Returned;
            Assert.All(text, id => Assert.Contains(id, hybrid));
            Assert.True(hybrid.Count <= RetrievalQualityRun.Limit);
            Assert.Equal(hybrid.Count, hybrid.Distinct().Count());
        }

        AssertNoIneligibleRecord(result);
    }

    [Fact]
    public void The_merge_keeps_one_entry_per_record_with_the_higher_relevance()
    {
        var scope = Storage.Conformance.ConformanceData.Scope("merge");
        var a = Storage.Conformance.ConformanceData.Record(scope);
        var b = Storage.Conformance.ConformanceData.Record(scope);

        var merged = HybridRetrievalRun.Merge(
            [new ExperienceCandidate(a, 0.4), new ExperienceCandidate(b, 0.3)],
            [new ExperienceCandidate(b, 0.9), new ExperienceCandidate(a, 0.1)]);

        Assert.Equal([b.ExperienceId, a.ExperienceId], merged.Select(c => c.Record.ExperienceId));
        Assert.Equal([0.9, 0.4], merged.Select(c => c.Relevance));
    }

    private static void AssertNoIneligibleRecord(HybridRetrievalResult result)
    {
        var corpus = result.Hybrid.Corpus;
        var hits = result.Hybrid.Outcomes
            .SelectMany(o => o.Returned.Where(id => !corpus.Record(id).IsEligible).Select(id => $"{o.Query.Id} returned {id}"))
            .ToList();
        Assert.True(hits.Count == 0, "Ineligible records were returned:\n" + string.Join("\n", hits));
    }
}
